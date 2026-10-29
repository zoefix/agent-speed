#!/usr/bin/env python3
"""Reference implementation of the task score (METRICS 6.9) and of task history, predictions and comparison
(METRICS 6.11).

  python3 spec/tools/task_score.py            check spec/fixtures/task-score-cases.json
  python3 spec/tools/task_score.py --write    regenerate it from spec/tools/task_score_cases.py

The model consumes a normalised event stream per agent (prompts and actions already parsed by the functions of
spec/TASK-PARSERS.md); nothing here reads a log. `--write` computes every case and refuses to write when a result
differs from the expectation written by hand in task_score_cases.py.
"""
from __future__ import annotations

import json
import math
import os
import sys
import unicodedata

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import live_thinking as thinking  # noqa: E402
from live_thinking import seq_sum  # noqa: E402
import reference as engine  # noqa: E402

FIXTURE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fixtures", "task-score-cases.json")

SCORED_SOURCES = ("codex", "claudeCode", "claudeDesktop", "omp", "pi", "openclaw", "droid")
SOURCE_ORDER = ("codex", "claudeCode", "claudeDesktop", "deepseek", "opencode", "gemini", "omp", "antigravity",
                "workbuddy", "workbuddyAi", "zcode", "kimi", "grokBuild", "pi", "openclaw", "hermes", "droid", "trae")
VERIFY = ("test", "build", "check")

V_TEST, V_BUILD = 40.0, 20.0
C_MAX, R_MAX, E_MAX = 25.0, 15.0, 10.0
K_BUILD, K_TEST, K_FULL = 4.0, 4.0, 2.0
RAMP = 8
LOOP_BLOCKS = (1, 2, 3)
LOOP_REPEATS = 3
STALL_WINDOW, STALL_SHARE = 8, 0.25
SNAPSHOTS = (1, 2, 4, 8, 16, 32, 64, 128, 256, 512)
COMPLETE_IDLE_MS = 30 * 60_000

SUCCESS_FEATURES = ("passing", "failing", "conv", "ig", "loop", "osc", "size")
STALL_FEATURES = ("ig", "loop", "osc", "failing", "size")
FIX_FEATURES = ("attempts", "errors", "trend", "osc", "ig")
SNAPSHOT_FIELDS = ("n", "passing", "failing", "conv", "ig", "loop", "osc", "size", "yStall")
FIX_FIELDS = ("attempts", "errors", "trend", "osc", "ig", "yFix")
PRIORS = {
    "success": (-1.5, 3.0, -0.5, 1.0, 1.0, -2.0, -1.5, -0.5),
    "stall": (-1.0, -2.0, 3.0, 2.0, 0.8, 0.5),
    "fix": (-0.5, 1.5, 1.0, 1.0, 1.5, -1.0),
}
MODEL_FEATURES = {"success": SUCCESS_FEATURES, "stall": STALL_FEATURES, "fix": FIX_FEATURES}
MIN_TASKS, MIN_CLASS_WEIGHT, MAX_TASKS = 40, 5.0, 2000
L2, ITERATIONS, ETA_LIMIT = 4.0, 25, 35.0

# ----------------------------------------------------------------------------- task segmentation (6.9 Tasks)

CONTINUATIONS = frozenset([
    "continue", "continue please", "please continue", "go on", "keep going", "proceed", "please proceed",
    "carry on", "go ahead", "resume", "ok", "okay", "ok continue", "ok go ahead", "yes", "y", "yep", "yes please",
    "sure", "do it", "lgtm",
    "繼續", "繼續吧", "請繼續", "繼續做", "接著做", "好", "好的", "好啊", "可以", "嗯", "是", "對", "行", "沒問題",
    "继续", "继续吧", "请继续", "继续做", "接着做", "对", "没问题",
    "続けて", "続けてください", "続行", "進めて", "進めてください", "はい", "お願いします", "うん", "いいよ", "どうぞ",
])
TRAILING = ".!~,。、"
ASCII_LETTERS = frozenset("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz")
CLAUDE_OUTPUT_TAGS = ("local-command-stdout", "local-command-stderr")
CLAUDE_IGNORED_TAGS = ("local-command-caveat", "bash-input", "bash-stdout", "bash-stderr", "user-memory-input",
                       "system-reminder", "task-notification")



def normalize_prompt(text):
    """NFKC, trimmed, trailing `.!~,。、` removed, ASCII letters lower-cased, whitespace runs collapsed."""
    s = unicodedata.normalize("NFKC", text).strip()
    while s and (s[-1] in TRAILING or s[-1].isspace()):
        s = s[:-1]
    s = "".join(chr(ord(c) + 32) if "A" <= c <= "Z" else c for c in s)
    return " ".join(s.split())


def is_continuation(text):
    return isinstance(text, str) and normalize_prompt(text) in CONTINUATIONS


def _starts_with_tag(head, tags):
    for tag in tags:
        opening = "<" + tag
        if head.startswith(opening) and (len(head) == len(opening) or head[len(opening)] in ">/" or
                                         head[len(opening)].isspace()):
            return True
    return False


def _content_parts(content, media_types):
    """(texts, has media, lead text) of a message content, or None when it has neither text nor media."""
    if isinstance(content, str):
        texts, media = [content], False
    elif isinstance(content, list):
        blocks = [b for b in content if isinstance(b, dict)]
        texts = [b["text"] for b in blocks if b.get("type") == "text" and isinstance(b.get("text"), str)]
        media = any(b.get("type") in media_types for b in blocks)
    else:
        return None
    if not media and all(not t.strip() for t in texts):
        return None
    return texts, media, texts[0] if texts else ""


def _prompt_text(content, texts):
    return content if isinstance(content, str) else "\n".join(t for t in texts if not t.lstrip().startswith("<"))


