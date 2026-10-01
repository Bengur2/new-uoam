using System.Threading;
using System.Windows;
using NewUOAM.MapData;
using NewUOAM.MapData.Maps;

namespace NewUOAM.App;

// Old UOAM's view switches in the Mapa menu (user's request 2026-10-01): Normal view / X-ray view
// and Show statics / Hide statics. With statics hidden the map is land only, so Normal/X-ray have
// nothing to choose between and are disabled, as in old UOAM. The drawing rules are in StaticsView.
public partial class MainWindow
{
    private bool _showStatics = true;
    private bool _xRayView;
    // What the map is actually drawn in. It follows CurrentStaticsView only once the new view's
    // color map is in memory (a ~50 ms disk-cache load), because drawing a view decoded on demand
    // takes seconds - the "2-3 s per switch" the user saw. Only a view not on disk yet is drawn
    // decoded on demand, while it's being built.
    private StaticsView _drawnStaticsView = StaticsView.Normal;
    private int _viewSwitchTicket;
    private CancellationTokenSource? _preloadCts;

    private StaticsView CurrentStaticsView =>
        !_showStatics ? StaticsView.Hidden : _xRayView ? StaticsView.XRay : StaticsView.Normal;

    private void NormalViewMenuItem_Click(object sender, RoutedEventArgs e) => SetStaticsView(_showStatics, xRay: false);
    private void XRayViewMenuItem_Click(object sender, RoutedEventArgs e) => SetStaticsView(_showStatics, xRay: true);
    private void ShowStaticsMenuItem_Click(object sender, RoutedEventArgs e) => SetStaticsView(showStatics: true, _xRayView);
    private void HideStaticsMenuItem_Click(object sender, RoutedEventArgs e) => SetStaticsView(showStatics: false, _xRayView);

    private void UpdateStaticsViewMenu()
    {
        NormalViewMenuItem.IsChecked = !_xRayView;
        XRayViewMenuItem.IsChecked = _xRayView;
        NormalViewMenuItem.IsEnabled = XRayViewMenuItem.IsEnabled = _showStatics;
        ShowStaticsMenuItem.IsChecked = _showStatics;
        HideStaticsMenuItem.IsChecked = !_showStatics;
    }

    private async void SetStaticsView(bool showStatics, bool xRay)
    {
        bool changed = showStatics != _showStatics || xRay != _xRayView;
        _showStatics = showStatics;
        _xRayView = xRay;
        UpdateStaticsViewMenu();
        if (!changed) return;
        SaveSettings();

        var view = CurrentStaticsView;
        if (_clientData is not { } clientData)
        {
            _drawnStaticsView = view;
            return;
        }

        // The facet on screen first, from the disk cache; the old view stays on screen meanwhile.
        int ticket = ++_viewSwitchTicket;
        int facetIndex = _currentFacetIndex;
        try
        {
            await Task.Run(() => clientData.TryLoadCachedColorMap(facetIndex, view));
        }
        catch (Exception)
        {
            // Not cached / unreadable: it gets built below and drawn on demand meanwhile.
        }
        if (ticket != _viewSwitchTicket || _clientData != clientData) return; // switched again

        _drawnStaticsView = view;
        Redraw(); // EnsureCache sees the view change and rebuilds
        _trackWindow?.Redraw();
        _ = PreloadColorMapsAsync(clientData); // the other facets; drops the old view's maps
    }

    /// <summary>Loads (disk cache) or builds every facet's color map in the current view, the
    /// facet on screen first, then builds the other views into the disk cache in the background.
    /// A newer call (view switched again, another map folder) cancels the older one. Returns false
    /// only on an error, which is logged.</summary>
    private async Task<bool> PreloadColorMapsAsync(UoClientData clientData)
    {
        _preloadCts?.Cancel();
        var cts = _preloadCts = new CancellationTokenSource();
        var view = CurrentStaticsView;
        try
        {
            var progress = new Progress<string>(msg => { if (_preloadCts == cts) Log(msg); });
            await clientData.PreloadAllColorMapsAsync(view, _currentFacetIndex, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            if (_preloadCts == cts) Log($"Chyba při předpočítávání map: {ex.Message}");
            return false;
        }

        if (_clientData != clientData || _preloadCts != cts) return true;
        // A cancelled build of the previous view may have finished just before noticing.
        clientData.ReleaseColorMapsExcept(view);
        ReturnFreedColorMaps();
        Log("Mapy předpočítané - panning/zoom/otočení jsou teď rychlé.");
        RequestRedraw();
        _ = PrebuildOtherViewsAsync(clientData, view, cts.Token);
        return true;
    }

    /// <summary>A color map is one big array per facet (~100 MiB), on the large object heap, which
    /// a normal GC neither compacts nor hands back. Without this, every view switch left the old
    /// view's arrays behind and the process grew by hundreds of MB per switch (measured on Moria:
    /// 401 MB before, 2.2 GB after six switches; 720 MB with this). Runs only after a switch or a
    /// prebuild, so the cost is rare.</summary>
    private static void ReturnFreedColorMaps()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>Old UOAM renders every view at load, so switching is instant even the first time.
    /// Same here, quietly and on the disk only (memory keeps just the view shown); views already
    /// on disk cost a header check. Cancelled with the preload that started it.</summary>
    private async Task PrebuildOtherViewsAsync(UoClientData clientData, StaticsView shown, CancellationToken ct)
    {
        var others = Enum.GetValues<StaticsView>().Where(v => v != shown).ToList();
        try
        {
            await clientData.PrebuildColorMapsOnDiskAsync(others, _currentFacetIndex, ct);
            ReturnFreedColorMaps(); // every build allocated a whole facet's worth of pixels
        }
        catch (Exception)
        {
            // Cancelled, or a cache write failed - a switch then just builds that view itself.
        }
    }
}
