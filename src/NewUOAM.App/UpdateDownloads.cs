using System.IO;

namespace NewUOAM.App;

/// <summary>Where a downloaded update lives until it's installed:
/// %LocalAppData%\NewUOAM\updates\&lt;version&gt;-&lt;pid&gt; (per process, so two map windows
/// updating at once can't trip over each other's files).</summary>
internal static class UpdateDownloads
{
    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NewUOAM", "updates");

    public static string DirFor(string version) => Path.Combine(Root, $"{version}-{Environment.ProcessId}");

    public static void Delete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Leftovers of an interrupted or failed update; an hour old means nobody's using them.</summary>
    public static void CleanupOld()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (string dir in Directory.GetDirectories(Root))
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-1)) Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
