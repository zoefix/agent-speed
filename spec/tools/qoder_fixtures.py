#!/usr/bin/env python3
"""Qoder fixtures (METRICS 4.18 Qoder IDE, 4.19 Qoder CLI), with hand-computed expectations.

  python3 spec/tools/qoder_fixtures.py [FIXTURES_DIR]

FIXTURES_DIR/qoder holds Qoder IDE data folders, each with its database at SharedClientCache/cache/db/local.db:
  home/Library/Application Support/Qoder        the international edition (macOS layout, home = qoder/home): the
                                                current schema, a WAL header without a -wal file (opened immutable);
                                                its User/globalStorage/state.vscdb is never read
  home/Library/Application Support/<CN_FOLDER>  the China edition: an older schema without parent_session_id,
                                                chat_message.request_id and chat_record (rollback journal)
  portable/user-data                            a VSCODE_PORTABLE data folder: a copied session (skipped), a call whose
                                                key the first database owns (ignored) and a call of its own
  legacy                                        a data folder whose database has no token_info or model_info:
                                                unsupported
FIXTURES_DIR/qoder-cli plays a Qoder CLI config folder (`~/.qoder`): projects/<encoded cwd>/<session>.jsonl with lines
shaped like those of Qoder CLI 1.1.86 (spec/research/qoder.md), and an event log under logs/ that is never read.

Writes FIXTURES_DIR/qoder-expected.json; generate_fixtures.py calls `write_cli_fixtures` for the Qoder CLI entries of
parser-expected.json. The China edition's folder name comes from qoder_reference.CN_FOLDER. Every value is
synthetic. Expected values are written here by hand from METRICS 4.18 and 4.19 (4.1 for Qoder CLI), never by a reader.
"""
import json
import os
import sqlite3
import sys
from datetime import datetime, timezone
from pathlib import Path

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from qoder_reference import CN_FOLDER  # noqa: E402

DAY0 = 1792454400000  # 2026-10-20T00:00:00Z
CLI_DAY = DAY0 + 2 * 86_400_000  # 2026-10-22
SECRET = "ZW5jcnlwdGVkIGNvbnRlbnQ="  # Qoder stores content encrypted; never read
DATA = "qoder/home/Library/Application Support"


def at(h, m, s=0, ms=0, day=DAY0):
    """Unix ms of hh:mm:ss.mmm UTC on the fixture day."""
    return day + ((h * 60 + m) * 60 + s) * 1000 + ms


def cli_at(h, m, s=0, ms=0):
    return at(h, m, s, ms, CLI_DAY)


def iso(value):
    dt = datetime.fromtimestamp(value / 1000, tz=timezone.utc)
    return dt.strftime("%Y-%m-%dT%H:%M:%S.") + "%03dZ" % (value % 1000)


def uid(kind, n):
    """A deterministic UUID: kind 1 messages, 2 requests, 3 transcript lines, 4 tool calls."""
    return "%08x-%04x-4%03x-8%03x-%012x" % (0x6f1d2c3a + kind, n, kind, n % 0x1000, 0x9e3779b9 * n % 2 ** 48)


def compact(value):
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False)


def response(key, agent, start, end, inp, cached, out, model, write=0, write1h=0):
    """An expected response; `agent` None for the parser-expected.json shape, which has no agentId."""
    r = dict(key=key, agentId=agent, startMs=start, endMs=end, firstMs=None, input=inp, cachedInput=cached,
             cacheWriteInput=write, cacheWrite1hInput=write1h, output=out, reasoning=0, model=model, provider=None,
             final=True, fast=False)
    if agent is None:
        del r["agentId"]
    return r


def agent_view(project, cwd, model, phase, last, responses, in_flight=None, malformed=0, subagent=False, parent=None):
    return dict(project=project, cwdPath=cwd, model=model, isSubagent=subagent, parentId=parent, phase=phase,
                inFlightStartMs=in_flight, lastLineMs=last, malformedLines=malformed, responses=responses)


def day_totals(responses, measured_models=False):
    """UTC day totals (METRICS 8): measured = final with a valid interval, its duration widened to 500 ms."""
    days = {}
    for r in responses:
        day = datetime.fromtimestamp(r["endMs"] / 1000, tz=timezone.utc).strftime("%Y-%m-%d")
        row = days.setdefault(day, dict(requests=0, input=0, cachedInput=0, cacheWriteInput=0, output=0,
                                        reasoning=0, measuredOutput=0, measuredMs=0, agents=set(), models={}))
        row["requests"] += 1
        for field in ("input", "cachedInput", "cacheWriteInput", "output", "reasoning"):
            row[field] += r[field]
        start, end = r["startMs"], r["endMs"]
        measured = start is not None and 0 <= end - start <= 15 * 60_000
        if measured:
            row["measuredOutput"] += r["output"]
            row["measuredMs"] += max(500, end - start)
        row["agents"].add(r["agentId"])
        empty = dict(requests=0, output=0, measuredOutput=0, measuredMs=0) if measured_models else dict(requests=0,
                                                                                                       output=0)
        model = row["models"].setdefault(r["model"] or "", empty)
        model["requests"] += 1
        model["output"] += r["output"]
        if measured_models and measured:
            model["measuredOutput"] += r["output"]
            model["measuredMs"] += max(500, end - start)
    for row in days.values():
        row["agents"] = len(row["agents"])
    return days


