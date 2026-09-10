using SysDescription = System.ComponentModel.DescriptionAttribute;
using System.Diagnostics.Metrics;
using System.Text.Json;
using McpAggregator.Core;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tests.Helpers;
using McpAggregator.Core.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// The proxied prompt (issue #40). Each test hosts a real <see cref="DownstreamPromptWrapper"/> in
/// an in-process MCP server and requests it through a real client, so the whole path — JSON-RPC
/// in, wrapper pre-flight, <see cref="ToolProxyHandler.GetPromptAsync"/>, downstream round-trip,
/// JSON-RPC out — is exercised.
/// </summary>
[TestClass]
public class DownstreamPromptWrapperTests
{
    private const string Downstream = "probe";

    private string _dataDir = null!;
    private static int _summarizeCalls;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        Interlocked.Exchange(ref _summarizeCalls, 0);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- downstream doubles

    [SysDescription("Summarizes a document in a given style.")]
    private static string Summarize(
        [SysDescription("Text to summarize")] string text,
        [SysDescription("Writing style")] string? style = null)
    {
        Interlocked.Increment(ref _summarizeCalls);
        return $"Summarize ({style ?? "default"}): {text}";
    }

    [SysDescription("Always fails in this test double.")]
    private static string Broken([SysDescription("Anything")] string input)
        => throw new InvalidOperationException("template engine exploded");

    private static McpServerPrompt SummarizePrompt()
        => McpServerPrompt.Create(Summarize, new McpServerPromptCreateOptions { Name = "summarize", Title = "Summarize a document" });

    private static McpServerPrompt BrokenPrompt()
        => McpServerPrompt.Create(Broken, new McpServerPromptCreateOptions { Name = "broken" });

    private static McpServerTool EchoTool()
        => McpServerTool.Create(([SysDescription("Message")] string message) => message, new McpServerToolCreateOptions { Name = "echo" });

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static string TextOf(GetPromptResult result)
        => string.Join("\n", result.Messages.Select(m => m.Content).OfType<TextContentBlock>().Select(b => b.Text));

