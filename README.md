# Fho.Core

A .NET 10 library providing low-level threading and concurrency utilities: an async mutual-exclusion lock, a two-group pessimistic lock, lock-free atomic helpers, and small coordination primitives.

## Fho.Core.Threading

### Async locking

`AsyncLock` is a `SemaphoreSlim`-backed mutual-exclusion lock safe for use across `await` async boundaries. It supports async-flow reentrancy via `AsyncLocal` depth tracking so the same logical async flow can re-enter without deadlocking. Work is submitted via `RunAsync`/`RunTaskAsync`; `TryRunAsync`/`TryRunTaskAsync` variants return an `AsyncLockResult` instead of throwing when the lock is disposed concurrently.

Disposal is orderly: it atomically marks the lock disposed, cancels any pending waiters, drains the waiter count, and then disposes the semaphore. Disposal races are normalized into `LockDisposedException` rather than surfacing `ObjectDisposedException` from the underlying primitive.

`AsyncAlphaBetaLock` combines the two-group semantics of `AlphaBetaLockSlim` (same-group concurrency, cross-group exclusion, alpha admission precedence) with async-native waiting and generation-scoped async-flow reentrancy. Work is submitted only through structured `Run*`/`TryRun*` methods. Waiters park on `TaskCompletionSource` gates rather than blocking threads, and disposal seals admission and wakes waiters without synchronously draining async continuations. Active beta ownership generations may reenter while alpha is waiting, preventing nested beta work from deadlocking against alpha precedence. See `docs/architecture/async-alpha-beta-lock.md` for the state machine and race catalog.

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
