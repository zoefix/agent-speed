#!/usr/bin/env python3
"""OpenClaw agent-database fixtures (METRICS 4.14), used by generate_fixtures.py.

  python3 spec/tools/openclaw_fixtures.py OUT_DIR

OUT_DIR/openclaw plays the home folder: `.openclaw` is the default state folder and `.openclaw-work` a named profile;
`.openclaw-default` and `.openclaw-bad.name` are not profiles. Databases are laid out and filled as OpenClaw 2026.10
(agent schema 37, WAL, zstd rows) and 2026.8.1 (schema 19, no zstd columns) write them, reduced to the columns
Agent Speed reads; `legacy` is an agent database of 2026.7 (schema 1, no session tables). The Codex rollout lies in the
agent-scoped Codex home of the `main` agent (a Codex root, METRICS 2). The returned expectations have the shape of
spec/fixtures/parser-expected.json (`order`, `files`, `mtimes` for the Codex rollout and the databases) plus
`databases` (per database: per transcript window, keyed by agent id relative to OUT_DIR), `dbOrder`, `discover`,
`codexDiscover` and `traces` (phase after a given row). Expected values are written by hand from METRICS 4.7, 4.13
and 4.14, independently of reference.py.
"""
import json
import os
import sqlite3
import subprocess
import sys

from antigravity_fixtures import close_cleanly
from omp_fixtures import iso, ms

DAY = "2026-10-20"
NEXT_DAY = "2026-10-21"
HOME = "/Users/dev"
WORKSPACE = HOME + "/.openclaw/workspace"
RELAY = HOME + "/projects/relay"
MAIN_DB = "openclaw/.openclaw/agents/main/agent/openclaw-agent.sqlite"
OPS_DB = "openclaw/.openclaw-work/agents/ops/agent/openclaw-agent.sqlite"
LEGACY_DB = "openclaw/.openclaw/agents/legacy/agent/openclaw-agent.sqlite"
CODEX_HOME = "openclaw/.openclaw/agents/main/agent/codex-home"

OPUS = ("anthropic-messages", "anthropic", "claude-opus-5-5")
SONNET = ("anthropic-messages", "anthropic", "claude-sonnet-5-5")
GPT = ("openai-responses", "openai", "gpt-5.5")

SCHEMA_2026_10 = """
CREATE TABLE schema_meta (meta_key TEXT NOT NULL PRIMARY KEY, role TEXT NOT NULL, schema_version INTEGER NOT NULL,
  agent_id TEXT, app_version TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL) STRICT;
CREATE TABLE session_nodes (session_key TEXT NOT NULL PRIMARY KEY, current_session_id TEXT NOT NULL,
  entry_json TEXT NOT NULL, updated_at INTEGER NOT NULL, parent_session_key TEXT, spawned_by TEXT) STRICT;
CREATE TABLE session_windows (session_id TEXT NOT NULL PRIMARY KEY, session_key TEXT NOT NULL,
  previous_session_id TEXT, reason TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL,
  transcript_updated_at INTEGER DEFAULT NULL, acp_owned INTEGER NOT NULL DEFAULT 0, model_provider TEXT, model TEXT,
  agent_harness_id TEXT, parent_session_key TEXT, spawned_by TEXT, display_name TEXT) STRICT;
CREATE TABLE transcript_events (session_id TEXT NOT NULL, seq INTEGER NOT NULL, event_json TEXT,
  created_at INTEGER NOT NULL, event_zstd BLOB, event_utf8_bytes INTEGER, navigation_json TEXT,
  PRIMARY KEY (session_id, seq)) STRICT;
CREATE TABLE transcript_rewrite_watermarks (session_id TEXT NOT NULL PRIMARY KEY, generation TEXT NOT NULL,
  updated_at INTEGER NOT NULL) STRICT;
CREATE TABLE trajectory_runtime_events (session_id TEXT NOT NULL, seq INTEGER NOT NULL, run_id TEXT,
  event_json TEXT NOT NULL, created_at INTEGER NOT NULL, event_utf8_bytes INTEGER, event_payload BLOB, event_codec TEXT,
  PRIMARY KEY (session_id, seq)) STRICT;
CREATE TABLE auth_profile_store (store_key TEXT NOT NULL PRIMARY KEY, value_json TEXT NOT NULL) STRICT;
"""

SCHEMA_2026_8 = """
CREATE TABLE schema_meta (meta_key TEXT NOT NULL PRIMARY KEY, role TEXT NOT NULL, schema_version INTEGER NOT NULL,
  agent_id TEXT, app_version TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL) STRICT;
CREATE TABLE session_nodes (session_key TEXT NOT NULL PRIMARY KEY, current_session_id TEXT NOT NULL,
  entry_json TEXT NOT NULL, updated_at INTEGER NOT NULL, parent_session_key TEXT, spawned_by TEXT) STRICT;
CREATE TABLE session_windows (session_id TEXT NOT NULL PRIMARY KEY, session_key TEXT NOT NULL,
  previous_session_id TEXT, reason TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL,
  transcript_updated_at INTEGER DEFAULT NULL, acp_owned INTEGER NOT NULL DEFAULT 0, model_provider TEXT, model TEXT,
  agent_harness_id TEXT, parent_session_key TEXT, spawned_by TEXT, display_name TEXT) STRICT;
CREATE TABLE transcript_events (session_id TEXT NOT NULL, seq INTEGER NOT NULL, event_json TEXT NOT NULL,
  created_at INTEGER NOT NULL, PRIMARY KEY (session_id, seq)) STRICT;
CREATE TABLE transcript_rewrite_watermarks (session_id TEXT NOT NULL PRIMARY KEY, generation TEXT NOT NULL,
  updated_at INTEGER NOT NULL) STRICT;
CREATE TABLE trajectory_runtime_events (session_id TEXT NOT NULL, seq INTEGER NOT NULL, run_id TEXT,
  event_json TEXT NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (session_id, seq)) STRICT;
"""

