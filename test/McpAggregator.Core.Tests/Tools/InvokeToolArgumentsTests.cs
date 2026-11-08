using SysDescription = System.ComponentModel.DescriptionAttribute;
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
/// <c>invoke_tool</c> and <c>get_prompt</c> take <c>arguments</c> as the argument object or as a JSON
/// string of it (issue #70). Before, the parameter was a string, so sending the object (the natural
/// reading of "argument object") failed in SDK binding with the bare "An error occurred invoking
/// '...'", before the tool's own helpful message could run. Driven over a real MCP round-trip.
/// </summary>
[TestClass]
public class InvokeToolArgumentsTests
{
    private const string Downstream = "probe";
    private const string SdkGenericError = "An error occurred invoking";

    private string _dataDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-args-" + Guid.NewGuid().ToString("N"));
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
    private static CallToolResult Echo([SysDescription("Message to echo")] string message, [SysDescription("Repeat count")] int times = 1)
        => new() { IsError = false, Content = [new TextContentBlock { Text = "echo:" + string.Concat(Enumerable.Repeat(message, times)) }] };

    [SysDescription("Answers pong.")]
    private static CallToolResult Ping()
        => new() { IsError = false, Content = [new TextContentBlock { Text = "pong" }] };

    [SysDescription("Summarizes a document.")]
    private static string Summarize([SysDescription("Text to summarize")] string text) => "Summarize: " + text;

    [SysDescription("Says hello.")]
    private static string Hello() => "Hello";

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static string TextOf(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static string PromptTextOf(CallToolResult result)
    {
        using var doc = JsonDocument.Parse(TextOf(result));
        return doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString()!;
    }

    private sealed record Rig(ServiceProvider Provider, List<InMemoryMcpServer> Downstreams, InMemoryMcpServer Aggregator, McpClient Client)
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

    private async Task<Rig> BuildAsync()
    {
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
                ["McpAggregator:WrapperMode"] = nameof(WrapperToolMode.Lazy),
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
            var d = new InMemoryMcpServer(Downstream,
                [
                    McpServerPrompt.Create(Summarize, new McpServerPromptCreateOptions { Name = "summarize" }),
                    McpServerPrompt.Create(Hello, new McpServerPromptCreateOptions { Name = "hello" }),
                ],
                McpServerTool.Create(Echo, new McpServerToolCreateOptions { Name = "echo" }),
                McpServerTool.Create(Ping, new McpServerToolCreateOptions { Name = "ping" }));
            lock (downstreams) downstreams.Add(d);
            return d.ClientTransport;
        };

        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var aggregator = InMemoryMcpServer.Host("aggregator", options, provider);
        var client = await aggregator.CreateClientAsync(new McpClientOptions { ProtocolVersion = "2025-06-18" }, TestTimeout);
        return new Rig(provider, downstreams, aggregator, client);
    }

    private static Task<CallToolResult> InvokeAsync(Rig rig, string tool, object? arguments, bool omit = false)
    {
        var args = new Dictionary<string, object?> { ["serverName"] = Downstream, ["toolName"] = tool };
        if (!omit)
            args["arguments"] = arguments;
        return rig.Client.CallToolAsync("invoke_tool", args, cancellationToken: TestTimeout).AsTask();
    }

    private static Task<CallToolResult> GetPromptAsync(Rig rig, string prompt, object? arguments, bool omit = false)
    {
        var args = new Dictionary<string, object?> { ["serverName"] = Downstream, ["promptName"] = prompt };
        if (!omit)
            args["arguments"] = arguments;
        return rig.Client.CallToolAsync("get_prompt", args, cancellationToken: TestTimeout).AsTask();
    }

    // ------------------------------------------------------------------ invoke_tool: accepted forms

    [TestMethod]
    public async Task InvokeTool_ArgumentsAsObject_ReachesTheDownstreamTool()
    {
        await using var rig = await BuildAsync();

        var result = await InvokeAsync(rig, "echo", new Dictionary<string, object?> { ["message"] = "hi", ["times"] = 2 });

        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        Assert.AreEqual("echo:hihi", TextOf(result));
    }

    [TestMethod]
    public async Task InvokeTool_ArgumentsAsJsonString_ReachesTheDownstreamTool()
    {
        await using var rig = await BuildAsync();

        var result = await InvokeAsync(rig, "echo", """{"message":"hi","times":2}""");

        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        Assert.AreEqual("echo:hihi", TextOf(result));
    }

    [TestMethod]
    public async Task InvokeTool_ArgumentsNull_CallsWithNoArguments()
    {
        await using var rig = await BuildAsync();

        var result = await InvokeAsync(rig, "ping", null);

        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        Assert.AreEqual("pong", TextOf(result));
    }

    [TestMethod]
    public async Task InvokeTool_ArgumentsOmitted_CallsWithNoArguments()
    {
        await using var rig = await BuildAsync();

        var result = await InvokeAsync(rig, "ping", null, omit: true);

        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        Assert.AreEqual("pong", TextOf(result));
    }

    [TestMethod]
    public async Task InvokeTool_ArgumentsEmptyString_CallsWithNoArguments()
    {
        await using var rig = await BuildAsync();

        var result = await InvokeAsync(rig, "ping", "  ");

        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        Assert.AreEqual("pong", TextOf(result));
    }

