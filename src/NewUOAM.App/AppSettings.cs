using System.IO;
using System.Text.Json;

namespace NewUOAM.App;

/// <summary>Small persisted settings file in %LocalAppData%\NewUOAM\settings.json - just enough
/// to remember the client folder and a couple of UI toggles across sessions.</summary>
public sealed class AppSettings
{
    public string? ClientDirectory { get; set; }
    public bool ShowCoordinates { get; set; }
    public bool ShowCompass { get; set; }
    // Mapa menu: Show/Hide statics and Normal/X-ray view (default Normal with statics, as old UOAM).
    public bool ShowStatics { get; set; } = true;
    public bool XRayView { get; set; }
    // UI language of the controls: "cs" (default), "sk", "en" (Mapa > Jazyk, Loc).
    public string? Language { get; set; }
    public string? RelayServerAddress { get; set; }
    public string? MultiplayerDisplayName { get; set; }
    public string? MultiplayerColor { get; set; }
    // Your own marker's color on your map, "RRGGBB"; null = the default red.
    public string? SelfMarkerColor { get; set; }
    public string? RelayRoomPassword { get; set; }
    public bool ShowChatInGame { get; set; } = true;
    public bool PanicSound { get; set; } = true;
    public bool ShowUnreadChat { get; set; } = true;
    // Connect to the relay room at startup (Online > Připojit k mapě); off by default.
    public bool AutoConnectRelay { get; set; }
    // Track reports ("-t"): the track map window, its mode, position and map-only mode, and the sound.
    public bool ShowTrackMap { get; set; } = true;
    public bool TrackMapAlwaysOpen { get; set; }
    public WindowBounds? TrackMapBounds { get; set; }
    public bool TrackMapOnlyMode { get; set; }
    public bool TrackSound { get; set; } = true;
    public string? MarkersFolder { get; set; }
    public bool ShowMarkers { get; set; } = true;
    public bool SettingsPanelOpen { get; set; } = true;
    // Where the map and the chat were last (user's request 2026-09-28); null = default placement.
    public WindowBounds? MainWindowBounds { get; set; }
    public bool MapOnlyMode { get; set; }
    public WindowBounds? ChatWindowBounds { get; set; }
    // Marker categories the user switched on/off in the side panel (key = normalized icon name);
    // categories not listed use their marker files' own +/- default.
    public Dictionary<string, bool>? MarkerCategories { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NewUOAM", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Missing/corrupt settings file - just start with defaults rather than crash on launch.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - a failed settings save shouldn't crash or interrupt the app.
        }
    }
}
