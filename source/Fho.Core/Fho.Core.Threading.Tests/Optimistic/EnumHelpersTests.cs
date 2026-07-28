using Fho.Core.Threading.Optimistic;

namespace Fho.Core.Threading.Tests.Optimistic;

[TestClass]
public sealed class EnumHelpersTests
{
    [TestMethod]
    public void And_ByteEnum_Works()
    {
        Assert.AreEqual(ByteEnum.None, ByteEnum.A.And(ByteEnum.B));
        Assert.AreEqual(ByteEnum.A, ByteEnum.A.And(ByteEnum.C));
        Assert.AreEqual(ByteEnum.None, ByteEnum.A.And(ByteEnum.None));
        Assert.AreEqual(ByteEnum.All, ByteEnum.All.And(ByteEnum.All));
        Assert.AreEqual(ByteEnum.A, ByteEnum.All.And(ByteEnum.A));
    }

    [TestMethod]
    public void And_UShortEnum_Works()
    {
        Assert.AreEqual(UShortEnum.None, UShortEnum.A.And(UShortEnum.B));
        Assert.AreEqual(UShortEnum.A, UShortEnum.A.And(UShortEnum.C));
        Assert.AreEqual(UShortEnum.None, UShortEnum.A.And(UShortEnum.None));
        Assert.AreEqual(UShortEnum.All, UShortEnum.All.And(UShortEnum.All));
        Assert.AreEqual(UShortEnum.A, UShortEnum.All.And(UShortEnum.A));
    }

    [TestMethod]
    public void And_UIntEnum_Works()
    {
        Assert.AreEqual(UIntEnum.None, UIntEnum.A.And(UIntEnum.B));
        Assert.AreEqual(UIntEnum.A, UIntEnum.A.And(UIntEnum.C));
        Assert.AreEqual(UIntEnum.None, UIntEnum.A.And(UIntEnum.None));
        Assert.AreEqual(UIntEnum.All, UIntEnum.All.And(UIntEnum.All));
        Assert.AreEqual(UIntEnum.A, UIntEnum.All.And(UIntEnum.A));
    }

    [TestMethod]
    public void And_ULongEnum_Works()
    {
        Assert.AreEqual(ULongEnum.None, ULongEnum.A.And(ULongEnum.B));
        Assert.AreEqual(ULongEnum.A, ULongEnum.A.And(ULongEnum.C));
        Assert.AreEqual(ULongEnum.None, ULongEnum.A.And(ULongEnum.None));
        Assert.AreEqual(ULongEnum.All, ULongEnum.All.And(ULongEnum.All));
        Assert.AreEqual(ULongEnum.A, ULongEnum.All.And(ULongEnum.A));
    }

    [TestMethod]
    public void And_ImplicitIntEnum_Works() 
    {
        Assert.AreEqual(ImplicitIntEnum.None, ImplicitIntEnum.A.And(ImplicitIntEnum.B));
        Assert.AreEqual(ImplicitIntEnum.A, ImplicitIntEnum.A.And(ImplicitIntEnum.C));
        Assert.AreEqual(ImplicitIntEnum.None, ImplicitIntEnum.A.And(ImplicitIntEnum.None));
        Assert.AreEqual(ImplicitIntEnum.All, ImplicitIntEnum.All.And(ImplicitIntEnum.All));
        Assert.AreEqual(ImplicitIntEnum.A, ImplicitIntEnum.All.And(ImplicitIntEnum.A));
    }

    [TestMethod]
    public void Or_ByteEnum_Works()
    {
        Assert.AreEqual(ByteEnum.C, ByteEnum.A.Or(ByteEnum.B));
        Assert.AreEqual(ByteEnum.A, ByteEnum.A.Or(ByteEnum.None));
        Assert.AreEqual(ByteEnum.All, ByteEnum.All.Or(ByteEnum.None));
        Assert.AreEqual(ByteEnum.All, ByteEnum.All.Or(ByteEnum.A));
        Assert.AreEqual(ByteEnum.None, ByteEnum.None.Or(ByteEnum.None));
    }

