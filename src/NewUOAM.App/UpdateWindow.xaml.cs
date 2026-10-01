using System.ComponentModel;
using System.IO;
using System.Windows;
using NewUOAM.Updates;

namespace NewUOAM.App;

/// <summary>"Aktualizovat / Později" for one verified manifest. DialogResult = true means the new
/// files are in place and App.RestartExePath is set; the caller then closes the map.</summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateManifest _manifest;
    private readonly UpdateClient _client;
    private CancellationTokenSource? _download;
    private bool _installing;

    public UpdateWindow(UpdateManifest manifest, UpdateClient client)
    {
        InitializeComponent();
        _manifest = manifest;
        _client = client;
        VersionText.Text = Loc.F("Up_Version", App.CurrentVersion, manifest.Version,
            manifest.PublishedUtc.ToLocalTime().ToString(Loc.T("Up_DateFormat")), (manifest.PackageSize / (1024.0 * 1024.0)).ToString("0"));
        NotesTextBox.Text = string.IsNullOrWhiteSpace(manifest.Notes) ? Loc.T("Up_NoNotes") : manifest.Notes;
        Closing += OnClosing;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        LaterButton.SetResourceReference(ContentProperty, "Cr_Cancel");
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        ShowStatus("Stahuji…", error: false);

        string dir = UpdateDownloads.DirFor(_manifest.Version);
        _download = new CancellationTokenSource();
        try
        {
            string zip = Path.Combine(dir, "package.zip");
            var progress = new Progress<double>(p =>
            {
                DownloadProgress.Value = p;
                ShowStatus($"Stahuji… {p * 100:0} %", error: false);
            });
            await _client.DownloadAsync(_manifest, zip, progress, _download.Token);

            _installing = true;
            LaterButton.IsEnabled = false;
            ShowStatus("Instaluji…", error: false);
            string appDir = AppContext.BaseDirectory;
            string newExe = await Task.Run(() =>
            {
                string staged = PackageInstaller.Stage(zip, Path.Combine(dir, "staging"));
                return PackageInstaller.Apply(appDir, staged);
            });
            UpdateDownloads.Delete(dir);

            App.RestartExePath = newExe;
            _installing = false;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _installing = false;
            UpdateDownloads.Delete(dir);
            ShowStatus(Describe(ex), error: true);
            DownloadProgress.Visibility = Visibility.Collapsed;
            UpdateButton.IsEnabled = true;
            UpdateButton.SetResourceReference(ContentProperty, "Up_Retry");
            LaterButton.IsEnabled = true;
            LaterButton.SetResourceReference(ContentProperty, "Up_Later");
        }
        finally
        {
            _download?.Dispose();
            _download = null;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "Stahování zrušeno.",
        UnauthorizedAccessException =>
            $"Nemám právo zapisovat do složky mapy ({AppContext.BaseDirectory.TrimEnd('\\')}). " +
            "Spusť mapu jako správce, nebo ji přesuň do složky, kam se dá zapisovat. Nic se nezměnilo.",
        UpdateException => ex.Message + " Nic se nezměnilo.",
        IOException => $"Soubory mapy nejde vyměnit ({ex.Message}). Nic se nezměnilo.",
        _ => $"Aktualizace selhala ({ex.Message}). Nic se nezměnilo.",
    };

    private void ShowStatus(string text, bool error)
    {
        StatusLine.Text = text;
        StatusLine.Foreground = error ? System.Windows.Media.Brushes.LightCoral : System.Windows.Media.Brushes.Silver;
        StatusLine.Visibility = Visibility.Visible;
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_download is not null) { _download.Cancel(); return; }
        DialogResult = false;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // The file swap takes a moment and must not be interrupted by closing the map halfway.
        if (_installing) { e.Cancel = true; return; }
        _download?.Cancel();
    }
}
