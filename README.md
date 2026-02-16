# MCP Aggregator

A gateway that sits between AI/LLM tools and multiple [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) servers, providing a single unified endpoint with lazy loading, dynamic registration, and dual MCP + REST interfaces.

## Why MCP Aggregator?

As MCP adoption grows, AI-powered tools like Claude Code, Cursor, and Copilot each need direct connections to every MCP server they use. This creates configuration sprawl and connection overhead.

MCP Aggregator solves this by acting as a single gateway:

- **One connection, many servers** — your AI tool connects to the aggregator; the aggregator manages connections to all downstream MCP servers.
- **Lazy loading** — downstream servers are connected on first use, not at startup. Idle connections are automatically cleaned up.
- **Dynamic registration** — add or remove MCP servers at runtime without restarting. Changes are persisted to disk.
- **Skill documents** — attach optional markdown guides to each server describing when and how to use its tools, giving LLMs better context.
- **Dual interface** — expose everything over MCP (stdio or HTTP/SSE) for AI tools, and a REST API for programmatic access.

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (preview)

### Run the HTTP Server

```bash
dotnet run --project src/McpAggregator.HttpServer
```

The server starts on `http://localhost:8080` and exposes:

| Endpoint | Description |
|----------|-------------|
| `/mcp` | MCP over HTTP/SSE transport |
| `/api/services` | REST API for consumers |
| `/api/admin/services` | REST API for administration |
| `/scalar` | Interactive API documentation |
| `/health` | Health check |

### Run the Stdio Server

```bash
dotnet run --project src/McpAggregator.StdioServer
```

Use this mode when configuring MCP Aggregator as a stdio server in Claude Code, Cursor, or similar tools.

### Register a Downstream Server

Once the aggregator is running, register downstream MCP servers using the `register_server` tool or the REST API.

**Via REST API:**

```bash
# Register a stdio-based MCP server
curl -X POST http://localhost:8080/api/admin/services \
  -H "Content-Type: application/json" \
  -d '{
    "name": "my-server",
    "displayName": "My MCP Server",
    "description": "Does useful things",
    "transportType": "Stdio",
    "command": "npx",
    "arguments": ["-y", "@example/mcp-server"]
  }'

# Register an HTTP-based MCP server
curl -X POST http://localhost:8080/api/admin/services \
  -H "Content-Type: application/json" \
  -d '{
    "name": "remote-server",
    "description": "A remote MCP server",
    "transportType": "Http",
    "url": "http://localhost:3000/mcp"
  }'
```

**Via MCP tool call** (from an AI tool connected to the aggregator):

> Use `register_server` with name "my-server", transportType "Stdio", endpoint "npx", arguments "['-y', '@example/mcp-server']"

## How It Works

```
┌─────────────┐     ┌───────────────────┐     ┌──────────────┐
│  Claude Code │────▶│                   │────▶│ MCP Server A │
│  Cursor      │     │  MCP Aggregator   │     └──────────────┘
│  Copilot     │────▶│                   │────▶┌──────────────┐
│  REST client │     │  (stdio or HTTP)  │     │ MCP Server B │
└─────────────┘     └───────────────────┘     └──────────────┘
                              │                ┌──────────────┐
                              └───────────────▶│ MCP Server C │
                                               └──────────────┘
```

1. An AI tool connects to the aggregator via MCP (stdio or HTTP/SSE).
2. It calls `list_services` to get a concise index of all registered servers and their tools.
3. It calls `get_service_details` to drill into a specific server's full tool schemas.
4. It calls `invoke_tool` to proxy a tool call to the downstream server. The aggregator lazily connects to the downstream server on first use.
5. Idle downstream connections are automatically closed after a configurable timeout.

## MCP Tools

| Tool | Description |
|------|-------------|
| `list_services` | Concise index of all registered servers with tool names and descriptions |
| `get_service_details` | Full tool schemas for a specific server |
| `get_service_skill` | Retrieve a server's skill document (markdown guide) |
| `invoke_tool` | Proxy a tool call to a downstream server |
| `register_server` | Register a new downstream MCP server |
| `unregister_server` | Remove a registered server |
| `update_skill` | Set or update a server's skill document |

## REST API

Available on the HTTP server only.

### Consumer Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/services` | List all registered services |
| GET | `/api/services/{name}` | Get a service's details and tool schemas |
| GET | `/api/services/{name}/skill` | Get a service's skill document |
| POST | `/api/services/{name}/tools/{tool}/invoke` | Invoke a tool on a downstream server |

### Admin Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/admin/services` | Register a new server |
| DELETE | `/api/admin/services/{name}` | Unregister a server |
| PUT | `/api/admin/services/{name}/skill` | Set or update a skill document |

## Configuration

Settings are in `appsettings.json` under the `McpAggregator` section:

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

| Setting | Default | Description |
|---------|---------|-------------|
| `DataDirectory` | `data` | Directory for registry and skill files |
| `RegistryFile` | `registry.json` | Server registry filename within the data directory |
| `SkillsDirectory` | `skills` | Skill documents subdirectory within the data directory |
| `IndexCacheTtl` | 5 minutes | How long to cache the service index |
| `ConnectionIdleTimeout` | 30 minutes | Disconnect downstream servers after this idle period |
| `DefaultToolTimeout` | 30 seconds | Timeout for downstream tool calls |

All settings can be overridden with environment variables using the `MCPAGGREGATOR__` prefix (e.g., `MCPAGGREGATOR__CONNECTIONIDLETIMEOUT=01:00:00`).

## Deployment

### Docker

```bash
docker build -f src/McpAggregator.HttpServer/Dockerfile -t mcp-aggregator .
docker run -p 8080:8080 -v mcp-data:/data mcp-aggregator
```

The container:
- Exposes port 8080
- Persists registry and skill data to the `/data` volume
- Includes a health check at `/health` (30s interval)

### Docker Compose

```yaml
services:
  mcp-aggregator:
    build:
      context: .
      dockerfile: src/McpAggregator.HttpServer/Dockerfile
    ports:
      - "8080:8080"
    volumes:
      - mcp-data:/data
    environment:
      - MCPAGGREGATOR__CONNECTIONIDLETIMEOUT=01:00:00

volumes:
  mcp-data:
```

### Claude Code Integration

Add the aggregator as a stdio MCP server in your Claude Code configuration:

```json
{
  "mcpServers": {
    "aggregator": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/src/McpAggregator.StdioServer"]
    }
  }
}
```

Or point to the HTTP server if it's already running:

```json
{
  "mcpServers": {
    "aggregator": {
      "url": "http://localhost:8080/mcp"
    }
  }
}
```

## Project Structure

```
src/
  McpAggregator.Core/        Shared library: models, services, MCP tools
  McpAggregator.StdioServer/  Stdio MCP host (console app)
  McpAggregator.HttpServer/   HTTP/SSE MCP + REST API host (web app)
data/
  registry.json               Server registry (created at runtime)
  skills/                     Skill documents (created at runtime)
```

## Tech Stack

- [.NET 10](https://dotnet.microsoft.com/) / ASP.NET Core
- [Model Context Protocol SDK](https://github.com/modelcontextprotocol/csharp-sdk) 0.8.0-preview.1
- [Serilog](https://serilog.net/) + [OpenTelemetry](https://opentelemetry.io/) for observability
- [Scalar](https://scalar.com/) for interactive API documentation

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines on reporting issues, suggesting features, and submitting pull requests.

## License

[MIT](LICENSE) &copy; 2026 Marimer LLC
