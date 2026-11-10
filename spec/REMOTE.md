# Agent Speed — Remote Hosts Specification

Agent Speed can watch agents that run on other machines the user reaches with SSH: a Codex, Claude Code or any other
supported agent working on a remote Linux server (an SSH terminal session, or a desktop app such as Codex driving an
app-server on the host). The desktop app starts a small read-only program on the host, the **relay**, over the user's
own `ssh`. The relay reads that host's transcripts with exactly the rules of `spec/METRICS.md` and streams normalized
numbers back: responses, agent states and daily history. Conversation content never leaves the host.

This document is the contract between the relay (`linux/relay`, C++ on the Linux core) and the desktop apps. The UI is
DESIGN 13. Status: implemented by the macOS, Windows and Linux desktop apps; platform differences are noted inline.

## 1. Principles

- **Same numbers.** A response measured on a host has exactly the tokens, interval, cost class and modes it would have
  if Agent Speed ran there. Speed comes from the timestamps in the host's logs (METRICS 3.3), never from the time a
  message crossed the network, so transfer delays and reconnects never create or remove speed.
- **Numbers only.** The relay sends what METRICS already keeps: token counts, times, model ids, provider ids, mode
  facts, project names (the last component of the working directory), the full working directory (METRICS 10.6,
  metadata), transcript paths as agent ids, tool names with input digests (METRICS 6.10) and daily aggregates. Never a
  prompt, message, tool input or output, file content or command.
- **The user's SSH.** The system OpenSSH client with the user's configuration, keys, agent and jump hosts. Agent Speed
  stores no password, key or passphrase and never answers a prompt; a host that needs a password or an unknown host
  key fails with a clear status (4.5).
- **Read-only, no network** — with the quota exceptions of section 11 (user's choice 2026-11-10). The relay opens files
  read-only (databases as METRICS 5 says) and writes only inside its own folder on the host (4.2), except the Claude
  Code status line of a host where the user turned the bridge on (11.3). It opens network connections only for the
  quota requests of 11.4, which the app's switches allow. It runs another program only as that status line helper,
  passing the user's previous status line through.
- **Supported hosts**: Linux on `x86_64` and `aarch64`, any distribution (the relay is a static executable). Other
  systems report `unsupportedSystem` (4.5).

## 2. Hosts

### 2.1 Settings

Persisted as one JSON value in the settings store (key `remoteHosts`), written atomically on every change (Linux: the
JSON text as a string in the `[General]` group of the app's QSettings file):

```json
{
  "version": 1,
  "hosts": [
    { "id": "8C0D6B0A-…", "destination": "devbox", "name": null, "enabled": true, "relayFolder": null }
  ]
}
```

| Field | Rule |
| --- | --- |
| `id` | UUID (upper case, as `UUID().uuidString`), never reused. `local` is reserved for this computer. |
| `destination` | What the user would type after `ssh`: a `Host` alias of `~/.ssh/config`, `host`, `user@host`, or `ssh://[user@]host[:port]`. 1–255 bytes of printable ASCII without space, `"`, `'`, `` ` ``, `\`, `;`, `&`, `|`, `$`, `<`, `>`, `(`, `)`, `*`, `?`, `!`, `{`, `}`, `#` and not starting with `-`. Anything else is refused by the editor and, when read from settings, disables the host (`invalidDestination`). |
| `name` | Display name, 1–32 characters after trimming; `null` = automatic: the host part of `destination` (without `ssh://`, user and port). |
| `enabled` | Off: no connection; the host's history stays visible. |
| `relayFolder` | Folder on the host for the relay and its cache; `null` = `~/.cache/agent-speed`. Must be `~/…` or absolute, 1–200 bytes of `[A-Za-z0-9._/~-]`, contain no `..` component and no `//`. Anything else is refused like `destination`. |

| `quotaClaudeCli` | The host's Claude Code status line bridge (11.3); absent = false. |

At most **8** hosts. Unknown fields from a newer version are kept. Duplicated destinations are allowed (two users on
one machine) but get distinct automatic names (`name (2)`).

### 2.2 Identity

Every agent, response, history row and display row carries a **host**: `local`, or the `id` of a remote host. A host
removed from settings keeps its id in the history store with the name it had (2.4) until its history is deleted.

- **Agent ids** of a host's agents are `ssh://<host id>/` followed by the agent id the relay sends (an absolute path
  for transcript files, so the id contains `//`). The app never interprets the part after the prefix except for
  display fallbacks (the last path component).
- **Response keys** are not prefixed: a key identifies the same model call on any machine (3.4 of METRICS).
- **Project names** are the relay's (the last component of the working directory on the host).

### 2.3 Master switch

The network master switch (`spec/NETWORK.md`) also governs SSH: while it is off no `ssh` process runs, every host shows
`networkOff`, and the hosts' agents leave the live views (their responses keep counting in today's totals).

