#!/usr/bin/env python3
"""Synthetic Trae databases. Never reads real user data; OpenSSL is needed to regenerate.

SQLCipher 4 raw-key page format; SQLite reserves 80 bytes before tables are created.
The WAL includes one committed update and one valid but UNCOMMITTED update which
must not become visible. Encryption salt/IVs are deterministic test data only.
"""
import ctypes
import ctypes.util
import hashlib
import hmac
import json
from pathlib import Path
import struct
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1] / "fixtures"
BASE = 1792497600000
KEY = bytes.fromhex("3605f6691095a993f03d5009c918352ef5be31ae31e8f000212b81ff058da773")
SALT = bytes(range(16))
PAGE = 4096


def context(start, end, inp=0, out=0, model=None, cache=0, write=0, reasoning=0):
    return dict(chat_start_time=BASE + start, chat_end_time=BASE + end if end is not None else 0,
                workspace_folders=["/projects/trae-fixture"],
                persist_user_message_context=dict(model_info=dict(display_model_name=model)),
                token_usage=dict(prompt_tokens=inp, completion_tokens=out, total_tokens=inp + out,
                                 cache_read_input_tokens=cache, cache_creation_input_tokens=write,
                                 reasoning_tokens=reasoning, prompt_tokens_total=0, completion_tokens_total=0))


