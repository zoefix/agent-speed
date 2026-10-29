# Execution mode metadata

This contract adds metadata and grouping to the existing measurement engine. Token normalization, request identity,
timing validation, speed windows, estimates and pricing equations remain unchanged. A missing mode is never Standard.

## Common facts

Both platform cores expose `ExecutionModes` containing at most 20 `ExecutionModeFact` values. A fact has:

| Field | Values |
| --- | --- |
| `kind` | `serviceTier`, `accelerator`, `modelVariant`, `routing`, `workflow`, `effort` (reasoning effort, see below) |
| `value` | Canonical lower-case mode token; known examples are `standard`, `fast`, `ultrafast`, `highspeed`, `balanced`, `deep`, `planning`, `auto`. New valid tokens remain distinct. |
| `rawValue` | Native token, retained locally; never a configuration document or a path. |
| `evidence` | `configured`, `selected`, `recorded`, `served` |
| `provider` | Optional safe provider identifier; never a URL or credential. |
| `scope` | `configuration`, `session`, `turn`, `request`, `response` |

All strings are bounded safe ASCII identifiers: value/rawValue at most 64 characters, provider at most 32,
matching `[A-Za-z0-9][A-Za-z0-9._:-]*`. Reject other values rather than displaying or reporting arbitrary source text.
An absent/null field does not manufacture a fact. Unmapped `auto`, `inherit`, `inherited`, `unknown` and `none`
remain unresolved selections; an adapter may map a native explicit off value only when its semantics are verified.
An explicit clear must clear the corresponding selected context; absence must not clear or override it.
Configured facts are source information only and never become a response's selected/served fact implicitly.
Facts are provider-aware: OpenAI `default` -> `standard`, `priority` -> `fast`; Anthropic Fast comes from
`usage.speed`, not `usage.service_tier`. Do not infer modes from throughput or arbitrary model names; reasoning effort
is its own kind (`effort`) and never implies a speed mode.

`ResponseRecord.modes` / `ParsedResponse.Modes`, `AgentStatus.modes` and source-level configured modes use this type.
Empty is the backwards-compatible default. Display configured, selected/recorded, and served evidence separately.
Only explicit native inheritance may propagate selections to child agents; served evidence never propagates.
Mode changes affect their native scope and time, not earlier responses. Upserts keep the original request identity.

## Native adapters

- Codex: read an explicit supported transcript mode field if present. Read the declared user `config.toml`
  `service_tier` as configured information only, with model-catalog labels where available. Do not launch/resume a
  thread, attach to an unverified control interface, reconstruct all config layers, or promote recent-composer values.
  Only an unambiguous root string literal is accepted; multiline strings, duplicate declarations and unsupported
  syntax stay unknown. A declared default is not a claim about a running task's effective configuration.
- Claude Code/Desktop: retain explicit `message.usage.speed` `fast` and `standard` as served service-tier facts.
  Reasoning effort and ultracode: see *Reasoning effort and speed*.
  Missing speed is not Standard. Preserve the existing pricing fast flag's compatibility behavior.
- OpenCode: `session_message.data.native.serviceTier` is served OpenAI tier metadata for supported versions.
  Reasoning effort: see *Reasoning effort and speed*.
  Native provider IDs `openai` and `azure` use the `openai` semantic namespace. Other provider IDs require their
  own mapping; custom names and variant names alone are not speed evidence.
- omp: `service_tier_change.serviceTier` is a provider-family map on the native journal branch; selections are
  branch/time scoped. Assistant `message.serviceTier` is recorded evidence because the harness may substitute the
  requested tier when a response echo is absent. Respect explicit rejection/fallback and child overrides.
- ZCode: user-message `metadata.highspeed` is selected `accelerator:highspeed` for that turn. Actual provider/model
  routing on an attempt is recorded evidence, not a provider response echo; a served fact requires explicit response
  metadata. Do not treat the full turn as accelerated after
  fallback, propagate an overlay to forks/subagents, or count `hsc_mock_*` metadata as served acceleration.
- Kimi: exact official IDs `k3.1-pro-highspeed` and `kimi-for-coding-highspeed` are selected `modelVariant:highspeed`.
  The recorded model is requested metadata, not proof of the provider's served model. No substring matching.
- WorkBuddy/WorkBuddy AI: `providerData.requestModelId` `fast-model`, `balanced-model`, `deep-model` are selected
  routing facts. A synthetic compatibility `service_tier:"standard"` is not served evidence.
