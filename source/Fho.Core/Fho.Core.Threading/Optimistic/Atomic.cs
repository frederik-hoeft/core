using Fho.Core.Extensions;
using Fho.Core.Threading.Optimistic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Fho.Core.Threading.Optimistic;

/// <summary>
/// Provides additional atomic operations, complementing the <see cref="Interlocked"/> API.
/// </summary>
/// <remarks>
/// Operations in this class are guaranteed to be lock-free, and exhibit atomic behavior to the calling thread.
/// </remarks>
public static class Atomic
{
    #region Interlocked

    #region ConcurrentBoolean

    /// <summary>
    /// Sets a <see cref="AtomicBoolean"/> to the specified value and returns the original value, as an atomic operation.
    /// </summary>
    /// <param name="location1">The variable to set to the specified value.</param>
    /// <param name="value">The value to which the <paramref name="location1"/> parameter is set.</param>
    /// <returns>The original value of <paramref name="location1"/>.</returns>
    /// <exception cref="NullReferenceException">The address of <paramref name="location1"/> is a null pointer.</exception>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Exchange(ref AtomicBoolean location1, bool value) =>
        Interlocked.Exchange(ref Unsafe.As<AtomicBoolean, uint>(ref location1), (uint)(AtomicBoolean)value) != AtomicBoolean.FALSE;

    /// <summary>
    /// Compares two <see cref="AtomicBoolean"/> values for equality and, if they are equal, replaces the first value, as an atomic operation.
    /// </summary>
    /// <param name="location1">The destination, whose value is compared with <paramref name="comparand"/> and possibly replaced.</param>
    /// <param name="value">The value that replaces the destination value if the comparison results in equality.</param>
    /// <param name="comparand">The value that is compared to the value at <paramref name="location1"/>.</param>
    /// <returns>The original value in <paramref name="location1"/>.</returns>
    /// <exception cref="NullReferenceException">The address of <paramref name="location1"/> is a null pointer.</exception>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CompareExchange(ref AtomicBoolean location1, bool value, bool comparand) =>
        Interlocked.CompareExchange(ref Unsafe.As<AtomicBoolean, uint>(ref location1), (uint)(AtomicBoolean)value, (uint)(AtomicBoolean)comparand) != AtomicBoolean.FALSE;

    #endregion ConcurrentBoolean
    #region Enum

    /// <summary>
    /// Sets a variable of the specified type <typeparamref name="TEnum"/> to a specified value and returns the original value, as an atomic operation.
    /// </summary>
    /// <typeparam name="TEnum">The type to be used for <paramref name="location1"/> and <paramref name="value"/>. 
    /// This type must be an enum type whose underlying size is 32 or 64 bits.</typeparam>
    /// <param name="location1">The variable to set to the specified value. This is a reference parameter (<see langword="ref"/> in C#, <c>ByRef</c> in Visual Basic).</param>
    /// <param name="value">The value to which the <paramref name="location1"/> parameter is set.</param>
    /// <returns>The original value of <paramref name="location1"/>.</returns>
    /// <exception cref="NullReferenceException">The address of <paramref name="location1"/> is a null pointer.</exception>
    /// <exception cref="NotSupportedException">An unsupported <typeparamref name="TEnum"/> is specified.</exception>
    [DebuggerStepThrough]
    public static TEnum Exchange<TEnum>(ref TEnum location1, TEnum value) where TEnum : unmanaged, Enum => Unsafe.SizeOf<TEnum>() switch
    {
        // since the size of TEnum is known at compile time, the switch statement is optimized away by the JIT
        1 => Unsafe.BitCast<byte, TEnum>(Interlocked.Exchange(ref Unsafe.As<TEnum, byte>(ref location1), Unsafe.BitCast<TEnum, byte>(value))),
        2 => Unsafe.BitCast<ushort, TEnum>(Interlocked.Exchange(ref Unsafe.As<TEnum, ushort>(ref location1), Unsafe.BitCast<TEnum, ushort>(value))),
        4 => Unsafe.BitCast<uint, TEnum>(Interlocked.Exchange(ref Unsafe.As<TEnum, uint>(ref location1), Unsafe.BitCast<TEnum, uint>(value))),
        8 => Unsafe.BitCast<ulong, TEnum>(Interlocked.Exchange(ref Unsafe.As<TEnum, ulong>(ref location1), Unsafe.BitCast<TEnum, ulong>(value))),
        _ => throw EnumHelpers.UnsupportedEnumSize<TEnum>()
    };