def database(second_output=100, second_end=60000):
    # SQLITE_FCNTL_RESERVE_BYTES affects new pages; establish it before schema creation.
    lib = ctypes.CDLL(ctypes.util.find_library("sqlite3"))
    lib.sqlite3_open.argtypes = [ctypes.c_char_p, ctypes.POINTER(ctypes.c_void_p)]
    lib.sqlite3_exec.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
    lib.sqlite3_file_control.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_void_p]
    lib.sqlite3_close.argtypes = [ctypes.c_void_p]
    rows = [
        ("s-main", "t-one", "completed", context(0, 10000, 1200, 240, "claude-sonnet-4-5", 400, 100, 40), 0),
        ("s-main", "t-two", "completed", context(20000, second_end, 800, second_output), 0),
        ("s-other", "t-empty-output", "completed", context(70000, 80000, 200, 0, "test-model"), 0),
        ("s-main", "t-running", "running", context(90000, None, 99000, 9999), 0),
        ("s-main", "t-canceled", "canceled", context(0, 50000, 99000, 9999), 0),
        ("s-main", "t-failed", "failed", context(0, 50000, 99000, 9999), 0),
        ("s-main", "t-deleted", "completed", context(0, 50000, 99000, 9999), 1),
        ("s-main", "t-missing-end", "completed", context(0, None, 99000, 9999), 0),
        ("s-main", "t-malformed", "completed", "{broken", 0),
        ("s-main", "t-no-usage", "completed", dict(chat_end_time=BASE + 90000), 0),
    ]
    for name, changes in [
        ("negative", dict(prompt_tokens=-1)), ("fraction", dict(completion_tokens=2.5)),
        ("boolean", dict(completion_tokens=True)), ("string", dict(prompt_tokens="1200")),
        ("mismatched-total", dict(total_tokens=0)),
        ("overlap-cache", dict(cache_read_input_tokens=1000, cache_creation_input_tokens=500)),
        ("reasoning-over-output", dict(reasoning_tokens=241)),
        ("missing-input", dict(prompt_tokens=None)),
    ]:
        invalid = context(0, 10000, 1200, 240)
        invalid["token_usage"].update(changes)
        rows.append(("s-main", "t-invalid-" + name, "completed", invalid, 0))
    schema = """PRAGMA page_size=4096; CREATE TABLE chat_turn (
        id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, turn_id TEXT NOT NULL,
        turn_status TEXT NOT NULL, context TEXT NOT NULL, created_at BIGINT NOT NULL,
        updated_at BIGINT NOT NULL, deleted_at BIGINT NOT NULL DEFAULT 0);"""
    def quote(s):
        return "'" + s.replace("'", "''") + "'"
    for number, (session, turn, status, value, deleted) in enumerate(rows, 1):
        payload = value if isinstance(value, str) else json.dumps(value, separators=(",", ":"))
        schema += "INSERT INTO chat_turn VALUES (%d,%s,%s,%s,%s,%d,%d,%d);" % (
            number, quote(session), quote(turn), quote(status), quote(payload), BASE // 1000, BASE // 1000, deleted)
    with tempfile.TemporaryDirectory() as scratch:
        path = Path(scratch) / "fixture.db"
        handle = ctypes.c_void_p()
        assert lib.sqlite3_open(str(path).encode(), ctypes.byref(handle)) == 0
        try:
            reserve = ctypes.c_int(80)
            assert lib.sqlite3_file_control(handle, b"main", 38, ctypes.byref(reserve)) == 0
            assert lib.sqlite3_exec(handle, schema.encode(), None, None, None) == 0
        finally:
            lib.sqlite3_close(handle)
        data = path.read_bytes()
    assert data[20] == 80 and len(data) % PAGE == 0
    return data


def encrypt(data):
    hkey = hashlib.pbkdf2_hmac("sha512", KEY, bytes(x ^ 0x3a for x in SALT), 2, 32)
    result = bytearray()
    for index in range(len(data) // PAGE):
        start = 16 if index == 0 else 0
        page = data[index * PAGE:(index + 1) * PAGE]
        iv = hashlib.sha256(struct.pack("<I", index + 1) + page).digest()[:16]
        cipher = subprocess.run(["openssl", "enc", "-aes-256-cbc", "-K", KEY.hex(), "-iv", iv.hex(),
                                 "-nopad"], input=page[start:4016], capture_output=True, check=True).stdout
        auth = hmac.digest(hkey, cipher + iv + struct.pack("<I", index + 1), "sha512")
        result.extend((SALT if index == 0 else b"") + cipher + iv + auth)
    return bytes(result)


def checksum(data, state=(0, 0)):
    a, b = state
    for x, y in struct.iter_unpack("<II", data):
        a = (a + x + b) & 0xffffffff
        b = (b + y + a) & 0xffffffff
    return a, b


def wal(committed, uncommitted):
    salt = bytes.fromhex("1223344556677889")
    header = struct.pack(">IIII", 0x377f0682, 3007000, PAGE, 0) + salt
    state = checksum(header)
    result = bytearray(header + struct.pack(">II", *state))
    for data, commit in [(committed, True), (uncommitted, False)]:
        count = len(data) // PAGE
        for i in range(count):
            body = data[i * PAGE:(i + 1) * PAGE]
            prefix = struct.pack(">II", i + 1, count if commit and i == count - 1 else 0)
            state = checksum(body, checksum(prefix, state))
            result.extend(prefix + salt + struct.pack(">II", *state) + body)
    return bytes(result)


def records(output=100, end=60000):
    result = []
    for session, turn, finish, inp, out, cached, write, reasoning, model in [
        ("s-main", "t-one", 10000, 1200, 240, 400, 100, 40, "claude-sonnet-4-5"),
        ("s-main", "t-two", end, 800, output, 0, 0, 0, None),
        ("s-other", "t-empty-output", 80000, 200, 0, 0, 0, 0, "test-model"),
    ]:
        result.append(dict(key="trae:" + session + ":" + turn, startMs=None, endMs=BASE + finish,
                           input=inp, output=out, cachedInput=cached, cacheWriteInput=write,
                           cacheWrite1hInput=0, reasoning=reasoning, model=model,
                           provider=None, final=True, fast=False, requestCount=1))
    return result


def main():
    plain = database()
    encrypted = encrypt(plain)
    committed = encrypt(database(180, 65000))
    uncommitted = encrypt(database(9999, 99000))
    outputs = {"plain": plain, "encrypted": encrypted, "wal": encrypted}
    expected = dict(version=1, files=[])
    for name, data in outputs.items():
        path = ROOT / "trae" / name / "database.db"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        expected["files"].append(dict(path=str(path.relative_to(ROOT)), responses=records(180, 65000)
                                      if name == "wal" else records()))
    (ROOT / "trae" / "wal" / "database.db-wal").write_bytes(wal(committed, uncommitted))
    (ROOT / "trae-expected.json").write_text(json.dumps(expected, indent=2) + "\n")
    print("Generated synthetic plain, encrypted and committed/uncommitted WAL Trae fixtures")


if __name__ == "__main__":
    main()
