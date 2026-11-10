#!/usr/bin/env python3
"""Reference implementation of the local network feed (spec/LAN.md).

Standard library only. It implements the settings document, the frame and its history, the UDP size rule, the
server's first-line classification, HTTP request heads and responses, the access key and lockout, the WebSocket
handshake and framing, local addresses, target addresses, signatures and the push rules; it writes and checks
spec/fixtures/lan-vectors.json. It is also a client for a running app (see `--help`).

    python3 spec/tools/lan_reference.py --check-fixtures
    python3 spec/tools/lan_reference.py --write
"""

from __future__ import annotations

import argparse
import base64
import calendar
import hashlib
import hmac
import ipaddress
import json
import math
import os
import re
import secrets
import socket
import struct
import sys
import time
import uuid
from pathlib import Path
from urllib.parse import unquote

# The repository root when run from spec/tools; the script also works copied on its own (client commands only).
_HERE = Path(__file__).resolve()
ROOT = _HERE.parents[2] if len(_HERE.parents) > 2 else _HERE.parent
VECTORS = ROOT / "spec" / "fixtures" / "lan-vectors.json"

SCHEMA = 1
DEFAULT_PORT = 7247
MAX_TARGETS = 8
KEY_ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
KEY_LENGTH = 12
SOURCE_ORDER = ("codex", "claudeCode", "claudeDesktop", "deepseek", "opencode", "gemini", "omp", "antigravity",
                "workbuddy", "workbuddyAi", "zcode", "kimi", "grokBuild", "pi", "openclaw", "hermes", "droid", "devin",
                "qoder", "qoderCli")
MAX_TOKS = 1e9
MAX_COUNT = 100_000
NAME_LIMIT = 64
DEFAULT_NAME = "Agent Speed"
HISTORY_STEP_MS = 1000
HISTORY_LENGTH = 60
HISTORY_RESTART_MS = 60_000
FRAME_LIMIT = 16_384
DATAGRAM_LIMIT = 1400
HEAD_LIMIT = 8192
HEAD_TIMEOUT_MS = 5000
MAX_CONNECTIONS = 16
LOCKOUT_FAILURES = 10
LOCKOUT_WINDOW_MS = 60_000
LOCKOUT_MS = 60_000
LOCKOUT_ADDRESSES = 256
WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
WS_CLIENT_LIMIT = 4096
UNSENT_LIMIT = 65_536
PUSH = {"stream": (500, 5000), "udp": (500, 1000), "http": (1000, 10_000)}
PUSH_TOLERANCE_MS = 100
HTTP_TIMEOUT_MS = 3000
HTTP_BACKOFF_MS = (2000, 4000, 8000, 16_000, 30_000)
UDP_RESOLVE_MS = 30_000
REASONS = {101: "Switching Protocols", 200: "OK", 400: "Bad Request", 401: "Unauthorized", 404: "Not Found",
           405: "Method Not Allowed", 426: "Upgrade Required", 429: "Too Many Requests", 503: "Service Unavailable"}
UUID_RE = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")
RUN_RE = re.compile(r"^[0-9a-f]{16}$")
AT_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")
REQUEST_LINE_RE = re.compile(r"^([A-Z]+) (\S+) HTTP/1\.[01]$")
SOURCE_ID_RE = re.compile(r"^[a-z][A-Za-z0-9]{0,31}$")
LABEL_RE = re.compile(r"^[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?$")


# ------------------------------------------------------------------------------------------------- numbers and JSON

def round_away(value: float, decimals: int) -> float:
    """METRICS 11.7: half away from zero on the scaled binary value, like the apps."""
    if not math.isfinite(value):
        return 0.0
    scale = 10.0 ** decimals
    scaled = value * scale
    whole = math.floor(abs(scaled))
    rounded = whole + 1 if abs(scaled) - whole >= 0.5 else whole
    return math.copysign(rounded, scaled) / scale


def plain(value: float):
    """The written form of a rounded decimal: an int when whole, so `0` and `1200` carry no fraction."""
    return int(value) if float(value).is_integer() else value


def tok_s(value) -> float:
    value = float(value) if isinstance(value, (int, float)) and math.isfinite(value) else 0.0
    return plain(round_away(min(max(value, 0.0), MAX_TOKS), 1))


def share(value) -> float:
    value = float(value) if isinstance(value, (int, float)) and math.isfinite(value) else 0.0
    return plain(round_away(min(max(value, 0.0), 1.0), 3))


def count(value) -> int:
    return min(max(int(value), 0), MAX_COUNT) if isinstance(value, int) else 0


def compact(value) -> bytes:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def format_instant(ms: int) -> str:
    seconds, millis = divmod(ms, 1000)
    return time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(seconds)) + f".{millis:03d}Z"


# ------------------------------------------------------------------------------------------------- settings (2.1)

def valid_key(value) -> bool:
    return isinstance(value, str) and len(value) == KEY_LENGTH and all(ch in KEY_ALPHABET for ch in value)


def new_key() -> str:
    return "".join(secrets.choice(KEY_ALPHABET) for _ in range(KEY_LENGTH))


def display_key(key: str) -> str:
    return "-".join(key[i:i + 4] for i in range(0, len(key), 4))


def _uuid(value) -> str | None:
    return value if isinstance(value, str) and UUID_RE.match(value) else None


def _text(value, low: int, high: int) -> str | None:
    if not isinstance(value, str):
        return None
    value = value.strip()
    if not low <= len(value) <= high or any(ord(ch) < 0x20 or ord(ch) == 0x7F for ch in value):
        return None
    return value


def parse_settings(document) -> dict:
    """2.1: the normalized document; `instance` or `server.key` null means "create one when needed"."""
    defaults = {"version": 1, "instance": None, "server": {"enabled": False, "port": DEFAULT_PORT, "key": None},
                "targets": []}
    if not isinstance(document, dict) or document.get("version") != 1:
        return defaults
    result = {"version": 1, "instance": _uuid(document.get("instance")),
              "server": dict(defaults["server"]), "targets": []}
    server = document.get("server")
    if isinstance(server, dict):
        if isinstance(server.get("enabled"), bool):
            result["server"]["enabled"] = server["enabled"]
        port = server.get("port")
        if isinstance(port, int) and not isinstance(port, bool) and 1024 <= port <= 65535:
            result["server"]["port"] = port
        if valid_key(server.get("key")):
            result["server"]["key"] = server["key"]
    targets = document.get("targets")
    seen = set()
    for entry in targets if isinstance(targets, list) else []:
        if len(result["targets"]) == MAX_TARGETS:
            break
        if not isinstance(entry, dict):
            continue
        target_id = _uuid(entry.get("id"))
        kind = entry.get("kind")
        address = entry.get("address")
        if target_id is None or target_id in seen or kind not in ("udp", "http") or not isinstance(address, str):
            continue
        address = address.strip()
        if "error" in parse_target(kind, address):
            continue
        name = entry.get("name")
        name = _text(name, 0, NAME_LIMIT) if isinstance(name, str) else ""
        key = entry.get("key")
        key = _text(key, 1, 128) if key is not None else None
        enabled = entry.get("enabled")
        seen.add(target_id)
        result["targets"].append({"id": target_id, "name": name or "", "kind": kind, "address": address, "key": key,
                                  "enabled": enabled if isinstance(enabled, bool) else True})
    return result


