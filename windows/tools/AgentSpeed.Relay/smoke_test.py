"""Exercise the relay with synthetic logs and a live append. Pass the command that runs the relay."""
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
from queue import Queue
import re
import subprocess
import sys
import tempfile
from threading import Thread
from time import monotonic

FIXTURES = Path(__file__).resolve().parents[3] / "spec" / "fixtures"
ORIGINAL = datetime(2026, 10, 20, 10, tzinfo=timezone.utc)
PRIVATE_MARKER = "agent-speed-synthetic-private-prompt"


def recent_fixture(path, start):
    def replace(match):
        timestamp = datetime.fromisoformat(match.group().replace("Z", "+00:00"))
        return (start + (timestamp - ORIGINAL)).isoformat(timespec="milliseconds").replace("+00:00", "Z")

    return re.sub(r"2026-10-20T10:\d\d:\d\d\.\d\d\dZ", replace, path.read_text(encoding="utf-8"))


def smoke(command):
    now = datetime.now(timezone.utc)
    with tempfile.TemporaryDirectory() as folder:
        root = Path(folder)
        codex = root / "codex" / "sessions"
        codex.mkdir(parents=True)
        claude = root / "claude" / "projects" / "sample"
        claude.mkdir(parents=True)
        sample = next((FIXTURES / "codex").rglob("*modern.jsonl"))
        fixture = codex / sample.name
        lines = recent_fixture(sample, now - timedelta(minutes=3)).splitlines(keepends=True)
        split = next(i for i, line in enumerate(lines) if '"function_call_output"' in line)
        first = "".join(lines[:split])
        first = re.sub(r'("type":"user_message","message":)"[^"]+"', r'\1"' + PRIVATE_MARKER + '"', first)
        assert PRIVATE_MARKER in first
        fixture.write_text(first, encoding="utf-8")
        sample = next((FIXTURES / "claude").rglob("s-cli.jsonl"))
        (claude / sample.name).write_text(recent_fixture(sample, now - timedelta(minutes=3)), encoding="utf-8")
        env = dict(os.environ, CODEX_HOME=str(codex.parent), CLAUDE_CONFIG_DIR=str(claude.parent.parent))
        with subprocess.Popen([*command, "serve"], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE) as process:
            try:
                check_session(process, now, fixture, lines[split:])
            finally:
                if process.poll() is None:
                    process.kill()


def check_session(process, now, fixture, append):
    output = Queue()

    def read():
        for line in process.stdout:
            output.put(line)
        output.put(b"")

    Thread(target=read, daemon=True).start()

    def send(**message):
        process.stdin.write(json.dumps(message).encode() + b"\n")
        process.stdin.flush()

    def get_until(done):
        deadline = monotonic() + 20
        while monotonic() < deadline:
            line = output.get(timeout=max(0.01, deadline - monotonic()))
            assert line, "relay closed stdout"
            assert PRIVATE_MARKER.encode() not in line, "transcript content left the relay"
            message = json.loads(line)
            yield message
            if done(message):
                return
        raise AssertionError("relay did not finish the expected exchange")

    send(t="start", protocol=1, todayStartMs=int((now - timedelta(minutes=5)).timestamp() * 1000),
         sources=["codex", "claudeCode"], zone={"id": "UTC", "tzif": ""}, prices={})
    seeded = list(get_until(lambda message: message["t"] == "ready"))
    assert seeded[0]["t"] == "hello" and seeded[0]["protocol"] == 1
    assert set(row["r"]["source"] for row in seeded if row["t"] == "response" and row["r"]["output"] > 0) == {
        "codex", "claudeCode"
    }
    assert any(row["t"] == "agent" for row in seeded)
    with fixture.open("a", encoding="utf-8") as stream:
        stream.writelines(append)

    live = list(get_until(lambda message: message["t"] == "response" and message["r"]["source"] == "codex"))
    assert live[-1]["r"]["output"] > 0
    send(t="ping", id=42)
    assert list(get_until(lambda message: message["t"] == "clock"))[-1]["id"] == 42
    send(t="history", digests={})
    assert list(get_until(lambda message: message["t"] == "error"))[-1]["code"] == "historyFailed"
    send(t="ping", id=43)
    assert list(get_until(lambda message: message["t"] == "clock"))[-1]["id"] == 43
    process.stdin.close()
    assert process.wait(timeout=5) == 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("usage: smoke_test.py RELAY_COMMAND [ARGS...]")
    smoke(sys.argv[1:])