    /// <summary>
    /// Compares two <typeparamref name="TEnum"/> values for equality and, if they are equal, replaces the first value, as an atomic operation.
    /// </summary>
    /// <typeparam name="TEnum">The type to be used for <paramref name="location1"/>, <paramref name="value"/>, and <paramref name="comparand"/>. 
    /// This type must be an enum type whose underlying size is 32 or 64 bits.</typeparam>
    /// <param name="location1">The destination, whose value is compared with <paramref name="comparand"/> and possibly replaced.</param>
    /// <param name="value">The value that replaces the destination value if the comparison results in equality.</param>
    /// <param name="comparand">The value that is compared to the value at <paramref name="location1"/>.</param>
    /// <returns>The original value in <paramref name="location1"/>.</returns>
    /// <exception cref="NullReferenceException">The address of <paramref name="location1"/> is a null pointer.</exception>
    /// <exception cref="NotSupportedException">An unsupported <typeparamref name="TEnum"/> is specified.</exception>
    [DebuggerStepThrough]
    public static TEnum CompareExchange<TEnum>(ref TEnum location1, TEnum value, TEnum comparand) where TEnum : unmanaged, Enum => Unsafe.SizeOf<TEnum>() switch
    {
        // since the size of TEnum is known at compile time, the switch statement is optimized away by the JIT
        1 => Unsafe.BitCast<byte, TEnum>(Interlocked.CompareExchange(ref Unsafe.As<TEnum, byte>(ref location1), Unsafe.BitCast<TEnum, byte>(value), Unsafe.BitCast<TEnum, byte>(comparand))),
        2 => Unsafe.BitCast<ushort, TEnum>(Interlocked.CompareExchange(ref Unsafe.As<TEnum, ushort>(ref location1), Unsafe.BitCast<TEnum, ushort>(value), Unsafe.BitCast<TEnum, ushort>(comparand))),
        4 => Unsafe.BitCast<uint, TEnum>(Interlocked.CompareExchange(ref Unsafe.As<TEnum, uint>(ref location1), Unsafe.BitCast<TEnum, uint>(value), Unsafe.BitCast<TEnum, uint>(comparand))),
        8 => Unsafe.BitCast<ulong, TEnum>(Interlocked.CompareExchange(ref Unsafe.As<TEnum, ulong>(ref location1), Unsafe.BitCast<TEnum, ulong>(value), Unsafe.BitCast<TEnum, ulong>(comparand))),
        _ => throw EnumHelpers.UnsupportedEnumSize<TEnum>()
    };

    #endregion Enum
    #endregion Interlocked

    #region Read

    /// <inheritdoc cref="Volatile.Read(ref readonly bool)"/>
    [DebuggerStepThrough]
    public static bool VolatileRead(ref readonly AtomicBoolean location) =>
        Volatile.Read(ref Unsafe.As<AtomicBoolean, uint>(ref Unsafe.AsRef(in location))) != AtomicBoolean.FALSE;

    /// <inheritdoc cref="Volatile.Read(ref readonly bool)"/>
    [DebuggerStepThrough]
    public static TEnum VolatileRead<TEnum>(ref readonly TEnum location) where TEnum : unmanaged, Enum => Unsafe.SizeOf<TEnum>() switch
    {
        1 => Unsafe.BitCast<byte, TEnum>(Volatile.Read(ref Unsafe.As<TEnum, byte>(ref Unsafe.AsRef(in location)))),
        2 => Unsafe.BitCast<ushort, TEnum>(Volatile.Read(ref Unsafe.As<TEnum, ushort>(ref Unsafe.AsRef(in location)))),
        4 => Unsafe.BitCast<uint, TEnum>(Volatile.Read(ref Unsafe.As<TEnum, uint>(ref Unsafe.AsRef(in location)))),
        8 => Unsafe.BitCast<ulong, TEnum>(Volatile.Read(ref Unsafe.As<TEnum, ulong>(ref Unsafe.AsRef(in location)))),
        _ => throw EnumHelpers.UnsupportedEnumSize<TEnum>(),
    };

    #endregion Read

    #region IncrementModulo

    /// <summary>
    /// Atomically increments the value stored in the specified location and wraps it around to zero if it exceeds the specified modulo value.
    /// </summary>
    /// <param name="location">A reference to the integer value to increment.</param>
    /// <param name="modulo">The modulo value. If the incremented value exceeds this modulo, it wraps around to zero.</param>
    /// <returns>The original value stored in <paramref name="location"/> before incrementing.</returns>
    [DebuggerStepThrough]
    public static int IncrementModulo(ref int location, int modulo)
    {
        int original, newValue;
        do
        {
            original = Volatile.Read(ref location);
            newValue = (original + 1) % modulo;
        }
        while (Interlocked.CompareExchange(ref location, newValue, original) != original);
        return original;
    }

