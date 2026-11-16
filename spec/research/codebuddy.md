# CodeBuddy Code — research notes

CodeBuddy Code is Tencent's terminal coding agent (`codebuddy`, alias `cbc`), published on npm as
`@tencent-ai/codebuddy-code`. It is the CLI that WorkBuddy embeds (`spec/research/workbuddy.md`), here installed and run
on its own. Requested in GitHub issues #36 and #62.

Evidence (2026-11-16): **static analysis only**. The npm tarball of the latest release was downloaded with
`npm pack @tencent-ai/codebuddy-code` (no install, no install scripts) and read with grep and Python; none of its code
was executed, no account was signed in and no real session file was available. The CodeBuddy IDE and the IDE plugins
were not downloaded.

| Package | Version | Published | Tarball sha256 | Bundles read |
| --- | --- | --- | --- | --- |
| `@tencent-ai/codebuddy-code` | 2.184.0 (`latest`; commit `589d18ac`) | 2026-11-13 | `882f681a…c9b7` | `dist/codebuddy.js` (the CLI), `dist/codebuddy-headless.js`, `dist/codebuddy-lite-wb.mjs` (the WorkBuddy build of the same version) |

The tarball also ships the user documentation (`dist/web-ui/docs/{en,cn}/cli/*.md`) and the changelog; both are cited
below. A separate `rust` dist-tag (`2.182.0-rust`, binaries `codebuddy-rust` / `cbc-rust`, "Rust edition") exists; it is
not `latest` and was not read.

## Verdict

