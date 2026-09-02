# AsyncLock correctness redesign

## Status and scope

This document defines the target behavior and implementation plan for hardening `AsyncLock`. It is a development plan rather than steady-state architecture documentation. Phase 3 will fold the durable contracts into the public/contributor documentation and remove planning-only detail.

The redesign keeps the existing delegate-based public execution model. Callers still submit synchronous work through `RunAsync`, asynchronous work through `RunTaskAsync`, and may use the corresponding `Try*` methods when concurrent disposal should result in a skipped execution rather than an exception.

The work addresses four related correctness problems in the current implementation:

1. reentrancy is inferred from an `AsyncLocal<int>` depth. `ExecutionContext` copies that value into forked work, so a child task can inherit a positive depth and bypass the semaphore even while its parent continues executing;
2. disposal synchronously waits for semaphore waiters to resume after cancellation, which can deadlock when those continuations depend on the thread or scheduler running `Dispose`;
3. the waiter count does not cover the complete lifetime during which an operation may touch the internal `CancellationTokenSource` or `SemaphoreSlim`, so physical disposal can race supported operations on those objects;
4. `TryRunAsync` and `TryRunTaskAsync` currently infer an acquisition-disposal race by catching `LockDisposedException` around the whole operation, which can misclassify the same exception when it is thrown by caller code.

The design treats reentrancy misuse and disposal as separate state machines. Reentrancy tracks logical ownership within one acquired semaphore slot. Resource lifetime tracks whether the semaphore and cancellation source may still be touched at all.

## Target behavioral contract

### Mutual exclusion

For callers that obey the reentrancy contract, at most one outer ownership context executes under an `AsyncLock` at a time. Reentrant calls do not acquire another semaphore slot; they execute under the same physical ownership as their outer call.

The lock remains an async-flow primitive rather than a thread-affine lock. A valid critical section may suspend and resume on different threads.

### Serialized reentrancy

Reentrancy is supported only as a strictly serialized logical call stack. A conforming flow has one active reentrancy path:

```text
outer A
  -> nested A
       -> nested A
       <- nested A
  <- nested A
<- outer A
```

Concurrent branching of inherited ownership is invalid usage. Examples include starting a reentrant `Task.Run` and continuing protected work in the parent, or allowing sibling tasks that inherited the same ownership context to enter the lock concurrently.

The implementation detects violations at lock acquisition and release boundaries. Detected violations throw a dedicated usage exception and poison the affected ownership context so later reentrant acquisition cannot silently continue under an invalid topology.

The lock cannot observe arbitrary caller code between lock operations. A branch that runs concurrently, completes all of its nested lock operations, and restores the shared stack before the parent next interacts with the lock may therefore evade detection. Such branching is still outside the contract. Once callers violate the serialized-call-stack requirement, the lock makes no mutual-exclusion guarantee for the overlapping caller code even if no diagnostic is observed.

This limitation is explicit rather than hidden behind the current `AsyncLocal<int>` behavior.

### Disposal

`Dispose` has two distinct effects:

- **logical disposal** is immediate: admission closes, future outer and reentrant interactions fail, and pending outer waiters are canceled;
- **physical disposal** is lazy: the internal `CancellationTokenSource` and `SemaphoreSlim` are disposed only after the last admitted operation that may touch them has exited.

`Dispose` never spins, blocks waiting for an asynchronous continuation, or schedules a separate drainer that waits for other work. If no admitted operation remains, the disposing thread may finalize resources inline. Otherwise the last exiting waiter or lock ownership context performs finalization.

An already executing caller delegate is not forcibly canceled by disposal. The delegate still receives only its original caller cancellation token. A reentrant call attempted after logical disposal fails even when made from a delegate that acquired the lock earlier.

### Cancellation and `Try*` behavior

Caller cancellation and disposal remain distinct:

- caller cancellation while waiting produces `OperationCanceledException` associated with the caller token;
- disposal while waiting produces `LockDisposedException` for `Run*` methods or `TaskExecuted == false` for `Try*` methods;
- caller exceptions raised after acquisition propagate unchanged;
- a caller delegate that itself throws `LockDisposedException` is a caller failure and must not be converted into a skipped `Try*` result.

Null delegates are rejected before any lifetime registration or semaphore acquisition.

## Reentrancy design

