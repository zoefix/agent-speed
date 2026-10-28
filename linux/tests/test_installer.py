#!/usr/bin/env python3
"""Installer boundary checks: real loopback downloads, isolated fake apt/sudo."""
import hashlib
import http.server
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import unittest

ROOT = Path(__file__).resolve().parents[2]


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="agent-speed-installer-")
        self.root = Path(self.temp.name)
        self.release = self.root / "release"
        self.release.mkdir()
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.log = self.root / "apt.log"
        for name, body in {
            "sudo": '#!/bin/sh\nexec "$@"\n',
            "apt-get": '#!/bin/sh\nprintf "%s\\n" "$@" >> "$AGENT_SPEED_TEST_APT_LOG"\n',
            "agent-speed": '#!/bin/sh\nexit 0\n',
        }.items():
            path = self.bin / name
            path.write_text(body)
            path.chmod(0o755)

        directory = str(self.release)

        class Handler(http.server.SimpleHTTPRequestHandler):
            def __init__(self, *args, **kwargs):
                super().__init__(*args, directory=directory, **kwargs)

            def log_message(self, *args):
                pass

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                        AGENT_SPEED_TEST_APT_LOG=str(self.log),
                        AGENT_SPEED_DOWNLOAD_BASE=f"http://127.0.0.1:{self.server.server_port}",
                        XDG_CONFIG_HOME=str(self.root / "config"), XDG_DATA_HOME=str(self.root / "data"),
                        XDG_CACHE_HOME=str(self.root / "cache"))

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        self.temp.cleanup()

    def run_installer(self, *args):
        return subprocess.run(["sh", str(ROOT / "install-linux.sh"), *args], env=self.env,
                              capture_output=True, text=True, timeout=15)

    def make_safe_uninstall_env(self, xdg_values):
        """Stub every external command reachable on the Linux uninstall path."""
        safe_bin = self.root / "safe-uninstall-bin"
        safe_bin.mkdir(exist_ok=True)
        logs = {name: self.root / f"{name}.log" for name in ("apt", "sudo", "agent", "rm")}
        scripts = {
            "uname": '#!/bin/sh\ncase "$1" in -s) echo Linux;; -m) echo x86_64;; *) exit 2;; esac\n',
            "id": '#!/bin/sh\n[ "$1" = -u ] || exit 2\necho 1000\n',
            "apt-get": '#!/bin/sh\nprintf "%s\\n" "$#" "$@" >> "$AGENT_SPEED_TEST_APT_LOG"\n',
            "sudo": '#!/bin/sh\nprintf "%s\\n" "$#" "$@" >> "$AGENT_SPEED_TEST_SUDO_LOG"\nexec "$@"\n',
            "agent-speed": '#!/bin/sh\nprintf "%s\\n" "$#" "$@" >> "$AGENT_SPEED_TEST_AGENT_LOG"\n',
            # Deliberately log arguments only: never delete files.
            "rm": '#!/bin/sh\nprintf "%s\\n" "$#" "$@" >> "$AGENT_SPEED_TEST_RM_LOG"\n',
        }
        for name, body in scripts.items():
            path = safe_bin / name
            path.write_text(body)
            path.chmod(0o755)

        env = dict(self.env)
        env["PATH"] = str(safe_bin) + os.pathsep + env["PATH"]
        env.update({
            "AGENT_SPEED_TEST_APT_LOG": str(logs["apt"]),
            "AGENT_SPEED_TEST_SUDO_LOG": str(logs["sudo"]),
            "AGENT_SPEED_TEST_AGENT_LOG": str(logs["agent"]),
            "AGENT_SPEED_TEST_RM_LOG": str(logs["rm"]),
        })
        for name in ("XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME"):
            env.pop(name, None)
        env.update(xdg_values)
        return env, logs

    @staticmethod
    def read_argument_calls(path):
        if not path.exists():
            return []
        lines = iter(path.read_text().splitlines())
        calls = []
        for count in lines:
            calls.append([next(lines) for _ in range(int(count))])
        return calls

    def run_installer_env(self, env, *args):
        return subprocess.run(["sh", str(ROOT / "install-linux.sh"), *args], env=env,
                              capture_output=True, text=True, timeout=15)

    def publish(self, correct=True, duplicate=False):
        body = b"installer-test-only, never passed to the real package manager"
        asset = "AgentSpeed-Linux-x64.deb"
        (self.release / asset).write_bytes(body)
        digest = hashlib.sha256(body if correct else b"corrupted").hexdigest()
        line = f"{digest}  {asset}\n"
        (self.release / "SHA256SUMS.txt").write_text(line * (2 if duplicate else 1))

    def test_verified_download_reaches_package_manager(self):
        self.publish()
        result = self.run_installer("--no-launch")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("install\n-y\n", self.log.read_text())

    def test_common_entrypoint_dispatches_to_local_linux_installer(self):
        result = subprocess.run(["sh", "install.sh", "--help"], cwd=ROOT, env=self.env,
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("install-linux.sh", result.stdout)

    def test_tampering_and_duplicate_checksums_never_reach_apt(self):
        for correct, duplicate in [(False, False), (True, True)]:
            self.publish(correct, duplicate)
            self.assertNotEqual(0, self.run_installer("--no-launch").returncode)
            self.assertFalse(self.log.exists())

    def test_uninstall_xdg_resolution_and_purge_arguments_are_fully_stubbed(self):
        # Preserve the real HOME from the test process; only XDG values vary.
        home = self.env["HOME"]
        self.assertTrue(home.startswith("/"), home)
        expected_default = {
            "XDG_CONFIG_HOME": f"{home.rstrip('/')}/.config",
            "XDG_DATA_HOME": f"{home.rstrip('/')}/.local/share",
            "XDG_CACHE_HOME": f"{home.rstrip('/')}/.cache",
        }
        cases = (
            ("absolute", {
                "XDG_CONFIG_HOME": str(self.root / "abs-config") + "/",
                "XDG_DATA_HOME": str(self.root / "abs-data") + "/",
                "XDG_CACHE_HOME": str(self.root / "abs-cache") + "/",
            }),
            ("absolute-root", {
                "XDG_CONFIG_HOME": "/",
                "XDG_DATA_HOME": "/",
                "XDG_CACHE_HOME": "/",
            }),
            ("relative", {
                "XDG_CONFIG_HOME": "relative-config",
                "XDG_DATA_HOME": "relative-data",
                "XDG_CACHE_HOME": "relative-cache",
            }),
            ("unset", {}),
        )
        for label, xdg_values in cases:
            with self.subTest(xdg=label):
                env, logs = self.make_safe_uninstall_env(xdg_values)
                for log in logs.values():
                    log.unlink(missing_ok=True)

                resolved = dict(xdg_values) if label.startswith("absolute") else expected_default
                child = lambda base, path: f"{base.rstrip('/')}/{path}"
                result = self.run_installer_env(env, "--uninstall")
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual([["-f", child(resolved["XDG_CONFIG_HOME"], "autostart/agent-speed.desktop")]],
                                 self.read_argument_calls(logs["rm"]))

                logs["rm"].unlink()
                result = self.run_installer_env(env, "--uninstall", "--purge")
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual([
                    ["-f", child(resolved["XDG_CONFIG_HOME"], "autostart/agent-speed.desktop")],
                    ["-rf", "--", child(resolved["XDG_CONFIG_HOME"], "agent-speed"),
                     child(resolved["XDG_DATA_HOME"], "agent-speed"),
                     child(resolved["XDG_CACHE_HOME"], "agent-speed")],
                ], self.read_argument_calls(logs["rm"]))
                self.assertEqual([["remove", "-y", "agent-speed"]] * 2,
                                 self.read_argument_calls(logs["apt"]))
                self.assertEqual([["apt-get", "remove", "-y", "agent-speed"]] * 2,
                                 self.read_argument_calls(logs["sudo"]))
                self.assertEqual([["--quit"], ["--quit"]],
                                 self.read_argument_calls(logs["agent"]))


if __name__ == "__main__":
    unittest.main()
