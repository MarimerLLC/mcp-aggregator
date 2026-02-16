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

- `ModelContextProtocol` 0.8.0-preview.1 — MCP server hosting + DI
- `ModelContextProtocol.Core` 0.8.0-preview.1 — Client types (transitive)
- `ModelContextProtocol.AspNetCore` 0.8.0-preview.1 — HTTP transport (HttpServer only)

## Key Types

- `McpClient` (concrete class, not interface) — use `McpClient.CreateAsync()` factory
- `McpClientTool` — extends `AIFunction`, has `Name`, `Description`, `JsonSchema`
- `CallToolResult.IsError` is `bool?` — always use `?? false`
- `ListToolsAsync(RequestOptions?, CancellationToken)` — CancellationToken is named parameter
- `StdioClientTransport` implements `IClientTransport` (not `IAsyncDisposable`)
