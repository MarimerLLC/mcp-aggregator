# MCP Aggregator Skill Guide

This server acts as a unified gateway to multiple downstream MCP servers. Instead of connecting to each server individually, use the aggregator to discover, inspect, and invoke tools across all registered servers through a single connection.

## When to Use the Aggregator

- Use the aggregator to discover and call tools on downstream servers that are **only** accessible through it.
- If a downstream server is also directly connected to your session (e.g., as a native MCP connection), prefer the direct connection for performance — the aggregator adds a proxy hop.
- Use `list_services` at the start of a session to understand what's available.

## Workflow

1. **Discover** — call `list_services` to see all registered downstream servers and a summary of their tools.
2. **Drill down** — call `get_service_details` with a `serverName` to get full tool schemas (parameter names, types, descriptions).
3. **Read the skill** — call `get_service_skill` with a `serverName` to get usage guidance for that server's tools (if a skill document is available).
4. **Invoke** — call `invoke_tool` with `serverName`, `toolName`, and `arguments` to execute a tool on the downstream server.

## Tool Reference

| Tool | Purpose |
|------|---------|
| `list_services` | Get a concise index of all servers and their tools |
| `get_service_details` | Get full parameter schemas for a server's tools |
| `get_service_skill` | Get the skill/usage guide for a server |
| `invoke_tool` | Proxy a tool call to a downstream server |
| `register_server` | Register a new downstream server (admin) |
| `unregister_server` | Remove a registered server (admin) |
| `update_skill` | Set or update a server's skill document (admin) |
| `regenerate_summary` | Re-generate the AI summary for a server (admin) |

## Calling invoke_tool

The `arguments` parameter must be a **JSON string**, not a raw JSON object. Serialize the arguments before passing them.

**Example:**
```
invoke_tool(
  serverName: "microsoft-learn",
  toolName: "microsoft_docs_search",
  arguments: "{\"query\": \"dependency injection in ASP.NET Core\"}"
)
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
| `transportType` | Yes | `"Stdio"` or `"Http"` |
| `endpoint` | Yes | For Stdio: the command to run; for Http: the server URL |
| `arguments` | Stdio only | JSON array of command arguments |
| `environment` | Stdio only | JSON object of environment variables |
| `displayName` | No | Human-friendly name |
| `description` | No | What the server does |

An AI-generated summary is created automatically at registration time based on the server's tools.

### Updating Skills

Use `update_skill` to attach a markdown guide to any registered server. Skill documents help LLM sessions use a server's tools more effectively and are retrieved via `get_service_skill`.

### Regenerating Summaries

Use `regenerate_summary` to refresh a server's AI-generated summary (the description shown in `list_services`). This is rarely needed — summaries are generated automatically at registration. Use it if a server's tools have changed significantly or if the original summary was inaccurate.
