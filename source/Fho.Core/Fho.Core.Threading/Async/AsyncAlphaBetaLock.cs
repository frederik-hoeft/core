using Fho.Core.Threading.Exceptions;
using Fho.Core.Threading.Optimistic;
using System.Diagnostics;

namespace Fho.Core.Threading.Async;

/// <summary>
/// An asynchronous two-group lock combining the group semantics of
/// <c>AlphaBetaLockSlim</c> with the async-flow reentrancy and disposal model of
/// <see cref="AsyncLock"/>.
/// </summary>
/// <remarks>
/// <para>
/// Members of the same group may hold the lock concurrently. Members of different groups
/// are mutually exclusive. Alpha has admission precedence: a waiting alpha blocks
/// <em>new</em> beta acquisition (including while beta currently holds the lock), which can
/// starve beta. Reentrant acquisition by an async flow that already holds its group is
/// always granted immediately — even for beta while alphas are waiting — so that an in-flight
/// beta operation cannot deadlock against a later alpha waiter.
/// </para>
/// <para>
/// This type is async-native: waiters park on <see cref="TaskCompletionSource"/> gates rather
/// than blocking thread-pool workers. A short <see langword="lock"/> protects only the state
/// machine; it is never held across an <see langword="await"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("AlphaHolders = {CurrentAlphaCount}, BetaHolders = {CurrentBetaCount}, AlphaWaiters = {WaitingAlphaCount}, BetaWaiters = {WaitingBetaCount}")]
[method: DebuggerStepThrough]
public sealed class AsyncAlphaBetaLock() : IDisposable
{
    // Task.CompletedTask, just with a dummy bool result to wrap Action delegates into Task<TResult>
    private static readonly Task<bool> s_completedDummyTask = Task.FromResult(true);

    // Short critical section protecting holder/waiter counts and the per-group pulse gates.
    // NEVER hold this lock across an await — waiters must park on TaskCompletionSource so that
    // thread-pool workers are not blocked (async-all-the-way).
    private readonly Lock _stateGuard = new();

    // Per-group holder counts. Same-group holders are compatible; the two groups are exclusive.
    // Invariant (under _stateGuard): !(_alphaHolders > 0 && _betaHolders > 0)
    private int _alphaHolders;
    private int _betaHolders;

    // Per-group waiter counts. Alpha waiters participate in admission control for beta:
    // new beta entry requires _alphaHolders == 0 && _alphaWaiters == 0.
    private int _alphaWaiters;
    private int _betaWaiters;

    // Manual-reset-style async gates. Waiters await the current gate's Task; a pulse completes
    // the gate and clears the field so the next waiter allocates a fresh incomplete gate.
    // TaskCreationOptions.RunContinuationsAsynchronously prevents waiter continuations from
    // running inline on the pulsing thread (which could otherwise re-enter _stateGuard or starve release).
    private TaskCompletionSource? _alphaGate;
    private TaskCompletionSource? _betaGate;

    // Cancels all waiters on Dispose. Linked with the caller's token on each wait.
    private readonly CancellationTokenSource _cts = new();

    // --- Ownership model (two complementary AsyncLocal channels) ---
    //
    // Run* reentrancy uses value-type depths, same as AsyncLock:
    //   - Writes after await are visible to nested work on the same flow
    //   - Writes do NOT flow up to a parent, so concurrent sibling Runs stay independent
    //
    // Acquire* uses a mutable FlowOwnership heap object published on the *caller's* context
    // before any await, so Is*Held and LockReleaser.Dispose observe depth after Acquire returns.
    // Nested Run* under an Acquire scope also sees this object and reenters correctly.
    private readonly AsyncLocal<int> _al_alphaDepth = new();
    private readonly AsyncLocal<int> _al_betaDepth = new();
    private readonly AsyncLocal<FlowOwnership?> _al_acquireOwnership = new();

    // Interlocked disposed flag. Once true, all future public interactions throw LockDisposedException
    // (or Try* returns Skipped). Current holders may still exit.
    private AtomicBoolean _disposedValue;

    // Number of async flows currently inside EnterCoreAsync (including those that will acquire
    // without parking). Dispose spins until this reaches zero so that every waiter has observed
    // cancellation before Dispose returns — otherwise a waiter could hang forever on a gate
    // that will never be pulsed again.
    private int _waitingCount;

