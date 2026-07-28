# AsyncAlphaBetaLock

Location: `source/Fho.Core/Fho.Core.Threading/Async/AsyncAlphaBetaLock.cs`

## Why it exists

`AlphaBetaLockSlim` gives two-group (alpha / beta) mutual exclusion with same-group concurrency and alpha admission precedence, but it is fully synchronous: waiters block threads, ownership is thread-affine, and recursion is forbidden. That makes it a poor fit for async ASP.NET-style code that synchronizes access to shared singleton services across `await` points.

`AsyncLock` is async-native (SemaphoreSlim + AsyncLocal reentrancy + orderly dispose), but it is strict mutual exclusion — only one async flow at a time.

`AsyncAlphaBetaLock` combines both:

| Concern | Behavior |
|---|---|
| Same-group holders | Concurrent |
| Cross-group holders | Mutually exclusive |
| Alpha vs beta admission | Alpha wins: a waiting alpha blocks *new* beta entry |
| Reentrancy | Allowed for the same group on the same async flow (AsyncLocal depth) |
| Beta reentry under waiting alpha | Allowed (avoids deadlock with outer beta frame) |
| Cross-group upgrade | Forbidden (`InvalidOperationException`) |
| Waiting | `TaskCompletionSource` gates — no thread blocked on the lock |
| Disposal | Cancels waiters, drains in-flight enters, holders may still exit |

It is **not** a thin async wrapper around `AlphaBetaLockSlim`. The state machine is built on BCL async primitives so waiters do not consume thread-pool workers.

## Mental model

Think of two compatible clubs that cannot share the building:

- Any number of **alphas** may be inside together.
- Any number of **betas** may be inside together.
- Alphas and betas may never be inside at the same time.
- If an alpha is *in line*, the bouncer stops admitting new betas (even while betas are still inside). Existing betas may re-enter (bathroom break) so they can finish and leave.
- When the building empties, alphas in line go first.

## Public API shape

Mirrors `AsyncLock`, doubled for the two groups:

- **Run\* / Run\*Task\*** — acquire, run sync or async work, release in `finally`. Preferred.
- **TryRun\*** — same, but concurrent dispose yields `AsyncLockResult` with `TaskExecuted: false` instead of throwing `LockDisposedException`.
- **AcquireAlphaAsync / AcquireBetaAsync** — low-level `IDisposable` scope for `await using` / `using` patterns. Prefer Run\* when possible.
- **IsAlphaHeld / IsBetaHeld** — current async-flow ownership.
- **CurrentAlphaCount / CurrentBetaCount / WaitingAlphaCount / WaitingBetaCount** — diagnostics (approximate under concurrency).
- **Dispose** — fail future acquires; cancel and drain waiters.

## State machine

All shared mutable state is guarded by a short `lock (_stateGuard)` that is **never held across an await**.

```
_alphaHolders, _betaHolders   // outermost acquisitions only (not reentrancy depth)
_alphaWaiters, _betaWaiters   // flows parked or about to park
_alphaGate, _betaGate         // single-shot TaskCompletionSource per generation
_disposedValue                // AtomicBoolean
_waitingCount                 // in-flight EnterCoreAsync attempts (for dispose drain)
_al_alphaDepth, _al_betaDepth // AsyncLocal reentrancy per flow
```

### Admission (`CanEnter`)

| Request | Condition |
|---|---|
| Alpha | `_betaHolders == 0` |
| Beta | `_alphaHolders == 0 && _alphaWaiters == 0` |

Waiting betas do **not** block alpha. Waiting alphas **do** block new beta. Reentrancy never consults `CanEnter`.

### Ownership / reentrancy (two AsyncLocal channels)

