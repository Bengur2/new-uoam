using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using NewUOAM.Positioning.Relay;

namespace NewUOAM.App;

/// <summary>The track map (user's request 2026-09-30): a second, resizable map window showing
/// where the last "-t" track report was made and who was tracked there. It keeps showing that
/// report, with its age, until the next one arrives or the window is closed. The rendering itself
/// is MainWindow's (the <see cref="Renderer"/> delegate), so it looks exactly like the main map,
/// in the same projection; this window has its own zoom (mouse wheel).</summary>
public partial class TrackMapWindow : Window
{
    /// <summary>BGRA pixels of a <paramref name="widthPx"/> x <paramref name="heightPx"/> view of
    /// facet <paramref name="map"/> centered on (centerX, centerY), or null if that facet isn't loaded.</summary>
    public delegate byte[]? Renderer(int map, int widthPx, int heightPx, double centerX, double centerY, double pixelsPerTile);

    private const int MinPixelsPerTile = 2, MaxPixelsPerTile = 24;

    private readonly Renderer _render;
    private readonly DispatcherTimer _ageTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private RelayProtocol.TrackReport? _report;
    private DateTime _reportTime;
    // Even, for the same reason as the main map's zoom (Rotated45 moves by pixelsPerTile/2).
    private int _pixelsPerTile = 8;

    public bool MapOnly { get; private set; }

    public TrackMapWindow(Renderer render)
    {
        InitializeComponent();
        _render = render;
        _ageTimer.Tick += (_, _) => UpdateHeader();
        Loaded += (_, _) => Redraw();
        Loc.Changed += UpdateHeader;
        Closed += (_, _) =>
        {
            _ageTimer.Stop();
            Loc.Changed -= UpdateHeader;
        };
    }

    public void ShowReport(RelayProtocol.TrackReport report)
    {
        _report = report;
        _reportTime = DateTime.Now;
        _ageTimer.Start();
        Redraw();
    }

    /// <summary>Re-renders (e.g. the main map's projection changed or the map finished loading).</summary>
    public void Redraw()
    {
        UpdateHeader();
        if (!IsLoaded || _report is not { } r || MapBorder.ActualWidth < 1 || MapBorder.ActualHeight < 1)
        {
            SpotRing.Visibility = NamesBox.Visibility = Visibility.Collapsed;
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int w = Math.Max(1, (int)(MapBorder.ActualWidth * dpi.DpiScaleX));
        int h = Math.Max(1, (int)(MapBorder.ActualHeight * dpi.DpiScaleY));
        byte[]? pixels = _render(r.Map, w, h, r.X, r.Y, _pixelsPerTile);
        if (pixels is null)
        {
            MapImage.Source = null;
            HeaderText.Text += "\nMapa není načtená.";
        }
        else
        {
            var bitmap = new WriteableBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
            MapImage.Source = bitmap;
        }

        // The spot is the center of the view; the names go below it, kept inside the window.
        double cx = MapBorder.ActualWidth / 2, cy = MapBorder.ActualHeight / 2;
        Canvas.SetLeft(SpotRing, cx - SpotRing.Width / 2);
        Canvas.SetTop(SpotRing, cy - SpotRing.Height / 2);
        SpotRing.Visibility = Visibility.Visible;

        // Below the ring, at most down to the window's bottom edge; with more names than fit, the
        // box scrolls (mouse wheel over it) rather than covering the map or being cut off.
        NamesText.Text = string.Join(", ", r.Names);
        NamesBox.Visibility = Visibility.Visible;
        double top = cy + SpotRing.Height / 2 + 4;
        NamesBox.MaxWidth = Math.Max(60, MapBorder.ActualWidth - 4);
        NamesBox.MaxHeight = Math.Max(24, MapBorder.ActualHeight - top - 4);
        NamesBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = NamesBox.DesiredSize;
        Canvas.SetLeft(NamesBox, Math.Clamp(cx - size.Width / 2, 2, Math.Max(2, MapBorder.ActualWidth - size.Width - 2)));
        Canvas.SetTop(NamesBox, Math.Clamp(top, 2, Math.Max(2, MapBorder.ActualHeight - size.Height - 2)));
    }

    private void UpdateHeader()
    {
        if (_report is not { } r)
        {
            HeaderText.Text = Loc.T("Tr_None");
            return;
        }
        HeaderText.Text = $"{r.Reporter} · {_reportTime:HH:mm} ({Age(DateTime.Now - _reportTime)}) · {r.X},{r.Y} · {r.Names.Count} {Loc.Plural("Tr_Players", r.Names.Count)}";
    }

    private static string Age(TimeSpan age) =>
        age.TotalMinutes < 1 ? Loc.T("Tr_JustNow")
        : age.TotalHours < 1 ? Loc.F("Tr_MinutesAgo", (int)age.TotalMinutes)
        : Loc.F("Tr_HoursAgo", (int)age.TotalHours);

    private void MapBorder_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void MapBorder_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        int next = Math.Clamp(_pixelsPerTile + (e.Delta > 0 ? 2 : -2), MinPixelsPerTile, MaxPixelsPerTile);
        if (next == _pixelsPerTile) return;
        _pixelsPerTile = next;
        Redraw();
    }

    private void MapBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            SetMapOnly(!MapOnly);
            e.Handled = true;
        }
        else if (MapOnly && e.LeftButton == MouseButtonState.Pressed)
        {
            // No title bar in map-only mode - drag the window by the map.
            try { DragMove(); }
            catch (InvalidOperationException) { /* button already released */ }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Map-only mode: no title bar, own always-on-top, resize grip. Same order of chrome
    /// and template changes as ChatWindow.SetCompact - see there for the two crashes the order avoids.</summary>
    public void SetMapOnly(bool mapOnly)
    {
        MapOnly = mapOnly;
        if (!mapOnly) WindowChrome.SetWindowChrome(this, null);
        WindowStyle = mapOnly ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = mapOnly ? ResizeMode.CanResizeWithGrip : ResizeMode.CanResize;
        ApplyTemplate();
        if (mapOnly)
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
        Topmost = mapOnly || StayOnTop;
    }

    /// <summary>Set by MainWindow when the window popped up because of a track ("otevřít navrchu").</summary>
    public bool StayOnTop
    {
        get => _stayOnTop;
        set
        {
            _stayOnTop = value;
            Topmost = MapOnly || value;
        }
    }
    private bool _stayOnTop;
}
