# Agent Speed — Design Specification

The product should feel like a precision racing instrument that comes alive as your agents speed up. Calm and
quiet when idle, electric when cruising, and on fire at the redline. Every effect must be driven by real data, stay
smooth (60 fps, no dropped frames), and never cost noticeable CPU when idle.

Both platforms share this language. Platform idioms win for chrome (menus, settings controls, window behaviour);
this document wins for the orb, the bar widget and all speed visuals.

## 0. Appearance and language

- **Appearance** setting: Auto (default, follows the system and switches live), Light, Dark. It applies to every
  surface: orb, dashboard, settings, and the bar widget text. On Windows, Auto follows the app theme
  (`AppsUseLightTheme`) for windows and the taskbar theme (`SystemUsesLightTheme`) for the taskbar widget.
- **Language** setting: System (default), 繁體中文, 简体中文, English, 日本語. Switching applies live, no restart.
  Fallback: zh-HK/zh-MO/zh-TW → zh-Hant, other zh → zh-Hans, ja → ja, everything else → en.

## 1. Speed → heat

`R` = redline speed of the selected metric. Output speed (default metric): setting default **2000 tok/s** (the
slider's maximum), range 50–2000, step 50. Total throughput (includes cached input, typically ~1000× larger): its own
setting, default **2M tok/s** (maximum), range 20k–2M. Settings migration: a stored redline equal to the old
default (400 tok/s, 200 000 tok/s) counts as never set and moves to the new default once; any other stored value is
kept. The redline slider edits the value of the current metric. `v` = live value of the
selected metric.

- Heat `h = min(1, (v / R) ^ 0.6)`.
- Overdrive `o = clamp((v - R) / R, 0, 1)` — extra intensity beyond the redline.
- Both are smoothed with a critically damped spring (settle ≈ 0.6 s) before driving visuals.

### Effect tiers (hysteresis: a tier is entered at its threshold and left at 90 % of it; a tier must hold 0.8 s to take effect)

Tiers drive the effects below (sparks, embers, shimmer, tick dots). They are not shown as text; the visible label is
the speed title.

| Tier | zh-Hant | zh-Hans | English | 日本語 | Enter at |
| --- | --- | --- | --- | --- | --- |
| idle | 待機 | 待机 | IDLE | 待機 | `v < 0.5` for 3 s |
| cruise | 巡航 | 巡航 | CRUISE | 巡航 | `v ≥ 0.5` |
| boost | 加速 | 加速 | BOOST | 加速 | `v ≥ 0.15 R` |
| overdrive | 狂飆 | 狂飙 | OVERDRIVE | 爆速 | `v ≥ 0.375 R` |
| redline | 極限 | 极限 | REDLINE | 限界 | `v ≥ 0.75 R` |

### Speed titles (the visible label)

The label under the value (orb, dashboard hero, accessibility) is the speed title, colored with the heat color: one of
**13 levels**, the rows of this table from top to bottom, named by the selected **title theme** (below). The default
theme, Vehicles, names them with this ladder. Thresholds scale with `R` (shown values are for R = 400 tok/s; the
default is 2000, where each is five times higher), so every metric and redline
setting reads the same. Same hysteresis and 0.8 s hold as the effect tiers. Idle shows the theme's idle label
(Vehicles: `tier.idle`, 待機).

| Key | zh-Hant | zh-Hans | English | 日本語 | Enter at | at R = 400 |
| --- | --- | --- | --- | --- | --- | --- |
| `title.walk` | 步行 | 步行 | WALKING | 徒歩 | `v ≥ 0.5` | 0.5 |
| `title.bicycle` | 自行車 | 自行车 | BICYCLE | 自転車 | `0.02 R` | 8 |
| `title.ebike` | 電動自行車 | 电动车 | E-BIKE | 電動自転車 | `0.03 R` | 12 |
| `title.motorcycle` | 摩托車 | 摩托车 | MOTORCYCLE | バイク | `0.04 R` | 16 |
| `title.car` | 汽車 | 汽车 | CAR | 自動車 | `0.06 R` | 24 |
| `title.sportsCar` | 跑車 | 跑车 | SPORTS CAR | スポーツカー | `0.08 R` | 32 |
| `title.bulletTrain` | 高鐵 | 高铁 | BULLET TRAIN | 新幹線 | `0.12 R` | 48 |
| `title.raceCar` | 賽車 | 赛车 | RACE CAR | レースカー | `0.17 R` | 68 |
| `title.maglev` | 磁浮列車 | 磁悬浮列车 | MAGLEV | リニア | `0.24 R` | 96 |
| `title.jetliner` | 噴射客機 | 喷气客机 | JETLINER | ジェット機 | `0.34 R` | 136 |
| `title.fighterJet` | 戰鬥機 | 战斗机 | FIGHTER JET | 戦闘機 | `0.5 R` | 200 |
| `title.hypersonic` | 極音速飛機 | 高超音速飞机 | HYPERSONIC | 極超音速機 | `0.7 R` | 280 |
| `title.rocket` | 火箭 | 火箭 | ROCKET | ロケット | `1.0 R` | 400 |

A title change slides the old label up/out and the new one up/in (0.35 s); the gauge flash is reserved for effect-tier
changes, so climbing the ladder feels lively but never noisy. A title that does not fit the label width first tightens
its tracking (linearly, down to 0), then shrinks (down to 80 % of the label size, never below the 7 pt floor of 2a);
if it still does not fit it is drawn centered past the label width. It is never truncated (Title length, below).

### Title themes

A title theme names the 13 levels and the idle state. Nine presets ship; the user can add up to **eight custom sets**
next to them. The theme is chosen in Settings → General (section 5) and applies to every surface; a floating orb may
override it in its Advanced settings (2b).

- **One ladder, many names.** Every theme has exactly 13 titles on the thresholds above plus one idle label. The title
  tracker (hysteresis, 0.8 s hold, per orb with its redline) works on the level (0 = idle, 1–13) and never looks at the
  theme. Changing the theme never changes a level, a threshold, heat or an effect tier: the label crossfades (0.25 s)
  to the new name without any level-up effect, and the dashboard hero crossfades between vehicle and title badge
  (0.3 s; Reduce Motion: cut).
- **Ids and keys.** Preset ids are fixed words; custom sets use a UUID. Strings: `titles.<id>.name`, the idle label
  `titles.<id>.idle` and the titles `titles.<id>.1` … `titles.<id>.13`. Vehicles keeps its `title.*` keys (the rows
  of the table above) and `tier.idle`. An unknown id (a newer version, a deleted set) falls back as described under
  Custom title sets; with nothing else to fall back to it is `vehicles`.

| Picker order | Id | Name (zh-Hant · en) | Hero center (4c) |
| --- | --- | --- | --- |
| 1 (default) | `vehicles` | 交通工具 · Vehicles | the vehicle |
| 2 | `animals` | 動物 · Animals | title badge |
| 3 | `wind` | 蒲福風級 · Beaufort Scale | title badge |
| 4 | `space` | 宇宙 · Cosmos | title badge |
| 5 | `cultivation` | 修真境界 · Cultivation | title badge |
| 6 | `wuxia` | 武學境界 · Martial Arts | title badge |
| 7 | `mage` | 魔法位階 · Arcane Ranks | title badge |
| 8 | `gameRank` | 遊戲段位 · Game Ranks | title badge |
| 9 | `engineer` | 工程師職涯 · Engineer Career | title badge |
| then | custom sets, in creation order | their own names | vehicle when based on `vehicles`, else badge |

The physical themes come first (how fast things move), then the ranks (how far someone has risen). The ladders
(zh-Hant; zh-Hans and 日本語 are in `spec/strings.json`):

| Level | Enter at | R = 2000 | `animals` | `wind` | `space` | `cultivation` | `wuxia` | `mage` | `gameRank` | `engineer` |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| idle | `v < 0.5` for 3 s | – | 打盹 | 風平浪靜 | 休眠 | 閉關 | 打坐 | 冥想 | 掛機 | 下班 |
| 1 | `v ≥ 0.5` | 0.5 | 蝸牛 | 無風 | 星塵 | 凡人 | 初學乍練 | 學徒 | 菜鳥 | 實習生 |
| 2 | `0.02 R` | 40 | 烏龜 | 軟風 | 流星 | 練氣 | 粗通皮毛 | 見習法師 | 黑鐵 | 初級工程師 |
| 3 | `0.03 R` | 60 | 刺蝟 | 輕風 | 彗星 | 築基 | 略知一二 | 法師 | 青銅 | 工程師 |
| 4 | `0.04 R` | 80 | 老鼠 | 微風 | 月球 | 金丹 | 漸入佳境 | 大法師 | 白銀 | 資深工程師 |
| 5 | `0.06 R` | 120 | 松鼠 | 和風 | 行星 | 元嬰 | 略有小成 | 魔導師 | 黃金 | 技術主管 |
| 6 | `0.08 R` | 160 | 貓 | 清風 | 恆星 | 化神 | 駕輕就熟 | 大魔導師 | 白金 | 主任工程師 |
| 7 | `0.12 R` | 240 | 兔子 | 強風 | 紅巨星 | 煉虛 | 融會貫通 | 賢者 | 翡翠 | 架構師 |
| 8 | `0.17 R` | 340 | 袋鼠 | 疾風 | 超新星 | 合體 | 爐火純青 | 大賢者 | 鑽石 | 首席工程師 |
| 9 | `0.24 R` | 480 | 馬 | 大風 | 中子星 | 大乘 | 出類拔萃 | 傳奇 | 大師 | 技術總監 |
| 10 | `0.34 R` | 680 | 獵豹 | 烈風 | 黑洞 | 渡劫 | 出神入化 | 半神 | 宗師 | 技術副總 |
| 11 | `0.5 R` | 1000 | 蝙蝠 | 狂風 | 星系 | 真仙 | 登峰造極 | 神 | 王者 | CTO |
| 12 | `0.7 R` | 1400 | 金鵰 | 暴風 | 類星體 | 金仙 | 一代宗師 | 主神 | 傳奇 | 傳奇工程師 |
| 13 | `1.0 R` | 2000 | 遊隼 | 颶風 | 大爆炸 | 道祖 | 天下無雙 | 創世神 | 榜首 | 10x 工程師 |

English:

| Level | `animals` | `wind` | `space` | `cultivation` | `wuxia` | `mage` | `gameRank` | `engineer` |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| idle | NAPPING | BECALMED | DORMANT | SECLUSION | AT REST | MEDITATING | AFK | OFF DUTY |
| 1 | SNAIL | FORCE 0 | STARDUST | MORTAL | NOVICE | NOVICE | ROOKIE | INTERN |
| 2 | TORTOISE | FORCE 1 | METEOR | QI REFINING | DABBLER | APPRENTICE | IRON | JUNIOR |
| 3 | HEDGEHOG | FORCE 2 | COMET | FOUNDATION | LEARNER | MAGE | BRONZE | ENGINEER |
| 4 | MOUSE | FORCE 3 | MOON | GOLD CORE | PROMISING | HIGH MAGE | SILVER | SENIOR |
| 5 | SQUIRREL | FORCE 4 | PLANET | NASCENT | CAPABLE | MAGUS | GOLD | TECH LEAD |
| 6 | CAT | FORCE 5 | STAR | SPIRIT | SKILLED | ARCHMAGE | PLATINUM | STAFF |
| 7 | RABBIT | FORCE 6 | RED GIANT | VOID | ADEPT | SAGE | EMERALD | ARCHITECT |
| 8 | KANGAROO | FORCE 7 | SUPERNOVA | INTEGRATION | EXPERT | GREAT SAGE | DIAMOND | PRINCIPAL |
| 9 | HORSE | FORCE 8 | PULSAR | MAHAYANA | ELITE | LEGEND | MASTER | DIRECTOR |
| 10 | CHEETAH | FORCE 9 | BLACK HOLE | TRIBULATION | VIRTUOSO | DEMIGOD | ELITE | VP ENG |
| 11 | BAT | FORCE 10 | GALAXY | IMMORTAL | PARAGON | DEITY | CHAMPION | CTO |
| 12 | EAGLE | FORCE 11 | QUASAR | CELESTIAL | LEGEND | HIGH GOD | LEGEND | LEGEND |
| 13 | FALCON | FORCE 12 | BIG BANG | DAO LORD | PEERLESS | CREATOR | #1 | 10X DEV |

Notes on the presets:

- `animals` is ordered by commonly cited top speeds: snail 0.05 km/h, tortoise 0.3, hedgehog ≈ 7, mouse 13, squirrel
  32, cat 48, rabbit 56, kangaroo 71, horse 88 (quarter horse), cheetah ≈ 110, Brazilian free-tailed bat 160 (level
  flight), golden eagle 240–320 (dive), peregrine falcon 389 (dive).
- `wind` is the Beaufort scale, force 0–12, with each language's official names (Taiwan CWA, China GB/T 28591 — force
  5 is 清劲风 —, Japan JMA 静穏 … 颶風); English uses `FORCE n`, because the WMO names (up to MODERATE BREEZE) do not fit
  the readout. 無風 is a level here, so the idle label is 風平浪靜 / BECALMED / 凪.
- Localized ladders are ladders in their own right: only the level order must match. 日本語 `wuxia` uses the
  traditional budō license grades (入門 · 見習い · 初伝 · 中伝 · 奥伝 · 免許皆伝 · 師範代 · 師範 · 達人 · 名人 · 宗師 · 武聖 ·
  天下無双), `cultivation` keeps the Chinese stage names in shinjitai, the English ladders use short words of the field
  (QI REFINING, GOLD CORE, NASCENT …), and `space` level 9 is PULSAR in English (a neutron star) because NEUTRON STAR
  does not fit.
- `gameRank` uses generic rank words only (no league or trademarked rank names).

**Rules for a good ladder** (every preset; custom sets are the user's to name, held only to the text rules below):

1. Monotonic: every title is unmistakably above the previous one for someone who knows the field (measured speed,
   an official scale, the canonical order of stages or careers). Level 1 is the humblest active state, level 13 the
   field's summit.
2. Recognisable, plain words of the field; no trademarks.
3. Unique inside the theme, and the idle label is never one of the 13: it comes from the theme's world but must
   plainly mean "at rest" (打盹, 下班, 掛機, AFK …) and is drawn in the secondary color like `tier.idle`.
4. Short: within the title length budget in every language (below); a localized rendering may differ from a literal
   translation to stay short and natural.
5. One line, no trailing punctuation; English in uppercase (the readout uppercases Latin anyway).

**Title length.** `w7` = the width of a title in the readout's title style (system font, semibold, uppercase for
Latin) at the 7 pt floor with its tracking tightened to 0 — the narrowest the readout can set it without going below
7 pt.

| Budget | `w7` | Guarantee |
| --- | --- | --- |
| Comfortable | ≤ 44.2 pt | fits every style at every size from 96 pt (`minimal` from 72 pt) |
| Maximum | ≤ 51.5 pt | fits every style from the Small size preset (112 pt) up; on smaller orbs it may run past the label width, centered, never truncated |

Up to six CJK characters are comfortable (42 pt) and seven reach the maximum (49 pt); in English about eight letters
are comfortable and ten to eleven reach the maximum. Measured on macOS (SF Pro, PingFang, Hiragino Sans), every
preset title of the eight new themes is within the maximum in all four languages and most are comfortable; between the
two: English QI REFINING, FOUNDATION, INTEGRATION, TRIBULATION, SUPERNOVA, BLACK HOLE, APPRENTICE, GREAT SAGE,
MEDITATING and 日本語 ブラックホール, 10xエンジニア. Vehicles predates the budget and keeps its names: English
MOTORCYCLE (52.2 pt) and BULLET TRAIN (53.4 pt) exceed the maximum, SPORTS CAR, FIGHTER JET and HYPERSONIC the
comfortable budget. Each platform has a unit test that measures every preset title in every language with its own
fonts (Windows: Segoe UI Variable, Microsoft JhengHei UI, Microsoft YaHei UI, Yu Gothic UI, semibold) and fails above
the maximum, except those two Vehicles titles.

**Where the theme applies.**

| Surface | Theme |
| --- | --- |
| Floating orb: title label, idle label, accessibility label (`a11y.orb`, `a11y.orb.named`) | the orb's own theme (2b), else General |
| Settings → 懸浮窗: style preview cards, the Advanced redline title list | the selected orb's theme |
| Dashboard hero: title text, hero vehicle or title badge, its accessibility | General |
| Settings → General: the redline title list and its heat-strip ticks, the theme picker | General (the selected theme) |
| Menu bar / taskbar widget, quick menu, Usage History, CSV export | none (they show no titles) |

`heroLastTitle` (4c) stays a vehicle key and is used only while the hero shows vehicles. Titles are not stored in
history.

### Custom title sets

A custom set is a named copy of a theme whose titles the user can rename, one field per level plus the idle label.
Presets and custom sets live side by side in the picker; a custom set is used exactly like a preset.

- **Creating**: `titles.duplicateEdit` on any card (preset or custom) or the `titles.custom.new` card, which copies the
  selected theme. The copy is selected at once and its editor opens with the name field focused. Its **base** is the
  preset it came from (a copy of a custom set inherits that set's base and texts), so a set is always one step from a
  preset. Name: `titles.custom.copyName` with the source's name in the current language (`修真境界（自訂）`), plus ` 2`,
  ` 3` … when taken; from then on the name is literal text (1–32 characters, trimmed; committing an empty name keeps
  the previous one). Up to 8 custom sets (`titles.custom.limit`).
- **Fields**: 13 titles and the idle label. An **empty field** shows and uses the base preset's title in the current
  language, drawn as the field's placeholder, and keeps following the language. A filled field is literal text, the
  same in every language; only the readout's case rule applies to it (Latin uppercase on the orb and the badge when the
  app language is English; the editor, the redline title list and accessibility use the text as typed).
- **Text rules**, applied on commit (Return, focus leaving the field, or 0.8 s without typing; never during an IME
  composition): Unicode NFC; control characters (Cc), line and paragraph separators and bidi controls (U+202A–202E,
  U+2066–2069) removed; tabs, line breaks and other spaces (U+3000 included) become one ASCII space; runs of spaces
  collapse; trimmed. Emoji with emoji presentation are removed and `titles.custom.noEmoji` shows (titles are drawn in
  the heat color and color glyphs cannot be tinted); text symbols such as ★ ✦ ∞ stay. At most 24 grapheme clusters.
- **Length**: a custom title must fit the maximum budget, measured in the title style of the current app language
  (uppercased for English). Text beyond it is cut at the last whole grapheme cluster that fits; `titles.custom.tooLong`
  shows for 4 s and is announced. The caption `titles.custom.lengthHint` states the limit in words. After a language
  change a stored title may exceed the budget (uppercase is wider); it then follows the fitting order of section 1.
- **Base**: the editor's `titles.custom.base` popup changes the base; filled fields stay, empty ones follow the new base
  at once.
- **Reset**: a filled row's reset button (`titles.custom.reset`) empties it (field undo restores it);
  `titles.custom.resetAll` empties all 14 fields with an inline banner `titles.custom.didReset` · `orbs.undo` for 8 s.
- **Duplicate**: as Creating. **Delete** (card menu, ⌫ / Delete on a focused card, or the editor's footer): no
  confirmation; an inline banner `titles.custom.didDelete` · `orbs.undo` for 8 s. Wherever the set was used (General,
  orb overrides) its base preset takes its place; undo restores the set with the same id, its position and every
  reference.
- **Hero**: a set based on `vehicles` keeps the vehicle drawing (its levels still mean those vehicles; caption
  `titles.custom.vehicleArt`); every other set shows the title badge.
- **Text atlas**: the orb's glyph atlas (2a) holds the titles of every theme in use (presets per language, custom text
  as typed), rebuilt when a set changes.
- **Persistence**: one JSON value in the settings store (key `titleThemes`), written atomically on every commit:

```json
{
  "version": 1,
  "selected": "cultivation",
  "custom": [
    {
      "id": "3E0B6C1A-…",
      "name": "我的修仙路",
      "base": "cultivation",
      "idle": "",
      "titles": ["", "", "", "結丹", "", "", "", "", "", "", "", "飛升", ""]
    }
  ]
}
```

| Field | Rule |
| --- | --- |
| `selected` | A preset id or a custom `id`; default and fallback `vehicles`. Absent key = fresh install (no migration needed). |
| `custom[].id` | UUID, never reused. |
| `custom[].base` | A preset id; unknown → `vehicles`. |
| `custom[].titles` | Exactly 13 strings, level 1 first (padded with `""` or cut on load); `""` = the base's title. |
| `custom[].idle` | `""` = the base's idle label. |

Stored text is re-normalized on load (text rules, not the length cut). Import and export are not provided.

### Heat palette (interpolate in OKLab or linear RGB between stops)

| h | Color | Note |
| --- | --- | --- |
| 0.00 | `#7C8798` | slate, idle |
| 0.10 | `#2DD4BF` | teal |
| 0.30 | `#38BDF8` | sky |
| 0.48 | `#818CF8` | indigo |
| 0.64 | `#E879F9` | fuchsia |
| 0.78 | `#FB7185` | rose |
| 0.90 | `#FB923C` | orange |
| 1.00 | `#FACC15` | gold |

White-hot core for highlights: `#FFF7D6`. With overdrive `o > 0`, highlights blend toward the white-hot core by `o`.

### Source colors

| Source | Color |
| --- | --- |
| Codex | `#34D399` mint |
| Claude Code | `#FF8A65` coral |
| Claude Desktop | `#B39DFF` lilac |
| DeepSeek | `#4D6BFE` DeepSeek blue |
| OpenCode | `#F4B13E` amber |
| Gemini CLI | `#34B8F0` sky |
| omp | `#84CC16` lime |
| Antigravity | `#EC4899` pink |
| WorkBuddy | `#11A68B` jade |
| WorkBuddy AI | `#007AB4` ocean |
| ZCode | `#466BFF` cobalt |
| Kimi Code CLI | `#E16F8A` rose |
| Grok Build | `#8290A6` steel |
| Pi | `#A855F7` violet |
| OpenClaw | `#DE2E1F` lobster red |
| Hermes Agent | `#B8860B` dark goldenrod |
| Factory Droid | `#9E5F46` rust copper |

Pink is `#EC4899`, not the lighter `#F472B6` (too close to coral and lilac); lime is `#84CC16`, not the lighter `#A3E635`, which nearly vanishes on light backgrounds.

Pi takes violet `#A855F7`: Pi's own mark is monochrome, and violet is at least 0.13 (OKLab) from every other source
color (closest: DeepSeek blue 0.139, ZCode cobalt 0.146, lilac 0.18), while the colors near omp's lime, its fork, are
taken by Codex, WorkBuddy and omp itself.

OpenClaw takes lobster red `#DE2E1F` after its lobster mark. Red is the one hue no source uses yet; this red is at least
0.13 (OKLab) from every other source color and from the slate `#7C8798` (closest: Kimi rose 0.138, Antigravity pink
0.147, Claude Code coral 0.182; brighter reds such as `#EF4444` come within 0.10 of rose and pink) and reads on both
appearances (contrast against `#1E1E1E`: 3.6; against white: 4.7). Like every source it has no icon and no short
name: its dot and `source.openclaw` "OpenClaw", in source order (METRICS 1) after Pi.

Hermes Agent takes dark goldenrod `#B8860B`, a darker member of the gold of Nous Research's Hermes site (`#FFD700`,
light theme `#8B6508`). The bright gold is 0.10 (OKLab) from OpenCode's amber; dark goldenrod is at least 0.14 from
every other source color, including OpenClaw's `#DE2E1F` (0.18), and from the slate `#7C8798` (closest: Claude Code
coral 0.148, amber 0.153, slate 0.164) and reads on both appearances (contrast against `#1E1E1E`: 5.1; against white:
3.3). Like every source it has no icon and no short name: its dot and `source.hermes` "Hermes Agent", in source order
(METRICS 1) after OpenClaw.

Factory Droid takes rust copper `#9E5F46`, a muted, industrial copper for Factory. The warm side of the wheel is
crowded (coral, amber, rose, lobster red, dark goldenrod), so it keeps their hue family but drops chroma and lightness
(OKLCH L 0.55, C 0.09, h 42°): it is at least 0.13 (OKLab) from every other source color and from the slate `#7C8798`
(closest: OpenClaw's lobster red 0.131, Hermes Agent's dark goldenrod 0.132, slate 0.135, Grok Build's steel 0.157)
and reads on both appearances (contrast against `#1E1E1E`: 3.3; against white: 5.0). Like every source it has no icon
and no short name: its dot and `source.droid` "Factory Droid", in source order (METRICS 1) after Hermes Agent.

The two WorkBuddy editions share one app icon, teal-green (`#0EC8A8` → `#05C890`), which is nearly Codex mint (OKLab
distance about 0.04; the closest existing pair, coral and amber, is 0.11 apart). So both take a darker member of that teal family,
which keeps them related to each other and to the brand: jade at the brand's hue for WorkBuddy, and ocean, bluer
and darker, for WorkBuddy AI. Each is at least 0.12 from every other source color, from the other edition and from
the slate `#7C8798` of idle and "other" values (history), and reads on both appearances (contrast against `#1E1E1E`:
5.4 and 3.5; against white: 3.1 and 4.7). They follow every
rule of the other colors (bezel, dots, chips, history bars, light-mode darkening). As for every source, there is no
separate light or dark tint, no icon and no short name: a source is its colour dot and its name (`source.workbuddy`
"WorkBuddy", `source.workbuddyAi` "WorkBuddy AI", the apps' own names in every language), in source order (METRICS 1)
after Antigravity, the China edition first.

## 2. The orb (floating window)

A circular smoked-glass instrument. This section defines the shared frame and the default style, `led`; section 2a adds
four more styles and section 2b lets the user float several orbs, each tracking its own sources. Diameter `S` (per orb,
96–240 pt/DIP, `minimal` 64–240, default 148). The window is `1.5 S` square so the aura and sparks can leave the disc; everything outside the disc is fully transparent and ignores clicks wherever
the platform allows it. The orb follows the Appearance setting. The dark variant is described inline below; the
light variant ("porcelain") changes only these tokens:

| Token | Dark | Light |
| --- | --- | --- |
| Body gradient | `#1B2030 → #090B10` at 88 % | `#FFFFFF → #E7EBF3` at 94 % |
| Blur material | macOS `.hudWindow` | macOS `.popover` (light) |
| Rim | white 14 % top → 0 % | white 95 % top highlight + 1 px outer stroke black 8 % |
| Drop shadow | black 45 %, y `0.03 S`, blur `0.10 S` | black 16 %, y `0.04 S`, blur `0.12 S` |
| Unlit segments | white 7 % | black 8 % |
| Value text | white 96 % | `#0B0D12` 92 % |
| Unit / secondary | white 55 % | black 50 % |
| Title label | heat color | heat color darkened 18 % (OKLab L) for ≥ 3:1 contrast |
| Aura opacity | `0.55 * h`, additive | `0.32 * h`, normal blending |
| Sparks / embers | additive, heat → white-hot | normal blending, heat colors, size +20 %, white-hot replaced by `#FFB020` |
| Leading segment | white-hot `#FFF7D6` + bloom | heat color + bloom |

Layers, back to front:

1. **Aura** — radial gradient in the heat color around the disc, radius `0.5 S → 0.75 S`, opacity `0.55 * h`
   (0 at idle). Breathes: period 4 s at cruise → 0.7 s at redline, amplitude ±15 %. Flares on every response pulse.
2. **Body** — circle with a background blur of the desktop (macOS `NSVisualEffectView` `.hudWindow`; Windows: acrylic
   is not available for layered windows, use a solid gradient) overlaid with a vertical gradient `#1B2030 → #090B10`
   at 88 % (multiplied by the opacity setting). A 1 px inner stroke, white 14 % at the top fading to 0 % at the bottom
   (specular rim), and a soft inner shadow. A faint radial "lens" highlight at the top-left (white 6 %).
   - Floating orb: the **silk core** (section 2.1) is drawn here, above the body and below the gauge.
   - Embedded style (dashboard hero) only: the hero vehicle layer (section 4c) is drawn here, above the body and below
     the gauge (no silk core).
3. **Gauge** — 270° arc starting at 135° (bottom-left) going clockwise to 45° (bottom-right), gap at the bottom.
   Radius `0.41 S`, made of **36 LED segments**, each `0.045 S` long radially, with 2.2° gaps. Unlit segments:
   white 7 %. Lit count = `h * 36` (fractional: the last segment is partially lit by brightness). Each lit segment has
   the heat color *of its own position* (segment i uses `h_i = i / 35`), so the arc shows the spectrum as it fills.
   The leading segment is drawn white-hot with a bloom (blurred copy, radius `0.03 S`). Four tick dots just outside
   the arc mark the tier thresholds.
   Redline: lit segments shimmer (random brightness 75–100 %, 12 Hz) and the outer edge of the arc gets a pulsing
   glow.
4. **Comet** — a 40° gradient arc (transparent → heat color → white-hot head) travelling just outside the gauge,
   clockwise. Hidden at idle. Period: 6 s at cruise → 0.7 s at redline (interpolate by `h`). Opacity `0.25 + 0.6 h`.
5. **Sparks** (from boost upward, `h ≥ 0.45`) — emitted from the leading segment and randomly along the lit arc,
   flying outward with a tangential component in the comet direction. Lifetime 0.5–1.2 s, size 1.2–2.8 px, color from
   heat to white-hot, additive blending, slight drag. Emission rate `120/s * ((h - 0.45) / 0.55)`.
   **Embers** at redline: rising flame particles around the upper half of the ring with turbulence and gravity
   pointing up, rate `+90/s * max(o, 0.3)`.
6. **Readout** — centered (floating orb only; the embedded style shows the hero vehicle instead).
   - Value: system font (SF Pro / Segoe UI Variable Display), weight bold, tabular digits, size `0.24 S`, white 96 %.
     Rolling digit transition on change. From boost upward the value gets a heat-colored glow (radius `0.035 S`).
     From overdrive upward the value leans forward: skew `-6° * h` (motion feel). At redline add a 0.6 px jitter
     (≤ 30 Hz) — disabled in "soft" effects and with Reduce Motion.
   - Unit `tok/s` (or the money unit `US$/s`, per orb, section 9): `0.072 S`, medium, white 55 %, tracking +0.5.
   - Title label (speed title, section 1): `0.062 S`, semibold, uppercase for Latin, tracking +1.6, in the heat
     color, below the unit. A title change slides the old label up/out and the new one up/in (0.35 s); an effect-tier
     change also flashes the gauge (+25 % brightness, 0.4 s).
   - Agent dots: in the gauge gap at the bottom, one 4 px dot per generating agent colored by source (max 6, then
     `+n`), gently pulsing while generating.
7. **Pulse** — on each `responseCompleted`: a shockwave ring from `0.5 S` to `0.74 S`, stroke 2 px → 0, opacity
   0.6 → 0 over 0.7 s ease-out, color = heat of that response's speed. Strength (opacity/width) scales with
   `log10(output)`. Coalesce: at most one wave per 150 ms.
8. **New peak** — on `newPeak`: 24 gold rays burst from the disc edge, the gauge sweeps 0 → current in 0.5 s, and a
   pill label `新紀錄 NEW PEAK` (localized) springs in above the value for 1.8 s.

Idle state: value `0` in white 60 %, the title theme's idle label (`待機` for Vehicles, section 1), gauge dim, comet
hidden, and every 6 s a slow light sweep runs along the unlit segments as a sign of life. Optional "fade when idle"
(setting): after 60 s idle, fade the window to 45 % opacity; restore instantly on activity or hover.

Hover: the orb scales to 1.03 (spring), the title label crossfades to a compact info line
(`今日 1.2M · 3 個 agent`, counted over the orb's sources), and the cursor shows it is draggable. An orb tracking a
subset of sources also shows its source chip (2b).

Interaction:
- Drag anywhere on the disc to move. On release within 24 pt of a screen edge, snap to a 12 pt margin (spring); other
  orbs attract it magnetically (2b). Position (per orb, per display arrangement, 2b) persists.
- Double-click: open the dashboard.
- Right-click: the orb's context menu (2b) — style, sources, size (S / M / L presets 112 / 148 / 196), effects (Off /
  Soft / Standard / Max), always on top, click-through, add / duplicate / split / merge, hide, remove, settings, quit.
- Click-through mode (per orb): the orb ignores all mouse events; it is controlled from the menu bar / taskbar.

Layers 1–8 above are the v1.3 base; section 2.1 adds the silk core and the effects that build on these layers, and its
effects-level table replaces the one that used to stand here. Effects intensity is a setting, default Standard.
System "Reduce Motion" caps the level at Soft and replaces movement with crossfades. Every other style has its own row
set in 2a (their "as `led`" rows keep the v1.3 behaviour: Off static; Soft aura, comet and pulses; Standard adds sparks,
embers and skew; Max 1.6× particle rates and stronger bloom).

Performance: the frame policy and budget of 2a apply to every style, `led` included (idle is static apart from the 6 s
sweep, active is capped at 60 fps, never the display rate). Particles use a fixed-size pool (≤ 640). The renderer
pauses entirely while the orb is hidden or fully occluded.

## 2.1 `led` effects: silk core, corona and lightning (v1.4)

The look was tuned in the prototype `spec/prototypes/led-effects.html` (open it in a browser: 新版 is this section,
目前版本 the v1.3 look; `?v=<tok/s>&t=<seconds>[&look=light][&peak=<seconds>][&level=1.6][&zap=1]` renders a still
frame). The prototype's fragment shader and its particle, arc and lightning code are the **normative reference** for
shapes, constants and colors: port those functions instead of re-deriving them; this section names every element, its
parameters, and what differs in the apps. Prototype coordinates are orb units with the rim at r = 0.5 and y up:
`(x, y) = ((X − c.x) / S, (c.y − Y) / S)`. `lvl` = 1 at Standard, 1.6 at Max. `act = smoothstep(0, 0.08, h)`. The
prototype draws on a 1.9 S canvas; the apps keep the 1.5 S window, so everything outside the disc fades to zero by
**0.74 S** from `c` (constants that reach further in the prototype are clamped as stated below).

Speed phases (accumulated, 4c resolved detail 3), advanced only on rendered frames with `dt ≤ 1/15 s` so a static orb
never jumps when it renders again:

| Phase | Rate per second | Drives |
| --- | --- | --- |
| `φsilk` | `0.12 + 1.4 h + 2.2 o` | silk folds |
| `φlight` | `0.05 + 0.5 h + 1.2 o` | silk light azimuth and glide |
| `φflow` | `0.35 + 1.9 h + 1.6 o` | gauge energy flow |
| `φwarp` | `0.2 + 1.1 h + 1.2 o` | warp streaks |
| `φcorona` | `0.5 + 1.7 h + 1.8 o` | corona |

Elements, back to front (layer numbers of section 2):

- **Silk core** (layer 2, floating orb only; prototype `silkCore`, `silk_height`). A satin height field of long
  diagonal folds — three folded sine bands along the 37° diagonal, gently fanned and warped by one simplex noise —
  shaded by a key light and a rim light whose colors are the heat palette around `h`. Four height samples per pixel
  (four noise evaluations), normals by finite differences.
  - Dark: near-black satin (`#020304` at 70 %) whose body light, sheen, glints and rim light glow additively: at idle a
    few faint silver glints; cruise teal; 700 tok/s indigo; extreme orange-gold to rose; past the redline the sheen
    turns white-gold and fine ripple packets run across the cloth (`rip = min(1, 2.5 o)`).
  - Light: pearl-white silk (`#FCFBF9` at 55 %) with grey fold shadows, white glints and a soft heat-colored sheen.
  - Fades from 0.385 S to 0.475 S radius. Behind the readout (rounded boxes around the value and around unit + title)
    it drops to 30 %.
  - Brightness `(0.3 + 0.65 act + 0.3 h + 0.25 o) · (1 + 0.35 (lvl − 1))`, × (1 + 0.3 tier flash + 0.5 wave).
  - Response pulse: the cloth is displaced along the wave ring and brightens 50 % under it. New peak: a gold band
    (`#FFD673`) sweeps diagonally across the silk in 1.2 s.
- **Gauge energy flow** (layer 3): bright pulses run along the lit segments toward the leading one: segment `i` gets
  `flow = (1 − fract(2.2 i / 36 − φflow))^9 · act · (0.5 + 0.9 h)`; its color moves `0.75 flow` toward white-hot (light:
  ×1.15) and its brightness × `(1 + 0.9 flow)`. The outer glow of the lit arc is 30 % stronger than v1.3.
- **Tip flare** (layer 3, from `h ≥ 0.5`): a four-pointed star on the leading segment — two crossed Gaussian lines,
  tangential `exp(−|t| / (0.05 + 0.16 o)) · exp(−|n| / 0.0032)` plus radial `exp(−|n| / (0.035 + 0.09 o)) ·
  exp(−|t| / 0.0028)` — at `smoothstep(0.5, 0.85, h) · (0.55 + 0.9 o) · lvl`, white-hot at 90 % (light: heat at 40 %).
- **Comets** (layer 4): tail `40° + 60° h`; from `h > 0.55` a second comet half a turn behind at 70 %; past the redline
  the period shortens by a further `1 + 0.6 o`.
- **Corona** (outside the rim, from `h ≥ 0.45`; prototype "corona"): flame tongues licking off the rim — ridged simplex
  noise rising outward (`rise = 9 (r − 0.5) − φcorona`), reach `0.02 + (0.06 h + 0.12 o) · 1.7 · body^1.4` with a slow
  body noise, variable tongues `mix(0.12, 1.25, tongues)`, and a thin bright base hugging the rim; white-hot at the base
  to the heat color at the tips (light: heat, ink-blended at 95 %). Amount `smoothstep(0.45, 0.85, h) · (0.45 + 0.55 o)
  · lvl`. Only in the annulus 0.47–0.74 S, faded by `smoothstep(0.74, 0.64, r)` (the prototype fades by 0.81); three
  noise evaluations per pixel.
- **Warp streaks** (outside the rim, from `h ≥ 0.32`): thin lines of light rushing into the orb. 96 angular bins; a bin
  is active when its hash < `0.18 + 0.42 h`; its head travels from **0.74 S** (prototype 0.9) to 0.51 S at
  `φwarp · (0.55 + 0.9 seed)`, length `(0.02 + 0.08 h + 0.07 o)(0.55 + seed)` S, width `0.0018 + 0.001 h` S, fading in
  over the first 30 % of its travel and out over the last 18 %; color `mix(heat(h), #FFF7D6, 0.45)` at 80 % (light:
  heat at 75 %). Amount `smoothstep(0.32, 0.62, h) · lvl`. No noise; one hash per pixel.
- **Sparks** (layer 5): emission × 1.6; speed `(0.3–0.9) S/s · (1 + 0.8 o) · 1.3` outward plus a tangential
  `(−0.35…0.65) · 0.6 S/s · 1.3`. Each spark is a streak from its position back along its velocity for
  `clamp(0.06 · v / (0.12 S), 0.02, 0.06)` s, fading to transparent at the tail, width 0.75 × size (light 1 ×), with a
  white-mixed head dot of 0.55 × size in dark; alpha × `(0.25 + 0.75 min(1, v / 0.12 S))` so a slowed spark fades
  instead of becoming a dot. Embers × 1.4, size 1.2–3.0 px, radius flickering `0.55 + 0.45 sin(18 t + seed)`, a radial
  glow of 2.2 × radius. Particles fade out by 0.74 S.
- **Electric arcs** (redline tier, Standard and Max): jagged discharges from a random lit point of the arc (0.41 S) out to
  0.50–0.60 S and ±0.45 rad, one every `(0.28 − 0.2 o) / lvl · (0.5…1.5)` s, living 0.09–0.19 s; a 10-point `polyline`
  re-jittered by ±0.0225 S every frame, drawn as three strokes: 0.022 S at 18 % and 0.008 S at 50 % in
  `mix(heat(h), #BFD9FF, 0.5)`, 0.0032 S white at 95 % (light: heat darkened 15 % for all three, no white core).
- **Readout** (layer 6): from boost the glow σ is × `(1 + 0.8 o)`; from overdrive a second glow 2.4× wider at 45 %
  (light 20 %). Dark: whenever `v ≥ 0.5` a black backing under the value (Gaussian σ 0.0125 S at 70 % plus a drop
  shadow 0.006 S down, σ 0.006 S, at 60 %) keeps it legible over the silk sheen. Lean `−7.5° h` from overdrive; redline
  jitter `0.9 + 1.2 o` px. Redline, dark only: a chromatic split — the value drawn twice more under the main glyphs,
  offset `±(0.6 + 1.6 o) · S / 300` pt horizontally, `#FF3C8C` at 55 % to the left and `#3CD2FF` at 55 % to the right.
- **Digit lightning** (Standard and Max; not with Reduce Motion; drawn above the readout): when the odometer changes a
  digit, lightning crackles off it.
  - Per changed digit position: cooldown 0.11 s; chance `min(1, 0.3 + 0.3 s + 0.4 h)`, `s` = the digits to its right;
    power `p = (0.55 + 0.22 s)(0.6 + 0.6 h + 0.6 o) · lvl`. A change of length (999 → 1.00k) changes every digit.
  - Flash: a radial glow over the digit's cell, radius 1.44 × the cell width, peak `min(0.8, 0.35 p)` fading as
    `(1 − t/T)²`, `T = 0.16 + 0.06 p` s; white in dark, `#6B80FF` at half strength in light.
  - Bolts: `1 + [h > 0.55] + [redline tier, 60 %]` per digit, at most 16 alive. A bolt starts on the top or bottom
    edge of the digit (±0.36 cell height, within ±0.29 cell width of its center) and ends — with one uniform roll `u`:
    redline tier and `u < 0.3`: on the LED ring (0.39 S) in the direction of its start; else `u < 0.6` and a
    neighboring digit exists: on the same edge of that digit; else a short crackle `S (0.05 + 0.07 p)` long pointing away from the digit (±90° ± 0.85 rad). Path: midpoint
    displacement, 5 levels, roughness 0.3, × 0.55 per level; one fork (two when `p > 0.9`) from an interior point at
    ±0.95 rad, 0.2–0.5 of the bolt length, 4 levels, roughness 0.35. Re-shaped every 45 ms; life
    `0.14 + 0.1 rand + 0.05 p` s; alpha `min(1, 1.6 (1 − t/T))` with a 22 % chance per frame of dropping to 40 %.
  - Strokes like the arcs with `w = 0.0032 S (0.7 + 0.5 p)`: 5 w at 16 % and 2 w at 45 % in the glow color, 0.8 w at
    95 % in the core color; forks 0.6 w at 80 %. Dark: glow `mix(heat(max(h, 0.3)), #B3D1FF, 0.45)`, core white,
    additive. Light: glow `#6B80FF`, core `#3340D9`, normal blending.
- **Pulse** (layer 7): plus a soft wake ring (`0.5 → 0.66 S`, 3.5× wider, 30 %) and the silk ripple; 0.85 s.
- **New peak** (layer 8): the disc flashes (white-hot, `0.75 e^(−4.5 t)`, full within 0.15 S of `c` and fading to zero
  at 0.6 S; light: `#FFB021` at 35 %); a gold ring `#FFC74D` blows from 0.5 S to 0.74 S in 1.4 s; the 24 rays are 0.014 S wide, turn at
  0.4 rad/s and reach 0.74 S; 160 sparks burst outward from 0.48 S (30 % gold `#FFD159`, 0.5–1.8 S/s, 0.7–1.6 s); plus
  the silk's gold sweep.

Effects levels for `led`:

| Level | Behaviour |
| --- | --- |
| Off | Static: silk (no flow), colors and segments; no aura breathing, comet, particles, pulses, lightning, jitter or skew |
| Soft | Silk at half rate without ripples; aura, one comet, pulses (with the silk ripple); no flow, tip flare, corona, warp streaks, particles, arcs, lightning, jitter or chromatic split |
| Standard | Everything above at `lvl` = 1 |
| Max | `lvl` = 1.6: particle and arc rates × 1.6, corona, warp, flare and lightning amounts × 1.6, stronger bloom |

Reduce Motion caps the level at Soft and freezes the silk. LOD: S < 120 halves the spark, ember and bolt counts;
thumbnails draw the silk static and no corona or warp streaks. Budget: the silk is one disc-sized quad (4 noise per
pixel), corona and warp streaks one annulus quad (≤ 3 noise per pixel); the 2a budgets and frame classes apply
unchanged — the benchmark harness must show it.

## 2a. Orb styles

Every floating orb has a **style**: `led` (the LED gauge of section 2, the default), `needle`, `liquid`, `reactor` or
`minimal`. The styles are one family: they share the disc and window, the body, the aura, the readout, the agent dots,
the response wave, the new-peak pill, hover, the source bezel (2b), the effects levels, accessibility and the frame
policy below; each one adds its own instrument between the body and the readout. The dashboard hero (section 4) always
uses `led` with the hero vehicle.

| Key | Name (`style.*`) | Personality | Primary encoding of `h` |
| --- | --- | --- | --- |
| `led` | LED 儀表 | precise, electric | lit segments of a 270° arc |
| `needle` | 指針轉速錶 | mechanical, physical, racing | needle angle on a 270° dial with a red zone |
| `liquid` | 能量液球 | organic, luminous, alive | liquid level in a glass sphere |
| `reactor` | 反應爐 | sci-fi, rotational energy | arc length and spin of counter-rotating rings |
| `minimal` | 極簡圓環 | quiet, typographic | one thin progress ring |

Conventions for this section: `c` = disc center, `S` = diameter, radii and lengths are fractions of `S`, angles are
degrees clockwise from 3 o'clock in screen space (y down). A *dial position* `n` maps to the angle `135° + 270° n` (the
LED arc). `heat(x)` is the heat palette; the *label color* is the title token (dark: heat; light: heat darkened 18 %
OKLab L, stepped to ≥ 4.5:1 against the body); *hot* is `mix(heat(h), #FFF7D6, 0.55 + 0.45 o)` in dark and
`mix(label color, #FFB020, 0.3)` in light. Glows are Gaussian with standard deviation `σ` (a canvas `shadowBlur` of
`2σ`), in points, independent of the backing scale. "Additive" applies to dark only; light uses normal blending.
`h_raw = min(1, (v / R)^0.6)` and `o_raw` are the unsmoothed values of section 1 (they step with the 500 ms engine tick);
`h`, `o` are the smoothed ones. Every speed-dependent oscillation uses an accumulated phase (4c resolved detail 3).

### Shared foundation

- **Window, body, rim, shadow, Reduce Transparency**: section 2, layer 2, unless a style's table overrides a token.
- **Aura**: section 2, layer 1, for every style except `minimal` (no aura). Its breathing and the idle sweep run on one
  global clock, so several orbs breathe and sweep together.
- **Readout**: section 2, layer 6 — same fonts, weights, tokens, glow (σ 0.035 S), forward lean (`−6° h` from
  overdrive), redline jitter and title slide — placed by the style's frame:

  | Style | Value size · baseline · max width | Unit | Title baseline · size · max width | Agent dots | Pill center |
  | --- | --- | --- | --- | --- | --- |
  | `led` | 0.24 S · c + 0.012 S · 0.62 S | 0.072 S, 0.104 S below the value | 0.09 S below the unit · 0.062 S · 0.58 S | c + 0.3785 S | c − 0.232 S |
  | `needle` | 0.205 S · c + 0.035 S · 0.40 S (inside the hub) | 0.062 S, 0.08 S below | fixed at c + 0.338 S (dial gap under the hub) · 0.062 S · 0.46 S; hover line ≤ 0.42 S at 85 % size | c + 0.405 S | c − 0.175 S |
  | `liquid` | as `led` | as `led` | as `led` | c + 0.3785 S, with a 1.2 pt halo | as `led` |
  | `reactor` | 0.205 S · c + 0.03 S · 0.46 S (core window) | 0.062 S, 0.08 S below | 0.074 S below the unit · 0.058 S · 0.50 S | c + 0.252 S (inside the core) | c − 0.172 S |
  | `minimal` | 0.30 S · c + 0.07 S · 0.66 S | none | 0.098 S below the value · 0.068 S · 0.62 S | none | c − 0.25 S |

  - The value shrinks to its max width. Titles tighten, then shrink to 80 %, and are never truncated (section 1); the
    hover info line shrinks to 70 %. Unit and title never go below 7 pt; below S = 64 (thumbnails, 2b and section 5)
    only the value is drawn.
  - **Odometer**: on a value change only the characters that changed roll, each clipped to its own character cell,
    travel 0.92 × the value size, 0.24 s ease-out (up when rising). Different lengths crossfade in 0.12 s. No roll with
    Off, Reduce Motion, `minimal` and S < 80 (the text simply updates).
  - Readout text is always drawn above every effect. Particles that pass behind it fade to 35 % inside the *readout
    zone* (an ellipse 0.30 S × 0.19 S around `c + (0, 0.07 S)`, Gaussian falloff) so it never competes with them.
- **Readout** (optional, per orb): two small lines above the value (`needle`: inside the hub, below it) with the
  download and upload speed or 性能, the performance score (section 10; values in section 12).
- **Agent dots**: section 2, layer 6, at the style's position; only the orb's own agents (2b). None in `minimal`.
- **Response pulse** (`responseCompleted` of the orb's sources, 2b): the shockwave of section 2, layer 7, for every
  style except `minimal`, plus the style's accent below. Pulses toggle: Settings → Effects → Response pulses.
- **New peak** (per orb, METRICS 6.8): the pill of section 2, layer 8, for every style; the 24 gold rays for every style
  except `minimal`; plus the style's accent below. Toggle: Celebrate new peaks.
- **Tier flash**: an effect-tier change brightens the style's primary lit element by 25 % for 0.4 s.
- **Idle** (tier idle): value `0` in the idle value color, the idle label of the orb's title theme (section 1; Vehicles:
  `tier.idle`) in the secondary color, the instrument at rest.
  **Sign of life**: every 6 s on the global clock (skipping one that would start less than 2 s after entering idle) a
  0.9 s event as listed per style; `minimal` has none.
- **Hover**: scale 1.03 (spring), the title crossfades (0.2 s) to the info line (`today.summary`, counted over the orb's
  sources), plus the source chip of 2b. Unchanged otherwise.
- **Style change**: the outgoing and incoming renderers crossfade for 0.25 s (Reduce Motion: cut); springs, trackers
  and particles live in the orb, not in the style, so the value never jumps.

### 2a.1 `led` — LED gauge

Section 2, layers 1–8, unchanged; it is the reference for the family. Additions: the shared rules above (odometer,
readout zone, text floors), the frame policy and budget below, and LOD: S < 120 halves the spark and ember rates;
thumbnails draw the 36 segments without bloom.

### 2a.2 `needle` — Analog needle tachometer

A classic racing tachometer: graduated dial, red zone, a heavy needle that sweeps from the zero stop to the redline,
trembles at speed and pins against the peg beyond the redline, and shift lights that flash at the redline. The needle
shows `n = h` on the LED's 270° scale; the red zone starts at the redline-tier threshold `n_r = 0.75^0.6 = 0.8415`.

Layers, back to front:

1. **Aura**, **body**: shared.
2. **Dial band**: a ring stroke centered at r 0.41 S, width 0.075 S, over the scale (n −0.004 → 1.004); white 2.8 % /
   black 3.5 %.
3. **Red zone**: arc centered at r 0.4655 S, width 0.017 S, butt caps, n_r → 1; `#F43F5E` at 62 % (dark) / `#E11D48` at
   80 % (light). In the redline tier its opacity pulses 80 → 100 % at `2 + o` Hz; Standard and Max add a glow σ `0.025 S (0.6 + 0.4 o)`
   (× 1.3 at Max) in the same color.
4. **Graduations**: 41 ticks at n = k/40, outer radius 0.445 S, round caps; every 5th is major (9): length 0.062 S,
   width 0.014 S; minor: 0.03 S, 0.0075 S. Unlit: white 50 % / 24 % (major / minor; Increase Contrast × 1.6), light
   black 42 % / 20 %; unlit ticks inside the red zone use the red-zone color at 85 % / 60 %. **Lit** (`n_k ≤` needle,
   not idle): `heat(n_k)` (light: its label color) at 95 %; the last lit tick blends 75 % toward `#FFF7D6` (light: 25 %
   toward `#FFB020`); lit majors glow σ 0.015 S (dark, Soft and up).
5. **Trace**: an arc at r 0.366 S, width 0.011 S, from n = 0 to the needle; each point in `heat(its n)`, opacity rising
   from 30 % at 0 to 100 % at the needle, × 0.70 additive (dark) / × 0.85 (light). Hidden at idle.
6. **Stop pins**: dots of radius 0.011 S at r 0.405 S, n = −0.034 and n = 1.034; white 32 % / black 28 %.
7. **Shift lights** (S ≥ 120): five lamps of radius 0.019 S on r 0.33 S at −110°, −100°, −90°, −80°, −70°, housings
   white 8 % / black 9 %. Lamp i lights when the needle passes n = 0.70, 0.735, 0.77, 0.805, 0.8415, in `heat(that n)`
   (light: darkened 15 %), glow σ 0.02 S (dark). In the redline tier all five flash together at **3 Hz** (opacity
   28 → 100 % through `smoothstep(−0.35, 0.35, sin φ)`, ≈ 50 % duty) in `mix(heat(0.97), #FFF7D6, 0.55)` (light
   `#F59E0B`). This is the product's only blinking element and never exceeds 3 flashes per second.
8. **Wake** (Soft and up, while the needle turns faster than 12°/s): an annular wedge r 0.235 → 0.43 S trailing the
   needle by `4° + 0.085 s × |ω|` (≤ 55°), `heat(h)` from 32 % at the needle to 0, additive.
9. **Needle**: a tapered blade from the counterweight (r −0.09 S, hidden under the hub) through the pivot (width
   0.062 S) to a round tip at r 0.44 S (width 0.012 S). Fill `heat(h)` blended `0.05 + 0.45 o` toward `#FFF7D6`
   (dark) / the label color (light); idle `#9EA8BA` at 75 %. Dark: glow σ 0.0275 S at `0.45 + 0.35 h` (× 1.25 at
   Max). Light: drop shadow black 22 %, offset (0.006, 0.01) S, σ 0.007 S. Spine: a 0.0045 S line from r 0.21 S to the
   tip, `#FFF7D6` 75 % (dark) / white 55 % (light).
10. **Peg sparks** (Standard and up), below.
11. **Hub**: a disc of radius 0.235 S, vertical gradient `#262C3C → #0C0E14` at 95 % (light `#FFFFFF → #ECEFF5` at
    98 %), drop shadow black 55 % / 16 %, σ 0.025 S, y 0.012 S; a chrome bezel stroke 0.013 S with a 135° gradient
    white 42 % → 10 % → 22 % (light: white → `#A0A8B6` 55 % → `#78808E` 60 %).
12. **Readout, agent dots, wave, rays, pill**: shared (frame table).

Motion:

- **Needle dynamics**: target `n* = h_raw` (0 at idle), plus `0.02 + 0.05 o_raw` while `o_raw > 0` so it presses into
  the peg. An underdamped spring, frequency 1.5 Hz, damping ratio 0.55 (≈ 12 % overshoot on a step), integrated in
  sub-steps ≤ 1/480 s. Stops at n = −0.012 and 1.012; hitting one reverses the velocity × −0.32 (a small bounce).
  Off and Reduce Motion: the needle follows the smoothed `h` directly.
- **Tremble** (Standard and Max): amplitude `A = smoothstep(0.55, 1, h) × (0.3° + 1.0° o)` (× 1.4 at Max), offset
  `A (0.5 sin φ1 + 0.3 sin(φ2 + 1.3) + 0.2 sin(φ3 + 2.1))` at 11.3 / 17.9 / 26.1 Hz, added after the spring and
  clamped by the stops: pinned at the peg it reads as the needle chattering against the pin.
- **Peg sparks** (while pinned: needle ≥ 1.0105 and target > 1.012): `25 + 70 o` per s (× 1.6 Max) from the contact
  point at r 0.405 S, speed 0.35–0.9 S/s along the clockwise tangent ±30°, plus 0.2 S/s outward, gravity 1.1 S/s²,
  drag 2/s, life 0.22–0.55 s; streaks (velocity × 0.025 s), width 0.8–1.6 pt, `#FFF7D6` → `heat(0.95)` (light
  `#FFB020` → label color). Pools: 48 (Standard), 80 (Max).
- **Pulse accent**: the tip flares for 0.35 s — glow + 0.0175 S σ and a radial `#FFF7D6` (light `#FFB020`) spot of
  radius 0.06 S at 70 % × strength.
- **New-peak accent — the gauge sweep**: displayed `n + (1.012 − n)(smoothstep(0, 0.38, τ) − smoothstep(0.5, 1.0, τ))`
  for τ ∈ [0, 1] s: the needle swings to the peg and back, like an instrument cluster's start-up sweep; the shift lights
  fire once with it.
- **Title change**: shared slide. **Tier flash**: lit ticks and trace.
- **Idle**: needle on the zero stop, slate; trace and lamps dark. Sign of life: the 6 s sweep brightens the graduations
  as a band `exp(−90 d²)` (+35 % dark / +25 % light) travelling n −0.15 → 1.15 in 0.9 s, in step with `led`.

| Effects | `needle` |
| --- | --- |
| Off | needle on the smoothed `h` (no overshoot or tremble), red zone and lamps steady, no wake, aura breathing or pulses |
| Soft | spring overshoot and stop bounce, wake, shift-light flash, aura breathing, pulses with the tip flare; no tremble or sparks |
| Standard | Soft + tremble, peg sparks, red-zone glow |
| Max | Standard with sparks × 1.6, glows × 1.25, tremble × 1.4 |

Reduce Motion: as Off for motion (lamps steady-lit at the redline instead of flashing), crossfades for titles and the
new peak (pill only). Increase Contrast: ticks × 1.6 opacity, a 1 pt outline around the needle (black 60 % dark /
white light), glows × 0.5. Windows high contrast: ticks, needle and trace in the theme's text color, red zone in its
highlight color, no glow, wake or sparks. LOD: S < 120 drops the minor ticks and the shift lights; thumbnails show dial
band, red zone, majors, needle and value.

### 2a.3 `liquid` — Liquid energy orb

A glass sphere of glowing liquid. The level rises with speed, the surface waves grow, luminous motes stream up, and at
the redline it boils, splashes and steams; beyond it the core turns white-hot.

Layers, back to front (everything from 3 to 9 is clipped to the inner glass, r 0.455 S):

1. **Aura**: shared.
2. **Body**: section 2 without the lens highlight; dark gradient `#181D2B → #07090D`, light `#FFFFFF → #E4E8F0`.
   **Volume shading**: radial gradient centered at `c − (0.12, 0.16) S`, clear to 75 % of 0.5 S, then to black 42 %
   (light: `#1E283C` 12 %) at the edge.
3. **Light spill**: a 0.22 S band above the surface in the liquid color `L` from 0 to 16 % (additive; light 10 %),
   × `0.4 + 0.6 h`.
4. **Back wave** (S ≥ 120): the surface below with phase + 1.7 rad, 75 % amplitude, raised 0.008 S, filled with `L`
   darkened 25 % (light 8 %) at 55 % of the front opacity.
5. **Liquid body**: vertical gradient from `min(y_L, c + 0.2 S) − 0.04 S` to the bottom, `L` at `α` → `L` darkened 62 %
   (light 14 %) at `α + 0.2`. `L = heat(h)` (dark) / `mix(heat(h), white, 0.12)` (light); `α = 0.46 + 0.10 h` (dark) /
   0.80 (light); idle: slate at 0.40 / 0.55. Then a **spherical shade** over the liquid: radial from 35 % of the radius
   (center `c − (0.06, 0.08) S`) to black 50 % (light `#283042` 14 %) at the glass, so the liquid reads as a glowing
   volume, not a flat fill. Then the **inner glow** (additive), kept below the readout: radial at
   `(c_x, max(y_L + 0.08 S, c_y + 0.38 S))`, radius 0.32 S, `mix(L, #FFF7D6, 0.25 + 0.45 o)` at
   `(0.26 + 0.2 o)(0.3 + 0.7 h)` (× 1.25 Max; light `L` at 10 %). Then the **readout scrim** (dark only): an elliptical
   radial gradient centered at `c + (0, 0.1 S)`, radii 0.36 × 0.26 S, black 35 % → 30 % at 55 % → 0. Together they keep
   the white readout ≥ 4.5:1 on every heat color up to full overdrive (computed worst case: value 5.0:1, title
   4.55:1).
6. **Energy currents** (Soft and up, S ≥ 120, not Reduce Motion): three filaments at 25 / 50 / 75 % of the depth,
   `y = depth + 0.03 S sin(7u + φ) + 0.015 S sin(13u − 1.7 φ)` across the width, widths 0.018 / 0.014 / 0.010 S,
   blurred σ 0.006 S, `mix(L, #FFF7D6, 0.35 + 0.4 o)` at 10 % (light: white 22 %) × `(0.4 + 0.6 h)`, drifting with the
   wave phase.
7. **Motes and bubbles**, **pops**, **splashes**, **steam**, **falling droplets** (below).
8. **Meniscus**: the surface stroked at `(0.008 + 0.004 h) S` in `mix(L, #FFF7D6, 0.45 + 0.5 o)` with glow
   σ `(0.015 + 0.015 h) S` (× 1.3 Max); light: `heat(h)` darkened 25 %; idle white 28 % / slate 50 %.
9. **Glass**: wall stroke 0.006 S at r 0.455 S (white 9 % / black 6 %); bottom caustic: arc r 0.475 S, 55° → 125°,
   width 0.02 S, σ 0.008 S, `L` at `0.35 (0.3 + 0.7 h)` (light 0.3); specular ellipse 0.2 × 0.12 S rotated −34° at
   `c − (0.17, 0.22) S`, white 13 % / 75 % → 0; rim glint arc r 0.42 S, 200° → 236°, width 0.012 S, white 22 % / 95 %.
10. **Readout split by the surface**: above the surface in the normal tokens; below it in white 97 % / unit white 80 % /
    title white 94 %, each with a black 55 % shadow σ 0.015 S (dark), or ink `#0B0D12` 94 % / unit 72 % / title =
    `heat(h)` darkened 55 % without glow (light). The title therefore never sits in its own color on its own color.
11. **Agent dots** with a 1.2 pt halo (black 55 % / white 75 %), wave, rays, pill: shared.

Surface and level:

- Level (fraction of the inner diameter): `f* = 0.08 + 0.74 h + 0.06 o` (idle 0.08) through an underdamped spring,
  0.75 Hz, damping 0.62 (a gentle slosh after a change); Off and Reduce Motion: critically damped, settle 0.9 s. Mean
  surface `y_L = c_y + 0.455 S − 0.91 S f`.
- `y(x) = y_L + A [0.55 sin(2π x / 0.9 S + φ1) + 0.30 sin(2π x / 0.47 S − φ2) + 0.15 sin(2π x / 0.23 S + φ3)] + chop +
  ripples` with `A = (0.004 + 0.02 h + 0.018 o) S` (Soft × 0.6, idle 0.002 S, Off/Reduce Motion 0) through a spring
  (settle 0.8 s); phase rates φ1 `0.25 + 0.5 h + 0.3 o`, φ2 `0.4 + 0.8 h + 0.4 o`, φ3 `0.9 + 1.6 h` Hz. The two
  counter-moving components make a sloshing standing wave rather than a conveyor belt.
- **Boil chop** (redline tier, Standard and up): `0.006 S (0.5 + 0.5 o) sin(2π x / 0.11 S + φ4)(0.7 + 0.3 sin(x /
  0.05 S + 1.7 φ4))`, φ4 3.1 Hz; halved below S 120.
- **Ripples** (≤ 4): `−A_r e^(−τ/0.75) exp(−((d − 0.42 S τ) / 0.075 S)²) cos(2π (d − 0.42 S τ) / 0.1 S)`, `d` = distance
  from the impact, `A_r = 0.022 S (0.6 + 0.6 strength)`.

Particles:

- **Motes / bubbles** rise from near the bottom (x uniform in ±60 % of the radius), speed `(0.08 + 0.35 h + 0.25 o)
  S/s × U(0.7, 1.3)`, accelerating `(0.12 + 0.2 h) S/s²`, wobble 0.006 S at 2.3 Hz, radius `(0.006 + 0.01 h) S ×
  U(0.6, 1.4)` (≥ 0.9 pt). Their look follows `hot = smoothstep(0.35, 0.9, h)`: cool ones are glass bubbles (a ring
  of 28 % of the radius, white 50 % / 85 %, and a highlight dot); hot ones are luminous motes (radial gradient to
  2 × the radius: `mix(L, #FFF7D6, 0.9)` 95 % → 40 % at 35 % → 0, additive). They pop at the surface (a ring growing
  to 2.5 × in 0.25 s); while boiling, 55 % also throw 1–2 splash droplets.
- Rates: Soft `0.6 + 3 h` per s (≤ 6 alive); Standard `1.5 + 12 h` (+ `6 + 10 o` in the redline tier) per s, pool 32
  (16 below S 120); Max × 1.6, pool 48.
- **Splash droplets** (pool 40): ±0.25 S/s sideways, 0.35–0.7 S/s up, gravity 2.2 S/s², radius 0.006–0.011 S, gone
  when they fall back in.
- **Steam** (redline tier, Standard and up): `2 + 4 o` wisps per s (× 1.6 Max, pool 12), soft discs of radius
  0.03–0.06 S doubling as they rise 0.06–0.12 S/s for 0.8–1.4 s, `#FFF7D6` 12 % (light white 18 %) × `sin(π u)`.

Events:

- **Pulse accent**: a droplet (teardrop of radius `(0.012 + 0.006 strength) S` in the response's heat color) appears
  0.16 S above the surface (never above c − 0.414 S, never below c − 0.3 S) and falls at 2.6 S/s²; at impact a ripple,
  3–6 splash droplets (Standard and up) and 2 / 6 bubbles (Soft / Standard). At most 3 droplets in flight.
- **New-peak accent**: the level surges (velocity + 0.9/s on a 0.9 Hz, damping 0.45 spring: ≈ +0.1 of the diameter),
  a gold band (`#FACC15` 45 %, ±0.06 S) rises from the bottom to the surface over 0.9 s, and 22 gold motes stream up.
- **Tier flash**: the meniscus.
- **Idle**: a slate pool at 8 %, flat (static), no motes. Sign of life: one cool bubble rises from the bottom-left and
  pops (≤ 1.2 s, 30 fps).

| Effects | `liquid` |
| --- | --- |
| Off | static level by `h`, frozen surface shape, no particles, currents or droplets, fixed glow |
| Soft | waves × 0.6, ≤ 6 bubbles, currents, droplets with ripples (no splash), aura breathing; no boil, steam or splash |
| Standard | full waves and motes, boil chop, splashes, steam, surge |
| Max | Standard with particles × 1.6 and glows × 1.25 |

Reduce Motion: flat static surface (A = 0), the level eases critically damped, no particles, currents or droplets;
boiling reads as a brighter meniscus and inner glow; the new peak is the pill plus the gold band as a 0.3 s crossfade.
Increase Contrast: meniscus × 1.5 width, readout shadow 75 %, glows × 0.5. Windows high contrast: the liquid is a solid
fill in the theme's highlight color at 35 %, meniscus in the highlight color, no particles or glows. Reduce
Transparency: solid body and α + 0.15. LOD: S < 120 drops the back wave, currents and steam, halves the chop and caps
the motes at 16; thumbnails show the still liquid and the value.

### 2a.4 `reactor` — Reactor energy ring

A sci-fi core: a dark core window carries the readout; around it three counter-rotating energy rings spin up with
speed, lengthen until they close into a solid ring of fire beyond the redline, and throw lightning arcs.

Layers, back to front:

1. **Aura**: shared. **Body**: dark `#141824 → #06070B`; light as section 2 with the bottom `#E5E9F1`.
2. **Stator**: a full ring r 0.468 S, width 0.006 S, unlit token × 1.4; 24 notches r 0.456 → 0.474 S, width 0.005 S
   (every 6th × 2.6 opacity; 4 notches below S 120).
3. **Tracks**: unlit rings under rotor A (r 0.425 S, width 0.024 S) and rotor C (r 0.338 S, width 0.009 S).
4. **Corona** (not idle): radial gradient r 0.30 → 0.40 S, `heat(h)` (dark: blended `0.2 o` toward `#FFF7D6`) at
   `(0.10 + 0.42 h)` × breathing (±15 %, `0.3 + 1.7 h` Hz) + pulse flare (+0.25 fading over 0.35 s), additive (light
   `0.06 + 0.2 h`), × 1.2 at Max.
5. **Rotor A** (outer, clockwise): three arcs 120° apart, each `20° + 90° h + 10° o` long (capped at 120°: at full
   overdrive they join into a closed ring), width 0.024 S (idle 0.6 ×). Each arc is a gradient from the tail
   (`heat(h)` at 25 % dark / 50 % light) to the head (*hot*), glow σ 0.015 S (dark, × 1.3 Max).
6. **Rotor B** (middle, counter-clockwise): 24 coils (12 of 13° below S 120), 7.5° long, width 0.018 S at r 0.383 S.
   Energy `e_i = 0.18 + 0.82 (½ + ½ cos(3 θ_i − ψ))^2.2 (0.35 + 0.65 h)`: three bright bands run clockwise — against
   the coils — at `ψ̇ = 0.15 + 1.4 h + 0.6 o` rev/s; color `heat(h)`, blending toward *hot* above e 0.75; opacity `e`.
   Idle: slate at 25 %.
7. **Rotor C** (inner, clockwise, fastest): two arcs 180° apart, `14° + 130° h + 20° o` long (≤ 175°), width 0.009 S,
   gradient as A.
8. **Lightning** (redline tier, Standard and up): `1.5 + 6 o` bolts per s (× 1.6 Max), at most 3 alive (Max 4). A bolt
   lives 0.09–0.18 s and is re-randomized every 50 ms: from the core rim (r 0.306 S) to rotor A (r 0.425 S) within
   ±29°, or — when `o > 0.3`, 40 % of bolts — leaping outward from r 0.437 S to r 0.56–0.66 S, into the aura (still
   inside the 1.5 S window). An 8-segment polyline with perpendicular jitter `±0.048 S sin(π u)`, three strokes: glow
   0.03 S `heat(0.92)` 28 %, body 0.011 S 75 %, core 0.0045 S `#FFF7D6` (light: label color of 0.9 and `#FFB020`),
   full for 20 % of its life, then fading. 6 sparks at the far end (0.2–0.6 S/s, drag 3/s, 0.15–0.4 s; pool 40, Max 64).
   Flashes that brighten the corona are limited to 3 per second.
9. **Pulse flare**: a ring from r 0.30 to 0.50 S in 0.32 s (ease-out), width 0.012 S → 0, in the response's heat color
   at 55 %; it hands over to the shared wave at the disc edge.
10. **Core window**: a disc r 0.30 S, radial gradient `#181C28 → #080A0F` at 97 % (light white → `#EEF1F6`); rim 0.009 S
    in `mix(heat(h), hot, 0.4 o)` at `60 + 40 h` %, glow σ `(0.015 + 0.015 h) S` (dark); idle: unlit token × 2.2.
11. **Readout, agent dots (inside the window), wave, rays, pill**: shared.

Motion: rotor speeds in rev/s, integrated as angles: A `0.03 + 0.45 h^1.3 + 0.35 o + k`; B `−(0.02 + 0.30 h^1.3 +
0.25 o + 0.6 k)`; C `0.05 + 1.1 h^1.3 + 0.7 o + 1.2 k`. The pulse kick `k += 0.25 × strength` decays with τ 0.5 s, so
every completed response visibly spins the reactor up. At the redline rotor C turns ≈ 1.2 rev/s (≈ 7° per frame at
60 fps, smooth); at full overdrive ≈ 1.9 rev/s.

- **New-peak accent — lock**: each rotor swings forward (in its direction of travel) to the nearest pose with an arc
  head at 12 o'clock (symmetry 120° / 15° / 180°) over 0.42 s (`easeOutBack`, overshoot 1.2), holds to 0.57 s while all
  three flash gold `#FACC15` (intensity `1 − |τ/0.42 − 1| / 0.35`), then spins on from the locked pose.
- **Tier flash**: corona and arc heads.
- **Idle**: rotors stopped where they were, arcs at minimum length in slate at 38 % (light 45 %), corona off, core rim
  unlit. Sign of life: a light band (+60 %, Gaussian, ≈ 1/7 of the circle wide) runs once around rotor B in 0.9 s.

| Effects | `reactor` |
| --- | --- |
| Off | rotors still (arc lengths and color still encode `h`), steady corona, no bands, flares, kicks or lightning |
| Soft | rotation, energy bands, corona breathing, pulse flares and kicks, lock; no lightning or sparks |
| Standard | Soft + lightning and sparks |
| Max | Standard with bolts and sparks × 1.6 (4 alive), glows × 1.3, corona × 1.2 |

Reduce Motion: rotors frozen, bands static, corona steady, no lightning; the lock becomes a 0.3 s gold crossfade.
Increase Contrast: unlit tracks × 2, rotor tails at 60 %, glows × 0.5. Windows high contrast: rings in the theme's
text color, heads in its highlight color, no corona, glow or lightning. LOD: S < 120 has 12 coils and 4 notches;
thumbnails draw rotors A and C and the core only.

### 2a.5 `minimal` — Minimal ring

Only the number, one thin progress ring and the title; almost nothing moves. The calmest style, and the one that stays
clear at small sizes (it alone may go down to 64 pt).

Layers, back to front:

1. **Body**: section 2 without the lens highlight. No aura, agent dots, unit, wave or rays.
2. **Track**: a full circle r 0.425 S, width `w = max(2.5 pt, 0.034 S)`, unlit token × 1.15.
3. **Progress**: from 12 o'clock clockwise over `360° × p` with `p = h + o` (0 → 2): the first lap fills to the
   redline, a **second lap** runs over the first beyond it (complete at `v = 2R`), the way an activity ring passes 100 %.
   Round caps; a conic gradient from the label color darkened 28 % (light 10 %) at the start to the label color at the
   head; the second lap is the head color throughout and its head casts a shadow onto the lap below (black 55 % / 22 %,
   σ `0.35 w`, from 0.02 rad behind to 0.07 rad ahead of the head). Dark, from boost up: glow σ `0.0125 S` at 35 %.
4. **Head dot** (redline and pulses): radius `0.32 w` at the head, `mix(head, #FFF7D6, 0.6)` (light: head).
5. **Readout**: frame table; the value never rolls, titles crossfade in 0.25 s instead of sliding.

Motion: the ring follows the smoothed `h` and `o`. The only continuous animation: in the redline tier the glow
breathes at 1 Hz (σ `0.0125 → 0.025 S`, opacity 35 → 70 %) and the head dot shows at `0.6 ×` the breath. Pulse accent:
the head dot and glow blink (+30 %, 0.4 s); no wave. New peak: the ring tints toward gold (`#FACC15` / light `#C99700`)
by `sin(π τ / 0.7)` over 0.7 s, plus the pill. Idle: the track only; no sign of life, so an idle `minimal` orb draws
no frames at all.

| Effects | `minimal` |
| --- | --- |
| Off | no glow, breathing or blink |
| Soft, Standard | as described |
| Max | Standard with the glow × 1.3 |

Reduce Motion: steady glow (no breathing), the gold tint and blink become 0.2 s crossfades. Increase Contrast: track
× 2, ring width × 1.25, no glow. Windows high contrast: track in the theme's gray text color, ring in its highlight
color. Sizes 64–240 pt; at S < 80 the title uses 7 pt and the ring keeps `w ≥ 2.5 pt`.

### Frame policy and performance budget (normative for every style, `led` included)

The orb runs all day and there may be eight of them; a single Windows orb at the redline costs ≈ 38 % of one core today
on the 144 Hz 4K reference machine. Every style must stay far below that.

**Frame classes.** All orbs on a display share **one** frame clock (macOS: one `CADisplayLink` per screen; Windows:
one `CompositionTarget.Rendering` subscription or one DXGI/DComp frame callback), and each orb renders only on its own
k-th tick. "60 fps" means the integer divisor of the refresh rate closest to 60 (ties to the higher: 144 Hz → 72,
120 → 60, 60 → 60); "30 fps" likewise (144 → 28.8, 120 → 30); "15 fps" likewise. The display refresh rate itself is
never used above 72.

| State | `led` | `needle` | `liquid` | `reactor` | `minimal` |
| --- | --- | --- | --- | --- | --- |
| Idle, not hovered | static; 30 fps during the 0.9 s sweep every 6 s | static; 30 fps sweep | static; 30 fps for the 1.2 s bubble | static; 30 fps sweep | static, never animates |
| Cruise, boost (`h < 0.45`) | 30 fps | 30 fps; 60 while the needle turns > 90°/s | 30 fps | 30 fps | 30 fps while a spring settles, else static |
| Overdrive, redline | 60 fps | 60 fps | 60 fps (Soft: 30) | 60 fps | as above; 15 fps for the redline breathing |
| Transitions (title, peak, pulse, hover, style change) | 60 fps for their duration | 60 | 60 | 60 | 30 |
| Off or Reduce Motion | on change only: a value tick (≤ 2/s) renders the spring at 30 fps until it settles (change < 0.001), then stops | same | same | same | same |

- "Static" means no frames: the last frame stays on screen; the next one is drawn on data change, hover or the next
  scheduled sign of life (a single timer, not polling).
- When **four or more orbs** are in the 60 fps class at once, all of them drop to 30 fps except the hovered or most
  recently interacted one.
- Rendering pauses entirely while an orb is hidden, faded below 5 % opacity mid-fade, fully occluded (macOS
  `occlusionState`; Windows `DwmGetWindowAttribute(DWMWA_CLOAKED)` and monitor-off / session-lock notifications), or on
  a Space that is not visible. Data keeps flowing; the first frame after resuming shows the current state, never a
  catch-up animation.

**Budgets** (release build, Standard effects, S = 148 and S = 240, one orb unless stated; CPU = main + render threads,
% of one core averaged over 10 s; reference machines: an M1/M2 MacBook Air at 2× and the Windows 144 Hz 4K machine at
its native scale):

| Scenario | macOS | Windows |
| --- | --- | --- |
| Idle, any style (incl. the 6 s sign of life) | ≤ 0.5 % | ≤ 0.5 % |
| Cruise (30 fps) | ≤ 2.5 % | ≤ 3 % |
| Redline / overdrive, Standard | ≤ 4.5 % | ≤ 6 % |
| Redline / overdrive, Max | ≤ 5 % | ≤ 8 % |
| Eight orbs at redline, Standard | ≤ 12 % | ≤ 25 % |

On macOS the system's own cost per presented frame (Metal drawable, commit, pacing; ≈ 0.4–0.6 ms of CPU) dominates
the orb's own ≈ 0.1–0.2 ms, so the budget is a frame-count budget: keep the frame rates of the table above and never
present a frame that would be identical to the previous one.
| GPU time per orb frame (S = 240, 2× / 200 %) | ≤ 1.0 ms | ≤ 1.0 ms |
| Memory per orb (surfaces, buffers, atlases) | ≤ 12 MB | ≤ 12 MB |

Targets are part of the definition of done: each platform ships a benchmark harness that drives every style through
the scenario of the prototype (idle → climb → redline → overdrive → back) and reports these numbers.

**Techniques (required).**

- GPU only: every layer is a primitive of one instanced signed-distance-field pipeline (the macOS `OrbShaderSource`
  shapes — rounded rect, circle, ring, capsule, radial, arc — plus `polyline` for bolts and the liquid surface strip and
  `wedge` for the needle wake). Glows are the analytic Gaussian of the distance, never a blur pass; the liquid's
  surface, body, currents and spherical shade are one fragment shader evaluating `y(x)` per pixel; text comes from a
  glyph atlas (digits, `.kMB–`, the unit and the titles of the title themes in use — presets in the current language,
  custom titles as typed — built once per size and rebuilt when a theme or custom set changes).
- No per-frame allocation: instance buffers and particle pools are sized once from the style's maximum budget (ring
  buffers), paths and geometry are rebuilt only on a size or appearance change; WPF: frozen resources only.
- CPU per frame ≤ 0.25 ms for state updates (springs, particles) at the maximum budget.
- **Windows**: the current path — a WPF layered window (`AllowsTransparency`) whose `DrawingVisual`s are rebuilt every
  frame and read back by `UpdateLayeredWindow` at the display rate — is the likely source of the 38 % (to be
  confirmed with a profile on the reference machine). The animated orb draws with
  Direct3D 11 into a composition swap chain (`CreateSwapChainForComposition`, premultiplied alpha) hosted by
  DirectComposition in a `WS_EX_NOREDIRECTIONBITMAP` window, running an HLSL port of the same instanced SDF shader as
  macOS (one shared instance layout). Click-through outside the disc and the click-through mode keep today's behaviour
  (WS_EX_TRANSPARENT toggled by cursor position); verify that it works with the composition window on Windows 10 and 11.
  WPF effects (`BlurEffect`, `DropShadowEffect`) are not allowed on animated content. If the WPF path is kept, it must
  meet the same budget, measured on the reference machine.

## 2b. Multiple orbs

The user can float up to **eight** orbs, each tracking any combination of sources — for example one orb for Claude
Code + Claude Desktop and one for Codex — with its own position, size, style and window options. An orb's value,
heat, title, tiers, agent dots, pulses, new peaks and hover line count only its own sources. The dashboard, the menu bar
/ taskbar widget and their accessibility always show all sources.

### Data model

Persisted as one JSON value in the settings store (key `orbs`), written atomically on every change:

```json
{
  "version": 1,
  "orbs": [
    {
      "id": "6F1C2A9E-…",
      "name": null,
      "sources": ["claudeCode", "claudeDesktop"],
      "style": "needle",
      "size": 148,
      "opacity": 1.0,
      "alwaysOnTop": true,
      "clickThrough": false,
      "fadeWhenIdle": false,
      "allSpaces": true,
      "visible": true,
      "redlineOutput": null,
      "redlineTotal": null,
      "titleTheme": null,
      "unit": "tokens",
      "traffic": "sources",
      "showName": null
    }
  ],
  "layouts": {
    "<arrangement key>": { "<orb id>": { "display": "<display key>", "xAnchor": "high", "x": 12, "yAnchor": "fraction", "y": 0.31 } }
  }
}
```

| Field | Rule |
| --- | --- |
| `id` | UUID, never reused. |
| `name` | Custom name, 1–32 characters, trimmed; `null` = automatic: `orbs.sources.all` for all sources, the source name for one, `orbs.name.pair` for two, `orbs.name.more` (first source + count) for three or more, in source order (METRICS 1). |
| `sources` | `"all"` (every enabled source, including sources detected or added later) or a non-empty array of METRICS 1 keys, stored in source order without duplicates. Unknown keys from a newer version are kept and ignored. |
| `style` | `led` · `needle` · `liquid` · `reactor` · `minimal`; unknown → `led`. |
| `size` | 96–240 pt/DIP (`minimal` 64–240), clamped when the style changes (a 72 pt `minimal` becomes 96 pt `needle`). |
| `opacity` | 0.4–1.0. |
| `alwaysOnTop`, `clickThrough`, `fadeWhenIdle`, `allSpaces` | As the former global settings, now per orb (`allSpaces` macOS only). |
| `visible` | Hides one orb without removing it. |
| `redlineOutput`, `redlineTotal` | `null` = the General redline of that metric; otherwise this orb's redline (same ranges). Heat, tiers and titles of the orb use it. |
| `unit` | `"tokens"` (tok/s, default) or `"cost"` (the money unit of section 9); unknown → `"tokens"`. Copied by Add, Duplicate and Split. Heat, tiers and titles always follow tokens. |
| `traffic` | What the orb's two-line readout shows (section 10, *Floating orb readout*): `"sources"` (default: the traffic of the orb's own sources), `"machine"` (the whole computer), `"intelligence"` (性能, the highest performance score of the orb's own agents, section 12 and METRICS 6.9) or `"off"`; absent (orbs saved by older builds) → `"sources"`, unknown → `"off"`. Copied by Add, Duplicate and Split; Merge keeps the target's. Older builds ignore it, and read `"intelligence"` as `"off"`. |
| `showName` | `null` (default) follows the global `orbNames`; `true` / `false` shows or hides this orb's name plate (Name plate, below). Copied by Add, Duplicate and Split; Merge keeps the target's; absent or unknown → `null`. Older builds ignore it. |
| `titleTheme` | `null` = the General title theme; otherwise a preset id or a custom set id (section 1, Title themes) naming this orb's titles, idle label and accessibility label. Deleting the custom set it names replaces it with that set's base preset (undo restores it); an unknown id counts as `null`. Added in this version without a format change: older builds ignore it. |
| `layouts` | Positions per **display arrangement**: the key is the sorted list of connected display keys (macOS display UUID; Windows monitor device ID) joined with `+`. Each entry uses today's position record (edge-docked distance or fraction of the usable area, per axis) on the named display. At most 16 arrangements are kept (least recently used dropped). |

Global (unchanged, shared by all orbs): Show floating orbs (master switch), Appearance, Language, metric, General
redline, title theme (the default of every orb), Effects intensity, Celebrate new peaks, Response pulses, name plates
(`orbNames`: `"auto"` default, `"always"`, `"never"`; Name plate, below). Effects stay global because the style
already sets an orb's character.

**Arrangement changes.** When displays are connected, removed or rearranged, each orb takes its record from the layout
of the new arrangement; if the arrangement is new, an orb keeps its record from the last arrangement when its display is
still connected, else moves to the main display keeping its anchors. Overlaps are then resolved in list order: a later
orb slides to the nearest free touching position (below). Nothing is ever placed off-screen.

**Migration** (first launch of the multi-orb version, when `orbs` is absent): create one orb — new id, `name null`,
`sources "all"`, `style "led"`, `size` = old `orbSize`, `opacity` = `orbOpacity`, the four toggles from the old keys,
`visible true` — and put the old per-display positions into the current arrangement's layout (every old display key is
kept as that orb's fallback record). The master switch takes the old `orbVisible`. The old keys are left untouched and
never written again, so a downgrade restores the single orb. After migration the orb looks and sits exactly as before
(test: same window frame and style).

**Defaults**: a fresh install has one orb tracking all sources, `led`, 148 pt, at today's default position.

**Limits**: 1–8 orbs. Add, Duplicate and Split are disabled at 8 (tooltip `orbs.limit`); a split that would exceed 8
creates the first sources in order until 8 and says so in its undo banner (`orbs.didSplitLimited`). The last orb cannot
be removed (Remove is disabled; the master switch hides it).

### Creating, placing and snapping

- **Add**: copies the style, size, opacity, toggles and title theme of the selected orb (Settings) or the clicked orb
  (menu). Its sources: the first detected, enabled source (source order) that no orb tracks *explicitly*; if every
  detected source is tracked explicitly, `"all"`. From the menu the new orb shows its chip for 2 s; from Settings it is
  selected and its editor opens.
- **Duplicate**: an exact copy with a new id; a custom name gets ` 2` appended.
- **Placement** of a new orb: on the display of the orb it came from (else the main display), try the four touching
  slots of that orb — left, below, right, above (12 pt gap, centers aligned) — and take the first one fully inside the
  usable area that overlaps no orb (disc + 12 pt). Otherwise search a 24 pt grid outward from that orb for the nearest
  free spot; if the display is full, cascade +24 / +24 pt from it, clamped inside the usable area. It springs in from
  0.6 × scale and 0 opacity (response 0.45 s, damping 0.78; Reduce Motion: 0.2 s fade).
- **Screen edges**: today's rule — released within 24 pt of a usable edge, the disc snaps to a 12 pt margin.
- **Orb to orb**: while dragging, each other orb on the display offers a touching position at a 12 pt gap along the
  line between the centers; within 16 pt of it the dragged disc is pulled in (the disc follows the pointer through a
  critically damped spring, settle 0.12 s, so the pull feels magnetic, not sticky). Release springs into place
  (response 0.35 s, damping 0.8). Released overlapping another orb, the disc slides to the nearest free touching
  position.
- **Alignment**: when the dragged center is within 8 pt of another orb's center x or y and the two discs are within
  64 pt of each other (edge to edge), it aligns; a 1 pt dashed guide in the accent color at 60 % shows while dragging.
  Priority: screen edge, then orb-to-orb touching, then alignment (alignment alone never pulls a disc away from an edge
  it is docked to).
- **Z-order**: all orbs use the window level of their `alwaysOnTop`; among orbs the last dragged or clicked is in front.
  Because snapping prevents overlap, the order rarely shows. Orbs never take keyboard focus (non-activating, as today).
- **Click-through** and **fade when idle** are per orb. Fade uses the orb's own idle state (its sources idle for 60 s)
  and multiplies its opacity.
- **Master switch**: hides or shows all orbs. Turning it on while every orb is individually hidden makes them all
  visible. The quick menu (menu bar / tray) keeps one item, `menu.showOrb` / `menu.hideOrb` with one orb and
  `menu.showOrbs` / `menu.hideOrbs` with several; with several orbs it adds a `settings.orb` submenu listing each orb
  (colour dots + name) with Show, Click-through and `menu.orb.settings` — the way back to a click-through orb.

### Identity at a glance

- **Source bezel**: an orb tracking a subset replaces the disc's white rim highlight with a colour rim of width
  `max(1 pt, 0.0105 S)` (1 px in thumbnails) on the disc edge, split into equal arcs clockwise from 12 o'clock in source
  order with 4° gaps, in the source colors at 85 % (light: darkened 10 %, 90 %). An unavailable source's arc is dashed
  (1.2 w on, 1.4 w off) in the unlit token × 2.5. Orbs tracking all sources keep the plain rim. Together with the
  agent dots, which are already in source colors, this identifies an orb without any label.
- **Source chip**: a capsule shown after 0.35 s of hover, during a drag, and for 2 s after the orb is created, split,
  merged or its sources change. Height 18 pt, 11 pt semibold, radius 9, padding 8, 6 pt colour dots; dark
  `rgba(20, 22, 30, 0.82)`, light `rgba(255, 255, 255, 0.9)`, hairline border and a soft shadow; drawn in a separate
  non-activating overlay so it may extend past the orb window. Placed centered 6 pt below the disc; when another orb or
  the screen edge is in the way, on the side facing the screen center, vertically centered; always clamped inside the
  usable area. Content: dot + name for up to two sources, else the first and `+n`; unavailable sources in secondary
  color with `source.notFound` / `source.disabled`. An all-sources orb shows `orbs.sources.all`, and only when more than
  one orb exists (a single orb behaves exactly as today). While the orb shows a name plate, the chip is folded into the
  plate instead of drawn as a second capsule (Name plate, below).
- **Hover info line** inside the orb: `today.summary` over the orb's sources (today's output of those sources, the
  number of its agents).
- **Settings highlight**: hovering, selecting or focusing an orb row in Settings rings that orb on the desktop (2 pt
  accent ring 4 pt outside the disc, 60 %, fading in over 0.15 s), so the user always knows which orb is which.

### Name plate

Each orb can carry its name on a small glass plate beside the disc — normally centered under it, the way a row of gauges
carries engraved nameplates — so `Codex · Claude Code · Claude Desktop` reads at a glance on any wallpaper. Prototyped
(2026-10-22) for every style in both appearances at 112, 148 and 196 pt on busy dark, light and photographic
wallpapers, in tight rows, columns and clusters, with Latin, CJK, long and multi-source names.

- **Chosen over**: lettering along the inside of the upper rim like a chronograph's brand ring (no free band in any
  style: it runs into the LED arc and comet, the needle's graduations, the liquid's glass wall and the reactor's stator,
  and sits at the 7 pt floor at 112); print on the dial above the value (that space holds the traffic readout and the
  new-peak pill, and at 112 a name shrinks to `CL…`); a curved glass collar hugging the lower rim (charming alone, but
  curved text reads worse, a row of them makes a wavy line, and long or CJK names tilt at the ends); plain text with a
  shadow and no plate (breaks up on light and patterned wallpapers). One plate for all five styles: a mixed-style row
  shares one baseline, and the plate repeats the orb's own glass instead of competing with any instrument.
- **When it shows**: the orb's `showName` (`true` / `false`), else the global `orbNames`:

  | `orbNames` | Plate |
  | --- | --- |
  | `"auto"` (default) | when two or more orbs are visible, or when the orb tracks some (not all) sources — so the classic single all-sources orb, and every migrated install, looks exactly as before |
  | `"always"` | on every orb |
  | `"never"` | on none |

  Never drawn below S = 80 (`minimal` only: the plate would outweigh the orb's 7 pt title; the source chip still appears
  on hover), nor while its orb is hidden.
- **Content**: colour dots, then the orb's name (custom `name`, else automatic; data model above).
  - Dots: one per tracked source in source order, at most three, each overlapping the previous by a third, the first in
    front, separated by a 1 pt ring in the plate color; source colors as the bezel (light: darkened 10 %). All sources:
    one spectrum dot, a conic gradient through every source color in source order from 12 o'clock. An unavailable
    source: a hollow ring (stroke `max(1 pt, 0.18 d)`) in the secondary color.
  - Text in neutral ink, never in a source color (sky, blue or pink text on glass is weak, and the dots already tie the
    plate to the bezel and the agent dots). Latin as written, no capitals or wide tracking: names are proper nouns
    (`omp`, `DeepSeek`), and capitals belong to the speed title.
  - Shortening within the width budget (below); the type never shrinks: automatic pair `A + B` → `A +1`
    (`orbs.name.more`) → `A… +1`; automatic `A +n` → `A… +n`; one source or a custom name → tail ellipsis `…` on a
    grapheme boundary. `Claude Code + Claude Desktop` needs 171 pt at 112, 189 at 148 and 222 at 196, so in a tight row
    it reads `Claude Code +1`, and hovering expands it (chip, below).
- **Geometry** (pt):

  | Measure | Rule | 112 | 148 | 196 |
  | --- | --- | --- | --- | --- |
  | Text size `f` | 0.066 S rounded to 0.5, clamped 9–12 | 9 | 10 | 12 |
  | Plate height `h` | round(1.78 f), corner radius h / 2 | 16 | 18 | 21 |
  | Gap to the disc `g` | round(0.034 S), clamped 4–7 | 4 | 5 | 7 |
  | Dot `d` | max(5, 0.6 f) | 5.4 | 6 | 7.2 |
  | Width budget, alone | 1.5 S | 168 | 222 | 294 |
  | Width budget, tight row (12 pt gap) | S + 6 | 118 | 154 | 202 |

  The first dot is concentric with the left end cap (its center h / 2 from the edge); the text starts 0.42 f after the
  last dot; trailing padding 0.52 h. **Width budget**: 1.5 S; when another orb's disc overlaps the orb's horizontal
  band (`|Δy| < (S + S_n) / 2`), a plate above or below may not pass the point that divides the distance between the
  two centers in proportion to the two sizes, less 3 pt — equal orbs at the 12 pt snap gap get S + 6 each, so their
  plates stay ≥ 6 pt apart. A side plate is limited by the usable area (3 pt inset) instead.
- **Placement**: centered g below the disc (following the disc edge through the hover scale, so the gap stays). Orbs
  whose discs are within 16 pt form a group (a row, a column, a cluster). A group takes the first placement that fits
  every member — below, above, the side facing the center of the usable area (vertically centered on the disc, g from
  its edge), the other side; if none fits all, each member takes its own first fit; if nothing fits, that orb's plate
  hides (the chip still appears on hover). A plate fits when it stays 3 pt inside the usable area and 4 pt from every
  other disc and plate. So a top row has its plates below, a row docked to the bottom edge has them above, a column
  docked to a side edge lists its names toward the screen like a legend, and a 2 × 2 cluster puts the top pair's plates
  above and the bottom pair's below. Placement is resolved on drag release (after the snap spring settles), on display
  or arrangement changes, and when a name, size, visibility or plate setting changes; during a drag the plate keeps its
  slot and travels with the orb.
- **Type**: system font (section 6), semibold (600), tracking +0.012 em, or +0.04 em when the name contains CJK; one
  line, never wrapped; centered on the plate by cap height (Latin) or the ideographic center (CJK); tabular digits.
- **Material** — the orb's own glass, so the plate reads as part of the instrument:

  | Token | Dark | Light |
  | --- | --- | --- |
  | Fill (vertical) | `rgba(32, 37, 52, 0.72)` → `rgba(12, 14, 20, 0.80)` over a background blur (macOS `.hudWindow`) | `rgba(255, 255, 255, 0.80)` → `rgba(241, 243, 248, 0.84)` over `.popover` |
  | Rim | 1 px inner stroke white 16 % at the top → 4 % at the bottom; 0.5 px outer stroke black 35 % | 1 px inner highlight white 95 % at the top; 0.5 px outer stroke black 10 % |
  | Shadow | black 38 %, y 1, blur 6 | black 14 %, y 1, blur 6 |
  | Text | white 92 % | `#0B0D12` 88 % |
  | Secondary (statuses) | white 66 % | black 60 % |

  Worst case (the dark plate's blur over pure white, the light plate's over pure black): text 7.1:1 / 9.7:1, secondary
  4.6:1 / 4.9:1. Reduce Transparency, and Windows (layered windows have no acrylic, as for the body): solid
  `#1F2432 → #0E1016` / `#FFFFFF → #F0F2F6`. Increase Contrast: fill opacity 92–96 %, a 1 pt border white 42 % /
  black 45 %, text 100 %, secondary 78 % / 72 %. Windows high contrast: `ButtonFace` fill, `ButtonText` text, a 1 px
  `WindowText` border; the dots keep their colors inside a 1 px `WindowText` ring. The plate's opacity multiplies by the
  orb's opacity, its fade when idle and the unavailable dim (55 %).
- **Window and pointer**: the plate lives in the orb's chip overlay (non-activating; on macOS a child window of the orb,
  so it moves with it; on Windows an owned window moved in the same `DeferWindowPos` batch), so a side plate can sit
  beside the disc. It belongs to the orb: hovering it hovers the orb (scale, info line, chip), dragging it moves the
  orb, right-click opens the orb's menu, double-click opens the dashboard, and with click-through it ignores the mouse
  as well. Outside the capsule everything stays click-through. It draws above the orb's aura, sparks, embers, waves and
  rays; the Settings highlight ring passes beneath it.
- **Chip and undo**: while a plate shows, the source chip is folded into it. After 0.35 s of hover, during a drag and
  for 2 s after a sources change, the plate's width springs (response 0.3 s, damping 0.86, about its center) to the
  chip's content in the plate's own metrics — dot + name per source, statuses in the secondary color (`Gemini CLI ·
  未偵測到`) — while the text crossfades (0.15 s), then returns. When the content would be the same (`Codex`), nothing
  moves. After Add, Split or Merge the new orbs' plates fade in 0.28 s after landing and stay, in place of the 2 s
  chips; the undo chip stacks 4 pt beyond the first resulting orb's plate (above it when the plate is above, else under
  it), in the plate's metrics and material, with `orbs.undo` in the accent color.
- **Motion**: appear — opacity 0 → 1, 4 pt from the disc side → 0, scale 0.96 → 1, 0.28 s ease-out; disappear — fade
  0.18 s while moving 2 pt toward the disc. A new name crossfades (0.2 s) while the width springs (response 0.3 s,
  damping 0.86, about its center); added or removed dots scale in or out (0.2 s). A new placement fades out at the old
  slot (0.12 s) and in at the new one (0.2 s); a plate never travels across its disc. Reduce Motion: 0.15 s fades;
  widths and placements jump.
- **Inside the orb nothing changes**: value, title, agent dots, the traffic readout (section 10), the new-peak pill and
  every effect keep their places whether the plate is on or off.
- **Performance**: static — set with the platform text stack (Core Text / DirectWrite) into a cached layer, redrawn
  only when its name, appearance, placement or width changes, at 60 fps only during a morph or fade, otherwise no
  frames. The budgets of 2a are unchanged.
- **Accessibility**: not an element of its own. The orb's label already begins with its name (`a11y.orb.named`), so
  showing or hiding plates changes nothing for VoiceOver or Narrator, and a plate never shows anything the label and
  hint lack (statuses are in `a11y.orb.unavailable` and `a11y.orb.sources`).
- **Controls**: Settings → 懸浮窗 → master group: `orbs.names` popup (`orbs.names.auto` / `orbs.names.always` /
  `orbs.names.never`) with the caption `orbs.names.hint`. Orb editor: `orbs.showName` popup — `orbs.showName.auto`
  (filled with the current automatic result, `orbs.showName.on` or `orbs.showName.off`), a separator,
  `orbs.showName.on`, `orbs.showName.off`. Context menu and the quick menu's per-orb submenu: `orbs.showName` with a
  check mark on the effective state; choosing it stores the opposite as `true` / `false` for that orb (automatic is
  restored in the editor).

### Unavailable sources

A source is unavailable when it is disabled (Settings → Data Sources) or not detected (no folder and no data today).
An orb never deletes or rewrites its sources because of that.

- Some unavailable: the orb works with the rest; their bezel arcs are dashed and the chip greys them.
- All unavailable: the orb dims to 55 % of its opacity, shows the value `–` (en dash) in the idle value color and the
  title `source.disabled` if every source is disabled, else `source.notFound`; the instrument rests, there is no sign of
  life and no hover info line (the chip explains: `Gemini CLI · 未偵測到`). Accessibility: `a11y.orb.unavailable`.
  When a source comes back, the orb wakes through the normal start-up.
- An all-sources orb is never unavailable.

### Context menu (right-click on an orb)

Native menu (macOS `NSMenu` with leading check marks; Windows Fluent context menu), submenus open away from the screen
edge. Windows (this menu and the tray quick menu): every label starts in one text column whether or not the item has an
icon (icons sit in a fixed leading column), and toggle items show their check mark trailing, right-aligned in the same
column as submenu chevrons, so checked and unchecked items never shift the text:

1. Header (disabled): the orb's colour dots and name, followed directly by the separator. The menu shows no effort
   capsule or mode row (user's choice, 2026-10-28); agents' effort and speed stay on the dashboard's agent rows
   (section 4, Agents), boards and history. The orb's hover info line never mentions modes either.
2. `orbs.style` ▸ the five style names, check on the current one.
3. `orbs.sources` ▸ `orbs.sources.all`, separator, every source in source order with its colour dot and a check when
   tracked; unavailable ones show their status in secondary text and stay selectable. Unchecking the last tracked source
   is disabled (`orbs.sources.lastOne`). Checking `orbs.sources.all` replaces the set; checking a source on an
   all-sources orb makes the set that single source. It is followed by `orbs.unit` ▸ `orbs.unit.tokens` /
   `orbs.unit.cost` (section 9), `orbs.traffic` ▸ `orbs.traffic.sources` / `orbs.traffic.machine` /
   `orbs.traffic.intelligence` / `orbs.traffic.off` (section 10) and `orbs.showName`, checked when the orb's name
   plate shows (Name plate).
4. `menu.size` ▸ `menu.size.small` / `.medium` / `.large` (112 / 148 / 196; `minimal` sizes below 96 are set in
   Settings).
5. `menu.effects` ▸ Off / Soft / Standard / Max (global).
6. Separator; `menu.alwaysOnTop`, `menu.clickThrough` (this orb).
7. Separator; `orbs.add`; `menu.orb.duplicate`; `menu.orb.split` (enabled when the orb tracks two or more available
   sources, or all sources with two or more detected); `menu.orb.mergeAll` (enabled with two or more orbs).
8. Separator; `menu.orb.hide` (with one visible orb this item is `menu.hideOrb`, the master switch);
   `menu.orb.remove` (two or more orbs; asks `orbs.remove.confirm` / `orbs.remove.detail` with a destructive
   `orbs.remove.button` and Cancel).
9. Separator; `menu.orb.settings` (opens Settings → 懸浮窗 with this orb selected; macOS shows ⌘,); `menu.dashboard`;
   `menu.quit` (⌘Q).

### Split and merge

- **Split this orb** (menu): one orb per available source of its set (all detected, enabled sources for an all-sources
  orb), in source order. The first keeps the orb's id and position; the others are new orbs with the same style, size,
  opacity, toggles and title theme and automatic names. They form a tidy row extending toward the screen interior from
  the original position — a column when the original is docked to a left or right edge — with 12 pt gaps and aligned
  centers, wrapping into a second row or column when the display runs out; orbs in the way are pushed along the same
  axis.
- **Split by source** (Settings): replaces all orbs by one orb per detected, enabled source, styled like the selected
  orb, laid out from the first orb's position by the same rule. Disabled with `orbs.split.unavailable` when fewer than
  two sources are detected.
- **Merge** (Settings `orbs.merge`, menu `menu.orb.mergeAll`): replaces all orbs by one all-sources orb that keeps the
  selected (Settings) or clicked (menu) orb's id, style, size, toggles and position.
- **Motion**: split — the original scales to 0.85 and fades (0.2 s) while the children spring out of its center to
  their slots (stagger 60 ms, response 0.45 s, damping 0.78), their bezels draw on over 0.3 s and their chips appear
  0.28 s after landing for 2 s (with name plates showing, the plates appear then and stay). Merge is the reverse: the
  orbs fly to the target (0.3 s) and fade, the merged orb springs from 0.85 to 1. Reduce Motion: 0.2 s crossfades in
  place.
- **Undo**: split, merge and remove can be undone for 8 s and restore the previous list and layout exactly. In Settings
  an inline banner above the orb list (`orbs.didSplit` / `orbs.didMerge` / `orbs.didRemove` · `orbs.undo`); on the
  desktop an undo chip under the first resulting orb, stacked under its source chip, or beyond its name plate (Name
  plate). Removing with the Settings − button
  needs no confirmation because of the undo; the menu asks (it has no banner).

### Accessibility and keyboard

- Each orb window is one accessibility element. Label: `a11y.orb.named` (`Claude Code + Claude Desktop：每秒 128 個
  token，高鐵`); with a single all-sources orb, today's `a11y.orb`. Unavailable: `a11y.orb.unavailable`. Hint /
  help: `a11y.orb.sources` (`追蹤 Claude Code、Claude Desktop`; names joined with `list.separator`). Updated at most
  once per second.
- Actions (VoiceOver custom actions; UI Automation Invoke and the context menu key): `menu.dashboard`,
  `a11y.orb.showMenu`, `a11y.orb.toggleUnit` (section 9), `a11y.orb.moveCorner` (cycles top-right → bottom-right → bottom-left → top-left of its display,
  placed as a drag release, skipping occupied corners to the next free touching slot), `menu.orb.settings`.
- Keyboard: orbs never take focus; everything is reachable from Settings (below) and the quick menu's orb submenu.

### Engine

Per orb, from the per-source data the engine already keeps (METRICS 6.8): live value = the sum of the per-source live
rates of its sources; its own tier and title trackers (section 1 hysteresis and hold) with its redline; its agents and
`responseCompleted` events filtered by source; its own `peakToday` and `newPeak`. Editing an orb's sources recomputes
its peak and suppresses its celebration for 60 s, so changing settings never fires confetti.

## 2c. Leaderboard boards (排行看板)

A board is a second kind of floating window, next to the orbs (which stay exactly as they are): a rounded square that
ranks the user's agents by live speed, so the speed and what matters about every agent are visible at a glance. It
comes in three styles. The visual reference for each style is its prototype in `spec/prototypes/board/`
(`board-glass.html`, `board-timing.html`, `board-neon.html`, shared `sim.js` and `fx.js`): colors, radii, type sizes,
spacing and motion curves in those files are normative wherever this section does not give a value. The prototypes
render with HTML, SVG and canvas and stutter in a browser; the apps draw the same picture on the GPU (Frame policy,
below).

### Data model

Persisted next to the orbs as one JSON value (key `boards`), written atomically on every change:

```json
{ "version": 1,
  "boards": [ { "id": "…", "style": "neon", "size": "M", "sources": "all", "opacity": 1.0, "alwaysOnTop": true,
                "clickThrough": false, "allSpaces": true, "visible": true,
                "readouts": { "intelligence": true, "thinking": true, "cost": true } } ],
  "layouts": { "<arrangement key>": { "<board id>": { "display": "…", "xAnchor": "high", "x": 12, "yAnchor": "fraction", "y": 0.2 } } } }
```

| Field | Rule |
| --- | --- |
| `id` | UUID, never reused. |
| `style` | `glass` (流光玻璃) · `timing` (計時塔) · `neon` (霓虹座艙); unknown → `glass`. |
| `size` | `S` 280 × 280, `M` 340 × 340 (default), `L` 420 × 420 pt/DIP; unknown → `M`. |
| `sources` | As an orb's (2b): `"all"` or METRICS 1 keys; the board counts only those sources (METRICS 6.8). |
| `opacity`, `alwaysOnTop`, `clickThrough`, `allSpaces`, `visible` | As an orb's. |
| `readouts` | The live metrics switches (*Live metrics*, below): `intelligence`, `thinking`, `cost`; a missing field or key, or a value that is not a boolean, reads as `true`. A `rowCost` key saved by an earlier version is ignored and no longer written. |
| `layouts` | As the orbs' (2b, arrangement keys, position records, at most 16 arrangements). |

Global settings shared with the orbs: Appearance, Language, metric, General redline, title theme, Effects intensity,
Celebrate new peaks. Limits: 0–4 boards; a fresh install and an upgrade have none. `boards.add` (quick menu,
dashboard footer menu, Settings → 懸浮窗, an orb's context menu) creates one with the last used style (else `neon`),
`M`, `"all"`, every readout on, placed like a new orb (2b Placement) next to the window it came from; at 4 it is disabled with
`boards.limit`. Boards and orbs never overlap: placement and snapping treat both as obstacles, using the rounded
square (+12 pt) for a board.

### Content

All three styles show the same content; the style decides its look (Styles, below). Metric: the selected metric
(output or total, METRICS 6.3); heat, tiers and title follow section 1 with the General redline and title theme.

1. **Hero**: the board's live value (METRICS 6.8, `live_A`, formatted per METRICS 7, tabular digits), the unit, the
   speed title in heat color, `agents.context` (generating and tools agents), and a history of `live_A`: glass a 60 s
   sparkline, timing 30 one-second bars, neon a 24-segment LED bar lit to the heat with today's peak marked. With the
   live metrics on (the default), the hero's right side is the sub-gauge — 性能, 思考 and 費用 — and glass and timing
   draw their history as a full-width strip under the hero (*Live metrics*, below).
2. **Ranking**: the agents of the board's sources (METRICS 6.7 list rules, filtered by 6.8), grouped by their root: a
   group is a root agent and all its descendants (a sub-agent whose parent is not listed is its own root). Group speed
   = the sum of its members' live values. Groups are ranked (Ranking stability, below); each group row shows the rank,
   the root's phase badge in its source color (section 4), its project, model and `boards.subCount` when it has
   sub-agents, the root's effort capsule (section 4, Agents), the group speed, and its share of `live_A` (glass and neon:
   a glowing underline from the name to the value; timing: the interval to the group above, `boards.leader` on the first).
   Under a group, its fastest generating sub-agents (smaller rows with their own capsule and value), then
   `boards.moreSubs` (`+97 個子 agent · 8.6k`) for the rest. When groups do not fit, the last line is
   `boards.moreGroups` with their summed speed. With the live metrics on, every group and sub row also shows its
   性能 (section 12) in a column before the value. At M and L each group row has a second line with its model and
   `boards.subCount` under the project (*Live metrics*, *The second line*).
3. **Footer**: `boards.today` (today's output of the board's sources), today's cost (section 9) and `boards.peak` (`peakToday_A`).

**Layout per size.** S: up to 4 groups (3 fit with the live metrics on), no sub rows, capsule without its word. M: up to 6 groups; sub rows two for #1,
one for #2 and #3. L: up to 8 groups; three sub rows for #1–#3, two for #4–#5. Groups are placed first and sub rows
only fill the room that remains, so a sub row never pushes a group out; no row is ever cut in half. Clicking a group
toggles showing up to 8 of its sub-agents in that space (others collapse); the expansion ends after 60 s without
interaction.

**Ranking stability.** A group passes the group above it only after leading it by more than 10 % + 1 tok/s on two
consecutive engine ticks (1 s), or immediately when it leads by more than 2× + 20; groups that are all below 0.05
order by latest activity. So the 3 s live window's wobble never reshuffles the board, and the displayed values agree
with the order except inside that 10 % band. A group new to the board enters at the place of its value; groups that
leave are dropped. Only passing emits an event: `overtake` (climber, from, to; ranks before and after the passes of
that tick, ignoring places gained because others left or entered) and `newLeader` when the climber reaches first.
Sub rows sort by live value, latest activity, id; they emit nothing. Shared cases:
`spec/fixtures/board-ranking-cases.json`, generated and checked by `spec/tools/board_ranking.py`.

### Live metrics (性能 · 思考 · 費用)

User's choice (2026-10-28): layout **B, 副錶**, in every style. The right side of the hero becomes a small
sub-gauge — the board's 性能 (the highest performance score of its agents, section 12) large, its thinking rate and its
cost per second smaller beneath it — every ranking row gets a 性能 column, and at M and L each group row gets a second line
with its model. 費用 is the board's only live cost: no row, sub row, tooltip or score card carries a cost of its own
(user's choice, 2026-10-28: the board does not need a live cost per agent; the earlier `rowCost` switch, the cost on
the second line and the tooltip's cost lines are retired). Reference: `spec/prototypes/board/board-metrics.html`, which
opens on B with every switch on (its options A and C are the alternatives that were not chosen); where it and this
section differ, this section wins. 性能 replaced the 智力 readout of that prototype in the same places (user's
decision, 2026-10-29); its numbers, log arc, meter, states and card are drawn in `spec/prototypes/perf-score/index.html`. Numbers are
pt (DIP on Windows) on boards of side S 280, M 340, L 420, from the board's top-left corner unless a row says
otherwise; baselines are rounded to 0.5 pt from the macOS fonts of the prototype.

**Values.**

| Readout | Value | Shown as | Label |
| --- | --- | --- | --- |
| 性能 | METRICS 6.9 over the board's agents `A` (6.8): the highest live score, else the highest resting score, else none | integer (section 12, *Number formats*: compact in glass's dial, full in neon and timing); resting pale; none → `–`; the provisional mark under a provisional score (section 12) | `intel.glyph` (性能 / 性能 / Perf / 性能) |
| 思考 | METRICS 6.9, *Live thinking*: `liveThinking` over `A` | METRICS 7 · `unit.tokS` | `intel.thinking.glyph` (思考 / 思考 / TH / 思考) |
| 費用 | METRICS 9.5 `liveCost` and `liveCostKnownShare` over the billable sources of `A` | section 9 rate form in the display currency (METRICS 9.8) · `cost.unitPerSecond` | `cost.glyph` (費用 / 费用 / Cost / 費用) |
| row 性能 | group row: METRICS 6.9 over the group (its highest member score); sub row: the sub-agent's own value | compact form; live, resting (pale) or none, with the provisional mark (section 12) | caption `intel.glyph` |

- Every cost on a board follows `settings.cost.includeSubscriptions`, like every other displayed cost (METRICS 9.4):
  with it off (the default) a source marked as a subscription adds nothing to 費用 or to the footer's today cost.
- `≥` precedes a cost whose known share is below 0.995 and which is not 0 (section 9), at 0.85 × the number's size in
  `ink-3` — the orb's 60 % is meant for its large value and would fall under 7 pt here. Nothing priced (output > 0,
  known share 0) → `–`. A free or local model is a real `0`.
- When every source of `A` is excluded as a subscription, 費用 and the footer's today cost show
  `cost.subscription.tag` in place of a number (UI font, weight 600, 0.8 × the number's size, `ink-3`, no unit). The
  orb shows `0` and explains it in its hover line; a board's hero has no hover line, and `0 US$/s` beside busy agents
  would read as free.
- None of these values drives heat, tiers, titles, peaks or celebrations.

**Switches.** `readouts` (data model) holds three independent switches, all on for a new board and for a board saved
by an earlier version:

| Key | Item (menu and editor) | Shows |
| --- | --- | --- |
| `intelligence` | `boards.readouts.intel` | 性能 in the sub-gauge, and the rows' 性能 column with its captions line |
| `thinking` | `boards.readouts.thinking` | 思考 in the sub-gauge |
| `cost` | `boards.readouts.cost` | 費用 in the sub-gauge |

- Context menu: `boards.readouts` ▸ the three items in this order, each with a check, after `orbs.sources` ▸; choosing
  one toggles it and saves at once. Settings → 懸浮窗, board editor: a `boards.readouts` group with the same three
  checkboxes and the caption `boards.readouts.hint`; the style thumbnail shows the readouts as switched. Add copies
  nothing: a new board has every switch on.
- A readout switched off takes no room; what remains closes up and re-centres (*Folding*, below). With `intelligence`,
  `thinking` and `cost` off the hero is exactly the board without metrics (history on the right, neon's hero
  82 / 92 / 100); with `intelligence` off the rows lose the 性能 column and the captions line and take the columns of
  the boards without metrics again (value 46; timing's interval 38); with all three off the board is exactly as before
  this section, except that group rows at M and L keep their second line (S is exactly as before).
- A change applies at once: the hero crossfades in 0.2 s and rows move to their new places with the style's spring
  (rows that enter fade in, as always). Reduce Motion: the crossfade only.

**Hero.** While at least one of `intelligence`, `thinking` and `cost` is on, the hero has two columns 8 apart: the
left keeps the value, the unit, the title and `agents.context` (the context truncates first, then the title); the right
is the sub-gauge, right-aligned at the side inset and centred vertically as below. Glass and timing move their history
to a full-width strip under the hero; neon keeps its LED bar.

| | Glass S / M / L | Timing S / M / L | Neon S / M / L |
| --- | --- | --- | --- |
| side inset | 14 / 16 / 18 | 12 / 14 / 16 | 14 / 16 / 18 |
| hero band: top, height | 12, 78 / 14, 86 / 16, 94 (as before) | 14, 72 / 16, 80 / 18, 88 (as before) | 12, 92 / 14, 100 / 16, 108 (8 more at M and L, 10 at S) |
| sub-gauge: nominal width | 92 / 112 / 128 | 110 / 134 / 152 (the data box) | 92 / 112 / 128 |
| sub-gauge: centred in | the band | the band | the band less its bottom 17 (the LED bar: 7 tall, its bottom 6 above the band's) |
| history | strip from inset to inset, right under the band, 14 / 16 / 18 tall | strip 8 under the band, 10 / 12 / 12 tall | the LED bar, as before |
| ranking starts | at the strip's bottom | 5 under the strip | 4 under the band |
| ranking height | 138 / 186 / 254 | 139 / 187 / 257 | 136 / 186 / 256 |

- Glass strip: the 60 s sparkline of `live_A` stretched over the strip (scale `max(10, window maximum) × 1.12`, 1.5 pt
  free above and below), a 1.25 pt line in the heat color, the area under it the heat color from 28 % at the top to
  0 at the bottom, no end dot.
- Timing strip: the 30 one-second bars, 3 wide, radius 1, spaced so the first starts at the left inset and the last
  ends at the right inset; height `max(1.5, value / max(10, maximum of the 30) × strip height)`, the heat color at
  85 %.

**Sub-gauge, glass (光環副錶)** — a pearl arc around 性能, the table centred under it. Vertical numbers from the
sub-gauge's top:

| | S | M | L |
| --- | --- | --- | --- |
| dial `D` (a `D` × 0.742 `D` box, centred) | 54 | 66 | 74 |
| arc radius, stroke, tip radius | 23.7, 2.45, 1.9 | 29, 3, 2.3 | 32.5, 3.4, 2.6 |
| 性能 label: size, baseline | 7.5, 19 | 8.5, 22 | 8.5, 24 |
| 性能 number: size, baseline | 13, 35.5 | 17, 42.5 | 19, 46.5 |
| table: number size; label and unit size | 9.5; 7.5 | 11; 8.5 | 12; 9 |
| table baselines: 思考, 費用 | 51, 68 | 62, 79 | 69, 86 |
| height (思考 hidden) | 75 (58) | 84 (67) | 90 (73) |

- Arc: centre at (`D`/2, `D`/2) of the dial box, radius 0.439 `D`, stroke 0.0455 `D`, round caps, from 150° (lower
  left) clockwise over the top to 30° (lower right), 240° in all (angles with y down). Track: the whole sweep in
  `line-2` (dark white 14 %, light black 11 %). Value: the first `f` of the sweep, `f` the log scale of section 12
  for `shown`, the displayed integer (250 → empty, 1000 → half, at the top of the dial, 4000 and above → full) — stroked with a linear gradient across the dial box from its
  lower-left to its upper-right corner, `pearl-c` → `pearl-a` → `pearl-b` (dark `#A5F3FC` → `#C7D2FE` → `#F5D0FE`,
  light `#1AA3C4` → `#6D7CF0` → `#C05BD9`), with a 3 pt glow of `pearl-b` at 60 %. Tip: a dot of radius 0.035 `D` on
  the value's end, white with a 3 pt `pearl-b` glow (light: `pearl-b`, no glow). `f = 0` draws no value arc; none
  draws neither the value arc nor the tip. Resting (section 12): the value arc and the tip at 35 %, without glow.
  Provisional (section 12): the value arc is drawn as round dots (dash 0, gap 2 × stroke) and the tip as a hollow ring
  (stroke 0.4 × its radius, no fill), so a young score never looks settled.
- Reference tick: at the top of the sweep (270°, `f = 0.5`, the reference 1000), a radial line inside the track, from
  1 pt inside its inner edge 0.05 `D` toward the centre, 1 pt wide, round caps, `ink-3` (Increase Contrast `ink-2`;
  high contrast the gray-text color). It belongs to the track: always drawn, never animated.
- 性能 label weight 600, tracking 0.1 em, `ink-3`; number SF Pro Display / Segoe UI Variable Display, weight 650
  (Windows Semibold), tracking −0.02 em, tabular, compact form, `ink` (resting and `–`: `ink-3`; the provisional mark
  under it). Each is centred on its own, so the number moves only when its width changes. The number sizes are fitted
  so that `8,888`, the widest compact value, keeps 2 pt clear of the track's inner edge at its baseline (they were
  16 / 19 / 21 for `888`); Windows fits Segoe UI Variable Display by the same rule.
- Table: rows 15 tall, 2 apart, the first 3 under the dial box. Labels weight 600, tracking 0.04 em (S 0.02 em),
  `ink-3`; numbers weight 600, tabular, `ink`; units weight 500, `ink-3`. Tokens: `ink` dark `rgba(244,246,252,.96)`,
  light `rgba(17,20,30,.94)`; `ink-3` dark `rgba(226,230,242,.38)`, light `rgba(30,36,52,.40)`.

**Sub-gauge, timing (資料盒)** — a broadcast data box: label tabs on the left, every value on one right edge.

| | S | M | L |
| --- | --- | --- | --- |
| box width | 110 | 134 | 152 |
| row height: 性能; others (with a 1 pt separator at their top) | 21; 15 | 24; 17 | 27; 19 |
| tab label size, horizontal padding, tracking | 8, 4, 0 | 9, 6, 0.06 em | 9, 6, 0.06 em |
| number size: 性能; others | 15; 10.5 | 17; 11.5 | 19; 12.5 |
| unit size, gap before it; right padding | 7, 2; 5 | 8, 3; 7 | 8, 3; 7 |
| baselines from the box's top: 性能, 思考, 費用 | 15.5, 32, 47 | 17.5, 36.5, 53.5 | 20, 41, 60 |
| height (思考 hidden) | 51 (36) | 58 (41) | 65 (46) |

- Box: radius 5, fill `slab-2` (dark `#12151D`, light `#F1F3F7`), a 1 pt inner stroke and the separators in `line-2`
  (dark white 10 %, light black 10 %). Tab column: as wide as the widest switched-on label plus its padding, fill
  `block` (dark `#1B1F2A`, light `#E6E9F0`); labels UI font, weight 700, centred in their tab, `ink-2` (dark
  `rgba(232,236,245,.66)`, light `rgba(14,17,24,.62)`), 性能's `block-ink` (dark `#E8ECF5`, light `#1A1E28`). Latin
  labels are upper-case (`PERF`, `TH`, `COST`), like the style's other labels.
- Values: mono, weight 800, tabular, tracking −0.03 em (性能 −0.04 em, full form: `99,999` fits the row at every
  size), `ink` (dark `#F5F7FB`, light `#0E1118`; 性能 resting and `–`: `ink-3`, with the provisional mark of section 12
  under a provisional score); the
  number's box (1.1 × its size) is centred in the row below its separator, and the unit (mono, weight 700, upper-case:
  `TOK/S`, `US$/S`; `ink-3`) and `≥` sit on the number's baseline. A value too wide for its row widens the box to the
  left at once and narrows it 6 s after the last value that needed it.

**Sub-gauge, neon (LED 副錶)** — lit LED digits over ghost segments, a 12-segment meter, the table under it.

| | S | M | L |
| --- | --- | --- | --- |
| 性能 label: size, tracking, baseline | 7.5, 0.18 em, 6 | 8.5, 0.24 em, 7 | 8.5, 0.24 em, 7 |
| 性能 number: size, baseline | 21, 28.5 | 24, 32.5 | 28, 35.5 |
| meter: width, top | 62, 36.5 | 74, 41 | 84, 45.5 |
| table: number; label; unit size | 9.5; 7.5; 7.5 | 10.5; 8.5; 8 | 11.5; 8.5; 8 |
| table: gap under the meter; between rows (rows 15 tall) | 3; 2 | 6; 3 | 6; 3 |
| table baselines: 思考, 費用 | 50.5, 67.5 | 59, 77 | 64.5, 82.5 |
| height (思考 hidden) | 74.5 (57.5) | 83 (65) | 87.5 (69.5) |

- 性能 label: UI font, weight 600, `ink-3` (dark `rgba(214,224,255,.36)`, light `rgba(10,13,24,.40)`). Number: mono,
  weight 700, tracking −0.03 em, `led` (dark `#EAF2FF` with the LED glow — 4 pt `rgba(150,190,255,.75)` and 12 pt
  `rgba(120,160,255,.40)`; light `#141A33`, no glow), in full form (`8,888` at M is 69, inside the 74 meter), over ghost
  segments: the shown text with every digit as `8` and the comma kept (`8,888` behind `1,286`), in `ghost`
  (dark `rgba(160,190,255,.075)`, light `rgba(20,30,80,.06)`).
- Meter: 12 segments, 3 tall, 2 apart, radius 1, centred, 3 under the number's box (1.1 × its size). `lit =
  round(f × 12)` segments from the left, `f` the log scale of section 12 for the shown value (6 lit at 1000, all 12 from
  4000); lit `mind` (dark
  `#CFE0FF` with a 5 pt glow of itself, light `#3D4F9A`, no glow), unlit `unlit` (dark `rgba(140,170,255,.16)`, light
  `rgba(20,30,80,.14)`).
- Resting (section 12): the digits in `ink-3` and the lit segments at 35 %, both without glow. Provisional: the lit
  segments are hollow (a 1 pt inner stroke of `mind`, no fill, no glow) and the digits carry the dotted underline.
- Table: labels UI font, weight 600, tracking 0.06 em, `ink-3`; numbers mono, weight 700, tracking −0.02 em, `led`
  (dark: a 6 pt glow `rgba(140,180,255,.45)`); units mono, weight 600, `ink-3`.

**Table cells** (glass and neon). The table is one block, centred as a whole on the sub-gauge's centre line, with
fixed cells so that values never move it: the label cell (the widest label of the switched-on table rows in the
current language, then 4 pt; S 3), the `≥` cell (always reserved: `≥` at 0.85 × the number's size, then 1 pt), the
number cell (as wide as `0.00088` in the number font; numbers right-aligned in it) and the unit cell (3 pt, S 2, then
the widest unit of those rows: `tok/s`, or `cost.unitPerSecond` with the current symbol). A text wider than its cell
widens the cell at once and narrows it 6 s after the last value that needed it (section 3's rule for the value). The
sub-gauge is as wide as its nominal width or its widest line, whichever is more, and grows to the left. Widths are
measured when the language, currency, font or switches change, never per value (macOS, zh-Hant, USD: the table is
105.5 wide in glass and 100 in neon at M, 92 and 89 at S).

**Folding.** Switched-off readouts take no room; the rest closes up and the sub-gauge re-centres in its band. Cells
are sized for the switched-on rows. With `intelligence` off, glass and neon show the table alone (no dial, no meter)
and timing's box starts with 思考 at the small row height; with only `intelligence`, the dial (glass), the digits and
meter (neon) or the 性能 row (timing) remain.

**Thinking hidden at zero** (section 10, *Performance readout*; user's choice, 2026-10-28). While `thinking` and at
least one other hero readout are on, 思考 shows only while its displayed value is above zero. When the board has a
value (`Σ w > 0`, METRICS 6.9, *Live thinking*) and the shown 思考 reads zero (`0`, or `0.0` below 0.05 tok/s) for 3 s, it fades out in place
(0.25 s) while what is below it glides up and the sub-gauge glides to its new centred place (smoothstep, 0.3 s); a
shown value above zero brings it back at once (fade in 0.25 s, the same glide back). Reduce Motion: at once. The quiet
state (no value) changes nothing: a hidden 思考 stays hidden, a shown one reads `–`. Switching `thinking` on shows it.
With 思考 the only hero readout it never hides — the sub-gauge would be empty and the hero would change its layout at
every pause. The cells and widths stay, so nothing moves sideways:

- Glass: 費用 takes 思考's baseline and the sub-gauge is 17 shorter (S / M / L 58 / 67 / 73): the dial moves down 8.5
  and 費用 moves up 8.5.
- Neon: the same, 17 / 18 / 18 shorter (57.5 / 65 / 69.5).
- Timing: the 思考 row leaves the box, 費用 sits right under 性能, and the box, 15 / 17 / 19 shorter (36 / 41 / 46),
  re-centres.
- Accessibility then reads `a11y.orb.intel.only`.

**Updates, motion and quiet.**

- The readouts are evaluated on every engine tick (500 ms) together with the hero value, so they change at most twice
  per second and in the hero value's frame. 性能 follows section 12, *Motion*: a change of at least 1 % and 3 counts
  through the integers, up or down (the arc's end and the meter follow the shown value).
  The other readouts' changed characters roll like the hero's: 0.24 s, cubic-bezier(0.2, 0.8,
  0.2, 1), each entering character starting with a 1.4 pt blur that is gone by 0.24 s, upward when the value rises and
  downward when it falls; no white-hot flash, punch or embers (they stay the hero value's). A change in the number of
  characters, or between `–` or the tag and a number, crossfades in 0.15 s instead. On a change too small to count, the arc's
  end and tip move to the new value in 0.24 s on the same curve and the meter's segments crossfade in 0.24 s, so everything shares the frames
  of the hero's roll. Reduce Motion: 0.15 s crossfades; the arc and the meter jump.
- Row values (the 性能 cell) change in place on the tick with the row, without roll, count or flash, up or down
  (cached row content, *Frame policy*); a change between live and resting crossfades the color in 0.2 s (an opacity change, kept with Reduce Motion).
- Quiet (`Σ w = 0` over `A`): 思考 reads `–` and 費用 shows its real `0`. 性能 keeps its own form (section 12): live
  while an agent of `A` is not idle (an agent running a tool generates nothing and is still live), the highest
  resting score in pale after work, `–` with an empty arc and meter (track and unlit segments only) when no agent of
  `A` has a score. `–`, `0` and the tag are drawn in `ink-3` without glow. The sub-gauge stays, like a system
  meter (section 10, *Menu bar and taskbar*), so the hero never changes its layout when work stops.
- No continuous animation is added: an idle board with every readout on is static.

**Ranking rows.** The 性能 column — every group and sub row, between the effort capsule column and the value,
while `intelligence` is on:

| | Glass | Timing | Neon |
| --- | --- | --- | --- |
| cell width: one-line rows; two-line group rows (M, L) | 33; 39 | 34; 40 | 34; 40 |
| row columns (gap between them) | rank 16, badge 10, name, capsule, 性能, value 44 (8) | position 24, stripe 3, name, capsule, interval 34 (none at S), 性能, value 42, S 40 (7) | as glass |
| number: group row; sub row | 11; 10.5, SF Pro, weight 600 | 11; 10, mono, weight 700 | 11; 10, mono, weight 600 |
| live | `ink-2` | `ink-2` | `led` at 78 %, dark glow 6 pt `rgba(140,180,255,.45)` |
| resting, `–` | `ink-3`, weight 500 | `ink-3`, weight 600 | `ink-3` |

- Right-aligned, tabular, on the value's baseline (in a two-line group row both are centred on the row, *The second line*). Compact form (section 12). Group row: the group's value (METRICS 6.9: the
  highest live score of its members, else the highest resting score, else none), so a group whose root only
  delegates still shows what its busiest sub-agent achieves; sub row: its sub-agent's own value. Live
  in the table's live color, resting and none in its resting color; the provisional mark (section 12) under a
  provisional score — a group row's follows the member that supplies the value. The `boards.moreSubs` and
  `boards.moreGroups` lines leave the cell empty. Live is one step quieter than section 4's column (here the speed is
  the headline).
- The cell is at least as wide as `8,888` in its font and as the caption (the widths above, macOS fonts: it was 20–26
  wide for `888`); it never truncates, and the name block gives
  way (the detail first). Timing drops the capsule's word below L while the column shows (meter and bolts stay; the
  word stays in the tooltip); S drops it in every style, as before.
- Captions line: at the top of the ranking, 12 tall (timing 14), the first row 2 under it; `intel.glyph` right-aligned
  over the 性能 cells and `unit.tokS` over the values (timing also `boards.gap` over the interval column, except at
  S), all `ink-3`; a 1 pt `line` hairline from the rank column to the end of the capsule column, centred on the line
  (it replaces glass's top hairline). Glass: UI font 9, weight 600, tracking 0.06 em; timing: mono 8.5, weight 700,
  tracking 0.1 em, upper-case; neon: mono 8.5, weight 600, tracking 0.12 em.

The second line — every group row at M and L, whatever the switches:

- Row height `row` + 8 (38 at M, 40 at L; glass and neon then leave out their 2 pt between group rows). Line 1, its
  baseline 16 under the row's top: rank, badge, project name, capsule; the capsule sits in the first 16 pt band
  starting 4 under the top, outside the baseline group, so every first line shares one baseline.
- **The row's figures**, the 性能 cell and the value (and timing's interval), are not on line 1 (user's request, 2026-10-29: they read as the
  row's numbers, centred and a little larger). They share one baseline, placed so that the cap-height centre of their
  digits lies on the row's centre line (19 under the top at M, 20 at L). Both are 2 pt larger than in a one-line row:
  the 性能 number of a group row 11 → 13 (same weight and colors), the value its style's row size + 2. Each cell
  widens to fit `8,888` (性能) and the widest value in the new size; the name block gives way first, as before. S rows
  and sub rows (one line) keep their sizes and line 1's baseline.
  Line 2, its
  baseline 31 under the top (glass 32): the detail (model · `boards.subCount`) alone, starting under the name and
  running under the capsule column up to the first figure column's left edge (the figures, centred on the row, hold
  their columns on both lines), truncating at its tail. Timing's position block (26 tall) and stripe (24) span both
  lines, and timing's interval column is a figure too, centred on the row at its own size on its own baseline (on
  the shared one its smaller digits would sit 1.3 pt off the centre line). The keyboard focus ring (section 12, *Keyboard*) is drawn outside the row like a system
  control's and may overlap the 1–3 pt gap to a neighbouring row.
- Nothing else is drawn on line 2: rows show no cost, whatever the readouts.
- S never draws the second line: rows stay one line.

**Rows kept** with the readouts on, by the existing rule (groups first while they fit, sub rows only in the room
left; the `team` scenario of `sim.js`, as the prototype shows it):

| S | M | L |
| --- | --- | --- |
| 3 groups + `boards.moreGroups` | 4 + `boards.moreGroups` | 5 (timing: and 1 sub row) |

**Tooltip and score card.** While `intelligence` is on, the tooltip of a group or sub row is the score card of
section 12 for the agent whose score the row shows (a group row with none: its root), with the row's existing lines
(*Interaction and window*) in its facts block; with `intelligence` off it stays text. Neither names a cost, whatever
the switches. Hovering the hero's 性能 for 0.35 s (glass: the dial; neon: the digits and meter; timing: the 性能 row of
the box) shows the card of the agent that supplies the hero's value, with the eyebrow `perf.card.top` when more than one
agent of `A` has a score in that form; nothing while the value is none.

**Accessibility.**

- The board's label (`a11y.board`) goes on, joined with `，` / `, `, with what is switched on, in this order:
  `a11y.orb.intel` (性能 and a shown 思考), `a11y.orb.intel.only` (思考 off or hidden) or `a11y.board.thinking` (性能
  off), with the score words of section 12, *Accessibility* (provisional, idle, the highest of N agents), and
  `intel.none` in place of either 性能 form while 性能 is none; then `a11y.board.cost` (the
  number as shown, wrapped in `a11y.orb.costAtLeast` when partial, and the currency's name `currency.<code>`),
  `a11y.board.costUnknown` or `cost.subscriptionOnly`. Example: `排行看板，全部來源：每秒 707 個 token，噴射客機，性能
  1,286，4 個 agent 中最高，思考每秒 174 個 token，費用每秒 0.024 美元（API 等值）`. The 1-second update limit stays.
- A row's label (`a11y.board.row`) adds `a11y.agent.intel` with the row's score words (section 12; a group row's
  wrapped in `a11y.perf.top` when more than one member has a score in that form) right after its speed (nothing for
  none) while `intelligence` is on, and offers the custom action `a11y.perf.card`; it names no cost. Sub rows add the
  same 性能 words.
- Nothing relies on color: resting scores are announced with `a11y.perf.idle`, the provisional mark with
  `a11y.perf.provisional`, `≥` as `a11y.orb.costAtLeast`.
- Increase Contrast: no glows (arc, tip, LED digits, meter, row digits) and no ghost segments; quiet values (`–`,
  resting scores, `0`, the tag) in `ink-2` instead of `ink-3`; the provisional mark's dots at 100 %; glass's track `ink-3`, its value arc and tip `ink`
  without the gradient; neon's lit segments `ink`, unlit `ink-3`; timing's box stroke 1 pt `ink-3`. Windows high
  contrast: numbers in the text color, the value arc and lit segments in the highlight color, labels, units, tracks,
  the reference tick and quiet values in the gray-text color, no fills.

**Frame policy.** The readouts are content (cached text and shapes, redrawn on a data change, at most twice per
second). Their rolls, the arc's move and the meter's crossfade start on the tick with the hero value and end within the
hero roll's 0.24 s, so they add no frames of their own; 性能's count (section 12) runs in the roll class, 60 fps for
at most 0.7 s, only when the shown score changes by at least 1 % and 3 (an event per response or tool result, never a
steady state); the thinking fold and a switch change run at 60 fps for their
0.3 s, like a row move; a quiet or idle board renders nothing. The budgets of *Frame policy and performance* (below) are
unchanged, and each platform's benchmark measures boards with every readout on.

### Motion and effects (all styles)

- **Rows** move to new places with a spring (glass: critically damped, settle ≈ 0.45 s; timing: damping 0.72,
  slight overshoot; neon: 0.9) driven by the compositor; entering rows fade in 0.3 s from 10 pt below, leaving rows
  fade out 0.3 s. Ranks roll vertically. Values update at the engine tick; a value that rises more than 5 % flashes
  warm (white-hot then its heat color, 0.8 s), one that falls more than 5 % dims briefly.
- **Hero number**: changed digits roll in (0.24 s) with a short motion blur; when rising they flash white-hot. A rise
  of more than 8 % + 20 tok/s in one tick punches the number (scale 1.06, 0.4 s) and releases embers (glass: rising
  sparks; timing: a horizontal spray; neon: rising sparks plus a lightning arc across the digits). A new `peakToday_A`
  sweeps a gold sheen across the digits, pops `boards.newPeak` above them for 2.2 s and sends a gold ring from the
  number.
- **Perimeter**: comets race around the rounded square, clockwise, evenly spaced, each a bright core, a glow and a
  fading tail in the heat color (white-hot core above h = 0.85). Count by effect tier: idle 1 (dim), cruise 1,
  boost 2, overdrive 3, redline 4 (timing 1/1/2/2/3; neon 1/2/3/4/4). Lap time 7 s at idle down to 1.6 s at the
  redline. From h = 0.55 a full ring lights and blooms around the board; at the redline sparks fly outward from each
  comet head every 120 ms. A bloom outside the board grows with heat: the prototypes' `box-shadow` (blur `bloom × B`,
  spread `bloom × S`; `bloom` = 0.15 + 0.6 h + 0.4 overdrive, B / S = 70 / 6 for glass and timing, 80 / 8 for neon), a
  Gaussian edge of σ = bloom × B / 2 beyond the spread (never reduced), multiplied by (1 − (d / R)²)² for the distance
  d outside the rounded square, R = 38 pt (the window's 40 pt margin less 2). So it fades out along the board's rounded
  outline and is gone before the window's edge, which never cuts it into square corners.
- **Overtake**: a light sweep crosses the climber's row in its source color, streak particles fly off along it, a
  burst leaves its rank and `▲n` pops before its value for 1.6 s (glass mint chip, timing green delta, neon mint
  chip); timing's position block flashes green; neon arcs lightning from the climber's rank to the row it passed.
- **New leader**: a gold shockwave and a white one expand from the first rank across the board, ~70 gold sparks burst,
  and the comets turn gold and run 1.8× faster for 2.2 s; timing slides its `boards.newLeader` banner across the
  hero; neon strikes lightning from the top edge to the rank and flashes the board.
- **Style ambience**: glass — a heat-tinted aurora drifting behind the hero (19 s and 23 s cycles); timing — a heat
  rail along the top edge and a pulsing `boards.live` dot; neon — a breathing neon tube on the edge (cycle 3.2 s → 1 s with
  heat), hyperspace streaks behind the board from h = 0.2 (count 20 → 90 and speed with heat), and chromatic
  fringes on the number from h = 0.85.
- **Effects intensity** (section 2): Off — no comets, particles, bolts, streaks or ambience, rows still move;
  Soft — one comet at most, no particles or warp; Standard — as above; Max — 1.5× particles and warp density.
  Reduce Motion: rows and values crossfade, nothing travels or bursts. `Celebrate new peaks` off removes the
  new-peak and new-leader celebrations (rank changes still animate).

### Frame policy and performance (normative)

The board shares the orbs' frame clock and budgets (2a, Frame policy) with these classes: idle (nothing generating,
no effect alive) — static, no frames; cruise/boost — 30 fps; overdrive/redline or any burst, wave, bolt, celebration,
row move or digit roll alive — 60 fps until it ends; value flashes and dims run in the current class (30 fps at
cruise). "60 fps" and "30 fps" are the divisors of the display's refresh rate of 2a (72 and 36 on 144 Hz). Hidden,
occluded or faded boards render nothing. Budgets per board (M, Standard, release build, measured after start-up and
JIT warm-up): idle ≤ 0.3 % CPU; cruise ≤ 3 % (macOS) / ≤ 5.5 % (Windows, whose presents cost about 0.45 ms of CPU
each on the reference machine; the user chose to keep digit rolls at 60 fps during cruise, which costs about 1.5 % on
Windows); redline with bursts ≤ 6 % (macOS) / ≤ 8 % (Windows); GPU ≤ 1.5 ms per frame at 2× / 200 %;
memory the board owns (layer textures, swap chain or drawable surfaces, instance buffers) ≤ 24 MB.

- **Two layers.** Content (text, badges, capsules, values) is laid out and rasterized only when data changes (≤ 2 per
  second) and cached per row; frames only composite the cached rows at their animated positions. Effects (comets,
  ring, bloom, sparks, streaks, waves, bolts, warp) are primitives of the orbs' instanced SDF pipeline (2a: rounded
  rect, circle, ring, capsule, radial, polyline), glows analytic, particles from preallocated pools. No per-frame
  allocation, no blur passes, no per-frame text layout.
- **macOS**: an `NSPanel` (non-activating, like the orbs); rows as Core Animation layers (positions animated by the
  render server), effects in a `CAMetalLayer` overlay driven by the shared display link. The glass backdrop is
  `NSVisualEffectView` (Liquid Glass on macOS 26).
- **Windows**: no WPF layered window for animated content (2a). The board draws with Direct2D/DirectWrite and Direct3D
  11 into a composition swap chain hosted by DirectComposition in a `WS_EX_NOREDIRECTIONBITMAP` window, like the GPU
  orbs; rows are cached D2D bitmaps redrawn on data change. Glass on Windows 11 uses an acrylic-like blur of the
  backdrop only if it costs nothing per frame (system backdrop); otherwise a tinted translucent fill.
- Each platform's benchmark harness (2a) gains the three board styles in the idle → climb → redline → overtake →
  new leader scenario of `sim.js` and reports these budgets.

### Interaction and window

- Drag anywhere to move; release within 24 pt of a screen edge snaps to a 12 pt margin, as orbs do; boards never take
  focus. Hover a group row: a tooltip after 0.35 s — project, source, model, effort and speed (section 4 tooltip
  lines), group speed and its sub-agents' share, today's output of the group, then the live metrics lines (*Live
  metrics*, Tooltip and score card); while `intelligence` is on, these lines are the facts block of the row's score
  card (section 12), which sub rows get too. Hover lifts the row tint only.
- Context menu: header (style name and the board's sources); `boards.style` ▸ the three styles; `orbs.sources` ▸ as
  an orb's; `boards.readouts` ▸ the three live metrics switches (*Live metrics*); `menu.size` ▸ S / M / L; `menu.effects` ▸ (global); `menu.alwaysOnTop`, `menu.clickThrough`;
  `boards.add`, `boards.remove` (asks `boards.remove.confirm`); `menu.dashboard`; `boards.settings` (Settings →
  懸浮窗, Boards).
- Settings → 懸浮窗 gains a Boards list under the orbs: one row per board (style preview thumbnail, style name,
  sources, visible switch) with add / remove, and an editor for style, size, sources, the live metrics switches
  (*Live metrics*), opacity and the window toggles.
  The quick menu gets `boards.show` / `boards.hide` (all boards) and `boards.add`.
- Accessibility: a board is a list element labelled `a11y.board` (sources, value, title); each group row is an item
  labelled `a11y.board.row` (`第 1 名，tihu，每秒 7.88k 個 token，推理強度 xhigh，加速 Fast，100 個子 agent`); the live
  metrics add their words to both (*Live metrics*, Accessibility).
  A new leader is announced politely at most once per 10 s (`a11y.board.newLeader`). Increase Contrast / high
  contrast: solid fills, 1 pt outlines, no glows, comets as a 2 pt solid ring segment.
- Light and dark follow Appearance: each prototype's light theme is the reference.


## 3. Menu bar (macOS) and taskbar (Windows) widget

Glanceable and calm; it must never jitter in width.

- Layout: `[gauge glyph] [value] [unit]`.
- Gauge glyph: 270° ring, 1.75 pt stroke (scale with DPI), diameter = text cap height × 1.2 (≈ 13 pt in the menu
  bar, ≈ 14 px at 100 % on the taskbar), optically centered on the digits,
  track at 25 % of the label color, fill fraction `h` in the heat color with a 6 px soft glow from overdrive upward.
  At redline the fill flickers gently (opacity 85–100 %, 6 Hz). Monochrome option uses the label color only.
- Value: system font, 13 pt (menu bar) / 15 px semibold (taskbar), tabular digits, placed tight against the glyph
  (4–5 pt gap, never more). The item hugs its content: its width grows immediately when the formatted value gets
  longer and shrinks only after the shorter width has held for 6 s, so with tabular digits it changes only when the
  digit count changes and never flickers. Idle value uses the secondary label color.
- Unit `tok/s`: 10 pt / 11 px, secondary color, optional (setting). With `settings.liveUnit` set to money the value
  is the live cost rate and the unit `US$/s` (section 9).
- Display modes (setting): glyph + value (default), value only, glyph only.
- Money block (optional, default off): right after a tok/s value, two lines in the traffic block's 9 pt medium tabular
  text and 10 pt pitch, centered on the bar — `bar.money.now` with the live money rate (section 9, billable sources,
  `0.43 CN¥/s`) over `bar.money.today` with today's billable total (the dashboard's 今日費用, `128.40 CN¥`). Labels sit
  left-aligned in a column, numbers right-aligned in one shared cell, units left-aligned after them; labels and units
  take the secondary color, numbers the label color at 85 % (secondary when `–` or zero). The number cell grows at once
  and shrinks only after 6 s (the value's width governor), so the item never jitters while the numbers change; `≥`
  and `–` follow section 9; 8 pt before the block; hidden in glyph-only mode; read after the value by accessibility
  and in the tooltip as `a11y.bar.money`. In 選單列 / 工作列 the live value control has three choices —
  `settings.liveUnit.tokens`, `settings.liveUnit.cost` and `settings.liveUnit.both` (tok/s with the money block after
  it; tok/s alone stays the default) — so showing both is one visible choice rather than a separate switch.
- Readout blocks (optional, both default off): network speed (`↓` / `↑`, `settings.bar.traffic`) and 性能, the
  highest performance score (section 12), with 思考 (`性能` / `思考`, `settings.bar.intelligence`). They are fixed-width
  two-line blocks after the value and the money block, shown alone or together (section 10, *Menu bar and taskbar*).
- Click: open/close the dashboard. Right-click (Windows) / ⌥-click (macOS): quick menu.
- The widget follows the system/taskbar light or dark theme for text; the glyph keeps heat colors.

### Position on the taskbar (Windows)

Windows only. macOS has no equivalent: the system places menu bar items and the user rearranges them with ⌘-drag.

- Default spot: just before the notification area — the widget's trailing edge (the one facing the notification area)
  4 px × the taskbar's DPI scale before it, spanning the notification area's height when that is shorter than the
  taskbar. Other programs that draw on the taskbar (for example KuGou Music's taskbar lyrics) may claim the same spot,
  so the user can move the widget along the taskbar.
- Drag: press the left button on the widget and move the pointer along the taskbar (left/right; up/down on a vertical
  taskbar). Once the pointer has moved along the taskbar by more than the system drag distance (`SM_CXDRAG` /
  `SM_CYDRAG` at the taskbar's DPI), the press becomes a drag: the widget follows the pointer at once (no animation)
  and keeps the grabbed point under it, the cursor is the two-way sizing arrow, the tooltip stays hidden. Movement
  across the taskbar never starts a drag. Releasing ends the drag and saves the position; it never toggles the
  dashboard. A press released without a drag is a click — the dashboard toggles when it is released over the widget,
  nothing happens otherwise. Losing the mouse capture during a press, or the taskbar changing DPI or orientation under
  it, cancels it: no click, and a dragged widget returns to the saved position. The right-click menu is unchanged (a
  right-click during a press is ignored).
- Stored value `taskbar.offset`: the distance in DIPs, rounded to 0.01, from the default spot to the widget's trailing
  edge, measured toward the taskbar's start (left, or up on a vertical taskbar); 0 = the default spot (default), never
  negative, at most 10 000. Physical offset = round(offset × DPI / 96). It is a distance from the notification area,
  not an absolute or fractional position, because the taskbar lays out its items from its ends: when the notification
  area grows or shrinks (icons, clock, input indicator) the widget moves with it and keeps its gap to whatever sits in
  between, and DIPs keep that gap visually the same when the taskbar's DPI changes or it moves to a monitor of another
  resolution (a fraction of the taskbar's length would drift with every resolution change). 0.01 DIP keeps a dropped
  position on the same pixel at every scale up to 500 %.
- Limits (applied when placing, never written back): the widget's far edge never passes the point a quarter of the
  taskbar's length from its start — the room the widget already needs at its default spot — which keeps a
  left-aligned Start button and the first app buttons clear. A stored distance that does not fit (a smaller monitor, a
  wider notification area, a wider widget) places the widget as far along as it fits and is kept, so the position
  comes back when there is room again; a drag stores the position where the widget was dropped. Without room even at
  the default spot the widget is unavailable, as before. The widget keeps hugging its content: width changes move its
  far edge, never its trailing edge.
- Only the primary taskbar shows the widget, so there is one value. The floating fallback (when the widget cannot be
  embedded in the taskbar) follows the same rules.
- The taskbar tooltip ends with `taskbar.dragHint` in caption size and the secondary text color (tertiary text falls
  below the 4.5:1 contrast of section 7 on a light tooltip), 6 DIP below the lines above it.
- Undo: Settings → 工作列 → `settings.taskbar.position.reset` (section 5) — also the keyboard and screen reader route.

## 4. Dashboard

macOS: popover-style panel under the menu bar item. Windows: flyout above the taskbar with Mica. Width 368 pt/DIP.
Follows system light/dark. Corner radius: platform default (macOS 12–16 pt, Windows 8 px). 20 pt padding.

Sections (top to bottom, 16 pt spacing, hairline separators):

1. **Hero** — a 112 pt orb (same component, ring effects capped at Soft) on the left, its center carrying the hero
   vehicle of the current speed title (Vehicles theme) or the title badge (every other theme; section 4c); on the
   right the value at 44 pt bold tabular with `tok/s`, the speed title of the General title theme in heat color, and
   one line of context: `生成中 3 · 工具 1`.
2. **Live chart** — last 2 minutes (segmented control 1m / 2m / 5m), height 96. Smooth area chart (Catmull-Rom),
   fill = vertical gradient mapped through the heat palette by value, line 1.5 px. The in-flight estimated part is
   drawn with 45 % opacity and a dashed top line at the right edge. A dashed horizontal line marks today's peak with a
   small label. Hover/drag shows a crosshair with time and value.
3. **Sources** — one row per detected source: colored dot, name, live tok/s (right), today's output tokens
   (secondary; with today's cost, section 9); a live value below 0.05 shows the tertiary `–` like the agents list. Each row is a button that
   opens the Usage History window (section 4b) pre-filtered to that source:
   on hover the row gets a subtle highlight and a trailing chevron fades in; keyboard focusable; a11y hint "顯示用量歷史". Only
   sources that produced output tokens today, or are generating right now (live output above 0, e.g. the day's first
   response still in flight), are listed; the others are left to Settings → Data Sources, which lists every source
   with its status. A listed source that is disabled is dimmed. Rows fade in and out as sources join and leave (at
   midnight every row leaves until its source produces again). With no row the section shows
   `dashboard.sources.empty` in secondary text under its header.
4. **Agents** — up to 6 rows sorted by section 6.7 of METRICS: project (semibold), source + model (secondary,
   truncated), phase badge (generating: animated three-dot shimmer in the source color; tools: wrench glyph;
   idle: grey dot), effort capsule, 性能 (the performance score, section 12) and live tok/s right-aligned. Sub-agents are indented 12 pt with a
   thin connector line.
   **Header** — the section title `dashboard.agents` and, on the same line while the list has rows, two column
   captions, each right-aligned to its column's right edge: `intel.glyph` (性能 / 性能 / Perf / 性能, the word of the
   orb's and the menu bar's readout) over the 性能 column and `unit.tokS` over the live value. Captions:
   10.5 pt medium (Windows 11 px medium), secondary color, one line, never truncated or scaled, hidden from
   accessibility (every row says its values in words). Hovering the 性能 caption shows `agents.intel.hint`.
   The empty state has no captions.
   A live value below 0.05 (it would display as `0` or `0.0`) is never shown as a number: `tools` shows a small hourglass glyph (SF
   Symbol `hourglass` / Segoe Fluent `HourGlass`, 11 pt, secondary color, slow 2 s opacity breathing 55–100 %, static
   with Reduce Motion); `idle` and `generating` show an en dash `–` in tertiary color. Glyph and dash sit in the same
   right-aligned trailing slot as the digits (same baseline, no width change), crossfade 0.2 s to and from numbers,
   and read as the phase name for accessibility (`phase.tools`, `phase.idle`).
   **Looping mark** — while the agent is *looping* (METRICS 6.10) an amber `arrow.triangle.2.circlepath` (Segoe
   Fluent Sync U+E895), 11 pt, sits before the project name (4 pt gap) and the row's score card lists `work.looping`
   first in its 行為 block (section 12, *Score card (性能分析卡)*). It fades in 0.25 s and out 0.6 s (Reduce Motion: no fade), never
   moves the row (the 6.7 order is unchanged) and is not drawn on orbs or boards. The 行為 block also shows
   `work.repeating` while the agent repeats without failures and `work.summary` when it has at least 8 recent actions.
   Accessibility: the row label adds `work.looping` while it holds.
   **Effort capsule** — the agent's current reasoning effort and speed (`spec/EXECUTION-MODES.md`, Reasoning effort
   and speed), drawn in a trailing column just before the 性能 column (8 pt gap). The column is as wide as the
   widest capsule among the visible rows (width changes animate 0.2 s), so every meter starts on the same vertical
   line and every bolt ends on another; rows without a capsule leave it empty, and with no capsule at all the column
   takes no space. A capsule appears when the effort is known or the speed has bolts; nothing is drawn for unknown values or for
   a standard speed, and there is never a "not provided" label. When the column would leave the project name less
   than 72 pt (the room left after every trailing column, the 性能 column included), every capsule drops its
   word (meter and bolts stay; the word stays in the tooltip).
   - Capsule: height 18, fully rounded, fill label 5.5 % (dark 7 %), 0.5 pt stroke label 8 %; 6 pt horizontal padding,
     5 pt between its parts: meter, word, bolts.
   - Meter: five bars 2.5 pt wide with heights 4 / 5.5 / 7 / 8.5 / 10 pt, 1.5 pt apart, corner radius 0.75, bottoms
     aligned, the group centered in the capsule. The level (EXECUTION-MODES table) lights that many bars from the left.
     Levels up to 4 light in the secondary label color; `max` lights in heat 0.82 (section 1 palette); the top rung
     (`ultra`, `ultracode`) lights with a vertical gradient heat 0.72 → heat 1.0, a 3 pt outer glow of the same
     gradient at 45 %, and a shimmer: a soft 30° light band crosses the bars in 0.9 s every 2.6 s (none with Reduce
     Motion or effects Off). Unlit bars: label 14 % (dark 18 %). A token without a level has no meter.
   - Word: the native token in lower case (`xhigh`, `ultra`, `ultracode`, …), 11 pt medium, secondary color; `max` in
     heat 0.82, the top rung in the same gradient as its bars. Never truncated: it is dropped whole (above).
   - Bolts: SF Symbol `bolt.fill` / Segoe Fluent Icons Lightning (U+E945), 9 pt, amber `#F5A524` (light `#B86E00`).
     One bolt for `fast` and `highspeed`; two for `ultrafast`, overlapping by 3 pt with the rear one at 60 %.
   - Changes: lit bars fill or empty one after another (30 ms apart, 0.22 s spring), the word crossfades in 0.2 s,
     bolts pop in (scale 0.6 → 1, 0.3 s spring) and fade out in 0.15 s. Reduce Motion: crossfades only.
   - Increase Contrast / Windows high contrast: no fill, a 1 pt stroke in the text color; lit bars, word and top rung
     in the text color, unlit bars in the disabled-text color, bolts in the highlight color; no glow or shimmer.
   - Tooltip (the capsule's; the row's score card carries the same lines in its facts block): `effort.tooltip` and
     `speed.tooltip` lines, each value spelled as its token (speed
     tokens through `speed.value.*` when one exists), `modes.unrecorded` for an unknown one; a top rung adds
     `effort.ultra.detail` or `effort.ultracode.detail`. Accessibility: the row's combined label ends with the same
     two lines joined by `，` / `, `.
   **性能 column** — every row, root or sub-agent, shows the agent's performance score (METRICS 6.9; section 12)
   between the effort capsule column and the live value (user's choice, 2026-10-28; 性能 replaced 智力 in it,
   2026-10-29):
   - Cell: fixed, as wide as the wider of `8,888` (the widest compact score, section 12) in the column's number font
     and the header caption, measured when the language, font or text size changes and never per value (macOS 35 pt:
     `8,888` is 34.5 pt in SF Pro 12 pt medium, 性能 21 pt and Perf 21.6 pt at 10.5 pt; it was 24 pt for `888`;
     Windows the same rule in Segoe UI). The number is in compact form (section 12). The number is right-aligned in it, tabular
     digits, 12 pt medium (Windows 12 px semibold) — one point under the live value, so tok/s stays the row's main
     number — on the live value's baseline. Gaps: capsule column, 8 pt, the cell, 6 pt, the live value; with no
     capsule column the cell follows the name block at the row's spacing. The column is present whenever the list
     has rows, so the digits of every row share one right edge whatever the indent.
   - Value and color: **live** (the agent is not idle) — the score in the live value's number color; **resting**
     (the agent is idle and still has a score: timed requests within the last 30 minutes) — the score in the tertiary
     color; **none** (fewer than 3 timed requests in the last 30 minutes) — the tertiary `–` of the live slot, never
     `0`. The provisional mark (section 12) sits under a provisional score. Updated on every engine tick with the
     row: a change of at least 1 % and 3 counts through the integers, up or down (section 12, *Motion*), smaller
     changes in place. A
     change between live and resting crossfades the color in 0.2 s (an opacity change, kept with Reduce Motion).
     Increase Contrast: resting and `–` in the secondary color. Windows high contrast: live in the text color,
     resting and `–` in the gray-text color.
   - Room: the cell never truncates, shrinks or hides (the compact form never needs more than `8,888` below a
     million); the name block gives way. The detail (model · source)
     truncates at its tail first (the source name goes first, the badge color still carries it), then the project
     name with a tail ellipsis; the traffic suffix stays whole. The panel has one width (368 pt/DIP, rows 328), so
     the room is fixed by the trailing columns and the indent; on macOS (badge 16, two 12 pt row spacings, cell 35,
     6, live value 48) the name block keeps 199 pt at depth 0 and 175 pt at the deepest indent (depth 2) with no
     capsule, ≈ 126 / 102 pt with a typical capsule (meter and `xhigh`, ≈ 65 pt) and ≈ 83 / 59 pt with the widest
     (meter, `ultracode`, two bolts, ≈ 108 pt), so words drop only in rare combinations — the widest capsule on a
     sub-agent row (the 72 pt rule above).
     Windows keeps its row structure (name block over two lines, badge column 30, live value 56) and inserts the
     cell between the capsule column and the live value by the same rules.
   - Tooltip: the row's tooltip is the score card (section 12, *Score card (性能分析卡)*), shown beside the panel; the row's
     former lines (effort and speed, a top rung's detail, traffic) are its facts block.
   - Accessibility: the row's combined label adds, right after the live value, `a11y.agent.intel` with the score
     words of section 12 (`性能 1,286`, `性能 912，初步（樣本少）`, `性能 1,540，閒置中`); nothing for none. The row
     offers the custom action `a11y.perf.card`, which opens the card (section 12).
   Empty state: `目前沒有 agent 在工作` with a muted gauge glyph.
   Collapsed (default) shows the first 6 rows of the depth-first order (so a child never appears without its parent); when more agents exist, a disclosure row `agents.expand` ("+4 個 agent",
   with a small chevron, accessibility label `agents.expand.a11y`) expands the list in place with a spring height
   animation; expanded shows every agent and ends with `agents.collapse`. Beyond 12 rows the expanded list scrolls
   inside a fixed-height region (12 rows tall) so the panel never outgrows the screen. The hidden count counts agents,
   not groups. The expanded/collapsed
   choice is remembered.
5. **Today** — 2×2 metric grid: 輸出, 輸入 (含快取), 請求, 平均速度; plus 今日峰值 with time and 今日費用 (section 9). The
   hero's value follows `settings.liveUnit` (section 9). The section header
   carries a small "歷史" text button (clock glyph) that opens the Usage History window for all sources.
6. **Network** (`traffic.title`, while traffic measuring is on) — this computer's live speed, a 60 s sparkline and
   today's traffic per source; agents' rows gain their traffic (section 10).
7. **Footer** — floating orb toggle, metric info button (ⓘ explaining the measurement in one paragraph), settings,
   quit.

Metric explanation text (localized):
> 速度＝輸出 token ÷ 實際生成時間（從送出請求到完成，含等待第一個字）。多個 agent 會加總。進行中的回應先以該 agent
> 近期的實測速度估算，回應完成後立即以實測值校正。資料只來自本機紀錄。

## 4b. Usage History window

A native, resizable window (default 920 × 680, minimum 720 × 540, frame remembered; macOS: SwiftUI in an `NSWindow`
with a unified toolbar and the system window material; Windows: Fluent window with Mica). Title `history.title`.
Opened from a source row (pre-selected source), the Today header's 歷史 button (all sources), the quick menu
(`history.open`), and Settings → Data Sources. Only one instance; reopening focuses it and applies the new filter.

Layout, top to bottom, 24 pt outer padding, 20 pt section spacing:

1. **Toolbar** — leading: source picker (`history.allSources` plus every source with its color dot; sources without
   any history — token usage or recorded agent traffic, section 10 — are hidden, and so are disabled ones). Center: range segmented control `今天 · 昨天 · 7 天 · 30 天 · 90 天 · 今年 · 全部 · 自訂…`
   (`history.range.today`, `history.range.yesterday`). A single-day range (今天, 昨天, or a custom range of one day)
   compares with the day before (`history.summary.vsPreviousDay`), shows card sparklines over the 7 days ending on that
   day with the selected day's point highlighted, replaces the daily chart with a breakdown of that day
   (`history.chart.bySource` with 全部來源: one horizontal bar per source in its colour, sorted by output, with output,
   share and average speed; with one source, `history.chart.byModel`: the same per model), and today's figures update
   live (marked 進行中). The toolbar narrows the labels before it ever wraps. 自訂 opens a
   compact popover with two date pickers (`從` / `到`, clamped to the available data, the end never before the start)
   and `套用`. The resolved range is always spelled out under the title in the locale's date format (e.g.
   "2026/9/22 – 10/21 · 30 天"). Trailing: `匯出 CSV…` (the rows currently shown, locale-independent columns).
2. **Summary** — four cards in a row (two rows when narrow; a fifth, 費用, in section 9): 輸出 token, 輸入 token（含快取）, 請求, 平均速度 (Σ measured
   output ÷ Σ measured time over the range, never an average of daily averages). Each card: large tabular value,
   a secondary line (活躍 N 天 / 單日最高 date / 比前一段期間 +12%, colored green/red only for the delta), and a tiny
   sparkline of the range. Values use the compact formatter (METRICS 7) with full numbers in the tooltip. A card
   title never truncates: one that does not fit its card uses its short form (`history.summary.input.short` for
   輸入 token（含快取）, e.g. five cards in a row at the default width in Japanese), with the full title in its tooltip
   and for accessibility.
3. **Chart** (height ≈ 260) — one bar per day of output tokens; with 全部來源 the bars stack by source in source
   colors (largest at the bottom), otherwise a single bar in the source color with a soft vertical gradient. An
   average-speed line on a secondary axis (right) in the label color with small dots on days with measurements,
   breaks (no line) on days without measurements. Today's bar is drawn hatched/lighter and labelled 進行中. Weekend
   bars are not special-cased; month boundaries get a faint vertical rule and a month label. Hover (or keyboard
   arrows) shows a crosshair and a tooltip card: date with weekday, output, input (incl. cache), requests, average
   speed, peak (if recorded; all-sources view only, METRICS 8) and the top 3 models with share bars. Clicking a bar scrolls the table to that day and
   highlights it for 1.2 s. Transitions between ranges/sources animate bar heights with a spring (≤ 0.45 s);
   Reduce Motion → crossfade.
3b. **Effort and speed** (`modes.history.title`) — between the chart and the table, a section whose header carries
   a two-segment control (`effort.label` / `speed.label`, remembered) and the trailing `mode.exportCsv` button (the
   mode CSV of EXECUTION-MODES, every kind including `effort` and `speed`). One row per value over the selected range
   and source, summing models, providers and evidence, in the display order of EXECUTION-MODES with the unknown bucket
   (`modes.unrecorded`, tertiary text) last, so the rows always add up to the range's totals. Each row: the leading
   glyph (the meter of the effort capsule; for speed its bolts, a hollow 6 pt ring for `standard` and tokens without
   bolts), the token, a share-of-output bar (effort: neutral up to `xhigh`, heat 0.82 for `max`, the top-rung gradient
   for `ultra` / `ultracode`; speed: amber for bolts, neutral otherwise; unknown: label 12 %), then requests, output
   and average speed (`history.noSpeed` when none) in tabular digits. Hovering a row shows its top three models with
   share bars. Rows reorder and resize with a spring (≤ 0.35 s) when the range or source changes; Reduce Motion
   crossfades. With no known value of the selected kind in the range, only the unknown row shows, with
   `modes.history.empty` under it.
3c. **Performance** (`history.perf.title`, 性能表現; it replaces the task comparison 任務表現) — between 3b and the
   daily table: the performance score of every source and model over the selected range, pooled from the daily
   records (METRICS 6.11), with its components and its usual value, and the daily trend of the selected row against
   the reference 1000 and its usual line. Layout, widths, states and accessibility: section 12, *Usage History:
   performance (性能表現)*.
4. **Daily table** — newest first; columns: 日期 (date + weekday; 今天 / 昨天 for the latest two), 請求, 輸出, 輸入（含快取）,
   快取命中 (cachedInput ÷ input, percentage), 平均速度 (tok/s, `history.noSpeed` when none), 峰值 (if recorded;
   all-sources view only),
   Agent, 主要模型 (up to 2 model names, rest as "+n"); plus 費用 after 輸入 (section 9, also for chart, tooltips and CSV). Sortable headers, tabular digits, right-aligned numbers,
   alternating subtle row tint, hover highlight linked with the chart, keyboard navigation, copy row as text.
   Days without data inside the range are not listed (the chart shows the gap).
5. **Footer** (secondary text) — left: analysis state: `history.analyzing` with a thin progress bar and
   `history.analyzingFiles` while the background analysis runs (the window is usable and updates live as days come
   in), otherwise `history.updated`; right: `history.privacy` (daily totals and per-model daily performance figures, section 12), the
   first to give way when notes crowd the footer: it
   switches to `history.privacy.short`, then to its lock icon alone (the full text in the tooltip and for
   accessibility), never cut mid-sentence.

CSV export (`匯出 CSV…`): the rows the daily table shows, in locale-independent form — English snake_case headers,
ISO dates, plain integers, a dot as the decimal separator (`cache_hit_percent`, `avg_speed_tok_s` and `peak_tok_s`
with one decimal, empty when there is no value), RFC 4180 quoting, CRLF line ends, UTF-8 with a byte order mark, and a
`'` before a field that starts with `=`, `+`, `-`, `@`, a tab or a carriage return (so a spreadsheet never reads it as
a formula). Columns: `date`, `source`, `requests`, `input_tokens`, `cached_input_tokens`, `cache_write_input_tokens`,
`output_tokens`, `reasoning_tokens`, `cache_hit_percent`, `measured_output_tokens`, `measured_ms`, `avg_speed_tok_s`,
`peak_tok_s`, `agents`, `unfinalized`, `models` (the model names joined with `; `), then the cost columns (section 9)
and the traffic columns (section 10). `source` is the code of the selected source, `all` for `history.allSources` (traffic adds `machine`
rows, section 10); the file name is `agent-speed-history-<code>-<first date>-<last date>.csv`. Source codes:

| Source (METRICS 1) | Code |
| --- | --- |
| `codex` | `codex` |
| `claudeCode` | `claude-code` |
| `claudeDesktop` | `claude-desktop` |
| `deepseek` | `deepseek` |
| `opencode` | `opencode` |
| `gemini` | `gemini` |
| `omp` | `omp` |
| `antigravity` | `antigravity` |
| `workbuddy` | `workbuddy` |
| `workbuddyAi` | `workbuddy-ai` |
| `zcode` | `zcode` |
| `kimi` | `kimi` |
| `grokBuild` | `grokBuild` |
| `pi` | `pi` |
| `openclaw` | `openclaw` |
| `hermes` | `hermes` |
| `droid` | `droid` |

Empty state (the range has neither token usage nor agent traffic, section 10): centered muted gauge glyph,
`history.empty` and `history.empty.hint`. First launch after upgrading:
the window opens instantly with whatever is cached and fills in as the analysis progresses — never a blocking
spinner. All numbers and dates follow the selected language; weeks start per locale.

Network traffic adds a third chart metric, a table column, tooltip lines and CSV columns, from the day measuring began
(section 10).

## 4c. Hero vehicle

The empty center of the dashboard hero orb (section 4.1) carries a glowing line drawing of the current speed title's
vehicle, moving at a pace set by the live value. It belongs to the instrument rather than sitting on it: one stroke
weight, the heat colors and the same glow as the LED gauge and the comet. The floating orb keeps its readout. The
vehicle belongs to the Vehicles title theme (and custom sets based on it); with any other theme the center carries the
title badge instead (Hero title badge, at the end of this section).

Source of truth: `spec/assets/vehicles.json` (art, rigs, emitters and the per-vehicle motion constants).
`python3 spec/tools/vehicles.py` validates it; `--export` regenerates `spec/assets/vehicles/*.svg` (generated, never
edited) and `--preview DIR` renders PNG previews in both appearances. Both apps embed the JSON (copied at build time,
like `strings.json`) and draw only from it.

### Art format

- Canvas 100 × 100 units, y down, every vehicle faces right (+x). The canvas maps to a square of side `0.72 S`
  centered on the orb (`S` = 112 pt, so 1 unit = 0.806 pt).
- `safeCircle` (center 50, 50, r 42): every part, every rig pose and the full sweep of every spinning part stay inside
  it, stroke included. Parts flagged `bleed` (train bodies, flames, the shock cone) may run to the canvas edge and
  dissolve in the mask; the leading half of a bleeding body (x > 45) still stays inside.
- Every vehicle carries a similar visual weight and is centered for its own shape: wide, low vehicles (cars, aircraft)
  span almost the full width of the safe circle, tall ones (walker, riders, rocket) more of its height.
- `stroke.width` 2.0 units (1.6 pt), round caps and joins. A part's `role` (table `roles`) sets its weight multiplier,
  opacity, glow share, whether it is `sprung` (moves with the body) and its paint: `color: "hot"` uses the accent;
  only `flame` (heat fill at 30 %) and `flameCore` (white-hot fill) fill.
- Geometry: `d` holds absolute `M L C Q Z` commands, tokens separated by single spaces, numbers with at most two
  decimals (WPF `Geometry.Parse` reads it directly; macOS builds a `CGPath` with a small tokenizer), or `circle` =
  `[cx, cy, r]`. No arcs, no transforms.
- Optional part fields: `spin {pivot, radius, symmetry}` (rotates with the ground, below), `nozzle {at, dir}` (flames
  scale about it), `sprung` (overrides the role), `bleed`.
- `rig`: `walk` (the walker) or `pedal` (bicycle and e-bike riders); limbs are generated, see Continuous motion.
- `emitters`: `{id, type, at, dir}` plus type fields — `trail` (`lanes`, `length`), `dust`, `exhaust`, `arc`
  (`count` = puffs or arcs per start-up burst), `sparks`, `contrail` (`weak`), `afterburner`, `plume`. `dir` is a unit
  vector (0, 0 for radial bursts).
- `idle`: `{vehicle: "last", fallback: "walk"}` (see Idle).
- `groundY` (road, rail and guideway vehicles): the height of the vehicle's road line, rail or guideway top; tires
  touch it and the scenery is drawn from it. Each vehicle has its own (60.1–79), because each one is centered for its
  own shape.
- `motion`: `speed [lo, hi]` world speed range in units/s, `rest` (ground distance of the static pose), and
  `bob` / `float` / `hover` amplitudes in units. `heading` (rocket only): travel direction in degrees, screen space,
  0 = right, −48 = up-right.
- `scenery`: shared backdrops `road`, `rail`, `guideway`, `sky`, `stratosphere`, `space`; each vehicle names one.
- Draw order: far rig limbs, then `parts` in array order, then near limbs and the head.

### Placement, mask and layer order

- Drawn after the body (layer 2) and before the gauge (layer 3), in place of the readout. The mask ends at 0.353 S
  (49 units; the validator keeps it ≤ 0.3585 S), inside the gauge's inner edge (0.3875 S) and above the agent dots
  (0.3785 S), so drawing the layer as an overlay above the whole orb looks identical and is allowed.
- A soft circular mask covers the whole layer (scenery, speed lines, particles and glow included): opacity 1 up to
  r = 42 units (`mask.solid`, equal to the safe circle), falling linearly to 0 at r = 49 (`mask.clear`). Speed lines
  and train bodies dissolve at the rim; nothing ever reaches the gauge arc or the agent dots.
- Per frame: (1) draw scenery, speed lines, vehicles and particles into a layer of `ceil(0.72 S × scale)` px;
  (2) draw the vehicle strokes again into a glow source, each part at its role's `glow` share; (3) blur the glow source
  and composite it under the crisp layer; (4) apply the mask; (5) composite onto the orb.

### Color and glow

| | Dark | Light |
| --- | --- | --- |
| Stroke `C` | heat color of the smoothed heat `h` | title label color: heat darkened 18 % (OKLab L), then stepped to ≥ 4.5:1 on the popover (`heatText`) |
| Accent `H` (`light` and `glow` roles, flame outline) | `mix(C, #FFF7D6, 0.65 + 0.35 o)` | `mix(C, #FFB020, 0.45)` |
| Flame core | `#FFF7D6`, toward white by `o` | `#FFB020` |
| Glow blur σ | `1.0 + 1.4 h + 0.6 o` units (× 1.2 at Max) | same |
| Glow opacity | `0.30 + 0.45 h + 0.15 o`, additive | `0.14 + 0.18 h`, normal blending |

Mixes are in OKLab. Increase Contrast: stroke weights × 1.3, `detail` / `window` / `rim` / `spoke` opacity at least
0.9, glow × 0.5, `C` at 7:1 (light) / 4.5:1 (dark). Where a GPU blur is unavailable, approximate the glow with two
extra strokes under each part: width `w + 1.2 σ` at 0.5 × the glow opacity and `w + 2.6 σ` at 0.2 ×.

### Speed model

Everything that moves derives from one world distance `D`, so wheels, cranks, feet, road dashes and sleepers stay
locked together: no sliding feet, no slipping tires.

- Smoothed value: `v_s = R · h_s^(1/0.6)` while `h_s < 1`, else `R (1 + o_s)` (`h_s`, `o_s`: the section 1 springs).
- Band progress inside the displayed title `k` (thresholds `T_k` from section 1; walk = 0.5 tok/s):
  `b = clamp(ln(v_s / T_k) / ln(T_(k+1) / T_k), 0, 1)`; rocket: `b = o_s`.
- Coasting: `c = clamp(v_s / T_k, 0, 1)`. The title holds through hysteresis, but the vehicle slows when the agents
  stop.
- Ground speed target `G* = 0` when parked, else `lerp(lo, hi, b) · √c` with `speed [lo, hi]` from the JSON. Each
  range starts at or above the previous title's top (walk 24–41 … rocket 838–1008 units/s), so an upgrade never slows
  the world down.
- `G` follows `G*` with a critically damped spring, settle 0.9 s, evaluated exactly per frame: `ω = 6.2 / settle`,
  `c1 = G − G*`, `c2 = a + ω c1`, then `G ← G* + (c1 + c2 Δt) e^(−ω Δt)` and `a ← (c2 − ω (c1 + c2 Δt)) e^(−ω Δt)`
  (`a = dG/dt`, used below). The same form, settle 0.5 s, drives `wake`. `D += G Δt`; keep `D` in double precision
  and reduce angles modulo 2π before handing them to the GPU. Off and Reduce Motion freeze `D` at `motion.rest`.

### Continuous motion

- **Spin** — angle `D / spin.radius` rad, clockwise. Wheels use their tire radius (rolling without slip); cranks use
  tire radius × gear (bicycle 1.15; e-bike 1.9, so its rider pedals lazily while the motor works).
- **Spoke blur** — spoke frequency `f = G / (2π radius) × symmetry`, `blur = smoothstep((f − 14) / 12)`. Spokes fade by
  `1 − blur` while two motion arcs appear (radii `0.72 (radius − 2)` and `0.45 (radius − 2)`, 120° long, on opposite
  sides, turning clockwise at 0.6 rev/s, opacity `0.5 blur`, spoke weight). Bicycle spokes stay crisp, car spokes
  start to blur, sports and race car wheels read as spinning discs.
- **Walker rig** (all lengths and amplitudes from `rig`) — gait phase `p = (D / cycle) mod 1`; near leg `p`, far leg
  `p + 0.5`. Stance (`p < stance`): the foot rests at `groundY − 0.6` and slides from `+a` to `−a` (`a = cycle ·
  stance / 2`) at exactly ground speed. Swing (`s = (p − stance) / (1 − stance)`): `x = −a + 2a smoothstep(s)`, lift
  `lift · sin(π s)`. Hip bob `−bob · cos(4π (p − stance / 2))`. Knees by two-bone IK (`thigh`, `shin`), the knee on
  the +x side of the hip–foot line (`cross(foot − hip, knee − hip) < 0`, y down); a `foot`-long toe stroke points
  forward from the ankle. Shoulder = hip + `torso`, head circle = shoulder + `head` (dx, dy, r). Arms (`upperArm`,
  `forearm`) swing `−armSwing° · cos(2π p)` from vertical, forearms bent a further `elbow`°. Far limbs at 42 %
  opacity. Parked: legs and arms blend to a standing pose (feet ±1.6 under the hip, arms ±4°, elbows halved, hip at
  `groundY − 0.6 − 0.97 (thigh + shin)`), time constant 0.18 s once `G < 6`.
- **Pedal rig** — crank angle `φ = D / crank.radius`; near pedal `crank.pivot + crank.length (−sin φ, cos φ)`, far
  pedal opposite. Hip bob `bob · cos 2φ`, applied to the torso, arm and head (`rider` parts) too. Legs (`thigh`,
  `shin`) reach the pedals by the same IK; pedals are `pedal`-long horizontal strokes that stay level.
- **Suspension** (road vehicles, sprung parts only):
  `y = bob (0.6 sin(2π 1.7 (1 + 0.5 b) t) + 0.4 sin(2π 3.3 (1 + 0.5 b) t + 1.3)) (0.4 + 0.6 b)`. Acceleration pitch
  `clamp(−0.0035 a, −1°, 1°)` about the rear tire's ground contact, and a forward lurch of the whole vehicle
  `clamp(0.008 a, −1.2, 1.2)` units.
- **Aircraft** — float `y = float · sin(2π 0.32 t)`, pitch `0.8° (float / 0.7) cos(2π 0.32 t)` about the vehicle center.
  Rocket: `float · sin(2π 0.55 t)` across its heading, no pitch.
- **Maglev** — hover lift `hover · (0.75 + 0.25 sin(2π 0.7 t))` units. The hover-gap glow line stays centered in the
  gap, opacity × `(0.5 + 0.5 b)`, and a 14-unit bright pulse travels along it from nose to tail at `0.5 G`.
- **Flames** (afterburner, scramjet, rocket plume) — scaled about `nozzle.at`: along `nozzle.dir` by
  `(0.55 + 0.45 b + 0.35 o) · flicker`, `flicker = 1 + 0.1 · mean(sin 2π17t, sin(2π23t + 1.1), sin(2π31t + 2.3))`;
  across by `(1 + 0.06 sin 2π13t) · min(1, 0.6 + 0.4 length)`. Standard and Max add three shock diamonds when
  `b > 0.35`: at 3.0 / 5.8 / 8.6 units × the flame length behind the nozzle, half-height 1.0 / 0.82 / 0.64
  (width 0.9 ×), weight 0.35, opacity `0.75 (b − 0.35) / 0.65`, shimmering ±25 % at 19 Hz.
- **Shock cone** (hypersonic) — scaled vertically about the nose tip by `1.15 − 0.35 b` (the Mach cone narrows with
  speed), opacity × `(0.4 + 0.6 b)(0.7 + 0.3 sin 2π11t)`.
- **Contrails** — start 2 units behind each `contrail` emitter, 46 units long (`weak` wing-tip vapor: 22), sampled
  every 2 units at `y(d) = at.y + float(t − d / 90) + 0.35 sin((at.x − d + 0.05 D) / 7) · d / 40`, so the trail ripples
  behind the floating aircraft. Weight 0.8 (weak 0.45); opacity fades linearly from `0.55 (0.5 + 0.5 b)` (weak 0.26)
  to 0.
- **Trail marks** — the short speed marks behind each vehicle (`trail` emitters): lane `i` starts at
  `at.x − 1.5 − 2.5 (i mod 2)`, length `length (0.45 + 0.55 b + 0.25 h)(1 + 0.18 sin(2π (5.3 + 1.7 i) t + i))` ×
  `min(1, G / 20)`, opacity 0.55 fading to 0 along the mark, weight 0.5.
- **Scenery** (opacity 0.30 dark / 0.34 light, weight 0.55, scrolling with `D` × the scenery's parallax):
  - road: dashes 7 / gaps 5 on `groundY`. Anti-strobe smear `s = clamp(G / 60 / 12 / 0.45, 0, 1)` lengthens each dash by
    `0.85 s × gap` and multiplies the opacity by `1 − 0.35 s`: at race-car speed the markings blur into a line instead
    of appearing to run backwards.
  - rail: rail on `groundY` (opacity × 1.3); sleepers every 9 units (2.6 deep, leaning back 1.2), smeared with
    `s = clamp(G / 60 / 9 / 0.4, 0, 1)`: the lean grows by `4 s`, opacity × `1 − 0.55 s`.
  - guideway: beams on `groundY` and `groundY + 3.4`, pylons every 38 units hanging 9 units below.
  - sky: the two cloud outlines of the JSON on two layers, parallax 0.12 (near, below) and 0.05 (far, above),
    spacing 72.
  - stratosphere: the Earth's limb (circle center y 178, r 108, 207°–333°) with a soft blurred under-glow; fixed.
  - space: 14 stars streaking against the rocket's heading at `0.5 G` × depth (0.5–1.2), length
    `max(0.3, 0.5 G / 60 × 1.3)` × depth.
- **Speed lines** — pool of 16. Live count `N = round((2 + 8 h) · clamp(G / lo, 0, 1))`, capped at 5 (Soft, 0.6 N),
  9 (Standard) and 14 (Max, 1.4 N); a new line every `0.9 / N × (0.5…1.5)` s. Each line: length `(6 + 20 h) × (0.7…1.3)`
  plus `1.5 G / 60` (never shorter than one frame of travel), speed `1.25 max(G, 40) × (0.85…1.15)`, opacity
  `(0.16 + 0.32 h) × (0.7…1.3)` fading from head to tail, weight 0.5, 0.12 s fade-in, accent color at redline (dark).
  `y` is uniform in 16…84; a line whose `y` crosses the vehicle's bounds is born at the vehicle's tail (it peels off the
  body) instead of crossing the drawing. Rocket lines run against the heading across the whole disc.
- **Particle streams** (Standard and Max; pool of 64): rocket sparks `26 + 60 o` per s from the nozzle; race-car floor
  sparks `30 (b − 0.55) / 0.45` per s from the floor with gravity 260 units/s²; afterburner embers `6 + 14 b` per s
  from the overdrive tier up. Sparks are short streaks (velocity × 0.022 s) shading from the flame core to `C`, life
  0.25–0.45 s. Max: × 1.6.
- **Camera shake** (Standard and Max, redline tier, not parked): amplitude `A = 0.2 + 0.5 o` units (× 1.4 at Max);
  `x = A (0.5 sin 2π23t + 0.3 sin(2π31t + 1) + 0.2 sin(2π41t + 2))`,
  `y = A (0.5 sin(2π19t + 0.5) + 0.3 sin(2π29t + 2) + 0.2 sin(2π37t + 1))`. It moves the layer's content, never the orb
  or the mask.

### Title transitions

A transition starts on the same title-change event as the label slide (after the 0.8 s hold and the 90 % exit
hysteresis), so label and vehicle change together. Transitions last at most 0.75 s, less than the hold, so they never
queue; should one still be running, the incoming vehicle becomes the outgoing one from its current position. A jump
across several titles goes straight to the final vehicle. `τ` = time since the change; offsets run along the heading
(horizontal, up-right for the rocket).

| | Outgoing | Incoming | Extras |
| --- | --- | --- | --- |
| Upgrade | accelerates away: `+90 easeInCubic(τ / 0.34)`, opacity `1 − smoothstep((τ − 0.12) / 0.18)` | swoops in from `τ = 0.12`: spring −62 → 0, initial velocity +140 units/s, response 0.5 s, damping 0.8 (≈ 1 unit overshoot), opacity `smoothstep((τ − 0.12) / 0.14)` | while faster than 40 units/s, 3 motion ghosts trailing by speed × 0.012 / 0.026 / 0.042 s at 26 / 13 / 6 %; at `τ = 0.32` a flash on the incoming vehicle's center (radial gradient, radius 8 → 36 units easeOutCubic over 0.35 s, opacity 0.42 dark / 0.26 light × `(1 − u)²` × `min(1.6, 1 + 0.2 (titles climbed − 1))`, × 0.6 at Soft) and +60 % glow fading over 0.4 s; 4 burst speed lines |
| Downgrade | falls back: `−46 easeInOutSine(τ / 0.55)`, opacity `1 − smoothstep(τ / 0.45)` | glides in from +8: `8 (1 − easeOutCubic((τ − 0.1) / 0.55))`, opacity `smoothstep((τ − 0.1) / 0.35)` | none; `G` eases down through its spring |
| Reduce Motion | 0.3 s crossfade | 0.3 s crossfade | none |
| Off | cut | cut | none |

Scenery belongs to its vehicle and crossfades with it when it changes (road → rail → road → guideway → sky →
stratosphere → space). When both vehicles use the same scenery, it is drawn once and its ground line glides from the
outgoing vehicle's `groundY` to the incoming one's over 0.45 s (`0.5 − 0.5 cos(π u)`), so the road never jumps.

Motion keeps every drawing inside the soft fade: lurch, pitch, float and camera shake carry a tip at most ≈ 1.7 units
past the safe circle (a hard downgrade; mask still ≥ 75 % there), the motorcycle's start-up wheelie ≈ 2.2 units, and
the rocket's lift-off deliberately starts 6 units back inside the fade. Nothing comes near the clear radius (49) and
nothing is ever cut.

### Idle, start-up and parking

- **Idle** — the last vehicle stays parked, also across relaunches: the apps persist the last non-idle title key in
  their settings (`heroLastTitle`, a vehicle key such as `sportsCar`, written on every title change). First launch
  ever, or an unknown value: the walker, standing. Color `C(0)` (slate) at 55 % (dark) / 50 % (light) × breathing
  `0.86 + 0.14 sin(2π t / 4.8)`; no glow, speed lines, flames, contrails or hover; scenery still. Every 6 s, in step
  with the gauge's idle sweep, a light band crosses the vehicle's strokes: an 18-unit horizontal gradient (white-hot
  at 75 % dark / slate at 60 % light) travelling x −10 → 110 in 0.9 s, ease-in-out.
- **Parking** (title → idle) — `G* = 0` (the vehicle has already coasted down with `c`); `wake` → 0 (critically
  damped, settle 0.5 s) fades color, glow, flames, contrails and hover; the walker settles into the standing pose;
  riders keep their last crank position.
- **Start-up** (idle → title) — `wake` → 1 recolors slate → heat. Same vehicle: it starts where it stands. Another
  vehicle: the parked one fades out (0–0.2 s) and the new one fades in on the spot (0.12–0.40 s). The choreography
  starts 0.15 s (same) / 0.30 s (other) after the change; `G` rises from 0 through its spring, which gives every launch
  its natural acceleration. `bump(a, p, e)`: smoothstep from 0 at `a` to 1 at `p`, back to 0 at `e` (seconds).

| Vehicle | Start-up |
| --- | --- |
| walk | first step from standing; 3 dust puffs at the feet |
| bicycle | the rider stands on the pedals (hip 1.3 up and 0.8 forward, `bump(0, 0.18, 0.6)`), push-off lurch `1.6 bump(0, 0.22, 0.9)`, 6 dust puffs at the rear tire |
| e-bike | as the bicycle, plus power-on: battery LEDs and headlight flare `bump(0, 0.08, 0.5)`, 7 electric arcs around the hub motor (30 ms apart, 0.16–0.30 s each) |
| motorcycle | two exhaust pops (0 s, 0.18 s), 0.3 s engine shake (0.35 units), wheelie −3° `bump(0.12, 0.34, 0.8)` about the rear contact, lurch `1.4 bump(0.1, 0.35, 0.9)`, dust |
| car, sports car | double headlight blink (`bump(0, 0.04, 0.12)`, `bump(0.16, 0.2, 0.3)`), squat −1.3° `bump(0.05, 0.25, 0.7)`, lurch `1.2 bump(0.08, 0.3, 0.9)`; car: exhaust puff; sports car: an exhaust flame pop (0.22 s) |
| race car | launch: 5 tire-smoke puffs, squat −1.1° `bump(0, 0.2, 0.6)`, lurch `2.0 bump(0.05, 0.28, 0.9)` |
| bullet train | headlight on `bump(0, 0.15, 0.7)`, gentle pull-away lurch `1.0 bump(0.1, 0.45, 1.1)` |
| maglev | levitation: the hover gap ignites (glow +`bump(0, 0.3, 0.8)`), the body lifts by `smoothstep((τ − 0.2) / 0.5)` |
| jetliner | engines spool up: intake light `bump(0, 0.2, 0.8)`, contrails grow over 1.2 s, climb 2 units `bump(0.1, 0.5, 1.2)` |
| fighter jet, hypersonic | afterburner light-off: the flame springs 0 → 1 (initial velocity 9/s, response 0.32 s, damping 0.55, ≈ 25 % overshoot), nozzle flash (glow +`bump(0, 0.06, 0.35)`), shock cone draws on over 0.4 s, lurch `1.4 bump(0, 0.2, 0.8)` |
| rocket | ignition: white-hot flash, the plume springs to full (initial velocity 8/s, response 0.36 s, damping 0.5), 6 exhaust rings, 0.6 s shake (0.8 units), lift-off from 6 units back along the heading (easeInCubic, 0.9 s) |

Puffs and rings are outline circles (weight 0.45 → 0.22) growing by 2.6–5 units while fading over 0.45–0.85 s; dust
drifts back with the road. Every start-up adds 3 burst speed lines. Soft keeps the start-ups but caps each burst at 6
particles.

### Effects intensity

| Level | Vehicle layer |
| --- | --- |
| Off | Static glyph in its rest pose, static scenery, 1–3 static speed marks by `b` (`< 0.34`, `< 0.67`, else), fixed glow (0.30 dark / 0.12 light), cuts between vehicles |
| Soft | Every continuous motion (wheels, cranks, gait, suspension, scenery, trail marks, flames, contrails, hover, shock cone), ≤ 5 speed lines, transitions with ghosts and a 60 % flash, start-up bursts ≤ 6 particles; no particle streams, no shake |
| Standard | Soft plus particle streams, shock diamonds, ≤ 9 speed lines, camera shake at redline |
| Max | Standard with 1.6× particles, ≤ 14 speed lines, glow radius × 1.2, shake × 1.4 |

The vehicle layer follows the user's Effects setting (default Standard); the hero's ring layers (aura, comet, pulses)
stay capped at Soft as in section 4. Reduce Motion still caps the vehicle layer at Soft (below).

### Accessibility and performance

- Reduce Motion caps the layer at Soft and removes translation, rotation, scrolling, shake and particles: the vehicle
  sits in its rest pose, and speed reads from 1–3 static speed marks (by `b`), flame length (by `b`, no flicker) and
  the glow. Titles crossfade (0.3 s); a start-up is a crossfade plus the recolor; idle is static (no breathing, no
  sweep).
- Increase Contrast: see Color and glow. Windows high-contrast themes: no glow, scenery or speed lines; strokes in the
  theme's highlight color, flames as outlines.
- Decorative: hidden from VoiceOver and UI Automation (the hero already announces value and title), never
  hit-testable, no announcements of its own.
- Runs only while the dashboard is visible (popover or flyout open and not occluded). While moving it renders at the
  display rate; parked, at 10 fps for the breathing and 30 fps during the 0.9 s sweep; Off and Reduce Motion redraw
  only on change.
- Budget: ≤ 2 ms CPU per frame at 60 Hz on Apple Silicon (and on the Windows reference machine), blur on the GPU. No
  allocations per frame: parse the JSON once, build every part path once (`CGPath` / frozen `StreamGeometry`), keep
  fixed pools (64 particles, 16 speed lines) and reuse mutable paths for rig limbs and contrails (WPF: update
  `PathFigure` points in place).


### Resolved details (normative; found while implementing)

1. Glow is composited **over** the strokes, not under: additive in dark mode, normal blending in light mode. Glow share
   values above 1 (2, 1.6) clamp to 1.
2. Afterburner and rocket ignition overshoot come from the spring parameters given above (≈ 14.5 % fighter jet,
   ≈ 18.6 % rocket); the "about 25 %" wording is descriptive only.
3. Suspension and every other speed-dependent oscillation use an **accumulated phase**: `φ += 2π · f(b) · dt`, then
   `sin(φ)`; never `sin(2π · f(b) · t)`, which jumps when `b` changes.
4. The spoke motion arc rotates at its own 0.6 rev/s only; it does not add the wheel's rotation.
5. Leaving idle into the walker, the standing pose fades into the gait with a 0.18 s time constant (no pose jump).
6. During a transition, scene parts shared by both vehicles (road, track, sky) are drawn once at full strength, not at
   the max of the two vehicle opacities.
7. With Reduce Motion, contrails are frozen as well.
8. The lurch (forward surge) applies to every vehicle along its direction of travel (the rocket: up-right).
9. A title change during a running transition continues from the current positions, never restarting at 0.
10. The idle light band starts 0.63 s into the gauge's 6 s sweep cycle, so it crosses the vehicle's center while the
    gauge sweep passes the top.
11. Frame rate: the largest integer divisor of the display refresh rate that is ≥ 60 (144 Hz → 72, 120 Hz → 60,
    60 Hz → 60); parked/idle: ≈ 15 fps; static: no frames.
12. At Soft, start-up engine shake (motorcycle, rocket) stays; redline camera shake needs Standard or higher.
13. The spring `settle` time leaves ≈ 1.5 % of the step at `settle` (ω = 6.2 / settle); the behaviour is defined by ω.

### Hero title badge (every title theme except Vehicles)

With any title theme other than Vehicles — custom sets based on Vehicles keep the vehicle — the hero orb's center
carries a glowing typographic badge of the current title instead of the vehicle: a medallion that ranks up. It reuses
the vehicle layer's canvas (100 × 100 units on `0.72 S`), placement, soft mask (solid to r 42, clear at 49), layer
order, colors `C` and `H` (Color and glow), the smoothed `h` and `o`, `wake`, camera shake, frame policy and budget.
Like the vehicle it is decorative. The numbers below were tuned on an HTML prototype of the badge.

**Title layout** (computed once per text, language, appearance and size, then cached). System font at its heaviest
weight up to Heavy (macOS SF Pro Heavy with PingFang / Hiragino Sans at their heaviest; Windows Segoe UI Variable
Display Bold with Microsoft JhengHei UI / YaHei UI / Yu Gothic UI Bold), Latin in uppercase with tracking +0.05 em
(0 when the one-line size would fall below 14 units), CJK +0.04 em. `s1` = the largest size, in 0.5-unit steps from
the maximum down to 9, at which the title fits 72 units on one line; maximum 34 units for a single grapheme, else 28
(CJK) / 26 (Latin, two graphemes) / 22 (Latin).

1. **Seal**: zh-Hant and zh-Hans titles of exactly four Han characters (初學乍練, 一代宗師, 風平浪靜, 技術主管) stack as
   2 × 2 at 21 units, line advance 1.04 em — the shape of a seal. Japanese never uses the seal.
2. **Two lines** when the title has a break opportunity — a space, or, in Han-only text of five or more characters,
   after the second character (five) or at the middle (six or more) — and some size from 22 units (Latin 20) down to
   13 that is also at least `1.15 s1` fits both lines in 72 units: the largest such size with the most balanced
   break, line advance 1.1 em, each line centered (RED / GIANT, FORCE / 7, 初級 / 工程師, 10x / 工程師). Runs of kana
   or Latin letters never break.
3. Otherwise **one line** at `s1` (a single long word such as TRIBULATION or ブラックホール gets ≈ 9.5–12 units); it is
   never truncated.

The block is centered optically: the ink box of all lines (cap and ideograph bounds, not the line boxes) sits on
(50, 50).

**Layers**, back to front, all inside the mask:

1. **Halo** — radial gradient at (50, 50), r 0 → 34 units, `C` at `0.10 + 0.22 h` (dark, additive) / `0.05 + 0.08 h`
   (light) fading to 0, × `wake`; breathes ±10 % on the aura's period (section 2, layer 1). None with Increase
   Contrast.
2. **Speed streaks** — the 4c speed lines (pool, counts, caps per effects level, lengths, speeds, opacities, fade-in,
   accent at redline), driven by a badge ground speed `G* = lerp(60, 900, h) + 300 o` units/s (0 when parked) through a
   critically damped spring, settle 0.9 s, with `lo = 60`; born only in the bands y 16–33 and 67–84, so they never
   cross the title.
3. **Medallion** — two rings at r 33 and 30.5 units, weight 0.8 / 0.5, `C` at 34 % / 16 % (light 42 % / 22 %;
   Increase Contrast 60 % / 30 %), × `(0.6 + 0.4 wake)`. They are not drawn inside the title's ink box grown by 2.5
   units, so a wide title interrupts them like a coin legend.
4. **Rank pips** — 13 dots on r 37.5 from 216° to 324° (9° apart, across the top), radius 1.05 units: pip k is lit for
   k ≤ the current level, in the heat color of its threshold `(T_k / R)^0.6` (light: its label color, ≥ 3:1 on the
   body); the current level's pip has radius 1.45 and a glow σ 1.2 (dark); unlit pips white 12 % / black 12 %
   (Increase Contrast 30 %). The pips say "level k of 13" for every theme, custom sets included.
5. **Orbit motes** — comets running clockwise on the outer ring: 12 slots at `φ + 2π slot / 12`, filled in the order 0,
   6, 3, 9, 1, 7, 4, 10, 2, 8, 5, 11 so any count is evenly spread; count `2 + 6 h` (fractional: the last one fades in),
   × 1.5 at Max, capped at 4 / 8 / 12 for Soft / Standard / Max. `φ` advances at `ω`, a critically damped spring
   (settle 0.6 s) toward `2π (0.06 + 0.9 h + 0.5 o)` rad/s (0 when parked), on an accumulated phase. Each mote: a trail
   arc of `8° + 40° h`, weight 1.2 → 0.35 and opacity 1 → 0 toward its tail, in `C`; a head dot of radius 1.0 in `H`;
   × `wake`.
6. **Embers** (Standard and Max, from the overdrive tier up) — born on the lower ring arc (30°–150°), rising at 18–34
   units/s with a lateral drift `6 sin(seed + 7 age)`, life 0.6–1.1 s, `6 + 26 o` per s (Max × 1.6), drawn as streaks of
   velocity × 0.022 s from the flame core (`#FFF7D6`, light `#FFB020`) to `C`. They share the vehicle layer's pool (64).
7. **Flash and shockwave** — transitions, below.
8. **Title** — fill `C`; dark adds a vertical gradient from `mix(C, H, 0.45)` at the top of the ink box to `C` at its
   bottom, both ends moving toward `#FFF7D6` by `0.35 o` / `0.2 o` beyond the redline. Glow: the glyphs blurred with
   σ `1.0 + 1.6 h + 0.6 o` units (× 1.2 at Max) at `0.30 + 0.45 h + 0.15 o`, additive, in `C` (dark); light uses the
   undarkened `heat(h)` at `0.16 + 0.22 h` with normal blending, so the glow reads as a warm halo and never as a gray
   shadow. Off: fixed glow 0.30 / 0.20. Increase Contrast: glow × 0.5 and a solid fill.
9. **Sheen** (Standard and Max, `h ≥ 0.45`) — a diagonal band 18 units wide, `#FFF7D6` at 40 % (dark) / white at 50 %
   (light), masked to the glyphs, crosses them left to right in 0.7 s, once every `max(1.4, 5 − 3.5 h)` s on an
   accumulated phase: energy without moving the letters.
10. **Sparks** — the level-up burst, below.

At the redline tier (Standard and Max) the 4c camera shake moves the content, never the mask.

**Transitions** start on the title-change event, like the label slide and the vehicle (`τ` = time since the change;
≤ 0.75 s).

| | Outgoing title | Incoming title | Extras |
| --- | --- | --- | --- |
| Level up | scale 1 → 1.12 (easeOutCubic, 0.22 s), opacity `1 − smoothstep(τ / 0.16)` | from `τ = 0.08`: opacity `smoothstep((τ − 0.08) / 0.12)`, scale spring 0.72 → 1 (response 0.42 s, damping 0.6, ≈ 4 % overshoot), glow +80 % decaying with a 0.25 s time constant | at `τ = 0.06` the 4c flash (radius 8 → 40 units, 0.35 s, opacity 0.42 dark / 0.26 light × `(1 − u)²` × `min(1.6, 1 + 0.2 (titles climbed − 1))`, × 0.6 at Soft) and a shockwave ring r 33 → 44 (easeOutCubic, 0.5 s, weight 1.6 → 0, `H` at 70 % → 0); the newly lit pips pop in order 45 ms apart from `τ = 0.10` (spring 0 → 1, response 0.3 s, damping 0.5); a burst of `10 + 3 (climbed − 1)` sparks (≤ 18; Soft 6) from the title's edge, 40–90 units/s, drag 2.5/s, life 0.35–0.6 s |
| Level down | sinks 5 units (easeInOutSine) and fades over 0.3 s | from `τ = 0.12`: settles from 3 units above (easeOutCubic) while fading in over 0.35 s | the pips above the new level fade out together over 0.35 s; no flash, ring or sparks |
| Start-up (idle → title) | the idle label fades out over 0.25 s | fades in over 0.25 s, scale spring 0.85 → 1, glow +50 % (0.3 s) | `wake` recolors slate → heat; the outer ring draws on clockwise from 12 o'clock (0.45 s, easeOutCubic); pips light from 1 up, 35 ms apart from `τ = 0.12`; the flash at 60 % |
| Parking (title → idle) | fades out over 0.4 s | the idle label fades in over 0.4 s | pips go dark from the top, 30 ms apart; `wake` → 0 fades halo, glow, motes (their `ω` springs to 0) and streaks |
| Reduce Motion | 0.3 s crossfade | 0.3 s crossfade | pips crossfade; nothing else |
| Off | cut | cut | none |

A jump across several titles is one transition to the final title (the pips catch up in sequence). A change during a
running transition starts from what is on screen (4c resolved detail 9). When the new level has the same text (a
custom set may repeat a name) the title does not transition; color and pips still follow. A theme change crossfades
the title in 0.25 s with no effects.

**Idle.** The theme's idle label, laid out by the same rules, in `C(0)` slate (light: slate stepped to 4.5:1 on the
body) at 55 % (dark) / 70 % (light) × breathing `0.86 + 0.14 sin(2π t / 4.8)`; no halo, glow, motes, streaks, embers or
sheen; medallion at 60 %, pips unlit. Every 6 s the 4c idle light band (18 units, x −10 → 110 in 0.9 s, 0.63 s into the
gauge sweep) crosses the glyphs only: `#FFF7D6` at 75 % (dark) / slate at 60 % (light). After the agents stop the title
holds through hysteresis while `h` falls, so color, glow, motes and streaks calm down before parking, as the vehicle
coasts.

| Effects | Badge |
| --- | --- |
| Off | static title and pips, fixed glow, no halo breathing, motes, streaks, embers, sheen or sparks; cuts |
| Soft | halo breathing, ≤ 4 motes, ≤ 5 streaks, transitions with the flash at 60 %, ring and pip pops, 6-spark bursts, start-up and parking; no embers, sheen or shake |
| Standard | Soft plus ≤ 8 motes, ≤ 9 streaks, embers, sheen, full bursts, camera shake at the redline |
| Max | Standard with 1.6× particles, ≤ 12 motes, ≤ 14 streaks, glow × 1.2, shake × 1.4 |

**Accessibility and performance.** Reduce Motion caps the badge at Soft and removes motion: no motes, streaks,
embers, sheen, scale, offsets, flash, ring or sparks; speed reads from glow, color and the lit pips; transitions are
0.3 s crossfades; idle is static. Increase Contrast as above; Windows high-contrast themes: title, rings and lit pips
in the highlight color, unlit pips as outlines in the gray-text color, no glow, halo, motes, streaks or particles.
Hidden from VoiceOver and UI Automation (the hero announces value and title). Runs, renders and budgets exactly as the
vehicle layer (Accessibility and performance, above): the glyphs of each title are turned into a cached path or texture
once; the glow blends two pre-blurred copies (σ 1.6 and 4.2 units) per frame instead of blurring per frame; fixed pools
(motes 12, streaks 16, particles 64 shared); no per-frame allocation.

## 5. Settings

Native settings window (macOS: SwiftUI `Settings`/grouped `Form`, 600 pt wide so that the nine toolbar tabs fit in
every language — the Japanese labels need at least 580 pt — and 680 pt for 一般 and 懸浮窗; Windows: Fluent WPF window
with Mica). Sections:

- **一般 General** — Appearance (Auto / Light / Dark), Language (System / 繁體中文 / 简体中文 / English / 日本語),
  Launch at login, Speed metric
  (Output speed / Total throughput incl. input), Redline speed (slider 50–2000, step 50). Under the slider the heat
  strip carries one thin tick per speed title (positions `(t / R)^0.6`, so they stay put while the values move); below
  it `settings.redline.hint` and the `title.ladder` list: every title of the selected title theme followed by its
  threshold at the current redline (at R = 2000: `自行車 40 · 電動自行車 60 · … · 火箭 2.00k`), title in secondary
  color, value semibold tabular in the heat color of that threshold, wrapping across lines. The values update live
  while the slider is dragged; hovering or focusing a title highlights its tick. With Total throughput the same list
  shows that metric's values (compact `k`/`M`). Below the redline: the title theme picker and the custom set editor
  (Title themes, below).
- **選單列 / 工作列 Menu bar / Taskbar** — Display mode, Colored glyph, Show unit, `settings.bar.traffic` (network
  speed, section 10), `settings.bar.intelligence` (性能, section 10; the persisted key is unchanged), `settings.liveUnit` (section 9). Windows adds, right after
  `settings.bar.show`, the card `settings.taskbar.position` with the hint `settings.taskbar.position.hint` and the
  button `settings.taskbar.position.reset`: it puts the widget back at its default spot (`taskbar.offset` 0, section
  3, Position on the taskbar) and is disabled while the widget is already there, whether or not the widget is shown.
- **懸浮窗 Floating orbs** — the orb list and the per-orb editor (Floating orb pane, below).
- **特效 Effects** — Intensity (Off / Soft / Standard / Max), Celebrate new peaks, Response pulses,
  **Preview effects** button: runs the demo driver for 20 s sweeping through every tier.
- **費用 Cost** — currency, live value unit, subscription marks, price table status (section 9).
- **網路流量 Network Traffic** — measuring switch, tools, speed unit, the menu bar / taskbar readout and the per-agent
  measurement status; on Windows the optional helper service (section 10).
- **API** (`settings.report`, the same short name in every language; the broadcast icon) — opt-in live reporting to
  an API the user chooses, for example a leaderboard service: service, username, API key, what is sent, status, pause
  and *Forget this device* (section 11, `spec/REPORTING.md`).
- **資料來源 Data sources** — per source: toggle, detected path, status (`找到 128 個工作階段` / `未找到資料夾`),
  Change…, Reset. Below the list: `history.open` and `history.clear` (with a confirmation dialog `history.clear.confirm`).
  - omp: one row. The path is the default sessions root (METRICS 2); the status counts the sessions of the default
    root and every named-profile root together. Change… picks one `sessions` folder and replaces all automatic roots;
    Reset returns to them. Caption `settings.sources.ompHint`.
  - Pi: one row after Grok Build: the path is the default sessions root (METRICS 2), the status counts the main
    session files of every automatic root (`source.sessions`; sub-agent files are not sessions), Change… picks one
    folder and replaces the automatic roots (`mac.settings.sources.choosePi` on macOS), Reset returns to them.
    Caption `settings.sources.piHint`. An omp folder that is Pi's (METRICS 4.7) is counted under Pi only.
  - OpenClaw: one row after Pi. The path lists the state folders found (`~/.openclaw`, then any `~/.openclaw-<profile>`;
    METRICS 2, 4.14); the status counts the transcript windows of every agent database that are not sub-agents
    (`source.sessions`). Change… picks one folder or database — a state folder, an agent folder (OpenClaw's
    `agentDir`) or `openclaw-agent.sqlite` — and replaces the automatic roots (`mac.settings.sources.chooseOpenClaw`
    on macOS, a file-or-folder picker); Reset returns to them. A database of OpenClaw 2026.7 or older (no session
    tables) shows `warning.unsupportedFormat`. Caption `settings.sources.openclawHint`, which also says that turns
    OpenClaw hands to Claude Code, Codex or ACP agents count under those agents.
  - Hermes Agent: one row after OpenClaw. The path is the Hermes home (`~/.hermes`, Windows `%LOCALAPPDATA%\hermes`; METRICS 2,
    4.15); the status counts today's main sessions (`source.sessions`; sub-agent sessions are not counted). Change…
    picks a Hermes home or one `logs` folder and replaces the automatic home (`mac.settings.sources.chooseHermes` on
    macOS); Reset returns to it. A home without logs (installed, never run) shows `source.sessions` with 0. Caption
    `settings.sources.hermesHint`, which also says that turns of Hermes's Codex runtime count under Codex.
  - Factory Droid: one row after Hermes Agent. The path is the sessions folder (`~/.factory/sessions`, Windows
    `%USERPROFILE%\.factory\sessions`; METRICS 2, 4.16); the status counts today's main sessions (`source.sessions`;
    Task-tool workers are not counted). Change… picks the `.factory` folder or its `sessions` folder and replaces the
    automatic root (`mac.settings.sources.chooseDroid` on macOS); Reset returns to it. Caption
    `settings.sources.droidHint`, which also says that token usage comes from each session's settings file, so a
    call's tokens appear once droid saves them, and that sub-agents count on their own.
  - Antigravity: one row. The path lists the stores found (`~/.gemini/antigravity-cli`, `antigravity`,
    `antigravity-ide`); the status counts conversation databases (`source.sessions`). Change… picks one store folder
    (or its `conversations` folder) and replaces all three; Reset returns to them. A chosen `*-backup` store is never
    read (METRICS 4.8) and shows `source.folderMissing`. Caption `settings.sources.antigravityHint`.
  - WorkBuddy and WorkBuddy AI: one row each, adjacent in source order, each with its own folder (two groups where a
    platform groups rows by folder, as macOS does). The path is the data folder (`~/.workbuddy`, `~/.workbuddy-ai`);
    the status counts main session files (`source.sessions`; sub-agent transcripts and cloud conversations are not
    sessions here). Change… picks the data folder or its `projects` folder and replaces that source's folder only;
    Reset returns to the default. A data folder without `projects` (installed, never used) shows `source.sessions`
    with 0. Captions `settings.sources.workbuddyHint` and `settings.sources.workbuddyAiHint`, which also say that
    cloud conversations are not counted (METRICS 4.9).
- **關於 About** — version, GitHub link, license, privacy statement: read-only, local-only, no network, no telemetry,
  conversation content is never stored or displayed. With the cost feature: `privacy.statement` and the Network group
  with the master switch (section 9, `spec/NETWORK.md`). With traffic measuring: `privacy.traffic` follows
  (section 10). `privacy.perfScore` follows, always (section 12: what the performance score reads, what it keeps and
  what live reporting may send). With live reporting: `privacy.report` follows, and the texts switch while reporting is
  on (section 11); `privacy.perfScore` stays as it is: it already says that only live reporting with model sharing
  sends the score and the performance statistics.

All settings apply live without restart and persist immediately.

### Title themes (一般)

A group below the redline, in the same calm grouped style as the Floating orb pane (inset group, 13 pt text, 11 pt
captions, accent selection, no extra borders). Every change applies live everywhere and persists at once.

1. **Header** `titles.theme` with the caption `titles.theme.hint`.
2. **Theme cards** — a grid of 3 columns (2 below 560 pt of width), 8 pt gaps; each card 64 pt tall, 10 pt radius, a
   subtle fill one step above the group. Content: the theme name (13 pt semibold) over a **preview strip** (11 pt,
   one line): levels 1, 5, 9 and 13 of that theme joined by a tertiary ` · `, each title in the heat color of its
   threshold (light: its label color) — `步行 · 汽車 · 磁浮列車 · 火箭`, `凡人 · 元嬰 · 大乘 · 道祖`; a custom set shows
   its effective titles. The strip may end in `…` inside the card (it is a preview; the full ladder is in the list
   above and in the editor). Its tooltip lists the whole ladder with the thresholds at the current redline (at R = 2000:
   `凡人 0.5 · 練氣 40 · 築基 60 · … · 道祖 2.00k`). The selected card has a 2 pt accent ring, a faint accent fill and
   an accent check badge at its top trailing corner; hover lightens the fill (0.2 s ease-out).
   - The nine presets first, in picker order; then the small header `titles.custom` with the custom sets in creation
     order and a last dashed card `titles.custom.new` (accent text with a `+`), disabled at eight sets with the tooltip
     `titles.custom.limit`.
   - A `⋯` button fades in at the top trailing corner of a custom card on hover, focus or selection; it and the context
     menu (right-click, the context-menu key) offer: presets — `titles.duplicateEdit`; custom sets —
     `titles.custom.edit`, `titles.custom.duplicate`, `titles.custom.delete`. A new set appears with a 0.35 s spring
     (scale 0.85 → 1 with the fade; Reduce Motion: fade).
   - Keyboard: arrow keys move between cards (row-major), Space or Return selects, ⌘D / Ctrl+D duplicates and edits,
     ⌫ / Delete deletes a custom set (undo banner). Accessibility: a radio group labelled `titles.theme`; each card is a
     radio button labelled `titles.card.a11y` (name and the strip's titles joined with `list.separator`); custom cards
     add the `titles.custom.more.a11y` actions.
3. **Banner** for delete and reset all: `titles.custom.didDelete` / `titles.custom.didReset` with `orbs.undo`, 8 s,
   above the editor (as the orb list's banner).
4. **Editor**, shown below the cards while a custom set is selected (height spring 0.35 s; Reduce Motion: fade),
   headed `titles.custom.editing` (17 pt semibold). Two columns from 600 pt of width — the form, and a 200 pt preview
   column that stays in view while the pane scrolls; below that width the preview sits above the form.
   - `titles.custom.name` text field; `titles.custom.base` popup with the nine presets; caption
     `titles.custom.baseHint` (plus `titles.custom.vehicleArt` when the base is Vehicles).
   - **Title table**: a header row (`titles.custom.speedColumn` with the unit, `titles.custom.titleColumn`), then 14
     rows 30 pt tall with hairline separators: the idle row (`titles.custom.idleRow` in secondary in the speed column),
     then levels 1–13. Columns: the level number (11 pt, tertiary, right-aligned); the threshold at the current redline
     and metric (semibold tabular, heat color of that threshold, right-aligned; at R = 2000: 0.5, 40, 60, 80, 120,
     160, 240, 340, 480, 680, 1.00k, 1.40k, 2.00k), updating live with the redline slider; the text field, whose
     placeholder is the base's title in the current language; a reset button (`↺`, `titles.custom.reset`) that fades in
     when the field has text. The focused row gets a faint accent wash.
   - Caption `titles.custom.lengthHint`; footer text buttons `titles.custom.resetAll`, `titles.custom.duplicate` and,
     trailing, the destructive `titles.custom.delete`.
   - **Live preview**: the dashboard hero orb at 112 pt (badge, or the vehicle for a Vehicles-based set) and a floating
     orb at 96 pt in the style of the orb selected in the 懸浮窗 pane (default the first orb), both with Standard
     effects, ≤ 30 fps and only while visible; captions `menu.dashboard` / `settings.orb` with the size. They show the
     focused row's level at 1.05 × its threshold (idle row: idle); moving the focus to another row plays the real
     transition — down the table a level-up, up the table a level-down — so the editor is where the ladder can be felt.
     Typing updates the text live without a transition. With no field focused they show level 13. Reduce Motion: the
     same, with crossfades. The 96 pt orb shows at once when a title will tighten or shrink.
   - Keyboard: Tab / Shift-Tab through the fields; ↑ / ↓ move between the rows' fields; Return commits and moves down;
     Esc restores the value the field had when it was focused; ⌘Z / Ctrl+Z undoes inside the field. Accessibility: each
     field is labelled `titles.custom.row.a11y` (level, threshold with unit, base title) or `titles.custom.idle.a11y`;
     reset buttons `titles.custom.reset`; `titles.custom.tooLong` and `titles.custom.noEmoji` are announced politely;
     the previews are images labelled with `titles.custom.preview` and the title shown.

The editor and its previews stay under the 3 % budget of the 懸浮窗 pane's thumbnails while visible and draw nothing
when hidden.

### Floating orb pane (懸浮窗)

Calm and native: the platform's grouped form (macOS inset groups, Windows Fluent cards), content up to 680 pt wide,
13 pt text, 11 pt secondary captions, groups 10 pt radius, spacing from section 6, hairline separators, the system
accent color for selection, no extra borders. The pane scrolls when taller than the window. Top to bottom:

1. **Master group** — `settings.orb.show` switch; caption `orbs.allNote` (with `settings.bar` on macOS,
   `settings.taskbar` on Windows); then `orbs.names` popup (`orbs.names.auto` / `orbs.names.always` /
   `orbs.names.never`, 2b Name plate) with the caption `orbs.names.hint`.
2. **Orb list** — header `settings.orb` with `orbs.count` (`3 / 8`) trailing. One row per orb, 52 pt tall:
   - a **live thumbnail**: the real renderer at 38 pt (value only, section 2a LOD), effects capped at Soft, ≤ 15 fps,
     only while the pane is visible;
   - the name (13 pt; custom or automatic) over `orbs.row.detail` (style name · size) in secondary;
   - trailing: the orb's live value (`128 tok/s`, tertiary, tabular; `–` when unavailable) and an eye toggle
     (`orbs.visible`); a hidden orb's row dims to 50 % and its value reads `orbs.hidden`.
   - Selection tints the row with the accent; hovering, selecting or focusing a row rings that orb on the desktop (2b).
     Rows reorder by drag (list order only). Keyboard: ↑ / ↓ select, Space toggles visibility, ⌘D / Ctrl+D duplicates,
     ⌫ / Delete removes, Tab moves into the editor.
   - List footer: borderless `+` (`orbs.add`), `−` (`orbs.remove`) and `orbs.duplicate` on the left; text buttons
     `orbs.split` and `orbs.merge` on the right, with `orbs.split.hint` / `orbs.merge.hint` as help tags; merge is
     disabled with one orb, split with fewer than two detected sources (`orbs.split.unavailable`). After split, merge or
     remove an inline banner above the list offers `orbs.undo` for 8 s.
3. **Editor of the selected orb**, headed by its colour dots and name (17 pt semibold):
   - **`orbs.sources`** — first row `orbs.sources.all` (checkbox, caption `orbs.sources.allHint`), then every source in
     source order: checkbox, colour dot, name, trailing status (`source.notFound` / `source.disabled`) in secondary.
     With all sources on, the rows show checked and disabled. The last checked source cannot be unchecked (help tag
     `orbs.sources.lastOne`).
   - **`orbs.style`** — five cards in one row (≈ 120 × 112 pt; 3 + 2 rows below 560 pt of width): an animated preview
     (the real renderer at 64 pt, effects Soft, looping cruise → redline → cruise every 8 s, each card 1.6 s later than
     the previous, ≤ 24 fps, only while visible; with Reduce Motion a still frame at the redline) above the style name
     (`style.*`); the selected card has a 2 pt accent ring and a faint accent fill. Arrow keys move between cards; each
     card's accessibility label is `orbs.preview.a11y`. Below the cards the selected style's name (semibold) and its
     description (`style.*.desc`).
   - **Size** — `menu.size.small` / `.medium` / `.large` presets (112 / 148 / 196) as a compact segmented control left of
     a slider (96–240, `minimal` 64–240, step 4) with the value in pt. **Opacity** — slider 40–100 % with the value.
   - `orbs.showName` popup — `orbs.showName.auto` with the current automatic result, a separator, `orbs.showName.on`,
     `orbs.showName.off` (2b Name plate); changing it fades the desktop plate in or out live.
   - Switches: `menu.alwaysOnTop`; `menu.clickThrough` with the platform hint (the orb is then controlled from the menu
     bar / taskbar orb submenu); `settings.orb.fadeIdle`; `settings.orb.allSpaces` (macOS). Footer: the platform's
     Reset Position for this orb (it returns to the default corner, or to the creation placement rule when another orb
     is there).
   - **`orbs.advanced`** disclosure (collapsed; its summary lists `orbs.name`, `titles.theme`, `settings.redline`):
     `orbs.name` text field (placeholder `orbs.name.auto` with the automatic name; empty = automatic); `titles.theme`
     popup — `orbs.titleTheme.global` with the General theme's name, a separator, the nine presets, a separator, the
     custom sets — with the caption `orbs.titleTheme.hint` (2b `titleTheme`; editing sets happens in General);
     `settings.redline` as a segmented `orbs.redline.global` / `orbs.redline.custom`, with custom showing the General
     slider (same range, step and title ladder in this orb's theme, for the current metric) and the caption
     `orbs.redline.hint`.
4. Every change applies live and persists immediately: the desktop orb crossfades to a new style (0.25 s), springs to
   a new size around its docked edges, and shows its source chip for 2 s after a sources change.

The thumbnails and previews together stay under 3 % of one core while the pane is visible and draw nothing when it is
not.

## 6. Typography & spacing

- macOS: system font (SF Pro; PingFang TC/SC and Hiragino Sans chosen automatically). Windows: `Segoe UI Variable`
  (fallback `Segoe UI`) with `Microsoft JhengHei UI` (zh-Hant) / `Microsoft YaHei UI` (zh-Hans) / `Yu Gothic UI` (ja).
- Numbers: always tabular digits.
- Spacing scale: 4, 8, 12, 16, 20, 24, 32.
- Animations: springs (response 0.45 s, damping 0.82) for layout; 0.2 s ease-out for color and opacity.

## 7. Accessibility

- Every interactive element is reachable by keyboard and has an accessible label.
- The orb exposes its value as an accessibility element (`每秒 128 個 token，加速`; money unit: `a11y.orb.cost`,
  section 9), updated at most once per second.
  With several orbs each one is named after its sources and offers custom actions (2b, Accessibility and keyboard).
  The title in it comes from the orb's title theme (section 1); custom titles are read exactly as typed.
- Blinking is limited to the needle's shift lights and the reactor's bolt flashes, both ≤ 3 flashes per second.
- Respect Reduce Motion, Reduce Transparency (solid body instead of blur), Increase Contrast (stronger strokes),
  and high-contrast themes on Windows.
- Text contrast ≥ 4.5:1 for all secondary text.
- Network rates and totals are read as words, never as arrows (`a11y.orb.traffic`, section 10).
- A position set by dragging the Windows taskbar widget can be undone without a mouse: Settings → 工作列 →
  `settings.taskbar.position.reset` (section 3, Position on the taskbar).

## 8. Localization

Languages: zh-Hant (primary), zh-Hans, en, ja. Follow the system language unless overridden in settings (section 0).
All shared strings live in `spec/strings.json`; each platform embeds that table (copied at build time or as a
resource) and keeps platform-only strings in its own table with all four languages. Never hard-code user-visible text.
Preset title themes are localized ladders (`titles.<id>.*`, section 1); a custom title is user text and is shown the
same in every language, while an empty custom field follows the language through its base preset.

## 9. Cost (money unit)

Agent Speed can show what the live activity costs: next to tok/s there is a money unit such as US$/s or ¥/s, today's
cost on the dashboard and each day's cost in the usage history. Numbers come from METRICS 9 (tokens × the model's
official API price, converted at display time); network rules for the price table and exchange rates are in
`spec/NETWORK.md`.

### Principles

- **A display unit, not a new instrument.** Heat, gauge, tiers, titles, sparks, peaks, new-peak celebrations and the
  hero vehicle always follow the token metric (section 1), so switching an orb to money never changes how hot it looks
  and never celebrates by itself. Only the value, the unit line, the hover line and accessibility change.
- **API-equivalent, said once.** Every cost is an estimate at API list prices, labelled `cost.apiEquivalent` (API
  等值) where a cost first appears on a surface (section header, card title, unit menu item, tooltip, accessibility),
  never repeated on every number. `cost.apiEquivalent.hint` explains it in tooltips and Settings.
- **Unknown is never zero.** Responses of models without a price are left out and said so calmly: `–` when nothing is
  priced, a small `≥` when part of the value is missing, and a notice with the count. Free and local models are a real
  `0`.
- **Calm.** Same typography, tokens, spacing and motion as the token values; no green or red money colors, no flags, no
  currency animations. Numbers use tabular digits.

### Currency

Setting `settings.currency` (Settings → Cost), default **USD**. The supported currencies, their decimals and symbols
are `spec/assets/currencies.json`; names are `currency.<code>` in `spec/strings.json`.

- **Picker**: a button showing `code · name` opens a searchable list (macOS: a popover with a search field and a list,
  ≈ 320 × 400 pt; Windows: a Fluent flyout with an `AutoSuggestBox` above a `ListView`). The search field has focus;
  typing filters by code, localized name and English name — case-, width- and diacritic-insensitive, matching the
  start of any word (`dol` finds every dollar, `yen` and `円` find JPY, `nt` finds TWD). Groups: `settings.currency.common`
  — USD first, then the UI language's home currencies (zh-Hant: TWD, HKD, MOP; zh-Hans: CNY; ja: JPY), then the
  remaining `common` ranks in order — and `settings.currency.all`, every other currency sorted by localized name.
  While searching the groups collapse into one ranked list (code matches first). Rows: code (semibold, tabular, fixed
  width) · name · symbol (secondary, trailing); the current currency has a check. ↑ / ↓ move, Return selects, Escape
  closes; an empty result shows `settings.currency.noResults`. Selecting applies live everywhere (values crossfade
  0.2 s) and, for a non-USD currency without fresh rates, starts a rate fetch (NETWORK 4) while showing the bundled or
  last rates at once.
- **Rate status** (under the picker, 11 pt secondary, hidden for USD): `rates.status`
  (`US$1 = NT$32.55 · 2026/10/22 · open.er-api.com`: one US dollar and its value, both in amount format with at least
  4 significant digits, the rate date, the provider's host), with `rates.attribution` as a small link when ExchangeRate-API
  is the source, `rates.fallback` with the bundled date before the first fetch, `rates.stale` after 7 days, and a
  borderless `rates.refresh` button (disabled while refreshing, `rates.refreshing`; disabled with
  `settings.network.offHint` when the network is off). Errors show `rates.failed` in place of the status for 6 s, then
  the status returns.
- **Symbols**: `symbols[<UI language>]` when present, else `symbol` (`$` in English and `US$` elsewhere; `¥` for CNY in
  简体中文 and for JPY in 日本語, else `CN¥` / `JP¥`; `NT$`, `HK$`, `€`, `£`, `₩`, …). The symbol precedes the number;
  a symbol that ends in a letter is followed by a no-break space (`CHF 0.42`, `RM 1.20`). The four UI languages all
  write `.` for decimals and `,` for grouping, so formatting does not otherwise depend on the language.

### Money formatting

Two forms, both never in scientific notation, never negative, tabular digits. `d` = the currency's `digits`. Values
are decimals: `amount = costMicroUsd × rate / 1 000 000` (rates per second the same way) in decimal arithmetic (Swift
`Decimal`, C# `decimal`), never binary floating point, so both platforms print the same digits. Rounding is half up.
A value that rounds into the next row uses that row (`0.9996` → `1.00`, `99.96` → `100`).

**Rate** (the live value per second):

| Value | Rule | Examples |
| --- | --- | --- |
| exactly 0 | `0` | `0` |
| < 0.000 000 5 | `<0.000001` | `<0.000001` |
| < 1 | 2 significant digits (at most 6 decimals), then *trim* | `0.0042`, `0.03` (USD, from 0.030), `0.50`, `0.3` (JPY), `0.000081` |
| < 100 | 3 significant digits, then *trim* | `4.20` (USD), `12.30` (USD, from 12.3), `4.2` (JPY, from 4.20), `56.1` (KRW) |
| < 1 000 000 | integer with grouping | `128`, `1,234` |
| else | METRICS 7 compact | `1.28M` |

*Trim*: drop trailing fractional zeros beyond `d` decimals, then pad with zeros to `d` decimals.

**Amount** (today, history, breakdowns, tooltips):

| Value | Rule | Examples |
| --- | --- | --- |
| exactly 0 | `0` with `d` decimals | `0.00` (USD), `0` (JPY) |
| < 0.000 000 5 | `<0.000001` | `<0.000001` |
| < 1 | as the rate: 2 significant digits, then *trim* | `0.42`, `0.042`, `0.03`, `0.0042`, `0.42` (JPY) |
| < 1 000 000 | `d` decimals with grouping | `12.34`, `1,234.56`, `1,235` (TWD), `12,345` (KRW) |
| else | METRICS 7 compact | `1.28M` |

An amount is shown with its symbol in front (`$12.34`, `NT$1,235`, `₩1.28M`, `CHF 0.42`); a rate is shown as the number
with the unit `cost.unitPerSecond` after it (`0.0042 US$/s`), or on the orb as the number over the unit line. Examples
(number · unit): `0.0042` · `$/s` (en, USD) · `0.0042` · `US$/s` (zh-Hant, USD) · `0.03` · `¥/s` and `4.20` · `¥/s`
(zh-Hans, CNY) · `0.66` · `¥/s` (ja, JPY) · `0.14` · `NT$/s` · `5.57` · `₩/s`. Tooltips add, for a non-USD currency,
the USD amount in secondary text: `NT$1,235（US$37.94）`. Test vectors: `spec/fixtures/money-format.json`
(`python3 spec/tools/prices.py` checks them with a reference formatter).

The unit is `cost.unitPerSecond` (`%@/s` with the symbol: `US$/s`, `¥/s`). Where a unit can be hidden (menu bar
`settings.bar.unit` off, `minimal` orbs), the rate carries the symbol in front instead, like an amount: `$0.0042`.

### Orb

- **Per-orb unit**: the 2b data model gains `"unit": "tokens" | "cost"` (default `tokens`; unknown values → `tokens`).
  Add, Duplicate and Split copy it; Merge keeps the target's; the single-orb migration sets `tokens`.
- **Value**: the orb's live cost rate (METRICS 9.5) over its sources, without subscription sources unless
  `settings.cost.includeSubscriptions` is on; rate format; the unit line shows `cost.unitPerSecond`. The odometer, glow,
  lean and jitter work exactly as for tok/s (they are driven by the token heat). The value shrinks to the style's max
  width like any value.
- **Partial**: when `liveCostKnownShare < 0.995` and the value is not 0, a `≥` precedes the value at 60 % of its size
  in the secondary color (baseline-aligned, part of the value's width); the hover line becomes `cost.partial`.
- **Nothing priced**: live output > 0 but `liveCostKnownShare == 0` → value `–` in the idle value color, hover line
  `cost.unknownModels` with up to two model names (`+n`).
- **Subscription only**: every source of the orb is marked as a subscription and excluded → value `0`, hover line
  `cost.subscriptionOnly`.
- **Idle**: `0` with the unit line; title `tier.idle` as always.
- **Hover line** in money mode: `cost.today.summary` (`今日 US$4.20 · 3 個 agent`, today's billable cost of the
  orb's sources, METRICS 9.6) instead of `today.summary`.
- **Switching** — three ways, all per orb, all persisted at once:
  1. **Click the unit**: a single click (press and release within 4 pt of movement and 0.5 s) on the unit line toggles
     the unit. Hit area: the unit text bounds grown to at least 44 × 20 pt, clipped to the disc. The toggle fires when
     the system double-click interval has passed without a second click, so a double-click still opens the dashboard
     and never toggles; a drag that starts on the unit moves the orb. Hover over the hit area raises the unit text from
     55 % to 85 % with a 0.5 pt underline at 40 % (0.15 s) and shows `orbs.unit.toggleHint` as a tooltip after 0.8 s; the
     cursor stays the drag cursor elsewhere on the disc and becomes the pointing hand over the unit. In `minimal` (no
     unit line) the value is the hit area. Click-through orbs ignore clicks: use the menu bar / taskbar orb submenu.
  2. **Context menu**: `orbs.unit` ▸ `orbs.unit.tokens` (`tok/s`) / `orbs.unit.cost` (`US$/s · API 等值`), check on
     the current one; placed after `orbs.sources` (2b item 3). The quick menu's per-orb submenu (2b master switch) has
     the same item.
  3. **Settings** → 懸浮窗 → orb editor: `orbs.unit` as a two-segment control (`tok/s` | `US$/s`) directly under the
     style cards.
- **Transition**: the value and the unit slide up/out and up/in like a title change (0.3 s, the new value already in
  the new unit); Reduce Motion: 0.15 s crossfade. The thumbnail in Settings shows the same value.

### Menu bar, taskbar and dashboard

- **Setting** `settings.liveUnit` (Settings → Cost; also in 選單列 / 工作列): `tok/s` (default) or money, shared by the
  menu bar / taskbar widget and the dashboard hero. Clicking the hero's unit toggles it (single click, immediate — the
  hero has no double-click action) with the same hover affordance.
- **Widget**: `[glyph] 0.0042 US$/s`, or `$0.0042` with the unit hidden. The width rule of section 3 is unchanged
  (the value's length changes only at a new digit count). Glyph: token heat, as always.
- **Hero**: value 44 pt with `US$/s`, the speed title below (token heat), context line unchanged. `≥` and `–` as on
  the orb.
- **Sources**: the secondary line becomes `1.2M · US$4.20` (today's output · today's cost of that source); `–` for the
  cost when every response of the source today is unpriced, a `≥` before it when some are; a subscription source shows
  `cost.subscription.tag` (訂閱) instead of an amount unless subscriptions are included, then its API-equivalent amount.
  The row's live value follows `settings.liveUnit`.
- **Today**: a full-width row under the 2×2 grid, styled like 今日峰值: `today.cost` with the caption
  `cost.apiEquivalent`, the amount (semibold, tabular) trailing, and an ⓘ button. The ⓘ popover (≈ 280 pt): per-source
  amounts with colour dots and thin share bars (sorted by cost), `cost.unpriced` when today has unpriced responses,
  `cost.subscriptionExcluded` listing excluded sources, and the rate line (`rates.status`) for a non-USD currency.
- **Metric info** (ⓘ in the footer): `cost.info` is appended as a second paragraph.
- **Boards**: 費用 in the sub-gauge and the footer's today cost (section 2c, *Live metrics*); no cost per row, group
  or agent.

### Usage history

- **Summary**: a fifth card `history.summary.cost` (費用（API 等值）): the range's cost (amount format), secondary line
  `history.summary.costPerDay` (日均 over active days) or the delta vs the previous period (same rules as the other
  cards), and a sparkline of daily cost. Five cards share one row from 860 pt of content width (the default window), else 3 + 2.
- **Chart**: a two-segment control in the chart header, `history.chart.output` | `history.chart.cost`. With 費用 the
  bars show daily cost stacked by source in source colors (single source: one bar in its color), the right axis and the
  speed line are hidden, and today's bar is hatched as usual. The tooltip card always carries a cost line
  (`history.table.cost`: `US$4.20`, with `≥` and `history.unpricedDay` when some responses are unpriced) and the top 3
  models show their cost next to their share (a free or local model shows `cost.free`, an unpriced one
  `cost.unknown`). Single-day breakdowns sort and size the bars by the chosen metric.
- **Table**: column 費用 (`history.table.cost`) after 輸入（含快取）: amount format; `≥` prefix plus tooltip
  `history.unpricedDay` when some responses are unpriced; `–` when none is priced or the day has no cost data
  (tooltip `history.costUnavailable`). Sortable; copy row includes it.
- **Subscriptions**: when at least one source is marked, the toolbar shows a checkbox `history.includeSubscriptions`
  (initially `settings.cost.includeSubscriptions`; the window remembers its own choice). Excluded sources keep their
  tokens everywhere but add nothing to cost cards, bars or the column; in the source picker they carry the tag
  `cost.subscription.tag`. Selecting such a source directly shows its API-equivalent cost (the card title then reads
  `history.summary.costApi`).
- **Unknown prices** (METRICS 9.10): when the view has unpriced responses, the footer's left side adds
  `history.unpricedNotice` (Σ `unpriced` of the view) with a `history.unpricedNotice.show` link. Its popover (macOS: a
  SwiftUI popover, arrow up; Windows: the flyout-styled `Popup` above the notice, as before; pt = DIP) explains every
  request of that count for the window's current source, range and subscription choice, and offers them as text.
  380 pt wide, 16 pt padding; the groups scroll when the content would exceed 440 pt, header and footer stay put.
  Top to bottom:
  1. **Header**: `cost.unknown` (13 pt semibold) with the count trailing (13 pt, secondary, tabular).
  2. **Source groups**, one per source that has rows, in the order of 9.10: a 7 pt source dot, the source name
     (12 pt semibold) and the Σ requests of its rows trailing (12 pt, secondary, tabular); 12 pt above every group but
     the first. Up to 20 rows per group, then `history.unpricedModels.more` (the number of further rows and their
     requests; 11 pt, tertiary; the copied text has all).
  3. **Rows**, 7 pt apart, inset 14 pt (aligned with the source name), two lines:
     - the model (12.5 pt medium, middle truncation, selectable; empty → `cost.noModelName` in secondary italic) and,
       trailing, its requests (12 pt, secondary, tabular, full number);
     - 11 pt secondary: the **reason chip** — `cost.reason.<reason>` at 10.5 pt medium in a capsule (horizontal padding
       5 pt, vertical 1 pt, the label colour at 9 % fill and 0.5 pt stroke at 16 %; the same neutral look for every
       reason: an unknown price is a gap, not an error) — then, joined by ` · `, `history.unpricedModels.via` with the
       provider when there is one, `speed.value.fast` with one bolt glyph (as in 4b, item 3b) when the facts were
       Fast, and `history.unpricedModels.lookup` with the canonical key when it differs from the model. The line
       truncates at the tail; its tooltip holds the whole line plus `history.unpricedModels.seen` (the first and last
       day, one date when equal, in the locale's short date format). The chip's own tooltip is
       `cost.reason.<reason>.hint`, followed for `unpriceable` by the table's rule text (English, as published).
  4. **Priced now**: when `pricedNow` > 0, `history.unpricedModels.pricedNow` (11.5 pt, secondary) under the groups
     (alone when every request of the count is priced now).
  5. **Footer**, under a divider: `history.unpricedModels.hint` (11.5 pt, secondary), then a row with the
     `history.unpricedModels.copy` button (small bordered; SF Symbol `doc.on.doc` / Fluent `Copy` glyph) and, beside
     it, `history.unpricedModels.copyHint` (11 pt, tertiary, wraps). The button copies the text of METRICS 9.10 for the
     same view to the clipboard as plain text; it then shows a checkmark and `history.unpricedModels.copied` for
     1.6 s (0.15 s crossfade; Reduce Motion: instant), keeping its width so nothing shifts. A clipboard error (Windows
     can report one while another app holds the clipboard; retry once after 100 ms) shows
     `history.unpricedModels.copyFailed` the same way, with the warning glyph.
  The popover follows the window live: a new range, source, subscription choice, finished analysis or price table
  recomputes it (rows that the current table now prices move to `pricedNow`); the scroll position stays. Esc or a
  click outside closes it and focus returns to the link.
- **Frozen days**: the cost card's tooltip says `history.costFrozen`; a day's cost never changes when prices change
  (METRICS 9.7).
- **Rate note**: for a non-USD currency the footer's right side starts with `rates.historyNote` (以 2026/10/22 的匯率換算)
  before `history.privacy`.
- **CSV**: adds `cost_usd` (exact, 6 decimals, from micro-USD; empty when the day has no cost data),
  `unpriced_requests`, `price_version`, and for a non-USD display currency `cost_<code>` (lower-case code, the
  currency's `digits` + 2 decimals) converted with the rate in use. Subscription exclusion does not apply to CSV: every
  row has its own cost and the `source` column.

### Settings → Cost (費用)

A new pane between 特效 and 資料來源, the platform's grouped form like the other panes:

1. **Currency** — the picker and rate status (above).
2. **Live value** — `settings.liveUnit` (`settings.liveUnit.tokens` / `settings.liveUnit.cost`), caption: orbs choose
   their own unit (`settings.liveUnit.hint`).
3. **Subscriptions** — caption `settings.cost.subscriptionsHint`; one row per detected source (colour dot, name,
   trailing switch `settings.cost.subscription`); below, `settings.cost.includeSubscriptions` with
   `settings.cost.includeSubscriptionsHint`. Defaults: no source marked, include off.
4. **Price table** — status `prices.status` (version · updated date · model count; `prices.bundled` when the bundled
   table is current), `prices.lastSync` (relative time, `prices.never` before the first sync), the `prices.autoSync`
   switch with `prices.autoSync.hint`, a `prices.check` button with inline result (`prices.checking`,
   `prices.upToDate`, `prices.updatedTo`, `prices.failed`, `prices.newerSchema`), and a `prices.viewSource` link that
   opens the table on GitHub. When today has unpriced responses, `prices.unknownToday` lists them (selectable text, so
   the user can report them) from the live view of METRICS 9.10 over every enabled source: one
   `prices.unknownToday.item` per distinct model and reason (`neko-model-7（無公開價格）`; an empty model reads
   `cost.noModelName`), ordered by requests descending, then the model's UTF-8 bytes, joined by `list.separator`. The
   whole line's tooltip adds each reason's `cost.reason.<reason>.hint` once; details and the copyable text live in the
   History window.

**About**: the privacy text becomes `privacy.statement`; a **Network** group (`settings.network`) holds the master
switch with `settings.network.hint`, followed by the update controls of UPDATES 4. With the switch off the update,
price and rate controls are disabled and show `settings.network.offHint`.

### Accessibility

- Orb in money mode: label `a11y.orb.cost` (`每秒 0.0042 美元（API 等值），高鐵`, the number formatted as shown, the
  currency's name instead of its symbol); named orbs `a11y.orb.costNamed`; partial values wrap the amount in
  `a11y.orb.costAtLeast`; nothing priced → `a11y.orb.costUnknown`. Same 1 s update limit.
- The unit toggle is the custom action `a11y.orb.toggleUnit` (VoiceOver action, UI Automation and the context-menu
  key); a change is announced once as `a11y.unitChanged`. The hero's unit is a button with the same label.
- Every amount reads with the currency's name (`12.34 美元`); `≥` reads as `a11y.orb.costAtLeast`, `–` as
  `cost.unknown`. Nothing relies on color.
- The currency list is a standard list with a search field; each row reads `code, name`.
- Unknown-prices popover: the header and each source group are headings (VoiceOver header trait; UI Automation
  heading level 2 and 3); a group reads `source name, history.unpricedDay`. Each row is one element: label = the model
  (or `cost.noModelName`), value = the reason label, provider (`history.unpricedModels.via`), `speed.value.fast` when
  Fast, the lookup key, `history.table.requests` with the full number and the days seen; help = the reason's hint.
  The copy button is reachable with Tab and Enter/Space; its result is announced once (`history.unpricedModels.copied`
  or `.copyFailed`: VoiceOver announcement, UI Automation notification) without moving focus. Nothing relies on
  colour: the chip is text.

## 10. Network traffic

Agent Speed shows how fast this computer uses the network and how much of it the agents use: live download and upload
speed, today's bytes per source and per agent, and a daily history from the day measuring began. Numbers come from
METRICS 10; the optional Windows helper is `spec/WINDOWS-SERVICE.md`.

### Principles

- **A quieter second instrument.** Traffic never drives heat, the gauge, tiers, titles, sparks, peaks, new-peak
  celebrations, pulses or the hero vehicle. It uses secondary typography and neutral colors; source colors appear only
  where sources are listed.
- **Bytes only, said plainly.** Settings and About say once that only byte counts are measured, on this computer.
- **Unknown is never zero.** When agent traffic cannot be measured (Windows without the helper, a failed sampler, days
  before measuring began) the value is `–` with the reason, never `0`.
- **Two scopes, always named.** This computer (`traffic.machine`) is everything on the network hardware; agent values
  are the agents' own processes plus, by default, the tools they run (`traffic.includesTools`).
- **A disabled source shows no traffic.** A source turned off in Settings → Data Sources disappears from traffic as it
  does from tokens: no dashboard row, no part of an orb's, the menu bar's or the taskbar's agent readout, no history
  bars, table values, CSV bytes or picker entry. This computer's values are unchanged. Its bytes are still recorded
  (METRICS 10.5), so turning it back on brings back its traffic, as it brings back its tokens.

### Formatting

- Numbers and units: METRICS 10.11 (`512 B/s`, `1.28 KB/s`, `12.4 MB/s`, `48.2 MB`, `3.10 GB`; bits: `10.0 Mb/s`).
- Direction: `↓` download, `↑` upload, download always first. In running text a pair is `traffic.pair`
  (`↓ 48.2 MB · ↑ 3.10 MB`); in native text the arrows are the characters U+2193 and U+2191 in the secondary color.
- **Fixed cells** wherever a rate updates in place (orb, menu bar, dashboard trailing values): the number is
  right-aligned in a cell the width of `8.88` with tabular digits, the unit left-aligned in a cell the width of `MB/s`
  (bits: `Mb/s`), so a readout never changes width or position when values or units change.
- `settings.traffic.unit`: bytes (default) or bits, for rates only; totals are always bytes.

### Floating orb readout

Each orb has a `traffic` setting (2b data model) that picks what its two-line readout shows: `"sources"` — the
traffic of the orb's own sources, like everything else an orb shows (2b), with tools per `includeTools` —
`"machine"` — the whole computer, for a desktop network meter — `"intelligence"` — 性能, the highest performance score
of the orb's own agents, with their thinking rate (*Performance readout*, below; section 12) — or `"off"`. **Default `"sources"`** for new orbs and for orbs migrated from earlier versions: the readout appears
only while those agents use the network, so an idle orb looks exactly as before. Prototyped (2026-10-22) for every
style in both appearances at 112, 148 and 196 pt.

**Form**: two lines, download over upload, each `arrow · number · unit` in fixed cells; the two lines share their left
edge (a tiny table) and the block is centered on the orb. Rejected after prototyping: one line above the value (wider
than every style's free area at the 7 pt floor; it ran into the LED arc), two fields flanking the agent dots in the
bottom gap (no room beside six dots, crowds the disc edge, collides with the reactor rings), and two micro arcs at the
bottom edge (pretty but unreadable without hovering).

| Style | Placement | Baseline of the line nearest the value | Text size `t` | Drawn when a line fits in |
| --- | --- | --- | --- | --- |
| `led` | above the value, ↑ line nearest | c − 0.245 S | max(7 pt, 0.050 S) | 0.44 S |
| `liquid` | as `led` | c − 0.245 S | max(7 pt, 0.050 S) | 0.44 S |
| `reactor` | above the value inside the core window | c − 0.175 S | max(7 pt, 0.046 S) | 0.32 S |
| `needle` | inside the hub below the value, ↓ line nearest | c + 0.108 S | max(7 pt, 0.046 S) | 0.285 S |
| `minimal` | above the value | c − 0.228 S | max(7 pt, 0.054 S) | 0.50 S |

- **Gap to the value**: the ↑ line's baseline sits ≈ 0.089 S above the value's cap top in `led`, `liquid` and `minimal`
  (≈ 0.062 S in `reactor`), well over twice the gap between the two lines, so the block reads as its own group and
  never crowds the digits.
- **Line**: arrow (vector glyph from the atlas, 0.50 t wide, 0.66 t tall, stroke `max(0.9 pt, 0.11 t)`, round caps) ·
  gap 0.16 t · number (weight 600, tabular, right-aligned in the `8.88` cell) · gap 0.16 t · unit (weight 500, size
  0.82 t, left-aligned in the `MB/s` cell). Line pitch 1.32 t. A line is ≈ 5.6 t wide (39 pt at 7 pt), so the readout
  shows from S ≈ 96 in `led`, `liquid` and `minimal`, from S ≈ 122 in `reactor` and from S ≈ 137 in `needle`
  (computed per size with the platform font, never truncated or shrunk below 7 pt); below that, and in thumbnails, it
  is not drawn.
- **`needle`**: while the readout is enabled and fits, the unit `tok/s` leaves the hub and is printed on the dial face
  above it — baseline c − 0.254 S (centred between the shift lamps and the hub rim), max(7 pt, 0.046 S), weight 500,
  the secondary token at 75 %, tracking +0.4 —
  drawn under the graduations and the needle like the legend of a real tachometer. It stays there while the readout is
  quiet, so nothing moves when traffic comes and goes.
- **Colors**: number = the value token at 80 % (dark white 78 %, light `#0B0D12` 76 %); arrow and unit = the unit /
  secondary token (white 50 % / black 50 %). `liquid` below its surface uses the split rule of 2a.3 layer 10 (number
  white 94 %, unit 72 %, black 55 % shadow σ 0.015 S in dark; ink 88 % / 62 % in light). Increase Contrast: number
  92 %, unit 70 %. Windows high contrast: the theme's text and gray-text colors. No glow, lean, jitter or odometer.
- **Quiet rule**: visible while the shown `↓ + ↑` rate is at least 1 KB/s; after 10 s below it fades out (0.6 s); it
  fades in (0.25 s) when traffic returns.
- **Centering**: in `led`, `liquid` and `minimal` the readout stack — this block, the value, the unit, the title and
  the new-peak pill — stays centered as one stack: while the block shows, everything moves down by half the block's
  height over the value's cap top, `max(0, ((valueBaseline − 0.70 × valueSize) × S − (nearest − 1.32 t − 0.70 t)) / 2)`
  (0.70 = cap height; ≈ 0.095 S in `led`), but never so far that the title's bottom (its baseline + 0.22 × its
  size) comes within 0.05 S of the agent dots' top (dots line − half a dot); small orbs, whose 7 pt text floor makes
  the block relatively taller, hit that limit first. The move follows the quiet-rule fade with smoothstep easing (the value glides
  down as the block fades in and back up once it has faded out; Reduce Motion: instant) and ignores the pill's fade.
  The agent dots never move. `reactor` and `needle` keep their readout in the core window and the hub, so nothing
  moves there.
- **Updates**: once per second with each sample (METRICS 10.7); characters are replaced in their cells, no animation.
- **New-peak pill**: in `led`, `liquid`, `reactor` and `minimal` the pill appears where the readout is; the readout
  fades out (0.15 s) for the pill's 1.8 s and back.
- **Hover**: unchanged (the title crossfades to `today.summary`; the readout stays).
- **Frame policy** (2a): a value change renders one frame of an otherwise static orb (at most one per second); fades
  render at 30 fps for their duration; a quiet readout draws nothing. Budgets unchanged.
- **Unavailable**: in `"sources"` mode without per-process data (Windows without the helper, a failed sampler) the
  readout is not drawn and the context-menu item shows `traffic.unavailable.short`; Settings explains. With
  `settings.traffic.enabled` off the readout is never drawn and the menu item is disabled.
- **Controls**: context menu `orbs.traffic` ▸ `orbs.traffic.sources` / `orbs.traffic.machine` /
  `orbs.traffic.intelligence` / `orbs.traffic.off` (check on the current one), placed after `orbs.unit`; the same submenu in the quick menu's per-orb submenu; Settings
  → 懸浮窗 → orb editor: `orbs.traffic` popup under the unit control with the caption `orbs.traffic.hint`. Add,
  Duplicate and Split copy it; Merge keeps the target's.
- **Accessibility**: while the readout is visible the orb's label adds `a11y.orb.traffic` (`下載每秒 12.4 MB，上傳每秒
  186 KB`), within the label's 1-second update limit.

### Performance readout

With `traffic` = `"intelligence"` the same two lines show 性能 — the highest performance score among the orb's
agents (METRICS 6.9; section 12) — and the thinking rate of those agents (6.9, *Live thinking*: a total,
not a maximum) instead of network speed. It needs neither the traffic switch nor the Windows helper, so it is never
unavailable.

- **Form** (user's choice, 2026-10-28): two lines, each **centred on the orb on its own**, the intelligence line
  on top and larger, the thinking line below it and smaller, like a gauge's main and sub reading. Order and sizes are
  the same in every style; the network readout's placement, text size `t`, quiet rule, colors and frame rule apply.

  | Line | Size | Content (left to right) |
  | --- | --- | --- |
  | intelligence | `p = 1.25 t` (`needle`: `1.1 t`) | `intel.glyph` (性能 / 性能 / Perf / 性能) · gap 0.20 p · the score in compact form (section 12) |
  | thinking | `t` | `intel.thinking.glyph` (思考 / 思考 / TH / 思考) · gap 0.16 t · `liveThinking` formatted as METRICS 7 · gap 0.16 t · `tok/s` (not in `needle`) |

  - Glyphs: text, weight 600, 0.82 × the line's size, the unit token's color. Numbers: weight 600, tabular digits,
    the line's size, the number color. `tok/s`: weight 500, 0.82 t, the unit token's color.
  - 性能's forms (section 12): live in the number color; resting in the unit token's color; none `–` in the unit
    token's color. The provisional mark (dotted underline, 0.22 p below the baseline) sits under a provisional
    score; it stays 0.34 t clear of the thinking line's cap top and takes no width, so fit and centring ignore it.
  - Centring: each line is measured with its current text (advances only, no side bearings) and its left edge is
    `center − width / 2`, rounded to the device pixel, so a line moves only when its width changes (digit count).
    Every following piece starts at its advance, also rounded to the device pixel, so the glyphs stay sharp.
  - Baselines: above the value (`led`, `liquid`, `reactor`, `minimal`) the thinking line sits on the placement's
    nearest baseline and the intelligence line 1.32 t above it; `reactor` puts the thinking line at c − 0.155 S,
    0.020 S lower than its nearest baseline, so the larger intelligence line clears the core's rim (tuned
    2026-10-28: at c − 0.175 S its corners touched the rim at 148 pt). Below the value (`needle`) the intelligence
    line's baseline is the nearest baseline + 0.70 (p − t), so its cap top stays where a `t` line's would be, and the
    thinking line is 1.32 t below it.
  - Fit: drawn when both lines fit in the placement's `fit × S` with their widest text: the intelligence line with
    `8,888` (the widest compact score; it was `888`), the thinking line with `8.88` (and `tok/s` where it carries
    it); otherwise neither line is drawn.
  - **`needle`** (tuned 2026-10-28; line size 2026-10-29): the 性能 line is `p = 1.1 t`, because at 1.25 t `8,888`
    stays inside the hub only from 151 pt (187 in English), which would take the readout off the medium orb (148);
    at 1.1 t it shows from 134 / 139. The thinking line leaves out `tok/s`, which the dial's legend already shows while
    the readout fits (the network readout's `needle` rule), and the lines must also stay inside the hub: with `w`
    a line's widest width and `y` its baseline below the center, `√((w / 2)² + y²) ≤ 0.223 S` for both lines (0.012 S
    inside the hub's 0.235 S radius). With `tok/s` the thinking line (≈ 6.5 t with 思考) never fit the hub, and at
    the 7 pt floor the lower line sits relatively lower, which a width alone cannot catch.
  - Smallest sizes that show the readout (macOS fonts, Chinese and Japanese / English; re-measured 2026-10-29 for
    `8,888` and `Perf`): `led` and `liquid` 104 / 102 pt, `minimal` 92 / 89, `reactor` 143 / 139, `needle` 134 / 139
    (with 智力's `888` they were 104 / 98, 92 / 86, 143 / 134 and 128 / 122).
  - Recentering: the block's top for the stack shift is the nearest baseline − 1.32 t − 0.70 p (`led`, `liquid`,
    `minimal`); everything else of the stack shift is the network readout's.
  - While the readout is visible and 性能 is none (no agent of the orb has 3 timed requests in the last 30 minutes),
    line 1 shows `–`; the thinking line keeps its rule below.
  - **Thinking hidden at zero** (user's choice, 2026-10-28; some sources never report reasoning): the thinking line
    shows only while its displayed value is above 0. When the shown `liveThinking` is `0` it stays drawn for 3 s
    (a pause between reasoning bursts does not flicker), then fades out (0.25 s) and the intelligence line glides
    (smoothstep, 0.3 s; Reduce Motion: at once) to the **single-line position**: its baseline on the placement's
    nearest baseline in every style (`reactor`: the thinking line's c − 0.155 S; `needle`: the nearest baseline
    + 0.70 (p − t)), so it sits where the line next to the value would be and the gap to the value is unchanged.
    A value above 0 brings the thinking line back at once (fade in 0.25 s, the intelligence line glides back up).
    The stack shift (`led`, `liquid`, `minimal`) uses the single line's top (nearest baseline − 0.70 p) while the
    thinking line is hidden and follows the glide. Fit is unchanged (both lines must fit), so the readout never
    appears or disappears because of the thinking value. Accessibility then reads `a11y.orb.intel.only`.
- **Quiet rule** (revised 2026-10-28; unchanged for 性能): visible while 性能 has a value — live or resting
  (section 12) — or an agent of the orb is `generating`; 10 s after neither holds it fades out (0.6 s), and it fades
  in (0.25 s) when either returns — the network readout's timings. So after work the pale resting score stays, on one
  line once 思考 has hidden at zero, for as long as its agents are in the agents list (METRICS 6.7: 10 minutes after
  their last line) and its score lasts (30 minutes after its timed requests), then the readout leaves. Chosen over
  hiding with the last token (`Σ w = 0`, the earlier rule): a long tool run generates nothing while the agent works,
  and the user's decision shows the resting score pale
  rather than not at all; the 10-minute bound comes from the list, so an idle orb still returns to its plain face.
- **Updates**: on every engine tick (METRICS 6, 500 ms), with the value. A change of 性能 by at least 1 % and 3
  counts, up or down (section 12, *Motion*: at most 0.7 s at 30 fps); every other change replaces characters in
  their cells. Any other change renders one frame of an otherwise static orb, at most two per second.
- **Hover**: the title crossfade of the network readout. The orb's tooltip (Windows) adds `perf.tooltip.top` with
  the score in full form and the project of the agent that supplies it (`性能 1,286 · 最高：nova`; nothing while none)
  and the caption `perf.tooltip.note` (1000 為參考水準，沒有上限). The agent rows' score card is section 12's.
- **Accessibility**: while the readout is visible the orb's label adds `a11y.orb.intel` with the score words of
  section 12 (`性能 1,286，4 個 agent 中最高，思考每秒 412 個 token`), or `a11y.orb.intel.only` while 思考 is hidden, within
  the label's 1-second update limit.
- **Settings**: the orb editor's `orbs.traffic` popup lists `orbs.traffic.intelligence`; its caption becomes
  `orbs.traffic.hint.intel` while it is chosen.

### Menu bar and taskbar

Two independent readout blocks can follow the menu bar or taskbar value (user's request, 2026-10-29: the score and
the network speed together, the score with a switch of its own; 性能 replaced 智力 in it the same day):
- `settings.bar.traffic` picks the network block. Stored values `off` (default), `machine` and `agents`; an unknown
  value reads as `off`. The control is labelled `settings.bar.traffic` with the choices `settings.bar.traffic.off`,
  `.machine` and `.agents`.
- `settings.bar.intelligence` (a switch, default off) adds the intelligence block.
- A stored `intelligence` from an earlier build migrates once to `settings.bar.traffic` = `off` and
  `settings.bar.intelligence` = on.

Order, left to right: the value, the money block (section 3) when shown, the network block, the intelligence block,
each after an 8 pt (8 px) gap. With both on, the item is the sum of both fixed widths, and still never changes width
with the values.

- **Block**: after the value and after the money block when section 3 shows one, 8 pt gap; two lines centred on the
  bar, 9 pt (menu bar) / 10 px (taskbar), weight 500, tabular digits, 10 pt / 11 px pitch; numbers in the label color
  at 85 %, everything else in the secondary color; fixed cells — so the item's width never changes with the values
  and section 3's width rule is untouched. Display mode "glyph only" hides it.
- **Network** (`settings.bar.traffic` = `machine` or `agents`): `↓ 1.28 MB/s` over `↑ 64.0 KB/s`. It stays visible at `0 B/s` (a system meter is
  expected to stay). It needs `settings.traffic.enabled`: with measuring off it is not drawn. Accessibility and the
  Windows tooltip add `a11y.orb.traffic`.
- **Intelligence** (`settings.bar.intelligence` on; user's choices, 2026-10-28 and 2026-10-29): 性能 — the highest performance score among the agents of
  all enabled sources (METRICS 6.9; section 12) — and their thinking rate, with the orb readout's words
  and number formats (*Performance readout*) at the bar's one size:

  | Line | Label cell (left-aligned) | Number cell (right-aligned) | Unit cell |
  | --- | --- | --- | --- |
  | 1 | `intel.glyph` (性能 / 性能 / Perf / 性能) | the score in compact form (section 12), right-aligned over the number and unit cells | (taken by the score) |
  | 2 | `intel.thinking.glyph` (思考 / 思考 / TH / 思考) | `liveThinking` (METRICS 6.9, *Live thinking*), formatted as METRICS 7 | `tok/s` |

  - Cells, left to right: the label cell, as wide as the wider of the two labels in the current language; 3 pt / 3 px;
    one number cell shared by both lines, as wide as the wider of `8.88k` (the widest METRICS 7 form below 1 M tok/s)
    and `8,888` (the widest compact score, section 12; 1 pt wider in SF Pro 9 pt); 2.5 pt / 3 px; the unit cell
    (`tok/s`). Line 1 has no unit: its score is right-aligned over the number cell, the 2.5 pt / 3 px gap and the unit
    cell, so it ends where line 2's `tok/s` ends and both lines share the block's right edge (user's choice,
    2026-10-29: a score that stopped one unit short of the thinking line read as misaligned). The provisional mark
    follows the score. The block's
    width depends on the language only: in SF Pro 9 pt, with every cell rounded up to a whole point, 74.5 pt in
    Chinese and Japanese and 75.5 pt in English (the label cell holds `Perf`), plus the 8 pt gap (73.5 / 68.5 with
    智力 / IQ). A text wider than its cell (thinking at 1 M tok/s or more) widens the cell at once and narrows it
    after 6 s, section 3's rule for the value.
  - 性能's forms (section 12): live in the number color (label 85 %); resting in the secondary color; none `–` in the
    secondary color. The provisional mark under a provisional score is centred 0.17 × the text size below line
    1's baseline (1.5 pt at 9 pt), which keeps its dots 1.5 pt clear of line 2's cap top; it takes no width.
  - Updates: on every engine tick (500 ms); characters are replaced in their cells, no animation (no count in the
    bar: a status item stays still).
  - **Quiet state**: the block stays, like a system meter. While nothing is generated (`Σ w = 0`) the thinking number
    is `–` in the secondary color and `tok/s` stays; 性能 keeps its own form — the highest resting score, pale, after
    work, and `–` when no agent has a score.
  - **Thinking hidden at zero** (user's choice, 2026-10-28): while 性能 has a value (live or resting) and the
    displayed thinking rate has been `0` for 3 s, line 2 is not drawn and line 1 is centred vertically in the block (its baseline halfway between the two
    lines' baselines, the item's text centre); the cells and the block's width are unchanged, so nothing beside it
    moves. A rate above 0 draws line 2 again and puts line 1 back at once (no animation in the bar). Accessibility
    then reads `a11y.orb.intel.only` (`性能 1,286`). Chosen over hiding the block: a block that came and went with every
    pause would shift every menu bar item to its left (on Windows, the taskbar's neighbours) whenever an agent
    started or stopped, against section 3's rule that the item never jitters in width; the fixed cells keep it calm,
    and `–` says "nothing right now" without claiming a score of 0.
  - Independent of network measuring: it needs neither `settings.traffic.enabled` nor the Windows helper and is never
    unavailable.
  - Accessibility: the item's label adds, after the network words when that block is shown, `a11y.orb.intel` with the score words of section 12 (`性能 1,286，4 個
    agent 中最高，思考每秒 128 個 token`; the thinking rate in METRICS 7 form), `a11y.orb.intel.only` while line 2 is hidden,
    or `intel.none` while 性能 is none. Windows tooltip: its first line adds the same text after ` · `, where the
    traffic words go, and a second line `perf.tooltip.top` names the agent that supplies the score.
- **Settings**:
  - **Network control.** `settings.bar.traffic` appears in 選單列 / 工作列 (section 5) and in 網路流量 (*Settings →
    網路流量*, below). In 選單列 / 工作列 it is always enabled. While `settings.traffic.enabled` is off, the two
    network choices are disabled (shown, not selectable), and a stored network choice stays selected and draws
    nothing, as before. In 網路流量 the control follows that pane's state like its other controls.
  - **Intelligence switch.** `settings.bar.intelligence` appears only in 選單列 / 工作列, as the row right after
    `settings.bar.traffic`, a switch that is always enabled. While it is on, its caption is
    `settings.bar.intelligence.hint`.

### Dashboard

A section **Network** (`traffic.title`) between Today and the footer, while `settings.traffic.enabled` is on:

1. **Header**: the title in the section-header style and, trailing, this computer's live rates
   (`↓ 1.28 MB/s  ↑ 64.0 KB/s`, 13 pt, tabular, secondary, fixed cells); tooltip `traffic.machine.hint`.
2. **Sparkline**: the last 60 s of this computer's traffic, full width, 28 pt tall, mirrored — download as an area
   above a hairline midline (label color, fill 16 %, 1 pt line 55 %), upload below it (fill 10 %, line 40 %). One
   linear scale for both, its top the largest value of the window rounded up to a 1-2-5 step (at least 10 KB/s);
   a scale change eases over 0.3 s (Reduce Motion: jumps). Redrawn once per second; hover or drag shows a crosshair
   with the time and both rates. Accessibility: an image labelled `traffic.a11y.chart`.
3. **Source rows**: sources with traffic today or now, sorted by today's ↓ + ↑, up to 3 (`traffic.expand` shows the
   rest in place): colored dot, name, secondary line today's `traffic.pair`, and trailing the live rates as two stacked
   11 pt lines (`↓ 24.0 KB/s` / `↑ 1.20 KB/s`, fixed cells; a direction below 0.5 KB/s shows the tertiary `–`). A row
   opens the Usage History window with the traffic metric and that source selected.
4. **Caption** (11 pt, tertiary): `traffic.includesTools` or `traffic.excludesTools`. When agent traffic is
   unavailable the rows are replaced by the reason (`traffic.unavailable.helperMissing`, `.helperStopped`,
   `.helperOutdated`, `.denied`, `.sampler`) and, on Windows, a borderless button (`traffic.helper.install` or
   `traffic.helper.update`) that opens Settings → 網路流量 — never the administrator prompt itself. No traffic today:
   `traffic.empty`.

**Agents** (section 4.4): when an agent has attributed traffic today (METRICS 10.6, tools per the setting), its
secondary line ends with ` · ↓ 2.10 MB`; the row's score card carries `traffic.agent.tooltip` with both directions
in its facts block (section 12). Agents whose
traffic could only be assigned to their source show nothing.

### Usage history

- **Chart**: the metric control gains a third segment, `history.chart.traffic`. Daily bars are mirrored: download above
  the axis, stacked by source in source colors (one source: its color), upload below the axis in the same colors at
  55 %. With all sources, a hairline outline bar behind each day shows this computer's total, so the agents' share of
  all traffic is visible. The speed line and right axis are hidden; the left axis uses METRICS 10.11. Days before
  `trafficSince` show a faint hatched band labelled `history.traffic.notRecorded`; today is hatched as usual.
- **Tooltip card**: a traffic line (`traffic.pair`), `history.traffic.tools` when tools are included,
  `history.traffic.machine`, and `history.traffic.partial` with the recorded time when the day is partial (whole
  minutes, at least one: `time.minutes` below an hour, else `time.hours` or `time.hoursMinutes`).
- **Table**: column `history.table.traffic` after the cost column: `traffic.pair`, sorted by ↓ + ↑; `–` with the
  tooltip `history.traffic.notRecorded` before measuring began; the partial tooltip on partial days; copy row includes
  it. Single-day breakdowns size the bars by ↓ + ↑ when the traffic metric is selected.
- **Summary**: no new card; with the traffic metric the chart header shows the range total (`traffic.pair`).
- **Traffic without token usage**: recorded agent traffic is part of a source's history. A day of the range with agent
  traffic of the selection (tools per the setting; with all sources, any enabled source's — this computer's own total
  does not count) is a day with data even without token usage: the table lists it (requests, output and input `0`,
  `history.noSpeed`), the CSV exports it, the source picker offers its source, `全部` reaches back to it, and the
  window's empty state appears only when a range has neither. The summary cards keep counting tokens only (活躍 N 天
  and 單日最高 count days with token usage). A single day of one source without token usage breaks down into that
  source's one bar when the traffic metric is selected.
- **Opening on traffic**: when the window opens or its source or range changes while the chart shows output or cost,
  and the range has agent traffic but no token usage, the chart switches to `history.chart.traffic`, so a source that
  only used the network (or the dashboard's Network row of one) never opens on an empty chart. It never switches back
  by itself; the user can still choose output or cost.
- **Footer**: when the range starts before `trafficSince`, the left side adds `history.traffic.since`.
- **CSV**: every source row adds `traffic_rx_bytes`, `traffic_tx_bytes`, `traffic_rx_tools_bytes`,
  `traffic_tx_tools_bytes` (empty before measuring began); each date adds a row with `source = machine` carrying only
  `traffic_rx_bytes` and `traffic_tx_bytes`.
- **Clear history**: the confirmation adds `history.clear.traffic` when traffic history exists.

### Settings → 網路流量 (Network Traffic)

A pane titled `settings.traffic` between 費用 and 資料來源, the platform's grouped form like the other panes:

1. `settings.traffic.enabled` (default on), caption `settings.traffic.enabled.hint`.
2. `settings.traffic.includeTools` (default on), caption `settings.traffic.includeTools.hint`.
3. `settings.traffic.unit`: `settings.traffic.unit.bytes` | `settings.traffic.unit.bits`.
4. `settings.bar.traffic` (the same control as in 選單列 / 工作列).
5. **`traffic.status`** group:
   - macOS: `traffic.status.ok` with the caption `traffic.status.macos`; after a failure the reason
     (`traffic.unavailable.sampler`) and a `traffic.retry` button.
   - Windows: header `traffic.helper`; a status line with a small dot (green running, gray standby or not installed,
     amber outdated, red error — always with its text): `traffic.helper.notInstalled`, `traffic.helper.idle`,
     `traffic.helper.running`, `traffic.helper.outdated`, `traffic.helper.error`. Below it `traffic.helper.explain`
     (11 pt secondary, always visible) and the link `traffic.helper.details` (opens `spec/WINDOWS-SERVICE.md` on
     GitHub). One primary button with the Windows shield icon: `traffic.helper.install` or `traffic.helper.update`; when
     installed, a destructive text button `traffic.helper.remove` (asks `traffic.helper.removeConfirm` /
     `traffic.helper.removeDetail`). Pressing install or update first shows `traffic.helper.uacNote` inline with
     `traffic.helper.installing` and a small progress indicator, then the administrator prompt appears. Results replace
     the note inline: `traffic.helper.installed`, `traffic.helper.removed`, `traffic.helper.cancelled` (secondary, not
     red), or `traffic.helper.error`.

Everything applies live; turning measuring off stops the sampler and the helper connection at once.

### About and privacy

- About shows `privacy.traffic` as a second paragraph under `privacy.statement`, then `privacy.perfScore` (section 12).
- README privacy sections add, when the feature ships:
  - English: "Network traffic: Agent Speed counts how many bytes programs send and receive, on this computer only. It
    never reads content, addresses or destinations, and sends nothing anywhere. On Windows, per-agent traffic needs an
    optional helper service that runs as the restricted Local Service account and only counts bytes per process; it is
    installed and removed with one administrator prompt each."
  - 繁體中文：「網路流量：Agent Speed 只在本機計算各程式收發的位元組數，從不讀取內容、網址或連線對象，也不會傳送到任何地方。在
    Windows 上，要顯示各 agent 的流量需要安裝選用的輔助服務；它以權限受限的「本機服務」帳戶執行，只統計每個程式的位元組數，安裝和
    移除各需要一次系統管理員確認。」
  - 简体中文：「网络流量：Agent Speed 只在本机计算各程序收发的字节数，从不读取内容、网址或连接对象，也不会发送到任何地方。在
    Windows 上，要显示各 agent 的流量需要安装可选的辅助服务；它以权限受限的“本地服务”账户运行，只统计每个程序的字节数，安装和
    卸载各需要一次管理员确认。」
  - 日本語：「通信量：Agent Speed はこのコンピューター上で、プログラムが送受信したバイト数だけを数えます。内容・アドレス・接続先
    は一切読まず、どこにも送信しません。Windows で agent ごとの通信量を表示するには、任意のヘルパーサービスが必要です。権限の
    少ない「ローカル サービス」アカウントで動作し、プロセスごとのバイト数だけを数えます。インストールと削除のときに、それぞれ一度
    だけ管理者の承認が必要です。」

### Accessibility

- Rates and totals are read as words (`a11y.orb.traffic`; today's totals `traffic.a11y.totals`; a day or range that
  its context names — history rows, chart days, breakdown bars, the table's traffic cells — `traffic.a11y.pair`, after
  the `history.table.traffic` label); arrows are never read; units are read as written (KB, MB). Recorded times read
  as `time.minutes`, `time.hours` and `time.hoursMinutes`. Both platforms read a history row with its traffic.
- The sparkline is an image labelled `traffic.a11y.chart`; the history table is the text equivalent of the chart.
- Status dots always have text; nothing relies on color.
- Reduce Motion: the readout's fades become instant; scale changes jump.

## 11. Live reporting (即時上報)

Agent Speed can share its live numbers with an API the user picks — for example a leaderboard service such as Codex
Radar. The protocol and everything a service sees are `spec/REPORTING.md`; how each value is computed and when a
report is sent are METRICS 11. Strings are `report.*` in `spec/strings.json`.

Agents show their reasoning effort and speed as the effort capsule (section 4); evidence categories stay internal and
missing values stay unknown. See `spec/EXECUTION-MODES.md`. The optional profile controls in Settings → API
provide avatar preview/upload/removal and approved one-time device pairing when the service advertises support
(`spec/REPORTING-EXTENSIONS.md`). Explicit profile actions follow the network master switch; periodic sharing
does not upload images or create identities. Existing measurement formulas and orb styles remain unchanged.

The API page has one personal-profile card: avatar and editable name together. Before shared-identity binding,
the name uses the existing device registration/rename flow; after binding it edits the shared display name without
changing the device's registered username. Image selection keeps a local preview until explicit upload. A separate
Manage devices surface contains identity creation, unlinking, pairing and the device list, with no duplicate avatar
or name editor. Services without profile capabilities retain the existing registration/name controls.

**Naming.** The feature is named after where the numbers go: the settings tab and its window title are
`settings.report` — "API" in every language — and the main switch is `report.enabled` (即時上報到 API · 实时上报到
API · Live reporting to an API · API へのライブ送信). Running text keeps the short name 即時上報 (实时上报 · live
reporting · ライブ送信) where a sentence needs a noun: the quick menu, the dashboard indicator and invitation, About and
the statuses. Links to a service's own pages keep their words (`report.service.leaderboard`).

### Principles

- **Off until the user says so.** The default is off. Nothing is sent before the user confirmed that service — in
  the consent sheet, or with `report.invite.accept` on the one-time dashboard invitation (below) — and nothing at all
  while the network master switch (section 9, NETWORK 1) is off.
- **Show exactly what is sent.** The preview is the real body of the next report, not a description of it.
- **Always say where.** Wherever reporting is mentioned, the destination host is named; the menu and the dashboard
  show quietly that reporting is on.
- **Calm.** No dialogs, notifications, badges or animations for reporting, apart from the one-time dashboard
  invitation. The orb is unchanged. Problems appear in the pane's status line and as a small attention dot on the
  dashboard indicator.
- **The service's words are not ours.** Service names and descriptions are shown as plain text, links only when
  `https`. Error messages from the service are never shown; every status is a localized string of this table.
- **No internals.** Status codes, error classes, field names and anything else technical are never shown — not even in
  parentheses; each failure is one of the plain sentences below, and the details go to the log (REPORTING 2.3).
- **Unknown is never zero** applies to the payload too: unavailable values are sent as a status, never as `0`.

### Settings → API

A pane titled `settings.report` ("API"; toolbar tab and window title alike, with the broadcast icon — SF Symbol
`dot.radiowaves.left.and.right`, on Windows the closest Segoe Fluent Icons glyph) between 網路流量 and 資料來源, the
platform's grouped form like the other panes (13 pt text, 11 pt secondary captions, inset groups, accent selection).
Top to bottom:

1. **Master** — `report.enabled` (即時上報到 API; default off) with the caption `report.enabled.hint`. With the network master switch
   off the switch is disabled and the caption becomes `settings.network.offHint`. Turning it on opens the consent
   sheet (below) unless this service is already confirmed and registered; then reporting resumes at once. Turning it
   off sends one `leaving` report (METRICS 11.5) and keeps the registration.
2. **Service** (`report.service`) — a popup: the presets (`Codex Radar`, `https://deng.codexradar.com`, covering the domain `codexradar.com`), a separator,
   `report.service.custom`. Custom shows the `report.url` field (placeholder `report.url.placeholder`), validated as
   the user types (0.5 s debounce, REPORTING 2.1; local only): `report.url.invalid`, `report.url.https`; a loopback
   `http` URL shows the amber note `report.url.loopback`. **When discovery is fetched** (REPORTING 3.4): choosing a
   service in the popup, committing a valid custom URL (Return or leaving the field), opening the consent sheet,
   accepting the dashboard invitation, `report.service.retry`, and the refreshes while reporting is on (also while an
   accepted invitation still waits for the document). Opening Settings or this pane never touches the network.
   Below the popup the **service card** (REPORTING 3): the name (13 pt semibold), `report.service.host` with the API
   host (secondary, selectable), the description (11 pt secondary, at most 3 lines), `report.service.retention` when
   given, and borderless link buttons `report.service.terms`, `report.service.privacy`,
   `report.service.leaderboard`. Before the first fetch the card shows only what the app itself knows, without links:
   a preset's name and `report.service.host` with its host, or for a custom URL just `report.service.host`; the
   fetched document's details fade in when they arrive (no fade with Reduce Motion). While fetching:
   `report.service.checking` with a small inline progress indicator. A failed fetch shows one plain sentence with a
   symbol and, trailing, the small push button `report.service.retry` — never a status code, an error class or
   anything from the response (REPORTING 3.1); while reporting is being set up after the invitation the status line
   (item 6) carries the sentence instead, so it is not said twice:

   | Outcome of the fetch | Text | Symbol |
   | --- | --- | --- |
   | `404`, any other `4xx` except `410` and `429`, or a `200` that is not an `agent-speed-report` document: not a JSON object, over 64 KiB (typically a web page), another `protocol` | `report.service.notSupported` | red octagon |
   | an `agent-speed-report` document that breaks REPORTING 3.2 | `report.service.invalid` | red octagon |
   | no connection, the host not found or refusing, a timeout | `report.service.unreachable` | amber triangle |
   | a TLS failure, or a redirect to a non-`https` URL | `report.service.insecure` | amber triangle |
   | `429`, `5xx`, a `3xx` (a fourth redirect included) or any other status | `report.service.unavailable` | amber triangle |
   | `410` | `report.status.gone` | red octagon |

   The details (status, error class, the first invalid field) are logged. While reporting is off, the message
   belongs to the attempt that caused it and is cleared when the pane closes, so a later visit never shows an old
   error. After a change that needs consent (REPORTING 3.3): `report.service.changed` with the button
   `report.service.review` (opens the consent sheet). Switching services pauses the old one (its identity is kept,
   `report.others`) and opens the consent sheet for the new one.
2b. **Transport** (`report.transport`) — under the service card, a two-segment control `report.transport.http` /
   `report.transport.websocket` (default WebSocket, also for services set up by earlier builds; only the user's
   choice of HTTP is stored, per service origin, Storage below) with the caption `report.transport.hint`. With
   WebSocket chosen the caption becomes `report.transport.source` (the data comes from watching local logs, METRICS
   11.9 `realtime`) and, when the document lacks a valid WebSocket capability, the secondary note
   `report.status.wsUnsupported` replaces it, led by an information glyph (SF Symbol `info.circle`, Segoe Fluent
   `Info`) in the caption's color: reports go over HTTP meanwhile (METRICS 11.9, effective transport), the choice
   stays selected, and the status line (item 6) is HTTP's.
   When the service's `minIntervalMs` is above 50 the amber note `report.transport.throttled` (with the interval)
   says the connection is slower than the real-time level. Switching applies at once: the old transport stops (its
   unanswered reports are dropped, METRICS 11.9) and a new run starts on the new one.
3. **Username** — always shown, whether or not the service's document is known and whether or not this device is
   registered: `report.username` text field with the caption `report.username.public` and, trailing, the borderless
   `report.username.random` (a dice glyph: SF Symbol `dice`, Segoe Fluent `Shuffle`). Registered, it shows the
   registered name. Not registered yet, it shows the name the next registration will use: the user's own once they
   edited it (stored locally, no request), else the computer name fitted to the rules (Default username, below).
   Validation as the user types against the service's rules (REPORTING 4.3) — the stored or fetched document's, and
   before the first fetch the protocol grammar with the default lengths 2–32, since a service's narrower rules
   cannot be known without fetching (REPORTING 4.3): `report.username.invalid.length`,
   `report.username.invalid.characters` (`report.username.invalid.ascii` for an ASCII-only service),
   `report.username.invalid.start`, `report.username.invalid.spaces`, and `report.username.invalid.noSpaces` when
   the service allows no spaces. When a document arrives whose rules the user's own name breaks, the message appears
   under the field and nothing is registered until the name fits; the computer-name default is fitted to the new
   rules instead. The edit commits on Return or when the field loses focus (Esc restores the previous name): when
   registered it renames (REPORTING 4.4) and shows the result inline — `report.username.updated` (secondary, fades
   after 3 s), `report.username.taken`, `report.username.invalid.reserved`; when reporting waits for a new name
   (`report.status.usernameTaken`, `report.status.usernameRejected`) it registers at once with the new one. When a
   registration answers `username_taken`, the service uses API keys and a key is set, the error offers the button
   `report.username.transfer` with `report.username.transfer.hint`: it registers again with `"transfer": true`
   (REPORTING 4.7). This is the only path that sends `transfer`.
4. **API key** — shown once this device exists for the service (the user confirmed it) and the known document's
   `apiKey.mode` is `optional` or `required`; before that, a key is entered in the consent sheet. The row never shows
   for a service whose document is unknown or says `none`: most services use no keys, and an empty key field would
   suggest that one is needed. A secure field `report.apiKey` with the placeholder `report.apiKey.optional` or the
   caption `report.apiKey.required`, the caption `report.apiKey.hint`, the link `report.apiKey.get` when the
   service gives `apiKey.url`, and `report.apiKey.clear`. A saved key shows as `••••` plus its last 4 characters; it
   is never shown again in full, never in the preview and never logged. Saving a key while reporting waits for one
   (`report.status.apiKey`) registers at once.
5. **What's sent** (`report.sections`) — the caption `report.sections.hint`, then one switch per optional section:
   `report.section.sources`, `report.section.models`, `report.section.cost` and `report.section.traffic` (the
   agents' own traffic), all on by default. The computer's overall network traffic is never reported (REPORTING 1.3);
   a switch stored for it by an earlier build is ignored. A section the service does not list is shown off and
   disabled with `report.section.notAccepted`; the traffic switch is disabled with `report.section.trafficOff` while
   traffic measuring is off (the report then says `status: "off"`). Under the models switch, the 11 pt secondary
   caption says plainly what the switch also shares (METRICS 11.10; user's decisions 2026-10-29: no switch of its own
   and no new consent; the score is reported for ranking and statistical analysis):
   `report.section.models.perfScore` — the performance score, the thinking speed and per-model performance statistics
   (sums of output and duration, the first-content latency distribution, thinking, cache, tool-call counts, time and
   failures, active time), only per-model totals, never anything about a single request or tool call and no prompts,
   commands, paths or timestamps — while the service's known document advertises the `perfScore` extension, and
   before any document is known (the preview then assumes it too); `report.section.models.thinking` while the
   document advertises only the retired `taskScore` extension, which now receives the thinking speed alone (METRICS
   11.10, legacy). It hides when the service advertises neither, since nothing more is sent, and while the section is
   not accepted (`report.section.notAccepted` stands there instead). The caption stays visible with the switch off, so the user
   knows what turning it on shares. The button `report.preview` opens the preview.
6. **Status** (`report.status`) — one line with a small dot that always has text: green `report.status.reporting`
   (with `time.secondsAgo` for the last delivery); gray `report.status.off`, `report.status.never`,
   `report.status.idle`, `report.status.quiet`, `report.status.paused` / `report.status.pausedIndefinitely`,
   `settings.network.offHint`; amber `report.status.connecting`, `report.status.registering`,
   `report.status.retrying` and `report.status.registerFailed` (each with its reason under it as a footnote, one of
   `report.error.*`), `report.status.rateLimited` (with the local time); red (needs the user) `report.status.apiKey`,
   `report.status.usernameTaken`, `report.status.usernameRejected` (the service refused the name as invalid or
   reserved), `report.status.review` (an accepted invitation needs the consent sheet, REPORTING 3.3),
   `report.status.keyMismatch`, `report.status.rejected` (three malformed or invalid reports in a row),
   `report.status.forbidden` (the service refuses this device by its own policy), `report.status.upgrade`,
   `report.status.gone`. While an accepted invitation cannot fetch the document, the line is the fetch's sentence of
   item 2, red or amber as its symbol. With WebSocket effective (item 2b): green `report.status.streaming` (with the
   last acknowledged report's time) instead of `report.status.reporting`; amber `report.status.wsReconnecting` with
   the last success (`report.status.wsReconnectingNever` before any); red
   `report.status.wsMismatch` and `report.status.wsReplaced`, both with the push button `report.service.retry`
   (METRICS 11.9 *Try Again*), `report.status.wsIncompatible`, `report.status.wsNonce`, `report.status.wsClientState`.
   A connected socket or a Pong is never shown as reporting: only an acknowledged report is. Under the line, while
   WebSocket is effective and the run delivered or lost anything, one 11 pt secondary line `report.ws.stats` (latency
   p50 and p95 of METRICS 11.9, delivered and not delivered counts) with every counter in its tooltip
   (`report.ws.counters`); `report.ws.noLatency` stands for the latency while no sample exists. When the service returns a rank: `report.status.rank` after the line. Actions
   on the right: `report.pause` as a pull-down with `report.pause.hour` and `report.pause.indefinite`, or
   `report.resume` while paused; while reporting is being set up, `report.service.retry` after a failed fetch or
   registration and `report.service.review` for `report.status.review`.
7. **This device** (`report.device`) — `report.device.detail` (username and host) with, trailing, the borderless
   link button `report.device.profile` when the service returned a `profileUrl` (the user's public page, REPORTING
   4.1; opened in the browser on click), `report.device.fingerprint`
   followed by the first 8 characters of the key's RFC 7638 thumbprint (monospaced, selectable), and the destructive
   text button `report.forget`. It asks `report.forget.confirm` with `report.forget.detail` (destructive default
   `report.forget.action`, `history.cancel`), then calls the deletion endpoint (REPORTING 4.6): success →
   `report.forget.done`, the key and device id are deleted and reporting is off; failure → `report.forget.failed`
   (with its `report.error.*` reason) and the button `report.forget.localOnly` and `report.forget.localOnly.detail` (with the service's privacy link).
   After `report.status.keyMismatch` the button `report.reset` appears (`report.reset.detail`): delete the local
   identity, make a new key and device id and register again.
8. **Other services** — a collapsed disclosure `report.others`, only when identities for other services exist: one
   row per service (host, username) with `report.forget`.

Everything applies at once and persists immediately; a username or API key change takes effect with the next report.

### Consent sheet

A sheet (macOS) / `ContentDialog` (Windows), 520 pt wide, opened when reporting is turned on for a service that is not
yet confirmed, or by `report.service.review` (after a change, or when an accepted invitation needs it):

- Title `report.consent.title` (the service name), body `report.consent.body` (with the service's interval).
- The service card (as in the pane); the **username field**, filled with the pane's name (by default the computer
  name, below) and editable here, with its caption `report.username.public` and the random button; the API key field when needed;
  and the section switches with their defaults and captions (5 above, the models switch's caption included). The
  preview shows the name exactly as it will be sent.
- **Nothing is sent before `report.start`**: no key is created and no request — not even registration — is made while
  the sheet is open; only the discovery document has been fetched (REPORTING 3). Cancelling leaves no trace on the
  service.
- **Live preview**: a disclosure `report.preview.title`, expanded, showing the next report body pretty-printed in the
  monospaced system font at 11 pt (keys in the secondary color, values in the label color), at most 220 pt tall and
  scrollable, refreshed once per second while visible (the text changes in place, the scroll position stays), with
  `report.preview.copy` and the caption `report.preview.hint`.
- `report.consent.never` and `report.consent.honesty` as 11 pt secondary footnotes.
- Buttons: `history.cancel` and the default `report.start`. Starting generates the key (when this service has none),
  registers (REPORTING 4.1) and shows `report.status.registering` inline; errors stay in the sheet next to the field
  they concern (`report.username.taken`, `report.status.apiKey`; anything else as its `report.error.*` reason beside
  the buttons). Success closes the sheet
  and the pane's status turns green with the first delivery.

Keyboard: Tab order follows the layout; Return starts (when valid); Esc cancels. Accessibility: the preview is a
read-only text area labelled `report.preview.title`; switches and fields have their visible labels.

### Preview window

`report.preview` in the pane opens a small window (macOS panel / Windows dialog, 560 × 640 pt) with the same
preview: the request line and headers on top — `X-AgentSpeed-Signature` shown as `…` with `report.preview.hint`,
`Authorization: Bearer ••••` when a key is set — then the body, `report.preview.next` (seconds until the next
report) and `report.preview.copy`. While reporting is off or quiet it shows the report that would be built now. With
WebSocket effective the request line is the connection's (`GET wss://…/v1/reports/ws` and its handshake headers), the
body is the `report` message's body including `realtime`, and `report.preview.onChange` replaces
`report.preview.next`. It can also be opened from the dashboard invitation (`report.invite.preview`). Before the service's document is known it
shows the body only — no request line or headers, whose API URL comes from the document — built with the default
switches and the protocol's default limits, with the `perfScore` extension's fields (METRICS 11.10), and
`report.preview.assumed` instead of `report.preview.hint`: the real report can only be smaller, since a service that
lists fewer sections or advertises fewer extensions receives fewer.

### Default username and random names

- Before registration the username field (pane, consent sheet, invitation) starts with the **computer name** fitted
  to the service's rules (the protocol grammar with the default lengths before the first fetch): macOS `SCDynamicStoreCopyComputerName` (the name in System Settings → General → Sharing, e.g.
  `Zoe’s MacBook Pro`); Windows `GetComputerNameExW(ComputerNamePhysicalDnsHostname)` (keeps the letter case the user
  typed), else `Environment.MachineName`. When fitting leaves nothing usable, a random name is used.
- **Fitting** (`fit`, vectors `usernames.fit`): NFKC; remove apostrophes (`'` `‘` `’` `ʼ` `` ` ``); for an ASCII-only
  service decompose (NFKD) and drop combining marks (General Category `M*`); every white space and every character the rules do not allow
  becomes a space (when spaces are allowed) or `-`; a run of spaces and hyphens that contains a hyphen becomes one
  `-`, a run of spaces one space; drop leading characters until a letter or number; cut to `maxLength` code points;
  drop trailing spaces, `-`, `_` and `.`; the result must pass validation, else there is none.
- **Random**: `racer-` followed by 6 characters from `23456789abcdefghjkmnpqrstuvwxyz`, from the system's
  cryptographic random generator. A taken random name is replaced by a new one.
- A name the user typed, and every registered name, is never changed silently; only the user renames it. Only the
  computer-name default follows the service's rules as they become known.

### Dashboard invitation (一鍵開啟)

Reporting stays off by default; instead the dashboard offers it once, so that people who would like it need not
find the setting.

- **When.** At the top of the dashboard, above the hero, while all of these hold: the invitation was never answered;
  reporting is off; the network master switch is on; the selected service is the built-in preset (Codex Radar) and no
  identity is registered for any service. The answer is stored (`report.invite`, Storage below); a fresh install, and
  an update from a build that had no invitation, start unanswered, so every installation sees it once. Unanswered, it
  stays until answered — closing the dashboard does not answer it. While `update.done` shows (UPDATES 4) the
  invitation waits for the next opening of the dashboard: one card at a time. It is answered — and never shown again —
  by `report.invite.accept`, `report.invite.decline`, its close button (the same as decline) or by turning reporting
  on in Settings. Showing it touches nothing: no request, no key, no stored name.
- **Content.** Title `report.invite.title`; `report.invite.body` with the preset's name and domain (`codexradar.com`, as the app knows
  them — nothing is fetched); `report.invite.username` with the username field's name (Default username, below; the
  name in the label color, medium weight, isolated with U+2068 … U+2069); then, on one row, the borderless link
  `report.invite.preview` leading and the buttons `report.invite.decline` and `report.invite.accept` (prominent)
  trailing; a close button (`xmark`) in the top trailing corner.
- **Layout.** The dashboard's width minus its padding (328 pt / DIP). A card with 12 pt continuous corners, the accent
  color at 8 % (light) / 14 % (dark) over the panel and a 0.5 pt hairline of the accent at 20 % (40 % with Increase
  Contrast); 12 pt padding. A header row: a 22 pt rounded-square badge (6 pt corners, accent gradient, the broadcast
  glyph in white, 10.5 pt semibold), 8 pt, the title (13 pt semibold), leaving room for the close button. Below it,
  at the full width of the card: 8 pt; the body (12 pt secondary, the primary color with Increase Contrast); 4 pt;
  the username line (11 pt secondary); 10 pt; the button row (small controls, 8 pt apart; the link with an `eye`
  glyph in the accent color). Every text wraps — at most 4 lines for the body — and nothing is ever truncated or
  shrunk, in any of the four languages; the buttons keep their full labels. The close button is a 9 pt bold `xmark`
  in an 18 pt hit target, secondary color, as on the update banner.
- **Motion.** It enters like the update banner (opacity and a short move from the top with the dashboard's layout
  spring). `report.invite.accept` cross-fades the card's content to one line, `report.invite.done` with an accent
  checkmark, holds it 2.4 s and then collapses the card (height and opacity) while the footer's indicator fades in;
  decline and close collapse it at once. Reduce Motion: nothing slides or springs — the card and its content fade
  with the dashboard's 0.2 s ease-out, as every other dashboard change does; the done line still holds 2.4 s.
- **Accessibility.** One container labelled `report.invite.title`; reading and Tab order: title, body, username,
  `report.invite.preview`, `report.invite.decline`, `report.invite.accept`, close. The close button's label is
  `report.invite.decline`. No button is the default: Return never accepts, so consent cannot come from a stray key.
  `report.invite.done` is announced politely when it appears. Nothing relies on color.
- **Accept** turns reporting on for the preset with the section switches as they are (all on unless the user changed
  them) and the username field's name, without
  opening Settings: the discovery document is fetched (REPORTING 3.4) and confirmed without a sheet when the
  invitation covered it (REPORTING 3.3), the key is created, the device registers (REPORTING 4.1) and reporting
  starts. Progress and problems appear like any other: the footer indicator (amber states quietly, an attention dot
  when the user must act) and the pane's status line, whose actions finish what needs the user
  (`report.service.retry`, `report.service.review`, a new username, an API key). **Decline** does nothing else; no
  request is ever made.
- **Preview** (`report.invite.preview`) opens the Preview window above (document not known yet) and does not answer
  the invitation.
- Strings: `report.invite.title`, `report.invite.body` (service name, host), `report.invite.username` (name),
  `report.invite.preview`, `report.invite.accept`, `report.invite.decline`, `report.invite.done`.

### Menu, dashboard and About

- **Quick menu** (macOS status item menu, Windows tray and widget menu): while reporting is on and this device is
  registered (not while it is still being set up), an item
  `report.menu.on` (service name) with a small status dot opens a submenu: `report.pause.hour`,
  `report.pause.indefinite` (or `report.resume`), `report.service.leaderboard` (when the service has one) and
  `report.menu.settings`. While paused the item reads `report.menu.paused`.
- **Dashboard footer**: a small broadcast glyph (SF Symbol `dot.radiowaves.left.and.right`; on Windows the closest
  Segoe Fluent Icons glyph, chosen when implementing) left of the settings button, secondary color, static; tooltip and accessibility label
  `report.indicator` (username and host); a click opens Settings → API. Paused: the glyph at 40 % with the tooltip
  `report.menu.paused`. When the status needs the user (red in the pane), a 5 pt amber dot sits on the glyph and the
  label becomes `report.indicator.attention`. Shown while reporting is on — also while it is still being set up
  after the invitation (fetching, registering, waiting for the user), so that its problems have a place — and hidden
  while it is off.
- **About**: `privacy.report` follows `privacy.statement` and `privacy.traffic`. While reporting is on,
  `privacy.statement.report` replaces `privacy.statement`, `privacy.traffic.report` replaces `privacy.traffic` and
  `settings.network.hint.report` replaces `settings.network.hint`, so no sentence claims that nothing is sent. The
  Network Traffic pane adds `report.traffic.note` under its switch while a traffic section is being reported.

### Storage

- Settings (plain, in the settings store): `report.enabled`, the selected service URL and preset, the section switches,
  the pause end (or indefinite), the invitation's answer (`report.invite`: unanswered, accepted or declined), the
  username for the next registration when the user typed one (absent: the computer-name default), the origins whose
  transport the user set to HTTP (latest choice last, at most 16; every other service uses WebSocket, item 2b — the
  list of WebSocket origins kept by earlier builds is ignored), and per service
  origin: the confirmed discovery fields (REPORTING 3.3), `deviceId`,
  the registered username, `profileUrl`, the API origin, the key's creation date and the pending-key flag (REPORTING
  4.5). At most 8 service identities; the oldest unused one is forgotten locally first.
- **Private keys and API keys** never go to the settings store, logs or crash reports. **Availability** (normative,
  REPORTING 4.7): every key below is usable once the user has unlocked the computer after it started, without the
  user's presence — no password, Touch ID, Windows Hello or other prompt for each use — so reports continue while the
  screen is locked. Accessibility "when unlocked" and any user-presence or biometry flag are not used.
  - **macOS, with a Secure Enclave** (`SecureEnclave.isAvailable`: Apple silicon and Intel Macs with a T2 chip) — the
    normative store. The device key is generated **inside the Secure Enclave** with CryptoKit
    (`SecureEnclave.P256.Signing.PrivateKey(accessControl:)` with
    `SecAccessControlCreateWithFlags(nil, kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly, .privateKeyUsage, nil)`:
    usable after the first unlock since boot, never asking the user; CryptoKit's default access control,
    `kSecAttrAccessibleWhenUnlockedThisDeviceOnly`, would stop signing while the screen is locked); it cannot be
    exported, and its `dataRepresentation` is an encrypted handle that only this Mac's Secure Enclave can use. The
    handle is stored in `<app data>/report/<deviceId>.sekey` (mode 0600, written atomically). The API key is sealed
    with AES-GCM under a key derived (`hkdfDerivedSymmetricKey`, SHA-256, salt `AgentSpeed report`, info `api-key`,
    32 bytes) from an ECDH agreement between a Secure Enclave key-agreement key
    (`SecureEnclave.P256.KeyAgreement.PrivateKey`, the same access control, its handle in `<deviceId>.seagree`) and an
    ephemeral P-256 public key
    stored with the sealed box in `<deviceId>.apikey`. No Keychain item is used, so updates of the ad-hoc signed app
    never trigger a Keychain password prompt. Verified on 2026-10-22 with ad-hoc signed builds on macOS 27: Secure
    Enclave signing (signatures verified by the reference) and the API-key sealing both work without entitlements.
  - **macOS without a Secure Enclave** (older Intel Macs) — fallback: a software key (`P256.Signing.PrivateKey`, its
    32-byte `rawRepresentation`) and the API key in Keychain generic-password items, service
    `io.github.zoefix.agentspeed.report`, accounts `<deviceId>.key` and `<deviceId>.apikey`,
    `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`, no `kSecAttrAccessControl`, not synchronizable. Because
    release builds are ad-hoc signed,
    the Keychain may ask for the login password the first time an updated build reads these items (not verified).
  - Windows: `%LOCALAPPDATA%\AgentSpeed\report\<deviceId>.key` (the PKCS#8 private key from
    `ECDsa.ExportPkcs8PrivateKey`) and `<deviceId>.apikey` (UTF-8), each protected with DPAPI (`CryptProtectData`,
    current user, the device id's 16 bytes as additional entropy, flags `CRYPTPROTECT_UI_FORBIDDEN` only and no
    prompt structure: readable whenever the user is signed in, the locked screen included, never prompting), written
    to a temporary file and renamed.
  - Both are deleted by *Forget*, *Reset* and when a service identity is forgotten locally.

### README and About wording (prepared; applied when the feature ships)

The About texts are the strings above. The README privacy sections add this paragraph after the traffic paragraph
when the feature ships (until then the README describes the released app). The clause on each model's performance
score, performance statistics and thinking speed comes with the `perfScore` extension (METRICS 11.10); the
*Performance score* paragraph says exactly what they contain (section 12, *Privacy and wording*):

- English: "Live reporting (optional, off by default; the dashboard offers it once): if you turn it on for an API —
  for example a public leaderboard service — Agent Speed sends that service your live speed, the tokens used in the
  last few seconds and, if you leave them on, the API-equivalent cost in US dollars, your agents' network traffic,
  models in use (with, where the service supports it, each model's performance score, performance statistics and
  thinking speed) and a per-tool breakdown, together with the username you choose (it starts as your computer's name;
  one click picks a random one). It reports
  every few seconds while your agents work and stops when they rest. It never sends conversation content, prompts,
  commands or their output, file, folder or project names, or your usage history, and failed reports are never
  resent. Each report is signed
  with a key that stays on your computer; the service can tell that reports come from the same device, not whether
  the numbers are true. You can pause, turn it off, or ask the service to forget your device in Settings."
- 繁體中文：「即時上報（選用，預設關閉；儀表板會詢問一次）：為某個 API（例如公開排行榜服務）開啟後，Agent Speed 會把你的即
  時速度、最近幾秒用掉的 token 數，以及你保留的項目——以美元計的 API 等值費用、agent 的網路流量、使用中的模型（服務支援
  時，含各模型的性能分數、性能統計和思考速度）、各工具的明細——連同你選擇的使用者名稱
  （預設是電腦名稱，一鍵就能換成隨機名稱）傳送給該服務。agent 工作時每隔幾秒上報一次，閒下來就停止。它從不傳送對話內容、提示
  詞、指令或指令輸出、檔案／資料夾／專案名稱或用量歷史，上報失敗的資料也不會補傳。每筆上報都以只存在你電腦上的金鑰簽章；服務能確認資料來自同
  一台裝置，但無法確認數字是否正確。可以在設定中暫停、關閉，或要求服務忘記你的裝置。」
- 简体中文：「实时上报（可选，默认关闭；仪表板会询问一次）：为某个 API（例如公开排行榜服务）开启后，Agent Speed 会把你的实
  时速度、最近几秒用掉的 token 数，以及你保留的项目——以美元计的 API 等值费用、agent 的网络流量、使用中的模型（服务支持
  时，含各模型的性能分数、性能统计和思考速度）、各工具的明细——连同你选择的用户名
  （默认是电脑名称，一键即可换成随机名称）发送给该服务。agent 工作时每隔几秒上报一次，空闲时停止。它从不发送对话内容、提示
  词、命令或命令输出、文件／文件夹／项目名称或用量历史，上报失败的数据也不会补发。每条上报都用只保存在你电脑上的密钥签名；服务能确认数据来自同
  一台设备，但无法确认数字是否准确。可以在设置中暂停、关闭，或要求服务忘记你的设备。」
- 日本語：「ライブ送信（任意・初期設定はオフ。ダッシュボードで一度だけ案内します）：API（公開ランキングサービスなど）に対して
  オンにすると、Agent Speed は現在の速度、直近数秒に使った token 数、そしてオンのままにした項目——米ドル建ての API 換算費
  用、agent の通信量、使用中のモデル（サービスが対応していれば、モデルごとの性能スコア、性能統計、思考の速度を含む）、
  ツールごとの内訳——を、選んだユーザー名（初期値はコンピューター名。ワンクリックでランダムな名前にできます）と一緒にそのサービスへ送信
  します。agent が作業している間は数秒ごとに送信し、休んでいる間は止まります。会話の内容、プロンプト、コマンドやその出力、
  ファイル・フォルダー・プロジェクトの名前、使用履歴は一切送信せず、送信に失敗したデータを後から送り直すこともありません。各送信はこのコンピューター
  から出ない鍵で署名されます。サービスは同じデバイスからの送信であることは確認できますが、数値が正しいかどうかは確認できません。
  設定から一時停止・オフにしたり、サービスにデバイスの削除を依頼したりできます。」

### Accessibility

- Every control has its visible label; status dots always have text; nothing relies on color.
- The status line is a live region (polite): changes are announced at most once per minute, except changes to a red
  state, which are announced at once.
- The preview is selectable text; `report.preview.copy` copies it.
- Reduce Motion: the service card and status changes appear without fades; the dashboard invitation fades without
  sliding (Dashboard invitation).

## 12. Performance score (性能)

性能 is the **performance score** of METRICS 6.9: a benchmark-style number ("跑分") per agent that says how fast its
setup — agent, model, computer and network together — has been working over its latest timed requests: how fast it
writes, how soon content starts, how fast it thinks, how briskly it acts and how fast its tools answer. **1000 is the
fixed reference level and there is no upper limit**: twice as fast in every respect scores twice as high, and
different agents and models are directly comparable, so boards rank them meaningfully (user's decision,
2026-10-29). It replaces the task score (智力) of earlier versions on every surface that showed it — the orb readout
and the menu bar / taskbar block (section 10), the board hero and rows (2c), the dashboard's agent rows (4) — and the
Usage History window shows it per source and model over time (4b, 3c). The `思考` line beside it is unchanged
(METRICS 6.9, *Live thinking*).

Score, components, forms, the provisional rule and the highest-score rule for sets: METRICS 6.9 "Performance score";
the daily records, pooled scores and the baseline behind 比平常: METRICS 6.11 "Performance history"; what live
reporting sends: METRICS 11.10. The visual reference is `spec/prototypes/perf-score/index.html` (both appearances,
every surface and state, the wide and narrow History layouts; `?static=1` for still frames): its colors, radii, sizes
and spacing are normative wherever this section gives no value. Strings: `perf.*`, `a11y.perf.*`, `history.perf.*`,
`a11y.history.perf.*`, `privacy.perfScore`, `report.section.models.perfScore`, `report.section.models.thinking`, and
the readout keys kept from 智力 (`intel.glyph`, `intel.none`, `a11y.orb.intel`, `a11y.orb.intel.only`,
`a11y.agent.intel`, `orbs.traffic.intelligence`, `settings.bar.intelligence`, `boards.readouts.intel` and their
hints), whose texts now say 性能.

### Principles

- **A benchmark, honestly labelled.** Higher is faster; 1000 is the reference level, not a maximum, so the number
  never "maxes out". The reference values and weights are a calibration on the developer's own logs, not an industry
  standard, and every place that explains the score says so in one short line (`perf.card.footnote`,
  `history.perf.info`).
- **Alive, never alarming.** The score moves with every response and tool result. Shown values count smoothly to each
  new value in both directions; a drop is never red, never shaken and never flashed (*Motion*). The speed redline
  keeps heat, tiers, titles, peaks and celebrations: the score drives none of them, and neither do its tier words.
- **Few samples look provisional.** A score from fewer than 10 timed requests (METRICS 6.9) carries the dotted
  provisional mark on every surface, the app's one visual language for "not settled yet" (*Provisional mark*).
- **Absent is not zero.** A component the source does not record, or has too few values for, is left out of the score
  instead of counted against it; the card says which and why. No score is `–`, never `0`.
- **Compared with itself too.** Next to the absolute number, 比平常 compares the score with the same source and model
  on this computer over the 14 days before today (METRICS 6.11), so a slow day stands out whatever the model.
- **The highest agent, not an average.** A set — an orb, the menu bar, a board's hero, a board group — shows the
  highest score among its agents, live before resting. The value always belongs to one agent, which the tooltip or
  the card names.
- **One number per agent.** An agent's score is the same on the dashboard row, its board sub row and in its card.
- **Local unless reported by choice.** Live reporting sends the score and per-model performance statistics, for
  ranking and statistical analysis, only with the models switch on and to a service that advertises the `perfScore`
  extension — never anything about a single request or tool call (section 11, METRICS 11.10).

### Values and forms

| Surface | Value (METRICS 6.9) | Forms |
| --- | --- | --- |
| dashboard agent row (4), board sub row (2c) | the agent's | live · resting · none |
| board group row (2c) | the group's: its highest member score | live · resting · none |
| orb readout (10) | the orb's agents `A` (the highest score) | live · resting · none |
| menu bar / taskbar block (10) | the agents of all enabled sources (the highest score) | live · resting · none |
| board hero (2c) | the board's agents `A` (the highest score) | live · resting · none |

- **Number**: an integer from 0 up, rounded half away from zero (METRICS 6.9), with no upper bound; formatted as
  *Number formats* says.
- **live** (the agent is not idle and has a score): the surface's number color. **resting** (the agent is idle and
  still has a score: its timed requests are within the last 30 minutes): the surface's pale color. **none** (fewer than
  3 timed requests in the last 30 minutes): `–` (U+2013, the app's absent mark everywhere) in the pale color, never
  `0`.
- **Provisional** (3 to 9 timed requests): either form, with the provisional mark.
- A set's value names its **supplying agent** (METRICS 6.9: among the agents with that exact score in that form, the
  latest `lastLineMs`, then the smallest id) and **N**, the number of the set's agents with a score in that form; N
  appears only in accessibility and the card's eyebrow. A set's provisional mark follows its supplying agent.

| Surface | live | resting and `–` |
| --- | --- | --- |
| dashboard row | the live value's number color | tertiary (Increase Contrast: secondary) |
| board rows | the style's live row color (2c) | the style's resting color |
| board hero | `ink` (neon: `led`) | `ink-3`; the arc and the meter at 35 %, no glow |
| orb readout | the number token at 80 % | the unit token |
| menu bar / taskbar | the label color at 85 % | secondary |

### Number formats

Scores have no upper bound, so every cell that was measured with `888` is measured again. Two forms, both from the
rounded integer `v`:

| Form | Rule | Examples | Used by |
| --- | --- | --- | --- |
| **full** | `v` with the locale's digit grouping (a comma every three digits in all four languages) up to 99,999; from 100,000 the compact form | `0` · `986` · `1,286` · `12,840` · `128k` | score card, board hero (neon, timing), Usage History, tooltips, accessibility |
| **compact** | below 10,000 the full form; from 10,000 three significant digits with `k` (thousands) or `M` (millions), rounded half away from zero, the suffix following the rounded value (`99,960` → `100k`, `999,500` → `1.00M`) | `1,286` · `9,999` · `12.8k` · `128k` · `1.28M` | dashboard rows, board rows, glass's dial, orb readout, menu bar / taskbar block |

- Below 1,000,000 the compact form is never wider than `8,888`, so a compact cell is measured with **`8,888`** in its
  font (wider than `88.8k` in every font the apps use). Cells are measured when the language, font, text size or
  switches change, never per value; the number is right-aligned and tabular in its cell. A wider text (`1.28M`)
  widens its cell at once and narrows it 6 s after the last value that needed it (section 3's rule for the value).
- Measured cells (macOS fonts, rounded up to a whole point; Windows applies the same rule in Segoe UI / Cascadia
  Mono): dashboard column 35 (was 24; `8,888` in SF Pro 12 pt medium is 34.5); board rows 33 (glass, SF Pro 11 pt
  semibold), 34 (timing and neon, SF Mono 11 pt), and on the two-line group rows of M and L 39 (glass, 13 pt) and 40
  (timing and neon, 13 pt); menu bar number cell 27 (`8,888` in SF Pro 9 pt medium is 26.7, `8.88k` 25.8). Section 2c,
  4 and 10 give the layouts these widths change.
- Accessibility always speaks the full integer (`12.8k` is read as 12,840), and tooltips show the full form.

### Log scale

Every meter of the score — the board hero's arc and LED meter, the card's scale, the History meters and chart — maps a
score `v` to `f = clamp(log2(v / 250) / 4, 0, 1)`: 250 is empty, 500 a quarter, **1000 half**, 2000 three quarters,
4000 full; above 4000 the meter stays full while the number keeps rising. Equal steps of the meter are equal ratios
of speed, so twice as fast always moves it by the same amount, and the reference level sits at the middle, where the
eye expects "normal". Sub-scores (*Score card*) use the same scale.

### Provisional mark

A provisional score (3 ≤ n < 10, METRICS 6.9) is drawn **dotted** — the language the app has used for "not yet
settled" since the task score, now with this one meaning:

- **Text**: a dotted underline under the digits. Dot diameter `d = max(1 device px, 0.1 × the text size)`, rounded to
  the device pixel; `n = max(2, ⌊(w − d) / (2.4 d)⌋ + 1)` dots spread evenly from the left to the right edge of the
  number's advance width `w` (the grouping comma included); centred `0.22 ×` the text size below the baseline (the
  menu bar and taskbar block: `0.17 ×`, the room left under line 1); the number's color at 70 %, never a glow. It
  takes no width and no height in layout and is drawn in the content layer (boards: inside the cached row).
- **Shapes**: glass's value arc as round dots (dash 0, gap 2 × the stroke) with a hollow tip ring; neon's lit
  segments hollow (1 pt inner stroke); the card's scale fill as round dots of its height on a 2.2 × pitch.
- It fades in and out with the state (0.25 s; Reduce Motion: at once). Increase Contrast: the dots at 100 % and
  `d + 0.5 pt`. Windows high contrast: the dots in the text color.
- Accessibility never depends on it: the score words carry `a11y.perf.provisional`, and the card says how many
  timed requests there are (`perf.provisional.hint`).

Why dotted: a dotted line reads as provisional — a pencilled value, a draft line — in every culture the app speaks
to; it needs no width in cells that have none to spare, and it works in monochrome (the menu bar's template text), at
the orb's 7 pt floor and in high contrast. Rejected: a trailing mark (no width in the fixed cells), a lighter weight or
color (reads as resting), `~` or `?` (an approximation or an error, which a young score is not).

### Motion

The score changes when a response completes, a tool result is read or an old response leaves the 30-minute window
(METRICS 6.9) — often, while agents work. Every change is calm:

- **Count** — a change of the shown integer by at least 1 % and at least 3, up or down: on the orb readout, the board
  hero (the arc's end and the meter follow the shown value), the dashboard row and the open card's number (its scale
  follows), the integer counts through the intermediate values over `clamp(0.3 + 0.35 × |log2(new / old)|, 0.3, 0.7)`
  s with easeOutCubic timing (+10 % lands in 0.35 s, a doubling in 0.65 s: quick at first, settling into the final
  number). Each intermediate value is formatted in the surface's form, so a count from 986 to 1,240 gains its comma
  as it passes 1,000. A new value during a count continues from the value on screen with a fresh duration (never a
  jump back). A drop counts down the same way: no red, no color change, no shake, no flash.
- Smaller changes, every change on board rows (cached row content, 2c *Frame policy*) and on the menu bar / taskbar
  block (a status item) replace the characters in place.
- none → a value and a value → none crossfade in 0.15 s (no count from or to `–`).
- A change between live and resting crossfades the color in 0.2 s (an opacity change, kept with Reduce Motion).
- **Reduce Motion**: no count; every change is a 0.15 s crossfade.
- **Frames**: a count renders at the surface's animation rate for its duration (orbs 30 fps, boards 60 fps in the
  roll class, the dashboard and the card at the display rate); everything else renders one frame per change. Nothing
  about 性能 animates continuously, and an idle orb, board or menu bar renders nothing.

### Score card (性能分析卡)

The breakdown behind the number: what the score is, whether it is usual, what makes it high or low and how sure it is.
It replaces the task score card wherever that opened.

**Where.** It is the tooltip of a dashboard agent row (4) and of a board group or sub row while the board's
`intelligence` readout is on (2c), and it opens on hover over a board hero's 性能 (2c) for the supplying agent. A group
row's card is the supplying member's (none: the root's). The orb's Windows tooltip and the taskbar tooltip stay text
(`perf.tooltip.top`, section 10).

**Window.** 320 pt / DIP wide (300 before: the component rows now carry four columns), as tall as its content. macOS: a
non-activating borderless panel with the system popover material (`NSVisualEffectView` `.popover`; Liquid Glass on
macOS 26), radius 12, a 0.5 pt separator-color stroke, the system shadow. Windows: the flyout style of the
unknown-prices popover (9) — acrylic, radius 8, a 1 px stroke. Reduce Transparency: a solid fill (dark `#26272B`, light
`#FFFFFF`). It never takes focus and ignores the mouse: it is a tooltip, so the pointer passing over it hides it.

**Placement and timing.** Dashboard: beside the panel, on the side toward the screen's centre (left of the macOS
popover and of the Windows flyout), 8 pt from it, its top level with the row's top, clamped 8 pt inside the visible
frame. Boards: beside the board on the side with more room, level with the row (the hero: with the dial). It shows
after 0.45 s of hover on a dashboard row (0.35 s on boards, their tooltip delay); while a card is shown, moving to
another row switches at once (0.12 s crossfade); it hides 0.15 s after the pointer leaves the row. It appears with a
0.15 s fade and a 2 pt slide away from the row (Reduce Motion: the fade only). Keyboard: Space on a focused row, or the
row's custom action `a11y.perf.card`, shows it; it follows the focus while shown; Esc hides it. Its content updates on
the engine tick with the row; its number and scale follow *Motion*; component values change in place.

**Layout** (padding 14; blocks 12 apart; hairline separators under the header and above the facts):

1. **Header**: a 7 pt source dot, the project (13 pt semibold, tail truncation) and, trailing, the provisional chip
   while the score is provisional. Line 2 (11 pt, secondary): model · source — the model of the newest timed request
   (METRICS 6.9), else the agent's current model; a group row adds `boards.subCount`.
2. **Score**:
   - An eyebrow line (11 pt, secondary) only when it applies: `phase.idle` while resting, `perf.card.top` on the
     hero's card when N ≥ 2, both joined with ` · ` when both apply (`閒置 · 2 個 agent 中最高`).
   - The number: 34 pt SF Pro Display / Segoe UI Variable Display semibold, tracking −0.02 em, tabular, full form,
     the label color (resting: secondary; none: `–` in tertiary, weight 500), the provisional mark under a
     provisional number. Trailing on the same row, its centre level with the digits' cap-height centre: the **usual
     chip** (*Chips*).
   - The **scale**, 10 under the number's baseline: the content width (292) is the log scale. Track 6 tall, radius 3.
     Fill from the left to `f` of the shown value, round end, painted with the `pearl` gradient laid across the whole
     track (`pearl-c` → `pearl-a` → `pearl-b`, the board glass arc's colors: dark `#A5F3FC` → `#C7D2FE` → `#F5D0FE`,
     light `#1AA3C4` → `#6D7CF0` → `#C05BD9`), so the color at the fill's end tells the level too. Ticks across the
     track at 500, 1000 and 2000: 1 pt wide, the track's height + 4, centred on it; 500 and 2000 in the label color at
     18 %, 1000 (the reference) at 45 % and 4 pt taller. Labels 4 under the track, 9.5 pt tabular, tertiary,
     centred on their ticks: `500`, `1000` (secondary, semibold), `2000`, and `4000` right-aligned to the end. A
     provisional fill is dotted (*Provisional mark*); a resting fill is at 45 %; none draws the track alone.
   - The **summary**, 8 under the labels (12 pt, secondary, up to two lines): `perf.summary.slowest` with the tier
     word, the ratio and the label of the present component with the lowest sub-score (ties in the order of the rows
     below) when at least two components are present and that sub-score is below 1000; `perf.summary.allAbove` when
     at least two are present and none is below 1000; `perf.summary` otherwise. Tier words, from the shown integer:
     `perf.tier.slow` below 700, `perf.tier.standard` from 700, `perf.tier.fast` from 1300, `perf.tier.veryFast`
     from 2000. The ratio is `v / 1000` with two decimals (`1.29`, `0.86`, `12.84`). The tier words are words for
     the card only: they never color anything and never drive heat, tiers, titles, peaks or celebrations.
   - Provisional: `perf.provisional.hint` with n and 10 under the summary (11 pt, tertiary).
   - None: the summary is replaced by `perf.status.none` with n and 3, and the component groups are left out.
3. **Components**, two groups 14 apart, each under its title (11 pt semibold, tertiary, tracking 0.06 em; Latin
   upper-case at 10.5 pt): `perf.group.speed` — throughput, latency, thinking; `perf.group.efficiency` — tool pace, tool time,
   tool success, cache hits (METRICS 6.9 components T, L, R and N, U, S, H). Rows 9 apart, each a four-column line and
   a meaning:
   - Line 1: the label (`perf.comp.throughput`, `.latency`, `.reasoning`, `.pace`, `.toolTime`, `.toolSuccess`,
     `.cache`; 12.5 pt semibold, the rest of the width, tail truncation), the measured value (12 pt medium, tabular,
     right-aligned, in a column as wide as the widest value shown, at least 64), the **mini meter** (44 × 4, radius
     2, 10 after the value: the sub-score on the log scale in the score ink, a 1 × 8 tick at the middle — 1000 — in
     the label color at 35 %) and the sub-score (`1000 × r`, METRICS 6.9; full form, 12 pt semibold, tabular,
     right-aligned, 8 after the meter, in a column as wide as the widest shown, at least 34).
   - Values: throughput and thinking speed per METRICS 7 with `unit.tokS`; latency and tool time as `perf.unit.ms`
     below 1 s (integer), `perf.unit.s` with one decimal below 10 s and as an integer up to 60 s, `perf.unit.min`
     from 60 s (one decimal below 10 min, integer from there), each rounding taken once from milliseconds, half away from
     zero, the unit following the rounded value; tool pace as `perf.unit.perMin` with one decimal below 10, integer
     from there; tool success and cache hits as whole percents, half away from zero, except that a value below 100 %
     never shows `100%` (it shows `99%`).
   - The meaning, 3 under line 1 (10.5 pt, caption color, up to two lines, never truncated): the component's
     `.desc`; tool pace appends ` · ` and `perf.comp.pace.count` with `|C|`.
   - **Absent** components (METRICS 6.9 presence): the value column shows `perf.reason.notRecorded` (the source
     never records it) or `perf.reason.fewSamples` (too few values in the window) in 11.5 pt tertiary, the meter is
     a dotted track without fill (round dots of its height on a 2.2 × pitch, the label color at 18 %), the
     sub-score is a tertiary `–`, and the meaning line says why instead: `perf.reason.notRecorded.hint` with the
     source's name or `perf.reason.fewSamples.hint` with 3. Never a fake `0`.
4. **行為** (`perf.signals.title`), when it has a line — METRICS 6.10 work signals, unchanged: `work.looping` with
   the amber loop glyph of section 4, `work.repeating` (secondary glyph), `work.summary` (at least 8 recent actions).
   12 pt text, 13 pt glyphs, 6 pt apart. They describe behaviour and never change the score.
5. **Facts** (11 pt, secondary): `perf.card.sample` with n first, then the row's former tooltip lines in their order —
   dashboard: `effort.tooltip`, `speed.tooltip`, a top rung's detail, `traffic.agent.tooltip`; boards: group speed and
   its sub-agents' share, today's output (2c).
6. **Footnote**: `perf.card.footnote` (10.5 pt, caption color): 1000 is the reference with no upper limit, the
   calibration note, and that the score and per-model performance statistics are reported, for ranking, only with
   the models switch to a service that supports them.

A board with `intelligence` off keeps its text tooltip.

**Chips** — 18 tall, fully rounded, horizontal padding 7, 11 pt semibold text, tabular:

| Chip | Text | Look |
| --- | --- | --- |
| usual, `vsUsual` rounds above 0 | `perf.usual` with `+8%` | mint: dark text `#6EE7B7` on `rgba(52,211,153,.14)`, light `#047857` on `rgba(16,185,129,.12)` |
| usual, rounds to 0 or below | `perf.usual` with `±0%` or `−12%` (U+2212) | neutral: secondary text on the label color at 6 % |
| usual, no baseline | `perf.usual.none` | neutral text, no fill, a 1 pt dotted outline of the label color at 35 % |
| provisional | a ring of 8 dots (10 pt glyph, 4 before the text) and `perf.provisional` | as *no baseline* |

`vsUsual` (METRICS 6.11) is shown as a signed whole percent rounded half away from zero. A slower day is neutral, never
amber or red: a busy network or a long-thinking prompt is a normal reason, and the score is not a warning. The
usual chip has no tooltip of its own (the card is one); its words are in the accessibility content.

**Colors.** Score ink — mini meters and History meters: dark `#93A4D6`, light `#4B5FA6` (a quiet slate blue from the
heat palette's indigo, distinct from every source color). Track: dark white 9 %, light black 7 %. Text uses the system
label colors. Caption color (meanings, footnote, hints): dark `rgba(235,235,245,0.56)`, light `rgba(60,60,67,0.72)`,
which keeps 4.5:1 at 10.5 pt. Increase Contrast: the scale fill, meters and ticks in the label color without the
gradient, tracks the label color at 25 %, dots at 100 %, captions in the system secondary color, chips with a 1 pt
solid stroke in their text color. Windows high contrast: fills in the highlight color, tracks, ticks and dots in the
gray-text color, chips in the text color with a 1 px stroke and no fill.

**Accessibility.** The card itself is not focusable. Its content is the row's extra content — macOS `AXCustomContent`
(VoiceOver's "more content"), Windows the row's `AutomationProperties.HelpText` (WPF has no `FullDescription`) — in
this order: `a11y.perf.score` (the score words and the tier word), the usual words (`a11y.perf.usual.up` /
`a11y.perf.usual.down` with the percentage, `a11y.perf.usual.same`, or `perf.usual.none`), the summary, the
provisional hint, each component as `a11y.perf.comp` (label, value as shown, sub-score in full form) or
`a11y.perf.comp.absent` (label, reason) followed by its meaning, the 行為 lines, the facts and the footnote. The row's
custom action `a11y.perf.card` shows the card for sighted keyboard users; since UI Automation has no custom actions,
Space on the focused row opens it on Windows. Nothing relies on color, the gradient or the dotted shape: every number,
meter and chip has a text equivalent.

**Keyboard.** Dashboard and board rows that show a score can take keyboard focus: macOS when keyboard navigation is
on (Full Keyboard Access or "Keyboard navigation"), with the system focus ring; Windows always, with the standard
focus visual. Space shows the card of the focused row, focus moves keep it on the focused row, and Esc closes it.

**Fit.** A card taller than the visible screen area minus 16 pt (or 16 DIP) drops its components' meanings first,
then the footnote. The accessibility content stays complete. The card is a non-activating, click-through panel outside
the window (macOS `NSPanel`; Windows a tool window like the taskbar tooltip, not a `Popup`).

**Platform details**, for parity:
- **Menu bar / taskbar.** A change replaces the number at once, since the status item is one image. With Increase
  Contrast (Windows high contrast) a live score takes the full label color, so that live and resting differ by more
  than the pale level.
- **Orb.** During the 10 s fade after its agents leave the agents list, the orb keeps its last score instead of
  switching to `–`.
- **Board under Increase Contrast.** The track and resting colors are adjusted so that a resting arc stays distinct
  from the track.

### Usage History: performance (性能表現)

Section 3c of the Usage History window (4b), between *Effort and speed* and the daily table, in the chart's card style
(radius 14, the label color at 4.5 %, padding 16 × 18). Data: METRICS 6.11 — the daily performance records of each
`(source, model)` whose day lies in the window's range, pooled over the range; the window's source picker narrows it to
one source's models. Every `(source, model)` with at least one timed request in the range has a row.

**Header.** `history.perf.title` (15 pt semibold) and an ⓘ button (accessibility label and tooltip
`history.perf.info.a11y`) whose popover (340 pt) holds `history.perf.info` and, as a definition list, every
component's name (`perf.comp.*`) with its `.desc`.

**Trend.** Under the header (12): the title line `history.perf.trend` with the selected row's model · source (12 pt,
secondary; the model in the label color, medium) and, trailing, the legend: a 12 pt line sample with `1000` and
`perf.reference`, and a dashed sample with `history.perf.usual.line` while the usual line shows (11 pt, tertiary).
Then the chart, as wide as the section and 150 tall (medium 132, narrow 120):

- x: the range's days, one slot each, the same day alignment, month rules and labels as the daily chart (4b 3); a
  single-day range shows the 14 days ending on that day instead (the title line adds ` · ` and
  `history.perf.trend.days` with the date), with that day's slot tinted (the label color at 5 %) and its dot larger.
- y: the log scale, from 250 at the bottom to `max(4000, 250 · 2^⌈log2(max / 250)⌉)` at the top (8000, 16000, … when
  a day exceeds 4000); gridlines at every power-of-two step from 500 in the hairline color with their values at the
  left (9.5 pt, tertiary, tabular, full form); the 1000 line in the label color at 35 %, 1 pt.
- The series: the selected row's pooled score of each day with at least 20 timed requests (METRICS 6.11; fewer → a
  gap that breaks the line), a 2 pt line in the row's source color with round joins, over an area from the line down
  to the bottom in the source color from 12 % to 0; a 4.5 pt dot on each day (a 1.5 pt ring in the chart's
  background), the latest day 6 pt. A day alone between gaps is its dot only.
- The usual line: the row's baseline (METRICS 6.11, the 14 days before today) as a 1 pt dashed line (4 · 3) in the
  source color at 60 %, named by the legend only (a label on the line would sit on the latest days' dots); none
  without a baseline.
- Hover (or the keyboard arrows while the chart has focus) shows a crosshair and a tooltip card: the date with
  weekday, the score (full form), `history.perf.requests` and the day's tier word; a gap day shows
  `history.perf.sample.hint` with its count and 20.
- No day with enough data: the chart's area holds `a11y.history.perf.chart.empty` (12 pt, tertiary, centred) over
  the gridlines.

**Table** (12 under the chart; the daily table's style: rows 30, alternating tint, hover highlight, sortable headers,
copy row as text). Columns, wide layout:

| Column | Header (tooltip and accessibility name) | Cell |
| --- | --- | --- |
| model | `history.perf.column.model` | the source dot (7 pt), the model id (12.5 pt medium, middle truncation; an unknown model `cost.noModelName`), then the source name (11 pt, secondary); `history.perf.sample.low` after the id when the row has no score |
| score | `history.perf.column.score` | the pooled score (13 pt semibold, full form) and a 36 × 4 meter (log scale, score ink, the 1000 tick); `–` below 20 timed requests, with `history.perf.sample.hint` as its tooltip |
| usual | `history.perf.column.usual` (`history.perf.usual.hint`) | the baseline (full form, secondary) or `–` with `perf.usual.none.hint` |
| timed | `history.perf.column.timed` (`.timed.full`) | Σ n |
| throughput · latency · thinking · pace · tool time · tool success · cache | `history.perf.column.throughput` … `.cache` (the full `perf.comp.*` name; hovering a header also shows its `.desc`) | the pooled value in the card's formats; an absent component `–` (tertiary) with its reason hint as the tooltip |

- Default sort: score descending (rows without a score after, by timed requests descending), then the model id; the
  window remembers the sort. A row without a score shows its values in secondary.
- **Selection** picks the chart's series: click a row (or move with the arrow keys); the selected row has the system's
  unemphasized selection fill. Default: the row with the most timed requests (the fullest trend; ties in the table's
  order); the choice is kept per `(source, model)` while the window is open and falls back to the default when it
  leaves the range.

**Widths** (the section's content width): **wide** from 860 — as above. **Medium** 600–860 (the window's minimum 720
lands here): the seven component columns become one `history.perf.column.parts` column (64): seven bars, one per
component in the card's order, 5 wide, 2 apart, 20 tall, each filled from the bottom to `f` of its sub-score in the
score ink over a track, a dashed 1000 line across them at mid-height; an absent component a 1 pt dotted stub. Hovering
a bar shows the component's name, value and sub-score. **Narrow** below 600 (large text sizes and any future smaller
minimum): the table becomes a list of rows, each selectable: line 1 the dot, the model id (middle truncation), and
trailing the score with its meter; line 2 (11 pt, secondary) the source · `history.perf.requests` · the usual value
with `history.perf.column.usual`; line 3 (11 pt, secondary, wrapping) the present components as `label value` pairs
joined with ` · ` (the History column names). Nothing scrolls sideways at any width.

**Footnote**: `history.perf.footnote` (11 pt, tertiary).

**States.**

- **Re-analysing**: the history store upgrade re-analyses every log that still exists (METRICS 6.11). While
  `history.analyzing` runs and the range has no records yet, the section shows static placeholders (the chart's
  gridlines and four table rows in the track color) with `history.perf.analyzing`; records fill in with a 0.2 s
  crossfade.
- **Empty**: no timed request in the range for any source — the muted gauge glyph, `history.perf.empty` and
  `history.perf.empty.hint` (the section stays, so the feature is discoverable). Days whose logs were gone before the
  upgrade keep their totals elsewhere in the window and have no performance data.

**Motion.** A new range, source or selection crossfades the values and the series (0.2 s) and springs the meters to
their new lengths (≤ 0.35 s, section 6); Reduce Motion: crossfades only.

**Accessibility.** The section is a group with the heading `history.perf.title`. The chart is an image labelled
`a11y.history.perf.chart` (model · source, the lowest, highest and latest daily scores, full form) or
`a11y.history.perf.chart.empty`, updated when the selection changes. The table is a native table whose headers speak
the full names; meters are hidden; `–` speaks its tooltip; the medium layout's bars are one element per row,
`a11y.history.perf.parts` with each component as `label sub-score` joined by `list.separator`. Chips are text.

**CSV**: unchanged in this version (the daily CSV has no performance columns).

### Accessibility (every surface)

The **score words** are built in this order and then placed into the surface's label (`a11y.orb.intel`,
`a11y.orb.intel.only`, `a11y.agent.intel`):

1. the number in full form (`1,286`; a compact `12.8k` is spoken as 12,840);
2. provisional: wrapped in `a11y.perf.provisional`;
3. resting: wrapped in `a11y.perf.idle`;
4. a set or group with N ≥ 2: wrapped in `a11y.perf.top` with N.

`性能 1,286，4 個 agent 中最高，思考每秒 128 個 token` · `性能 912，初步（樣本少）` · `性能 1,540，閒置中，2 個 agent 中最高`.
While the value is none, `intel.none` replaces the 性能 words. Updates keep each label's 1-second limit; a count is
never announced step by step (the label carries the final value). Nothing relies on color, the gradient or the dotted
shape.

### Privacy and wording

The score is computed from what the engine already reads — response times, token counts, when tool calls start and
finish and whether they failed (METRICS 6.9, 6.10) — never from what prompts, commands or files say. The history keeps
only per-day, per-`(source, model)` sums (METRICS 6.11). Live reporting sends the score and per-model performance
statistics with the models switch, to a service that advertises `perfScore`, for ranking and statistical analysis,
never anything per request or per tool call (section 11, METRICS 11.10). So:

- **About**: `privacy.perfScore` follows `privacy.traffic`, unchanged while live reporting is on (section 5).
- **Usage History footer**: `history.privacy` and `history.privacy.short` name the per-model daily performance
  figures.
- **Hints**: `agents.intel.hint`, `orbs.traffic.hint.intel`, `settings.bar.intelligence.hint`,
  `boards.readouts.hint` and `perf.tooltip.note` say what the score is, that 1000 is the reference and that there is
  no upper limit.
- **README** privacy sections (all four languages) carry a *Performance score* paragraph after the network traffic
  paragraph, and their storage sentence names the per-model daily performance figures. The paragraph says what is
  read, what is kept and exactly what live reporting sends; the live reporting paragraph lists the score, the
  performance statistics and the thinking speed among the model numbers (section 11, *README and About wording*).

Retired with the task score: every `task.*`, `a11y.task.*`, `history.tasks.*` and `a11y.history.tasks.*` string,
`privacy.taskScore`, `report.section.models.taskScore` and `intel.disclaimer`; the proof states, state chips,
predictions, the composition bar and the task comparison; `spec/prototypes/task-score/`.
