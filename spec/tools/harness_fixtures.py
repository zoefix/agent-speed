#!/usr/bin/env python3
"""Synthetic native-format harness fixtures; expected values are independent of the readers."""
import json
import sqlite3
import sys
import tempfile
from pathlib import Path

BASE = 1792497600000  # 2026-10-20T12:00:00Z


def response(key, start, end, inp, out, cached=0, write=0, reasoning=0, model="glm-5.3", provider="zai"):
    return dict(key=key, startMs=BASE + start, endMs=BASE + end, firstMs=None, input=inp, cachedInput=cached,
                cacheWriteInput=write, cacheWrite1hInput=0, output=out, reasoning=reasoning,
                model=model, provider=provider, final=True, fast=False, requestCount=1)


def attributed(value, evidence, provider=None):
    return dict(dict(mode=value, evidence=evidence), **(dict(provider=provider) if provider else {}))


def mode_block(responses, effort=None, speed=None, modes=None):
    """`modes`: (effort, speed) per response key that has one."""
    return dict(effort=effort, speed=speed, responseModes={
        row["key"]: dict(zip(("effort", "speed"), (modes or {}).get(row["key"], (None, None)))) for row in responses})


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def write_lines(path, rows):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n" for row in rows),
                    encoding="utf-8")


ZCODE_TABLES = """
    DROP TABLE IF EXISTS model_usage;
    DROP TABLE IF EXISTS message;
    DROP TABLE IF EXISTS part;
    DROP TABLE IF EXISTS session;
    CREATE TABLE session (
        id TEXT PRIMARY KEY, project_id TEXT NOT NULL, parent_id TEXT, slug TEXT NOT NULL,
        directory TEXT NOT NULL, title TEXT NOT NULL, version TEXT NOT NULL,
        time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL,
        task_type TEXT NOT NULL DEFAULT 'interactive', time_deleted INTEGER, last_outcome TEXT);
    CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, time_created INTEGER NOT NULL,
        time_updated INTEGER NOT NULL, data TEXT NOT NULL, sequence INTEGER);
    CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT NOT NULL, session_id TEXT NOT NULL,
        time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL, data TEXT NOT NULL, sequence INTEGER);
    CREATE TABLE model_usage (
        id TEXT PRIMARY KEY, logical_request_id TEXT NOT NULL, attempt_index INTEGER NOT NULL DEFAULT 0,
        session_id TEXT NOT NULL, turn_id TEXT, %s query_source TEXT NOT NULL, provider_id TEXT NOT NULL,
        model_id TEXT NOT NULL, %s status TEXT NOT NULL, started_at INTEGER NOT NULL, completed_at INTEGER,
        duration_ms INTEGER, input_tokens INTEGER NOT NULL DEFAULT 0, output_tokens INTEGER NOT NULL DEFAULT 0,
        reasoning_tokens INTEGER NOT NULL DEFAULT 0, cache_creation_input_tokens INTEGER NOT NULL DEFAULT 0,
        cache_read_input_tokens INTEGER NOT NULL DEFAULT 0, raw_usage_json TEXT);
"""


def save_database(database, path):
    path.parent.mkdir(parents=True, exist_ok=True)
    database.commit()
    database.execute("VACUUM")
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix="fixture-", suffix=".sqlite", delete=False) as handle:
        temporary = Path(handle.name)
    with sqlite3.connect(str(temporary)) as target:
        database.backup(target)
    temporary.replace(path)
    database.close()


def zcode_session(database, sid, parent):
    database.execute("INSERT INTO session (id,project_id,parent_id,slug,directory,title,version,time_created,time_updated) "
                     "VALUES (?,?,?,?,?,?,?,?,?)", (sid, "project-quasar", parent, sid, "/projects/quasar",
                                                     "Synthetic fixture", "3.16.2", BASE, BASE + 21000))


