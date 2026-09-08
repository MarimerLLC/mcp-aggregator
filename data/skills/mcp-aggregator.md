# MCP Aggregator Skill Guide

This server acts as a unified gateway to multiple downstream MCP servers. Instead of connecting to each server individually, use the aggregator to discover, inspect, and invoke tools across all registered servers through a single connection. The aggregator exposes both an **MCP tool interface** and an equivalent **REST API** — use whichever fits your client.

Every downstream tool is available as a **typed tool named `{server}__{tool}`** (for example `microsoft-learn__microsoft_docs_search`) that takes the downstream tool's own parameters. Prefer those. `invoke_tool` is the fallback.

## When to Use the Aggregator

- Use the aggregator to discover and call tools on downstream servers that are **only** accessible through it.
- If a downstream server is also directly connected to your session (e.g., as a native MCP connection), prefer the direct connection for performance — the aggregator adds a proxy hop.
- Use `find_tools` when you know what you need but not which server has it; use `list_services` to browse.

## Workflow

1. **Find** — call `find_tools(query: "what you need")`. It searches every registered server and returns matching tools with their exact typed name (`tool`), owning server, description and full `inputSchema`. In `Lazy` mode this also adds those tools to your tool list and the aggregator sends `tools/list_changed`.
2. **Call the typed tool** — call the returned `tool` name directly with the parameters from its `inputSchema`, e.g. `microsoft-learn__microsoft_docs_search(query: "dependency injection")`.
3. **Or browse** — `list_services` shows every server with each tool's `wrapperName`; `get_service_details(serverName)` returns full schemas and prompt templates and, in `Lazy` mode, makes that server's typed tools callable.
4. **Read the skill** — call `get_service_skill(serverName)` before using a server for the first time; skill documents carry required-parameter patterns and gotchas.
5. **Fallback** — if a typed tool is not in your tool list (your client has not refreshed it, or you are on the stateless HTTP endpoint), call `invoke_tool(serverName, toolName, arguments)` with `arguments` as a JSON object encoded as a string. Use `get_prompt` the same way for prompt templates.
6. **Improve the skill** — if you discover tips, gotchas, required parameter patterns, or better workflows while using a server, call `update_skill` to improve its skill doc so future sessions benefit.

The same discovery data is available via the REST API. Start with `GET /api` to get aggregator info and links, then use the REST endpoints listed in the Tool Reference table below. Typed tools are MCP-only; REST callers use the invoke endpoint.

## Tool Reference

| MCP Tool | REST Endpoint | Purpose |
|----------|--------------|---------|
| `find_tools` | — | Search every server for tools; returns typed names and schemas, activates them in `Lazy` mode |
| `{server}__{tool}` | — | Typed wrapper for one downstream tool; takes that tool's own parameters |
| `list_services` | `GET /api/services` | Index of all servers, their tools and each tool's `wrapperName` |
| `get_service_details` | `GET /api/services/{name}` | Full tool schemas and prompt templates for a server; activates its typed tools in `Lazy` mode |
| `get_service_skill` | `GET /api/services/{name}/skill` | Skill/usage guide for a server |
| `invoke_tool` | `POST /api/services/{name}/tools/{tool}/invoke` | Escape hatch: proxy a tool call with a stringified JSON argument object |
| `get_prompt` | — | Escape hatch: retrieve a rendered prompt template from a downstream server (MCP only) |
| `refresh_service` | — | Drop cached connection, tools and prompts for a server and rebuild its typed tools |
| `enable_service` | `POST /api/admin/services/{name}/enable` | Enable a disabled server, allowing tool invocations |
| `disable_service` | `POST /api/admin/services/{name}/disable` | Disable a server, preventing tool invocations |
| `register_server` | `POST /api/admin/services` | Register a new downstream server |
| `update_server` | `PUT /api/admin/services/{name}` | Update a server's transport configuration or metadata |
| `unregister_server` | `DELETE /api/admin/services/{name}` | Remove a registered server |
| `update_skill` | `PUT /api/admin/services/{name}/skill` | Set or update a server's skill document |
| `regenerate_summary` | `POST /api/admin/services/{name}/regenerate-summary` | Re-generate the AI summary for a server |

The REST API entry point is `GET /api`, which returns aggregator info and links to all endpoints.

## Calling typed wrapper tools

Typed tools are named `{server}__{tool}` and take exactly the downstream tool's parameters. Get the name and schema from `find_tools`, `list_services` (`wrapperName`) or `get_service_details`.