- Antigravity: Planning/Fast is a workflow dimension only when its actual native field is known. Turbo permissions,
  animation speed and effort are excluded. Unknown protobuf tier semantics remain unclassified.
- Gemini CLI, Grok Build, Trae: expose the common unknown state until an explicit supported native field is present;
  a provider API's capability alone is not evidence that its harness selected it.
- DSH: `request/header` `data.header.config.reasoningEffort` is the effort the harness sent with that request (see
  *Reasoning effort and speed*). Observed on DeepSeek Harness 0.2.1 (Windows, 2026-10-25): `max`; headers of older
  sessions have no such field.
- Factory Droid: each assistant message's `message.reasoningEffort` is the effort of that call (see *Reasoning effort
  and speed*). Observed in issue #33 (droid 0.250.0) and in public recordings of droid and Factory Desktop.

## Reasoning effort and speed

What the agent rows, the floating windows and the history show per agent and per response (DESIGN 4, 4b, 2c) is two
values derived from the facts above: the **reasoning effort** and the **speed**. Both are verified against real logs
(Claude Code 2.1.219+, Claude Desktop, Codex desktop/CLI with `thread_settings_applied`, OpenCode 2.x, DeepSeek
Harness 0.2.1; Factory Droid from public recordings, METRICS 4.16); every other source has no effort until its native
field is verified.

### Effort facts

`effort` is a fact kind. Its adapter (`adapter: "effort"`, shared fixture `execution-mode-cases.json`) takes the
native token: it must be a safe identifier (Common facts); it is lower-cased; `none` becomes `off` (reasoning
disabled); `auto`, `default`, `inherit`, `inherited` and `unknown` produce no fact; every other token is kept as is
(future tokens stay distinct). Effort facts have no provider.

| Value | Meter level (0–5) | Meaning |
| --- | --- | --- |
| `off` | 0 | reasoning off |
| `minimal`, `low` | 1 | |
| `medium` | 2 | |
| `high` | 3 | |
| `xhigh` | 4 | |
| `max` | 5 | |
| `ultra` (Codex) | 5, top rung | maximum reasoning with automatic task delegation |
| `ultracode` (Claude Code) | 5, top rung | maximum effort with multi-agent workflows |
| any other token | none (word only) | |

Display order (history, ties): `ultracode`, `ultra`, `max`, `xhigh`, `high`, `medium`, `low`, `minimal`, `off`, other
tokens in ordinal order, unknown last.

### Speed

`speed` is not a fact kind; it is derived from a fact set: the attribution of `serviceTier` when it is resolved, else
of `accelerator`, else of `modelVariant` when that value is `highspeed`, else unknown. Its value keeps the fact's
canonical token (`standard`, `fast`, `ultrafast`, `highspeed`, `flex`, `scale`, …), evidence and provider. Bolts:
`fast` and `highspeed` 1, `ultrafast` 2, everything else 0. Display order: `ultrafast`, `fast`, `highspeed`,
`standard`, other tokens in ordinal order, unknown last.

Attribution of either value follows *Local aggregates* (served, then recorded, then selected; scope breaks ties;
configured never counts). A value is shown only when it is known; unknown is never drawn as Standard or as a level.

### Native rules

Facts live in two places per agent: the *context* (facts that apply from their time on) and each response's own facts
(a snapshot taken when the response is created; Claude merges the facts of every line of the message, a later fact
replacing one of the same kind, evidence, provider and scope). An agent's *current* effort and speed are attributed
from its current facts, below.

- **Claude Code and Claude Desktop** (per transcript file):
  - Assistant line effort: `perTurnEffort` when it is a string, else top-level `effort` when it is a string (both are
    absent on some third-party-model Desktop transcripts: no fact). Fact `effort`, evidence `recorded`, scope
    `request`. Lines that are skipped (`<synthetic>`, keys owned by another file) contribute nothing.
  - Ultracode, session: an `attachment` line whose `attachment.type` is `ultra_effort_enter` turns it on,
    `ultra_effort_exit` turns it off (Claude Code repeats `enter` as a sparse reminder while it stays on). Ultracode,
    turn: `attachment.type == "workflow_keyword_request"` turns it on; the next *prompt line* turns it off. A prompt
    line is a `user` line whose `isMeta` is not `true` and whose `message.content` is a string or contains a block of
    type `text` (interrupt markers included; tool results and the meta lines Claude adds after a prompt are not).
  - While either ultracode is on, an assistant line's effort fact is `ultracode` (evidence `recorded`, scope
    `request`) instead of its recorded token.
  - Speed: `message.usage.speed` (served service tier, provider `anthropic`, scope `response`), as above.
  - Current: the facts of the latest accepted response, with the effort replaced by `ultracode` while ultracode is on,
    otherwise by the latest accepted assistant line's own recorded effort. Ultracode never propagates to sub-agents
    (their own files record their own effort).
