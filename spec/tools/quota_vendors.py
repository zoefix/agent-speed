"""Phase 2 quota readers (spec/QUOTAS.md section 9): response shapes to windows, and browser cookie decryption.

Imported by quota_reference.py, which writes and checks the fixtures. Standard library only, plus the `openssl`
command-line tool for AES-128-CBC.
"""

from __future__ import annotations

import hashlib
import json
import math
import subprocess
from datetime import datetime, timezone

DAY_MIN = 1440
WEEK_MIN = 10080
MONTH_MIN = 43200


def is_number(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v)


def number(v):
    if is_number(v):
        return float(v)
    if isinstance(v, str):
        try:
            f = float(v.strip())
        except ValueError:
            return None
        return f if math.isfinite(f) else None
    return None


def time_ms(v):
    """ISO 8601 text, epoch seconds or epoch milliseconds (above 1e11) to epoch milliseconds."""
    if is_number(v):
        return int(v) if v > 1e11 else int(v * 1000)
    if isinstance(v, str) and v.strip():
        text = v.strip()
        if text.isdigit():
            return time_ms(int(text))
        try:
            dt = datetime.fromisoformat(text.replace("Z", "+00:00"))
        except ValueError:
            return None
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return int(dt.timestamp() * 1000)
    return None


def win(kind, used, resets, name=None, minutes=None):
    if minutes is None:
        minutes = {"fiveHour": 300, "sevenDay": WEEK_MIN, "month": MONTH_MIN}.get(kind)
    return {"kind": kind, "name": name, "minutes": minutes,
            "usedPercent": round(float(used), 6), "resetsAtMs": resets}


def kind_for_minutes(minutes):
    if 290 <= minutes <= 310:
        return "fiveHour"
    if 10000 <= minutes <= 10160:
        return "sevenDay"
    return "other"


# ---------- 9.2 Grok Build ----------

def grok_from_log(lines):
    best = None
    for line in lines:
        try:
            obj = json.loads(line)
        except ValueError:
            continue
        if not isinstance(obj, dict) or obj.get("msg") != "billing: fetched credits config":
            continue
        ctx = obj.get("ctx")
        ts = time_ms(obj.get("ts"))
        if not isinstance(ctx, dict) or not isinstance(ctx.get("config"), dict) or ts is None:
            continue
        if best is None or ts > best[0]:
            best = (ts, ctx["config"])
    if best is None:
        return None
    return {"observedMs": best[0], "windows": grok_windows(best[1])}


def grok_windows(config):
    period = config.get("currentPeriod") if isinstance(config.get("currentPeriod"), dict) else {}
    kind = "month" if "MONTH" in str(period.get("type") or "").upper() else "sevenDay"
    used = number(config.get("creditUsagePercent"))
    if used is None:
        if config.get("isUnifiedBillingUser") is True and period.get("start") and period.get("end"):
            used = 0.0
        else:
            return []
    resets = time_ms(period.get("end")) or time_ms(config.get("billingPeriodEnd"))
    minutes = None
    start = time_ms(period.get("start"))
    if kind == "month" and start is not None and time_ms(period.get("end")) is not None:
        minutes = (time_ms(period.get("end")) - start) // 60000
    return [win(kind, used, resets, minutes=minutes)]


# ---------- 9.3 Devin ----------

def devin_windows(rows):
    """rows: [(key, json text)]; the greatest endTimestamp wins."""
    best = None
    for _, value in rows:
        try:
            plan = json.loads(value)
        except ValueError:
            continue
        if not isinstance(plan, dict):
            continue
        end = number(plan.get("endTimestamp")) or 0
        if best is None or end > best[0]:
            best = (end, plan)
    if best is None:
        return []
    plan = best[1]
    out = []
    for prefix, kind, minutes in (("daily", "other", DAY_MIN), ("weekly", "sevenDay", None)):
        if plan.get(f"hide{prefix.capitalize()}Quota") is True:
            continue
        remaining = number(plan.get(f"{prefix}RemainingPercent"))
        if remaining is None:
            continue
        out.append(win(kind, 100 - remaining, time_ms(plan.get(f"{prefix}ResetAtUnix")), minutes=minutes))
    return out


