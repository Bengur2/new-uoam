using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NewUOAM.MapData;
using NewUOAM.MapData.Colors;
using NewUOAM.MapData.Markers;
using NewUOAM.MapData.Maps;
using NewUOAM.Positioning;
using NewUOAM.Positioning.Providers;
using NewUOAM.Positioning.ClientIntegration;
using NewUOAM.Positioning.Relay;
using NewUOAM.Updates;

namespace NewUOAM.App;

public enum MapRenderMode
{
    /// <summary>Classic UO isometric-style 45° rotation, matching the game's own screen orientation
    /// (and the old UOAM's default view) - roads/rivers run diagonally, N points to the top-right.</summary>
    Rotated45,
    /// <summary>Plain top-down, north straight up, like a real atlas.</summary>
    NorthUp,
}

public partial class MainWindow : Window
{
    // Discrete zoom levels, ordered from most zoomed-out to most zoomed-in. Two different regimes:
    //
    // - pixelsPerTile >= 2 (D=1): the original even-integer-only zoom-in range. The rotated
    //   projection moves the screen by pixelsPerTile/2 for every 1-tile step in either world axis
    //   (see WorldToScreen); if that per-tile pixel step isn't a whole number, the tile grid's
    //   alignment to the pixel grid drifts by a fraction with every step the player takes, and
    //   nearest-neighbor sampling (Redraw) renders that drift as pixels "shimmering"/changing near
    //   tile edges on every move - even though nothing about the underlying map data changed.
    //   Keeping the per-tile pixel step an exact integer avoids that: any two exact-tile centers
    //   are guaranteed pixel-exactly aligned with each other.
    //
    // - pixelsPerTile < 2 (D=2,3,...): zoom-out beyond 1:1, where multiple world tiles collapse
    //   into a single screen pixel. Restricted to pixelsPerTile = 2/D for integer D so the same
    //   "any two centers are pixel-exactly aligned" guarantee still holds - see EnsureCache's
    //   cache-center snapping, which snaps to whole multiples of D tiles instead of whole tiles.
    //   (D itself is exactly recoverable per level here rather than back-computed from
    //   pixelsPerTile, since floating-point division/round-tripping isn't guaranteed exact.)
    private static (double Ppt, int D)[] BuildZoomLevels()
    {
        var list = new List<(double Ppt, int D)>();
        for (int d = 8; d >= 2; d--) list.Add((2.0 / d, d));
        for (int ppt = 2; ppt <= 24; ppt += 2) list.Add((ppt, 1));
        return list.ToArray();
    }
    private static readonly (double Ppt, int D)[] ZoomLevels = BuildZoomLevels();

    private UoClientData? _clientData;
    private WriteableBitmap? _bitmap;
    // private IPositionProvider? _orionProvider; // Varianta A, UI hidden (see StartOrionAsync)
    private IPositionProvider? _b2Provider;
    // private IPositionProvider? _proxyProvider; // B1 - odloženo, viz StartProxyButton_Click výše

    // Multiplayer relay (see NewUOAM.Positioning.Relay): _relayClient forwards our own position
    // out and surfaces other connected players' positions in. _remotePlayers is the latest known
    // state per player name; _remotePlayerViews is the matching set of WPF elements added to
    // RemotePlayersCanvas for each - kept alive across redraws and just repositioned/hidden (see
    // UpdateRemotePlayerOverlay), not recreated every frame. Dot shows a player within the current
    // viewport; Arrow (with its own reusable RotateTransform) shows one who's currently panned/
    // zoomed out of view, clamped to the viewport edge and pointing toward their real position.
    private RelayMultiplayerClient? _relayClient;
    // Latest position from whichever local provider is running - handed to a freshly created
    // relay client so it can register/report immediately (see StartRelayButton_Click).
    private PositionUpdate? _lastLocalUpdate;

    // Room chat (see RelayProtocol.ChatSendTag / ChatWindow) - opened lazily, only when the user
    // clicks into the map (focusing it) and starts typing while _relayClient is active; see
    // MapBorder_PreviewTextInput. Never created eagerly, discarded (not just hidden) whenever the
    // relay disconnects or the window itself is closed - nothing about chat is meant to outlive
    // the connection it happened on.
    private ChatWindow? _chatWindow;

    private readonly Dictionary<string, PositionUpdate> _remotePlayers = new();
    private readonly Dictionary<string, (Rectangle Dot, Polygon Arrow, RotateTransform ArrowRotation, TextBlock Label)> _remotePlayerViews = new();

    // How far inside the viewport edge an off-screen player's arrow indicator is clamped to - a
    // small visual margin so it doesn't sit flush against the window border.
    private const double RemoteEdgeMargin = 24.0;

    // Smoothly glides each remote player's marker between reported positions instead of snapping
    // to the new one - same ease-out technique as BeginPanAnimation/OnAnimationTick below, just
    // applied per remote player instead of to the local view. Without this, every relay report
    // made the marker visibly teleport, which read as "laggy, just like old UOAM" even though the
    // underlying data was already fresh - the user's own comparison after trying it live. One
    // shared CompositionTarget.Rendering subscription services every remote player's animation at
    // once (unsubscribed when none are in flight), same "costs nothing while idle" property as the
    // local pan animation.
    //
    // Deliberately its OWN duration constant, not a reuse of AnimationDurationMs above: the glide
    // itself adds latency (a marker takes this long to visually reach a freshly reported
    // position), so it needs to stay in step with RelayProtocol.ClientReportIntervalMs (currently
    // 50ms) - reusing the local view's 150ms pan-animation duration here would silently cancel out
    // most of the benefit of a fast report interval.
    private const double RemoteAnimationDurationMs = 60.0;
    private sealed class RemoteAnim
    {
        public double FromX, FromY, ToX, ToY;
        public long StartTicks;
    }
    private readonly Dictionary<string, RemoteAnim> _remoteAnims = new();
    private bool _remoteAnimRunning;

