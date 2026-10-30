# Hermes Agent (Nous Research) — research notes

Read from the source of `NousResearch/hermes-agent` at `9b904dcc22c23fe8b82834339bbdd96495072e59` (main, 2026-10-26;
the latest release, v0.22.5, is `bf0203a794a0`, 2026-10-22). This is the AI agent, not the JavaScript engine of the
same name. Requested in GitHub issue #18. No real Hermes home was read and no Hermes code was run; the rules below are
derived from the writer's code and checked on synthetic fixtures (`spec/tools/hermes_fixtures.py`).

## Verdict

Feasible. Hermes writes no per-request transcript, but its log `logs/agent.log` carries one INFO line per model API
call of the agent loop with the call's tokens, model, provider and wall-clock duration, tagged with the session id.
That line is the source (METRICS 4.15). Everything else Hermes stores about usage is cumulative per session
(`state.db`), so it cannot give speed.

| | What the log gives | Agent Speed |
| --- | --- | --- |
| Measured | per API call: input (cache included), cache read, cache write, output, model, provider, end time (log time, ms), duration (`latency`, wall clock, 0.1 s) | tokens, speed (`start = end − latency`), cost |
| Estimated | phase inside a turn (tool runs are not logged per session), in-flight time | phase `generating` from turn start to turn end |
| Unknown | reasoning tokens, time to first token, effort, tool-call inputs, sub-agent parents, working directory | reasoning 0, no effort, no work signals, sub-agents without parent, no project |

## Summary

- **Home** (`hermes_constants.py`): `$HERMES_HOME`, else macOS `~/.hermes`, Windows `%LOCALAPPDATA%\hermes`
  (`%USERPROFILE%\AppData\Local\hermes` without `LOCALAPPDATA`). A desktop channel build appends a literal suffix from
  its bundle environment (`$HERMES_DATA_DIR_SUFFIX`, e.g. `~/.hermes-channel-build-canary`); a launched app never sees
  it. Named profiles live in `<root>/profiles/<name>` (names `[a-z0-9][a-z0-9_-]{0,63}`; `.deleted` holds tombstones),
  each a complete home with its own `logs/`. A `$HERMES_HOME` under the default home, or of the form
  `<root>/profiles/<name>`, has the root `~/.hermes` or `<root>`; any other value is a root of its own.
- **Logs** (`hermes_logging.py`): every process that builds an agent calls `setup_logging` (`agent/agent_init.py`), which
  adds `logs/agent.log` (all records at the configured level, default INFO), `errors.log` (WARNING+), and only in the
  gateway and GUI processes `gateway.log` / `gui.log` (component filters that exclude the agent loop). Records go
  through one queue and a `RedactingFormatter`. A process that serves several profiles routes each record to the
  `logs/agent.log` of the profile it belongs to.
- **Line format**: `%(asctime)s %(levelname)s%(session_tag)s %(name)s: %(message)s`, where `asctime` is Python's
  default `YYYY-MM-DD HH:MM:SS,mmm` in the **local time of the writing process** (`time.localtime`; no zone, no offset;
  Hermes never changes the converter) and `session_tag` is ` [<session id>]` when the writing thread has a session
  context, else empty. Multi-line messages continue on lines without the prefix.
- **Rotation**: `logging.max_size_mb` (default 5) and `logging.backup_count` (default 3; 0 also means 3). POSIX: the
  stdlib `RotatingFileHandler` (rename `agent.log.N-1` → `agent.log.N`, …, `agent.log` → `agent.log.1`, delete the
  oldest); every other process notices the new inode on its next record and reopens. Windows: `concurrent-log-handler`
  with a cross-process lock and the same renames (it closes the file after every write); when its lock cannot work,
  the stdlib handler without rollover (the file grows). With the defaults the set is `agent.log` plus `.1`–`.3`, about
  20 MB; busy gateways rotate within hours, so older lines disappear.
