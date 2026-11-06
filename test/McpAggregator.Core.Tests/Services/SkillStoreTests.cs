using McpAggregator.Core.Configuration;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Services;
using McpAggregator.Core.Tests.Helpers;

namespace McpAggregator.Core.Tests.Services;

[TestClass]
public class SkillStoreTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private SkillStore CreateStore()
    {
        var options = new AggregatorOptions
        {
            DataDirectory = _tempDir,
            SkillsDirectory = "skills"
        };
        return new SkillStore(
            TestHelpers.OptionsOf(options),
            TestHelpers.NullLoggerOf<SkillStore>());
    }

    // --- GetAsync ---

    [TestMethod]
    public async Task GetAsync_ReturnsNull_WhenMissing()
    {
        var store = CreateStore();

        var result = await store.GetAsync("nonexistent");

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetAsync_ReturnsContent_WhenExists()
    {
        var store = CreateStore();
        await store.SetAsync("my-server", "# My Skill\nSome content");

        var result = await store.GetAsync("my-server");

        Assert.AreEqual("# My Skill\nSome content", result);
    }

    // --- SetAsync ---

    [TestMethod]
    public async Task SetAsync_WritesMarkdownFile()
    {
        var store = CreateStore();

        await store.SetAsync("my-server", "# Skill Doc");

        var path = Path.Combine(_tempDir, "skills", "my-server.md");
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual("# Skill Doc", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task SetAsync_OverwritesExisting()
    {
        var store = CreateStore();
        await store.SetAsync("srv", "v1");

        await store.SetAsync("srv", "v2");

        Assert.AreEqual("v2", await store.GetAsync("srv"));
    }

    [TestMethod]
    public async Task SetAsync_ThrowsAggregatorException_WhenTooLarge()
    {
        var store = CreateStore();
        var largeContent = new string('x', 256 * 1024 + 1);

        await Assert.ThrowsExceptionAsync<AggregatorException>(
            () => store.SetAsync("srv", largeContent));
    }

    [TestMethod]
    public async Task SetAsync_ExactlyAtLimit_Succeeds()
    {
        var store = CreateStore();
        var content = new string('x', 256 * 1024);

        await store.SetAsync("srv", content);

        Assert.AreEqual(content, await store.GetAsync("srv"));
    }

    // --- Delete ---

    [TestMethod]
    public async Task Delete_ReturnsTrue_WhenExists()
    {
        var store = CreateStore();
        await store.SetAsync("srv", "content");

        Assert.IsTrue(store.Delete("srv"));
        Assert.IsNull(await store.GetAsync("srv"));
    }

    [TestMethod]
    public void Delete_ReturnsFalse_WhenMissing()
    {
        var store = CreateStore();

        Assert.IsFalse(store.Delete("nonexistent"));
    }

    // --- Exists ---

    [TestMethod]
    public async Task Exists_ReturnsTrue_WhenPresent()
    {
        var store = CreateStore();
        await store.SetAsync("srv", "content");

        Assert.IsTrue(store.Exists("srv"));
    }

    [TestMethod]
    public void Exists_ReturnsFalse_WhenMissing()
    {
        var store = CreateStore();

        Assert.IsFalse(store.Exists("nonexistent"));
    }

    // --- Baselines (issue #56) ---

    [TestMethod]
    public async Task SetBaselineAsync_ConcurrentWritesForOneServer_AllSucceedAndLeaveNoTempFiles()
    {
        // PR #60 review: two saves of the same skill must not collide on a shared temp file.
        var store = CreateStore();
        var writes = Enumerable.Range(0, 20).Select(i => Task.Run(() => store.SetBaselineAsync("svc",
            new McpAggregator.Core.Models.SkillBaseline { RemoteVersion = $"1.0.{i}", RecordedAt = DateTimeOffset.UtcNow })));

        await Task.WhenAll(writes);

        var baseline = await store.GetBaselineAsync("svc");
        Assert.IsNotNull(baseline);
        StringAssert.StartsWith(baseline.RemoteVersion, "1.0.");
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(_tempDir, "skills"), "*.tmp").Length);
    }

    [TestMethod]
    public async Task GetBaselineAsync_CorruptFile_ReadsAsAbsent()
    {
        var store = CreateStore();
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "skills", "svc.baseline.json"), "{ not json");

        Assert.IsNull(await store.GetBaselineAsync("svc"));
    }

    [TestMethod]
    public async Task Delete_RemovesTheBaselineToo()
    {
        var store = CreateStore();
        await store.SetAsync("svc", "# Skill");
        await store.SetBaselineAsync("svc", new McpAggregator.Core.Models.SkillBaseline());

        Assert.IsTrue(store.Delete("svc"));

        Assert.IsNull(await store.GetBaselineAsync("svc"));
    }

    // --- History and drafts (issue #57) ---

    private SkillStore CreateStore(int historyLimit) => new(
        TestHelpers.OptionsOf(new AggregatorOptions { DataDirectory = _tempDir, SkillsDirectory = "skills", SkillHistoryLimit = historyLimit }),
        TestHelpers.NullLoggerOf<SkillStore>());

    [TestMethod]
    public async Task SetAsync_KeepsReplacedVersionsWithTheirBaseline()
    {
        var store = CreateStore();
        await store.SetAsync("svc", "v1");
        await store.SetBaselineAsync("svc", new McpAggregator.Core.Models.SkillBaseline { RemoteVersion = "1.0" });
        await store.SetAsync("svc", "v2");

        var entry = (await store.ListHistoryAsync("svc")).Single();
        var (markdown, baseline) = (await store.GetHistoryAsync("svc", entry.Version))!.Value;

        Assert.AreEqual("v1", markdown);
        Assert.AreEqual("1.0", baseline?.RemoteVersion);
        Assert.AreEqual("1.0", entry.RecordedVersion);
        Assert.IsNotNull(entry.ReplacedAt);
    }

    [TestMethod]
    public async Task SetAsync_IdenticalText_KeepsNoHistory()
    {
        var store = CreateStore();
        await store.SetAsync("svc", "same");
        await store.SetAsync("svc", "same");

        Assert.AreEqual(0, (await store.ListHistoryAsync("svc")).Count);
    }

    [TestMethod]
    public async Task SetAsync_TrimsHistoryToTheLimit()
    {
        var store = CreateStore(historyLimit: 3);
        for (var i = 1; i <= 6; i++)
            await store.SetAsync("svc", $"v{i}");

        var history = await store.ListHistoryAsync("svc");

        Assert.AreEqual(3, history.Count);
        Assert.AreEqual("v5", (await store.GetHistoryAsync("svc", history[0].Version))!.Value.Markdown, "Newest first.");
    }

    [TestMethod]
    public async Task Delete_RemovesHistoryAndDraft()
    {
        var store = CreateStore();
        await store.SetAsync("svc", "v1");
        await store.SetAsync("svc", "v2");
        await store.SetDraftAsync("svc", new McpAggregator.Core.Models.SkillDraft { Markdown = "d", BasedOnSha256 = "x" });

        store.Delete("svc");

        Assert.AreEqual(0, (await store.ListHistoryAsync("svc")).Count);
        Assert.IsNull(await store.GetDraftAsync("svc"));
    }
}
