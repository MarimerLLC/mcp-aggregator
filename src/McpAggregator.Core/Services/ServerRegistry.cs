using System.Collections.Concurrent;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Models;
using McpAggregator.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpAggregator.Core.Services;

public class ServerRegistry
{
    private readonly ConcurrentDictionary<string, RegisteredServer> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly IRegistryPersistence _persistence;
    private readonly ILogger<ServerRegistry> _logger;
    private readonly AggregatorOptions _options;
    private bool _loaded;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public event Action? RegistryChanged;

    public ServerRegistry(
        IRegistryPersistence persistence,
        IOptions<AggregatorOptions> options,
        ILogger<ServerRegistry> logger)
    {
        _persistence = persistence;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;

        await _loadLock.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            var data = await _persistence.LoadAsync(ct);
            foreach (var server in data.Servers)
            {
                _servers[server.Name] = server;
            }
            _loaded = true;
            _logger.LogInformation("Loaded {Count} servers from registry", _servers.Count);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public IReadOnlyList<RegisteredServer> GetAll()
    {
        return _servers.Values.ToList();
    }

    public RegisteredServer Get(string name)
    {
        if (_servers.TryGetValue(name, out var server))
            return server;
        throw new ServerNotFoundException(name);
    }

    public bool TryGet(string name, out RegisteredServer? server)
    {
        return _servers.TryGetValue(name, out server);
    }

    public async Task RegisterAsync(RegisteredServer server, CancellationToken ct = default)
    {
        ValidateTransportConfig(server.Transport);

        if (!_servers.TryAdd(server.Name, server))
            throw new ServerAlreadyExistsException(server.Name);

        server.RegisteredAt = DateTimeOffset.UtcNow;
        await PersistAsync(ct);
        _logger.LogInformation("Registered server '{Name}'", server.Name);
        RegistryChanged?.Invoke();
    }

    public async Task UnregisterAsync(string name, CancellationToken ct = default)
    {
        if (!_servers.TryRemove(name, out _))
            throw new ServerNotFoundException(name);

        await PersistAsync(ct);
        _logger.LogInformation("Unregistered server '{Name}'", name);
        RegistryChanged?.Invoke();
    }

    public async Task UpdateSkillFlagAsync(string name, bool hasSkill, CancellationToken ct = default)
    {
        var server = Get(name);
        server.HasSkillDocument = hasSkill;
        await PersistAsync(ct);
    }

    public async Task UpdateSummaryAsync(string name, string summary, CancellationToken ct = default)
    {
        var server = Get(name);
        server.AiSummary = summary;
        await PersistAsync(ct);
    }

    public async Task SetEnabledAsync(string name, bool enabled, CancellationToken ct = default)
    {
        var server = Get(name);
        server.Enabled = enabled;
        await PersistAsync(ct);
        _logger.LogInformation("Server '{Name}' {Status}", name, enabled ? "enabled" : "disabled");
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        var data = new RegistryData { Servers = _servers.Values.ToList() };
        await _persistence.SaveAsync(data, ct);
    }

    private static void ValidateTransportConfig(TransportConfig config)
    {
        switch (config.Type)
        {
            case TransportType.Stdio:
                if (string.IsNullOrWhiteSpace(config.Command))
                    throw new InvalidTransportConfigException("Stdio transport requires a 'command'.");
                break;
            case TransportType.Http:
                if (string.IsNullOrWhiteSpace(config.Url))
                    throw new InvalidTransportConfigException("HTTP transport requires a 'url'.");
                if (!Uri.TryCreate(config.Url, UriKind.Absolute, out _))
                    throw new InvalidTransportConfigException($"Invalid URL: '{config.Url}'.");
                break;
            default:
                throw new InvalidTransportConfigException($"Unknown transport type: {config.Type}");
        }
    }
}
