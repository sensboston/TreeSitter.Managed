// Arena storage for heap subtree data (ARENA.md A2, A4).
// Handles are (segment << 12 | slot), slot 0 of segment 0 is reserved (handle 0 = null).
// Segments never move. The segment table is replaced on grow and published with a volatile write.
// Allocation uses thread-local batches of 64 handles; the global free list and segment growth
// are under one lock. RefCount changes are Interlocked at the call sites.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace TreeSitter.Subtrees;

/// <summary>
/// Heap subtree data stored in <see cref="SubtreeArena"/>. No reference fields (A2).
/// The layout follows SubtreeHeapData of subtree.h: the union is a real union here too,
/// with the same order and offsets inside it (non-terminal fields, external scanner state
/// and lookahead char overlap, as in C). A node then holds only its own interpretation,
/// which keeps the struct small: 84 bytes (88 in DEBUG) instead of 116 (120 in DEBUG),
/// with the offsets given explicitly below.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct SubtreeHeapData
{
    // ---------------------------------------------------------------- union (offset 0)
    // Non-terminal nodes (ChildCount > 0), in the C order.
    [FieldOffset(0)] internal uint VisibleChildCount;
    [FieldOffset(4)] internal uint NamedChildCount;
    [FieldOffset(8)] internal uint VisibleDescendantCount;
    [FieldOffset(12)] internal int DynamicPrecedence;
    [FieldOffset(16)] internal ushort RepeatDepth;
    [FieldOffset(18)] internal ushort ProductionId;
    [FieldOffset(20)] internal ushort FirstLeafSymbol;
    [FieldOffset(22)] internal ushort FirstLeafParseState;

    // External token (ChildCount == 0 and HasExternalTokens).
    [FieldOffset(0)] internal ExternalScannerStateData ExternalScannerState;

    // Error leaf (ChildCount == 0 and Symbol == BuiltinSymbols.Error).
    [FieldOffset(0)] internal int LookaheadChar;

    // ---------------------------------------------------------------- outside the union
    [FieldOffset(28)] internal ushort Symbol;
    [FieldOffset(30)] internal ushort ParseState;
    [FieldOffset(32)] internal SubtreeFlags Flags;

    [FieldOffset(36)] internal uint LookaheadBytes;
    [FieldOffset(40)] internal uint ErrorCost;
    [FieldOffset(44)] internal uint ChildCount;

    /// <summary>Handle into the children side table, or -1 when the node has no children array.</summary>
    [FieldOffset(48)] internal int ChildrenHandle;

    [FieldOffset(52)] internal Length Padding;
    [FieldOffset(64)] internal Length Size;

    /// <summary>The handle of this slot (segment and slot), set when the segment is made.</summary>
    [FieldOffset(76)] internal int SelfHandle;
    [FieldOffset(80)] internal int RefCount;

#if DEBUG
    [FieldOffset(84)] internal ushort Generation;
    [FieldOffset(86)] internal bool Allocated;
#endif

    /// <summary>Sets or clears one flag.</summary>
    internal void SetFlag(SubtreeFlags flag, bool value) =>
        Flags = value ? Flags | flag : Flags & ~flag;

    /// <summary>True when the flag is set.</summary>
    internal readonly bool HasFlag(SubtreeFlags flag) => (Flags & flag) != 0;
}

/// <summary>
/// Unmanaged external scanner state stored inside <see cref="SubtreeHeapData"/>.
/// Short data is inline (24 bytes). Long data is a handle into <see cref="SubtreeArena"/>'s
/// long-state side table. As ExternalScannerState in subtree.h, the long data pointer (a
/// handle here) overlaps the short data; the length follows the union at offset 24.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 28)]
internal struct ExternalScannerStateData
{
    internal const int ShortCapacity = 24;

    [InlineArray(ShortCapacity)]
    internal struct ShortBuffer
    {
        private byte _element0;
    }

