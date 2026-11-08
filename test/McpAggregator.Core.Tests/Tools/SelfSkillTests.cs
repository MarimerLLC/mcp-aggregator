using SysDescription = System.ComponentModel.DescriptionAttribute;
using System.Text.Json;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Services;
using McpAggregator.Core.Tests.Helpers;
using McpAggregator.Core.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// The aggregator's own skill document (issue #72): <c>get_service_skill(serverName: "mcp-aggregator")</c>
/// serves it, so <c>update_skill</c>, <c>list_skill_history</c> and <c>restore_skill</c> must accept that
/// name too. Before, all three called <c>registry.Get</c>, the aggregator is not in the registry, and the
/// caller got the SDK's bare "An error occurred invoking 'update_skill'."
/// </summary>
[TestClass]
public class SelfSkillTests
{
    private const string Server = "adjutant";
    private const string SelfName = "mcp-aggregator";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-selfskill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [SysDescription("List accounts.")]
    private static string ListAccounts() => "[]";

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private SkillStore Skills() => new(
        TestHelpers.OptionsOf(TestHelpers.DefaultAggregatorOptions(_dataDir)), TestHelpers.NullLoggerOf<SkillStore>());

    private Task<WrapperHarness> CreateAsync()
        => WrapperHarness.CreateAsync(_dataDir, WrapperToolMode.Lazy,
            (Server, [McpServerTool.Create(ListAccounts, new McpServerToolCreateOptions { Name = "list_accounts" })]));

    [TestMethod]
    [DataRow("mcp-aggregator")]
    [DataRow("MCP-Aggregator")]
    public async Task UpdateSkill_AggregatorItself_SavesTheDocument(string name)
    {
        await using var harness = await CreateAsync();
        var skills = Skills();

        var message = await AdminTools.UpdateSkill(harness.Registry, skills, harness.Index, name, "# Aggregator\n\nUse find_tools.\n", TestTimeout);

        StringAssert.Contains(message, $"Skill document updated for '{SelfName}'");
        StringAssert.Contains(message, "the aggregator itself");
        Assert.AreEqual("# Aggregator\n\nUse find_tools.\n", await skills.GetAsync(SelfName, TestTimeout));
        Assert.IsTrue(harness.Registry.GetAll().All(s => s.Name != SelfName), "The aggregator must not be added to the registry.");
    }

    [TestMethod]
    public async Task SkillHistory_AggregatorItself_ListsAndRestoresReplacedVersions()
    {
        await using var harness = await CreateAsync();
        var skills = Skills();
        await AdminTools.UpdateSkill(harness.Registry, skills, harness.Index, SelfName, "# Version one\n", TestTimeout);
        await AdminTools.UpdateSkill(harness.Registry, skills, harness.Index, SelfName, "# Version two\n", TestTimeout);

        var historyJson = await AdminTools.ListSkillHistory(harness.Registry, skills, SelfName, TestTimeout);
        using var history = JsonDocument.Parse(historyJson);
        Assert.AreEqual(1, history.RootElement.GetArrayLength(), historyJson);
        var version = history.RootElement[0].GetProperty("version").GetString()!;

        var message = await AdminTools.RestoreSkill(harness.Registry, skills, harness.Index, SelfName, version, TestTimeout);

        StringAssert.Contains(message, $"Restored version '{version}'");
        Assert.AreEqual("# Version one\n", await skills.GetAsync(SelfName, TestTimeout));
    }

    [TestMethod]
    public async Task UpdateSkill_UnknownServer_NamesTheRegisteredServers()
    {
        await using var harness = await CreateAsync();

        var ex = await Assert.ThrowsAsync<ServerNotFoundException>(async () =>
            await AdminTools.UpdateSkill(harness.Registry, Skills(), harness.Index, "nope", "# x\n", TestTimeout));

        StringAssert.Contains(ex.Message, "Server 'nope' not found.");
        StringAssert.Contains(ex.Message, $"Registered servers: [{Server}]");
        Assert.IsInstanceOfType<McpException>(ex,
            "The SDK shows the message only for an McpException; anything else becomes the bare 'An error occurred invoking'.");
    }
}
