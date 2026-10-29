#!/usr/bin/env python3
"""Pi (Pi Coding Agent) session fixtures (METRICS 4.13), used by generate_fixtures.py.

  python3 spec/tools/pi_fixtures.py OUT_DIR

OUT_DIR/pi plays the role of `~/.pi`: the default root is pi/agent/sessions, and pi/agent/settings.json names the
custom session folder `~/pi/custom-sessions` (home = OUT_DIR), whose session files lie directly in it. Files are laid
out as Pi writes them. The returned expectations have the shape of spec/fixtures/parser-expected.json (`order`,
`files`, `mtimes`) plus `discover` (the session files discovery must find in both roots) and `traces` (phase
checkpoints after a given physical line of a file). Expected values are written by hand from METRICS 4.7 and 4.13
and EXECUTION-MODES (Pi), independently of reference.py.
"""
import json
import os
import sys

from omp_fixtures import iso, ms, uuid7

DAY = "2026-10-20"
NEXT_DAY = "2026-10-21"
HOME = "/Users/dev"
SESSIONS = "pi/agent/sessions"
CUSTOM = "pi/custom-sessions"

OPUS = ("anthropic-messages", "anthropic", "claude-opus-5-5")
GPT = ("openai-responses", "openai", "gpt-5.5")
SONNET = ("anthropic-messages", "anthropic", "claude-sonnet-5-5")


def usage(inp, out, cache_read, cache_write, reasoning=None, cache_write_1h=None):
    u = {"input": inp, "output": out, "cacheRead": cache_read, "cacheWrite": cache_write}
    if cache_write_1h is not None:
        u["cacheWrite1h"] = cache_write_1h
    if reasoning is not None:
        u["reasoning"] = reasoning
    u["totalTokens"] = inp + out + cache_read + cache_write
    u["cost"] = {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0, "total": 0}
    return u


def text(value):
    return {"type": "text", "text": value}


def thinking(value):
    return {"type": "thinking", "thinking": value}


def tool_call(call_id, name, arguments):
    return {"type": "toolCall", "id": call_id, "name": name, "arguments": arguments}


def bucket(cwd):
    return "--%s--" % cwd.lstrip("/\\").replace("/", "-").replace("\\", "-").replace(":", "-")


def session_name(created, session_id):
    return "%s_%s.jsonl" % (iso(created).replace(":", "-").replace(".", "-"), session_id)


