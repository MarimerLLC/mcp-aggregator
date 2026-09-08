using SysDescription = System.ComponentModel.DescriptionAttribute;
using System.Text.Json;
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
/// The whole aggregator, wired through the real DI extension methods, hosted as an MCP server over
/// pipes and driven by a real client. Proves the two SDK behaviors the design leans on (issue #39,
/// Risks): the server reads <c>ToolCollection</c> live rather than snapshotting it, and a
/// <c>Changed</c> event on the collection reaches the client as
/// <c>notifications/tools/list_changed</c>.
/// <para>
/// Delivery depends on the negotiated protocol. A pre-2026-07-28 client (Claude Desktop, Claude
/// Code and rockbot all still negotiate via <c>initialize</c>) receives a session-wide broadcast.
/// A 2026-07-28 client only receives list-changed notifications it explicitly asked for through a
/// <c>subscriptions/listen</c> stream (SEP-2575), which the SDK client does not open on its own.
/// Both paths are covered here.
/// </para>
/// </summary>
[TestClass]
public class ListChangedEndToEndTests
{
    private const string Downstream = "probe";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-e2e-" + Guid.NewGuid().ToString("N"));
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

    private sealed record Rig(
        ServiceProvider Provider,
        WrapperToolCatalog Catalog,
        List<InMemoryMcpServer> Downstreams,
        InMemoryMcpServer Aggregator) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Aggregator.DisposeAsync();
            await Provider.GetRequiredService<ConnectionManager>().DisposeAsync();
            List<InMemoryMcpServer> all;
            lock (Downstreams) all = [.. Downstreams];
            foreach (var d in all)
                await d.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }

    /// <summary>
    /// Builds the aggregator exactly as the hosts do (AddAggregatorCore + AddAggregatorMcpServer +
    /// WithToolsFromAssembly), swaps in an in-memory registry and downstream, and hosts the
    /// DI-built <see cref="McpServerOptions"/> over pipes.
    /// </summary>
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
        services.AddAggregatorMcpServer()
            .WithToolsFromAssembly(typeof(ConsumerTools).Assembly);

        var provider = services.BuildServiceProvider();

        // One in-memory downstream per connection: an in-process pipe pair cannot be reconnected,
        // and refresh_service disconnects.
        var downstreams = new List<InMemoryMcpServer>();
        provider.GetRequiredService<ConnectionManager>().TransportFactoryOverride = _ =>
        {
            var downstream = new InMemoryMcpServer(Downstream,
                McpServerTool.Create(Echo, new McpServerToolCreateOptions { Name = "echo" }));
            lock (downstreams) downstreams.Add(downstream);
            return downstream.ClientTransport;
        };

        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var catalog = provider.GetRequiredService<WrapperToolCatalog>();
        var aggregator = InMemoryMcpServer.Host("aggregator", options, provider);

        return new Rig(provider, catalog, downstreams, aggregator);
    }

    /// <summary>
    /// Connects as today's hosts do: the legacy <c>initialize</c> handshake at protocol 2025-06-18,
    /// where the server broadcasts <c>tools/list_changed</c> to the session.
    /// </summary>
    private static Task<(McpClient Client, TaskCompletionSource ListChanged)> ConnectAsync(Rig rig)
        => ConnectAsync(rig, "2025-06-18");

    private static async Task<(McpClient Client, TaskCompletionSource ListChanged)> ConnectAsync(Rig rig, string? protocolVersion)
    {
        var listChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientOptions = new McpClientOptions
        {
            ProtocolVersion = protocolVersion,
            Handlers = new McpClientHandlers
            {
                NotificationHandlers =
                [
                    new KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>(
                        NotificationMethods.ToolListChangedNotification,
                        (_, _) => { listChanged.TrySetResult(); return ValueTask.CompletedTask; })
                ]
            }
        };
        var client = await rig.Aggregator.CreateClientAsync(clientOptions, TestTimeout);
        return (client, listChanged);
    }

    [TestMethod]
    public async Task Lazy_FindTools_ActivatesWrapper_NotifiesClient_AndWrapperIsCallable()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, listChanged) = await ConnectAsync(rig);

        var before = await client.ListToolsAsync(cancellationToken: TestTimeout);
        Assert.IsFalse(before.Any(t => t.Name == "probe__echo"), "Lazy mode must start without wrappers.");
        Assert.IsTrue(before.Any(t => t.Name == "find_tools"));

        // Step 1: the model searches.
        var found = await client.CallToolAsync("find_tools",
            new Dictionary<string, object?> { ["query"] = "echo" }, cancellationToken: TestTimeout);
        Assert.IsFalse(found.IsError ?? false, TextOf(found));

        using var doc = JsonDocument.Parse(TextOf(found));
        var match = doc.RootElement.GetProperty("matches")[0];
        Assert.AreEqual("probe__echo", match.GetProperty("tool").GetString());
        Assert.AreEqual(Downstream, match.GetProperty("server").GetString());
        Assert.IsTrue(match.GetProperty("activated").GetBoolean());
        Assert.AreEqual(JsonValueKind.Object, match.GetProperty("inputSchema").ValueKind);
        Assert.AreEqual("Lazy", doc.RootElement.GetProperty("mode").GetString());

        // Step 2: the host is told the tool list changed …
        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // … and re-listing shows the wrapper with the downstream's schema.
        var after = await client.ListToolsAsync(cancellationToken: TestTimeout);
        var wrapper = after.SingleOrDefault(t => t.Name == "probe__echo");
        Assert.IsNotNull(wrapper, "The server must read ToolCollection live, not snapshot it.");
        Assert.AreEqual("message", wrapper.JsonSchema.GetProperty("required")[0].GetString());

        // Step 3: the model calls the typed tool with typed arguments.
        var result = await client.CallToolAsync("probe__echo",
            new Dictionary<string, object?> { ["message"] = "hello" }, cancellationToken: TestTimeout);
        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        Assert.AreEqual("echo:hello", TextOf(result));
    }

    [TestMethod]
    public async Task Lazy_GetServiceDetails_ActivatesThatServersWrappers()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, listChanged) = await ConnectAsync(rig);

        var details = await client.CallToolAsync("get_service_details",
            new Dictionary<string, object?> { ["serverName"] = Downstream }, cancellationToken: TestTimeout);
        Assert.IsFalse(details.IsError ?? false, TextOf(details));

        using var doc = JsonDocument.Parse(TextOf(details));
        Assert.AreEqual("probe__echo", doc.RootElement.GetProperty("tools")[0].GetProperty("wrapperName").GetString());
        Assert.IsFalse(string.IsNullOrEmpty(doc.RootElement.GetProperty("id").GetString()), "id must be surfaced.");

        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var tools = await client.ListToolsAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(tools.Any(t => t.Name == "probe__echo"));
    }

    [TestMethod]
    public async Task Eager_Sync_ExposesWrappers_AndDisableRemovesThemWithNotification()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Eager);
        var (client, listChanged) = await ConnectAsync(rig);

        // The hosted service is not running in a bare ServiceProvider; sync as it would.
        await rig.Catalog.SyncAsync(TestTimeout);

        var tools = await client.ListToolsAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(tools.Any(t => t.Name == "probe__echo"), "Eager mode must list every wrapper.");

        var disabled = await client.CallToolAsync("disable_service",
            new Dictionary<string, object?> { ["serverName"] = Downstream }, cancellationToken: TestTimeout);
        Assert.IsFalse(disabled.IsError ?? false, TextOf(disabled));

        await rig.Catalog.PendingSync;
        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var after = await client.ListToolsAsync(cancellationToken: TestTimeout);
        Assert.IsFalse(after.Any(t => t.Name == "probe__echo"), "disable_service must remove the wrappers.");
        Assert.IsTrue(after.Any(t => t.Name == "list_services"), "Aggregator tools must be untouched.");
    }

    [TestMethod]
    public async Task July2026Client_ReceivesListChanged_OnlyThroughSubscriptionsListen()
    {
        // The 2026-07-28 path: no broadcast; the client must open a subscriptions/listen stream.
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, listChanged) = await ConnectAsync(rig, protocolVersion: null);
        Assert.AreEqual("2026-07-28", client.NegotiatedProtocolVersion);

        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ack = client.RegisterNotificationHandler(
            NotificationMethods.SubscriptionsAcknowledgedNotification,
            (_, _) => { acknowledged.TrySetResult(); return ValueTask.CompletedTask; });

        // Long-lived request; it completes only when the subscription ends, so do not await it.
        // The server acknowledges the subscription with a notification before fanning anything out.
        _ = client.SendRequestAsync<SubscriptionsListenRequestParams, EmptyResult>(
            RequestMethods.SubscriptionsListen,
            new SubscriptionsListenRequestParams
            {
                Notifications = new SubscriptionsListenNotifications { ToolsListChanged = true }
            },
            cancellationToken: CancellationToken.None).AsTask();
        await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await rig.Catalog.ActivateServerAsync(Downstream, TestTimeout);

        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var tools = await client.ListToolsAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(tools.Any(t => t.Name == "probe__echo"));
    }

    [TestMethod]
    public async Task RefreshService_KeepsTheWrapper_AndStaysQuietWhenNothingChanged()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Eager);
        var (client, _) = await ConnectAsync(rig);
        await rig.Catalog.SyncAsync(TestTimeout);
        var before = rig.Catalog.ActiveWrappers.Single();

        var notifications = 0;
        await using var registration = client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (_, _) => { Interlocked.Increment(ref notifications); return ValueTask.CompletedTask; });

        // refresh_service invalidates the cache and disconnects; the catalog re-reads the
        // downstream. Same schema, so the wrapper instance is reused, the collection does not
        // change, and the client is not told to re-list for nothing.
        var refreshed = await client.CallToolAsync("refresh_service",
            new Dictionary<string, object?> { ["serverName"] = Downstream }, cancellationToken: TestTimeout);
        Assert.IsFalse(refreshed.IsError ?? false, TextOf(refreshed));
        await rig.Catalog.PendingSync;

        var tools = await client.ListToolsAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(tools.Any(t => t.Name == "probe__echo"), "The wrapper must survive a refresh.");
        Assert.AreSame(before, rig.Catalog.ActiveWrappers.Single(), "Unchanged schema must reuse the wrapper instance.");
        Assert.AreEqual(0, notifications, "No change means no list_changed.");
    }

    [TestMethod]
    public async Task InvokeTool_StillWorksAsEscapeHatch()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        var result = await client.CallToolAsync("invoke_tool",
            new Dictionary<string, object?>
            {
                ["serverName"] = Downstream,
                ["toolName"] = "echo",
                ["arguments"] = """{"message":"via proxy"}"""
            }, cancellationToken: TestTimeout);

        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        Assert.AreEqual("echo:via proxy", TextOf(result));
    }

    [TestMethod]
    public async Task WrapperCalledWithoutArguments_NamesTheMissingParameter()
    {
        // Acceptance criterion: "wrapper with missing required argument (error names the parameter)".
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);
        await rig.Catalog.ActivateServerAsync(Downstream, TestTimeout);

        var result = await client.CallToolAsync("probe__echo", cancellationToken: TestTimeout);

        Assert.IsTrue(result.IsError ?? false);
        StringAssert.Contains(TextOf(result), "Missing required parameter(s): [message]");
    }
}
