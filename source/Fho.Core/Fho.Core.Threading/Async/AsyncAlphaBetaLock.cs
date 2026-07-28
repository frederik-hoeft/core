using Fho.Core.Threading.Exceptions;
using System.Diagnostics;

namespace Fho.Core.Threading.Async;

/// <summary>
/// An asynchronous two-group lock with same-group concurrency, cross-group exclusion,
/// alpha admission precedence, and async-flow reentrancy.
/// </summary>
/// <remarks>
/// <para>
/// Members of the same group may execute concurrently. Alpha and beta executions never overlap.
/// Once an alpha operation is waiting, new beta ownership generations are held back until all
/// eligible alpha work has completed. An already active beta ownership generation may still
/// reenter so that nested beta work cannot deadlock behind a later alpha waiter.
/// </para>
/// <para>
/// Work is submitted through the <c>Run*</c> and <c>TryRun*</c> methods. Ownership cannot escape
/// through a manual acquire/release handle. Waiters park on <see cref="TaskCompletionSource"/>
/// gates; the lock never blocks a thread while waiting for admission or disposal.
/// </para>
/// </remarks>
[DebuggerDisplay("AlphaHolders = {CurrentAlphaCount}, BetaHolders = {CurrentBetaCount}, AlphaWaiters = {WaitingAlphaCount}, BetaWaiters = {WaitingBetaCount}")]
[method: DebuggerStepThrough]
public sealed class AsyncAlphaBetaLock() : IDisposable
{
    private static readonly Task<bool> s_completedDummyTask = Task.FromResult(true);

    // Protects the complete admission state. It is held only for short, await-free state
    // transitions. Gate completion always happens after releasing this guard.
    private readonly Lock _stateGuard = new();

    // The ambient lease identifies the ownership generation inherited by the current execution
    // context. The lease itself is shared by inherited contexts and contains synchronized liveness
    // and operation-count state; a copied AsyncLocal reference alone never authorizes stale reentry.
    private readonly AsyncLocal<OwnershipLease?> _al_ownership = new();

    // Every queued operation also awaits this never-reset signal. It closes the narrow window in
    // which another thread has detached a group gate for completion but disposal races before that
    // completion occurs; disposal can always wake the waiter independently.
    private readonly TaskCompletionSource _disposeGate = CreateGate();

    // Same-group holders are compatible. Cross-group overlap is forbidden.
    // Invariant under _stateGuard: !(_alphaHolders > 0 && _betaHolders > 0).
    private int _alphaHolders;
    private int _betaHolders;

    // A registered alpha waiter immediately closes admission to new beta ownership generations.
    private int _alphaWaiters;
    private int _betaWaiters;

    // Single-shot manual-reset-style gates. A pulse detaches the current gate under _stateGuard
    // and completes it afterwards. Woken waiters always recheck the admission predicate.
    private TaskCompletionSource? _alphaGate;
    private TaskCompletionSource? _betaGate;

    // Guarded by _stateGuard. Disposal seals new ownership generations and wakes queued work,
    // but existing ownership generations remain able to finish and reenter their own group.
    private bool _disposedValue;

    /// <summary>
    /// Whether the current async flow belongs to an active alpha ownership generation.
    /// </summary>
    public bool IsAlphaHeld => _al_ownership.Value?.IsActiveFor(isAlpha: true) == true;

    /// <summary>
    /// Whether the current async flow belongs to an active beta ownership generation.
    /// </summary>
    public bool IsBetaHeld => _al_ownership.Value?.IsActiveFor(isAlpha: false) == true;

    /// <summary>
    /// Approximate number of alpha ownership generations currently admitted.
    /// </summary>
    public int CurrentAlphaCount
    {
        get
        {
            lock (_stateGuard)
            {
                return _alphaHolders;
            }
        }
    }

    /// <summary>
    /// Approximate number of beta ownership generations currently admitted.
    /// </summary>
    public int CurrentBetaCount
    {
        get
        {
            lock (_stateGuard)
            {
                return _betaHolders;
            }
        }
    }

