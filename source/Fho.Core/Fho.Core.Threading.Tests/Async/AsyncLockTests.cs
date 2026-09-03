using Fho.Core.Threading.Async;
using Fho.Core.Threading.Exceptions;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Fho.Core.Threading.Tests.Async;

[TestClass]
public sealed class AsyncLockTests
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(2);

    [TestMethod]
    public async Task RunTaskAsync_SerializesConcurrentCallers()
    {
        using AsyncLock asyncLock = new();
        int active = 0;
        bool overlapped = false;

        Task[] tasks = Enumerable.Range(0, 32)
            .Select(_ => asyncLock.RunTaskAsync(async ct =>
            {
                if (Interlocked.Increment(ref active) != 1)
                {
                    overlapped = true;
                }

                await Task.Yield();
                Interlocked.Decrement(ref active);
            }))
            .ToArray();

        await Task.WhenAll(tasks);
        Assert.IsFalse(overlapped);
        Assert.IsFalse(asyncLock.IsHeld);
    }

    [TestMethod]
    public async Task RunTaskAsync_ReentersAcrossAwaitBoundaries()
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
        });

        Assert.AreEqual(2, nestedRuns);
        Assert.IsFalse(asyncLock.IsHeld);
    }

    [TestMethod]
    public async Task RunMethods_ReturnDelegateResults()
    {
        using AsyncLock asyncLock = new();

        int synchronousResult = await asyncLock.RunAsync(() => 42);
        int asynchronousResult = await asyncLock.RunTaskAsync(async ct =>
        {
            await Task.Yield();
            return 7;
        });

        Assert.AreEqual(42, synchronousResult);
        Assert.AreEqual(7, asynchronousResult);
    }

    [TestMethod]
    public async Task NullDelegates_AreRejectedBeforeAcquisition()
    {
        using AsyncLock asyncLock = new();

        await CaptureExceptionAsync<ArgumentNullException>(async () => await asyncLock.RunAsync((Action)null!));
        await CaptureExceptionAsync<ArgumentNullException>(async () => await asyncLock.RunTaskAsync((Func<CancellationToken, Task>)null!));
        await CaptureExceptionAsync<ArgumentNullException>(async () => await asyncLock.TryRunAsync((Action)null!));
        await CaptureExceptionAsync<ArgumentNullException>(async () => await asyncLock.TryRunTaskAsync((Func<CancellationToken, Task>)null!));

        Assert.IsFalse(asyncLock.IsHeld);
        Assert.AreEqual(0, GetResourceUsers(asyncLock));
    }

    [TestMethod]
    public async Task ConcurrentSiblingReentrancy_IsRejected()
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
            }));

            await childEntered.Task;

            Task secondChild = Task.Run(() => asyncLock.RunAsync(() => { }));
            await CaptureExceptionAsync<AsyncLockUsageException>(() => secondChild);

            releaseChild.SetResult();
            await firstChild;
        });

        Assert.IsFalse(asyncLock.IsHeld);
    }

    [TestMethod]
    public async Task ParentReentrancyWhileChildFrameIsActive_IsRejected()
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
            }));

            await childEntered.Task;
            await CaptureExceptionAsync<AsyncLockUsageException>(async () => await asyncLock.RunAsync(() => { }));

            releaseChild.SetResult();
            await child;
        });
    }

    [TestMethod]
    public async Task PoisonedOwnershipContext_RejectsFurtherReentrancy()
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
            }));

            await childEntered.Task;
            await CaptureExceptionAsync<AsyncLockUsageException>(async () => await asyncLock.RunAsync(() => { }));

            releaseChild.SetResult();
            await child;

            await CaptureExceptionAsync<AsyncLockUsageException>(async () => await asyncLock.RunAsync(() => { }));
        });
    }

    [TestMethod]
    public async Task ParentExitWithActiveOrphan_ThrowsWithoutReleasingSemaphore()
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
            }));

            await childEntered.Task;
        });

        await CaptureExceptionAsync<AsyncLockUsageException>(() => outer);
        Assert.AreEqual(0, GetSemaphore(asyncLock).CurrentCount);
        Assert.AreEqual(1, GetResourceUsers(asyncLock));

        bool waiterRan = false;
        Task waiter = asyncLock.RunAsync(() => waiterRan = true);
        Assert.IsFalse(waiter.IsCompleted);

        releaseChild.SetResult();
        await child!;
        await waiter;

        Assert.IsTrue(waiterRan);
        Assert.AreEqual(0, GetResourceUsers(asyncLock));
    }

    [TestMethod]
    public async Task StaleInheritedOwnership_IsRejectedAfterRootExit()
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
                await asyncLock.RunAsync(() => { });
            });

            await childStarted.Task;
        });

        continueChild.SetResult();
        await CaptureExceptionAsync<AsyncLockUsageException>(() => staleChild!);
    }

    [TestMethod]
    public async Task SuppressedExecutionContext_DoesNotInheritReentrantOwnership()
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
                    await asyncLock.RunAsync(() => childRan = true);
                });
            }

            await childStarted.Task;
            Assert.IsFalse(child!.IsCompleted);
        });

        await child!;
        Assert.IsTrue(childRan);
    }

    [TestMethod]
    public void DisposeWithoutUsers_PhysicallyDisposesInline()
    {
        using AsyncLock asyncLock = new();
        CancellationTokenSource internalCts = GetCancellationSource(asyncLock);

        asyncLock.Dispose();

        Assert.IsTrue(IsDisposed(internalCts));
        Assert.AreEqual(0, GetResourceUsers(asyncLock));
        asyncLock.Dispose();
    }

    [TestMethod]
    public async Task DisposeWithHolder_DefersPhysicalDisposalUntilHolderExits()
    {
        using AsyncLock asyncLock = new();
        CancellationTokenSource internalCts = GetCancellationSource(asyncLock);
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();

        Task holder = asyncLock.RunTaskAsync(async ct =>
        {
            entered.SetResult();
            await release.Task;
        });

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
    public async Task DisposeWithWaiter_WakesWaiterAndWaitsForAllResourceUsersToExit()
    {
        using AsyncLock asyncLock = new();
        CancellationTokenSource internalCts = GetCancellationSource(asyncLock);
        TaskCompletionSource holderEntered = NewSignal();
        TaskCompletionSource releaseHolder = NewSignal();

        Task holder = asyncLock.RunTaskAsync(async ct =>
        {
            holderEntered.SetResult();
            await releaseHolder.Task;
        });

        await holderEntered.Task;
        Task waiter = asyncLock.RunAsync(() => { });
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
    public async Task LastExitingWaiter_FinalizesAfterHolderHasAlreadyExited()
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
        });
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
    public async Task DisposeOnSingleThreadedTaskScheduler_DoesNotWaitForCapturedWaiterContinuation()
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
        }));
        await holderEntered.Task;

        Task scenario = Task.Factory.StartNew(async () =>
        {
            Task waiter = asyncLock.RunAsync(() => { });
            asyncLock.Dispose();
            disposeReturned.SetResult();
            releaseHolder.SetResult();
            await CaptureExceptionAsync<LockDisposedException>(() => waiter);
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler).Unwrap();

        await disposeReturned.Task.WaitAsync(s_testTimeout);
        await scenario.WaitAsync(s_testTimeout);
        await holder;
    }

    [TestMethod]
    public async Task DisposeOnSingleThreadedSynchronizationContext_DoesNotWaitForWaiterContinuation()
    {
        using AsyncLock asyncLock = new();
        TaskCompletionSource holderEntered = NewSignal();
        TaskCompletionSource releaseHolder = NewSignal();

        Task holder = Task.Run(() => asyncLock.RunTaskAsync(async ct =>
        {
            holderEntered.SetResult();
            await releaseHolder.Task;
        }));
        await holderEntered.Task;

        Task scenario = RunOnDedicatedSynchronizationContext(async () =>
        {
            Task waiter = asyncLock.RunAsync(() => { });
            asyncLock.Dispose();
            releaseHolder.SetResult();
            await CaptureExceptionAsync<LockDisposedException>(() => waiter);
        });

        await scenario.WaitAsync(s_testTimeout);
        await holder;
    }

    [TestMethod]
    public async Task CallerCancellationWhileWaiting_PreservesCallerToken()
    {
        using AsyncLock asyncLock = new();
        TaskCompletionSource holderEntered = NewSignal();
        TaskCompletionSource releaseHolder = NewSignal();
        using CancellationTokenSource callerCts = new();

        Task holder = asyncLock.RunTaskAsync(async ct =>
        {
            holderEntered.SetResult();
            await releaseHolder.Task;
        });
        await holderEntered.Task;

        Task waiter = asyncLock.RunAsync(() => { }, callerCts.Token);
        await WaitForResourceUsersAsync(asyncLock, expected: 2);
        callerCts.Cancel();

        OperationCanceledException exception = await CaptureExceptionAsync<OperationCanceledException>(() => waiter);
        Assert.AreEqual(callerCts.Token, exception.CancellationToken);

        releaseHolder.SetResult();
        await holder;
    }

    [TestMethod]
    public async Task RunAsync_DisposalBeforeExecutionThrowsLockDisposedException()
    {
        using AsyncLock asyncLock = new();
        asyncLock.Dispose();

        await CaptureExceptionAsync<LockDisposedException>(async () => await asyncLock.RunAsync(() => { }));
    }

    [TestMethod]
    public async Task TryRunAsync_DisposalReturnsSkipped()
    {
        using AsyncLock asyncLock = new();
        asyncLock.Dispose();

        AsyncLockResult result = await asyncLock.TryRunAsync(() => { });
        AsyncLockResult<int> genericResult = await asyncLock.TryRunAsync(() => 42);

        Assert.IsFalse(result.TaskExecuted);
        Assert.IsFalse(genericResult.TaskExecuted);
    }

    [TestMethod]
    public async Task TryRunAsync_CallerLockDisposedExceptionPropagates()
    {
        using AsyncLock asyncLock = new();

        LockDisposedException expected = new(objectName: null, message: "caller failure");
        LockDisposedException exception = await CaptureExceptionAsync<LockDisposedException>(async () =>
            await asyncLock.TryRunAsync((Action)(() => throw expected)));

        Assert.IsTrue(ReferenceEquals(expected, exception));
    }

    [TestMethod]
    public async Task TryRunTaskAsync_CallerExceptionPropagates()
    {
        using AsyncLock asyncLock = new();

        InvalidOperationException expected = new("caller failure");
        InvalidOperationException exception = await CaptureExceptionAsync<InvalidOperationException>(async () =>
            await asyncLock.TryRunTaskAsync(_ => Task.FromException(expected)));

        Assert.IsTrue(ReferenceEquals(expected, exception));
    }

    [TestMethod]
    public async Task ReentrantAcquisitionAfterDispose_IsRejectedWhileCurrentHolderMayFinish()
    {
        using AsyncLock asyncLock = new();
        bool outerFinished = false;

        await asyncLock.RunTaskAsync(async ct =>
        {
            asyncLock.Dispose();
            await CaptureExceptionAsync<LockDisposedException>(async () => await asyncLock.RunAsync(() => { }));
            outerFinished = true;
        });

        Assert.IsTrue(outerFinished);
        Assert.IsTrue(IsDisposed(GetCancellationSource(asyncLock)));
    }

    [TestMethod]
    public async Task ConcurrentDispose_IsIdempotent()
    {
        using AsyncLock asyncLock = new();

        Task[] disposers = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(asyncLock.Dispose))
            .ToArray();

        await Task.WhenAll(disposers);
        Assert.IsTrue(IsDisposed(GetCancellationSource(asyncLock)));
    }

    [TestMethod]
    public async Task ConcurrentAcquisitionAndDispose_DoNotLeakObjectDisposedException()
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
                    await asyncLock.RunAsync(() => { });
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
            });

            start.SetResult();
            Exception? exception = await runner;
            await disposer;

            Assert.IsTrue(exception is null or LockDisposedException,
                $"Unexpected acquisition/disposal race exception: {exception?.GetType().FullName}");
        }
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
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

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
        private readonly BlockingCollection<Task> _tasks = new();
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
