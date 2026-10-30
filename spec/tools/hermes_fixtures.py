#!/usr/bin/env python3
"""Hermes Agent log fixtures (METRICS 4.15), used by generate_fixtures.py.

  python3 spec/tools/hermes_fixtures.py OUT_DIR

OUT_DIR/hermes/home plays the Hermes home (`~/.hermes`): its log folder `logs` holds the live `agent.log` and two
rotated files, and the profile `work` has its own `logs/agent.log`. Lines are laid out as Hermes's logging writes them
(`%(asctime)s %(levelname)s%(session_tag)s %(name)s: %(message)s`), with wall times of a process in
America/Los_Angeles (PDT, UTC-7 on 2026-10-20). The returned expectations have the parser-expected.json shape of the
`hermes` block: `order`, `mtimes`, `discover`, `sets` (per log folder: malformed lines and one agent per session id),
`traces`, `wallTimes`, `homes`, a scripted `rotation`, `runtimeConfigs` (config.yaml texts and whether they select
the Codex runtime) and a scripted `codexRuntime`. Expected values are written by hand from METRICS 4.15,
independently of reference.py: every UTC time below is the local time plus seven hours, worked out by hand.
"""
import json
import os
import sqlite3
import sys

from antigravity_fixtures import close_cleanly
from omp_fixtures import NEXT_DAY, ms

ZONE = "America/Los_Angeles"
LOGS = "hermes/home/logs"
WORK = "hermes/home/profiles/work/logs"

A = "20261020_045958_a1b2c3"             # CLI session
S = "20261020_050019_5d6e7f"             # delegate_task child (platform subagent)
G = "20261020_050455_9f8e7d6c"           # messaging gateway session (8 hex)
U = "3f6c1d2e-8a4b-4c5d-9e0f-1a2b3c4d5e6f"  # ACP session (UUID)
W = "cron_3f2a1b0c9d8e_20261020_051000"  # cron job in the profile `work`
OPUS = "anthropic/claude-opus-5-5"


def stamp(clock, day="2026-10-20"):
    return "%s %s" % (day, clock)


def rec(clock, level, logger, message, sid=None, day="2026-10-20"):
    tag = " [%s]" % sid if sid is not None else ""
    return "%s %s%s %s: %s" % (stamp(clock, day), level, tag, logger, message)


def turn(clock, sid, model, provider, platform, history, preview, origin=None, day="2026-10-20"):
    msg = "conversation turn: session=%s model=%s provider=%s platform=%s history=%d msg=%r" % (
        sid, model, provider, platform, history, preview)
    return rec(clock, "INFO", "agent.turn_context", msg + (" origin=%s" % origin if origin else ""), sid, day)


def call(clock, sid, n, model, provider, tail, level="INFO"):
    return rec(clock, level, "agent.conversation_loop",
               "API call #%d: model=%s provider=%s %s" % (n, model, provider, tail), sid)


def ended(clock, sid, model, reason="text_response(finish_reason=stop)", calls=1, origin=None):
    msg = ("Turn ended: reason=%s model=%s api_calls=%d/90 budget=%d/90 tool_turns=0 last_msg_role=assistant "
           "response_len=42 session=%s" % (reason, model, calls, calls, sid))
    return rec(clock, "INFO", "agent.conversation_loop", msg + (" origin=%s" % origin if origin else ""), sid)


def resp(key, start, end, inp, cached, cache_write, out, model, provider):
    return {"key": key, "startMs": start, "endMs": end, "firstMs": None, "input": inp, "cachedInput": cached,
            "cacheWriteInput": cache_write, "cacheWrite1hInput": 0, "output": out, "reasoning": 0,
            "model": model, "provider": provider, "fast": False, "final": True}


def key(sid, n, clock, day="2026-10-20"):
    return "hermes:%s:%d:%s" % (sid, n, stamp(clock, day))


def agent(model, sub, phase, in_flight, last, responses, effort=None, speed=None):
    return {"source": "hermes", "project": None, "model": model, "isSubagent": sub, "parentId": None,
            "phase": phase, "inFlightStartMs": in_flight, "lastLineMs": last, "responses": responses, "effort": effort,
            "speed": speed, "responseModes": {r["key"]: {"effort": None, "speed": None} for r in responses}}


CONFIG = """logging:
  level: INFO
  max_size_mb: 5
  backup_count: 3
agent:
  max_turns: 90
  # reasoning_effort: low
  reasoning_effort: "medium"  # new sessions start here
  service_tier: fast
  reasoning_overrides:
    reasoning_effort: max
delegation:
  reasoning_effort: low
"""

STATE_SCHEMA = """
CREATE TABLE sessions (id TEXT PRIMARY KEY, source TEXT NOT NULL, model TEXT, model_config TEXT, system_prompt TEXT,
  started_at REAL NOT NULL, input_tokens INTEGER DEFAULT 0, output_tokens INTEGER DEFAULT 0);
CREATE TABLE messages (id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, role TEXT NOT NULL, content TEXT);
"""


