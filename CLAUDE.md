# MCP Aggregator — Developer Guide

## Project Structure

- `src/McpAggregator.Core/` — Shared library: models, services, MCP tools, storage
- `src/McpAggregator.StdioServer/` — Stdio MCP host (console app)
- `src/McpAggregator.HttpServer/` — HTTP/SSE MCP + REST API host (web app)
- `data/` — Runtime data directory (registry.json, skills/)

## Build & Run

```bash
dotnet build                                          # Build all
dotnet run --project src/McpAggregator.HttpServer      # Run HTTP server
dotnet run --project src/McpAggregator.StdioServer     # Run stdio server
```

## Critical Rules

1. **StdioServer must NEVER write to stdout/stderr** — those streams are the MCP transport. All logging goes to file/OTLP sinks only.
2. **MCP tools are defined in Core** — both hosts discover them via `WithToolsFromAssembly(typeof(ConsumerTools).Assembly)`.
3. **Registry persistence uses atomic writes** — temp file + rename to prevent corruption.
4. **Connections are lazy** — downstream MCP servers are connected on first use, not at registration.

## Configuration

All settings under `McpAggregator` section in appsettings.json, overridable via `MCPAGGREGATOR__*` env vars.

## NuGet Packages

- `ModelContextProtocol` 2.2.0 — MCP server hosting + DI
- `ModelContextProtocol.Core` 2.2.0 — Client types (transitive)
- `ModelContextProtocol.AspNetCore` 2.2.0 — HTTP transport (HttpServer only)

The 2.x line implements the MCP **2026-07-28** spec. Behaviors that matter here:

- **Stateless HTTP is the default** (`HttpServerTransportOptions.Stateless`); `ServeCommand` still sets it
  explicitly. Stateful-only options now emit `MCP9006`.
- **Discovery-first negotiation** — clients probe `server/discover` and fall back to the legacy
  `initialize` handshake for down-level servers. `client.ServerInfo` / `ServerInstructions` are
  populated either way.
- **SSE failures propagate the real exception** (`HttpRequestException`, `TimeoutException`, genuine
  I/O) instead of a blanket `IOException` wrapper — `ConnectionManager.ShouldRetry` accounts for this.
- **`Tool.inputSchema` is required on deserialization**; a downstream that omits it throws
  `JsonException`, which `ToolIndex.GetToolsForServerAsync` translates into a named `AggregatorException`.
- Roots/Sampling/Logging are deprecated (`MCP9005`), Tasks moved to `ModelContextProtocol.Extensions.Tasks`,
  and `AuthorizationRedirectDelegate` is superseded by `AuthorizationCallbackHandler` (`MCP9007`).
  None of these are used by this project today.
- `Microsoft.Extensions.*` must be **10.0.10+** — the SDK pins `Hosting.Abstractions` to that floor.

## Key Types

- `McpClient` (concrete class, not interface) — use `McpClient.CreateAsync()` factory
- `McpClientTool` — extends `AIFunction`, has `Name`, `Description`, `JsonSchema`
- `CallToolResult.IsError` is `bool?` — always use `?? false`
- `ListToolsAsync(RequestOptions?, CancellationToken)` — CancellationToken is named parameter
- `StdioClientTransport` implements `IClientTransport` (not `IAsyncDisposable`)
