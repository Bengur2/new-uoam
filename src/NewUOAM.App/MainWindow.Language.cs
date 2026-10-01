using System.Windows.Controls;
using NewUOAM.Updates;

namespace NewUOAM.App;

// Mapa > Jazyk (user's request 2026-10-01): the language of the map's controls, see Loc. Texts in
// XAML follow a switch on their own (DynamicResource); the ones code keeps on screen are set again
// in ApplyLocalizedTexts.
public partial class MainWindow
{
    private void BuildLanguageMenu()
    {
        foreach (var (code, name) in Loc.Languages)
        {
            // A TextBlock header: the names aren't translated and have no access key.
            var item = new MenuItem { Header = new TextBlock { Text = name }, Tag = code };
            item.Click += (_, _) => SetLanguage(code);
            LanguageMenuItem.Items.Add(item);
        }
    }

    private void SetLanguage(string code)
    {
        if (code == Loc.Current) return;
        Loc.Apply(code); // raises Loc.Changed -> ApplyLocalizedTexts
        SaveSettings();
    }

    /// <summary>Texts set from code that stay on screen; called at startup and on every switch.</summary>
    private void ApplyLocalizedTexts()
    {
        foreach (var item in LanguageMenuItem.Items.OfType<MenuItem>())
            item.IsChecked = (string)item.Tag == Loc.Current;

        VersionMenuItem.Header = Loc.F("Menu_Version", App.CurrentVersion);
        // Version in the title (user's request 2026-10-01); a build from bin\ says so, to tell it
        // apart from the installed package when both run.
        Title = PackageInstaller.IsInstalledPackage(AppContext.BaseDirectory)
            ? $"new UOAM {App.CurrentVersion}"
            : Loc.F("Title_Dev", App.CurrentVersion);

        SetSelfMarkerColor(_selfMarkerColor); // "(výchozí)"
        UpdateMarkersDirPlaceholder();
        UpdateChatUnreadBadge();
        RebuildMarkerRows(keepScroll: true); // section titles, hint, count
    }
}
