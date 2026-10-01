using System.Globalization;
using System.Text;

namespace NewUOAM.MapData.Markers;

/// <summary>
/// Writes marker files (append / edit / delete one entry) without disturbing anything else in them.
///
/// Real marker files are old-UOAM-era ANSI text (e.g. Moria's Shadow.map is Windows-1250 with an
/// icon named "Ostatní", CRLF line endings), and old UOAM / Orion read them as ANSI. So:
/// - encoding is detected per file (<see cref="DetectEncoding"/>): valid UTF-8 containing non-ASCII
///   bytes means UTF-8, anything else (incl. pure ASCII and brand-new files) is the system ANSI code
///   page, which keeps new files readable by old UOAM/Orion;
/// - an edit/delete works on raw byte lines and replaces or drops exactly one line, so every other
///   line (and its line ending) stays byte-for-byte identical, even bytes the decoder can't map;
/// - the first time an existing file is modified, it is copied to "&lt;file&gt;.bak" (if no such
///   backup exists yet), so the original can always be recovered.
/// </summary>
public static class MarkerFileStore
{
    private static readonly Lazy<Encoding> Ansi = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // Code page 0 = the system's active ANSI code page (1250 on the user's machine).
        return Encoding.GetEncoding(0);
    });

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static Encoding AnsiEncoding => Ansi.Value;

    public static Encoding DetectEncoding(byte[] bytes)
    {
        bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (hasBom) return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (!bytes.Any(b => b > 0x7F)) return AnsiEncoding;
        try
        {
            StrictUtf8.GetString(bytes);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return AnsiEncoding;
        }
    }

    /// <summary>Decodes a whole marker file to lines with the detected encoding (used by the reader
    /// too, so reading and matching lines for edit/delete always agree).</summary>
    public static string[] ReadAllLines(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return SplitLines(bytes).Select(l => Decode(bytes, l, DetectEncoding(bytes))).ToArray();
    }

    public static string FormatLine(string path, MarkerEntry entry) =>
        IsCsv(path)
            ? string.Join(',',
                entry.X.ToString(CultureInfo.InvariantCulture),
                entry.Y.ToString(CultureInfo.InvariantCulture),
                entry.MapIndex.ToString(CultureInfo.InvariantCulture),
                entry.Name.Replace(',', ' '),
                entry.IconName ?? "",
                entry.Color ?? "",
                entry.ZoomIndex.ToString(CultureInfo.InvariantCulture))
            // UOAM style: "+icon: X Y Map Name" ('-' = hidden by default).
            : $"{(entry.Visible ? '+' : '-')}{entry.IconName}: {entry.X} {entry.Y} {entry.MapIndex} {entry.Name}";

    /// <summary>Appends one entry, creating the file (with UOAM's "3" version line for .map) if needed.</summary>
    public static void Append(string path, MarkerEntry entry)
    {
        if (!File.Exists(path))
        {
            string header = IsCsv(path) ? "" : "3\r\n";
            File.WriteAllBytes(path, AnsiEncoding.GetBytes(header + FormatLine(path, entry) + "\r\n"));
            return;
        }

        byte[] bytes = File.ReadAllBytes(path);
        Encoding encoding = DetectEncoding(bytes);
        string newline = DetectNewline(bytes);
        BackupOnce(path);

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write);
        // The last line may lack a terminator - don't glue the new entry onto it.
        if (bytes.Length > 0 && bytes[^1] != (byte)'\n')
            stream.Write(encoding.GetBytes(newline));
        stream.Write(encoding.GetBytes(FormatLine(path, entry) + newline));
    }

    /// <summary>Replaces (or, with <paramref name="replacement"/> null, deletes) the first line that
    /// parses to exactly <paramref name="original"/>. Returns false if no such line exists (the
    /// file changed since it was loaded) - nothing is written then.</summary>
    public static bool Replace(string path, MarkerEntry original, MarkerEntry? replacement)
    {
        if (!File.Exists(path)) return false;
        byte[] bytes = File.ReadAllBytes(path);
        Encoding encoding = DetectEncoding(bytes);
        var lines = SplitLines(bytes);

        int index = lines.FindIndex(l => ParseLine(path, Decode(bytes, l, encoding)) == original);
        if (index < 0) return false;

        BackupOnce(path);
        var target = lines[index];
        using var output = new MemoryStream(bytes.Length + 128);
        output.Write(bytes, 0, target.Start);
        if (replacement is not null)
        {
            output.Write(encoding.GetBytes(FormatLine(path, replacement)));
            output.Write(bytes, target.Start + target.ContentLength, target.TerminatorLength); // keep its own line ending
        }
        int after = target.Start + target.ContentLength + target.TerminatorLength;
        output.Write(bytes, after, bytes.Length - after);
        File.WriteAllBytes(path, output.ToArray());
        return true;
    }

    private static MarkerEntry? ParseLine(string path, string line)
    {
        var parsed = IsCsv(path) ? MarkerFileReader.ReadCsv([line]) : MarkerFileReader.ReadUoam([line]);
        return parsed.Count == 1 ? parsed[0] : null;
    }

    private static bool IsCsv(string path) => Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase);

    private static void BackupOnce(string path)
    {
        string backup = path + ".bak";
        if (!File.Exists(backup)) File.Copy(path, backup);
    }

    private static string DetectNewline(byte[] bytes)
    {
        int lf = Array.IndexOf(bytes, (byte)'\n');
        return lf > 0 && bytes[lf - 1] == (byte)'\r' ? "\r\n" : lf >= 0 ? "\n" : "\r\n";
    }

    private readonly record struct RawLine(int Start, int ContentLength, int TerminatorLength);

    private static List<RawLine> SplitLines(byte[] bytes)
    {
        var result = new List<RawLine>();
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        while (start < bytes.Length)
        {
            int lf = Array.IndexOf(bytes, (byte)'\n', start);
            int end = lf < 0 ? bytes.Length : lf;
            int content = end - start;
            int terminator = lf < 0 ? 0 : 1;
            if (content > 0 && bytes[end - 1] == (byte)'\r') { content--; terminator++; }
            result.Add(new RawLine(start, content, terminator));
            start = lf < 0 ? bytes.Length : lf + 1;
        }
        return result;
    }

    private static string Decode(byte[] bytes, RawLine line, Encoding encoding) =>
        encoding.GetString(bytes, line.Start, line.ContentLength);
}
