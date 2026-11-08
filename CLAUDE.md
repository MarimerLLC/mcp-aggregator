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
5. **Only `WrapperToolCatalog` mutates `McpServerOptions.ToolCollection`, `PromptCollection` and
   `ResourceCollection` at runtime, and it only ever adds or removes `DownstreamToolWrapper` /
   `DownstreamPromptWrapper` / `DownstreamResourceWrapper` instances.** The aggregator's own attributed
   tools are never touched.
6. **`ServerInstructions` is the hand-written orientation in `AggregatorInstructions`** (ceiling 6 KB, pinned
   by `ServerInstructionsTests`); the skill document is never embedded in the handshake, it stays behind
   `get_service_skill(serverName: "mcp-aggregator")` (issue #44).

## Typed wrapper tools (issue #39), proxied prompts (issue #40) and bridged resources (issue #45)

Every downstream tool is exposed as a `DownstreamToolWrapper` named `{server}__{tool}` carrying the
downstream `inputSchema` unchanged, every downstream prompt as a `DownstreamPromptWrapper`
(`McpServerPrompt`) named `{server}__{prompt}` carrying the downstream `arguments` unchanged, and
every downstream resource or resource template as a `DownstreamResourceWrapper` (`McpServerResource`)
at `mcp-aggregator://{server}/{uri}` (`ResourceUriNaming`: fixed scheme, server name as authority,
downstream URI verbatim as path, templates keep their `{…}`) carrying the downstream metadata
unchanged. `WrapperToolCatalog` (singleton, registered in `AddAggregatorMcpServer`) builds all three
from `ToolIndex` (`ToolDetail.Protocol` / `PromptDetail.Protocol` / `ResourceDetail.Protocol` +
`ProtocolTemplate` keep the wire objects). Everything below applies to prompts and resources as it
does to tools, with `PromptCollection` / `ResourceCollection`, `ToolIndex.PromptsChanged` /
`ResourcesChanged`, `prompts/list_changed` / `resources/list_changed`, a `ListPromptsHandler` /
`ListResourcesHandler` + `ListResourceTemplatesHandler`, and a `GetPromptHandler` /
`ReadResourceHandler` fallback standing in for the tool equivalents; prompts and resources have no
`isError` result, so wrapper-side failures are `McpProtocolException`s with readable messages.
Resource specifics: the SDK's `McpServerResourceCollection` keys plain URIs as `System.Uri`
(fragment ignored, host case-insensitive; the catalog keeps the first of two colliding entries),
`resources/read` tries an exact key then `IsMatch` on every entry then the fallback handler, the
wrapper's template matcher is its own RFC 6570 approximation (the SDK's is internal), the unknown-URI
error code is `ResourceNotFound` before protocol `2026-07-28` and `InvalidParams` from it on, and
subscriptions are not bridged: `resources.subscribe` is kept unadvertised (the `PostConfigure` guard
clears what `WithSubscribeToResourcesHandler` sets) and the subscribe/unsubscribe handlers reject with
`InvalidRequest`. Design note: `docs/typed-wrapper-tools.md` Q12. `AggregatorOptions.WrapperMode`:

- `Eager`: the catalog reconciles the shared `ToolCollection` on `ServerRegistry.RegistryChanged` and
  `ToolIndex.ToolsChanged`; `WrapperSyncHostedService` runs the first sync after host start and then every
  `IndexCacheTtl`. The SDK sends `list_changed` from the collection's `Changed` event.
