#!/usr/bin/env python3
"""Compares `--dump-today` (and `--dump-history`) outputs from different implementations row by row.

  python3 spec/tools/compare_dumps.py reference.jsonl swift.jsonl csharp.jsonl

Responses that end within --grace-ms of the newest dump's capture time are ignored, because the dumps are taken
seconds apart while agents keep writing. The performance score rows (METRICS 6.9, 6.11) are compared whenever both
dumps have them: `perf` rows of `--dump-today` per agent (skipped for agents written to within the grace period), the
`perfSets` row only when both dumps were evaluated at the same `nowMs` (`--now`), and the `perf` history rows of
`--dump-history` per day, source and model (skipped for days that end within the grace period). Numbers compare within
1e-9 (relative above 1).
"""
import argparse
import json
import sys

FIELDS = ("source", "agentId", "model", "startMs", "endMs", "input", "cachedInput", "cacheWriteInput", "output",
          "reasoning", "final")
# Added with the cost feature (METRICS 3.2, 9): compared whenever the other dump has them.
NEW_FIELDS = ("cacheWrite1hInput", "provider", "fast", "costMicroUsd", "requestCount", "firstMs")
# Reasoning effort and speed (EXECUTION-MODES): compared only when both dumps carry them.
MODE_FIELDS = ("effort", "speed")


class Dump:
    def __init__(self, path):
        self.path = path
        self.rows, self.summary, self.perf, self.sets, self.history = {}, None, {}, None, {}
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                obj = json.loads(line)
                if "summary" in obj:
                    self.summary = obj["summary"]
                elif "perf" in obj and "agentId" in obj["perf"]:
                    self.perf[obj["perf"]["agentId"]] = obj["perf"]
                elif "perf" in obj:
                    row = obj["perf"]
                    self.history[(row["day"], row["source"], row["model"])] = row
                elif "perfSets" in obj:
                    self.sets = obj["perfSets"]
                elif "key" in obj:
                    self.rows[obj["key"]] = obj

    def newest_response(self):
        return max((r["endMs"] for r in self.rows.values()), default=0)

    def newest(self):
        ends = [r["endMs"] for r in self.rows.values()]
        ends += [t["lastLineMs"] for t in self.perf.values() if t.get("lastLineMs") is not None]
        return max(ends, default=0)


def close(a, b, tolerance):
    if isinstance(a, bool) or isinstance(b, bool) or a is None or b is None or isinstance(a, str):
        return a == b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return abs(a - b) <= tolerance * max(1.0, abs(a), abs(b))
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(close(a[k], b[k], tolerance) for k in a)
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(close(x, y, tolerance) for x, y in zip(a, b))
    return a == b


def differing(a, b, tolerance, label):
    """Paths where two JSON values differ."""
    if isinstance(a, dict) and isinstance(b, dict):
        out = []
        for k in sorted(set(a) | set(b)):
            if k not in a or k not in b:
                out.append("%s.%s present on one side only" % (label, k))
            else:
                out += differing(a[k], b[k], tolerance, "%s.%s" % (label, k))
        return out
    if isinstance(a, list) and isinstance(b, list) and len(a) == len(b):
        out = []
        for i, (x, y) in enumerate(zip(a, b)):
            out += differing(x, y, tolerance, "%s[%d]" % (label, i))
        return out
    return [] if close(a, b, tolerance) else ["%s: %r != %r" % (label, a, b)]


def compare_responses(base, other, cutoff):
    missing = [k for k, r in base.rows.items() if k not in other.rows and r["endMs"] < cutoff]
    extra = [k for k, r in other.rows.items() if k not in base.rows and r["endMs"] < cutoff]
    diffs = []
    for key, ref in base.rows.items():
        row = other.rows.get(key)
        if row is None or ref["endMs"] >= cutoff:
            continue
        for field in (FIELDS + tuple(f for f in NEW_FIELDS if f in row)
                      + tuple(f for f in MODE_FIELDS if f in row and f in ref)):
            if ref.get(field) != row.get(field):
                diffs.append("%s %s: %r != %r" % (key, field, ref.get(field), row.get(field)))
    return missing, extra, diffs


def compare_perf(base, other, cutoff):
    if not base.perf and base.sets is None or not other.perf and other.sets is None:
        return [], "perf rows: not in both dumps"
    diffs, notes = [], []
    for aid in sorted(set(base.perf) | set(other.perf)):
        a, b = base.perf.get(aid), other.perf.get(aid)
        last = max((t.get("lastLineMs") or 0) for t in (a, b) if t is not None)
        if last >= cutoff:
            continue
        if a is None or b is None:
            diffs.append("perf %s: in one dump only" % aid)
            continue
        diffs += ["perf " + d for d in differing(a, b, 1e-9, aid)]
    now_a, now_b = (base.summary or {}).get("nowMs"), (other.summary or {}).get("nowMs")
    if base.sets is not None and other.sets is not None:
        if now_a is not None and now_a == now_b:
            diffs += ["perfSets " + d for d in differing(base.sets, other.sets, 1e-9, "sets")]
        else:
            notes.append("perfSets not compared (nowMs %r vs %r; pass --now to both)" % (now_a, now_b))
    return diffs, "; ".join(notes)


def compare_history(base, other, cutoff):
    if not base.history or not other.history:
        return [], ""
    from datetime import datetime, timezone
    recent = datetime.fromtimestamp(cutoff / 1000, tz=timezone.utc).strftime("%Y-%m-%d")
    diffs = []
    for key in sorted(set(base.history) | set(other.history)):
        if key[0] >= recent:
            continue
        a, b = base.history.get(key), other.history.get(key)
        if a is None or b is None:
            diffs.append("history %s: in one dump only" % (key,))
            continue
        diffs += ["history " + d for d in differing(a, b, 1e-9, "/".join(key))]
    return diffs, ""


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("dumps", nargs="+")
    parser.add_argument("--grace-ms", type=int, default=120_000)
    args = parser.parse_args()
    loaded = [Dump(p) for p in args.dumps]
    response_cutoff = max(d.newest_response() for d in loaded) - args.grace_ms
    cutoff = max(d.newest() for d in loaded) - args.grace_ms
    base = loaded[0]
    failures = 0
    for d in loaded:
        print("%-60s responses=%d perf=%d history=%d summary=%s" % (d.path[-60:], len(d.rows), len(d.perf),
                                                                      len(d.history), json.dumps(d.summary)[:120]))
    for other in loaded[1:]:
        missing, extra, diffs = compare_responses(base, other, response_cutoff)
        perf_diffs, perf_note = compare_perf(base, other, cutoff)
        history_diffs, history_note = compare_history(base, other, cutoff)
        failures += len(missing) + len(extra) + len(diffs) + len(perf_diffs) + len(history_diffs)
        print("\n%s vs %s: missing=%d extra=%d field differences=%d perf differences=%d history differences=%d" % (
            base.path, other.path, len(missing), len(extra), len(diffs), len(perf_diffs), len(history_diffs)))
        for note in (perf_note, history_note):
            if note:
                print("  note   " + note)
        for key in missing[:10]:
            print("  missing", key)
        for key in extra[:10]:
            print("  extra  ", key)
        for diff in (diffs + perf_diffs + history_diffs)[:30]:
            print("  diff   " + diff)
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
