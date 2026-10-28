# Linux module review — 2026-10-28

Scope: the Linux port in PR #19, with a second source review after the fixes. Application source reviewed through `c4ca9dc`. Findings have separate commits so they can be reviewed or reverted individually; follow-up integration and regression commits are retained.

| Module | Fixes or reviewed behavior | Verification |
| --- | --- | --- |
| Startup and platform lifecycle | Retry activation during bridge startup; stop initialization after shutdown; preserve pending positions before close/suspend; prevent orb synchronization reentry | Linux lifecycle/dispatcher tests; isolated X11 close and hide/show regression |
| Settings | Clear restored-input errors; normalize metric-specific redlines; validate absolute source folders; share the Claude folder editor; report autostart permission failures | UI smoke exercises production controls and synthetic write failures |
| Dashboard | Refresh network sampler state in settings; stop the hero animation timer while hidden or detached | Snapshot state transitions, page switching, Hide/Show assertions |
| History | Snapshot export filters before the file picker; handle denied/read-only destinations and successful retries | History/chart tests and UI export/recovery assertions |
| Orbs and rendering | Shared Add/Duplicate placement and display-layout restoration; refresh input regions after scaling; remove user-closed windows from live state | Geometry/layout tests; actual X11 Add/Duplicate/Remove, close, rapid hide/show and debounced-move preservation; five styles rendered with GPU and CPU paths |
| GNOME panel | Bounded label widths, owner replacement, stale replies, disable/re-enable, failure recovery | 3 Node regressions; isolated GNOME 46 checks from the earlier panel-fix iteration |
| Traffic/helper | Suspend-aware sample clock and elapsed idle timeout; response and identity validation reviewed | Native clock read, fake suspend gap, pure C idle-clock regression; helper compiled into package |
| Discovery and storage | Ignore relative XDG OpenCode roots; review file rotation, parsers, SQLite and compression paths | Core tests, including Linux file identity and OpenCode discovery |
| Reporting and updates | Handle key-store I/O failures; visible-tab polling; discard stale polls and older pairing operations; display unsupported download states | Fake transport and response-order tests; no live report submission |
| Packaging and CI | Resolve uninstall XDG paths before any action; add helper clock regression to Linux CI and release workflow | 4 fully stubbed installer tests; ShellCheck; shader sync; YAML parsing; final Debian package checks |
| Shared compatibility | Keep Linux changes scoped to Linux entry points or shared platform adapters | Core + Presentation suites; Windows Release cross-build |

## Results

- Core: **2,013 passed**.
- Presentation: **3,821 passed**.
- Linux: **68 passed**.
- Total: **5,902 .NET tests passed**, no failures or skipped cases reported by these suites.
- GNOME: **3 passed**; installer: **4 passed**; helper clock: **1 passed**.
- Windows Release cross-build: **0 warnings, 0 errors**.
- Source X11 smoke: passed, including the new interactive and orb lifecycle assertions.
- ShellCheck, shader synchronization and workflow YAML parsing: passed.
- Final Debian package: **1.4.1 / amd64**, checksum verified; the extracted self-contained executable passed the same isolated UI smoke, including all new lifecycle assertions.
- Spec fixtures, reporting contracts, 55 execution-mode cases, 19 reporting-extension tests and 13 vehicle definitions: passed.

The regression tests use temporary XDG directories, synthetic usage data, fake HTTP responses and stubbed installer commands. They do not submit reports, modify real credentials, remove user files or install the package.

## Reproduction commands

Run .NET build/test/publish operations serially:

```sh
dotnet test shared/tests/AgentSpeed.Core.Tests -c Release
dotnet test shared/tests/AgentSpeed.Presentation.Tests -c Release
dotnet test linux/tests/AgentSpeed.Linux.Tests -c Release
dotnet build windows/src/AgentSpeed -c Release -p:EnableWindowsTargeting=true
node --test linux/tests/test_gnome_extension.mjs
python3 linux/tests/test_installer.py
python3 linux/tests/test_traffic_helper_clock.py
python3 linux/scripts/sync-shaders.py --check
shellcheck install-linux.sh linux/scripts/*.sh linux/traffic-helper/build.sh linux/packaging/install-traffic-helper
xvfb-run -a sh linux/scripts/smoke-test.sh
sh linux/scripts/publish.sh
```

Publish last, then validate the extracted self-contained executable without rebuilding into the same output directories. Check its version, package checksums and `--smoke-test` in a fresh X11/D-Bus session with temporary XDG directories.

## Verification limits

This review found no additional confirmed issue in the paths reviewed after the fixes. It does not establish that the entire application is bug-free. Native Windows/macOS execution, physical mixed-DPI hotplug, actual machine suspend/resume, live reporting services, and installation of the privileged eBPF helper were not exercised in this round. Wayland remains outside the supported initial Linux target. No system package installation was performed.

Upstream GitHub Actions may require maintainer approval for a fork contribution; local results are separate from upstream CI status.