- **Codex** (per rollout; copied history, METRICS 3.5, is ignored as usual):
  - `event_msg` with `payload.type == "thread_settings_applied"`: for each of `thread_settings.service_tier` (adapter
    `serviceTier`, provider `openai`: `default` → `standard`, `priority` → `fast`, `ultrafast`) and
    `thread_settings.reasoning_effort` (adapter `effort`) that is present, the context's fact of that kind with
    evidence `selected` and scope `session` is removed and, when the value is a string, replaced. An explicit `null`
    clears it; a missing field changes nothing.
  - `turn_context` starts a turn: every context fact with scope `turn` is removed; then `payload.effort` (else
    `payload.collaboration_mode.settings.reasoning_effort`) adds an `effort` fact (evidence `recorded`, scope `turn`)
    and `payload.service_tier`, when it is a string, a selected turn tier (as before).
  - A response (`token_usage_record` or legacy `token_count`) takes a snapshot of the context; a
    `token_usage_record` `payload.service_tier` string adds a recorded request tier to that snapshot only.
    Request facts never enter the context, and responding never clears turn or session facts.
  - Current: the context. Settings are not inherited by sub-agent rollouts.
- **OpenCode**: every assistant row (in flight or not) and every compaction row carries `data.model.variant`
  (adapter `effort`, evidence `recorded`, scope `request`; `default` gives no fact) and, for providers `openai` and
  `azure`, `data.native.serviceTier` (served, as above). A response's facts are its row's. Current: the facts of the
  session's latest assistant row (fork copies, METRICS 4.5, are skipped), so an in-flight request already shows its
  effort.