SCHEMA_2026_7 = """
CREATE TABLE schema_meta (meta_key TEXT NOT NULL PRIMARY KEY, role TEXT NOT NULL, schema_version INTEGER NOT NULL,
  agent_id TEXT, app_version TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL) STRICT;
CREATE TABLE cache_entries (cache_key TEXT NOT NULL PRIMARY KEY, value_json TEXT NOT NULL) STRICT;
CREATE TABLE auth_profile_store (store_key TEXT NOT NULL PRIMARY KEY, value_json TEXT NOT NULL) STRICT;
"""


def usage(inp, out, cache_read, cache_write, cache_write_1h=None, reasoning=None, unavailable=False):
    u = {"input": inp, "output": out, "cacheRead": cache_read, "cacheWrite": cache_write}
    if cache_write_1h is not None:
        u["cacheWrite1h"] = cache_write_1h
    if reasoning is not None:
        u["reasoningTokens"] = reasoning
    u["totalTokens"] = inp + out + cache_read + cache_write
    u["cost"] = {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0, "total": 0}
    if unavailable:
        u["usageReport"] = {"state": "unavailable"}
    return u


def text(value):
    return {"type": "text", "text": value}


def tool_call(call_id, name, arguments, asynchronous=False):
    block = {"type": "toolCall", "id": call_id, "name": name, "arguments": arguments}
    if asynchronous:
        block["async"] = True
    return block


def zstd(raw):
    out = subprocess.run(["zstd", "-q", "-1", "--check", "-c"], input=raw, capture_output=True, check=True)
    return out.stdout


def brotli_stream(raw):
    """Compressed as OpenClaw's trajectory writer does: quality 3, text mode, a 256 KiB window."""
    try:
        import brotli
    except ImportError:
        return subprocess.run(["brotli", "-q", "3", "-w", "18", "-c"], input=raw, capture_output=True,
                              check=True).stdout
    return brotli.compress(raw, mode=brotli.MODE_TEXT, quality=3, lgwin=18)