    [TestMethod]
    public void Or_UShortEnum_Works()
    {
        Assert.AreEqual(UShortEnum.C, UShortEnum.A.Or(UShortEnum.B));
        Assert.AreEqual(UShortEnum.A, UShortEnum.A.Or(UShortEnum.None));
        Assert.AreEqual(UShortEnum.All, UShortEnum.All.Or(UShortEnum.None));
        Assert.AreEqual(UShortEnum.All, UShortEnum.All.Or(UShortEnum.A));
        Assert.AreEqual(UShortEnum.None, UShortEnum.None.Or(UShortEnum.None));
    }

    [TestMethod]
    public void Or_UIntEnum_Works()
    {
        Assert.AreEqual(UIntEnum.C, UIntEnum.A.Or(UIntEnum.B));
        Assert.AreEqual(UIntEnum.A, UIntEnum.A.Or(UIntEnum.None));
        Assert.AreEqual(UIntEnum.All, UIntEnum.All.Or(UIntEnum.None));
        Assert.AreEqual(UIntEnum.All, UIntEnum.All.Or(UIntEnum.A));
        Assert.AreEqual(UIntEnum.None, UIntEnum.None.Or(UIntEnum.None));
    }

    [TestMethod]
    public void Or_ULongEnum_Works()
    {
        Assert.AreEqual(ULongEnum.C, ULongEnum.A.Or(ULongEnum.B));
        Assert.AreEqual(ULongEnum.A, ULongEnum.A.Or(ULongEnum.None));
        Assert.AreEqual(ULongEnum.All, ULongEnum.All.Or(ULongEnum.None));
        Assert.AreEqual(ULongEnum.All, ULongEnum.All.Or(ULongEnum.A));
        Assert.AreEqual(ULongEnum.None, ULongEnum.None.Or(ULongEnum.None));
    }

    [TestMethod]
    public void Or_ImplicitIntEnum_Works()
    {
        Assert.AreEqual(ImplicitIntEnum.C, ImplicitIntEnum.A.Or(ImplicitIntEnum.B));
        Assert.AreEqual(ImplicitIntEnum.A, ImplicitIntEnum.A.Or(ImplicitIntEnum.None));
        Assert.AreEqual(ImplicitIntEnum.All, ImplicitIntEnum.All.Or(ImplicitIntEnum.None));
        Assert.AreEqual(ImplicitIntEnum.All, ImplicitIntEnum.All.Or(ImplicitIntEnum.A));
        Assert.AreEqual(ImplicitIntEnum.None, ImplicitIntEnum.None.Or(ImplicitIntEnum.None));
    }

    [TestMethod]
    public void Xor_ByteEnum_Works()
    {
        Assert.AreEqual(ByteEnum.C, ByteEnum.A.Xor(ByteEnum.B));
        Assert.AreEqual(ByteEnum.None, ByteEnum.A.Xor(ByteEnum.A));
        Assert.AreEqual(ByteEnum.All, ByteEnum.None.Xor(ByteEnum.All));
        Assert.AreEqual(ByteEnum.All, ByteEnum.All.Xor(ByteEnum.None));
        Assert.AreEqual(ByteEnum.B, ByteEnum.C.Xor(ByteEnum.A));
        Assert.AreEqual(ByteEnum.A, ByteEnum.All.Xor(ByteEnum.A).Not());
    }

    [TestMethod]
    public void Xor_UShortEnum_Works()
    {
        Assert.AreEqual(UShortEnum.C, UShortEnum.A.Xor(UShortEnum.B));
        Assert.AreEqual(UShortEnum.None, UShortEnum.A.Xor(UShortEnum.A));
        Assert.AreEqual(UShortEnum.All, UShortEnum.None.Xor(UShortEnum.All));
        Assert.AreEqual(UShortEnum.All, UShortEnum.All.Xor(UShortEnum.None));
        Assert.AreEqual(UShortEnum.B, UShortEnum.C.Xor(UShortEnum.A));
        Assert.AreEqual(UShortEnum.A, UShortEnum.All.Xor(UShortEnum.A).Not());
    }

