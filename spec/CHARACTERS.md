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
poses/<state>/rig.json   optional mood poses of a living pack (section 5, Behavior), each a rig like a band pose
poses/<state>/atlas.png
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
| `states` | optional `{state: "poses/<state>"}`: full-body poses of the moods of section 5, *Behavior*, for the states `think`, `wait`, `cheer`, `sleepy`, `focus`, `greet` (others are ignored); a missing one shows the band pose instead. Players that do not know the key ignore it: it never joins `poses`, whose keys stay bands |
| `preview` | `"preview.png"` |

Bands are `idle`, `cruise`, `boost`, `overdrive`, `redline` (the effect tiers of METRICS 6). A band without a pose
uses the nearest lower band that has one.

Players refuse a pack whose format or version they do not know, whose `minApp` is newer than the app, or that
references a path outside the pack (`..`, absolute paths, symbolic links), in `states` too. A pack with `states` or
with face patches is a *living* pack (section 5, *Behavior*); it stays playable by players without that section.

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

- **Automatic download.** When an orb switches to the `character` style, and at launch while any orb uses it, the app
  downloads every pack of the catalog that is neither installed nor set aside, one at a time: first the packs of the
  vendors that orb shows live now (fastest first), then the rest in catalog order. A pack the user removes, or whose
  download the user cancels, is **set aside** (stored per user, `charactersSetAside`) and never downloaded
  automatically again; pressing its Download clears that. Stop (Settings) sets aside every pack still queued. A
  network failure ends the run (the next launch or style switch starts it again); a pack that fails its checks is
  skipped until the next run. Nothing downloads while network features are off. Settings shows the run as
  `characters.auto.status` (done / total) with `characters.auto.stop`. The catalog itself is fetched when the
  character list is shown or a run needs it, and at most once a day otherwise.
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

The build (`characters/rig`) bends the body at the waist: the top and the skirt layer weigh `chest` above it and
`lower` below it, blending from 0.3 face heights above to 0.1 below, so a cape the skirt layer carries from the
shoulders moves with the chest. The waist is halfway between the top's lower edge and the skirt's upper edge, unless
the pose estimator finds both shoulders and both hips and that point lies outside 0.5–0.9 of the way from the
shoulders to the hips (a cape in the skirt layer, a coat to the knees): then it is at 0.65 of the way.

### Variants

A pose's `variants` (characters/README.md) are parts that swap in over its drawn parts through the parameter
`Var.<id>` (0 to 1, default 0, so a player that never moves it shows the pose as drawn):

- `arm`: an arm, and the held object for the object arm, from a See-Through split of an edit of the pose image. Arm
  gestures of section 5 play the variant whose id is theirs. An edit named `gesture-<name>` builds the arm variant
  `<name>` (`wave`, `peace`, `thumbsup`, `chin`, `idea`, `fist`, `hairtuck`, `hip`, `shh`, `cheek`, `present`,
  `salute`), except `gesture-stretch`, whose id is `reach` (the whole-body `stretch` keeps its name). The free arm
  (the side that does not hold the object) carries them.
- `face`: eyes, brows and mouth of a split face edit, with `Mouth.<id>` for its mouth (section 5).
- `patch`: one part cut from an edit that changes only the face, with `region` `face` (the whole face: an
  expression), `mouth` or `eyes`. Its part `face@<id>` is fully in by `Var` 0.5 while the parts it replaces fade out
  to 1. Under the layers kept above it (the bangs) a patch shows the base face, by their opacity softened by a 2.5 px
  blur raised 1.5 times (from 0.15 to 0.6), so the bangs' stair-stepped edges never show as a hard switch from the
  edit; that base face holds the parts the patch replaces as drawn, except where the edit changed the art (its change
  blurred by 1.5 px, from 0.15 to 0.5), where it is the bare face layer (a closed eye leaves no lash tip by the bangs). Patches lie right above the face and below the bangs, by region: `face` lowest, then `mouth`, then `eyes`,
  so a blink or a gaze shows over an expression and a talking mouth over it too. Ids name what they show:
  `expr-<name>` (region `face`: `smile`, `laugh`, `wink`, `surprised`, `pout`, `shy`, `thinking`, `sleepy`, `yawn`,
  `focused`, `smug`, `worried`, `excited`), `mouth-<a|i|u|o|closed>` (region `mouth`) and
  `gaze-<left|right|up|down|closed>` (region `eyes`); sides are image sides. The build leaves out an edit that barely
  changes its region (a `mouth-closed` on a face whose mouth is closed already), and an arm edit whose split draws
  both arms as one layer (a hand across the face center).

### Appendages

