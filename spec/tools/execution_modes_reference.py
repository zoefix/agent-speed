#!/usr/bin/env python3
"""Independent execution-mode normalization and report-extension checks."""

from __future__ import annotations

import json
import re
from collections import defaultdict
from pathlib import Path

KINDS = ("serviceTier", "accelerator", "modelVariant", "routing", "workflow")
# The kinds each version of the report extension carries: version 2 adds the reasoning effort. `speed` is a derived
# breakdown and stays local (its facts are the serviceTier, accelerator and modelVariant rows).
REPORT_KINDS = {1: KINDS, 2: (*KINDS, "effort")}
LOCAL_KINDS = ("speed",)
VERSIONS = {"executionModes": (1, 2)}
MAX_ROWS = 64
EFFORT_UNRESOLVED = ("auto", "default", "inherit", "inherited", "unknown", "thinking_level_unspecified")
EFFORT_ALIASES = {"none": "off", "disabled": "off", "enabled": "on"}
EFFORT_LEVELS = {"off": 0, "minimal": 1, "low": 1, "medium": 2, "high": 3, "xhigh": 4, "max": 5, "ultra": 5,
                 "ultracode": 5}
EFFORT_TOP = ("ultra", "ultracode")
EFFORT_ORDER = ("ultracode", "ultra", "max", "xhigh", "high", "medium", "low", "minimal", "off")
SPEED_BOLTS = {"fast": 1, "highspeed": 1, "ultrafast": 2}
SPEED_ORDER = ("ultrafast", "fast", "highspeed", "standard")
UNKNOWN = {"mode": "unknown", "evidence": "unknown"}
EVIDENCE = ("configured", "selected", "recorded", "served")
SCOPES = ("configuration", "session", "turn", "request", "response")
FIELDS = ("requests", "output", "measuredOutput", "measuredMs")
EXTENSIONS = ("modelHarnessRequests", "executionModes", "profileAvatar", "sharedIdentity", "taskScore",
              "perfScore")
MAX_INTEGER = 9007199254740991
TOKEN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]*$", re.ASCII)
GOOGLE_TIERS = {"priority": "fast"}
# Factory's catalog entries that change the request's tier (EXECUTION-MODES, Factory Droid). `glm-5.2-fast` is a model
# of its own and `auto-fast` a router: neither is listed.
DROID_TIERS = {
    **{m: ("fast", "anthropic") for m in ("claude-opus-4-6-fast", "claude-opus-4-7-fast", "claude-opus-4-8-fast",
                                          "claude-opus-5-fast", "claude-opus-5-5-fast")},
    **{m: ("fast", "openai") for m in ("gpt-5.3-codex-fast", "gpt-5.4-fast", "gpt-5.4-mini-fast", "gpt-5.5-fast",
                                       "gpt-5.6-fast", "gpt-5.6-sol-fast", "gpt-6-sol-fast", "gpt-6-luna-fast")},
    **{m: ("ultrafast", "openai") for m in ("gpt-6-astra-ultrafast", "gpt-6.1-sol-ultrafast")},
    **{m: ("flex", "openai") for m in ("gpt-5.6-terra-flex", "gpt-5.6-luna-flex", "gpt-6-sol-flex", "gpt-6-astra-flex",
                                       "gpt-6-luna-flex")},
}
ZCODE_HIGHSPEED_MODELS = ("GLM-5.1-Highspeed", "MiniMax-M2.7-highspeed", "MiniMax-M2.5-highspeed",
                          "kimi-k2.7-code-highspeed", "kimi-for-coding-highspeed", "kimi-k3.1-pro-highspeed",
                          "k3.1-pro-highspeed")
ZCODE_HIGHSPEED_PROVIDERS = ("account:zai-highspeed-card", "account:bigmodel-highspeed-card")
HERMES_EFFORTS = ("minimal", "low", "medium", "high", "xhigh", "max", "ultra")
HERMES_OFF = ("none", "false", "disabled")
HERMES_TIERS = {"fast": "fast", "priority": "fast", "ultrafast": "ultrafast", "ultra": "ultrafast",
                "normal": "standard", "default": "standard", "standard": "standard", "off": "standard",
                "none": "standard"}
