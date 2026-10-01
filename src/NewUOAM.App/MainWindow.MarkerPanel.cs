using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NewUOAM.MapData.Markers;
using NewUOAM.Positioning.Relay;

namespace NewUOAM.App;

/// <summary>One row of the side panel's marker list (MainWindow.MarkerList). Flat rows in a
/// virtualizing ListBox rather than a TreeView, so thousands of markers stay cheap.</summary>
public abstract class MarkerRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnChanged(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>A row with a show/hide checkbox (IsVisible, bound two-way) and an expand arrow.</summary>
public abstract class CheckableMarkerRow : MarkerRow
{
    private readonly Action<CheckableMarkerRow>? _visibilityChanged;
    private bool _isVisible;
    private bool _isExpanded;

    protected CheckableMarkerRow(bool isVisible, bool isExpanded, Action<CheckableMarkerRow>? visibilityChanged)
    {
        _isVisible = isVisible;
        _isExpanded = isExpanded;
        _visibilityChanged = visibilityChanged;
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnChanged(nameof(IsVisible));
            _visibilityChanged?.Invoke(this);
        }
    }

    /// <summary>Updates the checkbox without reporting it back as a user change (Vše/Nic, a
    /// player's checkbox changing their markers' ones).</summary>
    internal void SetVisibleQuietly(bool value)
    {
        if (_isVisible == value) return;
        _isVisible = value;
        OnChanged(nameof(IsVisible));
    }

    public Visibility CheckboxVisibility => _visibilityChanged is null ? Visibility.Collapsed : Visibility.Visible;
    public string Arrow => _isExpanded ? "▾" : "▸";

    internal bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            OnChanged(nameof(Arrow));
        }
    }
}

public enum MarkerSection { Own, Saved, Shared }

/// <summary>"MOJE MARKERY" / "ULOŽENÉ OD OSTATNÍCH" / "SDÍLENÉ V MÍSTNOSTI". Only the shared
/// section has a checkbox (show/hide everyone's shared markers at once).</summary>
public sealed class MarkerSectionRow : CheckableMarkerRow
{
    internal MarkerSectionRow(MarkerSection section, string title, int count, bool isExpanded, bool isVisible, Action<CheckableMarkerRow>? visibilityChanged)
        : base(isVisible, isExpanded, visibilityChanged)
    {
        Section = section;
        Title = title;
        Count = count;
    }

    public MarkerSection Section { get; }
    public string Title { get; }
    public int Count { get; }
}

public sealed class MarkerCategoryRow : CheckableMarkerRow
{
    internal MarkerCategoryRow(string key, string name, ImageSource? icon, int count, bool isVisible, bool isExpanded,
        Action<CheckableMarkerRow> visibilityChanged)
        : base(isVisible, isExpanded, visibilityChanged)
    {
        Key = key;
        Name = name;
        Icon = icon;
        Count = count;
    }

    public string Key { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }
    public int Count { get; }
}

public sealed class MarkerEntryRow : MarkerRow
{
    internal MarkerEntryRow(string name, int x, int y, ImageSource? icon, object source, bool isShared)
    {
        Name = name;
        Coordinates = $"{x},{y}";
        Icon = icon;
        Source = source;
        SharedBadgeVisibility = isShared ? Visibility.Visible : Visibility.Collapsed;
    }

    public string Name { get; }
    public string Coordinates { get; }
    public ImageSource? Icon { get; }
    /// <summary>"sdíleno" next to one of my markers I currently share with the room.</summary>
    public Visibility SharedBadgeVisibility { get; }
    internal object Source { get; }
}

/// <summary>A player in the room who shares markers.</summary>
public sealed class SharedOwnerRow : CheckableMarkerRow
{
    internal SharedOwnerRow(string owner, Brush brush, int count, bool isVisible, bool isExpanded, Action<CheckableMarkerRow> visibilityChanged)
        : base(isVisible, isExpanded, visibilityChanged)
    {
        Owner = owner;
        Brush = brush;
        Count = count;
    }

    public string Owner { get; }
    public Brush Brush { get; }
    public int Count { get; }
}

/// <summary>One marker someone shares: checkbox, name, coordinates and "Uložit" (or "uloženo"
/// once a local copy with the same position and name exists).</summary>
public sealed class SharedMarkRow : CheckableMarkerRow
{
    internal SharedMarkRow(string owner, RelayProtocol.SharedMark mark, ImageSource? icon, bool isSaved, bool isVisible,
        Action<CheckableMarkerRow> visibilityChanged)
        : base(isVisible, isExpanded: false, visibilityChanged)
    {
        Owner = owner;
        Mark = mark;
        Icon = icon;
        IsSaved = isSaved;
    }