- **Pi** (per session file; entries METRICS 4.13 skips contribute nothing): the context holds one effort fact. A
  `thinking_level_change` entry's `thinkingLevel` (adapter `effort`, evidence `selected`, scope `session`) replaces
  it; an accepted assistant message's `thinkingLevel` (adapter `effort`, evidence `recorded`, scope `request`; Pi's
  levels `off`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`) is that response's fact and replaces it too, so
  the later of the two is current. A response takes a snapshot of the context after its own message's fact (so a
  message without `thinkingLevel`, written by pi-ai before 0.99, takes the selected level); side usage (METRICS 4.13)
  carries no facts. `providerThinkingLevel` (the provider's native token) is not used. No speed facts. Current: the
  context.
- **OpenClaw** (METRICS 4.14): no facts. Its `thinking_level_change` entries are written when a session starts and when
  a level is selected explicitly, not for the level a single message asks for, and assistant messages carry no level,
  so no entry is reliable evidence for a request; nothing marks a served speed tier.
- **DeepSeek Harness** (per session file; inherited history, METRICS 4.3, is skipped): every `request/header` replaces
  the context's effort fact with its `config.reasoningEffort` (adapter `effort`, evidence `recorded`, scope `request`)
  or, when the field is absent, removes it. A response (`assistant/message`) takes a snapshot of the context. No speed
  facts. Current: the context.
- **Factory Droid** (per session transcript; METRICS 4.16): every call (an accepted assistant message) replaces the
  context's effort fact with its `message.reasoningEffort` (adapter `effort`, evidence `recorded`, scope `request`)
  or, when the field is absent, removes it; the call's response takes a snapshot of the context then, and keeps it when
  its tokens are allocated later. Droid's tokens are `none` (→ `off`), `off`, `minimal`, `low`, `medium`, `high`,
  `xhigh`, `max` and `dynamic` (a word without a meter level); droid silently runs a model that lacks the chosen effort
  at `off` and records that. Messages ignored by deduplication contribute nothing. The sidecar's `reasoningEffort`
  (the selection) and `effectiveFactoryRouterModel.reasoningEffort` are not used. No speed facts (`-fast` model ids
  are priced by METRICS 9.2, not facts). Current: the context, so a call shows its effort as soon as its line is read.

`parser-expected.json` checks these for the Claude, Codex, OpenCode, Pi, DeepSeek Harness and Factory Droid fixtures: per file (or
OpenCode session) `effort` and `speed` (the current attributions) and `responseModes` (per response key), each
attribution an object `{mode, evidence[, provider]}` or `null` when unknown. Files without these keys are not checked. `reference.py
--dump-today` adds `effort` and `speed` to every response row (`null` when unknown); `compare_dumps.py` compares them
when both dumps carry them.

## Local aggregates and history

Mode classification is independent of the original model ID. For each kind, choose served evidence first, then
recorded, then selected. Configured-only or unresolved automatic selections become `unknown` / evidence `unknown`.
Preserve evidence categories as separate groups; selected Fast is not served Fast.

The history and the mode CSV also break responses down by `effort` and by the derived `speed`, exactly like a
kind (one attribution per response, unknown bucket included). Adding them bumps the history analysis version, so days
whose logs still exist are recomputed. Every response contributes once within a mode kind. A response may independently have service, routing or workflow
facts, so rows of different kinds must not be added together. For any displayed kind, include missing records in an
unknown bucket; its group totals equal the ordinary model totals. Keep source/provider, model, kind, value and
evidence separate in grouping keys. Requests, output, measured output and measured milliseconds use existing rules.

Local upserts may replace a response's classification without incrementing its request count again. Existing
persisted aggregates remain available and unknown, never backfilled as Standard. Prefer an additive aggregate table
and parser-version reanalysis over rewriting old model IDs or deleting history.

The existing CSV format stays unchanged. A separate mode CSV uses these fixed columns, in order:
`date,source,model,kind,mode,evidence,provider,requests,output_tokens,measured_output_tokens,measured_ms,avg_speed_tok_s`.
Each kind is an independent breakdown with unknown totals filled from the ordinary aggregates. Average speed uses
the existing measured-output/measured-time formula; it is not an average of group averages.

## Optional report extension version 1

Discovery advertises `extensions.executionModes: 1`. A client sends the optional `models[].modeUsage` field only
when this exact integer is advertised and the user's existing models sharing section is enabled. Boolean true,
unknown versions, absent capability and disabled models sharing all retain the old report shape. The receiver
and leaderboard must implement this contract before advertising it. No conversation, paths or raw config is sent.

`effort` and `speed` are local only and are never sent (they are not version 1 kinds).
Each modeUsage row contains `source`, optional `provider`, `kind`, `mode`, `evidence`, `requests`, `output`,
`measuredOutput`, `measuredMs`. Evidence is `served`, `recorded`, `selected`, or `unknown`; `configured` is forbidden.
Mode and evidence are both `unknown` for an unattributable row. Native rawValue and native scope stay local.
Use the same bounded identifiers and integer bounds as the existing protocol. At most 64 rows per model.

For every kind present, rows have unique `(source,provider,kind,mode,evidence)` keys and their requests/output/
measuredOutput/measuredMs sums exactly equal the parent model entry. Row sources belong to the parent's sources.
Kinds are independent breakdowns, never extra model totals. If a complete bounded breakdown does not fit, omit the
optional field for that model rather than transmitting a partial/misleading breakdown or exceeding body limits.
Keep model IDs unique and unchanged. Existing signatures, run/seq deduplication and retry rules apply unchanged.

The report ledger freezes a response's mode attribution when that response first enters its live counters. Later
numeric growth uses that same attribution, so a late mode observation cannot replay a previously reported request
or transfer old tokens into a later window. Local UI/history can still retain richer later evidence. A late or
ambiguous fact may therefore remain unknown in already-reported data; no unnegotiated correction is sent.

## Acceptance

Shared fixtures must cover explicit Standard, missing/null/auto, OpenAI aliases, unknown future tiers, Anthropic
capacity-vs-speed, recorded-vs-served omp evidence, Kimi exact IDs, routing/workflow separation and unsafe strings.
Native adapter tests cover turn changes, parallel sessions, parent/child scope, fallback, streaming upserts and
restart/history migration. Both platforms must agree with the independent reference. Report checks cover exact
capability negotiation, complete per-kind conservation, body bounds, zero-request growth, late metadata, disabled
sharing, legacy payloads and receiver run/seq deduplication. Runtime UI checks use isolated settings and history.