### 2.4 Removing a host

Removing a host stops its connection immediately and removes it from every orb and board selection (DESIGN 2b, 2c; a
selection left with no host becomes `all`). Its daily history is kept unless the user ticks *Also delete its usage
history* in the confirmation (DESIGN 13), which deletes the host's rows (8.3). The relay files on the host are left in
place (the app may not be able to reach the host); the confirmation says where they are. A host whose quota bridge is
on is first asked to restore its status line (11.3); `remote.quota.removeNote` in the confirmation says what stays
if it cannot be reached.

## 3. The relay program

### 3.1 Build

`linux/relay` builds `agent-speed-relay`, linked fully statically against musl (Alpine), for `x86_64` and `aarch64`.
It contains the Linux core's parsers, monitor, history analyzer and price book; it does not contain reporting,
traffic sampling, the demo driver or ICU. The macOS app bundles both executables as
`Contents/Resources/relay/agent-speed-relay-linux-x86_64` and `…-aarch64`; their SHA-256 is computed by the app at
run time from the bundled files (the bundle signature protects them). The Linux app installs them as
`<prefix>/share/agent-speed/relay/agent-speed-relay-linux-<arch>` next to its `bin` folder (the AppImage's `usr/`,
`/opt/agent-speed` for the bundled .deb, `/usr` for the system .deb, protected by the package); a development build
also finds them in `relay/` beside the executable or in the folder `AGENT_SPEED_RELAY_DIR` names. The Windows app
embeds both in `AgentSpeed.exe` as resources `AgentSpeed.Relay.linux-<arch>` with a build-time `.sha256` (the in-app
updater replaces only the executable, so the relays always match it); the SHA-256 is computed from the resource bytes,
which must equal the build-time digest, and the upload hashes the bytes it sends; `AGENT_SPEED_RELAY_DIR` overrides them.

### 3.2 Command line

| Command | Behaviour |
| --- | --- |
| `agent-speed-relay --version` | Prints one line `agent-speed-relay <app version> protocol <n>` and exits 0. |
| `agent-speed-relay serve --folder <path>` | Runs the protocol of section 5 on stdin/stdout until stdin closes, then exits 0. `<path>` is its folder (2.1, already expanded by the remote shell); the history cache lives there. |
| `agent-speed-statusline --folder <path>` | The same executable run under that name (11.3): the status line helper of QUOTAS 5.5, with its files in `<path>/quota/`. Claude Code runs it; it never speaks the protocol. |

Anything else prints a usage line to stderr and exits 2. The relay never reads a terminal, ignores `SIGPIPE` (a write
error ends it) and exits when stdin reaches end of file, so a dropped connection never leaves it running.

### 3.3 Roots

The relay reads the default roots of every source on the host (METRICS 2) from the environment the SSH command runs
in (`HOME`, `CODEX_HOME`, `CLAUDE_CONFIG_DIR`, … — the variables a non-interactive SSH command sees): each source's
automatic entry. The location lists of the desktop app (METRICS 2.1) never apply to hosts. The set of enabled sources
is the app's (5.3 `start`).

### 3.4 Limits

Steady state below 1 % of one core on an idle host; memory bounded by the same retention as the desktop monitor plus
the encoded backlog of one history batch; at most one history analysis at a time, on a second thread at low priority
(`nice` 10), with at most 2 workers.

## 4. Connecting

### 4.1 The `ssh` command

