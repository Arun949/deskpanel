using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.Security;

/// <summary>
/// One enrolled phone. The key itself is never stored on the PC: only two values derived from it, one to check requests
/// (<see cref="AuthHash"/>) and one to prove to the phone that it reached this agent (<see cref="ProofKey"/>).
/// </summary>
public sealed record EnrolledDevice(
    string Id,
    string Serial,
    string Model,
    string AuthHash,
    string ProofKey,
    DateTimeOffset CreatedAt,
    bool Revoked = false,
    bool Wifi = false,
    string? ClientSpki = null);
// Wifi/ClientSpki (docs/design/WIFI.md §3.1): the phone may connect over Wi-Fi with the Keystore key whose public-key
// hash is ClientSpki. Older devices.json files simply lack them (false/null: USB only).

/// <summary>Per-device keys (PhoneBox shared one token for every phone, Module 4). Stored owner-only in devices.json.</summary>
public sealed class DeviceStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly Lock _lock = new();
    private List<EnrolledDevice> _devices;

    /// <summary>Raised after any enrol, revoke or Wi-Fi change (the Wi-Fi listener opens or closes on it).</summary>
    public event Action? Changed;

    public DeviceStore(AgentPaths paths)
    {
        _path = paths.Devices;
        _devices = Load(_path);
    }

    /// <summary>Creates a device with a fresh 256-bit key. Returns the key once; it is not kept.</summary>
    public (EnrolledDevice Device, string Key) Enrol(string serial, string model)
    {
        var key = Base64Url(RandomNumberGenerator.GetBytes(32));
        var device = new EnrolledDevice(
            Id: Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            Serial: serial,
            Model: model,
            AuthHash: Derive("auth", key),
            ProofKey: Derive("proof", key),
            CreatedAt: DateTimeOffset.UtcNow);
        lock (_lock)
        {
            // one active enrolment per phone: a re-enrolment revokes the old key
            _devices = [.. _devices.Select(d => d.Serial == serial && !d.Revoked ? d with { Revoked = true } : d), device];
            Save();
        }
        Changed?.Invoke();
        return (device, key);
    }

    public void Revoke(string id)
    {
        lock (_lock)
        {
            _devices = [.. _devices.Select(d => d.Id == id ? d with { Revoked = true } : d)];
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Turns Wi-Fi on for a device with the public-key hash of its Keystore client key, or off (null). Off forgets the
    /// key hash, so that phone's next Wi-Fi handshake fails. Returns false for an unknown or revoked device.
    /// </summary>
    public bool SetWifi(string id, string? clientSpki)
    {
        lock (_lock)
        {
            if (!_devices.Any(d => d.Id == id && !d.Revoked)) return false;
            _devices = [.. _devices.Select(d => d.Id == id ? d with { Wifi = clientSpki is not null, ClientSpki = clientSpki } : d)];
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>The active device allowed on Wi-Fi with this client key, or null. Used during the TLS handshake.</summary>
    public EnrolledDevice? ActiveOnWifi(string clientSpki)
    {
        lock (_lock)
            return _devices.FirstOrDefault(d => !d.Revoked && d.Wifi && d.ClientSpki is { } k
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(k), Encoding.ASCII.GetBytes(clientSpki)));
    }

    /// <summary>True while at least one active device may use Wi-Fi (the listener runs only then).</summary>
    public bool AnyOnWifi() { lock (_lock) return _devices.Any(d => !d.Revoked && d.Wifi && d.ClientSpki is not null); }

    public IReadOnlyList<EnrolledDevice> All() { lock (_lock) return _devices.ToList(); }

    public EnrolledDevice? Active(string id)
    {
        lock (_lock) return _devices.FirstOrDefault(d => d.Id == id && !d.Revoked);
    }

    public EnrolledDevice? ActiveForSerial(string serial)
    {
        lock (_lock) return _devices.LastOrDefault(d => d.Serial == serial && !d.Revoked);
    }

    /// <summary>The device a request's key belongs to, or null. Constant-time comparison.</summary>
    public EnrolledDevice? Verify(string id, string key)
    {
        var device = Active(id);
        if (device is null) return null;
        var given = Encoding.ASCII.GetBytes(Derive("auth", key));
        var stored = Encoding.ASCII.GetBytes(device.AuthHash);
        return CryptographicOperations.FixedTimeEquals(given, stored) ? device : null;
    }

    /// <summary>
    /// HMAC proving this agent knows the device's key, for <c>/api/v1/hello</c>. The phone computes the same value and
    /// refuses to talk to anything on 127.0.0.1:8765 that can't produce it (port squatting, Module 3).
    /// </summary>
    public static string Proof(EnrolledDevice device, string nonce, string runId)
    {
        var message = Encoding.UTF8.GetBytes($"deskpanel-proof|v1|{device.Id}|{nonce}|{runId}");
        return Base64Url(HMACSHA256.HashData(Convert.FromHexString(device.ProofKey), message));
    }

    /// <summary>SHA-256 over a purpose label and the key, as lowercase hex. The key has 256 random bits, so no slow KDF is needed.</summary>
    public static string Derive(string purpose, string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"deskpanel|{purpose}|{key}")));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void Save() => PrivateFile.WriteAtomic(_path, JsonSerializer.Serialize(_devices, Json));

    private static List<EnrolledDevice> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<EnrolledDevice>>(File.ReadAllText(path), Json) ?? []; }
        catch (JsonException) { return []; } // a damaged file means no devices: fail closed, re-enrol over USB
    }
}
