using Fho.Core.Threading.Exceptions;
using Fho.Core.Threading.Optimistic;
using System.Diagnostics;

namespace Fho.Core.Threading.Async;

public sealed partial class AsyncLock : IDisposable
{
    private const string REENTRANCY_BRANCH_MESSAGE = "Concurrent or branched reentrant AsyncLock acquisition violated the serialized call-stack contract.";

    // Task.CompletedTask, just with a dummy bool result to wrap Action delegates into Task<TResult>
    private static readonly Task<bool> s_completedDummyTask = Task.FromResult(true);

    // the semaphore owns the physical exclusion slot; reentrant frames share one acquired slot
    private readonly SemaphoreSlim _semaphore = new(initialCount: 1, maxCount: 1);
    // disposal cancellation is used only to wake pending outer waiters
    private readonly CancellationTokenSource _disposalCancellationSource = new();
    // each async flow carries its current frame; all descendant frames share a heap ownership context
    private readonly AsyncLocal<OwnershipFrame?> _al_currentFrame = new();
    // outer acquisition paths and root owners keep the disposable resources alive while they may be touched
    private int _resourceUsers;
    // see LifecycleState: disposal closes admission before cancellation and enables finalization only afterwards
    private LifecycleState _lifecycleState;

    /// <summary>
    /// Whether the current async flow is the active serialized owner of the lock.
    /// </summary>
    public bool IsHeld => GetCurrentActiveFrame() is not null;

    /// <summary>
    /// The number of times the current async flow has entered the lock recursively.
    /// </summary>
    internal int LocksHeld => GetCurrentActiveFrame()?.Depth ?? 0;

