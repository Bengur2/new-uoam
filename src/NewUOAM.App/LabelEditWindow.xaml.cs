using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace NewUOAM.App;

/// <summary>Old UOAM's "Edit Label" dialog: Name, Type (icon), Position X/Y (prefilled from the
/// clicked tile, editable), Land (facet) and File (which marker file it's saved in). Used both for
/// "New Label..." and for editing an existing marker. The caller does the saving.</summary>
public partial class LabelEditWindow : Window
{
    public sealed record IconItem(string Name, BitmapImage? Image);
    public sealed record LandItem(int Index, string Name, int WidthTiles, int HeightTiles);
    public sealed record FileItem(string Path, string Display);

    public sealed record Values(string Name, string IconName, int X, int Y, int MapIndex, string FilePath);

    private const string DefaultIconName = "POINT";

    /// <summary>The icon string as it was in the file, kept verbatim unless the user picks a
    /// different type (files spell them loosely: "landmark", "armourers guild", "Ostatní").</summary>
    private readonly string? _originalIconName;
    private readonly Func<string, string> _normalizeIconName;
    /// <summary>How a newly picked type is written to the file (the icon files are named in
    /// capitals, "TOWN", which isn't how marker files spell categories).</summary>
    private readonly Func<string, string> _iconSpelling;

    public Values? Result { get; private set; }

    public LabelEditWindow(Values initial, IReadOnlyList<LandItem> lands, IReadOnlyList<FileItem> files,
                           string iconsDirectory, Func<string, BitmapImage?> loadIcon, Func<string, string> normalizeIconName,
                           Func<string, string> iconSpelling)
    {
        InitializeComponent();
        _normalizeIconName = normalizeIconName;
        _iconSpelling = iconSpelling;
        _originalIconName = string.IsNullOrEmpty(initial.IconName) ? null : initial.IconName;

        NameTextBox.Text = initial.Name;
        XTextBox.Text = initial.X.ToString();
        YTextBox.Text = initial.Y.ToString();

        var icons = new List<IconItem>();
        if (Directory.Exists(iconsDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(iconsDirectory, "*.png").Order(StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                icons.Add(new IconItem(name, loadIcon(name)));
            }
        }
        IconItem? selectedIcon = null;
        if (_originalIconName is not null)
        {
            string key = normalizeIconName(_originalIconName);
            selectedIcon = icons.FirstOrDefault(i => i.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (selectedIcon is null)
            {
                // A type with no bundled icon (shows the fallback icon on the map) - still offer it
                // so an edit doesn't silently change it.
                selectedIcon = new IconItem(_originalIconName, loadIcon(_originalIconName));
                icons.Insert(0, selectedIcon);
            }
        }
        IconComboBox.ItemsSource = icons;
        IconComboBox.SelectedItem = selectedIcon ?? icons.FirstOrDefault(i => i.Name == DefaultIconName) ?? icons.FirstOrDefault();

        LandComboBox.ItemsSource = lands;
        LandComboBox.SelectedItem = lands.FirstOrDefault(l => l.Index == initial.MapIndex) ?? lands.FirstOrDefault();

        FileComboBox.ItemsSource = files;
        FileComboBox.SelectedItem = files.FirstOrDefault(f => f.Path.Equals(initial.FilePath, StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault();

        Loaded += (_, _) => { NameTextBox.Focus(); NameTextBox.SelectAll(); };
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        string name = NameTextBox.Text.Trim();
        string? error = null;
        int x = 0, y = 0;
        var land = LandComboBox.SelectedItem as LandItem;
        var file = FileComboBox.SelectedItem as FileItem;

        if (name.Length == 0) error = "Vyplň název.";
        else if (!int.TryParse(XTextBox.Text.Trim(), out x) || !int.TryParse(YTextBox.Text.Trim(), out y)) error = "X a Y musí být celá čísla.";
        else if (land is null) error = "Vyber Land.";
        else if (x < 0 || y < 0 || x >= land.WidthTiles || y >= land.HeightTiles)
            error = $"Pozice je mimo {land.Name} (0-{land.WidthTiles - 1}, 0-{land.HeightTiles - 1}).";
        else if (file is null) error = "Vyber soubor.";

        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        string icon = (IconComboBox.SelectedItem as IconItem)?.Name ?? DefaultIconName;
        if (_originalIconName is not null && _normalizeIconName(_originalIconName).Equals(_normalizeIconName(icon), StringComparison.OrdinalIgnoreCase))
            icon = _originalIconName;
        else
            icon = _iconSpelling(icon);

        Result = new Values(name, icon, x, y, land!.Index, file!.Path);
        DialogResult = true;
    }
}
