# Agent Speed — Local Network Specification

Agent Speed can share its live speed with devices on the user's local network, such as a desk display, a home
automation hub or a script. It works in two directions:

- **Devices read** (the *server*): the app listens on one TCP port. A device fetches the latest frame over HTTP or
  keeps a WebSocket or plain TCP connection open and receives every new frame.
- **The app sends** (the *targets*): the app sends every new frame to addresses the user added, as UDP datagrams
  (one device or a broadcast) or as HTTP `POST` requests (a device or a webhook).

Every transport carries the same message, the **frame** (section 3), so a device parses the same JSON whichever way
it receives it. This document is the contract for device makers and for the three apps. The UI is DESIGN 14; the
network rules are NETWORK 8. Status: implemented by the macOS, Windows and Linux apps.

## 1. Principles

- **Numbers only.** A frame holds the live values of METRICS 11.2 (output speed, its estimated share, agent counts)
  overall and per source, a one-minute history of the output speed, the computer's name and two random ids. It never
  holds a model, project, path, host, prompt, message, tool, cost or anything else.
- **Local network only.** The server closes every connection from an address that is not local (2.3), and a target
  receives frames only while every address its host resolves to is local. No proxy is ever used and no redirect is
  followed, so frames cannot leave the local network through the app.
- **Off by default.** The server and every target are switched separately. Both obey the master switch
  `networkEnabled` (NETWORK 1): with it off, the port is closed and nothing is sent.
- **Plain text.** There is no TLS. Anyone who can watch the network traffic can read frames and the access key. The
  access key (4.4) keeps other devices from reading the server; a target's key (5.4) lets a device check that a frame
  came from this computer. A broadcast reaches every device on the network.
- **Read-only.** Nothing a peer sends changes the app: the server answers `GET` requests only and ignores the rest.

## 2. Settings

### 2.1 Storage

One JSON value under the settings key `lan` (Linux: the JSON text as a string in the `[General]` group of the app's
QSettings file), written atomically on every change:

```json
{
  "version": 1,
  "instance": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "server": { "enabled": true, "port": 7247, "key": "K7Q4M2X9PT3D" },
  "targets": [
    { "id": "8c0d6b0a-5a8e-4b0e-9d4e-2f1f3c5a7b90", "name": "Desk display", "kind": "udp",
      "address": "192.168.1.50:7247", "key": null, "enabled": true },
    { "id": "1b9e8a3c-2d4f-4e6a-8b0c-7d5e3f1a9c24", "name": "", "kind": "http",
      "address": "http://homeassistant.local:8123/api/webhook/agent-speed", "key": "s3cret-phrase", "enabled": true }
  ]
}
```

| Member | Rule |
| --- | --- |
| `version` | `1`. Another value: the whole document reads as the defaults (and is overwritten on the next change). |
| `instance` | A random UUID (version 4, lowercase), created the first time the document is written and never changed. A missing or invalid value is replaced. |
| `server.enabled` | Default `false`. |
| `server.port` | 1024–65535, default `7247`. Out of range or not an integer: the default. |
| `server.key` | The access key: 12 characters of the Crockford Base32 alphabet `0123456789ABCDEFGHJKMNPQRSTVWXYZ`, from the system's cryptographic random generator. Created when the server is first turned on; a missing or invalid value is replaced at that moment. Shown in groups of four (`K7Q4-M2X9-PT3D`). |
| `targets` | At most 8, in the order added. An entry with an unknown `kind`, an invalid `address` (5.1) or a duplicate `id` is left out; entries past the eighth are left out. |
| `targets[].id` | A random UUID, lowercase. |
| `targets[].name` | 0–64 characters, trimmed; empty means the address is shown instead. |
| `targets[].kind` | `udp` or `http`. |
| `targets[].address` | As 5.1, stored as the user typed it after trimming. |
| `targets[].key` | `null`, or 1–128 characters without control characters, trimmed. |
| `targets[].enabled` | Default `true`. |

The key is a read credential for numbers that are sent in plain text anyway, so it is stored with the other settings
(not in the keychain or credential store): the user must be able to see and copy it at any time.

### 2.2 Master switch

With `networkEnabled` off the server closes its port and every connection (WebSocket close code 1001), targets send
nothing and their status becomes `networkOff`. Turning it back on starts both as after a launch.

### 2.3 Local addresses

An address is **local** when it is in one of these ranges:

