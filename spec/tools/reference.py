#!/usr/bin/env python3
"""Independent reference implementation of spec/METRICS.md, used to cross-check the Swift and C# cores.

  python3 spec/tools/reference.py --check-fixtures
  python3 spec/tools/reference.py --dump-today [--codex PATH] [--claude PATH] [--dsh PATH] [--gemini PATH]
                                              [--omp PATH] [--pi PATH] [--antigravity PATH] [--workbuddy PATH]
                                              [--workbuddy-ai PATH] [--openclaw PATH] [--hermes PATH]
                                              [--droid PATH]
  python3 spec/tools/reference.py --dump-history [--utc] [--prices PATH] [...]
  python3 spec/tools/reference.py --write-history-fixture
  python3 spec/tools/reference.py --dump-unpriced [--days N | --first YYYY-MM-DD --last YYYY-MM-DD | --live]
                                                 [--source ID] [--prices PATH] [...]
  python3 spec/tools/reference.py --dump-tasks [--utc] [--first YYYY-MM-DD --last YYYY-MM-DD] [--source ID] [...]
  python3 spec/tools/reference.py --write-task-fixture

The dump format matches the `--dump-today` output of both platform probes. Conversation content is never printed.
`--dump-today` ends with one `task` row per seeded agent of a scored source and a `taskSets` row (METRICS 6.9 Values,
evaluated at `--now` or the capture time, predictions with the prior coefficients); `--dump-tasks` prints the task
records of every log read whole, the calibration per source and the comparison (METRICS 6.11).
`--dump-unpriced` prints the copied diagnostics text of METRICS 9.10 (model ids, providers and counts only) over the
daily history, or with `--live` over today's engine responses.
Costs (METRICS 9) use spec/assets/prices.json unless --prices names another table; prices.py does the pricing.
"""
import argparse
import math
import ntpath
import posixpath
import sqlite3
import subprocess
import json
import os
import re
import shutil
import stat
import sys
import tempfile
import time
from collections import OrderedDict
from datetime import datetime, timedelta, timezone
from urllib.parse import unquote
from zoneinfo import ZoneInfo

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import prices as pricing  # noqa: E402
import harness_reference as harnesses  # noqa: E402
import trae_reference as trae  # noqa: E402
import unpriced as diagnostics  # noqa: E402
import execution_modes_reference as modes  # noqa: E402

if hasattr(sys, "set_int_max_str_digits"):
    sys.set_int_max_str_digits(0)  # integer literals of any length are numbers (METRICS 3.6), not malformed lines

MAX_LINE = 32 * 1024 * 1024
DEDUPE_LIMIT = 65_536
WINDOW_MS = 3000
TS_MAX_CHARS = 64
TS_RE = re.compile(r"([0-9]{4})-([0-9]{2})-([0-9]{2})[Tt ]([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]+))?"
                   r"(?:[Zz]|([+-])([0-9]{2}):?([0-9]{2}))")
INT64_MIN, INT64_MAX = -2 ** 63, 2 ** 63 - 1
TOKEN_CAP = 10 ** 12
DAY_FIRST_MS = -62_135_596_800_000 + 2 * 86_400_000  # 0001-01-03T00:00:00Z
DAY_LAST_MS = 253_402_300_799_999 - 2 * 86_400_000  # 9999-12-29T23:59:59.999Z
TOKEN_FIELDS = ("input", "cachedInput", "cacheWriteInput", "cacheWrite1hInput", "output", "reasoning")
GEMINI_LEGACY_MAX = 64 * 1024 * 1024
GEMINI_SANITIZE = re.compile(r"[^A-Za-z0-9_-]")
GEMINI_CANCELLED = "Request cancelled."
OMP_COPY_MS = 2000
AG_BLOB_MAX = 1024 * 1024
AG_TOKEN_MAX = 10_000_000
AG_OPEN = {1, 2, 8, 9, 11}
AG_FAILED = {6, 7, 12}
AG_NAME = re.compile(r"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\.db$")


def days_from_civil(y, m, d):
    """Days since 1970-01-01 of a proleptic Gregorian date (Howard Hinnant's algorithm), for any year."""
    y = y - 1 if m <= 2 else y
    era = y // 400
    yoe = y - era * 400
    doy = (153 * (m + (-3 if m > 2 else 9)) + 2) // 5 + d - 1
    return era * 146_097 + yoe * 365 + yoe // 4 - yoe // 100 + doy - 719_468


def days_in_month(y, m):
    if m == 2:
        return 29 if y % 4 == 0 and (y % 100 != 0 or y % 400 == 0) else 28
    return 30 if m in (4, 6, 9, 11) else 31


def parse_ts(value):
    """A timestamp string of METRICS 3.1 -> Unix ms; None when the string does not follow it exactly."""
    if not isinstance(value, str) or len(value) > TS_MAX_CHARS:
        return None
    m = TS_RE.fullmatch(value)
    if not m:
        return None
    y, mo, d, h, mi, s = (int(g) for g in m.groups()[:6])
    frac, sign, oh, om = m.groups()[6:]
    if not (1 <= mo <= 12 and 1 <= d <= days_in_month(y, mo) and h <= 23 and mi <= 59 and s <= 59):
        return None
    offset = 0
    if sign:
        if int(oh) > 23 or int(om) > 59:
            return None
        offset = (int(oh) * 3600 + int(om) * 60) * (1 if sign == "+" else -1)
    seconds = days_from_civil(y, mo, d) * 86_400 + h * 3600 + mi * 60 + s - offset
    return seconds * 1000 + int((frac or "0")[:3].ljust(3, "0"))


def finite(value):
    """A JSON number whose nearest double is finite (METRICS 3.6): 1e999 and integers past ~1.8e308 are not."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return False
    if isinstance(value, int):
        try:
            float(value)
        except OverflowError:
            return False
        return True
    return math.isfinite(value)


def trunc64(value):
    """A finite number truncated toward zero and saturated to the signed 64-bit range (METRICS 3.6)."""
    return min(max(int(value), INT64_MIN), INT64_MAX)


def ordinal(value):
    """A Codex rollout ordinal (METRICS 3.5): an integer literal from 0 through 2^63 - 1; anything else is None."""
    return value if type(value) is int and 0 <= value <= INT64_MAX else None


def num(value):
    """A token value (METRICS 3.2): a number truncated toward zero and clamped to 0…10^12; anything else is 0."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return 0
    if isinstance(value, float) and not math.isfinite(value):
        return TOKEN_CAP if value > 0 else 0
    return min(max(0, int(value)), TOKEN_CAP)


def cap(value):
    """A normalized token field that is a sum saturates at 10^12 (METRICS 3.2)."""
    return min(value, TOKEN_CAP)


def has_day(ms):
    """Whether an instant has a calendar date in every time zone (METRICS 8)."""
    return DAY_FIRST_MS <= ms <= DAY_LAST_MS


def not_json(constant):
    raise ValueError("%s is not JSON" % constant)


def loads(raw):
    """json.loads without NaN / Infinity / -Infinity, which are not JSON: such a line is malformed (METRICS 3.6)."""
    return json.loads(raw, parse_constant=not_json)


def basename(path):
    if not isinstance(path, str) or not path:
        return None
    parts = [p for p in re.split(r"[\\/]", path) if p]
    return parts[-1] if parts else None


class Deduper:
    def __init__(self, limit=DEDUPE_LIMIT):
        self.owners = OrderedDict()
        self.limit = limit

    def owner(self, key):
        return self.owners.get(key)

    def claim(self, key, owner):
        self.owners[key] = owner
        while len(self.owners) > self.limit:
            self.owners.popitem(last=False)


class FileState:
    def __init__(self, path, source, root=None):
        self.path = path
        self.source = source
        self.source_decided = False
        self.project = None
        self.model = None
        self.is_subagent = source == "claudeCode" and "subagents" in [p for p in re.split(r"[\\/]", path) if p]
        self.phase = "idle"
        self.in_flight = None
        self.last_line = None
        self.pending = None
        self.malformed = 0
        self.responses = OrderedDict()
        # reasoning effort and speed (EXECUTION-MODES): context facts, each response's facts, Claude's ultracode
        self.facts = []
        self.response_facts = {}
        self.latest_key = None
        self.line_effort = None
        self.ultra_session = False
        self.ultra_turn = False
        # codex
        self.file_created = None
        self.start_ordinal = None
        self.provider = None
        self.my_agent_path = "/root"
        self.thread_id = None
        self.parent_thread_id = None
        self.streaming = False
        self.saw_modern = False
        self.prev_legacy = None
        # claude
        self.precise = False
        # deepseek, gemini
        self.parent_session = None
        # gemini
        self.marker = None
        self.seen_calls = set()
        if source == "gemini":
            parts = [p for p in re.split(r"[\\/]", path) if p]
            nested = len(parts) >= 3 and parts[-3] == "chats"
            self.source_decided = True
            self.is_subagent = nested
            self.parent_session = parts[-2] if nested else None
            self.project = gemini_project(path)
        # omp
        self.header_seen = False
        self.pending_calls = set()
        if source == "omp":
            self.source_decided = True
            rel = os.path.relpath(path, root) if root else path
            self.is_subagent = len([p for p in re.split(r"[\\/]", rel) if p]) >= 3
        if source == "pi":
            self.source_decided = True
            self.is_subagent = pi_parent_file(path) is not None
        # openclaw (METRICS 4.14): the window's runtime decides delegated turns
        self.oc_delegated = False
        self.oc_mirror_window = False
        if source == "openclaw":
            self.source_decided = True
        # factory droid (METRICS 4.16): parent links, open calls, the counters' high-water mark and unallocated growth
        self.droid_parent = None
        self.droid_calling = None
        self.droid_open = []
        self.droid_seen = set()
        self.droid_mark = {f: 0 for f in ("inputTokens", "cacheCreationTokens", "cacheReadTokens", "outputTokens",
                                          "thinkingTokens")}
        self.droid_growth = dict(self.droid_mark)
        self.droid_signature = None
        self.droid_parent_bad = None  # signature of the parent sidecar the last attempt found unreadable
        self.droid_unknown = False
        self.droid_baseline = False
        self.droid_model = None
        self.droid_call_model = None
        self.droid_compaction = None
        self.droid_read_from = 0
        if source == "droid":
            self.source_decided = True
        # workbuddy
        self.cwd = None
        self.open_calls = OrderedDict()
        # task score (METRICS 6.9): the event composer of task_events.py, when attached
        self.tasks = None
        if source in WB_FOLDERS:
            self.source_decided = True
            rel = [p for p in re.split(r"[\\/]", os.path.relpath(path, root) if root else path) if p]
            self.is_subagent = len(rel) == 4 and rel[2] == "subagents"


def emit(state, key, start, end, tokens, model, final=True, provider=None, fast=False):
    tokens.setdefault("cacheWrite1hInput", 0)
    state.responses[key] = dict(key=key, startMs=start, endMs=end, model=model, provider=provider, fast=fast,
                                final=final, **tokens)


def codex_tokens(usage):
    usage = usage if isinstance(usage, dict) else {}
    return dict(input=num(usage.get("input_tokens")), cachedInput=num(usage.get("cached_input_tokens")),
                cacheWriteInput=num(usage.get("cache_write_input_tokens")), output=num(usage.get("output_tokens")),
                reasoning=num(usage.get("reasoning_output_tokens")))


def codex_line(state, obj, dedupe):
    ts = parse_ts(obj.get("timestamp"))
    if ts is None:
        return
    kind = obj.get("type")
    payload = obj.get("payload") if isinstance(obj.get("payload"), dict) else {}
    if kind == "session_meta":
        if state.file_created is not None:
            return
        state.file_created = ts
        state.start_ordinal = ordinal(payload.get("subagent_history_start_ordinal"))
        state.last_line = ts if state.last_line is None else max(state.last_line, ts)
        state.project = basename(payload.get("cwd")) or state.project
        state.my_agent_path = payload.get("agent_path") or "/root"
        state.provider = payload.get("model_provider") if isinstance(payload.get("model_provider"), str) else None
        source = payload.get("source")
        state.is_subagent = state.is_subagent or payload.get("thread_source") == "subagent" or (
            isinstance(source, dict) and "subagent" in source)
        state.thread_id = payload.get("id") or state.thread_id
        subagent = source.get("subagent") if isinstance(source, dict) else None
        spawn = subagent.get("thread_spawn") if isinstance(subagent, dict) else None
        state.parent_thread_id = payload.get("parent_thread_id") or (
            spawn.get("parent_thread_id") if isinstance(spawn, dict) else None)
        return
    if state.file_created is not None and ts < state.file_created - 2000:
        return
    line_ordinal = ordinal(obj.get("ordinal"))
    if state.start_ordinal is not None and line_ordinal is not None and line_ordinal < state.start_ordinal:
        return
    state.last_line = ts if state.last_line is None else max(state.last_line, ts)
    ptype = payload.get("type")
    if kind == "turn_context":
        state.model = payload.get("model") or state.model
        state.project = basename(payload.get("cwd")) or state.project
        codex_turn_facts(state, payload)
        return
    if kind == "token_usage_record":
        state.saw_modern = True
        thread = payload.get("thread_id") or state.thread_id or state.path
        key = payload.get("response_id") or "codex:%s:%d" % (thread, ts)
        if dedupe.owner(key) is not None:
            return
        dedupe.claim(key, state.path)
        emit(state, key, state.pending, ts, codex_tokens(payload.get("usage")), state.model,
             provider=state.provider)
        tier = payload.get("service_tier")
        state.response_facts[key] = modes.put(list(state.facts), modes.normalize(
            "serviceTier", tier, "openai", "recorded", "request") if isinstance(tier, str) else None)
        after_usage(state, ts)
        return
    if kind == "event_msg":
        if ptype == "token_count":
            if state.saw_modern and state.streaming:
                after_usage(state, ts)
            else:
                legacy(state, ts, payload, dedupe)
        elif ptype in ("task_complete", "turn_aborted"):
            state.phase, state.pending, state.streaming, state.in_flight = "idle", None, False, None
        elif ptype in ("task_started", "user_message"):
            input_line(state, ts)
        elif ptype == "thread_settings_applied" and isinstance(payload.get("thread_settings"), dict):
            codex_thread_settings(state, payload["thread_settings"])
        return
    if kind == "response_item":
        role = payload.get("role")
        if ptype == "reasoning" or (isinstance(ptype, str) and ptype.endswith("_call")) or (
                ptype == "message" and role == "assistant"):
            state.streaming = True
            state.phase = "generating"
            state.in_flight = state.pending if state.pending is not None else ts
        elif (isinstance(ptype, str) and ptype.endswith("_output")) or (
                ptype == "message" and role in ("user", "developer", "system")):
            input_line(state, ts)
        elif ptype == "agent_message" and payload.get("recipient") == state.my_agent_path:
            if not state.streaming:
                state.pending = ts


def codex_turn_facts(state, payload):
    """A turn context starts a new turn: turn facts are replaced (EXECUTION-MODES, Codex)."""
    state.facts = [f for f in state.facts if f["scope"] != "turn"]
    raw = payload.get("effort")
    if not isinstance(raw, str):
        collab = payload.get("collaboration_mode")
        settings = collab.get("settings") if isinstance(collab, dict) else None
        raw = settings.get("reasoning_effort") if isinstance(settings, dict) else None
    if isinstance(raw, str):
        state.facts = modes.put(state.facts, modes.normalize("effort", raw, None, "recorded", "turn"))
    tier = payload.get("service_tier")
    if isinstance(tier, str):
        state.facts = modes.put(state.facts, modes.normalize("serviceTier", tier, "openai", "selected", "turn"))


def codex_thread_settings(state, settings):
    """`thread_settings_applied`: the thread's selected tier and effort from now on; null clears them."""
    for field, adapter, provider in (("service_tier", "serviceTier", "openai"), ("reasoning_effort", "effort", None)):
        if field not in settings:
            continue
        kind = "serviceTier" if adapter == "serviceTier" else "effort"
        state.facts = [f for f in state.facts if not (f["kind"] == kind and f["evidence"] == "selected"
                                                      and f["scope"] == "session" and f.get("provider") == provider)]
        raw = settings[field]
        if isinstance(raw, str):
            state.facts = modes.put(state.facts, modes.normalize(adapter, raw, provider, "selected", "session"))


def input_line(state, ts):
    if not state.streaming:
        state.pending = ts
        state.phase = "generating"
        state.in_flight = state.pending


def after_usage(state, ts):
    state.pending, state.streaming, state.phase, state.in_flight = ts, False, "tools", None


def legacy(state, ts, payload, dedupe):
    info = payload.get("info")
    if state.saw_modern or not isinstance(info, dict):
        return
    total = info.get("total_token_usage")
    last = info.get("last_token_usage")
    if not isinstance(total, dict):
        return
    fields = ("input_tokens", "cached_input_tokens", "cache_write_input_tokens", "output_tokens",
              "reasoning_output_tokens", "total_tokens")
    if state.prev_legacy is None:
        usage = last if isinstance(last, dict) else total
    else:
        delta = {f: num(total.get(f)) - num(state.prev_legacy.get(f)) for f in fields}
        if delta["total_tokens"] == 0:
            return
        usage = (last if isinstance(last, dict) else {}) if any(v < 0 for v in delta.values()) else delta
    state.prev_legacy = total
    thread = state.thread_id or state.path
    key = "codex-legacy:%s:%d" % (thread, num(total.get("total_tokens")))
    if dedupe.owner(key) is not None:
        return
    dedupe.claim(key, state.path)
    emit(state, key, state.pending, ts, codex_tokens(usage), state.model, provider=state.provider)
    state.response_facts[key] = list(state.facts)
    after_usage(state, ts)


def is_interrupt(message):
    content = message.get("content") if isinstance(message, dict) else None
    marker = "[Request interrupted by user"
    if isinstance(content, str):
        return content.startswith(marker)
    if isinstance(content, list):
        return any(isinstance(b, dict) and b.get("type") == "text" and isinstance(b.get("text"), str)
                   and b["text"].startswith(marker) for b in content)
    return False


def is_prompt(obj):
    """A user line a person typed (or an interrupt): not meta, string content or a text block."""
    if obj.get("isMeta") is True:
        return False
    message = obj.get("message")
    content = message.get("content") if isinstance(message, dict) else None
    return isinstance(content, str) or (isinstance(content, list) and any(
        isinstance(b, dict) and b.get("type") == "text" for b in content))


def claude_recorded_effort(obj):
    raw = obj.get("perTurnEffort")
    raw = raw if isinstance(raw, str) else obj.get("effort")
    return modes.normalize("effort", raw, None, "recorded", "request") if isinstance(raw, str) else None


def claude_ultracode(state):
    if state.ultra_session or state.ultra_turn:
        return modes.normalize("effort", "ultracode", None, "recorded", "request")
    return None


def claude_agent_facts(state):
    facts = [f for f in state.response_facts.get(state.latest_key, []) if f["kind"] != "effort"]
    return modes.put(facts, claude_ultracode(state) or state.line_effort)


def claude_tokens(usage):
    usage = usage if isinstance(usage, dict) else {}
    inp, cc, cr = num(usage.get("input_tokens")), num(usage.get("cache_creation_input_tokens")), num(
        usage.get("cache_read_input_tokens"))
    details = usage.get("output_tokens_details") if isinstance(usage.get("output_tokens_details"), dict) else {}
    creation = usage.get("cache_creation") if isinstance(usage.get("cache_creation"), dict) else {}
    return dict(input=cap(inp + cc + cr), cachedInput=cr, cacheWriteInput=cc,
                cacheWrite1hInput=min(cc, num(creation.get("ephemeral_1h_input_tokens"))),
                output=num(usage.get("output_tokens")), reasoning=num(details.get("thinking_tokens")))


def claude_line(state, obj, dedupe):
    ts = parse_ts(obj.get("timestamp"))
    if ts is None:
        return
    state.last_line = ts if state.last_line is None else max(state.last_line, ts)
    if not state.source_decided and isinstance(obj.get("entrypoint"), str):
        state.source = "claudeDesktop" if obj["entrypoint"].startswith("claude-desktop") else "claudeCode"
        state.source_decided = True
    state.project = basename(obj.get("cwd")) or state.project
    if obj.get("isSidechain") is True:
        state.is_subagent = True
    kind = obj.get("type")
    if kind == "attachment" and isinstance(obj.get("attachment"), dict):
        marker = obj["attachment"].get("type")
        if marker in ("ultra_effort_enter", "ultra_effort_exit"):
            state.ultra_session = marker == "ultra_effort_enter"
        elif marker == "workflow_keyword_request":
            state.ultra_turn = True
    if kind == "user" and is_prompt(obj):
        state.ultra_turn = False
    if kind == "api-request":
        state.pending, state.precise, state.phase, state.in_flight = ts, True, "generating", ts
    elif kind == "user":
        if is_interrupt(obj.get("message")) or obj.get("toolEndsTurn") is True:
            state.phase, state.pending, state.precise, state.in_flight = "idle", None, False, None
        else:
            if not state.precise:
                state.pending = ts
            state.phase, state.in_flight = "generating", state.pending
    elif kind == "assistant":
        message = obj.get("message") if isinstance(obj.get("message"), dict) else {}
        if message.get("model") == "<synthetic>":
            return
        key = message.get("id") or obj.get("requestId") or obj.get("uuid")
        if not isinstance(key, str) or not key:
            return
        owner = dedupe.owner(key)
        if owner is not None and owner != state.path:
            return
        tokens = claude_tokens(message.get("usage"))
        usage = message.get("usage") if isinstance(message.get("usage"), dict) else {}
        fast = usage.get("speed") == "fast"
        stop = message.get("stop_reason")
        resp = state.responses.get(key)
        if resp is None:
            dedupe.claim(key, state.path)
            emit(state, key, state.pending, ts, tokens, message.get("model"), stop is not None, fast=fast)
            state.pending, state.precise = None, False
            resp = state.responses[key]
        else:
            resp["endMs"] = max(resp["endMs"], ts)
            for f in TOKEN_FIELDS:
                resp[f] = max(resp[f], tokens[f])
            resp["model"] = message.get("model") or resp["model"]
            resp["fast"] = resp["fast"] or fast
            resp["final"] = resp["final"] or stop is not None
        state.model = resp["model"] or state.model
        recorded = claude_recorded_effort(obj)
        line_facts = [claude_ultracode(state) or recorded]
        if isinstance(usage.get("speed"), str):
            line_facts.append(modes.normalize("claudeSpeed", usage["speed"], "anthropic", "served", "response"))
        merged = state.response_facts.get(key, [])
        for fact in line_facts:
            merged = modes.put(merged, fact)
        state.response_facts[key], state.latest_key = merged, key
        state.line_effort = recorded or state.line_effort
        content = message.get("content") if isinstance(message.get("content"), list) else []
        has_tool_use = any(isinstance(b, dict) and b.get("type") == "tool_use" for b in content)
        if stop == "tool_use" or (stop is None and has_tool_use):
            state.phase, state.in_flight = "tools", None
        elif stop is None or stop == "pause_turn":
            state.phase, state.in_flight = "generating", resp["startMs"] if resp["startMs"] is not None else ts
        else:
            state.phase, state.in_flight = "idle", None


ZSTD_MAGIC = 0xFD2FB528
SKIPPABLE_MAGIC = re.compile(rb"[\x50-\x5f]\x2a\x4d\x18")


def zstd_frame_size(data, pos):
    """Compressed size of the frame starting at pos, or None when it is not complete yet (zstd format spec)."""
    if len(data) - pos < 8:
        return None
    magic = int.from_bytes(data[pos:pos + 4], "little")
    if magic & 0xFFFFFFF0 == 0x184D2A50:
        size = 8 + int.from_bytes(data[pos + 4:pos + 8], "little")
        return size if len(data) - pos >= size else None
    if magic != ZSTD_MAGIC:
        raise ValueError("not a zstd frame")
    fhd = data[pos + 4]
    single, fcs_flag, checksum, dict_flag = (fhd >> 5) & 1, fhd >> 6, (fhd >> 2) & 1, fhd & 3
    header = 1 + (0 if single else 1) + [0, 1, 2, 4][dict_flag] + [1 if single else 0, 2, 4, 8][fcs_flag]
    cur = pos + 4 + header
    while True:
        if len(data) - cur < 3:
            return None
        bh = int.from_bytes(data[cur:cur + 3], "little")
        last, kind, size = bh & 1, (bh >> 1) & 3, bh >> 3
        cur += 3 + (1 if kind == 1 else size)
        if kind == 3:
            raise ValueError("reserved block type")
        if last:
            break
    cur += 4 if checksum else 0
    return cur - pos if cur <= len(data) else None


def zstd_lines(path, start_offset=0):
    data = open(path, "rb").read()
    pos, pending, malformed = start_offset, b"", 0
    while pos < len(data):
        try:
            size = zstd_frame_size(data, pos)
        except ValueError:
            malformed += 1
            candidates = [i for i in [data.find(b"\x28\xb5\x2f\xfd", pos + 1)] if i >= 0]
            skippable = SKIPPABLE_MAGIC.search(data, pos + 1)
            if skippable:
                candidates.append(skippable.start())
            if not candidates:
                break
            pos = min(candidates)
            continue
        if size is None:
            break
        frame = data[pos:pos + size]
        pos += size
        if int.from_bytes(frame[:4], "little") != ZSTD_MAGIC:
            continue
        out = subprocess.run(["zstd", "-dc", "-q"], input=frame, capture_output=True)
        if out.returncode != 0 or len(out.stdout) > 64 * 1024 * 1024:
            malformed += 1
            continue
        pending += out.stdout
        *complete, pending = pending.split(b"\n")
        for raw in complete:
            if raw.strip() and len(raw) <= MAX_LINE:
                yield raw
    if malformed:
        yield ("malformed", malformed)


def read_lines(path, start_offset=0, max_lines=None):
    with open(path, "rb") as f:
        f.seek(start_offset)
        data = f.read()
    if start_offset:
        nl = data.find(b"\n")
        data = data[nl + 1:] if nl >= 0 else b""
    end = data.rfind(b"\n")
    if end < 0:
        return
    for count, raw in enumerate(data[:end].split(b"\n"), start=1):
        if max_lines is not None and count > max_lines:
            return
        if len(raw) > MAX_LINE or not raw.strip():
            continue
        yield raw


