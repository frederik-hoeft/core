global using static Fho.Core.Threading.Globals.Discards;

namespace Fho.Core.Threading.Globals;

internal static class Discards
{
    /// <summary>
    /// A strongly typed null value that can be used for type inference in generic methods or to indicate that a return value does not matter.
    /// </summary>
    public static object? __ => null;
}
