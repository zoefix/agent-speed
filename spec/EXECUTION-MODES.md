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
Configured facts are source information only and never become a response's selected/served fact implicitly: they
reach an agent's current value (*Current values*) and nothing else.
Facts are provider-aware: OpenAI `default` -> `standard`, `priority` -> `fast`; Google `priority` -> `fast`, `standard`
and `flex` as written, `unspecified` no fact; Anthropic Fast comes from `usage.speed`, not `usage.service_tier`. Do not
infer modes from throughput or arbitrary model names; reasoning effort is its own kind (`effort`) and never implies a
speed mode.

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
  requested tier when a response echo is absent. Respect explicit rejection/fallback and child overrides. Reasoning
  effort: see *Reasoning effort and speed*.
- ZCode: user-message `metadata.highspeed` is selected `accelerator:highspeed` for that turn. Actual provider/model
  routing on an attempt is recorded evidence, not a provider response echo; a served fact requires explicit response
  metadata. Do not treat the full turn as accelerated after
  fallback, propagate an overlay to forks/subagents, or count `hsc_mock_*` metadata as served acceleration. Reasoning
  effort and the exact rules: see *Reasoning effort and speed*.
- Kimi: exact official IDs `k3.1-pro-highspeed` and `kimi-for-coding-highspeed` are selected `modelVariant:highspeed`.
  The recorded model is requested metadata, not proof of the provider's served model. No substring matching.
  Reasoning effort: see *Reasoning effort and speed*.
- WorkBuddy/WorkBuddy AI: `providerData.requestModelId` `fast-model`, `balanced-model`, `deep-model` are selected
  routing facts: they pick a model at a credit rate, all three with the same default effort, so they are neither an
  effort nor a speed. A synthetic compatibility `service_tier:"standard"` is not served evidence.
- Antigravity: Planning/Fast is a workflow dimension only when its actual native field is known; `/fast` was removed
  in agy 1.1.0 and was never a service speed. Turbo permissions and animation speed are excluded. Effort and
  `service_tier`: see *Reasoning effort and speed*.
- Gemini CLI: no effort and no speed are persisted; a provider API's capability alone is not evidence that its harness
  selected it. The one value available is configured (*Current values*).
- Grok Build: the session's current effort is in `summary.json` (*Current values*); no speed.
- DSH: `request/header` `data.header.config.reasoningEffort` is the effort the harness sent with that request (see
  *Reasoning effort and speed*). Observed on DeepSeek Harness 0.2.1 (Windows, 2026-10-25): `max`; headers of older
  sessions have no such field.
- Factory Droid: each assistant message's `message.reasoningEffort` is the effort of that call, and its
  `message.modelId` names a tier variant from Factory's catalog (see *Reasoning effort and speed*). Effort observed in
  issue #33 (droid 0.250.0) and in public recordings of droid and Factory Desktop.

## Reasoning effort and speed

What the agent rows, the floating windows and the history show per agent and per response (DESIGN 4, 4b, 2c) is two
values derived from the facts above: the **reasoning effort** and the **speed**. Every source has its rule under
*Native rules*; a field the source does not write is unknown, never a guess. *Per source* lists what each one gives
and which rules were checked only against shipped code (user's decision, 2026-10-30: implement them now, without real
logs).

### Effort facts

`effort` is a fact kind. Its adapter (`adapter: "effort"`, shared fixture `execution-mode-cases.json`) takes the
native token: it must be a safe identifier (Common facts); it is lower-cased; `none` and `disabled` become `off`
(reasoning disabled), `enabled` becomes `on`; `auto`, `default`, `inherit`, `inherited`, `unknown` and
`thinking_level_unspecified` produce no fact; every other token is kept as is (future tokens stay distinct). Effort
facts have no provider.

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
| any other token | none (word only) | such as `on` (thinking on, no effort parameter), `adaptive`, `dynamic`, `extra-low` |

Display order (history, ties): `ultracode`, `ultra`, `max`, `xhigh`, `high`, `medium`, `low`, `minimal`, `off`, other
tokens in ordinal order, unknown last.

### Speed

