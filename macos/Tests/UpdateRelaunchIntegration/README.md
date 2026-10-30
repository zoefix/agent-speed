# Update relaunch integration

Run `python3 macos/scripts/test-update-relaunch.py` from the repository root on macOS with Xcode and a
working AppKit/LaunchServices session. This is a separate process test, outside the `swift test` targets.

The runner compiles the current production updater sources in a fresh scratch package, records their SHA256
hashes, and creates two ad-hoc-signed fixture bundles with a unique bundle identifier and UserDefaults suite.
A loopback feed serves version 1.1.0 to the 1.0.0 fixture. The real installer validates and replaces the bundle;
the real relaunch helper must wait for normal termination, then automatically start the new version.

The old delegate returns `.terminateLater` and approves termination from the main dispatch queue, exercising
the same asynchronous approval pattern used by the app's leaving report. The test requires that approval and
normal termination complete, then verifies the new PID, version and consumption of the one-time update flags.
Calling termination directly from the updater's MainActor task deadlocks this case; wrapping it in another
main-dispatch work item also deadlocks it.

Both fixture processes have a ten-second watchdog that exits only its own process. The host bounds build and
process waits; it never signals the installed app. Bundles, logs, source hashes and JSON results remain under
`artifacts/macos-update-relaunch/run-<uuid>/` for inspection. Each test-only preferences suite is named in the
results and may remain in the user's preferences directory. No shared preferences, caches or app data are removed.

`--repository`, `--fixtures` and `--output` can override the source checkout, fixtures and output parent.
