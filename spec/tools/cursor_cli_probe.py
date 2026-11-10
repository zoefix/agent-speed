#!/usr/bin/env python3
"""Looks for token counts in a Cursor CLI chat store (spec/research/cursor-cli.md).

  python3 spec/tools/cursor_cli_probe.py STORE_DB NUMBER [NUMBER ...]

STORE_DB is `<config dir>/chats/<md5 of the workspace path>/<chat id>/store.db` (or an ACP session's
`<config dir>/acp-sessions/<session id>/store.db`). The NUMBERs are the token counts the same turn reported on stdout,
e.g. `usage.outputTokens` and `usage.inputTokens` of `agent -p --output-format json`. Every blob is decoded as
protobuf (every varint, nested messages included) or read as JSON text; the tool prints the blobs and fields that hold
one of the numbers, and the step timings it saw. Exit status 0 when a number was found, 1 when none was.
"""
import sqlite3
import sys

STEP_TIMES = {
    ".1.2": "assistant text start",
    ".1.3": "assistant text end",
    ".2.59": "tool call start",
    ".2.60": "tool call end",
    ".3.3": "thinking start",
    ".3.4": "thinking end",
}


def varint(data, i):
    value = shift = 0
    while True:
        byte = data[i]
        i += 1
        value |= (byte & 0x7F) << shift
        shift += 7
        if byte < 0x80:
            return value, i


def fields(data, depth=0):
    """(field, kind, value) triples, or None when the bytes are not a protobuf message."""
    out, i = [], 0
    try:
        while i < len(data):
            key, i = varint(data, i)
            number, wire = key >> 3, key & 7
            if number == 0:
                return None
            if wire == 0:
                value, i = varint(data, i)
                out.append((number, "varint", value))
            elif wire in (1, 5):
                i += 8 if wire == 1 else 4
            elif wire == 2:
                size, i = varint(data, i)
                chunk = data[i:i + size]
                i += size
                if i > len(data):
                    return None
                inner = fields(chunk, depth + 1) if size and depth < 16 else None
                out.append((number, "message", inner) if inner else (number, "bytes", chunk))
            else:
                return None
    except IndexError:
        return None
    return out


def varints(tree, path=""):
    for number, kind, value in tree:
        here = f"{path}.{number}"
        if kind == "varint":
            yield here, value
        elif kind == "message":
            yield from varints(value, here)


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    wanted = {int(n) for n in sys.argv[2:]}
    found = False
    timings = {}
    with sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True) as db:
        for blob_id, data in db.execute("SELECT id, data FROM blobs"):
            data = bytes(data)
            if data[:1] == b"{":
                text = data.decode("utf-8", "replace")
                hits = sorted(n for n in wanted if f":{n}" in text.replace(" ", ""))
                if hits:
                    found = True
                    print(f"{blob_id[:12]} JSON holds {hits}")
                continue
            tree = fields(data)
            if tree is None:
                continue
            for path, value in varints(tree):
                if value in wanted:
                    found = True
                    print(f"{blob_id[:12]} protobuf field {path} = {value}")
                if path in STEP_TIMES and 1_000_000_000_000 < value < 10_000_000_000_000:
                    timings[STEP_TIMES[path]] = timings.get(STEP_TIMES[path], 0) + 1
    for name, count in sorted(timings.items()):
        print(f"step timing: {name} ×{count}")
    print("token counts found" if found else "none of the numbers is stored")
    sys.exit(0 if found else 1)


if __name__ == "__main__":
    main()