OMP_FAMILIES = {"anthropic": "anthropic", "openai": "openai", "openai-codex": "openai", "azure": "openai",
                "azure-openai-responses": "openai", "google": "google", "google-gemini-cli": "google",
                "google-antigravity": "google", "google-vertex": "google"}
AG_EFFORT_SUFFIXES = ("-extra-low", "-low", "-medium", "-high", "-max")
AG_LABEL = re.compile(r".+ \((Low|Medium|High|Max)\)", re.S)


def identifier(value, limit=64):
    return isinstance(value, str) and 0 < len(value) <= limit and TOKEN.fullmatch(value) is not None


def version(extensions, name, *, models_enabled=True):
    """The negotiated version of an extension: an exact JSON integer the client knows, else 0 (not active)."""
    if not models_enabled or not isinstance(extensions, dict):
        return 0
    value = extensions.get(name)
    return value if type(value) is int and value in VERSIONS.get(name, (1,)) else 0


def supported(extensions, name, *, models_enabled=True):
    return version(extensions, name, models_enabled=models_enabled) > 0


def normalize(adapter, raw, provider=None, evidence="served", scope="response"):
    if not identifier(raw) or (provider is not None and not identifier(provider, 32)):
        return None
    if evidence not in EVIDENCE or scope not in SCOPES:
        return None
    token = raw.lower()
    namespace = provider.lower() if provider else None
    if adapter == "serviceTier":
        if namespace == "anthropic" or (namespace == "google" and token == "unspecified"):
            return None
        kind = "serviceTier"
        value = {"default": "standard", "priority": "fast"}.get(token, token) if namespace == "openai" \
            else GOOGLE_TIERS.get(token, token) if namespace == "google" else token
    elif adapter == "claudeSpeed":
        kind, value = "serviceTier", token
    elif adapter == "kimiModel":
        if raw not in ("k3.1-pro-highspeed", "kimi-for-coding-highspeed"):
            return None
        kind, value = "modelVariant", "highspeed"
    elif adapter == "zcodeModel":
        if raw not in ZCODE_HIGHSPEED_MODELS:
            return None
        kind, value = "modelVariant", "highspeed"
    elif adapter == "zcodeProvider":
        if raw not in ZCODE_HIGHSPEED_PROVIDERS:
            return None
        kind, value = "accelerator", "highspeed"
    elif adapter == "droidModel":
        if raw not in DROID_TIERS:
            return None
        kind, (value, namespace) = "serviceTier", DROID_TIERS[raw]
    elif adapter == "ompTier":
        family = OMP_FAMILIES.get(namespace)
        if family is None:
            return None
        namespace, kind = family, "serviceTier"
        value = {"default": "standard", "priority": "fast"}.get(token, token) if family == "openai" \
            else "fast" if token == "priority" else token
    elif adapter == "hermesEffort":
        if token not in (*HERMES_OFF, *HERMES_EFFORTS):
            return None
        kind, value = "effort", "off" if token in HERMES_OFF else token
    elif adapter == "hermesServiceTier":
        if token not in HERMES_TIERS:
            return None
        kind, value = "serviceTier", HERMES_TIERS[token]
    elif adapter == "routing":
        slots = {"fast-model": "fast", "balanced-model": "balanced", "deep-model": "deep", "auto": "auto"}
        if raw not in slots:
            return None
        kind, value = "routing", slots[raw]
    elif adapter == "workflow":
        if token not in ("fast", "planning"):
            return None
        kind, value = "workflow", token
    elif adapter == "effort":
        if token in EFFORT_UNRESOLVED:
            return None
        kind, value = "effort", EFFORT_ALIASES.get(token, token)
    else:
        return None
    result = {"kind": kind, "value": value, "rawValue": raw, "evidence": evidence, "scope": scope}
    if namespace:
        result["provider"] = namespace
    return result