    [FieldOffset(0)] internal ShortBuffer Short;

    [FieldOffset(0)] internal int LongHandle; // 0 (NULL) when not used; only valid when Length > ShortCapacity

    [FieldOffset(24)] internal uint Length;

    internal readonly bool IsLong => Length > ShortCapacity;

    internal readonly ushort UnionRepeatDepth
    {
        get
        {
            if (Length > ShortCapacity)
            {
                return 0;
            }

            return (ushort)(Short[16] | (Short[17] << 8));
        }
    }
}

/// <summary>Global arena for subtree heap data and side tables (A2, A4).</summary>
internal static unsafe class SubtreeArena
{
    private const int SlotBits = 12;
    private const int SegmentSize = 1 << SlotBits; // 4096
    private const int SlotMask = SegmentSize - 1;
    private const int BatchSize = 64;
    private const int InitialSegments = 1;

    private static readonly object s_lock = new object();

    // Segment table: replaced on grow, published with Volatile.Write. Readers never take the lock.
    private static SubtreeHeapData[][] s_segments;

    // Global free handle stack.
    private static int[] s_freeHandles;
    private static int s_freeCount;

    // Children of a node, indexed by the node's own handle: a segment table parallel to
    // s_segments, grown with it. Written only by the thread that owns the node, read
    // without a lock; no handle of its own, no free list.
    private static Subtree[]?[][] s_childSegments;

    // Side table: long external scanner states. Index 0 reserved.
    private static byte[]?[] s_longStates;
    private static int[] s_longStatesFree;
    private static int s_longStatesFreeCount;
    private static int s_longStatesNext;


    // The handle batch of a thread: one thread-local lookup per allocation or release.
    internal sealed class Batch
    {
        internal readonly int[] Handles = new int[BatchSize];
        internal int Count;

        // The pool of child arrays of the thread (see RentChildren), in the same object
        // so that one thread-local lookup serves both.
        internal readonly Subtree[][]?[] ChildArrays = new Subtree[][]?[MaxPooledChildren + 1];
        internal readonly int[] ChildArrayCounts = new int[MaxPooledChildren + 1];
    }

    // The thread-local batch: a separate class with no static constructor so that
    // accessing it does not go through SubtreeArena's static constructor.
    internal static class BatchThreadLocal
    {
        [ThreadStatic]
        internal static Batch? Value;
    }

    static SubtreeArena()
    {
        s_segments = new SubtreeHeapData[InitialSegments][];
        for (int i = 0; i < InitialSegments; i++)
        {
            s_segments[i] = NewSegment(i);
        }

        // Reserve handle 0 (segment 0, slot 0) as NULL sentinel.
        ref SubtreeHeapData reserved = ref s_segments[0][0];
#if DEBUG
        reserved.Allocated = true;
        reserved.Generation = 1;
#endif
        reserved.RefCount = 1;

        s_freeHandles = new int[SegmentSize];
        s_freeCount = 0;

        s_childSegments = new Subtree[]?[InitialSegments][];
        for (int i = 0; i < InitialSegments; i++)
        {
            s_childSegments[i] = new Subtree[]?[SegmentSize];
        }

        s_longStates = new byte[]?[64];
        s_longStatesFree = new int[32];
        s_longStatesFreeCount = 0;
        s_longStatesNext = 1; // 0 reserved
    }

    // A segment never moves (pinned object heap), so the address of a slot stays valid and a
    // heap Subtree holds it directly, like the pointer of the C union: one load per access.
    private static SubtreeHeapData[] NewSegment(int segmentIndex)
    {
        SubtreeHeapData[] segment = GC.AllocateArray<SubtreeHeapData>(SegmentSize, pinned: true);
        for (int slot = 0; slot < SegmentSize; slot++)
        {
            segment[slot].SelfHandle = HandleOf(segmentIndex, slot);
        }

        return segment;
    }