def claude_queued_prompt(obj):
    """A message the user typed while the agent worked: a `queued_command` attachment in prompt mode, from a human."""
    attachment = obj.get("attachment")
    if not isinstance(attachment, dict) or attachment.get("type") != "queued_command":
        return None
    if attachment.get("commandMode") != "prompt" or attachment.get("humanTurn") is not True:
        return None
    if obj.get("isMeta") is True or attachment.get("isMeta") is True:
        return None
    content = attachment.get("prompt")
    parts = _content_parts(content, ("image", "document"))
    if parts is None:
        return None
    found = {"kind": "prompt", "continuation": is_continuation(_prompt_text(content, parts[0]))}
    queued_ms = engine.parse_ts(attachment.get("timestamp"))
    if queued_ms is not None:
        found["t"] = queued_ms
    return found


def claude_prompt(obj):
    if obj.get("type") == "attachment":
        return claude_queued_prompt(obj)
    if obj.get("type") != "user" or any(obj.get(f) is True for f in
                                        ("isMeta", "isCompactSummary", "isVisibleInTranscriptOnly")):
        return None
    message = obj.get("message")
    content = message.get("content") if isinstance(message, dict) else None
    if isinstance(content, list) and any(isinstance(b, dict) and b.get("type") == "tool_result" for b in content):
        return None
    parts = _content_parts(content, ("image", "document"))
    if parts is None:
        return None
    texts, _, lead = parts
    head = lead.lstrip()
    if head.startswith("[Request interrupted by user"):
        return None
    if _starts_with_tag(head, CLAUDE_OUTPUT_TAGS):
        return {"kind": "commandOutput"}
    if _starts_with_tag(head, CLAUDE_IGNORED_TAGS):
        return None
    if head.startswith("<command-name>") or head.startswith("<command-message>"):
        return {"kind": "command"}
    return {"kind": "prompt", "continuation": is_continuation(_prompt_text(content, texts))}


CODEX_AGENTS = "# AGENTS.md instructions"


def codex_prompt(obj):
    """A `response_item` user message the user wrote; injected context (a text led by a tag, AGENTS.md) is not."""
    payload = obj.get("payload")
    if obj.get("type") != "response_item" or not isinstance(payload, dict) or payload.get("type") != "message" \
            or payload.get("role") != "user":
        return None
    content = payload.get("content")
    if isinstance(content, str):
        texts, media = [content], False
    elif isinstance(content, list):
        blocks = [b for b in content if isinstance(b, dict)]
        texts = [b["text"] for b in blocks if b.get("type") in ("input_text", "text") and isinstance(b.get("text"), str)]
        media = any(b.get("type") == "input_image" for b in blocks)
    else:
        return None
    if not media and all(not t.strip() for t in texts):
        return None
    lead = texts[0].lstrip() if texts else ""
    if (len(lead) > 1 and lead[0] == "<" and lead[1] in ASCII_LETTERS) or lead.startswith(CODEX_AGENTS):
        return None
    return {"kind": "prompt", "continuation": is_continuation(_prompt_text(content, texts))}


def pi_family_prompt(obj):
    message = obj.get("message")
    if obj.get("type") != "message" or not isinstance(message, dict) or message.get("role") != "user":
        return None
    parts = _content_parts(message.get("content"), ("image",))
    if parts is None:
        return None
    return {"kind": "prompt", "continuation": is_continuation("\n".join(parts[0]))}


def droid_prompt(obj):
    """A Factory Droid user message without `visibility` (the user's text, not the per-turn context or a notice) that
    holds no tool result and has a non-blank text or an image."""
    message = obj.get("message")
    if obj.get("type") != "message" or not isinstance(message, dict) or message.get("role") != "user" \
            or "visibility" in message and message["visibility"] is not None:
        return None
    content = message.get("content")
    if isinstance(content, list) and any(isinstance(b, dict) and b.get("type") == "tool_result" for b in content):
        return None
    parts = _content_parts(content, ("image",))
    if parts is None:
        return None
    return {"kind": "prompt", "continuation": is_continuation(_prompt_text(content, parts[0]))}


PROMPT_LINES = {"claudeCode": claude_prompt, "claudeDesktop": claude_prompt, "codex": codex_prompt,
                "omp": pi_family_prompt, "pi": pi_family_prompt, "openclaw": pi_family_prompt, "droid": droid_prompt}


def prompt_line(source, obj):
    """The prompt kind of one line that passed its source's copied-history rules: prompt, command, commandOutput or
    None (anything else). A prompt with `t` takes that time instead of the line's (a queued Claude message)."""
    extractor = PROMPT_LINES.get(source)
    return extractor(obj) if extractor and isinstance(obj, dict) else None


def segment(lines):
    """Prompt events from classified lines: `prompt`, `command`, `commandOutput` and `assistant` (with `duplicate`
    when 4.1 or 4.7 step 2 ignored it). A prompt or command takes effect at once and is withdrawn when the first
    assistant line after it is a duplicate; a command is also withdrawn by a command output before that line."""
    out, pending = [], None
    for line in lines:
        kind = line["line"]
        if kind in ("prompt", "command"):
            out.append({"t": line["t"], "type": "prompt",
                        "continuation": kind == "prompt" and bool(line.get("continuation"))})
            pending = kind
        elif kind == "commandOutput":
            if pending == "command":
                out.pop()
            pending = None
        elif kind == "assistant" and pending is not None:
            if line.get("duplicate"):
                out.pop()
            pending = None
    return out


def split_tasks(events):
    """Tasks of one agent's stream: a prompt starts one, a continuation joins the current one, and events before the
    first prompt form a task of their own."""
    tasks = []
    for e in events:
        if e["type"] == "prompt" and not (e.get("continuation") and tasks):
            tasks.append({"startMs": e["t"], "events": [e]})
        else:
            if not tasks:
                tasks.append({"startMs": e["t"], "events": []})
            tasks[-1]["events"].append(e)
    return tasks


def task_end(task):
    end = task["startMs"]
    for e in task["events"]:
        end = max(end, e["t"])
        outcome = e.get("outcome")
        if outcome and outcome.get("t") is not None:
            end = max(end, outcome["t"])
    return end


# ----------------------------------------------------------------------------- one task (6.9 Runs … Score)

def _is_int(v):
    return type(v) is int


