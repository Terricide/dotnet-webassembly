using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WebAssembly.Runtime;

/// <summary>
/// Implements a WebAssembly linear memory resource using unmanaged memory.
/// </summary>
public sealed class UnmanagedMemory : IDisposable
{
    internal static readonly RegeneratingWeakReference<FieldInfo> SizeField = new(()
        => typeof(UnmanagedMemory).GetTypeInfo().GetDeclaredField(nameof(RawSize))!);
    internal static readonly RegeneratingWeakReference<FieldInfo> StartField = new(()
        => typeof(UnmanagedMemory).GetTypeInfo().GetDeclaredField(nameof(RawStart))!);
    internal static readonly RegeneratingWeakReference<MethodInfo> SizeGetter = new(()
        => typeof(UnmanagedMemory).GetTypeInfo().DeclaredProperties.First(prop => prop.Name == nameof(Size)).GetMethod!);
    internal static readonly RegeneratingWeakReference<MethodInfo> StartGetter = new(()
        => typeof(UnmanagedMemory).GetTypeInfo().DeclaredProperties.First(prop => prop.Name == nameof(Start)).GetMethod!);
    internal static readonly RegeneratingWeakReference<MethodInfo> GrowMethod = new(()
        => typeof(UnmanagedMemory).GetTypeInfo().DeclaredMethods.First(prop => prop.Name == nameof(Grow)));
    internal static readonly RegeneratingWeakReference<MethodInfo> CopyMethod = new(()
        => typeof(UnmanagedMemory).GetTypeInfo().DeclaredMethods.First(m => m.Name == nameof(Copy)));
    internal static readonly RegeneratingWeakReference<MethodInfo> FillMethod = new(()
        => typeof(UnmanagedMemory).GetTypeInfo().DeclaredMethods.First(m => m.Name == nameof(Fill)));
    internal static readonly RegeneratingWeakReference<MethodInfo> InitFromSegmentMethod = new(()
        => typeof(UnmanagedMemory).GetTypeInfo().DeclaredMethods.First(m => m.Name == nameof(InitFromSegment)));

    private bool disposed;

    /// <summary>
    /// Creates a new <see cref="UnmanagedMemory"/> instance with the provided parameters.
    /// </summary>
    /// <param name="minimum">The initial size of memory in <see cref="Memory.PageSize"/> pages.</param>
    /// <param name="maximum">The maximum size of memory in <see cref="Memory.PageSize"/> pages.</param>
    public UnmanagedMemory(uint minimum, uint? maximum)
    {
        if ((ulong)minimum * Memory.PageSize > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(minimum), minimum, $"Maximum value allowed for memory minimum is {uint.MaxValue / Memory.PageSize}.");

        this.Minimum = minimum;
        this.Maximum = Math.Min(maximum.GetValueOrDefault(uint.MaxValue / Memory.PageSize), uint.MaxValue / Memory.PageSize);

        if (minimum == 0)
            return;

        this.Grow(minimum);
    }

    /// <summary>
    /// The initial size of memory in <see cref="Memory.PageSize"/> pages.
    /// </summary>
    public uint Minimum { get; }

    /// <summary>
    /// The maximum size of memory in <see cref="Memory.PageSize"/> pages.
    /// </summary>
    public uint Maximum { get; }

    /// <summary>
    /// The currently allocated size of memory in <see cref="Memory.PageSize"/> pages.
    /// </summary>
    public uint Current => this.RawSize / Memory.PageSize;

    /// <summary>
    /// The start of linear memory, or <see cref="IntPtr.Zero"/> if not used.
    /// </summary>
    public IntPtr Start => this.RawStart;

    /// <summary>
    /// The current amount of memory allocated.
    /// </summary>
    public uint Size => this.RawSize;

    /// <summary>
    /// Raw start pointer used by generated code in hot paths.
    /// </summary>
    [SuppressMessage("Design", "CA1051:Do not declare visible instance fields", Justification = "Generated wasm IL uses direct field loads to avoid accessor-call overhead in hot paths.")]
    public IntPtr RawStart;

    /// <summary>
    /// Raw byte length used by generated code in hot paths.
    /// </summary>
    [SuppressMessage("Design", "CA1051:Do not declare visible instance fields", Justification = "Generated wasm IL uses direct field loads to avoid accessor-call overhead in hot paths.")]
    public uint RawSize;

