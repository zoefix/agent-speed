# Agent Speed — Quota Specification

Agent Speed shows how much of each subscription's usage limits the user has used: for every limit window an account
has (a 5-hour window, a weekly window, a weekly limit of one model), the used percentage and the time it resets. The
dashboard's Sources rows show every source that has a quota; the `character` orb's 額度 card shows the sources whose
app runs and is signed in. The UI is DESIGN 4 (Sources), 2a.6 (*Readouts*, 額度) and 5 (Data sources); the fixtures are
checked by `spec/tools/quota_reference.py --check-fixtures`. Status: specified 2026-11-10 (user's request); phase 1
covers Codex, Claude Desktop and Claude Code. Other sources follow in section 9.

## 1. Principles

- **Display only.** A quota never changes a speed, a score, a cost or anything Agent Speed reports. Quotas are not
  sent anywhere: they are not part of live reporting (REPORTING), the local network frame (LAN) or remote hosts
  (REMOTE).
- **Claude: only what the Claude apps leave on this computer, and the official status line.** Agent Speed never reads
  Claude credentials, OAuth tokens, cookies or session tokens, and never sends a request to claude.ai or Anthropic.
  Anthropic does not permit third-party developers to "collect, store, or intermediate Claude.ai credentials or session
  tokens" (Claude Code, *Legal and compliance*). This rules out the `api/oauth/usage` endpoint that some tools call with
  Claude Code's token. For the Claude CLI account (section 5), the only official source is the `rate_limits` field
  that Claude Code passes to a status line command. The Claude Desktop account (section 4) is read from the response
  Desktop itself cached; that cache is not a documented interface (section 8).
- **Dynamic windows.** Only the windows an account has are shown. Some plans have no 5-hour window (observed
  2026-11-10: a Claude Ultra account and a Codex `promax` plan with a weekly window only); such an account shows its
  weekly window alone, with no placeholder for the missing one.
- **Each account on its own.** Claude Desktop and the Claude CLI may be signed in to different accounts (the user's
  case), so `claudeDesktop` and `claudeCode` each carry their own snapshot and are never merged.
- **Phase 1 needs no network.** The Codex and Claude Desktop readers only read local files. The Claude CLI bridge
  writes one value into Claude Code's settings, and only after the user turns it on (section 5).

## 2. Model

A **snapshot** belongs to one source: `{source, observedMs, windows}`. `observedMs` is when the vendor computed the
values (section 3, 4, 5 say which time). A **window** is `{kind, name, minutes, usedPercent, resetsAtMs}`:

| `kind` | Meaning | Label (every language) |
| --- | --- | --- |
| `fiveHour` | the rolling 5-hour window | `5h` |
| `sevenDay` | the weekly window over every model | `7d` |
| `modelWeek` | a weekly window of one model or model family, `name` = the vendor's display name (`Fable`, `Opus`) | `<name> 7d` (`Fable 7d`) |
| `other` | any other length, `minutes` known | `<n>h` for whole hours below a day, else `<n>d` for whole days, else `<n>m` |

- `usedPercent` is a number; the bar draws `clamp(usedPercent, 0, 100)` and the text shows it rounded half up to an
  integer, clamped to 0…100 (`100%` once a window is exhausted, even when the vendor says 103).
- `resetsAtMs` may be null (unknown).
- `minutes` is 300 for `fiveHour`, 10080 for `sevenDay` and `modelWeek`, the vendor's value for `other`.
- **Order**: `fiveHour`, `sevenDay`, then `modelWeek` by `name` (ordinal, case-insensitive), then `other` by
  `minutes`.

**Reset passed.** When `now ≥ resetsAtMs`, the window has started over. It shows `0%` and no reset time until a newer
snapshot arrives, and its pace mark (DESIGN 4) is hidden. Claude Code itself drops a window from the status line once
its reset time passes (section 5), so a newer CLI snapshot simply lacks it.

**Age.** `age = now − observedMs`. A snapshot is never thrown away for its age. Its age shows once it is older than
1 hour (`quota.age`, DESIGN 4). The 額度 card (DESIGN 2a.6) only lists sources whose app runs and is signed in
(section 6).

## 3. Codex

Codex writes the account's limits into its rollouts with every token count (observed with Codex 0.163,
2026-11-10). The Codex parser (METRICS 4.2) passes each `event_msg` line whose `payload.type == "token_count"` and whose
`payload.rate_limits` is an object to the quota reader, unless the line is copied history (METRICS 3.5).

