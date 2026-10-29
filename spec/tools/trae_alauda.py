"""Read-only research decoder for locally observed Alauda raw-DEFLATE/CBOR logs.

No raw record is printed or persisted by this module. Records may contain private
content; consumers must project only identifiers, timestamps and usage metadata.
This independent reference recognizes only the locally verified Alauda format.
"""
from collections import Counter
from calendar import isleap
from datetime import datetime, timedelta, timezone
from pathlib import Path
import struct
import zlib

MAX_BYTES = 128 * 1024 * 1024


class CBOR:
    def __init__(self, data):
        self.data, self.pos = data, 0

    def take(self, size):
        if size < 0 or self.pos + size > len(self.data):
            raise EOFError("Incomplete CBOR item")
        out = self.data[self.pos:self.pos + size]
        self.pos += size
        return out

    def read(self, depth=0):
        if depth > 64:
            raise ValueError("CBOR depth bound")
        byte = self.take(1)[0]
        major, argument = byte >> 5, byte & 31
        if argument < 24:
            value = argument
        elif argument in (24, 25, 26, 27):
            width = 1 << (argument - 24)
            raw = self.take(width)
            value = int.from_bytes(raw, "big")
        else:
            raise ValueError("Indefinite CBOR not observed")
        if major == 0:
            return value
        if major == 1:
            return -1 - value
        if major == 2:
            return self.take(value)
        if major == 3:
            return self.take(value).decode("utf-8", errors="strict")
        if major == 4:
            return [self.read(depth + 1) for _ in range(value)]
        if major == 5:
            return {self.read(depth + 1): self.read(depth + 1) for _ in range(value)}
        if major == 6:
            return self.read(depth + 1)
        if major == 7:
            if argument in (20, 21):
                return argument == 21
            if argument in (22, 23):
                return None
            if argument in (25, 26, 27):
                return struct.unpack({25: ">e", 26: ">f", 27: ">d"}[argument], raw)[0]
            return value
        raise ValueError("Unknown CBOR type")


def string(value):
    return value.decode("utf-8", errors="replace") if isinstance(value, bytes) else value


def time_ms(value):
    if not isinstance(value, list) or len(value) != 9:
        return None
    if any(type(x) is not int for x in value):
        return None
    year, ordinal, hour, minute, second, nanos, offset_h, offset_m, offset_s = value
    if not (1 <= year <= 9999 and 1 <= ordinal <= 365 + isleap(year) and 0 <= hour < 24
            and 0 <= minute < 60 and 0 <= second < 60 and 0 <= nanos < 1_000_000_000
            and abs(offset_h) < 24 and abs(offset_m) < 60 and abs(offset_s) < 60):
        return None
    offset = timedelta(hours=offset_h, minutes=offset_m, seconds=offset_s)
    try:
        dt = datetime(year, 1, 1, hour, minute, second, tzinfo=timezone(offset)) + timedelta(days=ordinal - 1)
        return int(dt.timestamp()) * 1000 + nanos / 1_000_000
    except (ValueError, OverflowError):
        return None


def read(path, limit=MAX_BYTES, with_size=False):
    path = Path(path)
    before = path.stat()
    if path.is_symlink() or before.st_size > 32 * 1024 * 1024:
        raise ValueError("Compressed log exceeds bound")
    data = path.read_bytes()
    after = path.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise RuntimeError("Log changed during read; retry")
    if data[:4] != b"AalG" or data[8:12] != b"Atab":
        raise ValueError("Unknown Alauda container")
    offset = int.from_bytes(data[16:24], "little")
    if offset != 152 or any(data[24:152]):
        raise ValueError("Unsupported Alauda segment table")
    if data[offset:offset + 4] != b"AALG":
        raise ValueError("Unknown Alauda segment")
    version_length = int.from_bytes(data[offset + 16:offset + 20], "little")
    if version_length > 1024:
        raise ValueError("Invalid version length")
    start = offset + 20 + version_length
    inflater = zlib.decompressobj(-15)
    if limit <= 0:
        raise ValueError("Decoded log budget exhausted")
    decoded = inflater.decompress(data[start:], limit + 1)
    if len(decoded) > limit or inflater.unconsumed_tail:
        raise ValueError("Decoded log exceeds bound")
    if inflater.unused_data:
        raise ValueError("Multiple Alauda segments need explicit handling")
    parser = CBOR(decoded)
    records = []
    while parser.pos < len(decoded):
        try:
            item = parser.read()
        except EOFError:
            break  # an active stream can end with an incomplete final item
        if isinstance(item, dict):
            records.append(item)
    return (records, len(decoded)) if with_size else records


def summaries(records):
    return {
        "records": len(records),
        "span_names": dict(Counter(string(x["name"]) for x in records if "name" in x)),
        "field_keys": dict(Counter(k for x in records for k in x.get("fields", {}))),
        "tag_keys": dict(Counter(k for x in records for k, _ in x.get("tags", []))),
    }


if __name__ == "__main__":
    import json
    import sys
    for name in sys.argv[1:]:
        print(json.dumps(summaries(read(name)), ensure_ascii=False))
