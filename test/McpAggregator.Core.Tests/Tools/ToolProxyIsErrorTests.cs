using SysDescription = System.ComponentModel.DescriptionAttribute;
using System.Text.Json;
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
/// Regression guard for issue #24: an <c>isError: true</c> result from a downstream MCP server must
/// reach the aggregator's callers still flagged as an error. If the flag is flattened, every
/// consumer (agents, call recorders, learning loops) silently records logical failures as
/// successes. These tests pin the flag across the whole proxy path — downstream MCP round-trip,
/// <see cref="ToolProxyHandler"/>, the <c>invoke_tool</c> MCP surface, and REST JSON.
/// </summary>
[TestClass]
public class ToolProxyIsErrorTests
{
    private const string GraphError = "Graph API error: The resource could not be found.";
    private const string DownstreamName = "graph";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-iserror-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- downstream tool doubles

    [SysDescription("Lists files. Always fails in this test double.")]
    private static CallToolResult FailingListFiles([SysDescription("Folder to list")] string? folder = null)
        => new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = GraphError }]
        };

    [SysDescription("Lists files. Always succeeds in this test double.")]
    private static CallToolResult SucceedingListFiles([SysDescription("Folder to list")] string? folder = null)
        => new()
        {
            IsError = false,
            Content = [new TextContentBlock { Text = "report.docx" }]
        };

    // ---------------------------------------------------------------- harness

    private sealed record Harness(
        ToolProxyHandler Proxy,
        ServerRegistry Registry,
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
    /// Wires a registered server whose transport is redirected at an in-process MCP server hosting
    /// <paramref name="tools"/>, then builds the real registry → connections → index → proxy chain.
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
        var toolIndex = new ToolIndex(registry, connections, skillStore, options,
            TestHelpers.NullLoggerOf<ToolIndex>());

        var proxy = new ToolProxyHandler(connections, toolIndex, options,
            TestHelpers.NullLoggerOf<ToolProxyHandler>());

        return new Harness(proxy, registry, connections, downstream);
    }

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static string TextOf(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    // ---------------------------------------------------------------- doubles for type mismatches

    [SysDescription("Sends an email. Never fails once bound.")]
    private static CallToolResult SendEmail(
        [SysDescription("Recipients")] string[] to,
        [SysDescription("Subject")] string subject)
        => new() { IsError = false, Content = [new TextContentBlock { Text = $"sent to {string.Join(",", to)}: {subject}" }] };

    // ---------------------------------------------------------------- tests

    [TestMethod]
    public async Task InvokeAsync_ValueOfWrongType_AttachesTheSchemaHint()
    {
        // The small-model slip from the measurement runs: every key present, but 'to' sent as a
        // string where the schema wants an array. The downstream SDK sanitizes that to "An error
        // occurred invoking 'send_email'.", which names nothing; the proxy must add the schema.
        var tool = McpServerTool.Create(SendEmail, new McpServerToolCreateOptions { Name = "send_email" });
        await using var harness = await CreateHarnessAsync(tool);

        var result = await harness.Proxy.InvokeAsync(
            DownstreamName, "send_email", """{"to":"alice@example.com","subject":"Lunch"}""", TestTimeout);

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        StringAssert.Contains(text, "Parameter 'to' is declared as array but you sent a string.");
        StringAssert.Contains(text, "\"type\":\"array\"", "The schema must be attached so the caller can fix the shape.");
    }

    [SysDescription("Moves an email. Always fails at runtime in this test double.")]
    private static string MoveEmail(
        [SysDescription("Account")] string accountId,
        [SysDescription("Message")] string emailId,
        [SysDescription("Destination folder")] string destinationFolder)
        => throw new McpException("Failed to move email");

    [SysDescription("Lists files. Throws a plain exception in this test double.")]
    private static string CrashingListFiles([SysDescription("Folder to list")] string folder)
        => throw new InvalidOperationException("provider exploded");

    [TestMethod]
    public async Task InvokeAsync_McpExceptionOnSchemaValidArguments_GetsNoHint()
    {
        // Issue #50: calendar-mcp move_email, three string arguments all declared as strings, failed
        // on a provider bug. The SDK reports that as "An error occurred invoking 'move_email':
        // Failed to move email", which must come back unchanged, not re-described as a type error.
        var tool = McpServerTool.Create(MoveEmail, new McpServerToolCreateOptions { Name = "move_email" });
        await using var harness = await CreateHarnessAsync(tool);

        var result = await harness.Proxy.InvokeAsync(DownstreamName, "move_email",
            """{"accountId":"work","emailId":"AAMk1","destinationFolder":"Archive"}""", TestTimeout);

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        StringAssert.Contains(text, "Failed to move email");
        Assert.IsFalse(text.Contains("Argument mismatch"), text);
        Assert.IsFalse(text.Contains("declared"), text);
        Assert.AreEqual(1, result.Content.Count, "Nothing may be appended to a runtime error.");
    }

    [TestMethod]
    public async Task InvokeAsync_PlainExceptionOnSchemaValidArguments_GetsHedgedNoteOnly()
    {
        // The SDK sanitizes a plain exception to the same bare text it uses for a binding failure,
        // so the proxy cannot tell them apart: it may attach the schema but must not claim a cause.
        var tool = McpServerTool.Create(CrashingListFiles, new McpServerToolCreateOptions { Name = "list_files" });
        await using var harness = await CreateHarnessAsync(tool);

        var result = await harness.Proxy.InvokeAsync(
            DownstreamName, "list_files", """{"folder":"/Documents"}""", TestTimeout);

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        StringAssert.Contains(text, "the downstream gave no detail");
        StringAssert.Contains(text, "otherwise the failure is on the downstream's side");
        Assert.IsFalse(text.Contains("Argument mismatch"), text);
        Assert.IsFalse(text.Contains("declared as"), text);
    }

    [TestMethod]
    public async Task InvokeAsync_ToolErrorMentioningValidation_GetsNoHint()
    {
        // The old heuristic matched the bare word "validation" anywhere in the error.
        var tool = McpServerTool.Create(
            ([SysDescription("Folder to list")] string folder) => new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "Mailbox validation failed on the provider." }]
            },
            new McpServerToolCreateOptions { Name = "list_files" });
        await using var harness = await CreateHarnessAsync(tool);

        var result = await harness.Proxy.InvokeAsync(
            DownstreamName, "list_files", """{"folder":"/Documents"}""", TestTimeout);

        Assert.IsTrue(result.IsError ?? false);
        Assert.AreEqual("Mailbox validation failed on the provider.", TextOf(result));
    }

    [TestMethod]
    public async Task InvokeAsync_OtherSdkRejectsArguments_AttachesSchemaWithoutTypeClaim()
    {
        // The TypeScript SDK's own validation text is positive evidence the arguments were rejected,
        // but it does not say a value had the wrong type, so neither does the hint.
        var tool = McpServerTool.Create(
            ([SysDescription("Folder to list")] string folder) => new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "MCP error -32602: Invalid arguments for tool list_files: folder must match pattern" }]
            },
            new McpServerToolCreateOptions { Name = "list_files" });
        await using var harness = await CreateHarnessAsync(tool);

        var result = await harness.Proxy.InvokeAsync(
            DownstreamName, "list_files", """{"folder":"Documents"}""", TestTimeout);

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        StringAssert.Contains(text, "the downstream rejected a value");
        StringAssert.Contains(text, "Re-invoke with arguments matching this input schema");
        Assert.IsFalse(text.Contains("declared as"), text);
    }

    [TestMethod]
    public async Task InvokeAsync_SchemaValidArgumentsWithAGenuineToolError_GetsNoHint()
    {
        // Regression guard: a tool-side failure on well-formed arguments must not be re-described
        // as an argument problem.
        var tool = McpServerTool.Create(FailingListFiles, new McpServerToolCreateOptions { Name = "list_files" });
        await using var harness = await CreateHarnessAsync(tool);

        var result = await harness.Proxy.InvokeAsync(
            DownstreamName, "list_files", """{"folder":"/Documents"}""", TestTimeout);

        Assert.IsTrue(result.IsError ?? false);
        Assert.IsFalse(TextOf(result).Contains("Argument mismatch"), TextOf(result));
    }

    [TestMethod]
    public async Task InvokeAsync_DownstreamReturnsIsError_PreservesFlag()
    {
        // The exact scenario from issue #24: a downstream tool reports a logical failure.
        var tool = McpServerTool.Create(FailingListFiles,
            new McpServerToolCreateOptions { Name = "list_files" });

        await using var harness = await CreateHarnessAsync(tool);

        var result = await harness.Proxy.InvokeAsync(
            DownstreamName, "list_files", """{"folder":"/Documents"}""", TestTimeout);

        Assert.IsTrue(result.IsError ?? false, "isError must survive the proxy hop.");
        StringAssert.Contains(TextOf(result), GraphError, "Original error text must survive.");
    }

    [TestMethod]
    public async Task InvokeAsync_DownstreamSucceeds_DoesNotFlagError()
    {
        var tool = McpServerTool.Create(SucceedingListFiles,
            new McpServerToolCreateOptions { Name = "list_files" });

        await using var harness = await CreateHarnessAsync(tool);

        var result = await harness.Proxy.InvokeAsync(
            DownstreamName, "list_files", """{"folder":"/Documents"}""", TestTimeout);

        Assert.IsFalse(result.IsError ?? false, "A successful call must not be flagged as an error.");
        StringAssert.Contains(TextOf(result), "report.docx");
    }

    [TestMethod]
    public async Task InvokeAsync_ErrorWithArgumentMismatch_KeepsIsErrorWhenHintAppended()
    {
        // Guards the one place that mutates the downstream result: the self-correction hint is
        // appended to Content, and IsError must not be lost in the process.
        var tool = McpServerTool.Create(FailingListFiles,
            new McpServerToolCreateOptions { Name = "list_files" });

        await using var harness = await CreateHarnessAsync(tool);

        // "path" is not a declared property of the tool's schema — triggers the hint.
        var result = await harness.Proxy.InvokeAsync(
            DownstreamName, "list_files", """{"path":"/Documents"}""", TestTimeout);

        var text = TextOf(result);
        StringAssert.Contains(text, "Argument mismatch", "Expected the self-correction hint.");
        Assert.IsTrue(result.IsError ?? false, "isError must survive the hint being appended.");
    }

    [TestMethod]
    public async Task InvokeTool_OverRealMcpRoundTrip_PreservesIsError()
    {
        // Two hops: consumer client → aggregator's invoke_tool → downstream. This is the link that
        // would break on an SDK upgrade that re-wrapped tool results as structured content.
        var downstreamTool = McpServerTool.Create(FailingListFiles,
            new McpServerToolCreateOptions { Name = "list_files" });

        await using var harness = await CreateHarnessAsync(downstreamTool);

        var proxy = harness.Proxy;
        var registry = harness.Registry;
        var invokeTool = McpServerTool.Create(
            (string serverName, string toolName, string? arguments, CancellationToken ct)
                => ConsumerTools.InvokeTool(proxy, registry, serverName, toolName, arguments, ct),
            new McpServerToolCreateOptions { Name = "invoke_tool" });

        await using var aggregator = new InMemoryMcpServer("mcp-aggregator", invokeTool);
        await using var client = await aggregator.CreateClientAsync(TestTimeout);

        var result = await client.CallToolAsync("invoke_tool", new Dictionary<string, object?>
        {
            ["serverName"] = DownstreamName,
            ["toolName"] = "list_files",
            ["arguments"] = """{"folder":"/Documents"}"""
        }, cancellationToken: TestTimeout);

        Assert.IsTrue(result.IsError ?? false, "isError must survive the invoke_tool MCP hop.");
        StringAssert.Contains(TextOf(result), GraphError, "Original error text must survive.");
    }

    [TestMethod]
    public async Task InvokeTool_AgainstTheAggregatorItself_SaysToCallTheToolDirectly()
    {
        // Seen on Claude Desktop: list_services advertises the aggregator as a service, so after
        // show_admin_tools the model tried invoke_tool(serverName: "mcp-aggregator", toolName:
        // "update_skill"). The answer must redirect, not just say "unknown server".
        await using var harness = await CreateHarnessAsync();

        var result = await ConsumerTools.InvokeTool(harness.Proxy, harness.Registry,
            "MCP-Aggregator", "update_skill", "{\"serverName\":\"adjutant\"}", CancellationToken.None);

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        StringAssert.Contains(text, "is this aggregator");
        StringAssert.Contains(text, "Call 'update_skill' directly");
        StringAssert.Contains(text, "show_admin_tools");
        Assert.IsFalse(text.Contains("Registered servers"), "Must not fall through to the unknown-server hint.");
    }

    [TestMethod]
    public void CallToolResult_WebJson_EmitsIsError()
    {
        // ServicesController.InvokeTool does Ok(result), which serializes with ASP.NET web defaults
        // rather than the SDK's own options — assert the flag survives that path too.
        var result = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = GraphError }]
        };

        var json = JsonSerializer.Serialize(result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        StringAssert.Contains(json, "\"isError\":true");
        StringAssert.Contains(json, GraphError);
    }
}
