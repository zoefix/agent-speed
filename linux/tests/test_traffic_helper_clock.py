import os
from pathlib import Path
import shlex
import subprocess
import tempfile
import unittest


class TrafficHelperClockTests(unittest.TestCase):
    def test_boottime_clock_and_idle_deadline(self):
        root = Path(__file__).resolve().parents[2]
        source = root / "linux" / "tests" / "traffic_helper_clock_test.c"
        helper = (root / "linux" / "traffic-helper" / "helper.c").read_text()
        self.assertIn("traffic_helper_boottime", helper)
        self.assertNotIn("time(NULL)", helper)
        with tempfile.TemporaryDirectory(prefix="agent-speed-clock-") as temporary:
            executable = Path(temporary) / "traffic-helper-clock-test"
            compiler = shlex.split(os.environ.get("CC", "cc"))
            subprocess.run(
                compiler + ["-std=c11", "-Wall", "-Wextra", "-Werror", str(source), "-o", str(executable)],
                check=True,
            )
            subprocess.run([str(executable)], check=True)


if __name__ == "__main__":
    unittest.main()
