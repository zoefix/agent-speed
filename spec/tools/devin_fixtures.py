#!/usr/bin/env python3
"""Devin CLI fixtures (METRICS 4.17): two session databases, with hand-computed expectations.

  python3 spec/tools/devin_fixtures.py [FIXTURES_DIR]

FIXTURES_DIR/devin holds:
  share/devin          a Devin data folder: cli/sessions.db (rollback journal)
  second               a CLI folder: sessions.db (WAL header, no -wal file: opened immutable)
  Devin                a Devin Desktop user-data folder with an event file: not a Devin location, never read
Scenario `one` reads [share/devin, Devin]; scenario `two` reads [share/devin, second]. Every value is synthetic.
Expected responses and day totals are computed here from what this file writes, never by a reader.
"""
import json
import os
import sqlite3
import sys
from pathlib import Path

DAY0 = 1792454400  # 2026-10-20T00:00:00Z, seconds


def at(h, m, s=0):
    """Unix seconds of 2026-10-20 hh:mm:ss UTC."""
    return DAY0 + h * 3600 + m * 60 + s


SCHEMA = """
    CREATE TABLE sessions (
        id TEXT PRIMARY KEY, working_directory TEXT NOT NULL, backend_type TEXT NOT NULL DEFAULT 'windsurf',
        model TEXT NOT NULL, title TEXT, agent_mode TEXT NOT NULL DEFAULT 'normal', hidden INTEGER NOT NULL DEFAULT 0,
        main_chain_id INTEGER, created_at INTEGER NOT NULL, last_activity_at INTEGER NOT NULL);
    CREATE TABLE message_nodes (
        row_id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, node_id INTEGER NOT NULL,
        parent_node_id INTEGER, chat_message TEXT NOT NULL, created_at INTEGER NOT NULL, metadata TEXT);
    CREATE TABLE tool_call_state (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, tool_call_json TEXT,
        tool_call_update_json TEXT, status TEXT);
"""


def compact(value):
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False)


def user(text="(prompt, never read)"):
    return compact(dict(role="user", content=text, metadata=dict(is_user_input=True, num_tokens=None)))


def tool_result(call_id):
    return compact(dict(role="tool", tool_call_id=call_id, content="(tool output, never read)"))


def assistant(metrics=None, request=None, model=None, finish=None, tools=None, extra=None):
    metadata = dict(num_tokens=None, is_user_input=False)
    for name, value in (("request_id", request), ("generation_model", model), ("finish_reason", finish),
                        ("metrics", metrics)):
        if value is not None:
            metadata[name] = value
    if extra:
        metadata.update(extra)
    message = dict(role="assistant", content="(answer, never read)", metadata=metadata)
    if tools:
        message["tool_calls"] = [dict(id=t, type="function", function=dict(name="read_file", arguments="{}"))
                                 for t in tools]
    return compact(message)


def metrics(inp, out, read=None, write=None, total=None, ttft=None):
    value = dict(input_tokens=inp, output_tokens=out)
    for name, item in (("cache_read_tokens", read), ("cache_creation_tokens", write), ("total_time_ms", total),
                       ("ttft_ms", ttft)):
        if item is not None:
            value[name] = item
    value["tokens_per_sec"] = 50.0
    return value


def response(key, agent, end, inp, cached, write, out, model, start=None, first=None):
    return dict(key=key, agentId=agent, startMs=start, endMs=end, firstMs=first, input=inp, cachedInput=cached,
                cacheWriteInput=write, cacheWrite1hInput=0, output=out, reasoning=0, model=model, provider=None,
                final=True, fast=False)


def build_database(path, sessions, rows, wal=False):
    path.parent.mkdir(parents=True, exist_ok=True)
    for suffix in ("", "-wal", "-shm", "-journal"):
        if os.path.exists(str(path) + suffix):
            os.remove(str(path) + suffix)
    db = sqlite3.connect(path)
    db.executescript(SCHEMA)
    for sid, cwd, model, title, hidden in sessions:
        db.execute("INSERT INTO sessions(id, working_directory, model, title, hidden, created_at, last_activity_at) "
                   "VALUES (?,?,?,?,?,?,?)", (sid, cwd, model, title, hidden, DAY0, DAY0))
    for node, (sid, message, created) in enumerate(rows, 1):
        db.execute("INSERT INTO message_nodes(session_id, node_id, parent_node_id, chat_message, created_at, metadata) "
                   "VALUES (?,?,?,?,?,NULL)", (sid, node, node - 1 or None, message, created))
    db.commit()
    if wal:
        db.execute("PRAGMA journal_mode=WAL")
        db.commit()
    db.close()
    for suffix in ("-wal", "-shm"):
        assert not os.path.exists(str(path) + suffix), "a clean close removes the WAL"


def desktop_usage_line():
    """A Devin Desktop event line: the folder that holds it is no Devin location (METRICS 4.17)."""
    return compact(dict(notification=dict(sessionUpdate="usage_update", used=999999, size=200000, _meta={
        "cognition.ai/inputTokens": 999999, "cognition.ai/outputTokens": 999999}))) + "\n"


