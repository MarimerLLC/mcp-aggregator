using SysDescription = System.ComponentModel.DescriptionAttribute;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using McpAggregator.Core.Storage;
using McpAggregator.Core.Tests.Helpers;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rocks;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// Guards how the aggregator treats a downstream that answers "I don't implement that"
/// (JSON-RPC -32601) or that is asked for a tool it does not declare.
///
/// Both were observed against a live downstream: <c>prompts/list</c> on a tools-only server was
/// logged as a non-retryable error on every service-details fetch, and an unknown tool name
/// escaped <c>invoke_tool</c> as an unhandled exception instead of the self-correcting error
/// result an argument mismatch already produced (issue #29).
/// </summary>
[TestClass]
public class DownstreamCapabilityTests
{
    private const string DownstreamName = "capability-probe";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-capability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- downstream tool double

    [SysDescription("Echoes a message back. The only tool this test double declares.")]
    private static CallToolResult Echo([SysDescription("Message to echo")] string message)
        => new()
        {
            IsError = false,
            Content = [new TextContentBlock { Text = message }]
        };

    // ---------------------------------------------------------------- harness

    private sealed record Harness(
        ToolProxyHandler Proxy,
        ToolIndex Index,
        ConnectionManager Connections,
        InMemoryMcpServer Downstream) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Connections.DisposeAsync();
            await Downstream.DisposeAsync();
        }
    }

    /// <summary>
    /// Registers one server whose transport points at an in-process MCP server declaring only
    /// <paramref name="tools"/> — and, deliberately, no prompts capability at all.
    /// </summary>
    private async Task<Harness> CreateHarnessAsync(params McpServerTool[] tools)
    {
        var server = TestHelpers.StdioServer(DownstreamName);

        var expectations = new IRegistryPersistenceCreateExpectations();
        expectations.Setups.LoadAsync(Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new RegistryData { Servers = [server] }));
        expectations.Setups.SaveAsync(Arg.Any<RegistryData>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);

        var options = TestHelpers.OptionsOf(TestHelpers.DefaultAggregatorOptions(_dataDir));

        var registry = new ServerRegistry(
            expectations.Instance(),
            options,
            TestHelpers.NullLoggerOf<ServerRegistry>());
        await registry.EnsureLoadedAsync();

        var downstream = new InMemoryMcpServer(DownstreamName, tools);

        var connections = new ConnectionManager(
            registry,
            options,
            NullLoggerFactory.Instance,
            TestHelpers.NullLoggerOf<ConnectionManager>())
        {
            TransportFactoryOverride = _ => downstream.ClientTransport
        };

        var skillStore = new SkillStore(options, TestHelpers.NullLoggerOf<SkillStore>());
        var index = new ToolIndex(registry, connections, skillStore, options,
            TestHelpers.NullLoggerOf<ToolIndex>());

        var proxy = new ToolProxyHandler(connections, index, options,
            TestHelpers.NullLoggerOf<ToolProxyHandler>());

        return new Harness(proxy, index, connections, downstream);
    }

    private static McpServerTool EchoTool()
        => McpServerTool.Create(Echo, new McpServerToolCreateOptions { Name = "echo" });

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static string TextOf(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    // ---------------------------------------------------------------- capability classification

    [TestMethod]
    public void IsUnsupportedCapability_TrueForMethodNotFound()
    {
        var ex = new McpProtocolException("Method 'prompts/list' is not available.", McpErrorCode.MethodNotFound);
        Assert.IsTrue(ConnectionManager.IsUnsupportedCapability(ex));
    }

    [TestMethod]
    public void IsUnsupportedCapability_FalseForOtherProtocolErrors()
    {
        // -32602 is how a server reports a bad call to a method it *does* implement, so it must
        // not be mistaken for an absent capability.
        var ex = new McpProtocolException("Unknown tool: 'nope'", McpErrorCode.InvalidParams);
        Assert.IsFalse(ConnectionManager.IsUnsupportedCapability(ex));
    }

    [TestMethod]
    public void IsUnsupportedCapability_FalseForTransportFailures()
    {
        Assert.IsFalse(ConnectionManager.IsUnsupportedCapability(new IOException("socket closed")));
        Assert.IsFalse(ConnectionManager.IsUnsupportedCapability(new TimeoutException()));
    }

    // ---------------------------------------------------------------- prompts/list is optional

    [TestMethod]
    public async Task GetPromptsForServerAsync_ServerWithoutPromptSupport_ReturnsEmpty()
    {
        // The live symptom: a tools-only downstream rejects prompts/list with -32601, which used
        // to surface as a non-retryable error rather than "this server has no prompts".
        await using var harness = await CreateHarnessAsync(EchoTool());

        var prompts = await harness.Index.GetPromptsForServerAsync(DownstreamName, TestTimeout);

        Assert.AreEqual(0, prompts.Count, "A server without prompt support must index as zero prompts.");
    }

    [TestMethod]
    public async Task GetPromptsForServerAsync_ServerWithoutPromptSupport_CachesEmptyResult()
    {
        // The empty result is cached so the unsupported call is not re-issued on every fetch.
        await using var harness = await CreateHarnessAsync(EchoTool());

        var first = await harness.Index.GetPromptsForServerAsync(DownstreamName, TestTimeout);
        var second = await harness.Index.GetPromptsForServerAsync(DownstreamName, TestTimeout);

        Assert.AreEqual(0, first.Count);
        Assert.AreEqual(0, second.Count);
        Assert.AreSame(first, second, "The second call must be served from cache, not re-probed.");
    }

    [TestMethod]
    public async Task GetServiceDetails_ServerWithoutPromptSupport_StillReportsTools()
    {
        // The whole point: an absent optional capability must not cost us the tool index.
        await using var harness = await CreateHarnessAsync(EchoTool());

        var tools = await harness.Index.GetToolsForServerAsync(DownstreamName, TestTimeout);

        Assert.AreEqual(1, tools.Count);
        Assert.AreEqual("echo", tools[0].Name);
    }

    // ---------------------------------------------------------------- unknown tool names

    [TestMethod]
    public async Task InvokeAsync_UnknownToolName_ReturnsErrorResultInsteadOfThrowing()
    {
        await using var harness = await CreateHarnessAsync(EchoTool());

        var result = await harness.Proxy.InvokeAsync(
            DownstreamName, "no_such_tool_xyz", "{}", TestTimeout);

        Assert.IsTrue(result.IsError ?? false, "An unknown tool must come back flagged as an error.");
    }

    [TestMethod]
    public async Task InvokeAsync_UnknownToolName_NamesTheToolsThatDoExist()
    {
        await using var harness = await CreateHarnessAsync(EchoTool());

        var result = await harness.Proxy.InvokeAsync(
            DownstreamName, "no_such_tool_xyz", "{}", TestTimeout);

        var text = TextOf(result);
        StringAssert.Contains(text, "Unknown tool 'no_such_tool_xyz'");
        StringAssert.Contains(text, "echo", "The caller needs the valid tool names to self-correct.");
        StringAssert.Contains(text, DownstreamName);
    }

    [TestMethod]
    public async Task InvokeAsync_KnownToolStillSucceeds()
    {
        // Regression guard: the unknown-tool path must not intercept ordinary calls.
        await using var harness = await CreateHarnessAsync(EchoTool());

        var result = await harness.Proxy.InvokeAsync(
            DownstreamName, "echo", """{"message":"hello"}""", TestTimeout);

        Assert.IsFalse(result.IsError ?? false);
        StringAssert.Contains(TextOf(result), "hello");
    }
}