    // Acquisition status is resolved before caller code runs so Try* never mistakes a caller-thrown
    // LockDisposedException for a concurrent-disposal result.
    private async Task<ExecutionResult<TResult>> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken)
    {
        OwnershipFrame? frame = await TryEnterAsync(cancellationToken);
        if (frame is null)
        {
            return ExecutionResult<TResult>.Skipped();
        }

        _al_currentFrame.Value = frame;
        try
        {
            TResult result = await synchronizedTask(cancellationToken);
            return ExecutionResult<TResult>.Executed(result);
        }
        finally
        {
            try
            {
                ExitFrame(frame);
            }
            finally
            {
                // AsyncLocal assignments made by nested async methods do not flow back into their caller.
                // Restoring the parent keeps this execution path internally coherent while it unwinds.
                _al_currentFrame.Value = frame.Parent;
            }
        }
    }

    [DebuggerStepThrough]
    private Task<OwnershipFrame?> TryEnterAsync(CancellationToken cancellationToken)
    {
        OwnershipFrame? inheritedFrame = _al_currentFrame.Value;
        return inheritedFrame is null
            ? TryEnterOuterAsync(cancellationToken)
            : Task.FromResult(TryEnterReentrant(inheritedFrame));
    }

    [DebuggerStepThrough]
    private OwnershipFrame? TryEnterReentrant(OwnershipFrame inheritedFrame)
    {
        if (ReadLifecycleState() != LifecycleState.Active)
        {
            return null;
        }

        OwnershipContext context = inheritedFrame.Context;
        if (Atomic.VolatileRead(in context.Poisoned))
        {
            throw new AsyncLockUsageException("The inherited AsyncLock ownership context was invalidated by an earlier reentrancy violation.");
        }

        OwnershipFrame? top = Volatile.Read(ref context.Top);
        if (top is null)
        {
            Poison(context);
            throw new AsyncLockUsageException("The async flow attempted to reuse stale AsyncLock ownership after its outer ownership had already ended.");
        }

        if (!ReferenceEquals(top, inheritedFrame) || Atomic.VolatileRead(in inheritedFrame.ExitRequested))
        {
            Poison(context);
            throw new AsyncLockUsageException(REENTRANCY_BRANCH_MESSAGE);
        }

        OwnershipFrame childFrame = new(context, inheritedFrame, inheritedFrame.Depth + 1);
        OwnershipFrame? observed = Interlocked.CompareExchange(ref context.Top, childFrame, inheritedFrame);
        if (!ReferenceEquals(observed, inheritedFrame))
        {
            Poison(context);
            throw new AsyncLockUsageException(REENTRANCY_BRANCH_MESSAGE);
        }

        return childFrame;
    }

    [DebuggerStepThrough]
    private async Task<OwnershipFrame?> TryEnterOuterAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _resourceUsers);
        bool resourceReferenceTransferred = false;
        bool semaphoreAcquired = false;
        CancellationTokenSource? linkedCancellationSource = null;
        try
        {
            // Registration happens before this check. Once Active has been observed, physical disposal
            // cannot occur until this operation releases or transfers its resource reference.
            if (ReadLifecycleState() != LifecycleState.Active)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            CancellationToken waitToken;
            if (cancellationToken.CanBeCanceled)
            {
                linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(_disposalCancellationSource.Token, cancellationToken);
                waitToken = linkedCancellationSource.Token;
            }
            else
            {
                waitToken = _disposalCancellationSource.Token;
            }

            try
            {
                await _semaphore.WaitAsync(waitToken);
                semaphoreAcquired = true;
            }
            catch (OperationCanceledException) when (ReadLifecycleState() != LifecycleState.Active)
            {
                return null;
            }
            catch (OperationCanceledException)
            {
                // WaitAsync reports cancellation with the linked token. Re-throw from the original caller
                // token so callers can reliably identify caller cancellation.
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            // A successful semaphore wait can race disposal cancellation or another holder's release.
            // Observing Active here is the admission linearization point for caller execution.
            if (ReadLifecycleState() != LifecycleState.Active)
            {
                return null;
            }

            // Dispose the temporary linked source while this acquisition still owns the resource reference.
            // If disposal were ever to fail, the local finally path can still release both the semaphore slot
            // and the resource reference instead of leaking a transferred root lease.
            linkedCancellationSource?.Dispose();
            linkedCancellationSource = null;

            OwnershipContext context = new();
            OwnershipFrame rootFrame = new(context, parent: null, depth: 1);
            Volatile.Write(ref context.Top, rootFrame);

            // The root ownership context now owns both the semaphore slot and this resource reference.
            resourceReferenceTransferred = true;
            semaphoreAcquired = false;
            return rootFrame;
        }
        finally
        {
            if (semaphoreAcquired)
            {
                _semaphore.Release();
            }

            linkedCancellationSource?.Dispose();

            if (!resourceReferenceTransferred)
            {
                ReleaseResourceUser();
            }
        }
    }

    [DebuggerStepThrough]
    private void ExitFrame(OwnershipFrame frame)
    {
        OwnershipContext context = frame.Context;
        while (true)
        {
            OwnershipFrame? top = Volatile.Read(ref context.Top);
            if (ReferenceEquals(top, frame))
            {
                OwnershipFrame? observed = Interlocked.CompareExchange(ref context.Top, frame.Parent, frame);
                if (!ReferenceEquals(observed, frame))
                {
                    continue;
                }

                if (frame.Parent is null)
                {
                    ReleaseRootOwnership(context);
                    return;
                }

                DrainExitRequestedFrames(context);
                return;
            }

            // A descendant is still active, or this frame has already become stale. Record the exit
            // request before failing so the descendant that eventually reaches this frame can finish
            // physical cleanup instead of releasing the semaphore underneath itself.
            Atomic.Exchange(ref frame.ExitRequested, true);
            Poison(context);
            DrainExitRequestedFrames(context);
            throw new AsyncLockUsageException("AsyncLock ownership exited out of order while a descendant reentrant frame was still active.");
        }
    }

    [DebuggerStepThrough]
    private void DrainExitRequestedFrames(OwnershipContext context)
    {
        while (true)
        {
            OwnershipFrame? top = Volatile.Read(ref context.Top);
            if (top is null || Atomic.VolatileRead(in top.ExitRequested) == false)
            {
                return;
            }

            OwnershipFrame? observed = Interlocked.CompareExchange(ref context.Top, value: top.Parent, comparand: top);
            if (!ReferenceEquals(observed, top))
            {
                continue;
            }

            if (top.Parent is null)
            {
                ReleaseRootOwnership(context);
                return;
            }
        }
    }

    [DebuggerStepThrough]
    private void ReleaseRootOwnership(OwnershipContext context)
    {
        Debug.Assert(Volatile.Read(ref context.Top) is null);
        bool previousRootReleased = Atomic.Exchange(ref context.RootReleased, true);
        Debug.Assert(!previousRootReleased);

        try
        {
            _semaphore.Release();
        }
        finally
        {
            ReleaseResourceUser();
        }
    }

    [DebuggerStepThrough]
    private OwnershipFrame? GetCurrentActiveFrame()
    {
        OwnershipFrame? frame = _al_currentFrame.Value;
        if (frame is null || Atomic.VolatileRead(in frame.Context.Poisoned))
        {
            return null;
        }

        return ReferenceEquals(Volatile.Read(ref frame.Context.Top), frame) ? frame : null;
    }

    [DebuggerStepThrough]
    private static void Poison(OwnershipContext context) => Atomic.Exchange(ref context.Poisoned, true);

    [DebuggerStepThrough]
    private LifecycleState ReadLifecycleState() => Atomic.VolatileRead(in _lifecycleState);

    [DebuggerStepThrough]
    private void ReleaseResourceUser()
    {
        int remaining = Interlocked.Decrement(ref _resourceUsers);
        Debug.Assert(remaining >= 0);
        if (remaining == 0)
        {
            TryFinalizeDisposal();
        }
    }

    [DebuggerStepThrough]
    private void TryFinalizeDisposal()
    {
        if (Volatile.Read(ref _resourceUsers) != 0)
        {
            return;
        }
        if (Atomic.CompareExchange(ref _lifecycleState, value: LifecycleState.Disposed, comparand: LifecycleState.Quiescing) != LifecycleState.Quiescing)
        {
            return;
        }

        try
        {
            _disposalCancellationSource.Dispose();
        }
        finally
        {
            _semaphore.Dispose();
        }
    }

    /// <summary>
    /// Logically disposes the lock, cancels pending waiters, and releases its resources once admitted users have quiesced.
    /// </summary>
    /// <remarks>
    /// This method is thread-safe and non-blocking with respect to pending asynchronous lock operations. An already admitted or executing delegate is allowed to finish; waiters that have not crossed the execution-admission point and future acquisitions do not start caller code.
    /// </remarks>
    public void Dispose()
    {
        if (Atomic.CompareExchange(ref _lifecycleState, value: LifecycleState.Canceling, comparand: LifecycleState.Active) != LifecycleState.Active)
        {
            return;
        }

        try
        {
            _disposalCancellationSource.Cancel();
        }
        finally
        {
            // Canceling deliberately cannot finalize resources. Publishing Quiescing only after Cancel
            // prevents a rejected racing entrant from becoming the last user and disposing the CTS while
            // the disposer is still trying to signal it.
            Atomic.CompareExchange(ref _lifecycleState, value: LifecycleState.Quiescing, comparand: LifecycleState.Canceling);
            TryFinalizeDisposal();
        }
    }
}
