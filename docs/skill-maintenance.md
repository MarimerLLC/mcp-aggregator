# Skill-document maintenance

Design note for issues [#56](https://github.com/MarimerLLC/mcp-aggregator/issues/56) (phase 1),
[#57](https://github.com/MarimerLLC/mcp-aggregator/issues/57) (phase 2),
[#58](https://github.com/MarimerLLC/mcp-aggregator/issues/58) (phase 3) and
[#59](https://github.com/MarimerLLC/mcp-aggregator/issues/59) (phase 4).

## The problem

A skill document is hand-written guidance for one downstream server, served by
`get_service_skill`. Downstreams change under it. Adjutant went from 1.7.1 to 1.8.4 with
all-day events as floating dates, local-day calendar windows, per-account `warnings`, new ids
after `move_email`, and new parameters on four tools. Its skill document kept describing 1.7.1
for two months.

The aggregator already *detects* this. When a skill is saved, `SkillSnapshot` records the
server's version and a fingerprint of its tools and prompts (`SkillFingerprint`, issues #28 and
#41). `ComputeFreshness` then reports `fresh`, `stale` or `unknown`. But:

- **only `list_services` and `get_service_details` carry the flag.** The call an agent makes
  before using a server, `get_service_skill`, returns the old document with no warning;
- **the fingerprint is a hash.** The aggregator knows *that* the catalog drifted, not *how*,
  so it can't tell a caller what changed or give an LLM anything to revise from;
- **nobody fixes it** unless a person notices and rewrites the document by hand.

## Phases

| Phase | Issue | What it adds | LLM |
|---|---|---|---|
| 0 | #53 / PR #55 | Tools-only HTTP servers get a freshness baseline at all | no |
| 1 | #56 | Baseline catalog snapshot, `skillDrift`, stale notice in `get_service_skill` | no |
| 2 | #57 | `refresh_skill`: LLM-revised draft, validation, history, `restore_skill` | yes |
| 3 | #58 | Background upkeep with `Off` / `Notify` / `Propose` / `Auto` modes | yes |
| 4 | #59 | Change sources: release notes, server guides, documentation resources | yes |

Each phase is one PR and is useful on its own. Phase 1 is the foundation: the snapshot it
records is the input every later phase reads.

## Phase 1: baseline catalog and drift (#56)

### Baseline snapshot

`SkillSnapshot.CaptureAsync` runs whenever a skill is saved (`update_skill`, REST
`PUT /api/admin/servers/{name}/skill`). It now also writes `skills/{name}.baseline.json`
through `SkillStore`:

```json
{
  "remoteVersion": "1.8.4",
  "remoteInstructions": "Adjutant exposes email, calendar, and contacts tools ...",
  "recordedAt": "2026-11-06T19:54:27Z",
  "tools":   [{ "name": "create_event", "description": "...", "inputSchema": { ... } }],
  "prompts": [{ "name": "daily_briefing", "description": "...", "arguments": [ ... ] }]
}
```

- A failed capture deletes the file, as it already clears the fingerprint.
- Deleting the skill or unregistering the server deletes it.
- `k8s/deploy.sh` copies only `*.md`, so it never overwrites a live baseline.

### Drift

`SkillDriftCalculator.Compute(server, baseline, tools, prompts)` compares the snapshot with the live catalog:

| Field | Meaning |
|---|---|
| `recordedVersion` / `currentVersion` | Version the skill was written for, and the version now |
| `baselineAvailable` | False for skills recorded before #56: only the version comparison is possible |
| `instructionsChanged` | The server's `initialize` instructions differ (line endings and outer whitespace ignored) |
| `toolsAdded` / `toolsRemoved` | Names |
| `toolsChanged[]` | `name`, `descriptionChanged`, `parametersAdded`, `parametersRemoved`, `parametersChanged`, `otherSchemaChanges` |
| `promptsAdded` / `promptsRemoved` / `promptsChanged[]` | Same shape; arguments stand in for parameters |

Schemas are compared on their canonical form, as `SkillFingerprint` does: property order is
ignored. A parameter is "changed" when its sub-schema or its `required` membership differs.
Anything else (top-level keywords, `additionalProperties` and the like) is
`otherSchemaChanges`.

### Freshness

The existing rules are unchanged: no fingerprint means `unknown`; unreadable tools or prompts
mean `unknown`; a version or fingerprint mismatch means `stale`. One rule is added: when a
baseline file exists and the server's instructions differ from it, the skill is `stale`.
Instructions are the server's own orientation and often carry exactly the behavior notes a
skill repeats. Skills without a baseline file can't make that comparison and keep the old
behavior.

### Surfacing it

- **`get_service_details`** gains `skillDrift`, which is non-null only when `skillFreshness` is
  `stale`.
- **`get_service_skill`** puts a notice in front of a stale document:

  ```markdown
  <!-- aggregator:skill-notice -->
  > **This skill document may be out of date** (the aggregator's freshness check reports it stale).
  > It was written for version 1.7.1 (2026-09-09); the server now reports 1.8.4.
  > Changes since then:
  > - tool `create_event`: new parameter `isAllDay`
  > - tool `get_calendar_events`: new parameter `includeAttendeeDetails`; description changed
  > - server instructions changed
  > Where this document and the live tool schemas disagree, trust the schemas (`get_service_details`).
  <!-- /aggregator:skill-notice -->
  ```

  The list is capped (12 lines, then "and N more"). The check is best-effort and time-boxed
  (10 s). If the server can't be read, the document comes back unchanged rather than failing.
- **Round trips are safe.** `update_skill` and the REST `PUT` strip a leading notice block, so
  fetching with `get_service_skill` and saving the result never stores the notice.
- **REST `GET /api/services/{name}/skill`** stays raw markdown: it's the editing surface.

## Phase 2: LLM revision on demand (#57)

`SkillMaintainer` asks a model for a **minimal edit** of the current document. Its inputs are
the phase 1 drift, the current catalog and the server's instructions. The aggregator already
holds an `IChatClient` (`McpAggregator:AI`, used by `SummaryGenerator`). Skill revision gets
its own `McpAggregator:SkillMaintenance` options (model, timeout, output limit), defaulting to
the same endpoint.

`refresh_skill(serverName, apply = false)` returns the draft, a diff and a validation report.
Nothing is saved unless `apply: true`.

Validation rejects a draft that does any of these:

- still names a removed tool or prompt;
- ignores an added tool without saying why;
- falls outside the length bounds;
- introduces URLs or addresses that aren't in its inputs.

Every save keeps the previous version under `skills/history/{name}/`. `restore_skill` rolls back.

**Version-only drift** is a new version with an identical catalog. The model has nothing to work
from, so `refresh_skill` offers to re-baseline without calling it.

## Phase 3: background upkeep (#58)

A hosted service checks freshness on a timer and after `refresh_service`. It respects a daily
revision cap and backs off from servers that keep failing validation. The mode is set by
`SkillMaintenance:Mode`:

- `Off`: nothing.
- `Notify`: phase 1 only. This is the appsettings default.
- `Propose`: drafts wait for `apply_skill_draft` / `discard_skill_draft`.
- `Auto`: validated drafts are published, with history kept.

Version-only drift is re-baselined without the model in `Propose` and `Auto`.

## Phase 4: change sources (#59)

A catalog diff can't see behavior changes. Most of Adjutant 1.8.x's changes were in its
changelogs, not its schemas. Each server can name optional `changeNotes` (a URL or GitHub path),
a `guideTool` (e.g. Adjutant's `get_guide`) and `guideResources`. These feed the model, are
size-capped, and are treated as untrusted data.

## Trust

Tool descriptions, instructions and change notes come from downstream servers. Today a model
reads them as tool metadata. An LLM-revised skill document turns them into guidance that
carries the aggregator's authority, which is a prompt-injection amplifier. The mitigations:

- the revision prompt treats every input as data;
- validation is mechanical, not model-judged;
- every revision is in history and can be rolled back;
- machine-revised documents carry a provenance line;
- the live instance starts in `Propose`.
