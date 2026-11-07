# Characters

The `character` orb style (DESIGN 2a, Character) shows an animated anime character, one per model vendor, instead of
a round instrument. This document is the contract between the offline tools in `characters/` and the native players
on macOS, Windows and Linux: the downloadable pack, the catalog, how a rig is evaluated, how the character moves, the
floating readouts, which character an orb shows, and the conformance fixtures.

The Python reference player in `characters/reference/` is normative for everything that moves: when this text and the
reference disagree, the reference wins and this text is fixed. The fixtures in `spec/fixtures/characters/` are its
outputs; a native player passes them before it ships.

## 1. Character packs

A pack is a zip named `<id>-<packVersion>.zip`:

```
manifest.json
preview.png              512 x 640, the idle pose at rest on transparency, feet on the bottom edge
poses/<band>/rig.json    one rig per speed band (section 3); idle is required
poses/<band>/atlas.png
```

`manifest.json`:

| Key | Content |
| --- | --- |
| `format`, `version` | `"agent-speed-character-pack"`, `1` |
| `id` | lowercase letters, digits and dashes; stable forever (`claude`, `gpt`, ...) |
| `packVersion` | integer, raised whenever any file of the pack changes |
| `name` | `{zh-Hant, zh-Hans, en, ja}` display names |
| `vendors` | model vendors this character stands for, as `providers` keys of `prices.json` (`anthropic`, `openai`, ...) |
| `accent` | `#RRGGBB`, the character's color (vendor dot, selection tint) |
| `artist` | optional `{name, url}`, shown in Settings as the credit |
| `license` | optional text shown with the credit |
| `minApp` | optional lowest app version that can play the pack |
| `poses` | `{band: "poses/<band>"}` for the bands present |
| `preview` | `"preview.png"` |

Bands are `idle`, `cruise`, `boost`, `overdrive`, `redline` (the effect tiers of METRICS 6). A band without a pose
uses the nearest lower band that has one.

Players refuse a pack whose format or version they do not know, whose `minApp` is newer than the app, or that
references a path outside the pack (`..`, absolute paths, symbolic links).

## 2. Catalog and download

The catalog is `catalog.json`, published with the packs as assets of the GitHub release tagged `characters` of
`zoefix/agent-speed`:

```
https://github.com/zoefix/agent-speed/releases/download/characters/catalog.json
```

```json
{"format": "agent-speed-character-catalog", "version": 1,
 "characters": [{"id": "claude", "packVersion": 1, "name": {...}, "vendors": ["anthropic"], "accent": "#D97757",
                 "artist": {...}, "size": 9551622, "sha256": "...", "url": ".../claude-1.zip",
                 "preview": ".../claude-preview.png"}]}
```