    /// <inheritdoc cref="IncrementModulo(ref int, int)"/>
    [DebuggerStepThrough]
    public static uint IncrementModulo(ref uint location, uint modulo)
    {
        uint original, newValue;
        do
        {
            original = Volatile.Read(ref location);
            newValue = (original + 1) % modulo;
        }
        while (Interlocked.CompareExchange(ref location, newValue, original) != original);
        return original;
    }

    /// <inheritdoc cref="IncrementModulo(ref int, int)"/>
    [DebuggerStepThrough]
    public static long IncrementModulo(ref long location, long modulo)
    {
        long original, newValue;
        do
        {
            original = Volatile.Read(ref location);
            newValue = (original + 1) % modulo;
        }
        while (Interlocked.CompareExchange(ref location, newValue, original) != original);
        return original;
    }

    /// <inheritdoc cref="IncrementModulo(ref int, int)"/>
    [DebuggerStepThrough]
    public static ulong IncrementModulo(ref ulong location, ulong modulo)
    {
        ulong original, newValue;
        do
        {
            original = Volatile.Read(ref location);
            newValue = (original + 1) % modulo;
        }
        while (Interlocked.CompareExchange(ref location, newValue, original) != original);
        return original;
    }

    #endregion

    #region WriteMax

    /// <summary>
    /// Atomically writes the maximum of the value stored in the specified location and the specified value.
    /// </summary>
    /// <remarks>
    /// The input values must satisfy the following precondition:
    /// <c>int.MinValue &lt;= location - value - 1 &lt;= int.MaxValue</c>
    /// </remarks>
    /// <param name="location">A reference to the integer value to write to.</param>
    /// <param name="value">The value to write if it is greater than the value stored in <paramref name="location"/>.</param>
    /// <returns>The original value stored in <paramref name="location"/> before writing.</returns>
    [DebuggerStepThrough]
    public static int WriteMaxFast(ref int location, int value)
    {
        int original, newValue;
        do
        {
            original = Volatile.Read(ref location);
            newValue = Math.FastMax(original, value);
        }
        while (Interlocked.CompareExchange(ref location, newValue, original) != original);
        return original;
    }

    /// <summary>
    /// Atomically writes the maximum of the value stored in the specified location and the specified value.
    /// </summary>
    /// <param name="location">A reference to the integer value to write to.</param>
    /// <param name="value">The value to write if it is greater than the value stored in <paramref name="location"/>.</param>
    /// <returns>The original value stored in <paramref name="location"/> before writing.</returns>
    [DebuggerStepThrough]
    public static int WriteMax(ref int location, int value)
    {
        int original, newValue;
        do
        {
            original = Volatile.Read(ref location);
            newValue = Math.Max(original, value);
        }
        while (Interlocked.CompareExchange(ref location, newValue, original) != original);
        return original;
    }

    /// <inheritdoc cref="WriteMax(ref int, int)"/>
    [DebuggerStepThrough]
    public static uint WriteMax(ref uint location, uint value)
    {
        uint original, newValue;
        do
        {
            original = Volatile.Read(ref location);
            newValue = Math.Max(original, value);
        }
        while (Interlocked.CompareExchange(ref location, newValue, original) != original);
        return original;
    }

    /// <inheritdoc cref="WriteMax(ref int, int)"/>
    [DebuggerStepThrough]
    public static long WriteMax(ref long location, long value)
    {
        long original, newValue;
        do
        {
            original = Volatile.Read(ref location);
            newValue = Math.Max(original, value);
        }
        while (Interlocked.CompareExchange(ref location, newValue, original) != original);
        return original;
    }

    /// <inheritdoc cref="WriteMax(ref int, int)"/>
    [DebuggerStepThrough]
    public static ulong WriteMax(ref ulong location, ulong value)
    {
        ulong original, newValue;
        do
        {
            original = Volatile.Read(ref location);
            newValue = Math.Max(original, value);
        }
        while (Interlocked.CompareExchange(ref location, newValue, original) != original);
        return original;
    }

    #endregion WriteMax

    #region IncrementClampMax

