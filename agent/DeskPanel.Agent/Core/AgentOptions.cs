using System.Globalization;

namespace DeskPanel.Agent.Core;

/// <summary>Start-up options. The agent always listens on loopback only; only the port is configurable.</summary>
public sealed record AgentOptions(int Port)
{
    public const int DefaultPort = 8765;

    /// <summary>Reads <c>--port N</c> from the arguments, else <c>DESKPANEL_PORT</c>, else <see cref="DefaultPort"/>.</summary>
    public static AgentOptions From(IReadOnlyList<string> args, string? environmentPort = null)
    {
        string? raw = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] != "--port") continue;
            if (raw is not null) throw new ArgumentException("--port is given more than once");
            if (i + 1 >= args.Count) throw new ArgumentException("--port needs a value");
            raw = args[++i];
        }
        raw ??= environmentPort;
        if (raw is null) return new AgentOptions(DefaultPort);
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
            throw new ArgumentException($"invalid port '{raw}': use a number from 1024 to 65535");
        return new AgentOptions(port);
    }
}