def zst_stream_lines(path):
    """A compressed Codex session (METRICS 5.3): decompressed as one stream, always from the start."""
    if os.path.getsize(path) == 0:
        return  # nothing to read; not a decoding error (zstd -dc exits non-zero on an empty file)
    proc = subprocess.Popen(["zstd", "-dc", "-q", path], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    pending = b""
    try:
        for chunk in iter(lambda: proc.stdout.read(1 << 20), b""):
            pending += chunk
            *complete, pending = pending.split(b"\n")
            for raw in complete:
                if raw.strip() and len(raw) <= MAX_LINE:
                    yield raw
            if len(pending) > MAX_LINE:
                pending = b""
    finally:
        proc.stdout.close()
        if proc.wait() != 0:
            yield ("malformed", 1)


def process_file(state, dedupe, start_offset=0, max_lines=None):
    if state.source == "gemini" and state.path.endswith(".json"):
        return gemini_legacy(state, dedupe)
    if state.source == "droid":
        return droid_read(state, dedupe, start_offset, max_lines)
    handler = {"codex": codex_line, "deepseek": dsh_event, "gemini": gemini_record, "omp": omp_line, "pi": pi_line,
               "workbuddy": workbuddy_line, "workbuddyAi": workbuddy_line}.get(state.source, claude_line)
    if state.source in ("omp", "pi") and start_offset > 0:
        omp_read_header(state, dedupe)
    composer = state.tasks
    if state.path.endswith(".zstd"):
        lines = zstd_lines(state.path, start_offset)
    elif state.path.endswith(".jsonl.zst"):
        lines = zst_stream_lines(state.path)
    else:
        lines = read_lines(state.path, start_offset, max_lines)
    for raw in lines:
        if isinstance(raw, tuple):
            state.malformed += raw[1]
            continue
        try:
            obj = loads(raw)
        except (ValueError, UnicodeDecodeError, RecursionError):
            state.malformed += 1
            continue
        if not isinstance(obj, dict):
            state.malformed += 1
            continue
        handler(state, obj, dedupe)
        if composer is not None:
            composer.feed(state, obj, dedupe, raw)


def resolve_parents(states):
    """Maps agent id (path) to its parent agent id per METRICS 6.7."""
    by_path = {s.path: s for s in states}
    by_thread, by_session = {}, {}
    for s in sorted(states, key=lambda s: (s.file_created or 0, s.path)):
        if s.source == "codex" and s.thread_id and s.thread_id not in by_thread:
            by_thread[s.thread_id] = s.path
        if s.source == "deepseek" and s.thread_id and s.thread_id not in by_session:
            by_session[s.thread_id] = s.path
    gemini_mains = {}
    for s in sorted((s for s in states if s.source == "gemini" and s.thread_id),
                    key=lambda s: (0 if s.path.endswith(".jsonl") else 1, s.file_created or 0, s.path)):
        parts = re.split(r"[\\/]", s.path)
        if len(parts) >= 2 and parts[-2] == "chats":
            gemini_mains.setdefault((os.sep.join(parts[:-1]), GEMINI_SANITIZE.sub("_", s.thread_id)), s.path)
    parents = {}
    for s in states:
        parent = None
        if s.source == "codex":
            parent = by_thread.get(s.parent_thread_id) if s.parent_thread_id else None
        elif s.source == "deepseek":
            parent = by_session.get(s.parent_session) if s.parent_session else None
        elif s.source == "gemini":
            chats = os.sep.join(re.split(r"[\\/]", s.path)[:-2])
            parent = gemini_mains.get((chats, s.parent_session)) if s.parent_session else None
        elif s.source == "droid":
            candidate = os.path.join(os.path.dirname(s.path), s.droid_calling + ".jsonl") if s.droid_calling else None
            tracked = candidate in by_path and by_path[candidate].source == "droid"
            parent = candidate if tracked else None
        elif s.source in ("omp", "pi"):
            candidate = os.path.dirname(s.path) + ".jsonl"
            tracked = candidate in by_path and by_path[candidate].source == s.source
            parent = candidate if s.is_subagent and tracked else None
        else:
            parts = re.split(r"[\\/]", s.path)
            if len(parts) >= 3 and parts[-2] == "subagents":
                candidate = s.path[:len(s.path) - len(parts[-1]) - len(parts[-2]) - 2] + ".jsonl"
                parent = candidate if candidate in by_path else None
        parents[s.path] = parent if parent != s.path else None
    for path in list(parents):
        seen, cur = set(), path
        while cur is not None and cur not in seen:
            seen.add(cur)
            cur = parents.get(cur)
        if cur is not None:
            parents[path] = None
    return parents


def dsh_event(state, obj, dedupe):
    kind = obj.get("type")
    if kind == "event" and isinstance(obj.get("event"), dict):
        return dsh_event(state, obj["event"], dedupe)
    if kind == "snapshot":
        if isinstance(obj.get("header"), dict):
            dsh_header(state, obj["header"])
        for record in obj.get("records") or []:
            if isinstance(record, dict):
                dsh_event(state, record, dedupe)
        return
    if kind == "session":
        dsh_header(state, obj)
        return
    ts = obj.get("time")
    if not finite(ts):
        return
    ts = trunc64(ts)
    if state.file_created is not None and ts < state.file_created - 2000:
        return
    state.last_line = ts if state.last_line is None else max(state.last_line, ts)
    data = obj.get("data") if isinstance(obj.get("data"), dict) else {}
    if kind == "request/header":
        config = (data.get("header") or {}).get("config") if isinstance(data.get("header"), dict) else None
        if isinstance(config, dict) and isinstance(config.get("model"), str) and config["model"]:
            state.model = config["model"]
        if isinstance(config, dict) and isinstance(config.get("provider"), str) and config["provider"]:
            state.provider = config["provider"]
        effort = config.get("reasoningEffort") if isinstance(config, dict) else None
        state.facts = [f for f in state.facts if f["kind"] != "effort"]
        if isinstance(effort, str):
            state.facts = modes.put(state.facts, modes.normalize("effort", effort, None, "recorded", "request"))
    elif kind == "step/start":
        state.pending, state.phase, state.in_flight = ts, "generating", ts
    elif kind == "turn/end":
        state.phase, state.pending, state.in_flight = "idle", None, None
    elif kind == "assistant/message":
        message = data.get("message") if isinstance(data.get("message"), dict) else {}
        key = message.get("id") or "dsh:%s:%s" % (state.thread_id, obj.get("seq"))
        if dedupe.owner(key) is not None:
            return
        dedupe.claim(key, state.path)
        usage = data.get("usage")
        if isinstance(usage, dict):
            cr, cw = num(usage.get("cacheReadTokens")), num(usage.get("cacheWriteTokens"))
            tokens = dict(input=cap(num(usage.get("inputTokens")) + cr + cw), cachedInput=cr, cacheWriteInput=cw,
                          output=num(usage.get("outputTokens")), reasoning=0)
            source = message.get("source") if isinstance(message.get("source"), dict) else {}
            model = source.get("model") or state.model
            provider = source.get("provider") if isinstance(source.get("provider"), str) and source["provider"] \
                else state.provider
            emit(state, key, state.pending, ts, tokens, model, provider=provider)
            state.response_facts[key] = list(state.facts)
            state.model = model or state.model
        content = message.get("content") if isinstance(message.get("content"), list) else []
        tool = any(isinstance(b, dict) and b.get("type") == "tool-call" for b in content)
        state.pending, state.phase, state.in_flight = None, ("tools" if tool else "idle"), None


def dsh_header(state, header):
    if state.file_created is not None:
        return
    created = header.get("createdAt")
    state.file_created = trunc64(created) if finite(created) else 0
    state.thread_id = header.get("id") or state.thread_id
    state.project = basename(header.get("cwd")) or state.project
    parent = header.get("parentSession")
    depth = header.get("delegationDepth")
    state.parent_session = parent if isinstance(parent, str) and parent else None
    state.is_subagent = state.is_subagent or header.get("origin") == "subagent" or bool(state.parent_session) or (
        isinstance(depth, (int, float)) and not isinstance(depth, bool) and depth > 0)


def opencode_states(db_path, dedupe):
    """Parses an OpenCode database (METRICS 4.5); returns one state per session, keyed like agent ids."""
    db = sqlite_readonly(db_path)
    try:
        tables = {r[0] for r in db.execute("select name from sqlite_master where type='table'")}
        if not {"session_v2", "session_message"} <= tables:
            return []
        sessions = {r[0]: r for r in db.execute(
            "select id, parent_id, directory, time_created from session_v2")}
        states = {}
        for sid, parent, directory, created in sessions.values():
            st = FileState("opencode:" + sid, "opencode")
            st.project = basename(directory)
            st.is_subagent = parent is not None
            st.parent_session = ("opencode:" + parent) if parent in sessions else None
            st.file_created = created
            states[sid] = st
        rows = db.execute("select id, session_id, type, time_updated, data from session_message "
                          "order by session_id, seq")
        for rid, sid, kind, updated, data in rows:
            st = states.get(sid)
            if st is None:
                continue
            try:
                d = loads(data)
            except (ValueError, RecursionError):
                st.malformed += 1
                continue
            if not isinstance(d, dict):
                st.malformed += 1
                continue
            times = d.get("time") if isinstance(d.get("time"), dict) else {}
            if kind == "assistant":
                created = times.get("created")
                if isinstance(created, (int, float)) and created < st.file_created - 2000:
                    continue
                st.last_line = updated if st.last_line is None else max(st.last_line, updated)
                model = (d.get("model") or {}).get("id") if isinstance(d.get("model"), dict) else None
                provider = d["model"].get("providerID") if isinstance(d.get("model"), dict) else None
                provider = provider if isinstance(provider, str) and provider else None
                st.model = model or st.model
                st.facts = opencode_facts(d, provider)
                if "streamed" not in times:
                    st.phase, st.in_flight = "generating", created
                    continue
                tokens = d.get("tokens")
                if isinstance(tokens, dict) and dedupe.owner(rid) in (None, st.path):
                    dedupe.claim(rid, st.path)
                    emit(st, rid, created, times["streamed"], opencode_tokens(tokens), model, provider=provider)
                    st.response_facts[rid] = st.facts
                st.phase = "tools" if d.get("finish") == "tool-calls" else "idle"
                st.in_flight = None
            else:
                st.last_line = updated if st.last_line is None else max(st.last_line, updated)
                if kind == "compaction" and isinstance(d.get("tokens"), dict):
                    if dedupe.owner(rid) in (None, st.path):
                        dedupe.claim(rid, st.path)
                        model = (d.get("model") or {}).get("id") if isinstance(d.get("model"), dict) else None
                        provider = d["model"].get("providerID") if isinstance(d.get("model"), dict) else None
                        provider = provider if isinstance(provider, str) and provider else None
                        emit(st, rid, times.get("created"), times.get("completed", updated),
                             opencode_tokens(d["tokens"]), model, provider=provider)
                        st.response_facts[rid] = opencode_facts(d, provider)
                elif kind == "idle":
                    st.phase, st.in_flight = "idle", None
        return list(states.values())
    finally:
        db.close()


def opencode_facts(d, provider):
    """An assistant row's effort variant (recorded) and, for OpenAI providers, its served tier."""
    facts = []
    model = d.get("model") if isinstance(d.get("model"), dict) else {}
    if isinstance(model.get("variant"), str):
        facts = modes.put(facts, modes.normalize("effort", model["variant"], None, "recorded", "request"))
    native = d.get("native") if isinstance(d.get("native"), dict) else {}
    if provider in ("openai", "azure") and isinstance(native.get("serviceTier"), str):
        facts = modes.put(facts, modes.normalize("serviceTier", native["serviceTier"], "openai", "served", "response"))
    return facts


def agent_facts(state):
    return claude_agent_facts(state) if state.source in ("claudeCode", "claudeDesktop") else state.facts


def mode_view(facts):
    """(effort, speed) attributions, each None when unknown."""
    effort, speed = modes.attribution(facts, "effort"), modes.speed_attribution(facts)
    return (None if effort["mode"] == "unknown" else effort, None if speed["mode"] == "unknown" else speed)


def mode_fields(state):
    effort, speed = mode_view(agent_facts(state))
    by_key = {}
    for key in state.responses:
        e, s = mode_view(state.response_facts.get(key, []))
        by_key[key] = {"effort": e, "speed": s}
    return dict(effort=effort, speed=speed, responseModes=by_key)


def opencode_tokens(t):
    cache = t.get("cache") if isinstance(t.get("cache"), dict) else {}
    cr, cw, r = num(cache.get("read")), num(cache.get("write")), num(t.get("reasoning"))
    return dict(input=cap(num(t.get("input")) + cr + cw), cachedInput=cr, cacheWriteInput=cw,
                output=cap(num(t.get("output")) + r), reasoning=r)


def gemini_tokens(t):
    return dict(input=cap(num(t.get("input")) + num(t.get("tool"))), cachedInput=num(t.get("cached")),
                cacheWriteInput=0, output=cap(num(t.get("output")) + num(t.get("thoughts"))),
                reasoning=num(t.get("thoughts")))


def gemini_content(content):
    """(has visible non-thought text, has a functionCall part)."""
    if isinstance(content, str):
        return bool(content.strip()), False
    if isinstance(content, dict):
        content = [content]
    text = call = False
    if isinstance(content, list):
        for part in content:
            if isinstance(part, str):
                text = text or bool(part.strip())
            elif isinstance(part, dict):
                if isinstance(part.get("functionCall"), dict):
                    call = True
                value = part.get("text")
                if isinstance(value, str) and value.strip() and part.get("thought") is not True:
                    text = True
    return text, call


def gemini_touch(state, ts):
    if ts is not None:
        state.last_line = ts if state.last_line is None else max(state.last_line, ts)


def gemini_idle(state):
    state.phase, state.pending, state.marker, state.in_flight = "idle", None, None, None


def gemini_generating(state, start, marker):
    state.phase, state.pending, state.marker, state.in_flight = "generating", start, marker, start


def gemini_model(msg):
    return msg.get("model") if isinstance(msg.get("model"), str) and msg.get("model") else None


def gemini_update(state, resp, end, tokens, model):
    resp.update(tokens)
    resp["endMs"] = max(resp["endMs"], end)
    resp["model"] = model or resp["model"]
    state.model = resp["model"] or state.model


def gemini_record(state, obj, dedupe):
    """One line of a Gemini CLI session (METRICS 4.6)."""
    if isinstance(obj.get("$rewindTo"), str):
        gemini_idle(state)
    elif isinstance(obj.get("$patch"), dict):
        removed = obj["$patch"].get("removeIds")
        if state.phase == "generating" and isinstance(removed, list) and state.marker in removed:
            gemini_idle(state)
    elif isinstance(obj.get("id"), str):
        gemini_message(state, obj, dedupe)
    elif isinstance(obj.get("$set"), dict):
        gemini_set(state, obj["$set"], dedupe)
    elif isinstance(obj.get("sessionId"), str) and isinstance(obj.get("projectHash"), str):
        gemini_header(state, obj, dedupe)


def gemini_header(state, obj, dedupe):
    state.thread_id = obj["sessionId"]
    if obj.get("kind") == "subagent":
        state.is_subagent = True
    started = parse_ts(obj.get("startTime"))
    if state.file_created is None:
        state.file_created = started
    gemini_touch(state, started)
    gemini_touch(state, parse_ts(obj.get("lastUpdated")))
    if isinstance(obj.get("messages"), list):
        for msg in obj["messages"]:
            if isinstance(msg, dict) and isinstance(msg.get("id"), str):
                gemini_message(state, msg, dedupe)


def gemini_set(state, values, dedupe):
    gemini_touch(state, parse_ts(values.get("lastUpdated")))
    if values.get("kind") == "subagent":
        state.is_subagent = True
    if isinstance(values.get("sessionId"), str):
        state.thread_id = values["sessionId"]
    if isinstance(values.get("messages"), list):
        gemini_checkpoint(state, values["messages"], dedupe)
    if isinstance(values.get("summary"), str) and values["summary"]:
        gemini_idle(state)


def gemini_checkpoint(state, messages, dedupe):
    ids = set()
    for msg in messages:
        if not isinstance(msg, dict) or not isinstance(msg.get("id"), str):
            continue
        ids.add(msg["id"])
        if msg.get("type") != "gemini" or not isinstance(msg.get("tokens"), dict):
            continue
        key, ts = msg["id"], parse_ts(msg.get("timestamp"))
        if ts is None or dedupe.owner(key) not in (None, state.path):
            continue
        tokens, model = gemini_tokens(msg["tokens"]), gemini_model(msg)
        resp = state.responses.get(key)
        if resp is None:
            dedupe.claim(key, state.path)
            emit(state, key, None, ts, tokens, model)
            state.model = model or state.model
        else:
            gemini_update(state, resp, ts, tokens, model)
    if state.phase == "generating" and state.marker not in ids:
        gemini_idle(state)


def gemini_message(state, msg, dedupe):
    ts = parse_ts(msg.get("timestamp"))
    if ts is None:
        return
    gemini_touch(state, ts)
    calls = msg.get("toolCalls") if isinstance(msg.get("toolCalls"), list) else []
    calls = [c for c in calls if isinstance(c, dict) and isinstance(c.get("id"), str)]
    call_ts = [t for t in (parse_ts(c.get("timestamp")) for c in calls) if t is not None]
    for t in call_ts:
        gemini_touch(state, t)
    for th in msg.get("thoughts") if isinstance(msg.get("thoughts"), list) else []:
        if isinstance(th, dict):
            gemini_touch(state, parse_ts(th.get("timestamp")))
    kind = msg.get("type")
    if kind == "user":
        gemini_generating(state, ts, msg["id"])
        return
    if kind == "error" or (kind == "info" and msg.get("content") == GEMINI_CANCELLED):
        gemini_idle(state)
        return
    if kind != "gemini":
        return
    key = msg["id"]
    if dedupe.owner(key) not in (None, state.path):
        return
    if isinstance(msg.get("tokens"), dict):
        tokens, model = gemini_tokens(msg["tokens"]), gemini_model(msg)
        resp = state.responses.get(key)
        if resp is None:
            start = None if call_ts and ts >= min(call_ts) else state.pending
            dedupe.claim(key, state.path)
            emit(state, key, start, ts, tokens, model)
            state.model = model or state.model
            has_text, has_call = gemini_content(msg.get("content"))
            state.phase = "tools" if has_call or not has_text else "idle"
            state.pending, state.marker, state.in_flight = None, None, None
        else:
            gemini_update(state, resp, ts, tokens, model)
    new_calls = [c for c in calls if c["id"] not in state.seen_calls]
    state.seen_calls.update(c["id"] for c in new_calls)
    if not new_calls:
        return
    if any(c.get("name") == "complete_task" and c.get("status") == "success" for c in new_calls):
        gemini_idle(state)
    elif all(c.get("status") == "cancelled" for c in new_calls):
        gemini_idle(state)
    else:
        times = [t for t in (parse_ts(c.get("timestamp")) for c in new_calls) if t is not None]
        gemini_generating(state, max(times) if times else ts, key)


def gemini_legacy(state, dedupe):
    """A legacy (<= v0.38) whole-document session file: replayed from the start on every change."""
    if os.path.getsize(state.path) > GEMINI_LEGACY_MAX:
        state.partial_history = True
        return
    try:
        with open(state.path, "rb") as f:
            obj = loads(f.read())
    except (ValueError, UnicodeDecodeError, RecursionError):
        state.malformed += 1
        return
    if isinstance(obj, dict) and isinstance(obj.get("sessionId"), str):
        gemini_header(state, obj, dedupe)
    else:
        state.malformed += 1


_gemini_projects = {}


def gemini_project(path):
    parts = re.split(r"[\\/]", path)
    if len(parts) >= 3 and parts[-2] == "chats":
        project_dir = os.sep.join(parts[:-2])
    elif len(parts) >= 4 and parts[-3] == "chats":
        project_dir = os.sep.join(parts[:-3])
    else:
        return None
    if project_dir in _gemini_projects:
        return _gemini_projects[project_dir]
    project = None
    try:
        with open(os.path.join(project_dir, ".project_root"), encoding="utf-8") as f:
            project = basename(f.read().strip())
    except OSError:
        try:
            registry = os.path.join(os.path.dirname(os.path.dirname(project_dir)), "projects.json")
            with open(registry, encoding="utf-8") as f:
                projects = json.load(f).get("projects")
            slug = os.path.basename(project_dir)
            if isinstance(projects, dict):
                project = next((basename(k) for k, v in projects.items() if v == slug), None)
        except (OSError, ValueError, AttributeError):
            project = None
    _gemini_projects[project_dir] = project
    return project


def gemini_roots(env=None, platform=None):
    env = os.environ if env is None else env
    platform = sys.platform if platform is None else platform
    home = env.get("GEMINI_CLI_HOME") or os.path.expanduser("~")
    found = [os.path.join(home, ".gemini", "tmp")]
    if platform == "darwin":
        found.append(os.path.join(home, ".cache", ".gemini", "tmp"))
    return found


def discover_gemini(root, limit=20_000):
    found = []
    if not os.path.isdir(root):
        return found
    for project in sorted(os.listdir(root)):
        chats = os.path.join(root, project, "chats")
        if os.path.islink(os.path.join(root, project)) or os.path.islink(chats) or not os.path.isdir(chats):
            continue
        for name in os.listdir(chats):
            full = os.path.join(chats, name)
            if os.path.islink(full):
                continue
            if os.path.isdir(full):
                for sub in os.listdir(full):
                    nested = os.path.join(full, sub)
                    if sub.endswith((".jsonl", ".json")) and os.path.isfile(nested) and not os.path.islink(nested):
                        found.append(nested)
            elif name.endswith((".jsonl", ".json")) and os.path.isfile(full):
                found.append(full)
    found.sort(key=lambda p: os.path.getmtime(p), reverse=True)
    return found[:limit]


# ----------------------------------------------------------------------------- omp (METRICS 4.7)

def omp_touch(state, ts):
    state.last_line = ts if state.last_line is None else max(state.last_line, ts)


def omp_tokens(usage):
    cr, cw = num(usage.get("cacheRead")), num(usage.get("cacheWrite"))
    return dict(input=cap(num(usage.get("input")) + cr + cw), cachedInput=cr, cacheWriteInput=cw,
                output=num(usage.get("output")), reasoning=num(usage.get("reasoningTokens")))


def omp_has_usage(usage):
    return any(num(usage.get(f)) > 0 for f in ("input", "output", "cacheRead", "cacheWrite"))


def omp_guard(obj):
    return (all(isinstance(obj.get(f), str) for f in ("model", "provider", "api"))
            and isinstance(obj.get("usage"), dict))


def omp_line(state, obj, dedupe):
    """One line of an omp session file (METRICS 4.7)."""
    kind = obj.get("type")
    if kind == "title":
        return
    if kind == "session":
        if state.header_seen:
            return
        state.header_seen = True
        state.file_created = parse_ts(obj.get("timestamp"))
        state.project = basename(obj.get("cwd"))
        state.thread_id = obj.get("id") if isinstance(obj.get("id"), str) else None
        if state.file_created is not None:
            omp_touch(state, state.file_created)
        return
    if not state.header_seen or not isinstance(obj.get("id"), str):
        return
    ts = parse_ts(obj.get("timestamp"))
    if ts is None or (state.file_created is not None and ts < state.file_created - OMP_COPY_MS):
        return
    omp_touch(state, ts)
    if kind == "message" and isinstance(obj.get("message"), dict):
        msg = obj["message"]
        role = msg.get("role")
        if role in ("user", "developer"):
            state.phase, state.in_flight = "generating", ts
            state.pending_calls.clear()
        elif role == "assistant":
            omp_assistant(state, obj, msg, ts, dedupe)
        elif role == "toolResult":
            state.pending_calls.discard(msg.get("toolCallId"))
            if msg.get("toolName") == "yield" and msg.get("isError") is not True:
                state.phase, state.in_flight = "idle", None
            elif state.phase == "tools" and not state.pending_calls:
                state.phase, state.in_flight = "generating", ts
    elif kind == "model_usage":
        if not omp_guard(obj):
            return
        key = "omp:%s:%d" % (obj["id"], ts)
        if dedupe.owner(key) not in (None, state.path) or not omp_has_usage(obj["usage"]):
            return
        dedupe.claim(key, state.path)
        emit(state, key, None, ts, omp_tokens(obj["usage"]), obj["model"], provider=obj["provider"] or None)
    elif kind == "custom" and obj.get("customType") == "session_exit":
        state.phase, state.in_flight = "idle", None
        state.pending_calls.clear()


def omp_assistant(state, entry, msg, ts, dedupe):
    if not omp_guard(msg):
        return
    started = trunc64(msg["timestamp"]) if finite(msg.get("timestamp")) and msg["timestamp"] > 0 else None
    key = "omp:%s:%d" % (entry["id"], started if started is not None else ts)
    if dedupe.owner(key) not in (None, state.path):
        return
    stop = msg.get("stopReason") if isinstance(msg.get("stopReason"), str) else "aborted"
    duration, completed = msg.get("duration"), msg.get("completedAt")
    if started is not None and finite(duration) and duration >= 0:
        end = min(started + trunc64(duration), INT64_MAX)
    elif finite(completed) and completed > 0:
        end = trunc64(completed)
    else:
        end = ts
    if omp_has_usage(msg["usage"]):
        dedupe.claim(key, state.path)
        emit(state, key, None if stop in ("error", "aborted") else started, end, omp_tokens(msg["usage"]),
             msg["model"], provider=msg["provider"] or None)
        state.model = msg["model"]
    content = msg.get("content") if isinstance(msg.get("content"), list) else []
    calls = {b["id"] for b in content if isinstance(b, dict) and b.get("type") == "toolCall"
             and isinstance(b.get("id"), str)}
    details = msg.get("stopDetails") if isinstance(msg.get("stopDetails"), dict) else {}
    if stop in ("error", "aborted"):
        state.phase, state.in_flight = "idle", None
        state.pending_calls.clear()
    elif calls and stop in ("toolUse", "stop", "length"):
        state.phase, state.in_flight, state.pending_calls = "tools", None, calls
    elif stop == "stop" and details.get("type") == "pause_turn":
        state.phase, state.in_flight = "generating", end
    else:
        state.phase, state.in_flight = "idle", None


def omp_read_header(state, dedupe):
    """Reading that starts mid-file first takes the header from the first two lines (METRICS 4.7)."""
    with open(state.path, "rb") as f:
        for _ in range(2):
            raw = f.readline(MAX_LINE)
            try:
                obj = loads(raw)
            except (ValueError, UnicodeDecodeError, RecursionError):
                continue
            if isinstance(obj, dict) and obj.get("type") == "session":
                (pi_line if state.source == "pi" else omp_line)(state, obj, dedupe)
                if state.tasks is not None:
                    state.tasks.feed(state, obj, dedupe, raw)
                return


# ----------------------------------------------------------------------------- Pi (METRICS 4.13)

PI_FAILED = ("error", "aborted", "pending", "deferred")


def pi_tokens(usage):
    cr, cw = num(usage.get("cacheRead")), num(usage.get("cacheWrite"))
    return dict(input=cap(num(usage.get("input")) + cr + cw), cachedInput=cr, cacheWriteInput=cw,
                cacheWrite1hInput=min(num(usage.get("cacheWrite1h")), cw), output=num(usage.get("output")),
                reasoning=num(usage.get("reasoning")))


def pi_side(state, entry_id, ts, usage, model, provider, dedupe):
    """Side usage (usage entries, tool results, compaction and branch summaries): no timing, no facts."""
    if not isinstance(usage, dict) or not omp_has_usage(usage):
        return
    key = "pi:%s:%d" % (entry_id, ts)
    if dedupe.owner(key) not in (None, state.path):
        return
    dedupe.claim(key, state.path)
    emit(state, key, None, ts, pi_tokens(usage), model, provider=provider or None)


def pi_effort(state, raw, evidence, scope):
    fact = modes.normalize("effort", raw, None, evidence, scope) if isinstance(raw, str) else None
    if fact is not None:
        state.facts = [f for f in state.facts if f["kind"] != "effort"] + [fact]


def pi_line(state, obj, dedupe):
    """One line of a Pi session file (METRICS 4.13: the omp rules of 4.7 with Pi's differences)."""
    kind = obj.get("type")
    if kind == "session":
        if state.header_seen:
            return
        state.header_seen = True
        state.file_created = parse_ts(obj.get("timestamp"))
        state.project = basename(obj.get("cwd"))
        state.thread_id = obj.get("id") if isinstance(obj.get("id"), str) else None
        if state.file_created is not None:
            omp_touch(state, state.file_created)
        return
    if not state.header_seen or not isinstance(obj.get("id"), str):
        return
    ts = parse_ts(obj.get("timestamp"))
    if ts is None or (state.file_created is not None and ts < state.file_created - OMP_COPY_MS):
        return
    omp_touch(state, ts)
    if kind == "message" and isinstance(obj.get("message"), dict):
        msg = obj["message"]
        role = msg.get("role")
        if role in ("user", "developer"):
            state.phase, state.in_flight = "generating", ts
            state.pending_calls.clear()
        elif role == "assistant":
            pi_assistant(state, obj, msg, ts, dedupe)
        elif role == "toolResult":
            pi_side(state, obj["id"], ts, msg.get("usage"), None, None, dedupe)
            state.pending_calls.discard(msg.get("toolCallId"))
            if state.phase == "tools" and not state.pending_calls:
                state.phase, state.in_flight = "generating", ts
    elif kind == "usage":
        if isinstance(obj.get("provider"), str) and isinstance(obj.get("model"), str):
            pi_side(state, obj["id"], ts, obj.get("usage"), obj["model"], obj["provider"], dedupe)
    elif kind in ("compaction", "branch_summary"):
        pi_side(state, obj["id"], ts, obj.get("usage"), None, None, dedupe)
    elif kind == "thinking_level_change":
        pi_effort(state, obj.get("thinkingLevel"), "selected", "session")


def pi_assistant(state, entry, msg, ts, dedupe):
    if not omp_guard(msg):
        return
    started = trunc64(msg["timestamp"]) if finite(msg.get("timestamp")) and msg["timestamp"] > 0 else None
    key = "pi:%s:%d" % (entry["id"], started if started is not None else ts)
    if dedupe.owner(key) not in (None, state.path):
        return
    stop = msg.get("stopReason") if isinstance(msg.get("stopReason"), str) else "aborted"
    duration = msg.get("durationMs")
    end = min(started + trunc64(duration), INT64_MAX) if started is not None and finite(duration) and duration >= 0 \
        else ts
    pi_effort(state, msg.get("thinkingLevel"), "recorded", "request")
    if omp_has_usage(msg["usage"]):
        dedupe.claim(key, state.path)
        emit(state, key, None if stop in PI_FAILED else started, end, pi_tokens(msg["usage"]), msg["model"],
             provider=msg["provider"] or None)
        state.response_facts[key] = list(state.facts)
        state.model = msg["model"]
    content = msg.get("content") if isinstance(msg.get("content"), list) else []
    calls = {b["id"] for b in content if isinstance(b, dict) and b.get("type") == "toolCall"
             and isinstance(b.get("id"), str)}
    if stop in PI_FAILED:
        state.phase, state.in_flight = "idle", None
        state.pending_calls.clear()
    elif calls and stop in ("toolUse", "stop", "length"):
        state.phase, state.in_flight, state.pending_calls = "tools", None, calls
    else:
        state.phase, state.in_flight = "idle", None


def pi_parent_file(path):
    """A Pi sub-agent lives in a folder named after its session file: that file, or None (METRICS 4.13)."""
    candidate = os.path.dirname(path) + ".jsonl"
    return candidate if os.path.isfile(candidate) and not os.path.islink(candidate) else None


def _expand_home(path, home):
    return os.path.join(home, path[2:]) if path in ("~",) or path.startswith(("~/", "~\\")) else path


def pi_roots(env=None, home=None):
    """The automatic Pi roots (METRICS 2, 4.13): <agent dir>/sessions, then the custom session folders."""
    env = os.environ if env is None else env
    home = home or os.path.expanduser("~")
    agent_dir = _expand_home(env["PI_CODING_AGENT_DIR"], home) if env.get("PI_CODING_AGENT_DIR") \
        else os.path.join(home, ".pi", "agent")
    roots = [os.path.join(agent_dir, "sessions")]
    try:
        with open(os.path.join(agent_dir, "settings.json"), "rb") as fh:
            settings = json.loads(fh.read(1_048_576))
    except (OSError, ValueError, UnicodeDecodeError, RecursionError):
        settings = None
    custom = settings.get("sessionDir") if isinstance(settings, dict) else None
    if isinstance(custom, str) and custom.strip():
        roots.append(_expand_home(custom.strip(), home))
    if env.get("PI_CODING_AGENT_SESSION_DIR"):
        roots.append(_expand_home(env["PI_CODING_AGENT_SESSION_DIR"], home))
    return list(OrderedDict.fromkeys(os.path.normpath(r) for r in roots))


def without_pi(omp_dirs, pi_dirs):
    """omp roots that are not a Pi root and do not lie inside one (METRICS 4.7)."""
    pis = [os.path.normpath(p) for p in pi_dirs]
    return [d for d in omp_dirs
            if not any(os.path.normpath(d) == p or os.path.normpath(d).startswith(p + os.sep) for p in pis)]


def discover_pi(root, limit=20_000):
    found = []
    if not os.path.isdir(root):
        return found
    base_depth = root.rstrip(os.sep).count(os.sep)
    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        if dirpath.count(os.sep) - base_depth >= 6:
            dirnames[:] = []
        dirnames[:] = [d for d in dirnames if not d.startswith(".") and not os.path.islink(os.path.join(dirpath, d))]
        for name in filenames:
            path = os.path.join(dirpath, name)
            if name.endswith(".jsonl") and not name.startswith(".") and not os.path.islink(path) \
                    and os.path.isfile(path):
                found.append(path)
    found.sort(key=lambda p: os.path.getmtime(p), reverse=True)
    return found[:limit]


def _subdirs(path):
    try:
        return [n for n in os.listdir(path)
                if os.path.isdir(os.path.join(path, n)) and not os.path.islink(os.path.join(path, n))]
    except OSError:
        return []


def omp_roots(env=None, platform=None, home=None):
    """The automatic omp roots (METRICS 2, 4.7): the default root, then one per named profile."""
    env = os.environ if env is None else env
    platform = sys.platform if platform is None else platform
    home = home or os.path.expanduser("~")
    cfg = os.path.join(home, (env.get("PI_CONFIG_DIR") or ".omp").lstrip("/\\"))
    xdg = env.get("XDG_DATA_HOME") if platform == "darwin" else None
    xdg_omp = os.path.join(xdg, "omp") if xdg else None
    if env.get("PI_CODING_AGENT_DIR"):
        roots = [os.path.join(env["PI_CODING_AGENT_DIR"], "sessions")]
    elif xdg_omp and os.path.isdir(xdg_omp):
        roots = [os.path.join(xdg_omp, "sessions")]
    else:
        roots = [os.path.join(cfg, "agent", "sessions")]
    names = set(_subdirs(os.path.join(cfg, "profiles")))
    if xdg_omp:
        names |= set(_subdirs(os.path.join(xdg_omp, "profiles")))
    for name in sorted(names):
        xdg_profile = os.path.join(xdg_omp, "profiles", name) if xdg_omp else None
        if xdg_profile and os.path.isdir(xdg_profile):
            roots.append(os.path.join(xdg_profile, "sessions"))
        else:
            roots.append(os.path.join(cfg, "profiles", name, "agent", "sessions"))
    return list(OrderedDict.fromkeys(roots))


def discover_omp(root, limit=20_000):
    found = []
    if not os.path.isdir(root):
        return found
    base_depth = root.rstrip(os.sep).count(os.sep)
    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        depth = dirpath.rstrip(os.sep).count(os.sep) - base_depth
        dirnames[:] = [] if depth >= 6 else [d for d in dirnames if not d.startswith(".")
                                              and not os.path.islink(os.path.join(dirpath, d))]
        if depth < 1:
            continue
        for name in filenames:
            path = os.path.join(dirpath, name)
            if name.endswith(".jsonl") and not name.startswith(".") and not os.path.islink(path):
                found.append(path)
    found.sort(key=lambda p: os.path.getmtime(p), reverse=True)
    return found[:limit]


# ----------------------------------------------------------------------------- OpenClaw (METRICS 4.14)

OC_DB = "openclaw-agent.sqlite"
OC_PROFILE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")
OC_EMBEDDED = ("", "openclaw", "pi", "auto", "default")
OC_STOPS = ("stop", "length", "toolUse", "error", "aborted")
OC_FAILED = ("error", "aborted")
OC_EVENT_MAX = 4 * 1024 * 1024


def openclaw_state_dirs(env=None, home=None):
    """The automatic OpenClaw state folders (METRICS 2, 4.14): the override alone, else .openclaw and its profiles."""
    env = os.environ if env is None else env
    home = home or os.path.expanduser("~")
    override = (env.get("OPENCLAW_STATE_DIR") or "").strip()
    if override:
        return [os.path.normpath(_expand_home(override, home))]
    base_raw = (env.get("OPENCLAW_HOME") or "").strip()
    base = _expand_home(base_raw, home) if base_raw else home
    try:
        names = sorted(n for n in os.listdir(base) if os.path.isdir(os.path.join(base, n)))
    except OSError:
        names = []
    roots = [os.path.join(base, ".openclaw")]
    for name in names:
        profile = name[len(".openclaw-"):] if name.startswith(".openclaw-") else None
        if profile and OC_PROFILE.match(profile) and profile.lower() != "default":
            roots.append(os.path.join(base, name))
    return list(OrderedDict.fromkeys(os.path.normpath(r) for r in roots))


def discover_openclaw(root):
    """Agent databases under one root: a state folder, an agent folder (it holds the database) or the database."""
    def regular(path):
        return os.path.isfile(path) and not os.path.islink(path)
    if regular(root):
        return [root] if os.path.basename(root) == OC_DB else []
    if regular(os.path.join(root, OC_DB)):
        return [os.path.join(root, OC_DB)]
    agents = os.path.join(root, "agents")
    try:
        names = sorted(os.listdir(agents))
    except OSError:
        return []
    found = []
    for name in names:
        folder = os.path.join(agents, name)
        if name.startswith(".") or os.path.islink(folder) or not os.path.isdir(folder):
            continue
        path = os.path.join(folder, "agent", OC_DB)
        if regular(path):
            found.append(path)
    return found


def openclaw_codex_roots(databases):
    """Codex roots OpenClaw adds (METRICS 2): the agent-scoped Codex home next to every agent database."""
    return [os.path.join(os.path.dirname(db), "codex-home", "sessions") for db in databases]


def openclaw_tokens(usage):
    cr, cw = num(usage.get("cacheRead")), num(usage.get("cacheWrite"))
    return dict(input=cap(num(usage.get("input")) + cr + cw), cachedInput=cr, cacheWriteInput=cw,
                cacheWrite1hInput=min(num(usage.get("cacheWrite1h")), cw), output=num(usage.get("output")),
                reasoning=num(usage.get("reasoningTokens")))


def openclaw_entry(state, obj, dedupe):
    """One transcript entry of an OpenClaw window (METRICS 4.14: the Pi rules of 4.7 and 4.13 with its differences)."""
    kind = obj.get("type")
    if kind == "session":
        if state.header_seen:
            return
        state.header_seen = True
        state.file_created = parse_ts(obj.get("timestamp"))
        state.project = basename(obj.get("cwd"))
        if state.file_created is not None:
            omp_touch(state, state.file_created)
        return
    if not state.header_seen or not isinstance(obj.get("id"), str):
        return
    ts = parse_ts(obj.get("timestamp"))
    if ts is None or (state.file_created is not None and ts < state.file_created - OMP_COPY_MS):
        return
    omp_touch(state, ts)
    if kind != "message" or not isinstance(obj.get("message"), dict):
        return
    msg = obj["message"]
    role = msg.get("role")
    if role == "assistant":
        openclaw_assistant(state, obj, msg, ts, dedupe)
    elif state.oc_delegated:
        return
    elif role in ("user", "developer"):
        state.phase, state.in_flight = "generating", ts
        state.pending_calls.clear()
    elif role == "toolResult":
        state.pending_calls.discard(msg.get("toolCallId"))
        if state.phase == "tools" and not state.pending_calls:
            state.phase, state.in_flight = "generating", ts


def openclaw_assistant(state, entry, msg, ts, dedupe):
    if not omp_guard(msg):
        return
    started = trunc64(msg["timestamp"]) if finite(msg.get("timestamp")) and msg["timestamp"] > 0 else None
    key = "openclaw:%s:%d" % (entry["id"], started if started is not None else ts)
    if dedupe.owner(key) not in (None, state.path):
        return
    stop = msg.get("stopReason") if msg.get("stopReason") in OC_STOPS else "aborted"
    meta = msg.get("__openclaw") if isinstance(msg.get("__openclaw"), dict) else {}
    origin = meta.get("mirrorOrigin")
    mirror = ((isinstance(origin, str) and origin != "") or (msg["api"] == "cli" and msg["provider"] == "claude-cli")
              or msg["provider"].startswith("acp-") or state.oc_mirror_window)
    usage = msg["usage"]
    report = usage.get("usageReport") if isinstance(usage.get("usageReport"), dict) else {}
    if not mirror and report.get("state") != "unavailable" and omp_has_usage(usage):
        timed = not state.oc_delegated and msg["api"] != "cli" and stop not in OC_FAILED
        dedupe.claim(key, state.path)
        emit(state, key, started if timed else None, ts, openclaw_tokens(usage), msg["model"],
             provider=msg["provider"] or None)
        state.response_facts[key] = []
        state.model = msg["model"]
    if state.oc_delegated:
        return
    content = msg.get("content") if isinstance(msg.get("content"), list) else []
    calls = {b["id"] for b in content if isinstance(b, dict) and b.get("type") == "toolCall"
             and isinstance(b.get("id"), str)}
    if stop in OC_FAILED:
        state.phase, state.in_flight = "idle", None
        state.pending_calls.clear()
    elif calls and stop in ("toolUse", "stop", "length"):
        state.phase, state.in_flight, state.pending_calls = "tools", None, calls
    else:
        state.phase, state.in_flight = "idle", None


def openclaw_event(state, text, blob, size, with_raw=False):
    """The entry of one transcript row: event_json, else the zstd frame decoded within event_utf8_bytes (with its raw
    text when `with_raw`)."""
    entry = openclaw_entry_raw(state, text, blob, size)
    if entry is None:
        return None
    return entry if with_raw else entry[0]


def openclaw_entry_raw(state, text, blob, size):
    if isinstance(text, str):
        raw = text.encode("utf-8", "surrogatepass")
    elif isinstance(blob, bytes) and isinstance(size, int) and 0 < size <= OC_EVENT_MAX:
        out = subprocess.run(["zstd", "-dc", "-q"], input=blob, capture_output=True)
        if out.returncode != 0 or len(out.stdout) > size:
            state.malformed += 1
            return None
        raw = out.stdout
    else:
        state.malformed += 1
        return None
    if len(raw) > MAX_LINE:
        return None
    try:
        obj = loads(raw)
    except (ValueError, UnicodeDecodeError, RecursionError):
        state.malformed += 1
        return None
    if not isinstance(obj, dict):
        state.malformed += 1
        return None
    return obj, raw


def openclaw_states(db_path, dedupe, stop_at=None, tasks=False):
    """Parses one agent database (METRICS 4.14): one state per transcript window, in created_at order, or None when the
    database has no session tables. `stop_at` = (session_id, seq) stops after that row (phase traces)."""
    db = sqlite_readonly(db_path)
    try:
        tables = {r[0] for r in db.execute("select name from sqlite_master where type='table'")}
        if not {"session_windows", "transcript_events"} <= tables:
            return None
        wcols = {r[1] for r in db.execute("pragma table_info(session_windows)")}
        ecols = {r[1] for r in db.execute("pragma table_info(transcript_events)")}

        def col(name, default):
            return name if name in wcols else default

        windows = db.execute("select session_id, created_at, %s, %s, %s from session_windows "
                             "order by created_at, session_id" % (col("spawned_by", "null"),
                                                                  col("agent_harness_id", "null"),
                                                                  col("acp_owned", "0"))).fetchall()
        current = dict(db.execute("select session_key, current_session_id from session_nodes")) \
            if "session_nodes" in tables else {}
        zstd_cols = "event_zstd, event_utf8_bytes" if {"event_zstd", "event_utf8_bytes"} <= ecols else "null, null"
        states, parent_of = [], {}
        for sid, _created, spawned, harness, acp in windows:
            st = FileState("%s#%s" % (db_path, sid), "openclaw")
            runtime = harness.strip().lower() if isinstance(harness, str) else ""
            st.oc_delegated = acp == 1 or runtime not in OC_EMBEDDED
            st.oc_mirror_window = acp == 1 or runtime in ("codex", "claude-cli") or runtime.startswith("acp")
            st.is_subagent = isinstance(spawned, str) and spawned != ""
            parent_of[st.path] = current.get(spawned) if st.is_subagent else None
            if tasks:
                import task_events
                task_events.attach(st)
            states.append(st)
            for seq, text, blob, size in db.execute("select seq, event_json, %s from transcript_events "
                                                    "where session_id = ? order by seq" % zstd_cols, (sid,)):
                entry = openclaw_event(st, text, blob, size, with_raw=True)
                if entry is not None:
                    openclaw_entry(st, entry[0], dedupe)
                    if st.tasks is not None:
                        st.tasks.feed(st, entry[0], dedupe, entry[1])
                if stop_at == (sid, seq):
                    break
            if stop_at is not None and stop_at[0] == sid:
                break
        tracked = {st.path for st in states}
        for st in states:
            parent = parent_of[st.path]
            st.parent_session = "%s#%s" % (db_path, parent) if parent and "%s#%s" % (db_path, parent) in tracked \
                else None
        return states
    finally:
        db.close()


# ----------------------------------------------------------------------------- Hermes Agent (METRICS 4.15)

HERMES_FILE = re.compile(r"agent\.log(?:\.[0-9]+)?")
HERMES_RECORD = re.compile(r"([0-9]{4})-([0-9]{2})-([0-9]{2}) ([0-9]{2}):([0-9]{2}):([0-9]{2}),([0-9]{3}) "
                           r"(DEBUG|INFO|WARNING|ERROR|CRITICAL)( .*)", re.S)
HERMES_TURN = re.compile(r"conversation turn: session=[^ ]* model=(.*?) provider=[^ ]* platform=([^ ]*) "
                         r"history=-?[0-9]+ msg=", re.S)
HERMES_CALL = re.compile(r"API call #([0-9]+): model=(.*?) provider=([^ ]*) in=(-?[0-9]+|\?) out=(-?[0-9]+|\?) "
                         r"total=(-?[0-9]+|\?) latency=(-?[0-9]+(?:\.[0-9]+)?)s"
                         r"(?: cache=(-?[0-9]+)/-?[0-9]+ \(-?[0-9]+%\))?(?: write=(-?[0-9]+))?(?: .*)?", re.S)
HERMES_LOG_PREFIX = re.compile(r"\[[^\]]*\] *")
HERMES_FAILED = ("API call failed after ", "Non-retryable client error: ")
HERMES_LOGGERS = ("agent.turn_context", "agent.conversation_loop")
HERMES_CONFIG_MAX = 1 << 20
HERMES_MODEL_HEADER = re.compile(r"(?:model|\"model\"|'model')[ \t]*:(?:[ \t]+(?:#.*)?)?")
HERMES_RUNTIME_KEY = re.compile(r"(?:openai_runtime|\"openai_runtime\"|'openai_runtime')[ \t]*:(|[ \t].*)")
HERMES_CODEX_VALUE = re.compile(r"[ \t]+(?:\"[ \t]*codex_app_server[ \t]*\"|'[ \t]*codex_app_server[ \t]*'|codex_app_server)"
                                r"(?:[ \t]+#.*)?[ \t]*", re.I | re.A)


def hermes_home(env=None, platform=None, home=None):
    """The Hermes home of METRICS 4.15: $HERMES_HOME (the root of a profile home), else the platform default."""
    env = os.environ if env is None else env
    platform = platform or sys.platform
    home = home or os.path.expanduser("~")
    paths = ntpath if platform == "win32" else posixpath
    if platform == "win32":
        local = (env.get("LOCALAPPDATA") or "").strip()
        default = paths.join(local or paths.join(home, "AppData", "Local"), "hermes")
    else:
        default = paths.join(home, ".hermes")
    raw = (env.get("HERMES_HOME") or "").strip()
    if raw == "~" or raw.startswith(("~/", "~\\")):
        raw = home + raw[1:]
    if not raw or not paths.isabs(raw):
        return default
    value = paths.normpath(raw)
    fold = (lambda p: paths.normcase(p)) if platform == "win32" else (lambda p: p)
    base = fold(paths.normpath(default))
    if fold(value) == base or fold(value).startswith(base.rstrip(paths.sep) + paths.sep):
        return default
    parent = paths.dirname(value)
    if paths.basename(parent) == "profiles":
        return paths.dirname(parent)
    return value


def hermes_log_folders(home):
    """`<home>/logs`, then `<home>/profiles/<name>/logs` in ordinal order; missing folders skipped."""
    folders = [os.path.join(home, "logs")]
    profiles = os.path.join(home, "profiles")
    if os.path.isdir(profiles):
        for name in sorted(os.listdir(profiles)):
            path = os.path.join(profiles, name)
            if not name.startswith(".") and os.path.isdir(path) and not os.path.islink(path):
                folders.append(os.path.join(path, "logs"))
    return [f for f in OrderedDict.fromkeys(folders) if os.path.isdir(f)]


def hermes_setting_folders(setting):
    """The user setting: a Hermes home (it has a `logs` folder) or one log folder."""
    if os.path.isdir(os.path.join(setting, "logs")):
        return hermes_log_folders(setting)
    return [setting] if os.path.isdir(setting) else []


def discover_hermes(folder):
    """`agent.log` and `agent.log.<n>` directly in a log folder, regular files only."""
    if not os.path.isdir(folder):
        return []
    return [os.path.join(folder, name) for name in sorted(os.listdir(folder))
            if HERMES_FILE.fullmatch(name) and os.path.isfile(os.path.join(folder, name))
            and not os.path.islink(os.path.join(folder, name))]


def hermes_config_path(folder, platform=None):
    """The `config.yaml` of a log folder's profile (METRICS 4.15 Codex runtime): none unless the folder is `logs`."""
    folder = os.path.normpath(folder)
    name = os.path.basename(folder)
    if (name.lower() if (platform or sys.platform) == "win32" else name) != "logs":
        return None
    return os.path.join(os.path.dirname(folder), "config.yaml")


def hermes_codex_runtime(text):
    """Whether config.yaml text selects the Codex runtime: the last `openai_runtime` key line of a top-level `model`
    block decides (METRICS 4.15 Codex runtime). A tolerant line reader, not a YAML parser."""
    if text.startswith("\ufeff"):
        text = text[1:]
    codex, in_model, indent = False, False, None
    for line in text.split("\n"):
        if line.endswith("\r"):
            line = line[:-1]
        rest = line.lstrip(" \t")
        if not rest or rest.startswith("#"):
            continue
        body = line.lstrip(" ")
        depth = len(line) - len(body)
        if depth == 0:
            in_model, indent = HERMES_MODEL_HEADER.fullmatch(line) is not None, None
            continue
        if not in_model:
            continue
        if indent is None:
            indent = depth
        if depth == indent:
            key = HERMES_RUNTIME_KEY.fullmatch(body)
            if key:
                codex = HERMES_CODEX_VALUE.fullmatch(key.group(1)) is not None
    return codex


def hermes_lines(path, start_offset=0, max_lines=None):
    """Complete physical lines from `start_offset`, `\\r\\n` or `\\n`, decoded as UTF-8 with replacement."""
    with open(path, "rb") as f:
        f.seek(start_offset)
        data = f.read()
    end = data.rfind(b"\n")
    if end < 0:
        return
    for count, raw in enumerate(data[:end].split(b"\n"), start=1):
        if max_lines is not None and count > max_lines:
            return
        if len(raw) > MAX_LINE:
            continue
        if raw.endswith(b"\r"):
            raw = raw[:-1]
        yield raw.decode("utf-8", errors="replace")


def hermes_wall_ms(m, tz):
    """The record timestamp (local wall time, no zone) as Unix ms: fold 0, `tz` or this computer's zone."""
    y, mo, d, h, mi, s, frac = (int(m.group(i)) for i in range(1, 8))
    if not (1970 <= y <= 9999 and 1 <= mo <= 12 and 1 <= d <= days_in_month(y, mo) and h <= 23 and mi <= 59
            and s <= 59):
        return None
    try:
        dt = datetime(y, mo, d, h, mi, s, tzinfo=tz) if tz is not None else datetime(y, mo, d, h, mi, s)
        return int(dt.timestamp()) * 1000 + frac
    except (OverflowError, OSError, ValueError):
        return None


def hermes_split(rest, logger):
    """(session id, message) when `rest` (the text after the level) belongs to `logger`; the id is empty untagged."""
    head = " " + logger + ": "
    if rest.startswith(head):
        return "", rest[len(head):]
    if rest.startswith(" ["):
        end = rest.find("] " + logger + ": ", 2)
        if end > 2:
            return rest[2:end], rest[end + len(logger) + 4:]
    return None


def hermes_count(text):
    """A token count of the line: an integer literal, negatives 0, capped (METRICS 3.2); absent 0."""
    return 0 if text is None else min(max(int(text), 0), TOKEN_CAP)


def hermes_latency_ms(text):
    """The latency's decimal digits × 1000, truncated to whole milliseconds and saturated (METRICS 3.6)."""
    whole, _, frac = text.partition(".")
    return min(int(whole) * 1000 + int((frac + "000")[:3]), INT64_MAX)


class HermesSet:
    """One log folder: one parser state for all its files, one agent (FileState) per session id."""

    def __init__(self, folder, tz=None):
        self.folder, self.tz = folder, tz
        self.agents = OrderedDict()
        self.malformed = 0
        self.codex_runtime = False
        self.config_sig = None

    def check_config(self):
        """Re-reads the profile's config.yaml when its modification time or size changed (METRICS 4.15)."""
        path = hermes_config_path(self.folder)
        try:
            st = os.stat(path) if path else None
        except (FileNotFoundError, NotADirectoryError):
            st = None
        except OSError:
            return
        if st is None or not stat.S_ISREG(st.st_mode):
            self.config_sig = None
            self.set_codex_runtime(False)
            return
        sig = (st.st_mtime_ns, st.st_size)
        if sig == self.config_sig:
            return
        data = b""
        if st.st_size <= HERMES_CONFIG_MAX:
            try:
                with open(path, "rb") as f:
                    data = f.read(HERMES_CONFIG_MAX + 1)
            except OSError:
                return
        self.config_sig = sig
        self.set_codex_runtime(len(data) <= HERMES_CONFIG_MAX
                               and hermes_codex_runtime(data.decode("utf-8", errors="replace")))

    def set_codex_runtime(self, on):
        if on and not self.codex_runtime:
            for state in self.agents.values():
                if state.phase == "generating":
                    state.phase, state.in_flight = "idle", None
        self.codex_runtime = on

    def agent(self, sid, ts):
        state = self.agents.get(sid)
        if state is None:
            state = FileState(self.folder + "#" + sid, "hermes")
            state.source_decided = True
            self.agents[sid] = state
        state.last_line = ts if state.last_line is None else max(state.last_line, ts)
        return state

    def line(self, text, dedupe):
        m = HERMES_RECORD.fullmatch(text)
        if m is None:
            return
        level, rest = m.group(8), m.group(9)
        for logger in HERMES_LOGGERS:
            split = hermes_split(rest, logger)
            if split is not None:
                break
        else:
            return
        sid, msg = split
        kind = None
        if logger == "agent.turn_context":
            turn = HERMES_TURN.match(msg) if level == "INFO" else None
            kind = "turn" if turn else None
        elif level == "INFO" and msg.startswith("API call #"):
            kind = "call"
        elif level in ("INFO", "WARNING") and msg.startswith(("Turn ended: ", "Turn ended with pending tool result")):
            kind = "end"
        elif level == "ERROR":
            prefix = HERMES_LOG_PREFIX.match(msg)
            kind = "end" if msg[prefix.end() if prefix else 0:].startswith(HERMES_FAILED) else None
        if kind is None:
            return
        ts = hermes_wall_ms(m, self.tz)
        if ts is None:
            return
        if kind == "turn":
            state = self.agent(sid, ts)
            state.phase, state.in_flight = ("idle", None) if self.codex_runtime else ("generating", ts)
            if turn.group(1):
                state.model = turn.group(1)
            state.is_subagent = turn.group(2) == "subagent"
        elif kind == "end":
            state = self.agent(sid, ts)
            state.phase, state.in_flight = "idle", None
        else:
            self.call(sid, msg, text[:23], ts, dedupe)

    def call(self, sid, msg, stamp, ts, dedupe):
        c = HERMES_CALL.fullmatch(msg)
        if c is None:
            self.malformed += 1
            return
        state = self.agent(sid, ts)
        key = "hermes:%s:%s:%s" % (sid, c.group(1), stamp)
        if dedupe.owner(key) is not None:
            return
        dedupe.claim(key, state.path)
        model = c.group(2) or None
        if model:
            state.model = model
        if state.phase == "generating":
            state.in_flight = ts
        if c.group(4) == "?" or c.group(5) == "?":
            return
        tokens = dict(input=hermes_count(c.group(4)), cachedInput=hermes_count(c.group(8)),
                      cacheWriteInput=hermes_count(c.group(9)), output=hermes_count(c.group(5)), reasoning=0)
        if tokens["input"] == 0 and tokens["output"] == 0:
            return
        latency = c.group(7)
        start = None if latency.startswith("-") else max(ts - hermes_latency_ms(latency), -INT64_MAX - 1)
        provider = c.group(3) if c.group(3) not in ("", "unknown") else None
        emit(state, key, start, ts, tokens, model, provider=provider)

    def feed(self, path, dedupe, start_offset=0, max_lines=None):
        for text in hermes_lines(path, start_offset, max_lines):
            self.line(text, dedupe)


def hermes_files(folder, since_ms=None):
    """A log set's files in startup order (modification time, then path); files older than `since_ms` start at their
    end and are not read (METRICS 5)."""
    timed = sorted((os.path.getmtime(p), p) for p in discover_hermes(folder))
    return [p for t, p in timed if since_ms is None or t * 1000 >= since_ms]


def hermes_sets(folders, dedupe, tz=None, since_ms=None):
    sets = []
    for folder in OrderedDict.fromkeys(folders):
        hs = HermesSet(folder, tz)
        hs.check_config()
        for path in hermes_files(folder, since_ms):
            hs.feed(path, dedupe)
        sets.append(hs)
    return sets


class HermesTail:
    """Live reading of one log set (METRICS 4.15 Reading and rotation): files followed by identity across renames."""

    def __init__(self, folder, tz=None):
        self.set = HermesSet(folder, tz)
        self.offsets = {}

    def read(self, dedupe):
        self.set.check_config()
        timed = []
        for path in discover_hermes(self.set.folder):
            st = os.stat(path)
            timed.append((st.st_mtime_ns, path, (st.st_dev, st.st_ino), st.st_size))
        offsets = {}
        for _, path, ident, size in sorted(timed):
            offset = self.offsets.get(ident, 0)
            if size < offset:
                offset = 0
            with open(path, "rb") as f:
                f.seek(offset)
                data = f.read()
            end = data.rfind(b"\n")
            if end >= 0:
                self.set.feed(path, dedupe, offset, data[:end].count(b"\n") + 1)
                offset += end + 1
            offsets[ident] = offset
        self.offsets = offsets


# ----------------------------------------------------------------------------- Factory Droid (METRICS 4.16)
DROID_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,127}")
DROID_CUSTOM = re.compile(r"custom:", re.IGNORECASE | re.ASCII)
DROID_SIDECAR_MAX = 1 << 20
DROID_WEIGHT_MAX = 8_388_608
DROID_CONTEXT_BASE = 65_536
DROID_FIELDS = ("inputTokens", "cacheCreationTokens", "cacheReadTokens", "outputTokens", "thinkingTokens")
DROID_INPUT_FIELDS = ("inputTokens", "cacheCreationTokens", "cacheReadTokens")