    /// <summary>
    /// Atomically increments the value stored in the specified location, but only if the incremented value is less than or equal to the specified maximum value.
    /// </summary>
    /// <remarks>
    /// The input values must satisfy the following precondition:
    /// <c>int.MinValue &lt;= location - maxValue + 1 &lt;= int.MaxValue</c>
    /// </remarks>
    /// <param name="location">A reference to the integer value to increment.</param>
    /// <param name="maxValue">The maximum value. If the incremented value would exceed this value, it is clamped to this value.</param>
    /// <returns>The original value stored in <paramref name="location"/> before incrementing.</returns>
    [DebuggerStepThrough]
    public static int IncrementClampMaxFast(ref int location, int maxValue)
    {
        int original, incremented;
        do
        {
            original = Volatile.Read(ref location);
            // believe it or not, this branchless version alone eliminates two branches and three jump labels
            // in the optimized x64 JIT assembly :0
            incremented = Math.FastMin(original + 1, maxValue);
        }
        while (Interlocked.CompareExchange(ref location, incremented, original) != original);
        return original;
    }

    /// <summary>
    /// Atomically increments the value stored in the specified location, but only if the incremented value is less than or equal to the specified maximum value.
    /// </summary>
    /// <param name="location">A reference to the integer value to increment.</param>
    /// <param name="maxValue">The maximum value. If the incremented value would exceed this value, it is clamped to this value.</param>
    /// <returns>The original value stored in <paramref name="location"/> before incrementing.</returns>
    [DebuggerStepThrough]
    public static int IncrementClampMax(ref int location, int maxValue)
    {
        int original, incremented;
        do
        {
            original = Volatile.Read(ref location);
            incremented = Math.Min(original + 1, maxValue);
        }
        while (Interlocked.CompareExchange(ref location, incremented, original) != original);
        return original;
    }

    /// <inheritdoc cref="IncrementClampMax(ref int, int)"/>
    [DebuggerStepThrough]
    public static uint IncrementClampMax(ref uint location, uint maxValue)
    {
        uint original, incremented;
        do
        {
            original = Volatile.Read(ref location);
            incremented = Math.Min(original + 1, maxValue);
        }
        while (Interlocked.CompareExchange(ref location, incremented, original) != original);
        return original;
    }

    /// <inheritdoc cref="IncrementClampMax(ref int, int)"/>
    [DebuggerStepThrough]
    public static long IncrementClampMax(ref long location, long maxValue)
    {
        long original, incremented;
        do
        {
            original = Volatile.Read(ref location);
            incremented = Math.Min(original + 1, maxValue);
        }
        while (Interlocked.CompareExchange(ref location, incremented, original) != original);
        return original;
    }

    /// <inheritdoc cref="IncrementClampMax(ref int, int)"/>
    [DebuggerStepThrough]
    public static ulong IncrementClampMax(ref ulong location, ulong maxValue)
    {
        ulong original, incremented;
        do
        {
            original = Volatile.Read(ref location);
            incremented = Math.Min(original + 1, maxValue);
        }
        while (Interlocked.CompareExchange(ref location, incremented, original) != original);
        return original;
    }

    #endregion IncrementClampMax

    #region DecrementClampMin

    /// <summary>
    /// Atomically decrements the value stored in the specified location, but only if the decremented value is greater than or equal to the specified minimum value.
    /// </summary>
    /// <remarks>
    /// The input values must satisfy the following precondition:
    /// <c>int.MinValue &lt;= location - minValue - 1 &lt;= int.MaxValue</c>
    /// </remarks>
    /// <param name="location">A reference to the integer value to decrement.</param>
    /// <param name="minValue">The minimum value. If the decremented value would be less than this value, it is clamped to this value.</param>
    /// <returns>The original value stored in <paramref name="location"/> before decrementing.</returns>
    [DebuggerStepThrough]
    public static int DecrementClampMinFast(ref int location, int minValue)
    {
        int original, decremented;
        do
        {
            original = Volatile.Read(ref location);
            decremented = Math.FastMax(original - 1, minValue);
        }
        while (Interlocked.CompareExchange(ref location, decremented, original) != original);
        return original;
    }

    /// <summary>
    /// Atomically decrements the value stored in the specified location, but only if the decremented value is greater than or equal to the specified minimum value.
    /// </summary>
    /// <param name="location">A reference to the integer value to decrement.</param>
    /// <param name="minValue">The minimum value. If the decremented value would be less than this value, it is clamped to this value.</param>
    /// <returns>The original value stored in <paramref name="location"/> before decrementing.</returns>
    [DebuggerStepThrough]
    public static int DecrementClampMin(ref int location, int minValue)
    {
        int original, decremented;
        do
        {
            original = Volatile.Read(ref location);
            decremented = Math.Max(original - 1, minValue);
        }
        while (Interlocked.CompareExchange(ref location, decremented, original) != original);
        return original;
    }