    private sealed record Rig(
        WrapperHarness Harness,
        InMemoryMcpServer Aggregator,
        McpClient Client,
        DownstreamPromptWrapper Wrapper) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Aggregator.DisposeAsync();
            await Harness.DisposeAsync();
        }
    }

    /// <summary>Builds the wrapper for one downstream prompt and hosts it in its own MCP server.</summary>
    private async Task<Rig> HostWrapperAsync(McpServerPrompt downstreamPrompt, string promptName)
    {
        var harness = await WrapperHarness.CreateWithPromptsAsync(_dataDir, WrapperToolMode.Eager,
            (Downstream, [EchoTool()], [downstreamPrompt]));
        var wrappers = await harness.Catalog.GetPromptWrappersAsync(Downstream, TestTimeout);
        var wrapper = wrappers.Single(w => w.PromptName == promptName);

        var aggregator = new InMemoryMcpServer("aggregator", new McpServerPrompt[] { wrapper });
        var client = await aggregator.CreateClientAsync(TestTimeout);
        return new Rig(harness, aggregator, client, wrapper);
    }

    private static MeterListener CapturePromptGets(List<Dictionary<string, object?>> captured)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AggregatorTelemetry.ServiceName && instrument.Name == "mcp_prompt_gets_total")
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

    // ---------------------------------------------------------------- fidelity

    [TestMethod]
    public async Task ProtocolPrompt_CarriesDownstreamArgumentsUnchanged()
    {
        var summarize = SummarizePrompt();
        await using var rig = await HostWrapperAsync(summarize, "summarize");

        Assert.AreEqual("probe__summarize", rig.Wrapper.ProtocolPrompt.Name);
        Assert.AreEqual("Summarize a document", rig.Wrapper.ProtocolPrompt.Title);
        StringAssert.StartsWith(rig.Wrapper.ProtocolPrompt.Description, "[probe]");
        // The wrapper is built from the wire copy, so compare structurally.
        var expected = summarize.ProtocolPrompt.Arguments!.Select(a => (a.Name, a.Description, a.Required ?? false)).ToList();
        var actual = rig.Wrapper.ProtocolPrompt.Arguments!.Select(a => (a.Name, a.Description, a.Required ?? false)).ToList();
        CollectionAssert.AreEqual(expected, actual, "The downstream argument list must be carried through unchanged.");
    }

    [TestMethod]
    public async Task ProtocolPrompt_ExposesTypedArgumentsToTheClient()
    {
        // What the host sees: a prompt with 'text' (required) and 'style' (optional), not
        // 'serverName/promptName/arguments'.
        await using var rig = await HostWrapperAsync(SummarizePrompt(), "summarize");

        var prompts = await rig.Client.ListPromptsAsync(cancellationToken: TestTimeout);
        var prompt = prompts.Single(p => p.Name == "probe__summarize");

        var arguments = prompt.ProtocolPrompt.Arguments!;
        CollectionAssert.AreEquivalent(new[] { "text", "style" }, arguments.Select(a => a.Name).ToList());
        Assert.IsTrue(arguments.Single(a => a.Name == "text").Required ?? false);
        Assert.IsFalse(arguments.Single(a => a.Name == "style").Required ?? false);
    }

    [TestMethod]
    public async Task ProtocolPrompt_MetaIdentifiesTheDownstream()
    {
        await using var rig = await HostWrapperAsync(SummarizePrompt(), "summarize");

        var meta = rig.Wrapper.ProtocolPrompt.Meta!["mcpAggregator"]!;
        Assert.AreEqual(Downstream, (string?)meta["serverName"]);
        Assert.AreEqual("summarize", (string?)meta["promptName"]);
        Assert.AreEqual(rig.Harness.Registry.Get(Downstream).Id, (string?)meta["serverId"]);
    }

    [TestMethod]
    public void Fingerprint_ChangesWhenArgumentsChange_AndIsStableOtherwise()
    {
        var a = new Prompt { Name = "p", Arguments = [new PromptArgument { Name = "x", Required = true }] };
        var same = new Prompt { Name = "p", Arguments = [new PromptArgument { Name = "x", Required = true }] };
        var different = new Prompt { Name = "p", Arguments = [new PromptArgument { Name = "x", Required = false }] };

        Assert.AreEqual(DownstreamPromptWrapper.Fingerprint(a), DownstreamPromptWrapper.Fingerprint(same));
        Assert.AreNotEqual(DownstreamPromptWrapper.Fingerprint(a), DownstreamPromptWrapper.Fingerprint(different));
    }

    // ---------------------------------------------------------------- round-trips

    [TestMethod]
    public async Task GetPrompt_ForwardsArguments_AndReturnsDownstreamMessages()
    {
        await using var rig = await HostWrapperAsync(SummarizePrompt(), "summarize");

        var result = await rig.Client.GetPromptAsync("probe__summarize",
            new Dictionary<string, object?> { ["text"] = "the report", ["style"] = "terse" }, cancellationToken: TestTimeout);

        Assert.AreEqual(1, result.Messages.Count);
        Assert.AreEqual(Role.User, result.Messages[0].Role);
        Assert.AreEqual("Summarize (terse): the report", TextOf(result));
        Assert.AreEqual(1, _summarizeCalls);
    }

    [TestMethod]
    public async Task GetPrompt_RecordsTheWrapperPath_InTelemetry()
    {
        var captured = new List<Dictionary<string, object?>>();
        using var listener = CapturePromptGets(captured);
        await using var rig = await HostWrapperAsync(SummarizePrompt(), "summarize");

        await rig.Client.GetPromptAsync("probe__summarize",
            new Dictionary<string, object?> { ["text"] = "x" }, cancellationToken: TestTimeout);

        Dictionary<string, object?>? last;
        lock (captured)
            last = captured.LastOrDefault(t => Equals(t.GetValueOrDefault("prompt_name"), "summarize") && Equals(t.GetValueOrDefault("server_name"), Downstream));
        Assert.IsNotNull(last);
        Assert.AreEqual(InvocationPath.Wrapper, last["via"]);
        Assert.AreEqual("success", last["result"]);
    }

    // ---------------------------------------------------------------- pre-flight and errors

    [TestMethod]
    public async Task MissingRequiredArgument_FailsNamingTheArgument_WithoutCallingDownstream()
    {
        await using var rig = await HostWrapperAsync(SummarizePrompt(), "summarize");

        var ex = await Assert.ThrowsAsync<McpException>(async () => await rig.Client.GetPromptAsync("probe__summarize",
            new Dictionary<string, object?> { ["style"] = "terse" }, cancellationToken: TestTimeout));

        StringAssert.Contains(ex.Message, "Missing required argument(s): [text]");
        StringAssert.Contains(ex.Message, "text (required)");
        Assert.AreEqual(0, _summarizeCalls, "The pre-flight must not reach the downstream.");
    }

    [TestMethod]
    public async Task DownstreamFailure_SurfacesAsAReadableError()
    {
        await using var rig = await HostWrapperAsync(BrokenPrompt(), "broken");

        var ex = await Assert.ThrowsAsync<McpException>(async () => await rig.Client.GetPromptAsync("probe__broken",
            new Dictionary<string, object?> { ["input"] = "x" }, cancellationToken: TestTimeout));

        Assert.IsFalse(string.IsNullOrWhiteSpace(ex.Message));
    }

    [TestMethod]
    public async Task UnreachableServer_SurfacesTheUnavailableMessage()
    {
        await using var rig = await HostWrapperAsync(SummarizePrompt(), "summarize");

        // Drop the downstream and its connection so the next request has to reconnect and fail.
        await rig.Harness.Connections.DisconnectAsync(Downstream);
        rig.Harness.DownstreamTools.Remove(Downstream);

        var ex = await Assert.ThrowsAsync<McpException>(async () => await rig.Client.GetPromptAsync("probe__summarize",
            new Dictionary<string, object?> { ["text"] = "x" }, cancellationToken: TestTimeout));

        StringAssert.Contains(ex.Message, "unavailable");
    }

    [TestMethod]
    public async Task NonStringArgumentValue_IsForwardedAsItsJsonText()
    {
        await using var rig = await HostWrapperAsync(SummarizePrompt(), "summarize");

        var result = await rig.Client.GetPromptAsync("probe__summarize",
            new Dictionary<string, object?> { ["text"] = 42 }, cancellationToken: TestTimeout);

        Assert.AreEqual("Summarize (default): 42", TextOf(result));
    }

    [TestMethod]
    public async Task ServerWithoutPromptSupport_YieldsNoWrappers()
    {
        await using var harness = await WrapperHarness.CreateWithPromptsAsync(_dataDir, WrapperToolMode.Eager,
            ("toolsonly", [EchoTool()], null));

        var wrappers = await harness.Catalog.GetPromptWrappersAsync("toolsonly", TestTimeout);

        Assert.AreEqual(0, wrappers.Count);
        _ = JsonSerializer.Serialize(wrappers); // the empty list is ordinary data, not a fault
    }
}
