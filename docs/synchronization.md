# Synchronization

The synchronization subsystem contains three lock models with deliberately different ownership semantics. Choosing between them is primarily a question of what constitutes an owner and which operations are compatible, not which API shape is most convenient.

## `AsyncLock`: async-flow mutual exclusion

`AsyncLock` serializes work submitted through its `RunAsync` and `RunTaskAsync` families. The public API exposes execution-under-lock rather than a manual enter/exit pair: acquisition, caller execution, and release remain one lock-owned operation, with release guaranteed from a `finally` path.

### Ownership and reentrancy

An outer acquisition owns one slot in a single-count `SemaphoreSlim`. Once acquired, the lock creates an ownership context for that physical lease and places a root ownership frame in the current `AsyncLocal`. Nested async calls inherit that frame through `ExecutionContext`, which allows reentrant calls to recognize the existing physical ownership across ordinary `await` boundaries.

Inheritance alone is not treated as proof that a branch may re-enter. Each ownership context contains one shared top-of-stack reference, while each async flow carries the frame it inherited. A reentrant call may advance the stack only when its inherited frame is still the shared top. The new frame is installed atomically, so two descendants that inherited the same parent frame cannot both become the next reentrant owner.

This establishes a strict serialized call-stack contract:

```text
outer
  -> nested
       -> nested
       <- nested
  <- nested
<- outer
```

Concurrent branching of inherited ownership is invalid. A sibling reentrant acquisition, a parent trying to re-enter while a child frame is active, stale inherited ownership after the root has exited, or a non-LIFO exit raises `AsyncLockUsageException`. The shared ownership context is then poisoned: existing frames may still unwind so the physical semaphore can be released safely, but no further reentrant acquisition is accepted through that context.

If an ancestor exits while a descendant frame is still active, the lock does not release the semaphore underneath the descendant. The ancestor is marked for deferred exit, the violating operation throws, and the descendant that eventually unwinds the stack also drains any exposed exit-requested ancestors. The root semaphore lease is released only when the root frame is actually removed.

The lock can diagnose only state transitions that cross its API boundary. Caller code that forks while holding the lock, overlaps protected work, and restores the ownership stack before the original branch next interacts with the lock can evade detection. Such concurrent branching is still outside the contract; once it occurs, mutual-exclusion guarantees do not apply to the overlapping caller code even if no exception is observed.

`IsHeld` therefore means that the current async flow carries the active, non-poisoned top frame for this ownership context. It is not a thread-affinity check.

### Delegate execution

`RunAsync` accepts synchronous `Action`/`Func<TResult>` delegates. `RunTaskAsync` accepts delegates that return `Task` or `Task<TResult>`. In both cases the critical section lasts until the supplied work completes; the lock is not released between awaits inside an asynchronous delegate.

The original caller cancellation token is passed to asynchronous delegates. The lock does not introduce disposal cancellation into caller code, so an already admitted delegate is not forcibly aborted when the lock is disposed.

### Cancellation and result semantics

For an outer acquisition, caller cancellation can interrupt the pending semaphore wait. When caller cancellation wins, the operation throws `OperationCanceledException` associated with the original caller token. Disposal uses a separate internal cancellation source only to wake waiters that have not been admitted to caller execution.

Acquisition status is resolved before the caller delegate is invoked. The `Run*` methods therefore report disposal-before-execution as `LockDisposedException`, while the corresponding `Try*` methods return an `AsyncLockResult` with `TaskExecuted == false`. In otherwise valid ownership usage, once caller code has started its exceptions propagate unchanged. In particular, a `LockDisposedException` thrown by the caller delegate is not reinterpreted as a skipped `Try*` operation. A simultaneous reentrancy-contract violation can instead surface `AsyncLockUsageException` from the lock-owned unwind path.

The generic `AsyncLockResult<TResult>` carries the delegate result and exposes `TryGetResult`; it can be converted to the non-generic form when only execution status matters. The result is specifically an admission/disposal contract, not a general success/failure envelope.

### Disposal and resource lifetime

Disposal separates logical lifetime from physical resource lifetime. The first `Dispose` closes admission and cancels the internal disposal token so pending waiters can leave. It does not wait for those continuations, post a draining task, or require a synchronization context, task scheduler, or spare thread-pool worker to make progress.

An outer acquisition registers as a resource user before it reads the internal cancellation token or touches the semaphore. That reference remains active until the wait path has fully exited or, after successful acquisition, until the root ownership frame finally releases the physical semaphore lease. Reentrant frames share the root reference because they do not touch another physical semaphore slot.

Physical disposal of the cancellation source and semaphore is enabled only after disposal cancellation has been issued. If no resource user remains, the disposing thread finalizes inline. Otherwise `Dispose` returns immediately, and one participant atomically claims cleanup after the last admitted waiter or root holder has left. Racing calls rejected after admission closes may participate transiently in the lifetime count, but they never touch the disposable resources. No participant blocks waiting for another participant to reach cleanup.

Operations that crossed the acquisition admission point before disposal may finish normally. Waiters that have not been admitted, future outer acquisitions, and reentrant acquisitions attempted after logical disposal do not start new caller work.

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
