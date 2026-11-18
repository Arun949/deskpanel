namespace DeskPanel.Agent.Security;

/// <summary>
/// Who may call a route. Every endpoint must declare exactly one; an endpoint without one is refused (404) at run time
/// and fails the route-classification test at build time. Being on loopback grants nothing by itself.
/// </summary>
public enum TrustClass
{
    /// <summary>Anyone who can reach the loopback port. Must never reveal secrets or change state.</summary>
    Public,

    /// <summary>An enrolled phone, authenticated with its per-device key.</summary>
    Device,

    /// <summary>The settings page in the PC's browser, authenticated with an admin session.</summary>
    Admin,
}

/// <summary>Endpoint metadata carrying the route's <see cref="TrustClass"/>.</summary>
public sealed record TrustMetadata(TrustClass Class);

public static class TrustEndpointExtensions
{
    /// <summary>Declares who may call this endpoint (or every endpoint in a group).</summary>
    public static TBuilder WithTrust<TBuilder>(this TBuilder builder, TrustClass trust)
        where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new TrustMetadata(trust));
}
