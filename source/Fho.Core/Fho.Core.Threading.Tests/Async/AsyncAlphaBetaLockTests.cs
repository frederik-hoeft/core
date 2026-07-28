using Fho.Core.Threading.Async;
using Fho.Core.Threading.Exceptions;

namespace Fho.Core.Threading.Tests.Async;

[TestClass]
public sealed class AsyncAlphaBetaLockTests
{
    // Generous enough for slow CI, tight enough to fail deadlocks quickly.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_shortTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly string[] s_orderAlpha2Only = ["alpha2"];
    private static readonly string[] s_orderAlpha2ThenBeta = ["alpha2", "beta"];
    private static readonly int[] s_nestedDepths = [1, 2, 3, 2, 1];

    #region Helpers

    private static CancellationToken TimeoutToken() => new CancellationTokenSource(s_testTimeout).Token;

    private static async Task WithTimeout(Task task, TimeSpan? timeout = null)
    {
        TimeSpan t = timeout ?? s_testTimeout;
        Task winner = await Task.WhenAny(task, Task.Delay(t));
        if (winner != task)
        {
            Assert.Fail($"Operation timed out after {t.TotalMilliseconds}ms (possible deadlock).");
        }
        await task; // propagate exceptions
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan? timeout = null)
    {
        TimeSpan t = timeout ?? s_testTimeout;
        Task winner = await Task.WhenAny(task, Task.Delay(t));
        if (winner != task)
        {
            Assert.Fail($"Operation timed out after {t.TotalMilliseconds}ms (possible deadlock).");
        }
        return await task;
    }