    public string Owner { get; }
    internal RelayProtocol.SharedMark Mark { get; }
    public string Name => Mark.Name;
    public string Coordinates => $"{Mark.X},{Mark.Y}";
    public ImageSource? Icon { get; }
    internal bool IsSaved { get; }
    public Visibility SaveButtonVisibility => IsSaved ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SavedTextVisibility => IsSaved ? Visibility.Visible : Visibility.Collapsed;
}

// ---- Side panel marker browser (user's request 2026-09-28, modeled on a colleague's "Moria
// Reborn Atlas"; sections added 2026-09-29 with marker sharing). Three sections:
//  - Moje markery: every marker file except shared_markers.map, by category;
//  - Uložené od ostatních: shared_markers.map (markers saved from someone's share), by category;
//  - Sdílené v místnosti: what other players currently share, by player (MainWindow.SharedMarks.cs).
// A category is the marker's icon type ("mage", "bank"...), the grouping old UOAM's own +/- flags
// use. Its default visibility comes from its file(s) (visible if any of its markers is "+"); a
// checkbox click overrides it and is remembered (AppSettings.MarkerCategories). The list shows the
// facet currently on screen. ----
public partial class MainWindow
{
    private sealed class MarkerCategory
    {
        public required string Key;
        public required string Name;
        public bool NameFromAllCaps;
        public required MarkerSection Section;
        public ImageSource? Icon;
        public readonly List<LoadedMarker> Markers = new();
        public bool DefaultVisible;
        public bool Expanded;
    }

    private const double GoToMarkerLabelSeconds = 3.0;
    /// <summary>Where "Uložit" puts markers from someone's share - the same name on every PC, and
    /// its own section in the panel so they don't mix with your own markers.</summary>
    private const string SharedMarkersFileName = "shared_markers.map";
    private const string SavedCategoryKeyPrefix = "S:";

    private readonly Dictionary<string, MarkerCategory> _markerCategories = new();
    // The user's own choices per category key; categories not in here use their file default.
    private Dictionary<string, bool> _markerCategoryVisible = new();
    private readonly HashSet<MarkerSection> _collapsedMarkerSections = new();
    private ObservableCollection<MarkerRow> _markerRows = new();
    private int _markerRowsFacet = -1;
    private DispatcherTimer? _goToLabelTimer;
    // (X, Y, Map, Name) of every local marker - a shared marker with a local twin counts as saved.
    private readonly HashSet<(int, int, int, string)> _localMarkerKeys = new();

    private static bool IsSavedSharedFile(string path) =>
        string.Equals(System.IO.Path.GetFileName(path), SharedMarkersFileName, StringComparison.OrdinalIgnoreCase);

    private static string MarkerCategoryKey(LoadedMarker marker)
    {
        string key = NormalizeIconName(marker.Entry.IconName ?? "");
        if (key.Length == 0) key = FallbackMarkerIconName;
        return IsSavedSharedFile(marker.FilePath) ? SavedCategoryKeyPrefix + key : key;
    }

    private bool IsMarkerCategoryVisible(string key) =>
        _markerCategoryVisible.TryGetValue(key, out bool chosen) ? chosen
        : !_markerCategories.TryGetValue(key, out var category) || category.DefaultVisible;

    private bool IsMarkerVisible(LoadedMarker marker) => IsMarkerCategoryVisible(MarkerCategoryKey(marker));

    private bool IsSavedLocally(RelayProtocol.SharedMark mark) => _localMarkerKeys.Contains((mark.X, mark.Y, mark.Map, mark.Name));