- **The API-call line** (`agent/turn_usage.py`), logger `agent.conversation_loop`, level INFO, written after each
  successful response of the agent loop, before its tools run:
  `API call #N: model=<model> provider=<provider or "unknown"> in=<prompt> out=<output> total=<prompt+output>
  latency=<seconds, %.1f>s[ cache=<read>/<prompt> (<pct>%)][ write=<cache write>][ id=<response id>][ upstream=<served by>]`.
  A response without usage writes `in=? out=? total=? latency=…s usage=unavailable`. The prefix through `latency=` is
  pinned by Hermes's own test, which says other parsers read it (`tests/agent/test_turn_usage_log_line.py`); the
  optional fields are appended in this order and only when present (`cache=` when read > 0, `write=` when > 0).
- **Tokens** (`agent/usage_pricing.py`, `CanonicalUsage`): `in` = `prompt_tokens` = uncached input + cache read +
  cache write; `out` = the provider's output tokens (reasoning included where the provider counts it there); `cache=`
  gives the cache read; `write=` the cache write. Reasoning tokens are counted (`canonical_usage.reasoning_tokens`) but
  only into `state.db`, never onto the line.
- **Timing**: `api_start_time = time.time()` once per loop iteration, before the retry loop (`agent/conversation_loop.py`),
  `api_duration = time.time() - api_start_time` when the response is checked (`agent/turn_response_check.py`). So
  `latency` covers request building, Hermes's own retries and their back-off, streaming and time to first token; it is
  a wall-clock difference (it can be negative after a clock step) printed with one decimal. The record's time is taken
  a few milliseconds later, in `record_response_usage`. There is no line when a request starts.
- **Turns**: `agent.turn_context` logs `conversation turn: session=<sid> model=<m> provider=<p> platform=<platform>
  history=<n> msg=<repr of the first 80 characters of the user message>[ origin=<fork kind>]` at the start of every
  turn, right after `set_session_context(session_id)`; `finalize_turn` logs `Turn ended: reason=<r> model=<m>
  api_calls=<n>/<max> … session=<sid>[ origin=<fork kind>]` (INFO, or WARNING as `Turn ended with pending tool result
  (agent may appear stuck). Turn ended: …`). Turns that end through an early return skip `finalize_turn`: a terminal
  API error logs ERROR `…API call failed after <n> retries. …` or `…Non-retryable client error: …`
  (`agent/turn_recovery.py`); the opt-in Codex app-server runtime, a preflight-compression timeout and a few
  recovery paths log nothing at the end. Tools log `tool <name> completed|failed|cancelled (<s>s…)` on the loop thread
  (sequential batches) or on worker threads without a session context (concurrent batches); there is no line when a
  tool starts.
- **Session ids** (`hermes_state_ids.py`): `YYYYMMDD_HHMMSS_<6 hex>` (CLI, TUI, agent), 8 hex for gateway keys,
  `cron_<job>_<stamp>`, `bg_…`, `api_…`, `run_<hex>`, `room_<hex>`, UUIDs (ACP, `/v1/responses`). The thread-local
  context is set at turn start and never cleared, so a thread keeps the id of the last turn it ran. Context
  compression rotates `agent.session_id` mid-turn; the thread keeps logging under the turn's original id until the
  next turn starts.
- **Sub-agents** (`tools/delegate_tool*.py`): `delegate_task` builds a child `AIAgent` with `platform="subagent"`, its own
  session id and `parent_session_id` (in `state.db` only), and runs it on a fresh daemon worker thread, so the child's
  lines carry the child's id and its turn line says `platform=subagent`. No log line links child and parent.
- **Forks**: background review and side-question turns reuse the parent's session id and tag their turn lines with
  `origin=`. Plugin side questions (`ctx.side_question`, `/btw`) run one request through `record_response_usage` without
  a turn: an API-call line with whatever session context the calling thread has.
- **N** in `API call #N` is `agent.session_api_calls`, a per-`AIAgent` counter: it restarts when a gateway builds a new
  agent for a message, and a side-question fork copies the parent's count. It is not unique per session; the session
  id, N and the line time together are.
- **Not logged**: auxiliary calls (titles, compression summaries, vision, web extraction, memory, auxiliary routing),
  responses refused by a content filter, truncated responses that are retried or continued, invalid responses that
  are retried, and the turns of the Codex app-server runtime (their usage goes to `state.db` only). Hermes's own
  totals (`state.db`, `/usage`) count the agent-loop calls and the Codex runtime turns, not the auxiliary calls.
