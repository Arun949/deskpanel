using System.Diagnostics;
using System.Text.RegularExpressions;
using DeskPanel.Agent.Security;

namespace DeskPanel.Agent.Control;

public sealed record ControlResult(bool Ok, string? Error = null);

/// <summary>
/// The one path every control request takes (Module 7's pipeline):
/// device (already authenticated by TrustGate) → permission → rate limit → idempotency → run → audit.
/// </summary>
public sealed partial class ControlGate(PermissionStore permissions, AuditLog audit, TimeProvider time)
{
    internal static readonly TimeSpan IdempotencyWindow = TimeSpan.FromMinutes(2);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (double Tokens, long At)> _buckets = [];
    private readonly Dictionary<string, (ControlResult Result, long Expires)> _seen = [];

    /// <summary>Bucket sizes: key presses are the easiest to abuse, so they get the smallest budget.</summary>
    internal static (double Capacity, double PerSecond) Budget(string permission) =>
        permission == Permission.KeysSend ? (3, 1) : (8, 4);

    /// <param name="onLayout">false when no widget on the phone's layout offers this control: refused (an id the phone
    /// can't show must not be callable, Module 7).</param>
    public async Task<IResult> RunAsync(HttpContext ctx, string action, string permission, bool onLayout,
        Func<CancellationToken, Task<ControlResult>> run)
    {
        var device = ctx.Items[TrustGate.DeviceIdItem] as string ?? throw new InvalidOperationException("no device");
        var started = Stopwatch.GetTimestamp();
        IResult Done(string result, IResult response, string? detail = null)
        {
            audit.Write(new AuditEntry(time.GetUtcNow().ToUnixTimeMilliseconds(), device, action, result,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, detail));
            return response;
        }

        if (!onLayout)
            return Done("not-on-layout", Results.Json(new { error = "not-on-layout" }, statusCode: StatusCodes.Status403Forbidden));

        if (!permissions.Has(device, permission))
            return Done("denied", Results.Json(new { error = "not-allowed", permission }, statusCode: StatusCodes.Status403Forbidden), permission);

        var idemHeader = ctx.Request.Headers["Idempotency-Key"].ToString();
        if (idemHeader.Length > 0 && !IdemRe().IsMatch(idemHeader))
            return Done("failed", Results.BadRequest(new { error = "bad-idempotency-key" }));
        var idem = idemHeader.Length > 0 ? $"{device}|{idemHeader}" : null;

        lock (_lock)
        {
            Sweep();
            if (idem is not null && _seen.TryGetValue(idem, out var prior))
            {
                // the phone retried the same tap: answer the first result, never run twice
                ctx.Response.Headers["Idempotent-Replayed"] = "true";
                return Done("replayed", Respond(prior.Result));
            }
            if (!TakeToken($"{device}|{permission}", permission))
                return Done("rate-limited", Results.Json(new { error = "slow-down" }, statusCode: StatusCodes.Status429TooManyRequests));
        }

        ControlResult result;
        try { result = await run(ctx.RequestAborted); }
        catch (Exception ex) when (ex is not OperationCanceledException) { result = new ControlResult(false, "failed"); }

        if (idem is not null) lock (_lock) _seen[idem] = (result, Now() + (long)IdempotencyWindow.TotalMilliseconds);
        return Done(result.Ok ? "ok" : "failed", Respond(result), result.Error);
    }

    /// <summary>Records an attempt that never reached the gate (an unknown action id).</summary>
    public IResult NotFound(HttpContext ctx, string action)
    {
        var device = ctx.Items[TrustGate.DeviceIdItem] as string ?? "?";
        audit.Write(new AuditEntry(time.GetUtcNow().ToUnixTimeMilliseconds(), device, action, "not-found", 0));
        return Results.NotFound(new { error = "unknown-action" });
    }

    private static IResult Respond(ControlResult r) =>
        r.Ok ? Results.Ok(new { ok = true }) : Results.Json(new { ok = false, error = r.Error ?? "failed" }, statusCode: StatusCodes.Status502BadGateway);

    private bool TakeToken(string bucket, string permission)
    {
        var (capacity, perSecond) = Budget(permission);
        var now = Now();
        var (tokens, at) = _buckets.TryGetValue(bucket, out var b) ? b : (capacity, now);
        tokens = Math.Min(capacity, tokens + (now - at) / 1000.0 * perSecond);
        if (tokens < 1) { _buckets[bucket] = (tokens, now); return false; }
        _buckets[bucket] = (tokens - 1, now);
        return true;
    }

    private void Sweep()
    {
        var now = Now();
        foreach (var key in _seen.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList()) _seen.Remove(key);
    }

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex IdemRe();
}
