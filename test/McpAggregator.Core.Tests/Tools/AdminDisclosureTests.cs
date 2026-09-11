using SysDescription = System.ComponentModel.DescriptionAttribute;
using System.Reflection;
using System.Text.Json;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using McpAggregator.Core.Storage;
using McpAggregator.Core.Tests.Helpers;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rocks;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// Progressive disclosure for the aggregator's own surface (issue #39 follow-on): in Lazy mode the
/// administrative tools stay out of every session's initial <c>tools/list</c> until that session
/// asks for them with <c>show_admin_tools</c> or calls one by name.
/// </summary>
[TestClass]
public class AdminDisclosureTests
{
    private const string Downstream = "probe";

    // Written into every rig's self skill: the handshake must not carry it, get_service_skill must.
    private const string SkillSentinel = "SENTINEL-FULL-SKILL-ONLY-BEHIND-GET-SERVICE-SKILL";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-admin-" + Guid.NewGuid().ToString("N"));
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
    private static CallToolResult Echo([SysDescription("Message to echo")] string message)
        => new() { IsError = false, Content = [new TextContentBlock { Text = "echo:" + message }] };

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static string TextOf(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private sealed record Rig(ServiceProvider Provider, WrapperToolCatalog Catalog, List<InMemoryMcpServer> Downstreams, InMemoryMcpServer Aggregator, McpClient Client, TaskCompletionSource ListChanged)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Aggregator.DisposeAsync();
            await Provider.GetRequiredService<ConnectionManager>().DisposeAsync();
            List<InMemoryMcpServer> all;
            lock (Downstreams) all = [.. Downstreams];
            foreach (var d in all) await d.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }

    private async Task<Rig> BuildAsync(WrapperToolMode mode)
    {
        // A self skill well past the old 16 KB embedding cap (issue #44). Tests that need a
        // specific document write their own before calling BuildAsync.
        var skillPath = Path.Combine(_dataDir, "skills", "mcp-aggregator.md");
        if (!File.Exists(skillPath))
        {
            Directory.CreateDirectory(Path.Combine(_dataDir, "skills"));
            var body = string.Join("\n", Enumerable.Repeat("Filler paragraph for the aggregator skill document.", 600));
            await File.WriteAllTextAsync(skillPath, $"# guide\n\n{SkillSentinel}\n\n{body}\n");
        }

        var server = TestHelpers.StdioServer(Downstream);
        var expectations = new IRegistryPersistenceCreateExpectations();
        expectations.Setups.LoadAsync(Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new RegistryData { Servers = [server] }));
        expectations.Setups.SaveAsync(Arg.Any<RegistryData>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpAggregator:DataDirectory"] = _dataDir,
                ["McpAggregator:WrapperMode"] = mode.ToString(),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddAggregatorCore(configuration);
        services.AddSingleton<IRegistryPersistence>(expectations.Instance());
        services.AddAggregatorMcpServer().WithToolsFromAssembly(typeof(ConsumerTools).Assembly);
        var provider = services.BuildServiceProvider();

        var downstreams = new List<InMemoryMcpServer>();
        provider.GetRequiredService<ConnectionManager>().TransportFactoryOverride = _ =>
        {
            var d = new InMemoryMcpServer(Downstream, McpServerTool.Create(Echo, new McpServerToolCreateOptions { Name = "echo" }));
            lock (downstreams) downstreams.Add(d);
            return d.ClientTransport;
        };

        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var catalog = provider.GetRequiredService<WrapperToolCatalog>();
        var aggregator = InMemoryMcpServer.Host("aggregator", options, provider);
        var listChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = await aggregator.CreateClientAsync(new McpClientOptions
        {
            ProtocolVersion = "2025-06-18",
            Handlers = new McpClientHandlers
            {
                NotificationHandlers =
                [
                    new KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>(
                        NotificationMethods.ToolListChangedNotification,
                        (_, _) => { listChanged.TrySetResult(); return ValueTask.CompletedTask; })
                ]
            }
        }, TestTimeout);
        return new Rig(provider, catalog, downstreams, aggregator, client, listChanged);
    }

    private static async Task<HashSet<string>> ToolNamesAsync(McpClient client)
        => (await client.ListToolsAsync(cancellationToken: TestTimeout)).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    // ---------------------------------------------------------------- the name set stays honest

    [TestMethod]
    public void AdminToolNames_MatchTheToolsDeclaredOnAdminTools()
    {
        var declared = typeof(AdminTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

        CollectionAssert.AreEquivalent(declared.ToList(), AdminTools.ToolNames.ToList(),
            "AdminTools.ToolNames must list exactly the tools declared on AdminTools; it is what Lazy mode hides.");
    }

    [TestMethod]
    public async Task AdminTools_ThatReferToAServer_AllTakeServerName()
    {
        // register_server mints a new name, so it takes 'name'; every other admin tool refers to
        // an existing server and takes 'serverName', like the consumer tools do.
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);

        foreach (var tool in rig.Provider.GetRequiredService<AdminToolSet>().Tools)
        {
            var expected = tool.ProtocolTool.Name == "register_server" ? "name" : "serverName";
            var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
            Assert.IsTrue(properties.TryGetProperty(expected, out _),
                $"{tool.ProtocolTool.Name} should take '{expected}'; it has [{string.Join(", ", properties.EnumerateObject().Select(p => p.Name))}].");
        }
    }

