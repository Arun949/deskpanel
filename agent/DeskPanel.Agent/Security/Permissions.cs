using System.Text.Json;
using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.Security;

/// <summary>
/// What a phone may do on this computer. Reading state is always allowed for an enrolled phone; every control is a
/// separate grant, and every grant starts off (PhoneBox derived rights from widget options that defaulted on, Module 2).
/// </summary>
public static class Permission
{
    public const string MediaControl = "media.control";
    public const string VolumeControl = "volume.control";
    public const string AppsLaunch = "apps.launch";
    public const string KeysSend = "keys.send";

    public static readonly IReadOnlyList<string> All = [MediaControl, VolumeControl, AppsLaunch, KeysSend];

    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [MediaControl] = "Play, pause and skip tracks",
        [VolumeControl] = "Change the output volume and mute",
        [AppsLaunch] = "Open the apps you list under Actions",
        [KeysSend] = "Press the key combinations you list under Actions",
    };
}

/// <summary>Grants per device id, in permissions.json (owner-only). Unknown permission names are dropped on load and save.</summary>
public sealed class PermissionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly Lock _lock = new();
    private Dictionary<string, HashSet<string>> _grants;

    public PermissionStore(AgentPaths paths)
    {
        _path = Path.Combine(paths.Home, "permissions.json");
        _grants = Load(_path);
    }

    public bool Has(string deviceId, string permission)
    {
        lock (_lock) return _grants.TryGetValue(deviceId, out var set) && set.Contains(permission);
    }

    public IReadOnlyList<string> For(string deviceId)
    {
        lock (_lock) return _grants.TryGetValue(deviceId, out var set) ? [.. set.Order(StringComparer.Ordinal)] : [];
    }

    /// <summary>Replaces a device's grants. Returns the names that were accepted.</summary>
    public IReadOnlyList<string> Set(string deviceId, IEnumerable<string> permissions)
    {
        var clean = permissions.Where(Permission.All.Contains).ToHashSet(StringComparer.Ordinal);
        lock (_lock)
        {
            if (clean.Count == 0) _grants.Remove(deviceId);
            else _grants[deviceId] = clean;
            PrivateFile.WriteAtomic(_path, JsonSerializer.Serialize(_grants, Json));
        }
        return [.. clean.Order(StringComparer.Ordinal)];
    }

    /// <summary>A revoked device loses every grant (re-enrolment starts from nothing).</summary>
    public void Clear(string deviceId) => Set(deviceId, []);

    private static Dictionary<string, HashSet<string>> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path), Json) ?? [];
            return raw.ToDictionary(kv => kv.Key, kv => kv.Value.Where(Permission.All.Contains).ToHashSet(StringComparer.Ordinal));
        }
        catch (JsonException) { return []; } // damaged: no grants (fail closed)
    }
}
