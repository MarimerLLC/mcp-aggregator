using System.Text.Json;
using SysDescription = System.ComponentModel.DescriptionAttribute;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using McpAggregator.Core.Tests.Helpers;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// <c>refresh_skill</c>, <c>list_skill_history</c> and <c>restore_skill</c> end to end (issue #57):
/// a live in-memory downstream is upgraded under a skill, a model double revises the document, and
/// the draft is proposed, validated, published, kept in history and rolled back.
/// </summary>
[TestClass]
public class SkillRefreshTests
{
    private const string Server = "adjutant";
    private const string Document = "# Adjutant Skill Guide\n\nCall `list_accounts` first, then `create_event` with a subject.\n";
    private const string Revised = "# Adjutant Skill Guide\n\nCall `create_event` with a subject; set `isAllDay` for all-day events.\n";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-refresh-" + Guid.NewGuid().ToString("N"));
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

    private sealed record Rig(WrapperHarness Harness, SkillStore Skills, SkillMaintainer Maintainer, FakeChatClient Model)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Harness.DisposeAsync();
    }

    private async Task<Rig> CreateAsync(bool modelConfigured = true)
    {
        var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Lazy,
            (Server, [Tool(CreateEvent, "create_event"), Tool(ListAccounts, "list_accounts")]));
        var options = TestHelpers.OptionsOf(TestHelpers.DefaultAggregatorOptions(_dataDir));
        var skills = new SkillStore(options, TestHelpers.NullLoggerOf<SkillStore>());
        await AdminTools.UpdateSkill(harness.Registry, skills, harness.Index, Server, Document, TestTimeout);

        var model = new FakeChatClient { Respond = _ => Revised };
        var services = new ServiceCollection();
        if (modelConfigured)
            services.AddSingleton<IChatClient>(model);
        var maintainer = new SkillMaintainer(harness.Registry, harness.Index, skills,
            TestHelpers.OptionsOf(new SkillMaintenanceOptions()),
            TestHelpers.OptionsOf(new AiOptions { Enabled = modelConfigured, Model = "test-model" }),
            services.BuildServiceProvider(), TestHelpers.NullLoggerOf<SkillMaintainer>());
        return new Rig(harness, skills, maintainer, model);
    }

    /// <summary><c>create_event</c> gains <c>isAllDay</c>; <c>list_accounts</c> goes away.</summary>
    private static void UpgradeDownstream(WrapperHarness harness)
    {
        var tools = harness.DownstreamTools[Server];
        tools.Remove(tools["create_event"]);
        tools.Remove(tools["list_accounts"]);
        tools.Add(Tool(CreateEventV2, "create_event"));
        harness.Index.InvalidateCache(Server);
    }

    private static async Task SetVersionAsync(WrapperHarness harness, string version)
    {
        var server = harness.Registry.Get(Server);
        await harness.Registry.UpdateRemoteMetadataAsync(Server, server.RemoteName, server.RemoteTitle, version,
            server.RemoteInstructions, TestTimeout);
    }

    private static Task<SkillRefreshResult> RefreshAsync(Rig rig, bool apply = false, bool regenerate = false)
        => rig.Maintainer.RefreshAsync(Server, apply, regenerate, TestTimeout);

    // ---------------------------------------------------------------- refresh_skill

    [TestMethod]
    public async Task FreshSkill_IsLeftAlone()
    {
        await using var rig = await CreateAsync();

        var result = await RefreshAsync(rig, apply: true);

        Assert.AreEqual("fresh", result.Status);
        Assert.AreEqual(0, rig.Model.Requests.Count);
    }

    [TestMethod]
    public async Task StaleSkill_ProposesAValidatedDraftWithoutSaving()
    {
        await using var rig = await CreateAsync();
        UpgradeDownstream(rig.Harness);

        var result = await RefreshAsync(rig);

        Assert.AreEqual("proposed", result.Status, result.Message);
        Assert.IsTrue(result.ValidationPassed, string.Join(" | ", result.Problems));
        StringAssert.StartsWith(result.Draft, Revised.TrimEnd());
        StringAssert.Contains(result.Draft, SkillMaintainer.ProvenanceMarker);
        StringAssert.Contains(result.Diff, "+Call `create_event` with a subject; set `isAllDay`");
        Assert.AreEqual(Document, await rig.Skills.GetAsync(Server, TestTimeout), "Proposing must not save.");
        Assert.IsNotNull(await rig.Skills.GetDraftAsync(Server, TestTimeout));
    }

    [TestMethod]
    public async Task ModelInput_CarriesChangesCatalogAndDocument_AsData()
    {
        await using var rig = await CreateAsync();
        UpgradeDownstream(rig.Harness);

        await RefreshAsync(rig);

        var request = rig.Model.Requests.Single();
        StringAssert.Contains(request[0].Text, "not instructions to you");
        var user = request[1].Text;
        StringAssert.Contains(user, "tool `list_accounts` was removed");
        StringAssert.Contains(user, "tool `create_event`: new parameter `isAllDay`");
        StringAssert.Contains(user, "<catalog>");
        StringAssert.Contains(user, "\"isAllDay\"");
        StringAssert.Contains(user, "<current_document>\n" + Document.TrimEnd());
        Assert.AreEqual(16_000, rig.Model.LastOptions?.MaxOutputTokens);
    }

    [TestMethod]
    public async Task ApplyAfterProposal_PublishesTheStoredDraftWithoutAskingAgain()
    {
        await using var rig = await CreateAsync();
        UpgradeDownstream(rig.Harness);
        var proposed = await RefreshAsync(rig);
        rig.Model.Respond = _ => "# Something else entirely\n";

        var applied = await RefreshAsync(rig, apply: true);

        Assert.AreEqual("applied", applied.Status, applied.Message);
        Assert.IsTrue(applied.FromStoredDraft);
        Assert.AreEqual(1, rig.Model.Requests.Count, "The reviewed draft is published as is.");
        Assert.AreEqual(proposed.Draft, await rig.Skills.GetAsync(Server, TestTimeout));
        Assert.IsNull(await rig.Skills.GetDraftAsync(Server, TestTimeout), "A published draft is cleared.");

        var details = await rig.Harness.Index.GetDetailsAsync(Server, TestTimeout);
        Assert.AreEqual("fresh", details.SkillFreshness, "Publishing records a new baseline.");
        Assert.AreEqual(1, (await rig.Skills.ListHistoryAsync(Server, TestTimeout)).Count, "The replaced version is in history.");
    }

    [TestMethod]
    public async Task ApplyWithoutProposal_GeneratesAndPublishes()
    {
        await using var rig = await CreateAsync();
        UpgradeDownstream(rig.Harness);

        var result = await RefreshAsync(rig, apply: true);

        Assert.AreEqual("applied", result.Status, result.Message);
        Assert.IsFalse(result.FromStoredDraft);
        StringAssert.StartsWith(await rig.Skills.GetAsync(Server, TestTimeout), Revised.TrimEnd());
    }

    [TestMethod]
    public async Task StoredDraftForAnOlderDocument_IsNotPublished()
    {
        await using var rig = await CreateAsync();
        UpgradeDownstream(rig.Harness);
        await RefreshAsync(rig);
        // Someone edits the document by hand after the proposal (re-baselining it), then the server changes again.
        await AdminTools.UpdateSkill(rig.Harness.Registry, rig.Skills, rig.Harness.Index, Server, Document + "\nHand edit.\n", TestTimeout);
        await SetVersionAsync(rig.Harness, "1.1.0");
        rig.Harness.DownstreamTools[Server].Add(Tool(ListAccounts, "list_accounts"));
        rig.Harness.Index.InvalidateCache(Server);

        var result = await RefreshAsync(rig, apply: true);

        Assert.IsFalse(result.FromStoredDraft, "The stored draft revised a document that is no longer current.");
        Assert.AreEqual(2, rig.Model.Requests.Count);
    }

    [TestMethod]
    public async Task DraftMentioningARemovedTool_IsRejectedAndNotSaved()
    {
        await using var rig = await CreateAsync();
        UpgradeDownstream(rig.Harness);
        rig.Model.Respond = _ => Document; // the model changed nothing, so `list_accounts` is still there

        var proposed = await RefreshAsync(rig);
        var applied = await RefreshAsync(rig, apply: true);

        Assert.AreEqual("proposed", proposed.Status);
        Assert.IsFalse(proposed.ValidationPassed);
        StringAssert.Contains(proposed.Problems.Single(), "removed tool `list_accounts`");
        Assert.AreEqual("rejected", applied.Status);
        Assert.AreEqual(Document, await rig.Skills.GetAsync(Server, TestTimeout));
    }

    [TestMethod]
    public async Task Regenerate_IgnoresTheStoredDraft()
    {
        await using var rig = await CreateAsync();
        UpgradeDownstream(rig.Harness);
        rig.Model.Respond = _ => Document;
        await RefreshAsync(rig);
        rig.Model.Respond = _ => Revised;

        var result = await RefreshAsync(rig, apply: true, regenerate: true);

        Assert.AreEqual("applied", result.Status, result.Message);
        Assert.AreEqual(2, rig.Model.Requests.Count);
    }

    [TestMethod]
    public async Task EditDuringGeneration_IsKeptAndNothingIsPublished()
    {
        // PR #61 review: an update_skill that lands while the model works must not be overwritten.
        await using var rig = await CreateAsync();
        UpgradeDownstream(rig.Harness);
        const string handEdit = "# Adjutant Skill Guide\n\nEdited by hand while the model was busy.\n";
        rig.Model.Respond = _ =>
        {
            rig.Skills.SetAsync(Server, handEdit).GetAwaiter().GetResult();
            return Revised;
        };

        var result = await RefreshAsync(rig, apply: true);

        Assert.AreEqual("conflict", result.Status, result.Message);
        Assert.AreEqual(handEdit, await rig.Skills.GetAsync(Server, TestTimeout));
        Assert.IsNull(await rig.Skills.GetDraftAsync(Server, TestTimeout), "The obsolete draft is dropped.");
    }

    [TestMethod]
    public async Task StoredDraft_CatalogChangedWithoutVersionBump_IsNotReused()
    {
        // PR #61 review: same document, same version, different catalog.
        await using var rig = await CreateAsync();
        UpgradeDownstream(rig.Harness);
        await RefreshAsync(rig);
        rig.Harness.DownstreamTools[Server].Add(Tool(ListAccounts, "list_accounts"));
        rig.Harness.Index.InvalidateCache(Server);

        var result = await RefreshAsync(rig, apply: true);

        Assert.IsFalse(result.FromStoredDraft);
        Assert.AreEqual(2, rig.Model.Requests.Count, "A new draft is generated for the new catalog.");
    }

    [TestMethod]
    public async Task Published_IsJudgedAgainstTheCatalogItWasWrittenFrom()
    {
        // PR #61 review: if the catalog changes while the model works, the published document must not
        // be recorded as fresh against the newer catalog.
        await using var rig = await CreateAsync();
        UpgradeDownstream(rig.Harness);
        rig.Model.Respond = _ =>
        {
            rig.Harness.DownstreamTools[Server].Add(Tool(ListAccounts, "list_accounts"));
            rig.Harness.Index.InvalidateCache(Server);
            return Revised;
        };

        var result = await RefreshAsync(rig, apply: true);

        Assert.AreEqual("applied", result.Status, result.Message);
        var details = await rig.Harness.Index.GetDetailsAsync(Server, TestTimeout);
        Assert.AreEqual("stale", details.SkillFreshness);
        CollectionAssert.AreEqual(new[] { "list_accounts" }, details.SkillDrift!.ToolsAdded);
    }

    [TestMethod]
    public async Task DraftOverTheSizeLimitWithProvenance_FailsValidation()
    {
        // PR #61 review: content checks run without the provenance line, but the saved text includes it.
        await using var rig = await CreateAsync();
        UpgradeDownstream(rig.Harness);
        var body = Revised + new string('x', SkillStore.MaxSkillSize - Revised.Length - 10);
        rig.Model.Respond = _ => body;

        var proposed = await RefreshAsync(rig);
        var applied = await RefreshAsync(rig, apply: true);

        Assert.IsFalse(proposed.ValidationPassed);
        StringAssert.Contains(string.Join(" | ", proposed.Problems), "with its provenance line");
        Assert.AreEqual("rejected", applied.Status);
    }

    [TestMethod]
    public async Task VersionOnlyChange_RebaselinesWithoutTheModel()
    {
        await using var rig = await CreateAsync();
        await SetVersionAsync(rig.Harness, "1.1.0");

        var proposed = await RefreshAsync(rig);
        var applied = await RefreshAsync(rig, apply: true);

        Assert.AreEqual("version_only", proposed.Status);
        Assert.AreEqual("rebaselined", applied.Status, applied.Message);
        Assert.AreEqual(0, rig.Model.Requests.Count);
        Assert.AreEqual(Document, await rig.Skills.GetAsync(Server, TestTimeout));
        Assert.AreEqual("fresh", (await rig.Harness.Index.GetDetailsAsync(Server, TestTimeout)).SkillFreshness);
    }

    [TestMethod]
    public async Task NoModelConfigured_ReportsUnavailableWithTheDrift()
    {
        await using var rig = await CreateAsync(modelConfigured: false);
        UpgradeDownstream(rig.Harness);

        var result = await RefreshAsync(rig);

        Assert.AreEqual("unavailable", result.Status);
        Assert.IsNotNull(result.Drift);
    }

    [TestMethod]
    public async Task ModelFailure_IsReportedNotThrown()
    {
        await using var rig = await CreateAsync();
        UpgradeDownstream(rig.Harness);
        rig.Model.Respond = _ => throw new HttpRequestException("model endpoint is down");

        var result = await RefreshAsync(rig);

        Assert.AreEqual("failed", result.Status);
        StringAssert.Contains(result.Message, "model endpoint is down");
    }

    [TestMethod]
    public async Task RefreshSkillTool_ReturnsJson()
    {
        await using var rig = await CreateAsync();
        UpgradeDownstream(rig.Harness);

        var json = await AdminTools.RefreshSkill(rig.Maintainer, Server, ct: TestTimeout);

        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual("proposed", doc.RootElement.GetProperty("status").GetString());
        Assert.IsTrue(doc.RootElement.GetProperty("validationPassed").GetBoolean());
    }

    // ---------------------------------------------------------------- history and restore

    [TestMethod]
    public async Task RestoreSkill_BringsBackTheOldVersionAndItsBaseline()
    {
        await using var rig = await CreateAsync();
        UpgradeDownstream(rig.Harness);
        await RefreshAsync(rig, apply: true);
        var version = (await rig.Skills.ListHistoryAsync(Server, TestTimeout)).Single();
        Assert.IsTrue(version.HasBaseline);
        Assert.AreEqual("1.0.0", version.RecordedVersion);

        var message = await AdminTools.RestoreSkill(rig.Harness.Registry, rig.Skills, rig.Harness.Index, Server, version.Version, TestTimeout);

        StringAssert.Contains(message, "Restored");
        Assert.AreEqual(Document, await rig.Skills.GetAsync(Server, TestTimeout));
        var details = await rig.Harness.Index.GetDetailsAsync(Server, TestTimeout);
        Assert.AreEqual("stale", details.SkillFreshness, "The old document is judged against the catalog it was written for.");
        CollectionAssert.AreEqual(new[] { "list_accounts" }, details.SkillDrift!.ToolsRemoved);
        Assert.AreEqual(2, (await rig.Skills.ListHistoryAsync(Server, TestTimeout)).Count, "The revised version went to history too.");
    }

    [TestMethod]
    public async Task RestoreSkill_UnchangedServer_ReadsFresh()
    {
        await using var rig = await CreateAsync();
        await AdminTools.UpdateSkill(rig.Harness.Registry, rig.Skills, rig.Harness.Index, Server, Document + "\nMore.\n", TestTimeout);
        var version = (await rig.Skills.ListHistoryAsync(Server, TestTimeout)).Single().Version;

        await AdminTools.RestoreSkill(rig.Harness.Registry, rig.Skills, rig.Harness.Index, Server, version, TestTimeout);

        Assert.AreEqual("fresh", (await rig.Harness.Index.GetDetailsAsync(Server, TestTimeout)).SkillFreshness,
            "The fingerprint recomputed from the stored baseline matches the live catalog.");
    }

    [TestMethod]
    public async Task RestoreSkill_UnknownOrHostileVersion_ChangesNothing()
    {
        await using var rig = await CreateAsync();

        foreach (var version in new[] { "20990101T000000000Z", "../../registry", "..\\x" })
        {
            var message = await AdminTools.RestoreSkill(rig.Harness.Registry, rig.Skills, rig.Harness.Index, Server, version, TestTimeout);
            StringAssert.Contains(message, "No version");
        }
        Assert.AreEqual(Document, await rig.Skills.GetAsync(Server, TestTimeout));
    }

    [TestMethod]
    public async Task ListSkillHistoryTool_ReturnsNewestFirst()
    {
        await using var rig = await CreateAsync();
        await AdminTools.UpdateSkill(rig.Harness.Registry, rig.Skills, rig.Harness.Index, Server, Document + "v2\n", TestTimeout);
        await AdminTools.UpdateSkill(rig.Harness.Registry, rig.Skills, rig.Harness.Index, Server, Document + "v3\n", TestTimeout);

        var json = await AdminTools.ListSkillHistory(rig.Harness.Registry, rig.Skills, Server, TestTimeout);

        var versions = JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.GetProperty("version").GetString()).ToList();
        Assert.AreEqual(2, versions.Count);
        Assert.IsTrue(string.CompareOrdinal(versions[0], versions[1]) > 0);
    }
}
