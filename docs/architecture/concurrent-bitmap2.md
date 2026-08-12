# ConcurrentBitmap2 architecture

This document describes the architecture of `ConcurrentBitmap2`, the design pressures that shaped it, and an informal correctness argument for the emptiness and point-update contracts. It is aimed at maintainers who need to change the type without re-deriving those constraints from the source.

The hierarchical `ConcurrentBitmap` remains in the tree for comparison and benchmarks. `ConcurrentBitmap2` is a separate design, not an incremental refactor of the tree.

## Problem framing

`ConcurrentBitmap2` is intended for producer/consumer work-tracking:

- producers mark a bucket as non-empty by setting a bit, then wake consumers;
- consumers scan set bits, drain the corresponding buckets, and clear bits when a bucket appears empty;
- consumers may terminate early when the bitmap reports global emptiness.

That workload imposes an asymmetric correctness requirement on emptiness:

| Result | Required strength | Failure mode if wrong |
|---|---|---|
| `IsEmpty == true` | **Authoritative** | Consumer exits while work remains → silent lost work |
| `IsEmpty == false` | Best-effort | Extra scan / invalidation work only |

Point updates must also support optimistic concurrency tokens so a consumer can clear a bit only if the segment has not changed since it observed the bit state (ABA-safe conditional write).

Structural changes (`Grow`, `RemoveBitAt`) are allowed to be rare and expensive. They must remain correct, but they are not on the latency-critical path.

## Why the hierarchical design was rejected

`ConcurrentBitmap` layers:

1. CAS-updated 56-bit guarded words at the leaves;
2. per-segment / per-child summary locks for empty/full propagation;
3. a root `ReaderWriterLockSlim` taken in read mode on every checked point operation to stabilize topology.

The leaf CAS is lock-free, but the surrounding summary and topology machinery is pessimistic. Under the work-tracking pattern the common operations are point set/clear and a global emptiness probe. Paying a shared RWLS (and summary lock traffic) on that path collapses the design toward “global lock + bit array” in practice, while keeping more moving parts.

`ConcurrentBitmap2` therefore optimizes for:

- lock-free concurrent point operations with respect to each other;
- a constant-time emptiness probe with a one-sided error bound;
- isolation of topology mutation onto an explicit cold path.

## Storage model

### Flat guarded segments

User bits are stored in a contiguous array of `ConcurrentBitmap56State` words. Each word holds:

```text
63                    56 55                         0
+-----------------------+---------------------------+
|  8-bit guard token    |  up to 56 data bits       |
+-----------------------+---------------------------+
```

Indexing is positional:

- segment = `index / 56`
- bit in segment = `index % 56`

There is no empty/full summary tree. Global emptiness is not derived from hierarchical metadata; it is derived from a separate atomic counter (below).

### Topology descriptor

`Bitmap2Storage` is an immutable pair `(Segments, Size)`:

- `Segments` — the array of atomic segment words;
- `Size` — the number of usable bits (may be smaller than `Segments.Length * 56`).

The bitmap holds a single `volatile` reference to the current descriptor. Point operations capture that reference after entering the hot-path gate. Structural operations install a replacement descriptor under exclusive ownership.

Guard tokens live inside segment words. They are not globally unique versions; they are per-segment change detectors with 8-bit wraparound, identical in spirit to `ConcurrentBitmap56`. Clients that need stronger identity must not treat a token as a permanent epoch.

## Emptiness: atomic set-bit count

### Representation

`ConcurrentBitmap2` maintains `_setBitCount`, an `int` updated with `Interlocked` operations. It counts how many user bits are set across the published storage.

```text
IsEmpty  ≜  Volatile.Read(ref _setBitCount) == 0
```

### Publication order (the core invariant)

To keep `IsEmpty == true` free of false positives, counter updates are ordered relative to the bit CAS:

| Transition | Order | Temporary anomaly |
|---|---|---|
| clear → set (0→1) | **increment, then CAS** | count may be high while the bit is still clear (false non-empty) |
| set → clear (1→0) | **CAS, then decrement** | count may be high while the bit is already clear (false non-empty) |
| no value change | CAS token bump only | count unchanged |

If the 0→1 CAS loses, the optimistic increment is undone. If the 1→0 CAS loses, no decrement occurs.

### Why this forbids false empty

Suppose a set bit is observable in some segment word. That bit became observable only after a successful 0→1 CAS, which was preceded by an increment. A matching decrement happens only after a successful 1→0 CAS (or after an exclusive structural removal of a set bit). Therefore, while the set bit remains published, the number of unmatched increments is at least one, so `_setBitCount != 0`, so `IsEmpty` is false.

The converse is intentionally false: `_setBitCount != 0` does not imply a set bit is currently visible. That is exactly the allowed false-negative region for emptiness.

### What the counter is not

- It is not a linearizable population count at every instant (it may over-count briefly).
- It is not protected by the structural mutex on the hot path; its correctness comes from the transition protocol and from structural ops running only when hot-path activity is quiescent.

## Hot path vs structural path

### Hot-path gate

