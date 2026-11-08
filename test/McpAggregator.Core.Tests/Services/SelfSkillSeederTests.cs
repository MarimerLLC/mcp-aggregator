using McpAggregator.Core.Services;
using McpAggregator.Core.Tests.Helpers;

namespace McpAggregator.Core.Tests.Services;

/// <summary>
/// The live copy of the aggregator's own skill document sits on a persistent volume that no deploy
/// step refreshes (issue #72). <see cref="SelfSkillSeeder"/> replaces it at startup only when it is
/// missing or an untouched earlier shipped version, as tracked by the marker every
/// <see cref="SkillStore"/> save keeps; an edited document is never overwritten.
/// </summary>
[TestClass]
public class SelfSkillSeederTests
{
    private const string SelfName = "mcp-aggregator";
    private const string Release1 = "# Aggregator\n\nRelease one.\n";
    private const string Release2 = "# Aggregator\n\nRelease two.\n";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-selfseed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    /// <summary>A store (and seeder) as a build shipping <paramref name="shipped"/> would see the shared skills directory.</summary>
    private (SkillStore Store, SelfSkillSeeder Seeder) Build(string? shipped)
    {
        var options = TestHelpers.OptionsOf(TestHelpers.DefaultAggregatorOptions(_dataDir));
        var store = new SkillStore(options, TestHelpers.NullLoggerOf<SkillStore>()) { ShippedSelfDocument = shipped };
        return (store, new SelfSkillSeeder(store, options, TestHelpers.NullLoggerOf<SelfSkillSeeder>()));
    }

    [TestMethod]
    public async Task NoDocument_WritesTheShippedOne()
    {
        var (store, seeder) = Build(Release1);

        Assert.AreEqual(SelfSkillSeedOutcome.Created, await seeder.SeedAsync(TestTimeout));

        Assert.AreEqual(Release1, await store.GetAsync(SelfName, TestTimeout));
        Assert.AreEqual(ShippedSelfSkill.Hash(Release1), await store.GetShippedMarkerAsync(TestTimeout));
    }

    [TestMethod]
    public async Task UntouchedEarlierRelease_IsUpgraded_AndKeptInHistory()
    {
        var (_, first) = Build(Release1);
        await first.SeedAsync(TestTimeout);

        var (store, second) = Build(Release2);
        Assert.AreEqual(SelfSkillSeedOutcome.Upgraded, await second.SeedAsync(TestTimeout));

        Assert.AreEqual(Release2, await store.GetAsync(SelfName, TestTimeout));
        Assert.AreEqual(ShippedSelfSkill.Hash(Release2), await store.GetShippedMarkerAsync(TestTimeout));
        var history = await store.ListHistoryAsync(SelfName, TestTimeout);
        Assert.AreEqual(1, history.Count);
        var (archived, _) = (await store.GetHistoryAsync(SelfName, history[0].Version, TestTimeout))!.Value;
        Assert.AreEqual(Release1, archived);
    }

    [TestMethod]
    public async Task EditedDocument_IsNeverOverwritten()
    {
        var (store1, first) = Build(Release1);
        await first.SeedAsync(TestTimeout);
        await store1.SetAsync(SelfName, Release1 + "\nA local note.\n", TestTimeout);
        Assert.IsNull(await store1.GetShippedMarkerAsync(TestTimeout), "A deliberate edit clears the marker.");

        var (store, second) = Build(Release2);
        Assert.AreEqual(SelfSkillSeedOutcome.Kept, await second.SeedAsync(TestTimeout));

        Assert.AreEqual(Release1 + "\nA local note.\n", await store.GetAsync(SelfName, TestTimeout));
    }

    [TestMethod]
    public async Task DocumentFromBeforeTheMarker_IsLeftAlone()
    {
        // Every deployment older than #72: a document on the volume and no marker. Whether it was edited
        // can't be told, so it stays until someone saves the shipped version with update_skill.
        Directory.CreateDirectory(Path.Combine(_dataDir, "skills"));
        var (store, seeder) = Build(Release2);
        File.WriteAllText(Path.Combine(TestHelpers.DefaultAggregatorOptions(_dataDir).SkillsDirectoryPath, $"{SelfName}.md"), Release1);

        Assert.AreEqual(SelfSkillSeedOutcome.Kept, await seeder.SeedAsync(TestTimeout));
        Assert.AreEqual(Release1, await store.GetAsync(SelfName, TestTimeout));

        // Saving the shipped version adopts it: the next release replaces it automatically.
        await store.SetAsync(SelfName, Release2, TestTimeout);
        var (next, nextSeeder) = Build("# Aggregator\n\nRelease three.\n");
        Assert.AreEqual(SelfSkillSeedOutcome.Upgraded, await nextSeeder.SeedAsync(TestTimeout));
        Assert.AreEqual("# Aggregator\n\nRelease three.\n", await next.GetAsync(SelfName, TestTimeout));
    }

