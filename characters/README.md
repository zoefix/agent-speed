# Characters (experiment)

Anime characters that stand beside the orb and act out the speed of the fastest model. This folder holds the offline
tools; the apps will play the packages they produce.

```
illustration ──See-Through──▶ layered PSD ──rig.build──▶ package (rig.json + atlas.png) ──▶ players
```

- `rig/` builds a package from a [See-Through](https://github.com/shitagaki-lab/see-through) PSD and the original
  illustration: semantic draw order, the original pixels reprojected onto the visible layer (the rest pose equals the
  art), left/right split of arms and eyes (the one open eye of a wink takes its side, so it turns with the head),
  underlays grown beneath moving parts, meshes, bones, skinning weights, parameters and physics chains. The irises
  travel 0.017 face widths across and 0.009 up and down at full glance, a small glance that never bares the white.
- Pose and expression variants are ChatGPT images of the same character (a raised hand, a peace sign, a smile),
  aligned to the base art and split by See-Through. Their arms and faces become extra parts that swap in; the old and
  new arm turn to meet halfway so the swap is hidden. What the variants show behind the arms replaces See-Through's
  guess there, and each arm variant also carries its own copy of that area.
- `reference/` is the reference player (rig evaluation, physics, motions, effects) and a preview renderer that films
  the character beside a mock orb while the speed climbs and falls.
- Hands get a wrist bone and three bones per finger. A hand pose model (RTMPose hand) finds the joints on the art and
  Segment Anything (ViT-B, `--sam` checkpoint) cuts the drawn hand out of its arm, so fingers curl, spread and trail
  the wrist without dragging the book or the dress along.
- `reference/stage.py` films the character style: the character alone on the desktop with the speed, its title, the
  model and the agents orbiting her on Kepler orbits (in front of her and behind her), a ring of lights at her feet as the gauge, and one illustration per speed
  band swapped while she turns edge-on.
- `reference/motions/` drives the whole body: a groove per speed band on a beat clock (weight shifts, bounce, arm
  swing), one action at a time (arm gestures, two-arm combos, hops, a spin, side steps, a curtsy, a sway dance,
  running in place, head and face gestures), and second-order springs on top so every move overshoots and settles.
  Curves pass through their keys on a monotone cubic (PCHIP).

## Run

```bash
python3 -m venv .venv && .venv/bin/pip install -r characters/requirements.txt
.venv/bin/python -m characters.rig.build --psd claude.psd --reference claude.png --name claude --out characters/build/claude \
    --variants variants/manifest.json
.venv/bin/python -m characters.reference.preview --package characters/build/claude --rest-check claude.png
.venv/bin/python -m characters.reference.preview --package characters/build/claude --out claude.mp4
.venv/bin/python -m characters.reference.preview --package characters/build/claude --reel --out claude-actions.mp4
.venv/bin/python -m characters.reference.stage --package characters/build/claude --pose boost=characters/build/claude-boost \
    --out claude-stage.mp4
.venv/bin/python -m unittest characters.tests.test_reference
```

Every other character builds the same way with her spec, which brings the rig hints (tail, wings, recovery of art
See-Through missed) and, for the stage, her motion profile, particles and accent. The anime figure matte
([skytnt/anime-seg](https://huggingface.co/skytnt/anime-seg) `isnetis.onnx`, `--matte`) tells the white paper that
shows through an axe blade or between hair and body from white clothing, so neither the recovered art nor any layer
draws it:

```bash
.venv/bin/python -m characters.rig.build --psd deepseek.psd --reference deepseek.png --name deepseek \
    --out characters/build/deepseek --spec characters/specs/deepseek.json --sam sam_vit_b_01ec64.pth --matte isnetis.onnx
.venv/bin/python -m characters.reference.stage --package characters/build/deepseek \
    --spec characters/specs/deepseek.json --out deepseek-stage.mp4
```

## Package format (version 1)

`rig.json` coordinates are canvas pixels with y pointing down; rotations are degrees, clockwise on screen.

| Key | Content |
| --- | --- |
| `canvas`, `atlas` | Canvas size; atlas file and size (straight alpha, colors bled into transparent texels). |
| `bones` | `name`, `parent` (listed before its children), `pivot`. A bone's local transform is translate(x, y) · translate(pivot) · rotate · scale · translate(−pivot); its world transform is the parent's world transform times its local one. |
| `params` | `id`, `min`, `max`, `default`. |
| `drivers` | `param`, `bone`, `channel` (`rotate`, `x`, `y`, `scaleX`, `scaleY`), `keys` [[value, amount], …], linear between keys and flat outside. Amounts of one channel add up; scale amounts are added to 1. |
| `physics` | Pendulum chains: `anchor`, `segments` (lengths), `inputs` (anchor x and y per parameter unit), `outputs` (parameter = tanh(segment angle relative to the previous segment / `degrees`)), `stiffness`, `damping`, `wind`. |
| `parts` | Draw `order`, `vertices`, `uv` (atlas pixels), `triangles`, `influences` per vertex with `bones` and `weights` (sum 1). A vertex is the weighted sum of its bones' world transforms applied to it. |
| `partDrivers` | `param`, `part`, `keys` [[value, opacity], …]. A part's opacity is the product of its drivers (1 without any). |
| `variants` | `id`, `kind` (`arm`, `face`), `param` (`Var.<id>`, 0 to 1). Arms add `side`, `front`, `arm`, `forearm`, `lead` (values the old arm's parameters move to before the swap) and `approach` (values the new arm starts from). Faces add `mouth` (`Mouth.<id>`) and `eyesClosed`. |

Sides (`.left`, `.right`) are image sides.

## Art

Character art is not stored in this repository. Each illustration belongs to its creator and is used only with their
permission.
