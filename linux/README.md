# Agent Speed for Linux

The Linux desktop application reuses the same .NET metrics, parsers, pricing,
history, presentation models and reporting controllers as Windows. Avalonia hosts
the UI; Skia consumes the shared orb draw lists. The procedural shader calculations
are generated from the existing HLSL with `scripts/sync-shaders.py`.

## Supported environment

Ubuntu 24.04, GNOME 46, X11, x86-64. Choose **Ubuntu on Xorg** at login.
Wayland, KDE and ARM64 are not release targets yet. `--allow-unsupported` allows
experimentation on XWayland without placement or click-through guarantees.

## Build and run

Install the .NET 10 SDK, then:

```sh
dotnet test shared/tests/AgentSpeed.Core.Tests -c Release
dotnet test shared/tests/AgentSpeed.Presentation.Tests -c Release
dotnet test linux/tests/AgentSpeed.Linux.Tests -c Release
dotnet run --project linux/src/AgentSpeed.Linux
```

Use `--preview` after `--` for synthetic data without reading transcripts or
making network requests. Preview settings are ephemeral. Close hides the panel;
use the tray, GNOME extension or relaunch the application to reopen it. The
**Restore mouse input** action disables click-through on all orbs.

For deterministic UI checks, `xvfb-run -a sh linux/scripts/smoke-test.sh` runs in
fresh XDG directories and exports screenshots of the dashboard, history, settings
and all five instruments. Screenshot paths are printed by the script.
The history page uses the shared summary cards, calendar-day source stacks, average-speed line and mirrored traffic bars. A recorded single day (including zero usage) remains visible. Relative ranges follow local midnight; custom dates remain fixed. Loading and retry states preserve previously loaded history.

GNOME label width is bounded and long values are ellipsized; the menu retains the full configured label and cost/traffic details. `node --test linux/tests/test_gnome_extension.mjs` checks long-label handling, D-Bus owner changes and disable/enable races. Layout was also checked in an isolated GNOME 46 session at normal and doubled text size.

`dotnet run -c Release --project linux/tools/AgentSpeed.RenderBenchmark` measures
warm CPU rendering and allocations for idle/active single-orb and five-orb scenes.
It uses synthetic data and no desktop connection; hardware rendering should also
be profiled on the target machine. A compositor without GPU acceleration uses
native Skia geometry and a 15 fps cap. Its cloth and liquid textures are generated
from the original shader calculations at bounded resolution and refreshed at up
to 2 Hz; text and instrument geometry stay at full display resolution. GPU
compositors use the complete shader calculations every frame. Both paths reuse
the shared styles, color tokens, layout, effects model and live measurements.
The orb loads the bundled Inter font files directly, without relying on a system
font with the same family name.

`dotnet run -c Release --project linux/tools/AgentSpeed.RenderBenchmark -- --capture /tmp/agent-speed-orb-gallery`
exports idle/active reference images for all five original styles in both themes
and both rendering paths. The original visual references are in
`docs/images/orb-styles.png` and `docs/images/orbs.png`.

## Debian package

```sh
sudo apt-get install clang libbpf-dev libelf-dev zlib1g-dev linux-tools-common linux-tools-generic
sh linux/scripts/publish.sh
sudo apt-get install ./dist/AgentSpeed-Linux-x64.deb
```

The package includes the .NET runtime. To install a published release:

```sh
curl -fsSL https://raw.githubusercontent.com/zoefix/agent-speed/main/install-linux.sh | sh
```

The installer verifies `SHA256SUMS.txt` before handing the package to apt.
Application updates use the same checksum validation. Checksums are distributed
with releases; they detect corruption, not a compromised release publisher.

## GNOME integration

The package installs `agent-speed@zoefix.github.io`. Enable **Agent Speed** in the
GNOME Extensions app, or run `gnome-extensions enable agent-speed@zoefix.github.io`.
New system extensions may require logging out and back in to be discovered.
The extension displays fixed-width live numbers and opens the app through the
session D-Bus interface `io.github.zoefix.AgentSpeed1`. It reads no transcripts.
The application remains usable without the extension via its launcher and tray.

## Optional process traffic helper

After enabling the helper, run `python3 linux/tests/test_traffic_helper.py` to
check TCP, IPv4/IPv6 UDP, peek reads and nonblocking error accounting against
the running kernel probes. This test uses only local loopback connections.

Whole-machine counters require no privilege. In Settings → Network, **Install
traffic helper** asks Polkit to enable the packaged systemd socket. Installation
does not enable it automatically. The service starts on demand and exits after
120 idle seconds, detaching all eBPF probes. To disable:

```sh
sudo systemctl disable --now agent-speed-traffic.socket
sudo systemctl stop agent-speed-traffic.service
```

The helper uses libbpf CO-RE with BTF and tracing support. It counts successful
TCP/UDP socket send/receive payload bytes, excluding MSG_PEEK, without collecting
addresses, packets or content. These counters need not match NIC wire bytes.
It uses kernel peer credentials to return only the requesting UID's counters;
the client verifies that its peer is root. Process identity includes start time,
and a helper restart begins a new baseline. Kernel lockdown or denied BPF
permissions produce an unavailable status rather than fabricated zeros.

Kernel probe attachment, process-level byte accuracy, GNOME extension activation,
mixed-DPI/hotplug window behavior and privileged install/update flows must also
be checked on a real supported desktop before release. Xvfb does not validate
GNOME window-manager policy or Polkit.

## Data and privacy

- Settings: `$XDG_CONFIG_HOME/agent-speed/settings.json`
- History and keys: `$XDG_DATA_HOME/agent-speed/`
- Downloads and caches: `$XDG_CACHE_HOME/agent-speed/`

Unset or relative XDG base paths use the standard home-directory defaults.
Device secrets use a 0700 directory and 0600 atomically replaced files. They are
not encrypted at rest; protect your account and disk accordingly. Reporting is
off by default and uses the existing consent and signed-request flows. The
protocol schema now recognizes `Linux`; receiving services must accept it.
Turning off network connections cancels update, price, rate and reporting work.

Uninstall with `sh install-linux.sh --uninstall`; add `--purge` to remove the
invoking user's settings, history and keys. Other users' data is retained.
