# Qoder — research notes

Qoder is Alibaba's agentic coding product line. Two local clients write per-call usage that Agent Speed can read: the
desktop editor **Qoder IDE** (a VS Code fork whose agent runs in a bundled Go core) and the terminal agent **Qoder CLI**
(`qodercli`, a Node bundle published on npm). Both are closed source. Requested in GitHub issue #47.

Evidence (2026-11-10):

- Qoder IDE 1.39.2 (`com.qoder.ide`, Developer ID Alibaba.com Singapore E-Commerce, notarized), macOS arm64 build
  `https://download.qoder.com/release/latest/Qoder-IDE-darwin-arm64.dmg`: static analysis of `out/main.js` and of the Go
  core `Contents/Resources/app/resources/bin/aarch64_darwin/Qoder` (module `cosy`, core version 1.39.2), then a real
  signed-in run: two prompts in the agent chat (one without tools, one reading a file), read back from the database.
- Qoder CLI 1.1.86 (npm `@qoder-ai/qodercli`, `bundle/qodercli.js`): static analysis, then two signed-in print-mode
  turns (`qodercli -p … -o json`) with the default settings and with `QODER_EXPOSE_TOKEN_USAGE=1`.
- The China editions and QoderWork: see *Other products* (static analysis only).

## Verdict

| | Qoder IDE | Qoder CLI |
| --- | --- | --- |
| Usage per model call on disk | yes: `chat_message.token_info` of each assistant row | only for custom (bring-your-own-key) models, or with `QODER_EXPOSE_TOKEN_USAGE=1`; otherwise removed before writing |
| Call end | the row's `gmt_create` (ms) | the assistant line's `timestamp` (ms) |
| Call start | none stored: the previous row of the session (the prompt or the tool results) | none stored: the previous user line (the prompt or the tool results) |
| Model | the selected tier only (`model_info.model_key`: `auto`, …) | the selected tier only (`message.model`: `auto`, …), or the custom model id |
| Reasoning tokens | not stored | not stored (Anthropic-shaped usage without a reasoning field) |
| Cost | not priceable: credits, and the tier names no model | not priceable for tiers; a custom model id prices normally |

Both are supported as sources: `qoder` (Qoder IDE) and `qoderCli` (Qoder CLI), METRICS 4.18 and 4.19. Speed is an
estimate whose interval starts at the previous record, so it includes the time to first token and Qoder's own queueing
and prompt building (the same model as Claude Code's rows without `api-request`, METRICS 4.1).

## Qoder IDE

- **Process.** The Electron main process starts the Go core as `Qoder start --workDir <userData>/SharedClientCache …`
  (`out/main.js`, its Go process service). `<userData>` is VS Code's user data folder for the product name `Qoder`:
  macOS `~/Library/Application Support/Qoder`, Windows `%APPDATA%\Qoder`, Linux `$XDG_CONFIG_HOME/Qoder`, else
  `~/.config/Qoder`; `VSCODE_PORTABLE` / `VSCODE_APPDATA` move it. Over Remote SSH the core and its data are on the
  remote machine.
- **Database.** `<workDir>/cache/db/local.db`, SQLite, opened by the core in WAL mode (`_journal_mode=WAL`). The
  session-database repair tool in `main.js` names the same path (`databasePath: …"cache","db","local.db"`). A later
  `memory.db` takes only the `agent_memory*` tables; the chat tables stay in `local.db`. Verified on macOS: the file
  exists after sign-in, with `-wal` and `-shm` beside it.
- **`chat_message`** (one row per message; DDL from the binary and from the real database):
  `id varchar(64) primary key, session_id, request_id, role, content, summary, summary_modified, summary_trigger,
  tool_result, token_info text, model_info text, extra text, gmt_create INTEGER`. The writer
  (`cosy/memory/stm.AddMessageHistory`) sets `gmt_create = time.Now().UnixMilli()` at insert. For `role ==
  "assistant"` it writes `token_info = {"prompt_tokens", "completion_tokens", "cached_tokens", "max_input_tokens"}`
  from the server's OpenAI-style stream `usage` (`ResponseMeta.Usage`; the reasoning count is in that struct but not
  written) and `model_info = {"model_key": "<the selected model config key>"}`. The server's `model` field is dropped.
  User rows are written when the turn starts, tool rows after the tool ran, assistant rows when the stream ended.
  `content` is stored encrypted (base64 of ciphertext); it is not needed.