class Act:
    __slots__ = ("index", "kind", "tool", "digest", "command", "outcome", "failures", "hunks", "weight", "result",
                 "errors", "fingerprints", "check", "block", "effective", "oscillating", "gain", "counted_index")

    def __init__(self, index, event, now):
        self.index = index
        self.kind = event.get("kind", "other")
        self.tool = event.get("tool")
        self.digest = event.get("digest")
        self.command = event.get("command") if self.kind == "command" else None
        outcome = event.get("outcome")
        if outcome is not None and now is not None and outcome.get("t") is not None and outcome["t"] > now:
            outcome = None
        self.outcome = outcome
        self.failures = (event.get("failures") or {}) if outcome is not None else {}
        self.hunks = event.get("hunks") if self.kind == "edit" else None
        verifying = self.command is not None and self.command.get("class") in VERIFY
        self.weight = 0 if self.kind == "other" else 2 if verifying else 1
        self.result = run_result(self) if verifying else None
        fingerprints = self.failures.get("fingerprints") or []
        self.fingerprints = frozenset(fingerprints)
        failed_count = self.failures.get("failedCount")
        self.errors = 0 if self.result != "fail" else max(1, len(fingerprints),
                                                          failed_count if _is_int(failed_count) else 0)
        self.check = check_id(self.command) if self.result is not None else None
        self.block = None
        self.effective = self.oscillating = False
        self.gain = 0
        self.counted_index = None

    @property
    def key(self):
        return self.tool, self.digest

    @property
    def targeted(self):
        return bool(self.command and self.command.get("targeted"))

    @property
    def suite(self):
        return self.command.get("suite") if self.command else None

    @property
    def run_class(self):
        return self.command.get("class") if self.command else None


def run_result(act):
    """pass, fail or None (not a run yet) for a test, build or check command (6.9 Runs and checks)."""
    if act.outcome is None:
        return None
    failed = act.outcome.get("failed")
    failed_count = act.failures.get("failedCount")
    passed_count = act.failures.get("passedCount")
    if failed is True or (_is_int(failed_count) and failed_count > 0):
        return "fail"
    if failed is False or (_is_int(failed_count) and failed_count == 0 and not (_is_int(passed_count) and
                                                                               passed_count == 0)):
        return "pass"
    return None


def check_id(command):
    if command.get("targeted") or command.get("suite") is None:
        return "key:" + command["key"]
    return "suite:" + command["suite"]


class FileEdits:
    """What a task's own edits say about one file (6.9 Oscillation)."""

    def __init__(self):
        self.written = []           # contents of whole-file writes, oldest first
        self.latest = None          # the file's content when known (after a write, before any hunk)
        self.current = set()        # texts the task's hunks produced
        self.superseded = set()     # texts the task's hunks replaced


def apply_edit(files, act):
    """Marks an edit effective and oscillating, updating the files' edit state (6.9 Effective edits)."""
    if act.outcome is not None and act.outcome.get("failed") is True:
        return
    hunks = act.hunks or []
    if not hunks:
        act.effective = True
        return
    changed = oscillating = False
    for h in hunks:
        state = files.setdefault(h.get("file"), FileEdits())
        old, new = h.get("old"), h.get("new")
        if old is None:
            if state.latest is not None and new == state.latest:
                continue
            changed = True
            if new in state.written:
                oscillating = True
            state.written.append(new)
            state.latest = new
            state.current.clear()
            state.superseded.clear()
        else:
            if old == new:
                continue
            changed = True
            if old in state.current and new in state.superseded:
                oscillating = True
            state.superseded.add(old)
            state.current.discard(old)
            state.current.add(new)
            state.latest = None
    act.effective, act.oscillating = changed, changed and oscillating


def loop_marks(keys):
    """Positions covered by a block of 1–3 keys repeated at least 3 times back to back, first block excluded."""
    n, marked = len(keys), [False] * len(keys)
    for i in range(n):
        for b in LOOP_BLOCKS:
            if i + LOOP_REPEATS * b > n:
                continue
            block, repeats = keys[i:i + b], 1
            while i + (repeats + 1) * b <= n and keys[i + repeats * b:i + (repeats + 1) * b] == block:
                repeats += 1
            if repeats >= LOOP_REPEATS:
                for p in range(i + b, i + repeats * b):
                    marked[p] = True
    return marked


def stall_ends(counted):
    """1-based counted indices that end a window of STALL_WINDOW actions with at most STALL_SHARE of new evidence."""
    ends = []
    for end in range(STALL_WINDOW, len(counted) + 1):
        window = counted[end - STALL_WINDOW:end]
        if seq_sum(a.weight * a.gain for a in window) <= STALL_SHARE * seq_sum(a.weight for a in window):
            ends.append(end)
    return ends