    /// <inheritdoc cref="DecrementClampMin(ref int, int)"/>
    [DebuggerStepThrough]
    public static uint DecrementClampMin(ref uint location, uint minValue)
    {
        uint original, decremented;
        do
        {
            original = Volatile.Read(ref location);
            decremented = Math.Max(original - 1, minValue);
        }
        while (Interlocked.CompareExchange(ref location, decremented, original) != original);
        return original;
    }

    /// <inheritdoc cref="DecrementClampMin(ref int, int)"/>
    [DebuggerStepThrough]
    public static long DecrementClampMin(ref long location, long minValue)
    {
        long original, decremented;
        do
        {
            original = Volatile.Read(ref location);
            decremented = Math.Max(original - 1, minValue);
        }
        while (Interlocked.CompareExchange(ref location, decremented, original) != original);
        return original;
    }

    /// <inheritdoc cref="DecrementClampMin(ref int, int)"/>
    [DebuggerStepThrough]
    public static ulong DecrementClampMin(ref ulong location, ulong minValue)
    {
        ulong original, decremented;
        do
        {
            original = Volatile.Read(ref location);
            decremented = Math.Max(original - 1, minValue);
        }
        while (Interlocked.CompareExchange(ref location, decremented, original) != original);
        return original;
    }

    #endregion DecrementClampMin

    #region TestAllFlagsExchange

    /// <summary>
    /// Tests whether the specified flags are set in the specified location (<c>(location &amp; flags) == flags</c>), and if so, 
    /// replaces the value stored in that location with the specified value.
    /// </summary>
    /// <param name="location">The location to test and exchange.</param>
    /// <param name="value">The value to exchange.</param>
    /// <param name="flags">The flags to test for.</param>
    /// <returns>The original value stored in <paramref name="location"/>.</returns>
    [DebuggerStepThrough]
    public static int TestAllFlagsExchange(ref int location, int value, int flags)
    {
        bool isFlagSet;
        int original;
        do
        {
            original = Volatile.Read(ref location);
            isFlagSet = (original & flags) == flags;
        }
        while (isFlagSet && Interlocked.CompareExchange(ref location, value, original) != original);
        return original;
    }

    /// <inheritdoc cref="TestAllFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint TestAllFlagsExchange(ref uint location, uint value, uint flags) =>
        (uint)TestAllFlagsExchange(ref Unsafe.As<uint, int>(ref location), (int)value, (int)flags);

    /// <inheritdoc cref="TestAllFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    public static long TestAllFlagsExchange(ref long location, long value, long flags)
    {
        bool isFlagSet;
        long original;
        do
        {
            original = Volatile.Read(ref location);
            isFlagSet = (original & flags) == flags;
        }
        while (isFlagSet && Interlocked.CompareExchange(ref location, value, original) != original);
        return original;
    }

    /// <inheritdoc cref="TestAllFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong TestAllFlagsExchange(ref ulong location, ulong value, ulong flags) =>
        (ulong)TestAllFlagsExchange(ref Unsafe.As<ulong, long>(ref location), (long)value, (long)flags);

    /// <inheritdoc cref="TestAllFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    public static nint TestAllFlagsExchange(ref nint location, nint value, nint flags)
    {
        bool isFlagSet;
        nint original;
        do
        {
            original = Volatile.Read(ref location);
            isFlagSet = (original & flags) == flags;
        }
        while (isFlagSet && Interlocked.CompareExchange(ref location, value, original) != original);
        return original;
    }

    /// <inheritdoc cref="TestAllFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint TestAllFlagsExchange(ref nuint location, nuint value, nuint flags) =>
        (nuint)TestAllFlagsExchange(ref Unsafe.As<nuint, nint>(ref location), (nint)value, (nint)flags);

    /// <inheritdoc cref="TestAllFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    public static TEnum TestAllFlagsExchange<TEnum>(ref TEnum location, TEnum value, TEnum flags) where TEnum : unmanaged, Enum
    {
        bool isFlagSet;
        TEnum original;
        do
        {
            original = VolatileRead(ref location);
            isFlagSet = original.Or(flags).FastEquals(flags);
        }
        while (isFlagSet && !CompareExchange(ref location, value, original).FastEquals(original));
        return original;
    }

    #endregion TestAllFlagsExchange

