using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.Security;

/// <summary>
/// Keeps web pages out of the local API, before any route-level check:
///  - Host must be 127.0.0.1 or localhost (blocks DNS rebinding);
///  - Origin, when present, must be this agent's own origin (blocks cross-site fetch/XHR);
///  - Sec-Fetch-Site cross-site / same-site is refused (blocks no-cors requests, &lt;img&gt;, links, forms that send no Origin).
/// This only stops browsers. Native clients send none of these headers, which is why every route also needs a
/// <see cref="TrustClass"/> check (see <see cref="TrustGate"/>).
/// </summary>
public sealed class BrowserGuard(RequestDelegate next, AgentOptions options)
{
    private readonly string[] _sameOrigins =
        [$"http://127.0.0.1:{options.Port}", $"http://localhost:{options.Port}"];

    public Task InvokeAsync(HttpContext context)
    {
        if (!IsAllowed(context.Request.Host.Host, context.Request.Headers.Origin.ToString(),
                context.Request.Headers["Sec-Fetch-Site"].ToString(), _sameOrigins))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        return next(context);
    }

    internal static bool IsAllowed(string host, string origin, string secFetchSite, IReadOnlyCollection<string> sameOrigins)
    {
        if (host is not ("127.0.0.1" or "localhost")) return false;
        if (origin.Length > 0 && !sameOrigins.Contains(origin, StringComparer.Ordinal)) return false;
        if (secFetchSite is "cross-site" or "same-site") return false;
        return true;
    }
}
