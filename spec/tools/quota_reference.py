#!/usr/bin/env python3
"""Reference implementation of subscription quotas (spec/QUOTAS.md).

Standard library only, plus the `zstd` and `brotli` command-line tools for compressed cache bodies. It implements
the window model, the Codex reader, the Claude Desktop cache entry reader, the Claude CLI status line helper and its
settings edits, and the text formats; it writes and checks spec/fixtures/quota-cases.json and spec/fixtures/quota/.

    python3 spec/tools/quota_reference.py --check-fixtures
    python3 spec/tools/quota_reference.py --write
"""

from __future__ import annotations

import argparse
import copy
import gzip
import hashlib
import json
import math
import os
import re
import shutil
import struct
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from email.utils import parsedate_to_datetime
from pathlib import Path
from zoneinfo import ZoneInfo

sys.path.insert(0, str(Path(__file__).resolve().parent))
import quota_vendors as vendors  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
CASES = ROOT / "spec" / "fixtures" / "quota-cases.json"
ENTRIES = ROOT / "spec" / "fixtures" / "quota"
STRINGS = ROOT / "spec" / "strings.json"

HEADER_MAGIC = 0xFCFB6D1BA7725C30
EOF_MAGIC = 0xF4FA6F45970D41D8
ENTRY_VERSION = 5
EOF_SIZE = 24
HEADER_SIZE = 24
KEY_SHA_FLAG = 0x2
ENTRY_LIMIT = 1 << 20
BODY_LIMIT = 1 << 20
USAGE_URL = re.compile(r"^https://claude\.ai/api/organizations/"
                       r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/usage(\?.*)?$")
KEY_PREFIX = re.compile(r"^\d+/\d+/")
HELPER_NAME = "agent-speed-statusline"
SAME_WINDOW_MS = 60_000
KIND_ORDER = {"fiveHour": 0, "other": 1, "sevenDay": 2, "modelWeek": 3, "month": 4, "credits": 5}
LANGS = ("zh-Hant", "zh-Hans", "en", "ja")
WEEKDAYS = {
    "zh-Hant": ("週一", "週二", "週三", "週四", "週五", "週六", "週日"),
    "zh-Hans": ("周一", "周二", "周三", "周四", "周五", "周六", "周日"),
    "en": ("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"),
    "ja": ("月", "火", "水", "木", "金", "土", "日"),
}
HOUR_MS = 3_600_000
DAY_MS = 24 * HOUR_MS


# ---------- model (section 2) ----------

def window(kind, used, resets_ms, name=None, minutes=None):
    minutes = minutes if minutes is not None else {"fiveHour": 300, "sevenDay": 10080, "modelWeek": 10080}.get(kind)
    return {"kind": kind, "name": name, "minutes": minutes, "usedPercent": used, "resetsAtMs": resets_ms}


def base_label(w, lang):
    kind = w["kind"]
    if kind == "fiveHour":
        return "5h"
    if kind in ("sevenDay", "modelWeek"):
        return "7d"
    if kind == "month":
        return string("quota.window.month", lang)
    if kind == "credits":
        return string("quota.window.credits", lang)
    minutes = w["minutes"]
    if minutes % 1440 == 0:
        return f"{minutes // 1440}d"
    if minutes % 60 == 0:
        return f"{minutes // 60}h"
    return f"{minutes}m"


def label(w, lang="en"):
    base = base_label(w, lang)
    return f"{w['name']} {base}" if w.get("name") else base


def ordered(windows):
    def length(w):
        return (math.inf if w["minutes"] is None or w["kind"] == "credits" else w["minutes"], KIND_ORDER[w["kind"]])
    unnamed = sorted((w for w in windows if not w.get("name")), key=length)
    names = []
    for w in windows:
        if w.get("name") and w["name"] not in names:
            names.append(w["name"])
    named = [w for n in names for w in sorted((x for x in windows if x.get("name") == n), key=length)]
    return unnamed + named


def percent(used):
    value = math.floor(float(used) + 0.5)
    return max(0, min(100, value))


def shown(snapshot, now_ms):
    """Windows as displayed: ordered, reset ones at 0 % with no reset time (section 2)."""
    out = []
    for w in ordered(snapshot["windows"]):
        passed = w["resetsAtMs"] is not None and now_ms >= w["resetsAtMs"]
        out.append({"label": label(w), "percent": 0 if passed else percent(w["usedPercent"]),
                    "bar": 0.0 if passed else max(0.0, min(100.0, float(w["usedPercent"]))),
                    "resetsAtMs": None if passed else w["resetsAtMs"], "passed": passed})
    return out


# ---------- Codex (section 3) ----------

def is_number(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v)


def parse_ts(text):
    return int(datetime.fromisoformat(text.replace("Z", "+00:00")).timestamp() * 1000)


def codex_kind(minutes):
    if 290 <= minutes <= 310:
        return "fiveHour"
    if 10000 <= minutes <= 10160:
        return "sevenDay"
    return "other"


def codex_windows(rate_limits, ts_ms):
    limit_id = rate_limits.get("limit_id")
    if limit_id not in (None, "codex"):
        return []
    found = {}
    for slot in ("primary", "secondary"):
        w = rate_limits.get(slot)
        if not isinstance(w, dict) or not is_number(w.get("used_percent")) or not is_number(w.get("window_minutes")):
            continue
        minutes = int(w["window_minutes"])
        if minutes <= 0:
            continue
        kind = codex_kind(minutes)
        if is_number(w.get("resets_at")):
            resets = int(w["resets_at"]) * 1000
        elif is_number(w.get("resets_in_seconds")):
            resets = ts_ms + int(w["resets_in_seconds"] * 1000)
        else:
            resets = None
        slot_key = (kind, minutes if kind == "other" else None)
        if slot_key not in found:
            found[slot_key] = window(kind, float(w["used_percent"]), resets,
                                     minutes=minutes if kind == "other" else None)
    return list(found.values())


def codex_snapshot(lines):
    best = None
    for line in lines:
        if line.get("copied"):
            continue
        obj = line["line"]
        payload = obj.get("payload") or {}
        if obj.get("type") != "event_msg" or payload.get("type") != "token_count":
            continue
        rl = payload.get("rate_limits")
        if not isinstance(rl, dict):
            continue
        ts = parse_ts(obj["timestamp"])
        windows = codex_windows(rl, ts)
        if not windows:
            continue
        if best is None or ts > best["observedMs"]:
            best = {"source": "codex", "observedMs": ts, "windows": windows}
    return best


# ---------- Claude Desktop cache entries (section 4) ----------

def entry_file_name(key):
    digest = hashlib.sha1(key.encode("utf-8")).digest()[:8]
    return "%016x_0" % struct.unpack("<Q", digest)[0]


def zstd(data, decompress):
    args = ["zstd", "-q", "-c", "-d"] if decompress else ["zstd", "-q", "-c", "-19", "--no-check"]
    return subprocess.run(args, input=data, capture_output=True, check=True).stdout


def brotli(data, decompress):
    args = ["brotli", "-c", "-d"] if decompress else ["brotli", "-c", "-q", "11"]
    return subprocess.run(args, input=data, capture_output=True, check=True).stdout


def build_entry(key, body, headers, status="HTTP/1.1 200", key_sha=True, version=ENTRY_VERSION):
    raw_key = key.encode("utf-8")
    info = (status + "\0" + "\0".join(f"{k}:{v}" for k, v in headers) + "\0\0").encode("latin-1")
    stream0 = struct.pack("<I", len(info)) + info + b"\x04\0\0\0"
    head = struct.pack("<QIIII", HEADER_MAGIC, version, len(raw_key), 0, 0)
    eof1 = struct.pack("<QIIII", EOF_MAGIC, 1, 0, 0, 0)
    sha = hashlib.sha256(raw_key).digest() if key_sha else b""
    eof0 = struct.pack("<QIIII", EOF_MAGIC, 1 | (KEY_SHA_FLAG if key_sha else 0), 0, len(stream0), 0)
    return head + raw_key + body + eof1 + stream0 + sha + eof0


def entry_url(key):
    if " " in key:
        return key.rsplit(" ", 1)[1]
    return KEY_PREFIX.sub("", key, count=1)


def response_info(stream0):
    start = stream0.find(b"HTTP/")
    if start < 0:
        return None
    lines = []
    for part in stream0[start:].split(b"\0"):
        if not part:
            break
        lines.append(part.decode("latin-1"))
    if not lines:
        return None
    status = lines[0].split(" ")
    code = int(status[1]) if len(status) > 1 and status[1].isdigit() else None
    headers = {}
    for line in lines[1:]:
        if ":" in line:
            name, value = line.split(":", 1)
            headers.setdefault(name.strip().lower(), value.strip())
    return code, headers


def decode_body(body, encoding):
    encoding = (encoding or "identity").strip().lower()
    try:
        if encoding == "zstd":
            out = zstd(body, True)
        elif encoding == "gzip":
            out = gzip.decompress(body)
        elif encoding == "br":
            out = brotli(body, True)
        elif encoding == "identity":
            out = body
        else:
            return None
    except (OSError, subprocess.CalledProcessError, EOFError):
        return None
    if len(out) > BODY_LIMIT:
        return None
    try:
        value = json.loads(out)
    except ValueError:
        return None
    return value if isinstance(value, dict) else None


def parse_entry(data, mtime_ms):
    """Returns {"url", "observedMs", "body"} for a usage entry, else None."""
    if len(data) > ENTRY_LIMIT or len(data) < HEADER_SIZE + 2 * EOF_SIZE:
        return None
    magic, version, key_len, _, _ = struct.unpack_from("<QIIII", data, 0)
    if magic != HEADER_MAGIC or version != ENTRY_VERSION:
        return None
    key_end = HEADER_SIZE + key_len
    if key_end > len(data):
        return None
    try:
        key = data[HEADER_SIZE:key_end].decode("utf-8")
    except UnicodeDecodeError:
        return None
    url = entry_url(key)
    if not USAGE_URL.match(url):
        return None
    eof0_at = len(data) - EOF_SIZE
    m0, flags0, _, size0, _ = struct.unpack_from("<QIIII", data, eof0_at)
    if m0 != EOF_MAGIC:
        return None
    stream0_end = eof0_at - (32 if flags0 & KEY_SHA_FLAG else 0)
    stream0_start = stream0_end - size0
    eof1_at = stream0_start - EOF_SIZE
    if eof1_at < key_end:
        return None
    m1 = struct.unpack_from("<Q", data, eof1_at)[0]
    if m1 != EOF_MAGIC:
        return None
    info = response_info(data[stream0_start:stream0_end])
    if info is None or info[0] != 200:
        return None
    code, headers = info
    body = decode_body(data[key_end:eof1_at], headers.get("content-encoding"))
    if body is None:
        return None
    observed = mtime_ms
    if "date" in headers:
        try:
            observed = int(parsedate_to_datetime(headers["date"]).timestamp() * 1000)
        except (TypeError, ValueError):
            observed = mtime_ms
    return {"url": url, "observedMs": observed, "body": body}


def iso_ms(text):
    if not isinstance(text, str):
        return None
    try:
        return int(datetime.fromisoformat(text.replace("Z", "+00:00")).timestamp() * 1000)
    except ValueError:
        return None


def desktop_windows(body):
    out = []
    for field, kind in (("five_hour", "fiveHour"), ("seven_day", "sevenDay")):
        w = body.get(field)
        if isinstance(w, dict) and is_number(w.get("utilization")):
            out.append(window(kind, float(w["utilization"]), iso_ms(w.get("resets_at"))))
    names = set()
    limits = body.get("limits")
    if isinstance(limits, list):
        for item in limits:
            if not isinstance(item, dict) or item.get("kind") != "weekly_scoped" or not is_number(item.get("percent")):
                continue
            scope = item.get("scope")
            model = scope.get("model") if isinstance(scope, dict) else None
            name = model.get("display_name") if isinstance(model, dict) else None
            if isinstance(name, str) and name.strip() and name.casefold() not in names:
                names.add(name.casefold())
                out.append(window("modelWeek", float(item["percent"]), iso_ms(item.get("resets_at")), name=name))
    for field, name in (("seven_day_opus", "Opus"), ("seven_day_sonnet", "Sonnet")):
        w = body.get(field)
        if isinstance(w, dict) and is_number(w.get("utilization")) and name.casefold() not in names:
            names.add(name.casefold())
            out.append(window("modelWeek", float(w["utilization"]), iso_ms(w.get("resets_at")), name=name))
    return out


def desktop_snapshot(entries):
    """entries: [(file name, bytes, mtime_ms)]. The newest observation wins (4.4)."""
    best = None
    for name, data, mtime in entries:
        parsed = parse_entry(data, mtime)
        if parsed is None:
            continue
        rank = (parsed["observedMs"], mtime)
        if best is None or rank > best[0]:
            best = (rank, name, parsed)
    if best is None:
        return None
    _, name, parsed = best
    return {"source": "claudeDesktop", "observedMs": parsed["observedMs"], "file": name,
            "windows": desktop_windows(parsed["body"])}


# ---------- Claude CLI bridge (section 5) ----------

def helper_merge(stored, status_input, now_ms):
    stored = copy.deepcopy(stored) if stored else {"version": 1, "receivedMs": None, "windows": {}}
    rl = status_input.get("rate_limits") if isinstance(status_input, dict) else None
    if not isinstance(rl, dict):
        return stored
    changed = False
    for field in ("five_hour", "seven_day"):
        new = rl.get(field)
        if not isinstance(new, dict) or not is_number(new.get("used_percentage")) or not is_number(new.get("resets_at")):
            continue
        new = {"used_percentage": new["used_percentage"], "resets_at": new["resets_at"]}
        old = stored["windows"].get(field)
        if old is None or new["resets_at"] * 1000 - old["resets_at"] * 1000 > SAME_WINDOW_MS:
            take = new
        elif abs(new["resets_at"] - old["resets_at"]) * 1000 <= SAME_WINDOW_MS:
            take = new if new["used_percentage"] > old["used_percentage"] else old
        else:
            take = old
        if take is not old:
            stored["windows"][field] = take
            changed = True
    if changed:
        stored["receivedMs"] = now_ms
    return stored


def cli_snapshot(stored):
    windows = []
    for field, kind in (("five_hour", "fiveHour"), ("seven_day", "sevenDay")):
        w = stored["windows"].get(field)
        if w:
            windows.append(window(kind, float(w["used_percentage"]), int(w["resets_at"] * 1000)))
    return {"source": "claudeCode", "observedMs": stored["receivedMs"], "windows": windows}


def helper_line(stored, now_ms):
    parts = [f"{w['label']} {w['percent']}%" for w in shown(cli_snapshot(stored), now_ms) if not w["passed"]]
    return " · ".join(parts)


def posix_command(path):
    return "'" + path.replace("'", "'\\''") + "'"


def windows_command(path):
    return '"' + path.replace("\\", "/") + '"'


def is_ours(value):
    return (isinstance(value, dict) and value.get("type") == "command" and isinstance(value.get("command"), str)
            and HELPER_NAME in value["command"])


def settings_text(obj):
    return json.dumps(obj, indent=2, ensure_ascii=False) + "\n"


def bridge_on(text, command, previous_sidecar=None):
    """Returns {"status", "settings", "previous"}; settings None means the file is left alone."""
    if text is None:
        current = {}
    else:
        try:
            current = json.loads(text)
        except ValueError:
            return {"status": "unreadable", "settings": None, "previous": previous_sidecar}
        if not isinstance(current, dict):
            return {"status": "unreadable", "settings": None, "previous": previous_sidecar}
    existing = current.get("statusLine")
    if is_ours(existing):
        previous = previous_sidecar
    else:
        previous = {"version": 1, "statusLine": existing} if "statusLine" in current else {"version": 1}
    line = {"type": "command", "command": command}
    old = previous.get("statusLine") if previous else None
    if isinstance(old, dict) and is_number(old.get("padding")):
        line["padding"] = old["padding"]
    current["statusLine"] = line
    return {"status": "on", "settings": settings_text(current), "previous": previous}


def bridge_off(text, previous):
    if text is None:
        return {"status": "off", "settings": None}
    try:
        current = json.loads(text)
    except ValueError:
        return {"status": "off", "settings": None}
    if not isinstance(current, dict) or not is_ours(current.get("statusLine")):
        return {"status": "off", "settings": None}
    if previous and "statusLine" in previous:
        current["statusLine"] = previous["statusLine"]
    else:
        current.pop("statusLine", None)
    return {"status": "off", "settings": settings_text(current)}


def bridge_status(text, has_quota_file):
    try:
        current = json.loads(text) if text is not None else {}
    except ValueError:
        return "unreadable"
    if not isinstance(current, dict):
        return "unreadable"
    if not is_ours(current.get("statusLine")):
        return "replaced"
    return "ok" if has_quota_file else "waiting"


# ---------- formatting (section 7) ----------

_STRINGS = None


def string(key, lang):
    global _STRINGS
    if _STRINGS is None:
        _STRINGS = json.loads(STRINGS.read_text(encoding="utf-8"))
    return _STRINGS[key][lang]


def fmt(template, *args):
    out, i = [], 0
    for token in re.split(r"(%d|%@|%%)", template):
        if token in ("%d", "%@"):
            out.append(str(args[i]))
            i += 1
        elif token == "%%":
            out.append("%")
        else:
            out.append(token)
    return "".join(out)


def duration_long(ms, lang):
    minutes = ms // 60_000
    if ms < HOUR_MS:
        return fmt(string("quota.duration.m", lang), minutes)
    hours, rest = divmod(minutes, 60)
    if rest == 0:
        return fmt(string("quota.duration.h", lang), hours)
    return fmt(string("quota.duration.hm", lang), hours, rest)


def local(ms, tz):
    return datetime.fromtimestamp(ms / 1000, tz)


def weekday_or_date(resets_ms, now_ms, lang, tz):
    at, now = local(resets_ms, tz), local(now_ms, tz)
    if at.weekday() == now.weekday():
        return f"{at.month}/{at.day}"
    return WEEKDAYS[lang][at.weekday()]


def reset_long(resets_ms, now_ms, lang, tz):
    d = resets_ms - now_ms
    if d < DAY_MS:
        return fmt(string("quota.reset.in", lang), duration_long(max(0, d), lang))
    at = local(resets_ms, tz)
    return fmt(string("quota.reset.at", lang), f"{weekday_or_date(resets_ms, now_ms, lang, tz)} {at:%H:%M}")


def reset_short(resets_ms, now_ms, lang, tz):
    d = resets_ms - now_ms
    if d < HOUR_MS:
        return f"{max(0, d) // 60_000}m"
    if d < DAY_MS:
        return f"{d // HOUR_MS}h"
    return weekday_or_date(resets_ms, now_ms, lang, tz)


def age_text(observed_ms, now_ms, lang):
    age = now_ms - observed_ms
    if age < HOUR_MS:
        return None
    if age < DAY_MS:
        return fmt(string("quota.age", lang), fmt(string("quota.duration.h", lang), age // HOUR_MS))
    return fmt(string("quota.age", lang), fmt(string("quota.duration.d", lang), age // DAY_MS))


def pace(w, now_ms):
    if w["resetsAtMs"] is None or now_ms >= w["resetsAtMs"] or not w["minutes"]:
        return None
    share = 1 - (w["resetsAtMs"] - now_ms) / (w["minutes"] * 60_000)
    return max(0.0, min(1.0, share))


# ---------- fixtures (section 10) ----------

ORG = "0fba5d0f-43ab-4376-8ca3-f41e01ec4623"
USAGE_KEY = f"1/0/https://claude.ai/api/organizations/{ORG}/usage"
DATE = "Tue, 10 Nov 2026 19:26:13 GMT"
ULTRA_BODY = {
    "five_hour": None,
    "seven_day": {"utilization": 94.0, "resets_at": "2026-11-11T06:59:59.589678+00:00", "limit_dollars": None},
    "seven_day_opus": None, "seven_day_sonnet": None, "amber_cistern": {"x": 1},
    "limits": [
        {"kind": "weekly_all", "percent": 94, "utilization": None, "resets_at": "2026-11-11T06:59:59.589678+00:00",
         "scope": None},
        {"kind": "weekly_scoped", "percent": 27, "utilization": None, "resets_at": "2026-11-11T06:59:59.589834+00:00",
         "scope": {"model": {"id": None, "display_name": "Fable"}, "surface": None}},
    ],
}
MAX_BODY = {
    "five_hour": {"utilization": 23.5, "resets_at": "2026-11-10T22:00:00+00:00"},
    "seven_day": {"utilization": 41.2, "resets_at": "2026-11-14T01:00:00Z"},
    "seven_day_opus": {"utilization": 12.0, "resets_at": "2026-11-14T01:00:00Z"},
}


def json_bytes(obj):
    return json.dumps(obj, separators=(",", ":")).encode("utf-8")


def entry_specs():
    headers = [("date", DATE), ("content-type", "application/json"), ("content-encoding", "zstd")]
    plain = [("date", DATE), ("content-type", "application/json")]
    gz = [("date", "Tue, 10 Nov 2026 19:43:25 GMT"), ("content-encoding", "gzip")]
    br = [("date", "Tue, 10 Nov 2026 19:10:00 GMT"), ("content-encoding", "br")]
    return [
        ("zstd-ultra", USAGE_KEY, zstd(json_bytes(ULTRA_BODY), False), headers, {}),
        ("identity-max", USAGE_KEY + "?skip_spend=1", json_bytes(MAX_BODY), plain, {}),
        ("gzip-max", USAGE_KEY + "?at_wall=1&skip_spend=1", gzip.compress(json_bytes(MAX_BODY), mtime=0), gz, {}),
        ("br-ultra", USAGE_KEY + "?cedar_ember=1&skip_spend=1", brotli(json_bytes(ULTRA_BODY), False), br, {}),
        ("partitioned", f"1/0/_dk_https://claude.ai https://claude.ai {USAGE_KEY[4:]}", json_bytes(MAX_BODY), plain, {}),
        ("status-304", USAGE_KEY + "?x=1", json_bytes(MAX_BODY), plain, {"status": "HTTP/1.1 304"}),
        ("other-url", f"1/0/https://claude.ai/api/organizations/{ORG}/members", json_bytes(MAX_BODY), plain, {}),
        ("no-key-sha", USAGE_KEY + "?y=1", json_bytes(MAX_BODY), plain, {"key_sha": False}),
        ("version-4", USAGE_KEY + "?z=1", json_bytes(MAX_BODY), plain, {"version": 4}),
        ("unknown-encoding", USAGE_KEY + "?w=1", json_bytes(MAX_BODY), [("content-encoding", "compress")], {}),
    ]


def write_entries():
    ENTRIES.mkdir(parents=True, exist_ok=True)
    for old in ENTRIES.glob("*.bin"):
        old.unlink()
    for name, key, body, headers, opts in entry_specs():
        data = build_entry(key, body, headers, **opts)
        (ENTRIES / f"{name}.bin").write_bytes(data)
    good = (ENTRIES / "identity-max.bin").read_bytes()
    (ENTRIES / "truncated.bin").write_bytes(good[:-10])
    (ENTRIES / "bad-magic.bin").write_bytes(b"\0" + good[1:])


def entry_bytes(name):
    return (ENTRIES / f"{name}.bin").read_bytes()


CODEX_BASE = {"limit_id": "codex", "limit_name": None, "credits": {"has_credits": False}, "plan_type": "promax"}


def codex_line(ts, primary=None, secondary=None, **extra):
    rl = dict(CODEX_BASE, primary=primary, secondary=secondary, **extra)
    return {"timestamp": ts, "type": "event_msg", "payload": {"type": "token_count", "info": {}, "rate_limits": rl}}


def build_cases(now_ms):
    tz = ZoneInfo("Asia/Taipei")
    cases = {"version": 1, "now": now_ms, "timeZone": "Asia/Taipei"}

    week = {"used_percent": 23.0, "window_minutes": 10080, "resets_at": 1794897479}
    five = {"used_percent": 6.0, "window_minutes": 300, "resets_at": 1794354000}
    codex = [
        ("weekOnly", [{"line": codex_line("2026-11-10T19:11:22.318Z", week)}]),
        ("bothWindows", [{"line": codex_line("2026-11-10T19:11:22.318Z", five, week)}]),
        ("zeroWindowSkipped", [{"line": codex_line("2026-11-03T11:32:36.462Z",
                                                   {"used_percent": 0.0, "window_minutes": 0, "resets_at": 1}, week)}]),
        ("premiumIgnored", [{"line": codex_line("2026-11-10T19:12:00Z", week, limit_id="premium")}]),
        ("nullWindowsReplaceNothing", [{"line": codex_line("2026-11-10T19:00:00Z", week)},
                                       {"line": codex_line("2026-11-10T19:30:00Z", None, None)}]),
        ("legacyResetsIn", [{"line": codex_line("2025-09-01T10:00:00Z",
                                                {"used_percent": 2.0, "window_minutes": 299, "resets_in_seconds": 15212},
                                                {"used_percent": 30.5, "window_minutes": 10079,
                                                 "resets_in_seconds": 400000})}]),
        ("newestWins", [{"line": codex_line("2026-11-10T19:30:00Z", dict(week, used_percent=25.0))},
                        {"line": codex_line("2026-11-10T19:00:00Z", dict(week, used_percent=24.0))}]),
        ("copiedIgnored", [{"line": codex_line("2026-11-10T19:00:00Z", week)},
                           {"copied": True, "line": codex_line("2026-11-10T19:40:00Z", dict(week, used_percent=99.0))}]),
        ("otherLength", [{"line": codex_line("2026-11-10T19:00:00Z",
                                             {"used_percent": 50.0, "window_minutes": 1440, "resets_at": 1794400000})}]),
    ]
    cases["codex"] = [{"name": n, "lines": lines, "expected": codex_snapshot(lines)} for n, lines in codex]

    cases["entries"] = []
    names = [s[0] for s in entry_specs()] + ["truncated", "bad-magic"]
    for name in names:
        parsed = parse_entry(entry_bytes(name), 1794338773000)
        cases["entries"].append({"file": f"{name}.bin",
                                 "expected": None if parsed is None else {
                                     "url": parsed["url"], "observedMs": parsed["observedMs"],
                                     "windows": desktop_windows(parsed["body"])}})
    cases["fileName"] = {"key": USAGE_KEY, "expected": entry_file_name(USAGE_KEY)}
    mtime = 1794338773000
    group = [(f"{n}.bin", entry_bytes(n), mtime) for n in ("zstd-ultra", "identity-max", "gzip-max", "br-ultra")]
    cases["desktopNewest"] = {"files": [g[0] for g in group], "mtimeMs": mtime, "expected": desktop_snapshot(group)}

    stored0 = None
    steps = [
        {"rate_limits": {"five_hour": {"used_percentage": 6, "resets_at": 1794354000},
                         "seven_day": {"used_percentage": 86, "resets_at": 1794448800}}},
        {"rate_limits": {"seven_day": {"used_percentage": 83, "resets_at": 1794448800}}},
        {"rate_limits": {"seven_day": {"used_percentage": 88, "resets_at": 1794448830}}},
        {"model": {"id": "claude-opus-5-5"}},
        {"rate_limits": {"five_hour": {"used_percentage": 2, "resets_at": 1794372000}}},
        {"rate_limits": {"five_hour": {"used_percentage": 40, "resets_at": 1794336000}}},
    ]
    merges, stored = [], stored0
    for i, step in enumerate(steps):
        at = now_ms + i * 60_000
        stored = helper_merge(stored, step, at)
        merges.append({"input": step, "nowMs": at, "expected": stored, "line": helper_line(stored, at)})
    cases["helper"] = merges
    cases["helperExpired"] = {"stored": stored, "nowMs": 1794372000000, "line": helper_line(stored, 1794372000000)}

    mac_path = "/Users/me/Library/Application Support/AgentSpeed/bin/agent-speed-statusline"
    odd_path = "/Users/o'neil/Library/Application Support/AgentSpeed/bin/agent-speed-statusline"
    win_path = "C:\\Users\\me\\AppData\\Local\\AgentSpeed\\bin\\agent-speed-statusline.exe"
    cmd = posix_command(mac_path)
    cases["commands"] = {"posix": [[mac_path, cmd], [odd_path, posix_command(odd_path)]],
                         "windows": [[win_path, windows_command(win_path)]]}
    existing = settings_text({"env": {}, "statusLine": {"type": "command", "command": "~/bin/line.sh", "padding": 0},
                              "model": "opus"})
    ours_text = bridge_on(existing, cmd)["settings"]
    plain = settings_text({"env": {}, "permissions": {"allow": ["Bash"]}, "model": "opus"})
    bridge = []
    for name, text, sidecar in (("noFile", None, None), ("noStatusLine", plain, None), ("withPadding", existing, None),
                                ("alreadyOurs", ours_text, {"version": 1, "statusLine": {"type": "command",
                                                                                          "command": "~/bin/line.sh",
                                                                                          "padding": 0}}),
                                ("notAnObject", "[1, 2]\n", None), ("brokenJson", "{\"env\": \n", None)):
        on = bridge_on(text, cmd, sidecar)
        off = bridge_off(on["settings"], on["previous"]) if on["settings"] else None
        bridge.append({"name": name, "settings": text, "sidecar": sidecar, "on": on, "off": off})
    replaced = settings_text({"env": {}, "statusLine": {"type": "command", "command": "npx ccstatusline"}})
    bridge.append({"name": "replacedByUser", "settings": replaced, "sidecar": {"version": 1},
                   "on": None, "off": bridge_off(replaced, {"version": 1})})
    cases["bridge"] = bridge
    cases["bridgeStatus"] = [
        {"settings": ours_text, "quotaFile": False, "expected": bridge_status(ours_text, False)},
        {"settings": ours_text, "quotaFile": True, "expected": bridge_status(ours_text, True)},
        {"settings": replaced, "quotaFile": True, "expected": bridge_status(replaced, True)},
        {"settings": "nope", "quotaFile": False, "expected": bridge_status("nope", False)},
    ]

    snap = {"source": "x", "observedMs": now_ms, "windows": [
        window("sevenDay", 94.0, 1794380399589), window("modelWeek", 27.0, 1794380399589, name="Fable"),
        window("fiveHour", 103.4, now_ms - 1000), window("other", 49.5, None, minutes=1440),
        window("modelWeek", 12.0, None, name="Opus")]}
    cases["shown"] = {"snapshot": snap, "expected": shown(snap, now_ms)}

    texts = []
    offsets = [5 * 60_000, 42 * 60_000 + 59_000, 3 * HOUR_MS, 3 * HOUR_MS + 12 * 60_000 + 30_000,
               23 * HOUR_MS + 59 * 60_000, 2 * DAY_MS + 5 * HOUR_MS, 6 * DAY_MS + 23 * HOUR_MS]
    for off in offsets:
        resets = now_ms + off
        texts.append({"resetsAtMs": resets,
                      "long": {lang: reset_long(resets, now_ms, lang, tz) for lang in LANGS},
                      "short": {lang: reset_short(resets, now_ms, lang, tz) for lang in LANGS}})
    cases["resetTexts"] = texts
    cases["ageTexts"] = [{"observedMs": now_ms - a, "expected": {lang: age_text(now_ms - a, now_ms, lang)
                                                                  for lang in LANGS}}
                         for a in (30 * 60_000, HOUR_MS, 5 * HOUR_MS + 59 * 60_000, 3 * DAY_MS + HOUR_MS)]
    cases["pace"] = [{"window": w, "expected": pace(w, now_ms)} for w in (
        window("fiveHour", 10.0, now_ms + 2 * HOUR_MS), window("sevenDay", 10.0, now_ms + 7 * DAY_MS + 1000),
        window("sevenDay", 10.0, None), window("fiveHour", 10.0, now_ms - 1))]
    label_windows = (window("fiveHour", 1, None), window("sevenDay", 1, None),
                     window("modelWeek", 1, None, name="Fable"), window("other", 1, None, minutes=1440),
                     window("other", 1, None, minutes=120), window("other", 1, None, minutes=45),
                     window("month", 1, None, minutes=43200), window("credits", 1, None),
                     window("fiveHour", 1, None, name="Gemini"), window("month", 1, None, name="MCP", minutes=43200))
    cases["labels"] = [[w, {lang: label(w, lang) for lang in LANGS}] for w in label_windows]
    mixed = [window("month", 5, None, minutes=43200), window("sevenDay", 4, None, name="Claude/GPT"),
             window("credits", 3, None), window("fiveHour", 2, None, name="Gemini"), window("sevenDay", 1, None),
             window("fiveHour", 6, None, name="Claude/GPT"), window("other", 7, None, minutes=1440),
             window("sevenDay", 8, None, name="Gemini"), window("fiveHour", 9, None)]
    cases["order"] = {"windows": mixed, "expected": [label(w) for w in ordered(mixed)]}
    cases["vendors"] = vendor_cases(now_ms)
    cases["percents"] = [[v, percent(v)] for v in (0, 0.49, 0.5, 22.5, 99.6, 100, 103.4, -2)]
    return cases


def vendor_cases(now_ms):
    v = vendors
    iso = "2026-11-12T00:00:00Z"
    grok_log = [
        "partial line",
        json.dumps({"ts": "2026-11-10T10:00:00Z", "msg": "billing: fetched credits config",
                    "ctx": {"config": {"creditUsagePercent": 12.5, "currentPeriod": {"type": "PERIOD_TYPE_WEEKLY",
                                                                                      "start": "2026-11-08T00:00:00Z",
                                                                                      "end": "2026-11-15T00:00:00Z"}}}}),
        json.dumps({"ts": "2026-11-10T12:00:00Z", "msg": "billing: fetched credits config",
                    "ctx": {"config": {"isUnifiedBillingUser": True,
                                       "currentPeriod": {"type": "PERIOD_TYPE_MONTHLY", "start": "2026-11-01T00:00:00Z",
                                                         "end": "2026-12-01T00:00:00Z"}}}}),
        json.dumps({"ts": "2026-11-10T11:00:00Z", "msg": "other"}),
    ]
    grok_api = {"config": {"creditUsagePercent": 64, "billingPeriodEnd": iso}}
    devin_rows = [("windsurf.reactSettings.cachedPlanInfoData:user-a", json.dumps({
        "endTimestamp": 1790000000, "dailyRemainingPercent": 90, "dailyResetAtUnix": 1794400000,
        "weeklyRemainingPercent": 10, "weeklyResetAtUnix": 1794800000000})),
        ("windsurf.reactSettings.cachedPlanInfoData:user-b", json.dumps({
            "endTimestamp": 1800000000, "dailyRemainingPercent": 75, "dailyResetAtUnix": 1794400000,
            "hideDailyQuota": False, "weeklyRemainingPercent": 40, "weeklyResetAtUnix": 1794800000,
            "hideWeeklyQuota": True, "accountIdentityText": "x"})), ("other", "not json")]
    antigravity = {"response": {"groups": [
        {"displayName": "Gemini models", "buckets": [
            {"bucketId": "gemini_5h", "remainingFraction": 0.75, "resetTime": "2026-11-10T22:00:00Z"},
            {"window": "WEEKLY", "remainingFraction": {"case": "remainingFraction", "value": 0.4},
             "resetTime": iso}]},
        {"displayName": "Claude and GPT (third party)", "buckets": [
            {"displayName": "Five hour", "remaining": {"remainingFraction": 1.0}},
            {"bucketId": "weekly", "remainingFraction": 0.1, "disabled": True}]},
        {"displayName": "Gemini 4 Argon", "buckets": [{"window": "7d", "remainingFraction": 0.95,
                                                         "resetTime": 1794800000}]}]}}
    antigravity_status = {"userStatus": {"cascadeModelConfigData": {"clientModelConfigs": [
        {"label": "Gemini 3 Pro", "quotaInfo": {"remainingFraction": 0.6, "resetTime": "2026-11-10T22:00:00Z"}},
        {"label": "x", "quotaInfo": {}}]}}}
    kimi_new = {"usages": {"limit_5h": {"used_ratio": 0.25, "reset_time": "2026-11-10T22:00:00Z"},
                           "limit_7d": {"used_ratio": 0.5, "reset_time": iso},
                           "limit_month_total": {"used_ratio": 0.125, "reset_time": "2026-12-01T00:00:00Z"},
                           "limit_month": {"used_ratio": 0.9}}}
    kimi_old = {"limits": [{"window": {"duration": "300", "timeUnit": "TIME_UNIT_MINUTE"},
                            "detail": {"limit": "200", "remaining": "150", "resetTime": "2026-11-10T22:00:00Z"}},
                           {"window": {"duration": 1, "timeUnit": "TIME_UNIT_DAY"},
                            "detail": {"limit": 100, "used": 10}}],
                "usage": {"limit": "2048", "used": "512", "reset_at": iso}}
    gemini_load = {"cloudaicompanionProject": {"id": "proj-123"}}
    gemini = {"buckets": [{"modelId": "gemini-3-pro", "remainingFraction": 0.8, "resetTime": iso},
                          {"modelId": "gemini-3-pro-preview", "remainingFraction": 0.6, "resetTime": iso},
                          {"modelId": "gemini-3-flash", "remainingFraction": 1.0, "resetTime": iso},
                          {"modelId": "gemini-3-flash-lite", "remainingFraction": 0.95},
                          {"modelId": "imagen-4", "remainingFraction": 0.5}]}
    droid = {"limits": {"standard": {"fiveHour": {"usedPercent": 20, "secondsRemaining": 3600},
                                     "weekly": {"usedPercent": 55, "windowEnd": 1794800000},
                                     "monthly": {"usedPercent": 70, "windowEnd": "2026-11-01T00:00:00Z"}},
                        "core": {"weekly": {"usedPercent": 5, "windowEnd": 1794800000000}}}}
    zcode = {"success": True, "code": 200, "data": {"limits": [
        {"type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 30, "nextResetTime": now_ms + 2 * 3600000},
        {"type": "TOKENS_LIMIT", "unit": 6, "number": 1, "usage": 1000, "currentValue": 250, "percentage": 99,
         "nextResetTime": now_ms + 5 * 86400000},
        {"type": "TIME_LIMIT", "unit": 5, "number": 1, "percentage": 40, "nextResetTime": now_ms + 20 * 86400000},
        {"type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 1, "nextResetTime": now_ms + 400 * 60000}]}}
    qoder = {"totalQuota": {"quotaSummary": {"usedValue": 30, "limitValue": 100}},
             "sharedQuota": {"quotaSummary": {"used_value": "20", "limit_value": "100"}},
             "next_reset_at": "2026-12-01T00:00:00+08:00"}
    qoder_pct = {"totalQuota": {"quotaSummary": {"usagePercentage": 42}}, "nextResetAt": 1796000000}
    workbuddy = {"code": 0, "data": {"Packages": [
        {"CycleTotalCapacity": "1000.00", "CycleRemainCapacity": "250.5", "CapacityUnit": "credits"},
        {"CycleTotalCapacity": "500", "CycleRemainCapacity": "500", "CapacityUnit": "credits"},
        {"CycleTotalCapacity": "9", "CycleRemainCapacity": "1", "CapacityUnit": "minutes"}]}}
    mac_blob = v.chrome_cbc_encrypt(b"qoder-session-1", "mac-safe-storage-password", 1003)
    mac_blob24 = v.chrome_cbc_encrypt(b"qoder-session-2", "mac-safe-storage-password", 1003, host=".qoder.com")
    linux10 = v.chrome_cbc_encrypt(b"wb-session", "peanuts", 1)
    linux11 = v.chrome_cbc_encrypt(b"wb-session-11", "libsecret-password", 1, prefix=b"v11", host="www.workbuddy.cn")
    cookie_rows = [(".qoder.com", "sid", "a1", 0), ("qoder.com", "csrf", "b2", now_ms // 1000 + 60),
                   ("old.qoder.com", "gone", "x", now_ms // 1000 - 60), ("notqoder.com", "evil", "y", 0),
                   ("api.qoder.com", "region", "sg", 0)]
    return {
        "grokLog": {"lines": grok_log, "expected": v.grok_from_log(grok_log)},
        "grokApi": {"body": grok_api, "expected": v.grok_windows(grok_api["config"])},
        "grokUnknown": {"config": {"currentPeriod": {"type": "WEEKLY"}}, "expected": v.grok_windows(
            {"currentPeriod": {"type": "WEEKLY"}})},
        "devin": {"rows": devin_rows, "expected": v.devin_windows(devin_rows)},
        "antigravity": {"body": antigravity, "expected": v.antigravity_windows(antigravity)},
        "antigravityStatus": {"body": antigravity_status, "expected": v.antigravity_status_windows(antigravity_status)},
        "kimiCurrent": {"body": kimi_new, "expected": v.kimi_windows(kimi_new)},
        "kimiLegacy": {"body": kimi_old, "expected": v.kimi_windows(kimi_old)},
        "geminiProject": {"body": gemini_load, "expected": v.gemini_project(gemini_load)},
        "gemini": {"body": gemini, "expected": v.gemini_windows(gemini)},
        "droidEnv": [[text, v.droid_key_from_env_file(text)] for text in (
            "# comment\nOTHER=1\nexport FACTORY_API_KEY=\"fk-abc\"\n", "FACTORY_API_KEY=fk-plain", "NOPE=1")],
        "droid": {"body": droid, "nowMs": now_ms, "expected": v.droid_windows(droid, now_ms)},
        "zcode": {"body": zcode, "nowMs": now_ms, "expected": v.zcode_windows(zcode, now_ms)},
        "zcodeFailure": {"body": {"success": False, "code": 401}, "expected": v.zcode_windows(
            {"success": False, "code": 401}, now_ms)},
        "qoder": {"body": qoder, "expected": v.qoder_windows(qoder)},
        "qoderPercent": {"body": qoder_pct, "expected": v.qoder_windows(qoder_pct)},
        "workbuddy": {"body": workbuddy, "expected": v.workbuddy_windows(workbuddy)},
        "userAgent": v.chrome_user_agent("Macintosh; Intel Mac OS X 10_15_7", 142),
        "cookies": {
            "macV10": {"password": "mac-safe-storage-password", "iterations": 1003, "metaVersion": 23,
                       "blob": mac_blob.hex(), "expected": v.chrome_cbc_decrypt(mac_blob, "mac-safe-storage-password",
                                                                                 1003, 23)},
            "macV10HostHash": {"password": "mac-safe-storage-password", "iterations": 1003, "metaVersion": 24,
                               "host": ".qoder.com", "blob": mac_blob24.hex(),
                               "expected": v.chrome_cbc_decrypt(mac_blob24, "mac-safe-storage-password", 1003, 24)},
            "linuxV10": {"password": "peanuts", "iterations": 1, "metaVersion": 23, "blob": linux10.hex(),
                         "expected": v.chrome_cbc_decrypt(linux10, "peanuts", 1, 23)},
            "linuxV11HostHash": {"password": "libsecret-password", "iterations": 1, "metaVersion": 24,
                                 "host": "www.workbuddy.cn", "blob": linux11.hex(),
                                 "expected": v.chrome_cbc_decrypt(linux11, "libsecret-password", 1, 24)},
            "windowsGcm": v.GCM_VECTOR,
        },
        "cookieHeader": {"rows": cookie_rows, "site": "qoder.com", "nowS": now_ms // 1000,
                         "expected": v.cookie_header(cookie_rows, "qoder.com", now_ms // 1000)},
    }


NOW_MS = 1794338773000  # 2026-11-10 19:26:13 UTC = 2026-11-11 03:26:13 Asia/Taipei


def check():
    stored = json.loads(CASES.read_text(encoding="utf-8"))
    fresh = json.loads(json.dumps(build_cases(stored["now"]), ensure_ascii=False))
    problems = [k for k in fresh if fresh[k] != stored.get(k)]
    for k in stored:
        if k not in fresh:
            problems.append(k)
    if problems:
        print("quota fixtures differ: " + ", ".join(problems), file=sys.stderr)
        return 1
    print(f"quota fixtures OK ({sum(len(v) if isinstance(v, list) else 1 for v in fresh.values())} items)")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check-fixtures", action="store_true")
    parser.add_argument("--write", action="store_true")
    args = parser.parse_args()
    for tool in ("zstd", "brotli", "openssl"):
        if shutil.which(tool) is None:
            print(f"{tool} is required", file=sys.stderr)
            return 2
    if args.write:
        write_entries()
        CASES.write_text(json.dumps(build_cases(NOW_MS), indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"wrote {CASES.relative_to(ROOT)} and {ENTRIES.relative_to(ROOT)}")
        return 0
    return check()


if __name__ == "__main__":
    sys.exit(main())