def droid_root(home=None, setting=None):
    """The sessions root (METRICS 4.16): ~/.factory/sessions, or the user setting (a .factory folder or the root)."""
    if setting:
        nested = os.path.join(setting, "sessions")
        return nested if os.path.isdir(nested) and not os.path.islink(nested) else setting
    return os.path.join(home or os.path.expanduser("~"), ".factory", "sessions")


def droid_sidecar_path(path):
    return path[:-len(".jsonl")] + ".settings.json"


def droid_mtime(path):
    """A session's time for the startup order: the newer of its transcript and its sidecar (METRICS 4.16)."""
    side = droid_sidecar_path(path)
    times = [os.path.getmtime(path)]
    if os.path.isfile(side) and not os.path.islink(side):
        times.append(os.path.getmtime(side))
    return max(times)


def discover_droid(root, limit=20_000):
    """`*.jsonl` directly in the root or in a folder directly in it; no symlinks, nothing starting with `.`."""
    found = []
    if not os.path.isdir(root):
        return found

    def sessions_in(folder):
        for name in sorted(os.listdir(folder)):
            path = os.path.join(folder, name)
            if name.endswith(".jsonl") and not name.startswith(".") and os.path.isfile(path) \
                    and not os.path.islink(path):
                yield path

    found += list(sessions_in(root))
    for name in sorted(os.listdir(root)):
        folder = os.path.join(root, name)
        if not name.startswith(".") and os.path.isdir(folder) and not os.path.islink(folder):
            found += list(sessions_in(folder))
    found.sort(key=droid_mtime, reverse=True)
    return found[:limit]


