using SysDescription = System.ComponentModel.DescriptionAttribute;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Services;
using McpAggregator.Core.Tests.Helpers;
using McpAggregator.Core.Tools;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// End to end through a live in-memory downstream (issue #56): saving a skill records the catalog,
/// a later change to the downstream shows up as <c>skillDrift</c> in <c>get_service_details</c> and as
/// a notice in front of the document from <c>get_service_skill</c>, and saving the document back
/// (notice included) stores it without the notice and reads fresh again.
/// </summary>
[TestClass]
public class SkillFreshnessNoticeTests
{
    private const string Server = "adjutant";
    private const string Document = "# Adjutant Skill Guide\n\nCall `create_event` with a subject.\n";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-skillnotice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [SysDescription("Create a calendar event.")]
    private static string CreateEvent([SysDescription("Subject")] string subject) => subject;

    [SysDescription("Create a calendar event.")]
    private static string CreateEventV2([SysDescription("Subject")] string subject, [SysDescription("All day")] bool isAllDay = false)
        => subject + isAllDay;

    [SysDescription("List accounts.")]
    private static string ListAccounts() => "[]";

    private static McpServerTool Tool(Delegate method, string name)
        => McpServerTool.Create(method, new McpServerToolCreateOptions { Name = name });

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private SkillStore Skills() => new(
        TestHelpers.OptionsOf(TestHelpers.DefaultAggregatorOptions(_dataDir)), TestHelpers.NullLoggerOf<SkillStore>());