| Channel | Type | Used by | Why |
|---|---|---|---|
| `_al_alphaDepth` / `_al_betaDepth` | `AsyncLocal<int>` | `Run*` | Same model as `AsyncLock`: nested work sees depth; concurrent sibling Runs from one parent do **not** share depths (writes don’t flow up). |
| `_al_acquireOwnership` | `AsyncLocal<FlowOwnership?>` | `Acquire*` | Heap object published on the **caller** before any await so `Is*Held` / releaser / nested `Run*` see depth after `Acquire*` returns. |

`GetTotalDepth` = run depth + acquire depth. Reentrancy (including beta under waiting alphas) and cross-group checks use the total. Only the outermost frame (total was 0) calls `EnterCoreAsync` / `ExitCore`.

### Enter sequence

1. If other-group total depth &gt; 0 → throw `InvalidOperationException`.
2. If same-group total depth &gt; 0 → reentrant: bump the appropriate depth only (no shared-state wait).
3. Otherwise `EnterCoreAsync`: `CheckDisposed`, then under `_stateGuard`: if `CanEnter`, bump holders and return; else bump waiters (alpha waiter immediately closes beta admission), snapshot current gate `Task`.
4. `await gate.WaitAsync(linkedToken)` outside the lock.
5. On wake, loop to step 3 (still registered as waiter until acquire or cancel).
6. On cancel: unregister waiter; if last alpha waiter with no alpha holders, pulse betas; `CheckDisposed` (dispose → `LockDisposedException`) else rethrow `OperationCanceledException` / `TaskCanceledException`.
7. After successful outermost enter, bump run or acquire depth for this frame.

### Exit sequence

1. Decrement AsyncLocal depth.
2. If depth hits 0, under `_stateGuard` decrement holders; if holders hit 0, select pulse target:
   - alpha waiters present → take/clear `_alphaGate`
   - else if beta waiters present → take/clear `_betaGate`
3. `TrySetResult` **outside** `_stateGuard`.

### Pulse protocol

Gates are single-shot:

1. Waiters await the current TCS.
2. Pulser nulls the field and completes the old TCS outside the lock.
3. Next waiter allocates a fresh incomplete TCS via `GetOrCreateGate`.
4. All waiters re-check `CanEnter` after wake (no trust in the pulse alone).

`TaskCreationOptions.RunContinuationsAsynchronously` plus “complete outside the lock” prevents waiter continuations from running under `_stateGuard`.

## Race catalog (maintainers)

These are the races the implementation is written against. If you change the state machine, re-verify each.

### 1. Dispose vs enter (TOC/TOU)

Enter checks disposed, then later takes `_stateGuard`. Dispose may CAS the flag in between. Under `_stateGuard`, enter re-checks disposed, unregisters if it had become a waiter, pulses betas if it was the last alpha waiter, then throws `LockDisposedException` **after** releasing `_stateGuard`.

### 2. Dispose vs parked waiter

Dispose cancels `_cts` (unblocks `WaitAsync`) **and** pulses both gates (covers the lost-wakeup window where cancel has not yet been observed). Enter’s cancel path unregisters and converts dispose-cancel into `LockDisposedException`.

### 3. Dispose vs holder

Holders are not in `_waitingCount`. Dispose does not wait for them. `ExitCore` remains valid after dispose so `finally` blocks do not fault after successful work. New reentry on a still-held outer frame is also allowed (depth &gt; 0 fast path skips `CheckDisposed`) so nested work under an in-flight holder does not throw mid-section during teardown.

### 4. Dispose drainage

`_waitingCount` covers every non-reentrant enter attempt. Dispose spins (`SpinWait.SpinUntil`) until it is zero before returning. This is the intentional short-term blocking exception called out in the product requirements — dispose is rare; orphaned waiters are not acceptable.

### 5. Cancel vs pulse

If a waiter is both pulsed and cancelled, `WaitAsync` may throw `OperationCanceledException`. Cancellation wins: the flow unregisters and does not acquire. That is intentional; the caller asked to abort.

### 6. Last alpha waiter cancels

