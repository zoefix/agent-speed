#!/usr/bin/env python3
"""Inputs of spec/fixtures/perf-score-cases.json (METRICS 6.9, 6.11), with the expectations written by hand (`want`).

  python3 spec/tools/perf_score_cases.py --write     compute, check every `want`, then write the fixture

Every `want` is derived by hand from the section text: the measures and ratios are worked out in the comments and the
score is the weighted geometric mean of those ratios (`gm`, written out below, not taken from perf_score.py). Nothing
is written when one result differs.
"""
import json
import math
import os
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import perf_score  # noqa: E402
from thinking_cases import THINKING  # noqa: E402

T0 = 1_800_000_000_000          # 2027-01-15T08:00:00Z
DAY = "2027-01-15"
NEXT_DAY_MS = 1_800_057_600_000  # 2027-01-16T00:00:00Z
S = 1000
MIN = 60 * S
OPUS, SONNET, GPT = "claude-opus-5-5", "claude-sonnet-5-5", "gpt-6.1-sol"
W = {"T": 0.30, "L": 0.20, "N": 0.15, "R": 0.10, "H": 0.10, "U": 0.10, "S": 0.05}

COMMENT = (
    "METRICS 6.9 and 6.11, checked by spec/tools/perf_score.py and generated from spec/tools/perf_score_cases.py "
    "(--write), whose expectations are written by hand. Times are Unix ms; days are UTC dates here (the apps use local "
    "dates). `edges`: the integer latency edges E_0 … E_64 = ⌈100 · 2^(j/4)⌉. `latencyBins`: a latency in ms -> its "
    "bucket. `agents`: each agent's responses (engine form, 3.3) and stored actions (6.10, file order) -> at `nowMs` "
    "the 6.9 evaluation (form, unrounded score, shown, provisional, n, tools = |C|, activeMs, spanStartMs, model of the "
    "newest sample response, the seven components with presence, reason, samples, measure, ratio and sub-score; with "
    "`history` and `today` also the baseline, vsUsual and vsUsualPercent). `sets`: agents' values -> the value of the "
    "whole set and of every board group, with the supplying agent. `days`: each agent's completions in the order they "
    "complete -> the history records per (day, source, model). `pooled`: records of one source -> the pooled "
    "evaluation. `baseline`: records -> the 14-day baseline of (source, model) before `today`. `range`: records -> "
    "the History window's rows over [first, last] (one source when `source` is given) with the daily trend. "
    "`thinking`: the live thinking line (6.9 Live thinking). Numbers compare within 1e-9 (relative above 1).")


def gm(pairs):
    """1000 × the weighted geometric mean of (component id, ratio) pairs."""
    total = sum(W[c] for c, _ in pairs)
    return 1000 * math.exp(sum(W[c] * math.log(r) for c, r in pairs) / total)


def half(value):
    """Rounded half away from zero (every value here is positive)."""
    return math.floor(value + 0.5)


def resp(key, agent, start, end, output, source="claudeCode", model=OPUS, first=None, inp=1000, cached=900,
         reasoning=0, final=True):
    return {"key": key, "agentId": agent, "source": source, "model": model, "startMs": T0 + start, "endMs": T0 + end,
            "firstMs": None if first is None else T0 + first, "input": inp, "cachedInput": cached, "output": output,
            "reasoning": reasoning, "final": final}


def act(call, result, failed=False, model=OPUS, tool="Bash"):
    return {"tool": tool, "digest": "0" * 16, "callMs": T0 + call, "resultMs": None if result is None else T0 + result,
            "failed": failed, "model": model}


def agent(aid, source, phase, last, parent=None):
    return {"id": aid, "source": source, "phase": phase, "lastLineMs": T0 + last, "parentId": parent}


def comp(cid, ratio=None, reason=None, samples=None, measure=None):
    out = {"id": cid, "present": reason is None}
    if reason is not None:
        out.update(reason=reason, measure=None, ratio=None, subScore=None)
    else:
        out.update(ratio=ratio, subScore=1000 * ratio)
        if measure is not None:
            out["measure"] = measure
    if samples is not None:
        out["samples"] = samples
    return out


# ----------------------------------------------------------------------------- latency bins

def latency_bins():
    # E_1 = ⌈118.92⌉ = 119, E_13 = ⌈951.36⌉ = 952, E_14 = ⌈1131.37⌉ = 1132, E_62 = ⌈4634095.5⌉, E_63 = ⌈5510898.4⌉.
    cases = [(0, 0), (99, 0), (100, 0), (118, 0), (119, 1), (200, 4), (951, 12), (952, 13), (1000, 13), (1131, 13),
             (1132, 14), (5510898, 62), (5510899, 63), (6553599, 63), (6553600, 63), (10 ** 9, 63)]
    return [{"ms": ms, "want": k} for ms, k in cases]


