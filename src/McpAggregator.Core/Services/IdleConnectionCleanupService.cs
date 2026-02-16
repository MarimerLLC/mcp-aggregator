using McpAggregator.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpAggregator.Core.Services;

public class IdleConnectionCleanupService : BackgroundService
{
    private readonly ConnectionManager _connectionManager;
    private readonly AggregatorOptions _options;
    private readonly ILogger<IdleConnectionCleanupService> _logger;

    public IdleConnectionCleanupService(
        ConnectionManager connectionManager,
        IOptions<AggregatorOptions> options,
        ILogger<IdleConnectionCleanupService> logger)
    {
        _connectionManager = connectionManager;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.ConnectionIdleTimeout / 2;
        if (interval < TimeSpan.FromMinutes(1))
            interval = TimeSpan.FromMinutes(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await _connectionManager.CleanupIdleConnectionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during idle connection cleanup");
            }
        }
    }
}