`characters/rig/appendages.py` rigs tails, wings and animal ears from the See-Through layers. A character's spec
(`characters/specs/<id>.json`) may carry build hints in `rig`, which stay out of the manifest: `tail`, `tailSwing`
(a number, 1 by default, that scales the tail's turns, *Tail* below: a big tail over clothes that hide much of it
swings less far, so less of what the art never drew shows),
`wings`, `ears` (animal ears; horns and fins beside the head stay still without it), `staticObjects` (a held object
that does not move with the hand, such as a desk; a gesture on its side swaps the arm alone and leaves the object the
base's), `swing` (parameter id prefixes to factors on their drivers, such as `{"Arm.right": 0.35}`: a part whose
hidden art shows when it swings far moves less; `Lift`, `Bend` and `Var` stay unless a key names them, such as
`Lift.right`, for an old arm that carries a toy), `poseSwing` (the same per pose,
over `swing`), `toy` (a held toy with a face of its own: its eyes and mouth go to the toy even where it is held up to
her face, all of a stacked eye at once, instead of to her face's layers, which follow the head and fade under face
patches) and `legsInSkirt` (the split draws the legs into the skirt layer too, through sheer cloth or a slit: legs
and shoes take the skirt's weights, `lower` to `skirt` to `skirtHem` down to the hem, and there
is no `foot` bone, so no `FootLift` or `FootPoint`; otherwise a lifted foot or a swinging hem shows each leg twice;
the hint also suits long-skirted art whose boots peek out under a hem that barely covers their tops, which would
otherwise part from the hem in a hop or a flare. With it, each blob of drawn art no layer covers (blobs as in
Recovery) that has at least 10 % of a 4 px ring on the skirt and 10 % on the shoes, and at least half of whose pixels
are darker than paper, joins the shoe layer holding most of that ring: a shin See-Through left out between the hem
and the boots, which would show the desktop. The piece is the patch of uncovered art the blob lies in, within 12 px
of it, opaque, as opaque as the art is dark (darkness / 0.3) on its 1 px rim against the paper, and it runs on 2 px
under the soft edges of the layers around it (covered, under 0.99, away from the paper), so the hem's edge never
shows a dark line across the shin), and `props` (per band or mood pose, points in canvas fractions on a held object the split
spreads over other layers, such as a doll it takes for a second head: its face in `face`, its eyes in `irides`, its
hair in `back hair`; build option `--band` names the band). Each point asks SAM on its own; the smallest mask it scores at least 0.8 joins the object,
closed by 3 px, with enclosed gaps under 5 % of it filled (eyes, a mouth) and larger ones left out (a hand across it).
The mask, grown by 2 px, becomes one `objects` layer named `prop` in the objects slot of the draw order, opaque
inside SAM's mask and as opaque as the layers were on the grown rim. Every layer drawn above that slot gives up its
pixels there, and so does the pixel's visible owner and every layer above it; what lies hidden below the owner stays
(the body behind a hugged doll), unless it belongs to a piece of its layer (opacity above 0.12) that touches the
mask and lies at least 60 % within 15 px of it: the split paints hair behind the doll's face as if it were a head.
Such a piece leaves its layer and joins the prop. Topwear above the upper 30 % of the face (at least 1000 px over
opacity 0.5: a white beret the split takes for the white blouse, a crown) becomes a `headwear` layer named `hat` that
follows the head, kept in the top's place in the draw order; when the art shows it tossed above the head (the median
gap, column by column, between its lower edge and the head's top is over a tenth of its height) it is set down so its
lower 35 % covers the head's top, and drawn over the hair. A halo (its pixels over opacity 0.5 fill under a quarter
of its box: a ring, a crown of thorns) floats above the head in every pose and stays where the art draws it. The
objects layer the split made is cut into its pieces (gaps up
to 8 px closed; pieces of at least 5 % of the layer, smaller bits joining the nearest piece), so a fan in one hand and
a purse in the other each follow their own holder: a piece follows the hand (wrist to fingertip segment, or the hand's
keypoints) that comes within 0.6 face widths of it, else the body (`lower` below the waist, `chest` above), and
`objectSide` is the hand holding the largest held piece. The prop follows the hand nearest it, unless the
band's entry is `{"points": [...], "follow": "chest" | "lower" | "head"}`: a book held up before the chest, which the
split also paints into the top, moves with the chest so that copy never shows beside it. A seated character (motion
`seated`), or one with the hint `rigidLower`, has no `foot` bone: legs and shoes follow `lower`, and the skirt blends
from `chest` to `lower` at the waist like the top, so knees and ankles stay whole and the skirt physics has nothing
to swing (a harpy whose feather skirt the split scatters over her wing and arm layers). With the hint `bodyInArms`
the arm layers give the body what the split drew of it into them: a vertex's body share is the larger of
`smoothstep(waist + 0.2 face, waist + 1.2 face)` (a skirt) and, inside the torso (between the shoulders, softened by
0.3 face widths, and below the chin), `smoothstep(0.45, 0.9)` of its distance to the arm's bones in face widths;
that share follows the body like the top (`chest` to `lower` at the waist), so the front of a feather dress stays
put and only stretches where it meets a moving arm. Before that, the body the split drew into each arm layer is cut
out along the art's ink lines: a watershed over the art's gradient grows the arm from its bones (within 4 px of
shoulder, elbow and wrist) and the body from the torso (between the shoulders, 0.15 face widths in from each, below
the upper half of the face, over 0.35 face widths from the arm's bones); what the body reaches becomes a `topwear`
layer named `<arm> body`, drawn right after the arm, so a robe's front edge beside a wide sleeve stays with the robe. A pose whose shoes are separate feet with soles more than 15 %
of the leg (waist to feet) apart, one kicked up behind, has no `foot` bone either: the one bone for both feet would
pivot between them and tear the raised leg from the skirt, so legs and shoes follow `lower`.

- **Recovery** (every character, no hint): See-Through sometimes leaves a piece of the art out of every layer, and
  in one pose of a series where the others have it (the boots of Claude's `think`). The figure is
  everything not white, with areas enclosed by line art filled, less the bare paper; what no layer covers (2 px away
  from covered pixels, opened by 2 px, blobs of at least 0.05 % of the canvas) becomes a layer of its own, opaque
  inside and as opaque as the art is dark on its rim (darkness / 0.3), except within 3 px of a layer (away from the
  paper), where the art runs on under that layer: opaque there, and also 2 px under that layer's soft edge (coverage
  under 0.99), else the two soft edges left a dotted seam of the stage (ear tops the split left out, on the scalp).
  The largest blob below the chin is the tail
  with the `tail` hint and no tail layer; with `wings`, the largest blob below the eyes and away from the head is the
  wings; either only when it covers at least 0.4 of the face's bounding box. Blobs by the head are `headwear` above
  the face and `ears` beside it; when the split made no footwear, a blob reaching the bottom of the figure (within
  2 % of the canvas) that touches the legwear, or whose center lies below the skirt's hem (the lowest row the
  `bottomwear` is opaque on: boots under a long skirt, with no legwear layer), is a shoe, `footwear`, checked first so
  a shoe is never taken for the tail, and a second such blob joins the first; a blob no larger than the footwear
  with at least 15 % of a 4 px ring on it (a calf above a kicked-up slipper) is `legwear`; any other blob is
  `extra`, drawn with the tail. With `--matte`, a grey blob (mean
  chroma, brightest minus darkest channel, below 0.15) the matte scores below 0.5 on average is the shadow drawn on
  the floor and stays out; a coloured one the matte also misses (a hologram) stays in. A smaller blob (the same
  grey rule applies) that sticks out of the layers, its 3 px ring under 90 % covered, goes to the layer holding most
  of that ring when that is at least 12 px of it (a long feather meets the layer only at its cut root): See-Through
  cuts parts at the edge of the box it splits the head in, so the top of a crown belongs to no layer. The piece is
  the whole patch of uncovered art the blob lies in, within 12 px of it, so thin tips come along; it is opaque, and
  as opaque as the art is dark (darkness / 0.3) on its 1 px rim against the paper. Then every remaining patch of
  the figure no layer covers (opacity above 0.5), up to 0.2 % of the canvas, whose 3 px surrounding ring is at least
  90 % covered goes to the lowest layer holding at least 15 % of that ring, opaque and painted from the art: a gap
  between belt straps shows the robe behind them instead of a white speck. Recovery runs before the shins under
  the hem are given to the shoes (`legsInSkirt`), so recovered boots take them too.
