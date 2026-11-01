# Devin — research notes

Devin CLI and Devin Desktop are closed-source Cognition clients. The CLI rules were checked against a real local
`sessions.db` (Devin CLI 3000.11.234) without retaining conversation content; Desktop's ACP shapes and locations were
checked against Devin's ACP protocol and the public tokscale reader (`junhoyeo/tokscale`, `crates/tokscale-core/src/
sessions/devin.rs`). Fixtures are hand-written and synthetic.

Sources:

- Devin documentation: installation, CLI troubleshooting and data/log locations (docs.devin.ai/cli).
- The local CLI database schema and assistant metadata: `message_nodes.chat_message.metadata.metrics`,
  `generation_model`, `request_id` and `finish_reason`; `message_nodes.created_at` is Unix seconds.
- tokscale's Devin reader for the two ACP usage shapes and the rule that the CLI database is authoritative when both
  cover one session.

## Verdict

One `devin` source can cover both clients without double counting. CLI assistant rows provide exact per-request usage
and timing. Desktop ACP `usage_update` events provide cumulative input/cache counts and per-step output counts, but no
per-request interval; their file-level aggregate therefore counts tokens without a measured speed.

| | Where | Confidence |
| --- | --- | --- |
| CLI request identity | assistant `metadata.request_id`, row id as fallback | real database |
| CLI tokens and model | `metadata.metrics`, `metadata.generation_model` | real database |
| CLI end and duration | row `created_at` (seconds), `metrics.total_time_ms` | real database |
| CLI input semantics | `input_tokens` excludes cache; `context_input_tokens` = input + cache read + cache creation | real database |
| CLI request start | end − `total_time_ms` | real database and public reader |
| Desktop usage | `usage_update._meta` `cognition.ai/*Tokens` | public reader |
| Desktop legacy usage | notification's embedded `metrics` | public reader |
| CLI/Desktop duplicate | unique `session_info_update.title` → CLI `sessions.title` | public reader |

The default Desktop `devin-cloud` agent writes no local usage; it cannot be measured from these files. Devin's
subscription and ACU accounting are not token usage and are never substituted for it.
