#!/usr/bin/env python3
"""Reference reader for Qoder IDE (METRICS 4.18): the agent core's local.db, and the roots of Qoder CLI (4.19).

Speed, history days and pricing are left to reference.py, which also parses Qoder CLI transcripts (Claude's parser
with the differences of 4.19). `collect` reads a list of locations completely (the history rule); `since_ms` gives
the startup read of `--dump-today` instead.
"""
import json
import ntpath
import os
import posixpath
import re
import sqlite3
import sys
from collections import OrderedDict
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import unquote
from urllib.request import pathname2url

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from devin_reference import columns, finite_ms, is_regular, loads, resolved, token  # noqa: E402

# The China edition's IDE data folder name (METRICS 2 and 4.18 `@@CNFOLDER@@`). Not confirmed yet: update it here,
# then regenerate the fixtures (`python3 spec/tools/qoder_fixtures.py`), which take it from this module.
CN_FOLDER = "QoderCN"
EDITIONS = ("Qoder", CN_FOLDER)
DATABASE = ("SharedClientCache", "cache", "db", "local.db")
CLI_FOLDERS = (".qoder", ".qoder-cn")
MAX_JSON = 1024 * 1024
TURN_INFO = "ai-coding/turn-info"
REQUIRED_MESSAGE = {"id", "session_id", "role", "token_info", "model_info", "gmt_create"}
REQUIRED_SESSION = {"session_id", "project_uri"}


def path_module(platform=None):
    platform = platform or sys.platform
    return ntpath if platform.startswith("win") else posixpath


def is_windows(platform):
    return platform.startswith("win")


def is_macos(platform):
    return platform in ("darwin", "macos")


# --- Roots ------------------------------------------------------------------------------------------------------


def data_folder(env=None, home=None, platform=None):
    """`<data>` of METRICS 4.18: where VS Code forks keep their user data folders."""
    env = os.environ if env is None else env
    home = home or os.path.expanduser("~")
    platform = platform or sys.platform
    paths = path_module(platform)
    if is_windows(platform):
        return env.get("APPDATA") or paths.join(home, "AppData", "Roaming")
    if is_macos(platform):
        return paths.join(home, "Library", "Application Support")
    return env.get("XDG_CONFIG_HOME") or paths.join(home, ".config")


def automatic_databases(env=None, home=None, platform=None):
    """The automatic entry's databases, international edition first; only those that exist are read."""
    platform = platform or sys.platform
    data = data_folder(env, home, platform)
    return [path_module(platform).join(data, edition, *DATABASE) for edition in EDITIONS]


def resolve_location(folder):
    """The database a user location names: `local.db`, `cache/db/local.db` or the full path below a data folder."""
    candidates = (os.path.join(folder, DATABASE[-1]), os.path.join(folder, *DATABASE[1:]),
                  os.path.join(folder, *DATABASE))
    return next((p for p in candidates if is_regular(p)), None)


def roots_of(locations, env=None, home=None, platform=None):
    """The databases of a location list (`"@auto"` = the automatic entry), each once, in list order."""
    databases = []
    for location in locations:
        found = automatic_databases(env, home, platform) if location == "@auto" else [resolve_location(location)]
        for path in found:
            if path and is_regular(path) and resolved(path) not in map(resolved, databases):
                databases.append(path)
    return databases


def cli_roots(env=None, home=None, platform=None):
    """Qoder CLI's automatic roots (METRICS 4.19): each edition's config folder (`$QODER_CONFIG_DIR`, else `~/.qoder`;
    `$QODERCN_CONFIG_DIR`, else `~/.qoder-cn`) with `projects` underneath."""
    env = os.environ if env is None else env
    home = home or os.path.expanduser("~")
    paths = path_module(platform)
    roots = []
    for variable, folder in zip(("QODER_CONFIG_DIR", "QODERCN_CONFIG_DIR"), CLI_FOLDERS):
        base = env.get(variable) or paths.join(home, folder)
        roots.append(paths.join(base, "projects"))
    return roots


def cli_location(folder, platform=None):
    """A Qoder CLI user location (a config folder or its `projects` folder) read as that `projects` folder."""
    folder = folder.rstrip("/\\") or folder
    name = re.split(r"[\\/]", folder)[-1]
    return folder if name == "projects" else path_module(platform).join(folder, "projects")


# --- Database ------------------------------------------------------------------------------------------------------


def open_database(path):
    """Read-only with the safeguards of METRICS 4.5: `immutable=1` for a WAL database without its `-wal` file."""
    with open(path, "rb") as handle:
        header = handle.read(20)
    wal = len(header) >= 20 and header[18] == 2 and header[19] == 2
    mode = "immutable=1" if wal and not os.path.exists(path + "-wal") else "mode=ro"
    return sqlite3.connect("file:%s?%s" % (pathname2url(os.path.abspath(path)), mode), uri=True)


