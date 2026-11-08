# Characters

The `character` orb style (DESIGN 2a.6) shows an animated anime character, one per model vendor, instead of
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
| `vendors` | model vendors this character stands for: `providers` keys of `prices.json` (`anthropic`, `openai`, ...) or the vendors of the model families of section 7 (`bytedance`, `meta`, ...); empty for a character no model stands for |
| `accent` | `#RRGGBB`, the character's color (vendor dot, selection tint) |
| `artist` | optional `{name, url}`, shown in Settings as the credit |
| `license` | optional text shown with the credit |
| `minApp` | optional lowest app version that can play the pack |
| `motion` | optional motion profile (section 5, Motion profile); without it the character moves like Claude |
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

### Appendages

`characters/rig/appendages.py` rigs tails, wings and animal ears from the See-Through layers. A character's spec
(`characters/specs/<id>.json`) may carry build hints in `rig`, which stay out of the manifest: `recover`, `tail`,
`wings`, `ears` (animal ears; horns and fins beside the head stay still without it) and `staticObjects` (a held object that does not
move with the hand, such as a desk).

- **Recovery** (`recover`): See-Through sometimes leaves a piece of the art out of every layer. The figure is
  everything not white, with areas enclosed by line art filled, less the bare paper; what no layer covers (2 px away
  from covered pixels, opened by 2 px, blobs of at least 0.05 % of the canvas) becomes a layer of its own, opaque
  inside and as opaque as the art is dark on its rim (darkness / 0.3). The largest blob below the chin is the tail
  with the `tail` hint and no tail layer; with `wings`, the largest blob below the eyes and away from the head is the
  wings; either only when it covers at least 0.4 of the face's bounding box. Blobs by the head are `headwear` above
  the face and `ears` beside it; any other blob is `extra`, drawn with the tail. Packs without the hint are built
  exactly as before.
- **Bare paper** (build option `--matte`, an anime figure matte such as skytnt/anime-seg `isnetis.onnx`): generated
  art sits on white, which also shows through enclosed gaps (an axe blade, hair beside the body). Bare paper is every
  pixel whose darkest channel is above 0.96 and that the matte scores below 0.5, plus every enclosed (not touching the
  border) area of such near-white pixels whose mean score is below 0.8. Every layer is transparent there, so white
  clothing (high scores) stays and the gaps show the desktop.
- **Tail**: the root is the tail pixel touching another layer (back hair excepted) nearest the waist; the midline is
  the geodesic path inside the tail to its farthest pixel. Bones `tail1`–`tail3` (parent the lower body, then each
  other) pivot at 0, 1/3 and 2/3 of its length. A vertex at geodesic fraction `u` weighs `tail3 =
  smoothstep(0.58, 0.75, u)`, `tail2 = smoothstep(0.25, 0.42, u) · (1 − tail3)`, the rest `tail1`, which hands over
  to the lower body below `u = 0.1`. `TailSwish` turns `tail1` ±16° and `tail2` ±10°; the physics chain `tail`
  (stiffness 28, damping 5, wind 0.7, anchored to the hip and pushed along x by `TailSwish`) drives `TailBend1`–`3`
  (−8°, −12°, −16° per unit).
- **Wings**: per image side, the bone `wing.<side>` (parent the chest) pivots at the wing pixel nearest the spine
  point (neck x, chin + 0.45 × chin-to-waist); a vertex weighs `smoothstep(0.03, 0.15, distance / reach)` on the
  wing of its side, the rest the chest. `WingFlap` turns them ±14° (both rise for +1) and widens them by 8 %.
- **Animal ears** (with the `ears` hint): an `ears` layer whose mass sits above the face top + 0.25 face height is an animal ear; its bone
  `ear.<side>` (parent the head) pivots at its base (the lowest 15 % of the ear). `EarTwitch.<side>` turns it ±12°
  outward for +1 and squashes it by 8 % (stretches it by 4 % for −1).

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
the same parameters (fixture `director.json`; `director-profiles.json` with motion profiles).

### Motion profile

Each character moves in her own way through the manifest's `motion` block (`motions/profile.py`). Every key is
optional and its default is Claude's behavior, so a pack without the block moves exactly as before.

| Key | Effect |
| --- | --- |
| `tempo` | the beat clock's BPM × tempo (at least 0.25) |
| `energy`, `sway`, `bounce` | groove values × the factor: `energy` for `Arm.`, `Forearm.`, `Shoulder.`; `sway` for `Hip`, `BodyAngleZ`, `Lean`, `AngleZ`; `bounce` for `PosY`, `Squash`, `FootPoint`, `FootLift`, `SkirtFlare`; also the tail's idle swish × `sway` |
| `gap` | the pause between actions × gap (at least 0.25) |
| `idle` | the idle groove: `read` (looking down at the book, default) or `rest` (looking ahead, hands at rest, a slow weight shift) |
| `seated` | sitting: no action that needs the legs or the whole body (hop, jump, hops, spin, step, run, jumpjoy, tiptoe, foottap, curtsy, pounce, floatbob); `PosX`, `PosY`, `Squash`, `Lean`, `Hip`, `FootLift`, `FootPoint`, `BodySway`, `SkirtFlare` stay at their defaults; pose swaps crossfade over 0.3 s instead of turning |
| `talk` | `false`: no hum or giggle and no lip flap |
| `signature` | her actions: each joins the pool of every band it fits (below) with a weight of three in all (two more copies when the pool has it already); a face gesture among them (yawn, hum, giggle) plays even when the pose has no face variant for it |
| `exclude` | actions she never plays |
| `hands` | the fidgets of her free hand, in place of tap, regrip, lift, flex, fan, count, drum |
| `poseHands` | `{band: set}`: while the pose of that band shows, its hands play the set back to back |
| `particles` | the particle kind of section 8 (default `petals`) |