# ----------------------------------------------------------------------------- agents

def case_all_components():
    """Five Claude responses of 4 s with 200 tokens each and four tool calls between them."""
    a1 = [agent("a1", "claudeCode", "tools", 44 * S)]
    lat = [1000, 2000, 2500, 3000, 4000]
    reasoning = [100, 120, 80, 0, 0]
    responses = [resp("r%d" % i, "a1", i * 10 * S, i * 10 * S + 4 * S, 200, first=i * 10 * S + lat[i], cached=950,
                      reasoning=reasoning[i]) for i in range(5)]
    actions = [act(-5 * S, -4 * S),                      # before the span: not in C
               act(4500, 6500), act(14500, 14900, failed=True), act(24500, 24600), act(34500, 34520),
               act(40500, None)]                         # no result yet: not in C
    # T = 1000 / 20 s = 50 -> 1; L = median(1000, 2000, 2500, 3000, 4000) = 2500 -> 2; R = 300 / 12 s = 25 -> 1.25;
    # H = 4750 / 5000 = 0.95 -> 1; C = 4 calls; active = 20 s of responses + 2 + 0.4 + 0.1 + 0.02 s = 22 520 ms;
    # N = 4 / (22 520 / 60 000) -> N / 3; U = (2000 · 400 · 100 · 50)^(1/4) -> 400 / U; S = 3/4 -> 0.75 / 0.95.
    n_rate = 4 / (22520 / 60000)
    u = (2000 * 400 * 100 * 50) ** 0.25
    ratios = [("T", 1.0), ("L", 2.0), ("N", n_rate / 3), ("R", 1.25), ("H", 1.0), ("U", 400 / u), ("S", 0.75 / 0.95)]
    score = gm(ratios)
    # History of (claudeCode, claude-opus-5-5): today−1 (12) and today−14 (8) count; today−15 and today do not.
    # Pooled: T = 20 000 / 400 s = 50 -> 1; L = the centre of bin 26 (all 20 values) -> 5000 / (100 · 2^(53/8)).
    today = "2027-01-15"
    history = [hist("2027-01-14", 12, 12000, 240000, {26: 12}), hist("2027-01-01", 8, 8000, 160000, {26: 8}),
               hist("2026-12-31", 30, 90000, 300000, {10: 30}), hist("2027-01-15", 30, 90000, 300000, {10: 30}),
               hist("2027-01-14", 40, 40000, 400000, {10: 40}, model=SONNET)]
    base = gm([("T", 1.0), ("L", 5000 / (100 * 2 ** (53 / 8)))])
    comps = [comp("T", 1.0, samples=5, measure=50.0), comp("L", 2.0, samples=5, measure=2500),
             comp("N", n_rate / 3, samples=4, measure=n_rate), comp("R", 1.25, samples=3, measure=25.0),
             comp("H", 1.0, samples=5, measure=0.95), comp("U", 400 / u, samples=4, measure=u),
             comp("S", 0.75 / 0.95, samples=4, measure=0.75)]
    return {"name": "every component present: five Claude responses, four tool calls in the span, a 14-day baseline",
            "agents": a1, "responses": responses, "actions": {"a1": actions}, "history": history, "today": today,
            "queries": [
                {"nowMs": T0 + 45 * S, "want": {"a1": {
                    "form": "live", "score": score, "shown": half(score), "provisional": True, "n": 5, "tools": 4,
                    "activeMs": 22520, "spanStartMs": T0, "model": OPUS, "components": comps, "baseline": base,
                    "vsUsual": score / base - 1, "vsUsualPercent": half(100 * (score / base - 1))}}},
                {"nowMs": T0 + 44 * S + 300_001, "note": "300 s without a line: idle, the same score rests",
                 "want": {"a1": {"form": "resting", "score": score, "tools": 4}}},
                {"nowMs": T0 + 44 * S + 30 * MIN + 1, "note": "every response left the 30-minute window",
                 "want": {"a1": {"form": "none", "score": None, "shown": None, "n": 0, "tools": 0, "baseline": None,
                                 "components": [comp("T", reason="fewSamples", samples=0)] + [
                                     comp(c, reason="fewSamples", samples=0) for c in "LNRHUS"]}}},
            ]}


def hist(day, n, output, duration, bins, source="claudeCode", model=OPUS, **extra):
    rec = dict(perf_score.empty_record(), day=day, source=source, model=model, n=n, outputSum=output,
               durationMsSum=duration)
    rec["latencyBins"] = [bins.get(k, 0) for k in range(64)]
    rec.update(extra)
    return rec