def write_state_db(path, sessions):
    """`state.db` (WAL, closed cleanly), reduced: only `sessions.model_config` is ever read."""
    for stale in (path, path + "-wal", path + "-shm"):
        if os.path.exists(stale):
            os.remove(stale)
    db = sqlite3.connect(path)
    db.execute("PRAGMA journal_mode=WAL")
    db.executescript(STATE_SCHEMA)
    for sid, config in sessions.items():
        db.execute("INSERT INTO sessions (id, source, model_config, system_prompt, started_at) VALUES (?,?,?,?,?)",
                   (sid, "cli", config, "(system prompt, never read)", 1.0))
        db.execute("INSERT INTO messages (session_id, role, content) VALUES (?, 'user', '(conversation, never read)')",
                   (sid,))
    db.commit()
    close_cleanly(db, path)


def write_lines(path, lines, endings=None):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as fh:
        for i, line in enumerate(lines):
            fh.write(line.encode("utf-8") + (endings or {}).get(i + 1, b"\n"))


def write_fixtures(out_dir):
    def p(rel):
        return os.path.join(out_dir, *rel.split("/"))

    traces = []

    def trace(rel, line, sid, phase, in_flight=None):
        traces.append(dict(file=rel, afterLine=line, session=sid, phase=phase, inFlightStartMs=in_flight))

    # ---- agent.log.2: the oldest rotated file. Session A's first turn starts here and continues in agent.log.1.
    log2 = LOGS + "/agent.log.2"
    write_lines(p(log2), [
        rec("04:59:58,100", "INFO", "hermes_cli.main", "Hermes Agent v0.22.5 starting (profile default)"),       # 1
        turn("05:00:00,000", A, OPUS, "openrouter", "cli", 0, "Split the queue from the dispatcher"),             # 2
        call("05:00:04,321", A, 1, OPUS, "openrouter", "in=18003 out=420 total=18423 latency=4.3s write=18000 "
             "id=gen-1788636728-qMa1 upstream=Anthropic"),                                                       # 3
        rec("05:00:04,980", "INFO", "agent.tool_executor", "tool read_file completed (0.52s, 1204 chars)", A),   # 4
        rec("05:00:05,001", "INFO", "agent.tool_executor", "tool search_files failed (0.40s): no match"),        # 5
    ])
    trace(log2, 2, A, "generating", ms("12:00:00.000"))
    trace(log2, 3, A, "generating", ms("12:00:04.321"))

    # ---- agent.log.1: A's turn ends, a sub-agent (S) runs twice, A's second turn fails.
    log1 = LOGS + "/agent.log.1"
    a2_line = call("05:00:09,512", A, 2, OPUS, "openrouter", "in=18610 out=260 total=18870 latency=4.5s "
                   "cache=18000/18610 (97%) write=600 id=gen-1788636733-Zx9 upstream=Claude Platform on AWS")
    write_lines(p(log1), [
        a2_line,                                                                                                 # 1
        ended("05:00:09,530", A, OPUS, calls=2),                                                                 # 2
        turn("05:00:20,000", S, "gpt-5.5", "openai-codex", "subagent", 0, "Review src/queue.ts for races"),     # 3
        call("05:00:26,789", S, 1, "gpt-5.5", "openai-codex",
             "in=5400 out=410 total=5810 latency=6.7s cache=5100/5400 (94%)"),                                   # 4
        ended("05:00:26,800", S, "gpt-5.5"),                                                                     # 5
        rec("05:00:30,000", "INFO", "agent.conversation_loop",
            "API call #2: model=gpt-5.5 provider=openai-codex in=12 out=", S),                                   # 6 malformed
        turn("05:00:40,000", S, "gpt-5.5", "openai-codex", "subagent", 2, "Now check the tests"),               # 7
        rec("05:00:41,000", "ERROR", "agent.conversation_loop",
            "[subagent-0] Non-retryable client error: Error code: 400 - context_length_exceeded", S),            # 8
        turn("05:01:00,000", A, OPUS, "openrouter", "cli", 4, "Run the tests, then fix what's failing"),        # 9
        call("05:01:02,000", A, 3, OPUS, "openrouter", "in=? out=? total=? latency=2.0s usage=unavailable"),    # 10
        call("05:01:20,250", A, 4, OPUS, "openrouter",
             "in=19100 out=300 total=19400 latency=-0.4s cache=18600/19100 (97%)"),                              # 11
        rec("05:02:40,000", "ERROR", "agent.conversation_loop",
            "API call failed after 3 retries. HTTP 529: Overloaded | provider=openrouter "
            "model=anthropic/claude-opus-5-5 msgs=9 tokens=~19,400", A),                                         # 12
        turn("24:00:00,000", A, OPUS, "openrouter", "cli", 9, "not a time"),                                    # 13
        call("05:02:50,000", A, 9, OPUS, "openrouter", "in=5 out=5 total=10 latency=0.1s", level="DEBUG"),      # 14
    ])
    trace(log1, 2, A, "idle")
    trace(log1, 3, S, "generating", ms("12:00:20.000"))
    trace(log1, 5, S, "idle")
    trace(log1, 7, S, "generating", ms("12:00:40.000"))
    trace(log1, 8, S, "idle")
    trace(log1, 10, A, "generating", ms("12:01:02.000"))
    trace(log1, 11, A, "generating", ms("12:01:20.250"))
    trace(log1, 12, A, "idle")
    trace(log1, 13, A, "idle")

    # ---- agent.log: a background-review fork of A, a gateway session whose failed tool quotes agent.log, an ACP
    # session (zero usage, no turn end), a side question without a session tag, a line with an impossible date.
    log0 = LOGS + "/agent.log"
    write_lines(p(log0), [
        turn("05:03:00,000", A, OPUS, "openrouter", "cli", 10, "Review the conversation above and update memory",
             origin="background_review"),                                                                        # 1
        call("05:03:05,500", A, 1, OPUS, "openrouter", "in=2000 out=150 total=2150 latency=5.5s "
             "cache=1800/2000 (90%)"),                                                                           # 2
        ended("05:03:05,600", A, OPUS, origin="background_review"),                                              # 3
        turn("05:05:00,000", G, "hermes-4-405b", "nous", "telegram", 12,
             "what is on my calendar? model=x provider=y platform=subagent history=1 msg=z"),                    # 4
        call("05:05:08,000", G, 1, "hermes-4-405b", "nous", "in=9000 out=0 total=9000 latency=8.0s"),           # 5
        rec("05:05:10,000", "INFO", "agent.tool_executor",
            "tool terminal failed (0.10s): grep -n \"API call #2\" agent.log.1", G),                              # 6
        a2_line,                                                                                                 # 7 quoted copy
        call("05:05:12,345", G, 2, "hermes-4-405b", "nous",
             "in=9100 out=120 total=9220 latency=0.3s cache=8960/9100 (98%)"),                                   # 8
        rec("05:05:12,400", "WARNING", "agent.conversation_loop",
            "Turn ended with pending tool result (agent may appear stuck). Turn ended: reason=max_iterations "
            "model=hermes-4-405b api_calls=2/90 budget=2/90 tool_turns=2 last_msg_role=tool response_len=0 "
            "session=%s last_tool=terminal" % G, G),                                                             # 9
        turn("05:06:00,000", U, "claude-sonnet-5-5", "copilot-acp", "acp", 0, "hi"),                            # 10
        call("05:06:03,000", U, 1, "claude-sonnet-5-5", "copilot-acp", "in=0 out=0 total=0 latency=3.0s"),      # 11
        call("05:07:00,000", None, 5, OPUS, "unknown", "in=700 out=40 total=740 latency=1.2s"),                 # 12
        turn("05:08:00,000", A, OPUS, "openrouter", "cli", 12, "impossible date", day="2026-02-30"),            # 13
    ])
    trace(log0, 1, A, "generating", ms("12:03:00.000"))
    trace(log0, 3, A, "idle")
    trace(log0, 4, G, "generating", ms("12:05:00.000"))
    trace(log0, 5, G, "generating", ms("12:05:08.000"))
    trace(log0, 7, G, "generating", ms("12:05:08.000"))
    trace(log0, 9, G, "idle")
    trace(log0, 11, U, "generating", ms("12:06:03.000"))
    trace(log0, 12, "", "idle")

    # ---- The profile `work`: a cron session on Windows-style CRLF lines; its evening turn ends after midnight UTC.
    work = WORK + "/agent.log"
    write_lines(p(work), [
        turn("05:10:00,000", W, "claude-sonnet-5-5", "anthropic", "cron", 0, "[cron] Summarize overnight alerts"),  # 1
        call("05:10:03,210", W, 1, "claude-sonnet-5-5", "anthropic", "in=3000 out=210 total=3210 latency=3.2s"),  # 2
        ended("05:10:03,250", W, "claude-sonnet-5-5"),                                                            # 3
        turn("17:10:00,000", W, "claude-sonnet-5-5", "anthropic", "cron", 2, "[cron] Summarize evening alerts"),  # 4
        call("17:10:04,000", W, 1, "claude-sonnet-5-5", "anthropic",
             "in=3200 out=180 total=3380 latency=3.5s write=3000"),                                               # 5
        ended("17:10:04,020", W, "claude-sonnet-5-5"),                                                            # 6
    ], endings={2: b"\r\n", 3: b"\r\n"})
    trace(work, 1, W, "generating", ms("12:10:00.000"))
    trace(work, 4, W, "generating", ms("00:10:00.000", NEXT_DAY))
    trace(work, 5, W, "generating", ms("00:10:04.000", NEXT_DAY))
    trace(work, 6, W, "idle")

    # ---- Discovery decoys: none of these is read.
    api = call("05:09:00,000", A, 7, OPUS, "openrouter", "in=100 out=10 total=110 latency=1.0s") + "\n"
    decoys = {
        LOGS + "/errors.log": rec("05:02:40,000", "ERROR", "agent.conversation_loop", "API call failed after 3 "
                                  "retries. HTTP 529: Overloaded", A) + "\n",
        LOGS + "/gateway.log": rec("05:05:00,000", "INFO", "gateway.platforms.telegram", "inbound message") + "\n",
        LOGS + "/agent.log.1.gz": "not a log\n",
        LOGS + "/agent.log.bak": api,
        LOGS + "/agent.log.x": api,
        LOGS + "/.__agent.lock": "",
        LOGS + "/install.log": "[install] dependencies installed\n",
        LOGS + "/archive/agent.log": api,
        "hermes/home/profiles/.deleted/gone/logs/agent.log": api,
        "hermes/home/profiles/empty/config.yaml": "model:\n  default: hermes-4-405b\n",
        "hermes/home/config.yaml": CONFIG,
        "hermes/home/profiles/work/config.yaml": "agent: {reasoning_effort: high, service_tier: fast}\n",
    }
    for rel, content in decoys.items():
        os.makedirs(os.path.dirname(p(rel)), exist_ok=True)
        with open(p(rel), "w", encoding="utf-8", newline="\n") as fh:
            fh.write(content)

    # The sessions of the default profile: A selected a level, S turned reasoning off, G has none (and a service tier
    # that is not read), U's cell is not JSON. The profile `work` has no database.
    write_state_db(p("hermes/home/state.db"), {
        A: json.dumps({"max_iterations": 90, "reasoning_config": {"enabled": True, "effort": "high"},
                       "max_tokens": None}),
        S: json.dumps({"max_iterations": 90, "reasoning_config": {"enabled": False}, "max_tokens": None}),
        G: json.dumps({"reasoning_config": None, "service_tier": "priority"}),
        U: "not json"})
    medium = {"mode": "medium", "evidence": "configured"}
    fast = {"mode": "fast", "evidence": "configured"}

    a = agent(OPUS, False, "idle", None, ms("12:03:05.600"), [
        resp(key(A, 1, "05:00:04,321"), ms("12:00:00.021"), ms("12:00:04.321"), 18003, 0, 18000, 420, OPUS,
             "openrouter"),
        resp(key(A, 2, "05:00:09,512"), ms("12:00:05.012"), ms("12:00:09.512"), 18610, 18000, 600, 260, OPUS,
             "openrouter"),
        resp(key(A, 4, "05:01:20,250"), None, ms("12:01:20.250"), 19100, 18600, 0, 300, OPUS, "openrouter"),
        resp(key(A, 1, "05:03:05,500"), ms("12:03:00.000"), ms("12:03:05.500"), 2000, 1800, 0, 150, OPUS,
             "openrouter"),
    ], {"mode": "high", "evidence": "selected"}, fast)
    s = agent("gpt-5.5", True, "idle", None, ms("12:00:41.000"), [
        resp(key(S, 1, "05:00:26,789"), ms("12:00:20.089"), ms("12:00:26.789"), 5400, 5100, 0, 410, "gpt-5.5",
             "openai-codex"),
    ], {"mode": "off", "evidence": "selected"}, fast)
    g = agent("hermes-4-405b", False, "idle", None, ms("12:05:12.400"), [
        resp(key(G, 1, "05:05:08,000"), ms("12:05:00.000"), ms("12:05:08.000"), 9000, 0, 0, 0, "hermes-4-405b",
             "nous"),
        resp(key(G, 2, "05:05:12,345"), ms("12:05:12.045"), ms("12:05:12.345"), 9100, 8960, 0, 120,
             "hermes-4-405b", "nous"),
    ], medium, fast)
    u = agent("claude-sonnet-5-5", False, "generating", ms("12:06:03.000"), ms("12:06:03.000"), [], medium, fast)
    side = agent(OPUS, False, "idle", None, ms("12:07:00.000"), [
        resp(key("", 5, "05:07:00,000"), ms("12:06:58.800"), ms("12:07:00.000"), 700, 0, 0, 40, OPUS, None),
    ], medium, fast)
    w = agent("claude-sonnet-5-5", False, "idle", None, ms("00:10:04.020", NEXT_DAY), [
        resp(key(W, 1, "05:10:03,210"), ms("12:10:00.010"), ms("12:10:03.210"), 3000, 0, 0, 210,
             "claude-sonnet-5-5", "anthropic"),
        resp(key(W, 1, "17:10:04,000"), ms("00:10:00.500", NEXT_DAY), ms("00:10:04.000", NEXT_DAY), 3200, 0, 3000,
             180, "claude-sonnet-5-5", "anthropic"),
    ])

    mtimes = {log2: ms("12:01:00.000", NEXT_DAY), log1: ms("12:01:01.000", NEXT_DAY),
              log0: ms("12:01:02.000", NEXT_DAY), work: ms("12:01:03.000", NEXT_DAY)}
    order = sorted(mtimes, key=lambda rel: (mtimes[rel], rel))
    for rel in order:
        os.utime(p(rel), (mtimes[rel] / 1000, mtimes[rel] / 1000))
    return {
        "_comment": "Hermes Agent (METRICS 4.15). `home` plays the Hermes home; `discover` lists the log files of "
                    "its log folders. Feed the files of `order` (modification times `mtimes`) to one parser state "
                    "per log folder, with wall times in `timeZone`, and the shared dedupe set; `sets` gives, per "
                    "log folder, its malformed API-call lines and one agent per session id (the empty id is the "
                    "agent of lines without a session tag; agent id = absolute folder path + '#' + id), in the order "
                    "agents first appear. `traces` give one session's phase after feeding a file up to and including "
                    "physical line `afterLine`, after the files before it in `order`. `wallTimes` convert a record "
                    "line's timestamp in a zone (null: not a record line). `homes` resolve the Hermes home from an "
                    "environment. `rotation` replays a log folder with real renames: `append` writes `lines` (each "
                    "followed by a newline) or raw `text` to a file and sets its modification time, `rename` and "
                    "`delete` change files, `read` reads every file of the folder from the offset its identity "
                    "reached and checks every response key emitted so far and the listed agents. `runtimeConfigs` "
                    "give config.yaml texts and whether they select the Codex runtime; `agentConfigs` give texts and the "
                    "two scalars of their `agent` block (EXECUTION-MODES). `home` also holds `state.db`, whose "
                    "sessions give the agents' session-current effort. `codexRuntime` replays a "
                    "Hermes home (folder `home`, with the profile `p`): `write` sets a file's text (`size`: padded "
                    "with `#` to that many bytes) and modification time, `remove` deletes it, `append` works like "
                    "the rotation's, and `read` checks the config.yaml of every log folder, then reads its new "
                    "lines, and checks each listed log folder's switch, every response key emitted so far and the "
                    "listed agents.",
        "home": "hermes/home",
        "timeZone": ZONE,
        "discover": sorted([log0, log1, log2, work]),
        "order": order,
        "mtimes": mtimes,
        "sets": {
            LOGS: {"malformedLines": 1, "agents": {A: a, S: s, G: g, U: u, "": side}},
            WORK: {"malformedLines": 0, "agents": {W: w}},
        },
        "traces": traces,
        "wallTimes": wall_times(),
        "homes": homes(),
        "rotation": rotation(),
        "runtimeConfigs": runtime_configs(),
        "codexRuntime": codex_runtime(),
        "agentConfigs": agent_configs(),
    }