- **Checks**: the build refuses a pose (exit status 1, and no package left at its output: one built before would be
  packed in its place) when its rig at rest does not match the art, or when the character's idle pose (build option
  `--idle`, its built package) draws feet and this pose draws none: the pose would stand on her hem, as the feet are
  its lowest drawn row (section 6). A pose draws feet when it has a `footwear` part, or `legwear` whose lowest row is
  at least 2 % of the canvas height below the hem of the `bottomwear` (of the `topwear` without one): tall boots or
  talons the split drew with the legs. The rest check is the layers composited at rest on white against the
  art, leaving out where a hat the build set down on the head was drawn and now is, and the shadow drawn on the
  floor that Recovery leaves out (grown by 4 px over what no layer covers; with `--matte` only): the mean absolute
  difference over the canvas (0 to 255) must be at most 1.0 and the share of pixels off by more
  than 32 at most 0.55 %. Every sound pose built so far stays at or under 0.82 and 0.40 %; Claude's `think` without
  its boots read 1.16 and 0.70 %. `--check` stops after the layers and prints both checks as JSON, without a
  package. The pack refuses a pose or mood pose that draws no feet when the idle pose does (the lowest row of a
  part being its lowest vertex there).
- **Bare paper** (build option `--matte`, an anime figure matte such as skytnt/anime-seg `isnetis.onnx`): generated
  art sits on white, which also shows through enclosed gaps (an axe blade, hair beside the body). Bare paper is every
  pixel whose darkest channel is above 0.96 and that the matte scores below 0.5, plus every enclosed (not touching the
  border) area of such near-white pixels whose mean score is below 0.8, or below 0.99 when most of the area is outside
  every layer of the split (opacity above 0.5): a gap framed by raised arms can score 0.93, but white clothing is in a
  layer. Every layer is transparent there, so white clothing (high scores) stays and the gaps show the desktop.
- **One tail**: the split also paints the tail into the layers around it, and may take a hat's white plume for tail:
  a piece of the tail layer (pieces 3 px apart are one) wholly above the chin that touches the headwear (within
  3 px) joins the headwear, and a tail left with nothing goes. What it drew of the tail into the front
  hair (a strand hanging over the tail takes the tail with it) goes back to the tail: a watershed over the art's
  gradient grows the hair from its pixels above the chin and the tail from the hair within 3 px of the tail layer;
  what the tail reaches, from where it narrows below half its widest row down (a strand is narrow, a tail wide),
  joins the tail when it is at least 2000 px and 80 % below the chin. With a SAM checkpoint, the whole tail in the
  art is found too, prompted by five points at the tail layer's thickest (the largest mask scored at least 0.8 that
  holds 90 % of the tail layer and is at most 2.5 times its size); the back hair's pixels inside it, in pieces of at
  least 500 px that touch the tail layer, join the tail (the top of a tail behind long hair), and the back hair is
  repainted there. Notches in the stitched tail's outline (closed by a disk of radius 15 px) are filled with the art
  where no layer drawn over it hides them, so the rest pose is unchanged; bare paper between the tufts of a furry
  outline (the matte's, *Recovery*) is left out, so it never stays on the tail as white specks.
  The tail is then made opaque inside its
  outline (eroded by 2 px) where no layer drawn over it hides it; what those layers hide of it keeps its opacity but
  takes a blur of the tail that shows (3 px inside its outline), so See-Through's dark guess there never shows as a
  band once the tail swings out. It is also opaque wherever a layer behind it
  was cleared, so its stitched pieces never show the backdrop through a hairline crack. A recovered `extra` blob no larger than the
  tail with at least 15 % of a 4 px ring on it (the tail's base, cut short) joins the tail layer. A `legwear`,
  `bottomwear` or `topwear` layer drawn over the tail is in front of it only if the art shows the clothes there: when
  on at least half of their overlap (2000 px or more) the tail layer's own paint matches the art (mean channel
  difference, 5 px box, under 0.06), the tail is what shows, so it moves to just above those layers. What the art
  shows of such a layer over the tail all the same becomes a layer of its own, `<name> front`, just above the tail
  (and keeps its paint in the layer under the tail too), so the tail never carries it when it swings: a coat's strap
  and tassel over the tail's root. Its seeds lie at least 4 px inside the tail's outline, where the layer's paint
  matches the art, the tail's does not, and the layer's stands apart from the tail's colors the art shows around
  there (a blur of them as for tail copies; mean channel difference over 0.15, 5 px box: a white skirt by a white
  tail tip is no seed), in pieces of 40 px or more; each runs on up to 12 px over the rest of what the layer's paint
  matches there, then 2 px over the layer. The tail under it takes that blur of the tail around it. A layer drawn
  under the tail whose paint matches the tail's on at least half of their overlap holds a copy too (the back hair
  under a tail curling up). Each such copy is repainted (for a layer over the tail the copy grown by 2 px, for one
  under it everything the tail covers at over 0.05 opacity, its ink outline and soft edge too): inside the layer's
  own outline (its pixels without the copy, closed by 15 px, holes filled) it takes a blur of the layer around it,
  from fine to coarse (Gaussian, sigma 2 to 64, normalized by the blurred coverage); outside it, it turns
  transparent, for a layer over the tail only where the tail is opaque (its soft rim keeps the clothes behind it).
- **Behind the tail's root**: once the tail is rigged, its footprint within 0.35 of its length from the root that the
  layers under it leave bare (coverage under 0.98) is filled by those layers (not wings, which swing too), each running
  on up to 25 px past its own pixels, the nearest first, in a blur of its colors 3 px inside its outline (as for tail
  copies; its nearest pixel would streak its ink lines): a tail swinging away from the body uncovers the coat or the
  hair it hung over there, not the backdrop.