    #region TryTestAllFlagsExchange

    /// <summary>
    /// Tests whether the specified flags is set in the specified location (<c>(location &amp; flags) == flags</c>), and if so, replaces the value stored in that location with the specified value.
    /// </summary>
    /// <param name="location">The location to test and exchange.</param>
    /// <param name="value">The value to exchange.</param>
    /// <param name="flags">The flags to test for.</param>
    /// <returns><see langword="true"/> if original value stored in <paramref name="location"/> was replaced; otherwise, <see langword="false"/>.</returns>
    [DebuggerStepThrough]
    public static bool TryTestAllFlagsExchange(ref int location, int value, int flags)
    {
        bool isFlagSet;
        int original;
        do
        {
            original = Volatile.Read(ref location);
            isFlagSet = (original & flags) == flags;
        }
        while (isFlagSet && Interlocked.CompareExchange(ref location, value, original) != original);
        return isFlagSet;
    }

    /// <inheritdoc cref="TryTestAllFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryTestAllFlagsExchange(ref uint location, uint value, uint flags) =>
        TryTestAllFlagsExchange(ref Unsafe.As<uint, int>(ref location), (int)value, (int)flags);

    /// <inheritdoc cref="TryTestAllFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    public static bool TryTestAllFlagsExchange(ref long location, long value, long flags)
    {
        bool isFlagSet;
        long original;
        do
        {
            original = Volatile.Read(ref location);
            isFlagSet = (original & flags) == flags;
        }
        while (isFlagSet && Interlocked.CompareExchange(ref location, value, original) != original);
        return isFlagSet;
    }

    /// <inheritdoc cref="TryTestAllFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryTestAllFlagsExchange(ref ulong location, ulong value, ulong flags) =>
        TryTestAllFlagsExchange(ref Unsafe.As<ulong, long>(ref location), (long)value, (long)flags);

    /// <inheritdoc cref="TryTestAllFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    public static bool TryTestAllFlagsExchange(ref nint location, nint value, nint flags)
    {
        bool isFlagSet;
        nint original;
        do
        {
            original = Volatile.Read(ref location);
            isFlagSet = (original & flags) == flags;
        }
        while (isFlagSet && Interlocked.CompareExchange(ref location, value, original) != original);
        return isFlagSet;
    }

    /// <inheritdoc cref="TryTestAllFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryTestAllFlagsExchange(ref nuint location, nuint value, nuint flags) =>
        TryTestAllFlagsExchange(ref Unsafe.As<nuint, nint>(ref location), (nint)value, (nint)flags);

    /// <inheritdoc cref="TryTestAllFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    public static bool TryTestAllFlagsExchange<TEnum>(ref TEnum location, TEnum value, TEnum flags) where TEnum : unmanaged, Enum
    {
        bool isFlagSet;
        TEnum original;
        do
        {
            original = VolatileRead(ref location);
            isFlagSet = original.Or(flags).FastEquals(flags);
        }
        while (isFlagSet && !CompareExchange(ref location, value, original).FastEquals(original));
        return isFlagSet;
    }

    #endregion TryTestAllFlagsExchange

    #region TestAnyFlagsExchange

    /// <summary>
    /// Tests whether any of the specified flags are set in the specified location (<c>(location &amp; flags) != 0</c>), and if so, replaces the value stored in that location with the specified value.
    /// </summary>
    /// <param name="location">The location to test and exchange.</param>
    /// <param name="value">The value to exchange.</param>
    /// <param name="flags">The flags to test for.</param>
    /// <returns>The original value stored in <paramref name="location"/>.</returns>
    [DebuggerStepThrough]
    public static int TestAnyFlagsExchange(ref int location, int value, int flags)
    {
        bool isFlagSet;
        int original;
        do
        {
            original = Volatile.Read(ref location);
            isFlagSet = (original & flags) != 0;
        }
        while (isFlagSet && Interlocked.CompareExchange(ref location, value, original) != original);
        return original;
    }

    /// <inheritdoc cref="TestAnyFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint TestAnyFlagsExchange(ref uint location, uint value, uint flags) =>
        (uint)TestAnyFlagsExchange(ref Unsafe.As<uint, int>(ref location), (int)value, (int)flags);

    /// <inheritdoc cref="TestAnyFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    public static long TestAnyFlagsExchange(ref long location, long value, long flags)
    {
        bool isFlagSet;
        long original;
        do
        {
            original = Volatile.Read(ref location);
            isFlagSet = (original & flags) != 0;
        }
        while (isFlagSet && Interlocked.CompareExchange(ref location, value, original) != original);
        return original;
    }