- **Later rewrites.** After a context compaction the core overwrites the latest assistant row's `token_info` with an
  estimate (cached 0) (`compact.updateLatestAssistantTokenInfo`); Quest mode rewrites it with context usage
  (`OnContextUsageUpdated`). Rows are read once, when they appear; a later change is not followed.
- **Sessions.** `chat_session(session_id, project_uri, project_name, gmt_create, gmt_modified, mode,
  parent_session_id, parent_tool_call_id, session_type, …)`: the working folder and the parent of a sub-agent
  session (sub-agents write their own `chat_message` rows under their own session id).
- **Turns.** `chat_record.extra["ai-coding/turn-info"] = {businessIds, userMessageCreatedAt, startedAt, finishedAt,
  durationMs, status, llmCallCount}` and `"modelConfig": {"key": "auto"}`: per turn, tool time included; not used for
  speed. Credits are fetched from Qoder's server per `businessId` and are not on disk.
- **Not read.** `state.vscdb` (interface state only), the hook transcripts (role and content, no usage), the core's
  `logs/qoder.log` (per-call usage lines are Debug level), `chat_record` content.

Real run (model tier `auto`, one session):

| Row | Role | `gmt_create` | `token_info` (prompt / completion / cached) | Interval from the previous row |
| --- | --- | --- | --- | --- |
| 1 | user | …72 476 | — | — |
| 2 | assistant | …78 296 | 16 846 / 481 / 2 048 | 5.82 s |
| 3 | user | …85 546 | — | — |
| 4 | assistant (tool call) | …87 806 | 17 407 / 99 / 17 152 | 2.26 s |
| 5 | tool | …87 811 | — | — |
| 6 | assistant | …90 447 | 17 631 / 244 / 17 408 | 2.64 s |

`prompt_tokens` includes `cached_tokens` (OpenAI semantics). The turn records say 5 935 ms and 4 995 ms for the two turns
(`llmCallCount` 1 and 2). About 200 words of answer came with 481 completion tokens, so `completion_tokens` very
likely includes reasoning; this is not proven.

## Qoder CLI

- **Folder.** The user config folder is `~/.qoder` (`$QODER_CONFIG_DIR` replaces it; `$QODER_CONFIG_DIR_NAME` replaces
  the folder name; the China build uses `.qoder-cn`). Transcripts: `<config>/projects/<encoded cwd>/<session id>.jsonl`
  (the cwd with every character that is not a letter or digit replaced by `-`, as Claude Code does:
  `/private/tmp/…/qoder-ws` → `-private-tmp-…-qoder-ws`).
- **Lines.** Claude Code's shape: `user`, `assistant`, `attachment`, plus Qoder's own `runtime-config` (`model`,
  `reasoningEffort`, `contextWindow`), `active-leaf`, `workspace-directories`, `last-prompt`. Conversation lines carry
  `uuid`, `parentUuid`, `isSidechain`, `sessionId`, `cwd`, `gitBranch`, `entrypoint` (`cli`), `version`, an ISO
  `timestamp`; the Qoder lines have a numeric `timestamp` (ms) or none. An assistant line is one model call:
  `message.id` (a UUID), `message.model` (the tier, `auto`), `message.stop_reason` (`tool_use`, `end_turn`, …),
  `message.content` blocks, `message.usage` when kept. A tool result is a `user` line with `tool_result` content and
  `sourceToolAssistantUUID`.
- **Usage hiding.** Before a line is written (`Ovl` → `u1` in the bundle), `message.usage` is dropped unless the model
  source is `custom`, `QODER_EXPOSE_TOKEN_USAGE` is truthy, or the usage carries billing fields
  (`billable`/`credits`/`original_credits`) — in that last case the token fields are written as 0. The same rule hides
  tokens in `-p -o json` output (only `total_credits` is real) and in the event logs
  (`<config>/logs/sessions/<project>/<session>/segments/<run>.jsonl`, `turn.finished` with 0 tokens).
