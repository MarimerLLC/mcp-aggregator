using SysDescription = System.ComponentModel.DescriptionAttribute;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using McpAggregator.Core;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tests.Helpers;
using McpAggregator.Core.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// The typed wrapper tool (issue #39). Each test hosts a real wrapper in an in-process MCP server
/// and calls it through a real client, so the whole path — JSON-RPC in, wrapper pre-flight,
/// <see cref="ToolProxyHandler"/>, downstream round-trip, JSON-RPC out — is exercised.
/// </summary>
[TestClass]
public class DownstreamToolWrapperTests
{
    private const string Downstream = "probe";

    private string _dataDir = null!;
    private static int _echoCalls;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-wrapper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        Interlocked.Exchange(ref _echoCalls, 0);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- downstream doubles

    [SysDescription("Echoes a message back.")]
    private static CallToolResult Echo(
        [SysDescription("Message to echo")] string message,
        [SysDescription("Optional suffix")] string? suffix = null)
    {
        Interlocked.Increment(ref _echoCalls);
        return new()
        {
            IsError = false,
            Content = [new TextContentBlock { Text = message + (suffix ?? string.Empty) }]
        };
    }

    [SysDescription("Lists files. Always fails in this test double.")]
    private static CallToolResult FailingListFiles([SysDescription("Folder to list")] string? folder = null)
        => new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "Graph API error: The resource could not be found." }]
        };

    private static McpServerTool EchoTool()
        => McpServerTool.Create(Echo, new McpServerToolCreateOptions { Name = "echo" });

    private static McpServerTool FailingTool()
        => McpServerTool.Create(FailingListFiles, new McpServerToolCreateOptions { Name = "list_files" });

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static string TextOf(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private sealed record Rig(
        WrapperHarness Harness,
        InMemoryMcpServer Aggregator,
        McpClient Client,
        DownstreamToolWrapper Wrapper) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Aggregator.DisposeAsync();
            await Harness.DisposeAsync();
        }
    }

    /// <summary>Builds the wrapper for one downstream tool and hosts it in its own MCP server.</summary>
    private async Task<Rig> HostWrapperAsync(McpServerTool downstreamTool, string toolName)
    {
        var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Eager, (Downstream, [downstreamTool]));
        var wrappers = await harness.Catalog.GetWrappersAsync(Downstream, TestTimeout);
        var wrapper = wrappers.Single(w => w.ToolName == toolName);

        var aggregator = new InMemoryMcpServer("aggregator", wrapper);
        var client = await aggregator.CreateClientAsync(TestTimeout);
        return new Rig(harness, aggregator, client, wrapper);
    }

    private static MeterListener CaptureInvocations(List<Dictionary<string, object?>> captured)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AggregatorTelemetry.ServiceName && instrument.Name == "mcp_tool_invocations_total")
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags) dict[tag.Key] = tag.Value;
            lock (captured) captured.Add(dict);
        });
        listener.Start();
        return listener;
    }

    private static Dictionary<string, object?>? LastInvocationOf(List<Dictionary<string, object?>> captured, string tool)
    {
        lock (captured)
            return captured.LastOrDefault(t => Equals(t.GetValueOrDefault("tool_name"), tool) && Equals(t.GetValueOrDefault("server_name"), Downstream));
    }

    // ---------------------------------------------------------------- schema fidelity

    [TestMethod]
    public async Task ProtocolTool_CarriesDownstreamInputSchemaUnchanged()
    {
        var echo = EchoTool();
        await using var rig = await HostWrapperAsync(echo, "echo");

        var downstreamSchema = JsonNode.Parse(echo.ProtocolTool.InputSchema.GetRawText());
        var wrapperSchema = JsonNode.Parse(rig.Wrapper.ProtocolTool.InputSchema.GetRawText());

        Assert.IsTrue(JsonNode.DeepEquals(downstreamSchema, wrapperSchema),
            $"Wrapper schema differs from downstream.\nDownstream: {downstreamSchema}\nWrapper: {wrapperSchema}");
        Assert.AreEqual("probe__echo", rig.Wrapper.ProtocolTool.Name);
        StringAssert.StartsWith(rig.Wrapper.ProtocolTool.Description, "[probe]");
    }

    [TestMethod]
    public async Task ProtocolTool_ExposesTypedParametersToTheClient()
    {
        // What the LLM sees: a tool with 'message' and 'suffix', not 'serverName/toolName/arguments'.
        await using var rig = await HostWrapperAsync(EchoTool(), "echo");

        var tools = await rig.Client.ListToolsAsync(cancellationToken: TestTimeout);
        var tool = tools.Single(t => t.Name == "probe__echo");

        var properties = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        CollectionAssert.AreEquivalent(new[] { "message", "suffix" }, properties);
        var required = tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "message" }, required);
    }

    [TestMethod]
    public async Task ProtocolTool_MetaIdentifiesTheDownstream()
    {
        await using var rig = await HostWrapperAsync(EchoTool(), "echo");

        var meta = rig.Wrapper.ProtocolTool.Meta!["mcpAggregator"]!;
        Assert.AreEqual(Downstream, (string?)meta["serverName"]);
        Assert.AreEqual("echo", (string?)meta["toolName"]);
        Assert.AreEqual(rig.Harness.Registry.Get(Downstream).Id, (string?)meta["serverId"]);
    }

    // ---------------------------------------------------------------- pre-flight

    [TestMethod]
    public async Task MissingRequiredArgument_ReturnsErrorNamingTheParameter_WithoutCallingDownstream()
    {
        // The rockbot #420 failure shape, on the wrapper path: the model omits the argument. The
        // error must name it (the #37 problem) and the downstream must not have been called.
        await using var rig = await HostWrapperAsync(EchoTool(), "echo");

        var result = await rig.Client.CallToolAsync("probe__echo", new Dictionary<string, object?>(), cancellationToken: TestTimeout);

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        StringAssert.Contains(text, "Missing required parameter(s): [message]");
        StringAssert.Contains(text, "\"required\":[\"message\"]", "The schema must be embedded so the caller can self-correct.");
        Assert.AreEqual(0, _echoCalls, "A pre-flight failure must not reach the downstream.");
    }

    [TestMethod]
    public async Task OmittingAnOptionalArgument_Succeeds()
    {
        await using var rig = await HostWrapperAsync(EchoTool(), "echo");

        var result = await rig.Client.CallToolAsync("probe__echo",
            new Dictionary<string, object?> { ["message"] = "hello" }, cancellationToken: TestTimeout);

        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        Assert.AreEqual("hello", TextOf(result));
    }

    // ---------------------------------------------------------------- pass-through

    [TestMethod]
    public async Task SuccessResult_PassesThroughWithTypedArguments()
    {
        await using var rig = await HostWrapperAsync(EchoTool(), "echo");

        var result = await rig.Client.CallToolAsync("probe__echo",
            new Dictionary<string, object?> { ["message"] = "hello", ["suffix"] = "!" }, cancellationToken: TestTimeout);

        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        Assert.AreEqual("hello!", TextOf(result));
        Assert.AreEqual(1, _echoCalls);
    }

    [TestMethod]
    public async Task DownstreamIsError_PropagatesFlagAndOriginalText()
    {
        // #24/#34 on the wrapper path.
        await using var rig = await HostWrapperAsync(FailingTool(), "list_files");

        var result = await rig.Client.CallToolAsync("probe__list_files",
            new Dictionary<string, object?> { ["folder"] = "/Documents" }, cancellationToken: TestTimeout);

        Assert.IsTrue(result.IsError ?? false, "isError must survive the wrapper hop.");
        StringAssert.Contains(TextOf(result), "Graph API error");
    }

    [TestMethod]
    public async Task DownstreamIsError_WithUnknownKey_CarriesTheArgumentHint()
    {
        // The ToolProxyHandler self-correction hint is shared with invoke_tool: an unrecognized key
        // on an error result gets the schema attached.
        await using var rig = await HostWrapperAsync(FailingTool(), "list_files");

        var result = await rig.Client.CallToolAsync("probe__list_files",
            new Dictionary<string, object?> { ["path"] = "/Documents" }, cancellationToken: TestTimeout);

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        StringAssert.Contains(text, "Graph API error");
        StringAssert.Contains(text, "Unrecognized argument key(s): [path]");
    }

    [TestMethod]
    public async Task DisabledServer_ReturnsErrorResultInsteadOfProtocolFault()
    {
        await using var rig = await HostWrapperAsync(EchoTool(), "echo");

        await rig.Harness.Registry.SetEnabledAsync(Downstream, false);
        await rig.Harness.Connections.DisconnectAsync(Downstream);

        var result = await rig.Client.CallToolAsync("probe__echo",
            new Dictionary<string, object?> { ["message"] = "hello" }, cancellationToken: TestTimeout);

        Assert.IsTrue(result.IsError ?? false);
        StringAssert.Contains(TextOf(result), "is unavailable");
    }

    // ---------------------------------------------------------------- telemetry

    [TestMethod]
    public async Task Invocation_IsTaggedViaWrapper()
    {
        await using var rig = await HostWrapperAsync(EchoTool(), "echo");
        var captured = new List<Dictionary<string, object?>>();
        using var listener = CaptureInvocations(captured);

        await rig.Client.CallToolAsync("probe__echo",
            new Dictionary<string, object?> { ["message"] = "hello" }, cancellationToken: TestTimeout);

        var mine = LastInvocationOf(captured, "echo");
        Assert.IsNotNull(mine, "No invocation metric was recorded for the wrapper call.");
        Assert.AreEqual(InvocationPath.Wrapper, mine["via"]);
        Assert.AreEqual("success", mine["result"]);
    }

    [TestMethod]
    public async Task InvokeTool_IsTaggedViaInvokeTool()
    {
        // The other half of the measurement: the generic proxy path must carry its own tag.
        await using var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Eager, (Downstream, [EchoTool()]));
        var captured = new List<Dictionary<string, object?>>();
        using var listener = CaptureInvocations(captured);

        await harness.Proxy.InvokeAsync(Downstream, "echo", """{"message":"hi"}""", TestTimeout);

        var mine = LastInvocationOf(captured, "echo");
        Assert.IsNotNull(mine);
        Assert.AreEqual(InvocationPath.InvokeTool, mine["via"]);
    }
}
