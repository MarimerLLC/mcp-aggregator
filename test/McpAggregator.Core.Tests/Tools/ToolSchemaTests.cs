using System.Text.Json;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// Keeps the generated tool schemas honest (#37). The MCP schema generator marks a parameter
/// optional only when it has a default value — C# nullability alone does nothing — so a parameter
/// documented as optional but declared without a default silently lands in the schema's
/// <c>required</c> array, and omitting it fails with a message that names nothing.
/// </summary>
[TestClass]
public class ToolSchemaTests
{
    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var services = new ServiceCollection();
        services.AddAggregatorCore(configuration);
        services.AddAggregatorMcpServer()
            .WithToolsFromAssembly(typeof(ConsumerTools).Assembly);

        return services.BuildServiceProvider();
    }

    private static (List<string> Properties, List<string> Required) GetSchema(string toolName)
    {
        using var provider = BuildProvider();

        // Consumer tools come from the assembly scan; admin tools are built by AdminToolSet.
        var tool = provider.GetServices<McpServerTool>()
            .Concat(provider.GetRequiredService<AdminToolSet>().Tools)
            .FirstOrDefault(t => t.ProtocolTool.Name == toolName);

        Assert.IsNotNull(tool, $"Tool '{toolName}' was not discovered from the Core assembly.");

        var schema = tool.ProtocolTool.InputSchema;
        Assert.AreEqual(JsonValueKind.Object, schema.ValueKind);

        var properties = schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
            ? props.EnumerateObject().Select(p => p.Name).ToList()
            : [];

        var required = schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Select(e => e.GetString()!).ToList()
            : [];

        return (properties, required);
    }

    private static void AssertOptional(string toolName, params string[] optionalParameters)
    {
        var (properties, required) = GetSchema(toolName);

        foreach (var parameter in optionalParameters)
        {
            CollectionAssert.Contains(properties, parameter,
                $"'{toolName}' no longer declares parameter '{parameter}'.");
            CollectionAssert.DoesNotContain(required, parameter,
                $"'{toolName}' marks documented-optional parameter '{parameter}' as required. " +
                "Give it a default value in the C# signature — nullability alone is not enough.");
        }
    }

    private static void AssertRequired(string toolName, params string[] requiredParameters)
    {
        var (properties, required) = GetSchema(toolName);

        foreach (var parameter in requiredParameters)
        {
            CollectionAssert.Contains(properties, parameter,
                $"'{toolName}' no longer declares parameter '{parameter}'.");
            CollectionAssert.Contains(required, parameter,
                $"'{toolName}' no longer marks genuinely-required parameter '{parameter}' as required.");
        }
    }

    [TestMethod]
    public void RegisterServer_OptionalParametersAreNotRequired()
        => AssertOptional("register_server",
            "displayName", "description", "arguments", "environment", "headers", "connectionTimeoutSeconds");

    [TestMethod]
    public void RegisterServer_KeepsGenuinelyRequiredParameters()
        => AssertRequired("register_server", "name", "transportType", "endpoint");

    [TestMethod]
    public void UpdateServer_OnlyServerNameIsRequired()
    {
        AssertOptional("update_server",
            "transportType", "endpoint", "displayName", "description",
            "arguments", "environment", "headers", "connectionTimeoutSeconds");
        AssertRequired("update_server", "serverName");

        // update_server exists to do partial updates; demanding anything beyond the target server
        // defeats its purpose.
        var (_, required) = GetSchema("update_server");
        CollectionAssert.AreEquivalent(new[] { "serverName" }, required);
    }

    [TestMethod]
    public void InvokeTool_ArgumentsIsOptional()
    {
        AssertOptional("invoke_tool", "arguments");
        AssertRequired("invoke_tool", "serverName", "toolName");
    }

    [TestMethod]
    public void FindTools_LimitIsOptional()
    {
        // The description says "(default 10)" — the schema must agree, and the DI-injected
        // catalog must not leak into the parameters.
        AssertOptional("find_tools", "limit");
        AssertRequired("find_tools", "query");

        var (properties, _) = GetSchema("find_tools");
        CollectionAssert.DoesNotContain(properties, "catalog");
    }

    [TestMethod]
    public void GetPrompt_ArgumentsIsOptional()
    {
        // The description already says "or null if no arguments needed" — the schema must agree.
        AssertOptional("get_prompt", "arguments");
        AssertRequired("get_prompt", "serverName", "promptName");
    }

    [TestMethod]
    public void ReadResource_RequiresServerNameAndUri()
    {
        AssertRequired("read_resource", "serverName", "uri");

        var (properties, _) = GetSchema("read_resource");
        CollectionAssert.DoesNotContain(properties, "proxy");
        CollectionAssert.DoesNotContain(properties, "registry");
    }

    [TestMethod]
    public void InjectedServicesAreNotExposedAsToolParameters()
    {
        // The DI-resolved services must stay out of the schema; if they leak in, every assertion
        // above is measuring the wrong thing.
        var (properties, _) = GetSchema("register_server");

        CollectionAssert.DoesNotContain(properties, "registry");
        CollectionAssert.DoesNotContain(properties, "connectionManager");
        CollectionAssert.DoesNotContain(properties, "summaryGenerator");
        CollectionAssert.DoesNotContain(properties, "ct");
    }
}
