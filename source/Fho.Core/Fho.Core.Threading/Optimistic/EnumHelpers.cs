using System.Runtime.CompilerServices;

namespace Fho.Core.Threading.Optimistic;

/// <summary>
/// Provides extension methods for performing bitwise operations on generic enum types.
/// </summary>
public static class EnumHelpers
{
    extension<TEnum>(TEnum self) where TEnum : unmanaged, Enum
    {
        /// <summary>
        /// Performs a bitwise AND operation between two enum values of the same type.
        /// </summary>
        /// <param name="other">The second operand.</param>
        /// <returns><c>self & other</c></returns>
        public TEnum And(TEnum other) => Unsafe.SizeOf<TEnum>() switch
        {
            // note: switch expression will be optimized away at JIT time when TEnum is known
            1 => Unsafe.BitCast<byte, TEnum>((byte)(Unsafe.BitCast<TEnum, byte>(self) & Unsafe.BitCast<TEnum, byte>(other))),
            2 => Unsafe.BitCast<ushort, TEnum>((ushort)(Unsafe.BitCast<TEnum, ushort>(self) & Unsafe.BitCast<TEnum, ushort>(other))),
            4 => Unsafe.BitCast<uint, TEnum>(Unsafe.BitCast<TEnum, uint>(self) & Unsafe.BitCast<TEnum, uint>(other)),
            8 => Unsafe.BitCast<ulong, TEnum>(Unsafe.BitCast<TEnum, ulong>(self) & Unsafe.BitCast<TEnum, ulong>(other)),
            _ => throw UnsupportedEnumSize<TEnum>()
        };

        public TEnum Or(TEnum other) => Unsafe.SizeOf<TEnum>() switch
        {
            1 => Unsafe.BitCast<byte, TEnum>((byte)(Unsafe.BitCast<TEnum, byte>(self) | Unsafe.BitCast<TEnum, byte>(other))),
            2 => Unsafe.BitCast<ushort, TEnum>((ushort)(Unsafe.BitCast<TEnum, ushort>(self) | Unsafe.BitCast<TEnum, ushort>(other))),
            4 => Unsafe.BitCast<uint, TEnum>(Unsafe.BitCast<TEnum, uint>(self) | Unsafe.BitCast<TEnum, uint>(other)),
            8 => Unsafe.BitCast<ulong, TEnum>(Unsafe.BitCast<TEnum, ulong>(self) | Unsafe.BitCast<TEnum, ulong>(other)),
            _ => throw UnsupportedEnumSize<TEnum>()
        };

        public TEnum Xor(TEnum other) => Unsafe.SizeOf<TEnum>() switch
        {
            1 => Unsafe.BitCast<byte, TEnum>((byte)(Unsafe.BitCast<TEnum, byte>(self) ^ Unsafe.BitCast<TEnum, byte>(other))),
            2 => Unsafe.BitCast<ushort, TEnum>((ushort)(Unsafe.BitCast<TEnum, ushort>(self) ^ Unsafe.BitCast<TEnum, ushort>(other))),
            4 => Unsafe.BitCast<uint, TEnum>(Unsafe.BitCast<TEnum, uint>(self) ^ Unsafe.BitCast<TEnum, uint>(other)),
            8 => Unsafe.BitCast<ulong, TEnum>(Unsafe.BitCast<TEnum, ulong>(self) ^ Unsafe.BitCast<TEnum, ulong>(other)),
            _ => throw UnsupportedEnumSize<TEnum>()
        };

        public TEnum Not() => Unsafe.SizeOf<TEnum>() switch
        {
            1 => Unsafe.BitCast<byte, TEnum>((byte)~Unsafe.BitCast<TEnum, byte>(self)),
            2 => Unsafe.BitCast<ushort, TEnum>((ushort)~Unsafe.BitCast<TEnum, ushort>(self)),
            4 => Unsafe.BitCast<uint, TEnum>(~Unsafe.BitCast<TEnum, uint>(self)),
            8 => Unsafe.BitCast<ulong, TEnum>(~Unsafe.BitCast<TEnum, ulong>(self)),
            _ => throw UnsupportedEnumSize<TEnum>()
        };

        public bool FastEquals(TEnum other) => Unsafe.SizeOf<TEnum>() switch
        {
            1 => Unsafe.BitCast<TEnum, byte>(self) == Unsafe.BitCast<TEnum, byte>(other),
            2 => Unsafe.BitCast<TEnum, ushort>(self) == Unsafe.BitCast<TEnum, ushort>(other),
            4 => Unsafe.BitCast<TEnum, uint>(self) == Unsafe.BitCast<TEnum, uint>(other),
            8 => Unsafe.BitCast<TEnum, ulong>(self) == Unsafe.BitCast<TEnum, ulong>(other),
            _ => throw UnsupportedEnumSize<TEnum>()
        };
    }
    internal static InvalidOperationException UnsupportedEnumSize<TEnum>() where TEnum : unmanaged, Enum => new($"Unsupported enum size: {Unsafe.SizeOf<TEnum>()} bytes. Must be 1, 2, 4 or 8 bytes.");
}
