# OpenClaw — research notes

Read from the source of `openclaw/openclaw` at `9302c1147252b00656395f350bf774696d3f6865` (2026-10-26; npm
`openclaw` 2026.10.7, agent database schema 37), with spot checks of the tag `v2026.8.1` (the first stable release
that keeps sessions in SQLite). Requested in GitHub issue #18. No real OpenClaw database was read; the parser rules
are derived from the writer's code and checked on synthetic fixtures (`spec/tools/openclaw_fixtures.py`).

## Verdict

Feasible. OpenClaw keeps one SQLite database per agent with every session transcript in it, and each request that
OpenClaw's own runtime sends is stored as one assistant message with per-request usage (non-cached input, cache read,
cache write, output), the model, the provider and the request start; the end is the time the message was appended,
a few milliseconds after the stream ended. So OpenClaw's own requests can be measured like Pi's before `durationMs`
(METRICS 4.13). Turns that OpenClaw hands to another agent runtime (Claude Code CLI, the Codex app server, ACP agents)
are mirrored into the transcript with the whole turn's summed usage and no request timing; the underlying agent's own
log has the per-request records, so those mirrors must not be counted again (METRICS 4.14).

## What exists

- **Storage** (stable since 2026.8.1, extended-stable 2026.8.3x and 2026.9.3x): `<state>/agents/<agentId>/agent/
  openclaw-agent.sqlite`, SQLite in WAL mode, one database per OpenClaw agent (agents are configured personas, not
  sub-agents). `<state>` is `$OPENCLAW_STATE_DIR`, else `<home>/.openclaw`, or `<home>/.openclaw-<profile>` for a named
  profile (`--profile`, `--dev` = `dev`, `$OPENCLAW_PROFILE`); `<home>` is `$OPENCLAW_HOME`, else the OS home. The same
  layout on macOS, Linux and native Windows (`%USERPROFILE%\.openclaw`). An agent's folder can be moved with
  `agents.entries.*.agentDir` in the config; the database then lives at `<agentDir>/openclaw-agent.sqlite`.
- **Not local or not readable**: incognito threads (memory only), PostgreSQL agent stores (an optional engine),
  remote Gateways, and the Windows Hub's default Gateway, which runs inside its own WSL distro (`OpenClawGateway`).
- **Older layout**: before the SQLite move (2026.7.x and older, including the extended-stable 2026.7.33–35 line,
  last updated 2026-09-20) transcripts were JSONL files `<state>/agents/<agentId>/sessions/<sessionId>.jsonl` in the
  same entry format. Current OpenClaw refuses to start until `openclaw doctor --fix` imports them. Out of scope.
- **Tables read** (only these; the same database also holds auth profiles and memory, which are never read):
  - `session_windows`: one row per transcript generation (`session_id`, the key of the logical session
    `session_key`, `created_at`, `transcript_updated_at`, `agent_harness_id` = the runtime of the latest run,
    `acp_owned`, `spawned_by` = the session key of the spawning parent).
  - `session_nodes`: one row per logical session (`session_key`, `current_session_id`).
  - `transcript_events`: one row per transcript entry, `(session_id, seq)`, `created_at` (Unix ms), the entry JSON in
    `event_json` or, from schema 23 (2026.9.6), for entries of 1 KiB – 4 MiB, a checksummed level-1 zstd frame in
    `event_zstd` (`event_utf8_bytes` = its decoded length). No dictionary.
  - `transcript_rewrite_watermarks`: a generation per session window that changes when the transcript is rewritten.
- **Entry format**: the Pi session JSONL family (METRICS 4.7, 4.13), session version 3 or 4. The first entry of a
  window is the header `{type:"session", version, id, timestamp (ISO), cwd, parentSession?}`; then entries
  `{type, id, parentId, timestamp (ISO, append time)}` of types `message`, `thinking_level_change`, `model_change`,
  `compaction`, `reset`, `branch_summary`, `custom`, `custom_message`, `label`, `session_info` and the control entry
  `leaf`. Messages have `role` `user`, `assistant`, `toolResult`, `custom`, `bashExecution`.