# --- Qoder IDE ---------------------------------------------------------------------------------------------------

CURRENT = """
    CREATE TABLE chat_session (
        session_id varchar(64) PRIMARY KEY, project_uri text, project_name text, gmt_create INTEGER,
        gmt_modified INTEGER, mode text, parent_session_id varchar(64), parent_tool_call_id varchar(64),
        session_type text);
    CREATE TABLE chat_message (
        id varchar(64) PRIMARY KEY, session_id varchar(64), request_id varchar(64), role text, content text,
        summary text, summary_modified INTEGER, summary_trigger text, tool_result text, token_info text,
        model_info text, extra text, gmt_create INTEGER);
    CREATE INDEX idx_chat_message_session ON chat_message (session_id);
    CREATE TABLE chat_record (
        request_id varchar(64) PRIMARY KEY, session_id varchar(64), question text, answer text, extra text,
        gmt_create INTEGER, gmt_modified INTEGER);
    CREATE TABLE agent_memory (id varchar(64) PRIMARY KEY, content text, gmt_create INTEGER);
"""
OLDER = """
    CREATE TABLE chat_session (
        session_id varchar(64) PRIMARY KEY, project_uri text, project_name text, gmt_create INTEGER,
        gmt_modified INTEGER, mode text);
    CREATE TABLE chat_message (
        id varchar(64) PRIMARY KEY, session_id varchar(64), role text, content text, token_info text,
        model_info text, gmt_create INTEGER);
"""
LEGACY = """
    CREATE TABLE chat_session (session_id varchar(64) PRIMARY KEY, project_uri text);
    CREATE TABLE chat_message (id varchar(64) PRIMARY KEY, session_id varchar(64), role text, content text,
        gmt_create INTEGER);
"""


def usage(prompt, completion, cached=None, window=200000):
    value = {"prompt_tokens": prompt, "completion_tokens": completion}
    if cached is not None:
        value["cached_tokens"] = cached
    value["max_input_tokens"] = window
    return compact(value)


def tier(key):
    return compact({"model_key": key})


def turn(started, finished, calls=1):
    return compact({"ai-coding/turn-info": {"businessIds": ["b-%d" % started], "userMessageCreatedAt": started,
                                            "startedAt": started + 40, "finishedAt": finished,
                                            "durationMs": finished - started, "status": "finished",
                                            "llmCallCount": calls}, "modelConfig": {"key": "auto"}})


def fresh(path):
    path.parent.mkdir(parents=True, exist_ok=True)
    for suffix in ("", "-wal", "-shm", "-journal"):
        if os.path.exists(str(path) + suffix):
            os.remove(str(path) + suffix)
    return sqlite3.connect(path)


def insert(db, table, values):
    names = list(values)
    db.execute("INSERT INTO %s (%s) VALUES (%s)" % (table, ", ".join(names), ", ".join("?" * len(names))),
               [values[n] for n in names])


def finish(db, path, wal=False):
    db.commit()
    if wal:
        db.execute("PRAGMA journal_mode=WAL")
        db.commit()
    db.close()
    for suffix in ("-wal", "-shm", "-journal"):
        assert not os.path.exists(str(path) + suffix), "a clean close leaves no side file"


def build(path, schema, sessions, rows, records=(), wal=False):
    """`sessions`: dicts of chat_session columns; `rows`: dicts of chat_message columns, inserted in order (rowid)."""
    db = fresh(path)
    db.executescript(schema)
    for session in sessions:
        insert(db, "chat_session", dict(dict(gmt_create=DAY0, gmt_modified=DAY0), **session))
    for row in rows:
        values = dict(row)
        if "content" not in values:
            values["content"] = SECRET
        insert(db, "chat_message", values)
    for request, session, extra in records:
        insert(db, "chat_record", dict(request_id=request, session_id=session, question=SECRET, answer=SECRET,
                                       extra=extra, gmt_create=DAY0, gmt_modified=DAY0))
    finish(db, path, wal)


def message(n, session, role, time, request=None, token_info=None, model_info=None, mid=None):
    row = dict(id=uid(1, n) if mid is None else mid, session_id=session, role=role, token_info=token_info,
               model_info=model_info, gmt_create=time)
    if request is not None:
        row["request_id"] = uid(2, request)
    return row


