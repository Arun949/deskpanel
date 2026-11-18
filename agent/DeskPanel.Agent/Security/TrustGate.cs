namespace DeskPanel.Agent.Security;

/// <summary>
/// Enforces each endpoint's <see cref="TrustClass"/>. Runs after routing, so it knows the matched endpoint.
/// Fail closed: an endpoint without a TrustClass is answered with 404, never served.
/// </summary>
public sealed partial class TrustGate(
    RequestDelegate next,
    IDeviceAuthenticator devices,
    IAdminAuthenticator admins,
    ILogger<TrustGate> log)
{
    /// <summary>The authenticated device id, for handlers and the audit log.</summary>
    public const string DeviceIdItem = "deskpanel.deviceId";

    public Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null) return next(context); // no route matched: routing answers 404

        var trust = endpoint.Metadata.GetMetadata<TrustMetadata>();
        if (trust is null)
        {
            LogUnclassified(log, endpoint.DisplayName);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        switch (trust.Class)
        {
            case TrustClass.Public:
                break;
            case TrustClass.Device:
                var deviceId = devices.Authenticate(context);
                if (deviceId is null) return Unauthorized(context, "Device");
                context.Items[DeviceIdItem] = deviceId;
                break;
            case TrustClass.Admin:
                if (!admins.Authenticate(context)) return Unauthorized(context, "Admin");
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
        }
        return next(context);
    }

    private static Task Unauthorized(HttpContext context, string scheme)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = scheme;
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Refused a request to {Endpoint}: the route declares no TrustClass")]
    private static partial void LogUnclassified(ILogger logger, string? endpoint);
}
