global using static Fho.Core.Threading.Globals.Nullables;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Fho.Core.Threading.Globals;

[SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "global internal API")]
internal static class Nullables
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
#pragma warning disable CS8777 // Parameter must have a non-null value when exiting.
    public static void __NullableRelax<T>([NotNull] ref T? _) where T : allows ref struct { }
#pragma warning restore CS8777 // Parameter must have a non-null value when exiting.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
#pragma warning disable CS8777 // Parameter must have a non-null value when exiting.
    public static void __NullableRelax<T>([NotNull] T? _) { }
#pragma warning restore CS8777 // Parameter must have a non-null value when exiting.
}