- **Assistant message** (OpenClaw's `@openclaw/llm-core`, forked from pi-ai): `api`, `provider`, `model`,
  `responseModel?`, `responseId?`, `usage`, `stopReason` (`stop`, `length`, `toolUse`, `error`, `aborted`),
  `timestamp` (Unix ms), content blocks `text`, `thinking`, `toolCall {id, name, arguments}`, private metadata
  `__openclaw` (run id, mirror origin, …). No duration field and no reasoning-effort field.
- **Usage**: `input` (non-cached), `output`, `cacheRead`, `cacheWrite`, `cacheWrite1h?` (subset of `cacheWrite`),
  `cacheWrite5m?`, `totalTokens`, `cost`, `usageReport?` (`unavailable` = provisional stream counters, which OpenClaw's
  own accounting drops), `contextUsage?`. No reasoning count from OpenClaw's own transports; mirrored harness turns
  may add `reasoningTokens`.
- **When things are written**: the user message is committed before the request is dispatched (for a new session
  together with the header); each assistant message is appended once, complete, at `message_end`; a tool result when
  the tool finished. Nothing is written while a request streams. An assistant message's `timestamp` is taken when the
  provider transport creates its output, before the request is built and sent; the entry `timestamp` is the append
  time. Streams with asynchronous tool calls (only when the `sessions_yield` tool exists) commit the finished prefix
  first as an assistant message with the same `timestamp`, `stopReason: toolUse` and empty usage; the usage belongs
  to the terminal fragment.
- **Side calls**: compaction summaries, titles, memory, recaps and `/btw` side questions record their usage only in
  memory or in derived caches, never as transcript entries.
- **Delegated runtimes** (the turn's model loop runs in another program, and OpenClaw writes a mirror):
  - CLI backends: `claude-cli` (Claude Code; shares the user's `~/.claude` or `$CLAUDE_CONFIG_DIR`, so Claude Code
    logs every request itself) and `google-gemini-cli` (Gemini CLI, run with a temporary private `GEMINI_CLI_HOME`
    that is removed afterwards). Their mirror: one assistant message per turn with `api: "cli"`, `provider` = the
    backend id, the turn's usage, `timestamp` at the end.
  - Codex app server (the default runtime for `openai/*` models with a ChatGPT/Codex subscription): mirror messages
    carry `__openclaw.mirrorOrigin: "codex-app-server"`; their usage is the sum of every model call of the turn.
    Codex writes its own rollouts with per-request usage and timing to `<agentDir>/codex-home/sessions` (the default
    agent scope) or to the user's `$CODEX_HOME` / `~/.codex/sessions` (`appServer.homeScope: "user"`). Rollouts that
    OpenClaw started have `session_meta.originator == "openclaw"`.
  - ACP: ACPx native agents (harness and provider ids `acp-opencode`, `acp-pi`, `acp-copilot`, `acp-kilocode`,
    `acp-qwen`) write one assistant message per turn with the ACP agent's per-turn usage, `timestamp` at the end and
    `idempotencyKey` `<requestId>:acp:assistant`; classic ACP sessions (`runtime: "acp"`: Claude Code, Codex, Gemini
    CLI, Grok Build, …) have `session_windows.acp_owned = 1`.
  - Optional external harness plugins (`copilot` for GitHub Copilot CLI, `muse` for Muse Code) write their own
    mirrors without a marker; the window's `agent_harness_id` names them.
- **Sub-agents**: `sessions_spawn` runs each child in its own session `agent:<agentId>:subagent:<uuid>` with
  `spawnedBy` = the parent's session key, in the same agent database (or another agent's for cross-agent spawns).
  `context: "fork"` children start from a copy of the parent's branch.
- **Forks and copies**: forked windows get a fresh header (new `timestamp`) followed by the copied entries with their
  original `id` and `timestamp` — the same copy pattern as Pi and omp.
- **Effort**: `thinking_level_change` entries (levels `off`, `minimal`, `low`, `medium`, `high`, `xhigh`,
  `adaptive`, `max`, `ultra`) are written when a session is first created and when the level is selected explicitly;
  a per-message level directive does not write one, so the entry can disagree with the level a request used. Not
  used: effort comes from the `trace.metadata` rows of `trajectory_runtime_events` (`src/trajectory/metadata.ts`
  333–337; METRICS 4.14), one per attempt, stored with Brotli from 1 KiB (`src/trajectory/runtime-payload.ts`).
- **Process**: Node.js 24.22.1+/26.12.1+ or Bun. npm and pnpm installs run `node …/openclaw/openclaw.mjs` (the
  package's `bin`), which may respawn itself with the same script; services run `openclaw.mjs`, `dist/entry.js` or
  `dist/entry.mjs` of the package. Standalone builds are one executable named `openclaw`. The Gateway — the process
  that sends the model requests — sets `process.title` to `openclaw-gateway` (`openclaw` for the CLI), which macOS
  `ps` shows but which does not change the executable path or arguments the app reads. CLI backends and the Codex app
  server run as child processes (`claude`, `gemini`, `codex app-server`).
- **Trajectory capture** (`trajectory_runtime_events`, on by default) records per-turn summaries with prompt and reply
  text; it has no per-request timing and is not read.

## Evidence

| Fact | Where (repository paths at the commit above) |
| --- | --- |
| State dir: `$OPENCLAW_STATE_DIR`, else `<home>/.openclaw`, profiles `.openclaw-<profile>` | `src/config/state-dir.ts` 23–54; `src/cli/profile-utils.ts` 5, 26–37 |
| `<home>` = `$OPENCLAW_HOME` (with `~`), else the OS home | `packages/normalization-core/src/home-dir.ts` 45–62 |
| Database path `<state>/agents/<agentId>/agent/openclaw-agent.sqlite`; incognito sentinel name | `src/state/openclaw-agent-db.paths.ts` 16, 35 |
| Sessions and transcripts live in the agent database; `agentDir` override | `docs/concepts/session.md` 449–457; `docs/concepts/multi-agent.md` 56–57; `docs/reference/database-schemas/layout.md` 15 |
| Schema history: sessions in SQLite from schema 8 (`v2026.7.2-beta.1`, stable `v2026.8.1`); zstd payloads from schema 23 (`v2026.9.6`); extended-stable `v2026.7.35` still writes schema 1 (no session tables) | `docs/reference/database-schemas/agent-schema-history.md` 20–60 |
| `session_windows`, `session_nodes`, `transcript_events`, `transcript_rewrite_watermarks` columns | `src/state/openclaw-agent-schema.sql` 16–51, 140–171, 437–484, 544–549 |
| `transcript_events` of `v2026.8.1` has `event_json` only | `src/state/openclaw-agent-schema.sql` at `v2026.8.1`, 389–396 |
| Compression: entries of 1 KiB – 4 MiB, zstd level 1 with checksum, header entries never compressed | `src/config/sessions/transcript-payload.ts` 43, 469–533; `src/config/sessions/transcript-payload-read.ts` 7; `src/infra/zstd-codec.ts` 11–30 |
| Header `{type:"session", version, id, timestamp, cwd, parentSession?}`; current version 4, version 3 readable | `src/agents/sessions/session-manager-types.ts` 7–14; `src/config/sessions/transcript-header.ts` 6–26; `src/config/sessions/version.ts` 2–4 |
| Header and first message written together; entry timestamp = append time | `src/config/sessions/session-accessor.sqlite-transcript-message-append.ts` 147–160; `src/agents/sessions/session-manager-append.ts` 633–641 |
| Entry types | `src/config/sessions/session-entry-codec.ts` 39–118 |
| Messages persisted at `message_end` | `src/agents/sessions/agent-session-base.ts` 363–425 |
| Assistant `timestamp` taken before the request is built (also in `v2026.8.1`: `packages/ai/src/providers/anthropic.ts` 337–352) | `packages/ai/src/transports/assistant-output.ts` 4–17; `packages/ai/src/transports/anthropic-stream-lifecycle.ts` 84–110; `packages/ai/src/transports/openai-completions-transport.ts` 272 |
| `AssistantMessage`, `Usage`, `StopReason` | `packages/llm-core/src/types.ts` 316–364, 525–560 |
| Async tool-call prefixes: empty usage, terminal fragment owns the usage | `packages/agent-core/src/agent-stream-response.ts` 419–450 |
| Unavailable usage is dropped by OpenClaw's accounting | `src/agents/usage.ts` 159–167 |
| OpenClaw's own cost report reads `message.usage` of user/assistant/tool-result entries | `src/infra/session-cost-usage-pricing.ts` 53–125 |
| CLI backend mirror: `api: "cli"`, `provider` = backend id, turn usage | `src/agents/cli-runner/cli-run-transcript.ts` 132–150, 213–228; `src/agents/cli-runner/types.ts` 105–113 |
| `claude-cli` shares Claude's config root | `extensions/anthropic/cli-constants.ts` 3; `src/agents/command/claude-cli-project-dir.ts` 8–15 |
| Gemini CLI runs with a temporary `GEMINI_CLI_HOME` | `extensions/google/cli-backend.ts` 94, 158; `extensions/google/cli-backend-auth.runtime.ts` 476–501, 565–574 |
| Harness messages created at turn end | `src/plugin-sdk/agent-harness-attempt-runtime.ts` 373–395 |
| Codex mirror: `mirrorOrigin: "codex-app-server"`; usage summed over the turn's calls | `extensions/codex/src/app-server/transcript-mirror-attestation.ts` 34–70; `extensions/codex/src/app-server/event-projector-usage.ts` 161–193 |
| Codex home: agent scope `<agentDir>/codex-home` by default, user scope `$CODEX_HOME` / `~/.codex` | `extensions/codex/src/app-server/auth-start-options.ts` 7–41; `extensions/codex/src/app-server/config-options.ts` 70–81; `docs/plugins/codex-supervision.md` 95–114 |
| Codex rollouts of OpenClaw threads: originator `openclaw` | `extensions/codex/src/app-server/client-initialize.ts` 8–15; `extensions/codex/src/session-catalog-provenance.ts` 158–173 |
| ACPx turns: per-turn ACP usage, end timestamp, `<requestId>:acp:assistant`; ids `acp-*` | `extensions/acpx/src/harness-attempt.ts` 256, 350–372; `extensions/acpx/openclaw.plugin.json` 28; `extensions/acpx/README.md` 41 |
| `acp_owned` and `spawned_by` window columns | `src/config/sessions/session-accessor.sqlite-provenance.ts` 64; `src/config/sessions/session-accessor.sqlite-session-row.ts` 74, 102 |
| Runtimes and harness ids (`openclaw`, `codex`, `claude-cli`, `copilot`, `muse`, ACP) | `docs/concepts/agent-runtimes.md` 10–45, 178–181 |
| Copilot harness mirror without a marker | `extensions/copilot/src/event-bridge-transcript.ts` 48–100 |
| Sub-agent sessions and `spawnedBy` | `docs/tools/subagents.md` 12–33; `src/config/sessions/types.ts` 353–358 |
| Forks: fresh header, copied entries | `src/config/sessions/session-accessor.sqlite-parent-fork.ts` 328–385 |
| Incognito in memory only | `docs/concepts/session.md` 198 |
| Thinking levels; when `thinking_level_change` is written | `src/auto-reply/thinking.shared.ts` 13–24; `src/agents/sessions/sdk.ts` 278–299; `src/agents/sessions/agent-session-inspection.ts` 170–186 |
| Release channels (extended-stable) | `docs/install/development-channels.md` 10–22, 198–213; tags `v2026.7.35` (2026-09-20), `v2026.9.34` (2026-10-23) |
| Windows: Hub with WSL Gateway, native `%USERPROFILE%\.openclaw` | `docs/platforms/windows.md` 10–70, 170–185 |
| Process: `bin` `openclaw.mjs`, service entries, titles, standalone executable | `package.json` 33–35; `src/daemon/program-args.ts` 31–33; `src/entry.ts` 221; `src/cli/gateway-cli/run-loop.ts` 78–82; `docs/install/standalone.md` 10–14; `docs/install/installer.md` 34–37 |
| Trajectory capture on by default, per-turn | `src/trajectory/runtime.ts` 351–353; `src/agents/embedded-agent-runner/run/attempt-finalize.ts` 133–146 |

## Recommendation

Add OpenClaw as source `openclaw` (METRICS 4.14) for the SQLite era, counting only the requests OpenClaw's runtime
sends itself and leaving delegated turns to the agents that ran them; add OpenClaw's agent-scoped Codex homes to the
Codex roots so those turns stay visible (under Codex, with exact timing). Leave the JSONL era (2026.7.x and older),
PostgreSQL stores, remote and WSL Gateways out of scope. Verify against a real database before release: the
timestamp rules, the zstd rows, the delegated-turn markers and the session-window columns.
