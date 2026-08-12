# Concurrent collections

The collection subsystem contains a general concurrent set and a specialized bitmap. They share the goal of avoiding global serialization on ordinary point operations, but they use different state models and should be understood independently.

## `ConcurrentHashSet<T>`

`ConcurrentHashSet<T>` is a thread-safe hash-based unique collection derived from the striped-lock architecture used by `ConcurrentDictionary`.

### Table ownership

The set publishes a single volatile `Tables` descriptor containing:

- the bucket array;
- the stripe-lock array;
- one item count per stripe.

A resize creates new buckets and count arrays, rehashes the existing nodes into them, and then replaces the descriptor as one published reference. Operations that captured an older descriptor detect the replacement where necessary and retry against the current table.

Node item and hash-code fields are immutable after publication. The linked-list `Next` field is volatile because removal may splice an existing bucket chain.

### Reads and point mutations

Lookup does not acquire a stripe lock. It captures the current table, selects a bucket, reads the bucket head with `Volatile.Read`, and traverses the published node chain.

Add and remove operations compute both the bucket and the stripe that owns it, then lock only that stripe. After entering the stripe, they verify that the captured table is still current; if a concurrent resize replaced it, they retry with the new topology.

This separation allows independent buckets owned by different stripes to be mutated concurrently while preserving a lock-free lookup path.

### Resizing

Each stripe has a budget derived from the bucket-to-lock ratio. An insertion that pushes a stripe beyond its budget requests a resize after releasing the stripe lock.

Resize coordination follows a fixed lock order:

1. acquire stripe 0 and verify that the requested table is still current;
2. decide whether a resize is actually useful or whether increasing the per-stripe budget is sufficient;
3. acquire the remaining stripes in ascending order;
4. rebuild the bucket topology and per-stripe counts;
5. publish the replacement `Tables` descriptor;
6. release the acquired stripes.

The default-concurrency constructors permit the lock array to grow up to an implementation limit as the table grows. Constructors that accept an explicit concurrency level keep that lock count fixed.

### Snapshot and enumeration semantics

Operations that require a coherent total, such as `Count` and collection copying, acquire all stripes. Their result reflects the set while those locks are held.

Enumeration deliberately does not acquire all stripes. The enumerator captures a bucket array and traverses it safely while concurrent updates continue. It is therefore weakly consistent rather than a moment-in-time snapshot: it may observe modifications that race with enumeration.

`IsEmpty` uses a cheap unlocked count-per-stripe scan first and only takes all stripes when it needs to confirm the empty result.

## Guarded 56-bit bitmap storage

`ConcurrentBitmap56State` and `ConcurrentBitmap56` form the atomic storage primitive used by the larger bitmap.

A state occupies one 64-bit word:

```text
63                         56 55                         0
+----------------------------+---------------------------+
|       8-bit guard token    |       56 data bits        |
+----------------------------+---------------------------+
```

`ConcurrentBitmap56State` is the stable storage location. `ConcurrentBitmap56` is a `ref struct` snapshot/view used to inspect a captured word or calculate a replacement.

### CAS updates and guard tokens

Normal writes read the complete 64-bit word, update the data region, increment the guard token, and compare-exchange the complete word back into storage. A concurrent writer that changed either data or token causes the CAS to fail.

The `Try*` APIs add optimistic validation. The caller first captures a token, then supplies it with a later attempted write. The write is committed only if the stored token still matches and the final CAS succeeds.

The token is an 8-bit counter, so it is a bounded change detector rather than a globally unique version. It can wrap after repeated writes. Code that requires an unbounded generation identity needs a different versioning mechanism.

Insert/remove operations on a 56-bit state shift bits within the data region and also advance the token. The token itself is never shifted as part of the bitmap payload.

## Hierarchical `ConcurrentBitmap`

`ConcurrentBitmap` scales the 56-bit primitive by organizing it into a tree with cached empty/full state.

