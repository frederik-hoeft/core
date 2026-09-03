# Architecture overview

Fho.Core is a small .NET 10 library collection centered on low-level utility and concurrency building blocks. The repository intentionally does not define an application framework or runtime of its own. Instead, it provides narrowly scoped primitives that callers compose into higher-level systems.

The architecture is split into two runtime assemblies:

| Assembly | Responsibility |
|---|---|
| `Fho.Core` | Low-level general-purpose helpers used by the rest of the repository, currently range-validation and performance-oriented integer math extensions. |
| `Fho.Core.Threading` | Synchronization, atomic operations, concurrent collections, and coordination helpers. It references `Fho.Core`. |

`Fho.Core.Threading.Tests` contains the test suite for the threading assembly. The dependency direction is one-way: threading utilities may depend on the base `Fho.Core` helpers, while `Fho.Core` has no dependency on the threading assembly.

## Subsystem map

The threading assembly is organized around four distinct concurrency models rather than one shared abstraction.

### Synchronization

The synchronization layer provides locks when a caller needs an explicit exclusion or compatibility boundary.

- `AsyncLock` serializes delegate execution across asynchronous boundaries. Ownership is represented by a serialized async-flow frame stack, and the public API exposes execution-under-lock rather than manual enter/exit operations.
- `AlphaBetaLockSlim` partitions synchronous callers into two compatibility groups. Members of the same group may run concurrently, while alpha and beta are mutually exclusive. Alpha admission takes precedence over beta admission.
- `ReaderWriterLockSlimExtensions` adds disposable ownership handles around the BCL reader/writer lock API so synchronous lock lifetime can be expressed with `using`.

These types intentionally have different ownership models. `AsyncLock` ownership follows asynchronous execution context but permits reentrancy only along one serialized logical call stack. `AlphaBetaLockSlim` ownership is thread-affine, while `ReaderWriterLockSlimExtensions` preserve the ownership rules of the wrapped BCL lock.

See [Synchronization](synchronization.md).

### Optimistic atomic primitives

`Atomic`, `AtomicBoolean`, and `EnumHelpers` provide operations that can be completed by atomic read/modify/write loops instead of a separately allocated lock. `Atomic` complements `Interlocked` with clamped counters, write-max operations, conditional flag exchanges, generic enum operations, and caller-supplied transforms.

The common update pattern is:

1. read the current value with volatile semantics;
2. derive a replacement value;
3. attempt a compare-and-swap;
4. retry if another writer changed the value first.

This layer is also used internally by higher-level components. The bitmap implementation, for example, uses CAS-updated 64-bit state words for its guarded leaf representation.

See [Atomic primitives](atomic-primitives.md).

### Concurrent collections

The collection layer contains two unrelated designs optimized for different workloads.

`ConcurrentHashSet<T>` is a general hash-based set derived from the striped-lock design of `ConcurrentDictionary`. Reads traverse immutable node payloads without taking a stripe lock, mutations lock only the stripe that owns the target bucket, and global operations acquire all stripes. Resizing swaps the complete table descriptor after rebuilding the bucket topology.

`ConcurrentBitmap` is a hierarchical bit set built from guarded 56-bit atomic state words. Point updates modify the containing 64-bit word with CAS, while summary metadata tracks whether child regions are empty or full. Structural operations such as insertion, removal, growth, and shrinking take an exclusive topology lock because they can shift indices or replace nodes.

See [Concurrent collections](concurrent-collections.md).

### Coordination and supporting contracts

A few smaller types round out the public surface or support the major subsystems without introducing another architectural layer:

- `Wait.Until` is a public polling helper that performs an immediate check, then bounded spinning, then sleep-based polling when no signaling primitive is available.
- `LockDisposedException` gives `AsyncLock` a public disposal-before-execution contract independent of its underlying `SemaphoreSlim`.
- `AsyncLockUsageException` reports observable violations of the lock's serialized reentrancy contract.
- Internal `TimeoutTracker` carries one remaining timeout budget through the synchronous alpha/beta acquisition loops and handles zero and infinite timeouts.
- An internal array-view wrapper provides soft resizing over backing child arrays used by bitmap tree nodes.

The internal support types are implementation details rather than extension points.

## Cross-subsystem dependencies

The components are deliberately coupled only where a lower-level primitive establishes a useful invariant for a higher-level subsystem:

```text
Fho.Core
  range checks / fast integer math
        |
        v
Fho.Core.Threading
  Atomic / AtomicBoolean ------> ConcurrentBitmap56
                                      |
ReaderWriterLockSlim -----------------+------> ConcurrentBitmap tree
 extensions                            |
                                       v
                               resizable child-array view

TimeoutTracker ------> AlphaBetaLockSlim
```

The diagram is a dependency map, not a lifecycle pipeline. In particular, the lock types do not depend on the collection types, and `ConcurrentHashSet<T>` is independent of the bitmap implementation.

## Concurrency boundaries

The repository uses the narrowest synchronization scope that preserves the required contract.

A single atomic word is preferred when the complete state transition fits in one location. This is the model used by `Atomic` and `ConcurrentBitmap56`. When a transition spans multiple locations, the implementation introduces a more local coordination boundary: hash-set bucket mutations use stripe locks, bitmap summary transitions use per-child locks, and bitmap topology changes use a root `ReaderWriterLockSlim` write lock.

This distinction is important when extending the library. A component is not considered lock-free merely because its leaf value uses CAS. If an operation must keep several pieces of state consistent, the synchronization that protects that relationship is part of the architecture.

## Ownership and lifetime

Synchronization objects are caller-owned and do not participate in dependency injection or a global runtime lifecycle.

Lock ownership is always explicit:

- asynchronous critical sections are represented by the lifetime of the delegate executed by `AsyncLock`;
- synchronous alpha/beta and reader/writer ownership can be represented by `ILockOwnership` and a `using` scope;
- direct `AlphaBetaLockSlim.Enter*` calls require a matching `Exit*` on the same thread.

Disposal is therefore also a caller coordination concern. `AsyncLock` closes admission, wakes its pending waiters, and defers physical cleanup until resource users have left; the synchronous locks have their own stricter disposal preconditions. None of the lock types discover or stop arbitrary work outside their ownership boundary.

## Checked and unsafe APIs

Several hot-path APIs expose an `Unsafe` variant. In this repository, `Unsafe` means that part of the normal safety contract is delegated to the caller. Depending on the API, that includes bounds checking and, for hierarchical bitmap access, topology stabilization.

Unsafe variants do not establish a separate data model. They operate on the same state and must preserve the same concurrency invariants. Callers should use them only when surrounding code already proves the omitted preconditions.

## Extension boundaries

The repository currently exposes primitives rather than a plugin model. Public interfaces such as `ILockOwnership` describe a lifetime contract, not a discovery or registration mechanism. Internal node, pooling, timeout, and access-tracking types are implementation details and should not be treated as extension points.

When adding functionality, prefer extending an existing public abstraction only when the new behavior shares its ownership and concurrency contract. Otherwise, a separate primitive is usually clearer than broadening a low-level type into a general framework.