def write_ide(root):
    """The Qoder IDE databases and the expected results of every scenario."""
    intl = Path(DATA) / "Qoder" / "SharedClientCache/cache/db/local.db"
    china = Path(DATA) / CN_FOLDER / "SharedClientCache/cache/db/local.db"
    portable = Path("qoder/portable/user-data/SharedClientCache/cache/db/local.db")
    legacy = Path("qoder/legacy/SharedClientCache/cache/db/local.db")
    a = str(intl) + "#"

    # --- The international edition: current schema; rowid = position in this list --------------------------------
    rows = [
        message(1, "aurora", "user", at(10, 0, 0), 1),                                                       # 1
        message(2, "aurora", "assistant", at(10, 0, 5, 820), 1, usage(16846, 481, 2048), tier("auto")),      # 2
        message(3, "aurora", "user", at(10, 1, 0), 2),                                                       # 3
        message(4, "aurora", "assistant", at(10, 1, 2, 260), 2, usage(17407, 99, 17152), tier("auto")),      # 4
        message(5, "aurora", "tool", at(10, 1, 2, 265), 2),                                                  # 5
        # the sub-agent runs inside aurora's tool call; its rows interleave with aurora's
        message(6, "aurora-scout", "user", at(10, 1, 2, 300), 3),                                            # 6
        message(7, "aurora-scout", "assistant", at(10, 1, 3, 100), 3, usage(5000, 120, 0), tier("lite")),    # 7
        message(8, "aurora-scout", "tool", at(10, 1, 3, 150), 3),                                            # 8
        message(9, "aurora-scout", "assistant", at(10, 1, 3, 400), 3, usage(5300, 60, 5000), tier("lite")),  # 9
        message(10, "aurora", "assistant", at(10, 1, 4, 900), 2, usage(17631, 244, 17408), tier("auto")),    # 10
        # copper: its first row is a call (no start); then rows that are no calls or are malformed
        message(11, "copper", "assistant", at(10, 5, 0), 4, usage(1200, 300, 0), tier("performance")),      # 11
        message(12, "ghost", "assistant", at(10, 5, 10), 9, usage(999, 999, 0), tier("auto")),               # 12
        message(13, "copper", "assistant", at(10, 5, 30, 500), 4, '{"prompt_tokens": 10,', tier("auto")),    # 13
        message(14, "copper", "assistant", at(10, 5, 40), 4, "[10, 20]", tier("auto")),                     # 14
        message(15, "copper", "user", None, 5),                                                              # 15
        message(16, "copper", "assistant", at(10, 6, 0), 5,
                compact({"prompt_tokens": 12.9, "completion_tokens": 7, "cached_tokens": 40}), tier("   ")),  # 16
        message(17, "copper", None, at(10, 6, 10), 5),                                                       # 17
        message(18, "copper", "assistant", at(10, 6, 20), 5, usage(0, 0, 0), tier("auto")),                 # 18
        message(19, "copper", "user", at(10, 6, 30, 750) + 0.25, 6),                                         # 19
        message(20, "copper", "assistant", at(10, 27, 0), 6,
                compact({"prompt_tokens": 800, "completion_tokens": 50, "cached_tokens": "x"}), tier(" efficient "),
                mid="  "),                                                                                   # 20
        # delta: its last call was written after the tool row, with an earlier time
        message(21, "delta", "user", at(10, 30, 0), 7),                                                      # 21
        message(22, "delta", "assistant", at(10, 30, 4), 7, usage(3000, 200, 1000), tier("auto")),           # 22
        message(23, "delta", "tool", at(10, 30, 4, 100), 7),                                                 # 23
        message(24, "delta", "assistant", at(10, 30, 3), 7, usage(3100, 50, 3000), tier("auto")),            # 24
        # two sessions whose calls start below the startup window of scenario `startup`
        message(25, "night-owl", "user", at(10, 44, 0), 8),                                                  # 25
        message(26, "orphan", "user", at(10, 44, 30), 10),                                                   # 26
        message(27, "night-owl", "assistant", at(10, 45, 10), 8, usage(2400, 180, 1024),
                tier("claude-sonnet-4-5")),                                                                  # 27
        message(28, "orphan", "assistant", at(10, 45, 12), 10, compact({"prompt_tokens": 2000,
                                                                        "completion_tokens": 30}), "not json"),  # 28
    ]
    sessions = [
        dict(session_id="aurora", project_uri="/Users/dev/projects/aurora", project_name="aurora", mode="agent",
             session_type="chat"),
        dict(session_id="aurora-scout", project_uri="file:///Users/dev/projects/aurora%20labs",
             project_name="aurora labs", mode="agent", parent_session_id="aurora", parent_tool_call_id="call-scout",
             session_type="subagent"),
        dict(session_id="copper", project_uri="C:\\Users\\dev\\work\\ledger", project_name="ledger", mode="agent"),
        dict(session_id="delta", project_uri="/Users/dev/projects/delta", project_name="delta", mode="agent",
             parent_session_id="   "),
        dict(session_id="night-owl", project_uri=None, project_name=None, mode="agent"),
        dict(session_id="orphan", project_uri="/Users/dev/projects/orphan", project_name="orphan", mode="agent",
             parent_session_id="missing-parent", session_type="subagent"),
        dict(session_id="empty", project_uri="/Users/dev/projects/empty", project_name="empty", mode="agent"),
    ]
    records = [
        (uid(2, 1), "aurora", turn(at(10, 0, 0), at(10, 0, 5, 900))),
        (uid(2, 2), "aurora", turn(at(10, 1, 0), at(10, 1, 5), 2)),
        (uid(2, 4), "copper", compact({"modelConfig": {"key": "performance"}})),
        (uid(2, 5), "copper", "{not json"),
        (uid(2, 6), "copper", turn(at(10, 6, 30), at(10, 26, 0))),
        (uid(2, 7), "delta", turn(at(10, 30, 0), at(10, 30, 5), 2)),
        (uid(2, 8), "night-owl", turn(at(10, 44, 0), at(10, 45, 10))),
    ]
    build(root / intl, CURRENT, sessions, rows, records, wal=True)
    state = root / DATA / "Qoder/User/globalStorage/state.vscdb"
    db = fresh(state)
    db.execute("CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)")
    db.execute("INSERT INTO ItemTable VALUES (?, ?)", ("chat.usage", compact({"prompt_tokens": 999999})))
    finish(db, state)

    def key(n):
        return "qoder:" + uid(1, n)

    intl_agents = {
        a + "aurora": agent_view("aurora", "/Users/dev/projects/aurora", "auto", "idle", at(10, 1, 4, 900), [
            response(key(2), a + "aurora", at(10, 0, 0), at(10, 0, 5, 820), 16846, 2048, 481, "auto"),
            response(key(4), a + "aurora", at(10, 1, 0), at(10, 1, 2, 260), 17407, 17152, 99, "auto"),
            response(key(10), a + "aurora", at(10, 1, 2, 265), at(10, 1, 4, 900), 17631, 17408, 244, "auto")]),
        a + "aurora-scout": agent_view(
            "aurora labs", "/Users/dev/projects/aurora labs", "lite", "tools", at(10, 1, 3, 400), [
                response(key(7), a + "aurora-scout", at(10, 1, 2, 300), at(10, 1, 3, 100), 5000, 0, 120, "lite"),
                response(key(9), a + "aurora-scout", at(10, 1, 3, 150), at(10, 1, 3, 400), 5300, 5000, 60, "lite")],
            subagent=True, parent=a + "aurora"),
        a + "copper": agent_view("ledger", "C:\\Users\\dev\\work\\ledger", "efficient", "tools", at(10, 27, 0), [
            response(key(11), a + "copper", None, at(10, 5, 0), 1200, 0, 300, "performance"),
            response(key(16), a + "copper", None, at(10, 6, 0), 12, 12, 7, None),
            response("qoder:copper:row:20", a + "copper", at(10, 6, 30, 750), at(10, 27, 0), 800, 0, 50, "efficient")],
            malformed=2),
        a + "delta": agent_view("delta", "/Users/dev/projects/delta", "auto", "generating", at(10, 30, 4, 100), [
            response(key(22), a + "delta", at(10, 30, 0), at(10, 30, 4), 3000, 1000, 200, "auto"),
            response(key(24), a + "delta", at(10, 30, 4, 100), at(10, 30, 3), 3100, 3000, 50, "auto")],
            in_flight=at(10, 30, 4, 100)),
        a + "night-owl": agent_view("night-owl", None, "claude-sonnet-4-5", "idle", at(10, 45, 10), [
            response(key(27), a + "night-owl", at(10, 44, 0), at(10, 45, 10), 2400, 1024, 180, "claude-sonnet-4-5")]),
        a + "orphan": agent_view("orphan", "/Users/dev/projects/orphan", None, "tools", at(10, 45, 12), [
            response(key(28), a + "orphan", at(10, 44, 30), at(10, 45, 12), 2000, 0, 30, None)], subagent=True),
    }
    intl_sessions = {a + "aurora": dict(rows=6, highestRowid=10), a + "aurora-scout": dict(rows=4, highestRowid=9),
                     a + "copper": dict(rows=9, highestRowid=20), a + "delta": dict(rows=4, highestRowid=24),
                     a + "night-owl": dict(rows=2, highestRowid=27), a + "orphan": dict(rows=2, highestRowid=28)}

    # --- The China edition: an older schema, no request ids, no turn records -----------------------------------
    build(root / china, OLDER, [dict(session_id="lumen", project_uri="/Users/dev/projects/lumen", project_name="lumen",
                                     mode="agent")], [
        dict(id=uid(1, 101), session_id="lumen", role="user", gmt_create=at(11, 0, 0)),
        dict(id=uid(1, 102), session_id="lumen", role="assistant", gmt_create=at(11, 0, 3, 500),
             token_info=usage(9000, 150, 8192, 128000), model_info=tier("ultimate")),
    ])
    c = str(china) + "#"
    china_agents = {c + "lumen": agent_view("lumen", "/Users/dev/projects/lumen", "ultimate", "tools",
                                            at(11, 0, 3, 500), [
        response("qoder:" + uid(1, 102), c + "lumen", at(11, 0, 0), at(11, 0, 3, 500), 9000, 8192, 150, "ultimate")])}
    china_sessions = {c + "lumen": dict(rows=2, highestRowid=2)}

    # --- A portable data folder: a copy of aurora (skipped), a call the first database owns (ignored) ------------
    build(root / portable, CURRENT, [
        dict(session_id="aurora", project_uri="/Users/dev/other", project_name="other", mode="agent"),
        dict(session_id="harbor", project_uri="/Users/dev/projects/harbor", project_name="harbor", mode="agent"),
    ], [
        message(201, "aurora", "assistant", at(10, 0, 5, 820), 1, usage(99999, 9999, 0), tier("auto")),
        message(202, "harbor", "user", at(11, 30, 0), 201),
        message(2, "harbor", "assistant", at(11, 30, 2), 201, usage(16846, 481, 2048), tier("auto")),
        message(203, "harbor", "user", at(11, 31, 0), 202),
        message(204, "harbor", "assistant", at(11, 31, 4), 202, usage(4000, 90, 0), tier("auto")),
    ], [(uid(2, 202), "harbor", turn(at(11, 31, 0), at(11, 31, 4, 500)))])
    p = str(portable) + "#"
    portable_agents = {p + "harbor": agent_view("harbor", "/Users/dev/projects/harbor", "auto", "idle", at(11, 31, 4), [
        response("qoder:" + uid(1, 204), p + "harbor", at(11, 31, 0), at(11, 31, 4), 4000, 0, 90, "auto")])}
    portable_sessions = {p + "harbor": dict(rows=4, highestRowid=5)}

    # --- A database without token_info and model_info: unsupported ---------------------------------------------
    db = fresh(root / legacy)
    db.executescript(LEGACY)
    insert(db, "chat_session", dict(session_id="relic", project_uri="/Users/dev/projects/relic"))
    insert(db, "chat_message", dict(id=uid(1, 301), session_id="relic", role="assistant", content=SECRET,
                                    gmt_create=at(9, 0, 0)))
    finish(db, root / legacy)

    one = dict(locations=[DATA + "/Qoder"], databases=[str(intl)], agents=intl_agents, sessions=intl_sessions)
    two = dict(locations=["@auto", "qoder/portable/user-data/SharedClientCache/cache/db", "qoder/legacy"],
               home="qoder/home", databases=[str(intl), str(china), str(portable), str(legacy)],
               unsupported=[str(legacy)], agents=dict(intl_agents, **china_agents, **portable_agents),
               sessions=dict(intl_sessions, **china_sessions, **portable_sessions))
    for scenario in (one, two):
        scenario["history"] = day_totals([r for v in scenario["agents"].values() for r in v["responses"]])
    # The startup read of a day that starts at 11:00 (window from 10:45): rows 27 and 28, and the times of rows 25
    # and 26 (each session's row just below the window) as the start of their calls.
    startup = dict(locations=[DATA + "/Qoder"], todayStartMs=at(11, 0, 0), databases=[str(intl)],
                   agents={k: intl_agents[k] for k in (a + "night-owl", a + "orphan")})
    resolve = {
        DATA + "/Qoder": str(intl),
        DATA + "/Qoder/SharedClientCache": str(intl),
        DATA + "/Qoder/SharedClientCache/cache/db": str(intl),
        DATA + "/Qoder/SharedClientCache/cache": None,
        DATA + "/" + CN_FOLDER + "/SharedClientCache": str(china),
        "qoder/portable/user-data": str(portable),
        "qoder/legacy": str(legacy),
        DATA + "/Qoder/User/globalStorage": None,
        "qoder/missing": None,
    }
    return dict(resolve=resolve, scenarios=dict(one=one, two=two, startup=startup))


