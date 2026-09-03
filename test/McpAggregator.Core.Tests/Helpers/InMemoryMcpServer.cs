using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tests.Helpers;

/// <summary>
/// A real <see cref="McpServer"/> running over a pair of in-process pipes, plus the matching
/// <see cref="IClientTransport"/> a client can connect with. Lets tests exercise a full MCP
/// round-trip (JSON-RPC serialization included) without spawning a downstream process.
/// </summary>
internal sealed class InMemoryMcpServer : IAsyncDisposable
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly StreamServerTransport _serverTransport;
    private readonly McpServer _server;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _runTask;

    public InMemoryMcpServer(string name, params McpServerTool[] tools)
    {
        _serverTransport = new StreamServerTransport(
            _clientToServer.Reader.AsStream(),
            _serverToClient.Writer.AsStream(),
            name);

        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in tools)
            toolCollection.Add(tool);

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = name, Version = "1.0.0" },
            ToolCollection = toolCollection
        };

        _server = McpServer.Create(_serverTransport, options);
        _runTask = _server.RunAsync(_cts.Token);

        ClientTransport = new StreamClientTransport(
            serverInput: _clientToServer.Writer.AsStream(),
            serverOutput: _serverToClient.Reader.AsStream());
    }

    /// <summary>Transport a client (or <c>ConnectionManager.TransportFactoryOverride</c>) connects with.</summary>
    public IClientTransport ClientTransport { get; }

    public Task<McpClient> CreateClientAsync(CancellationToken ct = default)
        => McpClient.CreateAsync(ClientTransport, cancellationToken: ct);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        // Completing both pipes unblocks the transport's pending reads so RunAsync can finish.
        await _clientToServer.Writer.CompleteAsync();
        await _serverToClient.Writer.CompleteAsync();

        try { await _runTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        catch (IOException) { }

        await _server.DisposeAsync();
        await _serverTransport.DisposeAsync();
        _cts.Dispose();
    }
}