def case_low_clamps():
    """Codex at its minimum sample: T, U, S and H below their floors; L and R short of samples."""
    agents = [agent("c1", "codex", "generating", 4500)]
    responses = [resp("q1", "c1", 0, 100, 0, source="codex", model=GPT, first=50, inp=0, cached=0),
                 resp("q2", "c1", 1000, 2000, 0, source="codex", model=GPT, inp=1000, cached=0),
                 resp("q3", "c1", 3000, 4500, 1, source="codex", model=GPT, first=2900, inp=1000, cached=0)]
    actions = [act(0, 10 * S, True, GPT), act(0, 10 * S, True, GPT), act(0, 10 * S, True, GPT)]
    # q1 lasts 100 ms: widened to 500 (spanStart = −400). T = 1 / 3 s -> 1/150 -> floor 0.01. L: q1 only (q2 has no
    # first time, q3's is before its start) -> 1 sample. R: no reasoning. H: q2, q3 (q1 has no input) -> 2 samples.
    # C = 3 calls of 10 s, all failed: U = 10 000 -> 0.04 -> 0.05; S = 0 -> 0.05. active = [−400, 10 000] = 10 400 ms.
    n_rate = 3 / (10400 / 60000)
    score = gm([("T", 0.01), ("N", n_rate / 3), ("U", 0.05), ("S", 0.05)])
    return {"name": "lower clamps: T at 0.01, U and S at 0.05; L, R and H short of samples; n = 3 is enough",
            "agents": agents, "responses": responses, "actions": {"c1": actions},
            "queries": [{"nowMs": T0 + 20 * S, "want": {"c1": {
                "form": "live", "score": score, "provisional": True, "n": 3, "tools": 3, "activeMs": 10400,
                "spanStartMs": T0 - 400, "model": GPT,
                "components": [comp("T", 0.01, samples=3), comp("L", reason="fewSamples", samples=1),
                               comp("N", n_rate / 3, samples=3), comp("R", reason="fewSamples", samples=0),
                               comp("H", reason="fewSamples", samples=2), comp("U", 0.05, samples=3, measure=10000.0),
                               comp("S", 0.05, samples=3, measure=0.0)]}}},
                        {"nowMs": T0 + 9999, "note": "the calls end after nowMs: none is in C yet",
                         "want": {"c1": {"tools": 0, "activeMs": 3000,
                                         "score": gm([("T", 0.01)]),
                                         "components": [comp("T", 0.01), comp("L", reason="fewSamples"),
                                                        comp("N", reason="fewSamples", samples=0),
                                                        comp("R", reason="fewSamples"), comp("H", reason="fewSamples"),
                                                        comp("U", reason="fewSamples", samples=0),
                                                        comp("S", reason="fewSamples", samples=0)]}}}]}


def case_cap_and_ties():
    """22 Claude Desktop responses (newest 20 with a tie at the cut) and 70 stored actions (the last 64 count)."""
    agents = [agent("d1", "claudeDesktop", "idle", 1_000_000)]
    responses = [resp("r%02d" % i, "d1", i * 10 * S, i * 10 * S + 2 * S, 10000, source="claudeDesktop",
                      first=i * 10 * S + 100, inp=1000, cached=1000) for i in range(21)]
    responses.append(resp("q01", "d1", 10 * S, 12 * S, 30000, source="claudeDesktop", model=SONNET,
                          first=10 * S + 100, inp=1000, cached=1000))
    dropped = [act(20 * S, 20 * S + 100) for _ in range(6)]
    stored = [act(5 * S, 5 * S + 100),                       # before spanStart (10 s)
              act(30 * S, None),                             # no result
              act(30 * S, 30 * S + 900_001),                 # longer than 15 minutes
              act(40 * S, 40 * S + 900_000),                 # exactly 15 minutes: counts
              act(950 * S, 1_000_001)]                       # result after nowMs
    stored += [act(100 * S + j * S, 100 * S + j * S + 200, failed=j in (3, 30, 57)) for j in range(59)]
    # Sample: r20 … r02, then q01 before r01 (same end, key "q01" < "r01"): n = 20, spanStart = 10 s.
    # T = (19 · 10 000 + 30 000) / 40 s = 5500 -> 110 (no upper clamp); L = 100 -> max(100, 250) -> 20;
    # H = 1 -> 1/0.95; R none. C = the 15-minute call and 59 short ones = 60 (3 failed): S = 0.95 -> 1;
    # U = exp((ln 900 000 + 59 ln 200) / 60); active = [10,12] ∪ [20,22] ∪ [30,32] ∪ [40, 940] s = 906 000 ms.
    n_rate = 60 / (906000 / 60000)
    u = math.exp((math.log(900000) + 59 * math.log(200)) / 60)
    score = gm([("T", 110.0), ("L", 20.0), ("N", n_rate / 3), ("H", 1 / 0.95), ("U", 400 / u), ("S", 1.0)])
    return {"name": "newest 20 with a tie broken by key; the last 64 stored actions; the 15-minute limit; T above 20 "
                    "is not clamped; L below 250 ms counts as 250",
            "agents": agents, "responses": responses, "actions": {"d1": dropped + stored},
            "queries": [{"nowMs": T0 + 1_000_000, "want": {"d1": {
                "form": "resting", "score": score, "provisional": False, "n": 20, "tools": 60, "activeMs": 906000,
                "spanStartMs": T0 + 10 * S, "model": OPUS,
                "components": [comp("T", 110.0, measure=5500.0), comp("L", 20.0, measure=100),
                               comp("N", n_rate / 3), comp("R", reason="fewSamples", samples=0),
                               comp("H", 1 / 0.95, samples=20), comp("U", 400 / u), comp("S", 1.0, measure=0.95)]}}}]}