    /// <summary>
    /// Whether the current async flow holds the alpha group lock (at any reentrancy depth).
    /// </summary>
    public bool IsAlphaHeld => GetTotalDepth(isAlpha: true) > 0;

    /// <summary>
    /// Whether the current async flow holds the beta group lock (at any reentrancy depth).
    /// </summary>
    public bool IsBetaHeld => GetTotalDepth(isAlpha: false) > 0;

    /// <summary>
    /// The reentrancy depth of the alpha lock for the current async flow.
    /// </summary>
    internal int AlphaLocksHeld => GetTotalDepth(isAlpha: true);

    /// <summary>
    /// The reentrancy depth of the beta lock for the current async flow.
    /// </summary>
    internal int BetaLocksHeld => GetTotalDepth(isAlpha: false);

    /// <summary>
    /// Approximate number of async flows waiting to enter the alpha group.
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
    /// Approximate number of async flows waiting to enter the beta group.
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

    /// <summary>
    /// Approximate number of concurrent alpha holders (each outermost acquisition counts once;
    /// reentrant depth on the same flow does not increase this value).
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
    /// Approximate number of concurrent beta holders (each outermost acquisition counts once;
    /// reentrant depth on the same flow does not increase this value).
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

    #region Alpha public API

    /// <summary>
    /// Asynchronously acquires the alpha group lock and executes <paramref name="synchronizedAction"/>,
    /// releasing the lock when the action completes.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The current async flow holds the beta lock.</exception>
    [DebuggerStepThrough]
    public Task RunAlphaAsync(Action synchronizedAction, CancellationToken cancellationToken = default)
    {
        return LockAsync(isAlpha: true, Wrapper, cancellationToken);

        [DebuggerStepThrough]
        Task<bool> Wrapper(CancellationToken _)
        {
            synchronizedAction();
            return s_completedDummyTask;
        }
    }

    /// <summary>
    /// Asynchronously acquires the alpha group lock and executes <paramref name="synchronizedAction"/>,
    /// releasing the lock when the action completes.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The current async flow holds the beta lock.</exception>
    [DebuggerStepThrough]
    public Task<TResult> RunAlphaAsync<TResult>(Func<TResult> synchronizedAction, CancellationToken cancellationToken = default)
    {
        return LockAsync(isAlpha: true, Wrapper, cancellationToken);

        [DebuggerStepThrough]
        Task<TResult> Wrapper(CancellationToken _) => Task.FromResult(synchronizedAction());
    }

    /// <summary>
    /// Asynchronously acquires the alpha group lock and executes <paramref name="synchronizedTask"/>,
    /// releasing the lock when the task completes.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The current async flow holds the beta lock.</exception>
    [DebuggerStepThrough]
    public Task RunAlphaTaskAsync(Func<CancellationToken, Task> synchronizedTask, CancellationToken cancellationToken = default)
    {
        return LockAsync(isAlpha: true, Wrapper, cancellationToken);

        [DebuggerStepThrough]
        async Task<bool> Wrapper(CancellationToken ct)
        {
            await synchronizedTask(ct);
            return true;
        }
    }

