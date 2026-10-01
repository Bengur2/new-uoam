using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NewUOAM.UoaBridge;

/// <summary>Receives old-UOAM-style in-game commands ("-c text", "-panic", ...) from one UO client
/// and forwards them to the running new UOAM app.
///
/// The commands come from the UOAssist API that Orion Assistant exposes: ADD_CMD (WM_USER+209)
/// registers a command name for a window, and when the player types "-name args" in the game OA
/// swallows the line (it never reaches the server) and posts the returned message id to that
/// window, with the args in a global atom (wParam, which the receiver must delete).
///
/// Why a separate process instead of the app registering its own window - tested live against
/// OA v3.0.38 on 2026-09-25:
/// - OA can't unregister a command (ADD_CMD with wParam 0 just registers another one), and
/// - when a name is registered twice, OA delivers only to the FIRST registration.
/// So a registration lives exactly as long as the client process, and the window it points to
/// has to live that long too. If the app registered itself, every app restart would leave the
/// commands pointing at a dead window until the client was restarted. This process registers
/// once per client and exits together with the client; the app can restart freely.
///
/// Stays read-only toward the client like the rest of new UOAM: only documented window
/// messages to the assistant, no injection or memory writes.
///
/// Args: &lt;client pid&gt; &lt;comma-separated command names&gt;. One instance per client pid
/// (named mutex). The app runs it from a copy in %LocalAppData% (see MainWindow), so rebuilding
/// or updating the app never collides with a running bridge. Kept dependency-free and tiny on
/// purpose: changing it only takes effect for clients started afterwards.</summary>
internal static class Program
{
    /// <summary>Window title of the app's receiving message-only window (MainWindow's
    /// UoAssistCommandSink). Must match on both sides.</summary>
    private const string SinkWindowTitle = "NewUOAM.CommandSink";
    /// <summary>WM_COPYDATA dwData marker ("NUOA") so the sink can ignore anything else.</summary>
    private const int CopyDataMarker = 0x4E554F41;

    private const string UoAssistWindowClass = "UOASSIST-TP-MSG-WND";
    private const uint WM_USER = 0x0400;
    private const uint UOA_DISPLAY_TEXT = WM_USER + 207;
    private const uint UOA_ADD_CMD = WM_USER + 209;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_COPYDATA = 0x004A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private static int _clientPid;
    private static string[] _commands = [];
    private static IntPtr _window;
    private static IntPtr _registeredWith;
    // Message id -> command. Never cleared: after a relogin OA may keep the old registrations
    // (first one wins), so old ids must keep working.
    private static readonly Dictionary<uint, string> _messageIds = new();
    private static WndProc? _wndProc; // kept alive - the native side holds only a function pointer
    private static string _logPath = "";

