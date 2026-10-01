using System.Windows;
using NewUOAM.Updates;

namespace NewUOAM.App;

/// <summary>
/// Self-update (2026-10-01, user's request: players get new versions without losing markers or
/// settings). At startup and from Mapa > Zkontrolovat aktualizace the app reads the signed feed
/// of the newest GitHub release; a newer version opens <see cref="UpdateWindow"/>. Only an
/// installed package (NewUOAM.files next to the exe) updates itself - a dev build from bin\ never
/// does. Release process and compatibility rules: docs/RELEASE.md.
/// </summary>
public partial class MainWindow
{
    private bool _updateCheckRunning;

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckRunning) return;
        if (!PackageInstaller.IsInstalledPackage(AppContext.BaseDirectory))
        {
            if (manual)
                MessageBox.Show(this, $"Tohle je vývojová verze {App.CurrentVersion} sestavená ze zdrojáků, ta se neaktualizuje sama.",
                    "Aktualizace", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _updateCheckRunning = true;
        using var client = new UpdateClient(App.UpdateFeedOverride);
        try
        {
            UpdateManifest latest;
            try
            {
                latest = await client.GetLatestAsync(CancellationToken.None);
            }
            catch (UpdateException ex)
            {
                // At startup a failed check (offline, GitHub down) stays silent; it would only
                // overwrite more useful status lines (elevation, map load).
                if (manual) MessageBox.Show(this, $"Aktualizace nejde zkontrolovat: {ex.Message}", "Aktualizace", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (UpdateFeed.TryParseVersion(latest.Version) is not { } version || version <= App.CurrentVersion)
            {
                if (manual)
                    MessageBox.Show(this, $"Máš nejnovější verzi ({App.CurrentVersion}).", "Aktualizace", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var window = new UpdateWindow(latest, client) { Owner = this };
            if (window.ShowDialog() == true)
            {
                // Closing runs the usual save-settings/bye path; App.OnExit then starts the new exe.
                Close();
                Application.Current.Shutdown();
            }
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    private async void CheckUpdatesMenuItem_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);
}
