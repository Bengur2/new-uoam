using System.Diagnostics;
using System.Text.Json;
using NewUOAM.MemoryScanner;

// Non-interactive mode: `NewUOAM.MemoryScanner.exe --state <file> <command> [args...]` runs ONE
// command and exits, loading/saving the attached PID + candidate list to a small JSON state file
// across invocations. Exists so an outer driver (Claude running this tool on the user's behalf,
// or a script) can run one scan/next step per real-world action - e.g. "move in-game, then run
// the next step" - without needing to keep an interactive process's stdin open across however
// long that takes. The normal interactive REPL below (for a human typing directly) is unaffected.
if (args.Length >= 3 && args[0] == "--state")
{
    RunNonInteractive(args[1], args[2], args.Length > 3 ? string.Join(' ', args[3..]) : "");
    return;
}

Console.WriteLine("NewUOAM Memory Scanner (Varianta B2) - jen čtení, nikdy nezapisuje do client.exe.");
Console.WriteLine("Napiš 'help' pro seznam příkazů.\n");

MemoryScanner? scanner = null;

while (true)
{
    Console.Write("> ");
    string? line = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(line)) continue;

    string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    string cmd = parts[0].ToLowerInvariant();
    string arg = parts.Length > 1 ? parts[1] : "";

    try
    {
        switch (cmd)
        {
            case "help":
                PrintHelp();
                break;

            case "attach":
                scanner?.Dispose();
                scanner = Attach(arg);
                break;

            case "selftest":
                SelfTest(arg);
                break;

            case "scan":
                if (!RequireScanner(scanner)) break;
                if (!int.TryParse(arg, out int scanVal)) { Console.WriteLine("Použití: scan <celé číslo>"); break; }
                {
                    var sw = Stopwatch.StartNew();
                    int count = scanner!.ScanInt32(scanVal);
                    Console.WriteLine($"Nalezeno {count} kandidátů za {sw.Elapsed.TotalSeconds:F1}s.");
                }
                break;

            case "next":
                if (!RequireScanner(scanner)) break;
                if (!int.TryParse(arg, out int nextVal)) { Console.WriteLine("Použití: next <celé číslo>"); break; }
                {
                    int before = scanner!.Candidates.Count;
                    int count = scanner.NarrowInt32(nextVal);
                    Console.WriteLine($"{before} -> {count} kandidátů.");
                }
                break;

            case "list":
                if (!RequireScanner(scanner)) break;
                {
                    int n = int.TryParse(arg, out int nn) ? nn : 20;
                    ListCandidates(scanner!, n);
                }
                break;

            case "watch":
                if (!RequireScanner(scanner)) break;
                Watch(scanner!);
                break;

            case "near":
                if (!RequireScanner(scanner)) break;
                DumpNear(scanner!, arg);
                break;

            case "findstr":
                if (!RequireScanner(scanner)) break;
                FindStr(scanner!, arg, unicode: false);
                break;

            case "findstr16":
                if (!RequireScanner(scanner)) break;
                FindStr(scanner!, arg, unicode: true);
                break;

            case "read":
                if (!RequireScanner(scanner)) break;
                ReadAt(scanner!, arg);
                break;

            case "findptr":
                if (!RequireScanner(scanner)) break;
                FindPtr(scanner!, arg);
                break;

            case "findptrrange":
                if (!RequireScanner(scanner)) break;
                FindPtrRange(scanner!, arg);
                break;

            case "clear":
                scanner?.Candidates.Clear();
                Console.WriteLine("Seznam kandidátů vyprázdněn.");
                break;

            case "quit":
            case "exit":
                scanner?.Dispose();
                return;

            default:
                Console.WriteLine("Neznámý příkaz. Napiš 'help'.");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Chyba: {ex.Message}");
    }
}

