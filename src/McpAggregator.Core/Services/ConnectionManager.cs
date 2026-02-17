using System.Collections.Concurrent;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpAggregator.Core.Services;

public sealed class ConnectionState : IAsyncDisposable
{
    public required McpClient Client { get; init; }
    public required IClientTransport Transport { get; init; }
    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    public bool IsConnected { get; set; } = true;

    public void Touch() => LastUsed = DateTimeOffset.UtcNow;

    public async ValueTask DisposeAsync()
    {
        IsConnected = false;
        await Client.DisposeAsync();
        if (Transport is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (Transport is IDisposable disposable)
            disposable.Dispose();
    }
}

public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ConnectionState> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ServerRegistry _registry;
    private readonly AggregatorOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ConnectionManager> _logger;

    public ConnectionManager(
        ServerRegistry registry,
        IOptions<AggregatorOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<ConnectionManager> logger)
    {
        _registry = registry;
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = logger;

        _registry.RegistryChanged += OnRegistryChanged;
    }

    public async Task<McpClient> GetClientAsync(string serverName, CancellationToken ct = default)
    {
        if (_connections.TryGetValue(serverName, out var state) && state.IsConnected)
        {
            state.Touch();
            return state.Client;
        }

        var connectLock = _connectLocks.GetOrAdd(serverName, _ => new SemaphoreSlim(1, 1));
        await connectLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_connections.TryGetValue(serverName, out state) && state.IsConnected)
            {
                state.Touch();
                return state.Client;
            }

            await _registry.EnsureLoadedAsync(ct);
            var server = _registry.Get(serverName);
            if (!server.Enabled)
                throw new ServerUnavailableException(serverName);

            return (await ConnectAsync(server, ct)).Client;
        }
        finally
        {
            connectLock.Release();
        }
    }

    public bool IsConnected(string serverName) =>
        _connections.TryGetValue(serverName, out var state) && state.IsConnected;

    public async Task DisconnectAsync(string serverName)
    {
        if (_connections.TryRemove(serverName, out var state))
        {
            _logger.LogInformation("Disconnecting from '{Server}'", serverName);
            await state.DisposeAsync();
        }
    }

    public async Task<T> ExecuteWithRetryAsync<T>(
        string serverName,
        Func<McpClient, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        var client = await GetClientAsync(serverName, ct);
        try
        {
            return await operation(client, ct);
        }
        catch (Exception ex) when (ShouldRetry(ex))
        {
            _logger.LogWarning(ex, "Connection to '{Server}' appears broken, reconnecting", serverName);
            await DisconnectAsync(serverName);
            client = await GetClientAsync(serverName, ct);
            return await operation(client, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AggregatorException)
        {
            _logger.LogError(ex, "Non-retryable error executing operation on '{Server}': {ExType}: {ExMessage}",
                serverName, ex.GetType().FullName, ex.Message);
            throw;
        }
    }

    private static bool ShouldRetry(Exception ex)
    {
        if (ex is OperationCanceledException or AggregatorException)
            return false;

        return ex is System.IO.IOException
            or System.Net.Http.HttpRequestException
            or System.Net.Sockets.SocketException
            or ObjectDisposedException
            || ex.InnerException is System.IO.IOException
            or System.Net.Http.HttpRequestException
            or System.Net.Sockets.SocketException
            or ObjectDisposedException;
    }

    public async Task CleanupIdleConnectionsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - _options.ConnectionIdleTimeout;
        var idle = _connections.Where(kvp => kvp.Value.LastUsed < cutoff).Select(kvp => kvp.Key).ToList();

        foreach (var name in idle)
        {
            _logger.LogInformation("Cleaning up idle connection to '{Server}'", name);
            await DisconnectAsync(name);
        }
    }

    private async Task<ConnectionState> ConnectAsync(RegisteredServer server, CancellationToken ct)
    {
        _logger.LogInformation("Connecting to '{Server}' via {Transport}", server.Name, server.Transport.Type);

        IClientTransport transport;
        McpClient client;

        try
        {
            switch (server.Transport.Type)
            {
                case TransportType.Stdio:
                    var stdioTransport = new StdioClientTransport(new StdioClientTransportOptions
                    {
                        Name = server.Name,
                        Command = server.Transport.Command!,
                        Arguments = server.Transport.Arguments ?? [],
                        EnvironmentVariables = server.Transport.Environment!
                    }, _loggerFactory);
                    transport = stdioTransport;
                    client = await McpClient.CreateAsync(stdioTransport, loggerFactory: _loggerFactory, cancellationToken: ct);
                    break;

                case TransportType.Http:
                    var httpTransport = new HttpClientTransport(new HttpClientTransportOptions
                    {
                        Endpoint = new Uri(server.Transport.Url!),
                        Name = server.Name
                    }, _loggerFactory);
                    transport = httpTransport;
                    client = await McpClient.CreateAsync(httpTransport, loggerFactory: _loggerFactory, cancellationToken: ct);
                    break;

                default:
                    throw new InvalidTransportConfigException($"Unsupported transport: {server.Transport.Type}");
            }
        }
        catch (Exception ex) when (ex is not AggregatorException)
        {
            _logger.LogError(ex, "Failed to connect to '{Server}'", server.Name);
            throw new ServerUnavailableException(server.Name, ex);
        }

        var state = new ConnectionState
        {
            Client = client,
            Transport = transport
        };

        _connections[server.Name] = state;
        _logger.LogInformation("Connected to '{Server}'", server.Name);
        return state;
    }

    private void OnRegistryChanged()
    {
        // Remove connections for unregistered servers
        var registered = _registry.GetAll().Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toRemove = _connections.Keys.Where(k => !registered.Contains(k)).ToList();
        foreach (var name in toRemove)
        {
            _ = DisconnectAsync(name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _registry.RegistryChanged -= OnRegistryChanged;
        foreach (var kvp in _connections)
        {
            try { await kvp.Value.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing connection to '{Server}'", kvp.Key); }
        }
        _connections.Clear();
    }
}