- `Lazy`: the shared collection is **never** touched. Activation is per session: `find_tools` /
  `get_service_details` record wrappers (tools, prompts and resources) for the calling session, `show_admin_tools`
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
args, via)` (`via` is `wrapper` or `get_prompt`). Both resource paths go through
`ToolProxyHandler.ReadResourceAsync(server, downstreamUri, via)` (`via` is `wrapper` or `read_resource`);
callers rewrite the returned content URIs with `ResourceUriNaming.RewriteContents`. Design note and
rockbot #420 answers: `docs/typed-wrapper-tools.md`.

`invoke_tool`, `get_prompt` and REST invoke take `arguments` as `JsonElement?`: the argument object or a JSON
string of it (issue #70), read by `ToolProxyHandler.TryReadArgumentObject`, which returns a named error for anything
else instead of throwing (a binding failure would surface as the SDK's bare "An error occurred invoking ..."). The
SDK's schema for a `JsonElement` parameter is untyped, so the `PostConfigure` guard in `AddAggregatorMcpServer` rewrites
`properties.arguments.type` to `["object","string","null"]` via `ConsumerTools.AdvertiseObjectOrStringArguments`.

`McpServer` is not in DI. The handle the catalog uses is `IOptions<McpServerOptions>.Value.ToolCollection`;
the SDK server reads it live per `tools/list` and (stateful transports only) subscribes to its `Changed`
event. `DeferChangedEvents()` batches a sync into one `Changed` (none if nothing changed).

The HTTP host uses `HttpServerSessionMode.StatefulForInitializeClients` (config `McpAggregator:Http:SessionMode`):
legacy `initialize` clients get an `Mcp-Session-Id` session so `list_changed` and per-session lists work;
2026-07-28 clients are stateless as SEP-2567 requires (protocol sessions were removed from that revision).

**Stateless HTTP builds a fresh `McpServerOptions` per request via `IOptionsFactory`**, and the SDK's
options setup does `ToolCollection ??= []` before adding the attributed tools. `AddAggregatorMcpServer`
therefore pre-assigns one shared `McpServerPrimitiveCollection<McpServerTool>` (plus one
`McpServerPrimitiveCollection<McpServerPrompt>` and one `McpServerResourceCollection`) in a `Configure`
registered *before* `AddMcpServer()`, with a `PostConfigure` guard, so every options instance — the singleton
and each per-request one — points at the same collections. Without that, the HTTP host lists only the
attributed tools no matter what the catalog does. The pre-assigned (even empty) `PromptCollection` /
`ResourceCollection` is also what makes the SDK advertise the prompts / resources capability with
`listChanged`. `McpServerWiringTests` pins all three.

`AggregatorException` derives from `McpException` (issue #72): the SDK passes an `McpException`'s message on
("An error occurred invoking '{tool}': {message}") and turns any other exception into the bare
"An error occurred invoking '{tool}'.", so an aggregator failure escaping any tool stays readable.
`ServerNotFoundException` from `ServerRegistry` lists the registered servers; `ConsumerTools` catches format
from `ServerName` and list only the enabled ones.

The aggregator's own skill document (`skills/{SelfName}.md`) is not in the registry: `update_skill`,
`list_skill_history`, `restore_skill` and REST `PUT .../skill` special-case `SelfName` (save via `SkillStore`,
no skill flag, no baseline). `data/skills/mcp-aggregator.md` is embedded in Core (`ShippedSelfSkill`;
`.dockerignore` re-includes it). Every `SkillStore` save of the self document writes
`skills/{SelfName}.shipped.sha256` when the text equals the shipped one (line endings aside) and deletes it
otherwise; `SelfSkillSeeder` (hosted, startup) writes the shipped document when there is none and replaces the
live one only when it equals that marker, so edits are never overwritten and a pre-#72 document stays until
someone saves the shipped text with `update_skill`.

Server names are validated at registration (`^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$`, no `__`, not the
aggregator's own name) because they become wrapper-name prefixes. `RegisteredServer.Id` is immutable
and backfilled on load.

## Skill freshness and drift (issues #28, #41, #56)

`SkillSnapshot.CaptureAsync` (run by `update_skill` and REST `PUT .../skill`) records the version and
`SkillFingerprint` on `RegisteredServer` and, since #56, the catalog itself as
`skills/{name}.baseline.json` (`SkillBaseline`, via `ToolIndex.SaveSkillBaselineAsync`; `SkillStore.Delete`
removes it too). `ToolIndex.ComputeFreshness` adds "instructions differ from the baseline" to the
version/fingerprint rules when a baseline exists. `SkillDriftCalculator` diffs baseline vs live catalog
(`ServiceDetails.SkillDrift`, set only when stale). `get_service_skill` prepends `SkillNotice.Render` for a
stale downstream (best-effort, 10 s, never for the aggregator itself), and both save paths run
`SkillNotice.Strip` so a fetched-and-saved document never stores the notice. REST `GET .../skill` stays raw.
LLM revision (#57): `SkillMaintainer.RefreshAsync` behind the admin tool `refresh_skill` (keyed
`IChatClient` `"skill-maintenance"` when `SkillMaintenance:Model` is set, else the default one; available only
with `AI:Enabled`). Drafts are stored in `skills/drafts/{name}.json` and `apply: true` publishes the stored draft
as is while its `BasedOnSha256` and server version still match. `SkillDraftValidator` is the mechanical gate (never
model-judged). Every `SkillStore.SetAsync` archives the replaced version plus its baseline under
`skills/history/{name}/`; `restore_skill` reinstates a version's baseline via `SkillSnapshot.ApplyBaselineAsync`
(fingerprint recomputed from the stored catalog). Tests use `Helpers/FakeChatClient`.
Background upkeep (#58): `SkillMaintenanceHostedService` (registered in `AddAggregatorCore`) sweeps every
`CheckInterval` and on `SkillMaintenanceSignal` (raised by `refresh_service`), mode `SkillMaintenance:Mode`
(`Off`/`Notify`/`Propose`/`Auto`). `SkillMaintainer` decides draft reuse with one `ServerState` (document hash,
version, catalog fingerprint, instructions hash); `discard_skill_draft` keeps the file with `Discarded = true` as
the "already tried" marker. A draft that passes validation and equals the current document (ignoring line endings,
trailing whitespace and the provenance line) is a `NoChanges` marker: `refresh_skill` returns `no_changes`, and
applying it re-baselines without touching the text. The prompts go through `ReplaceLineEndings("\n")`, because raw
string literals carry the checkout's CRLF on Windows. Tests drive `SweepAsync` / `CheckServerAsync` directly with a
manual `TimeProvider`.
Change sources (#59): `RegisteredServer.SkillSources` (set by the admin tool `set_skill_sources`), read by
`SkillSourceReader`: guide tool calls and resources go through `ToolProxyHandler` (`InvocationPath.SkillMaintenance`);
change notes are https only, from `AllowedChangeNoteHosts`; for a GitHub folder, only files dated from the baseline
on. Everything is capped (`MaxSourceChars` / `MaxSourcesTotalChars`) and failures become reports. With sources set, a
version-only change goes to the model (`SkillMaintainer.HasSources`). Guide text extends the URL/email allowlist;
change notes don't. Tests: `SkillSourcesTests` with a fake `HttpMessageHandler`. Design: `docs/skill-maintenance.md`.

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
- **A Streamable HTTP downstream answers an unimplemented method with HTTP 404 + a JSON-RPC -32601 body**,
  which the client surfaces as `HttpRequestException`, not `McpProtocolException`.
  `ExecuteWithRetryAsync` checks `IsMethodNotFoundOverHttp` before `ShouldRetry` and rethrows it as
  `McpProtocolException(MethodNotFound)`, so optional `prompts/list` / `resources/list` index as empty
  instead of reconnecting (issue #53). `HttpOptionalMethodTests` hosts a real SDK HTTP server to pin this.
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
