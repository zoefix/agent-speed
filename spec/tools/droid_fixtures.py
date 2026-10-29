#!/usr/bin/env python3
"""Factory Droid session fixtures (METRICS 4.16), used by generate_fixtures.py.

  python3 spec/tools/droid_fixtures.py OUT_DIR

OUT_DIR/droid plays the home folder: droid/.factory/sessions is the sessions root, with one folder per working
directory (macOS `-Users-dev-projects-forge`, `-Users-dev-projects-quasar`, Windows `-C-Users-dev-projects-halo`)
and one legacy session directly in the root. Every session is hand-written and synthetic: a main session over two UTC
days with a compaction and a model switch, its Task-tool worker, a session of the user's own model on Windows (CRLF
lines, a sidecar with a byte order mark) and its `/handoff` successor, an `auto` router session and its fork, and
sessions without a sidecar, without counters, with counters but no call, and with a malformed sidecar. Times are UTC.

Token shares are computed here from the bytes written, by this file's own largest-remainder split (METRICS 4.16
Allocation), independently of reference.py; a share that a single call takes whole is the sidecar's value itself.
The returned block has the parser-expected.json shape: `order`, `mtimes` (sidecars included, each 500 ms before its
transcript, so startup order follows the transcripts), `files` and `traces` for the shared per-file checks, and the
`droid` block: `discover`, `roots`, `sidecars`, `allocations` and the scripted `live` read cycles.
"""
import json
import os
import sys

from omp_fixtures import NEXT_DAY, iso, ms

ROOT = "droid/.factory/sessions"
FORGE = ROOT + "/-Users-dev-projects-forge"
HALO = ROOT + "/-C-Users-dev-projects-halo"
QUASAR = ROOT + "/-Users-dev-projects-quasar"

A = "a1f0c3d2-0b6e-4c1a-9d7e-2f3a4b5c6d70"  # forge: main session over two days
B = "b2e1d4c3-1c7f-4d2b-8e8f-3a4b5c6d7e81"  # forge: A's Task-tool worker
C = "c3d2e5f4-2d80-4e3c-9f90-4b5c6d7e8f92"  # halo (Windows): the user's own model
F = "f6a5b8c7-5ab3-4f6f-8c23-7e8f9a0b1c25"  # halo: C's /handoff successor
D = "d4e3f6a5-3e91-4f4d-8a01-5c6d7e8f9a03"  # quasar: Factory's router (`auto`)
E = "e5f4a7b6-4fa2-4a5e-9b12-6d7e8f9a0b14"  # quasar: a fork of D
G = "a7b6c9d8-6bc4-4a70-9d34-8f9a0b1c2d36"  # forge: no sidecar
H = "b8c7dae9-7cd5-4b81-8e45-9a0b1c2d3e47"  # forge: a sidecar without counters (droid before 0.123)
I = "c9d8ebfa-8de6-4c92-9f56-0b1c2d3e4f58"  # quasar: counters but no call
J = "dae9fc0b-9ef7-4da3-8a67-1c2d3e4f5a69"  # root: legacy layout, malformed sidecar
FIELDS = ("inputTokens", "cacheCreationTokens", "cacheReadTokens", "outputTokens", "thinkingTokens")
OPUS, SONNET_DATED, GPT = "claude-opus-5-5", "claude-sonnet-4-5-20250929", "gpt-5.5"
BYOK = "custom:GPT-5.5-[OpenAI-BYOK]-0"  # the user's own model (BYOK), priced by its name (METRICS 9.2 custom)


# ---------------------------------------------------------------------------------------------------- lines

def text(value):
    return {"type": "text", "text": value}


def tool_use(call_id, name, arguments):
    return {"type": "tool_use", "id": call_id, "name": name, "input": arguments}


def tool_result(call_id, content, error=False):
    return {"type": "tool_result", "tool_use_id": call_id, "content": content, "is_error": error}


def start(sid, cwd, title, **extra):
    line = {"type": "session_start", "id": sid, "title": title, "owner": "dev", "version": 2, "cwd": cwd,
            "hostId": "host-7f3a"}
    line.update(extra)
    return line


def user(mid, at, content, visibility=None, parent=None):
    msg = {"role": "user", "content": content}
    if visibility:
        msg["visibility"] = visibility
    return {"type": "message", "id": mid, "timestamp": iso(at), "parentId": parent, "message": msg}


def context(turn, at):
    """The per-turn context droid writes before the prompt (`llm_only`, id `context-<turn>`)."""
    return user("context-" + turn, at, [text("<system-reminder>The date is today.</system-reminder>")], "llm_only")


def assistant(mid, at, content, model=None, effort=None):
    msg = {"role": "assistant", "content": content}
    if model is not None:
        msg["modelId"] = model
    if effort is not None:
        msg["reasoningEffort"] = effort
    line = {"type": "message", "timestamp": iso(at) if isinstance(at, int) else at, "parentId": None, "message": msg}
    if mid is not None:
        line["id"] = mid
    return line


def outcome(turn, reason="completed"):
    return {"type": "agent_turn_outcome", "turnId": turn, "reason": reason, "resultKind": "text"}


def compaction(cid, at, removed):
    return {"type": "compaction_state", "id": cid, "timestamp": iso(at), "summaryText": "USER: earlier work",
            "summaryTokens": 1800, "summaryKind": "auto", "removedCount": removed}


def counters(inp, cache_write, cache_read, out, thinking, credits=0):
    return {"inputTokens": inp, "outputTokens": out, "cacheCreationTokens": cache_write, "cacheReadTokens": cache_read,
            "thinkingTokens": thinking, "factoryCredits": credits}


def sidecar(model, usage, **extra):
    doc = {"model": model, "reasoningEffort": "high", "interactionMode": "auto", "autonomyLevel": "medium",
           "assistantActiveTimeMs": 61_000}
    if usage is not None:
        doc["tokenUsage"] = usage
        doc["inclusiveTokenUsage"] = usage
        doc["childInclusiveTokenUsageBySessionId"] = {}
        doc["lastCallTokenUsage"] = {"inputTokens": 10, "outputTokens": 5, "cacheReadTokens": 0}
    doc.update(extra)
    return doc


def encode(line):
    return line if isinstance(line, str) else json.dumps(line, ensure_ascii=False, separators=(",", ":"))


# ---------------------------------------------------------------------------------------------------- shares

def split(total, weights):
    """Largest remainder: floors first, then one unit each by remainder, ties to the earlier call."""
    whole = sum(weights)
    parts = [divmod(total * w, whole) for w in weights]
    shares = [q for q, _ in parts]
    ranked = sorted(((r, -i) for i, (_, r) in enumerate(parts)), reverse=True)
    for _, neg in ranked[:total - sum(shares)]:
        shares[-neg] += 1
    return shares


