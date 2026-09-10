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
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rocks;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// The resource counterpart of <see cref="PromptListChangedEndToEndTests"/> (issue #45): the
/// whole aggregator wired through the real DI extension methods, hosted over pipes and driven by
/// a real client. Downstream resources must appear in <c>resources/list</c> (templates in
/// <c>resources/templates/list</c>) as <c>mcp-aggregator://{server}/{uri}</c>, read through
/// <c>resources/read</c> with content URIs rewritten, and announce themselves with
/// <c>notifications/resources/list_changed</c> on the same mutations that send the tool
/// notification. Subscriptions are rejected, not silently accepted.
/// </summary>
[TestClass]
public class ResourceListChangedEndToEndTests
{
    private const string Downstream = "probe";
    private const string ReadmeUri = "mcp-aggregator://probe/file:///readme.md";
    private const string DocTemplateUri = "mcp-aggregator://probe/file:///docs/{name}";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-resource-e2e-" + Guid.NewGuid().ToString("N"));
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
    private static string Echo([SysDescription("Message to echo")] string message) => "echo:" + message;

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static string TextOf(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static string TextOf(ReadResourceResult result)
        => string.Join("\n", result.Contents.OfType<TextResourceContents>().Select(c => c.Text));

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

        var downstreams = new List<InMemoryMcpServer>();
        provider.GetRequiredService<ConnectionManager>().TransportFactoryOverride = _ =>
        {
            var downstream = new InMemoryMcpServer(Downstream,
                prompts: null,
                resources: [DownstreamResourceWrapperTests.ReadmeResource(), DownstreamResourceWrapperTests.DocTemplate()],
                McpServerTool.Create(Echo, new McpServerToolCreateOptions { Name = "echo" }));
            lock (downstreams) downstreams.Add(downstream);
            return downstream.ClientTransport;
        };

        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var catalog = provider.GetRequiredService<WrapperToolCatalog>();
        var aggregator = InMemoryMcpServer.Host("aggregator", options, provider);

        return new Rig(provider, catalog, downstreams, aggregator);
    }

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
                        NotificationMethods.ResourceListChangedNotification,
                        (_, _) => { listChanged.TrySetResult(); return ValueTask.CompletedTask; })
                ]
            }
        };
        var client = await rig.Aggregator.CreateClientAsync(clientOptions, TestTimeout);
        return (client, listChanged);
    }

    [TestMethod]
    public async Task Server_AdvertisesResourcesCapability_WithListChanged_ButNotSubscribe()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        Assert.IsNotNull(client.ServerCapabilities.Resources, "A pre-assigned ResourceCollection must turn the capability on.");
        Assert.IsTrue(client.ServerCapabilities.Resources.ListChanged ?? false);
        Assert.IsFalse(client.ServerCapabilities.Resources.Subscribe ?? false, "Subscriptions are not bridged and must not be advertised.");
    }

    [TestMethod]
    public async Task Lazy_FindTools_ActivatesResource_NotifiesClient_AndResourceReads()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, listChanged) = await ConnectAsync(rig);

        var before = await client.ListResourcesAsync(cancellationToken: TestTimeout);
        Assert.AreEqual(0, before.Count, "Lazy mode must start without resources.");

        var found = await client.CallToolAsync("find_tools",
            new Dictionary<string, object?> { ["query"] = "readme" }, cancellationToken: TestTimeout);
        Assert.IsFalse(found.IsError ?? false, TextOf(found));

        using var doc = JsonDocument.Parse(TextOf(found));
        Assert.AreEqual(0, doc.RootElement.GetProperty("matches").GetArrayLength(), "No tool is called readme.");
        var match = doc.RootElement.GetProperty("resources")[0];
        Assert.AreEqual(ReadmeUri, match.GetProperty("uri").GetString());
        Assert.AreEqual(Downstream, match.GetProperty("server").GetString());
        Assert.AreEqual("file:///readme.md", match.GetProperty("downstreamUri").GetString());
        Assert.AreEqual("readme", match.GetProperty("name").GetString());
        Assert.AreEqual("text/markdown", match.GetProperty("mimeType").GetString());
        Assert.IsFalse(match.GetProperty("isTemplate").GetBoolean());
        Assert.IsTrue(match.GetProperty("activated").GetBoolean());

        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var after = await client.ListResourcesAsync(cancellationToken: TestTimeout);
        var resource = after.SingleOrDefault(r => r.Uri == ReadmeUri);
        Assert.IsNotNull(resource, "The session's resources/list must include the activated resource.");
        Assert.AreEqual("Project readme", resource.Title);
        Assert.AreEqual("text/markdown", resource.MimeType);

        var result = await client.ReadResourceAsync(ReadmeUri, cancellationToken: TestTimeout);
        Assert.AreEqual("# Readme", TextOf(result));
        Assert.AreEqual(ReadmeUri, result.Contents.Single().Uri);
    }

    [TestMethod]
    public async Task Lazy_Templates_AppearInTemplatesList_AndExpand()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, listChanged) = await ConnectAsync(rig);

        var found = await client.CallToolAsync("find_tools",
            new Dictionary<string, object?> { ["query"] = "documentation page" }, cancellationToken: TestTimeout);
        Assert.IsFalse(found.IsError ?? false, TextOf(found));
        using var doc = JsonDocument.Parse(TextOf(found));
        var match = doc.RootElement.GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("isTemplate").GetBoolean());
        Assert.AreEqual(DocTemplateUri, match.GetProperty("uri").GetString());

        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var templates = await client.ListResourceTemplatesAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(templates.Any(t => t.UriTemplate == DocTemplateUri), "Templates go to resources/templates/list.");
        var plain = await client.ListResourcesAsync(cancellationToken: TestTimeout);
        Assert.IsFalse(plain.Any(r => r.Uri == DocTemplateUri), "A template must not show up as a plain resource.");

        var result = await client.ReadResourceAsync("mcp-aggregator://probe/file:///docs/intro", cancellationToken: TestTimeout);
        Assert.AreEqual("doc:intro", TextOf(result));
        Assert.AreEqual("mcp-aggregator://probe/file:///docs/intro", result.Contents.Single().Uri);
    }

    [TestMethod]
    public async Task Lazy_GetServiceDetails_ListsAndActivatesThatServersResources()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, listChanged) = await ConnectAsync(rig);

        var details = await client.CallToolAsync("get_service_details",
            new Dictionary<string, object?> { ["serverName"] = Downstream }, cancellationToken: TestTimeout);
        Assert.IsFalse(details.IsError ?? false, TextOf(details));

        using var doc = JsonDocument.Parse(TextOf(details));
        var resources = doc.RootElement.GetProperty("resources").EnumerateArray().ToList();
        Assert.AreEqual(2, resources.Count);
        var readme = resources.Single(r => !r.GetProperty("isTemplate").GetBoolean());
        Assert.AreEqual(ReadmeUri, readme.GetProperty("uri").GetString());
        Assert.AreEqual("file:///readme.md", readme.GetProperty("downstreamUri").GetString());
        var template = resources.Single(r => r.GetProperty("isTemplate").GetBoolean());
        Assert.AreEqual(DocTemplateUri, template.GetProperty("uri").GetString());

        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var listed = await client.ListResourcesAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(listed.Any(r => r.Uri == ReadmeUri));
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(templates.Any(t => t.UriTemplate == DocTemplateUri));
    }

    [TestMethod]
    public async Task Eager_Sync_ExposesResources_AndDisableRemovesThemWithNotification()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Eager);
        var (client, listChanged) = await ConnectAsync(rig);

        await rig.Catalog.SyncAsync(TestTimeout);

        var resources = await client.ListResourcesAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(resources.Any(r => r.Uri == ReadmeUri), "Eager mode must list every bridged resource.");
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(templates.Any(t => t.UriTemplate == DocTemplateUri));

        var disabled = await client.CallToolAsync("disable_service",
            new Dictionary<string, object?> { ["serverName"] = Downstream }, cancellationToken: TestTimeout);
        Assert.IsFalse(disabled.IsError ?? false, TextOf(disabled));

        await rig.Catalog.PendingSync;
        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var after = await client.ListResourcesAsync(cancellationToken: TestTimeout);
        Assert.IsFalse(after.Any(r => r.Uri == ReadmeUri), "disable_service must remove the bridged resources.");
    }

    [TestMethod]
    public async Task July2026Client_ReceivesResourcesListChanged_OnlyThroughSubscriptionsListen()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, listChanged) = await ConnectAsync(rig, protocolVersion: null);
        Assert.AreEqual("2026-07-28", client.NegotiatedProtocolVersion);

        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ack = client.RegisterNotificationHandler(
            NotificationMethods.SubscriptionsAcknowledgedNotification,
            (_, _) => { acknowledged.TrySetResult(); return ValueTask.CompletedTask; });

        _ = client.SendRequestAsync<SubscriptionsListenRequestParams, EmptyResult>(
            RequestMethods.SubscriptionsListen,
            new SubscriptionsListenRequestParams
            {
                Notifications = new SubscriptionsListenNotifications { ResourcesListChanged = true }
            },
            cancellationToken: CancellationToken.None).AsTask();
        await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await rig.Catalog.ActivateServerAsync(rig.Aggregator.Server, Downstream, TestTimeout);

        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var resources = await client.ListResourcesAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(resources.Any(r => r.Uri == ReadmeUri));
    }

    [TestMethod]
    public async Task RefreshService_KeepsTheResource_AndStaysQuietWhenNothingChanged()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Eager);

        await rig.Catalog.SyncAsync(TestTimeout);
        var (client, _) = await ConnectAsync(rig);
        var before = rig.Catalog.ActiveResourceWrappers.Single(w => !w.IsTemplate);

        var notifications = 0;
        await using var registration = client.RegisterNotificationHandler(
            NotificationMethods.ResourceListChangedNotification,
            (_, _) => { Interlocked.Increment(ref notifications); return ValueTask.CompletedTask; });

        var refreshed = await client.CallToolAsync("refresh_service",
            new Dictionary<string, object?> { ["serverName"] = Downstream }, cancellationToken: TestTimeout);
        Assert.IsFalse(refreshed.IsError ?? false, TextOf(refreshed));
        StringAssert.Contains(TextOf(refreshed), "resources");
        await rig.Catalog.PendingSync;

        var resources = await client.ListResourcesAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(resources.Any(r => r.Uri == ReadmeUri), "The resource must survive a refresh.");
        Assert.AreSame(before, rig.Catalog.ActiveResourceWrappers.Single(w => !w.IsTemplate), "Unchanged metadata must reuse the wrapper instance.");
        Assert.AreEqual(0, notifications, "No change means no resources/list_changed.");
    }

    [TestMethod]
    public async Task Lazy_ActivationIsPerSession_AnotherClientReadsByUri()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (clientA, listChangedA) = await ConnectAsync(rig);

        var optionsB = rig.Provider.GetRequiredService<IOptionsFactory<McpServerOptions>>().Create(Options.DefaultName);
        await using var sessionB = InMemoryMcpServer.Host("aggregator-b", optionsB, rig.Provider);
        var listChangedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientB = await sessionB.CreateClientAsync(new McpClientOptions
        {
            ProtocolVersion = "2025-06-18",
            Handlers = new McpClientHandlers
            {
                NotificationHandlers =
                [
                    new KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>(
                        NotificationMethods.ResourceListChangedNotification,
                        (_, _) => { listChangedB.TrySetResult(); return ValueTask.CompletedTask; })
                ]
            }
        }, TestTimeout);

        var found = await clientA.CallToolAsync("find_tools",
            new Dictionary<string, object?> { ["query"] = "readme" }, cancellationToken: TestTimeout);
        Assert.IsFalse(found.IsError ?? false, TextOf(found));
        await listChangedA.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var resourcesA = await clientA.ListResourcesAsync(cancellationToken: TestTimeout);
        var resourcesB = await clientB.ListResourcesAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(resourcesA.Any(r => r.Uri == ReadmeUri));
        Assert.IsFalse(resourcesB.Any(r => r.Uri == ReadmeUri), "Another client's list must not grow because of A.");
        Assert.IsFalse(listChangedB.Task.IsCompleted);

        // B reads it by URI: dispatched through the ReadResourceHandler fallback, then listed for B.
        var result = await clientB.ReadResourceAsync(ReadmeUri, cancellationToken: TestTimeout);
        Assert.AreEqual("# Readme", TextOf(result));

        await listChangedB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var resourcesBAfter = await clientB.ListResourcesAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(resourcesBAfter.Any(r => r.Uri == ReadmeUri));
    }

    [TestMethod]
    public async Task Lazy_SharedResourceCollectionIsNeverTouched()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        await client.CallToolAsync("find_tools", new Dictionary<string, object?> { ["query"] = "readme" }, cancellationToken: TestTimeout);
        await client.ReadResourceAsync(ReadmeUri, cancellationToken: TestTimeout);
        await client.ReadResourceAsync("mcp-aggregator://probe/file:///docs/x", cancellationToken: TestTimeout);

        var shared = rig.Provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ResourceCollection!;
        Assert.AreEqual(0, shared.Count, "Lazy mode must keep the process-wide resource list empty.");
    }

    [TestMethod]
    public async Task UnknownResource_IsAProtocolError_WithAHint_AndTheLegacyCode()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await client.ReadResourceAsync("mcp-aggregator://probe/file:///nope.md", cancellationToken: TestTimeout));
        StringAssert.Contains(ex.Message, "Unknown resource");
        StringAssert.Contains(ex.Message, "file:///readme.md");
        Assert.AreEqual(McpErrorCode.ResourceNotFound, ex.ErrorCode, "A pre-2026-07-28 client expects -32002.");

        var notAggregatorForm = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await client.ReadResourceAsync("file:///readme.md", cancellationToken: TestTimeout));
        StringAssert.Contains(notAggregatorForm.Message, "mcp-aggregator://");
    }

    [TestMethod]
    public async Task UnknownResource_UsesInvalidParams_ForA2026Client()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig, protocolVersion: null);

        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await client.ReadResourceAsync("mcp-aggregator://probe/file:///nope.md", cancellationToken: TestTimeout));

        Assert.AreEqual(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    [TestMethod]
    public async Task Subscribe_IsRejected_NotSilentlyIgnored()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await client.SubscribeToResourceAsync(ReadmeUri, cancellationToken: TestTimeout));
        StringAssert.Contains(ex.Message, "not supported");
        Assert.AreEqual(McpErrorCode.InvalidRequest, ex.ErrorCode);

        var unsub = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await client.UnsubscribeFromResourceAsync(ReadmeUri, cancellationToken: TestTimeout));
        Assert.AreEqual(McpErrorCode.InvalidRequest, unsub.ErrorCode);
    }

    // ---------------------------------------------------------------- read_resource escape hatch

    [TestMethod]
    public async Task ReadResourceTool_ReadsByDownstreamUri_OrAggregatorUri()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        foreach (var uri in new[] { "file:///readme.md", ReadmeUri })
        {
            var result = await client.CallToolAsync("read_resource",
                new Dictionary<string, object?> { ["serverName"] = Downstream, ["uri"] = uri }, cancellationToken: TestTimeout);

            Assert.IsFalse(result.IsError ?? false, TextOf(result));
            var embedded = result.Content.OfType<EmbeddedResourceBlock>().Single();
            Assert.AreEqual(ReadmeUri, embedded.Resource.Uri, "Contents come back in aggregator form.");
            Assert.AreEqual("# Readme", ((TextResourceContents)embedded.Resource).Text);
        }
    }

    [TestMethod]
    public async Task ReadResourceTool_UnknownResource_ReturnsAnErrorResultNamingTheResources()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        var result = await client.CallToolAsync("read_resource",
            new Dictionary<string, object?> { ["serverName"] = Downstream, ["uri"] = "file:///nope.md" },
            cancellationToken: TestTimeout);

        Assert.IsTrue(result.IsError ?? false);
        StringAssert.Contains(TextOf(result), "Unknown resource");
        StringAssert.Contains(TextOf(result), "file:///readme.md");
        StringAssert.Contains(TextOf(result), "file:///docs/{name}");
    }

    [TestMethod]
    public async Task ReadResourceTool_WrongServer_ReturnsAnErrorResult()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        var mismatch = await client.CallToolAsync("read_resource",
            new Dictionary<string, object?> { ["serverName"] = "other", ["uri"] = ReadmeUri },
            cancellationToken: TestTimeout);
        Assert.IsTrue(mismatch.IsError ?? false);
        StringAssert.Contains(TextOf(mismatch), "belongs to server 'probe'");

        var unknownServer = await client.CallToolAsync("read_resource",
            new Dictionary<string, object?> { ["serverName"] = "ghost", ["uri"] = "file:///readme.md" },
            cancellationToken: TestTimeout);
        Assert.IsTrue(unknownServer.IsError ?? false);
        StringAssert.Contains(TextOf(unknownServer), "Registered servers: [probe]");

        var self = await client.CallToolAsync("read_resource",
            new Dictionary<string, object?> { ["serverName"] = "mcp-aggregator", ["uri"] = "file:///readme.md" },
            cancellationToken: TestTimeout);
        Assert.IsTrue(self.IsError ?? false);
        StringAssert.Contains(TextOf(self), "this aggregator");
    }
}