def zcode(root):
    """The usage table of CLI 0.15+ (`variant` holds the reasoning level) with its links to the user message."""
    relative = "zcode/cli/db/db.sqlite"
    database = sqlite3.connect(":memory:")
    database.executescript(ZCODE_TABLES % ("parent_user_message_id TEXT,", "variant TEXT,"))
    for sid, parent in [("sess-z-main", None), ("sess-z-sub", "sess-z-main"), ("sess-z-running", None)]:
        zcode_session(database, sid, parent)
    card = dict(schemaVersion=1, cardId="hsc_live_1", taskId="task-1", provider="zai", model="glm-5.3")
    users = [("z-u1", card), ("z-u2", dict(card, fallbackAt=BASE + 19000, fallbackReason="highspeed_card_expired")),
             ("z-u3", dict(card, cardId="hsc_mock_1"))]
    for mid, high in users:
        database.execute("INSERT INTO message (id,session_id,time_created,time_updated,data) VALUES (?,?,?,?,?)",
                         (mid, "sess-z-main", BASE, BASE, json.dumps(dict(role="user", metadata=dict(highspeed=high)))))
    high_card = "account:zai-highspeed-card"
    calls = [
        ("z-r1", "sess-z-main", 10000, 14000, 1200, 200, 500, 100, 50, "main_turn", "high", "zai", "glm-5.3", None),
        ("z-title", "sess-z-main", 15000, 16000, 80, 30, 0, 0, 0, "session_title", "disabled", "zai", "glm-5.3", None),
        ("z-r2", "sess-z-main", 17000, 18500, 2000, 150, 1000, 0, 0, "main_turn", "disabled", high_card, "glm-5.3", "z-u1"),
        ("z-r4", "sess-z-main", 19000, 19500, 2100, 160, 1000, 0, 0, "main_turn", "enabled", "zai", "glm-5.3", "z-u2"),
        ("z-r5", "sess-z-main", 20000, 20400, 2200, 170, 1000, 0, 0, "main_turn", "medium", high_card, "glm-5.3", "z-u3"),
        ("z-compact", "sess-z-main", 20500, 20900, 300, 40, 0, 0, 0, "compact", "low", "zai", "glm-5.3", None),
        ("z-r3", "sess-z-sub", 12000, 15000, 400, 90, 100, 20, 10, "main_turn", "xhigh", "zai", "GLM-5.1-Highspeed", None),
    ]
    expected = {}
    for rid, sid, start, end, inp, out, cached, write, reasoning, source, variant, provider, model, user in calls:
        database.execute("INSERT INTO model_usage (id,logical_request_id,session_id,parent_user_message_id,query_source,"
                         "provider_id,model_id,variant,status,started_at,completed_at,duration_ms,input_tokens,"
                         "output_tokens,reasoning_tokens,cache_creation_input_tokens,cache_read_input_tokens) "
                         "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                         (rid, rid, sid, user, source, provider, model, variant, "completed", BASE + start, BASE + end,
                          end - start, inp, out, reasoning, write, cached))
        expected.setdefault(sid, []).append(response("zcode:" + rid, start, end, inp, out, cached, write, reasoning,
                                                     model=model, provider=provider))
    database.execute("INSERT INTO model_usage (id,logical_request_id,session_id,query_source,provider_id,model_id,"
                     "variant,status,started_at) VALUES ('z-running','z-running','sess-z-running','main_turn','zai',"
                     "'glm-5.3','max','running',?)", (BASE + 22000,))
    save_database(database, root / relative)
    recorded = lambda value: attributed(value, "recorded")  # noqa: E731
    card_speed = attributed("highspeed", "recorded", "zcode")
    mock_speed = attributed("highspeed", "selected", "zcode")
    main = mode_block(expected["sess-z-main"], recorded("medium"), mock_speed, {
        "zcode:z-r1": (recorded("high"), None),
        "zcode:z-title": (recorded("off"), None),
        "zcode:z-r2": (recorded("off"), card_speed),
        "zcode:z-r4": (recorded("on"), attributed("standard", "recorded", "zcode")),
        "zcode:z-r5": (recorded("medium"), mock_speed),
        "zcode:z-compact": (recorded("low"), None)})
    sub = mode_block(expected["sess-z-sub"], recorded("xhigh"), attributed("highspeed", "selected", "zcode"), {
        "zcode:z-r3": (recorded("xhigh"), attributed("highspeed", "selected", "zcode"))})
    return dict(database=relative, sessions=[
        dict(id="sess-z-main", project="quasar", parentId=None, responses=expected["sess-z-main"], **main),
        dict(id="sess-z-sub", project="quasar", parentId="sess-z-main", responses=expected["sess-z-sub"], **sub),
        dict(id="sess-z-running", project="quasar", parentId=None, responses=[], effort=recorded("max"), speed=None,
             responseModes={})])


