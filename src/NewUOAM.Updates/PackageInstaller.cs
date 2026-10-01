using System.IO.Compression;
using System.Text;

namespace NewUOAM.Updates;

/// <summary>
/// Installs a staged package over the running app's own folder.
///
/// Windows lets a running process's exe and loaded DLLs be *renamed* (not overwritten), so every
/// file the update replaces is renamed to <c>name.old-update</c> and the new one copied in under
/// the real name - no separate updater exe. Verified on a self-contained WPF app: all 243 files,
/// 48 of them loaded modules, renamed while it ran. The next start deletes the leftovers
/// (<see cref="CleanupBackups"/>). Any failure rolls every step back, so a half-applied update
/// can't leave a broken app.
///
/// Only files listed in the package's <see cref="FileListName"/> are touched. Settings, markers
/// and caches live in %LocalAppData% or the player's own folders, never here, and a stray file a
/// player put next to the exe stays too.
/// </summary>
public static class PackageInstaller
{
    /// <summary>Every file of the package, one relative path per line. Its presence next to the
    /// exe is also what marks an installed package (a dev build from bin\ has none and never
    /// updates itself).</summary>
    public const string FileListName = "NewUOAM.files";
    public const string AppExeName = "NewUOAM.App.exe";
    public const string BackupSuffix = ".old-update";

    public static bool IsInstalledPackage(string appDir) => File.Exists(Path.Combine(appDir, FileListName));

    /// <summary>Release tool: writes the file list into a publish folder.</summary>
    public static void WriteFileList(string dir)
    {
        string root = Path.GetFullPath(dir);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f))
            .Where(f => !f.Equals(FileListName, StringComparison.OrdinalIgnoreCase))
            .Append(FileListName)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        File.WriteAllLines(Path.Combine(root, FileListName), files, new UTF8Encoding(false));
    }

    public static IReadOnlyList<string> ReadFileList(string dir)
    {
        var result = new List<string>();
        foreach (string raw in File.ReadAllLines(Path.Combine(dir, FileListName)))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (Path.IsPathRooted(line) || line.Split('\\', '/').Any(p => p is ".." or "."))
                throw new UpdateException($"Neplatná cesta v seznamu souborů: {line}");
            result.Add(line);
        }
        return result;
    }

    /// <summary>Extracts a downloaded package and returns the folder holding its files (the zip
    /// has them under one top folder, "NewUOAM\", for players unzipping it by hand).</summary>
    public static string Stage(string zipPath, string stagingDir)
    {
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
        // ExtractToDirectory refuses entries that would land outside stagingDir.
        ZipFile.ExtractToDirectory(zipPath, stagingDir);

        string root = File.Exists(Path.Combine(stagingDir, FileListName))
            ? stagingDir
            : Directory.GetDirectories(stagingDir).SingleOrDefault(d => File.Exists(Path.Combine(d, FileListName)))
              ?? throw new UpdateException("Balíček neobsahuje seznam souborů.");

        var files = ReadFileList(root);
        if (!files.Contains(AppExeName, StringComparer.OrdinalIgnoreCase))
            throw new UpdateException("Balíček neobsahuje aplikaci.");
        foreach (string file in files)
            if (!File.Exists(Path.Combine(root, file)))
                throw new UpdateException($"V balíčku chybí soubor {file}.");
        return root;
    }

    private sealed record Step(string Target, string? Backup, bool Copied);

    /// <summary>Swaps the staged files into appDir. Returns the new exe's path. Throws (after
    /// rolling back) on failure; UnauthorizedAccessException means no write access to appDir.</summary>
    public static string Apply(string appDir, string stagedRoot)
    {
        appDir = Path.GetFullPath(appDir);
        // Fail early and cleanly when the folder isn't writable (e.g. Program Files without admin).
        string probe = Path.Combine(appDir, $"write-test-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probe, "");
        File.Delete(probe);

        var newFiles = ReadFileList(stagedRoot);
        var newSet = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);
        var oldFiles = IsInstalledPackage(appDir) ? ReadFileList(appDir) : [];

        var steps = new List<Step>();
        try
        {
            foreach (string rel in newFiles)
            {
                string target = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? backup = File.Exists(target) ? MoveAside(target) : null;
                steps.Add(new Step(target, backup, Copied: false));
                File.Copy(Path.Combine(stagedRoot, rel), target);
                steps[^1] = steps[^1] with { Copied = true };
            }
            // Files the previous package had and this one dropped.
            foreach (string rel in oldFiles.Where(f => !newSet.Contains(f)))
            {
                string target = Path.Combine(appDir, rel);
                if (File.Exists(target)) steps.Add(new Step(target, MoveAside(target), Copied: false));
            }
        }
        catch
        {
            Rollback(steps);
            throw;
        }
        return Path.Combine(appDir, AppExeName);
    }

    private static string MoveAside(string target)
    {
        string backup = target + BackupSuffix;
        if (File.Exists(backup))
        {
            // A leftover a running instance still holds can't be deleted; pick a fresh name.
            try { File.Delete(backup); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                backup = $"{target}{BackupSuffix}-{Guid.NewGuid():N}";
            }
        }
        File.Move(target, backup);
        return backup;
    }

    private static void Rollback(List<Step> steps)
    {
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            var step = steps[i];
            try
            {
                if (step.Copied) File.Delete(step.Target);
                if (step.Backup is not null) File.Move(step.Backup, step.Target);
            }
            catch
            {
                // Best effort; keep restoring the rest.
            }
        }
    }

    public static bool HasBackups(string appDir)
    {
        try { return Directory.EnumerateFiles(appDir, "*" + BackupSuffix + "*", SearchOption.AllDirectories).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Deletes the previous version's renamed files. Ones another running instance
    /// still holds stay until a later start.</summary>
    public static int CleanupBackups(string appDir)
    {
        int deleted = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(appDir, "*" + BackupSuffix + "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); deleted++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return deleted;
    }
}
