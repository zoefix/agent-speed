#!/usr/bin/env python3
"""End-to-end checks of the reference server's WebSocket endpoint (report_server.py --websocket), stdlib only."""

from __future__ import annotations

import base64
import json
import os
import socket
import struct
import sys
import threading
import time
import unittest
import uuid
from http.server import ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import report_reference as ref  # noqa: E402
import report_server  # noqa: E402
import report_websocket as rw  # noqa: E402


class Client:
    """A minimal RFC 6455 client: masked frames, text messages, Ping answered."""

    def __init__(self, port: int, headers: dict, path: str):
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=5)
        key = base64.b64encode(os.urandom(16)).decode()
        lines = [f"GET {path} HTTP/1.1", f"Host: 127.0.0.1:{port}", "Upgrade: websocket", "Connection: Upgrade",
                 f"Sec-WebSocket-Key: {key}", "Sec-WebSocket-Version: 13"]
        lines += [f"{name}: {value}" for name, value in headers.items()]
        self.sock.sendall(("\r\n".join(lines) + "\r\n\r\n").encode())
        self.buffer = b""
        head = self._until(b"\r\n\r\n")
        status_line, *header_lines = head.decode().split("\r\n")
        self.status = int(status_line.split()[1])
        self.headers = {k.lower(): v.strip() for k, v in (line.split(":", 1) for line in header_lines if ":" in line)}
        self.body = b""
        if self.status != 101:
            self.body = self._take(int(self.headers.get("content-length", "0")))

    def _until(self, marker: bytes) -> bytes:
        while marker not in self.buffer:
            chunk = self.sock.recv(65536)
            if not chunk:
                raise EOFError
            self.buffer += chunk
        head, _, self.buffer = self.buffer.partition(marker)
        return head

    def _take(self, size: int) -> bytes:
        while len(self.buffer) < size:
            chunk = self.sock.recv(65536)
            if not chunk:
                raise EOFError
            self.buffer += chunk
        data, self.buffer = self.buffer[:size], self.buffer[size:]
        return data

    def send(self, opcode: int, payload: bytes) -> None:
        mask = os.urandom(4)
        size = len(payload)
        header = bytes([0x80 | opcode])
        if size < 126:
            header += bytes([0x80 | size])
        elif size < 65536:
            header += bytes([0x80 | 126]) + struct.pack("!H", size)
        else:
            header += bytes([0x80 | 127]) + struct.pack("!Q", size)
        self.sock.sendall(header + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload)))

    def send_text(self, text: str) -> None:
        self.send(0x1, text.encode("utf-8"))

    def receive(self) -> tuple[int, bytes]:
        while True:
            first, second = self._take(2)
            size = second & 0x7F
            if size == 126:
                size = struct.unpack("!H", self._take(2))[0]
            elif size == 127:
                size = struct.unpack("!Q", self._take(8))[0]
            opcode, data = first & 0x0F, self._take(size)
            if opcode == 0x9:
                self.send(0xA, data)
                continue
            return opcode, data

    def message(self) -> dict:
        opcode, data = self.receive()
        if opcode != 0x1:
            raise AssertionError(f"expected text, got opcode {opcode} {data!r}")
        return rw.parse_service_message(data)

    def close_code(self) -> int:
        opcode, data = self.receive()
        if opcode != 0x8:
            raise AssertionError(f"expected close, got opcode {opcode} {data!r}")
        return struct.unpack("!H", data[:2])[0]


class WebSocketEndpointTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.httpd = ThreadingHTTPServer(("127.0.0.1", 0), report_server.Handler)
        cls.port = cls.httpd.server_address[1]
        cls.api = f"http://127.0.0.1:{cls.port}/agent-speed"
        report_server.Handler.service = ref.ReferenceService(authority=f"127.0.0.1:{cls.port}", api_path="/agent-speed",
                                                             websocket={"minIntervalMs": 50})
        report_server.Handler.service.public_scheme = "http"
        report_server.Handler.ws_options = {}
        threading.Thread(target=cls.httpd.serve_forever, daemon=True).start()
        cls.d = ref.test_private_key("websocket end-to-end")
        cls.device = str(uuid.uuid4())
        status, _, body = report_server.Handler.service.handle(
            "POST", "/agent-speed/v1/devices",
            ref.signed_headers(cls.d, "POST", cls.api + "/v1/devices", cls.device,
                               ref._registration(cls.device, "ws-test", ref.public_key(cls.d))),
            ref._registration(cls.device, "ws-test", ref.public_key(cls.d)))
        assert status in (200, 201), (status, body)

    @classmethod
    def tearDownClass(cls):
        cls.httpd.shutdown()
        cls.httpd.server_close()

    def connect(self, **changes) -> Client:
        headers = rw.handshake_headers(self.d, self.api, self.device)
        headers.update(changes)
        client = Client(self.port, {k: v for k, v in headers.items() if v is not None}, "/agent-speed" + rw.WS_PATH)
        self.addCleanup(client.sock.close)
        return client

    def body(self, run_id: str, seq: int, output: int = 5) -> bytes:
        document = ref.load_examples()["report-minimal"]
        now_ms = int(time.time() * 1000)
        document.update(deviceId=self.device, username="ws-test", run={"id": run_id, "seq": seq},
                        sentAt=ref.format_instant(now_ms))
        document["delta"]["output"] = output
        document["delta"]["measuredOutput"] = min(document["delta"]["measuredOutput"], output)
        document["realtime"] = rw.realtime("log", now_ms, 12)
        return ref.compact_json(document)

    def report(self, client: Client, body: bytes, **header_changes) -> dict:
        headers = ref.signed_headers(self.d, "POST", self.api + "/v1/reports", self.device, body)
        headers.update(header_changes)
        client.send_text(rw.report_message(headers, body))
        return client.message()

    def test_ready_ack_duplicate_and_tampered(self):
        client = self.connect()
        self.assertEqual(client.status, 101)
        self.assertEqual(client.headers["sec-websocket-protocol"], rw.SUBPROTOCOL)
        ready = client.message()
        self.assertEqual(ready["limits"], {**rw.DEFAULT_STREAM, "minIntervalMs": 50})
        run_id, body = str(uuid.uuid4()), None
        body = self.body(run_id, 1)
        ack = self.report(client, body)
        self.assertEqual((ack["type"], ack["status"], ack["seq"], ack["bodySha256"]),
                         ("ack", "accepted", 1, ref.sha256_hex(body)))
        self.assertEqual(self.report(client, body)["status"], "duplicate")
        signed = ref.signed_headers(self.d, "POST", self.api + "/v1/reports", self.device, body)
        tampered = body.replace(b'"seq":1', b'"seq":2')
        client.send_text(rw.report_message(signed, tampered))
        error = client.message()
        self.assertEqual((error["type"], error["code"], error["seq"], error["fatal"]),
                         ("error", "signature_invalid", 2, False))
        client.send_text('{"type":"hello"}')
        self.assertEqual(client.message()["code"], "bad_request")
        client.send(0x8, struct.pack("!H", 1000))
        self.assertEqual(client.close_code(), 1000)

    def test_a_refused_upgrade_answers_with_the_v1_error(self):
        client = self.connect(**{"X-AgentSpeed-Signature": "A" * 86})
        self.assertEqual(client.status, 401)
        self.assertEqual(rw.classify_rejection(client.status, client.body, None)["code"], "signature_invalid")
        client = self.connect(**{"Sec-WebSocket-Protocol": "other"})
        self.assertEqual(client.status, 400)

    def test_a_newer_connection_replaces_the_older(self):
        first = self.connect()
        first.message()
        second = self.connect()
        second.message()
        self.assertEqual(first.close_code(), 4009)
        run_id = str(uuid.uuid4())
        self.assertEqual(self.report(second, self.body(run_id, 1))["status"], "accepted")
        error = self.report(second, self.body(str(uuid.uuid4()), 1))
        self.assertEqual((error["code"], error["fatal"]), ("run_conflict", True))
        self.assertEqual(second.close_code(), 1008)


class WebSocketPerfScoreTests(unittest.TestCase):
    """The perfScore extension over the WebSocket transport: the same body checks, `perfScore` before `realtime`."""

    @classmethod
    def setUpClass(cls):
        cls.httpd = ThreadingHTTPServer(("127.0.0.1", 0), report_server.Handler)
        cls.port = cls.httpd.server_address[1]
        cls.api = f"http://127.0.0.1:{cls.port}/agent-speed"
        cls.service = ref.ReferenceService(authority=f"127.0.0.1:{cls.port}", api_path="/agent-speed",
                                           websocket={"minIntervalMs": 50}, extensions={"perfScore": 1})
        cls.service.public_scheme = "http"
        report_server.Handler.service = cls.service
        report_server.Handler.ws_options = {}
        threading.Thread(target=cls.httpd.serve_forever, daemon=True).start()
        cls.d = ref.test_private_key("websocket perf score")
        cls.device = str(uuid.uuid4())
        registration = ref._registration(cls.device, "ws-perf", ref.public_key(cls.d))
        status, _, body = cls.service.handle(
            "POST", "/agent-speed/v1/devices",
            ref.signed_headers(cls.d, "POST", cls.api + "/v1/devices", cls.device, registration), registration)
        assert status in (200, 201), (status, body)

    @classmethod
    def tearDownClass(cls):
        cls.httpd.shutdown()
        cls.httpd.server_close()

    def test_perf_score_bodies_are_acknowledged_or_rejected_by_field(self):
        report_server.Handler.service = self.service
        client = Client(self.port, rw.handshake_headers(self.d, self.api, self.device), "/agent-speed" + rw.WS_PATH)
        self.addCleanup(client.sock.close)
        self.assertEqual(client.status, 101)
        client.message()
        run_id = str(uuid.uuid4())

        def send(seq, change=None):
            document = ref.load_examples()["report-perfscore-websocket"]
            now_ms = int(time.time() * 1000)
            document.update(deviceId=self.device, username="ws-perf", run={"id": run_id, "seq": seq},
                            sentAt=ref.format_instant(now_ms), realtime=rw.realtime("mixed", now_ms, 18))
            if change:
                change(document)
            self.assertEqual(list(document)[-2:], ["perfScore", "realtime"])
            body = ref.compact_json(document)
            client.send_text(rw.report_message(ref.signed_headers(self.d, "POST", self.api + "/v1/reports",
                                                                  self.device, body), body))
            return client.message()

        self.assertEqual(send(1)["status"], "accepted")
        error = send(2, lambda d: d["models"][2]["perfStats"][0].update(toolFailed=2))
        self.assertEqual((error["type"], error["code"], error["httpStatus"]), ("error", "payload_invalid", 422))
        self.assertEqual(send(2)["status"], "accepted")
        rows = {row["id"]: row for row in self.service.perf_board.leaderboard([self.device])["models"]}
        self.assertEqual(rows["claude-sonnet-5-5"]["tools"], 2)


if __name__ == "__main__":
    unittest.main()
