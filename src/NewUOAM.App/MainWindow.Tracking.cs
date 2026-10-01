using System.Media;
using System.Windows;
using NewUOAM.Positioning.Relay;

namespace NewUOAM.App;

// ---- Track reports (user's request 2026-09-30). "-t Bodhi, Sir Lancelot" typed in the game -
// normally by tools/OrionScripts/TrackPlayers.oajs right after a Tracking > Players use - reports
// the tracked players to the room, at the spot where you stood (tracking finds people around
// you). Comma separates names, since names can contain spaces. Everyone in the room gets a chat
// line, a line in the game, a sound (others' reports only, TrackSoundCheckBox) and, optionally,
// the track map window (TrackMapWindow): either kept open all the time, or popping up always on
// top when someone else tracks (ShowTrackMapCheckBox + TrackMapPopupRadio/TrackMapAlwaysRadio).
// The window shows the last report until the next one or until it's closed. ----
public partial class MainWindow
{
    private TrackMapWindow? _trackWindow;
    private RelayProtocol.TrackReport? _lastTrack;
    // The track window's last position/size and map-only mode (AppSettings), updated when it closes.
    private WindowBounds? _trackBounds;
    private bool _trackMapOnly;

    // A long name list doesn't fit in one line of speech, so TrackPlayers.oajs sends it as several
    // "-t" lines a few hundred ms apart. Parts from the same client that arrive within
    // TrackMergeMs of each other are one track: collected here and reported once, at the position
    // where the first part came in.
    private const int TrackMergeMs = 1000;
    private readonly List<string> _pendingTrackNames = new();
    private (int Pid, int X, int Y, int Map)? _pendingTrack;
    private System.Windows.Threading.DispatcherTimer? _trackMergeTimer;

    /// <summary>"-t name, name, ...": reports the tracked players at your current position.</summary>
    private void HandleTrackCommand(int pid, string args)
    {
        if (_relayClient is null) { ReplyInGame(pid, "Nejsi připojen(a) k online mapě."); return; }
        var names = RelayProtocol.TrackNames(args.Split(','));
        if (names.Count == 0) { ReplyInGame(pid, "Použití: -t jméno, jméno (jména oddělená čárkou)."); return; }

        if (_pendingTrack is { } pending && pending.Pid != pid) FlushTrack(); // another client's track
        if (_pendingTrack is null)
        {
            if (_lastLocalUpdate is not { } me) { ReplyInGame(pid, "Mapa nezná tvoji pozici - track se neodeslal."); return; }
            _pendingTrack = (pid, me.X, me.Y, me.Map);
        }
        _pendingTrackNames.AddRange(names);

        _trackMergeTimer ??= CreateTrackMergeTimer();
        _trackMergeTimer.Stop();
        _trackMergeTimer.Start(); // restarted by every part: the report goes out after the last one
    }

