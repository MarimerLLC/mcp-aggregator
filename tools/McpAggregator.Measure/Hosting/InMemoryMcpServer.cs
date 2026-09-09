using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Measure.Hosting;

/// <summary>
/// A real <see cref="McpServer"/> over a pair of in-process pipes plus the matching client
/// transport. Same shape as the test helper of the same name; duplicated here so the harness does
/// not depend on the test project.
/// </summary>
internal sealed class InMemoryMcpServer : IAsyncDisposable
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly StreamServerTransport _serverTransport;
    private readonly McpServer _server;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _runTask;

    private InMemoryMcpServer(string name, McpServerOptions options, IServiceProvider? services)
    {
        _serverTransport = new StreamServerTransport(
            _clientToServer.Reader.AsStream(),
            _serverToClient.Writer.AsStream(),
            name);

        _server = McpServer.Create(_serverTransport, options, loggerFactory: null, serviceProvider: services);
        _runTask = _server.RunAsync(_cts.Token);

        ClientTransport = new StreamClientTransport(
            serverInput: _clientToServer.Writer.AsStream(),
            serverOutput: _serverToClient.Reader.AsStream());
    }

    public static InMemoryMcpServer Host(string name, McpServerOptions options, IServiceProvider? services = null)
        => new(name, options, services);

    public static InMemoryMcpServer HostTools(string name, McpServerPrimitiveCollection<McpServerTool> tools)
        => new(name, new McpServerOptions
        {
            ServerInfo = new Implementation { Name = name, Version = "stub" },
            ToolCollection = tools
        }, services: null);

    public IClientTransport ClientTransport { get; }

    public McpServer Server => _server;

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
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
