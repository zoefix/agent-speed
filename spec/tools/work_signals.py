#!/usr/bin/env python3
"""Reference implementation of the work signals (METRICS 6.10).

  python3 spec/tools/work_signals.py            check spec/fixtures/work-signal-cases.json

Two parts: `ActionLog` turns one file's tool calls and results into actions without keeping their content, and
`signals` reads looping, repeating, novelty and recovery from a list of actions.
"""
from __future__ import annotations

import hashlib
import json
import os
import sys
from collections import OrderedDict
from dataclasses import dataclass, field

FIXTURES = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fixtures")

MAX_ACTIONS = 64
RECENT_MS = 600_000
FRESH_MS = 120_000
LOOP_REPEATS = 3
LOOP_BLOCKS = (1, 2, 3)
NOVELTY_MIN = 8
RECOVERY_WINDOW = 5


@dataclass
class Action:
    at: int
    tool: str
    digest: str
    failed: bool | None = None          # None: outcome unknown
    answered: bool = field(default=False, compare=False)

    @property
    def key(self):
        return self.tool, self.digest


def digest(value):
    """First 16 hex digits of SHA-256 over the input as stored: a string's text, an object's raw JSON. The apps hash
    the raw slice of the line; re-serializing compactly gives the same text for files written like the fixtures."""
    text = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]


class ActionLog:
    """The last MAX_ACTIONS actions of one file; results are matched to their call by id."""

    def __init__(self):
        self.actions: list[Action] = []
        self.by_call: OrderedDict[str, Action] = OrderedDict()

    def call(self, call_id, at, tool, value):
        if not isinstance(call_id, str) or call_id in self.by_call or not isinstance(tool, str):
            return
        action = Action(at, tool, digest(value if value is not None else ""))
        self.actions.append(action)
        self.by_call[call_id] = action
        if len(self.actions) > MAX_ACTIONS:
            dropped = self.actions.pop(0)
            self.by_call = OrderedDict((k, a) for k, a in self.by_call.items() if a is not dropped)

    def result(self, call_id, failed):
        """Only the first result of a call is its outcome (METRICS 6.10); a later one is ignored."""
        action = self.by_call.get(call_id)
        if action is None or action.answered:
            return
        action.answered, action.failed = True, failed


# ----------------------------------------------------------------------------- per-source extraction

def claude_line(log, obj, at):
    content = (obj.get("message") or {}).get("content")
    for block in content if isinstance(content, list) else []:
        if not isinstance(block, dict):
            continue
        if obj.get("type") == "assistant" and block.get("type") == "tool_use":
            log.call(block.get("id"), at, block.get("name"), block.get("input"))
        elif obj.get("type") == "user" and block.get("type") == "tool_result":
            log.result(block.get("tool_use_id"), block.get("is_error") is True)


def codex_exit_code(output):
    """`exit_code` of the leading JSON object of an exec output, or None (METRICS 6.10)."""
    if isinstance(output, list):
        first = next((i for i in output if isinstance(i, dict) and i.get("type") == "input_text"), None)
        output = first.get("text") if first else None
    if not isinstance(output, str) or not output.lstrip().startswith("{"):
        return None
    try:
        head, _ = json.JSONDecoder().raw_decode(output.lstrip())
    except ValueError:
        return None
    code = head.get("exit_code") if isinstance(head, dict) else None
    return code if isinstance(code, int) and not isinstance(code, bool) else None


def codex_line(log, obj, at):
    payload = obj.get("payload") if isinstance(obj.get("payload"), dict) else {}
    kind = payload.get("type")
    if obj.get("type") != "response_item":
        return
    if kind in ("function_call", "custom_tool_call"):
        log.call(payload.get("call_id"), at, payload.get("name"),
                 payload.get("arguments" if kind == "function_call" else "input"))
    elif kind in ("function_call_output", "custom_tool_call_output"):
        output = payload.get("output")
        if isinstance(output, str):
            try:
                output = json.loads(output) if output.lstrip().startswith("[") else output
            except ValueError:
                pass
        code = codex_exit_code(output)
        log.result(payload.get("call_id"), None if code is None else code != 0)


