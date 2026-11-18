using System.Reflection;
using System.Security.Cryptography;

namespace DeskPanel.Agent.Core;

/// <summary>
/// Identity of this agent process. <see cref="RunId"/> is random per start, so the phone can tell an agent restart
/// apart from a stream reconnect (event sequence numbers start again with a new run).
/// </summary>
public sealed class AgentRun
{
    public string RunId { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    public string Version { get; } =
        typeof(AgentRun).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";
}
