# Synchronization

The synchronization subsystem contains three lock models with deliberately different ownership semantics. Choosing between them is primarily a question of what constitutes an owner and which operations are compatible, not which API shape is most convenient.

## `AsyncLock`: async-flow mutual exclusion

`AsyncLock` serializes work submitted through its `RunAsync` and `RunTaskAsync` families. The public API does not expose an acquisition token or separate exit operation. Instead, the lock owns acquisition and release around a caller-provided delegate, which keeps the release path inside a `finally` block controlled by the lock.

### Acquisition and execution flow

For the outermost call in an async flow:

1. the lock checks whether it has already been disposed;
2. it creates a linked cancellation source only when caller cancellation must be combined with disposal cancellation;
3. it waits asynchronously on the single-slot `SemaphoreSlim`;
4. after acquisition, it records ownership in an `AsyncLocal<int>` depth counter;
5. it executes the caller delegate while holding the semaphore;
6. the `finally` path decrements the depth and releases the semaphore when the outermost scope exits.

Nested calls made in the same async flow see a non-zero depth and skip the semaphore wait. The depth still changes for each nested scope, so only the outermost scope owns the physical semaphore slot.

This makes reentrancy an async-execution-context property. It is not thread recursion: continuations may resume on different threads while remaining part of the same logical async flow.

### Synchronous versus asynchronous delegates

`RunAsync` accepts synchronous `Action`/`Func<TResult>` delegates. `RunTaskAsync` accepts delegates that return `Task` or `Task<TResult>` and passes the original caller cancellation token into them.

In both cases the critical section lasts until the supplied work completes. The lock does not release between awaits inside a `RunTaskAsync` delegate.

### Cancellation

Caller cancellation and disposal cancellation have different contracts.

Caller cancellation can interrupt a pending semaphore wait. If the caller delegate accepts the token, the same original token is also passed into that delegate; cancellation during execution is therefore cooperative and determined by the delegate.

Disposal cancellation exists to terminate pending acquisition. If a semaphore wait is canceled because the lock is being disposed, the implementation converts that race into `LockDisposedException`. Caller-originated cancellation remains `OperationCanceledException`.

### Disposal

Disposal is a terminal state transition arbitrated by `AtomicBoolean`. The first disposer:

1. marks the lock disposed so new entries fail;
2. cancels the internal token source, waking pending waiters;
3. waits until registered semaphore waiters have observed cancellation and left the wait path;
4. disposes the semaphore.

A delegate that already owns the lock is not forcibly aborted. Its release path tolerates the semaphore having been disposed concurrently.

The `TryRunAsync` and `TryRunTaskAsync` families convert `LockDisposedException` into an `AsyncLockResult` whose `TaskExecuted` flag is false. They do not suppress caller cancellation or exceptions thrown by the user delegate.

### Result contract

`AsyncLockResult` distinguishes "the task ran" from "the task was skipped because the lock was disposed." The generic form carries the delegate result and exposes `TryGetResult`; it can also be converted to the non-generic result when only execution status matters.

This result is specifically a disposal-race contract. It is not a general success/failure envelope for the protected work.

## `AlphaBetaLockSlim`: two compatibility groups

`AlphaBetaLockSlim` is a synchronous, thread-affine lock for workloads with two classes of operation:

- alpha operations are mutually compatible with other alpha operations;
- beta operations are mutually compatible with other beta operations;
- alpha and beta operations are incompatible with each other.

The names are intentionally semantic placeholders. The application decides what alpha and beta mean.

### Admission policy

Alpha has precedence. Once an alpha waiter is registered, new beta acquisition is blocked even while existing beta holders are still draining. When the beta count reaches zero, waiting alpha callers are released as a group. Beta waiters are only released when no alpha waiter remains.

This policy prevents a steady stream of new beta acquisitions from indefinitely delaying a queued alpha request, but it allows alpha traffic to starve beta traffic. That tradeoff is part of the lock contract.

### State and waiting model

A packed 64-bit owner word stores the active alpha count, active beta count, and a "waiting alpha" bit. The bit layout makes beta admission fail naturally whenever alpha ownership or alpha waiting state is present.

Acquisition first uses short spin phases. Contended callers then wait on lazily created manual-reset events, one for each group. A small internal spin lock protects the owner word, waiter counts, and event-transition decisions; the events themselves are signaled after leaving that spin lock.

The spin lock also deprioritizes acquisition paths that cannot currently make progress. In particular, alpha admission is favored over competing beta admission while an alpha is trying to establish its waiting state. This supports the higher-level alpha-precedence rule rather than defining a separate fairness policy.

### Thread ownership and recursion

Ownership is tracked in a thread-static linked list keyed by a numeric lock identifier. As a result:

- enter and exit must occur on the same thread;
- recursive acquisition of the same group throws `LockRecursionException`;
- attempting to acquire the other group while already holding one throws `InvalidOperationException`;
- there is no alpha-to-beta or beta-to-alpha upgrade path.

This ownership model is fundamentally different from `AsyncLock`; `AlphaBetaLockSlim` should not be held across an `await` that can resume on another thread.

### Acquisition APIs

Each group exposes the same three acquisition styles:

- blocking `Enter*Lock`;
- immediate or timeout-based `TryEnter*Lock`;
- `Acquire*Lock`, which returns `ILockOwnership` for `using`-scoped release.

Timeout handling is centralized in `TimeoutTracker`, so repeated spin and wait phases consume one total timeout budget rather than restarting the timeout at each phase.

### Disposal

`AlphaBetaLockSlim.Dispose` disposes its wait handles and makes future acquisition fail with `ObjectDisposedException`. It rejects disposal when waiters are registered or when the disposing thread itself owns the lock.

Disposal is not a coordinated shutdown mechanism for arbitrary participating threads. Callers should arrange quiescence before disposing a shared instance rather than racing disposal against active use.

## Scoped `ReaderWriterLockSlim` ownership

`ReaderWriterLockSlimExtensions` adds `AcquireReadLock`, `AcquireWriteLock`, and `AcquireUpgradableReadLock`. Each method enters the corresponding BCL lock mode immediately and returns an `ILockOwnership` whose disposal exits that mode.

The extensions do not alter `ReaderWriterLockSlim` fairness, recursion, thread affinity, timeout, or disposal semantics. Their purpose is lifetime safety and readability:

```csharp
using ILockOwnership readLock = rwLock.AcquireReadLock();
// protected synchronous work
```

The hierarchical bitmap uses these helpers to separate ordinary point operations, which hold the root lock in read mode, from topology-changing operations, which require write mode.

## Choosing a synchronization primitive

Use `AsyncLock` when the critical section must survive asynchronous suspension and ownership should follow the logical async flow.

Use `AlphaBetaLockSlim` when two synchronous operation classes are internally compatible but mutually incompatible, and alpha precedence is appropriate for the workload.

Use `ReaderWriterLockSlim` through the scoped extensions when the conventional many-readers/single-writer model matches the state being protected.

Do not interchange these types solely because they all provide "locking." Their ownership, reentrancy, fairness, and disposal contracts are different and are relied on by their callers.
