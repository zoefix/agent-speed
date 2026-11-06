# Devin — research notes

Devin is Cognition's coding agent. Two local clients matter here: the CLI `devin` (interactive, `devin -p`, and
`devin acp`, the Agent Client Protocol server that ACP clients start) and Devin Desktop, Cognition's VS Code-based app
(the former Windsurf), which runs ACP agents and records their event streams. Both are closed source. Nobody on the
project has a Devin account, so no Devin data exists on the project's machines: every rule of METRICS 4.17 is derived
from Devin's documentation and from public third-party readers (read as references, never copied), checked against
each other. Pull request #46 (not merged) contributed findings from its author's own database; they are used only
where a public source agrees, and the table below says which facts rest on that database alone. Every fixture in
`spec/fixtures/devin` is synthetic (`spec/tools/devin_fixtures.py`).

Sources (all checked 2026-11-06):

- Devin docs: `docs.devin.ai/cli` (install, platforms), `docs.devin.ai/cli/models` (`agent.model` in
  `~/.config/devin/config.json`, Windows `%APPDATA%\devin\config.json`; thinking level cycled with Alt+T; example
  model id `swe-1-6-fast`; Adaptive routing), `docs.devin.ai/federal/air-gapped/on-disk-formats` (the air-gapped
  build's on-disk layout, which also states the standard build's rules: below).
- tokscale (`junhoyeo/tokscale` at `82613d2`, `crates/tokscale-core/src/sessions/devin.rs`, `scanner.rs`,
  `lib.rs`), its pull request #858 (first reader, "verified against real local data": 42,648 messages, models
  `glm-5-2`, `glm-5-2-max-1m`, `kimi-k2-7`, `swe-1-6`, `swe-1-7`, `compactor`, `gpt-5-6-sol-max`, `summarizer`,
  `swe-check`), commit `cd07bf78`, and issue #1350 (a user's analysis of their real database, Devin CLI 3000.10.31).
- cctop 0.36.3 (`docs/providers/devin.md` on docs.rs): data folder, `$CHISEL_SESSION_DB`, the tables it reads, process
  detection.
- Live ACP clients of `devin acp`: OzEngineJP/pleiad (`docs/devin-usage.md`, Devin CLI 3000.11.92),
  spacedouut/openclaw-devin-cli (`src/devin-usage.ts`), lobehub (`packages/heterogeneous-agents/src/adapters/
  devinAcp.ts`), TianZuo555/pi-extensions (`packages/pi-devin-acp/src/updates.ts`), fy0/CodeKanban
  (`service/websession/devin_acp.go`).
- File readers derived from tokscale: Javis603/token-monitor (`docs/providers/devin.md`), qunqin24/Pulse
  (`DevinDesktopReader.swift`), qqzhangyanhua/mabiao (`docs/probe/token-fields.md`).
- The Agent Client Protocol's session-usage RFD (`agentclientprotocol.com/rfds/session-usage`) and its Rust schema.

## Verdict