static void PrintHelp()
{
    Console.WriteLine("""
        attach <jméno-procesu|PID>   - připojí se k běžícímu client.exe (nebo napiš PID)
        scan <číslo>                  - první scan: najde všechny výskyty přesné hodnoty (4B, zarovnané)
        next <číslo>                  - zúží seznam kandidátů na ty, co teď mají tuhle novou hodnotu
        list [n]                      - vypíše až n kandidátů (default 20) s jejich aktuální hodnotou
        watch                         - živě sleduje hodnoty všech kandidátů (Enter = konec)
        near <adresa> [bajtů]         - hex dump paměti kolem adresy (default 64 bajtů, po 4 na obě strany)
        findstr <text>                - najde ASCII text v paměti (např. jméno postavy)
        findstr16 <text>              - najde UTF-16 (unicode) text v paměti
        read <adresa> <byte|short|int> - přečte jednu hodnotu na dané adrese
        findptr <adresa>               - najde, co na tuhle adresu ukazuje (hledá stabilní kotvu
                                          v modulu hry, ne v proměnlivé heap paměti)
        clear                         - vyprázdní seznam kandidátů
        quit                          - konec

        Adresy piš jako hex, např. 0x7FF612345678.
        """);
}

static MemoryScanner? Attach(string arg)
{
    Process? proc = null;
    if (int.TryParse(arg, out int pid))
    {
        try { proc = Process.GetProcessById(pid); } catch { proc = null; }
    }
    else
    {
        string name = arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? arg[..^4] : arg;
        var candidates = Process.GetProcessesByName(name);
        if (candidates.Length == 0) { Console.WriteLine($"Proces '{arg}' neběží."); return null; }
        if (candidates.Length > 1)
        {
            Console.WriteLine($"Běží {candidates.Length} procesů '{arg}': {string.Join(", ", candidates.Select(p => p.Id))}. Napiš 'attach <PID>'.");
            return null;
        }
        proc = candidates[0];
    }

    if (proc is null) { Console.WriteLine($"Proces '{arg}' nenalezen."); return null; }

    var scanner = MemoryScanner.Attach(proc.Id);
    if (scanner is null)
    {
        Console.WriteLine($"Nepodařilo se otevřít proces {proc.Id} ({proc.ProcessName}) - zkus spustit tenhle nástroj jako administrátor.");
        return null;
    }

    Console.WriteLine($"Připojeno k {proc.ProcessName}.exe (PID {proc.Id}).");
    return scanner;
}

static bool RequireScanner(MemoryScanner? scanner)
{
    if (scanner is not null) return true;
    Console.WriteLine("Nejdřív se připoj příkazem 'attach <jméno-procesu|PID>'.");
    return false;
}

static void ListCandidates(MemoryScanner scanner, int n)
{
    if (scanner.Candidates.Count == 0) { Console.WriteLine("Žádní kandidáti."); return; }
    foreach (var addr in scanner.Candidates.Take(n))
    {
        int? val = scanner.ReadInt32At(addr);
        Console.WriteLine($"  0x{addr.ToInt64():X}  =  {(val.HasValue ? val.Value.ToString() : "?")}");
    }
    if (scanner.Candidates.Count > n) Console.WriteLine($"  ... a dalších {scanner.Candidates.Count - n}.");
}

static void Watch(MemoryScanner scanner)
{
    Console.WriteLine("Sledování živě (stiskni Enter pro konec)...");
    using var stopSignal = new CancellationTokenSource();
    var readerTask = Task.Run(() => { Console.ReadLine(); stopSignal.Cancel(); });

    while (!stopSignal.IsCancellationRequested)
    {
        var line = string.Join("   ", scanner.Candidates.Take(10).Select(a =>
            $"0x{a.ToInt64():X}={scanner.ReadInt32At(a)?.ToString() ?? "?"}"));
        Console.Write("\r" + line.PadRight(Console.WindowWidth - 1));
        Thread.Sleep(200);
    }
    Console.WriteLine();
}

