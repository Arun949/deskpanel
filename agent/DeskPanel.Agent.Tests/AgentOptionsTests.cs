using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.Tests;

public sealed class AgentOptionsTests
{
    [Fact]
    public void DefaultsTo8765() => Assert.Equal(8765, AgentOptions.From([]).Port);

    [Fact]
    public void ArgumentWinsOverEnvironment() => Assert.Equal(9000, AgentOptions.From(["--port", "9000"], "9100").Port);

    [Fact]
    public void EnvironmentIsUsedWithoutArgument() => Assert.Equal(9100, AgentOptions.From([], "9100").Port);

    [Theory]
    [InlineData("80")]       // privileged
    [InlineData("70000")]
    [InlineData("-1")]
    [InlineData("8765x")]
    [InlineData(" 8765")]
    public void InvalidPortsAreRejected(string value) =>
        Assert.Throws<ArgumentException>(() => AgentOptions.From(["--port", value]));

    [Fact]
    public void RepeatedOrMissingPortIsRejected()
    {
        Assert.Throws<ArgumentException>(() => AgentOptions.From(["--port"]));
        Assert.Throws<ArgumentException>(() => AgentOptions.From(["--port", "9000", "--port", "9001"]));
    }

    [Theory]
    [InlineData("127.0.0.1", "", "", true)]
    [InlineData("localhost", "http://localhost:8765", "same-origin", true)]
    [InlineData("localhost", "", "none", true)]                 // typed into the address bar
    [InlineData("evil.example", "", "", false)]
    [InlineData("localhost", "https://evil.example", "", false)]
    [InlineData("localhost", "", "cross-site", false)]
    public void BrowserGuardRules(string host, string origin, string site, bool allowed) =>
        Assert.Equal(allowed, Security.BrowserGuard.IsAllowed(host, origin, site,
            ["http://127.0.0.1:8765", "http://localhost:8765"]));
}
