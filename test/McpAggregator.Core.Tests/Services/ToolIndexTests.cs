using SysDescription = System.ComponentModel.DescriptionAttribute;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tests.Helpers;
using McpAggregator.Core.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tests.Services;

/// <summary>
/// The <see cref="Core.Services.ToolIndex.ToolsChanged"/> event is what keeps the typed wrapper
/// tools honest after a refresh (issue #39).
/// </summary>
[TestClass]
public class ToolIndexTests
{
    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-index-" + Guid.NewGuid().ToString("N"));
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
    private static string Echo([SysDescription("Message")] string message) => message;

    [SysDescription("Echoes a message back, v2.")]
    private static string EchoV2([SysDescription("Message")] string message, [SysDescription("Mode")] string mode) => message + mode;

    private static McpServerTool Tool(Delegate method, string name)
        => McpServerTool.Create(method, new McpServerToolCreateOptions { Name = name });

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    [TestMethod]
    public async Task InvalidateCache_ForOneServer_RaisesToolsChangedForIt()
    {
        await using var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")]), ("beta", [Tool(Echo, "echo")]));
        var raised = new List<string>();
        harness.Index.ToolsChanged += name => raised.Add(name);

        harness.Index.InvalidateCache("alpha");

        CollectionAssert.AreEqual(new[] { "alpha" }, raised);
    }

    [TestMethod]
    public async Task InvalidateCache_ForAll_RaisesToolsChangedForEveryCachedServer()
    {
        await using var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")]), ("beta", [Tool(Echo, "echo")]));
        await harness.Index.GetToolsForServerAsync("alpha", TestTimeout);
        await harness.Index.GetToolsForServerAsync("beta", TestTimeout);
        var raised = new List<string>();
        harness.Index.ToolsChanged += name => raised.Add(name);

        harness.Index.InvalidateCache();

        CollectionAssert.AreEquivalent(new[] { "alpha", "beta" }, raised);
    }

    [TestMethod]
    public async Task GetToolsForServerAsync_PopulatesWrapperNameAndProtocolTool()
    {
        await using var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")]));

        var tools = await harness.Index.GetToolsForServerAsync("alpha", TestTimeout);

        Assert.AreEqual("alpha__echo", tools[0].WrapperName);
        Assert.IsNotNull(tools[0].Protocol, "The protocol Tool must be kept for the wrapper.");
        Assert.AreEqual("echo", tools[0].Protocol!.Name);
    }

    [TestMethod]
    public async Task Refetch_AfterTtlExpiry_RaisesToolsChangedOnlyWhenTheToolSetDiffers()
    {
        // Zero TTL makes every fetch a re-fetch so the fingerprint comparison runs each time.
        await using var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Eager,
            o => o.IndexCacheTtl = TimeSpan.Zero,
            ("alpha", [Tool(Echo, "echo")]));

        await harness.Index.GetToolsForServerAsync("alpha", TestTimeout);
        var raised = new List<string>();
        harness.Index.ToolsChanged += name => raised.Add(name);

        // Same downstream, same schema: a refetch must be silent.
        await harness.Index.GetToolsForServerAsync("alpha", TestTimeout);
        Assert.AreEqual(0, raised.Count, "An unchanged tool set must not raise.");

        var downstream = harness.DownstreamTools["alpha"];
        downstream.Remove(downstream["echo"]);
        downstream.Add(Tool(EchoV2, "echo"));

        await harness.Index.GetToolsForServerAsync("alpha", TestTimeout);