class Evaluation:
    """Everything 6.9 derives from one task's events at one instant."""

    def __init__(self, events, now=None):
        self.acts = []
        for e in events:
            if e["type"] != "action" or (now is not None and e["t"] > now):
                continue
            self.acts.append(Act(len(self.acts), e, now))
        self._edits_and_gain()
        self._runs()
        self._dimensions()

    # -- edits, information gain, loops, oscillation
    def _edits_and_gain(self):
        files, edits, last_seen, last_run = {}, 0, {}, {}
        self.counted = []
        for a in self.acts:
            a.block = edits
            if a.kind == "edit":
                apply_edit(files, a)
                a.gain = 1 if a.effective and not a.oscillating else 0
                if a.effective:
                    edits += 1
            elif a.result is not None:
                signature = (a.result, tuple(sorted(a.fingerprints)), a.failures.get("failedCount"))
                previous = last_run.get(a.command["key"])
                a.gain = 1 if previous is None or previous != (edits, signature) else 0
                last_run[a.command["key"]] = (edits, signature)
            elif a.weight > 0:
                a.gain = 1 if last_seen.get(a.key) != edits else 0
            if a.kind not in ("edit",) and a.weight > 0 and a.result is None:
                last_seen[a.key] = edits
            if a.weight > 0:
                a.counted_index = len(self.counted) + 1
                self.counted.append(a)
        self.edits = edits
        effective = [a for a in self.acts if a.effective]
        self.oscillations = seq_sum(1 for a in effective if a.oscillating)
        self.osc = self.oscillations / len(effective) if effective else 0.0
        total = seq_sum(a.weight for a in self.counted)
        self.ig = seq_sum(a.weight * a.gain for a in self.counted) / total if total else 0.0
        marks = loop_marks([a.key for a in self.counted])
        self.loop_actions = seq_sum(marks)
        self.loop = self.loop_actions / len(self.counted) if self.counted else 0.0
        self.stall_ends = stall_ends(self.counted)

    # -- runs, checks, episodes, state
    def _runs(self):
        self.runs = [a for a in self.acts if a.result is not None]
        own = {}
        for r in self.runs:
            own.setdefault(r.check, []).append(r)
        self.seq = {}
        for check, runs in own.items():
            first = runs[0]
            covered = list(runs)
            if first.targeted and first.suite is not None:
                covered += [r for r in self.runs if r.index > first.index and r.result == "pass" and not r.targeted
                            and r.suite == first.suite and r.check != check]
            self.seq[check] = sorted(covered, key=lambda r: r.index)
        self.episodes = []          # (check, [failing runs], recovered)
        self.next_in_seq = {}
        for check, seq in self.seq.items():
            current = None
            for i, r in enumerate(seq):
                if r.check == check and r.result == "fail":
                    self.next_in_seq[r.index] = seq[i + 1] if i + 1 < len(seq) else None
                if r.result == "fail":
                    if current is None:
                        current = (check, [], False)
                        self.episodes.append(current)
                    current[1].append(r)
                elif current is not None:
                    self.episodes[-1] = (check, current[1], True)
                    current = None
        blocks = {}
        for r in self.runs:
            blocks.setdefault(r.block, []).append(r)
        self.current_block = blocks.get(self.edits, [])
        self.latest_block = blocks[max(blocks)] if blocks else []
        if self.current_block:
            self.state = "failing" if self._block_fails(self.current_block) else "passing"
        elif self.runs:
            self.state = "failing" if self._block_fails(self.latest_block) else "pending"
        else:
            self.state = "unverified" if self.edits else "none"

    def _block_fails(self, block):
        members = {r.index for r in block}
        for check in {r.check for r in block}:
            inside = [r for r in self.seq[check] if r.index in members]
            if inside and inside[-1].result == "fail":
                return True
        return False

    # -- the five dimensions
    def _dimensions(self):
        block = self.current_block
        passing_tests = [r for r in block if r.run_class == "test" and r.result == "pass"]
        if self.state == "passing":
            self.V = V_TEST if passing_tests else V_BUILD
        else:
            self.V = 0.0
        self.C = self._convergence()
        if self.episodes:
            self.R = R_MAX * seq_sum(2 / (len(fails) + 1) if recovered else 0.0
                                 for _, fails, recovered in self.episodes) / len(self.episodes)
        else:
            self.R = R_MAX if any(r.result == "pass" for r in self.runs) else 0.0
        n = len(self.counted)
        self.E = E_MAX * min(1.0, n / RAMP) * self.ig * (1 - self.loop) * (1 - self.osc) if n else 0.0
        k = 0.0
        if any(r.run_class in ("build", "check") for r in block) or passing_tests:
            k += K_BUILD
        if any(r.run_class == "test" for r in block):
            k += K_TEST
        for r in passing_tests:
            if not r.targeted and not any(f.index > r.index and f.run_class == "test" and f.result == "fail"
                                          for f in block):
                k += K_FULL
                break
        self.K = k
        self.score = None if self.state == "none" else self.V + self.C + self.R + self.E + self.K

    def _convergence(self):
        if not self.runs:
            return 0.0
        weighted = total = 0.0
        for seq in self.seq.values():
            peak = max(r.errors for r in seq)
            if peak == 0:
                continue
            first_peak = next(i for i, r in enumerate(seq) if r.errors == peak)
            base = frozenset().union(*(r.fingerprints for r in seq[:first_peak + 1]))
            last = seq[-1]
            new = len(last.fingerprints - base) if last.result == "fail" else 0
            weighted += peak * max(0.0, 1 - (last.errors + new) / peak)
            total += peak
        return C_MAX if total == 0 else C_MAX * weighted / total

    # -- facts for records and predictions
    def counts(self):
        recovered = [fails for _, fails, ok in self.episodes if ok]
        fix_attempts = [r for r in self.runs if r.result == "fail"]
        fix_failures = seq_sum(1 for r in fix_attempts if fix_label(self.next_in_seq.get(r.index)))
        return {"actions": len(self.acts), "counted": len(self.counted), "edits": self.edits,
                "oscillations": self.oscillations, "loopActions": self.loop_actions, "runs": len(self.runs),
                "failingRuns": len(fix_attempts), "episodes": len(self.episodes), "recovered": len(recovered),
                "recoverAttempts": seq_sum(len(f) for f in recovered), "fixAttempts": len(fix_attempts),
                "fixFailures": fix_failures}

    def dims(self):
        return {"V": self.V, "C": self.C, "R": self.R, "E": self.E, "K": self.K}

    def first_pass_ms(self):
        times = [r.outcome["t"] for r in self.runs if r.result == "pass" and r.outcome.get("t") is not None]
        return min(times) if times else None

    def open_failure(self):
        """The newest failing run that is still the last element of its check (6.11 Predictions)."""
        open_runs = [seq[-1] for check, seq in self.seq.items() if seq[-1].result == "fail" and seq[-1].check == check]
        return max(open_runs, key=lambda r: r.index) if open_runs else None

    def held_v(self):
        """While pending: the V the latest verified block would give without the edit after it."""
        if self.state != "pending":
            return None
        return V_TEST if any(r.run_class == "test" and r.result == "pass" for r in self.latest_block) else V_BUILD

    def open_failure_facts(self):
        """While failing: the open failure's check, its error count against the check's peak, and whether an
        effective edit followed it."""
        if self.state != "failing":
            return None
        run = self.open_failure()
        failed_count = run.failures.get("failedCount")
        return {"suite": run.suite, "class": run.run_class, "key": run.command["key"], "errors": run.errors,
                "peak": max(r.errors for r in self.seq[run.check]),
                "counted": bool(run.fingerprints) or (_is_int(failed_count) and failed_count > 0),
                "edited": run.block < self.edits}

    def last_pass_facts(self):
        """While passing or pending: the run that passed last and whether its block passed build or check runs only."""
        if self.state not in ("passing", "pending"):
            return None
        block = self.current_block if self.state == "passing" else self.latest_block
        run = block[-1]
        return {"suite": run.suite, "class": run.run_class, "key": run.command["key"],
                "buildOnly": not any(r.run_class == "test" and r.result == "pass" for r in block)}


