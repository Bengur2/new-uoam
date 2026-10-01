using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NewUOAM.App;

// Your own marker categories (user's request 2026-10-01), stored in MarkerCategoryStore next to the
// marker files. Which category a marker is in:
//  1. the one it was put in by hand (an assignment),
//  2. else your category that holds its icon type ("Treasures" = TREASURE_LEVEL1..8),
//  3. else its icon type, as before.
// Category keys: an icon type's is the normalized icon name ("SHRINE"), yours is "C:" + the name
// in capitals; shared_markers.map adds "S:" in front of either (MarkerCategoryKey).
public partial class MainWindow
{
    private const string CustomCategoryKeyPrefix = "C:";

    private MarkerCategoryStore _categoryStore = new();
    // The folder the categories file belongs to (the markers folder they were loaded from).
    private string? _categoryStoreDir;
    // Lookups rebuilt from _categoryStore by IndexCategoryStore.
    private readonly Dictionary<(string File, int X, int Y, int Map, string Name), MarkerCategoryStore.Assignment> _assignmentIndex = new();
    private readonly Dictionary<string, MarkerCategoryStore.Category> _categoryByIcon = new();

    private static string CustomCategoryKey(string name) => CustomCategoryKeyPrefix + name.Trim().ToUpperInvariant();

    private static string IconCategoryKey(string? iconName)
    {
        string key = NormalizeIconName(iconName ?? "");
        return key.Length == 0 ? FallbackMarkerIconName : key;
    }

    private static (string File, int X, int Y, int Map, string Name) AssignmentKey(LoadedMarker m) =>
        (System.IO.Path.GetFileName(m.FilePath).ToUpperInvariant(), m.Entry.X, m.Entry.Y, m.Entry.MapIndex, m.Entry.Name);

    private void IndexCategoryStore()
    {
        _assignmentIndex.Clear();
        foreach (var a in _categoryStore.Markers)
            _assignmentIndex[(a.File.ToUpperInvariant(), a.X, a.Y, a.Map, a.Name)] = a;
        _categoryByIcon.Clear();
        foreach (var c in _categoryStore.Categories)
            foreach (string icon in c.Icons)
                _categoryByIcon.TryAdd(NormalizeIconName(icon), c);
    }

    /// <summary>Called with the markers: reads the categories file of the folder they came from.</summary>
    private void LoadCategoryStore(string markersDirectory)
    {
        _categoryStore = MarkerCategoryStore.Load(markersDirectory, out string? error);
        _categoryStoreDir = markersDirectory;
        IndexCategoryStore();
        if (error is not null) Log($"{MarkerCategoryStore.FileName} se nepodařilo přečíst ({error}). Vlastní kategorie teď nefungují a soubor nepřepíšu.");
    }

    /// <summary>The category a marker is in, with its name and the icon its row shows.</summary>
    private (string Key, string Name, bool NameAllCaps, ImageSource? Icon, string? CustomName) DescribeMarkerCategory(LoadedMarker marker)
    {
        var e = marker.Entry;
        if (_assignmentIndex.TryGetValue(AssignmentKey(marker), out var a))
        {
            if (_categoryStore.FindCategory(a.Category) is { } chosen) return Custom(chosen);
            return (IconCategoryKey(a.Category), CategoryDisplayName(a.Category), IsAllCaps(a.Category), GetMarkerIcon(a.Category), null);
        }
        if (_categoryByIcon.TryGetValue(IconCategoryKey(e.IconName), out var byIcon)) return Custom(byIcon);
        return (IconCategoryKey(e.IconName), CategoryDisplayName(e.IconName), IsAllCaps(e.IconName ?? ""), GetMarkerIcon(e.IconName), null);

        (string, string, bool, ImageSource?, string?) Custom(MarkerCategoryStore.Category c) =>
            (CustomCategoryKey(c.Name), c.Name, false, GetMarkerIcon(c.Icons.Count > 0 ? c.Icons[0] : e.IconName), c.Name);
    }

    private string MarkerCategoryKey(LoadedMarker marker) =>
        (IsSavedSharedFile(marker.FilePath) ? SavedCategoryKeyPrefix : "") + DescribeMarkerCategory(marker).Key;