    [TestMethod]
    public async Task CurrentDocument_DiffersOnlyInLineEndings_IsCurrent()
    {
        var (store, seeder) = Build(Release1);
        await store.SetAsync(SelfName, Release1.ReplaceLineEndings("\r\n"), TestTimeout);

        Assert.AreEqual(SelfSkillSeedOutcome.Current, await seeder.SeedAsync(TestTimeout));

        Assert.AreEqual(Release1.ReplaceLineEndings("\r\n"), await store.GetAsync(SelfName, TestTimeout), "Not rewritten.");
        Assert.AreEqual(0, (await store.ListHistoryAsync(SelfName, TestTimeout)).Count);
        Assert.AreEqual(ShippedSelfSkill.Hash(Release1), await store.GetShippedMarkerAsync(TestTimeout));
    }

    // An edit saved between the seeder's checks and its write (another host on the same data
    // directory, or update_skill arriving during startup) must survive (PR #73 review).

    [TestMethod]
    public async Task EditBetweenCheckAndUpgrade_IsNotOverwritten()
    {
        var (_, first) = Build(Release1);
        await first.SeedAsync(TestTimeout);

        var (store, second) = Build(Release2);
        second.BeforeWriteForTesting = () => store.SetAsync(SelfName, "# Edited meanwhile\n", TestTimeout);

        Assert.AreEqual(SelfSkillSeedOutcome.Kept, await second.SeedAsync(TestTimeout));
        Assert.AreEqual("# Edited meanwhile\n", await store.GetAsync(SelfName, TestTimeout));
    }

    [TestMethod]
    public async Task DocumentCreatedBetweenCheckAndCreate_IsNotOverwritten()
    {
        var (store, seeder) = Build(Release1);
        seeder.BeforeWriteForTesting = () => store.SetAsync(SelfName, "# Written meanwhile\n", TestTimeout);

        Assert.AreEqual(SelfSkillSeedOutcome.Kept, await seeder.SeedAsync(TestTimeout));
        Assert.AreEqual("# Written meanwhile\n", await store.GetAsync(SelfName, TestTimeout));
    }

    [TestMethod]
    public async Task EditBetweenCheckAndMarkerAdoption_IsNotOverwritten()
    {
        var (store, seeder) = Build(Release1);
        await store.SetAsync(SelfName, Release1.ReplaceLineEndings("\r\n"), TestTimeout);
        File.Delete(Path.Combine(TestHelpers.DefaultAggregatorOptions(_dataDir).SkillsDirectoryPath, $"{SelfName}.shipped.sha256"));
        seeder.BeforeWriteForTesting = () => store.SetAsync(SelfName, "# Edited meanwhile\n", TestTimeout);

        Assert.AreEqual(SelfSkillSeedOutcome.Kept, await seeder.SeedAsync(TestTimeout));
        Assert.AreEqual("# Edited meanwhile\n", await store.GetAsync(SelfName, TestTimeout));
        Assert.IsNull(await store.GetShippedMarkerAsync(TestTimeout), "The edit's save cleared the marker; nothing may set it back.");
    }

    [TestMethod]
    public async Task NothingShipped_TouchesNothing()
    {
        var (store, seeder) = Build(null);

        Assert.AreEqual(SelfSkillSeedOutcome.NotShipped, await seeder.SeedAsync(TestTimeout));
        Assert.IsNull(await store.GetAsync(SelfName, TestTimeout));
    }

    [TestMethod]
    public async Task OtherServersDocuments_NeverGetAMarker()
    {
        var (store, _) = Build("# Adjutant\n");

        await store.SetAsync("adjutant", "# Adjutant\n", TestTimeout);

        Assert.IsNull(await store.GetShippedMarkerAsync(TestTimeout));
    }

    [TestMethod]
    public void TheBuildShipsTheRepositoryDocument()
    {
        // Guards the EmbeddedResource path in McpAggregator.Core.csproj.
        var repoRoot = AppContext.BaseDirectory;
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot, "McpAggregator.slnx")))
            repoRoot = Path.GetDirectoryName(repoRoot);
        Assert.IsNotNull(repoRoot, "Could not find the repository root from the test output directory.");

        var onDisk = File.ReadAllText(Path.Combine(repoRoot, "data", "skills", "mcp-aggregator.md"));
        Assert.IsNotNull(ShippedSelfSkill.Text, "McpAggregator.Core must embed data/skills/mcp-aggregator.md.");
        Assert.AreEqual(ShippedSelfSkill.Hash(onDisk), ShippedSelfSkill.Hash(ShippedSelfSkill.Text));
    }
}
