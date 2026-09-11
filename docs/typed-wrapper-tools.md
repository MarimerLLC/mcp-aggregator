# Typed wrapper tools for downstream servers

Design note for [issue #39](https://github.com/MarimerLLC/mcp-aggregator/issues/39), the
aggregator-side test bed for
[MarimerLLC/rockbot#420](https://github.com/MarimerLLC/rockbot/issues/420). The numbered
questions below match rockbot #420 so the answers carry across.

## The problem

The consumer surface used to be a generic proxy:

```
invoke_tool(serverName: string, toolName: string, arguments?: string)   // arguments is a JSON *string*
```

A model has to know the server exists, read the schema with `get_service_details`, author a nested
JSON object, and stringify it into one parameter. Low-tier models routinely emit
`invoke_tool(serverName, toolName)` with no arguments, get `'to' was not provided` back, and
conclude the tool is broken. The argument hints from #29/#36/#37 repair that after a failed
round-trip. This change makes the failing call shape impossible to emit.

## What shipped

Every tool of every registered downstream is exposed as a first-class MCP tool on the aggregator:

```
microsoft-learn__microsoft_docs_search(query: string)
adjutant__send_email(to: string[], subject: string, body: string, accountId?: string)
onedrive-marimer__list_files(path: string)
onedrive-personal__list_files(path: string)
```

The wrapper's `inputSchema`, `title`, `annotations`, `outputSchema` and `icons` are the
downstream's, unchanged. The description is prefixed `[server]`. `_meta.mcpAggregator` carries
`{ serverId, serverName, toolName }`.

| Piece | Where | Role |
|---|---|---|
| `DownstreamToolWrapper` | `Core/Tools` | `McpServerTool` subclass. Pre-flights `required` keys (error names the parameter), forwards raw arguments through `ToolProxyHandler`. |
| `WrapperToolCatalog` | `Core/Services` | Builds wrappers from `ToolIndex`, reconciles `McpServerOptions.ToolCollection`, serves `find_tools`. |
| `WrapperSyncHostedService` | `Core/Services` | First sync in the background after host start, then every `IndexCacheTtl`. |
| `WrapperNaming` | `Core/Tools` | `{server}__{tool}`, sanitization, parse for diagnostics. |
| `RegisteredServer.Id` | `Core/Models` | Immutable identity, backfilled on load, surfaced everywhere. |
| `find_tools` | `ConsumerTools` | Search across every enabled server; activates matches in Lazy mode. |
| `via` tag | `AggregatorTelemetry` | `wrapper` vs `invoke_tool` on `mcp_tool_invocations_total` and the `mcp.tool_invoke` activity. |

Both call paths share one method, `ToolProxyHandler.InvokeAsync(server, tool, args, via)`, so
timeout, retry, telemetry, the argument-schema hint on a downstream `isError`, the unknown-tool
hint and `isError` propagation are identical for wrappers and for `invoke_tool`.

## Answers to the design questions

### Q1 / Q8 — Eager vs Lazy vs Search

Two modes, `WrapperMode: Eager | Lazy` under `McpAggregator`.

| Mode | `tools/list` contents | How a wrapper becomes callable |
|---|---|---|
| `Eager` | meta-tools + every wrapper of every enabled server | always |
| `Lazy` | meta-tools only | `find_tools` or `get_service_details` activates it; SDK sends `tools/list_changed` |

The issue's third mode, **Search, folds into Lazy**. Search was "meta-tools plus a name-only
catalog; `find_tools` returns schemas". But `find_tools` *is* the search in both modes, and the
only thing that makes a found tool callable is activation into the tool list. A Search mode that
returns schemas without activating leaves the model holding a name it cannot call, which is the
`invoke_tool` fallback with extra steps. So Lazy = "search, then activate", and `list_services`
already carries the name-only catalog (`wrapperName` per tool) for browsing.

Activation is **per session**. The reason Lazy exists is to keep tool descriptions out of context
windows that did not ask for them, and that goal is defeated if one client's `find_tools` grows
every later client's `tools/list`. So in Lazy mode the process-wide `ToolCollection` is never
touched; instead the catalog keeps an activation set per session, a `ListToolsHandler` appends
that session's wrappers to the SDK's list (the SDK merges collection tools with handler results),
and a `CallToolHandler` fallback dispatches any wrapper **by name** whether or not it is listed,
activating it for that session as a side effect. The catalog sends that session
`tools/list_changed` itself, since the collection never changes.

The session key is `McpServer.SessionId` when the transport has one (stateful HTTP), otherwise the
`McpServerOptions` instance: one per stdio process, and one per request on stateless HTTP, which
yields exactly "no memory between requests". (`request.Server` is a fresh
`DestinationBoundMcpServer` facade per request in SDK 2.2.0 and cannot serve as a key.)

The same disclosure applies to the aggregator's own surface. A Lazy session starts with nine
consumer tools (about 5 KB of `tools/list`); the seven administrative tools are built by
`AdminToolSet` outside the SDK's assembly scan, never enter the shared collection in Lazy mode,
and join a session's list only through `show_admin_tools` or by being called by name. Eager mode
lists them as before.

Defaults: both hosts ship `Lazy`. On stdio, Claude Desktop caps the total tool count at roughly
44 across all servers, and eager registration of a 55-tool inventory would blow that on its own.
On stateless HTTP, Lazy means wrappers are never listed and are called by name (Q7); the HTTP host
first shipped `Eager` for that reason and was switched once by-name dispatch existed, since its
clients are programmatic and the minimal `tools/list` is the point.

### Q2 / Q3 — Naming and stable identity

Wrapper names derive from `RegisteredServer.Name` with separator `__`:
`onedrive-marimer__list_files`. Readable, unique across servers, and what other hosts do.

`RegisteredServer` gained an immutable `Id` (12 lowercase hex chars, assigned at registration,
backfilled once for registries written before it existed, never user-editable, preserved by
`update_server`). It is surfaced in `list_services`, `get_service_details`, `find_tools`,
`GET /api/admin/services/{name}` and the wrapper's `_meta`. A rename (unregister + re-register
under a new name) is a new registration with a new `Id`; the old wrappers disappear and new ones
appear, with `tools/list_changed`. The failure is visible, not silent. Consumers that need
durability (rockbot working memory, wisp JSON) store `Id` next to the wrapper name and re-resolve
on an unknown-tool error.

Server names are now validated at registration: `^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$`, never
containing `__`, never equal to the aggregator's own name. Existing registrations are not
revalidated. Downstream tool names are sanitized (anything outside `[A-Za-z0-9_.-]` becomes
`-`). Wrapper names longer than 64 characters are logged once, not truncated, because truncation
collides silently.

### Q4 — Backwards compatibility

`invoke_tool` and `get_prompt` remain, described as escape hatches (`get_prompt` now returns a
`CallToolResult` so its failures are `isError` results rather than bare faults, and its input
schema is unchanged). The REST invoke endpoint is unchanged and still uses the generic path. The
server instructions (the short orientation, see Q13) and the self skill document (the full
reference, behind `get_service_skill`) both steer the model to `find_tools` and the typed tools
first.

### Q5 — Wisp interaction

No aggregator analog. Anything that routes through `invoke_tool` keeps working unchanged.

### Q6 — Recovery and self-correction through wrappers

The SDK's argument binding does not run for a wrapper (it is a direct `McpServerTool` subclass,
not an `AIFunction`), so the wrapper does its own pre-flight: any key in the schema's `required`
that is absent produces an `isError` result naming the parameter and embedding the schema, and the
downstream is never called. Everything else is the shared proxy path, so a downstream `isError`
still carries the argument hint. `AggregatorToolErrorFilter` is unaffected by wrappers on the
binding path (it only converts binding `ArgumentException`s, which wrappers never raise).