The source `devin` reads the CLI only (the user's decision, 2026-11-06). The CLI's database records every model call
with exact usage, the request's total time and its time to first token, so it gives measured speed. A Devin Desktop
session run with the local agent is a `devin acp` session and is in that database. Devin Desktop's own event files are
**not supported** (below: no time, no model, unsettled counts, only titles to match them with the CLI), and Desktop's
default cloud agent writes no usage locally.

| Fact | Where | Confidence |
| --- | --- | --- |
| CLI database location | XDG data folder on macOS and Linux, `%APPDATA%` on Windows | Devin docs (air-gapped page) + tokscale, cctop |
| `$CHISEL_SESSION_DB` names the database file | — | cctop only |
| Usage per call | `message_nodes.chat_message` JSON, `metadata.metrics` | tokscale on real data, issue #1350 |
| `input_tokens` excludes the cache | `metrics` (67 input vs 32 256 cache read in one row) | issue #1350; PR #46 (`context_input_tokens` = sum) |
| One call stored twice | same `metadata.request_id`, identical usage | issue #1350 (100 % of 5 789 groups) |
| Row time | `message_nodes.created_at`, Unix seconds, written when the call ends | tokscale, cctop, PR #46 |
| Call duration, first token | `metrics.total_time_ms`, `metrics.ttft_ms` (13 666 and 13 394 ms in one row) | issue #1350 |
| Model | `metadata.generation_model`; `sessions.model` may be `adaptive` | tokscale on real data |
| Tool calls | `chat_message.tool_calls`; `metadata.finish_reason` | cctop (tool_calls); PR #46 only (finish_reason) |
| Desktop usage (not read) | `usage_update` notification, `_meta["cognition.ai/…Tokens"]` | 5 live clients, tokscale |
| One `usage_update` per call (not read) | — | pleiad, openclaw-devin-cli, lobehub; tokscale disagrees (below) |
| Desktop ↔ CLI link (not read) | `session_info_update.title` = `sessions.title`, unique titles only | tokscale, Pulse; no public data |
| Desktop line time (not read) | none known | — |

## CLI database

- **Location.** Devin's own documentation (the air-gapped page) says the CLI follows the XDG base-directory layout on
  macOS and Linux, honours `XDG_CONFIG_HOME` and `XDG_DATA_HOME`, does not use `~/Library` for its own state, reads no
  `XDG_*` variable on Windows and keeps the same files under `%APPDATA%\devin\` there; the air-gapped build uses the
  product folder `cli-federal` "to keep this state separate from any standard Devin CLI install, which uses `cli`".
  So the standard database is `$XDG_DATA_HOME/devin/cli/sessions.db`, else `~/.local/share/devin/cli/sessions.db`,
  and `%APPDATA%\devin\cli\sessions.db` on Windows, which tokscale and cctop read. tokscale also adds the XDG path on
  Windows; the documentation says the CLI never uses it there, so METRICS does not. cctop says `$CHISEL_SESSION_DB`
  (the CLI's internal name is *chisel*: `chisel/tool_call_content` appears in rows) names the database file and moves
  it; METRICS honours it, marked as third-party evidence. The air-gapped build's `cli-federal` folder is out of scope.
- **Tables.** cctop lists `sessions(id, working_directory, model, created_at, last_activity_at, title, hidden,
  agent_mode)`, `message_nodes(row_id, …, chat_message, metadata)` and `tool_call_state`; tokscale's test schema adds
  `message_nodes.session_id`, `node_id`, `parent_node_id`, `created_at` and `sessions.backend_type`. The SQL column
  `message_nodes.metadata` is always NULL in real databases (tokscale #858); everything lives in the `chat_message`
  JSON. `title` was missing in some older databases (tokscale reads them without it). Deleting a session sets
  `hidden = 1` and keeps its rows (cctop). Message nodes form a tree (`parent_node_id`) so a rewind branches; issue
  #1350 sums every row, branches included, and matches Devin's own totals.
- **Rows.** `chat_message` is `{role, content, tool_calls?, metadata{…}}`. Assistant rows carry
  `metadata.metrics{input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens, total_time_ms, ttft_ms, …}`
  (`tpot_ms`, `tokens_per_sec`, `context_input_tokens` and `num_tokens` also appear in the readers; none is needed),
  `metadata.generation_model`, `metadata.request_id`, `metadata.finish_reason`. Issue #1350's sample row has input 67,
  cache read 32 256, output 103, `ttft_ms` 13 394, `total_time_ms` 13 666: `input_tokens` is the uncached part, and the
  total time includes the time to first token (an end-to-end request time, like METRICS 3.3's speed).
- **Two rows per call.** Issue #1350 found every assistant message stored twice — same `request_id`, same `created_at`,
  byte-identical usage, the copies differing only in `metadata.extensions."chisel/tool_call_content"` — in 5 789 of
  6 046 request groups, all usage-identical, none without a `request_id`; `compactor` calls were stored once. Keyed by
  `(session, request_id)` the sums matched `final_metrics` of all 21 local `transcripts/<id>.json` exactly; by row they
  were about twice as large. tokscale has keyed this way since its #1359.
- **Times.** `created_at` is Unix seconds (cctop: "seconds, not milliseconds"; tokscale multiplies by 1000). The row is
  written when the finished assistant message is stored, so it is the call's end; tokscale back-anchors the start by
  `total_time_ms`. With one-second rows the call's position in time is uncertain by up to a second while its duration
  is exact. Whether `created_at` is truncated or rounded is not known.
- **Models.** `generation_model` names the served model; `sessions.model` is the selection and may be `adaptive`, a
  routing mode that is not a model (tokscale skips such rows; Agent Speed keeps them without a model). Internal calls
  (`compactor`, `summarizer`, `swe-check`) appear as models of their own.
- **Not used.** `transcripts/<id>.json` (ATIF v1.7 with per-step metrics and `final_metrics`) holds the same calls a
  second time, only for sessions that still have one. `num_tokens` is not a usage breakdown (tokscale uses it as an
  output fallback). Credits and ACUs are billing units. `agent_mode` is a permission mode (cctop: `normal`, `auto`,
  `accept-edits`, `plan`, `bypass`, …), not an effort. The thinking level (Alt+T) is not known to be stored anywhere.

## Devin Desktop event files: not supported

Why: the files give no time for a call (no speed, and every call of a file would fall on its last write), name no
model (always unpriced), and it is unsettled whether their counts are per call or cumulative (below), so even the
token totals could be wrong by a large factor. A session that Desktop runs with the local CLI agent is already in the
CLI's database, so its usage would count twice unless a Desktop file is matched to its CLI session, and the only link
is the session title — two sessions with one title, or a Desktop-only session whose title equals an unrelated CLI
session's, would be counted twice or dropped. Desktop's default cloud agent writes no usage locally at all. What was
learned is kept here in case real files settle these questions.

- **Location.** `User/acp-events/*.ndjson` in Desktop's user-data folder: macOS `~/Library/Application Support/Devin`,
  Windows `%APPDATA%\Devin`, Linux `~/.config/Devin` (tokscale also reads `~/.config/devin`). Builds that keep
  per-session databases under `acp-messages/` instead are not read (token-monitor). File names are UUIDs unrelated to
  the CLI's session ids (tokscale).
- **Lines.** Each line holds one ACP session update as `{"notification": {"sessionUpdate": …}}`; tokscale's test lines
  also carry a top-level `providerId` (`devin-cli`). `session_info_update` carries the session `title`;
  `usage_update` is ACP's context-window notification (`used`, `size`, optional `cost`) with Devin's `_meta` keys
  `cognition.ai/inputTokens`, `cognition.ai/outputTokens`, `cognition.ai/cachedReadTokens`,
  `cognition.ai/cachedWriteTokens` (plus `cognition.ai/totalCreditCost`, `cognition.ai/totalAcuCost`,
  `cognition.ai/responseDimensions` in pi-devin-acp). No source shows a timestamp on a line; tokscale probes four
  guessed paths and falls back to the file's modification time.
- **One update per call.** The live clients agree: pleiad (Devin CLI 3000.11.92) says each notification is the most
  recent single model call, input includes cache reads with the cache as its breakdown, calls of a turn add up,
  identical consecutive notifications and `subagent_context` copies are repeats, and notifications carry no call id;
  openclaw-devin-cli says each root `usage_update` reports one call whose `inputTokens` includes cached input and that
  "Devin repeats a call's usage once per agent scope", and skips a notification identical to the previous one;
  lobehub maps each update to one step (input + cached write as total input). tokscale instead says "real ACP usage is
  cumulative for input/cache tokens and incremental for output" (commit `cd07bf78`) and keeps one aggregate per file
  (latest input, summed output); Pulse and mabiao copy it. A call's prompt grows over a conversation, so per-call
  inputs look cumulative; the clients that read the stream as Devin writes it are explicit. Which reading is right
  decides between summing the inputs and keeping the last one — a factor of the number of calls in a session.
- **Legacy shape.** tokscale's first version also read usage objects embedded in arbitrary notifications; it predates
  its authors seeing real files, and no source shows Devin writing them. Not read.
- **Duplicates with the CLI.** tokscale resolves a Desktop file to a CLI session when the file's latest title equals
  the `title` of exactly one CLI session, and drops the file's usage when that session has CLI usage; ambiguous titles
  stay separate. A live monitor could not even apply that condition: at startup it reads only today's rows, so it
  cannot tell whether an older session has usage, and a call counted before its title arrives would have to be taken
  back.
- **Cloud agent.** The default `devin-cloud` agent is metered server-side and writes no usage locally
  (token-monitor); other ACP agents Desktop runs (Claude Code, OpenCode, Cascade) write `usage_update` without
  `cognition.ai` keys, and their own logs are read by their own sources.

## Processes

cctop recognises the CLI binary and requires the `acp` subcommand for sessions; Desktop starts `devin acp`. The CLI is
one native executable `devin` (the docs' install script, Homebrew cask and Windows installers). Desktop's own
executable is `Devin` (macOS `Devin.app/Contents/MacOS/Devin`); its Windows install folder is not known. As a
Windsurf descendant Desktop may still ship Windsurf's `language_server_*` binary, which METRICS 10.5 attributes to
Antigravity: unverified.

## Open questions (for someone with a Devin account)

1. A real `sessions.db`: `select role, count(*)` over `json_extract(chat_message,'$.role')` (is a tool result `tool` or
   `user`?), whether `tool_calls` sits on assistant rows, `typeof(created_at)`, and whether `created_at` grows with
   `row_id` across concurrent sessions.
2. For Desktop support later: in one Desktop session with the local agent, does every `usage_update` match one CLI
   row's metrics (per call), do both sides carry the same title from the start, and does any event line carry a time
   or a model?
3. Are CLI sub-agents (`subagent_context`) separate `sessions` rows, and how are they linked to their parent?