def droid_id(value):
    return value if isinstance(value, str) and DROID_ID.fullmatch(value) else None


def droid_read_sidecar(path):
    """(readable, counters, session model) of a sidecar; counters is None without a `tokenUsage` object."""
    try:
        st = os.lstat(path)
        if not stat.S_ISREG(st.st_mode) or st.st_size > DROID_SIDECAR_MAX:
            return False, None, None
        with open(path, "rb") as f:
            data = f.read(DROID_SIDECAR_MAX + 1)
        if len(data) > DROID_SIDECAR_MAX:
            return False, None, None
        text = data.decode("utf-8")
        obj = loads(text[1:] if text.startswith("﻿") else text)
    except (OSError, ValueError, RecursionError):
        return False, None, None
    if not isinstance(obj, dict):
        return False, None, None
    router = obj.get("effectiveFactoryRouterModel")
    routed = router.get("modelId") if isinstance(router, dict) else None
    chosen = obj.get("model")
    if isinstance(routed, str) and routed.strip():
        model = routed.strip()
    elif isinstance(chosen, str) and chosen.strip() and chosen.strip().lower() != "auto":
        model = chosen.strip()
    else:
        model = None
    usage = obj.get("tokenUsage")
    counters = {f: num(usage.get(f)) for f in DROID_FIELDS} if isinstance(usage, dict) else None
    return True, counters, model


def droid_signature(path):
    try:
        st = os.lstat(path)
    except OSError:
        return None
    return st.st_size, st.st_mtime_ns, st.st_dev, st.st_ino


def droid_parent_counters(state):
    """The parent's counters for the own counters (METRICS 4.16), or None while they are unknown: a parent sidecar that
    exists but is unreadable, unless an earlier attempt found it unreadable with the same signature (then 0)."""
    zero = dict.fromkeys(DROID_FIELDS, 0)
    if not state.droid_parent:
        return zero
    path = os.path.join(os.path.dirname(state.path), state.droid_parent + ".settings.json")
    signature = droid_signature(path)
    if signature is None:
        state.droid_parent_bad = None
        return zero
    readable, counters, _ = droid_read_sidecar(path)
    if readable:
        state.droid_parent_bad = None
        return counters or zero
    if signature == state.droid_parent_bad:
        return zero
    state.droid_parent_bad = signature
    return None


def droid_own(state, counters):
    """Own counters: a session with a `parent` starts from that session's counters (METRICS 4.16); None if unknown."""
    parent = droid_parent_counters(state)
    if parent is None:
        return None
    own = {f: max(0, counters[f] - parent[f]) for f in DROID_FIELDS}
    own["thinkingTokens"] = min(own["thinkingTokens"], own["outputTokens"])
    return own


def droid_line(state, obj, dedupe, offset, length):
    kind = obj.get("type")
    if kind == "session_start":
        if not state.header_seen:
            state.header_seen = True
            state.cwd = obj.get("cwd") if isinstance(obj.get("cwd"), str) else None
            state.project = basename(state.cwd)
            state.droid_parent = droid_id(obj.get("parent"))
            state.droid_calling = droid_id(obj.get("callingSessionId"))
            state.is_subagent = state.droid_calling is not None
        return
    if kind == "agent_turn_outcome":
        state.phase, state.in_flight, state.pending = "idle", None, None
        return
    ts = parse_ts(obj.get("timestamp"))
    if ts is None:
        return
    state.last_line = ts if state.last_line is None else max(state.last_line, ts)
    msg = obj.get("message") if isinstance(obj.get("message"), dict) else None
    if kind == "message" and msg is not None and msg.get("role") == "user":
        if msg.get("visibility") != "user_only":
            state.pending, state.phase, state.in_flight = ts, "generating", ts
    elif kind == "message" and msg is not None and msg.get("role") == "assistant":
        droid_call(state, obj, msg, ts, dedupe, offset, length)
    elif kind == "compaction_state":
        state.droid_compaction = offset
        if state.phase == "generating":
            state.pending, state.in_flight = ts, ts


def droid_call(state, obj, msg, ts, dedupe, offset, length):
    mid = obj.get("id")
    if not isinstance(mid, str) or not mid:
        return
    key = "droid:" + mid
    if key in state.droid_seen or dedupe.owner(key) not in (None, state.path):
        return
    dedupe.claim(key, state.path)
    state.droid_seen.add(key)
    model_id = msg.get("modelId").strip() if isinstance(msg.get("modelId"), str) and msg["modelId"].strip() else None
    effort = msg.get("reasoningEffort")
    state.facts = [f for f in state.facts if f["kind"] != "effort"]
    if isinstance(effort, str):
        state.facts = modes.put(state.facts, modes.normalize("effort", effort, None, "recorded", "request"))
    start = state.droid_compaction if state.droid_compaction is not None else state.droid_read_from
    state.droid_open.append(dict(key=key, start=state.pending, end=ts, model_id=model_id, facts=list(state.facts),
                                 w_in=min(DROID_CONTEXT_BASE + offset - start, DROID_WEIGHT_MAX),
                                 w_out=min(length, DROID_WEIGHT_MAX)))
    state.droid_call_model = model_id
    state.model = model_id or state.droid_model
    content = msg.get("content") if isinstance(msg.get("content"), list) else []
    tools = any(isinstance(b, dict) and b.get("type") == "tool_use" for b in content)
    state.pending, state.phase, state.in_flight = None, ("tools" if tools else "idle"), None


