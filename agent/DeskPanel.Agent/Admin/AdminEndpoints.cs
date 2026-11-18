using System.Reflection;
using DeskPanel.Agent.Control;
using DeskPanel.Agent.Core;
using DeskPanel.Agent.Layout;
using DeskPanel.Agent.Security;

namespace DeskPanel.Agent.Admin;

/// <summary>Asks the PC user to pick an application (a native dialog), so launch targets are never typed or sent by a phone.</summary>
public interface IAppPicker
{
    Task<string?> PickAsync(CancellationToken ct);
}

public sealed class MacAppPicker(IToolRunner tools) : IAppPicker
{
    private const string Script = """
        set a to choose application with title "DeskPanel" with prompt "Choose an app your phone may open:" as alias
        return POSIX path of a
        """;

    public async Task<string?> PickAsync(CancellationToken ct)
    {
        var r = await tools.RunAsync("/usr/bin/osascript", ["-e", Script], TimeSpan.FromMinutes(2), ct);
        var path = r.Output.Trim().TrimEnd('/');
        return r.ExitCode == 0 && ActionStore.IsLaunchTarget(path) ? path : null;
    }
}

public sealed class NoAppPicker : IAppPicker
{
    public Task<string?> PickAsync(CancellationToken ct) => Task.FromResult<string?>(null);
}

public sealed record LayoutEnvelope(string Version, LayoutDoc Layout);

public sealed record DeviceView(string Id, string Model, string Serial, DateTimeOffset CreatedAt, bool Revoked, IReadOnlyList<string> Permissions);
public sealed record PermissionsBody(IReadOnlyList<string> Permissions);

public static class AdminEndpoints
{
    /// <summary>Required on every admin API call. Browsers can't add it cross-site without a CORS preflight, which the agent never grants.</summary>
    public const string Header = "X-DeskPanel-Admin";

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        // Public: trades the one-time link for a session cookie. GET, so the link works from `open` or a click.
        app.MapGet("/admin/login", (string? t, AdminSessions sessions, HttpContext ctx) =>
        {
            var session = sessions.Redeem(t);
            if (session is null)
                return Results.Text("This admin link was already used or has expired. Get a fresh one: tools/admin.sh", "text/plain", statusCode: 403);
            ctx.Response.Cookies.Append(AdminSessions.Cookie, session, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/admin", MaxAge = AdminSessions.SessionLifetime, IsEssential = true,
            });
            return Results.Redirect("/admin", permanent: false);
        }).WithTrust(TrustClass.Public);

        app.MapGet("/admin", () => Page("admin.html", "text/html; charset=utf-8")).WithTrust(TrustClass.Admin);
        app.MapGet("/admin/app.js", () => Page("admin.js", "text/javascript; charset=utf-8")).WithTrust(TrustClass.Admin);
        app.MapGet("/admin/app.css", () => Page("admin.css", "text/css; charset=utf-8")).WithTrust(TrustClass.Admin);

        var api = app.MapGroup("/admin/api").WithTrust(TrustClass.Admin).AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.Request.Headers[Header] == "1" ? await next(ctx) : Results.StatusCode(StatusCodes.Status403Forbidden));

        api.MapPost("/logout", (AdminSessions sessions, HttpContext ctx) =>
        {
            sessions.SignOut(ctx.Request.Cookies[AdminSessions.Cookie]);
            ctx.Response.Cookies.Delete(AdminSessions.Cookie, new CookieOptions { Path = "/admin" });
            return Results.Ok();
        });

        api.MapGet("/devices", (DeviceStore devices, PermissionStore permissions) =>
            devices.All().OrderByDescending(d => d.CreatedAt)
                .Select(d => new DeviceView(d.Id, d.Model, d.Serial, d.CreatedAt, d.Revoked, d.Revoked ? [] : permissions.For(d.Id))));

        api.MapPost("/devices/{id}/revoke", (string id, DeviceStore devices, PermissionStore permissions) =>
        {
            if (devices.All().All(d => d.Id != id)) return Results.NotFound();
            devices.Revoke(id);
            permissions.Clear(id);
            return Results.Ok();
        });

        api.MapPut("/devices/{id}/permissions", (string id, PermissionsBody body, DeviceStore devices, PermissionStore permissions) =>
            devices.Active(id) is null
                ? Results.NotFound()
                : Results.Ok(new { permissions = permissions.Set(id, body.Permissions ?? []) }));

        api.MapGet("/permissions", () => Permission.All.Select(p => new { id = p, description = Permission.Descriptions[p] }));

        api.MapGet("/actions", (ActionStore actions) => actions.All());
        api.MapPut("/actions", (List<ActionDef> body, ActionStore actions) => Results.Ok(new { errors = actions.Replace(body) }));

        api.MapPost("/pick-app", async (IAppPicker picker, CancellationToken ct) =>
            await picker.PickAsync(ct) is { } path
                ? Results.Ok(new { path, name = Path.GetFileNameWithoutExtension(path) })
                : Results.NoContent());

        api.MapGet("/audit", (int? limit, AuditLog audit) => audit.Recent(limit ?? 200));

        // layout editor
        api.MapGet("/catalog", () => Results.Text(Catalog.RawJson, "application/json; charset=utf-8"));
        api.MapGet("/layout", (LayoutStore layout) => new LayoutEnvelope(layout.Version, layout.Get()));
        api.MapPut("/layout", (LayoutEnvelope body, LayoutStore layout) =>
        {
            var (outcome, errors, version) = layout.Save(body.Layout, body.Version);
            return outcome switch
            {
                SaveOutcome.Saved => Results.Ok(new { version }),
                SaveOutcome.Conflict => Results.Json(new { error = "changed-elsewhere", version }, statusCode: StatusCodes.Status409Conflict),
                _ => Results.BadRequest(new { error = "invalid", errors }),
            };
        });
    }

    private static IResult Page(string name, string type)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("admin/" + name);
        return stream is null ? Results.NotFound() : Results.Stream(stream, type);
    }
}
