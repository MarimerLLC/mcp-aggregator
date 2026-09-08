using SysDescription = System.ComponentModel.DescriptionAttribute;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using McpAggregator.Core.Storage;
using McpAggregator.Core.Tests.Helpers;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rocks;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// A <c>tools/call</c> for a <c>{server}__{tool}</c> name the aggregator does not expose is almost
/// always a stale tool list (issue #39): the server was renamed, disabled, or the wrapper was never
/// activated in Lazy mode. The caller must get a hint that says which, not the SDK's bare
/// "Unknown tool". Driven through the DI-built aggregator over a real MCP round-trip.
/// </summary>
[TestClass]
public class UnknownToolHintTests
{
    private const string Downstream = "probe";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-unknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [SysDescription("Echoes a message back.")]
    private static CallToolResult Echo([SysDescription("Message to echo")] string message)
        => new() { IsError = false, Content = [new TextContentBlock { Text = "echo:" + message }] };

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static string TextOf(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private sealed record Rig(ServiceProvider Provider, WrapperToolCatalog Catalog, List<InMemoryMcpServer> Downstreams, InMemoryMcpServer Aggregator, McpClient Client)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Aggregator.DisposeAsync();
            await Provider.GetRequiredService<ConnectionManager>().DisposeAsync();
            List<InMemoryMcpServer> all;
            lock (Downstreams) all = [.. Downstreams];
            foreach (var d in all) await d.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }

    private async Task<Rig> BuildAsync(WrapperToolMode mode)
    {
        var server = TestHelpers.StdioServer(Downstream);
        var expectations = new IRegistryPersistenceCreateExpectations();
        expectations.Setups.LoadAsync(Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new RegistryData { Servers = [server] }));
        expectations.Setups.SaveAsync(Arg.Any<RegistryData>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpAggregator:DataDirectory"] = _dataDir,
                ["McpAggregator:WrapperMode"] = mode.ToString(),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddAggregatorCore(configuration);
        services.AddSingleton<IRegistryPersistence>(expectations.Instance());
        services.AddAggregatorMcpServer().WithToolsFromAssembly(typeof(ConsumerTools).Assembly);
        var provider = services.BuildServiceProvider();

        var downstreams = new List<InMemoryMcpServer>();
        provider.GetRequiredService<ConnectionManager>().TransportFactoryOverride = _ =>
        {
            var d = new InMemoryMcpServer(Downstream, McpServerTool.Create(Echo, new McpServerToolCreateOptions { Name = "echo" }));
            lock (downstreams) downstreams.Add(d);
            return d.ClientTransport;
        };

        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var catalog = provider.GetRequiredService<WrapperToolCatalog>();
        var aggregator = InMemoryMcpServer.Host("aggregator", options, provider);
        var client = await aggregator.CreateClientAsync(new McpClientOptions { ProtocolVersion = "2025-06-18" }, TestTimeout);
        return new Rig(provider, catalog, downstreams, aggregator, client);
    }

    private static Task<CallToolResult> CallAsync(Rig rig, string tool, Dictionary<string, object?>? args = null)
        => rig.Client.CallToolAsync(tool, args ?? new Dictionary<string, object?>(), cancellationToken: TestTimeout).AsTask();

    [TestMethod]
    public async Task Lazy_UnlistedWrapper_IsDispatchedByName_AndThenListedForThatSession()
    {
        // The caller learned the name (a previous session, a skill doc, a colleague) and calls it
        // before anything activated it. It must simply work — and only this session's list grows.
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        Assert.IsFalse(rig.Catalog.IsActive("probe__echo", rig.Aggregator.Server));

        var result = await CallAsync(rig, "probe__echo", new() { ["message"] = "hi" });

        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        Assert.AreEqual("echo:hi", TextOf(result));
        Assert.IsTrue(rig.Catalog.IsActive("probe__echo", rig.Aggregator.Server), "Using it lists it for this session.");
        var tools = await rig.Client.ListToolsAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(tools.Any(t => t.Name == "probe__echo"));
    }