Numbers are clamped to 0…4; unknown actions and hand sets are ignored. The bands an action fits: yawn, stretch,
ponder idle–cruise; bow, curtsy, nod, tilt, lookaround, shake, foottap, floatbob idle–boost; hum, sway, tiptoe, peek
cruise–boost; giggle, doubletake, swagger cruise–overdrive; step idle–overdrive; hop cruise–redline; hops, spin,
pounce boost–redline; run, jump, jumpjoy overdrive–redline; tailswish, wingflap, eartwitch every band. Every action,
celebrations included, is skipped when the profile does not allow it.

Actions of the profiles (`motions/moves.py`), each played only when the rig has the parameter it needs:
**tailswish** (1.6 s, `TailSwish`): the tail swings in three beats against the hips; **wingflap** (1.5 s,
`WingFlap`): two flaps with a lift and a puff of particles; **eartwitch** (1 s, either `EarTwitch`): each ear flicks
in turn with a glance; **floatbob** (3.4 s, `PosY`): drifting as if under water, arms floating; **pounce** (1.25 s,
`PosY`, `Squash`): a crouch, a wiggle and a small hop toward the orb, airborne; **swagger** (1.9 s, `Hip`): chin up,
weight on one hip, a shrug.

Hand sets of the profiles (`motions/hands.py`): **flute** (fingerings of three fingers every half beat, the wrist
rocking), **fanflutter** (the hand holding the object flutters it), **abacus** (the index flicks beads every half
beat, at least 0.24 s apart, the thumb every other one), **sleeveflop** (hands flopping in long sleeves),
**ripple** (a slow finger ripple). `poseHands` sets play on both hands (flute, sleeveflop, ripple), on the hand
holding the object (fanflutter, tap, regrip, lift) or on the free one (abacus, flex, fan, count, drum).
**flute** holds an instrument in both hands: while the pose's set is flute, every `Arm.`, `Forearm.`, `Shoulder.`,
`Lift.` and `Bend.` parameter keeps its default after the groove and the action (before the hands and the springs),
so the hands never slide along the instrument and the body sways and bobs with both of them.

Appendages move at rest too: `TailSwish += (0.18 + 0.3 s) · sway · sin(π · beat phase + 0.5)` and `WingFlap +=
0.12 · sin(2π · breath phase)`, each scaled by `1 − w` while an action of weight `w` drives the same parameter and
clamped to its range; springs give them follow-through.

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
  to, METRICS 4; a model the table does not name a vendor for, or one a reseller sells, takes the vendor of its
  family below, else the vendor of its source); a vendor's speed is the sum of its live rates (like the orb's own
  value). Show the installed
  character of the fastest vendor that has one; if none of the live vendors has one, keep the character shown last;
  at launch, the character of the vendor seen most recently in the orb's history, else the first installed pack in
  catalog order.
- A vendor change is held for 3 s before the character changes (no flicker between two close vendors), then the swap
  of section 6 runs between the two characters.
- No character installed: the style shows the empty state of DESIGN 2a.6 (a prompt to download one).

Model families, by the start of the model id's last path segment, lowercased (`meta-llama/Llama-3.3-70B` →
`llama-3.3-70b`):

| Prefix | Vendor |
| --- | --- |
| `doubao`, `seed-`, `ui-tars` | `bytedance` |
| `llama` | `meta` |
| `sonar`, `pplx`, `r1-1776` | `perplexity` |
| `ernie` | `baidu` |
| `hunyuan` | `tencent` |
| `rwkv` | `rwkv` |

## 8. Floating readouts