class Window:
    """The rows of one transcript window. Entry ids are a 4-character prefix and a counter."""

    def __init__(self, session_id, key, created, cwd, prefix, harness=None, acp=0, spawned_by=None,
                 parent_key=None, reason="initial", version=4):
        self.session_id, self.key, self.created, self.prefix = session_id, key, created, prefix
        self.harness, self.acp, self.spawned_by, self.parent_key, self.reason = harness, acp, spawned_by, \
            parent_key, reason
        self.rows, self.count, self.last_id, self.updated = [], 0, None, created
        self.events = []
        self.raw({"type": "session", "version": version, "id": session_id, "timestamp": iso(created), "cwd": cwd},
                 created)

    def seq(self):
        return len(self.rows)

    def raw(self, value, created, compress=False, blob=None, size=None):
        """A row: an entry (JSON text, or a zstd frame when `compress`), or a literal text or frame."""
        if blob is not None:
            self.rows.append((None, created, blob, size))
        elif isinstance(value, str):
            self.rows.append((value, created, None, None))
        else:
            text_value = json.dumps(value, separators=(",", ":"), ensure_ascii=False)
            if compress:
                data = text_value.encode("utf-8")
                assert len(data) >= 1024
                self.rows.append((None, created, zstd(data), len(data)))
            else:
                self.rows.append((text_value, created, None, None))
        self.updated = max(self.updated, created)
        return self.seq()

    def entry(self, entry_type, when, compress=False, **fields):
        self.count += 1
        entry_id = "%s%04x" % (self.prefix, self.count)
        row = {"type": entry_type, "id": entry_id, "parentId": self.last_id, "timestamp": iso(when)}
        row.update(fields)
        self.last_id = entry_id
        self.raw(row, when, compress)
        return entry_id, row

    def user(self, when, value):
        return self.entry("message", when, message={"role": "user", "content": [text(value)], "timestamp": when})[0]

    def tool_result(self, when, call_id, name, value, is_error=False):
        msg = {"role": "toolResult", "toolCallId": call_id, "toolName": name, "content": [text(value)],
               "isError": is_error, "timestamp": when}
        return self.entry("message", when, message=msg)[0]

    def assistant(self, when, route, content, use, stop, started, compress=False, **extra):
        api, provider, model = route
        msg = {"role": "assistant", "content": content}
        if api is not None:
            msg["api"] = api
        msg.update({"provider": provider, "model": model, "usage": use, "stopReason": stop, "timestamp": started})
        msg.update(extra)
        return self.entry("message", when, compress, message=msg)

    def copy(self, rows):
        for row in rows:
            self.raw(row, ms("13:20:00.001"))
            self.last_id = row["id"]

    def event(self, kind, when, run, data, compress=False, payload=None, size=None):
        """A row of trajectory_runtime_events with the recorder's envelope: plain JSON, a Brotli stream of it, or a
        literal `payload` with the declared `size`."""
        line = json.dumps({"traceSchema": "openclaw-trajectory", "schemaVersion": 1, "traceId": self.session_id,
                           "source": "runtime", "type": kind, "ts": iso(when), "seq": len(self.events) + 1,
                           "sourceSeq": len(self.events) + 1, "sessionId": self.session_id, "sessionKey": self.key,
                           "runId": run, "workspaceDir": WORKSPACE, "data": data},
                          separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        if payload is not None:
            self.events.append((run, "", when, size, payload, "br"))
        elif compress:
            assert len(line) >= 1024
            self.events.append((run, "", when, len(line), brotli_stream(line), "br"))
        else:
            assert len(line) < 1024 or self.plain_only
            self.events.append((run, line.decode("utf-8"), when, len(line), None, None))
        return len(self.events)

    plain_only = False

    def attempt(self, when, run, think, fast=False, compress=True, model=True):
        """What one attempt writes first: `session.started`, then `trace.metadata` with the run's redacted snapshot."""
        self.event("session.started", when, run, {"trigger": "user", "toolCount": 41, "clientToolCount": 0})
        data = {"harness": {"type": "openclaw"}}
        if model:
            data["model"] = {"provider": "anthropic", "name": "claude-opus-5-5", "api": "anthropic-messages",
                             "fastMode": fast, "thinkLevel": think, "reasoningLevel": "off"}
        else:
            data.update(truncated=True, reason="trajectory-event-size-limit", droppedFields=["harness", "model"])
        if compress or self.plain_only:
            data["config"] = {"redacted": {"note": "(redacted configuration) " * 60}}
        return self.event("trace.metadata", when + 1, run, data, compress)


def write_database(path, schema, windows, journal_wal):
    for stale in (path, path + "-wal", path + "-shm"):
        if os.path.exists(stale):
            os.remove(stale)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    db = sqlite3.connect(path)
    if journal_wal:
        db.execute("PRAGMA journal_mode=WAL")
    db.executescript(schema)
    db.execute("INSERT INTO schema_meta VALUES (?,?,?,?,?,?,?)",
               ("agent", "agent", 37 if journal_wal else 19, os.path.basename(os.path.dirname(os.path.dirname(path))),
                "2026.10.7" if journal_wal else "2026.8.1", ms("12:00:00.000"), ms("12:00:00.000")))
    zstd_columns = journal_wal
    for w in windows:
        db.execute("INSERT INTO session_windows (session_id, session_key, reason, created_at, updated_at, "
                   "transcript_updated_at, acp_owned, agent_harness_id, parent_session_key, spawned_by) "
                   "VALUES (?,?,?,?,?,?,?,?,?,?)",
                   (w.session_id, w.key, w.reason, w.created, w.updated, w.updated, w.acp, w.harness, w.parent_key,
                    w.spawned_by))
        db.execute("INSERT INTO session_nodes VALUES (?,?,?,?,?,?)",
                   (w.key, w.session_id, json.dumps({"sessionId": w.session_id}), w.updated, w.parent_key,
                    w.spawned_by))
        for index, (run, line, created, size, payload, codec) in enumerate(w.events, start=1):
            if zstd_columns:
                db.execute("INSERT INTO trajectory_runtime_events VALUES (?,?,?,?,?,?,?,?)",
                           (w.session_id, index, run, line, created, size, payload, codec))
            else:
                db.execute("INSERT INTO trajectory_runtime_events VALUES (?,?,?,?,?)",
                           (w.session_id, index, run, line, created))
        for index, (event_json, created, blob, size) in enumerate(w.rows, start=1):
            if zstd_columns:
                db.execute("INSERT INTO transcript_events (session_id, seq, event_json, created_at, event_zstd, "
                           "event_utf8_bytes, navigation_json) VALUES (?,?,?,?,?,?,?)",
                           (w.session_id, index, event_json, created, blob, size,
                            None if blob is None else '{"version":1}'))
            else:
                db.execute("INSERT INTO transcript_events VALUES (?,?,?,?)", (w.session_id, index, event_json, created))
    if "auth_profile_store" in schema:
        db.execute("INSERT INTO auth_profile_store VALUES (?,?)", ("profiles", '{"note":"fixture, never read"}'))
    db.commit()
    if journal_wal:
        close_cleanly(db, path)
    else:
        db.close()
    with open(path, "rb") as fh:
        header = fh.read(20)
    assert (header[18], header[19]) == ((2, 2) if journal_wal else (1, 1)), path


def resp(key, start, end, inp, cached, cache_write, out, reasoning, model, provider, cache_write_1h=0, first=None):
    return {"key": key, "startMs": start, "endMs": end, "firstMs": first, "input": inp, "cachedInput": cached,
            "cacheWriteInput": cache_write, "cacheWrite1hInput": cache_write_1h, "output": out,
            "reasoning": reasoning, "model": model, "provider": provider, "fast": False, "final": True}


def key(entry_id, stamp):
    return "openclaw:%s:%d" % (entry_id, stamp)


def attributed(value, evidence):
    return {"mode": value, "evidence": evidence}


def recorded(value):
    return attributed(value, "recorded")


FAST = attributed("fast", "selected")


def no_modes(responses):
    return dict(effort=None, speed=None, responseModes={r["key"]: {"effort": None, "speed": None} for r in responses})


def agent(db_rel, window):
    return "%s#%s" % (db_rel, window.session_id)


def write_fixtures(out_dir):
    def p(rel):
        full = os.path.join(out_dir, *rel.split("/"))
        os.makedirs(os.path.dirname(full), exist_ok=True)
        return full

    databases, traces, mtimes = {MAIN_DB: {}, OPS_DB: {}, LEGACY_DB: {"unsupported": True}}, [], {}

    def expect(db_rel, window, modes=None, effort=None, speed=None, **kw):
        """`modes`: (effort, speed) per position in `responses`, for the responses that have one."""
        block = no_modes(kw["responses"])
        for index, (known_effort, known_speed) in (modes or {}).items():
            block["responseModes"][kw["responses"][index]["key"]] = {"effort": known_effort, "speed": known_speed}
        kw.update(block, effort=effort, speed=speed)
        databases[db_rel][agent(db_rel, window)] = dict(source="openclaw", **kw)

    def trace(db_rel, window, seq, phase, in_flight=None):
        traces.append(dict(database=db_rel, window=window.session_id, afterSeq=seq, phase=phase,
                           inFlightStartMs=in_flight))

    # ---- main (2026.10): an embedded window with every record kind the runtime writes.
    w1 = Window("0f1c2d3e-0001-4a00-8000-00000000c001", "agent:main:main", ms("13:00:00.000"), WORKSPACE, "a1f0",
                harness="openclaw")
    w1.entry("model_change", ms("13:00:00.010"), provider="anthropic", modelId="claude-opus-5-5")         # 2
    w1.entry("thinking_level_change", ms("13:00:00.011"), thinkingLevel="high")                           # 3
    w1.entry("custom_message", ms("13:00:04.990"), customType="openclaw.runtime-context",
             content="Runtime context", display=False)                                                    # 4
    w1.user(ms("13:00:05.000"), "Split the queue from the dispatcher")                                    # 5
    a1, a1_row = w1.assistant(ms("13:00:11.300"), OPUS,
                              [{"type": "thinking", "thinking": "Find the queue first. " * 70},
                               tool_call("toolu_q1", "read", {"path": "src/queue.ts"}),
                               tool_call("toolu_q2", "grep", {"pattern": "dispatch("})],
                              usage(3, 420, 0, 18000, cache_write_1h=18000), "toolUse", ms("13:00:05.010"),
                              compress=True)                                                              # 6 zstd row
    t1_row = w1.entry("message", ms("13:00:11.900"), message={
        "role": "toolResult", "toolCallId": "toolu_q1", "toolName": "read", "content": [text("export class Q {}")],
        "isError": False, "timestamp": ms("13:00:11.900")})[1]                                            # 7
    t2_row = w1.entry("message", ms("13:00:12.400"), message={
        "role": "toolResult", "toolCallId": "toolu_q2", "toolName": "grep", "content": [text("src/run.ts:41")],
        "isError": False, "timestamp": ms("13:00:12.400")})[1]                                            # 8
    a2, a2_row = w1.assistant(ms("13:00:16.520"), OPUS, [text("Queue moved to src/queue.ts.")],
                              usage(5, 260, 18000, 600), "stop", ms("13:00:12.405"))                     # 9
    user_row = json.loads(w1.rows[4][0])
    w1.user(ms("13:01:00.000"), "Spawn a reviewer and keep going")                                        # 10
    w1.assistant(ms("13:01:02.000"), OPUS, [tool_call("y1", "sessions_spawn", {"task": "review"}, True)],
                 usage(0, 0, 0, 0), "toolUse", ms("13:01:00.006"), turnId="turn-y")                       # 11 prefix
    w1.tool_result(ms("13:01:03.000"), "y1", "sessions_spawn", "accepted")                                # 12
    p2, _ = w1.assistant(ms("13:01:06.000"), OPUS, [text("Spawned the reviewer.")], usage(4, 300, 18600, 0),
                         "stop", ms("13:01:00.006"), turnId="turn-y")                                     # 13 terminal
    w1.user(ms("13:02:00.000"), "Stop")                                                                   # 14
    w1.assistant(ms("13:02:03.000"), OPUS, [text("Partial")], usage(0, 40, 0, 0, unavailable=True), "aborted",
                 ms("13:02:00.006"))                                                                      # 15 unavailable
    w1.user(ms("13:03:00.000"), "Run the tests")                                                          # 16
    e1, _ = w1.assistant(ms("13:03:01.200"), OPUS, [], usage(10, 0, 18600, 0), "error", ms("13:03:00.006"),
                         errorMessage="overloaded_error")                                                 # 17 error
    w1.user(ms("13:04:00.000"), "Ask Claude Code to refactor run.ts")                                     # 18
    w1.assistant(ms("13:04:30.005"), ("cli", "claude-cli", "claude-opus-5-5"), [text("Refactored.")],
                 usage(3, 500, 30000, 1200), "stop", ms("13:04:30.000"))                                  # 19 mirror
    w1.user(ms("13:05:00.000"), "Ask Gemini for a second opinion")                                        # 20
    g1, _ = w1.assistant(ms("13:05:20.004"), ("cli", "google-gemini-cli", "gemini-3-pro"),
                         [text("Looks fine.")], usage(1000, 200, 3000, 0), "stop", ms("13:05:20.000"))    # 21 untimed
    w1.raw('{"type":"message","id":', ms("13:05:30.000"))                                                 # 22 bad JSON
    w1.raw(None, ms("13:05:31.000"), blob=b"\x28\xb5\x2f\xfd\x00broken frame", size=2000)              # 23 bad zstd
    w1.user(ms("13:06:00.000"), "Next: the dispatcher")                                                   # 24
    # Its attempts. The trace row sits right after `session.started`; the rows with the conversation are never read.
    conversation = {"finalPromptText": "Split the queue from the dispatcher. " * 60}
    t1 = w1.attempt(ms("13:00:04.000"), "run-1", "high")                                                  # 1, 2
    w1.event("prompt.submitted", ms("13:00:05.000"), "run-1", conversation, compress=True)                # 3
    w1.event("model.completed", ms("13:00:17.000"), "run-1", dict(conversation, messagesSnapshot=[]), compress=True)  # 4
    w1.event("session.ended", ms("13:00:17.100"), "run-1", {"status": "success"})                          # 5
    t2 = w1.attempt(ms("13:01:00.000"), "run-2", "xhigh", fast=True)                                      # 6, 7
    w1.event("prompt.submitted", ms("13:01:00.002"), "run-2", conversation, compress=True)                # 8
    t3 = w1.attempt(ms("13:03:00.000"), "run-3", "adaptive", compress=False)                              # 9, 10
    t4 = w1.attempt(ms("13:05:00.000"), "run-4", None, compress=False, model=False)                       # 11, 12
    t5 = w1.attempt(ms("13:05:59.000"), "run-5", "max", fast=True)                                        # 13, 14
    w1.event("session.started", ms("13:06:00.100"), "run-6", {"trigger": "user"})                         # 15
    t6 = w1.event("trace.metadata", ms("13:06:00.101"), "run-6", {}, payload=b"\x1b\xff not a brotli stream", size=64)  # 16
    w1.event("session.started", ms("13:06:00.200"), "run-7", {"trigger": "user"})                         # 17
    good = brotli_stream(b'{"type":"trace.metadata","data":{"model":{"thinkLevel":"low"}}}')
    t7 = w1.event("trace.metadata", ms("13:06:00.201"), "run-7", {}, payload=good, size=9)                # 18 wrong size
    w1.event("session.started", ms("13:06:00.300"), "run-8", {"trigger": "user"})                         # 19
    t8 = w1.event("context.compiled", ms("13:06:00.301"), "run-8", conversation, compress=True)           # 20 not a trace
    w1.event("session.started", ms("13:06:00.400"), "run-9", {"trigger": "user"})                         # 21
    whole = brotli_stream(json.dumps({"type": "trace.metadata", "pad": "x" * 2000}).encode("utf-8"))
    t9 = w1.event("trace.metadata", ms("13:06:00.401"), "run-9", {}, payload=whole[:len(whole) // 2], size=2030)  # 22 cut
    trace_rows = {w1.session_id: [t1, t2, t3, t4, t5, t6, t7, t8, t9]}
    w1_responses = [
        resp(key(a1, ms("13:00:05.010")), ms("13:00:05.010"), ms("13:00:11.300"), 18003, 0, 18000, 420, 0,
             "claude-opus-5-5", "anthropic", cache_write_1h=18000),
        resp(key(a2, ms("13:00:12.405")), ms("13:00:12.405"), ms("13:00:16.520"), 18605, 18000, 600, 260, 0,
             "claude-opus-5-5", "anthropic"),
        resp(key(p2, ms("13:01:00.006")), ms("13:01:00.006"), ms("13:01:06.000"), 18604, 18600, 0, 300, 0,
             "claude-opus-5-5", "anthropic"),
        resp(key(e1, ms("13:03:00.006")), None, ms("13:03:01.200"), 18610, 18600, 0, 0, 0,
             "claude-opus-5-5", "anthropic"),
        resp(key(g1, ms("13:05:20.000")), None, ms("13:05:20.004"), 4000, 3000, 0, 200, 0,
             "gemini-3-pro", "google-gemini-cli"),
    ]
    expect(MAIN_DB, w1, project="workspace", model="gemini-3-pro", isSubagent=False, parentId=None,
           phase="generating", inFlightStartMs=ms("13:06:00.000"), lastLineMs=ms("13:06:00.000"), malformedLines=2,
           responses=w1_responses, effort=recorded("max"), speed=FAST,
           modes={0: (recorded("high"), None), 1: (recorded("high"), None), 2: (recorded("xhigh"), FAST),
                  3: (recorded("adaptive"), None)})
    for seq, phase, in_flight in ((4, "idle", None), (5, "generating", ms("13:00:05.000")), (6, "tools", None),
                                  (7, "tools", None), (8, "generating", ms("13:00:12.400")), (9, "idle", None),
                                  (11, "tools", None), (12, "generating", ms("13:01:03.000")), (13, "idle", None),
                                  (15, "idle", None), (18, "generating", ms("13:04:00.000")), (19, "idle", None),
                                  (21, "idle", None)):
        trace(MAIN_DB, w1, seq, phase, in_flight)

    # ---- A sessions_spawn child in the same database.
    w2 = Window("0f1c2d3e-0002-4a00-8000-00000000c002", "agent:main:subagent:5d1e0c9a-77b2-4f3e-9a10-2b6c8d4e0f11",
                ms("13:00:20.000"), WORKSPACE, "b2e1", harness="openclaw", spawned_by="agent:main:main")
    w2.user(ms("13:00:20.100"), "Review src/queue.ts")                                                    # 2
    r1, _ = w2.assistant(ms("13:00:29.300"), SONNET, [text("Looks good; one nit.")], usage(2400, 310, 0, 0),
                         "stop", ms("13:00:20.106"))                                                      # 3
    w2_responses = [resp(key(r1, ms("13:00:20.106")), ms("13:00:20.106"), ms("13:00:29.300"), 2400, 0, 0, 310, 0,
                         "claude-sonnet-5-5", "anthropic")]
    trace_rows[w2.session_id] = [w2.attempt(ms("13:00:19.900"), "run-s", "ultra", fast=True, compress=False)]
    expect(MAIN_DB, w2, project="workspace", model="claude-sonnet-5-5", isSubagent=True,
           parentId=agent(MAIN_DB, w1), phase="idle", inFlightStartMs=None, lastLineMs=ms("13:00:29.300"),
           malformedLines=0, responses=w2_responses, effort=recorded("ultra"), speed=FAST,
           modes={0: (recorded("ultra"), FAST)})

    # ---- A Codex app-server turn: the mirror counts nothing (the rollout below has the requests).
    w4 = Window("0f1c2d3e-0004-4a00-8000-00000000c004", "agent:main:codex1", ms("13:10:00.000"), WORKSPACE, "c3d2",
                harness="codex")
    w4.user(ms("13:10:00.100"), "Fix the flaky test")                                                     # 2
    w4.assistant(ms("13:10:40.005"), ("openai-chatgpt-responses", "openai", "gpt-5.5"),
                 [tool_call("c1", "shell", {"command": "npm test"})], usage(0, 0, 0, 0), "toolUse",
                 ms("13:10:19.900"), __openclaw={"mirrorOrigin": "codex-app-server", "runId": "run-c"})   # 3
    w4.tool_result(ms("13:10:40.006"), "c1", "shell", "1 failing")                                        # 4
    w4.assistant(ms("13:10:40.010"), ("openai-chatgpt-responses", "openai", "gpt-5.5"), [text("Fixed.")],
                 usage(500, 510, 21000, 0, reasoning=120), "stop", ms("13:10:40.000"),
                 __openclaw={"mirrorOrigin": "codex-app-server", "runId": "run-c", "runTerminal": True})  # 5
    expect(MAIN_DB, w4, project="workspace", model=None, isSubagent=False, parentId=None, phase="idle",
           inFlightStartMs=None, lastLineMs=ms("13:10:40.010"), malformedLines=0, responses=[])
    trace(MAIN_DB, w4, 2, "idle")

    # ---- A classic ACP session (acp_owned): mirrors only.
    w5 = Window("0f1c2d3e-0005-4a00-8000-00000000c005", "agent:main:acp:claude:1", ms("13:15:00.000"), WORKSPACE,
                "d4c3", acp=1)
    w5.user(ms("13:15:00.100"), "Summarize the PR")                                                       # 2
    w5.assistant(ms("13:15:30.004"), OPUS, [text("Three changes.")], usage(10, 700, 25000, 0), "stop",
                 ms("13:15:30.000"))                                                                      # 3
    expect(MAIN_DB, w5, project="workspace", model=None, isSubagent=False, parentId=None, phase="idle",
           inFlightStartMs=None, lastLineMs=ms("13:15:30.004"), malformedLines=0, responses=[])

    # ---- The GitHub Copilot harness: whole-turn usage, untimed.
    w6 = Window("0f1c2d3e-0006-4a00-8000-00000000c006", "agent:main:copilot1", ms("13:16:00.000"), WORKSPACE, "e5b4",
                harness="Copilot")
    w6.user(ms("13:16:00.100"), "Explain the build")                                                      # 2
    k1, _ = w6.assistant(ms("13:16:25.000"), ("openai-responses", "github-copilot", "gpt-5.5"),
                         [text("It uses tsdown.")], usage(800, 120, 2000, 0), "stop", ms("13:16:20.000"))  # 3
    w6_responses = [resp(key(k1, ms("13:16:20.000")), None, ms("13:16:25.000"), 2800, 2000, 0, 120, 0, "gpt-5.5",
                         "github-copilot")]
    trace_rows[w6.session_id] = [w6.attempt(ms("13:16:00.050"), "run-k", "off")]
    expect(MAIN_DB, w6, project="workspace", model="gpt-5.5", isSubagent=False, parentId=None, phase="idle",
           inFlightStartMs=None, lastLineMs=ms("13:16:25.000"), malformedLines=0, responses=w6_responses,
           effort=recorded("off"), modes={0: (recorded("off"), None)})
    trace(MAIN_DB, w6, 2, "idle")

    # ---- A fork from the dashboard: a fresh header, then copies of w1's rows 5-9 (copied history).
    w3 = Window("0f1c2d3e-0003-4a00-8000-00000000c003", "agent:main:dashboard:fork1", ms("13:20:00.000"), WORKSPACE,
                "f6a5", parent_key="agent:main:main", reason="fork")
    w3.copy([user_row, a1_row, t1_row, t2_row, a2_row])                                                   # 2-6
    w3.user(ms("13:20:05.000"), "Now add a test for the queue")                                           # 7
    f1, _ = w3.assistant(ms("13:20:09.000"), OPUS, [text("Added test/queue.test.ts.")], usage(4, 330, 18600, 0),
                         "stop", ms("13:20:05.006"))                                                      # 8
    w3_responses = [resp(key(f1, ms("13:20:05.006")), ms("13:20:05.006"), ms("13:20:09.000"), 18604, 18600, 0, 330,
                         0, "claude-opus-5-5", "anthropic")]
    expect(MAIN_DB, w3, project="workspace", model="claude-opus-5-5", isSubagent=False, parentId=None, phase="idle",
           inFlightStartMs=None, lastLineMs=ms("13:20:09.000"), malformedLines=0, responses=w3_responses)
    trace(MAIN_DB, w3, 6, "idle")

    write_database(p(MAIN_DB), SCHEMA_2026_10, [w1, w2, w4, w5, w6, w3], journal_wal=True)

    # ---- ops in the profile `work` (2026.8.1: no zstd columns, rollback journal).
    v1 = Window("6a7b8c9d-0001-4b00-9000-00000000d001", "agent:ops:main", ms("13:30:00.000"), RELAY, "a7f6",
                version=3)
    v1.user(ms("13:30:02.000"), "Rename Orbit to Trajectory")                                             # 2
    o1, _ = v1.assistant(ms("13:30:06.400"), GPT, [tool_call("call_r1", "edit", {"path": "src/orbit.ts"})],
                         usage(5200, 900, 0, 0, reasoning=300), "toolUse", ms("13:30:02.005"))           # 3
    v1.tool_result(ms("13:30:06.430"), "call_r1", "edit", "ok")                                           # 4
    o2, _ = v1.assistant(ms("13:30:09.560"), GPT, [text("Renamed in 14 files.")], usage(300, 410, 5100, 0),
                         "stop", ms("13:30:06.436"))                                                      # 5
    v1.assistant(ms("13:30:20.000"), (None, "openai", "gpt-5.5"), [text("no api")], usage(50, 5, 0, 0), "stop",
                 ms("13:30:19.000"))                                                                      # 6 guard
    v1_responses = [
        resp(key(o1, ms("13:30:02.005")), ms("13:30:02.005"), ms("13:30:06.400"), 5200, 0, 0, 900, 300, "gpt-5.5",
             "openai"),
        resp(key(o2, ms("13:30:06.436")), ms("13:30:06.436"), ms("13:30:09.560"), 5400, 5100, 0, 410, 0, "gpt-5.5",
             "openai"),
    ]
    # 2026.8.1 keeps every event as plain text: a nested `type` is selected too, and dropped once it is parsed.
    v1.plain_only = True
    p1 = v1.attempt(ms("13:30:01.000"), "run-o1", "minimal", compress=False)                              # 1, 2
    v1.event("prompt.submitted", ms("13:30:02.000"), "run-o1", {"finalPromptText": "Rename Orbit " * 200})  # 3
    p2 = v1.attempt(ms("13:30:06.420"), "run-o2", "medium", compress=False)                               # 4, 5
    p3 = v1.event("tool.result", ms("13:30:06.425"), "run-o2", {"result": {"type": "trace.metadata"}})    # 6
    expect(OPS_DB, v1, project="relay", model="gpt-5.5", isSubagent=False, parentId=None, phase="idle",
           inFlightStartMs=None, lastLineMs=ms("13:30:20.000"), malformedLines=0, responses=v1_responses,
           effort=recorded("medium"), modes={0: (recorded("minimal"), None), 1: (recorded("medium"), None)})
    trace(OPS_DB, v1, 3, "tools")
    trace(OPS_DB, v1, 4, "generating", ms("13:30:06.430"))

    # A window restored from a copy whose header predates the copied entry: the dedupe set removes it.
    v2 = Window("6a7b8c9d-0002-4b00-9000-00000000d002", "agent:ops:restored", ms("12:59:00.000"), WORKSPACE, "b8e7",
                reason="recovery")
    v2.raw(a2_row, ms("12:59:00.001"))                                                                    # 2
    expect(OPS_DB, v2, project="workspace", model=None, isSubagent=False, parentId=None, phase="idle",
           inFlightStartMs=None, lastLineMs=ms("13:00:16.520"), malformedLines=0, responses=[])

    # An ACPx native agent (OpenCode): the mirror counts nothing.
    v3 = Window("6a7b8c9d-0003-4b00-9000-00000000d003", "agent:ops:harness:acp-opencode:s1", ms("13:40:00.000"), RELAY,
                "c9d8", harness="acp-opencode")
    v3.user(ms("13:40:00.100"), "Add a changelog entry")                                                  # 2
    v3.assistant(ms("13:40:30.003"), ("openai-completions", "acp-opencode", "anthropic/claude-sonnet-5-5"),
                 [text("Added.")], usage(1000, 200, 0, 0), "stop", ms("13:40:30.000"),
                 idempotencyKey="req-9:acp:assistant")                                                    # 3
    expect(OPS_DB, v3, project="relay", model=None, isSubagent=False, parentId=None, phase="idle",
           inFlightStartMs=None, lastLineMs=ms("13:40:30.003"), malformedLines=0, responses=[])

    write_database(p(OPS_DB), SCHEMA_2026_8, [v2, v1, v3], journal_wal=False)

    # ---- legacy (2026.7): an agent database without session tables.
    if os.path.exists(p(LEGACY_DB)):
        os.remove(p(LEGACY_DB))
    db = sqlite3.connect(p(LEGACY_DB))
    db.executescript(SCHEMA_2026_7)
    db.commit()
    db.close()

    # ---- The Codex rollout of w4's turn in main's agent-scoped Codex home (a Codex root, METRICS 2).
    def codex(when, kind, payload):
        return {"timestamp": iso(when), "type": kind, "payload": payload}

    def cx(i, c, o, r):
        return {"input_tokens": i, "cached_input_tokens": c, "output_tokens": o, "reasoning_output_tokens": r,
                "total_tokens": i + o}

    rollout_rel = CODEX_HOME + "/sessions/2026/10/20/rollout-2026-10-20T13-10-00-019a0b1c-2d3e-7f40-8a51-c2d3e4f5a6b7.jsonl"
    rows = [
        codex(ms("13:10:00.200"), "session_meta", {"id": "019a0b1c-2d3e-7f40-8a51-c2d3e4f5a6b7",
                                                    "timestamp": iso(ms("13:10:00.200")), "cwd": WORKSPACE,
                                                    "originator": "openclaw", "model_provider": "openai",
                                                    "cli_version": "0.160.0", "source": "vscode"}),
        codex(ms("13:10:00.250"), "turn_context", {"cwd": WORKSPACE, "model": "gpt-5.5"}),
        codex(ms("13:10:00.300"), "event_msg", {"type": "task_started", "turn_id": "turn-1"}),
        codex(ms("13:10:00.300"), "event_msg", {"type": "user_message", "message": "Fix the flaky test"}),
        codex(ms("13:10:05.000"), "response_item", {"type": "reasoning", "summary": [], "encrypted_content": "x"}),
        codex(ms("13:10:19.900"), "response_item", {"type": "function_call", "name": "shell",
                                                     "arguments": "{\"command\":\"npm test\"}", "call_id": "c1"}),
        codex(ms("13:10:20.000"), "token_usage_record", {"response_id": "resp-oc-1", "usage": cx(12000, 9000, 300, 120)}),
        codex(ms("13:10:30.000"), "response_item", {"type": "function_call_output", "call_id": "c1",
                                                     "output": "1 failing"}),
        codex(ms("13:10:39.000"), "response_item", {"type": "message", "role": "assistant",
                                                     "content": [{"type": "output_text", "text": "Fixed."}]}),
        codex(ms("13:10:39.900"), "token_usage_record", {"response_id": "resp-oc-2", "usage": cx(12500, 12000, 210, 0)}),
        codex(ms("13:10:40.000"), "event_msg", {"type": "task_complete", "turn_id": "turn-1"}),
    ]
    with open(p(rollout_rel), "w", encoding="utf-8", newline="\n") as fh:
        for row in rows:
            fh.write(json.dumps(row, separators=(",", ":"), ensure_ascii=False) + "\n")
    files = {rollout_rel: dict(
        source="codex", project="workspace", model="gpt-5.5", isSubagent=False, parentFile=None, phase="idle",
        inFlightStartMs=None, lastLineMs=ms("13:10:40.000"), malformedLines=0,
        responses=[resp("resp-oc-1", ms("13:10:00.300"), ms("13:10:20.000"), 12000, 9000, 0, 300, 120, "gpt-5.5",
                        "openai", first=ms("13:10:05.000")),
                   resp("resp-oc-2", ms("13:10:30.000"), ms("13:10:39.900"), 12500, 12000, 0, 210, 0, "gpt-5.5",
                        "openai", first=ms("13:10:39.000"))])}
    files[rollout_rel].update(no_modes(files[rollout_rel]["responses"]))

    # ---- Discovery decoys: none of these is an agent database.
    decoys = [
        "openclaw/.openclaw/agents/main/agent/incognito-openclaw-agent.sqlite",
        "openclaw/.openclaw/agents/.archive/agent/openclaw-agent.sqlite",
        "openclaw/.openclaw/agents/main/openclaw-agent.sqlite",
        "openclaw/.openclaw/agents/main/sessions/0f1c2d3e-legacy.jsonl",
        "openclaw/.openclaw-default/agents/x/agent/openclaw-agent.sqlite",
        "openclaw/.openclaw-bad.name/agents/x/agent/openclaw-agent.sqlite",
    ]
    for rel in decoys:
        with open(p(rel), "w", encoding="utf-8", newline="\n") as fh:
            fh.write("not an agent database\n")

    mtimes[rollout_rel] = ms("12:00:10.000", NEXT_DAY)
    mtimes[MAIN_DB] = ms("12:00:11.000", NEXT_DAY)
    mtimes[OPS_DB] = ms("12:00:12.000", NEXT_DAY)
    mtimes[LEGACY_DB] = ms("12:00:13.000", NEXT_DAY)
    for rel, value in mtimes.items():
        os.utime(p(rel), (value / 1000, value / 1000))
    db_order = [MAIN_DB, OPS_DB, LEGACY_DB]
    return {
        "_comment": "OpenClaw: the folder `openclaw` plays the home folder; its state folders are .openclaw and the "
                    "profile .openclaw-work. Databases are read in `dbOrder` after the files of `order` with the same "
                    "dedupe set; windows in created_at order. Reading must not create any file. `traceSeqs` lists, "
                    "per database and window, the `seq` of every row the query on trajectory_runtime_events may "
                    "return, in order: no other row of that table is read.",
        "order": [rollout_rel],
        "files": files,
        "mtimes": mtimes,
        "dbOrder": db_order,
        "databases": databases,
        "discover": sorted([MAIN_DB, OPS_DB, LEGACY_DB]),
        "codexDiscover": [rollout_rel],
        "traceSeqs": {MAIN_DB: trace_rows, OPS_DB: {v1.session_id: [p1, p2, p3]}},
        "traces": traces,
    }


def main():
    if len(sys.argv) != 2:
        print(__doc__.strip())
        sys.exit(2)
    print(json.dumps(write_fixtures(sys.argv[1]), indent=2))


if __name__ == "__main__":
    main()