    // ---------------------------------------------------------------- lazy

    [TestMethod]
    public async Task Lazy_InitialToolList_HasNoAdminTools()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);

        var names = await ToolNamesAsync(rig.Client);

        Assert.IsFalse(names.Overlaps(AdminTools.ToolNames), $"Admin tools leaked into the initial list: {string.Join(", ", names.Intersect(AdminTools.ToolNames))}");
        Assert.IsTrue(names.IsSupersetOf(["find_tools", "list_services", "get_service_details", "get_service_skill", "invoke_tool", "get_prompt", "read_resource", "refresh_service", "show_admin_tools"]));
        Assert.AreEqual(9, names.Count, $"Initial surface: {string.Join(", ", names.Order())}");
    }

    [TestMethod]
    public async Task Lazy_ShowAdminTools_DisclosesThemToThisSession_AndNotifies()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);

        var result = await rig.Client.CallToolAsync("show_admin_tools", cancellationToken: TestTimeout);
        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        using var doc = JsonDocument.Parse(TextOf(result));
        var listed = doc.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToHashSet();
        CollectionAssert.AreEquivalent(AdminTools.ToolNames.ToList(), listed.ToList());

        await rig.ListChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var names = await ToolNamesAsync(rig.Client);
        Assert.IsTrue(names.IsSupersetOf(AdminTools.ToolNames));
    }

    [TestMethod]
    public async Task Lazy_AdminToolCalledByName_WorksAndIsThenListed()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        Assert.IsFalse((await ToolNamesAsync(rig.Client)).Contains("disable_service"));

        var result = await rig.Client.CallToolAsync("disable_service",
            new Dictionary<string, object?> { ["serverName"] = Downstream }, cancellationToken: TestTimeout);

        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        StringAssert.Contains(TextOf(result), "disabled");
        Assert.IsFalse(rig.Provider.GetRequiredService<ServerRegistry>().Get(Downstream).Enabled);

        await rig.ListChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue((await ToolNamesAsync(rig.Client)).Contains("disable_service"));
    }

    [TestMethod]
    public async Task Lazy_AdminDisclosure_IsPerSession()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        var optionsB = rig.Provider.GetRequiredService<IOptionsFactory<McpServerOptions>>().Create(Options.DefaultName);
        await using var sessionB = InMemoryMcpServer.Host("aggregator-b", optionsB, rig.Provider);
        var clientB = await sessionB.CreateClientAsync(new McpClientOptions { ProtocolVersion = "2025-06-18" }, TestTimeout);

        await rig.Client.CallToolAsync("show_admin_tools", cancellationToken: TestTimeout);
        await rig.ListChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue((await ToolNamesAsync(rig.Client)).IsSupersetOf(AdminTools.ToolNames));
        Assert.IsFalse((await ToolNamesAsync(clientB)).Overlaps(AdminTools.ToolNames), "B never asked; B's list must not grow.");
    }

    [TestMethod]
    public async Task Lazy_SharedCollection_HoldsNoAdminTools()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        await rig.Client.CallToolAsync("show_admin_tools", cancellationToken: TestTimeout);

        var shared = rig.Provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        Assert.IsFalse(shared.Any(t => AdminTools.ToolNames.Contains(t.ProtocolTool.Name)));

        // A fresh per-request options instance (stateless HTTP) must not resurrect them either.
        var perRequest = rig.Provider.GetRequiredService<IOptionsFactory<McpServerOptions>>().Create(Options.DefaultName);
        Assert.IsFalse(perRequest.ToolCollection!.Any(t => AdminTools.ToolNames.Contains(t.ProtocolTool.Name)));
    }

    [TestMethod]
    public async Task Lazy_UnknownToolHint_MentionsHiddenAdminTools()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);

        var result = await rig.Client.CallToolAsync("no_such_thing", cancellationToken: TestTimeout);

        Assert.IsTrue(result.IsError ?? false);
        StringAssert.Contains(TextOf(result), "hidden until show_admin_tools");
    }

    // ---------------------------------------------------------------- the handshake (issue #44)

    [TestMethod]
    public async Task Lazy_Handshake_IsShort_AndFullSkillStaysBehindGetServiceSkill()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);
        Assert.IsTrue(new FileInfo(Path.Combine(_dataDir, "skills", "mcp-aggregator.md")).Length > 30 * 1024);

        var instructions = rig.Client.ServerInstructions;
        Assert.IsFalse(string.IsNullOrWhiteSpace(instructions));
        Assert.IsTrue(instructions!.Length <= AggregatorInstructions.MaxChars, $"Handshake carried {instructions.Length} chars.");
        Assert.IsFalse(instructions.Contains(SkillSentinel, StringComparison.Ordinal), "The self skill must not be embedded in the handshake.");
        Assert.IsFalse(instructions.Contains("truncated", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(instructions, "get_service_skill(serverName: \"mcp-aggregator\")");

        var skill = await rig.Client.CallToolAsync("get_service_skill",
            new Dictionary<string, object?> { ["serverName"] = "mcp-aggregator" }, cancellationToken: TestTimeout);
        Assert.IsFalse(skill.IsError ?? false, TextOf(skill));
        StringAssert.Contains(TextOf(skill), SkillSentinel, "get_service_skill returns the full document.");
        Assert.IsTrue(TextOf(skill).Length > 30 * 1024, "The document must come back whole, not capped.");

        var services = await rig.Client.CallToolAsync("list_services", cancellationToken: TestTimeout);
        Assert.IsFalse(services.IsError ?? false, TextOf(services));
        using var doc = JsonDocument.Parse(TextOf(services));
        Assert.IsTrue(doc.RootElement.EnumerateArray().Any(e => e.GetProperty("name").GetString() == "mcp-aggregator"), "The self entry is still listed.");
    }

    // ---------------------------------------------------------------- the aggregator's own entry

    [TestMethod]
    public async Task ListServices_SelfEntry_DescribesTheAggregatorsOwnTools()
    {
        // Seen on Claude Desktop: the self entry had id null and no tools, so it read as a broken
        // downstream and the model tried invoke_tool(serverName: "mcp-aggregator", ...).
        Directory.CreateDirectory(Path.Combine(_dataDir, "skills"));
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "skills", "mcp-aggregator.md"), "# guide");
        await using var rig = await BuildAsync(WrapperToolMode.Lazy);

        var result = await rig.Client.CallToolAsync("list_services", cancellationToken: TestTimeout);
        Assert.IsFalse(result.IsError ?? false, TextOf(result));

        using var doc = JsonDocument.Parse(TextOf(result));
        var self = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "mcp-aggregator");
        Assert.AreEqual(ToolIndex.SelfId, self.GetProperty("id").GetString());
        StringAssert.Contains(self.GetProperty("description").GetString(), "never through invoke_tool");

        var tools = self.GetProperty("tools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("wrapperName").GetString());
        Assert.IsTrue(tools.ContainsKey("find_tools"), "consumer tools come from the shared collection");
        Assert.IsTrue(tools.ContainsKey("register_server"), "admin tools are described even though Lazy hides them");
        Assert.IsFalse(tools.Keys.Any(k => k.Contains("__")), "no downstream wrappers in the self entry");
        Assert.IsTrue(tools.All(kv => kv.Key == kv.Value), "own tools are called by their own name");
        Assert.IsTrue(tools.ContainsKey("get_prompt") && tools.ContainsKey("show_admin_tools"));

        var details = await rig.Client.CallToolAsync("get_service_details",
            new Dictionary<string, object?> { ["serverName"] = "mcp-aggregator" }, cancellationToken: TestTimeout);
        Assert.IsFalse(details.IsError ?? false, TextOf(details));
        using var detailDoc = JsonDocument.Parse(TextOf(details));
        Assert.AreEqual(ToolIndex.SelfId, detailDoc.RootElement.GetProperty("id").GetString());
        Assert.AreEqual(rig.Client.ServerInstructions, detailDoc.RootElement.GetProperty("remoteInstructions").GetString(),
            "The self entry's remoteInstructions is what this server says on connect, like any downstream's.");
        Assert.IsTrue(detailDoc.RootElement.GetProperty("tools").EnumerateArray().Any(t => t.GetProperty("name").GetString() == "update_skill"));

        // Admin entries used to come from reflection with no schema (inputSchema: null on Claude
        // Desktop); they now come from AdminToolSet like the consumer tools do.
        foreach (var tool in detailDoc.RootElement.GetProperty("tools").EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString();
            Assert.IsTrue(tool.TryGetProperty("inputSchema", out var schema) && schema.ValueKind == JsonValueKind.Object,
                $"'{name}' has no inputSchema in the self entry's details.");
            if (name == "register_server")
                Assert.IsTrue(schema.GetProperty("properties").TryGetProperty("name", out _), "register_server's schema must carry its parameters.");
        }
    }

    // ---------------------------------------------------------------- eager

    [TestMethod]
    public async Task Eager_AdminTools_AreListedFromTheStart()
    {
        await using var rig = await BuildAsync(WrapperToolMode.Eager);

        var names = await ToolNamesAsync(rig.Client);

        Assert.IsTrue(names.IsSupersetOf(AdminTools.ToolNames));
        Assert.AreEqual(AdminTools.ToolNames.Count, rig.Catalog.AdminTools.Count);
    }
}
