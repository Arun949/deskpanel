namespace DeskPanel.Agent.Control;

/// <summary>
/// A key combination such as <c>cmd+shift+4</c>, parsed against fixed tables. Only table values ever reach AppleScript,
/// never the user's text. Exactly one non-modifier key per combination.
/// </summary>
public sealed record KeySpec(IReadOnlyList<string> Modifiers, string Key, int KeyCode)
{
    public string Canonical => string.Join('+', Modifiers.Append(Key));

    /// <summary>The AppleScript statement for System Events, e.g. <c>key code 21 using {command down, shift down}</c>.</summary>
    public string AppleScript => Modifiers.Count == 0
        ? $"key code {KeyCode}"
        : $"key code {KeyCode} using {{{string.Join(", ", Modifiers.Select(m => ModifierNames[m]))}}}";

    private static readonly Dictionary<string, string> ModifierAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cmd"] = "cmd", ["command"] = "cmd", ["⌘"] = "cmd",
        ["ctrl"] = "ctrl", ["control"] = "ctrl",
        ["alt"] = "alt", ["option"] = "alt", ["opt"] = "alt",
        ["shift"] = "shift",
    };

    private static readonly Dictionary<string, string> ModifierNames = new()
    {
        ["cmd"] = "command down", ["ctrl"] = "control down", ["alt"] = "option down", ["shift"] = "shift down",
    };

    private static readonly string[] ModifierOrder = ["cmd", "ctrl", "alt", "shift"];

    // macOS virtual key codes (Carbon kVK_*), ANSI layout.
    private static readonly Dictionary<string, int> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 0, ["s"] = 1, ["d"] = 2, ["f"] = 3, ["h"] = 4, ["g"] = 5, ["z"] = 6, ["x"] = 7, ["c"] = 8, ["v"] = 9,
        ["b"] = 11, ["q"] = 12, ["w"] = 13, ["e"] = 14, ["r"] = 15, ["y"] = 16, ["t"] = 17, ["o"] = 31, ["u"] = 32,
        ["i"] = 34, ["p"] = 35, ["l"] = 37, ["j"] = 38, ["k"] = 40, ["n"] = 45, ["m"] = 46,
        ["1"] = 18, ["2"] = 19, ["3"] = 20, ["4"] = 21, ["5"] = 23, ["6"] = 22, ["7"] = 26, ["8"] = 28, ["9"] = 25, ["0"] = 29,
        ["return"] = 36, ["enter"] = 36, ["tab"] = 48, ["space"] = 49, ["delete"] = 51, ["backspace"] = 51,
        ["escape"] = 53, ["esc"] = 53, ["left"] = 123, ["right"] = 124, ["down"] = 125, ["up"] = 126,
        ["home"] = 115, ["end"] = 119, ["pageup"] = 116, ["pagedown"] = 121,
        ["f1"] = 122, ["f2"] = 120, ["f3"] = 99, ["f4"] = 118, ["f5"] = 96, ["f6"] = 97, ["f7"] = 98, ["f8"] = 100,
        ["f9"] = 101, ["f10"] = 109, ["f11"] = 103, ["f12"] = 111,
        ["-"] = 27, ["="] = 24, ["["] = 33, ["]"] = 30, [";"] = 41, ["'"] = 39, [","] = 43, ["."] = 47, ["/"] = 44, ["`"] = 50,
    };

    public static KeySpec? Parse(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || spec.Length > 60) return null;
        var parts = spec.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(p => p.Length == 0)) return null;
        var modifiers = new HashSet<string>();
        string? key = null;
        foreach (var p in parts)
        {
            if (ModifierAliases.TryGetValue(p, out var m)) { if (!modifiers.Add(m)) return null; continue; }
            if (key is not null || !Keys.ContainsKey(p)) return null; // one key, from the table
            key = p.ToLowerInvariant();
        }
        if (key is null) return null;
        return new KeySpec([.. ModifierOrder.Where(modifiers.Contains)], key, Keys[key]);
    }
}