The app runs `/usr/bin/ssh` (macOS and Linux; the system client only) with an argument vector, never a local shell
(Linux: `posix_spawn` with three non-blocking pipes, read on the connections' own thread):

```
/usr/bin/ssh -T -o BatchMode=yes -o ConnectTimeout=15 -o ServerAliveInterval=15 -o ServerAliveCountMax=3
    -o Compression=yes -o ControlMaster=no -o ClearAllForwardings=yes -o ForwardAgent=no -o ForwardX11=no
    -o PermitLocalCommand=no -o RequestTTY=no -o LogLevel=ERROR -- <destination> <remote command>
```

Windows: `%SystemRoot%\System32\OpenSSH\ssh.exe` (every option above is accepted), started with `CreateProcessW` on
overlapped named pipes (it stalls on synchronous ones) with the probe's stdin closed at once, and ended by closing stdin,
then `TerminateProcess` after 2 s.

`StrictHostKeyChecking` is left to the user's configuration (unknown keys then fail in batch mode, 4.5). The process
environment is the app's plus `LC_ALL=C` (stable error text). stderr is read with a 64 KiB cap and only used for the
status (4.5); it is never logged verbatim (it may contain user names and host names: the log keeps the status code).

Remote commands are fixed strings that the remote login shell runs; the only interpolated parts are the relay folder
(validated, 2.1, inserted unquoted so that `~` expands), the file name (3.1, 4.2: `[A-Za-z0-9._-]` only) and
integers. Each command is `sh -c '<script>' agent-speed <arguments>`, a form that bash, zsh, fish, dash and tcsh accept.

**Development hook.** In debug builds only, the environment variable `AGENT_SPEED_SSH_CONFIG=<absolute path>` adds
`-F <path>` before `--` to every invocation, so tests and the probe (`AgentSpeedProbe --remote`, 10) reach a test
host without the user's SSH configuration. Release builds ignore it, and nothing in the UI or the settings sets it.

### 4.2 Install

The relay lives on the host as `<folder>/agent-speed-relay-<version>-<arch>-<hash12>` where `<arch>` is `x86_64` or
`aarch64` and `<hash12>` the first 12 hex digits of its SHA-256.

1. **Probe**: `uname -s -m`, then the SHA-256 of every existing `agent-speed-relay-*` in the folder (`sha256sum`, else
   `shasum -a 256`, else `openssl dgst -sha256 -r`). Not `Linux` → `unsupportedSystem`; machine not `x86_64` /
   `amd64` / `aarch64` / `arm64` → `unsupportedSystem`; no hash tool → `cannotVerify`.
2. If the expected file exists with the expected hash, go to 4.3.
3. **Upload**: one command creates the folder (`umask 077`, `mkdir -p`), writes stdin to `<folder>/.upload-<pid>`,
   `chmod 700`, hashes it and prints the hash, then renames it to the final name only when the hash matches the
   expected one passed as an argument (else deletes it and exits 3). The app streams the bundled file and compares the
   printed hash too. Mismatch → `uploadFailed`. Then it deletes every other `agent-speed-relay-*` file and
   `.upload-*` file in the folder (only these names).
4. The app never uploads anything else and never deletes anything else.

### 4.3 Run

`<folder>/<file> serve --folder <folder>` through `exec`, so the relay is the SSH session's process. First the folder
is set to mode 700 and the file must be owned by the user (`chmod 700 <folder>` and `[ -O <file> ]`, exit 126
otherwise), and the upload step does the same `chmod` after `mkdir -p`: a folder another user owns (a shared `/tmp`
path) fails instead of letting that user swap the verified file before it runs. Exit status 126 or
a `Permission denied` / `cannot execute` message → `notExecutable` (a `noexec` home: the user picks another
`relayFolder`).

### 4.4 Life cycle

- A host connects when it is enabled, the master switch is on and the app is running; one connection per host.
- **Reconnect** after any failure with back-off 5, 10, 20, 40, 60 s (then every 60 s); immediately after the Mac wakes
  from sleep or its network comes back (Linux: a suspend shows as `CLOCK_BOOTTIME` running at least 5 s ahead of
  `CLOCK_MONOTONIC`; the network's return comes from `QNetworkInformation`); the back-off resets after a connection that stayed up for 60 s. Errors that
  need the user (`authFailed`, `hostKeyUnknown`, `unsupportedSystem`, `notExecutable`, `cannotVerify`,
  `uploadFailed`, `versionMismatch`, `sshMissing`) retry every 5 minutes, and immediately when the user presses *Retry*
  or edits the host; `invalidDestination` never retries (only an edit changes it). Waking from sleep does not retry
  these.
- Quitting the app, disabling or removing a host closes the relay's stdin and terminates `ssh` (SIGTERM, SIGKILL after
  2 s).
- A protocol violation (5.6) ends the connection with `protocolError` and normal back-off.
- **Silence**: the relay writes at least a `clock` reply every 60 s (5.3 `ping`); 150 s without any line ends the
  connection as `unreachable`. The other way round, the relay stops with `error silence` when no line has come from the
  app for 150 s after `start` (the app pings every 30 s, 6), so a relay whose network was cut does not stay behind
  until the host's TCP gives up.

### 4.5 Status

| Status | When | Shown (DESIGN 13) |
| --- | --- | --- |
| `connecting` | `ssh` running, no `hello` yet (probe, upload included) | progress |
| `installing` | uploading the relay | progress |
| `seeding` | `hello` received, no `ready` yet | progress with the relay's fraction |
| `connected` | after `ready` | live |
| `unreachable` | exit 255 with `connect to host ` (any reason, even `Permission denied`: a local firewall), `Could not resolve`, `timed out`, `Connection refused`, `Connection reset`, `No route`, `Network is unreachable`, `Connection closed`, or nothing but white space on stderr (`LogLevel=ERROR` hides *Connection to … closed by remote host*; the relay never exits with 255); or silence (4.4) | retrying |
| `authFailed` | `Permission denied` (exit 255) without `connect to host ` | needs the user |
| `hostKeyUnknown` | `Host key verification failed` | needs the user: connect once with `ssh <destination>` in Terminal |
| `unsupportedSystem`, `cannotVerify`, `uploadFailed`, `notExecutable` | 4.2, 4.3 | needs the user |
| `versionMismatch` | `hello.protocol` differs from the app's | needs an app update (the relay is replaced on the next connect) |
| `protocolError` | 5.6 | retrying |
| `exited` | anything else, with the exit status | retrying |
| `sshMissing` | `/usr/bin/ssh` absent | needs the user |
| `networkOff`, `disabled` | 2.3, 2.1 | — |

## 5. Protocol

### 5.1 Framing

Newline-delimited JSON objects in UTF-8 on the relay's stdout (relay → app) and stdin (app → relay). Each object has
a string `t` (its type). A line is at most 1 MiB (app → relay: 4 MiB, for the price table); a longer line is a
violation. Unknown types and unknown fields are ignored (forward compatibility); a missing required field makes the
line invalid. Times are Unix milliseconds of the host's clock (integers). Numbers follow METRICS 3.6; token values
follow METRICS 3.2 (truncated, clamped to 0 … 10^12) on both sides.

`protocol` is **1**. The relay of each app version speaks exactly that version's protocol; the app refuses another
(`versionMismatch`).

### 5.2 Relay → app

| `t` | Fields | Meaning |
| --- | --- | --- |
| `hello` | `protocol`, `version` (app version string), `arch`, `now` | sent as soon as a valid `start` is read (only `clock` replies to earlier pings may precede it) |
| `progress` | `value` (0–1) | seeding progress, at most every 250 ms |
| `agent` | `a` (agent object, 5.4) | the agent's complete current state; replaces the previous one |
| `agentRemoved` | `id` | the agent left (source disabled, file gone, no line any more) |
| `parents` | `links` (object: child id → parent id) | the complete resolved parent map (METRICS 6.7), sent whenever it changes |
| `response` | `r` (response object, 5.4), `completion` (bool), `project` (string, empty when the agent names none: the app then uses the agent's fallback) | an upsert (METRICS 4.4): `completion` is true for the first emission of a key. Only responses that end at or after `min(todayStartMs, now − 30 min)` are sent (the app keeps nothing older live) |
| `tool` | `agent`, `source`, `callMs`, `resultMs`, `failed` (bool or null), `model` (string or null), `seeded` | a tool call whose first result was read (METRICS 11.10 *Counting*) |
| `ready` | — | the startup backlog has been read: every line before was seeded (5.5) |
| `status` | `sources` (array of `{source, available, sessions, unsupported, configuredModes}` for the enabled sources only), `malformed`, `unreadable`, `partialHistory` | source availability on the host; sent after `ready` and whenever it changes, at most once per second. `sessions` follows METRICS/DESIGN 5 (`sessionCounts`) for the app's `todayStartMs` |
| `clock` | `id`, `now` | reply to `ping` |
| `historyDay` | `day`, `source`, `aggregate`, `digest` | one day and source of the host's daily history (8); a day and source that has performance records but no aggregate (a tool result read after midnight) is sent with an empty aggregate (all zero, no models) so its records travel |
| `historyPerf` | `day`, `source`, `model`, `record` | one performance record (METRICS 6.11) of that day |
| `historyDone` | `days` (count sent), `analyzed` (files) | end of a history answer |
| `historyProgress` | `value` (0–1) | analysis progress, at most once per second |
| `error` | `code`, `detail` (≤ 200 chars, no paths) | a fatal problem (`badStart`, `historyFailed`, `internal`, `silence`); the relay exits after it unless `code` is `historyFailed` |

### 5.3 App → relay

| `t` | Fields | Meaning |
| --- | --- | --- |
| `start` | `protocol`, `todayStartMs`, `sources` (array of METRICS 1 keys), `zone` (`{id, tzif}`: the IANA id and the base64 TZif data of the app's time zone), `prices` (the current price table, METRICS 9.1, as JSON) | first line; the relay answers `hello`, then seeds |
| `ping` | `id` (integer) | the relay answers `clock` immediately (from its reading thread) |
| `today` | `todayStartMs` | local midnight changed (day change, time zone change) |
| `prices` | `table` | a new price table: live responses read from now on and the next history run use it |
| `zone` | `id`, `tzif` | the app's time zone changed (the next history run recomputes, 8.1) |
| `history` | `digests` (object `"<day>/<source>"` → digest) | run the history analysis and send every day whose digest differs from or is missing in `digests` (8.2) |
| `watch` | `on` (bool) | live reporting's watch (METRICS 11.10): `tool` lines are sent only while on |

The relay ignores lines that arrive before a valid `start` except `ping`, and stops with `error badStart` when the
first line is not a valid `start` within 30 s.

### 5.4 Objects

**Agent** (METRICS 4 state, the Linux core's `AgentInfo` plus parser fields):

| Field | Type |
| --- | --- |
| `id` | string ≤ 4096 bytes: the host's agent id (transcript path or the source's id) |
| `source` | METRICS 1 key |
| `project` | string ≤ 256 or null |
| `cwd` | string ≤ 4096 or null (METRICS 10.6) |
| `model` | string ≤ 256 or null |
| `subagent` | bool |
| `phase` | `idle`, `generating`, `tools` |
| `inFlightStartMs`, `lastLineMs` | integer or null |
| `modes`, `sessionModes`, `configuredModes` | arrays of mode facts (EXECUTION-MODES: `kind`, `value`, `rawValue`, `evidence`, `provider`, `scope`), each ≤ 20 facts and validated by its rules |
| `actions` | the last ≤ 64 actions (METRICS 6.10 *Storage*): `{tool, digest, callMs, resultMs, failed, model, seeded}`, `digest` 16 lowercase hex digits, `tool` ≤ 128 |

**Response** (METRICS 4 record): `key` (≤ 512), `source`, `agent` (agent id), `model`, `startMs`, `endMs`, `firstMs`,
`input`, `cachedInput`, `cacheWriteInput`, `cacheWrite1hInput`, `output`, `reasoning`, `requestCount` (1–100 000),
`modes`, `final`, `provider` (≤ 64 or null), `fast`. No cost: the app prices every response with its own price book
(METRICS 9.3), so live costs are identical for every host.

**History aggregate**: the Linux core's day aggregate encoding (`history_store.cpp`, `encode`): `requests`, `input`,
`cachedInput`, `cacheWriteInput`, `output`, `reasoning`, `measuredOutput`, `measuredMs`, `unfinalized`, `agents`,
`costMicroUsd`, `unpricedRequests`, `models` (object model → `[requests, output, measuredOutput, measuredMs,
costMicroUsd, unpricedRequests]`), `unpriced` (array of `[model, provider, fast, noDate, count]`), `modes` (array of
`[model, kind, mode, evidence, provider, requests, output, measuredOutput, measuredMs]`). `digest` is the first 16 hex
digits of SHA-256 over the aggregate's exact JSON text followed by the JSON texts of that day and source's performance
records, ordered by model id as UTF-8 bytes, each text exactly as sent in its `historyPerf.record`, with no separators.

**Performance record**: METRICS 6.11 *Records*: `n`, `outputSum`, `durationMsSum`, `latencyBins` (64 integers),
`reasonN`, `reasonOutput`, `reasonDurationMsSum`, `cacheN`, `cacheInput`, `cacheCached`, `toolN`, `toolLnSum` (double),
`toolFailed`, `activeMs`.

### 5.5 Seeding and live

- After `start` the relay seeds exactly like the desktop monitor (METRICS 5: files modified since `todayStartMs −
  15 min` from offset 0, older ones from their end), sends the resulting agents, parents and responses, then `ready`.
- Every `response` before `ready` is *seeded*: the app stores it but emits no events and counts no live report
  (METRICS 6.4, 11.4). After `ready`, `response` lines are live and follow METRICS 6.4 (age rule included). A root that
  appears on the host later is seeded by the relay inside the live phase; its responses go out as live lines and the
  app's 120-second age rule (METRICS 6.4) keeps the old ones silent, as it does for a late local root.
- `parents` is the complete map; when it would exceed the 1 MiB line limit the relay keeps the links of the most
  recently active children that fit.
- The relay sends an `agent` line whenever any field of the agent changed since its last `agent` line, at most every
  250 ms per agent (the last state always goes out); `response` lines are never delayed.

### 5.6 Validation (app side)

The app treats everything from the relay as untrusted input:

- A line that is not a JSON object, exceeds 1 MiB, lacks a required field or has a wrong type is *invalid*: it is
  dropped and counted. More than 100 invalid lines in one connection end it with `protocolError`.
- Strings are bounded as 5.4 says and must not contain C0 or C1 control characters, U+2028, U+2029 or bidirectional
  controls (U+061C, U+200E, U+200F, U+202A–U+202E, U+2066–U+2069). Model ids are otherwise free text (real ids contain
  spaces and parentheses, e.g. `Gemini 3.1 Pro (High)`). Violations make the line invalid.
- A nullable field that is absent counts as null. `progress.value` outside 0–1 is clamped. More than 64 actions, 20
  facts in a mode list or 4096 entries in `history.digests` / `parents.links` make the line invalid; an `agent` line
  beyond the 4096-agent limit is dropped without counting as invalid.
- Integers outside −2^53 … 2^53 make the line invalid; token values are clamped (METRICS 3.2); times outside the
  years 2000–2100 make the line invalid.
- Limits per host: 4096 agents (a further `agent` line is dropped), 200 000 responses held for today and the retention
  window (older responses of the host are pruned first), 64 actions per agent, 20 facts per mode list.
- The app never uses a relay string as a path, URL, command or format string; it only displays it as plain text.

## 6. Clock

The relay's times come from the host's clock. The app estimates the offset `δ = remote − local` from `ping`/`clock`
pairs: `δ = now_remote − (t_send + t_receive) / 2` with the round-trip `t_receive − t_send`. It sends a ping right
after `hello` and every 30 s, keeps the last 8 samples and uses the one with the smallest round trip. Until the first
sample, `hello.now` gives `δ` (round trip unknown).

Every time in `agent`, `response` and `tool` lines is converted to local time as `t − δ` when the line is applied
(`δ` rounded to whole milliseconds; values that would leave 2000–2100 make the line invalid). Durations are
unchanged, so speeds are unchanged. History days (8) are labelled by the relay with the host's clock in the app's time
zone; the offset is not applied (a response within `|δ|` of midnight may fall on the neighbouring day). With
`|δ| ≥ 5 s` the host shows `clock.skewed` (DESIGN 13).

## 7. Live ingestion (app side)

- A host's lines are applied on the monitor's queue like any other source update (macOS `MonitorCore`), between
  reads of local files; they never block the UI.
- **Agents**: `agent` → the engine agent with the prefixed id (2.2), host, parent from `parents` (prefixed), and the
  agent's actions for the work signals and the score (METRICS 6.10, 6.9). `agentRemoved` removes it.
- **Responses**: `response` → an engine upsert with the prefixed agent id and the host, priced by the app (METRICS
  9.3), `emitsEvents` only after `ready`. **Cross-host deduplication**: a key the engine already holds for another host
  (or for local) is ignored, and so is a local response whose key the engine holds for a host — the first arrival wins
  (METRICS 3.4), so the same response visible on two machines (copied sessions, a desktop app that keeps a local
  copy of a remote session) counts once in live values.
- **Tools**: `tool` → METRICS 11.10 *Counting* while the watch is on.
- **Seeding**: lines before `ready` are applied with `emitsEvents = false`.
- **Reconnect**: a new connection seeds again. The host's agents and responses stay as they were until the new
  `ready`; then every agent of the host that the new connection did not send is removed. Responses are upserts by key,
  so seeding again never counts a response twice and never raises a peak: the live rate (METRICS 6.3) only takes the
  part of a response that lies inside the last 3 s of its own timestamps.
- **Disconnected**: when a connection ends, every agent of the host is set to `idle` without an in-flight start (no
  estimate keeps running for a host that cannot be seen), its responses keep counting, and the host's status changes.
  The agents leave the agents list by the usual rules (METRICS 6.7).
- **Monitor restart** (a settings change that re-reads local files, METRICS 5) keeps every host's received state and
  applies it again to the new monitor as seeded, without reconnecting.
- **Sources**: a disabled source is disabled for every host (the relay gets the enabled set in `start`; a change sends
  a new connection). Per-agent network traffic (METRICS 10) is local only: a host's agents have no traffic.
- **Live reporting** (METRICS 11) counts every host's responses like local ones: they are the user's agents.

## 8. History

### 8.1 On the host

The relay keeps its own incremental history store in `<folder>/history.sqlite` (the Linux core's store: per-file
contributions, key ownership, performance records, METRICS 8 rules unchanged). It labels days in the app's time zone
(`start.zone`, written to `<folder>/zone.tzif` and loaded from there; a different zone id recomputes every file, as the
Linux analyzer does), and prices with the app's current table (`start.prices`, `prices`), so costs and days match the
app's own history. Files deleted on the host keep their contributions there.

### 8.2 Sync

After `ready`, every 10 minutes and after the local midnight, the app sends `history` with the digests it holds for the
host (8.3). The relay runs its analysis, then sends `historyDay` (and its `historyPerf` lines, right after it) for every
day and source whose digest differs or is missing in the request, then `historyDone`. A day the relay has but the app
lacks is sent; a day the app has but the relay lacks (its cache was deleted, logs gone) is left alone: the app never
deletes a host's day because the relay did not send it. While a history run is in progress, a new `history` request
waits for it.

### 8.3 In the app

The app keeps a host's days in side tables of its history store (created on open, no schema version change, so older
builds ignore them):

- `remote_hosts(id TEXT PRIMARY KEY, name TEXT)` — the latest name of every host with history.
- `remote_days(host TEXT, day TEXT, source TEXT, data TEXT, digest TEXT, PRIMARY KEY(host, day, source))`.
- `remote_perf(host TEXT, day TEXT, source TEXT, model TEXT, data TEXT, PRIMARY KEY(host, day, source, model))`.

A `historyDay` replaces the row and deletes that day and source's `remote_perf` rows before the following
`historyPerf` lines are written; the batch commits at `historyDone` (a broken connection or a `historyFailed` error
rolls it back).

- **Time zone.** `remote_hosts` also keeps `zone`, the zone id the host's rows were computed in. When a sync runs in
  another zone (the Mac moved or the user changed it), the app sends no digests and the committed batch **replaces all
  of the host's rows** instead of merging, so no day is counted under two labels. Days whose logs are gone from the
  host and that its cache could not relabel are lost then, as they are for local history. Rows stored without a zone
  (by a build before this rule) take the zone of their next sync.
- **Bounds.** At most 100 000 `remote_days` rows and 1 000 000 `remote_perf` rows per host. A batch that would exceed
  them is discarded and the host shows `historyTooLarge` until the next successful sync; nothing already stored is
  changed. A `historyDay` with a day outside 2000-01-01 … 2100-12-31 is invalid (5.6).

The daily history (METRICS 8) is the local analysis plus every host's rows; each `DailyUsage` carries its host. The
performance history (METRICS 6.11) pools every host's records (the baseline compares a model with itself across the
user's machines). Day peaks stay the all-hosts, all-sources `peakToday` observed by the app. *Clear history* deletes the
side tables too; the next sync fills them again from the hosts' caches.

Known approximation: response keys are deduplicated within each host's analysis and within the local analysis, not
across them: a session copied from one machine to another counts once per machine in daily history (live values
deduplicate, 7).

## 9. Security

- No listening socket anywhere; the transport is the user's SSH session.
- The app runs only `/usr/bin/ssh` with an argument vector and validated values (2.1); remote commands are fixed
  strings with validated, unquoted-safe parts (4.1).
- The relay executable is the one bundled in the signed app, verified by SHA-256 on the host before it runs (4.2);
  the folder is created with mode 700 and the relay with mode 700.
- The relay is read-only (1) and writes only `<folder>/history.sqlite` (+ its journal), `<folder>/zone.tzif`,
  `<folder>/agent-speed-statusline` and `<folder>/quota/`. Its only exceptions are the quota bridge (11.3), which
  changes one value of the host's Claude Code settings and restores it, and the quota requests (11.4).
- A host agent's credential is read on the host, used there for one request to that agent's own vendor, and never
  sent to the app, logged or written.
- The app validates every relay line (5.6) and bounds memory per host; a hostile host can show wrong numbers for
  itself but cannot reach files, commands or other hosts' data on the Mac.
- Nothing about hosts goes to any other service: live reporting sends the same aggregate fields as for local agents
  (METRICS 11), never a host name, destination or path.

## 11. Quotas

Hosts show quotas like this computer (QUOTAS; user's choice 2026-11-10: "遠端主機的小程式也要安排上"). A host is a Linux
server with command-line agents, so the relay reads the CLI routes only: no Claude Desktop, Devin Desktop or
browsers, and no key the user pasted into the app.

| Source on a host | Route (QUOTAS) |
| --- | --- |
| `codex` | rollout `rate_limits` (3) |
| `claudeCode` | the status line bridge of the host (11.3), per host, off by default |
| `grokBuild` | the log (9.2); live when the app's `quotaGrokBuildLive` is on |
| `kimi`, `gemini`, `droid` | the vendor API with the host's own sign-in (9.5, 9.6, 9.7; Droid's key from the host's `~/.factory/.env` or the SSH command's environment) |
| `antigravity` | the `agy` language server on the host's loopback (9.4) |

Not on hosts:
- `claudeDesktop`, `devin`: desktop apps.
- `zcode`: its key lives in the app's secret store and never leaves this computer.
- `qoder`, `qoderCli`, `workbuddy`: browser sessions.

### 11.1 Protocol

- `start` gains an optional `quota` object, and the app sends a line `{"t": "quota", "config": {...}}` whenever it
  changes. The config is `{"sources": [<keys whose quota switch is on>], "network": <networkEnabled>, "grokLive":
  bool, "claudeBridge": bool}`.
- The relay answers with one line per source:
  - `{"t": "quota", "q": {source, observedMs, windows, running, signedIn, status}}` whenever it changes, at most once
    a second;
  - `{"t": "quotaRemoved", "source"}` when a source's snapshot goes.
- `windows` is an array of at most 16 `{kind, name, minutes, usedPercent, resetsAtMs}` (QUOTAS 2, 9.1).
  - `name` ≤ 64 characters or null.
  - `kind` one of the QUOTAS kinds.
  - `minutes` 1–527 040 or null.
  - `usedPercent` finite, 0–1000.
  - `resetsAtMs` a time or null.
- `status` is one of `ok`, `noCredential`, `expired`, `denied`, `failed`, `networkOff`, `noCertificates`,
  `notRunning`.
- The bridge reports `{"t": "quotaBridge", "state", "settingsPath"}`, with `state` from QUOTAS 5.4 (`off`,
  `waiting`, `ok`, `replaced`, `unreadable`, `failed`) and the path abbreviated with `~`.
- The app validates them by 5.6. An invalid quota line is dropped like any invalid line.
- An older relay ignores the new types (5.1), and an older app ignores the replies.

### 11.2 Running and signed in

The relay lists `/proc` every 15 s while `sources` is not empty: each process's `exe`, and for script hosts its
`cmdline`. Its agent processes match METRICS 10.5's rows. Signed in follows QUOTAS 6 and 9.1 with the host's files
(`$CODEX_HOME/auth.json`, `~/.claude.json` `oauthAccount`, and so on).

### 11.3 Claude Code bridge on a host

- `claudeBridge` comes from the host's `quotaClaudeCli` (2.1, a new boolean field, default false).
- **Helper.** At start, while it is true, the relay copies itself to `<folder>/agent-speed-statusline`: a temporary
  file, then rename, only when the bytes differ, mode 700. The upload step's clean-up (4.2) removes only
  `agent-speed-relay-*` and `.upload-*`, so the helper survives upgrades. Its name selects the helper mode (3.2).
- **Command**: `'<folder>/agent-speed-statusline' --folder '<folder>'`, with the folder absolute and quoted as
  QUOTAS 5.2.
- **Settings file**: `$CLAUDE_CONFIG_DIR/settings.json`, else `~/.claude/settings.json`, edited exactly as QUOTAS
  5.2–5.4. The sidecar and backup are `<folder>/quota/claude-statusline-previous.json` and
  `claude-settings.backup.json`; the quota file is `<folder>/quota/claude-cli.json`.
- **Turning off**: `claudeBridge` false, or the host removed from the app, restores as QUOTAS 5.3.
- **Removing a host** whose bridge is on first connects and turns it off (2.4). If the host cannot be reached, the
  confirmation says that its status line keeps running the helper in `<folder>` until the bridge is turned off from a
  new connection, or the user restores `claude-settings.backup.json`.

### 11.4 Requests from the host

- The routes of QUOTAS 9.1 apply on the host: only while `network` is true and the source is in `sources`; only the
  hosts of NETWORK 9; HTTPS with redirects refused, 10 s, 256 KiB; the same cadence. The relay only asks while the
  agent runs on the host (11.2).
- **TLS**: mbedTLS 3, statically linked. The CA certificates are the first that exists of:
  - `/etc/ssl/certs/ca-certificates.crt`;
  - `/etc/pki/tls/certs/ca-bundle.crt`;
  - `/etc/ssl/ca-bundle.pem`;
  - `/etc/ssl/cert.pem`;
  - `/etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem`.

  With none, the status is `noCertificates`.
- **Proxy**: `https_proxy`, else `HTTPS_PROXY`, from the SSH command's environment (an `http://host:port` proxy, used
  with `CONNECT`), unless `no_proxy` / `NO_PROXY` lists the host. The Antigravity loopback never uses a proxy.
- **Budget**: the relay stays below 3.4's 1 % of one core. A request is a few kilobytes every 5 minutes per running
  agent.

## 10. Tests

- **Relay**: protocol encoding unit tests; the quota fixtures of QUOTAS 10 through the relay's readers, its helper
  mode and its bridge against a temporary home; an end-to-end test that runs `agent-speed-relay serve` over the spec
  fixtures (`spec/fixtures`) as a fake home and checks that its responses and agent states equal
  `parser-expected.json` and its history equals `history-expected.json`.
- **App**: decoder validation (every rule of 5.6), clock estimation (6), ingestion (prefixing, cross-host
  deduplication, seeding without events, reconnect without double counting, disconnect to idle), history merge (8.3)
  and host subsets (DESIGN 13), all with a scripted relay transcript instead of `ssh`.
- **End to end**: a Linux container with `sshd` and fixture transcripts appended in real time, reached with a key
  from a test `ssh_config`: the Mac's probe (`AgentSpeedProbe --remote <destination> --watch N`) shows the host's
  agents and speed; killing the connection and reconnecting leaves today's totals unchanged.