# ------------------------------------------------------------------------------------------------- frame (3)

def computer_name(raw) -> str:
    text = raw.strip() if isinstance(raw, str) else ""
    text = text[:NAME_LIMIT].strip() if len(text) > NAME_LIMIT else text
    return text or DEFAULT_NAME


def phase(generating: int, tools: int) -> str:
    return "generating" if generating > 0 else "tools" if tools > 0 else "idle"


def _values(live: dict) -> dict:
    generating, tools, active = count(live.get("generating")), count(live.get("tools")), count(live.get("active"))
    return {"phase": phase(generating, tools), "outputTokS": tok_s(live.get("outputTokS")),
            "estimatedShare": share(live.get("estimatedShare")),
            "sessions": {"active": active, "generating": generating, "tools": tools}}


def build_frame(inputs: dict) -> dict:
    """3.1–3.2 from the tick's live values: `live` overall and `sources` per enabled source (raw, unrounded)."""
    overall = _values(inputs["live"])
    sources = []
    given = inputs.get("sources", {})
    for source in SOURCE_ORDER:
        if source not in given:
            continue
        values = _values(given[source])
        if values["sessions"]["active"] > 0 or values["outputTokS"] > 0:
            sources.append({"source": source, **values})
    frame = {"schema": SCHEMA, "type": "live", "instance": inputs["instance"], "name": computer_name(inputs["name"]),
             "run": inputs["run"], "seq": inputs["seq"], "at": format_instant(inputs["atMs"]),
             "phase": overall["phase"], "outputTokS": overall["outputTokS"],
             "estimatedShare": overall["estimatedShare"], "estimated": overall["estimatedShare"] > 0,
             "sessions": overall["sessions"], "sources": sources,
             "history": {"stepMs": HISTORY_STEP_MS, "outputTokS": [tok_s(v) for v in inputs.get("history", [])]}}
    return frame


def changed(previous: dict | None, frame: dict) -> bool:
    """3.3: any member other than `seq` and `at` differs."""
    if previous is None:
        return True
    strip = lambda f: {k: v for k, v in f.items() if k not in ("seq", "at")}  # noqa: E731
    return strip(previous) != strip(frame)


class History:
    """3.4: one sample per second on the monotonic clock, the newest 60."""

    def __init__(self):
        self.s0 = None
        self.next = 0
        self.last_sample_ms = None
        self.samples: list = []

    def tick(self, t: int, value) -> list:
        value = tok_s(value)
        if self.s0 is None or t - self.last_sample_ms >= HISTORY_RESTART_MS:
            self.s0, self.next, self.last_sample_ms, self.samples = t, 1, t, [value]
            return list(self.samples)
        while t >= self.s0 + self.next * HISTORY_STEP_MS:
            self.samples.append(value)
            self.next += 1
            self.last_sample_ms = t
        del self.samples[:-HISTORY_LENGTH]
        return list(self.samples)


def signature(key: str, body: bytes) -> str:
    return "sha256=" + hmac.new(key.encode("utf-8"), body, hashlib.sha256).hexdigest()


def datagram(frame: dict, key: str | None) -> dict:
    """3.5 and 5.2: the datagram's bytes and which members were left out."""
    omitted = []
    candidate = dict(frame)
    for member in (None, "history", "sources"):
        if member:
            candidate.pop(member, None)
            omitted.append(member)
        body = compact(candidate)
        data = body + (b"\n" + signature(key, body).encode() if key else b"")
        if len(data) <= DATAGRAM_LIMIT:
            return {"bytes": data, "omitted": omitted}
    return {"bytes": data, "omitted": omitted}


def frame_problems(frame) -> list[str]:
    """What a device can check in a received frame (3.1–3.2)."""
    problems = []
    if not isinstance(frame, dict):
        return ["not an object"]
    if frame.get("schema") != SCHEMA or frame.get("type") != "live":
        problems.append("schema/type")
    if not (isinstance(frame.get("instance"), str) and UUID_RE.match(frame["instance"])):
        problems.append("instance")
    if not (isinstance(frame.get("name"), str) and 0 < len(frame["name"]) <= NAME_LIMIT):
        problems.append("name")
    if not (isinstance(frame.get("run"), str) and RUN_RE.match(frame["run"])):
        problems.append("run")
    if not (isinstance(frame.get("seq"), int) and frame["seq"] >= 1):
        problems.append("seq")
    if not (isinstance(frame.get("at"), str) and AT_RE.match(frame["at"])):
        problems.append("at")

    def check_values(entry: dict, where: str):
        sessions = entry.get("sessions")
        if not isinstance(sessions, dict) or any(not isinstance(sessions.get(k), int) or sessions[k] < 0
                                                 for k in ("active", "generating", "tools")):
            problems.append(f"{where}.sessions")
            return
        if entry.get("phase") != phase(sessions["generating"], sessions["tools"]):
            problems.append(f"{where}.phase")
        value = entry.get("outputTokS")
        if not isinstance(value, (int, float)) or isinstance(value, bool) or value < 0:
            problems.append(f"{where}.outputTokS")
        value = entry.get("estimatedShare")
        if not isinstance(value, (int, float)) or isinstance(value, bool) or not 0 <= value <= 1:
            problems.append(f"{where}.estimatedShare")

    check_values(frame, "frame")
    if frame.get("estimated") is not (isinstance(frame.get("estimatedShare"), (int, float))
                                      and frame["estimatedShare"] > 0):
        problems.append("estimated")
    if "sources" in frame:
        sources = frame["sources"]
        if not isinstance(sources, list):
            problems.append("sources")
        else:
            order = [SOURCE_ORDER.index(e.get("source")) if e.get("source") in SOURCE_ORDER else None
                     for e in sources if isinstance(e, dict)]
            known = [o for o in order if o is not None]
            if known != sorted(set(known)):
                problems.append("sources.order")
            for entry in sources:
                if not isinstance(entry, dict) or not isinstance(entry.get("source"), str):
                    problems.append("sources.entry")
                    continue
                check_values(entry, f"sources.{entry['source']}")
    if "history" in frame:
        history = frame["history"]
        if (not isinstance(history, dict) or history.get("stepMs") != HISTORY_STEP_MS
                or not isinstance(history.get("outputTokS"), list)
                or not 0 < len(history["outputTokS"]) <= HISTORY_LENGTH
                or any(not isinstance(v, (int, float)) or v < 0 for v in history["outputTokS"])):
            problems.append("history")
    return problems


# ------------------------------------------------------------------------------------------------- addresses (2.3, 5.1)