| Family | Ranges |
| --- | --- |
| IPv4 | `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16` (private), `169.254.0.0/16` (link-local), `127.0.0.0/8` (loopback) |
| IPv6 | `::1`, `fe80::/10` (link-local), `fc00::/7` (unique local); an IPv4-mapped address (`::ffff:a.b.c.d`) by its IPv4 rule |

For a UDP target only, `255.255.255.255` (limited broadcast) is local too. A directed broadcast such as
`192.168.1.255` is local because it lies in a private range. Everything else is not local, including
`100.64.0.0/10`, `0.0.0.0`, multicast and every public address.

## 3. The frame

### 3.1 Members

A frame is one JSON object, UTF-8, compact (no insignificant whitespace), members in this order:

```json
{"schema":1,"type":"live","instance":"3f2504e0-4f89-41d3-9a0c-0305e82c3301","name":"Zoe's MacBook Pro","run":"9b1c4e7a2f6d8e03","seq":1342,"at":"2026-11-07T09:00:00.250Z","phase":"generating","outputTokS":75.9,"estimatedShare":0.42,"estimated":true,"sessions":{"active":4,"generating":2,"tools":1},"sources":[{"source":"codex","phase":"generating","outputTokS":75.9,"estimatedShare":0.42,"sessions":{"active":3,"generating":2,"tools":0}},{"source":"claudeCode","phase":"tools","outputTokS":0,"estimatedShare":0,"sessions":{"active":1,"generating":0,"tools":1}}],"history":{"stepMs":1000,"outputTokS":[61.2,70.4,75.9]}}
```

| Member | Meaning |
| --- | --- |
| `schema` | `1`. A later schema only adds members; a device must ignore members it does not know. |
| `type` | `"live"`. |
| `instance` | The settings `instance` (2.1): tells computers apart. |
| `name` | The computer's name: macOS the Computer Name (System Settings → General → Sharing), Windows `Dns.GetHostName()`, Linux the host name. Trimmed of white space, cut to 64 Unicode scalar values and trimmed again; empty becomes `Agent Speed`. |
| `run` | 16 lowercase hex digits from the cryptographic random generator, new at every app launch. |
| `seq` | 1 for the first frame of a run, then +1 for every frame built (3.3), whichever transports send it. A device that receives frames out of order (UDP) keeps the one with the higher `seq` of the same `run`. |
| `at` | The wall clock at the frame's engine tick, UTC, `YYYY-MM-DDTHH:MM:SS.sssZ`. |
| `phase` | `generating` when `sessions.generating > 0`, else `tools` when `sessions.tools > 0`, else `idle`. |
| `outputTokS` | METRICS 11.2 `live.outputTokS`: output tokens per second over the last 3 s, every agent of every enabled source on every host, in-flight estimates included. |
| `estimatedShare` | METRICS 11.2 `live.estimatedShare`, 0–1: the part of `outputTokS` that is estimated. |
| `estimated` | `estimatedShare > 0` (after rounding): a device may show an *EST* mark. |
| `sessions` | METRICS 11.2 `live.agents`: `active` (agents with activity in the last 10 minutes), `generating` (a model request in flight), `tools` (waiting for tool results). |
| `sources` | One entry per source with activity (3.2). |
| `history` | `stepMs` (always `1000`) and `outputTokS`, the last 60 one-second samples of `outputTokS`, oldest first (3.4). |

