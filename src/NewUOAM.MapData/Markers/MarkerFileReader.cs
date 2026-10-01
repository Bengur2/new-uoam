using System.Globalization;

namespace NewUOAM.MapData.Markers;

/// <summary>
/// Reads the classic UOAM ".map" marker format and the CSV marker format that ClassicUO and
/// other modern tools also understand, so existing community marker files keep working.
/// </summary>
public static class MarkerFileReader
{
    public static IReadOnlyList<MarkerEntry> Read(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        // Not File.ReadAllLines (always UTF-8): real marker files are often ANSI (e.g. Windows-1250
        // "Ostatní" in Moria's Shadow.map). Same decoding MarkerFileStore uses to find lines to edit.
        var lines = MarkerFileStore.ReadAllLines(path);
        return ext switch
        {
            ".csv" => ReadCsv(lines),
            _ => ReadUoam(lines), // .map is the conventional extension, but be permissive
        };
    }

    /// <summary>
    /// UOAM ".map" marker format, one entry per line:
    /// <c>+IconName:X Y MapIndex Rest of the name</c> ('+' visible, '-' hidden).
    /// The first line of a real UOAM file is often a lone "3" (format/version marker) - skipped.
    /// </summary>
    public static IReadOnlyList<MarkerEntry> ReadUoam(IEnumerable<string> lines)
    {
        var result = new List<MarkerEntry>();

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r', '\n');
            if (line.Length == 0) continue;

            char sign = line[0];
            if (sign != '+' && sign != '-') continue; // skip header/version/unknown lines

            int colon = line.IndexOf(':');
            if (colon < 1) continue;

            string icon = line[1..colon];
            string rest = line[(colon + 1)..].TrimStart(' ');
            string[] parts = rest.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)) continue;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) continue;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int map)) continue;
            string name = parts.Length > 3 ? parts[3] : string.Empty;

            result.Add(new MarkerEntry(x, y, map, name, IconName: icon, Visible: sign == '+'));
        }

        return result;
    }

    /// <summary>
    /// CSV marker format: <c>x,y,mapindex,name,iconname,color,zoomlevel</c> (zoomlevel optional).
    /// </summary>
    public static IReadOnlyList<MarkerEntry> ReadCsv(IEnumerable<string> lines)
    {
        var result = new List<MarkerEntry>();

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            string[] parts = line.Split(',');
            if (parts.Length < 5) continue;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)) continue;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) continue;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int map)) continue;
            string name = parts[3];
            string icon = parts[4];
            string? color = parts.Length > 5 ? parts[5] : null;
            int zoom = parts.Length > 6 && int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z) ? z : 3;

            result.Add(new MarkerEntry(x, y, map, name, IconName: icon, Color: color, ZoomIndex: zoom));
        }

        return result;
    }
}
