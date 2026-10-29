#!/usr/bin/env python3
"""Independent, conservative final-request timing join. Never guesses a duration."""
from collections import defaultdict
import json
import math
from pathlib import Path
import re
from urllib.parse import urlsplit

from trae_alauda import read, string, time_ms

GEN = "call_server_generate_plan_item"
HTTP = "byted_aha_ffi::aha_net::http"
STREAM = "ai_agent::infrastructure::aha_net::stream"
PLAN = "ai_agent::domain::plan::simple_service_v2"
SEMANTICS = "provider_raw_usage_completion_includes_reasoning"


def exact(value, cap=10 ** 12):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9]{1,13}", value):
        return None
    return int(value) if int(value) <= cap else None


def evidence(db):
    """Project only exact counters and identities; final reconciliation gates all extra calls."""
    try:
        vcols = {r[1] for r in db.execute("PRAGMA table_info(history_v2)")}
        hcols = {r[1] for r in db.execute("PRAGMA table_info(server_history_info)")}
        vr = "v.agent_run_id" if "agent_run_id" in vcols else "NULL"
        hr = "h.agent_run_id" if "agent_run_id" in hcols else "NULL"
        query = f"""SELECT t.session_id,t.turn_id,t.id,
            json_extract(t.context,'$.trace_id'),json_extract(t.context,'$.chat_end_time'),
            json_extract(t.context,'$.chat_start_time'),v.history_v2_id,v.created_at,v.content_source,
            h.created_at,{vr},{hr},
            CASE WHEN json_valid(h.extra_info) THEN json_extract(h.extra_info,
              '$.exact_usage_v1','$.exact_token_semantics_v1','$.exact_prompt_tokens_v1',
              '$.exact_output_tokens_v1','$.exact_total_tokens_v1','$.exact_reasoning_tokens_v1',
              '$.exact_cache_read_input_tokens_v1','$.config_name') END,h.history_id
            FROM chat_turn t JOIN history_v2 v ON v.session_id=t.session_id AND v.message_id=t.response_message_id
            LEFT JOIN server_history_info h ON h.client_history_id=v.history_v2_id
              AND h.source='llm_default' AND h.history_source='server' AND coalesce(h.is_deleted,0)=0
            WHERE t.turn_status='completed' AND coalesce(t.deleted_at,0)=0 AND json_valid(t.context)
              AND coalesce(v.deleted_at,0)=0"""
        rows = list(db.execute(query))
    except Exception:
        return {}
    groups = defaultdict(list)
    history_owners, server_owners = defaultdict(int), defaultdict(int)
    for row in rows:
        groups["trae:%s:%s" % (row[0], row[1] or row[2])].append(row)
        history_owners[(row[0],row[6])] += 1
        server_owners[(row[0],row[13])] += 1

    def proof(row):
        if any(not isinstance(row[i],str) or not row[i].strip() or len(row[i].encode())>4096 for i in (0,6,13)):
            return None
        if any(type(row[i]) is not int or not 0 < row[i] <= 253402300799 for i in (7,9)):
            return None
        try:
            kind, semantics, inp, out, total, reasoning, cached, config = json.loads(row[12])
        except (ValueError, TypeError):
            return None
        if kind != "chat_completion" or semantics != SEMANTICS or not isinstance(config, str) or not config.strip():
            return None
        values = [exact(inp), exact(out), exact(total, 2 * 10 ** 12), exact(reasoning), exact(cached)]
        if None in values: return None
        inp, out, total, reasoning, cached = values
        if total != inp + out or inp + out == 0 or reasoning > out or cached > inp: return None
        return dict(session=row[0], turn=row[1], trace=row[3], chatEnd=row[4], chatStart=row[5], history=row[6],
                    config=config, input=inp, output=out, reasoning=reasoning, cachedInput=cached,
                    vCreated=row[7], hCreated=row[9], vRun=row[10], hRun=row[11])

    result = {}
    for key, group in groups.items():
        finals = [r for r in group if r[8] == "llm_default"]
        if not finals or any(type(r[7]) is not int for r in finals): continue
        latest = max(r[7] for r in finals)
        finals = [r for r in finals if r[7] == latest]
        if len(finals) != 1 or not (final := proof(finals[0])): continue
        if sum(r[6]==finals[0][6] for r in group)!=1 or sum(r[13]==finals[0][13] for r in group)!=1:continue
        extras = []
        by_history = defaultdict(list)
        for row in group: by_history[row[6]].append(row)
        start, end = final["chatStart"], final["chatEnd"]
        run = final["vRun"]
        if (isinstance(run, str) and run.strip() and run == final["hRun"]
            and history_owners[(finals[0][0],finals[0][6])]==1 and server_owners[(finals[0][0],finals[0][13])]==1
            and type(start) in (int,float) and type(end) in (int,float) and math.isfinite(start) and math.isfinite(end)
            and start==int(start) and end==int(end) and 0 < start <= end <= 253402300799999
            and type(final["hCreated"]) is int
            and start//1000 <= final["vCreated"] <= (end+999)//1000
            and start//1000 <= final["hCreated"] <= (end+999)//1000):
            for history, candidates in by_history.items():
                if history == final["history"] or len(candidates) != 1: continue
                if history_owners[(candidates[0][0],history)]!=1 or server_owners[(candidates[0][0],candidates[0][13])]!=1:continue
                item = proof(candidates[0])
                if not item or item["vRun"] != run or item["hRun"] != run: continue
                vc, hc = item["vCreated"], item["hCreated"]
                if type(vc) is not int or type(hc) is not int: continue
                if not (0 < vc <= final["vCreated"] and 0 < hc <= final["hCreated"]
                        and start // 1000 <= vc <= (end + 999) // 1000
                        and start // 1000 <= hc <= (end + 999) // 1000): continue
                extras.append(item)
        result[key] = (final, sorted(extras, key=lambda p:(p["hCreated"],p["history"])))
    return result


class Graph:
    def __init__(self, records):
        self.spans, self.events = {}, []
        for record in records:
            if "sid" in record:
                span = self.spans.setdefault(record["sid"], {"sid": record["sid"], "tags": {}})
                for key in ("name", "psid", "start", "end"):
                    if key in record:
                        if key in span and span[key] != record[key]:
                            raise ValueError("Conflicting span identity")
                        span[key] = record[key]
                for key, value in record.get("tags", []):
                    if key not in ("session_id", "turn_id", "history_v2_id", "event_name"):
                        continue
                    value = string(value)
                    if key in span["tags"] and span["tags"][key] != value:
                        raise ValueError("Conflicting span tags")
                    span["tags"][key] = value
            if "fields" in record:
                self.events.append(record)
        self.owned = defaultdict(list)
        for event in self.events:
            owner = self.ancestor(event.get("span"), GEN)
            if owner:
                self.owned[owner["sid"]].append(event)

    def ancestor(self, sid, name):
        visited = set()
        while sid in self.spans and sid not in visited and len(visited) < 64:
            visited.add(sid)
            span = self.spans[sid]
            if span.get("name") == name:
                return span
            sid = span.get("psid")
        return None

    def children(self, owner):
        return [s for s in self.spans.values() if s != owner and self.ancestor(s.get("psid"), GEN) == owner]

    def details(self, owner):
        sends, done, eof, timings = [], [], [], []
        bad, traces = [], []
        for event in self.owned[owner["sid"]]:
            message = string(event.get("fields", {}).get("message", ""))
            if not isinstance(message, str):
                continue
            at = time_ms(event.get("time"))
            target = event.get("marker", {}).get("target")
            trace = string(event.get("fields", {}).get("trace_id"))
            if trace: traces.append((at,trace))
            if event.get("level") == "ERROR": bad.append(at)
            if target == HTTP and message.startswith("[aha_net] send: calling Fetch id="):
                match = re.search(r"method=([A-Z]+), url=([^, ]+)", message)
                sends.append((at, match.group(1) if match else None, urlsplit(match.group(2)).path if match else None))
            if target == STREAM and message == "DONE": done.append(at)
            if target == STREAM and message == "EOF": eof.append(at)
            if target == PLAN and message.startswith("TimingCost: TimingCostOriginEvent { data: "):
                try:
                    literal = json.JSONDecoder().raw_decode(message.split("data: ", 1)[1])[0]
                    timings.append((at, json.loads(literal)))
                except (ValueError, TypeError): bad.append(at)
            elif re.search(r"\bretry\b|\bretried\b|\breconnect", message, re.I):
                bad.append(at)
        return sends, done, eof, timings, traces, bad

    def interval(self, proof, final=True):
        origins = [s for s in self.spans.values() if s.get("name") == "add_origin_history"
                   and s["tags"].get("history_v2_id") == proof["history"]]
        if len(origins) != 1: return None
        origin = origins[0]
        call = self.ancestor(origin.get("psid"), GEN)
        if not call: return None
        root = self.ancestor(call.get("psid"), "do_chat")
        if not root: return None
        creates = [s for s in self.spans.values() if s.get("name") == "create_chat_turn_with_id"
                   and s["tags"].get("turn_id") == proof["turn"]
                   and s["tags"].get("session_id") == proof["session"]
                   and self.ancestor(s.get("psid"), "do_chat") == root]
        if len(creates) != 1: return None
        sends, done, eof, timings, traces, bad = self.details(call)
        if any(len(x) != 1 for x in (sends, done, eof, timings)): return None
        start, method, path = sends[0]
        finish, close = done[0], time_ms(call.get("end"))
        first, timing = timings[0]
        if method != "POST" or path not in ("/api/agent/v3/commit_toolcall_result", "/api/agent/v3/create_agent_task"): return None
        if timing.get("is_retry") is not False or timing.get("config_name") != proof["config"]: return None
        children = self.children(call)
        usages = [s for s in children if s.get("name") == "end_slardar_event_with_metrics"
                  and s["tags"].get("event_name") == "IcubeAiAgentTaskPlanToolTokenUsage"]
        updates = [s for s in children if s.get("name") == "update_turn_context"]
        origin_time = time_ms(origin.get("start"))
        values = [start, finish, eof[0], close, first, origin_time, proof["chatEnd"], time_ms(call.get("start"))]
        if any(x is None for x in values): return None
        if any(at is None or (start <= at <= eof[0] and proof.get("trace") and trace != proof["trace"]) for at,trace in traces):return None
        if any(at is None or start <= at <= finish for at in bad): return None
        if not (values[7] <= start < finish <= eof[0] <= close <= proof["chatEnd"]
                and 0 < finish - start <= 900000 and eof[0] - finish <= 1000
                and start <= first <= finish and start <= origin_time <= close and abs(origin_time-finish)<=1000): return None
        if final and proof["chatEnd"] - finish > 60000: return None
        def marker_in(span, limit):
            a,b=time_ms(span.get("start")),time_ms(span.get("end"))
            return a is not None and b is not None and start <= a <= b <= min(close,finish+limit)
        if not any(marker_in(s,1000) for s in updates) or not any(marker_in(s,5000) for s in usages): return None
        for other in self.spans.values():
            if other == call or other.get("name") != GEN or self.ancestor(other.get("psid"), "do_chat") != root:
                continue
            other_sends, other_done, *_ = self.details(other)
            if not other_sends: continue
            if not final and all(send[0] is not None and send[0] >= finish for send in other_sends): continue
            if len(other_sends) != 1 or len(other_done) != 1: return None
            if other_sends[0][0] is None or other_done[0] is None: return None
            if other_sends[0][0] < finish and other_done[0] > start: return None
            if final and other_sends[0][0] > start: return None  # final request only
        return int(start), int(finish)


def log_paths(database):
    path = Path(database)
    if path.parent.name not in ("ai-agent", "ai-chat") or path.parent.parent.name != "ModularData":
        return []
    root = path.parent.parent.parent
    paths = [p for p in (root / "logs").glob("*/Modular/ai_agent*.alaudalog")
             if p.is_file() and not any(x.is_symlink() for x in (p, *p.parents))]
    return sorted(paths, key=lambda p: (p.stat().st_mtime_ns, str(p)), reverse=True)[:64]


def apply(db, database, rows):
    proofs = evidence(db)
    if not proofs: return rows
    paths = log_paths(database)
    try:
        if sum(path.stat().st_size for path in paths) > 32 * 1024 * 1024:
            paths = []
    except OSError:
        paths = []
    graphs = []
    decoded_total = 0
    for path in paths:
        try:
            records, size = read(path, limit=128 * 1024 * 1024 - decoded_total, with_size=True)
            decoded_total += size
            graphs.append(Graph(records))
        except (OSError, ValueError, RuntimeError, EOFError, TypeError, IndexError): continue
    appended = []
    for source, agent, row in rows:
        group = proofs.get(row["key"])
        if not group: continue
        proof, extras = group
        if row["cacheWriteInput"] != 0 or any(row[key] != proof[key] for key in ("input", "output", "reasoning", "cachedInput")):
            continue
        def measure(item, target, final):
            if target["output"] <= 0: return
            intervals = {i for graph in graphs if (i := graph.interval(item, final)) is not None}
            if len(intervals) == 1: target["startMs"], target["endMs"] = intervals.pop()
        measure(proof,row,True)
        for item in extras:
            extra = dict(row)
            extra.update(key="trae:history:"+item["session"]+":"+item["history"], startMs=None,
                         endMs=item["hCreated"]*1000, model=item["config"])
            for field in ("input","output","reasoning","cachedInput"): extra[field] = item[field]
            measure(item,extra,False)
            appended.append((source,agent,extra))
    rows.extend(appended)
    return rows


def check_fixtures(root):
    from trae_reference import read_trae
    root = Path(root)
    errors = []
    cases = json.loads((root / "trae-timing-expected.json").read_text())["cases"]
    for case in cases:
        actual = [r for _, _, r in read_trae(root / case["database"])]
        if actual != case["responses"]:
            errors.append("Trae timing " + case["name"] + ": " + repr(actual))
    return errors
