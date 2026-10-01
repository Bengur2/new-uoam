using System.Runtime.InteropServices;
using System.Windows;

namespace NewUOAM.App;

/// <summary>A window's saved position and size (WPF units), persisted in AppSettings so the map
/// and the chat reopen where the user last left them.</summary>
public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    public Rect ToRect() => new(Left, Top, Width, Height);

    /// <summary>The window's normal bounds (RestoreBounds while maximized or minimized - a
    /// minimized window sits at -32000), or null before it has been shown.</summary>
    public static WindowBounds? From(Window window)
    {
        Rect r = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0 || double.IsNaN(r.Left) || double.IsNaN(r.Top)) return null;
        return new WindowBounds
        {
            Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height,
            Maximized = window.WindowState == WindowState.Maximized,
        };
    }
}

/// <summary>Where windows may go on the current monitor setup. A saved position can point at a
/// monitor that no longer exists (two monitors before, one now) or a smaller resolution, so a
/// restored position is always checked against the monitors present right now.</summary>
internal static class ScreenPlacement
{
    private const double Gap = 4;

    /// <summary>The work area (screen minus taskbar) of every monitor, in WPF units. The app is
    /// system-DPI-aware (no manifest), so Windows reports every monitor in the system DPI's
    /// coordinates and one scale converts them all.</summary>
    public static List<Rect> GetWorkAreas()
    {
        double scale = GetDpiForSystem() / 96.0;
        if (scale <= 0) scale = 1;
        var areas = new List<Rect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var w = info.rcWork;
                areas.Add(new Rect(w.Left / scale, w.Top / scale, (w.Right - w.Left) / scale, (w.Bottom - w.Top) / scale));
            }
            return true;
        }, IntPtr.Zero);
        if (areas.Count == 0) areas.Add(SystemParameters.WorkArea);
        return areas;
    }

    /// <summary>Makes a saved rectangle usable on today's monitors, or null if it's gone.
    /// Kept as is when all four corners are on some monitor (this also allows a window spanning
    /// two adjacent monitors). Otherwise it's moved (and shrunk if needed) fully onto the monitor
    /// it overlaps most - e.g. after a resolution change it's pulled back into view. If it
    /// doesn't overlap any monitor at all (it was on a monitor that's now disconnected), null:
    /// the caller then uses its default placement.</summary>
    public static Rect? FitToScreens(Rect saved)
    {
        if (saved.Width <= 0 || saved.Height <= 0 || double.IsNaN(saved.Left) || double.IsNaN(saved.Top)) return null;
        var areas = GetWorkAreas();

        // Corners are tested 1 unit inside, so a window snapped exactly to a screen edge counts.
        Point[] corners =
        [
            new(saved.Left + 1, saved.Top + 1), new(saved.Right - 1, saved.Top + 1),
            new(saved.Left + 1, saved.Bottom - 1), new(saved.Right - 1, saved.Bottom - 1),
        ];
        if (corners.All(c => areas.Any(a => a.Contains(c)))) return saved;

        Rect best = Rect.Empty;
        double bestOverlap = 0;
        foreach (var area in areas)
        {
            var overlap = Rect.Intersect(area, saved);
            double size = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
            if (size > bestOverlap) { bestOverlap = size; best = area; }
        }
        return bestOverlap > 0 ? ClampInto(saved, best) : null;
    }

    /// <summary>A spot for a window of <paramref name="size"/> right next to <paramref name="anchor"/>
    /// (right, below, left, above - the first that fits fully on some monitor), or null if none fits.</summary>
    public static Rect? PlaceNextTo(Rect anchor, Size size)
    {
        var areas = GetWorkAreas();
        Rect[] candidates =
        [
            new(anchor.Right + Gap, anchor.Top, size.Width, size.Height),
            new(anchor.Left, anchor.Bottom + Gap, size.Width, size.Height),
            new(anchor.Left - Gap - size.Width, anchor.Top, size.Width, size.Height),
            new(anchor.Left, anchor.Top - Gap - size.Height, size.Width, size.Height),
        ];
        foreach (var candidate in candidates)
            foreach (var area in areas)
                if (area.Contains(candidate)) return candidate;
        // Nothing fits exactly (e.g. the map is at the bottom-right corner): the side with the most
        // room, shifted along the anchor's edge as far as needed.
        foreach (var candidate in candidates)
            foreach (var area in areas)
            {
                var shifted = ClampInto(candidate, area);
                if (shifted.Size == candidate.Size && !shifted.IntersectsWith(anchor)) return shifted;
            }
        return null;
    }

    private static Rect ClampInto(Rect r, Rect area)
    {
        double w = Math.Min(r.Width, area.Width), h = Math.Min(r.Height, area.Height);
        double x = Math.Clamp(r.Left, area.Left, area.Right - w);
        double y = Math.Clamp(r.Top, area.Top, area.Bottom - h);
        return new Rect(x, y, w, h);
    }

    // ---- Win32 ----

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
