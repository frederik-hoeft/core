using Fho.Core.Threading.Collections;

namespace Fho.Core.Threading.Tests.Collections;

/// <summary>
/// Concurrent smoke/regression tests. These cannot prove correctness, but they
/// dynamically exercise races around emptiness, CAS updates, and structural ops.
/// </summary>
[TestClass]
public sealed class ConcurrentBitmap2StressTests
{
    private static int Degree => Math.Max(4, Environment.ProcessorCount);

    [TestMethod]
    public void ConcurrentSetsAndClears_FinalStateMatchesScan_AndClearsToEmpty()
    {
        const int size = 256;
        const int iterationsPerThread = 5_000;
        ConcurrentBitmap2 bmp = new(size);
        using Barrier barrier = new(Degree);

        void Worker()
        {
            barrier.SignalAndWait();
            Random rng = new(Environment.CurrentManagedThreadId * 7919);
            for (int i = 0; i < iterationsPerThread; i++)
            {
                int index = rng.Next(size);
                bmp.UpdateBit(index, isSet: rng.Next(2) == 0);
            }
        }

        Parallel.For(0, Degree, _ => Worker());

        int scanned = ScanSetBits(bmp);
        Assert.AreEqual(scanned, bmp.VolatileSetCount, "Set-bit counter drifted from scanned bit state.");
        Assert.AreEqual(scanned == 0, bmp.IsEmpty);

        for (int i = 0; i < size; i++)
        {
            bmp.UpdateBit(i, isSet: false);
        }

        Assert.IsTrue(bmp.IsEmpty);
        Assert.AreEqual(0, bmp.VolatileSetCount);
        Assert.AreEqual(0, ScanSetBits(bmp));
    }

    [TestMethod]
    public void ProducerConsumer_WorkTracking_NoLostWorkWhenIsEmpty()
    {
        // Models the primary usage pattern: producers set a bit after enqueuing work;
        // consumers drain buckets indicated by set bits and clear them with versioned CAS.
        const int buckets = 64;
        const int workItems = 20_000;
        ConcurrentBitmap2 bmp = new(buckets);
        int[] pending = new int[buckets];
        int produced = 0;
        int consumed = 0;
        int falseEmptyWithSetBit = 0;
        using Barrier start = new(Degree);

        int producers = Degree / 2;
        int consumers = Degree - producers;
        int producersRunning = producers;

        void Producer()
        {
            start.SignalAndWait();
            Random rng = new(HashCode.Combine(Environment.CurrentManagedThreadId, 11));
            try
            {
                while (true)
                {
                    int id = Interlocked.Increment(ref produced);
                    if (id > workItems)
                    {
                        break;
                    }

                    int bucket = rng.Next(buckets);
                    Interlocked.Increment(ref pending[bucket]);
                    Thread.MemoryBarrier();
                    bmp.UpdateBit(bucket, isSet: true);
                }
            }
            finally
            {
                Interlocked.Decrement(ref producersRunning);
            }
        }

        void Consumer()
        {
            start.SignalAndWait();
            Random rng = new(HashCode.Combine(Environment.CurrentManagedThreadId, 29));
            while (true)
            {
                bool producersDone = Volatile.Read(ref producersRunning) == 0;
                int residual = SumPending(pending);

                if (bmp.IsEmpty)
                {
                    // Re-sample emptiness after observing a set bit to avoid TOCTOU false failures
                    // from a concurrent setter that ran between IsEmpty and IsBitSet.
                    for (int b = 0; b < buckets; b++)
                    {
                        if (bmp.IsBitSet(b) && bmp.IsEmpty)
                        {
                            Interlocked.Increment(ref falseEmptyWithSetBit);
                            break;
                        }
                    }

                    if (producersDone && residual == 0)
                    {
                        break;
                    }

                    Thread.SpinWait(50);
                    continue;
                }

                int startIndex = rng.Next(buckets);
                for (int offset = 0; offset < buckets; offset++)
                {
                    int bucket = (startIndex + offset) % buckets;
                    if (Volatile.Read(ref pending[bucket]) <= 0 && !bmp.IsBitSet(bucket))
                    {
                        continue;
                    }

                    while (true)
                    {
                        int current = Volatile.Read(ref pending[bucket]);
                        if (current <= 0)
                        {
                            break;
                        }

                        if (Interlocked.CompareExchange(ref pending[bucket], current - 1, current) == current)
                        {
                            Interlocked.Increment(ref consumed);
                        }
                    }

                    // Clear when the bucket looks empty; re-set if a producer raced in.
                    GuardedBitInfo info = bmp.GetBitInfo(bucket);
                    if (info.IsSet && Volatile.Read(ref pending[bucket]) == 0)
                    {
                        bmp.TryUpdateBit(bucket, info.Token, isSet: false);
                        if (Volatile.Read(ref pending[bucket]) > 0)
                        {
                            bmp.UpdateBit(bucket, isSet: true);
                        }
                    }
                }

                if (producersDone && SumPending(pending) == 0)
                {
                    // Publish a clean empty bitmap for the IsEmpty assertions at the end.
                    for (int b = 0; b < buckets; b++)
                    {
                        if (bmp.IsBitSet(b))
                        {
                            bmp.UpdateBit(b, isSet: false);
                        }
                    }

                    break;
                }
            }
        }

        List<Task> tasks = new(Degree);
        for (int i = 0; i < producers; i++)
        {
            tasks.Add(Task.Run(Producer));
        }

        for (int i = 0; i < consumers; i++)
        {
            tasks.Add(Task.Run(Consumer));
        }

        Assert.IsTrue(
            Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(60)),
            "Producer/consumer stress timed out (possible deadlock/livelock).");