- The app never downloads a pack on its own: the user presses Download (Settings, or the orb's context menu). The
  catalog itself is fetched when the character list is shown and at most once a day otherwise.
- Only `https` URLs on `github.com` and `objects.githubusercontent.com` (where release downloads redirect) are
  followed. The download must match `size` and `sha256` before it is unpacked; otherwise it is deleted and the error
  shown. Packs are unpacked into a temporary folder, validated (section 1), then moved into place atomically.
- Installed packs live in the app's data folder: macOS `~/Library/Application Support/AgentSpeed/Characters/<id>/`,
  Windows `%LOCALAPPDATA%\AgentSpeed\Characters\<id>\`, Linux `$XDG_DATA_HOME/agent-speed/characters/<id>/`.
  A newer `packVersion` in the catalog shows an Update button; updating replaces the folder atomically.
- Removing a character deletes its folder; orbs locked to it fall back to Auto (section 7).
- Developer override: the environment variable `AGENT_SPEED_CHARACTERS` names a folder of unpacked packs that are
  listed as installed without a catalog.

`python3 -m characters.rig.pack` builds a pack and updates a catalog (characters/README.md).

## 3. Rig

`rig.json` is the package of characters/README.md (format `agent-speed-character`, version 1). Evaluation per frame:

1. Clamp each parameter to its range; missing parameters take their default.
2. For each bone in order (parents first): sum each channel over its drivers (piecewise linear in the parameter,
   flat outside the keys). Local transform = T(x, y) · T(pivot) · R(rotate) · S(1 + scaleX, 1 + scaleY) · T(-pivot);
   rotation in degrees, clockwise on screen (y points down). World = parent world · local.
3. Each vertex is the weighted sum of its bones' world transforms applied to its rest position (weights sum to 1).
4. A part's opacity is the product of its part drivers (1 without any); parts below 0.002 are skipped.
5. Parts draw in `order` with premultiplied alpha over each other; the atlas is straight alpha, sampled with
   mipmaps and linear filtering.

The rest pose (all defaults) reproduces the illustration exactly. Skinning may run on the CPU or the GPU.

## 4. Physics

Hair and skirt are pendulum chains (`physics` of the rig): Verlet integration in fixed 1/240 s substeps, gravity
1400 px/s², a spring toward the hanging rest shape, damping, two passes of length constraints, wind along the image
x axis with gusts, and the anchor moved by its input parameters. Each output parameter is
tanh(angle of its segment relative to the previous one / degrees). See `characters/reference/physics.py`.

## 5. Motion

`characters/reference/motions/` drives every parameter from the orb's speed level (0–13) and band:

- A groove per band on a beat clock (56 → 140 BPM): posture, weight shift, bounce, arm swing.
- One action at a time from the band's pool: head gestures, whole-body moves (hop, jump, hops, spin, side step,
  curtsy, tiptoe, foot tap, sway, run, stretch, bow, peek), with a gap per band between actions. Arm and face
  gestures that need pose variants are skipped when a pose has none.
- Hand acting per hand (finger ripple, taps, regrip, lift, flex, fan, count, drum, wrist follow-through).
- Breathing, glances, blinks, wind from the speed.
- Second-order springs on most parameters (`springs.py`), so motion overshoots and settles.
- Band change: the pose swap of section 6.

Random numbers come from the generator below; with the same seed and the same level history every player produces
the same parameters (fixture `director.json`).

### Random numbers

SplitMix64 over 64-bit unsigned integers: `state += 0x9E3779B97F4A7C15`; `z = state`;
`z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9`; `z = (z ^ (z >> 27)) * 0x94D049BB133111EB`; output `z ^ (z >> 31)`.
`random()` = (output >> 11) / 2^53. `uniform(a, b) = a + (b - a) * random()`. `index(n) = min(floor(random() * n),
n - 1)`. `choice` picks `index(len)`. `shuffle` is Fisher-Yates from the last item down, swapping item i with
`index(i + 1)`. `sample(seq, k)` shuffles a copy and takes the first k. Fixture `rng.json`.

## 6. Pose swap

When the band changes and the new band maps to a different pose, the character turns edge-on and back: `Spin` runs
0 → 0.25 over 0.2 s (ease-in, `k²`), the pose is swapped on the frame that crosses 0.2 s, which shows `Spin = 0.75`
exactly (also edge-on), then `Spin` runs 0.75 → 1 over 0.32 s (ease-out cubic) with a small squash, lift and skirt
flare, and returns to 0. A request while a swap runs is ignored; the next band change after it completes swaps
again. Each pose keeps its own physics, stepped every frame. The director's free and object arms follow the new
pose's `objectSide`. Fixture `switch.json`.

Poses are aligned on screen by the feet and scaled so that the distance from the face center to the feet matches the
idle pose.

## 7. Which character an orb shows

Per orb, the setting `character` is `auto` (default) or a pack id.

- Locked to an id: that character while it is installed; if it is removed, Auto.
- Auto: group the orb's live responses by model vendor (the `providers` key of `prices.json` that the model resolves
  to, METRICS 4); a vendor's speed is the sum of its live rates (like the orb's own value). Show the installed
  character of the fastest vendor that has one; if none of the live vendors has one, keep the character shown last;
  at launch, the character of the vendor seen most recently in the orb's history, else the first installed pack in
  catalog order.
- A vendor change is held for 3 s before the character changes (no flicker between two close vendors), then the swap
  of section 6 runs between the two characters.
- No character installed: the style shows the empty state of DESIGN 2a, Character (a prompt to download one).

## 8. Floating readouts

Around the character, on Kepler orbits seen slightly from above (`characters/reference/orbits.py`, fixture
`orbits.json`): the speed (rolling digits sampled five times a second, the unit and the speed title), the model chip
(vendor dot and model name) and the agent line (agents and today's tokens). Bodies in front of the character draw over
her, bigger and brighter; behind her they draw under her. At her feet a ring of 96 lights is the gauge, filled in the
heat colors with a comet; agents are lights orbiting her waist; response pulses ripple out from the ring; embers rise
at redline; a new peak pops a gold pill above the speed. See `characters/reference/hud.py` and DESIGN 2a, Character.

## 9. Conformance

Every player loads each fixture in `spec/fixtures/characters/` and matches it within 1e-6 (1e-4 for `director.json`
values): `rng`, `curves`, `springs`, `orbits` (positions and Kepler solutions), `rig` (world transforms, vertices,
opacities of a small rig), `physics`, `odometer` (text and per-digit roll state), `switch`, `director`.
`python3 -m characters.tests.make_fixtures --check` keeps them in step with the reference.