    /// <inheritdoc cref="TestAnyFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong TestAnyFlagsExchange(ref ulong location, ulong value, ulong flags) =>
        (ulong)TestAnyFlagsExchange(ref Unsafe.As<ulong, long>(ref location), (long)value, (long)flags);

    /// <inheritdoc cref="TestAnyFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    public static nint TestAnyFlagsExchange(ref nint location, nint value, nint flags)
    {
        bool isFlagSet;
        nint original;
        do
        {
            original = Volatile.Read(ref location);
            isFlagSet = (original & flags) != 0;
        }
        while (isFlagSet && Interlocked.CompareExchange(ref location, value, original) != original);
        return original;
    }

    /// <inheritdoc cref="TestAnyFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint TestAnyFlagsExchange(ref nuint location, nuint value, nuint flags) =>
        (nuint)TestAnyFlagsExchange(ref Unsafe.As<nuint, nint>(ref location), (nint)value, (nint)flags);

    /// <inheritdoc cref="TestAnyFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    public static TEnum TestAnyFlagsExchange<TEnum>(ref TEnum location, TEnum value, TEnum flags) where TEnum : unmanaged, Enum
    {
        bool isFlagSet;
        TEnum original;
        do
        {
            original = VolatileRead(ref location);
            isFlagSet = !original.And(flags).FastEquals(default);
        }
        while (isFlagSet && !CompareExchange(ref location, value, original).FastEquals(original));
        return original;
    }

    #endregion TestAnyFlagsExchange

    #region TryTestAnyFlagsExchange

    /// <summary>
    /// Tests whether any of the specified flags are set in the specified location (<c>(location &amp; flags) != 0</c>), and if so, replaces the value stored in that location with the specified value.
    /// </summary>
    /// <param name="location">The location to test and exchange.</param>
    /// <param name="value">The value to exchange.</param>
    /// <param name="flags">The flags to test for.</param>
    /// <returns><see langword="true"/> if original value stored in <paramref name="location"/> was replaced; otherwise, <see langword="false"/>.</returns>
    [DebuggerStepThrough]
    public static bool TryTestAnyFlagsExchange(ref int location, int value, int flags)
    {
        bool isFlagSet;
        int original;
        do
        {
            original = Volatile.Read(ref location);
            isFlagSet = (original & flags) != 0;
        }
        while (isFlagSet && Interlocked.CompareExchange(ref location, value, original) != original);
        return isFlagSet;
    }

    /// <inheritdoc cref="TryTestAnyFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryTestAnyFlagsExchange(ref uint location, uint value, uint flags) =>
        TryTestAnyFlagsExchange(ref Unsafe.As<uint, int>(ref location), (int)value, (int)flags);

    /// <inheritdoc cref="TryTestAnyFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    public static bool TryTestAnyFlagsExchange(ref long location, long value, long flags)
    {
        bool isFlagSet;
        long original;
        do
        {
            original = Volatile.Read(ref location);
            isFlagSet = (original & flags) != 0;
        }
        while (isFlagSet && Interlocked.CompareExchange(ref location, value, original) != original);
        return isFlagSet;
    }

    /// <inheritdoc cref="TryTestAnyFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryTestAnyFlagsExchange(ref ulong location, ulong value, ulong flags) =>
        TryTestAnyFlagsExchange(ref Unsafe.As<ulong, long>(ref location), (long)value, (long)flags);

    /// <inheritdoc cref="TryTestAnyFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    public static bool TryTestAnyFlagsExchange(ref nint location, nint value, nint flags)
    {
        bool isFlagSet;
        nint original;
        do
        {
            original = Volatile.Read(ref location);
            isFlagSet = (original & flags) != nint.Zero;
        }
        while (isFlagSet && Interlocked.CompareExchange(ref location, value, original) != original);
        return isFlagSet;
    }

    /// <inheritdoc cref="TryTestAnyFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryTestAnyFlagsExchange(ref nuint location, nuint value, nuint flags) =>
        TryTestAnyFlagsExchange(ref Unsafe.As<nuint, nint>(ref location), (nint)value, (nint)flags);

    /// <inheritdoc cref="TestAnyFlagsExchange(ref int, int, int)"/>
    [DebuggerStepThrough]
    public static bool TryTestAnyFlagsExchange<TEnum>(ref TEnum location, TEnum value, TEnum flags) where TEnum : unmanaged, Enum
    {
        bool isFlagSet;
        TEnum original;
        do
        {
            original = VolatileRead(ref location);
            isFlagSet = !original.And(flags).FastEquals(default);
        }
        while (isFlagSet && !CompareExchange(ref location, value, original).FastEquals(original));
        return isFlagSet;
    }

