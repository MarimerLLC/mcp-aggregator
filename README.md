# MCP Aggregator

A gateway that sits between AI/LLM tools and multiple [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) servers, providing a single unified endpoint with lazy loading, dynamic registration, and dual MCP + REST interfaces.

## Why MCP Aggregator?

As MCP adoption grows, AI-powered tools like Claude Code, Cursor, and Copilot each need direct connections to every MCP server they use. This creates configuration sprawl and connection overhead.

MCP Aggregator solves this by acting as a single gateway:

- **One connection, many servers** — your AI tool connects to the aggregator; the aggregator manages connections to all downstream MCP servers.
- **Typed wrapper tools** — every downstream tool is exposed as a first-class tool named `{server}__{tool}` with the downstream's own input schema, so models call `microsoft-learn__microsoft_docs_search(query: "...")` instead of authoring a stringified JSON blob. `find_tools` searches across every server.
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

### CLI Options

Both servers accept command-line switches that override appsettings and environment variables:

| Option | Servers | Description |
|--------|---------|-------------|
| `--data-dir` | Both | Path to the data directory (registry, skills) |
| `--log-dir` | Both | Path to the log directory |
| `--port` | HTTP only | HTTP listen port |

```bash
# Run HTTP server on a custom port with explicit data directory
dotnet run --project src/McpAggregator.HttpServer -- --port 5100 --data-dir /path/to/data

# Run stdio server with explicit directories
dotnet run --project src/McpAggregator.StdioServer -- --data-dir /path/to/data --log-dir /path/to/logs
```

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
    "transport": {
      "type": "Stdio",
      "command": "npx",
      "arguments": ["-y", "@example/mcp-server"]
    }
  }'

# Register an HTTP-based MCP server, with headers and a connection timeout
curl -X POST http://localhost:8080/api/admin/services \
  -H "Content-Type: application/json" \
  -d '{
    "name": "remote-server",
    "description": "A remote MCP server",
    "transport": {
      "type": "Http",
      "url": "http://localhost:3000/mcp",
      "headers": {
        "Authorization": "Bearer ${REMOTE_MCP_TOKEN}",
        "X-Tenant": "contoso"
      },
      "connectionTimeout": "00:00:30"
    }
  }'

# Rotate a header without losing the server's skill document or AI summary
curl -X PUT http://localhost:8080/api/admin/services/remote-server \
  -H "Content-Type: application/json" \
  -d '{
    "transport": {
      "type": "Http",
      "url": "http://localhost:3000/mcp",
      "headers": { "Authorization": "Bearer ${REMOTE_MCP_TOKEN_V2}" }
    }
  }'
