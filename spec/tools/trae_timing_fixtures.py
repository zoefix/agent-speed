#!/usr/bin/env python3
"""Synthetic native-shape Trae timing fixtures: no user log or database content."""
import copy
from datetime import datetime, timezone
import json
from pathlib import Path
import sqlite3
import struct
import zlib

from trae_fixtures import BASE, ROOT, database, encrypt, records

MODEL = "claude-sonnet-4-5"
HTTP = "byted_aha_ffi::aha_net::http"
STREAM = "ai_agent::infrastructure::aha_net::stream"
PLAN = "ai_agent::domain::plan::simple_service_v2"


def stamp(offset):
    dt = datetime.fromtimestamp((BASE + offset) / 1000, timezone.utc)
    return [dt.year, dt.timetuple().tm_yday, dt.hour, dt.minute, dt.second, dt.microsecond * 1000, 0, 0, 0]


def span(sid, name, start, parent=None, tags=None):
    result = dict(sid=sid, name=name, start=stamp(start))
    result["psid" if parent is not None else "tid"] = parent if parent is not None else 987654321
    if tags:
        result["tags"] = [[key, value.encode()] for key, value in tags.items()]
    return result


def end(sid, when):
    return dict(sid=sid, end=stamp(when))


def event(sid, when, target, message, level="INFO", trace="trace-one"):
    return dict(time=stamp(when), level=level, span=sid, file="synthetic.rs", line=1,
                marker=dict(target=target), fields=dict(message=message.encode(), trace_id=trace.encode()), thread_id=1)


def fetch(sid=3, when=1000, endpoint="commit_toolcall_result"):
    return event(sid, when, HTTP, "[aha_net] send: calling Fetch id=req-one, method=POST, "
                 "url=https://example.invalid/api/agent/v3/" + endpoint + ", headers_count=0, body_len=0")


def timing(model=MODEL, retry=False):
    body = json.dumps(dict(config_name=model, is_retry=retry), separators=(",", ":"))
    return "TimingCost: TimingCostOriginEvent { data: " + json.dumps(body) + " }"


def native_events():
    # do_chat deliberately has no close record (native traces retain this span).
    return [span(1, "do_chat", 0), span(2, "create_chat_turn_with_id", 10, 1),
            dict(sid=2, tags=[["session_id", b"s-main"], ["turn_id", b"t-one"]]), end(2, 20),
            span(3, "call_server_generate_plan_item", 900, 1), fetch(),
            span(4, "send_streaming", 999, 3), end(4, 1100),  # headers, NOT model completion
            event(3, 2000, PLAN, timing()),
            span(5, "update_turn_context", 8970, 3), end(5, 8971),
            event(3, 9000, STREAM, "DONE"), event(3, 9001, STREAM, "EOF"),
            span(6, "add_origin_history", 9002, 3, {"history_v2_id": "history-final"}), end(6, 9003),
            span(7, "end_slardar_event_with_metrics", 9040, 3,
                 {"event_name": "IcubeAiAgentTaskPlanToolTokenUsage"}), end(7, 9041), end(3, 9050)]


def cbor(value):
    def head(major, size):
        if size < 24:
            return bytes([major * 32 + size])
        for width, indicator in [(1, 24), (2, 25), (4, 26), (8, 27)]:
            if size < 1 << (width * 8):
                return bytes([major * 32 + indicator]) + size.to_bytes(width, "big")
        raise ValueError("CBOR integer out of bounds")
    if value is None: return b"\xf6"
    if value is False: return b"\xf4"
    if value is True: return b"\xf5"
    if isinstance(value, int): return head(0, value) if value >= 0 else head(1, -1 - value)
    if isinstance(value, bytes): return head(2, len(value)) + value
    if isinstance(value, str):
        encoded = value.encode()
        return head(3, len(encoded)) + encoded
    if isinstance(value, list): return head(4, len(value)) + b"".join(cbor(x) for x in value)
    if isinstance(value, dict): return head(5, len(value)) + b"".join(cbor(k) + cbor(v) for k, v in value.items())
    raise ValueError(type(value))


