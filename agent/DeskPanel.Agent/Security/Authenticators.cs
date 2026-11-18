namespace DeskPanel.Agent.Security;

/// <summary>Checks the per-device key a phone sends (<c>Authorization: Device &lt;id&gt;.&lt;key&gt;</c>).</summary>
public interface IDeviceAuthenticator
{
    /// <returns>The authenticated device id, or null.</returns>
    string? Authenticate(HttpContext context);
}

/// <summary>Checks the settings page's admin session.</summary>
public interface IAdminAuthenticator
{
    bool Authenticate(HttpContext context);
}

/// <summary>
/// M0: no device can be enrolled yet, so every Device request is refused. Replaced in M1 by the adb enrolment store.
/// Fail closed: a missing implementation must never mean "allow".
/// </summary>
internal sealed class NoDevicesAuthenticator : IDeviceAuthenticator
{
    public string? Authenticate(HttpContext context) => null;
}

/// <summary>M0: no admin session exists yet, so every Admin request is refused. Replaced in M2.</summary>
internal sealed class NoAdminAuthenticator : IAdminAuthenticator
{
    public bool Authenticate(HttpContext context) => false;
}
