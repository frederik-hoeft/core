namespace Fho.Core.Threading.Optimistic;

internal static class AtomicBooleanExtensions
{
    extension(AtomicBoolean self)
    {
        public ulong As64BitMask() => unchecked((ulong)(long)(int)(uint)self);
    }
}