    [TestMethod]
    public async Task Lazy_UnlistedWrapper_MissingRequiredArgument_StillNamesTheParameter()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);

        var result = await CallAsync(rig, "probe__echo");

        Assert.IsTrue(result.IsError ?? false);
        StringAssert.Contains(TextOf(result), "Missing required parameter(s): [message]");
    }

    [TestMethod]
    public async Task InvokeTool_UnknownServer_ReturnsTheMessageInsteadOfAnOpaqueFault()
    {
        // Seen in the measurement runs: a small model guessed serverName "email-service" and got
        // "An error occurred invoking 'invoke_tool'." back, which told it nothing.
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);

        var result = await CallAsync(rig, "invoke_tool", new()
        {
            ["serverName"] = "email-service",
            ["toolName"] = "send_email",
            ["arguments"] = "{}"
        });

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        StringAssert.Contains(text, "Server 'email-service' not found");
        Assert.IsFalse(text.Contains("An error occurred invoking"), text);
    }

    [TestMethod]
    public async Task UnknownServer_NamesRegisteredServersAndFindTools()
    {
        // The rename case: calendar-mcp became adjutant; the caller still holds the old prefix.
        await using var rig = await BuildAsync(WrapperToolMode.Eager);

        var result = await CallAsync(rig, "calendar-mcp__echo", new() { ["message"] = "hi" });

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        StringAssert.Contains(text, "no server named 'calendar-mcp' is registered");
        StringAssert.Contains(text, "renamed or removed");
        StringAssert.Contains(text, "Registered servers: [probe]");
        StringAssert.Contains(text, "find_tools(query: \"echo\")");
        StringAssert.Contains(text, "refresh your tool list");
    }

    [TestMethod]
    public async Task DisabledServer_SaysSoAndPointsAtEnableService()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Eager);
        await rig.Catalog.SyncAsync(TestTimeout);
        await rig.Provider.GetRequiredService<ServerRegistry>().SetEnabledAsync(Downstream, false);
        await rig.Catalog.PendingSync;
        Assert.IsFalse(rig.Catalog.IsActive("probe__echo", rig.Aggregator.Server));

        var result = await CallAsync(rig, "probe__echo", new() { ["message"] = "hi" });

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        StringAssert.Contains(text, "server 'probe' is disabled");
        StringAssert.Contains(text, "enable_service(serverName: \"probe\")");
    }

    [TestMethod]
    public async Task UnknownToolOnKnownServer_ListsThatServersTools()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Eager);

        var result = await CallAsync(rig, "probe__send_email", new() { ["to"] = "x" });

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        StringAssert.Contains(text, "server 'probe' has no tool 'send_email'");
        StringAssert.Contains(text, "Its tools: [probe__echo]");
        StringAssert.Contains(text, "get_service_details(serverName: \"probe\")");
    }

    [TestMethod]
    public async Task NameThatIsNotAWrapper_PointsAtAggregatorToolsAndFindTools()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);

        var result = await CallAsync(rig, "send_email", new() { ["to"] = "x" });

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        StringAssert.Contains(text, "Unknown tool 'send_email'");
        StringAssert.Contains(text, "Aggregator tools: [");
        StringAssert.Contains(text, "find_tools");
        StringAssert.Contains(text, "list_services");
    }

    [TestMethod]
    public async Task GenuineToolFault_IsStillNotMaskedAsUnknownTool()
    {
        // The new catch is scoped to "no matched tool"; a fault inside a matched tool must not
        // be re-described as a stale tool list.
        await using var rig = await BuildAsync(WrapperToolMode.Eager);

        var result = await CallAsync(rig, "get_service_details", new() { ["serverName"] = "nope" });

        Assert.IsTrue(result.IsError ?? false);
        Assert.IsFalse(TextOf(result).Contains("tool list"), TextOf(result));
    }
}