def case_high_clamps():
    """Three 500 ms Codex responses and ten 10 ms calls: N above its ceiling, U at its floor, H at zero."""
    agents = [agent("e1", "codex", "tools", 1500)]
    responses = [resp("e%d" % i, "e1", i * 500, i * 500 + 500, 100, source="codex", model=GPT, first=i * 500 + 300,
                      inp=1000, cached=0, reasoning=50) for i in range(3)]
    actions = [act(100, 110, model=GPT) for _ in range(10)]
    # T = 300 / 1.5 s = 200 -> 4; L = 300 -> 5000/300; N = 10 / (1500 / 60 000) = 400 -> 133.3 -> 20;
    # R = 150 / 1.5 s = 100 -> 5; H = 0 -> 0.05; U = 50 (10 ms floored to 50) -> 8; S = 1 -> 1/0.95.
    score = gm([("T", 4.0), ("L", 5000 / 300), ("N", 20.0), ("R", 5.0), ("H", 0.05), ("U", 8.0), ("S", 1 / 0.95)])
    return {"name": "upper clamps: N at 20; a 10 ms call counts as 50 ms; a cache hit of 0 at the 0.05 floor",
            "agents": agents, "responses": responses, "actions": {"e1": actions},
            "queries": [{"nowMs": T0 + 1500, "want": {"e1": {
                "form": "live", "score": score, "n": 3, "tools": 10, "activeMs": 1500,
                "components": [comp("T", 4.0), comp("L", 5000 / 300, measure=300), comp("N", 20.0, measure=400.0),
                               comp("R", 5.0, measure=100.0), comp("H", 0.05, measure=0.0),
                               comp("U", 8.0, measure=50.0), comp("S", 1 / 0.95)]}}}]}


def case_long_latency():
    """Four 400 s Claude responses: an even median far above the reference, clamped at 0.05."""
    agents = [agent("f1", "claudeCode", "generating", 1600 * S)]
    lat = [200_000, 300_000, 320_000, 350_000]
    responses = [resp("f%d" % i, "f1", i * 400 * S, (i + 1) * 400 * S, 20000, first=i * 400 * S + lat[i], inp=0,
                      cached=0) for i in range(4)]
    # T = 80 000 / 1600 s = 50 -> 1; L = (300 000 + 320 000) / 2 = 310 000 -> 0.0161 -> 0.05; H: no input.
    score = gm([("T", 1.0), ("L", 0.05)])
    return {"name": "an even count takes the mean of the two middle latencies; a slow first content clamps at 0.05",
            "agents": agents, "responses": responses, "actions": {},
            "queries": [{"nowMs": T0 + 1600 * S, "want": {"f1": {
                "score": score, "n": 4, "tools": 0,
                "components": [comp("T", 1.0), comp("L", 0.05, measure=310000.0),
                               comp("N", reason="fewSamples", samples=0), comp("R", reason="fewSamples", samples=0),
                               comp("H", reason="fewSamples", samples=0), comp("U", reason="fewSamples", samples=0),
                               comp("S", reason="fewSamples", samples=0)]}}}]}


