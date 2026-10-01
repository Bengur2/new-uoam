using System.IO;
using System.Text;
using System.Text.Json;

namespace NewUOAM.App;

/// <summary>
/// Your own marker categories (user's request 2026-10-01): "NewUOAM kategorie.json" in the
/// markers folder, next to the .map/.csv files. Marker files have no room for a category - the
/// .map/.csv formats carry only the icon type, and old UOAM and Orion read the same files, so
/// they can't change. Old UOAM and Orion only load .map/.csv, so this file doesn't bother them,
/// and it travels with the marker files (backup, another PC).
/// <list type="bullet">
/// <item>A category has a name and icon types: every marker with one of those icons goes in it
/// ("Treasures" = TREASURE_LEVEL1..8). The icon itself stays as it is.</item>
/// <item>A marker can also be put in a category of its own choosing (<see cref="Assignment"/>),
/// which beats its icon. The target is a category name: one of yours, or an icon type's
/// category ("Dungeon").</item>
/// </list>
/// A marker is identified by its file name, position, map and name (a marker file has no ids);
/// editing or moving a marker in this app carries its assignment along.
/// </summary>
public sealed class MarkerCategoryStore
{
    public const string FileName = "NewUOAM kategorie.json";

    public sealed class Category
    {
        public string Name { get; set; } = "";
        /// <summary>Normalized icon type names (upper case, no spaces), as in the icon files.</summary>
        public List<string> Icons { get; set; } = new();
    }

    public sealed class Assignment
    {
        /// <summary>The marker file's name (no folder: the folder is this file's).</summary>
        public string File { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int Map { get; set; }
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
    }

    public int Version { get; set; } = 1;
    public List<Category> Categories { get; set; } = new();
    public List<Assignment> Markers { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string PathIn(string markersDirectory) => Path.Combine(markersDirectory, FileName);

    /// <summary>Empty when there's no file yet. A file that can't be read is reported via
    /// <paramref name="error"/> and treated as empty - but then <see cref="Save"/> refuses to
    /// overwrite it (see <see cref="LoadFailed"/>), so a typo made by hand can't wipe it.</summary>
    public static MarkerCategoryStore Load(string markersDirectory, out string? error)
    {
        error = null;
        string path = PathIn(markersDirectory);
        if (!File.Exists(path)) return new MarkerCategoryStore();
        try
        {
            var store = JsonSerializer.Deserialize<MarkerCategoryStore>(File.ReadAllText(path, Encoding.UTF8)) ?? new MarkerCategoryStore();
            store.Categories.RemoveAll(c => string.IsNullOrWhiteSpace(c.Name));
            foreach (var c in store.Categories) c.Icons ??= new();
            store.Markers.RemoveAll(m => string.IsNullOrWhiteSpace(m.File) || string.IsNullOrWhiteSpace(m.Category));
            return store;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            return new MarkerCategoryStore { LoadFailed = true };
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool LoadFailed { get; private set; }

    /// <summary>Writes via a temp file + rename. Throws on failure, and when the file on disk
    /// couldn't be read at load (fix or delete it first).</summary>
    public void Save(string markersDirectory)
    {
        if (LoadFailed)
            throw new InvalidOperationException($"{FileName} se při načtení nepodařilo přečíst, nepřepisuju ho. Oprav ho nebo smaž a načti markery znovu.");
        Directory.CreateDirectory(markersDirectory);
        string path = PathIn(markersDirectory);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public Category? FindCategory(string name) =>
        Categories.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    public Assignment? FindAssignment(string file, int x, int y, int map, string name) =>
        Markers.FirstOrDefault(m => m.X == x && m.Y == y && m.Map == map && m.Name == name
                                    && string.Equals(m.File, file, StringComparison.OrdinalIgnoreCase));
}
