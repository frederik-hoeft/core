using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Fho.Core.Extensions.Exceptions;

namespace Fho.Core.Threading.Collections;

/// <summary>
/// A mostly lock-free concurrent bitmap optimized for producer/consumer work-tracking.
/// </summary>
/// <remarks>
/// <para>
/// Hot-path point operations (<see cref="IsEmpty"/>, <see cref="IsBitSet"/>, <see cref="GetBitInfo"/>,
/// <see cref="GetToken"/>, <see cref="UpdateBit"/>, <see cref="TryUpdateBit"/>) are lock-free with
/// respect to each other. They coordinate with rare structural operations through a quiescence gate
/// rather than a reader/writer lock on every access.
/// </para>
/// <para>
/// <see cref="IsEmpty"/> is authoritative when it returns <see langword="true"/>: no bit may be set.
/// A short-lived <see langword="false"/> while the bitmap is already empty is allowed.
/// </para>
/// <para>
/// Structural APIs (<see cref="Grow"/>, <see cref="RemoveBitAt"/>) are cold-path, exclusive, and may
/// briefly stall hot-path operations while the topology is rewritten.
/// </para>
/// </remarks>
[DebuggerDisplay("Size = {Size}, IsEmpty = {IsEmpty}, SetBits ≈ {VolatileSetCount}")]
public sealed class ConcurrentBitmap2
{
    internal const int SEGMENT_BIT_SIZE = ConcurrentBitmap56.MAX_CAPACITY;

    /// <summary>
    /// Mask for the 56 data bits inside a guarded segment word.
    /// </summary>
    private const ulong DATA_MASK = (1uL << SEGMENT_BIT_SIZE) - 1;

    private readonly Lock _structuralLock = new();

    /// <summary>
    /// Number of threads currently inside a hot-path operation that may touch segment storage.
    /// </summary>
    private int _hotPathParticipants;

    /// <summary>
    /// Non-zero while a structural operation holds exclusive ownership of storage.
    /// </summary>
    private int _structuralBlocked;

    /// <summary>
    /// Global count of set bits. Maintained with an increment-before-publish / decrement-after-clear
    /// protocol so that a zero reading is never a false empty.
    /// </summary>
    private int _popCount;

    private volatile Bitmap2Storage _storage;