def is_local(address: str, udp: bool = False) -> bool:
    try:
        ip = ipaddress.ip_address(address.split("%", 1)[0])
    except ValueError:
        return False
    if isinstance(ip, ipaddress.IPv6Address):
        if ip.ipv4_mapped is not None:
            ip = ip.ipv4_mapped
        else:
            return ip == ipaddress.IPv6Address("::1") or ip in ipaddress.IPv6Network("fe80::/10") \
                or ip in ipaddress.IPv6Network("fc00::/7")
    if udp and ip == ipaddress.IPv4Address("255.255.255.255"):
        return True
    return any(ip in ipaddress.IPv4Network(n) for n in
               ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "127.0.0.0/8"))


def _host(text: str) -> dict:
    """A host as written: `[IPv6]`, an IPv4 address or a DNS name."""
    if text.startswith("["):
        if not text.endswith("]"):
            return {"error": "host"}
        inner = text[1:-1]
        try:
            ipaddress.IPv6Address(inner.split("%", 1)[0])
        except ValueError:
            return {"error": "host"}
        return {"host": inner, "literal": True}
    if not text or len(text) > 253:
        return {"error": "host"}
    labels = text.rstrip(".").split(".") if text != "." else [""]
    if all(label.isdigit() for label in labels):
        try:
            ipaddress.IPv4Address(text)
        except ValueError:
            return {"error": "host"}
        return {"host": text, "literal": True}
    if not all(LABEL_RE.match(label) for label in labels):
        return {"error": "host"}
    return {"host": text, "literal": False}


def _port(text: str, default: int | None) -> int | None:
    if text == "" and default is not None:
        return default
    if not text.isdigit() or len(text) > 5 or (len(text) > 1 and text[0] == "0"):
        return None
    value = int(text)
    return value if 1 <= value <= 65535 else None


def _split_host_port(authority: str) -> tuple[str, str | None]:
    if authority.startswith("["):
        end = authority.find("]")
        if end < 0:
            return authority, None
        rest = authority[end + 1:]
        if rest == "":
            return authority, None
        return (authority[:end + 1], rest[1:]) if rest.startswith(":") else (authority, "!")
    if authority.count(":") > 1:
        return authority, "!"
    host, sep, port = authority.partition(":")
    return host, (port if sep else None)


def parse_target(kind: str, address: str) -> dict:
    """5.1: the parts of a target address, or `{"error": reason}`."""
    if not isinstance(address, str) or address != address.strip() or address == "":
        return {"error": "empty" if not (address or "").strip() else "spaces"}
    if any(ord(ch) <= 0x20 or ord(ch) == 0x7F for ch in address):
        return {"error": "spaces"}
    if kind == "udp":
        host_text, port_text = _split_host_port(address)
        if port_text is None or port_text == "!":
            return {"error": "port"}
        host = _host(host_text)
        if "error" in host:
            return host
        port = _port(port_text, None)
        if port is None:
            return {"error": "port"}
        return {"host": host["host"], "literal": host["literal"], "port": port}
    if kind != "http":
        return {"error": "kind"}
    lower = address.lower()
    if lower.startswith("https://"):
        return {"error": "https"}
    if not lower.startswith("http://"):
        return {"error": "scheme"}
    rest = address[7:]
    if "#" in rest:
        return {"error": "fragment"}
    cut = min([i for i in (rest.find("/"), rest.find("?")) if i >= 0], default=len(rest))
    authority, target = rest[:cut], rest[cut:]
    if "@" in authority:
        return {"error": "userinfo"}
    host_text, port_text = _split_host_port(authority)
    if port_text == "!":
        return {"error": "port"}
    host = _host(host_text)
    if "error" in host:
        return host
    port = _port(port_text or "", 80) if port_text != "" else None
    if port is None:
        return {"error": "port"}
    if target.startswith("?") or target == "":
        target = "/" + target
    return {"host": host["host"], "literal": host["literal"], "port": port, "hostHeader": authority,
            "target": target}


def resolve_check(addresses: list[str], udp: bool) -> str:
    """5.1: the status after resolution: `ok` with every address local, else `notLocal` (`notFound` when none)."""
    if not addresses:
        return "notFound"
    return "ok" if all(is_local(a, udp) for a in addresses) else "notLocal"


# ------------------------------------------------------------------------------------------------- server (4)

def classify_first_line(data: bytes) -> str:
    """4.2: `http`, `line` or `close` for the bytes received so far (complete: they contain `\\n`, or the limit)."""
    end = data.find(b"\n")
    if end < 0 or end >= HEAD_LIMIT:
        return "close"
    line = data[:end]
    if line.endswith(b"\r"):
        line = line[:-1]
    try:
        text = line.decode("utf-8")
    except UnicodeDecodeError:
        return "close"
    if REQUEST_LINE_RE.match(text):
        return "http"
    return "line" if text.startswith("{") else "close"


def parse_head(data: bytes) -> dict:
    """4.3: the request head (up to the empty line) as method, target and headers; `error` 400 when malformed."""
    end = data.find(b"\r\n\r\n")
    sep = 4
    alt = data.find(b"\n\n")
    if end < 0 or (0 <= alt < end):
        end, sep = alt, 2
    if end < 0 or end + sep > HEAD_LIMIT:
        return {"error": "incomplete"}
    lines = data[:end].decode("utf-8", "replace").split("\n")
    lines = [line[:-1] if line.endswith("\r") else line for line in lines]
    match = REQUEST_LINE_RE.match(lines[0])
    if not match:
        return {"error": "incomplete"}
    headers: dict[str, str] = {}
    for line in lines[1:]:
        if ":" not in line:
            return {"method": match.group(1), "target": match.group(2), "headers": headers, "malformed": True}
        name, _, value = line.partition(":")
        headers.setdefault(name.strip().lower(), value.strip())
    return {"method": match.group(1), "target": match.group(2), "headers": headers, "malformed": False}


def normalize_key(text: str) -> str:
    return "".join(ch.upper() if "a" <= ch <= "z" else ch for ch in text if ch not in "- ")


def key_matches(given: str | None, stored: str) -> bool:
    if given is None:
        return False
    candidate = normalize_key(given).encode("utf-8")
    expected = stored.encode("utf-8")
    return len(candidate) == len(expected) and hmac.compare_digest(candidate, expected)


def request_key(target: str, headers: dict) -> str | None:
    """4.4: the Bearer token, else the first `key` query parameter (percent-decoded), else none."""
    auth = headers.get("authorization")
    if auth:
        scheme, _, token = auth.partition(" ")
        if scheme.lower() == "bearer" and token.strip():
            return token.strip()
    _, _, query = target.partition("?")
    for part in query.split("&") if query else []:
        name, sep, value = part.partition("=")
        if name == "key" and sep:
            return unquote(value)
    return None


