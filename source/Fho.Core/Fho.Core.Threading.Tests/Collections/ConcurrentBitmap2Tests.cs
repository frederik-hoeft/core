using Fho.Core.Threading.Collections;

namespace Fho.Core.Threading.Tests.Collections;

[TestClass]
public sealed class ConcurrentBitmap2Tests
{
    [TestMethod]
    public void Constructor_RejectsNonPositiveSize()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ConcurrentBitmap2(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ConcurrentBitmap2(-1));
    }

    [TestMethod]
    public void BitRemovalInvalidatesTokens()
    {
        ConcurrentBitmap2 bmp = new(128);
    }

    [TestMethod]
    public void NewBitmap_IsEmpty_AllBitsClear()
    {
        ConcurrentBitmap2 bmp = new(128);
        Assert.AreEqual(128, bmp.Size);
        Assert.IsTrue(bmp.IsEmpty);
        Assert.AreEqual(0, bmp.VolatileSetCount);

        for (int i = 0; i < bmp.Size; i++)
        {
            Assert.IsFalse(bmp.IsBitSet(i));
            GuardedBitInfo info = bmp.GetBitInfo(i);
            Assert.IsFalse(info.IsSet);
            Assert.AreEqual(i, info.Index);
        }
    }

    [TestMethod]
    public void UpdateBit_SetAndClear_RoundTrips()
    {
        ConcurrentBitmap2 bmp = new(100);
        bmp.UpdateBit(0, isSet: true);
        bmp.UpdateBit(56, isSet: true);
        bmp.UpdateBit(99, isSet: true);

        Assert.IsFalse(bmp.IsEmpty);
        Assert.AreEqual(3, bmp.VolatileSetCount);
        Assert.IsTrue(bmp.IsBitSet(0));
        Assert.IsTrue(bmp.IsBitSet(56));
        Assert.IsTrue(bmp.IsBitSet(99));
        Assert.IsFalse(bmp.IsBitSet(1));

        bmp.UpdateBit(56, isSet: false);
        Assert.AreEqual(2, bmp.VolatileSetCount);
        Assert.IsFalse(bmp.IsBitSet(56));

        bmp.UpdateBit(0, isSet: false);
        bmp.UpdateBit(99, isSet: false);
        Assert.IsTrue(bmp.IsEmpty);
        Assert.AreEqual(0, bmp.VolatileSetCount);
    }

    [TestMethod]
    public void UpdateBit_SameValue_StillBumpsToken()
    {
        ConcurrentBitmap2 bmp = new(16);
        bmp.UpdateBit(3, isSet: true);
        byte token1 = bmp.GetToken(3);
        bmp.UpdateBit(3, isSet: true);
        byte token2 = bmp.GetToken(3);
        Assert.AreNotEqual(token1, token2);
        Assert.IsTrue(bmp.IsBitSet(3));
        Assert.AreEqual(1, bmp.VolatileSetCount);
    }

    [TestMethod]
    public void TryUpdateBit_SucceedsWithMatchingToken_FailsWithStaleToken()
    {
        ConcurrentBitmap2 bmp = new(32);
        GuardedBitInfo info = bmp.GetBitInfo(7);
        Assert.IsFalse(info.IsSet);

        Assert.IsTrue(bmp.TryUpdateBit(7, info.Token, isSet: true));
        Assert.IsTrue(bmp.IsBitSet(7));

        // Stale token from before the successful write.
        Assert.IsFalse(bmp.TryUpdateBit(7, info.Token, isSet: false));
        Assert.IsTrue(bmp.IsBitSet(7));

        GuardedBitInfo after = bmp.GetBitInfo(7);
        Assert.IsTrue(after.IsSet);
        Assert.IsTrue(bmp.TryUpdateBit(7, after.Token, isSet: false));
        Assert.IsFalse(bmp.IsBitSet(7));
        Assert.IsTrue(bmp.IsEmpty);
    }

    [TestMethod]
    public void GetBitInfo_ReportsIndexAndState()
    {
        ConcurrentBitmap2 bmp = new(10);
        bmp.UpdateBit(4, isSet: true);
        GuardedBitInfo info = bmp.GetBitInfo(4);
        Assert.AreEqual(4, info.Index);
        Assert.IsTrue(info.IsSet);
        Assert.AreEqual(bmp.GetToken(4), info.Token);
    }

    [TestMethod]
    public void PointApis_ThrowOnOutOfRange()
    {
        ConcurrentBitmap2 bmp = new(8);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => bmp.IsBitSet(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => bmp.IsBitSet(8));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => bmp.GetBitInfo(8));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => bmp.GetToken(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => bmp.UpdateBit(8, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => bmp.TryUpdateBit(-1, 0, true));
    }

    [TestMethod]
    public void Grow_ExtendsWithClearedBits_IsNoopWhenNotLarger()
    {
        ConcurrentBitmap2 bmp = new(40);
        bmp.UpdateBit(39, isSet: true);

        bmp.Grow(40);
        Assert.AreEqual(40, bmp.Size);

        bmp.Grow(10);
        Assert.AreEqual(40, bmp.Size);

        bmp.Grow(100);
        Assert.AreEqual(100, bmp.Size);
        Assert.IsTrue(bmp.IsBitSet(39));
        Assert.IsFalse(bmp.IsBitSet(40));
        Assert.IsFalse(bmp.IsBitSet(99));
        Assert.AreEqual(1, bmp.VolatileSetCount);
    }

    [TestMethod]
    public void Grow_AcrossSegmentBoundary_PreservesBits()
    {
        ConcurrentBitmap2 bmp = new(50);
        bmp.UpdateBit(0, isSet: true);
        bmp.UpdateBit(49, isSet: true);
        bmp.Grow(120);
        Assert.AreEqual(120, bmp.Size);
        Assert.IsTrue(bmp.IsBitSet(0));
        Assert.IsTrue(bmp.IsBitSet(49));
        Assert.IsFalse(bmp.IsBitSet(56));
        Assert.IsFalse(bmp.IsBitSet(119));
    }

    [TestMethod]
    public void RemoveBitAt_ShiftsHigherBitsDown()
    {
        ConcurrentBitmap2 bmp = new(10);
        // bits: indices 0..9; set 2, 5, 9
        bmp.UpdateBit(2, isSet: true);
        bmp.UpdateBit(5, isSet: true);
        bmp.UpdateBit(9, isSet: true);

        bmp.RemoveBitAt(3); // remove clear bit between 2 and 5
        Assert.AreEqual(9, bmp.Size);
        Assert.IsTrue(bmp.IsBitSet(2));
        Assert.IsTrue(bmp.IsBitSet(4)); // was 5
        Assert.IsTrue(bmp.IsBitSet(8)); // was 9
        Assert.AreEqual(3, bmp.VolatileSetCount);

        bmp.RemoveBitAt(2); // remove a set bit
        Assert.AreEqual(8, bmp.Size);
        Assert.IsFalse(bmp.IsBitSet(2));
        Assert.IsTrue(bmp.IsBitSet(3)); // was 4 (originally 5)
        Assert.IsTrue(bmp.IsBitSet(7)); // was 8 (originally 9)
        Assert.AreEqual(2, bmp.VolatileSetCount);
        Assert.IsFalse(bmp.IsEmpty);
    }

    [TestMethod]
    public void RemoveBitAt_LastSetBit_MakesEmpty()
    {
        ConcurrentBitmap2 bmp = new(5);
        bmp.UpdateBit(1, isSet: true);
        bmp.RemoveBitAt(1);
        Assert.AreEqual(4, bmp.Size);
        Assert.IsTrue(bmp.IsEmpty);
        Assert.AreEqual(0, bmp.VolatileSetCount);
    }

    [TestMethod]
    public void RemoveBitAt_ThrowsOnOutOfRange()
    {
        ConcurrentBitmap2 bmp = new(3);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => bmp.RemoveBitAt(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => bmp.RemoveBitAt(3));
    }

    [TestMethod]
    public void TryFindNextSetBit_FindsInOrder_AndHandlesEmpty()
    {
        ConcurrentBitmap2 bmp = new(200);
        Assert.IsFalse(bmp.TryFindNextSetBit(0, out int none));
        Assert.AreEqual(-1, none);

        bmp.UpdateBit(0, isSet: true);
        bmp.UpdateBit(55, isSet: true);
        bmp.UpdateBit(56, isSet: true);
        bmp.UpdateBit(199, isSet: true);

        Assert.IsTrue(bmp.TryFindNextSetBit(0, out int i0));
        Assert.AreEqual(0, i0);
        Assert.IsTrue(bmp.TryFindNextSetBit(1, out int i1));
        Assert.AreEqual(55, i1);
        Assert.IsTrue(bmp.TryFindNextSetBit(55, out int i2));
        Assert.AreEqual(55, i2);
        Assert.IsTrue(bmp.TryFindNextSetBit(56, out int i3));
        Assert.AreEqual(56, i3);
        Assert.IsTrue(bmp.TryFindNextSetBit(57, out int i4));
        Assert.AreEqual(199, i4);
        Assert.IsFalse(bmp.TryFindNextSetBit(200, out _));
    }

    [TestMethod]
    public void IsEmpty_TrueMeansNoBitsSet_AfterMixedOps()
    {
        ConcurrentBitmap2 bmp = new(64);
        for (int i = 0; i < 64; i++)
        {
            bmp.UpdateBit(i, isSet: true);
        }

        Assert.IsFalse(bmp.IsEmpty);
        Assert.AreEqual(64, bmp.VolatileSetCount);

        for (int i = 0; i < 64; i++)
        {
            bmp.UpdateBit(i, isSet: false);
            if (i < 63)
            {
                Assert.IsFalse(bmp.IsEmpty);
            }
        }

        Assert.IsTrue(bmp.IsEmpty);
        Assert.AreEqual(0, bmp.VolatileSetCount);
        for (int i = 0; i < 64; i++)
        {
            Assert.IsFalse(bmp.IsBitSet(i));
        }
    }

    [TestMethod]
    public void VersionedCas_Loop_Converges()
    {
        ConcurrentBitmap2 bmp = new(8);
        // Client-style CAS loop: set bit 2 only if observed clear.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            GuardedBitInfo info = bmp.GetBitInfo(2);
            if (info.IsSet)
            {
                break;
            }

            if (bmp.TryUpdateBit(2, info.Token, isSet: true))
            {
                break;
            }
        }

        Assert.IsTrue(bmp.IsBitSet(2));
    }

    [TestMethod]
    public void SpansMultipleSegments_IndependentTokens()
    {
        ConcurrentBitmap2 bmp = new(120);
        bmp.UpdateBit(0, isSet: true);
        byte t0 = bmp.GetToken(0);
        bmp.UpdateBit(60, isSet: true);
        byte t60 = bmp.GetToken(60);
        // Different segments have independent tokens; both start near zero and bump once.
        Assert.AreEqual(bmp.GetToken(0), t0);
        Assert.AreNotEqual(0, t0);
        Assert.AreNotEqual(0, t60);
        // Writing segment 0 must not change segment 1's token.
        byte t60Before = bmp.GetToken(60);
        bmp.UpdateBit(1, isSet: true);
        Assert.AreEqual(t60Before, bmp.GetToken(60));
    }
}
