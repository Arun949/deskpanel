using System.Text.RegularExpressions;

namespace DeskPanel.Agent.Security;

/// <summary>Checks <c>Authorization: Device &lt;id&gt;.&lt;key&gt;</c> against the enrolled devices.</summary>
public sealed partial class DeviceKeyAuthenticator(DeviceStore store) : IDeviceAuthenticator
{
    public string? Authenticate(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var m = HeaderRe().Match(header);
        if (!m.Success) return null;
        return store.Verify(m.Groups["id"].Value, m.Groups["key"].Value)?.Id;
    }

    // id: 16 hex characters; key: 32 bytes as base64url (43 characters)
    [GeneratedRegex("^Device (?<id>[0-9a-f]{16})\\.(?<key>[A-Za-z0-9_-]{43})$")]
    private static partial Regex HeaderRe();
}
