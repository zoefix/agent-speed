# Cursor CLI (`agent`) — research notes

Measured on 2026-11-10 with cursor-agent `2026.11.05-86047df` (macOS arm64, installed by `curl https://cursor.com/install`,
signed in with a Cursor account), model `Auto`, in print mode. The package's JavaScript was read as well. Requested in
GitHub issue #56; the earlier Cursor requests (#44, #54) were closed because the editor keeps no per-response usage.

## Verdict

Not feasible from local files. The CLI receives the token usage of every turn from Cursor's servers, but writes it to
no file: it only prints it in print mode (`result.usage` of `--output-format json` / `stream-json`) and passes it to
hooks. The chat store keeps the messages and the start and end time of every step, but no token counts, so Agent
Speed cannot compute a speed for it.

| | What the CLI keeps on disk | What it only shows while running |
| --- | --- | --- |
| Tokens | the context gauge (tokens in the context now, the window size, a breakdown) | per turn: input, output, cache read, cache write, reasoning (`result.usage`, hooks) |
| Time | per step: thinking, assistant text and tool calls with start and end in ms; turn start | per turn: `duration_ms`, `duration_api_ms` |
| Model | the resolved model inside the system prompt's fingerprint (`Auto` ran as `grok-4.5`) | `Auto` in the `init` event |

## Files

- **Config dir**: `$CURSOR_CONFIG_DIR`, else `$XDG_CONFIG_HOME/cursor`, else `~/.cursor`. **Data dir**:
  `$CURSOR_DATA_DIR`, else `~/.cursor`.
- **Chat store**: `<config dir>/chats/<md5 of the workspace path>/<chat id>/store.db`, plus `meta.json`
  (`schemaVersion`, `createdAtMs`, `updatedAtMs`, `cwd`, `hasConversation`). SQLite with two tables,
  `blobs(id TEXT PRIMARY KEY, data BLOB)` (content-addressed) and `meta(key, value)`; `meta` key `0` is hex-encoded
  JSON with `agentId`, `latestRootBlobId`, `name`, `mode` and `createdAt`. The blobs are JSON messages (`role`
  `system` / `user` / `assistant` / `tool`; assistant content parts `reasoning` and `text`; `providerOptions.cursor`
  holds the request id or the provider's message id) and protobuf messages: the root `ConversationStateStructure`
  and one `ConversationStep` per step.
- **ACP sessions** (`agent acp`, which T3 Code uses): `<config dir>/acp-sessions/<session id>/store.db` and
  `meta.json`, the same blob store (read from the code; no ACP session was run).
- **Transcript**: `<data dir>/projects/<workspace slug>/agent-transcripts/<chat id>/<chat id>.jsonl`, one line per
  user or assistant message (`role`, `message.content[].text`) and `{"type":"turn_ended","status":…}`. No times, no
  usage.
- Also written: `<data dir>/projects/<slug>/worker.log`, `repo.json`, `.workspace-trusted`, and the config files
  `cli-config.json`, `agent-cli-state.json`, `statsig-cache.json`. `~/.cursor/ai-tracking/ai-code-tracking.db` tracks
  lines of AI-written code (hashes, file, model), not tokens.

## Stored state (protobuf)

- `ConversationStateStructure`: field 5 `token_details` is `ConversationTokenDetails` (`used_tokens`, `max_tokens`,
  a breakdown by system prompt, tools, rules, skills, MCP, subagents, conversation): the context gauge, not usage.
  Field 26 `conversation_started_timestamp_ms`, 14 `turn_timings`, 8 `turns`.
- `ConversationStep`: 1 `assistant_message` (`AssistantMessage`: `text`, `started_at_ms`, `completed_at_ms`),
  2 `tool_call` (`started_at_ms` 59, `completed_at_ms` 60), 3 `thinking_message` (`ThinkingMessage`: `text`,
  `duration_ms`, `started_at_ms`, `completed_at_ms`). The last assistant text of both turns had a start but no end.
- `AgentConversationTurnStructure` 17 `run` (`TurnRunRecord`: `run_id`, `status`, `started_at_ms`, `finished_at_ms`).
- The code defines `ModelCallCompletedUpdate` (`model_call_id`, input, output, cache read, cache write, reasoning
  tokens), a streaming update for one model call. It is never persisted; print mode would echo it as
  `{"type":"usage","subtype":"model_call",…}` with `stream-json` or `--stream-partial-output`, but the server sent
  none in either run below.

## Measurement

Two turns in print mode (`agent -p --trust --mode ask --output-format stream-json "…"`) in an empty folder:

| Run | Turn | `result.usage` | `duration_ms` |
| --- | --- | --- | --- |
| 1 | about 200 words of text, no tools | input 9354, output 261, cache read 3968, cache write 0, reasoning 44 | 11333 |
| 2 | read a file and explain it: 7 tool calls over several model calls | input 11820, output 570, cache read 73600, cache write 0, reasoning 129 | 17251 |

Afterwards none of these numbers was in the two chat stores (every protobuf varint and every JSON blob, checked with
`spec/tools/cursor_cli_probe.py`; the context gauge's 15607 of run 2 is found, as a control), in `meta.json`, in the
transcripts or in any other file under `~/.cursor` and `~/.local/share/cursor-agent` that the runs changed.

## Why the remaining options are not used

- **Print-mode output**: it goes to the stdout of whoever started `agent -p`; Agent Speed reads files and cannot see it.
- **Hooks** (`afterAgentResponse`, `stop` in `hooks.json`): they get the turn's input, output and cache tokens, but
  Agent Speed would have to add a hook to Cursor's configuration, and the numbers are turn totals.
- **Turn totals** in general: a turn spans several model calls and the tool runs between them (run 2: 570 output
  tokens across seven tool calls in 17.3 s), so tokens ÷ turn time is not the model's speed.
- **Counting the stored text**: the text and reasoning are stored, but turning characters into tokens is a guess, and
  Agent Speed reports measured tokens only.
- **Cursor's servers** (the account's usage page): needs the account and the network, against the local-only rule.

## Re-evaluating

After a Cursor CLI update: run `agent -p --output-format json "…"` in an empty folder, take `usage.outputTokens` and
`usage.inputTokens` from the output, and run `python3 spec/tools/cursor_cli_probe.py <the new chat's store.db>
<output> <input>`. If the numbers appear, or `ConversationStep` / `AgentConversationTurnStructure` gain a usage field,
support becomes possible; per-model-call usage with the step times above would give the speed.