def case_not_recorded():
    """Sources that do not record a component: Gemini (no latency, no actions), DeepSeek Harness (no reasoning apart
    from output), Hermes Agent (no reliable cache read)."""
    agents = [agent("g1", "gemini", "idle", 8 * S), agent("k1", "deepseek", "idle", 8 * S),
              agent("h1", "hermes", "idle", 8 * S)]
    responses = []
    for aid, source in (("g1", "gemini"), ("k1", "deepseek"), ("h1", "hermes")):
        responses += [resp("%s-%d" % (aid, i), aid, i * 3 * S, i * 3 * S + 2 * S, 100, source=source, model="m",
                           inp=1000, cached=500, reasoning=50) for i in range(3)]
    # Every agent: T = 300 / 6 s = 50 -> 1. R = 150 / 6 s = 25 -> 1.25 (not DeepSeek); H = 0.5 -> 0.5/0.95 (not
    # Hermes). No first-content time (4: these parsers never set one): L is not recorded, nor N, U, S without actions.
    na = "notRecorded"
    return {"name": "components a source does not record are left out, marked notRecorded",
            "agents": agents, "responses": responses, "actions": {},
            "queries": [{"nowMs": T0 + 9 * S, "want": {
                "g1": {"form": "resting", "score": gm([("T", 1.0), ("R", 1.25), ("H", 0.5 / 0.95)]),
                       "components": [comp("T", 1.0), comp("L", reason=na), comp("N", reason=na),
                                      comp("R", 1.25), comp("H", 0.5 / 0.95), comp("U", reason=na),
                                      comp("S", reason=na)]},
                "k1": {"score": gm([("T", 1.0), ("H", 0.5 / 0.95)]),
                       "components": [comp("T", 1.0), comp("L", reason=na), comp("N", reason=na), comp("R", reason=na),
                                      comp("H", 0.5 / 0.95), comp("U", reason=na), comp("S", reason=na)]},
                "h1": {"score": gm([("T", 1.0), ("R", 1.25)]),
                       "components": [comp("T", 1.0), comp("L", reason=na), comp("N", reason=na), comp("R", 1.25),
                                      comp("H", reason=na), comp("U", reason=na), comp("S", reason=na)]}}}]}


def case_provisional():
    """Ten responses: not provisional; one minute later the oldest has left the window and nine remain."""
    agents = [agent("p1", "claudeCode", "idle", 47 * S)]
    responses = [resp("p%d" % i, "p1", i * 5 * S, i * 5 * S + 2 * S, 100, first=i * 5 * S + 1000, inp=0, cached=0)
                 for i in range(10)]
    # T = 1000 / 20 s = 50 -> 1 (nine: 900 / 18 s, still 50); L = 1000 -> 5.
    score = gm([("T", 1.0), ("L", 5.0)])
    return {"name": "provisional below 10 responses; the window keeps a response whose end is exactly 30 minutes old",
            "agents": agents, "responses": responses, "actions": {},
            "queries": [{"nowMs": T0 + 2 * S + 30 * MIN, "want": {"p1": {"score": score, "n": 10,
                                                                          "provisional": False}}},
                        {"nowMs": T0 + 2 * S + 30 * MIN + 1, "want": {"p1": {"score": score, "n": 9,
                                                                              "provisional": True}}},
                        {"nowMs": T0 + 6 * S, "note": "endMs above nowMs is not read yet: one response",
                         "want": {"p1": {"score": None, "n": 1, "form": "none"}}}]}


def agents_cases():
    return [case_all_components(), case_low_clamps(), case_cap_and_ties(), case_high_clamps(), case_long_latency(),
            case_not_recorded(), case_provisional()]


# ----------------------------------------------------------------------------- sets and groups

def sets_cases():
    def v(form, score):
        return {"form": form, "score": score}
    tree = [agent("root", "claudeCode", "tools", 50 * S), agent("sub1", "claudeCode", "generating", 60 * S, "root"),
            agent("sub2", "claudeCode", "idle", 70 * S, "root"), agent("solo", "codex", "idle", 80 * S),
            agent("quiet", "gemini", "idle", 90 * S)]
    return [
        {"name": "the highest live value wins over a higher resting one; a group takes its members' highest",
         "agents": tree,
         "values": {"root": v("live", 812.5), "sub1": v("live", 1240.25), "sub2": v("resting", 4000.0),
                    "solo": v("resting", 9000.0), "quiet": v("none", None)},
         "want": {"all": {"form": "live", "score": 1240.25, "shown": 1240, "agent": "sub1"},
                  "groups": {"root": {"members": ["root", "sub1", "sub2"], "form": "live", "score": 1240.25,
                                      "agent": "sub1"},
                             "solo": {"members": ["solo"], "form": "resting", "score": 9000.0, "shown": 9000},
                             "quiet": {"members": ["quiet"], "form": "none", "score": None, "agent": None}}}},
        {"name": "ties: the latest lastLineMs supplies the value, then the smallest id (ordinal)",
         "agents": [agent("b", "codex", "tools", 10 * S), agent("a", "codex", "tools", 10 * S),
                    agent("c", "codex", "tools", 5 * S)],
         "values": {"a": v("live", 1000.5), "b": v("live", 1000.5), "c": v("live", 1000.5)},
         "want": {"all": {"form": "live", "score": 1000.5, "shown": 1001, "agent": "a"}}},
        {"name": "no live value: the highest resting one; nothing at all: none",
         "agents": [agent("x", "codex", "idle", 1), agent("y", "codex", "idle", 2)],
         "values": {"x": v("resting", 640.0), "y": v("resting", 12840.4)},
         "want": {"all": {"form": "resting", "score": 12840.4, "shown": 12840, "agent": "y"}}},
    ]


