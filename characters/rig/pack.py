"""Assembles a downloadable character pack from one rig package per speed band, and updates the catalog.

  python3 -m characters.rig.pack --spec characters/specs/claude.json --pose idle=build/claude \\
      --pose cruise=build/claude-cruise ... [--state think=build/claude-think ...] --out dist \\
      [--catalog dist/catalog.json --base-url URL]

The pack is a zip holding manifest.json, preview.png and poses/<band>/{rig.json, atlas.png}, and for each mood pose
poses/<state>/ listed under the manifest's `states`; the catalog lists every pack with its size and SHA-256 so the app
can verify a download before it unpacks it (spec/CHARACTERS.md).
"""
import argparse
import hashlib
import json
import shutil
import zipfile
from pathlib import Path

import numpy as np
from PIL import Image

from ..reference.motions.behavior import STATES
from . import package

FORMAT = "agent-speed-character-pack"
CATALOG = "agent-speed-character-catalog"
VERSION = 1
BANDS = ("idle", "cruise", "boost", "overdrive", "redline")
LANGUAGES = ("zh-Hant", "zh-Hans", "en", "ja")
SPEC_KEYS = {"id", "packVersion", "name", "vendors", "accent", "artist", "license", "minApp", "motion", "rig"}
# Build hints for characters.rig.build --spec; not part of the manifest.
BUILD_ONLY = {"rig"}
MOTION_KEYS = {"tempo", "energy", "sway", "bounce", "gap", "idle", "seated", "talk", "signature", "exclude", "hands",
               "poseHands", "particles"}


def load_spec(path):
    spec = json.loads(Path(path).read_text())
    missing = {"id", "packVersion", "name", "vendors", "accent"} - set(spec)
    if missing:
        raise ValueError(f"{path}: missing {sorted(missing)}")
    unknown = set(spec) - SPEC_KEYS
    if unknown:
        raise ValueError(f"{path}: unknown keys {sorted(unknown)}")
    unknown = set(spec.get("motion", {})) - MOTION_KEYS
    if unknown:
        raise ValueError(f"{path}: unknown motion keys {sorted(unknown)}")
    if set(spec["name"]) != set(LANGUAGES):
        raise ValueError(f"{path}: name needs exactly {LANGUAGES}")
    if not spec["id"].replace("-", "").isalnum() or spec["id"].lower() != spec["id"]:
        raise ValueError(f"{path}: id must be lowercase letters, digits and dashes")
    return spec


def preview(pose_dir, size=(512, 640)):
    """The idle pose at rest on transparency, cropped to the character and fitted into `size`."""
    from ..reference.render import Renderer
    from ..reference.rig import Rig
    doc, atlas = package.read(pose_dir)
    rig = Rig(doc)
    renderer = Renderer(rig.canvas, atlas, rig.parts)
    renderer.begin((0, 0, 0, 0))
    renderer.character(rig.pose(rig.defaults()), 1.0, (0.0, 0.0), rig.opacities(rig.defaults()))
    rgba = renderer.finish()
    ys, xs = np.nonzero(rgba[..., 3] > 8)
    crop = Image.fromarray(rgba[ys.min():ys.max() + 1, xs.min():xs.max() + 1])
    crop.thumbnail(size, Image.LANCZOS)
    out = Image.new("RGBA", size, (0, 0, 0, 0))
    out.alpha_composite(crop, ((size[0] - crop.width) // 2, size[1] - crop.height))
    return out


def _copy_pose(source, root, name):
    doc, _ = package.read(source)
    target = root / "poses" / name
    target.mkdir()
    for file in ("rig.json", doc["atlas"]["file"]):
        shutil.copy2(Path(source) / file, target / file)
    return f"poses/{name}"


def build(spec, poses, out_dir, states=None):
    if "idle" not in poses:
        raise ValueError("a pack needs at least the idle pose")
    states = states or {}
    unknown = set(states) - set(STATES)
    if unknown:
        raise ValueError(f"unknown states {sorted(unknown)}")
    root = Path(out_dir) / spec["id"]
    if root.exists():
        shutil.rmtree(root)
    (root / "poses").mkdir(parents=True)
    manifest_poses = {band: _copy_pose(poses[band], root, band) for band in BANDS if band in poses}
    manifest_states = {state: _copy_pose(states[state], root, state) for state in STATES if state in states}
    preview(poses["idle"]).save(root / "preview.png", optimize=True)
    manifest = {"format": FORMAT, "version": VERSION, **{k: spec[k] for k in SPEC_KEYS - BUILD_ONLY if k in spec},
                "poses": manifest_poses, "preview": "preview.png"}
    if manifest_states:
        manifest["states"] = manifest_states
    (root / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=1))

    archive = Path(out_dir) / f"{spec['id']}-{spec['packVersion']}.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for f in sorted(root.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(root).as_posix())
    data = archive.read_bytes()
    return manifest, archive, {"size": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def catalog_entry(manifest, archive, digest, base_url):
    entry = {k: manifest[k] for k in ("id", "packVersion", "name", "vendors", "accent") if k in manifest}
    for k in ("artist", "minApp"):
        if k in manifest:
            entry[k] = manifest[k]
    entry.update(digest)
    entry["url"] = f"{base_url.rstrip('/')}/{archive.name}"
    entry["preview"] = f"{base_url.rstrip('/')}/{manifest['id']}-preview.png"
    return entry


def update_catalog(path, entry):
    path = Path(path)
    doc = json.loads(path.read_text()) if path.exists() else {"format": CATALOG, "version": VERSION, "characters": []}
    doc["characters"] = [c for c in doc["characters"] if c["id"] != entry["id"]] + [entry]
    doc["characters"].sort(key=lambda c: c["id"])
    path.write_text(json.dumps(doc, ensure_ascii=False, indent=1))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--spec", required=True)
    ap.add_argument("--pose", action="append", default=[], help="band=package directory")
    ap.add_argument("--state", action="append", default=[], help="mood pose: state=package directory")
    ap.add_argument("--out", required=True)
    ap.add_argument("--catalog")
    ap.add_argument("--base-url", default="https://github.com/zoefix/agent-speed/releases/download/characters")
    args = ap.parse_args(argv)
    spec = load_spec(args.spec)
    poses = {}
    for item in args.pose:
        band, path = item.split("=", 1)
        if band not in BANDS:
            raise ValueError(f"unknown band {band}")
        poses[band] = path
    states = dict(item.split("=", 1) for item in args.state)
    manifest, archive, digest = build(spec, poses, args.out, states)
    shutil.copy2(Path(args.out) / spec["id"] / "preview.png", Path(args.out) / f"{spec['id']}-preview.png")
    entry = catalog_entry(manifest, archive, digest, args.base_url)
    if args.catalog:
        update_catalog(args.catalog, entry)
    print(json.dumps(entry, ensure_ascii=False))


if __name__ == "__main__":
    main()