    /// <summary>Encodes a segment/slot into a handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int HandleOf(int segment, int slot) => (segment << SlotBits) | slot;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SegmentOf(int handle) => handle >> SlotBits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SlotOf(int handle) => handle & SlotMask;

    /// <summary>Returns a reference to the heap data for <paramref name="subtree"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref SubtreeHeapData Get(Subtree subtree)
    {
#if DEBUG
        ref SubtreeHeapData data = ref Unsafe.AsRef<SubtreeHeapData>((void*)subtree.Pointer);
        Debug.Assert(data.Allocated, "access to a freed subtree slot");
        Debug.Assert(data.Generation == subtree.Generation, "stale subtree handle");
        return ref data;
#else
        return ref Unsafe.AsRef<SubtreeHeapData>((void*)subtree.Pointer);
#endif
    }

    /// <summary>Returns a reference to the heap data by handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref SubtreeHeapData Get(int handle)
    {
#if DEBUG
        ref SubtreeHeapData data = ref s_segments[SegmentOf(handle)][SlotOf(handle)];
        Debug.Assert(data.Allocated, "access to a freed subtree slot");
        return ref data;
#else
        return ref Slot(handle);
#endif
    }

    // The slot of a handle without bounds checks: handles are made only by this arena
    // (segment and slot are always in range), and this is the hottest path of a parse
    // (measured with perf: two bounds checks per field read of a node). Debug builds
    // check bounds, allocation and generation above.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref SubtreeHeapData Slot(int handle)
    {
        SubtreeHeapData[] segment = Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(s_segments), SegmentOf(handle));
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(segment), SlotOf(handle));
    }

    /// <summary>Allocates a heap slot and returns a heap <see cref="Subtree"/> with RefCount 1.</summary>
    internal static Subtree Allocate()
    {
        Batch cache = BatchThreadLocal.Value ??= new Batch();
        if (cache.Count == 0)
        {
            RefillBatch(cache);
        }

        int handle = cache.Handles[--cache.Count];
        ref SubtreeHeapData data = ref Slot(handle);
        data.RefCount = 1;
        data.ChildrenHandle = -1;

        // The union is zero here: Free resets the whole slot, and a zero handle (like the
        // NULL pointer of the C union) means that no long external scanner state is stored.
        // A leaf keeps lookahead_char 0, as a zeroed SubtreeHeapData does in C.
#if DEBUG
        Debug.Assert(!data.Allocated, "allocating an already allocated slot");
        data.Allocated = true;
        unchecked { data.Generation++; }
        if (data.Generation == 0) data.Generation = 1;
        return Subtree.FromHeapPointer((nint)Unsafe.AsPointer(ref data), data.Generation);
#else
        return Subtree.FromHeapPointer((nint)Unsafe.AsPointer(ref data));
#endif
    }

    private static void RefillBatch(Batch cache)
    {
        lock (s_lock)
        {
            if (s_freeCount >= BatchSize)
            {
                s_freeCount -= BatchSize;
                Array.Copy(s_freeHandles, s_freeCount, cache.Handles, 0, BatchSize);
                cache.Count = BatchSize;
                return;
            }

            if (s_freeCount > 0)
            {
                int count = s_freeCount;
                Array.Copy(s_freeHandles, 0, cache.Handles, 0, count);
                s_freeCount = 0;
                cache.Count = count;
                return;
            }

            GrowAndRefill(cache);
        }
    }

    private static void GrowAndRefill(Batch cache)
    {
        // Called under lock.
        int newSegmentIndex = s_segments.Length;
        SubtreeHeapData[][] newTable = new SubtreeHeapData[newSegmentIndex + 1][];
        Array.Copy(s_segments, newTable, newSegmentIndex);
        newTable[newSegmentIndex] = NewSegment(newSegmentIndex);
        Subtree[]?[][] newChildTable = new Subtree[]?[newSegmentIndex + 1][];
        Array.Copy(s_childSegments, newChildTable, newSegmentIndex);
        newChildTable[newSegmentIndex] = new Subtree[]?[SegmentSize];
        // The child table is published first: a handle of the new segment is only handed
        // out after both tables are visible.
        Volatile.Write(ref s_childSegments, newChildTable);
        Volatile.Write(ref s_segments, newTable);

        // The top BatchSize slots go to the batch, the others to the global free list
        // (handle 0, slot 0 of segment 0, is the reserved NULL sentinel).
        int count = 0;
        for (int slot = SegmentSize - 1; slot >= SegmentSize - BatchSize; slot--)
        {
            cache.Handles[count++] = HandleOf(newSegmentIndex, slot);
        }

        int first = newSegmentIndex == 0 ? 1 : 0;
        EnsureFreeCapacity(s_freeCount + SegmentSize - BatchSize);
        for (int slot = first; slot < SegmentSize - BatchSize; slot++)
        {
            s_freeHandles[s_freeCount++] = HandleOf(newSegmentIndex, slot);
        }

        cache.Count = count;
    }

    private static void EnsureFreeCapacity(int needed)
    {
        if (s_freeHandles.Length >= needed)
        {
            return;
        }

        int newCap = s_freeHandles.Length * 2;
        while (newCap < needed)
        {
            newCap *= 2;
        }

        int[] newArr = new int[newCap];
        Array.Copy(s_freeHandles, newArr, s_freeCount);
        s_freeHandles = newArr;
    }

    /// <summary>Frees a heap slot (refcount already reached 0). Resets and returns to a batch/free list.</summary>
    internal static void Free(int handle)
    {
        SubtreeHeapData[][] segments = s_segments;
        ref SubtreeHeapData data = ref segments[SegmentOf(handle)][SlotOf(handle)];
#if DEBUG
        Debug.Assert(data.Allocated, "double free of subtree slot");
        data.Allocated = false;
        ushort gen = data.Generation;
#endif
        data = default;
        data.SelfHandle = handle;
#if DEBUG
        data.Generation = gen;
#endif

        Batch cache = BatchThreadLocal.Value ??= new Batch();
        if (cache.Count < BatchSize)
        {
            cache.Handles[cache.Count++] = handle;
            return;
        }

        FlushBatch(cache.Handles);
        cache.Handles[0] = handle;
        cache.Count = 1;
    }

    private static void FlushBatch(int[] batch)
    {
        lock (s_lock)
        {
            EnsureFreeCapacity(s_freeCount + BatchSize);
            Array.Copy(batch, 0, s_freeHandles, s_freeCount, BatchSize);
            s_freeCount += BatchSize;
        }
    }

    // ------------------------------------------------------------ children side table

    /// <summary>Stores the children array of the node with this handle; returns the handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int StoreChildren(int nodeHandle, Subtree[] children)
    {
        s_childSegments[SegmentOf(nodeHandle)][SlotOf(nodeHandle)] = children;
        return nodeHandle;
    }

    /// <summary>Gets the children array stored for a node handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Subtree[] GetChildren(int handle) =>
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(s_childSegments), SegmentOf(handle))),
            SlotOf(handle))!;

    /// <summary>Drops the children array of a node handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void FreeChildren(int handle)
    {
        if (handle < 0)
        {
            return;
        }

        ref Subtree[]? slot = ref s_childSegments[SegmentOf(handle)][SlotOf(handle)];
        Subtree[]? children = slot;
        slot = null;
        if (children is not null)
        {
            ReturnChildren(children);
        }
    }

    // Children arrays of an exact length are reused per thread: Subtree holds no reference,
    // so a pooled array keeps nothing alive. Lengths up to MaxPooledChildren, at most
    // MaxPooledPerLength arrays per length; others are left to the GC.
    private const int MaxPooledChildren = 32;
    private const int MaxPooledPerLength = 256;

    /// <summary>An array of exactly <paramref name="length"/> children, its contents unspecified.</summary>
    internal static Subtree[] RentChildren(int length)
    {
        if (length > 0 && length <= MaxPooledChildren && BatchThreadLocal.Value is { } pool && pool.ChildArrayCounts[length] > 0)
        {
            return pool.ChildArrays[length]![--pool.ChildArrayCounts[length]];
        }

        return new Subtree[length];
    }

    internal static void ReturnChildren(Subtree[] children)
    {
        int length = children.Length;
        if (length == 0 || length > MaxPooledChildren)
        {
            return;
        }

#if DEBUG
        // Poison: a stale user of a returned array reads inline leaves with symbol 0xFF
        // and wrong sizes, which the oracle tests see at once.
        children.AsSpan().Fill(Subtree.Poison);
#endif
        Batch pool = BatchThreadLocal.Value ??= new Batch();
        Subtree[][] arrays = pool.ChildArrays[length] ??= new Subtree[MaxPooledPerLength][];
        if (pool.ChildArrayCounts[length] < MaxPooledPerLength)
        {
            arrays[pool.ChildArrayCounts[length]++] = children;
        }
    }

    // ------------------------------------------------------------ releases deferred by finalizers

    // A tree that is not disposed is released by its finalizer. Releasing it on the
    // finalizer thread would put its slots and child arrays into that thread's batch and
    // pool, where no parser can reuse them. So the finalizer only queues the root, and the
    // next parse releases the queue on its own thread. A long queue (no parse runs) is
    // released by the finalizer itself.
    private const int MaxPendingReleases = 256;
    private static readonly ConcurrentQueue<Subtree> s_pendingReleases = new();

    /// <summary>Queues the root of a finalized tree for release.</summary>
    internal static void DeferRelease(Subtree root)
    {
        s_pendingReleases.Enqueue(root);
        if (s_pendingReleases.Count > MaxPendingReleases)
        {
            SubtreePool pool = new SubtreePool(0);
            ReleasePending(pool);
            pool.Delete();
        }
    }

    /// <summary>Releases the roots queued by finalizers, on the calling thread.</summary>
    internal static void ReleasePending(SubtreePool pool)
    {
        while (s_pendingReleases.TryDequeue(out Subtree root))
        {
            SubtreeOps.Release(pool, root);
        }
    }

    // ------------------------------------------------------------ long external scanner state side table

    /// <summary>Stores a long external scanner state and returns its handle.</summary>
    internal static int StoreLongExternalState(byte[] data)
    {
        lock (s_lock)
        {
            if (s_longStatesFreeCount > 0)
            {
                int handle = s_longStatesFree[--s_longStatesFreeCount];
                s_longStates[handle] = data;
                return handle;
            }

            if (s_longStatesNext >= s_longStates.Length)
            {
                int newCap = s_longStates.Length * 2;
                byte[]?[] newArr = new byte[]?[newCap];
                Array.Copy(s_longStates, newArr, s_longStates.Length);
                s_longStates = newArr;
            }

            int h = s_longStatesNext++;
            s_longStates[h] = data;
            return h;
        }
    }

    /// <summary>Gets long external scanner state bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte[] GetLongExternalState(int handle)
    {
        return s_longStates[handle]!;
    }

    /// <summary>Returns a long external scanner state handle to the free list.</summary>
    internal static void FreeLongExternalState(int handle)
    {
        if (handle <= 0)
        {
            return;
        }

        lock (s_lock)
        {
            s_longStates[handle] = null;
            if (s_longStatesFreeCount >= s_longStatesFree.Length)
            {
                int[] newArr = new int[s_longStatesFree.Length * 2];
                Array.Copy(s_longStatesFree, newArr, s_longStatesFree.Length);
                s_longStatesFree = newArr;
            }

            s_longStatesFree[s_longStatesFreeCount++] = handle;
        }
    }
}