        CollectionAssert.AreEqual(new[] { "alpha" }, raised);
    }

    // ---------------------------------------------------------------- prompts (issue #40)

    [SysDescription("Summarizes text.")]
    private static string Summarize([SysDescription("Text")] string text) => text;

    [SysDescription("Summarizes text, v2 with a style.")]
    private static string SummarizeV2([SysDescription("Text")] string text, [SysDescription("Style")] string style) => text + style;

    private static McpServerPrompt Prompt(Delegate method, string name)
        => McpServerPrompt.Create(method, new McpServerPromptCreateOptions { Name = name });

    [TestMethod]
    public async Task GetPromptsForServer_PopulatesWrapperNameAndProtocol()
    {
        await using var harness = await WrapperHarness.CreateWithPromptsAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")], [Prompt(Summarize, "summarize")]));

        var prompts = await harness.Index.GetPromptsForServerAsync("alpha", TestTimeout);

        Assert.AreEqual(1, prompts.Count);
        Assert.AreEqual("alpha__summarize", prompts[0].WrapperName);
        Assert.IsNotNull(prompts[0].Protocol, "The protocol Prompt must be kept for the wrapper.");
        Assert.AreEqual("summarize", prompts[0].Protocol!.Name);
        Assert.AreEqual("text", prompts[0].Arguments[0].Name);
        Assert.IsTrue(prompts[0].Arguments[0].Required);
    }

    [TestMethod]
    public async Task InvalidateCache_ForOneServer_RaisesPromptsChangedForIt()
    {
        await using var harness = await WrapperHarness.CreateWithPromptsAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")], [Prompt(Summarize, "summarize")]),
            ("beta", [Tool(Echo, "echo")], [Prompt(Summarize, "summarize")]));
        var raised = new List<string>();
        harness.Index.PromptsChanged += name => raised.Add(name);

        harness.Index.InvalidateCache("alpha");

        CollectionAssert.AreEqual(new[] { "alpha" }, raised);
    }

    [TestMethod]
    public async Task Refetch_AfterTtlExpiry_RaisesPromptsChangedOnlyWhenThePromptSetDiffers()
    {
        await using var harness = await WrapperHarness.CreateWithPromptsAsync(_dataDir, WrapperToolMode.Eager,
            o => o.IndexCacheTtl = TimeSpan.Zero,
            ("alpha", [Tool(Echo, "echo")], [Prompt(Summarize, "summarize")]));

        await harness.Index.GetPromptsForServerAsync("alpha", TestTimeout);
        var raised = new List<string>();
        harness.Index.PromptsChanged += name => raised.Add(name);

        await harness.Index.GetPromptsForServerAsync("alpha", TestTimeout);
        Assert.AreEqual(0, raised.Count, "An unchanged prompt set must not raise.");

        var downstream = harness.DownstreamPrompts["alpha"];
        downstream.Remove(downstream["summarize"]);
        downstream.Add(Prompt(SummarizeV2, "summarize"));

        await harness.Index.GetPromptsForServerAsync("alpha", TestTimeout);

        CollectionAssert.AreEqual(new[] { "alpha" }, raised);
    }

    [TestMethod]
    public async Task ServerWithoutPromptSupport_IndexesZeroPrompts_WithWrapperNamesUntouched()
    {
        await using var harness = await WrapperHarness.CreateWithPromptsAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")], null));

        var prompts = await harness.Index.GetPromptsForServerAsync("alpha", TestTimeout);

        Assert.AreEqual(0, prompts.Count);
    }
    // ---------------------------------------------------------------- resources (issue #45)

    [SysDescription("The project readme.")]
    private static string Readme() => "# Readme";

    [SysDescription("The project readme, now with a title.")]
    private static string ReadmeV2() => "# Readme v2";

    [SysDescription("One documentation page by name.")]
    private static string Doc([SysDescription("Page name")] string name) => "doc:" + name;

    private static McpServerResource Resource(Delegate method, string uriTemplate, string name, string? title = null)
        => McpServerResource.Create(method, new McpServerResourceCreateOptions { UriTemplate = uriTemplate, Name = name, Title = title });

    [TestMethod]
    public async Task GetResourcesForServer_PopulatesBothKinds_WithAggregatorUris()
    {
        await using var harness = await WrapperHarness.CreateWithResourcesAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")], null, [Resource(Readme, "file:///readme.md", "readme"), Resource(Doc, "file:///docs/{name}", "doc")]));

        var resources = await harness.Index.GetResourcesForServerAsync("alpha", TestTimeout);

        Assert.AreEqual(2, resources.Count);
        var readme = resources.Single(r => !r.IsTemplate);
        Assert.AreEqual("file:///readme.md", readme.DownstreamUri);
        Assert.AreEqual("mcp-aggregator://alpha/file:///readme.md", readme.Uri);
        Assert.AreEqual("readme", readme.Name);
        Assert.IsNotNull(readme.Protocol, "The protocol Resource must be kept for the wrapper.");
        Assert.IsNull(readme.ProtocolTemplate);

        var doc = resources.Single(r => r.IsTemplate);
        Assert.AreEqual("file:///docs/{name}", doc.DownstreamUri);
        Assert.AreEqual("mcp-aggregator://alpha/file:///docs/{name}", doc.Uri);
        Assert.IsNotNull(doc.ProtocolTemplate, "The protocol ResourceTemplate must be kept for the wrapper.");
        Assert.IsNull(doc.Protocol);

        var details = await harness.Index.GetDetailsAsync("alpha", TestTimeout);
        Assert.AreEqual(2, details.Resources.Count, "get_service_details carries the resources.");
    }

    [TestMethod]
    public async Task InvalidateCache_ForOneServer_RaisesResourcesChangedForIt()
    {
        await using var harness = await WrapperHarness.CreateWithResourcesAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")], null, [Resource(Readme, "file:///readme.md", "readme")]),
            ("beta", [Tool(Echo, "echo")], null, [Resource(Readme, "file:///readme.md", "readme")]));
        var raised = new List<string>();
        harness.Index.ResourcesChanged += name => raised.Add(name);

        harness.Index.InvalidateCache("alpha");

        CollectionAssert.AreEqual(new[] { "alpha" }, raised);
    }

    [TestMethod]
    public async Task Refetch_AfterTtlExpiry_RaisesResourcesChangedOnlyWhenTheResourceSetDiffers()
    {
        await using var harness = await WrapperHarness.CreateWithResourcesAsync(_dataDir, WrapperToolMode.Eager,
            o => o.IndexCacheTtl = TimeSpan.Zero,
            ("alpha", [Tool(Echo, "echo")], null, [Resource(Readme, "file:///readme.md", "readme")]));

        await harness.Index.GetResourcesForServerAsync("alpha", TestTimeout);
        var raised = new List<string>();
        harness.Index.ResourcesChanged += name => raised.Add(name);

        await harness.Index.GetResourcesForServerAsync("alpha", TestTimeout);
        Assert.AreEqual(0, raised.Count, "An unchanged resource set must not raise.");

        var downstream = harness.DownstreamResources["alpha"];
        downstream.Remove(downstream["file:///readme.md"]);
        downstream.Add(Resource(ReadmeV2, "file:///readme.md", "readme", title: "Readme"));

        await harness.Index.GetResourcesForServerAsync("alpha", TestTimeout);

        CollectionAssert.AreEqual(new[] { "alpha" }, raised);
    }

    [TestMethod]
    public async Task ServerWithoutResourceSupport_IndexesZeroResources()
    {
        await using var harness = await WrapperHarness.CreateWithResourcesAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")], null, null));

        var resources = await harness.Index.GetResourcesForServerAsync("alpha", TestTimeout);

        Assert.AreEqual(0, resources.Count);
        var details = await harness.Index.GetDetailsAsync("alpha", TestTimeout);
        Assert.AreEqual(0, details.Resources.Count);
    }

    // ---------------------------------------------------------------- skill freshness (issue #41)

    [SysDescription("Echoes a message back, now with a warning about rate limits.")]
    private static string EchoRedescribed([SysDescription("Message")] string message) => message;

    private static async Task<string?> FreshnessOfAsync(WrapperHarness harness, string server)
    {
        var index = await harness.Index.GetIndexAsync(TestTimeout);
        return index.Single(s => s.Name == server).SkillFreshness;
    }

    /// <summary>
    /// Installs a <c>prompts/list</c> handler on every spawned downstream that fails with a
    /// non-<c>MethodNotFound</c> error while <paramref name="fail"/> returns true. The SDK merges
    /// the handler's output with the prompt collection, so the healthy branch contributes nothing.
    /// </summary>
    private static void FailPromptsListWhen(WrapperHarness harness, Func<bool> fail)
    {
        harness.ConfigureDownstream = (_, options) =>
            options.Handlers.ListPromptsHandler = (_, _) => fail()
                ? throw new McpProtocolException("prompts backend is down", McpErrorCode.InternalError)
                : new ValueTask<ListPromptsResult>(new ListPromptsResult());
    }

    [TestMethod]
    public async Task SkillFreshness_IsFresh_RightAfterTheBaselineIsCaptured()
    {
        await using var harness = await WrapperHarness.CreateWithPromptsAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")], [Prompt(Summarize, "summarize")]));
        await harness.Registry.UpdateSkillFlagAsync("alpha", true, TestTimeout);

        var recorded = await SkillSnapshot.CaptureAsync(harness.Registry, harness.Index, "alpha", TestTimeout);

        Assert.IsTrue(recorded);
        var server = harness.Registry.Get("alpha");
        Assert.AreEqual("1.0.0", server.SkillRecordedVersion, "The downstream's reported version is the recorded one.");
        Assert.IsNotNull(server.SkillRecordedAt);
        Assert.IsNotNull(server.SkillRecordedFingerprint);
        Assert.AreEqual(64, server.SkillRecordedFingerprint.Length);
        Assert.IsTrue(server.SkillRecordedFingerprint.All(c => char.IsAsciiHexDigitLower(c)), "Expected a full lowercase SHA-256.");
        Assert.AreEqual("fresh", await FreshnessOfAsync(harness, "alpha"));
    }

    [TestMethod]
    public async Task SkillFreshness_IsStale_WhenOnlyAToolDescriptionChanged()
    {
        await using var harness = await WrapperHarness.CreateWithPromptsAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")], [Prompt(Summarize, "summarize")]));
        await harness.Registry.UpdateSkillFlagAsync("alpha", true, TestTimeout);
        await SkillSnapshot.CaptureAsync(harness.Registry, harness.Index, "alpha", TestTimeout);
        Assert.AreEqual("fresh", await FreshnessOfAsync(harness, "alpha"));

        // Same name, same input schema, different description: the drift a model would notice.
        var downstream = harness.DownstreamTools["alpha"];
        downstream.Remove(downstream["echo"]);
        downstream.Add(Tool(EchoRedescribed, "echo"));
        harness.Index.InvalidateCache("alpha");

        Assert.AreEqual("stale", await FreshnessOfAsync(harness, "alpha"));
        var details = await harness.Index.GetDetailsAsync("alpha", TestTimeout);
        Assert.AreEqual("stale", details.SkillFreshness, "get_service_details must agree with list_services.");
    }

    [TestMethod]
    public async Task SkillFreshness_IsStale_WhenTheRecordedVersionDiffersFromTheCurrentOne()
    {
        await using var harness = await WrapperHarness.CreateWithPromptsAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")], [Prompt(Summarize, "summarize")]));
        await harness.Registry.UpdateSkillFlagAsync("alpha", true, TestTimeout);
        await SkillSnapshot.CaptureAsync(harness.Registry, harness.Index, "alpha", TestTimeout);
        var fingerprint = harness.Registry.Get("alpha").SkillRecordedFingerprint!;

        // Same surface, but the skill was written against 0.9.0 and the downstream now reports 1.0.0.
        await harness.Registry.UpdateSkillSnapshotAsync("alpha", "0.9.0", fingerprint, DateTimeOffset.UtcNow, TestTimeout);

        Assert.AreEqual("stale", await FreshnessOfAsync(harness, "alpha"));
    }

    [TestMethod]
    public async Task SkillFreshness_IgnoresVersion_WhenNoneWasRecorded()
    {
        await using var harness = await WrapperHarness.CreateWithPromptsAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")], [Prompt(Summarize, "summarize")]));
        await harness.Registry.UpdateSkillFlagAsync("alpha", true, TestTimeout);
        await SkillSnapshot.CaptureAsync(harness.Registry, harness.Index, "alpha", TestTimeout);
        var fingerprint = harness.Registry.Get("alpha").SkillRecordedFingerprint!;

        // A downstream that reported no version when the skill was authored is not penalised for
        // reporting one now.
        await harness.Registry.UpdateSkillSnapshotAsync("alpha", null, fingerprint, DateTimeOffset.UtcNow, TestTimeout);

        Assert.AreEqual("fresh", await FreshnessOfAsync(harness, "alpha"));
    }

    [TestMethod]
    public async Task SkillFreshness_IsUnknown_WhenThePromptFetchFailsAtReadTime()
    {
        await using var harness = await WrapperHarness.CreateWithPromptsAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")], [Prompt(Summarize, "summarize")]));
        var failPrompts = false;
        FailPromptsListWhen(harness, () => failPrompts);
        await harness.Registry.UpdateSkillFlagAsync("alpha", true, TestTimeout);
        Assert.IsTrue(await SkillSnapshot.CaptureAsync(harness.Registry, harness.Index, "alpha", TestTimeout));
        Assert.AreEqual("fresh", await FreshnessOfAsync(harness, "alpha"));

        failPrompts = true;
        harness.Index.InvalidateCache("alpha");

        // Tools still list fine; a failed prompt fetch is not "zero prompts", so this is not drift.
        Assert.AreEqual("unknown", await FreshnessOfAsync(harness, "alpha"));
        var details = await harness.Index.GetDetailsAsync("alpha", TestTimeout);
        Assert.AreEqual("unknown", details.SkillFreshness);
        Assert.AreEqual(0, details.Prompts.Count, "The details DTO still carries an (empty) prompt list.");
    }

    [TestMethod]
    public async Task CaptureAsync_WhenThePromptFetchFails_RecordsNoBaselineAndClearsTheOldOne()
    {
        await using var harness = await WrapperHarness.CreateWithPromptsAsync(_dataDir, WrapperToolMode.Eager,
            ("alpha", [Tool(Echo, "echo")], [Prompt(Summarize, "summarize")]));
        FailPromptsListWhen(harness, () => true);
        await harness.Registry.UpdateSkillFlagAsync("alpha", true, TestTimeout);
        // A baseline from an earlier authoring pass that must not survive attached to the new document.
        await harness.Registry.UpdateSkillSnapshotAsync("alpha", "0.9.0", new string('a', 64), DateTimeOffset.UtcNow, TestTimeout);

        var recorded = await SkillSnapshot.CaptureAsync(harness.Registry, harness.Index, "alpha", TestTimeout);

        Assert.IsFalse(recorded);
        var server = harness.Registry.Get("alpha");
        Assert.IsNull(server.SkillRecordedFingerprint);
        Assert.IsNull(server.SkillRecordedVersion);
        Assert.IsNull(server.SkillRecordedAt);
        Assert.AreEqual("unknown", await FreshnessOfAsync(harness, "alpha"));
    }
}