def fix_label(next_run):
    return 1 if next_run is None or next_run.result == "fail" else 0


def round_half_away(value):
    return int(value + 0.5) if value >= 0 else -int(-value + 0.5)


def evaluate(events, now=None):
    return Evaluation(events, now)


def current_task(events, now=None):
    visible = [e for e in events if now is None or e["t"] <= now]
    tasks = split_tasks(visible)
    return tasks[-1] if tasks else None


def task_result(ev):
    return {"state": ev.state, "score": ev.score, "shown": None if ev.score is None else round_half_away(ev.score),
            "dims": ev.dims(), "ig": ev.ig, "loop": ev.loop, "osc": ev.osc, "counts": ev.counts(),
            "firstPassMs": ev.first_pass_ms(), "stallEnds": ev.stall_ends, "vHeld": ev.held_v(),
            "openFailure": ev.open_failure_facts(), "lastPass": ev.last_pass_facts()}


# ----------------------------------------------------------------------------- values (6.9 Agent, group and set values)

def agent_value(agent, now):
    """live, resting or none for one agent: its current task's score, by its phase (after 6.7 staleness)."""
    task = current_task(agent.get("events", []), now)
    ev = evaluate(task["events"], now) if task else None
    score = ev.score if ev else None
    if score is None:
        return {"form": "none", "score": None, "shown": None, "state": ev.state if ev else None}
    form = "resting" if agent["phase"] == "idle" else "live"
    return {"form": form, "score": score, "shown": round_half_away(score), "state": ev.state}


def highest(agents, values):
    """The highest live value, else the highest resting value, else none; `agent` breaks ties by latest activity,
    then id (ordinal)."""
    for form in ("live", "resting"):
        candidates = [a for a in agents if values[a["id"]]["form"] == form]
        if candidates:
            best = max(values[a["id"]]["score"] for a in candidates)
            ties = [a for a in candidates if values[a["id"]]["score"] == best]
            ties.sort(key=lambda a: (-a.get("lastLineMs", 0), _ordinal(a["id"])))
            return {"form": form, "score": best, "shown": round_half_away(best), "agent": ties[0]["id"]}
    return {"form": "none", "score": None, "shown": None, "agent": None}


def _ordinal(text):
    return text.encode("utf-16-be")


# ----------------------------------------------------------------------------- predictions (6.11)

def sigmoid(eta):
    eta = max(-ETA_LIMIT, min(ETA_LIMIT, eta))
    return 1.0 / (1.0 + math.exp(-eta))


def size_feature(n):
    return min(1.0, math.log2(1 + n) / 9)


def snapshot_features(ev):
    return {"passing": 1.0 if ev.state == "passing" else 0.0, "failing": 1.0 if ev.state == "failing" else 0.0,
            "conv": ev.C / C_MAX, "ig": ev.ig, "loop": ev.loop, "osc": ev.osc, "size": size_feature(len(ev.counted))}


def fix_features(events, run_event_index, now=None):
    """Features of the fix that follows one failing run, as they stand right after that run."""
    ev = evaluate(events[:run_event_index + 1], now)
    run = ev.acts[-1]
    seq = ev.seq[run.check]
    episode = next(fails for check, fails, _ in ev.episodes if check == run.check and run in fails)
    attempts = episode.index(run) + 1
    peak = max(r.errors for r in seq)
    return {"attempts": min(1.0, (attempts - 1) / 4), "errors": min(1.0, math.log2(1 + run.errors) / 8),
            "trend": run.errors / peak, "osc": ev.osc, "ig": ev.ig}


def linear(coefficients, features, names):
    return coefficients[0] + seq_sum(c * features[name] for c, name in zip(coefficients[1:], names))


def predictions(events, now, models):
    """P(success), P(stall) and P(fix fails) of an agent's current task; None while it has no score."""
    task = current_task(events, now)
    if task is None:
        return None
    ev = evaluate(task["events"], now)
    if ev.score is None:
        return None
    features = snapshot_features(ev)
    out = {"success": sigmoid(linear(models["success"]["coefficients"], features, SUCCESS_FEATURES)),
           "stall": sigmoid(linear(models["stall"]["coefficients"], features, STALL_FEATURES)), "fix": None,
           "features": features, "fixFeatures": None}
    open_run = ev.open_failure() if ev.state == "failing" else None
    if open_run is not None:
        action_events = [i for i, e in enumerate(task["events"]) if e["type"] == "action" and
                         (now is None or e["t"] <= now)]
        fixf = fix_features(task["events"], action_events[open_run.index], now)
        out["fix"] = sigmoid(linear(models["fix"]["coefficients"], fixf, FIX_FEATURES))
        out["fixFeatures"] = fixf
    for name in ("success", "stall", "fix"):
        out[name + "Shown"] = None if out[name] is None else round_half_away(out[name] * 100)
    return out


def prior_models():
    return {name: {"coefficients": list(PRIORS[name]), "calibrated": False, "tasks": 0} for name in PRIORS}


# ----------------------------------------------------------------------------- task records (6.11 History)