def automatic_cases():
    """The automatic entry's databases per platform (METRICS 4.18), as path computations."""
    tail = ("SharedClientCache", "cache", "db", "local.db")

    def posix(data):
        return ["/".join((data, edition) + tail) for edition in ("Qoder", CN_FOLDER)]

    def windows(data):
        return ["\\".join((data, edition) + tail) for edition in ("Qoder", CN_FOLDER)]

    return [
        dict(platform="macos", env={}, home="/Users/dev", databases=posix("/Users/dev/Library/Application Support")),
        dict(platform="macos", env={"XDG_CONFIG_HOME": "/Users/dev/.cfg"}, home="/Users/dev",
             databases=posix("/Users/dev/Library/Application Support")),
        dict(platform="windows", env={"APPDATA": "C:\\Users\\dev\\AppData\\Roaming"}, home="C:\\Users\\dev",
             databases=windows("C:\\Users\\dev\\AppData\\Roaming")),
        dict(platform="linux", env={"XDG_CONFIG_HOME": "/home/dev/.cfg"}, home="/home/dev",
             databases=posix("/home/dev/.cfg")),
        dict(platform="linux", env={"XDG_CONFIG_HOME": ""}, home="/home/dev", databases=posix("/home/dev/.config")),
    ]


# --- Qoder CLI ---------------------------------------------------------------------------------------------------

