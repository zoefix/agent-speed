# Characters (experiment)

Anime characters that stand beside the orb and act out the speed of the fastest model. This folder holds the offline
tools; the apps will play the packages they produce.

```
illustration ──See-Through──▶ layered PSD ──rig.build──▶ package (rig.json + atlas.png) ──▶ players
```

- `rig/` builds a package from a [See-Through](https://github.com/shitagaki-lab/see-through) PSD and the original
  illustration: semantic draw order, the original pixels reprojected onto the visible layer (the rest pose equals the
  art), left/right split of arms and eyes, underlays grown beneath moving parts, meshes, bones, skinning weights,
  parameters and physics chains.
- Pose and expression variants are ChatGPT images of the same character (a raised hand, a peace sign, a smile),
  aligned to the base art and split by See-Through. Their arms and faces become extra parts that swap in; the old and
  new arm turn to meet halfway so the swap is hidden. What the variants show behind the arms replaces See-Through's
  guess there, and each arm variant also carries its own copy of that area.
- `reference/` is the reference player (rig evaluation, physics, motions, effects) and a preview renderer that films
  the character beside a mock orb while the speed climbs and falls.

## Run

```bash
python3 -m venv .venv && .venv/bin/pip install -r characters/requirements.txt
.venv/bin/python -m characters.rig.build --psd claude.psd --reference claude.png --name claude --out characters/build/claude \
    --variants variants/manifest.json
.venv/bin/python -m characters.reference.preview --package characters/build/claude --rest-check claude.png
.venv/bin/python -m characters.reference.preview --package characters/build/claude --out claude.mp4
.venv/bin/python -m characters.reference.preview --package characters/build/claude --reel --out claude-actions.mp4
.venv/bin/python -m unittest characters.tests.test_reference
```

## Package format (version 1)

`rig.json` coordinates are canvas pixels with y pointing down; rotations are degrees, clockwise on screen.

| Key | Content |
| --- | --- |
| `canvas`, `atlas` | Canvas size; atlas file and size (straight alpha, colors bled into transparent texels). |
| `bones` | `name`, `parent` (listed before its children), `pivot`. A bone's local transform is translate(x, y) · translate(pivot) · rotate · scale · translate(−pivot); its world transform is the parent's world transform times its local one. |
| `params` | `id`, `min`, `max`, `default`. |
| `drivers` | `param`, `bone`, `channel` (`rotate`, `x`, `y`, `scaleX`, `scaleY`), `keys` [[value, amount], …], linear between keys and flat outside. Amounts of one channel add up; scale amounts are added to 1. |
| `physics` | Pendulum chains: `anchor`, `segments` (lengths), `inputs` (anchor x per parameter unit), `outputs` (parameter = tanh(segment angle relative to the previous segment / `degrees`)), `stiffness`, `damping`, `wind`. |
| `parts` | Draw `order`, `vertices`, `uv` (atlas pixels), `triangles`, `influences` per vertex with `bones` and `weights` (sum 1). A vertex is the weighted sum of its bones' world transforms applied to it. |
| `partDrivers` | `param`, `part`, `keys` [[value, opacity], …]. A part's opacity is the product of its drivers (1 without any). |
| `variants` | `id`, `kind` (`arm`, `face`), `param` (`Var.<id>`, 0 to 1). Arms add `side`, `front`, `arm`, `forearm`, `lead` (values the old arm's parameters move to before the swap) and `approach` (values the new arm starts from). Faces add `mouth` (`Mouth.<id>`) and `eyesClosed`. |

Sides (`.left`, `.right`) are image sides.

## Art

Character art is not stored in this repository. Each illustration belongs to its creator and is used only with their
permission.
