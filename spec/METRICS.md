# Agent Speed — Measurement Specification

This document is the single source of truth for how Agent Speed reads agent transcripts and turns them into
tokens-per-second. The macOS (Swift) and Windows (C#) implementations MUST follow it exactly and MUST pass the
shared fixtures in `spec/fixtures/`.

## 1. Vocabulary

| Term | Meaning |
| --- | --- |
| Source | `codex`, `claudeCode`, `claudeDesktop`, `deepseek` (display name **DSH**, DeepSeek Harness), `opencode`, `gemini` (Gemini CLI), `omp` (oh-my-pi), `antigravity` (Google Antigravity), `workbuddy` (Tencent WorkBuddy, China edition), `workbuddyAi` (WorkBuddy AI, international edition), `zcode` (ZCode), `kimi` (Kimi Code CLI), `grokBuild` (Grok Build), `pi` (Pi Coding Agent), `openclaw` (OpenClaw), `hermes` (Hermes Agent, Nous Research), `droid` (Factory Droid), `trae` (Trae, partial request usage). This is the append-only *source order*. |
| Agent | One transcript file. Agent id = absolute file path. Sub-agents have their own files and are separate agents. (OpenCode: one per session row, 4.5; Antigravity: one per conversation database, 4.8; OpenClaw: one per transcript window, 4.14; Hermes Agent: one per session id of a log folder, 4.15; Factory Droid: one per session transcript with its settings file, 4.16; Trae: one per native database session, 4.17.) |
| Response | One model API call that produced a usage record. |
| Task | One user request to one agent, from its prompt to the next one that is not a continuation (6.9). |
| Phase | `generating` (a request is in flight), `tools` (model finished, waiting for tool results), `idle` |

Parsers normally emit one record per response. A native Grok model batch is one record with `requestCount` underlying
responses (4.12); the speed engine still processes its normalized tokens and duration with the same equations.
Factory Droid records no usage per response: its responses are the calls its transcript times, with tokens allocated
from cumulative session counters (4.16).

## 2. Locations

| Source | Root | Override |
| --- | --- | --- |
| Codex | `$CODEX_HOME/sessions`, else `~/.codex/sessions`; plus `<agent folder>/codex-home/sessions` for the folder of every OpenClaw agent database (4.14: the Codex runs OpenClaw starts in its agent-scoped Codex homes) | user setting (replaces every automatic root) |
| Claude Code + Claude Desktop | `$CLAUDE_CONFIG_DIR/projects`, else `~/.claude/projects` | user setting |
| DeepSeek Harness (`dsh`, desktop app and CLI) | `$DSH_HOME/sessions`, else `~/.dsh/sessions` | user setting |
| OpenCode (CLI and desktop app) | `$XDG_DATA_HOME/opencode/opencode.db`, else `~/.local/share/opencode/opencode.db` (same form on Windows under `%USERPROFILE%`) | user setting (folder that contains `opencode.db`) |
| Gemini CLI (interactive, `-p`, ACP/IDE sessions) | `<home>/.gemini/tmp`; on macOS also `<home>/.cache/.gemini/tmp` (seatbelt sandbox, v0.63+). `<home>` = `$GEMINI_CLI_HOME`, else `~` | user setting (one `tmp` folder; replaces both defaults) |
| omp (oh-my-pi: TUI, `-p`, RPC, ACP) | The default root plus one root per named profile (4.7). Default: `$PI_CODING_AGENT_DIR/sessions`, else on macOS `$XDG_DATA_HOME/omp/sessions` when `$XDG_DATA_HOME/omp` exists, else `~/<cfg>/agent/sessions`, where `<cfg>` = `$PI_CONFIG_DIR`, else `.omp`. Profiles: `~/<cfg>/profiles/<name>/agent/sessions` for every folder `<name>` | user setting (one `sessions` folder; replaces every automatic root) |
| Antigravity (CLI `agy`, desktop app, IDE, IDE extensions) | three store folders: `~/.gemini/antigravity-cli`, `~/.gemini/antigravity`, `~/.gemini/antigravity-ide` (each contains `conversations`) | user setting (one store folder, or its `conversations` folder; replaces all three) |
| WorkBuddy (desktop app `WorkBuddy`, China edition) | `~/.workbuddy/projects` (4.9) | user setting (the data folder or its `projects` folder) |
| WorkBuddy AI (desktop app `WorkBuddy AI`, international edition) | `~/.workbuddy-ai/projects` (4.9) | user setting (the data folder or its `projects` folder) |
| ZCode | `~/.zcode/cli/db/db.sqlite`; `$ZCODE_DATA_BASE_DIR` replaces the home base, with `.zcode/cli/db` underneath | user setting (data directory, database folder, or `db.sqlite`) |
| Kimi Code CLI (TypeScript v2) | `$KIMI_CODE_HOME/sessions`, else `~/.kimi-code/sessions` | user setting (data directory or its `sessions` folder) |
| Grok Build | `$GROK_HOME/sessions`, else `~/.grok/sessions` | user setting (data directory or its `sessions` folder) |
| Pi (Pi Coding Agent: TUI, `-p`, RPC) | `<agent dir>/sessions` with `<agent dir>` = `$PI_CODING_AGENT_DIR`, else `~/.pi/agent`; plus Pi's own custom session folder when one is set: `sessionDir` of `<agent dir>/settings.json` and `$PI_CODING_AGENT_SESSION_DIR` (4.13) | user setting (one folder; replaces every automatic root) |
| OpenClaw (a Gateway on this computer, 2026.8.1+) | the agent databases `<state>/agents/<agentId>/agent/openclaw-agent.sqlite` of every state folder: `$OPENCLAW_STATE_DIR`, else `<base>/.openclaw` and every named-profile folder `<base>/.openclaw-<profile>`, with `<base>` = `$OPENCLAW_HOME`, else `~` (4.14) | user setting (a state folder, an agent folder or a database; replaces every automatic root) |
| Hermes Agent (CLI, TUI, desktop app, messaging gateway, cron, ACP) | the log folders of the Hermes home: `<home>/logs` and `<home>/profiles/<name>/logs`, with `<home>` from `$HERMES_HOME` (4.15), else macOS `~/.hermes`, Windows `%LOCALAPPDATA%\hermes` | user setting (a Hermes home or one `logs` folder; replaces the automatic home) |
| Factory Droid (CLI `droid`, interactive and `droid exec`; Factory Desktop) | `~/.factory/sessions`; no environment variable (4.16) | user setting (the `.factory` folder or its `sessions` folder) |
| Trae (IDE, China and SOLO editions) | macOS `~/Library/Application Support/{Trae,Trae CN,TRAE SOLO,TRAE SOLO CN}`; Windows `%APPDATA%/{Trae,Trae CN,TRAE SOLO,TRAE SOLO CN}`. Each reads `ModularData/ai-agent/database.db`, falling back to `ModularData/ai-chat/database.db` | user setting (one application data folder, database folder or exact database file, replacing automatic roots) |

`~` is `HOME` on macOS and `%USERPROFILE%` on Windows. Claude Code and Claude Desktop share one root; each file is
assigned to a source by its `entrypoint` field (section 4.1). Ordinary Claude Desktop chat conversations are not stored
locally and are out of scope.

Discovery: recursive enumeration of `*.jsonl` (Codex also `*.jsonl.zst`, 5.3; DeepSeek: `session.v<N>.jsonl.zstd` or `session.v<N>.jsonl`; Gemini CLI:
fixed depths, see 4.6; omp and Pi: see 4.7 and 4.13; Antigravity: conversation databases, see 4.8; WorkBuddy: fixed
depths, see 4.9; OpenClaw: agent databases, see 4.14; Hermes Agent: `agent.log` and its rotated files, see
4.15; Factory Droid: one or two levels below the root, each transcript with its settings file, see 4.16),
maximum depth 6, never follow symlinks, at most 20 000 files per root.
A source is *detected* when its root (Antigravity: any store; WorkBuddy: the data folder) exists or it has data today;
what an undetected source shows is DESIGN 2b (unavailable sources), 4 and 5.
The app never writes inside these folders.
For ZCode, Kimi Code CLI, Grok Build, Hermes Agent and Factory Droid, the source's session count includes today's main
sessions only; subagents remain visible in the agent tree and ordinary forks remain separate sessions, as with
WorkBuddy.
For OpenClaw, a source is detected when a state folder (or the folder of the setting) exists; its session count is the
number of transcript windows that are not sub-agents (4.14).

## 3. Common rules

### 3.1 Timestamps
RFC 3339 / ISO 8601 with optional fractional seconds and `Z` or offset. Convert to Unix milliseconds (Int64).
Lines without a parseable timestamp are ignored except where stated.

A timestamp string is exactly `YYYY-MM-DD`, then `T`, `t` or a space, then `hh:mm:ss`, then optionally `.` and one or
more digits, then `Z`, `z`, `±hh:mm` or `±hhmm`, with ASCII digits only, nothing before or after it (no whitespace) and
at most 64 characters (after JSON unescaping). Year 0000–9999 of the proleptic Gregorian calendar (0000 is a leap
year), month 01–12, a day that exists in that month, hour 00–23, minute and second 00–59 (no leap second), offset hours
00–23 and offset minutes 00–59. Anything else is not a timestamp (`2026-10-20T10:00:00` without a zone, `+08` without
minutes, `24:00:00`). Digits after the third fraction digit are dropped. The value is computed arithmetically —
`(days since 1970-01-01 × 86 400 + seconds of the day − offset) × 1000 + milliseconds` — so every valid string has a
value, also before 1970 (negative) and past 9999-12-31 UTC (`9999-12-31T23:59:59-23:59`). Fixture cases: the
WorkBuddy session `orbit-timestamps` (`spec/tools/workbuddy_fixtures.py`; WorkBuddy accepts string timestamps, and
every source shares this parser).

### 3.2 Normalized token usage
Every response carries:

| Field | Codex (`usage` / `last_token_usage`) | Claude (`message.usage`) |
| --- | --- | --- |
| `input` (total input) | `input_tokens` (already includes cached) | `input_tokens + cache_creation_input_tokens + cache_read_input_tokens` |
| `cachedInput` | `cached_input_tokens` | `cache_read_input_tokens` |
| `cacheWriteInput` | `cache_write_input_tokens` (absent → 0) | `cache_creation_input_tokens` |
| `cacheWrite1hInput` (the part of `cacheWriteInput` written with a 1-hour lifetime) | 0 | `cache_creation.ephemeral_1h_input_tokens` (absent → 0), at most `cacheWriteInput` |
| `output` (includes reasoning) | `output_tokens` | `output_tokens` |
| `reasoning` | `reasoning_output_tokens` | `output_tokens_details.thinking_tokens` (absent → 0) |

Missing numeric fields are 0. Negative values are clamped to 0. `total = input + output`. Every other source has
`cacheWrite1hInput = 0` (their logs do not separate cache lifetimes; Claude Code writes about a third of its cache
with the 1-hour lifetime, which costs 2× input instead of 1.25×). On a repeated Claude line the field merges with
`max` like the others (4.1).

Token values and the cap: every token value read from a log is its number truncated toward zero and clamped to
0 … 10^12 (numbers: 3.6). So `12.9` counts 12; `-7`, `-1e999` and `0.5` count 0; `1e20`, `1e999` and an integer
literal too long for 64 bits count 10^12; any other value (string, boolean, null, object, array) counts 0. A
normalized field that adds values (the `input` of Claude, DeepSeek Harness, OpenCode, Gemini CLI and omp; the `output`
of OpenCode and Gemini CLI; the sums of WorkBuddy, 4.9) saturates at 10^12 too, so no field ever exceeds it. No real
response comes near; the cap keeps every total far from 64-bit overflow, and cost classes are clamped further (9.3).
Antigravity has its own, lower limit (4.8). Fixture cases: the WorkBuddy session `orbit-edge`
(`spec/tools/workbuddy_fixtures.py`).

Every response also carries `provider` (string or null): the API provider the agent says it used, only for pricing
(9.2). Codex: `payload.model_provider` of the file's first `session_meta` (`openai`, or the user's own key such as
`custom`). DeepSeek Harness: `data.message.source.provider`, else the latest `request/header` `config.provider`.
OpenCode: `data.model.providerID`. omp: `message.provider` (assistant) or the entry's `provider` (`model_usage`).
Pi: `message.provider` (assistant) or the entry's `provider` (`usage` entries).
OpenClaw: `message.provider` (assistant).
Hermes Agent: the `provider=` field of the API-call line (4.15; `unknown` counts as null).
Factory Droid: `custom` for a model id that starts with `custom:` (the user's own model, 4.16).
WorkBuddy: `custom` for a model of the user's own provider (`custom:` / `custom-local:` ids, 4.9). Every other
source: null. Empty strings count as null.

Every response also carries `fast` (boolean): the request ran in a provider's fast mode, which has its own prices
(9.3). Claude: `message.usage.speed == "fast"` on any of the message's lines (merged with `or`). Every other source:
false (Codex records only the user's speed setting, not the tier a request was billed at).

### 3.3 Response timing
- `endMs` = timestamp of the usage record (Claude: the latest line of that message).
- `startMs` = when the request was sent, tracked per file as `pendingStartMs` (rules per source below). May be null.
- Valid interval: `startMs != null`, `endMs >= startMs`, `endMs - startMs <= 15 min`. Otherwise the response counts in
  totals but never in speed.
- `final`: whether the usage is the final usage. Codex responses are always final. A Claude response is final once any
  of its lines has a non-null `stop_reason`. Recent Claude Desktop versions often write only the streaming snapshot of
  sub-agent messages (output ≈ 8 tokens) and never the final usage; such responses are *unfinalized*.
- A response is *measured* when it is final and has a valid interval, and *unmeasured* when it is unfinalized and has a
  valid interval. Unmeasured responses never contribute measured speed; their time is estimated instead (6.3).
- Minimum duration: if `endMs - startMs < 500 ms`, use `startMs' = endMs - 500`.
- `speed = output / ((endMs - startMs') / 1000)` — this is end-to-end output speed and includes time to first token.

### 3.4 Deduplication
A global, bounded (65 536 entries, FIFO eviction) set of response keys shared by all files of all sources.
A key first seen in file A and later seen in file B is ignored in B (forks and resumes copy history). Claude: repeated
lines of the same key in the same file are merged (4.1, 4.4). Codex: every repeat of a key is skipped (4.2).

### 3.5 Copied history
For Codex, once `session_meta` has been read, any later line whose timestamp is earlier than `fileCreatedMs - 2000`
is copied history (forks and sub-agents copy their parent's records): ignore it completely (no totals, no speed, no
state change). For Claude and Factory Droid (4.16), copied history is removed by deduplication (3.4).

A paginated Codex sub-agent (multi-agent v2: `history_mode == "paginated"`) starts with a copy of its parent's records
written with fresh timestamps, so the timestamp rule misses them. Every line of such a rollout carries a top-level
`ordinal` (0 for the `session_meta` line), and the first `session_meta`'s `payload.subagent_history_start_ordinal` is
the first ordinal that belongs to the sub-agent (Codex: earlier records are "inherited model context"). When that
field is an *ordinal*, any later line whose own `ordinal` is an ordinal below it is copied history too: ignore it
completely, like the timestamp rule (no totals, no speed, no state change: no model, project, execution-mode facts,
work-signal actions or phase). An ordinal is an integer literal from 0 through 2^63 − 1; any other value (missing,
null, negative, fractional, exponent, string, beyond 64 bits) is no boundary for the field and catches nothing on a
line. Each line's own `ordinal` decides, never its position in the file: a malformed or truncated line does not shift
the boundary. Rollouts without the field (main threads, reverted threads with `history_base`, which start at a
non-zero ordinal and copy nothing) are unaffected. Adding this rule bumped the history analysis version (6), so days
whose logs still exist are recomputed.

### 3.6 Numbers
JSON numbers mean what Python's `json` reads: an integer literal is exact (any length), every other literal is the
nearest double (`1e999` is infinite). `NaN`, `Infinity` and `-Infinity` are not JSON: a line that contains them anywhere
is malformed (section 4).

- *Finite*: a number whose nearest double is finite. `1e999`, `-1e999` and integer literals beyond the double range
  (about 1.8 × 10^308) are not. A rule that asks for a finite number (the numeric timestamps and durations of 4.7 and
  4.9, the first-value rule of 4.9 Tokens) treats any other number like a missing value.
- *Truncated* (to an integer): toward zero, then saturated to the signed 64-bit range, so a millisecond value of
  `1e19` gives 2^63 − 1. A sum of milliseconds (omp `startMs + duration`) saturates too.
- Token values are truncated and capped as in 3.2.

## 4. Parsers

Each parser is a per-file state machine fed complete lines in order. It produces:
- response upserts (`key`, `source`, `agentId`, `model`, `startMs`, `endMs`, normalized tokens)
- phase updates (`phase`, `phaseSinceMs`, `inFlightStartMs`; `inFlightStartMs` is null whenever the phase is not
  `generating`)
- metadata (`project` = last path component of `cwd`, `cwdPath` = the full working directory, used only by 10.6,
  `model`, `isSubagent`)
- `lastLineMs` = the latest timestamp seen in the file

Lines that are not valid JSON objects are skipped and counted as malformed. Lines longer than 32 MiB are skipped.

### 4.1 Claude (Claude Code and Claude Desktop)

Source: the first line that has `entrypoint` decides: a value starting with `claude-desktop` (`claude-desktop`,
`claude-desktop-3p` on Windows) → `claudeDesktop`, anything else →
`claudeCode`. Before that, the file is `claudeCode`.

`isSubagent` = path contains a `subagents` directory component, or any line has `"isSidechain": true`.
`project` = basename of the latest `cwd`.

State: `pendingStartMs` (nullable), `preciseStart` (bool), `phase`.

| Line | Action |
| --- | --- |
| `type == "api-request"` | `pendingStartMs = ts`, `preciseStart = true`, phase `generating` (`inFlightStartMs = ts`) |
| `type == "user"`, content contains a text block (or string content) starting with `[Request interrupted by user` | phase `idle`, `pendingStartMs = null`, `preciseStart = false` |
| `type == "user"` with `toolEndsTurn == true` (the result of a tool that ends the agent's run, such as a sub-agent's `StructuredOutput` or `SubagentHandback`; no request follows) | phase `idle`, `pendingStartMs = null`, `preciseStart = false` |
| other `type == "user"` | if not `preciseStart`: `pendingStartMs = ts`; phase `generating` (`inFlightStartMs = pendingStartMs`) |
| `type == "assistant"` | see below |
| anything else | only updates `lastLineMs` |

Assistant line:
1. `key = message.id`, else `requestId`, else `uuid`. Skip the line if `message.model == "<synthetic>"`.
2. If `key` was first seen in another file: ignore the line entirely.
3. If `key` is new: create the response with `startMs = pendingStartMs` (may be null), then
   `pendingStartMs = null`, `preciseStart = false`.
4. Merge: `endMs = max(endMs, ts)`, every token field = `max(previous, new)`, `model = message.model`.
5. `final = final || stop_reason != null`.
6. Phase from `message.stop_reason`: `"tool_use"` → `tools`; `null`/missing → `tools` when the line's
   `message.content` contains a block of type `tool_use`, otherwise `generating`; `"pause_turn"` → `generating`
   (`inFlightStartMs` = the response start, or `ts` if unknown); any other value → `idle`.

### 4.2 Codex

Every line is an envelope `{ "timestamp", "type", "payload" }`.

State: `fileCreatedMs`, `subagentStartOrdinal` (nullable), `myAgentPath`, `pendingStartMs`, `streaming` (bool),
`sawModernUsage` (bool), `previousLegacyTotals` (nullable), `phase`.

Every later line first passes both copied-history rules of 3.5 (timestamp, then `ordinal` below
`subagentStartOrdinal`); a copied line does nothing below.

| Line | Action |
| --- | --- |
| first `session_meta` of the file | `fileCreatedMs = ts`; `subagentStartOrdinal = payload.subagent_history_start_ordinal` when it is an ordinal (3.5), else null; `project` from `payload.cwd`; `myAgentPath = payload.agent_path ?? "/root"`; `isSubagent = payload.thread_source == "subagent"` or `payload.source.subagent` exists; `threadId = payload.id`. Later `session_meta` lines (sub-agents copy their parent's) are ignored entirely. |
| `turn_context` | `model = payload.model`; `project` from `payload.cwd` when present |
| input (see below) | if not `streaming`: `pendingStartMs = ts`; phase `generating` (`inFlightStartMs = pendingStartMs`) |
| mailbox input (see below) | if not `streaming`: `pendingStartMs = ts`; the phase does not change |
| output (see below) | `streaming = true`; phase `generating` (`inFlightStartMs = pendingStartMs ?? ts`) |
| `token_usage_record` | modern usage (below) |
| `event_msg` + `payload.type == "token_count"` | if `sawModernUsage` and `streaming`: the response ended without a usage record — `streaming = false`, `pendingStartMs = ts`, phase `tools`. Otherwise legacy usage (below). |
| `event_msg` + `payload.type` in `task_complete`, `turn_aborted` | phase `idle`, `pendingStartMs = null`, `streaming = false` |

Input lines:
- `event_msg` with `payload.type` in `task_started`, `user_message`
- `response_item` with `payload.type` ending in `_output`
- `response_item` with `payload.type == "message"` and `payload.role` in `user`, `developer`, `system`

Mailbox input (a message from another agent; an orchestrator may receive it while it is blocked, so it marks a
possible request start but is not proof that a request is in flight):
- `response_item` with `payload.type == "agent_message"` and `payload.recipient == myAgentPath`

Output lines:
- `response_item` with `payload.type == "reasoning"`, or ending in `_call`, or `message` with role `assistant`

Modern usage (`token_usage_record`):
1. Skip if copied history (3.5).
2. `sawModernUsage = true`. `key = payload.response_id`, else `"codex:" + threadId + ":" + ts`.
   (`threadId` = `payload.thread_id` ?? `session_meta.payload.id` ?? file path.) Skip if already seen anywhere.
3. Tokens from `payload.usage`. Ignore `turn_token_usage` and `thread_token_usage`.
4. Emit the response with `startMs = pendingStartMs`, `endMs = ts`.
5. `pendingStartMs = ts`, `streaming = false`, phase `tools`.

Legacy usage (`event_msg` / `token_count`):
1. Ignore when `sawModernUsage` is true, when `payload.info` is null, or when copied history (3.5).
2. `total = info.total_token_usage`, `last = info.last_token_usage`.
3. If `previousLegacyTotals` is null → tokens = `last` (fallback `total`).
   Else `delta = total - previous` per field. If `delta.total_tokens == 0` → duplicate event, ignore (do not change
   any state). If any delta field is negative → tokens = `last`. Otherwise tokens = `delta`.
4. `previousLegacyTotals = total`.
5. `key = "codex-legacy:" + threadId + ":" + total.total_tokens`. Skip if already seen.
6. Emit with `startMs = pendingStartMs`, `endMs = ts`. Then `pendingStartMs = ts`, `streaming = false`, phase `tools`.

### 4.3 DeepSeek Harness (dsh)

Files are a sequence of independent zstd frames (section 5.1); the decoded stream is JSONL. Every event line has
`type`, `seq` (number) and `time` (Unix milliseconds, a number). Wrappers are unwrapped first: a line
`{"type":"event","event":{...}}` is processed as its inner event; a line `{"type":"snapshot","header":{...},
"records":[...]}` is processed as its header followed by each record (itself possibly an `event` wrapper) in order.

State: `sessionId`, `createdMs`, `parentSession`, `model`, `pendingStartMs`, `phase`.

| Event | Action |
| --- | --- |
| first header (`type == "session"`, or a snapshot `header`) | `sessionId = id`; `createdMs = createdAt`; `project` from `cwd`; `isSubagent = origin == "subagent"` or `delegationDepth > 0` or `parentSession` is a non-empty string; `parentSession = parentSession`. Later headers are ignored. |
| any event whose `time` is earlier than `createdMs - 2000` | fork-inherited history: ignore completely |
| `request/header` | `model = data.header.config.model` when present |
| `step/start` | `pendingStartMs = time`; phase `generating` (`inFlightStartMs = time`) |
| `assistant/message` | response (below) |
| `turn/end` | phase `idle`, `pendingStartMs = null` |
| anything else | only updates `lastLineMs` |

`assistant/message`:
1. `key = data.message.id`, else `"dsh:" + sessionId + ":" + seq`. Skip the event entirely if the key was seen anywhere.
2. When `data.usage` is an object, emit a response with `startMs = pendingStartMs`, `endMs = time`, `final = true`,
   `model = data.message.source.model ?? model`, and tokens `input = inputTokens + cacheReadTokens + cacheWriteTokens`,
   `cachedInput = cacheReadTokens`, `cacheWriteInput = cacheWriteTokens`, `output = outputTokens`, `reasoning = 0`
   (dsh does not report reasoning separately; it is included in `outputTokens`).
3. `pendingStartMs = null`; phase `tools` when `data.message.content` has a block of type `tool-call`, else `idle`.

`lastLineMs` is the latest `time` seen (excluding ignored inherited events). Verified against dsh's own session
statistics: the sum of `step/start → assistant/message` intervals equals dsh's `llmMs`, and the token sums equal its
`tokenUsage` totals.

### 4.4 Response upserts
A response is identified by its key. The first emission is a *completion* (used for live events, 6.4). Later
emissions with the same key in the same file are *updates*: the engine replaces the stored response.

### 4.5 OpenCode

OpenCode (2.x) stores everything in one SQLite database in WAL mode. It is opened read-only (`mode=ro`, never
written), with a busy timeout of 2 s, and every read is a short transaction so OpenCode's checkpoints are never
blocked. When OpenCode is not running it closes the database cleanly and deletes `opencode.db-wal` and
`opencode.db-shm`; a `mode=ro` open then either fails (macOS) or creates those two files (Windows `winsqlite3`), which
the app must never do. So: when the database header says WAL (bytes 18 and 19 are both 2) and no `-wal` file exists,
open with `immutable=1` directly, which reads the database file alone and creates nothing; otherwise open `mode=ro`,
and if that fails while no `-wal` file exists, fall back to `immutable=1`. Drop an immutable connection on the next
change of `opencode.db` or as soon as a `-wal` file appears, and reopen with `mode=ro`. A database without the tables `session_v2` and `session_message` is reported as unsupported (warning
`unsupportedFormat`) and contributes nothing.

Agents: one per session row. Agent id `opencode:<session_v2.id>`; `project` = basename of `session_v2.directory`;
`isSubagent = session_v2.parent_id IS NOT NULL`; parent link `opencode:<parent_id>` when that session is tracked;
`model` = the latest assistant `data.model.id`; `lastLineMs` = the latest `session_message.time_updated` of the
session.

Rows of `session_message` are processed per session in `seq` order; `data` is JSON:

| `type` | Action |
| --- | --- |
| `assistant` | Fork copies are ignored: skip rows whose `data.time.created < session_v2.time_created - 2000`. If `data.time.streamed` is absent the request is in flight: phase `generating`, `inFlightStartMs = data.time.created`, no response yet. Otherwise emit a response (key = row `id`) when `data.tokens` is an object: `startMs = data.time.created`, `endMs = data.time.streamed` (the stream end; tool execution between `streamed` and `completed` is not generation), `final = true`, `model = data.model.id`, tokens `input = tokens.input + tokens.cache.read + tokens.cache.write`, `cachedInput = tokens.cache.read`, `cacheWriteInput = tokens.cache.write`, `output = tokens.output + tokens.reasoning` (OpenCode reports reasoning separately), `reasoning = tokens.reasoning`. Phase afterwards: `tools` when `data.finish == "tool-calls"`, else `idle`. A later update of the same row replaces the response (4.4). |
| `compaction` | When `data.tokens` is an object: a response with the same token mapping, `startMs = data.time.created ?? null`, `endMs = data.time.completed ?? time_updated`, `model = data.model.id`. No phase change. |
| `idle` | phase `idle` |
| anything else | no change |

Verified on real data: per-session sums of these responses equal OpenCode's own `session_v2.tokens_*` totals except
for small background calls (titles) that OpenCode never stores as messages.

### 4.6 Gemini CLI

#### Format eras

| Versions | Session file | What changes for us |
| --- | --- | --- |
| ≤ v0.38 | `session-*.json` (sub-agents `<id>.json`): one pretty-printed JSON object, rewritten in place | Legacy snapshot (below) |
| v0.39 – v0.63 | `*.jsonl`, append-only | Full-history checkpoints `{"$set":{"messages":[…]}}` (v0.44+); failed requests (v0.54+) and aborted requests (v0.57+) are rolled back with a checkpoint |
| ≥ v0.64 | `*.jsonl`, append-only | `$patch` deltas replace checkpoints; rollbacks write `$rewindTo` |

Other version boundaries used below: ≤ v0.31 a response without text is first written when its tool calls finish;
v0.44+ every tool-result submission writes a `user` record; v0.54+ usage and thoughts are written only for the
successful stream attempt; v0.37+ sub-agents live in a per-parent directory; v0.29+ project directories are slugs.

#### Layout and discovery

- `<root>/<projectDir>/chats/<file>` — main sessions. `<file>` = `session-<YYYY-MM-DDTHH-MM>-<sessionId[0:8]>.jsonl`
  (UTC minute; a same-minute collision inserts `-<n>` before the short id).
- `<root>/<projectDir>/chats/<parentSessionId>/<sessionId>.jsonl` — sub-agents (v0.37+). Both ids have every
  character outside `[A-Za-z0-9_-]` replaced by `_`. Nested sub-agents are stored flat under the *main* session id.
- v0.33 – v0.36 wrote sub-agents as `session-*.json` directly in `chats/` (identified only by `kind`).
- `<projectDir>` is a slug of the project folder name (`agent-speed`, `agent-speed-1`, …) containing a `.project_root`
  file with the absolute project root (v0.29+; lowercased on Windows), or, for older versions, the SHA-256 hex of
  the project root. `<root>/../projects.json` maps project roots to slugs.
- Discovery: regular files (never symlinks) ending in `.jsonl` or `.json` whose path relative to the root is exactly
  `<projectDir>/chats/<file>` or `<projectDir>/chats/<dir>/<file>`. Nothing else under `tmp` is a session
  (`checkpoint-*.json`, `logs.json`, tool outputs, plans). Temporary and backup files (`*.tmp-<pid>`,
  `*.unreadable-<ms>`) do not match.
- Docker/Podman sandbox sessions are written to an ephemeral host temp directory and are out of scope.

`.jsonl` files use the line reader of section 5. `.json` files are whole-document snapshots (Legacy, below).
The startup reading order (section 5) covers all Gemini roots together.

#### Record classification

Every line is one JSON object, classified in this order (Gemini's own loader uses the same order):

1. `$rewindTo` is a string → rewind
2. `$patch` is an object → patch
3. `id` is a string → message record (`type`: `user`, `gemini`, `info`, `warning`, `error`)
4. `$set` is an object → metadata update
5. `sessionId` and `projectHash` are strings → header (first line of every file)
6. anything else → ignored

A message record is the complete message. The same `id` is appended again whenever the message changes (tool calls
attached, rarely tokens attached late, tool results masked); a later record replaces the earlier one and keeps the
original `timestamp`. A message `timestamp` is its creation time, never its update time. A tool call `timestamp` is the
time its batch of tool calls finished (all calls of a batch share it).

#### Timing model

- A `user` record is written immediately before the API request is sent: after compression, tool-output masking, model
  routing and loop detection, before `BeforeModel` hooks and connection retries. From v0.44 tool results are submitted
  as a `user` record too; before v0.44 the next request starts right after the tool calls are recorded on the previous
  `gemini` message.
- A `gemini` record is created once, after the last stream chunk (and after `AfterModel` hooks), already carrying the
  final `tokens` (from v0.32; earlier only when the response had text). The stream start is never recorded.
- Therefore `startMs` = latest `user` record, or the tool-call completion time when no `user` record followed it;
  `endMs` = the `gemini` record `timestamp`. The duration includes time to first token, 429 back-off and mid-stream
  retries of the same request.

State: `sessionId`, `pendingStartMs`, `pendingMarker` (id of the record that set `pendingStartMs`),
`seenToolCallIds` (set), `phase`; all start empty / `idle`.

| Record | Action |
| --- | --- |
| header | `sessionId`; `isSubagent` when `kind == "subagent"`; `startTime`, `lastUpdated` count for `lastLineMs`. A header that carries a `messages` array (a legacy object written on one line) is replayed like a legacy file. |
| `$set` | `lastUpdated` counts for `lastLineMs`; `kind`, `sessionId` as in the header; a non-empty string `summary` → phase `idle`, `pendingStartMs = null` (a sub-agent writes it when it finished; a main session only receives it from a later process); a `messages` array → checkpoint (below). Other keys are ignored. |
| `$rewindTo` | phase `idle`, `pendingStartMs = null` (user `/rewind`, cancelled or failed request). Responses are never removed. |
| `$patch` | if phase is `generating` and `$patch.removeIds` contains `pendingMarker` → phase `idle`, `pendingStartMs = null`. Nothing else. |
| `user` | `pendingStartMs = ts`, `pendingMarker = id`, phase `generating` (`inFlightStartMs = ts`) |
| `error` | phase `idle`, `pendingStartMs = null` (the interactive CLI writes it when a request failed) |
| `info` whose `content` is exactly the string `Request cancelled.` | phase `idle`, `pendingStartMs = null` (the interactive CLI writes it when the user cancels, before any rollback record) |
| other `info`, `warning`, other types | only `lastLineMs` |
| `gemini` | see below |

Message records without a parseable `timestamp` are ignored.

`gemini` record:
1. `key = id`. If the key was first seen in another file, ignore the record entirely (including its tool calls).
2. If `tokens` is an object:
   - New key: emit a response with `startMs = pendingStartMs`, `endMs = ts`, `final = true`, `model = model`. Exception:
     when the record already carries tool calls with parseable timestamps and `ts >=` the earliest of them, the
     message was created when its tools finished (≤ v0.31), the stream end is unknown, and `startMs = null`. Then
     `pendingStartMs = null`, `pendingMarker = null`. Phase `tools` when `content` has no visible text (a string
     that is empty after trimming, or no non-thought `text` part) or contains a `functionCall` part; otherwise `idle`.
   - Known key (same file): replace the tokens, `model = model ?? previous`, `endMs = max(endMs, ts)`. Phase unchanged.
3. Tool calls: the entries of `toolCalls` that have a string `id` not yet in `seenToolCallIds` are new (add them).
   If there are new calls:
   - one of them is named `complete_task` with `status == "success"` → phase `idle`, `pendingStartMs = null`
     (a sub-agent finished);
   - else all new calls have `status == "cancelled"` → phase `idle`, `pendingStartMs = null` (nothing is submitted);
   - else phase `generating`, `pendingStartMs` = the latest `timestamp` of the new calls (else `ts`),
     `pendingMarker = id`, `inFlightStartMs = pendingStartMs`.
4. A `gemini` record without `tokens` and without new tool calls changes nothing (synthetic turns: compression
   acknowledgement, binary-content acknowledgement, interrupted-response placeholder).

Step 2 runs before step 3, so a record that both creates a response and reports finished tools ends in the phase of
step 3.

Checkpoint (`$set.messages`, v0.44 – v0.63): for every `gemini` message in it that has `tokens` and whose key is not
owned by another file: a known key is updated as in step 2; an unknown key (only possible when reading started
mid-file) is emitted with `startMs = null`. A checkpoint changes no other state, except: if phase is `generating` and
no message in it has `id == pendingMarker`, the in-flight request was rolled back → phase `idle`,
`pendingStartMs = null`.

Tokens (`tokens` is Gemini's `usageMetadata`; missing fields are 0, negatives clamp to 0):

| Field | Value |
| --- | --- |
| `input` | `tokens.input + tokens.tool` |
| `cachedInput` | `tokens.cached` |
| `cacheWriteInput` | 0 |
| `output` | `tokens.output + tokens.thoughts` |
| `reasoning` | `tokens.thoughts` |

`tokens.input` is `promptTokenCount` (already includes cached content); `tokens.output` is `candidatesTokenCount`
(excludes thoughts); `tokens.tool` is `toolUsePromptTokenCount`: results of server-side tools (search grounding, code
execution, URL context) fed back to the model as input and not part of `promptTokenCount`. `tokens.total` is the sum of
all four, so normalized `input + output == total`. `tokens.total` itself is not used.

Metadata:
- `lastLineMs` = the latest parseable timestamp among header/`$set` `startTime` and `lastUpdated`, message `timestamp`,
  `toolCalls[].timestamp` and `thoughts[].timestamp` of every record, including records ignored by deduplication.
  Messages inside checkpoints are not scanned.
- `model` = the model of the latest response created or updated in this file.
- `project` = basename of the trimmed contents of `<projectDir>/.project_root` (split on `/` and `\`); else the key of
  `<root>/../projects.json` `projects` whose value equals the `<projectDir>` name; else null. The session file itself
  never records the working directory. Read once per project directory and cache.
- `isSubagent` = `kind == "subagent"` (header or `$set`), or the file is inside `chats/<dir>/`.

#### Legacy `.json` (≤ v0.38)

The file is one JSON object `{sessionId, projectHash, startTime, lastUpdated, messages[], kind?, directories?,
summary?}`, rewritten in place (not atomically) on every change. Whenever its size or modification time changes, read
the whole file (files over 64 MiB are not read and carry the `partialHistory` warning) and parse it. On a parse failure
(caught mid-write) count one malformed line and keep the previous state. On success reset the file's parser state
(never the dedupe set) and replay: the object as a header, then each `messages` element in order as a message record
with the rules above. Responses of earlier snapshots missing from the new one (rewound) stay recorded; same-key
emissions are updates (4.4). The startup rules of section 5 apply (files modified before `todayStartMs - 15 min` are
not read).

#### Deduplication and copied history

- `key` = message `id` (random UUID), shared global dedupe set (3.4): the first file that emits a key owns it.
- Resuming a `.jsonl` session appends to the same file; nothing is copied.
- Resuming a legacy `.json` session (v0.39+) copies its header and every message, unchanged, into a new file with the
  same name plus `l` (`….json` → `….jsonl`); the `.json` stays. The copied responses are owned by the `.json` (older
  modification time, read first) and ignored. If the `.json` was not read, the copies count with their original
  timestamps, normally before today.
- When a `.jsonl` cannot be reloaded on resume, Gemini rewrites it atomically (rename): the identity change resets the
  file (section 5); the same keys merge as same-file updates.
- `$set.messages` checkpoints repeat messages already in the file; they merge by key.

#### Known approximations

- `tools` vs `idle` after a response: Gemini writes a response's tool calls only after they finish, so a response
  that has text *and* calls tools shows `idle` while its tools run (and while they await approval). A final answer
  with empty text after a tool result shows `tools` until the 300-second staleness rule.
- Before v0.44 the next request's start is the tool-completion time, which also includes compression, masking and
  routing that run before the request.
- Up to v0.53 Gemini writes a `gemini` record for an invalid stream attempt before retrying; the retry's response
  then has `startMs = null` (totals only). v0.54+ record only the successful attempt, so tokens of failed attempts are lost.
- LLM calls that Gemini does not record (chat compression, model-routing classifier, next-speaker check, loop
  detection, session summaries, helper tools) are not counted; totals are lower than Gemini's `/stats`.
- Before v0.54 failed requests are not rolled back and before v0.57 cancelled ones are not: the interactive CLI's
  `error` / `Request cancelled.` records end the phase; in `-p` and ACP sessions the phase stays `generating` until
  staleness. A quota fallback that adds tool results without sending them also leaves `generating` until staleness.
- If the previous `gemini` message has no tokens when a new response's usage arrives, Gemini attaches the new usage
  to that older message: the response is emitted under the older key with an invalid interval (totals only).
- Derived from the Gemini CLI source (v0.66, evidence in `spec/research/gemini-cli.md`); not yet verified against real
  session files.

### 4.7 omp (oh-my-pi)

omp (`can1357/oh-my-pi`, MIT) is a fork of pi (`badlogic/pi-mono`) with its own JSONL session files. Upstream pi
(`~/.pi/agent/sessions`, no request durations) is out of scope. Every model response is written once, complete, after
its stream ended, with omp's own request start (`message.timestamp`) and measured request duration
(`message.duration`); nothing is written while a request is in flight. Evidence: `spec/research/omp.md`.

#### Format eras

| Versions | Change | Effect on these rules |
| --- | --- | --- |
| ≤ 2.3 | binary `pi`, root `~/.pi/agent/sessions`, session version 2 | out of scope |
| 3.0 – 6.9.0 | root `~/.omp/agent/sessions`, session version 3; assistant messages have no `duration` | `endMs` = entry timestamp |
| 6.9.69+ | `duration` and `ttft` on assistant messages | `endMs = timestamp + duration` |
| 16.3+ | the first physical line is a fixed 256-byte `title` slot, rewritten in place | ignored |
| 17.2.5 – 17.2.8 | bucket folders named `<scope>-<basename>-<sha256>` (17.2.9 migrates them back) | none |
| 18.1+ | `model_usage` entries (side calls), `completedAt` on assistant messages, `runtime` on entries | side calls count in totals; second `endMs` fallback |

omp migrates older session files on load and rewrites them on its next write; files written today come almost
entirely from 18.x.

#### Roots and discovery

- Automatic roots (section 2): the default root, then one root per named profile. Profile names are the folders in
  `~/<cfg>/profiles` and, on macOS when `$XDG_DATA_HOME` is set, in `$XDG_DATA_HOME/omp/profiles`. A profile's root is
  `$XDG_DATA_HOME/omp/profiles/<name>/sessions` on macOS when `$XDG_DATA_HOME/omp/profiles/<name>` exists, else
  `~/<cfg>/profiles/<name>/agent/sessions`. Profiles ignore `$PI_CODING_AGENT_DIR`. `XDG_*` variables are never used on
  Windows. Roots that do not exist are skipped; a path listed twice is scanned once.
- Sessions written to custom folders (`--session-dir`, `--session`, `PI_CODING_AGENT_SESSION_DIR`; omp lists them in
  `<agent dir>/custom-session-files/`) are read only when the user setting points at their folder.
- A root that is also the Pi root (4.13), or lies inside it, is not read as omp: omp inherited Pi's
  `PI_CODING_AGENT_DIR`, and before Pi was a source users pointed the omp setting at `~/.pi/agent/sessions`. Those
  files are Pi's.
- Layout: `<root>/<bucket>/<YYYY-MM-DDTHH-MM-SS-mmmZ>_<sessionId>.jsonl` are main sessions (`<bucket>` encodes the
  working directory, `sessionId` is a UUIDv7); `<root>/<bucket>/<stem>/<name>.jsonl` belong to the session
  `<stem>.jsonl` (task sub-agents `<AgentId>.jsonl`, advisors `__advisor.jsonl` / `__advisor.<slug>.jsonl`, seance
  consults); a sub-agent's own children nest the same way under `<stem>/<AgentId>/`.
- Discovery: regular files (never symlinks) whose name ends in `.jsonl` and whose path relative to the root has at
  least two components, within the depth limit of section 2. Every file or folder whose name starts with `.` is
  skipped (`omp gc --compress` stages copies in `.omp-gc-compress-*`; rewrite temps are `.<name>.jsonl.<id>.tmp`).
  Windows move-aside backups (`*.jsonl.<id>.bak`), archives (`*.jsonl.gz` under `<agent dir>/archive`, outside the
  root) and artifacts (`*.log`, `*.md`, `*.patch`, `draft.txt`) do not match. Collab replicas (`~/.omp/collab`),
  `omp render` working copies and sub-agents of `--no-session` runs are outside every root.

Writes are single-line appends or full rewrites through a temp file renamed over the transcript (a new identity:
section 5 resets the file). The title slot is overwritten in place without changing the file size.

#### Record classification

Every line is one JSON object, classified by `type`:

1. `title` — the title slot. Ignored entirely.
2. `session` — the header. Only the first one counts; later headers are ignored entirely.
3. `message` — by `message.role`: `user`, `developer`, `assistant`, `toolResult`; any other role (`bashExecution`,
   `pythonExecution`, `fileMention`, `custom`, `hookMessage`) only updates `lastLineMs`.
4. `model_usage` — a side model call.
5. `custom` with `customType == "session_exit"` — the omp process ended.
6. anything else (`model_change`, `thinking_level_change`, `compaction`, `branch_summary`, `custom_message`, other
   `custom`, `label`, …) — only `lastLineMs`.

Lines before the header are ignored. Entries other than the header without a string `id` (version-1 files from before
2026) or without a parseable `timestamp` are ignored entirely. When reading a file starts at an offset greater than 0
(section 5), its header is read first: the first line of type `session` among the first two lines of the file (the
title slot precedes it since 16.3).

#### Timing model

- An assistant `message` entry is appended once, at the end of its stream and before its tools run. Partial streaming
  output is never persisted; there is no record while a request is in flight.
- `message.timestamp` (Unix ms) is taken when the provider call starts, immediately before the request is built and
  sent; it is never rewritten (not on in-provider retries, not on copies). `message.duration` (ms, monotonic) runs from
  that instant to the end of the stream. `message.completedAt` (18.1+) is the wall clock when the message ended.
- Therefore `startMs = message.timestamp`; `endMs = startMs + duration` (truncated to whole milliseconds; `duration`
  has sub-millisecond digits) when `startMs` is known and `duration` is a finite number ≥ 0, else `completedAt`
  (truncated) when it is a finite number > 0, else the entry `timestamp`. The interval includes request building, time
  to first token and in-provider retries with their back-off; it excludes agent-level work before the call.
- The input entry (`user`, `developer`), or the tool result that completes a batch, is written a few milliseconds
  before the next request starts; it starts the in-flight estimate.

State: `headerMs` (nullable), `pendingToolCalls` (set of ids), `phase` (starts `idle`).

| Record | Action |
| --- | --- |
| header | `headerMs` = its `timestamp`; `project` from `cwd`; the header `timestamp` counts for `lastLineMs` |
| any entry whose `timestamp` is earlier than `headerMs - 2000` | copied history (forks, branches, imports, moves): ignore completely |
| `message` / `user` or `developer` | phase `generating`, `inFlightStartMs = ts`; `pendingToolCalls` cleared |
| `message` / `assistant` | response (below) |
| `message` / `toolResult` | remove `message.toolCallId` from `pendingToolCalls`. If `message.toolName == "yield"` and `message.isError` is not `true` → phase `idle` (a sub-agent's final yield ends its run). Else if phase is `tools` and `pendingToolCalls` is empty → phase `generating`, `inFlightStartMs = ts`. |
| `model_usage` | side-call response (below); no phase change |
| `custom` / `session_exit` | phase `idle`, `pendingToolCalls` cleared |
| anything else | only `lastLineMs` |

`message` / `assistant` (`m` = `message`):
1. Skip the entry (only `lastLineMs`) unless `m.model`, `m.provider` and `m.api` are strings and `m.usage` is an
   object (omp's own statistics apply the same guard).
2. `stamp` = `m.timestamp` truncated to an integer when it is a finite number > 0, else the entry `timestamp` (ms).
   `key = "omp:" + id + ":" + stamp` (decimal). If the key was first seen in another file, ignore the entry: no
   response and no phase change (it still counts for `lastLineMs`). A repeat in the same file is an update (4.4).
3. `stopReason` = `m.stopReason`; a missing or non-string value counts as `aborted`.
4. When at least one of `usage.input`, `usage.output`, `usage.cacheRead`, `usage.cacheWrite` is > 0, emit a response:
   `startMs = m.timestamp` when it is a finite number > 0 (else null), `endMs` as in the timing model, `final = true`,
   `model = m.model`, tokens below. When `stopReason` is `error` or `aborted`, `startMs = null` (totals only: failed
   and interrupted requests carry partial or no output). Messages whose four fields are all 0 (abort placeholders, the
   synthetic message that closes a resumed interrupted turn) produce no response.
5. Phase, also when step 4 emitted nothing: `stopReason` `error` or `aborted` → `idle`, `pendingToolCalls` cleared.
   Else when `m.content` has blocks of type `toolCall` with a string `id` and `stopReason` is `toolUse`, `stop` or
   `length` → `tools`, `pendingToolCalls` = those ids (omp runs tool calls under `stop` too; under `length` it writes
   skipped results and continues). Else when `stopReason == "stop"` and `m.stopDetails.type == "pause_turn"` →
   `generating`, `inFlightStartMs = endMs` (omp samples again). Otherwise `idle`.

`model_usage` (18.1+) records side calls billed to the session (`purpose`: `auto-thinking`, `job-widget`, `ttsr`,
`memory`, `find`, `task-completion-probe`, `auto-prewalk`, `auto-graph`, `unexpected-stop`, …). Same guard as step 1
on the entry's own `model`, `provider`, `api` and `usage`; `key = "omp:" + id + ":" + <entry timestamp ms>`,
deduplicated like step 2. When one of the four token fields is > 0, emit `startMs = null`, `endMs = ts`,
`final = true`, `model = model`. These responses never set the agent's `model`.

Verification target (once real files are available): per file, the token sums equal what `omp stats` stores for it (it
reads exactly the assistant messages and `model_usage` entries), and each response's speed equals omp's own
`output * 1000 / duration`.

#### Tokens

`usage` is omp's normalized `Usage` (every provider maps into it). Missing or non-numeric fields are 0, negatives clamp
to 0.

| Field | Value |
| --- | --- |
| `input` | `usage.input + usage.cacheRead + usage.cacheWrite` |
| `cachedInput` | `usage.cacheRead` |
| `cacheWriteInput` | `usage.cacheWrite` |
| `output` | `usage.output` |
| `reasoning` | `usage.reasoningTokens` (absent → 0) |

`usage.input` is non-cached input only; `usage.output` already includes thinking and tool-call arguments, and
`reasoningTokens` is a subset of it (absent when the provider does not report it). `usage.totalTokens` and
`usage.orchestration` are not used (see approximations).

#### Metadata

- `project` = basename of the header `cwd` (split on `/` and `\`); null before the header is read.
- `model` = `message.model` of the latest assistant response created or updated in this file.
- `isSubagent` = the file's path relative to its root has three or more components (it lives inside a session's
  artifacts folder: task sub-agents, advisors, seance consults). The header's `parentSession` is not used (forks and
  branched main sessions carry it too).
- `lastLineMs` = the latest parseable `timestamp` of the header and of every entry that is not copied history,
  including entries ignored by deduplication and entries without usage.

#### Deduplication and copied history

- The key combines the entry `id` with the message's own `timestamp`; both survive every copy omp makes, and omp's
  statistics deduplicate on the same pair (first file wins). The shared dedupe set (3.4) applies.
- Copies with a fresh header: `/fork`, `/tree` branches, `--fork`, seance consults, the move to a sibling file when
  another process owns the session, `persistCopy`. Copied entries keep their `id` and `timestamp` while the new header
  gets the current time, so the copied-history rule removes them even when the source file is gone. A header
  timestamp is set only when a session identity is created, never by lazy materialization or rewrites.
- Imports: `/resume @claude` converts a Claude Code transcript into a new omp session, keeping each record's original
  timestamp and Claude's usage under new entry ids (`/resume @codex` imports no usage). Those API calls are already
  counted under `claudeCode` with other keys; the copied-history rule removes them.
- Copies without a fresh header: forks and moves also copy the source's artifacts folder in the background, which
  duplicates its sub-agent transcripts byte for byte. Only the dedupe set removes them; the copies are extra idle
  agents without responses of their own.
- Rewrites of the same file (migrations, `/rename` without a title slot, discarding a failed retry turn, recovery,
  `omp gc --compress` on macOS, which replaces the file): section 5 resets the file and the same keys merge as
  same-file updates. A response removed by a rewrite stays recorded.
- Resume appends to the same file.

#### Known approximations

- `usage.orchestration` (provider-side orchestration tokens: billed and part of `totalTokens`, but not part of the
  conversation) is not counted, so normalized `input + output` can be lower than `totalTokens`.
- Error and aborted responses count in totals only; their partial streams never contribute speed.
- In-flight state is inferred from neighbouring records. A brand-new session has no file until its first assistant
  message completes, so the first request of every new session is invisible while in flight (it appears with its full
  timing when it ends).
- Agent-level retries (overload, rate limit, network) start a new request without writing a record: the phase stays
  `idle` during the back-off and the retried request; its response still has exact timing.
- Interrupting while tools run writes aborted tool results and ends the turn without a final record: the phase shows
  `generating` until the 300-second staleness rule.
- In-provider retries keep the original `timestamp`, so their back-off lowers the measured speed. Providers that run
  tools inside one stream (Cursor exec channel, Devin, GitLab Duo workflows) include that tool time in `duration`.
- Calls omp does not record are not counted (compaction summaries, titles, branch summaries, unpersisted refusals and
  empty error turns, every side call before 18.1); totals match `omp stats`, not the provider bill.
- GitLab Duo writes no `duration`; before 18.1 its responses end at the entry timestamp (a few ms after the stream).
- Sub-agents nested deeper than the depth limit are not tracked.
- Derived from the omp source (v18.12.6, evidence in `spec/research/omp.md`); not yet verified against real session
  files.

### 4.8 Antigravity

Google Antigravity (the `agy` CLI, which replaced Gemini CLI for personal accounts on 2026-06-18; the 2.x desktop app;
the Antigravity IDE; IDE extensions) runs a local language server that keeps one SQLite database per conversation.
Every model call is a `PLANNER_RESPONSE` step row that carries its own usage and timestamps. Evidence:
`spec/research/antigravity.md`.

#### Format eras

| Era | Conversation store | Effect on these rules |
| --- | --- | --- |
| Antigravity IDE 1.x, agy before 1.0.7 | `conversations/<id>.pb`, `implicit/<id>.pb`: encrypted protobuf | out of scope (never decrypted) |
| agy 1.0.7 – 1.4.4, desktop app and IDE 2.x (since about June 2026) | `conversations/<id>.db`: SQLite (WAL) with protobuf blobs | read (below) |
| forks in agy 1.2.17 | both `<id>.pb` (at fork time) and `<id>.db` (once the fork continues) | only the `.db` |
| newer builds | also `brain/<id>/.system_generated/logs/transcript.jsonl` | out of scope (no usage) |

The database schema (`PRAGMA user_version = 1`, 7 tables) and the protobuf field numbers below are unchanged from agy
1.0.7 to 1.4.4; the IDE writes the same schema (verified at 2.2.1).

#### Stores and discovery

- Stores (section 2): `~/.gemini/antigravity-cli` (CLI), `~/.gemini/antigravity` (desktop app, IDE extensions, IDE
  1.x), `~/.gemini/antigravity-ide` (IDE 2.x). A store is the language server's `--app_data_dir`; no environment
  variable moves it. A store folder whose name ends in `-backup` (`antigravity-backup` holds a copy of `antigravity`)
  is never read, not even when the user setting names it. A user setting that names a folder called `conversations`
  is used as that folder directly.
- Discovery: regular files (never symlinks) directly inside `<store>/conversations/` whose name is a UUID (8-4-4-4-12
  hexadecimal digits, any case) followed by `.db`. Not sessions: `*.db-wal`, `*.db-shm`, `*.pb`, `implicit/`,
  `*.trajectory.json` (sidecars of third-party exporters), `conversation_summaries.db` (in the store folder), `brain/`,
  `annotations/`, `log/`.
- Each database is one agent: agent id = absolute path of the `.db`; conversation id = its file name without `.db`. It
  counts as one file for discovery statistics and the 20 000-file limit.
- Gemini CLI (4.6) never reads below these stores, so nothing is counted twice between the two sources.

#### Opening and querying

Each database is opened with the rules of 4.5: read-only (`mode=ro`, never written), busy timeout 2 s, short read
transactions; when the header says WAL (bytes 18 and 19 are both 2) and no `-wal` file exists (a closed
conversation), open with `immutable=1`; otherwise open `mode=ro`, falling back to `immutable=1` only when no `-wal`
file exists; an immutable connection is dropped on the next change of the `.db` or as soon as a `-wal` appears. Never
create a file. A database that cannot be opened or queried contributes nothing and is retried on its next change; one
without a `steps` table with the columns below carries the warning `unsupportedFormat` and contributes nothing (no agent).

Only these queries run (`?` = the cursors of 5.4, `-1` for a complete read). The size guards keep large blobs unread:
`gen_metadata` rows embed the whole prompt and reach hundreds of MB, so a row whose `size` column exceeds 1 MiB is
never read, even when its blob would be small.

```sql
SELECT idx, step_type, status, CASE WHEN length(metadata) <= 1048576 THEN metadata END FROM steps WHERE idx > ? ORDER BY idx, rowid
SELECT idx, CASE WHEN typeof(size) = 'integer' AND size <= 1048576 AND length(data) <= 1048576 THEN data END FROM gen_metadata WHERE idx > ? ORDER BY idx, rowid
SELECT idx, CASE WHEN length(data) <= 1048576 THEN data END FROM executor_metadata WHERE idx > ? ORDER BY idx, rowid
SELECT CASE WHEN length(data) <= 1048576 THEN data END FROM trajectory_metadata_blob ORDER BY rowid LIMIT 1
```

`step_payload` (prompts, responses, file contents) and the other blob columns are never read. NULL integers count as
0; a NULL blob (absent or too large) is skipped without counting as malformed. The tables have no primary key: rows
with the same `idx` are processed in `rowid` order, the later one replacing the earlier.

#### Protobuf wire format

Blobs are protobuf messages, decoded by a small hand-written walker (no protobuf library). A message is a sequence of
fields; each starts with a varint key, field number `key >> 3`, wire type `key & 7`:

| Wire type | Payload |
| --- | --- |
| 0 | varint: at most 10 bytes, 7 bits each, least significant group first |
| 1 | 8 bytes (fixed64, double) |
| 2 | a varint length, then that many bytes (string, bytes, nested message, packed repeated scalars) |
| 5 | 4 bytes (fixed32, float) |

Field number 0, wire types 3, 4 (groups), 6 and 7, a varint longer than 10 bytes, and a field that runs past the end of
its buffer make the blob malformed. Unknown fields of every valid wire type are skipped, and so is a known field
whose wire type differs from the one its type uses (except packed repeated scalars). proto3 omits default values:
a missing number is 0, a missing string is empty, a missing message is absent. A non-repeated field that occurs more
than once takes its last occurrence; where one value of a repeated message field is needed, its first occurrence is
used. Repeated scalars (`step_indices`) are accepted packed (wire type 2) and unpacked (wire type 0). Varints are
unsigned 64-bit; `int32` and `int64` fields reinterpret them as signed 64-bit (negative `int32` values are 10-byte
varints). Strings are UTF-8 (invalid sequences become U+FFFD). A `Timestamp` is `{1: seconds (int64), 2: nanos
(int32)}`; it is valid when `seconds > 0` and then equals `seconds * 1000 + nanos div 1 000 000` ms (nanos outside
0 … 999 999 999 count as 0). A blob that is malformed, including any nested message these rules read, counts as one
malformed line and is ignored.

Fields used (all others are ignored):

| Message (column) | Fields (number) |
| --- | --- |
| `CortexStepMetadata` (`steps.metadata`) | `created_at` 1, `viewable_at` 6, `finished_generating_at` 7, `completed_at` 8, `model_usage` 9, `last_completed_chunk_at` 22, `started_at` 32 (all `Timestamp` except 9) |
| `ModelUsageStats` | `input_tokens` 2, `output_tokens` 3, `cache_read_tokens` 5, `message_id` 7 (string), `thinking_output_tokens` 9, `response_output_tokens` 10, `response_id` 11 (string) |
| `CortexStepGeneratorMetadata` (`gen_metadata.data`) | `chat_model` 1, `step_indices` 2 (repeated uint32) |
| `ChatModelMetadata` | `response_model` 19 (string), `model_display_name` 21 (string) |
| `ExecutorMetadata` (`executor_metadata.data`) | `termination_reason` 1 (enum), `last_step_idx` 3 (int32), `cascade_config` 10 → `planner_config` 1 → `model_name` 28 (string) |
| `CortexTrajectoryMetadata` (`trajectory_metadata_blob.data`) | `workspaces` 1 (repeated) → `workspace_folder_absolute_uri` 1 (string); `created_at` 2; `parent_conversation_id` 5 (string); `workspace_uris` 7 (repeated string); `subagent_spec` 8; `nesting_depth` 17 (int32) |

Column enums: `step_type` 14 = `USER_INPUT`, 15 = `PLANNER_RESPONSE`; every other value is a tool or system step (for
example 8 `VIEW_FILE`, 21 `RUN_COMMAND`, 127 `INVOKE_SUBAGENT`). `status` 1 `PENDING`, 2 `RUNNING`, 3 `DONE`,
4 `INVALID`, 5 `CLEARED`, 6 `CANCELED`, 7 `ERROR`, 8 `GENERATING`, 9 `WAITING`, 11 `QUEUED`, 12 `INTERRUPTED`.
*Open* statuses are 1, 2, 8, 9 and 11; every other value is *terminal*. `ModelUsageStats.model` and
`CortexStepMetadata.generator_model` are enums whose current values are placeholders; they are not used.

#### Step rows

The trajectory metadata gives `createdMs` (= `created_at`), `project`, `isSubagent` and the parent link (6.7); while
it is missing, every read retries it.

Rows of `steps` are processed in `idx` order:
1. Skip a row whose `metadata` is NULL. Skip a row and count one malformed line when its `metadata` does not decode or
   a token field of its `model_usage` exceeds 10 000 000.
2. If `createdMs` is known, `created_at` is valid and `created_at < createdMs - 2000`, the row is copied history (a
   fork): ignore it completely.
3. Every valid timestamp of the row's metadata counts for `lastLineMs`. The row with the highest `idx` processed so far
   is the *latest row* (a re-read row replaces its earlier version).
4. When `step_type == 15`, the status is terminal, `model_usage` is present and one of its five token fields is > 0,
   the row is a response:
   - `key` = `model_usage.response_id` when non-empty, else `model_usage.message_id` when non-empty, else
     `"antigravity:" + conversation id + ":" + idx`. If the key was first seen in another database, no response.
   - `endMs` = `finished_generating_at`, else `last_completed_chunk_at`, else `completed_at`; `startMs` = `created_at`
     (null when invalid). When none of the three is valid, `endMs` = the latest valid timestamp of the row and
     `startMs = null`; a row without any valid timestamp gives no response.
   - Status 6, 7 or 12 (cancelled, error, interrupted) → `startMs = null` (totals only).
   - `final = true`, `model` as in Metadata, tokens below. A later version of the row is an update (4.4).

A `PLANNER_RESPONSE` row with an open status is in flight and produces no response yet, even when it already carries
usage.

#### Phase

After every read (the startup read included) the phase is a function of the rows read so far:
1. No latest row → `idle`.
2. An `executor_metadata` row with `termination_reason != 0` and `last_step_idx >=` the latest row's `idx` (the run that
   contains it has ended) → `idle`.
3. Otherwise by the latest row:

| Latest row | Phase |
| --- | --- |
| `PLANNER_RESPONSE`, open status | `generating`, `inFlightStartMs = created_at` |
| `PLANNER_RESPONSE`, terminal status | `idle` (a response with tool calls is followed by its tool rows at once) |
| `USER_INPUT`, status 6, 7 or 12 | `idle` |
| `USER_INPUT`, any other status | `generating`, `inFlightStartMs = created_at` (the model call follows at once) |
| other step, open status | `tools` |
| other step, `DONE` | `generating`, `inFlightStartMs = completed_at`, else `created_at` (the next model call) |
| other step, any other terminal status | `idle` |

An invalid timestamp makes `inFlightStartMs` null. The staleness rule (6.7) applies.

#### Timing model

- A `PLANNER_RESPONSE` step is created when the planner starts the model call and is filled while the stream arrives;
  `finished_generating_at` marks the end of generation. The duration therefore includes time to first token and the
  planner's retries of the same call (`retry_infos`), as with Gemini CLI.
- The matching `gen_metadata` row's `time_to_first_token + streaming_duration` should be close to `endMs - startMs`; it
  is not used (and agy ≤ 1.1.17's `chat_start_metadata.created_at` neither).

#### Tokens

`model_usage` of the planner row (`ModelUsageStats`); missing fields are 0.

| Field | Value |
| --- | --- |
| `input` | `input_tokens + cache_read_tokens` |
| `cachedInput` | `cache_read_tokens` |
| `cacheWriteInput` | 0 |
| `output` | `output_tokens`; when it is 0, `thinking_output_tokens + response_output_tokens` |
| `reasoning` | `thinking_output_tokens` |

Unlike Gemini CLI's `usageMetadata`, `input_tokens` excludes cached input, and `output_tokens` already includes
thinking (`output_tokens == thinking_output_tokens + response_output_tokens` on every audited real row).
`cache_write_tokens` (4) is deprecated and 0.

#### Metadata

- `model` of a response: `chat_model.response_model` of the `gen_metadata` row with the lowest `idx` whose
  `step_indices` contains the row's `idx`, else that row's `chat_model.model_display_name`; else the `model_name` of
  the `executor_metadata` row with the smallest `last_step_idx >=` the row's `idx` (ties: lowest `idx`); else null.
  Empty strings count as missing. The model is resolved from the rows read so far; when a later read changes it, the
  response is updated (4.4). Serving variants (`-exp-b`) and routing labels (`gemini-pro-default`) are shown as they
  are.
- Agent `model` = the model of the response with the highest `idx` that has one.
- `project` = basename of the first `workspace_uris` entry, else of the first `workspaces` entry's
  `workspace_folder_absolute_uri`, else null. A URI (a scheme of two or more characters, such as `file:` or
  `vscode-remote:`) loses its scheme and authority (`//host`) and is percent-decoded as UTF-8 (invalid escapes stay as
  they are) before splitting on `/` and `\`; `file:///c%3A/Users/dev/projects/orbit` gives `orbit`.
- `isSubagent` = `parent_conversation_id` non-empty, or `nesting_depth > 0`, or `subagent_spec` present.
- `lastLineMs` = the latest valid timestamp among the processed rows' metadata and `createdMs`.

#### Deduplication and copied history

- The shared dedupe set (3.4) applies to response keys: the first database that emits a key owns it. This removes the
  same conversation found in two stores (some machines hold byte copies in `antigravity` and `antigravity-ide`) and
  history copied by `/fork`.
- The copied-history rule (step 2) also drops forked rows that have no response id.
- `/rewind` and cleared steps never remove counted responses.

#### Known approximations

- Antigravity timing semantics are not yet verified against real data: the meaning of `created_at` and
  `finished_generating_at` for planner rows is inferred from field names, Windsurf-era semantics and one sanitized
  timing sample (1.65 s and 2.36 s for two short answers).
- Whether a planner row with status `GENERATING` is written before its stream ends is unknown; if it is not, the
  in-flight phase comes only from the user-input and finished-tool rules.
- Error, cancelled and interrupted planner responses count in totals only.
- Model calls that are not planner steps (context checkpoints, titles, summaries, side questions) exist only in
  `gen_metadata`, which is not scanned for responses, and usage of failed retry attempts (`retry_infos[].usage`) is
  not counted: totals are lower than Antigravity's own `/usage`.
- Usage written to a row after it reached a terminal status is seen only by the next complete read (startup, history).
- Legacy encrypted conversations, transcript-only conversations, the Gemini macOS app's Antigravity integration and
  the Python SDK runtime contribute nothing.
- Derived from public reverse-engineering projects and the protobuf descriptors of agy 1.0.10 – 1.4.4 (evidence in
  `spec/research/antigravity.md`); not yet verified against real conversation databases.

### 4.9 WorkBuddy

Tencent WorkBuddy is an Electron desktop agent in two editions built from one code base. They differ only in branding,
service and data folder, so they are two sources with one parser. Each conversation runs in an embedded CodeBuddy Code
CLI, which appends one plain-text JSONL transcript per session. No line records when a request started, so the start
is inferred from the line before it, as for Gemini CLI. Evidence: `spec/research/workbuddy.md`.

| Source | App (macOS bundle id) | Windows executable | Data folder | Service |
| --- | --- | --- | --- | --- |
| `workbuddy` | `WorkBuddy.app` (`com.tencent.workbuddy.mac`) | `WorkBuddy.exe` | `~/.workbuddy` | workbuddy.cn |
| `workbuddyAi` | `WorkBuddy AI.app` (`com.workbuddy.workbuddy-ai`) | `WorkBuddyAI.exe` | `~/.workbuddy-ai` | workbuddy.ai |

#### Format eras

Only one era is known: app 5.8.3 (China edition) and 5.8.4 (international edition), both embedding CodeBuddy Code
2.161.0 with byte-identical transcript code. Older and newer versions have not been inspected, so the parser is
tolerant: unknown line types and fields are ignored, and a timestamp may be a number or an ISO string.

#### Roots and discovery

- Root (section 2): `<data folder>/projects`. The user setting, per source, names the data folder or its `projects`
  folder and replaces that source's root. The app's own `WORKBUDDY_CONFIG_DIR` / `CODEBUDDY_CONFIG_DIR` are **not**
  honoured: both editions read the same names, `CODEBUDDY_CONFIG_DIR` also moves the standalone CodeBuddy Code CLI, and
  the app sets them only on itself and its children (a launched app sees no shell variables).
- The standalone CodeBuddy Code CLI (`~/.codebuddy`, same format) is not a WorkBuddy source. The Windows fallback
  folders of older releases (`C:\ProgramData\WorkBuddy\users\<hash>\.workbuddy` and others) are not read: 5.8 merges them
  into the data folder at launch.
- Layout: `<root>/<slug>/<session>.jsonl` is a main session (`<slug>` is the working directory with `/`, `\` and `:`
  replaced by `-`; `<session>` is a UUID); `<root>/<slug>/<session>/subagents/agent-<id>.jsonl` is a sub-agent, filed
  under its main session (the code files a sub-agent under its parent's runtime session id, which for a main session is
  the file name).
- Discovery: regular files (never symlinks) whose name ends in `.jsonl` and does not start with `.`, whose path
  relative to the root is exactly `<slug>/<file>` or `<slug>/<dir>/subagents/<file>`, where neither `<slug>` nor
  `<dir>` starts with `.` and `<slug>` is not `cloud`. Nothing else is a transcript: `<session>.meta.json` and legacy
  `agent-<id>.meta.json`, `<session>/tool-results/`, `<session>/subagents/workflows/**/journal.jsonl`, and everything
  outside `projects` (`traces/`, `logs/`, `sessions/`, `usage-data/`, `app/`, `models.json`, `settings.json`,
  `workbuddy.db`) is never opened.
- `cloud/` holds mirrors of conversations that ran on Tencent's servers, rebuilt from the service: every record of a
  request shares one timestamp, usage is per request and the files are rewritten when they are reconciled. They are not
  read; cloud conversations are out of scope like Claude Desktop chats (section 2), and DESIGN 5 says so.
- Writes are appends of one line at a time (a failed append is truncated back); forks write a new file. The CLI's own
  30-day cleanup is turned off by the app; its data-retention setting deletes whole files.

#### Line format and classification

Every line is one JSON object: `id`, `parentId`, `timestamp` (Unix ms, a number), `type`, `role`, then the item's own
fields, `providerData`, the session's working directory `cwd` (on every line except `summary`, `topic` and
`session-meta`) and `sessionId`. `timestamp` is valid when it is a finite number > 0, truncated (3.6: `0.5` gives 0,
`1e19` gives 2^63 − 1), or a timestamp string (3.1); lines without a valid timestamp are ignored entirely. Only `id`,
`type`, `role`, `status`, `timestamp`, `cwd`, `callId` and the `providerData` fields named below are read; content,
arguments and tool output never are (WorkBuddy has no task score, 6.9).

Values of the wrong type count as absent: `id` and `providerData.messageId` only as non-empty strings, `callId` only
as a string (a `callId` that is an object or an array opens and closes no call), `providerData` only as an object, flags
only as `true`. A repeated member takes its last value, as with `json.loads`. *Blank* means empty or whitespace only,
with Python's `str.isspace()` as whitespace (U+0009–U+000D, U+001C–U+0020, U+0085, U+00A0, U+1680, U+2000–U+200A,
U+2028, U+2029, U+202F, U+205F, U+3000); *trimmed* removes that whitespace at both ends.

| Line | Meaning |
| --- | --- |
| `message`, `role == "user"` | input; written just before the request it starts. With `providerData.skipRun == true` it starts no request (a note the CLI adds). |
| `function_call_result` | the result of one tool call (`callId`); the results of a step are written together when every tool of the step finished |
| `reasoning`, `function_call`, `message` with `role == "assistant"` | output items of one model call, written one by one after the stream ended. An assistant `message` with `providerData.skipRun == true` is a notice written by the CLI (hook output, interruption, error, content-filter notice), never model output. |
| `model-usage` | usage of a model call whose last output item lacked raw usage, or that had no output item to carry it (reasoning only, empty); written when the run ends, with the earlier time the stream ended |
| anything else (`session-meta`, `credit-usage`, `summary`, `ai-title`, `custom-title`, `topic`, `file-history-snapshot`, `goal-progress`, `goal-result`, `resend-fork-notice`, `turn-metrics`, `acp-terminal-state`, `role == "system"`, unknown types) | only `lastLineMs` and `cwd` |

#### Timing model

- The transcript has no request start, time to first token or duration (they exist only in the CLI's memory and
  telemetry; `turn-metrics` is never written by the runtime WorkBuddy uses, and it measures turns, not calls).
- A user line is appended after the `UserPromptSubmit` hooks, while the request is being assembled; compaction that the
  new message needs runs before it and writes its own lines first. A model call's output items are created after the
  stream's final event and appended a few milliseconds apart; the last one carries the usage. Tool results are stamped
  when the CLI picks them up, which can be a few milliseconds after the next request was sent.
- Therefore `startMs` = the latest input line before the call (the user line, or the tool result that completed the
  step) and `endMs` = the line that carries the call's usage (the latest when several do). The interval includes time
  to first token and the assembly of the request; there is no record while a request is in flight, so the phase comes
  from the input lines.

State: `pendingStartMs`, `openCalls` (tool call id → response key), `phase`.

| Line | Action |
| --- | --- |
| any line | `lastLineMs`; a string `cwd` that is not blank sets `project` and `cwdPath` (the latest wins) |
| user `message`, not `skipRun` | `pendingStartMs = ts`, `openCalls` cleared, phase `generating` (`inFlightStartMs = ts`) |
| user `message` with `skipRun` | nothing else |
| `function_call_result` | remove its `callId` from `openCalls`. Not `skipRun`: `pendingStartMs = ts`, and when `openCalls` is empty phase `generating` (`inFlightStartMs = ts`). With `skipRun` (a rejected or cancelled tool; no request follows): when `openCalls` is empty, phase `idle`, `pendingStartMs = null`. A tool that failed (`status == "incomplete"` without `skipRun`) continues the turn. |
| assistant notice (`skipRun`) | `status == "incomplete"` or `providerData.error` is an object (interrupted or failed request) → phase `idle`, `pendingStartMs = null`, `openCalls` cleared; otherwise nothing (hook output while tools run) |
| output item with `providerData.isPartialAborted == true` | nothing else (a draft saved on interruption, without usage; the notice follows) |
| other output items, `model-usage` | below |

Output items and `model-usage`:
1. `key = providerData.messageId` (a non-empty string; one per model call, on every item of the call — the call's tool
   results repeat it, but they are not output items), else `"workbuddy:" + id` (a non-empty string). A line with
   neither has no key and is ignored entirely, like a line whose key was first seen in another file: no response, no
   phase change, `openCalls` and `pendingStartMs` unchanged (only the any-line row applies).
2. Usage: when `providerData.usage` or `providerData.rawUsage` is an object and the normalized `input` or `output` is
   > 0 — a new key emits a response with `startMs = pendingStartMs`, `endMs = ts`, `final = true`, `model` and
   `provider` below, then `pendingStartMs = null`; a known key (same file) is an update (4.4): `endMs = max`, every
   token field `= max(previous, new)`, `model = new ?? previous`, `provider = new ?? previous` (so a `custom` model
   keeps its provider when a later line of the call lacks the prefix).
3. Phase:
   - `model-usage`: the run has ended → phase `idle`, `pendingStartMs = null`, `openCalls` cleared.
   - First, drop the `openCalls` entries of other keys (calls left without a result).
   - `reasoning`: nothing.
   - `function_call`: `openCalls[callId] = key`; phase `tools`.
   - assistant `message`: when `openCalls` is empty — `providerData.agent == "compact"` (a compaction call) → phase
     `generating`, `pendingStartMs = ts`, `inFlightStartMs = ts` (the request it made room for follows); otherwise
     phase `idle`. When calls of the same key are open (text written after them), nothing.

A tool waiting for the user's permission keeps the phase `tools`. There is no end-of-turn line: the turn ends with the
final assistant message.

#### Tokens

`providerData.usage` is agents-core's `Usage` (`inputTokens`, `outputTokens`, `inputTokensDetails[]`,
`outputTokensDetails[]`); `providerData.rawUsage` is the service's OpenAI-style usage of the same call and is sometimes
missing. Values follow 3.2: missing or non-numeric values are 0, numbers are truncated and clamped to 0 … 10^12.

- *The first value > 0 of* a list of candidates: the first candidate, in the order listed, that is a finite number
  > 0 (3.6), truncated. Later candidates are not consulted, even when the chosen value truncates to 0 (`0.5`); 0 when
  no candidate qualifies (a `1e999` or a non-number is skipped like a missing one). A candidate inside an object that is
  missing or not an object is missing.
- `inputTokensDetails` and `outputTokensDetails` are arrays of objects; a single object counts as an array of that one
  object. Entries that are not objects, and any other value, count 0.
- Σ and `cachedInput + cacheWriteInput` saturate at 10^12 (3.2).

| Field | Value |
| --- | --- |
| `cachedInput` | max of Σ `usage.inputTokensDetails[].cached_tokens` and the first value > 0 of `rawUsage.cache_read_input_tokens`, `rawUsage.prompt_tokens_details.cached_tokens`, `rawUsage.prompt_cache_hit_tokens` (in this order) |
| `cacheWriteInput` | the first value > 0 of `rawUsage.cache_creation_input_tokens`, `rawUsage.prompt_cache_write_tokens` (in this order; else 0) |
| `input` | max of `usage.inputTokens`, `rawUsage.prompt_tokens` and `cachedInput + cacheWriteInput` |
| `output` | max of `usage.outputTokens` and `rawUsage.completion_tokens` |
| `reasoning` | max of Σ `usage.outputTokensDetails[].reasoning_tokens` and `rawUsage.completion_tokens_details.reasoning_tokens` |

Input follows the OpenAI convention: `inputTokens` (= `prompt_tokens`) already includes cache reads and writes (the
CLI's own cost code subtracts both from it). DeepSeek's `prompt_cache_miss_tokens` are uncached input, never cache
writes, although the CLI counts them as writes. The `max` keeps a value that one of the two objects lacks (DeepSeek's
hits are often only in `rawUsage`). `totalTokens`, `requests`, `credit` and the line's top-level `message.usage`
(derived by the CLI, with DeepSeek misses as writes) are not used.

#### Models and cost

- `model` = `providerData.model` (the model the stream reported; the CLI falls back to the requested id), else
  `providerData.requestModelId`: the first of the two that is a string and not blank, trimmed; neither → null. A
  `model` that is only whitespace therefore falls back to `requestModelId`. `requestModelName` (a display name) is not
  used.
- A `custom:` or `custom-local:` prefix (a model of the user's own provider, from `models.json`; the prefix is never
  sent to the provider), matched without regard to ASCII case (`Custom:`, `CUSTOM-LOCAL:`), is removed and the
  response's `provider` is `custom` (9.2 then prices the remaining id). The rest is trimmed; when it is empty
  (`custom:`), `model` is null — `requestModelId` is not consulted — and `provider` is still `custom`.
- Built-in ids are mostly the provider's own (`glm-5.3`, `kimi-k3`, `deepseek-v4-pro`, `gpt-5.5`, `hy3`) and resolve
  with 9.2. Hosting suffixes of DeepSeek ids (`-sg`, `-volc`, `-lkeap`, `-taiji`: the same model served from another
  region or cloud) are `modifierSuffixes` of `deepseek-` in the price table and price as DeepSeek's own model, the
  way Bedrock regions do. Suffixes whose meaning is unknown (`-pay`, `-x`, `-1`, `-2`) stay unpriced.
- Routing slots — `auto`, `auto-chat`, `default`, `default-model…`, `fast-model`, `balanced-model`, `primary-model`,
  `deep-model`, and the legacy `default-1.1` / `default-1.2` (labelled Claude 3.7 / 4.0 Sonnet in the China catalog) —
  are chosen by Tencent's service; they are `unknown` entries of the price table. When the stream names the model it
  chose, `providerData.model` carries that name and the call is priced normally.
- WorkBuddy bills its users in credits (`credit-usage` lines); they are not used. Cost is API-equivalent (9).

#### Metadata

- `project` = basename of the latest `cwd` (split on `/` and `\`); `cwdPath` = that `cwd`. A chat without a chosen
  folder runs in the app's default workspace folder, whose name then shows as the project.
- `model` = the model of the latest response created or updated in this file.
- `isSubagent` = the file's path relative to the root is `<slug>/<dir>/subagents/<file>` (the folder structure only;
  a `subagents` folder above the root does not count). Parent: 6.7.
- `lastLineMs` = the latest valid timestamp of any line, including lines ignored by deduplication.

#### Deduplication and copied history

- `key` = `messageId`, in the shared dedupe set (3.4): the first file that emits a key owns it.
- A fork (`/fork`, editing and resending a message) writes a new file whose copied lines have new `id`s but keep
  `timestamp`, `providerData` (with `messageId` and usage) and `cwd`. The source file is older, is read first and owns
  every copied call; if it no longer exists, the copies count with their original timestamps. A fork's `session-meta`
  follows the copy and carries no creation time, so there is no timestamp rule as in 4.7: deduplication alone removes
  copies. Copied input lines change the fork's phase like any line until its first new line.
- A call whose usage appears twice in the same file (its last output item, then the end-of-run `model-usage` that adds
  the raw usage) merges as an update. A retried call gets a new `messageId`; usage of failed attempts is not written.
- Resuming a session appends to the same file.

#### Known approximations (to verify with real data)

- Derived from the code of WorkBuddy 5.8.3 / 5.8.4 (CLI 2.161.0, evidence in `spec/research/workbuddy.md`); no real
  session file has been read. Verify with `python3 spec/tools/reference.py --dump-today --workbuddy <data folder>`
  (or `--workbuddy-ai`).
- The start is inferred: work done between the user line and the request (memory context, other interceptors) counts as
  generation time, and a tool result picked up late moves the start a few milliseconds past the real request start.
  Calls after a pre-message compaction start at the user line written after it.
- Whether the service reports the chosen model for routing slots (otherwise those calls stay unpriced), and what the
  catalog suffixes mean.
- Whether `prompt_tokens` includes cache writes for Anthropic models behind Tencent's OpenAI-style service (`input` is at
  least `cachedInput + cacheWriteInput` either way).
- Whether a sub-agent's folder always has its main session's file name, also for nested sub-agents (otherwise they are
  still found, but show without a parent).
- A compaction call is measured only when its prompt line precedes it; after a compaction the user started by hand, the
  phase stays `generating` until the staleness rule (6.7). A turn that ends with a content-filter notice or a silent
  abort (no final message, no error notice) does the same.
- Calls without transcript lines are not counted: prompt enhancement, titles and replays run in a runtime without
  session persistence. Cloud conversations are not counted.

### 4.10 ZCode

Read the native SQLite store with short, read-only transactions using the same WAL/immutable safeguards as OpenCode.
The `session` table supplies identity, directory and parent links. Every session is one agent whose id is
`<database path>#<session id>`. `model_usage` is the sole token source; `message`, `part`, `turn_usage` and the app's
task index must not be added to its totals. A source with unsupported tables or columns is not parsed as another harness.

A terminal `model_usage` row with `completed_at` emits `key = "zcode:" + id`, `startMs = started_at`,
`endMs = completed_at`, `model = model_id`, `provider = provider_id`, `final = true`. Rows still running do not emit
responses. Input is `input_tokens` (already includes cache); cache read/write and reasoning are separate details,
not extra input/output. All numeric normalization follows 3.2. Startup includes calls that started before midnight
but completed today. The latest native user/assistant message and pending tool parts supply in-flight phase when
usage is not written until completion. Reading never creates or migrates the upstream database.

### 4.11 Kimi Code CLI

Discover only `sessions/<workDirKey>/<sessionId>/agents/<agentId>/wire.jsonl`, without following symlinks. `main`
is the main agent; other agent folders are children. Each file is its own agent. Metadata comes from native
`state.json` and `config.update.environmentDisclosure.cwd`; no credential file or conversation body is retained.
This adapter targets the TypeScript v2 wire journal; the old Python `kimi-cli` layout is not assumed compatible.

Wire records are flat objects with millisecond `time`. The adapter accepts `context.append_message`,
`agent.message.appended`, `human.agent.message.appended`, and `context.append_loop_event` / `step.end`.
Assistant usage may be on the message, its nested history entry's `meta.usage`, or the step-end event.
Identity is the provider/message id (or step uuid); `key = "kimi:" + identity`. A mirror of the same response
updates that key instead of counting another request. The shared deduper also excludes copied fork history.
`usage.record`, context token counters and parent `subagent.completed.usage` are not additional responses.

`input = inputOther + inputCacheRead + inputCacheCreation`, `cachedInput = inputCacheRead`,
`cacheWriteInput = inputCacheCreation`, `output = output`; absent details use the existing 3.2 rules.
`llm.request.time` is the pending start. Where it is absent, first-token latency plus stream duration can reconstruct
the interval; it includes time to first token. Streaming partial messages do not count as completed responses.
Turn, tool and request events drive the existing phase state machine; all speed calculations remain sections 3 and 6.

### 4.12 Grok Build

Discover only `sessions/<encoded-cwd>/<sessionId>/updates.jsonl`. Native `summary.json` supplies the working directory,
current model, session kind and parent relationship. The persisted JSONL envelope is
`{timestamp: <Unix seconds>, method, params: {sessionId, update, _meta}}`; prefer
`params._meta.agentTimestampMs` over the second-resolution envelope time.
When the native summary is not readable yet, activity can be tracked but numeric batches wait for metadata so a
late child relationship cannot cause double-counting. The live parser keeps at most 256 pending numeric batches;
the history reader can replay the full journal once metadata becomes available.

The canonical token record is the persisted `turn_completed` update's `usage`. Prefer its `modelUsage` buckets;
when there are none, use the flat usage (or its compatible `totals` object) with the current model. Do not also add
the companion cumulative `usage.json`, `signals.json`, turn-level totals or context-window counters. Buffered
`response_started` / `response_completed` messages are not assumed to be persisted.

For each model bucket:

- `key = "grok:" + prompt_id + ":" + model`, with native event id as the fallback identity.
- `input = inputTokens`, already including `cachedReadTokens` and `cacheCreationTokens`;
  `output = outputTokens` includes `reasoningTokens`. Provider remains null because the native record does not name one.
- `endMs` is the completion event time. A positive `apiDurationMs` within the existing 15-minute interval limit gives
  `startMs = endMs - apiDurationMs`; otherwise `startMs = null`, as for any other response without a valid interval.
- `requestCount` is native `modelCalls`, clamped to 1–100000. Other sources default to 1. Totals, model breakdowns,
  history, unpriced-request counts and reporting use this multiplicity; no per-call token values are fabricated.
- A later version of the same key is an upsert, including changes to its request count.

Grok folds child usage into its parent ledger. A session explicitly marked `subagent` or `subagent_fork` with a
parent id therefore contributes activity/parent links, while the parent's native batch is the single accounting
source. Ordinary forks remain independent, with copied prompt/model keys deduplicated. This avoids counting the
same child twice or retracting usage already sent by the append-only report ledger. Background usage missing from
an upstream incomplete parent batch cannot be reconstructed from that batch.

Batch normalization changes neither the existing speed formula, rolling window, interval validation, estimator,
missing-number rules nor pricing formula. The upstream file does not preserve each call's separate timeline: a
batch's API duration is represented as one interval ending at its completion. Intra-batch concurrency cannot be
reconstructed; independent sessions still add through the normal engine. API-equivalent cost is calculated by the
existing price book on the native model batch, so tier selection and rounding can differ from individual-call bills.
Evidence and executable checks: `spec/research/harnesses.md`, `spec/tools/harness_reference.py`, and
`spec/fixtures/harness-expected.json`.

### 4.13 Pi (Pi Coding Agent)

Pi (`earendil-works/pi`, MIT; npm `@earendil-works/pi-coding-agent`, before 2026 `@mariozechner/pi-coding-agent`) is
the upstream of omp and writes the same JSONL family (session version 3). Every rule of 4.7 applies — record
classification, timing model, state and phase table, assistant steps 1–5, tokens, metadata, deduplication and copied
history, known approximations — with the differences below. Evidence: `spec/research/pi.md`; fixtures:
`spec/tools/pi_fixtures.py` (`spec/fixtures/pi`).

- **Roots** (section 2): the default root `<agent dir>/sessions` holds one folder per working directory
  (`--<cwd>--`, the path without its leading separator and with `/`, `\` and `:` replaced by `-`) and in it one file
  per session, `<YYYY-MM-DDTHH-MM-SS-mmmZ>_<uuid>.jsonl`. A custom session folder (`sessionDir` in
  `<agent dir>/settings.json` when it is a non-empty string, a leading `~` meaning the home folder;
  `$PI_CODING_AGENT_SESSION_DIR`; `--session-dir`) holds the session files directly, without working-directory
  folders. The settings file is read (never written) at startup and on rediscovery; a root that does not exist is
  skipped and a path listed twice is scanned once.
- **Discovery**: regular files (never symlinks) whose name ends in `.jsonl`, at least one component below the root,
  within the depth limit of section 2; every file or folder whose name starts with `.` is skipped.
- **Sub-agents**: Pi has none of its own; packages that add them write a session's children into a folder named after
  the session file (`<stem>/<name>.jsonl` next to `<stem>.jsonl`). A file is a sub-agent when its folder's path plus
  `.jsonl` is a regular file; that file is its parent when it is tracked.
- **Records**: there is no `title` slot, no `model_usage` and no `session_exit`; tool results never end a run (no
  `yield`), and `stopDetails` is never written. Any other `type` only updates `lastLineMs`, except the side usage
  below.
- **Keys**: `"pi:" + id + ":" + stamp` (the stamp of 4.7 step 2) for assistant messages, `"pi:" + id + ":" + <entry
  timestamp ms>` for side usage, deduplicated like omp's.
- **Timing**: `endMs = startMs + durationMs` (truncated) when `startMs` is known and `message.durationMs` is a finite
  number ≥ 0 (pi-ai 0.99+), else the entry `timestamp`; there is no `completedAt`.
- **Stop reasons**: `pending` and `deferred` are treated like `error` and `aborted` (no `startMs`, phase `idle`): the
  result of a deferred request is fetched later, without its timing.
- **Tokens**: as omp, with `reasoning = usage.reasoning` and `cacheWrite1hInput = usage.cacheWrite1h` (a subset of
  `cacheWrite`, 9.3; absent → 0).
- **Side usage** (Pi's own totals add all of them): a `usage` entry (`kind` such as `cache_warm`, with its own
  `provider`, `model` and `usage`, the guard of 4.7 step 1 on those three without `api`); the `usage` of a `toolResult`
  message (a tool's own model calls; model null); the `usage` of a `compaction` or `branch_summary` entry (the summary
  call; model null). Each emits a response with `startMs = null`, `endMs = ts`, `final = true` when one of the four
  token fields is > 0, and never sets the agent's `model`.
- **Effort** (EXECUTION-MODES): see the Pi native rule there.

Approximations beyond omp's: side usage has no timing and, apart from `usage` entries, no model (it is unpriced);
`responseModel` (the concrete model the provider reports) is not used, like omp's.

### 4.14 OpenClaw

OpenClaw (`openclaw/openclaw`, MIT; npm `openclaw`) is an agent gateway for chat channels, its web app and its
desktop companions; its agent runtime grew out of Pi. From 2026.8.1 (agent database schema 8 and later, also the
extended-stable lines 2026.8.3x and 2026.9.3x) the Gateway keeps every session transcript in one SQLite database per
OpenClaw agent (a configured persona, not a sub-agent), one transcript entry per row, in the JSONL entry format of Pi
(session version 3 or 4). Evidence: `spec/research/openclaw.md`; fixtures: `spec/tools/openclaw_fixtures.py`
(`spec/fixtures/openclaw`). Out of scope: 2026.7.x and older (JSONL files under `<state>/agents/<agentId>/sessions`),
PostgreSQL agent stores, incognito threads (memory only), remote Gateways and Gateways inside WSL (the Windows Hub's
default setup).

OpenClaw counts here only the requests its own runtime sends. A turn that OpenClaw hands to another agent runtime —
Claude Code (`claude-cli`), the Codex app server, an ACP agent — is mirrored into the transcript as one message with
the whole turn's usage and no request timing, while that agent writes every request to its own log: those mirrors
count nothing here (*delegated* below). Claude Code's requests count under `claudeCode` (it shares the user's
`~/.claude`), Codex's under `codex` (its rollouts are in OpenClaw's agent-scoped Codex homes, a Codex root of
section 2, or in the user's own Codex home), and an ACP agent's under its own source when Agent Speed reads it.

#### Roots and discovery

- **State folders** (section 2): `$OPENCLAW_STATE_DIR` when it is set (a leading `~` is the home folder), and then
  nothing else. Otherwise `<base>/.openclaw` and every folder `<base>/.openclaw-<name>` whose `<name>` matches
  `^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$` and is not `default` in any letter case (named profiles; `--dev` is the profile
  `dev`), where `<base>` is `$OPENCLAW_HOME` when it is set (a leading `~` is the home folder), else the home folder.
  Profile folders are listed in ordinal order after `.openclaw`. Folders that do not exist are skipped; a path listed
  twice is read once.
- **Databases**: for every folder `<agentId>` of `<state>/agents` (not a symlink, name not starting with `.`), the
  regular file (not a symlink) `<state>/agents/<agentId>/agent/openclaw-agent.sqlite`. Nothing else matches (the
  sentinel `incognito-openclaw-agent.sqlite`, archives, `-wal`, `-shm`, `.seal`).
- **User setting**: one folder or file; it replaces every automatic root. A folder that contains
  `openclaw-agent.sqlite` is an agent folder (OpenClaw's `agents.entries.*.agentDir`) and contributes that database;
  any other folder is a state folder; a file named `openclaw-agent.sqlite` is used directly.
- **Opening**: read-only, exactly like OpenCode (4.5): `mode=ro`, busy timeout 2 s, `immutable=1` when the header says
  WAL and no `-wal` file exists, short read transactions; reading never creates a file. Only the tables
  `session_windows`, `session_nodes`, `transcript_events` and `transcript_rewrite_watermarks` are read: the same
  database holds OpenClaw's credentials (`auth_profile_*`) and memory, which are never selected. A database without
  `session_windows` or `transcript_events` (agent schema 1 of 2026.5–2026.7, which held no sessions) contributes
  nothing and is reported as unsupported (warning `unsupportedFormat`).

#### Windows and agents

- One agent per **transcript window**, a row of `session_windows` (a reset, rollover, fork or recovery starts a new
  window of the same logical session). Agent id `<database path>#<session_id>`.
- `isSubagent` = the window's `spawned_by` is a non-empty string (children of `sessions_spawn`, whose session key is
  `agent:<agentId>:subagent:<uuid>`). Parent: the window `session_nodes.current_session_id` of the session key
  `spawned_by` in the same database, when it is tracked; a parent in another agent's database is not linked.
- **Delegated window**: `acp_owned = 1`, or `agent_harness_id` (trimmed, lower-cased; the runtime of the window's
  latest run) is non-empty and not `openclaw`, `pi`, `auto` or `default`. Every other window is *embedded*. A missing
  column counts as empty or 0.
- **Rows**: the window's `transcript_events` in `seq` order. The entry is `event_json` when it is not null, else the
  zstd frame in `event_zstd` (agent schema 23+; that column is selected only when it exists), decoded with at most
  `event_utf8_bytes` (≤ 4 MiB) of output. A row that fails to decode, is not valid UTF-8 or is not a JSON object counts
  as one malformed line (section 4); an entry longer than 32 MiB is skipped like a long line. The entries are fed in
  order to the state machine below, like the lines of a Pi file.

#### Records

Every rule of 4.7 and 4.13 applies to the entries — the header, record classification, state and phase table,
assistant steps 1–5, tokens, metadata (`project` = basename of the header `cwd`; `model` = the latest response's
`message.model`), deduplication and copied history (`headerMs − 2000`) — with these differences:

- **Keys**: `"openclaw:" + id + ":" + stamp` (the stamp of 4.7 step 2).
- **Timing**: there is no duration field. `startMs = message.timestamp` (taken when OpenClaw's provider transport
  creates the response, before the request is built and sent) and `endMs` = the entry `timestamp`, the time the
  finished message was appended (a few milliseconds after the stream ended).
- **Stop reasons**: `stop`, `length`, `toolUse`, `error`, `aborted`; a missing or other value counts as `aborted`
  (4.7 step 3).
- **Tokens**: as Pi, with `reasoning = usage.reasoningTokens` (absent → 0; OpenClaw's own transports do not report
  reasoning) and `cacheWrite1hInput = usage.cacheWrite1h` (at most `cacheWrite`). A message whose
  `usage.usageReport.state` is `unavailable` (provisional stream counters, which OpenClaw's own accounting drops) emits
  no response.
- **No side usage**: only assistant messages emit responses (OpenClaw writes no `usage` entries and no usage on tool
  results, compactions or branch summaries).
- **Mirrors** (delegated turns): an assistant message is a mirror when `message.__openclaw.mirrorOrigin` is a
  non-empty string (the Codex app server; imported history), when `message.api == "cli"` and
  `message.provider == "claude-cli"`, when `message.provider` starts with `acp-`, or when its window has
  `acp_owned = 1` or the runtime (`agent_harness_id`, as above) `codex`, `claude-cli` or one starting with `acp`. A
  mirror emits no response and sets no model; it still counts for `lastLineMs` and, in an embedded window, for the
  phase.
- **Untimed responses**: an assistant message that is not a mirror, when its window is delegated or
  `message.api == "cli"` (the Gemini CLI backend, whose own logs live in a temporary folder that OpenClaw removes; the
  GitHub Copilot and Muse harnesses; other CLI backends), emits its response with `startMs = null`: the usage of a
  whole turn, written when the turn ended.
- **Phase**: an embedded window follows the 4.7 phase table. A delegated window stays `idle` (its turns run in another
  program, whose own log shows their live state when Agent Speed reads it).
- **Effort and speed**: none (EXECUTION-MODES). `thinking_level_change` entries are written when a session starts and
  when a level is selected explicitly, not for a level a single message asks for, so they are not used.
- **Work signals and the task score** (6.10, 6.9): prompts, tool calls and results as Pi.

Startup and reading (section 5): an agent database takes part in the startup order with the modification time of
the newer of `openclaw-agent.sqlite` and its `-wal`; within a database the windows are read in `created_at` order, ties
by `session_id`. Windows whose `transcript_updated_at` (else `created_at`) is within `todayStartMs − 15 min` or later
are read completely at startup; older windows start after their last row, reading only their header (the first row
whose entry has type `session`) first, as omp does for a file read from an offset (4.7). While running, change
detection watches the database and its `-wal` (file watcher plus the 1-second fallback, like 5.2). On a change, one
short transaction reads
`session_windows` (a few thousand rows at most) and `transcript_rewrite_watermarks`, then the rows with a `seq` above
the last one read of every window whose `transcript_updated_at` advanced, and all rows of a new window. A window whose
watermark `generation` changed, or whose highest `seq` dropped, was rewritten: it is reset (parser state, never the
dedupe set) and read again from its first row; the same keys merge as same-file updates (4.4). The source counts as
one "file" per database for discovery statistics.

#### Known approximations

- `endMs` is the append time: it includes OpenClaw's work between the end of the stream and the write
  (`message_end` hooks, the database transaction), normally milliseconds. `startMs` includes building the request and
  preparing credentials; retries inside a transport keep the original `timestamp`, so their back-off lowers the
  measured speed. Older OpenClaw versions behave the same (checked at 2026.8.1).
- Streams with asynchronous tool calls first write the finished part as an assistant message with empty usage
  (`toolUse`): it emits nothing and moves the phase to `tools`, and its tool results to `generating`; the final part
  carries the usage and the full interval.
- Delegated turns count under the agent that ran them only when Agent Speed reads that agent's log: ACP agents it
  does not read (GitHub Copilot CLI, Kilo Code, Qwen Code), Codex homes OpenClaw was told to put elsewhere
  (`appServer.codexHome`, a remote app server) and Claude Code on a paired remote node are not counted anywhere.
- Untimed responses are whole turns: a turn of several model calls counts as one request, without speed, and its
  price tier is chosen from the summed tokens.
- `agent_harness_id` names the runtime of a window's latest run only. A window whose runtime changed between runs
  (for example a model switch from an API model to a Codex-run one) treats all its messages by the latest runtime:
  earlier embedded requests lose their speed (or, after a switch to `codex`, `claude-cli` or ACP, their tokens).
- A CLI backend turn in an embedded window shows `generating` from its user message until the mirror arrives, while
  the CLI's own log shows the same turn; the live estimate (6.3) can count that time twice.
- Side calls (compaction summaries, session titles, memory, recaps, `/btw` side questions) are not stored as
  transcript entries and are not counted; totals follow OpenClaw's own session report (`/usage cost`), not the
  provider bill.
- Windows that OpenClaw's maintenance prunes (`session.maintenance.pruneAfter`, default 30 days) or moves to cold
  archive files are not read again; a history rebuild (section 8) cannot recover them.
- Derived from the OpenClaw source (2026.10.7, evidence in `spec/research/openclaw.md`); not yet verified against real
  databases.

### 4.15 Hermes Agent

Hermes Agent (`NousResearch/hermes-agent`, MIT; Python, the AI agent, not the JavaScript engine of the same name) runs
as a CLI and TUI, a desktop app, a messaging gateway, cron jobs and an ACP agent. It keeps no transcript with usage:
its sessions database (`state.db`) has only cumulative per-session counters, and Agent Speed never opens it, not even
read-only (a foreign reader defers Hermes's database maintenance). The source is Hermes's log, `logs/agent.log`, which
has one line per model API call of the agent loop with that call's tokens and duration, tagged with the session id;
besides the logs only one key of each profile's `config.yaml` is read (Codex runtime, below). Evidence:
`spec/research/hermes.md`; fixtures: `spec/tools/hermes_fixtures.py` (`spec/fixtures/hermes`). Out of scope: Hermes on
another computer (remote gateways, SSH backends), inside WSL, and homes inside containers that this computer does not
see as folders.

#### Roots and discovery

- **Home** (section 2): `$HERMES_HOME` (trimmed) when it is not empty and, after a leading `~` is replaced by the
  home folder, an absolute path; else macOS `~/.hermes`, Windows `%LOCALAPPDATA%\hermes`
  (`%USERPROFILE%\AppData\Local\hermes` without `LOCALAPPDATA`). Hermes points `$HERMES_HOME` at a profile when it
  runs one, so a value that is the default home or lies inside it (compared case-insensitively on Windows) means the
  default home, a value whose parent folder is named `profiles` means that parent's parent, and any other value is a
  home of its own. The data-folder suffix of desktop channel builds (`$HERMES_DATA_DIR_SUFFIX`, baked into their
  bundle) is not used.
- **Log folders**: `<home>/logs` (the default profile), then `<home>/profiles/<name>/logs` for every folder `<name>`
  of `<home>/profiles` (not a symlink) that does not start with `.`, in ordinal order. Folders that do not exist are
  skipped; a path listed twice is read once. The user setting names a Hermes home (a folder that has a `logs` folder:
  that home's log folders as above) or one log folder (any other folder), and replaces the automatic home.
- **Files**: regular files (never symlinks) directly in a log folder named exactly `agent.log` or `agent.log.<n>`
  (`<n>` one or more ASCII digits): the live log and its rotated copies. No other log is read: `errors.log` repeats
  the warnings, `gateway.log` and `gui.log` exclude the agent loop, `install.log`, lock files and compressed copies do
  not match. Outside the log folders only `config.yaml` is read (Codex runtime, below); `state.db`, `sessions/`,
  `.env` and `auth.json` are never opened.
- **Log sets**: each log folder is one *log set* with one parser state, shared by its files, in which every session
  id is one agent: agent id = the log folder's absolute path + `#` + the session id (empty for lines without one).

#### Lines

Files are UTF-8 (an invalid byte sequence reads as U+FFFD), split at `\n` with one trailing `\r` removed; the line
limit of section 4 applies. A *record line* is

```
YYYY-MM-DD HH:MM:SS,mmm LEVEL[ [<session id>]] <logger>: <message>
```

a 23-character timestamp (ASCII digits, the calendar and clock ranges of 3.1 with years 1970–9999; anything else
is not a record line), a space, a level (`DEBUG`, `INFO`, `WARNING`, `ERROR`, `CRITICAL`), an optional session tag, a
space, the logger name, `: ` and the message. Hermes writes the tag ` [<id>]` when the writing thread has a session
context. For the loggers below, the text after the level is
either ` <logger>: <message>` (no session; the id is empty) or ` [<id>] <logger>: <message>`, where `<id>` is the
non-empty text up to the first occurrence of `] <logger>: `. Every other line (other loggers and messages, the
continuation lines of multi-line messages, foreign text) is ignored and is not malformed.

Only four kinds of record line are read (in the patterns of this section `.` matches any character, line separators
such as U+2028 included):

| Kind | Logger, level | Message |
| --- | --- | --- |
| turn start | `agent.turn_context`, `INFO` | matches `^conversation turn: session=[^ ]* model=(.*?) provider=[^ ]* platform=([^ ]*) history=-?[0-9]+ msg=` |
| API call | `agent.conversation_loop`, `INFO` | starts with `API call #` (grammar below) |
| turn end | `agent.conversation_loop`, `INFO` or `WARNING` | starts with `Turn ended: ` or `Turn ended with pending tool result` |
| turn failed | `agent.conversation_loop`, `ERROR` | after an optional leading `[…]` group (the agent's log prefix, such as `[subagent-0]`) and spaces, starts with `API call failed after ` or `Non-retryable client error: ` |

The turn-start message continues with a preview of the user's message (`msg=` and a Python `repr`); only the two
groups of the pattern are read and nothing after `msg=` is ever looked at or kept. No other part of any line is kept
either: tool lines, which quote tool output, are not read (Hermes Agent has no task score, 6.9).

**Time.** The timestamp is the wall clock of the writing process (Python's `time.localtime`, milliseconds truncated),
without a zone. It is converted with the time-zone rules of this computer at the time of reading: a wall time that
occurs once is that instant; one that occurs twice (the hour repeated when clocks go back) is the earlier instant; one
that does not occur (the hour skipped when clocks go forward) is converted with the offset in force before the change,
so `02:30` in a skip from `02:00` to `03:00` is `03:30` after it. This is Python's `datetime.timestamp()` of a naive
local time with `fold = 0`. Fixtures name the zone they use (`timeZone`); implementations take the zone as a parameter
so the fixtures can be checked anywhere.

#### API-call lines

The message must match, as a whole:

```
^API call #([0-9]+): model=(.*?) provider=([^ ]*) in=(-?[0-9]+|\?) out=(-?[0-9]+|\?) total=(-?[0-9]+|\?) latency=(-?[0-9]+(?:\.[0-9]+)?)s(?: cache=(-?[0-9]+)/-?[0-9]+ \(-?[0-9]+%\))?(?: write=(-?[0-9]+))?(?: .*)?$
```

(N, model, provider, in, out, total, latency, cache read, cache write). A message that starts with `API call #` and
does not match is counted as a malformed line of the log set and otherwise ignored. Hermes appends ` id=…` and
` upstream=…` (which may contain spaces) and, for a response without usage, ` usage=unavailable`; these are not
read.

1. `key = "hermes:" + <session id> + ":" + N + ":" + <the 23-character timestamp text>` (N as written). The local
   timestamp text keeps the key independent of the time zone; the session id, N and the time together identify one
   call (N alone restarts when a gateway builds a new agent for a message). A key that was already seen, in any file,
   is a copy (a failed tool can quote `agent.log`): the line is ignored — no response, no phase change — except for
   `lastLineMs`. Repeats in the same file are ignored the same way.
2. When `in` or `out` is `?` (the provider returned no usage), or input and output below are both 0 (ACP-backed
   providers report zeros), there is no response.
3. Otherwise emit a response: `endMs` = the line's time; `startMs = endMs − d`, where `d` = latency × 1000 computed
   exactly from its decimal digits and truncated to whole milliseconds (both saturated as in 3.6), when latency does
   not start with `-`, else `startMs = null` (a wall-clock step); `final = true`; `model` = the model group (empty →
   null); `provider` = the provider group (empty or `unknown` → null); tokens:

   | Field | Value |
   | --- | --- |
   | `input` | `in` (Hermes's `prompt_tokens`: uncached input + cache read + cache write) |
   | `cachedInput` | the cache read of ` cache=<read>/<prompt> (<p>%)`, else 0 |
   | `cacheWriteInput` | ` write=<n>`, else 0 |
   | `output` | `out` |
   | `reasoning`, `cacheWrite1hInput` | 0 (not on the line) |

   Values follow 3.2 (integers exact, negatives 0, capped at 10^12). `total` and the prompt and percentage of `cache=`
   are not used.

**Timing.** Hermes takes the request start once per loop iteration, before it builds the request and before its own
retries, and computes `latency` when the response has been received and checked; the line is written a few
milliseconds later. So the interval includes request building, time to first token, streaming and Hermes's in-call
retries with their back-off, and `startMs` is exact to about ±50 ms (latency has one decimal). The valid-interval and
minimum-duration rules of 3.3 apply as usual.

#### Agents and phase

State per agent: `phase` (starts `idle`), `inFlightStartMs`, `model`, `isSubagent`, `lastLineMs`. An agent exists
from its first record line of the four kinds.

| Line | Action |
| --- | --- |
| any of the four kinds | `lastLineMs` = the latest line time of the agent |
| turn start | phase `generating`, `inFlightStartMs = ts` (while the Codex runtime switch is on: `idle`, null; below); `model` = the model group when not empty; `isSubagent` = the platform group is `subagent` |
| API call (accepted, also without usage) | `model` = its model when not empty; when phase is `generating`, `inFlightStartMs = ts` (the next request, or the turn's end, follows) |
| turn end, turn failed | phase `idle` |

- An API call outside a turn (a plugin side question or `/btw`, which runs one request without a turn) changes no
  phase. Background review and side-question turns reuse their session's id and count as its turns.
- There is no `tools` phase: Hermes logs a tool only when it ends, and concurrent tools log from threads without a
  session tag. Tool time therefore shows as `generating`.
- `project` and `cwdPath` are null (the log names no working directory). `isSubagent` comes from the turn line
  (`delegate_task` children run with platform `subagent`); `parentId` is null, since the log does not link a child to
  its parent.
- Effort and speed facts (EXECUTION-MODES): none; the log records neither.

#### Codex runtime

Hermes can hand whole turns to Codex (`codex app-server`, Codex's own agent loop, started by Hermes with Hermes's
environment, so its rollouts land in the Codex home `$CODEX_HOME`, else `~/.codex`, and count under `codex`, 4.2). Such
a turn writes its turn-start line but no API-call line and no end line, so without this rule its agent would stay
`generating`, with an in-flight estimate (6.3) overlapping the Codex agent's, until the 300-second staleness rule.
Hermes selects this runtime with the key `model.openai_runtime` of the profile's `config.yaml`: `codex_app_server`
selects it; `auto` (the default), an empty or missing value and any other value keep Hermes's own runtime (Hermes
trims and lowercases the value; the `/codex-runtime` command writes `codex_app_server` or `auto`). Each log set has a
*Codex runtime switch*, read from that one key and nothing else of the file:

- **File**: `config.yaml` in the parent folder of the log folder, when the log folder is named `logs` (compared
  case-insensitively on Windows): `<home>/config.yaml` for `<home>/logs`, `<home>/profiles/<name>/config.yaml` for that
  profile's logs (macOS `~/.hermes/config.yaml`, Windows `%LOCALAPPDATA%\hermes\config.yaml`). Every profile has its
  own file and does not inherit the default profile's. A log folder with another name (a user setting) has no file
  and its switch is off. The administrator overlay `/etc/hermes/config.yaml` (`$HERMES_MANAGED_DIR`) is not read.
- **When**: before a log set's startup backlog is read, and at every rediscovery (section 5, every 10 seconds) before
  that set's new lines are read, the file's status is checked (a symbolic link is followed, as Hermes follows it). The
  file is read only when it is a regular file whose modification time or size differs from those of the last
  successful read (startup always reads it).
  - Missing, or not a regular file: the switch is off.
  - Larger than 1 MiB (1 048 576 bytes, at the check or while reading): the switch is off.
  - Any other failure (no permission, a sharing violation, an I/O error): the switch keeps its value and the next
    check reads the file again.
  - Windows: the file is opened with read, write and delete sharing and closed right after reading, so Hermes can
    replace it (it writes `config.yaml` by atomic replacement).
- **Reading**: the bytes are decoded as UTF-8 (an invalid sequence reads as U+FFFD; one leading U+FEFF is removed, as
  Hermes reads `utf-8-sig`) and split at `\n` with one trailing `\r` removed. This is a tolerant line reader for the
  block layout Hermes writes and hand edits of it, not a YAML parser:
  1. A line that is empty or starts with `#` after its leading spaces and tabs is blank or a comment and is skipped
     everywhere.
  2. The *indentation* of every other line is its number of leading U+0020 spaces (a line that starts with a tab
     has indentation 0). A line with indentation 0 ends the current `model` block, and opens a new one when it
     matches `^(model|"model"|'model')[ \t]*:([ \t]+(#.*)?)?$` (the key alone, optionally followed by a comment).
  3. In a `model` block, its first line sets the block's indentation `n` (> 0); only its lines with indentation
     exactly `n` are keys of `model` (deeper lines are not). A key line whose text after the `n` spaces matches
     `^(openai_runtime|"openai_runtime"|'openai_runtime')[ \t]*:(|[ \t].*)$` sets the switch: on when the group after
     the colon matches
     `^[ \t]+("[ \t]*codex_app_server[ \t]*"|'[ \t]*codex_app_server[ \t]*'|codex_app_server)([ \t]+#.*)?[ \t]*$`,
     with `codex_app_server` compared ASCII case-insensitively; off for every other value (empty, `auto`, `on`,
     `${VAR}`, a block scalar, a comment).
  4. The last such key line of the file decides; with none the switch is off. No other line changes it: keys of other
     sections, the root-level `openai_runtime`, flow mappings (`model: {openai_runtime: …}`) and list items are not
     recognized.
- **Effect** (per log set, while its switch is on): a turn-start line updates `lastLineMs`, `model` and `isSubagent`
  as usual but sets phase `idle` and `inFlightStartMs = null` instead of `generating`. When a check turns the switch
  on, every agent of the log set in phase `generating` becomes `idle` with `inFlightStartMs = null`; turning it off
  changes no phase (the next turn start does). API-call, turn-end and turn-failed lines are handled as before, so a
  turn Hermes still runs itself (a fallback provider after a Codex failure, or a provider the Codex runtime does not
  apply to) counts its responses but shows no `generating` phase and no in-flight estimate. Responses, totals and daily
  history do not depend on the switch.

#### Reading and rotation

- Startup reading order and the startup rule of section 5 apply per file; all files of a log set feed its one parser
  state, so a turn that started in `agent.log.1` continues in `agent.log`.
- Rotation renames files (`agent.log.2` → `agent.log.3`, `agent.log.1` → `agent.log.2`, `agent.log` →
  `agent.log.1`, deleting the oldest) and starts a new `agent.log`. Files are therefore followed by identity (macOS:
  device and inode; Windows: volume serial number and file index): a file whose identity was being read under
  another path of the log set continues under its new path from the same offset — no reset, nothing read again — and
  lines appended to it after the rename (another process that had not reopened yet) are read normally. The new
  `agent.log` is a new identity and is read from offset 0. A file that shrinks keeps the rule of section 5 (read again
  from offset 0); the dedupe set discards the responses already counted and the replayed lines end in the same phase.
- Windows: Hermes renames the live log while other processes write to it, so a reader opens it with read, write and
  delete sharing and does not keep it open between reads.
- Settings (DESIGN 5) count today's main sessions: distinct session ids with a turn-start line today whose platform
  is not `subagent`.

#### Known approximations

- No reasoning split: `reasoning` is 0, and `output` includes reasoning only where the provider counts it there (the
  line carries Hermes's normalized output, as Hermes bills it).
- Tool runs show as `generating`, and the in-flight estimate (6.3) runs during them; the first request of a turn is in
  flight from the turn start.
- Turns that end without an end line stay `generating` until the staleness rule (300 s): preflight-compression
  timeouts, killed processes, and Codex-runtime turns that the switch does not see (below).
- The Codex runtime switch follows the file, not each turn. Hermes applies a changed value from the next session ("the
  current cached agent keeps the prior runtime"), Agent Speed at its next check for every session of the profile.
  Hermes applies the runtime only to the providers `openai` and `openai-codex` and to named custom providers that
  Codex knows; with the switch on, turns of other providers count their responses but show no `generating` phase.
  The switch does not see the `/etc/hermes` overlay, `$HERMES_IGNORE_USER_CONFIG`, a hand-written
  `model.api_mode: codex_app_server`, flow-style or `${VAR}` values, or the last good configuration Hermes keeps
  using while its file does not parse (duplicate keys, tab indentation). Codex-runtime turns count under `codex` only
  when Hermes's `CODEX_HOME` is a Codex root Agent Speed reads (section 2).
- A side question counts for the session of the thread that ran it (another session, or the empty id).
- Context compression gives a session a new id inside a turn; Hermes keeps logging the rest of the turn under the old
  id, and the next turn appears as a new agent.
- Calls Hermes does not log are not counted: auxiliary calls (titles, compression summaries, vision, web extraction,
  memory), responses refused by a content filter, and truncated or invalid responses that Hermes retries. Hermes's own
  totals miss the auxiliary calls too.
- Mixture-of-agents presets fold their advisors' tokens into the aggregator's line; the model is the preset's name and
  the provider `moa`, so these responses are unpriced (9.2).
- Rotation deletes old lines (default 5 MB × 3 backups): daily history keeps what was read before (section 8), and
  lines rotated away before the first read are lost. With `logging.level` above `INFO` nothing is logged.
- A log written in another time zone (a container with `TZ=UTC` writing into this computer's Hermes home) is
  misplaced by the difference.
- Derived from the Hermes source (main `9b904dcc22c2`, after v0.22.5; evidence in `spec/research/hermes.md`); not yet
  verified against real logs. Verification target (checked by hand, outside the app): per session, the sums of
  `input` and `output` equal `state.db`'s `sessions` counters (`input_tokens + cache_read_tokens +
  cache_write_tokens`, `output_tokens`) for sessions without Codex-runtime turns, and each response's
  `endMs − startMs` equals Hermes's own `latency`.

### 4.16 Factory Droid

Factory Droid is the coding agent of Factory AI (closed source): the CLI `droid`, interactive and `droid exec`, and the
Factory Desktop app, which runs the same agent and writes the same files. Each session has a transcript, which times
every model call but records no usage, and next to it a settings file (*sidecar*) whose cumulative counters droid
rewrites as the session runs. So the calls, their timing, model and effort come from the transcript, and their tokens
from the growth of the counters, *allocated* to the calls written since (below). Evidence: `spec/research/droid.md`
(issue #33, checked on droid 0.250.0; public recordings of droid on Linux and Windows and of Factory Desktop in
third-party test data); fixtures: `spec/tools/droid_fixtures.py` (`spec/fixtures/droid`). Out of scope: sessions of
droid before 0.123, which kept no counters (they count nothing), cloud sessions without local files, droid inside WSL.

#### Roots and discovery

- **Root** (section 2): `~/.factory/sessions`. Factory documents no environment variable for this folder.
  `FACTORY_HOME_OVERRIDE`, which third parties found in the binary and read in two different ways, is not used; a moved
  folder is chosen in the user setting.
- **User setting**: one folder. When it contains a folder named `sessions` (compared case-insensitively on Windows)
  it is the Factory folder and that `sessions` folder is the root; otherwise the folder itself is the root. It replaces
  the automatic root.
- **Sessions**: regular files (never symlinks) whose name ends in `.jsonl` and does not start with `.`, directly in the
  root (older droid versions) or in a folder directly in it (the working directory with its separators replaced by `-`,
  such as `-Users-dev-projects-forge` or `-C-Users-dev-halo`; not a symlink, name not starting with `.`); nothing
  deeper. The *session id* is the file name without `.jsonl` (droid names the file after `session_start.id`). The
  sidecar is the regular file `<session id>.settings.json` in the same folder. Nothing else is opened: not
  `<session id>.settings.json.bak` (the previous copy droid keeps), index and cache files, or anything outside the
  root — `~/.factory/settings.json` holds the user's API keys in plain text.

#### Transcript lines

Lines follow section 4 (malformed and long lines). A line's time `ts` is its `timestamp` (3.1); the phase starts
`idle`.

| Line | Action |
| --- | --- |
| first `type == "session_start"` | `project` = basename of `cwd` (split on `/` and `\`), `cwdPath` = `cwd`. `parent` and `callingSessionId` are kept when they are *session ids* (strings matching `^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$`, so no other file name can be formed from them); any other value counts as absent. `isSubagent` = `callingSessionId` is present. Later `session_start` lines are ignored. |
| `type == "message"` with a `ts`, `message.role == "user"` | `message.visibility == "user_only"` (notices and hook records, shown to the user and never sent to the model): only `lastLineMs`. Otherwise (no `visibility`: the user's text or tool results; `llm_only`: the context droid adds to each turn): `pendingStartMs = ts`, phase `generating` (`inFlightStartMs = ts`). |
| `type == "message"` with a `ts`, `message.role == "assistant"` | a call (below) |
| `type == "compaction_state"` with a `ts` (a summary of the conversation so far) | the input weight starts over here (Allocation); when the phase is `generating`: `pendingStartMs = ts` and `inFlightStartMs = ts` (the summary call ended, the next request follows) |
| `type == "agent_turn_outcome"` (written without a timestamp) | phase `idle`, `pendingStartMs = null` |
| anything else (`todo_state`, other types and roles) | only `lastLineMs` |

`lastLineMs` = the latest `ts` of any line, including assistant messages ignored by deduplication.

**Calls.** An assistant message with a `ts` whose `id` is a non-empty string is a call with the key
`"droid:" + id`, unless that key is owned by another file (3.4: forks copy their parent's messages with their ids and
timestamps) or was already seen in this file (a repeated line changes nothing). The file claims the key and the call
becomes an *open* response:

- `startMs = pendingStartMs` (may be null), `endMs = ts`, `final = true`, one request. In the recordings each
  assistant message is one model call, written when it ends, and the user line before it (a prompt, the turn's context
  or tool results) is written just before the request is sent, so the interval is the call's end-to-end time (3.3).
- `model` = `message.modelId` when it is a string that is not blank (trimmed), else the session model (Sidecar) as it is
  when the call's tokens are allocated. Custom ids (`custom:<name>-<n>`, the user's own models) stay as written; 9.2
  prices them by `<name>`.
- `provider` = `custom` when the model starts with `custom:` (ASCII case-insensitive), else null. The sidecar's
  `providerLock` names an API dialect (`anthropic`, `openai`, `generic-chat-completion-api`), not a provider, and is
  not used.
- Its two weights (Allocation) and its effort fact (EXECUTION-MODES).

Then `pendingStartMs = null`; phase `tools` when `message.content` is an array with a block whose `type` is
`tool_use`, else `idle`. The agent's `model` is the latest call's `modelId`, else (no call yet, or the latest call has
none) the session model. An open response is emitted (4.4) when tokens are allocated to it.

#### Sidecar

- **Reading**: the whole file, which must be a regular file (a symlink is not followed) of at most 1 MiB, valid UTF-8
  (one leading U+FEFF is removed) and a JSON object (3.6); otherwise it is *unreadable* and changes nothing (droid may
  be replacing it; the next read cycle tries again). A readable sidecar sets the session model, and it has *counters*
  when its `tokenUsage` is an object. On Windows it is opened with read, write and delete sharing and closed right
  after reading (droid replaces it and keeps a `.bak`).
- **Counters**: `inputTokens`, `cacheCreationTokens`, `cacheReadTokens`, `outputTokens` and `thinkingTokens` of
  `tokenUsage`, each a token value of 3.2 (missing → 0). Not used: `factoryCredits` (Factory's billing unit, not
  tokens), `inclusiveTokenUsage` and `childInclusiveTokenUsageBySessionId` (they add the session's sub-agents, which
  count in their own files), `lastCallTokenUsage`, and `assistantActiveTimeMs` (the summed wall time of whole turns,
  tool runs included — 75.4 s for a recorded 75.2 s turn — so it cannot time calls).
- **Session model**: `effectiveFactoryRouterModel.modelId` when it is a string that is not blank (the model Factory's
  router chose when `model` is `auto`), else `model` when it is a string that is not blank and not `auto` (ASCII
  case-insensitive); trimmed; else null.
- **Own counters**: a session with a `parent` (a fork, or a successor started by droid 0.226's `/compress` or by
  `/handoff`) starts from its parent's counters: a recorded `/compress` successor began at its parent's 60/37
  input/output tokens, while its `assistantActiveTimeMs` started over; forks are assumed alike. Its own counters are,
  field by field, `max(0, counter − parent counter)`. The parent's counters come from the sidecar
  `<parent>.settings.json` in the same folder, read by the rules above whether or not the parent's transcript exists:
  no such file, or a readable one without counters, gives 0. One that exists but is unreadable (droid may be replacing
  it) leaves the own counters *unknown*: the session's sidecar read changes nothing in that read cycle, exactly as when
  the session's own sidecar is unreadable, and the next read cycle tries again. The session remembers the signature of
  that parent sidecar (size, modification time and identity: macOS device and inode, Windows volume serial number and
  file index); when a later attempt finds it unreadable with the same signature, the file is not being rewritten and
  will not mend, so it gives 0 from then on (a readable parent sidecar clears the memory). Every other session's own
  counters are its counters. Then own `thinkingTokens` = `min(thinkingTokens, outputTokens)` of the own counters.
- **Tokens** of a call, from its shares of the own counters:

  | Field | Value |
  | --- | --- |
  | `input` | `inputTokens + cacheCreationTokens + cacheReadTokens` (droid's `inputTokens` excludes the cache: in the issue's sample cache reads exceed it fivefold) |
  | `cachedInput` | `cacheReadTokens` |
  | `cacheWriteInput` | `cacheCreationTokens` |
  | `output` | `outputTokens` (includes reasoning) |
  | `reasoning` | `thinkingTokens` (a breakdown of `outputTokens`, often estimated by droid) |
  | `cacheWrite1hInput` | 0 |

  So `total = input + output` counts cache reads and writes as input and reasoning inside output, like every source.

#### Allocation

Each call has two weights, taken from the bytes of the transcript:

- `wOut` = the length in bytes of its line (without the `\n`; a `\r` before it counts), at most 8 388 608: it follows
  what the call wrote (text, thinking, tool input).
- `wIn` = 65 536 + the number of bytes from the start of the latest `compaction_state` line before it (none: from the
  offset where reading started, 0 for a file read from the start) to the start of its own line, at most 8 388 608: it
  follows the context the call was sent — the conversation since the last summary, plus 64 KiB standing in for
  droid's system prompt and tool schemas, which the transcript does not hold.

*Allocating* a vector `D` of the five counters to calls `c_1 … c_n` (in file order): for each field — `inputTokens`,
`cacheCreationTokens` and `cacheReadTokens` by `wIn`, `outputTokens` and `thinkingTokens` by `wOut` — with
`W = Σ w_i`, call `i` gets `⌊D_f · w_i / W⌋`, and the units left over go one each to the calls with the largest
remainders `D_f · w_i mod W`, ties to the earlier call (largest-remainder rounding in exact integers: `D_f ≤ 10^12`
and `w_i ≤ 2^23`, so every product fits in 64 bits).

**State** per session: `H`, the own counters already accounted (a high-water mark, all 0 at first), `P`, growth not
yet allocated (0), and the open calls.

**Read cycle.** Whenever a session is read — its startup read, appended transcript lines, a changed sidecar — the parser
(1) feeds the new transcript lines; (2) reads the sidecar when it is new, its size, modification time or identity
changed, or its last read found it unreadable or the own counters unknown, and when it has counters and the own counters
are known adds `max(0, own_f − H_f)` to `P_f` and sets `H_f = max(H_f, own_f)`, for each field; (3) when `P` is not zero
and calls are open, allocates `P` to the open calls, emits them, and sets `P = 0` (no call is open any more).

- One call per rewrite: the call gets exactly its own tokens, whichever of the two files droid writes first (a rewrite
  read before its call's line waits in `P`). Calls that share a rewrite (a late read, a sidecar written once per turn)
  split it by their weights.
- Growth while no call is open (a session title, a compaction summary, a retry) waits in `P` for the next call.
- Lower counters (a restored `.bak`, a recount) add nothing: `H` keeps the highest value, so growth counts again only
  above it. A sidecar without counters (missing, unreadable, droid before 0.123) or with unknown own counters adds
  nothing. A new session is a new file with its own state.
- A transcript that shrinks or changes identity resets the session (section 5): `H`, `P` and the open calls start
  over, and its lines are read again (the same keys update their responses, 4.4).

**Startup and history.** A session read from the start (at startup within the window of section 5, when discovered while
running, and in the history analysis) feeds its whole transcript before its first sidecar read, so its own counters are
allocated over all its calls at once — the *history rule*. A session whose transcript starts at its end (older than the
startup window) has no open calls and takes `H` from its sidecar at that moment, without adding to `P` (no sidecar or no
counters: `H` stays 0): only later growth counts. When the sidecar is unreadable or its own counters are unknown at that
moment, `H` is taken the same way at the first later read cycle that reads it with known own counters (or finds no
sidecar, or one without counters). In the history analysis a session whose own counters are unknown contributes nothing
to that analysis and its result is not stored (its stored contributions and identity stay, section 8), so the next
analysis tries again; the remembered parent signature carries over to that analysis (in memory: a relaunch starts
without it). After the history rule the history analysis (8) emits what is left in `P` (own counters but no call, for
example every call failed before writing a message) as one response with the key `"droid:" + session id + ":session"`,
`startMs = null`, `endMs = lastLineMs` (nothing when `lastLineMs` is null), the session model and `P`'s tokens; the live
parser keeps such growth in `P`.

**Live and history.** Both count the same calls (keys, times, models, effort) and, once `P` is empty and the counters
never went down, the same total per session. They can differ in how a session's tokens are split between its calls:
live gives each rewrite's growth to the calls it covers, history spreads the final counters by weight. So per-model
rows (a session that switched models), price tiers (chosen by each response's input), a day's measured speed, and the
day split of a session that Agent Speed watched across midnight can differ between the dashboard and the history row of
the same day (9.6) until the next launch seeds the session by the history rule.

#### Agents and phase

- One agent per transcript; agent id = its absolute path.
- Sub-agents: droid runs Task-tool workers as sessions of their own in the same folder, with `callingSessionId` (and
  `callingToolUseId`) in their `session_start`; their usage is in their own sidecar and in the parent's
  `inclusiveTokenUsage`, which is not read. Parent: 6.7. Forks and successors (`parent`) are independent root agents
  whose inherited counters are removed above.
- The phase follows the table: a call is in flight from the user line or summary that preceded it, `tools` lasts from
  a call that asked for tools until their results are written, and `idle` follows a call without tools and a turn
  outcome. The sidecar never changes the phase.
- Effort (EXECUTION-MODES): each call's `reasoningEffort`; no speed facts.
- Work signals (6.10) and the task score (6.9) read its tool calls, which the transcript records in Anthropic's shape
  (`tool_use` blocks in assistant messages, `tool_result` blocks in the next user message); a Task worker's
  `callingToolUseId` is the parent's `Task` call.
- Settings (DESIGN 5) count today's main sessions: session files whose `lastLineMs` is today and whose session is not a
  sub-agent.

#### Reading

- Startup order and window (section 5) use the newer of the transcript's and the sidecar's modification times, as 5.4
  does for a database and its WAL.
- Change detection: the file watcher, the 1-second fallback and rediscovery also check each tracked session's sidecar;
  a sidecar change starts a read cycle. The sidecar is never tailed: each read takes the whole file.

#### Known approximations

- Per-call tokens are exact only when a sidecar rewrite covers one call; otherwise, and always in history, they are
  estimated by weights (bytes, not tokens). Session totals, request counts, call times, models and efforts are not
  affected.
- When droid rewrites the sidecar is not documented. If it does so only when a turn ends, the turn's calls get their
  tokens together at its end: their measured speeds are weighted estimates, and those that ended more than 120 s
  earlier are not reported (11.4) and raise no `responseCompleted` (6.4); in-flight estimates (6.3) cover the turn
  meanwhile.
- Usage without a call message (session titles, compaction summaries, retries, which droid counts since 0.247) is added
  to the next call; after a session's last call it counts in history only.
- Inherited counters were recorded once (a droid 0.226 `/compress` successor); that forks and `/handoff` successors
  inherit the same way is assumed. Since droid 0.196 the user stays in the original session after a fork, so a parent
  that keeps working lowers its fork's own counters by its later usage (at least to 0); a parent whose files were
  deleted is not subtracted, so its inherited part counts twice.
- `inputTokens` without cache reads is verified on recordings with the user's own OpenAI-compatible key and on the
  issue's sample, not on Factory-routed models; that `outputTokens` includes reasoning follows one reader's analysis of
  droid's provider adapters. Factory changed what the counters count (0.168: successful turns only; 0.247: every
  billed attempt); they are used as written.
- Factory credits are not used; cost is API-equivalent (9) by the model each call names.
- A fork's copies of its parent's messages are removed by deduplication only when the parent is read first (startup
  order); otherwise the fork owns them at the parent's times and the parent loses them: totals stay right, the day
  split moves.
- Derived from the issue, Factory's documentation and changelog, and public recordings and code of third-party readers
  (evidence in `spec/research/droid.md`); not yet verified against a running droid. Verification target (by hand): for
  a session watched live, the sums of its responses' fields equal its sidecar's own counters, and each response's
  `endMs − startMs` equals the gap between its assistant message and the user line before it.

### 4.17 Trae: reconciled partial request usage

Trae counts completed-turn final requests and adds earlier exact per-call usage only after reconciling the final
request. A readable Trae database always carries the partial-history warning: these optional records are not a
complete Trae billing ledger. Execution modes and per-process network attribution remain unknown.

#### Base completed-turn records

Read undeleted `chat_turn` rows whose status is exactly `completed`. Require positive valid epoch-millisecond
`context.chat_end_time`; native `created_at`/`updated_at` and whole-turn `chat_start_time` are not model endpoints.
Skip failed, canceled, running, malformed and missing-end rows. The fallback key remains
`trae:<session_id>:<turn_id>` (row id substitutes for an empty turn id), agent `<database path>#<session_id>`.
`requestCount = 1`, `startMs = null`, `endMs = chat_end_time` until timing is verified.

`input = prompt_tokens`, `output = completion_tokens`; cached input and reasoning are already included. Input/output
must be finite nonnegative integers <=10^12. Optional cache/reasoning fields are zero when absent/null, otherwise
meet the same rule. Reject cache-read plus cache-write greater than input, reasoning greater than output, and a
present `total_tokens` unequal to input+output (maximum 2*10^12). Ignore all-zero rows and `*_tokens_total` fields.
Model comes from the first nonempty `persist_user_message_context.model_info.{display_model_name,model_name,config_name}`;
project comes from `workspace_folders`. Query only safe scalars, never message bodies or credentials.

#### Earlier per-request usage and stable identity

Find the unique latest undeleted `history_v2` row whose `content_source = llm_default`, matching the turn's session
and `response_message_id`. Include newer rows without server metadata when deciding latest; they block an older
final proof. Join by `history_v2_id = server_history_info.client_history_id` with `source = llm_default`,
`history_source = server`, and `coalesce(is_deleted,0)=0`. Duplicated client/server identities are ambiguous.

The final anchor requires `extra_info.exact_usage_v1 = chat_completion` and
`exact_token_semantics_v1 = provider_raw_usage_completion_includes_reasoning`. Strict decimal-string fields
`exact_prompt_tokens_v1`, `exact_output_tokens_v1`, `exact_total_tokens_v1`, `exact_reasoning_tokens_v1` and
`exact_cache_read_input_tokens_v1` must be valid and agree exactly with the base record; require zero cache-write
input because this metadata has no cache-creation field. Require a nonempty `config_name` and positive integer
history creation times <=253402300799 seconds. A final-anchor failure leaves the original fallback intact.

Once reconciled, append other valid exact history rows for the same session/response message, excluding that final
history ID. Earlier `content_source` may be a tool name. Each client and server history ID must be unique within
that session across the queried turns. Require the same nonempty `agent_run_id` on client, server and final anchor;
client and server creation times must be within the completed turn's floor(start/1000)..ceil(end/1000), and no later
than the corresponding final history timestamp. Missing run columns or turn start disables expansion, not legacy
final usage/timing. Invalid earlier rows are skipped individually.

Earlier key = `trae:history:<session_id>:<history_v2_id>`; model = that call's `config_name`, cache-write input = 0.
Fallback `endMs = server_history_info.created_at*1000` (second-resolution completion), with unknown start.
These counters do not depend on logs: unavailable, oversized, rotated or ambiguous timing never removes exact
usage. The final request keeps its original key; late metadata only adds earlier IDs, never a second final record.
Copies, reopens and repeated polls deduplicate the same keys. History uses a parser revision to reanalyze old caches.

#### Verified per-request timing

Read app-root `logs/<launch>/Modular/ai_agent*.alaudalog` only. Supported layout: `AalG`/`Atab`, one `AALG` segment at
152, raw DEFLATE with definite CBOR maps. Bound files, expanded bytes, depth and nodes; reject symlinks/unknown
containers. Read newest logs first; skip individually oversized files, and stop at the aggregate compressed or
expanded-byte budget while retaining newer valid graphs. Merge start/end/tag records using unsigned 64-bit span IDs scoped to each log. Retain only projected
identities and timestamps. An open `do_chat` is normal; request generation must close for measured timing.

Exact history ID identifies a unique `add_origin_history` span; its nearest `call_server_generate_plan_item`
ancestor owns the request. `update_history_by_server` replays old history and is not an origin. A unique
`create_chat_turn_with_id` with exact session/turn tags must identify the same `do_chat` root. Events belong to the
nearest generation ancestor, excluding nested calls. Require:

- One POST Fetch at `/api/agent/v3/create_agent_task` or `/api/agent/v3/commit_toolcall_result`, from
  `byted_aha_ffi::aha_net::http`; one uppercase `DONE`, then `EOF`, from `ai_agent::infrastructure::aha_net::stream`.
- One `TimingCostOriginEvent` from `ai_agent::domain::plan::simple_service_v2`, boolean `is_retry=false` and exact
  model config. Nonempty traces in Fetch..EOF must match the turn trace when present; undated traces reject timing.
- Request-owned ERROR/retry/retried/reconnect events in Fetch..DONE reject timing; undated unsafe events reject too.
  Errors from subsequent tools do not invalidate an already finished model stream. Feature-name substrings such
  as `enable_retry_after_content_loop` are not retry events.
- Ordered times: generation start <= Fetch < DONE <= EOF <= generation close <= turn end; request <=15 minutes,
  EOF-DONE <=1 second. The unique origin is inside the generation interval after Fetch and within 1 second of DONE.
- Auxiliary markers may repeat. Require at least one closed `update_turn_context` within Fetch..min(close,DONE+1s)
  and at least one closed `IcubeAiAgentTaskPlanToolTokenUsage` marker within Fetch..min(close,DONE+5s). These bounds
  corroborate identity; they do not change the measured denominator.
- Reject actual overlapping Fetch..DONE intervals. An incomplete earlier request whose interval could overlap is
  ambiguous; a later Fetch after this response ended does not invalidate an earlier response. Nested wrapper span
  lifetimes are not network intervals. The final request additionally has no later request and turnEnd-DONE<=60s;
  earlier requests can be followed by long tools and more calls.

Set `startMs = Fetch`, `endMs = DONE`. This is end-to-end request throughput, including TTFT/network overhead and
excluding post-DONE tools. Header arrival (`send_streaming` close) and whole-turn duration are not substitutes.
The common 500ms minimum still applies. Missing evidence keeps the unmeasured row and all valid counters.

#### Live lifecycle and existing estimator

Lifecycle is read separately from completed usage, including when exact metadata tables are absent. Project safe
latest-turn session/turn/status/start/end/model/trace fields. Require a unique latest valid `chat_start_time` per
session. A malformed row can only be ignored if its valid `created_at` second ends before that latest start;
otherwise the session is conservatively idle. This prevents an unknown newer turn from reviving an older request.

The shipped status enum includes `in_progress/completed/failed/canceled/paused/queuing/unknown`. Only `in_progress`
with no positive end may produce activity; the other statuses are idle. Status alone is insufficient:

- Exact turn-root association and a unique fresh supported Fetch with an open request, no DONE/EOF/error and no
  unresolved concurrent request => `generating`, `inFlightStartMs = Fetch`.
- A completed unique stream in an in-progress turn => `tools`; the next Fetch starts generation again.
- A terminal/end-time update, closed `chat_turn_finish`, closed failed request, EOF without DONE, identity conflict,
  malformed endpoint, future event or >300s stale request => idle. Closed earlier failed requests do not block a
  valid later Fetch, but their Fetch <= close <= laterFetch ordering must be established.

These phase updates feed section 6's existing estimator: recent same-agent, same-model, then same-source measured
speed; no reference means no estimate. Completed requests within the latest `in_progress` turn can supply
non-accounting reference responses before the turn finishes. Each requires unique client/server history identities,
matching nonempty run ids, valid exact-usage semantics/counters, creation seconds within the current turn and
observation time, and the same strict request timing proof above (`final=false`). Keep at most the latest ten
within 30 minutes. Merge these by response identity with measured references, using the existing recent-agent /
model / source selection rules. They never enter usage, request counts, completion events or measured history;
the completed-turn reconciliation remains the sole accounting authority. Clear the transient pool when the turn
becomes terminal, ambiguous, stale or idle. Without any previous measurement, the first request remains unknown;
its verified completion can seed subsequent generation in the same turn.
`liveEstimatedShare` marks estimates separately. Fetch never fabricates
usage, request counts or measured history. File-independent staleness is reevaluated while polling.

#### Storage, refresh and checks

SQLCipher 4: authenticated 4096-byte pages, AES-256-CBC/SHA-512 HMAC and 80 reserved bytes. Bounded in-memory snapshots
apply only checksum-valid committed WAL frames, reject active rollback journals/unstable reads and never write
source or plaintext databases. Plain SQLite snapshots are accepted. Unsupported keys, schema or system SQLite
`sqlite3_deserialize` capability produce a warning. See [native research](research/trae.md) and
[third-party attribution](research/trae-third-party.txt).

Log signatures refresh live/history timing without recounting. Surviving sessions replace their history aggregate
when native rows change; deleted rows can reduce that aggregate, while wholly disappeared sessions retain stored
history under the common history policy. Projected graph caches reuse stable files and evict rotated entries.

After startup, native snapshots and log parsing run outside the live monitor's publishing loop. Only one Trae read
may be pending per source; changes during that read are coalesced and detected on the next poll. The monitor commits
the projected result and deduplication changes on its own queue, rejects results from replaced sessions, and keeps
publishing other sources and aging stale activity while Trae is being read.

Shared fixtures cover plain/encrypted/committed-WAL stores, 50 timing/accounting scenarios and 22 lifecycle scenarios.
Both platform suites test real engine estimation, no-reference behavior, transitions, late metadata/logs, copied
stores, stale state and persistence. The independent Python/OpenSSL reference checks accounting/timing and is part
of `reference.py --check-fixtures`. No test fixture contains native conversation content.

## 5. Reading files

- Startup reading order (it decides which file owns a duplicated key): files sorted by modification time ascending,
  ties broken by ordinal path comparison. Each file is read to its end before the next one starts.
- Startup: every file whose modification time is within `todayStartMs - 15 min` or later is read from offset 0.
  Older files start at their current size (nothing is read). Files larger than 256 MiB are read from
  `size - 64 MiB`, aligned to the next newline, and the snapshot carries a `partialHistory` warning.
- While running: read appended bytes. Changes are detected with the OS file watcher (FSEvents on macOS,
  `FileSystemWatcher` on Windows) plus a 1-second fallback that stats files modified in the last 2 minutes, plus a full
  rediscovery every 10 seconds. Newly discovered files are read from offset 0.
- The fallbacks must not depend on the watcher: FSEvents reports nothing for appends to a file its writer keeps open
  until the file is closed, and Codex keeps a session's rollout open for as long as the session lives, often for days.
  So the rediscovery re-checks size, modification time and identity of every tracked file whose last observed change
  is within the last 24 hours, and of the other tracked files in turns, each at least once a minute (macOS: one sixth
  of them per round, by file id; Windows re-checks every tracked file in every round). Nothing in this depends on the
  calendar day: a session that goes quiet in the evening and continues after midnight is read like any other.
- Incomplete trailing lines are buffered until their newline arrives. A file that starts being read at its end
  skips the bytes before the first newline it reads only when the byte just before its start offset is not a newline
  (so a complete line appended later is never dropped). The 256 MiB tail rule always skips to the next newline.
- If a file shrinks or its identity changes, reset its offset and its parser state (keep the global dedupe set).
  Hermes Agent logs follow a renamed file instead and share one parser state per log folder (4.15). A Factory Droid
  sidecar is read whole after the transcript's new lines; its changes never reset the session (4.16).
- Reading never blocks the UI thread. Steady-state CPU target: < 1% on an idle machine.

Responses produced while reading the startup backlog are *seeded*: they count toward totals, peaks and agent speed
history but never produce live events.

### 5.1 zstd frame streams (DeepSeek Harness)
`.zstd` files are read frame by frame: from the current offset, determine the compressed size of the next frame
(zstd `findFrameCompressedSize`); if the frame is not complete yet, stop and wait for more bytes. Decode each complete
frame (at most 64 MiB of output per frame, else the frame counts as malformed and is skipped), feed the decoded bytes
to the line reader, and advance the offset past the frame. A frame that fails to decode is skipped and counted as one
malformed line. Skippable frames are skipped. When the bytes at the offset are not a frame at all (unknown magic or
an invalid block header), count one malformed line and resynchronize at the next zstd frame or skippable-frame magic
after the offset; if none is present yet, keep the offset and retry when the file grows. Files that start at their
end (older files) start at the current size; files over 256 MiB are not seeded at all (they start at their end with the `partialHistory` warning), because a frame
stream cannot be entered in the middle.

### 5.2 SQLite sources (OpenCode)
Change detection watches `opencode.db` and `opencode.db-wal` (file watcher plus the 1 s fallback). On a change, read
new rows with `time_created > cursor` (indexed) plus the current versions of rows that are still in flight or were
updated recently (`id IN (...)`), and sessions whose `time_updated` advanced. Startup reads sessions with
`time_updated >= todayStartMs - 15 min` completely. The source counts as one "file" for discovery statistics.

### 5.3 Compressed Codex sessions (`.jsonl.zst`)
Codex compresses finished rollouts in place (`rollout-….jsonl` becomes `rollout-….jsonl.zst`, written once). Such a
file is one zstd stream (usually one frame; its decompressed size can exceed 1 GB). Read it with streaming
decompression from offset 0 with bounded buffers: any number of frames, skippable frames skipped, no per-frame output
limit, the decoded bytes fed to the line reader (section 5 line rules). A decoding error stops the file there and
counts one malformed line; what was parsed before stays. The file is never tailed: when its size, modification time
or identity changes, reset it (parser state, never the dedupe set) and read it again from the start. Startup follows
the same modification-time rule as other files; the 256 MiB tail rule does not apply. A session compressed while it
is tracked shows up as a new file whose response keys are already owned by the `.jsonl` (3.4), so nothing counts
twice. Reading or re-reading a whole compressed file (or a legacy Gemini `.json`) while running happens off the
live loop, so live updates never pause; its results are applied in one step when the read completes.

### 5.4 Antigravity conversation databases
Real users have hundreds of small conversation databases (one audit: 381); only recently changed ones are opened.

- Startup: a database's modification time is the newer of `<id>.db` and `<id>.db-wal`; with that time it takes part
  in the startup reading order of section 5 (and of section 8). Databases modified within `todayStartMs - 15 min` or
  later are read completely (seeded). Older databases are not opened; the first time one changes while running, it is
  read completely off the live loop (like a whole compressed file, 5.3) and its results are applied in one step.
- Change detection: the file watcher on every `conversations` folder, plus the 1-second fallback, which stats the
  `.db` and `.db-wal` of databases modified in the last 2 minutes, plus rediscovery every 10 seconds (new databases are
  read completely). A change of either file marks the database dirty. The language server writes the WAL continuously
  while a conversation is open, so a dirty database is read at most once every 500 ms.
- Incremental read, one short transaction: `steps` rows with `idx` above the highest `idx` read so far, plus the current
  versions of the rows last seen with an open status (`idx IN (…)`); `gen_metadata` and `executor_metadata` rows above
  their own `idx` cursors; the trajectory metadata until it is found. Then the phase (4.8) is evaluated. A new identity
  of the `.db` file resets the database (parser state, never the dedupe set) and reads it completely.
- Connections: a connection idle for 60 seconds is closed, and at most 16 stay open (the least recently changed is
  closed first). Reopening follows the open rules of 4.8. Windows closes each connection right after its read instead:
  there a file another process holds open cannot be deleted or replaced, so a kept connection would stop Antigravity
  from deleting or rewriting the conversation.

## 6. Engine

All engine functions take an explicit `nowMs` so they are deterministic and testable. Production calls them every
500 ms and after each batch of file changes.

### 6.1 Retention
Keep responses with `endMs >= nowMs - 30 min` for speed computation (the fallbacks in 6.2 look back 30 minutes).
Today totals are aggregated incrementally.

### 6.2 Agent recent speed
For agent `a`: the up to 3 most recent measured responses ending within the last 10 minutes.
`recentSpeed(a) = Σ output / Σ duration_seconds` (and `recentTotalSpeed(a)` with `input + output`).
Fallbacks for estimation when the agent has none: the same formula over the up to 10 most recent measured
responses with the same model within 30 minutes (any agent), else with the same source within 30 minutes, else none.
Only measured responses are ever used as a speed reference.

### 6.3 Live rate
Window `W = 3000 ms`, interval `[nowMs - W, nowMs]`.

- Measured: for every measured response, `output * overlap(interval, [start', end]) / (end - start')`.
- Unmeasured: for every unmeasured response, `estimatedSpeed * overlap(interval, [start', end]) / 1000`, counted as
  estimated (`estimatedSpeed` as below, for that response's agent).
- Estimated (in-flight): for every agent in phase `generating` with `inFlightStartMs != null` and
  `nowMs - lastLineMs <= 300 000`, add `estimatedSpeed * overlap(interval, [estimateStart, nowMs]) / 1000`,
  where `estimateStart = max(inFlightStartMs, latest endMs of any response of that agent)` (so a response that is
  still being written, such as a streaming Claude message, is never counted twice) and `estimatedSpeed` is the
  agent's recent speed or its fallback (6.2). No estimate if none exists.
- `live.output = (measured + estimated) / (W / 1000)`. `live.total` uses `input + output` and total speeds.
- `liveEstimatedShare = estimated / (measured + estimated)` (0 when both are 0).

A completed response replaces its own in-flight estimate automatically, because a response ending moves the agent
out of `generating` or starts a new in-flight interval at the response end.

Per-source and per-agent live rates use the same formula restricted to that source or agent. The live cost rate
(9.5) uses the same window, intervals, estimates and reference responses.

### 6.4 Events (live only, never during seeding)
- `responseCompleted` — the first emission of a key (4.4) whose `endMs >= nowMs - 120 000`, carrying source,
  project, model, output, total, duration and speed. Older responses that appear later (a whole-file source such as a
  legacy Gemini `.json`, a `.jsonl.zst` read for the first time while running, or an older Antigravity database read
  on its first change, 5.4) update totals silently.
- `newPeak` — when the measured+estimated live output exceeds the previous peak of today by at least 1 tok/s, is at
  least 20 tok/s, and no `newPeak` was emitted in the last 60 seconds. This is the all-sources event; floating orbs that
  track a subset of sources use their own `newPeak_A` (6.8).

### 6.5 History
One-second buckets for the last 300 seconds, oldest first. Bucket `k` covers `[k*1000, (k+1)*1000)`.
`measured` = tokens of measured responses distributed uniformly over their intervals (the bucket value is tok/s for
that second). `estimated` = unmeasured-response and in-flight estimates for the same buckets (up to `nowMs`). Both for
output and total.

### 6.6 Today
`todayStartMs` = local midnight, supplied by the caller. A response belongs to today when `endMs >= todayStartMs`.
- `today`: sums of `requests`, `input`, `cachedInput`, `cacheWriteInput`, `output`, `reasoning` of all responses as
  recorded (unfinalized ones included, so output is a lower bound when any exist); per source as well.
- `unfinalizedToday`: number of today's unfinalized responses; when > 0 the snapshot carries the warning
  `unfinalizedUsage(count)`.
- `averageToday = Σ output / Σ duration` over today's measured responses (null when none).
- `peakToday`: maximum over today of the 3-bucket trailing average of *measured* one-second buckets, combined with the
  maximum live value observed while running. Keep a per-second measured array for the current day **per source**
  (`measured_s[k]`; the all-sources array is their sum), allocated only for sources with data today (86 400 Float32,
  ≈ 0.34 MB each), so subsets can be evaluated (6.8).
- `todayCost`: today's cost and unpriced responses, per source as well (9.6).
- On day change everything for "today" restarts at zero.

### 6.7 Agents list

Parent links (`parentId`, an agent id or null):
- Claude: a file at `<dir>/<session>/subagents/<name>.jsonl` has the parent `<dir>/<session>.jsonl` when that file is
  a tracked agent.
- DeepSeek: the tracked dsh file whose first header `id` equals this file's `parentSession` (the earliest created
  one if several).
- Codex: from the first `session_meta`, `parentThreadId = payload.parent_thread_id ??
  payload.source.subagent.thread_spawn.parent_thread_id`. The parent is the tracked file whose first `session_meta`
  has `payload.id == parentThreadId` (the earliest created one if several). `forked_from_id` alone is not a parent.
- Gemini CLI: a file at `<chats>/<dir>/<name>` (where `<chats>` is the directory named `chats`) has as parent the
  tracked Gemini file directly inside `<chats>` whose header `sessionId`, with every character outside
  `[A-Za-z0-9_-]` replaced by `_`, equals `<dir>`; several candidates → prefer `.jsonl` over `.json`, then the
  earliest header `startTime`, then ordinal path. Nested sub-agents therefore attach to the main session; v0.33 – v0.36
  sub-agents (directly in `chats/`) have no parent.
- omp: a file `<dir>/<name>.jsonl` whose path relative to its root has three or more components has the parent
  `<dir>.jsonl` when that file is a tracked agent (a nested sub-agent attaches to the sub-agent that started it).
- Antigravity: the tracked Antigravity database whose conversation id equals this database's
  `parent_conversation_id` (compared case-insensitively); several (copies in two stores) → prefer the one in the same
  `conversations` folder, then the ordinally smallest path. Sub-agents (browser, `invoke_subagent`, image generation)
  are separate conversations; parallel conversations of the agent manager and battle-mode forks are independent agents.
- WorkBuddy (both sources): as Claude — a file at `<slug>/<session>/subagents/<name>.jsonl` has the parent
  `<slug>/<session>.jsonl` when that file is a tracked agent of the same source. Nested sub-agents are stored flat
  under the main session, so they attach to it.
- Hermes Agent: none. Its log does not link a sub-agent session (platform `subagent`, 4.15) to its parent, so
  sub-agents are roots of their own.
- Factory Droid: a session whose `session_start` has a `callingSessionId` (a Task-tool worker, 4.16) has the parent
  `<folder>/<callingSessionId>.jsonl` (its own folder) when that file is a tracked agent. `parent` (forks and
  successors) is not a parent link.
- Self references and cycles resolve to null.

List:
1. Staleness first: a phase older than 300 seconds without a new line is reported as `idle`.
2. Base set: agents whose `lastLineMs` is within the last 10 minutes, or whose phase is `generating`. An agent whose
   file no longer exists (deleted, or replaced by its `.jsonl.zst`) leaves the list as soon as that is noticed; its
   responses stay in every total.
3. Closure: add every ancestor (following `parentId`) of an agent in the set, even if it is inactive.
4. Tree: an agent's tree parent is its `parentId` when that agent is in the list; otherwise it is a root, and the
   reported `parentId` is null.
5. Order groups (roots with all their descendants) by: any member `generating` first; then the sum of the members'
   live output, descending; then the latest member activity, descending; then root id (ordinal). Inside a group, list
   depth-first with every agent immediately followed by its children; siblings ordered by `generating` first, live
   output descending, latest activity descending, id (ordinal).

UIs indent an agent by its depth in this tree and draw connectors from parent to children. To avoid jitter a UI may
keep its previous order between groups or between siblings whose live values are nearly equal, but it never splits a
group or shows a child without its parent.

### 6.8 Orb subsets

A floating orb (DESIGN 2b) tracks a source set `A`: `all` (every enabled source) or an explicit set. Everything below
is evaluated per orb on every engine tick (500 ms) from per-source values the engine already has; an orb with
`A = all` gets exactly the all-sources values. Only enabled sources count; a disabled or undetected source in `A`
contributes nothing (the orb shows it as unavailable).

- **Live value**: `live_A = Σ_{s ∈ A} live_s` (6.3 per source, `output` or `total` per the selected metric), which equals
  6.3 restricted to the responses and agents of `A` because the formula is additive. `liveEstimatedShare_A =
  Σ estimated_s / Σ (measured_s + estimated_s)` over `A` (0 when both are 0).
- **Agents**: the agents list (6.7) filtered to sources in `A`; an orb's dots are its agents in phase `generating`
  (order and cap as DESIGN 2). The hover line counts the filtered agents and `Σ_{s ∈ A} today.output_s` (6.6).
- **Events**: `responseCompleted` is delivered to every orb whose `A` contains the response's source.
- **`peakToday_A`**: the maximum over today of the 3-bucket trailing average of `Σ_{s ∈ A} measured_s[k]` (6.6),
  combined with the maximum `live_A` observed while running. It is kept in memory per orb, rebuilt from the per-source
  arrays after a relaunch (as `peakToday`) and reset at the day change. When an orb is created or its set `A` changes,
  `peakToday_A` is recomputed from the arrays (the live component restarts at that value).
- **`newPeak_A`**: the 6.4 rule applied to `live_A` against `peakToday_A` — exceeds it by at least 1 tok/s, is at least
  20 tok/s, and this orb emitted no `newPeak_A` in the last 60 seconds (live output, like 6.4, whatever the selected
  metric). After a create or a set change no `newPeak_A` is emitted for 60 seconds, so editing an orb never
  celebrates. For `A = all`, `newPeak_A` is `newPeak`.
- **Heat, tiers, titles**: computed by the UI from `live_A` with the orb's redline (DESIGN 1 and 2b), each orb with its
  own trackers.
- **Cost**: an orb showing the money unit (DESIGN 9) shows `liveCost_A = Σ_{s ∈ A, s billable} liveCost_s` and the
  known share over the same sources (9.5); its hover line uses `Σ todayCost_s` (9.6). Heat, titles and `newPeak_A`
  still use `live_A`.
- The history store (8) is unaffected: its `peak` stays the all-sources `peakToday`.
- **Boards** (DESIGN 2c) are evaluated exactly like orbs, each with its own `A`, `peakToday_A` and `newPeak_A`. A
  board's hero adds the 6.9 values of `A` (the highest task score, *Sets and groups*, and the thinking sums, *Live
  thinking*) and the cost above; its group rows use the group form of 6.9 (*Sets and groups*). Boards show no cost per
  agent or group (9.5).

### 6.9 Task score

智力 (DESIGN 10, *Intelligence readout*; the agent rows of DESIGN 4; the boards of DESIGN 2c) is the **task score**: a
number from 0 to 100 per agent that starts with every user request and climbs with evidence that the agent is solving
it — changes that pass reliable checks, failures that shrink and get fixed, actions that keep finding something new. It
answers one question: is the agent solving the problem, or burning tokens in circles? It is computed from the logs by
the fixed algorithm below (user's choice, 2026-10-28). The five weights are the user's and are not validated against
outcomes; the predictions of 6.11 are calibrated on the user's own history. `spec/tools/task_score.py` implements 6.9
and 6.11 and checks `spec/fixtures/task-score-cases.json` (regenerated with `--write` from
`spec/tools/task_score_cases.py`, which holds every expectation written by hand and refuses to write when a result
differs).

The score replaces the grade of earlier versions (model class from the price table, 6 × effort level, 20 × thinking
share), which is retired everywhere. The thinking line (*Live thinking*, below) is unchanged.

Sources: Claude Code, Claude Desktop, Codex, omp, Pi, OpenClaw and Factory Droid, which record tool calls (6.10).
Every other source
has no tasks, and its agents' value is none.

#### Tasks

A **task** is one user request to one agent (1). A sub-agent is an agent of its own, so the request its parent gave it
is its own first task. The agent's lines are read in order (4), and:

- a *prompt* starts a new task;
- a *continuation* (a prompt in the list below) joins the current task; with no current task it starts one;
- the events before the first prompt (a sub-agent whose task arrived in a line that is not a prompt, a file read from an
  offset, 5) form a task of their own, which starts at its first event.

The latest task is the agent's **current task**. It stays current after the agent goes idle, until the next prompt.

**Prompt lines**, after each source's copied-history rules (3.5, 4.7):

| Source | Prompt | Never a prompt |
| --- | --- | --- |
| Claude (both) | a `type == "user"` line classified *prompt* or *command* below; a *queued message* (below) | assistant lines, every other attachment, every other type |
| Codex | a `response_item` with `payload.type == "message"` and `payload.role == "user"` whose content (a string, or its `input_text` and `text` blocks) has a non-blank text or an `input_image` block, unless the first text, with leading whitespace removed, starts with `<` and an ASCII letter or with `# AGENTS.md instructions`; its text is the string, or the text blocks that do not start (after leading whitespace) with `<`, joined with LF | injected user-role context (`<environment_context>`, `<heartbeat>`, `<subagent_notification>`, `<turn_aborted>`, the user's reply to the agent's question `<send_user_message_question_reply>`, AGENTS.md), `developer` and `system` messages, the `event_msg` `user_message` (older versions write it beside the item; the versions on this Mac since 2026-04 write none), mailbox input (4.2), tool outputs |
| omp, Pi, OpenClaw | a `message` entry with `message.role == "user"` whose content is a non-blank string, or has a non-blank `text` block or an `image` block; its text is the string, or the `text` blocks joined with LF | `developer`, `toolResult`, `bashExecution` and the other roles; `compaction`, `branch_summary`, `custom_message` and every other entry |
| Factory Droid | a `type == "message"` line with `message.role == "user"` and no `message.visibility` (absent or null: the user's own message) whose content has no `tool_result` block and has a non-blank text or an `image` block (rule 1 below, with `image` only); its text as rule 5 | tool results (the same role, with `tool_result` blocks), the per-turn context (`visibility: "llm_only"`, id `context-<turn id>`), notices and hook records (`user_only`), assistant messages and every other type (4.16). From public recordings; no Droid session was read on this machine |

*Blank* and *trimmed* are as in 4.9. A Claude `type == "user"` line is classified by the first rule that matches:

1. **Not a prompt**: `isMeta`, `isCompactSummary` or `isVisibleInTranscriptOnly` is `true` (injected skill and hook
   text, compaction summaries); `message.content` is an array with a `tool_result` block (tool results, also with text
   beside them); the content has no non-blank text (the string content, or the `text` blocks' texts) and no `image` or
   `document` block.
2. The *lead* is the string content, else the first `text` block's text, with leading whitespace removed. **Not a
   prompt** when the lead starts with `[Request interrupted by user`, or with `<` and one of the tags
   `local-command-caveat`, `bash-input`, `bash-stdout`, `bash-stderr`, `user-memory-input`, `system-reminder`,
   `task-notification`, followed by `>`, `/`, whitespace or the end (local-command caveats, the `!` shell mode, `#`
   memory input, injected reminders, background-task notifications).
3. **Command output**: the lead starts with `<local-command-stdout` or `<local-command-stderr` (same tag rule).
4. **Command**: the lead starts with `<command-name>` or `<command-message>` (a slash command or skill the user typed).
5. Otherwise a **prompt**. Its text is the string content, or the `text` blocks whose text, with leading whitespace
   removed, does not start with `<` (IDE selections and other tag blocks attached to the prompt), joined with LF.

A message the user types while the agent works is not a `user` line: Claude Code stores it as a `type ==
"attachment"` line whose `attachment.type` is `queued_command`. It is a **queued message**, a prompt, when
`attachment.commandMode == "prompt"`, `attachment.humanTurn == true`, and neither the line's nor the attachment's
`isMeta` is `true`; its content is `attachment.prompt` (a string or blocks), read like a `user` line's content (rule 1's
test for text or an image or document, rule 5's text), so it can be a continuation. Its time is
`attachment.timestamp` (when the user sent it) when that is a timestamp (3.1), else the line's own. Every other
`queued_command` is not a prompt: background-task notices (`commandMode == "task-notification"`) and the hand-back
messages of sub-agents (`isMeta`, text starting `<agent-message`). Checked against real Claude Code logs, counts and
field names only (2026-10-28).

**Withdrawal** (Claude, omp, Pi, OpenClaw, Factory Droid). A prompt or command takes effect as soon as its line is
read, so the new
task shows at once. It is *withdrawn* — as if the line were not there, and the previous task stays current — when:

- the first assistant line (Claude) or assistant `message` entry (omp, Pi, OpenClaw, Factory Droid) after it is ignored
  because its key was first seen in another file (4.1 step 2, 4.7 step 2, 4.16 *Calls*): the prompt was copied with
  its conversation (a forked Claude or Droid session, omp's byte copies of sub-agent transcripts);
- for a command only: a command output line comes before any assistant line that is not ignored. The command never
  asked the model anything (`/model`, `/cost`, `/compact`, …).

Only the first assistant line after the prompt decides; lines skipped as `<synthetic>` (4.1 step 1) do not count. A
command is never a continuation. Codex needs neither rule: 3.5 removes its copies, and its commands are not user
messages. Cases: `promptLines` (one line each) and `segmentation` (withdrawal over classified lines).

**Continuation.** The prompt's text is normalised: Unicode NFKC; trimmed; trailing characters among `.` `!` `~` `,` `。`
`、` and whitespace removed until none is left; ASCII `A`–`Z` lower-cased; every run of whitespace replaced by one
space. It is a continuation when the result is one of:

- `continue`, `continue please`, `please continue`, `go on`, `keep going`, `proceed`, `please proceed`, `carry on`,
  `go ahead`, `resume`, `ok`, `okay`, `ok continue`, `ok go ahead`, `yes`, `y`, `yep`, `yes please`, `sure`, `do it`,
  `lgtm`;
- `繼續`, `繼續吧`, `請繼續`, `繼續做`, `接著做`, `好`, `好的`, `好啊`, `可以`, `嗯`, `是`, `對`, `行`, `沒問題`;
- `继续`, `继续吧`, `请继续`, `继续做`, `接着做`, `对`, `没问题`;
- `続けて`, `続けてください`, `続行`, `進めて`, `進めてください`, `はい`, `お願いします`, `うん`, `いいよ`, `どうぞ`.

So `Continue.`, `ＯＫ!`, `繼續。` and `go ahead…` continue (NFKC turns `…` into `...`), while `continue?`, `next` and
`continue with the tests` start a new task. The plan says "case-folded": only ASCII letters are folded, because every
entry is ASCII or CJK (after NFKC, which also maps full-width letters to ASCII), so full Unicode case folding could
change no result and would differ between platforms. The text is compared in memory and never kept. Cases:
`continuations`.

A message the user sends while the agent works (steering: a Claude queued message, a Codex user message item) starts
a new task unless it is in the list. A correction typed mid-task therefore splits the task: an approximation. The Codex
rule was first written from format knowledge (the `event_msg` `user_message`) and changed after the end-to-end check
on real logs (2026-10-28): no rollout on this Mac since 2026-04 has that event, so every Codex file read as one task;
the item rule was checked against the same logs by counts and leading markers only.

#### Event stream

The model's input per agent is a stream of prompts and *actions* (6.10) in file order, a message's tool calls in
content order. It is the contract with the parsers: `spec/TASK-PARSERS.md` defines the functions named below, whose
outputs arrive here already parsed; their inputs are never kept.

```
{t, type: "prompt", continuation}
{t, type: "action", kind, tool, digest, command?, outcome?, failures?, hunks?}
```

| Field | Value |
| --- | --- |
| `t` | the time of the line that holds the prompt or the call (Unix ms); a queued Claude message's own time (*Tasks*) |
| `continuation` | *Continuation*, above |
| `kind` | `action_kind(source, tool, input, cwd)`: `read`, `search`, `edit`, `command`, `delegate`, `web` or `other` |
| `tool`, `digest` | as 6.10 |
| `command` | `kind == "command"` with command text (TASK-PARSERS 3.1) only: the fields `class` (`test`, `build`, `check` or `other`), `suite`, `targeted` and `key` of `command_info(text, cwd)` |
| `outcome` | once the result is read: `outcome(source, result, t, tool, input, cwd)` = `{exitCode, failed, t}` (`failed` true, false or null; `t` the time of the result line) |
| `failures` | with the outcome of an action that has `command`: `failures(info, result_text(source, result), cwd, outcome.failed)` = `{fingerprints, failedCount, passedCount, framework}` |
| `hunks` | `kind == "edit"` only: `edit_hunks(source, tool, input, cwd)` = `[{file, old, new}]`, 16-hex digests; `old` is null for a whole-file write, whose `new` is the digest of the whole content |

`cwd` is the agent's working directory when the call's line was read (*Composing the stream*, below): every function of
one action, also those applied when its result arrives, receives that same value.

- Results match their call by id (*Composing the stream*); a result for an unknown call is ignored.
- Seeded lines (5) add events, unlike the signals' action list (6.10), so the current task is rebuilt from the file
  after a relaunch. Copied lines (3.5, 4.7) and assistant lines ignored as duplicates (4.1 step 2, 4.7 step 2) add
  none.
- An evaluation at `nowMs` (the fixture queries) uses the events with `t ≤ nowMs`, and treats an outcome (with its
  failures) whose `t` is after `nowMs` as not read yet. The live engine evaluates everything it has read; it may keep
  incremental state for each agent's current task instead of evaluating the task again, as long as every value equals
  these definitions.

#### Composing the stream

The *composer* turns one agent's lines into the event stream. It reads every line right after the source's parser (4)
has read it and takes the parser's decisions as they are: a line the parser skips as copied history (3.5, 4.7) or for
want of a usable time adds nothing, and an assistant line or entry the parser ignores because its key was first seen in
another file (4.1 step 2, 4.7 step 2) is a **duplicate**. `spec/tools/task_events.py` implements it;
`spec/fixtures/task-e2e-expected.json` checks it end to end (below).

| Source | Line time `t` | `cwd` | Prompts | Calls | Results |
| --- | --- | --- | --- | --- | --- |
| Claude (both) | the line's `timestamp` (3.1); a line without one adds nothing | a non-blank string `cwd` of the line, taken before the line's own calls; the latest wins | `user` lines classified *prompt*, *command* or *command output*, and queued messages (*Tasks*) | blocks `tool_use` (`id`, `name`, `input`) of an `assistant` line that is not `<synthetic>` (4.1 step 1) and not a duplicate | blocks `tool_result` of a `user` line, by `tool_use_id`; the result is the block |
| Codex | the envelope's `timestamp`; lines that 3.5 removes add nothing | the first `session_meta`'s `payload.cwd`, then every `turn_context`'s `payload.cwd` (non-blank strings) | `response_item` user messages (*Tasks*) | `response_item` / `function_call` (`call_id`, `name`, `arguments`) and `custom_tool_call` (`call_id`, `name`, `input`); a code-mode script gives its tool calls instead (below) | `response_item` / `function_call_output` and `custom_tool_call_output`, by `call_id`; the result is the payload |
| omp, Pi, OpenClaw | the entry's `timestamp`; entries that 4.7 ignores (before the header, without a string `id` or a time, copied history) add nothing | the header's `cwd` (a non-blank string) | `message` entries of role `user` (*Tasks*) | blocks `toolCall` (`id`, `name`, `arguments`) of an assistant `message` that passes 4.7 step 1, is not a duplicate and, for OpenClaw, is not a mirror (4.14) | `message` entries of role `toolResult`, by `toolCallId`; the result is the message |
| Factory Droid | the line's `timestamp`; a line without one (`agent_turn_outcome`) adds nothing | the first `session_start`'s `cwd` (a non-blank string) | user `message` lines (*Tasks*) | blocks `tool_use` (`id`, `name`, `input`) of an assistant `message` line whose `id` is a non-empty string and whose key `droid:<id>` is not a duplicate (4.16 *Calls*) | blocks `tool_result` of a user `message` line, by `tool_use_id`; the result is the block |

*Non-blank* is as in 4.9; the `cwd` is passed to the parsers as stored (TASK-PARSERS normalises it).

**Prompts.** Prompt, command and command-output lines and the assistant lines with their duplicate flag go through the
withdrawal of *Tasks* in line order: a prompt or command adds its prompt event at once and is removed again when the
first assistant line after it is a duplicate, or, for a command, when a command output comes first. The assistant lines
that decide are, for Claude, every `assistant` line with a time except `<synthetic>` ones, for Factory Droid every
assistant `message` line with a time and a non-empty string `id`, and for omp, Pi and OpenClaw
every assistant `message` that passes 4.7 step 1 (an OpenClaw mirror decides like any other). Codex has no withdrawal.

**Calls.** A call needs a string id and a string tool name; an id the agent's file already had is a repeat and adds
nothing (a Claude message's lines can repeat its blocks). The action takes the call line's `t` and `cwd`, and:

- `kind` and `digest` (6.10: an object or array input hashes as the JSON text exactly as the line stores it, a string
  input as its text, an absent or null input as empty text);
- `command` when the kind is `command` and the tool has command text (TASK-PARSERS 3.1): `command_info(text, cwd)`'s
  four fields; its `masked` and `background` stay in memory for the outcome. A shell command that only reads or searches
  has kind `read` or `search` and no `command`; a shell call without readable text has kind `command` and no `command`;
- `hunks` when the kind is `edit`.

The call's input is held in memory until its result arrives, then released.

**Codex code mode** (TASK-PARSERS 4.4). Recent Codex versions run most tools from a JavaScript cell: a
`custom_tool_call` named `exec` whose script calls `tools.exec_command(…)`, `tools.write_stdin(…)`,
`tools.apply_patch(…)` and so on (on this Mac, 84 760 such calls since 2026-07, against a few thousand direct ones).
When `code_script(source, tool, input)` gives calls, the script adds no action of its own: each of its calls becomes an
action in source order, with the script line's `t` and `cwd`, its own tool name and input, and the digest of its source
text (in place of the 6.10 digest of the whole script). When the script's result arrives, the printing statements of
a plain script take the items of `code_items(source, result)` in order — each item read as its call's result above —
provided the counts match; every other call of the script gets no result. The 6.10 work
signals keep one action per script (they never look inside the input).

**Results.** A result belongs to the call with its id anywhere in the agent's stream (a result read after a new prompt
still completes the earlier task's action). Only the first result of a call counts; later ones, and results whose id
names no call, are ignored. The action's `outcome` is `outcome(source, result, t, tool, input, cwd)` with the result
line's `t` and the call's tool, input and `cwd`. An action with `command` also gets
`failures(info, result_text(source, result), cwd, outcome.failed)`, except while the result leaves a session running.

**Sessions** (TASK-PARSERS 4.3). Codex's unified exec returns after about 10 seconds while a slow command keeps running,
and the command's outcome arrives in the results of later `write_stdin` calls; a Claude background `Bash` ends with a
task notice (`queued_command` in `task-notification` mode) that states its exit code, or is read with `BashOutput` /
`TaskOutput` in older versions. Without stitching them back, a slow test suite would never count as a run, and Codex
would score below Claude Code for the same work. The composer keeps, per agent, a map from session key to the action
whose result left that session running:

1. When a call that is not itself a continuation gets a result with `running_session(source, result) = K`, its action
   keeps that result's outcome (state running: `exitCode` and `failed` null) and no `failures` yet, and K maps to the
   action, its command's `masked` flag and its output so far (the result's `result_text`). A later such result for the
   same K replaces the entry.
2. A call with `continuation(source, tool, input) = K` is an action of its own (kind `other`, its own outcome). When
   its result arrives and K is mapped, the result's `result_text` is appended to K's output (Codex parts concatenated
   as they are, being slices of one stream; Claude parts joined with LF).
   When that result's own outcome has a non-null `exitCode` and a non-null `failed` (finished with an exit code), the
   mapped action's outcome becomes `{exitCode, failed, t}` of it — with `failed` null when the mapped command is
   `masked`, since the exit code is then the pipeline's — its `failures` (when it has `command`) are computed over the
   whole output, and K leaves the map.
3. A Claude line for which `notification(source, line)` gives `{session: K, exitCode: N}`, when K is mapped, ends it
   the same way with the outcome `{exitCode: N, failed: N ≠ 0, t}` (`failed` null when `masked`), `t` being
   `attachment.timestamp` when it is a timestamp (3.1), else the line's; the notice carries no output, so the failures
   are computed over the output held so far.
4. A session that never reports an exit code leaves the action without a result (not a run, *Runs*). Its output is
   held in memory only, until then.

The stitched outcome keeps the action where the call was: a test started before an edit verifies the code before it,
whenever it finishes. An evaluation at `nowMs` sees it from the continuation result's `t` on.

**Reading from an offset, copies and sub-agents.**

- Seeded lines are composed like live ones (6.4's `responseCompleted` and `newPeak` are a different thing and never
  fire while seeding). A file read from an offset (5) composes from that offset, after the header that omp and Pi read
  first (its `cwd` counts); its events before the first prompt form a task of their own (*Tasks*).
- Copied history adds nothing: Codex lines that 3.5 removes, and omp, Pi and OpenClaw entries before `headerMs − 2000`.
  Claude and Factory Droid copies (forks, resumes) are found by deduplication: their assistant lines are duplicates (no
  actions, and the prompt before them is withdrawn), and their tool results match no call. omp's byte copies of sub-agent transcripts are
  duplicates the same way.
- Every agent has its own stream: a Claude `subagents/*.jsonl` file, a Codex sub-agent rollout, an omp or Pi nested
  file, an OpenClaw spawned window, a Factory Droid Task worker (its own session file, with `callingSessionId`; its
  `callingToolUseId` names the parent's `Task` call, which is the parent's `delegate` action). The request its parent gave it is its first prompt (a Claude `user` line, a Codex
  `user_message`, a user message of the Pi family); a request that arrives otherwise (a Codex mailbox message, 4.2)
  leaves its first events to a task before the first prompt.
- An OpenClaw delegated window's mirrors add no actions (the runtime that ran the turn records the tools in its own
  log, scored there when Agent Speed reads it); its prompts still start tasks, so its value is none.

**Model and effort.** After every line with a time that adds or could add something (above), the composer notes
`(t, model, effort)`: the agent's model (4) and the mode of its current effort attribution (EXECUTION-MODES), null when
unknown. 6.11 *Task records* takes the last note whose `t` is at most the task's `endMs`, in line order. A note equal
to the previous kept one, with a `t` not earlier than its, may be dropped: the choice stays the same.

**Privacy.** Held in memory only: a call's input until its result, a running session's output until its exit code, the
`cwd`. Prompt texts are compared with the continuation list and dropped. The stream keeps what the table of *Event
stream* lists.

**End to end.** `spec/fixtures/tasks/` holds hand-written sessions of every scored source (`spec/tools/task_e2e_fixtures.py`
writes them for `generate_fixtures.py`, with expectations written by hand) and `spec/fixtures/task-e2e-expected.json`
the expected result of reading them in its `order` with one dedupe set (3.4): per agent its event stream, its value
at `nowMs` (form, score, state, dimensions, facts, predictions with the source's coefficients) and its records (6.11,
`analysisMs`), then the sets and board groups (*Values*), the calibration and the comparison. `reference.py
--check-fixtures` checks it and `reference.py --write-task-fixture` regenerates it after checking the hand
expectations. Ports read the files with their own parsers and composer and must match every value (numbers within
10⁻⁹, calibration within 10⁻⁶).

**Dumps** (parity on real logs). `reference.py --dump-today` ends with one `task` row per seeded agent of a scored
source — `agentId`, `source`, `phase` (after staleness), `lastLineMs`, `parentId`, `taskStartMs`, `form`, `score`,
`shown`, `state`, the dimensions and facts of `task_result`, `predictions` (the prior coefficients: the dump analyses no
history) and the model flags — and one `taskSets` row (the agents list, the all-sources set, one set per scored source
with listed agents, the board groups), evaluated at `--now MS` or the capture time (`summary.nowMs`).
`reference.py --dump-tasks` prints a `record` row per task record of every log read whole (Codex archives included),
a `calibration` row per source and a `comparison` row (`--first` / `--last`, default every day; `--source`; `--utc`).
`spec/tools/compare_dumps.py` compares these rows whenever both dumps have them, skipping agents still being written.

#### Runs, checks and edits

**Weights.** `w = 0` for `other` (todo lists, plans, questions to the user: neither evidence nor waste), `w = 2` for a
command of class `test`, `build` or `check`, `w = 1` for every other action. *Counted* actions have `w > 0`; `n` is
their number.

**Runs.** A command of class `test`, `build` or `check` is a *run* once its result is known:

- **fail** when `outcome.failed` is true or `failures.failedCount > 0` (an exit code 0 with reported failures, such as
  `cargo test | tail`, still fails);
- **pass** otherwise, when `outcome.failed` is false, or when `failedCount == 0` and `passedCount` is not 0 (a
  summary that reports no failure: tests that passed, or a checker or build, which counts no passes — unless it says
  that nothing ran);
- otherwise (no outcome yet, or `failed` null — backgrounded, interrupted or still running, or masked by a pipe or a
  list without a status the output states, TASK-PARSERS 3.10 — without counts) it is not a run, only a counted
  action. A masked command whose output prints its own exit code (`; echo $?` after a command that owns its status,
  `${PIPESTATUS[0]}`, `&& echo ok || echo fail`, `|| echo "failed $?"`) gets that status, and a build or check piped
  into a grep for error lines that printed nothing passes (user's decision, 2026-10-28).

The error count of a run is `F = 0` for a pass and `max(1, |fingerprints|, failedCount)` for a fail (a null count is 0).

**Checks.** A run belongs to the check `suite:<suite>` when it is not targeted and its `suite` is known, else to
`key:<key>`. So full runs of one runner share a check whatever their flags, and every targeted command is a check of its
own: a narrowed run never makes the full suite look converged. The *sequence* of a check is its runs in order; a targeted
check whose suite is known also gets every **passing** non-targeted run of that suite after its own first run (a full
pass proves the targeted part too; a failing full run says nothing about it).

**Episodes.** Per check, over its sequence: a fail whose predecessor is a pass, or that has none, opens an episode; the
fails after it join; the next pass closes it (*recovered*). Its *attempts* are its number of fails.

**Effective edits.** An `edit` action is *effective* when its outcome is not `failed: true` (an unread outcome counts as
not failed) and it changes something. The task keeps, per file digest, the contents of its whole-file writes, the file's
latest known content, and two sets of hunk texts, *current* and *superseded*. The hunks are applied in order:

- whole-file (`old` null): no change when `new` equals the latest known content. Otherwise it changes the file; it
  *oscillates* when `new` is one of the file's earlier written contents; then `new` is added to the written contents,
  becomes the latest known content, and both hunk sets are cleared (a rewrite replaces every region);
- a hunk: no change when `old == new`. Otherwise it changes the file; it *oscillates* when `old` is a current text and
  `new` a superseded one (an exact revert, or `A → B → C → A`); then `old` moves to the superseded texts, `new` becomes
  current, and the latest known content becomes unknown.

An edit is effective when one of its hunks changed something, and *oscillating* when it is effective and one of its
hunks oscillated. An edit without hunks (an input the parser could not split) is effective and does not oscillate.
Edits made by shell commands other than `apply_patch` (`sed -i`, formatters, `git checkout`) are not edits: a known
approximation.

**Blocks.** Effective edits split a task's runs into blocks: block `m` holds the runs after the `m`-th effective edit and
before the next one (block 0 the runs before the first). The **current block** is the runs after the last effective
edit (every run when there is none). A block *fails* when some check with a run in the block has a fail as the last of
its sequence's elements that lie in the block.

#### States

| State | When |
| --- | --- |
| `none` | no effective edit and no run |
| `unverified` | effective edits, no run |
| `passing` | the current block is not empty and does not fail |
| `failing` | the current block fails; or it is empty and the latest non-empty block fails (failed, then edited: the fix is not proven yet) |
| `pending` | the current block is empty and the latest non-empty block does not fail (passed, then edited: the credit waits for a re-run) |

The plan names the first, second and last states only by example; *failed, then edited* stays `failing` because there
is no earlier pass whose credit could be pending.

#### Dimensions

| Dimension | Max | Value |
| --- | ---: | --- |
| V 驗證成功 verification success | 40 | `passing` with a passing test run in the current block: 40; `passing` with build and check runs only: 20; any other state: 0 |
| C 錯誤收斂 error convergence | 25 | below |
| R 錯誤恢復 error recovery | 15 | below |
| E 行為效率 behaviour efficiency | 10 | below |
| K 驗證完整性 verification completeness | 10 | below |

**C.** No run: 0. Otherwise, for every check `j` whose peak `F_peak = max F` over its sequence is above 0:

`c_j = max(0, 1 − (F_last + N_new) / F_peak)`

where `F_last` is the `F` of the sequence's last element and `N_new` (0 when that element passes) is the number of its
fingerprints found in no element of the sequence up to and including the first one with `F = F_peak`.
`C = 25 × Σ F_peak·c_j / Σ F_peak`; `C = 25` when runs exist and no check ever failed. Checks weigh by their peak, so a
check that went from 40 failures to 2 counts more than a one-off flaky failure. `N_new` is the regression penalty of the
plan: a fingerprint that appears after the peak (fix three, break one new) counts twice, once in `F_last` and once as
new.

**R.** With episodes: `R = 15 × mean q` over every episode of every check, with `q = 2 / (attempts + 1)` for a recovered
episode (1, 2/3, 1/2, 2/5, … for 1, 2, 3, 4 attempts) and `q = 0` for an open one. Without episodes: 15 when some run
passed, else 0.

**E.** From four parts over the task's counted actions:

- *New evidence* `ΔI` of each counted action, in order:
  - an edit: 1 when effective and not oscillating, else 0;
  - a run: 1 when no earlier run of the task has its `key`, or an effective edit came after the latest earlier run with
    that key, or its *signature* (result, sorted fingerprints, `failedCount`) differs from that run's; else 0 (the same
    check again on unchanged code);
  - any other counted action, a test, build or check command without a result included: 1 when no earlier such action
    has its `(tool, digest)`, or an effective edit came after the latest one; else 0 (re-reading after an edit is new).
- *Information gain* `IG = Σ w·ΔI / Σ w` (0 when `n = 0`).
- *Loop share* `L` = marked actions ÷ `n`: wherever a block of 1, 2 or 3 consecutive `(tool, digest)` pairs repeats at
  least 3 times back to back (6.10's *Looping* and *Repeating*, at every position, with or without a failure), the
  repeats after the block's first occurrence are marked.
- *Oscillation share* `O` = oscillating effective edits ÷ effective edits (0 without any).

`E = 10 × min(1, n / 8) × IG × (1 − L) × (1 − O)`: it ramps in over the first 8 counted actions, so it climbs from 0.
The plan divides by the number of tool calls; dividing by `Σ w` instead keeps `other` tools out of both sides, so a todo
list neither adds evidence nor dilutes it, and runs weigh double because they are the strongest evidence and the
costliest repeats. Loops are found among counted actions only, so a todo update between identical runs does not hide the
loop.

**K.** Over the current block: +4 when it has a build or check run, or a passing test run; +4 when it has a test run;
+2 when it has a passing non-targeted test run with no failing test run after it. Two deviations from the plan, argued:
a passing test run also earns the build credit, because runners such as `cargo test`, `swift test`, `go test` and
`dotnet test` build first and an interpreted suite has no build step; and the +2 goes to "the full suite passed last"
rather than only "after a targeted fix", so a task that ran the full suite from the start is not capped at 98.

Cases: `tasks` (one per dimension and edge case: none, unverified, ideal, convergence and recovery, pending, regression
and its penalty, loops, oscillation, whole-file writes, failed edits, build-only, pending after a build-only pass, a
failure without counts, verification without edits, targeted then full, continuation, a check beside a failing test,
failed then edited, unknown outcomes, piped failures, stall, `other` tools).

#### Score

`score = V + C + R + E + K`, from 0 to 100; **none** when the state is `none`. It is shown rounded half away from
zero, an integer of at most three digits; fixtures compare unrounded values within 10⁻⁹.

It changes only when an event is read, never with time alone. It rises with progress and drops only when:

1. a run fails: a check that passed fails again, a check fails on its first run, or a failing check's errors grow
   (V, C and R fall, and K loses what the failing run no longer earns) — the work turned out not to be verified;
2. an effective edit follows a run: V and K start over until a check runs again (`pending`, or `failing` after a failed
   run);
3. behaviour gets worse: the share of actions that repeat without new evidence, loop or undo earlier edits grows (E, at
   most 10 points). The plan's "drops only when" lists the first two; the third follows from its own definition of E
   ("loops, repeats and edit oscillation lower it") and is bounded by E's 10 points.

A task that never runs a check scores at most 10 (E alone), within the plan's "roughly ≤ 25". A new prompt starts a new
task, so the agent's value starts over; the finished task keeps its final score (6.11 records it).

#### Task facts

Besides its state, score and dimensions, a task gives the score card (DESIGN 12) these facts, from the same evaluation:

| Fact | Value |
| --- | --- |
| `vHeld` | `pending` only: the V of the latest non-empty block (40 when it has a passing test run, else 20) — the credit a passing re-run restores; null in every other state |
| `openFailure` | `failing` only: the *open failure*, the newest failing run that is still the last element of its check's sequence (in state `failing` there always is one): its `suite` (null when unknown), `class` and `key`; `errors`, its `F`; `peak`, its check's `F_peak`; `counted`, true when `F` comes from fingerprints or a `failedCount` above 0 rather than the minimum of 1; `edited`, true when an effective edit followed it. Null in every other state |
| `lastPass` | `passing` and `pending` only: the last run of the current block (`passing`) or of the latest non-empty block (`pending`) — it passes, since that block does not fail: its `suite`, `class` and `key`; `buildOnly`, true when that block has no passing test run (V, or `vHeld`, is 20). Null in every other state |
| `oscillations` | the task's oscillating effective edits (the numerator of `O`; also a record count, 6.11) |

`IG`, `L` and `O` (*Dimensions*) and the counts of 6.11 *Task records* are facts too. A `suite` names a check for
display; a `key` is a digest and only tells checks apart. Cases: the `tasks` queries carry every fact.

#### Values

**Agent.** Evaluated on every engine tick (500 ms) with the agents list (6.7) and that tick's phases (after the
staleness rule). Exactly one form:

- **live**: the agent is not `idle` and its current task has a score: that score;
- **resting**: the agent is `idle` and its current task has a score: that score, the task's final one until a
  continuation reopens it (shown pale, DESIGN);
- **none**: no current task, or one without a score (a task that has only read so far shows none until it edits or
  runs a check).

**Sets and groups: the highest score** (user's decision, 2026-10-28: "every agent session has its own 智力; pick the
highest one and show it"). For a set of agents — all enabled sources (menu bar or taskbar block), an orb's or a board's
set `A` (the agents list filtered by 6.8: the orb readout, the board hero), or a board group (a root and its descendants
in the board's agents list, DESIGN 2c):

- **live** when some agent of the set is live: the highest live score;
- else **resting** when some agent is resting: the highest resting score (shown pale);
- else **none**.

Tokens do not weigh: an agent that writes far more but scores lower does not pull the value down. The value names the
agent that supplied it (for tooltips): among the agents with that exact score in that form, the one with the latest
`lastLineMs`, then the smallest id (ordinal). A sub row shows its sub-agent's own value. Values never drive heat, tiers,
titles or peaks; live reporting sends them only through its `taskScore` extension (11.10). Cases: `values`.

#### Privacy

User-approved (2026-10-28). The parsers of TASK-PARSERS read a tool call's input (command text, edit input) and its
result's output in memory, while the line is parsed; the engine keeps, for each agent's current task only, what the
event stream lists: classes, flags, suite names (a fixed vocabulary), 16-hex digests, counts, exit codes and times. Prompt texts are compared with the
continuation list in memory. No text, path, command line, output, fingerprint source text or prompt is ever stored,
displayed, logged or reported; the history store keeps only the records of 6.11. Live reporting sends numbers only, and
only through its `taskScore` extension with the `models` section (11.10, user's decision 2026-10-29): the highest
score and its form, the thinking speed, and per model and source the counts of tasks that completed while reporting was
on — never a digest, suite name, key, time or record of a task. Every other source keeps its own exclusions (4.9
WorkBuddy, 4.15 Hermes Agent, …): sources without a score are read exactly as before.

#### Live thinking

The `思考` line next to 智力 (DESIGN 10) is unchanged by the task score: the reasoning tokens generating right now.
`spec/tools/live_thinking.py` implements it; the `thinking` cases of `spec/fixtures/task-score-cases.json` check it
(the values of the retired intelligence cases, carried over unchanged).

**Window sums**, with the window, intervals and estimates of 6.3, over the responses and agents of a set (all enabled
sources, or an orb's `A`, 6.8):

| Contribution | Weight `w` (output tokens) | Reasoning `ρ` |
| --- | --- | --- |
| measured response | `output × f`, `f = overlap / (end − start')` as 6.3 | `min(reasoning, output) × f` |
| unmeasured response (6.3) | `estimatedSpeed × secs` | `w × share_ref` |
| in-flight agent (6.3) | `estimatedSpeed × secs` | `w × share_ref` |

`share_ref` is Σ `min(reasoning, output)` ÷ Σ `output` over the agent's reference set (the set 6.2 used for
`estimatedSpeed`, as 9.5 uses it for `costRate`), 0 when that output is 0. A source that does not record reasoning
apart from output (DeepSeek Harness, 4.3) adds its `w` but nothing to the reasoning share: its weight is left out of
`w'`.

- `liveThinking = Σ ρ / (W / 1000)` tok/s (formatted as 7).
- `thinkingShare = Σ ρ / Σ w'`, where `w'` is the weight of sources that record reasoning; 0 when `Σ w' = 0`.

Every sum is additive over sources, so an orb adds the per-source sums of its `A` (6.8). *Agent sums* are the same sums
restricted to the contributions of agent `a` — its own measured and unmeasured responses (`agentId` = `a`) and its own
in-flight estimate; `estimatedSpeed` and `share_ref` are exactly those of the all-sources sums (the reference set may
hold other agents' responses, 6.2). A sub-agent is an agent of its own. The sums of all agents add up to the set's, and
a board group's sums add its members' (never an average of their values): `liveThinking_a`, `thinkingShare_a`,
`liveThinking_G` and `thinkingShare_G` follow. The `thinking` cases also give each set's, agent's and group's `Σ w`
(`weight`), the tokens generating in the window; the score no longer uses it.

*Model sums* (for live reporting, 11.10) are the same sums restricted to one model: a measured or unmeasured response
counts for its own model, an in-flight estimate for its agent's current model, with `estimatedSpeed` and `share_ref`
exactly those of the all-sources sums. The sums of all models add up to the set's. Cases: `modelWindows` (keyed by the
raw model, the empty string when unknown; reporting adds the raw models that resolve to one id, 11.3).

### 6.10 Work signals

Per agent, from the tool calls in its own file: whether it is going in circles, and whether its recent actions keep
finding something new. Signals appear on the agent's row and tooltip (DESIGN 4); the task score (6.9) builds on the same
actions and on the same loop detection. `spec/tools/work_signals.py` implements the signals and checks
`spec/fixtures/work-signal-cases.json`.

**Actions.** A parser turns every tool call into an action `(time, tool, digest)` and every tool result into an
outcome `(time, failed)`, matched by call id:

| Source | Call | `tool`, `digest` input | Result | `failed` |
| --- | --- | --- | --- | --- |
| Claude (both) | assistant `message.content[]` block `tool_use` (each `id` once per file: a message's lines repeat it) | `name`; `input` | user block `tool_result` with that `tool_use_id` | `is_error == true` |
| Codex | `response_item` `function_call` / `custom_tool_call` | `name`; `arguments` / `input` | `…_output` with that `call_id` | the output's leading JSON object (in the first `input_text` item when `output` is an array) has an integer `exit_code` ≠ 0 |
| omp, Pi, OpenClaw | assistant `toolCall` block | `name`; `arguments` | `toolResult` with that `toolCallId` | `isError == true` |
| Factory Droid | assistant `message` line, `message.content[]` block `tool_use` (each `id` once per file) | `name`; `input` | user `message` line, block `tool_result` with that `tool_use_id` | `is_error == true` |
| others | — (no actions; no signals) | | | |

`digest` = the first 16 lowercase hex digits of SHA-256 over the call's input as the file stores it: the UTF-8 text of
a string input (Codex `arguments` and `input` are strings), the raw JSON text of an object or array exactly as it
appears in the line (Claude and Factory Droid `input`, omp and Pi `arguments`; OpenClaw: in the decoded entry). It is computed while the
line is parsed. An assistant line or entry ignored because its key was first seen in another file (4.1 step 2, 4.7
step 2) adds no actions, and its results match nothing.

**Task fields.** For the task score, each action also carries what the functions of `spec/TASK-PARSERS.md` derive while
the call and its result are parsed, with the agent's working directory: `kind` (`action_kind`), for a command `command`
(`command_info`), with the result `outcome` (`outcome`) and for a command `failures` (`failures`, over the result's
output text), for an edit `hunks` (`edit_hunks`) — the event stream of 6.9, composed as 6.9 *Composing the stream*
says (including the late outcomes of commands that outlive their call). They are computed in memory from the call's input and the result's output
(6.9 *Privacy*); only the fields listed there are kept. The signals below use only `tool`, `digest`, times and
`failed` from the table above; `outcome.failed` refines the same fact for the score (null for a command that did not
finish).

**Storage.** For the signals only `tool`, `digest`, times and `failed` are kept, at most the last **64 actions** per
agent. An outcome never read (no result yet, or a format that does not say) is `unknown`, which is not a failure. Copied
history (3.5, 4.7) and seeding (6.4) add no actions to this list: it starts with the file's live lines. The task score
keeps its own state for the agent's current task, seeded lines included (6.9 *Event stream*), and the history analysis
(6.11) reads whole files.

**Signals** of an agent at `nowMs`, over its actions within the last 10 minutes (`recent`):

- **Looping**: the newest actions repeat the same block of 1, 2 or 3 consecutive `(tool, digest)` pairs at least 3
  times in a row (the same call 3 times; `A B A B A B`; `A B C A B C A B C`), the newest action is less than 120 s
  old, and at least one action of the repeated run failed. The count shown is the number of repeats (the largest over
  the three block sizes). Shown as long as it holds.
- **Repeating**: the same, with no failure in the repeated run (polling, waiting). Shown only in the tooltip.
- **Novelty**: `distinct (tool, digest) in recent ÷ |recent|` when `|recent| ≥ 8`, else none. Shown in the tooltip
  only, as a percentage.
- **Recovered**: an action that failed was followed, within the next 5 actions and 10 minutes, by an action with the
  same `tool` that did not fail. The counts of failed and recovered actions in `recent` are shown in the tooltip.

A signal never changes phase, speed, totals or anything reported (11): it describes behaviour, not quality, and
identical actions can be legitimate (a test re-run after an edit has the same digest when the command is the same). The
score's behaviour efficiency (6.9 E) applies the same repeat detection to every position of the current task, and its
new-evidence rule tells a re-run after an edit from a re-run on unchanged code.

### 6.11 Task history, predictions and comparison

Per-task records from the history analysis (8), the predictions shown with a live score, their calibration on those
records, and the comparison of sources and models in the Usage History window (DESIGN). `spec/tools/task_score.py`
implements this section with 6.9.

#### Task records

The history analysis (8) runs the rules of 6.9 over every whole file of a scored source (copied lines excluded, as 3.5
and 4.7; seeding does not apply). Every task with at least one action gets a **record**; tasks without actions (a
question answered without tools, an empty prompt) have none.

| Field | Value |
| --- | --- |
| `ordinal` | the record's index among the agent's records, from 0 |
| `startMs` | the task's first prompt, or its first event for the task before the first prompt |
| `endMs` | the latest `t` of the task's events and of their outcomes |
| `day` | the local date of `endMs`, as 8 |
| `complete` | another task (with or without a record) follows in the agent, or `analysisMs − lastLineMs ≥ 30 min` (the agent's `lastLineMs`, 4) |
| `source`, `model`, `effort` | the agent's source, and its model (4) and current effort (EXECUTION-MODES) after the last line whose time is at most `endMs`; null when unknown |
| `state`, `score`, `V` … `K` | 6.9 over the whole task (`score` null when the state is `none`) |
| counts | `actions`, `counted`, `edits` (effective), `oscillations`, `loopActions` (marked, 6.9 E), `runs`, `failingRuns`, `episodes`, `recovered`, `recoverAttempts` (Σ attempts of recovered episodes), `fixAttempts` (= failing runs), `fixFailures` |
| `firstPassMs` | the earliest outcome `t` of a passing run; null when none passed |
| `success` | the final state is `passing` |
| `stalled` | the task has a *stall*: a window of 8 consecutive counted actions whose `Σ w·ΔI ≤ 0.25 × Σ w` (6.9 E) |
| `snapshots` | rows for the success and stall models (below) |
| `fixRows` | rows for the fix model (below) |

**Labels.** `success` labels every snapshot of the task. The stall label of the snapshot at `n` is 1 when a stall
window ends at a counted index above `n` (the task stalls after that point). The fix label of a failing run is 1 when
the next element of its check's sequence fails, or when there is none (the failure was never fixed in the task), and 0
when it passes; `fixFailures` is the number of 1 labels.

**Snapshots.** For every `n` in 1, 2, 4, 8, …, 512 up to the task's counted actions: 6.9 evaluated over the task's
events up to and including its `n`-th counted action (all outcomes known); kept when that state is not `none`, as
`[n, passing, failing, conv, ig, loop, osc, size, yStall]` (*Features*, below). **Fix rows**: for every failing run,
6.9 evaluated up to and including that run, as `[attempts, errors, trend, osc, ig, yFix]`. Powers of two keep rows from
every stage of short and long tasks at a bounded cost.

**Store.** The side table `task_records` of the history database (8):

```
task_records(file_id INTEGER NOT NULL, ordinal INTEGER NOT NULL, source TEXT NOT NULL, model TEXT, effort TEXT,
             start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, day TEXT NOT NULL, complete INTEGER NOT NULL,
             state TEXT NOT NULL, score REAL, v REAL NOT NULL, c REAL NOT NULL, r REAL NOT NULL, e REAL NOT NULL,
             k REAL NOT NULL, counts TEXT NOT NULL, first_pass_ms INTEGER, success INTEGER NOT NULL,
             stalled INTEGER NOT NULL, snapshots TEXT NOT NULL, fix_rows TEXT NOT NULL, version INTEGER NOT NULL,
             PRIMARY KEY (file_id, ordinal)) WITHOUT ROWID
```

- `file_id` is the `files` entry that holds the agent's contributions (a log file; an OpenClaw transcript window).
  `counts` is a JSON object of the counts above; `snapshots` and `fix_rows` are JSON arrays of the rows above (numbers
  only); `version` is the analysis version that wrote the row (the file's `files.version`).
- Created if missing every time the database is opened, like `contribution_unpriced` (9.10): `user_version` does not
  change, and older builds ignore the table.
- **Writes**: whenever a file's contributions are saved, the same transaction deletes the file's records and inserts
  its new ones. A file whose records include an incomplete task is analysed again, even unchanged, on the first run
  (8: launch, every 10 minutes) at least 30 minutes after its modification time, so its last task completes.
- **Reading**: only rows whose `version` equals the file's `files.version` count (an older build that re-analyses a
  file leaves stale rows). Records of files whose logs are gone stay, like their contributions. "Clear history" deletes
  the table with the store.
- No content: no prompt, text, path, command, output or digest is stored — only the fields above; `model` and
  `effort` are the ids as logged, like the per-model rows of the history (8).

Adding the records raised the history analysis version to **8** (8), so every log that still exists is analysed again
and the comparison and calibration have a baseline from the first launch. Cases: `records`. Records are never reported;
live reporting counts the tasks that complete while it is on with the same fields (11.10).

#### Features

Computed from a 6.9 evaluation (a snapshot, a fix row, or the live current task), with fixed scaling so that no
statistic of the data enters a feature:

| Feature | Value |
| --- | --- |
| `passing`, `failing` | 1 when the state is that, else 0 (`unverified` and `pending` are the base) |
| `conv` | `C / 25` |
| `ig`, `loop`, `osc` | `IG`, `L`, `O` (6.9 E) |
| `size` | `min(1, log2(1 + n) / 9)` (1 from 511 counted actions) |
| `attempts` | `min(1, (a − 1) / 4)`, `a` = the failing run's position in its episode (1 for the first fail) |
| `errors` | `min(1, log2(1 + F) / 8)`, `F` of the failing run |
| `trend` | `F / F_peak`, over the check's sequence up to that run (1 when it is the worst so far) |

#### Predictions

Three logistic models, `P = σ(β₀ + Σ βᵢ·xᵢ)` with `σ(η) = 1 / (1 + e^(−η))` and `η` clamped to [−35, 35]:

| Model | Question | Features `x` (in this order) | Prior `β` (intercept first) |
| --- | --- | --- | --- |
| `success` | will the task end `passing`? | `passing`, `failing`, `conv`, `ig`, `loop`, `osc`, `size` | −1.5, 3.0, −0.5, 1.0, 1.0, −2.0, −1.5, −0.5 |
| `stall` | will it stall from here on? | `ig`, `loop`, `osc`, `failing`, `size` | −1.0, −2.0, 3.0, 2.0, 0.8, 0.5 |
| `fix` | will the next run of the failing check fail too? | `attempts`, `errors`, `trend`, `osc`, `ig` | −0.5, 1.5, 1.0, 1.0, 1.5, −1.0 |

The priors are documented guesses, not fitted: their signs say what the dimensions mean (a pass, convergence and new
evidence raise success; loops, oscillation and a failing state raise the stall risk; repeated attempts, many errors, no
trend and undone edits make a fix more likely to fail), and their sizes give plausible values: an unverified task that
keeps finding something new reads about 35 % success and 5 % stall; a passing, converged task about 97 % success.

For an agent's current task **with a score** (6.9), on every engine tick: `P(success)` and `P(stall)` from the live
features; `P(fix fails)` only in state `failing`, from the fix-row features of the open failure (6.9 *Task facts*) as
they stood right after that run. Shown as percentages
rounded half away from zero (DESIGN). They use the coefficients of the agent's source (*Calibration*), with the flag
"prior" or "calibrated on N tasks" per model. Cases: `predictions`.

#### Calibration

Per scored source and per model, from that source's records, on every history analysis run (8; in memory, never
stored):

1. Take the source's complete records, ordered by `endMs`, then `startMs`, then the agent id (ordinal), then `ordinal`,
   and keep the latest **2000**.
2. Rows: for `success` and `stall`, every snapshot (labels `success` and `yStall`); for `fix`, every fix row. Each row
   weighs `s = 1 / m`, `m` the task's number of rows for that model, so every task counts once. `N` is the number of
   tasks with at least one row.
3. The model is **calibrated** when `N ≥ 40` and both the positive and the negative rows weigh at least 5 in total;
   otherwise it keeps the prior and shows "prior".
4. Fit: the maximum a-posteriori estimate with a Gaussian prior centred on the prior coefficients, minimising
   `−Σ s·[y·log p + (1 − y)·log(1 − p)] + (λ/2)·Σₖ (βₖ − β⁰ₖ)²` with `λ = 4` (the prior weighs like a handful of
   tasks, so 40 tasks already move it and a few hundred dominate it). Newton–Raphson (IRLS) from `β = β⁰`, exactly 25
   iterations, no early stop:
   - gradient `g = Σ s·(p − y)·x + λ·(β − β⁰)`, Hessian `H = Σ s·p·(1 − p)·x·xᵀ + λ·I`, rows summed in the order of
     step 1 and within a task in row order, `x` with a leading 1;
   - solve `H·δ = g` by Cholesky (`H = L·Lᵀ`, column by column; forward then back substitution) and set `β ← β − δ`.
     `H` is positive definite because `λ > 0`.
   Every sum in 6.9 – 6.11 (dimensions, information gain, the fit, the comparison) is plain left-to-right IEEE 754
   double addition in the stated order, never compensated or pairwise summation. The reference uses
   `live_thinking.seq_sum` for this, because Python 3.12's `sum` compensates float rounding.
5. Python, Swift and C# agree within 10⁻⁶ on every coefficient and probability; the fixture checks 10⁻⁹ in the
   reference and, independently of the fitting path, that the gradient vanishes (below 10⁻⁹) at the stored
   coefficients.

Cases: `calibration` (a synthetic history: one source calibrated, one below 40 tasks, one with a single outcome).

#### Comparison

The Usage History window's *task performance* (DESIGN 12) compares sources and models over its date range
[`first`, `last`]: the complete records whose `day` lies in the range (incomplete ones join once complete) — of every
scored source, or only of the one source chosen in the window's source picker. For each source with records, in the
source order of section 1 (`bySource`), and for each model id with records across those sources, the empty string for
an unknown model (`byModel`, ordered by `tasks` descending, then the id's UTF-8 bytes; each row also lists its
`sources`, the sources with records of that model, in source order):

| Value | Computation (null when its denominator is 0, or when there is no value to take a quantile of) |
| --- | --- |
| `tasks` | records |
| `scoredTasks` | records with a score |
| `medianScore` | `q(0.5)` of their scores; shown rounded half away from zero |
| `lowerQuartile`, `upperQuartile` | `q(0.25)` and `q(0.75)` of their scores |
| `histogram` | ten counts of their scores, bins `[0, 10)`, `[10, 20)`, … `[80, 90)` and `[90, 100]` (all zeros without a scored record) |
| `successes`, `stalls` | `success` records (among the scored ones), `stalled` records: the numerators of the two rates, for the tooltips of DESIGN 12 (`96 / 128`) |
| `fixFailures`, `fixAttempts`, `recovered` | Σ `fixFailures`, Σ `fixAttempts`, Σ `recovered` |
| `successRate` | `successes` ÷ `scoredTasks` |
| `stallRate` | `stalls` ÷ `tasks` |
| `fixFailureRate` | `fixFailures` ÷ `fixAttempts` |
| `recoverAttempts` | Σ `recoverAttempts` ÷ `recovered`: the mean attempts of a recovered episode |
| `timeToFirstPassMs` | `q(0.5)` of `firstPassMs − startMs` over records with a pass |

- *Scores* are the unrounded `score`s of the records with a score; tasks without one count only in `tasks` and
  `stallRate`.
- `q(p)` over values sorted ascending `v[0] … v[n − 1]`: `h = (n − 1)·p`, `i = ⌊h⌋`; `q = v[i]` when `h = i`, else
  `v[i] + (h − i)·(v[i + 1] − v[i])` (linear interpolation between order statistics). So the median is the middle value,
  or the two middle ones' midpoint, and one value is every quantile.
- A score's bin is the number of the edges 10, 20, 30, 40, 50, 60, 70, 80 and 90 that it reaches (`score ≥ edge`),
  compared exactly, never by dividing: 9.999 is in bin 0, 10 in bin 1, 90 and 100 in bin 9.

Cases: `comparison` (a range, one source, an empty range, quartiles and bins at their edges with a model across two
sources, a source without scored tasks).

## 7. Number formatting (both platforms)

| Value | Display |
| --- | --- |
| `< 10` | one decimal (`4.2`); exactly 0 → `0` |
| `< 1000` | integer (`128`) |
| `< 10 000` | `1.28k` (two decimals) |
| `< 1 000 000` | `12.8k` / `128k` |
| `< 1 000 000 000` | `1.28M` / `12.8M` / `128M` |
| else | `1.28B` / `12.8B` / `128B` (three significant digits, more digits only above 999B) |

A value that rounds up to the next unit uses that unit (`999 700` → `1.00M`, `999 700 000` → `1.00B`). Token counts in
summaries use the same compact form (`1.2M`, `384k`, `2.49B`).
## 8. Daily history

Daily statistics are computed from the complete logs and kept permanently, so they survive tools deleting their own
logs (Claude Code removes transcripts after 30 days by default). Only aggregates and per-task records without content
(6.11) are stored — never requests, keys in plain text, prompts or content.

Scope: every discovered file of every enabled source with no date filter and no file-count limit (Codex `.jsonl.zst`,
all Gemini roots with legacy `.json`, every omp root including profiles, every Antigravity conversation database
of every store, both WorkBuddy roots, the rotated logs of every Hermes Agent profile and every Factory Droid session
with its sidecar included), plus Codex `archived_sessions` (the sibling of the sessions root) and the whole OpenCode
database. Parsing uses exactly the rules of section 4 (same state machines, dedupe, copied-history rules) over whole
files and complete database reads, in startup order (5; an Antigravity database's time is the newer of its `.db` and
`-wal`, 5.4).

Per local calendar day (date of `endMs`, `YYYY-MM-DD` in the computer's time zone at computation time) and source:
`requests`, `input`, `cachedInput`, `cacheWriteInput`, `output`, `reasoning`, `measuredOutput` and `measuredMs`
(sum of output and widened duration of measured responses, 3.3), `unfinalized` (count), `agents` (distinct agent ids
with at least one response), and per model `requests`, `output`, `measuredOutput` and `measuredMs` (so a model's average speed can be shown). The day's average speed is
`measuredOutput / (measuredMs / 1000)`. `peak` is stored only for days observed live: it is the day's overall
`peakToday` (all sources together), repeated on every source row of that day; a UI shows it only for all sources and
combines rows with max, never sum.

A response whose `endMs` is before `0001-01-03T00:00:00Z` or after `9999-12-29T23:59:59.999Z` (only broken timestamps
get there, 3.1, 3.6) has no date, since in some time zone it would fall outside the years 0001–9999: it is left out of
daily history and has no cost (it counts as unpriced, 9.3, 9.6), while it still counts in the live totals.

Incremental cache: for each file keep its identity (path, size, modification time; for an Antigravity database also
the size and modification time of its `-wal`), the 64-bit FNV-1a hashes of the
response keys it owns, and its per-day contributions. On launch and periodically (every 10 minutes and at midnight),
unchanged files reuse their contributions; new or changed files are re-parsed completely, skipping keys owned by any
other file (ownership follows startup order); files that no longer exist keep their contributions. Hermes Agent
logs (4.15) are cached by file identity instead of path: a rotation renames a log without changing it, so it keeps its
contributions and keys under its new name, and the oldest log, which rotation deletes, keeps its contributions like any
deleted file. Daily totals are the sums over files. The history store is versioned: a parser change bumps the version
and triggers a full recomputation that keeps previously stored days for which no logs exist any more (analysis version
8: the task records of 6.11; 7: the unpriced facts of 9.10; 6: the Codex ordinal rule of 3.5). "Clear history"
(settings) deletes the store and recomputes from the current logs.

Factory Droid (4.16): a session's identity adds its sidecar's size and modification time and, for a session with a
`parent`, those of the parent's sidecar, whose counters it subtracts; the history rule of 4.16 makes its contributions a
function of these files alone; a session whose parent sidecar is unreadable is not stored by that analysis and is
analysed again by the next one (4.16 *Own counters*). Adding the source needs no new analysis version: no earlier build
read its files, so the first analysis finds them as new files (this covers the `custom` step of 9.2 too, which in
practice only Droid's ids reach).

Cost (9.7): every row also has `costMicroUsd` and `unpricedRequests`, every model `costMicroUsd` and
`unpricedRequests`, and every date a `priceVersion`. The cost feature bumps the store version; days kept from an
older store whose logs no longer exist have no cost data (`costMicroUsd` null, `unpricedRequests` null): UIs show
them as unavailable (DESIGN 9), never as 0.

Unpriced facts (9.10) are stored in the side table `contribution_unpriced` of the same database, written with each
file's contributions; older builds ignore it, and readers check its rows against the file's analysis version.

Task records (6.11) are stored the same way in the side table `task_records`, one row per task of a scored source's
agent, written with the file's contributions. A file whose last task was still open is analysed again once it has been
quiet for 30 minutes (6.11 *Store*).

Network traffic (10.9) is stored in its own table of the same database. It is observed, not derived from logs:
version bumps and recomputations never touch it, and "Clear history" deletes it too.

## 9. Cost

Agent Speed estimates what the counted responses would cost at the providers' official API list prices — the
*API-equivalent* cost (DESIGN 9). Costs are exact integers in US dollars; conversion to the display currency happens
only when a value is shown (9.8). `spec/tools/prices.py` implements 9.1–9.3 and 9.7; `reference.py` uses it for 9.5–9.7
and `--dump-history` / `--dump-today` (`--prices PATH` selects another table); `spec/tools/unpriced.py` implements 9.10
(`reference.py --dump-unpriced`).

### 9.1 Price table

`spec/assets/prices.json` is bundled with every build and synced daily from this repository (NETWORK 3). The
*current* table is the higher `version` of the bundled table and the synced copy. `python3 spec/tools/prices.py`
validates it (the same rules apps apply to a download) and runs its `tests`.

| Field | Meaning |
| --- | --- |
| `schema` | `1`. A table with another schema is never used by a build that does not know it. |
| `version` | Positive integer, increased by one with every published change, never reused. |
| `updated` | `YYYY-MM-DD` of the change. |
| `currency`, `unit` | `"USD"`, `"perMillionTokens"`. |
| `providers` | key → `{name, url}`, `url` = the provider's official pricing page. |
| `normalize` | The lists used by 9.2. |
| `models[]` | `id` (lowercase, the provider's API id), `provider`, `name`, `status` (`current` · `preview` · `legacy` · `deprecated` · `retired`; retired models stay so that old days keep a price), either `prices` (with optional `tiers`, `offPeak`, `periods` and `fast`) or `"free": true`, optional `aliases`, `source` (https URL of the page the prices come from, or an archived copy of it for past prices), `checked` (date it was verified, ≤ `updated`), optional `note`. |
| `unknown[]` | `{match: "exact" \| "prefix", value, reason}`: ids known to have no public price (internal or opaque ids). |
| `tests[]` | `{model, provider?, expect}`: `expect` is a model id, `free` or `unknown` (9.2). |

**Price set**: `input` (uncached input), `cachedInput` (cache read; absent → `input`), `cacheWrite` (cache write with
the default lifetime, e.g. Anthropic's 5 minutes; absent → `input`), `cacheWrite1h` (1-hour cache write; absent →
`cacheWrite`), `output` (includes reasoning). Values are USD per million tokens, 0 – 10 000, with at most 6 decimals.
Implementations convert every price to the integer `p = price × 1 000 000` — micro-USD per million tokens, which is
pico-USD per token — by parsing the JSON number as a decimal, or by rounding `price × 1e6` to the nearest integer
(exact for at most 6 decimals).

**Tiers**: `tiers` = `[{above, …the same fields as prices}]`, `above` strictly ascending. The tier with the highest
`above` that is less than the response's `input` (3.2: all input, cached and cache writes included) replaces the
prices for the *whole* response. Thresholds are exclusive: "more than 200 000 tokens" is `above: 200000`, "200 000 or
more" is `above: 199999`.

**Periods**: `periods` = `[{from?, to?, prices, tiers?, offPeak?, note?}]`, at least one bound, never overlapping.
Bounds are UTC instants, `YYYY-MM-DD` (00:00 UTC) or `YYYY-MM-DDTHH:MMZ`; a period covers `from ≤ endMs < to`. A
response whose `endMs` lies in a period uses that period's `prices`, `tiers` and `offPeak` (absent = none) instead of
the model's. Periods carry announced future price changes, earlier prices for old days, promotions, and retired ids
that the provider bills at another model's price. Instants, not local dates, so every computer prices alike.

**Off-peak**: `offPeak` = `{peakUtc, peakWeekdays, offPeakDates?, prices, tiers?}` (DeepSeek bills half price outside
its peak hours). A response is at peak when the UTC date of its `endMs` is not in `offPeakDates`, its ISO weekday (1 =
Monday) is in `peakWeekdays`, and its UTC time of day lies in one of the `peakUtc` windows (`HH:MM-HH:MM`, start
inclusive, end exclusive). Otherwise it uses the `offPeak` prices and tiers.

**Fast mode**: `fast` = `{prices, tiers?}`, used instead of everything above (periods and off-peak included) for a
response with `fast` (3.2) or an id that selects fast mode (9.2). A fast response of a model without `fast` prices
has an unknown cost: never the cheaper standard price.

**Free**: `"free": true` — every response of the model costs 0, a known value.

### 9.2 Model resolution

Input: a response's `model` and `provider` (3.2). Result: a priced entry, `free` or `unknown`. No fuzzy matching:
everything these rules do not reach needs an explicit alias.

1. `provider`, trimmed and lowercased, is in `normalize.freeProviders` (local runtimes: `ollama`, `lmstudio`,
   `llama.cpp`, …) and the lowercased model does not end with an entry of `cloudSuffixes` (Ollama Cloud models run
   remotely) → **free**. A base URL is never used: local proxies to paid APIs exist.
2. Missing or empty model → **unknown**.
3. Candidate keys, formed in this order, each one looked up as soon as it is formed; the first hit wins. A key equal to
   an `id` or alias resolves to that entry (`free` when the entry is free); otherwise a key that equals an `exact`
   `unknown` value or starts with a `prefix` one is **unknown** with its reason.

   | Step | Key | Example |
   | --- | --- | --- |
   | exact | remove every `[…]` group, collapse whitespace, trim, lowercase | `claude-opus-5-5[1m]`, `Claude-Opus-5-5` → `claude-opus-5-5` |
   | custom | a key starting with `custom:` (Factory Droid's ids of the user's own models, `custom:<name>-<n>`, 4.16) loses that prefix, then a trailing `-` followed by one or more digits (droid's index), then spaces and `-` at both ends (left by a removed `[…]` group) | `custom:GPT-5.5-[OpenAI-BYOK]-0` → `gpt-5.5`; `custom:Claude-Opus-5.5-Thinking-0` → `claude-opus-5.5-thinking` (→ `claude-opus-5-5` by the steps below) |
   | route | the part after the last `/` | `anthropic/claude-sonnet-4.5` → `claude-sonnet-4.5`; `models/gemini-2.5-pro` |
   | cloud | `<region>.` (from `bedrockRegions`) is removed when a `<vendor>.` (from `bedrockVendors`) follows; a leading `<vendor>.` is removed and then a trailing `-v<N>` or `-v<N>:<M>`; a trailing `@YYYYMMDD` becomes `-YYYYMMDD`, any other trailing `@…` is removed | `us.anthropic.claude-sonnet-4-5-20250929-v1:0` → `claude-sonnet-4-5-20250929`; `claude-opus-5-5@default` → `claude-opus-5-5` |
   | free | the key ends with an entry of `freeSuffixes` (and is longer) → **free**, no lookup | `deepseek-v4.1-flash-free`, `…:free` |
   | variant | remove entries of `variantSuffixes` at the end, repeatedly | `gpt-5.5:nitro:exacto` → `gpt-5.5` |
   | rewrite | the first `rewrites` entry whose `prefix` starts the key replaces that prefix with `to` | `duo-chat-sonnet-5-5` → `claude-sonnet-5-5` |
   | fast | a key ending with an entry of `fastSuffixes` loses it and selects fast mode for this and every later step: a hit on an entry with `fast` prices is priced in fast mode, a hit on an entry without them is **unknown** | `claude-opus-5-5-fast` → `claude-opus-5-5` (fast); `claude-sonnet-5-5-fast` → unknown |
   | date | for keys starting with an entry of `dateSuffixPrefixes`, remove a trailing `-` and 8 digits | `claude-sonnet-4-5-20250929` → `claude-sonnet-4-5` |
   | modifier | for the first `modifierSuffixes` entry whose `prefix` starts the key, remove its suffixes at the end repeatedly, as long as more than the prefix remains | `claude-opus-5-thinking-max` → `claude-opus-5`; `gemini-3.1-pro-high` → `gemini-3.1-pro` |
   | dotHyphen | for keys starting with an entry of `dotHyphenPrefixes`, replace every `.` by `-` | `claude-opus-4.8` → `claude-opus-4-8` |

4. No hit → **unknown**.

The lists are data, so a synced table can extend them. The rules are scoped on purpose: elsewhere a date or suffix is
a different model at a different price (`gpt-4o-2024-05-13` $5/$15 vs `gpt-4o` $2.5/$10, `mistral-large-2411` vs
`-2512`, `qwen3-max`, `gpt-5.5-pro`), so none of those is stripped; `-fast` is tried only after the full id (so
`grok-4-fast`, a model of its own, matches first) and never falls back to the standard price.
`-latest` and display names (`Gemini 3.1 Pro (High)`) resolve only through aliases. The validator checks that every
id and alias resolves to its own entry, so a rule can never shadow an alias. Examples: `spec/fixtures/price-cases.json`
`resolve` and the table's `tests`.

### 9.3 Response cost

Billed token classes of a response (3.2):

- `uncached = max(0, input − cachedInput − cacheWriteInput)`
- `cacheRead = cachedInput`
- `write1h = min(cacheWrite1hInput, cacheWriteInput)`, `write = cacheWriteInput − write1h`
- `output` (reasoning included; every source counts reasoning inside `output`)

Each class is clamped to 100 000 000 tokens (no real response comes near; it keeps the sum below 2^63 with prices up
to 10 000 USD per million tokens).

With the price set of 9.1 — fast prices for a fast response, else the period covering `endMs`, then its off-peak
prices when `endMs` is off-peak, then the tier chosen by `input` — in 64-bit integers:

```
costPicoUsd  = uncached·p_input + cacheRead·p_cachedInput + write·p_cacheWrite + write1h·p_cacheWrite1h + output·p_output
costMicroUsd = (costPicoUsd + 500 000) div 1 000 000          (half up)
```

`costMicroUsd` is *the* cost of the response everywhere — live rate, today, history, CSV — so every total is a sum of
integers and any grouping (per file, day, source, model) gives the same number. Free → 0. Unknown → no cost (null),
never 0: it is counted in `unpricedRequests` instead. A response without a date (8) has no cost either. The cost is
computed when a response is emitted and again when an update replaces it (4.4), with the table of its date (9.7);
periods and off-peak hours follow its `endMs`. Cases: `spec/fixtures/price-cases.json` `cost` (fixture tables) and
`bundled.cost` (the real table of 9.1, each at a fixed `at`).

### 9.4 Subscriptions

Settings can mark sources as used through a subscription (DESIGN 9). Costs of every source are always computed and
stored; marks apply only when values are shown: with `includeSubscriptions` off (the default) every displayed cost
(live, today, orb, board, history totals) sums only the *billable* sources, the ones not marked; with it on, all
sources. A mark is a current setting, not data: changing it rewrites nothing.

### 9.5 Live cost rate

The money unit of the orb, menu bar and dashboard (DESIGN 9) and a board's 費用 (DESIGN 2c). Window, intervals and
estimates are those of 6.3.

- Measured: for every measured response with a known cost, `costMicroUsd * overlap(interval, [start', end]) /
  (end - start')`.
- Estimated: for every unmeasured response and every in-flight agent that 6.3 estimates, with the same `secs`
  (`overlap / 1000`), add `costRate(a) * secs`. `costRate(a)` = Σ `costMicroUsd` / Σ widened duration (seconds) over
  the responses of `a`'s *reference set* that have a known cost; the reference set is the set 6.2 used for
  `estimatedSpeed(a)` (the agent's up to 3 most recent measured responses within 10 minutes, else the model's up to 10
  within 30 minutes, else the source's). No cost estimate when none of them has a known cost.
- `liveCost = (measured + estimated) / (W / 1000)` micro-USD per second.
- `liveCostKnownShare` = the output tokens of 6.3 (measured and estimated) that carry a known cost ÷ all of them (1
  when there are none): a measured response counts when its cost is known, an estimate when `costRate` exists.
- Per source: the same formula restricted to the source's responses and agents. Both values are additive over
  sources, so an orb or a board (6.8) and the billable sum (9.4) add per-source values:
  `knownShare = Σ known tokens / Σ tokens`.
- No per-agent or per-group form: boards show only the cost of their sources (DESIGN 2c; user's choice, 2026-10-28,
  the per-row and tooltip costs are retired), and nothing else shows a live cost per agent, so the engine keeps none.
- The money value never drives heat, tiers, titles, peaks or `newPeak`; those follow the token metric.

Engine cases with `liveCost` in `spec/fixtures/engine-cases.json` give responses their `costMicroUsd` directly.

### 9.6 Today

- `today.costMicroUsd`: Σ `costMicroUsd` of today's responses with a known cost, as recorded (unfinalized ones
  included, so it is a lower bound like `output`).
- `today.unpricedRequests`: today's responses without a known price.
- Both per source, and per model for breakdowns. The dashboard's today cost equals the history's row for today
  (Factory Droid: up to the split of a session's tokens between its calls, 4.16).
- `today.unpricedFacts`: per source, the unpriced facts of today's responses (9.10 *Live*), for Settings → Cost.

### 9.7 Day binding and history

Each day reads like a bill: it keeps the prices that applied to it.

- **Binding**: the history store keeps `date → table version` for every local date that has been priced. The first
  time a response of a date is priced (by the engine or by the history analysis), the date is bound to the current
  table version. A binding never changes afterwards — not when a newer table arrives, not when a changed file is
  re-analysed, not on a full recomputation after a store version bump.
- **Pricing a response of date `d`**: with the table bound to `d` (its periods and off-peak hours apply by `endMs`). If that table
  cannot price it (`unknown`), the current table is used instead (*late pricing*): a new model added to the table later
  fills in the days before, while a price that was known never changes. Still unknown → unpriced.
- A price change in a new table therefore shows from the next day on (today stays bound); a change announced in
  advance takes effect on time when the bound table already carries it as a period.
- **Re-analysis**: per-day contributions of a re-analysed file are recomputed with these rules, so they are a pure
  function of the file's responses, the bindings and the stored tables. When the current table version changes, every
  file whose stored contribution has `unpricedRequests > 0` is re-analysed (as if it had changed), and the engine
  re-prices today's unpriced responses; it keeps the normalized usage of today's unpriced responses (at most 10 000)
  for that, with their `model`, `provider`, `fast` and `endMs`.
- **Storage**: the store keeps a copy of every table version that a binding references (deleted when no binding
  references it). A binding whose table is missing or invalid is replaced by the current version (logged). "Clear
  history" deletes bindings and tables with everything else.
- **Rows** (section 8): per date and source `costMicroUsd` (Σ known costs) and `unpricedRequests`; per model the same
  two; `priceVersion` = the date's binding (repeated on each row of the date); the unpriced facts (9.10). Subscription
  marks are not stored.
- Fixtures: `spec/fixtures/history-expected.json` `prices` (the fixture tables, the highest version current, and the
  bindings to load before the analysis) and `spec/fixtures/price-cases.json` `days` (among them a model that only a
  newer table prices: `mimo-v2.6-pro` of a custom provider, issue #20).

### 9.8 Display currency

Costs are stored and summed in micro-USD. A shown amount is `costMicroUsd / 1 000 000 × rate(currency)`, with the
latest accepted daily rate (NETWORK 4; USD = 1). History days are converted with that same latest rate, not the rate
of their own day: a range total then always equals the sum of its days, switching currencies changes no stored data,
and no historical rates are needed. The UI states the rate date (DESIGN 9). Rounding and formatting are DESIGN 9.

### 9.9 Known approximations

- List prices only: subscriptions, free tiers and credits, enterprise discounts, batch, flex and priority tiers,
  regional or data-residency surcharges (for example Bedrock regional endpoints and Vertex regions, +10 % on newer
  Claude models) and taxes are not modeled.
- Charges that are not tokens of a recorded response are not counted: web search and fetch requests, code execution,
  grounding, cache storage per hour, image and audio inputs priced differently from text.
- Calls an agent does not record (titles, compaction summaries, classifiers; 4.x approximations) are missing, so costs
  are lower bounds, like tokens. Unfinalized Claude Desktop responses count with their recorded usage (complete input,
  partial output).
- Only Claude Code separates 1-hour cache writes; every other source prices all cache writes at `cacheWrite`
  (OpenAI's 12-hour writes, 2.5× input, are priced as its 30-minute writes, 1.25×).
- Fast mode is known only for Claude Code (`usage.speed`) and for ids that name it (`-fast`); OpenAI's fast and
  ultrafast tiers in Codex are priced at standard rates. US-only inference (`usage.inference_geo == "us"`, 1.1× on
  newer Claude models) is not applied.
- DeepSeek's off-peak rule needs the list of Chinese public holidays in `offPeakDates`; weekend make-up workdays are
  treated as off-peak. Qwen's implicit-cache price (20 % of input) is used for every cache read, explicit caching
  (10 %) is not distinguished.
- A tier is chosen by the normalized `input`; for Gemini CLI that includes tool-use prompt tokens.
- OpenCode and omp record their own cost; it is not used, so every source follows one rule (resellers such as
  OpenCode Zen sometimes charge other prices than the original provider).
- Models behind a user's own proxy or router are priced by the model name the agent logged; opaque names stay unknown.
- WorkBuddy bills in credits, which are not used; calls through its routing slots stay unpriced unless the stream names
  the model it chose (4.9).
- Factory Droid's Factory credits are not used either. Its per-call tokens are allocated from session counters (4.16),
  so the tier a call is priced in can differ from the provider's bill; the user's own models price only when their
  name, after the `custom` step of 9.2, is a model id or alias of the table.

### 9.10 Unpriced diagnostics

Why responses have no price, shown on this computer only (DESIGN 9 *Unknown prices*). Nothing of it is reported:
section 11, `spec/REPORTING*.md` and `spec/report-schema.json` are unchanged, and raw model names leave the app only in
text the user copies. `spec/tools/unpriced.py` implements this section and checks `spec/fixtures/unpriced-cases.json`
(written by `spec/tools/unpriced_cases.py`, expectations by hand); both apps run the same cases.

**Facts.** Every response without a known cost (9.3) is an *unpriced fact* `(model, provider, fast)` counting its
requests (`requestCount` where a source records one, else 1): `model` is the response's model *trimmed* (4.9; missing
→ the empty string), `provider` its provider (3.2; null when missing or empty), `fast` its fast flag (3.2). Equal facts
add up. A free response (cost 0) is never a fact, and days kept without cost data (8) have none. Trimming does not
change the resolution (9.2 trims and collapses whitespace itself), so a fact has the reason of each of its responses.

**Reason.** Computed from `(model, provider, fast)` with the *current* table (9.1) every time it is shown, never stored,
so a model that a newer table prices leaves the list by itself (late pricing, 9.7). The resolution of 9.2 decides:

| Reason | When | `canonical` |
| --- | --- | --- |
| `noModel` | step 2: the model is empty | null |
| `unpriceable` | a candidate key equals or starts with a value of the table's `unknown[]`; that entry's `reason` is the fact's *rule* | the key that matched |
| `noFastPrice` | a candidate key hits a priced entry without `fast` prices while fast mode is selected, by the `fast` step or by the fact's `fast` | the key that hit |
| `notInTable` | no candidate key hits (step 4) | the `exact` step key; null when it is empty (`[proxy]`) |
| `noDate` | live only: the response's `endMs` has no date (8), whatever its model | null |
| *priced now* | the current table prices the fact (free included): no reason, it counts in `pricedNow` | — |

`Index.diagnose` in `prices.py`; cases `diagnose` (each also checks that the reason is null exactly when 9.3 prices the
response). Router and internal labels (`neko-model-*`, `codex-auto-review`, WorkBuddy's routing slots, …) stay
`unpriceable`: the table never maps them to a similar model's or a source's default price.

**History store.** A side table of the history database (section 8) keeps the facts of every stored contribution:

```
contribution_unpriced(file_id INTEGER NOT NULL, day TEXT NOT NULL, source TEXT NOT NULL, model TEXT NOT NULL,
                      provider TEXT NOT NULL, fast INTEGER NOT NULL, requests INTEGER NOT NULL,
                      version INTEGER NOT NULL, PRIMARY KEY (file_id, day, source, model, provider, fast)) WITHOUT ROWID
```

- Created if missing every time the database is opened, like `day_traffic` (10.9). The schema version
  (`user_version`) does not change, so older builds keep opening the store; they ignore the table.
- A row is one fact of one contribution (an entry of `files` — a log file or a database session such as OpenCode's
  or ZCode's — day, source): `model` trimmed (empty when none), `provider` the provider or the empty string for null
  (3.2 never yields an empty provider), `fast` 0 or 1, `requests` ≥ 1, `version` the analysis version that wrote it
  (the file's `files.version`).
- **Writes**: whenever a file's contributions are saved, the same transaction deletes the file's side rows and inserts
  the facts of each of its contributions. A contribution without unpriced responses has no rows. Re-analysis after a
  table change (9.7) rewrites them with the file.
- Adding the table raised the analysis version to **7**, so every file whose logs exist is analysed again and fills
  it. Contributions kept from files whose logs are gone get no rows; *Reading* derives theirs.
- An older build that re-analyses a file rewrites `contributions`, `contribution_models` and `files.version` (its own,
  lower version) but leaves the side rows: their `version` no longer matches, so readers ignore them until a build
  with this table analyses the file again (it does, since the version differs).
- "Clear history" deletes the table with the store.

**Reading** (cases `read`), for each stored contribution (file, day, source):

1. `unpriced_requests` null (no cost data, 8): no facts; its `requests` count as `noCostData`.
2. `unpriced_requests` 0: no facts; stray side rows are ignored.
3. The contribution's side rows are *valid* when there is at least one, every row has `version` = the file's
   `files.version` and `requests` ≥ 1, and their `requests` add up to `unpriced_requests`. Valid rows are the facts
   (rows equal after trimming the model add up).
4. Otherwise the facts are *derived* from `contribution_models`: in the order of the models' UTF-8 bytes, each model's
   `unpriced_requests`, at most what is still left of the contribution's `unpriced_requests`, becomes the fact
   `(trimmed model, null, false)`; what is left after the last model becomes `("", null, false)`. The contribution's
   `unpriced_requests` count as `derived`: provider and fast flag are unknown, so the reason is computed as if there
   were none (a Fast response can then read as priced now).

Side rows without a contribution row are ignored. A day's facts per source are the sum over its contributions, and
add up to its stored `unpricedRequests`.

**Live** (cases `live`): the engine's today (6.6, 9.6). The day ledger keeps per source the requests of today's
responses without a known cost per `(model, provider, fast, noDate)`, added and removed with each response like every
other today tally, so replacements (4.4) and re-pricing (9.7) move them; it restarts at zero at the day change.
`noDate` is true for a response whose `endMs` has no date (8; such a response belongs to today when
`endMs ≥ todayStartMs`), and such a fact is always `noDate`, never the reason of its model. The live view has one
part per source with today's responses: `requests` = today's requests of the source, its facts, `noCostData` and
`derived` 0. Settings → Cost shows it (DESIGN 9); the History window never does (its today comes from the store).

**Rows** (cases `reports`). A *view* takes *parts* — per (day, source) the requests, `noCostData`, `derived` and facts —
whose day lies in [`first`, `last`], for one source or all, without the sources the window excludes as subscriptions
(9.4, exactly as its costs). The History window's parts are the stored days (today included) after *Reading*; the live
view's single day is the engine's.

- Per source with at least one part in the view: `requests`, `noCostData`, `derived` (Σ over its parts), `unpriced`
  (Σ of its facts) and `pricedNow` (Σ of its facts the current table prices).
- A *row* gathers the facts of one (source, model, provider, fast, noDate) that have a reason: `reason`, `canonical`,
  `rule` (only for `unpriceable`), `requests` (Σ), `first` and `last` (the earliest and latest day of its facts).
- Order: sources by `unpriced` descending, then the order of section 1; a source's rows by `requests` descending, then
  the model's UTF-8 bytes, then provider (null first, then UTF-8 bytes), then `fast` (false first), then `noDate`
  (false first).
- Per source, Σ rows' `requests` + `pricedNow` = `unpriced`; over the view Σ `unpriced` equals the History window's
  `history.unpricedNotice` count (Σ stored `unpricedRequests`) and, live, today's `unpricedRequests` (9.6).

**Text** (`history.unpricedModels.copy`; cases `reports` `expect`, byte-identical on both platforms). UTF-8 without
a byte order mark; every line ends with LF:

```
Agent Speed unpriced diagnostics 1
app version=<app version> platform=<macos | windows> os=<os>
prices version=<current table version> updated=<its updated date>
range view=<view> first=<date> last=<date> days=<n> source=<source id | all> excluded=<ids | none>
source id=<id> requests=<n> unpriced=<n> pricedNow=<n> noCostData=<n> derived=<n> rows=<n>
row source=<id> reason=<reason> requests=<n> first=<date> last=<date> fast=<bool> model=<text> provider=<text> canonical=<text> rule=<text>
total requests=<n> unpriced=<n> pricedNow=<n> noCostData=<n> derived=<n> rows=<n>
```

- `1` is the format version: any change of lines, fields or their order raises it.
- One `source` line per source of the view in the order of *Rows*, each followed by its `row` lines; `total` sums the
  source lines (`rows` = the number of rows). A view without parts has only the header lines and `total`.
- `os`: `macos-<major>` (`macos-26`), `windows-11` (build ≥ 22000) or `windows-10`. The reference prints
  `platform=reference` with its own OS; the app version is the build's (`VERSION`).
- `view`: the History window's range — `today`, `yesterday`, `7d`, `30d`, `90d`, `year`, `all`, `custom` — or `live`.
  `first` and `last`: the resolved range (live: today); `days` = `last − first + 1`. `excluded`: the excluded sources
  in the order of section 1 joined by `,`, else `none`. Source ids are those of section 1.
- Numbers: decimal integers without separators. `<bool>`: `true` or `false`. Dates: `YYYY-MM-DD`.
- A `<text>` is `null` or a double-quoted string (cases `quote`): a lone surrogate becomes U+FFFD; a *path-like*
  value (except `rule`, which comes from the table) becomes `<path>`; more than 120 Unicode scalars keep the first 120
  followed by `…` (U+2026); then `"` and `\` are written `\"` and `\\`, U+0008, U+0009, U+000A, U+000C and U+000D
  `\b`, `\t`, `\n`, `\f` and `\r`, every other character of U+0000–U+001F, U+007F–U+009F, U+2028 and U+2029 `\u`
  with four lower-case hex digits, and everything else as is. *Path-like* (ASCII letters compared in lower case):
  starts with `/`, `\`, `~` or `file:`, or with a letter, `:` and `/` or `\`, or contains `/users/`, `\users\` or
  `/home/`.
- The text names no path, project, agent, session or file and holds no prompt or content: model ids as logged
  (path-like ones redacted; an empty model is `""`, *no model metadata*), providers, the table's rule texts, dates
  and counts.

**Command line.** `reference.py --dump-unpriced [--days N | --first YYYY-MM-DD --last YYYY-MM-DD | --live]
[--source ID] [--prices PATH]` prints the text over every log on this computer: the daily history (8; the reference
keeps no store, so every day is priced with the given table), or with `--live` today's engine responses (the rows of
`--dump-today`). `--days N` (default 30) is the last N local days with today; N = 1, 7, 30 and 90 print the views
`today`, `7d`, `30d` and `90d`, other values `custom`; `--first`/`--last` print `custom`. Both probes take the same
options and print the same text from their own history analysis — written to a store (in memory, or
`--history-store PATH`) and read back through *Reading* — and their own engine; only the `app` line differs.
`--dump-history` rows carry the day's facts as `unpriced` (`[{model, provider, fast, requests}]`, in the fact order
of *Rows*; `[]` when none); `spec/fixtures/history-expected.json` checks them.

**Recovery** (what changes once a missing price or alias is published; issue #20):

- *New requests* are priced with the current table, which the app has as soon as a build bundles it or the daily sync
  fetches it (NETWORK 3). A day's binding (9.7) keeps the prices it was first priced with; a request its bound table
  cannot price takes the current table.
- *Stored history*: when the current table version changes, every file whose stored contribution has unpriced
  requests is analysed again and the newly known prices fill in its days; a price that was known never changes. Days
  whose logs are gone cannot be re-priced: their facts show as `pricedNow`, not as rows. Nothing is estimated: a
  request is priced only through an exact table entry or alias (9.2).
- *Reporting*: reports carry increments only and history is never sent (11). A live response priced later adds its
  cost once to a later report (11.4, while its key is counted); the `unpricedRequests` increment it already added
  stays, as `unpricedRequests` never decreases; a built report is never resent. The receiver's past numbers
  therefore never change, and no request or cost is counted twice.

## 10. Network traffic

Agent Speed also measures network traffic: the whole computer's live upload and download speed, and how many bytes
each source (and, where it can be told apart, each agent) sends and receives. Only byte counts are read — never
content, addresses, ports or host names — and nothing leaves the computer. Display rules are DESIGN 10; the Windows
helper service is `spec/WINDOWS-SERVICE.md`. `spec/tools/traffic.py` implements 10.4–10.9 and 10.11 (10.5 and 10.6
from recorded process tables and agent activity) and checks `spec/fixtures/traffic-cases.json`; both apps run the same
cases through their own sampler-independent core.

### 10.1 Vocabulary

| Term | Meaning |
| --- | --- |
| `rx` / `tx` | Bytes received (download, ↓) / sent (upload, ↑). Unsigned 64-bit counters, deltas and totals in bytes. |
| Machine traffic | Bytes on the computer's physical network interfaces (10.2), all programs together. Includes protocol headers and VPN encryption overhead. |
| Process traffic | Bytes of one process's TCP and UDP sockets that do not go over loopback (10.3), tunnel interfaces included: transport payload, including TLS and QUIC framing, excluding IP/TCP headers. |
| Agent process | A process that runs a source (10.5). Its *tool processes* are all its descendants that are not agent processes themselves. |
| Self / tools | A source's traffic is split into `self` (its agent processes) and `tools` (their tool processes). |
| Agent traffic | Traffic attributed to one agent tree (6.7) by 10.6; best effort. |

### 10.2 Machine counters

Read on every sampling tick (10.4), cumulative 64-bit counters per interface.

- **macOS**: `sysctl {CTL_NET, PF_ROUTE, 0, 0, NET_RT_IFLIST2, 0}`, `RTM_IFINFO2` messages, `if_msghdr2.ifm_data`
  (`struct if_data64`: `ifi_ibytes`, `ifi_obytes`). Not `getifaddrs`, whose `if_data` counters are 32-bit and wrap
  every 4 GiB (34 s at 1 Gbit/s). One call costs ≈ 0.14 ms (measured, 40 interfaces). Since at least macOS 26 the
  values an unprivileged process sees move in steps of 1 KiB (observed on macOS 27.0.1: every counter is a multiple of
  1024), which is below display precision. Counted interfaces: the BSD names of `SCNetworkInterfaceCopyAll()` (the
  hardware ports of System Settings → Network: Wi-Fi, Ethernet, Thunderbolt, USB and iPhone tethering, …) except
  interfaces of type Bridge, Bond and VLAN (their member or parent interfaces already carry the bytes), and only while
  `IFF_UP` and `IFF_RUNNING`. The list is refreshed on a SystemConfiguration network-change notification and every
  60 s. Not counted: `lo0`, `utun*`/`ipsec*`/`ppp*` (VPN tunnels: the tunnelled bytes are counted once, encrypted, on
  the physical interface), `awdl0`/`llw0` (AirDrop and other peer-to-peer links), `bridge*`, `vmenet*`/`feth*` (virtual
  machines, whose internet traffic leaves through a physical interface), `anpi*`, `gif0`, `stf0`.
- **Windows**: `GetIfTable2Ex(MibIfTableNormal)` rows with `InterfaceAndOperStatusFlags.HardwareInterface` set and
  `FilterInterface` and `EndPointInterface` clear, `Type` not `IF_TYPE_SOFTWARE_LOOPBACK` or `IF_TYPE_TUNNEL`,
  `TunnelType == TUNNEL_TYPE_NONE`, `OperStatus == IfOperStatusUp`; `InOctets` / `OutOctets`. This excludes NDIS
  filter-module duplicates of the same adapter, Hyper-V/WSL/VMware virtual adapters, WAN miniports (VPN), Bluetooth
  PAN and Teredo/6to4/IP-HTTPS tunnels (verified against `Get-NetAdapter -IncludeHidden` on the test machine). The
  table decides the counted set; between table reads each tick reads only the counted rows, one `GetIfEntry2` per
  `InterfaceLuid` (10.10). The table is read again every 10 s and as soon as a counted row fails or is no longer up.
- An interface is identified by its BSD name (macOS) or `InterfaceLuid` (Windows).

Machine counters need no permission and work whether or not process traffic is available.

### 10.3 Process counters

- **macOS**: one long-running child process
  `/usr/bin/nettop -P -L 0 -s <interval> -x -n -t external -t undefined -J bytes_in,bytes_out` (interval 10.10). `nettop` ships
  with every macOS since 10.7 and carries Apple's private `com.apple.private.network.statistics` entitlement, which a
  third-party app cannot obtain; it is the only supported way for an unprivileged app to read every process's
  counters. Requirements, all verified on macOS 27.0.1 (M5 Pro):
  - **stdin** must be an open pipe that is never written (or a terminal). With `/dev/null` nettop busy-loops at
    ≈ 120 % of a core.
  - **stdout and stderr** must be a pseudo-terminal (`openpty`), and it must be the child's **controlling terminal**
    (new session, `TIOCSCTTY`). On a pipe, nettop buffers its output until it exits, so no sample arrives while it
    runs; without a controlling terminal, a crashed parent leaves nettop spinning at ≈ 126 % of a core instead of
    receiving SIGHUP.
  - Environment `LC_ALL=C`; the child is in its own process group and is killed with the app.
  - Output: each sample starts with a header line beginning with `,` (`,bytes_in,bytes_out,`); every following line
    until the next header is `<name>.<pid>,<bytes_in>,<bytes_out>,`. The pid is the digits after the last `.` of the
    first field (names are the 15-character kernel name and may contain `.` and spaces; they are never used). Columns
    are located by header name; a line that does not parse is skipped. Values are plain integers (`-x`).
  - Semantics: per-process totals since the process started, including connections that already closed (a process
    that opened three connections one after another showed one growing total), TCP and UDP. An exited process stays
    in the output for 2–3 samples with its final totals, so its last bytes are not lost at 1 Hz.
  - **Interface types** (`-t`): nettop gives every socket the type of the interface it last sent through (the
    kernel's `inp_last_outifp`), not of an interface it is bound to as its help text says: `wifi`, `wired`,
    `loopback`, `awdl`, `expensive`, `undefined` (no outgoing interface yet, or one of no defined type), and
    `external` (every defined type except loopback). Tunnel interfaces (`utun*`, `ipsec*`) have no defined type, so
    the traffic of a program behind a TUN-mode proxy or a VPN is `undefined`, a sibling of `external`, not part of
    it. Several `-t` add up, and nettop lists each socket once even when it matches more than one: a process with
    sockets in both views showed the same total under `-t external -t undefined` as with no `-t`, not the sum of
    the two views. The pair therefore selects every socket except loopback ones. Measured on macOS 27.0.1
    (2026-10-28, issue #35), per-process totals (received + sent) in each view:

    | Transfer | no `-t` | `external` | `undefined` | `loopback` | `external` + `undefined` |
    | --- | --- | --- | --- | --- | --- |
    | `curl` download over Ethernet, 9.0 MB | 9.0 MB | 9.0 MB | 0 | – | 9.0 MB |
    | UDP sent through a tunnel interface (`utun4`), 283 KB | 283 KB | – | 283 KB | – | 283 KB |
    | `curl` through a local HTTP proxy on `127.0.0.1`, 7.0 MB: `curl` | 7.0 MB | – | 0 | 7.0 MB | 0 |
    | the same, the proxy process | 14.0 MB | 7.0 MB | 0 | 7.0 MB | 7.0 MB |

    With `-t external` alone (until 1.4.1) a program behind a TUN-mode proxy or a VPN had no traffic at all. Loopback
    stays excluded (local model servers, MCP servers, an app's own client and server): 40 MB a test process moved
    over `127.0.0.1` were listed neither by `-t external` nor by the pair.
  - **Counted once**: a byte counts for the process whose socket carried it. Behind a TUN or VPN, the agent's own
    socket counts (`undefined`); the tunnel's encrypted packets count for the tunnel's process (a proxy core such as
    mihomo, a VPN app's network extension) or for no process (the kernel's IPsec), which is not an agent process
    and so belongs to no source (10.5), and the machine counters (10.2) count only the physical interface. Through a
    local HTTP or SOCKS proxy, the agent's socket goes only over loopback and is not counted; the proxy's outgoing
    socket counts for the proxy process, which belongs to no source either (10.12). Windows counts the same set: its
    helper drops loopback addresses only, whatever the interface (WINDOWS-SERVICE 3, step 5).
  - `-d` (delta mode) is not used: its first sample is neither a delta nor a total, and deltas are computed by the app
    anyway (10.4). One-shot runs (`-L 1` every second, as some monitors do) cost ≈ 50 ms of CPU each (5 % of a core at
    1 Hz) versus ≈ 16 ms per sample when streaming.
  - Health: no complete sample for 10 s, an exit, or 10 consecutive unparseable samples → restart after 1, 2, 4 … 60 s;
    5 restarts within 10 minutes → `processTraffic = unavailable(sampler)` until the next app launch or a manual retry.
    `/usr/bin/nettop` missing or not executable → `unavailable(sampler)`.
  - The private `NetworkStatistics.framework` (what nettop and Activity Monitor use) is not used directly: private
    API, private entitlement, unversioned kernel structures.
- **Windows**: the traffic helper service (`spec/WINDOWS-SERVICE.md`): per (pid, create time) cumulative `rx`/`tx`
  since the service first saw the process, for the processes of the user's session, plus the service `epoch`.
  Unavailable states: `helperNotInstalled`, `helperStopped` (could not be started), `helperOutdated`, `helperDenied`,
  `helperError`.
- **Process identity** everywhere is (pid, start time): macOS `proc_pidinfo(PROC_PIDTBSDINFO)` `pbi_start_tvsec/usec`;
  Windows `GetProcessTimes` creation time, which equals the helper's `createTime` (both are the kernel's process
  create time). A counter row whose identity does not
  match the app's process table entry for that pid is ignored for that sample.

### 10.4 Sampling and deltas

Sampling runs while `trafficEnabled` is on (DESIGN 10, default on). All times below are monotonic except where a local
date is needed.

1. **Tick**: every 1000 ms the app reads the machine counters (10.2), refreshes its process table (10.5) when 10.10
   says so and applies the newest process-counter sample (nettop's latest complete sample, or a helper snapshot when one
   is due). Sample time = the tick's time.
2. **Delta** of one counter key (interface, or process identity) between consecutive values `prev` → `cur`:
   - `cur ≥ prev` → `d = cur − prev`;
   - `cur < prev` (counter reset: interface re-created, helper restarted, nettop restarted) → `d = 0` and the new value
     becomes the baseline;
   - `d` greater than `12.5 GB/s × Δt` (100 Gbit/s) → treated as a reset (`d = 0`).
3. **First sight** of a key. Interfaces: always a baseline (`d = 0`; an interface that comes up may carry older
   counts). Processes: in the **first sample of a stream** every process is a baseline, so processes that were running
   before sampling started never add a burst. A stream starts at launch, when sampling is turned on, when nettop is
   started again after a failure, and on every new connection to the helper (WINDOWS-SERVICE 4); restarting nettop
   for an interval change (10.10) continues the stream. A process that first appears later in a running stream has
   `d = cur`: nettop lists a process only once it has used the network and the helper only after its first counted
   event, so all of it is new (a short tool run is not lost).
4. **Disappearance**: a key absent from three consecutive samples is forgotten (its last delta was already counted).
5. **Gaps**: when `Δt > 15 s` (sleep, a stalled sampler) the deltas are still added to totals — the bytes are real —
   but not to the live-rate series, and the rate window (10.7) restarts.
6. **Day**: a delta belongs to the local date of its sample time; deltas after local midnight start the new day.
7. **Continuing a stream** across a nettop restart keeps the process baselines: nettop reports totals since process
   start, so the first sample of the new child continues the same counters.

### 10.5 Attribution: processes → sources

The app keeps a **process table** of the current user's processes (on Windows: of the app's session), refreshed on the
ticks 10.10 gives: pid, parent pid, start time,
executable path and, only for script hosts (`node`, `bun`, `deno`, with or without `.exe`), Python hosts (`python`,
`pythonw`, or `python` followed by digits and dots such as `python3` and `python3.14`, compared case-insensitively on
both platforms because macOS framework builds run `Python`; with or without `.exe`) and WorkBuddy's executables
(below), the argument list; on macOS, for WorkBuddy, also the bundle id of the app whose `Contents/MacOS`
holds the executable (`CFBundleIdentifier` of its `Contents/Info.plist`, read once per app path).
macOS: `proc_listallpids`, `proc_pidinfo(PROC_PIDTBSDINFO)`, `proc_pidpath`, `sysctl(KERN_PROCARGS2)`. Windows: one
`NtQuerySystemInformation(SystemSessionProcessInformation)` listing of the session (pid, parent pid, create time; no
handles opened; `SystemProcessInformation` filtered by session where the session listing fails), then, once per new
identity, `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` with `GetProcessTimes` (the handle's create time must match
the listing), `QueryFullProcessImageNameW` and `NtQueryInformationProcess(ProcessCommandLineInformation)` (Windows
8.1+). Path
and arguments are read once per process identity; an exited process stays in the table for 10 s, so the counter rows
that still list it (10.3) can be matched. Paths compare with `/` and `\` equivalent, case-insensitively on Windows;
`~` is the home folder. File **names** compare without the `.exe` extension: case-sensitively on macOS, where the
command-line tools are lower-case (`claude`, `codex`, `opencode`) and the desktop apps' own executables capitalized
(`Claude`, `Codex`, `OpenCode`); case-insensitively on Windows, where an image path has the case the process was
launched with (observed 2026-10-25: Claude Desktop 3.6556 runs `app\claude.exe`, DeepSeek Harness
`DeepSeek Harness.exe`), so Windows tells the desktop apps by location (below), never by case. WorkBuddy is the one
desktop app that runs its agent on its own executable, so its row needs the argument list.

**Agent processes** (first matching row; `name` = executable file name without `.exe`):

| Source | Agent process |
| --- | --- |
| `claudeDesktop` | `name == claude` and the executable lies under Claude Desktop's bundled Claude Code folder (macOS `~/Library/Application Support/Claude/claude-code/`; Windows `%LOCALAPPDATA%\Claude\claude-code\`, `%LOCALAPPDATA%\Claude-3p\claude-code\`, `%APPDATA%\Claude\claude-code\`), or `name == claude` with an ancestor that is the Claude Desktop app (`/Applications/Claude.app/Contents/MacOS/Claude`, bundle id `com.anthropic.claudefordesktop`; Windows `claude.exe` directly in the `app` folder of the `Claude_*` package, or under `%LOCALAPPDATA%\AnthropicClaude`). Observed: macOS `Claude → disclaimer → …/claude-code/2.1.308/<hash>/claude.app/Contents/MacOS/claude`, one per Code session; Windows `Claude.exe → %LOCALAPPDATA%\Claude-3p\claude-code\2.1.299\<hash>\claude.exe`. |
| `claudeCode` | `name == claude` (native installer `~/.local/bin/claude` → `~/.local/share/claude/versions/<version>` — matched by that folder, since the file name is the version —, Homebrew, IDE extensions' `native-binary/claude`), or a script host whose first script argument contains `@anthropic-ai/claude-code/`. Not a process whose arguments contain `--chrome-native-host` (the Claude in Chrome bridge, a child of the browser). |
| `codex` | `name == codex` or `name` starts with `codex-` followed by a target triple or `x86_64`/`aarch64` (npm-vendored binaries), or a script host running `@openai/codex/bin/codex.js`. Covers the CLI, `codex app-server` started by the Codex app (macOS `…/codex-cli/CodexCLI.app/Contents/MacOS/codex`, Windows `%LOCALAPPDATA%\OpenAI\Codex\bin\<hash>\codex.exe`) and IDE extensions, and the app-server daemon (`~/.codex/packages/app-server-daemon/…/codex`). |
| `deepseek` | `name == dsh`; the DeepSeek Harness desktop app's main process (macOS bundle id `com.deepseek.dsh`, `DeepSeek Harness.app/Contents/MacOS/DeepSeek Harness`; Windows `DeepSeek Harness.exe`). The whole app tree counts. Observed on Windows (2026-10-25): the app runs its host (`…/app.asar/dsh/node_modules/@deepseek-ai/dsh-desktop-host/…`) and one runner per tool call (`…/dsh-subprocess-local/lib/runner.js`) on `DeepSeek Harness.exe` itself; the host holds the model connections, the runners start the tools (`powershell.exe`). |
| `opencode` | `name` in `opencode`, `opencode-cli` (desktop sidecar `opencode-cli serve`), or starting with `opencode-` followed by an OS and architecture (npm-vendored); or a script host running `opencode-ai/bin/opencode`. |
| `gemini` | A script host whose arguments contain `/@google/gemini-cli/` (npm, Homebrew `…/libexec/lib/node_modules/@google/gemini-cli/…`), or `name == gemini` for a native build. Gemini CLI relaunches itself as a child; the child is a descendant either way. |
| `omp` | `name == omp`, or a script host whose arguments contain `/@oh-my-pi/` or `/oh-my-pi/`. |
| `antigravity` | `name == agy`, or `name` starts with `language_server_` (the Antigravity language server, started by the CLI, the desktop app and the IDE with `--app_data_dir`). |
| `workbuddy`, `workbuddyAi` | The embedded CodeBuddy Code CLI, which makes the model calls itself: an executable of the edition — macOS: in the app with bundle id `com.tencent.workbuddy.mac` (WorkBuddy) or `com.workbuddy.workbuddy-ai` (WorkBuddy AI), whose main executable is named `Electron`; Windows: `name` `WorkBuddy` or `WorkBuddyAI` — whose arguments after the first contain a path with `/app.asar.unpacked/cli/` (the CLI entry `…/app.asar.unpacked/cli/bin/codebuddy`, run with `ELECTRON_RUN_AS_NODE`). Observed in the 5.8 code: pooled `--prewarm` processes that later serve one conversation each (`--serve --session-id <id>`; the command line does not change when one is taken from the pool), cold `--serve` starts, and the fallback `--acp` with first argument `wb-acp:<id>`. Not verified with a running app; the Windows install folder is unknown, so only the name is used. |
| `pi` | `name == pi` (release binaries), or a script host whose arguments contain `/@earendil-works/pi-coding-agent/` or `/@mariozechner/pi-coding-agent/` (npm, and the installers' `<agent dir>/install/releases/<version>/node_modules/…`, started by the `<agent dir>/bin/pi` launcher). |
| `openclaw` | `name == openclaw` (standalone builds), or a script host whose arguments after the first contain `/openclaw.mjs` (the npm package's `bin`, which may respawn itself with the same script; git checkouts) or `/openclaw/dist/` (services started on `dist/entry.js` or `dist/entry.mjs` of the npm or pnpm package). The Gateway process sends OpenClaw's own model requests. The agents it starts for delegated turns match their own rows and count for their sources: Claude Code and Codex app servers, like their turns (4.14); Gemini CLI too, although its turns count under `openclaw`. The Windows Hub's Gateway runs inside WSL, outside the process table. Not verified with a running OpenClaw; the macOS app's bundled runtime is assumed to run the same `openclaw.mjs`. |
| `hermes` | A Python host whose arguments after the first contain `hermes_bootstrap`, `hermes_cli` or `acp_adapter` (Hermes's launchers run `python -I -c "…import hermes_bootstrap…"`; the desktop app, the TUI's local gateway and the gateway services run `-m hermes_cli.main` or the same bootstrap; source checkouts `hermes_cli/main.py`), or one with an argument whose file name without `.exe` is `hermes`, `hermes-agent` or `hermes-acp` (pip and uv console scripts `bin/hermes`, `Scripts\hermes.exe`, and Hermes's Windows launcher `.hermes\bin\hermes.exe`, which runs Python on its own path). The Python process sends Hermes's model requests; a launcher process sends nothing and is not a row, since `hermes` is also the name of the Hermes JavaScript engine's command-line tool. Not verified with a running Hermes. |
| `droid` | `name == droid`: droid is one native executable (built with Bun), installed as `~/.local/bin/droid` (the install script), `%USERPROFILE%\bin\droid.exe` (the PowerShell installer), by the Homebrew cask `droid`, or as the npm package `droid`'s platform binary (`@factory/cli-<os>-<arch>[-baseline]/bin/droid`; on Windows a Node shim, which is not a row and sends nothing, starts `droid.exe`). The interactive CLI starts a second `droid` (`droid exec --input-format stream-jsonrpc …`), which counts as part of it; Factory Desktop runs `droid daemon` and `droid exec` children from its bundle (Windows `…\resources\bin\droid.exe`), and the app's own processes are app processes like the other desktop apps'. From the installers, the Homebrew cask, the npm package and Factory's issue reports; not verified with a running droid. |

The desktop apps' own processes are **not** agent processes, nor are their Electron or Tauri helpers: macOS
`/Applications/{Claude,ChatGPT,Codex,OpenCode,Antigravity}.app/Contents/MacOS/<name>`; Windows executables directly in
the `app` folder of the `WindowsApps\Claude_*`, `WindowsApps\OpenAI.Codex_*` and `WindowsApps\OpenAI.ChatGPT*`
packages (`app\claude.exe`, `app\Codex.exe`; the tools a package bundles deeper follow the rows, so
`app\resources\codex.exe` is a Codex agent) and everything under `%LOCALAPPDATA%\AnthropicClaude\`. Their own traffic (chat UI, updates, telemetry) is outside the sources' scope, like
Claude Desktop chats are in section 2; only the agent processes they start count. DeepSeek Harness is the exception
above. WorkBuddy's main process, its helpers, renderers and its daemon (the same executable running
`app.asar/main/daemon-app-server-entry.js`, which starts the CLI processes) are app processes like these, so the
CLI processes are roots and their children are their tools.

**Ancestry**: a process's parent is the table entry with its parent pid whose start time is not later than its own
(otherwise the link is broken: the pid was reused). The **nearest** ancestor-or-self that is an agent process decides:

- the process itself matches a row and has no agent ancestor of the same source → it is an agent process (a root);
- it matches a row but its nearest agent ancestor is of the same source → it is part of that agent process (a
  wrapper's native child, Gemini's relaunch): counted as `self` of the ancestor's root;
- it matches a row of a different source → it is an agent process of its own source (an agent run as another agent's
  tool counts for itself);
- otherwise, if it has an agent ancestor → a tool process of the nearest one (`tools`);
- otherwise → not attributed (it is still part of machine traffic).

Attribution is decided when a process is first seen and kept for its lifetime: a tool whose parent exits (reparented
to launchd or orphaned) stays with its agent. A process that detaches before the first tick that sees it cannot be
linked (known approximation, 10.12).

`includeTools` (DESIGN 10, default on) chooses whether displayed source and agent values include `tools`; totals and
history always keep `self` and `tools` separately, so the setting changes no stored data.

**Disabled sources** (Settings → Data Sources, DESIGN 5) are attributed, counted and stored like the others: bytes,
unlike transcripts, cannot be read again later, so a source turned back on shows its traffic for the days it was off,
as it shows its tokens. Every view leaves a disabled source out (DESIGN 10) — its live rates, today's bytes, its
sparkline and its history rows, and every sum shown over sources (orbs, menu bar and taskbar, dashboard, history
chart, table and CSV) — so it shows no traffic anywhere, as it shows no tokens. This computer's traffic (10.2) is
unaffected.

### 10.6 Attribution: agent processes → agents (best effort)

Per-agent traffic attaches to **agent trees** (the roots of 6.7 with their sub-agents: sub-agents run inside their
parent's process). Each tick, for every root agent process `P` of source `s` with a non-zero delta (self or tools):

1. **Candidates**: the agent trees of source `s` that are *active* during the tick interval: some member is in phase
   `generating` or `tools` (after the 6.7 staleness rule), or has `lastLineMs ≥ intervalStart − 2000`.
2. **Open-file evidence** (macOS only: `proc_pidinfo(PROC_PIDLISTFDS)` and `PROC_PIDFDVNODEPATHINFO`, refreshed every
   10 s for agent processes): if `P` or a process counted as part of it (10.5, `self`) has the transcript (or
   database) of a member of one or more candidates open, keep only those. Codex app servers keep their rollout files
   open (observed: two `rollout-….jsonl` held by one app server); Claude Code, Gemini CLI and omp do not.
3. **Working-directory evidence**: if `P`'s working directory is known and some candidates' root `cwdPath` (below)
   equals it, keep only those. macOS: `proc_pidinfo(PROC_PIDVNODEPATHINFO)` `pvi_cdir`, read once per process
   (observed: Claude Code and Claude Desktop's Code-session CLIs run in the project folder; Codex app servers in `/`).
   Windows: optional — `NtQueryInformationProcess(ProcessBasicInformation)` and `ReadProcessMemory` of
   `RTL_USER_PROCESS_PARAMETERS.CurrentDirectory` (same-user processes only; an undocumented but long-stable layout);
   when not implemented or not readable the directory is unknown and this step is skipped.
4. Each step applies only if it leaves at least one candidate. If exactly **one** candidate remains, the interval's
   deltas of `P` and its tools belong to that agent tree; otherwise they stay with the source only (counted in the
   source's `unassigned` share).

`cwdPath` is new parser metadata (section 4): the full working directory the transcript names — Claude: the latest
`cwd`; Codex: `session_meta`/`turn_context` `cwd`; dsh: header `cwd`; OpenCode: `session_v2.directory`; Gemini CLI: the
`.project_root` content; omp: header `cwd`; Antigravity: the decoded path of the first workspace URI; WorkBuddy: the
latest `cwd`; Pi and OpenClaw: header `cwd`; Factory Droid: `session_start.cwd`. Null when absent.
It is metadata only (no effect on tokens or history).

Typical outcomes: one Claude Code session per terminal → exact; several Claude Desktop Code sessions (one CLI process
each, own project folder) → exact; two sessions of one tool in the same folder working at the same time → source
only; a Codex app server, OpenCode server or Antigravity language server serving several conversations → exact while
one conversation is active, else source only; WorkBuddy (one CLI process per conversation, but a pooled process starts
before its conversation, so its working directory need not match) → exact while one conversation is active, else
source only.

### 10.7 Live rates

- For a series of deltas (machine; each source with self and tools; each agent tree), the live rate is
  `Σ deltas of the samples in (t − 3000 ms, t] ÷ ((t − t₀) / 1000)`, where `t` is the latest sample time and `t₀` the
  latest sample time ≤ `t − 3000` (or the first sample of the series when none). With 1 Hz sampling this is the mean of
  the last three intervals — the same 3-second window as the token live rate (6.3). Before two samples exist the rate
  is unknown (null). Separate for `rx` and `tx`.
- Values update once per sample (1 Hz); UIs never interpolate between samples.
- **History buffer**: one-second buckets for the last 300 s (as 6.5), per direction, for the machine and per source
  (self + tools), for sparklines. A bucket holds the bytes of the sample whose interval ends in it ÷ interval seconds.

### 10.8 Today

`todayStartMs` as 6.6. Sums of deltas whose sample time is today:

- `machine`: `rx`, `tx`, `sampledMs` (sum of intervals ≤ 15 s between consecutive machine samples).
- per source: `rxSelf`, `txSelf`, `rxTools`, `txTools`, `unassignedRx`, `unassignedTx` (the part, self and tools,
  not attributed to an agent tree), and `processSampledMs` (sum of intervals ≤ 15 s between consecutive process
  samples of one stream; the first sample of a stream adds nothing).
- per agent tree (memory only, not stored): `rxSelf`, `txSelf`, `rxTools`, `txTools` attributed today while running.
- On day change everything restarts at zero; a relaunch restores machine and source values from the history store
  (10.9), agent values start at zero.
- Kept for every source, enabled or not; views show and sum only the enabled ones (10.5).

### 10.9 Daily history

Traffic is observed, not read from logs: there is no history before sampling started, and it cannot be recomputed.

- **Store**: a new table in the history database (section 8), created if missing every time the database is opened (the schema
  version does **not** change, so older builds can still open the file; the analysis version does not change either,
  so nothing is re-analysed):
  `day_traffic(day TEXT, key TEXT, rx INTEGER, tx INTEGER, rx_tools INTEGER, tx_tools INTEGER, sampled_ms INTEGER,
  PRIMARY KEY (day, key)) WITHOUT ROWID`. `key` is a source (section 1), whose `rx`/`tx` are `self` and
  `rx_tools`/`tx_tools` are `tools`, or `machine` (`rx_tools` and `tx_tools` are 0). `sampled_ms` is the machine
  `sampledMs` on the `machine` row and `processSampledMs` on source rows. The meta key `trafficSince` holds the first
  local date with a stored sample.
- **Writes**: today's in-memory totals are written (upsert) every 60 s, at the day change, before sleep and at quit;
  a crash loses at most 60 s. Readers merge the unsaved part, so today's dashboard values equal today's history row.
- **Recomputation and clearing**: section 8's version bumps and full recomputations never touch `day_traffic` (like
  `day_peaks`). "Clear history" deletes it with everything else (DESIGN 10 says so in the confirmation). Older builds
  ignore the table.
- **Rows shown**: a date before `trafficSince` has no traffic data (UIs show it as not recorded, never 0); a date with
  `sampled_ms` below its elapsed time is partial (UIs say how long it was recorded).
- CSV export columns: DESIGN 10.

### 10.10 Sampling intervals and CPU budget

- Machine counters: every tick (1 Hz) while traffic sampling is on. Cost: ≤ 0.02 % of one core. macOS reads every
  interface in one `sysctl` (10.2). Windows reads each counted interface by its `InterfaceLuid` (`GetIfEntry2`: ≈ 0.2 ms
  wall, no measurable CPU) and reads the whole table (`GetIfTable2Ex`) only every 10 s and when a counted row fails or
  goes down: the table call asks every adapter and NDIS filter driver for its statistics, ≈ 0.8 ms of CPU and ≈ 100 ms
  wall per call (measured on the test machine, 47 rows), so reading it every second alone would cost 0.08 % of one
  core, four times the budget; every 10 s it costs 0.008 %. A new interface (Wi-Fi joined, tethering) is counted within
  10 s.
- Process counters (macOS nettop child), measured on the M5 Pro with ≈ 760 open sockets and `-t external` alone:
  ≈ 1.6 % of one core at `-s 1` (≈ 15 ms per sample plus ≈ 0.25 % fixed), ≈ 0.5 % at `-s 5`, 1.0 % at `-s 2`;
  memory 5 MB. Adding `-t undefined` (10.3) makes nettop follow ≈ 40 % more sockets (≈ 310 instead of ≈ 215 of
  ≈ 335 open; the added ones are mostly idle UDP sockets and listeners) and costs ≈ 40 % more. Measured 2026-10-28,
  both invocations side by side through a pseudo-terminal: 0.88 % → 1.22 % of one core at `-s 1` (180 s; four
  earlier runs: +0.37 to +0.52 points), 0.38 % → 0.59 % at `-s 5` (120 s). `-c` saves nothing measurable; `-m tcp`
  would cost less than `-t external` alone but would drop QUIC and other UDP traffic, so it is not used. To stay
  near the app's budget the interval adapts:
  - no agent process in the process table → nettop is not running (0 %);
  - agent processes exist, but no agent is active (10.6) for 60 s and no visible surface shows per-source or per-agent
    traffic → `-s 5`;
  - otherwise → `-s 1`.
  An interval change restarts nettop (≈ 50 ms of CPU) and happens at most once per 30 s; bytes are never lost by it
  (10.4.7), only the rate resolution changes.
- Windows helper: ≈ 0.1 % of one core at normal desktop traffic, < 1 % at 1 Gbit/s (WINDOWS-SERVICE 3). It counts every
  event and has no interval; the same three states decide how often the app asks it for a snapshot: never (the
  connection closes and the service stops itself after 120 s), every 5 s, or every second. Its counters are cumulative,
  so a slower poll loses no bytes, only rate resolution.
- App side (process table, parsing, attribution): ≤ 0.2 % of one core at 1 Hz with 800 processes; path and argument
  reads only for new processes. macOS lists every tick, and every 2 s while nettop is not running (only to notice agent
  processes).
- Windows process table: one listing of the session costs ≈ 1.6 ms of CPU for 143 processes (≈ 4.7 ms for all 273
  processes of the machine; measured on the test machine), so listing every second would take most of the app-side
  budget by itself. The table therefore matters only while per-process counters can arrive, and is listed:
  - never while the helper is not installed, outdated or refused (its state is checked again every 30 s): nothing
    could be attributed, and the first sample of the next stream is a baseline (10.4.3), so nothing is lost;
  - every 2 s while no agent process exists (the helper is not asked), only to notice agent processes;
  - every tick while an agent is active (10.6) or was within the last 60 s: its tool processes come and go then;
  - otherwise every 5 s, and in the same tick whenever a counter row names a process the table does not know yet. A
    process's first row arrives in the tick it first uses the network, so it is listed while it still runs, as a 1 Hz
    listing would find it; rows of processes that had already exited when listed do not ask again.
  With a typical session this keeps the listing at ≈ 0.16 % of one core while an agent is active and ≤ 0.08 %
  otherwise, within the app-side budget; it is not needed at all for the machine counters.
- Benchmark harness (both platforms): 10 minutes idle with agents running, 10 minutes with a 100 MB download in a tool
  process; report average CPU of the app plus nettop or the helper.

### 10.11 Byte formatting (both platforms)

Decimal units (1 KB = 1000 B), as macOS and network speeds use. Rates: `B/s`, `KB/s`, `MB/s`, `GB/s`; totals: `B`,
`KB`, `MB`, `GB`, `TB`. With the bits unit (DESIGN 10) rates are `× 8` with `b/s`, `Kb/s`, `Mb/s`, `Gb/s`.

| Value `x` in the unit | Display |
| --- | --- |
| bytes `< 1000` | integer (`0 B/s`, `512 B`) |
| `< 10` | two decimals (`1.28 KB/s`) |
| `< 100` | one decimal (`12.8 MB`) |
| `< 1000` | integer (`128 KB/s`) |

A value that rounds to 1000 in its unit moves to the next unit (`999.6 KB/s` → `1.00 MB/s`; `999.5 B` → `1.00 KB`);
the largest unit keeps larger integers (`5000 TB`).
Rounding is half-up on the decimal value. Number and unit are separated by a no-break space; units are never
translated. Cases: `spec/fixtures/traffic-cases.json` `format`.

### 10.12 Known approximations

- Machine traffic counts link-layer bytes (headers, retransmissions, VPN overhead, other programs, other users);
  process traffic counts socket payload, so the same transfer adds slightly less to its source than to the machine.
  The two are sampled at slightly different moments, so per-second values may briefly disagree.
- Child processes that detach before the first 1 Hz process-table refresh, or that are started on the agent's behalf
  by another process (a system service, Codex's Windows sandbox service `codex-windows-sandbox-service.exe`, an IDE's
  integrated terminal for Antigravity IDE commands, a browser opened by URL), are not attributed to the agent.
- Traffic of a remote machine an agent works on (SSH sessions are counted; the remote's own downloads are not).
- Per-agent attribution falls back to the source whenever more than one candidate is active (10.6).
- Windows counts start when the helper first sees a process; traffic of an agent process before the helper started is
  not counted (it is a baseline, 10.4.3).
- macOS unprivileged interface counters move in 1 KiB steps.
- Loopback traffic (local model servers such as Ollama or LM Studio, MCP servers on localhost, OpenCode's own client and
  server) is excluded everywhere. So is an agent's traffic through a **local HTTP or SOCKS proxy** (`HTTPS_PROXY` or
  a system proxy on `127.0.0.1`): the agent's socket only reaches the proxy over loopback, and the proxy's own process
  is not an agent's. Behind a TUN-mode proxy or a VPN the agent's traffic counts (10.3).
- An agent that starts a TUN-mode tunnel as its own tool (a user-space VPN client) and sends through it counts the
  same bytes twice: once on its own tunnel socket (`self`) and once, encrypted, on the tool's socket (`tools`). A
  proxy started as a tool counts once (as `tools`): the agent reaches it over loopback.
- WorkBuddy's app processes (chat UI, cloud conversations, sync) are not counted; its CLI processes are recognized by
  rules derived from the 5.8 code, not yet from a running app.

## 11. Live reporting

Execution modes are additive metadata specified in [EXECUTION-MODES.md](EXECUTION-MODES.md); they do not change
any token, request, speed, duration or price formula. Optional per-model Harness/mode breakdowns and separate
profile APIs are defined in [REPORTING-EXTENSIONS.md](REPORTING-EXTENSIONS.md).

When the user turns on live reporting (DESIGN 11), the app sends the values below to a service of the user's choice
with the protocol of `spec/REPORTING.md`. This section defines how every reported value comes from the engine, the
*report ledger* that makes deltas exact, and the report scheduler. `spec/tools/report_reference.py` implements 11.5 as
a simulator and checks `spec/fixtures/report-vectors.json` → `scheduler`; both apps run the same cases through their
own scheduler core with a fake clock and instant sends. Only realtime values are reported: nothing of sections 6.6
(today), 6.10 (signals), 6.11 (task records, predictions, calibration, comparison), 8 (daily history), 9.6–9.7 or
10.8–10.9 is ever sent. The task score of 6.9 is sent, as live values and as counts of tasks completed while reporting
is on, only by the `taskScore` extension with the `models` section (11.10).

### 11.1 Scope and clock

- **Sources**: the enabled sources, all together. Never an orb's subset (6.8) and never filtered by subscription marks
  (9.4): cost is reported for every source.
- **Sections**: an optional section is built only when the service lists it and the user's switch is on (REPORTING
  6.1). Default switches (DESIGN 11): `sources`, `models`, `cost`, `traffic`, all on. A section that
  is off is left out of the report.
- **Extensions** of REPORTING-EXTENSIONS add fields when the confirmed document advertises them; the per-model ones
  (Harness counts, execution modes, the task score of 11.10) ride with the `models` section and its switch.
- **Build time** `t` is an engine tick (6: every 500 ms) with `nowMs = t`. Durations (`windowMs`, holds, retries) use
  the monotonic clock. `sentAt` is the wall clock at `t` plus the session's clock correction (11.6), formatted
  `YYYY-MM-DDTHH:MM:SS.sssZ` in UTC.
- **Order within a tick**: first the gap rule (11.5 step 2) may discard the ledger; then the engine applies the tick's
  file changes and traffic sample and the ledger (11.4) receives that tick's increments; then the rest of the
  scheduler (11.5) runs. Increments of the tick that ends a gap therefore belong to the new window.

### 11.2 Instantaneous values (at `t`)

| Report field | Computation |
| --- | --- |
| `live.outputTokS`, `live.totalTokS` | 6.3 `live.output`, `live.total` at `nowMs = t` |
| `live.estimatedShare` | 6.3 `liveEstimatedShare` |
| `live.agents.generating`, `.tools` | agents of the 6.7 base set (steps 1–2: staleness applied, no ancestor closure) in phase `generating` / `tools` |
| `live.agents.active` | the size of that base set |
| `peak.outputTokS`, `peak.totalTokS` | the maximum of 6.3 `live.output` / `live.total` over the ticks in `(windowStart, t]` (so never below the values at `t`) |
| `title.metric`, `title.redline` | the General speed metric and its redline `R` (DESIGN 1), never an orb's |
| `title.level` | the *report title tracker*: the DESIGN 1 title tracker (levels 0–13, entered at the threshold, left at 90 % of it, 0.8 s hold) fed with the live value of `title.metric` and `R` on every tick while a run is active; it starts at 0 when the run starts. An implementation may reuse the dashboard hero's tracker only when that tracker runs on every tick with the same inputs. |
| `title.heat` | `min(1, (v / R)^0.6)` with `v` the unrounded live value of `title.metric`; not smoothed (no spring) |
| `cost.liveMicroUsdPerSecond`, `cost.knownShare` | 9.5 `liveCost` and `liveCostKnownShare` over all enabled sources |
| `cost.peakMicroUsdPerSecond` | the maximum of 9.5 `liveCost` over the ticks in `(windowStart, t]` |
| `cost.priceVersion` | the table version bound to the local date of `t` (9.7), or the current version when that date is not bound yet |
| `traffic.*BytesPerSecond` | 10.7 live rates of the latest sample at or before `t`: Σ over sources of `self + tools` (`self` only when `includeTools` is off); `null` while that series has fewer than two samples |
| `traffic.peak*BytesPerSecond` | the maximum of those rates over the samples in `(windowStart, t]`; `null` when there is none |
| `traffic.status` | `off` when `trafficEnabled` is off (DESIGN 10); `unavailable` when process traffic is unavailable (10.3 states); else `ok` |
| `traffic.includesTools` | the `includeTools` setting |

Per source (`sources[]`) every value is the same computation restricted to the source (6.3 and 9.5 per source; 10.7
per source series). Per model (`models[].generating`): agents counted in `live.agents.generating` grouped by the
resolved id (11.3) of the agent's current model (the model the agents list shows for it).

### 11.3 Model ids

`models[].id` is the 9.2 resolution of a response's (or agent's) `model` and `provider`: a hit on a price table entry
(priced or free) → that entry's `id`; free by `freeProviders` (step 1) → `local`; everything else (missing model,
`unknown`, a free suffix without an entry) → `unknown`. The raw model string never leaves the app. Entries are grouped
by id and sorted by `output` descending, then `id`. With more than `maxModels` (discovery, ≤ 16) entries the first
`maxModels − 1` are kept and the rest are merged into `other` (fields summed, `sources` united, placed last).
`maxModels = 0` means the section is not sent. With the `taskScore` extension, more ids get entries and the merged
`other` entry also merges the task fields (11.10).

### 11.4 Report ledger

The ledger holds the increments since the last build (or discard) of a report, in total, per source and per model.

**Counting a response.** A response key (3.4, 4.4) becomes *counted* at its first emission when all hold: the engine
is running live (not seeding, 6.4), `endMs ≥ nowMs − 120 000` (the `responseCompleted` rule of 6.4), and its source is
enabled. Counting adds to the ledger: `requests` + 1; `output`, `input`, `cachedInput`, `cacheWriteInput`,
`reasoning`; `measuredOutput` = `output` and `measuredMs` = `endMs − startMs'` (3.3) when the response is measured,
else 0; `costMicroUsd` (9.3) when known, else `unpricedRequests` + 1. A key that was not counted at its first emission
is never counted later (it is history).

**Growth of a counted response.** For every counted key the ledger keeps the values it already added (`reported_k`,
field by field: the token fields, `measuredOutput`, `measuredMs`, `costMicroUsd`). When an upsert (4.4) or a
re-pricing (9.7) changes the response, each field adds `max(0, new − reported_k)` and `reported_k` becomes
`max(reported_k, new)`. So a streaming Claude message adds its growth where it is seen, a response that becomes
measured (its `stop_reason` arrives) adds its measured output and duration then, and a response priced later adds its
cost then; `unpricedRequests` never decreases (REPORTING 6.6.1). Counted keys are kept while their response is in the
engine's retention (6.1, by its latest `endMs`), at most 65 536 (FIFO); a change after that is not reported.

**Attribution.** Each increment also goes to the response's source and to the resolved model id (11.3) of the
response's model at that moment.

**Traffic.** Every sample delta of 10.4 adds to the ledger at its sample time — per source `self` and `tools`; the
machine series (10.2) is never reported — except deltas of a sample whose interval is longer than 15 s (10.4.5 gap: those bytes are older than the
window) and the baseline of a stream's first sample (which adds nothing anyway). Agent traffic = Σ over sources of
`self + tools` (`self` only when `includeTools` is off). A source the user disabled (Settings → Data Sources) reports
no traffic — no rate and no delta — like every view of it (10.5); its bytes keep counting underneath, so the deltas
after it is enabled again start from the latest report, never from before it was disabled.

**Exactly once.** Every increment is applied once, at the tick where the engine produces it. *Build* (11.5) takes the
whole ledger into a report and empties it in one step; *discard* empties it without a report. Every increment
therefore ends up in exactly one built report or is discarded — never in two reports. The deltas of a report that is
built but not delivered are **lost**: they are never resent and never added to a later report (REPORTING 6.2). Tests
check: Σ deltas of built reports + Σ discarded = Σ increments, and the windows of built reports are disjoint.

### 11.5 Report scheduler

Limits come from the discovery document (REPORTING 3.2, clamped to its ranges): `I` = `intervalSeconds` (or the
`intervalSeconds` of the latest success, 5–300), `H` = `heartbeatSeconds` (≥ `I`), `A` = `maxAgeSeconds`. Fixed:
`HOLD` = 15 s, `QUIET` = 10 min, `GAP` = 15 s, `RETRY` = 2 s.

**Activity.** A tick is *active* when an agent of an enabled source is in phase `generating` or `tools` (after the 6.7
staleness rule), or `live.output > 0`, or the ledger received a token increment at that tick. `lastActive` = the
latest active tick of the run (none at its start).

**Mode** at tick `t`: `active` when `t − lastActive < HOLD`; `idle` when `t − lastActive < QUIET`; otherwise (and
before any activity in the run) `quiet`.

**Ready** = reporting on, the service confirmed (REPORTING 3.3) and registered (REPORTING 4.1), the network master
switch on (NETWORK 1), not paused, not inside a backoff or `Retry-After` wait, and no error that needs the user
(REPORTING 8.3).

**Run.** A run starts when reporting becomes ready for the first time after a launch (after seeding has finished), after
turning reporting on, after switching services, and after `seq_conflict`: a new random `run.id`, `seq = 0`, an empty
ledger, `windowStart = t`, no `lastActive`, the report title tracker at 0. Pausing, the network switch and backoff do
not start a new run.

**Each tick `t`**, in this order:

1. User events of this tick: pause, resume, network switch, turning reporting off, quitting.
2. **Gap**: when `t − previous tick > GAP` (sleep, a stalled engine), discard (empty the ledger, `windowStart = t`)
   before this tick's increments are added (11.1).
3. Update `lastActive` and the mode.
4. **Leaving on a user action**: when this tick pauses or turns off reporting or quits the app, the previous mode was
   not `quiet`, and nothing but a backoff kept the scheduler from being ready before this tick's event (the network
   switch is on, no `Retry-After` wait is in effect, the service is confirmed and registered, no error needs the
   user), build a `leaving` report and make one attempt (no retry; at quit wait at most 1 s). A waiting report and an
   in-flight report waiting for its retry are lost. During a `Retry-After` wait nothing is built or sent, not even
   this report (REPORTING 7.2); a backoff does not stop it.
5. **Not ready** → discard; stop here.
6. **Quiet**: on the tick the mode becomes `quiet` (the previous mode was `active` or `idle`), build a `leaving`
   report; on later quiet ticks discard.
7. **Due**: otherwise, when `t − windowStart ≥ I` (mode `active`) or `≥ H` (mode `idle`), build a report with
   `activity` = the mode.
8. **Deliver** (below).

**Build**: `seq + 1`; the report takes the ledger and the instantaneous values at `t` (with the `taskScore`
extension also the task ledger, 11.10, which discards leave alone); `windowMs = t − windowStart`;
then the ledger is empty and `windowStart = t`. A built report goes to the one-slot outbox; a report already waiting
there is lost (the newer one replaces it), and an in-flight report that is waiting for its retry is lost too (newest
wins).

**Deliver**: at most one request in flight. When nothing is in flight the outbox report is sent at once. An attempt
signs with a fresh timestamp and nonce and sends the report's fixed body. A report whose `t − builtAt > A` is lost
instead of sent. Outcomes:

| Outcome | Effect |
| --- | --- |
| `2xx` | delivered; `failures = 0`; adopt `intervalSeconds` when present |
| network error, timeout, `3xx`, `5xx` without `Retry-After` | retry once at `t + RETRY` if `t + RETRY − builtAt ≤ A`; otherwise (or when the retry fails too) lost |
| `429`, or `503` with `Retry-After` | lost; not ready until `t + Retry-After` (5 s – 1 h; 60 s when a `429` has none) |
| `401 timestamp_out_of_window` | correct the clock (11.6); the report is lost (its `sentAt` is on the old clock) |
| `401 nonce_replayed` | attempt again at once with a new nonce (once) |
| `401 signature_invalid` with a pending rotated key (REPORTING 4.5) | attempt again at once signed with the pending key (once) |
| any other error | lost; the reaction of REPORTING 8.3 (re-register, start a new run, or not ready until the user acts) |

**Backoff**: every lost report increments `failures`; when `failures ≥ 2` the scheduler is not ready for
`min(300 s, 5 s × 2^(failures − 2))` × a random factor in `[0.8, 1.2]` (the fixtures use 1). A delivered report resets
`failures` to 0.

**Rate guarantees** (what services can rely on): at most one request in flight; two consecutive built reports of a run
are at least `I` apart (at least `H` while idle), except that a `leaving` report may follow at any time; at most one
transient retry per report; never a report older than `A`; no queue on disk and nothing replayed after a relaunch.
(`windowStart` only moves forward from the last build or discard, so every path back to *ready* — resume, network on,
end of a backoff — waits at least `I` after the previous report.)

### 11.6 Time, midnight, restarts and sleep

- **Clock correction**: on `401 timestamp_out_of_window` with `serverTime`, `correction = serverTime − local Unix
  seconds` when `|correction| ≤ 24 h`; it applies to `X-AgentSpeed-Timestamp` and `sentAt` of every later report of
  the session and is discarded at quit. The rejected report itself is lost (11.5).
- **Local midnight** has no effect: deltas belong to windows, not to days, and today's totals (6.6) are never sent. A
  window that contains midnight is reported like any other; services assign days themselves (REPORTING 6.10).
- **Relaunch**: a new run (11.5). Responses read during seeding are never counted (11.4), so nothing that happened
  before the run is reported; the ledger is never persisted.
- **Sleep**: the gap rule (11.5 step 2) discards the window that contains the sleep; traffic gap deltas are excluded
  (11.4).
- **Wall-clock changes** do not affect windows (monotonic); `sentAt` may jump with the clock. Services order by
  `run.seq` and their own receive time.

### 11.7 Rounding and size

- Rounding is half away from zero (all values are ≥ 0): tok/s to 0.1, shares and heat to 0.001, bytes per second and
  micro-USD per second to integers. Token, byte and micro-USD deltas are exact integers. Every value is rounded on its
  own, so rounded per-source values may differ from the rounded total by the rounding.
- `sources[]` lists, in the source order of section 1, every source with activity (REPORTING 6.6.3): some number of
  its entry as sent — rounded, `null` as 0 — other than `agents.active`, `live.estimatedShare` and `cost.knownShare`
  is above 0. A source whose only increment in the window is cost (a counted response priced only now, 11.4) is
  listed, so the per-source cost sums add up. `models[]` lists every resolved id (11.3) that received a ledger
  increment in the window, a cost increment included, or that an agent is generating with at `t`; with the
  `taskScore` extension also the ids of 11.10 *Membership*.
- The body stays within `min(16384, maxBodyBytes)` bytes: when it would not, drop `models`, then `sources`, for that
  report only (11.10 *Size* for the `taskScore` fields).

### 11.8 Fixtures

`spec/fixtures/report-vectors.json` → `scheduler`: each case gives tick ranges in 500 ms steps (a jump between two
ranges is a sleep), activity ranges, output increments (`from`, `to`, `step`, `output`), user events
(`pause`, `resume`, `networkOff`, `networkOn`) and per-`seq` attempt outcomes (`ok`, `fail`, `rateLimited:<s>`;
default `ok`). `expect.delivered` lists `[seq, endMs, windowMs, activity, output]` of every delivered report and
`expect.lost` the lost `seq`s. The expected values were written by hand from 11.5 and the simulator must reproduce
them. The `taskScore` extension has its own vectors (11.10 *Fixtures*).

### 11.9 WebSocket transport

The user picks the transport of a service (DESIGN 11): **WebSocket** (default) or **HTTP** (11.5). The **effective**
transport is WebSocket when it is chosen and available (below), otherwise HTTP. With WebSocket the client follows the WebSocket extension of the protocol (`spec/REPORTING-WEBSOCKET.md`, kept with the partner
documents): reports are built when data changes, sent over one long-lived connection and acknowledged one by one. The
report body is the v1 body of 11.2–11.7 with one added object, `realtime` (below). Nothing of 11.5 (interval, retry,
outbox, backoff) applies to this transport; this section replaces it. `spec/tools/report_websocket.py` implements
this section as a second simulator; `spec/tools/report_reference.py` writes and checks
`spec/fixtures/report-vectors.json` → `websocket` with it; both apps run the same cases through their own stream
scheduler.

**Availability.** The transport is available when the confirmed document has `extensions.reportWebSocket` equal to
the JSON integer `1` and a valid `websocket` object: `path` exactly `/v1/reports/ws`, `subprotocol` exactly
`agent-speed-report.v1`, and the optional integers `minIntervalMs` 10–1000 (default 50), `maxInFlight` 1–64 (8),
`ackTimeoutMs` 500–10 000 (2000), `maxMessageBytes` 32 768–65 536 (65 536), `pingIntervalSeconds` 10–60 (20),
`pongTimeoutSeconds` 5–30 (10) and below `pingIntervalSeconds`. A field that is present with another type or out of
its range makes the whole object invalid (never clamped, unlike `limits`); unknown fields are ignored; an invalid
object or another extension value leaves the HTTP document valid. Unavailable while WebSocket is chosen: reports go
over HTTP (11.5) until a confirmed document offers the transport again; the choice is kept. A change of the effective
transport — the user's choice, or a stored or refreshed document that adds or drops the capability — is a change of
transport: the old run stops and a new one starts on the new transport (below). A `stop` of this transport never
falls back to HTTP; it waits for the user. The connection URL is `api` with `https` → `wss` (`http`
→ `ws` only for the loopback test service of REPORTING 2.2), authority and base path kept, plus `path`. A change of
these settings in a refreshed document applies at the next connection.

**Connection.** One connection at a time, opened with the v1 signature headers over `GET`, the URL path and an empty
body (11.6 clock correction applies), the API key when set, `Sec-WebSocket-Protocol: agent-speed-report.v1`, no
compression extension, no cookies, no authentication challenge answered, no redirect followed. The `101` response
must select that subprotocol, else the connection is closed and counts as `close 1002`. At most 10 s from `connect`
to `101` and 10 s from `101` to a valid `ready`; then `abort` and a failure. While open the client sends a Ping every
`pingIntervalSeconds` and aborts with a failure when no Pong arrives within `pongTimeoutSeconds` (the libraries answer
the service's Pings themselves; this bounds the "two Ping intervals without a frame" rule). `abort` drops the socket
without a close handshake; `close 1000` sends a close frame.

**Messages.** Each report is one text message `{"type":"report","headers":{…},"body":"…"}`: the four
`X-AgentSpeed-*` headers of a v1 signature over `POST`, the `api` base path + `/v1/reports` and the body bytes (a
fresh nonce per report), and `body` the compact body as a JSON string; the whole message stays within
`maxMessageBytes` and the body within 11.7 (drop `models`, then `sources`; a message still too large is dropped and
counted as `stale`). The body string is written with `"` and `\` escaped, control characters as `\b \f \n \r \t`
or `\u00xx`, and everything else as is (`/` and non-ASCII included). Received messages are parsed strictly (UTF-8
text, one JSON object, no duplicate member names, no lone surrogates, no NaN, depth ≤ 32, at most `maxMessageBytes`):
`ready` with `protocol`, `version` 1, `transportVersion` 1, `serverTime` and the six limits as integers; `ack` with
`run.id`, `run.seq` (≥ 1), `status` `accepted` or `duplicate`, `bodySha256` (64 lower-case hex digits), `receivedAt`,
`acceptedAt`, `processingMs` and an optional `rank` ≥ 1; `error` with `run` (object or `null`), `httpStatus`
400–599, `error.code`, optional `error.retryAfterSeconds` and `error.serverTime`, and `fatal`. A message of another
`type` is ignored; an invalid message from the service, or a binary one, counts as `close 1002`.

**`realtime`** (only in WebSocket bodies, last member of the body, after the `taskScore` object of 11.10):

| Field | Value |
| --- | --- |
| `sourceMode` | `log` (transcripts and databases read on change notifications, section 5); `mixed` when the report carries the `traffic` section with status `ok` (traffic is sampled, 10.10) |
| `observedAt` | `sentAt − observationAgeMs` on the same corrected clock, or `null` when the window had no new data |
| `observationAgeMs` | `builtAt − observed` (monotonic ms) of the window's earliest observation (below), or `null` |
| `sourceResolutionMs` | `1000` for `log` (the 1 s fallback stat of section 5 bounds a missed notification); `null` for `mixed` (the traffic interval adapts, 10.10) |

*Observations.* The monitor records, for every read that changed the engine (a file or database read on a change
notification, the 1 s fallback, a completed whole-file read), the monotonic time the read began; the snapshot
published after it carries the earliest such time since the previous snapshot. A tick passes it to the scheduler
when the tick is *changed* (below); the window keeps the earliest one since it started.

**Scheduler state** (per run, monotonic ms): `seq`, `windowStart`, the window's `observed`, `dirty`, `lastBuildAt`,
`lastActivity` (the `activity` of the last built report), `lastActive` and the mode of 11.5, the connection
(`closed`, `connecting`, `opening`, `open`, `closing`) with its deadline, `readyAt`, `awaiting` (reports written and
not yet answered, with their write time and observation), `failures`, `reconnectAt`, `rateLimitedUntil`, `stop` (a
reason, or none), the strike counts `rejectStrikes`, `nonceStrikes`, `signatureStrikes`, `runConflicts`, `oversize`,
and the `ready` mismatch flags. A run starts as in 11.5 and also when the user changes the transport, after
`seq_conflict` and on every launch; a new run closes the connection of the old one (no `leaving`) and drops its
awaiting reports as `unknown`. The counters `built`, `accepted`, `duplicate`, `rejected`, `stale`, `saturated`,
`unknown`, `unmatched` and the latency samples belong to the transport session (from turning reporting on, a launch,
or a change of service or transport), not to a run, so `seq_conflict` keeps them.

**Ready** is the readiness of 11.5 (reporting on, confirmed, registered, network on, not paused, no error that needs
the user) and the transport available and no `stop`. A tick is **changed** when the ledger received an increment at
it, or when one of the values a report would carry at it, rounded as sent (11.7) — `live.outputTokS`,
`live.totalTokS`, `live.estimatedShare`, `live.agents.generating`, `.tools`, `.active` — differs from the last built
report's; with the `taskScore` extension also as 11.10 *WebSocket* says.

**Each tick `t`** (an engine tick, a snapshot after a read, or a wake-up the scheduler asked for), in this order.
*Discard* empties the ledger, sets `windowStart = t` and clears the window's observation; *leave* builds `leaving`
when fewer than `maxInFlight` reports are awaited (no minimum interval), writes it, and makes the connection
`closing` with the deadline `t + 1000` — closed with `close 1000` at once when nothing is awaited.

1. User events as 11.5 step 1. `networkOff`: `abort` (every awaiting report `dropped unknown`, connection `closed`).
2. Gap (11.5 step 2): discard; a connection that is not `closed` is aborted (after a sleep the socket is stale).
3. Mode and `lastActive` as 11.5; `dirty` becomes true when the tick is changed or the mode differs from
   `lastActivity`; a changed tick's observation joins the window (the earliest stays). A `closing` connection at or
   past its deadline is aborted.
4. **Leaving on a user action** (pause, turning reporting off, quit): when the connection is `open`, the previous
   mode was not `quiet` and `t ≥ rateLimitedUntil`: leave.
5. **Not ready** (paused, off, network off, `stop`, transport unavailable): an `open` connection is closed with
   `close 1000` when nothing is awaited, else becomes `closing` with the deadline `t + 1000`; a `connecting` or
   `opening` one is aborted; discard; stop here.
6. **Quiet**: on the tick the mode becomes `quiet` with the connection `open`: leave. On any quiet tick a
   `connecting` or `opening` attempt is aborted; discard; stop here (nothing connects until activity returns).
7. **Connect**: when the connection is `closed`, `t ≥ reconnectAt`, `t ≥ rateLimitedUntil` and no refreshed settings
   are awaited (*`ready`* below): `connect` (connection `connecting`, deadline `t + 10 000`); discard; stop here.
   `connecting` / `opening` at or past their deadline: `abort`, failure (below); discard; stop here. `closing`:
   discard; stop here.
8. **Answers overdue**: when `t > writeTime + ackTimeoutMs` for an awaiting report: `abort`, failure; discard; stop
   here.
9. **Due**: `dirty` and `t − lastBuildAt ≥ minIntervalMs` (`lastBuildAt` is none after `ready`, which also sets
   `dirty`), or `t − (lastBuildAt, else readyAt) ≥ H` (`heartbeatSeconds`, 11.5) in either mode — the service keeps a
   device online by its reports, so a long tool run with nothing changing still sends one. When due and fewer than
   `maxInFlight` reports are awaited: **build** — `seq + 1`, the ledger and the instantaneous values at `t`,
   `activity` = the mode, `windowMs = t − windowStart`, then `windowStart = t`, the observation cleared,
   `dirty = false`, `lastBuildAt = t`, `lastActivity` = the mode; when its observation age is above 1000 ms it is
   `dropped stale`, otherwise written at once (`write`, awaited from `t`). When due and `maxInFlight` reports are
   awaited: discard (the window is lost, `saturated`), `dirty = false`. When `dirty` and not due: `wake` at
   `lastBuildAt + minIntervalMs` (once per distinct time).

**Answers** (only from the current connection; events of an ended one are ignored). An `ack` matches an awaiting
report by `run.id`, `seq` and `bodySha256`; a matched one is `delivered` (`accepted` or `duplicate`; the last
delivery time, the latency sample below and `rank` are taken from it, and the five strike counts return to 0); when
the connection has been `open` for at least 60 s, `failures = 0`. Anything else is `unmatched`. An `error` whose
`run` matches an awaiting report drops it as `rejected`; its code then gives the reaction below, also for `run: null`.
When a `fatal` error's reaction leaves the connection up, the client aborts it and counts a failure, so the close that
the service sends after it is never acted on. A `closing` connection is closed with `close 1000` once nothing is
awaited.

Whenever a connection ends — `abort`, `close`, a close by the service, a failed handshake — the reports it still
awaits are `dropped unknown` (their outcome is not known; they are never resent) and the connection is `closed`.

**Failures** (a refused handshake whose reaction does not stop, wait or start over, a deadline, an unexpected close
or abort, a fatal error): the connection ends, `failures + 1`, `reconnectAt = t + d × jitter` with `d` = 500, 1000,
2000, 4000, 8000, 16 000, then 30 000 ms for the 1st, 2nd … failure (jitter in [0.8, 1.2]; the fixtures use 1).
`Retry-After` always wins: `rateLimitedUntil = max(current, t + s)` with `s` clamped to 5 s – 1 h (60 s when a `429`
or a `4429`/`4503` close gives none); a wait is not a failure. A `stop` aborts a connection that is still up.

**Reactions** to an `error` code, a handshake rejection (its HTTP status and v1 error body) or a close code; an
explicit `error` received on a connection takes precedence over the close code that follows it:

| Code or close | Reaction |
| --- | --- |
| `timestamp_out_of_window` | correct the clock (11.6, `serverTime`); a handshake rejection is a failure |
| `nonce_replayed` | `nonceStrikes + 1`; at 3 `stop nonce`; a handshake rejection is a failure |
| `signature_invalid` | `react recheckRegistration` (REPORTING 8.3: pending key, then `POST /v1/devices`); `signatureStrikes + 1`, at 3 `stop keyMismatch`, else `close 1000` and a failure |
| `api_key_required`, `api_key_invalid`, close `4001` | `stop apiKey` |
| `forbidden`, close `4003` | `stop forbidden` |
| `unknown_device`, close `4004` | `react reRegister` (at most once per hour, REPORTING 8.3), failure |
| `username_mismatch` | `react rename` |
| `seq_conflict` | `abort`, `react newRun`: a new `run.id` with `seq` from 1 and a new window (the mode, `failures` and waits stay); it connects at the next tick |
| `run_conflict` | `close 1000` (every awaiting report `dropped unknown`), `runConflicts + 1`; at 2 `stop clientState`, else reconnect at the next tick |
| `version_unsupported` | `react refreshDiscovery`, `stop upgrade` |
| `payload_too_large`, close `1009` | `react refreshDiscovery`; a close is a failure; `oversize + 1`, at 3 `stop incompatible` |
| `report_stale` | nothing more |
| `rate_limited`, a `503` with `retryAfterSeconds`, close `4429` / `4503` | `abort`, `wait` (`Retry-After` as above) |
| `gone`, close `4410` | `stop gone` (reporting turns off, REPORTING 8.3) |
| close `4009` | `stop replaced` (another connection of this device took over) |
| close `4000` | failure (reconnect with the current key) |
| close `1002`, `1003`, `1007` | `stop incompatible` |
| `bad_request`, `payload_invalid`, `device_mismatch`, `not_found`, `method_not_allowed`, other 4xx | `rejectStrikes + 1`, at 3 `stop rejected`; a handshake rejection is also a failure |
| a `5xx` code, close `1000` not asked for, `1001`, `1011`, `1006` and every other close | failure |

A refused handshake takes the code of its v1 error body (`retryAfterSeconds` from the body, else the `Retry-After`
header, delay seconds only); a body that cannot be read is classified by its status: `401` as `signature_invalid`,
`403` as `forbidden`, `404` as `unknown_device`, `410` as `gone`, `413` as `payload_too_large`, `429` as
`rate_limited`, `503` with a `Retry-After` header as `rate_limited`, any other 4xx as `bad_request`, 5xx and others
as a failure. Every refused handshake whose reaction does not stop, wait or start over is also a failure. `stop`
ends automatic attempts until the user acts (`stop mismatch` and `stop replaced` offer *Try Again*, which clears the
stop and reconnects) or the settings change; `stop gone` turns reporting off.

**`ready`.** A valid `ready` (on an `opening` connection) whose six limits equal the document's opens the
connection: `readyAt = t`, discard, `dirty = true`, `lastBuildAt` none; its limits govern the connection. A `ready`
that differs: `close 1000`, `react refreshDiscovery` (forced, once), and the next connection waits for the refreshed
settings (`settings`) and uses them; a second differing `ready` before a matching one, a failed refresh or a document
without the capability (`settings` with none while waiting) gives `stop mismatch`. A refreshed document without the
capability at any other time makes the transport unavailable. Nothing is written before a valid `ready`. *Try Again*
(`retry`) clears a `stop`, the strike counts, the mismatch flags and `failures`, and connects at the next tick.

**Latency.** For each delivered report with an observation the client keeps `answer time − observed` (monotonic,
the conservative upper bound of REPORTING-WEBSOCKET 12) in a ring of the last 512 samples; settings show its p50 and
p95 next to the counters (DESIGN 11).

**Fixtures** (`report-vectors.json` → `websocket`): `discovery` (document → stream settings or `null`), `url`, the
signed `handshake` and `report` of REPORTING-WEBSOCKET 14, `encoding` (body → message text with given headers),
`realtime`, `messages` (service message text → parsed fields, `{"type":"other"}` or `"invalid"`), `rejections`
(status, body, `Retry-After` → code and wait), `backoff`, `latency` (samples → p50 and p95, nearest rank) and
`scheduler`. A scheduler case gives `config` (stream settings, `heartbeatSeconds`, `available`) and events at
monotonic ms: `tick` (`active`, `changed`, `observedAgo` — the observation is `t − observedAgo` —, `events`; `until`
and `step` repeat it), `open` (101 with the subprotocol), `ready` (`limits` that differ from the configured ones),
`ack` (`seq`, `status`, `sha` `"match"` or `"other"`), `error` (`seq` or none, `code`, `httpStatus`, `retryAfter`,
`fatal`), `close` (`code`, sent by the service or the network), `reject` (a refused handshake: `status`, `code`,
`retryAfter`), `settings` (refreshed stream settings, `null` for none) and `retry` (*Try Again*). Events at the same
time run in the listed order; a requested wake-up becomes a tick without new data (the `active` of the latest tick)
unless a listed tick has the same time. Writes are instant. `expect.log` lists every action as text — `<t> connect`,
`abort`, `close 1000`, `build <seq> <activity> <windowMs> <observationAgeMs or ->`, `write <seq>`,
`delivered <seq> <status>`, `dropped <seq> <stale|rejected|unknown>`, `saturated`, `react <name>`, `stop <reason>`,
`backoff <reconnectAt>`, `wait <rateLimitedUntil>`, `wake <at>` —, `expect.counters` the counters other than 0,
`expect.latency` the samples, `expect.state` the connection, `seq`, `failures`, `stop` and the last delivery time.
The expected values were written by hand from this section; the simulator and both apps must reproduce them.

### 11.10 Task score (extension `taskScore`)

User's decision (2026-10-29): live reporting carries the task score (6.9) — the highest 智力 with its form, and the
thinking speed — overall and per model, plus per-model statistics of the tasks that complete while reporting is on, so
that a service can rank models by score and success rate. It rides with the `models` section: no switch, section or
consent of its own; DESIGN 11 says it under the `models` switch, and the README privacy sections name it. The protocol
is `spec/REPORTING-EXTENSIONS.md` section 9 (kept with the partner documents). `spec/tools/report_task_score.py`
implements this section and checks `spec/fixtures/report-vectors.json` → `taskScore`.

**Active.** The extension is *active* for a report when the confirmed document has `extensions.taskScore` equal to the
JSON integer `1` (a missing value, `null`, `true`, `"1"` or another number is not) and the report carries `models` by
consent: the document lists `models`, `maxModels` ≥ 1 and the user's `models` switch is on (11.1). A report while it is
inactive carries nothing of this section, and its models list is exactly that of 11.3 and 11.7. A refreshed document
that adds or drops the capability takes effect at once and asks for no new consent (REPORTING 3.3 lists the changes
that do; the user's decision is that existing users are not asked again).

**Body.** With the extension active the body adds:

- a top-level object `taskScore`, after `models` and before `realtime` (11.9); it stays when the size rule drops
  `models`;
- on every `models[]` entry, after its other fields: `taskScore` (the same shape) and `taskStats` (an array, empty when
  no counted task of that model is in the report).

`taskScore` is `{score, form, thinkingTokS}`:

| Field | Value |
| --- | --- |
| `score` | the set's value (6.9 *Values*) rounded half away from zero, an integer 0–100; `null` exactly when `form` is `none` |
| `form` | `live`, `resting` or `none` (6.9 *Values*) |
| `thinkingTokS` | the set's `liveThinking` (6.9 *Live thinking*), rounded to 0.1 |

At the top level the set is all enabled sources — the value the menu bar or taskbar block shows — with the all-sources
sums. Per model it is the agents of that set whose current model (the model the agents list shows for it, as for
`models[].generating`, 11.2) resolves to the entry's id (11.3), with the *model sums* of 6.9 *Live thinking* added over
the raw models that resolve to that id. The top-level value is the highest of every agent, not of the listed entries:
a model with nothing else to report has no entry.

**Membership.** With the extension active, `models[]` also lists every id that an agent of the set is *live* with (not
idle, with a score: an agent running a test is in phase `tools` and has no increment) and every id with a row in the
task ledger (below); `sources` adds the sources of those agents and rows. A model that is only resting gets no entry.
Order, `maxModels` and `other` stay as 11.3 says. The merged `other` entry's `taskScore` is the set rule over the
merged ids' agents (the highest live score, else the highest resting, else none), its `thinkingTokS` the sum of their
unrounded model sums, rounded once, and its `taskStats` their rows added up per source.

**Task statistics.** `taskStats` has one row per source, in the source order of section 1, for the tasks counted
(below) since the previous build:

| Field | Value |
| --- | --- |
| `source` | the agent's source; it is one of the entry's `sources` |
| `completed` | counted tasks, 1–1000 |
| `scored` | those with a score (6.9) |
| `scoreSum` | the sum of their scores rounded half away from zero, as shown: means merge exactly |
| `scoreBins` | ten counts of those rounded scores, bins `[0, 10)`, `[10, 20)`, … `[80, 90)` and `[90, 100]`; they add up to `scored` |
| `successes`, `stalls` | tasks whose 6.11 `success` / `stalled` is true |
| `fixAttempts`, `fixFailures`, `recovered`, `recoverAttempts` | sums of the 6.11 counts |

The bins take the rounded score so that they agree with `scoreSum`; the comparison of 6.11 bins unrounded scores, which
differs only for a score within half a point below an edge.

**Watch.** The *watch* is on at a tick when reporting is on for a confirmed and registered service, the network switch
is on, reporting is not paused, no error or `stop` needs the user (REPORTING 8.3, 11.9), the extension is active and
seeding has finished (6.4). Transient waits leave it on: a backoff, a `Retry-After`, quiet, a sleep gap, a new run
(`seq_conflict`, a change of transport, `run_conflict`). A task is *watched* when its first event (its prompt, or the
first event of a task before the first prompt, 6.9 *Tasks*) is read at a tick when the watch is on and that event's `t`
is at most 2 minutes before `nowMs` (the 120 000 ms of 11.4). Every other task is history and is never counted: one read
while seeding, one that started before the watch was on (reporting turned on mid-task included), one whose first event
is older when it is read (a session found late, section 5).

- A withdrawn prompt (6.9 *Withdrawal*) never started a task; the previous task keeps its own watch.
- **Break**: at the tick the watch turns off — reporting turned off, paused or switched to another service, the network
  switch off, the `models` switch off, a refreshed document without `models` or the capability, an error or `stop` that
  needs the user, quitting — every task is unwatched and the task ledger is emptied, after a `leaving` report of that
  tick (11.5 step 4, 11.9 step 4) has taken it. A task whose agent's file goes away or whose source is disabled is
  unwatched.

**Counting.** A watched task is counted once, at the first tick at which it completes:

1. a later task of the same agent is *settled*, its prompt no longer withdrawable: for Claude, omp, Pi, OpenClaw and
   Factory Droid when the first deciding assistant line after it (6.9 *Composing the stream*) has been read and is not a
   duplicate (for a command, with no command output before it); for Codex at once;
2. `nowMs − lastLineMs ≥ 30 min` for its agent (6.11 `complete`), whether or not the agent is still in the agents list.

Counting a task with at least one action (one that has a 6.11 record) adds one row to the task ledger, under the
resolved id (11.3) of its model — the last composer note whose `t` is at most the task's `endMs` (6.9 *Model and
effort*, 6.11) — and its agent's source: `completed` 1, and the 6.11 fields evaluated over the task's events read so
far — its score rounded (`scored` 1, its bin) or none, `success`, `stalled`, `fixAttempts`, `fixFailures`, `recovered`,
`recoverAttempts`. A task without actions adds nothing. Either way the task is no longer watched, so it is never counted
again: a continuation that reopens it later (6.9 *Values*) and outcomes that arrive after it completed (a result read
after the next prompt, a background command's exit code) stay out of the report, though its history record (6.11) has
them. When the task ledger already holds 1000 completed tasks, a further completion is dropped, not deferred.

**Task ledger.** The rows counted since the previous build, per resolved id and source. A *build* (11.5, 11.9) with the
extension active takes the whole task ledger into `models[].taskStats` and empties it; a build whose `models` the size
rule dropped empties it too, and those completions are lost. Discards (11.5 steps 2, 5 and 6; 11.9 *discard* and
`saturated`) leave it alone: completions are outcomes, not usage of a window, so a task that completes during a quiet
period, a sleep or a backoff goes out with the next built report. Dropping it instead would lose the last task before
every break and bias the success rates. Otherwise only a break empties the task ledger. A built report that is lost
loses its completions, which are never resent (REPORTING 6.2): across retries, duplicates, `seq_conflict`, new runs and
reconnections every completed task is in at most one built report. The task ledger is never persisted: a relaunch
starts empty, and the tasks rebuilt from their files after it (6.9 *Event stream*) are history.

Completions are not activity (11.5 *Activity*): a task that completes by the 30-minute rule does not wake the
scheduler, keep a device online or start a report.

**WebSocket.** A tick is also *changed* (11.9) when the task ledger received a completion at it, or when a `taskScore`
the report would carry — the top-level one or an entry's, rounded as sent — differs from the last built report's: a
test result changes the score while every token value stands still.

**Size.** The top-level `taskScore` always stays. The per-model fields go with `models` when 11.7 drops it; a step that
first drops the per-model Harness and mode breakdowns (REPORTING-EXTENSIONS 2, 3) keeps them.

**Other extensions.** An entry that exists only for this extension has zero usage: its `harnessRequests` is empty or
absent and its `modeUsage` absent, so their sums stay exact. Task rows are not split by Harness beyond `source`, nor by
execution mode; the 6.11 `effort` is not sent.

**Never sent**, also with the extension: prompts, commands, outputs, paths, digests, fingerprints, suite names or keys;
any time of a task (start, end, first pass); a row per task (only sums per model and source within one report);
predictions, calibration, comparisons and the history records of 6.11; the work signals of 6.10.

**Fixtures** (`report-vectors.json` → `taskScore`): `capability` (a discovery document and the models switch → active),
`records` (a completed task → its row), `assembly` (agents' values, model sums, the models of 11.3 and the task ledger
→ the top-level `taskScore` and the final `models[]`), `ledger` (events in order — `watch`, `start` with the first
event's `t` or `seeding`, `withdraw`, `drop`, `complete` with the task's record, `build` with `models: false` for a size
drop, `discard`, `newRun`, `relaunch` — → what each build carries, `null` when `models` was dropped), `validation` (a
body → the pointers a service that advertises the extension rejects) and `receiver` (accepted reports → the merged
leaderboard of REPORTING-EXTENSIONS 9). The expected values were written by hand; both apps run `capability`,
`records`, `assembly` and `ledger` through their own code. The example bodies are `report-examples/report-taskscore.json`
(HTTP) and `report-taskscore-websocket.json`; the model sums are checked by the `modelWindows` cases of
`task-score-cases.json`.
