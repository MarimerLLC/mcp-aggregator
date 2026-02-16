# MCP Aggregator Skill Guide

You are connected to an MCP aggregator gateway. It proxies your tool calls to multiple downstream MCP servers through a single connection. Use this guide to work with it effectively.

## Workflow

1. **Discover** — call `list_services` to see all registered downstream servers and a summary of their tools.
2. **Drill down** — call `get_service_details` with a `serverName` to get full tool schemas (parameter names, types, descriptions).
3. **Read the skill** — call `get_service_skill` with a `serverName` to get usage guidance for that server's tools (if available).
4. **Invoke** — call `invoke_tool` with `serverName`, `toolName`, and `arguments` (a JSON object) to execute a tool on the downstream server.

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

## Tips

- The `list_services` descriptions may be AI-generated summaries that are more concise and differentiated than the raw server descriptions. These are created automatically when a server is registered.
- Always start with `list_services` to understand what is available before invoking tools.
- Use `get_service_skill` before using a server for the first time — skill documents contain important context about how to use the tools correctly.
- The `arguments` parameter of `invoke_tool` is a JSON string, not a JSON object. Serialize the arguments object to a string before passing it.
- Connections to downstream servers are lazy — the first `invoke_tool` call to a server may take slightly longer as the connection is established.
- If a tool call fails, check that you used the correct `serverName` and `toolName` from `list_services`.

## Admin Operations

Use `register_server` to add new downstream servers at runtime. You need:
- `name` — unique identifier
- `transportType` — `"Stdio"` or `"Http"`
- `endpoint` — for Stdio: the command to run; for Http: the server URL
- `arguments` (Stdio only) — JSON array of command arguments
- `environment` (Stdio only) — JSON object of environment variables

Use `update_skill` to attach a markdown guide to any registered server, helping future LLM sessions use its tools more effectively.

Use `regenerate_summary` to refresh a server's AI-generated summary. This is rarely needed — summaries are generated automatically at registration time. Use it if a server's tools have changed significantly since it was registered, or if the original summary generation failed.
