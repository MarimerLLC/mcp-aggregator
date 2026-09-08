using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Models;
using McpAggregator.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpAggregator.Core.Services;

public partial class ServerRegistry
{
    /// <summary>
    /// Server names become the prefix of every typed wrapper tool (<c>{server}__{tool}</c>), so
    /// they must be valid tool-name material: SDK charset, no leading punctuation, and never the
    /// <c>__</c> separator itself, which would make the wrapper name ambiguous to parse.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex ValidServerName();

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
            var assignedIds = 0;
            foreach (var server in data.Servers)
            {
                if (string.IsNullOrWhiteSpace(server.Id))
                {
                    server.Id = NewId();
                    assignedIds++;
                }
                _servers[server.Name] = server;
            }
            _loaded = true;
            _logger.LogInformation("Loaded {Count} servers from registry", _servers.Count);

            if (assignedIds > 0)
            {
                // Registry written before RegisteredServer.Id existed: persist the backfilled ids
                // once so they stay stable across restarts.
                await PersistAsync(ct);
                _logger.LogInformation("Assigned ids to {Count} server(s) registered before ids existed", assignedIds);
            }
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
        ValidateServerName(server.Name);
        ValidateTransportConfig(server.Transport);

        if (string.IsNullOrWhiteSpace(server.Id))
            server.Id = NewId();

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

    public async Task UpdateSkillSnapshotAsync(
        string name,
        string? recordedVersion,
        string? recordedFingerprint,
        DateTimeOffset? recordedAt,
        CancellationToken ct = default)
    {
        var server = Get(name);
        server.SkillRecordedVersion = recordedVersion;
        server.SkillRecordedFingerprint = recordedFingerprint;
        server.SkillRecordedAt = recordedAt;
        await PersistAsync(ct);
    }

    public async Task UpdateRemoteMetadataAsync(
        string name,
        string? remoteName,
        string? remoteTitle,
        string? remoteVersion,
        string? remoteInstructions,
        CancellationToken ct = default)
    {
        var server = Get(name);
        if (server.RemoteName == remoteName
            && server.RemoteTitle == remoteTitle
            && server.RemoteVersion == remoteVersion
            && server.RemoteInstructions == remoteInstructions)
        {
            return;
        }

        server.RemoteName = remoteName;
        server.RemoteTitle = remoteTitle;
        server.RemoteVersion = remoteVersion;
        server.RemoteInstructions = remoteInstructions;
        await PersistAsync(ct);
        _logger.LogDebug("Updated remote metadata for '{Name}' (version: {Version})", name, remoteVersion ?? "unknown");
    }

    /// <summary>
    /// Replaces the supplied fields on an existing server. Deliberately preserves
    /// <see cref="RegisteredServer.Enabled"/>, <see cref="RegisteredServer.RegisteredAt"/>,
    /// the skill document flag and snapshot, the AI summary, and the captured remote metadata.
    /// </summary>
    public async Task UpdateServerAsync(
        string name,
        TransportConfig? transport,
        string? displayName,
        string? description,
        CancellationToken ct = default)
    {
        var server = Get(name);

        if (transport is not null)
        {
            ValidateTransportConfig(transport);
            server.Transport = transport;
        }

        if (displayName is not null)
            server.DisplayName = displayName;

        if (description is not null)
            server.Description = description;

        await PersistAsync(ct);
        _logger.LogInformation("Updated server '{Name}'", name);
        RegistryChanged?.Invoke();
    }

    public async Task SetEnabledAsync(string name, bool enabled, CancellationToken ct = default)
    {
        var server = Get(name);
        server.Enabled = enabled;
        await PersistAsync(ct);
        _logger.LogInformation("Server '{Name}' {Status}", name, enabled ? "enabled" : "disabled");
        // Enabling or disabling changes which wrapper tools exist, so it is a registry change.
        RegistryChanged?.Invoke();
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private void ValidateServerName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new AggregatorException("Server name is required.");

        if (name.Contains(Tools.WrapperNaming.Separator, StringComparison.Ordinal))
            throw new AggregatorException(
                $"Invalid server name '{name}': names cannot contain '{Tools.WrapperNaming.Separator}', " +
                "which separates the server and tool parts of a wrapper tool name.");

        if (!ValidServerName().IsMatch(name))
            throw new AggregatorException(
                $"Invalid server name '{name}': use 1-64 characters from [A-Za-z0-9_.-], starting with a letter or digit.");

        if (string.Equals(name, _options.SelfName, StringComparison.OrdinalIgnoreCase))
            throw new AggregatorException(
                $"Invalid server name '{name}': that is the aggregator's own name.");
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
                if (config.Headers is { Count: > 0 })
                    throw new InvalidTransportConfigException("'headers' is only valid for HTTP transport.");
                if (config.ConnectionTimeout is not null)
                    throw new InvalidTransportConfigException("'connectionTimeout' is only valid for HTTP transport.");
                break;
            case TransportType.Http:
                if (string.IsNullOrWhiteSpace(config.Url))
                    throw new InvalidTransportConfigException("HTTP transport requires a 'url'.");
                if (!Uri.TryCreate(config.Url, UriKind.Absolute, out _))
                    throw new InvalidTransportConfigException($"Invalid URL: '{config.Url}'.");
                if (config.Headers is not null && config.Headers.Keys.Any(string.IsNullOrWhiteSpace))
                    throw new InvalidTransportConfigException("Header names cannot be blank.");
                if (config.ConnectionTimeout is { } timeout && timeout <= TimeSpan.Zero)
                    throw new InvalidTransportConfigException("'connectionTimeout' must be greater than zero.");
                break;
            default:
                throw new InvalidTransportConfigException($"Unknown transport type: {config.Type}");
        }
    }
}