def task_records(events, analysis_ms, last_line_ms, models=()):
    """The records of one agent's complete stream (history analysis): every task with at least one action."""
    tasks = split_tasks(events)
    records = []
    for i, task in enumerate(tasks):
        evs = task["events"]
        if not any(e["type"] == "action" for e in evs):
            continue
        ev = evaluate(evs)
        end = task_end(task)
        model = effort = None
        for t, m, eff in models:
            if t <= end:
                model, effort = m, eff
        success = ev.state == "passing"
        snapshots = []
        counted_events = [j for j, e in enumerate(evs) if e["type"] == "action" and e.get("kind", "other") != "other"]
        for n in SNAPSHOTS:
            if n > len(counted_events):
                break
            prefix = evaluate(evs[:counted_events[n - 1] + 1])
            if prefix.state == "none":
                continue
            f = snapshot_features(prefix)
            snapshots.append([n] + [f[name] for name in SNAPSHOT_FIELDS[1:-1]] +
                             [1 if any(end_index > n for end_index in ev.stall_ends) else 0])
        fix_rows = []
        action_events = [j for j, e in enumerate(evs) if e["type"] == "action"]
        for run in ev.runs:
            if run.result != "fail":
                continue
            f = fix_features(evs, action_events[run.index])
            fix_rows.append([f[name] for name in FIX_FIELDS[:-1]] + [fix_label(ev.next_in_seq.get(run.index))])
        records.append({
            "ordinal": len(records), "startMs": task["startMs"], "endMs": end,
            "complete": i < len(tasks) - 1 or analysis_ms - last_line_ms >= COMPLETE_IDLE_MS,
            "model": model, "effort": effort, "state": ev.state, "score": ev.score, "dims": ev.dims(),
            "counts": ev.counts(), "firstPassMs": ev.first_pass_ms(), "success": success,
            "stalled": bool(ev.stall_ends), "snapshots": snapshots, "fixRows": fix_rows})
    return records


# ----------------------------------------------------------------------------- calibration (6.11)

def calibration_order(record):
    return record["endMs"], record["startMs"], _ordinal(record["agentId"]), record["ordinal"]


def model_rows(records, name):
    """(features, label, weight) rows of one model over records already in calibration order, and the task count."""
    rows, tasks = [], 0
    for rec in records:
        if name == "fix":
            task_rows = [(dict(zip(FIX_FIELDS, r)), r[-1]) for r in rec["fixRows"]]
        else:
            task_rows = [(dict(zip(SNAPSHOT_FIELDS, r)), (1 if rec["success"] else 0) if name == "success" else r[-1])
                         for r in rec["snapshots"]]
        if not task_rows:
            continue
        tasks += 1
        weight = 1.0 / len(task_rows)
        rows.extend((f, y, weight) for f, y in task_rows)
    return rows, tasks


def cholesky_solve(matrix, vector):
    d = len(vector)
    lower = [[0.0] * d for _ in range(d)]
    for j in range(d):
        s = matrix[j][j] - seq_sum(lower[j][k] * lower[j][k] for k in range(j))
        lower[j][j] = math.sqrt(s)
        for i in range(j + 1, d):
            lower[i][j] = (matrix[i][j] - seq_sum(lower[i][k] * lower[j][k] for k in range(j))) / lower[j][j]
    z = [0.0] * d
    for i in range(d):
        z[i] = (vector[i] - seq_sum(lower[i][k] * z[k] for k in range(i))) / lower[i][i]
    x = [0.0] * d
    for i in reversed(range(d)):
        x[i] = (z[i] - seq_sum(lower[k][i] * x[k] for k in range(i + 1, d))) / lower[i][i]
    return x


def gradient_and_hessian(beta, prior, rows, names):
    d = len(beta)
    gradient = [L2 * (beta[k] - prior[k]) for k in range(d)]
    hessian = [[L2 if i == j else 0.0 for j in range(d)] for i in range(d)]
    for features, y, weight in rows:
        x = [1.0] + [features[name] for name in names]
        p = sigmoid(seq_sum(b * v for b, v in zip(beta, x)))
        for i in range(d):
            gradient[i] += weight * (p - y) * x[i]
            for j in range(d):
                hessian[i][j] += weight * p * (1 - p) * x[i] * x[j]
    return gradient, hessian


def fit(rows, names, prior):
    beta = list(prior)
    for _ in range(ITERATIONS):
        gradient, hessian = gradient_and_hessian(beta, prior, rows, names)
        step = cholesky_solve(hessian, gradient)
        beta = [b - s for b, s in zip(beta, step)]
    return beta


def calibrate(records):
    """Per model: the source's coefficients from its latest complete records, or the prior."""
    chosen = sorted((r for r in records if r["complete"]), key=calibration_order)[-MAX_TASKS:]
    models = {}
    for name, names in MODEL_FEATURES.items():
        rows, tasks = model_rows(chosen, name)
        positive = seq_sum(w for _, y, w in rows if y == 1)
        negative = seq_sum(w for _, y, w in rows if y == 0)
        if tasks >= MIN_TASKS and positive >= MIN_CLASS_WEIGHT and negative >= MIN_CLASS_WEIGHT:
            models[name] = {"coefficients": fit(rows, names, PRIORS[name]), "calibrated": True, "tasks": tasks}
        else:
            models[name] = {"coefficients": list(PRIORS[name]), "calibrated": False, "tasks": tasks}
    return models


# ----------------------------------------------------------------------------- comparison (6.11)

HISTOGRAM_EDGES = (10, 20, 30, 40, 50, 60, 70, 80, 90)


def quantile(values, p):
    """Linear interpolation between order statistics, h = (n − 1)·p; None for no values."""
    values = sorted(values)
    if not values:
        return None
    h = (len(values) - 1) * p
    i = math.floor(h)
    fraction = h - i
    return values[i] if fraction == 0 else values[i] + fraction * (values[i + 1] - values[i])


def histogram(scores):
    """Ten bins [0, 10) … [80, 90) and [90, 100]: a score's bin is the number of edges it reaches."""
    bins = [0] * (len(HISTOGRAM_EDGES) + 1)
    for score in scores:
        bins[seq_sum(1 for edge in HISTOGRAM_EDGES if score >= edge)] += 1
    return bins


