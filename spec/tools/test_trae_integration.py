#!/usr/bin/env python3
"""Exercise the shared reference CLI with synthetic Trae data and empty other roots."""
import contextlib
from datetime import datetime, timezone
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import reference as engine
import task_score
import traffic


class FixtureClock(datetime):
    @classmethod
    def now(cls, tz=None):
        value = cls(2026, 10, 20, 13, tzinfo=timezone.utc)
        return value.astimezone(tz) if tz is not None else value


class TraeIntegrationTests(unittest.TestCase):
    fixtures = Path(__file__).resolve().parents[1] / "fixtures"

    def run_cli(self, *options):
        database = self.fixtures / "trae/timing-valid/ModularData/ai-agent/database.db"
        with tempfile.TemporaryDirectory() as empty:
            arguments = ["reference.py", "--trae", str(database), "--trae", str(database)]
            # Every optional discovery path is explicitly isolated, including home and environment settings.
            for source in ("codex", "claude", "dsh", "opencode", "gemini", "omp", "pi", "hermes",
                           "antigravity", "workbuddy", "workbuddy-ai", "zcode", "kimi", "grok", "openclaw", "droid"):
                arguments.extend(["--" + source, str(Path(empty) / source)])
            arguments.extend(options)
            output = io.StringIO()
            environment = {"HOME": empty, "PATH": os.environ.get("PATH", "")}
            with patch.object(sys, "argv", arguments), patch.object(engine, "datetime", FixtureClock), \
                    patch.dict(os.environ, environment, clear=True), contextlib.redirect_stdout(output):
                engine.main()
            return output.getvalue()

    def test_today_preserves_usage_and_marks_no_task_support(self):
        rows = [json.loads(line) for line in self.run_cli("--dump-today").splitlines()]
        responses = [row for row in rows if row.get("source") == "trae"]
        self.assertEqual(3, len(responses))
        self.assertEqual(340, sum(row["output"] for row in responses))
        measured = [row for row in responses if row["startMs"] is not None]
        self.assertEqual(1, len(measured))
        self.assertEqual(8000, measured[0]["endMs"] - measured[0]["startMs"])
        summary = next(row["summary"] for row in rows if "summary" in row)
        self.assertEqual({"trae": 3}, summary["counts"])
        self.assertEqual(0, summary["taskAgents"])
        self.assertEqual("trae", task_score.SOURCE_ORDER[-1])
        self.assertNotIn("trae", task_score.SCORED_SOURCES)

    def test_history_preserves_measured_usage_without_duplicate_roots(self):
        rows = [json.loads(line) for line in self.run_cli("--dump-history", "--utc").splitlines()]
        self.assertEqual(1, len(rows))
        self.assertEqual(("trae", 3, 340, 240, 8000),
                         tuple(rows[0][key] for key in ("source", "requests", "output", "measuredOutput", "measuredMs")))

    def test_unpriced_live_includes_trae(self):
        output = self.run_cli("--dump-unpriced", "--live", "--source", "trae")
        self.assertIn("source id=trae requests=3 ", output)
        self.assertIn("row source=trae reason=noModel ", output)

    def test_unpriced_history_includes_trae(self):
        output = self.run_cli("--dump-unpriced", "--first", "2026-10-19", "--last", "2026-10-21", "--source", "trae")
        self.assertIn("source id=trae requests=3 ", output)
        self.assertIn("row source=trae reason=noModel ", output)

    def test_traffic_registry_retains_source_without_inventing_attribution(self):
        self.assertEqual(task_score.SOURCE_ORDER, traffic.SOURCES)
        self.assertEqual(engine.diagnostics.SOURCES, traffic.SOURCES)
        for platform, executable in (("macos", "/Applications/Trae CN.app/Contents/MacOS/Trae CN"),
                                     ("windows", "C:/Program Files/Trae CN/Trae.exe")):
            self.assertIsNone(traffic.match_source(traffic.Env({"platform": platform}), {"exe": executable}, []))
        # A generic stored source row must survive serialization without needing a process matcher.
        meter = traffic.Engine({})
        meter.day(0)["sources"]["trae"] = dict(traffic._zero_source(), rxSelf=123)
        self.assertEqual(("trae", 123), (meter.history()[-1]["key"], meter.history()[-1]["rx"]))


def check_fixtures(root):
    TraeIntegrationTests.fixtures = Path(root)
    result = unittest.TestResult()
    unittest.defaultTestLoader.loadTestsFromTestCase(TraeIntegrationTests).run(result)
    return ["%s: %s" % (test.id(), detail) for test, detail in result.failures + result.errors]


if __name__ == "__main__":
    unittest.main()
