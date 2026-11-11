# Agent Speed — Quota Specification

Agent Speed shows how much of each subscription's usage limits the user has used: for every limit window an account
has (a 5-hour window, a weekly window, a weekly limit of one model), the used percentage and the time it resets. The
dashboard's Sources rows show every source that has a quota; the `character` orb's 額度 card shows the sources whose
app runs and is signed in. The UI is DESIGN 4 (Sources), 2a.6 (*Readouts*, 額度) and 5 (Data sources); the fixtures are
checked by `spec/tools/quota_reference.py --check-fixtures`. Status: specified 2026-11-10 (user's request); phase 1
covers Codex, Claude Desktop and Claude Code; phase 2 (section 9) adds Grok Build, Devin, Antigravity, Kimi Code,
Gemini CLI, Factory Droid, ZCode, Qoder and WorkBuddy. Section 10 (2026-11-11) estimates each window's cap in
API-equivalent dollars, shows the amount used and reports both when the user shares quotas.

## 1. Principles

- **Display, and reporting only when shared.** A quota never changes a speed, a score or a cost. Quotas are not part
  of the local network frame (LAN), and remote hosts only send theirs to the app (REMOTE 11). The one exception is
  live reporting: with the 額度 sharing switch on (on by default, DESIGN 11) and a service that advertises
  `quotaEstimate`, reports carry each window's percent, measured spend and estimated cap (section 10,
  REPORTING-EXTENSIONS 11), with the plan but never an account id.
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
  case), so `claudeDesktop` and `claudeCode` each carry their own snapshot and are never merged. Only the estimate
  pools their spend, when they are provably one personal account (10.1).
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
- **Reset, long** (dashboard; a quota line's tooltip while its row shows the short one, DESIGN 4).
  `d = resetsAtMs − now`:
  - `d < 1 h`: `quota.reset.in` with `quota.duration.m`, e.g. `42 分後重置`.
  - `d < 24 h`: `quota.reset.in` with `quota.duration.hm`, e.g. `3 小時 12 分後重置`. Minutes are rounded down; an exact
    hour drops its minutes: `quota.duration.h`.
  - Otherwise: `quota.reset.at` with the weekday's short name and the 24-hour time from the platform's formatter in the
    app language, e.g. `週五 09:00 重置`. When the weekday is today's (a reset 6–7 days away), the numeric month and day
    replace it: `11/17 09:00 重置`.
- **Reset, short** (orb card; the dashboard's quota lines beside an amount, DESIGN 4), the same in every
  language:
  - `d < 1 h` → `42m`;
  - `d < 24 h` → `3h` (hours rounded down);
  - otherwise the weekday's short name (`週五`, `周五`, `Fri`, `金`), or `M/d` when it is today's weekday.
- **Age**: `quota.age` with a duration rounded down to whole hours (`quota.duration.h`), from 24 hours to whole days
  (`quota.duration.d`).
- **Pace** (dashboard tooltip): the share of the window that has passed, `1 − (resetsAtMs − now) / (minutes × 60000)`,
  clamped to 0…1, as `quota.pace` with an integer percent. It is absent when the reset time is unknown or has passed.

## 8. Privacy and risks

- What is read: Codex's rollout lines (already read for speed), the Claude Desktop usage entry, the helper's quota
  file, and the two signed-in markers of section 6. Only the snapshot is kept in memory. Written: the bridge's files
  (section 5) and the estimate's install id and store (10.1, 10.4), which hold account keys (keyed hashes), never ids.
- What leaves the computer: with the 額度 sharing switch on and a service that takes them, the report entries of
  REPORTING-EXTENSIONS 11 (each account's tag for that service, percent, spend, estimate, plan and tier, model mix);
  nothing else, and never an account id, e-mail address or key. Remote hosts receive the account salt over their SSH
  connection (REMOTE 11.1).
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

## 10. Estimates

The vendors show a percent, never the size of the limit. Agent Speed estimates each window's cap in API-equivalent US
dollars (METRICS 9) from the vendor's percent and the spend it measured itself, so that it can show the amount used
and the cap, extrapolate the percent between vendor values, and report both to services that collect statistics
(REPORTING-EXTENSIONS 11). User's request and decisions 2026-11-11 ("就算這個人沒用完額度也能預估出來…要做到極致的完美";
the UI of 10.8, the live percent of 10.7, the 額度 sharing switch on by default, the plan but no account id in reports).
The reference is `spec/tools/quota_estimate.py`, the simulation `quota_estimate_sim.py` (10.9) and the run over this
computer's own logs `quota_estimate_local.py`. Everything in this section is an estimate: it is shown with `≈` and
reported with its confidence, never as the vendor's number.

**The idea.** In a window that started at `S`, the vendor's percent is `p = 100 · U / K`, where `U` is the account's
usage so far and `K` the cap. Agent Speed sees `C`, the spend of this computer (and its hosts) on that account. Since
`C ≤ U` whatever else the account does, `K ≥ 100 · C / p` always holds (a lower bound). When nothing else used the
account, `C ≈ U` and `K ≤ 100 · C / p` holds too (an upper bound). Quantization, lag and unseen usage are handled by
turning each observation into an interval of `K` (10.5) and letting the intervals of many windows vote (10.6).

### 10.1 Accounts

Spend and observations are pooled per account, identified by
`key = first 16 hex digits of HMAC-SHA256(accountSalt, "<vendor>:<id>")`.
- `accountSalt`: the SHA-256 of the ASCII text `agent-speed quota account v1`, the same in every install, so that one
  account has one key on all of the user's computers and its report tag (REPORTING-EXTENSIONS 11.2) is the same from
  each of them (user's choice 2026-11-11, after a service rejected two Codex accounts that a report could not tell
  apart). The ids it keys are random, so a key reveals nothing; still, keys are never logged and never sent to a
  reporting service. It is sent to remote hosts in the quota config (REMOTE 11.1), as before.
- `installId`: 32 random bytes in `<data>/quota/estimate-salt` (the file keeps its name; permissions 0600 on macOS and
  Linux; created when first needed, with a temporary file and a rename), in hex. It only keys accounts without an id
  (below), so that those stay apart between computers. It never leaves the computer and is never logged.
- `codex`: `tokens.account_id` of the `auth.json` that section 6 already parses in memory (nothing else of it).
- `claudeCode`: `oauthAccount.organizationUuid` and `accountUuid` of `.claude.json` (section 6), as `<org>:<account>`.
- `claudeDesktop`: the organization uuid of its usage entry's key (4.2). It shares the CLI's account when the uuid
  equals the CLI's `organizationUuid`, the CLI's `organizationType` is a personal plan (`claude_free`, `claude_pro`,
  `claude_max` or `claude_ultra`) and the two sources' fingerprint (below) confirms it. A team or enterprise
  organization (`claude_team`, `claude_enterprise`, any other value) is shared by its members, so Desktop then keeps
  its own account: a wrong merge would add another account's spend to the lower bounds, a missed one only loses
  evidence. On a shared account the two sources' spends add up.
  - Desktop's own account is keyed by its organization (`claudeDesktop:<org>`), and organizations are compared by
    that key. Its timeline (below) holds those own keys; the key it spends on is derived again on every evaluation,
    with an entry at each entry of its own timeline and of the CLI's: the CLI's key in force when the rule holds, its
    own key otherwise.
- **Fingerprint** of two sources at a moment `at`. Their observations (10.4) of one window form a *comparable pair*
  when the two instances `[R − minutes, R]` overlap by more than a minute; resets within a minute of each other
  *confirm* that the sources are one account, resets further apart *contradict* it. A pair is observed at the later
  of its two times. The latest pair observed by `at` decides (the earlier time next, a contradiction first on a tie);
  with none by then the first pair observed after it (so that history read at the first launch follows the first
  evidence); with none at all the fingerprint is unknown. The inputs are every fresh and stored observation of the
  first source and those of the second source under its key in force. Seen 2026-11-11: one organization uuid on two
  Claude accounts, Desktop at 0 % while the CLI was at 100 %, their 7-day resets 19 h apart. Desktop shares only on a
  confirmation, so without the CLI bridge it keeps its own account.
- Phase-2 sources and any source without an id: the account is the pair (computer or host, source), keyed as vendor
  `pair` with id `<installId>:<machine>:<source>` (`machine` is `local` or the host's id).
- **Key timeline.** A source's key can change (`/login`, `codex login`). Spend belongs to the key in force when the
  response ended: the app keeps, per source, the key changes it saw (`<data>/quota/estimate.json`, 10.4) and gives
  the first key it ever saw to the responses before it.
- **Codex history.** Rollouts do not name the account, and users switch accounts (seen 2026-11-11: three Codex
  accounts within 8 days, each used until 70–100 %). So a Codex response belongs to the window instances (10.4) of
  the token count line that reports it (the line right after the response, at most 5 s later, in the same file), and
  instances named by one line belong to one account (union-find over the instances). A chain observed while
  `auth.json` gave a key takes that key (the key in force at its newest line read at or after the first key seen;
equal keys are one account); a chain never seen live is an account of its own, keyed as vendor `codex-chain` with
  id = its smallest instance tag in string order (`<kind>:<name>:<minutes>@<first R of the instance>`, R in ms). A response without such a
  line (API-key sign-in, another provider, a bucket other than `codex`) is no subscription spend and is left out.
- **Remote hosts.** The relay adds `account` (the key) to its quota line (REMOTE 11.1). A host's sources and this
  computer's sources with equal keys are one account unless their fingerprint contradicts it (an unknown one shares).

### 10.2 Plan and cap class

- `plan` and `tier`: Codex the `plan_type` of the account (a null `plan_type` takes the account's nearest earlier
  non-null one, else its first later one: newer Codex builds write null); Claude CLI `oauthAccount.organizationType` and
  `organizationRateLimitTier` (observed `claude_max`, `default_claude_max_20x`); Desktop inherits them on a shared
  account; else null. A value is kept only when it is 1–64 characters of `[a-z0-9_.-]`, else null.
- **Cap class.** A cap belongs to the plan, not to the person: estimates pool every account of the same
  `(vendor, plan, tier, window)` on this computer, so the user's several Codex accounts of one plan share their
  evidence. Without a plan the class is `(account, window)`.
- A plan change moves the account to another class from that moment; the evidence of the old plan stays with it. An
  instance that spans the change splits in two (same `R`), one per plan; the newer part is the current one.
  `window` is `kind`, `name` and `minutes` (section 2).

### 10.3 Spend ledger

Rows per (account, source, price-table model id, UTC minute of the response's `endMs`): `costMicroUsd` (METRICS 9.3),
unpriced requests, and the input + output tokens of priced and of unpriced responses. Kept for the last 8 days.
- Fed by the history analysis (the same parsing, dedupe and copied-history rules as METRICS 8, kept as per-file
  per-minute contributions for minutes within 8 days), the live engine (an update replaces a response's earlier
  contribution, as in history) and remote hosts' streamed responses.
- Left out: responses whose provider is not the vendor's own subscription route (Codex `model_provider` other than
  `openai`; Claude model ids in a Bedrock or Vertex form, METRICS 9.2 *cloud* step).
- Sums over an account's rows, for a span `[a, b]`:
  - `C⁻(a, b)`: the minutes wholly inside it (a lower bound of the spend that ended in the span; unpriced counts 0);
  - `C⁺(a, b)`: the minutes touching it (an upper bound), with each unpriced token at the highest cost per token of
    the account's priced models in the ledger (none priced: `∞`). Seen 2026-11-11: Codex's automatic-approval
    reviewer has no price and would otherwise leave no upper bound at all.
- A `modelWeek` window counts only the models whose price-table id contains each word of the window's name as a
  `-`-separated token, case-insensitive (`Fable` ↔ `claude-fable-5-2`).

### 10.4 Observations

Every window with a numeric `usedPercent` and a known `resetsAtMs` gives an observation `(account, window, t, tLo, p,
R)`: `t` its `observedMs`, `R` its reset.
- **`tLo`**, the earliest moment the value may describe. Codex and the Claude CLI take the limits from the headers
  of a response, which arrive when the response starts. Codex: the start of the response the token count follows.
  Claude CLI: the start of the earliest `claudeCode` response of the account that started before `t` and ended at or
  after `t − λ`. Otherwise `t`. (Sorted by `t`, Codex values drop by a point now and then: parallel agents' headers.)
- **Resolution** `q` per (source, account, window): the finest of its stored values, a value's own being 1 for
  integers (`23.0` included), 0.1 for one decimal, else 0.01. The vendor's rounding (down, nearest, up) is unknown, so
  a value `p` stands for a true value in `(p − q, p + q)`.
- **Instances.** Observations of one account and window whose `R` are within 2 minutes of the first are one window
  instance. `S = max R − minutes` starts the lower-bound sums and `S⁺ = min R − minutes` the upper-bound ones; for a
  `fiveHour` window whose `R` is a whole hour, `S⁺` is one more hour earlier (the reset may be rounded up). An
  observation with `t` outside `[S⁺ − 1 min, R + 1 min]` is skipped.
- **Sources of observations.** Codex: every token count line with rate limits (3), so that history exists at the
  first launch. Claude CLI: each change of the quota file (5.5). Desktop: each new cache entry (4.4). Phase 2: each
  response. Remote hosts: each quota line.
- **Store.** `<data>/quota/estimate.json` (0600, a temporary file then a rename, written at most once a minute and at
  quit): `{"version": 2, "observations": [...], "instances": [...], "keys": [...]}` (version 1 keyed accounts with a
  random salt and is replaced) with the last 8 days of
  observations (runs of equal values keep their first and last; above 256 per instance each run keeps its last; still
  above, 256 runs evenly spaced, first and last included), the closed instances' intervals (10.5, at most 64 per
  class, newest kept, each with its class, account, `R`, `lo`, `hi` (null for unbounded), `pMax` and `tLast`) and the
  key timeline of 10.1 (source, `at`, key, plan, tier; a `claudeCode` entry adds `orgKey`, its organization's
  `claudeDesktop` key; `claudeDesktop` entries hold Desktop's own keys). On load, stored observations and instances
  equal to fresh ones count once, and a stored Desktop observation takes the key Desktop spends on at its `t`. Account
  keys are stored, ids never. A file that does not parse is replaced.

### 10.5 Segments

With `λ = 2 min` and the sums of 10.3. Consecutive observations of an instance with equal `p` form a run, with its
first time `t₁`, last time `t₂` and largest `tLo`.
- **Cumulative** segment per run with `p > 0`:
  - `lo = 100 · C⁻(S, tLo − λ) / (p + q)`;
  - `hi = 100 · C⁺(S⁺, t₁ + λ) / (p − q)`, `∞` when `p − q ≤ 0`.
  - A value of 100 or more gives no lower bound (vendors stop at 100 % while usage goes on); its `hi` uses
    `p = 100`.
- **Increment** segments: anchors are the runs with `p > 0`, each at least `max(3q, 10)` points above the previous
  anchor. Each pair of anchors `a < b` (at most 32 pairs, the widest first) gives, with `Δ = p_b − p_a` and
  `q₂ = q_a + q_b`:
  - `lo = 100 · (C⁻(S, tLo_b − λ) − C⁺(S, t₁_a + λ)) / (Δ + q₂)`;
  - `hi = 100 · (C⁺(S⁺, t₁_b + λ) − C⁻(S⁺, tLo_a − λ)) / (Δ − q₂)`.
  An increment cancels any spend the vendor left uncounted before `a`.
- Spend after the current time does not exist yet: a sum ends at `now` at the latest.
- An instance keeps at most 64 segments: every increment and, evenly spaced, the cumulative ones.
- Lower bounds hold even when usage is invisible here (other computers, claude.ai on the web or phone, Codex cloud):
  that usage only raises `U`. Upper bounds hold only for clean spans.
- **Instance interval.** `L` = the largest lower bound, `H` = the smallest upper bound not below `L` (an upper bound
  below a lower bound of the same instance is provably wrong). When a cumulative lower bound lies above an increment's
  upper bound, the vendor left part of the window's first spend uncounted (seen on the user's Codex accounts: a
  straight line through their values crosses zero 3–10 points late), or an increment saw unseen usage: the instance
  then uses its increments alone. An instance keeps `(L, H, pMax, tLast, R)`; closed ones are stored (10.4).
- Worked example (the fixture `segments[0]`): $1 ends at `S + 10i min + 30 s` for `i = 1…60`, observations 2 % at
  100 and 130 min, 5 % at 300, 12 % at 610, `q = 1`. Run 2 %: `lo = 100 · $12 / 3 = $400`, `hi = 100 · $10 / 1 =
  $1000`. Run 5 %: `$29 / 6 · 100 = $483`, `$30 / 4 · 100 = $750`. Run 12 %: `$461`, `$545`. Increment 2 → 12 %:
  `100 · ($60 − $10) / 12 = $417`, `100 · ($60 − $12) / 8 = $600`. Instance: `[$483, $545]`.

### 10.6 Estimate

Per cap class, pooling the current and the stored instances.
- **Weight** of an instance: `w = min(1, pMax / 20) · 0.5^(age / halfLife)`, `age = now − tLast`, half-life 7 days
  for windows of 10 000 minutes or more, else 2 days. `w < 0.05` is dropped.
- **Lowered cap.** When the two newest instances with `H / L − 1 ≤ 0.08` agree with each other, older instances whose
  `L` lies above both are dropped: unseen usage never raises a lower bound, a cut cap does.
- **Vote** on `K`: `loss(K) = Σ w · ([K < L] + 0.25 · [K > H])`. Lower bounds are trusted four times more than upper
  bounds. The loss is evaluated on every endpoint and every open cell between endpoints; the cells of least loss,
  merged where they touch, form regions. The region with the most weight of instances holding it wins, then the
  lowest. It is `[Klo, Khi]`.
- **Cap** `K̂ = √(Klo · Khi)`. With `Khi = ∞` there is no cap, only `atLeast = Klo`.
- **Published range** `[min(Klo, K̂ / 1.02), max(Khi, K̂ · 1.02)]`: a window of a few percent cannot claim more than
  ±2 % (the vendors' metering is not exactly the API price list).
- **Confidence**:
  - `high`: `Khi / Klo − 1 ≤ 0.08`, the instances holding the region weigh ≥ 1.5, at least two of them are themselves
    at most 8 % wide, and no instance's `L` lies above the region;
  - `medium`: `Khi / Klo − 1 ≤ 0.25` and the holders weigh ≥ 0.5;
  - `low`: any other estimate, `atLeast` included; `none`: no instance with a lower bound.
- **Coverage** of the current instance: `C⁺(S, t) / (K̂ · p / 100)`, clamped to 0–1, when `p ≥ 5` (the share of the
  account's usage this computer saw). **Account-wide used** ≈ `K̂ · p_live / 100`.

### 10.7 Live percent

- While `now < R` and a cap exists: `p_live = p + 100 · (C⁺(S, now) − C⁺(S, t)) / K̂`, at least `p` and at most
  `max(100, p)`; it is *estimated* when it exceeds `p` by 0.5 or more. A new vendor value replaces it.
- After `R`: a new instance starts, `S′ = R + n · minutes` (the latest such start not after `now`) for weekly and other
  windows; for `fiveHour`, the minute of the start of the first local response that started at or after `R` and has ended
  (none yet: 0 %, not estimated, no reset).
  `R′ = S′ + minutes`, and `p_live = 100 · C⁺(S′, now) / K̂` (estimated) with `R′` as the reset.
- Without a cap: the vendor value, then 0 % after `R` (section 2).

### 10.8 Display data

- **Used**: `C⁺(S, now)`, this computer's and its hosts' measured spend in the current instance, in the display
  currency like other costs (METRICS 9.8; DESIGN 9 *Amount*).
- **Cap**: `≈K̂`, or `≥atLeast`; nothing at confidence `none`.
- **Details** (hover, DESIGN 4): account-wide used ≈, the published range, confidence, coverage, plan and tier, and
  the number of windows it rests on.

### 10.9 Validation

**Simulation** (`quota_estimate_sim.py --accounts 100`, seed 7). Accounts with caps of $50–2000, two models, one to
three parallel agents, responses of 5 s–8 min whose percent the vendor reports as of their start plus up to 20 s;
four weekly windows each, used to 65–100 %. Relative error of `K̂`, median / 90th percentile in %, when the window's
percent first reaches the given value (window n has n − 1 closed windows before it); then the share of `high`
estimates whose published range holds the truth:

| Scenario | w1 at 5 % | w1 at 20 % | w1 at 60 % | w2 at 20 % | w3 at 20 % | high holds truth |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| clean, integer percent | 1.2 / 4.8 | 0.2 / 0.6 | 0.1 / 0.3 | 0.1 / 0.2 | 0.1 / 0.2 | 100 % |
| clean, 0.1 % resolution | 0.3 / 1.2 | 0.1 / 0.2 | 0.0 / 0.1 | 0.0 / 0.1 | 0.0 / 0.1 | 100 % |
| integer percent rounded down | 11.6 / 13.6 | 2.7 / 3.2 | 0.9 / 1.0 | 1.0 / 2.6 | 0.8 / 1.3 | 100 % |
| 20 % of windows with 20–60 % unseen usage | 1.9 / 10.6 | 0.3 / 11.0 | 0.1 / 4.2 | 0.1 / 0.3 | 0.1 / 0.2 | 100 % |
| 50 % of windows with 20–60 % unseen usage | 1.4 / 44.3 | 0.6 / 54.6 | 0.4 / 22.0 | 0.1 / 13.3 | 0.1 / 6.6 | 98.2 % |
| vendor lag up to 3 min (λ is 2) | 3.6 / 11.2 | 0.5 / 2.0 | 0.3 / 0.8 | 0.1 / 0.6 | 0.1 / 0.4 | 100 % |
| first 3–10 % of each window uncounted | 315 / 463 | 2.7 / 6.4 | 0.3 / 0.9 | 0.5 / 2.8 | 0.2 / 0.8 | 100 % |
| vendor weights one model 20 % above its price | 3.8 / 8.4 | 3.1 / 5.7 | 2.8 / 5.4 | 3.2 / 5.3 | 2.5 / 4.4 | 32 % |
| five-hour windows | 1.2 / 3.9 | 0.3 / 0.9 | 0.2 / 0.7 | 0.3 / 0.9 | 0.2 / 0.5 | 100 % |
| cap raised 50 % from window 3 | 1.4 / 5.1 | 0.2 / 0.8 | 0.1 / 0.2 | 0.1 / 0.2 | 0.2 / 0.7 | 99.3 % |
| cap cut by a third from window 3 | 1.3 / 4.9 | 0.2 / 0.8 | 0.1 / 0.2 | 0.1 / 0.2 | 50.0 / 50.2 | 100 % |

- The targets hold: clean data within 5 % once one window reaches 20 %; half the windows contaminated, within 10 %
  from the third window; `high` holds the truth in ≥ 90 % wherever the cap is well defined.
- Tuning that got there, from the first design: one interval per instance before the vote (votes per segment let a
  contaminated window with many segments outvote a clean one); `high` needs two narrow instances and no lower bound
  above it (two contaminated windows otherwise agreed by chance); a value of 100 % gives no lower bound; increments
  over every anchor pair, and increments alone when a window's start went uncounted; the ±2 % floor of the range.
  The 0.25 penalty and λ = 2 min were kept.
- Limits: a lowered cap is followed only from the second agreeing window (`low`/`medium` before); a vendor whose
  metering weights models differently from the API prices gives a cap that depends on the model mix (2–4 %); an
  account that is always partly used elsewhere looks like a smaller cap, and nothing on one computer can tell.

**Real data** (2026-11-11, the user's Mac, read-only, `quota_estimate_local.py`): Codex `promax` 7d `≈$400`
(`$392–408`, `medium`; the earlier window alone gives `$399–401`, the current one `$53` at 42 % with coverage 0.15:
other usage of the account or a lowered cap, which a second window will tell); later the same day `≈$397`
(`$390–405`, `low`, at 57 %). Claude: a first run merged Desktop into the CLI's account on the organization alone and
gave 7d `≈$5742`; the fingerprint then showed two accounts (above), so that figure added another account's spend to
the lower bounds and is void. Separated, the CLI's `claude_max` / `default_claude_max_20x` 7d stood at 100 % (no lower
bound, so no estimate yet) and Desktop's 7d and Fable 7d had just reset (0 %).

### 10.10 Known approximations

- Vendors meter in their own units; API-equivalent dollars approximate them (`mix` in reports shows the models).
- History before the first launch gives Claude spend to the key read at launch (10.1).
- `λ` covers lags up to 2 minutes; Claude Desktop's cached value may be older than its `Date` (unverified).
- Claude's 5-hour and weekly windows are assumed to count from `R − minutes` (unverified); the whole-hour slack of
  10.4 covers a rounded 5-hour reset.

## 11. Fixtures

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
- Estimates (section 10): `spec/fixtures/quota-estimate-cases.json`, written and checked by
  `spec/tools/quota_estimate.py --write` / `--check-fixtures`, with account keys, the fingerprint (confirmation,
  contradiction, no overlap, a minute's jitter, another window, a pair only after `at`, a tie, the latest pair
  deciding), the Desktop merge rule and equal remote keys, plan
  strings and Codex account chains, resolution, segments (cumulative, increments, λ, `tLo`, unpriced tokens, 100 %,
  the five-hour slack, `modelWeek` filtering), instance intervals, the vote (ties, unbounded, a degenerate region,
  dropped weights), weights and half-lives, the estimate (published range, confidence, the lowered-cap rule), a plan
  change, the live percent before and after a reset, report entries (rounding of micro-USD and percent, checked
  against `report-schema.json`) and the send cadence. Some values are also checked against hand-worked numbers.
- The estimate pipeline (`pipeline` in the same file, from `spec/tools/quota_estimate_pipeline.py`): small raw inputs
  (responses with start, end, model and cost or tokens; Codex token count lines; `auth.json` and `.claude.json`
  changes; quota snapshots with Desktop's organization; a stored `estimate.json`) and the outputs a port must match,
  each with a `why`:
  - ledger sums `C⁻`/`C⁺` with an unpriced response priced at the highest rate, `modelWeek` token match, an
    unbounded `C⁺`, Bedrock and non-`openai` routes left out;
  - Codex chains: two accounts switching, one never seen live, a response without a token count line and one whose
    line is 6 s late;
  - the Desktop merge (personal plan, team plan, another organization, another account of the same organization,
    no CLI values, Desktop's value before the CLI's) and the key timeline (spend before the first key, a `/login`);
  - cap classes: two accounts of one plan pooled, a null `plan_type` inheriting, no plan; the lowered-cap rule; a
    plan change inside a window;
  - the live percent before a reset and after it (five-hour first response, weekly `n · minutes`);
  - the store: its shape, a read back giving the same estimate (with and without Desktop), the 256 and 64 limits,
    another version ignored;
  - report entries (order, rounding, Desktop of a team organization) and a 15-step send cadence (spend only, 60 s,
    percent, confidence, reset state, set change, a size drop counting as unsent, `[]` once when switched off, a new
    run, leaving).
- Phase 2: every response shape of section 9 (Grok config from a log and from the API, Devin rows, Antigravity
  summaries and `GetUserStatus`, Kimi current and legacy, Gemini buckets, Factory limits, z.ai limits, Qoder and
  WorkBuddy summaries), named-window labels and order, and Chrome cookie decryption vectors for macOS `v10`, Linux
  `v10`/`v11` (with and without the host hash) and Windows `v10` (AES-GCM, given key).