# ---------- 9.4 Antigravity ----------

def antigravity_group_name(display):
    d = (display or "").lower()
    if "gemini" in d and "argon" in d:
        return "Gemini Argon"
    if "gemini" in d:
        return "Gemini"
    if "claude" in d or "gpt" in d or "third" in d:
        return "Claude/GPT"
    return display or None


def antigravity_kind(bucket):
    text = " ".join(str(bucket.get(k) or "") for k in ("window", "bucketId", "displayName")).lower().replace("_", "-")
    if any(w in text for w in ("weekly", "week", "7d")):
        return "sevenDay"
    if any(w in text for w in ("5h", "5-hour", "five-hour", "five hour", "session")):
        return "fiveHour"
    return None


def antigravity_remaining(bucket):
    v = bucket.get("remainingFraction")
    if is_number(v):
        return float(v)
    if isinstance(v, dict) and v.get("case") == "remainingFraction" and is_number(v.get("value")):
        return float(v["value"])
    rem = bucket.get("remaining")
    if isinstance(rem, dict) and is_number(rem.get("remainingFraction")):
        return float(rem["remainingFraction"])
    return None


def antigravity_windows(body):
    root = body.get("response") if isinstance(body.get("response"), dict) else body
    out = []
    for group in root.get("groups") or []:
        if not isinstance(group, dict):
            continue
        name = antigravity_group_name(group.get("displayName"))
        for bucket in group.get("buckets") or []:
            if not isinstance(bucket, dict) or bucket.get("disabled") is True:
                continue
            kind = antigravity_kind(bucket)
            remaining = antigravity_remaining(bucket)
            if kind is None or remaining is None:
                continue
            out.append(win(kind, (1 - remaining) * 100, time_ms(bucket.get("resetTime")), name=name))
    return out


def antigravity_status_windows(body):
    status = body.get("userStatus") if isinstance(body.get("userStatus"), dict) else {}
    data = status.get("cascadeModelConfigData") if isinstance(status.get("cascadeModelConfigData"), dict) else {}
    out = []
    for config in data.get("clientModelConfigs") or []:
        info = config.get("quotaInfo") if isinstance(config, dict) else None
        if not isinstance(info, dict) or not is_number(info.get("remainingFraction")):
            continue
        out.append(win("fiveHour", (1 - info["remainingFraction"]) * 100, time_ms(info.get("resetTime")),
                       name=config.get("label") or None))
    return out


# ---------- 9.5 Kimi Code ----------

TIME_UNITS = {"TIME_UNIT_MINUTE": 1, "TIME_UNIT_HOUR": 60, "TIME_UNIT_DAY": DAY_MIN, "TIME_UNIT_WEEK": WEEK_MIN}


def kimi_reset(d):
    for key in ("resetTime", "reset_time", "resetAt", "reset_at"):
        if d.get(key) is not None:
            return time_ms(d[key])
    return None


def kimi_counts(d):
    limit, used, remaining = number(d.get("limit")), number(d.get("used")), number(d.get("remaining"))
    if limit is None or limit <= 0:
        return None
    if used is None and remaining is not None:
        used = limit - remaining
    return None if used is None else used / limit * 100


def kimi_windows(body):
    usages = body.get("usages")
    out = []
    if isinstance(usages, dict):
        for key, kind in (("limit_5h", "fiveHour"), ("limit_7d", "sevenDay")):
            w = usages.get(key)
            if isinstance(w, dict) and number(w.get("used_ratio")) is not None:
                out.append(win(kind, number(w["used_ratio"]) * 100, kimi_reset(w)))
        for key in ("limit_month_total", "limit_month"):
            w = usages.get(key)
            if isinstance(w, dict) and number(w.get("used_ratio")) is not None:
                out.append(win("month", number(w["used_ratio"]) * 100, kimi_reset(w)))
                break
        if out:
            return out
    for item in body.get("limits") or []:
        if not isinstance(item, dict):
            continue
        window = item.get("window") if isinstance(item.get("window"), dict) else {}
        detail = item.get("detail") if isinstance(item.get("detail"), dict) else {}
        duration = number(window.get("duration"))
        unit = TIME_UNITS.get(window.get("timeUnit"))
        used = kimi_counts(detail)
        if duration is None or unit is None or used is None:
            continue
        minutes = int(duration * unit)
        kind = kind_for_minutes(minutes)
        out.append(win(kind, used, kimi_reset(detail), minutes=minutes if kind == "other" else None))
    usage = body.get("usage")
    if isinstance(usage, dict) and not any(w["kind"] == "sevenDay" for w in out):
        used = kimi_counts(usage)
        if used is not None:
            out.append(win("sevenDay", used, kimi_reset(usage)))
    return out