```

#### HTTP Headers and Secrets

HTTP downstream servers accept arbitrary `headers` — an API key, a bearer token, a tenant
identifier, whatever the server requires. A header value may be a literal, or it may reference
a process environment variable with `${VAR}` syntax, so the registry file stays free of secrets:

- References are expanded **at connect time**, not at registration time. Rotate the variable,
  restart the aggregator (or let the connection go idle), and the next connection picks up the
  new value.
- Expansion works anywhere in the value, so `"Bearer ${GITHUB_TOKEN}"` works, not just a
  whole-value reference. Write `$${` for a literal `${`.
- A referenced variable that is not set fails the connection with an error naming the variable.
- Header values (and stdio `environment` values) are masked as `***` wherever the aggregator
  reads configuration back out, including `GET /api/admin/services/{name}`. They are never logged.

`connectionTimeout` is optional and per-server; omit it to use the SDK default.

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
2. It calls `find_tools` with what it needs ("send email", "docs search") and gets back matching typed tools with their exact names and input schemas. Or it browses: `list_services` for a concise index, `get_service_details` for a server's full schemas and prompt templates.
3. It calls the typed tool directly, e.g. `microsoft-learn__microsoft_docs_search(query: "...")`. The call is proxied to the downstream server with timeout, retry and error hints handled by the aggregator.
4. `invoke_tool` and `get_prompt` remain as escape hatches for the generic path.
5. Idle downstream connections are automatically closed after a configurable timeout.

### Typed wrapper tools

Each downstream tool becomes a tool on the aggregator named `{server}__{tool}` — the registered
server name, two underscores, the downstream tool name — carrying the downstream `inputSchema`
unchanged. Two servers with the same tool name (say, two OneDrive servers with `list_files`) get
two distinct wrappers. Calls flow through the same proxy as `invoke_tool`, so timeouts, retries,
telemetry and the self-correcting argument hints are shared. A wrapper called without a required
parameter returns an error naming the parameter without contacting the downstream.

`WrapperMode` controls when wrappers appear in `tools/list`:

| Mode | `tools/list` contains | Wrappers become callable when |
|------|-----------------------|-------------------------------|
| `Lazy` (default) | The aggregator's own tools, plus whatever **this session** has activated | `find_tools` or `get_service_details` activates them for the calling session and the aggregator sends that session `notifications/tools/list_changed`; any wrapper is also callable by name whether or not it is listed |
| `Eager` | Every tool of every enabled server | Always |

Lazy activation is **per session**. The point of the mode is to keep tool descriptions out of
context windows that did not ask for them, so one client's `find_tools` never enlarges another
client's `tools/list`. A client that learned a name (from `find_tools`, a skill document, an
earlier session) can call it directly; the aggregator resolves it by name and, from then on, lists
it for that session. `Lazy` is the default because Claude Desktop caps the total number of tools
across all connected servers at roughly 44.

The HTTP host ships with `Eager` in its `appsettings.json`. It runs MCP over **stateless** HTTP,
where every request is its own session: in `Lazy` mode wrappers are never listed there (nothing
carries over between requests) and are only callable by name, which suits programmatic clients but
not hosts that refuse to call a tool they have not listed. Switch it to `Lazy` if your HTTP
clients call by name and you want the smallest possible `tools/list`.

Wrapper names track the registered server name. Unregistering and re-registering a server under a
new name removes the old wrappers and adds new ones, and the server's immutable `id` (shown in
`list_services`, `get_service_details` and `find_tools`) changes, so stale references fail
visibly. Server names must match `[A-Za-z0-9][A-Za-z0-9_.-]{0,63}` and cannot contain `__`.

The design, the rockbot #420 question-by-question answers, and the pending measurement template
live in [docs/typed-wrapper-tools.md](docs/typed-wrapper-tools.md).

### Self-Describing Skill

The aggregator automatically advertises itself in the `list_services` index when a skill document exists at `data/skills/{SelfName}.md` (default: `data/skills/mcp-aggregator.md`). This skill document is shipped with the project and teaches consuming LLMs the discover-drill down-invoke workflow without any manual setup.

The aggregator's `SelfName` setting controls both the name shown in the index and which skill file is loaded. If you need to change it, update the `SelfName` in configuration and rename the skill file to match.

## MCP Tools

| Tool | Description |
|------|-------------|
| `find_tools` | Search every registered server for tools matching a query; returns typed tool names and schemas, and activates them in `Lazy` mode |
| `{server}__{tool}` | One typed wrapper per downstream tool, carrying the downstream input schema |
| `list_services` | Concise index of all registered servers with tool names, wrapper names and descriptions |
| `get_service_details` | Full tool schemas and prompt templates for a specific server; activates its wrappers in `Lazy` mode |
| `get_service_skill` | Retrieve a server's skill document (markdown guide) |
| `invoke_tool` | Escape hatch: proxy a tool call to a downstream server with a stringified JSON argument object |
| `get_prompt` | Escape hatch: retrieve a rendered prompt template from a downstream server |
| `refresh_service` | Drop cached connection, tool and prompt lists for a server and rebuild its wrappers |
| `enable_service` | Enable a registered server (its wrappers reappear) |
| `disable_service` | Disable a registered server (its wrappers are removed) |
| `register_server` | Register a new downstream MCP server |
| `unregister_server` | Remove a registered server |
| `update_server` | Update a registered server's transport configuration or metadata |
| `update_skill` | Set or update a server's skill document |
| `regenerate_summary` | Re-generate the AI summary for a registered server |

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
| GET | `/api/admin/services/{name}` | Get a server's full configuration (secrets masked) |
| PUT | `/api/admin/services/{name}` | Update a server's transport configuration or metadata |
| DELETE | `/api/admin/services/{name}` | Unregister a server |
| PUT | `/api/admin/services/{name}/skill` | Set or update a skill document |
| POST | `/api/admin/services/{name}/regenerate-summary` | Re-generate AI summary |

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
    "DefaultToolTimeout": "00:00:30",
    "WrapperMode": "Lazy"
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
| `WrapperMode` | `Lazy` (`Eager` in the HTTP host's appsettings) | When typed `{server}__{tool}` wrappers appear in `tools/list`; see [Typed wrapper tools](#typed-wrapper-tools) |
| `SelfName` | `mcp-aggregator` | Name used for the aggregator's own entry in the service index |
| `SelfDescription` | *(built-in)* | Description shown for the aggregator in the service index |

All settings can be overridden with environment variables using the `MCPAGGREGATOR__` prefix (e.g., `MCPAGGREGATOR__CONNECTIONIDLETIMEOUT=01:00:00`).

**Precedence** (highest to lowest): CLI switches > environment variables > user secrets (Development) > appsettings.json > defaults.

### AI-Generated Server Summaries

When an LLM-compatible AI endpoint is configured, the aggregator generates concise server summaries at registration time. These summaries replace verbose or vague descriptions in the service index, helping consuming LLMs make better routing decisions.

The summary is generated from the server's full capability set: its registered metadata, tool catalog, and any prompt templates it exposes. The prompt instructs the AI to frame the summary for consumption by another AI agent, so the output uses precise technical language suited for routing decisions rather than human marketing copy.

Summaries are generated once at registration and persisted. If the AI endpoint is unavailable or unconfigured, registration proceeds normally without a summary. You can regenerate a summary at any time via the `regenerate_summary` MCP tool or the `POST /api/admin/services/{name}/regenerate-summary` REST endpoint.

#### Configuration

Add an `AI` section under `McpAggregator` in `appsettings.json`:

```json
{
  "McpAggregator": {
    "AI": {
      "Enabled": false,
      "Endpoint": "",
      "Model": "",
      "ApiKey": ""
    }
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `false` | Set to `true` to enable AI summary generation |
| `Endpoint` | | Base URL of the Azure AI Inference-compatible endpoint |
| `Model` | `gpt-4.1` | Model name to use for summary generation |
| `ApiKey` | | API key for the AI endpoint |
| `Timeout` | 30 seconds | Timeout for the AI summary generation call |

The AI endpoint must be compatible with the [Azure AI Inference SDK](https://www.nuget.org/packages/Azure.AI.Inference) (`ChatCompletionsClient`). This includes Azure AI Foundry, Azure OpenAI, and any endpoint that supports the Azure AI Inference chat completions API.

> **Note:** The `Endpoint` should be the base URL up to (but not including) `/chat/completions`. The SDK appends that path automatically. For example, if the full completions URL is `https://my-service.services.ai.azure.com/models/chat/completions?api-version=2024-05-01-preview`, set the endpoint to `https://my-service.services.ai.azure.com/models`.

#### Using Environment Variables

```bash
export MCPAGGREGATOR__AI__ENABLED=true
export MCPAGGREGATOR__AI__ENDPOINT=https://my-service.services.ai.azure.com/models
export MCPAGGREGATOR__AI__MODEL=gpt-4.1
export MCPAGGREGATOR__AI__APIKEY=your-api-key
```

#### Using .NET User Secrets (Recommended for Development)

User secrets keep credentials out of source control and appsettings files:

```bash
# Initialize user secrets (one-time, per project)
cd src/McpAggregator.HttpServer
dotnet user-secrets init

# Set AI configuration
dotnet user-secrets set "McpAggregator:AI:Enabled" "true"
dotnet user-secrets set "McpAggregator:AI:Endpoint" "https://my-service.services.ai.azure.com/models"
dotnet user-secrets set "McpAggregator:AI:Model" "gpt-4.1"
dotnet user-secrets set "McpAggregator:AI:ApiKey" "your-api-key"

# Verify what's stored
dotnet user-secrets list
```

Repeat for `McpAggregator.StdioServer` if you run that host with AI enabled.

## Deployment

### Docker

```bash
docker build -f src/McpAggregator.HttpServer/Dockerfile -t mcp-aggregator .
docker run -p 8080:8080 -v mcp-data:/data mcp-aggregator
```

The container:
- Exposes port 8080
- Persists registry and skill data to the `/data` volume

> **Important:** Server registrations and skill documents are stored in the data directory. If the volume is lost or reset, all registrations must be re-created. Use a persistent volume to retain data across restarts and redeployments.

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

### Kubernetes

Kubernetes manifests are provided in the `k8s/` directory, targeting a k3s cluster with Longhorn storage and Tailscale ingress.

```bash
./k8s/build.sh          # Build and push Docker image
./k8s/deploy.sh         # Apply manifests and copy skill documents
./k8s/deploy.sh --restart  # Pull latest image without reapplying manifests
```

The deploy script automatically copies skill documents from `data/skills/` into the pod's persistent volume after each deployment. AI secrets (API keys) should be created directly via `kubectl create secret` — they are not stored in the manifests.

### Claude Code Integration

Add the aggregator as a stdio MCP server in your Claude Code configuration:

```json
{
  "mcpServers": {
    "aggregator": {
      "command": "dotnet",
      "args": [
        "run", "--project", "/path/to/src/McpAggregator.StdioServer",
        "--",
        "--data-dir", "/path/to/data",
        "--log-dir", "/path/to/logs"
      ]
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
- [Model Context Protocol SDK](https://github.com/modelcontextprotocol/csharp-sdk) 2.2.0
- [Serilog](https://serilog.net/) + [OpenTelemetry](https://opentelemetry.io/) for observability
- [Scalar](https://scalar.com/) for interactive API documentation

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines on reporting issues, suggesting features, and submitting pull requests.

## License

[MIT](LICENSE) &copy; 2026 Marimer LLC