def attribution(facts, kind, evidence=EVIDENCE[1:]):
    eligible = [(index, fact) for index, fact in enumerate(facts)
                if fact.get("kind") == kind and fact.get("evidence") in evidence]
    if not eligible:
        return {"mode": "unknown", "evidence": "unknown"}
    _, chosen = max(eligible, key=lambda pair: (EVIDENCE.index(pair[1]["evidence"]),
                                               SCOPES.index(pair[1].get("scope", "request")), pair[0]))
    tied = [fact for _, fact in eligible if (fact["evidence"], fact.get("scope", "request"))
            == (chosen["evidence"], chosen.get("scope", "request"))]
    if chosen["value"] in ("auto", "inherit", "inherited", "unknown", "none") or any(
            (fact["value"], fact.get("provider")) != (chosen["value"], chosen.get("provider")) for fact in tied):
        return {"mode": "unknown", "evidence": "unknown"}
    result = {"mode": chosen["value"], "evidence": chosen["evidence"]}
    if chosen.get("provider"):
        result["provider"] = chosen["provider"]
    return result


def speed_attribution(facts, evidence=EVIDENCE[1:]):
    """The derived `speed` breakdown: service tier, else accelerator, else a highspeed model variant."""
    for kind in ("serviceTier", "accelerator", "modelVariant"):
        result = attribution(facts, kind, evidence)
        if result["mode"] != "unknown" and (kind != "modelVariant" or result["mode"] == "highspeed"):
            return result
    return dict(UNKNOWN)


def local_attribution(facts, kind, evidence=EVIDENCE[1:]):
    return speed_attribution(facts, evidence) if kind == "speed" else attribution(facts, kind, evidence)


def current_attribution(kind, facts, session_facts=(), configured_facts=()):
    """An agent's current effort or speed: its own facts, then a session-current fact, then a configured value."""
    for candidates, evidence in ((facts, EVIDENCE[1:]), (session_facts, ("selected",)),
                                 (configured_facts, ("configured",))):
        result = local_attribution(list(candidates), kind, evidence)
        if result["mode"] != "unknown":
            return result
    return dict(UNKNOWN)


def ledger_effort(frozen, current):
    """The effort the report ledger freezes when it first counts a live response (EXECUTION-MODES, Current values)."""
    if frozen["mode"] != "unknown" or current["evidence"] not in EVIDENCE[1:]:
        return frozen
    return {"mode": current["mode"], "evidence": current["evidence"]}


def antigravity_model_effort(model):
    """(base model, effort token) of a catalog id that ends in exactly one effort suffix, else None."""
    for suffix in AG_EFFORT_SUFFIXES:
        if model.endswith(suffix) and len(model) > len(suffix):
            return model[:-len(suffix)], suffix[1:]
    return None


def antigravity_label_effort(label):
    """The effort word of a display name such as `Gemini 3.1 Pro (High)`, else None."""
    match = AG_LABEL.fullmatch(label)
    return match.group(1) if match else None


def effort_display(value):
    """Meter level 0-5 (None: a token without a level, shown as its word only) and whether it is a top rung."""
    return {"level": EFFORT_LEVELS.get(value), "top": value in EFFORT_TOP}


def display_order(kind, values):
    order = EFFORT_ORDER if kind == "effort" else SPEED_ORDER
    return sorted(values, key=lambda v: (v == "unknown", order.index(v) if v in order else len(order), v))


def put(facts, fact):
    """Insert like the platform cores: a fact replaces the one with the same kind, evidence, provider and scope."""
    if fact is None:
        return facts
    same = (fact["kind"], fact["evidence"], fact.get("provider"), fact["scope"])
    return [f for f in facts if (f["kind"], f["evidence"], f.get("provider"), f["scope"]) != same] + [fact]


