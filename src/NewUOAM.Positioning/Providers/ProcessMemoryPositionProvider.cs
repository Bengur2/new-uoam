using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NewUOAM.Positioning.Providers;

/// <summary>
/// Variant B2: reads the player's own position directly out of the game process's memory, entirely
/// read-only (OpenProcess + ReadProcessMemory only - no WriteProcessMemory, no injection, no
/// hooking; a failed read only affects this app, never the game client).
///
/// The pointer chain below was found empirically via a live NewUOAM.MemoryScanner session against
/// this user's actual setup (see CLAUDE.md for the full story of how) - a real running instance,
/// walked/moved on request, exact-value-scanned and narrowed down, then reverse-pointer-searched
/// to find a STABLE anchor (a static pointer inside the module's own image, at a fixed offset from
/// its base - survives both game restarts and ASLR, since ASLR only relocates the whole module,
/// never offsets within it). The global's offset is build-specific: known builds are keyed by the
/// exe's SHA-256, an unknown build gets it located heuristically (see OrionPlayerPointerLocator).
/// If a future build also changes the player object's own layout (X/Y/Z offsets), the heuristic
/// finds nothing and says so - then a MemoryScanner session is the fix.
///
/// One correction this session found: the actual running game process here is `OrionUO64.exe`,
/// not `client.exe` - OrionUO is a full replacement client for this user's setup, not just a
/// script host layered on the original client.
/// </summary>
public sealed class ProcessMemoryPositionProvider : IPositionProvider
{
    public const string TargetModuleName = "OrionUO64.exe";

    // module_base + pointer offset -> pointer -> (player object) -> +OffsetX/Y/Z. The pointer offset
    // differs per client build: known builds come from OrionPlayerPointerLocator's hash table, an
    // unknown build gets it located automatically once the character is logged in.
    private const int OffsetX = OrionPlayerPointerLocator.OffsetX;
    private const int OffsetY = OrionPlayerPointerLocator.OffsetY;
    private const int OffsetZ = OrionPlayerPointerLocator.OffsetZ;

    private const int LocateRetryIntervalMs = 2000;
    private const int NullPointerWarningMs = 3000;

    // No confirmed offset for map/facet yet - this user's shard (Dark Paradise) only actually runs
    // Felucca, so this is a real, deliberate, documented limitation rather than an oversight.
    // Revisit (another MemoryScanner session, changing facet to see what changes) if ever used
    // somewhere with more than one facet.
    private const int HardcodedMap = 0;

    private const int PollIntervalMs = 50;

    private readonly int _processId;
    private string? _characterName;
    private IntPtr _handle;
    private CancellationTokenSource? _cts;
    private Task? _pollLoop;

    /// <summary>The tracked OrionUO64.exe - also what in-game chat display targets (see
    /// ClientIntegration.UoAssistTextSender), so multiboxing shows chat in the tracked client.</summary>
    public int ProcessId => _processId;

    public string Name => "Process memory (Varianta B2)";
    public string Description => $"Čte pozici přímo z paměti procesu PID {_processId} ({TargetModuleName}).";
    public PositionProviderStatus Status { get; private set; } = PositionProviderStatus.Stopped;

    public event EventHandler<PositionUpdate>? PositionChanged;
    public event EventHandler<string>? StatusMessage;

    /// <param name="processId">Which running OrionUO64.exe to read - see FindCandidateProcesses
    /// for enumerating them (this app's own UI uses that to let the user pick when more than one
    /// is running, e.g. multiboxing).</param>
    /// <param name="characterName">Overrides the name attached to each PositionUpdate; if null,
    /// it's parsed from the process's own window title (e.g. "Bodhi (Dark Paradise)" -> "Bodhi")
    /// once at StartAsync - reading it from memory was deliberately skipped since the window title
    /// already gives it for free, and searching for it in memory turned out far noisier (hundreds
    /// of unrelated hits - chat history, UI text caches) than the position struct was.</param>
    public ProcessMemoryPositionProvider(int processId, string? characterName = null)
    {
        _processId = processId;
        _characterName = characterName;
        _nameFromTitle = characterName is null;
    }

    private readonly bool _nameFromTitle;