    /// <summary>Regroups _markers into categories (after loading or editing markers), keeping
    /// which categories were expanded.</summary>
    private void RebuildMarkerCategories()
    {
        var expanded = _markerCategories.Values.Where(c => c.Expanded).Select(c => c.Key).ToHashSet();
        _markerCategories.Clear();
        _localMarkerKeys.Clear();
        foreach (var marker in _markers)
        {
            _localMarkerKeys.Add((marker.Entry.X, marker.Entry.Y, marker.Entry.MapIndex, marker.Entry.Name));
            string key = MarkerCategoryKey(marker);
            string name = CategoryDisplayName(marker.Entry.IconName);
            if (_markerCategories.TryGetValue(key, out var category))
            {
                // Files spell a type loosely; prefer a spelling that isn't all capitals ("TOWN"
                // from a label made before 2026-09-29 vs. "town" elsewhere).
                if (category.NameFromAllCaps && !IsAllCaps(marker.Entry.IconName ?? ""))
                {
                    category.Name = name;
                    category.NameFromAllCaps = false;
                }
            }
            else
            {
                category = new MarkerCategory
                {
                    Key = key,
                    Name = name,
                    NameFromAllCaps = IsAllCaps(marker.Entry.IconName ?? ""),
                    Section = IsSavedSharedFile(marker.FilePath) ? MarkerSection.Saved : MarkerSection.Own,
                    Icon = GetMarkerIcon(marker.Entry.IconName),
                    Expanded = expanded.Contains(key),
                };
                _markerCategories[key] = category;
            }
            category.Markers.Add(marker);
            category.DefaultVisible |= marker.Entry.Visible;
        }
        RebuildMarkerRows();
    }

