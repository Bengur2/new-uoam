using System.Windows;

namespace NewUOAM.App;

/// <summary>
/// UI language of the map's controls (Mapa > Jazyk, user's request 2026-10-01): Czech, Slovak,
/// English. Only controls are translated - menus, dialogs, buttons, labels, tooltips. Status-bar
/// lines, error dialogs, chat/system lines and the texts shown in the game stay Czech (user's
/// call). The texts are in <see cref="UiStrings"/>.
/// <para>XAML uses <c>{DynamicResource Key}</c>: <see cref="Apply"/> writes every key into the
/// application's resources, so switching updates open windows live. Code uses <see cref="T"/> /
/// <see cref="F"/> when it builds a text; texts code keeps on screen are refreshed from
/// <see cref="Changed"/>.</para>
/// </summary>
public static class Loc
{
    public static readonly (string Code, string Name)[] Languages = [("cs", "Čeština"), ("sk", "Slovenčina"), ("en", "English")];

    public const string Default = "cs";

    // Throws on a duplicate key - at startup, so any test run catches it.
    private static readonly Dictionary<string, string[]> Table =
        UiStrings.All.ToDictionary(s => s.Key, s => new[] { s.Cs, s.Sk, s.En });

    private static int _column;

    public static string Current => Languages[_column].Code;

    public static event Action? Changed;

    /// <summary>Normalizes a saved language code; anything unknown is Czech.</summary>
    public static string Normalize(string? code) =>
        Languages.Any(l => l.Code == code) ? code! : Default;

    public static void Apply(string? code)
    {
        _column = Array.FindIndex(Languages, l => l.Code == Normalize(code));
        var resources = Application.Current.Resources;
        foreach (var (key, values) in Table) resources[key] = values[_column];
        Changed?.Invoke();
    }

    public static string T(string key) => Table.TryGetValue(key, out var v) ? v[_column] : key;

    public static string F(string key, params object?[] args) => string.Format(T(key), args);

    /// <summary>Plural form: key + "_1" (1), "_2" (2-4), "_5" (0, 5+) - the Czech/Slovak forms;
    /// English uses the same text for _2 and _5.</summary>
    public static string Plural(string key, int n) =>
        T(key + (n == 1 ? "_1" : n is >= 2 and <= 4 ? "_2" : "_5"));
}
