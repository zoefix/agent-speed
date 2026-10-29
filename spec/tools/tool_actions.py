#!/usr/bin/env python3
"""The actions of METRICS 6.10 read from log lines: every tool call with its tool, input digest, call time, the time
of its first result, whether it failed and the model of the response that issued it.

reference.py feeds every line to `Reader.feed` right after the source's own handler, so copied history and
deduplication are decided exactly as for tokens: a line the parser skips adds nothing, and the calls of an assistant
line or entry whose key was first seen in another file are not actions (their results match nothing). Inputs and
outputs are read in memory only; an action keeps the fields below.
"""
from __future__ import annotations

import hashlib
import json
import os
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from work_signals import codex_exit_code  # noqa: E402

SOURCES = ("codex", "claudeCode", "claudeDesktop", "omp", "pi", "openclaw", "droid")
COPY_MS = 2000
PI_FAMILY = ("omp", "pi", "openclaw")


def engine():
    import reference
    return reference


def family(source):
    return "claude" if source in ("claudeCode", "claudeDesktop") else source


def sha16(text):
    return hashlib.sha256(text.encode("utf-8", "surrogatepass")).hexdigest()[:16]


EMPTY = sha16("")

# ----------------------------------------------------------------------------- raw input slices (6.10 digest)

_DECODER = json.JSONDecoder()
_WS = " \t\n\r"


def _skip(s, i):
    while i < len(s) and s[i] in _WS:
        i += 1
    return i


def _members(s, i):
    """(key, start, end) of the members of the JSON object that starts at s[i]."""
    out, i = [], _skip(s, i + 1)
    if s[i] == "}":
        return out
    while True:
        key, i = json.decoder.scanstring(s, i + 1)
        i = _skip(s, _skip(s, i) + 1)
        end = _DECODER.raw_decode(s, i)[1]
        out.append((key, i, end))
        i = _skip(s, end)
        if s[i] != ",":
            return out
        i = _skip(s, i + 1)


def _elements(s, i):
    """(start, end) of the elements of the JSON array that starts at s[i]."""
    out, i = [], _skip(s, i + 1)
    if s[i] == "]":
        return out
    while True:
        end = _DECODER.raw_decode(s, i)[1]
        out.append((i, end))
        i = _skip(s, end)
        if s[i] != ",":
            return out
        i = _skip(s, i + 1)


def _last(s, i, name):
    span = None
    for key, start, end in _members(s, i):
        if key == name:
            span = (start, end)
    return span


def raw_inputs(text, field):
    """The raw JSON text of `message.content[k][field]` for every content index k whose block holds that field (a
    repeated member takes its last value, as json.loads)."""
    out = {}
    s = text if isinstance(text, str) else text.decode("utf-8", "surrogatepass")
    start = _skip(s, 0)
    message = _last(s, start, "message")
    if message is None or s[message[0]] != "{":
        return out
    content = _last(s, message[0], "content")
    if content is None or s[content[0]] != "[":
        return out
    for k, (a, _) in enumerate(_elements(s, content[0])):
        if s[a] == "{":
            span = _last(s, a, field)
            if span is not None:
                out[k] = s[span[0]:span[1]]
    return out


def digest(value, raw):
    """METRICS 6.10: a string input's text; an object's or array's JSON text exactly as the line stores it (`raw`);
    an absent or null input hashes as empty text."""
    if value is None:
        return EMPTY
    if isinstance(value, str):
        return sha16(value)
    text = raw if raw is not None else json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    return sha16(text)


def codex_failed(output):
    """6.10 Codex: the output's leading JSON object (in the first `input_text` item when it is an array) has an
    integer `exit_code`; None when the output does not say."""
    if isinstance(output, str):
        try:
            output = json.loads(output) if output.lstrip().startswith("[") else output
        except ValueError:
            pass
    code = codex_exit_code(output)
    return None if code is None else code != 0


# ----------------------------------------------------------------------------- the reader

