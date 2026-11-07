using System.Net;
using System.Text;
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
/// Change sources for skill revision (issue #59): release notes over an allow-listed https host or a
/// GitHub location, the server's own guide tool and resources, size caps, failures as reports, and how
/// a revision uses them. HTTP is a fake handler; the downstream is a live in-memory server.
/// </summary>
[TestClass]
public class SkillSourcesTests
{
    private const string Server = "alpha";
    private const string Document = "# Skill Guide\n\nCall `create_event` with a subject.\n";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-sources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _guideCalls = 0;
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

    private static int _guideCalls;

    [SysDescription("Read a guide.")]
    private static string GetGuide([SysDescription("Topic")] string? name = null)
        => Interlocked.Increment(ref _guideCalls) > 0 && name is null
            ? "Guides: calendar, email"
            : name == "broken"
                ? throw new InvalidOperationException("no such guide")
                : $"Guide {name}: all-day events are floating dates. See https://docs.example.com/{name}";

    [SysDescription("Overview documentation.")]
    private static string Overview() => "Overview: start with list_accounts.";

    private static McpServerTool Tool(Delegate method, string name)
        => McpServerTool.Create(method, new McpServerToolCreateOptions { Name = name });

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    /// <summary>Answers by exact URL; records every request.</summary>
    private sealed class FakeHttp : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> Responses { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Redirects { get; } = new(StringComparer.Ordinal);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Redirects.TryGetValue(request.RequestUri!.ToString(), out var location))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
                return Task.FromResult(redirect);
            }
            var (status, body) = Responses.TryGetValue(request.RequestUri!.ToString(), out var r) ? r : (HttpStatusCode.NotFound, "not found");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }

    private sealed record Rig(WrapperHarness Harness, SkillStore Skills, SkillSourceReader Reader, SkillMaintainer Maintainer,
        FakeChatClient Model, FakeHttp Http, SkillMaintenanceOptions Options) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Harness.DisposeAsync();
    }

    private async Task<Rig> CreateAsync(Action<SkillMaintenanceOptions>? configure = null)
    {
        var resources = new[]
        {
            McpServerResource.Create(Overview, new McpServerResourceCreateOptions { UriTemplate = "guide://overview", Name = "overview" }),
        };
        var harness = await WrapperHarness.CreateWithResourcesAsync(_dataDir, WrapperToolMode.Lazy,
            (Server, [Tool(CreateEvent, "create_event"), Tool(GetGuide, "get_guide")], null, resources));
        var skills = new SkillStore(TestHelpers.OptionsOf(TestHelpers.DefaultAggregatorOptions(_dataDir)), TestHelpers.NullLoggerOf<SkillStore>());
        await AdminTools.UpdateSkill(harness.Registry, skills, harness.Index, Server, Document, TestTimeout);

        var options = new SkillMaintenanceOptions { Mode = SkillMaintenanceMode.Propose, StartupDelay = TimeSpan.Zero };
        configure?.Invoke(options);
        var http = new FakeHttp();
        var reader = new SkillSourceReader(harness.Proxy, TestHelpers.OptionsOf(options), TestHelpers.NullLoggerOf<SkillSourceReader>(), new HttpClient(http));
        var model = new FakeChatClient { Respond = _ => Document };
        var maintainer = new SkillMaintainer(harness.Registry, harness.Index, skills, TestHelpers.OptionsOf(options),
            TestHelpers.OptionsOf(new AiOptions { Enabled = true, Model = "test-model" }),
            new ServiceCollection().AddSingleton<IChatClient>(model).BuildServiceProvider(),
            TestHelpers.NullLoggerOf<SkillMaintainer>(), reader);
        return new Rig(harness, skills, reader, maintainer, model, http, options);
    }

    private static async Task SetSourcesAsync(Rig rig, SkillSources sources)
        => await rig.Harness.Registry.UpdateSkillSourcesAsync(Server, sources, TestTimeout);

    private static async Task BumpVersionAsync(Rig rig, string version)
    {
        var server = rig.Harness.Registry.Get(Server);
        await rig.Harness.Registry.UpdateRemoteMetadataAsync(Server, server.RemoteName, server.RemoteTitle, version,
            server.RemoteInstructions, TestTimeout);
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    // ---------------------------------------------------------------- validation of locations

    [TestMethod]
    public async Task ValidateChangeNotes_AcceptsAllowedLocationsOnly()
    {
        await using var rig = await CreateAsync(o => o.AllowedChangeNoteHosts.Add("docs.example.com"));

        Assert.IsNull(rig.Reader.ValidateChangeNotes("https://docs.example.com/changelog.md"));
        Assert.IsNull(rig.Reader.ValidateChangeNotes("MarimerLLC/calendar-mcp:changelogs"));
        Assert.IsNull(rig.Reader.ValidateChangeNotes("MarimerLLC/calendar-mcp@main:CHANGELOG.md"));
        StringAssert.Contains(rig.Reader.ValidateChangeNotes("http://docs.example.com/x"), "https");
        StringAssert.Contains(rig.Reader.ValidateChangeNotes("https://evil.example/x"), "not in SkillMaintenance:AllowedChangeNoteHosts");
        StringAssert.Contains(rig.Reader.ValidateChangeNotes("not a location"), "neither");
        StringAssert.Contains(rig.Reader.ValidateChangeNotes("owner/repo:../../etc/passwd;rm"), "neither");
    }

    [TestMethod]
    public async Task ValidateChangeNotes_GitHubNeedsApiHostAllowed()
    {
        await using var rig = await CreateAsync(o => o.AllowedChangeNoteHosts.Clear());

        StringAssert.Contains(rig.Reader.ValidateChangeNotes("owner/repo:changelogs"), "api.github.com");
    }

    // ---------------------------------------------------------------- change notes

    [TestMethod]
    public async Task GitHubFolder_ReadsDatedFilesFromTheBaselineOn_NewestFirst()
    {
        await using var rig = await CreateAsync(o => o.GitHubToken = "ghp_test");
        rig.Http.Responses["https://api.github.com/repos/MarimerLLC/calendar-mcp/contents/changelogs"] = (HttpStatusCode.OK, JsonSerializer.Serialize(new object[]
        {
            new { name = "2026-05-01-old.md", type = "file", download_url = "https://raw.githubusercontent.com/x/2026-05-01-old.md" },
            new { name = "2026-09-22-read-failures.md", type = "file", download_url = "https://raw.githubusercontent.com/x/2026-09-22-read-failures.md" },
            new { name = "2026-10-19-response-status.md", type = "file", download_url = "https://raw.githubusercontent.com/x/2026-10-19-response-status.md" },
            new { name = "README.md", type = "file", download_url = "https://raw.githubusercontent.com/x/README.md" },
            new { name = "archive", type = "dir", download_url = (string?)null },
        }));
        rig.Http.Responses["https://raw.githubusercontent.com/x/2026-09-22-read-failures.md"] = (HttpStatusCode.OK, "Read failures now reach the client as warnings.");
        rig.Http.Responses["https://raw.githubusercontent.com/x/2026-10-19-response-status.md"] = (HttpStatusCode.OK, "Events now carry responseStatus.");
        await SetSourcesAsync(rig, new SkillSources { ChangeNotes = "MarimerLLC/calendar-mcp:changelogs" });

        var material = await rig.Reader.ReadAsync(rig.Harness.Registry.Get(Server), new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), TestTimeout);

        StringAssert.Contains(material.ChangeNotes, "responseStatus");
        StringAssert.Contains(material.ChangeNotes, "warnings");
        Assert.IsTrue(material.ChangeNotes.IndexOf("responseStatus") < material.ChangeNotes.IndexOf("warnings"), "Newest first.");
        Assert.IsFalse(material.ChangeNotes.Contains("old"), "Files before the baseline are skipped.");
        Assert.IsFalse(rig.Http.Requests.Any(r => r.RequestUri!.ToString().EndsWith("README.md")), "Undated files are skipped when a baseline date is known.");
        var api = rig.Http.Requests.First();
        Assert.AreEqual("Bearer ghp_test", api.Headers.Authorization?.ToString());
        StringAssert.Contains(api.Headers.UserAgent.ToString(), "mcp-aggregator");
        Assert.AreEqual(2, material.Reports.Count(r => r.Kind == "change_notes" && r.Error is null));
    }

    [TestMethod]
    public async Task GitHubFile_IsDecodedFromTheContentsApi()
    {
        await using var rig = await CreateAsync();
        rig.Http.Responses["https://api.github.com/repos/owner/repo/contents/CHANGELOG.md?ref=v2"] =
            (HttpStatusCode.OK, JsonSerializer.Serialize(new { content = Base64("## 2.0\n- Search is now case-insensitive.") + "\n", encoding = "base64" }));
        await SetSourcesAsync(rig, new SkillSources { ChangeNotes = "owner/repo@v2:CHANGELOG.md" });

        var material = await rig.Reader.ReadAsync(rig.Harness.Registry.Get(Server), null, TestTimeout);

        StringAssert.Contains(material.ChangeNotes, "case-insensitive");
    }

    [TestMethod]
    public async Task UrlNotes_AreCappedPerSourceAndOverall()
    {
        await using var rig = await CreateAsync(o =>
        {
            o.AllowedChangeNoteHosts.Add("docs.example.com");
            o.MaxSourceChars = 60;
            o.MaxSourcesTotalChars = 200;
        });
        rig.Http.Responses["https://docs.example.com/notes.md"] = (HttpStatusCode.OK, new string('n', 500));
        await SetSourcesAsync(rig, new SkillSources
        {
            GuideTool = "get_guide",
            GuideArguments = [JsonSerializer.SerializeToElement(new { name = "calendar" })],
            ChangeNotes = "https://docs.example.com/notes.md",
        });

        var material = await rig.Reader.ReadAsync(rig.Harness.Registry.Get(Server), null, TestTimeout);

        var guide = material.Reports.Single(r => r.Kind == "guide_tool");
        var notes = material.Reports.Single(r => r.Kind == "change_notes");
        Assert.IsTrue(guide.Chars <= 60);
        Assert.IsTrue(notes.Truncated);
        Assert.IsTrue(notes.Chars <= 60, "Per-source limit.");
        Assert.IsTrue(material.Guides.Length + material.ChangeNotes.Length <= 200,
            "Headings, text and markers together stay within the total.");
        StringAssert.Contains(material.ChangeNotes, "[... cut to the size limit]");
    }

    [TestMethod]
    public async Task Failures_BecomeReports_NotExceptions()
    {
        await using var rig = await CreateAsync();
        rig.Http.Responses["https://api.github.com/repos/owner/repo/contents/changelogs"] = (HttpStatusCode.OK, JsonSerializer.Serialize(new object[]
        {
            new { name = "2026-10-01-x.md", type = "file", download_url = "https://evil.example/2026-10-01-x.md" },
        }));
        await SetSourcesAsync(rig, new SkillSources
        {
            ChangeNotes = "owner/repo:changelogs",
            GuideTool = "get_guide",
            GuideArguments = [JsonSerializer.SerializeToElement(new { name = "broken" })],
            GuideResources = ["guide://missing"],
        });

        var material = await rig.Reader.ReadAsync(rig.Harness.Registry.Get(Server), null, TestTimeout);

        Assert.IsTrue(material.IsEmpty);
        Assert.AreEqual(3, material.Reports.Count(r => r.Error is not null), string.Join(" | ", material.Reports.Select(r => $"{r.Source}: {r.Error}")));
        StringAssert.Contains(material.Reports.Single(r => r.Kind == "change_notes").Error, "not allowed");
        Assert.IsFalse(rig.Http.Requests.Any(r => r.RequestUri!.Host == "evil.example"), "Never fetched.");
    }

    [TestMethod]
    public async Task MissingGitHubPath_IsReported()
    {
        await using var rig = await CreateAsync();
        await SetSourcesAsync(rig, new SkillSources { ChangeNotes = "owner/repo:nope" });

        var material = await rig.Reader.ReadAsync(rig.Harness.Registry.Get(Server), null, TestTimeout);

        StringAssert.Contains(material.Reports.Single().Error, "404");
    }

    // ---------------------------------------------------------------- PR #65 review

    [TestMethod]
    public async Task Redirect_ToADisallowedHost_IsRefusedWithoutContactingIt()
    {
        await using var rig = await CreateAsync(o => o.AllowedChangeNoteHosts.Add("docs.example.com"));
        rig.Http.Redirects["https://docs.example.com/notes.md"] = "https://evil.example/notes.md";
        await SetSourcesAsync(rig, new SkillSources { ChangeNotes = "https://docs.example.com/notes.md" });

        var material = await rig.Reader.ReadAsync(rig.Harness.Registry.Get(Server), null, TestTimeout);

        StringAssert.Contains(material.Reports.Single().Error, "not allowed (evil.example)");
        Assert.IsFalse(rig.Http.Requests.Any(r => r.RequestUri!.Host == "evil.example"));
    }

    [TestMethod]
    public async Task Redirect_ToAnAllowedHost_IsFollowed_AndTheTokenStaysOnGitHub()
    {
        await using var rig = await CreateAsync(o => o.GitHubToken = "ghp_test");
        rig.Http.Redirects["https://api.github.com/repos/owner/repo/contents/CHANGELOG.md"] = "https://raw.githubusercontent.com/owner/repo/main/CHANGELOG.md";
        rig.Http.Responses["https://raw.githubusercontent.com/owner/repo/main/CHANGELOG.md"] =
            (HttpStatusCode.OK, JsonSerializer.Serialize(new { content = Base64("Moved but fine."), encoding = "base64" }));
        await SetSourcesAsync(rig, new SkillSources { ChangeNotes = "owner/repo:CHANGELOG.md" });

        var material = await rig.Reader.ReadAsync(rig.Harness.Registry.Get(Server), null, TestTimeout);

        StringAssert.Contains(material.ChangeNotes, "Moved but fine.");
        Assert.IsNull(rig.Http.Requests.Single(r => r.RequestUri!.Host == "raw.githubusercontent.com").Headers.Authorization);
    }

    [TestMethod]
    public async Task ExhaustedBudget_SkipsLaterSourcesWithoutRequestingThem()
    {
        await using var rig = await CreateAsync(o => { o.MaxSourceChars = 1000; o.MaxSourcesTotalChars = 80; });
        await SetSourcesAsync(rig, new SkillSources
        {
            GuideTool = "get_guide",
            GuideArguments = [JsonSerializer.SerializeToElement(new { name = "calendar" }), JsonSerializer.SerializeToElement(new { name = "email" }), JsonSerializer.SerializeToElement(new { name = "contacts" })],
            GuideResources = ["guide://overview"],
            ChangeNotes = "owner/repo:changelogs",
        });

        var material = await rig.Reader.ReadAsync(rig.Harness.Registry.Get(Server), null, TestTimeout);

        Assert.AreEqual(1, _guideCalls, "Only the first guide fit; the rest were never called.");
        Assert.AreEqual(4, material.Reports.Count(r => r.Error == "skipped: the size limit for all sources was reached"),
            string.Join(" | ", material.Reports.Select(r => $"{r.Source}: {r.Error}")));
        Assert.AreEqual(0, rig.Http.Requests.Count, "No change-notes request once the budget is gone.");
    }

    [TestMethod]
    public async Task LongGuideArguments_AreBoundedAndCounted()
    {
        await using var rig = await CreateAsync(o => o.MaxSourcesTotalChars = 600);
        var huge = new string('a', 5000);
        await SetSourcesAsync(rig, new SkillSources
        {
            GuideTool = "get_guide",
            GuideArguments = [JsonSerializer.SerializeToElement(new { name = huge })],
        });

        var material = await rig.Reader.ReadAsync(rig.Harness.Registry.Get(Server), null, TestTimeout);

        Assert.IsTrue(material.Guides.Length <= 600, $"rendered {material.Guides.Length}");
        Assert.IsTrue(material.Reports.Single().Source.Length <= 201);
    }

    [TestMethod]
    public async Task ChangingSources_MakesEarlierDraftsStale()
    {
        await using var rig = await CreateAsync();
        await SetSourcesAsync(rig, new SkillSources { GuideTool = "get_guide" });
        await BumpVersionAsync(rig, "2.0.0");
        var first = await rig.Maintainer.RefreshAsync(Server, apply: false, regenerate: false, TestTimeout);
        Assert.AreEqual("no_changes", first.Status);
        Assert.IsNotNull(await rig.Maintainer.GetCurrentDraftAsync(Server, TestTimeout));

        await SetSourcesAsync(rig, new SkillSources { GuideTool = "get_guide", GuideResources = ["guide://overview"] });

        Assert.IsNull(await rig.Maintainer.GetCurrentDraftAsync(Server, TestTimeout), "The marker never saw the new source.");
        await rig.Maintainer.RefreshAsync(Server, apply: false, regenerate: false, TestTimeout);
        Assert.AreEqual(2, rig.Model.Requests.Count);
        StringAssert.Contains(rig.Model.Requests[1][1].Text, "Overview: start with list_accounts.");
    }

    // ---------------------------------------------------------------- guides

    [TestMethod]
    public async Task GuideToolAndResources_AreReadThroughTheServer()
    {
        await using var rig = await CreateAsync();
        await SetSourcesAsync(rig, new SkillSources
        {
            GuideTool = "get_guide",
            GuideArguments = [JsonSerializer.SerializeToElement(new { name = "calendar" }), JsonSerializer.SerializeToElement(new { name = "email" })],
            GuideResources = ["guide://overview"],
        });

        var material = await rig.Reader.ReadAsync(rig.Harness.Registry.Get(Server), null, TestTimeout);

        StringAssert.Contains(material.Guides, "Guide calendar: all-day events are floating dates");
        StringAssert.Contains(material.Guides, "Guide email");
        StringAssert.Contains(material.Guides, "Overview: start with list_accounts.");
        Assert.AreEqual(3, material.Reports.Count(r => r.Error is null));
        Assert.AreEqual(string.Empty, material.ChangeNotes);
    }

    [TestMethod]
    public async Task GuideToolWithoutArguments_IsCalledOnce()
    {
        await using var rig = await CreateAsync();
        await SetSourcesAsync(rig, new SkillSources { GuideTool = "get_guide" });

        var material = await rig.Reader.ReadAsync(rig.Harness.Registry.Get(Server), null, TestTimeout);

        StringAssert.Contains(material.Guides, "Guides: calendar, email");
    }

    // ---------------------------------------------------------------- revision

    [TestMethod]
    public async Task VersionOnlyChange_WithSources_GoesToTheModelWithTheMaterial()
    {
        // The csla case: nothing in the catalog changed, but the release notes might say something did.
        await using var rig = await CreateAsync();
        rig.Options.AllowedChangeNoteHosts.Add("docs.example.com");
        rig.Http.Responses["https://docs.example.com/notes.md"] = (HttpStatusCode.OK, "2.0: create_event now requires a time zone.");
        await SetSourcesAsync(rig, new SkillSources
        {
            ChangeNotes = "https://docs.example.com/notes.md",
            GuideTool = "get_guide",
            GuideArguments = [JsonSerializer.SerializeToElement(new { name = "calendar" })],
        });
        await BumpVersionAsync(rig, "2.0.0");

        var result = await rig.Maintainer.RefreshAsync(Server, apply: false, regenerate: false, TestTimeout);

        Assert.AreEqual(1, rig.Model.Requests.Count, $"{result.Status}: {result.Message}");
        Assert.AreEqual("no_changes", result.Status, "The fake model returns the document unchanged.");
        var user = rig.Model.Requests[0][1].Text;
        StringAssert.Contains(user, "<change_notes>");
        StringAssert.Contains(user, "requires a time zone");
        StringAssert.Contains(user, "<server_guides>");
        StringAssert.Contains(user, "all-day events are floating dates");
        StringAssert.Contains(user, "the tools, prompts and instructions are unchanged");
        StringAssert.Contains(rig.Model.Requests[0][0].Text, "<change_notes>");
        Assert.AreEqual(2, result.Sources.Count(s => s.Error is null));
    }

    [TestMethod]
    public async Task VersionOnlyChange_WithoutSources_IsStillRebaselinedWithoutTheModel()
    {
        await using var rig = await CreateAsync();
        await BumpVersionAsync(rig, "2.0.0");

        var result = await rig.Maintainer.RefreshAsync(Server, apply: false, regenerate: false, TestTimeout);

        Assert.AreEqual("version_only", result.Status);
        Assert.AreEqual(0, rig.Model.Requests.Count);
    }

    [TestMethod]
    public async Task Validation_TrustsGuideUrls_ButNotChangeNoteUrls()
    {
        await using var rig = await CreateAsync();
        rig.Options.AllowedChangeNoteHosts.Add("docs.example.com");
        rig.Http.Responses["https://docs.example.com/notes.md"] = (HttpStatusCode.OK, "See https://attacker.example/new for details.");
        await SetSourcesAsync(rig, new SkillSources
        {
            ChangeNotes = "https://docs.example.com/notes.md",
            GuideTool = "get_guide",
            GuideArguments = [JsonSerializer.SerializeToElement(new { name = "calendar" })],
        });
        await BumpVersionAsync(rig, "2.0.0");

        rig.Model.Respond = _ => Document + "\nCalendar guide: https://docs.example.com/calendar\n";
        var fromGuide = await rig.Maintainer.RefreshAsync(Server, apply: false, regenerate: true, TestTimeout);
        rig.Model.Respond = _ => Document + "\nDetails: https://attacker.example/new\n";
        var fromNotes = await rig.Maintainer.RefreshAsync(Server, apply: false, regenerate: true, TestTimeout);

        Assert.IsTrue(fromGuide.ValidationPassed, string.Join(" | ", fromGuide.Problems));
        Assert.IsFalse(fromNotes.ValidationPassed);
        StringAssert.Contains(fromNotes.Problems.Single(), "attacker.example");
    }

    [TestMethod]
    public async Task Upkeep_RevisesAVersionOnlyChangeWhenSourcesAreSet()
    {
        await using var rig = await CreateAsync();
        await SetSourcesAsync(rig, new SkillSources { GuideTool = "get_guide" });
        await BumpVersionAsync(rig, "2.0.0");
        var upkeep = new SkillMaintenanceHostedService(rig.Harness.Registry, rig.Harness.Index, rig.Maintainer,
            TestHelpers.OptionsOf(rig.Options), new SkillMaintenanceSignal(TestHelpers.OptionsOf(rig.Options)),
            TestHelpers.NullLoggerOf<SkillMaintenanceHostedService>());

        await upkeep.CheckServerAsync(Server, TestTimeout);

        Assert.AreEqual(1, rig.Model.Requests.Count, "With sources, a version-only change is worth a model call.");
        Assert.IsTrue((await rig.Skills.GetDraftAsync(Server, TestTimeout))!.NoChanges);
    }

    // ---------------------------------------------------------------- set_skill_sources

    [TestMethod]
    public async Task SetSkillSources_SavesKeepsAndClears()
    {
        await using var rig = await CreateAsync();

        var saved = await AdminTools.SetSkillSources(rig.Harness.Registry, rig.Reader, Server,
            guideTool: "get_guide", guideArguments: """[{"name":"calendar"}]""", ct: TestTimeout);
        var report = JsonDocument.Parse(saved).RootElement;
        Assert.AreEqual("get_guide", report.GetProperty("sources").GetProperty("guideTool").GetString());
        Assert.AreEqual(0, report.GetProperty("read").EnumerateArray().Count(r => r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String));

        await AdminTools.SetSkillSources(rig.Harness.Registry, rig.Reader, Server, guideResources: """["guide://overview"]""", ct: TestTimeout);
        var sources = rig.Harness.Registry.Get(Server).SkillSources!;
        Assert.AreEqual("get_guide", sources.GuideTool, "Omitted values are kept.");
        CollectionAssert.AreEqual(new[] { "guide://overview" }, sources.GuideResources);
        Assert.AreSame(sources, (await rig.Harness.Index.GetDetailsAsync(Server, TestTimeout)).SkillSources);

        var cleared = await AdminTools.SetSkillSources(rig.Harness.Registry, rig.Reader, Server,
            guideTool: "", guideArguments: "", guideResources: "", ct: TestTimeout);
        StringAssert.Contains(cleared, "Cleared");
        Assert.IsNull(rig.Harness.Registry.Get(Server).SkillSources);
    }

    [TestMethod]
    public async Task SetSkillSources_RefusesBadInputWithoutSaving()
    {
        await using var rig = await CreateAsync();

        StringAssert.Contains(await AdminTools.SetSkillSources(rig.Harness.Registry, rig.Reader, Server,
            changeNotes: "https://evil.example/notes", ct: TestTimeout), "Not saved");
        StringAssert.Contains(await AdminTools.SetSkillSources(rig.Harness.Registry, rig.Reader, Server,
            guideArguments: "[1, 2]", ct: TestTimeout), "Not saved");
        StringAssert.Contains(await AdminTools.SetSkillSources(rig.Harness.Registry, rig.Reader, Server,
            guideResources: "not json", ct: TestTimeout), "Not saved");
        Assert.IsNull(rig.Harness.Registry.Get(Server).SkillSources);
    }
}
