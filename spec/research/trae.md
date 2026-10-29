# Trae native storage and limits

## Verified data, 2026-10-28

The macOS Trae CN installation's native `ModularData/ai-agent/database.db` was inspected read-only with its WAL.
The store uses SQLCipher 4 authenticated encryption. All decoding and SQLite queries remained in memory; no
conversation content, credentials or plaintext database was saved as a fixture. Test databases are synthetic.
International and SOLO discovery paths are supported, but this native check exercised macOS Trae CN only.

`chat_turn` supplies `session_id`, `turn_id`, `turn_status`, `context`, `deleted_at`, and integer-second
`created_at`/`updated_at`. The latter often equal the turn start, so are not completion timestamps.
Context has `chat_start_time`, `chat_end_time`, `workspace_folders`, model metadata under
`persist_user_message_context.model_info`, and `token_usage`:

```json
{
  "prompt_tokens": 48397,
  "completion_tokens": 4000,
  "total_tokens": 52397,
  "reasoning_tokens": 875,
  "cache_read_input_tokens": 47360,
  "cache_creation_input_tokens": 0,
  "prompt_tokens_total": 0,
  "completion_tokens_total": 0
}
```

These counters describe the final model request. In the inspected store, the whole-turn `*_tokens_total` fields
were zero throughout. Cached input and reasoning were already included in prompt and completion totals. The
example's whole turn lasted 264.532 seconds and included tools and earlier model calls; dividing 4000 by that
duration would not measure model speed. Some completed rows lack `chat_end_time` and cannot be assigned reliably
to a history completion date. Failed and canceled rows can have an end time and are excluded as well.

The adapter shows **partial request history, with TPS when independent request evidence agrees and marked estimates while generation is in progress**.
The database alone has no usable request duration. `chat_message_task` describes generated items and tools;
`TimingCost` first-token/server fields alone also do not measure complete generation. Existing pricing may
calculate an API-equivalent cost for known models in this subset; it does not represent a Trae subscription
charge or the full usage of a turn.

## Verified request timing

Further inspection of Trae CN 3.4.9 found optional `server_history_info.extra_info` fields identifying exact
provider usage: `exact_usage_v1=chat_completion` and
`exact_token_semantics_v1=provider_raw_usage_completion_includes_reasoning`, with decimal-string counters.
They are not assumed to exist in every version or account. The unique latest undeleted `history_v2` LLM row,
its associated server history, and the final `chat_turn` usage must agree on counts. The history identifier
then links to an `add_origin_history` log span; `update_history_by_server` is replayed history and is not used.

After that final anchor reconciles, earlier exact history rows can be counted independently of logs. They must share
the same turn, response message and nonempty client/server `agent_run_id`, have unique history identities, and fall
within that completed turn's timestamps. Their per-call `config_name` supplies the model. The final request retains
`trae:<session>:<turn>`; earlier calls use `trae:history:<session>:<historyID>`, so late metadata adds earlier calls
without counting the final request twice. Missing optional columns leave the final fallback available. A valid
exact usage row whose interval is ambiguous still counts tokens with unknown speed.

`logs/<launch>/Modular/ai_agent*.alaudalog` contains raw DEFLATE and concatenated CBOR inside an Alauda header.
The supported layout has one segment at offset 152. Span tag updates arrive separately and are merged. Events
belong to the nearest `call_server_generate_plan_item` ancestor: an outer wrapper may contain many requests.
A `create_chat_turn_with_id` span identifies the database turn beneath their common `do_chat` ancestor.
The latter may remain open after completion; its close is not required.

