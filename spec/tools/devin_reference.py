#!/usr/bin/env python3
"""Reference reader for Devin (METRICS 4.17): the CLI's sessions.db.

Speed, history days and pricing are left to reference.py. `collect` reads a list of locations completely (the
history rule); `since_ms` gives the startup read of `--dump-today` instead.
"""
import json
import math
import os
import sqlite3
import stat as stat_module
import sys
from collections import OrderedDict
from pathlib import Path

CAP = 10 ** 12
MAX_ROW = 32 * 1024 * 1024
INT64 = (-(2 ** 63), 2 ** 63 - 1)


def token(value):
    """A token value of METRICS 3.2."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return 0
    if isinstance(value, float) and math.isnan(value):
        return 0
    if isinstance(value, float) and math.isinf(value):
        return CAP if value > 0 else 0
    return max(0, min(CAP, int(value)))


def finite_ms(value):
    """A finite number truncated and saturated to Int64 (METRICS 3.6), else None."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if isinstance(value, float) and not math.isfinite(value):
        return None
    return max(INT64[0], min(INT64[1], int(value)))


def loads(text):
    def refuse(name):
        raise ValueError(name)
    try:
        value = json.loads(text, parse_constant=refuse)
    except (ValueError, RecursionError, UnicodeError):
        return None
    return value if isinstance(value, dict) else None


def is_regular(path):
    try:
        return stat_module.S_ISREG(os.lstat(path).st_mode)
    except OSError:
        return False


def resolved(path):
    return os.path.realpath(path) if os.path.exists(path) else os.path.normpath(path)


# --- Roots ------------------------------------------------------------------------------------------------------


def automatic_database(env=None, home=None, platform=None):
    """The automatic entry's CLI database (METRICS 4.17)."""
    env = os.environ if env is None else env
    home = home or os.path.expanduser("~")
    platform = platform or sys.platform
    if env.get("CHISEL_SESSION_DB"):
        return env["CHISEL_SESSION_DB"]
    if platform.startswith("win"):
        appdata = env.get("APPDATA") or os.path.join(home, "AppData", "Roaming")
        return os.path.join(appdata, "devin", "cli", "sessions.db")
    data = env.get("XDG_DATA_HOME") or os.path.join(home, ".local", "share")
    return os.path.join(data, "devin", "cli", "sessions.db")


def resolve_location(folder):
    """The database a user location names (None when it names none)."""
    return next((p for p in (os.path.join(folder, "sessions.db"), os.path.join(folder, "cli", "sessions.db"))
                 if is_regular(p)), None)


def roots_of(locations, env=None, home=None, platform=None):
    """The databases of a location list (`"@auto"` = the automatic entry), each once, in list order."""
    databases = []
    for location in locations:
        path = automatic_database(env, home, platform) if location == "@auto" else resolve_location(location)
        if path and is_regular(path) and resolved(path) not in map(resolved, databases):
            databases.append(path)
    return databases


# --- CLI database ------------------------------------------------------------------------------------------------


def open_database(path):
    with open(path, "rb") as handle:
        header = handle.read(20)
    wal = len(header) >= 20 and header[18] == 2 and header[19] == 2
    mode = "immutable=1" if wal and not os.path.exists(path + "-wal") else "mode=ro"
    return sqlite3.connect("file:%s?%s" % (path, mode), uri=True)


def columns(db, table):
    return {row[1] for row in db.execute("SELECT * FROM pragma_table_info(?)", (table,))}


def session_model(raw):
    if not isinstance(raw, str) or not raw.strip() or raw.strip().lower() == "adaptive":
        return None
    return raw.strip()


def text_value(raw):
    return raw.strip() if isinstance(raw, str) and raw.strip() else None