class Lockout:
    """4.4: ten failures within 60 s lock an address out for 60 s; at most 256 addresses remembered."""

    def __init__(self):
        self.entries: dict[str, dict] = {}

    def locked(self, address: str, now: int) -> int | None:
        entry = self.entries.get(address)
        if entry and entry["until"] is not None and now < entry["until"]:
            return math.ceil((entry["until"] - now) / 1000)
        return None

    def fail(self, address: str, now: int) -> None:
        entry = self.entries.pop(address, None) or {"failures": [], "until": None}
        entry["failures"] = [t for t in entry["failures"] if now - t < LOCKOUT_WINDOW_MS] + [now]
        if len(entry["failures"]) >= LOCKOUT_FAILURES:
            entry["until"] = now + LOCKOUT_MS
            entry["failures"] = []
        self.entries[address] = entry
        while len(self.entries) > LOCKOUT_ADDRESSES:
            del self.entries[next(iter(self.entries))]

    def succeed(self, address: str) -> None:
        self.entries.pop(address, None)


def ws_accept(key: str) -> str:
    return base64.b64encode(hashlib.sha1((key + WS_GUID).encode()).digest()).decode()


def _tokens(value: str | None) -> set[str]:
    return {part.strip().lower() for part in (value or "").split(",") if part.strip()}


def _ws_key_ok(value: str | None) -> bool:
    if not value:
        return False
    try:
        return len(base64.b64decode(value, validate=True)) == 16
    except ValueError:
        return False


def error_body(code: str) -> bytes:
    return compact({"schema": SCHEMA, "type": "error", "error": code})


def respond(head: dict, peer: str, now: int, key: str, has_frame: bool, lockout: Lockout) -> dict:
    """4.3: the response to a parsed request head: status, error code, the extra headers, and what follows."""
    def reply(status, error=None, **extra):
        return {"status": status, "error": error, "headers": extra, "body": "error" if error else "frame"}
    if head.get("malformed"):
        return reply(400, "badRequest")
    left = lockout.locked(peer, now)
    if left is not None:
        return reply(429, "tooManyAttempts", **{"Retry-After": str(left)})
    if head["method"] != "GET":
        return reply(405, "methodNotAllowed", Allow="GET")
    path = head["target"].split("?", 1)[0]
    if path not in ("/v1/live", "/v1/stream"):
        return reply(404, "notFound")
    if not key_matches(request_key(head["target"], head["headers"]), key):
        lockout.fail(peer, now)
        return reply(401, "unauthorized", **{"WWW-Authenticate": 'Bearer realm="agent-speed"'})
    lockout.succeed(peer)
    headers = head["headers"]
    if path == "/v1/live":
        return reply(200) if has_frame else reply(503, "starting")
    if "websocket" not in _tokens(headers.get("upgrade")) or "upgrade" not in _tokens(headers.get("connection")):
        return reply(426, "upgradeRequired", Upgrade="websocket")
    if headers.get("sec-websocket-version") != "13" or not _ws_key_ok(headers.get("sec-websocket-key")):
        return reply(400, "badRequest", **{"Sec-WebSocket-Version": "13"})
    return {"status": 101, "error": None, "body": None,
            "headers": {"Upgrade": "websocket", "Connection": "Upgrade",
                        "Sec-WebSocket-Accept": ws_accept(headers["sec-websocket-key"])}}


def response_bytes(result: dict, frame: bytes | None) -> bytes:
    """The bytes of a response from `respond` (a 101 has no body)."""
    lines = [f"HTTP/1.1 {result['status']} {REASONS[result['status']]}"]
    if result["status"] == 101:
        lines += [f"{name}: {value}" for name, value in result["headers"].items()]
        return ("\r\n".join(lines) + "\r\n\r\n").encode()
    body = error_body(result["error"]) if result["error"] else frame
    lines += ["Content-Type: application/json; charset=utf-8", f"Content-Length: {len(body)}",
              "Cache-Control: no-store", "Connection: close"]
    lines += [f"{name}: {value}" for name, value in result["headers"].items()]
    return ("\r\n".join(lines) + "\r\n\r\n").encode() + body


def hello(line: bytes, peer: str, now: int, key: str, lockout: Lockout) -> str | None:
    """4.6: None for a valid hello, else the error code."""
    if lockout.locked(peer, now) is not None:
        return "tooManyAttempts"
    try:
        value = json.loads(line.decode("utf-8"))
    except (UnicodeDecodeError, ValueError):
        return "badRequest"
    if not isinstance(value, dict) or value.get("type") != "hello" or not isinstance(value.get("key"), str):
        return "badRequest"
    if not key_matches(value["key"], key):
        lockout.fail(peer, now)
        return "unauthorized"
    lockout.succeed(peer)
    return None


def ws_frame(opcode: int, payload: bytes) -> bytes:
    """A server frame: FIN, unmasked."""
    head = bytes([0x80 | opcode])
    size = len(payload)
    if size < 126:
        head += bytes([size])
    elif size <= 0xFFFF:
        head += bytes([126]) + struct.pack(">H", size)
    else:
        head += bytes([127]) + struct.pack(">Q", size)
    return head + payload


def ws_close(code: int | None) -> bytes:
    return ws_frame(0x8, b"" if code is None else struct.pack(">H", code))


def ws_client_frame(data: bytes) -> dict:
    """4.5: how the server handles one complete client frame."""
    if len(data) < 2:
        return {"action": "incomplete"}
    first, second = data[0], data[1]
    fin, rsv, opcode = first & 0x80, first & 0x70, first & 0x0F
    masked, size = second & 0x80, second & 0x7F
    pos = 2
    if size == 126:
        size, pos = struct.unpack(">H", data[2:4])[0], 4
    elif size == 127:
        size, pos = struct.unpack(">Q", data[2:10])[0], 10
    if rsv or opcode not in (0x0, 0x1, 0x2, 0x8, 0x9, 0xA) or not masked:
        return {"action": "close", "code": 1002}
    if opcode >= 0x8 and (not fin or size > 125):
        return {"action": "close", "code": 1002}
    if opcode < 0x8 and size > WS_CLIENT_LIMIT:
        return {"action": "close", "code": 1009}
    mask = data[pos:pos + 4]
    payload = bytes(b ^ mask[i % 4] for i, b in enumerate(data[pos + 4:pos + 4 + size]))
    if opcode == 0x9:
        return {"action": "pong", "payload": payload.hex()}
    if opcode == 0x8:
        if len(payload) == 1:
            return {"action": "close", "code": 1002}
        code = struct.unpack(">H", payload[:2])[0] if len(payload) >= 2 else None
        return {"action": "echoClose", "code": code}
    return {"action": "ignore"}


def mask_frame(opcode: int, payload: bytes, mask: bytes, fin: bool = True, rsv: int = 0, masked: bool = True) -> bytes:
    """A client frame for the vectors."""
    head = bytes([(0x80 if fin else 0) | rsv | opcode])
    size = len(payload)
    flag = 0x80 if masked else 0
    if size < 126:
        head += bytes([flag | size])
    elif size <= 0xFFFF:
        head += bytes([flag | 126]) + struct.pack(">H", size)
    else:
        head += bytes([flag | 127]) + struct.pack(">Q", size)
    if not masked:
        return head + payload
    return head + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload))