class Session:
    """The physical lines of one Pi session file. Entry ids are 8 hex characters: a 4-character prefix and a counter."""

    def __init__(self, prefix):
        self.prefix, self.lines, self.count, self.last_id = prefix, [], 0, None

    def line(self):
        return len(self.lines)

    def raw(self, value):
        self.lines.append(value)
        return self.line()

    def header(self, created, session_id, cwd, **extra):
        return self.raw(dict({"type": "session", "version": 3, "id": session_id, "timestamp": iso(created),
                              "cwd": cwd}, **extra))

    def entry(self, entry_type, when, **fields):
        self.count += 1
        entry_id = "%s%04x" % (self.prefix, self.count)
        row = {"type": entry_type, "id": entry_id, "parentId": self.last_id, "timestamp": iso(when)}
        row.update(fields)
        self.last_id = entry_id
        self.lines.append(row)
        return entry_id

    def copy(self, rows):
        for row in rows:
            self.lines.append(row)
            self.last_id = row["id"]

    def thinking_level(self, when, level):
        return self.entry("thinking_level_change", when, thinkingLevel=level)

    def model_change(self, when, provider, model):
        return self.entry("model_change", when, provider=provider, modelId=model)

    def user(self, when, value):
        return self.entry("message", when, message={"role": "user", "content": [text(value)], "timestamp": when})

    def tool_result(self, when, call_id, name, value, use=None, is_error=False):
        msg = {"role": "toolResult", "toolCallId": call_id, "toolName": name, "content": [text(value)],
               "isError": is_error, "timestamp": when}
        if use is not None:
            msg["usage"] = use
        return self.entry("message", when, message=msg)

    def assistant(self, when, route, content, use, stop, started, duration=None, level=None, **extra):
        api, provider, model = route
        msg = {"role": "assistant", "content": content}
        if api is not None:
            msg["api"] = api
        msg.update({"provider": provider, "model": model})
        if level is not None:
            msg["thinkingLevel"] = level
        msg.update({"usage": use, "stopReason": stop, "timestamp": started})
        if duration is not None:
            msg["durationMs"] = duration
        msg.update(extra)
        return self.entry("message", when, message=msg)

    def side_usage(self, when, kind, route, use):
        _, provider, model = route
        return self.entry("usage", when, kind=kind, provider=provider, model=model, usage=use)

    def write(self, path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            for row in self.lines:
                f.write(row if isinstance(row, str) else json.dumps(row, separators=(",", ":"), ensure_ascii=False))
                f.write("\n")


def resp(key, start, end, inp, cached, cache_write, out, reasoning, model, provider, cache_write_1h=0):
    return {"key": key, "startMs": start, "endMs": end, "firstMs": None, "input": inp, "cachedInput": cached,
            "cacheWriteInput": cache_write, "cacheWrite1hInput": cache_write_1h, "output": out,
            "reasoning": reasoning, "model": model, "provider": provider, "fast": False, "final": True}


def key(entry_id, stamp):
    return "pi:%s:%d" % (entry_id, stamp)


def effort(mode, evidence):
    return {"mode": mode, "evidence": evidence}


def write_fixtures(out_dir):
    def p(rel):
        full = os.path.join(out_dir, *rel.split("/"))
        os.makedirs(os.path.dirname(full), exist_ok=True)
        return full

    files, traces, mtimes = {}, [], {}

    def trace(rel, line, phase, in_flight=None):
        traces.append(dict(file=rel, afterLine=line, phase=phase, inFlightStartMs=in_flight))

    with open(p("pi/agent/settings.json"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps({"defaultProvider": "anthropic", "sessionDir": "~/pi/custom-sessions"}, indent=2) + "\n")

    # ---- Pi 1.x: durationMs, thinkingLevel, cacheWrite1h, side usage of every kind.
    halo = HOME + "/projects/halo"
    halo_created = ms("12:00:00.000")
    halo_id = uuid7(halo_created, 31)
    halo_name = session_name(halo_created, halo_id)
    halo_rel = "%s/%s/%s" % (SESSIONS, bucket(halo), halo_name)
    s = Session("a1f0")
    s.header(halo_created, halo_id, halo)                                                           # 1
    s.thinking_level(ms("12:00:00.010"), "medium")                                                   # 2
    s.model_change(ms("12:00:00.011"), "anthropic", "claude-opus-5-5")                               # 3
    s.user(ms("12:00:05.000"), "Split the queue from the dispatcher")                                # 4
    a1 = s.assistant(ms("12:00:11.300"), OPUS, [thinking("Find the queue"),
                                                tool_call("toolu_h1", "read", {"path": "src/queue.ts"}),
                                                tool_call("toolu_h2", "grep", {"pattern": "dispatch("})],
                     usage(3, 420, 0, 18000, reasoning=150, cache_write_1h=18000), "toolUse",
                     ms("12:00:05.006"), duration=6200.618, level="high")                            # 5
    s.tool_result(ms("12:00:11.900"), "toolu_h1", "read", "export class Queue {}")                   # 6
    t2 = s.tool_result(ms("12:00:12.400"), "toolu_h2", "grep", "src/run.ts:41",
                       use=usage(900, 40, 0, 0))                                                     # 7 tool usage
    a2 = s.assistant(ms("12:00:16.520"), OPUS, [text("Queue moved to src/queue.ts.")],
                     usage(5, 260, 18000, 600), "stop", ms("12:00:12.405"), duration=4100, level="high")  # 8
    w1 = s.side_usage(ms("12:00:20.000"), "cache_warm", OPUS, usage(0, 1, 18600, 0))                 # 9
    s.thinking_level(ms("12:00:30.000"), "xhigh")                                                    # 10
    c1 = s.entry("compaction", ms("12:01:00.000"), summary="Queue split done.", firstKeptEntryId=a2,
                 tokensBefore=19000, usage=usage(12000, 800, 0, 0))                                  # 11
    s.entry("branch_summary", ms("12:01:05.000"), fromId=a1, summary="Explored grep first.",
            usage=usage(0, 0, 0, 0))                                                                 # 12 zero usage
    s.user(ms("12:02:00.000"), "Wait for the long-running review")                                   # 13
    d1 = s.assistant(ms("12:02:00.520"), OPUS, [], usage(10, 0, 18600, 0), "deferred", ms("12:02:00.006"),
                     duration=500, level="xhigh")                                                    # 14 deferred
    s.user(ms("12:03:00.000"), "Run the tests")                                                      # 15
    s.assistant(ms("12:03:01.200"), OPUS, [], usage(0, 0, 0, 0), "error", ms("12:03:00.006"), duration=1190,
                errorMessage="overloaded_error")                                                     # 16 error, zero
    s.user(ms("12:03:10.000"), "Run the tests, please")                                              # 17
    halo_lines = list(s.lines)
    s.write(p(halo_rel))
    files[halo_rel] = dict(
        source="pi", project="halo", model="claude-opus-5-5", isSubagent=False, parentFile=None,
        phase="generating", inFlightStartMs=ms("12:03:10.000"), lastLineMs=ms("12:03:10.000"), malformedLines=0,
        responses=[
            resp(key(a1, ms("12:00:05.006")), ms("12:00:05.006"), ms("12:00:11.206"), 18003, 0, 18000, 420, 150,
                 "claude-opus-5-5", "anthropic", cache_write_1h=18000),
            resp(key(t2, ms("12:00:12.400")), None, ms("12:00:12.400"), 900, 0, 0, 40, 0, None, None),
            resp(key(a2, ms("12:00:12.405")), ms("12:00:12.405"), ms("12:00:16.505"), 18605, 18000, 600, 260, 0,
                 "claude-opus-5-5", "anthropic"),
            resp(key(w1, ms("12:00:20.000")), None, ms("12:00:20.000"), 18600, 18600, 0, 1, 0,
                 "claude-opus-5-5", "anthropic"),
            resp(key(c1, ms("12:01:00.000")), None, ms("12:01:00.000"), 12000, 0, 0, 800, 0, None, None),
            resp(key(d1, ms("12:02:00.006")), None, ms("12:02:00.506"), 18610, 18600, 0, 0, 0,
                 "claude-opus-5-5", "anthropic"),
        ],
        effort=effort("xhigh", "recorded"), speed=None,
        responseModes={
            key(a1, ms("12:00:05.006")): {"effort": effort("high", "recorded"), "speed": None},
            key(t2, ms("12:00:12.400")): {"effort": None, "speed": None},
            key(a2, ms("12:00:12.405")): {"effort": effort("high", "recorded"), "speed": None},
            key(w1, ms("12:00:20.000")): {"effort": None, "speed": None},
            key(c1, ms("12:01:00.000")): {"effort": None, "speed": None},
            key(d1, ms("12:02:00.006")): {"effort": effort("xhigh", "recorded"), "speed": None},
        })
    trace(halo_rel, 2, "idle")
    trace(halo_rel, 4, "generating", ms("12:00:05.000"))
    trace(halo_rel, 5, "tools")
    trace(halo_rel, 6, "tools")
    trace(halo_rel, 7, "generating", ms("12:00:12.400"))
    trace(halo_rel, 8, "idle")
    trace(halo_rel, 13, "generating", ms("12:02:00.000"))
    trace(halo_rel, 14, "idle")
    trace(halo_rel, 15, "generating", ms("12:03:00.000"))
    trace(halo_rel, 16, "idle")
    trace(halo_rel, 17, "generating", ms("12:03:10.000"))
    mtimes[halo_rel] = ms("12:00:01.000", NEXT_DAY)

    # ---- A package's sub-agent: <stem>/<name>.jsonl next to the session file <stem>.jsonl.
    sub_rel = halo_rel[:-len(".jsonl")] + "/reviewer.jsonl"
    sub_created = ms("12:02:00.100")
    s = Session("b2e1")
    s.header(sub_created, uuid7(sub_created, 32), halo)                                             # 1
    s.user(ms("12:02:00.200"), "Review src/queue.ts")                                                # 2
    r1 = s.assistant(ms("12:02:09.300"), SONNET, [text("Looks good; one nit.")], usage(2400, 310, 0, 0),
                     "stop", ms("12:02:00.206"), duration=9090.9)                                    # 3
    s.write(p(sub_rel))
    files[sub_rel] = dict(
        source="pi", project="halo", model="claude-sonnet-5-5", isSubagent=True, parentFile=halo_rel,
        phase="idle", inFlightStartMs=None, lastLineMs=ms("12:02:09.300"), malformedLines=0,
        responses=[resp(key(r1, ms("12:02:00.206")), ms("12:02:00.206"), ms("12:02:09.296"), 2400, 0, 0, 310, 0,
                        "claude-sonnet-5-5", "anthropic")],
        effort=None, speed=None,
        responseModes={key(r1, ms("12:02:00.206")): {"effort": None, "speed": None}})
    mtimes[sub_rel] = ms("12:00:02.000", NEXT_DAY)

    # ---- pi-ai before 0.99: no durationMs and no thinkingLevel; a guard failure, an id-less entry, a bad line.
    ember = HOME + "/projects/ember"
    ember_created = ms("12:10:00.000")
    ember_rel = "%s/%s/%s" % (SESSIONS, bucket(ember), session_name(ember_created, uuid7(ember_created, 33)))
    s = Session("c3d2")
    s.header(ember_created, uuid7(ember_created, 33), ember)                                         # 1
    s.thinking_level(ms("12:10:00.010"), "low")                                                      # 2
    s.user(ms("12:10:02.000"), "Rename Orbit to Trajectory")                                         # 3
    e1 = s.assistant(ms("12:10:08.400"), GPT, [tool_call("call_e1", "edit", {"path": "src/orbit.ts"})],
                     usage(5200, 900, 0, 0, reasoning=300), "toolUse", ms("12:10:02.005"))           # 4
    s.tool_result(ms("12:10:08.430"), "call_e1", "edit", "ok")                                       # 5
    e2 = s.assistant(ms("12:10:11.560"), GPT, [text("Renamed in 14 files.")], usage(300, 410, 5100, 0),
                     "stop", ms("12:10:08.436"))                                                     # 6
    s.assistant(ms("12:10:20.000"), (None, "openai", "gpt-5.5"), [text("no api")], usage(50, 5, 0, 0),
                "stop", ms("12:10:19.000"))                                                          # 7 guard: skip
    s.raw({"type": "message", "parentId": s.last_id, "timestamp": iso(ms("12:10:30.000")),
           "message": {"role": "user", "content": "No id", "timestamp": ms("12:10:30.000")}})       # 8 no id
    s.raw('{"type":"message","id":"c3d2ffff","timestamp":')                                         # 9 malformed
    s.write(p(ember_rel))
    files[ember_rel] = dict(
        source="pi", project="ember", model="gpt-5.5", isSubagent=False, parentFile=None,
        phase="idle", inFlightStartMs=None, lastLineMs=ms("12:10:20.000"), malformedLines=1,
        responses=[
            resp(key(e1, ms("12:10:02.005")), ms("12:10:02.005"), ms("12:10:08.400"), 5200, 0, 0, 900, 300,
                 "gpt-5.5", "openai"),
            resp(key(e2, ms("12:10:08.436")), ms("12:10:08.436"), ms("12:10:11.560"), 5400, 5100, 0, 410, 0,
                 "gpt-5.5", "openai"),
        ],
        effort=effort("low", "selected"), speed=None,
        responseModes={
            key(e1, ms("12:10:02.005")): {"effort": effort("low", "selected"), "speed": None},
            key(e2, ms("12:10:08.436")): {"effort": effort("low", "selected"), "speed": None},
        })
    trace(ember_rel, 3, "generating", ms("12:10:02.000"))
    trace(ember_rel, 4, "tools")
    trace(ember_rel, 5, "generating", ms("12:10:08.430"))
    trace(ember_rel, 6, "idle")
    trace(ember_rel, 7, "idle")
    mtimes[ember_rel] = ms("12:00:03.000", NEXT_DAY)

    # ---- /fork of halo: a fresh header, the copied entries keep their ids and timestamps (copied history).
    fork_created = ms("12:20:00.000")
    fork_rel = "%s/%s/%s" % (SESSIONS, bucket(halo), session_name(fork_created, uuid7(fork_created, 34)))
    s = Session("d4c3")
    s.header(fork_created, uuid7(fork_created, 34), halo,
             parentSession="%s/.pi/agent/sessions/%s/%s" % (HOME, bucket(halo), halo_name))              # 1
    s.copy([row for row in halo_lines[1:8]])                                                         # 2-8 copies
    s.user(ms("12:20:05.000"), "Now add a test for the queue")                                       # 9
    f1 = s.assistant(ms("12:20:09.000"), OPUS, [text("Added test/queue.test.ts.")], usage(4, 330, 18600, 0),
                     "stop", ms("12:20:05.006"), duration=3990, level="medium")                      # 10
    s.write(p(fork_rel))
    files[fork_rel] = dict(
        source="pi", project="halo", model="claude-opus-5-5", isSubagent=False, parentFile=None,
        phase="idle", inFlightStartMs=None, lastLineMs=ms("12:20:09.000"), malformedLines=0,
        responses=[resp(key(f1, ms("12:20:05.006")), ms("12:20:05.006"), ms("12:20:08.996"), 18604, 18600, 0, 330,
                        0, "claude-opus-5-5", "anthropic")],
        effort=effort("medium", "recorded"), speed=None,
        responseModes={key(f1, ms("12:20:05.006")): {"effort": effort("medium", "recorded"), "speed": None}})
    trace(fork_rel, 8, "idle")
    trace(fork_rel, 9, "generating", ms("12:20:05.000"))
    mtimes[fork_rel] = ms("12:00:04.000", NEXT_DAY)

    # ---- The custom session folder of settings.json: session files directly in it, and a sub-agent folder.
    flat = HOME + "/projects/flat"
    flat_created = ms("12:30:00.000")
    flat_name = session_name(flat_created, uuid7(flat_created, 35))
    flat_rel = "%s/%s" % (CUSTOM, flat_name)
    s = Session("e5b4")
    s.header(flat_created, uuid7(flat_created, 35), flat)                                            # 1
    s.user(ms("12:30:02.000"), "Summarize the README")                                               # 2
    g1 = s.assistant(ms("12:30:04.100"), GPT, [text("It explains the build.")], usage(1200, 140, 0, 0),
                     "stop", ms("12:30:02.006"), duration=2090.2, level="minimal")                   # 3
    s.write(p(flat_rel))
    files[flat_rel] = dict(
        source="pi", project="flat", model="gpt-5.5", isSubagent=False, parentFile=None,
        phase="idle", inFlightStartMs=None, lastLineMs=ms("12:30:04.100"), malformedLines=0,
        responses=[resp(key(g1, ms("12:30:02.006")), ms("12:30:02.006"), ms("12:30:04.096"), 1200, 0, 0, 140, 0,
                        "gpt-5.5", "openai")],
        effort=effort("minimal", "recorded"), speed=None,
        responseModes={key(g1, ms("12:30:02.006")): {"effort": effort("minimal", "recorded"), "speed": None}})
    trace(flat_rel, 2, "generating", ms("12:30:02.000"))
    mtimes[flat_rel] = ms("12:00:05.000", NEXT_DAY)

    helper_rel = flat_rel[:-len(".jsonl")] + "/helper.jsonl"
    helper_created = ms("12:30:03.000")
    s = Session("f6a5")
    s.header(helper_created, uuid7(helper_created, 36), flat)                                        # 1
    s.user(ms("12:30:03.100"), "List the headings")                                                  # 2
    s.write(p(helper_rel))
    files[helper_rel] = dict(
        source="pi", project="flat", model=None, isSubagent=True, parentFile=flat_rel,
        phase="generating", inFlightStartMs=ms("12:30:03.100"), lastLineMs=ms("12:30:03.100"), malformedLines=0,
        responses=[], effort=None, speed=None, responseModes={})
    mtimes[helper_rel] = ms("12:00:06.000", NEXT_DAY)

    # ---- Discovery decoys: none of these is a session file.
    head = json.dumps(halo_lines[0], separators=(",", ":")) + "\n"
    decoys = {
        "%s/%s/.%s.tmp.jsonl" % (SESSIONS, bucket(halo), halo_name[:-len(".jsonl")]): head,
        "%s/.trash/%s" % (SESSIONS, halo_name): head,
        "%s/%s/notes.md" % (SESSIONS, bucket(halo)): "# notes\n",
        "%s/%s.bak" % (CUSTOM, flat_name): head,
        "pi/agent/auth.jsonl.example": head,
    }
    for rel, content in decoys.items():
        with open(p(rel), "w", encoding="utf-8", newline="\n") as fh:
            fh.write(content)

    order = sorted(mtimes, key=lambda rel: (mtimes[rel], rel))
    for rel in order:
        os.utime(p(rel), (mtimes[rel] / 1000, mtimes[rel] / 1000))
    return {
        "_comment": "Pi roots: %s and the custom session folder %s named by pi/agent/settings.json (sessionDir "
                    "~/pi/custom-sessions with the fixture folder as home). The fork copies halo's entries 2-8 with "
                    "their original timestamps under a fresh header (copied history, METRICS 4.7)."
                    % (SESSIONS, CUSTOM),
        "order": order,
        "mtimes": mtimes,
        "discover": sorted(files),
        "files": {rel: files[rel] for rel in order},
        "traces": traces,
    }


def main():
    if len(sys.argv) != 2:
        print(__doc__.strip())
        sys.exit(2)
    print(json.dumps(write_fixtures(sys.argv[1]), indent=2))


if __name__ == "__main__":
    main()
