# MCP Aggregator

A gateway that sits between AI/LLM tools and multiple [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) servers, providing a single unified endpoint with lazy loading, dynamic registration, and dual MCP + REST interfaces.

## Why MCP Aggregator?

As MCP adoption grows, AI-powered tools like Claude Code, Cursor, and Copilot each need direct connections to every MCP server they use. This creates configuration sprawl and connection overhead.

MCP Aggregator solves this by acting as a single gateway:

- **One connection, many servers** — your AI tool connects to the aggregator; the aggregator manages connections to all downstream MCP servers.
- **Typed wrapper tools** — every downstream tool is exposed as a first-class tool named `{server}__{tool}` with the downstream's own input schema, so models call `microsoft-learn__microsoft_docs_search(query: "...")` instead of authoring a stringified JSON blob. `find_tools` searches across every server.
- **Proxied prompts** — every downstream prompt template is exposed as a real MCP prompt named `{server}__{prompt}` with the downstream's own arguments, so hosts that surface prompts natively show them in their prompt picker.
- **Bridged resources** — every downstream resource and resource template is exposed as a real MCP resource at `mcp-aggregator://{server}/{uri}`, with the downstream's own metadata, so hosts that surface resources natively show them in their attachment picker.
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
2. It calls `find_tools` with what it needs ("send email", "docs search") and gets back matching typed tools with their exact names and input schemas, plus matching prompt templates with their arguments and matching resources with their URIs. Or it browses: `list_services` for a concise index, `get_service_details` for a server's full schemas, prompt templates and resources.
3. It calls the typed tool directly, e.g. `microsoft-learn__microsoft_docs_search(query: "...")`. The call is proxied to the downstream server with timeout, retry and error hints handled by the aggregator. Prompt templates are requested the same way, through `prompts/get` on the `{server}__{prompt}` name, and resources through `resources/read` on the `mcp-aggregator://{server}/{uri}` URI.
4. `invoke_tool`, `get_prompt` and `read_resource` remain as escape hatches for the generic path.
5. Idle downstream connections are automatically closed after a configurable timeout.

### Typed wrapper tools

Each downstream tool becomes a tool on the aggregator named `{server}__{tool}` — the registered
server name, two underscores, the downstream tool name — carrying the downstream `inputSchema`
unchanged. Two servers with the same tool name (say, two OneDrive servers with `list_files`) get
two distinct wrappers. Calls flow through the same proxy as `invoke_tool`, so timeouts, retries,
telemetry and the self-correcting argument hints are shared. A wrapper called without a required
parameter returns an error naming the parameter without contacting the downstream.

Downstream prompt templates get the same treatment: each becomes an MCP prompt on the aggregator
named `{server}__{prompt}`, carrying the downstream's argument list unchanged, so a host that shows
prompts to the user (a prompt picker, a slash-command menu) shows the downstream's prompts with the
downstream's own arguments. `prompts/get` on that name is forwarded to the downstream through the
same proxy, and a request missing a required argument fails naming it without contacting the
downstream. Prompts have no `isError` result, so those failures are JSON-RPC errors with a readable
message.

Downstream resources are bridged the same way: each resource (and each resource template) becomes
an MCP resource on the aggregator at `mcp-aggregator://{server}/{uri}` — the fixed scheme, the
registered server name as the authority, and the downstream's own URI verbatim as the path, so
`file:///docs/readme.md` on server `probe` is `mcp-aggregator://probe/file:///docs/readme.md` and
the template `file:///docs/{path}` is `mcp-aggregator://probe/file:///docs/{path}`. Name, title,
description, MIME type, size, annotations and icons are carried through unchanged. `resources/read`
on an aggregator URI strips the prefix, forwards the read through the same proxy, and rewrites the
content URIs back to aggregator form. Resource subscriptions are not bridged and not advertised; a
`resources/subscribe` request is rejected with a readable error rather than silently accepted.

`WrapperMode` controls when wrappers appear in `tools/list`, proxied prompts in `prompts/list`, and
bridged resources in `resources/list` / `resources/templates/list`:

| Mode | `tools/list` / `prompts/list` / `resources/list` contain | Wrappers become callable when |
|------|----------------------------------------------------------|-------------------------------|
| `Lazy` (default) | The consumer tools (9), plus whatever **this session** has activated; no prompts or resources until activated | `find_tools` or `get_service_details` activates wrappers, prompts and resources for the calling session, `show_admin_tools` activates the administrative tools, and the aggregator sends that session `notifications/tools/list_changed` / `notifications/prompts/list_changed` / `notifications/resources/list_changed`; every tool, prompt and resource is also callable by name (or URI) whether or not it is listed |
| `Eager` | Every aggregator tool, every tool, prompt and resource of every enabled server | Always |