def bounded(column):
    """The column when it is text of at most 1 MiB, else NULL."""
    return "CASE WHEN typeof({0}) = 'text' AND length(CAST({0} AS BLOB)) <= {1} THEN {0} END".format(column, MAX_JSON)


def row_time(value):
    """`gmt_create`: Unix ms, an integer or a finite real, truncated (METRICS 3.6)."""
    return finite_ms(value)


def text_value(raw):
    return raw.strip() if isinstance(raw, str) and raw.strip() else None


def model_key(text):
    info = loads(text) if isinstance(text, str) else None
    return text_value(info.get("model_key")) if info is not None else None


def project_path(uri):
    """The working folder of `chat_session.project_uri`: a path as it is, or a `file://` URI's percent-decoded path."""
    if not isinstance(uri, str) or not uri.strip():
        return None
    if uri[:7].lower() != "file://":
        return uri
    rest = uri[7:]
    if not rest.startswith("/"):
        slash = rest.find("/")
        rest = rest[slash:] if slash >= 0 else ""
    decoded = unquote(rest)
    if re.match(r"^/[A-Za-z]:", decoded):
        decoded = decoded[1:]
    return decoded or None


def last_component(path):
    parts = [p for p in re.split(r"[\\/]", path or "") if p]
    return parts[-1] if parts else None


class Agent:
    """One chat session, with the fields reference.py's performance rows read (`path`, `source`, `tools`,
    `completions`)."""
    source = "qoder"
    tools = None

    def __init__(self, agent_id, project, cwd, parent_session):
        self.id = agent_id
        self.path = agent_id
        self.project = project
        self.cwd = cwd
        self.parent_session = parent_session
        self.parent_id = None
        self.model = None
        self.phase = "idle"
        self.phase_since = None
        self.in_flight = None
        self.last_line = None
        self.malformed = 0
        self.rows = 0
        self.highest_rowid = None
        self.responses = OrderedDict()

    @property
    def is_subagent(self):
        return self.parent_session is not None

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
        return dict(project=self.project, cwdPath=self.cwd, model=self.model, isSubagent=self.is_subagent,
                    parentId=self.parent_id, phase=self.phase, inFlightStartMs=self.in_flight,
                    lastLineMs=self.last_line, malformedLines=self.malformed, responses=list(self.responses.values()))


def call(session, rowid, message_id, info, start, end, model):
    """The response of an assistant row whose `token_info` is an object; None when its input and output are both 0."""
    inp = token(info.get("prompt_tokens"))
    out = token(info.get("completion_tokens"))
    if inp == 0 and out == 0:
        return None
    mid = text_value(message_id)
    key = "qoder:" + mid if mid else "qoder:%s:row:%d" % (session, rowid)
    return dict(key=key, startMs=start, endMs=end, firstMs=None, input=inp,
                cachedInput=min(token(info.get("cached_tokens")), inp), cacheWriteInput=0, cacheWrite1hInput=0,
                output=out, reasoning=0, model=model, provider=None, final=True, fast=False)


def finished_requests(db):
    """request id → the latest `finishedAt` of its `chat_record` rows (METRICS 4.18 phase), when the table exists."""
    finished = {}
    if not {"request_id", "extra"} <= columns(db, "chat_record"):
        return finished
    for request, extra in db.execute("SELECT request_id, %s FROM chat_record" % bounded("extra")):
        info = loads(extra) if isinstance(extra, str) else None
        turn = info.get(TURN_INFO) if info is not None else None
        at = finite_ms(turn.get("finishedAt")) if isinstance(turn, dict) else None
        if isinstance(request, str) and at is not None:
            finished[request] = max(finished.get(request, at), at)
    return finished


def startup_window(rows, since_ms):
    """The rows of the startup window, and per session the time of its row just below it (METRICS 4.18 Reading)."""
    kept = []
    for row in reversed(rows):
        ms = row_time(row[4])
        if ms is not None and ms < since_ms:
            break
        kept.append(row)
    kept.reverse()
    if not kept:
        return [], {}
    lowest = kept[0][0]
    below = {}
    for row in rows:
        if row[0] >= lowest:
            break
        below[row[2]] = row_time(row[4])
    return kept, {sid: below[sid] for sid in {row[2] for row in kept} if sid in below}