- **Hidden legs**: every `legwear` pixel the layers above hide (opacity over 0.5) takes the color of the nearest leg
  pixel that shows (over 10 px below the layers above, 2 px inside the outline) and is opaque where See-Through's
  guess is over 0.3 (ramping to it below), within a 45-degree cone above or below that pixel (6 px wider); the rest of
  the guess goes. A hem that swings up then shows the leg running on in its own colors, not a faint grey second pair.
- **Objects over arms**: the split draws the arms above the held objects. Where an object's paint is nearer the art
  than an arm's drawn above it (by 0.05 in mean channel difference, 5 px box), in pieces of 300 px or more, the arm
  leaves those pixels to the object before the art is painted on the layers; once the arms are split, each piece goes
  back to the nearest arm (hidden) and the object is drawn right above that arm, unless that arm also lies over the
  object on 300 px or more elsewhere. A book held against a sleeve then never moves with the sleeve.
- **Wings alone**: See-Through also paints the wings' silhouette into a layer under them (the back hair holding a dark
  copy of both wings), which showed once they flapped. A layer's pixels under the wings more than 6 px from where
  that layer shows beside them go; a bit of it then cut off from the rest (under 5 % of its largest piece), seen
  between the wing's spars (which frame 60 % of its rim), goes into that wing (the rest pose stays the same), so it
  flaps with it instead of floating as a dash. "Under the wings" takes in their soft rim (opacity over 0.02, grown
  1 px): the copy of the wing's outline under that rim is no layer showing beside it, else the copy within 6 px of
  it stayed and a swing of the hair drew it out as dashes along the wing's edge; what of it shows through the rim
  goes into the wing.
