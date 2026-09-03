using Fho.Core.Threading.Async;
using Fho.Core.Threading.Exceptions;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Fho.Core.Threading.Tests.Async;

[TestClass]
public sealed class AsyncLockTests
{
    public required TestContext TestContext { get; set; }

    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(2);

    [TestMethod]
    public async Task TestRunTaskAsync_SerializesConcurrentCallersAsync()
    {
        using AsyncLock asyncLock = new();
        int active = 0;
        bool overlapped = false;

        Task[] tasks =
        [
            .. Enumerable.Range(0, 32).Select(_ => asyncLock.RunTaskAsync(async ct =>
            {
                if (Interlocked.Increment(ref active) != 1)
                {
                    overlapped = true;
                }

                await Task.Yield();
                Interlocked.Decrement(ref active);
            }, TestContext.CancellationToken))
        ];

        await Task.WhenAll(tasks);
        Assert.IsFalse(overlapped);
        Assert.IsFalse(asyncLock.IsHeld);
    }

    [TestMethod]
    public async Task TestRunTaskAsync_ReentersAcrossAwaitBoundariesAsync()
    {
        using AsyncLock asyncLock = new();
        int nestedRuns = 0;

        await asyncLock.RunTaskAsync(async ct =>
        {
            Assert.IsTrue(asyncLock.IsHeld);
            await Task.Yield();
            Assert.IsTrue(asyncLock.IsHeld);

            await asyncLock.RunTaskAsync(async nestedCt =>
            {
                Assert.IsTrue(asyncLock.IsHeld);
                nestedRuns++;
                await Task.Yield();

                await asyncLock.RunAsync(() => nestedRuns++, nestedCt);
                Assert.IsTrue(asyncLock.IsHeld);
            }, ct);

            Assert.IsTrue(asyncLock.IsHeld);
        }, TestContext.CancellationToken);

        Assert.AreEqual(2, nestedRuns);
        Assert.IsFalse(asyncLock.IsHeld);
    }

    [TestMethod]
    public async Task TestRunMethods_ReturnDelegateResultsAsync()
    {
        using AsyncLock asyncLock = new();

        int synchronousResult = await asyncLock.RunAsync(() => 42, TestContext.CancellationToken);
        int asynchronousResult = await asyncLock.RunTaskAsync(async ct =>
        {
            await Task.Yield();
            return 7;
        }, TestContext.CancellationToken);

        Assert.AreEqual(42, synchronousResult);
        Assert.AreEqual(7, asynchronousResult);
    }

    [TestMethod]
    public async Task TestNullDelegates_AreRejectedBeforeAcquisitionAsync()
    {
        using AsyncLock asyncLock = new();

        await CaptureExceptionAsync<ArgumentNullException>(async () => await asyncLock.RunAsync(null!, TestContext.CancellationToken));
        await CaptureExceptionAsync<ArgumentNullException>(async () => await asyncLock.RunTaskAsync(null!, TestContext.CancellationToken));
        await CaptureExceptionAsync<ArgumentNullException>(async () => await asyncLock.TryRunAsync(null!, TestContext.CancellationToken));
        await CaptureExceptionAsync<ArgumentNullException>(async () => await asyncLock.TryRunTaskAsync(null!, TestContext.CancellationToken));

        Assert.IsFalse(asyncLock.IsHeld);
        Assert.AreEqual(0, GetResourceUsers(asyncLock));
    }

    [TestMethod]
    public async Task TestConcurrentSiblingReentrancy_IsRejectedAsync()
    {
        using AsyncLock asyncLock = new();
        TaskCompletionSource childEntered = NewSignal();
        TaskCompletionSource releaseChild = NewSignal();
        Task? firstChild = null;

        await asyncLock.RunTaskAsync(async ct =>
        {
            firstChild = Task.Run(() => asyncLock.RunTaskAsync(async childCt =>
            {
                childEntered.SetResult();
                await releaseChild.Task;
            }, TestContext.CancellationToken), TestContext.CancellationToken);

            await childEntered.Task;

            Task secondChild = Task.Run(() => asyncLock.RunAsync(() => { }, TestContext.CancellationToken), TestContext.CancellationToken);
            await CaptureExceptionAsync<AsyncLockUsageException>(() => secondChild);

            releaseChild.SetResult();
            await firstChild;
        }, TestContext.CancellationToken);

        Assert.IsFalse(asyncLock.IsHeld);
    }