    // ------------------------------------------------------------------ invoke_tool: rejected forms

    [TestMethod]
    [DataRow("array", "[1]", DisplayName = "array")]
    [DataRow("number", "5", DisplayName = "number")]
    [DataRow("boolean", "true", DisplayName = "boolean")]
    public async Task InvokeTool_ArgumentsOfWrongJsonKind_NamesWhatWasSent(string kind, string json)
    {
        await using var rig = await BuildAsync();

        using var doc = JsonDocument.Parse(json);
        var result = await InvokeAsync(rig, "echo", doc.RootElement.Clone());

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        Assert.IsFalse(text.Contains(SdkGenericError), text);
        StringAssert.Contains(text, $"You sent a JSON {kind}: {json}");
        StringAssert.Contains(text, """{"query": "text"}""");
    }

    [TestMethod]
    public async Task InvokeTool_StringHoldingANonObject_NamesWhatWasSent()
    {
        await using var rig = await BuildAsync();

        var result = await InvokeAsync(rig, "echo", "[1]");

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        Assert.IsFalse(text.Contains(SdkGenericError), text);
        StringAssert.Contains(text, "a JSON string holding a JSON array");
        StringAssert.Contains(text, "You sent: [1]");
    }

    [TestMethod]
    public async Task InvokeTool_StringThatIsNotJson_ReportsTheParseError()
    {
        await using var rig = await BuildAsync();

        var result = await InvokeAsync(rig, "echo", "message=hi");

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        Assert.IsFalse(text.Contains(SdkGenericError), text);
        StringAssert.Contains(text, "is not valid JSON");
        StringAssert.Contains(text, "You sent: message=hi");
        StringAssert.Contains(text, "Parse error:");
    }

    // ------------------------------------------------------------------ get_prompt

    [TestMethod]
    public async Task GetPrompt_ArgumentsAsObject_RendersThePrompt()
    {
        await using var rig = await BuildAsync();

        var result = await GetPromptAsync(rig, "summarize", new Dictionary<string, object?> { ["text"] = "as object" });

        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        Assert.AreEqual("Summarize: as object", PromptTextOf(result));
    }

    [TestMethod]
    public async Task GetPrompt_ArgumentsAsJsonString_RendersThePrompt()
    {
        await using var rig = await BuildAsync();

        var result = await GetPromptAsync(rig, "summarize", """{"text":"as string"}""");

        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        Assert.AreEqual("Summarize: as string", PromptTextOf(result));
    }

    [TestMethod]
    public async Task GetPrompt_ArgumentsNullOrOmitted_RendersWithNoArguments()
    {
        await using var rig = await BuildAsync();

        var withNull = await GetPromptAsync(rig, "hello", null);
        var omitted = await GetPromptAsync(rig, "hello", null, omit: true);

        Assert.IsFalse(withNull.IsError ?? false, TextOf(withNull));
        Assert.AreEqual("Hello", PromptTextOf(withNull));
        Assert.IsFalse(omitted.IsError ?? false, TextOf(omitted));
        Assert.AreEqual("Hello", PromptTextOf(omitted));
    }

    [TestMethod]
    public async Task GetPrompt_ArgumentsAsArray_NamesWhatWasSent()
    {
        await using var rig = await BuildAsync();

        var result = await GetPromptAsync(rig, "summarize", new[] { "x" });

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        Assert.IsFalse(text.Contains(SdkGenericError), text);
        StringAssert.Contains(text, "'arguments' must be the prompt's argument object");
        StringAssert.Contains(text, "You sent a JSON array");
    }

    [TestMethod]
    public async Task GetPrompt_StringThatIsNotJson_ReportsTheParseError()
    {
        await using var rig = await BuildAsync();

        var result = await GetPromptAsync(rig, "summarize", "text=hi");

        Assert.IsTrue(result.IsError ?? false);
        var text = TextOf(result);
        Assert.IsFalse(text.Contains(SdkGenericError), text);
        StringAssert.Contains(text, "is not valid JSON");
        StringAssert.Contains(text, "You sent: text=hi");
    }

    // ------------------------------------------------------------------ advertised schema

    [TestMethod]
    [DataRow("invoke_tool")]
    [DataRow("get_prompt")]
    public async Task ToolsList_AdvertisesObjectStringOrNullForArguments(string toolName)
    {
        await using var rig = await BuildAsync();

        var tools = await rig.Client.ListToolsAsync(cancellationToken: TestTimeout);
        var tool = tools.Single(t => t.Name == toolName);
        var arguments = tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("arguments");

        var types = arguments.GetProperty("type").EnumerateArray().Select(e => e.GetString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "object", "string", "null" }, types);
        Assert.IsTrue(arguments.TryGetProperty("description", out _), "The parameter description must survive the schema rewrite.");

        var required = tool.ProtocolTool.InputSchema.TryGetProperty("required", out var req)
            ? req.EnumerateArray().Select(e => e.GetString()).ToList()
            : [];
        CollectionAssert.DoesNotContain(required, "arguments");
    }
}