The measured interval is the unique HTTP Fetch send to uppercase streaming `DONE`, with a following `EOF`.
`send_streaming` closing only means response headers arrived. Model configuration, trace (when present),
TokenUsage marker, context update, origin history and a closed request span must corroborate the interval.
Retry/error during Fetch..DONE, multiple sends, overlapping or unresolved requests, mismatched identities,
deleted or missing final proof, and incomplete logs leave timing unknown. Post-DONE tool errors do not invalidate
the finished stream; traces are checked within Fetch..EOF. Auxiliary telemetry can repeat. Native origins can
precede DONE by milliseconds, context updates can follow EOF, and usage telemetry can arrive after tools.
The accepted corroboration windows are origin within 1 second of DONE, a closed context update by DONE+1 second,
and closed usage telemetry by DONE+5 seconds; only Fetch..DONE is used as the duration. See [the measurement contract](../METRICS.md)
for all gates. Both observed POST paths `/api/agent/v3/create_agent_task` and `/api/agent/v3/commit_toolcall_result` are supported.

In the native sample, a final request produced **4,000 output tokens** from HTTP send at
`1793172181112.218 ms` to `DONE` at `1793172207497.675 ms`: **26.385457 seconds, approximately 151.60 tok/s**.
The application's millisecond representation yields `26385 ms` and the same rounded rate. This includes
TTFT and transport overhead, excludes subsequent tools, and is not a pure decoder-throughput measurement.
The whole turn's 264.532 seconds must not be used as the denominator.

The previous final-request-only subset contained 132 requests (11,217,217 input / 116,086 output), with one
measured final request. The same native turn contains nine exact per-call rows. Eight have unambiguous intervals;
one 570-output-token call has two TimingCost records and overlapping request intervals, so its usage counts but
its speed remains unknown. Expanding the eight earlier history IDs, without repeating the final 4,000 tokens,
produces **140 requests, 11,528,525 input, 120,467 output, 10,378,008 cached input and 18,974 reasoning tokens**.
The measured subset is **eight requests, 7,811 output tokens over 59,787 ms**, flooring each endpoint before
subtraction as both production platforms do. Both native readers agree. This is a local snapshot, not a completeness
or speed-coverage claim for other Trae versions. The 131 older final requests have no matching retained turn logs.

## Live state and estimator

The shipped binary's serialized status enum is `in_progress/completed/failed/canceled/paused/queuing/unknown`.
Only a fresh exact-turn Fetch while the latest turn is `in_progress` may become generating; the status alone is
insufficient. A complete stream switches to tools, the next Fetch resumes generation, and terminal status/end time
or a closed `chat_turn_finish` forces idle. Identity conflicts, unresolved overlap, invalid/future timestamps and
more than 300 seconds without request activity produce idle. An old malformed row does not block a newer valid turn
when its recorded creation second proves it older; an unknown newer row does.

This uses the existing estimator: recent same-agent, same-model, then same-source measured speed. Estimated share
is separate from measured share, and no measured reference means no estimated rate. Lifecycle events never create
token counts or measured history. The active request can be recognized without any optional exact usage tables.

A subsequent native case exposed a cold-start gap: the previous measured response was over 64 minutes old, and
eight new requests completed inside a 74-second turn. Each exact usage row was persisted near its own DONE marker,
but waiting for the enclosing turn to complete left the live estimator without a reference throughout the turn.
The corrected reader supplies a separate, non-accounting reference pool from independently verified completed
requests in the current active turn. It retains the latest ten within 30 minutes and uses the shared estimator;
these samples do not increment usage or request totals, create completion events, or count twice when the turn
eventually finishes. A first request with no previous measured sample still has no estimated rate.

The native audit exercised completed recorded streams and their Fetch/DONE/tool ordering. Live lifecycle and
cancellation transitions were validated by shared synthetic records and real monitor/engine integration tests.
On 2026-10-29, a real ongoing macOS Trae CN generation also verified the corrected path: the isolated release
probe reported 100.796 output tok/s with estimated share 1, and the running app showed Trae generating at 101 tok/s.
This verifies live estimation, not that request's eventual measured average. Windows desktop UI remains unverified.

History is refreshed per native session. If Trae edits or deletes individual turns from a session that still
exists, the next successful scan replaces that session's partial aggregate and can reduce its totals. A session
that disappears entirely retains its last stored history under the existing history policy. This adapter is not
an immutable per-request billing ledger.