def zcode_without_variant(root):
    """A database from before the columns: the reader tolerates their absence and every value stays unknown."""
    relative = "zcode-old/cli/db/db.sqlite"
    database = sqlite3.connect(":memory:")
    database.executescript(ZCODE_TABLES % ("", ""))
    zcode_session(database, "sess-z-old", None)
    database.execute("INSERT INTO model_usage (id,logical_request_id,session_id,query_source,provider_id,model_id,"
                     "status,started_at,completed_at,duration_ms,input_tokens,output_tokens) "
                     "VALUES ('z-old','z-old','sess-z-old','main_turn','zai','glm-5.3','completed',?,?,1000,100,20)",
                     (BASE + 1000, BASE + 2000))
    save_database(database, root / relative)
    rows = [response("zcode:z-old", 1000, 2000, 100, 20)]
    return dict(database=relative, sessions=[dict(id="sess-z-old", project="quasar", parentId=None, responses=rows,
                                                  **mode_block(rows))])


def kimi(root):
    folder = "kimi/sessions/wd_nova_0123456789ab/sess-kimi"
    main = folder + "/agents/main/wire.jsonl"
    child = folder + "/agents/agent-0/wire.jsonl"
    def event(record_type, at, agent="main", **fields):
        return dict(type=record_type, time=BASE + at, agentId=agent, **fields)
    def usage(other, out, read=0, write=0):
        return dict(inputOther=other, output=out, inputCacheRead=read, inputCacheCreation=write)
    first, second, third = usage(100, 80, 20, 10), usage(200, 120, 80, 20), usage(50, 60, 10, 0)
    rows = [dict(type="metadata", protocol_version="1.5", created_at=BASE),
            event("profile.bind", 0, thinkingEffort="medium", systemPrompt="(never read)"),
            event("config.update", 1, environmentDisclosure=dict(cwd="/projects/nova")),
            event("turn.prompt", 29000, turnId=1),
            event("llm.request", 30000, provider="moonshot", model="kimi-k2.5", kind="loop", thinkingEffort="high",
                  thinkingKeep="all"),
            event("usage.record", 34000, model="kimi-k2.5", usage=first),
            event("context.append_message", 34000, message=dict(role="assistant", id="k-m1", content=[], toolCalls=[],
                  usage=first, llmTiming=dict(llmFirstTokenLatencyMs=500, llmStreamDurationMs=3500))),
            event("context.append_loop_event", 34000, event=dict(type="step.end", uuid="step-k1", messageId="k-m1",
                  turnId="1", step=1, finishReason="completed", usage=first,
                  llmFirstTokenLatencyMs=500, llmStreamDurationMs=3500)),
            event("llm.request", 40000, provider="moonshot", model="kimi-for-coding-highspeed", kind="loop",
                  thinkingEffort="on"),
            event("context.append_loop_event", 40000, event=dict(type="step.begin", uuid="step-k2", turnId="1", step=2)),
            event("context.append_loop_event", 43200, event=dict(type="step.end", uuid="step-k2", messageId="k-m2",
                  turnId="1", step=2, finishReason="tool_calls", usage=second,
                  llmFirstTokenLatencyMs=700, llmStreamDurationMs=2500)),
            event("context.append_message", 43200, message=dict(role="assistant", id="k-m2", content=[],
                  toolCalls=[dict(id="tool-k", name="Read", type="function", arguments="{}")], usage=second)),
            event("llm.request", 50000, provider="moonshot", model="kimi-k2.5", kind="loop"),
            event("human.agent.message.appended", 52000, message=dict(message=dict(role="assistant", content=[], toolCalls=[]),
                  meta=dict(messageId="k-m3", usage=third, model=dict(provider="moonshot", model="kimi-k2.5")))),
            event("subagent.completed", 52500, subagentId="agent-0", resultSummary="Synthetic result", usage=usage(5000, 5000)),
            event("turn.ended", 53000, turnId=1, reason="completed"),
            event("config.update", 53500, thinkingLevel="low")]
    write_lines(root / main, rows)
    child_usage = usage(70, 45)
    write_lines(root / child, [dict(type="metadata", protocol_version="1.5", created_at=BASE + 31000),
        event("config.update", 31001, "agent-0", environmentDisclosure=dict(cwd="/projects/nova")),
        event("llm.request", 32000, "agent-0", provider="moonshot", model="kimi-k2.5", kind="loop", thinkingEffort="off"),
        event("context.append_loop_event", 35000, "agent-0", event=dict(type="step.end", uuid="step-child",
              messageId="k-child", turnId="1", step=1, finishReason="completed", usage=child_usage,
              llmFirstTokenLatencyMs=400, llmStreamDurationMs=2600)),
        event("turn.ended", 35001, "agent-0", turnId=1, reason="completed")])
    copy = "kimi/sessions/wd_nova_0123456789ab/sess-kimi-copy/agents/main/wire.jsonl"
    write_lines(root / copy, rows)
    # A journal from before 0.23.1 has no `llm.request`: the selection is all there is, and it reaches its responses.
    old_wire = "kimi/sessions/wd_nova_0123456789ab/sess-kimi-old/agents/main/wire.jsonl"
    old_usage = usage(40, 30)
    write_lines(root / old_wire, [dict(type="metadata", protocol_version="1.4", created_at=BASE + 60000),
        event("config.update", 60001, environmentDisclosure=dict(cwd="/projects/nova"), thinkingLevel="xhigh"),
        event("context.append_loop_event", 62000, event=dict(type="step.end", uuid="step-old", messageId="k-old",
              turnId="1", step=1, finishReason="completed", usage=old_usage,
              llmFirstTokenLatencyMs=300, llmStreamDurationMs=1700))])
    common = dict(model="kimi-k2.5", provider="moonshot")
    main_rows = [response("kimi:k-m1", 30000, 34000, 130, 80, 20, 10, **common),
                 response("kimi:k-m2", 40000, 43200, 300, 120, 80, 20, model="kimi-for-coding-highspeed", provider="moonshot"),
                 response("kimi:k-m3", 50000, 52000, 60, 60, 10, **common)]
    child_rows = [response("kimi:k-child", 32000, 35000, 70, 45, **common)]
    old_rows = [response("kimi:k-old", 60000, 62000, 40, 30, model=None, provider=None)]
    recorded = lambda value: attributed(value, "recorded")  # noqa: E731
    selected = lambda value: attributed(value, "selected")  # noqa: E731
    highspeed = attributed("highspeed", "selected", "kimi")
    return dict(root="kimi/sessions", files=[
        dict(path=main, project="nova", isSubagent=False, parentFile=None, responses=main_rows,
             **mode_block(main_rows, selected("low"), None, {
                 "kimi:k-m1": (recorded("high"), None), "kimi:k-m2": (recorded("on"), highspeed)})),
        dict(path=child, project="nova", isSubagent=True, parentFile=main, responses=child_rows,
             **mode_block(child_rows, recorded("off"), None, {"kimi:k-child": (recorded("off"), None)})),
        dict(path=copy, project="nova", isSubagent=False, parentFile=None, responses=[],
             **mode_block([], selected("low"))),
        dict(path=old_wire, project="nova", isSubagent=False, parentFile=None, responses=old_rows,
             **mode_block(old_rows, selected("xhigh"), None, {"kimi:k-old": (selected("xhigh"), None)}))])