def agent_configs():
    """config.yaml texts and the two scalars of their `agent` block (null: the key is there, without one plain scalar)."""
    def case(name, text, **scalars):
        return dict({"name": name, "text": text}, **scalars)

    return [
        case("block", "agent:\n  reasoning_effort: high\n  service_tier: fast\n", reasoning_effort="high",
             service_tier="fast"),
        case("quotes and comments", "agent:\n  reasoning_effort: \"medium\"  # default\n  service_tier: 'priority'\n",
             reasoning_effort="medium", service_tier="priority"),
        case("quoted keys", "\"agent\":\n  'service_tier': ultra\n", service_tier="ultra"),
        case("the last key line decides", "agent:\n  reasoning_effort: low\n  reasoning_effort: max\n",
             reasoning_effort="max"),
        case("byte order mark and CRLF", "\ufeffagent:\r\n  service_tier: ultra\r\n", service_tier="ultra"),
        case("deeper keys are not the block's", "agent:\n  reasoning_overrides:\n    reasoning_effort: max\n"),
        case("other blocks", "delegation:\n  reasoning_effort: low\n"),
        case("root keys", "reasoning_effort: high\nservice_tier: fast\n"),
        case("flow mapping", "agent: {reasoning_effort: high}\n"),
        case("a tab ends the block", "agent:\n\treasoning_effort: high\n"),
        case("not a scalar", "agent:\n  reasoning_effort: {effort: high}\n", reasoning_effort=None),
        case("empty value", "agent:\n  service_tier:\n", service_tier=None),
        case("two words", "agent:\n  service_tier: very fast\n", service_tier=None),
    ]