VERSION = "1.1.86"
CLI_ROOT = "qoder-cli/projects"


def encoded(cwd):
    return "".join(ch if ch.isascii() and ch.isalnum() else "-" for ch in cwd)


class Transcript:
    """The lines of one Qoder CLI session file, each conversation line linked to the previous one."""

    def __init__(self, session, cwd, sidechain=False, agent=None):
        self.session, self.cwd, self.sidechain, self.agent = session, cwd, sidechain, agent
        self.lines, self.parent = [], None

    def own(self, kind, ms=None, **fields):
        row = dict(type=kind, **fields)
        row["sessionId"] = self.session
        if ms is not None:
            row["timestamp"] = ms
        self.lines.append(row)

    def entry(self, kind, ms, uuid, body):
        row = dict(parentUuid=self.parent, isSidechain=self.sidechain, userType="external", cwd=self.cwd,
                   sessionId=self.session, version=VERSION, gitBranch="main", entrypoint="cli")
        if self.agent:
            row["agentId"] = self.agent
        row.update(type=kind, **body)
        row.update(uuid=uuid, timestamp=iso(ms))
        self.lines.append(row)
        self.parent = uuid
        return row

    def user(self, ms, n, content, **extra):
        return self.entry("user", ms, uid(3, n), dict(message=dict(role="user", content=content), **extra))

    def result(self, ms, n, call, assistant_line, text):
        return self.user(ms, n, [dict(type="tool_result", tool_use_id=uid(4, call), content=text)],
                         sourceToolAssistantUUID=uid(3, assistant_line))

    def assistant(self, ms, n, mid, blocks, stop, model="auto", tokens=None, **extra):
        body = {} if mid is None else dict(id=uid(1, mid))
        body.update(type="message", role="assistant", model=model, content=blocks, stop_reason=stop,
                    stop_sequence=None)
        if tokens is not None:
            body["usage"] = tokens
        return self.entry("assistant", ms, uid(3, n), dict(message=body, **extra))

    def copy(self, line):
        row = dict(line, sessionId=self.session)
        self.lines.append(row)
        self.parent = row.get("uuid", self.parent)

    def write(self, path):
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            for row in self.lines:
                handle.write(compact(row) + "\n")