    /// <summary>Every running process that plausibly hosts this offset chain (currently just
    /// OrionUO64.exe), oldest first - the app tracks the first one automatically and lets the user
    /// pick another (multiboxing). Cheap enough to call every couple of seconds: one process
    /// snapshot plus a window-title lookup per OrionUO64.exe.</summary>
    public static IReadOnlyList<(int ProcessId, string WindowTitle)> FindCandidateProcesses()
    {
        var result = new List<(int Pid, string Title, DateTime Started)>();
        foreach (var proc in Process.GetProcessesByName("OrionUO64"))
        {
            using (proc)
            {
                try
                {
                    DateTime started;
                    try { started = proc.StartTime; }
                    catch { started = DateTime.MaxValue; } // no access: order it last
                    result.Add((proc.Id, proc.MainWindowTitle, started));
                }
                catch { /* process could exit mid-enumeration - just skip it */ }
            }
        }
        return result.OrderBy(r => r.Started).ThenBy(r => r.Pid).Select(r => (r.Pid, r.Title)).ToList();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Status == PositionProviderStatus.Running) return Task.CompletedTask;
        Status = PositionProviderStatus.Starting;

        Process proc;
        try
        {
            proc = Process.GetProcessById(_processId);
        }
        catch (ArgumentException)
        {
            Status = PositionProviderStatus.Error;
            StatusMessage?.Invoke(this, $"Proces PID {_processId} už neběží.");
            throw;
        }

