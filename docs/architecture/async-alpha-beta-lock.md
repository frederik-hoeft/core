# AsyncAlphaBetaLock

Location: `source/Fho.Core/Fho.Core.Threading/Async/AsyncAlphaBetaLock.cs`

## Purpose and public contract

`AsyncAlphaBetaLock` combines the two-group semantics of `AlphaBetaLockSlim` with async-native waiting and async-flow reentrancy:

- operations in the same group may execute concurrently;
- alpha and beta operations never overlap;
- a registered alpha waiter blocks new beta ownership generations;
- an already active ownership generation may reenter its own group, including beta while alpha waits;
- cross-group reentrancy is rejected with `InvalidOperationException`;
- callers submit work only through `Run*` and `TryRun*`; there is no manual acquire/release API;
- caller cancellation aborts admission, including a pre-canceled reentrant call;
- disposal seals new ownership generations, wakes queued operations, and never blocks waiting for async continuations;
- operations already admitted before disposal may finish and reenter their existing generation.

`TryRun*` returns `TaskExecuted: false` only when disposal prevents admission. Exceptions thrown by user work, including `LockDisposedException`, propagate unchanged.

## Shared state

A short `lock (_stateGuard)` protects:

```text
_alphaHolders, _betaHolders
_alphaWaiters, _betaWaiters
_alphaGate, _betaGate
_disposedValue
```

The guard is never held across `await`. Gate completion happens after the guard is released and each gate uses `RunContinuationsAsynchronously`.

Invariants under `_stateGuard`:

```text
!(_alphaHolders > 0 && _betaHolders > 0)
_alphaHolders >= 0
_betaHolders >= 0
_alphaWaiters >= 0
_betaWaiters >= 0
```

Admission predicates:

| Request | Condition |
|---|---|
| Alpha | `_betaHolders == 0` |
| Beta | `_alphaHolders == 0 && _alphaWaiters == 0` |

Waiting beta operations do not delay alpha. A waiting alpha immediately closes admission to new beta generations.

## Ownership generations

`AsyncLocal<OwnershipLease?>` carries the current ownership generation through `ExecutionContext`. The `AsyncLocal` value is only a reference; authority comes from synchronized state inside `OwnershipLease`:

```text
_isAlpha
_activeOperations
_active
```

Every structured `Run*` invocation contributes one active-operation reference:

1. If the ambient lease is active for the requested group, `TryEnter` increments its reference count.
2. If the ambient lease is active for the opposite group, the call fails.
3. If no active lease exists, the call performs normal admission, creates a fresh lease generation, and publishes it for user work.
4. In `finally`, the invocation decrements the lease count.
5. The transition from one reference to zero marks the lease inactive and releases exactly one shared holder.

`TryEnter` and the final `Exit` are serialized by the lease guard. Therefore a concurrent nested call either joins before the active-to-inactive transition, keeping the shared holder alive, or observes an inactive lease and performs fresh admission.

This handles two otherwise dangerous cases:

- nested work may outlive the callback that created it without releasing cross-group exclusion early;
- a child context may retain a copied `AsyncLocal` reference, but cannot use it after the generation becomes inactive.

Suppressing `ExecutionContext` flow removes the ambient lease and therefore removes reentrancy.

## Wait and handoff protocol

A denied operation registers once as a waiter and awaits its group's current single-shot `TaskCompletionSource` gate. A pulse detaches the gate, completes it outside `_stateGuard`, and causes all waiters on that generation to recheck admission.

When the final holder exits:

1. pulse alpha if any alpha waits;
2. otherwise pulse beta if any beta waits.

If the last alpha waiter cancels while no alpha holds, the cancellation path pulses beta because the alpha admission barrier has disappeared.

Cancellation is checked under `_stateGuard` before admission. If cancellation and handoff race, the first state transition observed under the guard determines whether the operation is admitted or canceled; it is never both.

## Disposal

`Dispose` performs only a bounded synchronous transition:

1. under `_stateGuard`, set `_disposedValue` and detach both group gates;
2. complete `_disposeGate` and the detached group gates outside the guard.

Every parked operation waits for either its group gate or `_disposeGate`. The independent disposal gate covers the race where a normal handoff has detached a group gate but has not completed it yet when disposal occurs.

There is no `CancellationTokenSource` to dispose and no waiter-drain spin. Consequently disposal cannot deadlock a single-threaded `SynchronizationContext` or a constrained thread pool.

A queued operation rechecks `_disposedValue` under `_stateGuard`, unregisters itself, and throws `LockDisposedException`. Existing ownership generations do not consult disposal on reentry and can complete their structured work safely.

## Linearization points

| Operation | Linearization point |
|---|---|
| New admission | holder increment under `_stateGuard` |
| Waiter registration | waiter increment under `_stateGuard` |
| Cancellation before admission | canceled-state branch under `_stateGuard` |
| Disposal | `_disposedValue = true` under `_stateGuard` |
| Reentrant join | lease reference increment under `_leaseGuard` |
| Final ownership release | lease active-to-inactive transition under `_leaseGuard` |
| Group release | holder decrement under `_stateGuard` |

## Race checklist

Changes must preserve all of the following:

- escaped nested same-group work retains the shared holder;
- stale inherited contexts cannot reenter a closed generation;
- cross-group ownership never overlaps;
- waiting alpha closes new beta admission;
- beta reentry remains possible while alpha waits;
- cancellation unregisters exactly once;
- canceling the last alpha waiter can wake beta;
- cancellation versus handoff has one outcome;
- disposal versus entry has one outcome;
- disposal wakes a gate detached by a concurrent handoff;
- `TryRun*` catches only admission failure, never exceptions from user work;
- no gate continuation runs under `_stateGuard`;
- no public path synchronously waits for an async continuation.

## Testing

`AsyncAlphaBetaLockTests` uses explicit synchronization points and timeouts. Coverage includes same-group concurrency, cross-group exclusion, alpha precedence, beta reentry, escaped nested work, stale contexts, suppressed context flow, cancellation cleanup and races, non-blocking disposal, single-threaded-context disposal, user-exception propagation, and high-contention exclusion stress.