static void DumpNear(MemoryScanner scanner, string arg)
{
    string[] p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (p.Length < 1 || !TryParseAddress(p[0], out long addr))
    {
        Console.WriteLine("Použití: near <adresa> [počet_bajtů]");
        return;
    }
    int span = p.Length > 1 && int.TryParse(p[1], out int s) ? s : 64;
    long start = addr - span / 2;
    var bytes = scanner.ReadBytes(new IntPtr(start), span);
    if (bytes is null) { Console.WriteLine("Nelze přečíst tuhle oblast paměti."); return; }

    for (int row = 0; row < bytes.Length; row += 16)
    {
        long rowAddr = start + row;
        string hex = string.Join(' ', bytes.Skip(row).Take(16).Select(b => b.ToString("X2")));
        string ascii = new string(bytes.Skip(row).Take(16).Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray());
        string marker = rowAddr <= addr && addr < rowAddr + 16 ? " <-- hledaná adresa" : "";
        Console.WriteLine($"  0x{rowAddr:X}:  {hex,-48}  {ascii}{marker}");
    }
}

static void FindStr(MemoryScanner scanner, string text, bool unicode)
{
    if (string.IsNullOrEmpty(text)) { Console.WriteLine("Použití: findstr[16] <text>"); return; }
    var results = scanner.FindString(text, unicode);
    Console.WriteLine($"Nalezeno {results.Count} výskytů.");
    foreach (var addr in results.Take(20)) Console.WriteLine($"  0x{addr.ToInt64():X}");
    if (results.Count > 20) Console.WriteLine($"  ... a dalších {results.Count - 20}.");
}

static void FindPtr(MemoryScanner scanner, string arg)
{
    if (!TryParseAddress(arg.Trim(), out long target))
    {
        Console.WriteLine("Použití: findptr <adresa>");
        return;
    }

    Process proc;
    try { proc = Process.GetProcessById(scanner.ProcessId); }
    catch { Console.WriteLine("Proces už neběží."); return; }

    IntPtr moduleBase = IntPtr.Zero;
    long moduleSize = 0;
    string moduleName = "?";
    try
    {
        var mainModule = proc.MainModule;
        if (mainModule is not null)
        {
            moduleBase = mainModule.BaseAddress;
            moduleSize = mainModule.ModuleMemorySize;
            moduleName = mainModule.ModuleName;
        }
    }
    catch { /* MainModule can throw for a 32-bit process from a 64-bit caller without extra work - ignore, we just lose the "is this stable" hint */ }

    Console.WriteLine($"Hledám ukazatele na 0x{target:X}... (může to chvíli trvat)");
    var sw = Stopwatch.StartNew();
    var pointers = scanner.FindPointersTo(new IntPtr(target));
    Console.WriteLine($"Nalezeno {pointers.Count} ukazatelů za {sw.Elapsed.TotalSeconds:F1}s.");

    foreach (var addr in pointers.Take(20))
    {
        long a = addr.ToInt64();
        bool inModule = moduleBase != IntPtr.Zero && a >= moduleBase.ToInt64() && a < moduleBase.ToInt64() + moduleSize;
        string note = inModule
            ? $" <-- STABILNÍ (uvnitř {moduleName}, offset +0x{a - moduleBase.ToInt64():X})"
            : " (v heap paměti - nestabilní mezi restarty)";
        Console.WriteLine($"  0x{a:X}{note}");
    }
    if (pointers.Count > 20) Console.WriteLine($"  ... a dalších {pointers.Count - 20}.");
}

static void FindPtrRange(MemoryScanner scanner, string arg)
{
    string[] p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (p.Length < 2 || !TryParseAddress(p[0], out long lo) || !TryParseAddress(p[1], out long hi))
    {
        Console.WriteLine("Použití: findptrrange <od> <do>  (hex adresy)");
        return;
    }

    Process? proc = null;
    try { proc = Process.GetProcessById(scanner.ProcessId); } catch { }
    IntPtr moduleBase = IntPtr.Zero; long moduleSize = 0; string moduleName = "?";
    try
    {
        var mm = proc?.MainModule;
        if (mm is not null) { moduleBase = mm.BaseAddress; moduleSize = mm.ModuleMemorySize; moduleName = mm.ModuleName; }
    }
    catch { }

    Console.WriteLine($"Hledám ukazatele v rozsahu 0x{lo:X}-0x{hi:X}...");
    var sw = Stopwatch.StartNew();
    var results = scanner.FindPointersInRange(lo, hi);
    Console.WriteLine($"Nalezeno {results.Count} za {sw.Elapsed.TotalSeconds:F1}s.");

    foreach (var (loc, val) in results.Take(30))
    {
        long a = loc.ToInt64();
        bool inModule = moduleBase != IntPtr.Zero && a >= moduleBase.ToInt64() && a < moduleBase.ToInt64() + moduleSize;
        string note = inModule ? $" <-- STABILNÍ ({moduleName} +0x{a - moduleBase.ToInt64():X})" : "";
        Console.WriteLine($"  0x{a:X} -> 0x{val:X}{note}");
    }
    if (results.Count > 30) Console.WriteLine($"  ... a dalších {results.Count - 30}.");
}