def droid_shares(total, weights):
    """Largest-remainder rounding in exact integers, ties to the earlier call (METRICS 4.16 Allocation)."""
    whole = sum(weights)
    shares = [total * w // whole for w in weights]
    left = total - sum(shares)
    for i in sorted(range(len(weights)), key=lambda i: (-(total * weights[i] % whole), i))[:left]:
        shares[i] += 1
    return shares


def droid_tokens(share):
    return dict(input=cap(share["inputTokens"] + share["cacheCreationTokens"] + share["cacheReadTokens"]),
                cachedInput=share["cacheReadTokens"], cacheWriteInput=share["cacheCreationTokens"],
                output=share["outputTokens"], reasoning=share["thinkingTokens"])


def droid_emit(state, key, start, end, share, model_id, facts):
    model = model_id or state.droid_model
    provider = "custom" if model and DROID_CUSTOM.match(model) else None
    emit(state, key, start, end, droid_tokens(share), model, provider=provider)
    state.response_facts[key] = facts


def droid_allocate(state):
    """Step 3 of a read cycle: the growth waiting in P goes to the open calls."""
    if not state.droid_open or not any(state.droid_growth.values()):
        return
    calls, shares = state.droid_open, [{} for _ in state.droid_open]
    for f in DROID_FIELDS:
        weights = [c["w_in"] if f in DROID_INPUT_FIELDS else c["w_out"] for c in calls]
        for share, value in zip(shares, droid_shares(state.droid_growth[f], weights)):
            share[f] = value
    for call, share in zip(calls, shares):
        droid_emit(state, call["key"], call["start"], call["end"], share, call["model_id"], call["facts"])
    state.droid_open = []
    state.droid_growth = dict.fromkeys(DROID_FIELDS, 0)


def droid_sidecar(state):
    """Step 2 of a read cycle. While `droid_baseline` is set (a transcript that started at its end) the first read
    whose own counters are known only sets H. An unreadable sidecar, or unknown parent counters, changes nothing and
    is tried again at the next read cycle (`droid_unknown` marks the latter for the history analysis)."""
    path = droid_sidecar_path(state.path)
    signature = droid_signature(path)
    state.droid_unknown = False
    if signature is None:
        state.droid_baseline = False
        return
    if signature == state.droid_signature:
        return
    readable, counters, model = droid_read_sidecar(path)
    if not readable:
        state.droid_signature = None
        return
    own = droid_own(state, counters) if counters is not None else None
    if counters is not None and own is None:
        state.droid_signature, state.droid_unknown = None, True
        return
    state.droid_signature = signature
    state.droid_model = model
    state.model = state.droid_call_model or state.droid_model
    baseline, state.droid_baseline = state.droid_baseline, False
    if own is None:
        return
    for f in DROID_FIELDS:
        if not baseline:
            state.droid_growth[f] += max(0, own[f] - state.droid_mark[f])
        state.droid_mark[f] = max(state.droid_mark[f], own[f])


def droid_lines(path, start_offset=0, max_lines=None):
    """(offset, raw line without `\n`) of every complete line from `start_offset`, and the offset after them."""
    with open(path, "rb") as f:
        f.seek(start_offset)
        data = f.read()
    pos = 0
    if start_offset > 0:
        with open(path, "rb") as f:
            f.seek(start_offset - 1)
            after_newline = f.read(1) == b"\n"
        if not after_newline:
            nl = data.find(b"\n")
            pos = nl + 1 if nl >= 0 else len(data)
    out, count = [], 0
    while True:
        nl = data.find(b"\n", pos)
        if nl < 0:
            break
        count += 1
        if max_lines is not None and count > max_lines:
            break
        out.append((start_offset + pos, data[pos:nl]))
        pos = nl + 1
    return out, start_offset + pos


def droid_feed(state, dedupe, start_offset=0, max_lines=None):
    """Step 1 of a read cycle: the transcript's new lines; returns the offset after the last complete line."""
    lines, end = droid_lines(state.path, start_offset, max_lines)
    for offset, raw in lines:
        if len(raw) > MAX_LINE or not raw.strip():
            continue
        try:
            obj = loads(raw)
        except (ValueError, UnicodeDecodeError, RecursionError):
            state.malformed += 1
            continue
        if not isinstance(obj, dict):
            state.malformed += 1
            continue
        droid_line(state, obj, dedupe, offset, len(raw))
        if state.tasks is not None:
            state.tasks.feed(state, obj, dedupe, raw)
    return end


def droid_read(state, dedupe, start_offset=0, max_lines=None):
    """A session read from `start_offset` as one read cycle (METRICS 4.16): the history rule from offset 0."""
    state.droid_read_from = start_offset
    end = droid_feed(state, dedupe, start_offset, max_lines)
    if max_lines is None:
        droid_sidecar(state)
        droid_allocate(state)
    return end


def droid_finish(state):
    """History only: own counters left without a call become one response of the session (METRICS 4.16)."""
    if not any(state.droid_growth.values()) or state.last_line is None:
        return
    sid = os.path.basename(state.path)[:-len(".jsonl")]
    droid_emit(state, "droid:%s:session" % sid, None, state.last_line, state.droid_growth, None, [])
    state.droid_growth = dict.fromkeys(DROID_FIELDS, 0)


class DroidTail:
    """Live reading of one session (METRICS 4.16 Read cycle): appended lines, then the sidecar, then allocation."""

    def __init__(self, path, dedupe, at_end=False):
        self.path, self.dedupe = path, dedupe
        self.state = FileState(path, "droid")
        self.offset = os.path.getsize(path) if at_end else 0
        self.identity = None
        self.state.droid_read_from = self.offset
        if at_end:
            self.identity = droid_signature(path)[2:]
            self.state.droid_baseline = True
            droid_sidecar(self.state)

    def read(self):
        st = os.stat(self.path)
        identity = (st.st_dev, st.st_ino)
        if self.identity is not None and (identity != self.identity or st.st_size < self.offset):
            self.state = FileState(self.path, "droid")
            self.offset = 0
        self.identity = identity
        self.offset = droid_feed(self.state, self.dedupe, self.offset)
        droid_sidecar(self.state)
        droid_allocate(self.state)


# ----------------------------------------------------------------------------- WorkBuddy (METRICS 4.9)

WB_FOLDERS = OrderedDict([("workbuddy", ".workbuddy"), ("workbuddyAi", ".workbuddy-ai")])
WB_USER_PREFIXES = ("custom:", "custom-local:")


def wb_ts(value):
    if finite(value):
        return trunc64(value) if value > 0 else None
    return parse_ts(value)


def wb_first(*values):
    """The first value that is a finite number > 0, truncated (0.5 gives 0 and ends the search); 0 when none. Like the
    CLI's firstPositiveTokenCount."""
    return next((num(v) for v in values if finite(v) and v > 0), 0)


def wb_sum(details, field):
    """Σ field over agents-core's per-request detail entries (a list of objects; one object is accepted too)."""
    entries = details if isinstance(details, list) else [details]
    return cap(sum(num(e.get(field)) for e in entries if isinstance(e, dict)))


def wb_tokens(pd):
    """Normalized usage of one model call (METRICS 4.9 Tokens): agents-core's usage, completed from the raw chunk."""
    usage = pd.get("usage") if isinstance(pd.get("usage"), dict) else {}
    raw = pd.get("rawUsage") if isinstance(pd.get("rawUsage"), dict) else {}
    prompt = raw.get("prompt_tokens_details") if isinstance(raw.get("prompt_tokens_details"), dict) else {}
    completion = raw.get("completion_tokens_details") if isinstance(raw.get("completion_tokens_details"), dict) else {}
    cached = max(wb_sum(usage.get("inputTokensDetails"), "cached_tokens"),
                 wb_first(raw.get("cache_read_input_tokens"), prompt.get("cached_tokens"),
                          raw.get("prompt_cache_hit_tokens")))
    write = wb_first(raw.get("cache_creation_input_tokens"), raw.get("prompt_cache_write_tokens"))
    output = max(num(usage.get("outputTokens")), num(raw.get("completion_tokens")))
    reasoning = max(wb_sum(usage.get("outputTokensDetails"), "reasoning_tokens"),
                    num(completion.get("reasoning_tokens")))
    inp = max(num(usage.get("inputTokens")), num(raw.get("prompt_tokens")), cap(cached + write))
    return dict(input=inp, cachedInput=cached, cacheWriteInput=write, output=output, reasoning=reasoning)


def wb_model(pd):
    """(model, provider): the chunk's model, else the requested id; a user-provider prefix is removed (4.9)."""
    model = next((v.strip() for v in (pd.get("model"), pd.get("requestModelId")) if isinstance(v, str) and v.strip()),
                 None)
    if model and model.lower().startswith(WB_USER_PREFIXES):
        return model.split(":", 1)[1].strip() or None, "custom"
    return model, None


def wb_idle(state):
    state.phase, state.pending, state.in_flight = "idle", None, None
    state.open_calls.clear()


def wb_response(state, key, pd, ts, dedupe):
    """Creates or merges the response of one model call from a line that carries usage (4.9 step 3)."""
    if not isinstance(pd.get("usage"), dict) and not isinstance(pd.get("rawUsage"), dict):
        return
    tokens = wb_tokens(pd)
    if tokens["input"] == 0 and tokens["output"] == 0:
        return
    model, provider = wb_model(pd)
    resp = state.responses.get(key)
    if resp is None:
        dedupe.claim(key, state.path)
        emit(state, key, state.pending, ts, tokens, model, provider=provider)
        state.pending = None
        resp = state.responses[key]
    else:
        resp["endMs"] = max(resp["endMs"], ts)
        for f in TOKEN_FIELDS:
            resp[f] = max(resp[f], tokens.get(f, 0))
        resp["model"] = model or resp["model"]
        resp["provider"] = provider or resp["provider"]
    state.model = resp["model"] or state.model


def workbuddy_line(state, obj, dedupe):
    """One line of a WorkBuddy session file (METRICS 4.9)."""
    ts = wb_ts(obj.get("timestamp"))
    if ts is None:
        return
    state.last_line = ts if state.last_line is None else max(state.last_line, ts)
    if isinstance(obj.get("cwd"), str) and obj["cwd"].strip():
        state.cwd = obj["cwd"]
        state.project = basename(obj["cwd"]) or state.project
    kind, role = obj.get("type"), obj.get("role")
    pd = obj.get("providerData") if isinstance(obj.get("providerData"), dict) else {}
    skip = pd.get("skipRun") is True
    if kind == "message" and role == "user":
        if not skip:
            state.open_calls.clear()
            state.phase, state.pending, state.in_flight = "generating", ts, ts
        return
    call_id = obj.get("callId") if isinstance(obj.get("callId"), str) else None
    if kind == "function_call_result":
        state.open_calls.pop(call_id, None)
        if skip:
            if not state.open_calls:
                wb_idle(state)
        else:
            state.pending = ts
            if not state.open_calls:
                state.phase, state.in_flight = "generating", ts
        return
    assistant = kind == "message" and role == "assistant"
    if not (assistant or kind in ("function_call", "reasoning", "model-usage")):
        return
    if assistant and skip:
        if obj.get("status") == "incomplete" or isinstance(pd.get("error"), dict):
            wb_idle(state)
        return
    if pd.get("isPartialAborted") is True:
        return
    mid = pd.get("messageId")
    key = mid if isinstance(mid, str) and mid else (
        "workbuddy:" + obj["id"] if isinstance(obj.get("id"), str) and obj["id"] else None)
    if key is None or dedupe.owner(key) not in (None, state.path):
        return
    wb_response(state, key, pd, ts, dedupe)
    if kind == "model-usage":
        wb_idle(state)
        return
    for call_id in [c for c, k in state.open_calls.items() if k != key]:
        del state.open_calls[call_id]
    if kind == "function_call":
        if call_id is not None:
            state.open_calls[call_id] = key
        state.phase, state.in_flight = "tools", None
    elif assistant and not state.open_calls:
        if pd.get("agent") == "compact":
            state.phase, state.pending, state.in_flight = "generating", ts, ts
        else:
            state.phase, state.in_flight = "idle", None


def workbuddy_roots(home=None, settings=None):
    """(source, projects folder) of both editions (METRICS 2, 4.9); a setting names the data root or `projects`."""
    home = home or os.path.expanduser("~")
    settings = settings or {}
    roots = []
    for source, folder in WB_FOLDERS.items():
        chosen = (settings.get(source) or "").rstrip("/\\")
        path = chosen or os.path.join(home, folder)
        roots.append((source, path if os.path.basename(path) == "projects" else os.path.join(path, "projects")))
    return roots


def discover_workbuddy(projects, limit=20_000):
    """Main sessions `<slug>/<name>.jsonl` and sub-agents `<slug>/<session>/subagents/<name>.jsonl` (4.9)."""
    found = []
    for slug in sorted(_subdirs(projects)):
        if slug == "cloud" or slug.startswith("."):
            continue
        base = os.path.join(projects, slug)
        candidates = [os.path.join(base, n) for n in os.listdir(base)]
        for session in _subdirs(base):
            agents = os.path.join(base, session, "subagents")
            if not session.startswith(".") and os.path.isdir(agents) and not os.path.islink(agents):
                candidates += [os.path.join(agents, n) for n in os.listdir(agents)]
        for path in candidates:
            name = os.path.basename(path)
            if name.endswith(".jsonl") and not name.startswith(".") and os.path.isfile(path) \
                    and not os.path.islink(path):
                found.append(path)
    found.sort(key=lambda p: os.path.getmtime(p), reverse=True)
    return found[:limit]


# ----------------------------------------------------------------------------- Antigravity (METRICS 4.8)

def pb_varint(data, pos):
    result = 0
    for i in range(10):
        if pos >= len(data):
            raise ValueError("truncated varint")
        byte = data[pos]
        pos += 1
        result |= (byte & 0x7F) << (7 * i)
        if not byte & 0x80:
            return result & 0xFFFF_FFFF_FFFF_FFFF, pos
    raise ValueError("varint longer than 10 bytes")


def pb_fields(data):
    """Top-level fields of a protobuf message as (number, wire, value); raises ValueError when malformed."""
    out, pos = [], 0
    while pos < len(data):
        key, pos = pb_varint(data, pos)
        number, wire = key >> 3, key & 7
        if number == 0:
            raise ValueError("field 0")
        if wire == 0:
            value, pos = pb_varint(data, pos)
        elif wire in (1, 5):
            size = 8 if wire == 1 else 4
            if pos + size > len(data):
                raise ValueError("truncated fixed field")
            value, pos = int.from_bytes(data[pos:pos + size], "little"), pos + size
        elif wire == 2:
            size, pos = pb_varint(data, pos)
            if size > len(data) - pos:
                raise ValueError("truncated length-delimited field")
            value, pos = bytes(data[pos:pos + size]), pos + size
        else:
            raise ValueError("wire type %d" % wire)
        out.append((number, wire, value))
    return out


def pb_last(fields, number, wire):
    values = [v for n, w, v in fields if n == number and w == wire]
    return values[-1] if values else None


def pb_first(fields, number, wire):
    values = [v for n, w, v in fields if n == number and w == wire]
    return values[0] if values else None


def pb_int(fields, number):
    value = pb_last(fields, number, 0)
    return 0 if value is None else value


def pb_signed(fields, number):
    value = pb_int(fields, number)
    return value - (1 << 64) if value >= 1 << 63 else value


def pb_str(fields, number, first=False):
    value = (pb_first if first else pb_last)(fields, number, 2)
    return value.decode("utf-8", "replace") if value else ""


def pb_msg(fields, number, first=False):
    value = (pb_first if first else pb_last)(fields, number, 2)
    return None if value is None else pb_fields(value)


def pb_timestamp(fields, number):
    sub = pb_msg(fields, number)
    if sub is None:
        return None
    seconds, nanos = pb_signed(sub, 1), pb_signed(sub, 2)
    if seconds <= 0:
        return None
    return seconds * 1000 + (nanos // 1_000_000 if 0 <= nanos <= 999_999_999 else 0)


def pb_repeated_uint(fields, number):
    values = []
    for n, w, v in fields:
        if n != number:
            continue
        if w == 0:
            values.append(v)
        elif w == 2:
            pos = 0
            while pos < len(v):
                item, pos = pb_varint(v, pos)
                values.append(item)
    return values


def uri_basename(value):
    """Basename of a path or URI; a URI loses its scheme and authority and is percent-decoded (METRICS 4.8)."""
    if not value:
        return None
    m = re.match(r"^[A-Za-z][A-Za-z0-9+.-]+:", value)
    if m:
        rest = value[m.end():]
        if rest.startswith("//"):
            slash = rest.find("/", 2)
            rest = rest[slash:] if slash >= 0 else ""
        value = unquote(rest, encoding="utf-8", errors="replace")
    return basename(value)


def sqlite_readonly(db_path):
    """Opens a database read-only without ever creating a file (METRICS 4.5, 4.8)."""
    with open(db_path, "rb") as f:
        header = f.read(20)
    closed_wal = len(header) == 20 and header[18] == 2 and header[19] == 2 and not os.path.exists(db_path + "-wal")
    try:
        if closed_wal:
            raise sqlite3.OperationalError("closed WAL database")
        db = sqlite3.connect("file:%s?mode=ro" % db_path, uri=True, timeout=2)
        db.execute("select 1 from sqlite_master limit 1")
    except sqlite3.OperationalError:
        if os.path.exists(db_path + "-wal"):
            raise
        db = sqlite3.connect("file:%s?immutable=1" % db_path, uri=True, timeout=2)
    return db


def ag_rows(db, sql):
    try:
        return db.execute(sql).fetchall()
    except sqlite3.DatabaseError:
        return []


def ag_decode(state, blob, decoder):
    if blob is None:
        return None
    try:
        return decoder(bytes(blob))
    except ValueError:
        state.malformed += 1
        return None


def ag_trajectory(data):
    fields = pb_fields(data)
    workspace = pb_msg(fields, 1, first=True)
    uris = [v.decode("utf-8", "replace") for n, w, v in fields if n == 7 and w == 2]
    project = uri_basename(uris[0]) if uris else None
    if project is None and workspace is not None:
        project = uri_basename(pb_str(workspace, 1))
    parent = pb_str(fields, 5)
    return dict(created=pb_timestamp(fields, 2), parent=parent or None, project=project,
                subagent=bool(parent) or pb_signed(fields, 17) > 0 or pb_last(fields, 8, 2) is not None)


def ag_generation(data):
    fields = pb_fields(data)
    chat = pb_msg(fields, 1) or []
    return dict(steps=set(pb_repeated_uint(fields, 2)), model=pb_str(chat, 19) or pb_str(chat, 21) or None)


def ag_executor(data):
    fields = pb_fields(data)
    planner = pb_msg(pb_msg(fields, 10) or [], 1) or []
    return dict(reason=pb_signed(fields, 1), last=pb_signed(fields, 3), model=pb_str(planner, 28) or None)


AG_TIMES = (("created", 1), ("viewable", 6), ("finished", 7), ("completed", 8), ("chunk", 22), ("started", 32))


def ag_step(data):
    fields = pb_fields(data)
    step = {name: pb_timestamp(fields, number) for name, number in AG_TIMES}
    use = pb_msg(fields, 9)
    if use is not None:
        tokens = {name: pb_int(use, number) for name, number in (
            ("input", 2), ("output", 3), ("cache", 5), ("thinking", 9), ("response", 10))}
        if any(v > AG_TOKEN_MAX for v in tokens.values()):
            raise ValueError("implausible token count")
        tokens["key"] = pb_str(use, 11) or pb_str(use, 7) or None
    step["usage"] = None if use is None else tokens
    return step


def antigravity_state(db_path, dedupe):
    """Parses one Antigravity conversation database completely; None when its format is unsupported."""
    db = sqlite_readonly(db_path)
    try:
        columns = {r[1] for r in ag_rows(db, "PRAGMA table_info(steps)")}
        if not {"idx", "step_type", "status", "metadata"} <= columns:
            return None
        st = FileState(db_path, "antigravity")
        conversation = os.path.basename(db_path)[:-3]
        traj = None
        for (blob,) in ag_rows(db, "SELECT CASE WHEN length(data) <= %d THEN data END FROM trajectory_metadata_blob "
                                   "ORDER BY rowid LIMIT 1" % AG_BLOB_MAX):
            traj = ag_decode(st, blob, ag_trajectory)
        created = traj["created"] if traj else None
        if traj:
            st.project, st.is_subagent, st.parent_session = traj["project"], traj["subagent"], traj["parent"]
        st.file_created = created
        gens = [g for _, blob in ag_last_per_idx(ag_rows(
                    db, "SELECT idx, CASE WHEN typeof(size) = 'integer' AND size <= %d AND length(data) <= %d THEN data "
                        "END FROM gen_metadata WHERE idx > -1 ORDER BY idx, rowid" % (AG_BLOB_MAX, AG_BLOB_MAX)))
                for g in [ag_decode(st, blob, ag_generation)] if g]
        execs = [dict(e, idx=idx) for idx, blob in ag_last_per_idx(ag_rows(
                    db, "SELECT idx, CASE WHEN length(data) <= %d THEN data END FROM executor_metadata WHERE idx > -1 "
                        "ORDER BY idx, rowid" % AG_BLOB_MAX))
                 for e in [ag_decode(st, blob, ag_executor)] if e]

        def model_for(idx):
            generation = next((g for g in gens if idx in g["steps"]), None)
            if generation and generation["model"]:
                return generation["model"]
            covering = sorted((e for e in execs if e["last"] >= idx), key=lambda e: (e["last"], e["idx"]))
            return covering[0]["model"] if covering else None

        times = [created] if created is not None else []
        latest = None
        models = {}
        for idx, step_type, status, blob in ag_last_per_idx(ag_rows(
                db, "SELECT idx, step_type, status, CASE WHEN length(metadata) <= %d THEN metadata END FROM steps "
                    "WHERE idx > -1 ORDER BY idx, rowid" % AG_BLOB_MAX)):
            idx, step_type, status = idx or 0, step_type or 0, status or 0
            step = ag_decode(st, blob, ag_step)
            if step is None:
                continue
            if created is not None and step["created"] is not None and step["created"] < created - 2000:
                continue
            row_times = [t for name, t in step.items() if name != "usage" and t is not None]
            times += row_times
            latest = dict(step, idx=idx, type=step_type, status=status)
            use = step["usage"]
            if step_type != 15 or status in AG_OPEN or not use:
                continue
            if not any(use[f] > 0 for f in ("input", "output", "cache", "thinking", "response")):
                continue
            key = use["key"] or "antigravity:%s:%d" % (conversation, idx)
            if dedupe.owner(key) not in (None, st.path):
                continue
            end = step["finished"] or step["chunk"] or step["completed"]
            start = step["created"]
            if end is None:
                if not row_times:
                    continue
                end, start = max(row_times), None
            if status in AG_FAILED:
                start = None
            dedupe.claim(key, st.path)
            models[key] = model_for(idx)
            output = use["output"] or use["thinking"] + use["response"]
            emit(st, key, start, end, dict(input=use["input"] + use["cache"], cachedInput=use["cache"],
                                           cacheWriteInput=0, output=output, reasoning=use["thinking"]), models[key])
            if models[key]:
                st.model = models[key]
        st.last_line = max(times) if times else None
        st.phase, st.in_flight = ag_phase(latest, execs)
        return st
    finally:
        db.close()


def ag_last_per_idx(rows):
    """Rows with the same idx are processed in rowid order, the later one replacing the earlier (METRICS 4.8)."""
    last = OrderedDict()
    for row in rows:
        last[row[0]] = row
    return list(last.values())


def ag_phase(latest, execs):
    if latest is None or any(e["reason"] != 0 and e["last"] >= latest["idx"] for e in execs):
        return "idle", None
    kind, status = latest["type"], latest["status"]
    if kind == 15:
        return ("generating", latest["created"]) if status in AG_OPEN else ("idle", None)
    if kind == 14:
        return ("idle", None) if status in AG_FAILED else ("generating", latest["created"])
    if status in AG_OPEN:
        return "tools", None
    if status == 3:
        return "generating", latest["completed"] if latest["completed"] is not None else latest["created"]
    return "idle", None


def antigravity_stores(home=None, setting=None, gemini_dir=None):
    """The stores to read (METRICS 2, 4.8); a `-backup` store is never read."""
    if setting:
        candidates = [setting]
    else:
        base = gemini_dir or os.path.join(home or os.path.expanduser("~"), ".gemini")
        candidates = [os.path.join(base, n) for n in ("antigravity-cli", "antigravity", "antigravity-ide")]
    stores = []
    for c in candidates:
        c = c.rstrip("/\\")
        store = os.path.dirname(c) if os.path.basename(c) == "conversations" else c
        if not os.path.basename(store).endswith("-backup"):
            stores.append(c)
    return stores


def discover_antigravity(store, limit=20_000):
    folder = store if os.path.basename(store.rstrip("/\\")) == "conversations" else os.path.join(store, "conversations")
    try:
        names = sorted(os.listdir(folder))
    except OSError:
        return []
    found = [os.path.join(folder, n) for n in names if AG_NAME.match(n)
             and os.path.isfile(os.path.join(folder, n)) and not os.path.islink(os.path.join(folder, n))]
    found.sort(key=ag_mtime, reverse=True)
    return found[:limit]


def ag_mtime(path):
    """A database's time for startup order: the newer of the .db and its -wal (METRICS 5.4)."""
    wal = path + "-wal"
    return max(os.path.getmtime(path), os.path.getmtime(wal) if os.path.exists(wal) else 0)


def antigravity_parents(states):
    by_id = {}
    for s in states:
        by_id.setdefault(os.path.basename(s.path)[:-3].lower(), []).append(s.path)
    parents = {}
    for s in states:
        candidates = by_id.get((s.parent_session or "").lower(), []) if s.parent_session else []
        candidates = sorted(candidates, key=lambda p: (os.path.dirname(p) != os.path.dirname(s.path), p))
        parent = candidates[0] if candidates else None
        parents[s.path] = parent if parent != s.path else None
    for path in list(parents):
        seen, cur = set(), path
        while cur is not None and cur not in seen:
            seen.add(cur)
            cur = parents.get(cur)
        if cur is not None:
            parents[path] = None
    return parents


# ----------------------------------------------------------------------------- engine

def interval(resp):
    start, end = resp.get("startMs"), resp["endMs"]
    if start is None or end < start or end - start > 15 * 60_000:
        return None
    return (min(start, end - 500), end)


def overlap(a0, a1, b0, b1):
    return max(0, min(a1, b1) - max(a0, b0))


def speed_of(responses):
    out = tot = dur = 0.0
    for r in responses:
        iv = interval(r)
        out += r["output"]
        tot += r["output"] + r["input"]
        dur += (iv[1] - iv[0]) / 1000
    return (out / dur, tot / dur) if dur > 0 else None


def recent(valid, now, predicate, limit, horizon):
    picked = recent_set(valid, now, predicate, limit, horizon)
    return speed_of(picked) if picked else None


def recent_set(valid, now, predicate, limit, horizon):
    picked = [r for r in valid if predicate(r) and r["endMs"] >= now - horizon]
    picked.sort(key=lambda r: r["endMs"], reverse=True)
    return picked[:limit]


def is_final(resp):
    return resp.get("final", True)


def estimated_speed(valid, now, agent):
    return (recent(valid, now, lambda r: r["agentId"] == agent["id"], 3, 600_000)
            or recent(valid, now, lambda r: r.get("model") == agent.get("model") and agent.get("model"), 10, 1_800_000)
            or recent(valid, now, lambda r: r["source"] == agent["source"], 10, 1_800_000))


def live_rate(responses, agents, now):
    w0 = now - WINDOW_MS
    valid = [r for r in responses if interval(r) is not None and is_final(r)]
    unmeasured = [r for r in responses if interval(r) is not None and not is_final(r)]
    by_id = {a["id"]: a for a in agents}
    measured = [0.0, 0.0]
    for r in valid:
        s, e = interval(r)
        frac = overlap(w0, now, s, e) / (e - s)
        measured[0] += r["output"] * frac
        measured[1] += (r["output"] + r["input"]) * frac
    estimated = [0.0, 0.0]
    for r in unmeasured:
        agent = by_id.get(r["agentId"], {"id": r["agentId"], "model": r.get("model"), "source": r["source"]})
        sp = estimated_speed(valid, now, agent)
        if sp is None:
            continue
        s, e = interval(r)
        secs = overlap(w0, now, s, e) / 1000
        estimated[0] += sp[0] * secs
        estimated[1] += sp[1] * secs
    for a in agents:
        if a["phase"] != "generating" or a.get("inFlightStartMs") is None or a.get("lastLineMs") is None:
            continue
        if now - a["lastLineMs"] > 300_000:
            continue
        sp = estimated_speed(valid, now, a)
        if sp is None:
            continue
        own_ends = [r["endMs"] for r in responses if r["agentId"] == a["id"]]
        start = max([a["inFlightStartMs"]] + own_ends)
        secs = overlap(w0, now, start, now) / 1000
        estimated[0] += sp[0] * secs
        estimated[1] += sp[1] * secs
    out = (measured[0] + estimated[0]) / (WINDOW_MS / 1000)
    tot = (measured[1] + estimated[1]) / (WINDOW_MS / 1000)
    denom = measured[0] + estimated[0]
    return out, tot, (estimated[0] / denom if denom > 0 else 0.0)


def reference_set(valid, now, agent):
    """The measured responses behind estimatedSpeed (6.2): the first non-empty level of the fallback chain."""
    return (recent_set(valid, now, lambda r: r["agentId"] == agent["id"], 3, 600_000)
            or recent_set(valid, now, lambda r: r.get("model") == agent.get("model") and agent.get("model"), 10,
                          1_800_000)
            or recent_set(valid, now, lambda r: r["source"] == agent["source"], 10, 1_800_000))


def cost_rate(refs):
    """micro-USD per second of the reference responses whose cost is known (9.5), or None."""
    known = [r for r in refs if r.get("costMicroUsd") is not None]
    secs = sum((interval(r)[1] - interval(r)[0]) / 1000 for r in known)
    return sum(r["costMicroUsd"] for r in known) / secs if known and secs > 0 else None


def live_cost(responses, agents, now):
    """(live cost in micro-USD per second, share of live output tokens whose cost is known) - METRICS 9.5."""
    w0 = now - WINDOW_MS
    valid = [r for r in responses if interval(r) is not None and is_final(r)]
    unmeasured = [r for r in responses if interval(r) is not None and not is_final(r)]
    by_id = {a["id"]: a for a in agents}
    cost = known_tokens = all_tokens = 0.0
    for r in valid:
        s, e = interval(r)
        frac = overlap(w0, now, s, e) / (e - s)
        all_tokens += r["output"] * frac
        if r.get("costMicroUsd") is not None:
            cost += r["costMicroUsd"] * frac
            known_tokens += r["output"] * frac
    estimates = []
    for r in unmeasured:
        agent = by_id.get(r["agentId"], {"id": r["agentId"], "model": r.get("model"), "source": r["source"]})
        s, e = interval(r)
        estimates.append((agent, overlap(w0, now, s, e) / 1000))
    for a in agents:
        if a["phase"] != "generating" or a.get("inFlightStartMs") is None or a.get("lastLineMs") is None:
            continue
        if now - a["lastLineMs"] > 300_000:
            continue
        start = max([a["inFlightStartMs"]] + [r["endMs"] for r in responses if r["agentId"] == a["id"]])
        estimates.append((a, overlap(w0, now, start, now) / 1000))
    for agent, secs in estimates:
        refs = reference_set(valid, now, agent)
        if not refs:
            continue
        tokens = speed_of(refs)[0] * secs
        all_tokens += tokens
        rate = cost_rate(refs)
        if rate is not None:
            cost += rate * secs
            known_tokens += tokens
    return cost / (WINDOW_MS / 1000), (known_tokens / all_tokens if all_tokens > 0 else 1.0)


def agent_list(responses, agents, now):
    by_id = {a["id"]: dict(a) for a in agents}
    for a in by_id.values():
        if a["phase"] == "generating" and a.get("lastLineMs") is not None and now - a["lastLineMs"] > 300_000:
            a["phase"] = "idle"
    live = {aid: live_rate([r for r in responses if r["agentId"] == aid], [a], now)[0] for aid, a in by_id.items()}
    chosen = {aid for aid, a in by_id.items()
              if a["phase"] == "generating" or (a.get("lastLineMs") is not None and now - a["lastLineMs"] <= 600_000)}
    for aid in list(chosen):
        cur = by_id[aid].get("parentId")
        while cur in by_id and cur not in chosen:
            chosen.add(cur)
            cur = by_id[cur].get("parentId")
    parent = {aid: (by_id[aid].get("parentId") if by_id[aid].get("parentId") in chosen else None) for aid in chosen}
    children = {}
    for aid in chosen:
        children.setdefault(parent[aid], []).append(aid)

    def members(aid):
        out = [aid]
        for c in children.get(aid, []):
            out += members(c)
        return out

    def last(aid):
        return by_id[aid].get("lastLineMs") or 0

    def sibling_key(aid):
        return (0 if by_id[aid]["phase"] == "generating" else 1, -live[aid], -last(aid), aid)

    def group_key(root):
        m = members(root)
        return (0 if any(by_id[x]["phase"] == "generating" for x in m) else 1, -sum(live[x] for x in m),
                -max(last(x) for x in m), root)

    order = []

    def walk(aid):
        order.append(aid)
        for c in sorted(children.get(aid, []), key=sibling_key):
            walk(c)

    for root in sorted(children.get(None, []), key=group_key):
        walk(root)
    return order, {aid: p for aid, p in parent.items() if p is not None}


def buckets(responses, first, last):
    values = {}
    for r in responses:
        iv = interval(r)
        if iv is None or not is_final(r):
            continue
        s, e = iv
        k = (s // 1000) * 1000
        while k < e:
            if first <= k <= last:
                values[k] = values.get(k, 0.0) + r["output"] * overlap(k, k + 1000, s, e) / (e - s)
            k += 1000
    return values


def today_stats(responses, today_start, now):
    today = [r for r in responses if r["endMs"] >= today_start]
    valid = [r for r in today if interval(r) is not None and is_final(r)]
    sp = speed_of(valid)
    b = buckets(today, today_start, now)
    peak = 0.0
    for k in b:
        peak = max(peak, sum(b.get(k - i * 1000, 0.0) for i in range(3)) / 3)
    return dict(requests=sum(r.get("requestCount", 1) for r in today), output=sum(r["output"] for r in today),
                unfinalized=sum(r.get("requestCount", 1) for r in today if not is_final(r)),
                input=sum(r["input"] for r in today), average=sp[0] if sp else None, peak=peak, buckets=b,
                cost=sum(r["costMicroUsd"] for r in today if r.get("costMicroUsd") is not None),
                unpriced=sum(r.get("requestCount", 1) for r in today if r.get("costMicroUsd") is None))


# ----------------------------------------------------------------------------- task score (METRICS 6.9, 6.11)

TASK_SOURCES = ("codex", "claudeCode", "claudeDesktop", "omp", "pi", "openclaw", "droid")


def task_states(files, dedupe, include=lambda path: True):
    """Parses `files` ((time, path, source, root) in startup order) with the task composer attached to every agent of a
    scored source; returns the parser states of those agents."""
    import task_events
    states = []
    for _, path, source, root in files:
        if not include(path):
            continue
        if source == "openclaw":
            states += [st for st in openclaw_states(path, dedupe, tasks=True) or [] if st.tasks is not None]
        elif source in TASK_SOURCES:
            state = task_events.attach(FileState(path, source, root))
            process_file(state, dedupe)
            states.append(state)
    return states


def task_parents(states):
    """6.7 parent links of the scored agents (OpenClaw windows link inside their database, 4.14)."""
    parents = resolve_parents([s for s in states if s.source != "openclaw"])
    for s in states:
        if s.source == "openclaw":
            parents[s.path] = s.parent_session
    return parents


def task_value_rows(states, now, models_by_source=None, sets=None, label=lambda path: path):
    """The `task` rows (one per agent) and the `taskSets` row of a dump at `now` (6.9 Values, 6.11 Predictions)."""
    import task_events
    rows = task_events.agent_rows(states, task_parents(states), now, models_by_source)
    out = []
    for r in rows:
        a, v, d, p = r["agent"], r["value"], r["task"], r["predictions"]
        row = OrderedDict([("agentId", label(a["id"])), ("source", a["source"]), ("phase", a["phase"]),
                           ("lastLineMs", a["lastLineMs"]),
                           ("parentId", label(a["parentId"]) if a["parentId"] else None),
                           ("taskStartMs", r["taskStartMs"]), ("form", v["form"]), ("score", v["score"]),
                           ("shown", v["shown"]), ("state", v["state"])])
        if d is not None:
            for k in ("dims", "ig", "loop", "osc", "counts", "firstPassMs", "vHeld", "openFailure", "lastPass"):
                row[k] = d[k]
        row["predictions"] = None if p is None else OrderedDict(
            [(k, p[k]) for k in ("success", "stall", "fix", "successShown", "stallShown", "fixShown")])
        row["models"] = r["flags"]
        out.append(row)
    values = task_events.set_values(rows, now, sets)
    sets_row = OrderedDict([("listed", [label(x) for x in values["listed"]]),
                            ("sets", [dict(v, agent=label(v["agent"]) if v["agent"] else None) for v in values["sets"]]),
                            ("groups", OrderedDict((label(root), dict(g, members=[label(m) for m in g["members"]],
                                                                       agent=label(g["agent"]) if g["agent"] else None))
                                                   for root, g in sorted(values["groups"].items(),
                                                                         key=lambda kv: kv[0].encode("utf-16-be"))))])
    return out, sets_row


def task_records_all(states, analysis_ms, tz=None, label=lambda path: path):
    """Every task record of the agents read whole (6.11 Task records), by agent id, then ordinal."""
    import task_events

    def day_of(ms):
        if not has_day(ms):
            return ""
        zone = tz or datetime.now().astimezone().tzinfo
        return datetime.fromtimestamp(ms / 1000, tz=zone).strftime("%Y-%m-%d")
    records = []
    for st in states:
        records += task_events.records_of(st, analysis_ms, day_of)
    records.sort(key=lambda r: (r["agentId"].encode("utf-16-be"), r["ordinal"]))
    for r in records:
        r["agentId"] = label(r["agentId"])
    return records


RECORD_FIELDS = ("agentId", "ordinal", "source", "model", "effort", "startMs", "endMs", "day", "complete", "state",
                 "score", "dims", "counts", "firstPassMs", "success", "stalled", "snapshots", "fixRows")


def task_calibration(records):
    """Per scored source with records: its three models (6.11 Calibration)."""
    import task_score
    out = OrderedDict()
    for source in task_score.SOURCE_ORDER:
        part = [r for r in records if r["source"] == source]
        if part:
            out[source] = task_score.calibrate(part)
    return out


def dump_tasks(files, first=None, last=None, source=None, tz=None):
    """`--dump-tasks`: the task records of every log read whole (6.11), the calibration and the comparison."""
    import task_score
    started = time.time()
    analysis_ms = int(started * 1000)
    states = task_states(files, Deduper(limit=10 ** 9))
    records = task_records_all(states, analysis_ms, tz)
    for r in records:
        print(json.dumps({"record": OrderedDict((k, r[k]) for k in RECORD_FIELDS)}, ensure_ascii=False,
                         separators=(",", ":")))
    for src, models in task_calibration(records).items():
        print(json.dumps({"calibration": {"source": src, "models": models}}, separators=(",", ":")))
    days = sorted(r["day"] for r in records if r["day"])
    first = first or (days[0] if days else "0001-01-01")
    last = last or (days[-1] if days else "9999-12-31")
    print(json.dumps({"comparison": dict(first=first, last=last, source=source,
                                         **task_score.compare(records, first, last, source))},
                     ensure_ascii=False, separators=(",", ":")))
    stitched = sum(st.tasks.stats["stitched"] for st in states)
    print(json.dumps({"summary": {"agents": len(states), "records": len(records),
                                  "complete": sum(1 for r in records if r["complete"]),
                                  "scored": sum(1 for r in records if r["score"] is not None),
                                  "stitched": stitched, "analysisMs": analysis_ms,
                                  "seconds": round(time.time() - started, 1)}}))


TASK_FIXTURE = "task-e2e-expected.json"
TASK_FIXTURE_COMMENT = (
    "End-to-end task score (METRICS 6.9 Composing the stream, Values; 6.11): read the files of `order` (paths relative "
    "to spec/fixtures; omp and Pi files with the roots of `roots`; an OpenClaw database gives one agent per window, "
    "id `<path>#<session_id>`) in that order with one dedupe set (3.4) and the task composer, then: `agents` gives each "
    "agent's composed event stream and the number of sessions stitched back (TASK-PARSERS 4.3); `records` the task "
    "records of every agent read whole at `analysisMs`, days in UTC, by agent id (UTF-16 ordinal), then ordinal; "
    "`calibration` the models of each source from those records; `values` each agent's form, value, state, dimensions, "
    "facts and predictions at `nowMs`, with the phase after the 300-second staleness rule and the coefficients of its "
    "source's calibration; `taskSets` the agents list (6.7) at `nowMs`, the highest-score value of every set in "
    "`sets` (null: all sources) and of every board group; `comparison` the 6.11 comparison over `comparisonRange`. "
    "Generated by spec/tools/reference.py --write-task-fixture after checking the hand expectations of "
    "spec/tools/task_e2e_fixtures.py; ports must match every value (numbers within 1e-9, calibration coefficients "
    "and probabilities within 1e-6).")


def task_fixture_result(root, meta):
    """Everything spec/fixtures/task-e2e-expected.json states, computed from the files of `meta`."""
    import task_score
    files = []
    for item in meta["order"]:
        files.append((0, os.path.join(root, *item["path"].split("/")), item["source"],
                      os.path.join(root, *meta["roots"][item["source"]].split("/")) if item["source"] in meta["roots"]
                      else None))
    states = task_states(files, Deduper())

    def label(path):
        base, sep, window = path.partition("#")
        return os.path.relpath(base, root).replace(os.sep, "/") + sep + window
    records = task_records_all(states, meta["analysisMs"], timezone.utc, label)
    calibration = task_calibration(records)
    values, sets_row = task_value_rows(states, meta["nowMs"], calibration, meta["sets"], label)
    agents = OrderedDict()
    for st in sorted(states, key=lambda st: label(st.path).encode("utf-16-be")):
        agents[label(st.path)] = OrderedDict([("source", st.source), ("stitched", st.tasks.stats["stitched"]),
                                              ("events", st.tasks.events)])
    rng = meta["comparison"]
    return OrderedDict([
        ("_comment", TASK_FIXTURE_COMMENT), ("nowMs", meta["nowMs"]), ("analysisMs", meta["analysisMs"]),
        ("timeZone", meta["timeZone"]), ("roots", meta["roots"]), ("order", meta["order"]), ("sets", meta["sets"]),
        ("comparisonRange", rng), ("agents", agents),
        ("records", [OrderedDict((k, r[k]) for k in RECORD_FIELDS) for r in records]),
        ("calibration", calibration), ("values", values), ("taskSets", sets_row),
        ("comparison", task_score.compare(records, rng["first"], rng["last"]))])


def task_fixture_hand_check(got, want):
    """The expectations written by hand in task_e2e_fixtures.py hold in the computed fixture."""
    import task_score
    failures = []

    def subset(w, g, label):
        if isinstance(w, dict):
            if not isinstance(g, dict):
                failures.append("%s: expected %r got %r" % (label, w, g))
                return
            for k, v in w.items():
                if k not in g:
                    failures.append("%s: missing %s" % (label, k))
                else:
                    subset(v, g[k], label + "." + k)
        elif isinstance(w, list) and isinstance(g, list) and any(isinstance(x, dict) for x in w):
            if len(w) != len(g):
                failures.append("%s: expected %d items got %d" % (label, len(w), len(g)))
            for i, (x, y) in enumerate(zip(w, g)):
                subset(x, y, "%s[%d]" % (label, i))
        elif not task_score.close(w, g, 1e-9):
            failures.append("%s: expected %r got %r" % (label, w, g))

    values = {v["agentId"]: v for v in got["values"]}
    for aid, w in want["agents"].items():
        subset(w, values.get(aid), "value " + aid)
    for aid, w in want["records"].items():
        subset(w, [r for r in got["records"] if r["agentId"] == aid], "records " + aid)
    subset(want["sets"], got["taskSets"]["sets"], "sets")
    subset(want["groups"], got["taskSets"]["groups"], "groups")
    subset(want["calibration"], got["calibration"], "calibration")
    for aid, n in want["stitched"].items():
        subset(n, got["agents"].get(aid, {}).get("stitched"), "stitched " + aid)
    return failures


def write_task_fixture():
    import task_e2e_fixtures as fx
    root = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fixtures"))
    files = fx.write(root)
    got = task_fixture_result(root, fx.meta(files))
    failures = task_fixture_hand_check(json.loads(json.dumps(got)), fx.want(files))
    if failures:
        print("\n".join(failures))
        print("%d hand expectations failed; %s not written" % (len(failures), TASK_FIXTURE))
        return 1
    with open(os.path.join(root, TASK_FIXTURE), "w", encoding="utf-8") as f:
        json.dump(got, f, indent=1, ensure_ascii=False)
        f.write("\n")
    print("wrote %s: %d agents, %d records, %d values" % (TASK_FIXTURE, len(got["agents"]), len(got["records"]),
                                                         len(got["values"])))
    return 0


def check_task_fixture(root):
    """spec/fixtures/task-e2e-expected.json against the reference (numbers exact: the reference wrote them)."""
    path = os.path.join(root, TASK_FIXTURE)
    if not os.path.exists(path):
        return [], 0
    want = json.load(open(path, encoding="utf-8"))
    meta = {k: want[k] for k in ("nowMs", "analysisMs", "timeZone", "roots", "order", "sets")}
    meta["comparison"] = want["comparisonRange"]
    got = json.loads(json.dumps(task_fixture_result(root, meta)))
    failures = []
    for key in want:
        if got.get(key) != want[key]:
            failures.append("task fixture: %s differs" % key)
            if isinstance(want[key], dict):
                for k in sorted(set(want[key]) | set(got.get(key) or {})):
                    if (got.get(key) or {}).get(k) != want[key].get(k):
                        failures.append("  %s.%s" % (key, k))
                        if len(failures) > 20:
                            break
    return failures, len(want["agents"])


# ----------------------------------------------------------------------------- commands

def check_fixtures():
    root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fixtures")
    expected = json.load(open(os.path.join(root, "parser-expected.json"), encoding="utf-8"))
    dedupe = Deduper()
    failures = []
    states = []
    for rel in expected["order"]:
        exp = expected["files"][rel]
        state = fixture_state(root, rel)
        process_file(state, dedupe)
        states.append(state)
        got = dict(source=state.source, project=state.project, model=state.model, isSubagent=state.is_subagent,
                   phase=state.phase, inFlightStartMs=state.in_flight, lastLineMs=state.last_line,
                   malformedLines=state.malformed, responses=list(state.responses.values()), **mode_fields(state))
        for k, v in exp.items():
            if k != "parentFile" and got[k] != v:
                failures.append("%s: %s expected %r got %r" % (rel, k, v, got[k]))
    oc = expected.get("opencode")
    if oc:
        for st in opencode_states(os.path.join(root, oc["database"]), dedupe):
            exp = oc["agents"].get(st.path)
            if exp is None:
                failures.append("opencode: unexpected agent %s" % st.path)
                continue
            got = dict(source=st.source, project=st.project, model=st.model, isSubagent=st.is_subagent,
                       parentId=st.parent_session, phase=st.phase, inFlightStartMs=st.in_flight,
                       lastLineMs=st.last_line, responses=list(st.responses.values()), **mode_fields(st))
            for k, v in exp.items():
                if got[k] != v:
                    failures.append("%s: %s expected %r got %r" % (st.path, k, v, got[k]))
    parents = resolve_parents(states)
    for rel in expected["order"]:
        parent = parents[os.path.join(root, rel)]
        got = os.path.relpath(parent, root) if parent else None
        if got != expected["files"][rel]["parentFile"]:
            failures.append("%s: parentFile expected %r got %r" % (rel, expected["files"][rel]["parentFile"], got))
    gm = expected.get("gemini")
    if gm:
        groot = os.path.join(root, "gemini", "tmp")
        found = sorted(os.path.relpath(p, root).replace(os.sep, "/") for p in discover_gemini(groot))
        if found != gm["discover"]:
            failures.append("gemini discover: expected %r got %r" % (gm["discover"], found))
        failures += check_traces(root, expected, gm["traces"])
        if gemini_roots({"GEMINI_CLI_HOME": "/x"}, "linux") != [os.path.join("/x", ".gemini", "tmp")]:
            failures.append("gemini roots: GEMINI_CLI_HOME not honoured")
        if len(gemini_roots({"GEMINI_CLI_HOME": "/x"}, "darwin")) != 2:
            failures.append("gemini roots: macOS sandbox root missing")
    om = expected.get("omp")
    if om:
        found = sorted(os.path.relpath(p, root).replace(os.sep, "/")
                       for r in fixture_omp_roots(root) for p in discover_omp(r))
        if found != om["discover"]:
            failures.append("omp discover: expected %r got %r" % (om["discover"], found))
        failures += check_traces(root, expected, om["traces"])
        failures += check_tail_reads(root, expected, om.get("tailReads", []))
        failures += check_omp_roots()
    pm = expected.get("pi")
    if pm:
        found = sorted(os.path.relpath(p, root).replace(os.sep, "/")
                       for r in fixture_pi_roots(root) for p in discover_pi(r))
        if found != pm["discover"]:
            failures.append("pi discover: expected %r got %r" % (pm["discover"], found))
        failures += check_traces(root, expected, pm["traces"])
        failures += check_pi_roots()
    ag = expected.get("antigravity")
    if ag:
        failures += check_antigravity(root, ag, dedupe)
    ocl = expected.get("openclaw")
    if ocl:
        failures += check_openclaw(root, ocl, dedupe)
    wb = expected.get("workbuddy")
    if wb:
        found = sorted(os.path.relpath(p, root).replace(os.sep, "/")
                       for _, r in fixture_wb_roots(root) for p in discover_workbuddy(r))
        if found != wb["discover"]:
            failures.append("workbuddy discover: expected %r got %r" % (wb["discover"], found))
        failures += check_traces(root, expected, wb["traces"])
        failures += check_workbuddy_roots()
    hm = expected.get("hermes")
    if hm:
        failures += check_hermes(root, hm, dedupe)
    dr = expected.get("droid")
    if dr:
        failures += check_droid(root, dr)
        failures += check_traces(root, expected, dr["traces"])
    hist_path = os.path.join(root, "history-expected.json")
    if os.path.exists(hist_path):
        hist = json.load(open(hist_path, encoding="utf-8"))
        got = fixture_history(root, expected, hist["prices"])
        want = hist["days"]
        if json.loads(json.dumps(got)) != want:
            failures.append("history: expected %s got %s" % (json.dumps(want)[:300], json.dumps(got)[:300]))
    cases = json.load(open(os.path.join(root, "engine-cases.json"), encoding="utf-8"))["cases"]
    for case in cases:
        for q in case["queries"]:
            out, tot, share = live_rate(case["responses"], case["agents"], q["nowMs"])
            for name, val in (("liveOutput", out), ("liveTotal", tot), ("estimatedShare", share)):
                if abs(val - q[name]) > 1e-6:
                    failures.append("%s @%d: %s expected %r got %r" % (case["name"], q["nowMs"], name, q[name], val))
            if "agentsOrder" in q:
                order, parent_map = agent_list(case["responses"], case["agents"], q["nowMs"])
                if order != q["agentsOrder"] or parent_map != q["agentParents"]:
                    failures.append("%s: agents %r %r" % (case["name"], order, parent_map))
            if "liveCost" in q:
                cost, share = live_cost(case["responses"], case["agents"], q["nowMs"])
                for name, val in (("liveCost", cost), ("liveCostKnownShare", share)):
                    if abs(val - q[name]) > 1e-6:
                        failures.append("%s @%d: %s expected %r got %r" % (case["name"], q["nowMs"], name, q[name],
                                                                           val))
            if "todayCostMicroUsd" in q:
                st = today_stats(case["responses"], case.get("todayStartMs", 0), q["nowMs"])
                for name, val in (("todayCostMicroUsd", st["cost"]), ("todayUnpricedRequests", st["unpriced"])):
                    if val != q[name]:
                        failures.append("%s: %s expected %r got %r" % (case["name"], name, q[name], val))
            for sub in q.get("subsets", []):
                chosen = set(sub["sources"])
                resp = [r for r in case["responses"] if r["source"] in chosen]
                ags = [a for a in case["agents"] if a["source"] in chosen]
                got = (live_rate(resp, ags, q["nowMs"])[0],
                       today_stats(resp, case.get("todayStartMs", 0), q["nowMs"])["peak"],
                       live_cost(resp, ags, q["nowMs"])[0])
                for name, val in zip(("liveOutput", "peakTodayOutput", "liveCost"), got):
                    if name not in sub:
                        continue
                    if abs(val - sub[name]) > 1e-6:
                        failures.append("%s %s: %s expected %r got %r" % (case["name"], sub["sources"], name,
                                                                         sub[name], val))
            if "todayRequests" in q:
                st = today_stats(case["responses"], case.get("todayStartMs", 0), q["nowMs"])
                checks = (("todayRequests", st["requests"]), ("todayOutput", st["output"]),
                          ("todayInput", st["input"]), ("averageToday", st["average"]),
                          ("peakTodayOutput", st["peak"]), ("todayUnfinalized", st["unfinalized"]))
                for name, val in checks:
                    if abs((val or 0) - q[name]) > 1e-6:
                        failures.append("%s: %s expected %r got %r" % (case["name"], name, q[name], val))
                for k, v in q["historyMeasuredOutput"].items():
                    if abs(st["buckets"].get(int(k), 0.0) - v) > 1e-6:
                        failures.append("%s: bucket %s expected %r got %r" % (case["name"], k, v,
                                                                              st["buckets"].get(int(k))))
    task_failures, task_agents = check_task_fixture(root)
    failures += task_failures
    failures += ["prices: " + e for e in pricing.check_cases()]
    failures += ["unpriced: " + e for e in diagnostics.check_cases()]
    failures += ["harnesses: " + e for e in harnesses.check_fixtures(root)]
    failures += ["trae: " + e for e in trae.check_fixtures(root)]
    attribution = json.load(open(os.path.join(root, "model-attribution-cases.json"), encoding="utf-8"))
    failures += ["model attribution: " + e for e in check_model_attribution(attribution)]
    if failures:
        print("\n".join(failures))
        sys.exit(1)
    print("reference implementation matches all fixtures (%d files, %d Antigravity databases, %d OpenClaw databases, "
          "%d Hermes log files, %d Droid read cycles, %d engine cases, price cases, %d model attribution cases, "
          "unpriced %s, %d task score agents end to end)" % (
              len(expected["order"]), len((ag or {}).get("order", [])), len((ocl or {}).get("dbOrder", [])),
              len((hm or {}).get("order", [])), sum("read" in st for st in (dr or {}).get("live", {}).get("steps", [])),
              len(cases), len(attribution["cases"]), diagnostics.summary(), task_agents))


def report_model_id(index, model, provider=None):
    """`models[].id` of METRICS 11.3: a table entry's id, `local` for a free provider, else `unknown`."""
    res = index.resolve(model, provider)
    if res.entry is not None:
        return res.entry["id"]
    return "local" if res.kind == "free" and res.reason == "free provider" else "unknown"


def check_model_attribution(doc, table=pricing.TABLE):
    """model-attribution-cases.json: every response and every ledger increment (METRICS 11.4) keeps its own model."""
    index = pricing.Index(pricing.load(table))
    failures = []
    for case in doc["cases"]:
        dedupe, ledger, reported = Deduper(), {}, {}
        for spec in case["files"]:
            state = FileState("/fixture/" + spec["path"], "claudeCode")
            for text in spec["lines"]:
                before = {k: dict(r) for k, r in state.responses.items()}
                claude_line(state, loads(text), dedupe)
                for key, r in state.responses.items():
                    if before.get(key) == r:
                        continue
                    mid = report_model_id(index, r["model"], r["provider"])
                    slot = ledger.setdefault((mid, state.source), [0, 0])
                    old = reported.get(key)
                    slot[0] += 0 if old is not None else 1
                    slot[1] += max(0, r["output"] - (old or 0))
                    reported[key] = max(old or 0, r["output"])
            exp = case["expect"][spec["path"]]
            got = dict(source=state.source, isSubagent=state.is_subagent, model=state.model,
                       reportModelId=report_model_id(index, state.model),
                       responses=[dict(key=r["key"], model=r["model"], output=r["output"],
                                       reportModelId=report_model_id(index, r["model"], r["provider"]))
                                  for r in state.responses.values()])
            if got != exp:
                failures.append("%s / %s: expected %r got %r" % (case["name"], spec["path"], exp, got))
        got_ledger = sorted([m, s, v[0], v[1]] for (m, s), v in ledger.items())
        want_ledger = sorted([e["modelId"], e["source"], e["requests"], e["output"]] for e in case["ledger"])
        if got_ledger != want_ledger:
            failures.append("%s: ledger expected %r got %r" % (case["name"], want_ledger, got_ledger))
    for model, want in doc["reportIds"]:
        got = report_model_id(index, model)
        if got != want:
            failures.append("report id %r: expected %s got %s" % (model, want, got))
    return failures


def check_traces(root, expected, traces):
    """Phase after feeding a file up to a physical line, after the files before it in `order`."""
    failures = []
    for t in traces:
        trace_dedupe = Deduper()
        for rel in expected["order"]:
            if rel == t["file"]:
                break
            process_file(fixture_state(root, rel), trace_dedupe)
        state = fixture_state(root, t["file"])
        process_file(state, trace_dedupe, max_lines=t["afterLine"])
        if (state.phase, state.in_flight) != (t["phase"], t["inFlightStartMs"]):
            failures.append("%s line %d: expected %s/%r got %s/%r" % (
                t["file"], t["afterLine"], t["phase"], t["inFlightStartMs"], state.phase, state.in_flight))
    return failures


def check_tail_reads(root, expected, reads):
    """Files that start being read at a byte offset (an older file that grows): the header is read first."""
    failures = []
    for t in reads:
        tail_dedupe = Deduper()
        for rel in expected["order"]:
            if rel == t["file"]:
                break
            process_file(fixture_state(root, rel), tail_dedupe)
        state = fixture_state(root, t["file"])
        process_file(state, tail_dedupe, start_offset=t["offset"])
        got = dict(project=state.project, phase=state.phase, inFlightStartMs=state.in_flight,
                   lastLineMs=state.last_line, responseKeys=list(state.responses))
        for k, v in got.items():
            if t[k] != v:
                failures.append("%s from byte %d: %s expected %r got %r" % (t["file"], t["offset"], k, t[k], v))
    return failures


def check_pi_roots():
    """Root resolution of METRICS 2 / 4.13 and the omp overlap rule of 4.7, on a scratch home."""
    failures = []
    with tempfile.TemporaryDirectory() as home:
        agent = os.path.join(home, ".pi", "agent")
        cases = [({}, None, [os.path.join(agent, "sessions")]),
                 ({}, {"sessionDir": "~/elsewhere"}, [os.path.join(agent, "sessions"), os.path.join(home, "elsewhere")]),
                 ({}, {"sessionDir": "   "}, [os.path.join(agent, "sessions")]),
                 ({}, {"sessionDir": 7}, [os.path.join(agent, "sessions")]),
                 ({"PI_CODING_AGENT_DIR": "~/pi-agent"}, None, [os.path.join(home, "pi-agent", "sessions")]),
                 ({"PI_CODING_AGENT_SESSION_DIR": "/srv/pi"}, {"sessionDir": "/srv/pi"},
                  [os.path.join(agent, "sessions"), "/srv/pi"])]
        for env, settings, want in cases:
            target = os.path.join(_expand_home(env.get("PI_CODING_AGENT_DIR", ""), home) or agent, "settings.json")
            os.makedirs(os.path.dirname(target), exist_ok=True)
            if settings is None:
                if os.path.exists(target):
                    os.remove(target)
            else:
                with open(target, "w", encoding="utf-8") as fh:
                    json.dump(settings, fh)
            got = pi_roots(env, home)
            if got != want:
                failures.append("pi roots %r %r: expected %r got %r" % (env, settings, want, got))
        omp = [os.path.join(home, ".pi", "agent", "sessions"), os.path.join(home, ".pi", "agent", "sessions", "x"),
               os.path.join(home, ".omp", "agent", "sessions"), os.path.join(home, ".pi", "agent", "sessions2")]
        got = without_pi(omp, [os.path.join(agent, "sessions")])
        if got != omp[2:]:
            failures.append("omp roots without pi: got %r" % got)
    return failures


def check_omp_roots():
    failures = []
    with tempfile.TemporaryDirectory() as tmp:
        home, xdg = os.path.join(tmp, "home"), os.path.join(tmp, "xdg")
        for d in (".omp/profiles/work", ".omp/profiles/play", ".pi-alt/profiles/solo"):
            os.makedirs(os.path.join(home, d))
        for d in ("omp/profiles/work", "omp/profiles/lab"):
            os.makedirs(os.path.join(xdg, d))
        h, x = os.path.join(home, ".omp"), os.path.join(xdg, "omp")
        cases = [
            ({}, "linux", [h + "/agent/sessions", h + "/profiles/play/agent/sessions", h + "/profiles/work/agent/sessions"]),
            ({"PI_CODING_AGENT_DIR": "/agent"}, "win32",
             ["/agent/sessions", h + "/profiles/play/agent/sessions", h + "/profiles/work/agent/sessions"]),
            ({"PI_CONFIG_DIR": ".pi-alt"}, "linux",
             [home + "/.pi-alt/agent/sessions", home + "/.pi-alt/profiles/solo/agent/sessions"]),
            ({"XDG_DATA_HOME": xdg}, "darwin",
             [x + "/sessions", x + "/profiles/lab/sessions", h + "/profiles/play/agent/sessions",
              x + "/profiles/work/sessions"]),
            ({"XDG_DATA_HOME": xdg}, "win32",
             [h + "/agent/sessions", h + "/profiles/play/agent/sessions", h + "/profiles/work/agent/sessions"]),
        ]
        for env, platform, want in cases:
            got = omp_roots(env, platform, home)
            if [os.path.normpath(p) for p in got] != [os.path.normpath(p) for p in want]:
                failures.append("omp roots %r %s: expected %r got %r" % (env, platform, want, got))
    return failures


def hermes_agent_view(state):
    return dict(source=state.source, project=state.project, model=state.model, isSubagent=state.is_subagent,
                parentId=None, phase=state.phase, inFlightStartMs=state.in_flight, lastLineMs=state.last_line,
                responses=list(state.responses.values()))


def check_hermes(root, hm, dedupe):
    """METRICS 4.15 on spec/fixtures/hermes: discovery, the log sets in `hermes.order`, traces, wall times, homes,
    a scripted rotation, config.yaml texts and a scripted Codex runtime switch."""
    failures = []
    tz = ZoneInfo(hm["timeZone"])
    home = os.path.join(root, hm["home"])
    found = sorted(os.path.relpath(p, root).replace(os.sep, "/") for f in hermes_log_folders(home)
                   for p in discover_hermes(f))
    if found != hm["discover"]:
        failures.append("hermes discover: expected %r got %r" % (hm["discover"], found))
    sets = OrderedDict()
    for rel in hm["order"]:
        folder = os.path.dirname(os.path.join(root, rel))
        if folder not in sets:
            sets[folder] = HermesSet(folder, tz)
            sets[folder].check_config()
        sets[folder].feed(os.path.join(root, rel), dedupe)
    for rel_folder, exp in hm["sets"].items():
        hs = sets.get(os.path.join(root, rel_folder))
        if hs is None:
            failures.append("hermes %s: log set not read" % rel_folder)
            continue
        if hs.malformed != exp["malformedLines"]:
            failures.append("hermes %s: malformedLines expected %r got %r" % (rel_folder, exp["malformedLines"],
                                                                            hs.malformed))
        if list(hs.agents) != list(exp["agents"]):
            failures.append("hermes %s: agents expected %r got %r" % (rel_folder, list(exp["agents"]), list(hs.agents)))
        for sid, want in exp["agents"].items():
            if sid not in hs.agents:
                continue
            got = hermes_agent_view(hs.agents[sid])
            for k, v in want.items():
                if got[k] != v:
                    failures.append("hermes %s#%s: %s expected %r got %r" % (rel_folder, sid, k, v, got[k]))
    for t in hm["traces"]:
        trace_dedupe, trace_sets = Deduper(), {}
        for rel in hm["order"]:
            folder = os.path.dirname(os.path.join(root, rel))
            if folder not in trace_sets:
                trace_sets[folder] = HermesSet(folder, tz)
                trace_sets[folder].check_config()
            hs = trace_sets[folder]
            if rel == t["file"]:
                hs.feed(os.path.join(root, rel), trace_dedupe, max_lines=t["afterLine"])
                break
            hs.feed(os.path.join(root, rel), trace_dedupe)
        state = hs.agents.get(t["session"])
        got = (state.phase, state.in_flight) if state else (None, None)
        if got != (t["phase"], t["inFlightStartMs"]):
            failures.append("hermes %s line %d %s: expected %s/%r got %s/%r" % (
                t["file"], t["afterLine"], t["session"], t["phase"], t["inFlightStartMs"], got[0], got[1]))
    for case in hm["wallTimes"]:
        m = HERMES_RECORD.fullmatch(case["line"])
        got = hermes_wall_ms(m, ZoneInfo(case["timeZone"])) if m else None
        if got != case["ms"]:
            failures.append("hermes wall time %r in %s: expected %r got %r" % (case["line"][:23], case["timeZone"],
                                                                             case["ms"], got))
    for case in hm["homes"]:
        got = hermes_home(case["env"], case["platform"], case["home"])
        if got != case["expected"]:
            failures.append("hermes home %r %s: expected %r got %r" % (case["env"], case["platform"],
                                                                       case["expected"], got))
    failures += check_hermes_rotation(hm["rotation"])
    for case in hm["runtimeConfigs"]:
        got = hermes_codex_runtime(case["text"])
        if got != case["codexRuntime"]:
            failures.append("hermes config.yaml %r: codexRuntime expected %r got %r" % (case["name"],
                                                                                      case["codexRuntime"], got))
    failures += check_hermes_codex_runtime(hm["codexRuntime"])
    return failures


def check_hermes_codex_runtime(scenario):
    """Replays `hermes.codexRuntime` in a scratch Hermes home (METRICS 4.15 Codex runtime)."""
    failures = []
    tz = ZoneInfo(scenario["timeZone"])
    with tempfile.TemporaryDirectory() as tmp:
        home = os.path.join(tmp, "home")
        tails = OrderedDict()
        for rel in scenario["logFolders"]:
            os.makedirs(os.path.join(home, *rel.split("/")))
            tails[rel] = HermesTail(os.path.join(home, *rel.split("/")), tz)
        dedupe = Deduper()
        for number, step in enumerate(scenario["steps"], start=1):
            path = os.path.join(home, *(step.get("append") or step.get("write") or step.get("remove") or "").split("/"))
            if "append" in step:
                with open(path, "ab") as fh:
                    fh.write("".join(line + "\n" for line in step["lines"]).encode("utf-8"))
                os.utime(path, (step["mtime"] / 1000, step["mtime"] / 1000))
            elif "write" in step:
                data = step["text"].encode("utf-8")
                data += b"#" * (step.get("size", len(data)) - len(data))
                with open(path, "wb") as fh:
                    fh.write(data)
                os.utime(path, (step["mtime"] / 1000, step["mtime"] / 1000))
            elif "remove" in step:
                os.remove(path)
            else:
                for tail in tails.values():
                    tail.read(dedupe)
                keys = [k for tail in tails.values() for st in tail.set.agents.values() for k in st.responses]
                if sorted(keys) != sorted(step["responseKeys"]):
                    failures.append("hermes codex runtime step %d: keys expected %r got %r" % (
                        number, step["responseKeys"], keys))
                for rel, want in step["sets"].items():
                    hs = tails[rel].set
                    if hs.codex_runtime != want["codexRuntime"]:
                        failures.append("hermes codex runtime step %d %s: codexRuntime expected %r got %r" % (
                            number, rel, want["codexRuntime"], hs.codex_runtime))
                    for sid, agent_want in want["agents"].items():
                        st = hs.agents.get(sid)
                        got = dict(phase=st.phase, inFlightStartMs=st.in_flight, lastLineMs=st.last_line,
                                   model=st.model) if st else None
                        if got is None or any(got[k] != v for k, v in agent_want.items()):
                            failures.append("hermes codex runtime step %d %s#%s: expected %r got %r" % (
                                number, rel, sid, agent_want, got))
    return failures


def check_hermes_rotation(rotation):
    """Replays `hermes.rotation` in a scratch log folder with real renames (identity following, METRICS 4.15)."""
    failures = []
    tz = ZoneInfo(rotation["timeZone"])
    with tempfile.TemporaryDirectory() as tmp:
        folder = os.path.join(tmp, "logs")
        os.makedirs(folder)
        tail, dedupe = HermesTail(folder, tz), Deduper()
        for number, step in enumerate(rotation["steps"], start=1):
            if "append" in step:
                path = os.path.join(folder, step["append"])
                text = step["text"] if "text" in step else "".join(line + "\n" for line in step["lines"])
                with open(path, "ab") as fh:
                    fh.write(text.encode("utf-8"))
                os.utime(path, (step["mtime"] / 1000, step["mtime"] / 1000))
            elif "rename" in step:
                os.rename(os.path.join(folder, step["rename"][0]), os.path.join(folder, step["rename"][1]))
            elif "delete" in step:
                os.remove(os.path.join(folder, step["delete"]))
            else:
                tail.read(dedupe)
                keys = [k for st in tail.set.agents.values() for k in st.responses]
                if sorted(keys) != sorted(step["responseKeys"]):
                    failures.append("hermes rotation step %d: keys expected %r got %r" % (number, step["responseKeys"],
                                                                                         keys))
                for sid, want in step["agents"].items():
                    st = tail.set.agents.get(sid)
                    got = dict(phase=st.phase, inFlightStartMs=st.in_flight, lastLineMs=st.last_line) if st else None
                    if got != want:
                        failures.append("hermes rotation step %d %s: expected %r got %r" % (number, sid, want, got))
    return failures


def check_antigravity(root, ag, dedupe):
    failures = []
    tree = os.path.join(root, "antigravity")
    before = sorted(os.path.relpath(os.path.join(d, n), tree) for d, _, ns in os.walk(tree) for n in ns)
    stores = antigravity_stores(gemini_dir=tree)
    found = sorted(os.path.relpath(p, root).replace(os.sep, "/") for s in stores for p in discover_antigravity(s))
    if found != ag["discover"]:
        failures.append("antigravity discover: expected %r got %r" % (ag["discover"], found))
    backup = os.path.join(tree, "antigravity-backup")
    if antigravity_stores(setting=backup) or antigravity_stores(setting=os.path.join(backup, "conversations")):
        failures.append("antigravity: a -backup store chosen in the setting must not be read")
    for store in stores:
        via_folder = [p for s in antigravity_stores(setting=os.path.join(store, "conversations"))
                      for p in discover_antigravity(s)]
        if sorted(via_folder) != sorted(discover_antigravity(store)):
            failures.append("antigravity: a `conversations` folder chosen in the setting must be used directly")
    states = []
    for rel in ag["order"]:
        exp = ag["databases"][rel]
        st = antigravity_state(os.path.join(root, rel), dedupe)
        if st is None:
            if exp != {"unsupported": True}:
                failures.append("%s: unexpectedly unsupported" % rel)
            continue
        if exp.get("unsupported"):
            failures.append("%s: expected unsupported" % rel)
            continue
        states.append(st)
        got = dict(source=st.source, project=st.project, model=st.model, isSubagent=st.is_subagent,
                   phase=st.phase, inFlightStartMs=st.in_flight, lastLineMs=st.last_line, malformedLines=st.malformed,
                   responses=list(st.responses.values()))
        for k, v in exp.items():
            if k != "parentFile" and got[k] != v:
                failures.append("%s: %s expected %r got %r" % (rel, k, v, got[k]))
    parents = antigravity_parents(states)
    for st in states:
        rel = os.path.relpath(st.path, root).replace(os.sep, "/")
        got = os.path.relpath(parents[st.path], root).replace(os.sep, "/") if parents[st.path] else None
        if got != ag["databases"][rel]["parentFile"]:
            failures.append("%s: parentFile expected %r got %r" % (rel, ag["databases"][rel]["parentFile"], got))
    after = sorted(os.path.relpath(os.path.join(d, n), tree) for d, _, ns in os.walk(tree) for n in ns)
    if after != before:
        failures.append("antigravity: reading created files %r" % sorted(set(after) - set(before)))
    return failures


def check_openclaw(root, oc, dedupe):
    """OpenClaw agent databases (METRICS 4.14): discovery, the Codex roots next to them, every window, traces."""
    failures = []
    home = os.path.join(root, "openclaw")
    before = sorted(os.path.relpath(os.path.join(d, n), home) for d, _, ns in os.walk(home) for n in ns)
    dbs = [db for r in fixture_openclaw_dirs(root) for db in discover_openclaw(r)]
    rel = lambda path: os.path.relpath(path, root).replace(os.sep, "/")  # noqa: E731
    if sorted(rel(db) for db in dbs) != oc["discover"]:
        failures.append("openclaw discover: expected %r got %r" % (oc["discover"], sorted(rel(db) for db in dbs)))
    rollouts = sorted(rel(f) for r in openclaw_codex_roots(dbs) for f in discover(r, codex=True))
    if rollouts != oc["codexDiscover"]:
        failures.append("openclaw codex roots: expected %r got %r" % (oc["codexDiscover"], rollouts))
    main = os.path.join(root, *oc["dbOrder"][0].split("/"))
    for setting in (os.path.dirname(main), main):
        if discover_openclaw(setting) != [main]:
            failures.append("openclaw: the setting %s must name that database" % rel(setting))
    for db_rel in oc["dbOrder"]:
        exp_db = oc["databases"][db_rel]
        states = openclaw_states(os.path.join(root, *db_rel.split("/")), dedupe)
        if states is None:
            if exp_db != {"unsupported": True}:
                failures.append("%s: unexpectedly unsupported" % db_rel)
            continue
        if exp_db.get("unsupported"):
            failures.append("%s: expected unsupported" % db_rel)
            continue
        got_ids = [rel(st.path) for st in states]
        if sorted(got_ids) != sorted(exp_db):
            failures.append("%s: windows expected %r got %r" % (db_rel, sorted(exp_db), sorted(got_ids)))
        for st in states:
            exp = exp_db.get(rel(st.path))
            if exp is None:
                continue
            got = dict(source=st.source, project=st.project, model=st.model, isSubagent=st.is_subagent,
                       parentId=rel(st.parent_session) if st.parent_session else None, phase=st.phase,
                       inFlightStartMs=st.in_flight, lastLineMs=st.last_line, malformedLines=st.malformed,
                       responses=list(st.responses.values()), **mode_fields(st))
            for k, v in exp.items():
                if got[k] != v:
                    failures.append("%s: %s expected %r got %r" % (rel(st.path), k, v, got[k]))
    for t in oc["traces"]:
        trace_dedupe = Deduper()
        for db_rel in oc["dbOrder"]:
            path = os.path.join(root, *db_rel.split("/"))
            if db_rel != t["database"]:
                openclaw_states(path, trace_dedupe)
                continue
            st = (openclaw_states(path, trace_dedupe, stop_at=(t["window"], t["afterSeq"])) or [None])[-1]
            got = (st.phase, st.in_flight) if st and st.path.endswith("#" + t["window"]) else None
            if got != (t["phase"], t["inFlightStartMs"]):
                failures.append("%s#%s row %d: expected %s/%r got %r" % (db_rel, t["window"], t["afterSeq"], t["phase"],
                                                                        t["inFlightStartMs"], got))
            break
    failures += check_openclaw_roots()
    after = sorted(os.path.relpath(os.path.join(d, n), home) for d, _, ns in os.walk(home) for n in ns)
    if after != before:
        failures.append("openclaw: reading created files %r" % sorted(set(after) - set(before)))
    return failures


def check_droid(root, dr):
    """METRICS 4.16 on spec/fixtures/droid: discovery, roots, sidecars, allocations and scripted read cycles."""
    failures = []
    rel = lambda path: os.path.relpath(path, root).replace(os.sep, "/")  # noqa: E731
    found = sorted(rel(p) for p in discover_droid(fixture_droid_root(root)))
    if found != dr["discover"]:
        failures.append("droid discover: expected %r got %r" % (dr["discover"], found))
    for case in dr["roots"]:
        with tempfile.TemporaryDirectory() as tmp:
            for folder in case["make"]:
                os.makedirs(os.path.join(tmp, *folder.split("/")))
            setting = os.path.join(tmp, *case["setting"].split("/")) if case["setting"] else None
            got = os.path.relpath(droid_root(tmp, setting), tmp).replace(os.sep, "/")
            if got != case["expected"]:
                failures.append("droid root %r: expected %r got %r" % (case, case["expected"], got))
    for case in dr["sidecars"]:
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "x.settings.json")
            data = case["text"].encode(case.get("encoding", "utf-8"))
            with open(path, "wb") as fh:
                fh.write(data + b" " * (case.get("size", len(data)) - len(data)))
            readable, counters, model = droid_read_sidecar(path)
            got = dict(readable=readable, counters=counters, model=model)
            want = dict(readable=case["readable"], counters=case["counters"], model=case["model"])
            if got != want:
                failures.append("droid sidecar %r: expected %r got %r" % (case["name"], want, got))
    for case in dr["allocations"]:
        got = droid_shares(case["total"], case["weights"])
        if got != case["shares"]:
            failures.append("droid allocation %r: expected %r got %r" % (case["weights"], case["shares"], got))
    failures += check_droid_live(dr["live"])
    failures += check_droid_history(dr["history"])
    return failures


def check_droid_history(history):
    """Replays `droid.history`: history analyses of a scratch home, the parent memory kept between them."""
    failures = []
    with tempfile.TemporaryDirectory() as tmp:
        memory, none = {}, os.path.join(tmp, "none")
        for n, step in enumerate(history["steps"], start=1):
            if "write" in step:
                path = os.path.join(tmp, *step["write"].split("/"))
                os.makedirs(os.path.dirname(path), exist_ok=True)
                with open(path, "w", encoding="utf-8", newline="\n") as fh:
                    fh.write(step["text"])
                os.utime(path, ns=(step["mtime"] * 1_000_000, step["mtime"] * 1_000_000))
                continue
            skipped, got = [], OrderedDict()
            rows = collect_all(none, none, none, None, include_archived=False,
                               droid_roots=[droid_root(tmp)], droid_memory=memory, droid_skipped=skipped)
            for _, agent, r in rows:
                got.setdefault(os.path.relpath(agent, tmp).replace(os.sep, "/"), []).append(r)
            skipped = sorted(os.path.relpath(p, tmp).replace(os.sep, "/") for p in skipped)
            if dict(got) != step["responses"] or skipped != step["skipped"]:
                failures.append("droid history step %d: expected %r skipped %r, got %r skipped %r" % (
                    n, step["responses"], step["skipped"], dict(got), skipped))
    return failures


def check_droid_live(live):
    """Replays `droid.live` in a scratch home (METRICS 4.16 Read cycle)."""
    failures = []
    with tempfile.TemporaryDirectory() as tmp:
        dedupe, tails, seen = Deduper(), {}, {}
        for n, step in enumerate(live["steps"], start=1):
            if "append" in step or "rewrite" in step:
                path = os.path.join(tmp, *(step.get("append") or step["rewrite"]).split("/"))
                os.makedirs(os.path.dirname(path), exist_ok=True)
                data = "".join(json.dumps(line, ensure_ascii=False, separators=(",", ":")) + "\n"
                               for line in step["lines"]).encode("utf-8")
                if "append" in step:
                    with open(path, "ab") as fh:
                        fh.write(data)
                else:
                    with open(path + ".new", "wb") as fh:
                        fh.write(data)
                    os.replace(path + ".new", path)
            elif "sidecar" in step:
                path = droid_sidecar_path(os.path.join(tmp, *step["sidecar"].split("/")))
                with open(path, "w", encoding="utf-8") as fh:
                    fh.write(step["text"] if "text" in step else json.dumps(step["doc"], indent=2))
                os.utime(path, ns=(step["mtime"] * 1_000_000, step["mtime"] * 1_000_000))
            elif "open" in step:
                path = os.path.join(tmp, *step["open"].split("/"))
                tails[step["open"]] = DroidTail(path, dedupe, at_end=step["atEnd"])
                seen[step["open"]] = OrderedDict()
            elif "read" in step:
                tail = tails[step["read"]]
                tail.read()
                for key, r in tail.state.responses.items():
                    seen[step["read"]][key] = r
                got = list(seen[step["read"]].values())
                if got != step["responses"]:
                    failures.append("droid live step %d: responses expected %r got %r" % (n, step["responses"], got))
                if tail.state.droid_growth != step["pending"]:
                    failures.append("droid live step %d: pending expected %r got %r" % (n, step["pending"],
                                                                                       tail.state.droid_growth))
                if "phase" in step and tail.state.phase != step["phase"]:
                    failures.append("droid live step %d: phase expected %s got %s" % (n, step["phase"],
                                                                                     tail.state.phase))
    return failures


def check_openclaw_roots():
    """State folders of METRICS 2 / 4.14 on a scratch home."""
    failures = []
    with tempfile.TemporaryDirectory() as home:
        for name in (".openclaw", ".openclaw-work", ".openclaw-Default", ".openclaw-x.y", ".openclaw-_z",
                     ".openclaw-2nd", "openclaw-play"):
            os.makedirs(os.path.join(home, name))
        open(os.path.join(home, ".openclaw-file"), "w").close()
        oc = os.path.join(home, ".openclaw")
        cases = [({}, [oc, os.path.join(home, ".openclaw-2nd"), os.path.join(home, ".openclaw-work")]),
                 ({"OPENCLAW_STATE_DIR": "~/state"}, [os.path.join(home, "state")]),
                 ({"OPENCLAW_STATE_DIR": "  ", "OPENCLAW_PROFILE": "work"},
                  [oc, os.path.join(home, ".openclaw-2nd"), os.path.join(home, ".openclaw-work")]),
                 ({"OPENCLAW_HOME": "/srv/oc"}, [os.path.join("/srv/oc", ".openclaw")]),
                 ({"OPENCLAW_HOME": "~/.openclaw-work"}, [os.path.join(home, ".openclaw-work", ".openclaw")])]
        for env, want in cases:
            got = openclaw_state_dirs(env, home)
            if got != [os.path.normpath(w) for w in want]:
                failures.append("openclaw roots %r: expected %r got %r" % (env, want, got))
    return failures


def fixture_openclaw_dirs(root):
    """The fixture folder `openclaw` plays the home folder: its state folders are .openclaw and .openclaw-work."""
    return openclaw_state_dirs({}, os.path.join(root, "openclaw"))


def check_workbuddy_roots():
    """Both data folders under the home folder; a setting names a data folder or its `projects` folder."""
    failures = []
    home = os.path.join(os.sep, "home", "dev")
    cases = [
        ({}, [("workbuddy", os.path.join(home, ".workbuddy", "projects")),
              ("workbuddyAi", os.path.join(home, ".workbuddy-ai", "projects"))]),
        ({"workbuddy": "/data/wb/", "workbuddyAi": "/data/wb-ai/projects"},
         [("workbuddy", "/data/wb/projects"), ("workbuddyAi", "/data/wb-ai/projects")]),
    ]
    for settings, want in cases:
        got = workbuddy_roots(home, settings)
        if [(s, os.path.normpath(p)) for s, p in got] != [(s, os.path.normpath(p)) for s, p in want]:
            failures.append("workbuddy roots %r: expected %r got %r" % (settings, want, got))
    return failures


def fixture_wb_roots(root):
    """The fixture folder `workbuddy` plays the home folder of both WorkBuddy editions."""
    return workbuddy_roots(os.path.join(root, "workbuddy"))


def fixture_droid_root(root):
    """The fixture folder `droid` plays the home folder: its sessions root is droid/.factory/sessions."""
    return droid_root(os.path.join(root, "droid"))


def fixture_source(rel):
    if rel.startswith("openclaw/") and "/codex-home/sessions/" in rel:
        return "codex"
    if rel.startswith("droid/"):
        return "droid"
    for prefix, source in (("codex/", "codex"), ("dsh/", "deepseek"), ("gemini/", "gemini"), ("omp/", "omp"), ("pi/", "pi"),
                           ("workbuddy/.workbuddy/", "workbuddy"), ("workbuddy/.workbuddy-ai/", "workbuddyAi")):
        if rel.startswith(prefix):
            return source
    return "claudeCode"


def fixture_pi_roots(root):
    """The fixture folder `pi` plays `~/.pi` with the fixture folder as home (its settings name a custom folder)."""
    return pi_roots({"PI_CODING_AGENT_DIR": os.path.join(root, "pi", "agent")}, root)


def fixture_omp_roots(root):
    """The fixture folder `omp` plays `~/.omp`: its automatic roots are the default root and every profile."""
    return omp_roots({"PI_CONFIG_DIR": "omp"}, "linux", root)


def fixture_hermes_dirs(root):
    """The fixture folder `hermes/home` plays the Hermes home (`~/.hermes`): its log folder and every profile's."""
    return hermes_log_folders(os.path.join(root, "hermes", "home"))


def fixture_state(root, rel):
    path = os.path.join(root, rel)
    source = fixture_source(rel)
    if source in WB_FOLDERS:
        return FileState(path, source, next((r for _, r in fixture_wb_roots(root) if path.startswith(r + os.sep)), None))
    omp_root = next((r for r in fixture_omp_roots(root) if path.startswith(r + os.sep)), None)
    return FileState(path, source, omp_root if source == "omp" else None)


HISTORY_PRICES = OrderedDict([("tables", ["prices/v1.json", "prices/v2.json"]), ("bindings", {"2026-10-20": 1})])


def fixture_pricing(root, config):
    """The fixture price tables of history-expected.json `prices`; the newest table is the current one."""
    return pricing.DayPricing([pricing.load(os.path.join(root, rel)) for rel in config["tables"]], config["bindings"])


def fixture_history(root, expected, config=HISTORY_PRICES):
    """Daily history over a copy of the fixtures whose modification times follow `mtimes` (git does not keep them)."""
    with tempfile.TemporaryDirectory() as tmp:
        copy = os.path.join(tmp, "fixtures")
        shutil.copytree(root, copy)
        for rel, ms in expected.get("mtimes", {}).items():
            os.utime(os.path.join(copy, rel), (ms / 1000, ms / 1000))
        return daily_history(collect_all(os.path.join(copy, "codex", "sessions"), os.path.join(copy, "claude", "projects"),
                                         os.path.join(copy, "dsh", "sessions"), os.path.join(copy, "opencode", "opencode.db"),
                                         [os.path.join(copy, "gemini", "tmp")], fixture_omp_roots(copy),
                                         antigravity_stores(gemini_dir=os.path.join(copy, "antigravity")),
                                         fixture_wb_roots(copy), pi_dirs=fixture_pi_roots(copy),
                                         openclaw_dbs=[db for r in fixture_openclaw_dirs(copy) for db in
                                                       discover_openclaw(r)],
                                         hermes_dirs=fixture_hermes_dirs(copy), hermes_tz=hermes_zone(expected),
                                         droid_roots=[fixture_droid_root(copy)]),
                             tz=timezone.utc, day_pricing=fixture_pricing(root, config))


def hermes_zone(expected):
    zone = expected.get("hermes", {}).get("timeZone")
    return ZoneInfo(zone) if zone else None


def write_history_fixture():
    root = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fixtures"))
    expected = json.load(open(os.path.join(root, "parser-expected.json"), encoding="utf-8"))
    with open(os.path.join(root, "history-expected.json"), "w", encoding="utf-8") as f:
        json.dump({"_comment": "Daily history (METRICS 8, 9.7) over all fixtures, grouped in UTC, after setting each "
                               "file's modification time from parser-expected.json `mtimes`. Costs use the fixture "
                               "price tables in `prices.tables` (paths relative to spec/fixtures; the highest version "
                               "is the current table) with the stored day bindings `prices.bindings`; unbound days "
                               "bind to the current table. `unpriced` lists the day's unpriced facts (METRICS 9.10): "
                               "trimmed raw model, provider (null when none), fast, requests, as stored in the side "
                               "table `contribution_unpriced` summed over files, ordered by the UTF-8 bytes of model, "
                               "then provider (null first), then fast; its requests add up to `unpricedRequests`. "
                               "Generated by spec/tools/reference.py --write-history-fixture; platforms must match "
                               "exactly.",
                   "prices": HISTORY_PRICES,
                   "days": fixture_history(root, expected)}, f, indent=2, ensure_ascii=False)
        f.write("\n")


DSH_NAME = re.compile(r"^session\.v\d+\.jsonl(\.zstd)?$")


def discover(root, limit=20_000, deepseek=False, codex=False):
    found = []
    if not os.path.isdir(root):
        return found
    base_depth = root.rstrip(os.sep).count(os.sep)
    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        if dirpath.count(os.sep) - base_depth >= 6:
            dirnames[:] = []
        dirnames[:] = [d for d in dirnames if not os.path.islink(os.path.join(dirpath, d))]
        for name in filenames:
            path = os.path.join(dirpath, name)
            wanted = DSH_NAME.match(name) if deepseek else name.endswith((".jsonl", ".jsonl.zst") if codex else ".jsonl")
            if wanted and not os.path.islink(path):
                found.append(path)
    found.sort(key=lambda p: os.path.getmtime(p), reverse=True)
    return found[:limit]


def startup_files(codex_root, claude_root, dsh_root, gemini_dirs=(), omp_dirs=(), ag_stores=(), wb_roots=(),
                  limit=20_000, include_archived=False, pi_dirs=(), openclaw_dbs=(), codex_roots=None, droid_roots=()):
    """(time, path, source, root) of every discovered file and Antigravity or OpenClaw database, in startup order (5);
    the root (omp root or WorkBuddy `projects`) decides sub-agents by folder structure. `codex_roots` replaces the
    automatic Codex roots (the Codex setting); by default they are `codex_root` and the Codex homes OpenClaw keeps next
    to its agent databases (METRICS 2)."""
    files = []
    if codex_roots is None:
        codex_roots = [codex_root] + openclaw_codex_roots(openclaw_dbs)
    for root in OrderedDict.fromkeys(codex_roots):
        files += [(p, "codex", None) for p in discover(root, limit=limit, codex=True)]
        if include_archived:
            archived = os.path.join(os.path.dirname(root.rstrip(os.sep)), "archived_sessions")
            files += [(p, "codex", None) for p in discover(archived, limit=limit, codex=True)]
    files += [(p, "claudeCode", None) for p in discover(claude_root, limit=limit)]
    files += [(p, "deepseek", None) for p in discover(dsh_root, limit=limit, deepseek=True)]
    files += [(p, "gemini", None) for d in gemini_dirs for p in discover_gemini(d, limit=limit)]
    files += [(p, "omp", r) for r in OrderedDict.fromkeys(without_pi(omp_dirs, pi_dirs))
              for p in discover_omp(r, limit=limit)]
    files += [(p, "pi", r) for r in OrderedDict.fromkeys(pi_dirs) for p in discover_pi(r, limit=limit)]
    files += [(p, "antigravity", None) for s in ag_stores for p in discover_antigravity(s, limit=limit)]
    files += [(p, s, r) for s, r in wb_roots for p in discover_workbuddy(r, limit=limit)]
    files += [(p, "openclaw", None) for p in OrderedDict.fromkeys(openclaw_dbs)]
    files += [(p, "droid", None) for r in OrderedDict.fromkeys(droid_roots) for p in discover_droid(r, limit=limit)]
    timed = {(ag_mtime(p) if s in ("antigravity", "openclaw") else droid_mtime(p) if s == "droid"
              else os.path.getmtime(p), p, s, r) for p, s, r in files}
    return sorted(timed, key=lambda t: (t[0], t[1]))


def today_rows(codex_root, claude_root, dsh_root, gemini_dirs=(), omp_dirs=(), ag_stores=(), wb_roots=(),
               table=pricing.TABLE, zcode_root=None, kimi_root=None, grok_root=None, pi_dirs=(), openclaw_dbs=(),
               codex_roots=None, hermes_dirs=(), droid_roots=(), tasks=False, trae_paths=()):
    """Today's responses as the engine seeds them (the `--dump-today` rows), with files and bytes read; with `tasks`,
    also the seeded agents of scored sources with their task composers (METRICS 6.9)."""
    import task_events
    scored = []
    index = pricing.Index(pricing.load(table))
    now = datetime.now().astimezone()
    today_start = int(now.replace(hour=0, minute=0, second=0, microsecond=0).timestamp() * 1000)
    files = startup_files(codex_root, claude_root, dsh_root, gemini_dirs, omp_dirs, ag_stores, wb_roots, pi_dirs=pi_dirs,
                          openclaw_dbs=openclaw_dbs, codex_roots=codex_roots, droid_roots=droid_roots)
    dedupe = Deduper()
    rows, read_files, read_bytes = [], 0, 0
    for mtime, path, source, root in files:
        if mtime * 1000 < today_start - 15 * 60_000:
            continue
        size = os.path.getsize(path)
        if size > 256 * 1024 * 1024 and path.endswith(".zstd"):
            continue
        if source == "gemini" and path.endswith(".json") and size > GEMINI_LEGACY_MAX:
            continue
        if source == "antigravity":
            states = [antigravity_state(path, dedupe)]
            if states[0] is None:
                continue
            offset = 0
        elif source == "openclaw":
            states = openclaw_states(path, dedupe, tasks=tasks)
            if states is None:
                continue
            offset = 0
        else:
            offset = size - 64 * 1024 * 1024 if size > 256 * 1024 * 1024 and not path.endswith(".jsonl.zst") else 0
            states = [FileState(path, source, root)]
            if tasks:
                task_events.attach(states[0])
            process_file(states[0], dedupe, offset)
        scored += [st for st in states if st.tasks is not None]
        read_files += 1
        read_bytes += size - offset
        for state, r in [(st, r) for st in states for r in st.responses.values()]:
            if r["endMs"] >= today_start:
                effort, speed = mode_view(state.response_facts.get(r["key"], []))
                rows.append(OrderedDict([("key", r["key"]), ("source", state.source), ("agentId", state.path),
                                         ("model", r["model"]), ("startMs", r["startMs"]), ("endMs", r["endMs"]),
                                         ("input", r["input"]), ("cachedInput", r["cachedInput"]),
                                         ("cacheWriteInput", r["cacheWriteInput"]),
                                         ("cacheWrite1hInput", r["cacheWrite1hInput"]), ("output", r["output"]),
                                         ("reasoning", r["reasoning"]), ("final", r["final"]),
                                         ("provider", r["provider"]), ("fast", r["fast"]),
                                         ("requestCount", 1),
                                         ("costMicroUsd", index.price(r) if has_day(r["endMs"]) else None),
                                         ("effort", effort), ("speed", speed)]))
    for folder in OrderedDict.fromkeys(hermes_dirs):
        for path in hermes_files(folder, today_start - 15 * 60_000):
            read_files += 1
            read_bytes += os.path.getsize(path)
    for hs in hermes_sets(hermes_dirs, dedupe, since_ms=today_start - 15 * 60_000):
        for st in hs.agents.values():
            for r in st.responses.values():
                if r["endMs"] >= today_start:
                    rows.append(OrderedDict([("key", r["key"]), ("source", "hermes"), ("agentId", st.path)]
                                            + [(k, r[k]) for k in ("model", "startMs", "endMs", "input", "cachedInput",
                                                                   "cacheWriteInput", "cacheWrite1hInput", "output",
                                                                   "reasoning", "final", "provider", "fast")]
                                            + [("requestCount", 1),
                                               ("costMicroUsd", index.price(r) if has_day(r["endMs"]) else None),
                                               ("effort", None), ("speed", None)]))
    for source, agent, record in harnesses.collect(zcode_root, kimi_root, grok_root) + trae.collect(trae_paths):
        if record["endMs"] >= today_start:
            rows.append(dict(record, source=source, agentId=agent,
                             costMicroUsd=index.price(record) if has_day(record["endMs"]) else None))
    rows.sort(key=lambda r: (r["endMs"], r["key"]))
    return (rows, read_files, read_bytes, scored) if tasks else (rows, read_files, read_bytes)


def dump_today(codex_root, claude_root, dsh_root, gemini_dirs=(), omp_dirs=(), ag_stores=(), wb_roots=(),
               table=pricing.TABLE, zcode_root=None, kimi_root=None, grok_root=None, pi_dirs=(), openclaw_dbs=(),
               codex_roots=None, hermes_dirs=(), droid_roots=(), now_ms=None, trae_paths=()):
    started = time.time()
    rows, read_files, read_bytes, scored = today_rows(codex_root, claude_root, dsh_root, gemini_dirs, omp_dirs,
                                                      ag_stores, wb_roots, table, zcode_root, kimi_root, grok_root,
                                                      pi_dirs, openclaw_dbs, codex_roots, hermes_dirs, droid_roots,
                                                      tasks=True, trae_paths=trae_paths)
    for r in rows:
        print(json.dumps(r, ensure_ascii=False, separators=(",", ":")))
    now = now_ms if now_ms is not None else int(time.time() * 1000)
    task_rows, sets_row = task_value_rows(scored, now)
    for row in task_rows:
        print(json.dumps({"task": row}, ensure_ascii=False, separators=(",", ":")))
    print(json.dumps({"taskSets": sets_row}, ensure_ascii=False, separators=(",", ":")))
    counts = {}
    for r in rows:
        counts[r["source"]] = counts.get(r["source"], 0) + 1
    print(json.dumps({"summary": {"counts": counts, "output": sum(r["output"] for r in rows),
                                  "costMicroUsd": sum(r["costMicroUsd"] or 0 for r in rows),
                                  "unpricedRequests": sum(r.get("requestCount", 1) for r in rows if r["costMicroUsd"] is None),
                                  "input": sum(r["input"] for r in rows), "filesRead": read_files,
                                  "bytesRead": read_bytes, "seedMs": int((time.time() - started) * 1000),
                                  "nowMs": now, "taskAgents": len(task_rows)}}))


def collect_all(codex_root, claude_root, dsh_root, opencode_db, gemini_dirs=(), omp_dirs=(), ag_stores=(),
                wb_roots=(), include_archived=True, zcode_root=None, kimi_root=None, grok_root=None, pi_dirs=(),
                openclaw_dbs=(), codex_roots=None, hermes_dirs=(), hermes_tz=None, droid_roots=(), droid_memory=None,
                droid_skipped=None, trae_paths=()):
    """Parses every log completely (METRICS 8) and returns all responses with their source and agent. A Droid session
    whose parent counters are unknown is skipped (listed in `droid_skipped`); `droid_memory` keeps the signatures of
    unreadable parent sidecars from one analysis to the next (METRICS 4.16 Own counters)."""
    files = startup_files(codex_root, claude_root, dsh_root, gemini_dirs, omp_dirs, ag_stores, wb_roots,
                          limit=10 ** 9, include_archived=include_archived, pi_dirs=pi_dirs, openclaw_dbs=openclaw_dbs,
                          codex_roots=codex_roots, droid_roots=droid_roots)
    dedupe = Deduper(limit=10 ** 9)
    out = []
    for _, path, source, root in files:
        if source == "openclaw":
            out += [(st.source, st.path, r) for st in openclaw_states(path, dedupe) or [] for r in st.responses.values()]
            continue
        if source == "antigravity":
            state = antigravity_state(path, dedupe)
            if state is None:
                continue
        else:
            state = FileState(path, source, root)
            if source == "droid" and droid_memory is not None:
                state.droid_parent_bad = droid_memory.get(path)
            process_file(state, dedupe)
            if source == "droid":
                if droid_memory is not None:
                    droid_memory[path] = state.droid_parent_bad
                if state.droid_unknown:
                    if droid_skipped is not None:
                        droid_skipped.append(path)
                    continue
                droid_finish(state)
        out += [(state.source, path, r) for r in state.responses.values()]
    if opencode_db and os.path.exists(opencode_db):
        for st in opencode_states(opencode_db, dedupe):
            out += [(st.source, st.path, r) for r in st.responses.values()]
    for hs in hermes_sets(hermes_dirs, dedupe, hermes_tz):
        out += [(st.source, st.path, r) for st in hs.agents.values() for r in st.responses.values()]
    out += harnesses.collect(zcode_root, kimi_root, grok_root)
    out += trae.collect(trae_paths)
    return out


HISTORY_TOKENS = ("input", "cachedInput", "cacheWriteInput", "output", "reasoning")


def unpriced_range(days=None, first=None, last=None, live=False):
    """(view, first, last) of `--dump-unpriced`: the History window's presets for --days, else a custom range."""
    today = datetime.now().astimezone().date()
    if live:
        return "live", today.isoformat(), today.isoformat()
    if first is not None:
        return "custom", first, last
    count = days or 30
    view = {1: "today", 7: "7d", 30: "30d", 90: "90d"}.get(count, "custom")
    return view, (today - timedelta(days=count - 1)).isoformat(), today.isoformat()


def dump_unpriced(parts, table_path, view, first, last, source=None):
    """Prints the copied diagnostics text (METRICS 9.10) of `parts` with the given table as the current one."""
    index = pricing.Index(pricing.load(table_path))
    _, text = diagnostics.report(parts, index, diagnostics.reference_app(), view, first, last, source)
    sys.stdout.write(text)


def daily_history(responses, tz=None, day_pricing=None):
    """Rows per (date, source), METRICS 8 and 9.7. `day_pricing` defaults to the bundled table for every day."""
    day_pricing = day_pricing or pricing.DayPricing([pricing.load(pricing.TABLE)])
    days = {}
    for source, agent, r in responses:
        if not has_day(r["endMs"]):
            continue
        date = datetime.fromtimestamp(r["endMs"] / 1000, tz=tz or datetime.now().astimezone().tzinfo)
        key = (date.strftime("%Y-%m-%d") if tz else datetime.fromtimestamp(r["endMs"] / 1000).strftime("%Y-%m-%d"),
               source)
        d = days.setdefault(key, dict(requests=0, input=0, cachedInput=0, cacheWriteInput=0, output=0, reasoning=0,
                                      measuredOutput=0, measuredMs=0, unfinalized=0, costMicroUsd=0,
                                      unpricedRequests=0, agents=set(), models={}, unpriced={}))
        requests = r.get("requestCount", 1)
        d["requests"] += requests
        for f in HISTORY_TOKENS:
            d[f] += r[f]
        iv = interval(r)
        m = d["models"].setdefault(r.get("model") or "", [0, 0, 0, 0, 0, 0])
        m[0] += requests
        m[1] += r["output"]
        if iv is not None and is_final(r):
            d["measuredOutput"] += r["output"]
            d["measuredMs"] += iv[1] - iv[0]
            m[2] += r["output"]
            m[3] += iv[1] - iv[0]
        if not is_final(r):
            d["unfinalized"] += requests
        cost = day_pricing.price(key[0], r)
        if cost is None:
            d["unpricedRequests"] += requests
            m[5] += requests
            fact = diagnostics.fact_key(r.get("model"), r.get("provider"), r.get("fast"))
            d["unpriced"][fact] = d["unpriced"].get(fact, 0) + requests
        else:
            d["costMicroUsd"] += cost
            m[4] += cost
        d["agents"].add(agent)
    rows = []
    for (date, source), d in sorted(days.items()):
        rows.append(OrderedDict([("date", date), ("source", source)] + [(k, d[k]) for k in (
            "requests", "input", "cachedInput", "cacheWriteInput", "output", "reasoning", "measuredOutput",
            "measuredMs", "unfinalized", "costMicroUsd", "unpricedRequests")] + [
            ("priceVersion", day_pricing.version_for(date)), ("agents", len(d["agents"])),
            ("models", {k: {"requests": v[0], "output": v[1], "measuredOutput": v[2], "measuredMs": v[3],
                            "costMicroUsd": v[4], "unpricedRequests": v[5]}
                        for k, v in sorted(d["models"].items())}),
            ("unpriced", diagnostics.fact_list(d["unpriced"]))]))
    return rows


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check-fixtures", action="store_true")
    parser.add_argument("--dump-today", action="store_true")
    parser.add_argument("--dump-history", action="store_true")
    parser.add_argument("--opencode", default=None)
    parser.add_argument("--utc", action="store_true", help="group days in UTC instead of local time")
    parser.add_argument("--codex", default=None)
    parser.add_argument("--claude", default=None)
    parser.add_argument("--dsh", default=None)
    parser.add_argument("--zcode", default=None, help="ZCode data folder, database folder, or db.sqlite")
    parser.add_argument("--kimi", default=None, help="Kimi Code CLI sessions folder")
    parser.add_argument("--grok", default=None, help="Grok Build sessions folder")
    parser.add_argument("--trae", action="append", default=[],
                        help="Trae database file (repeatable; partial request usage)")
    parser.add_argument("--gemini", action="append", default=None, help="Gemini CLI tmp folder (repeatable)")
    parser.add_argument("--omp", action="append", default=None, help="omp sessions folder (repeatable)")
    parser.add_argument("--pi", default=None, help="Pi sessions folder (replaces every automatic Pi root)")
    parser.add_argument("--hermes", default=None,
                        help="Hermes Agent home or one logs folder (replaces the automatic Hermes home)")
    parser.add_argument("--antigravity", action="append", default=None,
                        help="Antigravity store folder or its conversations folder (repeatable)")
    parser.add_argument("--workbuddy", default=None, help="WorkBuddy data folder (~/.workbuddy) or its projects folder")
    parser.add_argument("--workbuddy-ai", default=None,
                        help="WorkBuddy AI data folder (~/.workbuddy-ai) or its projects folder")
    parser.add_argument("--openclaw", default=None,
                        help="OpenClaw state folder, agent folder or openclaw-agent.sqlite (replaces the automatic roots)")
    parser.add_argument("--droid", default=None,
                        help="Factory Droid .factory folder or its sessions folder (replaces the automatic root)")
    parser.add_argument("--write-history-fixture", action="store_true")
    parser.add_argument("--prices", default=pricing.TABLE, help="price table for --dump-history (METRICS 9)")
    parser.add_argument("--dump-unpriced", action="store_true", help="unpriced diagnostics text (METRICS 9.10)")
    parser.add_argument("--days", type=int, default=None,
                        help="--dump-unpriced: the last N local days, today included (default 30)")
    parser.add_argument("--first", default=None, help="--dump-unpriced: first local date YYYY-MM-DD (with --last)")
    parser.add_argument("--last", default=None, help="--dump-unpriced: last local date YYYY-MM-DD")
    parser.add_argument("--source", default=None, help="--dump-unpriced: one source id (METRICS 1)")
    parser.add_argument("--live", action="store_true",
                        help="--dump-unpriced: today's engine responses (Settings -> Cost) instead of the history")
    parser.add_argument("--dump-tasks", action="store_true",
                        help="task records of every log read whole, calibration and comparison (METRICS 6.11)")
    parser.add_argument("--now", type=int, default=None,
                        help="--dump-today: evaluate the task values at this Unix ms instead of the capture time")
    parser.add_argument("--write-task-fixture", action="store_true",
                        help="regenerate spec/fixtures/task-e2e-expected.json (METRICS 6.9 Composing the stream)")
    args = parser.parse_args()
    home = os.path.expanduser("~")
    codex_root = args.codex or (os.path.join(os.environ["CODEX_HOME"], "sessions") if os.environ.get("CODEX_HOME")
                                else os.path.join(home, ".codex", "sessions"))
    claude_root = args.claude or (os.path.join(os.environ["CLAUDE_CONFIG_DIR"], "projects")
                                  if os.environ.get("CLAUDE_CONFIG_DIR") else os.path.join(home, ".claude", "projects"))
    dsh_root = args.dsh or (os.path.join(os.environ["DSH_HOME"], "sessions") if os.environ.get("DSH_HOME")
                            else os.path.join(home, ".dsh", "sessions"))
    opencode_db = args.opencode or os.path.join(os.environ.get("XDG_DATA_HOME") or os.path.join(home, ".local", "share"),
                                                "opencode", "opencode.db")
    gemini_dirs = args.gemini or gemini_roots()
    omp_dirs = args.omp or omp_roots()
    pi_dirs = [args.pi] if args.pi else pi_roots()
    hermes_dirs = hermes_setting_folders(args.hermes) if args.hermes else hermes_log_folders(hermes_home())
    ag_stores = ([s for a in args.antigravity for s in antigravity_stores(setting=a)] if args.antigravity
                 else antigravity_stores())
    wb_roots = workbuddy_roots(settings={"workbuddy": args.workbuddy, "workbuddyAi": args.workbuddy_ai})
    zcode_root = args.zcode or os.path.join(os.environ.get("ZCODE_DATA_BASE_DIR") or home, ".zcode", "cli", "db")
    kimi_root = args.kimi or os.path.join(os.environ.get("KIMI_CODE_HOME") or os.path.join(home, ".kimi-code"), "sessions")
    grok_root = args.grok or os.path.join(os.environ.get("GROK_HOME") or os.path.join(home, ".grok"), "sessions")
    openclaw_dbs = [db for r in ([args.openclaw] if args.openclaw else openclaw_state_dirs()) for db in discover_openclaw(r)]
    codex_roots = [args.codex] if args.codex else None
    droid_roots = [droid_root(setting=args.droid)] if args.droid else [droid_root(home)]
    if args.write_history_fixture:
        write_history_fixture()
        return
    if args.write_task_fixture:
        sys.exit(write_task_fixture())
    if args.dump_tasks:
        if args.source is not None and args.source not in TASK_SOURCES:
            parser.error("--source takes a scored source id: " + ", ".join(TASK_SOURCES))
        files = startup_files(codex_root, claude_root, os.path.join(home, ".agent-speed-none"), omp_dirs=omp_dirs,
                              limit=10 ** 9, include_archived=True, pi_dirs=pi_dirs, openclaw_dbs=openclaw_dbs,
                              codex_roots=codex_roots, droid_roots=droid_roots)
        dump_tasks(files, args.first, args.last, args.source, timezone.utc if args.utc else None)
        return
    if args.dump_unpriced:
        if args.days is not None and (args.days < 1 or args.first or args.last):
            parser.error("--days takes a positive number and excludes --first/--last")
        if any(t is not None and not pricing.DATE.match(t) for t in (args.first, args.last)):
            parser.error("dates are YYYY-MM-DD")
        if (args.first is None) != (args.last is None) or (args.first and args.first > args.last):
            parser.error("--first and --last go together, first <= last")
        if args.live and (args.days is not None or args.first):
            parser.error("--live takes no range: it is today's engine data")
        if args.source is not None and args.source not in diagnostics.SOURCES:
            parser.error("--source takes a source id: " + ", ".join(diagnostics.SOURCES))
        view, first, last = unpriced_range(args.days, args.first, args.last, args.live)
        if args.live:
            rows, _, _ = today_rows(codex_root, claude_root, dsh_root, gemini_dirs, omp_dirs, ag_stores, wb_roots,
                                    args.prices, zcode_root, kimi_root, grok_root, pi_dirs=pi_dirs,
                                    openclaw_dbs=openclaw_dbs, codex_roots=codex_roots, hermes_dirs=hermes_dirs,
                                    droid_roots=droid_roots, trae_paths=args.trae)
            parts = diagnostics.live_parts([(r["source"], r) for r in rows], first)
        else:
            parts = diagnostics.history_parts(daily_history(
                collect_all(codex_root, claude_root, dsh_root, opencode_db, gemini_dirs, omp_dirs, ag_stores, wb_roots,
                            zcode_root=zcode_root, kimi_root=kimi_root, grok_root=grok_root, pi_dirs=pi_dirs,
                            openclaw_dbs=openclaw_dbs, codex_roots=codex_roots, hermes_dirs=hermes_dirs,
                            droid_roots=droid_roots, trae_paths=args.trae),
                day_pricing=pricing.DayPricing([pricing.load(args.prices)])))
        dump_unpriced(parts, args.prices, view, first, last, args.source)
        return
    if args.dump_history:
        rows = daily_history(collect_all(codex_root, claude_root, dsh_root, opencode_db, gemini_dirs, omp_dirs,
                                         ag_stores, wb_roots, zcode_root=zcode_root, kimi_root=kimi_root,
                                         grok_root=grok_root, pi_dirs=pi_dirs, openclaw_dbs=openclaw_dbs,
                                         codex_roots=codex_roots, hermes_dirs=hermes_dirs, droid_roots=droid_roots,
                                         trae_paths=args.trae),
                             tz=timezone.utc if args.utc else None,
                             day_pricing=pricing.DayPricing([pricing.load(args.prices)]))
        for row in rows:
            print(json.dumps(row, ensure_ascii=False, separators=(",", ":")))
        return
    if args.check_fixtures:
        check_fixtures()
    elif args.dump_today:
        dump_today(codex_root, claude_root, dsh_root, gemini_dirs, omp_dirs, ag_stores, wb_roots, args.prices,
                   zcode_root, kimi_root, grok_root, pi_dirs=pi_dirs, openclaw_dbs=openclaw_dbs, codex_roots=codex_roots,
                   hermes_dirs=hermes_dirs, droid_roots=droid_roots, now_ms=args.now, trae_paths=args.trae)
    else:
        parser.print_help()


if __name__ == "__main__":
    main()
