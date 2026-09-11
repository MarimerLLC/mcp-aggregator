using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// Issue #44: the handshake <c>instructions</c> are a short hand-written orientation. The self
/// skill document is never embedded (it grew past the old 16 KB cap and went out truncated), never
/// restates what <c>tools/list</c> already carries, and is the same text on every options instance.
/// </summary>
[TestClass]
public class ServerInstructionsTests
{
    private const string Sentinel = "SENTINEL-THIS-LINE-MUST-NOT-REACH-THE-HANDSHAKE";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-instr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dataDir, "skills"));

        // A self skill well past the old embedding cap, with a sentinel the handshake must not carry.
        var body = string.Join("\n", Enumerable.Repeat("Filler paragraph for the aggregator skill document.", 600));
        File.WriteAllText(Path.Combine(_dataDir, "skills", "mcp-aggregator.md"), $"# guide\n\n{Sentinel}\n\n{body}\n");
        Assert.IsTrue(new FileInfo(Path.Combine(_dataDir, "skills", "mcp-aggregator.md")).Length > 30 * 1024);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["McpAggregator:DataDirectory"] = _dataDir })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddAggregatorCore(configuration);
        services.AddAggregatorMcpServer()
            .WithToolsFromAssembly(typeof(ConsumerTools).Assembly);
        return services.BuildServiceProvider();
    }

    private static string InstructionsOf(ServiceProvider provider)
    {
        var instructions = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ServerInstructions;
        Assert.IsFalse(string.IsNullOrWhiteSpace(instructions));
        return instructions!;
    }

    [TestMethod]
    public void Instructions_StayUnderTheCeiling_EvenWithALargeSelfSkill()
    {
        using var provider = BuildProvider();

        var instructions = InstructionsOf(provider);

        Assert.IsTrue(instructions.Length <= AggregatorInstructions.MaxChars,
            $"Instructions are {instructions.Length} chars; ceiling is {AggregatorInstructions.MaxChars}.");
        Assert.IsFalse(instructions.Contains("truncated", StringComparison.OrdinalIgnoreCase), "No truncation marker: nothing is embedded to truncate.");
        Assert.IsFalse(instructions.Contains(Sentinel, StringComparison.Ordinal), "The self skill document must not be embedded in the handshake.");
        Assert.AreEqual(6 * 1024, AggregatorInstructions.MaxChars, "The ceiling is a design decision (issue #44); change it deliberately.");
    }

    [TestMethod]
    public void Instructions_PointToTheSkillDocument()
    {
        using var provider = BuildProvider();

        var instructions = InstructionsOf(provider);

        StringAssert.Contains(instructions, "get_service_skill(serverName: \"mcp-aggregator\")");
        StringAssert.Contains(instructions, "find_tools");
        StringAssert.Contains(instructions, "invoke_tool");
        StringAssert.Contains(instructions, "show_admin_tools");
    }

    [TestMethod]
    public void Instructions_DoNotRestateToolDescriptions()
    {
        using var provider = BuildProvider();

        var instructions = InstructionsOf(provider);
        var tools = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!
            .Concat(provider.GetRequiredService<AdminToolSet>().Tools)
            .ToList();

        Assert.IsTrue(tools.Count >= 9, "The attributed consumer tools and the admin tools must both be under test.");
        foreach (var tool in tools)
        {
            var description = tool.ProtocolTool.Description;
            Assert.IsFalse(string.IsNullOrWhiteSpace(description), $"{tool.ProtocolTool.Name} has no description.");
            Assert.IsFalse(instructions.Contains(description!, StringComparison.Ordinal),
                $"The instructions restate the description of '{tool.ProtocolTool.Name}'; tools/list already carries it.");
        }
    }

    [TestMethod]
    public void EveryOptionsInstance_CarriesTheSameInstructions()
    {
        using var provider = BuildProvider();

        var singleton = InstructionsOf(provider);
        var factory = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>();
        var perRequest1 = factory.Create(Options.DefaultName).ServerInstructions;
        var perRequest2 = factory.Create(Options.DefaultName).ServerInstructions;

        Assert.AreEqual(singleton, perRequest1, "Stateless HTTP builds fresh options per request; the handshake must not differ.");
        Assert.AreEqual(singleton, perRequest2);
    }
}