# ------------------------------------------------------------------------------------------------- push (4.7)

def simulate_push(kind: str, ticks: list) -> list[int]:
    """4.7 for one receiver that is never busy: the tick times at which it sends. `ticks` = [[t, changed], …];
    the first tick starts the receiver."""
    minimum, heartbeat = PUSH[kind]
    sent, last, held = [], None, False
    for t, is_changed in ticks:
        if last is None:
            sent.append(t)
            last = t
            continue
        held = held or bool(is_changed)
        elapsed = t - last + PUSH_TOLERANCE_MS
        if (held and elapsed >= minimum) or elapsed >= heartbeat:
            sent.append(t)
            last, held = t, False
    return sent


def http_backoff_ms(failures: int) -> int:
    return HTTP_BACKOFF_MS[min(failures, len(HTTP_BACKOFF_MS)) - 1] if failures > 0 else 0


# ------------------------------------------------------------------------------------------------- vectors

INSTANCE = "3f2504e0-4f89-41d3-9a0c-0305e82c3301"
RUN = "9b1c4e7a2f6d8e03"
KEY = "K7Q4M2X9PT3D"
AT_MS = 1_794_042_000_250  # 2026-11-07T09:00:00.250Z


def _frame_case(label: str, **inputs) -> dict:
    base = {"instance": INSTANCE, "name": "Zoe's MacBook Pro", "run": RUN, "seq": 1342, "atMs": AT_MS,
            "live": {"outputTokS": 0, "estimatedShare": 0, "generating": 0, "tools": 0, "active": 0},
            "sources": {}, "history": [0]}
    base.update(inputs)
    frame = build_frame(base)
    return {"name": label, "input": base, "frame": compact(frame).decode()}