- **`state.db`** (SQLite WAL, schema 32): `sessions` has cumulative counters (input, output, cache read, cache write,
  reasoning, cost, `api_call_count`), `cwd`, `parent_session_id`, `source`; `session_model_usage` is cumulative per
  session, model, billing route and task, with `first_seen` / `last_seen`. Messages carry no usage. No per-request
  usage or timing. Hermes defers its structural maintenance (VACUUM, generation swaps) while any foreign process holds
  the database or its WAL (`hermes_state_holders.py`), so for a long time Agent Speed never opened it (user decision,
  2026-10-28). Since 2026-10-30 it reads one value there, `sessions.model_config.reasoning_config`, in short reads
  (METRICS 4.15): still no project names and no sub-agent parents.
- **Effort**: `agent.reasoning_effort` (config) and `/reasoning` choose it; the log mentions it only when a fallback or
  model switch re-resolves it, without a session id. No per-request record, so no response has an effort; the session
  value and the config value give the agent's current one (EXECUTION-MODES, *Current values*).
- **Double counting**: Hermes's model calls exist only here. Turns run by the Codex app-server runtime
  (`model.openai_runtime: codex_app_server`, providers `openai` / `openai-codex`) are Codex's own (its rollouts under
  `CODEX_HOME`, default `~/.codex`) and write no API-call line; see "Codex runtime switch" below. ACP-backed providers (`copilot-acp`, `kiro-acp`,
  `grok-acp`, `cursor-acp`, …) report zero usage to Hermes (`in=0 out=0`), so their agents' own logs remain the only
  count. `hermes setup` can import Claude Code, Codex and OpenClaw sessions into `state.db`; that never writes
  API-call lines. Hermes run as an ACP agent (`hermes-acp`) by an editor or by OpenClaw logs its own calls here.
- **Process**: CPython 3.11–3.14. Installers and `hermes update` publish launchers in `<install>/.hermes/bin`
  (`<install>` = `<home>/hermes-agent` by default): POSIX a `/bin/sh` wrapper `exec <store python> -I -c "<bootstrap>"`
  whose script imports `hermes_bootstrap` and then `hermes_cli.main` (or `acp_adapter.entry` for `hermes-acp`); Windows a
  distlib launcher `hermes.exe` (or `hermes.cmd`) that starts `python.exe -I <…\hermes.exe>`. Desktop backends, gateway
  services (macOS: launchd job → `osascript` → `sh` → exec Python) and the TUI's local gateway run
  `python … -m hermes_cli.main …` or the same `-c` bootstrap. pip installs give a console script `bin/hermes`
  (`Scripts\hermes.exe`). `hermes_cli.main` only renames the process where `setproctitle` is installed (opt-in) or on
  Linux; the executable stays the Python interpreter. Hermes recognizes its own processes the same way
  (`hermes_state_holders.py`: executables `hermes`, `hermes-agent`, `hermes-acp`; Python modules `hermes_cli.main`,
  `acp_adapter`; scripts `hermes_cli/main.py`, `run_agent.py`).

## Codex runtime switch (`config.yaml`)

User decision (2026-10-28): read only the runtime key of Hermes's `config.yaml`; when it selects the Codex runtime,
the Hermes rows show no `generating` phase and no in-flight estimate, and Codex's own rollouts count the requests.

- **Key and values**: `model.openai_runtime`. `codex_app_server` selects the runtime; `auto` (the documented default)
  and anything else keep Hermes's runtime. Hermes reads it as `str(value or "").strip().lower()` and treats unknown
  values as `auto` (`get_current_runtime`); the overlay that applies it does the same comparison
  (`_maybe_apply_codex_app_server_runtime`). The `/codex-runtime` command accepts `on`/`codex`/`enable` and
  `off`/`default`/`disable`/`hermes` as synonyms but always persists `codex_app_server` or `auto`. YAML 1.1 booleans
  (`on`, `yes`) load as `True` and therefore mean `auto`. Only `model.openai_runtime` counts: the root-level key is not
  moved under `model` (`_normalize_root_model_keys` moves only `provider`, `base_url`, `context_length`).
