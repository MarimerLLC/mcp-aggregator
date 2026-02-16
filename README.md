# MCP Aggregator

An MCP (Model Context Protocol) aggregator gateway that sits between LLM consumers and downstream MCP servers, providing lazy loading, dual MCP+REST interfaces, and dynamic server registration.

## Features

- **Lazy loading** — consumers get a concise tool index first, drill down on demand
- **Dual interface** — MCP (stdio + HTTP/SSE) and REST API
- **Dynamic registration** — add/remove MCP servers at runtime, persisted to JSON
- **Skill documents** — optional markdown per service describing best usage
- **Idle cleanup** — background service disconnects idle downstream connections

## Quick Start

### HTTP Server

```bash
dotnet run --project src/McpAggregator.HttpServer
```

The HTTP server exposes:
- MCP endpoint at `/mcp` (HTTP/SSE)
- REST API at `/api/services` and `/api/admin/services`
- Scalar API docs at `/scalar`
- Health check at `/health`

### Stdio Server

```bash
dotnet run --project src/McpAggregator.StdioServer
```

For use as a stdio MCP server in Claude Code, Cursor, or similar tools.

## MCP Tools

| Tool | Description |
|------|-------------|
| `list_services` | Concise index of all servers + tool names/descriptions |
| `get_service_details` | Full tool schemas for one server |
| `get_service_skill` | Markdown skill document |
| `invoke_tool` | Proxy a tool call downstream |
| `register_server` | Register a new MCP server |
| `unregister_server` | Remove a server |
| `update_skill` | Set skill markdown |

## REST API (HttpServer only)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/services` | List all services |
| GET | `/api/services/{name}` | Get service details |
| GET | `/api/services/{name}/skill` | Get skill document |
| POST | `/api/services/{name}/tools/{tool}/invoke` | Invoke a tool |
| POST | `/api/admin/services` | Register a server |
| DELETE | `/api/admin/services/{name}` | Unregister a server |
| PUT | `/api/admin/services/{name}/skill` | Update skill document |

## Configuration

```json
{
  "McpAggregator": {
    "DataDirectory": "data",
    "RegistryFile": "registry.json",
    "SkillsDirectory": "skills",
    "IndexCacheTtl": "00:05:00",
    "ConnectionIdleTimeout": "00:30:00",
    "DefaultToolTimeout": "00:00:30"
  }
}
```

All settings overridable via `MCPAGGREGATOR__*` environment variables.

## Docker

```bash
docker build -f src/McpAggregator.HttpServer/Dockerfile -t mcp-aggregator .
docker run -p 8080:8080 -v mcp-data:/data mcp-aggregator
```

## Tech Stack

- .NET 10, ASP.NET Core
- ModelContextProtocol SDK (0.8.0-preview.1)
- Serilog + OpenTelemetry
- Scalar for API documentation