def build_vectors() -> dict:
    frames = [
        _frame_case("idle, nothing active"),
        _frame_case(
            "generating with an estimate, two sources, one silent enabled source left out",
            live={"outputTokS": 75.94, "estimatedShare": 0.41951, "generating": 2, "tools": 1, "active": 4},
            sources={"claudeCode": {"outputTokS": 0.04, "estimatedShare": 0, "generating": 0, "tools": 1,
                                    "active": 1},
                     "codex": {"outputTokS": 75.9, "estimatedShare": 0.4195, "generating": 2, "tools": 0,
                               "active": 3},
                     "gemini": {"outputTokS": 0, "estimatedShare": 0, "generating": 0, "tools": 0, "active": 0}},
            history=[61.2, 70.4, 75.9]),
        _frame_case("rounding half away from zero, clamps and a whole number",
                    live={"outputTokS": 1200.05, "estimatedShare": 0.0005, "generating": 250_000, "tools": -3,
                          "active": 250_000},
                    sources={"devin": {"outputTokS": 2e12, "estimatedShare": 1.7, "generating": 0, "tools": 0,
                                       "active": 0},
                             "grokBuild": {"outputTokS": 0.05, "estimatedShare": 0.0004, "generating": 0, "tools": 0,
                                           "active": 0}},
                    history=[1200, 0.1, 0]),
        _frame_case("only tools: phase tools, an active source with no speed",
                    live={"outputTokS": 0, "estimatedShare": 0, "generating": 0, "tools": 2, "active": 2},
                    sources={"kimi": {"outputTokS": 0, "estimatedShare": 0, "generating": 0, "tools": 2,
                                      "active": 2}}),
        _frame_case("name: trimmed, cut at 64 scalars, quotes and controls escaped",
                    name="  " + "名" * 70 + "  "),
        _frame_case("name with escapes", name='Zoe "Dev" \\ Box\t\u0001'),
        _frame_case("empty name", name="   "),
    ]

    history_cases = []
    for name, ticks in [
        ("steady 500 ms ticks", [[i * 500, i * 10] for i in range(9)]),
        ("jittered ticks sample at the first tick at or after each step",
         [[0, 1], [510, 2], [1020, 3], [1530, 4], [2040, 5], [2490, 6], [3001, 7]]),
        ("a stall fills the missed samples with the late tick's value", [[0, 5], [500, 6], [1000, 7], [4300, 9]]),
        ("a gap of 60 s restarts the history", [[0, 5], [1000, 6], [61_000, 8], [61_500, 9], [62_000, 10]]),
        ("only the newest 60 are kept", [[i * 1000, i] for i in range(65)]),
    ]:
        history = History()
        results = [history.tick(t, v) for t, v in ticks]
        history_cases.append({"name": name, "ticks": ticks, "after": results[-1]})

    big_sources = {source: {"outputTokS": 1234.5 + i, "estimatedShare": 0.123, "generating": 1, "tools": 1,
                            "active": 12} for i, source in enumerate(SOURCE_ORDER[:9])}
    long_history = [1234.5 + i / 10 for i in range(60)]
    datagram_cases = []
    for name, inputs, key in [
        ("small frame, no key", {"live": {"outputTokS": 75.9, "estimatedShare": 0.42, "generating": 1, "tools": 0,
                                          "active": 1}, "history": [75.9]}, None),
        ("small frame with a key", {"live": {"outputTokS": 75.9, "estimatedShare": 0.42, "generating": 1,
                                             "tools": 0, "active": 1}, "history": [75.9]}, "s3cret-phrase"),
        ("history left out", {"live": {"outputTokS": 1300, "estimatedShare": 0.1, "generating": 3, "tools": 1,
                                       "active": 5},
                              "sources": dict(list(big_sources.items())[:6]), "history": long_history}, None),
        ("history and sources left out", {"live": {"outputTokS": 1300, "estimatedShare": 0.1, "generating": 9,
                                                   "tools": 9, "active": 108},
                                          "sources": big_sources, "history": long_history}, "s3cret-phrase"),
    ]:
        base = {"instance": INSTANCE, "name": "Zoe's MacBook Pro", "run": RUN, "seq": 7, "atMs": AT_MS,
                "sources": {}, "history": [0]}
        base.update(inputs)
        frame = build_frame(base)
        result = datagram(frame, key)
        datagram_cases.append({"name": name, "frame": compact(frame).decode(), "key": key,
                               "datagram": result["bytes"].decode(), "omitted": result["omitted"],
                               "bytes": len(result["bytes"])})

    first_lines = [
        ["GET /v1/live HTTP/1.1\r\n", "http"], ["GET /v1/stream?key=x HTTP/1.0\n", "http"],
        ["POST /v1/live HTTP/1.1\r\n", "http"], ["get /v1/live HTTP/1.1\r\n", "close"],
        ["GET /v1/live HTTP/2\r\n", "close"], ["GET  /v1/live HTTP/1.1\r\n", "close"],
        ['{"type":"hello","key":"K7Q4-M2X9-PT3D"}\n', "line"], ['{"type":"hello"}\r\n', "line"],
        ["hello\n", "close"], ["\n", "close"], ["GET /v1/live HTTP/1.1", "close"],
        ["G" * 8200 + "\n", "close"],
    ]

    lockout = Lockout()
    peer = "192.168.1.77"
    requests = []

    def request(name, head, now=0, has_frame=True, peer_address=peer):
        parsed = parse_head(head.encode())
        result = respond(parsed, peer_address, now, KEY, has_frame, lockout)
        requests.append({"name": name, "peer": peer_address, "nowMs": now, "hasFrame": has_frame, "head": head,
                         "status": result["status"], "error": result["error"], "headers": result["headers"]})

    ws_head = ("GET /v1/stream HTTP/1.1\r\nHost: 192.168.1.20:7247\r\nUpgrade: websocket\r\n"
               "Connection: keep-alive, Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"
               "Sec-WebSocket-Version: 13\r\nAuthorization: Bearer K7Q4-M2X9-PT3D\r\n\r\n")
    request("live with a Bearer key", "GET /v1/live HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer K7Q4-M2X9-PT3D\r\n\r\n")
    request("live with a lowercase scheme and key without hyphens",
            "GET /v1/live HTTP/1.1\r\nauthorization: bearer k7q4m2x9pt3d\r\n\r\n")
    request("live with the key in the query, percent-encoded",
            "GET /v1/live?x=1&key=K7Q4%2DM2X9%2DPT3D&key=wrong HTTP/1.1\r\n\r\n")
    request("live before the first frame", "GET /v1/live?key=K7Q4M2X9PT3D HTTP/1.1\r\n\r\n", has_frame=False)
    request("bare LF line endings", "GET /v1/live?key=K7Q4M2X9PT3D HTTP/1.1\nHost: x\n\n")
    request("websocket upgrade", ws_head)
    request("websocket without upgrade", "GET /v1/stream?key=K7Q4M2X9PT3D HTTP/1.1\r\n\r\n")
    request("websocket version 8", ws_head.replace("Version: 13", "Version: 8"))
    request("websocket key of 15 bytes", ws_head.replace("dGhlIHNhbXBsZSBub25jZQ==", "dGhlIHNhbXBsZSBub25j"))
    request("unknown path", "GET /v1/other?key=K7Q4M2X9PT3D HTTP/1.1\r\n\r\n")
    request("trailing slash is another path", "GET /v1/live/ HTTP/1.1\r\n\r\n")
    request("POST", "POST /v1/live HTTP/1.1\r\nContent-Length: 2\r\n\r\n")
    request("header line without a colon", "GET /v1/live HTTP/1.1\r\nBroken header\r\n\r\n")
    request("Basic auth is no key", "GET /v1/live HTTP/1.1\r\nAuthorization: Basic Zm9vOmJhcg==\r\n\r\n", now=1000)
    for i in range(9):
        request(f"wrong key {i + 2}", "GET /v1/live?key=AAAA-BBBB-CCCC HTTP/1.1\r\n\r\n", now=2000 + i * 1000)
    request("locked out after the 10th failure, even with the right key",
            "GET /v1/live?key=K7Q4M2X9PT3D HTTP/1.1\r\n\r\n", now=10_500)
    request("locked out answers before the method", "POST /v1/live HTTP/1.1\r\n\r\n", now=30_000)
    request("another address is not locked out", "GET /v1/live?key=K7Q4M2X9PT3D HTTP/1.1\r\n\r\n", now=30_000,
            peer_address="192.168.1.78")
    request("one second before the lockout ends", "GET /v1/live?key=K7Q4M2X9PT3D HTTP/1.1\r\n\r\n", now=69_000)
    request("the lockout ends 60 s after the 10th failure", "GET /v1/live?key=K7Q4M2X9PT3D HTTP/1.1\r\n\r\n",
            now=70_000)

    hello_lockout = Lockout()
    hellos = []
    for name, line, now in [
        ("valid hello", '{"type":"hello","key":"K7Q4-M2X9-PT3D","device":"desk"}', 0),
        ("hello with a lowercase key", '{"key":"k7q4 m2x9 pt3d","type":"hello"}', 0),
        ("wrong type", '{"type":"subscribe","key":"K7Q4-M2X9-PT3D"}', 0),
        ("key not a string", '{"type":"hello","key":12}', 0),
        ("not JSON", '{"type":"hello",', 0),
        ("an array", '[{"type":"hello"}]', 0),
        ("wrong key", '{"type":"hello","key":"K7Q4-M2X9-PL3E"}', 0),
        ("short key", '{"type":"hello","key":"K7Q4-M2X9"}', 0),
    ]:
        hellos.append({"name": name, "line": line, "nowMs": now,
                       "error": hello(line.encode(), peer, now, KEY, hello_lockout)})

    mask = bytes.fromhex("37fa213d")
    ws_cases = [
        {"name": "ping", "frame": mask_frame(0x9, b"hi", mask).hex()},
        {"name": "close with 1000", "frame": mask_frame(0x8, struct.pack(">H", 1000) + b"bye", mask).hex()},
        {"name": "close without a code", "frame": mask_frame(0x8, b"", mask).hex()},
        {"name": "close with one byte", "frame": mask_frame(0x8, b"\x03", mask).hex()},
        {"name": "text is ignored", "frame": mask_frame(0x1, b"hello", mask).hex()},
        {"name": "pong is ignored", "frame": mask_frame(0xA, b"", mask).hex()},
        {"name": "unmasked", "frame": mask_frame(0x1, b"x", mask, masked=False).hex()},
        {"name": "reserved bit", "frame": mask_frame(0x1, b"x", mask, rsv=0x40).hex()},
        {"name": "reserved opcode", "frame": mask_frame(0x3, b"x", mask).hex()},
        {"name": "fragmented ping", "frame": mask_frame(0x9, b"x", mask, fin=False).hex()},
        {"name": "text of 4096 bytes", "frame": mask_frame(0x1, b"a" * 4096, mask).hex()},
        {"name": "text of 4097 bytes", "frame": mask_frame(0x2, b"a" * 4097, mask).hex()},
        {"name": "ping of 126 bytes", "frame": mask_frame(0x9, b"p" * 126, mask).hex()},
    ]
    for case in ws_cases:
        case["result"] = ws_client_frame(bytes.fromhex(case["frame"]))
    accept = [{"key": k, "accept": ws_accept(k)} for k in ("dGhlIHNhbXBsZSBub25jZQ==", "AQIDBAUGBwgJCgsMDQ4PEA==")]
    server_frames = [{"opcode": op, "payload": p, "frame": ws_frame(op, p.encode()).hex()}
                     for op, p in ((1, "{}"), (1, "x" * 126), (1, "y" * 70_000))]
    server_frames[2]["frame"] = server_frames[2]["frame"][:40]
    server_frames[2]["prefixOnly"] = True
    closes = [{"code": c, "frame": ws_close(c).hex()} for c in (1001, 1008, None)]

    locals_ = [[a, u, is_local(a, u)] for a, u in [
        ("192.168.1.20", False), ("10.1.2.3", False), ("172.16.0.1", False), ("172.31.255.255", False),
        ("172.32.0.1", False), ("169.254.10.1", False), ("127.0.0.1", False), ("8.8.8.8", False),
        ("100.64.0.1", False), ("0.0.0.0", False), ("255.255.255.255", False), ("255.255.255.255", True),
        ("192.168.1.255", True), ("224.0.0.251", True), ("::1", False), ("fe80::1", False),
        ("fe80::1%en0", False), ("fd12:3456::1", False), ("fc00::1", False), ("2001:db8::1", False),
        ("::ffff:192.168.1.5", False), ("::ffff:8.8.8.8", False), ("::", False), ("ff02::1", True),
    ]]

    targets = []
    for kind, address in [
        ("udp", "192.168.1.50:7247"), ("udp", "192.168.1.255:7247"), ("udp", "[fe80::1]:9000"),
        ("udp", "desk-display.local:7247"), ("udp", "desk_display:1"), ("udp", "192.168.1.50"),
        ("udp", "192.168.1.50:0"), ("udp", "192.168.1.50:65536"), ("udp", "192.168.1.50:07247"),
        ("udp", "fe80::1:9000"), ("udp", "192.168.1.300:7247"), ("udp", "-bad.local:7247"),
        ("udp", " 192.168.1.50:7247"), ("udp", ""),
        ("http", "http://192.168.1.50/agent-speed"), ("http", "http://192.168.1.50"),
        ("http", "HTTP://Desk.local:8080?x=1"), ("http", "http://homeassistant.local:8123/api/webhook/a?b=c"),
        ("http", "http://[fd00::5]:8080/x"), ("http", "https://192.168.1.50/"), ("http", "ftp://192.168.1.50/"),
        ("http", "http://user@192.168.1.50/"), ("http", "http://192.168.1.50/#top"), ("http", "http:///x"),
        ("http", "http://192.168.1.50:/x"), ("http", "http://192.168.1.50:99999/"),
        ("http", "http://192.168.1.50/a b"),
    ]:
        targets.append({"kind": kind, "address": address, "result": parse_target(kind, address)})
    resolutions = [{"addresses": a, "udp": u, "status": resolve_check(a, u)} for a, u in [
        (["192.168.1.50"], False), (["192.168.1.50", "fe80::5"], False), (["192.168.1.50", "52.1.2.3"], False),
        ([], False), (["255.255.255.255"], True), (["255.255.255.255"], False)]]

    signatures = [{"key": k, "body": b, "signature": signature(k, b.encode())} for k, b in [
        ("s3cret-phrase", '{"schema":1,"type":"live"}'), ("鍵", "{}"), ("k", "")]]

    settings_cases = []
    for name, doc in [
        ("missing", None),
        ("another version", {"version": 2, "server": {"enabled": True}}),
        ("full", {"version": 1, "instance": INSTANCE,
                  "server": {"enabled": True, "port": 7300, "key": KEY},
                  "targets": [{"id": "8c0d6b0a-5a8e-4b0e-9d4e-2f1f3c5a7b90", "name": " Desk ", "kind": "udp",
                               "address": " 192.168.1.50:7247 ", "key": None, "enabled": False},
                              {"id": "1b9e8a3c-2d4f-4e6a-8b0c-7d5e3f1a9c24", "kind": "http",
                               "address": "http://homeassistant.local:8123/api/webhook/x", "key": " s3cret "}]}),
        ("invalid members fall back", {"version": 1, "instance": "NOT-A-UUID",
                                       "server": {"enabled": "yes", "port": 80, "key": "k7q4m2x9pt3d"},
                                       "targets": "none"}),
        ("bad targets are left out", {"version": 1, "instance": INSTANCE, "server": {"port": 65535},
                                      "targets": [
                                          {"id": "8c0d6b0a-5a8e-4b0e-9d4e-2f1f3c5a7b90", "kind": "tcp",
                                           "address": "192.168.1.5:1"},
                                          {"id": "8c0d6b0a-5a8e-4b0e-9d4e-2f1f3c5a7b90", "kind": "udp",
                                           "address": "192.168.1.5"},
                                          {"id": "8c0d6b0a-5a8e-4b0e-9d4e-2f1f3c5a7b90", "kind": "udp",
                                           "address": "192.168.1.5:1", "key": "", "name": "x" * 65},
                                          {"id": "8c0d6b0a-5a8e-4b0e-9d4e-2f1f3c5a7b90", "kind": "udp",
                                           "address": "192.168.1.6:1"},
                                          {"id": "not-a-uuid", "kind": "udp", "address": "192.168.1.7:1"}]}),
        ("at most eight targets", {"version": 1, "targets": [
            {"id": str(uuid.UUID(int=i, version=4)), "kind": "udp", "address": f"192.168.1.{i}:7247"}
            for i in range(1, 11)]}),
    ]:
        settings_cases.append({"name": name, "document": doc, "settings": parse_settings(doc)})

    push = []
    for kind, ticks in [
        ("stream", [[t, t in (0, 500, 1000, 1500)] for t in range(0, 12_001, 500)]),
        ("udp", [[t, t in (2000,)] for t in range(0, 4501, 500)]),
        ("http", [[t, t >= 500 and t <= 2500] for t in range(0, 14_001, 500)]),
        ("stream", [[0, True], [490, True], [985, True], [1470, True], [1860, True], [2400, False], [7350, False]]),
    ]:
        push.append({"kind": kind, "ticks": ticks, "sent": simulate_push(kind, ticks)})

    keys = [{"given": g, "matches": key_matches(g, KEY)} for g in (
        "K7Q4-M2X9-PT3D", "k7q4m2x9pt3d", " K7Q4 M2X9 PT3D ", "K7Q4-M2X9-PT3D-", "K7Q4_M2X9_PT3D", "K7Q4-M2X9-PT3",
        "K7Q4-M2X9-PT3DX", "", "Ｋ7Q4M2X9PT3D")]

    return {"_comment": "Written by spec/tools/lan_reference.py --write; checked by --check-fixtures (spec/LAN.md).",
            "key": KEY, "keyDisplay": display_key(KEY), "frames": frames, "history": history_cases,
            "datagrams": datagram_cases, "firstLines": first_lines, "requests": requests, "hellos": hellos,
            "websocket": {"accept": accept, "clientFrames": ws_cases, "serverFrames": server_frames,
                          "closes": closes},
            "local": locals_, "targets": targets, "resolutions": resolutions, "signatures": signatures,
            "settings": settings_cases, "push": push, "keys": keys,
            "httpBackoffMs": [http_backoff_ms(n) for n in range(0, 8)]}


