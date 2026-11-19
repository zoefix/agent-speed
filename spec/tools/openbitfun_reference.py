#!/usr/bin/env python3
"""Read-only reference for OpenBitFun usage records, model rounds and diagnostic request traces (METRICS 4.21)."""
import json
import os
import sys
from functools import lru_cache
from pathlib import Path

import harness_reference as h

MAX_DOCUMENT = 64 * 1024 * 1024


def projects(path):
    path = Path(path)
    return path if path.name == "projects" else path / "projects"


def traces(metadata):
    session = Path(metadata).parent
    shared = session.parent.parent / "request-traces"
    folders = [session / "request-traces"]
    if shared.is_dir() and not shared.is_symlink():
        folders.append(shared / session.name)
    return sorted(str(path) for folder in folders if folder.is_dir() and not folder.is_symlink()
                  for path in folder.glob("request-*.json") if path.is_file() and not path.is_symlink())


def modified(metadata):
    usage, usage_time = session_ledger(Path(metadata).parent.name)
    paths = [metadata, *turn_files(metadata)]
    if not usage:
        paths.extend(traces(metadata))
    return max(usage_time, max(os.path.getmtime(path) for path in paths))


def discover(root, limit=20_000):
    found = []
    root = Path(root)
    if not root.is_dir() or root.is_symlink():
        return found
    for project in root.iterdir():
        sessions = project / "sessions"
        if project.name.startswith(".") or project.is_symlink() or sessions.is_symlink() or not sessions.is_dir():
            continue
        for session in sessions.iterdir():
            metadata = session / "metadata.json"
            if not session.name.startswith(".") and not session.is_symlink() and metadata.is_file() and not metadata.is_symlink():
                found.append(str(metadata))
    return sorted(found, key=lambda path: (-modified(path), path))[:limit]


@lru_cache(maxsize=1)
def ledger_index(days):
    """Share the daily ledger snapshot across all sessions in this reference run."""
    sessions = {}
    for path, _, _ in days:
        document = h.json_file(path, MAX_DOCUMENT) or {}
        for row in document.get("records") or []:
            if not isinstance(row, dict):
                continue
            details = h.object_value(row.get("token_details"))
            context = h.object_value(details.get("openbitfunModelRequest"))
            session, turn = h.text(row.get("session_id")), h.text(row.get("turn_id"))
            model_round, attempt = h.text(context.get("roundId")), h.text(context.get("attemptId"))
            if context.get("version") != 1 or not (session and turn and model_round and attempt):
                continue
            output = h.count(row.get("output_tokens"))
            tokens = dict(input=h.count(row.get("input_tokens")), output=output,
                          cachedInput=h.count(row.get("cached_tokens")), cacheWriteInput=h.count(row.get("cache_write_tokens")),
                          reasoning=min(output, h.count(details.get("reasoningTokenCount"))))
            model = (h.text(row.get("effective_model_name")) or h.text(context.get("responseModel"))
                     or h.text(context.get("requestedModel")))
            provider = h.text(h.object_value(context.get("channel")).get("protocol"))
            rounds, newest = sessions.get(session, ({}, 0))
            rounds.setdefault((turn, model_round), {})[attempt] = (tokens if context.get("usageKnown") is True else None, model, provider)
            sessions[session] = rounds, max(newest, (timestamp(row.get("timestamp")) or 0) / 1000)
    return sessions


def session_ledger(session, folder=None):
    import reference as r
    if folder is None:
        if os.name == "nt":
            if not os.environ.get("APPDATA"):
                return {}, 0
            config = Path(os.environ["APPDATA"])
        elif sys.platform == "darwin":
            config = Path.home() / "Library/Application Support"
        else:
            config = Path(os.environ.get("XDG_CONFIG_HOME", ""))
            if not config.is_absolute():
                config = Path.home() / ".config"
        folder = config / "openbitfun/data/token_usage/records"
    days = []
    if not folder.is_symlink():
        for path in sorted(folder.glob("????-??-??.json")):
            if path.is_symlink() or not path.is_file() or r.parse_ts(path.stem + "T00:00:00Z") is None:
                continue
            stat = path.stat()
            days.append((path, stat.st_mtime_ns, stat.st_size))
    return ledger_index(tuple(days)).get(session, ({}, 0))