    // Static landmark markers (UOAM ".map" / ClassicUO-style ".csv" files - NewUOAM.MapData.Markers.
    // MarkerFileReader). Unlike remote players these never move, so there's no animation loop -
    // just repositioned every Redraw() (camera pan moves them, they don't move themselves) via
    // UpdateMarkersOverlay. One Image element per loaded marker entry, built once by
    // BuildMarkerViews when "Načíst markery" runs (not incrementally like remote players' views,
    // since the whole set is known up front rather than trickling in over the network).
    // Each marker remembers the file it came from, so Edit/Delete (right-click on a marker) can
    // rewrite exactly that file.
    private sealed record LoadedMarker(MarkerEntry Entry, string FilePath);
    private readonly List<LoadedMarker> _markers = new();
    private readonly List<(LoadedMarker Marker, Image Icon)> _markerViews = new();
    // Icon bitmaps loaded lazily per normalized icon name and cached/shared across every marker
    // using that name (hundreds of markers, far fewer distinct icon types) - see GetMarkerIcon.
    private readonly Dictionary<string, BitmapImage?> _markerIconCache = new(StringComparer.OrdinalIgnoreCase);
    // A marker whose icon name doesn't match any file in MarkerIconsDirectory (shard-specific NPC
    // types the bundled UOAM/Orion icon set has no picture for) falls back to this one instead of
    // rendering nothing.
    private const string FallbackMarkerIconName = "OTHER";
    private static readonly string MarkerIconsDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "MapIcons");

    private MapFacet? _currentFacet;
    private int _currentFacetIndex = -1;

    // _centerX/_centerY: the exact, current logical player position (integer tiles) - what we're
    // ultimately panning *toward*. _viewX/_viewY: the actual (possibly fractional, mid-animation)
    // position everything gets RENDERED relative to - see the animation fields/comment below for
    // why these two are different fields.
    private int _centerX, _centerY;
    private double _viewX, _viewY;
    private int? _markerX, _markerY;

    private MapRenderMode _renderMode = MapRenderMode.Rotated45;
    // _zoomIndex is the single source of truth (index into ZoomLevels); _pixelsPerTile is kept in
    // sync with it on every change, since the rest of the rendering code reads _pixelsPerTile
    // directly and there's no benefit to rewriting all of those call sites to index the array.
    private int _zoomIndex = Array.FindIndex(ZoomLevels, z => Math.Abs(z.Ppt - 4.0) < 0.0001);
    private double _pixelsPerTile = 4.0;
    private bool _mapOnlyMode;
    private bool _settingsPanelOpen = true;

    // Position updates can arrive much faster (every ~50ms from the Orion feed) than a full-window
    // repaint can complete. Coalesce them: at most one Redraw() is ever in flight/queued, always
    // using whatever the latest known position is by the time it actually runs.
    private bool _redrawPending;

    // Smoothly animates _viewX/_viewY from wherever it currently is to a new _centerX/_centerY
    // over AnimationDurationMs instead of snapping instantly - the render cache (below) already
    // guarantees a snap is pixel-exact with no resampling artifacts, but even a mathematically
    // perfect few-pixel jump every ~step still reads as "constant jittery movement" to the eye at
    // typical walking cadence (the user's own words: "pořád se to mění, je to nepříjemné pro oko").
    // This only changes HOW OFTEN and BY HOW MUCH _viewX/_viewY moves between redraws (many small
    // sub-pixel-rounded steps instead of one big one) - it does not reintroduce the earlier
    // shimmer bug, because the cache content itself is untouched during an animation; only the
    // integer crop offset read out of it changes, same as old UOAM just sliding its viewport.
    private const double AnimationDurationMs = 150.0;

    // Pre-fills the Multiplayer "Adresa host:port" field on first run (before anything's been
    // saved to AppSettings yet) with this project's own deployed relay server - the user asked
    // for this specifically so they don't have to type/remember the address themselves. Once they
    // connect once, AppSettings.RelayServerAddress takes over as normal and this default is never
    // consulted again unless the settings file is reset.
    private const string DefaultRelayServerAddress = "89.168.122.175:27980";
    private double _animFromX, _animFromY, _animToX, _animToY;
    private long _animStartTicks;
    private bool _animRunning;

    public MainWindow()
    {
        InitializeComponent();


        var settings = AppSettings.Load();
        ClientDirTextBox.Text = settings.ClientDirectory ?? "";
        ShowCoordinatesCheckBox.IsChecked = settings.ShowCoordinates;
        ShowCompassCheckBox.IsChecked = settings.ShowCompass;
        // string.IsNullOrWhiteSpace, not just "?? default" - SaveSettings() always writes
        // whatever's currently in the box, including "" if it was ever left blank when the app
        // closed, and "" ?? default never triggers (only a true null would).
        RelayServerTextBox.Text = string.IsNullOrWhiteSpace(settings.RelayServerAddress)
            ? DefaultRelayServerAddress
            : settings.RelayServerAddress;
        DisplayNameTextBox.Text = settings.MultiplayerDisplayName ?? "";
        PlayerColorGrid.ItemsSource = PlayerColors.Palette;
        PlayerColorPopup.PlacementTarget = PlayerColorButton; // not an ElementName binding, see the XAML
        SetPlayerColor(RelayProtocol.NormalizeColor(settings.MultiplayerColor) ?? RelayProtocol.DefaultPlayerColor);
        SelfMarkerColorGrid.ItemsSource = PlayerColors.Palette;
        SelfMarkerColorPopup.PlacementTarget = SelfMarkerColorButton;
        SetSelfMarkerColor(settings.SelfMarkerColor);
        RoomPasswordTextBox.Text = settings.RelayRoomPassword ?? "";
        MarkersDirTextBox.Text = settings.MarkersFolder ?? "";
        UpdateMarkersDirPlaceholder(); // TextChanged doesn't fire when both stay empty
        ShowMarkersCheckBox.IsChecked = settings.ShowMarkers;
        ShowChatInGameCheckBox.IsChecked = settings.ShowChatInGame;
        PanicSoundCheckBox.IsChecked = settings.PanicSound;
        ShowUnreadChatCheckBox.IsChecked = settings.ShowUnreadChat;
        AutoConnectRelayCheckBox.IsChecked = settings.AutoConnectRelay;
        ShowTrackMapCheckBox.IsChecked = settings.ShowTrackMap;
        (settings.TrackMapAlwaysOpen ? TrackMapAlwaysRadio : TrackMapPopupRadio).IsChecked = true;
        TrackSoundCheckBox.IsChecked = settings.TrackSound;
        _trackBounds = settings.TrackMapBounds;
        _trackMapOnly = settings.TrackMapOnlyMode;
        UpdateTrackSettingsEnabled();
        CoordinatesOverlay.Visibility = settings.ShowCoordinates ? Visibility.Visible : Visibility.Collapsed;
        UpdateCompassOverlay();
        _settingsPanelOpen = settings.SettingsPanelOpen;
        UpdateSettingsPanelVisibility();
        RestoreWindowPlacement(settings);
        _chatBounds = settings.ChatWindowBounds;
        _markerCategoryVisible = settings.MarkerCategories ?? new();
        RebuildMarkerRows(); // empty until markers load - shows the "nothing loaded" hint
        VersionMenuItem.Header = $"Verze {App.CurrentVersion}";
        // Version in the title (user's request 2026-10-01); a build from bin\ says so, to tell it
        // apart from the installed package when both run.
        Title = PackageInstaller.IsInstalledPackage(AppContext.BaseDirectory)
            ? $"new UOAM {App.CurrentVersion}"
            : $"new UOAM {App.CurrentVersion} (vývoj)";
        _applyingLoadedSettings = false;
        StartClientTracking();

        _uoaCommands.CommandReceived += OnGameCommand;
        // A map tracking a client via B2 takes only that client's commands (another map on this
        // PC may belong to the other one); without B2 it takes any.
        _uoaCommands.AcceptsClient = pid => (_b2Provider as ProcessMemoryPositionProvider)?.ProcessId is not { } tracked || tracked == pid;
        _uoaCommands.Problem += Log;
        _commandBridgeTimer.Tick += (_, _) => EnsureCommandBridge();
        _commandBridgeTimer.Start();

        Closing += (_, _) =>
        {
            // Closing the chat records its bounds; it would also keep the process alive on its
            // own otherwise (it has no Owner, see EnsureChatWindowOpen).
            _chatWindow?.Close();
            // Records its bounds/map-only mode (its Closing handler) and doesn't outlive the map.
            _trackWindow?.Close();
            _clientScanTimer.Stop();
            SaveSettings();
            _relayClient?.SendByeNow();
        };
        Closed += (_, _) => _uoaCommands.Dispose();

        // Auto-load on launch if a client path is already saved: skips the user having to click
        // "Načíst mapu" every time. (The UDP feed used to auto-start here too; dropped 2026-09-24
        // on the user's request together with hiding Varianta A.) Deferred to Loaded (not run inline
        // here) because Redraw() reads MapBorder.ActualWidth/Height, which is only valid after the
        // first layout pass. "Are the map files current" is already handled by the existing
        // mtime+size-based FacetColorMapDiskCache invalidation inside LoadMapAsync - no separate
        // freshness check is needed here.
        bool loadMap = !string.IsNullOrEmpty(settings.ClientDirectory) && System.IO.Directory.Exists(settings.ClientDirectory);
        Loaded += async (_, _) =>
        {
            if (loadMap) await AutoStartAsync();
            else if (App.ElevationDeclined) Log(ElevationDeclinedMessage);
            // After the map load, so its status lines don't overwrite a connect error.
            OpenTrackWindowIfAlways();
            // Before auto-connect: accepting an update restarts the map anyway.
            await CheckForUpdatesAsync(manual: false);
            if (settings.AutoConnectRelay) await AutoConnectRelayAsync();
        };
    }

    private async Task AutoStartAsync()
    {
        await LoadMapAsync();
        // The side panel is the marker list now, so markers load with the map (2026-09-28).
        if (System.IO.Directory.Exists(EffectiveMarkersDirectory)) await LoadMarkersAsync();
        // After the load, so its own status lines don't immediately overwrite the warning.
        if (App.ElevationDeclined) Log(ElevationDeclinedMessage);
    }

    private const string ElevationDeclinedMessage =
        "Mapa neběží jako správce (UAC odmítnuto) - Varianta B2 nejspíš nepůjde spustit, pokud klient běží jako správce.";

    // True while the constructor is copying the loaded settings into the controls. Setting a
    // checkbox's IsChecked there fires its Checked handler, which calls SaveSettings - at a point
    // where the fields below it (relay address, name, room password, markers folder...) are still
    // empty, so the file on disk got overwritten with them blank until the next clean save.
    private bool _applyingLoadedSettings = true; // true from construction on, so it also covers InitializeComponent()

    private void SaveSettings()
    {
        if (_applyingLoadedSettings) return;
        BuildSettings().Save();
    }

    private AppSettings BuildSettings() => new AppSettings
    {
        ClientDirectory = ClientDirTextBox.Text.Trim(),
        ShowCoordinates = ShowCoordinatesCheckBox.IsChecked == true,
        ShowCompass = ShowCompassCheckBox.IsChecked == true,
        RelayServerAddress = RelayServerTextBox.Text.Trim(),
        MultiplayerDisplayName = DisplayNameTextBox.Text.Trim(),
        MultiplayerColor = _playerColor,
        SelfMarkerColor = _selfMarkerColor == DefaultSelfMarkerColor ? null : _selfMarkerColor,
        RelayRoomPassword = RoomPasswordTextBox.Text.Trim(),
        MarkersFolder = MarkersDirTextBox.Text.Trim(),
        ShowMarkers = ShowMarkersCheckBox.IsChecked == true,
        ShowChatInGame = ShowChatInGameCheckBox.IsChecked == true,
        PanicSound = PanicSoundCheckBox.IsChecked == true,
        ShowUnreadChat = ShowUnreadChatCheckBox.IsChecked == true,
        AutoConnectRelay = AutoConnectRelayCheckBox.IsChecked == true,
        ShowTrackMap = ShowTrackMapCheckBox.IsChecked == true,
        TrackMapAlwaysOpen = TrackMapAlwaysRadio.IsChecked == true,
        TrackMapBounds = _trackBounds,
        TrackMapOnlyMode = _trackMapOnly,
        TrackSound = TrackSoundCheckBox.IsChecked == true,
        SettingsPanelOpen = _settingsPanelOpen,
        MainWindowBounds = WindowBounds.From(this),
        MapOnlyMode = _mapOnlyMode,
        ChatWindowBounds = _chatBounds,
        MarkerCategories = _markerCategoryVisible,
    };

    /// <summary>Reopens the map where it was last, in the same mode. A position on a monitor that
    /// is no longer connected is dropped (the window gets its default place), one partly off the
    /// screen is pulled back in (ScreenPlacement.FitToScreens).</summary>
    private void RestoreWindowPlacement(AppSettings settings)
    {
        if (settings.MainWindowBounds is { } saved && ScreenPlacement.FitToScreens(saved.ToRect()) is { } rect)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = rect.Left;
            Top = rect.Top;
            Width = rect.Width;
            Height = rect.Height;
            // Set before Show: it then maximizes on the monitor Left/Top point at.
            if (saved.Maximized && !settings.MapOnlyMode) WindowState = WindowState.Maximized;
        }
        if (settings.MapOnlyMode) SetMapOnlyMode(true);
    }

    private void OverlayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        CoordinatesOverlay.Visibility = ShowCoordinatesCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateCompassOverlay();
        SaveSettings();
    }

    /// <summary>Which letter goes in which corner depends on render mode: Rotated45 matches old
    /// UOAM's own orientation (screenshot: W top-left, N top-right, S bottom-left, E bottom-right,
    /// since north points to the top-right in that projection); NorthUp gets ordinary diagonal
    /// corner labels.</summary>
    private void UpdateCompassOverlay()
    {
        var vis = ShowCompassCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CompassTopLeft.Visibility = vis;
        CompassTopRight.Visibility = vis;
        CompassBottomLeft.Visibility = vis;
        CompassBottomRight.Visibility = vis;
        if (vis != Visibility.Visible) return;

        if (_renderMode == MapRenderMode.Rotated45)
        {
            CompassTopLeft.Text = "W";
            CompassTopRight.Text = "N";
            CompassBottomLeft.Text = "S";
            CompassBottomRight.Text = "E";
        }
        else
        {
            CompassTopLeft.Text = "NW";
            CompassTopRight.Text = "NE";
            CompassBottomLeft.Text = "SW";
            CompassBottomRight.Text = "SE";
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Vyber složku se soubory mapy (map0.mul / map0LegacyMUL.uop)" };
        if (dialog.ShowDialog() == true)
        {
            ClientDirTextBox.Text = dialog.FolderName;
        }
    }

    private async void LoadMapButton_Click(object sender, RoutedEventArgs e) => await LoadMapAsync();

    /// <summary>Loads the map from whatever's currently in ClientDirTextBox. Shared by the manual
    /// "Načíst mapu" button and by the startup auto-load. Returns whether a facet was successfully
    /// opened (used to gate the startup auto-start of the UDP feed on a real success).</summary>
    private async Task<bool> LoadMapAsync()
    {
        string dir = ClientDirTextBox.Text.Trim();
        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
        {
            Log("Neplatná složka klienta.");
            return false;
        }

        LoadMapButton.IsEnabled = false;
        try
        {
            _clientData?.Dispose();
            var clientData = new UoClientData(dir);
            _clientData = clientData;

            if (!clientData.TryGetFacet(0, out var facet))
            {
                Log("Nepodařilo se otevřít map0 z této složky.");
                return false;
            }

            // Draw the raw (un-cached-color) view immediately so there's something on screen
            // right away, then precompute every facet's color map in the background - once that
            // finishes, panning/zooming/rotating samples the precomputed bitmap instead of
            // re-decoding raw tiles on every repaint (see FacetColorMap).
            _currentFacet = facet;
            _currentFacetIndex = 0;
            // No client tracked yet: 0,0 (user's call, 2026-09-28 - same as when no client runs).
            _centerX = _centerY = 0;
            _viewX = _viewY = 0; // no animation for the initial default view - just snap
            _markerX = _markerY = null;
            Redraw();
            if (_lastLocalUpdate is { } last) ShowLocalPosition(last);
            Log($"Načteno: {facet.Name} ({facet.WidthTiles}x{facet.HeightTiles}). Předpočítávám mapy…");
            SaveSettings(); // remember the client path even if the app never closes cleanly

            var progress = new Progress<string>(msg => Log(msg));
            await clientData.PreloadAllColorMapsAsync(progress);

            if (_clientData == clientData) // user might have loaded a different folder meanwhile
            {
                Log("Mapy předpočítané - panning/zoom/otočení jsou teď rychlé.");
                RequestRedraw();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"Chyba při načítání: {ex.Message}");
            return false;
        }
        finally
        {
            LoadMapButton.IsEnabled = true;
        }
    }

    // Varianta A (Orion UDP) UI is hidden - see the commented-out block in MainWindow.xaml. Uncomment
    // both (and the _orionProvider field) to bring it back.
    /*
    private async void StartOrionButton_Click(object sender, RoutedEventArgs e) => await StartOrionAsync();

    /// <summary>Starts the Orion UDP position feed (the "Start (UDP)" button).</summary>
    private async Task StartOrionAsync()
    {
        if (_orionProvider is not null) return;

        var provider = new OrionUdpPositionProvider();
        provider.PositionChanged += OnPositionChanged;
        provider.StatusMessage += (_, msg) => Dispatcher.Invoke(() => Log(msg));

        try
        {
            await provider.StartAsync();
            _orionProvider = provider;
            StartOrionButton.IsEnabled = false;
            StopOrionButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Log($"Nepodařilo se spustit UDP listener: {ex.Message}");
        }
    }

    private async void StopOrionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_orionProvider is null) return;
        await _orionProvider.StopAsync();
        _orionProvider.Dispose();
        _orionProvider = null;
        StartOrionButton.IsEnabled = true;
        StopOrionButton.IsEnabled = false;
    }
    */

    // ---- Automatic client tracking (Varianta B2), user's request 2026-09-28: no Start/Stop.
    // The map tracks the oldest running OrionUO64.exe on its own and follows the choice in the
    // Klient menu. When the tracked client exits it moves to the next running one; with none
    // running it shows 0,0 and keeps looking.
    //
    // Cost: a scan every ClientScanInterval is one process snapshot plus a window-title lookup per
    // OrionUO64.exe - ~1-3 ms, and it runs on a pool thread, so it never holds up a map frame. A
    // client exiting is noticed immediately through Process.Exited, not by the scan, and a new
    // client is picked up by the next scan (within 2s). Memory reading itself is unchanged: one
    // client at a time, 3 small reads every 50ms. ----

    private sealed record B2ProcessItem(int Pid, string Display);

    private static readonly TimeSpan ClientScanInterval = TimeSpan.FromSeconds(2);
    private readonly DispatcherTimer _clientScanTimer = new() { Interval = ClientScanInterval };
    private List<B2ProcessItem> _clientItems = new();
    // Clients whose memory couldn't be opened (e.g. the map isn't admin and the client is) - not
    // retried automatically, only when picked by hand, so a failure isn't retried every 2s.
    private readonly HashSet<int> _failedClientPids = new();
    private Process? _trackedProcess; // only for its Exited event
    private bool _switchingClient;
    private bool _scanningClients;

    private int? TrackedPid => (_b2Provider as ProcessMemoryPositionProvider)?.ProcessId;

    private void StartClientTracking()
    {
        _clientScanTimer.Tick += async (_, _) => await ScanClientsAsync();
        _clientScanTimer.Start();
        _ = ScanClientsAsync();
    }

    /// <summary>Refreshes the client list and makes sure a running client is tracked.</summary>
    private async Task ScanClientsAsync()
    {
        if (_switchingClient || _scanningClients) return;
        _scanningClients = true;
        List<B2ProcessItem> items;
        try
        {
            // Off the UI thread: the scan is cheap, but it then can't delay a map frame at all.
            items = await Task.Run(() => ProcessMemoryPositionProvider.FindCandidateProcesses()
                .Select(c => new B2ProcessItem(c.ProcessId, string.IsNullOrWhiteSpace(c.WindowTitle) ? $"PID {c.ProcessId}" : $"{c.WindowTitle} (PID {c.ProcessId})"))
                .ToList());
        }
        finally
        {
            _scanningClients = false;
        }
        _clientItems = items; // the Klient menu is built from this whenever it opens
        _failedClientPids.IntersectWith(items.Select(i => i.Pid)); // forget clients that are gone

        if (TrackedPid is { } tracked && items.Any(i => i.Pid == tracked)) return;

        var next = items.FirstOrDefault(i => !_failedClientPids.Contains(i.Pid));
        if (next is not null)
            await TrackClientAsync(next.Pid);
        else if (_b2Provider is not null)
            await TrackClientAsync(null); // the tracked client is gone and no other one runs
    }

    /// <summary>Builds the Klient menu from the last scan: every running client, the tracked one
    /// checked; clicking another switches to it.</summary>
    private void ClientMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ClientMenu)) return; // bubbled from a nested item
        ClientMenu.Items.Clear();
        foreach (var client in _clientItems)
        {
            var item = new MenuItem
            {
                Header = client.Display.Replace("_", "__"), // "_" would mark an access key
                IsCheckable = true, // exposes the check to UI Automation; the menu is rebuilt on every open anyway
                IsChecked = client.Pid == TrackedPid,
            };
            item.Click += async (_, _) =>
            {
                if (client.Pid == TrackedPid) return;
                _failedClientPids.Remove(client.Pid); // picked by hand: try again even if it failed before
                await TrackClientAsync(client.Pid);
            };
            ClientMenu.Items.Add(item);
        }
        if (_clientItems.Count == 0)
            ClientMenu.Items.Add(new MenuItem { Header = "Žádný klient OrionUO neběží", IsEnabled = false });
        ClientMenu.Items.Add(new Separator());
        var hint = new MenuItem
        {
            Header = "Mapa sama sleduje první spuštěný klient",
            IsEnabled = false,
            ToolTip = "Když sledovaný klient zavřeš, mapa přejde na další běžící. Když žádný neběží, ukazuje 0,0 a čeká na spuštění klienta.",
        };
        ToolTipService.SetShowOnDisabled(hint, true);
        ClientMenu.Items.Add(hint);
    }

    /// <summary>Switches memory reading to the client <paramref name="pid"/>, or to none (the map
    /// then shows 0,0).</summary>
    private async Task TrackClientAsync(int? pid)
    {
        if (_switchingClient) return;
        _switchingClient = true;
        try
        {
            if (_b2Provider is not null)
            {
                var old = _b2Provider;
                _b2Provider = null;
                await old.StopAsync();
                old.Dispose();
            }
            _trackedProcess?.Dispose();
            _trackedProcess = null;

            if (pid is not { } p)
            {
                ShowNoPosition("žádný klient");
                return;
            }
            // Until the new client reports (it may be at the login screen), don't keep showing the
            // previous client's character.
            ShowNoPosition("čekám na pozici");

            var provider = new ProcessMemoryPositionProvider(p);
            provider.PositionChanged += OnPositionChanged;
            provider.StatusMessage += (_, msg) => Dispatcher.Invoke(() => Log(msg));
            try
            {
                await provider.StartAsync();
            }
            catch (Exception ex)
            {
                provider.Dispose();
                _failedClientPids.Add(p);
                Log($"Nepodařilo se číst paměť klienta PID {p}: {ex.Message}"
                    + (App.IsAdministrator ? "" : " Spusť mapu jako správce."));
                ShowNoPosition("žádný klient");
                return;
            }
            _b2Provider = provider;

            try
            {
                var process = Process.GetProcessById(p);
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => Dispatcher.BeginInvoke(async () => await ScanClientsAsync());
                _trackedProcess = process;
            }
            catch (Exception)
            {
                // Already gone, or no access to wait on it: the periodic scan notices the exit.
            }
        }
        finally
        {
            _switchingClient = false;
        }
    }

    // What the relay gets while no client reports a position: 0,0 under the last known name, so
    // the room sees you at 0,0 like your own map does, but you stay connected and can chat (user's
    // call, 2026-09-29). Deliberately not _lastLocalUpdate: directions to the shared marker or a
    // panicking room mate must not be computed from this 0,0.
    private PositionUpdate? _noClientRelayUpdate;

    /// <summary>No position to show (no client, or a new one that hasn't reported yet): the map
    /// goes to 0,0 (user's call) without a player marker, and so does your position on the relay.</summary>
    private void ShowNoPosition(string note)
    {
        // Keep the name: a different one would look like someone else joining the room. Without
        // any name yet (no display name, never had a client) there is nothing to report.
        string? name = _lastLocalUpdate?.CharacterName ?? _noClientRelayUpdate?.CharacterName;
        _noClientRelayUpdate = name is null ? null : PositionUpdate.Now(0, 0, 0, 0, name);
        if (_noClientRelayUpdate is not null) _relayClient?.ReportLocalPosition(_noClientRelayUpdate);

        _lastLocalUpdate = null;
        _markerX = _markerY = null;
        PositionText.Text = $"X: -  Y: -  Z: -  Map: -  ({note})";
        CoordinatesOverlay.Text = "";
        if (_clientData is null) return;
        if (ViewFollowsPlayer)
            BeginPanAnimation(0, 0);
        else
            RequestRedraw();
    }

    private async void StartRelayButton_Click(object sender, RoutedEventArgs e)
    {
        StopAutoConnectRetry(); // a manual connect replaces any pending automatic retry
        await ConnectRelayAsync(automatic: false);
    }

    /// <summary>Connects to the relay. Returns the numbered error (RelayError*), or null when
    /// connected (or already connected). <paramref name="automatic"/> = the connect at startup
    /// (AutoConnectRelayCheckBox): errors then go only to the status bar, no dialog.</summary>
    private async Task<int?> ConnectRelayAsync(bool automatic)
    {
        if (_relayClient is not null) return null;

        int? Fail(int code, string explanation)
        {
            if (automatic) Log($"Automatické připojení k mapě selhalo - chyba {code}: {explanation}");
            else ShowRelayError(code, explanation);
            return code;
        }

        string addr = RelayServerTextBox.Text.Trim();
        int colon = addr.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(addr[(colon + 1)..], out int port) || port is < 1 or > 65535)
            return Fail(RelayErrorInvalidAddress, "Neplatná adresa relay serveru. Zadej ji ve tvaru host:port, např. 89.168.122.175:27980.");
        string host = addr[..colon];

        string roomPassword = RoomPasswordTextBox.Text.Trim();
        if (string.IsNullOrEmpty(roomPassword))
            return Fail(RelayErrorMissingRoomPassword, "Chybí heslo místnosti. Heslo ti musí dát ten, kdo místnost založil.");

        // Empty = "use my character name" (allowed); non-empty but invisible (only spaces, tabs,
        // zero-width characters, blank filler letters...) is refused - such a player would be an
        // unlabeled dot on everyone's map and an empty sender in chat. The raw, untrimmed text is
        // checked on purpose: Trim() would silently turn "   " into "" and let it through.
        string displayName = DisplayNameTextBox.Text;
        if (displayName.Length > 0 && !RelayProtocol.IsVisibleName(displayName))
            return Fail(RelayErrorInvisibleName,
                "Zobrazované jméno je neviditelné - obsahuje jen mezery, tabulátory nebo jiné neviditelné znaky. " +
                "Zadej jméno s alespoň jedním viditelným znakem, nebo pole nech úplně prázdné (pak se použije jméno postavy ze hry).");

        var client = new RelayMultiplayerClient(host, port, roomPassword, displayName.Trim(), _playerColor);
        client.RemotePlayerUpdated += OnRemotePlayerUpdated;
        client.RemotePlayerJoined += OnRemotePlayerJoined;
        client.RemotePlayerLeft += OnRemotePlayerLeft;
        client.StatusMessage += (_, msg) => Dispatcher.Invoke(() => Log(msg));
        // BeginInvoke, not Invoke: Kicked is raised from the client's receive loop, and the
        // teardown awaits that very loop - Invoke would deadlock.
        client.Kicked += (_, msg) => Dispatcher.BeginInvoke(new Action(async () => await DisconnectRelayAsync(msg)));
        client.ChatMessageReceived += (_, msg) => Dispatcher.Invoke(() => OnChatMessageReceived(msg));
        client.SharedMarkerChanged += OnSharedMarkerChanged;
        client.PanicChanged += OnPanicChanged;
        client.TrackReported += (_, r) => Dispatcher.BeginInvoke(() => OnTrackReported(r));
        // Shared markers (MainWindow.SharedMarks.cs): changes come in bursts, refreshed coalesced.
        client.SharedMarksChanged += (_, _) => Dispatcher.BeginInvoke(RequestSharedMarksRefresh);
        client.MySharedMarksChanged += (_, _) => Dispatcher.BeginInvoke(RequestSharedMarksRefresh);
        client.SharedMarksAnnounced += (_, a) => Dispatcher.BeginInvoke(() => OnSharedMarksAnnounced(a));
        // Same BeginInvoke reasoning as Kicked (raised from the receive loop the teardown awaits).
        client.NameTaken += (_, name) => Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_relayClient != client) return;
            await DisconnectRelayAsync($"Multiplayer: odpojeno - jméno \"{name}\" už v místnosti někdo používá.");
            ShowRelayError(RelayErrorNameTaken, NameTakenMessage);
        }));

        // Seed the relay with where we already are. Position providers only push on change (B2)
        // or on a ~2s heartbeat (Orion script), so a player standing still at connect time would
        // otherwise have no position - and, without a display name, no name either - which meant
        // the server never registered them: they got no join notices and nobody saw them on the
        // map until they took a step (the exact "works only after moving" report).
        // With no client running, that's 0,0 (see _noClientRelayUpdate).
        if ((_lastLocalUpdate ?? _noClientRelayUpdate) is { } seed) client.ReportLocalPosition(seed);

        StartRelayButton.IsEnabled = false; // StartAsync waits up to ~3s for the server's answer
        Log($"Multiplayer: připojuji k {host}:{port}…");
        try
        {
            var result = await client.StartAsync();
            if (result != RelayConnectResult.Connected)
            {
                client.Dispose();
                StartRelayButton.IsEnabled = true;
                return result switch
                {
                    RelayConnectResult.NameTaken => Fail(RelayErrorNameTaken, NameTakenMessage),
                    // Deliberately doesn't say "wrong password" - the user asked for an error that
                    // points to the map's admin without revealing why.
                    RelayConnectResult.Denied => Fail(RelayErrorAccessDenied, "Nepodařilo se připojit k mapě. Kontaktujte admina mapy."),
                    _ => Fail(RelayErrorNoResponse, $"Relay server {host}:{port} neodpovídá. Zkontroluj adresu a připojení k internetu, případně kontaktuj admina mapy."),
                };
            }
            _relayClient = client;
            StopRelayButton.IsEnabled = true;
            SaveSettings();
            // Shared markers that arrived during the connect (before _relayClient was set), and
            // the "Sdílené v místnosti" section, which only exists while connected.
            RequestSharedMarksRefresh();
            return null;
        }
        catch (Exception ex)
        {
            StartRelayButton.IsEnabled = true;
            client.Dispose();
            return Fail(RelayErrorConnectFailed, $"Nepodařilo se připojit k relay serveru: {ex.Message}");
        }
    }

    // ---- Connecting at startup (Online > "Připojit automaticky při spuštění", user's request
    // 2026-09-29). Runs once the window is loaded, whether or not a map/client is ready (the relay
    // client waits for a name by itself). Errors go to the status bar, never a dialog. Errors that
    // settings can't fix by waiting (bad address, no password, invisible name, access denied, name
    // taken) aren't retried; no answer / network failure (106/104, e.g. the network isn't up yet
    // right after boot) is retried AutoConnectAttempts times, AutoConnectRetrySeconds apart. ----

    private const int AutoConnectAttempts = 5;
    private const int AutoConnectRetrySeconds = 20;
    private DispatcherTimer? _autoConnectRetryTimer;
    private int _autoConnectAttempt;

    private async Task AutoConnectRelayAsync()
    {
        _autoConnectAttempt++;
        if (_autoConnectAttempt == 1) Log("Připojuji se automaticky k mapě…");
        int? error = await ConnectRelayAsync(automatic: true);
        if (error is not (RelayErrorNoResponse or RelayErrorConnectFailed)) return;

        if (_autoConnectAttempt >= AutoConnectAttempts)
        {
            Log($"Automatické připojení k mapě se nepovedlo ani po {AutoConnectAttempts} pokusech (chyba {error}). Připoj se ručně v Online → Připojit k mapě.");
            return;
        }
        Log($"Automatické připojení: server neodpovídá (chyba {error}), zkusím to znovu za {AutoConnectRetrySeconds} s (pokus {_autoConnectAttempt + 1}/{AutoConnectAttempts}).");
        _autoConnectRetryTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(AutoConnectRetrySeconds) };
        _autoConnectRetryTimer.Tick -= OnAutoConnectRetryTick;
        _autoConnectRetryTimer.Tick += OnAutoConnectRetryTick;
        _autoConnectRetryTimer.Start();
    }

    private async void OnAutoConnectRetryTick(object? sender, EventArgs e)
    {
        _autoConnectRetryTimer?.Stop();
        // The user may have connected by hand or turned the option off in the meantime.
        if (_relayClient is not null || AutoConnectRelayCheckBox.IsChecked != true) return;
        await AutoConnectRelayAsync();
    }

    private void StopAutoConnectRetry() => _autoConnectRetryTimer?.Stop();

    private void AutoConnectRelayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoConnectRelayCheckBox.IsChecked != true) StopAutoConnectRetry();
        SaveSettings();
    }

    // Numbered multiplayer connect errors - a number is easier to report back ("hází mi to 103")
    // than paraphrasing a message. Keep the numbers stable once shipped.
    private const int RelayErrorInvalidAddress = 101;
    private const int RelayErrorMissingRoomPassword = 102;
    private const int RelayErrorInvisibleName = 103;
    private const int RelayErrorConnectFailed = 104;
    private const int RelayErrorAccessDenied = 105;   // unknown room password - message must not say so
    private const int RelayErrorNoResponse = 106;
    private const int RelayErrorNameTaken = 107;      // name already used by another active player in the same room

    private const string NameTakenMessage =
        "Toto jméno už v místnosti někdo používá. Změň zobrazované jméno (nebo sleduj jinou postavu) a připoj se znovu.";

    // ---- Player color picker (PlayerColorButton + PlayerColorPopup in XAML) ----

    private string _playerColor = RelayProtocol.DefaultPlayerColor;

    private void SetPlayerColor(string hex)
    {
        _playerColor = RelayProtocol.NormalizeColor(hex) ?? RelayProtocol.DefaultPlayerColor;
        PlayerColorSwatch.Background = PlayerColors.ToBrush(_playerColor);
        PlayerColorText.Text = $"#{_playerColor}";
    }

    private void PlayerColorButton_Click(object sender, RoutedEventArgs e)
    {
        PlayerColorHexTextBox.Text = $"#{_playerColor}";
        PlayerColorHexError.Visibility = Visibility.Collapsed;
        PlayerColorPopup.IsOpen = true;
    }

    /// <summary>A swatch in either color grid (ColorSwatchTemplate) - whichever popup is open.</summary>
    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex }) return;
        if (SelfMarkerColorPopup.IsOpen) ApplySelfMarkerColor(hex);
        else ApplyPickedColor(hex);
    }

    private void PlayerColorHexApply_Click(object sender, RoutedEventArgs e) => ApplyCustomHex();

    private void PlayerColorHexTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyCustomHex();
        e.Handled = true;
    }

    private void ApplyCustomHex()
    {
        string? hex = RelayProtocol.NormalizeColor(PlayerColorHexTextBox.Text);
        string? error = hex is null ? "Zadej barvu ve tvaru #RRGGBB, např. #FF8800."
            : !PlayerColors.IsReadable(hex) ? "Tahle barva je moc tmavá - na mapě ani v chatu by nebyla čitelná. Zkus světlejší."
            : null;
        if (error is not null)
        {
            PlayerColorHexError.Text = error;
            PlayerColorHexError.Visibility = Visibility.Visible;
            return;
        }
        ApplyPickedColor(hex!);
    }

    /// <summary>Applies immediately, even mid-connection: the relay client sends its color with
    /// every report/hello (see RelayMultiplayerClient.Color).</summary>
    private void ApplyPickedColor(string hex)
    {
        SetPlayerColor(hex);
        PlayerColorPopup.IsOpen = false;
        SaveSettings();
        if (_relayClient is not null) _relayClient.Color = _playerColor;
    }

    // ---- Your own marker's color (Mapa > Nastavení > Zobrazení, 2026-09-29): the square and the
    // edge arrow, only on your own map (others see your player color). Any color is allowed - it's
    // drawn over the map, not on the dark UI, so PlayerColors.IsReadable doesn't apply. ----

    private const string DefaultSelfMarkerColor = "FF0000";
    private string _selfMarkerColor = DefaultSelfMarkerColor;
    private (byte R, byte G, byte B) _selfMarkerRgb = (255, 0, 0);

    private void SetSelfMarkerColor(string? hex)
    {
        _selfMarkerColor = RelayProtocol.NormalizeColor(hex) ?? DefaultSelfMarkerColor;
        _selfMarkerRgb = (Convert.ToByte(_selfMarkerColor[0..2], 16), Convert.ToByte(_selfMarkerColor[2..4], 16), Convert.ToByte(_selfMarkerColor[4..6], 16));
        Brush brush = PlayerColors.ToBrush(_selfMarkerColor);
        SelfMarkerColorSwatch.Background = brush;
        SelfMarkerColorText.Text = _selfMarkerColor == DefaultSelfMarkerColor ? $"#{_selfMarkerColor} (výchozí)" : $"#{_selfMarkerColor}";
        SelfMarkerColorResetButton.IsEnabled = _selfMarkerColor != DefaultSelfMarkerColor;
        SelfArrow.Fill = brush;
    }

    private void ApplySelfMarkerColor(string hex)
    {
        SetSelfMarkerColor(hex);
        SelfMarkerColorPopup.IsOpen = false;
        SaveSettings();
        RequestRedraw(); // the square is drawn into the bitmap
    }

    private void SelfMarkerColorButton_Click(object sender, RoutedEventArgs e)
    {
        SelfMarkerColorHexTextBox.Text = $"#{_selfMarkerColor}";
        SelfMarkerColorHexError.Visibility = Visibility.Collapsed;
        SelfMarkerColorPopup.IsOpen = true;
    }

    private void SelfMarkerColorReset_Click(object sender, RoutedEventArgs e) => ApplySelfMarkerColor(DefaultSelfMarkerColor);

    private void SelfMarkerColorHexApply_Click(object sender, RoutedEventArgs e) => ApplySelfMarkerCustomHex();

    private void SelfMarkerColorHexTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplySelfMarkerCustomHex();
        e.Handled = true;
    }

    private void ApplySelfMarkerCustomHex()
    {
        if (RelayProtocol.NormalizeColor(SelfMarkerColorHexTextBox.Text) is { } hex)
        {
            ApplySelfMarkerColor(hex);
            return;
        }
        SelfMarkerColorHexError.Text = "Zadej barvu ve tvaru #RRGGBB, např. #FF8800.";
        SelfMarkerColorHexError.Visibility = Visibility.Visible;
    }

    private void ShowRelayError(int code, string explanation)
    {
        Log($"Chyba {code}: {explanation}");
        MessageBox.Show(this, explanation, $"Multiplayer - chyba {code}", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void StopRelayButton_Click(object sender, RoutedEventArgs e) =>
        await DisconnectRelayAsync("Multiplayer: odpojeno.");

    /// <summary>Single teardown path for every way a relay connection ends (Odpojit button, room
    /// deleted by the admin). Always ends by writing <paramref name="statusMessage"/> to the
    /// status bar - before this existed, Odpojit changed nothing there, so the bar kept saying
    /// "připojeno k ..." after disconnecting (a real user report).</summary>
    private async Task DisconnectRelayAsync(string statusMessage)
    {
        if (_relayClient is null) return;
        var client = _relayClient;
        _relayClient = null; // first, so a second click/kick during the await can't run this twice
        StopRelayButton.IsEnabled = false;
        await client.StopAsync();
        client.Dispose();
        StartRelayButton.IsEnabled = true;
        Log(statusMessage);

        // The shared marker belongs to the room - it's gone for us once we leave it.
        _sharedMarker = null;
        _sharedMarkerRepeatTimer.Stop();
        RequestRedraw();
        // So are everyone's shared markers (saved copies stay; ours stop being shared).
        ClearSharedMarks();

        // Same for Panic! states, ours and everyone else's.
        FinishPanicPeekNow();
        _myPanic = false;
        _roomPanic = null;
        _panicPlayers.Clear();
        _roomPlayers.Clear();
        UpdatePanicState();

        foreach (string name in _remotePlayers.Keys.ToList()) RemoveRemotePlayerView(name);
        _remotePlayers.Clear();
        _remoteAnims.Clear();
        if (_remoteAnimRunning)
        {
            CompositionTarget.Rendering -= OnRemoteAnimationTick;
            _remoteAnimRunning = false;
        }

        // Chat is scoped to this connection - nothing about it should outlive it (see
        // ChatWindow's own doc comment). Close() (not Hide()) triggers the Closed handler below,
        // which clears _chatWindow.
        _chatWindow?.Close();
        ClearChatHistory();
    }

    private void OpenAdminButton_Click(object sender, RoutedEventArgs e)
    {
        var admin = new AdminWindow(RelayServerTextBox.Text.Trim()) { Owner = this };
        admin.Show();
    }

    /// <summary>Click-to-focus-then-type chat trigger (MapBorder.PreviewTextInput below) routes
    /// here for both "open a fresh window" and "an already-open window received a message" -
    /// creates the window on first use only, never eagerly at connect time.</summary>
    private void EnsureChatWindowOpen()
    {
        if (_chatWindow is not null) return;

        // Deliberately NOT Owner = this: an owned window always stays above its owner and so
        // became always-on-top whenever the map was (map-only mode). The chat has its own
        // always-on-top, toggled by double-clicking it (ChatWindow compact mode).
        var chat = new ChatWindow(_chatHistory) { WindowStartupLocation = WindowStartupLocation.Manual };
        _unreadChat = 0; // opening the chat shows everything
        UpdateChatUnreadBadge();
        var rect = PlaceChatWindow(new Size(chat.Width, chat.Height));
        chat.Left = rect.Left;
        chat.Top = rect.Top;
        chat.Width = rect.Width;
        chat.Height = rect.Height;
        chat.MessageSubmitted += (_, text) => _relayClient?.SendChatMessage(text);
        chat.PlayersProvider = DescribeRoomPlayers;
        chat.Closing += (_, _) => { _chatBounds = WindowBounds.From(chat) ?? _chatBounds; SaveSettings(); };
        chat.Closed += (_, _) => _chatWindow = null;
        _chatWindow = chat;
        chat.Show();
        // The map is always on top in map-only mode, so a normal chat window would open hidden
        // under it (user report 2026-09-28). Compact mode has its own always-on-top.
        if (_mapOnlyMode) chat.SetCompact(true);
    }

    // The chat's last position (AppSettings.ChatWindowBounds), updated whenever it closes.
    private WindowBounds? _chatBounds;

    /// <summary>Where the chat was last, if that's still on a connected monitor; otherwise right
    /// next to the map (ScreenPlacement.PlaceNextTo), and over the map only if nothing fits.</summary>
    private Rect PlaceChatWindow(Size defaultSize)
    {
        if (_chatBounds is not null && ScreenPlacement.FitToScreens(_chatBounds.ToRect()) is { } saved)
            return saved;
        var map = WindowBounds.From(this)?.ToRect() ?? new Rect(Left, Top, ActualWidth, ActualHeight);
        if (WindowState != WindowState.Maximized && ScreenPlacement.PlaceNextTo(map, defaultSize) is { } next)
            return next;
        return new Rect(
            Left + Math.Max(0, (ActualWidth - defaultSize.Width) / 2),
            Top + Math.Max(0, (ActualHeight - defaultSize.Height) / 2),
            defaultSize.Width, defaultSize.Height);
    }

    // ---- Chat history (user's request 2026-09-29): everything since connecting, kept in memory
    // whether the chat window is open or not; the window only shows it. Never written to disk,
    // dropped on disconnect (ClearChatHistory) and with the app. Messages from others that arrive
    // while the window is closed count as unread (the counter next to the top-right compass
    // letter, ChatUnreadBadge; it can be turned off in Online > Připojit k mapě). ----

    private const int MaxChatHistory = 200;
    private readonly System.Collections.ObjectModel.ObservableCollection<ChatMessageViewModel> _chatHistory = new();
    private int _unreadChat;

    private void AddChatLine(string sender, string text, string? colorHex, bool fromOthers) =>
        AppendChat(new ChatMessageViewModel(sender, text, PlayerColors.ToBrush(colorHex)), countsAsUnread: fromOthers);

    /// <summary>A gray, sender-less notice line (someone joined/left, panic, shared marker...).</summary>
    private void AddChatSystemLine(string text) => AppendChat(ChatMessageViewModel.System(text), countsAsUnread: false);

    private void AppendChat(ChatMessageViewModel line, bool countsAsUnread)
    {
        _chatHistory.Add(line);
        while (_chatHistory.Count > MaxChatHistory) _chatHistory.RemoveAt(0);
        if (countsAsUnread && _chatWindow is null)
        {
            _unreadChat++;
            UpdateChatUnreadBadge();
        }
    }

    private void ClearChatHistory()
    {
        _chatHistory.Clear();
        _unreadChat = 0;
        UpdateChatUnreadBadge();
    }

    private void UpdateChatUnreadBadge()
    {
        // The checkbox's Checked handler runs during InitializeComponent, before the badge exists.
        if (ChatUnreadBadge is null) return;
        bool show = _unreadChat > 0 && ShowUnreadChatCheckBox.IsChecked == true;
        ChatUnreadText.Text = _unreadChat > 99 ? "99+" : _unreadChat.ToString();
        ChatUnreadBadge.ToolTip = $"Nepřečtené zprávy v chatu: {_unreadChat} (kliknutím otevřeš chat)";
        ChatUnreadBadge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowUnreadChatCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateChatUnreadBadge();
        SaveSettings();
    }

    /// <summary>On button down, marked handled, so the map's own click handling (focus, drag,
    /// double-click = map-only mode) doesn't run too.</summary>
    private void ChatUnreadBadge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_relayClient is null) return;
        EnsureChatWindowOpen();
        _chatWindow!.Activate();
        _chatWindow.FocusInput();
    }

    /// <summary>A received message never opens the chat window by itself (the user asked for
    /// that: the chat is opened only by typing into the map, and incoming messages show in the
    /// game anyway). It goes into the history, and counts as unread while the window is closed.</summary>
    private void OnChatMessageReceived(RelayMultiplayerClient.ChatMessage msg)
    {
        // Private: our own echo shows "Me -> Them", one sent to us "Them (soukromě)".
        string sender = !msg.Private ? msg.Name
            : msg.To is not null ? $"{msg.Name} -> {msg.To}"
            : $"{msg.Name} (soukromě)";
        AddChatLine(sender, msg.Message, msg.Color, fromOthers: msg.Name != _relayClient?.PlayerName);
        ShowInGame($"[Mapa] {sender}: {msg.Message}");
    }

    // ---- In-game chat display (UOAssist API, see UoAssistTextSender) ----

    // Sends are chained so messages arrive in the game in the same order they arrived here, and
    // run off the UI thread (each send can block up to ~1s if the client is busy/hung).
    private Task _inGameSendChain = Task.CompletedTask;
    private bool _inGameUnavailableLogged;

    private void ShowChatInGameCheckBox_Changed(object sender, RoutedEventArgs e) => SaveSettings();

    /// <summary>Mirrors a map-chat line into the UO client as a local system message, the way old
    /// UOAM did. Targets the client tracked by Varianta B2 (so multiboxing shows it in the right
    /// window); without B2 running, falls back to the only client exposing the UOAssist API, if
    /// there's exactly one. Silently does nothing if no client can take it (logged once).</summary>
    /// <param name="overhead">Over the player's own head (like old UOAM's "Shared Marker" text)
    /// instead of a bottom-left system message.</param>
    /// <param name="targetPid">Send to this client instead (the answer to a command typed there).</param>
    /// <param name="force">Show even with "Zobrazovat chat mapy ve hře" off - for answers to the
    /// player's own in-game commands, which would otherwise seem to do nothing.</param>
    /// <param name="hue">UO hue instead of the default (green sysmsg / light-blue overhead).</param>
    private void ShowInGame(string text, bool overhead = false, int? targetPid = null, bool force = false, ushort? hue = null)
    {
        if (!force && ShowChatInGameCheckBox.IsChecked != true) return;
        int? trackedPid = targetPid ?? (_b2Provider as ProcessMemoryPositionProvider)?.ProcessId;

        _inGameSendChain = _inGameSendChain.ContinueWith(_ =>
        {
            int? pid = trackedPid;
            if (pid is null)
            {
                var all = UoAssistTextSender.FindAllProcessIds();
                if (all.Count == 1) pid = all[0];
            }
            IntPtr hwnd = pid is { } p ? UoAssistTextSender.FindWindowForProcess(p) : IntPtr.Zero;
            bool ok = overhead
                ? UoAssistTextSender.DisplayOverheadText(hwnd, text, hue ?? UoAssistTextSender.SharedMarkerHue)
                : UoAssistTextSender.DisplaySystemText(hwnd, text, hue ?? UoAssistTextSender.DefaultHue);
            if (!ok && !_inGameUnavailableLogged)
            {
                _inGameUnavailableLogged = true;
                Dispatcher.BeginInvoke(() => Log(trackedPid is null
                    ? "Chat ve hře: nevím, do kterého klienta psát - mapa zatím nesleduje žádný klient."
                    : "Chat ve hře: sledovaný klient nenabízí rozhraní UOAssist (běží v něm Orion Assistant a je přihlášený?)."));
            }
            else if (ok) _inGameUnavailableLogged = false;
        }, TaskScheduler.Default);
    }

    private void OnRemotePlayerUpdated(object? sender, (string Name, PositionUpdate Update, string Color) e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            _roomPlayers.Add(e.Name);
            _remotePlayers[e.Name] = e.Update;
            EnsureRemotePlayerView(e.Name);
            ApplyRemotePlayerColor(e.Name, e.Color);
            BeginRemoteAnimation(e.Name, e.Update.X, e.Update.Y);
        }));
    }

    /// <summary>Join/leave notices go into the chat history (AddChatSystemLine) but never open the
    /// chat window or count as unread. The map needs no
    /// handling here: the server follows a join with the newcomer's position right away (see
    /// RelayServer.AnnounceJoinAsync), which OnRemotePlayerUpdated handles as usual.</summary>
    private void OnRemotePlayerJoined(object? sender, (string Name, string Color) e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            _roomPlayers.Add(e.Name);
            AddChatSystemLine($"{e.Name} se připojil(a) k mapě.");
            ShowInGame($"[Mapa] {e.Name} se připojil(a) k mapě.");
        }));
    }

    private void OnRemotePlayerLeft(object? sender, string name)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            _roomPlayers.Remove(name);
            // The server clears a leaver's panic too; this just doesn't wait for it.
            if (_roomPanic == name)
            {
                _roomPanic = null;
                _panicPlayers.Clear();
                UpdatePanicState();
            }
            _remotePlayers.Remove(name);
            RemoveRemotePlayerView(name);
            AddChatSystemLine($"{name} se odpojil(a) od mapy.");
            ShowInGame($"[Mapa] {name} se odpojil(a) od mapy.");
        }));
    }

    private void ApplyRemotePlayerColor(string name, string color)
    {
        if (!_remotePlayerViews.TryGetValue(name, out var views)) return;
        Brush brush = PlayerColors.ToBrush(color);
        if (ReferenceEquals(views.Dot.Fill, brush)) return; // brushes are cached per color - cheap no-op check
        views.Dot.Fill = brush;
        views.Arrow.Fill = brush;
        views.Label.Foreground = brush;
    }

    /// <summary>Retargets (or starts) a remote player's glide toward a freshly reported position,
    /// continuing smoothly from wherever its marker currently is if one was already in flight -
    /// same "retarget mid-flight" behavior as BeginPanAnimation. Snaps (no glide) the very first
    /// time a player is sighted, since there's nowhere sensible to glide FROM yet.</summary>
    private void BeginRemoteAnimation(string name, int targetX, int targetY)
    {
        long now = Environment.TickCount64;
        if (_remoteAnims.TryGetValue(name, out var anim))
        {
            var (curX, curY) = InterpolateRemote(anim, now);
            anim.FromX = curX;
            anim.FromY = curY;
        }
        else
        {
            anim = new RemoteAnim { FromX = targetX, FromY = targetY };
            _remoteAnims[name] = anim;
        }
        anim.ToX = targetX;
        anim.ToY = targetY;
        anim.StartTicks = now;

        if (!_remoteAnimRunning)
        {
            _remoteAnimRunning = true;
            CompositionTarget.Rendering += OnRemoteAnimationTick;
        }
    }

    private static (double x, double y) InterpolateRemote(RemoteAnim anim, long nowTicks)
    {
        double t = (nowTicks - anim.StartTicks) / RemoteAnimationDurationMs;
        if (t >= 1.0) return (anim.ToX, anim.ToY);
        double eased = 1 - (1 - t) * (1 - t); // same ease-out quad as OnAnimationTick
        return (anim.FromX + (anim.ToX - anim.FromX) * eased, anim.FromY + (anim.ToY - anim.FromY) * eased);
    }

    private void OnRemoteAnimationTick(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        bool anyRunning = false;
        foreach (var anim in _remoteAnims.Values)
        {
            if (now - anim.StartTicks < RemoteAnimationDurationMs) anyRunning = true;
        }

        // Cheap: just repositions existing WPF elements via WorldToScreen, no bitmap/cache work -
        // safe to call every frame independent of the (much heavier) main Redraw() pipeline.
        UpdateRemotePlayerOverlay();

        if (!anyRunning)
        {
            CompositionTarget.Rendering -= OnRemoteAnimationTick;
            _remoteAnimRunning = false;
        }
    }

    private void EnsureRemotePlayerView(string name)
    {
        if (_remotePlayerViews.ContainsKey(name)) return;

        // A small square (user's request - reads better than a circle over the tile grid), the same
        // size as your own marker (2026-09-29): UpdateRemotePlayerOverlay sizes and places it in
        // physical pixels, like DrawMarker.
        var dot = new Rectangle { Fill = Brushes.Cyan };
        // Points a triangle right (+X) by default, tip at (16,0); rotated per-frame around its own
        // center (RenderTransformOrigin 0.5,0.5) to point toward the player's true off-screen
        // position - see UpdateRemotePlayerOverlay's angle math.
        var arrowRotation = new RotateTransform();
        var arrow = new Polygon
        {
            Points = new PointCollection { new Point(0, -7), new Point(16, 0), new Point(0, 7) },
            Fill = Brushes.Cyan,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = arrowRotation,
            Visibility = Visibility.Collapsed,
        };
        var label = new TextBlock
        {
            Text = name,
            Foreground = Brushes.Cyan,
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Effect = new DropShadowEffect { Color = Colors.Black, Direction = 315, ShadowDepth = 1, BlurRadius = 2, Opacity = 0.9 },
        };
        RemotePlayersCanvas.Children.Add(dot);
        RemotePlayersCanvas.Children.Add(arrow);
        RemotePlayersCanvas.Children.Add(label);
        _remotePlayerViews[name] = (dot, arrow, arrowRotation, label);
    }

    private void RemoveRemotePlayerView(string name)
    {
        if (!_remotePlayerViews.TryGetValue(name, out var views)) return;
        RemotePlayersCanvas.Children.Remove(views.Dot);
        RemotePlayersCanvas.Children.Remove(views.Arrow);
        RemotePlayersCanvas.Children.Remove(views.Label);
        _remotePlayerViews.Remove(name);
        _remoteAnims.Remove(name);
    }

    /// <summary>Repositions each remote player's marker over the map (or hides it if that player
    /// is on a different facet than the one currently displayed), using its currently-interpolated
    /// glide position (see BeginRemoteAnimation) rather than snapping straight to the latest
    /// report. Called both at the end of Redraw() and, independently, on every remote-animation
    /// tick - computes its own DPI rather than taking it as a parameter so it works either way.
    /// WorldToScreen's output is in PHYSICAL pixels (the same space the render cache/bitmap use),
    /// but Canvas.Left/Top are WPF's LOGICAL units, hence the division by dpi.DpiScaleX/Y.
    ///
    /// A player whose marker would land within the viewport (inset by RemoteEdgeMargin) shows as
    /// the usual dot; one who's panned/zoomed out of view instead shows as an arrow, clamped to
    /// the inset viewport edge along the line from its center toward the player's true (possibly
    /// far off-screen) position, and rotated to point in that same direction - the classic
    /// "edge indicator" technique from top-down games/maps.</summary>
    private void UpdateRemotePlayerOverlay()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double viewportW = MapBorder.ActualWidth;
        double viewportH = MapBorder.ActualHeight;
        double centerXLogical = viewportW / 2.0;
        double centerYLogical = viewportH / 2.0;
        double halfW = Math.Max(1.0, viewportW / 2.0 - RemoteEdgeMargin);
        double halfH = Math.Max(1.0, viewportH / 2.0 - RemoteEdgeMargin);
        long now = Environment.TickCount64;

        foreach (var kv in _remotePlayerViews)
        {
            string name = kv.Key;
            var (dot, arrow, arrowRotation, label) = kv.Value;

            if (!_remotePlayers.TryGetValue(name, out var update) || update.Map != _currentFacetIndex)
            {
                dot.Visibility = Visibility.Collapsed;
                arrow.Visibility = Visibility.Collapsed;
                label.Visibility = Visibility.Collapsed;
                PlacePanicLine(name, null);
                continue;
            }

            double wx = update.X, wy = update.Y;
            if (_remoteAnims.TryGetValue(name, out var anim))
                (wx, wy) = InterpolateRemote(anim, now);

            var (sx, sy) = WorldToScreen(wx, wy);
            double x = centerXLogical + sx / dpi.DpiScaleX;
            double y = centerYLogical + sy / dpi.DpiScaleY;
            double dx = x - centerXLogical, dy = y - centerYLogical;
            PlacePanicLine(name, new Point(x, y));

            label.Visibility = Visibility.Visible;

            if (Math.Abs(dx) <= halfW && Math.Abs(dy) <= halfH)
            {
                dot.Visibility = Visibility.Visible;
                arrow.Visibility = Visibility.Collapsed;
                // Exactly your own marker's 5x5 physical pixels, on the physical pixel grid (the
                // canvas is in logical units, 125% scaling would otherwise blur/enlarge it).
                dot.Width = PlayerMarkerSizePx / dpi.DpiScaleX;
                dot.Height = PlayerMarkerSizePx / dpi.DpiScaleY;
                Canvas.SetLeft(dot, (Math.Round(x * dpi.DpiScaleX) - PlayerMarkerSizePx / 2) / dpi.DpiScaleX);
                Canvas.SetTop(dot, (Math.Round(y * dpi.DpiScaleY) - PlayerMarkerSizePx / 2) / dpi.DpiScaleY);
                PlaceRemoteLabel(label, x, y, 6, preferLeft: false, viewportW, viewportH);
            }
            else
            {
                dot.Visibility = Visibility.Collapsed;
                arrow.Visibility = Visibility.Visible;

                var (ex, ey) = PlaceEdgeArrow(arrow, arrowRotation, centerXLogical, centerYLogical, dx, dy, halfW, halfH);
                // On the arrow's inner side, so the name doesn't run off the map (user's report
                // 2026-09-29: a player to the right had their name cut off by the edge).
                PlaceRemoteLabel(label, ex, ey, 10, preferLeft: ex > centerXLogical, viewportW, viewportH);
            }
        }
    }

    /// <summary>Puts a remote player's name <paramref name="gap"/> px beside (x, y) - right of it,
    /// or left with <paramref name="preferLeft"/> - switching sides if it wouldn't fit, and keeps it
    /// inside the map vertically.</summary>
    private static void PlaceRemoteLabel(TextBlock label, double x, double y, double gap, bool preferLeft, double viewportW, double viewportH)
    {
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = label.DesiredSize.Width, h = label.DesiredSize.Height;
        const double pad = 2;
        double right = x + gap, left = x - gap - w;
        bool fitsRight = right + w <= viewportW - pad, fitsLeft = left >= pad;
        double lx = preferLeft ? (fitsLeft || !fitsRight ? left : right) : (fitsRight || !fitsLeft ? right : left);
        Canvas.SetLeft(label, Math.Clamp(lx, pad, Math.Max(pad, viewportW - pad - w)));
        Canvas.SetTop(label, Math.Clamp(y - 8, pad, Math.Max(pad, viewportH - pad - h)));
    }

    /// <summary>Puts an edge arrow (0,-7 16,0 0,7 triangle) where the ray from the viewport center
    /// through (dx,dy) hits the rectangle of half-extents halfW/halfH, pointing along the ray.
    /// Standard center-to-boundary clamp: scale by whichever axis needs the smaller factor to
    /// reach its half-extent. Returns the point on the edge (logical units).</summary>
    private static (double X, double Y) PlaceEdgeArrow(Polygon arrow, RotateTransform rotation, double centerX, double centerY,
        double dx, double dy, double halfW, double halfH)
    {
        double tx = dx != 0 ? halfW / Math.Abs(dx) : double.PositiveInfinity;
        double ty = dy != 0 ? halfH / Math.Abs(dy) : double.PositiveInfinity;
        double t = Math.Min(tx, ty);
        double ex = centerX + dx * t;
        double ey = centerY + dy * t;

        rotation.Angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        Canvas.SetLeft(arrow, ex - 8);
        Canvas.SetTop(arrow, ey - 7);
        return (ex, ey);
    }

    // ---- "Go to location..." (map context menu, user's request 2026-09-29) ----

    private const double GoToCrosshairSeconds = 4.0;
    private (int X, int Y, int Map)? _goToPoint;
    private DispatcherTimer? _goToCrosshairTimer;
    // With Track Player on, the view stays on the target (ViewFollowsPlayer is false) until your
    // character takes a step; then it flies back and follows again (user's request 2026-09-29).
    private bool _goToHold;
    private bool _goToReturning;

    /// <summary>Asks for X/Y (prefilled with the clicked tile) and flies the map there. Track
    /// Player stays as it is: when it's on, the view waits on the target until your character
    /// moves (ReturnFromGoTo). A crosshair marks the tile for a few seconds after landing.</summary>
    private void GoToLocation()
    {
        if (_currentFacet is not { } facet) return;
        var (x, y) = _contextMenuWorld ?? ((int)Math.Round(_viewX), (int)Math.Round(_viewY));
        var dialog = new GoToLocationWindow(Math.Clamp(x, 0, facet.WidthTiles - 1), Math.Clamp(y, 0, facet.HeightTiles - 1),
            facet.Name, facet.WidthTiles, facet.HeightTiles) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not var (tx, ty)) return;

        if (_peek is not null) CancelPanicPeek();
        _goToHold = _trackPlayer;
        _goToReturning = false;
        _goToPoint = (tx, ty, _currentFacetIndex);
        // Started now, not when the flight lands: a step during the flight replaces it with the
        // flight back, and its "done" would never run.
        _goToCrosshairTimer?.Stop();
        _goToCrosshairTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FlightMs) + TimeSpan.FromSeconds(GoToCrosshairSeconds) };
        _goToCrosshairTimer.Tick += (_, _) =>
        {
            _goToCrosshairTimer?.Stop();
            _goToPoint = null;
            UpdateGoToCrosshair();
        };
        _goToCrosshairTimer.Start();
        FlyTo(tx, ty, () => RequestRedraw());
        Log(_goToHold ? $"Mapa na {tx},{ty} - jakmile se pohneš, vrátí se na postavu." : $"Mapa na {tx},{ty}.");
    }

    /// <summary>Your character moved while the view was held on a "Go to" target: fly back, then
    /// follow again.</summary>
    private void ReturnFromGoTo()
    {
        if (_markerX is not int mx || _markerY is not int my)
        {
            EndGoToHold();
            return;
        }
        _goToReturning = true;
        FlyTo(mx, my, () =>
        {
            EndGoToHold();
            // Catch up with any step taken during the flight.
            if (ViewFollowsPlayer && _markerX is int x && _markerY is int y) BeginPanAnimation(x, y);
            RequestRedraw();
        });
    }

    /// <summary>Click on your own edge arrow: the map flies back to your character (user's request
    /// 2026-09-29). With Track Player on (the arrow shows then only during a "Go to" hold or a
    /// panic peek) it follows the character again afterwards; with it off it just flies there.
    /// Handled on button down so the map's own click/drag/double-click doesn't run too.</summary>
    private void SelfArrow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_markerX is not int x || _markerY is not int y) return;
        if (_trackPlayer)
        {
            if (_peek is not null) CancelPanicPeek();
            _goToHold = true; // ReturnFromGoTo's fly-back-then-follow
            ReturnFromGoTo();
        }
        else
        {
            FlyTo(x, y, RequestRedraw);
        }
    }

    private void EndGoToHold()
    {
        _goToHold = false;
        _goToReturning = false;
    }

    /// <summary>Part of Redraw: keeps the crosshair on its tile while the map moves.</summary>
    private void UpdateGoToCrosshair()
    {
        if (_goToPoint is not var (x, y, map) || map != _currentFacetIndex)
        {
            GoToCrosshair.Visibility = Visibility.Collapsed;
            return;
        }
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        var (sx, sy) = WorldToScreen(x, y);
        Canvas.SetLeft(GoToCrosshair, MapBorder.ActualWidth / 2.0 + sx / dpi.DpiScaleX);
        Canvas.SetTop(GoToCrosshair, MapBorder.ActualHeight / 2.0 + sy / dpi.DpiScaleY);
        GoToCrosshair.Visibility = Visibility.Visible;
    }

    /// <summary>With Track Player off (or during a panic peek) your character can be far off
    /// screen; then an arrow at the map's edge points toward them, like the other players' arrows
    /// (user's request 2026-09-29). Shown only while your square isn't in view at all.</summary>
    private void UpdateSelfArrow()
    {
        if (ViewFollowsPlayer || _markerX is not int mx || _markerY is not int my)
        {
            SelfArrow.Visibility = Visibility.Collapsed;
            return;
        }
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double viewportW = MapBorder.ActualWidth, viewportH = MapBorder.ActualHeight;
        var (sx, sy) = WorldToScreen(mx, my);
        double dx = sx / dpi.DpiScaleX, dy = sy / dpi.DpiScaleY;
        if (Math.Abs(dx) <= viewportW / 2.0 && Math.Abs(dy) <= viewportH / 2.0)
        {
            SelfArrow.Visibility = Visibility.Collapsed;
            return;
        }
        SelfArrow.Visibility = Visibility.Visible;
        PlaceEdgeArrow(SelfArrow, SelfArrowRotation, viewportW / 2.0, viewportH / 2.0, dx, dy,
            Math.Max(1.0, viewportW / 2.0 - RemoteEdgeMargin), Math.Max(1.0, viewportH / 2.0 - RemoteEdgeMargin));
    }

    private void BrowseMarkersButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Vyber složku s marker soubory (.map/.csv)" };
        if (dialog.ShowDialog() == true)
        {
            MarkersDirTextBox.Text = dialog.FolderName;
        }
    }

    private async void LoadMarkersButton_Click(object sender, RoutedEventArgs e) => await LoadMarkersAsync();

    /// <summary>The markers folder: the one typed in Mapa > Nastavení, or - left empty, the
    /// default - the map files folder (user's request 2026-09-28: marker files usually sit next
    /// to the client's own files).</summary>
    private string EffectiveMarkersDirectory =>
        MarkersDirTextBox.Text.Trim() is { Length: > 0 } dir ? dir : ClientDirTextBox.Text.Trim();

    private void MapFolderTextBoxes_TextChanged(object sender, TextChangedEventArgs e) => UpdateMarkersDirPlaceholder();

    /// <summary>Shows, inside the empty markers box, which folder an empty value stands for.</summary>
    private void UpdateMarkersDirPlaceholder()
    {
        string mapDir = ClientDirTextBox.Text.Trim();
        MarkersDirPlaceholder.Text = mapDir.Length > 0 ? $"(složka mapy: {mapDir})" : "(složka mapy)";
        MarkersDirPlaceholder.Visibility = MarkersDirTextBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Reads every .map/.csv file directly inside the chosen folder (not recursively -
    /// matches how both old UOAM and OrionUO's own WorldMapExternalMarkers folder are laid out,
    /// several sibling marker files side by side) and merges all their entries into one overlay.
    /// A single marker file loader wouldn't fit this app's actual data: this project's own client
    /// has multiple relevant files at once (shard-specific "DP Mesta.map"/"DP Dungy.map" plus
    /// Orion's stock Atlas/Common/Dungeons/etc.).</summary>
    private async Task<bool> LoadMarkersAsync()
    {
        string dir = EffectiveMarkersDirectory;
        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
        {
            Log(MarkersDirTextBox.Text.Trim().Length > 0
                ? "Neplatná složka s markery."
                : "Složka s markery není nastavená a složka mapy neexistuje.");
            return false;
        }

        return await Task.Run(() =>
        {
            var loaded = new List<LoadedMarker>();
            int fileCount = 0, errorCount = 0;
            foreach (string file in System.IO.Directory.EnumerateFiles(dir))
            {
                string ext = System.IO.Path.GetExtension(file);
                if (!ext.Equals(".map", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    loaded.AddRange(MarkerFileReader.Read(file).Select(m => new LoadedMarker(m, file)));
                    fileCount++;
                }
                catch (Exception ex)
                {
                    errorCount++;
                    Dispatcher.BeginInvoke(() => Log($"Chyba při čtení {System.IO.Path.GetFileName(file)}: {ex.Message}"));
                }
            }

            Dispatcher.Invoke(() =>
            {
                _markers.Clear();
                _markers.AddRange(loaded);
                BuildMarkerViews();
                Log($"Načteno {_markers.Count} markerů z {fileCount} souborů" + (errorCount > 0 ? $" ({errorCount} se nepodařilo přečíst)." : "."));
                SaveSettings();
                RequestRedraw();
            });
            return fileCount > 0;
        });
    }

    private void ClearMarkerViews()
    {
        foreach (var view in _markerViews) MarkersCanvas.Children.Remove(view.Icon);
        _markerViews.Clear();
        HideMarkerHoverLabel(); // any previously-hovered icon reference is about to go stale
    }

    private void BuildMarkerViews()
    {
        ClearMarkerViews();
        foreach (var marker in _markers)
        {
            var bitmap = GetMarkerIcon(marker.Entry.IconName);
            var image = new Image
            {
                Source = bitmap,
                Width = bitmap is { PixelWidth: > 0 } ? bitmap.PixelWidth : 16,
                Height = bitmap is { PixelHeight: > 0 } ? bitmap.PixelHeight : 16,
                Visibility = Visibility.Collapsed,
            };
            var capturedMarker = marker; // local copy for the closures below (loop variable)
            image.MouseEnter += (_, _) => ShowMarkerHoverLabel(capturedMarker.Entry.Name, image);
            image.MouseLeave += (_, _) => HideMarkerHoverLabel();
            image.MouseRightButtonUp += (_, e) =>
            {
                // Handled here so the map's own right-click menu (MapBorder_MouseRightButtonUp)
                // doesn't open on top of the marker's.
                e.Handled = true;
                ShowMarkerContextMenu(capturedMarker, image);
            };
            MarkersCanvas.Children.Add(image);
            _markerViews.Add((marker, image));
        }
        RebuildMarkerCategories(); // the side panel's list
    }

    // Which marker (if any) the mouse is currently over - kept so the label can be re-positioned
    // alongside its icon in UpdateMarkersOverlay if the camera moves while still hovering, and so
    // it gets hidden automatically if that marker becomes hidden (facet change, toggle) mid-hover.
    // (A marker's icon, or a shared marker's frame.)
    private FrameworkElement? _hoveredMarkerIcon;

    private void ShowMarkerHoverLabel(string text, FrameworkElement icon)
    {
        _hoveredMarkerIcon = icon;
        MarkerHoverLabel.Text = text;
        PositionMarkerHoverLabel(icon);
        MarkerHoverLabel.Visibility = Visibility.Visible;
    }

    private void HideMarkerHoverLabel()
    {
        _hoveredMarkerIcon = null;
        MarkerHoverLabel.Visibility = Visibility.Collapsed;
    }

    private void PositionMarkerHoverLabel(FrameworkElement icon)
    {
        double width = double.IsNaN(icon.Width) ? icon.ActualWidth : icon.Width;
        Canvas.SetLeft(MarkerHoverLabel, Canvas.GetLeft(icon) + width + 4);
        Canvas.SetTop(MarkerHoverLabel, Canvas.GetTop(icon) - 2);
    }

    private BitmapImage? GetMarkerIcon(string? iconName)
    {
        string key = NormalizeIconName(iconName ?? "");
        if (_markerIconCache.TryGetValue(key, out var cached)) return cached;

        var bmp = LoadIconFile(key) ?? (key != FallbackMarkerIconName ? LoadIconFile(FallbackMarkerIconName) : null);
        _markerIconCache[key] = bmp;
        return bmp;
    }

    private static BitmapImage? LoadIconFile(string key)
    {
        string path = System.IO.Path.Combine(MarkerIconsDirectory, key + ".png");
        if (!System.IO.File.Exists(path)) return null;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad; // decode immediately so PixelWidth/Height are available right away
        bmp.EndInit();
        bmp.Freeze(); // immutable once loaded - safe to share one instance across every marker using this icon
        return bmp;
    }

    // Marker files spell icon names inconsistently ("arms", "armourers guild", "BANK") while the
    // bundled icon files are all upper-case with no spaces ("ARMOURERSGUILD.png") - this is the
    // one normalization that maps every real-world spelling seen in this project's own DP/Orion
    // marker files onto the matching file name.
    private static string NormalizeIconName(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        int n = 0;
        foreach (char c in s)
            if (!char.IsWhiteSpace(c)) buf[n++] = char.ToUpperInvariant(c);
        return new string(buf[..n]);
    }

    private void ShowMarkersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateMarkerCount();
        RequestRedraw();
        SaveSettings();
    }

    /// <summary>Repositions every loaded marker's icon over the map (or hides it if markers are
    /// toggled off, its category is hidden in the side panel, or it's on a different facet than the
    /// one currently displayed). Markers never move in world space, so unlike remote players there
    /// is no animation to drive - this only needs to run once per Redraw() (the camera panning is
    /// what moves them on screen). See UpdateRemotePlayerOverlay for the same PHYSICAL-vs-LOGICAL
    /// pixel/DPI conversion this mirrors.</summary>
    private void UpdateMarkersOverlay()
    {
        if (_markerRowsFacet != _currentFacetIndex) RebuildMarkerRows(); // the list shows the facet on screen
        if (_markerViews.Count == 0 && _sharedMarkViews.Count == 0) return;

        bool show = ShowMarkersCheckBox.IsChecked == true;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double centerXLogical = MapBorder.ActualWidth / 2.0;
        double centerYLogical = MapBorder.ActualHeight / 2.0;
        UpdateSharedMarkViews(show, dpi, centerXLogical, centerYLogical);
        if (!show)
        {
            foreach (var view in _markerViews) view.Icon.Visibility = Visibility.Collapsed;
            HideMarkerHoverLabel();
            return;
        }

        foreach (var (loaded, icon) in _markerViews)
        {
            var marker = loaded.Entry;
            if (marker.MapIndex != _currentFacetIndex || !IsMarkerVisible(loaded))
            {
                icon.Visibility = Visibility.Collapsed;
                continue;
            }

            var (sx, sy) = WorldToScreen(marker.X, marker.Y);
            icon.Visibility = Visibility.Visible;
            Canvas.SetLeft(icon, centerXLogical + sx / dpi.DpiScaleX - icon.Width / 2);
            Canvas.SetTop(icon, centerYLogical + sy / dpi.DpiScaleY - icon.Height / 2);
        }

        // Keep the hover label glued to its icon if the camera moved while still hovering (e.g.
        // an in-progress pan animation); hide it if that marker just became hidden instead.
        if (_hoveredMarkerIcon is { } hoveredIcon)
        {
            if (hoveredIcon.Visibility == Visibility.Visible) PositionMarkerHoverLabel(hoveredIcon);
            else HideMarkerHoverLabel();
        }
    }

    // Varianta B1 (packet proxy) a log panel - odloženo, viz poznámka u B1 v MainWindow.xaml a
    // v CLAUDE.md/README.md: herní server po redirectu vyžaduje starou "login encryption" vrstvu,
    // kterou zatím neimplementujeme. Backend (PacketProxyPositionProvider) zůstává funkční a
    // otestovaný - odkomentovat tohle + odpovídající XAML blok, až se k tomu vrátíme.
    /*
    private async void StartProxyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_proxyProvider is not null) return;

        string addr = ProxyRealServerTextBox.Text.Trim();
        int colon = addr.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(addr[(colon + 1)..], out int realPort) ||
            !int.TryParse(ProxyLocalPortTextBox.Text.Trim(), out int localPort))
        {
            Log("Neplatná adresa serveru nebo lokální port (očekávám IP:port).");
            return;
        }
        string realHost = addr[..colon];

        var provider = new PacketProxyPositionProvider(realHost, realPort, localPort);
        provider.PositionChanged += OnPositionChanged;
        provider.StatusMessage += (_, msg) => Dispatcher.Invoke(() => Log(msg));

        try
        {
            await provider.StartAsync();
            _proxyProvider = provider;
            StartProxyButton.IsEnabled = false;
            StopProxyButton.IsEnabled = true;
            Log($"Proxy běží. V Orion profilu dočasně nastav server na 127.0.0.1:{localPort} místo {realHost}:{realPort}.");
        }
        catch (Exception ex)
        {
            Log($"Nepodařilo se spustit proxy: {ex.Message}");
        }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) => LogTextBox.Clear();

    private async void StopProxyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_proxyProvider is null) return;
        await _proxyProvider.StopAsync();
        _proxyProvider.Dispose();
        _proxyProvider = null;
        StartProxyButton.IsEnabled = true;
        StopProxyButton.IsEnabled = false;
    }
    */

    /// <summary>Writes to the status bar. Used to be a richer scrollable log panel too, dropped
    /// along with B1 (it existed mainly to debug the proxy's packet-level traffic).</summary>
    private void Log(string message) => StatusText.Text = message;

    private void ToggleRotationButton_Click(object sender, RoutedEventArgs e)
    {
        _renderMode = _renderMode == MapRenderMode.Rotated45 ? MapRenderMode.NorthUp : MapRenderMode.Rotated45;
        ToggleRotationButton.Content = _renderMode == MapRenderMode.Rotated45 ? "Pohled: otočený 45°" : "Pohled: sever nahoře";
        UpdateCompassOverlay();
        Redraw();
        _trackWindow?.Redraw(); // it uses the main map's projection
    }

    private void MapBorder_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Step through the discrete ZoomLevels list (not a raw +/- delta) - see its comment for
        // why the values in it are restricted to what they are.
        _zoomIndex = Math.Clamp(_zoomIndex + (e.Delta > 0 ? 1 : -1), 0, ZoomLevels.Length - 1);
        _pixelsPerTile = ZoomLevels[_zoomIndex].Ppt;
        Redraw();
    }

    private void MapBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Gives the map keyboard focus regardless of click count/mode - this is what lets
        // MapBorder_PreviewTextInput below tell "the user is typing into the map" apart from
        // typing into any of the side panel's own textboxes (which keep focus, so this handler
        // never fires for them).
        MapBorder.Focus();

        if (e.ClickCount == 2)
        {
            SetMapOnlyMode(!_mapOnlyMode);
            return;
        }

        // Track Player off: left-drag pans the map (in map-only mode too, taking precedence over
        // moving the window - turn tracking back on to drag the window again).
        if (!_trackPlayer && e.ClickCount == 1 && _currentFacet is not null)
        {
            if (_peek is not null) CancelPanicPeek(); // the user takes the map over - no return
            _dragStart = e.GetPosition(MapBorder);
            _dragStartViewX = _viewX;
            _dragStartViewY = _viewY;
            MapBorder.CaptureMouse();
            e.Handled = true;
            return;
        }

        // Map-only mode has no title bar to drag by (WindowStyle.None) - a single click-drag on
        // the map itself moves the window instead. Only active in map-only mode: in the normal
        // window a plain click on the map shouldn't hijack anything (e.g. future marker-placement
        // features would want plain clicks free).
        if (_mapOnlyMode && e.ClickCount == 1 && e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* DragMove requires the button still down when called - ignore the rare race */ }
        }
    }

    // "Track Player" (right-click menu, like old UOAM): on = the view follows the player (the
    // default, not persisted - every launch starts tracking); off = position updates still move
    // the player's own marker, but the view stays where the user dragged it.
    private bool _trackPlayer = true;
    private Point? _dragStart;
    private double _dragStartViewX, _dragStartViewY;
    // World tile the context menu was opened on - what "New Label..." places the label at.
    private (int X, int Y)? _contextMenuWorld;

    private void MapBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start || !MapBorder.IsMouseCaptured || _currentFacet is null) return;

        // Mouse deltas are logical units; projection math works in physical pixels (see Redraw).
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Point now = e.GetPosition(MapBorder);
        double dxPx = (now.X - start.X) * dpi.DpiScaleX, dyPx = (now.Y - start.Y) * dpi.DpiScaleY;

        // Dragging moves the map WITH the cursor, so the view moves the opposite way. ScreenToWorld
        // is relative to the current view, so subtracting _viewX/_viewY leaves the pure delta.
        var (wx, wy) = ScreenToWorld(dxPx, dyPx);
        double worldDx = wx - _viewX, worldDy = wy - _viewY;
        _viewX = Math.Clamp(_dragStartViewX - worldDx, 0, _currentFacet.WidthTiles - 1);
        _viewY = Math.Clamp(_dragStartViewY - worldDy, 0, _currentFacet.HeightTiles - 1);
        // The render cache is always generated around the integer _centerX/_centerY (see
        // EnsureCache); keep it next to the view so a long drag regenerates it around where we are.
        _centerX = (int)Math.Round(_viewX);
        _centerY = (int)Math.Round(_viewY);
        RequestRedraw();
    }

    private void MapBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        MapBorder.ReleaseMouseCapture();
    }

    private void MapBorder_LostMouseCapture(object sender, MouseEventArgs e) => _dragStart = null;

    private void MapBorder_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _contextMenuWorld = null;
        if (_currentFacet is not null)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Point p = e.GetPosition(MapBorder);
            var (wx, wy) = ScreenToWorld((p.X - MapBorder.ActualWidth / 2) * dpi.DpiScaleX,
                                         (p.Y - MapBorder.ActualHeight / 2) * dpi.DpiScaleY);
            _contextMenuWorld = ((int)Math.Floor(wx + 0.5), (int)Math.Floor(wy + 0.5));
        }

        var trackItem = new MenuItem { Header = "Track Player", IsCheckable = true, IsChecked = _trackPlayer };
        trackItem.Click += (_, _) => SetTrackPlayer(!_trackPlayer);
        var labelItem = new MenuItem { Header = "New Label...", IsEnabled = _contextMenuWorld is not null };
        labelItem.Click += (_, _) => AddNewLabel();
        var goToItem = new MenuItem { Header = "Go to location...", IsEnabled = _currentFacet is not null };
        goToItem.Click += (_, _) => GoToLocation();

        // Same order as old UOAM's menu: Track Player, Drop or Pickup Marker(s), New Label...
        var menu = new ContextMenu { PlacementTarget = MapBorder, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(trackItem);
        foreach (var item in BuildDropMarkerMenuItems(_contextMenuWorld)) menu.Items.Add(item);
        menu.Items.Add(labelItem);
        menu.Items.Add(goToItem);
        if (_relayClient is { } client)
        {
            // Old UOAM's Panic! toggle; also "-panic"/"-unpanic" in the game.
            // One panic per room, anyone may turn it off (also Space over the map, "-panic").
            var panicItem = new MenuItem
            {
                Header = _roomPanic is { } p && !_myPanic ? $"Vypnout Panic! ({p})" : "Panic!",
                IsCheckable = true,
                IsChecked = _roomPanic is not null,
            };
            panicItem.Click += (_, _) => TogglePanic();
            menu.Items.Add(new Separator());
            menu.Items.Add(panicItem);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---- "Drop or Pickup Marker" (local) and "Drop or Pick Up Shared Marker" (relay room). A
    // marker is the point you want to run to: a dot, a line from your own position and the
    // distance in tiles bottom-right (UpdateDropMarkerOverlay). The item carries a checkmark while
    // a marker is down; choosing it then picks the marker up, wherever you clicked. ----

    private (int X, int Y, int Map)? _dropMarker;
    // The relay room's shared marker exactly as the server last reported it (null = none).
    private RelayProtocol.SharedMarker? _sharedMarker;

    /// <param name="at">World tile to drop at: the clicked map tile, or a label's own position
    /// when opened on a label. Null (no map loaded) disables dropping; picking up still works.</param>
    private IEnumerable<MenuItem> BuildDropMarkerMenuItems((int X, int Y)? at)
    {
        var local = new MenuItem { Header = "Drop or Pickup Marker", IsCheckable = true, IsChecked = _dropMarker is not null,
                                   IsEnabled = _dropMarker is not null || at is not null };
        local.Click += (_, _) =>
        {
            if (_dropMarker is not null) _dropMarker = null;
            else if (at is (int x, int y)) _dropMarker = (x, y, _currentFacetIndex);
            RequestRedraw();
        };
        yield return local;

        // Only while connected to the online map, like old UOAM's shared-marker item.
        if (_relayClient is { } client)
        {
            var shared = new MenuItem { Header = "Drop or Pick Up Shared Marker", IsCheckable = true, IsChecked = _sharedMarker is not null,
                                        IsEnabled = _sharedMarker is not null || at is not null };
            shared.Click += (_, _) =>
            {
                // Nothing changes locally until the server's state comes back (OnSharedMarkerChanged).
                if (_sharedMarker is not null) client.PickupSharedMarker();
                else if (at is (int x, int y)) client.DropSharedMarker(x, y, _currentFacetIndex);
            };
            yield return shared;
        }
    }

    private void OnSharedMarkerChanged(object? sender, (RelayProtocol.SharedMarker? Marker, string Actor, bool Notify) e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (sender != _relayClient) return; // a late datagram from a client we already disconnected
            _sharedMarker = e.Marker;
            RequestRedraw();
            // Repeats for as long as a marker is down, also for one we only learned about from a
            // resync (late joiner). A new drop restarts the interval, so no repeat right after it.
            if (e.Marker is null) _sharedMarkerRepeatTimer.Stop();
            else if (e.Notify || !_sharedMarkerRepeatTimer.IsEnabled)
            {
                _sharedMarkerRepeatTimer.Stop();
                _sharedMarkerRepeatTimer.Start();
            }
            if (!e.Notify) return;

            if (e.Marker is { } m)
            {
                Log($"{e.Actor} položil(a) sdílený marker na {m.X},{m.Y}.");
                AddChatSystemLine($"{e.Actor} položil(a) sdílený marker.");
                ShowSharedMarkerInGame(m);
            }
            else
            {
                Log($"{e.Actor} zrušil(a) sdílený marker.");
                AddChatSystemLine($"{e.Actor} zrušil(a) sdílený marker.");
            }
        }));
    }

    // Old UOAM repeated the overhead "Shared Marker" text while the marker was down.
    private const int SharedMarkerRepeatSeconds = 10;
    private DispatcherTimer? _sharedMarkerRepeatTimerField;
    private DispatcherTimer _sharedMarkerRepeatTimer => _sharedMarkerRepeatTimerField ??= CreateSharedMarkerRepeatTimer();

    private DispatcherTimer CreateSharedMarkerRepeatTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SharedMarkerRepeatSeconds) };
        timer.Tick += (_, _) =>
        {
            if (_sharedMarker is { } m && _relayClient is not null) ShowSharedMarkerInGame(m);
            else timer.Stop();
        };
        return timer;
    }

    /// <summary>Old UOAM's overhead text: "Shared Marker /^ North", direction from the current
    /// position. Just "Shared Marker" while our position or facet doesn't match.</summary>
    private void ShowSharedMarkerInGame(RelayProtocol.SharedMarker m)
    {
        // Paused while the room has a panic (user's request) - the panic text is what matters
        // then. The repeat timer keeps running, so it's back within 10s of the panic ending.
        if (_roomPanic is not null) return;
        if (_lastLocalUpdate is { } me && me.Map == m.Map)
        {
            string dir = DirectionName(m.X - me.X, m.Y - me.Y);
            string arrow = DirectionArrow(dir);
            ShowInGame(arrow.Length == 0 ? $"Shared Marker {dir}" : $"Shared Marker {arrow} {dir}", overhead: true);
        }
        else ShowInGame("Shared Marker", overhead: true);
    }

    /// <summary>ASCII arrow pointing where <paramref name="direction"/> lies on the UO client's
    /// screen, which shows the world rotated 45° (north is up-right). Plain ASCII only: Orion
    /// Assistant converts the text to ANSI and the overhead font blanks everything above 127, so
    /// Unicode arrows (↗) came out as '?' (tested live 2026-09-25).</summary>
    internal static string DirectionArrow(string direction) => direction switch
    {
        "North" => "/^", "NorthEast" => "->", "East" => "\\v", "SouthEast" => "V",
        "South" => "v/", "SouthWest" => "<-", "West" => "^\\", "NorthWest" => "^",
        _ => "",
    };

    /// <summary>Compass direction of a world-space offset in UO terms (north = -Y, east = +X), as
    /// old UOAM names it ("NorthEast").</summary>
    internal static string DirectionName(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return "Here";
        double angle = Math.Atan2(-dy, dx) * 180.0 / Math.PI; // 0 = east, 90 = north
        int sector = (int)Math.Round(angle / 45.0);
        return (((sector % 8) + 8) % 8) switch
        {
            0 => "East", 1 => "NorthEast", 2 => "North", 3 => "NorthWest",
            4 => "West", 5 => "SouthWest", 6 => "South", _ => "SouthEast",
        };
    }

    // ---- Old UOAM's in-game commands, typed in the UO client ("-c text", "-panic",
    // "-unpanic"). They arrive through NewUOAM.UoaBridge (see UoAssistCommands for why a separate
    // process) and are answered in that same client. "--text" (UOAM's chat prefix) can't work:
    // Orion Assistant swallows lines starting with "--" without delivering them (tested live), so
    // map chat is "-c". Beware: a mistyped command ("-panik") isn't registered, so OA lets it
    // through and the character says it aloud. ----

    private readonly UoAssistCommands _uoaCommands = new();
    private readonly DispatcherTimer _commandBridgeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    // Everyone else in the room (joined, even without a position yet) - for "-c name>text".
    private readonly HashSet<string> _roomPlayers = new();

    /// <summary>Makes sure the client the in-game text goes to (the one B2 tracks, or the only
    /// one running) has a command bridge. Cheap when it already has one (a mutex check).</summary>
    private void EnsureCommandBridge()
    {
        int? pid = (_b2Provider as ProcessMemoryPositionProvider)?.ProcessId;
        if (pid is null)
        {
            var all = UoAssistTextSender.FindAllProcessIds();
            if (all.Count == 1) pid = all[0];
        }
        if (pid is { } p) _uoaCommands.EnsureBridge(p);
    }

    private void OnGameCommand(int pid, string command, string args)
    {
        switch (command)
        {
            case "c": HandleChatCommand(pid, args); break;
            case "panic": HandlePanicCommand(pid, on: true); break;
            case "unpanic": HandlePanicCommand(pid, on: false); break;
            case "who": HandleWhoCommand(pid); break;
            case "t": HandleTrackCommand(pid, args); break;
        }
    }

    /// <summary>"-who" (old UOAM): who's in the room, with distance and direction from you, one
    /// line per player (user's request 2026-09-29).</summary>
    private void HandleWhoCommand(int pid)
    {
        if (_relayClient is null) { ReplyInGame(pid, "Nejsi připojen(a) k online mapě."); return; }
        var others = DescribeRoomPlayers().Where(p => !p.IsMe).ToList();
        if (others.Count == 0) { ReplyInGame(pid, "V místnosti kromě tebe nikdo není."); return; }
        ReplyInGame(pid, $"V místnosti ({others.Count}):");
        foreach (var p in others) ReplyInGame(pid, p.Detail.Length > 0 ? $"{p.Name} - {p.Detail}" : p.Name);
    }

    /// <summary>Everyone in the room (you first, then others by name) with where they are relative
    /// to you: "120 tiles NorthEast", "tady", "jiná mapa" or "bez pozice". Used by "-who" and the
    /// chat's player list.</summary>
    private List<ChatPlayerInfo> DescribeRoomPlayers()
    {
        var list = new List<ChatPlayerInfo>();
        if (_relayClient is null) return list;
        if (_relayClient.PlayerName is { } myName)
            list.Add(new ChatPlayerInfo(myName, PlayerColors.ToBrush(_playerColor), "ty", IsMe: true));

        var me = _lastLocalUpdate;
        foreach (string name in _roomPlayers.Where(n => n != _relayClient.PlayerName).Order(StringComparer.CurrentCultureIgnoreCase))
        {
            string detail;
            if (!_remotePlayers.TryGetValue(name, out var them)) detail = "bez pozice";
            else if (me is null) detail = $"{them.X},{them.Y}";
            else if (them.Map != me.Map) detail = "jiná mapa";
            else
            {
                int dx = them.X - me.X, dy = them.Y - me.Y;
                int distance = Math.Max(Math.Abs(dx), Math.Abs(dy)); // UO range, like the drop marker
                detail = distance == 0 ? "tady" : $"{distance} tiles {DirectionName(dx, dy)}";
            }
            list.Add(new ChatPlayerInfo(name, OwnerBrush(name), detail, IsMe: false));
        }
        return list;
    }

    private void ReplyInGame(int pid, string text) => ShowInGame($"[Mapa] {text}", targetPid: pid, force: true);

    /// <summary>"-c text" to the room, "-c name>text" privately to one player (old UOAM's
    /// "--name>text"; the first letters of the name are enough if they match only one player).
    /// A "&gt;" whose left part matches nobody is an error rather than a public message, so a
    /// mistyped private message never goes to the whole room.</summary>
    private void HandleChatCommand(int pid, string args)
    {
        if (_relayClient is null) { ReplyInGame(pid, "Nejsi připojen(a) k online mapě."); return; }
        if (args.Length == 0) { ReplyInGame(pid, "Použití: -c zpráva, nebo -c jméno>zpráva pro soukromou zprávu."); return; }

        int gt = args.IndexOf('>');
        string prefix = gt > 0 ? args[..gt].Trim() : "";
        if (prefix.Length == 0)
        {
            _relayClient.SendChatMessage(args);
            return;
        }

        string body = args[(gt + 1)..].Trim();
        var matches = MatchRoomPlayers(prefix);
        if (matches.Count == 1 && body.Length > 0) _relayClient.SendChatMessage(body, matches[0]);
        else if (matches.Count == 1) ReplyInGame(pid, $"Chybí text zprávy: -c {matches[0]}>zpráva");
        else if (matches.Count > 1) ReplyInGame(pid, $"Jménu \"{prefix}\" odpovídá víc hráčů: {string.Join(", ", matches)}. Napiš víc písmen.");
        else ReplyInGame(pid, $"Hráč \"{prefix}\" není na mapě. (Zpráva se znakem > se posílá jako soukromá.)");
    }

    /// <summary>Exact name (ignoring case) wins; otherwise every room player whose name starts
    /// with the prefix.</summary>
    private List<string> MatchRoomPlayers(string prefix)
    {
        var exact = _roomPlayers.Where(n => string.Equals(n, prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        return _roomPlayers.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                           .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"-panic" makes you the room's panic (replacing anyone else's), "-unpanic" turns
    /// the room's panic off, whoever's it is.</summary>
    private void HandlePanicCommand(int pid, bool on)
    {
        if (_relayClient is null) { ReplyInGame(pid, "Nejsi připojen(a) k online mapě."); return; }
        // The answer ("Panic zapnut/vypnut") comes with the server's state (OnPanicChanged).
        if (!_relayClient.SetPanic(on)) ReplyInGame(pid, "Panic nejde přepnout - mapa zatím nezná tvoje jméno.");
    }

    // ---- Panic! mode (old UOAM). One panic per room at a time, and anyone in the room can turn
    // it off (user's call - people forget to). While another player has it on, the map border
    // flashes, a dotted line runs from you to them, their marker blinks, a system sound repeats
    // (PanicSoundCheckBox) and the game shows who needs help and where over your head, every
    // 10s. Your own panic shows "PANIC!" on your map. Toggled by Space over the map, the map menu,
    // or "-panic"/"-unpanic" in the game. The state comes only from the server
    // (RelayMultiplayerClient.PanicChanged). ----

    // Who in the room is in panic (null = nobody); _myPanic / _panicPlayers are derived from it.
    private string? _roomPanic;
    private bool _myPanic;
    private readonly HashSet<string> _panicPlayers = new();
    private readonly Dictionary<string, Line> _panicLines = new();
    private DispatcherTimer? _panicBlinkTimer;
    private bool _panicBlinkOn;
    private long _lastPanicSoundTicks;
    private const int PanicBlinkMs = 400;
    // Red over the head for "PANIC! ...": 0x0021, the user's pick from a live comparison with
    // 0x22/0x25/0x26. The turn-off lines keep the default color.
    private const ushort PanicHue = 0x0021;
    private const int PanicSoundIntervalMs = 3000;

    /// <summary>Space over the map / the map menu: turns the room's panic off if there is one
    /// (anyone's), otherwise turns yours on.</summary>
    private void TogglePanic()
    {
        if (_relayClient is null)
        {
            Log("Panic: nejsi připojen(a) k online mapě.");
            return;
        }
        _relayClient.SetPanic(_roomPanic is null);
    }

    private void OnPanicChanged(object? sender, RelayProtocol.PanicState e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            // A late datagram from a client we already disconnected (null = still connecting).
            if (_relayClient is not null && sender != _relayClient) return;
            string? me = ((RelayMultiplayerClient)sender!).PlayerName;

            bool changed = _roomPanic != e.Panicker;
            _roomPanic = e.Panicker;
            _myPanic = _roomPanic is not null && _roomPanic == me;
            _panicPlayers.Clear();
            if (_roomPanic is { } other && !_myPanic) _panicPlayers.Add(other);
            UpdatePanicState();

            // A re-sync (Notify = false) only announces a panic we didn't know about yet (joined
            // while it runs); a quiet "none" just repairs a lost datagram or a leaver's panic.
            if (!e.Notify && !(changed && e.Panicker is not null)) return;

            string text;
            bool mine;
            if (e.Panicker is { } who)
            {
                mine = who == me;
                if (!mine && !changed) return;
                text = mine ? "PANIC zapnut - ostatní na mapě vidí, že potřebuješ pomoc." : PanicText(who);
                if (!mine) _panicRepeatTimer.Stop(); // restart the 10s repeat from this announcement
                if (!mine) _panicRepeatTimer.Start();
                if (!mine && e.Notify) StartPanicPeek(who); // not for one we only learn about from a re-sync
            }
            else
            {
                string actor = e.Actor;
                string? previous = e.Previous;
                mine = actor == me || previous == me;
                text = previous is null ? "Žádná panika není zapnutá."
                    : previous == me ? (actor == me ? "Panic vypnut." : $"{actor} vypnul(a) tvou paniku.")
                    : actor == me ? $"Vypnul(a) jsi paniku hráče {previous}."
                    : actor == previous ? $"{previous} už nepotřebuje pomoc."
                    : $"{actor} vypnul(a) paniku hráče {previous}.";
            }
            Log($"Multiplayer: {text}");
            AddChatSystemLine(text);
            // Over the player's head (user's request). Your own panic's lines always show - they
            // answer your own action (or tell you someone else ended your panic).
            ShowInGame(mine && e.Panicker is not null ? "PANIC zapnut - ostatni te vidi na mape" : text, overhead: true, force: mine,
                       hue: e.Panicker is not null ? PanicHue : null);
        }));
    }

    /// <summary>"PANIC! Name potřebuje pomoc! /^ North, 37 tiles" - direction and distance from
    /// your current position (left out while either position is unknown or on another facet).</summary>
    private string PanicText(string name)
    {
        string where = "";
        if (_remotePlayers.TryGetValue(name, out var them) && _lastLocalUpdate is { } me && me.Map == them.Map)
        {
            string dir = DirectionName(them.X - me.X, them.Y - me.Y);
            string arrow = DirectionArrow(dir);
            int tiles = Math.Max(Math.Abs(them.X - me.X), Math.Abs(them.Y - me.Y));
            where = arrow.Length == 0 ? $" {dir}, {tiles} tiles" : $" {arrow} {dir}, {tiles} tiles";
        }
        return $"PANIC! {name} potřebuje pomoc!{where}";
    }

    // Repeats the overhead panic text for every room mate in panic, like the shared marker's
    // (user's request); the first one is shown right when the panic arrives.
    private DispatcherTimer? _panicRepeatTimerField;
    private DispatcherTimer _panicRepeatTimer => _panicRepeatTimerField ??= CreatePanicRepeatTimer();

    private DispatcherTimer CreatePanicRepeatTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SharedMarkerRepeatSeconds) };
        timer.Tick += (_, _) =>
        {
            foreach (string name in _panicPlayers.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                ShowInGame(PanicText(name), overhead: true, hue: PanicHue);
        };
        return timer;
    }

    private void PanicSoundCheckBox_Changed(object sender, RoutedEventArgs e) => SaveSettings();

    /// <summary>Applies _myPanic/_panicPlayers to the map: "PANIC!" label, blink timer on/off,
    /// dotted lines (drawn by UpdateRemotePlayerOverlay).</summary>
    private void UpdatePanicState()
    {
        PanicStatusText.Visibility = _myPanic ? Visibility.Visible : Visibility.Collapsed;

        foreach (string name in _panicLines.Keys.Where(n => !_panicPlayers.Contains(n)).ToList())
        {
            DropMarkersCanvas.Children.Remove(_panicLines[name]);
            _panicLines.Remove(name);
        }

        if (_panicPlayers.Count > 0 && _panicBlinkTimer is null)
        {
            _panicBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PanicBlinkMs) };
            _panicBlinkTimer.Tick += (_, _) => PanicBlinkTick();
            _panicBlinkTimer.Start();
            _lastPanicSoundTicks = 0; // sound right away
            PanicBlinkTick();
            _panicRepeatTimer.Start();
        }
        else if (_panicPlayers.Count == 0 && _panicBlinkTimer is not null)
        {
            _panicBlinkTimer.Stop();
            _panicBlinkTimer = null;
            PanicFlashBorder.Visibility = Visibility.Collapsed;
            _panicRepeatTimer.Stop();
        }
        UpdateRemotePlayerOverlay();
    }

    private void PanicBlinkTick()
    {
        _panicBlinkOn = !_panicBlinkOn;
        PanicFlashBorder.Visibility = _panicBlinkOn ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (name, views) in _remotePlayerViews)
        {
            double opacity = _panicPlayers.Contains(name) && !_panicBlinkOn ? 0.2 : 1.0;
            views.Dot.Opacity = opacity;
            views.Arrow.Opacity = opacity;
        }

        long now = Environment.TickCount64;
        if (PanicSoundCheckBox.IsChecked == true && now - _lastPanicSoundTicks >= PanicSoundIntervalMs)
        {
            _lastPanicSoundTicks = now;
            System.Media.SystemSounds.Exclamation.Play();
        }
    }

    /// <summary>Dotted line from your own marker to a panicking player (screen position in
    /// logical units, possibly far off-screen - MapBorder clips it). Null hides it.</summary>
    private void PlacePanicLine(string name, Point? target)
    {
        if (!_panicPlayers.Contains(name)) return;
        if (!_panicLines.TryGetValue(name, out var line))
        {
            line = new Line { Stroke = Brushes.Red, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 2, 2 } };
            DropMarkersCanvas.Children.Add(line);
            _panicLines[name] = line;
        }
        Point? me = SelfScreenPoint();
        if (target is not Point t || me is not Point m)
        {
            line.Visibility = Visibility.Collapsed;
            return;
        }
        line.Visibility = Visibility.Visible;
        line.X1 = m.X; line.Y1 = m.Y; line.X2 = t.X; line.Y2 = t.Y;
    }

    /// <summary>Where your own marker is on the map, in logical units: dead center while
    /// tracking (see Redraw), its projected position otherwise; null before any position.</summary>
    private Point? SelfScreenPoint()
    {
        if (_markerX is not int px || _markerY is not int py) return null;
        double cx = MapBorder.ActualWidth / 2.0, cy = MapBorder.ActualHeight / 2.0;
        if (ViewFollowsPlayer) return new Point(cx, cy);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        var (sx, sy) = WorldToScreen(px, py);
        return new Point(cx + sx / dpi.DpiScaleX, cy + sy / dpi.DpiScaleY);
    }

    private Line? _dropLine, _sharedLine;
    private Ellipse? _dropDot, _sharedDot;
    private static readonly Brush DropMarkerBrush = Brushes.White;
    private static readonly Brush SharedMarkerBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));

    /// <summary>Positions the marker dots/lines and the bottom-right distance text. Called at the
    /// end of every Redraw (the player moving with Track Player off also redraws). Same
    /// physical-to-logical pixel conversion as the other overlays.</summary>
    private void UpdateDropMarkerOverlay()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double cx = MapBorder.ActualWidth / 2.0, cy = MapBorder.ActualHeight / 2.0;

        // Your own marker is dead center while tracking (see Redraw), wherever you are otherwise.
        Point? me = null;
        if (_markerX is int px && _markerY is int py)
        {
            if (ViewFollowsPlayer) me = new Point(cx, cy);
            else
            {
                var (sx, sy) = WorldToScreen(px, py);
                me = new Point(cx + sx / dpi.DpiScaleX, cy + sy / dpi.DpiScaleY);
            }
        }

        DropDistanceText.Inlines.Clear();
        int lines = 0;
        void Place(ref Line? line, ref Ellipse? dot, Brush brush, Brush textBrush, (int X, int Y, int Map)? marker, string label)
        {
            line ??= AddToDropCanvas(new Line { Stroke = brush, StrokeThickness = 1.5 });
            dot ??= AddToDropCanvas(new Ellipse { Width = 9, Height = 9, Stroke = brush, StrokeThickness = 2,
                                                  Fill = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x40, 0x40)) });

            bool visible = marker is { } mk && mk.Map == _currentFacetIndex;
            dot.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            line.Visibility = visible && me is not null ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) return;

            var (msx, msy) = WorldToScreen(marker!.Value.X, marker.Value.Y);
            double mx = cx + msx / dpi.DpiScaleX, my = cy + msy / dpi.DpiScaleY;
            Canvas.SetLeft(dot, mx - dot.Width / 2);
            Canvas.SetTop(dot, my - dot.Height / 2);
            if (me is not Point p) return;

            line.X1 = p.X; line.Y1 = p.Y; line.X2 = mx; line.Y2 = my;
            // UO range is the larger of the two axis distances - what old UOAM shows as "tiles".
            int tiles = Math.Max(Math.Abs(marker.Value.X - _markerX!.Value), Math.Abs(marker.Value.Y - _markerY!.Value));
            if (lines++ > 0) DropDistanceText.Inlines.Add(new System.Windows.Documents.LineBreak());
            DropDistanceText.Inlines.Add(new System.Windows.Documents.Run($"{label}{tiles} tiles") { Foreground = textBrush });
        }

        Place(ref _dropLine, ref _dropDot, DropMarkerBrush, Brushes.Yellow, _dropMarker, ""); // UOAM: white line, yellow "N tiles"
        Place(ref _sharedLine, ref _sharedDot, SharedMarkerBrush, SharedMarkerBrush,
              _sharedMarker is { } s ? (s.X, s.Y, s.Map) : null, "Shared: ");
        DropDistanceText.Visibility = lines > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private T AddToDropCanvas<T>(T element) where T : UIElement
    {
        DropMarkersCanvas.Children.Add(element);
        return element;
    }

    private void SetTrackPlayer(bool enabled)
    {
        if (_peek is not null) CancelPanicPeek();
        if (_goToHold)
        {
            StopFlight();
            EndGoToHold();
        }
        _trackPlayer = enabled;
        if (enabled)
        {
            _dragStart = null;
            if (MapBorder.IsMouseCaptured) MapBorder.ReleaseMouseCapture();
            // Jump back to the player (BeginPanAnimation snaps for anything farther than a few steps).
            if (_markerX is int x && _markerY is int y) BeginPanAnimation(x, y);
            Log("Track Player zapnuto - mapa sleduje postavu.");
        }
        else
        {
            if (_animRunning)
            {
                CompositionTarget.Rendering -= OnAnimationTick;
                _animRunning = false;
            }
            Log("Track Player vypnuto - mapu posuneš tažením levým tlačítkem.");
        }
        RequestRedraw();
    }

    // ---- Panic peek (user's request): when a room mate turns Panic! on, the map flies to them,
    // stays PanicPeekSeconds, and flies back to where it was: to your character with Track
    // Player on, to the exact same view with it off (you were looking at something). A drag or a
    // Track Player change meanwhile cancels the return - the user took the map over. Your own
    // position updates don't pull the view back during it (ViewFollowsPlayer). ----

    private const double PanicPeekSeconds = 2.0;
    private const double FlightMs = 350.0;

    private sealed class PanicPeek
    {
        public bool ReturnToPlayer;
        public double ReturnX, ReturnY;
    }

    private PanicPeek? _peek;
    private DispatcherTimer? _peekHoldTimer;
    private bool _flying;
    private double _flightFromX, _flightFromY, _flightToX, _flightToY;
    private long _flightStartTicks;
    private Action? _flightDone;

    /// <summary>The view follows your character: Track Player on, no panic peek running and not
    /// held on a "Go to location..." target.</summary>
    private bool ViewFollowsPlayer => _trackPlayer && _peek is null && !_goToHold;

    private void StartPanicPeek(string name)
    {
        if (_currentFacet is null || !_remotePlayers.TryGetValue(name, out var them) || them.Map != _currentFacetIndex) return;
        EndGoToHold(); // the peek returns to your character itself
        // A second panic during a peek just retargets it; the return point stays the original one.
        _peek ??= new PanicPeek { ReturnToPlayer = _trackPlayer, ReturnX = _viewX, ReturnY = _viewY };
        _peekHoldTimer?.Stop();
        FlyTo(them.X, them.Y, () =>
        {
            _peekHoldTimer ??= CreatePeekHoldTimer();
            _peekHoldTimer.Start();
        });
    }

    private DispatcherTimer CreatePeekHoldTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(PanicPeekSeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_peek is not { } peek) return;
            // Your character's position NOW, if you moved during the peek.
            var (x, y) = peek.ReturnToPlayer && _markerX is int mx && _markerY is int my ? (mx, my) : (peek.ReturnX, peek.ReturnY);
            FlyTo(x, y, EndPanicPeek);
        };
        return timer;
    }

    private void EndPanicPeek()
    {
        var peek = _peek;
        _peek = null;
        // Catch up with any step taken during the last flight.
        if (peek?.ReturnToPlayer == true && _markerX is int mx && _markerY is int my) BeginPanAnimation(mx, my);
        RequestRedraw();
    }

    /// <summary>Stops the peek where it is (the user took the map over).</summary>
    private void CancelPanicPeek()
    {
        _peekHoldTimer?.Stop();
        StopFlight();
        _peek = null;
        RequestRedraw();
    }

    /// <summary>Jumps straight back to the peek's return point (disconnect).</summary>
    private void FinishPanicPeekNow()
    {
        if (_peek is not { } peek) return;
        _peekHoldTimer?.Stop();
        StopFlight();
        if (!peek.ReturnToPlayer)
        {
            _viewX = peek.ReturnX;
            _viewY = peek.ReturnY;
            _centerX = (int)Math.Round(_viewX);
            _centerY = (int)Math.Round(_viewY);
        }
        EndPanicPeek();
    }

    /// <summary>Fast fly-over of the view to (x, y) over FlightMs, whatever the distance (the
    /// user asked for a fly-over rather than a jump even across the whole map).</summary>
    private void FlyTo(double x, double y, Action done)
    {
        if (_animRunning)
        {
            CompositionTarget.Rendering -= OnAnimationTick;
            _animRunning = false;
        }
        _flightFromX = _viewX;
        _flightFromY = _viewY;
        _flightToX = x;
        _flightToY = y;
        _flightStartTicks = Environment.TickCount64;
        _flightDone = done;
        if (!_flying)
        {
            _flying = true;
            CompositionTarget.Rendering += OnFlightTick;
        }
    }

    private void StopFlight()
    {
        if (_flying) CompositionTarget.Rendering -= OnFlightTick;
        _flying = false;
        _flightDone = null;
    }

    private void OnFlightTick(object? sender, EventArgs e)
    {
        double t = Math.Min(1.0, (Environment.TickCount64 - _flightStartTicks) / FlightMs);
        double eased = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2; // ease-in-out quad
        _viewX = _flightFromX + (_flightToX - _flightFromX) * eased;
        _viewY = _flightFromY + (_flightToY - _flightFromY) * eased;
        // EnsureCache centers on _centerX/_centerY - keep them on the flight, like a drag does.
        _centerX = (int)Math.Round(_viewX);
        _centerY = (int)Math.Round(_viewY);
        if (t < 1.0)
        {
            Redraw();
            return;
        }

        var done = _flightDone;
        StopFlight(); // before the last Redraw: back to the normal, full-margin cache
        Redraw();
        done?.Invoke();
    }

    private const string CustomLabelsFileName = "NewUOAM Labels.map";

    // The File the last new label went into - the next "New Label..." preselects it (in UOAM you
    // usually add a batch of labels to one file).
    private string? _lastLabelFile;

    /// <summary>"New Label..." from the map's context menu: old UOAM's "Edit Label" dialog,
    /// prefilled with the clicked tile, appended to the chosen marker file (UOAM format, so it
    /// loads like any other marker file - here, in old UOAM, or in Orion's World Map).</summary>
    private void AddNewLabel()
    {
        if (_contextMenuWorld is not (int x, int y)) return;
        string dir = EnsureMarkersDirectory();
        string defaultFile = _lastLabelFile is { } last && System.IO.Path.GetDirectoryName(last) == dir
            ? last : System.IO.Path.Combine(dir, CustomLabelsFileName);

        var result = ShowLabelDialog(new LabelEditWindow.Values("", "", x, y, _currentFacetIndex, defaultFile), dir);
        if (result is null) return;

        var entry = new MarkerEntry(result.X, result.Y, result.MapIndex, result.Name, IconName: result.IconName);
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            MarkerFileStore.Append(result.FilePath, entry);
        }
        catch (Exception ex)
        {
            Log($"Label se nepodařilo uložit do {result.FilePath}: {ex.Message}");
            return;
        }

        _lastLabelFile = result.FilePath;
        _markers.Add(new LoadedMarker(entry, result.FilePath));
        AfterMarkersChanged($"Label \"{entry.Name}\" přidán na {entry.X},{entry.Y} ({System.IO.Path.GetFileName(result.FilePath)}).");
    }

    /// <summary>A label's menu - the same whether right-clicked on the map or in the side panel's
    /// list (<paramref name="placementTarget"/> is the icon or the list row).</summary>
    private void ShowMarkerContextMenu(LoadedMarker marker, UIElement placementTarget)
    {
        var edit = new MenuItem { Header = "Edit" };
        edit.Click += (_, _) => EditMarker(marker);
        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => DeleteMarker(marker);

        // Like old UOAM's label menu: the drop items first (dropping onto the label's own tile),
        // then Edit/Delete.
        var menu = new ContextMenu { PlacementTarget = placementTarget, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (var item in BuildDropMarkerMenuItems((marker.Entry.X, marker.Entry.Y))) menu.Items.Add(item);
        // Sharing only while connected; moving only for a marker saved from others.
        var shareItems = WithSeparator(BuildShareMenuItems([marker], null), BuildMoveToOwnMenuItems([marker], "Přesunout do Moje markery"));
        if (shareItems.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var item in shareItems) menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(edit);
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private void EditMarker(LoadedMarker marker)
    {
        var e = marker.Entry;
        string dir = System.IO.Path.GetDirectoryName(marker.FilePath) ?? EnsureMarkersDirectory();
        var result = ShowLabelDialog(new LabelEditWindow.Values(e.Name, e.IconName ?? "", e.X, e.Y, e.MapIndex, marker.FilePath), dir);
        if (result is null) return;

        // Fields the dialog doesn't show (visibility flag, CSV color/zoom) are kept as they were.
        var updated = e with { Name = result.Name, IconName = result.IconName, X = result.X, Y = result.Y, MapIndex = result.MapIndex };
        bool sameFile = string.Equals(result.FilePath, marker.FilePath, StringComparison.OrdinalIgnoreCase);
        try
        {
            if (sameFile)
            {
                if (!MarkerFileStore.Replace(marker.FilePath, e, updated)) { ReportMarkerNotFound(marker); return; }
            }
            else
            {
                // Moving to another file: add there first, so a failure can't lose the label.
                MarkerFileStore.Append(result.FilePath, updated);
                if (!MarkerFileStore.Replace(marker.FilePath, e, null))
                    Log($"Pozor: label je nově v {System.IO.Path.GetFileName(result.FilePath)}, ale v {System.IO.Path.GetFileName(marker.FilePath)} jsem původní řádek nenašel.");
            }
        }
        catch (Exception ex)
        {
            Log($"Label se nepodařilo upravit: {ex.Message}");
            return;
        }

        int index = _markers.IndexOf(marker);
        var replacement = new LoadedMarker(updated, result.FilePath);
        if (index >= 0) _markers[index] = replacement; else _markers.Add(replacement);
        AfterMarkersChanged($"Label \"{updated.Name}\" upraven.");
    }

    private void DeleteMarker(LoadedMarker marker)
    {
        string file = System.IO.Path.GetFileName(marker.FilePath);
        if (MessageBox.Show(this, $"Smazat label \"{marker.Entry.Name}\" ze souboru {file}?", "Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            if (!MarkerFileStore.Replace(marker.FilePath, marker.Entry, null)) { ReportMarkerNotFound(marker); return; }
        }
        catch (Exception ex)
        {
            Log($"Label se nepodařilo smazat: {ex.Message}");
            return;
        }

        _markers.Remove(marker);
        AfterMarkersChanged($"Label \"{marker.Entry.Name}\" smazán ze souboru {file}.");
    }

    private void ReportMarkerNotFound(LoadedMarker marker) =>
        Log($"Label \"{marker.Entry.Name}\" jsem v {System.IO.Path.GetFileName(marker.FilePath)} nenašel - soubor se mezitím změnil? Načti markery znovu.");

    private void AfterMarkersChanged(string message)
    {
        BuildMarkerViews();
        SaveSettings();
        RequestRedraw();
        Log(message + (ShowMarkersCheckBox.IsChecked == true ? "" : " Markery jsou skryté - zapni \"Zobrazit markery\"."));
    }

    /// <summary>The markers folder (EffectiveMarkersDirectory); if that doesn't exist either, a
    /// default one (and the settings are pointed at it, so the labels come back on the next
    /// "Načíst markery").</summary>
    private string EnsureMarkersDirectory()
    {
        string dir = EffectiveMarkersDirectory;
        if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir)) return dir;

        dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NewUOAM", "markers");
        MarkersDirTextBox.Text = dir;
        return dir;
    }

    private LabelEditWindow.Values? ShowLabelDialog(LabelEditWindow.Values initial, string dir)
    {
        var lands = new List<LabelEditWindow.LandItem>();
        if (_clientData is not null)
        {
            foreach (int index in _clientData.DiscoverFacetIndices())
                if (_clientData.TryGetFacet(index, out var facet))
                    lands.Add(new(index, facet.Name, facet.WidthTiles, facet.HeightTiles));
        }
        if (lands.Count == 0)
            lands.AddRange(FacetInfo.Known.Values.OrderBy(f => f.Index).Select(f => new LabelEditWindow.LandItem(f.Index, f.Name, f.WidthTiles, f.HeightTiles)));

        var files = MarkerFilesIn(dir, initial.FilePath);
        var dialog = new LabelEditWindow(initial, lands, files, MarkerIconsDirectory, GetMarkerIcon, NormalizeIconName, IconSpelling) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <summary>Every marker file in the folder, plus <paramref name="alsoInclude"/> (the file a label
    /// is in / defaults to - it may not exist yet, it's created on save).</summary>
    private static List<LabelEditWindow.FileItem> MarkerFilesIn(string dir, string alsoInclude)
    {
        var paths = new List<string>();
        if (System.IO.Directory.Exists(dir))
            paths.AddRange(System.IO.Directory.EnumerateFiles(dir).Where(p =>
                System.IO.Path.GetExtension(p).Equals(".map", StringComparison.OrdinalIgnoreCase) ||
                System.IO.Path.GetExtension(p).Equals(".csv", StringComparison.OrdinalIgnoreCase)));
        if (!paths.Contains(alsoInclude, StringComparer.OrdinalIgnoreCase)) paths.Add(alsoInclude);
        // Shown without extension like UOAM ("Shadow"), unless a .map and a .csv share the name.
        var baseNameCounts = paths.GroupBy(p => System.IO.Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return paths.OrderBy(p => System.IO.Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                string baseName = System.IO.Path.GetFileNameWithoutExtension(p);
                return new LabelEditWindow.FileItem(p, baseNameCounts[baseName] > 1 ? System.IO.Path.GetFileName(p) : baseName);
            })
            .ToList();
    }

    /// <summary>How a marker type goes into a file: the spelling your marker files already use for
    /// it ("bank", "armourers guild"), else the type in normal case ("Town"). Picking a type in
    /// the dialog gives the icon file's name, all capitals ("TOWN"), and a new category used to
    /// end up like that (user's report 2026-09-29).</summary>
    private string IconSpelling(string icon)
    {
        string key = NormalizeIconName(icon);
        foreach (var marker in _markers)
        {
            string? name = marker.Entry.IconName?.Trim();
            if (!string.IsNullOrEmpty(name) && !IsAllCaps(name) && NormalizeIconName(name) == key) return name;
        }
        return WithoutAllCaps(icon.Trim());
    }

    private static bool IsAllCaps(string s) => s.Any(char.IsLetter) && !s.Any(char.IsLower);

    /// <summary>"TOWN" -> "Town"; anything already in mixed/lower case is left alone.</summary>
    private static string WithoutAllCaps(string s) =>
        s.Length > 1 && IsAllCaps(s) ? s[..1] + s[1..].ToLower(System.Globalization.CultureInfo.CurrentCulture) : s;

    /// <summary>Click-into-map-then-type opens the chat window (per the user's explicit request:
    /// only once the map has focus AND relay is connected - not a global hotkey, and not gated on
    /// the room password actually being valid, since the app itself has no way to know that - see
    /// the room-password design). Uses PreviewTextInput rather than PreviewKeyDown specifically
    /// because it hands over the actual composed Unicode character(s) (respecting Shift/CapsLock/
    /// keyboard layout) instead of a raw Key enum that would need error-prone manual
    /// reconstruction, and it naturally never fires for non-printable keys (Enter, Escape, arrows,
    /// Tab) - exactly the "did the user start typing a message" signal this needs, not "did they
    /// press some key."</summary>
    /// <summary>Space over the focused map toggles Panic! (old UOAM did the same), in both the
    /// normal and the map-only mode; it never opens the chat. Key repeat is ignored so holding
    /// Space doesn't flip it back and forth. Inside the chat window Space is an ordinary space
    /// (that's a different window with its own focus).</summary>
    private void MapBorder_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        e.Handled = true;
        if (!e.IsRepeat) TogglePanic();
    }

    private void MapBorder_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_relayClient is null || string.IsNullOrEmpty(e.Text)) return;
        // Space is Panic! (MapBorder_PreviewKeyDown), never the start of a chat message.
        if (e.Text == " ") { e.Handled = true; return; }

        EnsureChatWindowOpen();
        _chatWindow!.FocusInput(e.Text);
        e.Handled = true;
    }

    private void MapBorder_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void SettingsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsPanelOpen = !_settingsPanelOpen;
        UpdateSettingsPanelVisibility();
        SaveSettings();
    }

    /// <summary>SettingsPanel shows only when the user has it toggled open AND we're not in
    /// map-only mode (same "just the map" rule the old always-visible toolbar followed). Toggling
    /// this resizes MapBorder, which already triggers a redraw via MapBorder_SizeChanged - no
    /// extra repaint call needed here.</summary>
    private void UpdateSettingsPanelVisibility()
    {
        bool show = _settingsPanelOpen && !_mapOnlyMode;
        SettingsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SettingsToggleButton.Visibility = _mapOnlyMode ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Menu bar (Mapa, Online), user's request 2026-09-28 ----

    // Open menu dialogs, by the panel they show.
    private readonly Dictionary<FrameworkElement, HostedPanelWindow> _panelWindows = new();

    private void MapSettingsMenuItem_Click(object sender, RoutedEventArgs e) =>
        ShowPanelWindow("Nastavení mapy", ClientSettingsContent);

    private void OnlineMenuItem_Click(object sender, RoutedEventArgs e) =>
        ShowPanelWindow("Připojit k mapě", OnlineSettingsContent);

    /// <summary>Opens the dialog for one of DialogPanelStore's panels, or brings it to the front
    /// if it's already open.</summary>
    private void ShowPanelWindow(string title, FrameworkElement panel)
    {
        if (_panelWindows.TryGetValue(panel, out var open))
        {
            open.Activate();
            return;
        }
        var window = new HostedPanelWindow(title, panel, DialogPanelStore, this);
        window.Closed += (_, _) => _panelWindows.Remove(panel);
        _panelWindows[panel] = window;
        window.Show();
    }

    private void SetMapOnlyMode(bool enabled)
    {
        _mapOnlyMode = enabled;

        MainMenu.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        UpdateSettingsPanelVisibility();
        StatusBarBottom.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        WindowStyle = enabled ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = enabled ? ResizeMode.NoResize : ResizeMode.CanResize;
        Topmost = enabled;

        // Layout changes async; the Border's size changes once the chrome/UI rows collapse.
        Dispatcher.BeginInvoke(Redraw);
    }

    private void OnPositionChanged(object? sender, PositionUpdate update)
    {
        // BeginInvoke, not Invoke: the caller is the UDP provider's receive loop. Blocking it on
        // a full-window repaint means the next packet can't even be read off the socket until the
        // previous one's render finishes, which backs up and makes the whole feed feel stuck once
        // redraws take longer than the ~50ms interval between updates.
        //
        // Priority.Input, not Normal: a real bug found via the marker tooltip work - WPF's
        // Dispatcher always fully drains every higher-priority item before touching a lower-
        // priority one, regardless of arrival order. Normal (9) sits ABOVE Input (5), which is
        // where WPF's own mouse-hover/ToolTipService tracking runs - so with the live Orion feed
        // pushing a new position every ~20ms, a perpetual stream of Normal-priority work can
        // starve Input-priority processing indefinitely, since a fresh Normal item is always
        // queued again before the queue ever empties down to Input's level. Symptom: hovering a
        // marker icon while the UDP feed is running never shows its tooltip, no matter how long
        // you wait - confirmed by reproducing it, then confirming a plain static-XAML button's
        // tooltip worked FINE with the feed stopped and failed the same way with it running.
        // Dropping this to Input lets position-update handling interleave fairly with mouse input
        // instead of always preempting it; a few extra milliseconds of latency here is
        // imperceptible, unlike a tooltip that can never appear at all.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            // A client the map just switched away from can still have an update queued.
            if (sender is ProcessMemoryPositionProvider && !ReferenceEquals(sender, _b2Provider)) return;

            PositionText.Text = $"X: {update.X}  Y: {update.Y}  Z: {update.Z}  Map: {update.Map}" +
                                 (update.CharacterName is null ? "" : $"  ({update.CharacterName})");
            _lastLocalUpdate = update;
            _relayClient?.ReportLocalPosition(update);
            ShowLocalPosition(update);
        }));
    }

    /// <summary>Puts the player's marker (and, when following, the view) on <paramref name="update"/>.
    /// Also used right after a map load: the client is usually tracked before the map is loaded,
    /// and B2 only reports a position when it changes.</summary>
    private void ShowLocalPosition(PositionUpdate update)
    {
        if (_clientData is null) return;
        if (!_clientData.TryGetFacet(update.Map, out var facet))
        {
            Log($"Facet {update.Map} není v této klientské složce k dispozici.");
            return;
        }

        bool moved = _markerX != update.X || _markerY != update.Y;
        _currentFacet = facet;
        _currentFacetIndex = update.Map;
        _markerX = update.X;
        _markerY = update.Y;
        if (moved && _goToHold && !_goToReturning) ReturnFromGoTo();

        if (ShowCoordinatesCheckBox.IsChecked == true)
            CoordinatesOverlay.Text = $"{facet.Name} {update.X},{update.Y}";

        if (ViewFollowsPlayer)
            BeginPanAnimation(update.X, update.Y);
        else
            RequestRedraw(); // view stays put, only the player's own marker moves
    }

    /// <summary>Schedules a repaint, coalescing bursts of requests into a single render using
    /// whatever state is current by the time it actually runs. Used for redraws that aren't part
    /// of a position-driven pan (zoom, resize, mode toggle, color-map-finished) - those still just
    /// snap, there's nothing to smoothly animate between.</summary>
    private void RequestRedraw()
    {
        if (_redrawPending) return;
        _redrawPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _redrawPending = false;
            Redraw();
        }));
    }

    // A pan animation only ever needs to smooth out ordinary footsteps (a handful of tiles at
    // most). Anything bigger - the very first position after Start (jumping from the default
    // map-center view to wherever the player actually is, often thousands of tiles away),
    // a recall/gate, a facet change - must snap instantly instead of "animating": the render
    // cache's margin is sized relative to the *viewport*, not to how far apart _viewX/_viewY and
    // the new target can get, so animating across a huge distance risks the crop landing outside
    // the cache entirely (this actually happened - see the crash this constant fixes, below).
    private const double MaxAnimatedDistanceTiles = 40.0;

    /// <summary>Starts (or retargets, if one is already in flight) a smooth pan from the current
    /// _viewX/_viewY to the new logical position, driven by CompositionTarget.Rendering so it
    /// redraws at display refresh rate only while actually animating (unsubscribes once done, so
    /// an idle map costs nothing). Snaps instantly instead for a jump bigger than a few dozen
    /// tiles - see MaxAnimatedDistanceTiles.</summary>
    private void BeginPanAnimation(int targetX, int targetY)
    {
        _centerX = targetX;
        _centerY = targetY;

        double distance = Math.Max(Math.Abs(targetX - _viewX), Math.Abs(targetY - _viewY));
        if (distance > MaxAnimatedDistanceTiles)
        {
            if (_animRunning)
            {
                CompositionTarget.Rendering -= OnAnimationTick;
                _animRunning = false;
            }
            _viewX = targetX;
            _viewY = targetY;
            RequestRedraw();
            return;
        }

        _animFromX = _viewX;
        _animFromY = _viewY;
        _animToX = targetX;
        _animToY = targetY;
        _animStartTicks = Environment.TickCount64;

        if (!_animRunning)
        {
            _animRunning = true;
            CompositionTarget.Rendering += OnAnimationTick;
        }
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        double t = (Environment.TickCount64 - _animStartTicks) / AnimationDurationMs;
        if (t >= 1.0)
        {
            _viewX = _animToX;
            _viewY = _animToY;
            CompositionTarget.Rendering -= OnAnimationTick;
            _animRunning = false;
        }
        else
        {
            double eased = 1 - (1 - t) * (1 - t); // ease-out quad - starts fast, settles gently
            _viewX = _animFromX + (_animToX - _animFromX) * eased;
            _viewY = _animFromY + (_animToY - _animFromY) * eased;
        }

        Redraw();
    }

    /// <summary>World (x,y) relative to the current (possibly mid-animation) view position ->
    /// screen pixel offset from the canvas center.</summary>
    private (double sx, double sy) WorldToScreen(double wx, double wy)
    {
        double dx = wx - _viewX, dy = wy - _viewY;
        if (_renderMode == MapRenderMode.NorthUp)
            return (dx * _pixelsPerTile, dy * _pixelsPerTile);

        double h = _pixelsPerTile / 2.0;
        return ((dx - dy) * h, (dx + dy) * h);
    }

    /// <summary>Screen pixel offset from the canvas center -> world (x,y).</summary>
    private (double wx, double wy) ScreenToWorld(double sx, double sy)
    {
        if (_renderMode == MapRenderMode.NorthUp)
            return (_viewX + sx / _pixelsPerTile, _viewY + sy / _pixelsPerTile);

        double h = _pixelsPerTile / 2.0;
        double a = sx / h, b = sy / h;
        return (_viewX + (a + b) / 2.0, _viewY + (b - a) / 2.0);
    }

    /// <summary>Screen-pixel displacement for a given WORLD-space displacement. When both deltas
    /// are whole MULTIPLES OF D (the current zoom level's granularity - 1 tile for pixelsPerTile
    /// >= 2, more for the zoomed-out fractional levels; see ZoomLevels) this is always an exact
    /// whole number of pixels too - that exactness is what makes the render cache below valid
    /// (sliding by this many pixels is bit-identical to re-rendering at the new position). During
    /// a pan animation the deltas are fractional and so is the raw result; the
    /// caller rounds it to the nearest screen pixel, which only ever costs a sub-pixel wobble in
    /// the crop position - never a re-decoded/resampled tile boundary, so it can't shimmer.</summary>
    private (double sx, double sy) ProjectDelta(double worldDx, double worldDy)
    {
        if (_renderMode == MapRenderMode.NorthUp)
            return (worldDx * _pixelsPerTile, worldDy * _pixelsPerTile);

        double h = _pixelsPerTile / 2.0;
        return ((worldDx - worldDy) * h, (worldDx + worldDy) * h);
    }

    // Offscreen render cache: rendered ONCE (bigger than the visible viewport, with margin on
    // every side) and panned by cropping a moving window out of it - the same approach old UOAM
    // uses (render into a raster, slide a viewport over it) instead of recomputing colors on
    // every repaint. As long as the player stays within the cached margin, a repaint is a pure
    // memory copy: no per-pixel color recomputation, so nothing can shimmer/reshuffle, by
    // construction, not just "in practice" like the even-pixelsPerTile fix alone achieved.
    private byte[]? _cacheBgra;
    private int _cacheWidth, _cacheHeight;
    private double _cacheCenterX, _cacheCenterY;
    private MapRenderMode _cacheMode;
    private double _cachePixelsPerTile;
    private int _cacheFacetIndex = -1;
    private const int CacheMarginFactor = 3; // cache is this many viewports wide/tall (margin = (factor-1)/2 viewports each side)

    private void Redraw()
    {
        if (_currentFacet is null) return;

        // Render at PHYSICAL screen pixels, not WPF's 96-DPI logical units: a WriteableBitmap
        // declared at 96 DPI still gets resampled by WPF's own compositor to match the monitor's
        // real DPI whenever Windows display scaling isn't 100% (125%/150%/... are the Windows
        // defaults on most modern displays) - and that resampling uses its own smoothing filter,
        // not controllable via the Image's RenderOptions.BitmapScalingMode="NearestNeighbor" (that
        // only governs *our own* explicit scaling, not the system DPI compositing step). The
        // result is exactly what was reported: tiles look "too sharp/rounded" instead of crisp
        // squares like the real client or old UOAM (neither is a 96-DPI-then-rescaled WPF bitmap),
        // and since which physical pixel a given logical pixel lands on shifts continuously as
        // content moves, that softening visibly changes during panning even though our own pixel
        // grid is provably stable (see the render-cache fixes above). Fix: make the bitmap's own
        // pixel dimensions and declared DPI match the real ones, so WPF's compositor has nothing
        // left to rescale - one physical monitor pixel in, one out, every time.
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Max(1, Math.Round(MapBorder.ActualWidth * dpi.DpiScaleX));
        int height = (int)Math.Max(1, Math.Round(MapBorder.ActualHeight * dpi.DpiScaleY));
        if (width <= 1 || height <= 1) return;

        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Bgr32, null);
            MapImage.Source = _bitmap;
        }

        EnsureCache(width, height);

        var (offXf, offYf) = ProjectDelta(_viewX - _cacheCenterX, _viewY - _cacheCenterY);
        int offX = (int)Math.Round(offXf), offY = (int)Math.Round(offYf);
        int srcX = _cacheWidth / 2 + offX - width / 2;
        int srcY = _cacheHeight / 2 + offY - height / 2;

        // Defensive: EnsureCache is supposed to guarantee this crop fits, but clamp anyway rather
        // than let a future edge case (or one we haven't found yet) throw out of Array.Copy below
        // and take the whole app down - worst case this shows a very slightly wrong crop for one
        // frame instead of crashing. (A real version of exactly this - the crop landing outside
        // the cache - did crash the app: see MaxAnimatedDistanceTiles's comment for the actual
        // root cause that fix addresses; this clamp is the belt to that fix's suspenders.)
        srcX = Math.Clamp(srcX, 0, Math.Max(0, _cacheWidth - width));
        srcY = Math.Clamp(srcY, 0, Math.Max(0, _cacheHeight - height));

        int stride = width * 4;
        var pixels = new byte[stride * height];
        int cacheStride = _cacheWidth * 4;
        for (int y = 0; y < height; y++)
            Array.Copy(_cacheBgra!, (srcY + y) * cacheStride + srcX * 4, pixels, y * stride, stride);

        // Drawn at a fixed screen position (dead center), not via WorldToScreen: this "self"
        // marker's world position is always exactly the pan target (_centerX/_centerY, what
        // _viewX/_viewY is animating *toward*), so it belongs glued to the middle of the screen
        // throughout the pan - the background slides underneath it, matching the camera-follows-
        // player convention every top-down map/game uses (and matching old UOAM, which never
        // shows its own marker drifting off-center either).
        // With Track Player off the view isn't following, so the marker goes where the player
        // actually is on the (freely dragged) map instead.
        if (_markerX.HasValue && _markerY.HasValue)
        {
            if (ViewFollowsPlayer)
                DrawMarker(pixels, stride, width, height, width / 2, height / 2, _selfMarkerRgb);
            else
            {
                var (msx, msy) = WorldToScreen(_markerX.Value, _markerY.Value);
                DrawMarker(pixels, stride, width, height, width / 2 + (int)Math.Round(msx), height / 2 + (int)Math.Round(msy), _selfMarkerRgb);
            }
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);

        UpdateSelfArrow();
        UpdateGoToCrosshair();
        UpdateRemotePlayerOverlay();
        UpdateMarkersOverlay();
        UpdateDropMarkerOverlay();
    }

    /// <summary>Regenerates the offscreen cache (centered on the current player position) if the
    /// current viewport would need pixels outside it, or the mode/zoom/facet/viewport size
    /// changed since it was last built.</summary>
    private void EnsureCache(int viewportWidth, int viewportHeight)
    {
        // While flying (panic peek) the view crosses the map far faster than the margin could
        // absorb, so the cache would be rebuilt nearly every frame anyway - build just the
        // viewport then (1/9 of the work), re-centered on the flight's current tile.
        int factor = _flying ? 1 : CacheMarginFactor;
        int desiredWidth = viewportWidth * factor;
        int desiredHeight = viewportHeight * factor;

        bool staleParams = _cacheBgra is null || _cacheMode != _renderMode || _cachePixelsPerTile != _pixelsPerTile
            || _cacheFacetIndex != _currentFacetIndex || _cacheWidth != desiredWidth || _cacheHeight != desiredHeight;

        bool needsRegenerate = staleParams;
        if (!needsRegenerate)
        {
            var (offXf, offYf) = ProjectDelta(_viewX - _cacheCenterX, _viewY - _cacheCenterY);
            int offX = (int)Math.Round(offXf), offY = (int)Math.Round(offYf);
            int srcX = _cacheWidth / 2 + offX - viewportWidth / 2;
            int srcY = _cacheHeight / 2 + offY - viewportHeight / 2;
            needsRegenerate = srcX < 0 || srcY < 0 || srcX + viewportWidth > _cacheWidth || srcY + viewportHeight > _cacheHeight;
        }

        if (!needsRegenerate) return;

        _cacheWidth = desiredWidth;
        _cacheHeight = desiredHeight;
        // Always generate the cache centered on an EXACT MULTIPLE OF D tiles, snapped from the
        // current, always-integer _centerX/_centerY - never on the current, possibly-fractional
        // _viewX/_viewY (mid pan-animation). D is 1 (i.e. this is just _centerX/_centerY
        // unchanged) for every pixelsPerTile >= 2 level; see ZoomLevels for the D>1 zoomed-out
        // case. Any two multiple-of-D centers are guaranteed exactly phase-aligned with each other
        // (see ProjectDelta's doc comment) - two arbitrary/fractional ones generally are not.
        // Without this, a cache regeneration that happened to land mid-animation would build a new
        // cache on a slightly different sub-pixel phase than the old one, and near-single-pixel
        // features (small statics, terrain speckle) would visibly hop by a fraction of a pixel
        // exactly when that regeneration occurred - this was the real cause of the "bubbling 1x1
        // pixels" the user kept seeing even after the cache fix, since walking for any length of
        // time eventually triggers a regeneration while a pan animation is in flight.
        int d = ZoomLevels[_zoomIndex].D;
        _cacheCenterX = d <= 1 ? _centerX : Math.Round(_centerX / (double)d) * d;
        _cacheCenterY = d <= 1 ? _centerY : Math.Round(_centerY / (double)d) * d;
        _cacheMode = _renderMode;
        _cachePixelsPerTile = _pixelsPerTile;
        _cacheFacetIndex = _currentFacetIndex;
        _cacheBgra = RenderRegion(_currentFacet!, _cacheWidth, _cacheHeight, _cacheCenterX, _cacheCenterY);
    }

    /// <summary>The actual per-pixel color computation - only ever called by
    /// <see cref="EnsureCache"/> to (re)build the offscreen cache, never directly per repaint.
    /// Takes its center explicitly (see the comment at the call site) rather than reading
    /// _viewX/_viewY, specifically so it can never accidentally render off the live, possibly
    /// mid-animation view position.</summary>
    private byte[] RenderRegion(MapFacet facet, int width, int height, double centerX, double centerY) =>
        RenderRegion(facet, _currentFacetIndex, _renderMode, _pixelsPerTile, width, height, centerX, centerY);

    /// <summary>The same rendering for an explicit facet/projection/zoom - also used by the track
    /// map window (RenderTrackMap), which has its own zoom.</summary>
    private byte[] RenderRegion(MapFacet facet, int facetIndex, MapRenderMode mode, double pixelsPerTile,
        int width, int height, double centerX, double centerY)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        var radar = _clientData?.RadarColors;
        FacetColorMap? colorMap = null;
        _clientData?.TryGetColorMap(facetIndex, out colorMap);

        int cx = width / 2, cy = height / 2;

        // This is ScreenToWorld's formula inlined against the explicit center parameter (not the
        // live _viewX/_viewY field) - and, same as ScreenToWorld, computed once per row then walked
        // with additions instead of paying its division per pixel.
        double stepWxPerPx, stepWyPerPx, stepWxPerPy, stepWyPerPy;
        double originWx, originWy;
        if (mode == MapRenderMode.NorthUp)
        {
            stepWxPerPx = 1.0 / pixelsPerTile; stepWyPerPx = 0;
            stepWxPerPy = 0; stepWyPerPy = 1.0 / pixelsPerTile;
            originWx = centerX + (-cx) / pixelsPerTile;
            originWy = centerY + (-cy) / pixelsPerTile;
        }
        else
        {
            double h = pixelsPerTile / 2.0;
            stepWxPerPx = 1.0 / (2 * h); stepWyPerPx = -1.0 / (2 * h);
            stepWxPerPy = 1.0 / (2 * h); stepWyPerPy = 1.0 / (2 * h);
            double a = (-cx) / h, b = (-cy) / h;
            originWx = centerX + (a + b) / 2.0;
            originWy = centerY + (b - a) / 2.0;
        }

        double rowWx = originWx, rowWy = originWy;

        for (int py = 0; py < height; py++)
        {
            double wx = rowWx, wy = rowWy;
            int rowStart = py * stride;

            // Memoize the previous pixel's tile/color within this row - at any sane zoom level
            // many consecutive screen pixels land on the same source tile.
            int lastTileX = int.MinValue, lastTileY = int.MinValue;
            byte lr = 0, lg = 0, lb = 0;

            for (int px = 0; px < width; px++)
            {
                int tileX = (int)Math.Floor(wx), tileY = (int)Math.Floor(wy);
                if (tileX != lastTileX || tileY != lastTileY)
                {
                    if (colorMap is not null)
                    {
                        colorMap.Sample(tileX, tileY, out lr, out lg, out lb);
                    }
                    else
                    {
                        // Color map for this facet isn't precomputed yet (still loading, or the
                        // client folder doesn't have it) - fall back to decoding on demand.
                        if (facet.HasStatics && facet.TryGetTopStatic(tileX, tileY, out var top) && radar is not null)
                        {
                            var c = radar.GetColor((ushort)(top.TileId + 0x4000));
                            lr = c.R; lg = c.G; lb = c.B;
                        }
                        else
                        {
                            var tile = facet.GetLandTile(tileX, tileY);
                            if (radar is not null)
                            {
                                var c = radar.GetColor(tile.TileId);
                                lr = c.R; lg = c.G; lb = c.B;
                            }
                            else
                            {
                                byte gray = (byte)Math.Clamp(128 + tile.Z * 3, 0, 255);
                                lr = lg = lb = gray;
                            }
                        }
                    }
                    lastTileX = tileX; lastTileY = tileY;
                }

                byte r = lr, g = lg, b = lb;
                wx += stepWxPerPx; wy += stepWyPerPx;

                int i = rowStart + px * 4;
                pixels[i + 0] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }

            rowWx += stepWxPerPy; rowWy += stepWyPerPy;
        }

        return pixels;
    }

    /// <summary>Side of the player squares in physical pixels - yours (DrawMarker) and the other
    /// players' (UpdateRemotePlayerOverlay). Odd, so the square has a center pixel.</summary>
    private const int PlayerMarkerSizePx = 5;

    /// <summary>The player's own marker: a filled square (PlayerMarkerSizePx, physical pixels) in
    /// your marker color (default red), the same size as the other players' markers.</summary>
    private static void DrawMarker(byte[] pixels, int stride, int width, int height, int cx, int cy, (byte R, byte G, byte B) color)
    {
        const int halfSize = PlayerMarkerSizePx / 2;
        for (int dy = -halfSize; dy <= halfSize; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= height) continue;
            for (int dx = -halfSize; dx <= halfSize; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= width) continue;
                int i = y * stride + x * 4;
                pixels[i + 0] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
                pixels[i + 3] = 255;
            }
        }
    }
}
