#!/usr/bin/env python3
"""Devin CLI and Desktop fixtures (METRICS 4.17), used by generate_fixtures.py."""
import json
import os
import sqlite3

from omp_fixtures import iso, ms


def chat(role, request=None, model=None, metrics=None, finish=None):
    metadata = {"num_tokens": None, "is_user_input": role == "user", "request_id": request,
                "metrics": metrics, "finish_reason": finish}
    if model is not None:
        metadata["generation_model"] = model
    return json.dumps({"role": role, "content": "content not read", "metadata": metadata}, separators=(",", ":"))


def metrics(inp, out, read=0, write=0, duration=None):
    return {"input_tokens": inp, "output_tokens": out, "cache_read_tokens": read,
            "cache_creation_tokens": write, "total_time_ms": duration}


def response(key, start, end, inp, cached, write, out, model, agent):
    return {"key": key, "source": "devin", "agentId": agent, "startMs": start, "endMs": end, "firstMs": None,
            "input": inp, "cachedInput": cached, "cacheWriteInput": write, "cacheWrite1hInput": 0,
            "output": out, "reasoning": 0, "model": model, "provider": None, "fast": False, "final": True}


def event(notification):
    return json.dumps({"notification": notification}, separators=(",", ":"))


def write_fixtures(root):
    base = os.path.join(root, "devin")
    cli = os.path.join(base, "cli", "sessions.db")
    desktop = os.path.join(base, "User", "acp-events")
    os.makedirs(os.path.dirname(cli), exist_ok=True)
    os.makedirs(desktop, exist_ok=True)
    if os.path.exists(cli):
        os.remove(cli)
    db = sqlite3.connect(cli)
    db.executescript("""
        CREATE TABLE sessions(id TEXT PRIMARY KEY, working_directory TEXT NOT NULL, model TEXT NOT NULL,
            title TEXT, created_at INTEGER NOT NULL, last_activity_at INTEGER NOT NULL);
        CREATE TABLE message_nodes(row_id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL,
            node_id INTEGER NOT NULL, parent_node_id INTEGER, chat_message TEXT NOT NULL,
            created_at INTEGER NOT NULL, metadata TEXT);
    """)
    sessions = [
        ("orbit-main", "/Users/dev/projects/orbit", "adaptive", "Mirror task"),
        ("orbit-fallback", "C:\\Users\\dev\\projects\\fallback", "claude-sonnet-4-5", "Fallback"),
        ("orbit-routing", "/Users/dev/projects/routing", "adaptive", "Routing"),
        ("orbit-metadata", "/Users/dev/projects/metadata-only", "claude-sonnet-4-5", "Metadata only"),
    ]
    for sid, cwd, model, title in sessions:
        db.execute("INSERT INTO sessions VALUES (?,?,?,?,?,?)", (sid, cwd, model, title, 1793500000, 1793501000))
    rows = [
        ("orbit-main", 1, None, chat("user"), ms("10:00:00.000") // 1000),
        ("orbit-main", 2, 1, chat("assistant", "req-1", "swe-2-max", metrics(100, 40, 20, 5, 2000), "tool_calls"), ms("10:00:02.000") // 1000),
        ("orbit-main", 3, 2, chat("assistant", "req-1", "swe-2-max", metrics(100, 40, 20, 5, 2000), "tool_calls"), ms("10:00:02.000") // 1000),
        ("orbit-main", 4, 3, chat("user"), ms("10:00:03.000") // 1000),
        ("orbit-main", 5, 4, chat("assistant", "req-2", "swe-2-max", metrics(50, 10, 0, 0, 1000), "stop"), ms("10:00:04.000") // 1000),
        ("orbit-main", 6, 5, "{not json", ms("10:00:05.000") // 1000),
        ("orbit-fallback", 1, None, chat("assistant", None, None, metrics(12.9, 7, 3, 2, 500), "stop"), ms("10:01:00.000") // 1000),
        ("orbit-routing", 1, None, chat("assistant", "req-routing", None, metrics(9, 4, 1, 0, -1), "stop"), ms("10:02:00.000") // 1000),
    ]
    for node, (sid, node_id, parent, message, created) in enumerate(rows, 1):
        db.execute("INSERT INTO message_nodes(row_id,session_id,node_id,parent_node_id,chat_message,created_at,metadata) VALUES (?,?,?,?,?,?,NULL)",
                   (node, sid, node_id, parent, message, created))
    db.commit()
    db.close()

    def ndjson(name, lines):
        with open(os.path.join(desktop, name), "w", encoding="utf-8", newline="\n") as fh:
            fh.write("\n".join(lines) + "\n")

    ndjson("desktop-only.ndjson", [
        event({"sessionUpdate": "session_info_update", "title": "Desktop only"}),
        event({"sessionUpdate": "usage_update", "_meta": {"cognition.ai/inputTokens": 100,
               "cognition.ai/outputTokens": 5, "cognition.ai/cachedReadTokens": 20},
               "metadata": {"created_at": iso(ms("10:10:00.000"))}}),
        event({"sessionUpdate": "usage_update", "_meta": {"cognition.ai/inputTokens": 150,
               "cognition.ai/outputTokens": 8, "cognition.ai/cachedReadTokens": 30,
               "cognition.ai/cachedWriteTokens": 2}, "created_at": iso(ms("10:10:02.000"))}),
        "not json",
    ])
    ndjson("desktop-mirror.ndjson", [
        event({"sessionUpdate": "session_info_update", "title": "Mirror task"}),
        event({"sessionUpdate": "usage_update", "_meta": {"cognition.ai/inputTokens": 999,
               "cognition.ai/outputTokens": 999}}),
    ])
    ndjson("desktop-legacy.ndjson", [
        event({"metadata": {"input_tokens": 30, "output_tokens": 12, "cache_read_tokens": 4,
               "cache_creation_tokens": 1, "total_time_ms": 1500, "generation_model": "swe-2-max",
               "created_at": iso(ms("10:11:00.000"))}}),
        event({"metadata": {"input_tokens": 0, "output_tokens": 0}}),
    ])
    ndjson("desktop-empty.ndjson", [event({"sessionUpdate": "agent_message_chunk", "content": {"text": "not read"}})])
    ndjson("desktop-metadata.ndjson", [
        event({"sessionUpdate": "session_info_update", "title": "Metadata only"}),
        event({"metadata": {"input_tokens": 30, "output_tokens": 12, "cache_read_tokens": 4,
               "cache_creation_tokens": 1, "total_time_ms": 1500,
               "created_at": iso(ms("10:12:00.000"))}}),
    ])
    for path in (os.path.join(base, "desktop", "acp-messages"),):
        os.makedirs(path, exist_ok=True)
        with open(os.path.join(path, "ignored.ndjson"), "w", encoding="utf-8") as fh:
            fh.write(event({"metadata": {"input_tokens": 999, "output_tokens": 999}}) + "\n")

    main_agent = "devin/cli/sessions.db#orbit-main"
    fallback_agent = "devin/cli/sessions.db#orbit-fallback"
    routing_agent = "devin/cli/sessions.db#orbit-routing"
    only_agent = "devin/User/acp-events/desktop-only.ndjson"
    legacy_agent = "devin/User/acp-events/desktop-legacy.ndjson"
    metadata_agent = "devin/User/acp-events/desktop-metadata.ndjson"
    responses = [
        response("devin:orbit-main:request:req-1", ms("10:00:00.000"), ms("10:00:02.000"), 125, 20, 5, 40, "swe-2-max", main_agent),
        response("devin:orbit-main:request:req-2", ms("10:00:03.000"), ms("10:00:04.000"), 50, 0, 0, 10, "swe-2-max", main_agent),
        response("devin:orbit-fallback:row:7", ms("10:00:59.500"), ms("10:01:00.000"), 17, 3, 2, 7, "claude-sonnet-4-5", fallback_agent),
        response("devin:orbit-routing:request:req-routing", None, ms("10:02:00.000"), 10, 1, 0, 4, None, routing_agent),
        response("devin-desktop:desktop-legacy:line:1", ms("10:10:58.500"), ms("10:11:00.000"), 35, 4, 1, 12, "swe-2-max", legacy_agent),
        response("devin-desktop:desktop-metadata:line:2", ms("10:11:58.500"), ms("10:12:00.000"), 35, 4, 1, 12, "claude-sonnet-4-5", metadata_agent),
        response("devin-desktop:desktop-only:usage", None, ms("10:10:02.000"), 152, 30, 2, 13, None, only_agent),
    ]
    agents = {
        main_agent: {"project": "orbit", "model": "swe-2-max", "phase": "idle", "lastLineMs": ms("10:00:05.000"), "malformedLines": 1},
        fallback_agent: {"project": "fallback", "model": "claude-sonnet-4-5", "phase": "idle", "lastLineMs": ms("10:01:00.000"), "malformedLines": 0},
        routing_agent: {"project": "routing", "model": None, "phase": "idle", "lastLineMs": ms("10:02:00.000"), "malformedLines": 0},
        only_agent: {"project": None, "model": None, "phase": "idle", "lastLineMs": ms("10:10:02.000"), "malformedLines": 1},
        legacy_agent: {"project": None, "model": "swe-2-max", "phase": "idle", "lastLineMs": ms("10:11:00.000"), "malformedLines": 0},
        metadata_agent: {"project": "metadata-only", "model": "claude-sonnet-4-5", "phase": "idle", "lastLineMs": ms("10:12:00.000"), "malformedLines": 0},
    }
    return {"_comment": "Devin CLI sessions.db plus Desktop acp-events. Paths in agentId are relative to the devin fixture folder. Responses are in row/event order; duplicate CLI request rows are one response and the uniquely titled Desktop mirror is suppressed.",
            "responses": responses, "agents": agents, "desktopFiles": ["desktop-empty.ndjson", "desktop-legacy.ndjson", "desktop-metadata.ndjson", "desktop-only.ndjson"]}