```json
{"timestamp": "2026-11-10T19:11:22.318Z", "type": "event_msg", "payload": {"type": "token_count", "info": {},
 "rate_limits": {"limit_id": "codex", "primary": {"used_percent": 23.0, "window_minutes": 10080,
 "resets_at": 1794897479}, "secondary": null, "credits": {}, "plan_type": "promax",
 "rate_limit_reached_type": null}}}
```

- Only `limit_id` `"codex"`, null or absent counts. Other buckets (`"premium"`, observed with both windows null) are
  ignored.
- `primary` and `secondary` are each a window when they are an object with a numeric `used_percent` and a numeric
  `window_minutes` above 0. A window of 0 minutes (observed) or a null one is skipped.
- Window kind by `window_minutes`:
  - 290–310 → `fiveHour`. Older Codex versions wrote 299.
  - 10000–10160 → `sevenDay`.
  - anything else → `other` with that length.
- Two windows of the same kind keep the first (`primary`).
- Reset time:
  - `resets_at`, epoch seconds, when it is a number.
  - Else `resets_in_seconds` (older Codex versions), counted from the line's timestamp.
  - Else null.
- `observedMs` is the line's timestamp. The reader keeps the snapshot with the newest `observedMs` across every Codex
  location. A token count with no usable window (both null) replaces nothing.
- Seeding reads the same lines, so the newest snapshot exists right after launch when the newest rollout is within
  the parser's retention (METRICS 6.1). An older one is gone. Its windows would have reset anyway.

Codex's `codex app-server` offers `account/rateLimits/read` (experimental). It is not used: the user chose the local
records (2026-11-10).

## 4. Claude Desktop

Claude Desktop asks claude.ai for the signed-in account's usage while it runs, about every 15 minutes (observed
11:26:13 → 11:43:25 on 2026-11-10), and its Chromium network stack caches each response on disk. Agent Speed reads that
cached response, read-only. It never reads cookies, `Local Storage`, `IndexedDB` or credentials, and never sends the
request itself.

### 4.1 Cache folders

| Platform | Folder |
| --- | --- |
| macOS | `~/Library/Application Support/Claude/Cache/Cache_Data` |
| Windows | `%APPDATA%\Claude\Cache\Cache_Data`; the Microsoft Store build also `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\Cache\Cache_Data` (unverified) |
| Linux | `$XDG_CONFIG_HOME/Claude/Cache/Cache_Data`, else `~/.config/Claude/Cache/Cache_Data` (unverified) |

### 4.2 Entry files

The folder is a Chromium *simple cache*. One entry is the file `<16 hex digits>_0`. The digits are the first 8 bytes
of the SHA-1 of the entry's key, read as a little-endian 64-bit number and printed in lowercase hex. The usage entry's
key `1/0/https://claude.ai/api/organizations/<uuid>/usage` is in `383706cf5aea4bcc_0` for the user's organization.
Little-endian layout:

```
[header 24 B][key][stream 1: the body][EOF₁ 24 B][stream 0: the response info][key SHA-256 32 B, optional][EOF₀ 24 B]
header = u64 magic 0xfcfb6d1ba7725c30 | u32 version (5) | u32 key length | u32 key hash | u32 padding
EOF    = u64 magic 0xf4fa6f45970d41d8 | u32 flags | u32 CRC-32 | u32 stream size | u32 padding
```

