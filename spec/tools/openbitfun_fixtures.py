#!/usr/bin/env python3
"""Synthetic usage records, rounds, request traces and hand-calculated expectations for METRICS 4.21."""
import json
from datetime import datetime, timezone
from pathlib import Path

BASE = 1792497600000


def generate(root):
    def write(path, value):
        destination = root / path
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")

    def stamp(offset):
        return datetime.fromtimestamp((BASE + offset) / 1000, timezone.utc).isoformat(timespec="milliseconds")

    def trace(key, session, start, end, duration, inp, out, cached=0, write_tokens=0, reasoning=0):
        return dict(version=1, trace_id=key, session_id=session, capture_mode="usage_only",
                    recorded_at=stamp(start), request=dict(model_id="gpt-5", provider="openai", attempt_number=1),
                    timing=dict(started_at=stamp(start), finished_at=stamp(end), duration_ms=duration),
                    response=dict(status="completed", usage=dict(promptTokenCount=inp, candidatesTokenCount=out,
                        cachedContentTokenCount=cached, cacheCreationTokenCount=write_tokens, reasoningTokenCount=reasoning)))

    def expected(key, start, end, inp, out, cached=0, write_tokens=0, reasoning=0):
        return dict(key="openbitfun:" + key, startMs=None if start is None else BASE + start, endMs=BASE + end,
                    firstMs=None, input=inp, output=out, cachedInput=cached, cacheWriteInput=write_tokens,
                    cacheWrite1hInput=0, reasoning=reasoning, model="gpt-5", provider="openai", final=True,
                    fast=False, requestCount=1)

    main = "openbitfun/projects/demo/sessions/main/metadata.json"
    child = "openbitfun/projects/demo/sessions/child/metadata.json"
    mirror = "openbitfun/projects/copy/sessions/main/metadata.json"
    metadata = dict(sessionId="main", sessionKind="primary", workspacePath="/work/fixture-project", createdAt=BASE)
    write(main, metadata)
    write(mirror, metadata)
    write(child, dict(session_id="child", session_kind="subagent", workspace_path="/work/fixture-project",
                      created_at=BASE + 6000, relationship=dict(kind="subagent", parent_session_id="main")))
    first = trace("request-a", "main", 0, 2100, 2000, 200, 100, 50, 20, 30)
    partial = trace("request-b", "main", 3000, 4000, 1000, 80, 40)
    partial["response"]["status"] = "error"
    missing = trace("no-usage", "main", 4500, 5000, 500, 0, 0)
    missing["response"].pop("usage")
    pending = trace("pending", "main", 8000, 8000, 0, 0, 0)
    pending.pop("response")
    pending["timing"] = dict(started_at=stamp(8000))
    legacy = trace("legacy", "main", 0, 2000, 2000, 500, 500)
    legacy.pop("timing")
    long = trace("long", "main", 0, 10000, 1_000_000, 100, 30)
    records = [first, partial, first, missing, pending, legacy, long,
               trace("foreign", "other-session", 0, 2000, 2000, 900, 900)]
    for index, record in enumerate(records, 1):
        write(f"openbitfun/projects/demo/request-traces/main/request-{index:06d}.json", record)
    write("openbitfun/projects/copy/sessions/main/request-traces/request-000001.json", first)
    write("openbitfun/projects/demo/sessions/child/request-traces/request-000001.json",
          trace("child-a", "child", 6000, 7000, 1000, 100, 70, 20, reasoning=15))
    completed = trace("pending", "main", 8000, 12000, 4000, 120, 80)
    write("openbitfun/completed-request.json", completed)

    # One round has two distinct attempts and a revised copy of its first attempt.
    ledger = "openbitfun/ledger"
    session = "openbitfun/ledger-project/sessions/session/metadata.json"
    write(session, dict(sessionId="session"))
    write("openbitfun/ledger-project/sessions/session/request-traces/request-000001.json",
          trace("fallback", "session", 1000, 2000, 1000, 10, 25))
    write("openbitfun/ledger-project/sessions/session/request-traces/request-000002.json", dict(
        version=1, session_id="session", trace_id="unfinished", turn_id="turn", operation_kind="model_round",
        operation_id="pending", timing=dict(started_at=stamp(5000))))
    write("openbitfun/ledger-project/sessions/session/turns/turn-000001.json", dict(
        sessionId="session", turnId="turn", status="inprogress", modelRounds=[
            dict(id="finished", status="completed", startTime=BASE + 1000, endTime=BASE + 4000,
                 durationMs=2000, attemptCount=2),
            dict(id="pending", status="streaming", startTime=BASE + 5000, attemptCount=1)]))

    def usage(round_id, attempt, inp, out, session_id="session"):
        return dict(session_id=session_id, turn_id="turn", timestamp=stamp(4000), effective_model_name="gpt-5",
                    input_tokens=inp, output_tokens=out, cached_tokens=2, cache_write_tokens=1,
                    token_details=dict(reasoningTokenCount=3, openbitfunModelRequest=dict(
                        version=1, roundId=round_id, attemptId=attempt, usageKnown=True, channel=dict(protocol="openai"))))

    write(ledger + "/2026-10-20.json", dict(records=[
        usage("finished", "a", 8, 15), usage("finished", "b", 30, 175), usage("finished", "a", 10, 25), usage("pending", "c", 4, 50),
        usage("finished", "a", 100, 900, session_id="other")]))
    round_response = expected("round:session:turn:finished", 2000, 4000, 40, 200, 4, 2, 6)
    round_response["requestCount"] = 2
    write("openbitfun-expected.json", dict(todayStartMs=1792454400000, nowMs=BASE + 13000, files=[
        dict(path=main, project="fixture-project", parent=None, isSubagent=False, phase="generating", responses=[
            expected("request-a", 100, 2100, 200, 100, 50, 20, 30), expected("request-b", 3000, 4000, 80, 40),
            expected("long", None, 10000, 100, 30)]),
        dict(path=child, project="fixture-project", parent="main", isSubagent=True, phase="idle", responses=[
            expected("child-a", 6000, 7000, 100, 70, 20, reasoning=15)]),
        dict(path=mirror, project="fixture-project", parent=None, isSubagent=False, phase="idle", responses=[]),
    ], totals=dict(requests=4, input=480, output=240, measuredOutput=210, measuredMs=4000, average=52.5),
       ledger=dict(root=ledger, metadata=session, phase="generating", responses=[round_response])))


if __name__ == "__main__":
    generate(Path(__file__).resolve().parent.parent / "fixtures")