        Assert.AreEqual(0, falseEmptyWithSetBit, "IsEmpty was true while a bit was still set.");
        Assert.AreEqual(workItems, consumed, "Not all work items were consumed.");
        Assert.IsTrue(bmp.IsEmpty);
        Assert.AreEqual(0, bmp.VolatileSetCount);
    }

    [TestMethod]
    public void ConcurrentTryUpdateBit_CasSemantics_NoLostIncrements()
    {
        const int size = 128;
        const int rounds = 2_000;
        ConcurrentBitmap2 bmp = new(size);
        using Barrier barrier = new(Degree);

        void Worker()
        {
            barrier.SignalAndWait();
            Random rng = new(Environment.CurrentManagedThreadId * 104729);
            for (int i = 0; i < rounds; i++)
            {
                int index = rng.Next(size);
                GuardedBitInfo info = bmp.GetBitInfo(index);
                _ = bmp.TryUpdateBit(index, info.Token, isSet: !info.IsSet);
            }
        }

        Parallel.For(0, Degree, _ => Worker());

        int scanned = ScanSetBits(bmp);
        Assert.AreEqual(scanned, bmp.VolatileSetCount, "Set-bit counter drifted from actual bit state.");
        Assert.AreEqual(scanned == 0, bmp.IsEmpty);
    }

    [TestMethod]
    public void ConcurrentPointOpsWithOccasionalGrow_RemainsConsistent()
    {
        const int initialSize = 64;
        ConcurrentBitmap2 bmp = new(initialSize);
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(800));
        using Barrier barrier = new(Degree);

        void Worker(int workerId)
        {
            barrier.SignalAndWait();
            Random rng = new(workerId * 9973);
            while (!cts.IsCancellationRequested)
            {
                int size = bmp.Size;
                if (size <= 0)
                {
                    continue;
                }

                int index = rng.Next(size);
                try
                {
                    if (workerId == 0 && rng.Next(50) == 0)
                    {
                        bmp.Grow(size + rng.Next(1, 40));
                    }
                    else if (rng.Next(2) == 0)
                    {
                        bmp.UpdateBit(index, isSet: rng.Next(2) == 0);
                    }
                    else
                    {
                        GuardedBitInfo info = bmp.GetBitInfo(index);
                        bmp.TryUpdateBit(index, info.Token, isSet: !info.IsSet);
                    }
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Should be rare with grow-only topology changes.
                }
            }
        }

        Parallel.For(0, Degree, Worker);
        Assert.IsGreaterThanOrEqualTo(initialSize, bmp.Size);

        int scanned = ScanSetBits(bmp);
        Assert.AreEqual(scanned, bmp.VolatileSetCount);
        Assert.AreEqual(scanned == 0, bmp.IsEmpty);
    }

    [TestMethod]
    public void ConcurrentPointOpsWithRemoveBitAt_NoDeadlock_CountMatchesScan()
    {
        ConcurrentBitmap2 bmp = new(100);
        for (int i = 0; i < 100; i++)
        {
            if (i % 3 == 0)
            {
                bmp.UpdateBit(i, isSet: true);
            }
        }

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(500));

        // Use Parallel.For (no Barrier/Task.Run) so this does not deadlock the thread pool
        // when the test host already runs many stress tests concurrently.
        Parallel.For(0, Degree, workerId =>
        {
            Random rng = new(workerId * 3221);
            while (!cts.IsCancellationRequested)
            {
                int size = bmp.Size;
                if (size <= 1)
                {
                    break;
                }

                try
                {
                    if (workerId == 0 && rng.Next(30) == 0)
                    {
                        bmp.RemoveBitAt(rng.Next(size));
                    }
                    else
                    {
                        bmp.UpdateBit(rng.Next(bmp.Size), isSet: rng.Next(2) == 0);
                    }
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Size shrank between sampling and the call.
                }
            }
        });

        int scanned = ScanSetBits(bmp);
        Assert.AreEqual(scanned, bmp.VolatileSetCount, "Counter drifted after RemoveBitAt stress.");
        Assert.AreEqual(scanned == 0, bmp.IsEmpty);
    }

    [TestMethod]
    public void HighContentionSameBit_UpdateAndTryUpdate_Stable()
    {
        ConcurrentBitmap2 bmp = new(8);
        using Barrier barrier = new(Degree);
        const int hits = 10_000;

        void Worker(int workerId)
        {
            barrier.SignalAndWait();
            for (int i = 0; i < hits; i++)
            {
                if ((workerId & 1) == 0)
                {
                    bmp.UpdateBit(0, isSet: (i & 1) == 0);
                }
                else
                {
                    GuardedBitInfo info = bmp.GetBitInfo(0);
                    bmp.TryUpdateBit(0, info.Token, isSet: !info.IsSet);
                }
            }
        }

        Parallel.For(0, Degree, Worker);

        int scanned = ScanSetBits(bmp);
        Assert.AreEqual(scanned, bmp.VolatileSetCount);
        Assert.AreEqual(scanned == 0, bmp.IsEmpty);
    }

    [TestMethod]
    public void TryFindNextSetBit_ConcurrentWithUpdates_NoCrashAndInRange()
    {
        ConcurrentBitmap2 bmp = new(512);
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(500));
        int errors = 0;
        using Barrier barrier = new(Degree);

        void Worker(int workerId)
        {
            barrier.SignalAndWait();
            Random rng = new(workerId * 13);
            while (!cts.IsCancellationRequested)
            {
                if (workerId == 0)
                {
                    if (bmp.TryFindNextSetBit(rng.Next(0, 512), out int index))
                    {
                        if ((uint)index >= (uint)bmp.Size)
                        {
                            Interlocked.Increment(ref errors);
                        }
                    }
                }
                else
                {
                    bmp.UpdateBit(rng.Next(bmp.Size), isSet: rng.Next(3) != 0);
                }
            }
        }

        Parallel.For(0, Degree, Worker);
        Assert.AreEqual(0, errors);
    }

    [TestMethod]
    public void AllClearViaVersionedCas_EndsEmpty()
    {
        const int size = 96;
        ConcurrentBitmap2 bmp = new(size);
        Parallel.For(0, size, i => bmp.UpdateBit(i, isSet: true));
        Assert.IsFalse(bmp.IsEmpty);

        Parallel.For(0, size, i =>
        {
            while (true)
            {
                GuardedBitInfo info = bmp.GetBitInfo(i);
                if (!info.IsSet)
                {
                    return;
                }

                if (bmp.TryUpdateBit(i, info.Token, isSet: false))
                {
                    return;
                }
            }
        });

        Assert.IsTrue(bmp.IsEmpty);
        Assert.AreEqual(0, bmp.VolatileSetCount);
        Assert.IsFalse(bmp.TryFindNextSetBit(0, out _));
    }

    [TestMethod]
    public void SingleBit_HighContention_CountNeverDrifts()
    {
        ConcurrentBitmap2 bmp = new(1);
        using Barrier barrier = new(Degree);
        const int hits = 20_000;

        void Worker()
        {
            barrier.SignalAndWait();
            for (int i = 0; i < hits; i++)
            {
                bmp.UpdateBit(0, isSet: (i & 1) == 0);
            }
        }

        Parallel.For(0, Degree, _ => Worker());

        int scanned = ScanSetBits(bmp);
        Assert.AreEqual(scanned, bmp.VolatileSetCount);
        Assert.AreEqual(scanned == 0, bmp.IsEmpty);
    }

    private static int ScanSetBits(ConcurrentBitmap2 bmp)
    {
        int count = 0;
        int size = bmp.Size;
        for (int i = 0; i < size; i++)
        {
            if (bmp.IsBitSet(i))
            {
                count++;
            }
        }

        return count;
    }

    private static int SumPending(int[] pending)
    {
        int sum = 0;
        for (int i = 0; i < pending.Length; i++)
        {
            sum += Volatile.Read(ref pending[i]);
        }

        return sum;
    }
}
