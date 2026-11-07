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
/// The background skill upkeep (issue #58) and its draft tools, end to end against live in-memory
/// downstreams and a model double: what each mode does with a stale skill, at most one draft per
/// server state, the daily cap, failure back-off, discard, and the refresh_service wake-up.
/// </summary>
[TestClass]
public class SkillUpkeepTests
{
    private const string Alpha = "alpha";
    private const string Beta = "beta";
    private const string Document = "# Skill Guide\n\nCall `list_accounts` first, then `create_event` with a subject.\n";
    private const string Revised = "# Skill Guide\n\nCall `create_event` with a subject; set `isAllDay` for all-day events.\n";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-upkeep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        AggregatorTelemetry.StaleSkills = 0;
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

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Rig(
        WrapperHarness Harness, SkillStore Skills, SkillMaintainer Maintainer, FakeChatClient Model,
        SkillMaintenanceHostedService Upkeep, SkillMaintenanceSignal Signal, ManualTime Time, SkillMaintenanceOptions Options)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Upkeep.Dispose();
            await Harness.DisposeAsync();
        }
    }

    private async Task<Rig> CreateAsync(SkillMaintenanceMode mode, Action<SkillMaintenanceOptions>? configure = null, bool twoServers = false)
    {
        var downstreams = new List<(string, McpServerTool[])> { (Alpha, [Tool(CreateEvent, "create_event"), Tool(ListAccounts, "list_accounts")]) };
        if (twoServers)
            downstreams.Add((Beta, [Tool(CreateEvent, "create_event"), Tool(ListAccounts, "list_accounts")]));
        var harness = await WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Lazy, downstreams.ToArray());

        var skills = new SkillStore(TestHelpers.OptionsOf(TestHelpers.DefaultAggregatorOptions(_dataDir)), TestHelpers.NullLoggerOf<SkillStore>());
        foreach (var (name, _) in downstreams)
            await AdminTools.UpdateSkill(harness.Registry, skills, harness.Index, name, Document, TestTimeout);

        var options = new SkillMaintenanceOptions { Mode = mode, StartupDelay = TimeSpan.Zero };
        configure?.Invoke(options);
        var model = new FakeChatClient { Respond = _ => Revised };
        var services = new ServiceCollection().AddSingleton<IChatClient>(model).BuildServiceProvider();
        var maintainer = new SkillMaintainer(harness.Registry, harness.Index, skills, TestHelpers.OptionsOf(options),
            TestHelpers.OptionsOf(new AiOptions { Enabled = true, Model = "test-model" }), services,
            TestHelpers.NullLoggerOf<SkillMaintainer>());
        var signal = new SkillMaintenanceSignal(TestHelpers.OptionsOf(options));
        var time = new ManualTime();
        var upkeep = new SkillMaintenanceHostedService(harness.Registry, harness.Index, maintainer, TestHelpers.OptionsOf(options),
            signal, TestHelpers.NullLoggerOf<SkillMaintenanceHostedService>(), time);
        return new Rig(harness, skills, maintainer, model, upkeep, signal, time, options);
    }

    /// <summary><c>create_event</c> gains <c>isAllDay</c>; <c>list_accounts</c> goes away.</summary>
    private static void Upgrade(WrapperHarness harness, string server = Alpha)
    {
        var tools = harness.DownstreamTools[server];
        tools.Remove(tools["create_event"]);
        tools.Remove(tools["list_accounts"]);
        tools.Add(Tool(CreateEventV2, "create_event"));
        harness.Index.InvalidateCache(server);
    }

    private static Task<string> ServedSkillAsync(Rig rig, string server = Alpha)
        => ConsumerTools.GetServiceSkill(rig.Skills, rig.Harness.Registry, rig.Harness.Index, rig.Maintainer,
            TestHelpers.OptionsOf(rig.Options), server, TestTimeout);

    // ---------------------------------------------------------------- modes

    [TestMethod]
    public async Task Notify_CountsStaleSkillsWithoutRevising()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Notify);
        Upgrade(rig.Harness);

        await rig.Upkeep.SweepAsync(TestTimeout);

        Assert.AreEqual(1, AggregatorTelemetry.StaleSkills);
        Assert.AreEqual(0, rig.Model.Requests.Count);
        Assert.IsNull(await rig.Skills.GetDraftAsync(Alpha, TestTimeout));
    }

    [TestMethod]
    public async Task Off_DoesNothingAndServesNoNotice()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Off);
        Upgrade(rig.Harness);

        Assert.IsFalse(await rig.Upkeep.CheckServerAsync(Alpha, TestTimeout));
        Assert.AreEqual(0, rig.Model.Requests.Count);
        Assert.AreEqual(Document, await ServedSkillAsync(rig));
    }

    [TestMethod]
    public async Task Propose_DraftsOnceAndLeavesTheDocumentAlone()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose);
        Upgrade(rig.Harness);

        await rig.Upkeep.SweepAsync(TestTimeout);
        await rig.Upkeep.SweepAsync(TestTimeout);

        Assert.AreEqual(1, rig.Model.Requests.Count, "A draft that still fits is never made again.");
        Assert.AreEqual(Document, await rig.Skills.GetAsync(Alpha, TestTimeout));
        var draft = await rig.Skills.GetDraftAsync(Alpha, TestTimeout);
        Assert.IsTrue(draft!.ValidationPassed);
        Assert.AreEqual(1, AggregatorTelemetry.StaleSkills, "Still stale until someone applies the draft.");
    }

    [TestMethod]
    public async Task Propose_NoticeSaysADraftIsWaiting()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose);
        Upgrade(rig.Harness);
        await rig.Upkeep.SweepAsync(TestTimeout);

        var served = await ServedSkillAsync(rig);

        StringAssert.Contains(served, "A revised draft of this document");
        StringAssert.Contains(served, "`apply_skill_draft` publishes it");
    }

    [TestMethod]
    public async Task Propose_FailedDraftIsNotRetriedForTheSameState()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose);
        Upgrade(rig.Harness);
        rig.Model.Respond = _ => Document; // still names the removed list_accounts

        await rig.Upkeep.SweepAsync(TestTimeout);
        await rig.Upkeep.SweepAsync(TestTimeout);

        Assert.AreEqual(1, rig.Model.Requests.Count);
        Assert.IsFalse((await rig.Skills.GetDraftAsync(Alpha, TestTimeout))!.ValidationPassed);
        StringAssert.Contains(await ServedSkillAsync(rig), "failed validation");
    }

    [TestMethod]
    public async Task Auto_PublishesAValidDraft()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Auto);
        Upgrade(rig.Harness);

        await rig.Upkeep.SweepAsync(TestTimeout);

        StringAssert.StartsWith(await rig.Skills.GetAsync(Alpha, TestTimeout), Revised.TrimEnd());
        Assert.AreEqual("fresh", (await rig.Harness.Index.GetDetailsAsync(Alpha, TestTimeout)).SkillFreshness);
        Assert.AreEqual(1, (await rig.Skills.ListHistoryAsync(Alpha, TestTimeout)).Count);
        Assert.AreEqual(0, AggregatorTelemetry.StaleSkills);
    }

    [TestMethod]
    public async Task Auto_PublishesAWaitingValidDraftWithoutTheModel()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Auto);
        Upgrade(rig.Harness);
        await rig.Maintainer.RefreshAsync(Alpha, apply: false, regenerate: false, TestTimeout); // proposed by hand

        await rig.Upkeep.SweepAsync(TestTimeout);

        Assert.AreEqual(1, rig.Model.Requests.Count);
        StringAssert.StartsWith(await rig.Skills.GetAsync(Alpha, TestTimeout), Revised.TrimEnd());
    }

    [TestMethod]
    public async Task Auto_FailedDraftWaitsForReview()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Auto);
        Upgrade(rig.Harness);
        rig.Model.Respond = _ => Document;

        await rig.Upkeep.SweepAsync(TestTimeout);
        await rig.Upkeep.SweepAsync(TestTimeout);

        Assert.AreEqual(Document, await rig.Skills.GetAsync(Alpha, TestTimeout));
        Assert.AreEqual(1, rig.Model.Requests.Count);
    }

    [TestMethod]
    public async Task VersionOnlyChange_IsRebaselinedWithoutTheModel()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose);
        var server = rig.Harness.Registry.Get(Alpha);
        await rig.Harness.Registry.UpdateRemoteMetadataAsync(Alpha, server.RemoteName, server.RemoteTitle, "1.1.0",
            server.RemoteInstructions, TestTimeout);

        await rig.Upkeep.SweepAsync(TestTimeout);

        Assert.AreEqual(0, rig.Model.Requests.Count);
        Assert.AreEqual("fresh", (await rig.Harness.Index.GetDetailsAsync(Alpha, TestTimeout)).SkillFreshness);
        Assert.AreEqual(0, AggregatorTelemetry.StaleSkills);
    }

    // ---------------------------------------------------------------- limits

    [TestMethod]
    public async Task DailyCap_LimitsModelCallsUntilTheNextUtcDay()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose, o => o.MaxRevisionsPerDay = 1, twoServers: true);
        Upgrade(rig.Harness, Alpha);
        Upgrade(rig.Harness, Beta);

        await rig.Upkeep.SweepAsync(TestTimeout);
        Assert.AreEqual(1, rig.Model.Requests.Count);
        Assert.AreEqual(2, AggregatorTelemetry.StaleSkills);

        rig.Time.Now = rig.Time.Now.AddDays(1);
        await rig.Upkeep.SweepAsync(TestTimeout);

        Assert.AreEqual(2, rig.Model.Requests.Count, "The second server gets its draft the next day.");
    }

    [TestMethod]
    public async Task ModelFailure_BacksOffThenRetries()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose, o => o.FailureBackoff = TimeSpan.FromHours(1));
        Upgrade(rig.Harness);
        rig.Model.Respond = _ => throw new HttpRequestException("model down");

        await rig.Upkeep.CheckServerAsync(Alpha, TestTimeout);
        await rig.Upkeep.CheckServerAsync(Alpha, TestTimeout);
        Assert.AreEqual(1, rig.Model.Requests.Count, "Backed off after the failure.");

        rig.Time.Now = rig.Time.Now.AddMinutes(61);
        rig.Model.Respond = _ => Revised;
        await rig.Upkeep.CheckServerAsync(Alpha, TestTimeout);

        Assert.AreEqual(2, rig.Model.Requests.Count);
        Assert.IsNotNull(await rig.Skills.GetDraftAsync(Alpha, TestTimeout));
    }

    // ---------------------------------------------------------------- draft tools

    [TestMethod]
    public async Task ListAndApplyDraft_PublishTheWaitingDraft()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose);
        Upgrade(rig.Harness);
        await rig.Upkeep.SweepAsync(TestTimeout);

        var all = JsonDocument.Parse(await AdminTools.ListSkillDrafts(rig.Maintainer, ct: TestTimeout)).RootElement;
        var one = JsonDocument.Parse(await AdminTools.ListSkillDrafts(rig.Maintainer, Alpha, TestTimeout)).RootElement.EnumerateArray().Single();
        var applied = JsonDocument.Parse(await AdminTools.ApplySkillDraft(rig.Maintainer, Alpha, TestTimeout)).RootElement;

        var summary = all.EnumerateArray().Single();
        Assert.AreEqual(Alpha, summary.GetProperty("serverName").GetString());
        Assert.IsTrue(summary.GetProperty("current").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, summary.GetProperty("draft").ValueKind, "The list stays small.");
        StringAssert.Contains(one.GetProperty("diff").GetString(), "+Call `create_event` with a subject; set `isAllDay`");
        Assert.AreEqual("applied", applied.GetProperty("status").GetString());
        Assert.AreEqual(1, rig.Model.Requests.Count, "apply_skill_draft never calls the model.");
        StringAssert.StartsWith(await rig.Skills.GetAsync(Alpha, TestTimeout), Revised.TrimEnd());
    }

    [TestMethod]
    public async Task ApplyDraft_ObsoleteAfterTheCatalogMoved()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose);
        Upgrade(rig.Harness);
        await rig.Upkeep.SweepAsync(TestTimeout);
        rig.Harness.DownstreamTools[Alpha].Add(Tool(ListAccounts, "list_accounts"));
        rig.Harness.Index.InvalidateCache(Alpha);

        var result = await rig.Maintainer.ApplyDraftAsync(Alpha, TestTimeout);

        Assert.AreEqual("obsolete", result.Status);
        Assert.AreEqual(Document, await rig.Skills.GetAsync(Alpha, TestTimeout));
    }

    [TestMethod]
    public async Task DiscardedDraft_IsHiddenNotAppliedAndNotRedrafted()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose);
        Upgrade(rig.Harness);
        await rig.Upkeep.SweepAsync(TestTimeout);

        StringAssert.Contains(await AdminTools.DiscardSkillDraft(rig.Maintainer, Alpha, TestTimeout), "Discarded");
        await rig.Upkeep.SweepAsync(TestTimeout);

        Assert.AreEqual(1, rig.Model.Requests.Count, "The upkeep doesn't draft again for the same state.");
        Assert.AreEqual("[]", (await AdminTools.ListSkillDrafts(rig.Maintainer, ct: TestTimeout)).Trim());
        Assert.AreEqual("no_draft", (await rig.Maintainer.ApplyDraftAsync(Alpha, TestTimeout)).Status);
        Assert.IsFalse((await ServedSkillAsync(rig)).Contains("draft of this document"));

        var manual = await rig.Maintainer.RefreshAsync(Alpha, apply: false, regenerate: false, TestTimeout);
        Assert.AreEqual("proposed", manual.Status);
        Assert.AreEqual(2, rig.Model.Requests.Count, "refresh_skill still drafts on request.");
    }

    [TestMethod]
    public async Task RefreshSkillProposal_ReusesAFittingDraft()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose);
        Upgrade(rig.Harness);
        await rig.Upkeep.SweepAsync(TestTimeout);

        var result = await rig.Maintainer.RefreshAsync(Alpha, apply: false, regenerate: false, TestTimeout);

        Assert.AreEqual("proposed", result.Status);
        Assert.IsTrue(result.FromStoredDraft);
        Assert.AreEqual(1, rig.Model.Requests.Count);
    }

    // ---------------------------------------------------------------- review fixes (PR #62)

    [TestMethod]
    public async Task Notice_DoesNotAnnounceADraftTheCatalogHasOutdated()
    {
        // The draft still matches the document text, but apply_skill_draft would call it obsolete.
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose);
        Upgrade(rig.Harness);
        await rig.Upkeep.SweepAsync(TestTimeout);
        rig.Harness.DownstreamTools[Alpha].Add(Tool(ListAccounts, "list_accounts"));
        rig.Harness.Index.InvalidateCache(Alpha);

        var served = await ServedSkillAsync(rig);

        StringAssert.StartsWith(served, SkillNotice.StartMarker, "Still stale.");
        Assert.IsFalse(served.Contains("draft of this document"), "The outdated draft must not be announced.");
        Assert.AreEqual("obsolete", (await rig.Maintainer.ApplyDraftAsync(Alpha, TestTimeout)).Status);
    }

    [TestMethod]
    public async Task Discard_LeavesADraftSavedInTheMeantimeAlone()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose);
        Upgrade(rig.Harness);
        await rig.Upkeep.SweepAsync(TestTimeout);
        var read = (await rig.Skills.GetDraftAsync(Alpha, TestTimeout))!;
        // A newer draft lands between discard_skill_draft's read and its write.
        rig.Model.Respond = _ => Revised + "\nAlso: `create_event` returns the event id.\n";
        await rig.Maintainer.RefreshAsync(Alpha, apply: false, regenerate: true, TestTimeout);

        var outcome = await rig.Skills.DiscardDraftIfUnchangedAsync(
            Alpha, read.CreatedAt, SkillStore.ContentHash(read.Markdown), TestTimeout);

        Assert.AreEqual(DraftDiscardOutcome.Replaced, outcome);
        var stored = (await rig.Skills.GetDraftAsync(Alpha, TestTimeout))!;
        Assert.IsFalse(stored.Discarded);
        StringAssert.Contains(stored.Markdown, "returns the event id", "The newer draft survives intact.");
    }

    [TestMethod]
    public async Task DiscardTool_ReportsTheOutcome()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose);
        Upgrade(rig.Harness);
        await rig.Upkeep.SweepAsync(TestTimeout);

        StringAssert.Contains(await AdminTools.DiscardSkillDraft(rig.Maintainer, Alpha, TestTimeout), "Discarded");
        StringAssert.Contains(await AdminTools.DiscardSkillDraft(rig.Maintainer, Alpha, TestTimeout), "No draft was waiting");
    }

    [TestMethod]
    public async Task CancellationThatIsNotShutdown_IsLoggedAndSwallowed()
    {
        // An HTTP timeout to a downstream surfaces as TaskCanceledException; it must not fault the service.
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose);

        await rig.Upkeep.RunSafelyAsync(() => throw new TaskCanceledException("downstream timed out"), "test", CancellationToken.None);

        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => rig.Upkeep.RunSafelyAsync(() => throw new OperationCanceledException(stopping.Token), "test", stopping.Token),
            "Shutdown still propagates.");
    }

    [TestMethod]
    public void Signal_IsDroppedWhenUpkeepIsOff_AndBounded()
    {
        var off = new SkillMaintenanceSignal(TestHelpers.OptionsOf(new SkillMaintenanceOptions { Mode = SkillMaintenanceMode.Off }));
        Assert.IsFalse(off.Request(Alpha));
        Assert.IsFalse(off.Reader.TryRead(out _), "Nothing queues up when no one reads.");

        var on = new SkillMaintenanceSignal(TestHelpers.OptionsOf(new SkillMaintenanceOptions { Mode = SkillMaintenanceMode.Propose }));
        var accepted = Enumerable.Range(0, SkillMaintenanceSignal.Capacity + 10).Count(i => on.Request($"server-{i}"));
        Assert.AreEqual(SkillMaintenanceSignal.Capacity, accepted);
    }

    // ---------------------------------------------------------------- the running service

    [TestMethod]
    public async Task RefreshService_WakesTheUpkeepForThatServer()
    {
        await using var rig = await CreateAsync(SkillMaintenanceMode.Propose, o => o.CheckInterval = TimeSpan.FromDays(1));
        var upkeep = new SkillMaintenanceHostedService(rig.Harness.Registry, rig.Harness.Index, rig.Maintainer,
            TestHelpers.OptionsOf(rig.Options), rig.Signal, TestHelpers.NullLoggerOf<SkillMaintenanceHostedService>());
        await upkeep.StartAsync(TestTimeout);
        try
        {
            await Task.Delay(200); // the first sweep: nothing is stale yet
            Assert.AreEqual(0, rig.Model.Requests.Count);

            Upgrade(rig.Harness);
            await ConsumerTools.RefreshService(rig.Harness.Index, rig.Harness.Connections, rig.Signal, Alpha, TestTimeout);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (await rig.Skills.GetDraftAsync(Alpha, TestTimeout) is null && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.IsNotNull(await rig.Skills.GetDraftAsync(Alpha, TestTimeout), "refresh_service must trigger a check well before the daily sweep.");
        }
        finally
        {
            await upkeep.StopAsync(CancellationToken.None);
            upkeep.Dispose();
        }
    }
}
