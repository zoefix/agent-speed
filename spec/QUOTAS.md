# Agent Speed — Quota Specification

Agent Speed shows how much of each subscription's usage limits the user has used: for every limit window an account
has (a 5-hour window, a weekly window, a weekly limit of one model), the used percentage and the time it resets. The
dashboard's Sources rows show every source that has a quota; the `character` orb's 額度 card shows the sources whose
app runs and is signed in. The UI is DESIGN 4 (Sources), 2a.6 (*Readouts*, 額度) and 5 (Data sources); the fixtures are
checked by `spec/tools/quota_reference.py --check-fixtures`. Status: specified 2026-11-10 (user's request); phase 1
covers Codex, Claude Desktop and Claude Code; phase 2 (section 9) adds Grok Build, Devin, Antigravity, Kimi Code,
Gemini CLI, Factory Droid, ZCode, Qoder and WorkBuddy.

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
  writes one value into Claude Code's settings, and only after the user turns it on (section 5). Phase 2's vendor
  requests follow section 9.1 and NETWORK 9.

## 2. Model

A **snapshot** belongs to one source: `{source, observedMs, windows}`. `observedMs` is when the vendor computed the
values (section 3, 4, 5 say which time). A **window** is `{kind, name, minutes, usedPercent, resetsAtMs}`:

| `kind` | Meaning | Label (every language) |
| --- | --- | --- |
| `fiveHour` | the rolling 5-hour window | `5h` |
| `sevenDay` | the weekly window over every model | `7d` |
| `modelWeek` | a weekly window of one model or model family, `name` = the vendor's display name (`Fable`, `Opus`) | `<name> 7d` (`Fable 7d`) |
| `other` | any other length, `minutes` known | `<n>d` for whole days, else `<n>h` for whole hours (`25h`), else `<n>m` |

- `usedPercent` is a number; the bar draws `clamp(usedPercent, 0, 100)` and the text shows it rounded half up to an
  integer, clamped to 0…100 (`100%` once a window is exhausted, even when the vendor says 103).
- `resetsAtMs` may be null (unknown).
- `minutes` is 300 for `fiveHour`, 10080 for `sevenDay` and `modelWeek`, the vendor's value for `other`.
- **Order**: windows without a name first, by `minutes` (shortest first; equal lengths in kind order `fiveHour`,
  `other`, `sevenDay`, `modelWeek`, `month`, `credits`; `credits` and any window with null minutes last). Then the
  named windows, grouped by name in the order the vendor lists the names, each group by `minutes` the same way.

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

Claude Desktop asks claude.ai for the signed-in account's usage while it runs, every 5 to 17 minutes (observed
2026-11-10: 11:26:13, 11:43:25, 11:54:39, 11:59:47, 12:04:59), and its Chromium network stack caches each response on disk. Agent Speed reads that
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
| `claudeDesktop` | Claude Desktop's main process (macOS `/Applications/Claude.app/Contents/MacOS/Claude`, bundle id `com.anthropic.claudefordesktop`; Windows `WindowsApps\Claude_*\app\claude.exe` or `%LOCALAPPDATA%\AnthropicClaude\app-*\claude.exe`; Linux unverified) | its snapshot's `observedMs` is at most 24 hours old: Desktop only refreshes it while signed in and in use: when its window comes to the front, then every few minutes while it stays there (seen 2026-11-11; no refresh at all for over an hour while it ran in the background), so a shorter limit drops a Desktop that is signed in but left in the background |

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

## 9. Further sources (phase 2)

The user chose on 2026-11-10 to add every source that has a quota: "全都做，包含讀瀏覽器 Cookie". For vendors other
than Anthropic, they allow the vendor's API with the agent's own sign-in, a key the user enters, and the user's
browser session ("就算是讀取API也無所謂，我們只在乎Claude的用戶協議").

The readers follow the open-source apps Tokei (cclank/tokei, `usage.30s.py`, 2026-11-09) and CodexBar
(steipete/CodexBar, 2026-11-10). They are **unverified**: none of these agents is installed on the development
machine, and the endpoints are undocumented. They ship with the caption `settings.quota.unverified` and are fixed from
user reports ("如果到時候有用戶反饋有問題，我們在修復即可").

Remote hosts read the CLI routes on the host itself (REMOTE 11). That includes the Claude Code bridge, turned on per
host.

