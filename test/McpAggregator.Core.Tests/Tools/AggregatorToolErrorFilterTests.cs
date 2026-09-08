using System.Text.Json;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tests.Helpers;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// A binding failure on one of the aggregator's own tools used to reach the caller as a bare
/// "An error occurred invoking 'X'", with the detail confined to the server log (#37). These tests
/// pin the self-correcting replacement.
/// </summary>
[TestClass]
public class AggregatorToolErrorFilterTests
{
    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "serverName": { "type": "string" },
            "toolName": { "type": "string" },
            "arguments": { "type": ["string", "null"], "default": null }
          },
          "required": ["serverName", "toolName"]
        }
        """;

    private static JsonElement ParseSchema(string json = Schema)
        => JsonSerializer.Deserialize<JsonElement>(json);

    private static Dictionary<string, JsonElement> Args(params string[] keys)
        => keys.ToDictionary(k => k, _ => JsonSerializer.Deserialize<JsonElement>("\"x\""));

    [TestMethod]
    public void Hint_NamesMissingRequiredParameters()
    {
        var hint = AggregatorToolErrorFilter.TryBuildBindingHint(
            "invoke_tool", ParseSchema(), Args("serverName"), "underlying detail");

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "invoke_tool");
        StringAssert.Contains(hint, "Missing required parameter(s): [toolName]");
        StringAssert.Contains(hint, "You sent: [serverName]");
        StringAssert.Contains(hint, "underlying detail");
    }

    [TestMethod]
    public void Hint_NamesUnrecognizedKeys()
    {
        var hint = AggregatorToolErrorFilter.TryBuildBindingHint(
            "invoke_tool", ParseSchema(), Args("serverName", "toolName", "endpiont"), "underlying detail");

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "Unrecognized argument key(s): [endpiont]");
    }

    [TestMethod]
    public void Hint_EmbedsTheInputSchemaSoTheCallerCanSelfCorrect()
    {
        var hint = AggregatorToolErrorFilter.TryBuildBindingHint(
            "invoke_tool", ParseSchema(), Args(), "underlying detail");

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "You sent: [(no arguments)]");
        StringAssert.Contains(hint, "\"required\":[\"serverName\",\"toolName\"]");
        StringAssert.Contains(hint, "Parameters absent from the schema's 'required' list may be omitted entirely.");
    }

    [TestMethod]
    public void Hint_ReportsATypeMismatchWhenEveryRequiredKeyIsPresent()
    {
        var hint = AggregatorToolErrorFilter.TryBuildBindingHint(
            "invoke_tool", ParseSchema(), Args("serverName", "toolName"), "underlying detail");

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "a supplied value did not match its declared type");
    }

    [TestMethod]
    public void Hint_IsNullWithoutAUsableSchema()
    {
        // No matched aggregator tool means no schema to offer — the exception must keep propagating
        // rather than being swallowed into an unhelpful error result.
        Assert.IsNull(AggregatorToolErrorFilter.TryBuildBindingHint("x", null, Args(), "detail"));
        Assert.IsNull(AggregatorToolErrorFilter.TryBuildBindingHint(
            "x", ParseSchema("""{"type":"object"}"""), Args(), "detail"));
        Assert.IsNull(AggregatorToolErrorFilter.TryBuildBindingHint(
            "x", ParseSchema("\"not an object\""), Args(), "detail"));
    }

    // ---- End-to-end through a real MCP server -------------------------------------------------

    [System.ComponentModel.Description("Test stand-in for an aggregator tool.")]
    public static string Sample(
        [System.ComponentModel.Description("required")] string serverName,
        [System.ComponentModel.Description("optional")] string? arguments = null)
        => $"{serverName}/{arguments}";

    public static string Faulty([System.ComponentModel.Description("required")] string serverName)
        => throw new InvalidOperationException("genuine tool fault");

    private static Action<McpServerOptions> WithFilter() =>
        options => options.Filters.Request.CallToolFilters.Add(
            next => AggregatorToolErrorFilter.Create(next, NullLogger.Instance));

    private static McpServerTool ToolFor(string methodName)
        => McpServerTool.Create(typeof(AggregatorToolErrorFilterTests).GetMethod(methodName)!, target: null);

    [TestMethod]
    public async Task MissingRequiredArgument_ReturnsActionableErrorInsteadOfBareMessage()
    {
        var tool = ToolFor(nameof(Sample));
        await using var server = new InMemoryMcpServer("filtered", WithFilter(), tool);
        var client = await server.CreateClientAsync();

        var result = await client.CallToolAsync(tool.ProtocolTool.Name, new Dictionary<string, object?>());

        Assert.IsTrue(result.IsError ?? false);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

        StringAssert.Contains(text, "Missing required parameter(s): [serverName]");
        StringAssert.Contains(text, "Re-invoke with arguments matching this input schema:");
        Assert.IsFalse(text.Contains("An error occurred invoking"),
            $"The caller still got the opaque SDK message: {text}");
    }

    [TestMethod]
    public async Task OmittingAnOptionalArgument_Succeeds()
    {
        var tool = ToolFor(nameof(Sample));
        await using var server = new InMemoryMcpServer("filtered", WithFilter(), tool);
        var client = await server.CreateClientAsync();

        var result = await client.CallToolAsync(
            tool.ProtocolTool.Name,
            new Dictionary<string, object?> { ["serverName"] = "alpha" });

        Assert.IsFalse(result.IsError ?? false);
        StringAssert.Contains(
            string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text)),
            "alpha/");
    }

    [TestMethod]
    public void AddAggregatorMcpServer_InstallsTheCallToolFilter()
    {
        // Both hosts get the filter purely by calling AddAggregatorMcpServer(); nothing in
        // StdioServer or HttpServer opts in explicitly, so the wiring is worth pinning.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddAggregatorCore(configuration);
        services.AddAggregatorMcpServer();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        Assert.AreEqual(1, options.Filters.Request.CallToolFilters.Count);
    }

    [TestMethod]
    public async Task GenuineToolFault_IsNotMaskedByTheFilter()
    {
        var tool = ToolFor(nameof(Faulty));
        await using var server = new InMemoryMcpServer("filtered", WithFilter(), tool);
        var client = await server.CreateClientAsync();

        var result = await client.CallToolAsync(
            tool.ProtocolTool.Name,
            new Dictionary<string, object?> { ["serverName"] = "alpha" });

        Assert.IsTrue(result.IsError ?? false);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.IsFalse(text.Contains("Argument binding failed"),
            $"A fault from inside the tool body was misreported as a binding failure: {text}");
    }

    // ---- Unknown tool names (issue #39: stale wrapper names) -----------------------------------

    [TestMethod]
    public async Task UnknownTool_WithoutACatalog_ReturnsTheFallbackHintInsteadOfFaulting()
    {
        // A bare server with no WrapperToolCatalog in DI: the filter must still turn the SDK's
        // "Unknown tool" fault into a self-correcting error result.
        var tool = ToolFor(nameof(Sample));
        await using var server = new InMemoryMcpServer("filtered", WithFilter(), tool);
        var client = await server.CreateClientAsync();

        var result = await client.CallToolAsync("ghost__echo", new Dictionary<string, object?>());

        Assert.IsTrue(result.IsError ?? false);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        StringAssert.Contains(text, "Unknown tool 'ghost__echo'");
        StringAssert.Contains(text, "tool list may be out of date");
        StringAssert.Contains(text, "find_tools(query: \"echo\")");
        StringAssert.Contains(text, "invoke_tool(serverName: \"ghost\", toolName: \"echo\"");
    }

    [TestMethod]
    public void FallbackHint_ForANonWrapperName_PointsAtFindTools()
    {
        var hint = AggregatorToolErrorFilter.BuildUnknownToolFallbackHint("send_email");

        StringAssert.Contains(hint, "Unknown tool 'send_email'");
        StringAssert.Contains(hint, "find_tools(query: \"send_email\")");
        Assert.IsFalse(hint.Contains("invoke_tool("), "Without a server there is nothing to route invoke_tool to.");
    }
}
