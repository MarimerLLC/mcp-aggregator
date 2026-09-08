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

Activation is **process-wide**, not per session. Everything the SDK offers is process-wide too:
there is one `ToolCollection` per `McpServerOptions` instance, and both hosts share the
`IOptions<McpServerOptions>` singleton with the server.

Defaults: the stdio host keeps `Lazy` (Claude Desktop caps the total tool count at roughly 44
across all servers; eager registration of a 55-tool inventory would blow that on its own). The
HTTP host's `appsettings.json` sets `Eager`, for the reason under Q7.

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

`invoke_tool` and `get_prompt` remain, described as escape hatches. The REST invoke endpoint is
unchanged and still uses the generic path. The self skill and the server instructions steer the
model to `find_tools` and the typed tools first.

### Q5 — Wisp interaction

No aggregator analog. Anything that routes through `invoke_tool` keeps working unchanged.

### Q6 — Recovery and self-correction through wrappers

The SDK's argument binding does not run for a wrapper (it is a direct `McpServerTool` subclass,
not an `AIFunction`), so the wrapper does its own pre-flight: any key in the schema's `required`
that is absent produces an `isError` result naming the parameter and embedding the schema, and the
downstream is never called. Everything else is the shared proxy path, so a downstream `isError`
still carries the argument hint. `AggregatorToolErrorFilter` is unaffected (it only converts
binding `ArgumentException`s, which wrappers never raise). Covered by
`DownstreamToolWrapperTests` and `ListChangedEndToEndTests`.

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

So on the HTTP host, Lazy activation is invisible to a client until it re-lists on its own, which
is why that host defaults to `Eager`. The stdio host gets the broadcast.

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

**Decision: proxy downstream prompts as real MCP prompts via `McpServerOptions.PromptCollection`**,
in a follow-up issue. The aggregator is an MCP server, so it can preserve the tool/prompt
distinction for hosts that surface prompts natively, and `prompts/list_changed` follows the same
collection mechanism as tools. Bridging prompts as *tools* would work in every host but loses the
distinction and doubles the tool count. `get_prompt` stays as the escape hatch meanwhile.

### Q11 — Skill documents

`data/skills/mcp-aggregator.md` now teaches the wrapper-first workflow. Per-server skill docs that
show `invoke_tool(...)` examples still work; rewrite them to the typed form as they are touched.
`SkillFingerprint` was left as is (tool and prompt names): a rename already changes the server
entry, and a schema change is caught by the index fingerprint rather than the skill fingerprint.

## Measurements

Pending. The implementation ships with the `via` tag so these can be split by path. Fill in when
a hosted low-tier model key (Azure AI Foundry or OpenRouter) is available.

### Reliability: first-call success by path and model tier

Fixed task set: `adjutant/send_email`, `adjutant/get_calendar_events`, `onedrive-*/list_files`,
`microsoft-learn/microsoft_docs_search`. Ten runs each. Source: `mcp_tool_invocations_total`
grouped by `via` and `result`.

| Model tier | Model | Path | Attempts | First-call success | Notes |
|---|---|---|---|---|---|
| High | | `wrapper` | | | |
| High | | `invoke_tool` | | | |
| Low | | `wrapper` | | | |
| Low | | `invoke_tool` | | | |

### Token cost

| Mode | `tools/list` bytes | Tools listed | Prompt-token delta per turn |
|---|---|---|---|
| Eager | | | |
| Lazy (before activation) | | | |
| Lazy (after `find_tools`) | | | |

### Host behavior

| Host | Protocol negotiated | Honors `list_changed` | Behavior at the 44-tool cap (Eager) | Activated-then-removed tool |
|---|---|---|---|---|
| Claude Desktop | | | | |
| Claude Code | | | | |
| rockbot client | | | | |
| C# SDK client (tests) | 2025-06-18 / 2026-07-28 | yes (broadcast) / yes (with `subscriptions/listen`) | n/a | removed from next `tools/list` |
| Stateless HTTP | any | no | n/a | removed from next `tools/list` |

### Rename drill

Register `calendar-mcp`, activate its wrappers, unregister, register the same transport as
`adjutant`. Expected: `calendar-mcp__*` gone, `adjutant__*` present, `id` differs, one
`list_changed` per step. Record what each host shows.