def wall_times():
    def case(zone, clock, expected, day="2026-10-20", sep=","):
        line = "%s %s INFO [x] agent.conversation_loop: Turn ended: reason=x" % (day, clock.replace(",", sep))
        return {"timeZone": zone, "line": line, "ms": expected}

    return [
        case(ZONE, "05:00:00,000", ms("12:00:00.000")),
        case(ZONE, "00:59:59,999", ms("07:59:59.999", "2026-11-01"), "2026-11-01"),
        case(ZONE, "01:30:00,250", ms("08:30:00.250", "2026-11-01"), "2026-11-01"),   # repeated: the earlier (PDT)
        case(ZONE, "02:30:00,000", ms("10:30:00.000", "2026-11-01"), "2026-11-01"),   # after the change (PST)
        case(ZONE, "01:59:59,999", ms("09:59:59.999", "2026-03-08"), "2026-03-08"),   # before the skip (PST)
        case(ZONE, "02:30:00,000", ms("10:30:00.000", "2026-03-08"), "2026-03-08"),   # skipped: offset before (PST)
        case(ZONE, "03:00:00,000", ms("10:00:00.000", "2026-03-08"), "2026-03-08"),   # after the skip (PDT)
        case("Europe/Berlin", "02:30:00,000", ms("00:30:00.000", "2026-10-25"), "2026-10-25"),  # repeated: CEST
        case("Europe/Berlin", "02:30:00,000", ms("01:30:00.000", "2026-03-29"), "2026-03-29"),  # skipped: CET
        case("Asia/Shanghai", "20:00:00,000", ms("12:00:00.000")),
        case("America/St_Johns", "09:30:00,000", ms("12:00:00.000")),                 # NDT, UTC-2:30
        case("UTC", "12:00:00,007", ms("12:00:00.007")),
        case(ZONE, "10:00:00,000", None, "2026-02-29"),                               # 2026 is not a leap year
        case(ZONE, "24:00:00,000", None),
        case(ZONE, "16:00:00,000", None, "1969-12-31"),                               # before 1970
        case(ZONE, "05:00:00,000", None, sep="."),                                    # not Python's asctime
    ]