        ProcessModule? module;
        try
        {
            module = proc.Modules.Cast<ProcessModule>()
                .FirstOrDefault(m => m.ModuleName.Equals(TargetModuleName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Status = PositionProviderStatus.Error;
            StatusMessage?.Invoke(this, $"Nepodařilo se přečíst moduly procesu: {ex.Message}");
            throw;
        }

        if (module is null)
        {
            Status = PositionProviderStatus.Error;
            string message = $"Proces PID {_processId} neobsahuje modul {TargetModuleName}.";
            StatusMessage?.Invoke(this, message);
            throw new InvalidOperationException(message);
        }

        _handle = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, _processId);
        if (_handle == IntPtr.Zero)
        {
            Status = PositionProviderStatus.Error;
            string message = $"Nepodařilo se otevřít proces PID {_processId} pro čtení paměti.";
            StatusMessage?.Invoke(this, message);
            throw new InvalidOperationException(message);
        }

        if (string.IsNullOrWhiteSpace(_characterName))
            _characterName = ExtractCharacterName(proc.MainWindowTitle);

        string? sha256 = OrionPlayerPointerLocator.ComputeSha256(module.FileName);
        int? pointerOffset = null;
        string buildInfo;
        if (OrionPlayerPointerLocator.TryGetKnown(sha256, out string version, out int knownOffset))
        {
            pointerOffset = knownOffset;
            buildInfo = $"OrionUO {version}";
        }
        else
        {
            buildInfo = $"neznámá verze OrionUO64.exe (SHA-256 {sha256?[..12] ?? "?"}…), adresu postavy hledám automaticky";
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollLoop = Task.Run(() => PollLoopAsync(module.BaseAddress, pointerOffset, _cts.Token));
        Status = PositionProviderStatus.Running;
        StatusMessage?.Invoke(this, $"Čte paměť procesu {_processId} ({_characterName ?? "?"}), {buildInfo}.");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_pollLoop is not null)
        {
            try { await _pollLoop; } catch (OperationCanceledException) { }
        }
        if (_handle != IntPtr.Zero)
        {
            Native.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
        Status = PositionProviderStatus.Stopped;
    }

    private async Task PollLoopAsync(IntPtr moduleBase, int? pointerOffset, CancellationToken ct)
    {
        int? lastX = null, lastY = null;
        sbyte? lastZ = null;
        long nextLocateTicks = 0;
        bool reportedLocateFailure = false;
        long nullSinceTicks = 0;
        bool reportedNullPointer = false;
        long nextNameCheckTicks = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // The name comes from the window title, which only carries it once a character is
                // logged in - and changes on relog. The app now attaches to a client as soon as it
                // starts (often still at the login screen), so it's re-read every couple of seconds.
                if (_nameFromTitle && Environment.TickCount64 >= nextNameCheckTicks)
                {
                    nextNameCheckTicks = Environment.TickCount64 + LocateRetryIntervalMs;
                    if (RefreshCharacterName()) lastX = null; // re-emit so the new name goes out
                }

                if (pointerOffset is null)
                {
                    if (Environment.TickCount64 >= nextLocateTicks)
                    {
                        nextLocateTicks = Environment.TickCount64 + LocateRetryIntervalMs;
                        pointerOffset = TryLocatePointer(moduleBase);
                        if (pointerOffset is int found)
                            StatusMessage?.Invoke(this, $"Adresa postavy nalezena automaticky: {TargetModuleName}+0x{found:X}.");
                        else if (!reportedLocateFailure)
                        {
                            reportedLocateFailure = true;
                            StatusMessage?.Invoke(this, "Adresu postavy zatím nejde najít - postava musí být přihlášená ve hře. Zkouším dál...");
                        }
                    }
                }

                IntPtr objectBase = pointerOffset is int offset ? ReadPointer(IntPtr.Add(moduleBase, offset)) : IntPtr.Zero;
                if (pointerOffset is not null && objectBase == IntPtr.Zero)
                {
                    // Silent before: a null pointer (logged out, or offsets that don't fit this
                    // build) just skipped ticks with no hint at all.
                    if (nullSinceTicks == 0) nullSinceTicks = Environment.TickCount64;
                    if (!reportedNullPointer && Environment.TickCount64 - nullSinceTicks >= NullPointerWarningMs)
                    {
                        reportedNullPointer = true;
                        StatusMessage?.Invoke(this, "Ukazatel na postavu je prázdný - postava není přihlášená ve hře?");
                    }
                }
                else
                {
                    nullSinceTicks = 0;
                    reportedNullPointer = false;
                }

                if (objectBase != IntPtr.Zero)
                {
                    int x = ReadInt32(IntPtr.Add(objectBase, OffsetX));
                    int y = ReadInt32(IntPtr.Add(objectBase, OffsetY));
                    sbyte z = ReadSByte(IntPtr.Add(objectBase, OffsetZ));

                    if (x != lastX || y != lastY || z != lastZ)
                    {
                        lastX = x; lastY = y; lastZ = z;
                        PositionChanged?.Invoke(this, PositionUpdate.Now(x, y, z, HardcodedMap, _characterName));
                    }
                }
            }
            catch (Exception ex)
            {
                // Best-effort, same spirit as the UDP provider's malformed-packet handling - a
                // single bad read (freed page, timing hiccup) shouldn't stop the whole provider.
                StatusMessage?.Invoke(this, $"Chyba při čtení paměti: {ex.Message}");
            }

            try { await Task.Delay(PollIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Takes the character name from the window title when it has the "Name (Shard)"
    /// form. Other titles (login screen, a secondary dialog such as "Text Dialog") are ignored and
    /// keep the last known name. Returns whether the name changed.</summary>
    private bool RefreshCharacterName()
    {
        string? title;
        try
        {
            using var proc = Process.GetProcessById(_processId);
            title = proc.MainWindowTitle;
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (title is null || !title.Contains('(')) return false;
        string? name = ExtractCharacterName(title);
        if (name is null || name == _characterName) return false;
        _characterName = name;
        return true;
    }

    /// <summary>Unknown client build: find the g_Player offset from the live process. Needs the
    /// character logged in - the window title only carries "Name (Shard)" then, and the name is
    /// what tells the real player object apart from other globals with plausible coordinates.</summary>
    private int? TryLocatePointer(IntPtr moduleBase)
    {
        string? title;
        try
        {
            using var proc = Process.GetProcessById(_processId);
            title = proc.MainWindowTitle;
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (title is null || !title.Contains('(')) return null;
        string? name = ExtractCharacterName(title);
        if (name is null) return null;

        return OrionPlayerPointerLocator.TryLocate(TryRead, moduleBase.ToInt64(), name);
    }

    private byte[]? TryRead(long address, int size)
    {
        var buffer = new byte[size];
        bool ok = Native.ReadProcessMemory(_handle, new IntPtr(address), buffer, new IntPtr(size), out IntPtr bytesRead);
        return ok && bytesRead.ToInt64() == size ? buffer : null;
    }

    private IntPtr ReadPointer(IntPtr address) => new IntPtr(BitConverter.ToInt64(ReadExact(address, 8), 0));
    private int ReadInt32(IntPtr address) => BitConverter.ToInt32(ReadExact(address, 4), 0);
    private sbyte ReadSByte(IntPtr address) => unchecked((sbyte)ReadExact(address, 1)[0]);

    private byte[] ReadExact(IntPtr address, int size)
    {
        var buffer = new byte[size];
        bool ok = Native.ReadProcessMemory(_handle, address, buffer, new IntPtr(size), out IntPtr bytesRead);
        if (!ok || bytesRead.ToInt64() != size)
            throw new IOException($"ReadProcessMemory selhalo na 0x{address.ToInt64():X}.");
        return buffer;
    }

    /// <summary>"Bodhi (Dark Paradise)" -> "Bodhi" - OrionUO's own window title convention for
    /// this user's setup (character name, then shard name in parentheses).</summary>
    internal static string? ExtractCharacterName(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return null;
        int paren = windowTitle.IndexOf('(');
        string name = (paren > 0 ? windowTitle[..paren] : windowTitle).Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) Native.CloseHandle(_handle);
        _cts?.Dispose();
    }

    private static class Native
    {
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_VM_READ = 0x0010;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);
    }
}
