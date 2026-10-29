# Leaderboard panel prototypes (排行看板)

A new floating window, separate from the orbs (which stay exactly as they are): a rounded square that ranks every
agent by live tok/s so the user sees their speed and what matters at a glance. These prototypes compare visual
directions before the platform specification (DESIGN 2c) is written. All of them render the same simulation
(`sim.js`), so differences are design only.

Open any `board-*.html` directly in a browser (no server, no network).

## Content (every direction)

1. **Hero** — total live tok/s (METRICS 7 format, tabular digits), `tok/s`, the speed title in heat color
   (DESIGN 1), `生成中 N · 工具 M`, and the last 60 s of total speed as an ambient sparkline.
2. **Board** — groups ranked by group speed (a root agent plus all its sub-agents, the user's choice). Each group row:
   rank, phase badge in the source color (generating: three-dot shimmer; tools: wrench; idle: grey dot), project,
   model, the effort capsule (DESIGN 4 Agents: meter, word, bolts), live value, and a bar of its share of the total.
   Under a group, its fastest sub-agents (smaller rows), then `+97 個子 agent · 8.6k` for the rest.
3. **Footer** — today's output tokens, today's cost, today's peak.

Sizes: S 280 × 280 (up to 4 groups, no sub rows), M 340 × 340 (default; up to 6 groups, two sub rows for #1, one for
#2–#3), L 420 × 420 (up to 8 groups; three sub rows for #1–#3, two for #4–#5). Groups first, sub rows fill what
remains. Light and dark. The normative specification is `spec/DESIGN.md` 2c; these files are its visual reference.

## Ranking (shared, `sim.js`)

Groups rank by the value they show (the engine's 3 s live rate); a group passes the one above only after leading it by
more than 10 % + 1 tok/s for two ticks (1 s), or at once by more than 2× + 20, so the window's wobble never reshuffles
the board. Idle groups keep their order by latest
activity. Each tick reports `events`: `overtake` / `newLeader` with `from` and `to` ranks.

## Motion rules

- Overtake: rows move to their new places with a spring (FLIP, ≤ 0.5 s); the climber gets a short accent (light
  sweep, rank roll, `▲2` chip); the passed row only follows. New leader: a distinct, richer accent on row 1.
- Values: digits roll, bars and meters spring; nothing jumps.
- Idle and steady states cost nothing: only transform / opacity / filter animations, no layout thrash, and no
  animation frames while nothing changes (a slow breathing glow at most).
- Reduce Motion (control in the page): crossfades only.

## Controls (every page)

Scenario (`swarm`: one workflow with 100 sub-agents plus three projects; `team`; `duel`; `calm`), size S/M/L, theme
dark/light, Reduce Motion, pause / step. Keep the controls quiet and outside the panel; the panel floats on a desktop
backdrop.

## Effects

`fx.js` is the shared effects layer: perimeter comets (count and lap time by heat), the heat ring and bloom, sparks,
row streaks, shockwaves, lightning, the new-leader celebration and neon's hyperspace streaks. Append `?steps=40&
scenario=team&size=L&theme=light` to a page for a settled frame without motion (screenshots); add `&fx=1` to keep the
effects and fire a new-leader celebration.

## Directions

| File | Direction |
| --- | --- |
| `board-glass.html` | 流光玻璃 — refined translucent glass, heat-tinted aurora light, calm typography; elegance first |
| `board-timing.html` | 計時塔 — racing broadcast timing tower: position blocks, gap-to-leader column, kinetic overtakes |
| `board-neon.html` | 霓虹座艙 — night HUD in the LED orb's language: neon edges, LED digits, warp streaks and digit lightning |

## New live metrics (`board-metrics.html`)

> Since 2026-10-29 the 智力 readout below is **性能**, the performance score (DESIGN 2c and 12): an uncapped number
> where 1000 is the reference, drawn on a log arc and meter. `board-metrics.html` still shows the earlier index; the
> 性能 numbers, arc, meter, row cells and score card are drawn in `spec/prototypes/perf-score/index.html`.

The board also shows the live **intelligence index** (智力, METRICS 6.9), **thinking** tok/s (思考) and the **cost per
second** (費用, METRICS 9.5 in the display currency, DESIGN 9 rate form, `≥` when part is unpriced, `–` when nothing is).
`board-metrics.html` compares three layouts for them in every style, all rendered from the same simulation; the three
pages above are unchanged. Files: `board-metrics.html` (page), `board-metrics.js` (a multi-instance board for any
style and option), `board-metrics.css`, `metrics-format.js` (money and index formatting, checked against
`spec/fixtures/money-format.json`).

| | Glass 流光玻璃 | Timing 計時塔 | Neon 霓虹座艙 |
| --- | --- | --- | --- |
| **A** — a band of three equal readouts under the hero | 三欄儀表: frosted capsule, hairline separators, label over value | 數據條: three broadcast chips, label tab + mono value | LED 視窗: three recessed LED windows with ghost segments |
| **B** — the hero's right side becomes a sub-dial (智力 large, 思考 / 費用 small) | 光環副錶: pearl arc around 智力; the 60 s sparkline becomes a full-width horizon strip | 資料盒: label tabs, one right edge for all values; the 30 bars become a full-width strip | LED 副錶: LED digits, a 12-segment meter; the LED bar stays |
| **C** — the board becomes a table: each group row gets a second line (model · its cost per second); the three totals move to a two-row footer, live over today (智力 over today's tokens, 費用 over today's cost, 思考 over the peak) | 明細表 | 明細表 (position block spans both lines) | 明細表 |

**Every option, every row**: a group row and a sub row show their own intelligence in a 3-digit column between the
effort capsule and the value (group rows: the group's index, over the same agents as its speed; sub rows: the
sub-agent's own). An idle row shows its grade (`class + 6 × level`) in the quieter ink. The column names 智力 and
`tok/s` sit on the hairline at the top of the ranking. Timing hides the capsule's effort word below L so names stay
readable (meter and bolts remain; the word is in the tooltip).

**Rows kept** (team scenario; without the metrics: S 4 groups, M 5 groups + 0–1 sub row, L 5 groups + 3–4 sub rows):

| | S | M | L |
| --- | --- | --- | --- |
| A | 3 + `還有 N 個` | glass, neon 4 + `還有`; timing 5 | 5 + 1–2 sub rows |
| B | 3 + `還有 N 個` | 4 + `還有` | 5 (timing + 1 sub row) |
| C | 3 + `還有 N 個` | 4 + `還有` | 5 (+ 0–1 sub rows) |

**At S**: A's readouts become one line each (label left of the number); B's sub-gauge narrows to 92 pt (timing's data
box 110 pt); C's rows return to one line (the per-row cost collapses, the totals stay in the footer); every option keeps
3 groups and the rest line.

**Chosen (2026-10-28): B**, specified in `spec/DESIGN.md` 2c *Live metrics*; the page opens on it. Its group rows keep
the second line with the model at M and L, without a cost (user's choice, 2026-10-28: the board does not need a live
cost per agent; the 每列費用 switch is retired). Where this page and that section differ, the section wins.

**Switches** (`看板顯示 ▸ 智力 / 思考 / 費用` in the board's context menu and the board editor, all on by default and
independent of each other; try them with 顯示 under the boards): a readout switched off folds away and the remaining
readouts re-centre; 智力 also removes the rows' intelligence column; with all three off a board looks as before, except
that group rows at M and L keep the model line. 思考為 0 (`noreasoning=1`) simulates sources that do not record
reasoning: 思考 reads 0 and, after 3 s, folds away (a settled frame replays those 3 s).

**Simulation**: `sim.js` adds per agent `grade`, `intelligence` (none while idle), `thinking`, `costRate` (µUSD/s),
`priced`, and per group and total the METRICS 6.9 window sums (`intelligence`, `thinking`, `thinkingShare`) and
`costRate` / `costKnownShare`, plus `total.costToday`. They are derived without drawing from the shared random
sequence, so every existing field is identical step for step. Prices follow `spec/assets/prices.json` (gpt-6.1-sol and
gemini-3.5-pro assumed); Ultrafast has no list price, so the swarm and duel scenarios show `≥`.

URL parameters: `style`, `option` (default `B`), `view` (`single` / `grid` / `all`), `size`, `theme`, `scenario`,
`currency` (`USD` / `TWD` / `CNY` / `JPY`), `metrics=intel,think,cost`, `noreasoning=1` (思考 reads 0), `subs=all` (every source marked as a subscription), `steps=N` (settled frame), `fx=1`
(keep effects), `shot=1` (boards only).