def homes():
    mac, win = "/Users/dev", "C:\\Users\\dev"
    local = "C:\\Users\\dev\\AppData\\Local"

    def case(env, platform, expected):
        return {"env": env, "platform": platform, "home": mac if platform == "darwin" else win,
                "expected": expected}

    return [
        case({}, "darwin", "/Users/dev/.hermes"),
        case({"HERMES_HOME": "~/.hermes/profiles/work"}, "darwin", "/Users/dev/.hermes"),
        case({"HERMES_HOME": "/Users/dev/.hermes"}, "darwin", "/Users/dev/.hermes"),
        case({"HERMES_HOME": "/srv/hermes/profiles/ops"}, "darwin", "/srv/hermes"),
        case({"HERMES_HOME": "/srv/agent-home/"}, "darwin", "/srv/agent-home"),
        case({"HERMES_HOME": "   "}, "darwin", "/Users/dev/.hermes"),
        case({"HERMES_HOME": "relative/home"}, "darwin", "/Users/dev/.hermes"),
        case({"LOCALAPPDATA": local}, "win32", local + "\\hermes"),
        case({}, "win32", local + "\\hermes"),
        case({"LOCALAPPDATA": local, "HERMES_HOME": "c:\\users\\dev\\appdata\\local\\HERMES\\profiles\\work"},
             "win32", local + "\\hermes"),
        case({"LOCALAPPDATA": local, "HERMES_HOME": "D:\\hermes-data"}, "win32", "D:\\hermes-data"),
        case({"LOCALAPPDATA": local, "HERMES_HOME": "~\\hermes-x"}, "win32", "C:\\Users\\dev\\hermes-x"),
    ]


