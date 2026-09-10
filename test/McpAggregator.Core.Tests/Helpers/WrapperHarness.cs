using SysDescription = System.ComponentModel.DescriptionAttribute;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using McpAggregator.Core.Storage;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rocks;

namespace McpAggregator.Core.Tests.Helpers;

/// <summary>
/// The real registry → connections → index → proxy → catalog chain over any number of in-process
/// downstream MCP servers. Each downstream's tool set is a live
/// <see cref="McpServerPrimitiveCollection{T}"/> a test can mutate to simulate a downstream whose
/// tools changed. A fresh in-memory server is spun up per connection, so disconnect/reconnect
/// cycles work.
/// </summary>
internal sealed class WrapperHarness : IAsyncDisposable
{
    private readonly List<InMemoryMcpServer> _spawned = [];
    private readonly object _spawnLock = new();

    private WrapperHarness() { }

    public ServerRegistry Registry { get; private set; } = null!;
    public ConnectionManager Connections { get; private set; } = null!;
    public ToolIndex Index { get; private set; } = null!;
    public ToolProxyHandler Proxy { get; private set; } = null!;
    public McpServerOptions McpOptions { get; private set; } = null!;
    public WrapperToolCatalog Catalog { get; private set; } = null!;
    public Dictionary<string, McpServerPrimitiveCollection<McpServerTool>> DownstreamTools { get; private set; } = null!;

    /// <summary>
    /// Live prompt sets per downstream. Only downstreams created with prompts have an entry; the
    /// others are hosted without a prompt collection and so report no prompt support.
    /// </summary>
    public Dictionary<string, McpServerPrimitiveCollection<McpServerPrompt>> DownstreamPrompts { get; private set; } = null!;

    /// <summary>A non-wrapper tool that lives in the aggregator's collection; the catalog must never touch it.</summary>
    public McpServerTool Sentinel { get; private set; } = null!;

    /// <summary>A non-wrapper prompt that lives in the aggregator's prompt collection; the catalog must never touch it.</summary>
    public McpServerPrompt SentinelPrompt { get; private set; } = null!;

    public McpServerPrimitiveCollection<McpServerTool> ToolCollection => McpOptions.ToolCollection!;

    public McpServerPrimitiveCollection<McpServerPrompt> PromptCollection => McpOptions.PromptCollection!;

    private readonly List<InMemoryMcpServer> _sessions = [];

    /// <summary>
    /// A new aggregator session sharing the tool collection but with its own
    /// <see cref="McpServerOptions"/> instance, as the SDK gives each stateless request and as a
    /// stateful host does per session; that instance is what the catalog keys Lazy activation by
    /// on transports without a session id. Disposed with the harness.
    /// </summary>
    public InMemoryMcpServer NewSession()
    {
        var options = new McpServerOptions
        {
            ServerInfo = McpOptions.ServerInfo,
            ToolCollection = McpOptions.ToolCollection,
            PromptCollection = McpOptions.PromptCollection,
        };
        var session = InMemoryMcpServer.Host("aggregator", options);
        lock (_spawnLock) _sessions.Add(session);
        return session;
    }

    public List<string> WrapperNames
        => ToolCollection.OfType<DownstreamToolWrapper>().Select(w => w.ProtocolTool.Name).Order(StringComparer.Ordinal).ToList();

    public List<string> PromptWrapperNames
        => PromptCollection.OfType<DownstreamPromptWrapper>().Select(w => w.ProtocolPrompt.Name).Order(StringComparer.Ordinal).ToList();

    public static Task<WrapperHarness> CreateAsync(
        string dataDir,
        WrapperToolMode mode,
        params (string Name, McpServerTool[] Tools)[] downstreams)
        => CreateAsync(dataDir, mode, configure: null, downstreams);

    public static Task<WrapperHarness> CreateAsync(
        string dataDir,
        WrapperToolMode mode,
        Action<AggregatorOptions>? configure,
        params (string Name, McpServerTool[] Tools)[] downstreams)
        => CreateCoreAsync(dataDir, mode, configure,
            downstreams.Select(d => (d.Name, d.Tools, (McpServerPrompt[]?)null)).ToArray());

    /// <summary>Downstreams that serve prompts as well as tools (issue #40). A null prompt array means "no prompt support".</summary>
    public static Task<WrapperHarness> CreateWithPromptsAsync(
        string dataDir,
        WrapperToolMode mode,
        params (string Name, McpServerTool[] Tools, McpServerPrompt[]? Prompts)[] downstreams)
        => CreateCoreAsync(dataDir, mode, configure: null, downstreams);

