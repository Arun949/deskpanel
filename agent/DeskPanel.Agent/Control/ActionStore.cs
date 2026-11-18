using System.Text.Json;
using System.Text.RegularExpressions;
using DeskPanel.Agent.Core;
using DeskPanel.Agent.Security;

namespace DeskPanel.Agent.Control;

/// <summary>
/// A user-defined action, stored only on the PC (actions.json). The phone only ever sends its <see cref="Id"/>.
/// <list type="bullet">
/// <item><c>launch</c>: <see cref="Target"/> is an app bundle chosen in a macOS dialog; it is opened with no arguments.</item>
/// <item><c>keys</c>: <see cref="Target"/> is a key combination like <c>cmd+shift+4</c>, from a fixed key table.</item>
/// </list>
/// There is deliberately no "run a command" kind (PhoneBox's arbitrary-command path, Module 7).
/// </summary>
public sealed record ActionDef(string Id, string Label, string Kind, string Target, string? Icon = null)
{
    [System.Text.Json.Serialization.JsonIgnore] // derived from Kind; not stored, not sent
    public string Permission => Kind == "launch" ? Security.Permission.AppsLaunch : Security.Permission.KeysSend;
}

/// <summary>What the phone may see about an action: no target.</summary>
public sealed record PublicAction(string Id, string Label, string Kind, string? Icon);

public sealed partial class ActionStore
{
    public const int MaxActions = 64;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly Lock _lock = new();
    private List<ActionDef> _actions;

    public ActionStore(AgentPaths paths)
    {
        _path = Path.Combine(paths.Home, "actions.json");
        _actions = Load(_path);
    }

    public IReadOnlyList<ActionDef> All() { lock (_lock) return _actions.ToList(); }

    public ActionDef? Get(string id) { lock (_lock) return _actions.FirstOrDefault(a => a.Id == id); }

    /// <summary>Replaces the list. Returns an error per rejected entry; valid entries are kept.</summary>
    public IReadOnlyList<string> Replace(IEnumerable<ActionDef> actions)
    {
        var errors = new List<string>();
        var kept = new List<ActionDef>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in actions.Take(MaxActions))
        {
            var error = Validate(a);
            if (error is null && !ids.Add(a.Id)) error = $"duplicate id '{a.Id}'";
            if (error is null) kept.Add(Normalize(a));
            else errors.Add(error);
        }
        lock (_lock)
        {
            _actions = kept;
            PrivateFile.WriteAtomic(_path, JsonSerializer.Serialize(_actions, Json));
        }
        return errors;
    }

    /// <summary>Null when valid. Launch targets must be an existing .app bundle given as an absolute path.</summary>
    public static string? Validate(ActionDef? a)
    {
        if (a is null) return "empty action";
        if (!IdRe().IsMatch(a.Id ?? "")) return $"id '{a.Id}' must be 1–40 lowercase letters, digits or '-'";
        if (string.IsNullOrWhiteSpace(a.Label) || a.Label.Length > 40) return $"'{a.Id}': label must be 1–40 characters";
        if (a.Icon is { Length: > 8 }) return $"'{a.Id}': icon is too long";
        return a.Kind switch
        {
            "launch" => IsAppBundle(a.Target) ? null : $"'{a.Id}': choose an app (an existing .app bundle)",
            "keys" => KeySpec.Parse(a.Target ?? "") is null ? $"'{a.Id}': unknown key combination '{a.Target}'" : null,
            _ => $"'{a.Id}': kind must be 'launch' or 'keys'",
        };
    }

    public static bool IsAppBundle(string? path) =>
        path is { Length: > 5 and < 1024 }
        && Path.IsPathFullyQualified(path)
        && path.EndsWith(".app", StringComparison.Ordinal)
        && !path.Contains("..", StringComparison.Ordinal)
        && Directory.Exists(path);

    private static ActionDef Normalize(ActionDef a) =>
        a with { Label = a.Label.Trim(), Target = a.Kind == "keys" ? KeySpec.Parse(a.Target)!.Canonical : a.Target };

    private static List<ActionDef> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<ActionDef>>(File.ReadAllText(path), Json) ?? [];
            // re-validated on load: a hand edit can't introduce a target the editor would refuse
            return [.. list.Where(a => Validate(a) is null).DistinctBy(a => a.Id).Take(MaxActions)];
        }
        catch (JsonException) { return []; }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IdRe();
}