class Transcript:
    """The bytes of one transcript and the weights of its calls (wIn from the latest compaction, else the start)."""

    def __init__(self, lines, crlf=False):
        self.lines = lines
        self.ending = b"\r\n" if crlf else b"\n"
        self.blob, self.weights, offset, base = b"", {}, 0, 0
        for line in lines:
            raw = encode(line).encode("utf-8") + (b"\r" if crlf else b"")
            if isinstance(line, dict) and line.get("type") == "compaction_state":
                base = offset
            mid = line.get("id") if isinstance(line, dict) else None
            msg = line.get("message") if isinstance(line, dict) else None
            if isinstance(msg, dict) and msg.get("role") == "assistant" and mid:
                self.weights[mid] = (min(65_536 + offset - base, 8_388_608), min(len(raw), 8_388_608))
            self.blob += raw + b"\n"
            offset += len(raw) + 1

    def write(self, path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(self.blob)


def allocate(growth, calls, weights):
    """{call id: five shares} of `growth` over `calls` (METRICS 4.16 Allocation)."""
    out = {c: {} for c in calls}
    for f in FIELDS:
        column = [weights[c][0] if f in FIELDS[:3] else weights[c][1] for c in calls]
        for c, share in zip(calls, split(growth[f], column)):
            out[c][f] = share
    return out


def tokens(share):
    return {"input": share["inputTokens"] + share["cacheCreationTokens"] + share["cacheReadTokens"],
            "cachedInput": share["cacheReadTokens"], "cacheWriteInput": share["cacheCreationTokens"],
            "cacheWrite1hInput": 0, "output": share["outputTokens"], "reasoning": share["thinkingTokens"]}


def resp(key, start_ms, end_ms, share, model):
    provider = "custom" if model and model[:7].lower() == "custom:" else None
    r = {"key": key, "startMs": start_ms, "endMs": end_ms, "firstMs": None, "model": model, "provider": provider, "fast": False,
         "final": True}
    r.update(tokens(share))
    return r


def minus(a, b):
    return {f: a[f] - b.get(f, 0) for f in FIELDS}


def mode(value):
    return {"mode": value, "evidence": "recorded"} if value else None


def modes(efforts):
    return {"droid:" + mid: {"effort": mode(v), "speed": None} for mid, v in efforts.items()}


# ---------------------------------------------------------------------------------------------------- fixtures

def write_fixtures(out_dir):
    def p(rel):
        full = os.path.join(out_dir, *rel.split("/"))
        os.makedirs(os.path.dirname(full), exist_ok=True)
        return full

    files, traces, mtimes = {}, [], {}

    def trace(rel, line, phase, in_flight=None):
        traces.append(dict(file=rel, afterLine=line, phase=phase, inFlightStartMs=in_flight))

    def session(folder, sid, lines, side, mtime, crlf=False, bom=False):
        rel = "%s/%s.jsonl" % (folder, sid)
        t = Transcript(lines, crlf)
        t.write(p(rel))
        if side is not None:
            side_rel = "%s/%s.settings.json" % (folder, sid)
            with open(p(side_rel), "w", encoding="utf-8", newline="\n") as fh:
                fh.write(("\ufeff" if bom else "") + (side if isinstance(side, str) else json.dumps(side, indent=2)))
            mtimes[side_rel] = mtime - 500
        mtimes[rel] = mtime
        return rel, t

    # ---- J: the legacy layout (directly in the root); its sidecar is cut off mid-write, so it has no counters.
    j_rel, _ = session(ROOT, J, [
        start(J, "/Users/dev/legacy", "Old notes"),                                                       # 1
        user("tj", ms("07:00:00.000"), [text("Summarize the notes")]),                                   # 2
        assistant("da-m1", ms("07:00:02.000"), [text("Done.")], OPUS),                                    # 3
        outcome("tj"),                                                                                    # 4
    ], '{"model":"claude-opus-5-5","tokenUsage":{"inputTokens":10', ms("12:02:00.000", NEXT_DAY))
    files[j_rel] = dict(source="droid", project="legacy", model=OPUS, isSubagent=False, parentFile=None,
                        phase="idle", inFlightStartMs=None, lastLineMs=ms("07:00:02.000"), malformedLines=0,
                        responses=[], effort=None, speed=None, responseModes={})

    # ---- H: droid before 0.123 kept no counters; the sidecar still names the model the call omits.
    h_rel, _ = session(FORGE, H, [
        start(H, "/Users/dev/projects/forge", "Lint"),                                                   # 1
        user("th", ms("08:00:00.000"), [text("Run the linter")]),                                        # 2
        assistant("b8-m1", ms("08:00:05.000"), [text("Clean.")], effort="low"),                          # 3
        outcome("th"),                                                                                    # 4
    ], {"model": "claude-sonnet-4-5", "reasoningEffort": "low"}, ms("12:02:01.000", NEXT_DAY))
    files[h_rel] = dict(source="droid", project="forge", model="claude-sonnet-4-5", isSubagent=False, parentFile=None,
                        phase="idle", inFlightStartMs=None, lastLineMs=ms("08:00:05.000"), malformedLines=0,
                        responses=[], effort=mode("low"), speed=None, responseModes={})

    # ---- C (Windows, CRLF, a sidecar with a byte order mark): the user's own model; the second call names no model
    # and records no effort, so it takes the session model and clears the effort.
    c_usage = counters(3500, 0, 24000, 420, 150)
    c_rel, ct = session(HALO, C, [
        start(C, "C:\\Users\\dev\\projects\\halo", "List the solution"),                                 # 1
        context("tc", ms("11:00:00.000")),                                                               # 2
        user("tc", ms("11:00:00.015"), [text("What is in this solution?")], parent="context-tc"),        # 3
        assistant("c3-m1", ms("11:00:03.500"), [tool_use("call_c1", "LS", {"path": "C:\\Users\\dev"})],
                  BYOK, "low"),                                                                           # 4
        user("c3-r1", ms("11:00:03.600"), [tool_result("call_c1", "halo.sln\nsrc\ntests")]),             # 5
        assistant("c3-m2", ms("11:00:05.100"), [text("A solution with src and tests.")]),                # 6
        outcome("tc"),                                                                                    # 7
    ], sidecar(BYOK, c_usage, providerLock="openai"), ms("12:02:02.000", NEXT_DAY), crlf=True, bom=True)
    cs = allocate(c_usage, ["c3-m1", "c3-m2"], ct.weights)
    files[c_rel] = dict(
        source="droid", project="halo", model=BYOK, isSubagent=False, parentFile=None, phase="idle",
        inFlightStartMs=None, lastLineMs=ms("11:00:05.100"), malformedLines=0,
        responses=[resp("droid:c3-m1", ms("11:00:00.015"), ms("11:00:03.500"), cs["c3-m1"], BYOK),
                   resp("droid:c3-m2", ms("11:00:03.600"), ms("11:00:05.100"), cs["c3-m2"], BYOK)],
        effort=None, speed=None, responseModes=modes({"c3-m1": "low", "c3-m2": None}))
    trace(c_rel, 3, "generating", ms("11:00:00.015"))
    trace(c_rel, 4, "tools")

    # ---- F: C's /handoff successor (parent, no forkedAtMessageId). Its counters started at C's: the own counters
    # are the difference, cache reads clamp at 0 and the thinking share is capped at the output.
    f_usage = counters(4700, 500, 20000, 720, 900)
    f_own = {"inputTokens": 1200, "cacheCreationTokens": 500, "cacheReadTokens": 0, "outputTokens": 300,
             "thinkingTokens": 300}
    f_rel, _ = session(HALO, F, [
        start(F, "C:\\Users\\dev\\projects\\halo", "Continue: list the solution", parent=C),               # 1
        compaction("fc1", ms("11:10:00.000"), 6),                                                        # 2
        context("tf", ms("11:10:05.000")),                                                               # 3
        user("tf", ms("11:10:05.010"), [text("Now add a test project")], parent="context-tf"),           # 4
        assistant("f6-m1", ms("11:10:08.000"), [text("Added tests/halo.tests.csproj.")], BYOK, "xhigh"),  # 5
        outcome("tf"),                                                                                    # 6
    ], sidecar(BYOK, f_usage, providerLock="openai"), ms("12:02:03.000", NEXT_DAY))
    assert minus(f_usage, c_usage)["inputTokens"] == f_own["inputTokens"]
    files[f_rel] = dict(
        source="droid", project="halo", model=BYOK, isSubagent=False, parentFile=None, phase="idle",
        inFlightStartMs=None, lastLineMs=ms("11:10:08.000"), malformedLines=0,
        responses=[resp("droid:f6-m1", ms("11:10:05.010"), ms("11:10:08.000"), f_own, BYOK)],
        effort=mode("xhigh"), speed=None, responseModes=modes({"f6-m1": "xhigh"}))

    # ---- D: Factory's router. The sidecar's model is `auto`; the router's pick names the calls' model.
    d_usage = counters(2000, 30000, 0, 1500, 600)
    d_prompt = user("td", ms("14:00:00.012"), [text("Find where the cache is flushed")], parent="context-td")
    d_call = assistant("d4-m1", ms("14:00:06.000"), [{"type": "thinking", "thinking": "Search first.",
                                                      "signature": "sig", "durationMs": 900},
                                                     tool_use("toolu_d1", "Grep", {"pattern": "flush"})],
                       effort="max")
    d_rel, dt = session(QUASAR, D, [
        start(D, "/Users/dev/projects/quasar", "Cache flush"),                                           # 1
        context("td", ms("14:00:00.000")),                                                               # 2
        d_prompt,                                                                                        # 3
        d_call,                                                                                          # 4
        user("d4-r1", ms("14:00:06.400"), [tool_result("toolu_d1", "src/cache.ts:88: flush()")]),        # 5
        assistant("d4-m2", ms("14:00:09.900"), [text("cache.ts line 88.")], effort="max"),               # 6
        outcome("td"),                                                                                    # 7
    ], sidecar("auto", d_usage, effectiveFactoryRouterModel={"modelId": OPUS, "reasoningEffort": "high",
                                                             "fallbacks": []}), ms("12:02:04.000", NEXT_DAY))
    ds = allocate(d_usage, ["d4-m1", "d4-m2"], dt.weights)
    files[d_rel] = dict(
        source="droid", project="quasar", model=OPUS, isSubagent=False, parentFile=None, phase="idle",
        inFlightStartMs=None, lastLineMs=ms("14:00:09.900"), malformedLines=0,
        responses=[resp("droid:d4-m1", ms("14:00:00.012"), ms("14:00:06.000"), ds["d4-m1"], OPUS),
                   resp("droid:d4-m2", ms("14:00:06.400"), ms("14:00:09.900"), ds["d4-m2"], OPUS)],
        effort=mode("max"), speed=None, responseModes=modes({"d4-m1": "max", "d4-m2": "max"}))

    # ---- E: a fork of D at d4-m1. It copies D's lines with their ids and timestamps (the copied call is owned by D,
    # read first), then a summary; its counters started at D's.
    e_usage = counters(2700, 30000, 31000, 1750, 700)
    e_own = minus(e_usage, d_usage)
    e_rel, _ = session(QUASAR, E, [
        start(E, "/Users/dev/projects/quasar", "Fork: cache flush", parent=D, forkedAtMessageId="d4-m1"),  # 1
        context("td", ms("14:00:00.000")),                                                               # 2
        d_prompt,                                                                                        # 3
        d_call,                                                                                          # 4
        compaction("ec1", ms("14:30:00.000"), 3),                                                        # 5
        context("te", ms("14:30:10.000")),                                                               # 6
        user("te", ms("14:30:10.020"), [text("Try the other approach")], parent="context-te"),           # 7
        assistant("e5-m1", ms("14:30:15.000"), [text("Flushing on idle instead.")], OPUS, "high"),       # 8
        outcome("te"),                                                                                    # 9
    ], sidecar("auto", e_usage, effectiveFactoryRouterModel={"modelId": OPUS, "reasoningEffort": "high",
                                                             "fallbacks": []}), ms("12:02:05.000", NEXT_DAY))
    files[e_rel] = dict(
        source="droid", project="quasar", model=OPUS, isSubagent=False, parentFile=None, phase="idle",
        inFlightStartMs=None, lastLineMs=ms("14:30:15.000"), malformedLines=0,
        responses=[resp("droid:e5-m1", ms("14:30:10.020"), ms("14:30:15.000"), e_own, OPUS)],
        effort=mode("high"), speed=None, responseModes=modes({"e5-m1": "high"}))
    trace(e_rel, 3, "generating", ms("14:00:00.012"))
    trace(e_rel, 4, "generating", ms("14:00:00.012"))
    trace(e_rel, 5, "generating", ms("14:30:00.000"))
    trace(e_rel, 8, "idle")

    # ---- B: A's Task-tool worker, a session of its own in the same folder.
    b_usage = counters(9000, 0, 21000, 700, 240)
    b_rel, bt = session(FORGE, B, [
        start(B, "/Users/dev/projects/forge", "worker: run tests", callingSessionId=A,
              callingToolUseId="toolu_a2"),                                                              # 1
        context("tb", ms("23:58:10.000")),                                                               # 2
        user("tb", ms("23:58:10.005"), [text("Run the test suite and report failures")], parent="context-tb"),
        assistant("b2-m1", ms("23:58:14.000"), [tool_use("call_b1", "Execute", {"command": "npm test"})],
                  GPT, "medium"),                                                                         # 4
        user("b2-r1", ms("23:59:40.000"), [tool_result("call_b1", "2 failing")]),                        # 5
        assistant("b2-m2", ms("23:59:45.250"), [text("Two tests fail in queue.test.ts.")], GPT, "medium"),  # 6
        outcome("tb"),                                                                                    # 7
    ], sidecar(GPT, b_usage, providerLock="openai"), ms("12:02:06.000", NEXT_DAY))
    bs = allocate(b_usage, ["b2-m1", "b2-m2"], bt.weights)
    files[b_rel] = dict(
        source="droid", project="forge", model=GPT, isSubagent=True, parentFile="%s/%s.jsonl" % (FORGE, A),
        phase="idle", inFlightStartMs=None, lastLineMs=ms("23:59:45.250"), malformedLines=0,
        responses=[resp("droid:b2-m1", ms("23:58:10.005"), ms("23:58:14.000"), bs["b2-m1"], GPT),
                   resp("droid:b2-m2", ms("23:59:40.000"), ms("23:59:45.250"), bs["b2-m2"], GPT)],
        effort=mode("medium"), speed=None, responseModes=modes({"b2-m1": "medium", "b2-m2": "medium"}))

    # ---- A: the main session. Turn 1 runs across UTC midnight (its Task call waits for B); turn 2 compacts, calls
    # once without an id (not a call) and switches model. Two malformed lines and a line without a zone.
    a_usage = counters(41003, 12011, 380017, 5207, 905, 1234.5)
    a_side = sidecar(SONNET_DATED, a_usage, providerLock="anthropic",
                     inclusiveTokenUsage={f: a_usage[f] + b_usage[f] for f in a_usage},
                     childInclusiveTokenUsageBySessionId={B: b_usage})
    a_rel, at = session(FORGE, A, [
        start(A, "/Users/dev/projects/forge", "Split the scheduler"),                                    # 1
        context("t1", ms("23:58:00.000")),                                                               # 2
        user("t1", ms("23:58:00.010"), [text("Split the scheduler into a queue and a dispatcher")],
             parent="context-t1"),                                                                       # 3
        assistant("a1-m1", ms("23:58:04.210"), [{"type": "thinking", "thinking": "Read it first.",
                                                 "signature": "sig", "durationMs": 1200},
                                                tool_use("toolu_a1", "Read", {"file_path": "src/scheduler.ts"})],
                  OPUS, "high"),                                                                          # 4
        {"type": "todo_state", "id": "todo-1", "timestamp": iso(ms("23:58:04.250")), "messageIndex": 2,
         "todos": {"todos": "1. [in_progress] Split the scheduler"}},                                    # 5
        user("a1-r1", ms("23:58:04.900"), [tool_result("toolu_a1", "export class Scheduler {\n"
                                                        + "  tick() {}\n" * 3000 + "}")]),               # 6
        assistant("a1-m2", ms("23:58:09.400"), [text("I'll have a worker run the tests."),
                                                tool_use("toolu_a2", "Task", {"subagent_type": "worker",
                                                                              "description": "run tests"})],
                  OPUS, "high"),                                                                          # 7
        user("a1-r2", ms("00:00:30.000", NEXT_DAY), [tool_result("toolu_a2", "agentId: %s\n2 failing" % B)]),  # 8
        assistant("a1-m3", ms("00:00:33.500", NEXT_DAY), [text("Split done; two tests need fixing.")],
                  OPUS, "High"),                                                                          # 9
        user("a1-n1", ms("00:00:33.600", NEXT_DAY), [text("Turn completed")], "user_only"),              # 10
        outcome("t1"),                                                                                    # 11
        '{"type":"message","id":"a1-x","timestamp":"2026-10-21T00:00:40.000Z"',                          # 12
        "[1,2,3]",                                                                                        # 13
        context("t2", ms("09:00:00.000", NEXT_DAY)),                                                     # 14
        user("t2", ms("09:00:00.020", NEXT_DAY), [text("Now wire the dispatcher into main")],
             parent="context-t2"),                                                                       # 15
        compaction("ac1", ms("09:00:20.000", NEXT_DAY), 9),                                              # 16
        assistant("a1-m4", ms("09:00:26.000", NEXT_DAY), [text("Editing main."),
                                                         tool_use("toolu_a4", "Edit", {"file_path": "src/main.ts"})],
                  OPUS, "none"),                                                                          # 17
        user("a1-r4", ms("09:00:27.000", NEXT_DAY), [tool_result("toolu_a4", "ok")]),                    # 18
        assistant(None, ms("09:00:30.000", NEXT_DAY), [text("(no id)")], OPUS),                          # 19
        assistant("a1-m5", ms("09:00:31.500", NEXT_DAY), [text("Done.")], SONNET_DATED, "dynamic"),      # 20
        outcome("t2"),                                                                                    # 21
        assistant("a1-m6", "2026-10-21T09:01:00", [text("no zone")], OPUS),                              # 22
    ], a_side, ms("12:02:07.000", NEXT_DAY))
    calls = ["a1-m1", "a1-m2", "a1-m3", "a1-m4", "a1-m5"]
    shares = allocate(a_usage, calls, at.weights)
    span = {"a1-m1": (ms("23:58:00.010"), ms("23:58:04.210")),
            "a1-m2": (ms("23:58:04.900"), ms("23:58:09.400")),
            "a1-m3": (ms("00:00:30.000", NEXT_DAY), ms("00:00:33.500", NEXT_DAY)),
            "a1-m4": (ms("09:00:20.000", NEXT_DAY), ms("09:00:26.000", NEXT_DAY)),
            "a1-m5": (ms("09:00:27.000", NEXT_DAY), ms("09:00:31.500", NEXT_DAY))}
    files[a_rel] = dict(
        source="droid", project="forge", model=SONNET_DATED, isSubagent=False, parentFile=None, phase="idle",
        inFlightStartMs=None, lastLineMs=ms("09:00:31.500", NEXT_DAY), malformedLines=2,
        responses=[resp("droid:" + c, span[c][0], span[c][1], shares[c], SONNET_DATED if c == "a1-m5" else OPUS)
                   for c in calls],
        effort=mode("dynamic"), speed=None,
        responseModes=modes({"a1-m1": "high", "a1-m2": "high", "a1-m3": "high", "a1-m4": "off", "a1-m5": "dynamic"}))
    for line, phase, in_flight in ((3, "generating", ms("23:58:00.010")), (4, "tools", None),
                                   (6, "generating", ms("23:58:04.900")), (7, "tools", None), (9, "idle", None),
                                   (10, "idle", None), (15, "generating", ms("09:00:00.020", NEXT_DAY)),
                                   (16, "generating", ms("09:00:20.000", NEXT_DAY)), (17, "tools", None),
                                   (19, "generating", ms("09:00:27.000", NEXT_DAY)), (21, "idle", None)):
        trace(a_rel, line, phase, in_flight)
    with open(p("%s/%s.settings.json.bak" % (FORGE, A)), "w", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps(sidecar(OPUS, counters(99999, 0, 0, 9999, 0)), indent=2))

    # ---- G: no sidecar yet. The calls exist (model, effort, phase) but have no tokens.
    g_rel, _ = session(FORGE, G, [
        start(G, "/Users/dev/projects/forge", "Docs"),                                                   # 1
        user("tg", ms("10:00:00.000", NEXT_DAY), [text("Update the README")]),                           # 2
        assistant("a7-m1", ms("10:00:04.000", NEXT_DAY), [text("Updated.")], "glm-4.6", "medium"),       # 3
        user("tg2", ms("10:05:00.000", NEXT_DAY), [text("And the changelog")]),                          # 4
    ], None, ms("12:02:08.000", NEXT_DAY))
    files[g_rel] = dict(
        source="droid", project="forge", model="glm-4.6", isSubagent=False, parentFile=None, phase="generating",
        inFlightStartMs=ms("10:05:00.000", NEXT_DAY), lastLineMs=ms("10:05:00.000", NEXT_DAY), malformedLines=0,
        responses=[], effort=mode("medium"), speed=None, responseModes={})
    trace(g_rel, 4, "generating", ms("10:05:00.000", NEXT_DAY))

    # ---- I: the only turn failed before any call message; its counters wait without a call (history emits them as
    # the session's own response, METRICS 4.16).
    i_rel, _ = session(QUASAR, I, [
        start(I, "/Users/dev/projects/quasar", "Benchmark"),                                             # 1
        context("ti", ms("15:00:00.000", NEXT_DAY)),                                                     # 2
        user("ti", ms("15:00:00.010", NEXT_DAY), [text("Benchmark the flush")], parent="context-ti"),    # 3
        outcome("ti", "error"),                                                                           # 4
    ], sidecar(OPUS, counters(5000, 0, 0, 0, 0)), ms("12:02:09.000", NEXT_DAY))
    files[i_rel] = dict(
        source="droid", project="quasar", model=OPUS, isSubagent=False, parentFile=None, phase="idle",
        inFlightStartMs=None, lastLineMs=ms("15:00:00.010", NEXT_DAY), malformedLines=0, responses=[],
        effort=None, speed=None, responseModes={})

    # ---- Discovery decoys: none of these is a session.
    head = encode(start(A, "/Users/dev/projects/forge", "decoy")) + "\n"
    decoys = {
        "%s/.draft.jsonl" % FORGE: head,
        "%s/nested/deep.jsonl" % FORGE: head,
        "%s/.trash/%s.jsonl" % (ROOT, A): head,
        "%s/%s.jsonl.tmp" % (FORGE, A): head,
        "%s/sessions-index.json" % ROOT: "{\"version\":2,\"entries\":[]}\n",
    }
    for rel, content in decoys.items():
        with open(p(rel), "w", encoding="utf-8", newline="\n") as fh:
            fh.write(content)
    link = p("%s/link.jsonl" % FORGE)
    if os.path.lexists(link):
        os.remove(link)
    os.symlink("%s.jsonl" % A, link)

    for rel, value in mtimes.items():
        os.utime(p(rel), (value / 1000, value / 1000))
    order = sorted((rel for rel in mtimes if rel.endswith(".jsonl")), key=lambda rel: (mtimes[rel], rel))
    return {
        "order": order,
        "mtimes": mtimes,
        "files": {rel: files[rel] for rel in order},
        "traces": traces,
        "droid": {
            "_comment": "Factory Droid (METRICS 4.16). The folder `droid` plays the home folder; `discover` lists "
                        "the sessions discovery finds under droid/.factory/sessions. Its sessions are in `order` and "
                        "`files` (each read from the start: the history rule); the history analysis also emits the "
                        "own counters of a session without calls (I) as `droid:<session id>:session`. `roots`: "
                        "create the folders `make` in a scratch folder, resolve the setting (null: the automatic "
                        "root of the scratch folder as home) and compare with `expected` (relative). `sidecars`: "
                        "a sidecar's text (`size`: padded with spaces to that many bytes) and whether it is "
                        "readable, its counters and session model (`encoding`: the text's bytes in that encoding "
                        "instead of UTF-8). `allocations`: the largest-remainder split of "
                        "`total` by `weights`. `live`: scripted read cycles in a scratch home (see its _comment). "
                        "`history`: scripted history analyses in a scratch home (see its _comment).",
            "discover": sorted(files),
            "roots": roots(),
            "sidecars": sidecar_cases(),
            "allocations": allocation_cases(),
            "live": live(),
            "history": history_passes(),
        },
    }


def roots():
    def case(make, setting, expected):
        return {"make": make, "setting": setting, "expected": expected}

    return [
        case([".factory/sessions"], None, ".factory/sessions"),
        case([], None, ".factory/sessions"),
        case([".factory/sessions"], ".factory", ".factory/sessions"),
        case([".factory/sessions"], ".factory/sessions", ".factory/sessions"),
        case(["moved/sessions/-tmp-x"], "moved", "moved/sessions"),
        case(["elsewhere/-tmp-x"], "elsewhere", "elsewhere"),
    ]


def sidecar_cases():
    usage = '{"inputTokens":120,"outputTokens":30,"cacheCreationTokens":7,"cacheReadTokens":900,"thinkingTokens":4}'
    full = {"inputTokens": 120, "cacheCreationTokens": 7, "cacheReadTokens": 900, "outputTokens": 30,
            "thinkingTokens": 4}

    def case(name, text_value, readable, counter_values=None, model=None, size=None, encoding=None):
        c = {"name": name, "text": text_value, "readable": readable, "counters": counter_values, "model": model}
        if size is not None:
            c["size"] = size
        if encoding is not None:
            c["encoding"] = encoding
        return c

    return [
        case("plain", '{"model":"claude-opus-5-5","tokenUsage":%s}' % usage, True, full, "claude-opus-5-5"),
        case("byte order mark", '\ufeff{"model":" gpt-5.5 ","tokenUsage":%s}' % usage, True, full, "gpt-5.5"),
        case("router pick", '{"model":"AUTO","effectiveFactoryRouterModel":{"modelId":"claude-opus-5"},'
             '"tokenUsage":%s}' % usage, True, full, "claude-opus-5"),
        case("auto without a pick", '{"model":"auto","tokenUsage":%s}' % usage, True, full, None),
        case("blank pick falls back to the model", '{"model":"glm-5.3","effectiveFactoryRouterModel":{"modelId":" "},'
             '"tokenUsage":%s}' % usage, True, full, "glm-5.3"),
        case("no tokenUsage (droid before 0.123)", '{"model":"claude-sonnet-4-5"}', True, None, "claude-sonnet-4-5"),
        case("tokenUsage not an object", '{"model":"x","tokenUsage":[1,2]}', True, None, "x"),
        case("token values", '{"tokenUsage":{"inputTokens":12.9,"outputTokens":-7,"cacheCreationTokens":"5",'
             '"cacheReadTokens":1e20,"thinkingTokens":null,"factoryCredits":3.5}}', True,
             {"inputTokens": 12, "cacheCreationTokens": 0, "cacheReadTokens": 10 ** 12, "outputTokens": 0,
              "thinkingTokens": 0}, None),
        case("missing fields count 0", '{"tokenUsage":{"outputTokens":3}}', True,
             {"inputTokens": 0, "cacheCreationTokens": 0, "cacheReadTokens": 0, "outputTokens": 3,
              "thinkingTokens": 0}, None),
        case("cut off mid-write", '{"model":"x","tokenUsage":{"inputTokens":10', False),
        case("not an object", '[{"tokenUsage":{}}]', False),
        case("NaN is not JSON", '{"tokenUsage":{"inputTokens":NaN}}', False),
        case("not UTF-8", '{"model":"caf\xe9"}', False, encoding="latin-1"),
        case("exactly 1 MiB", '{"model":"m","tokenUsage":%s}' % usage, True, full, "m", size=1048576),
        case("one byte more", '{"model":"m","tokenUsage":%s}' % usage, False, size=1048577),
    ]


def allocation_cases():
    def case(total, weights):
        return {"total": total, "weights": weights, "shares": split(total, weights)}

    return [
        case(10, [1, 1, 1]),
        case(7, [3, 2, 2]),
        case(5, [65536, 65536]),
        case(1, [4, 4, 4, 4]),
        case(0, [5, 9]),
        case(1000000000000, [8388608, 1, 8388607]),
        case(101, [70000, 70100, 140200]),
    ]


CUT_OFF = '{"model":"claude-opus-5-5","tokenUsage":{"inputTokens":4'  # a sidecar caught mid-write


def pair(parent_id, fork_id, cwd, base, parent_model=OPUS):
    """A parent with one call and its successor (`parent`, a summary first) with one call, `base` (ms) the start."""
    parent_lines = [start(parent_id, cwd, "Parent"),
                    user("p-" + parent_id[:4], base, [text("Start")]),
                    assistant("pm-" + parent_id[:4], base + 3000, [text("Started.")], parent_model, "high"),
                    outcome("p-" + parent_id[:4])]
    fork_lines = [start(fork_id, cwd, "Continue", parent=parent_id),
                  compaction("fc-" + fork_id[:4], base + 60_000, 3),
                  user("f-" + fork_id[:4], base + 61_000, [text("Continue")]),
                  assistant("fm-" + fork_id[:4], base + 64_000, [text("Continued.")], OPUS, "high"),
                  outcome("f-" + fork_id[:4])]
    calls = {"parent": ("droid:pm-" + parent_id[:4], base, base + 3000),
             "fork": ("droid:fm-" + fork_id[:4], base + 61_000, base + 64_000)}
    return parent_lines, fork_lines, calls


def history_passes():
    """History analyses of a scratch home whose parent sidecars are unreadable (METRICS 4.16 Own counters)."""
    folder = ".factory/sessions/-Users-dev-h"
    PA, KA = "1a2b3c4d-0000-4000-8000-00000000000a", "2b3c4d5e-0000-4000-8000-00000000000b"
    PB, KB = "3c4d5e6f-0000-4000-8000-00000000000c", "4d5e6f70-0000-4000-8000-00000000000d"
    pa_lines, ka_lines, a = pair(PA, KA, "/Users/dev/h", ms("16:00:00.000"))
    pb_lines, kb_lines, b = pair(PB, KB, "/Users/dev/h", ms("16:10:00.000"))
    pa, ka = counters(3000, 0, 9000, 300, 0), counters(3800, 0, 20000, 450, 20)
    kb = counters(2500, 100, 7000, 180, 10)

    def rel(sid, ext=".jsonl"):
        return "%s/%s%s" % (folder, sid, ext)

    def write(sid, lines=None, doc=None, text_value=None, at=None):
        step = {"write": rel(sid, ".jsonl" if lines is not None else ".settings.json"), "mtime": at}
        if lines is not None:
            step["text"] = "".join(encode(line) + "\n" for line in lines)
        else:
            step["text"] = text_value if text_value is not None else json.dumps(doc, indent=2)
        return step

    def r(call, share):
        return resp(call[0], call[1], call[2], share, OPUS)

    counted = {rel(PA): [r(a["parent"], pa)], rel(KA): [r(a["fork"], minus(ka, pa))], rel(KB): [r(b["fork"], kb)]}
    return {
        "_comment": "Steps in a scratch home with only Factory Droid sessions: `write` sets a file's `text` and "
                    "modification time (ms); `analyze` runs a history analysis of the home's sessions (METRICS 8, "
                    "the history rule of 4.16), keeping the remembered parent signatures from the analyses before, "
                    "and compares the responses per session file and the session files skipped because their own "
                    "counters are unknown.",
        "steps": [
            write(PA, pa_lines, at=ms("16:00:04.000")),
            write(PA, text_value=CUT_OFF, at=ms("16:00:05.000")),
            write(KA, ka_lines, at=ms("16:01:05.000")),
            write(KA, doc=sidecar(OPUS, ka), at=ms("16:01:05.500")),
            write(PB, pb_lines, at=ms("16:10:04.000")),
            write(PB, text_value=CUT_OFF, at=ms("16:10:05.000")),
            write(KB, kb_lines, at=ms("16:11:05.000")),
            write(KB, doc=sidecar(OPUS, kb), at=ms("16:11:05.500")),
            # Both parents unreadable: the successors are skipped (the parents themselves have no counters).
            {"analyze": True, "responses": {}, "skipped": [rel(KA), rel(KB)]},
            # PA readable now: KA counts its own share. PB unreadable again with the same signature: 0, KB counts all.
            write(PA, doc=sidecar(OPUS, pa), at=ms("16:20:00.000")),
            {"analyze": True, "responses": counted, "skipped": []},
            {"analyze": True, "responses": counted, "skipped": []},
        ],
    }


def live():
    """Read cycles in a scratch home: L read from its start, S from its end, then successors whose parent sidecar is
    unreadable (K1 until it is rewritten, K2 for good, K3 started at its end)."""
    L, S = "5a1d7c3e-0f4b-4e2a-8c6d-9b0e1f2a3b4c", "6b2e8d4f-1a5c-4f3b-9d7e-0c1f2a3b4c5d"
    folder = ".factory/sessions/-Users-dev-p"
    lpath, spath = "%s/%s.jsonl" % (folder, L), "%s/%s.jsonl" % (folder, S)
    lines = [start(L, "/Users/dev/p", "Live"),
             context("u1", ms("13:00:00.000")),
             user("u1", ms("13:00:00.010"), [text("Add a flag")], parent="context-u1"),
             assistant("l-m1", ms("13:00:03.000"), [tool_use("toolu_l1", "Read", {"file_path": "cli.ts"})], OPUS,
                       "high")]
    r1 = user("l-r1", ms("13:00:03.200"), [tool_result("toolu_l1", "export {}")])
    m2 = assistant("l-m2", ms("13:00:05.000"), [text("Added --dry-run.")], OPUS, "high")
    turn2 = [user("u2", ms("13:01:00.000"), [text("Also document it")]),
             assistant("l-m3", ms("13:01:04.000"), [tool_use("toolu_l3", "Edit", {"file_path": "README.md",
                                                                                 "new_string": "x" * 600})],
                       OPUS, "high"),
             user("l-r3", ms("13:01:04.100"), [tool_result("toolu_l3", "ok")]),
             assistant("l-m4", ms("13:01:09.000"), [text("Documented.")], OPUS, "high"),
             outcome("u2")]
    m5 = [user("u3", ms("13:02:00.000"), [text("Run it")]),
          assistant("l-m5", ms("13:02:03.000"), [text("It runs.")], SONNET_DATED, "medium")]
    m6 = [user("u4", ms("13:03:00.000"), [text("Thanks")]),
          assistant("l-m6", ms("13:03:02.500"), [text("You're welcome.")], SONNET_DATED)]
    c1 = counters(1000, 4000, 0, 120, 30)
    c2 = counters(1200, 4000, 4800, 200, 30)
    c4 = counters(1800, 4000, 13800, 700, 150)
    c5 = counters(1900, 4000, 14300, 260, 35)
    c6 = counters(2200, 4000, 14300, 260, 35)
    full = Transcript(lines + [r1, m2, outcome("u1")] + turn2 + m5 + m6)
    w = full.weights

    def r(mid, start_ms, end_ms, share, model=OPUS):
        return resp("droid:" + mid, start_ms, end_ms, share, model)

    k1 = r("l-m1", ms("13:00:00.010"), ms("13:00:03.000"), c1)
    k2 = r("l-m2", ms("13:00:03.200"), ms("13:00:05.000"), minus(c2, c1))
    pair = allocate(minus(c4, c2), ["l-m3", "l-m4"], w)
    k3 = r("l-m3", ms("13:01:00.000"), ms("13:01:04.000"), pair["l-m3"])
    k4 = r("l-m4", ms("13:01:04.100"), ms("13:01:09.000"), pair["l-m4"])
    mark = {f: max(c4[f], c5[f]) for f in FIELDS}
    k5 = r("l-m5", ms("13:02:00.000"), ms("13:02:03.000"), {f: max(0, c5[f] - c4[f]) for f in FIELDS}, SONNET_DATED)
    k6 = r("l-m6", ms("13:03:00.000"), ms("13:03:02.500"), {f: max(0, c6[f] - mark[f]) for f in FIELDS},
           SONNET_DATED)
    again = allocate(c6, ["l-m1", "l-m2", "l-m3", "l-m4", "l-m5", "l-m6"], w)
    spans = {"l-m1": (ms("13:00:00.010"), ms("13:00:03.000")), "l-m2": (ms("13:00:03.200"), ms("13:00:05.000")),
             "l-m3": (ms("13:01:00.000"), ms("13:01:04.000")), "l-m4": (ms("13:01:04.100"), ms("13:01:09.000")),
             "l-m5": (ms("13:02:00.000"), ms("13:02:03.000")), "l-m6": (ms("13:03:00.000"), ms("13:03:02.500"))}
    reset = [r(m, spans[m][0], spans[m][1], again[m], SONNET_DATED if m in ("l-m5", "l-m6") else OPUS)
             for m in again]
    zero = dict.fromkeys(FIELDS, 0)
    s_old = [start(S, "/Users/dev/p", "Old"),
             user("v1", ms("09:00:00.000"), [text("Old work")]),
             assistant("s-m1", ms("09:00:04.000"), [text("Done long ago.")], GPT, "low"),
             outcome("v1")]
    s_new = [user("v2", ms("13:05:00.000"), [text("Resume")]),
             assistant("s-m2", ms("13:05:02.000"), [text("Resumed.")], GPT, "low")]
    s0, s1 = counters(50000, 0, 200000, 9000, 1000), counters(50400, 0, 210000, 9080, 1010)
    ks = resp("droid:s-m2", ms("13:05:00.000"), ms("13:05:02.000"), minus(s1, s0), GPT)

    def read(path, keys, pending=zero, phase=None):
        step = {"read": path, "responses": keys, "pending": pending}
        if phase:
            step["phase"] = phase
        return step

    def side(path, usage, at):
        return {"sidecar": path, "doc": sidecar(OPUS, usage), "mtime": at}

    return {
        "_comment": "Steps, in order, in a scratch home: `append` writes `lines` (each followed by \\n) to a "
                    "transcript; `rewrite` replaces it with `lines` through a new file (a new identity); `sidecar` "
                    "writes the transcript's sidecar (`doc` as JSON) and sets its modification time (ms); `open` "
                    "starts following a transcript (`atEnd`: it starts at its end, older than the startup window); "
                    "a `sidecar` step with `text` writes that text instead of `doc`; "
                    "`read` runs one read cycle of that transcript and compares every response emitted so far "
                    "(the latest version of each key, in first-emission order), the growth still waiting (P) and, "
                    "when given, the phase.",
        "steps": [
            # One call, then its rewrite: the call takes the rewrite whole.
            {"append": lpath, "lines": lines},
            side(lpath, c1, ms("13:00:03.050")),
            {"open": lpath, "atEnd": False},
            read(lpath, [k1], phase="tools"),
            # The rewrite before the call's line: it waits, then the call takes it.
            {"append": lpath, "lines": [r1]},
            side(lpath, c2, ms("13:00:04.990")),
            read(lpath, [k1], minus(c2, c1), "generating"),
            {"append": lpath, "lines": [m2, outcome("u1")]},
            read(lpath, [k1, k2], phase="idle"),
            # Two calls share one rewrite: split by their weights.
            {"append": lpath, "lines": turn2},
            side(lpath, c4, ms("13:01:09.050")),
            read(lpath, [k1, k2, k3, k4]),
            # A restored .bak (lower counters) adds nothing; later growth counts only above the mark.
            side(lpath, c2, ms("13:01:30.000")),
            read(lpath, [k1, k2, k3, k4]),
            {"append": lpath, "lines": m5},
            side(lpath, c5, ms("13:02:03.050")),
            read(lpath, [k1, k2, k3, k4, k5]),
            # Growth without a call waits for the next call.
            side(lpath, c6, ms("13:02:30.000")),
            read(lpath, [k1, k2, k3, k4, k5], {f: max(0, c6[f] - mark[f]) for f in FIELDS}),
            {"append": lpath, "lines": m6},
            read(lpath, [k1, k2, k3, k4, k5, k6]),
            # A session older than the startup window: its sidecar only sets the mark, later growth counts.
            {"append": spath, "lines": s_old},
            side(spath, s0, ms("09:00:04.050")),
            {"open": spath, "atEnd": True},
            read(spath, []),
            {"append": spath, "lines": s_new},
            side(spath, s1, ms("13:05:02.050")),
            read(spath, [ks]),
            # The transcript replaced (a new identity): the session starts over and allocates its counters again.
            {"rewrite": lpath, "lines": lines + [r1, m2, outcome("u1")] + turn2 + m5 + m6},
            read(lpath, reset),
        ] + unreadable_parents(folder),
    }


def unreadable_parents(folder):
    """Successors whose parent sidecar is unreadable (METRICS 4.16 Own counters), as steps of `live`."""
    P1, K1 = "7a1b2c3d-0000-4000-8000-000000000001", "8b2c3d4e-0000-4000-8000-000000000002"
    P2, K2 = "9c3d4e5f-0000-4000-8000-000000000003", "ad4e5f60-0000-4000-8000-000000000004"
    P3, K3 = "be5f6071-0000-4000-8000-000000000005", "cf607182-0000-4000-8000-000000000006"
    p1_lines, k1_lines, c1 = pair(P1, K1, "/Users/dev/p", ms("14:00:00.000"))
    p2_lines, k2_lines, c2 = pair(P2, K2, "/Users/dev/p", ms("14:10:00.000"))
    p3_lines, k3_lines, c3 = pair(P3, K3, "/Users/dev/p", ms("08:00:00.000"))
    path = {sid: "%s/%s.jsonl" % (folder, sid) for sid in (P1, K1, P2, K2, P3, K3)}
    p1, k1 = counters(5000, 1000, 20000, 400, 50), counters(5600, 1000, 41000, 520, 60)
    k2 = counters(1500, 0, 6000, 90, 0)
    p3, k3a, k3b = counters(4000, 0, 10000, 300, 0), counters(4500, 0, 18000, 380, 0), counters(4900, 0, 30000, 460, 5)
    zero = dict.fromkeys(FIELDS, 0)
    more = [user("g-k3", ms("14:30:00.000"), [text("More")]),
            assistant("gm-k3", ms("14:30:03.000"), [text("More done.")], OPUS, "high")]

    def side(sid, usage, at, text_value=None):
        step = {"sidecar": path[sid], "mtime": at}
        if text_value is not None:
            step["text"] = text_value
        else:
            step["doc"] = sidecar(OPUS, usage)
        return step

    def read(sid, keys):
        return {"read": path[sid], "responses": keys, "pending": zero}

    k1_resp = resp(c1["fork"][0], c1["fork"][1], c1["fork"][2], minus(k1, p1), OPUS)
    k2_resp = resp(c2["fork"][0], c2["fork"][1], c2["fork"][2], k2, OPUS)
    k3_resp = resp("droid:gm-k3", ms("14:30:00.000"), ms("14:30:03.000"), minus(k3b, k3a), OPUS)
    return [
        # K1: its parent's sidecar is caught mid-write; nothing counts until it reads, then K1 counts its own share.
        {"append": path[P1], "lines": p1_lines},
        side(P1, None, ms("14:00:03.100"), CUT_OFF),
        {"append": path[K1], "lines": k1_lines},
        side(K1, k1, ms("14:01:04.050")),
        {"open": path[K1], "atEnd": False},
        read(K1, []),
        side(P1, p1, ms("14:01:05.000")),
        read(K1, [k1_resp]),
        # K2: its parent's sidecar stays malformed; the second attempt finds the same signature, so the parent gives 0.
        {"append": path[P2], "lines": p2_lines},
        side(P2, None, ms("14:10:03.100"), CUT_OFF),
        {"append": path[K2], "lines": k2_lines},
        side(K2, k2, ms("14:11:04.050")),
        {"open": path[K2], "atEnd": False},
        read(K2, []),
        read(K2, [k2_resp]),
        # K3 starts at its end while its parent's sidecar is unreadable: H waits for known own counters, so only the
        # growth after the parent reads counts.
        {"append": path[P3], "lines": p3_lines},
        side(P3, None, ms("08:00:03.100"), CUT_OFF),
        {"append": path[K3], "lines": k3_lines},
        side(K3, k3a, ms("08:01:04.050")),
        {"open": path[K3], "atEnd": True},
        side(P3, p3, ms("14:20:00.000")),
        read(K3, []),
        {"append": path[K3], "lines": more},
        side(K3, k3b, ms("14:30:03.050")),
        read(K3, [k3_resp]),
    ]


def main():
    if len(sys.argv) != 2:
        print(__doc__.strip())
        sys.exit(2)
    print(json.dumps(write_fixtures(sys.argv[1]), indent=2))


if __name__ == "__main__":
    main()