    public static Task<WrapperHarness> CreateWithPromptsAsync(
        string dataDir,
        WrapperToolMode mode,
        Action<AggregatorOptions>? configure,
        params (string Name, McpServerTool[] Tools, McpServerPrompt[]? Prompts)[] downstreams)
        => CreateCoreAsync(dataDir, mode, configure, downstreams);

    private static async Task<WrapperHarness> CreateCoreAsync(
        string dataDir,
        WrapperToolMode mode,
        Action<AggregatorOptions>? configure,
        (string Name, McpServerTool[] Tools, McpServerPrompt[]? Prompts)[] downstreams)
    {
        var harness = new WrapperHarness();

        var servers = downstreams.Select(d => TestHelpers.StdioServer(d.Name)).ToList();
        foreach (var s in servers)
            s.Id = Guid.NewGuid().ToString("N")[..12];

        var expectations = new IRegistryPersistenceCreateExpectations();
        expectations.Setups.LoadAsync(Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new RegistryData { Servers = servers }));
        expectations.Setups.SaveAsync(Arg.Any<RegistryData>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);

        var aggOptions = TestHelpers.DefaultAggregatorOptions(dataDir);
        aggOptions.WrapperMode = mode;
        configure?.Invoke(aggOptions);
        var options = TestHelpers.OptionsOf(aggOptions);

        harness.Registry = new ServerRegistry(expectations.Instance(), options, TestHelpers.NullLoggerOf<ServerRegistry>());
        await harness.Registry.EnsureLoadedAsync();

        harness.DownstreamTools = new Dictionary<string, McpServerPrimitiveCollection<McpServerTool>>(StringComparer.OrdinalIgnoreCase);
        harness.DownstreamPrompts = new Dictionary<string, McpServerPrimitiveCollection<McpServerPrompt>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, tools, prompts) in downstreams)
        {
            var collection = new McpServerPrimitiveCollection<McpServerTool>();
            foreach (var t in tools) collection.Add(t);
            harness.DownstreamTools[name] = collection;

            if (prompts is not null)
            {
                var promptCollection = new McpServerPrimitiveCollection<McpServerPrompt>();
                foreach (var p in prompts) promptCollection.Add(p);
                harness.DownstreamPrompts[name] = promptCollection;
            }
        }

        harness.Sentinel = McpServerTool.Create(Ping, new McpServerToolCreateOptions { Name = "aggregator_ping" });
        harness.SentinelPrompt = McpServerPrompt.Create(Greeting, new McpServerPromptCreateOptions { Name = "aggregator_greeting" });
        harness.McpOptions = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "aggregator", Version = "test" },
            ToolCollection = [harness.Sentinel],
            PromptCollection = [harness.SentinelPrompt]
        };

        harness.Connections = new ConnectionManager(harness.Registry, options, NullLoggerFactory.Instance,
            TestHelpers.NullLoggerOf<ConnectionManager>())
        {
            TransportFactoryOverride = server => harness.Spawn(server.Name).ClientTransport
        };

        var skillStore = new SkillStore(options, TestHelpers.NullLoggerOf<SkillStore>());
        harness.Index = new ToolIndex(harness.Registry, harness.Connections, skillStore, options,
            TestHelpers.NullLoggerOf<ToolIndex>());
        harness.Proxy = new ToolProxyHandler(harness.Connections, harness.Index, options,
            TestHelpers.NullLoggerOf<ToolProxyHandler>());
        harness.Catalog = new WrapperToolCatalog(harness.Registry, harness.Index, harness.Proxy, options,
            TestHelpers.OptionsOf(harness.McpOptions), new AdminToolSet(new ServiceCollection().BuildServiceProvider()),
            TestHelpers.NullLoggerOf<WrapperToolCatalog>());

        return harness;
    }

    private InMemoryMcpServer Spawn(string serverName)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = serverName, Version = "1.0.0" },
            ToolCollection = DownstreamTools[serverName],
            PromptCollection = DownstreamPrompts.TryGetValue(serverName, out var prompts) ? prompts : null
        };
        var server = InMemoryMcpServer.Host(serverName, options);
        lock (_spawnLock) _spawned.Add(server);
        return server;
    }

    [SysDescription("Aggregator-side sentinel tool.")]
    private static string Ping() => "pong";

    [SysDescription("Aggregator-side sentinel prompt.")]
    private static string Greeting() => "hello";

    public async ValueTask DisposeAsync()
    {
        await Connections.DisposeAsync();

        List<InMemoryMcpServer> all;
        lock (_spawnLock) all = [.. _sessions, .. _spawned];
        foreach (var s in all)
            await s.DisposeAsync();
    }
}