    /// <summary>"town" / "TOWN" -> "Town"; no type -> "Ostatní".</summary>
    private static string CategoryDisplayName(string? iconName)
    {
        string name = string.IsNullOrWhiteSpace(iconName) ? "Ostatní" : WithoutAllCaps(iconName.Trim());
        return char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..];
    }

    /// <summary>Rebuilds the list for the current facet and search text, keeping the scroll
    /// position unless the search changed.</summary>
    private void RebuildMarkerRows(bool keepScroll = true)
    {
        string query = MarkerSearchTextBox.Text.Trim();
        bool searching = query.Length > 0;
        int facet = _currentFacetIndex;
        _markerRowsFacet = facet;

        var rows = new ObservableCollection<MarkerRow>();
        int otherFacets = 0;
        foreach (var section in new[] { MarkerSection.Own, MarkerSection.Saved })
        {
            var sectionRows = new List<MarkerRow>();
            int sectionCount = 0;
            foreach (var category in _markerCategories.Values.Where(c => c.Section == section)
                         .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var onFacet = category.Markers.Where(m => m.Entry.MapIndex == facet).ToList();
                otherFacets += category.Markers.Count - onFacet.Count;
                if (onFacet.Count == 0) continue;

                var matches = onFacet;
                if (searching)
                {
                    // The category's own name matching shows all of it; otherwise just the
                    // markers whose name matches.
                    if (!MarkerTextMatches(category.Name, query))
                        matches = onFacet.Where(m => MarkerTextMatches(m.Entry.Name, query)).ToList();
                    if (matches.Count == 0) continue;
                }
                sectionCount += matches.Count;

                bool expanded = category.Expanded || searching;
                sectionRows.Add(new MarkerCategoryRow(category.Key, category.Name, category.Icon, matches.Count,
                    IsMarkerCategoryVisible(category.Key), expanded, OnMarkerCategoryVisibilityChanged));
                if (expanded) sectionRows.AddRange(EntryRows(matches));
            }
            // "Uložené od ostatních" only appears once something was saved; while searching, a
            // section without matches is left out.
            if ((section == MarkerSection.Saved || searching) && sectionRows.Count == 0) continue;
            AddSection(rows, section, Loc.T(section == MarkerSection.Own ? "Panel_SectionOwn" : "Panel_SectionSaved"), sectionCount, sectionRows, searching);
        }
        AddSharedSection(rows, query);

        _markerRows = rows;
        double offset = keepScroll ? MarkerListScrollViewer()?.VerticalOffset ?? 0 : 0;
        MarkerList.ItemsSource = rows;
        if (offset > 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => MarkerListScrollViewer()?.ScrollToVerticalOffset(offset));

        bool anyMarkerRows = rows.Any(r => r is not MarkerSectionRow);
        MarkerPanelHint.Text =
            _markers.Count == 0 && !anyMarkerRows ? Loc.T("Panel_NothingLoaded")
            : !anyMarkerRows && searching ? Loc.T("Panel_NothingFound")
            : !anyMarkerRows ? Loc.F("Panel_NoneOnFacet", otherFacets)
            : "";
        MarkerPanelHint.Visibility = MarkerPanelHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMarkerCount();
    }

    /// <summary>A section header plus, unless collapsed (searching expands everything), its rows.</summary>
    private void AddSection(ObservableCollection<MarkerRow> rows, MarkerSection section, string title, int count,
        List<MarkerRow> sectionRows, bool searching, bool? isVisible = null, Action<CheckableMarkerRow>? visibilityChanged = null)
    {
        bool expanded = searching || !_collapsedMarkerSections.Contains(section);
        rows.Add(new MarkerSectionRow(section, title, count, expanded, isVisible ?? true, visibilityChanged));
        if (expanded)
            foreach (var row in sectionRows) rows.Add(row);
    }

    private ScrollViewer? MarkerListScrollViewer()
    {
        DependencyObject? current = MarkerList;
        while (current is not null && current is not ScrollViewer)
            current = VisualTreeHelper.GetChildrenCount(current) > 0 ? VisualTreeHelper.GetChild(current, 0) : null;
        return current as ScrollViewer;
    }

    private IEnumerable<MarkerEntryRow> EntryRows(IEnumerable<LoadedMarker> markers) =>
        markers
            .OrderBy(m => m.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(m => new MarkerEntryRow(m.Entry.Name, m.Entry.X, m.Entry.Y, GetMarkerIcon(m.Entry.IconName), m, IsSharedByMe(m)));

    /// <summary>Case- and diacritics-insensitive ("mesto" finds "Město").</summary>
    private static bool MarkerTextMatches(string text, string query) =>
        CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    /// <summary>"N zobrazeno": markers of the current facet actually shown on the map.</summary>
    private void UpdateMarkerCount()
    {
        if (_markers.Count == 0 && _sharedMarks.Count == 0)
        {
            MarkerCountText.Text = "";
            return;
        }
        if (ShowMarkersCheckBox.IsChecked != true)
        {
            MarkerCountText.Text = Loc.T("Panel_CountOff");
            return;
        }
        int shown = _markers.Count(m => m.Entry.MapIndex == _currentFacetIndex && IsMarkerVisible(m))
                    + _sharedMarks.Sum(kv => kv.Value.Count(m => m.Map == _currentFacetIndex && IsSharedMarkShown(kv.Key, m)));
        MarkerCountText.Text = Loc.F("Panel_CountShown", shown);
    }

    private void OnMarkerCategoryVisibilityChanged(CheckableMarkerRow row)
    {
        _markerCategoryVisible[((MarkerCategoryRow)row).Key] = row.IsVisible;
        AfterMarkerVisibilityChanged();
    }

    private void MarkerShowAllButton_Click(object sender, RoutedEventArgs e) => SetAllMarkerCategories(true);
    private void MarkerHideAllButton_Click(object sender, RoutedEventArgs e) => SetAllMarkerCategories(false);

    /// <summary>Vše/Nic: your own and saved categories. Shared markers have their own switches
    /// (the "Sdílené v místnosti" checkbox), so a "Vše" can't flood the map with them.</summary>
    private void SetAllMarkerCategories(bool visible)
    {
        foreach (string key in _markerCategories.Keys) _markerCategoryVisible[key] = visible;
        foreach (var row in _markerRows.OfType<MarkerCategoryRow>()) row.SetVisibleQuietly(visible);
        AfterMarkerVisibilityChanged();
    }

    private void AfterMarkerVisibilityChanged()
    {
        UpdateMarkerCount();
        SaveSettings();
        RequestRedraw();
    }

    private void MarkerSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        MarkerSearchPlaceholder.Visibility = MarkerSearchTextBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildMarkerRows(keepScroll: false);
    }

    /// <summary>A section/category/player row expands or collapses, a marker row moves the map
    /// to that marker. Clicks on a checkbox or button inside a row are left to that control.</summary>
    private void MarkerListItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideRowControl(e.OriginalSource as DependencyObject)) return;
        switch ((sender as ListBoxItem)?.DataContext)
        {
            case MarkerSectionRow section:
                if (!_collapsedMarkerSections.Remove(section.Section)) _collapsedMarkerSections.Add(section.Section);
                RebuildMarkerRows();
                break;
            case MarkerCategoryRow category:
                ToggleMarkerCategory(category);
                break;
            case SharedOwnerRow owner:
                if (!_expandedSharedOwners.Remove(owner.Owner)) _expandedSharedOwners.Add(owner.Owner);
                RebuildMarkerRows();
                break;
            case MarkerEntryRow { Source: LoadedMarker marker }:
                GoToMarker(marker);
                break;
            case SharedMarkRow shared:
                GoToSharedMark(shared.Owner, shared.Mark);
                break;
        }
    }

    /// <summary>Right-click on a row: share/unshare your markers, save someone's.</summary>
    private void MarkerListItem_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBoxItem)?.DataContext is not { } row) return;
        if (row is MarkerEntryRow { Source: LoadedMarker clicked })
        {
            // The same menu as the marker's icon on the map (drop marker, share, move, Edit/Delete);
            // the map stays where it is.
            ShowMarkerContextMenu(clicked, (UIElement)sender);
            e.Handled = true;
            return;
        }
        var items = row switch
        {
            MarkerSectionRow { Section: MarkerSection.Own } => BuildUnshareAllMenuItems(),
            MarkerSectionRow { Section: MarkerSection.Saved } =>
                BuildMoveToOwnMenuItems(_markers.Where(m => IsSavedSharedFile(m.FilePath)).ToList(), Loc.T("Ctx_MoveAllToOwn")),
            MarkerCategoryRow category => WithSeparator(
                BuildShareMenuItems(CategoryMarkersOnFacet(category.Key), category.Name),
                BuildMoveToOwnMenuItems(_markerCategories.TryGetValue(category.Key, out var c) ? c.Markers : [],
                    Loc.F("Ctx_MoveCategoryToOwn", category.Name))),
            SharedOwnerRow owner => BuildSaveSharedMenuItems(owner.Owner, _sharedMarks.GetValueOrDefault(owner.Owner) ?? [], Loc.F("Ctx_SaveAllFrom", owner.Owner)),
            SharedMarkRow shared => BuildSaveSharedMenuItems(shared.Owner, [shared.Mark], Loc.T("Ctx_SaveToSaved")),
            _ => [],
        };
        if (items.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.MousePoint };
        foreach (var item in items) menu.Items.Add(item);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private List<LoadedMarker> CategoryMarkersOnFacet(string key) =>
        _markerCategories.TryGetValue(key, out var category)
            ? category.Markers.Where(m => m.Entry.MapIndex == _markerRowsFacet).ToList()
            : [];

    private static bool IsInsideRowControl(DependencyObject? element)
    {
        for (var current = element; current is not null and not ListBoxItem; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ButtonBase) return true; // CheckBox and Button both
        return false;
    }

    /// <summary>Inserts/removes the category's marker rows in place, so the list doesn't jump.
    /// While searching everything is expanded.</summary>
    private void ToggleMarkerCategory(MarkerCategoryRow row)
    {
        if (MarkerSearchTextBox.Text.Trim().Length > 0 || !_markerCategories.TryGetValue(row.Key, out var category)) return;

        category.Expanded = !category.Expanded;
        row.IsExpanded = category.Expanded;
        int index = _markerRows.IndexOf(row) + 1;
        if (category.Expanded)
        {
            foreach (var entry in EntryRows(category.Markers.Where(m => m.Entry.MapIndex == _markerRowsFacet)))
                _markerRows.Insert(index++, entry);
        }
        else
        {
            while (index < _markerRows.Count && _markerRows[index] is MarkerEntryRow)
                _markerRows.RemoveAt(index);
        }
    }

    /// <summary>Flies the map to the marker and shows its name for a moment. Turns Track Player
    /// off first - otherwise the next step of your character would pull the view straight back.</summary>
    private void GoToMarker(LoadedMarker marker)
    {
        var view = _markerViews.FirstOrDefault(v => ReferenceEquals(v.Marker, marker));
        GoToMapPoint(marker.Entry.X, marker.Entry.Y, marker.Entry.MapIndex, marker.Entry.Name, view.Icon, $"\"{marker.Entry.Name}\"");
    }

    /// <param name="anchor">The marker's element on the map (its label is shown next to it once
    /// the flight lands), or null if it has none.</param>
    private void GoToMapPoint(int x, int y, int map, string label, FrameworkElement? anchor, string what)
    {
        if (_currentFacet is null) return;
        if (map != _currentFacetIndex)
        {
            Log($"Marker {what} je na jiné mapě.");
            return;
        }
        if (_trackPlayer) SetTrackPlayer(false);

        FlyTo(x, y, () =>
        {
            if (anchor is { Visibility: Visibility.Visible })
            {
                ShowMarkerHoverLabel(label, anchor);
                _goToLabelTimer?.Stop();
                _goToLabelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(GoToMarkerLabelSeconds) };
                _goToLabelTimer.Tick += (_, _) =>
                {
                    _goToLabelTimer?.Stop();
                    if (ReferenceEquals(_hoveredMarkerIcon, anchor) && !anchor.IsMouseOver) HideMarkerHoverLabel();
                };
                _goToLabelTimer.Start();
            }
            else
            {
                Log(ShowMarkersCheckBox.IsChecked != true
                    ? $"Mapa je na markeru {what}, markery jsou ale vypnuté (Mapa → Nastavení)."
                    : $"Mapa je na markeru {what}, je ale skrytý (zapni ho v panelu markerů).");
            }
        });
    }

    private static List<Control> WithSeparator(List<Control> first, List<Control> second) =>
        first.Count == 0 ? second : second.Count == 0 ? first : [.. first, new Separator(), .. second];

    // ---- Merging "Uložené od ostatních" into your own markers (user's request 2026-09-29) ----

    /// <summary>"Přesunout do Moje markery ▸ &lt;file&gt;" for those of <paramref name="markers"/> that
    /// are saved from others (shared_markers.map). Empty if there are none.</summary>
    private List<Control> BuildMoveToOwnMenuItems(IReadOnlyCollection<LoadedMarker> markers, string header)
    {
        var saved = markers.Where(m => IsSavedSharedFile(m.FilePath)).ToList();
        if (saved.Count == 0) return [];
        string dir = System.IO.Path.GetDirectoryName(saved[0].FilePath)!;
        var parent = new MenuItem { Header = saved.Count > 1 ? $"{header} ({saved.Count})" : header };
        foreach (var file in MarkerFilesIn(dir, System.IO.Path.Combine(dir, CustomLabelsFileName)).Where(f => !IsSavedSharedFile(f.Path)))
        {
            var item = new MenuItem { Header = new TextBlock { Text = file.Display } }; // a TextBlock: '_' in a file name isn't an access key
            item.Click += (_, _) => MoveToOwnMarkers(saved, file.Path);
            parent.Items.Add(item);
        }
        return [parent];
    }

    /// <summary>Moves markers from shared_markers.map into one of your files: appended there first
    /// (so a failure can't lose one), then removed from shared_markers.map. A marker you already
    /// have in your own files (same position, map and name) is only removed. Its type is written
    /// the way your files spell it (<see cref="IconSpelling"/>).</summary>
    private void MoveToOwnMarkers(IReadOnlyList<LoadedMarker> markers, string targetPath)
    {
        var ownKeys = _markers.Where(m => !IsSavedSharedFile(m.FilePath))
            .Select(m => (m.Entry.X, m.Entry.Y, m.Entry.MapIndex, m.Entry.Name)).ToHashSet();
        string target = System.IO.Path.GetFileName(targetPath);
        int moved = 0, duplicates = 0, notFound = 0;
        string? error = null;
        try
        {
            foreach (var marker in markers)
            {
                var e = marker.Entry;
                LoadedMarker? copy = null;
                if (ownKeys.Add((e.X, e.Y, e.MapIndex, e.Name)))
                {
                    var entry = e with { IconName = string.IsNullOrWhiteSpace(e.IconName) ? e.IconName : IconSpelling(e.IconName) };
                    MarkerFileStore.Append(targetPath, entry);
                    copy = new LoadedMarker(entry, targetPath);
                    moved++;
                }
                else duplicates++;

                if (!MarkerFileStore.Replace(marker.FilePath, e, null)) notFound++;
                int index = _markers.IndexOf(marker);
                if (index >= 0 && copy is not null) _markers[index] = copy;
                else if (index >= 0) _markers.RemoveAt(index);
                else if (copy is not null) _markers.Add(copy);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        if (moved + duplicates == 0 && error is not null)
        {
            Log($"Přesun do {target} selhal: {error}");
            return;
        }

        string message = $"Přesunuto {MarkersPhrase(moved)} do {target}"
                         + (duplicates > 0 ? $", {duplicates} už jsi měl(a), ty jsem jen odebral(a) z {SharedMarkersFileName}" : "")
                         + ".";
        if (notFound > 0) message += $" Pozor: {notFound} jsem v {SharedMarkersFileName} nenašel - soubor se mezitím změnil?";
        if (error is not null) message += $" Zbytek selhal: {error}";
        AfterMarkersChanged(message);
    }
}