- EOF₀ is the file's last 24 bytes.
- Stream 0 has `EOF₀.stream size` bytes and ends where EOF₀ starts. If EOF₀'s flags have bit 1 (`0x2`), the 32-byte
  SHA-256 sits in between.
- EOF₁ is the 24 bytes before stream 0. Stream 1 runs from `24 + key length` to EOF₁.
- An entry is skipped when:
  - a magic does not match;
  - the version is not 5;
  - the offsets fall outside the file;
  - the file is larger than 1 MiB.

**Key.** The URL is the key's text after its last space. With no space, a leading `<digits>/<digits>/` is dropped
(observed `1/0/`; partitioned caches prefix the top-frame site, separated by spaces). The entry counts when the URL is
`https://claude.ai/api/organizations/<uuid>/usage`, optionally followed by `?` and any query. Desktop's query varies
with its features: observed `?skip_spend=1`, `?at_wall=1&skip_spend=1` and `?cedar_ember=1&skip_spend=1`, each its own
entry.

**Response info.** Stream 0 holds the raw response headers as NUL-separated lines. The first line starts with
`HTTP/` (observed `HTTP/1.1 200`), followed by `name:value` lines and an empty line. The status must be 200.
- `date` (RFC 7231) gives `observedMs`. Without it, the file's modification time does.
- `content-encoding` picks the decoder: `zstd` (observed), `gzip`, `br`, `identity` or absent. Any other value skips
  the entry. A platform without a Brotli decoder skips `br`.
- The decoded body must be a JSON object of at most 1 MiB.

### 4.3 Body

```json
{"five_hour": null,
 "seven_day": {"utilization": 94.0, "resets_at": "2026-11-11T06:59:59.589678+00:00"},
 "seven_day_opus": null, "seven_day_sonnet": null,
 "limits": [{"kind": "weekly_all", "percent": 94, "resets_at": "2026-11-11T06:59:59.589678+00:00", "scope": null},
            {"kind": "weekly_scoped", "percent": 27, "resets_at": "2026-11-11T06:59:59.589834+00:00",
             "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": null}}]}
```

- `five_hour` → `fiveHour`, `seven_day` → `sevenDay`, each when it is an object with a numeric `utilization`
  (0–100). `resets_at` is ISO 8601 with up to 6 fractional digits and an offset; otherwise the reset time is null.
  `null` means the account has no such window right now. The response above (Ultra, 2026-11-10) has no 5-hour window.
- Every `limits[]` entry with `kind == "weekly_scoped"`, a numeric `percent` and a non-empty string
  `scope.model.display_name` → `modelWeek` with that name.
- `seven_day_opus` and `seven_day_sonnet`, when objects with a numeric `utilization`, → `modelWeek` `Opus` and
  `Sonnet`, unless a scoped limit already gave that name.