def model_extension_errors(model, *, extensions=None, pointer="/models/0"):
    errors = []
    enabled = lambda name: extensions is None or supported(extensions, name)
    kinds = REPORT_KINDS[2 if extensions is None else max(1, version(extensions, "executionModes"))]
    if "harnessRequests" in model and enabled("modelHarnessRequests"):
        rows = model["harnessRequests"]
        path = pointer + "/harnessRequests"
        if not isinstance(rows, list) or len(rows) > 8:
            errors.append((path, "expected an array of at most 8 rows"))
        else:
            sources, total = set(), 0
            for index, row in enumerate(rows):
                p = path + "/" + str(index)
                if not isinstance(row, dict) or not {"source", "requests"}.issubset(row):
                    errors.append((p, "source and requests are required"))
                    continue
                source, count = row["source"], row["requests"]
                if not isinstance(source, str) or source not in model.get("sources", []) or source in sources:
                    errors.append((p + "/source", "source must be unique and belong to the model"))
                if type(count) is not int or not 0 <= count <= MAX_INTEGER:
                    errors.append((p + "/requests", "expected a nonnegative safe integer"))
                else:
                    total += count
                if isinstance(source, str):
                    sources.add(source)
            if total != model.get("requests"):
                errors.append((path, "requests do not equal the model total"))
    if "modeUsage" not in model or not enabled("executionModes"):
        return errors
    rows = model["modeUsage"]
    path = pointer + "/modeUsage"
    if not isinstance(rows, list) or len(rows) > 64:
        return errors + [(path, "expected an array of at most 64 rows")]
    seen, sums = set(), defaultdict(lambda: defaultdict(int))
    for index, row in enumerate(rows):
        p = path + "/" + str(index)
        required = {"source", "kind", "mode", "evidence", *FIELDS}
        if not isinstance(row, dict) or not required.issubset(row):
            errors.append((p, "missing mode fields"))
            continue
        source, kind, mode, evidence = (row[k] for k in ("source", "kind", "mode", "evidence"))
        provider = row.get("provider", "")
        valid_key = True
        if not isinstance(source, str) or source not in model.get("sources", []):
            errors.append((p + "/source", "source does not belong to the model"))
            valid_key = False
        if kind not in kinds:
            errors.append((p + "/kind", "unknown kind"))
            valid_key = False
        if kind == "effort" and "provider" in row:
            errors.append((p + "/provider", "effort rows have no provider"))
        if not identifier(mode) or ("provider" in row and not identifier(provider, 32)):
            errors.append((p, "invalid mode or provider identifier"))
            valid_key = False
        if evidence not in (*EVIDENCE[1:], "unknown"):
            errors.append((p + "/evidence", "invalid evidence"))
            valid_key = False
        if (mode == "unknown") != (evidence == "unknown"):
            errors.append((p, "unknown mode and evidence must agree"))
        if valid_key:
            key = (source, provider, kind, mode, evidence)
            if key in seen:
                errors.append((p, "duplicate mode row"))
            seen.add(key)
        for field in FIELDS:
            value = row[field]
            if type(value) is not int or not 0 <= value <= MAX_INTEGER:
                errors.append((p + "/" + field, "expected a nonnegative safe integer"))
            elif kind in kinds:
                sums[kind][field] += value
        if (type(row["measuredOutput"]) is int and type(row["output"]) is int
                and row["measuredOutput"] > row["output"]):
            errors.append((p + "/measuredOutput", "exceeds output"))
    for kind, totals in sums.items():
        for field in FIELDS:
            if totals[field] != model.get(field):
                errors.append((path, kind + "." + field + " does not equal the model total"))
    return errors


def _row_order(row):
    return "/".join((row["source"], row["kind"], row.get("provider", ""), row["mode"], row["evidence"]))


def _valid_attribution(group):
    if not identifier(group["mode"]) or ("provider" in group and not identifier(group["provider"], 32)):
        return False
    if group["mode"] == "unknown":
        return group["evidence"] == "unknown" and "provider" not in group
    return group["evidence"] in ("served", "recorded", "selected")