## Reader and discovery

An extended macOS run on 2026-10-29 exposed a refresh stall as native logs grew. Process sampling placed the live
monitor queue in Trae graph parsing, predominantly the retry-marker regular expression over large log messages.
The Swift reader now compiles that expression once with ICU while preserving retry word boundaries. Both live
monitors prepare Trae snapshots off their publishing loop, allow one pending read per source, and apply results
only on the owning monitor. Slow-reader regressions check that unrelated source updates and periodic snapshots
continue during a blocked read. Startup and command-line accounting retain their synchronous snapshot semantics.

Both platforms check the application data folders `Trae`, `Trae CN`, `TRAE SOLO`, and `TRAE SOLO CN`. The current
`ModularData/ai-agent/database.db` takes precedence over legacy `ModularData/ai-chat/database.db`; they are not
summed. A selected root replaces automatic discovery and accepts the app folder, containing database folder, or
database file. No plaintext sibling export is picked automatically.

Readers authenticate SQLCipher pages and apply only committed, checksummed WAL transactions. All file access is
read-only, snapshots are bounded, and source changes cause a retry or warning. No account secret is needed: the
supported encryption format uses Trae's shared application-format key. A different key or schema fails visibly.
System SQLite must expose `sqlite3_deserialize`; a Windows installation lacking it reports an unsupported source.
No system package, Trae configuration, log level or credential is changed.

Timing logs are read only beneath the same recognized application root. Symlinks and unsupported containers
are skipped. Selected snapshots are bounded to 64 files, 32 MiB compressed and 128 MiB expanded in aggregate;
projected graph caches reuse unchanged files and evict rotated files. Each read checks its own file snapshot.
Later appends do not invalidate an already stable completed prefix. Log-only arrivals refresh the same request
and persisted history without adding another request; missing evidence restores unknown timing while preserving
exact usage. Fresh request activity is projected separately and old completed logs cannot resurrect generation.

Native crypto is CommonCrypto on macOS and .NET cryptography on Windows. The independent Python reference uses
OpenSSL; regeneration needs a SQLite library exposing `SQLITE_FCNTL_RESERVE_BYTES` and OpenSSL. Tests include
encrypted snapshots, an actual valid WAL transaction followed by an uncommitted one, bad counters and HMAC tampering.
Fifty shared timing/accounting fixtures cover per-call expansion, stable final keys, equal counts with distinct
history IDs, missing/deleted/duplicate/run-mismatched records, retries, overlap, post-DONE errors, repeated telemetry,
model changes and zero-output usage. Twenty-two shared activity scenarios cover lifecycle, malformed-old/latest
rows, stale/future events and cancellations. Platform integration tests check marked estimates, no-reference
behavior, log-only changes, oversized logs, copied stores and persistent upgrades.
Twenty-four additional live-reference fixtures start without any completed-turn sample and cover the first request,
tool pause, next request, terminal transitions, invalid/missing metadata, duplicate identities, mismatched runs,
invalid creation times and incomplete/unsafe request proofs. The regression also exercises snapshot transitions
and reopening after completion so the reference pool cannot duplicate counted usage.

## References

- [Trae official log retrieval](https://docs.trae.cn/ide_get-logs-or-session-id)
- [SQLite WAL format](https://www.sqlite.org/fileformat2.html#walformat)
- [SQLCipher implementation](https://github.com/sqlcipher/sqlcipher/blob/master/src/sqlcipher.c)
- [TokenTracker native schema research](https://github.com/xiufengsun/TokenTracker/blob/main/src/lib/trae-db.js)
- [TokenTracker snapshot reader](https://github.com/xiufengsun/TokenTracker/blob/main/src/lib/trae-snapshot.js)
- [MIT attribution](trae-third-party.txt)

The third-party reader provided implementation guidance; local native inspection established the counter and
timing limitations. Automated tests and a cross-build do not establish Windows guest UI acceptance.
