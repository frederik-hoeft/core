using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Fho.Core.Extensions;

/// <summary>
/// Provides performance-oriented implementations of common math functions that are generally faster 
/// than the BCL implementations in the <see cref="Math"/> API, but some additional preconditions may apply.
/// </summary>
[DebuggerStepThrough]
[SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "C# 14 extension methods require a static class.")]
public static class MathExtensions
{
    extension(Math)
    {
        /// <summary>
        /// Calculates the minimum of <paramref name="x"/> and <paramref name="y"/> where <c>int.MinValue &lt;= x - y &lt;= int.MaxValue</c>
        /// </summary>
        /// <param name="x">x, where <c>int.MinValue &lt;= x - y &lt;= int.MaxValue</c></param>
        /// <param name="y">y, where <c>int.MinValue &lt;= x - y &lt;= int.MaxValue</c></param>
        /// <returns>The minimum of <paramref name="x"/> and <paramref name="y"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FastMin(int x, int y) =>
            y + ((x - y) & ((x - y) >> 31));

        /// <summary>
        /// Calculates the minimum of <paramref name="x"/> and <paramref name="y"/> where <c>long.MinValue &lt;= x - y &lt;= long.MaxValue</c>
        /// </summary>
        /// <param name="x">x, where <c>long.MinValue &lt;= x - y &lt;= long.MaxValue</c></param>
        /// <param name="y">y, where <c>long.MinValue &lt;= x - y &lt;= long.MaxValue</c></param>
        /// <returns>The minimum of <paramref name="x"/> and <paramref name="y"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long FastMin(long x, long y) =>
            y + ((x - y) & ((x - y) >> 63));

        /// <summary>
        /// Calculates the maximum of <paramref name="x"/> and <paramref name="y"/> where <c>int.MinValue &lt;= x - y &lt;= int.MaxValue</c>
        /// </summary>
        /// <param name="x">x, where <c>int.MinValue &lt;= x - y &lt;= int.MaxValue</c></param>
        /// <param name="y">y, where <c>int.MinValue &lt;= x - y &lt;= int.MaxValue</c></param>
        /// <returns>The maximum of <paramref name="x"/> and <paramref name="y"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FastMax(int x, int y) =>
            x - ((x - y) & ((x - y) >> 31));

        /// <summary>
        /// Calculates the maximum of <paramref name="x"/> and <paramref name="y"/> where <c>long.MinValue &lt;= x - y &lt;= long.MaxValue</c>
        /// </summary>
        /// <param name="x">x, where <c>long.MinValue &lt;= x - y &lt;= long.MaxValue</c></param>
        /// <param name="y">y, where <c>long.MinValue &lt;= x - y &lt;= long.MaxValue</c></param>
        /// <returns>The maximum of <paramref name="x"/> and <paramref name="y"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long FastMax(long x, long y) =>
            x - ((x - y) & ((x - y) >> 63));
    }
}