### Shared ownership context and flow-local cursor

Each `AsyncLock` replaces the integer `AsyncLocal` depth with an `AsyncLocal` reference to the current ownership frame. A successful outer semaphore acquisition creates an ownership context and a root frame. Nested async calls inherit the frame reference through `ExecutionContext`.

The frame serves as the flow-local cursor. The ownership context is a heap object shared by reference across every descendant execution context that inherited it:

```text
AsyncLocal value in a flow
        |
        v
  OwnershipFrame --------+
        |                 |
        v                 v
   Parent frame      OwnershipContext
                          |
                          v
                     atomic Top
```

Every frame instance has unique object identity. That identity is the acquisition token; a separate UUID is unnecessary.

The shared context contains an atomically updated `Top` pointer. Reentrant acquisition is permitted only when the caller's inherited frame is exactly the shared top. The new frame is installed with compare-and-exchange:

```text
flow cursor = P
shared top  = P

CAS(shared top, expected: P, replacement: C)

success -> cursor for the nested call becomes C
failure -> another branch changed the stack; reject as invalid usage
```

This turns the shared context into a serialization boundary instead of treating inherited ambient state as proof of ownership.

### Detecting branching

If two forked children inherit frame `P`, only one can advance the stack from `P` to a child frame. A sibling that still holds cursor `P` observes a different shared top and fails. The same rule catches a parent attempting another lock operation while a descendant frame is still active.

A stale child that inherited an ownership frame but runs only after the outer ownership has already been released also fails: its local cursor points at a frame that is no longer the context top and whose ownership context has already closed.

### Release order and deferred unwind

Every frame records whether its owning `Run*` invocation has requested exit. Normal release marks the current frame exiting and removes it only if it is still the shared top.

If a parent invocation tries to exit while a descendant frame is still on top, the stack is not LIFO. The implementation:

1. marks the parent frame as exit-requested;
2. poisons the ownership context;
3. throws the usage exception from the operation that observed the violation;
4. leaves the physical semaphore ownership intact while descendant frames remain active.

When descendants later unwind, the thread/continuation that removes the current top also drains any now-exposed ancestor frames that had already requested exit. This allows invalid orphaning to fail loudly without immediately releasing the semaphore underneath still-running descendants.

The root frame owns the physical semaphore lease. Physical semaphore ownership is released only when the root frame is actually removed, whether by its normal exit or by a later descendant draining a previously requested root exit.

A context that has been poisoned rejects new reentrant acquisitions. Existing frames may still unwind so resources can be released deterministically when possible.

### Diagnostics

Phase 2 should introduce a public exception dedicated to invalid `AsyncLock` reentrancy topology, tentatively `AsyncLockUsageException : InvalidOperationException`. It is distinct from:

- `LockDisposedException`, which describes lock lifetime;
- `OperationCanceledException`, which describes caller cancellation;
- arbitrary caller exceptions from protected code.

The exception message should identify the invalid condition without attempting to infer a specific task/thread relationship that the runtime cannot prove. Useful categories are stale ownership, concurrent/branched reentrant acquisition, and non-LIFO exit.

## Resource lifetime and lazy disposal

### Why waiter counting is insufficient

A semaphore waiter can stop being a waiter and become the holder. At that point it still has to execute protected work and may later release the semaphore. Therefore `_waitingCount == 0` does not prove that the semaphore is safe to dispose.

The redesign tracks admitted **resource users** instead. An outer acquisition attempt registers before it reads `_cts.Token` or calls `_semaphore.WaitAsync`. Its registration remains alive until either:

- acquisition fails/cancels and the wait path has finished touching the resources; or
- acquisition succeeds and the complete root ownership context finally releases physical semaphore ownership.

Nested reentrant calls do not add resource-user references because the root context already keeps the physical resources alive for the whole reentrancy chain.

### Disposal state machine

A small atomic lifecycle state separates closing admission from enabling finalization:

```text
Active
  |
  | first Dispose
  v
Canceling       admission closed; physical finalization forbidden
  |
  | internal CTS cancellation has been issued
  v
Quiescing       last resource user may finalize
  |
  | resource-user count reaches zero and one caller wins finalization
  v
Disposed
```

The intermediate `Canceling` state is required. If finalization were allowed immediately after admission closed, a concurrently rejected entrant could transiently take and release the last reference and dispose `_cts` before the disposer had called `_cts.Cancel()`.