`speed` is not a fact kind; it is derived from a fact set: the attribution of `serviceTier` when it is resolved, else
of `accelerator`, else of `modelVariant` when that value is `highspeed`, else unknown. Its value keeps the fact's
canonical token (`standard`, `fast`, `ultrafast`, `highspeed`, `flex`, `scale`, …), evidence and provider. Bolts:
`fast` and `highspeed` 1, `ultrafast` 2, everything else 0. Display order: `ultrafast`, `fast`, `highspeed`,
`standard`, other tokens in ordinal order, unknown last.

Attribution of either value follows *Local aggregates* (served, then recorded, then selected; scope breaks ties;
facts that still tie and disagree give unknown; configured never counts). A value is shown only when it is known;
unknown is never drawn as Standard or as a level. The `fast` flag of pricing (METRICS 3.2, 9) is a separate thing: no
rule here changes what a response costs.

### Native rules

Facts live in two places per agent: the *context* (facts that apply from their time on) and each response's own facts
(a snapshot taken when the response is created; Claude merges the facts of every line of the message, a later fact
replacing one of the same kind, evidence, provider and scope). An agent's *current* effort and speed start from its
current facts, below (*Current values* says what follows them). A source's rule that names no speed has no speed
facts, and one that names no effort has no effort facts.

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
  Pi has no local speed data at all: `serviceTier` is only a stream option of its library, unknown to its CLI and
  settings, and the tier a response reports only adjusts `usage.cost`.