    private System.Windows.Threading.DispatcherTimer CreateTrackMergeTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TrackMergeMs) };
        timer.Tick += (_, _) => FlushTrack();
        return timer;
    }

    private void FlushTrack()
    {
        _trackMergeTimer?.Stop();
        if (_pendingTrack is not { } t) return;
        var names = RelayProtocol.TrackNames(_pendingTrackNames);
        int total = _pendingTrackNames.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        _pendingTrack = null;
        _pendingTrackNames.Clear();
        if (_relayClient is null) { ReplyInGame(t.Pid, "Nejsi připojen(a) k online mapě."); return; }
        if (total > names.Count)
            ReplyInGame(t.Pid, $"Tracknuto {total} hráčů, posílám prvních {names.Count} (víc jich jeden track neunese).");
        // The report comes back from the server like everyone else's (OnTrackReported).
        if (!_relayClient.ReportTrack(t.X, t.Y, t.Map, names))
            ReplyInGame(t.Pid, "Track se neodeslal - mapa zatím nezná tvoje jméno.");
    }

    private void OnTrackReported(RelayProtocol.TrackReport r)
    {
        bool mine = r.Reporter == _relayClient?.PlayerName;
        string names = string.Join(", ", r.Names);
        string where = WhereFromMe(r.X, r.Y, r.Map);

        AppendChat(ChatMessageViewModel.System($"{r.Reporter} tracknul(a) u {r.X},{r.Y}{where}: {names}"), countsAsUnread: !mine);
        Log($"Track: {r.Reporter} u {r.X},{r.Y}: {names}");
        ShowInGame($"[Mapa] {(mine ? "Track odeslan" : $"{r.Reporter} tracknul(a)")}: {names}{where}");
        if (!mine && TrackSoundCheckBox.IsChecked == true) SystemSounds.Asterisk.Play();

        _lastTrack = r;
        if (ShowTrackMapCheckBox.IsChecked != true) return;
        bool popup = TrackMapPopupRadio.IsChecked == true;
        if (_trackWindow is null && popup && mine) return; // your own track doesn't pop the window up
        EnsureTrackWindow(stayOnTop: popup && !mine);
        _trackWindow!.ShowReport(r);
    }

    /// <summary>" (120 tiles NorthEast od tebe)", or "" when your position isn't known / another facet.</summary>
    private string WhereFromMe(int x, int y, int map)
    {
        if (_lastLocalUpdate is not { } me || me.Map != map) return "";
        int dx = x - me.X, dy = y - me.Y;
        int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return distance == 0 ? "" : $" ({distance} tiles {DirectionName(dx, dy)} od tebe)";
    }

    /// <summary>Opens the track window if it isn't open (where it was last, else next to the map),
    /// and brings it forward. <paramref name="stayOnTop"/>: popped up by someone's track.</summary>
    private void EnsureTrackWindow(bool stayOnTop)
    {
        if (_trackWindow is null)
        {
            var window = new TrackMapWindow(RenderTrackMap) { WindowStartupLocation = WindowStartupLocation.Manual };
            Rect rect = _trackBounds is not null && ScreenPlacement.FitToScreens(_trackBounds.ToRect()) is { } saved ? saved
                : ScreenPlacement.PlaceNextTo(WindowBounds.From(this)?.ToRect() ?? new Rect(Left, Top, ActualWidth, ActualHeight), new Size(window.Width, window.Height))
                  ?? new Rect(Left + 40, Top + 40, window.Width, window.Height);
            window.Left = rect.Left;
            window.Top = rect.Top;
            window.Width = rect.Width;
            window.Height = rect.Height;
            window.Closing += (_, _) =>
            {
                _trackBounds = WindowBounds.From(window) ?? _trackBounds;
                _trackMapOnly = window.MapOnly;
                SaveSettings();
            };
            window.Closed += (_, _) => { if (ReferenceEquals(_trackWindow, window)) _trackWindow = null; };
            _trackWindow = window;
            window.Show();
            if (_trackMapOnly) window.SetMapOnly(true);
            if (_lastTrack is { } last) window.ShowReport(last);
        }
        _trackWindow.StayOnTop = stayOnTop;
        if (_trackWindow.WindowState == WindowState.Minimized) _trackWindow.WindowState = WindowState.Normal;
        if (stayOnTop) _trackWindow.Activate();
    }

    /// <summary>TrackMapWindow's renderer: the main map's rendering and projection, its own zoom.</summary>
    private byte[]? RenderTrackMap(int map, int widthPx, int heightPx, double centerX, double centerY, double pixelsPerTile)
    {
        if (_clientData is null || !_clientData.TryGetFacet(map, out var facet)) return null;
        return RenderRegion(facet, map, _renderMode, pixelsPerTile, widthPx, heightPx, centerX, centerY);
    }

    /// <summary>"Nechat otevřenou pořád": the window is open from startup and after switching to it.</summary>
    private void OpenTrackWindowIfAlways()
    {
        if (ShowTrackMapCheckBox.IsChecked == true && TrackMapAlwaysRadio.IsChecked == true) EnsureTrackWindow(stayOnTop: false);
    }

    private void TrackMapSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingLoadedSettings) return;
        if (ShowTrackMapCheckBox.IsChecked != true) _trackWindow?.Close();
        else OpenTrackWindowIfAlways();
        UpdateTrackSettingsEnabled();
        SaveSettings();
    }

    private void UpdateTrackSettingsEnabled()
    {
        if (TrackMapPopupRadio is null || TrackMapAlwaysRadio is null) return; // during InitializeComponent
        TrackMapPopupRadio.IsEnabled = TrackMapAlwaysRadio.IsEnabled = ShowTrackMapCheckBox.IsChecked == true;
    }

    private void TrackSoundCheckBox_Changed(object sender, RoutedEventArgs e) => SaveSettings();
}