def mode_rows(sources, groups, extension_version):
    """The client's `models[].modeUsage` for one model (EXECUTION-MODES, *Report extension*): `sources` maps each source
    to the model's totals in the report, `groups` are the frozen attributions' usage (known modes only). Every kind of
    the version that is observed, plus `serviceTier` and (version 2) `effort`, is complete per source with an unknown
    bucket. More than 64 rows: version 2 drops `effort` first; still too many, or any group that does not fit its
    source's total or carries an unsafe identifier, and the field is omitted (None)."""
    if extension_version not in REPORT_KINDS:
        return None
    report_kinds = REPORT_KINDS[extension_version]
    observed = {g["kind"] for g in groups if g["mode"] != "unknown"}
    kinds = [k for k in report_kinds if k in observed or k in ("serviceTier", "effort")]
    complete = {}
    for kind in kinds:
        for source, total in sources.items():
            remaining = dict(total)
            for group in (g for g in groups if g["source"] == source and g["kind"] == kind):
                part = {field: group[field] for field in FIELDS}
                if not _valid_attribution(group) or any(not 0 <= part[f] <= min(remaining[f], MAX_INTEGER) for f in FIELDS):
                    return None
                remaining = {f: remaining[f] - part[f] for f in FIELDS}
                key = (source, kind, group.get("provider"), group["mode"], group["evidence"])
                complete[key] = {f: complete.get(key, {}).get(f, 0) + part[f] for f in FIELDS}
            if any(remaining.values()):
                key = (source, kind, None, "unknown", "unknown")
                complete[key] = {f: complete.get(key, {}).get(f, 0) + remaining[f] for f in FIELDS}
    rows = [{"source": k[0], "kind": k[1], **({"provider": k[2]} if k[2] is not None else {}), "mode": k[3],
             "evidence": k[4], **v} for k, v in complete.items() if any(v.values())]
    rows.sort(key=_row_order)
    if len(rows) > MAX_ROWS:
        return mode_rows(sources, [g for g in groups if g["kind"] != "effort"], 1) if extension_version == 2 else None
    return rows


def report_extension_errors(report, extensions=None):
    return [problem for index, model in enumerate(report.get("models", []))
            for problem in model_extension_errors(model, extensions=extensions, pointer="/models/" + str(index))]


def check_fixtures():
    path = Path(__file__).resolve().parent.parent / "fixtures/execution-mode-cases.json"
    data = json.loads(path.read_text())
    count = 0
    for case in data["normalization"]:
        actual = normalize(case["adapter"], case["rawValue"], case.get("provider"), case["evidence"], case["scope"])
        assert actual == case["expected"], (case["id"], actual, case["expected"])
        count += 1
    for case in data["attribution"]:
        assert attribution(case["facts"], case["kind"]) == case["expected"], case["id"]
        count += 1
    for case in data["capabilities"]:
        assert supported(case["extensions"], "executionModes", models_enabled=case["modelsEnabled"]) == case["expected"]
        assert version(case["extensions"], "executionModes", models_enabled=case["modelsEnabled"]) == case["version"]
        count += 1
    for case in data["reportRows"]:
        actual = mode_rows(case["sources"], case["groups"], case["version"])
        assert actual == case["expected"], (case["id"], actual)
        if actual is not None:
            model = {"sources": list(case["sources"]), **{f: sum(t[f] for t in case["sources"].values()) for f in FIELDS},
                     "modeUsage": actual}
            assert model_extension_errors(model, extensions={"executionModes": case["version"]}) == [], case["id"]
        count += 1
    for case in data["speed"]:
        assert speed_attribution(case["facts"]) == case["expected"], case["id"]
        count += 1
    for case in data["effortDisplay"]:
        assert effort_display(case["value"]) == case["expected"], case["value"]
        count += 1
    for case in data["speedBolts"]:
        assert SPEED_BOLTS.get(case["value"], 0) == case["bolts"], case["value"]
        count += 1
    for case in data["displayOrder"]:
        assert display_order(case["kind"], case["values"]) == case["expected"], case["id"]
        count += 1
    for case in data["current"]:
        actual = current_attribution(case["kind"], case["facts"], case.get("session", []), case.get("configured", []))
        assert actual == case["expected"], (case["id"], actual)
        count += 1
    for case in data["ledger"]:
        assert ledger_effort(case["response"], case["current"]) == case["expected"], case["id"]
        count += 1
    for case in data["antigravityEffort"]:
        actual = antigravity_model_effort(case["model"]) if "model" in case else antigravity_label_effort(case["label"])
        assert (list(actual) if isinstance(actual, tuple) else actual) == case["expected"], case["id"]
        count += 1
    return count


if __name__ == "__main__":
    print(str(check_fixtures()) + " shared execution-mode cases passed")
