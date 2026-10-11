using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NewUOAM.Positioning.ClientIntegration;

namespace NewUOAM.Positioning.Providers;

/// <summary>
/// Variant B3: the player's position from an assistant that exposes the **UOAssist API** - in
/// practice Razor running inside a ClassicUO client (e.g. Endor Revived's EndorRevived.exe), the
/// same way old UOAM got it from UOAssist/Razor. No memory reading at all, only documented window
/// messages to the assistant's <see cref="UoAssistTextSender.WindowClass"/> window:
/// - <c>GET_COORDS</c> (WM_USER+202), polled every <see cref="PollIntervalMs"/>: returns
///   X | Y &lt;&lt; 16, or 0 while no character is logged in. No Z (always reported as 0).
/// - <c>REGISTER</c> (WM_USER+200, wParam = our window) subscribes to notifications. Razor
///   toggles: registering the same window again unregisters it (returns 2), so it's sent exactly
///   once per assistant window, and once more on stop.
/// - <c>MAP_INFO</c> (WM_USER+313, wParam = facet) arrives only when the facet **changes**
///   (Razor's PlayerData.OnMapChange). <c>LOGIN</c> (WM_USER+303) resets it to 0: a new player
///   object starts at facet 0, and Razor posts MAP_INFO right after if the character is elsewhere.
///   Limitation: attaching to a character that is already logged in on another facet shows facet
///   0 until the next facet change or relog - Razor offers no way to query the current facet.
///   Within one app run the last facet seen per character is remembered across client switches,
///   and the map's World menu can set it by hand (<see cref="OverrideFacet"/>).
/// The character name comes from Razor's own window title, "Name (Shard) - Razor v1.46.0.0".
///
/// Source of the contract: Razor's Razor/Client/UOAssist.cs and Core/Player.cs (markdwags/Razor),
/// verified live against Endor Revived's Razor 1.46 on 2026-10-11.
/// </summary>
public sealed class UoAssistPositionProvider : IClientPositionProvider
{
    private const uint WM_USER = 0x0400;
    private const uint UOA_REGISTER = WM_USER + 200;
    private const uint UOA_GET_COORDS = WM_USER + 202;
    private const uint UOA_LOGIN = WM_USER + 303;
    private const uint UOA_LOGOUT = WM_USER + 308;
    private const uint UOA_MAP_INFO = WM_USER + 313;
    private const uint WM_OVERRIDE_FACET = 0x8000 + 1; // WM_APP+1, posted by OverrideFacet to our own window
    private const uint WM_TIMER = 0x0113;
    private const uint WM_QUIT = 0x0012;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint MSGFLT_ALLOW = 1;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private const int PollIntervalMs = 50;
    private const int CheckIntervalMs = 2000;
    private const uint SendTimeoutMs = 500;
    private const string WindowClassName = "NewUOAM.UoAssistPosition";
    private const string RazorTitleMarker = " - Razor v";

    private const int PollTimerId = 1;
    private const int CheckTimerId = 2;

    // Last facet seen per character name in this app run: MAP_INFO only comes on a change, so
    // switching away from a client and back must not drop to facet 0.
    private static readonly Dictionary<string, int> LastFacetByCharacter = new(StringComparer.Ordinal);
    private static readonly object FacetLock = new();

    private static WndProc? _wndProcKeepAlive; // the native class holds only a function pointer
    private static readonly Dictionary<IntPtr, UoAssistPositionProvider> Instances = new();

    private readonly int _processId;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _window;
    private IntPtr _assistant; // the client's UOAssist window
    private bool _registered; // REGISTER accepted by _assistant (a second REGISTER would undo it)
    private int _facet;
    private bool _facetKnown;
    private string? _characterName;
    private int? _lastX, _lastY, _lastFacet;
    private bool _reportedNotLoggedIn;
    private bool _reportedNoAssistant;
    private bool _reportedSendFailure;

    public UoAssistPositionProvider(int processId) => _processId = processId;

    public int ProcessId => _processId;
    public string Name => "UOAssist API (Varianta B3)";
    public string Description => $"Čte pozici přes rozhraní UOAssist (Razor) klienta PID {_processId}.";
    public PositionProviderStatus Status { get; private set; } = PositionProviderStatus.Stopped;

    public event EventHandler<PositionUpdate>? PositionChanged;
    public event EventHandler<string>? StatusMessage;

