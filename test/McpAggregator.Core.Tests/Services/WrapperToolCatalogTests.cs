using SysDescription = System.ComponentModel.DescriptionAttribute;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tests.Helpers;
using McpAggregator.Core.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tests.Services;

/// <summary>
/// The catalog keeps <c>McpServerOptions.ToolCollection</c> in step with the registry and index
/// (issue #39). These tests drive it with real in-process downstreams and inspect the collection
/// the SDK's server would serve from.
/// </summary>
[TestClass]
public class WrapperToolCatalogTests
{
    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- downstream doubles

    [SysDescription("Lists files in a folder.")]
    private static string ListFiles([SysDescription("Folder to list")] string? folder = null) => "report.docx";

    [SysDescription("Search the documentation for a query.")]
    private static string SearchDocs([SysDescription("Search query")] string query) => "results";

    [SysDescription("Fetch a documentation page by url.")]
    private static string FetchPage([SysDescription("Page url")] string url) => "page";

    [SysDescription("Echoes a message back.")]
    private static string Echo([SysDescription("Message")] string message) => message;

    [SysDescription("Echoes a message back, v2 with a required mode.")]
    private static string EchoV2([SysDescription("Message")] string message, [SysDescription("Mode")] string mode) => message + mode;

    private static McpServerTool Tool(Delegate method, string name)
        => McpServerTool.Create(method, new McpServerToolCreateOptions { Name = name });

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private Task<WrapperHarness> TwoOneDriveServersAsync(WrapperToolMode mode)
        => WrapperHarness.CreateAsync(_dataDir, mode,
            ("onedrive-marimer", [Tool(ListFiles, "list_files")]),
            ("onedrive-personal", [Tool(ListFiles, "list_files")]));

    // ---------------------------------------------------------------- eager

    [TestMethod]
    public async Task Eager_Sync_PopulatesCollectionWithWrappersFromEveryServer()
    {
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Eager);

        await harness.Catalog.SyncAsync(TestTimeout);