    /// <summary>
    /// Asynchronously acquires the alpha group lock and executes <paramref name="synchronizedTask"/>,
    /// releasing the lock when the task completes.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The current async flow holds the beta lock.</exception>
    [DebuggerStepThrough]
    public Task<TResult> RunAlphaTaskAsync<TResult>(Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedTask);
        return LockAsync(isAlpha: true, synchronizedTask, cancellationToken);
    }

    /// <summary>
    /// Attempts to acquire the alpha group lock and run <paramref name="synchronizedAction"/>.
    /// Returns a skipped result instead of throwing when the lock is disposed concurrently.
    /// </summary>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult> TryRunAlphaAsync(Action synchronizedAction, CancellationToken cancellationToken = default)
    {
        try
        {
            await RunAlphaAsync(synchronizedAction, cancellationToken);
            return new AsyncLockResult(TaskExecuted: true);
        }
        catch (LockDisposedException)
        {
            return AsyncLockResult.Skipped();
        }
    }

    /// <summary>
    /// Attempts to acquire the alpha group lock and run <paramref name="synchronizedAction"/>.
    /// Returns a skipped result instead of throwing when the lock is disposed concurrently.
    /// </summary>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult<TResult>> TryRunAlphaAsync<TResult>(Func<TResult> synchronizedAction, CancellationToken cancellationToken = default)
    {
        try
        {
            TResult result = await RunAlphaAsync(synchronizedAction, cancellationToken);
            return new AsyncLockResult<TResult>(result, TaskExecuted: true);
        }
        catch (LockDisposedException)
        {
            return AsyncLockResult.Skipped<TResult>();
        }
    }

    /// <summary>
    /// Attempts to acquire the alpha group lock and run <paramref name="synchronizedTask"/>.
    /// Returns a skipped result instead of throwing when the lock is disposed concurrently.
    /// </summary>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult> TryRunAlphaTaskAsync(Func<CancellationToken, Task> synchronizedTask, CancellationToken cancellationToken = default)
    {
        try
        {
            await RunAlphaTaskAsync(synchronizedTask, cancellationToken);
            return new AsyncLockResult(TaskExecuted: true);
        }
        catch (LockDisposedException)
        {
            return AsyncLockResult.Skipped();
        }
    }

    /// <summary>
    /// Attempts to acquire the alpha group lock and run <paramref name="synchronizedTask"/>.
    /// Returns a skipped result instead of throwing when the lock is disposed concurrently.
    /// </summary>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult<TResult>> TryRunAlphaTaskAsync<TResult>(Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken = default)
    {
        try
        {
            TResult result = await RunAlphaTaskAsync(synchronizedTask, cancellationToken);
            return new AsyncLockResult<TResult>(result, TaskExecuted: true);
        }
        catch (LockDisposedException)
        {
            return AsyncLockResult.Skipped<TResult>();
        }
    }

    /// <summary>
    /// Asynchronously acquires the alpha group lock and returns a scope that releases it on dispose.
    /// Prefer the <c>Run*</c> APIs when possible — they guarantee release via <see langword="finally"/>.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The current async flow holds the beta lock.</exception>
    [DebuggerStepThrough]
    public Task<IDisposable> AcquireAlphaAsync(CancellationToken cancellationToken = default)
    {
        // CRITICAL: publish FlowOwnership on the caller's context *synchronously* before any await
        // so IsAlphaHeld / nested Run reentrancy / LockReleaser.Dispose observe depth after return.
        FlowOwnership ownership = GetOrCreateAcquireOwnership();
        return AcquireAsyncCore(ownership, isAlpha: true, cancellationToken);
    }

    #endregion Alpha public API

    #region Beta public API

    /// <summary>
    /// Asynchronously acquires the beta group lock and executes <paramref name="synchronizedAction"/>,
    /// releasing the lock when the action completes.
    /// </summary>
    /// <remarks>
    /// Alpha has admission precedence: if any alpha is waiting, this method will not acquire until
    /// all alpha waiters have been satisfied and the alpha group has released, except when the
    /// current async flow already holds beta (reentrancy).
    /// </remarks>
    /// <exception cref="LockDisposedException">The lock has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The current async flow holds the alpha lock.</exception>
    [DebuggerStepThrough]
    public Task RunBetaAsync(Action synchronizedAction, CancellationToken cancellationToken = default)
    {
        return LockAsync(isAlpha: false, Wrapper, cancellationToken);

        [DebuggerStepThrough]
        Task<bool> Wrapper(CancellationToken _)
        {
            synchronizedAction();
            return s_completedDummyTask;
        }
    }

    /// <summary>
    /// Asynchronously acquires the beta group lock and executes <paramref name="synchronizedAction"/>,
    /// releasing the lock when the action completes.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The current async flow holds the alpha lock.</exception>
    [DebuggerStepThrough]
    public Task<TResult> RunBetaAsync<TResult>(Func<TResult> synchronizedAction, CancellationToken cancellationToken = default)
    {
        return LockAsync(isAlpha: false, Wrapper, cancellationToken);

        [DebuggerStepThrough]
        Task<TResult> Wrapper(CancellationToken _) => Task.FromResult(synchronizedAction());
    }

    /// <summary>
    /// Asynchronously acquires the beta group lock and executes <paramref name="synchronizedTask"/>,
    /// releasing the lock when the task completes.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The current async flow holds the alpha lock.</exception>
    [DebuggerStepThrough]
    public Task RunBetaTaskAsync(Func<CancellationToken, Task> synchronizedTask, CancellationToken cancellationToken = default)
    {
        return LockAsync(isAlpha: false, Wrapper, cancellationToken);

        [DebuggerStepThrough]
        async Task<bool> Wrapper(CancellationToken ct)
        {
            await synchronizedTask(ct);
            return true;
        }
    }

    /// <summary>
    /// Asynchronously acquires the beta group lock and executes <paramref name="synchronizedTask"/>,
    /// releasing the lock when the task completes.
    /// </summary>
    /// <exception cref="LockDisposedException">The lock has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The current async flow holds the alpha lock.</exception>
    [DebuggerStepThrough]
    public Task<TResult> RunBetaTaskAsync<TResult>(Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedTask);
        return LockAsync(isAlpha: false, synchronizedTask, cancellationToken);
    }

    /// <summary>
    /// Attempts to acquire the beta group lock and run <paramref name="synchronizedAction"/>.
    /// Returns a skipped result instead of throwing when the lock is disposed concurrently.
    /// </summary>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult> TryRunBetaAsync(Action synchronizedAction, CancellationToken cancellationToken = default)
    {
        try
        {
            await RunBetaAsync(synchronizedAction, cancellationToken);
            return new AsyncLockResult(TaskExecuted: true);
        }
        catch (LockDisposedException)
        {
            return AsyncLockResult.Skipped();
        }
    }

    /// <summary>
    /// Attempts to acquire the beta group lock and run <paramref name="synchronizedAction"/>.
    /// Returns a skipped result instead of throwing when the lock is disposed concurrently.
    /// </summary>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult<TResult>> TryRunBetaAsync<TResult>(Func<TResult> synchronizedAction, CancellationToken cancellationToken = default)
    {
        try
        {
            TResult result = await RunBetaAsync(synchronizedAction, cancellationToken);
            return new AsyncLockResult<TResult>(result, TaskExecuted: true);
        }
        catch (LockDisposedException)
        {
            return AsyncLockResult.Skipped<TResult>();
        }
    }

    /// <summary>
    /// Attempts to acquire the beta group lock and run <paramref name="synchronizedTask"/>.
    /// Returns a skipped result instead of throwing when the lock is disposed concurrently.
    /// </summary>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult> TryRunBetaTaskAsync(Func<CancellationToken, Task> synchronizedTask, CancellationToken cancellationToken = default)
    {
        try
        {
            await RunBetaTaskAsync(synchronizedTask, cancellationToken);
            return new AsyncLockResult(TaskExecuted: true);
        }
        catch (LockDisposedException)
        {
            return AsyncLockResult.Skipped();
        }
    }

    /// <summary>
    /// Attempts to acquire the beta group lock and run <paramref name="synchronizedTask"/>.
    /// Returns a skipped result instead of throwing when the lock is disposed concurrently.
    /// </summary>
    [DebuggerStepThrough]
    public async Task<AsyncLockResult<TResult>> TryRunBetaTaskAsync<TResult>(Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken = default)
    {
        try
        {
            TResult result = await RunBetaTaskAsync(synchronizedTask, cancellationToken);
            return new AsyncLockResult<TResult>(result, TaskExecuted: true);
        }
        catch (LockDisposedException)
        {
            return AsyncLockResult.Skipped<TResult>();
        }
    }

    /// <summary>
    /// Asynchronously acquires the beta group lock and returns a scope that releases it on dispose.
    /// Prefer the <c>Run*</c> APIs when possible — they guarantee release via <see langword="finally"/>.
    /// </summary>
    /// <remarks>
    /// Reentrant beta acquisition succeeds even if an alpha waiter is present.
    /// </remarks>
    /// <exception cref="LockDisposedException">The lock has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The current async flow holds the alpha lock.</exception>
    [DebuggerStepThrough]
    public Task<IDisposable> AcquireBetaAsync(CancellationToken cancellationToken = default)
    {
        FlowOwnership ownership = GetOrCreateAcquireOwnership();
        return AcquireAsyncCore(ownership, isAlpha: false, cancellationToken);
    }

    #endregion Beta public API

    /// <summary>
    /// Combined reentrancy depth: Run* value-type depth plus any ambient Acquire* scope depth.
    /// </summary>
    private int GetTotalDepth(bool isAlpha)
    {
        int runDepth = isAlpha ? _al_alphaDepth.Value : _al_betaDepth.Value;
        FlowOwnership? acquire = _al_acquireOwnership.Value;
        int acquireDepth = acquire is null ? 0 : (isAlpha ? acquire.AlphaDepth : acquire.BetaDepth);
        return runDepth + acquireDepth;
    }

    [DebuggerStepThrough]
    private FlowOwnership GetOrCreateAcquireOwnership()
    {
        FlowOwnership? ownership = _al_acquireOwnership.Value;
        if (ownership is null)
        {
            ownership = new FlowOwnership();
            _al_acquireOwnership.Value = ownership;
        }
        return ownership;
    }

    private async Task<IDisposable> AcquireAsyncCore(FlowOwnership ownership, bool isAlpha, CancellationToken cancellationToken)
    {
        ThrowIfCrossGroup(isAlpha);

        // Outermost for the shared holder count only when neither Run nor Acquire already holds.
        bool outermost = GetTotalDepth(isAlpha) == 0;
        if (outermost)
        {
            await EnterCoreAsync(isAlpha, cancellationToken);
        }

        if (isAlpha)
        {
            ownership.AlphaDepth++;
        }
        else
        {
            ownership.BetaDepth++;
        }

        return new LockReleaser(this, ownership, isAlpha, releasesSharedHolder: outermost);
    }

    // Core locking mechanism. MUST release in a finally block, otherwise we deadlock the group.
    // DebuggerStepThrough is intentionally omitted on the async state machine so stepping lands
    // in user code after acquisition (mirrors AsyncLock).
    private async Task<TResult> LockAsync<TResult>(bool isAlpha, Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken)
    {
        ThrowIfCrossGroup(isAlpha);

        // Reentrancy if this flow already holds via Run depth or an ambient Acquire scope.
        // This is the path that lets beta reenter while alphas are waiting.
        bool outermost = GetTotalDepth(isAlpha) == 0;
        if (outermost)
        {
            await EnterCoreAsync(isAlpha, cancellationToken);
        }

        // Increment Run depth AFTER EnterCoreAsync so the write lands on this method's
        // continuation context (visible to nested work; not to concurrent parent siblings).
        if (isAlpha)
        {
            ++_al_alphaDepth.Value;
        }
        else
        {
            ++_al_betaDepth.Value;
        }

        try
        {
            return await synchronizedTask(cancellationToken);
        }
        finally
        {
            if (isAlpha)
            {
                --_al_alphaDepth.Value;
                Debug.Assert(_al_alphaDepth.Value >= 0);
            }
            else
            {
                --_al_betaDepth.Value;
                Debug.Assert(_al_betaDepth.Value >= 0);
            }

            // Only the outermost frame releases the shared holder slot.
            if (outermost)
            {
                ExitCore(isAlpha);
            }
        }
    }

    private void ThrowIfCrossGroup(bool isAlpha)
    {
        int otherDepth = GetTotalDepth(isAlpha: !isAlpha);
        if (otherDepth > 0)
        {
            string held = isAlpha ? "beta" : "alpha";
            string requested = isAlpha ? "alpha" : "beta";
            throw new InvalidOperationException(
                $"Cannot acquire the {requested} lock while the current async flow holds the {held} lock. " +
                $"{nameof(AsyncAlphaBetaLock)} does not support cross-group upgrades.");
        }
    }

    /// <summary>
    /// Attempts to admit the current async flow into the requested group, parking on a TCS gate
    /// when admission is not immediately possible.
    /// </summary>
    /// <remarks>
    /// <para><b>Reentrancy:</b> handled by callers via depth checks before invoking this method.
    /// This method only runs for outermost acquisitions.</para>
    /// <para><b>Waiter registration:</b> a flow stays counted in <c>_*Waiters</c> from the first failed
    /// admission attempt until it either acquires or cancels/disposes. Registering an alpha waiter
    /// immediately closes beta admission (<see cref="CanEnter_NoLock"/>), matching AlphaBetaLockSlim's
    /// WAITING_ALPHAS bit — even before the alpha actually parks on the gate.</para>
    /// <para><b>Pulse protocol:</b> waiters always re-check <see cref="CanEnter_NoLock"/> after wake-up.
    /// Gates are single-shot; after a pulse the field is nulled and the next waiter creates a new
    /// incomplete TCS. Completing the TCS happens *outside* <c>_stateGuard</c> to avoid running
    /// continuations (even with RunContinuationsAsynchronously as belt-and-suspenders) under the lock.</para>
    /// <para><b>Cancellation / dispose:</b> a linked token unifies caller cancellation with Dispose.
    /// On cancel we must unregister as a waiter; if we were the last alpha waiter and no alpha
    /// holds, we may need to pulse beta waiters that were held back solely by our presence.</para>
    /// </remarks>
    [DebuggerStepThrough]
    private async Task EnterCoreAsync(bool isAlpha, CancellationToken cancellationToken)
    {
        // Outermost enter only — callers already handled reentrancy / cross-group.
        CheckDisposed();

        CancellationTokenSource? linkedCts = null;
        CancellationToken ct;
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Link dispose-cancellation with the caller's token so either source can unblock the wait.
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            ct = linkedCts.Token;
        }
        else
        {
            ct = _cts.Token;
        }

        // Track this enter attempt for Dispose drainage. Includes both the immediate-acquire path
        // and the parking path so Dispose never tears down while a concurrent Enter is mid-flight.
        Interlocked.Increment(ref _waitingCount);
        bool registeredAsWaiter = false;
        try
        {
            while (true)
            {
                Task? waitTask = null;
                TaskCompletionSource? pulseAfterLock = null;
                bool throwDisposed = false;

                lock (_stateGuard)
                {
                    // TOC/TOU: Dispose may have raced in after CheckDisposed() above.
                    // Unregister (and maybe free betas) under the lock, but pulse + throw only
                    // after release — never complete a TCS or throw while holding _stateGuard.
                    if (Atomic.VolatileRead(in _disposedValue))
                    {
                        UnregisterWaiter_NoLock(isAlpha, ref registeredAsWaiter, out pulseAfterLock);
                        throwDisposed = true;
                    }
                    else if (CanEnter_NoLock(isAlpha))
                    {
                        if (registeredAsWaiter)
                        {
                            DecrementWaiters_NoLock(isAlpha);
                            registeredAsWaiter = false;
                        }
                        IncrementHolders_NoLock(isAlpha);
                        return;
                    }
                    else
                    {
                        // Admission denied — register as waiter (idempotent across loop iterations).
                        // For alpha, this immediately blocks *new* beta admission (alpha precedence).
                        if (!registeredAsWaiter)
                        {
                            IncrementWaiters_NoLock(isAlpha);
                            registeredAsWaiter = true;
                        }

                        waitTask = GetOrCreateGate_NoLock(isAlpha).Task;
                    }
                }

                // Side-effects that must not run under _stateGuard:
                pulseAfterLock?.TrySetResult();
                LockDisposedException.ThrowIf(condition: throwDisposed, this);

                Debug.Assert(waitTask is not null);

                // Park outside _stateGuard. WaitAsync respects both caller cancellation and Dispose.
                try
                {
                    await waitTask.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // Unregister before converting dispose-cancel into LockDisposedException.
                    // Race: we may have been pulsed successfully *and* cancelled; cancellation wins
                    // if WaitAsync throws — we intentionally do not acquire in that case.
                    TaskCompletionSource? pulse = null;
                    lock (_stateGuard)
                    {
                        UnregisterWaiter_NoLock(isAlpha, ref registeredAsWaiter, out pulse);
                    }
                    pulse?.TrySetResult();

                    // If Dispose cancelled us, surface LockDisposedException so Try* can skip.
                    CheckDisposed();
                    // Otherwise this is genuine caller cancellation — propagate
                    // (may be TaskCanceledException from WaitAsync).
                    throw;
                }
                // Woken by pulse: loop and re-evaluate CanEnter. We remain registered as a waiter
                // until we either acquire or cancel, so alpha precedence stays intact across retries.
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waitingCount);
            linkedCts?.Dispose();
        }
    }

    /// <summary>
    /// Releases one outermost group hold and, if this was the last holder of the group,
    /// pulses the appropriate waiters (alphas preferred).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wake policy (mirrors AlphaBetaLockSlim's ExitAndWakeUpAppropriateWaitersPreferringAlphas):
    /// </para>
    /// <list type="number">
    /// <item>If the group still has holders, nobody is pulsed (same-group concurrency has no cap here).</item>
    /// <item>If the group is empty and alpha waiters exist, pulse alphas only — never hand the lock
    /// to beta while alphas are queued.</item>
    /// <item>If the group is empty and only beta waiters exist, pulse betas.</item>
    /// </list>
    /// <para>
    /// The TCS is completed *outside* <c>_stateGuard</c>. Completing inside the lock risks deadlock if a
    /// continuation ran inline and tried to re-enter <c>_stateGuard</c> (e.g. a synchronous
    /// <c>OnCompleted</c> path). We still use <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>
    /// as defense in depth.
    /// </para>
    /// <para>
    /// Exit remains valid after Dispose: a holder that acquired before Dispose must be able to
    /// release without throwing, otherwise user <c>finally</c> blocks would fault after successful work
    /// (same rationale as <see cref="AsyncLock"/>).
    /// </para>
    /// </remarks>
    [DebuggerStepThrough]
    private void ExitCore(bool isAlpha)
    {
        TaskCompletionSource? toPulse = null;
        lock (_stateGuard)
        {
            if (isAlpha)
            {
                _alphaHolders--;
                Debug.Assert(_alphaHolders >= 0, "Alpha holder count underflow");
                if (_alphaHolders == 0)
                {
                    toPulse = SelectPulseTarget_NoLock();
                }
            }
            else
            {
                _betaHolders--;
                Debug.Assert(_betaHolders >= 0, "Beta holder count underflow");
                if (_betaHolders == 0)
                {
                    toPulse = SelectPulseTarget_NoLock();
                }
            }
        }
        // Pulse outside the lock — see remarks.
        toPulse?.TrySetResult();
    }

    // Prefer alpha waiters; only wake betas when no alpha is waiting.
    // Called under _stateGuard. Returns the gate to complete (field already cleared), or null.
    private TaskCompletionSource? SelectPulseTarget_NoLock()
    {
#if DEBUG
        Debug.Assert(_stateGuard.IsHeldByCurrentThread);
#endif
        // Even after Dispose we may pulse to help waiters fall out of WaitAsync quickly;
        // they will observe cancellation / disposed and exit. Harmless if the gate is null.
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

    private static TaskCompletionSource? TakeGate_NoLock(ref TaskCompletionSource? gate) => Interlocked.Exchange(ref gate, value: null);

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

    // Admission rules (under _stateGuard):
    //   Alpha: no beta holders. (Waiting betas do not block alpha — alpha precedence.)
    //   Beta:  no alpha holders AND no alpha waiters. (Waiting alphas close the door to new betas.)
    // Reentrancy is handled before this check and never consults these rules.
    private bool CanEnter_NoLock(bool isAlpha)
    {
#if DEBUG
        Debug.Assert(_stateGuard.IsHeldByCurrentThread);
#endif
        if (isAlpha)
        {
            return _betaHolders == 0;
        }
        return _alphaHolders == 0 && _alphaWaiters == 0;
    }

    private void IncrementHolders_NoLock(bool isAlpha)
    {
#if DEBUG
        Debug.Assert(_stateGuard.IsHeldByCurrentThread);
#endif
        if (isAlpha)
        {
            _alphaHolders++;
        }
        else
        {
            _betaHolders++;
        }
    }

    private void IncrementWaiters_NoLock(bool isAlpha)
    {
#if DEBUG
        Debug.Assert(_stateGuard.IsHeldByCurrentThread);
#endif
        if (isAlpha)
        {
            _alphaWaiters++;
        }
        else
        {
            _betaWaiters++;
        }
    }

    private void DecrementWaiters_NoLock(bool isAlpha)
    {
#if DEBUG
        Debug.Assert(_stateGuard.IsHeldByCurrentThread);
#endif
        if (isAlpha)
        {
            _alphaWaiters--;
            Debug.Assert(_alphaWaiters >= 0);
        }
        else
        {
            _betaWaiters--;
            Debug.Assert(_betaWaiters >= 0);
        }
    }

    /// <summary>
    /// Unregisters this flow as a waiter, if registered, and optionally produces a beta pulse
    /// when the last alpha waiter drops away with no alpha holders (so stranded beta waiters
    /// are not left blocked solely by a cancelled/disposed alpha waiter).
    /// </summary>
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

        // Last alpha waiter gone, no alpha holders => beta may proceed if any are waiting.
        // (If alpha holders remain, betas stay blocked until ExitCore pulses.)
        if (isAlpha && _alphaWaiters == 0 && _alphaHolders == 0 && _betaWaiters > 0)
        {
            pulse = TakeGate_NoLock(ref _betaGate);
        }
    }

    [DebuggerStepThrough]
    private void CheckDisposed()
    {
        bool disposedValue = Atomic.VolatileRead(in _disposedValue);
        LockDisposedException.ThrowIf(disposedValue, this);
    }

    /// <summary>
    /// Releases all resources used by this lock and fails pending / future acquisitions with
    /// <see cref="LockDisposedException"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dispose is thread-safe and idempotent. It does <em>not</em> require the lock to be free
    /// (unlike <c>AlphaBetaLockSlim</c>): concurrent holders are allowed to finish and exit;
    /// waiters are cancelled. This matches <see cref="AsyncLock"/> and is the only model that is
    /// safe for async singleton teardown without racing in-flight requests.
    /// </para>
    /// <para>
    /// Order of operations (all races hinge on this):
    /// </para>
    /// <list type="number">
    /// <item>CAS the disposed flag so new enters fail at <see cref="CheckDisposed"/>.</item>
    /// <item>Cancel <c>_cts</c> so parked <c>WaitAsync</c> calls throw.</item>
    /// <item>Pulse both gates so waiters that lost the race with cancellation still wake.</item>
    /// <item>Spin until <c>_waitingCount == 0</c> so every in-flight enter has left EnterCoreAsync.
    /// Short blocking is acceptable here — dispose is rare and must not leave orphans.</item>
    /// <item>Dispose the CTS. Holders may still call ExitCore afterwards; that path tolerates disposal.</item>
    /// </list>
    /// </remarks>
    public void Dispose()
    {
        // dispose only once
        if (Atomic.CompareExchange(ref _disposedValue, value: true, comparand: false) == false)
        {
            // 1+2: seal the lock and cancel waiters.
            _cts.Cancel();

            // 3: pulse both gates under _stateGuard, complete outside.
            TaskCompletionSource? alphaGate;
            TaskCompletionSource? betaGate;
            lock (_stateGuard)
            {
                alphaGate = TakeGate_NoLock(ref _alphaGate);
                betaGate = TakeGate_NoLock(ref _betaGate);
            }
            alphaGate?.TrySetResult();
            betaGate?.TrySetResult();

            // 4: drain in-flight enters. Spin/yield only — dispose is rare.
            SpinWait.SpinUntil(() => Volatile.Read(in _waitingCount) == 0);

            // 5: release the CTS. No SemaphoreSlim to dispose (async-native design).
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Mutable ownership record for <see cref="AcquireAlphaAsync"/> / <see cref="AcquireBetaAsync"/> scopes.
    /// Published into the caller's <see cref="AsyncLocal{T}"/> before any await so depth remains
    /// visible after the Acquire task completes (see class-level ownership remarks).
    /// </summary>
    private sealed class FlowOwnership
    {
        public int AlphaDepth;
        public int BetaDepth;
    }

    /// <summary>
    /// Scope token returned by <see cref="AcquireAlphaAsync"/> / <see cref="AcquireBetaAsync"/>.
    /// Dispose releases one reentrancy level (and the shared holder slot when this scope was outermost).
    /// </summary>
    private sealed class LockReleaser(
        AsyncAlphaBetaLock owner,
        FlowOwnership ownership,
        bool isAlpha,
        bool releasesSharedHolder) : IDisposable
    {
        private AsyncAlphaBetaLock? _owner = owner;

        public void Dispose()
        {
            // Idempotent: protect against double-dispose by callers.
            AsyncAlphaBetaLock? owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            if (isAlpha)
            {
                ownership.AlphaDepth--;
                Debug.Assert(ownership.AlphaDepth >= 0);
            }
            else
            {
                ownership.BetaDepth--;
                Debug.Assert(ownership.BetaDepth >= 0);
            }

            if (releasesSharedHolder)
            {
                owner.ExitCore(isAlpha);
            }
        }
    }
}
