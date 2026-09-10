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
5. **Only `WrapperToolCatalog` mutates `McpServerOptions.ToolCollection` and `PromptCollection` at runtime, and
   it only ever adds or removes `DownstreamToolWrapper` / `DownstreamPromptWrapper` instances.** The
   aggregator's own attributed tools are never touched.

## Typed wrapper tools (issue #39) and proxied prompts (issue #40)

Every downstream tool is exposed as a `DownstreamToolWrapper` named `{server}__{tool}` carrying the
downstream `inputSchema` unchanged, and every downstream prompt as a `DownstreamPromptWrapper`
(`McpServerPrompt`) named `{server}__{prompt}` carrying the downstream `arguments` unchanged.
`WrapperToolCatalog` (singleton, registered in `AddAggregatorMcpServer`) builds both from `ToolIndex`
(`ToolDetail.Protocol` / `PromptDetail.Protocol` keep the wire objects). Everything below applies to
prompts as it does to tools, with `PromptCollection`, `ToolIndex.PromptsChanged`,
`prompts/list_changed`, a `ListPromptsHandler` and a `GetPromptHandler` fallback standing in for the
tool equivalents; prompts have no `isError` result, so wrapper-side failures are
`McpProtocolException`s with readable messages. `AggregatorOptions.WrapperMode`:

- `Eager`: the catalog reconciles the shared `ToolCollection` on `ServerRegistry.RegistryChanged` and
  `ToolIndex.ToolsChanged`; `WrapperSyncHostedService` runs the first sync after host start and then every
  `IndexCacheTtl`. The SDK sends `list_changed` from the collection's `Changed` event.
- `Lazy`: the shared collection is **never** touched. Activation is per session: `find_tools` /
  `get_service_details` record wrappers (tools and prompts) for the calling session, `show_admin_tools`
  records the admin tools, a `ListToolsHandler` appends that session's tools to `tools/list`, a
  `CallToolHandler` fallback dispatches any wrapper or admin tool by name (listed or not) and activates it
  for that session, and the catalog sends that session `list_changed` itself. `AdminTools` is deliberately
  not `[McpServerToolType]`; `AdminToolSet`
  builds those tools outside the assembly scan so the SDK's per-request options setup never re-adds them,
  and only Eager mode puts them in the shared collection. Never inject `WrapperToolCatalog` (or anything
  depending on `IOptions<McpServerOptions>`) into `McpServerOptions` configuration — it deadlocks the
  options pipeline. Session key:
  `McpServer.SessionId` when non-empty (stateful HTTP), else the `McpServerOptions` instance (one per stdio
  process; one per request on stateless HTTP, so nothing sticks there). `request.Server` is a fresh
  `DestinationBoundMcpServer` facade per request and must not be used as a key.

Both tool call paths go through `ToolProxyHandler.InvokeAsync(server, tool, args, via)`; the `via` metric tag
is `wrapper` or `invoke_tool`. Both prompt paths go through `ToolProxyHandler.GetPromptAsync(server, prompt,
args, via)` (`via` is `wrapper` or `get_prompt`). Design note and rockbot #420 answers:
`docs/typed-wrapper-tools.md`.

`McpServer` is not in DI. The handle the catalog uses is `IOptions<McpServerOptions>.Value.ToolCollection`;
the SDK server reads it live per `tools/list` and (stateful transports only) subscribes to its `Changed`
event. `DeferChangedEvents()` batches a sync into one `Changed` (none if nothing changed).

The HTTP host uses `HttpServerSessionMode.StatefulForInitializeClients` (config `McpAggregator:Http:SessionMode`):
legacy `initialize` clients get an `Mcp-Session-Id` session so `list_changed` and per-session lists work;
2026-07-28 clients are stateless as SEP-2567 requires (protocol sessions were removed from that revision).

**Stateless HTTP builds a fresh `McpServerOptions` per request via `IOptionsFactory`**, and the SDK's
options setup does `ToolCollection ??= []` before adding the attributed tools. `AddAggregatorMcpServer`
therefore pre-assigns one shared `McpServerPrimitiveCollection<McpServerTool>` (and one
`McpServerPrimitiveCollection<McpServerPrompt>`) in a `Configure` registered *before* `AddMcpServer()`, with
a `PostConfigure` guard, so every options instance — the singleton and each per-request one — points at the
same collections. Without that, the HTTP host lists only the attributed tools no matter what the catalog
does. The pre-assigned (even empty) `PromptCollection` is also what makes the SDK advertise the prompts
capability with `listChanged`. `McpServerWiringTests` pins both.

Server names are validated at registration (`^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$`, no `__`, not the
aggregator's own name) because they become wrapper-name prefixes. `RegisteredServer.Id` is immutable
and backfilled on load.

## Configuration

All settings under `McpAggregator` section in appsettings.json, overridable via `MCPAGGREGATOR__*` env vars.

## NuGet Packages

- `ModelContextProtocol` 2.2.0 — MCP server hosting + DI
- `ModelContextProtocol.Core` 2.2.0 — Client types (transitive)
- `ModelContextProtocol.AspNetCore` 2.2.0 — HTTP transport (HttpServer only)

The 2.x line implements the MCP **2026-07-28** spec. Behaviors that matter here:

- **Stateless HTTP is the default** (`HttpServerTransportOptions.Stateless`); `ServeCommand` still sets it
  explicitly. Stateful-only options now emit `MCP9006`.
- **`tools/list_changed` delivery depends on transport and protocol.** The server subscribes to
  `ToolCollection.Changed` only on a stateful transport (stdio; not stateless HTTP). A client that
  negotiated a pre-2026-07-28 protocol via `initialize` (Claude Desktop, Claude Code, rockbot) gets a
  session-wide broadcast. A 2026-07-28 client (the SDK client's default) receives it **only** after opening a
  `subscriptions/listen` stream with `toolsListChanged: true` (SEP-2575) — the SDK client does not do that
  on its own. `ListChangedEndToEndTests` covers both. On stateless HTTP no `list_changed` is ever delivered and Lazy
  wrappers are called by name only.
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

`Spectre.Console.Cli` is **pinned to an exact version (0.55.0)**, not a floating `0.*`. The package is
pre-1.0 and `AsyncCommand<T>.ExecuteAsync` changed both arity and accessibility between releases —
`public abstract (CommandContext, TSettings)` through 0.53.1, `protected abstract (CommandContext,
TSettings, CancellationToken)` in 0.55.0. A floating range made restore non-deterministic and broke
fresh clones (issue #23). Bump it deliberately and re-check the `ServeCommand.ExecuteAsync` override
in both hosts when you do.

## Key Types

- `McpClient` (concrete class, not interface) — use `McpClient.CreateAsync()` factory
- `McpClientTool` — extends `AIFunction`, has `Name`, `Description`, `JsonSchema`
- `CallToolResult.IsError` is `bool?` — always use `?? false`
- `ListToolsAsync(RequestOptions?, CancellationToken)` — CancellationToken is named parameter
- `StdioClientTransport` implements `IClientTransport` (not `IAsyncDisposable`)
