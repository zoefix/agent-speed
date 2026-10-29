#!/usr/bin/env python3
"""Synthetic completed-call references while a Trae turn is still running.

These samples have no previous completed turn, reproducing a first run after
the shared 30-minute estimate window expires. No private native data is used.
"""
import json
import sqlite3

from trae_fixtures import BASE, ROOT, encrypt
from trae_timing_fixtures import (
    HTTP, MODEL, PLAN, STREAM, end, event, fetch, log_bytes, native_events,
    span, timed_database, timing,
)


def main():
    cases = []
    names = [
        "before-first-completion", "next-request", "tools", "completed", "canceled", "failed",
        "missing-metadata", "invalid-usage", "deleted-client", "deleted-server",
        "duplicate-client", "duplicate-server", "foreign-run", "missing-run-columns",
        "pre-turn-created", "future-created", "fractional-created", "wrong-trace",
        "missing-done", "missing-eof", "retry", "request-error", "unclosed-call", "no-log",
    ]
    for name in names:
        db = sqlite3.connect(":memory:")
        db.deserialize(timed_database(plain=True))
        db.execute("DELETE FROM chat_turn WHERE turn_id!='t-one'")
        db.execute("UPDATE history_v2 SET agent_run_id='run-one'")
        db.execute("UPDATE server_history_info SET agent_run_id='run-one'")
        db.execute("UPDATE chat_turn SET turn_status='in_progress', "
                   "context=json_set(context,'$.chat_end_time',0)")
        events = native_events()
        # The next request is open. The first request's 240 tokens / 8 s can
        # seed its estimate, but must not enter the completed-turn ledger yet.
        events += [span(20, "call_server_generate_plan_item", 11900, 1), fetch(20, 12000)]
        now, phase, refs, live, requests, output = BASE + 15000, "generating", 1, 30.0, 0, 0

        if name == "before-first-completion":
            events = native_events()[:8]  # root, create, Fetch, headers and TimingCost only
            db.execute("DELETE FROM history_v2")
            db.execute("DELETE FROM server_history_info")
            now, refs, live = BASE + 5000, 0, 0.0
        elif name == "tools":
            events = native_events()
            now, phase, live = BASE + 10000, "tools", 0.0
        elif name == "completed":
            final_meta = dict(config_name=MODEL, exact_usage_v1="chat_completion",
                exact_token_semantics_v1="provider_raw_usage_completion_includes_reasoning",
                exact_prompt_tokens_v1="900", exact_output_tokens_v1="600", exact_total_tokens_v1="1500",
                exact_reasoning_tokens_v1="50", exact_cache_read_input_tokens_v1="100")
            db.execute("INSERT INTO history_v2 VALUES(2,'history-last','s-main','response-one','llm_default',?,?,0,'run-one')",
                       ((BASE + 18000)//1000, (BASE + 18000)//1000))
            db.execute("INSERT INTO server_history_info VALUES('server-last','history-last','llm_default','server',?,?,?,0,'run-one')",
                       (json.dumps(final_meta), (BASE + 18000)//1000, (BASE + 18000)//1000))
            db.execute("UPDATE chat_turn SET turn_status='completed', context=json_set(context,"
                       "'$.chat_end_time',?,'$.token_usage.prompt_tokens',900,'$.token_usage.completion_tokens',600,"
                       "'$.token_usage.total_tokens',1500,'$.token_usage.cache_read_input_tokens',100,"
                       "'$.token_usage.reasoning_tokens',50)", (BASE + 19000,))
            events += [event(20, 12500, PLAN, timing()), span(21, "update_turn_context", 17970, 20), end(21, 17971),
                event(20, 18000, STREAM, "DONE"), event(20, 18001, STREAM, "EOF"),
                span(22, "add_origin_history", 18002, 20, {"history_v2_id": "history-last"}), end(22, 18003),
                span(23, "end_slardar_event_with_metrics", 18040, 20, {"event_name": "IcubeAiAgentTaskPlanToolTokenUsage"}),
                end(23, 18041), end(20, 18050)]
            now, phase, refs, live, requests, output = BASE + 22000, "idle", 0, 0.0, 2, 840
        elif name in ("canceled", "failed"):
            db.execute("UPDATE chat_turn SET turn_status=?,context=json_set(context,'$.chat_end_time',?)", (name, BASE + 14000))
            phase, refs, live = "idle", 0, 0.0
        elif name == "missing-metadata":
            db.execute("UPDATE server_history_info SET extra_info='{}'")
        elif name == "invalid-usage":
            db.execute("UPDATE server_history_info SET extra_info=json_set(extra_info,'$.exact_total_tokens_v1','1441')")
        elif name == "deleted-client":
            db.execute("UPDATE history_v2 SET deleted_at=1")
        elif name == "deleted-server":
            db.execute("UPDATE server_history_info SET is_deleted=1")
        elif name == "duplicate-client":
            db.execute("INSERT INTO history_v2 SELECT 2,history_v2_id,session_id,message_id,content_source,created_at,updated_at,deleted_at,agent_run_id FROM history_v2 WHERE id=1")
        elif name == "duplicate-server":
            db.execute("INSERT INTO server_history_info SELECT 'server-duplicate',client_history_id,source,history_source,extra_info,created_at,updated_at,is_deleted,agent_run_id FROM server_history_info WHERE history_id='server-final'")
        elif name == "foreign-run":
            db.execute("UPDATE server_history_info SET agent_run_id='foreign-run'")
        elif name == "missing-run-columns":
            db.execute("ALTER TABLE history_v2 DROP COLUMN agent_run_id")
            db.execute("ALTER TABLE server_history_info DROP COLUMN agent_run_id")
        elif name == "pre-turn-created":
            db.execute("UPDATE history_v2 SET created_at=?", ((BASE//1000)-1,))
        elif name == "future-created":
            db.execute("UPDATE server_history_info SET created_at=?", ((BASE//1000)+100,))
        elif name == "fractional-created":
            db.execute("UPDATE history_v2 SET created_at=created_at+0.5")
        elif name == "wrong-trace":
            events[5]["fields"]["trace_id"] = b"foreign-trace"
        elif name in ("missing-done", "missing-eof"):
            message = b"DONE" if name == "missing-done" else b"EOF"
            events = [e for e in events if e.get("fields", {}).get("message") != message]
        elif name == "retry":
            events[8] = event(3, 2000, PLAN, timing(retry=True))
        elif name == "request-error":
            events.append(event(3, 4000, HTTP, "transport failed", level="ERROR"))
        elif name == "unclosed-call":
            events = [e for e in events if not (e.get("sid") == 3 and "end" in e)]
        elif name == "no-log":
            phase = "idle"

        if name not in ("next-request", "tools", "completed", "canceled", "failed", "before-first-completion"):
            refs, live = 0, 0.0
        db.commit()
        root = ROOT / "trae" / ("live-reference-" + name)
        path = root / "ModularData/ai-agent/database.db"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(encrypt(db.serialize()))
        db.close()
        log = root / "logs/20261020T120000/Modular/ai_agent-0-2026-10-20.alaudalog"
        if name != "no-log":
            log.parent.mkdir(parents=True, exist_ok=True)
            log.write_bytes(log_bytes(events))
        cases.append(dict(name=name, root=str(root.relative_to(ROOT)), nowMs=now, phase=phase,
            expectedReferenceCount=refs, expectedLiveOutput=live, expectedRequests=requests, expectedOutput=output))
    (ROOT / "trae-live-reference-expected.json").write_text(json.dumps(dict(version=1, cases=cases), indent=2) + "\n")
    print(f"Generated {len(cases)} synthetic Trae live-reference cases")


if __name__ == "__main__":
    main()