Values follow METRICS 11.1 (scope: every enabled source, never an orb's subset, every host) and are rounded as METRICS
11.7: tok/s to 0.1, shares to 0.001, half away from zero, each value on its own. Tok/s is clamped to
0–1 000 000 000 and counts to 0–100 000, as in reports. Numbers are written as reports write them: plain decimals
with trailing zeros and a trailing point removed (`75.9`, `0.42`, `0`, `1200`). Strings escape only `"`, `\` and
control characters (`\n`, `\r`, `\t`, `\b`, `\f`, otherwise `\u00XX` in lowercase hex); everything else stays UTF-8.
So the same values give the same bytes on every platform.

### 3.2 Sources

`sources` lists, in the source order of REPORTING 6.4 (the order of each app's source list, `qoderCli` last), every
enabled source whose `sessions.active` is above 0 or whose rounded `outputTokS` is above 0. Each entry has, in this
order: `source` (the REPORTING 6.4 id), `phase`, `outputTokS`, `estimatedShare`, `sessions`, computed as in 3.1
restricted to that source. With no such source the array is empty. A device must accept and may ignore a source id it
does not know.

### 3.3 Building and changes

While the feed runs (the server listens or any target is enabled, and `networkEnabled` is on), a frame is built at
every engine tick (METRICS 6: every 500 ms) with `seq` + 1. A frame is **changed** when any member other than `seq`
and `at` differs from the previous frame's. The feed stops when neither the server nor any target is active; its
history and its last frame are then dropped, and `seq` continues where it was.

### 3.4 History

The feed samples the rounded `outputTokS` once per second on the monotonic clock. With `s0` the time of the feed's
first tick, sample `k` is taken at the first tick at or after `s0 + k · 1000 ms`. When a tick comes later than one
whole step after the previous sample (a stalled or sleeping computer), every missed sample takes the value of that
tick; when it comes 60 s or more after the previous sample, the history restarts with that tick as `s0`. The frame
carries the newest 60 samples; right after the feed starts the array is shorter (one sample at the first tick).

### 3.5 Size

A frame is at most 16 KiB. A UDP datagram (5.2) is at most 1400 bytes, signature line included: when it would be
larger, `history` is left out; when it still would be, `sources` is left out too. A device must accept a frame
without `history` or `sources`.

### 3.6 Errors

The server's error messages share the frame's envelope: `{"schema":1,"type":"error","error":"<code>"}` with the codes
of 4.3 and 4.6.

## 4. Server: devices read

### 4.1 Listening

- **Port**: `server.port`, TCP, on every interface, IPv4 and IPv6 (IPv4 only where IPv6 is unavailable). A port that
  is taken gives the status `portInUse`; the server tries again every 30 s and whenever the port setting changes.
  Changing the port closes the old port and every connection on it.
- **Peers**: a connection from an address that is not local (2.3) is closed at once, before anything is read. At
  most 16 connections are open at a time; a further one is closed at once.
- **Discovery**: while listening, the app registers the DNS-SD service `_agentspeed._tcp` on the port with the
  instance name `name` (3.1; cut to 63 UTF-8 bytes at a scalar boundary; the system renames it on a conflict) and the
  TXT record `schema=1`, `id=<instance>`. macOS uses Bonjour, Windows `DnsServiceRegister`, Linux Avahi over D-Bus
  when the Avahi daemon is running. A failed registration is logged and changes nothing else.

### 4.2 The first line

The server reads the connection's first line (up to `\n`, a preceding `\r` removed) within 5 s and 8192 bytes:

| First line | Protocol |
| --- | --- |
| `METHOD SP target SP HTTP/1.x`: `METHOD` one or more letters `A`–`Z`, `x` `0` or `1` | HTTP (4.3) |
| starts with `{` | Line protocol (4.6) |
| anything else, nothing within 5 s, or 8192 bytes without `\n` | closed at once, no reply |

### 4.3 HTTP

The request head (first line and header lines up to an empty line) must arrive within the same 5 s and 8192 bytes;
otherwise the connection is closed without a reply. Header names are case-insensitive; of a repeated header the first
counts. The server never reads a request body. Checks, in this order:

| Check | Response |
| --- | --- |
| a header line without `:` | `400` `badRequest` |
| the peer is locked out (4.4) | `429` `tooManyAttempts`, `Retry-After: <seconds left, rounded up>` |
| the method is not `GET` | `405` `methodNotAllowed`, `Allow: GET` |
| the path (the target up to `?`) is neither `/v1/live` nor `/v1/stream` | `404` `notFound` |
| no valid access key (4.4) | `401` `unauthorized`, `WWW-Authenticate: Bearer realm="agent-speed"` |
| `/v1/live` before the feed's first frame | `503` `starting` |
| `/v1/live` | `200` with the latest frame (the one built at the latest tick, never a new one) |
| `/v1/stream` with a valid WebSocket upgrade | `101`, then the stream (4.5) |
| `/v1/stream` without `Upgrade: websocket` | `426` `upgradeRequired`, `Upgrade: websocket` |
| `/v1/stream` with another `Sec-WebSocket-Version` than `13`, or without a `Sec-WebSocket-Key` that is the Base64 form of 16 bytes | `400` `badRequest`, `Sec-WebSocket-Version: 13` |

A valid upgrade has `Upgrade` containing the token `websocket` and `Connection` containing the token `upgrade`
(case-insensitive, comma-separated lists), `Sec-WebSocket-Version: 13` and the key. The `101` response carries
`Upgrade: websocket`, `Connection: Upgrade` and `Sec-WebSocket-Accept` (RFC 6455: Base64 of the SHA-1 of the key
followed by `258EAFA5-E914-47DA-95CA-C5AB0DC85B11`); no subprotocol, no extension (offers are ignored).

Every other response is `HTTP/1.1 <code> <reason>` with the headers `Content-Type: application/json; charset=utf-8`,
`Content-Length`, `Cache-Control: no-store`, `Connection: close` and those named above, and a body that is the frame
or the error object (3.6). The connection is closed after the response. Reasons: `OK`, `Bad Request`,
`Unauthorized`, `Not Found`, `Method Not Allowed`, `Upgrade Required`, `Too Many Requests`, `Service Unavailable`.
There are no CORS headers.

### 4.4 Access key

- **Where**: the header `Authorization: Bearer <key>` (scheme case-insensitive), else the query parameter `key`
  (`/v1/live?key=K7Q4-M2X9-PT3D`, the first `key` parameter, percent-decoded), else, on the line protocol, the
  hello's `key`. A request with neither has no key.
- **Comparison**: hyphens and spaces are removed, ASCII letters upper-cased, and the result is compared with
  `server.key` in constant time; a different length is a mismatch.
- **Lockout**: a missing or wrong key is a failure of the peer's address. The 10th failure within 60 s locks that
  address out for 60 s from that failure: every attempt from it is then answered `tooManyAttempts` without checking a
  key. A valid key clears the address's failures. The server remembers at most 256 addresses (the least recently
  failed is forgotten first).
- **A new key** (the user regenerates it) closes every WebSocket (close code 1008) and line connection at once; the
  devices must use the new key.

### 4.5 WebSocket stream

After `101` the server sends frames as text messages: one unfragmented, unmasked frame per message (RFC 6455). It
sends the latest frame at once, then follows 4.7. From the client it accepts:

| Client frame | Handling |
| --- | --- |
| ping | a pong with the same payload |
| pong | ignored |
| close | a close frame with the same status code (none when the client sent none), then the connection is closed |
| text, binary, continuation | ignored; a payload above 4096 bytes closes the connection with 1009 |
| an unmasked frame, a reserved bit or opcode, a control frame above 125 bytes or fragmented | close 1002 |

When the server stops (the switch, the master switch, a new port, the app quitting) it sends close 1001 first.

### 4.6 Line protocol

The first line is a JSON object with `"type":"hello"` and `"key"` (a string); other members are ignored:

```text
→ {"type":"hello","key":"K7Q4-M2X9-PT3D"}
← {"schema":1,"type":"live",…}
← {"schema":1,"type":"live",…}
```

After a valid hello the server writes frames, each one line of compact JSON followed by `\n`: the latest frame at
once, then as 4.7. Anything the client sends after the hello is read and discarded; end of input closes the
connection. A line that is not such an object gives `badRequest`, a missing or wrong key `unauthorized` (a failure,
4.4), a locked-out peer `tooManyAttempts`: the error object (3.6) on one line, then the connection is closed.

### 4.7 Push rules

Every push receiver (a WebSocket or line connection, a target) sends:

1. a frame at once when it starts (a connection: right after the upgrade or hello; a target: at the feed's first tick
   after it is enabled);
2. every changed frame (3.3), but at most one per *minimum interval*: a changed frame that comes sooner is held and
   the latest held frame goes out at the first tick once the interval has passed;
3. the latest frame whenever nothing was sent for the *heartbeat* interval, so a device can tell a quiet computer
   from a gone one.

Engine ticks do not come exactly 500 ms apart, so an interval counts as passed 100 ms early (a tick 490 ms after the
last send may send again; one 380 ms after it may not). A receiver that is still sending its previous frame skips
frames and sends the latest one when it is free; frames are never queued. A connection with more than 64 KiB not yet
sent is closed.

| Receiver | Minimum interval | Heartbeat |
| --- | --- | --- |
| WebSocket, line protocol | 500 ms (every tick) | 5 s |
| UDP target | 500 ms (every tick) | 1 s |
| HTTP target | 1 s | 10 s |

A device should treat the computer as gone after three heartbeat intervals without a frame.

## 5. Targets: the app sends

### 5.1 Addresses

| Kind | Form | Example |
| --- | --- | --- |
| `udp` | `host:port`, port 1–65535; `host` an IPv4 address, `[IPv6 address]`, or a DNS or `.local` name | `192.168.1.50:7247`, `192.168.1.255:7247`, `desk-display.local:7247` |
| `http` | `http://host[:port]/path[?query]`, no user information, no fragment; port 80 by default, path `/` by default | `http://192.168.1.50/agent-speed` |

`https` is not supported (1, *Plain text*). A name is resolved with the system resolver before each HTTP request and
at most every 30 s for UDP (again after a send error). Only addresses that are local (2.3) are used: when the name
resolves to any address that is not local, the target's status is `notLocal` and nothing is sent. The connection
or datagram goes to the checked address itself, never to a second lookup.

### 5.2 UDP

One datagram per frame sent: the frame's bytes; with a target key, followed by `\n` and the signature line (5.4).
Broadcast is allowed on the socket. At most 1400 bytes (3.5). A send error gives `failed` until a later send
succeeds.

### 5.3 HTTP

The app opens a TCP connection to the checked address and port and writes:

```text
POST /api/webhook/agent-speed HTTP/1.1
Host: homeassistant.local:8123
User-Agent: AgentSpeed/1.7.0 (macOS)
Content-Type: application/json
Content-Length: 512
X-AgentSpeed-Signature: sha256=5d2c…
Connection: close

{"schema":1,"type":"live",…}
```

`Host` is the host and port as written in the address (the port only when given); the path and query as written;
`X-AgentSpeed-Signature` only with a target key. The app reads the status line and closes the connection: `2xx` is
a success, any other status `httpStatus` with the code; no connection, a refusal or nothing within 3 s is
`unreachable`. Redirects are not followed and no proxy is used. One request at a time per target. After a failure
the target waits 2, 4, 8, 16 then 30 s (doubling, capped) before the next request, which carries the latest frame.

### 5.4 Signature

With a target key, the signature is `sha256=` followed by the lowercase hex HMAC-SHA256 of the frame's exact bytes,
keyed with the target key's UTF-8 bytes. A device recomputes it over the bytes it received (the datagram up to the
first `\n`, or the HTTP body) and may also reject a frame whose `at` is far from its own clock or whose `seq` is not
above the last one of the same `run`.

### 5.5 Status

| Status | When |
| --- | --- |
| `off` | the target is switched off |
| `networkOff` | `networkEnabled` is off |
| `ok` | the latest attempt succeeded (HTTP `2xx`; UDP sent) |
| `notFound` | the name does not resolve |
| `notLocal` | the name resolves to an address that is not local |
| `unreachable` | HTTP: no connection or no status line within 3 s |
| `httpStatus` | HTTP: a status other than `2xx` (the code is shown) |
| `failed` | UDP: the system refused the datagram (for example no route) |

The app remembers the time of the last success for the status line; nothing is persisted.

## 6. Security

| Threat | Answer |
| --- | --- |
| Another device on the network reads the speed | The server needs the access key; regenerating it locks out every old device (4.4). |
| Guessing the key | 12 Base32 characters (60 bits) and the lockout of 4.4. |
| A web page in the user's browser reaches the server (DNS rebinding, cross-site requests) | Every request needs the key, the server answers `GET` only, changes nothing and sends no CORS headers. |
| Frames leave the local network | Local peers only (4.1), local target addresses only, checked after resolution (5.1); no proxy, no redirects. |
| A fake computer sends frames to a device | A target key and the signature of 5.4. |
| Too many or slow connections | 16 connections, 5 s and 8 KiB for the request head, 64 KiB unsent, 4 KiB client messages. |

Logs name the peer address, the protocol and status codes; never the key, a frame or a request's query.

## 7. Tests

`spec/tools/lan_reference.py` is the reference implementation (standard library only). `--check-fixtures` checks
`spec/fixtures/lan-vectors.json` (written by `--write`): frames built from live values, sources, rounding, the
history rule, the UDP size rule, first-line classification, HTTP request heads and their responses, access key
parsing and comparison, the lockout, WebSocket accept values and frame encoding, local addresses, target address
parsing and signatures. Each app checks the same vectors in its unit tests.

The same tool is a client for checking a running app from another machine:

```sh
python3 spec/tools/lan_reference.py live   --host 192.168.1.20 --key K7Q4-M2X9-PT3D   # GET /v1/live
python3 spec/tools/lan_reference.py stream --host 192.168.1.20 --key K7Q4-M2X9-PT3D   # WebSocket
python3 spec/tools/lan_reference.py lines  --host 192.168.1.20 --key K7Q4-M2X9-PT3D   # line protocol
python3 spec/tools/lan_reference.py udp    --port 7247 [--key s3cret-phrase]           # receive a UDP target
python3 spec/tools/lan_reference.py http   --port 8080 [--key s3cret-phrase]           # receive an HTTP target
```

The receiving commands check every frame against section 3 and the signature, and print one line per frame.