While an alpha is registered as a waiter, `CanEnter(beta)` is false. If that alpha cancels (or hits dispose) and it was the last alpha waiter with `_alphaHolders == 0`, betas would be stranded unless we pulse them. `UnregisterWaiter_NoLock` handles this.

### 7. Alpha arrives while betas wait on an empty lock

Alpha `CanEnter` only checks holders, not beta waiters, so alpha walks in immediately. Correct precedence; betas remain parked until alphas finish.

### 8. Pulse then alpha sneaks in before beta re-acquires

Last alpha exits → pulse betas → before a beta re-takes `_stateGuard`, a new alpha acquires (holders were 0, waiters 0). Beta wakes, `CanEnter` fails, re-waits. Alpha precedence holds.

### 9. Beta reentry while alpha waits

Outer beta holds (`_betaHolders ≥ 1`, flow depth ≥ 1). Alpha registers as waiter (`_alphaWaiters ≥ 1`) → new betas blocked. Nested beta `RunBeta*` on the same flow hits the depth fast path and proceeds without consulting `CanEnter`. Without this, the outer beta could never finish and alpha would wait forever (deadlock).

### 10. Exception in user work

`LockAsync` always `ExitLocal`s in `finally`. Holder counts cannot leak from user exceptions.

### 11. Double-dispose / double-release of Acquire scope

Dispose is idempotent via CAS on `_disposedValue`. `LockReleaser` uses `Interlocked.Exchange` so double-`Dispose` on the scope is a no-op.

### 12. Thundering herd

A pulse wakes all waiters of a group; each re-serializes on `_stateGuard` and admits if `CanEnter`. Same-group waiters typically all admit (no concurrency cap). This is simple and correct; fairness beyond alpha precedence is not guaranteed.

## What this is not

- **Not** a reader/writer lock. Both groups are “writer-like” relative to each other; neither is a pure reader tier with upgrade paths.
- **Not** fair FIFO across groups. Alpha can starve beta by design (same as `AlphaBetaLockSlim`).
- **Not** thread-affine. Ownership is async-flow-affine via `AsyncLocal`. Do not assume `Thread.CurrentThread` is stable across awaits inside a hold.
- **Not** a wrapper over `AlphaBetaLockSlim` / `ReaderWriterLockSlim` / a blocking `Monitor.Wait`. Blocking waits on the hot path would defeat async scalability.

## Implementation map

| Piece | Role |
|---|---|
| `EnterCoreAsync` | Admission + wait loop + cancel/dispose handling |
| `ExitCore` / `SelectPulseTarget_NoLock` | Holder release + alpha-preferring wake |
| `CanEnter_NoLock` | Group exclusivity + alpha precedence |
| `LockAsync` / `ExitLocal` | Depth bookkeeping + guaranteed release |
| `LockReleaser` | Acquire\* scope token |
| `_waitingCount` + Dispose spin | Orphan-free teardown |

## Testing expectations

See `Fho.Core.Threading.Tests/Async/AsyncAlphaBetaLockTests.cs`. Tests use timeouts (typically via `CancellationTokenSource.CancelAfter` or `Task.WhenAny` with a delay) so a logic bug deadlocks the test harness rather than hanging CI forever. Coverage should include:

- Same-group concurrency (alpha–alpha, beta–beta)
- Cross-group exclusion
- Alpha precedence over waiting / held beta
- Beta reentrancy under waiting alpha
- Cross-group rejection
- Cancellation of waiters
- Dispose vs waiters and Try\* skipped results
- Exception safety (lock released)
- Acquire\* scope dispose

## Change checklist

When editing the lock:

1. Re-read the race catalog above.
2. Keep `_stateGuard` sections tiny and await-free.
3. Complete TCS gates only outside `_stateGuard`.
4. Keep alpha waiter registration as the beta admission barrier (not only alpha holders).
5. Keep the reentrancy fast path before any shared-state check.
6. Run the full `AsyncAlphaBetaLockTests` suite; treat any timeout as a deadlock bug, not a flake, until proven otherwise.
