using McpAggregator.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpAggregator.Core.Services;

/// <summary>
/// Runs the first <see cref="WrapperToolCatalog.SyncAsync"/> in the background once the host is
/// up (so Eager mode populates the tool list without blocking startup on every downstream
/// connection), then resyncs every <see cref="AggregatorOptions.IndexCacheTtl"/> so a downstream
/// whose tools changed, or that was unavailable at the last sync, is picked up. In Lazy mode the
/// periodic sync only touches servers that have activated wrappers.
/// </summary>
public sealed class WrapperSyncHostedService : BackgroundService
{
    private readonly WrapperToolCatalog _catalog;
    private readonly AggregatorOptions _options;
    private readonly ILogger<WrapperSyncHostedService> _logger;

    public WrapperSyncHostedService(
        WrapperToolCatalog catalog,
        IOptions<AggregatorOptions> options,
        ILogger<WrapperSyncHostedService> logger)
    {
        _catalog = catalog;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before the first sync begins connecting downstreams.
        await Task.Yield();

        var interval = _options.IndexCacheTtl;
        if (interval < TimeSpan.FromSeconds(30))
            interval = TimeSpan.FromSeconds(30);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _catalog.SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Periodic wrapper tool sync failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