static void ReadAt(MemoryScanner scanner, string arg)
{
    string[] p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (p.Length < 2 || !TryParseAddress(p[0], out long addr))
    {
        Console.WriteLine("Použití: read <adresa> <byte|short|int>");
        return;
    }
    int size = p[1].ToLowerInvariant() switch { "byte" => 1, "short" => 2, "int" => 4, "long" or "ptr" => 8, _ => 0 };
    if (size == 0) { Console.WriteLine("Typ musí být byte, short, int nebo long/ptr."); return; }

    var bytes = scanner.ReadBytes(new IntPtr(addr), size);
    if (bytes is null) { Console.WriteLine("Nelze přečíst."); return; }
    long value = size switch
    {
        1 => bytes[0],
        2 => BitConverter.ToInt16(bytes, 0),
        4 => BitConverter.ToInt32(bytes, 0),
        _ => BitConverter.ToInt64(bytes, 0),
    };
    Console.WriteLine(size == 8 ? $"0x{addr:X} = 0x{value:X}" : $"0x{addr:X} = {value}");
}

static bool TryParseAddress(string s, out long addr)
{
    s = s.Trim();
    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
    return long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out addr);
}

static void RunNonInteractive(string statePath, string command, string arg)
{
    var saved = LoadState(statePath);
    MemoryScanner? scanner = null;
    if (saved is not null)
    {
        scanner = MemoryScanner.Attach(saved.Pid);
        if (scanner is null)
            Console.WriteLine($"Proces PID {saved.Pid} z uloženého stavu už neběží - připoj se znovu ('attach').");
        else
            foreach (string hex in saved.Candidates) scanner.Candidates.Add(new IntPtr(Convert.ToInt64(hex, 16)));
    }

    switch (command.ToLowerInvariant())
    {
        case "attach":
            scanner?.Dispose();
            scanner = Attach(arg);
            break;

        case "scan":
            if (!RequireScanner(scanner)) break;
            if (!int.TryParse(arg, out int sv)) { Console.WriteLine("Použití: scan <číslo>"); break; }
            {
                var sw = Stopwatch.StartNew();
                int c = scanner!.ScanInt32(sv);
                Console.WriteLine($"Nalezeno {c} kandidátů za {sw.Elapsed.TotalSeconds:F1}s.");
            }
            break;

        case "next":
            if (!RequireScanner(scanner)) break;
            if (!int.TryParse(arg, out int nv)) { Console.WriteLine("Použití: next <číslo>"); break; }
            {
                int before = scanner!.Candidates.Count;
                int c = scanner.NarrowInt32(nv);
                Console.WriteLine($"{before} -> {c} kandidátů.");
            }
            break;

        case "list":
            if (!RequireScanner(scanner)) break;
            ListCandidates(scanner!, int.TryParse(arg, out int n) ? n : 20);
            break;

        case "near":
            if (!RequireScanner(scanner)) break;
            DumpNear(scanner!, arg);
            break;

        case "findstr":
            if (!RequireScanner(scanner)) break;
            FindStr(scanner!, arg, unicode: false);
            break;

        case "findstr16":
            if (!RequireScanner(scanner)) break;
            FindStr(scanner!, arg, unicode: true);
            break;

        case "read":
            if (!RequireScanner(scanner)) break;
            ReadAt(scanner!, arg);
            break;

        case "findptr":
            if (!RequireScanner(scanner)) break;
            FindPtr(scanner!, arg);
            break;

        case "findptrrange":
            if (!RequireScanner(scanner)) break;
            FindPtrRange(scanner!, arg);
            break;

        default:
            Console.WriteLine("Neznámý příkaz.");
            break;
    }

    if (scanner is not null)
        SaveState(statePath, new ScanState(scanner.ProcessId, scanner.Candidates.Select(a => "0x" + a.ToInt64().ToString("X")).ToList()));
    scanner?.Dispose();
}