# ----------------------------------------------------------------------------- history records

def ev_resp(r):
    return {"response": r}


def ev_act(a):
    return {"action": a}


def days_cases():
    a = resp("A", "h1", 1000, 5000, 400, first=2000, inp=1000, cached=900, reasoning=100)
    b = resp("B", "h1", 5500, 6000, 50, final=False)
    c = resp("C", "h1", 6000, 9000, 300, first=6200, inp=2000, cached=1000)
    d = resp("D", "h1", 10 * S, 12 * S, 100, model=SONNET, first=10 * S + 1500, inp=0, cached=0)
    e_start = NEXT_DAY_MS - T0 - 1000
    e = resp("E", "h1", e_start, e_start + 2000, 80, first=e_start + 150, inp=500, cached=500)
    k = resp("K", "k1", 0, 2000, 100, source="deepseek", model="deepseek-v4", first=None, inp=800, cached=600,
             reasoning=50)
    events = [ev_resp(a), ev_act(act(5000, 7000, failed=True)), ev_resp(b), ev_resp(c),
              ev_act(act(2000, 3000)), ev_act(act(0, 9500)), ev_resp(d), ev_act(act(20 * S, 20 * S + 900_001)),
              ev_act(act(30 * S, None)), ev_act(act(40 * S, 40 * S + 20, model=None)), ev_resp(e)]
    # opus, DAY: A (n 1, out 400, 4000 ms, latency 1000 -> bin 13, reasoning 100 over 4000 ms, cache 900/1000) and C
    # (3000 ms, latency 200 -> bin 4, cache 1000/2000). Calls: 2000 ms (failed), 1000 ms, 9500 ms.
    # activeMs (online, opus): A adds 4000 (cu 5000); the 5000–7000 call adds 2000 (cu 7000); C adds 9000 − 7000 = 2000
    # (cu 9000); the 2000–3000 call adds 0; the 0–9500 call adds 500 (the exact union would add 1500): 8500.
    # sonnet: D adds 2000. The model-less call (20 ms) goes to model "" and adds 20. E ends the next day: its own
    # record there, active 2000.
    opus_day = dict(perf_score.empty_record(), n=2, outputSum=700, durationMsSum=7000, reasonN=1, reasonOutput=100,
                    reasonDurationMsSum=4000, cacheN=2, cacheInput=3000, cacheCached=1900, toolN=3,
                    toolLnSum=math.log(2000) + math.log(1000) + math.log(9500), toolFailed=1, activeMs=8500)
    opus_day["latencyBins"] = [1 if k in (4, 13) else 0 for k in range(64)]
    none_day = dict(perf_score.empty_record(), toolN=1, toolLnSum=math.log(50), activeMs=20)
    sonnet_day = dict(perf_score.empty_record(), n=1, outputSum=100, durationMsSum=2000, activeMs=2000)
    sonnet_day["latencyBins"] = [1 if k == 15 else 0 for k in range(64)]   # 1500 ms: E_15 = 1346 ≤ 1500 < E_16 = 1600
    next_day = dict(perf_score.empty_record(), n=1, outputSum=80, durationMsSum=2000, cacheN=1, cacheInput=500,
                    cacheCached=500, activeMs=2000)
    next_day["latencyBins"] = [1 if k == 2 else 0 for k in range(64)]      # 150 ms: E_2 = 142 ≤ 150 < E_3 = 169
    # DeepSeek: reasoning is not separate (no R sums); its cache counts; no first time.
    k_day = dict(perf_score.empty_record(), n=1, outputSum=100, durationMsSum=2000, cacheN=1, cacheInput=800,
                 cacheCached=600, activeMs=2000)
    return [{"name": "records per day, source and model: measured responses only, calls within 15 minutes, the "
                     "online union rule for activeMs, a response ending after midnight, a model-less call",
             "agents": [{"id": "h1", "source": "claudeCode", "events": events},
                        {"id": "k1", "source": "deepseek", "events": [ev_resp(k)]}],
             "want": [dict(day=DAY, source="claudeCode", model="", **none_day),
                      dict(day=DAY, source="claudeCode", model=OPUS, **opus_day),
                      dict(day=DAY, source="claudeCode", model=SONNET, **sonnet_day),
                      dict(day=DAY, source="deepseek", model="deepseek-v4", **k_day),
                      dict(day="2027-01-16", source="claudeCode", model=OPUS, **next_day)]}]


# ----------------------------------------------------------------------------- pooled, baseline, range

