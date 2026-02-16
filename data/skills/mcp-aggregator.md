# MCP Aggregator Skill Guide

This server acts as a unified gateway to multiple downstream MCP servers. Instead of connecting to each server individually, use the aggregator to discover, inspect, and invoke tools across all registered servers through a single connection. The aggregator exposes both an **MCP tool interface** and an equivalent **REST API** — use whichever fits your client.

## When to Use the Aggregator

- Use the aggregator to discover and call tools on downstream servers that are **only** accessible through it.
- If a downstream server is also directly connected to your session (e.g., as a native MCP connection), prefer the direct connection for performance — the aggregator adds a proxy hop.
- Use `list_services` at the start of a session to understand what's available.

## Workflow

1. **Discover** — call `list_services` to see all registered downstream servers and a summary of their tools.
2. **Drill down** — call `get_service_details` with a `serverName` to get full tool schemas (parameter names, types, descriptions).
3. **Read the skill** — call `get_service_skill` with a `serverName` to get usage guidance for that server's tools (if a skill document is available).
4. **Invoke** — call `invoke_tool` with `serverName`, `toolName`, and `arguments` to execute a tool on the downstream server.
5. **Improve the skill** — if you discover tips, gotchas, required parameter patterns, or better workflows while using a server, call `update_skill` to improve its skill doc so future sessions benefit.

The same workflow applies via the REST API. Start with `GET /api` to get aggregator info and links, then use the REST endpoints listed in the Tool Reference table below.

## Tool Reference

| MCP Tool | REST Endpoint | Purpose |
|----------|--------------|---------|
| `list_services` | `GET /api/services` | Index of all servers and their tools |
| `get_service_details` | `GET /api/services/{name}` | Full parameter schemas for a server's tools |
| `get_service_skill` | `GET /api/services/{name}/skill` | Skill/usage guide for a server |
| `invoke_tool` | `POST /api/services/{name}/tools/{tool}/invoke` | Proxy a tool call to a downstream server |
| `register_server` | `POST /api/admin/services` | Register a new downstream server |
| `unregister_server` | `DELETE /api/admin/services/{name}` | Remove a registered server |
| `update_skill` | `PUT /api/admin/services/{name}/skill` | Set or update a server's skill document |
| `regenerate_summary` | `POST /api/admin/services/{name}/regenerate-summary` | Re-generate the AI summary for a server |

The REST API entry point is `GET /api`, which returns aggregator info and links to all endpoints.

## Calling invoke_tool

Pass `arguments` as a JSON object with the tool's expected parameters.

**Example:**
```
invoke_tool(
  serverName: "microsoft-learn",
  toolName: "microsoft_docs_search",
  arguments: {"query": "dependency injection in ASP.NET Core"}
)
```

## Calling via REST API

The same invocation is available as an HTTP request:

```
POST /api/services/microsoft-learn/tools/microsoft_docs_search/invoke
Content-Type: application/json

{"arguments": "{\"query\": \"dependency injection in ASP.NET Core\"}"}
```

## Error Handling

- **Server unavailable:** If `list_services` shows a server with `available: false`, it means the downstream connection could not be established. Do not attempt `invoke_tool` calls against it — they will fail.
- **Tool call failures:** Verify that `serverName` and `toolName` exactly match values from `list_services`. Tool names are case-sensitive.
- **Slow first call:** Connections to downstream servers are lazy. The first `invoke_tool` call to a server may take longer as the connection is established. Subsequent calls will be faster.
## Tips

- The `list_services` descriptions are AI-generated summaries that are concise and differentiated. These summaries are what you see when discovering servers, so they are worth keeping accurate (see `regenerate_summary` below).
- Always check `get_service_skill` before using a server for the first time — skill documents contain important context about correct tool usage, required parameters, and best practices.

## Admin Operations

### Registering a Server

Use `register_server` to add new downstream servers at runtime:

| Parameter | Required | Description |
|-----------|----------|-------------|
| `name` | Yes | Unique identifier for the server |
| `transportType` | Yes | `"Stdio"` or `"Http"`. Stdio servers must be installed and executable on the machine running the aggregator. |
| `endpoint` | Yes | For Stdio: the command to run; for Http: the server URL. **The URL must be reachable from the aggregator's network**, not the client's — use cluster-internal DNS for k8s co-located servers. |
| `arguments` | Stdio only | JSON array of command arguments |
| `environment` | Stdio only | JSON object of environment variables |
| `displayName` | No | Human-friendly name |
| `description` | No | What the server does |

An AI-generated summary is created automatically at registration time based on the server's tools. Summary generation requires the AI backend to be configured on the aggregator (`McpAggregator:AI:Enabled = true` with a valid endpoint and API key). If AI is not configured, registration still succeeds but no summary is generated.

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