**Example:**
```
find_tools(query: "docs search")
  → matches: [{ tool: "microsoft-learn__microsoft_docs_search", server: "microsoft-learn",
                inputSchema: { properties: { query: { type: "string" } }, required: ["query"] }, activated: true }]

microsoft-learn__microsoft_docs_search(query: "dependency injection in ASP.NET Core")
```

- A typed tool called without one of its required parameters returns an error naming the parameter and embedding the schema; the downstream is not contacted. Re-invoke with the missing parameter.
- Each server has an immutable `id` (in `list_services`, `get_service_details`, `find_tools`). Typed tool names follow the server *name*; if a server is unregistered and re-registered under a new name, its typed tools change and its `id` changes. If a stored typed name stops existing, run `find_tools` again.
- `WrapperMode` on the aggregator is `Lazy` (typed tools appear after `find_tools` / `get_service_details`; the aggregator sends `tools/list_changed`) or `Eager` (all typed tools are always listed). `find_tools` reports the mode.

## Calling invoke_tool (fallback)

Use `invoke_tool` only when the typed tool is not in your tool list. Pass `arguments` as a JSON object **encoded as a string** with the tool's expected parameters.

**Example:**
```
invoke_tool(
  serverName: "microsoft-learn",
  toolName: "microsoft_docs_search",
  arguments: "{\"query\": \"dependency injection in ASP.NET Core\"}"
)
```

## Using Prompt Templates

Some downstream servers expose prompt templates — reusable message sequences that can be parameterized. `get_service_details` always returns both tools and prompts for a server, so you'll see them in the same call you use to inspect tool schemas.

To render a prompt, call `get_prompt` with the server name, the prompt name, and any required arguments:

```
get_prompt(
  serverName: "my-server",
  promptName: "summarize_document",
  arguments: {"document": "...content...", "style": "bullet-points"}
)
```

The result contains a description and a list of messages (with role and content) ready to inject into a conversation. Required vs. optional arguments are indicated in the `arguments` list returned by `get_service_details`.

## Calling via REST API

The same invocation is available as an HTTP request:

```
POST /api/services/microsoft-learn/tools/microsoft_docs_search/invoke
Content-Type: application/json

{"arguments": "{\"query\": \"dependency injection in ASP.NET Core\"}"}
```

## Error Handling

- **Server unavailable:** If a server is disabled (via `disable_service`) or cannot be reached, typed tool and `invoke_tool` calls return an error result "Server '{name}' is unavailable." Check `list_services` to see the server's enabled status. In `Eager` mode a disabled server's typed tools are removed from the tool list.
- **Missing parameter:** A typed tool called without a required parameter returns an error naming it. `invoke_tool` errors on argument mismatches attach the missing/unknown keys and the schema.
- **Unknown typed tool:** Calling a `{server}__{tool}` name the aggregator does not currently expose returns an error result that says why: the server was renamed or removed (with the registered server names), the server is disabled, the server has no such tool (with its actual tool names), or the tool exists but was not in your tool list yet — in that case the aggregator activates it and sends `tools/list_changed`, so refresh your tool list and retry, or use `invoke_tool` immediately. If your client rejects the name before sending it, run `find_tools` again.
- **Tool call failures:** Verify that `serverName` and `toolName` exactly match values from `list_services`. Tool names are case-sensitive.
- **Slow first call:** Connections to downstream servers are lazy. The first call to a server may take longer as the connection is established. Subsequent calls will be faster.
## Tips

- The `list_services` descriptions are AI-generated summaries written for AI consumers — they use precise technical language to help with routing decisions. Summaries are generated from the server's full capability set (tools and prompt templates) at registration time. If a server's capabilities change significantly, use `regenerate_summary` to refresh the summary.
- Always check `get_service_skill` before using a server for the first time — skill documents contain important context about correct tool usage, required parameters, and best practices.

## Admin Operations

### Enabling and Disabling Servers

Servers can be temporarily disabled without unregistering them. This is useful when a downstream server is misbehaving, unreachable, or being maintained.

**Disabling a server:**
- Use `disable_service(serverName)` to disable a registered server
- The server's connection is immediately closed
- Its typed `{server}__{tool}` tools are removed from the tool list, and any call to it fails with "Server is unavailable"
- The server remains registered and all its configuration, skill docs, and metadata are preserved
- The disabled state persists across aggregator restarts