    private static async Task AssertNeverCompletes(Task task, TimeSpan window)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(window));
        if (winner == task)
        {
            // If it completed with an exception, still a failure for "should not complete".
            if (task.IsFaulted)
            {
                Assert.Fail($"Task faulted unexpectedly: {task.Exception!.GetBaseException()}");
            }
            Assert.Fail("Task completed but was expected to remain pending.");
        }
    }

    #endregion Helpers

    #region Basic exclusive / concurrent

    [TestMethod]
    public async Task Alpha_Basic_RunAsync_Executes()
    {
        using AsyncAlphaBetaLock gate = new();
        int value = 0;
        await WithTimeout(gate.RunAlphaAsync(() => value = 42, TimeoutToken()));
        Assert.AreEqual(42, value);
        Assert.IsFalse(gate.IsAlphaHeld);
        Assert.AreEqual(0, gate.CurrentAlphaCount);
    }

    [TestMethod]
    public async Task Beta_Basic_RunAsync_Executes()
    {
        using AsyncAlphaBetaLock gate = new();
        int value = 0;
        await WithTimeout(gate.RunBetaAsync(() => value = 7, TimeoutToken()));
        Assert.AreEqual(7, value);
        Assert.IsFalse(gate.IsBetaHeld);
    }

    [TestMethod]
    public async Task Alpha_RunTaskAsync_WithResult()
    {
        using AsyncAlphaBetaLock gate = new();
        int result = await WithTimeout(gate.RunAlphaTaskAsync(async ct =>
        {
            await Task.Yield();
            Assert.IsTrue(gate.IsAlphaHeld);
            return 99;
        }, TimeoutToken()));
        Assert.AreEqual(99, result);
    }

    [TestMethod]
    public async Task ConcurrentAlphas_AreCompatible()
    {
        using AsyncAlphaBetaLock gate = new();
        using CancellationTokenSource cts = new(s_testTimeout);

        TaskCompletionSource bothInside = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int inside = 0;
        int maxInside = 0;

        async Task HoldAsync()
        {
            await gate.RunAlphaTaskAsync(async ct =>
            {
                int now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maxInside, now);
                if (now == 2)
                {
                    bothInside.TrySetResult();
                }
                // Stay until both observed concurrent, or timeout.
                await bothInside.Task.WaitAsync(ct);
                Interlocked.Decrement(ref inside);
            }, cts.Token);
        }

        await WithTimeout(Task.WhenAll(HoldAsync(), HoldAsync()));
        Assert.IsGreaterThanOrEqualTo(2, maxInside, $"Expected concurrent alphas, maxInside={maxInside}");
    }

    [TestMethod]
    public async Task ConcurrentBetas_AreCompatible()
    {
        using AsyncAlphaBetaLock gate = new();
        using CancellationTokenSource cts = new(s_testTimeout);

        TaskCompletionSource bothInside = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int inside = 0;
        int maxInside = 0;

        async Task HoldAsync()
        {
            await gate.RunBetaTaskAsync(async ct =>
            {
                int now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maxInside, now);
                if (now == 2)
                {
                    bothInside.TrySetResult();
                }
                await bothInside.Task.WaitAsync(ct);
                Interlocked.Decrement(ref inside);
            }, cts.Token);
        }

        await WithTimeout(Task.WhenAll(HoldAsync(), HoldAsync()));
        Assert.IsGreaterThanOrEqualTo(2, maxInside, $"Expected concurrent betas, maxInside={maxInside}");
    }

    [TestMethod]
    public async Task AlphaAndBeta_AreMutuallyExclusive()
    {
        using AsyncAlphaBetaLock gate = new();
        using CancellationTokenSource cts = new(s_testTimeout);

        TaskCompletionSource alphaHolds = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseAlpha = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int concurrent = 0;
        int maxConcurrent = 0;

        Task alpha = gate.RunAlphaTaskAsync(async ct =>
        {
            InterlockedMax(ref maxConcurrent, Interlocked.Increment(ref concurrent));
            alphaHolds.TrySetResult();
            await releaseAlpha.Task.WaitAsync(ct);
            Interlocked.Decrement(ref concurrent);
        }, cts.Token);

        await WithTimeout(alphaHolds.Task);

        Task betaStarted = gate.RunBetaAsync(() =>
        {
            InterlockedMax(ref maxConcurrent, Interlocked.Increment(ref concurrent));
            Interlocked.Decrement(ref concurrent);
        }, cts.Token);

        // Beta must not enter while alpha holds.
        await AssertNeverCompletes(betaStarted, s_shortTimeout);
        Assert.AreEqual(1, maxConcurrent);

        releaseAlpha.TrySetResult();
        await WithTimeout(Task.WhenAll(alpha, betaStarted));
        Assert.AreEqual(1, maxConcurrent, "Alpha and beta must never overlap");
    }

    #endregion Basic exclusive / concurrent

    #region Alpha precedence

    [TestMethod]
    public async Task WaitingAlpha_BlocksNewBeta_EvenWhenBetaHolds()
    {
        using AsyncAlphaBetaLock gate = new();
        using CancellationTokenSource cts = new(s_testTimeout);

        TaskCompletionSource betaHolds = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource alphaWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseBeta = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseAlpha = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // 1) Beta acquires and holds.
        Task beta = gate.RunBetaTaskAsync(async ct =>
        {
            betaHolds.TrySetResult();
            await releaseBeta.Task.WaitAsync(ct);
        }, cts.Token);
        await WithTimeout(betaHolds.Task);

        // 2) Alpha tries to enter — must wait behind beta.
        Task alpha = gate.RunAlphaTaskAsync(async ct =>
        {
            await releaseAlpha.Task.WaitAsync(ct);
        }, cts.Token);

        // Give alpha time to register as waiter.
        await Task.Delay(50);
        Assert.IsTrue(gate.WaitingAlphaCount >= 1 || !alpha.IsCompleted);

        // 3) New beta must NOT sneak in while alpha is waiting.
        Task newBeta = gate.RunBetaAsync(() => { }, cts.Token);
        await AssertNeverCompletes(newBeta, s_shortTimeout);

        // 4) Release outer beta → alpha should enter, newBeta still blocked.
        releaseBeta.TrySetResult();
        await WithTimeout(beta);

        // Wait until alpha is holding (no longer waiting).
        Assert.IsTrue(SpinWait.SpinUntil(() => gate.CurrentAlphaCount >= 1, s_testTimeout));
        await AssertNeverCompletes(newBeta, TimeSpan.FromMilliseconds(200));

        // 5) Release alpha → newBeta proceeds.
        releaseAlpha.TrySetResult();
        await WithTimeout(Task.WhenAll(alpha, newBeta));
    }

    [TestMethod]
    public async Task Alpha_TakesPrecedence_OverWaitingBeta_OnEmptyLock()
    {
        using AsyncAlphaBetaLock gate = new();
        using CancellationTokenSource cts = new(s_testTimeout);

        TaskCompletionSource alphaHolds = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseAlpha = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Hold with alpha so beta queues.
        Task holder = gate.RunAlphaTaskAsync(async ct =>
        {
            alphaHolds.TrySetResult();
            await releaseAlpha.Task.WaitAsync(ct);
        }, cts.Token);
        await WithTimeout(alphaHolds.Task);

        List<string> order = [];
        object orderLock = new();

        Task beta = gate.RunBetaAsync(() =>
        {
            lock (orderLock)
            {
                order.Add("beta");
            }
        }, cts.Token);

        // Ensure beta is waiting.
        Assert.IsTrue(SpinWait.SpinUntil(() => gate.WaitingBetaCount >= 1, s_testTimeout));

        // Second alpha should still enter (same group) before beta.
        Task alpha2 = gate.RunAlphaAsync(() =>
        {
            lock (orderLock)
            {
                order.Add("alpha2");
            }
        }, cts.Token);
        await WithTimeout(alpha2);

        lock (orderLock)
        {
            CollectionAssert.AreEqual(s_orderAlpha2Only, order.ToArray());
        }

        releaseAlpha.TrySetResult();
        await WithTimeout(Task.WhenAll(holder, beta));

        lock (orderLock)
        {
            CollectionAssert.AreEqual(s_orderAlpha2ThenBeta, order.ToArray());
        }
    }

    #endregion Alpha precedence

    #region Reentrancy

    [TestMethod]
    public async Task Alpha_Reentrancy_SameFlow_DoesNotDeadlock()
    {
        using AsyncAlphaBetaLock gate = new();
        int depthObserved = 0;

        await WithTimeout(gate.RunAlphaTaskAsync(async ct =>
        {
            Assert.IsTrue(gate.IsAlphaHeld);
            await gate.RunAlphaTaskAsync(async ct2 =>
            {
                Assert.IsTrue(gate.IsAlphaHeld);
                depthObserved = gate.AlphaLocksHeld;
                await Task.Yield();
            }, ct);
        }, TimeoutToken()));

        Assert.IsGreaterThanOrEqualTo(2, depthObserved);
        Assert.IsFalse(gate.IsAlphaHeld);
    }

    [TestMethod]
    public async Task Beta_Reentrancy_WhileAlphaWaiting_DoesNotDeadlock()
    {
        using AsyncAlphaBetaLock gate = new();
        using CancellationTokenSource cts = new(s_testTimeout);

        TaskCompletionSource betaOuterHolds = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource alphaIsWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reentered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reentryCount = 0;

        // Outer beta holds, then waits until we confirm alpha is waiting, then reenters.
        Task beta = gate.RunBetaTaskAsync(async ct =>
        {
            betaOuterHolds.TrySetResult();
            await alphaIsWaiting.Task.WaitAsync(ct);

            // CRITICAL: reenter beta while alpha is waiting — must not block.
            await gate.RunBetaAsync(() =>
            {
                reentryCount++;
                reentered.TrySetResult();
            }, ct);
        }, cts.Token);

        await WithTimeout(betaOuterHolds.Task);

        // Alpha queues behind beta.
        TaskCompletionSource releaseAlpha = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task alpha = gate.RunAlphaTaskAsync(async ct =>
        {
            await releaseAlpha.Task.WaitAsync(ct);
        }, cts.Token);

        Assert.IsTrue(SpinWait.SpinUntil(() => gate.WaitingAlphaCount >= 1, s_testTimeout));
        alphaIsWaiting.TrySetResult();

        await WithTimeout(reentered.Task);
        Assert.AreEqual(1, reentryCount);

        // Let everyone finish.
        // Beta should complete after reentry; then alpha can enter.
        await WithTimeout(beta);
        releaseAlpha.TrySetResult();
        await WithTimeout(alpha);
    }

    [TestMethod]
    public async Task CrossGroup_AlphaThenBeta_Throws()
    {
        using AsyncAlphaBetaLock gate = new();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await gate.RunAlphaTaskAsync(async ct =>
            {
                await gate.RunBetaAsync(() => { }, ct);
            }, TimeoutToken());
        });
    }

    [TestMethod]
    public async Task CrossGroup_BetaThenAlpha_Throws()
    {
        using AsyncAlphaBetaLock gate = new();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await gate.RunBetaTaskAsync(async ct =>
            {
                await gate.RunAlphaAsync(() => { }, ct);
            }, TimeoutToken());
        });
    }

    [TestMethod]
    public async Task NestedReentrancy_DepthUnwindsCorrectly()
    {
        using AsyncAlphaBetaLock gate = new();
        List<int> depths = [];

        await WithTimeout(gate.RunAlphaTaskAsync(async ct =>
        {
            depths.Add(gate.AlphaLocksHeld);
            await gate.RunAlphaTaskAsync(async ct2 =>
            {
                depths.Add(gate.AlphaLocksHeld);
                await gate.RunAlphaAsync(() => depths.Add(gate.AlphaLocksHeld), ct2);
                depths.Add(gate.AlphaLocksHeld);
            }, ct);
            depths.Add(gate.AlphaLocksHeld);
        }, TimeoutToken()));

        CollectionAssert.AreEqual(s_nestedDepths, depths);
        Assert.AreEqual(0, gate.CurrentAlphaCount);
    }

    #endregion Reentrancy

    #region Cancellation

    [TestMethod]
    public async Task Cancel_WaitingBeta_UnregistersAndThrows()
    {
        using AsyncAlphaBetaLock gate = new();
        using CancellationTokenSource holdCts = new(s_testTimeout);

        TaskCompletionSource alphaHolds = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseAlpha = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task alpha = gate.RunAlphaTaskAsync(async ct =>
        {
            alphaHolds.TrySetResult();
            await releaseAlpha.Task.WaitAsync(ct);
        }, holdCts.Token);
        await WithTimeout(alphaHolds.Task);

        using CancellationTokenSource waitCts = new();
        Task beta = gate.RunBetaAsync(() => { }, waitCts.Token);
        Assert.IsTrue(SpinWait.SpinUntil(() => gate.WaitingBetaCount >= 1, s_testTimeout));

        await waitCts.CancelAsync();
        // WaitAsync surfaces TaskCanceledException (a subclass of OperationCanceledException).
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await beta);

        Assert.AreEqual(0, gate.WaitingBetaCount);

        releaseAlpha.TrySetResult();
        await WithTimeout(alpha);
    }

    [TestMethod]
    public async Task Cancel_LastAlphaWaiter_ReleasesBetaAdmission()
    {
        using AsyncAlphaBetaLock gate = new();
        using CancellationTokenSource holdCts = new(s_testTimeout);

        // Beta holds.
        TaskCompletionSource betaHolds = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseBeta = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task betaHolder = gate.RunBetaTaskAsync(async ct =>
        {
            betaHolds.TrySetResult();
            await releaseBeta.Task.WaitAsync(ct);
        }, holdCts.Token);
        await WithTimeout(betaHolds.Task);

        // Alpha waits (blocks new betas).
        using CancellationTokenSource alphaCts = new();
        Task alphaWaiter = gate.RunAlphaAsync(() => { }, alphaCts.Token);
        Assert.IsTrue(SpinWait.SpinUntil(() => gate.WaitingAlphaCount >= 1, s_testTimeout));

        // Another beta queues behind alpha precedence.
        Task beta2 = gate.RunBetaAsync(() => { }, holdCts.Token);
        Assert.IsTrue(SpinWait.SpinUntil(() => gate.WaitingBetaCount >= 1, s_testTimeout));

        // Cancel the alpha waiter — should open the door for beta2 once holder releases
        // (and must not leave beta2 stranded if holder already gone).
        await alphaCts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await alphaWaiter);
        Assert.AreEqual(0, gate.WaitingAlphaCount);

        releaseBeta.TrySetResult();
        await WithTimeout(Task.WhenAll(betaHolder, beta2));
    }

    [TestMethod]
    public async Task Cancel_BeforeAcquire_DoesNotLeakHolders()
    {
        using AsyncAlphaBetaLock gate = new();
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await gate.RunAlphaAsync(() => { }, cts.Token);
        });

        Assert.AreEqual(0, gate.CurrentAlphaCount);
        Assert.AreEqual(0, gate.WaitingAlphaCount);

        // Lock still usable.
        await WithTimeout(gate.RunAlphaAsync(() => { }, TimeoutToken()));
    }

    #endregion Cancellation

    #region Disposal

    [TestMethod]
    public async Task Dispose_CancelsWaiters_WithLockDisposedException()
    {
        AsyncAlphaBetaLock gate = new();
        using CancellationTokenSource holdCts = new(s_testTimeout);

        TaskCompletionSource alphaHolds = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseAlpha = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task holder = gate.RunAlphaTaskAsync(async ct =>
        {
            alphaHolds.TrySetResult();
            await releaseAlpha.Task.WaitAsync(ct);
        }, holdCts.Token);
        await WithTimeout(alphaHolds.Task);

        Task waiter = gate.RunBetaAsync(() => { });
        Assert.IsTrue(SpinWait.SpinUntil(() => gate.WaitingBetaCount >= 1, s_testTimeout));

        // Dispose while waiter is parked. Holder still inside.
        gate.Dispose();

        await Assert.ThrowsExactlyAsync<LockDisposedException>(async () => await waiter);

        releaseAlpha.TrySetResult();
        await WithTimeout(holder);

        // Further use fails.
        await Assert.ThrowsExactlyAsync<LockDisposedException>(async () =>
            await gate.RunAlphaAsync(() => { }));
    }

    [TestMethod]
    public async Task TryRun_OnDisposed_ReturnsSkipped()
    {
        AsyncAlphaBetaLock gate = new();
        gate.Dispose();

        AsyncLockResult r1 = await gate.TryRunAlphaAsync(() => { });
        Assert.IsFalse(r1.TaskExecuted);

        AsyncLockResult<int> r2 = await gate.TryRunBetaAsync(() => 1);
        Assert.IsFalse(r2.TaskExecuted);
        Assert.IsFalse(r2.TryGetResult(out _));

        AsyncLockResult r3 = await gate.TryRunAlphaTaskAsync(async ct => await Task.Yield());
        Assert.IsFalse(r3.TaskExecuted);

        AsyncLockResult<string> r4 = await gate.TryRunBetaTaskAsync(async ct =>
        {
            await Task.Yield();
            return "x";
        });
        Assert.IsFalse(r4.TaskExecuted);
    }

    [TestMethod]
    public async Task TryRun_SucceedsWhenNotDisposed()
    {
        using AsyncAlphaBetaLock gate = new();

        AsyncLockResult r1 = await WithTimeout(gate.TryRunAlphaAsync(() => { }));
        Assert.IsTrue(r1.TaskExecuted);

        AsyncLockResult<int> r2 = await WithTimeout(gate.TryRunBetaAsync(() => 5));
        Assert.IsTrue(r2.TaskExecuted);
        Assert.IsTrue(r2.TryGetResult(out int value));
        Assert.AreEqual(5, value);
    }

    [TestMethod]
    public async Task Dispose_IsIdempotent()
    {
        AsyncAlphaBetaLock gate = new();
        gate.Dispose();
        gate.Dispose();

        await Assert.ThrowsExactlyAsync<LockDisposedException>(async () =>
            await gate.RunAlphaAsync(() => { }));
    }

    [TestMethod]
    public async Task Dispose_DuringHold_AllowsExit_AndBlocksNewEntry()
    {
        AsyncAlphaBetaLock gate = new();
        using CancellationTokenSource cts = new(s_testTimeout);

        TaskCompletionSource holds = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool exitedCleanly = false;

        Task holder = gate.RunAlphaTaskAsync(async ct =>
        {
            holds.TrySetResult();
            await disposed.Task.WaitAsync(ct);
            exitedCleanly = true;
        }, cts.Token);

        await WithTimeout(holds.Task);
        gate.Dispose();
        disposed.TrySetResult();
        await WithTimeout(holder);
        Assert.IsTrue(exitedCleanly);

        await Assert.ThrowsExactlyAsync<LockDisposedException>(async () =>
            await gate.RunBetaAsync(() => { }));
    }

    #endregion Disposal

    #region Exception safety

    [TestMethod]
    public async Task ExceptionInUserCode_ReleasesAlphaLock()
    {
        using AsyncAlphaBetaLock gate = new();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await gate.RunAlphaAsync(() => throw new InvalidOperationException("boom"));
        });

        Assert.AreEqual(0, gate.CurrentAlphaCount);
        Assert.IsFalse(gate.IsAlphaHeld);

        // Lock still usable.
        await WithTimeout(gate.RunAlphaAsync(() => { }, TimeoutToken()));
        await WithTimeout(gate.RunBetaAsync(() => { }, TimeoutToken()));
    }

    [TestMethod]
    public async Task ExceptionInAsyncUserCode_ReleasesBetaLock()
    {
        using AsyncAlphaBetaLock gate = new();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await gate.RunBetaTaskAsync(async ct =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            });
        });

        Assert.AreEqual(0, gate.CurrentBetaCount);
        await WithTimeout(gate.RunAlphaAsync(() => { }, TimeoutToken()));
    }

    #endregion Exception safety

    #region Acquire scope

    [TestMethod]
    public async Task AcquireAlphaAsync_ScopeReleasesOnDispose()
    {
        using AsyncAlphaBetaLock gate = new();

        IDisposable scope = await WithTimeout(gate.AcquireAlphaAsync(TimeoutToken()));
        Assert.IsTrue(gate.IsAlphaHeld);
        Assert.AreEqual(1, gate.CurrentAlphaCount);

        scope.Dispose();
        Assert.IsFalse(gate.IsAlphaHeld);
        Assert.AreEqual(0, gate.CurrentAlphaCount);
    }

    [TestMethod]
    public async Task AcquireBetaAsync_Scope_DoubleDispose_IsSafe()
    {
        using AsyncAlphaBetaLock gate = new();

        IDisposable scope = await WithTimeout(gate.AcquireBetaAsync(TimeoutToken()));
        scope.Dispose();
        scope.Dispose();

        Assert.AreEqual(0, gate.CurrentBetaCount);
        await WithTimeout(gate.RunBetaAsync(() => { }, TimeoutToken()));
    }

    [TestMethod]
    public async Task Acquire_Reentrancy_WithRun()
    {
        using AsyncAlphaBetaLock gate = new();

        IDisposable scope = await WithTimeout(gate.AcquireAlphaAsync(TimeoutToken()));
        try
        {
            await WithTimeout(gate.RunAlphaAsync(() =>
            {
                Assert.IsGreaterThanOrEqualTo(2, gate.AlphaLocksHeld);
            }, TimeoutToken()));
        }
        finally
        {
            scope.Dispose();
        }

        Assert.AreEqual(0, gate.CurrentAlphaCount);
    }

    #endregion Acquire scope

    #region Argument / edge cases

    [TestMethod]
    public async Task NullTaskDelegate_Throws()
    {
        using AsyncAlphaBetaLock gate = new();

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
        {
            await gate.RunAlphaTaskAsync<int>(null!, TimeoutToken());
        });

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
        {
            await gate.RunBetaTaskAsync<int>(null!, TimeoutToken());
        });
    }

    [TestMethod]
    public async Task ManyConcurrentSameGroup_AllComplete()
    {
        using AsyncAlphaBetaLock gate = new();
        using CancellationTokenSource cts = new(s_testTimeout);
        const int N = 50;
        int counter = 0;

        Task[] tasks = Enumerable.Range(0, N).Select(_ =>
            gate.RunAlphaTaskAsync(async ct =>
            {
                Interlocked.Increment(ref counter);
                await Task.Yield();
            }, cts.Token)).ToArray();

        await WithTimeout(Task.WhenAll(tasks));
        Assert.AreEqual(N, counter);
        Assert.AreEqual(0, gate.CurrentAlphaCount);
    }

    [TestMethod]
    public async Task AlternatingGroups_SerializeCorrectly()
    {
        using AsyncAlphaBetaLock gate = new();
        using CancellationTokenSource cts = new(s_testTimeout);
        const int N = 20;
        int concurrent = 0;
        int violations = 0;
        List<Task> tasks = [];

        for (int i = 0; i < N; i++)
        {
            bool alpha = i % 2 == 0;
            if (alpha)
            {
                tasks.Add(gate.RunAlphaTaskAsync(async ct =>
                {
                    int c = Interlocked.Increment(ref concurrent);
                    // concurrent may be >1 for same group; track mixed by using separate counters would be better
                    await Task.Yield();
                    Interlocked.Decrement(ref concurrent);
                }, cts.Token));
            }
            else
            {
                tasks.Add(gate.RunBetaTaskAsync(async ct =>
                {
                    await Task.Yield();
                }, cts.Token));
            }
        }

        await WithTimeout(Task.WhenAll(tasks));

        // Stronger check with exclusive counters:
        int alphaInside = 0;
        int betaInside = 0;
        tasks.Clear();
        for (int i = 0; i < N; i++)
        {
            bool isAlpha = i % 2 == 0;
            if (isAlpha)
            {
                tasks.Add(gate.RunAlphaTaskAsync(async ct =>
                {
                    Interlocked.Increment(ref alphaInside);
                    if (Volatile.Read(ref betaInside) != 0)
                    {
                        Interlocked.Increment(ref violations);
                    }
                    await Task.Yield();
                    Interlocked.Decrement(ref alphaInside);
                }, cts.Token));
            }
            else
            {
                tasks.Add(gate.RunBetaTaskAsync(async ct =>
                {
                    Interlocked.Increment(ref betaInside);
                    if (Volatile.Read(ref alphaInside) != 0)
                    {
                        Interlocked.Increment(ref violations);
                    }
                    await Task.Yield();
                    Interlocked.Decrement(ref betaInside);
                }, cts.Token));
            }
        }

        await WithTimeout(Task.WhenAll(tasks));
        Assert.AreEqual(0, violations, "Detected overlapping alpha and beta holders");
    }

    [TestMethod]
    public async Task TryRunTaskAsync_PropagatesUserException()
    {
        using AsyncAlphaBetaLock gate = new();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await gate.TryRunAlphaTaskAsync(async ct =>
            {
                await Task.Yield();
                throw new InvalidOperationException("user");
            });
        });
    }

    #endregion Argument / edge cases

    private static void InterlockedMax(ref int location, int value)
    {
        int current = Volatile.Read(ref location);
        while (value > current)
        {
            int prev = Interlocked.CompareExchange(ref location, value, current);
            if (prev == current)
            {
                return;
            }
            current = prev;
        }
    }
}