def rotation():
    """Two rotations while the reader runs. A process that had not reopened yet writes a queued record into the
    renamed file after the new agent.log was written, so the renamed file is read last: reading it again from the
    start (following paths instead of identities) would replay R's turn start after R's turn ended."""
    r_turn = turn("06:00:00,000", "R", "gpt-5.5", "openai", "cli", 0, "rotate the logs")
    r_call1 = call("06:00:03,000", "R", 1, "gpt-5.5", "openai", "in=1000 out=100 total=1100 latency=2.9s")
    q_turn = turn("06:00:03,500", "Q", "gpt-5.5", "openai", "tui", 0, "late writer")
    r_call2 = call("06:00:07,000", "R", 2, "gpt-5.5", "openai", "in=1200 out=80 total=1280 latency=3.8s")
    r_end = ended("06:00:07,010", "R", "gpt-5.5", calls=2)
    q_call = call("06:00:09,000", "Q", 1, "gpt-5.5", "openai", "in=500 out=50 total=550 latency=5.4s")
    q_end = ended("06:00:09,100", "Q", "gpt-5.5")
    r_next = turn("06:00:12,000", "R", "gpt-5.5", "openai", "cli", 4, "next")
    k1, k2, k3 = key("R", 1, "06:00:03,000"), key("R", 2, "06:00:07,000"), key("Q", 1, "06:00:09,000")

    def state(phase, in_flight, last):
        return {"phase": phase, "inFlightStartMs": in_flight, "lastLineMs": last}

    return {
        "timeZone": ZONE,
        "steps": [
            {"append": "agent.log", "lines": [r_turn, r_call1], "mtime": ms("13:00:03.000")},
            {"read": True, "responseKeys": [k1],
             "agents": {"R": state("generating", ms("13:00:03.000"), ms("13:00:03.000"))}},
            {"rename": ["agent.log", "agent.log.1"]},
            {"append": "agent.log", "lines": [r_call2, r_end], "mtime": ms("13:00:07.100")},
            {"append": "agent.log.1", "lines": [q_turn], "mtime": ms("13:00:07.200")},
            {"read": True, "responseKeys": [k1, k2],
             "agents": {"R": state("idle", None, ms("13:00:07.010")),
                        "Q": state("generating", ms("13:00:03.500"), ms("13:00:03.500"))}},
            {"rename": ["agent.log.1", "agent.log.2"]},
            {"rename": ["agent.log", "agent.log.1"]},
            {"append": "agent.log", "lines": [q_call, q_end], "mtime": ms("13:00:09.200")},
            {"delete": "agent.log.2"},
            {"read": True, "responseKeys": [k1, k2, k3],
             "agents": {"R": state("idle", None, ms("13:00:07.010")),
                        "Q": state("idle", None, ms("13:00:09.100"))}},
            {"append": "agent.log", "text": r_next, "mtime": ms("13:00:12.000")},
            {"read": True, "responseKeys": [k1, k2, k3],
             "agents": {"R": state("idle", None, ms("13:00:07.010"))}},
            {"append": "agent.log", "text": "\n", "mtime": ms("13:00:12.100")},
            {"read": True, "responseKeys": [k1, k2, k3],
             "agents": {"R": state("generating", ms("13:00:12.000"), ms("13:00:12.000"))}},
        ],
    }