Lazy activation is **per session** and is the aggregator's progressive disclosure. A session
starts with nine consumer tools (`find_tools`, `list_services`, `get_service_details`,
`get_service_skill`, `invoke_tool`, `get_prompt`, `read_resource`, `refresh_service`,
`show_admin_tools`), about 5 KB of `tools/list`. Downstream wrappers join that session's list when it searches for them or
drills into a server; the seven administrative tools (`register_server`, `update_server`,
`unregister_server`, `update_skill`, `regenerate_summary`, `enable_service`, `disable_service`)
join when it calls `show_admin_tools`. One client's discovery never enlarges another client's
list. A client that already knows a name (from `find_tools`, a skill document, an earlier
session) can call it directly; the aggregator resolves it by name and, from then on, lists it for
that session. Whether the *client* lets that call out is another matter: Claude Desktop chat
re-fetches `tools/list` on `list_changed` but does not refresh the running conversation's tool
index, so a wrapper activated mid-conversation is rejected client-side there and the model has
to fall back to `invoke_tool` (the skill document and every runtime hint say so). `Lazy` is the
default because Claude Desktop caps the total number of tools across all connected servers at
roughly 44.

Both hosts ship with `Lazy`. On the HTTP host, session handling follows the client's protocol
revision (`McpAggregator:Http:SessionMode`, default `StatefulForInitializeClients`): a client that
still uses the `initialize` handshake — Claude Desktop through mcp-remote, Claude Code, rockbot —
gets a session with an `Mcp-Session-Id`, so its list can grow and `list_changed` reaches it. A
2026-07-28 client is stateless, as that revision requires (it removed protocol sessions,
SEP-2567): its `tools/list` is always the minimal set and it calls wrappers by name after
`find_tools`. Set `WrapperMode` to `Eager` only for a client that cannot do either.

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
| `find_tools` | Search every registered server for tools, prompts and resources matching a query; returns typed tool names and schemas (prompt names and arguments, resource URIs), and activates them for this session in `Lazy` mode |
| `{server}__{tool}` | One typed wrapper per downstream tool, carrying the downstream input schema |
| `{server}__{prompt}` | One MCP prompt per downstream prompt template, carrying the downstream arguments (`prompts/list` / `prompts/get`, not a tool) |
| `mcp-aggregator://{server}/{uri}` | One MCP resource per downstream resource or template, carrying the downstream metadata (`resources/list`, `resources/templates/list`, `resources/read`, not a tool) |
| `show_admin_tools` | Add the administrative tools below to this session's tool list (`Lazy` mode hides them by default) |
| `list_services` | Concise index of all registered servers with tool names, wrapper names and descriptions |
| `get_service_details` | Full tool schemas, prompt templates and resources for a specific server; activates its wrappers, prompts and resources in `Lazy` mode |
| `get_service_skill` | Retrieve a server's skill document (markdown guide) |
| `invoke_tool` | Escape hatch: proxy a tool call to a downstream server with a stringified JSON argument object |
| `get_prompt` | Escape hatch: retrieve a rendered prompt template from a downstream server when the client cannot use MCP prompts |
| `read_resource` | Escape hatch: read a downstream resource by its URI (downstream or aggregator form) when the client cannot use MCP resources |
| `refresh_service` | Drop cached connection, tool, prompt and resource lists for a server and rebuild its wrappers, prompts and resources |
| `enable_service` | Admin: enable a registered server (its wrappers reappear) |
| `disable_service` | Admin: disable a registered server (its wrappers are removed) |
| `register_server` | Admin: register a new downstream MCP server |
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
| `Http:SessionMode` | `StatefulForInitializeClients` | HTTP host only: `Stateless`, `Stateful`, or sessions only for clients that use the legacy `initialize` handshake |
| `WrapperMode` | `Lazy` | When typed `{server}__{tool}` wrappers appear in `tools/list`, `{server}__{prompt}` prompts in `prompts/list` and `mcp-aggregator://{server}/{uri}` resources in `resources/list`; see [Typed wrapper tools](#typed-wrapper-tools) |
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
