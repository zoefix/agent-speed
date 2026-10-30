#!/usr/bin/env python3
"""Reference server for the Agent Speed Live Report API (spec/REPORTING.md), standard library only.

For local testing by Agent Speed developers and by services that implement the API: it serves the discovery
document, registers devices, verifies every signature, enforces the timestamp window, nonce cache, sequence
rules, size and rate limits, and prints every report it accepts. State is in memory and lost on exit.

  python3 spec/tools/report_server.py                       http://127.0.0.1:8787
  python3 spec/tools/report_server.py --api-path /agent-speed --api-key-mode required --api-key secret-123
  python3 spec/tools/report_server.py --tls-cert cert.pem --tls-key key.pem --public-url https://test.local:8787
  python3 spec/tools/report_server.py --websocket            also offer GET {api}/v1/reports/ws (METRICS 11.9)
  python3 spec/tools/report_server.py --perf-score           advertise and check the perfScore extension (METRICS 11.10)
  python3 spec/tools/report_server.py --task-score           advertise only the retired taskScore (thinking speed only)

Then, in another terminal, the reference client:

  python3 spec/tools/report_reference.py client --url http://127.0.0.1:8787 --username demo --reports 3

GET / shows a plain-text table of the devices and their summed deltas (with --perf-score also the model leaderboard
pooled from every accepted perfStats row with the METRICS 6.9 formula, whose JSON is GET {api}/public/models). Not for
production use.
"""

from __future__ import annotations

import argparse
import json
import ssl
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import execution_modes_reference as modes  # noqa: E402
import report_reference as ref  # noqa: E402
import report_server_ws as ws  # noqa: E402


def build_service(args) -> ref.ReferenceService:
    public = args.public_url or f"{'https' if args.tls_cert else 'http'}://{args.host}:{args.port}"
    rules = {"minLength": args.min_length, "maxLength": args.max_length, "charset": args.charset,
             "spaces": not args.no_spaces}
    limits = {**ref.DEFAULT_LIMITS, "intervalSeconds": args.interval}
    service = ref.ReferenceService(authority=ref.authority_of(public), api_path=args.api_path, name=args.name,
                                   api_key_mode=args.api_key_mode, api_keys=tuple(args.api_key), rules=rules,
                                   limits=limits, log=lambda line: print(line, flush=True),
                                   extensions=extensions(args),
                                   websocket=websocket_settings(args))
    service.public_scheme = public.split("://", 1)[0]
    return service


def extensions(args) -> dict | None:
    # Every extension at its newest version: executionModes 2 also carries the reasoning effort.
    chosen = {name: max(modes.VERSIONS.get(name, (1,))) for name in ref.EXTENSIONS} if args.extensions else {}
    if args.perf_score:
        chosen["perfScore"] = 1
    if args.task_score:
        chosen["taskScore"] = 1
    return chosen or None


def websocket_settings(args) -> dict | None:
    if not args.websocket:
        return None
    given = {"minIntervalMs": args.ws_min_interval, "maxInFlight": args.ws_max_in_flight,
             "ackTimeoutMs": args.ws_ack_timeout, "pingIntervalSeconds": args.ws_ping_interval,
             "pongTimeoutSeconds": args.ws_pong_timeout}
    return {key: value for key, value in given.items() if value is not None}