def aggregate(records):
    scored = [r for r in records if r["score"] is not None]
    scores = [r["score"] for r in scored]
    fix_attempts = seq_sum(r["counts"]["fixAttempts"] for r in records)
    recovered = seq_sum(r["counts"]["recovered"] for r in records)
    first_pass = [r["firstPassMs"] - r["startMs"] for r in records if r["firstPassMs"] is not None]
    med = quantile(scores, 0.5)
    successes = seq_sum(1 for r in scored if r["success"])
    stalls = seq_sum(1 for r in records if r["stalled"])
    fix_failures = seq_sum(r["counts"]["fixFailures"] for r in records)
    return {
        "tasks": len(records), "scoredTasks": len(scored), "medianScore": med,
        "medianShown": None if med is None else round_half_away(med),
        "lowerQuartile": quantile(scores, 0.25), "upperQuartile": quantile(scores, 0.75),
        "histogram": histogram(scores),
        "successes": successes, "stalls": stalls, "fixFailures": fix_failures, "fixAttempts": fix_attempts,
        "recovered": recovered,
        "successRate": successes / len(scored) if scored else None,
        "stallRate": stalls / len(records) if records else None,
        "fixFailureRate": fix_failures / fix_attempts if fix_attempts else None,
        "recoverAttempts": seq_sum(r["counts"]["recoverAttempts"] for r in records) / recovered if recovered else None,
        "timeToFirstPassMs": quantile(first_pass, 0.5),
    }


def compare(records, first, last, source=None):
    """6.11 Comparison over [first, last], for every source or for the one the window's picker chose."""
    chosen = [r for r in records if r["complete"] and first <= r["day"] <= last and source in (None, r["source"])]
    by_source = []
    for source in SOURCE_ORDER:
        part = [r for r in chosen if r["source"] == source]
        if part:
            by_source.append(dict(source=source, **aggregate(part)))
    models = {}
    for r in chosen:
        models.setdefault(r["model"] or "", []).append(r)
    by_model = [dict(model=m, sources=[s for s in SOURCE_ORDER if any(r["source"] == s for r in part)],
                     **aggregate(part)) for m, part in models.items()]
    by_model.sort(key=lambda row: (-row["tasks"], row["model"].encode("utf-8")))
    return {"bySource": by_source, "byModel": by_model}


# ----------------------------------------------------------------------------- fixtures

def compute(cases):
    """The expected outputs of every case (inputs as in task_score_cases.py)."""
    out = {}
    out["continuations"] = [dict(c, expect=is_continuation(c["text"])) for c in cases["continuations"]]
    out["promptLines"] = [dict(c, expect=prompt_line(c["source"], c["line"])) for c in cases["promptLines"]]
    out["segmentation"] = [dict(c, expect=segment(c["lines"])) for c in cases["segmentation"]]
    out["tasks"] = []
    for case in cases["tasks"]:
        queries = []
        for q in case["queries"]:
            task = current_task(case["events"], q.get("nowMs"))
            queries.append(dict(q, expect=task_result(evaluate(task["events"], q.get("nowMs")))))
        out["tasks"].append(dict(case, queries=queries))
    out["values"] = []
    for case in cases["values"]:
        now = case["nowMs"]
        values = {a["id"]: agent_value(a, now) for a in case["agents"]}
        groups = thinking.board_groups(case["agents"])
        by_id = {a["id"]: a for a in case["agents"]}
        expect = {"agents": values,
                  "groups": {root: dict(members=members, **highest([by_id[m] for m in members], values))
                             for root, members in groups.items()},
                  "all": highest(case["agents"], values)}
        out["values"].append(dict(case, expect=expect))
    out["predictions"] = []
    for case in cases["predictions"]:
        models = case.get("models") or prior_models()
        got = predictions(case["events"], case.get("nowMs"), models)
        out["predictions"].append(dict(case, expect=got))
    out["records"] = []
    for case in cases["records"]:
        got = task_records(case["events"], case["analysisMs"], case["lastLineMs"], case.get("models", ()))
        out["records"].append(dict(case, expect=got))
    out["calibration"] = []
    for case in cases["calibration"]:
        expect = {}
        for source in sorted(case["records"]):
            models = calibrate(case["records"][source])
            probes = []
            for probe in case.get("probes", []):
                name = probe["model"]
                p = sigmoid(linear(models[name]["coefficients"], probe["features"], MODEL_FEATURES[name]))
                probes.append({"model": name, "p": p, "shown": round_half_away(p * 100)})
            expect[source] = {"models": models, "probes": probes}
        out["calibration"].append(dict(case, expect=expect))
    out["comparison"] = [dict(c, expect=compare(c["records"], c["first"], c["last"], c.get("source")))
                         for c in cases["comparison"]]
    out["thinking"] = compute_thinking(cases["thinking"])
    return out


def compute_thinking(cases):
    out = {"windows": [], "agentWindows": [], "groupWindows": [], "modelWindows": []}
    for case in cases["windows"]:
        queries = []
        for q in case["queries"]:
            subset = set(q.get("sources") or [r["source"] for r in case["responses"]] +
                         [a["source"] for a in case["agents"]])
            responses = [r for r in case["responses"] if r["source"] in subset]
            agents = [a for a in case["agents"] if a["source"] in subset]
            queries.append(dict(q, expect=thinking.window_sums(responses, agents, q["nowMs"])[0].result()))
        out["windows"].append(dict(case, queries=queries))
    for case in cases["agentWindows"]:
        queries = []
        for q in case["queries"]:
            total, per_agent = thinking.window_sums(case["responses"], case["agents"], q["nowMs"])
            agents = {a["id"]: per_agent.get(a["id"], thinking.Window()).result() for a in case["agents"]}
            queries.append(dict(q, expect=total.result(), agents=agents))
        out["agentWindows"].append(dict(case, queries=queries))
    for case in cases["groupWindows"]:
        queries = []
        groups = thinking.board_groups(case["agents"])
        for q in case["queries"]:
            total, per_agent = thinking.window_sums(case["responses"], case["agents"], q["nowMs"])
            got = {root: dict(members=members, **thinking.group_window(per_agent, members).result())
                   for root, members in groups.items()}
            queries.append(dict(q, expect=total.result(), groups=got))
        out["groupWindows"].append(dict(case, queries=queries))
    for case in cases.get("modelWindows", []):
        queries = []
        for q in case["queries"]:
            total, _ = thinking.window_sums(case["responses"], case["agents"], q["nowMs"])
            models = {model: window.result()
                      for model, window in thinking.model_sums(case["responses"], case["agents"], q["nowMs"]).items()}
            queries.append(dict(q, expect=total.result(), models=models))
        out["modelWindows"].append(dict(case, queries=queries))
    return out