def write_fixtures(root):
    root = Path(root)
    base = root / "devin"
    cli1 = Path("devin/share/devin/cli/sessions.db")
    cli2 = Path("devin/second/sessions.db")
    a1 = str(cli1) + "#aurora-lantern"
    c1 = str(cli1) + "#copper-meadow"
    q1 = str(cli1) + "#quiet-harbor"
    o1 = str(cli1) + "#night-owl"
    n2 = str(cli2) + "#north-wind"

    # --- Database 1: three sessions with calls, two with titles only -----------------------------------------
    a_first = metrics(1200, 340, 8000, 200, 4200, 1100)
    a_second = metrics(300, 90, 9100, 0, 2500, 900)
    rows = [
        ("aurora-lantern", user(), at(10, 0, 0)),
        ("aurora-lantern", assistant(a_first, "req-a1", "swe-1-6", "tool_calls", ["call_1"]), at(10, 0, 5)),
        ("aurora-lantern", assistant(a_first, "req-a1", "swe-1-6", "tool_calls", ["call_1"],
                                     {"extensions": {"chisel/tool_call_content": {"call_1": "(rendered)"}}}),
         at(10, 0, 5)),
        ("aurora-lantern", tool_result("call_1"), at(10, 0, 7)),
        ("aurora-lantern", assistant(a_second, "req-a2", "swe-1-6", "stop"), at(10, 0, 10)),
        ("aurora-lantern", assistant(a_second, "req-a2", "swe-1-6", "stop",
                                     extra={"extensions": {"chisel/tool_call_content": {}}}), at(10, 0, 10)),
        ("aurora-lantern", "{not json", at(10, 0, 11)),
        ("aurora-lantern", user(), at(10, 2, 0)),
        # row 9: no request id, no generation model; 12.9 → 12, a text cache count → 0, ttft above the total time
        ("copper-meadow", assistant(dict(input_tokens=12.9, output_tokens=7, cache_read_tokens="x", total_time_ms=300,
                                         ttft_ms=400)), at(10, 5, 0)),
        ("copper-meadow", assistant(finish="stop"), at(10, 5, 30)),
        ("copper-meadow", assistant(metrics(0, 0, 0, 0, 1000)), at(10, 6, 0)),
        # row 12: a blank request id and model; a negative total time gives no start
        ("copper-meadow", assistant(metrics(5, 2, total=-1, ttft=1), "   ", "  "), at(10, 7, 0)),
        # row 13: a real time with a fraction of a second
        ("copper-meadow", assistant(metrics(1, 1, total=2000), "req-c5", "gpt-5-6-sol-max"), at(10, 8, 0) + 0.75),
        # row 14: a time that is not a number
        ("copper-meadow", assistant(metrics(1, 1)), "soon"),
        ("quiet-harbor", '{"role":"assistant","metadata":{"request_id":"req-q1","metrics":{"input_tokens":50,'
                         '"output_tokens":20,"total_time_ms":1e999}}}', at(10, 10, 0)),
        ("quiet-harbor", '{"role":"assistant","metadata":{"metrics":{"input_tokens":NaN}}}', at(10, 10, 30)),
        # a session without a working directory: its project is the session id
        ("night-owl", assistant(metrics(70, 30, total=900), "req-o1", "swe-1-7", "stop"), at(10, 20, 0)),
    ]
    sessions = [
        ("aurora-lantern", "/Users/dev/projects/aurora", "adaptive", "Wire the telemetry export", 0),
        ("copper-meadow", "C:\\Users\\dev\\work\\ledger", "claude-sonnet-4-5", "Port the ledger", 0),
        ("quiet-harbor", "/Users/dev/projects/harbor", "ADAPTIVE", "Refactor", 0),
        ("still-river", "/Users/dev/projects/river", "swe-1-6", "Refactor", 1),
        ("empty-pier", "/Users/dev/projects/pier", "swe-1-6", "Desktop sketch", 0),
        ("night-owl", "", "swe-1-7", None, 0),
    ]
    build_database(root / cli1, sessions, rows)

    a1_end = at(10, 0, 5) * 1000
    a2_end = at(10, 0, 10) * 1000
    c9_end = at(10, 5, 0) * 1000
    c12_end = at(10, 7, 0) * 1000
    c13_end = at(10, 8, 0) * 1000 + 750
    cli_agents = {
        a1: dict(project="aurora", cwdPath="/Users/dev/projects/aurora", model="swe-1-6", phase="generating",
                 inFlightStartMs=at(10, 2, 0) * 1000, lastLineMs=at(10, 2, 0) * 1000, malformedLines=1, responses=[
                     response("devin:aurora-lantern:request:req-a1", a1, a1_end, 9400, 8000, 200, 340, "swe-1-6",
                              a1_end - 4200, a1_end - 4200 + 1100),
                     response("devin:aurora-lantern:request:req-a2", a1, a2_end, 9400, 9100, 0, 90, "swe-1-6",
                              a2_end - 2500, a2_end - 2500 + 900)]),
        c1: dict(project="ledger", cwdPath="C:\\Users\\dev\\work\\ledger", model="gpt-5-6-sol-max", phase="idle",
                 inFlightStartMs=None, lastLineMs=c13_end, malformedLines=1, responses=[
                     response("devin:copper-meadow:row:9", c1, c9_end, 12, 0, 0, 7, "claude-sonnet-4-5", c9_end - 300),
                     response("devin:copper-meadow:row:12", c1, c12_end, 5, 0, 0, 2, "claude-sonnet-4-5"),
                     response("devin:copper-meadow:request:req-c5", c1, c13_end, 1, 0, 0, 1, "gpt-5-6-sol-max",
                              c13_end - 2000)]),
        q1: dict(project="harbor", cwdPath="/Users/dev/projects/harbor", model=None, phase="idle",
                 inFlightStartMs=None, lastLineMs=at(10, 10, 30) * 1000, malformedLines=1, responses=[
                     response("devin:quiet-harbor:request:req-q1", q1, at(10, 10, 0) * 1000, 50, 0, 0, 20, None)]),
        o1: dict(project="night-owl", cwdPath=None, model="swe-1-7", phase="idle", inFlightStartMs=None,
                 lastLineMs=at(10, 20, 0) * 1000, malformedLines=0, responses=[
                     response("devin:night-owl:request:req-o1", o1, at(10, 20, 0) * 1000, 70, 0, 0, 30, "swe-1-7",
                              at(10, 20, 0) * 1000 - 900)]),
    }

    # --- Database 2: a copied session (skipped) and one of its own ------------------------------------------
    build_database(root / cli2, [
        ("aurora-lantern", "/Users/dev/other", "swe-1-6", "Shadow copy", 0),
        ("north-wind", "/Users/dev/projects/north", "swe-1-7", "Desktop only notes", 0),
    ], [
        ("aurora-lantern", assistant(metrics(999, 999, total=1000), "req-a1", "swe-1-6", "stop"), at(10, 0, 5)),
        ("north-wind", user(), at(10, 40, 0)),
        ("north-wind", assistant(metrics(400, 60, 0, 0, 1500, 300), "req-n1", None, "stop"), at(10, 40, 3)),
    ], wal=True)
    n_end = at(10, 40, 3) * 1000
    second_agents = {
        n2: dict(project="north", cwdPath="/Users/dev/projects/north", model="swe-1-7", phase="idle",
                 inFlightStartMs=None, lastLineMs=n_end, malformedLines=0, responses=[
                     response("devin:north-wind:request:req-n1", n2, n_end, 400, 0, 0, 60, "swe-1-7", n_end - 1500,
                              n_end - 1500 + 300)]),
    }

    # A Devin Desktop folder is not read, also when it is a location of the list.
    never = root / "devin/Devin/User/acp-events/7c1e9a02-aurora.ndjson"
    never.parent.mkdir(parents=True, exist_ok=True)
    never.write_text(desktop_usage_line(), encoding="utf-8")

    scenario_one = dict(locations=["devin/share/devin", "devin/Devin"], databases=[str(cli1)], agents=dict(cli_agents))
    scenario_two = dict(locations=["devin/share/devin", "devin/second"], databases=[str(cli1), str(cli2)],
                        agents=dict(cli_agents, **second_agents))
    for scenario in (scenario_one, scenario_two):
        scenario["history"] = day_totals(scenario["agents"])
    return dict(
        _comment="Devin CLI (METRICS 4.17), checked by spec/tools/devin_reference.py. Paths are relative to the fixtures "
                 "folder. Each scenario reads its `locations` (user locations, list order) completely, as history does; "
                 "agents not listed have no responses. `history` is the scenario's UTC day totals of the listed "
                 "responses.",
        version=2,
        resolve={"devin/share/devin": str(cli1), "devin/share/devin/cli": str(cli1), "devin/second": str(cli2),
                 "devin/Devin": None, "devin/Devin/User/acp-events": None, "devin/missing": None},
        scenarios=dict(one=scenario_one, two=scenario_two))


def day_totals(agents):
    """UTC day totals of the scenario's responses (METRICS 8): measured = final with a valid interval, widened to 500 ms."""
    days = {}
    for agent in agents.values():
        for r in agent["responses"]:
            day = "2026-10-%02d" % (20 + (r["endMs"] // 1000 - DAY0) // 86400)
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
            model = row["models"].setdefault(r["model"] or "", dict(requests=0, output=0))
            model["requests"] += 1
            model["output"] += r["output"]
    for row in days.values():
        row["agents"] = len(row["agents"])
    return days


def main():
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "fixtures"
    expected = write_fixtures(root)
    with open(root / "devin-expected.json", "w", encoding="utf-8") as handle:
        handle.write(json.dumps(expected, indent=1, ensure_ascii=False, sort_keys=False) + "\n")


if __name__ == "__main__":
    main()