def check_vectors(verbose: bool = False) -> list[str]:
    stored = json.loads(VECTORS.read_text(encoding="utf-8"))
    fresh = json.loads(json.dumps(build_vectors(), ensure_ascii=False))
    problems = []
    for section in fresh:
        if stored.get(section) != fresh[section]:
            problems.append(f"{section}: differs from the reference")
    for frame in fresh["frames"]:
        found = frame_problems(json.loads(frame["frame"]))
        if found:
            problems.append(f"frame {frame['name']}: {found}")
    for case in fresh["datagrams"]:
        if case["bytes"] > DATAGRAM_LIMIT:
            problems.append(f"datagram {case['name']}: {case['bytes']} bytes")
    expected_omissions = {"small frame, no key": [], "small frame with a key": [], "history left out": ["history"],
                          "history and sources left out": ["history", "sources"]}
    for case in fresh["datagrams"]:
        if case["omitted"] != expected_omissions[case["name"]]:
            problems.append(f"datagram {case['name']}: omitted {case['omitted']}")
    if verbose:
        for section, value in fresh.items():
            size = len(value) if isinstance(value, (list, dict)) else 1
            print(f"  {section}: {size}")
    return problems


# ------------------------------------------------------------------------------------------------- clients

def _print_frame(raw: bytes, key: str | None, origin: str) -> None:
    body, _, signature_line = raw.partition(b"\n")
    try:
        frame = json.loads(body)
    except ValueError:
        print(f"{origin}: not JSON ({len(raw)} bytes)")
        return
    problems = frame_problems(frame)
    if key is not None:
        expected = signature(key, body).encode()
        if signature_line.strip() != expected:
            problems.append("signature")
    sources = ",".join(f"{s['source']}={s['outputTokS']}" for s in frame.get("sources", []))
    history = frame.get("history", {}).get("outputTokS")
    print(f"{origin} seq={frame.get('seq')} {frame.get('phase')} {frame.get('outputTokS')} tok/s "
          f"est={frame.get('estimatedShare')} sessions={frame.get('sessions')} [{sources}] "
          f"history={len(history) if history is not None else '-'} {len(raw)}B"
          + (f" PROBLEMS {problems}" if problems else " ok"))