- **omp** (per session file; entries METRICS 4.7 skips contribute nothing): an entry's context is its parent's
  (`parentId`), kept per entry id, so a branch sees only its own selections; a missing or unknown parent is an empty
  context. `service_tier_change.serviceTier` replaces the context's tier facts with one per family it names (keys
  `openai`, `anthropic`, `google`; adapter `ompTier`, evidence `selected`, scope `session`). A
  `thinking_level_change` entry with a string `thinkingLevel` replaces the context's effort fact (adapter `effort`,
  evidence `selected`, scope `session`); with `thinkingLevel: null` it removes it, which is not `off` (the level was
  undefined: a model without thinking); `configured` (`auto` or a level) is the selector and is not used. Levels:
  `off`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`. A session whose default is `auto` may never write such an
  entry: its effort stays unknown, never `off`.
  A response's facts are the context's effort fact, the context's tier of its provider's family, and what the message
  records: `message.serviceTier` (adapter `ompTier`, evidence `recorded`, scope `response`) and, on models with
  per-message effort, `message.requestControls.effort` as `tail`, else `topLevel`, when that is a string (adapter
  `effort`, evidence `recorded`, scope `request`; both null is the API's default: no fact). What a message records
  stays out of the branch. A `model_usage` side call takes the tier of its family and no effort: it runs at its own,
  unrecorded level. Families: `anthropic`; `openai`, `openai-codex`, `azure`, `azure-openai-responses` as `openai`;
  `google`, `google-gemini-cli`, `google-antigravity`, `google-vertex` as `google`; any other provider has no family
  and no tier fact. Current: the context of the latest entry, plus what that entry recorded when it is an assistant
  message, its tier facts limited to the family of the latest response.
- **OpenClaw** (METRICS 4.14): a window's `trace.metadata` rows, one per attempt. `data.model.thinkLevel` (adapter
  `effort`, evidence `recorded`, scope `turn`; `off`, `minimal`, `low`, `medium`, `high`, `xhigh`, `adaptive` (a word
  without a meter level), `max`, `ultra`) and `data.model.fastMode` when it is `true` (`serviceTier:fast`, evidence
  `selected`, scope `turn`, no provider). `false` says nothing: `ultrafast` and `auto` are written as `false` too, so
  it is not Standard, and `true` only says the harness asked. `reasoningLevel` is a display setting and is not used.
  A row without `data.model` (trimmed by OpenClaw's 256 KiB limit) has no facts. A response's facts are those of the
  window's latest row with `created_at` at or before the response's stamp (METRICS 4.7 step 2); a trimmed row
  therefore makes its attempt unknown instead of letting the attempt before it reach further. Rows arrive late (METRICS
  4.14): a row read after its responses updates them like any upsert (4.4). `thinking_level_change` entries and
  `session_nodes.entry_json` are not used: they say what the session selected, not what an attempt ran. Current: the
  facts of the window's latest row.
- **DeepSeek Harness** (per session file; inherited history, METRICS 4.3, is skipped): every `request/header` replaces
  the context's effort fact with its `config.reasoningEffort` (adapter `effort`, evidence `recorded`, scope `request`)
  or, when the field is absent, removes it. A response (`assistant/message`) takes a snapshot of the context. No speed
  facts: the harness's call config has no tier, and `deepseek-flash` and `deepseek-pro` are models. Current: the context.
- **Factory Droid** (per session transcript; METRICS 4.16): every call (an accepted assistant message) replaces the
  context's effort fact with its `message.reasoningEffort` (adapter `effort`, evidence `recorded`, scope `request`)
  or, when the field is absent, removes it; the call's response takes a snapshot of the context then, and keeps it when
  its tokens are allocated later. Droid's tokens are `none` (→ `off`), `off`, `minimal`, `low`, `medium`, `high`,
  `xhigh`, `max` and `dynamic` (a word without a meter level); droid silently runs a model that lacks the chosen effort
  at `off` and records that. Messages ignored by deduplication contribute nothing. The sidecar's `reasoningEffort`
  (the selection) and `effectiveFactoryRouterModel.reasoningEffort` are not used.
  Speed: the same call replaces the context's tier with a `serviceTier` fact when `message.modelId` is exactly one of
  the catalog entries below (adapter `droidModel`, evidence `recorded`, scope `request`), or removes it: `/fast` moves
  the session to one of these ids, which are written as the message's model and change the request. A `speed` value
  only comes from `serviceTier`, `accelerator` and `highspeed` model variants, so a `modelVariant:fast` would never
  show. Pricing is untouched (9.2 prices `-fast` by its suffix).

  | Exact `message.modelId` | Value | Provider |
  | --- | --- | --- |
  | `claude-opus-4-6-fast`, `claude-opus-4-7-fast`, `claude-opus-4-8-fast`, `claude-opus-5-fast`, `claude-opus-5-5-fast` | `fast` | `anthropic` |
  | `gpt-5.3-codex-fast`, `gpt-5.4-fast`, `gpt-5.4-mini-fast`, `gpt-5.5-fast`, `gpt-5.6-fast` (alias), `gpt-5.6-sol-fast`, `gpt-6-sol-fast`, `gpt-6-luna-fast` | `fast` | `openai` |
  | `gpt-6-astra-ultrafast`, `gpt-6.1-sol-ultrafast` | `ultrafast` | `openai` |
  | `gpt-5.6-terra-flex`, `gpt-5.6-luna-flex`, `gpt-6-sol-flex`, `gpt-6-astra-flex`, `gpt-6-luna-flex` | `flex` | `openai` |

  No substring or suffix matching: `glm-5.2-fast` is a model of its own, `auto-fast` a router, and a `custom:` id has no
  tier fact. Current: the context, so a call shows its effort and tier as soon as its line is read.
- **Gemini CLI** (METRICS 4.6): no facts. The session file has token counts for thoughts but no level, `logs.json`
  has no settings, and OpenTelemetry leaves `thinkingConfig` out; `tokens.thoughts` says that a response thought, not
  how hard. It has no tier either: nothing in its code selects one. Only a configured effort exists.
- **Grok Build** (METRICS 4.12): no facts. `updates.jsonl` has no effort, and no line of `chat_history.jsonl` can be
  tied to a batch (it is not read). Every request is sent without a tier, and `grok-code-fast-1` and its like are model
  names, not speed. Only the session-current effort exists.
- **WorkBuddy, WorkBuddy AI** (METRICS 4.9): routing facts only. The effort of a request exists in the CLI's memory and
  is written to no transcript, and the service reports no tier. Only the session-current and configured efforts exist.
- **Hermes Agent** (METRICS 4.15): no facts. `API call #` lines have no effort or tier, and the log lines that do name a
  `reasoning_config` carry no session id. Only the session-current effort and the configured values exist.
