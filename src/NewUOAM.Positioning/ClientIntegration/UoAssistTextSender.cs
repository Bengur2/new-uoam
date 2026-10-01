using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace NewUOAM.Positioning.ClientIntegration;

/// <summary>Shows text inside a running UO client as a local system message (bottom-left, like a
/// sysmsg) through the **UOAssist API** - exactly how the original UOAM did it. UOAM itself never
/// injected anything into the client: it sent a Windows message to whatever assistant exposed the
/// UOAssist API (originally UOAssist, later Razor), and the assistant displayed the text locally.
/// Nothing reaches the game server, so only the local player sees it.
///
/// The wire contract (documented in Razor's "UOAssist API" docs, handled in Razor's
/// UOAssist.cs): find the window with class <see cref="WindowClass"/>, put the text in a global
/// atom, send <c>WM_USER+207</c> (DISPLAY_TEXT) with wParam = hue (low word) | 0x10000 (system
/// message, instead of over the player's head) and lParam = the atom. The receiver frees the
/// atom. Max 255 characters per message (atom limit).
///
/// On this user's setup the API is provided by **Orion Assistant** (OA, closed-source, loaded
/// inside OrionUO64.exe) - it creates the <see cref="WindowClass"/> window (plus a "UOAM UO Fake
/// Window") for old-UOAM compatibility. Verified live 2026-09-23: returns 1, frees the atom, the
/// text appears bottom-left as a system message, nothing overhead. OA's in-game font dropped
/// Czech diacritics ("příliš" rendered with gaps), hence <see cref="ToGameSafeText"/>.
///
/// Zero writes to the client's memory, no injection, no hooks - just a documented window message
/// to an API the assistant exposes for exactly this purpose.</summary>
public static class UoAssistTextSender
{
    public const string WindowClass = "UOASSIST-TP-MSG-WND";
    private const uint WM_USER = 0x0400;
    private const uint UOA_DISPLAY_TEXT = WM_USER + 207;
    private const int SystemMessageFlag = 0x10000;
    private const int MaxAtomLength = 255;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SendTimeoutMs = 1000;

    /// <summary>Default hue (UO palette index, not RGB) of map chat in the game. Turquoise (user's request 2026-09-29, was green 0x0044): the hue in the DP client's
    /// hues.mul closest to #40E0D0 (its ramp reads ~#3AD6C5). A hue outside the stock 0x0001-0x03E9
    /// range, so it relies on the shard's hues.mul; 0x005A is the nearest stock one.</summary>
    public const ushort DefaultHue = 0x05BA;

