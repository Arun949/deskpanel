using System.Net;
using System.Net.Http.Json;
using DeskPanel.Agent.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DeskPanel.Agent.Tests;

/// <summary>
/// The request gate as a native client sees it (no Origin, no Sec-Fetch-* headers), and as a browser sees it.
/// PhoneBox tested only the browser side; the native side is where its loopback hole was.
/// </summary>
public sealed class ApiSecurityTests(TestHost factory) : IClassFixture<TestHost>
{
    private HttpClient Client(string baseAddress = "http://localhost/") =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(baseAddress), AllowAutoRedirect = false });

    [Fact]
    public async Task HelloIsPublicAndRevealsOnlyIdentity()
    {
        var res = await Client().GetAsync("/api/v1/hello");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var hello = await res.Content.ReadFromJsonAsync<HelloResponse>();
        Assert.NotNull(hello);
        Assert.Equal("DeskPanel", hello.App);
        Assert.Equal(Endpoints.ApiVersion, hello.Api);
        Assert.Matches("^[0-9a-f]{16}$", hello.RunId);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task ADeviceRouteRefusesANativeClientWithoutAKey()
    {
        // exactly what any other app on the phone would send through adb reverse
        var res = await Client("http://127.0.0.1/").GetAsync("/api/v1/state");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("Device", res.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task ADeviceRouteRefusesAMadeUpKey()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/state");
        req.Headers.TryAddWithoutValidation("Authorization", "Device phone1.not-a-real-key");
        var res = await Client().SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task AnUnknownRouteIsNotFound() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync("/api/v1/settings")).StatusCode);

    [Theory]
    [InlineData("http://evil.example/")]      // DNS rebinding: a hostile name resolving to 127.0.0.1
    [InlineData("http://192.168.1.20/")]
    public async Task AForeignHostIsRefused(string baseAddress) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(baseAddress).GetAsync("/api/v1/hello")).StatusCode);

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:9999")]
    [InlineData("null")]                      // sandboxed iframes and file:// pages
    public async Task AForeignOriginIsRefused(string origin)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/hello");
        req.Headers.Add("Origin", origin);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task TheAgentsOwnOriginIsAllowed()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/hello");
        req.Headers.Add("Origin", "http://localhost:8765");
        Assert.Equal(HttpStatusCode.OK, (await Client().SendAsync(req)).StatusCode);
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task ABrowserRequestFromAnotherSiteIsRefused(string site)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/hello");
        req.Headers.Add("Sec-Fetch-Site", site);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().SendAsync(req)).StatusCode);
    }
}