- **Scope of the runtime**: applied once after provider resolution, only to `openai`, `openai-codex` and named custom
  providers that Codex knows; other providers keep Hermes's runtime even when the key is set. A Codex failure can
  activate a fallback provider that re-runs the same turn on Hermes's loop (which then logs its API calls and its turn
  end). A changed value applies from the next session ("Effective on next session — current cached agent keeps the
  prior runtime"). With the runtime active, `resolve_runtime_provider` logs one INFO line
  `model.openai_runtime=codex_app_server overrides the … runtime` per resolution, not per session or turn, so it is not
  used.
- **File**: `<HERMES_HOME>/config.yaml` (`get_config_path`, both in `hermes_constants.py` and `hermes_cli/config.py`);
  the config backend routes every read of a home's `config.yaml` to `FileBackend`, which opens it with
  `encoding="utf-8-sig"` and parses it with `hermes_yaml.safe_load` (ruamel, YAML 1.1, duplicate keys rejected). Each
  profile is a home of its own (`<root>/profiles/<name>/config.yaml`, next to its `logs/`); a profile does not inherit
  the default profile's file. Same layout on macOS (`~/.hermes`) and Windows (`%LOCALAPPDATA%\hermes`). Hermes writes
  it by atomic replacement with ruamel's round-trip dumper (block style, two-space indent), which keeps comments.
- **How Hermes loads it**: `load_config` deep-merges the file onto `DEFAULT_CONFIG`, expands `${VAR}` references, then
  overlays the managed scope (`$HERMES_MANAGED_DIR`, else `/etc/hermes`, when that folder exists: its `config.yaml`
  wins per leaf). Loads are cached by the file's stat signature. A file that does not parse is replaced by the last
  good configuration of the process, else the newest good backup in `backups/config/`, else the defaults.
  `HERMES_IGNORE_USER_CONFIG=1` ignores the file. Agent Speed reproduces none of this: it reads the one key with a
  tolerant line reader (METRICS 4.15 Codex runtime), so flow-style YAML (the JSON form some of Hermes's own evals
  write), `${VAR}` values, the managed overlay and the last-good fallback are approximations.
- **Where the Codex turns count**: the app-server subprocess gets Hermes's environment minus its secrets and no
  `CODEX_HOME` of its own ("codex's home is shared"), so its rollouts land in the Codex home Agent Speed already reads
  when Hermes runs with the same `CODEX_HOME`.

## Evidence

| Fact | Where (repository paths at the commit above) |
| --- | --- |
| Default home per platform, `HERMES_DATA_DIR_SUFFIX` | `hermes_constants.py` 57–64; `website/docs/reference/environment-variables.md` 883 |
| `$HERMES_HOME` wins; root of a profile home | `hermes_constants.py` 99–108, 194–200, 243–250, 302–315 |
| Profiles `<root>/profiles/<name>`, tombstones in `.deleted`, id grammar | `hermes_constants.py` 318, 345–356, 401 |
| Log files, levels, sizes, component filters | `hermes_logging.py` 1–6, 351–420; `hermes_cli/config_defaults.py` 2031–2035 |
| Line format and session tag | `hermes_logging.py` 107, 253–289 |
| Records routed to the owning profile's files | `hermes_logging.py` 336–348, 640–760; `agent/agent_init.py` 629–637 |
| POSIX rotation and reopen on a new inode | `hermes_logging.py` 461–612 |
| Windows `concurrent-log-handler`, fallback without rollover | `hermes_logging.py` 38–101 |
| `asctime` uses the default converter (no override anywhere) | `agent/redact.py` 1378–1382 (formatter subclass only redacts) |
| API-call line, usage-less variant, optional fields | `agent/turn_usage.py` 85–98, 198–217 |
| Prefix pinned for other parsers; field order; `upstream=` may contain spaces | `tests/agent/test_turn_usage_log_line.py` 1–55 |
| Hermes's own reader of the line (forensics) | `evals/postmortem/forensics/logcalls.py` 1–55 |
| `prompt_tokens = input + cache read + cache write`; usage shapes | `agent/usage_pricing.py` 66–81, 598–680 |
| `api_start_time` once per iteration, before retries | `agent/conversation_loop.py` 1730–1735 |
| `api_duration` measured when the response is checked; content-filter and length paths return before usage | `agent/turn_response_check.py` 119, 150–199 |
| Turn-start line, session context set just before | `agent/turn_context.py` 1078, 1096–1112 |
| Turn-end line (INFO / WARNING), `origin=` | `agent/turn_finalizer.py` 347–400, 711 |
| Early returns skip `finalize_turn` | `agent/conversation_loop.py` 1707–1764 |
| Terminal API error lines | `agent/turn_recovery.py` 1105, 1236–1240 |
| Tool completion lines, worker threads | `agent/tool_executor.py` 1112, 1347–1400 |
| Session id shapes | `hermes_state_ids.py` 17–60 |
| Sub-agents: `platform="subagent"`, own id, `parent_session_id`, worker thread | `tools/delegate_tool.py` 290–305; `tools/delegate_tool_child_run.py` 895–917 |
| Side questions record usage without a turn | `agent/side_question_request.py` 1–12, 171–186 |
| Codex app-server runtime: usage to `state.db` only, no line, no turn end | `agent/codex_runtime.py` 149–215; `agent/conversation_loop.py` 1707–1709; `hermes_cli/runtime_provider.py` 134, 340–349 |
| `model.openai_runtime`: values, trim + lowercase, unknown → `auto`, `/codex-runtime` synonyms and persistence | `hermes_cli/codex_runtime_switch.py` 13–18, 55–63, 77–85; `website/docs/user-guide/features/codex-app-server-runtime.md` 214, 228–232 |
| Runtime applied once after resolution, eligible providers, INFO line | `hermes_cli/runtime_provider.py` 339–350, 1025–1034, 1056–1057, 1073–1082 |
| Changed runtime effective on the next session | `hermes_cli/codex_runtime_switch.py` 190–200 |
| Codex failure → fallback turn on Hermes's loop | `agent/conversation_loop.py` 1589–1604 |
| `config.yaml` path per home, `utf-8-sig`, YAML loader | `hermes_constants.py` 1269–1271; `hermes_cli/config.py` 432–434; `hermes_cli/config_backend.py` 1–16, 82–86, 103–104, 146–157; `utils.py` 609–611; `hermes_yaml.py` 1–38 |
| Load pipeline: defaults merge, last-good fallback, managed overlay, cache by stat | `hermes_cli/config.py` 159–183, 2142–2217, 2247–2332 |
| Managed scope `/etc/hermes`, `$HERMES_MANAGED_DIR` | `hermes_cli/managed_scope.py` 31, 45–59, 107–109 |
| Only `provider`, `base_url`, `context_length` move from the root to `model` | `hermes_cli/config.py` 1642–1665 |
| YAML 1.1 booleans (`on`, `off`, `yes`) in config reads | `utils.py` 515–519 |
| Codex subprocess shares the Codex home (no `CODEX_HOME` override) | `agent/codex_runtime.py` 653–663; `agent/transports/codex_app_server.py` 90–109 |
| ACP providers report zero usage | `agent/acp_client.py` 388 |
| MoA folds advisor usage into the line; provider `moa` | `agent/turn_usage.py` 48–70, 101–106, 224–231 |
| `state.db` tables and counters | `hermes_state_common.py` 289, 461–501; `hermes_state_usage.py` 1–70 |
| Foreign holders defer maintenance | `hermes_state_holders.py` 425–440 |
| Launchers (`-I -c` bootstrap, distlib `hermes.exe`, entry points) | `hermes_cli/_launchers.py` 107–110, 445–536; `scripts/install.sh` 97 |
| Desktop backend runs `-m hermes_cli.main` through the installation launcher | `apps/desktop/electron/source-backend.ts` 26–60; `apps/desktop/electron/updater-process.ts` 36–73 |
| launchd gateway command | `hermes_cli/gateway_launchd.py` 215–252 |
| Process title only via `setproctitle` / Linux | `hermes_cli/main.py` 210–236 |
| Hermes's own process recognition | `hermes_state_holders.py` 36–38, 71–135 |
| Effort is not logged per request | `agent/chat_completion_helpers.py` 2013; `hermes_cli/cli_agent_setup_mixin.py` 367 |