A `tools/call` for a `{server}__{tool}` name that is not in the shared collection reaches the
aggregator's `CallToolHandler` fallback, which resolves the wrapper by name against the live index
and runs it (so a stale-but-valid name, or a name learned from `find_tools` on stateless HTTP,
just works). Only a name that cannot be resolved faults, and `AggregatorToolErrorFilter` turns
that fault into an `isError` result from `WrapperToolCatalog.BuildUnknownToolHintAsync` naming the
cause — unknown server with the registered names (the rename case, Q2/Q3), disabled server,
unknown tool with the server's real tool names, unreachable server — and the recovery. Covered by
`DownstreamToolWrapperTests`, `ListChangedEndToEndTests`, `UnknownToolHintTests` and
`AggregatorToolErrorFilterTests`.

### Q7 — Mutation → `ToolCollection` → `list_changed`

Every mutation that changes the wrapper set reconciles the collection under one
`DeferChangedEvents()` scope, so the SDK sees exactly one `Changed` per sync:

| Mutation | Signal to the catalog |
|---|---|
| `register_server`, `unregister_server`, `update_server` | `ServerRegistry.RegistryChanged` |
| `enable_service`, `disable_service` | `RegistryChanged` (`SetEnabledAsync` now raises it) |
| `refresh_service`, `update_server` with a transport change | `ToolIndex.ToolsChanged` (from `InvalidateCache`) |
| TTL refresh that finds a different tool set or schema | `ToolIndex.ToolsChanged` (fingerprint compare) |
| unavailable downstream at sync time | its wrappers are removed; retried on the next TTL sync |

A refresh that finds the same tools and schemas reuses the wrapper instances, so the collection
does not change and no notification is sent.

**How `list_changed` actually reaches a client (verified against SDK 2.2.0):**

- The SDK server subscribes to `ToolCollection.Changed` only on a **stateful** transport (stdio,
  stateful HTTP). On stateless HTTP the subscription is skipped and `tools.listChanged` is not
  advertised to `initialize`-era clients. `tools/list` still reads the live collection per request.
- For a client that negotiated a **pre-2026-07-28** protocol via `initialize` (Claude Desktop,
  Claude Code, rockbot today), the notification is a session-wide broadcast.