# ---------- 9.6 Gemini CLI ----------

FAMILY_ORDER = {"Pro": 0, "Flash": 1, "Flash Lite": 2}


def gemini_family(model_id):
    m = (model_id or "").lower()
    if "flash-lite" in m:
        return "Flash Lite"
    if "flash" in m:
        return "Flash"
    if "pro" in m:
        return "Pro"
    return model_id


def gemini_project(body):
    p = body.get("cloudaicompanionProject")
    if isinstance(p, str) and p:
        return p
    if isinstance(p, dict) and isinstance(p.get("id"), str):
        return p["id"]
    return None


def gemini_windows(body):
    best = {}
    for bucket in body.get("buckets") or []:
        if not isinstance(bucket, dict) or not is_number(bucket.get("remainingFraction")):
            continue
        family = gemini_family(bucket.get("modelId"))
        if not family:
            continue
        remaining = float(bucket["remainingFraction"])
        if family not in best or remaining < best[family][0]:
            best[family] = (remaining, time_ms(bucket.get("resetTime")))
    names = sorted(best, key=lambda f: (FAMILY_ORDER.get(f, 3), f))
    return [win("other", (1 - best[f][0]) * 100, best[f][1], name=f, minutes=DAY_MIN) for f in names]


# ---------- 9.7 Factory Droid ----------

def droid_key_from_env_file(text):
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("export "):
            stripped = stripped[len("export "):].strip()
        if stripped.startswith("FACTORY_API_KEY="):
            value = stripped.split("=", 1)[1].strip()
            if len(value) >= 2 and value[0] == value[-1] and value[0] in "'\"":
                value = value[1:-1]
            return value or None
    return None


def droid_windows(body, now_ms):
    limits = body.get("limits") if isinstance(body.get("limits"), dict) else {}
    out = []
    for pool, name in (("standard", None), ("core", "Core")):
        p = limits.get(pool)
        if not isinstance(p, dict):
            continue
        for key, kind in (("fiveHour", "fiveHour"), ("weekly", "sevenDay"), ("monthly", "month")):
            w = p.get(key)
            if not isinstance(w, dict) or number(w.get("usedPercent")) is None:
                continue
            remaining = number(w.get("secondsRemaining"))
            end = time_ms(w.get("windowEnd"))
            if remaining is not None:
                resets = now_ms + int(remaining * 1000)
                used = number(w["usedPercent"])
            elif end is not None and end <= now_ms:
                resets, used = None, 0.0
            else:
                resets, used = end, number(w["usedPercent"])
            out.append(win(kind, used, resets, name=name))
    return out


# ---------- 9.8 ZCode ----------

ZAI_UNITS = {1: DAY_MIN, 3: 60, 5: 1, 6: WEEK_MIN}


def zcode_windows(body, now_ms):
    if body.get("success") is not True or body.get("code") != 200:
        return None
    data = body.get("data") if isinstance(body.get("data"), dict) else {}
    out = []
    for item in data.get("limits") or []:
        if not isinstance(item, dict):
            continue
        usage, current = number(item.get("usage")), number(item.get("currentValue"))
        used = current / usage * 100 if usage is not None and current is not None and usage > 0 \
            else number(item.get("percentage"))
        if used is None:
            continue
        resets = time_ms(item.get("nextResetTime"))
        if item.get("type") == "TIME_LIMIT":
            out.append(win("month", used, resets, name="MCP"))
            continue
        if item.get("type") not in ("TOKENS_LIMIT", "CREDIT_LIMIT"):
            continue
        unit, count = ZAI_UNITS.get(item.get("unit")), number(item.get("number"))
        if unit is None or count is None:
            continue
        minutes = int(count * unit)
        kind = kind_for_minutes(minutes)
        if kind == "fiveHour" and resets is not None and resets - now_ms > 301 * 60000:
            resets = None
        out.append(win(kind, used, resets, minutes=minutes if kind == "other" else None))
    return out