Point APIs that touch segment storage (`GetBitInfo`, `IsBitSet`, `UpdateBit`, `TryUpdateBit`, `TryFindNextSetBit`) enter a lightweight participation protocol:

1. if a structural op is marked active, spin until it finishes;
2. increment `_hotPathParticipants`;
3. re-check the structural flag; if set, decrement and retry;
4. run the operation;
5. decrement `_hotPathParticipants` in a `finally`.

There is **no** `ReaderWriterLockSlim` and **no** per-segment lock on this path. Concurrent point operations on distinct segments proceed with independent CASes. Concurrent point operations on the same segment serialize only through the 64-bit word CAS.

`IsEmpty` and `Size` do not enter the gate:

- `IsEmpty` only reads `_setBitCount`;
- `Size` only reads the current descriptor’s immutable `Size` field.

### Structural exclusion

`Grow` and `RemoveBitAt` take `_structuralMutex` (serializing structural ops with each other), then:

1. set `_structuralBlocked = 1`;
2. spin until `_hotPathParticipants == 0`;
3. mutate or replace storage with exclusive ownership;
4. clear `_structuralBlocked`.

During the exclusive window, no hot-path CAS is in flight against the live segment array. That makes copy/shift rewrites safe without COW races against concurrent bit updates.

### Cost model

| Operation class | Synchronization | Expected frequency |
|---|---|---|
| `IsEmpty` | single volatile/atomic read | very high |
| point read / CAS write | participation counter + segment CAS | very high |
| `Grow` / `RemoveBitAt` | mutex + quiescence drain + rewrite | rare |

The participation counter is pure atomics and does not block when no structural op is running. Structural ops may stall the hot path briefly; that is accepted cold-path behavior.

## Point-update protocol

### Unconditional write (`UpdateBit`)

Loop:

1. observe the segment word;
2. compute the replacement word (data bit + bumped guard token);
3. if the data bit value changes, apply the emptiness-safe counter order around a single `CompareExchange`;
4. if the data bit value is unchanged, still CAS a token bump so stale readers are invalidated;
5. retry on CAS failure.

### Conditional write (`TryUpdateBit`)

Same as above, but the observation must still carry the caller-supplied token. A mismatched token fails immediately without retry. This is the client-facing CAS primitive for “clear only if nothing changed since `GetBitInfo`”.

### Versioned read (`GetBitInfo` / `GetToken`)

A single volatile read of the segment word yields `(isSet, token)` for the target bit. The returned index is the **global** bit index, not the in-segment offset. The token is the segment guard, so any concurrent write to the same segment (including other bits in that word) invalidates the token. That is coarser than per-bit versioning and matches the packing of `ConcurrentBitmap56`.

## Structural operations

### `Grow(newSize)`

No-op when `newSize <= Size`. Otherwise, under exclusive ownership:

- if the existing segment array already has capacity, only the usable `Size` is raised (new indices were unused and read as clear);
- otherwise a larger array is allocated, existing words are copied, and the descriptor is replaced.

New bits are clear; `_setBitCount` is unchanged.

### `RemoveBitAt(index)`

Under exclusive ownership, a new layout is built by copying all bits except `index`, shifting higher indices down by one. The descriptor is replaced with the smaller usable size. If the removed bit was set, `_setBitCount` is decremented **after** the new descriptor is published (symmetric to the 1→0 hot-path order: remove first, then drop the count).

Tokens in the rebuilt storage restart at zero. That is acceptable because any client still holding an old token also holds a stale index map once removal has shifted indices; structural changes are outside the optimistic token contract.

## Optional scan API

`TryFindNextSetBit` walks segment words with trailing-zero scans under the hot-path gate. It is weakly consistent: it may miss a bit that is set only briefly, or observe a bit that is cleared immediately afterward. It never returns an index outside the usable size captured for that scan. It exists for consumer bucket walks and is not required for the emptiness proof.

## Concurrency review and informal proof

### Claim A — `IsEmpty == true` implies no set bit is published

**Proof sketch.** All publications of a set bit occur at a successful segment CAS that installs a 1 in some data position. Every such CAS on the 0→1 path is preceded by `Interlocked.Increment(ref _setBitCount)`. Every retirement of a set bit occurs either at a successful 1→0 CAS followed by a decrement, or in `RemoveBitAt` under quiescence followed by a decrement when the removed bit was set. While a particular set bit remains in published storage, its matching decrement has not yet occurred, so the counter is strictly positive. Therefore a zero counter cannot coexist with a published set bit.

Memory ordering: `Interlocked` operations and `CompareExchange` provide full fences. A reader that observes `_setBitCount == 0` via `Volatile.Read` cannot observe a later 0→1 increment-as-ordering without also being able to observe the corresponding protocol state; more directly, any 0→1 that has completed its increment is already non-zero before the bit CAS, and any still-visible set bit has a completed increment without a completed decrement.

### Claim B — `IsEmpty == false` may be wrong (allowed)

**Counterexamples (benign).**

1. Increment has run; bit CAS has not yet succeeded → count high, storage still empty.
2. Bit CAS cleared the last set bit; decrement has not yet run → count high, storage empty.

Both produce false non-empty. Neither produces false empty.