    /// <summary>Writes the categories file and regroups the panel. False (and logged) on failure;
    /// the in-memory change stays until the next marker load.</summary>
    private bool AfterCategoriesChanged()
    {
        // A category with no icon types and no marker put in it has no row in the panel, so it
        // couldn't be deleted - it goes by itself (its last marker deleted or moved out).
        _categoryStore.Categories.RemoveAll(c => c.Icons.Count == 0 &&
            !_categoryStore.Markers.Any(a => string.Equals(a.Category, c.Name, StringComparison.CurrentCultureIgnoreCase)));
        IndexCategoryStore();
        string dir = _categoryStoreDir ?? EnsureMarkersDirectory();
        bool saved = true;
        try
        {
            _categoryStore.Save(dir);
            _categoryStoreDir = dir;
        }
        catch (Exception ex)
        {
            Log($"Kategorie se nepodařilo uložit: {ex.Message}");
            saved = false;
        }
        RebuildMarkerCategories();
        SaveSettings();
        RequestRedraw(); // a marker's visibility follows its category's
        return saved;
    }

    /// <summary>A marker or icon moving into a category that has no show/hide choice yet takes the
    /// visibility it had, so markers don't appear or vanish just by being regrouped.</summary>
    private void KeepVisibility(string fromKey, string toKey)
    {
        foreach (string prefix in new[] { "", SavedCategoryKeyPrefix })
            if (!_markerCategoryVisible.ContainsKey(prefix + toKey))
                _markerCategoryVisible[prefix + toKey] = IsMarkerCategoryVisible(prefix + fromKey);
    }

    // ---- One marker ----