### Leaf clusters

A leaf cluster contains up to 28 data segments. Each segment stores up to 56 user-visible bits, so one full cluster represents up to 1,568 bits.

The cluster has another 56-bit state word used as metadata rather than user data:

- bits 0 through 27 mark child segments that are empty;
- bits 28 through 55 mark child segments that are full.

A child cannot be both empty and full in this summary. If it is partially populated, neither summary bit is set.

### Internal nodes

Internal nodes use the same two-bits-per-child summary encoding for up to 28 child nodes. A child may itself be an internal node or a leaf cluster depending on depth.

This creates a recursive summary tree: user data lives only in leaf segments, while each higher level records whether its child regions are entirely empty, entirely full, or partial. `IsEmpty` and `IsFull` can therefore be answered from the root summary instead of scanning every user bit.

The last region at each level may be only partially populated. Length and child capacity are carried separately so unused bits in the final storage word do not participate in full/empty decisions.

### Point-operation flow

The checked point APIs (`IsBitSet`, `GetBitInfo`, `GetToken`, `UpdateBit`, and `TryUpdateBit`) acquire the root `ReaderWriterLockSlim` in read mode. This does not serialize point operations with each other; it stabilizes the tree topology while they navigate to the target leaf.

A point update then proceeds in two layers:

1. update the target 56-bit data state with CAS;
2. if that update changed whether the segment is empty or full, update the parent summary and propagate the state transition upward as needed.

Summary transitions use short per-segment or per-child locks around the relationship between a child's state and its parent metadata. The data-word mutation remains a CAS operation, but the hierarchical collection as a whole is not a single lock-free state machine.

`VolatilePopCount` also takes the topology read lock, but point writers may run concurrently under the same read mode. Its result is consequently a best-effort count over a stable topology, not a global moment-in-time snapshot of all bits.

### Structural operations

`InsertBitAt`, `RemoveBitAt`, `Grow`, and `Shrink` can change index mapping, child capacities, or the tree root. They acquire the root lock in write mode and therefore exclude all checked point operations while the topology is changing.

Insertion and removal shift later bits across segment and child boundaries. After a shift, the affected summary path is refreshed so empty/full metadata matches the new data layout.

Growth first expands the existing rightmost node when possible. When the current root cannot represent the requested size, a new internal root is introduced and the previous root becomes its first child. Shrinking removes capacity from the right side and may collapse a root internal node when only one child remains.

These operations are the structural consistency boundary of the bitmap. Code that assumes stable indices must coordinate with them.

### Unsafe access

The bitmap exposes `Unsafe` point-operation variants for hot paths. They omit normal validation and call directly into the current root without taking the topology read lock.

The caller must therefore establish both relevant preconditions:

- the index is valid for the current length;
- no concurrent insertion, removal, growth, or shrinking can invalidate the root or index mapping during the operation.

The unsafe methods still use the same leaf CAS and summary-update machinery. They bypass safety checks and topology stabilization, not the underlying state model.

### Fullness summaries are cached state

The empty/full metadata is part of the bitmap's maintained state, not a recomputed query cache. Every mutation path that changes a child between empty, partial, and full must keep the corresponding parent summary synchronized and propagate any resulting parent transition.

This invariant is the main extension constraint for the bitmap tree. Adding a new mutation path without updating summary propagation can leave `IsEmpty`, `IsFull`, and higher-level state decisions inconsistent even if the user-visible data bits themselves were written correctly.

## Relationship between the collection designs

`ConcurrentHashSet<T>` and `ConcurrentBitmap` intentionally do not share a common internal collection framework.

The hash set coordinates independent bucket chains through stable stripes and table replacement. The bitmap instead packs the leaf state into atomic words and adds a hierarchy whose metadata depends on child state. Their synchronization strategies follow those data models, and forcing them behind one shared locking abstraction would hide the invariants that make each implementation work.