Sources with no quota to show:
- DeepSeek Harness: a wallet balance, no windows.
- Pi, oh-my-pi, OpenCode: no subscription quota of their own.
- OpenClaw and Hermes: they ask other vendors live, including Anthropic, which section 1 excludes.

### 9.1 Common rules

**More window kinds.**
- `month`: a monthly or billing-cycle window. `minutes` is the cycle length when start and end are known, else
  43200. Label `quota.window.month`.
- `credits`: a credit pool with no stated length. `minutes` is null and there is no pace. Label
  `quota.window.credits`.
- `other` with 1440 minutes is the daily window (`1d`).
- **Names.** Any kind may carry a `name`, a model group such as `Gemini` or `Claude/GPT`. Its label is
  `<name> <base label>`; `modelWeek` is the same as `sevenDay` with a name.
- **Order** is section 2's: unnamed by length, then named groups in vendor order.

**Routes and switches.** One switch per source, `quota<Source>` (DESIGN 5).

| Route | Sources | Default |
| --- | --- | --- |
| Local file | Grok Build (log), Devin | on |
| Loopback server | Antigravity | on |
| Vendor API with the agent's own sign-in | Kimi Code, Gemini CLI, Factory Droid (its API key), Grok Build (live) | on; Grok Build live off |
| Vendor API with a key the user enters | ZCode | off until a key is saved |
| Vendor website with the browser's session | Qoder IDE and Qoder CLI, WorkBuddy and WorkBuddy AI | off; turning it on asks first (9.10) |

**Network rules.** These are the new feature *Quotas* of NETWORK 9.
- Requests are only made while `networkEnabled` is on, the source itself is enabled (DESIGN 5, Data sources) and its
  quota switch is on. A disabled source reads no quota on any route, phase 1 included, and shows none.
- HTTPS only, except the loopback route. A redirect is never followed and counts as a failure. The system proxy
  applies, except for loopback.
- Timeout 10 s (loopback 2 s). Body cap 256 KiB. JSON only.
- Headers are only the ones each section lists, plus `User-Agent: AgentSpeed/<version> (<macOS|Windows|Linux>)`.
  WorkBuddy is the one exception: it sends the browser's user agent (9.9).

**Credentials.**
- A credential is read from its file, the keychain or the browser right before the request.
- It stays in memory for that request only. It is never written, logged, shown or sent to any host but the one its
  section names.
- A token is only ever read, never refreshed: refreshing would race the agent and could sign it out (Kimi rotates its
  refresh tokens).
- When a token's own expiry has passed, no request is made, and the last snapshot stays with its age (section 2).
- The snapshot is keyed by the first 8 bytes of the SHA-256 of the credential, so signing in to another account drops
  the old account's numbers.

**Cadence.**
- A vendor request runs at most every 5 minutes per source, and only while the source runs (section 6, rows below) or
  the dashboard is open.
- A failure waits 15 minutes.
- `401` and `403` stop the source's requests until its credential changes (new file modification time, a new cookie,
  a new key).
- `observedMs` is the response's `Date` header, else the time it arrived.
- A local route is read when its file changes, checked every 60 s.

**Signed in.** A source with a vendor route counts as signed in (section 6) while its credential exists and has not
expired. A local route is signed in while its snapshot is at most 24 hours old, and Devin's while its launch time
(9.3) is.

### 9.2 Grok Build

The Grok Build CLI logs its billing config: Tokei reads it, with no network.
- **Log** (`quotaGrokBuild`, on): `$GROK_HOME/logs/unified.jsonl`, else `~/.grok/logs/unified.jsonl`.
  - Read the last 2 MiB of the file and skip its first, partial line.
  - Keep JSON lines with `msg == "billing: fetched credits config"`. The one with the greatest `ts` (ISO 8601)
    wins: `config` = `ctx.config`, `observedMs` = `ts`.
- **Live** (`quotaGrokBuildLive`, off):
  - `GET https://cli-chat-proxy.grok.com/v1/billing?format=credits` with `Accept: application/json`,
    `Authorization: Bearer <token>` and `x-xai-token-auth: xai-grok-cli`.
  - The token is the first entry of `$GROK_HOME/auth.json`, an object whose keys are scope URLs. The entry's `key`,
    else `access_token`, with `expires_at` (ISO 8601). Within 60 s of that expiry, no request.
  - The response's `config` has the same shape as the log's.