- **Specks in the front hair**: the split leaves a few specks of other parts in the front hair (white flecks of a
  tail's fur rim), which a lock's swing threw off. Pieces of the front hair (opacity over 0.3) within 6 px of each
  other form a cluster; one under 80 px in all, more than 20 px from the rest of the hair (a broken strand is longer,
  a strand's tip nearer), that one layer holds by 80 % goes to it: composited into the layer under it (not hair or
  the face's), or cleared where a layer above hides it. The rest pose stays the same.
- **Arms over the face**: the split draws the face over the arms, so a finger on the lips was painted onto the face
  and stayed there when the arm moved. A piece of 30 px or more where an arm and the face's layers overlap is the
  arm's when the face's own paint there is off the art (median mean channel difference over 0.06, 5 px box) and the
  arm's is no further off by 0.05; that arm is drawn right above the face's features, below the bangs, and keeps none
  of the face's other pixels.
- **Features away from the face**: See-Through files eyes, brows, a nose or a mouth drawn away from the face (a plush
  toy's face in her arms) with the face's features, which blink, follow the head and fade under face patches, so
  they floated where the toy had been once it moved. A piece of a feature layer (8-connected, alpha over 0.3, grown
  2 px over its soft edge) wholly outside the face's box grown by a quarter of its width goes into the layer drawn
  below it (not the face's own layers or hair) that holds most of a 2 px ring around it (at least half), when the
  layers between are clear there, composited over it (the rest pose stays the same).
- **Objects over the face**: a held object is never behind the face, so where one overlaps the face's layers (pieces
  of 30 px or more: a flute's lip plate at the lips, which See-Through often paints into the face too) the face keeps
  what the art shows and no face patch replaces it (each region leaves it out, grown by 1 px), so an edit that left
  the plate out never makes it pop in and out.
  What the features (eyes, brows, nose, mouth) draw there goes into the face layer (the rest pose stays the same):
  See-Through often paints the plate into the mouth, which a face patch fades out. In a pose that plays an instrument
  at the lips (`poseHands` in the at-mouth set: the flute) the whole mouth area (`patches.mouth_area`) is kept so:
  every face patch then changes the eyes and brows only, as the mouth stays on the instrument.
- **Back hair alone**: See-Through paints a whole sheet of back hair behind the body (flat, the gaps between curls
  drawn as outlines), which showed as a slab once the hair swung out past the body. Back hair the body's clothes
  (neck, top, neckwear, bottom, legwear, shoes) cover by over 0.995, more than 10 px from where the back hair shows,
  goes, fading out from 5 px on (no straight cut edge once it swings out); what arms, hands, objects, the head or
  the bangs cover stays within 20 px of where it shows (fading out from 10 px on), as far as their own small sways
  uncover it (a gesture shows the variant's art there): a long sleeve held out hid a whole sheet of it, a grey ghost
  once the hair swung, and a wider band showed as a darker panel beside the body. The back hair's mesh is held by
  the body near its outline: each vertex's share of `hairBack1`/`hairBack2` fades in over 0.6 face widths of distance
  outside the body's and arms' layers, `chest` (`lower` below the waist) holding the rest, so the hair beside the
  body never parts from it (a sliver of the stage) nor swings out what the body hid of it, and the curls farther out
  swing fully. A bit then cut off from the rest (under 5 % of its largest piece),
  seen through a gap in the clothes that frame 60 % of its rim, would float once the hair swings: it goes into the
  clothing layer framing it most (the rest pose stays the same) and keeps still with it.
- **Hidden paint**: what See-Through guessed of a layer under the layers drawn over it, where it stands apart from a
  blur of the colors that layer shows (mean channel difference over 0.3: a dark slab on a white blouse, a hand drawn
  into the book under it), takes that blur, so a part moving away uncovers the layer's own colors.
- **Rims of moving parts**: reproject gives a pixel to the topmost layer covering it by over half, so the soft edge
  of a sleeve, hand, object, the bangs or locks, the tail or wings stays on the layer under it and a part moving away
  leaves a dotted copy of its outline. Within 2 px of such a part, a pixel a lower layer owns splits into the part's
  nearest color, at the share the art implies, over that layer's own color (a blur of its pixels farther out).
- **Lashes in the bangs**: See-Through draws a lash seen through the bangs (or drawn over them, as a winged liner)
  into the front hair, and face patches lie under the hair, so a closed eye left it there. A front hair pixel within
  4 px of an eye's layers that is dark (luminance under 0.4) and 0.2 darker than the hair around it (the median of
  its opaque pixels in a 21 px square: an 11 px one was mostly lash where a lash tip ran along the bangs) is a lash
  when its dark stroke (8-connected, followed 12 px past the eyes) lies over the eyes by 75 % at least; the outline
  of a strand runs on past them and stays. A blob of such pixels 3 px thick or more (opened by a 3 × 3 square, grown
  back by 1 px) where the lash layer under it draws its own ink (opaque, luminance under 0.4) is a lash too, though
  it joins a strand's outline: the corner of a winged lash on the bangs' edge. Those pixels, grown by 1 px, take a blur of the hair's other colors
  without its dark strokes (LaMa drew the strand's body color, blue on pale tips, down into them). What the art
  showed there goes to `eyelash.<side>.tip`, right above the hair: opaque on the lash pixels, at least half on the
  grown pixel, colored so the rest pose stays exactly the same. It is an eyelash layer, so it follows the eye and
  fades out with the lashes under a face patch, leaving the bangs; LaMa's hidden repaint does not count it as hiding
  the hair. A piece of the hair under 80 px lying over an eye goes to the nearest lash layer as it is.
- **Paper between curls**: the matte calls a whole mass of long hair figure, white pockets between its curls too. A
  near-white pocket below the chin, off the image edge, of 20 px or more, that only the hair layers cover (95 % of it,
  no other layer over half), is as white as paper (mean darkest channel over 0.985) and is framed by the hair's ink
  or color (mean darkest channel on a 2 px ring under 0.75; white hair around a highlight is lighter) is paper.
- **Underlays**: each layer grows up to 10 px under the opaque layers above it (the nearest of its pixels' colors), so
  the seams between still parts never open; under parts that swing away (sleeves, hands, objects, the bangs and
  locks, tails, wings) only 3 px, enough to back their soft edge, so no tab of a layer past its outline shows once
  they move. The back hair, a tail and wings sway on their own behind the body, so they grow only 3 px under
  anything: more swung out past the body's outline as a flat band around the hair.
- **Gaps under moving parts**: a gap of 30 px or more that the layers drawn below a sleeve, hand, object, the bangs or
  the tail enclose where that part covers them joins the layer holding most of its rim, in a blur of its colors. So
  does a gap in the body's layers (all but the back hair, tail and wings) over what lies behind them, when the body
  holds 60 % of its rim, as far as the body lies within 60 px to both sides of it along a row or a column (a braid
  over a blouse the split left open down to the hair behind).
- **What moving parts uncover** (with `--lama`, the LaMa inpainting model `big-lama.pt` as lama-cleaner ships it):
  once the layers are final, each layer's pixels that the layers above cover by over 0.98 are inpainted from what the
  layer shows, on the layer alone over its mean color (the mask reaching 3 px past them, over the soft rim of the
  parts above, and 24 px past them where the layer is clear, whose mean color LaMa took for paint: a strip hidden
  along the outline under a lock faded to grey), in a crop 32 px around them scaled to at most 1024 px; the arm variants' art then takes over where
  it shows those areas. Outline ink copied outward when a layer grows under its neighbours, See-Through's blotches
  and ghosts of a hand on a book give way to the layer's own texture.
- **Held objects**: an object piece follows a hand only when that arm's sleeve or hand comes within 6 px of it (a
  pendant hanging from the belt by the hand stays with the body). One both hands touch (their hand layers, or their
  sleeves without one) goes with the body (`lower` below the waist, `chest` above) and both arms swing 0.05 of their
  `Arm`/`Forearm`/`Wrist` turns and `Shoulder` moves; so do clasped hands (the hand drawn on top hides 15 % or more
  of the other, whose hidden part is a grey guess); otherwise the arm holding an object swings 0.5 of them (what the
  art hides behind it is a guess). `Lift` and `Bend` keep their range: a variant's lead turns the old arm by them, at
  most 30° (a cut-out forearm turned further shears into a flat ribbon while it is still fully drawn); the new arm
  starts as near the old one as its own range allows.
- **Arm sway**: every arm's own sway (`Shoulder`, `Arm`, `Forearm`, `Wrist`) is 0.4 of its range (held objects and
  clasped hands on top of that): what an arm hides of the body is a guess, a flat slab or a ghost of the hand that a
  wide sway uncovers. Gestures are variants and keep their reach; `Lift` and `Bend` keep theirs. An arm whose hand
  lies on the face or an ear (30 px or more over the face's layers, the ears or earwear: a finger at the chin, a fist
  rubbing an eye, a hand on a headphone) keeps to 0.05 of that, `Shoulder` too, so the hand stays there instead of
  uncovering a smear on the neck, a torn jaw or carrying a piece of the headphone away. An arm whose hand, or whose
  sleeve, lies over the other layers (body, clothes, objects, hair: 0.7 or more of its opaque pixels) rather than out
  over the stage keeps to 0.25 of it, `Shoulder` too: what it hides there is a guess as well (a dark smear on the
  chest, the old hand's outline on a book, a slab beside the coat under a long sleeve).
- **Hair swing**: the back hair turns at most 10° and 9° per segment at ±1, a lock or braid 12° and 14° (they were
  14°/12° and 20°/24°): what they hide is a guess. A lock's turns shrink by the share of it (the front hair below the
  chin) lying on the body's layers rather than over the stage or the back hair: a braid resting on the chest hardly
  swings, and what it hides of the clothes (a bow under its end) is a guess.
- **Blink**: `EyeOpen.<side>` at 0 squashes the eye's whites and lower lash to 0.1 of their height about the eye's
  pivot (0.62 down its box), while the upper lash (lash pixels above the pivot, handing over within 0.12 of the
  box's height above it and 0.08 below; the lash tips in the bangs too) rides bone `lid.<side>` (pivot a quarter
  down the box) 0.45 of the box's height down and thins by 0.35: squashed whole, a thin lash had closed to nothing,
  leaving an eyeless face.
- **Earrings over the locks**: See-Through paints an earring seen over the side locks into the earwear and copies it,
  roughly, into the front hair, and a turn of the head moved the two apart (a doubled earring). When the earwear's
  paint is as near the art as the hair's (median mean channel difference, within 0.02) on pieces holding 60 % of its
  overlap with the front hair (30 px or more), it is drawn right above the front hair and keeps none of the other
  pieces, where the art shows the hair; the copy in the hair is then hidden and repainted.
- **Tail**: only a character whose spec has the `tail` hint has one; otherwise a layer See-Through tags as the tail
  (a plush toy in her arms) is an object and follows the hands that hold it. The root is the tail pixel touching
  another layer (back hair excepted) nearest the waist; the midline is
  the geodesic path inside the tail to its farthest pixel. Bones `tail1`–`tail3` (parent the lower body, then each
  other) pivot at 0, 1/3 and 2/3 of its length. A vertex at geodesic fraction `u` weighs `tail3 =
  smoothstep(0.58, 0.75, u)`, `tail2 = smoothstep(0.25, 0.42, u) · (1 − tail3)`, the rest `tail1`, which hands over
  to the lower body below `u = 0.1`. `TailSwish` turns `tail1` ±16° and `tail2` ±10°; the physics chain `tail`
  (stiffness 28, damping 5, wind 0.7, anchored to the hip and pushed along x by `TailSwish`) drives `TailBend1`–`3`
  (−8°, −12°, −16° per unit). The `tailSwing` hint scales these driver turns (not the chain's own angles).
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
- `Squash` stretches (+) or squashes (−) her whole figure over her feet, face and all, so past ±0.2 (2 % taller and
  1.2 % narrower, or the reverse) it reads as the art drawn out or pressed flat: it goes above 0.2 only for an instant
  (under 0.25 s: a take-off, a landing, the turn of a pose swap) and below −0.2 only for that or the crouch before a
  leap (under 0.5 s), and an action that holds her up or down (stretch, yawn, tiptoe, curtsy) stays within ±0.2.
- Second-order springs on most parameters (`springs.py`), so motion overshoots and settles.
- Band change: the pose swap of section 6.
- A living pack also follows the agents' phases and the user's cursor (*Behavior* below).

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
still), **fanflutter** (the hand holding the object waves it three times, a 0.6 s wave), **abacus** (the index flicks beads every half
beat, at least 0.24 s apart, the thumb every other one), **sleeveflop** (hands flopping in long sleeves),
**ripple** (a slow finger ripple), **grip** (both hands holding one object up, fingers 1–4 tightening by 0.1 over two
beats; also for fists held up as paws, which a turning forearm would stretch). `poseHands` sets play on both hands
(flute, sleeveflop, ripple, grip), on the hand
holding the object (fanflutter, tap, regrip, lift) or on the free one (abacus, flex, fan, count, drum).
**flute** and **grip** hold an object in both hands: while the pose's set is one of them, every `Arm.`, `Forearm.`, `Shoulder.`,
`Lift.` and `Bend.` parameter keeps its default after the groove and the action (before the hands and the springs),
so the hands never slide along the instrument and the body sways and bobs with both of them. The flute is also played
at the lips: `AngleX`, `AngleY` and `AngleZ` keep their defaults too (after the idle head drift, before the springs),
so the face never slides along the flute and the mouth never swallows it. The build ties a held object to the `head`
in a band whose `poseHands` set is played at the lips, the flute set rocks no wrist, and a hand in a two-handed set has
no idle wrist sway: head, hands and flute all ride on the chest, and only the fingers move on the keys.

Appendages move at rest too: `TailSwish += (0.18 + 0.3 s) · sway · sin(π · beat phase + 0.5)` and `WingFlap +=
0.12 · sin(2π · breath phase)`, each scaled by `1 − w` while an action of weight `w` drives the same parameter and
clamped to its range; springs give them follow-through.

### Behavior

User's request 2026-11-11: each character feels alive. She reacts to what the agents of her orb are doing and to the
user's cursor, not only to the speed: a *mood* picks her pose, her face, where she looks and which actions she plays,
while the speed band still drives the groove, the beat and the band pose. `characters/reference/motions/behavior.py`
and its use in `director.py` are normative; fixture `behavior.json`.

**Living packs.** A pack is *living* when its manifest lists `states` (section 1) or its idle pose has a `patch`
variant (section 3, *Variants*). Only a living pack takes the inputs below; any other pack moves exactly as the rest
of section 5 says, and a player that does not implement this section plays a living pack as an ordinary one (its
mood poses unused, every `Var.` at 0, so the drawn face shows).

**Inputs**, sampled every frame for the orb's set `A` (METRICS 6.8); times in seconds:

| Input | Value |
| --- | --- |
| `phase` | `generating` when an agent of `A` is generating, else `tools` when one waits for a tool, else `idle` (METRICS 1, with the 300 s staleness of 6.7) |
| `wait` | while `phase` is `generating` and none of the generating agents of `A` has output since its `inFlightStartMs` (no response of it ends at or after that time and its in-flight response has no `firstMs`, METRICS 3.3): the time since the earliest such `inFlightStartMs`; otherwise none. Sources that log a response only when it ends wait until then |
| `idle` | while `phase` is `idle`: the time since the newest `endMs` of a response of `A` (since the app started when there is none); otherwise 0 |
| `hour` | the local hour, 0–23 |
| events | `newPeak` (`newPeak_A`, METRICS 6.8); `trouble`: an agent of `A` starts *Looping* (METRICS 6.10), or a tool result read live fails right after the previous result of that agent failed too |
| `cursor` | the pointer relative to her face in her heights: `((px − fx) / H, (py − fy) / H)`, screen axes (y down), `(fx, fy)` the center of the shown pose's face box at rest as placed on screen (section 6, plus `PosX`), `H` her height on screen; none while the pointer cannot be read (Wayland outside the window), the orb is click-through, hidden or being dragged, or the pointer is on another display |
| `over` | the pointer is inside her hit capsule (DESIGN 2a.6) and no drag runs |
| `click` | a single click on the capsule, as the unit toggle of DESIGN defines it (press and release within 4 pt and 0.5 s, fired once the system double-click time passed without a second click, never the end of a drag); a double-click still opens the dashboard and a right-click the menu |

**Moods.** Two layers. *Event moods* start when their trigger comes and last a fixed time; a trigger during the mood
restarts it. *State moods* follow the inputs; a new state mood replaces the current one only after it has lasted
`1.2 s` (the first frame decides at once). The mood is the highest event mood running, else the state mood.

| Mood | Kind | Trigger or condition | Lasts |
| --- | --- | --- | --- |
| `poke` | event | `click` | 1.6 s |
| `cheer` | event | `newPeak` | 3.2 s |
| `greet` | event | `phase` leaves `idle` while the state mood is `rest` | 2.4 s |
| `trouble` | event | `trouble` | 2.5 s |
| `think` | state | `phase` is `generating` and `wait` ≥ 3 s | |
| `work` | state | `phase` is `generating`; or `tools` for less than 3 s | |
| `focus` | state | `phase` has been `tools` for 3 s (its own clock, restarted by every phase change) | |
| `rest` | state | `phase` is `idle`, and `idle` ≥ 600 s, or ≥ 120 s while `hour` is 0–5; and no click woke her (a `click` while the state mood is `rest` keeps her from resting for 60 s) | |
| `wait` | state | `phase` is `idle` otherwise | |

| Mood | Pose | Face | Looks | Actions |
| --- | --- | --- | --- | --- |
| `poke` | the shown pose | `surprised` for 0.4 s from the click, then `laugh` | user | starts with `doubletake` |
| `cheer` | `cheer` | `excited` | user | starts 0.55 s in with `hop` (with the `cheer` pose) or `jumpjoy`, else `hop` |
| `greet` | `greet` | `smile` | user | without the `greet` pose starts 0.55 s in with `wave`, else `bow` |
| `trouble` | the shown pose | `worried` | user | starts with `chin`, else `shake` |
| `think` | `think` once `wait` ≥ 10 s | `thinking` | up | `chin`, `tilt`, `ponder`, 2–4 s apart |
| `work` | band pose | as *Faces* below (band: idle none, cruise and boost `smile`, overdrive `focused`, redline `excited`) | user | the band's pool and gaps (as section 5) |
| `focus` | `focus` once `tools` has lasted 12 s | `focused` | none | `chin`, `nod`, 3–6 s apart |
| `rest` | `sleepy` | `sleepy`; dozing blinks | none | starts 0.55 s in with `yawn`; then `yawn` 20–40 s apart |
| `wait` | `wait` once `idle` ≥ 20 s and no attention (below) | `smile` while `idle` < 6 s, else none | user | the idle pool and gaps without `yawn` and `stretch` until `idle` ≥ 180 s; entering `wait` from `work`, `think` or `focus` starts with `nod` |

- **Actions.** A mood's first actions are tried in order when no action plays and their time has come (the first
  one the pose can play starts); they are dropped when the mood changes before. They never cut a playing action
  short. Between actions she picks from the mood's list, shuffled, skipping the last three played, after the mood's
  gap × the profile's `gap` (a change of mood restarts that wait from the new mood's gap). The `work` and `wait`
  lists take the band's signature actions of the profile (section 5, *Motion profile*); the other moods' lists only
  drop what the profile does not allow. While the cursor is `over` her in `work` or `wait` she starts nothing from
  her list. The arm gestures `thumbsup`, `shh`, `cheek`, `salute` and `reach` join those of section 5 (hold s,
  flourish, expression, tiptoe: 1.4 bob smile no; 1.6 slow smile no; 1.8 slow shy no; 1.2 bob smile no; 1.6 slow
  sleepy yes; `gestures.py`). In a living pack `yawn` and `hum` play in every pose; their face and lips show
  where the pose has the patches.
- **Pose.** The pose to show is the mood's pose of the table when the pack has that state; for `poke` and `trouble`
  the pose that shows already; else the band pose (section 6). A band change swaps nothing while a mood pose shows.
- **Faces.** One expression patch (`expr-<name>` of the shown pose) at a time; the name, in order: `poke` as above;
  `yawn` while the `yawn` action plays; in moods other than `work` and `wait` the mood's face; else a hover `wink`
  (below); else the playing action's expression (section 5 names map `smile` → `smile`, `happy` → `excited`,
  `surprised` → `surprised`, `determined` → `focused`, `sleepy` → `sleepy`, `talk` → none, others as named; `giggle` →
  `laugh`); else in `work` the band change face (section 5: `excited` for 1.2 s after the band rises, `surprised` when
  it rises into redline, `smile` for 1.5 s after it falls); else the mood's face; else `smile` while hovered (below).
  A name the pose has no patch for shows the drawn face. While she speaks (*Talking*) no expression shows: the drawn
  face and the mouth shapes only.
- **Gaze.** With no expression showing and while she does not speak, the eyes take the patch `gaze-<direction>` of
  where she looks (below) when the pose has it, else the drawn eyes and their `EyeBall` glance.
- **Swaps and blinks.** When the wanted expression or gaze differs from the shown one she blinks at once and they
  swap on the first frame her eyes are shut (`EyeOpen` ≤ 0.02), or, at a frame rate too low to show them shut (15
  fps), on the first frame after the shut phase passed. A blink closes over 0.07 s, stays shut 0.04 s
  (0.5–1.2 s while she rests: dozing, drawn from the behavior generator when the blink starts) and opens over
  0.09 s; the next one comes 2–5 s later, or 0.25 s later when a draw of `random()` is at most 0.15 (a
  double blink), from the director's generator. While a patch covers the eyes, a blink shows `gaze-closed` while the
  eyes are under half open, except over `laugh` and `yawn`, whose eyes are shut already. Expression and gaze patches
  fade in 0.05 s, `gaze-closed` in 0.03 s. A pose swap shows the new pose's face patches at once, without a blink.
- **Talking** is an anime lip flap, not lip sync. She talks while (she is in `work`, `phase` is `generating`, `wait` is
  none and the level is at least 1) or the playing action talks (`hum`, `sway`), unless the profile's `talk` is false,
  the `yawn` action plays or the pose has none of `mouth-a`, `mouth-o`, `mouth-u` (`mouth-i`, the teeth, never
  talks). She talks in groups of 2–4 phrases; a phrase is 3–8 syllables at `min(7, uniform(5, 6) + 0.1 level)`
  syllables per second (drawn when the phrase starts); a syllable shows one shape for 55–70 % of it, drawn with
  weights `a` 0.6, `o` 0.25, `u` 0.15 among those the pose has, then closed for the rest (`mouth-closed` when the pose
  has it, else the drawn mouth). Phrases are 0.4–1.2 s apart, and a group is followed by 2–5 s of silence, so she
  does not talk all the time. She *speaks* from the start of a group until its silence: the face is drawn then (no
  expression and no gaze patch; the expression leaves at a blink, and the group's first syllable waits until it has
  faded out), and the expressions of *Faces* come back, at a blink, in the silence. Mouth shapes switch on a frame,
  with no fade (a blend of two mouths would show both); they lie over the face (section 3, *Variants*). The counts
  are drawn when the group, the phrase or the syllable starts, from the behavior generator.
- **Looking.** Per mood she looks at the *user*, *up* or *none* (table). *User* with a `cursor` within 2 heights of
  her face: the head targets `AngleX = 0.7 · clamp(x / 0.8)`, `AngleY = −0.5 · clamp(y / 0.8)` and the eyes
  `EyeBallX = clamp(x / 0.3)`, `EyeBallY = clamp(y / 0.3)` (clamp to ±1; `AngleY` and `EyeBallY` signs as section 5,
  up is `+AngleY`, `−EyeBallY`); the event moods without such a cursor look straight ahead (all 0). *Up*: `0.15`,
  `0.3`, `0.3`, `−0.8`. The look weight rises to 1 over 0.4 s while it applies and falls to 0 over 0.8 s otherwise,
  keeping the last target. Each parameter becomes `v (1 − k) + target · k` with `k` the weight times the share the
  playing action leaves it (`1 − w` when the action moves it): the head after breathing and before the idle head
  drift (so springs and drift still play), the eyes after the glance. The gaze direction: with a cursor whose larger
  component `max(|x|, |y|)` is at least 0.35 (0.25 once a gaze shows), the side of the larger component (`left`,
  `right`, `up`, `down`; the shown axis keeps until the other component exceeds it by a factor 1 / 0.75); `up` for
  *up*; else none. The direction changes at most every 0.5 s.
- **Hovering** (`over`, in `wait` and `work`): after 0.8 s she smiles (above) and has her *attention* until 8 s after
  the cursor leaves her (the `wait` pose gives way meanwhile); after 4 s, once per hover, she plays one of `wave`,
  `peace`, `thumbsup` the pose has (drawn from the behavior generator), else shows `wink` for 1.2 s when the pose has
  it. Leaving her after at least 1 s of hovering, in the idle or cruise band, she plays `wave`, else `nod`.

**Random numbers.** The behavior has its own generator, seeded with the director's seed + 1 (mod 2^64): the hover
gesture, the groups, phrases, syllables, pauses and silences of talking, the dozing blinks. Everything else draws from
the director's generator as in section 5; the director picks a new gap when the mood changes.

**Order of a frame** (`Director.update(dt, inputs)`): the behavior (phase clock and triggers, then the state mood,
the mood and its first actions, then hovering, then looking), a new gap if the mood changed; then section 5 as before
(grooves, the action: first actions of the mood, else the list), with the head look after breathing, the eye look
after the glance, and the face patches in place of section 5's face variants. The mood pose is read after the frame
(`pose_wanted`) and swapped by section 6.

**Missing assets.** Every rule above falls back on its own: a mood pose the pack lacks shows the band pose; an
expression, gaze or mouth patch the shown pose lacks leaves that part as drawn (mood poses and band poses without
patches show their drawn faces); without `gaze-closed` a blink over a patch shows nothing, and swaps still wait for the
blink; without `mouth-a`, `mouth-o` or `mouth-u` she does not move her lips (and her expressions stay while the speed
streams); a first action the pose cannot play gives way to the next of its list, then to nothing. Following the cursor,
a face or gaze swap and talking count as acting for the frame policy (DESIGN 2a.6).

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

In a living pack the pose to show is the mood's pose when it names one (section 5, *Behavior*), else the band pose,
and every change of it, whatever the reason, is the same swap; while a mood pose shows a band change swaps nothing.
On the swap the director takes the new pose's variants: an action playing a variant of the old pose stops on the
edge-on frame, and the face patches of the new pose show at once. A mood pose is aligned and scaled as a band pose
(below). A seated character crossfades as for bands.

Poses, band and mood poses alike, are aligned on screen by the feet and scaled to match the idle pose by the middle of
three ratios (idle over pose): the distance from the face center to the feet, the diagonal of the face box, and
`torsoLength` (shoulder to hip midpoints from the pose estimator, written by the build). Any one of them can be thrown
off by the pose (legs tucked in a jump shorten face-to-feet, a turned or tilted face changes its box), and the other two
outvote it. A pose without `torsoLength` (packs built before it) is scaled by the face-to-feet ratio alone.

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

Beside the character, never behind her: the speed (rolling digits sampled five times a second, outlined, with
lightning when they change, the unit and the speed title) at her right at chest height; the facts — 性能 and 思考,
network speed, 費用, today's tokens and agents — as glass cards in a column at her left from her shoulders down; and,
when switched on, the model chip (vendor dot and model name) above her head. Each card keeps clear of her outline as
drawn this frame (the skinned mesh), shows while it has something to say, and floats on its own slow figure of eight;
when her left side has no room the column's last cards continue under the speed. The layout is `HudWings` and
`CharacterPerformer.trace` in the macOS engine (DESIGN 2a.6, *Readouts*, is normative). At her feet a ring of 96 lights
is the gauge, filled in the heat colors with a comet; agents are lights orbiting her waist; response pulses ripple out
from the ring; embers rise at redline; a new peak pops a gold pill above the speed. See `characters/reference/hud.py`
(its orbiting bodies are the earlier layout, kept for the films) and DESIGN 2a.6.

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

Every player loads each fixture in `spec/fixtures/characters/` and matches it within 1e-6 (1e-4 for `director.json`,
`director-profiles.json` and `behavior.json` values): `rng`, `curves`, `springs`, `orbits` (positions and Kepler
solutions), `rig` (world transforms, vertices, opacities of a small rig), `physics`, `odometer` (text and per-digit roll
state), `switch`, `director`, `director-profiles` (three profiles with tail, wing and ear parameters: a flutist with
pose hand sets, a seated silent character, a cat, each across pose changes), `behavior` (a scripted session of section
5, *Behavior*, through every mood: a living pack with every mood pose, face patch and hover gesture, and a sparse one
with one mood pose, one expression and three mouths; values within 1e-4, moods, poses, faces, gazes, mouth shapes and
actions exactly). `python3 -m characters.tests.make_fixtures --check` keeps them in step with the reference.