class Handler(BaseHTTPRequestHandler):
    server_version = "AgentSpeedReferenceServer/1"
    service: ref.ReferenceService
    lock = threading.Lock()
    verbose = False
    sessions: dict = {}
    ws_options: dict = {}

    def log_message(self, fmt, *args):
        if self.verbose:
            sys.stderr.write(f"{self.address_string()} {fmt % args}\n")

    def _send(self, status: int, headers: dict, payload, content_type: str = "application/json") -> None:
        data = b"" if payload is None else (payload if isinstance(payload, bytes)
                                            else json.dumps(payload, ensure_ascii=False).encode("utf-8"))
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        for name, value in headers.items():
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(data)

    def _dispatch(self) -> None:
        method = self.command
        if method == "GET" and self.path in ("/", "/status"):
            self._send(200, {}, self._status_page().encode("utf-8"), "text/plain; charset=utf-8")
            return
        if (method == "GET" and self.service.websocket
                and self.path.split("?")[0] == self.service.api_path + "/v1/reports/ws"):
            self._websocket()
            return
        if method == "GET" and self.path.split("?")[0] == "/.well-known/agent-speed-report":
            with self.lock:
                document = self.service.discovery(self.service.public_scheme)
            self._send(200, {"Cache-Control": "max-age=60"}, document)
            return
        body = b""
        if method in ("POST", "PATCH", "PUT", "DELETE"):
            if "chunked" in self.headers.get("Transfer-Encoding", "").lower():
                self._send(411, {}, {"error": {"code": "length_required"}})
                return
            length = self.headers.get("Content-Length")
            if length is None and method != "DELETE":
                self._send(411, {}, {"error": {"code": "length_required"}})
                return
            try:
                size = int(length or 0)
            except ValueError:
                self._send(400, {}, {"error": {"code": "bad_request", "field": "Content-Length"}})
                return
            avatar_upload = (method == "PUT" and self.path == self.service.api_path + "/v1/profile/avatar"
                             and ref.supported(self.service.extensions, "profileAvatar"))
            body_limit = 512 * 1024 if avatar_upload else self.service.limits["maxBodyBytes"]
            if size < 0 or size > body_limit:
                status = 400 if size < 0 else 413
                code = "bad_request" if size < 0 else ("avatar_too_large" if avatar_upload else "payload_too_large")
                self._send(status, {"Connection": "close"}, {"error": {"code": code}})
                self.close_connection = True
                return
            body = self.rfile.read(size)
        headers = {name: value for name, value in self.headers.items()}
        with self.lock:
            status, extra, payload = self.service.handle(method, self.path, headers, body,
                                                         client_ip=self.client_address[0])
            if self.verbose and self.path.endswith("/v1/reports") and status == 200:
                print(json.dumps(json.loads(body), ensure_ascii=False, indent=2), flush=True)
        if status >= 400 and self.verbose:
            print(f"  -> {status} {payload['error']['code']}", flush=True)
        content_type = next((v for k, v in extra.items() if k.lower() == "content-type"), "application/json")
        self._send(status, {k: v for k, v in extra.items() if k.lower() != "content-type"}, payload, content_type)

    def _websocket(self) -> None:
        """REPORTING-WEBSOCKET 4: every v1 check before 101, then one session until the connection ends."""
        headers = {name: value for name, value in self.headers.items()}
        problem = ws.upgrade_problem(headers)
        if problem is None and "?" in self.path:
            problem = "the upgrade path takes no query"
        if problem:
            self._send(400, {}, {"error": {"code": "bad_request", "message": problem}})
            return
        with self.lock:
            status, extra, payload = self.service.handle("GET", self.path, headers, b"",
                                                         client_ip=self.client_address[0])
        if status != 101:
            if self.verbose:
                print(f"  -> upgrade refused {status} {payload['error']['code']}", flush=True)
            self._send(status, extra, payload)
            return
        # RFC 6455 4.1: the 101 is an HTTP/1.1 response; BaseHTTPRequestHandler answers HTTP/1.0 by default.
        self.protocol_version = "HTTP/1.1"
        self.send_response(101)
        self.send_header("Upgrade", "websocket")
        self.send_header("Connection", "Upgrade")
        self.send_header("Sec-WebSocket-Accept", ws.accept_key(self.headers["Sec-WebSocket-Key"].strip()))
        self.send_header("Sec-WebSocket-Protocol", ws.SUBPROTOCOL)
        self.end_headers()
        self.wfile.flush()
        self.close_connection = True
        ws.Session(self, self.service, self.lock, payload["deviceId"], self.headers.get("Authorization"),
                   self.sessions, self.ws_options).run()

    def _status_page(self) -> str:
        with self.lock:
            devices = list(self.service.devices.items())
            online = {id(d) for d in self.service.online()}
        lines = [f"{self.service.name} - {len(devices)} device(s)", "",
                 f"{'username':<24} {'online':<7} {'activity':<8} {'tok/s':>9} {'output':>12} {'requests':>9} "
                 f"{'cost US$':>12}"]
        for device_id, device in sorted(devices, key=lambda item: item[1]["username"]):
            last = device.get("last") or {}
            totals = device["totals"]
            lines.append(f"{device['username']:<24} {'yes' if id(device) in online else 'no':<7} "
                         f"{last.get('activity', '-'):<8} {last.get('live', {}).get('outputTokS', 0):>9.1f} "
                         f"{totals.get('output', 0):>12} {totals.get('requests', 0):>9} "
                         f"{totals.get('costMicroUsd', 0) / 1e6:>12.6f}")
        if self.service.perf_board:
            with self.lock:
                online = [i for i, d in self.service.devices.items() if id(d) in online]
                board = self.service.perf_board.leaderboard(online)
            score = board["live"]["score"]
            lines += ["", f"perfScore: {'-' if score is None else score} ({board['live']['form']}), "
                          f"thinking {board['live']['thinkingTokS']:.1f} tok/s over online devices; pooled scores "
                          f"need 20 responses", "",
                      f"{'model':<28} {'ranked':<7} {'score':>7} {'n':>7} {'tools':>7} {'T tok/s':>8} {'L ms':>7} "
                      f"{'live':>7}"]
            for row in board["models"]:
                live = row["live"] or {}
                t, latency = row["components"]["T"], row["components"]["L"]
                live_score = ("-" if live.get("score") is None
                              else f"{live['score']}{'' if live['form'] == 'live' else '*'}")
                lines.append(f"{row['id']:<28} {'yes' if row['ranked'] else 'no':<7} "
                             f"{'-' if row['shown'] is None else row['shown']:>7} {row['n']:>7} {row['tools']:>7} "
                             f"{'-' if t is None else format(t['measure'], '.1f'):>8} "
                             f"{'-' if latency is None else format(latency['measure'], '.0f'):>7} {live_score:>7}")
        return "\n".join(lines) + "\n"

    do_GET = do_POST = do_PATCH = do_PUT = do_DELETE = _dispatch