`Dispose` therefore performs:

1. atomically transition `Active -> Canceling`; later `Dispose` calls return;
2. cancel the internal CTS so pending waiters are woken;
3. publish `Quiescing` in a `finally` path so finalization is enabled even if cancellation unexpectedly throws;
4. attempt inline finalization if no admitted resource users remain;
5. otherwise return immediately.

The final admitted user that decrements the resource-user count to zero attempts the same finalization. Compare-and-exchange on the lifecycle state guarantees that `_cts.Dispose()` and `_semaphore.Dispose()` execute exactly once.

### Register-before-check protocol

An outer attempt must increment the resource-user count before checking whether admission is still open:

```text
increment resource users
read lifecycle state

Active      -> admitted; resources may be touched
not Active  -> release reference; fail without touching resources
```

This ordering closes the check/dispose race:

- if registration wins first, disposal observes a live resource user and cannot physically finalize;
- if disposal closes admission first, the entrant may transiently increment the count but observes the closed state and never touches either disposable object.

Once an entrant has observed `Active`, its reference keeps the resources alive even if disposal begins immediately afterward.

### Successful acquisition during disposal

A semaphore wait can race cancellation and semaphore release. After `WaitAsync` reports success, the acquisition path rechecks the lock lifecycle before invoking caller code. If logical disposal has already begun, no new delegate is admitted. The operation follows the disposal result contract and releases its resource reference without exposing the acquired slot to caller code.

The implementation must not depend on `SynchronizationContext`, `TaskScheduler`, thread-pool availability, or `ConfigureAwait(false)` for disposal correctness. Lazy finalization removes the scheduler dependency instead of moving a blocking drain to another scheduler.

## Interaction between ownership and resource lifetime

The root ownership context receives the resource-user reference from the successful outer wait. That reference is released only when physical semaphore ownership ends.

This relationship is important for both valid and invalid reentrancy:

```text
outer wait registered
    |
    v
semaphore acquired
    |
    +--> root ownership context keeps resource reference
             |
             +--> nested frames may come and go
             |
             +--> root physically released
                       |
                       v
               resource reference released
```

If an outer invocation exits illegally while an orphaned nested frame is still active, the root reference remains alive. The descendant that eventually drains the exit-requested root performs the physical release and drops that reference. Disposal can therefore remain non-blocking without disposing resources underneath the orphaned frame.

If invalid caller code never allows the inherited frames to unwind, physical cleanup may remain deferred indefinitely. That is preferable to releasing the semaphore while code still claims inherited ownership; liveness after a documented usage violation is not guaranteed.

## Internal execution shape

Phase 2 should separate acquisition status from caller-delegate execution. The core entry operation should produce an internal ownership/entry result rather than relying on `LockDisposedException` thrown across the entire protected delegate invocation.

A useful shape is:

```text
TryEnter / Enter core
    -> acquired ownership frame
    -> disposed before execution
    -> caller cancellation (exception)
    -> invalid reentrancy (exception)

public Run*
    disposed -> LockDisposedException
    acquired -> invoke caller delegate; propagate caller exceptions unchanged

public TryRun*
    disposed -> AsyncLockResult.Skipped
    acquired -> invoke caller delegate; propagate caller exceptions unchanged
```

This makes it impossible for a `LockDisposedException` thrown by user code to be mistaken for an acquisition race.

The existing delegate-based release `finally` remains mandatory. The redesign changes what that `finally` releases, not the guarantee that every successfully entered frame has a lock-owned unwind path.

## Validation plan

Phase 2 should add focused tests before considering the implementation complete.

### Basic behavior

- concurrent outer callers remain serialized;
- reentrancy works before and after ordinary `await` boundaries;
- multiple nested levels unwind in strict reverse order;
- `IsHeld` and internal depth reporting reflect the current flow's frame while ownership is active;
- null delegates fail before acquisition.

### Reentrancy misuse

- two concurrent children inheriting the same frame cannot both acquire reentrantly;
- a parent attempting another reentrant acquisition while a child frame is active receives the usage exception;
- a parent exiting while an orphaned child frame is active receives the usage exception and does not release the semaphore underneath the child;
- when that orphaned child later exits, deferred root cleanup releases the physical semaphore;
- a child using a stale inherited frame after the root already exited receives the usage exception;
- a poisoned ownership context rejects further reentrant acquisition but still permits existing frames to unwind;
- intentionally undetectable caller branching is documented as outside the contract rather than represented by a misleading passing safety test.