Around the character, on Kepler orbits seen slightly from above (`characters/reference/orbits.py`, fixture
`orbits.json`): the speed (rolling digits sampled five times a second, outlined, with lightning when they change,
the unit and the speed title), the model chip
(vendor dot and model name) and the agent line (agents and today's tokens). Bodies in front of the character draw over
her, bigger and brighter; behind her they draw under her. At her feet a ring of 96 lights is the gauge, filled in the
heat colors with a comet; agents are lights orbiting her waist; response pulses ripple out from the ring; embers rise
at redline; a new peak pops a gold pill above the speed. See `characters/reference/hud.py` and DESIGN 2a.6.

### Particles

Each character has one particle kind (`motion.particles`, `characters/reference/effects.py`), 55 % drawn in front
of her and the rest behind. `petals` (default) are the flower sprite of the pose's headwear part, carried across the
stage by the wind from boost up (a pose without the sprite gets `sparkles`). The other kinds start in the area
`feet.x ± 0.625 H`, `feet.y − 1.083 H … feet.y − 0.042 H` (`H` her height) and emit per second
`base + gain · max(0, s − floor)^power` of the intensity `s` (`base · (1 − s)²` for `zzz`, which only drifts while
she rests). Lengths are reference units (972 per height); `vx` gains `wind · (0.6 + s)`, `x` wobbles by
`amplitude · sin(age · frequency + phase)`; opacity fades in over the first 15 % of the life and out over the last
30 %; size changes by `grow` over the life.

| Kind | Look | base, gain, floor, power | Origin | vx, vy | wind | wobble | size | grow | life s | spin °/s |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `bubbles` | ring with a highlight | 3, 16, 0, 1 | body | ±15, −90…−40 | 20 | 14, 4 | 6–14 | 0.4 | 1.6–2.8 | 0 |
| `sparkles` | four-point star, twinkling | 4, 26, 0, 1.5 | body | ±12, ±12 | 0 | 0 | 8–16 | 0 | 0.8–1.6 | ±40 |
| `stars` | four-point star, twinkling | 3, 18, 0, 1.4 | body | ±10, −30…−10 | 10 | 6, 2 | 10–18 | 0 | 1.4–2.4 | ±90 |
| `notes` | ♪ ♫ | 3, 14, 0, 1 | body | ±10, −60…−30 | 30 | 20, 3 | 18–28 | 0 | 1.8–2.8 | 0 |
| `embers` | glowing dot | 0, 30, 0.2, 1.3 | feet | ±20, −160…−80 | 25 | 18, 6 | 2–4 | −0.5 | 0.7–1.5 | 0 |
| `foxfire` | tall soft wisp with a core | 2, 10, 0, 1 | body | ±8, −45…−20 | 8 | 16, 2.5 | 7–12 | −0.3 | 1.4–2.4 | 0 |
| `confetti` | tumbling chip | 0, 40, 0.4, 1.5 | top | ±40, 60…140 | 60 | 25, 5 | 5–9 | 0 | 1.6–2.6 | ±400 |
| `zzz` | Z z | 1.2 (inverse) | head | 10…22, −30…−18 | 0 | 6, 1.5 | 14–18 | 0.7 | 2.2–3 | ±10 |
| `pages` | tumbling chip | 0, 20, 0.2, 1.4 | body | 120…220, −40…30 | 0 | 18, 5 | 10–16 | 0 | 1.3–2.4 | ±260 |
| `blossoms` | tumbling chip | 2, 24, 0, 1.5 | body | 100…200, −30…30 | 0 | 18, 5 | 8–13 | 0 | 1.4–2.6 | ±200 |
| `feathers` | long soft oval | 3, 18, 0, 1.4 | top | ±20, 25…50 | 40 | 30, 1.8 | 14–24 | 0 | 2–3.2 | ±60 |
| `snow` | soft dot | 6, 20, 0, 1 | top | ±10, 30…60 | 30 | 12, 2 | 3–6 | 0 | 2.5–4 | 0 |

Colors (`a` the accent, `l = 0.45 a + 0.55`): bubbles `#BFE6FF`; sparkles `l`, `#FFF7D9`; stars `l`, `#FFE58C`;
notes and zzz `l`; embers `#FF8C33`, `#FFCC59`; foxfire `#73B8FF`; confetti `a`, `#FF7AB8`, `#FFD459`, `#7AE0C2`;
pages `#FAF7ED`; blossoms `#FFDBEB`, `#DBE6FF`, white; feathers `a`, `l`; snow white. Stars, wisps and embers add
up on dark glass. Origins: body `x0 + 0.1…0.9 w`, `y0 + 0.1…0.8 h`; top `x0…x1`, `y0 − 0.05 h + 0…0.25 h`; head
`x0 + 0.35…0.65 w`, `y0…y0 + 0.2 h`; feet `x0 + 0.25…0.75 w`, `y1 − 0…0.1 h`.

A puff event of an action (take-offs, a wing flap, a pounce) and a pose swap throw out 14 from her middle: speed
0.3–0.8 × 260 in a random direction (vertical × 0.6, then −40), at 0.7 × the life (petals: 0.4–1 × 260, then −60).
The pool holds 160. Bursts and wind-blown petals ask for 60 fps; the slow drift of the other kinds rides the band's
frame rate (DESIGN 2a.6, Frame policy).

## 9. Conformance

Every player loads each fixture in `spec/fixtures/characters/` and matches it within 1e-6 (1e-4 for `director.json`
and `director-profiles.json` values): `rng`, `curves`, `springs`, `orbits` (positions and Kepler solutions), `rig`
(world transforms, vertices, opacities of a small rig), `physics`, `odometer` (text and per-digit roll state),
`switch`, `director`, `director-profiles` (three profiles with tail, wing and ear parameters: a flutist with pose
hand sets, a seated silent character, a cat, each across pose changes).
`python3 -m characters.tests.make_fixtures --check` keeps them in step with the reference.