- For a **2026-07-28** client (the C# SDK's default), the notification is only fanned out to
  clients that opened a `subscriptions/listen` stream asking for `toolsListChanged` (SEP-2575).
  The SDK client does not open one on its own.

**Sessions on HTTP.** The 2026-07-28 revision removed protocol sessions and the `Mcp-Session-Id`
header (SEP-2567): `tools/list` must not vary per connection, and "servers that need cross-call
state use explicit, server-minted handles passed as ordinary tool arguments". For the aggregator
that means a 2026 client gets the minimal list and calls wrappers by name after `find_tools` —
progressive disclosure without a session, and no handle is needed because by-name dispatch is
stateless. Clients that still use the `initialize` handshake (Claude Desktop through mcp-remote,
Claude Code, rockbot today) only call tools they have listed, and the only way their list can grow
is a session that receives `list_changed`. The HTTP host therefore runs
`HttpServerSessionMode.StatefulForInitializeClients` (`McpAggregator:Http:SessionMode`): sessions
with `Mcp-Session-Id` for those clients, stateless requests for everyone else. The SDK marks
stateful mode a back-compat escape hatch for exactly this population; it will retire with them.
The stdio host gets the per-session broadcast on activation.

One more stateless-HTTP wrinkle, found during the manual check: the SDK builds a **fresh
`McpServerOptions` per request** through `IOptionsFactory`, and its options setup runs
`ToolCollection ??= []` before adding the attributed tools. The first cut of this change
post-configured the collection on the `IOptions` singleton only, so every stateless request got a
throwaway collection and `tools/list` showed the 14 attributed tools while the catalog reported
three active wrappers. `AddAggregatorMcpServer` now pre-assigns one shared collection in a
`Configure` registered before `AddMcpServer()`, so every options instance shares it.

### Q9 — Collisions

The prefix is load-bearing. Two servers with identical tool names produce distinct wrappers
(`WrapperNamingTests`, `WrapperToolCatalogTests.Eager_Sync_PopulatesCollectionWithWrappersFromEveryServer`).
Two tools on one server that sanitize to the same wrapper name keep the first and log the second.
A wrapper name that matches one of the aggregator's own tools is never added.

### Q10 — Prompt bridging

**Decision: proxy downstream prompts as real MCP prompts via `McpServerOptions.PromptCollection`.**
The aggregator is an MCP server, so it can preserve the tool/prompt distinction for hosts that
surface prompts natively, and `prompts/list_changed` follows the same collection mechanism as
tools. Bridging prompts as *tools* would work in every host but loses the distinction and doubles
the tool count. `get_prompt` stays as the escape hatch.

Shipped in [issue #40](https://github.com/MarimerLLC/mcp-aggregator/issues/40) as a mirror of the
tool pipeline inside the same types, so session state, sync scheduling and DI wiring are shared:

| Piece | Where | Role |
|---|---|---|
| `DownstreamPromptWrapper` | `Core/Tools` | `McpServerPrompt` subclass named `{server}__{prompt}`; carries `title`, `arguments`, `icons` unchanged, description prefixed `[server]`, `_meta.mcpAggregator = { serverId, serverName, promptName }`. Pre-flights `required` arguments; forwards through `ToolProxyHandler.GetPromptAsync`. |
| `ToolProxyHandler.GetPromptAsync` | `Core/Tools` | The single `prompts/get` path for wrappers and `get_prompt`: `DefaultToolTimeout`, `ConnectionManager` retry, `mcp_prompt_gets_total` / `mcp_prompt_get_duration_seconds` with the `via` tag (`wrapper` or `get_prompt`), and an unknown-prompt hint naming the server's real prompts. |
| `WrapperToolCatalog` (prompt side) | `Core/Services` | Builds prompt wrappers from `ToolIndex.GetPromptsForServerAsync` (reusing an instance when the argument fingerprint is unchanged), reconciles `PromptCollection` in Eager mode, keeps a per-session prompt activation set in Lazy mode and sends that session `prompts/list_changed`; `find_tools` scores prompts too and returns them under `prompts`. |
| `ToolIndex.PromptsChanged` | `Core/Services` | Raised on `InvalidateCache` and on a TTL re-fetch whose prompt names, titles, descriptions or arguments differ; the catalog schedules a sync on it as it does on `ToolsChanged`. |
| `PromptDetail.WrapperName` / `Protocol` | `Core/Models` | The proxied name (serialized, so `get_service_details` shows it) and the wire `Prompt` (not serialized). |
| `AddAggregatorMcpServer` | `Core/Configuration` | Pre-assigns one shared `McpServerPrimitiveCollection<McpServerPrompt>` before `AddMcpServer()` (with the `PostConfigure` guard) — the same stateless-HTTP fix as for tools, and also what makes the SDK advertise `prompts.listChanged` — plus a `ListPromptsHandler` that appends the session's activated prompts and a `GetPromptHandler` fallback that resolves any proxied prompt by name. |

Prompts have no `isError` result, so a missing required argument, an unknown name and an
unreachable server all surface as JSON-RPC errors whose message is written for the caller
(`McpErrorCode.InvalidParams` for the first two, `InternalError` for the last). `get_prompt` now
routes through the same proxy and returns those messages as `isError` results, the way
`invoke_tool` does.

One consequence of honoring `WrapperMode` for prompts, flagged rather than solved: hosts show
prompts to the *user*, and in Lazy mode a session's `prompts/list` is empty until the model calls
`find_tools` or `get_service_details`. Prompts do not count toward Claude Desktop's tool cap, so a
separate prompt mode (or always-Eager prompts) is a plausible follow-up if that turns out to matter.

Covered by `DownstreamPromptWrapperTests`, `PromptListChangedEndToEndTests` (the prompt twin of
`ListChangedEndToEndTests`, including the `subscriptions/listen { promptsListChanged }` path for
2026-07-28 clients), the prompt half of `WrapperToolCatalogTests`, `ToolIndexTests` and
`McpServerWiringTests`.

### Q11 — Skill documents

`data/skills/mcp-aggregator.md` now teaches the wrapper-first workflow. Per-server skill docs that
show `invoke_tool(...)` examples still work; rewrite them to the typed form as they are touched.
`SkillFingerprint` (tightened in [issue #41](https://github.com/MarimerLLC/mcp-aggregator/issues/41))
is a full SHA-256 over each tool's name, description and input schema (canonical JSON, object keys
sorted at every level so property order does not flap it) and each prompt's name, description and
arguments (name, description, required). `ToolIndex.ComputeFreshness` also compares the recorded
server version against the current one when a version was recorded, and reports `unknown` (not a
false `stale`) when the prompt fetch fails at read time; `SkillSnapshot.CaptureAsync` refuses to
bake a failed prompt fetch into the baseline, clears any earlier snapshot and returns `false` so
`update_skill` can say that no baseline was recorded. Fingerprints recorded before #41 are 16 hex
characters over names only; `SkillFingerprint.Matches` recognises that format and compares with the
old algorithm, so an upgrade does not flip existing skills to `stale`. The next `update_skill`
records the 64-hex format.

### Q12 — Resource bridging

**Decision: bridge downstream resources and resource templates as real MCP resources via
`McpServerOptions.ResourceCollection`**, under the URI `mcp-aggregator://{server}/{uri}`. After
#39 and #40 resources were the one primitive the aggregator did not surface at all; a downstream
that exposes documents, logs, schemas or templated lookups was invisible through it. Shipped in
[issue #45](https://github.com/MarimerLLC/mcp-aggregator/issues/45) as the third mirror of the
tool pipeline inside the same types.

| Piece | Where | Role |
|---|---|---|
| `ResourceUriNaming` | `Core/Tools` | `mcp-aggregator://{server}/{uri}` rewrite and reverse (`TryParse` / `IsFor`), content-URI rewrite of a `ReadResourceResult`, and a best-effort RFC 6570 matcher (`BuildTemplateMatcher`) because the SDK's own `UriTemplate` is internal. |
| `DownstreamResourceWrapper` | `Core/Tools` | `McpServerResource` subclass built from either a `Resource` or a `ResourceTemplate`. `ProtocolResourceTemplate.UriTemplate` is the rewritten URI (what the SDK keys the collection by); `ProtocolResource` is overridden to the full rewritten resource (keeps `size`) for plain resources and null for templates. Name, title, MIME type, annotations and icons carried through; description prefixed `[server]`; `_meta.mcpAggregator = { serverId, serverName, uri }`. `IsMatch` strips the prefix and compares ordinally or runs the matcher; `ReadAsync` strips the prefix, forwards through `ToolProxyHandler.ReadResourceAsync`, rewrites the content URIs back. |
| `ToolProxyHandler.ReadResourceAsync` | `Core/Tools` | The single `resources/read` path for wrappers and `read_resource`: `DefaultToolTimeout`, `ConnectionManager` retry, `mcp_resource_reads_total` / `mcp_resource_read_duration_seconds` with the `via` tag (`wrapper` or `read_resource`), and an unknown-resource hint listing the server's URIs and templates (and naming the `mcp-aggregator://` slip when the caller passed the aggregator form as `uri`). |
| `ToolIndex.GetResourcesForServerAsync` / `ResourcesChanged` | `Core/Services` | `resources/list` plus `resources/templates/list`, cached with a fingerprint over URI, name, title, description, MIME type and size. `MethodNotFound` on the list call means "no resources" (cached empty); on the templates call alone it means resources only. The event is raised on `InvalidateCache` and on a differing re-fetch. |
| `WrapperToolCatalog` (resource side) | `Core/Services` | Builds resource wrappers (reusing an instance when URI and fingerprint are unchanged), reconciles `ResourceCollection` in Eager mode through the same generic `Reconcile`, keeps a per-session resource activation set in Lazy mode and sends `resources/list_changed`; `ResolveResourceAsync` matches an exact plain resource first, then the first template the URI expands; `find_tools` scores resources over the aggregator URI, name, title, description and MIME type and returns them under `resources`. |
| `ResourceDetail` | `Core/Models` | `ServiceDetails.Resources`: `uri` (aggregator form), `downstreamUri`, `isTemplate`, name, title, description, MIME type, size; the wire objects are kept but not serialized. |
| `AddAggregatorMcpServer` | `Core/Configuration` | Pre-assigns one shared `McpServerResourceCollection` before `AddMcpServer()` (the stateless-HTTP fix again; a non-null collection is also what advertises `resources.listChanged`), a `ListResourcesHandler` / `ListResourceTemplatesHandler` that append the session's activated plain resources and templates, a `ReadResourceHandler` fallback that resolves any bridged resource by URI, and explicit subscribe/unsubscribe handlers that reject with `InvalidRequest`. |
| `read_resource` | `ConsumerTools` | Escape hatch taking the downstream URI or the aggregator form (checked against `serverName`); returns one `EmbeddedResourceBlock` per content with URIs in aggregator form; errors as `isError` results like `get_prompt`. |

**URI scheme.** `mcp-aggregator://{server}/{original-uri}` with the original verbatim, no
percent-encoding. `System.Uri` parses it with the server as host and the original as path, keeping
query and fragment, so the SDK's collection comparer (which parses plain keys as `System.Uri`)
round-trips it, and reversal is "strip the `mcp-aggregator://{server}/` prefix". Templates keep their
`{…}` expressions untouched (`mcp-aggregator://probe/file:///docs/{path}`) and stay ordinal keys.
The scheme is a fixed constant rather than `SelfName` because scheme characters exclude `_`. The
verbatim form was chosen over percent-encoding for readability and because templates survive it
unchanged; it is user-visible and durable — clients may store these URIs — so changing it later is
a breaking change.

**Subscriptions are not advertised.** The aggregator does not fan out `resources/updated`, so
`resources.subscribe` stays unset (the SDK's `WithSubscribeToResourcesHandler` would advertise it,
and the `PostConfigure` guard clears that) and the subscribe/unsubscribe handlers reject with
`McpErrorCode.InvalidRequest` and a message, so a client that tries anyway gets a truthful answer
instead of the SDK's silent no-op default.

**Unknown-resource error code.** The SDK's default splits on the negotiated revision and its helper
is internal, so the fallback handler does the same by ordinal date compare: `ResourceNotFound`
(-32002) for clients before `2026-07-28`, `InvalidParams` from that revision on. The message is the
catalog's hint (not aggregator form / unknown server / disabled / unreachable / no such resource
with the server's real URIs and templates).

**Fragment collisions.** The SDK collection compares plain URIs with `Uri ==`, which ignores the
fragment and the host's case, so two downstream URIs that differ only by fragment collapse to one
key. The catalog keeps the first and logs the second. Template matching is a best-effort RFC 6570
approximation; the downstream is authoritative, and a false positive only picks a same-server
wrapper, which forwards the URI identically.

**Timeouts.** Reads share `DefaultToolTimeout`. A dedicated read timeout for large blobs is a
follow-up, not solved here. The Lazy-mode caveat from Q10 applies to resources too: a host's
attachment picker is empty until the model calls `find_tools` or `get_service_details`.

Covered by `ResourceUriNamingTests`, `DownstreamResourceWrapperTests`,
`ResourceListChangedEndToEndTests` (the resource twin of `ListChangedEndToEndTests`, including the
`subscriptions/listen { resourcesListChanged }` path for 2026-07-28 clients, the error-code split,
the subscribe rejection and the `read_resource` escape hatch), the resource third of
`WrapperToolCatalogTests`, `ToolIndexTests`, `McpServerWiringTests` and `ToolSchemaTests`.

### Q13 — What goes in `ServerInstructions` (issue #44)

Since #28 the handshake `instructions` were a ~2.5 KB hardcoded header plus the self skill
document capped at 16 KB. The document grew past the cap (23 KB), so every `initialize` /
`server/discover` response carried ~18.5 KB that ended mid-sentence with a `[…truncated]` marker,
and most of it restated what `tools/list` already delivers (tool descriptions, admin parameter
tables) or the header itself.

**Decision: the handshake carries a short hand-written orientation only.** `AggregatorInstructions`
is a constant with just the aggregator's name interpolated: what the aggregator is, the three
naming conventions, the three-step workflow, the client-capability caveat with the `invoke_tool` /
`get_prompt` / `read_resource` escape hatches, "admin tools are hidden", "connections are lazy",
and a pointer to `get_service_skill(serverName: "mcp-aggregator")`. It names tools where the
workflow needs them but never repeats a tool's `[Description]` text or parameters.

- The skill document is **never embedded**. It stays the full, stand-alone reference behind
  `get_service_skill`, un-capped and never truncated. `LoadSelfSkill`, the 16 KB cap and the
  truncation marker are gone.
- **Tool call, not a resource, is the access path.** Tool calls are model-invocable on every host;
  resources are user-attached on Claude Desktop and ignored by many clients. Exposing skill
  documents as MCP resources is a separate follow-up.
- **No runtime cap setting.** A cap on a constant would guard nothing. `AggregatorInstructions.MaxChars`
  (6 KB) is pinned by `ServerInstructionsTests`, and the actual size is logged once at startup
  (`MCP server instructions: {Chars} chars, {Bytes} UTF-8 bytes`; a warning if over the ceiling).
  On stateless HTTP the options delegate runs per request, so the log is guarded with an
  interlocked flag.
- Behavioural change: hosts that relied on the embedded skill in the system prompt now have to
  call `get_service_skill` for error handling and admin details. The header keeps the two things
  that measurably mattered in the runs above (the workflow and the client-capability caveat).

Covered by `ServerInstructionsTests` (ceiling, pointer, no restated tool descriptions, same text on
every options instance) and `AdminDisclosureTests.Lazy_Handshake_IsShort_AndFullSkillStaysBehindGetServiceSkill`
(end to end: a 30 KB self skill never reaches the handshake but comes back whole from
`get_service_skill`).

Follow-ups from the first Claude Desktop round on 1.0.1, fixed in the same PR:

- With the skill out of the handshake, `get_service_skill` was named only in the connect string
  and the self entry's description, and an agent that goes straight to `find_tools` passes
  neither. The `list_services` description now carries the breadcrumb too
  (`ServerInstructionsTests.ListServicesDescription_CarriesTheSkillBreadcrumb`).
- `get_prompt` without a required argument came back as a bare "Tool execution failed": the
  required-argument pre-flight lived only in `DownstreamPromptWrapper`, and a downstream fault on a
  known prompt escaped `ConsumerTools.GetPrompt` as an unhandled `McpProtocolException` that the
  SDK sanitized. The pre-flight now runs in `ToolProxyHandler.GetPromptAsync` (both paths), and the
  escape hatch returns the downstream message with the prompt's real signature as an error result
  (`PromptListChangedEndToEndTests.GetPromptTool_MissingRequiredArgument_*` / `_DownstreamFault_*`).
- The self entry's admin tools reported `inputSchema: null` because `ToolIndex.OwnTools` built them
  from reflection (`AdminTools.Describe`) rather than from the built `AdminToolSet`, which carries
  the schemas. It now reads `AdminToolSet` when hosted with the MCP server and falls back to the
  schema-less reflection list otherwise (`ListServices_SelfEntry_DescribesTheAggregatorsOwnTools`).

## Measurements

### How to run them

`tools/McpAggregator.Measure` is a console harness that hosts the aggregator in-process over
stub downstreams mimicking the issue's inventory (`adjutant`, `onedrive-marimer`,
`onedrive-personal`, `microsoft-learn`; the stubs declare the real parameter shapes and validate
what they receive) and drives it with a model under three conditions:

| Condition | What the model sees |
|---|---|
| `eager` | `WrapperMode=Eager`: meta-tools plus every `{server}__{tool}` wrapper from the first turn |
| `lazy` | `WrapperMode=Lazy`: meta-tools only; `find_tools` / `get_service_details` activate wrappers and the tool list is re-read each turn, as a host honoring `list_changed` would. A cold aggregator per run, because activation is process-wide |
| `invoke_tool` | The pre-#39 surface: no `find_tools`, no wrappers, only `invoke_tool` with a stringified JSON argument object, and the old server instructions |

Each run is a plain agent loop (no auto tool invocation) so every call is observed. Recorded per
run: the first call that tried to reach a downstream and whether it hit the right tool with usable
arguments (**first-call success**), whether the right tool was eventually reached (**completed**),
steps and model turns, prompt and completion tokens, and per condition the `tools/list` byte size.

```bash
# any OpenAI-compatible server (OpenRouter, llama.cpp, Ollama, vLLM, LM Studio)
dotnet run --project tools/McpAggregator.Measure --   --endpoint https://openrouter.ai/api/v1 --api-key $KEY --model qwen/qwen3-8b   --runs 10 --no-think --out results.json --md results.md

dotnet run --project tools/McpAggregator.Measure -- --provider scripted   # validates the harness: 100% everywhere
dotnet run --project tools/McpAggregator.Measure -- --dry-run             # tools/list size per condition, no model
dotnet run --project tools/McpAggregator.Measure -- --help
```

`--no-think` turns model thinking off at the request level (`chat_template_kwargs.enable_thinking`
for llama.cpp/vLLM; override with `--no-think-json '{"reasoning_effort":"none"}'` for
OpenAI-style servers). `--provider azure` targets an Azure AI Foundry `.../models` endpoint.
`--real-docs` swaps the Microsoft Learn stub for the real server.

### Results

#### Run 1 — 2026-09-08, `qwen3.8` on llama.cpp (local network)

Full tables: [thinking off](measurements/2026-09-08-qwen3.8-thinking-off.md),
[thinking on](measurements/2026-09-08-qwen3.8-thinking-on.md).

Model as reported by the server: 27.3B parameters, IQ3_S quantization (3.4 bpw), 131k context.
**This is not an 8B-class model**, so it is a mid-tier data point, not the low-tier one the issue
asks for. 10 runs per task per condition, 5 tasks, stub downstreams, 300 runs total, zero
transport errors. The Lazy condition used a cold aggregator per run, which is equivalent to the
per-session activation that shipped afterwards for a single client.

**Reliability by path** (first downstream call hit the right tool with usable arguments; the task
was eventually completed):

| Condition | Thinking | First-call success | Completed | Wrong tool first |
|---|---|---:|---:|---:|
| Eager (wrappers listed) | off | 48/50 (96%) | 50/50 | 0 |
| Lazy (`find_tools` → wrapper) | off | 47/50 (94%) | 50/50 | 0 |
| `invoke_tool` (pre-#39 surface) | off | 49/50 (98%) | 50/50 | 0 |
| Eager | on | 47/50 (94%) | 50/50 | 0 |
| Lazy | on | 50/50 (100%) | 50/50 | 0 |
| `invoke_tool` | on | 47/50 (94%) | 50/50 | 0 |

Every single first-call miss, in every condition, was the same thing: the calendar task sent
`end` equal to `start` for "tomorrow" (a date-range semantics slip, corrected on the retry). Not
one run in 300 produced the failure the issue is about — a downstream call with missing or
misshapen arguments. This model authors the stringified JSON blob for `invoke_tool` as reliably as
it fills typed parameters, and it never confused the two OneDrive servers' identical `list_files`
tools (Q9). **For a model of this size the wrapper benefit is not reliability.**

**Cost and latency by path** (per task, all five tasks averaged):

| Condition | Thinking | Steps to done | Model turns | Input tokens | Output tokens | Wall time |
|---|---|---:|---:|---:|---:|---:|
| Eager | off | 1.0 | 1.0 | 4,288 | 52 | 1.1 s |
| Lazy | off | 2.0 | 2.0 | 7,599 | 79 | 3.0 s |
| `invoke_tool` | off | 3.0 | 3.0 | 10,259 | 123 | 3.9 s |
| Eager | on | 1.0 | 1.0 | 4,410 | 130 | 2.4 s |
| Lazy | on | 2.0 | 2.0 | 7,420 | 147 | 4.1 s |
| `invoke_tool` | on | 3.0 | 3.0 | 10,562 | 270 | 6.5 s |

The old surface costs three tool calls for every downstream action (`list_services` →
`get_service_details` → `invoke_tool`, exactly the discovery flow the old instructions taught),
**2.4× the input tokens and 3.4× the wall time of Eager**. Lazy costs one extra turn for
`find_tools` and lands in between on totals, while keeping the per-turn tool list at 7.8 KB
instead of 13.6 KB.

**`tools/list` size** (the per-turn context cost of the tool surface):

| Condition | Tools | Bytes |
|---|---:|---:|
| Eager | 27 (14 aggregator + 13 wrappers) | 13,593 |
| Lazy, before any activation | 14 | 7,754 |
| Lazy, after activating everything the five tasks touched | 23 | 11,969 |
| `invoke_tool` surface (no `find_tools`) | 13 | 7,004 |

With per-session activation a session pays only for the wrappers it activated, so a real Lazy
session sits near the 7.8 KB floor plus the servers it actually used.

**Thinking on vs off:** no reliability difference worth the name; roughly 2× the output tokens
and 1.5–2× the latency. A deployment that runs a low tier for cost would run it off.

**What this answers for rockbot #420:** Q1/Q8 — on token budget alone, typed wrappers beat the
generic proxy decisively, and Lazy is the right default when the host caps tools; Q9 —
prefixing resolved every collision; Q6 — the pre-flight and hints were never exercised by this
model because it never sent a malformed call. **Still open:** the reliability claim itself. It
needs a genuinely low-tier model (8B or smaller, or `gpt-4.1-nano` / `gemini-flash-8b` class),
which is a one-line change to the harness invocation once an endpoint exists.

#### Run 2 — 2026-09-08, three low-tier models via OpenRouter

Full tables: [qwen/qwen3-8b](measurements/2026-09-08-openrouter-qwen3-8b.md),
[meta-llama/llama-3.1-8b-instruct](measurements/2026-09-08-openrouter-llama-3.1-8b.md),
[openai/gpt-4.1-nano](measurements/2026-09-08-openrouter-gpt-4.1-nano.md). Same harness, tasks
and stubs as run 1; reasoning off; 10 runs per task per condition. Provider-side HTTP 400s (all
ten `invoke_tool` runs of `docs_search` on qwen3-8b, five scattered runs on llama) are excluded
from the rates below and noted in the per-model files.

**This is the low tier the issue is about, and the picture flips.**

| Model | Condition | First-call success | Completed | Input tok / task | Time / task |
|---|---|---:|---:|---:|---:|
| qwen/qwen3-8b | Eager | **50/50 (100%)** | 50/50 | 4,001 | 1.5 s |
| qwen/qwen3-8b | Lazy | **50/50 (100%)** | 50/50 | 6,801 | 2.9 s |
| qwen/qwen3-8b | `invoke_tool` | 10/40 (25%) | 10/40 (25%) | 7,065 | 4.0 s |
| llama-3.1-8b-instruct | Eager | 20/50 (40%) | 22/50 (44%) | 4,669 | 0.5 s |
| llama-3.1-8b-instruct | Lazy | 17/48 (35%) | 32/48 (67%) | 10,642 | 3.6 s |
| llama-3.1-8b-instruct | `invoke_tool` | **0/47 (0%)** | 5/47 (11%) | 10,036 | 3.5 s |
| gpt-4.1-nano | Eager | **45/50 (90%)** | 49/50 (98%) | 2,684 | 2.0 s |
| gpt-4.1-nano | Lazy | 30/50 (60%) | 38/50 (76%) | 4,769 | 17.1 s |
| gpt-4.1-nano | `invoke_tool` | 5/50 (10%) | 22/50 (44%) | 18,482 | 7.0 s |

**What the models actually did on the old surface** (from the per-run records):

- **qwen3-8b** never did discovery. It invented server names — `email-service`,
  `mcp-aggregator` — in 20 of 40 runs, refused the calendar task outright in 10 ("I don't have
  access to your calendar"), and completed only `list_files_personal`. It did not retry after
  "Server 'x' not found." even once. With the typed tools it was perfect: one call, correct
  arguments, every task, in both modes.
- **llama-3.1-8b** produced the exact failure trace from rockbot #420: `invoke_tool({})`, no
  arguments at all, in 22 of 47 runs, then wandered off registering imaginary servers with
  `register_server`. On the typed surface its arguments were right, but the OpenRouter provider
  returned about half of its tool calls as plain text (`<adjutant__send_email>{"to":
  ["alice@example.com"], …}</function>`) rather than as function calls, which no host can
  execute. Inside that text the arguments were correct in most cases. That is a model/template
  problem, not an aggregator one, and Eager's 40% is the ceiling it allows.
- **gpt-4.1-nano** did discovery correctly, then in 15 of 50 runs passed the typed name it had
  just read from `list_services` (`adjutant__send_email`) as the downstream `toolName`, and in
  others sent `to` as a string where the schema wants an array. On the typed surface it used the
  right shapes. Its Eager misses were `adjutant__list_accounts` before `send_email` — arguably
  reasonable (pick an account first), scored as a miss by the strict first-call metric — and its
  Lazy misses were mostly answering after `find_tools` without making the second call.

These runs used the build after the first two escape-hatch fixes ("Server not found" as a
result instead of an opaque fault; the type-mismatch schema hint) and before the three that
followed from them (registered-server list on unknown server, the wrapper-name-as-`toolName`
explanation, non-JSON `arguments` reported). The `invoke_tool` condition was then re-run with
those in place
([qwen3-8b](measurements/2026-09-08-openrouter-qwen3-8b-invoke-tool-after-fixes.md),
[llama-3.1-8b](measurements/2026-09-08-openrouter-llama-3.1-8b-invoke-tool-after-fixes.md),
[gpt-4.1-nano](measurements/2026-09-08-openrouter-gpt-4.1-nano-invoke-tool-after-fixes.md)):

| Model, `invoke_tool` surface | First-call success | Completed before fixes | Completed after fixes | Steps to done |
|---|---:|---:|---:|---:|
| qwen/qwen3-8b | 10/50 (20%) | 10/40 (25%) | **40/50 (80%)** | 3.5 |
| llama-3.1-8b-instruct | 2/46 (4%) | 5/47 (11%) | 13/46 (28%) | 3.2 |
| gpt-4.1-nano | 7/50 (14%) | 22/50 (44%) | 23/50 (46%) | 4.7 |

The first call is as bad as before — the fixes are about what happens after it — but the
recovery text now works for the model that reads it: every qwen3-8b run that guessed a server
name recovered once the error listed the registered servers, and its only remaining failures are
the ten calendar refusals where it never called a tool at all. nano did not move: its stuck runs
never reach a downstream (it loops on `list_services` / `get_service_details` until the step
budget is gone), so there is no error to recover from. Even at its best, recovery on the old
surface costs 3–5 calls and 2–3× the tokens of one typed call.

**What this answers for rockbot #420:**

- **Q1 (reliability):** for 8B-class models the typed surface is the difference between working
  and not working: 100% vs 25% (qwen3-8b), 90% vs 10% (gpt-4.1-nano), 40% vs 0% (llama-3.1-8b)
  on the first downstream call. The mid-tier result from run 1 stands as the other end of the
  curve: a 27B model does not need the wrappers for correctness, only for cost.
- **Q8 (token budget):** Eager is the cheapest per task for every model, because it is one call.
  Lazy costs one extra turn and, for the weakest models, that turn is where they drop the ball
  (nano 60% vs 90%; llama's `find_tools` runs often ended in text instead of the second call).
  When the host's tool cap allows Eager, use Eager for a low tier; use Lazy when it does not, and
  accept the extra turn.
- **Q6 (recovery):** error text that names the fix does help the models that read it — listing
  the registered servers took qwen3-8b's old-surface completion from 25% to 80% — but it costs
  3–5 calls per task, some models never reach an error to recover from (nano's loops), and none
  of it matters on the typed surface, where the first call is right. The hints are worth
  shipping; the durable fix is not giving the model a call shape it can get wrong.
- **Q9:** no model confused the two OneDrive servers on the typed surface.

#### Real host: Claude Desktop (2026-09-09, HTTP host 1.0.0 via mcp-remote 0.8.6)

Lazy mode, `StatefulForInitializeClients`. The pod log shows the transport half working exactly
as designed: Desktop negotiated 2025-06-18 and got an `Mcp-Session-Id`; `find_tools` activated 3
wrappers and `get_service_details` 6 more, and after each activation the client re-fetched
`tools/list` within the same second (8 → 11 → 17 tools for that session; a second Desktop
identity on the same endpoint stayed at 8). Every recovery path held: missing-parameter hint,
unknown-tool hint with the server's real tool names, `refresh_service` followed by a clean lazy
reconnect.

The application half did not: Desktop chat builds the conversation's tool index once and
validates names against it before dispatch, so `csla__version()` failed client-side with
"Tool 'csla__version' not found" and never reached the aggregator (no `tools/call` in the log).
All seven downstream calls in that conversation went through `invoke_tool`, all correct. So on
Claude Desktop chat, a wrapper activated mid-conversation is unreachable until the next
conversation, and the skill document's original "prefer typed; invoke_tool is the fallback"
cost the model a guaranteed failed turn. The skill document, server instructions and runtime
hints now carry a client-capability caveat: if the client rejects a typed name as not found,
switch to `invoke_tool` for the rest of the conversation. Desktop also renders content blocks
with no separator, which is why the argument-mismatch hint block now starts with a paragraph
break.

#### Not yet measured

- Whether Claude Desktop offers the activated wrappers at the start of the *next* conversation
  on the same mcp-remote session (the session's list already contains them).
- Whether Claude Code and rockbot's client refresh their tool index on `list_changed`
  mid-conversation.
- What Claude Desktop does at the 44-tool cap in Eager mode.
- The rename drill on a real host (register, activate, re-register under a new name).

### Templates for the remaining runs

#### Reliability: first-call success by path and model tier

Fixed task set: `adjutant/send_email`, `adjutant/get_calendar_events`, `onedrive-*/list_files`,
`microsoft-learn/microsoft_docs_search`. Ten runs each. Source: `mcp_tool_invocations_total`
grouped by `via` and `result`.

| Model tier | Model | Path | Attempts | First-call success | Notes |
|---|---|---|---|---|---|
| High | | `wrapper` | | | |
| High | | `invoke_tool` | | | |
| Low | | `wrapper` | | | |
| Low | | `invoke_tool` | | | |

#### Token cost

| Mode | `tools/list` bytes | Tools listed | Prompt-token delta per turn |
|---|---|---|---|
| Eager | | | |
| Lazy (before activation) | | | |
| Lazy (after `find_tools`) | | | |

| Handshake payload | `ServerInstructions` chars | UTF-8 bytes | Notes |
|---|---|---|---|
| Before #44 | ≈ 18,900 | ≈ 19,000 | 2.5 KB header + self skill capped at 16 KB, cut mid-text with `[…truncated]` |
| After #44 | 1,588 | 1,588 | orientation only; skill behind `get_service_skill` |

#### Host behavior

| Host | Protocol negotiated | Honors `list_changed` | Behavior at the 44-tool cap (Eager) | Activated-then-removed tool |
|---|---|---|---|---|
| Claude Desktop (chat, mcp-remote 0.8.6) | 2025-06-18 | transport yes (re-lists within 1 s); conversation tool index no | | |
| Claude Code | | | | |
| rockbot client | | | | |
| C# SDK client (tests) | 2025-06-18 / 2026-07-28 | yes (broadcast) / yes (with `subscriptions/listen`) | n/a | removed from next `tools/list` |
| Stateless HTTP | any | no | n/a | removed from next `tools/list` |

#### Rename drill

Register `calendar-mcp`, activate its wrappers, unregister, register the same transport as
`adjutant`. Expected: `calendar-mcp__*` gone, `adjutant__*` present, `id` differs, one
`list_changed` per step. Record what each host shows.
