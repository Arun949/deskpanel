using System.Security.Cryptography;
using System.Text;
using DeskPanel.Agent.Core;
using DeskPanel.Agent.Security;

namespace DeskPanel.Agent.Admin;

/// <summary>
/// Admin access for the settings page, separate from device keys (PhoneBox gave admin power to anything on loopback,
/// Modules 1 and 11). The agent keeps one single-use login link in <c>admin-link</c> (owner-only file): opening it sets an
/// HttpOnly, SameSite=Strict session cookie for 12 hours. A used or expired link is replaced at once, so the file always
/// holds a fresh one. Only someone who can read the PC user's files can log in.
/// </summary>
public sealed class AdminSessions : IAdminAuthenticator
{
    public const string Cookie = "dp_admin";
    internal static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private readonly AgentPaths _paths;
    private readonly AgentOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private (byte[] Token, DateTimeOffset Expires)? _link;
    private readonly Dictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);

    public AdminSessions(AgentPaths paths, AgentOptions options, TimeProvider time)
    {
        (_paths, _options, _time) = (paths, options, time);
        NewLink();
    }

    public string LinkPath => Path.Combine(_paths.Home, "admin-link");

    /// <summary>The current login link (also written to <see cref="LinkPath"/>), renewed if expired.</summary>
    public string CurrentLink()
    {
        lock (_lock)
        {
            if (_link is not { } l || l.Expires <= _time.GetUtcNow()) return NewLink();
            return File.ReadAllText(LinkPath).Trim();
        }
    }

    /// <summary>Trades a link token for a session id, once. Any attempt, right or wrong, burns the current link.</summary>
    public string? Redeem(string? token)
    {
        lock (_lock)
        {
            var valid = token is { Length: 43 } && _link is { } l && l.Expires > _time.GetUtcNow()
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), l.Token);
            NewLink(); // single use; a guess also invalidates the link (no brute force against one token)
            if (!valid) return null;
            var session = DeviceStore.Base64Url(RandomNumberGenerator.GetBytes(32));
            _sessions[session] = _time.GetUtcNow() + SessionLifetime;
            return session;
        }
    }

    public bool Authenticate(HttpContext context)
    {
        var session = context.Request.Cookies[Cookie];
        if (session is null) return false;
        lock (_lock)
        {
            foreach (var expired in _sessions.Where(s => s.Value <= _time.GetUtcNow()).Select(s => s.Key).ToList()) _sessions.Remove(expired);
            return _sessions.ContainsKey(session);
        }
    }

    public void SignOut(string? session)
    {
        if (session is null) return;
        lock (_lock) _sessions.Remove(session);
    }

    private string NewLink()
    {
        var token = DeviceStore.Base64Url(RandomNumberGenerator.GetBytes(32));
        _link = (Encoding.ASCII.GetBytes(token), _time.GetUtcNow() + LinkLifetime);
        var link = $"http://127.0.0.1:{_options.Port}/admin/login?t={token}";
        PrivateFile.WriteAtomic(LinkPath, link + "\n");
        return link;
    }
}