- Every other field is ignored (Desktop's body carries dozens of feature fields).

### 4.4 Choosing and polling

- **Choosing.** Among every usage entry found (all queries, all organizations), the snapshot with the newest
  `observedMs` wins; ties go to the newer file modification time. A newer entry without windows replaces an older one
  (the account lost its limits).
- **Candidates.** The reader remembers the file names of the usage entries it found.
  - Every 60 s while the quota is switched on, it checks each candidate's size and modification time and reads only a
    changed one.
  - A candidate that disappears is dropped.
- **Search.** The search lists the folder and reads the first `24 + key length` bytes of `_0` files, newest
  modification time first, stopping at the first usage entry. It skips files older than 7 days and stops after 4000
  reads.
  - It runs at start (while the folder exists), and when Claude Desktop starts (section 6).
  - Otherwise it runs at most every 30 minutes, while Desktop runs and the newest candidate is more than 20 minutes
    old. Desktop may have switched to a new query, or to another organization.
  - The user's folder holds ≈ 83 000 entries. A listing with modification times costs ≈ 1 s of CPU in Python and less
    natively (macOS can use `getattrlistbulk`). Reading stops after a few hundred headers, since Desktop refreshes the
    entry often.
- **Budget.** The checks every 60 s cost one `stat` per candidate. Searches are rare. Nothing runs while the switch is
  off.

## 5. Claude Code (the CLI)

Claude Code runs the user's status line command after each response, in the terminal CLI only, and gives it JSON on
standard input. For claude.ai Pro, Max and Ultra subscribers, after the session's first response, the JSON carries
`rate_limits` (Claude Code docs, *Status line*):

```json
{"rate_limits": {"five_hour": {"used_percentage": 23.5, "resets_at": 1738425600},
                 "seven_day": {"used_percentage": 41.2, "resets_at": 1738857600}}}
```

Each window may be absent, and Claude Code drops a window once its `resets_at` passes. Claude Desktop's Code tab never
runs the status line. Tested 2026-11-10: a Code-tab session answered with a recording status line in place, and only
terminal sessions ran it. So this reader only sees the CLI's account.

### 5.1 The bridge

The bridge is off by default. Turning `quotaClaudeCli` on (DESIGN 5) installs it, and turning it off restores the
previous status line.

- **Helper.** `agent-speed-statusline` (Windows `agent-speed-statusline.exe`) is a small native program shipped with
  the app.
  - It lives inside the app (macOS `Agent Speed.app/Contents/Helpers/`; Windows and Linux beside the app's
    executable).
  - When the bridge is turned on, and at every launch while it is on, the app copies it to `<data>/bin/`. It replaces
    the copy (temporary file, then rename) only when the bytes differ.
  - `<data>` is the app's data folder: macOS `~/Library/Application Support/AgentSpeed`, Windows
    `%LOCALAPPDATA%\AgentSpeed`, Linux `$XDG_DATA_HOME/agent-speed`, else `~/.local/share/agent-speed`.
  - The status line points at the copy, so it keeps working when the app moves or updates.
- **Settings file.** `$CLAUDE_CONFIG_DIR/settings.json`, else `~/.claude/settings.json` (Claude Code's user settings).
  A project's `.claude/settings.json` or managed settings with a `statusLine` win over it in their scope. Those
  sessions do not run the helper, and the bridge's status says nothing about them.
- **Our command.** A `statusLine` value that is an object with `type == "command"` and a `command` containing
  `agent-speed-statusline`.

### 5.2 Turning it on

1. Read the settings file. A missing file reads as `{}`. If it is not a JSON object, stop and change nothing. The
   status is then `settings.quota.cli.unreadable`.
2. Unless the current `statusLine` is already ours, write `<data>/quota/claude-statusline-previous.json`:
   - `{"version": 1, "statusLine": <the current value>}`, or `{"version": 1}` when there is none.
   - Also copy the settings file to `<data>/quota/claude-settings.backup.json`.
3. Set `statusLine` to `{"type": "command", "command": <helper command>}`. A previous object's numeric `padding` is
   kept.
   - macOS and Linux: the copy's absolute path in single quotes, with `'` written as `'\''`.
   - Windows: the path with `/` separators in double quotes. Unverified until the Windows box confirms how Claude
     Code runs it.
4. Write the settings file the way Claude Code writes it:
   - the same members in the same order, `statusLine` replaced where it was, or appended last;
   - two-space indentation and a final newline;
   - a temporary file, then rename, keeping the file's permissions.

### 5.3 Turning it off

- If the settings file's `statusLine` is still ours, put the previous value back from the sidecar file (or remove the
  member when there was none) and write the file as in 5.2 step 4.
- If the user replaced it meanwhile, leave the file alone.
- Either way, delete the sidecar, the backup and the quota file.

### 5.4 Status

While the bridge is on, the app checks the settings file every 60 s and when Settings opens. The status (DESIGN 5)
is one of:
- `settings.quota.cli.waiting`: ours, with no quota file yet;
- `settings.quota.cli.ok`: ours, with a quota file;
- `settings.quota.cli.replaced`: not ours any more, with a button that runs 5.2 again;
- `settings.quota.cli.unreadable`.

### 5.5 The helper

1. Read standard input, at most 1 MiB, and parse it as JSON.
2. If `rate_limits` is an object, merge it into `<data>/quota/claude-cli.json` (format below), writing a temporary
   file and renaming it, with permissions 0600 on macOS and Linux:
   - Take each of `five_hour` and `seven_day` that is an object with a numeric `used_percentage` and a numeric
     `resets_at`.
   - The file keeps one value per window. A new value replaces the stored one when its `resets_at` is more than 60 s
     later. When the two are within 60 s, it is the same window and the higher `used_percentage` wins. An earlier one
     is ignored.
   - Other sessions run their status line too, with the values of their own last response. Usage only grows within a
     window, so the newest value is the highest.
   - A stored window missing from the input is kept, because another session may simply not have seen it. Its reset
     time expires it (section 2).
   - `receivedMs` = now when a value changed.
3. Write standard output:
   - **With a previous command** (the sidecar's `statusLine` is an object with `type == "command"` and a non-empty
     `command`): run it with the same input bytes, environment and working folder, pass its standard output through,
     and exit with its code. It is killed after 10 s. Unix shells use `/bin/sh -c`. Windows uses the Git Bash Claude
     Code uses: `CLAUDE_CODE_GIT_BASH_PATH`, else `bash.exe` beside `git.exe`'s `..\bin`, else `cmd.exe /d /s /c`.
     The Windows order is unverified.
   - **Without one**: print one line with the stored windows that have not reset, in section 2's order and labels,
     joined by ` · `: `5h 6% · 7d 86%`. Print nothing when there is none.
4. Never print anything else, never log the input, and exit 0 on any error of its own.

The helper finishes in under 50 ms without a previous command: one read, one small write, no network.

Quota file:

```json
{"version": 1, "receivedMs": 1794338685000,
 "windows": {"five_hour": {"used_percentage": 6, "resets_at": 1794354000},
             "seven_day": {"used_percentage": 86, "resets_at": 1794448800}}}
```

**Reader.** The app checks the quota file's modification time every 5 s while the bridge is on. Each window becomes
`fiveHour` / `sevenDay` with `usedPercent = used_percentage` and `resetsAtMs = resets_at × 1000`.
`observedMs = receivedMs`.

**Known approximation.** `/login` to another account within a window keeps the higher of the two accounts' values
until that window resets. The status line JSON names no account.

## 6. Running and signed in

The 額度 card lists a source only while its app runs, it is signed in to a subscription, and it has a snapshot with
at least one window.

| Source | Runs | Signed in |
| --- | --- | --- |
| `codex` | a `codex` agent process (METRICS 10.5), or the Codex app's main process (macOS `/Applications/Codex.app/Contents/MacOS/Codex`; Windows `WindowsApps\OpenAI.Codex_*\app\Codex.exe`) | `$CODEX_HOME/auth.json`, else `~/.codex/auth.json`, parses as an object with `auth_mode == "chatgpt"` or a `tokens` object |
| `claudeCode` | a `claudeCode` agent process (METRICS 10.5) | `$CLAUDE_CONFIG_DIR/.claude.json`, else `~/.claude.json`, has an `oauthAccount` object |
| `claudeDesktop` | Claude Desktop's main process (macOS `/Applications/Claude.app/Contents/MacOS/Claude`, bundle id `com.anthropic.claudefordesktop`; Windows `WindowsApps\Claude_*\app\claude.exe` or `%LOCALAPPDATA%\AnthropicClaude\app-*\claude.exe`; Linux unverified) | its snapshot's `observedMs` is at most 45 minutes old: Desktop only refreshes it while signed in |

- Only the named keys are looked at. `auth.json` and `.claude.json` are parsed in memory and never copied, kept or
  logged.
- They are read at most once a minute, and again when their modification time changes.
- Processes come from the process table when METRICS 10.10 lists it. Otherwise, while an orb shows the 額度 card, a
  listing runs every 15 s (one listing ≈ 1.6 ms on Windows).

## 7. Formatting

- **Percent**: `23%`, the integer of section 2 and `%`, with no space in any language.
- **Reset, long** (dashboard). `d = resetsAtMs − now`:
  - `d < 1 h`: `quota.reset.in` with `quota.duration.m`, e.g. `42 分後重置`.
  - `d < 24 h`: `quota.reset.in` with `quota.duration.hm`, e.g. `3 小時 12 分後重置`. Minutes are rounded down; an exact
    hour drops its minutes: `quota.duration.h`.
  - Otherwise: `quota.reset.at` with the weekday's short name and the 24-hour time from the platform's formatter in the
    app language, e.g. `週五 09:00 重置`. When the weekday is today's (a reset 6–7 days away), the numeric month and day
    replace it: `11/17 09:00 重置`.
- **Reset, short** (orb card), the same in every language:
  - `d < 1 h` → `42m`;
  - `d < 24 h` → `3h` (hours rounded down);
  - otherwise the weekday's short name (`週五`, `周五`, `Fri`, `金`), or `M/d` when it is today's weekday.
- **Age**: `quota.age` with a duration rounded down to whole hours (`quota.duration.h`), from 24 hours to whole days
  (`quota.duration.d`).
- **Pace** (dashboard tooltip): the share of the window that has passed, `1 − (resetsAtMs − now) / (minutes × 60000)`,
  clamped to 0…1, as `quota.pace` with an integer percent. It is absent when the reset time is unknown or has passed.

## 8. Privacy and risks

- What is read: Codex's rollout lines (already read for speed), the Claude Desktop usage entry, the helper's quota
  file, and the two signed-in markers of section 6. None of it leaves the computer, and only the snapshot is kept in
  memory. Nothing quota-related is written except by the bridge (section 5).
- `privacy.quota` (DESIGN 5, About) says this.
- **Claude Desktop's cache** is Desktop's private format, not an interface. A Desktop update may stop writing it, move
  it, change the key or the body.
  - Every check is strict, so the reader then finds nothing: the Desktop rows stop showing a quota. They never show a
    wrong number.
  - The fixtures pin the observed format (2026-11-10, Claude Desktop with Claude Code 2.1.321).
- **The bridge** changes one value in Claude Code's settings and restores it. If the user removes Agent Speed without
  turning the bridge off, the status line keeps running the copy in `<data>/bin`, which still prints their previous
  status line or the quota line.

## 9. Further sources

Sources other than these three follow the same model and UI once their readers are specified here. The research
started 2026-11-10 (Tokei's readers and each agent's local data). For vendors other than Anthropic, the user allows
an agent's own vendor API with that agent's local sign-in (2026-11-10).

## 10. Fixtures

`spec/fixtures/quota-cases.json` (written by `spec/tools/quota_reference.py --write`) holds the cases, and
`spec/fixtures/quota/` holds the binary cache entries. The cases cover:
- Codex lines: primary only, primary and secondary, a zero window, `premium` ignored, `resets_in_seconds`, the newest
  observation winning, and copied history ignored.
- Cache entries:
  - a zstd body with the observed shape;
  - identity and gzip bodies;
  - a non-200 status;
  - a key of another URL;
  - a partitioned key;
  - a bad magic;
  - a truncated file;
  - the SHA-256 flag absent;
  - the newest of several entries winning.
- Bodies: the 5-hour window present or null, scoped limits, `seven_day_opus`.
- The helper's merge (newer window, same window with lower and higher values, earlier window, missing window) and its
  line.
- Turning the bridge on and off: no file, no `statusLine`, an existing one with `padding`, already ours, replaced by
  the user, not an object.
- Reset passed, labels and order, and the long and short reset texts in the four languages for fixed `now` values in
  `Asia/Taipei`.
