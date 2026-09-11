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
/// The prompt counterpart of <see cref="ListChangedEndToEndTests"/> (issue #40): the whole
/// aggregator wired through the real DI extension methods, hosted over pipes and driven by a real
/// client. Downstream prompts must appear in <c>prompts/list</c> as <c>{server}__{prompt}</c>,
/// render through <c>prompts/get</c>, and announce themselves with
/// <c>notifications/prompts/list_changed</c> on the same mutations that send the tool notification.
/// </summary>
[TestClass]
public class PromptListChangedEndToEndTests
{
    private const string Downstream = "probe";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-prompt-e2e-" + Guid.NewGuid().ToString("N"));
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

    [SysDescription("Summarizes a document.")]
    private static string Summarize([SysDescription("Text to summarize")] string text)
        => text == "boom" ? throw new InvalidOperationException("downstream exploded") : "Summarize: " + text;

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static string TextOf(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static string TextOf(GetPromptResult result)
        => string.Join("\n", result.Messages.Select(m => m.Content).OfType<TextContentBlock>().Select(b => b.Text));

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
                [McpServerPrompt.Create(Summarize, new McpServerPromptCreateOptions { Name = "summarize" })],
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
                        NotificationMethods.PromptListChangedNotification,
                        (_, _) => { listChanged.TrySetResult(); return ValueTask.CompletedTask; })
                ]
            }
        };
        var client = await rig.Aggregator.CreateClientAsync(clientOptions, TestTimeout);
        return (client, listChanged);
    }

    [TestMethod]
    public async Task Server_AdvertisesPromptsCapability_WithListChanged()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        Assert.IsNotNull(client.ServerCapabilities.Prompts, "A pre-assigned PromptCollection must turn the capability on.");
        Assert.IsTrue(client.ServerCapabilities.Prompts.ListChanged ?? false);
    }

    [TestMethod]
    public async Task Lazy_FindTools_ActivatesPrompt_NotifiesClient_AndPromptRenders()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, listChanged) = await ConnectAsync(rig);

        var before = await client.ListPromptsAsync(cancellationToken: TestTimeout);
        Assert.AreEqual(0, before.Count, "Lazy mode must start without prompts.");

        var found = await client.CallToolAsync("find_tools",
            new Dictionary<string, object?> { ["query"] = "summarize" }, cancellationToken: TestTimeout);
        Assert.IsFalse(found.IsError ?? false, TextOf(found));

        using var doc = JsonDocument.Parse(TextOf(found));
        Assert.AreEqual(0, doc.RootElement.GetProperty("matches").GetArrayLength(), "No tool is called summarize.");
        var match = doc.RootElement.GetProperty("prompts")[0];
        Assert.AreEqual("probe__summarize", match.GetProperty("prompt").GetString());
        Assert.AreEqual(Downstream, match.GetProperty("server").GetString());
        Assert.AreEqual("summarize", match.GetProperty("downstreamPrompt").GetString());
        Assert.IsTrue(match.GetProperty("activated").GetBoolean());
        Assert.AreEqual("text", match.GetProperty("arguments")[0].GetProperty("name").GetString());

        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var after = await client.ListPromptsAsync(cancellationToken: TestTimeout);
        var prompt = after.SingleOrDefault(p => p.Name == "probe__summarize");
        Assert.IsNotNull(prompt, "The session's prompts/list must include the activated prompt.");
        Assert.AreEqual("text", prompt.ProtocolPrompt.Arguments![0].Name);
        Assert.IsTrue(prompt.ProtocolPrompt.Arguments[0].Required ?? false);

        var result = await client.GetPromptAsync("probe__summarize",
            new Dictionary<string, object?> { ["text"] = "hello" }, cancellationToken: TestTimeout);
        Assert.AreEqual("Summarize: hello", TextOf(result));
    }

    [TestMethod]
    public async Task Lazy_GetServiceDetails_ActivatesThatServersPrompts()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, listChanged) = await ConnectAsync(rig);

        var details = await client.CallToolAsync("get_service_details",
            new Dictionary<string, object?> { ["serverName"] = Downstream }, cancellationToken: TestTimeout);
        Assert.IsFalse(details.IsError ?? false, TextOf(details));

        using var doc = JsonDocument.Parse(TextOf(details));
        Assert.AreEqual("probe__summarize", doc.RootElement.GetProperty("prompts")[0].GetProperty("wrapperName").GetString());

        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var prompts = await client.ListPromptsAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(prompts.Any(p => p.Name == "probe__summarize"));
    }

    [TestMethod]
    public async Task Eager_Sync_ExposesPrompts_AndDisableRemovesThemWithNotification()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Eager);
        var (client, listChanged) = await ConnectAsync(rig);

        await rig.Catalog.SyncAsync(TestTimeout);

        var prompts = await client.ListPromptsAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(prompts.Any(p => p.Name == "probe__summarize"), "Eager mode must list every proxied prompt.");

        var disabled = await client.CallToolAsync("disable_service",
            new Dictionary<string, object?> { ["serverName"] = Downstream }, cancellationToken: TestTimeout);
        Assert.IsFalse(disabled.IsError ?? false, TextOf(disabled));

        await rig.Catalog.PendingSync;
        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var after = await client.ListPromptsAsync(cancellationToken: TestTimeout);
        Assert.IsFalse(after.Any(p => p.Name == "probe__summarize"), "disable_service must remove the proxied prompts.");
    }

    [TestMethod]
    public async Task July2026Client_ReceivesPromptsListChanged_OnlyThroughSubscriptionsListen()
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
                Notifications = new SubscriptionsListenNotifications { PromptsListChanged = true }
            },
            cancellationToken: CancellationToken.None).AsTask();
        await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await rig.Catalog.ActivateServerAsync(rig.Aggregator.Server, Downstream, TestTimeout);

        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var prompts = await client.ListPromptsAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(prompts.Any(p => p.Name == "probe__summarize"));
    }

    [TestMethod]
    public async Task RefreshService_KeepsThePrompt_AndStaysQuietWhenNothingChanged()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Eager);

        await rig.Catalog.SyncAsync(TestTimeout);
        var (client, _) = await ConnectAsync(rig);
        var before = rig.Catalog.ActivePromptWrappers.Single();

        var notifications = 0;
        await using var registration = client.RegisterNotificationHandler(
            NotificationMethods.PromptListChangedNotification,
            (_, _) => { Interlocked.Increment(ref notifications); return ValueTask.CompletedTask; });

        var refreshed = await client.CallToolAsync("refresh_service",
            new Dictionary<string, object?> { ["serverName"] = Downstream }, cancellationToken: TestTimeout);
        Assert.IsFalse(refreshed.IsError ?? false, TextOf(refreshed));
        await rig.Catalog.PendingSync;

        var prompts = await client.ListPromptsAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(prompts.Any(p => p.Name == "probe__summarize"), "The prompt must survive a refresh.");
        Assert.AreSame(before, rig.Catalog.ActivePromptWrappers.Single(), "Unchanged arguments must reuse the wrapper instance.");
        Assert.AreEqual(0, notifications, "No change means no prompts/list_changed.");
    }

    [TestMethod]
    public async Task Lazy_ActivationIsPerSession_AnotherClientSeesNothing()
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
                        NotificationMethods.PromptListChangedNotification,
                        (_, _) => { listChangedB.TrySetResult(); return ValueTask.CompletedTask; })
                ]
            }
        }, TestTimeout);

        var found = await clientA.CallToolAsync("find_tools",
            new Dictionary<string, object?> { ["query"] = "summarize" }, cancellationToken: TestTimeout);
        Assert.IsFalse(found.IsError ?? false, TextOf(found));
        await listChangedA.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var promptsA = await clientA.ListPromptsAsync(cancellationToken: TestTimeout);
        var promptsB = await clientB.ListPromptsAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(promptsA.Any(p => p.Name == "probe__summarize"));
        Assert.IsFalse(promptsB.Any(p => p.Name == "probe__summarize"), "Another client's list must not grow because of A.");
        Assert.IsFalse(listChangedB.Task.IsCompleted);

        // B requests it by name: dispatched through the GetPromptHandler fallback, then listed for B.
        var result = await clientB.GetPromptAsync("probe__summarize",
            new Dictionary<string, object?> { ["text"] = "from B" }, cancellationToken: TestTimeout);
        Assert.AreEqual("Summarize: from B", TextOf(result));

        await listChangedB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var promptsBAfter = await clientB.ListPromptsAsync(cancellationToken: TestTimeout);
        Assert.IsTrue(promptsBAfter.Any(p => p.Name == "probe__summarize"));
    }

    [TestMethod]
    public async Task Lazy_SharedPromptCollectionIsNeverTouched()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        await client.CallToolAsync("find_tools", new Dictionary<string, object?> { ["query"] = "summarize" }, cancellationToken: TestTimeout);
        await client.GetPromptAsync("probe__summarize", new Dictionary<string, object?> { ["text"] = "x" }, cancellationToken: TestTimeout);

        var shared = rig.Provider.GetRequiredService<IOptions<McpServerOptions>>().Value.PromptCollection!;
        Assert.AreEqual(0, shared.Count, "Lazy mode must keep the process-wide prompt list empty.");
    }

    [TestMethod]
    public async Task UnknownPrompt_IsAProtocolError_WithAHint()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        var ex = await Assert.ThrowsAsync<McpException>(async () => await client.GetPromptAsync("probe__nope",
            new Dictionary<string, object?>(), cancellationToken: TestTimeout));

        StringAssert.Contains(ex.Message, "Unknown prompt");
    }

    [TestMethod]
    public async Task PromptRequestedWithoutArguments_NamesTheMissingArgument()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);
        await rig.Catalog.ActivateServerAsync(rig.Aggregator.Server, Downstream, TestTimeout);

        var ex = await Assert.ThrowsAsync<McpException>(async () => await client.GetPromptAsync("probe__summarize", cancellationToken: TestTimeout));

        StringAssert.Contains(ex.Message, "Missing required argument(s): [text]");
    }

    [TestMethod]
    public async Task GetPromptTool_StillWorksAsEscapeHatch()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        var result = await client.CallToolAsync("get_prompt",
            new Dictionary<string, object?>
            {
                ["serverName"] = Downstream,
                ["promptName"] = "summarize",
                ["arguments"] = """{"text":"via proxy"}"""
            }, cancellationToken: TestTimeout);

        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        using var doc = JsonDocument.Parse(TextOf(result));
        var text = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString();
        Assert.AreEqual("Summarize: via proxy", text);
    }

    [TestMethod]
    public async Task GetPromptTool_UnknownPrompt_ReturnsAnErrorResultNamingThePrompts()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        var result = await client.CallToolAsync("get_prompt",
            new Dictionary<string, object?> { ["serverName"] = Downstream, ["promptName"] = "probe__summarize" },
            cancellationToken: TestTimeout);

        Assert.IsTrue(result.IsError ?? false);
        StringAssert.Contains(TextOf(result), "Available prompts: [summarize]");
        StringAssert.Contains(TextOf(result), "promptName: \"summarize\"");
    }

    [TestMethod]
    public async Task GetPromptTool_MissingRequiredArgument_ReturnsAnErrorResultNamingTheArgument()
    {
        // Seen on Claude Desktop: get_prompt without a required argument came back as a bare
        // "Tool execution failed" because only the proxied prompt had the pre-flight.
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        var result = await client.CallToolAsync("get_prompt",
            new Dictionary<string, object?>
            {
                ["serverName"] = Downstream,
                ["promptName"] = "summarize",
                ["arguments"] = """{"style":"terse"}"""
            }, cancellationToken: TestTimeout);

        Assert.IsTrue(result.IsError ?? false, TextOf(result));
        StringAssert.Contains(TextOf(result), "Missing required argument(s): [text]");
        StringAssert.Contains(TextOf(result), "text (required)");
    }

    [TestMethod]
    public async Task GetPromptTool_DownstreamFault_ReturnsAnErrorResultWithTheSignature()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        var result = await client.CallToolAsync("get_prompt",
            new Dictionary<string, object?>
            {
                ["serverName"] = Downstream,
                ["promptName"] = "summarize",
                ["arguments"] = """{"text":"boom"}"""
            }, cancellationToken: TestTimeout);

        Assert.IsTrue(result.IsError ?? false, "A downstream fault on a known prompt must be an error result, not an unhandled exception.");
        StringAssert.Contains(TextOf(result), "Prompt 'summarize' on server 'probe' failed:");
        StringAssert.Contains(TextOf(result), "text (required)");
    }

    [TestMethod]
    public async Task GetPromptTool_MalformedArguments_ReturnsAnErrorResult()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var (client, _) = await ConnectAsync(rig);

        var result = await client.CallToolAsync("get_prompt",
            new Dictionary<string, object?> { ["serverName"] = Downstream, ["promptName"] = "summarize", ["arguments"] = "text=hi" },
            cancellationToken: TestTimeout);

        Assert.IsTrue(result.IsError ?? false);
        StringAssert.Contains(TextOf(result), "encoded as a JSON string");
    }
}
