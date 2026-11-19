using McpAggregator.Core.Security;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tools;

/// <summary>
/// Keeps the administrative tools away from callers on the public endpoint
/// (<see cref="AggregatorClaims.IsPublicCaller"/>). Admin tools reach the caller three ways —
/// listed from the shared collection (Eager), listed per session after <c>show_admin_tools</c>
/// (Lazy), and dispatched by name (Lazy fallback handler) — so the gate sits on the
/// <c>tools/list</c> and <c>tools/call</c> filter pipelines, which wrap all of them.
/// </summary>
public static class PublicCallerAdminFilter
{
    public const string ShowAdminToolsName = "show_admin_tools";

    internal const string DeniedMessage =
        "Administrative tools are not available on the public endpoint. " +
        "Use the private (tailnet) endpoint to register, update or remove servers and edit skills.";

    public static bool IsAdminToolName(string? name, AdminToolSet adminTools)
        => name is { Length: > 0 }
           && (adminTools.ByName.ContainsKey(name) || string.Equals(name, ShowAdminToolsName, StringComparison.Ordinal));

    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> CreateListFilter(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next,
        AdminToolSet adminTools)
    {
        return async (request, ct) =>
        {
            var result = await next(request, ct);
            if (!AggregatorClaims.IsPublicCaller(request.User))
                return result;

            result.Tools = result.Tools.Where(t => !IsAdminToolName(t.Name, adminTools)).ToList();
            return result;
        };
    }

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> CreateCallFilter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        AdminToolSet adminTools,
        ILogger logger)
    {
        return (request, ct) =>
        {
            var name = request.Params?.Name;
            if (AggregatorClaims.IsPublicCaller(request.User) && IsAdminToolName(name, adminTools))
            {
                logger.LogWarning("Rejected administrative tool '{Tool}' called through the public endpoint", name);
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = DeniedMessage }]
                });
            }

            return next(request, ct);
        };
    }
}