def read_database(path, claimed, owners, since_ms=None, label=lambda p: p):
    """Agents of one database, or None when it is unsupported. `claimed`: session ids earlier databases have;
    `owners`: key → the database that owns it."""
    try:
        db = open_database(path)
        try:
            db.execute("BEGIN")
            message, session = columns(db, "chat_message"), columns(db, "chat_session")
            if not REQUIRED_MESSAGE <= message or not REQUIRED_SESSION <= session:
                return None
            parent = "parent_session_id" if "parent_session_id" in session else "NULL"
            listed = list(db.execute("SELECT session_id, project_uri, %s FROM chat_session" % parent))
            request = "request_id" if "request_id" in message else "NULL"
            rows = list(db.execute("SELECT rowid, id, session_id, role, gmt_create, %s, %s, %s FROM chat_message "
                                   "ORDER BY rowid" % (bounded("token_info"), bounded("model_info"), request)))
            finished = finished_requests(db)
            db.rollback()
        finally:
            db.close()
    except sqlite3.Error:
        return None
    sessions = OrderedDict()
    for sid, uri, parent_sid in listed:
        if isinstance(sid, str) and sid not in claimed and sid not in sessions:
            sessions[sid] = (project_path(uri), parent_sid if text_value(parent_sid) else None)
    previous = {}
    if since_ms is not None:
        rows, previous = startup_window(rows, since_ms)
    owner = label(path)
    agents = OrderedDict()
    for rowid, message_id, sid, role, created, token_text, model_text, request_id in rows:
        if sid not in sessions:
            continue
        agent_id = owner + "#" + sid
        agent = agents.get(agent_id)
        if agent is None:
            cwd, parent_sid = sessions[sid]
            agent = agents[agent_id] = Agent(agent_id, last_component(cwd) or sid, cwd, parent_sid)
        agent.rows += 1
        agent.highest_rowid = rowid
        ms = row_time(created)
        start = previous.get(sid)
        previous[sid] = ms
        agent.note(ms)
        if ms is None or not isinstance(role, str):
            agent.malformed += 1
            continue
        if role == "assistant":
            info = loads(token_text) if isinstance(token_text, str) else None
            record = call(sid, rowid, message_id, info, start, ms, model_key(model_text)) if info is not None else None
            if record is not None and owners.setdefault(record["key"], owner) == owner:
                agent.responses[record["key"]] = dict(record, agentId=agent_id)
                agent.model = record["model"] or agent.model
            done = finished.get(request_id) if isinstance(request_id, str) else None
            agent.set_phase("idle" if done is not None and done >= ms else "tools", ms)
        elif role in ("user", "tool"):
            agent.set_phase("generating", ms)
    for agent in agents.values():
        candidate = owner + "#" + agent.parent_session if agent.parent_session is not None else None
        agent.parent_id = candidate if candidate in agents else None
    for agent in agents.values():
        seen, cur = set(), agent.id
        while cur is not None and cur not in seen:
            seen.add(cur)
            cur = agents[cur].parent_id
        if cur is not None:
            agent.parent_id = None  # METRICS 6.7: self references and cycles resolve to null
    return dict(agents=agents, sessions=[sid for sid, _, _ in listed if isinstance(sid, str)])


# --- The source ----------------------------------------------------------------------------------------------------


def collect(locations, since_ms=None, label=lambda p: p, env=None, home=None, platform=None, unsupported=None):
    """Every agent of a location list (METRICS 4.18), by agent id; a session an earlier database has is skipped.
    `unsupported` receives the databases without the required tables."""
    owners, claimed, agents = {}, set(), OrderedDict()
    for path in roots_of(locations, env, home, platform):
        result = read_database(path, claimed, owners, since_ms, label)
        if result is None:
            if unsupported is not None:
                unsupported.append(path)
            continue
        claimed.update(result["sessions"])
        agents.update(result["agents"])
    return agents


def records(locations, since_ms=None):
    """(source, agent id, response) of every Qoder IDE response, for reference.py's history."""
    return [("qoder", agent.id, dict(r)) for agent in collect(locations, since_ms).values()
            for r in agent.responses.values()]


def contributions(agents):
    """The history's per-session identity (METRICS 4.18 History): row count and highest rowid, by agent id."""
    return {aid: dict(rows=agent.rows, highestRowid=agent.highest_rowid) for aid, agent in agents.items()}


def day_totals(responses):
    """UTC day totals of responses (METRICS 8): measured = final with a valid interval, widened to 500 ms."""
    days = {}
    for r in responses:
        day = datetime.fromtimestamp(r["endMs"] / 1000, tz=timezone.utc).strftime("%Y-%m-%d")
        row = days.setdefault(day, dict(requests=0, input=0, cachedInput=0, cacheWriteInput=0, output=0,
                                        reasoning=0, measuredOutput=0, measuredMs=0, agents=set(), models={}))
        row["requests"] += 1
        for field in ("input", "cachedInput", "cacheWriteInput", "output", "reasoning"):
            row[field] += r[field]
        start, end = r["startMs"], r["endMs"]
        if start is not None and 0 <= end - start <= 15 * 60_000:
            row["measuredOutput"] += r["output"]
            row["measuredMs"] += max(500, end - start)
        row["agents"].add(r["agentId"])
        model = row["models"].setdefault(r["model"] or "", dict(requests=0, output=0))
        model["requests"] += 1
        model["output"] += r["output"]
    for row in days.values():
        row["agents"] = len(row["agents"])
    return days


