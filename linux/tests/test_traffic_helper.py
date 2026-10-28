"""Run against the opt-in, installed helper: python3 linux/tests/test_traffic_helper.py."""
import os
import socket
import threading
import unittest

SOCKET = "/run/agent-speed-traffic/helper.sock"


def counters():
    with socket.socket(socket.AF_UNIX) as peer:
        peer.settimeout(5)
        peer.connect(SOCKET)
        data = b""
        while not data.endswith(b"END\n"):
            chunk = peer.recv(65536)
            if not chunk:
                raise RuntimeError("Incomplete helper response")
            data += chunk
    for line in data.decode().splitlines()[1:-1]:
        pid, _, rx, tx = map(int, line.split())
        if pid == os.getpid():
            return rx, tx
    return 0, 0


@unittest.skipUnless(os.path.exists(SOCKET), "optional traffic helper is not enabled")
class HelperTests(unittest.TestCase):
    def test_send_errors_do_not_count_as_bytes(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
            before = counters()
            with self.assertRaises(OSError):
                udp.send(b"no destination")
            self.assertEqual(counters(), before)

    def test_nonblocking_errors_do_not_count_as_bytes(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
            udp.bind(("127.0.0.1", 0))
            udp.setblocking(False)
            before = counters()
            for _ in range(10):
                with self.assertRaises(BlockingIOError):
                    udp.recv(1024)
            self.assertEqual(counters(), before)

    def test_udp_ipv4_and_ipv6_with_peek(self):
        for family, host in [(socket.AF_INET, "127.0.0.1"), (socket.AF_INET6, "::1")]:
            with self.subTest(family=family), socket.socket(family, socket.SOCK_DGRAM) as a, socket.socket(family, socket.SOCK_DGRAM) as b:
                a.bind((host, 0))
                b.bind((host, 0))
                a.settimeout(2)
                b.settimeout(2)
                before = counters()
                for _ in range(10):
                    a.sendto(b"a" * 1024, b.getsockname())
                    self.assertEqual(len(b.recv(1024, socket.MSG_PEEK)), 1024)
                    self.assertEqual(len(b.recv(1024)), 1024)
                    b.sendto(b"b" * 2048, a.getsockname())
                    self.assertEqual(len(a.recv(2048)), 2048)
                after = counters()
                self.assertEqual(tuple(y - x for x, y in zip(before, after)), (30720, 30720))

    def test_tcp_payload_and_nonblocking_error(self):
        with socket.socket() as server:
            server.bind(("127.0.0.1", 0))
            server.listen()
            with socket.create_connection(server.getsockname()) as client, server.accept()[0] as accepted:
                client.setblocking(False)
                before = counters()
                with self.assertRaises(BlockingIOError):
                    client.recv(1024)
                self.assertEqual(counters(), before)
                client.setblocking(True)
                client.settimeout(3)
                accepted.settimeout(3)
                size = 2 * 1024 * 1024

                def echo():
                    remaining = size
                    while remaining:
                        block = accepted.recv(min(65536, remaining))
                        if not block:
                            return
                        remaining -= len(block)
                    accepted.sendall(b"b" * size)

                worker = threading.Thread(target=echo, daemon=True)
                worker.start()
                client.sendall(b"a" * size)
                remaining = size
                while remaining:
                    remaining -= len(client.recv(min(65536, remaining)))
                worker.join(timeout=4)
                self.assertFalse(worker.is_alive())
                after = counters()
                self.assertEqual(tuple(y - x for x, y in zip(before, after)), (2 * size, 2 * size))


if __name__ == "__main__":
    unittest.main(verbosity=2)