static ScanState? LoadState(string path)
{
    if (!File.Exists(path)) return null;
    try { return JsonSerializer.Deserialize<ScanState>(File.ReadAllText(path)); }
    catch { return null; }
}

static void SaveState(string path, ScanState state) => File.WriteAllText(path, JsonSerializer.Serialize(state));

/// <summary>Not for real use - exercises ScanInt32/NarrowInt32/ReadInt32At end-to-end against a
/// throwaway target process that holds one known value at a fixed unmanaged address (so it
/// behaves like a real native process's own memory, not a GC-managed .NET object that could move).
/// Validates the scan/narrow mechanics work in this environment BEFORE using them on the real
/// classic UO client. Usage: selftest &lt;path to a scan-target exe&gt;.</summary>
static void SelfTest(string targetExePath)
{
    if (string.IsNullOrWhiteSpace(targetExePath) || !File.Exists(targetExePath))
    {
        Console.WriteLine("Použití: selftest <cesta k testovacímu .exe>");
        return;
    }

    int failures = 0;
    void Check(string name, bool cond)
    {
        Console.WriteLine(cond ? $"OK   {name}" : $"FAIL {name}");
        if (!cond) failures++;
    }

    var psi = new ProcessStartInfo(targetExePath) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
    using var target = Process.Start(psi);
    if (target is null) { Console.WriteLine("Nepodařilo se spustit testovací proces."); return; }

    try
    {
        string? pidLine = target.StandardOutput.ReadLine(); // "PID: <n>"
        _ = target.StandardOutput.ReadLine(); // "Value address: 0x...", not needed - we find it by scanning
        _ = target.StandardOutput.ReadLine(); // "Commands: ..."
        int targetPid = int.Parse(pidLine!.Split(':')[1].Trim());

        using var ms = MemoryScanner.Attach(targetPid);
        Check("Attach k testovacímu procesu", ms is not null);
        if (ms is null) return;

        int firstCount = ms.ScanInt32(1000);
        Check("Prvotní scan (hodnota 1000) najde aspoň 1 kandidáta", firstCount >= 1);

        target.StandardInput.WriteLine("set 1234");
        target.StandardInput.Flush();
        target.StandardOutput.ReadLine(); // "Value is now 1234."

        int narrowedCount = ms.NarrowInt32(1234);
        Check("Po změně na 1234 zúžení najde přesně 1 kandidáta", narrowedCount == 1);

        if (ms.Candidates.Count == 1)
        {
            int? val = ms.ReadInt32At(ms.Candidates[0]);
            Check("Hodnota na nalezené adrese je skutečně 1234", val == 1234);
        }

        // Confirm it keeps tracking a further change too, not just a one-off coincidence.
        target.StandardInput.WriteLine("set 987654");
        target.StandardInput.Flush();
        target.StandardOutput.ReadLine();
        int trackedCount = ms.NarrowInt32(987654);
        Check("Sleduje i další změnu (987654)", trackedCount == 1 && ms.Candidates.Count == 1);

        Console.WriteLine(failures == 0 ? "\nSELFTEST: VŠE PROŠLO" : $"\nSELFTEST: {failures} SELHÁNÍ");
    }
    finally
    {
        try { target.StandardInput.WriteLine("quit"); target.StandardInput.Flush(); } catch { }
        if (!target.WaitForExit(1000)) target.Kill();
    }
}

record ScanState(int Pid, List<string> Candidates);
