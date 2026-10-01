using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NewUOAM.MapData.Markers;
using NewUOAM.Positioning.Relay;

namespace NewUOAM.App;

// ---- Sharing markers with the room (user's request 2026-09-29). See RelayProtocol.MarkShareTag
// for the wire side. You share one marker or a whole category (right-click in the panel or on
// the map); everyone else sees them under "Sdílené v místnosti", by player, HIDDEN by default so
// a mis-click on a big category can't flood their map. Visibility is three levels, in memory
// only: the section checkbox (everyone), a player's checkbox, and single markers (an override
// of the player's choice). "Uložit" copies a marker into shared_markers.map, which then shows
// under "Uložené od ostatních" like any local marker; unsharing or the owner leaving never
// touches saved copies. A shared marker that already has a local twin (same position and name)
// isn't drawn twice. ----
public partial class MainWindow
{
    // Everyone else's shared markers (a snapshot of RelayMultiplayerClient.GetSharedMarks).
    private IReadOnlyDictionary<string, IReadOnlyList<RelayProtocol.SharedMark>> _sharedMarks =
        new Dictionary<string, IReadOnlyList<RelayProtocol.SharedMark>>();
    private bool _sharedMarksAllVisible = true;
    private readonly Dictionary<string, bool> _sharedOwnerVisible = new();            // default: hidden
    private readonly Dictionary<(string Owner, string Id), bool> _sharedMarkVisible = new(); // overrides the player's choice
    private readonly HashSet<string> _expandedSharedOwners = new();
    private readonly Dictionary<(string Owner, string Id), (Border Frame, RelayProtocol.SharedMark Mark)> _sharedMarkViews = new();
    private bool _sharedMarksRefreshPending;

    private bool IsSharedMarkChecked(string owner, string id) =>
        _sharedMarkVisible.TryGetValue((owner, id), out bool chosen) ? chosen : _sharedOwnerVisible.GetValueOrDefault(owner);

    /// <summary>Whether a shared marker is drawn: all three switches allow it, and there's no local
    /// copy of it (that one is drawn instead).</summary>
    private bool IsSharedMarkShown(string owner, RelayProtocol.SharedMark mark) =>
        _sharedMarksAllVisible && IsSharedMarkChecked(owner, mark.Id) && !IsSavedLocally(mark);

    // ---- Sharing my own markers ----

    /// <summary>A local marker as it would be shared (null if its name can't be sent, e.g. empty).</summary>
    private static RelayProtocol.SharedMark? ToSharedMark(LoadedMarker marker)
    {
        var e = marker.Entry;
        string icon = RelayProtocol.SanitizeMarkIcon(e.IconName);
        string? name = RelayProtocol.SanitizeMarkName(e.Name);
        return name is null ? null
            : RelayProtocol.CreateSharedMark(RelayProtocol.SharedMarkId(e.X, e.Y, e.MapIndex, icon, name), e.X, e.Y, e.MapIndex, icon, name);
    }

    private bool IsSharedByMe(LoadedMarker marker) =>
        _relayClient is { MySharedMarkCount: > 0 } client && ToSharedMark(marker) is { } mark && client.IsMarkShared(mark.Id);

    /// <summary>"Sdílet" / "Přestat sdílet" for one marker or a category (<paramref name="categoryName"/>).
    /// Empty when not connected.</summary>
    private List<Control> BuildShareMenuItems(IReadOnlyCollection<LoadedMarker> markers, string? categoryName)
    {
        if (_relayClient is not { } client || markers.Count == 0) return [];
        var marks = markers.Select(ToSharedMark).OfType<RelayProtocol.SharedMark>().ToList();
        var shared = marks.Where(m => client.IsMarkShared(m.Id)).ToList();
        var notShared = marks.Where(m => !client.IsMarkShared(m.Id)).ToList();
        var items = new List<Control>();

        if (notShared.Count > 0)
        {
            var share = new MenuItem
            {
                Header = categoryName is null ? Loc.T("Ctx_Share") : Loc.F("Ctx_ShareCategory", categoryName, notShared.Count),
            };
            share.Click += (_, _) => ShareMarks(notShared, categoryName);
            items.Add(share);
        }
        if (shared.Count > 0)
        {
            var unshare = new MenuItem
            {
                Header = categoryName is null ? Loc.T("Ctx_Unshare") : Loc.F("Ctx_UnshareCategory", categoryName, shared.Count),
            };
            unshare.Click += (_, _) =>
            {
                client.UnshareMarks(shared.Select(m => m.Id));
                Log($"Přestal(a) jsi sdílet {MarkersPhrase(shared.Count)}. Kdo si je uložil, tomu zůstanou.");
            };
            items.Add(unshare);
        }
        return items;
    }

