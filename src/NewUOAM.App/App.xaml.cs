using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using NewUOAM.Updates;

namespace NewUOAM.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>Skips the elevation relaunch (testing/automation - an elevated window can't be
    /// driven by a non-elevated UI Automation client).</summary>
    private const string NoElevateArg = "--no-elevate";

    /// <summary>Testing only: check this feed URL instead of the GitHub one (see docs/RELEASE.md).</summary>
    private const string UpdateFeedArg = "--update-feed";

    private const int ErrorCancelled = 1223;

    /// <summary>True when the user declined the UAC prompt and the app kept running without admin
    /// rights - MainWindow shows a warning, since Varianta B2 then usually can't read the client.</summary>
    public static bool ElevationDeclined { get; private set; }

    public static string? UpdateFeedOverride { get; private set; }

    /// <summary>The installed version, as major.minor.build.</summary>
    public static Version CurrentVersion { get; } =
        UpdateFeed.Normalize(typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Set by the update dialog after the new files are in place; started on exit, once
    /// every window has saved its settings.</summary>
    public static string? RestartExePath { get; set; }

    private string[] _args = [];

    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _args = e.Args;
        int feedIndex = Array.IndexOf(e.Args, UpdateFeedArg);
        if (feedIndex >= 0 && feedIndex + 1 < e.Args.Length) UpdateFeedOverride = e.Args[feedIndex + 1];

        // Relaunch elevated: Varianta B2 reads the client's memory, which Windows denies to a
        // non-admin process whenever OrionUO runs as admin (user's request, 2026-09-24). Done here
        // rather than via requireAdministrator in a manifest so there's a way around it
        // (--no-elevate) and a declined prompt still leaves a usable app.
        if (!IsAdministrator && !e.Args.Contains(NoElevateArg) && Environment.ProcessPath is string exePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = JoinArgs(e.Args),
                });
                Shutdown();
                return;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                ElevationDeclined = true;
            }
        }

        // The previous version's files, renamed aside by the last update (PackageInstaller). Right
        // after an update the old process is still exiting and holds them, hence the retries.
        if (PackageInstaller.IsInstalledPackage(AppContext.BaseDirectory))
        {
            Task.Run(async () =>
            {
                foreach (int delayMs in new[] { 0, 3000, 10000, 30000 })
                {
                    await Task.Delay(delayMs);
                    PackageInstaller.CleanupBackups(AppContext.BaseDirectory);
                    if (!PackageInstaller.HasBackups(AppContext.BaseDirectory)) break;
                }
                UpdateDownloads.CleanupOld();
            });
        }

        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        if (RestartExePath is null) return;
        try
        {
            // Same arguments, same elevation (a child of an elevated process is elevated too; an
            // unelevated one stays unelevated instead of asking UAC again).
            var args = _args.ToList();
            if (!IsAdministrator && !args.Contains(NoElevateArg)) args.Add(NoElevateArg);
            Process.Start(new ProcessStartInfo(RestartExePath, JoinArgs(args))
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(RestartExePath)!,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Aktualizace je nainstalovaná, ale mapu se nepodařilo znovu spustit ({ex.Message}). Spusť ji prosím ručně.",
                "new UOAM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string JoinArgs(IEnumerable<string> args) => string.Join(' ', args.Select(a => $"\"{a}\""));
}
