using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using DeskPanel.Agent.Api;
using DeskPanel.Agent.Security;

namespace DeskPanel.Agent.Tests;

/// <summary>Per-device keys (M1 exit criteria: other phone apps get 401; the app can verify it reached the real agent).</summary>
public sealed class DeviceAuthTests(TestHost host) : IClassFixture<TestHost>
{
    [Fact]
    public async Task AnEnrolledDeviceReadsState()
    {
        var (client, _, _) = host.Enrolled("AUTH-OK");
        var res = await client.GetAsync("/api/v1/state");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var state = await res.Content.ReadFromJsonAsync<StateMessage>();
        Assert.True(state!.Full);
        Assert.Matches("^[0-9a-f]{16}$", state.RunId);
    }

    [Fact]
    public async Task AWrongKeyForARealDeviceIs401()
    {
        var (_, device, key) = host.Enrolled("AUTH-WRONG");
        var other = key[..^1] + (key[^1] == 'A' ? 'B' : 'A');
        Assert.Equal(HttpStatusCode.Unauthorized, await StateStatus($"Device {device.Id}.{other}"));
    }

    [Fact]
    public async Task AKeyIsBoundToItsOwnDevice()
    {
        var (_, a, keyA) = host.Enrolled("AUTH-A");
        var (_, b, _) = host.Enrolled("AUTH-B");
        Assert.Equal(HttpStatusCode.OK, await StateStatus($"Device {a.Id}.{keyA}"));
        Assert.Equal(HttpStatusCode.Unauthorized, await StateStatus($"Device {b.Id}.{keyA}"));
    }

    [Fact]
    public async Task ARevokedDeviceIs401()
    {
        var (_, device, key) = host.Enrolled("AUTH-REVOKED");
        host.Devices.Revoke(device.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, await StateStatus($"Device {device.Id}.{key}"));
    }

    [Fact]
    public async Task ReEnrollingAPhoneRevokesItsOldKey()
    {
        var (_, old, oldKey) = host.Enrolled("AUTH-REENROL");
        var (_, fresh, freshKey) = host.Enrolled("AUTH-REENROL");
        Assert.Equal(HttpStatusCode.Unauthorized, await StateStatus($"Device {old.Id}.{oldKey}"));
        Assert.Equal(HttpStatusCode.OK, await StateStatus($"Device {fresh.Id}.{freshKey}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer abc")]
    [InlineData("Device ")]
    [InlineData("Device 0123456789abcdef")]
    [InlineData("Device 0123456789abcdef.")]
    [InlineData("device 0123456789abcdef.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task MalformedHeadersAre401(string header) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await StateStatus(header));

    [Fact]
    public async Task HelloProvesTheAgentKnowsTheDeviceKey()
    {
        var (_, device, key) = host.Enrolled("PROOF");
        const string nonce = "abcdefghijklmnop0123";
        var hello = await host.Client().GetFromJsonAsync<HelloResponse>($"/api/v1/hello?d={device.Id}&n={nonce}");

        // what the phone computes from its own key
        var proofKey = SHA256.HashData(Encoding.UTF8.GetBytes($"deskpanel|proof|{key}"));
        var expected = DeviceStore.Base64Url(HMACSHA256.HashData(proofKey,
            Encoding.UTF8.GetBytes($"deskpanel-proof|v1|{device.Id}|{nonce}|{hello!.RunId}")));
        Assert.Equal(expected, hello.Proof);
    }

    /// <summary>The same vector is checked by the Android app's ProofTest, so both sides agree byte for byte.</summary>
    [Fact]
    public void TheProofMatchesTheSharedVector()
    {
        var key = new string('A', 43);
        var device = new EnrolledDevice("0123456789abcdef", "S", "M", DeviceStore.Derive("auth", key), DeviceStore.Derive("proof", key), DateTimeOffset.UnixEpoch);
        Assert.Equal("qkFWpPVfSTRkC4LjD-1oQ6pU3IKIulXbXAhfQfx2_m8", DeviceStore.Proof(device, "nonce-nonce-nonce-01", "fedcba9876543210"));
    }

    [Fact]
    public async Task HelloGivesNoProofForAnUnknownDevice()
    {
        var hello = await host.Client().GetFromJsonAsync<HelloResponse>("/api/v1/hello?d=0123456789abcdef&n=abcdefghijklmnop0123");
        Assert.Null(hello!.Proof);
    }

    [Theory]
    [InlineData("?d=0123456789abcdef")]                         // nonce missing
    [InlineData("?n=abcdefghijklmnop0123")]                     // device missing
    [InlineData("?d=0123456789abcdef&n=short")]                 // nonce too short to be fresh
    [InlineData("?d=XYZ&n=abcdefghijklmnop0123")]
    public async Task HelloRejectsBadProofRequests(string query) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client().GetAsync("/api/v1/hello" + query)).StatusCode);

    private async Task<HttpStatusCode> StateStatus(string authorization)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/state");
        if (authorization.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", authorization);
        return (await host.Client().SendAsync(req)).StatusCode;
    }
}