- **Kimi Code CLI** (per wire file, METRICS 4.11): the context holds one effort fact and one model-variant fact.
  `profile.bind.thinkingEffort` and `config.update.thinkingEffort`, else its legacy `thinkingLevel`, replace the effort
  fact (adapter `effort`, evidence `selected`, scope `session`) when they are non-empty strings (a token the adapter
  leaves unresolved clears it); a record without them changes nothing. Every `llm.request` (one per attempt, `loop` and
  `compaction` alike) replaces it with its
  `thinkingEffort` (adapter `effort`, evidence `recorded`, scope `request`) or, when that is absent, removes it. A line
  with a `model` member, or a message's `meta.model.model`, replaces the variant (adapter `kimiModel`,
  evidence `selected`, scope `request`, provider `kimi`). A response takes a snapshot of the context at its first
  emission and a mirror of the same key keeps it. `llm.request` exists since 0.23.1, so older journals only have the
  selection, which reaches their responses. Tokens: `off`, `on` (thinking on, no effort parameter), `low`, `medium`,
  `high`, `xhigh`, `max`, or other strings. `subagent.spawned.thinkingEffort` is not used (a child has its own
  journal). Current: the context.
- **ZCode** (per session, METRICS 4.10): a `model_usage` row carries its own facts, from fields of that row and of the
  user message it answers (`parent_user_message_id`, either column may be absent):
  - `variant` (the column's old name; the level the request sent): adapter `effort`, evidence `recorded`, scope
    `request`. Tokens: `disabled` (→ `off`), `enabled` (→ `on`), `none` (→ `off`), `minimal`, `low`, `medium`, `high`,
    `xhigh`, `max`.
  - `provider_id` exactly `account:zai-highspeed-card` or `account:bigmodel-highspeed-card` (adapter `zcodeProvider`,
    provider `zcode`): `accelerator:highspeed`, evidence `recorded`, scope `request`, unless the message's card is a
    `hsc_mock_*` one.
  - The message's `metadata.highspeed` when `schemaVersion` is 1, `cardId` a non-empty string and `model` a string:
    `accelerator:highspeed`, evidence `selected`, scope `turn`, provider `zcode`. A row that is not on a card provider,
    started at or after an integer `fallbackAt`, with a card that is not `hsc_mock_*`, adds `accelerator:standard`
    (raw `fallback`), evidence `recorded`, scope `request`: the fallback is not read as acceleration.
  - `model_id` exactly one of `GLM-5.1-Highspeed`, `MiniMax-M2.7-highspeed`, `MiniMax-M2.5-highspeed`,
    `kimi-k2.7-code-highspeed`, `kimi-for-coding-highspeed`, `kimi-k3.1-pro-highspeed`, `k3.1-pro-highspeed` (adapter
    `zcodeModel`): `modelVariant:highspeed`, evidence `selected`, scope `request`, provider `zcode`.

  Title, compaction and verification calls run at the catalog's lowest level; their rows keep it. Current: the facts
  of the session's latest row by `started_at`, a running one included, whose `query_source` is not `compact`,
  `session_title` or `goal_completion_verification`. No row is served evidence: nothing in ZCode echoes a tier.
- **Antigravity** (per database, METRICS 4.8): effort is mostly a model variant, not a field. A response's effort,
  from the first of:
  1. its generation's `model_display_name`, when it ends in exactly ` (Low)`, ` (Medium)`, ` (High)` or ` (Max)`
     after a non-empty name (evidence `recorded`, scope `request`; the word, lower-cased by the adapter);
  2. the `model_name` of its executor (the covering row of 4.8 *Metadata*) with an exact suffix `-extra-low`, `-low`,
     `-medium`, `-high` or `-max`, tried in that order (evidence `recorded`, scope `turn`; `extra-low` is a word
     without a meter level);
  3. that executor's `completion_config_override.thinking_level` (`1` `low`, `2` `medium`, `3` `high`, others no fact),
     the API-key path where a model has no variants (evidence `recorded`, scope `turn`).

  Steps 2 and 3 need an executor with a `model_name`, and apply only when the generation's `response_model` is missing,
  or equals the executor's `model_name` or
  its base (the name without the suffix) once a trailing `-exp-b` is removed: otherwise the call was answered by
  another model and the effort is unknown. A row outside every executor's range has only step 1. `response_model` is
  never read for a suffix, and neither are the placeholder enums, `metadata.staticConfig` or
  `steps.metadata.requestedModel`.
  Speed: `model_usage.service_tier` (field 13) when it is not empty: adapter `serviceTier`, provider `google`, evidence
  `served`, scope `response`. No real row has shown a value so far, and agy 1.4.11's own code does not fill it, so the
  speed is normally unknown. Current: the facts of the response with the highest `idx`.

`parser-expected.json` checks these for every source (`harness-expected.json` for ZCode, Kimi and Grok): per agent
`effort` and `speed` (the current values of *Current values*, whose evidence may be `configured`) and `responseModes`
(per response key, never `configured`), each attribution an object `{mode, evidence[, provider]}` or `null` when
unknown. `reference.py --dump-today` adds `effort` and `speed` to every response row (`null` when unknown);
`compare_dumps.py` compares them when both dumps carry them.

### Current values

An agent's current effort and its current speed (the agent rows, boards and their tooltips, DESIGN 4) are each the
first known one of (`current` of `execution-mode-cases.json`):

1. the attribution of the agent's own facts (the *Current* of its rule);
2. a *session-current fact*;
3. a *configured value*.

**Session-current facts.** Some sources keep only a session's latest value, overwritten in place and without history.
Such a value says what the session has selected now, not what any response ran with, so it is held apart from the
facts above: it never enters the context, a response's facts, the history analysis or the mode CSV. Each gives an
`effort` fact (adapter `effort`, evidence `selected`, scope `session`); a missing value gives none:

| Source | Value | Read |
| --- | --- | --- |
| Grok Build | `summary.json` `reasoning_effort` (`none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`) | with the rest of that file (METRICS 4.12) |
| WorkBuddy, WorkBuddy AI | `workbuddy.db` `sessions.thought_level` of the session id | METRICS 4.9 |
| Hermes Agent | `state.db` `sessions.model_config.reasoning_config` of the session id: `enabled: false` is `off`, else its `effort` | METRICS 4.15 |

**Configured values.** A value that exists only in a config file (user's decision, 2026-10-30). It is shown as
configured with its own mark (DESIGN 4) and goes nowhere else: not into a response's attribution, the history, the mode
CSV, live reports or performance statistics. Evidence `configured`, scope `configuration`.

| Source | File | Key | Adapter |
| --- | --- | --- | --- |
| Codex | `config.toml` of the Codex home | root `service_tier`, as *Native adapters* says | `serviceTier`, provider `openai` |
| Gemini CLI | `<home>/.gemini/settings.json` (`<home>` as METRICS 2; not a workspace's) | below | `effort` |
| Antigravity | `config/config.json` in the folder that holds the store (`~/.gemini`) | `userSettings.vertexServiceTier` | `serviceTier`, provider `google` |
| WorkBuddy, WorkBuddy AI | `<data folder>/settings.json` | `reasoningEffort` | `effort` |
| Hermes Agent | the profile's `config.yaml` (METRICS 4.15) | `agent.reasoning_effort`, `agent.service_tier` | `hermesEffort`, `hermesServiceTier` |

- Gemini CLI: of `modelConfigs.overrides` and then `modelConfigs.customOverrides`, in order, an entry counts only when
  its `match` is an object with the one member `model` equal to the agent's model. The last such entry whose
  `modelConfig.generateContentConfig.thinkingConfig` has a string `thinkingLevel` (`HIGH`, `LOW`, …) or a
  `thinkingBudget` of 0 (`off`) decides; any other budget names no level and is passed over. Built-in defaults are not
  written anywhere and are not assumed.
- `hermesEffort`: `none`, `false`, `disabled` → `off`; `minimal`, `low`, `medium`, `high`, `xhigh`, `max`, `ultra` as
  written; anything else no fact (Hermes would ignore it too).
- `hermesServiceTier`: `fast`, `priority` → `fast`; `ultrafast`, `ultra` → `ultrafast`; `normal`, `default`, `standard`,
  `off`, `none` → `standard`; `auto` and `cold` open a bounded fast window on some turns, so they stay unresolved, like
  anything else. Per-model `agent.reasoning_overrides` and `delegation.*` are not read.
- Files are regular files (a symlink is not followed, except Hermes's `config.yaml`, METRICS 4.15) of at most 1 MiB,
  UTF-8 (one leading U+FEFF removed), JSON objects where the table's file is JSON; anything else gives no value. Only
  the named key is taken: `settings.json` files hold other settings, and nothing else of them is kept or logged. They
  are checked at startup and at every rediscovery (METRICS 5) and read again only when their size or modification time
  changed.
- No other config file is read, so omp `config.yml`, Kimi `config.toml`, OpenClaw `openclaw.json`, Grok `config.toml`,
  DSH `cordis.yml` and Antigravity's CLI `settings.json` give nothing even where their source's log is silent, and
  Droid's `~/.factory/settings.json` stays closed (it holds API keys).

**Ledger.** One rule for every source, in one place: when the report ledger (METRICS 11.4) first counts a live response
whose effort attribution is unknown, it freezes the agent's current effort at that moment instead, when that value's
evidence is `selected` or better, without a provider; a configured or unknown current value leaves the response
unknown (`ledger` of `execution-mode-cases.json`). It changes reports only: the response's own attribution, the history
and the mode CSV keep it unknown. `configured` stays forbidden in reports.

### Per source

| Source | Effort | Speed | Only checked against shipped code |
| --- | --- | --- | --- |
| Claude Code, Claude Desktop | recorded per request; `ultracode` | served `usage.speed` | no |
| Codex | selected and recorded per turn | selected, recorded; configured `service_tier` | no |
| OpenCode | recorded per request | served, OpenAI providers only | no |
| DeepSeek Harness | recorded per request | no local data | no |
| Factory Droid | recorded per request | recorded, exact model ids | speed: yes; effort: public recordings |
| Pi | selected, recorded per request | no local data | yes |
| omp | selected on the branch; recorded where the message has `requestControls` | selected, recorded | yes |
| OpenClaw | recorded per attempt | selected, `fastMode: true` only | yes |
| Kimi Code CLI | recorded per attempt, else selected | selected, exact model ids | yes |
| ZCode | recorded per attempt | recorded, selected | yes |
| Antigravity | recorded per generation or run | served, when a value appears; configured `vertexServiceTier` | yes; a third party saw the model-name fields |
| Grok Build | session-current | no local data | yes |
| WorkBuddy, WorkBuddy AI | session-current; configured `reasoningEffort` | no local data (`fast-model` and its siblings are routing) | yes |
| Hermes Agent | session-current; configured `agent.reasoning_effort` | configured `agent.service_tier` only | yes |
| Gemini CLI | configured only | no local data | yes |

What would settle the rules checked only against shipped code:

| Rule | Evidence that settles it |
| --- | --- |
| Grok `reasoning_effort` | A real `summary.json`, before and after `/effort`. |
| ZCode `variant`, card `provider_id` | `select query_source, provider_id, model_id, variant, count(*) from model_usage group by 1,2,3,4` over a session with an accelerated turn. |
| WorkBuddy `thought_level` | `select id, model, thought_level from sessions limit 20`, compared with the file names under `projects`: that `sessions.id` is the transcript's name is inferred. |
| WorkBuddy, Antigravity, Gemini config keys | The real files with the key set; Antigravity's mapping of `vertexServiceTier` to `config.json` is inferred from the descriptor's JSON name. |
| Antigravity model name and `thinking_level` | One CLI and one IDE database: decode `executor_metadata` 10→1→28, 10→1→56→19 and `gen_metadata` 1→21, polling during a long answer to see when the executor row is written. That `thinking_level` reaches the database at all is inferred. |
| Antigravity `service_tier` | A database from the Vertex path with `vertexServiceTier = "priority"`: decode `model_usage` field 13 for the real string. |
| omp `thinking_level_change`, `requestControls` | A real session file with a manual change and an `auto` session; the version that added `requestControls.effort` was not pinned down. |
| Kimi `llm.request.thinkingEffort` | A real `wire.jsonl` from 0.23.1 or later. |
| OpenClaw `trace.metadata` | A real database: that compressed `trace.metadata` rows directly follow a plain `session.started`, how often `session.started` itself exceeds 1 KiB, and whether a `/think` inside an attempt changes what the row says. |
| Droid `-fast` ids | A real session: that a `-fast` id reaches `message.modelId`. It is read from droid 1.0.0's bundled code. |
| Hermes `reasoning_config` | A real `state.db`: that the log's session id is `sessions.id`, and how stale `reasoning_config` gets (the CLI writes it only when the row is created). |

## Local aggregates and history

Mode classification is independent of the original model ID. For each kind, choose served evidence first, then
recorded, then selected. Configured-only or unresolved automatic selections become `unknown` / evidence `unknown`, and
session-current facts (*Current values*) are not a response's at all. Preserve evidence categories as separate groups;
selected Fast is not served Fast.

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

## Report extension (versions 1 and 2)

Discovery advertises `extensions.executionModes` as the JSON integer `1` or `2`. A client sends the optional
`models[].modeUsage` field only when one of these exact integers is advertised and the user's existing models sharing
section is enabled. Boolean true, other numbers (an unknown future version included), absent capability and disabled
models sharing all retain the old report shape. The receiver and leaderboard must implement the advertised version
before advertising it. No conversation, paths or raw config is sent.

- **Version 1** carries the kinds `serviceTier`, `accelerator`, `modelVariant`, `routing` and `workflow`.
- **Version 2** (user's decision, 2026-10-30: report the reasoning effort) carries those and `effort`: the response's
  effort attribution (*Reasoning effort and speed*; `ultracode` and `ultra` included). Effort rows have no
  `provider`. `speed` stays local: it is derived from the service-tier, accelerator and model-variant rows a service
  already receives. A version 2 receiver accepts every version 1 row.

Each modeUsage row contains `source`, optional `provider`, `kind`, `mode`, `evidence`, `requests`, `output`,
`measuredOutput`, `measuredMs`. Evidence is `served`, `recorded`, `selected`, or `unknown`; `configured` is forbidden.
Mode and evidence are both `unknown` for an unattributable row. Native rawValue and native scope stay local.
Use the same bounded identifiers and integer bounds as the existing protocol. At most 64 rows per model.

For every kind present, rows have unique `(source,provider,kind,mode,evidence)` keys and their requests/output/
measuredOutput/measuredMs sums exactly equal the parent model entry. Row sources belong to the parent's sources.
Kinds are independent breakdowns, never extra model totals.

**Rows** (both platforms; `execution_modes_reference.py` `mode_rows`, fixture `reportRows` of
`execution-mode-cases.json`). The kinds of the negotiated version that some frozen attribution of the model has, plus
`serviceTier` and, with version 2, `effort` (so a service always gets their complete denominators), each complete per
source: the attributions' usage, then an `unknown` bucket for the rest of that source's totals. Rows are ordered by
`source/kind/provider/mode/evidence` joined with `/` (an absent provider is empty), compared as strings. If a complete
bounded breakdown does not fit, omit the optional field for that model rather than transmitting a partial or
misleading breakdown or exceeding body limits: more than 64 rows under version 2 first leave the `effort` kind out
(the version 1 rows); more than 64 rows then, an attribution whose usage does not fit within its source's totals, or
an unsafe identifier omit the field. Keep model IDs unique and unchanged. Existing signatures, run/seq deduplication
and retry rules apply unchanged.

The report ledger freezes a response's mode attributions, the effort included, when that response first enters its
live counters (an unknown effort by the rule of *Current values*, Ledger). Later numeric growth uses that same
attribution, so a late mode observation cannot replay a previously reported request or transfer old tokens into a
later window. Local UI/history can still retain richer later evidence.
A late or ambiguous fact may therefore remain unknown in already-reported data; no unnegotiated correction is sent.

## Acceptance

Shared fixtures must cover explicit Standard, missing/null/auto, OpenAI and Google aliases, unknown future tiers,
Anthropic capacity-vs-speed, recorded-vs-served omp evidence, Kimi, ZCode and Droid exact IDs, Antigravity's exact
suffixes, routing/workflow separation, unsafe strings, the fallback order of current values and the ledger rule.
Native adapter tests cover turn changes, parallel sessions, parent/child scope, fallback, streaming upserts and
restart/history migration. All platforms must agree with the independent reference. Report checks cover exact
capability negotiation, complete per-kind conservation, body bounds, zero-request growth, late metadata, disabled
sharing, legacy payloads and receiver run/seq deduplication. Runtime UI checks use isolated settings and history.
