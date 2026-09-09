using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tools;

/// <summary>
/// The aggregator's administrative tools, built from <see cref="AdminTools"/> outside the SDK's
/// assembly scan so they are never part of the shared tool collection unless the catalog puts
/// them there (Eager mode). In Lazy mode they are disclosed per session by <c>show_admin_tools</c>
/// or dispatched by name. Depends on nothing but the service provider, so it can be consulted
/// while <c>McpServerOptions</c> are being configured without a dependency cycle.
/// </summary>
public sealed class AdminToolSet
{
    public AdminToolSet(IServiceProvider services)
    {
        var tools = new List<McpServerTool>();

        foreach (var method in typeof(AdminTools).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
            if (attribute is null)
                continue;

            tools.Add(McpServerTool.Create(method, target: null, new McpServerToolCreateOptions
            {
                Services = services,
                Name = attribute.Name,
                Description = method.GetCustomAttribute<DescriptionAttribute>()?.Description,
            }));
        }

        Tools = tools.OrderBy(t => t.ProtocolTool.Name, StringComparer.Ordinal).ToList();
        ByName = Tools.ToDictionary(t => t.ProtocolTool.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<McpServerTool> Tools { get; }

    public IReadOnlyDictionary<string, McpServerTool> ByName { get; }
}
