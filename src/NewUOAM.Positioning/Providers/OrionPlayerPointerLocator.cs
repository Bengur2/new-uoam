using System.Security.Cryptography;
using System.Text;

namespace NewUOAM.Positioning.Providers;

/// <summary>
/// Finds the module offset of OrionUO's global player pointer (`g_Player`) for Variant B2.
///
/// Known builds are looked up by the exe's SHA-256 (offsets found by live MemoryScanner sessions).
/// For an unknown build, <see cref="TryLocate"/> finds it heuristically, read-only, from the running
/// process. It walks the module's executable sections for RIP-relative qword loads
/// (`mov r64, [rip+disp32]`), ranks the load targets by how often the code reads them, and takes
/// the most-read one whose object has plausible X/Y at the player offsets AND contains the
/// character's name (from the window title) in its first bytes. Measured on v1.0.35.1: `g_Player`
/// is the #3 most-read global (534 loads) and holds the name inline at +0x97. A decoy global
/// (#19, a different object near the player) also has plausible coordinates, which is why the
/// coordinates alone are not trusted and the name check is required.
/// </summary>
public static class OrionPlayerPointerLocator
{
    // Player object layout, the same in v1.0.35.1 and v1.0.37.0; only the global's address moved.
    public const int OffsetX = 0x28;
    public const int OffsetY = 0x2C;
    public const int OffsetZ = 0x41;

    /// <summary>OrionUO64.exe SHA-256 -> (version label, module offset of the g_Player pointer).</summary>
    private static readonly Dictionary<string, (string Version, int PointerOffset)> KnownBuilds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Main PC, found 2026-09-23.
        ["7EE94D35FAAEAA4B149EBCBF2F816C57A39479885AC41254D7EDD609D493E35B"] = ("1.0.37.0", 0x3796EF8),
        // Second PC (C:\Games\DP\Ultima Online DP\Orion Launcher), found 2026-09-24, verified against
        // the live position 2023,2872. Found by TryLocate too, before this entry existed.
        ["66D65061D47618EE6F463570FB7515DB63B5DD373AEC3B231D0D5210C5C423A2"] = ("1.0.35.1", 0x3793EE8),
    };

    private const int CandidateGlobalsToCheck = 32;
    private const int NameSearchWindow = 0x400;
    private const int MaxMapCoordinate = 8192;
    private const int ReadChunkSize = 1 << 20;

    public static string? ComputeSha256(string exePath)
    {
        try
        {
            using var stream = File.OpenRead(exePath);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }

    public static bool TryGetKnown(string? sha256, out string version, out int pointerOffset)
    {
        if (sha256 is not null && KnownBuilds.TryGetValue(sha256, out var known))
        {
            (version, pointerOffset) = known;
            return true;
        }
        version = "";
        pointerOffset = 0;
        return false;
    }

    /// <param name="read">Reads exactly n bytes at an absolute address, or returns null.</param>
    /// <param name="characterName">The character name, as shown in the client window title.</param>
    public static int? TryLocate(Func<long, int, byte[]?> read, long moduleBase, string characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName)) return null;
        byte[] nameBytes = Encoding.ASCII.GetBytes(characterName);

        var sections = ReadSections(read, moduleBase);
        if (sections is null) return null;

        var loadCounts = new Dictionary<long, int>();
        foreach (var (rva, size, characteristics) in sections)
        {
            if ((characteristics & ImageScnMemExecute) == 0) continue;
            CountRipRelativeLoads(read, moduleBase, rva, size, loadCounts);
        }

        // g_Player is a mutable global, so only targets inside a writable section qualify.
        bool IsWritable(long rva) => sections.Any(s => (s.Characteristics & ImageScnMemWrite) != 0 && rva >= s.Rva && rva < s.Rva + s.Size);

        foreach (long rva in loadCounts.Where(kv => IsWritable(kv.Key))
                     .OrderByDescending(kv => kv.Value)
                     .Take(CandidateGlobalsToCheck)
                     .Select(kv => kv.Key))
        {
            if (rva > int.MaxValue) continue;
            byte[]? pointer = read(moduleBase + rva, 8);
            if (pointer is null) continue;
            long obj = BitConverter.ToInt64(pointer, 0);
            if (obj < 0x10000) continue;

            byte[]? body = read(obj, NameSearchWindow);
            if (body is null) continue;
            int x = BitConverter.ToInt32(body, OffsetX);
            int y = BitConverter.ToInt32(body, OffsetY);
            if (x is <= 0 or >= MaxMapCoordinate || y is <= 0 or >= MaxMapCoordinate) continue;
            if (body.AsSpan().IndexOf(nameBytes) < 0) continue;

            return (int)rva;
        }
        return null;
    }

    private const uint ImageScnMemExecute = 0x20000000;
    private const uint ImageScnMemWrite = 0x80000000;

    /// <summary>Section table from the PE headers of the mapped module (in-memory image, so a
    /// section's VirtualAddress is directly its RVA).</summary>
    private static List<(long Rva, long Size, uint Characteristics)>? ReadSections(Func<long, int, byte[]?> read, long moduleBase)
    {
        byte[]? dos = read(moduleBase, 0x40);
        if (dos is null || dos[0] != 'M' || dos[1] != 'Z') return null;
        int ntOffset = BitConverter.ToInt32(dos, 0x3C);

        byte[]? fileHeader = read(moduleBase + ntOffset, 24);
        if (fileHeader is null || BitConverter.ToUInt32(fileHeader, 0) != 0x00004550) return null; // "PE\0\0"
        int sectionCount = BitConverter.ToUInt16(fileHeader, 6);
        int optionalHeaderSize = BitConverter.ToUInt16(fileHeader, 20);

        byte[]? table = read(moduleBase + ntOffset + 24 + optionalHeaderSize, sectionCount * 40);
        if (table is null) return null;

        var result = new List<(long, long, uint)>();
        for (int i = 0; i < sectionCount; i++)
        {
            int s = i * 40;
            uint virtualSize = BitConverter.ToUInt32(table, s + 8);
            uint virtualAddress = BitConverter.ToUInt32(table, s + 12);
            uint characteristics = BitConverter.ToUInt32(table, s + 36);
            result.Add((virtualAddress, virtualSize, characteristics));
        }
        return result;
    }

    /// <summary>Counts `REX.W(+R) 8B modrm [rip+disp32]` loads (48/4C 8B, mod=00 rm=101) per target RVA.</summary>
    private static void CountRipRelativeLoads(Func<long, int, byte[]?> read, long moduleBase, long sectionRva, long sectionSize, Dictionary<long, int> counts)
    {
        const int InstructionLength = 7;
        for (long chunkStart = 0; chunkStart < sectionSize; chunkStart += ReadChunkSize)
        {
            // Overlap by one instruction so a load straddling two chunks isn't missed.
            int length = (int)Math.Min(ReadChunkSize + InstructionLength - 1, sectionSize - chunkStart);
            byte[]? code = read(moduleBase + sectionRva + chunkStart, length);
            if (code is null) continue;

            int limit = Math.Min(code.Length - InstructionLength, ReadChunkSize - 1);
            for (int i = 0; i <= limit; i++)
            {
                if ((code[i] != 0x48 && code[i] != 0x4C) || code[i + 1] != 0x8B || (code[i + 2] & 0xC7) != 0x05) continue;
                long target = sectionRva + chunkStart + i + InstructionLength + BitConverter.ToInt32(code, i + 3);
                counts[target] = counts.GetValueOrDefault(target) + 1;
            }
        }
    }
}