    /// <summary>The UOAssist window belonging to one specific process (so multiboxing targets the
    /// right client), or IntPtr.Zero if that process doesn't expose the API (no assistant loaded,
    /// still at the login screen, ...).</summary>
    public static IntPtr FindWindowForProcess(int processId)
    {
        IntPtr found = IntPtr.Zero;
        var className = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != processId) return true;
            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() != WindowClass) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Process ids of every running process that exposes a UOAssist window.</summary>
    public static IReadOnlyList<int> FindAllProcessIds()
    {
        var pids = new List<int>();
        var className = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() == WindowClass)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                pids.Add((int)pid);
            }
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    /// <summary>Displays <paramref name="text"/> as a system message in the given UOAssist
    /// window, splitting it into several messages if it's longer than one atom allows. Blocks for
    /// at most ~1s per message (SendMessageTimeout) - call off the UI thread. Returns false if the
    /// window didn't accept it (gone, hung, or not a UOAssist receiver).</summary>
    public static bool DisplaySystemText(IntPtr uoAssistWindow, string text, ushort hue = DefaultHue) =>
        DisplayText(uoAssistWindow, text, hue, overhead: false);

    /// <summary>Hue of UOAM's own overhead "Shared Marker / NorthEast" text (light blue).</summary>
    public const ushort SharedMarkerHue = 0x0059;

    /// <summary>Same DISPLAY_TEXT message WITHOUT the system-message flag: the assistant shows it
    /// over the player's own head instead (Razor's UOAssist.cs: flag set -> sysmsg from "System",
    /// flag clear -> World.Player.OverheadMessage). That's how old UOAM showed "Shared Marker" +
    /// the direction when a shared marker was dropped.</summary>
    public static bool DisplayOverheadText(IntPtr uoAssistWindow, string text, ushort hue = SharedMarkerHue) =>
        DisplayText(uoAssistWindow, text, hue, overhead: true);

    private static bool DisplayText(IntPtr uoAssistWindow, string text, ushort hue, bool overhead)
    {
        if (uoAssistWindow == IntPtr.Zero) return false;
        bool allOk = true;
        int flags = overhead ? 0 : SystemMessageFlag;
        foreach (string chunk in Split(ToGameSafeText(text), MaxAtomLength))
        {
            ushort atom = GlobalAddAtom(chunk);
            if (atom == 0) return false;
            IntPtr sent = SendMessageTimeout(uoAssistWindow, UOA_DISPLAY_TEXT, (IntPtr)(hue | flags), (IntPtr)atom,
                SMTO_ABORTIFHUNG, SendTimeoutMs, out IntPtr result);
            if (sent == IntPtr.Zero || result == IntPtr.Zero)
            {
                // The receiver only frees the atom when it handled the message.
                GlobalDeleteAtom(atom);
                allOk = false;
            }
        }
        return allOk;
    }

    /// <summary>Strips diacritics ("příliš žluťoučký" → "prilis zlutoucky") and anything outside
    /// printable ASCII, since the in-game font OA prints with doesn't render them (verified
    /// live). Also removes control characters so a chat message can't inject line breaks.</summary>
    public static string ToGameSafeText(string text)
    {
        var sb = new StringBuilder(text.Length);
        string decomposed = text.Normalize(NormalizationForm.FormD);
        for (int i = 0; i < decomposed.Length; i++)
        {
            char ch = decomposed[i];
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch is >= ' ' and <= '~') sb.Append(ch);
            else if (char.IsWhiteSpace(ch)) sb.Append(' ');
            else if (AsciiReplacements.TryGetValue(ch, out string? replacement)) sb.Append(replacement);
            else
            {
                // One '?' per unrepresentable character - an emoji is a surrogate PAIR, skip its
                // second half so it doesn't become "??".
                if (char.IsHighSurrogate(ch) && i + 1 < decomposed.Length && char.IsLowSurrogate(decomposed[i + 1])) i++;
                sb.Append('?');
            }
        }
        return sb.ToString();
    }

    // Characters NFD can't reduce to ASCII but that have an obvious ASCII stand-in. Mostly the
    // typographic punctuation phones/Word insert automatically (Czech „quotes“, dashes, ellipsis),
    // which would otherwise all turn into '?', plus the few non-decomposing Latin letters.
    private static readonly Dictionary<char, string> AsciiReplacements = new()
    {
        ['„'] = "\"", ['“'] = "\"", ['”'] = "\"", ['«'] = "\"", ['»'] = "\"",
        ['‚'] = "'", ['‘'] = "'", ['’'] = "'", ['‹'] = "'", ['›'] = "'",
        ['–'] = "-", ['—'] = "-", ['−'] = "-", ['…'] = "...", ['•'] = "*", ['×'] = "x",
        ['€'] = "EUR", ['°'] = "st.",
        ['ł'] = "l", ['Ł'] = "L", ['đ'] = "d", ['Đ'] = "D", ['ø'] = "o", ['Ø'] = "O",
        ['ß'] = "ss", ['æ'] = "ae", ['Æ'] = "AE", ['œ'] = "oe", ['Œ'] = "OE",
    };

    private static IEnumerable<string> Split(string text, int max)
    {
        if (text.Length == 0) yield break;
        for (int i = 0; i < text.Length; i += max)
            yield return text.Substring(i, Math.Min(max, text.Length - i));
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort GlobalAddAtom(string text);

    [DllImport("kernel32.dll")]
    private static extern ushort GlobalDeleteAtom(ushort atom);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);
}