# --- Fixtures ------------------------------------------------------------------------------------------------------


def check_fixtures(root):
    import shutil
    import tempfile
    root = Path(root)
    expected = json.loads((root / "qoder-expected.json").read_text(encoding="utf-8"))
    failures = []
    for case in expected["automatic"]:
        got = automatic_databases(case["env"], case["home"], case["platform"])
        if got != case["databases"]:
            failures.append("qoder automatic %r: expected %r got %r" % (case, case["databases"], got))
    cli = expected["cli"]
    for case in cli["automatic"]:
        got = cli_roots(case["env"], case["home"], case["platform"])
        if got != case["roots"]:
            failures.append("qoderCli roots %r: expected %r got %r" % (case, case["roots"], got))
    for location, want in cli["locations"].items():
        if cli_location(location, "linux") != want:
            failures.append("qoderCli location %s: expected %r got %r" % (location, want, cli_location(location)))
    with tempfile.TemporaryDirectory() as tmp:
        copy = Path(tmp) / "fixtures"
        shutil.copytree(root / "qoder", copy / "qoder")
        label = lambda p: os.path.relpath(p, copy)  # noqa: E731
        for location, want in expected["resolve"].items():
            database = resolve_location(str(copy / location))
            if (label(database) if database else None) != want:
                failures.append("qoder resolve %s: expected %r got %r" % (location, want, database))
        for name, scenario in expected["scenarios"].items():
            env = {"APPDATA": "", "XDG_CONFIG_HOME": ""}
            home = str(copy / scenario["home"]) if scenario.get("home") else None
            locations = [l if l == "@auto" else str(copy / l) for l in scenario["locations"]]
            databases = [label(p) for p in roots_of(locations, env, home, "darwin")]
            if databases != scenario["databases"]:
                failures.append("qoder %s databases: expected %r got %r" % (name, scenario["databases"], databases))
            unsupported = []
            since = scenario["todayStartMs"] - 15 * 60_000 if "todayStartMs" in scenario else None
            agents = collect(locations, since, label, env, home, "darwin", unsupported)
            if [label(p) for p in unsupported] != scenario.get("unsupported", []):
                failures.append("qoder %s unsupported: got %r" % (name, [label(p) for p in unsupported]))
            got = {aid: agent.view() for aid, agent in agents.items()}
            if set(got) != set(scenario["agents"]):
                failures.append("qoder %s agents: expected %r got %r" % (name, sorted(scenario["agents"]), sorted(got)))
            for aid, want in scenario["agents"].items():
                for field, value in want.items():
                    if got.get(aid, {}).get(field) != value:
                        failures.append("qoder %s %s %s: expected %r got %r" % (name, aid, field, value,
                                                                                  got.get(aid, {}).get(field)))
            if "sessions" in scenario and contributions(agents) != scenario["sessions"]:
                failures.append("qoder %s sessions: expected %r got %r" % (name, scenario["sessions"],
                                                                           contributions(agents)))
            if "history" in scenario:
                days = day_totals([r for agent in agents.values() for r in agent.responses.values()])
                if days != scenario["history"]:
                    failures.append("qoder %s history: expected %r got %r" % (name, scenario["history"], days))
        created = sorted(str(p.relative_to(copy)) for p in copy.rglob("*")
                         if p.name.endswith(("-wal", "-shm", "-journal")))
        if created:
            failures.append("qoder: reading created %r" % created)
    return failures


def check_cli_history(root, rows):
    """The `qoderCli` rows of reference.py's fixture history against qoder-expected.json `cli.history` (by hand)."""
    expected = json.loads((Path(root) / "qoder-expected.json").read_text(encoding="utf-8"))["cli"]["history"]
    got = {}
    for row in rows:
        if row["source"] != "qoderCli":
            continue
        got[row["date"]] = {k: row[k] for k in ("requests", "input", "cachedInput", "cacheWriteInput", "output",
                                                "reasoning", "measuredOutput", "measuredMs", "agents")}
        got[row["date"]]["models"] = {m: {k: v[k] for k in ("requests", "output", "measuredOutput", "measuredMs")}
                                      for m, v in row["models"].items()}
    return [] if got == expected else ["qoderCli history: expected %r got %r" % (expected, got)]


if __name__ == "__main__":
    problems = check_fixtures(Path(__file__).resolve().parent.parent / "fixtures")
    print("\n".join(problems) if problems else "qoder reference matches qoder-expected.json")
    sys.exit(1 if problems else 0)
