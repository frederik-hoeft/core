using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fho.Core.Threading.Optimistic;

/// <summary>
/// Represents a boolean value that can be used for CAS operations.
/// </summary>
[DebuggerDisplay("{ToString(),nq}")]
[StructLayout(LayoutKind.Explicit, Size = sizeof(uint))]
public readonly struct AtomicBoolean : IEquatable<AtomicBoolean>, IEqualityOperators<AtomicBoolean, AtomicBoolean, bool>
{
    [FieldOffset(0)]
    private readonly uint _value;

    /// <summary>
    /// Converts the specified <see cref="AtomicBoolean"/> to its equivalent <see cref="bool"/> value.
    /// </summary>
    public static explicit operator bool(AtomicBoolean value) => value._value != FALSE._value;

    /// <summary>
    /// Converts the specified <see cref="bool"/> value to its equivalent <see cref="AtomicBoolean"/>.
    /// </summary>
    public static implicit operator AtomicBoolean(bool value) => value ? TRUE : FALSE;

    /// <summary>
    /// Converts the specified <see cref="AtomicBoolean"/> to its equivalent <see cref="uint"/> value,
    /// where <see cref="FALSE"/> is <c>0</c> and <see cref="TRUE"/> is <c>0xffffffff</c> (<c>~FALSE</c>).
    /// </summary>
    public static explicit operator uint(AtomicBoolean value) =>
        Unsafe.BitCast<AtomicBoolean, uint>(value);

    /// <summary>
    /// Converts the specified <see cref="uint"/> value to its equivalent <see cref="AtomicBoolean"/>,
    /// where <c>0</c> is <see cref="FALSE"/> and any other value is <see cref="TRUE"/>.
    /// </summary>
    public static implicit operator AtomicBoolean(uint value) =>
        Unsafe.BitCast<uint, AtomicBoolean>((uint)(-value >> 63));

    public static bool operator ==(AtomicBoolean left, AtomicBoolean right) =>
        left._value == right._value;

    public static bool operator !=(AtomicBoolean left, AtomicBoolean right) =>
        left._value != right._value;

    /// <summary>
    /// Represents the <see cref="AtomicBoolean"/> value that is <see langword="false"/>.
    /// </summary>
    public static AtomicBoolean FALSE => 0u;

    /// <summary>
    /// Represents the <see cref="AtomicBoolean"/> value that is <see langword="true"/>.
    /// </summary>
    public static AtomicBoolean TRUE => ~FALSE._value;

    /// <summary>
    /// Returns the string representation of the <see cref="AtomicBoolean"/>.
    /// </summary>
    public override string ToString() => ((bool)this).ToString();

    public bool Equals(AtomicBoolean other) => (bool)this == (bool)other;

    public override bool Equals([NotNullWhen(true)] object? obj) =>
        obj is AtomicBoolean other && Equals(other);

    public override int GetHashCode() => ((bool)this).GetHashCode();
}