| | CodeBuddy Code 2.184.0 |
| --- | --- |
| Transcripts on disk | yes: one JSONL file per session, `<config>/projects/<slug>/<session>.jsonl`, the format WorkBuddy's embedded CLI writes |
| Usage per model call | yes: `providerData.usage` (agents-core) and `providerData.rawUsage` (the service's OpenAI-style usage) on the call's last output item, else a `model-usage` line |
| Call end | the time of the line that carries the usage (Unix ms) |
| Call start | **new in 2.184.0**: `rawUsage.model_calls[].duration_ms`, the measured duration of each attempt; older versions store none (inferred from the previous input line, as for WorkBuddy) |
| Time to first token | **new in 2.184.0**: `rawUsage.model_calls[].time_to_first_token_ms` |
| Model | `providerData.model` (the stream's model), else the requested id; routing slots (`auto`, `fast-model`, …) as in WorkBuddy |
| Reasoning tokens | yes, when the service reports them (`outputTokensDetails[].reasoning_tokens`, `completion_tokens_details.reasoning_tokens`) |
| Cost | API-equivalent where the model id is priced; credits (`credit-usage`) are not used |

Supported as the source `codebuddyCode` ("CodeBuddy Code"), METRICS 4.20, with the WorkBuddy parser (4.9). Not yet
verified with real signed-in sessions.

## Where sessions live

- **Config folder.** `resolveCliHomeDir()` (module `296371`): a customized (white-label) build's
  `customization.json` `dataLayout.configDir.userDirName` under the home folder; else `CODEBUDDY_CONFIG_DIR` when it is
  not blank (used as it is); else `~/.codebuddy` (`os.homedir()`, `%USERPROFILE%` on Windows). The official npm package
  has no `customization.json`. The docs list `CODEBUDDY_CONFIG_DIR` as "custom location … to store configuration and
  data files" (`env-vars.md`) and describe the folder in `codebuddy-dir.md`.
- **Transcripts.** `PathUtils.getHomeProjectsDir()` = `<config>/projects`; a session's folder is
  `compressWorkspacePathName(realpath(cwd))`: `/`, `\` and `:` become `-`, leading and trailing `-` are removed, runs
  are collapsed, and a name over 255 bytes is cut to 180 bytes plus `-<djb2 base36>` (identical to WorkBuddy). The store
  is `LocalJsonlSessionStore`: `<projects>/<slug>/<sessionId>.jsonl`; a sub-agent (`sessionId` starting with `agent-`
  and a parent) is `<slug>/<parentSessionId>/subagents/agent-<id>.jsonl` (`sessionKeyFor`).
- **Not transcripts** (same shapes as WorkBuddy): `<slug>/<session>.meta.json`, `<slug>/<session>/tool-results/`,
  `<slug>/<session>/subagents/workflows/…/journal.jsonl`, `<slug>/<session>.file-rollback.ndjson`, and, only with
  `CODEBUDDY_CODE_PERSIST_STDIO`, the stream-json channel logs `<projects>/<slug>/<x>/stdio/<stamp>.jsonl`. None
  matches the two discovery shapes of METRICS 4.9.
- **Other files of the config folder** (`codebuddy-dir.md`): `sessions/` (active-session registry), `history.jsonl`
  (prompt history for `/resume`), `logs/`, `traces/` (OpenTelemetry), `file-history/`, `blobs/`, `plans/`, `tasks/`,
  `teams/`, `agent-home/` (tool journal, audit), `shell-snapshots/`, `plugins/`, `mods*/`, `local_storage/`,
  `usage-data/`, `models.json`, `settings.json`, `user-state.json`.
- **Retention.** `SessionHistoryCleanService` deletes whole session files (with their sidecars and artifacts) whose
  modification time is older than `cleanupPeriodDays` (user setting, default 30) once at start-up and every 24 hours.
  Since 2.166.0 the cleanup runs only for interactive starts (TUI, `--serve` Web UI, `codebuddy cleanup`); `-p`,
  `--acp`, `--a2a`, the SDK and hosted CLIs (WorkBuddy) keep everything unless `CODEBUDDY_SESSION_HISTORY_CLEANUP=1`
  (changelog 2.166.0, `#145446`; 2.179.0 stopped nested CLIs from inheriting it). Agent Speed's daily history keeps the
  aggregates after a file is deleted (METRICS 8).
- `--no-session-persistence` writes no transcript (documented in `cli-reference.md`).

## Line format

The transcript code of the standalone CLI and of the WorkBuddy build is the same: in 2.184.0,
`transformItemForSaveAsync`, `handleStreamRunResult`, the usage processor (priority `UsageProcessor`),
`LocalJsonlSessionStore.filePathFor`, `sessionKeyFor`, `normalizeUsageFormat`, `resolveCliHomeDir` and
`buildModelCallAttemptMetrics` are identical in `codebuddy.js` and `codebuddy-lite-wb.mjs` once minified names are
normalized. The line shapes of METRICS 4.9 therefore hold: `id, parentId, logicalParentId, timestamp (Date.now()),
type, role`, then the item, `providerData`, `cwd` and `sessionId`; `message`, `function_call`,
`function_call_result`, `reasoning` and `model-usage` lines as in `workbuddy.md`.

Changes since the 2.161.0 build that `workbuddy.md` describes:

- **`rawUsage.model_calls`** (2.184.0, changelog `#179641`; release notes: "persisted with the session record and
  replayed when the session is resumed"). `withModelMessageCorrelation` wraps every model the CLI creates. Each call
  keeps `attemptStartedAt = Date.now()` when the call starts and again for every retry inside the provider
  (`startRetriedModelAttempt`: connection failures, 401 refresh, request-level retries); the provider sets
  `firstTokenAt` at the first chunk with content, reasoning, a tool call or a refusal. At `response_done`
  `stampModelCallMetrics` builds `[{attempt_no, duration_ms, result, time_to_first_token_ms?, finish_reason?, model?,
  output_tokens?}, …]` — the failed attempts first, the attempt that produced the response last, `duration_ms =
  round(endedAt − attemptStartedAt)` with `endedAt = Date.now()` at `response_done` — and attaches it as `model_calls`
  to `response.rawUsage` and to every provider usage object seen in the stream (`prompt_tokens`, `completion_tokens`,
  `prompt_cache_hit_tokens`, `cache_read_input_tokens` or `credit` present). The chunk usage that
  `handleStreamRunResult` writes as `providerData.rawUsage` is that same object (or a copy made after the stamp), so
  the line that carries a call's usage carries its attempts. `result` is `success`, `failed` (also refusals and
  content filters) or `cancelled`. The ACP `usage_update` event strips the field unless the client asks for it; the
  transcript keeps it.
- **New fact lines**: `goal-state`, `request-recovery`, `context-usage` (context categories at the end of a turn),
  `checkpoint-restore`, besides the ones `workbuddy.md` lists. None carries a call's usage; they are *anything else*.
- **`turn-metrics`** is written by interactive TUI sessions (the standalone CLI's normal mode) when `showTurnDuration`
  is on (default): one line per finished turn with `durationMs` and `tokenDelta`, and one per finished background task.
  WorkBuddy's `--serve` runtime never writes it. It measures turns, not calls; not used.
- Output items now also carry `providerData.entryId` and, for custom models on the Responses API, `upstream`.

What is measured, per call: input (including cache), cache reads and writes, output, reasoning, model, end time; with
2.184.0 also the request duration and the time to first token of the attempt that answered. The interval
`[end − duration_ms, end]` excludes the request assembly before the HTTP call and failed attempts; it includes the
stream's tail up to the usage chunk.

## How it differs from WorkBuddy

| | WorkBuddy (desktop app) | CodeBuddy Code (standalone CLI) |
| --- | --- | --- |
| Config folder | `~/.workbuddy`, `~/.workbuddy-ai` (the app sets `WORKBUDDY_CONFIG_DIR` / `CODEBUDDY_CONFIG_DIR` for its children only) | `$CODEBUDDY_CONFIG_DIR`, else `~/.codebuddy` |
| CLI version | 2.161.0 in WorkBuddy 5.8.3 / 5.8.4; updated with the app | whatever npm installed; 2.184.0 read here |
| Runtime | `--serve` / `--acp` behind the desktop | interactive TUI, `-p`, `--acp`, Web UI, SDK |
| `turn-metrics` lines | never | interactive sessions |
| Cleanup | off (and the desktop's retention setting) | 30 days for interactive starts (`cleanupPeriodDays`) |
| `cloud/` mirrors | written by the desktop | not written by the CLI |
| `workbuddy.db` (effort, titles) | yes | none; the effort exists in memory and as `reasoningEffort` of `<config>/settings.json` |
| Model catalog | per edition | `product.json`: `auto` (default), `fast-model`, `balanced-model`, `primary-model`, `deep-model`, DeepSeek V4.1 Flash (`-sg`), GPT-6 Astra, GPT-5.6 Sol/Terra/Luna, GPT-5.5, GPT-5.4, Gemini 3.5 Flash, GLM 5.3 / 5.3 Flash / 5.2, Kimi K3 / K2.8 Preview / K2.6, `hy3`, `hy4-preview` (WorkBuddy AI's list); replaced at run time by remote configuration |
| Process | `Electron …/app.asar.unpacked/cli/bin/codebuddy` | `node …/node_modules/@tencent-ai/codebuddy-code/bin/codebuddy` (npm; `codebuddy`, `codebuddy-code`, `cbc` are links to it) |

## What #36's user saw

Pointing WorkBuddy's folder at `~/.codebuddy` reads `~/.codebuddy/projects` with the same parser (METRICS 4.9 already
said the format is the same), so CLI sessions there would have been counted. "0 sessions" means no file of the shape
`projects/<slug>/<session>.jsonl` was there. The likeliest reason, **not verified**: the user's "CodeBuddy" is the
CodeBuddy IDE or IDE plugin, not the CLI. The CLI shares a module with the plugins whose constants name `.codebuddy` as
the user data folder (rules, skills, MCP and settings live there) and `CodeBuddyExtension` as the plugin's data folder
(`~/Library/Application Support/CodeBuddyExtension`, `%USERPROFILE%\AppData\Local\CodeBuddyExtension`,
`~/.local/share/CodeBuddyExtension`), where the shared sign-in is kept. An IDE user can thus have a `~/.codebuddy`
folder without CLI transcripts. The IDE's own chat storage was not examined (out of scope).

## Read and never read

Read: the transcripts under `<config>/projects` (the two discovery shapes of METRICS 4.9) and, for the configured
effort only, the key `reasoningEffort` of `<config>/settings.json` (EXECUTION-MODES).

Never read:

- The sign-in: `FileAuthenticationStorage` keeps it in `<CodeBuddyExtension>/Data/Public/auth/<hash>.info` (folder
  above); `CODEBUDDY_CREDENTIALS_IN_MEMORY=1` keeps it in memory.
- `<config>/.credentials.json` (MCP OAuth tokens), `<config>/credentials.json` (plugin secrets),
  `remote-host-credentials.enc` / `.key`, `frp/` keys, `local_storage/` (internal key-value store), `models.json` (the
  user's own models, with API keys), `channels/`, `connectors/`.
- Everything else outside `projects`, and inside it everything that is not a transcript (above).
- Message content, tool arguments and tool output inside transcripts (METRICS 4.9 reads only the fields it names).

## Limits

- Static analysis of one version. Older CLIs write the same layout (WorkBuddy's 2.161.0 build) but no `model_calls`,
  so their calls are timed as WorkBuddy's are (start inferred from the latest input line).
- `duration_ms` is rounded to whole milliseconds and ends at `response_done`; the line carrying the usage is written a
  few milliseconds later, so `end − duration_ms` starts a few milliseconds late.
- Calls on the Responses API (custom models with `"api": "openai-responses"`) carry no provider usage object that the
  stamp recognizes; they keep the inferred start.
- Agent teams, background jobs, the Web UI and worktrees were not traced beyond the shared session store; their
  sessions are expected under `projects` like the others.
- Processes: the npm layout is read from the package; Homebrew or other installers were not found in the bundle.

## Verify with real data

1. One interactive session: the files under `~/.codebuddy/projects`, and `python3 spec/tools/reference.py
   --dump-today --codebuddy-code ~/.codebuddy` against the CLI's own `/cost` or usage view.
2. `rawUsage.model_calls` on the usage line of each call: present, one element per attempt, `duration_ms` against a
   stopwatch for a long answer, `time_to_first_token_ms` plausible.
3. A sub-agent (`Task`): its file at `<slug>/<session>/subagents/agent-<id>.jsonl`.
4. A retried call (network drop): `attempt_no` 2 on the last element, and only one usage line.
5. Processes: `ps -o pid,ppid,command` while a session runs (npm global install), on macOS and Windows.