def turn_files(metadata):
    folder = Path(metadata).parent / "turns"
    if folder.is_symlink():
        return []
    return sorted(path for path in folder.glob("turn-*.json") if path.is_file() and not path.is_symlink())


def timestamp(value):
    import reference as r
    return r.parse_ts(value) if isinstance(value, str) else h.millis(value)


def read_rounds(state, deduper, usage, trace_starts):
    import reference as r
    pending, turns = [], {}
    for path in turn_files(state.path):
        turn = h.json_file(path, MAX_DOCUMENT) or {}
        if turn.get("sessionId") != state.thread_id or not h.text(turn.get("turnId")):
            continue
        turn_start, turn_end = timestamp(turn.get("startTime")), timestamp(turn.get("endTime"))
        round_starts, round_ends = [], []
        closed_turn = turn.get("status") in ("completed", "cancelled", "error")
        for row in turn.get("modelRounds") or []:
            if not isinstance(row, dict) or not h.text(row.get("id")):
                continue
            start, end = timestamp(row.get("startTime")), timestamp(row.get("endTime"))
            if start is not None:
                round_starts.append(start)
            if end is not None or start is not None:
                round_ends.append(end if end is not None else start)
            closed = closed_turn or row.get("status") in ("completed", "failed", "cancelled", "interrupted")
            model = h.text(row.get("effectiveModelName")) or h.text(row.get("modelConfigId"))
            if start is not None:
                time = end if end is not None else start
                if state.last_line is None or time >= state.last_line:
                    state.last_line, state.model = time, model
                if end is None and not closed:
                    pending.append(start)
            attempts = list(usage.get((turn["turnId"], row["id"]), {}).values())
            if end is None or not attempts or len(attempts) < h.count(row.get("attemptCount")):
                continue
            key = "openbitfun:round:%s:%s:%s" % (state.thread_id, turn["turnId"], row["id"])
            if deduper.owner(key) not in (None, state.path):
                continue
            deduper.claim(key, state.path)
            duration = h.millis(row.get("durationMs"))
            if duration is None or duration < 0:
                duration = end - start if start is not None else 0
            known = [tokens for tokens, _, _ in attempts if tokens is not None]
            measured = len(known) == len(attempts) and 0 < duration <= 900_000
            tokens = {field: r.cap(sum(entry.get(field, 0) for entry in known)) for field in h.FIELDS}
            r.emit(state, key, end - duration if measured else None, end, tokens,
                   attempts[-1][1] or model, provider=attempts[-1][2])
            state.responses[key]["requestCount"] = len(attempts)
        turns[turn["turnId"]] = (turn_start if turn_start is not None else min(round_starts, default=None),
                                 turn_end if turn_end is not None else max(round_ends, default=None), closed_turn)
    for owner, sent in trace_starts:
        if owner is not None:
            ended = owner in turns and turns[owner][2]
        else:
            matches = [closed for start, end, closed in turns.values()
                       if start is not None and start <= sent and (not closed or end is not None and sent <= end)]
            ended = matches == [True]
        if not ended:
            pending.append(sent)
    state.in_flight = min(pending) if pending else None
    state.phase = "generating" if pending else "idle"