def pi_family_line(log, obj, at):
    """omp and Pi: assistant toolCall blocks and toolResult messages."""
    message = obj.get("message") if obj.get("type") == "message" and isinstance(obj.get("message"), dict) else {}
    if message.get("role") == "assistant":
        for block in message.get("content") if isinstance(message.get("content"), list) else []:
            if isinstance(block, dict) and block.get("type") == "toolCall":
                log.call(block.get("id"), at, block.get("name"), block.get("arguments"))
    elif message.get("role") == "toolResult":
        log.result(message.get("toolCallId"), message.get("isError") is True)


def droid_line(log, obj, at):
    """Factory Droid: Anthropic-shaped blocks in `message` lines (assistant tool_use, user tool_result)."""
    message = obj.get("message") if obj.get("type") == "message" and isinstance(obj.get("message"), dict) else {}
    content = message.get("content")
    for block in content if isinstance(content, list) else []:
        if not isinstance(block, dict):
            continue
        if message.get("role") == "assistant" and block.get("type") == "tool_use":
            log.call(block.get("id"), at, block.get("name"), block.get("input"))
        elif message.get("role") == "user" and block.get("type") == "tool_result":
            log.result(block.get("tool_use_id"), block.get("is_error") is True)


EXTRACTORS = {"claude": claude_line, "codex": codex_line, "omp": pi_family_line, "pi": pi_family_line,
              "droid": droid_line}


# ----------------------------------------------------------------------------- signals

def repeat_run(actions, size):
    """How many times the newest block of `size` actions repeats back to back, and the actions it covers."""
    if len(actions) < size:
        return 0, []
    block = [a.key for a in actions[-size:]]
    count, end = 1, len(actions) - size
    while end - size >= 0 and [a.key for a in actions[end - size:end]] == block:
        count, end = count + 1, end - size
    return count, actions[end:]


def signals(actions, now):
    recent = [a for a in actions if a.at >= now - RECENT_MS]
    looping = repeating = 0
    if recent and now - recent[-1].at < FRESH_MS:
        for size in LOOP_BLOCKS:
            count, run = repeat_run(recent, size)
            if count < LOOP_REPEATS:
                continue
            if any(a.failed for a in run):
                looping = max(looping, count)
            else:
                repeating = max(repeating, count)
    failed = [i for i, a in enumerate(recent) if a.failed]
    recovered = sum(1 for i in failed if any(
        b.tool == recent[i].tool and b.failed is False and b.at - recent[i].at <= RECENT_MS
        for b in recent[i + 1:i + 1 + RECOVERY_WINDOW]))
    novelty = len({a.key for a in recent}) / len(recent) if len(recent) >= NOVELTY_MIN else None
    return {"looping": looping or None, "repeating": None if looping else repeating or None,
            "novelty": novelty, "failed": len(failed), "recovered": recovered}


# ----------------------------------------------------------------------------- fixtures

def check_cases(path=None):
    cases = json.load(open(path or os.path.join(FIXTURES, "work-signal-cases.json"), encoding="utf-8"))
    failures = []
    for case in cases["digests"]:
        if digest(case["input"]) != case["expect"]:
            failures.append("digest %r: expected %s got %s" % (case["input"], case["expect"], digest(case["input"])))
    for case in cases["signals"]:
        actions = [Action(a["at"], a["tool"], a["digest"], a.get("failed")) for a in case["actions"]]
        got = signals(actions, case["nowMs"])
        if got != case["expect"]:
            failures.append("signals %s: expected %r got %r" % (case["name"], case["expect"], got))
    for case in cases["files"]:
        log = ActionLog()
        for at, obj in case["lines"]:
            EXTRACTORS[case["format"]](log, obj, at)
        got = [{"at": a.at, "tool": a.tool, "digest": a.digest, "failed": a.failed} for a in log.actions]
        if got != case["expect"]:
            failures.append("file %s: expected %r got %r" % (case["name"], case["expect"], got))
    return failures, sum(len(cases[k]) for k in ("digests", "signals", "files"))


def main():
    failures, count = check_cases()
    if failures:
        print("\n".join(failures))
        sys.exit(1)
    print("work signals reference matches %d cases" % count)


if __name__ == "__main__":
    main()
