using System.Threading.Channels;
using McpAggregator.Core.Configuration;
using Microsoft.Extensions.Options;

namespace McpAggregator.Core.Services;

/// <summary>
/// Asks <see cref="SkillMaintenanceHostedService"/> to check one server now rather than at the next
/// scheduled sweep (issue #58). <c>refresh_service</c> raises it, since a downstream that was just
/// upgraded is exactly when its skill goes stale. When the upkeep is <see cref="SkillMaintenanceMode.Off"/>
/// nothing reads the queue, so requests are dropped at the door; otherwise the queue is bounded and a
/// request beyond <see cref="Capacity"/> is dropped too (the hourly sweep still covers that server).
/// </summary>
public sealed class SkillMaintenanceSignal
{
    public const int Capacity = 64;

    private readonly bool _enabled;
    private readonly Channel<string> _requests = Channel.CreateBounded<string>(
        // Wait mode: TryWrite fails (returns false) when full instead of silently dropping and returning true.
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public SkillMaintenanceSignal(IOptions<SkillMaintenanceOptions> options)
    {
        _enabled = options.Value.Mode != SkillMaintenanceMode.Off;
    }

    /// <summary>Queues a check of <paramref name="serverName"/>; returns false when the request was dropped.</summary>
    public bool Request(string serverName) => _enabled && _requests.Writer.TryWrite(serverName);

    internal ChannelReader<string> Reader => _requests.Reader;
}