def client_live(args) -> int:
    with socket.create_connection((args.host, args.port), timeout=5) as conn:
        conn.sendall(f"GET /v1/live HTTP/1.1\r\nHost: {args.host}\r\nAuthorization: Bearer {args.key}\r\n\r\n"
                     .encode())
        data = b""
        while chunk := conn.recv(65536):
            data += chunk
    head, _, body = data.partition(b"\r\n\r\n")
    print(head.decode(errors="replace"))
    if head.startswith(b"HTTP/1.1 200"):
        _print_frame(body, None, "live")
        return 0
    print(body.decode(errors="replace"))
    return 1


def _read_ws_frame(conn) -> tuple[int, bytes]:
    def exact(n):
        out = b""
        while len(out) < n:
            chunk = conn.recv(n - len(out))
            if not chunk:
                raise ConnectionError("closed")
            out += chunk
        return out
    first, second = exact(2)
    size = second & 0x7F
    if size == 126:
        size = struct.unpack(">H", exact(2))[0]
    elif size == 127:
        size = struct.unpack(">Q", exact(8))[0]
    return first & 0x0F, exact(size)


def client_stream(args) -> int:
    nonce = base64.b64encode(os.urandom(16)).decode()
    with socket.create_connection((args.host, args.port), timeout=15) as conn:
        conn.sendall((f"GET /v1/stream HTTP/1.1\r\nHost: {args.host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                      f"Sec-WebSocket-Key: {nonce}\r\nSec-WebSocket-Version: 13\r\n"
                      f"Authorization: Bearer {args.key}\r\n\r\n").encode())
        head = b""
        while b"\r\n\r\n" not in head:
            chunk = conn.recv(1)
            if not chunk:
                break
            head += chunk
        print(head.decode(errors="replace").strip())
        if not head.startswith(b"HTTP/1.1 101") or ws_accept(nonce).encode() not in head:
            return 1
        deadline = time.monotonic() + args.seconds
        while time.monotonic() < deadline:
            opcode, payload = _read_ws_frame(conn)
            if opcode == 0x8:
                print(f"close {struct.unpack('>H', payload[:2])[0] if len(payload) >= 2 else ''}")
                return 0
            if opcode == 0x1:
                _print_frame(payload, None, "ws")
    return 0


def client_lines(args) -> int:
    with socket.create_connection((args.host, args.port), timeout=15) as conn:
        conn.sendall(compact({"type": "hello", "key": args.key}) + b"\n")
        reader = conn.makefile("rb")
        deadline = time.monotonic() + args.seconds
        while time.monotonic() < deadline:
            line = reader.readline()
            if not line:
                print("closed")
                return 0
            _print_frame(line.rstrip(b"\n"), None, "line")
    return 0


def receive_udp(args) -> int:
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind(("0.0.0.0", args.port))
    sock.settimeout(1)
    deadline = time.monotonic() + args.seconds
    while time.monotonic() < deadline:
        try:
            data, origin = sock.recvfrom(65536)
        except socket.timeout:
            continue
        _print_frame(data, args.key, f"udp {origin[0]}")
    return 0


def receive_http(args) -> int:
    from http.server import BaseHTTPRequestHandler, HTTPServer

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):  # noqa: N802
            body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
            given = self.headers.get("X-AgentSpeed-Signature")
            raw = body + (b"\n" + given.encode() if given else b"")
            _print_frame(raw, args.key, f"http {self.client_address[0]} {self.path}")
            self.send_response(204)
            self.end_headers()

        def log_message(self, *_):
            pass

    server = HTTPServer(("0.0.0.0", args.port), Handler)
    server.timeout = 1
    deadline = time.monotonic() + args.seconds
    while time.monotonic() < deadline:
        server.handle_request()
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check-fixtures", action="store_true", help="check spec/fixtures/lan-vectors.json")
    parser.add_argument("--write", action="store_true", help="write spec/fixtures/lan-vectors.json")
    parser.add_argument("-v", "--verbose", action="store_true")
    sub = parser.add_subparsers(dest="command")
    for name in ("live", "stream", "lines"):
        p = sub.add_parser(name)
        p.add_argument("--host", required=True)
        p.add_argument("--port", type=int, default=DEFAULT_PORT)
        p.add_argument("--key", required=True)
        p.add_argument("--seconds", type=float, default=30)
    for name in ("udp", "http"):
        p = sub.add_parser(name)
        p.add_argument("--port", type=int, default=DEFAULT_PORT)
        p.add_argument("--key")
        p.add_argument("--seconds", type=float, default=60)
    args = parser.parse_args()
    if args.write:
        VECTORS.write_text(json.dumps(build_vectors(), ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        print(f"wrote {VECTORS.relative_to(ROOT)}")
        return 0
    if args.check_fixtures:
        problems = check_vectors(args.verbose)
        for problem in problems:
            print(problem)
        print("OK" if not problems else f"{len(problems)} problem(s)")
        return 1 if problems else 0
    commands = {"live": client_live, "stream": client_stream, "lines": client_lines, "udp": receive_udp,
                "http": receive_http}
    if args.command in commands:
        return commands[args.command](args)
    parser.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