def runtime_configs():
    """config.yaml texts and whether they select the Codex runtime (METRICS 4.15 Codex runtime)."""
    def case(name, text, codex):
        return {"name": name, "text": text, "codexRuntime": codex}

    return [
        case("block as Hermes writes it", "model:\n  default: gpt-5.5\n  provider: openai-codex\n"
             "  openai_runtime: codex_app_server\n", True),
        case("auto", "model:\n  openai_runtime: auto\n", False),
        case("upper case and a comment", "model:\n  openai_runtime: CODEX_APP_SERVER  # via /codex-runtime\n", True),
        case("double quotes with spaces inside", "model:\n  openai_runtime: \" codex_app_server \"\n", True),
        case("single quotes, four spaces", "model:\n    openai_runtime: 'codex_app_server'\n", True),
        case("quoted value and a comment", "model:\n  openai_runtime: \"codex_app_server\" # on\n", True),
        case("quoted value and more text", "model:\n  openai_runtime: \"codex_app_server\" x\n", False),
        case("BOM, CRLF, comments and blank lines", "\ufeff# Hermes\r\nmodel:   # main model\r\n\r\n"
             "  # runtime below\r\n  default: gpt-5.5\r\n\t\r\n\t# tab comment\r\n"
             "  openai_runtime: codex_app_server\r\n", True),
        case("quoted keys", "\"model\":\n  'openai_runtime': codex_app_server\n", True),
        case("spaces before the colons", "model :\n  openai_runtime : codex_app_server\n", True),
        case("a tab after the colon", "model:\n  openai_runtime:\tcodex_app_server\n", True),
        case("no space after the colon", "model:\n  openai_runtime:codex_app_server\n", False),
        case("a hash without a space is part of the value", "model:\n  openai_runtime: codex_app_server#on\n", False),
        case("empty value", "model:\n  openai_runtime:\n", False),
        case("a comment as the value", "model:\n  openai_runtime: # codex_app_server\n", False),
        case("YAML 1.1 boolean", "model:\n  openai_runtime: on\n", False),
        case("environment reference", "model:\n  openai_runtime: ${HERMES_RUNTIME}\n", False),
        case("block scalar", "model:\n  openai_runtime: >-\n    codex_app_server\n", False),
        case("nested deeper", "model:\n  providers:\n    openai_runtime: codex_app_server\n", False),
        case("first child sets the indentation", "model:\n    default: gpt-5.5\n  openai_runtime: codex_app_server\n",
             False),
        case("list item", "model:\n  - openai_runtime: codex_app_server\n", False),
        case("tab indentation", "model:\n\topenai_runtime: codex_app_server\n", False),
        case("another section", "fallback_model:\n  openai_runtime: codex_app_server\nmodel:\n  default: gpt-5.5\n",
             False),
        case("the block ends at a top-level key", "model:\n  default: gpt-5.5\nagent:\n"
             "  openai_runtime: codex_app_server\n", False),
        case("root level", "openai_runtime: codex_app_server\nmodel:\n  default: gpt-5.5\n", False),
        case("scalar model", "model: gpt-5.5\n  openai_runtime: codex_app_server\n", False),
        case("flow mapping", "model: {openai_runtime: codex_app_server}\n", False),
        case("the last key line wins", "model:\n  openai_runtime: codex_app_server\n  openai_runtime: auto\n", False),
        case("the last block wins", "model:\n  openai_runtime: auto\nmodel:\n  openai_runtime: codex_app_server\n",
             True),
        case("document markers", "---\nmodel:\n  openai_runtime: codex_app_server\n...\n", True),
        case("no final newline", "model:\n  openai_runtime: codex_app_server", True),
        case("empty file", "", False),
    ]


