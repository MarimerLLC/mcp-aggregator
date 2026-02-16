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
}