def pooled_cases():
    full = [hist(DAY, 15, 15000, 300000, {10: 2, 12: 1, 20: 2}, reasonN=3, reasonOutput=600,
                 reasonDurationMsSum=30000, cacheN=15, cacheInput=15000, cacheCached=14250, toolN=2,
                 toolLnSum=2 * math.log(400), toolFailed=0, activeMs=300000),
            hist("2027-01-16", 5, 5000, 100000, {}, toolN=2, toolLnSum=2 * math.log(400), toolFailed=1,
                 activeMs=100000)]
    # Σ n = 20: T = 20 000 / 400 s = 50 -> 1; latencies 5: the 3rd value is in bin 12 -> 5000 / (100 · 2^(25/8));
    # R = 600 / 30 s = 20 -> 1; H = 14 250 / 15 000 = 0.95 -> 1; tools 4: N = 4 / (400 000 / 60 000) = 0.6 -> 0.2;
    # U = 400 -> 1; S = 3/4 -> 0.75/0.95.
    l_ratio = 5000 / (100 * 2 ** (25 / 8))
    score = gm([("T", 1.0), ("L", l_ratio), ("N", 0.2), ("R", 1.0), ("H", 1.0), ("U", 1.0), ("S", 0.75 / 0.95)])
    even = [hist(DAY, 20, 40000, 400000, {3: 1, 7: 1, 9: 2})]
    # Four latencies: the 2nd value (⌈4/2⌉) is in bin 7. T = 100 -> 2.
    return [
        {"name": "pooled sums over days: the median latency bin, components with enough samples, Σ n = 20",
         "source": "claudeCode", "records": full,
         "want": {"n": 20, "tools": 4, "activeMs": 400000, "score": score, "shown": half(score),
                  "components": [comp("T", 1.0, measure=50.0, samples=20),
                                 comp("L", l_ratio, measure=100 * 2 ** (25 / 8), samples=5),
                                 comp("N", 0.2, measure=0.6), comp("R", 1.0), comp("H", 1.0),
                                 comp("U", 1.0, measure=400.0), comp("S", 0.75 / 0.95)]}},
        {"name": "an even count takes the ⌈count/2⌉-th value",
         "source": "codex", "records": even,
         "want": {"score": gm([("T", 2.0), ("L", 5000 / (100 * 2 ** (15 / 8)))]),
                  "components": [comp("T", 2.0), comp("L", 5000 / (100 * 2 ** (15 / 8)), samples=4),
                                 comp("N", reason="fewSamples", samples=0), comp("R", reason="fewSamples"),
                                 comp("H", reason="fewSamples"), comp("U", reason="fewSamples"),
                                 comp("S", reason="fewSamples")]}},
        {"name": "Σ n = 19: the components are shown, there is no score",
         "source": "claudeCode", "records": [hist(DAY, 19, 19000, 380000, {12: 19})],
         "want": {"n": 19, "score": None, "shown": None,
                  "components": [comp("T", 1.0, samples=19), comp("L", 5000 / (100 * 2 ** (25 / 8)), samples=19),
                                 comp("N", reason="fewSamples"), comp("R", reason="fewSamples"),
                                 comp("H", reason="fewSamples"), comp("U", reason="fewSamples"),
                                 comp("S", reason="fewSamples")]}},
        {"name": "a source without latency or actions: notRecorded, as in the live score",
         "source": "gemini", "records": [hist(DAY, 30, 30000, 600000, {}, source="gemini", model="gemini-3-pro")],
         "want": {"score": 1000.0, "components": [comp("T", 1.0), comp("L", reason="notRecorded"),
                                                  comp("N", reason="notRecorded"), comp("R", reason="fewSamples"),
                                                  comp("H", reason="fewSamples"), comp("U", reason="notRecorded"),
                                                  comp("S", reason="notRecorded")]}},
    ]


def baseline_cases():
    rows = [hist("2027-01-14", 12, 12000, 240000, {}), hist("2027-01-01", 7, 7000, 140000, {}),
            hist("2026-12-31", 30, 90000, 300000, {}), hist("2027-01-15", 30, 90000, 300000, {}),
            hist("2027-01-10", 30, 60000, 300000, {}, model=SONNET), hist("2027-01-10", 30, 60000, 300000, {},
                                                                          source="codex")]
    return [
        {"name": "the 14 days before today, this source and model only: Σ n = 19, no baseline",
         "records": rows, "source": "claudeCode", "model": OPUS, "today": "2027-01-15", "want": None},
        {"name": "one more record inside the 14 days: Σ n = 20, T = 20 000 / 400 s = 50",
         "records": rows + [hist("2027-01-05", 1, 1000, 20000, {})], "source": "claudeCode", "model": OPUS,
         "today": "2027-01-15", "want": 1000.0},
        {"name": "another model of the same source has its own baseline (T = 60 000 / 300 s = 200 -> 4)",
         "records": rows, "source": "claudeCode", "model": SONNET, "today": "2027-01-15", "want": 4000.0},
    ]


