using System.Threading.Channels;

namespace McpAggregator.Core.Services;

/// <summary>
/// Asks <see cref="SkillMaintenanceHostedService"/> to check one server now rather than at the next
/// scheduled sweep (issue #58). <c>refresh_service</c> raises it, since a downstream that was just
/// upgraded is exactly when its skill goes stale. Requests are dropped silently when the upkeep is off.
/// </summary>
public sealed class SkillMaintenanceSignal
{
    private readonly Channel<string> _requests = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    public void Request(string serverName) => _requests.Writer.TryWrite(serverName);

    internal ChannelReader<string> Reader => _requests.Reader;
}