def close(a, b, tolerance=1e-9):
    """Structural equality with numbers compared within an absolute tolerance."""
    if isinstance(a, bool) or isinstance(b, bool) or a is None or b is None or isinstance(a, str):
        return a == b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return abs(a - b) <= tolerance
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(close(a[k], b[k], tolerance) for k in a)
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(close(x, y, tolerance) for x, y in zip(a, b))
    return a == b


def stationarity(case):
    """The fitted coefficients make the penalised log-likelihood's gradient vanish (an independent optimality check)."""
    failures = []
    for source, recs in case["records"].items():
        chosen = sorted((r for r in recs if r["complete"]), key=calibration_order)[-MAX_TASKS:]
        for name, model in case["expect"][source]["models"].items():
            if not model["calibrated"]:
                continue
            rows, _ = model_rows(chosen, name)
            gradient, _ = gradient_and_hessian(model["coefficients"], PRIORS[name], rows, MODEL_FEATURES[name])
            if max(abs(g) for g in gradient) > 1e-9:
                failures.append("calibration %s %s %s: gradient %r does not vanish" % (case["name"], source, name,
                                                                                       gradient))
    return failures


def counts(data):
    return {
        "continuations": len(data["continuations"]), "promptLines": len(data["promptLines"]),
        "segmentation": len(data["segmentation"]), "taskQueries": seq_sum(len(c["queries"]) for c in data["tasks"]),
        "values": len(data["values"]), "predictions": len(data["predictions"]), "records": len(data["records"]),
        "calibration": len(data["calibration"]), "comparison": len(data["comparison"]),
        "thinkingQueries": seq_sum(len(c["queries"]) for k in ("windows", "agentWindows", "groupWindows",
                                                                "modelWindows")
                               for c in data["thinking"][k]),
    }


def hand_check(computed, cases):
    """Every expectation written by hand in task_score_cases.py (`want`) holds in the computed output."""
    failures = []

    def subset(want, got, label):
        if isinstance(want, dict):
            if not isinstance(got, dict):
                failures.append("%s: expected %r got %r" % (label, want, got))
                return
            for k, v in want.items():
                if k not in got:
                    failures.append("%s: missing %s" % (label, k))
                else:
                    subset(v, got[k], label + "." + k)
        elif isinstance(want, list) and isinstance(got, list) and len(want) == len(got) and any(
                isinstance(w, dict) for w in want):
            for i, (w, g) in enumerate(zip(want, got)):
                subset(w, g, "%s[%d]" % (label, i))
        elif not close(want, got, 1e-9):
            failures.append("%s: expected %r got %r" % (label, want, got))

    for section, items in cases.items():
        if section in ("thinking",) or not isinstance(items, list):
            continue
        for i, case in enumerate(items):
            got = computed[section][i]
            if "want" in case:
                subset(case["want"], got["expect"], "%s %s" % (section, case.get("name", case.get("text", i))))
            for j, q in enumerate(case.get("queries", [])):
                if "want" in q:
                    subset(q["want"], got["queries"][j]["expect"], "%s %s #%d" % (section, case["name"], j))
    for section, items in cases["thinking"].items():
        for i, case in enumerate(items):
            for j, q in enumerate(case["queries"]):
                if "want" in q:
                    subset(q["want"], computed["thinking"][section][i]["queries"][j],
                           "thinking %s %s #%d" % (section, case["name"], j))
    return failures


def strip_want(value):
    if isinstance(value, dict):
        return {k: strip_want(v) for k, v in value.items() if k != "want"}
    if isinstance(value, list):
        return [strip_want(v) for v in value]
    return value


def main():
    if "--write" in sys.argv:
        import task_score_cases
        cases = task_score_cases.cases()
        computed = compute(strip_want(cases))
        failures = hand_check(computed, cases)
        for case in computed["calibration"]:
            failures.extend(stationarity(case))
        if failures:
            print("\n".join(failures))
            return 1
        data = dict({"_comment": task_score_cases.COMMENT}, **computed)
        with open(FIXTURE, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=1, ensure_ascii=False)
            f.write("\n")
        print("wrote", FIXTURE, counts(data))
        return 0
    with open(FIXTURE, encoding="utf-8") as f:
        data = json.load(f)
    inputs = strip_expect(data)
    computed = compute(inputs)
    failures = []
    for section in computed:
        if not close(computed[section], data[section]):
            failures.append("section %s differs from the reference" % section)
            failures.extend(diff(computed[section], data[section], section))
    for case in data["calibration"]:
        failures.extend(stationarity(case))
    if failures:
        print("\n".join(failures[:40]))
        return 1
    print("task score reference matches %s" % ", ".join("%d %s" % (v, k) for k, v in counts(data).items()))
    return 0


EXPECT_KEYS = ("expect", "agents", "groups", "models")


def strip_expect(data):
    """The fixture's inputs: everything but the computed fields."""
    out = {}
    for section, items in data.items():
        if section == "_comment":
            continue
        if section == "thinking":
            out[section] = {k: [dict(c, queries=[{qk: qv for qk, qv in q.items() if qk not in EXPECT_KEYS}
                                                  for q in c["queries"]]) for c in v] for k, v in items.items()}
            continue
        cleaned = []
        for case in items:
            case = {k: v for k, v in case.items() if k != "expect"}
            if "queries" in case:
                case["queries"] = [{k: v for k, v in q.items() if k != "expect"} for q in case["queries"]]
            cleaned.append(case)
        out[section] = cleaned
    return out


def diff(got, want, label):
    if isinstance(got, dict) and isinstance(want, dict):
        lines = []
        for k in sorted(set(got) | set(want), key=str):
            if k not in got or k not in want:
                lines.append("%s.%s: present on one side only" % (label, k))
            elif not close(got[k], want[k]):
                lines.extend(diff(got[k], want[k], "%s.%s" % (label, k)))
        return lines
    if isinstance(got, list) and isinstance(want, list) and len(got) == len(want):
        lines = []
        for i, (g, w) in enumerate(zip(got, want)):
            if not close(g, w):
                lines.extend(diff(g, w, "%s[%d]" % (label, i)))
        return lines
    return ["%s: fixture %r, reference %r" % (label, want, got)]


if __name__ == "__main__":
    sys.exit(main())