def range_cases():
    rows = [hist("2027-01-10", 25, 25000, 500000, {}), hist("2027-01-11", 5, 10000, 100000, {}),
            hist("2027-01-12", 20, 40000, 400000, {}), hist("2027-01-11", 40, 40000, 400000, {}, source="codex",
                                                             model=GPT),
            hist("2027-01-09", 50, 50000, 1000000, {})]
    # claudeCode/opus over 10–12: Σ n 50, T = 75 000 / 1000 s = 75 -> 1.5; days: 25 (T 50), 5 (gap), 20 (T 100).
    # codex/gpt: one day, n 40, T = 100 -> 2. Rows in source order: codex first.
    trend = [{"day": "2027-01-10", "score": 1000.0}, {"day": "2027-01-11", "score": None},
             {"day": "2027-01-12", "score": 2000.0}]
    return [
        {"name": "per source and model over the range; a day below 20 responses is a gap in the trend",
         "records": rows, "first": "2027-01-10", "last": "2027-01-12",
         "want": [{"source": "codex", "model": GPT, "n": 40, "score": 2000.0,
                   "trend": [{"day": "2027-01-10", "score": None}, {"day": "2027-01-11", "score": 2000.0},
                             {"day": "2027-01-12", "score": None}]},
                  {"source": "claudeCode", "model": OPUS, "n": 50, "score": 1500.0, "trend": trend}]},
        {"name": "one source chosen in the window's source picker",
         "records": rows, "first": "2027-01-10", "last": "2027-01-12", "source": "claudeCode",
         "want": [{"source": "claudeCode", "model": OPUS, "n": 50, "score": 1500.0}]},
    ]


def cases():
    return {"latencyBins": latency_bins(), "agents": agents_cases(), "sets": sets_cases(), "days": days_cases(),
            "pooled": pooled_cases(), "baseline": baseline_cases(), "range": range_cases(), "thinking": THINKING}


# ----------------------------------------------------------------------------- checking and writing

def subset(want, got, label, failures):
    if isinstance(want, dict):
        if not isinstance(got, dict):
            failures.append("%s: expected %r got %r" % (label, want, got))
            return
        for k, v in want.items():
            if k not in got:
                failures.append("%s: missing %s" % (label, k))
            else:
                subset(v, got[k], label + "." + k, failures)
    elif isinstance(want, list) and isinstance(got, list) and len(want) == len(got) and any(
            isinstance(w, dict) for w in want):
        for i, (w, g) in enumerate(zip(want, got)):
            subset(w, g, "%s[%d]" % (label, i), failures)
    elif not perf_score.close(want, got):
        failures.append("%s: expected %r got %r" % (label, want, got))


def strip_want(value):
    if isinstance(value, dict):
        return {k: strip_want(v) for k, v in value.items() if k != "want"}
    if isinstance(value, list):
        return [strip_want(v) for v in value]
    return value


def hand_check(computed, data):
    failures = []
    for j, e in ((1, 119), (4, 200), (13, 952), (14, 1132), (8, 400), (16, 1600), (63, 5510899), (64, 6553600)):
        if computed["edges"][j] != e:
            failures.append("edges[%d]: expected %d got %d" % (j, e, computed["edges"][j]))
    for section, items in data.items():
        if section == "thinking":
            for kind, kind_cases in items.items():
                for i, case in enumerate(kind_cases):
                    for j, q in enumerate(case["queries"]):
                        if "want" in q:
                            subset(q["want"], computed["thinking"][kind][i]["queries"][j],
                                   "thinking %s %s #%d" % (kind, case["name"], j), failures)
            continue
        for i, case in enumerate(items):
            label = "%s %s" % (section, case.get("name", case.get("ms", i)))
            if "want" in case:
                subset(case["want"], computed[section][i]["expect"], label, failures)
            for j, q in enumerate(case.get("queries", [])):
                if "want" in q:
                    subset(q["want"], computed[section][i]["queries"][j]["expect"], "%s #%d" % (label, j), failures)
    return failures


def main():
    data = cases()
    computed = perf_score.compute(strip_want(data))
    failures = hand_check(computed, data)
    if failures:
        print("\n".join(failures))
        return 1
    if "--write" in sys.argv:
        with open(perf_score.FIXTURE, "w", encoding="utf-8") as f:
            json.dump(dict({"_comment": COMMENT}, **computed), f, indent=1, ensure_ascii=False)
            f.write("\n")
        print("wrote", perf_score.FIXTURE, perf_score.counts(computed))
    else:
        print("hand-written expectations match the reference", perf_score.counts(computed))
    return 0


if __name__ == "__main__":
    sys.exit(main())
