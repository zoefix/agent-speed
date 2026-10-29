#!/usr/bin/env python3
"""Independent read-only reference for Trae's partial, reconciled request usage.

No conversation text is returned. The reference uses OpenSSL for AES; product
readers use native cryptography. Decrypted snapshots are never written to disk.
"""
import hashlib
import hmac
import json
import math
from pathlib import Path
import sqlite3
import struct
import subprocess

from harness_reference import millis, record

KEY = bytes.fromhex("3605f6691095a993f03d5009c918352ef5be31ae31e8f000212b81ff058da773")
MAX_BYTES = 256 * 1024 * 1024


def text(value):
    return value if isinstance(value, str) and value.strip() and len(value.encode("utf-8")) <= 4096 else None


def token(value, optional=False, cap=10 ** 12):
    if optional and value is None:
        return 0
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError("invalid usage type")
    if not math.isfinite(value) or value < 0 or value > cap or int(value) != value:
        raise ValueError("invalid usage value")
    return int(value)


def checksum(data, state, endian):
    a, b = state
    for x, y in struct.iter_unpack(endian + "II", data):
        a = (a + x + b) & 0xffffffff
        b = (b + y + a) & 0xffffffff
    return a, b


def snapshot(path):
    path = Path(path)
    paths = [path, Path(str(path) + "-wal"), Path(str(path) + "-journal")]
    def stats():
        result = []
        for item in paths:
            if item.is_symlink():
                raise ValueError("symlink database")
            if not item.exists():
                result.append(None)
                continue
            stat = item.stat()
            if not item.is_file() or stat.st_size > MAX_BYTES:
                raise ValueError("database exceeds snapshot bounds")
            result.append((stat.st_ino, stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns))
        return result
    before = stats()
    if before[2] and any(paths[2].read_bytes()[:8]):
        raise ValueError("active rollback journal")
    data = path.read_bytes()
    encrypted = data[:16] != b"SQLite format 3\x00"
    size = 4096 if encrypted else int.from_bytes(data[16:18], "big")
    size = 65536 if size == 1 else size
    if not (512 <= size <= 65536) or size & (size - 1) or not data or len(data) % size:
        raise ValueError("invalid page size")
    overlay, pending = {}, {}
    pages = len(data) // size
    if before[1] and before[1][1]:
        wal = paths[1].read_bytes()
        if len(wal) < 32:
            raise ValueError("incomplete WAL header")
        magic, version, wal_size = struct.unpack(">III", wal[:12])
        if magic not in (0x377f0682, 0x377f0683) or version != 3007000 or wal_size != size:
            raise ValueError("unsupported WAL")
        endian = "<" if magic == 0x377f0682 else ">"
        state = checksum(wal[:24], (0, 0), endian)
        if state != struct.unpack(">II", wal[24:32]):
            raise ValueError("WAL header checksum")
        for pos in range(32, len(wal) - size - 23, size + 24):
            head, body = wal[pos:pos + 24], wal[pos + 24:pos + 24 + size]
            if head[8:16] != wal[16:24]:
                break
            state = checksum(body, checksum(head[:8], state, endian), endian)
            if state != struct.unpack(">II", head[16:24]):
                break
            number, commit = struct.unpack(">II", head[:8])
            if number == 0 or number * size > MAX_BYTES or commit * size > MAX_BYTES:
                raise ValueError("WAL page outside bounds")
            pending[number] = body
            if commit:
                overlay.update(pending)
                pending.clear()
                pages = commit
                overlay = {n: value for n, value in overlay.items() if n <= pages}
    if pages * size > MAX_BYTES:
        raise ValueError("snapshot exceeds bounds")
    # Validate the acquisition of DB/WAL bytes, not the later CPU-only decryption. A Trae append after this
    # stable read does not change the immutable snapshot and must not invalidate a slow OpenSSL reference run.
    if stats() != before:
        raise ValueError("database changed during snapshot")
    hkey = hashlib.pbkdf2_hmac("sha512", KEY, bytes(x ^ 0x3a for x in data[:16]), 2, 32)
    output = bytearray()
    for number in range(1, pages + 1):
        page = overlay.get(number, data[(number - 1) * size:number * size])
        if len(page) != size:
            raise ValueError("missing page")
        if encrypted:
            start = 16 if number == 1 else 0
            expected = hmac.digest(hkey, page[start:4032] + struct.pack("<I", number), "sha512")
            if not hmac.compare_digest(expected, page[4032:]):
                raise ValueError("SQLCipher authentication")
            decoded = subprocess.run(["openssl", "enc", "-d", "-aes-256-cbc", "-K", KEY.hex(),
                                      "-iv", page[4016:4032].hex(), "-nopad"],
                                     input=page[start:4016], capture_output=True, check=True).stdout
            page = (b"SQLite format 3\x00" if number == 1 else b"") + decoded + bytes(80)
            if number == 1 and (page[20] != 80 or page[16:18] != b"\x10\x00"):
                raise ValueError("unsupported encrypted page format")
        output.extend(page)
    output[18:20] = b"\x01\x01"
    output[28:32] = pages.to_bytes(4, "big")
    output[92:96] = output[24:28]
    return output