### Claim C — hot-path point ops are lock-free w.r.t. each other

Progress of a point op depends only on:

- a finite spin while a structural op is active (cold path; not part of the mutual hot-path claim);
- successful CAS on a single word, or retry after a failed CAS caused by another hot-path writer.

There is no lock acquire among hot-path operations. Under contention on one word, at least the CAS winner progresses. Multi-word system-wide lock-freedom follows the usual CAS-loop argument.

### Claim D — structural ops do not race with segment CASes

Structural ops set `_structuralBlocked` before waiting for `_hotPathParticipants == 0`. Hot-path entry re-checks the flag after incrementing the participant count and backs out if a structural op started. Therefore, when the structural op mutates or replaces storage, no hot-path critical section is active. Descriptor publication is a single volatile write of a reference to an immutable topology object.

### Claim E — no deadlock between gate and mutex

- Hot path never acquires `_structuralMutex`.
- Structural ops acquire the mutex, then wait only on `_hotPathParticipants`.
- Hot-path participants always decrement in `finally`.
- `_structuralBlocked` is always cleared in structural `finally`.

There is no wait cycle: hot path waits only for “not blocked”; structural waits only for “no participants”; participants never wait for the mutex.

### Claim F — token-guarded clears cannot clobber a concurrent set on the same segment

`TryUpdateBit` requires the observed token. Any intervening write to the segment bumps the token, so the clear CAS fails. The producer/consumer pattern “observe set + empty bucket; try clear; on failure or later non-empty, set again” is therefore safe against lost “has work” marks caused by stale clears on that segment.

Note the granularity limit: a write to **another** bit in the same 56-bit segment also invalidates the token. That can cause extra clear retries; it does not create false empty.

### Claim G — `RemoveBitAt` / `Grow` preserve bit content for surviving indices

Under exclusive ownership, `Grow` only extends usable size or copies whole segment words. `RemoveBitAt` rebuilds bit-by-bit from a quiescent snapshot. No concurrent mutator changes the source mid-copy. The set-count adjustment for a removed set bit maintains Claim A.

### Residual risks (accepted)

1. **8-bit token wrap.** After 256 writes to a segment, tokens alias. A client that stalls that long can theoretically pass `TryUpdateBit` spuriously. This matches `ConcurrentBitmap56` and is accepted for the intended short CAS loops.
2. **Structural pause.** A long `RemoveBitAt` on a huge bitmap stalls hot-path threads. Cold-path cost by design.
3. **Weak scans.** `TryFindNextSetBit` is not a snapshot enumerator.
4. **Counter overflow.** `_setBitCount` is an `int`; usable size is also bounded by `int` indexing, so the number of set bits cannot exceed `int.MaxValue` under a correct protocol. Temporary over-count is bounded by in-flight 0→1 operations.

## Comparison with alternatives considered

| Approach | Why rejected or limited |
|---|---|
| Keep hierarchical summaries, drop only the RWLS | Summary updates still need cross-location coordination; emptiness would still be a multi-word invariant |
| Pure `BitArray` + global RWLS | Simple, but serializes the hot path; fails the lock-free goal |
| COW storage without quiescence | Concurrent CAS into an abandoned array loses updates or corrupts counts unless every writer retries with complex compensation |
| Count non-empty segments only | Sufficient for emptiness, but a full set-bit count also supports `VolatileSetCount` and is equally cheap per transition |

The chosen design keeps one atomic word per 56 bits for data+token, one global counter for emptiness, and a quiescence gate only for topology.

## Extension guidance

When modifying `ConcurrentBitmap2`, preserve these rules:

1. Never publish a 0→1 bit transition without a prior unmatched increment of `_setBitCount`.
2. Never decrement `_setBitCount` for a bit that is still published.
3. Do not add hot-path locks or hierarchical summary updates without revisiting the emptiness proof.
4. Any new structural mutation must run under the same exclusive quiescence window (or an equivalent stronger fence) before it reindexes or replaces storage.
5. Do not treat guard tokens as global epochs; they are per-segment and wrap.

If a future requirement needs linearizable popcount or strict snapshot enumeration, that is a new contract: the current counter and scan APIs are intentionally weaker than that.

## Test map

- Unit tests in `Fho.Core.Threading.Tests/Collections/ConcurrentBitmap2Tests.cs` cover API contracts, growth, removal shifts, token CAS, and scan ordering.
- Stress tests in `ConcurrentBitmap2StressTests.cs` exercise concurrent set/clear, producer/consumer work-tracking, mixed grow/remove, and high-contention single-bit updates as smoke/regression oracles. They do not constitute a proof.

## Relationship to the rest of the library

`ConcurrentBitmap2` reuses `ConcurrentBitmap56` / `ConcurrentBitmap56State` as the packed atomic word format and reuses `GuardedBitInfo` as the versioned read DTO. It does not use `ReaderWriterLockSlim` extensions, bitmap tree nodes, or summary extension helpers. Architecturally it sits beside `ConcurrentHashSet<T>` as another concurrent collection with its own state model: flat CAS words + global counting + rare exclusive topology rewrite, rather than striped locks + table descriptor swaps.