    private async Task<WrapperHarness> CreateWithSkillAsync()
    {
        var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Lazy,
            (Server, [Tool(CreateEvent, "create_event"), Tool(ListAccounts, "list_accounts")]));
        var result = await AdminTools.UpdateSkill(harness.Registry, Skills(), harness.Index, Server, Document, TestTimeout);
        Assert.AreEqual($"Skill document updated for '{Server}'.", result, "The baseline must be recorded.");
        return harness;
    }

    private static Task<string> GetSkillAsync(WrapperHarness harness, SkillStore skills)
        => ConsumerTools.GetServiceSkill(skills, harness.Registry, harness.Index,
            TestHelpers.OptionsOf(new SkillMaintenanceOptions()), Server, TestTimeout);

    /// <summary>Upgrades the downstream in place: <c>create_event</c> gains <c>isAllDay</c>, <c>list_accounts</c> goes away.</summary>
    private static void UpgradeDownstream(WrapperHarness harness)
    {
        var tools = harness.DownstreamTools[Server];
        tools.Remove(tools["create_event"]);
        tools.Remove(tools["list_accounts"]);
        tools.Add(Tool(CreateEventV2, "create_event"));
        harness.Index.InvalidateCache(Server);
    }

    [TestMethod]
    public async Task UpdateSkill_RecordsTheBaselineCatalog()
    {
        await using var harness = await CreateWithSkillAsync();

        var baseline = await Skills().GetBaselineAsync(Server, TestTimeout);

        Assert.IsNotNull(baseline);
        CollectionAssert.AreEqual(new[] { "create_event", "list_accounts" }, baseline.Tools.Select(t => t.Name).ToList());
        Assert.AreEqual("1.0.0", baseline.RemoteVersion);
        Assert.IsNotNull(baseline.Tools[0].InputSchema);
    }

    [TestMethod]
    public async Task FreshSkill_IsServedWithoutANotice()
    {
        await using var harness = await CreateWithSkillAsync();

        Assert.AreEqual(Document, await GetSkillAsync(harness, Skills()));
        var details = await harness.Index.GetDetailsAsync(Server, TestTimeout);
        Assert.AreEqual("fresh", details.SkillFreshness);
        Assert.IsNull(details.SkillDrift);
    }

    [TestMethod]
    public async Task ChangedDownstream_ReportsDriftInDetails()
    {
        await using var harness = await CreateWithSkillAsync();
        UpgradeDownstream(harness);

        var details = await harness.Index.GetDetailsAsync(Server, TestTimeout);

        Assert.AreEqual("stale", details.SkillFreshness);
        Assert.IsNotNull(details.SkillDrift);
        CollectionAssert.AreEqual(new[] { "list_accounts" }, details.SkillDrift.ToolsRemoved);
        var change = details.SkillDrift.ToolsChanged.Single();
        Assert.AreEqual("create_event", change.Name);
        CollectionAssert.AreEqual(new[] { "isAllDay" }, change.ParametersAdded);
    }

    [TestMethod]
    public async Task ChangedDownstream_GetServiceSkillLeadsWithTheNotice()
    {
        await using var harness = await CreateWithSkillAsync();
        UpgradeDownstream(harness);

        var served = await GetSkillAsync(harness, Skills());

        StringAssert.StartsWith(served, SkillNotice.StartMarker);
        StringAssert.Contains(served, "tool `list_accounts` was removed");
        StringAssert.Contains(served, "tool `create_event`: new parameter `isAllDay`");
        Assert.IsTrue(served.EndsWith(Document, StringComparison.Ordinal), "The document itself follows unchanged.");
    }

    [TestMethod]
    public async Task SavingTheServedDocumentBack_StripsTheNoticeAndReadsFresh()
    {
        await using var harness = await CreateWithSkillAsync();
        UpgradeDownstream(harness);
        var skills = Skills();
        var served = await GetSkillAsync(harness, skills);

        await AdminTools.UpdateSkill(harness.Registry, skills, harness.Index, Server, served, TestTimeout);

        Assert.AreEqual(Document, await skills.GetAsync(Server, TestTimeout), "The notice must never be stored.");
        Assert.AreEqual(Document, await GetSkillAsync(harness, skills), "Re-saving records a new baseline.");
    }

    [TestMethod]
    public async Task ChangedInstructions_MakeTheSkillStale()
    {
        await using var harness = await CreateWithSkillAsync();
        harness.ConfigureDownstream = (_, options) => options.ServerInstructions = "Call list_accounts before anything else.";
        await harness.Connections.DisconnectAsync(Server); // the next call reconnects and records the new instructions
        harness.Index.InvalidateCache(Server);

        var details = await harness.Index.GetDetailsAsync(Server, TestTimeout);

        Assert.AreEqual("stale", details.SkillFreshness);
        Assert.IsTrue(details.SkillDrift!.InstructionsChanged);
        StringAssert.Contains(await GetSkillAsync(harness, Skills()), "the server's instructions changed");
    }

    [TestMethod]
    public async Task VersionOnlyChange_IsStaleWithAnUnchangedCatalog()
    {
        await using var harness = await CreateWithSkillAsync();
        var server = harness.Registry.Get(Server);
        await harness.Registry.UpdateRemoteMetadataAsync(Server, server.RemoteName, server.RemoteTitle, "1.1.0",
            server.RemoteInstructions, TestTimeout);

        var details = await harness.Index.GetDetailsAsync(Server, TestTimeout);

        Assert.AreEqual("stale", details.SkillFreshness);
        Assert.IsTrue(details.SkillDrift!.CatalogUnchanged);
        Assert.AreEqual("1.0.0", details.SkillDrift.RecordedVersion);
        Assert.AreEqual("1.1.0", details.SkillDrift.CurrentVersion);
    }

    [TestMethod]
    public async Task SkillRecordedBeforeBaselines_StillGetsAVersionNotice()
    {
        await using var harness = await CreateWithSkillAsync();
        var skills = Skills();
        Assert.IsTrue(skills.DeleteBaseline(Server), "Simulate a skill saved before #56.");
        var server = harness.Registry.Get(Server);
        await harness.Registry.UpdateRemoteMetadataAsync(Server, server.RemoteName, server.RemoteTitle, "2.0.0",
            server.RemoteInstructions, TestTimeout);

        var served = await GetSkillAsync(harness, skills);

        StringAssert.Contains(served, "the server now reports 2.0.0");
        StringAssert.Contains(served, "was not recorded");
    }

    [TestMethod]
    public async Task UnreachableServer_ServesTheDocumentWithoutANotice()
    {
        await using var harness = await CreateWithSkillAsync();
        UpgradeDownstream(harness);
        await harness.Connections.DisconnectAsync(Server);
        harness.Connections.TransportFactoryOverride = _ => throw new IOException("downstream is down");

        Assert.AreEqual(Document, await GetSkillAsync(harness, Skills()));
    }

    [TestMethod]
    public async Task DeletingTheSkill_RemovesTheBaseline()
    {
        await using var harness = await CreateWithSkillAsync();
        var skills = Skills();

        Assert.IsTrue(skills.Delete(Server));

        Assert.IsNull(await skills.GetBaselineAsync(Server, TestTimeout));
    }
}