    /// <summary>
    /// Initializes a new empty bitmap with the specified number of usable bits.
    /// </summary>
    /// <param name="size">The number of usable bits. Must be positive.</param>
    public ConcurrentBitmap2(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size, nameof(size));
        _storage = Bitmap2Storage.CreateEmpty(size);
    }

    /// <summary>
    /// Gets a value indicating whether no bits are set.
    /// </summary>
    /// <remarks>
    /// When this property returns <see langword="true"/>, it is guaranteed that no bit is set.
    /// When it returns <see langword="false"/>, the result is best-effort: a bit may already have
    /// been cleared, or a set may still be in flight. False negatives are acceptable; false
    /// positives are not.
    /// </remarks>
    public bool IsEmpty => Volatile.Read(ref _popCount) == 0;

    /// <summary>
    /// Gets the number of usable bits in this instance.
    /// </summary>
    public int Size => _storage.Size;

    /// <summary>
    /// Gets a best-effort snapshot of the number of set bits.
    /// </summary>
    /// <remarks>
    /// May temporarily over-count while a set is published (increment happens before the bit CAS).
    /// Never under-counts in a way that would make <see cref="IsEmpty"/> return true incorrectly.
    /// </remarks>
    public int VolatileSetCount => Volatile.Read(ref _popCount);

    /// <summary>
    /// Returns a versioned snapshot of the bit at <paramref name="index"/>.
    /// </summary>
    public GuardedBitInfo GetBitInfo(int index)
    {
        HotPathScope scope = EnterHotPath();
        try
        {
            Bitmap2Storage storage = _storage;
            ValidateIndex(index, storage.Size);
            ConcurrentBitmap56 map = ConcurrentBitmap56.VolatileRead(ref storage.SegmentRef(index));
            int bit = BitInSegment(index);
            // GuardedBitInfo.Index is the caller-visible global index, not the in-segment offset.
            return new GuardedBitInfo(map.IsBitSet(bit), map.GetToken(), index);
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>
    /// Returns a single-shot snapshot of whether the bit at <paramref name="index"/> is set.
    /// </summary>
    public bool IsBitSet(int index)
    {
        HotPathScope scope = EnterHotPath();
        try
        {
            Bitmap2Storage storage = _storage;
            ValidateIndex(index, storage.Size);
            ConcurrentBitmap56 map = ConcurrentBitmap56.VolatileRead(ref storage.SegmentRef(index));
            return map.IsBitSet(BitInSegment(index));
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>
    /// Returns the segment guard token for the bit at <paramref name="index"/>.
    /// </summary>
    public byte GetToken(int index) => GetBitInfo(index).Token;

    /// <summary>
    /// Unconditionally writes the bit at <paramref name="index"/>.
    /// </summary>
    public void UpdateBit(int index, bool isSet)
    {
        HotPathScope scope = EnterHotPath();
        try
        {
            Bitmap2Storage storage = _storage;
            ValidateIndex(index, storage.Size);
            ref ConcurrentBitmap56State state = ref storage.SegmentRef(index);
            int bit = BitInSegment(index);
            while (!TryCommitBitTransition(ref state, bit, isSet, requiredToken: null))
            {
                // Another writer won the CAS; retry with a fresh observation.
            }
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>
    /// Conditionally writes the bit at <paramref name="index"/> if the segment guard token still matches.
    /// </summary>
    /// <returns><see langword="true"/> if the write committed; otherwise <see langword="false"/>.</returns>
    public bool TryUpdateBit(int index, byte token, bool isSet)
    {
        HotPathScope scope = EnterHotPath();
        try
        {
            Bitmap2Storage storage = _storage;
            ValidateIndex(index, storage.Size);
            return TryCommitBitTransition(ref storage.SegmentRef(index), BitInSegment(index), isSet, token);
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>
    /// Grows the usable size to at least <paramref name="newSize"/>. New bits are cleared.
    /// </summary>
    /// <param name="newSize">The desired usable size. No-op when less than or equal to <see cref="Size"/>.</param>
    public void Grow(int newSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(newSize, nameof(newSize));

        lock (_structuralLock)
        {
            if (newSize <= _storage.Size)
            {
                return;
            }

            using StructuralScope structural = EnterStructural();
            _storage = _storage.GrowTo(newSize);
        }
    }

    /// <summary>
    /// Removes the bit at <paramref name="index"/> and shifts all higher bits down by one.
    /// </summary>
    public void RemoveBitAt(int index)
    {
        lock (_structuralLock)
        {
            Bitmap2Storage current = _storage;
            ArgumentOutOfRangeException.ThrowIfNotInRange(index, 0, current.Size - 1);

            using StructuralScope structural = EnterStructural();

            current = _storage;
            ArgumentOutOfRangeException.ThrowIfNotInRange(index, 0, current.Size - 1);

            bool removedWasSet = ConcurrentBitmap56.VolatileRead(ref current.SegmentRef(index))
                .IsBitSet(BitInSegment(index));

            _storage = current.RemoveBitAt(index);

            if (removedWasSet)
            {
                // Removal is published; decrement after the bit is no longer observable.
                Interlocked.Decrement(ref _popCount);
            }
        }
    }

    /// <summary>
    /// Finds the smallest index greater than or equal to <paramref name="startIndex"/> whose bit is set.
    /// </summary>
    /// <param name="startIndex">The first index to consider.</param>
    /// <param name="index">Receives the found index when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if a set bit was found; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// The scan is weakly consistent: concurrent updates may cause it to miss a bit that was set
    /// briefly, or observe a bit that is cleared shortly after. It never reports an index outside
    /// the usable range observed for the scan.
    /// </remarks>
    public bool TryFindNextSetBit(int startIndex, out int index)
    {
        HotPathScope scope = EnterHotPath();
        try
        {
            Bitmap2Storage storage = _storage;
            int size = storage.Size;
            if (startIndex < 0)
            {
                startIndex = 0;
            }

            if (startIndex >= size)
            {
                index = -1;
                return false;
            }

            int segmentIndex = startIndex / SEGMENT_BIT_SIZE;
            int bitOffset = startIndex % SEGMENT_BIT_SIZE;
            int segmentCount = storage.SegmentCount;

            for (; segmentIndex < segmentCount; segmentIndex++)
            {
                ConcurrentBitmap56 map = ConcurrentBitmap56.VolatileRead(ref storage.Segments[segmentIndex]);
                ulong data = map.GetRawData();

                if (bitOffset != 0)
                {
                    data &= ~((1uL << bitOffset) - 1);
                    bitOffset = 0;
                }

                int segmentBase = segmentIndex * SEGMENT_BIT_SIZE;
                int usableInSegment = Math.Min(SEGMENT_BIT_SIZE, size - segmentBase);
                if (usableInSegment < SEGMENT_BIT_SIZE)
                {
                    data &= (1uL << usableInSegment) - 1;
                }

                if (data != 0)
                {
                    int tz = BitOperations.TrailingZeroCount(data);
                    index = segmentBase + tz;
                    Debug.Assert((uint)index < (uint)size);
                    return true;
                }
            }

            index = -1;
            return false;
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>
    /// Attempts one compare-and-swap that applies <paramref name="isSet"/> and updates
    /// <see cref="_popCount"/> with the emptiness-safe protocol.
    /// </summary>
    /// <param name="requiredToken">
    /// When non-null, the CAS requires the segment guard token to match; used by
    /// <see cref="TryUpdateBit"/>. When null, the write is unconditional aside from CAS conflicts.
    /// </param>
    private bool TryCommitBitTransition(ref ConcurrentBitmap56State state, int bit, bool isSet, byte? requiredToken)
    {
        ref ulong target = ref AsUlong(ref state);
        ulong oldState = Volatile.Read(ref target);
        byte oldToken = GetTokenFromState(oldState);

        if (requiredToken.HasValue && oldToken != requiredToken.Value)
        {
            return false;
        }

        bool wasSet = (oldState & (1uL << bit)) != 0;
        ulong newData = isSet
            ? (oldState | (1uL << bit))
            : (oldState & ~(1uL << bit));
        // Preserve only data bits from the arithmetic above, then install bumped token.
        ulong newState = (newData & DATA_MASK) | ((ulong)(byte)(oldToken + 1) << 56);

        if (wasSet == isSet)
        {
            // Value unchanged: still bump the guard token so stale readers are invalidated.
            return Interlocked.CompareExchange(ref target, newState, oldState) == oldState;
        }

        if (isSet)
        {
            // 0 → 1: increment first, then CAS. Undo if the CAS loses.
            Interlocked.Increment(ref _popCount);
            if (Interlocked.CompareExchange(ref target, newState, oldState) != oldState)
            {
                Interlocked.Decrement(ref _popCount);
                return false;
            }

            return true;
        }

        // 1 → 0: CAS first, then decrement. A delayed decrement only yields a false non-empty.
        if (Interlocked.CompareExchange(ref target, newState, oldState) != oldState)
        {
            return false;
        }

        Interlocked.Decrement(ref _popCount);
        return true;
    }

    private HotPathScope EnterHotPath()
    {
        while (true)
        {
            if (Volatile.Read(ref _structuralBlocked) != 0)
            {
                WaitWhileStructuralBlocked();
                continue;
            }

            Interlocked.Increment(ref _hotPathParticipants);

            if (Volatile.Read(ref _structuralBlocked) != 0)
            {
                Interlocked.Decrement(ref _hotPathParticipants);
                WaitWhileStructuralBlocked();
                continue;
            }

            return new HotPathScope(this);
        }
    }

    private StructuralScope EnterStructural()
    {
        // Caller holds _structuralMutex.
        Volatile.Write(ref _structuralBlocked, 1);

        SpinWait spinner = default;
        while (Volatile.Read(ref _hotPathParticipants) != 0)
        {
            spinner.SpinOnce();
        }

        return new StructuralScope(this);
    }

    private void WaitWhileStructuralBlocked()
    {
        SpinWait spinner = default;
        while (Volatile.Read(ref _structuralBlocked) != 0)
        {
            spinner.SpinOnce();
        }
    }

    private void ExitHotPath() => Interlocked.Decrement(ref _hotPathParticipants);

    private void ExitStructural() => Volatile.Write(ref _structuralBlocked, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateIndex(int index, int size) =>
        ArgumentOutOfRangeException.ThrowIfNotInRange(index, 0, size - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BitInSegment(int index) => index % SEGMENT_BIT_SIZE;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref ulong AsUlong(ref ConcurrentBitmap56State state) =>
        ref Unsafe.As<ConcurrentBitmap56State, ulong>(ref state);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GetTokenFromState(ulong state) => (byte)(state >> 56);

    private readonly struct HotPathScope(ConcurrentBitmap2 owner) : IDisposable
    {
        public void Dispose() => owner.ExitHotPath();
    }

    private readonly struct StructuralScope(ConcurrentBitmap2 owner) : IDisposable
    {
        public void Dispose() => owner.ExitStructural();
    }
}

/// <summary>
/// Topology descriptor for <see cref="ConcurrentBitmap2"/>: usable size plus segment array.
/// Point mutations CAS into the segment words; structural ops replace the descriptor under exclusion.
/// </summary>
internal sealed class Bitmap2Storage
{
    public readonly ConcurrentBitmap56State[] Segments;
    public readonly int Size;

    private Bitmap2Storage(ConcurrentBitmap56State[] segments, int size)
    {
        Segments = segments;
        Size = size;
    }

    public int SegmentCount => Segments.Length;

    public static Bitmap2Storage CreateEmpty(int size)
    {
        int segmentCount = SegmentCountForSize(size);
        return new Bitmap2Storage(new ConcurrentBitmap56State[segmentCount], size);
    }

    public ref ConcurrentBitmap56State SegmentRef(int bitIndex) =>
        ref Segments[bitIndex / ConcurrentBitmap2.SEGMENT_BIT_SIZE];

    public Bitmap2Storage GrowTo(int newSize)
    {
        Debug.Assert(newSize > Size);
        int newSegmentCount = SegmentCountForSize(newSize);
        if (newSegmentCount == Segments.Length)
        {
            // Usable size grows into already-allocated trailing capacity (always zeroed / unused).
            return new Bitmap2Storage(Segments, newSize);
        }

        ConcurrentBitmap56State[] grown = new ConcurrentBitmap56State[newSegmentCount];
        Array.Copy(Segments, grown, Segments.Length);
        return new Bitmap2Storage(grown, newSize);
    }

    public Bitmap2Storage RemoveBitAt(int index)
    {
        Debug.Assert(index >= 0 && index < Size);
        int newSize = Size - 1;
        if (newSize == 0)
        {
            return new Bitmap2Storage(new ConcurrentBitmap56State[1], 0);
        }

        int newSegmentCount = SegmentCountForSize(newSize);
        ConcurrentBitmap56State[] next = new ConcurrentBitmap56State[newSegmentCount];

        // Exclusive structural ownership: aligned ulong reads of segment words are atomic.
        for (int oldIndex = 0; oldIndex < Size; oldIndex++)
        {
            if (oldIndex == index)
            {
                continue;
            }

            int newIndex = oldIndex < index ? oldIndex : oldIndex - 1;
            ulong sourceWord = Unsafe.As<ConcurrentBitmap56State, ulong>(ref Segments[oldIndex / ConcurrentBitmap2.SEGMENT_BIT_SIZE]);
            bool bit = (sourceWord & (1uL << (oldIndex % ConcurrentBitmap2.SEGMENT_BIT_SIZE))) != 0;
            if (bit)
            {
                int newSeg = newIndex / ConcurrentBitmap2.SEGMENT_BIT_SIZE;
                int newBit = newIndex % ConcurrentBitmap2.SEGMENT_BIT_SIZE;
                Unsafe.As<ConcurrentBitmap56State, ulong>(ref next[newSeg]) |= 1uL << newBit;
            }
        }

        // Guard tokens reset to 0 after a structural rewrite. Callers that held tokens across
        // RemoveBitAt are already operating on stale indices; token reuse is therefore acceptable.
        return new Bitmap2Storage(next, newSize);
    }

    private static int SegmentCountForSize(int size)
    {
        if (size <= 0)
        {
            return 1;
        }

        return (size + ConcurrentBitmap2.SEGMENT_BIT_SIZE - 1) / ConcurrentBitmap2.SEGMENT_BIT_SIZE;
    }
}