    [TestMethod]
    public void Xor_UIntEnum_Works()
    {
        Assert.AreEqual(UIntEnum.C, UIntEnum.A.Xor(UIntEnum.B));
        Assert.AreEqual(UIntEnum.None, UIntEnum.A.Xor(UIntEnum.A));
        Assert.AreEqual(UIntEnum.All, UIntEnum.None.Xor(UIntEnum.All));
        Assert.AreEqual(UIntEnum.All, UIntEnum.All.Xor(UIntEnum.None));
        Assert.AreEqual(UIntEnum.B, UIntEnum.C.Xor(UIntEnum.A));
        Assert.AreEqual(UIntEnum.A, UIntEnum.All.Xor(UIntEnum.A).Not());
    }

    [TestMethod]
    public void Xor_ULongEnum_Works()
    {
        Assert.AreEqual(ULongEnum.C, ULongEnum.A.Xor(ULongEnum.B));
        Assert.AreEqual(ULongEnum.None, ULongEnum.A.Xor(ULongEnum.A));
        Assert.AreEqual(ULongEnum.All, ULongEnum.None.Xor(ULongEnum.All));
        Assert.AreEqual(ULongEnum.All, ULongEnum.All.Xor(ULongEnum.None));
        Assert.AreEqual(ULongEnum.B, ULongEnum.C.Xor(ULongEnum.A));
        Assert.AreEqual(ULongEnum.A, ULongEnum.All.Xor(ULongEnum.A).Not());
    }

    [TestMethod]
    public void Xor_ImplicitIntEnum_Works()
    {
        Assert.AreEqual(ImplicitIntEnum.C, ImplicitIntEnum.A.Xor(ImplicitIntEnum.B));
        Assert.AreEqual(ImplicitIntEnum.None, ImplicitIntEnum.A.Xor(ImplicitIntEnum.A));
        Assert.AreEqual(ImplicitIntEnum.All, ImplicitIntEnum.None.Xor(ImplicitIntEnum.All));
        Assert.AreEqual(ImplicitIntEnum.All, ImplicitIntEnum.All.Xor(ImplicitIntEnum.None));
        Assert.AreEqual(ImplicitIntEnum.B, ImplicitIntEnum.C.Xor(ImplicitIntEnum.A));
        Assert.AreEqual(ImplicitIntEnum.A, ImplicitIntEnum.All.Xor(ImplicitIntEnum.A).Not());
    }

    [TestMethod]
    public void Not_ByteEnum_Works()
    {
        unchecked
        {
            Assert.AreEqual(~ByteEnum.A, ByteEnum.A.Not());
            Assert.AreEqual(ByteEnum.All, ByteEnum.None.Not());
            Assert.AreEqual(ByteEnum.None, ByteEnum.All.Not());
            Assert.AreEqual(ByteEnum.C, ByteEnum.All.Xor(ByteEnum.C).Not());
        }
    }

    [TestMethod]
    public void Not_UShortEnum_Works()
    {
        unchecked
        {
            Assert.AreEqual(~UShortEnum.A, UShortEnum.A.Not());
            Assert.AreEqual(UShortEnum.All, UShortEnum.None.Not());
            Assert.AreEqual(UShortEnum.None, UShortEnum.All.Not());
            Assert.AreEqual(UShortEnum.C, UShortEnum.All.Xor(UShortEnum.C).Not());
        }
    }

    [TestMethod]
    public void Not_UIntEnum_Works()
    {
        unchecked
        {
            Assert.AreEqual(~UIntEnum.A, UIntEnum.A.Not());
            Assert.AreEqual(UIntEnum.All, UIntEnum.None.Not());
            Assert.AreEqual(UIntEnum.None, UIntEnum.All.Not());
            Assert.AreEqual(UIntEnum.C, UIntEnum.All.Xor(UIntEnum.C).Not());
        }
    }

    [TestMethod]
    public void Not_ULongEnum_Works()
    {
        unchecked
        {
            Assert.AreEqual(~ULongEnum.A, ULongEnum.A.Not());
            Assert.AreEqual(ULongEnum.All, ULongEnum.None.Not());
            Assert.AreEqual(ULongEnum.None, ULongEnum.All.Not());
            Assert.AreEqual(ULongEnum.C, ULongEnum.All.Xor(ULongEnum.C).Not());
        }
    }

