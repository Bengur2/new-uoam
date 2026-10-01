using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Interop;
using NewUOAM.Positioning.ClientIntegration;

namespace NewUOAM.App;

/// <summary>App side of the old-UOAM in-game commands ("-c text", "-panic", "-unpanic").
///
/// Orion Assistant delivers a registered command only to the window that registered it FIRST and
/// can't unregister, so the registration is owned by NewUOAM.UoaBridge - one small process per UO
/// client that lives exactly as long as the client (see its Program.cs for the live test behind
/// this). The bridge forwards each command here via WM_COPYDATA to a message-only window titled
/// <see cref="SinkWindowTitle"/>; this class owns that window and starts a bridge for a client
/// that doesn't have one yet.</summary>
public sealed class UoAssistCommands : IDisposable
{
    /// <summary>Must match NewUOAM.UoaBridge's SinkWindowTitle and CopyDataMarker.</summary>
    private const string SinkWindowTitle = "NewUOAM.CommandSink";
    private const int CopyDataMarker = 0x4E554F41;
    private const uint WM_COPYDATA = 0x004A;

    /// <summary>The commands registered in a client. Fixed for a client's lifetime once its bridge
    /// runs - a command added here only reaches clients started afterwards.</summary>
    public static readonly string[] Commands = ["c", "panic", "unpanic", "who", "t"];

    private static readonly string[] BridgeFiles =
        ["NewUOAM.UoaBridge.exe", "NewUOAM.UoaBridge.dll", "NewUOAM.UoaBridge.runtimeconfig.json", "NewUOAM.UoaBridge.deps.json"];

    private readonly HwndSource _sink;
    private readonly HashSet<int> _startedFor = new();

    /// <summary>A command typed in a client (process id), with its argument text ("" if none).
    /// Raised on the UI thread.</summary>
    public event Action<int, string, string>? CommandReceived;

    /// <summary>Whether this app takes commands from the given client. With several maps open on
    /// one PC (one per client), the bridge offers a command to every map in turn; a map that
    /// declines lets it go to the next one. Null = accept everything.</summary>
    public Func<int, bool>? AcceptsClient { get; set; }

    /// <summary>Written once per problem (bridge files missing, start failed), for the status bar.</summary>
    public event Action<string>? Problem;

    public UoAssistCommands()
    {
        _sink = new HwndSource(new HwndSourceParameters(SinkWindowTitle)
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: message-only, never shown
            WindowStyle = 0,
        });
        _sink.AddHook(WndProc);
        // A bridge started by an unelevated run of the app is a lower-integrity process than an
        // elevated app, and Windows (UIPI) blocks its WM_COPYDATA unless explicitly allowed.
        ChangeWindowMessageFilterEx(_sink.Handle, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
    }

    private const uint MSGFLT_ALLOW = 1;

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr changeFilterStruct);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_COPYDATA) return IntPtr.Zero;
        var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
        if ((long)cds.dwData != CopyDataMarker || cds.cbData <= 0) return IntPtr.Zero;

        string text = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2);
        string[] parts = text.Split('\n', 3);
        if (parts.Length < 2 || !int.TryParse(parts[0], out int pid)) return IntPtr.Zero;
        handled = true;
        if (AcceptsClient is { } accepts && !accepts(pid)) return IntPtr.Zero; // another map's client
        string command = parts[1].ToLowerInvariant(); // OA matches commands case-insensitively
        string args = parts.Length > 2 ? parts[2].Trim() : "";
        CommandReceived?.Invoke(pid, command, args);
        return new IntPtr(1);
    }

    /// <summary>Starts a bridge for this client unless one already runs (named mutex, created by
    /// the bridge itself) - including one started by an earlier run of the app, which is exactly
    /// the case the bridge exists for. Only once the client exposes the UOAssist window (it's
    /// logged in with Orion Assistant), so there's no bridge for a client that can't use it.</summary>
    public void EnsureBridge(int clientPid)
    {
        if (UoAssistTextSender.FindWindowForProcess(clientPid) == IntPtr.Zero) return;
        if (Mutex.TryOpenExisting($"Local\\NewUOAM.UoaBridge.{clientPid}", out var existing))
        {
            existing.Dispose();
            return;
        }
        // Not twice from this run, even if the bridge failed to start (it logs why to
        // %LocalAppData%\NewUOAM\uoabridge\bridge.log); a new app run tries again.
        if (!_startedFor.Add(clientPid)) return;

        try
        {
            string exe = PrepareBridgeCopy();
            Process.Start(new ProcessStartInfo(exe, $"{clientPid} {string.Join(",", Commands)}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Problem?.Invoke($"Příkazy ve hře: nepodařilo se spustit pomocný proces ({ex.Message}).");
        }
    }

    /// <summary>Copies the bridge next to the app into %LocalAppData%\NewUOAM\uoabridge\&lt;hash&gt;
    /// and returns the copy's exe. A running bridge keeps its files open for as long as its client
    /// runs; running a copy keyed by content hash means a rebuilt/updated app never has to
    /// overwrite a file that's in use, and a new bridge version simply gets its own folder.</summary>
    private static string PrepareBridgeCopy()
    {
        string sourceDir = AppContext.BaseDirectory;
        // A dev build has the framework-dependent dll + apphost; the player package has one
        // self-contained single-file exe instead (players have no .NET installed, see
        // docs/RELEASE.md). Either way the hash is of the file that holds the code.
        string dll = Path.Combine(sourceDir, "NewUOAM.UoaBridge.dll");
        string codeFile = File.Exists(dll) ? dll : Path.Combine(sourceDir, "NewUOAM.UoaBridge.exe");
        if (!File.Exists(codeFile)) throw new FileNotFoundException("chybí NewUOAM.UoaBridge vedle aplikace");

        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(codeFile)))[..12];
        string targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NewUOAM", "uoabridge", hash);
        Directory.CreateDirectory(targetDir);
        foreach (string file in BridgeFiles)
        {
            string source = Path.Combine(sourceDir, file);
            string target = Path.Combine(targetDir, file);
            if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
        }
        return Path.Combine(targetDir, "NewUOAM.UoaBridge.exe");
    }

    public void Dispose() => _sink.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }
}