- **Config → one window.**
  - Kind: `currentPeriod.type` containing `MONTH` → `month`, else `sevenDay`.
  - `usedPercent = creditUsagePercent` (0–100).
  - When it is absent, `0` only if `isUnifiedBillingUser == true` and `currentPeriod.start` and `currentPeriod.end`
    exist (protobuf JSON drops zero values). Otherwise the snapshot has no window.
  - The reset is `currentPeriod.end`, else `billingPeriodEnd` (ISO 8601). A `month` with both period ends known has
    `minutes` = their difference.
  - `productUsage[]` (`{product | name, usagePercent}`) is not shown.
- **Runs**: a `grokBuild` agent process (METRICS 10.5).

### 9.3 Devin

Devin Desktop caches the account's plan when it starts; Tokei reads it. The quota is the account's, shared by the
Devin CLI. Users of the CLI alone have no local quota.
- **Folder**: the newest of these that contains `User/globalStorage/state.vscdb`:
  - macOS `~/Library/Application Support/Devin`, `…/Windsurf`;
  - Windows `%APPDATA%\Devin`, `%APPDATA%\Windsurf`;
  - Linux `~/.config/Devin`, `~/.config/Windsurf`.
- **Query** (SQLite, read-only, immutable): `SELECT key, value FROM ItemTable WHERE key LIKE
  'windsurf.reactSettings.cachedPlanInfoData%'`, else `LIKE 'windsurf.settings.cachedPlanInfo%'`.
  - The value is JSON (bytes as UTF-8).
  - Several rows are several accounts. The greatest `endTimestamp` wins.
- **Fields**:
  - `dailyRemainingPercent` with `dailyResetAtUnix` → `other` 1440 minutes, unless `hideDailyQuota == true`.
  - `weeklyRemainingPercent` with `weeklyResetAtUnix` → `sevenDay`, unless `hideWeeklyQuota == true`.
  - `usedPercent = 100 − remaining`. Resets are epoch seconds; values above 1e11 are milliseconds.
  - Message pools, overage and the plan name are not shown. `accountIdentityText` holds an e-mail address and is never
    read into the snapshot.
- **Time**: `observedMs` is the newest launch folder `<folder>/logs/<YYYYMMDDTHHMMSS>`, read as local time. Without
  one, there is no snapshot. The row is written at launch, not while Desktop runs.
- **Runs**: a `devin` agent process, or Devin Desktop's main process (METRICS 10.5 names it).

### 9.4 Antigravity

The running language server answers a quota request on loopback. Antigravity is never started for it.
- **Servers**, from the process table (METRICS 10.5):
  - An executable named `language_server_*` whose arguments contain `--app_data_dir` and `antigravity`, or whose path
    is inside `Antigravity.app`, `Antigravity IDE.app` or `Gemini.app`. It takes `--csrf_token`,
    `--extension_server_port` and `--extension_server_csrf_token`.
  - Or an `agy` process. Its token comes from its own environment, `ANTIGRAVITY_CSRF_TOKEN`, with the address from
    `ANTIGRAVITY_LS_ADDRESS` (`host:port`). On macOS this is `KERN_PROCARGS2`, on Linux `/proc/<pid>/environ`; there
    is none on Windows.
  - Only that one process is read: never the environment of every process, as Tokei does.
  - Daemon files `~/.gemini/antigravity/daemon/ls_*.json`, `~/.gemini/antigravity-ide/daemon/ls_*.json`
    (`pid`, `csrfToken`, `httpsPort`, `httpPort`), used while that pid runs.
- **Ports**:
  - `--extension_server_port` first, with `--extension_server_csrf_token`, then `--csrf_token`.
  - Then the process's listening TCP ports on 127.0.0.1, over `http` and then `https`. Self-signed certificates are
    accepted on loopback only.
  - Ports come from macOS `proc_pidfdinfo` sockets, Windows `GetExtendedTcpTable`, Linux `/proc/net/tcp` with the
    process's socket inodes.
