namespace McpAggregator.Core.Configuration;

/// <summary>
/// The hand-written orientation the aggregator sends as <c>instructions</c> in every
/// <c>initialize</c> / <c>server/discover</c> response (issue #44). It is deliberately short: the
/// full usage guide, error handling and admin reference live in the self skill document and are
/// fetched on demand through <c>get_service_skill</c>, never embedded here. Tool names appear
/// because the workflow needs them; tool descriptions and parameter text do not, because
/// <c>tools/list</c> already carries those (<c>ServerInstructionsTests</c> pins that).
/// </summary>
internal static class AggregatorInstructions
{
    /// <summary>
    /// Ceiling for the handshake payload in characters. The text is a constant with only the
    /// aggregator's name interpolated, so this is pinned by <c>ServerInstructionsTests</c> rather
    /// than enforced at runtime; the actual length is logged once at startup.
    /// </summary>
    public const int MaxChars = 6 * 1024;

    public static string Build(string selfName) => $$"""
        MCP Aggregator: one MCP endpoint that fans out to many downstream MCP servers, so this single
        connection gives you every registered server's tools, prompts and resources.

        Naming: each downstream tool is a typed tool "<server>__<tool>" taking that tool's own parameters;
        each downstream prompt is an MCP prompt "<server>__<prompt>"; each downstream resource is an MCP
        resource at "mcp-aggregator://<server>/<downstream-uri>" (templates keep their {placeholders}).

        Workflow:
          1. find_tools(query) to search every server for what you need, or list_services then
             get_service_details(serverName) to browse. Either one makes the matching typed tools, prompts
             and resources callable in your session.
          2. Call the typed tool by its listed name with its listed parameters; use prompts/get and
             resources/read for prompts and resources.
          3. Read get_service_skill(serverName) before using a server for the first time.

        Client capability: if your client rejects a "<server>__<tool>" name as not found before the call
        reaches this server, its tool index did not refresh. Do not retry the typed name in this
        conversation; call invoke_tool(serverName, toolName, arguments) instead (get_prompt and
        read_resource are the same escape hatch for prompts and resources). The stateless HTTP endpoint
        always needs this path.

        Administrative tools are hidden until show_admin_tools is called; they are also callable by name.
        Downstream connections open lazily on first use and are reused.

        Full usage guide, error handling and admin reference: get_service_skill(serverName: "{{selfName}}").
        """;
}