def exposed(inp, out, read, write=0, request=1, ratio=0.1):
    """Usage as Qoder CLI keeps it with QODER_EXPOSE_TOKEN_USAGE=1 (input_tokens includes the cache read)."""
    return dict(input_tokens=inp, cache_creation_input_tokens=write, cache_read_input_tokens=read, output_tokens=out,
                request_id=uid(2, 400 + request), context_usage_ratio=ratio, service_tier="standard",
                speed="standard", iterations=1)


def text(value):
    return dict(type="text", text=value)


def tool_use(call, name, value):
    return dict(type="tool_use", id=uid(4, call), name=name, input=value)


def cli_file(session, cwd, sub=None):
    base = "%s/%s/%s" % (CLI_ROOT, encoded(cwd), session)
    return base + ".jsonl" if sub is None else "%s/subagents/%s.jsonl" % (base, sub)


def cli_expect(model, project, phase, last, responses, in_flight=None, subagent=False, parent=None):
    return dict(source="qoderCli", project=project, model=model, isSubagent=subagent, parentFile=parent, phase=phase,
                inFlightStartMs=in_flight, lastLineMs=last, malformedLines=0, responses=responses, actions=[],
                effort=None, speed=None, responseModes={r["key"]: {"effort": None, "speed": None} for r in responses})


def cli_response(mid, start, end, inp, cached, out, model="auto", write=0, write1h=0):
    return response("qodercli:" + mid, None, start, end, inp, cached, out, model, write, write1h)


