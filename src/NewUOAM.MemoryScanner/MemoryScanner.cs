using System.Text;
using static NewUOAM.MemoryScanner.Native;

namespace NewUOAM.MemoryScanner;

/// <summary>Cheat-Engine-style exact-value scanner against a live process's memory, entirely
/// read-only (see Native.cs). This is the interactive tool NewUOAM.CLAUDE.md describes as the
/// missing piece for Variant B2: no public offset list exists for this client build, so the
/// player-position pointer has to be found empirically, live, with the user actually moving their
/// character while this narrows down candidate addresses.</summary>
internal sealed class MemoryScanner : IDisposable
{
    private readonly IntPtr _handle;
    public int ProcessId { get; }
    public List<IntPtr> Candidates { get; } = new();

    private MemoryScanner(IntPtr handle, int processId)
    {
        _handle = handle;
        ProcessId = processId;
    }

    public static MemoryScanner? Attach(int processId)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
        return handle == IntPtr.Zero ? null : new MemoryScanner(handle, processId);
    }

    /// <summary>Committed, private (i.e. the process's own heap/data, not mapped DLL images) pages
    /// with read+write protection - the same "writable memory" default filter Cheat Engine and
    /// similar tools use, since that's where live game state actually lives. Excludes PAGE_GUARD
    /// pages (accessing those deliberately faults).</summary>
    private IEnumerable<(IntPtr Base, long Size)> EnumerateScannableRegions()
    {
        IntPtr address = IntPtr.Zero;
        while (true)
        {
            IntPtr result = VirtualQueryEx(_handle, address, out var mbi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
            if (result == IntPtr.Zero) yield break;

            long regionSize = mbi.RegionSize.ToInt64();
            if (regionSize <= 0) yield break; // guard against an infinite loop on malformed output

            bool writable = (mbi.Protect & (PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE)) != 0;
            bool guarded = (mbi.Protect & PAGE_GUARD) != 0;
            if (mbi.State == MEM_COMMIT && mbi.Type == MEM_PRIVATE && writable && !guarded)
                yield return (mbi.BaseAddress, regionSize);

            long next = mbi.BaseAddress.ToInt64() + regionSize;
            if (next <= address.ToInt64()) yield break; // no forward progress - stop rather than loop forever
            address = new IntPtr(next);
        }
    }

    /// <summary>Same filter as EnumerateScannableRegions but ALSO includes writable MEM_IMAGE
    /// pages (a loaded module's own .data/.bss - global/static variables) - deliberately excluded
    /// from the normal heap-value scan (too slow and noisy to include by default), but exactly
    /// where a genuinely STABLE pointer to a heap struct is likely to live: unlike heap addresses,
    /// a module's own image base is constant across restarts for a non-ASLR-relocated module
    /// (common for older game clients), so module_base + fixed_offset -> (pointer chain) -> our
    /// struct survives a game restart even though the struct's own heap address doesn't.</summary>
    private IEnumerable<(IntPtr Base, long Size)> EnumerateRegionsIncludingModuleData()
    {
        IntPtr address = IntPtr.Zero;
        while (true)
        {
            IntPtr result = VirtualQueryEx(_handle, address, out var mbi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
            if (result == IntPtr.Zero) yield break;

            long regionSize = mbi.RegionSize.ToInt64();
            if (regionSize <= 0) yield break;

            bool writable = (mbi.Protect & (PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE)) != 0;
            bool guarded = (mbi.Protect & PAGE_GUARD) != 0;
            bool relevantType = mbi.Type == MEM_PRIVATE || mbi.Type == MEM_IMAGE;
            if (mbi.State == MEM_COMMIT && relevantType && writable && !guarded)
                yield return (mbi.BaseAddress, regionSize);

            long next = mbi.BaseAddress.ToInt64() + regionSize;
            if (next <= address.ToInt64()) yield break;
            address = new IntPtr(next);
        }
    }

    /// <summary>Reverse pointer search: finds every 8-byte-aligned location across
    /// EnumerateRegionsIncludingModuleData whose value equals the target address exactly - i.e.
    /// "what points at this?". Used to find a stable anchor for a candidate found by value-scanning
    /// (which only ever finds the CURRENT, restart-unstable heap address).</summary>
    public List<IntPtr> FindPointersTo(IntPtr target)
    {
        byte[] pattern = BitConverter.GetBytes(target.ToInt64());
        var found = new List<IntPtr>();
        foreach (var (baseAddr, size) in EnumerateRegionsIncludingModuleData())
        {
            const long chunkSize = 64L * 1024 * 1024;
            for (long offset = 0; offset < size; offset += chunkSize)
            {
                long thisChunk = Math.Min(chunkSize, size - offset);
                if (thisChunk < 8) continue;
                var buffer = ReadRegion(IntPtr.Add(baseAddr, (int)offset), thisChunk);
                if (buffer is null) continue;

                for (int i = 0; i <= buffer.Length - 8; i += 8)
                {
                    bool match = true;
                    for (int j = 0; j < 8; j++)
                    {
                        if (buffer[i + j] != pattern[j]) { match = false; break; }
                    }
                    if (match) found.Add(IntPtr.Add(baseAddr, (int)offset + i));
                }
            }
        }
        return found;
    }

    /// <summary>Same as FindPointersTo but matches any 8-byte value within [lo, hi] instead of one
    /// exact value - one pass instead of needing a separate full scan per candidate "maybe the
    /// object's real header starts a bit before the field I found" guess.</summary>
    public List<(IntPtr Location, long Value)> FindPointersInRange(long lo, long hi)
    {
        var found = new List<(IntPtr, long)>();
        foreach (var (baseAddr, size) in EnumerateRegionsIncludingModuleData())
        {
            const long chunkSize = 64L * 1024 * 1024;
            for (long offset = 0; offset < size; offset += chunkSize)
            {
                long thisChunk = Math.Min(chunkSize, size - offset);
                if (thisChunk < 8) continue;
                var buffer = ReadRegion(IntPtr.Add(baseAddr, (int)offset), thisChunk);
                if (buffer is null) continue;

                for (int i = 0; i <= buffer.Length - 8; i += 8)
                {
                    long val = BitConverter.ToInt64(buffer, i);
                    if (val >= lo && val <= hi)
                        found.Add((IntPtr.Add(baseAddr, (int)offset + i), val));
                }
            }
        }
        return found;
    }

    public int? GetModuleTypeAt(IntPtr address)
    {
        VirtualQueryEx(_handle, address, out var mbi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
        return (int)mbi.Type;
    }

    private byte[]? ReadRegion(IntPtr baseAddress, long size)
    {
        var buffer = new byte[size];
        bool ok = ReadProcessMemory(_handle, baseAddress, buffer, new IntPtr(size), out IntPtr bytesRead);
        if (!ok || bytesRead.ToInt64() != size) return null; // partial/failed reads are common (pages can be freed mid-scan) - just skip
        return buffer;
    }

    public byte[]? ReadBytes(IntPtr address, int count) => ReadRegion(address, count);

    /// <summary>First scan: every 4-byte-aligned occurrence of a little-endian int32 across all
    /// scannable regions becomes a candidate. Replaces any previous candidate list.</summary>
    public int ScanInt32(int value)
    {
        Candidates.Clear();
        byte[] pattern = BitConverter.GetBytes(value);
        foreach (var (baseAddr, size) in EnumerateScannableRegions())
        {
            const long chunkSize = 64L * 1024 * 1024; // cap a single read - some regions are huge
            for (long offset = 0; offset < size; offset += chunkSize)
            {
                long thisChunk = Math.Min(chunkSize, size - offset);
                if (thisChunk < 4) continue;
                var buffer = ReadRegion(IntPtr.Add(baseAddr, (int)offset), thisChunk);
                if (buffer is null) continue;

                for (int i = 0; i <= buffer.Length - 4; i += 4)
                {
                    if (buffer[i] == pattern[0] && buffer[i + 1] == pattern[1] && buffer[i + 2] == pattern[2] && buffer[i + 3] == pattern[3])
                        Candidates.Add(IntPtr.Add(baseAddr, (int)offset + i));
                }
            }
        }
        return Candidates.Count;
    }

    /// <summary>Narrows the existing candidate list to only those whose CURRENT value now matches
    /// - the "value changed to X after you moved" half of the classic scan/narrow technique.</summary>
    public int NarrowInt32(int value)
    {
        byte[] pattern = BitConverter.GetBytes(value);
        Candidates.RemoveAll(addr =>
        {
            var buf = ReadRegion(addr, 4);
            return buf is null || buf[0] != pattern[0] || buf[1] != pattern[1] || buf[2] != pattern[2] || buf[3] != pattern[3];
        });
        return Candidates.Count;
    }

    public int? ReadInt32At(IntPtr address)
    {
        var buf = ReadRegion(address, 4);
        return buf is null ? null : BitConverter.ToInt32(buf, 0);
    }

    /// <summary>Scans for a literal ASCII (or, with unicode:true, UTF-16LE) byte sequence - useful
    /// for finding a known character name in memory as an independent anchor point, separate from
    /// the numeric position scan.</summary>
    public List<IntPtr> FindString(string text, bool unicode)
    {
        byte[] pattern = unicode ? Encoding.Unicode.GetBytes(text) : Encoding.ASCII.GetBytes(text);
        var found = new List<IntPtr>();
        foreach (var (baseAddr, size) in EnumerateScannableRegions())
        {
            const long chunkSize = 64L * 1024 * 1024;
            long overlap = pattern.Length - 1;
            for (long offset = 0; offset < size; offset += chunkSize - overlap)
            {
                long thisChunk = Math.Min(chunkSize, size - offset);
                if (thisChunk < pattern.Length) continue;
                var buffer = ReadRegion(IntPtr.Add(baseAddr, (int)offset), thisChunk);
                if (buffer is null) continue;

                for (int i = 0; i <= buffer.Length - pattern.Length; i++)
                {
                    bool match = true;
                    for (int j = 0; j < pattern.Length; j++)
                    {
                        if (buffer[i + j] != pattern[j]) { match = false; break; }
                    }
                    if (match) found.Add(IntPtr.Add(baseAddr, (int)offset + i));
                }
            }
        }
        return found;
    }

    public void Dispose() => CloseHandle(_handle);
}