class Reader:
    """The actions of one agent (a log file, or an OpenClaw transcript window), in file order."""

    def __init__(self, state):
        self.path = state.path
        self.completions = state.completions
        self.actions = []
        self.calls = {}
        self.header_seen = False

    def feed(self, state, obj, dedupe, raw):
        fam = family(state.source)
        if fam == "claude":
            self.claude(state, obj, dedupe, raw)
        elif fam == "codex":
            self.codex(state, obj)
        elif fam in PI_FAMILY:
            self.pi_family(state, obj, dedupe, raw)
        elif fam == "droid":
            self.droid(state, obj, dedupe, raw)

    def claude(self, state, obj, dedupe, raw):
        ref = engine()
        t = ref.parse_ts(obj.get("timestamp"))
        if t is None:
            return
        kind = obj.get("type")
        message = obj.get("message") if isinstance(obj.get("message"), dict) else {}
        if kind == "user":
            content = message.get("content")
            for block in content if isinstance(content, list) else []:
                if isinstance(block, dict) and block.get("type") == "tool_result":
                    self.result(block.get("tool_use_id"), t, block.get("is_error") is True)
        elif kind == "assistant":
            if message.get("model") == "<synthetic>":
                return
            key = message.get("id") or obj.get("requestId") or obj.get("uuid")
            if not isinstance(key, str) or not key or dedupe.owner(key) not in (None, self.path):
                return
            self.calls_in(state, message.get("content"), "tool_use", "input", t, raw)

    def codex(self, state, obj):
        ref = engine()
        t = ref.parse_ts(obj.get("timestamp"))
        if t is None or obj.get("type") != "response_item":
            return
        if state.file_created is not None and t < state.file_created - COPY_MS:
            return
        line_ordinal = ref.ordinal(obj.get("ordinal"))
        if state.start_ordinal is not None and line_ordinal is not None and line_ordinal < state.start_ordinal:
            return
        payload = obj.get("payload") if isinstance(obj.get("payload"), dict) else {}
        ptype = payload.get("type")
        if ptype in ("function_call", "custom_tool_call"):
            value = payload.get("arguments" if ptype == "function_call" else "input")
            self.call(state, payload.get("call_id"), t, payload.get("name"), digest(value, None))
        elif ptype in ("function_call_output", "custom_tool_call_output"):
            self.result(payload.get("call_id"), t, codex_failed(payload.get("output")))

    def pi_family(self, state, obj, dedupe, raw):
        ref = engine()
        kind = obj.get("type")
        if kind == "session":
            self.header_seen = True
            return
        if not self.header_seen or not isinstance(obj.get("id"), str):
            return
        t = ref.parse_ts(obj.get("timestamp"))
        if t is None or (state.file_created is not None and t < state.file_created - COPY_MS):
            return
        message = obj.get("message")
        if kind != "message" or not isinstance(message, dict):
            return
        role = message.get("role")
        if role == "assistant":
            if not ref.omp_guard(message):
                return
            stamp = ref.trunc64(message["timestamp"]) if ref.finite(message.get("timestamp")) and \
                message["timestamp"] > 0 else t
            key = "%s:%s:%d" % (state.source, obj["id"], stamp)
            if dedupe.owner(key) not in (None, self.path):
                return
            if state.source == "openclaw" and openclaw_mirror(state, message):
                return
            self.calls_in(state, message.get("content"), "toolCall", "arguments", t, raw)
        elif role == "toolResult":
            self.result(message.get("toolCallId"), t, message.get("isError") is True)

    def droid(self, state, obj, dedupe, raw):
        ref = engine()
        t = ref.parse_ts(obj.get("timestamp"))
        message = obj.get("message")
        if t is None or obj.get("type") != "message" or not isinstance(message, dict):
            return
        role = message.get("role")
        if role == "user":
            content = message.get("content")
            for block in content if isinstance(content, list) else []:
                if isinstance(block, dict) and block.get("type") == "tool_result":
                    self.result(block.get("tool_use_id"), t, block.get("is_error") is True)
        elif role == "assistant" and isinstance(obj.get("id"), str) and obj["id"]:
            if dedupe.owner("droid:" + obj["id"]) not in (None, self.path):
                return
            self.calls_in(state, message.get("content"), "tool_use", "input", t, raw)

    def calls_in(self, state, content, block_type, field, t, raw):
        if not isinstance(content, list):
            return
        raws = None
        for k, block in enumerate(content):
            if not isinstance(block, dict) or block.get("type") != block_type:
                continue
            value = block.get(field)
            if raws is None and isinstance(value, (dict, list)) and raw is not None:
                raws = raw_inputs(raw, field)
            self.call(state, block.get("id"), t, block.get("name"), digest(value, (raws or {}).get(k)))

    def call(self, state, call_id, t, tool, dig):
        if not isinstance(call_id, str) or not isinstance(tool, str) or call_id in self.calls:
            return
        action = {"tool": tool, "digest": dig, "callMs": t, "resultMs": None, "failed": None,
                  "model": state.model}
        self.calls[call_id] = action
        self.actions.append(action)

    def result(self, call_id, t, failed):
        action = self.calls.get(call_id) if isinstance(call_id, str) else None
        if action is None or action["resultMs"] is not None:
            return
        action["resultMs"], action["failed"] = t, failed
        self.completions.append(("action", action))


def openclaw_mirror(state, message):
    """METRICS 4.14 *Mirrors*: a delegated turn mirrored into the transcript."""
    meta = message.get("__openclaw") if isinstance(message.get("__openclaw"), dict) else {}
    origin = meta.get("mirrorOrigin")
    return ((isinstance(origin, str) and origin != "") or (message["api"] == "cli" and message["provider"] == "claude-cli")
            or message["provider"].startswith("acp-") or state.oc_mirror_window)