def grok(root):
    folder = "grok/sessions/%2Fprojects%2Fcomet"
    main = folder + "/g-main/updates.jsonl"
    child = folder + "/g-child/updates.jsonl"
    copy = folder + "/g-copy/updates.jsonl"
    def update(kind, at, sid="g-main", method="_x.ai/session/update", **fields):
        return dict(timestamp=(BASE + at) // 1000, method=method, params=dict(sessionId=sid,
                    update=dict(sessionUpdate=kind, **fields), _meta=dict(eventId=sid + ":" + str(at),
                                                                       agentTimestampMs=BASE + at)))
    def bucket(inp, out, cached, reasoning, calls, duration):
        return dict(inputTokens=inp, outputTokens=out, cachedReadTokens=cached, cacheCreationTokens=0,
                    reasoningTokens=reasoning, totalTokens=inp + out, modelCalls=calls, apiDurationMs=duration)
    first = bucket(1500, 300, 400, 30, 2, 6000)
    a, b = bucket(1200, 250, 300, 25, 1, 3000), bucket(800, 150, 0, 15, 1, 2000)
    second = bucket(2000, 400, 300, 40, 2, 5000)
    rows = [update("user_message_chunk", 60000, method="session/update", content=dict(type="text", text="Synthetic prompt")),
        update("subagent_spawned", 60500, subagent_id="g-child", child_session_id="g-child",
               parent_session_id="g-main", parent_prompt_id="g-p1", subagent_type="general-purpose", description="Synthetic task"),
        update("subagent_finished", 63500, subagent_id="g-child", child_session_id="g-child", status="completed",
               tool_calls=0, turns=1, duration_ms=2500, tokens_used=600, will_wake=False),
        update("turn_completed", 65000, prompt_id="g-p1", stop_reason="end_turn", elapsed_ms=5000,
               usage=dict(first, modelUsage={"grok-4.5": first}, numTurns=1)),
        update("user_message_chunk", 70000, method="session/update", content=dict(type="text", text="Synthetic continuation")),
        update("turn_completed", 75000, prompt_id="g-p2", stop_reason="end_turn", elapsed_ms=5000,
               usage=dict(second, modelUsage={"grok-4.5": a, "grok-4.6": b}, numTurns=2))]
    write_lines(root / main, rows)
    sub = bucket(500, 100, 100, 10, 1, 2000)
    write_lines(root / child, [update("user_message_chunk", 61000, "g-child", method="session/update",
                                     content=dict(type="text", text="Synthetic child task")),
        update("turn_completed", 63000, "g-child", prompt_id="g-c1", stop_reason="end_turn", elapsed_ms=2000,
               usage=dict(sub, modelUsage={"grok-4.5": sub}, numTurns=1))])
    write_lines(root / copy, rows)
    for sid, parent, kind, effort in [("g-main", None, "headless", "high"), ("g-child", "g-main", "subagent", "none"),
                                       ("g-copy", "g-main", "fork", None)]:
        summary = dict(info=dict(id=sid, cwd="/projects/comet"), current_model_id="grok-4.5", parent_session_id=parent,
                       session_kind=kind, created_at="2026-10-20T12:00:00Z", updated_at="2026-10-20T12:01:15Z")
        if effort:
            summary["reasoning_effort"] = effort
        write_json(root / folder / sid / "summary.json", summary)
        # The rebuildable conversation cache is never read, whatever it holds.
        write_lines(root / folder / sid / "chat_history.jsonl", [dict(type="assistant", content="(never read)",
                                                                      model_id="grok-4.5", reasoning_effort="max")])
        # The companion cumulative store must never be added to the turn records.
        write_json(root / folder / sid / "usage.json", dict(sessionId=sid, session=dict(inputTokens=999999,
                   outputTokens=999999, modelCalls=999), turns=[]))
    expected_first = response("grok:g-p1:grok-4.5", 59000, 65000, 1500, 300, 400, reasoning=30,
                              model="grok-4.5", provider=None)
    expected_first["requestCount"] = 2
    main_rows = [expected_first,
                 response("grok:g-p2:grok-4.5", 72000, 75000, 1200, 250, 300, reasoning=25, model="grok-4.5", provider=None),
                 response("grok:g-p2:grok-4.6", 73000, 75000, 800, 150, reasoning=15, model="grok-4.6", provider=None)]
    return dict(root="grok/sessions", files=[
        dict(path=main, project="comet", isSubagent=False, parentFile=None, responses=main_rows,
             **mode_block(main_rows, attributed("high", "selected"))),
        dict(path=child, project="comet", isSubagent=True, parentFile=main, responses=[],
             **mode_block([], attributed("off", "selected"))),
        dict(path=copy, project="comet", isSubagent=False, parentFile=main, responses=[], **mode_block([]))])


def generate(root):
    root = Path(root)
    expected = dict(version=1, zcode=zcode(root), zcodeWithoutVariant=zcode_without_variant(root), kimi=kimi(root),
                    grok=grok(root))
    write_json(root / "harness-expected.json", expected)
    return expected


if __name__ == "__main__":
    generate(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "fixtures")