**Enabling a server:**
- Use `enable_service(serverName)` to re-enable a disabled server
- The server will be available for tool invocations again, and its typed tools come back (immediately in `Eager` mode, on the next `find_tools` / `get_service_details` in `Lazy` mode)
- A new connection will be established on the next call

**When to use:**
- Temporarily disable a server that is returning errors or timing out
- Disable servers during maintenance windows
- Quickly recover from problematic downstream servers without losing their configuration

**Example:**
```
# Disable a problematic server
disable_service(serverName: "calendar-mcp")

# Later, re-enable it
enable_service(serverName: "calendar-mcp")
```

### Registering a Server

Use `register_server` to add new downstream servers at runtime:

| Parameter | Required | Description |
|-----------|----------|-------------|
| `name` | Yes | Unique identifier for the server. Becomes the prefix of its typed tools (`{name}__{tool}`), so it must match `[A-Za-z0-9][A-Za-z0-9_.-]{0,63}` and cannot contain `__`. |
| `transportType` | Yes | `"Stdio"` or `"Http"`. Stdio servers must be installed and executable on the machine running the aggregator. |
| `endpoint` | Yes | For Stdio: the command to run; for Http: the server URL. **The URL must be reachable from the aggregator's network**, not the client's — use cluster-internal DNS for k8s co-located servers. |
| `arguments` | Stdio only | JSON array of command arguments |
| `environment` | Stdio only | JSON object of environment variables |
| `headers` | Http only | JSON object of HTTP headers, e.g. `{"Authorization": "Bearer ${TOKEN}"}`. Values may reference process environment variables with `${VAR}`, resolved at connect time; an unset variable fails the connection. |
| `connectionTimeoutSeconds` | Http only | Connection timeout in seconds; omit for the default |
| `displayName` | No | Human-friendly name |
| `description` | No | What the server does |

An AI-generated summary is created automatically at registration time from the server's tools and prompt templates. Summary generation requires the AI backend to be configured on the aggregator (`McpAggregator:AI:Enabled = true` with a valid endpoint and API key). If AI is not configured, registration still succeeds but no summary is generated.

### Updating a Server

Use `update_server` to change a registered server's transport configuration or metadata **without
losing its skill document, AI summary, enabled state, or registration timestamp**. This is the tool
for rotating an API key or bearer token on an HTTP downstream.

| Parameter | Required | Description |
|-----------|----------|-------------|
| `serverName` | Yes | The registered server to update |
| `transportType` | With `endpoint` | `"Stdio"` or `"Http"`. Supply both to replace the transport wholesale; supply neither to change metadata only. |
| `endpoint` | With `transportType` | For Stdio: the command; for Http: the server URL |
| `arguments` | Stdio only | JSON array of command arguments |
| `environment` | Stdio only | JSON object of environment variables |
| `headers` | Http only | JSON object of HTTP headers |
| `connectionTimeoutSeconds` | Http only | Connection timeout in seconds |
| `displayName` | No | New display name |
| `description` | No | New description |

The transport is replaced wholesale, not merged — omitting `headers` on an update that supplies
`transportType` and `endpoint` clears the existing headers. Fetch the current configuration with
`GET /api/admin/services/{name}` first if you need to preserve unrelated fields (header *values*
come back masked, so re-supply them from their source).

Any live connection to the server is dropped on update, so the next call reconnects with the new
configuration. A transport change also rebuilds the server's typed tools from whatever the new
transport serves.

**Example — rotating a token:**
```
update_server(
  serverName: "remote-api",
  transportType: "Http",
  endpoint: "https://api.example.com/mcp",
  headers: {"Authorization": "Bearer ${REMOTE_API_TOKEN}"}
)
```

### Updating Skills

Skill docs are **living documents** — they should be updated as you learn better patterns for using a server's tools. This is not just an admin task; it's an expected part of the usage lifecycle.

Use `update_skill(serverName, markdown)` to set or improve any server's skill document. You should update a skill doc when you:

- Discover required parameter patterns or defaults that aren't documented
- Find gotchas, error conditions, or workarounds worth noting
- Develop a better workflow or sequence of tool calls
- Notice the existing skill doc is missing, incomplete, or wrong

Each update replaces the full document, so fetch the current content with `get_service_skill` first, then merge your improvements. The goal is that the next session using this server benefits from what you learned.

### Regenerating Summaries

Use `regenerate_summary` to refresh a server's AI-generated summary (the description shown in `list_services`). This is rarely needed — summaries are generated automatically at registration. Use it if a server's tools have changed significantly or if the original summary was inaccurate.