- **Request**:
  - `POST <scheme>://127.0.0.1:<port>/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary` with
    `Connect-Protocol-Version: 1`, `Content-Type: application/json`, `X-Codeium-Csrf-Token: <token>` (left out
    without a token), and body `{"forceRefresh": true}`.
  - `401` or `403` forgets that process's token.
  - A `404` (the IDE's server) falls back to `.../GetUserStatus` with body
    `{"metadata":{"ideName":"antigravity","extensionName":"antigravity","ideVersion":"unknown","locale":"en"}}`.
- **Summary → windows**:
  - The root is `response` when present, else the body.
  - Each `groups[]` entry has a `displayName`, which gives its name:
    - containing `gemini` and `argon` → `Gemini Argon`;
    - `gemini` → `Gemini`;
    - `claude`, `gpt` or `third` → `Claude/GPT`;
    - otherwise the display name.
  - Each `buckets[]` entry is skipped when `disabled == true`. Its kind comes from `window`, `bucketId` and
    `displayName`, lowercased with `_` as `-`:
    - containing `weekly`, `week` or `7d` → `sevenDay`;
    - `5h`, `5-hour`, `five-hour`, `five hour` or `session` → `fiveHour`;
    - else skipped.
  - Remaining fraction: the bucket's `remainingFraction`, else `{"case": "remainingFraction", "value": x}`, else
    `remaining.remainingFraction`. `usedPercent = (1 − x) × 100`.
  - Reset: `resetTime` (ISO 8601 or epoch).
- **GetUserStatus → windows**: `userStatus.cascadeModelConfigData.clientModelConfigs[].quotaInfo`
  (`remainingFraction`, `resetTime`) → `fiveHour` named by the config's `label`.
- **Runs**: one of the servers above runs. **Signed in**: the last request succeeded.

### 9.5 Kimi Code

- **Credential**: `$KIMI_CODE_HOME/credentials/kimi-code.json`, else `~/.kimi-code/credentials/kimi-code.json`, else
  the legacy `~/.kimi/credentials/kimi-code.json`. It holds `access_token` and `expires_at` (seconds; above 1e11,
  milliseconds). `refresh_token` is never read.
- **Request**: `GET https://api.kimi.com/coding/v1/usages` with `Accept: application/json` and
  `Authorization: Bearer <access_token>`. A `KIMI_CODE_BASE_URL` override is never used with this token.
- **Response, current shape**: `usages.limit_5h` → `fiveHour`, `usages.limit_7d` → `sevenDay`, and the first of
  `usages.limit_month_total`, `usages.limit_month` → `month`. Each is `{used_ratio (0–1), reset_time (ISO 8601)}`,
  with `usedPercent = used_ratio × 100`.
- **Legacy shape**:
  - `limits[]` of `{window: {duration, timeUnit}, detail: {limit, used, remaining, resetTime}}`, numbers possibly as
    strings. `timeUnit` is `TIME_UNIT_MINUTE`, `_HOUR`, `_DAY` or `_WEEK`.
  - 300 minutes → `fiveHour`, a week → `sevenDay`, others → `other`.
  - The top-level `usage {limit, used, remaining, resetTime}` → `sevenDay` when no limit gave one.
  - `usedPercent = used / limit × 100`, with `used = limit − remaining` when `used` is missing.
  - Reset keys: `resetTime`, `reset_time`, `resetAt`, `reset_at`.
- **Runs**: a `kimi` agent process.

### 9.6 Gemini CLI

- **Credential**: `<home>/.gemini/oauth_creds.json` (`<home>` as METRICS 2: `$GEMINI_CLI_HOME`, else `~`):
  `access_token` with `expiry_date` (milliseconds). Only the CLI's Google sign-in has one; API-key and Vertex users
  have no quota here.
- **Requests**, `Authorization: Bearer <access_token>`, `Content-Type: application/json`:
  1. `POST https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist` with body
     `{"metadata":{"ideType":"GEMINI_CLI","pluginType":"GEMINI"}}`. It gives `cloudaicompanionProject` (a string, or
     an object's `id`). The result is kept for the access token's life.
  2. `POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota` with body `{"project": "<id>"}`, or `{}`
     without one.
- **Buckets**: `buckets[]` of `{modelId, remainingFraction (0–1), resetTime (ISO 8601)}`.
  - Group by family:
    - `modelId` containing `flash-lite` → `Flash Lite`;
    - `flash` → `Flash`;
    - `pro` → `Pro`;
    - otherwise the id.
  - Each family keeps the lowest remaining fraction, with its reset. It becomes `other` 1440 minutes named by the
    family (Gemini's quotas are daily per model).
  - Family order: Pro, Flash, Flash Lite, then others by id.
- Google ended Gemini CLI sign-in for individual, AI Pro and Ultra accounts on 2026-06-18
  (developers.google.com/gemini-code-assist/docs/deprecations/code-assist-individuals). Such accounts answer `403`,
  so the source has no snapshot.
- **Runs**: a `gemini` agent process.

### 9.7 Factory Droid

- **Credential**: `FACTORY_API_KEY` from the app's environment, else the `FACTORY_API_KEY=` line of `~/.factory/.env`
  (that one line only; quotes trimmed). `~/.factory/settings.json` holds the user's own provider keys and is never
  read. Where droid keeps its own sign-in is unverified, so a Droid user with no API key has no quota.
- **Request**: `GET https://api.factory.ai/api/billing/limits` with `Accept: application/json` and
  `Authorization: Bearer <key>`.
- **Response**:
  - `limits.standard` and the optional `limits.core`, each with `fiveHour`, `weekly` and `monthly` objects of
    `{usedPercent (0–100), windowEnd (epoch s, ms or ISO 8601), secondsRemaining}`.
  - Standard gives `fiveHour`, `sevenDay` and `month` (43200 minutes); `core` gives the same kinds named `Core`.
  - The reset is `now + secondsRemaining` when present, else `windowEnd`. A `windowEnd` already past without
    `secondsRemaining` means 0 %.
- **Runs**: a `droid` agent process.

### 9.8 ZCode

ZCode uses Zhipu's GLM Coding Plan. Its quota endpoint takes the plan's API key, which the user pastes in Settings
(Tokei's z.ai card). Whether ZCode's own sign-in works there is unverified.
- **Key**: `quotaZcodeKey` in the platform's secret store: the macOS Keychain (service `AgentSpeed`, account
  `zcode-api-key`), Windows Credential Manager (`AgentSpeed:zcode-api-key`), Linux libsecret (`service AgentSpeed`,
  `account zcode-api-key`). It is never in the settings file.
- **Region** `quotaZcodeRegion`: `global` (`https://api.z.ai`, default) or `bigmodel-cn` (`https://open.bigmodel.cn`).
- **Request**: `GET <base>/api/monitor/usage/quota/limit` with `Authorization: Bearer <key>`.
- **Response**:
  - It must have `success == true` and `code == 200`.
  - `data.limits[]` of `{type, unit, number, percentage (0–100), usage, currentValue, remaining, nextResetTime
    (epoch ms)}`.
  - `TOKENS_LIMIT` and `CREDIT_LIMIT`: minutes = `number ×` the unit's minutes (`{1: 1440, 3: 60, 5: 1, 6: 10080}`).
    300 → `fiveHour`, 10080 → `sevenDay`, others → `other`.
  - `TIME_LIMIT` (MCP calls) → `month` named `MCP`.
  - `usedPercent` = `currentValue / usage × 100` when both are numbers and `usage > 0`, else `percentage`.
  - A `fiveHour` reset more than 301 minutes ahead is dropped.
- **Runs**: a `zcode` agent process.

### 9.9 Qoder and WorkBuddy (browser session)

**Qoder** (`quotaQoder`, for `qoder` and `qoderCli`, one account; off):
- Request: `GET https://qoder.com/api/v2/me/usages/big_model_credits`, or `https://qoder.com.cn/...` for the China
  edition. Headers: `Cookie` (9.10, cookies of `qoder.com` or `qoder.com.cn`), `Origin` and `Referer` set to the same
  site, and `Accept: application/json`.
- Response: `totalQuota.quotaSummary` and the optional `sharedQuota.quotaSummary`. Each has `usedValue` / `used_value`
  and `limitValue` / `limit_value`, or `usagePercentage` / `usage_percentage`.
  - Totals are summed. `usedPercent = used / limit × 100`, else the percentage.
  - The reset is `nextResetAt` / `next_reset_at` (epoch s, ms or ISO 8601).
  - One `credits` window.
- Both sources share the snapshot; each shows it on its own row.

**WorkBuddy** (`quotaWorkBuddy`, China edition `workbuddy` only; off; the international edition's endpoint is
unknown):
- Request: `POST https://www.workbuddy.cn/billing/meter/get-user-resource-summary` with body `{}`,
  `Content-Type: application/json`, `Cookie` (9.10, cookies of `workbuddy.cn`) and `User-Agent` set to the user agent
  of the browser the cookies came from. The server ties the session to it.
  - Chrome family: `Mozilla/5.0 (<platform token>) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/<major>.0.0.0 Safari/537.36`,
    with `<major>` from the profile's `Last Version` file.
  - Firefox: `Mozilla/5.0 (<platform token>; rv:<v>) Gecko/20100101 Firefox/<v>`.
  - Safari: `Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/<v> Safari/605.1.15`.
- Response: `code == 0`. `data.Packages[]` with `CycleTotalCapacity`, `CycleRemainCapacity` (decimal strings) and
  `CapacityUnit == "credits"`.
  - Totals are summed, and `usedPercent = (total − remain) / total × 100`.
  - One `month` window with no reset time: the reset needs client-version package codes, left out.
- **Runs** (both): the source's agent process (METRICS 10.5).

### 9.10 Browser cookies

Turning `quotaQoder` or `quotaWorkBuddy` on asks first (DESIGN 5). The dialog names:
- the site;
- that only that site's cookies are read, from the browsers on this computer, and kept in memory for one request;
- that macOS asks once per browser for its "Safe Storage" keychain item.

A refusal leaves the browser out.

- **Browsers**, tried in this order, one profile after another (`Default`, then `Profile *`):
  - Chrome;
  - Edge;
  - Brave;
  - Arc (macOS);
  - Vivaldi;
  - Chromium;
  - Firefox (every profile in `profiles.ini`);
  - Safari (macOS; needs Full Disk Access, else skipped).

  The first profile with an unexpired cookie for the site wins; with several, the one with the newest
  `last_access_utc`.
- **Chrome family**: the `Cookies` SQLite database of the profile (also `Network/Cookies`), copied with its `-wal`
  to a temporary file first and read there. Columns `host_key`, `name`, `value`, `encrypted_value`, `expires_utc`,
  `last_access_utc`. A plain `value` is used as is. Otherwise `encrypted_value` decrypts:
  - **macOS, `v10`**:
    - key = PBKDF2-HMAC-SHA1(password, `saltysalt`, 1003 iterations, 16 bytes);
    - the password is the keychain item `<Browser> Safe Storage` (`Chrome Safe Storage`, `Microsoft Edge Safe
      Storage`, `Brave Safe Storage`, `Arc Safe Storage`, `Vivaldi Safe Storage`, `Chromium Safe Storage`);
    - AES-128-CBC, IV of 16 spaces (`0x20`), PKCS#7.
  - **Linux**: `v10` with the password `peanuts`, or `v11` with the libsecret item of `application` `chrome` (or the
    browser's), PBKDF2 with 1 iteration, otherwise as on macOS.
  - **Windows, `v10`/`v11`**:
    - key = DPAPI `CryptUnprotectData` of `Local State` `os_crypt.encrypted_key` (base64, `DPAPI` prefix dropped);
    - AES-256-GCM with a 12-byte nonce after the prefix and a 16-byte tag at the end.
    - `v20` (app-bound encryption, Chrome 127+) cannot be decrypted by a user program and is skipped.
    - A database the running browser holds locked is skipped too. Windows users of Chrome and Edge may therefore have
      no session; Firefox works.
  - Since Chrome 130 (the database's `meta` `version` ≥ 24), the decrypted value starts with the SHA-256 of
    `host_key`; those 32 bytes are dropped.
- **Firefox**: `cookies.sqlite` of each profile (copied with its `-wal`), table `moz_cookies` (`host`, `name`,
  `value`, `expiry`, `lastAccessed`). Values are plain.
- **Safari**: `~/Library/Containers/com.apple.Safari/Data/Library/Cookies/Cookies.binarycookies`, the binary cookies
  format (big-endian page table, little-endian records).
- **Hosts**: `host_key` equal to the site or ending in `.<site>`. The `Cookie` header joins every such `name=value`
  that has not expired, in database order.

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
- Phase 2: every response shape of section 9 (Grok config from a log and from the API, Devin rows, Antigravity
  summaries and `GetUserStatus`, Kimi current and legacy, Gemini buckets, Factory limits, z.ai limits, Qoder and
  WorkBuddy summaries), named-window labels and order, and Chrome cookie decryption vectors for macOS `v10`, Linux
  `v10`/`v11` (with and without the host hash) and Windows `v10` (AES-GCM, given key).