def write_cli_fixtures(root):
    """Qoder CLI sessions; returns the parser-expected.json shape (`order`, `files`, `mtimes`) plus `discover`,
    `traces` (phase after a physical line, after the files before it in `order`) and `history` (UTC day totals)."""
    root = Path(root)
    atlas, borealis = "/Users/dev/projects/atlas", "/Users/dev/projects/borealis"
    s1, s3, s4, s5 = ("3f2c9a10-7b1e-4c8d-9a21-5e6f70819a2b", "8c41d2e7-2f90-4b6a-a3c5-91e07d4b6f18",
                      "c7a9e3f1-5b2d-4e8a-9c10-2d3f4a5b6c7d", "e2b8f4a6-1c3d-4f5e-8a7b-9c0d1e2f3a4b")
    files, mtimes, traces = {}, {}, []

    def trace(rel, line, phase, in_flight=None):
        traces.append(dict(file=rel, afterLine=line, phase=phase, inFlightStartMs=in_flight))

    # ---- An exposed run (QODER_EXPOSE_TOKEN_USAGE=1): two calls, input_tokens includes the cache read.
    main_rel = cli_file(s1, atlas)
    prompt = "Read notes.txt and summarize it in two sentences."
    t = Transcript(s1, atlas)
    t.own("runtime-config", cli_at(13, 3, 14, 850), model="auto", reasoningEffort=None, contextWindow=200000)  # 1
    t.own("workspace-directories", directories=[atlas])                                                        # 2
    first_prompt = t.user(cli_at(13, 3, 14, 903), 1, prompt)                                                    # 3
    attachment = t.entry("attachment", cli_at(13, 3, 14, 905), uid(3, 2),                                       # 4
                         dict(attachment=dict(type="nested_memory", path=atlas + "/AGENTS.md")))
    m1 = t.assistant(cli_at(13, 3, 18, 25), 3, 1, [text("Reading the notes."), tool_use(
        1, "Read", {"file_path": atlas + "/notes.txt"})], "tool_use", tokens=exposed(20341, 92, 20096))         # 5
    r1 = t.result(cli_at(13, 3, 18, 131), 4, 1, 3, "1\tShip the beta on Friday.")                              # 6
    m2 = t.assistant(cli_at(13, 3, 21, 662), 5, 2, [text("The notes plan the beta for Friday.")], "end_turn",
                     tokens=exposed(20478, 107, 20096, request=2))                                             # 7
    t.own("active-leaf", cli_at(13, 3, 21, 700), leafUuid=uid(3, 5))                                           # 8
    t.own("last-prompt", lastPrompt=prompt)                                                                    # 9
    t.write(root / main_rel)
    files[main_rel] = cli_expect("auto", "atlas", "idle", cli_at(13, 3, 21, 662), [
        cli_response(uid(1, 1), cli_at(13, 3, 14, 903), cli_at(13, 3, 18, 25), 20341, 20096, 92),
        cli_response(uid(1, 2), cli_at(13, 3, 18, 131), cli_at(13, 3, 21, 662), 20478, 20096, 107)])
    mtimes[main_rel] = cli_at(13, 3, 23)
    trace(main_rel, 4, "generating", cli_at(13, 3, 14, 903))
    trace(main_rel, 5, "tools")
    trace(main_rel, 6, "generating", cli_at(13, 3, 18, 131))

    # ---- Its sub-agent: a custom Anthropic model whose input_tokens excludes the cache, speed "fast" (fast stays
    # false), and a line without message.id (the key falls back to the line's uuid).
    sub_rel = cli_file(s1, atlas, "agent-5d1e7a")
    t = Transcript(s1, atlas, sidechain=True, agent="5d1e7a")
    t.user(cli_at(13, 3, 18, 200), 11, "Find every TODO marker in notes.txt.")                                  # 1
    custom = dict(input_tokens=12, cache_creation_input_tokens=1800, cache_read_input_tokens=4000, output_tokens=64,
                  cache_creation=dict(ephemeral_5m_input_tokens=1200, ephemeral_1h_input_tokens=600),
                  service_tier="standard", speed="fast")
    t.assistant(cli_at(13, 3, 19, 950), 12, 3, [tool_use(2, "Grep", {"pattern": "TODO"})], "tool_use",
                model="claude-sonnet-4-5", tokens=custom)                                                       # 2
    t.result(cli_at(13, 3, 20, 10), 13, 2, 12, "No matches found")                                              # 3
    equal = dict(input_tokens=3000, cache_creation_input_tokens=500, cache_read_input_tokens=2500, output_tokens=41,
                 service_tier="standard")
    t.assistant(cli_at(13, 3, 21, 300), 14, None, [text("There are no TODO markers.")], "end_turn",
                model="claude-sonnet-4-5", tokens=equal)                                                        # 4
    t.write(root / sub_rel)
    files[sub_rel] = cli_expect("claude-sonnet-4-5", "atlas", "idle", cli_at(13, 3, 21, 300), [
        cli_response(uid(1, 3), cli_at(13, 3, 18, 200), cli_at(13, 3, 19, 950), 5812, 4000, 64, "claude-sonnet-4-5",
                     1800, 600),
        cli_response(uid(3, 14), cli_at(13, 3, 20, 10), cli_at(13, 3, 21, 300), 3000, 2500, 41, "claude-sonnet-4-5",
                     500)], subagent=True, parent=main_rel)
    mtimes[sub_rel] = cli_at(13, 3, 24)

    # ---- The default run: Qoder removed the usage of the first call; then a prompt the user interrupts.
    plain_rel = cli_file(s3, borealis)
    t = Transcript(s3, borealis)
    t.own("runtime-config", cli_at(13, 19, 59, 950), model="auto", reasoningEffort=None, contextWindow=200000)  # 1
    p1 = t.user(cli_at(13, 20, 0), 21, "List the failing tests.")                                              # 2
    m5 = t.assistant(cli_at(13, 20, 3, 120), 22, 5, [tool_use(5, "Bash", {"command": "npm test"})], "tool_use")  # 3
    r5 = t.result(cli_at(13, 20, 3, 300), 23, 5, 22, "2 failing")                                              # 4
    # The second call kept usage with billing fields, its tokens written as 0: no usage (4.19).
    m6 = t.assistant(cli_at(13, 20, 6, 800), 24, 6, [text("Two tests fail.")], "end_turn",                      # 5
                     tokens=dict(input_tokens=0, cache_creation_input_tokens=0, cache_read_input_tokens=0,
                                 output_tokens=0, billable=True, credits=0.0191))
    t.user(cli_at(13, 25, 0), 25, "Explain the second failure.")                                               # 6
    t.user(cli_at(13, 25, 2), 26, [text("[Request interrupted by user]")])                                     # 7
    t.own("last-prompt", lastPrompt="Explain the second failure.")                                             # 8
    t.write(root / plain_rel)
    files[plain_rel] = cli_expect("auto", "borealis", "idle", cli_at(13, 25, 2), [])
    mtimes[plain_rel] = cli_at(13, 25, 4)
    trace(plain_rel, 3, "tools")
    trace(plain_rel, 4, "generating", cli_at(13, 20, 3, 300))
    trace(plain_rel, 5, "idle")
    trace(plain_rel, 6, "generating", cli_at(13, 25, 0))

    # ---- A fork of the exposed run: its first lines are copies (same message ids, original times), ignored.
    fork_rel = cli_file(s4, atlas)
    t = Transcript(s4, atlas)
    t.own("runtime-config", cli_at(13, 39, 59, 900), model="auto", reasoningEffort=None, contextWindow=200000)  # 1
    for line in (first_prompt, m1, r1, m2):                                                                    # 2-5
        t.copy(line)
    t.user(cli_at(13, 40, 0), 31, "Add a summary section to notes.txt.")                                       # 6
    t.assistant(cli_at(13, 40, 5, 500), 32, 7, [tool_use(7, "Edit", {"file_path": atlas + "/notes.txt"})],
                "tool_use", tokens=exposed(20900, 310, 20480, request=7))                                      # 7
    t.result(cli_at(13, 40, 5, 700), 33, 7, 32, "Edited notes.txt")                                            # 8
    t.assistant(cli_at(13, 40, 7, 200), 34, 8, [text("Added the summary.")], "end_turn",
                tokens=exposed(21250, 58, 20736, request=8))                                                   # 9
    t.write(root / fork_rel)
    files[fork_rel] = cli_expect("auto", "atlas", "idle", cli_at(13, 40, 7, 200), [
        cli_response(uid(1, 7), cli_at(13, 40, 0), cli_at(13, 40, 5, 500), 20900, 20480, 310),
        cli_response(uid(1, 8), cli_at(13, 40, 5, 700), cli_at(13, 40, 7, 200), 21250, 20736, 58)])
    mtimes[fork_rel] = cli_at(13, 40, 9)
    trace(fork_rel, 3, "generating", cli_at(13, 3, 14, 903))
    trace(fork_rel, 5, "generating", cli_at(13, 3, 18, 131))

    # ---- A fork of the default run: the copied calls without usage are owned by the file they came from, so they
    # change nothing here either.
    plain_fork_rel = cli_file(s5, borealis)
    t = Transcript(s5, borealis)
    for line in (p1, m5, r5, m6):                                                                              # 1-4
        t.copy(line)
    t.user(cli_at(13, 50, 0), 41, "Run them again.")                                                           # 5
    t.assistant(cli_at(13, 50, 4), 42, 9, [tool_use(9, "Bash", {"command": "npm test"})], "tool_use")          # 6
    t.write(root / plain_fork_rel)
    files[plain_fork_rel] = cli_expect("auto", "borealis", "tools", cli_at(13, 50, 4), [])
    mtimes[plain_fork_rel] = cli_at(13, 50, 6)
    trace(plain_fork_rel, 2, "generating", cli_at(13, 20, 0))
    trace(plain_fork_rel, 4, "generating", cli_at(13, 20, 3, 300))
    trace(plain_fork_rel, 5, "generating", cli_at(13, 50, 0))

    # ---- An event log: Qoder CLI's own record of the run, outside the projects folder; never read.
    log = root / "qoder-cli/logs/sessions" / encoded(atlas) / s1 / "segments/run-1.jsonl"
    log.parent.mkdir(parents=True, exist_ok=True)
    log.write_text(compact(dict(type="turn.finished", sessionId=s1, timestamp=cli_at(13, 3, 21, 700),
                                usage=dict(input_tokens=0, output_tokens=0), total_credits=0.191)) + "\n",
                   encoding="utf-8")

    order = sorted(mtimes, key=lambda rel: (mtimes[rel], rel))
    for rel in order:
        os.utime(root / rel, (mtimes[rel] / 1000, mtimes[rel] / 1000))
    history = day_totals([dict(r, agentId=rel) for rel in order for r in files[rel]["responses"]],
                         measured_models=True)
    return dict(order=order, mtimes=mtimes, discover=sorted(files), files={rel: files[rel] for rel in order},
                traces=traces, history=history)