    [TestMethod]
    public async Task TestParentReentrancyWhileChildFrameIsActive_IsRejectedAsync()
    {
        using AsyncLock asyncLock = new();
        TaskCompletionSource childEntered = NewSignal();
        TaskCompletionSource releaseChild = NewSignal();
        Task? child = null;

        await asyncLock.RunTaskAsync(async ct =>
        {
            child = Task.Run(() => asyncLock.RunTaskAsync(async childCt =>
            {
                childEntered.SetResult();
                await releaseChild.Task;
            }, TestContext.CancellationToken), TestContext.CancellationToken);

            await childEntered.Task;
            await CaptureExceptionAsync<AsyncLockUsageException>(async () => await asyncLock.RunAsync(() => { }, TestContext.CancellationToken));

            releaseChild.SetResult();
            await child;
        }, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task TestPoisonedOwnershipContext_RejectsFurtherReentrancyAsync()
    {
        using AsyncLock asyncLock = new();
        TaskCompletionSource childEntered = NewSignal();
        TaskCompletionSource releaseChild = NewSignal();
        Task? child = null;

        await asyncLock.RunTaskAsync(async ct =>
        {
            child = Task.Run(() => asyncLock.RunTaskAsync(async childCt =>
            {
                childEntered.SetResult();
                await releaseChild.Task;
            }, TestContext.CancellationToken), TestContext.CancellationToken);

            await childEntered.Task;
            await CaptureExceptionAsync<AsyncLockUsageException>(async () => await asyncLock.RunAsync(() => { }, TestContext.CancellationToken));

            releaseChild.SetResult();
            await child;

            await CaptureExceptionAsync<AsyncLockUsageException>(async () => await asyncLock.RunAsync(() => { }, TestContext.CancellationToken));
        }, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task TestParentExitWithActiveOrphan_ThrowsWithoutReleasingSemaphoreAsync()
    {
        using AsyncLock asyncLock = new();
        TaskCompletionSource childEntered = NewSignal();
        TaskCompletionSource releaseChild = NewSignal();
        Task? child = null;

        Task outer = asyncLock.RunTaskAsync(async ct =>
        {
            child = Task.Run(() => asyncLock.RunTaskAsync(async childCt =>
            {
                childEntered.SetResult();
                await releaseChild.Task;
            }, TestContext.CancellationToken), TestContext.CancellationToken);

            await childEntered.Task;
        }, TestContext.CancellationToken);

        await CaptureExceptionAsync<AsyncLockUsageException>(() => outer);
        Assert.AreEqual(0, GetSemaphore(asyncLock).CurrentCount);
        Assert.AreEqual(1, GetResourceUsers(asyncLock));

        bool waiterRan = false;
        Task waiter = asyncLock.RunAsync(() => waiterRan = true, TestContext.CancellationToken);
        Assert.IsFalse(waiter.IsCompleted);

        releaseChild.SetResult();
        await child!;
        await waiter;

        Assert.IsTrue(waiterRan);
        Assert.AreEqual(0, GetResourceUsers(asyncLock));
    }

    [TestMethod]
    public async Task TestStaleInheritedOwnership_IsRejectedAfterRootExitAsync()
    {
        using AsyncLock asyncLock = new();
        TaskCompletionSource childStarted = NewSignal();
        TaskCompletionSource continueChild = NewSignal();
        Task? staleChild = null;

        await asyncLock.RunTaskAsync(async ct =>
        {
            staleChild = Task.Run(async () =>
            {
                childStarted.SetResult();
                await continueChild.Task;
                await asyncLock.RunAsync(() => { }, TestContext.CancellationToken);
            }, TestContext.CancellationToken);

            await childStarted.Task;
        }, TestContext.CancellationToken);

        continueChild.SetResult();
        await CaptureExceptionAsync<AsyncLockUsageException>(() => staleChild!);
    }

    [TestMethod]
    public async Task TestSuppressedExecutionContext_DoesNotInheritReentrantOwnershipAsync()
    {
        using AsyncLock asyncLock = new();
        TaskCompletionSource childStarted = NewSignal();
        Task? child = null;
        bool childRan = false;

        await asyncLock.RunTaskAsync(async ct =>
        {
            using (ExecutionContext.SuppressFlow())
            {
                child = Task.Run(async () =>
                {
                    childStarted.SetResult();
                    await asyncLock.RunAsync(() => childRan = true, TestContext.CancellationToken);
                }, TestContext.CancellationToken);
            }

            await childStarted.Task;
            Assert.IsFalse(child!.IsCompleted);
        }, TestContext.CancellationToken);

        await child!;
        Assert.IsTrue(childRan);
    }

    [TestMethod]
    public void TestDisposeWithoutUsers_PhysicallyDisposesInline()
    {
        using AsyncLock asyncLock = new();
        CancellationTokenSource internalCts = GetCancellationSource(asyncLock);

        asyncLock.Dispose();

        Assert.IsTrue(IsDisposed(internalCts));
        Assert.AreEqual(0, GetResourceUsers(asyncLock));
        asyncLock.Dispose();
    }

    [TestMethod]
    public async Task TestDisposeWithHolder_DefersPhysicalDisposalUntilHolderExitsAsync()
    {
        using AsyncLock asyncLock = new();
        CancellationTokenSource internalCts = GetCancellationSource(asyncLock);
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();

        Task holder = asyncLock.RunTaskAsync(async ct =>
        {
            entered.SetResult();
            await release.Task;
        }, TestContext.CancellationToken);

        await entered.Task;
        asyncLock.Dispose();

        Assert.IsFalse(IsDisposed(internalCts));
        Assert.AreEqual(1, GetResourceUsers(asyncLock));

        release.SetResult();
        await holder;

        Assert.IsTrue(IsDisposed(internalCts));
        Assert.AreEqual(0, GetResourceUsers(asyncLock));
    }

    [TestMethod]
    public async Task TestDisposeWithWaiter_WakesWaiterAndWaitsForAllResourceUsersToExitAsync()
    {
        using AsyncLock asyncLock = new();
        CancellationTokenSource internalCts = GetCancellationSource(asyncLock);
        TaskCompletionSource holderEntered = NewSignal();
        TaskCompletionSource releaseHolder = NewSignal();

        Task holder = asyncLock.RunTaskAsync(async ct =>
        {
            holderEntered.SetResult();
            await releaseHolder.Task;
        }, TestContext.CancellationToken);

        await holderEntered.Task;
        Task waiter = asyncLock.RunAsync(() => { }, TestContext.CancellationToken);
        await WaitForResourceUsersAsync(asyncLock, expected: 2);

        asyncLock.Dispose();
        await CaptureExceptionAsync<LockDisposedException>(() => waiter);

        Assert.IsFalse(IsDisposed(internalCts));
        Assert.AreEqual(1, GetResourceUsers(asyncLock));

        releaseHolder.SetResult();
        await holder;

        Assert.IsTrue(IsDisposed(internalCts));
    }

    [TestMethod]
    public async Task TestLastExitingWaiter_FinalizesAfterHolderHasAlreadyExitedAsync()
    {
        using AsyncLock asyncLock = new();
        CancellationTokenSource internalCts = GetCancellationSource(asyncLock);
        TaskCompletionSource holderEntered = NewSignal();
        TaskCompletionSource releaseHolder = NewSignal();
        TaskCompletionSource waiterStarted = NewSignal();
        using ManualResetEventSlim allowWaiterPump = new(initialState: false);

        Task holder = asyncLock.RunTaskAsync(async ct =>
        {
            holderEntered.SetResult();
            await releaseHolder.Task;
        }, TestContext.CancellationToken);
        await holderEntered.Task;

        Task waiter = StartWaiterOnPausedSynchronizationContext(asyncLock, waiterStarted, allowWaiterPump);
        await waiterStarted.Task;
        await WaitForResourceUsersAsync(asyncLock, expected: 2);

        asyncLock.Dispose();
        releaseHolder.SetResult();
        await holder;

        Assert.AreEqual(1, GetResourceUsers(asyncLock));
        Assert.IsFalse(IsDisposed(internalCts));

        allowWaiterPump.Set();
        await CaptureExceptionAsync<LockDisposedException>(() => waiter);

        Assert.AreEqual(0, GetResourceUsers(asyncLock));
        Assert.IsTrue(IsDisposed(internalCts));
    }

    [TestMethod]
    public async Task TestDisposeOnSingleThreadedTaskScheduler_DoesNotWaitForCapturedWaiterContinuationAsync()
    {
        using SingleThreadTaskScheduler scheduler = new();
        using AsyncLock asyncLock = new();
        TaskCompletionSource holderEntered = NewSignal();
        TaskCompletionSource releaseHolder = NewSignal();
        TaskCompletionSource disposeReturned = NewSignal();

        Task holder = Task.Run(() => asyncLock.RunTaskAsync(async ct =>
        {
            holderEntered.SetResult();
            await releaseHolder.Task;
        }, TestContext.CancellationToken), TestContext.CancellationToken);
        await holderEntered.Task;

        Task scenario = Task.Factory.StartNew(async () =>
        {
            Task waiter = asyncLock.RunAsync(() => { }, TestContext.CancellationToken);
            asyncLock.Dispose();
            disposeReturned.SetResult();
            releaseHolder.SetResult();
            await CaptureExceptionAsync<LockDisposedException>(() => waiter);
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler).Unwrap();

        await disposeReturned.Task.WaitAsync(s_testTimeout, TestContext.CancellationToken);
        await scenario.WaitAsync(s_testTimeout, TestContext.CancellationToken);
        await holder;
    }

    [TestMethod]
    public async Task TestDisposeOnSingleThreadedSynchronizationContext_DoesNotWaitForWaiterContinuationAsync()
    {
        using AsyncLock asyncLock = new();
        TaskCompletionSource holderEntered = NewSignal();
        TaskCompletionSource releaseHolder = NewSignal();

        Task holder = Task.Run(() => asyncLock.RunTaskAsync(async ct =>
        {
            holderEntered.SetResult();
            await releaseHolder.Task;
        }, TestContext.CancellationToken), TestContext.CancellationToken);
        await holderEntered.Task;

        Task scenario = RunOnDedicatedSynchronizationContext(async () =>
        {
            Task waiter = asyncLock.RunAsync(() => { }, TestContext.CancellationToken);
            asyncLock.Dispose();
            releaseHolder.SetResult();
            await CaptureExceptionAsync<LockDisposedException>(() => waiter);
        });

        await scenario.WaitAsync(s_testTimeout, TestContext.CancellationToken);
        await holder;
    }

    [TestMethod]
    public async Task TestCallerCancellationWhileWaiting_PreservesCallerTokenAsync()
    {
        using AsyncLock asyncLock = new();
        TaskCompletionSource holderEntered = NewSignal();
        TaskCompletionSource releaseHolder = NewSignal();
        using CancellationTokenSource callerCts = new();

        Task holder = asyncLock.RunTaskAsync(async ct =>
        {
            holderEntered.SetResult();
            await releaseHolder.Task;
        }, TestContext.CancellationToken);
        await holderEntered.Task;

        Task waiter = asyncLock.RunAsync(() => { }, callerCts.Token);
        await WaitForResourceUsersAsync(asyncLock, expected: 2);
        await callerCts.CancelAsync();

        OperationCanceledException exception = await CaptureExceptionAsync<OperationCanceledException>(() => waiter);
        Assert.AreEqual(callerCts.Token, exception.CancellationToken);

        releaseHolder.SetResult();
        await holder;
    }

    [TestMethod]
    public async Task TestRunAsync_DisposalBeforeExecutionThrowsLockDisposedExceptionAsync()
    {
        using AsyncLock asyncLock = new();
        asyncLock.Dispose();

        await CaptureExceptionAsync<LockDisposedException>(async () => await asyncLock.RunAsync(() => { }, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task TestTryRunAsync_DisposalReturnsSkippedAsync()
    {
        using AsyncLock asyncLock = new();
        asyncLock.Dispose();

        AsyncLockResult result = await asyncLock.TryRunAsync(() => { }, TestContext.CancellationToken);
        AsyncLockResult<int> genericResult = await asyncLock.TryRunAsync(() => 42, TestContext.CancellationToken);

        Assert.IsFalse(result.TaskExecuted);
        Assert.IsFalse(genericResult.TaskExecuted);
    }

    [TestMethod]
    public async Task TestTryRunAsync_CallerLockDisposedExceptionPropagatesAsync()
    {
        using AsyncLock asyncLock = new();

        LockDisposedException expected = new(objectName: null, message: "caller failure");
        LockDisposedException exception = await CaptureExceptionAsync<LockDisposedException>(async () =>
            await asyncLock.TryRunAsync(() => throw expected, TestContext.CancellationToken));

        Assert.IsTrue(ReferenceEquals(expected, exception));
    }

    [TestMethod]
    public async Task TestTryRunTaskAsync_CallerExceptionPropagatesAsync()
    {
        using AsyncLock asyncLock = new();

        InvalidOperationException expected = new("caller failure");
        InvalidOperationException exception = await CaptureExceptionAsync<InvalidOperationException>(async () =>
            await asyncLock.TryRunTaskAsync(_ => Task.FromException(expected), TestContext.CancellationToken));

        Assert.IsTrue(ReferenceEquals(expected, exception));
    }

    [TestMethod]
    public async Task TestReentrantAcquisitionAfterDispose_IsRejectedWhileCurrentHolderMayFinishAsync()
    {
        using AsyncLock asyncLock = new();
        bool outerFinished = false;

        await asyncLock.RunTaskAsync(async ct =>
        {
            asyncLock.Dispose();
            await CaptureExceptionAsync<LockDisposedException>(async () => await asyncLock.RunAsync(() => { }, TestContext.CancellationToken));
            outerFinished = true;
        }, TestContext.CancellationToken);

        Assert.IsTrue(outerFinished);
        Assert.IsTrue(IsDisposed(GetCancellationSource(asyncLock)));
    }

    [TestMethod]
    public async Task TestConcurrentDispose_IsIdempotentAsync()
    {
        using AsyncLock asyncLock = new();

        Task[] disposers = [.. Enumerable.Range(0, 32).Select(_ => Task.Run(asyncLock.Dispose, TestContext.CancellationToken))];

        await Task.WhenAll(disposers);
        Assert.IsTrue(IsDisposed(GetCancellationSource(asyncLock)));
    }

    [TestMethod]
    public async Task TestConcurrentAcquisitionAndDispose_DoNotLeakObjectDisposedExceptionAsync()
    {
        const int ITERATIONS = 256;

        for (int i = 0; i < ITERATIONS; i++)
        {
            using AsyncLock asyncLock = new();
            TaskCompletionSource start = NewSignal();

            Task<Exception?> runner = Task.Run(async () =>
            {
                await start.Task;
                try
                {
                    await asyncLock.RunAsync(() => { }, TestContext.CancellationToken);
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });

            Task disposer = Task.Run(async () =>
            {
                await start.Task;
                asyncLock.Dispose();
            }, TestContext.CancellationToken);

            start.SetResult();
            Exception? exception = await runner;
            await disposer;

            Assert.IsTrue(exception is null or LockDisposedException,
                $"Unexpected acquisition/disposal race exception: {exception?.GetType().FullName}");
        }
    }

    [TestMethod]
    public async Task TestConcurrentSiblingReentrancyRace_OnlyOneChildCanEnterAsync()
    {
        const int ITERATIONS = 64;

        for (int i = 0; i < ITERATIONS; i++)
        {
            using AsyncLock asyncLock = new();
            TaskCompletionSource start = NewSignal();
            TaskCompletionSource winnerEntered = NewSignal();
            TaskCompletionSource releaseWinner = NewSignal();

            await asyncLock.RunTaskAsync(async ct =>
            {
                Task<bool> RunChild() => Task.Run(async () =>
                {
                    await start.Task;
                    try
                    {
                        await asyncLock.RunTaskAsync(async childCt =>
                        {
                            winnerEntered.TrySetResult();
                            await releaseWinner.Task;
                        }, TestContext.CancellationToken);
                        return true;
                    }
                    catch (AsyncLockUsageException)
                    {
                        return false;
                    }
                }, CancellationToken.None);

                Task<bool> first = RunChild();
                Task<bool> second = RunChild();
                start.SetResult();

                await winnerEntered.Task.WaitAsync(s_testTimeout, CancellationToken.None);
                Task<bool> rejected = await Task.WhenAny(first, second).WaitAsync(s_testTimeout, CancellationToken.None);
                Assert.IsFalse(await rejected, "A competing sibling should be rejected while the winning child frame is active.");

                releaseWinner.SetResult();
                bool[] results = await Task.WhenAll(first, second);
                Assert.AreEqual(1, results.Count(static result => result));
                Assert.AreEqual(1, results.Count(static result => !result));
            }, TestContext.CancellationToken);

            Assert.AreEqual(0, GetResourceUsers(asyncLock));
            Assert.AreEqual(1, GetSemaphore(asyncLock).CurrentCount);
        }
    }

    [TestMethod]
    public async Task TestThreeLevelOutOfOrderExit_DeepestChildDrainsRequestedAncestorsAsync()
    {
        using AsyncLock asyncLock = new();
        TaskCompletionSource grandchildEntered = NewSignal();
        TaskCompletionSource allowChildExit = NewSignal();
        TaskCompletionSource releaseGrandchild = NewSignal();
        Task? child = null;
        Task? grandchild = null;

        Task outer = asyncLock.RunTaskAsync(async ct =>
        {
            child = Task.Run(() => asyncLock.RunTaskAsync(async childCt =>
            {
                grandchild = Task.Run(() => asyncLock.RunTaskAsync(async grandchildCt =>
                {
                    grandchildEntered.SetResult();
                    await releaseGrandchild.Task;
                }, TestContext.CancellationToken), CancellationToken.None);

                await grandchildEntered.Task;
                await allowChildExit.Task;
            }, TestContext.CancellationToken), CancellationToken.None);

            await grandchildEntered.Task;
        }, TestContext.CancellationToken);

        await CaptureExceptionAsync<AsyncLockUsageException>(() => outer);
        Assert.AreEqual(0, GetSemaphore(asyncLock).CurrentCount);
        Assert.AreEqual(1, GetResourceUsers(asyncLock));

        allowChildExit.SetResult();
        await CaptureExceptionAsync<AsyncLockUsageException>(() => child!);
        Assert.AreEqual(0, GetSemaphore(asyncLock).CurrentCount);
        Assert.AreEqual(1, GetResourceUsers(asyncLock));

        bool waiterRan = false;
        Task waiter = asyncLock.RunAsync(() => waiterRan = true, TestContext.CancellationToken);
        Assert.IsFalse(waiter.IsCompleted);
        Assert.AreEqual(2, GetResourceUsers(asyncLock));

        releaseGrandchild.SetResult();
        await grandchild!;
        await waiter.WaitAsync(s_testTimeout, TestContext.CancellationToken);

        Assert.IsTrue(waiterRan);
        Assert.AreEqual(0, GetResourceUsers(asyncLock));
        Assert.AreEqual(1, GetSemaphore(asyncLock).CurrentCount);
    }

    [TestMethod]
    public async Task TestDisposeWithManyWaitersAndDisposers_FinalizesAfterAllUsersExitAsync()
    {
        const int WAITER_COUNT = 64;
        const int DISPOSER_COUNT = 16;

        using AsyncLock asyncLock = new();
        CancellationTokenSource internalCts = GetCancellationSource(asyncLock);
        TaskCompletionSource holderEntered = NewSignal();
        TaskCompletionSource releaseHolder = NewSignal();

        Task holder = asyncLock.RunTaskAsync(async ct =>
        {
            holderEntered.SetResult();
            await releaseHolder.Task;
        }, TestContext.CancellationToken);
        await holderEntered.Task;

        Task<Exception?>[] waiters =
        [
            .. Enumerable.Range(0, WAITER_COUNT).Select(_ => Task.Run(async () =>
            {
                try
                {
                    await asyncLock.RunAsync(() => { }, TestContext.CancellationToken);
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }))
        ];

        await WaitForResourceUsersAsync(asyncLock, expected: WAITER_COUNT + 1);

        Task[] disposers = [.. Enumerable.Range(0, DISPOSER_COUNT).Select(_ => Task.Run(asyncLock.Dispose, TestContext.CancellationToken))];
        await Task.WhenAll(disposers);

        Exception?[] waiterResults = await Task.WhenAll(waiters);
        Assert.IsTrue(waiterResults.All(static exception => exception is LockDisposedException));
        Assert.AreEqual(1, GetResourceUsers(asyncLock));
        Assert.IsFalse(IsDisposed(internalCts));

        releaseHolder.SetResult();
        await holder;

        Assert.AreEqual(0, GetResourceUsers(asyncLock));
        Assert.IsTrue(IsDisposed(internalCts));
    }

    [TestMethod]
    public async Task TestCallerCancellationRacingDispose_ReportsOnlyCallerCancellationOrDisposalAsync()
    {
        const int ITERATIONS = 128;

        for (int i = 0; i < ITERATIONS; i++)
        {
            using AsyncLock asyncLock = new();
            using CancellationTokenSource callerCts = new();
            TaskCompletionSource holderEntered = NewSignal();
            TaskCompletionSource releaseHolder = NewSignal();
            TaskCompletionSource start = NewSignal();

            Task holder = asyncLock.RunTaskAsync(async ct =>
            {
                holderEntered.SetResult();
                await releaseHolder.Task;
            }, TestContext.CancellationToken);
            await holderEntered.Task;

            Task waiter = asyncLock.RunAsync(() => { }, callerCts.Token);
            await WaitForResourceUsersAsync(asyncLock, expected: 2);

            Task cancelCaller = Task.Run(async () =>
            {
                await start.Task;
                await callerCts.CancelAsync();
            }, TestContext.CancellationToken);
            Task dispose = Task.Run(async () =>
            {
                await start.Task;
                asyncLock.Dispose();
            }, TestContext.CancellationToken);

            start.SetResult();

            Exception? waiterFailure = null;
            try
            {
                await waiter;
            }
            catch (Exception exception)
            {
                waiterFailure = exception;
            }

            await Task.WhenAll(cancelCaller, dispose);

            Assert.IsNotNull(waiterFailure);
            Assert.IsTrue(waiterFailure is LockDisposedException or OperationCanceledException,
                $"Unexpected cancellation/disposal race exception: {waiterFailure?.GetType().FullName ?? "<none>"}");
            if (waiterFailure is OperationCanceledException cancellationException)
            {
                Assert.AreEqual(callerCts.Token, cancellationException.CancellationToken);
            }

            releaseHolder.SetResult();
            await holder;

            Assert.AreEqual(0, GetResourceUsers(asyncLock));
            Assert.IsTrue(IsDisposed(GetCancellationSource(asyncLock)));
        }
    }

    [TestMethod]
    public async Task TestConcurrentAcquirersAndDisposers_QuiesceWithoutLifetimeLeaksAsync()
    {
        const int ITERATIONS = 64;
        const int ACQUIRER_COUNT = 16;
        const int DISPOSER_COUNT = 4;

        for (int i = 0; i < ITERATIONS; i++)
        {
            using AsyncLock asyncLock = new();
            TaskCompletionSource start = NewSignal();

            Task<AsyncLockResult>[] runners =
            [
                .. Enumerable.Range(0, ACQUIRER_COUNT).Select(_ => Task.Run(async () =>
                {
                    await start.Task;
                    return await asyncLock.TryRunTaskAsync(async ct => await Task.Yield(), TestContext.CancellationToken);
                }, TestContext.CancellationToken))
            ];

            Task[] disposers =
            [
                .. Enumerable.Range(0, DISPOSER_COUNT).Select(_ => Task.Run(async () =>
                {
                    await start.Task;
                    asyncLock.Dispose();
                }, TestContext.CancellationToken))
            ];

            start.SetResult();
            await Task.WhenAll(runners.Cast<Task>().Concat(disposers));

            Assert.AreEqual(0, GetResourceUsers(asyncLock));
            Assert.IsTrue(IsDisposed(GetCancellationSource(asyncLock)));
        }
    }

    [TestMethod]
    public async Task TestDisposeInsideNestedReentrantDelegate_DefersPhysicalDisposalUntilRootExitAsync()
    {
        using AsyncLock asyncLock = new();
        CancellationTokenSource internalCts = GetCancellationSource(asyncLock);
        bool outerContinued = false;

        await asyncLock.RunTaskAsync(async ct =>
        {
            await asyncLock.RunTaskAsync(async nestedCt =>
            {
                asyncLock.Dispose();

                Assert.IsFalse(IsDisposed(internalCts));
                Assert.AreEqual(1, GetResourceUsers(asyncLock));
                await CaptureExceptionAsync<LockDisposedException>(async () => await asyncLock.RunAsync(() => { }, TestContext.CancellationToken));
            }, ct);

            Assert.IsFalse(IsDisposed(internalCts));
            Assert.AreEqual(1, GetResourceUsers(asyncLock));
            outerContinued = true;
        }, TestContext.CancellationToken);

        Assert.IsTrue(outerContinued);
        Assert.AreEqual(0, GetResourceUsers(asyncLock));
        Assert.IsTrue(IsDisposed(internalCts));
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<TException> CaptureExceptionAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        Assert.Fail($"Expected {typeof(TException).Name}.");
        throw new InvalidOperationException("Assert.Fail unexpectedly returned.");
    }

    private static async Task WaitForResourceUsersAsync(AsyncLock asyncLock, int expected)
    {
        DateTime deadline = DateTime.UtcNow + s_testTimeout;
        while (GetResourceUsers(asyncLock) != expected)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"Expected {expected} AsyncLock resource users, observed {GetResourceUsers(asyncLock)}.");
            }

            await Task.Yield();
        }
    }

    private static int GetResourceUsers(AsyncLock asyncLock) =>
        (int)GetField("_resourceUsers").GetValue(asyncLock)!;

    private static SemaphoreSlim GetSemaphore(AsyncLock asyncLock) =>
        (SemaphoreSlim)GetField("_semaphore").GetValue(asyncLock)!;

    private static CancellationTokenSource GetCancellationSource(AsyncLock asyncLock) =>
        (CancellationTokenSource)GetField("_disposalCancellationSource").GetValue(asyncLock)!;

    private static FieldInfo GetField(string name) =>
        typeof(AsyncLock).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"AsyncLock field '{name}' was not found.");

    private static bool IsDisposed(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            _ = cancellationTokenSource.Token;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static Task StartWaiterOnPausedSynchronizationContext(
        AsyncLock asyncLock,
        TaskCompletionSource waiterStarted,
        ManualResetEventSlim allowPump)
    {
        TaskCompletionSource completion = NewSignal();
        Thread thread = new(() =>
        {
            SingleThreadSynchronizationContext context = new();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                Task waiter = asyncLock.RunAsync(() => { });
                waiterStarted.SetResult();
                allowPump.Wait();
                context.Run(waiter);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
                context.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "AsyncLockTests.PausedSyncContext"
        };
        thread.Start();
        return completion.Task;
    }

    private static Task RunOnDedicatedSynchronizationContext(Func<Task> action)
    {
        TaskCompletionSource completion = NewSignal();
        Thread thread = new(() =>
        {
            SingleThreadSynchronizationContext context = new();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                context.Run(action);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
                context.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "AsyncLockTests.SyncContext"
        };
        thread.Start();
        return completion.Task;
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public void Run(Task task)
        {
            _ = task.ContinueWith(
                static (_, state) => ((SingleThreadSynchronizationContext)state!)._queue.CompleteAdding(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }

            task.GetAwaiter().GetResult();
        }

        public void Run(Func<Task> action)
        {
            Exception? failure = null;
            Post(async _ =>
            {
                try
                {
                    await action();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    _queue.CompleteAdding();
                }
            }, state: null);

            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        public void Dispose() => _queue.Dispose();
    }

    private sealed class SingleThreadTaskScheduler : TaskScheduler, IDisposable
    {
        private readonly BlockingCollection<Task> _tasks = [];
        private readonly Thread _thread;

        public SingleThreadTaskScheduler()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "AsyncLockTests.TaskScheduler"
            };
            _thread.Start();
        }

        protected override IEnumerable<Task>? GetScheduledTasks() => _tasks.ToArray();

        protected override void QueueTask(Task task) => _tasks.Add(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        public void Dispose()
        {
            _tasks.CompleteAdding();
            bool joined = _thread.Join(s_testTimeout);
            _tasks.Dispose();
            if (!joined)
            {
                throw new TimeoutException("The single-threaded test scheduler did not stop.");
            }
        }

        private void Run()
        {
            foreach (Task task in _tasks.GetConsumingEnumerable())
            {
                TryExecuteTask(task);
            }
        }
    }
}