def log_bytes(events):
    header = bytearray(152)
    header[:4] = b"AalG"
    header[8:12] = b"Atab"
    header[16:24] = (152).to_bytes(8, "little")
    segment = b"AALG\x00\x00\x01\x00" + struct.pack("<Q", BASE) + struct.pack("<I", 5) + b"3.4.9"
    compressor = zlib.compressobj(level=6, wbits=-15)
    payload = b"".join(cbor(row) for row in events)
    return bytes(header) + segment + compressor.compress(payload) + compressor.flush()


def timed_database(mismatch=False, variant="valid", plain=False):
    db = sqlite3.connect(":memory:")
    db.deserialize(database())
    db.executescript("""
        ALTER TABLE chat_turn ADD COLUMN response_message_id TEXT NOT NULL DEFAULT '';
        UPDATE chat_turn SET response_message_id='response-one',
            context=json_set(context,'$.trace_id','trace-one','$.token_usage.cache_creation_input_tokens',0) WHERE turn_id='t-one';
        CREATE TABLE history_v2 (id INTEGER PRIMARY KEY,history_v2_id TEXT,session_id TEXT,message_id TEXT,
            content_source TEXT,created_at BIGINT,updated_at BIGINT,deleted_at BIGINT DEFAULT 0);
        CREATE TABLE server_history_info (history_id TEXT PRIMARY KEY,client_history_id TEXT,
            source TEXT,history_source TEXT,extra_info TEXT,created_at BIGINT,updated_at BIGINT,is_deleted BOOLEAN);
    """)
    meta = dict(config_name=MODEL, exact_usage_v1="chat_completion",
                exact_token_semantics_v1="provider_raw_usage_completion_includes_reasoning",
                exact_prompt_tokens_v1="1200", exact_output_tokens_v1="241" if mismatch else "240",
                exact_total_tokens_v1="1441" if mismatch else "1440",
                exact_reasoning_tokens_v1="40", exact_cache_read_input_tokens_v1="400")
    db.execute("INSERT INTO history_v2 VALUES(1,'history-final','s-main','response-one','llm_default',?,?,0)",
               ((BASE + 9000) // 1000, (BASE + 9000) // 1000))
    db.execute("INSERT INTO server_history_info VALUES('server-final','history-final','llm_default','server',?,?,?,NULL)",
               (json.dumps(meta), (BASE + 9000) // 1000, (BASE + 9000) // 1000))
    if variant == "deleted-history":
        db.execute("UPDATE server_history_info SET is_deleted=1")
    if variant == "blank-server-id":
        db.execute("UPDATE server_history_info SET history_id='' ")
    if variant == "deleted-client":
        db.execute("UPDATE history_v2 SET deleted_at=1")
    if variant == "newer-unmatched":
        db.execute("INSERT INTO history_v2 VALUES(2,'history-newer','s-main','response-one','llm_default',?,?,0)",
                   ((BASE + 10000) // 1000, (BASE + 10000) // 1000))
    if variant == "earlier-equal-usage":
        db.execute("INSERT INTO history_v2 VALUES(2,'history-earlier','s-main','response-one','llm_default',?,?,0)",
                   ((BASE + 8000) // 1000, (BASE + 8000) // 1000))
        db.execute("INSERT INTO server_history_info VALUES('server-earlier','history-earlier','llm_default','server',?,?,?,NULL)",
                   (json.dumps(meta), (BASE + 8000) // 1000, (BASE + 8000) // 1000))
    if variant.startswith("multi-"):
        early = dict(meta, exact_prompt_tokens_v1="600", exact_output_tokens_v1="60", exact_total_tokens_v1="660",
                     exact_reasoning_tokens_v1="10", exact_cache_read_input_tokens_v1="200")
        if variant=="multi-foreign-model":early["config_name"]="other-model"
        if variant=="multi-zero-output":early.update(exact_output_tokens_v1="0",exact_total_tokens_v1="600",exact_reasoning_tokens_v1="0")
        earlier = BASE // 1000
        if variant == "multi-long-tools":
            earlier -= 119
            db.execute("UPDATE chat_turn SET context=json_set(context,'$.chat_start_time',?) WHERE turn_id='t-one'", (BASE-120000,))
        db.execute("INSERT INTO history_v2 VALUES(2,'history-earlier','s-main','response-one','Grep',?,?,0)", (earlier,earlier))
        db.execute("INSERT INTO server_history_info VALUES('server-earlier','history-earlier','llm_default','server',?,?,?,NULL)",
                   (json.dumps(early),earlier,earlier))
        if variant == "multi-invalid-usage":
            db.execute("UPDATE server_history_info SET extra_info=json_set(extra_info,'$.exact_output_tokens_v1','invalid') WHERE history_id='server-earlier'")
        if variant == "multi-deleted-earlier":
            db.execute("UPDATE history_v2 SET deleted_at=1 WHERE id=2")
        if variant == "multi-duplicate-server":
            db.execute("INSERT INTO server_history_info SELECT 'server-copy',client_history_id,source,history_source,extra_info,created_at,updated_at,is_deleted FROM server_history_info WHERE history_id='server-earlier'")
        if variant == "multi-duplicate-client":
            db.execute("INSERT INTO history_v2 SELECT 3,history_v2_id,session_id,message_id,content_source,created_at,updated_at,deleted_at FROM history_v2 WHERE id=2")
        if variant == "multi-pre-turn":
            db.execute("UPDATE history_v2 SET created_at=created_at-1 WHERE id=2")
        if variant == "multi-invalid-time":
            db.execute("UPDATE history_v2 SET created_at=0 WHERE id=2")
        if variant == "multi-fraction-time":
            db.execute("UPDATE history_v2 SET created_at=created_at+0.5 WHERE id=2")
        if variant == "multi-final-time-invalid":
            db.execute("UPDATE server_history_info SET created_at=0 WHERE history_id='server-final'")
        if variant == "multi-final-mismatch":
            db.execute("UPDATE server_history_info SET extra_info=json_set(extra_info,'$.exact_output_tokens_v1','241','$.exact_total_tokens_v1','1441') WHERE history_id='server-final'")
        if variant == "multi-newer-unmatched":
            db.execute("INSERT INTO history_v2 VALUES(3,'history-newer','s-main','response-one','llm_default',?,?,0)",((BASE+10000)//1000,(BASE+10000)//1000))
        if variant == "multi-missing-start":
            db.execute("UPDATE chat_turn SET context=json_remove(context,'$.chat_start_time') WHERE turn_id='t-one'")
    if variant != "multi-missing-run-columns":
        db.executescript("ALTER TABLE history_v2 ADD COLUMN agent_run_id TEXT DEFAULT 'run-one'; ALTER TABLE server_history_info ADD COLUMN agent_run_id TEXT DEFAULT 'run-one';")
    if variant == "multi-run-mismatch":
        db.execute("UPDATE server_history_info SET agent_run_id='other-run' WHERE history_id='server-earlier'")
    if variant == "multi-final-run-mismatch":
        db.execute("UPDATE server_history_info SET agent_run_id='other-run' WHERE history_id='server-final'")
    db.commit()
    result = db.serialize()
    db.close()
    assert result[20] == 80
    return result if plain else encrypt(result)


def earlier_events(long_tools=False):
    delta = -120000 if long_tools else 0
    return [span(20,"call_server_generate_plan_item",50+delta,1),fetch(20,100+delta,"create_agent_task"),
            event(20,200+delta,PLAN,timing()),span(21,"update_turn_context",870+delta,20),end(21,875+delta),
            span(22,"add_origin_history",890+delta,20,{"history_v2_id":"history-earlier"}),end(22,895+delta),
            event(20,900+delta,STREAM,"DONE"),event(20,901+delta,STREAM,"EOF"),
            span(23,"end_slardar_event_with_metrics",920+delta,20,{"event_name":"IcubeAiAgentTaskPlanToolTokenUsage"}),end(23,925+delta),
            event(20,930+delta,"ai_agent::domain::permission::script_safety","tool rejected",level="ERROR"),
            end(20,9050 if long_tools else 950)]


def main():
    cases = []
    for name in ["valid", "nested-noise", "missing-done", "retry", "wrong-history", "wrong-model",
                 "second-fetch", "overlap", "wrong-trace", "usage-mismatch", "headers-only", "replay-history",
                 "deleted-history", "deleted-client", "newer-unmatched", "earlier-open", "wrong-target", "earlier-equal-usage",
                 "retry-message", "create-agent-task", "post-done-error", "in-stream-error", "undated-error",
                 "origin-before-done", "repeated-telemetry", "late-telemetry", "multi-call", "multi-no-logs",
                 "multi-ambiguous", "multi-long-tools", "multi-invalid-usage", "multi-deleted-earlier",
                 "multi-duplicate-server", "multi-duplicate-client", "multi-pre-turn", "multi-run-mismatch",
                 "multi-final-mismatch", "multi-newer-unmatched", "multi-missing-start", "multi-missing-run-columns",
                 "multi-later-open", "post-done-other-trace", "retry-feature-flag", "multi-invalid-time", "multi-fraction-time",
                 "multi-final-time-invalid", "multi-final-run-mismatch", "multi-foreign-model", "multi-zero-output", "blank-server-id"]:
        events = copy.deepcopy(native_events())
        accepted = name in ("valid", "nested-noise", "earlier-equal-usage", "create-agent-task", "post-done-error",
                            "origin-before-done", "repeated-telemetry", "late-telemetry", "post-done-other-trace", "retry-feature-flag") or name.startswith("multi-")
        if name in ("multi-no-logs", "multi-final-mismatch", "multi-newer-unmatched", "multi-later-open", "multi-final-time-invalid"):
            accepted = False
        if name == "missing-done":
            events = [e for e in events if e.get("fields", {}).get("message") != b"DONE"]
        if name == "headers-only":
            events = [e for e in events if e.get("fields", {}).get("message") not in (b"DONE", b"EOF")]
        if name == "retry":
            next(e for e in events if e.get("marker", {}).get("target") == PLAN)["fields"]["message"] = timing(retry=True).encode()
        if name == "retry-message":
            events.append(event(3, 2100, PLAN, "server retried request"))
        if name == "wrong-model":
            next(e for e in events if e.get("marker", {}).get("target") == PLAN)["fields"]["message"] = timing(model="other-model").encode()
        if name == "wrong-target":
            next(e for e in events if e.get("marker", {}).get("target") == PLAN)["marker"]["target"] = "other::target"
        if name == "wrong-history":
            next(e for e in events if e.get("name") == "add_origin_history")["tags"] = [["history_v2_id", b"other-history"]]
        if name == "replay-history":
            next(e for e in events if e.get("name") == "add_origin_history")["name"] = "update_history_by_server"
        if name == "second-fetch":
            events.append(fetch(3, 1100))
        if name == "wrong-trace":
            next(e for e in events if e.get("fields", {}).get("message") == b"DONE")["fields"]["trace_id"] = b"other-trace"
        if name == "overlap":
            events.extend([span(20, "call_server_generate_plan_item", 2000, 1), fetch(20, 2100),
                           event(20, 5000, STREAM, "DONE"), event(20, 5001, STREAM, "EOF"), end(20, 5100)])
        if name == "earlier-open":
            events.extend([span(20, "call_server_generate_plan_item", 700, 1), fetch(20, 800)])
        if name == "nested-noise":
            # An unrelated nested logical wrapper's markers must not contaminate the outer request.
            events.extend([span(20, "call_server_generate_plan_item", 3000, 3),
                           event(20, 3100, STREAM, "DONE"), event(20, 3101, STREAM, "EOF"), end(20, 3200)])
        if name == "post-done-other-trace":
            events.append(event(3,9020,"tool::script","tool completed",trace="tool-trace"))
        if name == "retry-feature-flag":
            events.append(event(3,2100,PLAN,"enable_retry_after_content_loop=true"))
        if name == "create-agent-task":
            next(e for e in events if e.get("fields",{}).get("message",b"").startswith(b"[aha_net]"))["fields"]["message"] = fetch(endpoint="create_agent_task")["fields"]["message"]
        if name in ("post-done-error", "in-stream-error", "undated-error"):
            error = event(3, 9020 if name == "post-done-error" else 2100,"ai_agent::domain::permission::script_safety","script rejected",level="ERROR")
            if name == "undated-error": del error["time"]
            events.append(error)
        if name == "origin-before-done":
            next(e for e in events if e.get("sid")==6 and "start" in e)["start"] = stamp(8913)
            next(e for e in events if e.get("sid")==6 and "end" in e)["end"] = stamp(8914)
        if name == "repeated-telemetry":
            events.extend([span(30,"update_turn_context",8990,3),end(30,8991),
                           span(31,"end_slardar_event_with_metrics",9042,3,{"event_name":"IcubeAiAgentTaskPlanToolTokenUsage"}),end(31,9043)])
        if name == "late-telemetry":
            for e in events:
                if e.get("sid")==5:
                    if "start" in e:e["start"]=stamp(9004)
                    if "end" in e:e["end"]=stamp(9005)
                if e.get("sid")==7:
                    if "start" in e:e["start"]=stamp(12506)
                    if "end" in e:e["end"]=stamp(12507)
                if e.get("sid")==3 and "end" in e:e["end"]=stamp(12520)
        if name.startswith("multi-"):
            events.extend(earlier_events(name=="multi-long-tools"))
            if name=="multi-long-tools":
                next(e for e in events if e.get("sid")==1 and "start" in e)["start"]=stamp(-120100)
                next(e for e in events if e.get("sid")==2 and "start" in e)["start"]=stamp(-120000)
                next(e for e in events if e.get("sid")==2 and "end" in e)["end"]=stamp(-119999)
            if name=="multi-ambiguous":
                events.append(event(20,250,PLAN,timing()))
            if name=="multi-foreign-model":
                next(e for e in events if e.get("span")==20 and e.get("marker",{}).get("target")==PLAN)["fields"]["message"]=timing(model="other-model").encode()
            if name=="multi-later-open":
                events.extend([span(40,"call_server_generate_plan_item",9200,1),fetch(40,9300)])
            if name=="multi-no-logs":events=[]
        root = ROOT / "trae" / ("timing-" + name)
        path = root / "ModularData/ai-agent/database.db"
        path.parent.mkdir(parents=True, exist_ok=True)
        data = timed_database(mismatch=name == "usage-mismatch", variant=name, plain=True)
        if name=="late-telemetry":
            db=sqlite3.connect(":memory:");db.deserialize(data)
            db.execute("UPDATE chat_turn SET context=json_set(context,'$.chat_end_time',?) WHERE turn_id='t-one'",(BASE+13000,));db.commit()
            data=db.serialize();db.close()
        path.write_bytes(encrypt(data))
        log = root / "logs/20261020T120000/Modular/ai_agent-0-2026-10-20.alaudalog"
        log.parent.mkdir(parents=True, exist_ok=True)
        log.write_bytes(log_bytes(events))
        wanted = records()
        wanted[0]["cacheWriteInput"] = 0
        if name=="late-telemetry":wanted[0]["endMs"]=BASE+13000
        if accepted:
            wanted[0]["startMs"], wanted[0]["endMs"] = BASE + 1000, BASE + 9000
        extra_counted = name=="earlier-equal-usage" or name in ("multi-call","multi-no-logs","multi-ambiguous","multi-long-tools","multi-later-open","multi-foreign-model","multi-zero-output")
        if extra_counted:
            extra=dict(wanted[0],key="trae:history:s-main:history-earlier",startMs=None,endMs=BASE,model=MODEL,
                       input=600,output=60,cachedInput=200,reasoning=10)
            if name=="earlier-equal-usage":
                extra.update(input=1200,output=240,cachedInput=400,reasoning=40,endMs=BASE+8000)
            elif name not in ("multi-no-logs","multi-ambiguous","multi-zero-output"):
                shift=-120000 if name=="multi-long-tools" else 0
                extra.update(startMs=BASE+100+shift,endMs=BASE+900+shift)
            if name=="multi-foreign-model":extra["model"]="other-model"
            if name=="multi-zero-output":extra.update(output=0,reasoning=0)
            wanted.append(extra)
        cases.append(dict(name=name, root=str(root.relative_to(ROOT)), database=str(path.relative_to(ROOT)),
                          log=str(log.relative_to(ROOT)), measured=accepted, responses=wanted))
    (ROOT / "trae-timing-expected.json").write_text(json.dumps(dict(version=1, cases=cases), indent=2) + "\n")
    print("Generated %d synthetic Trae timing cases" % len(cases))


def activity_fixtures():
    cases=[]
    names=["generating","tools","next-fetch","completed","canceled","paused","queuing","unknown-status",
           "positive-end","finished-span","stale","future","wrong-trace","missing-root","closed-failed",
           "failed-then-next","overlapping","eof-without-done","error","missing-start","malformed-latest","old-malformed"]
    for name in names:
        db=sqlite3.connect(":memory:");db.deserialize(timed_database(plain=True))
        db.execute("DELETE FROM chat_turn WHERE turn_id!='t-one'")
        value=json.loads(db.execute("SELECT context FROM chat_turn").fetchone()[0])
        value.pop("token_usage",None)
        value.update(chat_start_time=BASE+20000,chat_end_time=0,trace_id="trace-live")
        status=name if name in ("completed","canceled","paused","queuing") else "unknown" if name=="unknown-status" else "in_progress"
        if name in ("completed","canceled","positive-end"):value["chat_end_time"]=BASE+24000
        if name=="missing-start":value.pop("chat_start_time")
        db.execute("INSERT INTO chat_turn VALUES(100,'s-main','t-live',?,?,?,?,0,'response-live')",
                   (status,json.dumps(value), (BASE+20000)//1000,(BASE+20000)//1000))
        if name in ("malformed-latest","old-malformed"):
            bad_time=BASE+21000 if name=="malformed-latest" else BASE-10000
            db.execute("INSERT INTO chat_turn VALUES(101,'s-main','t-newer','in_progress','{bad',?,?,0,'response-newer')",
                       (bad_time//1000,bad_time//1000))
        events=native_events()
        active=[span(101,"do_chat",20000),span(102,"create_chat_turn_with_id",20010,101,{"session_id":"s-main","turn_id":"t-live"}),end(102,20020),
                span(103,"call_server_generate_plan_item",21000,101),fetch(103,21100)]
        now=BASE+22000; phase="generating"; start=BASE+21100
        done_names=("tools","next-fetch","finished-span")
        if name in done_names:
            active += [event(103,23000,STREAM,"DONE"),event(103,23001,STREAM,"EOF"),end(103,23050)]
            now=BASE+24000;phase="tools";start=None
        if name=="next-fetch":
            active += [span(104,"call_server_generate_plan_item",24100,101),fetch(104,24200)]
            now=BASE+25000;phase="generating";start=BASE+24200
        if name=="finished-span":
            active += [span(105,"chat_turn_finish",23500,101),end(105,23520)]
        if name=="stale":now=BASE+400000
        if name=="future":now=BASE+21050
        if name=="wrong-trace":active[-1]["fields"]["trace_id"]=b"other-trace"
        if name=="missing-root":
            active=[e for e in active if e.get("sid")!=102]
        if name in ("closed-failed","failed-then-next"):
            active += [end(103,21500)]
            if name=="failed-then-next":
                active += [span(104,"call_server_generate_plan_item",21600,101),fetch(104,21700)]
                phase="generating";start=BASE+21700
        if name=="overlapping":active += [span(104,"call_server_generate_plan_item",21200,101),fetch(104,21300)]
        if name=="eof-without-done":active += [event(103,21500,STREAM,"EOF")]
        if name=="error":active += [event(103,21500,HTTP,"transport failed",level="ERROR")]
        for e in active:
            if "fields" in e and not (name=="wrong-trace" and e.get("fields",{}).get("trace_id")==b"other-trace"):
                e["fields"]["trace_id"]=b"trace-live"
        events += active
        if name not in ("generating","tools","next-fetch","failed-then-next","old-malformed"):phase="idle";start=None
        db.commit();data=db.serialize();db.close()
        root=ROOT/"trae"/("activity-"+name);path=root/"ModularData/ai-agent/database.db"
        path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(encrypt(data))
        log=root/"logs/20261020T120000/Modular/ai_agent-0-2026-10-20.alaudalog"
        log.parent.mkdir(parents=True,exist_ok=True);log.write_bytes(log_bytes(events))
        cases.append(dict(name=name,root=str(root.relative_to(ROOT)),database=str(path.relative_to(ROOT)),log=str(log.relative_to(ROOT)),nowMs=now,
                          expected=[dict(sessionId="s-main",phase=phase,inFlightStartMs=start,model=MODEL)]))
    (ROOT/"trae-activity-expected.json").write_text(json.dumps(dict(version=1,cases=cases),indent=2)+"\n")
    print("Generated %d synthetic Trae activity cases" % len(cases))


if __name__ == "__main__":
    main()
    activity_fixtures()