class Agent:
    """One agent with the fields reference.py's performance rows read (`path`, `source`, `tools`, `completions`)."""
    source = "devin"
    tools = None

    def __init__(self, agent_id, project=None, cwd=None):
        self.id = agent_id
        self.path = agent_id
        self.project = project
        self.cwd = cwd
        self.model = None
        self.phase = "idle"
        self.phase_since = None
        self.in_flight = None
        self.last_line = None
        self.malformed = 0
        self.responses = OrderedDict()
        self.fallback_model = None

    @property
    def completions(self):
        return [("response", key) for key in self.responses]

    def note(self, ms):
        if ms is not None and (self.last_line is None or ms > self.last_line):
            self.last_line = ms

    def set_phase(self, phase, ms):
        if self.phase_since is not None and ms < self.phase_since:
            return
        self.phase, self.phase_since = phase, ms
        self.in_flight = ms if phase == "generating" else None

    def view(self):
        model = self.model if self.model is not None else self.fallback_model
        return dict(project=self.project, cwdPath=self.cwd, model=model, phase=self.phase,
                    inFlightStartMs=self.in_flight, lastLineMs=self.last_line, malformedLines=self.malformed,
                    responses=list(self.responses.values()))


def row_time(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    return finite_ms(value * 1000) if isinstance(value, float) else finite_ms(value * 1000)


def cli_call(session, row_id, message, end):
    metadata = message.get("metadata")
    metadata = metadata if isinstance(metadata, dict) else {}
    m = metadata.get("metrics")
    if message.get("role") != "assistant" or not isinstance(m, dict):
        return None
    read, write = token(m.get("cache_read_tokens")), token(m.get("cache_creation_tokens"))
    inp = min(CAP, token(m.get("input_tokens")) + read + write)
    out = token(m.get("output_tokens"))
    if inp == 0 and out == 0:
        return None
    request = text_value(metadata.get("request_id"))
    key = "devin:%s:request:%s" % (session, request) if request else "devin:%s:row:%d" % (session, row_id)
    total = finite_ms(m.get("total_time_ms"))
    start = max(INT64[0], end - total) if total is not None and total > 0 else None
    ttft = finite_ms(m.get("ttft_ms"))
    first = start + ttft if start is not None and ttft is not None and 0 < ttft <= total else None
    return key, dict(key=key, startMs=start, endMs=end, firstMs=first, input=inp, cachedInput=read,
                     cacheWriteInput=write, cacheWrite1hInput=0, output=out, reasoning=0,
                     model=text_value(metadata.get("generation_model")), provider=None, final=True, fast=False)


def cli_phase(message):
    role = message.get("role")
    if role in ("user", "tool"):
        return "generating"
    if role == "assistant":
        metadata = message.get("metadata")
        finish = metadata.get("finish_reason") if isinstance(metadata, dict) else None
        tools = message.get("tool_calls")
        return "tools" if (isinstance(tools, list) and tools) or finish == "tool_calls" else "idle"
    return None


def read_database(path, claimed, owners, since_ms=None, label=lambda p: p):
    """Agents of one database. `claimed`: session ids earlier databases read; `owners`: key → owner."""
    try:
        db = open_database(path)
        db.execute("BEGIN")
        if not {"id", "working_directory", "model"} <= columns(db, "sessions") or \
                not {"row_id", "session_id", "chat_message", "created_at"} <= columns(db, "message_nodes"):
            return None
        sessions = OrderedDict()
        for sid, cwd, model in db.execute("SELECT id, working_directory, model FROM sessions"):
            if isinstance(sid, str) and sid not in claimed:
                sessions[sid] = (cwd if isinstance(cwd, str) and cwd else None, session_model(model))
        query = ("SELECT row_id, session_id, created_at, CASE WHEN length(CAST(chat_message AS BLOB)) <= %d "
                 "THEN chat_message END, typeof(chat_message) FROM message_nodes ORDER BY row_id" % MAX_ROW)
        rows = list(db.execute(query))
        db.rollback()
        db.close()
    except sqlite3.Error:
        return None
    if since_ms is not None:
        kept = []
        for row in reversed(rows):
            ms = row_time(row[2])
            if ms is not None and ms < since_ms:
                break
            kept.append(row)
        rows = list(reversed(kept))
    agents = OrderedDict()
    owner = label(path)
    for row_id, sid, created, text, kind in rows:
        if sid not in sessions:
            continue
        cwd, model = sessions[sid]
        agent_id = label(path) + "#" + sid
        agent = agents.get(agent_id)
        if agent is None:
            project = cwd.replace("\\", "/").rstrip("/").rsplit("/", 1)[-1] if cwd else None
            agent = agents[agent_id] = Agent(agent_id, project or sid, cwd)
            agent.fallback_model = model
        ms = row_time(created)
        message = loads(text) if isinstance(text, str) and kind == "text" else None
        if ms is None or message is None:
            agent.malformed += 1
            agent.note(ms)
            continue
        agent.note(ms)
        call = cli_call(sid, row_id, message, ms)
        if call:
            key, record = call
            record["model"] = record["model"] or model
            if owners.setdefault(key, owner) == owner:
                agent.responses[key] = dict(record, agentId=agent_id)
                agent.model = record["model"]
        phase = cli_phase(message)
        if phase:
            agent.set_phase(phase, ms)
    return dict(agents=agents, sessions=list(sessions))


# --- The source ----------------------------------------------------------------------------------------------------


def collect(locations, since_ms=None, label=lambda p: p, env=None, home=None, platform=None):
    """Every agent of a location list (METRICS 4.17), by agent id; a session an earlier database has is skipped."""
    owners, claimed, agents = {}, set(), OrderedDict()
    for path in roots_of(locations, env, home, platform):
        result = read_database(path, claimed, owners, since_ms, label)
        if result is None:
            continue
        claimed.update(result["sessions"])
        agents.update(result["agents"])
    return agents


def records(locations, since_ms=None):
    """(source, agent id, response) of every Devin response, for reference.py's history."""
    return [("devin", agent.id, dict(r)) for agent in collect(locations, since_ms).values() for r in agent.responses.values()]


# --- Fixtures ------------------------------------------------------------------------------------------------------


def check_fixtures(root):
    import shutil
    import tempfile
    root = Path(root)
    expected = json.loads((root / "devin-expected.json").read_text(encoding="utf-8"))
    failures = []
    with tempfile.TemporaryDirectory() as tmp:
        copy = Path(tmp) / "fixtures"
        shutil.copytree(root / "devin", copy / "devin")
        label = lambda p: os.path.relpath(p, copy)  # noqa: E731
        for location, want in expected["resolve"].items():
            database = resolve_location(str(copy / location))
            if (label(database) if database else None) != want:
                failures.append("devin resolve %s: expected %r got %r" % (location, want, database))
        for name, scenario in expected["scenarios"].items():
            locations = [str(copy / l) for l in scenario["locations"]]
            if [label(p) for p in roots_of(locations)] != scenario["databases"]:
                failures.append("devin %s databases: got %r" % (name, roots_of(locations)))
            agents = collect(locations, label=label)
            got = {aid: agent.view() for aid, agent in agents.items() if agent.responses}
            if set(got) != set(scenario["agents"]):
                failures.append("devin %s agents: expected %r got %r" % (name, sorted(scenario["agents"]), sorted(got)))
            for aid, want in scenario["agents"].items():
                for field, value in want.items():
                    if got.get(aid, {}).get(field) != value:
                        failures.append("devin %s %s %s: expected %r got %r" % (name, aid, field, value,
                                                                                  got.get(aid, {}).get(field)))
        created = sorted(str(p.relative_to(copy)) for p in copy.rglob("*")
                         if p.name.endswith(("-wal", "-shm", "-journal")))
        if created:
            failures.append("devin: reading created %r" % created)
    return failures


if __name__ == "__main__":
    problems = check_fixtures(Path(__file__).resolve().parent.parent / "fixtures")
    print("\n".join(problems) if problems else "devin reference matches devin-expected.json")
    sys.exit(1 if problems else 0)
