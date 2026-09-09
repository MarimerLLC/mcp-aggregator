using SysDescription = System.ComponentModel.DescriptionAttribute;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tests.Helpers;
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
}