        // Collision resolved by prefixing: both list_files tools survive as distinct wrappers.
        CollectionAssert.AreEqual(
            new[] { "onedrive-marimer__list_files", "onedrive-personal__list_files" },
            harness.WrapperNames);
        Assert.IsTrue(harness.ToolCollection.Contains(harness.Sentinel), "Non-wrapper tools must be left alone.");
    }

    [TestMethod]
    public async Task Eager_Sync_RaisesExactlyOneChangedEvent()
    {
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Eager);
        var changed = 0;
        harness.ToolCollection.Changed += (_, _) => Interlocked.Increment(ref changed);

        await harness.Catalog.SyncAsync(TestTimeout);
        Assert.AreEqual(1, changed, "Two adds must be batched into one Changed (one list_changed).");

        await harness.Catalog.SyncAsync(TestTimeout);
        Assert.AreEqual(1, changed, "A sync that changes nothing must not raise Changed.");
    }

    [TestMethod]
    public async Task Eager_Sync_ReusesWrapperInstancesWhenSchemaUnchanged()
    {
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Eager);

        await harness.Catalog.SyncAsync(TestTimeout);
        var first = harness.Catalog.ActiveWrappers.Single(w => w.ServerName == "onedrive-marimer");

        harness.Index.InvalidateCache("onedrive-marimer");
        await harness.Catalog.PendingSync;
        await harness.Catalog.SyncAsync(TestTimeout);
        var second = harness.Catalog.ActiveWrappers.Single(w => w.ServerName == "onedrive-marimer");

        Assert.AreSame(first, second, "Same name and schema must not churn the collection.");
    }

    // ---------------------------------------------------------------- lazy

    [TestMethod]
    public async Task Lazy_Sync_LeavesTheSharedCollectionEmpty()
    {
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Lazy);

        await harness.Catalog.SyncAsync(TestTimeout);

        Assert.AreEqual(0, harness.WrapperNames.Count);
        Assert.IsFalse(harness.Connections.IsConnected("onedrive-marimer"),
            "Lazy mode must not connect to a downstream nobody has asked about.");
    }

    [TestMethod]
    public async Task Lazy_FindAsync_ActivatesMatchesForTheCallingSessionOnly()
    {
        // The point of Lazy: one client's discovery must not enlarge anyone else's tool list.
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Lazy);
        await using var sessionA = harness.NewSession();
        await using var sessionB = harness.NewSession();

        var result = await harness.Catalog.FindAsync("list files", 10, sessionA.Server, TestTimeout);

        Assert.AreEqual(2, result.Matches.Count);
        CollectionAssert.AreEqual(
            new[] { "onedrive-marimer__list_files", "onedrive-personal__list_files" },
            harness.Catalog.ActivatedFor(sessionA.Server).Select(w => w.ProtocolTool.Name).ToList());
        Assert.AreEqual(0, harness.Catalog.ActivatedFor(sessionB.Server).Count, "Another session must see nothing.");
        Assert.AreEqual(0, harness.WrapperNames.Count, "The shared collection must stay untouched in Lazy mode.");
        Assert.IsTrue(result.Matches.All(m => harness.Catalog.IsActive(m.Wrapper.ProtocolTool.Name, sessionA.Server)));
        Assert.IsFalse(result.Matches.Any(m => harness.Catalog.IsActive(m.Wrapper.ProtocolTool.Name, sessionB.Server)));
    }

    [TestMethod]
    public async Task Lazy_FindAsync_WithoutASession_ActivatesNothing()
    {
        // Stateless HTTP has no session to remember for; find_tools still answers, nothing sticks.
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Lazy);

        var result = await harness.Catalog.FindAsync("list files", 10, session: null, TestTimeout);

        Assert.AreEqual(2, result.Matches.Count);
        Assert.AreEqual(0, harness.WrapperNames.Count);
    }

    [TestMethod]
    public async Task Lazy_ActivateServerAsync_ExposesEveryToolOfThatServerOnly_ToThatSession()
    {
        await using var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Lazy,
            ("docs", [Tool(SearchDocs, "search_docs"), Tool(FetchPage, "fetch_page")]),
            ("files", [Tool(ListFiles, "list_files")]));
        await using var session = harness.NewSession();

        await harness.Catalog.ActivateServerAsync(session.Server, "docs", TestTimeout);

        CollectionAssert.AreEqual(new[] { "docs__fetch_page", "docs__search_docs" },
            harness.Catalog.ActivatedFor(session.Server).Select(w => w.ProtocolTool.Name).ToList());
        Assert.AreEqual(0, harness.WrapperNames.Count);
    }

    [TestMethod]
    public async Task Eager_ActivateAsync_IsANoOp()
    {
        // Everything is already listed process-wide; per-session state has no meaning.
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Eager);
        await using var session = harness.NewSession();
        await harness.Catalog.SyncAsync(TestTimeout);

        await harness.Catalog.ActivateServerAsync(session.Server, "onedrive-marimer", TestTimeout);

        Assert.AreEqual(0, harness.Catalog.ActivatedFor(session.Server).Count);
        Assert.IsTrue(harness.Catalog.IsActive("onedrive-marimer__list_files", session.Server), "Listed via the shared collection.");
    }

    [TestMethod]
    public async Task ResolveAsync_FindsAWrapperByNameWhetherOrNotItIsListed()
    {
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Lazy);

        var wrapper = await harness.Catalog.ResolveAsync("onedrive-personal__list_files", TestTimeout);

        Assert.IsNotNull(wrapper);
        Assert.AreEqual("onedrive-personal", wrapper.ServerName);
        Assert.AreEqual("list_files", wrapper.ToolName);
        Assert.AreEqual(0, harness.WrapperNames.Count, "Resolving must not list anything.");
    }

    [TestMethod]
    public async Task ResolveAsync_ReturnsNullForUnknownDisabledOrMissing()
    {
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Lazy);
        await harness.Registry.SetEnabledAsync("onedrive-personal", false);

        Assert.IsNull(await harness.Catalog.ResolveAsync("not-a-wrapper", TestTimeout));
        Assert.IsNull(await harness.Catalog.ResolveAsync("ghost__list_files", TestTimeout));
        Assert.IsNull(await harness.Catalog.ResolveAsync("onedrive-personal__list_files", TestTimeout), "Disabled server.");
        Assert.IsNull(await harness.Catalog.ResolveAsync("onedrive-marimer__send_email", TestTimeout), "No such tool.");
    }

    // ---------------------------------------------------------------- find_tools ranking

    [TestMethod]
    public async Task FindAsync_ExactToolNameRanksFirst()
    {
        await using var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Eager,
            ("docs", [Tool(SearchDocs, "search_docs"), Tool(FetchPage, "fetch_page")]));

        var result = await harness.Catalog.FindAsync("search_docs", 10, session: null, TestTimeout);

        Assert.AreEqual("docs__search_docs", result.Matches[0].Wrapper.ProtocolTool.Name);
        Assert.IsTrue(result.Matches[0].Score >= 1000);
    }

    [TestMethod]
    public async Task FindAsync_ExactWrapperNameRanksFirst()
    {
        await using var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Eager,
            ("docs", [Tool(SearchDocs, "search_docs"), Tool(FetchPage, "fetch_page")]));

        var result = await harness.Catalog.FindAsync("docs__fetch_page", 10, session: null, TestTimeout);

        Assert.AreEqual("docs__fetch_page", result.Matches[0].Wrapper.ProtocolTool.Name);
    }

    [TestMethod]
    public async Task FindAsync_MatchesOnDescription()
    {
        await using var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Eager,
            ("docs", [Tool(SearchDocs, "search_docs"), Tool(FetchPage, "fetch_page")]));

        var result = await harness.Catalog.FindAsync("documentation page url", 10, session: null, TestTimeout);

        Assert.AreEqual("docs__fetch_page", result.Matches[0].Wrapper.ProtocolTool.Name);
        Assert.IsNotNull(result.Matches[0].Detail.InputSchema, "Matches must carry the schema so the caller can call the tool directly.");
    }

    [TestMethod]
    public async Task FindAsync_HonorsLimitAndReportsNoMatch()
    {
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Eager);

        var limited = await harness.Catalog.FindAsync("list files", 1, session: null, TestTimeout);
        Assert.AreEqual(1, limited.Matches.Count);

        var none = await harness.Catalog.FindAsync("send email", 10, session: null, TestTimeout);
        Assert.AreEqual(0, none.Matches.Count);
    }

    [TestMethod]
    public async Task FindAsync_SkipsDisabledServers()
    {
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Eager);
        await harness.Registry.SetEnabledAsync("onedrive-personal", false);

        var result = await harness.Catalog.FindAsync("list files", 10, session: null, TestTimeout);

        Assert.AreEqual(1, result.Matches.Count);
        Assert.AreEqual("onedrive-marimer", result.Matches[0].Server.Name);
    }

    // ---------------------------------------------------------------- registry events

    [TestMethod]
    public async Task DisableServer_RemovesItsWrappers()
    {
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Eager);
        await harness.Catalog.SyncAsync(TestTimeout);

        await harness.Registry.SetEnabledAsync("onedrive-personal", false);
        await harness.Catalog.PendingSync;

        CollectionAssert.AreEqual(new[] { "onedrive-marimer__list_files" }, harness.WrapperNames);
    }

    [TestMethod]
    public async Task EnableServer_RestoresItsWrappers()
    {
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Eager);
        await harness.Registry.SetEnabledAsync("onedrive-personal", false);
        await harness.Catalog.SyncAsync(TestTimeout);
        Assert.AreEqual(1, harness.WrapperNames.Count);

        await harness.Registry.SetEnabledAsync("onedrive-personal", true);
        await harness.Catalog.PendingSync;

        Assert.AreEqual(2, harness.WrapperNames.Count);
    }

    [TestMethod]
    public async Task UnregisterServer_RemovesItsWrappers()
    {
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Eager);
        await harness.Catalog.SyncAsync(TestTimeout);

        await harness.Registry.UnregisterAsync("onedrive-marimer");
        await harness.Catalog.PendingSync;

        CollectionAssert.AreEqual(new[] { "onedrive-personal__list_files" }, harness.WrapperNames);
        Assert.IsTrue(harness.ToolCollection.Contains(harness.Sentinel));
    }

    [TestMethod]
    public async Task Lazy_UnregisterServer_DropsItsSessionActivations()
    {
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Lazy);
        await using var session = harness.NewSession();
        await harness.Catalog.FindAsync("list files", 10, session.Server, TestTimeout);
        Assert.AreEqual(2, harness.Catalog.ActivatedFor(session.Server).Count);

        await harness.Registry.UnregisterAsync("onedrive-marimer");
        await harness.Catalog.PendingSync;

        CollectionAssert.AreEqual(new[] { "onedrive-personal__list_files" },
            harness.Catalog.ActivatedFor(session.Server).Select(w => w.ProtocolTool.Name).ToList());
    }

    [TestMethod]
    public async Task Lazy_DisableServer_DropsItsSessionActivations()
    {
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Lazy);
        await using var session = harness.NewSession();
        await harness.Catalog.FindAsync("list files", 10, session.Server, TestTimeout);

        await harness.Registry.SetEnabledAsync("onedrive-personal", false);
        await harness.Catalog.PendingSync;

        CollectionAssert.AreEqual(new[] { "onedrive-marimer__list_files" },
            harness.Catalog.ActivatedFor(session.Server).Select(w => w.ProtocolTool.Name).ToList());
    }

    // ---------------------------------------------------------------- index events

    [TestMethod]
    public async Task InvalidateCache_RebuildsWrapperWithNewSchema()
    {
        // refresh_service after a downstream upgrade: the wrapper must pick up the new schema.
        await using var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Eager,
            ("svc", [Tool(Echo, "echo")]));
        await harness.Catalog.SyncAsync(TestTimeout);
        var before = harness.Catalog.ActiveWrappers.Single();
        Assert.IsFalse(before.ProtocolTool.InputSchema.GetRawText().Contains("\"mode\""));

        // Simulate the downstream being upgraded in place.
        var downstream = harness.DownstreamTools["svc"];
        downstream.Remove(downstream["echo"]);
        downstream.Add(Tool(EchoV2, "echo"));

        harness.Index.InvalidateCache("svc");
        await harness.Catalog.PendingSync;

        var after = harness.Catalog.ActiveWrappers.Single();
        Assert.AreNotSame(before, after);
        Assert.AreEqual("svc__echo", after.ProtocolTool.Name);
        StringAssert.Contains(after.ProtocolTool.InputSchema.GetRawText(), "\"mode\"");
        Assert.AreNotEqual(before.SchemaFingerprint, after.SchemaFingerprint);
    }

    [TestMethod]
    public async Task InvalidateCache_RemovesWrappersForToolsThatDisappeared()
    {
        await using var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Eager,
            ("docs", [Tool(SearchDocs, "search_docs"), Tool(FetchPage, "fetch_page")]));
        await harness.Catalog.SyncAsync(TestTimeout);
        Assert.AreEqual(2, harness.WrapperNames.Count);

        var downstream = harness.DownstreamTools["docs"];
        downstream.Remove(downstream["fetch_page"]);

        harness.Index.InvalidateCache("docs");
        await harness.Catalog.PendingSync;

        CollectionAssert.AreEqual(new[] { "docs__search_docs" }, harness.WrapperNames);
    }

    [TestMethod]
    public async Task UnavailableServer_IsSkippedAndOthersStillSync()
    {
        await using var harness = await TwoOneDriveServersAsync(WrapperToolMode.Eager);

        // Point one server at a downstream whose tool set does not exist, so connecting fails.
        harness.DownstreamTools.Remove("onedrive-personal");

        await harness.Catalog.SyncAsync(TestTimeout);

        CollectionAssert.AreEqual(new[] { "onedrive-marimer__list_files" }, harness.WrapperNames);
    }

    // ---------------------------------------------------------------- prompts (issue #40)

    [SysDescription("Summarizes text.")]
    private static string Summarize([SysDescription("Text")] string text) => text;

    [SysDescription("Summarizes text, v2 with a style.")]
    private static string SummarizeV2([SysDescription("Text")] string text, [SysDescription("Style")] string style) => text + style;

    [SysDescription("Drafts a release plan.")]
    private static string PlanRelease([SysDescription("Version")] string version) => version;

    private static McpServerPrompt Prompt(Delegate method, string name)
        => McpServerPrompt.Create(method, new McpServerPromptCreateOptions { Name = name });

    private Task<WrapperHarness> PromptServersAsync(WrapperToolMode mode)
        => WrapperHarness.CreateWithPromptsAsync(_dataDir, mode,
            ("docs", [Tool(SearchDocs, "search_docs")], [Prompt(Summarize, "summarize"), Prompt(PlanRelease, "plan_release")]),
            ("toolsonly", [Tool(Echo, "echo")], null));

    [TestMethod]
    public async Task Eager_Sync_PopulatesPromptCollection_AndLeavesTheSentinelAlone()
    {
        await using var harness = await PromptServersAsync(WrapperToolMode.Eager);

        await harness.Catalog.SyncAsync(TestTimeout);

        CollectionAssert.AreEqual(new[] { "docs__plan_release", "docs__summarize" }, harness.PromptWrapperNames);
        Assert.IsTrue(harness.PromptCollection.Contains(harness.SentinelPrompt), "Non-wrapper prompts must be left alone.");
        CollectionAssert.AreEqual(new[] { "docs__search_docs", "toolsonly__echo" }, harness.WrapperNames,
            "A server without prompt support still contributes its tools.");
    }

    [TestMethod]
    public async Task Eager_Sync_RaisesExactlyOneChangedEventOnThePromptCollection()
    {
        await using var harness = await PromptServersAsync(WrapperToolMode.Eager);
        var changed = 0;
        harness.PromptCollection.Changed += (_, _) => Interlocked.Increment(ref changed);

        await harness.Catalog.SyncAsync(TestTimeout);
        Assert.AreEqual(1, changed, "Two adds must be batched into one Changed (one prompts/list_changed).");

        await harness.Catalog.SyncAsync(TestTimeout);
        Assert.AreEqual(1, changed, "A sync that changes nothing must not raise Changed.");
    }

    [TestMethod]
    public async Task Eager_DisablingAServer_RemovesItsPrompts()
    {
        await using var harness = await PromptServersAsync(WrapperToolMode.Eager);
        await harness.Catalog.SyncAsync(TestTimeout);

        await harness.Registry.SetEnabledAsync("docs", false);
        await harness.Catalog.PendingSync;

        Assert.AreEqual(0, harness.PromptWrapperNames.Count);
        Assert.IsTrue(harness.PromptCollection.Contains(harness.SentinelPrompt));
    }

    [TestMethod]
    public async Task Lazy_Sync_LeavesThePromptCollectionUntouched()
    {
        await using var harness = await PromptServersAsync(WrapperToolMode.Lazy);

        await harness.Catalog.SyncAsync(TestTimeout);

        Assert.AreEqual(0, harness.PromptWrapperNames.Count);
        Assert.AreEqual(1, harness.PromptCollection.Count);
    }

    [TestMethod]
    public async Task InvalidateCache_RebuildsThePromptWrapperWhenItsArgumentsChanged()
    {
        await using var harness = await PromptServersAsync(WrapperToolMode.Eager);
        await harness.Catalog.SyncAsync(TestTimeout);
        var before = harness.Catalog.ActivePromptWrappers.Single(w => w.PromptName == "summarize");

        var downstream = harness.DownstreamPrompts["docs"];
        downstream.Remove(downstream["summarize"]);
        downstream.Add(Prompt(SummarizeV2, "summarize"));

        harness.Index.InvalidateCache("docs");
        await harness.Catalog.PendingSync;

        var after = harness.Catalog.ActivePromptWrappers.Single(w => w.PromptName == "summarize");
        Assert.AreNotSame(before, after);
        Assert.AreNotEqual(before.ArgumentsFingerprint, after.ArgumentsFingerprint);
        Assert.AreEqual(2, after.ProtocolPrompt.Arguments!.Count);
        Assert.AreSame(
            harness.Catalog.ActivePromptWrappers.Single(w => w.PromptName == "plan_release"),
            harness.Catalog.ActivePromptWrappers.Single(w => w.PromptName == "plan_release"),
            "Unchanged prompts keep their instance.");
    }

    [TestMethod]
    public async Task FindAsync_ReturnsAndActivatesMatchingPrompts()
    {
        await using var harness = await PromptServersAsync(WrapperToolMode.Lazy);
        await using var session = harness.NewSession();

        var result = await harness.Catalog.FindAsync("release plan", 10, session.Server, TestTimeout);

        Assert.AreEqual(0, result.Matches.Count, "No tool talks about releases.");
        Assert.AreEqual("docs__plan_release", result.Prompts.Single().Wrapper.ProtocolPrompt.Name);
        CollectionAssert.AreEqual(new[] { "docs__plan_release" },
            harness.Catalog.ActivatedPromptsFor(session.Server).Select(p => p.ProtocolPrompt.Name).ToList());
        Assert.IsTrue(harness.Catalog.IsPromptActive("docs__plan_release", session.Server));
        Assert.AreEqual(0, harness.Catalog.ActivatedFor(session.Server).Count, "No tool was activated.");
    }

    [TestMethod]
    public async Task ResolvePromptAsync_FindsAnUnlistedPromptByName_AndRejectsUnknownOnes()
    {
        await using var harness = await PromptServersAsync(WrapperToolMode.Lazy);

        var found = await harness.Catalog.ResolvePromptAsync("docs__summarize", TestTimeout);
        Assert.IsNotNull(found);
        Assert.AreEqual("summarize", found.PromptName);

        Assert.IsNull(await harness.Catalog.ResolvePromptAsync("docs__nope", TestTimeout));
        Assert.IsNull(await harness.Catalog.ResolvePromptAsync("nosuchserver__summarize", TestTimeout));
        Assert.IsNull(await harness.Catalog.ResolvePromptAsync("noseparator", TestTimeout));
    }
}