    private static int Main(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[0], out _clientPid)) return 2;
        _commands = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NewUOAM", "uoabridge");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "bridge.log");

        using var mutex = new Mutex(initiallyOwned: true, MutexName(_clientPid), out bool createdNew);
        if (!createdNew) return 0; // another bridge already serves this client

        Process client;
        try { client = Process.GetProcessById(_clientPid); }
        catch (ArgumentException) { return 0; }

        _wndProc = WindowProc;
        var wc = new WNDCLASS { lpfnWndProc = _wndProc, lpszClassName = "NewUOAM.UoaBridge" };
        RegisterClassW(ref wc);
        _window = CreateWindowExW(0, wc.lpszClassName, "NewUOAM.UoaBridge", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_window == IntPtr.Zero) { Log($"pid {_clientPid}: CreateWindowEx failed ({Marshal.GetLastWin32Error()})"); return 1; }
        Log($"pid {_clientPid}: started, commands {string.Join(",", _commands)}");

        // Checks every 2s whether the client still runs and whether its assistant window appeared
        // (OA creates it only once the character is logged in) or changed (relogin).
        SetTimer(_window, (UIntPtr)1, 2000, IntPtr.Zero);
        CheckClient(client);
        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_TIMER && msg.hwnd == _window && !CheckClient(client)) break;
            DispatchMessageW(ref msg);
        }
        Log($"pid {_clientPid}: client exited, bridge ends");
        return 0;
    }

    internal static string MutexName(int pid) => $"Local\\NewUOAM.UoaBridge.{pid}";

    /// <returns>False once the client process is gone.</returns>
    private static bool CheckClient(Process client)
    {
        client.Refresh();
        if (client.HasExited) return false;
        IntPtr uoa = FindUoAssistWindow(_clientPid);
        if (uoa != IntPtr.Zero && uoa != _registeredWith)
        {
            _registeredWith = uoa;
            foreach (string command in _commands) Register(uoa, command);
        }
        return true;
    }

    private static void Register(IntPtr uoa, string command)
    {
        ushort atom = GlobalAddAtomW(command);
        if (atom == 0) return;
        IntPtr sent = SendMessageTimeoutW(uoa, UOA_ADD_CMD, _window, (IntPtr)atom, SMTO_ABORTIFHUNG, 2000, out IntPtr result);
        GlobalDeleteAtom(atom);
        uint id = (uint)(long)result;
        if (sent != IntPtr.Zero && id >= WM_USER + 400)
        {
            _messageIds[id] = command;
            // The app runs elevated (for B2), so this bridge does too, while the client usually
            // doesn't - and Windows (UIPI) silently drops messages a lower-integrity process
            // posts to a higher one unless the window allows them. Found live 2026-09-25: every
            // command vanished with an elevated app, worked with --no-elevate.
            if (!ChangeWindowMessageFilterEx(_window, id, MSGFLT_ALLOW, IntPtr.Zero))
                Log($"pid {_clientPid}: allowing WM_USER+{id - WM_USER} through UIPI failed ({Marshal.GetLastWin32Error()})");
            Log($"pid {_clientPid}: registered -{command} as WM_USER+{id - WM_USER}");
        }
        else Log($"pid {_clientPid}: registering -{command} failed (sent={sent != IntPtr.Zero}, result={result})");
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg >= WM_USER + 400 && _messageIds.TryGetValue(msg, out string? command))
        {
            string args = "";
            ushort atom = (ushort)(long)wParam;
            if (atom != 0)
            {
                var sb = new StringBuilder(512);
                GlobalGetAtomNameW(atom, sb, sb.Capacity);
                GlobalDeleteAtom(atom);
                args = sb.ToString();
            }
            Forward(command, args);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>WM_COPYDATA to the app's sink window: "pid\ncommand\nargs" (UTF-16). Several map
    /// windows can run on one PC (one per client): each sink is tried in turn, and an app that
    /// tracks a different client via B2 declines (returns 0), so the command lands in the map
    /// that belongs to this client. If none takes it, says so in the game instead of failing
    /// silently.</summary>
    private static void Forward(string command, string args)
    {
        bool ok = false;
        byte[] data = Encoding.Unicode.GetBytes($"{_clientPid}\n{command}\n{args}");
        IntPtr buffer = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, buffer, data.Length);
            var cds = new COPYDATASTRUCT { dwData = (IntPtr)CopyDataMarker, cbData = data.Length, lpData = buffer };
            IntPtr sink = IntPtr.Zero;
            while (!ok && (sink = FindWindowExW(HWND_MESSAGE, sink, null, SinkWindowTitle)) != IntPtr.Zero)
            {
                IntPtr sent = SendMessageTimeoutW(sink, WM_COPYDATA, _window, ref cds, SMTO_ABORTIFHUNG, 3000, out IntPtr result);
                ok = sent != IntPtr.Zero && result != IntPtr.Zero;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        Log($"pid {_clientPid}: -{command} received, {(ok ? "forwarded to the app" : "app not reachable")}"); // never the text itself
        if (!ok) ShowInGame($"[Mapa] new UOAM nebezi - prikaz -{command} nejde provest.");
    }

    private static void ShowInGame(string text)
    {
        if (_registeredWith == IntPtr.Zero) return;
        ushort atom = GlobalAddAtomW(text);
        if (atom == 0) return;
        IntPtr sent = SendMessageTimeoutW(_registeredWith, UOA_DISPLAY_TEXT, (IntPtr)(0x44 | 0x10000), (IntPtr)atom, SMTO_ABORTIFHUNG, 1000, out IntPtr result);
        if (sent == IntPtr.Zero || result == IntPtr.Zero) GlobalDeleteAtom(atom);
    }

    private static IntPtr FindUoAssistWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        var className = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != processId) return true;
            className.Clear();
            GetClassNameW(hwnd, className, className.Capacity);
            if (className.ToString() != UoAssistWindowClass) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static void Log(string line)
    {
        try
        {
            var info = new FileInfo(_logPath);
            if (info.Exists && info.Length > 256 * 1024) info.Delete();
            File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (IOException) { } // several bridges may write at once - a lost log line is fine
        catch (UnauthorizedAccessException) { }
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr func);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string windowName);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wParam, ref COPYDATASTRUCT lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern ushort GlobalAddAtomW(string text);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GlobalGetAtomNameW(ushort atom, StringBuilder buffer, int size);
    [DllImport("kernel32.dll")] private static extern ushort GlobalDeleteAtom(ushort atom);
    private const uint MSGFLT_ALLOW = 1;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr changeFilterStruct);
}
