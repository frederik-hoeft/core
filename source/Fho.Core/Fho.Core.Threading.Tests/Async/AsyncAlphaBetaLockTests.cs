using Fho.Core.Threading.Async;
using Fho.Core.Threading.Exceptions;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Fho.Core.Threading.Tests.Async;

[TestClass]
public sealed class AsyncAlphaBetaLockTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_pendingWindow = TimeSpan.FromMilliseconds(150);

    private static async Task WithTimeout(Task task)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(s_timeout));
        Assert.AreSame(task, completed, "Operation timed out (possible deadlock).");
        await task;
    }

    private static async Task<TResult> WithTimeout<TResult>(Task<TResult> task)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(s_timeout));
        Assert.AreSame(task, completed, "Operation timed out (possible deadlock).");
        return await task;
    }

    private static async Task AssertPendingAsync(Task task)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(s_pendingWindow));
        Assert.AreNotSame(task, completed, "Task completed while it was expected to remain pending.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.IsTrue(stopwatch.Elapsed < s_timeout, "Condition timed out.");
            await Task.Delay(1);
        }
    }

    [TestMethod]
    public void PublicApi_DoesNotExposeManualAcquireRelease()
    {
        string[] names = typeof(AsyncAlphaBetaLock).GetMethods().Select(method => method.Name).ToArray();
        Assert.IsFalse(names.Any(name => name.StartsWith("Acquire", StringComparison.Ordinal)));
        Assert.IsFalse(names.Any(name => name.StartsWith("Release", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SameGroupOperations_ExecuteConcurrently()
    {
        using AsyncAlphaBetaLock gate = new();
        TaskCompletionSource bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int inside = 0;

        Task HoldAsync() => gate.RunAlphaTaskAsync(async _ =>
        {
            if (Interlocked.Increment(ref inside) == 2)
            {
                bothEntered.TrySetResult();
            }
            await release.Task;
            Interlocked.Decrement(ref inside);
        });

        Task first = HoldAsync();
        Task second = HoldAsync();
        await WithTimeout(bothEntered.Task);
        Assert.AreEqual(2, gate.CurrentAlphaCount);
        release.TrySetResult();
        await WithTimeout(Task.WhenAll(first, second));
    }

    [TestMethod]
    public async Task AlphaAndBeta_NeverOverlap()
    {
        using AsyncAlphaBetaLock gate = new();
        TaskCompletionSource alphaEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseAlpha = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int alphaInside = 0;
        int violations = 0;

        Task alpha = gate.RunAlphaTaskAsync(async _ =>
        {
            Interlocked.Increment(ref alphaInside);
            alphaEntered.TrySetResult();
            await releaseAlpha.Task;
            Interlocked.Decrement(ref alphaInside);
        });
        await WithTimeout(alphaEntered.Task);

        Task beta = gate.RunBetaAsync(() =>
        {
            if (Volatile.Read(ref alphaInside) != 0)
            {
                Interlocked.Increment(ref violations);
            }
        });
        await AssertPendingAsync(beta);
        releaseAlpha.TrySetResult();
        await WithTimeout(Task.WhenAll(alpha, beta));
        Assert.AreEqual(0, violations);
    }

    [TestMethod]
    public async Task WaitingAlpha_BlocksNewBeta_AndRunsFirst()
    {
        using AsyncAlphaBetaLock gate = new();
        TaskCompletionSource betaEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseBeta = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource alphaEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseAlpha = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task currentBeta = gate.RunBetaTaskAsync(async _ =>
        {
            betaEntered.TrySetResult();
            await releaseBeta.Task;
        });
        await WithTimeout(betaEntered.Task);

        Task alpha = gate.RunAlphaTaskAsync(async _ =>
        {
            alphaEntered.TrySetResult();
            await releaseAlpha.Task;
        });
        await WaitUntilAsync(() => gate.WaitingAlphaCount == 1);

        Task nextBeta = gate.RunBetaAsync(() => { });
        await WaitUntilAsync(() => gate.WaitingBetaCount == 1);
        releaseBeta.TrySetResult();
        await WithTimeout(currentBeta);
        await WithTimeout(alphaEntered.Task);
        await AssertPendingAsync(nextBeta);
        releaseAlpha.TrySetResult();
        await WithTimeout(Task.WhenAll(alpha, nextBeta));
    }

    [TestMethod]
    public async Task Beta_ReentersWhileAlphaWaits()
    {
        using AsyncAlphaBetaLock gate = new();
        TaskCompletionSource betaEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reenter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseAlpha = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool nestedExecuted = false;

        Task beta = gate.RunBetaTaskAsync(async ct =>
        {
            betaEntered.TrySetResult();
            await reenter.Task;
            await gate.RunBetaAsync(() => { nestedExecuted = true; }, ct);
        });
        await WithTimeout(betaEntered.Task);
        Task alpha = gate.RunAlphaTaskAsync(async _ => await releaseAlpha.Task);
        await WaitUntilAsync(() => gate.WaitingAlphaCount == 1);
        reenter.TrySetResult();
        await WithTimeout(beta);
        Assert.IsTrue(nestedExecuted);
        releaseAlpha.TrySetResult();
        await WithTimeout(alpha);
    }

    [TestMethod]
    public async Task EscapedNestedRun_KeepsOwnershipUntilNestedCompletion()
    {
        using AsyncAlphaBetaLock gate = new();
        TaskCompletionSource nestedEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseNested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task nested = Task.CompletedTask;

        await gate.RunBetaAsync(() =>
        {
            nested = gate.RunBetaTaskAsync(async _ =>
            {
                nestedEntered.TrySetResult();
                await releaseNested.Task;
            });
        });

        await WithTimeout(nestedEntered.Task);
        Assert.AreEqual(1, gate.CurrentBetaCount);
        Task alpha = gate.RunAlphaAsync(() => { });
        await AssertPendingAsync(alpha);
        releaseNested.TrySetResult();
        await WithTimeout(Task.WhenAll(nested, alpha));
    }

    [TestMethod]
    public async Task StaleInheritedContext_CannotReenterClosedGeneration()
    {
        using AsyncAlphaBetaLock gate = new();
        TaskCompletionSource allowChild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource childAttempting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource childEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task child = Task.CompletedTask;

        await gate.RunBetaAsync(() =>
        {
            child = Task.Run(async () =>
            {
                await allowChild.Task;
                childAttempting.TrySetResult();
                await gate.RunBetaAsync(() => { childEntered.TrySetResult(); });
            });
        });

        TaskCompletionSource alphaEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseAlpha = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task alpha = gate.RunAlphaTaskAsync(async _ =>
        {
            alphaEntered.TrySetResult();
            await releaseAlpha.Task;
        });
        await WithTimeout(alphaEntered.Task);
        allowChild.TrySetResult();
        await WithTimeout(childAttempting.Task);
        await AssertPendingAsync(childEntered.Task);
        releaseAlpha.TrySetResult();
        await WithTimeout(Task.WhenAll(alpha, child));
    }

    [TestMethod]
    public async Task SuppressedExecutionContext_DoesNotReceiveReentrancy()
    {
        using AsyncAlphaBetaLock gate = new();
        Task independentBeta = Task.CompletedTask;
        Task alpha = Task.CompletedTask;
        TaskCompletionSource releaseAlpha = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await gate.RunBetaTaskAsync(async _ =>
        {
            alpha = gate.RunAlphaTaskAsync(async _ => await releaseAlpha.Task);
            await WaitUntilAsync(() => gate.WaitingAlphaCount == 1);
            using (ExecutionContext.SuppressFlow())
            {
                independentBeta = Task.Run(() => gate.RunBetaAsync(() => { }));
            }
            await WaitUntilAsync(() => gate.WaitingBetaCount == 1);
            await AssertPendingAsync(independentBeta);
        });

        await AssertPendingAsync(independentBeta);
        releaseAlpha.TrySetResult();
        await WithTimeout(Task.WhenAll(alpha, independentBeta));
    }

    [TestMethod]
    public async Task CrossGroupReentrancy_Throws()
    {
        using AsyncAlphaBetaLock gate = new();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await gate.RunAlphaTaskAsync(async ct => await gate.RunBetaAsync(() => { }, ct)));
    }

    [TestMethod]
    public async Task PreCanceledReentrantCall_DoesNotExecute()
    {
        using AsyncAlphaBetaLock gate = new();
        bool executed = false;
        await gate.RunBetaTaskAsync(async _ =>
        {
            using CancellationTokenSource canceled = new();
            canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await gate.RunBetaAsync(() => { executed = true; }, canceled.Token));
        });
        Assert.IsFalse(executed);
    }

    [TestMethod]
    public async Task Cancellation_UnregistersWaiter()
    {
        using AsyncAlphaBetaLock gate = new();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource alphaEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseAlpha = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task alpha = gate.RunAlphaTaskAsync(async _ =>
        {
            alphaEntered.TrySetResult();
            await releaseAlpha.Task;
        });
        await WithTimeout(alphaEntered.Task);

        Task beta = gate.RunBetaAsync(() => { }, cancellation.Token);
        await WaitUntilAsync(() => gate.WaitingBetaCount == 1);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await beta);
        Assert.AreEqual(0, gate.WaitingBetaCount);
        releaseAlpha.TrySetResult();
        await WithTimeout(alpha);
    }

    [TestMethod]
    public async Task Dispose_WakesWaitersWithoutBlocking()
    {
        AsyncAlphaBetaLock gate = new();
        TaskCompletionSource alphaEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseAlpha = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task alpha = gate.RunAlphaTaskAsync(async _ =>
        {
            alphaEntered.TrySetResult();
            await releaseAlpha.Task;
        });
        await WithTimeout(alphaEntered.Task);
        Task beta = gate.RunBetaAsync(() => { });
        await WaitUntilAsync(() => gate.WaitingBetaCount == 1);

        Stopwatch stopwatch = Stopwatch.StartNew();
        gate.Dispose();
        stopwatch.Stop();
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        await Assert.ThrowsExactlyAsync<LockDisposedException>(async () => await WithTimeout(beta));
        releaseAlpha.TrySetResult();
        await WithTimeout(alpha);
    }

    [TestMethod]
    public async Task ActiveGeneration_CanReenterAfterDispose()
    {
        AsyncAlphaBetaLock gate = new();
        bool nestedExecuted = false;
        await gate.RunBetaTaskAsync(async ct =>
        {
            gate.Dispose();
            AsyncLockResult nested = await gate.TryRunBetaAsync(() => { nestedExecuted = true; }, ct);
            Assert.IsTrue(nested.TaskExecuted);
        });
        Assert.IsTrue(nestedExecuted);
        AsyncLockResult skipped = await gate.TryRunAlphaAsync(() => { });
        Assert.IsFalse(skipped.TaskExecuted);
    }

    [TestMethod]
    public async Task TryRun_DoesNotSwallowUserLockDisposedException()
    {
        using AsyncAlphaBetaLock gate = new();
        await Assert.ThrowsExactlyAsync<LockDisposedException>(async () =>
            await gate.TryRunAlphaAsync(() => { throw new LockDisposedException("user-code"); }));
    }

    [TestMethod]
    public async Task Dispose_OnSingleThreadedContext_DoesNotDeadlock()
    {
        await RunOnSingleThreadContextAsync(async () =>
        {
            AsyncAlphaBetaLock gate = new();
            TaskCompletionSource alphaEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource releaseAlpha = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task alpha = gate.RunAlphaTaskAsync(async _ =>
            {
                alphaEntered.TrySetResult();
                await releaseAlpha.Task;
            });
            await alphaEntered.Task;
            Task beta = gate.RunBetaAsync(() => { });
            await WaitUntilAsync(() => gate.WaitingBetaCount == 1);
            gate.Dispose();
            releaseAlpha.TrySetResult();
            await alpha;
            await Assert.ThrowsExactlyAsync<LockDisposedException>(async () => await beta);
        });
    }

    [TestMethod]
    public async Task HighContentionStress_PreservesExclusion()
    {
        using AsyncAlphaBetaLock gate = new();
        int alphaInside = 0;
        int betaInside = 0;
        int violations = 0;
        Task[] tasks = Enumerable.Range(0, 250).Select(index => index % 2 == 0
            ? gate.RunAlphaTaskAsync(async _ =>
            {
                Interlocked.Increment(ref alphaInside);
                if (Volatile.Read(ref betaInside) != 0) Interlocked.Increment(ref violations);
                await Task.Yield();
                Interlocked.Decrement(ref alphaInside);
            })
            : gate.RunBetaTaskAsync(async _ =>
            {
                Interlocked.Increment(ref betaInside);
                if (Volatile.Read(ref alphaInside) != 0) Interlocked.Increment(ref violations);
                await Task.Yield();
                Interlocked.Decrement(ref betaInside);
            })).ToArray();
        await WithTimeout(Task.WhenAll(tasks));
        Assert.AreEqual(0, violations);
    }

    private static async Task RunOnSingleThreadContextAsync(Func<Task> action)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            using SingleThreadSynchronizationContext context = new();
            SynchronizationContext.SetSynchronizationContext(context);
            context.Post(async _ =>
            {
                try
                {
                    await action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    context.Complete();
                }
            }, null);
            context.Run();
        }) { IsBackground = true };
        thread.Start();
        await WithTimeout(completion.Task);
        Assert.IsTrue(thread.Join(s_timeout));
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _queue.Add((callback, state));
        }

        public void Run()
        {
            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }

        public void Complete() => _queue.CompleteAdding();

        public void Dispose() => _queue.Dispose();
    }
}
