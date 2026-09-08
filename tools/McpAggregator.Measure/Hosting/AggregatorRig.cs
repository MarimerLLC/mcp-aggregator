using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using McpAggregator.Core.Storage;
using McpAggregator.Core.Tools;
using McpAggregator.Measure.Stubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Measure.Hosting;

/// <summary>Which surface the model is given. See <c>docs/typed-wrapper-tools.md</c>, Measurements.</summary>
public enum Condition
{
    /// <summary>WrapperMode=Eager: meta-tools plus every typed wrapper, from the first turn.</summary>
    Eager,

    /// <summary>WrapperMode=Lazy: meta-tools only until find_tools/get_service_details activates wrappers; the tool list is refreshed after every call, as a host that honors list_changed would.</summary>
    Lazy,

    /// <summary>The pre-#39 surface: meta-tools without find_tools, so the only route to a downstream is invoke_tool with a stringified JSON argument object.</summary>
    InvokeTool,
}

/// <summary>
/// The whole aggregator built through the real DI extension methods, hosted over in-process pipes,
/// with the stub downstreams (and optionally the real Microsoft Learn server) behind it, plus one
/// connected client for the model to call through.
/// </summary>
public sealed class AggregatorRig : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly List<InMemoryMcpServer> _spawned = [];
    private readonly InMemoryMcpServer _aggregator;
    private readonly string _dataDir;

    private AggregatorRig(ServiceProvider provider, InMemoryMcpServer aggregator, McpClient client, CallRecorder recorder, Condition condition, string dataDir)
    {
        _provider = provider;
        _aggregator = aggregator;
        Client = client;
        Recorder = recorder;
        Condition = condition;
        _dataDir = dataDir;
    }

    public Condition Condition { get; }
    public McpClient Client { get; }
    public CallRecorder Recorder { get; }
    public WrapperToolCatalog Catalog => _provider.GetRequiredService<WrapperToolCatalog>();

    /// <summary>Names of the aggregator's own (non-wrapper) tools, for classifying model calls.</summary>
    public HashSet<string> MetaToolNames { get; } = new(StringComparer.Ordinal);

    public static async Task<AggregatorRig> CreateAsync(Condition condition, bool realDocs, CancellationToken ct)
    {
        var recorder = new CallRecorder();
        var servers = StubDownstreams.Registrations(realDocs);
        var mode = condition == Condition.Eager ? WrapperToolMode.Eager : WrapperToolMode.Lazy;

        var dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-measure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpAggregator:DataDirectory"] = dataDir,
                ["McpAggregator:WrapperMode"] = mode.ToString(),
                ["McpAggregator:DefaultToolTimeout"] = "00:01:00",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddAggregatorCore(configuration);
        services.AddSingleton<IRegistryPersistence>(new StaticRegistryPersistence(servers));
        services.AddAggregatorMcpServer().WithToolsFromAssembly(typeof(ConsumerTools).Assembly);
        var provider = services.BuildServiceProvider();

        var rig = default(AggregatorRig);
        var spawned = new List<InMemoryMcpServer>();
        provider.GetRequiredService<ConnectionManager>().TransportFactoryOverride = server =>
        {
            if (server.Transport.Type == TransportType.Http)
            {
                return new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(server.Transport.Url!),
                    Name = server.Name,
                });
            }

            var stub = InMemoryMcpServer.HostTools(server.Name, StubDownstreams.ToolsFor(server.Name, recorder));
            lock (spawned) spawned.Add(stub);
            return stub.ClientTransport;
        };

        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var aggregator = InMemoryMcpServer.Host("aggregator", options, provider);

        // Connect as today's hosts do (initialize handshake, pre-2026 protocol) so list_changed is
        // broadcast; the harness re-lists tools itself in Lazy mode rather than relying on it.
        var client = await McpClient.CreateAsync(aggregator.ClientTransport,
            new McpClientOptions { ProtocolVersion = "2025-06-18" }, cancellationToken: ct);

        rig = new AggregatorRig(provider, aggregator, client, recorder, condition, dataDir);
        rig._spawned.AddRange(spawned);
        foreach (var tool in options.ToolCollection!)
        {
            if (tool is not DownstreamToolWrapper)
                rig.MetaToolNames.Add(tool.ProtocolTool.Name);
        }

        if (condition == Condition.Eager)
            await rig.Catalog.SyncAsync(ct);

        return rig;
    }

    /// <summary>The tools the model is shown right now for this condition.</summary>
    public async Task<IList<McpClientTool>> ListModelToolsAsync(CancellationToken ct)
    {
        var tools = await Client.ListToolsAsync(cancellationToken: ct);

        return Condition switch
        {
            // The pre-#39 surface never had find_tools, and no wrapper can ever be active.
            Condition.InvokeTool => tools.Where(t => t.Name != "find_tools" && !t.Name.Contains(WrapperNaming.Separator)).ToList(),
            _ => tools,
        };
    }

    /// <summary>The MCP server instructions the model is shown, per condition.</summary>
    public string Instructions => Condition == Condition.InvokeTool
        ? LegacyInstructions
        : Client.ServerInstructions ?? string.Empty;

    // The pre-#39 header from McpServerBuilderExtensions.BuildInstructions, verbatim, so the
    // invoke_tool condition is the old product rather than the new product with tools hidden.
    private const string LegacyInstructions = """
        MCP Aggregator — a single MCP endpoint that fans out to many downstream MCP servers.
        One connection gives the client the union of tools across every registered server,
        without consuming a slot per server in clients that cap concurrent MCP connections.

        Discovery flow:
          1. list_services() — see every registered downstream server and its summary.
          2. get_service_skill(serverName: "mcp-aggregator") — full usage guide for this aggregator.
          3. get_service_skill(serverName: "<downstream>") — usage guide for a specific service.
          4. invoke_tool(serverName, toolName, arguments) — invoke any downstream tool through the aggregator.

        Downstream connections are established lazily on first use and reused across calls.
        """;

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _aggregator.DisposeAsync();
        await _provider.GetRequiredService<ConnectionManager>().DisposeAsync();
        List<InMemoryMcpServer> all;
        lock (_spawned) all = [.. _spawned];
        foreach (var s in all) await s.DisposeAsync();
        await _provider.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class StaticRegistryPersistence(IReadOnlyList<RegisteredServer> servers) : IRegistryPersistence
    {
        public Task<RegistryData> LoadAsync(CancellationToken ct = default)
            => Task.FromResult(new RegistryData { Servers = [.. servers] });

        public Task SaveAsync(RegistryData data, CancellationToken ct = default) => Task.CompletedTask;
    }
}
