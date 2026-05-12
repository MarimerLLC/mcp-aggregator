using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Configuration;

public static class McpServerBuilderExtensions
{
    public static IMcpServerBuilder AddAggregatorMcpServer(this IServiceCollection services)
    {
        var version = GetAggregatorVersion();
        var builder = services.AddMcpServer();

        services.AddOptions<McpServerOptions>()
            .Configure<IOptions<AggregatorOptions>>((mcpOpts, aggOpts) =>
            {
                var agg = aggOpts.Value;
                mcpOpts.ServerInfo = new Implementation
                {
                    Name = agg.SelfName,
                    Title = "MCP Aggregator",
                    Version = version,
                };
                mcpOpts.ServerInstructions = BuildInstructions(agg.SelfName);
            });

        return builder;
    }

    private static string BuildInstructions(string selfName) =>
        $"""
        MCP Aggregator — a single MCP endpoint that fans out to many downstream MCP servers.
        One connection gives the client the union of tools across every registered server,
        without consuming a slot per server in clients that cap concurrent MCP connections.

        Discovery flow:
          1. list_services() — see every registered downstream server and its summary.
          2. get_service_skill(serverName: "{selfName}") — full usage guide for this aggregator.
          3. get_service_skill(serverName: "<downstream>") — usage guide for a specific service.
          4. call_tool(serverName, toolName, arguments) — invoke any downstream tool through the aggregator.

        Downstream connections are established lazily on first use and reused across calls.
        Start by calling get_service_skill(serverName: "{selfName}").
        """;

    private static string GetAggregatorVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(McpServerBuilderExtensions).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}