def read(state, deduper, ledger_root=None):
    import reference as r
    metadata = h.json_file(state.path, MAX_DOCUMENT) or {}
    state.thread_id = metadata.get("sessionId", metadata.get("session_id"))
    relationship = metadata.get("relationship") or {}
    state.parent_thread_id = relationship.get("parentSessionId", relationship.get("parent_session_id"))
    state.file_created = metadata.get("createdAt", metadata.get("created_at", 0))
    state.project = r.basename(metadata.get("workspacePath", metadata.get("workspace_path")))
    state.is_subagent = metadata.get("sessionKind", metadata.get("session_kind")) == "subagent" or relationship.get("kind") == "subagent"
    usage, _ = session_ledger(state.thread_id, ledger_root)
    if usage:
        return read_rounds(state, deduper, usage, [])
    pending, completed = {}, set()
    for path in traces(state.path):
        obj = h.json_file(path, MAX_DOCUMENT) or {}
        if obj.get("version") != 1 or obj.get("session_id") != state.thread_id or not obj.get("trace_id"):
            continue
        timing, request = obj.get("timing") or {}, obj.get("request") or {}
        start, end = r.parse_ts(timing.get("started_at")), r.parse_ts(timing.get("finished_at"))
        response = obj.get("response")
        if start is not None:
            time = end if end is not None else start
            if state.last_line is None or time >= state.last_line:
                state.model = request.get("model_id")
            state.last_line = max(time, state.last_line or time)
        if isinstance(response, dict):
            completed.add(obj["trace_id"])
            pending.pop(obj["trace_id"], None)
        elif start is not None and obj["trace_id"] not in completed:
            pending[obj["trace_id"]] = h.text(obj.get("turn_id")), start
        usage = response.get("usage") if isinstance(response, dict) else None
        if end is None or not isinstance(usage, dict):
            continue
        duration = timing.get("duration_ms")
        if r.finite(duration) and duration >= 0:
            start = end - int(duration) if duration <= 900_000 else None
        key = "openbitfun:" + obj["trace_id"]
        if deduper.owner(key) not in (None, state.path):
            continue
        deduper.claim(key, state.path)
        output = r.num(usage.get("candidatesTokenCount"))
        r.emit(state, key, start, end, dict(input=r.num(usage.get("promptTokenCount")), output=output,
            cachedInput=r.num(usage.get("cachedContentTokenCount")), cacheWriteInput=r.num(usage.get("cacheCreationTokenCount")),
            reasoning=min(output, r.num(usage.get("reasoningTokenCount")))), request.get("model_id"), provider=request.get("provider"))
    read_rounds(state, deduper, {}, list(pending.values()))


def check_fixtures(root):
    import reference as r
    root = Path(root)
    expected = json.loads((root / "openbitfun-expected.json").read_text())
    deduper, failures, responses = r.Deduper(), [], []
    for file in expected["files"]:
        state = r.FileState(str(root / file["path"]), "openbitfun")
        read(state, deduper, root / expected["ledger"]["root"])
        actual = [dict(value, requestCount=1) for value in state.responses.values()]
        for key, value in dict(responses=actual, project=state.project, parent=state.parent_thread_id,
                               isSubagent=state.is_subagent, phase=state.phase).items():
            if value != file[key]:
                failures.append("openbitfun %s %s: expected %r got %r" % (file["path"], key, file[key], value))
        responses += actual
    totals = expected["totals"]
    got = r.today_stats(responses, expected["todayStartMs"], expected["nowMs"])
    for key in ("requests", "input", "output", "average"):
        if got[key] != totals[key]:
            failures.append("openbitfun %s: expected %r got %r" % (key, totals[key], got[key]))
    ledger = expected["ledger"]
    state = r.FileState(str(root / ledger["metadata"]), "openbitfun")
    read(state, r.Deduper(), root / ledger["root"])
    for key, value in dict(responses=list(state.responses.values()), phase=state.phase).items():
        if value != ledger[key]:
            failures.append("openbitfun ledger %s: expected %r got %r" % (key, ledger[key], value))
    return failures


if __name__ == "__main__":
    failures = check_fixtures(Path(__file__).resolve().parent.parent / "fixtures")
    print("\n".join(failures) if failures else "OpenBitFun reference matches shared fixtures")
    sys.exit(bool(failures))