    private void ShareMarks(List<RelayProtocol.SharedMark> marks, string? categoryName)
    {
        if (_relayClient is not { } client) return;
        int room = RelayProtocol.MaxSharedMarksPerPlayer - client.MySharedMarkCount;
        if (marks.Count > room)
        {
            Log($"Sdílet můžeš nejvýš {RelayProtocol.MaxSharedMarksPerPlayer} markerů najednou - sdílím jen prvních {Math.Max(0, room)}.");
            marks = marks.Take(Math.Max(0, room)).ToList();
        }
        client.ShareMarks(marks);
        Log(categoryName is null
            ? $"Sdílíš marker \"{marks.FirstOrDefault()?.Name}\" s místností."
            : $"Sdílíš kategorii {categoryName} ({MarkersPhrase(marks.Count)}) s místností.");
    }

    private List<Control> BuildUnshareAllMenuItems()
    {
        if (_relayClient is not { MySharedMarkCount: > 0 } client) return [];
        var item = new MenuItem { Header = Loc.F("Ctx_UnshareAll", client.MySharedMarkCount) };
        item.Click += (_, _) =>
        {
            client.UnshareAllMarks();
            Log("Přestal(a) jsi sdílet všechny svoje markery. Kdo si je uložil, tomu zůstanou.");
        };
        return [item];
    }

    private static string MarkersPhrase(int count) => count switch
    {
        1 => "1 marker",
        >= 2 and <= 4 => $"{count} markery",
        _ => $"{count} markerů",
    };

    // ---- Someone else's shared markers ----