    /// <summary>
    /// Grows memory by <paramref name="delta"/> multiplied by <see cref="Memory.PageSize"/>.
    /// </summary>
    /// <param name="delta">The amount of memory pages to allocate.</param>
    /// <returns>The previous <see cref="Current"/> size value, or -1 (unchecked wrap to unsigned 32-bit integer) in the event of a failure.</returns>
    public uint Grow(uint delta)
    {
#if NETSTANDARD
        if (this.disposed)
            throw new ObjectDisposedException(nameof(UnmanagedMemory));
#else
        ObjectDisposedException.ThrowIf(this.disposed, this);
#endif

        var oldCurrent = this.Current;
        if (delta == 0)
            return oldCurrent;

        static unsafe void ZeroMemory(IntPtr s, uint n)
        {
            // CIL `initblk` can't be generated from C# (as of v8.0).
            // Using run-time code generation here would interfere with AoT efforts.
            // The logic below is 95% as fast as `initblk`.
            var p = (ulong*)s;
            var limit = n / 8;
            var init = 0ul; // Not const for smaller CIL size and potentially more favorable JIT optimization.

            for (var i = 0; i < limit; i += 8)
            {
                p[i] = init;
                p[i + 1] = init;
                p[i + 2] = init;
                p[i + 3] = init;
                p[i + 4] = init;
                p[i + 5] = init;
                p[i + 6] = init;
                p[i + 7] = init;
            }
        }

        const uint failed = unchecked((uint)-1);
        try
        {
            if (checked(oldCurrent + delta) > this.Maximum)
                return failed;

            var newCurrent = oldCurrent + delta;
            var newSize = newCurrent * Memory.PageSize;
            if (this.RawStart == default)
            {
                this.RawStart = Marshal.AllocHGlobal(new IntPtr(newSize));
                ZeroMemory(this.RawStart, newSize);
            }
            else
            {
                this.RawStart = Marshal.ReAllocHGlobal(this.RawStart, new IntPtr(newSize));
                ZeroMemory(this.RawStart + checked((int)this.RawSize), newSize - this.RawSize);
            }
            this.RawSize = newSize;

            return oldCurrent;
        }
        catch
        {
        }

        return failed;
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes from <paramref name="src"/> to <paramref name="dst"/> within this memory.
    /// Handles overlapping regions correctly.
    /// </summary>
    public unsafe void Copy(uint dst, uint src, uint length)
    {
        var dstEnd = checked(dst + length);
        var srcEnd = checked(src + length);
        if (dstEnd > this.RawSize)
            throw new MemoryAccessOutOfRangeException(dstEnd, this.RawSize);
        if (srcEnd > this.RawSize)
            throw new MemoryAccessOutOfRangeException(srcEnd, this.RawSize);
        if (length == 0) return;
        Buffer.MemoryCopy((void*)(this.RawStart + (int)src), (void*)(this.RawStart + (int)dst), length, length);
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes from <paramref name="src"/> starting at <paramref name="srcOffset"/> into this memory at <paramref name="dst"/>.
    /// Used to implement <c>memory.init</c>. A null <paramref name="src"/> is treated as a dropped (length-0) segment.
    /// </summary>
    public unsafe void InitFromSegment(uint dst, byte[]? src, uint srcOffset, uint length)
    {
        // A dropped (null) segment is treated as length-0 for bounds checking purposes (WASM spec).
        var srcLen = src != null ? (uint)src.Length : 0u;
        var dstEnd = checked(dst + length);
        var srcEnd = checked(srcOffset + length);
        if (dstEnd > this.RawSize)
            throw new MemoryAccessOutOfRangeException(dstEnd, this.RawSize);
        if (srcEnd > srcLen)
            throw new MemoryAccessOutOfRangeException(srcEnd, srcLen);
        if (length == 0) return;
        fixed (byte* pSrc = src)
            Buffer.MemoryCopy(pSrc + srcOffset, (void*)(this.RawStart + (int)dst), length, length);
    }

    /// <summary>
    /// Fills <paramref name="length"/> bytes starting at <paramref name="dst"/> with the low 8 bits of <paramref name="value"/>.
    /// </summary>
    public unsafe void Fill(uint dst, uint value, uint length)
    {
        if (checked(dst + length) > this.RawSize)
            throw new MemoryAccessOutOfRangeException(checked(dst + length), this.RawSize);
        if (length == 0) return;
        var p = (byte*)(this.RawStart + (int)dst);
        var b = (byte)(value & 0xFF);
        for (uint i = 0; i < length; i++)
            p[i] = b;
    }

    /// <summary>
    /// Calls <see cref="Dispose"/>.
    /// </summary>
    ~UnmanagedMemory() => this.Dispose();

    /// <summary>
    /// Releases unmanaged resources associated with this instance.
    /// </summary>
    public void Dispose()
    {
        this.disposed = true;

        if (this.RawStart == IntPtr.Zero)
            return;
        if (this.RawSize == 0)
            return;

        Marshal.FreeHGlobal(this.RawStart);
        this.RawStart = IntPtr.Zero;
        this.RawSize = 0;
        GC.SuppressFinalize(this);
    }
}