def codex_runtime():
    """A Hermes home whose config.yaml switches the Codex runtime on and off while the reader runs (METRICS 4.15)."""
    logs, plogs = "logs/agent.log", "profiles/p/logs/agent.log"
    codex = "model:\n  default: gpt-5.5\n  provider: openai-codex\n  openai_runtime: codex_app_server\n"
    c_turn1 = turn("07:00:00,000", "C", "gpt-5.5", "openai-codex", "cli", 0, "build the parser")
    p_turn1 = turn("07:00:01,000", "P", "gpt-5.5", "openai-codex", "tui", 0, "hello")
    c_turn2 = turn("07:00:30,000", "C", "gpt-5.5-codex", "openai-codex", "cli", 2, "now the tests")
    f_turn = turn("07:01:00,000", "F", "claude-sonnet-5-5", "anthropic", "cli", 0, "review")
    f_call = call("07:01:03,000", "F", 1, "claude-sonnet-5-5", "anthropic", "in=1000 out=50 total=1050 latency=2.5s")
    f_end = ended("07:01:03,020", "F", "claude-sonnet-5-5")
    c_turn3 = turn("07:02:00,000", "C", "gpt-5.5", "openai-codex", "cli", 4, "back to Hermes")
    p_turn2 = turn("07:03:00,000", "P", "gpt-5.5", "openai-codex", "tui", 2, "again")
    c_turn4 = turn("07:04:00,000", "C", "gpt-5.5", "openai-codex", "cli", 6, "one more")
    kf = key("F", 1, "07:01:03,000")

    def state(phase, in_flight, last, model=None):
        out = {"phase": phase, "inFlightStartMs": in_flight, "lastLineMs": last}
        if model:
            out["model"] = model
        return out

    def read(keys, **sets):
        return {"read": True, "responseKeys": keys, "sets": sets}

    def logset(codex_on, **agents):
        return {"codexRuntime": codex_on, "agents": agents}

    return {
        "timeZone": ZONE,
        "logFolders": ["logs", "profiles/p/logs"],
        "steps": [
            # 1. No config.yaml: Hermes's own runtime.
            {"append": logs, "lines": [c_turn1], "mtime": ms("14:00:00.100")},
            {"append": plogs, "lines": [p_turn1], "mtime": ms("14:00:01.100")},
            read([], logs=logset(False, C=state("generating", ms("14:00:00.000"), ms("14:00:00.000"))),
                 **{"profiles/p/logs": logset(False, P=state("generating", ms("14:00:01.000"), ms("14:00:01.000")))}),
            # 2. The default profile switches to the Codex runtime: its generating agents become idle; the profile p
            #    has no config.yaml of its own and keeps Hermes's runtime.
            {"write": "config.yaml", "text": codex, "mtime": ms("14:00:10.000")},
            read([], logs=logset(True, C=state("idle", None, ms("14:00:00.000"))),
                 **{"profiles/p/logs": logset(False, P=state("generating", ms("14:00:01.000"), ms("14:00:01.000")))}),
            # 3. A Codex-runtime turn: only its start line; model and lastLineMs follow it, the phase stays idle.
            {"append": logs, "lines": [c_turn2], "mtime": ms("14:00:30.100")},
            read([], logs=logset(True, C=state("idle", None, ms("14:00:30.000"), "gpt-5.5-codex"))),
            # 4. A turn Hermes still runs itself (another provider): its response counts, its phase stays idle.
            {"append": logs, "lines": [f_turn, f_call], "mtime": ms("14:01:03.100")},
            read([kf], logs=logset(True, F=state("idle", None, ms("14:01:03.000")))),
            {"append": logs, "lines": [f_end], "mtime": ms("14:01:03.200")},
            read([kf], logs=logset(True, F=state("idle", None, ms("14:01:03.020")))),
            # 5. Back to auto: no phase changes until the next turn start.
            {"write": "config.yaml", "text": "model:\n  default: gpt-5.5\n  openai_runtime: auto\n",
             "mtime": ms("14:01:30.000")},
            read([kf], logs=logset(False, C=state("idle", None, ms("14:00:30.000")))),
            {"append": logs, "lines": [c_turn3], "mtime": ms("14:02:00.100")},
            read([kf], logs=logset(False, C=state("generating", ms("14:02:00.000"), ms("14:02:00.000")))),
            # 6. A hand edit with a BOM, CRLF and a quoted value turns it on again.
            {"write": "config.yaml", "text": "\ufeffmodel:\r\n  openai_runtime: \"Codex_App_Server\"\r\n",
             "mtime": ms("14:02:10.000")},
            read([kf], logs=logset(True, C=state("idle", None, ms("14:02:00.000")))),
            # 7. The profile p: a nested key does not count; its own block does.
            {"write": "profiles/p/config.yaml", "text": "model:\n  providers:\n    openai_runtime: codex_app_server\n",
             "mtime": ms("14:02:20.000")},
            read([kf], **{"profiles/p/logs": logset(False, P=state("generating", ms("14:00:01.000"),
                                                                   ms("14:00:01.000")))}),
            {"write": "profiles/p/config.yaml", "text": codex, "mtime": ms("14:02:30.000")},
            {"append": plogs, "lines": [p_turn2], "mtime": ms("14:03:00.100")},
            read([kf], **{"profiles/p/logs": logset(True, P=state("idle", None, ms("14:03:00.000")))}),
            # 8. config.yaml removed: Hermes's runtime again.
            {"remove": "config.yaml"},
            {"append": logs, "lines": [c_turn4], "mtime": ms("14:04:00.100")},
            read([kf], logs=logset(False, C=state("generating", ms("14:04:00.000"), ms("14:04:00.000")))),
            # 9. Exactly 1 MiB is read; one byte more is not (the switch is off).
            {"write": "config.yaml", "text": codex, "size": 1048576, "mtime": ms("14:04:10.000")},
            read([kf], logs=logset(True, C=state("idle", None, ms("14:04:00.000")))),
            {"write": "config.yaml", "text": codex, "size": 1048577, "mtime": ms("14:04:20.000")},
            read([kf], logs=logset(False, C=state("idle", None, ms("14:04:00.000")))),
        ],
    }


def main():
    if len(sys.argv) != 2:
        print(__doc__.strip())
        sys.exit(2)
    print(json.dumps(write_fixtures(sys.argv[1]), indent=2))


if __name__ == "__main__":
    main()