- **Usage when kept.** `input_tokens`, `output_tokens`, `cache_read_input_tokens`, `cache_creation_input_tokens`,
  `request_id`, `context_usage_ratio`, `service_tier`, `speed`, `iterations`. `input_tokens` includes the cache read
  (two calls of one turn: input 20 341 and 20 478 with 20 096 cached each; the second call added the 137-token tool
  result), unlike Claude Code's usage.

Real runs (tier `auto`, the same prompt reading `notes.txt`, two model calls each):

| Run | Assistant lines' `usage` | Event log `turn.finished` | `-o json` result |
| --- | --- | --- | --- |
| default | absent on both | 0 tokens | 0 tokens, `total_credits` 0.191 |
| `QODER_EXPOSE_TOKEN_USAGE=1` | 20 341 / 92 / 20 096 cached, then 20 478 / 107 / 20 096 | 40 819 in, 199 out | 40 819 in, 199 out, `total_credits` 0.021 |

Timing in the exposed run: prompt line 13:03:14.903 → first assistant line 18.025 (3.12 s, 92 tokens); tool result
18.131 → second assistant line 21.662 (3.53 s, 107 tokens). The event log brackets the same calls
(`loop.iteration.started` 15.026 and 18.245); it is not read.

## Other products

Static analysis only (2026-11-10; nothing executed):

- **Qoder CN IDE 1.39.2** (`Qoder CN IDE.app`, bundle id `com.aliyun.lingma.ide`; `product.json`: `nameShort`
  `QoderCN`, `dataFolderName` `.qoder-cn`, `applicationName` `qoder-cn`). `main.js` builds
  `<userData>/QoderCN/SharedClientCache`, so the database is `~/Library/Application Support/QoderCN/SharedClientCache/
  cache/db/local.db` (Windows `%APPDATA%\QoderCN\…`, Linux `~/.config/QoderCN/…`); the Qoder desktop app's import
  table names the same path (`["QoderCN","SharedClientCache","cache","db","local.db"]`, the international row
  `"Qoder"`). Its Go core is the international one: same version and commit (1.39.2, `f6e2d23`), identical embedded
  DDL and `token_info` field names. Both editions ship macOS (arm64, x64), Windows and Linux (deb, rpm) builds.
- **Qoder CN CLI 1.1.86** (`qoderclicn`; install script `qoder.com.cn/install` → `static.qoder.com.cn/qoder-cli-cn`,
  npm `@qodercn-ai/qoderclicn`): `configDirNames: {user: ".qoder-cn", project: ".qoder"}`, variables prefixed
  `QODERCN_` (`QODERCN_CONFIG_DIR`, `QODERCN_EXPOSE_TOKEN_USAGE`, …). Path encoding, transcript writer and usage
  hiding are character for character the international bundle's.
- **Qoder desktop app 0.5.7** (`com.qoder.app`, China `com.qodercn.app`; the installer also installs the IDE): runs the
  CLI engine, config folder `~/.qoder` (`~/.qoder-cn`), and writes its conversations into the same `projects` folder
  with the same usage hiding. They count under Qoder CLI; its own `main.sqlite` was not examined.
- **QoderWork 0.9.18** (`com.qoder.work`, a desktop assistant for non-coding work, announced to end service on
  2027-09-05): bundles CLI 1.1.26 with the config folder `~/.qoderwork` (China `~/.qoderworkcn`), which zeroes tokens
  unless `EXPOSE_TOKEN_USAGE` is set, and the app zeroes them again. No Go core, no `local.db`. Not supported.

CLI details used by the readers: sub-agents (Task tool and forks) write `<project folder>/<session id>/subagents/
agent-<agentId>.jsonl` (with `.meta.json` and `task-<taskId>.json` beside them), every line `isSidechain: true`. The
project folder name is `cwd.replace(/[^a-zA-Z0-9]/g, "-")`, cut to 200 characters plus a base-36 hash when longer
(`C:\Users\me\proj` → `C--Users-me-proj`); readers scan `projects/*/` instead of encoding a path.

## Unverified

- Whether `completion_tokens` (IDE) and `output_tokens` (CLI) include reasoning tokens.
- Tier names other than `auto` in real rows, and the rows of sub-agents, Quest mode and compactions.
- Windows and Linux paths of the IDE (VS Code's convention for the product names `Qoder` and `QoderCN`; not run).
- The China editions with a real account (no signed-in run).
- Custom-model rows of the CLI (no custom provider was configured).