    /// <summary>Called for every change the relay client reports; bursts (a shared category is
    /// hundreds of changes) collapse into one refresh.</summary>
    private void RequestSharedMarksRefresh()
    {
        if (_sharedMarksRefreshPending) return;
        _sharedMarksRefreshPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _sharedMarksRefreshPending = false;
            RefreshSharedMarks();
        });
    }

    private void RefreshSharedMarks()
    {
        _sharedMarks = _relayClient?.GetSharedMarks() ?? new Dictionary<string, IReadOnlyList<RelayProtocol.SharedMark>>();
        // Forget choices about players who no longer share anything.
        foreach (string gone in _sharedOwnerVisible.Keys.Where(o => !_sharedMarks.ContainsKey(o)).ToList()) _sharedOwnerVisible.Remove(gone);
        foreach (var gone in _sharedMarkVisible.Keys.Where(k => !_sharedMarks.ContainsKey(k.Owner)).ToList()) _sharedMarkVisible.Remove(gone);
        _expandedSharedOwners.RemoveWhere(o => !_sharedMarks.ContainsKey(o));
        BuildSharedMarkViews();
        RebuildMarkerRows();
        RequestRedraw();
    }

    private void OnSharedMarksAnnounced((string Owner, int Count) announcement)
    {
        string text = $"{announcement.Owner} sdílí {MarkersPhrase(announcement.Count)} - zobrazíš je v panelu markerů (Sdílené v místnosti).";
        Log(text);
        AddChatSystemLine(text);
        ShowInGame($"[Mapa] {announcement.Owner} sdili {MarkersPhrase(announcement.Count)} (panel markeru na mape)");
    }

    /// <summary>The "Sdílené v místnosti" section: players (with their color) and, expanded, their
    /// markers on the current facet. Only while connected or while anything is shared.</summary>
    private void AddSharedSection(System.Collections.ObjectModel.ObservableCollection<MarkerRow> rows, string query)
    {
        if (_relayClient is null && _sharedMarks.Count == 0) return;
        bool searching = query.Length > 0;
        var sectionRows = new List<MarkerRow>();
        int total = 0;
        foreach (var (owner, marks) in _sharedMarks.OrderBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var onFacet = marks.Where(m => m.Map == _currentFacetIndex).ToList();
            if (onFacet.Count == 0) continue;
            if (searching && !MarkerTextMatches(owner, query))
                onFacet = onFacet.Where(m => MarkerTextMatches(m.Name, query)).ToList();
            if (onFacet.Count == 0) continue;
            total += onFacet.Count;

            bool expanded = searching || _expandedSharedOwners.Contains(owner);
            sectionRows.Add(new SharedOwnerRow(owner, OwnerBrush(owner), onFacet.Count, _sharedOwnerVisible.GetValueOrDefault(owner), expanded,
                OnSharedOwnerVisibilityChanged));
            if (!expanded) continue;
            foreach (var mark in onFacet.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
                sectionRows.Add(new SharedMarkRow(owner, mark, GetMarkerIcon(mark.Icon), IsSavedLocally(mark), IsSharedMarkChecked(owner, mark.Id),
                    OnSharedMarkVisibilityChanged));
        }
        if (searching && sectionRows.Count == 0) return;
        AddSection(rows, MarkerSection.Shared, Loc.T("Panel_SectionShared"), total, sectionRows, searching,
            _sharedMarksAllVisible, OnSharedSectionVisibilityChanged);
    }

    private Brush OwnerBrush(string owner) =>
        _remotePlayerViews.TryGetValue(owner, out var view) ? view.Label.Foreground : PlayerColors.ToBrush(null);

    private void OnSharedSectionVisibilityChanged(CheckableMarkerRow row)
    {
        _sharedMarksAllVisible = row.IsVisible;
        AfterMarkerVisibilityChanged();
    }

    /// <summary>A player's checkbox sets all their markers (single-marker choices are reset).</summary>
    private void OnSharedOwnerVisibilityChanged(CheckableMarkerRow row)
    {
        var ownerRow = (SharedOwnerRow)row;
        _sharedOwnerVisible[ownerRow.Owner] = row.IsVisible;
        foreach (var key in _sharedMarkVisible.Keys.Where(k => k.Owner == ownerRow.Owner).ToList()) _sharedMarkVisible.Remove(key);
        foreach (var markRow in _markerRows.OfType<SharedMarkRow>().Where(r => r.Owner == ownerRow.Owner)) markRow.SetVisibleQuietly(row.IsVisible);
        AfterMarkerVisibilityChanged();
    }

    private void OnSharedMarkVisibilityChanged(CheckableMarkerRow row)
    {
        var markRow = (SharedMarkRow)row;
        _sharedMarkVisible[(markRow.Owner, markRow.Mark.Id)] = row.IsVisible;
        AfterMarkerVisibilityChanged();
    }

    private void SharedMarkSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SharedMarkRow row) SaveSharedMarks(row.Owner, [row.Mark]);
    }

    private List<Control> BuildSaveSharedMenuItems(string owner, IReadOnlyList<RelayProtocol.SharedMark> marks, string header)
    {
        var unsaved = marks.Where(m => !IsSavedLocally(m)).ToList();
        if (unsaved.Count == 0) return [new MenuItem { Header = Loc.T("Ctx_AlreadySaved"), IsEnabled = false }];
        var item = new MenuItem { Header = marks.Count > 1 ? $"{header} ({unsaved.Count})" : header };
        item.Click += (_, _) => SaveSharedMarks(owner, unsaved);
        return [item];
    }

    /// <summary>Appends the markers to shared_markers.map in the markers folder (skipping any that
    /// already exist locally) and adds them to the loaded markers - they then live on as ordinary
    /// local markers under "Uložené od ostatních", whatever the owner does later.</summary>
    private void SaveSharedMarks(string owner, IReadOnlyList<RelayProtocol.SharedMark> marks)
    {
        string path = System.IO.Path.Combine(EnsureMarkersDirectory(), SharedMarkersFileName);
        int saved = 0;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            foreach (var mark in marks)
            {
                if (IsSavedLocally(mark)) continue;
                var entry = new MarkerEntry(mark.X, mark.Y, mark.Map, mark.Name, IconName: mark.Icon.Length > 0 ? IconSpelling(mark.Icon) : null);
                MarkerFileStore.Append(path, entry);
                _markers.Add(new LoadedMarker(entry, path));
                _localMarkerKeys.Add((mark.X, mark.Y, mark.Map, mark.Name));
                saved++;
            }
        }
        catch (Exception ex)
        {
            Log($"Uložení do {SharedMarkersFileName} selhalo: {ex.Message}");
        }
        if (saved > 0)
            AfterMarkersChanged($"Uloženo {MarkersPhrase(saved)} od {owner} do {SharedMarkersFileName} (Uložené od ostatních).");
    }

    private void GoToSharedMark(string owner, RelayProtocol.SharedMark mark)
    {
        _sharedMarkViews.TryGetValue((owner, mark.Id), out var view);
        GoToMapPoint(mark.X, mark.Y, mark.Map, $"{mark.Name} (od {owner})", view.Frame, $"\"{mark.Name}\" od {owner}");
    }

    // ---- Drawing them on the map: the marker's icon in a thin frame of the owner's color ----

    private void BuildSharedMarkViews()
    {
        foreach (var (frame, _) in _sharedMarkViews.Values) MarkersCanvas.Children.Remove(frame);
        _sharedMarkViews.Clear();
        foreach (var (owner, marks) in _sharedMarks)
        {
            Brush brush = OwnerBrush(owner);
            foreach (var mark in marks)
            {
                var bitmap = GetMarkerIcon(mark.Icon);
                var frame = new Border
                {
                    BorderBrush = brush,
                    BorderThickness = new Thickness(1),
                    Background = Brushes.Transparent,
                    Child = new Image
                    {
                        Source = bitmap,
                        Width = bitmap is { PixelWidth: > 0 } ? bitmap.PixelWidth : 16,
                        Height = bitmap is { PixelHeight: > 0 } ? bitmap.PixelHeight : 16,
                    },
                    Visibility = Visibility.Collapsed,
                };
                string label = $"{mark.Name} (od {owner})";
                var capturedMark = mark;
                frame.MouseEnter += (_, _) => ShowMarkerHoverLabel(label, frame);
                frame.MouseLeave += (_, _) => HideMarkerHoverLabel();
                frame.MouseRightButtonUp += (_, e) =>
                {
                    e.Handled = true; // not the map's own menu as well
                    ShowSharedMarkContextMenu(owner, capturedMark, frame);
                };
                MarkersCanvas.Children.Add(frame);
                _sharedMarkViews[(owner, mark.Id)] = (frame, mark);
            }
        }
    }

    private void ShowSharedMarkContextMenu(string owner, RelayProtocol.SharedMark mark, FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.MousePoint };
        foreach (var item in BuildSaveSharedMenuItems(owner, [mark], Loc.T("Ctx_SaveToSaved"))) menu.Items.Add(item);
        var hide = new MenuItem { Header = Loc.T("Ctx_HideMarker") };
        hide.Click += (_, _) =>
        {
            _sharedMarkVisible[(owner, mark.Id)] = false;
            RebuildMarkerRows();
            AfterMarkerVisibilityChanged();
        };
        var hideOwner = new MenuItem { Header = Loc.F("Ctx_HideAllFrom", owner) };
        hideOwner.Click += (_, _) =>
        {
            _sharedOwnerVisible[owner] = false;
            foreach (var key in _sharedMarkVisible.Keys.Where(k => k.Owner == owner).ToList()) _sharedMarkVisible.Remove(key);
            RebuildMarkerRows();
            AfterMarkerVisibilityChanged();
        };
        menu.Items.Add(new Separator());
        menu.Items.Add(hide);
        menu.Items.Add(hideOwner);
        menu.IsOpen = true;
    }

    /// <summary>Part of UpdateMarkersOverlay: positions the shared markers that are shown.</summary>
    private void UpdateSharedMarkViews(bool markersOn, DpiScale dpi, double centerX, double centerY)
    {
        foreach (var ((owner, _), (frame, mark)) in _sharedMarkViews)
        {
            if (!markersOn || mark.Map != _currentFacetIndex || !IsSharedMarkShown(owner, mark))
            {
                frame.Visibility = Visibility.Collapsed;
                continue;
            }
            var (sx, sy) = WorldToScreen(mark.X, mark.Y);
            var image = (Image)frame.Child;
            frame.Visibility = Visibility.Visible;
            Canvas.SetLeft(frame, centerX + sx / dpi.DpiScaleX - image.Width / 2 - 1);
            Canvas.SetTop(frame, centerY + sy / dpi.DpiScaleY - image.Height / 2 - 1);
        }
    }

    /// <summary>Disconnecting: others' shared markers are gone for us (saved copies stay).</summary>
    private void ClearSharedMarks()
    {
        _sharedMarks = new Dictionary<string, IReadOnlyList<RelayProtocol.SharedMark>>();
        _sharedOwnerVisible.Clear();
        _sharedMarkVisible.Clear();
        _expandedSharedOwners.Clear();
        _sharedMarksAllVisible = true;
        BuildSharedMarkViews();
        RebuildMarkerRows();
    }
}