### Disposal and scheduler independence

- disposing with no admitted users finalizes resources synchronously and exactly once;
- disposing with a holder returns without waiting and physical cleanup occurs when the holder exits;
- disposing with one or many waiters returns without waiting, wakes the waiters, and the final exiting resource user finalizes;
- disposal racing initial token acquisition never leaks raw `ObjectDisposedException`;
- disposal racing successful semaphore acquisition never starts caller code after logical disposal;
- the behavior does not deadlock under a single-threaded `SynchronizationContext`;
- the behavior does not depend on a custom `TaskScheduler` or on an available spare thread-pool worker;
- repeated/concurrent `Dispose` calls cannot double-dispose resources.

Where useful, test-only hooks or internal observable counters may be preferable to timing-sensitive sleeps for proving finalization ordering.

### Exception/result behavior

- caller cancellation stays `OperationCanceledException`;
- disposal before execution becomes `LockDisposedException` for `Run*`;
- the same disposal race becomes `TaskExecuted == false` for `Try*`;
- `LockDisposedException` thrown by protected caller code propagates from `Try*` unchanged;
- arbitrary caller exceptions remain unchanged.

### General validation

Run the targeted `AsyncLock` suite, the complete threading test project, the full solution build/test set available in the environment, and `git diff --check`. Stress/repetition should be used for race tests where deterministic barriers cannot fully cover scheduling interleavings.

## Delivery phases

### Phase 1: design and planning

This document is the phase-1 deliverable. No runtime behavior changes are included. The phase establishes the failure model, target contracts, state machines, and validation strategy before concurrency code changes begin.

### Phase 2: implementation

Implement the lifecycle gate, reference-type ownership stack, usage diagnostics, acquisition/result separation, and focused regression tests on a fresh branch from the approved phase-1 baseline. Keep compatibility changes limited to behavior required by the corrected contracts.

The implementation should be developed in small validated batches, but phase 2 remains one reviewable architectural slice because the lifetime and ownership state machines interact at the root ownership boundary.

### Phase 3: production review and documentation

After phase 2 is approved, perform a fresh review pass from the updated baseline:

- audit the concurrency state transitions and exception precedence against the tests and design invariants;
- remove obsolete comments, temporary test hooks, and planning-only machinery;
- update `README.md`, `docs/synchronization.md`, and `docs/architecture.md` to describe only the resulting steady-state contract;
- remove or archive this planning document so the steady-state documentation remains the source of truth;
- run the strongest available final validation and inspect the complete release diff for unrelated changes.

## Alternatives deliberately not used

### Keep `AsyncLocal<int>` depth

An inherited positive integer cannot distinguish a linear continuation from a forked child and therefore cannot safely authorize reentrant semaphore bypass.

### Explicit reentrancy scope

Passing an explicit ownership capability to caller delegates is straightforward and stronger because callers must deliberately hand that capability to another branch. It remains a viable future API direction, but the current redesign preserves scope-less reentrancy and makes its serialized-flow restriction explicit and diagnosable.

### Shared reference without a flow-local cursor

A heap object alone provides upward-visible shared mutations but does not establish which inherited branch is entitled to advance ownership. The flow-local frame plus shared CAS top is required to serialize observed reentrancy transitions.

### Synchronous waiter drain

Any implementation that blocks until asynchronous waiter continuations run can deadlock if the blocker occupies the execution resource those continuations need.

### Queue the drain to the thread pool

Moving a blocking drain to `ThreadPool.UnsafeQueueUserWorkItem` bypasses a custom `TaskScheduler` but still depends on thread-pool progress. A constrained or starved pool can reproduce the same dependency cycle. Last-user finalization removes the wait entirely.

### Treat `ConfigureAwait(false)` as the disposal fix

Suppressing context/scheduler capture can mitigate specific deadlocks, but it does not establish correct resource lifetime and can change where caller-facing delegate execution resumes. Disposal correctness must be independent of continuation scheduling. Internal `ConfigureAwait` choices can then be made separately based on API semantics and performance.