    /// <summary>
    /// Approximate number of alpha operations waiting for a new ownership generation.
    /// </summary>
    public int WaitingAlphaCount
    {
        get
        {
            lock (_stateGuard)
            {
                return _alphaWaiters;
            }
        }
    }

    /// <summary>
    /// Approximate number of beta operations waiting for a new ownership generation.
    /// </summary>
    public int WaitingBetaCount
    {
        get
        {
            lock (_stateGuard)
            {
                return _betaWaiters;
            }
        }
    }

    #region Alpha public API

    /// <summary>
    /// Executes a synchronous action while holding alpha ownership.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock was disposed before a new ownership generation could be admitted.</exception>
    /// <exception cref="InvalidOperationException">The current async flow belongs to an active beta ownership generation.</exception>
    [DebuggerStepThrough]
    public Task RunAlphaAsync(Action synchronizedAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedAction);
        return LockAsync(isAlpha: true, Wrapper, cancellationToken);

        [DebuggerStepThrough]
        Task<bool> Wrapper(CancellationToken _)
        {
            synchronizedAction();
            return s_completedDummyTask;
        }
    }

    /// <summary>
    /// Executes a synchronous function while holding alpha ownership.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock was disposed before a new ownership generation could be admitted.</exception>
    /// <exception cref="InvalidOperationException">The current async flow belongs to an active beta ownership generation.</exception>
    [DebuggerStepThrough]
    public Task<TResult> RunAlphaAsync<TResult>(Func<TResult> synchronizedAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedAction);
        return LockAsync(isAlpha: true, Wrapper, cancellationToken);

        [DebuggerStepThrough]
        Task<TResult> Wrapper(CancellationToken _) => Task.FromResult(synchronizedAction());
    }

    /// <summary>
    /// Executes an asynchronous operation while holding alpha ownership.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock was disposed before a new ownership generation could be admitted.</exception>
    /// <exception cref="InvalidOperationException">The current async flow belongs to an active beta ownership generation.</exception>
    [DebuggerStepThrough]
    public Task RunAlphaTaskAsync(Func<CancellationToken, Task> synchronizedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedTask);
        return LockAsync(isAlpha: true, Wrapper, cancellationToken);

        [DebuggerStepThrough]
        async Task<bool> Wrapper(CancellationToken ct)
        {
            await synchronizedTask(ct);
            return true;
        }
    }

    /// <summary>
    /// Executes an asynchronous function while holding alpha ownership.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock was disposed before a new ownership generation could be admitted.</exception>
    /// <exception cref="InvalidOperationException">The current async flow belongs to an active beta ownership generation.</exception>
    [DebuggerStepThrough]
    public Task<TResult> RunAlphaTaskAsync<TResult>(Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedTask);
        return LockAsync(isAlpha: true, synchronizedTask, cancellationToken);
    }

    /// <summary>
    /// Attempts to execute a synchronous action while holding alpha ownership.
    /// </summary>
    /// <returns>A skipped result only when disposal prevents admission of a new ownership generation.</returns>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult> TryRunAlphaAsync(Action synchronizedAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedAction);
        InvocationResult<bool> result = await TryLockAsync(isAlpha: true, Wrapper, cancellationToken);
        return new AsyncLockResult(result.TaskExecuted);

        [DebuggerStepThrough]
        Task<bool> Wrapper(CancellationToken _)
        {
            synchronizedAction();
            return s_completedDummyTask;
        }
    }

    /// <summary>
    /// Attempts to execute a synchronous function while holding alpha ownership.
    /// </summary>
    /// <returns>A skipped result only when disposal prevents admission of a new ownership generation.</returns>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult<TResult>> TryRunAlphaAsync<TResult>(Func<TResult> synchronizedAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedAction);
        InvocationResult<TResult> result = await TryLockAsync(isAlpha: true, Wrapper, cancellationToken);
        return result.TaskExecuted
            ? new AsyncLockResult<TResult>(result.Result, TaskExecuted: true)
            : AsyncLockResult.Skipped<TResult>();

        [DebuggerStepThrough]
        Task<TResult> Wrapper(CancellationToken _) => Task.FromResult(synchronizedAction());
    }

    /// <summary>
    /// Attempts to execute an asynchronous operation while holding alpha ownership.
    /// </summary>
    /// <returns>A skipped result only when disposal prevents admission of a new ownership generation.</returns>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult> TryRunAlphaTaskAsync(Func<CancellationToken, Task> synchronizedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedTask);
        InvocationResult<bool> result = await TryLockAsync(isAlpha: true, Wrapper, cancellationToken);
        return new AsyncLockResult(result.TaskExecuted);

        [DebuggerStepThrough]
        async Task<bool> Wrapper(CancellationToken ct)
        {
            await synchronizedTask(ct);
            return true;
        }
    }

    /// <summary>
    /// Attempts to execute an asynchronous function while holding alpha ownership.
    /// </summary>
    /// <returns>A skipped result only when disposal prevents admission of a new ownership generation.</returns>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult<TResult>> TryRunAlphaTaskAsync<TResult>(Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedTask);
        InvocationResult<TResult> result = await TryLockAsync(isAlpha: true, synchronizedTask, cancellationToken);
        return result.TaskExecuted
            ? new AsyncLockResult<TResult>(result.Result, TaskExecuted: true)
            : AsyncLockResult.Skipped<TResult>();
    }

    #endregion Alpha public API

    #region Beta public API

    /// <summary>
    /// Executes a synchronous action while holding beta ownership.
    /// </summary>
    /// <remarks>An active beta ownership generation may reenter while alpha operations are waiting.</remarks>
    /// <exception cref="LockDisposedException">The lock was disposed before a new ownership generation could be admitted.</exception>
    /// <exception cref="InvalidOperationException">The current async flow belongs to an active alpha ownership generation.</exception>
    [DebuggerStepThrough]
    public Task RunBetaAsync(Action synchronizedAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedAction);
        return LockAsync(isAlpha: false, Wrapper, cancellationToken);

        [DebuggerStepThrough]
        Task<bool> Wrapper(CancellationToken _)
        {
            synchronizedAction();
            return s_completedDummyTask;
        }
    }

    /// <summary>
    /// Executes a synchronous function while holding beta ownership.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock was disposed before a new ownership generation could be admitted.</exception>
    /// <exception cref="InvalidOperationException">The current async flow belongs to an active alpha ownership generation.</exception>
    [DebuggerStepThrough]
    public Task<TResult> RunBetaAsync<TResult>(Func<TResult> synchronizedAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedAction);
        return LockAsync(isAlpha: false, Wrapper, cancellationToken);

        [DebuggerStepThrough]
        Task<TResult> Wrapper(CancellationToken _) => Task.FromResult(synchronizedAction());
    }

    /// <summary>
    /// Executes an asynchronous operation while holding beta ownership.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock was disposed before a new ownership generation could be admitted.</exception>
    /// <exception cref="InvalidOperationException">The current async flow belongs to an active alpha ownership generation.</exception>
    [DebuggerStepThrough]
    public Task RunBetaTaskAsync(Func<CancellationToken, Task> synchronizedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedTask);
        return LockAsync(isAlpha: false, Wrapper, cancellationToken);

        [DebuggerStepThrough]
        async Task<bool> Wrapper(CancellationToken ct)
        {
            await synchronizedTask(ct);
            return true;
        }
    }

    /// <summary>
    /// Executes an asynchronous function while holding beta ownership.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock was disposed before a new ownership generation could be admitted.</exception>
    /// <exception cref="InvalidOperationException">The current async flow belongs to an active alpha ownership generation.</exception>
    [DebuggerStepThrough]
    public Task<TResult> RunBetaTaskAsync<TResult>(Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedTask);
        return LockAsync(isAlpha: false, synchronizedTask, cancellationToken);
    }

    /// <summary>
    /// Attempts to execute a synchronous action while holding beta ownership.
    /// </summary>
    /// <returns>A skipped result only when disposal prevents admission of a new ownership generation.</returns>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult> TryRunBetaAsync(Action synchronizedAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedAction);
        InvocationResult<bool> result = await TryLockAsync(isAlpha: false, Wrapper, cancellationToken);
        return new AsyncLockResult(result.TaskExecuted);

        [DebuggerStepThrough]
        Task<bool> Wrapper(CancellationToken _)
        {
            synchronizedAction();
            return s_completedDummyTask;
        }
    }

    /// <summary>
    /// Attempts to execute a synchronous function while holding beta ownership.
    /// </summary>
    /// <returns>A skipped result only when disposal prevents admission of a new ownership generation.</returns>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult<TResult>> TryRunBetaAsync<TResult>(Func<TResult> synchronizedAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedAction);
        InvocationResult<TResult> result = await TryLockAsync(isAlpha: false, Wrapper, cancellationToken);
        return result.TaskExecuted
            ? new AsyncLockResult<TResult>(result.Result, TaskExecuted: true)
            : AsyncLockResult.Skipped<TResult>();

        [DebuggerStepThrough]
        Task<TResult> Wrapper(CancellationToken _) => Task.FromResult(synchronizedAction());
    }

    /// <summary>
    /// Attempts to execute an asynchronous operation while holding beta ownership.
    /// </summary>
    /// <returns>A skipped result only when disposal prevents admission of a new ownership generation.</returns>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult> TryRunBetaTaskAsync(Func<CancellationToken, Task> synchronizedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedTask);
        InvocationResult<bool> result = await TryLockAsync(isAlpha: false, Wrapper, cancellationToken);
        return new AsyncLockResult(result.TaskExecuted);

        [DebuggerStepThrough]
        async Task<bool> Wrapper(CancellationToken ct)
        {
            await synchronizedTask(ct);
            return true;
        }
    }

    /// <summary>
    /// Attempts to execute an asynchronous function while holding beta ownership.
    /// </summary>
    /// <returns>A skipped result only when disposal prevents admission of a new ownership generation.</returns>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult<TResult>> TryRunBetaTaskAsync<TResult>(Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedTask);
        InvocationResult<TResult> result = await TryLockAsync(isAlpha: false, synchronizedTask, cancellationToken);
        return result.TaskExecuted
            ? new AsyncLockResult<TResult>(result.Result, TaskExecuted: true)
            : AsyncLockResult.Skipped<TResult>();
    }

    #endregion Beta public API

    // Core structured-ownership path. Each invocation contributes one reference to an ownership
    // lease. The last completing invocation releases the shared holder, even when nested work
    // outlives the invocation that originally acquired the group.
    private async Task<TResult> LockAsync<TResult>(bool isAlpha, Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken)
    {
        InvocationResult<TResult> result = await LockCoreAsync(isAlpha, synchronizedTask, cancellationToken, skipIfDisposed: false);
        Debug.Assert(result.TaskExecuted);
        return result.Result;
    }

    private Task<InvocationResult<TResult>> TryLockAsync<TResult>(bool isAlpha, Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken)
    {
        return LockCoreAsync(isAlpha, synchronizedTask, cancellationToken, skipIfDisposed: true);
    }

    // DebuggerStepThrough is intentionally omitted so stepping enters user code after admission.
    private async Task<InvocationResult<TResult>> LockCoreAsync<TResult>(
        bool isAlpha,
        Func<CancellationToken, Task<TResult>> synchronizedTask,
        CancellationToken cancellationToken,
        bool skipIfDisposed)
    {
        cancellationToken.ThrowIfCancellationRequested();

        OwnershipLease? previousOwnership = _al_ownership.Value;
        OwnershipLease? ownership = previousOwnership;
        OwnershipEnterResult ownershipEnterResult = ownership?.TryEnter(isAlpha) ?? OwnershipEnterResult.Inactive;

        if (ownershipEnterResult == OwnershipEnterResult.CrossGroup)
        {
            string held = isAlpha ? "beta" : "alpha";
            string requested = isAlpha ? "alpha" : "beta";
            throw new InvalidOperationException(
                $"Cannot acquire the {requested} lock while the current async flow belongs to an active {held} ownership generation. " +
                $"{nameof(AsyncAlphaBetaLock)} does not support cross-group upgrades.");
        }

        bool restorePreviousOwnership = false;
        if (ownershipEnterResult == OwnershipEnterResult.Inactive)
        {
            // Allocate before admission so an allocation failure cannot occur after the shared
            // holder count has been incremented and leak ownership.
            OwnershipLease newOwnership = new(isAlpha);
            try
            {
                await EnterCoreAsync(isAlpha, cancellationToken);
            }
            catch (LockDisposedException) when (skipIfDisposed)
            {
                return InvocationResult<TResult>.Skipped();
            }

            ownership = newOwnership;
            _al_ownership.Value = ownership;
            restorePreviousOwnership = true;
        }

        Debug.Assert(ownership is not null);
        try
        {
            TResult result = await synchronizedTask(cancellationToken);
            return InvocationResult<TResult>.Executed(result);
        }
        finally
        {
            // Restore this continuation's previous ambient value before potentially releasing the
            // shared holder. Inherited contexts retain their own lease reference and can only use it
            // while TryEnter observes that the generation is still active.
            if (restorePreviousOwnership)
            {
                _al_ownership.Value = previousOwnership;
            }

            if (ownership.Exit())
            {
                ExitCore(isAlpha);
            }
        }
    }

    /// <summary>
    /// Admits a new ownership generation or waits asynchronously until admission becomes possible.
    /// </summary>
    /// <remarks>
    /// Waiter registration and admission are linearized under <c>_stateGuard</c>. Cancellation and
    /// disposal unregister the waiter under the same guard, preventing leaked counts and lost beta
    /// wake-ups when the last alpha waiter disappears. No continuation or exception is produced
    /// while the guard is held.
    /// </remarks>
    [DebuggerStepThrough]
    private async Task EnterCoreAsync(bool isAlpha, CancellationToken cancellationToken)
    {
        bool registeredAsWaiter = false;
        while (true)
        {
            Task? waitTask = null;
            TaskCompletionSource? pulseAfterLock = null;
            bool throwDisposed = false;
            bool throwCanceled = false;

            lock (_stateGuard)
            {
                // Disposal and cancellation are checked before admission. If either was already
                // observable at this state transition, this operation cannot become a holder.
                if (_disposedValue)
                {
                    UnregisterWaiter_NoLock(isAlpha, ref registeredAsWaiter, out pulseAfterLock);
                    throwDisposed = true;
                }
                else if (cancellationToken.IsCancellationRequested)
                {
                    UnregisterWaiter_NoLock(isAlpha, ref registeredAsWaiter, out pulseAfterLock);
                    throwCanceled = true;
                }
                else if (CanEnter_NoLock(isAlpha))
                {
                    if (registeredAsWaiter)
                    {
                        DecrementWaiters_NoLock(isAlpha);
                    }
                    IncrementHolders_NoLock(isAlpha);
                    return;
                }
                else
                {
                    if (!registeredAsWaiter)
                    {
                        IncrementWaiters_NoLock(isAlpha);
                        registeredAsWaiter = true;
                    }
                    waitTask = GetOrCreateGate_NoLock(isAlpha).Task;
                }
            }

            pulseAfterLock?.TrySetResult();
            LockDisposedException.ThrowIf(throwDisposed, this);
            if (throwCanceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new UnreachableException();
            }

            Debug.Assert(waitTask is not null);
            try
            {
                Task<Task> wakeTask = Task.WhenAny(waitTask, _disposeGate.Task);
                await wakeTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TaskCompletionSource? pulse = null;
                bool disposed;
                lock (_stateGuard)
                {
                    UnregisterWaiter_NoLock(isAlpha, ref registeredAsWaiter, out pulse);
                    disposed = _disposedValue;
                }
                pulse?.TrySetResult();

                // Once disposal is visible, normalize the wake-up to LockDisposedException so the
                // TryRun APIs can distinguish failed admission from caller cancellation.
                LockDisposedException.ThrowIf(disposed, this);
                throw;
            }
        }
    }

    /// <summary>
    /// Releases the last reference of an ownership generation and wakes the next eligible group.
    /// </summary>
    /// <remarks>
    /// This method remains valid after disposal because work admitted before disposal must finish
    /// without faulting in its <c>finally</c> path. The gate is detached under the state guard and
    /// completed afterwards so no waiter continuation can execute inside the state transition.
    /// </remarks>
    [DebuggerStepThrough]
    private void ExitCore(bool isAlpha)
    {
        TaskCompletionSource? toPulse = null;
        lock (_stateGuard)
        {
            if (isAlpha)
            {
                --_alphaHolders;
                Debug.Assert(_alphaHolders >= 0, "Alpha holder count underflow");
                if (_alphaHolders == 0)
                {
                    toPulse = SelectPulseTarget_NoLock();
                }
            }
            else
            {
                --_betaHolders;
                Debug.Assert(_betaHolders >= 0, "Beta holder count underflow");
                if (_betaHolders == 0)
                {
                    toPulse = SelectPulseTarget_NoLock();
                }
            }
        }
        toPulse?.TrySetResult();
    }

    // Alpha waiters always receive the next pulse. Beta is eligible only when no alpha waits.
    private TaskCompletionSource? SelectPulseTarget_NoLock()
    {
#if DEBUG
        Debug.Assert(_stateGuard.IsHeldByCurrentThread);
#endif
        if (_alphaWaiters > 0)
        {
            return TakeGate_NoLock(ref _alphaGate);
        }
        if (_betaWaiters > 0)
        {
            return TakeGate_NoLock(ref _betaGate);
        }
        return null;
    }

    private TaskCompletionSource GetOrCreateGate_NoLock(bool isAlpha)
    {
#if DEBUG
        Debug.Assert(_stateGuard.IsHeldByCurrentThread);
#endif
        if (isAlpha)
        {
            return _alphaGate ??= CreateGate();
        }
        return _betaGate ??= CreateGate();
    }

    private static TaskCompletionSource CreateGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource? TakeGate_NoLock(ref TaskCompletionSource? gate)
    {
        TaskCompletionSource? result = gate;
        gate = null;
        return result;
    }

    // Admission rules under _stateGuard:
    //   alpha: no beta holders;
    //   beta: no alpha holders and no registered alpha waiters.
    private bool CanEnter_NoLock(bool isAlpha)
    {
#if DEBUG
        Debug.Assert(_stateGuard.IsHeldByCurrentThread);
#endif
        return isAlpha
            ? _betaHolders == 0
            : _alphaHolders == 0 && _alphaWaiters == 0;
    }

    private void IncrementHolders_NoLock(bool isAlpha)
    {
#if DEBUG
        Debug.Assert(_stateGuard.IsHeldByCurrentThread);
#endif
        if (isAlpha)
        {
            ++_alphaHolders;
        }
        else
        {
            ++_betaHolders;
        }
    }

    private void IncrementWaiters_NoLock(bool isAlpha)
    {
#if DEBUG
        Debug.Assert(_stateGuard.IsHeldByCurrentThread);
#endif
        if (isAlpha)
        {
            ++_alphaWaiters;
        }
        else
        {
            ++_betaWaiters;
        }
    }

    private void DecrementWaiters_NoLock(bool isAlpha)
    {
#if DEBUG
        Debug.Assert(_stateGuard.IsHeldByCurrentThread);
#endif
        if (isAlpha)
        {
            --_alphaWaiters;
            Debug.Assert(_alphaWaiters >= 0, "Alpha waiter count underflow");
        }
        else
        {
            --_betaWaiters;
            Debug.Assert(_betaWaiters >= 0, "Beta waiter count underflow");
        }
    }

    // Unregisters one waiter and returns a beta gate to pulse when the last alpha barrier is
    // removed while beta operations are queued. Called only under _stateGuard.
    private void UnregisterWaiter_NoLock(bool isAlpha, ref bool registeredAsWaiter, out TaskCompletionSource? pulse)
    {
#if DEBUG
        Debug.Assert(_stateGuard.IsHeldByCurrentThread);
#endif
        pulse = null;
        if (!registeredAsWaiter)
        {
            return;
        }

        DecrementWaiters_NoLock(isAlpha);
        registeredAsWaiter = false;

        if (!_disposedValue &&
            isAlpha &&
            _alphaWaiters == 0 &&
            _alphaHolders == 0 &&
            _betaWaiters > 0)
        {
            pulse = TakeGate_NoLock(ref _betaGate);
        }
    }

    /// <summary>
    /// Seals the lock against new ownership generations and wakes all queued operations.
    /// </summary>
    /// <remarks>
    /// Disposal is synchronous but non-blocking: it performs one short state transition and gate
    /// completion only. Queued operations observe <see cref="LockDisposedException"/> when they
    /// resume. Existing ownership generations remain valid until their last nested operation exits.
    /// </remarks>
    public void Dispose()
    {
        TaskCompletionSource? alphaGate;
        TaskCompletionSource? betaGate;
        lock (_stateGuard)
        {
            if (_disposedValue)
            {
                return;
            }

            _disposedValue = true;
            alphaGate = TakeGate_NoLock(ref _alphaGate);
            betaGate = TakeGate_NoLock(ref _betaGate);
        }

        // Complete the independent disposal signal first. It wakes even waiters whose group gate
        // was concurrently detached by a normal handoff but has not yet been completed.
        _disposeGate.TrySetResult();
        alphaGate?.TrySetResult();
        betaGate?.TrySetResult();
    }

    // One shared lease represents one admitted ownership generation. All inherited execution
    // contexts reference the same lease. TryEnter and Exit are serialized so a nested invocation
    // either joins before the final exit, keeping the group held, or observes an inactive lease and
    // performs a fresh admission. This closes both escaped-nesting and stale-AsyncLocal races.
    private sealed class OwnershipLease(bool isAlpha)
    {
        private readonly Lock _leaseGuard = new();
        private readonly bool _isAlpha = isAlpha;
        private int _activeOperations = 1;
        private bool _active = true;

        public OwnershipEnterResult TryEnter(bool requestedIsAlpha)
        {
            lock (_leaseGuard)
            {
                if (!_active)
                {
                    return OwnershipEnterResult.Inactive;
                }
                if (_isAlpha != requestedIsAlpha)
                {
                    return OwnershipEnterResult.CrossGroup;
                }

                checked
                {
                    ++_activeOperations;
                }
                return OwnershipEnterResult.Reentered;
            }
        }

        public bool Exit()
        {
            lock (_leaseGuard)
            {
                Debug.Assert(_active);
                Debug.Assert(_activeOperations > 0);

                --_activeOperations;
                if (_activeOperations != 0)
                {
                    return false;
                }

                // The active-to-inactive transition is the lease linearization point. A concurrent
                // TryEnter either incremented before this transition or will observe Inactive.
                _active = false;
                return true;
            }
        }

        public bool IsActiveFor(bool requestedIsAlpha)
        {
            lock (_leaseGuard)
            {
                return _active && _isAlpha == requestedIsAlpha;
            }
        }
    }

    private enum OwnershipEnterResult
    {
        Inactive,
        Reentered,
        CrossGroup,
    }

    private readonly struct InvocationResult<TResult>
    {
        private InvocationResult(TResult result, bool taskExecuted)
        {
            Result = result;
            TaskExecuted = taskExecuted;
        }

        public TResult Result { get; }

        public bool TaskExecuted { get; }

        public static InvocationResult<TResult> Executed(TResult result) => new(result, taskExecuted: true);

        public static InvocationResult<TResult> Skipped() => new(default!, taskExecuted: false);
    }
}