def read_trae(path, seen=None):
    seen = seen if seen is not None else set()
    db = sqlite3.connect(":memory:")
    try:
        db.deserialize(snapshot(path))
        db.execute("PRAGMA query_only=1")
        # Project just counters and metadata within SQLite; never return context or prompts.
        fields = ["chat_end_time", "token_usage.prompt_tokens", "token_usage.completion_tokens",
                  "token_usage.cache_read_input_tokens", "token_usage.cache_creation_input_tokens",
                  "token_usage.reasoning_tokens", "persist_user_message_context.model_info.display_model_name",
                  "persist_user_message_context.model_info.model_name",
                  "persist_user_message_context.model_info.config_name", "workspace_folders[0]",
                  "token_usage.total_tokens"]
        expressions = ["json_extract(context, '$.%s')" % field for field in fields]
        # SQLite represents JSON booleans as integers; retain native types for counter validation.
        types = ["json_type(context, '$.%s')" % field for field in fields[1:6] + fields[10:]]
        query = "SELECT id,session_id,turn_id," + ",".join(expressions + types + ["json_type(context,'$.chat_end_time')"]) + """ FROM chat_turn
            WHERE turn_status='completed' AND deleted_at=0 AND CASE WHEN json_valid(context)
            THEN json_type(context,'$.token_usage')='object' ELSE 0 END ORDER BY id"""
        output = []
        for row in db.execute(query):
            identifier, session, turn, end, inp, out, cached, write, reasoning, display, model, config, cwd, total = row[:14]
            end = millis(end)
            if end is None or end <= 0 or not text(session) or row[-1] not in ("integer", "real"):
                continue
            if any(t not in ("integer", "real", None, "null") for t in row[14:-1]):
                continue
            try:
                inp, out = token(inp), token(out)
                cached, write, reasoning = token(cached, True), token(write, True), token(reasoning, True)
                if cached + write > inp or reasoning > out:
                    continue
                if total is not None and token(total, cap=2 * 10 ** 12) != inp + out:
                    continue
            except ValueError:
                continue
            if inp + out == 0:
                continue
            key = "trae:%s:%s" % (session, text(turn) or str(identifier))
            if key in seen:
                continue
            seen.add(key)
            usage = dict(input=inp, output=out, cachedInput=cached,
                         cacheWriteInput=write, reasoning=reasoning)
            normalized = record(key, None, end, usage, text(display) or text(model) or text(config))
            output.append(("trae", str(path) + "#" + session, normalized))
        from trae_timing_reference import apply
        return apply(db, path, output)
    finally:
        db.close()


def collect(paths=()):
    seen, output = set(), []
    for path in paths:
        output.extend(read_trae(path, seen))
    return output


def check_fixtures(root):
    root = Path(root)
    failures = []
    for case in json.loads((root / "trae-expected.json").read_text())["files"]:
        got = [r for _, _, r in read_trae(root / case["path"])]
        if got != case["responses"]:
            failures.append("%s: expected %r, got %r" % (case["path"], case["responses"], got))
    from trae_timing_reference import check_fixtures as check_timing
    failures.extend(check_timing(root))
    from test_trae_integration import check_fixtures as check_integration
    failures.extend(check_integration(root))
    return failures


if __name__ == "__main__":
    import sys
    if len(sys.argv) > 1:
        for _, _, row in collect(sys.argv[1:]):
            print(json.dumps(row))
    else:
        errors = check_fixtures(Path(__file__).resolve().parents[1] / "fixtures")
        print("Trae reference: %d failures" % len(errors))
        for error in errors:
            print(error)
        sys.exit(bool(errors))