# ---------- 9.9 Qoder and WorkBuddy ----------

def pick(d, *keys):
    for k in keys:
        if d.get(k) is not None:
            return d[k]
    return None


def qoder_windows(body):
    used_total = limit_total = 0.0
    percents, resets, seen = [], None, False
    for key in ("totalQuota", "sharedQuota"):
        q = body.get(key)
        s = q.get("quotaSummary") if isinstance(q, dict) else None
        if not isinstance(s, dict):
            continue
        used, limit = number(pick(s, "usedValue", "used_value")), number(pick(s, "limitValue", "limit_value"))
        if used is not None and limit is not None and limit > 0:
            used_total += used
            limit_total += limit
            seen = True
        else:
            p = number(pick(s, "usagePercentage", "usage_percentage"))
            if p is not None:
                percents.append(p)
    reset_value = pick(body, "nextResetAt", "next_reset_at")
    resets = time_ms(reset_value) if reset_value is not None else None
    if seen:
        return [win("credits", used_total / limit_total * 100, resets)]
    if percents:
        return [win("credits", max(percents), resets)]
    return []


def workbuddy_windows(body):
    if body.get("code") != 0:
        return None
    data = body.get("data") if isinstance(body.get("data"), dict) else {}
    total = remain = 0.0
    seen = False
    for package in data.get("Packages") or []:
        if not isinstance(package, dict) or package.get("CapacityUnit") != "credits":
            continue
        t, r = number(package.get("CycleTotalCapacity")), number(package.get("CycleRemainCapacity"))
        if t is None or r is None:
            continue
        total += t
        remain += r
        seen = True
    if not seen or total <= 0:
        return []
    return [win("month", (total - remain) / total * 100, None)]


def chrome_user_agent(platform_token, major):
    return (f"Mozilla/5.0 ({platform_token}) AppleWebKit/537.36 (KHTML, like Gecko) "
            f"Chrome/{major}.0.0.0 Safari/537.36")


# ---------- 9.10 browser cookies ----------

def chrome_cbc_key(password, iterations):
    return hashlib.pbkdf2_hmac("sha1", password.encode("utf-8"), b"saltysalt", iterations, 16)


def openssl_cbc(data, key, decrypt):
    args = ["openssl", "enc", "-aes-128-cbc", "-K", key.hex(), "-iv", "20" * 16]
    if decrypt:
        args.append("-d")
    return subprocess.run(args, input=data, capture_output=True, check=True).stdout


def chrome_cbc_encrypt(plain, password, iterations, prefix=b"v10", host=None):
    body = (hashlib.sha256(host.encode("utf-8")).digest() if host else b"") + plain
    return prefix + openssl_cbc(body, chrome_cbc_key(password, iterations), False)


def chrome_cbc_decrypt(blob, password, iterations, meta_version):
    if blob[:3] not in (b"v10", b"v11"):
        return None
    plain = openssl_cbc(blob[3:], chrome_cbc_key(password, iterations), True)
    if meta_version >= 24:
        plain = plain[32:]
    return plain.decode("utf-8")


def cookie_header(rows, site, now_s):
    """rows: [(host_key, name, value, expires_s)] in database order."""
    parts = []
    for host, name, value, expires in rows:
        h = host.lstrip(".") if host.startswith(".") else host
        if not (h == site or h.endswith("." + site) or host == "." + site):
            continue
        if expires and expires <= now_s:
            continue
        parts.append(f"{name}={value}")
    return "; ".join(parts) or None


GCM_VECTOR = {
    "key": bytes(range(32)).hex(),
    "nonce": bytes(0xA0 + i for i in range(12)).hex(),
    "ciphertextAndTag": "957d0f5e2ca46c820506eafe6415afb519c97466f3db370979330478b4637df5c1fcbcfa51a6ed7b",
    "plaintext": "session=gcm-cookie-value",
    "blob": "v10" + bytes(0xA0 + i for i in range(12)).hex(),
}