def main() -> int:
    # Usernames are Unicode; Windows consoles and pipes otherwise use a legacy code page and fail mid-request.
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="backslashreplace")
    parser = argparse.ArgumentParser(description="Agent Speed Live Report API reference server")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8787)
    parser.add_argument("--public-url", help="the service URL clients use (default http(s)://host:port); its "
                                             "host and port are the signed authority")
    parser.add_argument("--api-path", default="", help="path prefix of the API, e.g. /agent-speed")
    parser.add_argument("--name", default="Agent Speed reference server")
    parser.add_argument("--api-key-mode", choices=("none", "optional", "required"), default="none")
    parser.add_argument("--api-key", action="append", default=[], help="an accepted API key (repeatable)")
    parser.add_argument("--charset", choices=("unicode", "ascii"), default="unicode")
    parser.add_argument("--min-length", type=int, default=2)
    parser.add_argument("--max-length", type=int, default=32)
    parser.add_argument("--no-spaces", action="store_true")
    parser.add_argument("--interval", type=int, default=5, help="intervalSeconds in the discovery document")
    parser.add_argument("--tls-cert")
    parser.add_argument("--tls-key")
    parser.add_argument("--verbose", action="store_true", help="print every accepted report in full")
    parser.add_argument("--extensions", action="store_true",
                        help="enable every optional extension: modes, profiles, pairing, perfScore, taskScore")
    parser.add_argument("--perf-score", action="store_true",
                        help="advertise and check the perfScore extension (METRICS 11.10)")
    parser.add_argument("--task-score", action="store_true",
                        help="advertise the retired taskScore extension (clients send the thinking speed only)")
    parser.add_argument("--websocket", action="store_true", help="offer the WebSocket transport (METRICS 11.9)")
    parser.add_argument("--ws-min-interval", type=int, help="minIntervalMs in the discovery document")
    parser.add_argument("--ws-max-in-flight", type=int, help="maxInFlight in the discovery document")
    parser.add_argument("--ws-ack-timeout", type=int, help="ackTimeoutMs in the discovery document")
    parser.add_argument("--ws-ping-interval", type=int, help="pingIntervalSeconds in the discovery document")
    parser.add_argument("--ws-pong-timeout", type=int, help="pongTimeoutSeconds in the discovery document")
    parser.add_argument("--ws-ready-limits", type=json.loads, default={},
                        help='test: limits that ready announces instead, as JSON, e.g. \'{"maxInFlight": 4}\'')
    parser.add_argument("--ws-drop-answers", type=int, default=0, help="test: send no answer to every Nth report")
    parser.add_argument("--ws-answer-delay", type=int, default=0, help="test: answer every report this many ms late")
    args = parser.parse_args()
    Handler.service = build_service(args)
    Handler.verbose = args.verbose
    Handler.ws_options = {"readyLimits": args.ws_ready_limits, "dropAnswers": args.ws_drop_answers,
                          "answerDelayMs": args.ws_answer_delay}
    httpd = ThreadingHTTPServer((args.host, args.port), Handler)
    scheme = "http"
    if args.tls_cert:
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        context.load_cert_chain(args.tls_cert, args.tls_key)
        httpd.socket = context.wrap_socket(httpd.socket, server_side=True)
        scheme = "https"
    if args.host not in ("127.0.0.1", "::1", "localhost"):
        print("warning: listening beyond loopback; this server keeps no data safe and is for testing only")
    service = Handler.service
    print(f"listening on {scheme}://{args.host}:{args.port}  signed authority {service.authority!r}  "
          f"api path {service.api_path or '/'}  api key {service.api_key_mode}"
          f"{'  extensions ' + ','.join(sorted(service.extensions)) if service.extensions else ''}"
          f"{'  websocket ' + json.dumps(service.websocket) if service.websocket else ''}", flush=True)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