    /// <summary>
    /// Tests whether any of the specified flags are set in the specified location (<c>(location &amp; flags) != 0</c>), and if so, 
    /// runs the specified transformation function on the original value and replaces the value stored in that location with the transformed value.
    /// </summary>
    /// <param name="location">The location to test and exchange.</param>
    /// <param name="flags">The flags to test for.</param>
    /// <param name="transform">The transformation function to apply to the original value.</param>
    /// <returns><see langword="true"/> if original value stored in <paramref name="location"/> was replaced; otherwise, <see langword="false"/>.</returns>
    [DebuggerStepThrough]
    public static bool TryTestAnyFlagsTransform<TEnum>(ref TEnum location, TEnum flags, Func<TEnum, TEnum> transform) where TEnum : unmanaged, Enum
    {
        ArgumentNullException.ThrowIfNull(transform);
        bool isFlagSet;
        TEnum original;
        TEnum value;
        do
        {
            original = VolatileRead(ref location);
            value = transform(original);
            isFlagSet = !original.And(flags).FastEquals(default);
        }
        while (isFlagSet && !CompareExchange(ref location, value, original).FastEquals(original));
        return isFlagSet;
    }

    [DebuggerStepThrough]
    public static bool TryCompareTransform<TEnum>(ref TEnum location, Predicate<TEnum> predicate, Func<TEnum, TEnum> transform) where TEnum : unmanaged, Enum
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(transform);
        bool isSuccess;
        TEnum original;
        TEnum value;
        do
        {
            original = VolatileRead(ref location);
            value = transform(original);
            isSuccess = predicate(original);
        }
        while (isSuccess && !CompareExchange(ref location, value, original).FastEquals(original));
        return isSuccess;
    }

    #endregion TryTestAnyFlagsExchange

    #region BitOperations

    /// <inheritdoc cref="Interlocked.Or(ref int, int)"/>
    [DebuggerStepThrough]
    public static TEnum Or<TEnum>(ref TEnum location, TEnum value) where TEnum : unmanaged, Enum =>
        ApplyBitOperation(ref location, value, EnumHelpers.Or);

    /// <inheritdoc cref="Interlocked.And(ref int, int)"/>
    [DebuggerStepThrough]
    public static TEnum And<TEnum>(ref TEnum location, TEnum value) where TEnum : unmanaged, Enum =>
        ApplyBitOperation(ref location, value, EnumHelpers.And);

    /// <summary>
    /// Atomically performs a bitwise exclusive OR operation on the specified location and the specified value.
    /// </summary>
    /// <param name="location">A reference to the value to perform the operation on.</param>
    /// <param name="value">The value to perform the operation with.</param>
    /// <returns>The original value stored in <paramref name="location"/>.</returns>
    [DebuggerStepThrough]
    public static TEnum Xor<TEnum>(ref TEnum location, TEnum value) where TEnum : unmanaged, Enum =>
        ApplyBitOperation(ref location, value, EnumHelpers.Xor);

    /// <summary>
    /// Atomically transforms the value stored in the specified location using the specified transformation function.
    /// </summary>
    /// <param name="location">A reference to the value to transform.</param>
    /// <param name="transform">The transformation function to apply to the value.</param>
    /// <returns>The original value stored in <paramref name="location"/>.</returns>
    [DebuggerStepThrough]
    public static TEnum Transform<TEnum>(ref TEnum location, Func<TEnum, TEnum> transform) where TEnum : unmanaged, Enum
    {
        ArgumentNullException.ThrowIfNull(transform);
        TEnum original;
        TEnum newValue;
        do
        {
            original = VolatileRead(ref location);
            newValue = transform(original);
        }
        while (!CompareExchange(ref location, newValue, original).FastEquals(original));
        return original;
    }

    [DebuggerStepThrough]
    private static TEnum ApplyBitOperation<TEnum>(ref TEnum location, TEnum value, Func<TEnum, TEnum, TEnum> transform) where TEnum : unmanaged, Enum
    {
        TEnum original;
        TEnum newValue;
        do
        {
            original = VolatileRead(ref location);
            newValue = transform(original, value);
        }
        while (!CompareExchange(ref location, newValue, original).FastEquals(original));
        return original;
    }

    #endregion BitOperations
}
