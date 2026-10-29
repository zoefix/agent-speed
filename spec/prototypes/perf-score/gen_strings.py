#!/usr/bin/env python3
"""Embed the strings the perf-score prototype uses from spec/strings.json into index.html."""
import json
import pathlib
import re

HERE = pathlib.Path(__file__).resolve().parent
SOURCE = HERE.parent.parent / "strings.json"
TARGET = HERE / "index.html"
BLOCK = re.compile(r"(/\* strings:begin \*/).*?(/\* strings:end \*/)", re.S)

PREFIXES = (
    "perf.", "a11y.perf.", "history.perf.", "a11y.history.perf.", "intel.", "work.", "phase.", "source.",
    "history.range.", "speed.value.", "a11y.orb.intel", "a11y.board", "boards.more",
)
KEYS = (
    "a11y.agent.intel", "agents.intel.hint", "unit.tokS", "dashboard.agents", "agents.context", "cost.glyph",
    "cost.unitPerSecond", "history.title", "history.allSources", "history.export", "history.privacy",
    "history.privacy.short", "history.analyzing", "list.separator", "effort.tooltip", "speed.tooltip",
    "boards.subCount", "boards.today", "boards.peak", "title.maglev", "title.jetliner", "title.sportsCar",
    "title.walk", "privacy.perfScore", "report.section.models.perfScore", "report.section.models",
    "orbs.traffic.intelligence", "settings.bar.intelligence", "settings.bar.intelligence.hint",
    "boards.readouts.intel", "boards.readouts.hint", "traffic.agent.tooltip", "cost.noModelName",
)


def main():
    table = json.loads(SOURCE.read_text(encoding="utf-8"))
    picked = {k: v for k, v in table.items()
              if not k.startswith("_") and (k.startswith(PREFIXES) or k in KEYS)}
    missing = [k for k in KEYS if k not in picked]
    if missing:
        raise SystemExit(f"missing keys: {missing}")
    body = json.dumps(picked, ensure_ascii=False, separators=(",", ":"))
    html = TARGET.read_text(encoding="utf-8")
    if not BLOCK.search(html):
        raise SystemExit("strings block not found in index.html")
    html = BLOCK.sub(lambda m: f"{m.group(1)}\nwindow.PS_STRINGS = {body};\n{m.group(2)}", html, count=1)
    TARGET.write_text(html, encoding="utf-8")
    print(f"{len(picked)} strings -> {TARGET.name}")


if __name__ == "__main__":
    main()
