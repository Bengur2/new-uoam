using System.Security.Cryptography;
using System.Text;
using NewUOAM.MapData.Maps;

namespace NewUOAM.MapData.Colors;

/// <summary>
/// Saves/loads a built <see cref="FacetColorMap"/> to/from a flat file in
/// %LocalAppData%\NewUOAM\colormap-cache\, so a decoded map survives the app closing instead of
/// being rebuilt from scratch (the several-seconds-per-facet <see cref="FacetColorMap.Build"/>
/// pass) every single launch - the same "render once, reuse the picture" idea as the in-memory
/// render cache in the WPF app, just persisted across sessions. Invalidated automatically: the
/// cache file records the (last-write-time, length) of every source file
/// (<see cref="MapFacet.SourceFilePaths"/> plus radarcol.mul) at the time it was written, and a
/// load is only accepted if every one of those still matches exactly - so re-installing/patching
/// the client, or editing a marker/static by hand, invalidates the cache for that facet on the
/// next load rather than silently serving stale colors.
/// </summary>
public static class FacetColorMapDiskCache
{
    private const int FormatVersion = 1;
    private static readonly byte[] Magic = "NUOAMCM1"u8.ToArray();

    private static string CacheDirFor(string clientDirectory)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NewUOAM", "colormap-cache");
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(clientDirectory.ToLowerInvariant())))[..16];
        return Path.Combine(root, hash);
    }

    private static string CacheFilePath(string clientDirectory, int facetIndex) =>
        Path.Combine(CacheDirFor(clientDirectory), $"facet{facetIndex}.bin");

    public static bool TryLoad(string clientDirectory, int facetIndex, IReadOnlyList<string> sourceFiles, out FacetColorMap? map)
    {
        map = null;
        string path = CacheFilePath(clientDirectory, facetIndex);
        if (!File.Exists(path)) return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);

            byte[] magic = reader.ReadBytes(Magic.Length);
            if (!magic.AsSpan().SequenceEqual(Magic)) return false;
            if (reader.ReadInt32() != FormatVersion) return false;

            int fileCount = reader.ReadInt32();
            if (fileCount != sourceFiles.Count) return false;

            foreach (string sourceFile in sourceFiles)
            {
                string storedPath = reader.ReadString();
                long storedWriteTicks = reader.ReadInt64();
                long storedLength = reader.ReadInt64();

                if (!string.Equals(storedPath, sourceFile, StringComparison.OrdinalIgnoreCase)) return false;
                if (!File.Exists(sourceFile)) return false;

                var info = new FileInfo(sourceFile);
                if (info.LastWriteTimeUtc.Ticks != storedWriteTicks || info.Length != storedLength) return false;
            }

            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            long expectedBytes = (long)width * height * 4;
            byte[] bgra = new byte[expectedBytes];
            int read = 0;
            while (read < bgra.Length)
            {
                int n = stream.Read(bgra, read, bgra.Length - read);
                if (n == 0) return false; // truncated cache file
                read += n;
            }

            map = FacetColorMap.FromRawBgra(width, height, bgra);
            return true;
        }
        catch (IOException) { return false; } // includes EndOfStreamException (truncated cache file)
        catch (UnauthorizedAccessException) { return false; }
    }

    public static void Save(string clientDirectory, int facetIndex, IReadOnlyList<string> sourceFiles, FacetColorMap map)
    {
        try
        {
            string dir = CacheDirFor(clientDirectory);
            Directory.CreateDirectory(dir);
            string path = CacheFilePath(clientDirectory, facetIndex);
            string tempPath = path + ".tmp";

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(sourceFiles.Count);

                foreach (string sourceFile in sourceFiles)
                {
                    var info = new FileInfo(sourceFile);
                    writer.Write(sourceFile);
                    writer.Write(info.LastWriteTimeUtc.Ticks);
                    writer.Write(info.Length);
                }

                writer.Write(map.WidthTiles);
                writer.Write(map.HeightTiles);
                writer.Flush();
                stream.Write(map.RawBgraArray, 0, map.RawBgraArray.Length);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (IOException)
        {
            // Best-effort - a failed cache write just means next launch rebuilds from scratch,
            // never worth failing the (already-succeeded) in-memory build over.
        }
        catch (UnauthorizedAccessException) { }
    }
}