    [TestMethod]
    public void Not_ImplicitIntEnum_Works()
    {
        unchecked
        {
            Assert.AreEqual(~ImplicitIntEnum.A, ImplicitIntEnum.A.Not());
            Assert.AreEqual(ImplicitIntEnum.All, ImplicitIntEnum.None.Not());
            Assert.AreEqual(ImplicitIntEnum.None, ImplicitIntEnum.All.Not());
            Assert.AreEqual(ImplicitIntEnum.C, ImplicitIntEnum.All.Xor(ImplicitIntEnum.C).Not());
        }
    }

    [TestMethod]
    public void FastEquals_ByteEnum_Works()
    {
        Assert.IsTrue(ByteEnum.A.FastEquals(ByteEnum.A));
        Assert.IsFalse(ByteEnum.A.FastEquals(ByteEnum.B));
        Assert.IsTrue(ByteEnum.All.FastEquals(ByteEnum.All));
        Assert.IsFalse(ByteEnum.All.FastEquals(ByteEnum.None));
        Assert.IsTrue(ByteEnum.None.FastEquals(ByteEnum.All.Not()));
    }

    [TestMethod]
    public void FastEquals_UShortEnum_Works()
    {
        Assert.IsTrue(UShortEnum.A.FastEquals(UShortEnum.A));
        Assert.IsFalse(UShortEnum.A.FastEquals(UShortEnum.B));
        Assert.IsTrue(UShortEnum.All.FastEquals(UShortEnum.All));
        Assert.IsFalse(UShortEnum.All.FastEquals(UShortEnum.None));
        Assert.IsTrue(UShortEnum.None.FastEquals(UShortEnum.All.Not()));
    }

    [TestMethod]
    public void FastEquals_UIntEnum_Works()
    {
        Assert.IsTrue(UIntEnum.A.FastEquals(UIntEnum.A));
        Assert.IsFalse(UIntEnum.A.FastEquals(UIntEnum.B));
        Assert.IsTrue(UIntEnum.All.FastEquals(UIntEnum.All));
        Assert.IsFalse(UIntEnum.All.FastEquals(UIntEnum.None));
        Assert.IsTrue(UIntEnum.None.FastEquals(UIntEnum.All.Not()));
    }

    [TestMethod]
    public void FastEquals_ULongEnum_Works()
    {
        Assert.IsTrue(ULongEnum.A.FastEquals(ULongEnum.A));
        Assert.IsFalse(ULongEnum.A.FastEquals(ULongEnum.B));
        Assert.IsTrue(ULongEnum.All.FastEquals(ULongEnum.All));
        Assert.IsFalse(ULongEnum.All.FastEquals(ULongEnum.None));
        Assert.IsTrue(ULongEnum.None.FastEquals(ULongEnum.All.Not()));
    }

    [TestMethod]
    public void FastEquals_ImplicitIntEnum_Works()
    {
        Assert.IsTrue(ImplicitIntEnum.A.FastEquals(ImplicitIntEnum.A));
        Assert.IsFalse(ImplicitIntEnum.A.FastEquals(ImplicitIntEnum.B));
        Assert.IsTrue(ImplicitIntEnum.All.FastEquals(ImplicitIntEnum.All));
        Assert.IsFalse(ImplicitIntEnum.All.FastEquals(ImplicitIntEnum.None));
        Assert.IsTrue(ImplicitIntEnum.None.FastEquals(ImplicitIntEnum.All.Not()));
    }
}

// Sample enums for each supported size
[Flags] file enum ByteEnum : byte { None = 0, A = 1, B = 2, C = 3, All = byte.MaxValue }
[Flags] file enum UShortEnum : ushort { None = 0, A = 1, B = 2, C = 3, All = ushort.MaxValue }
[Flags] file enum UIntEnum : uint { None = 0, A = 1, B = 2, C = 3, All = uint.MaxValue }
[Flags] file enum ULongEnum : ulong { None = 0, A = 1, B = 2, C = 3, All = ulong.MaxValue }
[Flags] file enum ImplicitIntEnum { None = 0, A = 1, B = 2, C = 3, All = -1 } // int is 4 bytes
