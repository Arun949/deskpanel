using DeskPanel.Agent.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DeskPanel.Agent.Tests;

/// <summary>
/// The rule that would have caught PhoneBox's main flaw: no route may exist without an explicit trust class.
/// A new endpoint added without .WithTrust(...) fails this test.
/// </summary>
public sealed class RouteClassificationTests(TestHost factory) : IClassFixture<TestHost>
{
    private IReadOnlyList<RouteEndpoint> Routes()
    {
        using var _ = factory.CreateClient(); // starts the host so the endpoints are built
        return factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
    }

    [Fact]
    public void TheAppHasRoutes() => Assert.NotEmpty(Routes()); // guards against a vacuous pass below

    [Fact]
    public void EveryRouteDeclaresATrustClass()
    {
        var unclassified = Routes()
            .Where(e => e.Metadata.GetMetadata<TrustMetadata>() is null)
            .Select(e => e.RoutePattern.RawText)
            .ToList();
        Assert.True(unclassified.Count == 0, "Routes without a TrustClass: " + string.Join(", ", unclassified));
    }

    [Fact]
    public void PublicRoutesAreReadOnly()
    {
        var publicWrites = Routes()
            .Where(e => e.Metadata.GetMetadata<TrustMetadata>()?.Class == TrustClass.Public)
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Any(m => m is not ("GET" or "HEAD")) != false)
            .Select(e => e.RoutePattern.RawText)
            .ToList();
        Assert.True(publicWrites.Count == 0, "Public routes must be GET-only: " + string.Join(", ", publicWrites));
    }
}
