using System.Runtime.CompilerServices;

namespace Fho.Core.Threading.Collections.BitmapInternals;

internal static class ConcurrentBitmap56Extensions
{
    // each cluster must track the fullness and emptiness of its segments, so 2 bits are required per segment
    // bits 0 to 27 are used for the segment emptiness state,
    // bits 28 to 55 are used for the segment fullness state

    extension(ConcurrentBitmap56 bmp)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AreChildrenEmpty(int numberOfChildren)
        {
            // we are only interested in the lower numberOfChildren bits
            ulong mask = (1ul << numberOfChildren) - 1;
            return (bmp.GetRawData() & mask) == mask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AreChildrenFull(int numberOfChildren)
        {
            // we are only interested in the upper 28 bits
            ulong mask = ((1ul << numberOfChildren) - 1) << 28;
            return (bmp.GetRawData() & mask) == mask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsChildEmpty(int childIndex) =>
            // read the childIndex-th bit in the lower 28 bits
            (bmp.GetRawData() & (1ul << childIndex)) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsChildFull(int childIndex) =>
            // read the childIndex-th bit in the upper 28 bits
            (bmp.GetRawData() & (1ul << (28 + childIndex))) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ConcurrentBitmap56 SetChildEmpty(int childIndex)
        {
            // write 1 to the childIndex-th bit in the lower 28 bits and 0 to the childIndex-th bit in the upper 28 bits
            ulong mask = 1ul << childIndex;
            // important: we need to preserve the full state (including the guard token).
            return new ConcurrentBitmap56((bmp.GetFullState() | mask) & ~(mask << 28));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ConcurrentBitmap56 SetChildFull(int childIndex)
        {
            // write 1 to the childIndex-th bit in the upper 28 bits and 0 to the childIndex-th bit in the lower 28 bits
            ulong mask = 1ul << childIndex;
            // important: we need to preserve the full state (including the guard token).
            return new ConcurrentBitmap56((bmp.GetFullState() & ~mask) | (mask << 28));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ConcurrentBitmap56 ClearChildEmpty(int childIndex)
        {
            // write 0 to the childIndex-th bit in the lower 28 bits
            ulong mask = 1ul << childIndex;
            // important: we need to preserve the full state (including the guard token).
            return new ConcurrentBitmap56(bmp.GetFullState() & ~mask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ConcurrentBitmap56 ClearChildFull(int childIndex)
        {
            // write 0 to the childIndex-th bit in the upper 28 bits
            ulong mask = 1ul << childIndex;
            // important: we need to preserve the full state (including the guard token).
            return new ConcurrentBitmap56(bmp.GetFullState() & ~(mask << 28));
        }
    }
}
