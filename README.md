# Fho.Core

A .NET 10 library providing low-level threading and concurrency utilities: an async mutual-exclusion lock, a two-group pessimistic lock, lock-free atomic helpers, and small coordination primitives.

## Fho.Core.Threading

### Async locking

`AsyncLock` is a `SemaphoreSlim`-backed mutual-exclusion lock whose critical section may cross `await` boundaries. Callers submit work through `RunAsync`/`RunTaskAsync` rather than manually acquiring and releasing ownership. Reentrancy is supported for a strictly serialized logical async call stack: inherited ownership can re-enter without taking another semaphore slot, while observable concurrent branching is rejected with `AsyncLockUsageException`.

Disposal closes admission immediately and cancels pending waiters, but physical cleanup is lazy. The internal cancellation source and semaphore remain alive until the last admitted waiter or lock holder leaves its resource path, so `Dispose` never waits for asynchronous continuations to run. `TryRunAsync`/`TryRunTaskAsync` report disposal-before-execution through `AsyncLockResult`; exceptions thrown by caller code, including `LockDisposedException`, still propagate normally in otherwise valid usage.

### Pessimistic locking

`AlphaBetaLockSlim` is a two-group reader-writer-like lock where concurrent holders within the same group are compatible, but the two groups are mutually exclusive. This is suited for workloads where operations naturally partition into two incompatible sets (e.g. two classes of write that conflict with each other but not within their own class). Alpha has admission precedence: a waiting alpha blocks new beta entry, which can starve beta. The lock uses a packed 64-bit state word, per-group manual-reset events for blocked waiters, and thread-static ownership records that prevent recursion and cross-group upgrades.

`ReaderWriterLockSlimExtensions` wraps the BCL `ReaderWriterLockSlim` enter/exit calls in `ILockOwnership` disposables for `using`-scoped lock management.

### Lock-free atomics

`Atomic` provides a suite of CAS-loop operations over `int`, `long`, `AtomicBoolean`, and any unmanaged enum (via size-specialized `Unsafe.BitCast`). Operations include exchange, compare-exchange, volatile read, modulo-increment, clamped increment/decrement, write-max, conditional flag-based exchange, bit set/clear/toggle, and generic read-transform-CAS.

`AtomicBoolean` is an explicit-layout 4-byte boolean struct whose encoding (`0` / all-bits-set) supports direct use with `Interlocked` without boxing.

`EnumHelpers` adds `And`, `Or`, `Xor`, `Not`, and `FastEquals` extension methods to any unmanaged enum, dispatched by backing-type size.

### Coordination helpers

`Wait.Until` is a hybrid polling helper: it performs an immediate check, a short spin phase via `SpinWait`, then a sleep loop. It is suitable for coarse coordination where no signal source exists.

`TimeoutTracker` is an internal budget helper based on `Environment.TickCount64` that handles zero, infinite, and elapsed-overrun edge cases cleanly when threading a remaining-timeout through layered wait calls.

## Project structure

| Project | Description |
|---|---|
| `Fho.Core.Threading` | Library (net10.0, BCL-only dependencies) |
| `Fho.Core.Threading.Tests` | MSTest 4 test suite |

## Documentation

Contributor-oriented architecture and concurrency documentation lives in [`docs/`](docs/README.md):

- [Architecture overview](docs/architecture.md)
- [Synchronization](docs/synchronization.md)
- [Atomic primitives](docs/atomic-primitives.md)
- [Concurrent collections](docs/concurrent-collections.md)