    /// <summary>Puts a marker in a category by name (yours or an icon type's), or back to where its
    /// icon puts it (null). A choice equal to the icon's own category isn't stored.</summary>
    private void SetMarkerCategory(LoadedMarker marker, string? categoryName)
    {
        string fromKey = DescribeMarkerCategory(marker).Key;
        RemoveAssignment(marker);
        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            _categoryStore.Markers.Add(new MarkerCategoryStore.Assignment
            {
                File = System.IO.Path.GetFileName(marker.FilePath),
                X = marker.Entry.X, Y = marker.Entry.Y, Map = marker.Entry.MapIndex, Name = marker.Entry.Name,
                Category = _categoryStore.FindCategory(categoryName)?.Name ?? categoryName.Trim(),
            });
            IndexCategoryStore();
            if (DescribeMarkerCategoryIgnoringAssignment(marker) == DescribeMarkerCategory(marker).Key) RemoveAssignment(marker);
        }
        IndexCategoryStore();
        KeepVisibility(fromKey, DescribeMarkerCategory(marker).Key);
        AfterCategoriesChanged();
    }

    private string DescribeMarkerCategoryIgnoringAssignment(LoadedMarker marker)
    {
        string icon = IconCategoryKey(marker.Entry.IconName);
        return _categoryByIcon.TryGetValue(icon, out var c) ? CustomCategoryKey(c.Name) : icon;
    }

    private void RemoveAssignment(LoadedMarker marker)
    {
        var key = AssignmentKey(marker);
        _categoryStore.Markers.RemoveAll(a => (a.File.ToUpperInvariant(), a.X, a.Y, a.Map, a.Name) == key);
    }

    /// <summary>After a marker was edited or moved to another file in this app: its hand-picked
    /// category goes with it. <paramref name="newCategory"/> comes from the Edit Label dialog:
    /// null = left as it was, "" = by icon, else one of your categories (created if new).</summary>
    private void CarryMarkerCategory(LoadedMarker before, LoadedMarker after, string? newCategory)
    {
        var old = _assignmentIndex.GetValueOrDefault(AssignmentKey(before));
        string? target = newCategory ?? old?.Category;
        RemoveAssignment(before);
        if (!string.IsNullOrWhiteSpace(newCategory) && _categoryStore.FindCategory(newCategory) is null)
            _categoryStore.Categories.Add(new MarkerCategoryStore.Category { Name = newCategory.Trim() });
        IndexCategoryStore();
        if (!string.IsNullOrWhiteSpace(target)) SetMarkerCategory(after, target); // saves
        else if (old is not null) AfterCategoriesChanged();
    }

    /// <summary>After a marker was deleted.</summary>
    private void ForgetMarkerCategory(LoadedMarker marker)
    {
        if (!_assignmentIndex.ContainsKey(AssignmentKey(marker))) return;
        RemoveAssignment(marker);
        AfterCategoriesChanged();
    }

    /// <summary>"Kategorie ▸" in a marker's menu: by icon, your categories, the icon types' ones,
    /// a new one.</summary>
    private MenuItem BuildMarkerCategoryMenu(LoadedMarker marker)
    {
        string current = DescribeMarkerCategory(marker).Key;
        bool assigned = _assignmentIndex.ContainsKey(AssignmentKey(marker));
        var menu = new MenuItem { Header = Loc.T("Ctx_Category") };

        string byIconKey = DescribeMarkerCategoryIgnoringAssignment(marker);
        string byIconName = _categoryByIcon.TryGetValue(IconCategoryKey(marker.Entry.IconName), out var c) ? c.Name : CategoryDisplayName(marker.Entry.IconName);
        var byIcon = Item(Loc.F("Ctx_CategoryByIcon", byIconName), !assigned);
        byIcon.Click += (_, _) => SetMarkerCategory(marker, null);
        menu.Items.Add(byIcon);

        if (_categoryStore.Categories.Count > 0) menu.Items.Add(new Separator());
        foreach (var category in _categoryStore.Categories.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            string key = CustomCategoryKey(category.Name);
            var item = Item(category.Name, assigned && current == key);
            item.Click += (_, _) => SetMarkerCategory(marker, category.Name);
            menu.Items.Add(item);
        }

        // The icon types' categories your markers have, for e.g. a cave marked with a shrine icon.
        var iconCategories = _markers.Where(m => !IsSavedSharedFile(m.FilePath))
            .Select(m => (Key: IconCategoryKey(m.Entry.IconName), Name: CategoryDisplayName(m.Entry.IconName), m.Entry.IconName))
            .GroupBy(t => t.Key).Select(g => g.First())
            .Where(t => t.Key != byIconKey)
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (iconCategories.Count > 0)
        {
            var sub = new MenuItem { Header = Loc.T("Ctx_CategoryIconTypes") };
            foreach (var t in iconCategories)
            {
                var item = Item(t.Name, assigned && current == t.Key);
                item.Icon = new Image { Source = GetMarkerIcon(t.IconName), Width = 16, Height = 16 };
                item.Click += (_, _) => SetMarkerCategory(marker, t.IconName);
                sub.Items.Add(item);
            }
            menu.Items.Add(sub);
        }

        menu.Items.Add(new Separator());
        var create = new MenuItem { Header = Loc.T("Ctx_CategoryNew") };
        create.Click += (_, _) =>
        {
            if (PromptNewCategoryName() is not { } name) return;
            _categoryStore.Categories.Add(new MarkerCategoryStore.Category { Name = name });
            SetMarkerCategory(marker, name);
        };
        menu.Items.Add(create);
        return menu;
    }

    // ---- A category row ----

    /// <summary>Right-click on a category row: an icon type's category can be put in one of yours;
    /// yours can be renamed, deleted, and have icon types taken out.</summary>
    private List<Control> BuildCategoryRowMenuItems(MarkerCategoryRow row)
    {
        if (!_markerCategories.TryGetValue(row.Key, out var category)) return [];
        if (category.CustomName is { } customName && _categoryStore.FindCategory(customName) is { } custom)
        {
            var rename = new MenuItem { Header = Loc.T("Ctx_CategoryRename") };
            rename.Click += (_, _) => RenameCategory(custom);
            var delete = new MenuItem { Header = Loc.T("Ctx_CategoryDelete") };
            delete.Click += (_, _) => DeleteCategory(custom);
            var items = new List<Control> { rename, delete };
            if (custom.Icons.Count > 0)
            {
                var icons = new MenuItem { Header = Loc.T("Ctx_CategoryIcons") };
                foreach (string icon in custom.Icons.ToList())
                {
                    var item = Item(CategoryDisplayName(icon), true);
                    item.Icon = new Image { Source = GetMarkerIcon(icon), Width = 16, Height = 16 };
                    item.ToolTip = Loc.T("Ctx_CategoryIconRemoveTip");
                    item.Click += (_, _) =>
                    {
                        custom.Icons.Remove(icon);
                        IndexCategoryStore();
                        KeepVisibility(CustomCategoryKey(custom.Name), IconCategoryKey(icon));
                        AfterCategoriesChanged();
                    };
                    icons.Items.Add(item);
                }
                items.Add(icons);
            }
            return items;
        }

        // An icon type's category: put the icon type in one of yours.
        string iconKey = row.Key.StartsWith(SavedCategoryKeyPrefix, StringComparison.Ordinal) ? row.Key[SavedCategoryKeyPrefix.Length..] : row.Key;
        var put = new MenuItem { Header = new TextBlock { Text = Loc.F("Ctx_IconToCategory", category.Name) } }; // "_" in an icon name isn't an access key
        foreach (var target in _categoryStore.Categories.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new MenuItem { Header = new TextBlock { Text = target.Name } };
            item.Click += (_, _) => AddIconToCategory(iconKey, target);
            put.Items.Add(item);
        }
        if (put.Items.Count > 0) put.Items.Add(new Separator());
        var create = new MenuItem { Header = Loc.T("Ctx_CategoryNew") };
        create.Click += (_, _) =>
        {
            if (PromptNewCategoryName() is not { } name) return;
            var target = new MarkerCategoryStore.Category { Name = name };
            _categoryStore.Categories.Add(target);
            AddIconToCategory(iconKey, target);
        };
        put.Items.Add(create);
        return [put];
    }

    private void AddIconToCategory(string iconKey, MarkerCategoryStore.Category target)
    {
        // An icon type belongs to one of your categories at a time.
        foreach (var c in _categoryStore.Categories) c.Icons.RemoveAll(i => NormalizeIconName(i) == iconKey);
        target.Icons.Add(iconKey);
        IndexCategoryStore();
        KeepVisibility(iconKey, CustomCategoryKey(target.Name));
        AfterCategoriesChanged();
    }

    private void RenameCategory(MarkerCategoryStore.Category category)
    {
        string oldName = category.Name;
        var dialog = new TextPromptWindow(Loc.T("Dlg_RenameCategory"), Loc.T("Dlg_CategoryName"), oldName,
            text => ValidateCategoryName(text, except: category)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } name || name == oldName) return;

        category.Name = name;
        foreach (var a in _categoryStore.Markers.Where(a => string.Equals(a.Category, oldName, StringComparison.CurrentCultureIgnoreCase)))
            a.Category = name;
        foreach (string prefix in new[] { "", SavedCategoryKeyPrefix })
            if (_markerCategoryVisible.Remove(prefix + CustomCategoryKey(oldName), out bool visible))
                _markerCategoryVisible[prefix + CustomCategoryKey(name)] = visible;
        AfterCategoriesChanged();
    }

    private void DeleteCategory(MarkerCategoryStore.Category category)
    {
        if (MessageBox.Show(this, Loc.F("Dlg_DeleteCategoryConfirm", category.Name), Loc.T("Ctx_CategoryDelete"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _categoryStore.Categories.Remove(category);
        _categoryStore.Markers.RemoveAll(a => string.Equals(a.Category, category.Name, StringComparison.CurrentCultureIgnoreCase));
        foreach (string prefix in new[] { "", SavedCategoryKeyPrefix })
            _markerCategoryVisible.Remove(prefix + CustomCategoryKey(category.Name));
        AfterCategoriesChanged();
    }

    private string? PromptNewCategoryName()
    {
        var dialog = new TextPromptWindow(Loc.T("Dlg_NewCategory"), Loc.T("Dlg_CategoryName"), "",
            text => ValidateCategoryName(text, except: null)) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private string? ValidateCategoryName(string name, MarkerCategoryStore.Category? except)
    {
        if (name.Length == 0 || name.Any(char.IsControl)) return Loc.T("Dlg_CategoryNameEmpty");
        var existing = _categoryStore.FindCategory(name);
        return existing is not null && !ReferenceEquals(existing, except) ? Loc.F("Dlg_CategoryExists", existing.Name) : null;
    }

    /// <summary>Your categories' names, for the Edit Label dialog.</summary>
    private List<string> CustomCategoryNames() =>
        _categoryStore.Categories.Select(c => c.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The name the Edit Label dialog shows for a marker: its hand-picked category if it's
    /// one of yours, else empty (= by icon).</summary>
    private string AssignedCustomCategory(LoadedMarker marker) =>
        _assignmentIndex.TryGetValue(AssignmentKey(marker), out var a) && _categoryStore.FindCategory(a.Category) is { } c ? c.Name : "";

    private static MenuItem Item(string text, bool isChecked) =>
        new() { Header = new TextBlock { Text = text }, IsCheckable = false, IsChecked = isChecked };
}