def cli_cases():
    return dict(
        automatic=[
            dict(platform="macos", env={}, home="/Users/dev",
                 roots=["/Users/dev/.qoder/projects", "/Users/dev/.qoder-cn/projects"]),
            dict(platform="linux", env={"QODER_CONFIG_DIR": ""}, home="/home/dev",
                 roots=["/home/dev/.qoder/projects", "/home/dev/.qoder-cn/projects"]),
            dict(platform="linux", env={"QODER_CONFIG_DIR": "/srv/qoder"}, home="/home/dev",
                 roots=["/srv/qoder/projects", "/home/dev/.qoder-cn/projects"]),
            dict(platform="linux", env={"QODERCN_CONFIG_DIR": "/srv/qoder-cn"}, home="/home/dev",
                 roots=["/home/dev/.qoder/projects", "/srv/qoder-cn/projects"]),
            dict(platform="windows", env={}, home="C:\\Users\\dev",
                 roots=["C:\\Users\\dev\\.qoder\\projects", "C:\\Users\\dev\\.qoder-cn\\projects"]),
        ],
        locations={"/Users/dev/.qoder": "/Users/dev/.qoder/projects",
                   "/Users/dev/.qoder/projects": "/Users/dev/.qoder/projects",
                   "/data/qoder-cn": "/data/qoder-cn/projects"})


def write_fixtures(root):
    root = Path(root)
    ide = write_ide(root)
    cli = write_cli_fixtures(root)
    return dict(
        _comment="Qoder IDE (METRICS 4.18) and Qoder CLI (4.19), checked by spec/tools/qoder_reference.py; the Qoder "
                 "CLI files themselves are in parser-expected.json (`qoderCli`). Paths are relative to the fixtures "
                 "folder. `automatic`: the automatic entry's databases per platform. Each scenario reads its "
                 "`locations` (list order) completely, as history does, unless it has `todayStartMs`: then it is the "
                 "startup read of that day (window from todayStartMs - 15 min). `@auto` is the automatic entry with "
                 "`home` as the macOS home folder (on Windows and Linux: APPDATA or XDG_CONFIG_HOME = "
                 "<home>/Library/Application Support). `agents` lists every agent (a session with a row read); "
                 "`sessions` the history identity of each (METRICS 4.18 History); `history` the UTC day totals of the "
                 "listed responses. `cli.history` is the UTC day totals of the Qoder CLI responses of "
                 "parser-expected.json (history-expected.json, source qoderCli).",
        version=1,
        automatic=automatic_cases(),
        resolve=ide["resolve"],
        scenarios=ide["scenarios"],
        cli=dict(cli_cases(), history=cli["history"]))


def main():
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "fixtures"
    expected = write_fixtures(root)
    with open(root / "qoder-expected.json", "w", encoding="utf-8", newline="\n") as handle:
        handle.write(json.dumps(expected, indent=1, ensure_ascii=False) + "\n")


if __name__ == "__main__":
    main()