    /// <summary>Every running process with a UOAssist window that B2 doesn't already read
    /// (OrionUO64.exe exposes the API too, through Orion Assistant), oldest first.</summary>
    public static IReadOnlyList<(int ProcessId, string WindowTitle)> FindCandidateProcesses()
    {
        var result = new List<(int Pid, string Title, DateTime Started)>();
        foreach (int pid in UoAssistTextSender.FindAllProcessIds().Distinct())
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.ProcessName.Equals("OrionUO64", StringComparison.OrdinalIgnoreCase)) continue;
                DateTime started;
                try { started = proc.StartTime; }
                catch { started = DateTime.MaxValue; }
                result.Add((pid, proc.MainWindowTitle, started));
            }
            catch { /* exited mid-enumeration */ }
        }
        return result.OrderBy(r => r.Started).ThenBy(r => r.Pid).Select(r => (r.Pid, r.Title)).ToList();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Status == PositionProviderStatus.Running) return Task.CompletedTask;
        Status = PositionProviderStatus.Starting;

        IntPtr assistant = UoAssistTextSender.FindWindowForProcess(_processId);
        if (assistant == IntPtr.Zero)
        {
            Status = PositionProviderStatus.Error;
            string message = $"Proces PID {_processId} nenabízí rozhraní UOAssist (běží v něm Razor?).";
            StatusMessage?.Invoke(this, message);
            throw new InvalidOperationException(message);
        }
        // A test query before committing: fails when the client runs elevated and the map doesn't
        // (UIPI blocks a lower-integrity sender), or when the assistant hangs.
        if (!TrySend(assistant, UOA_GET_COORDS, IntPtr.Zero, out _))
        {
            Status = PositionProviderStatus.Error;
            string message = $"Rozhraní UOAssist klienta PID {_processId} neodpovídá (Přístup byl odepřen?).";
            StatusMessage?.Invoke(this, message);
            throw new InvalidOperationException(message);
        }

        var started = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(started)) { IsBackground = true, Name = $"UOAssist position {_processId}" };
        _thread.Start();
        Exception? error = started.Task.GetAwaiter().GetResult();
        if (error is not null)
        {
            Status = PositionProviderStatus.Error;
            StatusMessage?.Invoke(this, error.Message);
            throw error;
        }
        Status = PositionProviderStatus.Running;
        StatusMessage?.Invoke(this, $"Čte pozici přes UOAssist (Razor) klienta PID {_processId} ({_characterName ?? "?"}).");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_thread is { } thread)
        {
            PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            return Task.Run(() =>
            {
                thread.Join(2000);
                _thread = null;
                Status = PositionProviderStatus.Stopped;
            });
        }
        Status = PositionProviderStatus.Stopped;
        return Task.CompletedTask;
    }

    /// <summary>Posted to the provider's thread, which owns all the facet state.</summary>
    public void OverrideFacet(int facet)
    {
        if (_window != IntPtr.Zero) PostMessageW(_window, WM_OVERRIDE_FACET, (IntPtr)facet, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_thread is not null) StopAsync().GetAwaiter().GetResult();
    }

    /// <summary>The provider's own thread: owns the message-only window that receives Razor's
    /// notifications and runs the poll timer, so all state is touched from this thread only.</summary>
    private void Run(TaskCompletionSource<Exception?> started)
    {
        _threadId = GetCurrentThreadId();
        try
        {
            lock (Instances)
            {
                if (_wndProcKeepAlive is null)
                {
                    _wndProcKeepAlive = StaticWindowProc;
                    var wc = new WNDCLASS { lpfnWndProc = _wndProcKeepAlive, lpszClassName = WindowClassName };
                    RegisterClassW(ref wc); // fails harmlessly if a previous provider registered it
                }
            }
            _window = CreateWindowExW(0, WindowClassName, WindowClassName, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_window == IntPtr.Zero)
                throw new InvalidOperationException($"Nepodařilo se vytvořit okno pro zprávy UOAssist ({Marshal.GetLastWin32Error()}).");
            lock (Instances) Instances[_window] = this;

            // The map usually runs elevated and the client doesn't: without this Windows (UIPI)
            // silently drops the notifications the client posts to us (same as the bridge's
            // command messages, found live 2026-09-25).
            foreach (uint msg in new[] { UOA_LOGIN, UOA_LOGOUT, UOA_MAP_INFO })
                ChangeWindowMessageFilterEx(_window, msg, MSGFLT_ALLOW, IntPtr.Zero);

            RefreshCharacterName();
            CheckAssistant();
            SetTimer(_window, (UIntPtr)PollTimerId, PollIntervalMs, IntPtr.Zero);
            SetTimer(_window, (UIntPtr)CheckTimerId, CheckIntervalMs, IntPtr.Zero);
            started.TrySetResult(null);
        }
        catch (Exception ex)
        {
            Cleanup();
            started.TrySetResult(ex);
            return;
        }

        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            DispatchMessageW(ref msg);

        Cleanup();
    }

    private void Cleanup()
    {
        if (_registered)
            TrySend(_assistant, UOA_REGISTER, _window, out _); // second REGISTER = unregister
        _registered = false;
        _assistant = IntPtr.Zero;
        if (_window != IntPtr.Zero)
        {
            lock (Instances) Instances.Remove(_window);
            DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
    }

    private static IntPtr StaticWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        UoAssistPositionProvider? self;
        lock (Instances) Instances.TryGetValue(hwnd, out self);
        if (self is not null && self.HandleMessage(msg, wParam)) return IntPtr.Zero;
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private bool HandleMessage(uint msg, IntPtr wParam)
    {
        try
        {
            switch (msg)
            {
                case WM_TIMER when (long)wParam == PollTimerId:
                    Poll();
                    return true;
                case WM_TIMER when (long)wParam == CheckTimerId:
                    CheckAssistant();
                    if (RefreshCharacterName()) _lastX = null; // re-emit with the new name
                    return true;
                case UOA_LOGIN:
                    // New player object: facet 0 until Razor says otherwise (MAP_INFO follows
                    // right away for a character on another facet).
                    SetFacet(0);
                    RefreshCharacterName();
                    _lastX = null;
                    return true;
                case UOA_LOGOUT:
                    _lastX = null;
                    return true;
                case WM_OVERRIDE_FACET:
                    SetFacet((int)(long)wParam);
                    Poll();
                    return true;
                case UOA_MAP_INFO:
                    SetFacet((int)(long)wParam & 0xFF);
                    StatusMessage?.Invoke(this, $"Razor hlásí změnu mapy: facet {_facet}.");
                    Poll();
                    return true;
            }
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(this, $"Chyba při čtení pozice přes UOAssist: {ex.Message}");
            return true;
        }
        return false;
    }

    private void SetFacet(int facet)
    {
        _facet = facet;
        _facetKnown = true;
        if (_characterName is { } name)
            lock (FacetLock) LastFacetByCharacter[name] = facet;
    }

    /// <summary>(Re-)registers with the client's UOAssist window when it appears or changes
    /// (Razor restarted inside the client).</summary>
    private void CheckAssistant()
    {
        IntPtr assistant = UoAssistTextSender.FindWindowForProcess(_processId);
        if (assistant == _assistant) return;
        _assistant = assistant;
        _registered = false;
        if (assistant == IntPtr.Zero)
        {
            if (!_reportedNoAssistant)
            {
                _reportedNoAssistant = true;
                StatusMessage?.Invoke(this, "Rozhraní UOAssist v klientovi zmizelo - čekám, až se Razor znovu načte.");
            }
            return;
        }
        _reportedNoAssistant = false;
        // Polling works without the registration, so a failure only costs the facet changes.
        _registered = TrySend(assistant, UOA_REGISTER, _window, out IntPtr result) && result == (IntPtr)1;
        if (!_registered)
            StatusMessage?.Invoke(this, $"Registrace u rozhraní UOAssist selhala (výsledek {result}) - změny mapy (facet) neuvidím.");
    }

    private void Poll()
    {
        if (_assistant == IntPtr.Zero) return;
        if (!TrySend(_assistant, UOA_GET_COORDS, IntPtr.Zero, out IntPtr result))
        {
            if (!_reportedSendFailure)
            {
                _reportedSendFailure = true;
                StatusMessage?.Invoke(this, "Rozhraní UOAssist neodpovídá.");
            }
            return;
        }
        _reportedSendFailure = false;

        int packed = unchecked((int)(long)result);
        if (packed == 0)
        {
            if (!_reportedNotLoggedIn)
            {
                _reportedNotLoggedIn = true;
                StatusMessage?.Invoke(this, "Razor zatím nehlásí pozici - postava není přihlášená ve hře?");
            }
            return;
        }
        _reportedNotLoggedIn = false;

        if (!_facetKnown && _characterName is { } name)
        {
            lock (FacetLock)
                if (LastFacetByCharacter.TryGetValue(name, out int remembered)) _facet = remembered;
            _facetKnown = true;
        }

        int x = packed & 0xFFFF;
        int y = (packed >> 16) & 0xFFFF;
        if (x == _lastX && y == _lastY && _facet == _lastFacet) return;
        _lastX = x; _lastY = y; _lastFacet = _facet;
        PositionChanged?.Invoke(this, PositionUpdate.Now(x, y, 0, _facet, _characterName));
    }

    /// <summary>Takes the character name from Razor's window title ("Name (Shard) - Razor
    /// v1.46.0.0"); without a logged-in character the title is just "Razor v..." and the last
    /// known name stays. Returns whether the name changed.</summary>
    private bool RefreshCharacterName()
    {
        string? name = null;
        var title = new StringBuilder(512);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != _processId) return true;
            title.Clear();
            GetWindowTextW(hwnd, title, title.Capacity);
            string text = title.ToString();
            int marker = text.IndexOf(RazorTitleMarker, StringComparison.Ordinal);
            if (marker <= 0) return true;
            string head = text[..marker];
            int paren = head.IndexOf('(');
            if (paren <= 0) return true;
            name = head[..paren].Trim();
            return false;
        }, IntPtr.Zero);

        if (string.IsNullOrEmpty(name) || name == _characterName) return false;
        _characterName = name;
        return true;
    }

    private static bool TrySend(IntPtr hwnd, uint msg, IntPtr wParam, out IntPtr result) =>
        SendMessageTimeoutW(hwnd, msg, wParam, IntPtr.Zero, SMTO_ABORTIFHUNG, SendTimeoutMs, out result) != IntPtr.Zero;

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr func);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr changeFilterStruct);
}
