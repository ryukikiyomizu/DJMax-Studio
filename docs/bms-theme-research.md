# BMS theme research: an IIDX theme, and themes-as-plugins

Date: 2026-09-10. Status: research, plus the Studio-side seam it recommended — see §6 for what
landed and what is still open. The legacy WinForms editor is unchanged by §6.

> **Historical note (2026-09-10):** the legacy WinForms editor (`DJMaxEditor`) has since been
> retired — `DJMaxEditor.Studio` is the only shell, and the sources it used to compile from that
> tree now live in `Shared/`. The WinForms references below describe where these ideas were first
> tried and are left as written; read them as history, not as a map of the tree.

## 1. What "theme" means in this repo today

Theming is currently spread across two generations:

**Generation 1 — legacy per-game render themes (already pluggable in spirit).**
`EventsRenderer.Themes` (`DJMaxEditor/Controls/Editor/Renderers/EventsRenderer.cs`)
is a list of `IEventRenderer` implementations — Null, Technika, Trilogy,
Cyclon, Respect — picked from a toolbar dropdown (`MainForm.ApplyEventsTheme`)
and persisted by name (`EditorForm`). `ZonesRenderer.Themes` mirrors it for
lane/zone art (Trilogy 4/5/6/7/8K, Respect 4/5/6/8B, …). The Technika/Trilogy/
Cyclon note themes draw embedded `Resources` bitmaps (`DJMRessources`), while
Default and Respect draw procedurally. Limits: every theme is still a
compiled-in C# class (no external art files), and the newer surfaces below
bypass this system with their own palettes (TimelineV2 only receives the theme
object for reference — `EditorForm` lines ~401–402). A theme picker dialog
(`UI/ThemePickerForm.cs`, opened from the toolbar "Themes..." button) now
lists every theme with its `GetDescription()` subtext and applies on click.

**Generation 2 — hardcoded palettes per surface**, all `static readonly`
values that can only change at compile time:

| Surface | File | Model |
|---|---|---|
| WinForms shell + legacy editor | `DJMaxEditor/UI/StudioDesignSystem.cs`, `StudioTheme.cs` | static `Color` tokens, applied imperatively |
| Timeline V2 | `DJMaxEditor/Controls/TimelineV2/Renderers/TimelineRenderTheme.cs` | static `Color` tokens |
| Vertical strip | `DJMaxEditor/Controls/Vertical/VerticalRenderTheme.cs` | static `Color` tokens + velocity shading |
| WPF Studio shell | `DJMaxEditor.Studio/Design/StudioPalette.cs` + `Theme/*.xaml` | hex-string constants → frozen brushes |
| TECHNIKA preview playfield | `DJMaxEditor.Studio/Preview/TechnikaPlayfieldTheme.cs` | frozen brush/pen set, arcade-sampled note hues |

Two things that matter for any theme system follow from this:

1. There is no single "theme object" — a theme plugin would need one seam per
   surface, or a refactor that funnels all five through one registry.
2. The WPF side freezes brushes for performance (see the `StudioTimelineTheme`
   doc comment). A theme system must keep that "build once, freeze, reuse"
   discipline: resolve the theme at load/switch time, never per-frame.

Framework note: the WinForms editor targets **.NET Framework 4.6.1**, Studio
targets **net8.0-windows**, Core is **netstandard2.0**. So compiled-code
plugins would need two different loaders (no `AssemblyLoadContext` on 4.6.1),
while a data-driven theme folder works identically on both.

## 2. How other BMS / VSRG tools do themes

### 2.1 beatoraja — the closest neighbour (BMS player, JSON + Lua skins)

beatoraja skins are folders containing a header plus `source`/`image`/`value`/
`text`/`destination` element tables, authored either as **JSON** or as **Lua**
(a `.luaskin` bootstrap that returns `{header, main}`; `main()` builds the same
tables JSON would declare, with Lua available for loops/conditionals)
[3](https://github.com/exch-bms2/beatoraja/wiki/Lua%E3%82%B9%E3%82%AD%E3%83%B3%E3%81%AE%E8%A8%98%E8%BF%B0%E6%96%B9%E6%B3%95).
The JSON→Lua relationship is deliberately near-1:1 so authors can graduate
from data to script without relearning the model
[1](https://www.kasacontent.com/musicgame/beatoraja/beatoraja-skin/4334/).

Ideas worth stealing:

- **Skin `type` per mode.** `type` 0/1/2/3/4/16/17 select 7K/5K/14K/10K/9K/24K
  play skins; other types cover select/decide/result screens
  [5](https://github.com/exch-bms2/beatoraja/wiki/JSON-Skin-Tips-(English)).
  We have the same axis: Trilogy 4–8K, TECHNIKA, Respect, Cyclon — a theme
  format needs per-mode sections.
- **`property` / `filepath` / `offset` = user-facing skin options.** Authors
  declare dropdown options (toggle BGA, fast/slow style) and swappable asset
  paths (note/laser PNG sets); the game renders the option UI automatically
  [5](https://github.com/exch-bms2/beatoraja/wiki/JSON-Skin-Tips-(English)).
  This is the "theme preferences" concept, and it is what makes one skin feel
  like many.
- **Source → destination split.** Images/fonts/values are *declared*, then
  *placed* with `dst` rects. Layout and assets stay independent, so users can
  reposition without touching art.
- **LR2 backward compatibility.** beatoraja still loads Lunatic Rave 2's
  `.lr2skin` + CSV format (`#IMAGE` / `#SRC_` / `#DST_` element tables, one-line
  `#RESOLUTION` fix for HD/FHD skins)
  [4](https://blog.bms.cab/2021/01/22/lr2-skins-beatoraja.html)
  [2](https://github.com/wcko87/beatoraja-english-guide/wiki/List-of-beatoraja-Skins).
  Lesson: if the element model is a dumb table of placed rectangles, old
  content keeps working for decades. Favour boring formats.

### 2.2 StepMania / Etterna / ITGmania — the gold standard for "themes as plugins"

A theme is a **folder** with `metrics.ini`, `ThemeInfo.ini`, and
`Graphics/`, `Scripts/`, `Sounds/`, `Fonts/`, `BGAnimations/`, `Languages/`
subfolders
[3](https://github.com/electromuis/stepmania/wiki/Theming).
The two load-bearing ideas:

- **Fallback inheritance.** Every theme is implicitly a child of `_fallback`;
  a theme only overrides what it changes and everything else resolves upward
  [2](https://deepwiki.com/itgmania/itgmania/3-theme-system)
  [5](https://github.com/stepmania/stepmania/wiki/Theming).
  A minimal theme is literally an empty folder + one `metrics.ini`
  [1](https://www.reddit.com/r/Stepmania/comments/4h8g93/how_do_i_make_a_theme/).
  This is *the* mechanism that makes a theme ecosystem grow: the cost of a
  first theme is minutes, not a full re-skin.
- **Two customization axes: theme × noteskin.** Screen/UI layout (theme) is
  independent from note art (noteskin), and metrics map noteskin names into the
  options menu
  [4](https://aceofarrows.weebly.com/stepmania-stuff.html).
  Directly applicable here: editor chrome theme vs. note/lane art theme should
  be separable choices.

Scripting (Lua actors + `Scripts/`) exists for total overhauls, but the
important part is that it is *optional* — most themes are metrics + PNGs.

### 2.3 osu! — the lowest-friction model

A skin is a folder of **conventionally-named PNGs/WAVs + `skin.ini`**,
zipped as `.osk`. The game looks elements up by filename; anything missing
falls back to the default skin, so partial skins always work
[1](https://osuskins.org/make/).
`skin.ini` carries `[General]` behaviour flags, `[Colours]` (combo colours,
selection colours), and per-mode sections like `[Mania]` with per-key-count
column widths
[3](https://github.com/emersion/osu-skin-default/blob/master/skin.ini).
Notable details:

- Some sprites are **tinted at runtime** by ini colours (draw near-white, let
  the ini recolour); others are used as-is — the tint contract is documented
  per element [1](https://osuskins.org/make/). A theme spec must say the same
  for every asset ("this PNG is multiplied by `NotePlayable`" vs. "used
  verbatim").
- Newer osu! adds skin-defined options (a JSON sidecar declaring user
  choices that patch `skin.ini` entries + swap files)
  [2](https://github.com/ppy/osu/discussions/30568) — the same
  property/ThemePrefs idea as beatoraja and StepMania, converged on
  independently. Any theme format we design should include an options block
  from day one.

### 2.4 What "an IIDX theme" concretely is

beatmania IIDX's visual identity (RemyWiki's gameplay description):

- 8 lanes: 7 keys + turntable. Keys 1/3/5/7 are **white**, 2/4/6 are **blue**,
  scratch lane is **red**; notes fall to a **red judgement line**
  [1](https://remywiki.com/What_is_beatmania_IIDX).
- The piano-style white/black key stagger is the physical mnemonic players
  learn; note colour tracks *lane*, not note type
  [3](https://www.reddit.com/r/DanceDanceRevolution/comments/1h684hp/why_do_find_beatmania_iidx_popn_music_and_sound/).
- Signature chrome: groove gauge, judgement popup tiers, lane covers
  (SUDDEN+/HIDDEN+/LIFT)
  [1](https://remywiki.com/What_is_beatmania_IIDX).

Mapped onto our surfaces, an IIDX theme would mean:

1. **Preview playfield** (`TechnikaPlayfieldTheme` sibling): dark field,
   white/blue/red lane columns, red judge line, groove-gauge HUD cell. This is
   where the theme reads most strongly.
2. **Vertical strip / timeline lane tints**: alternate lane backgrounds by
   white-key/blue-key role; note bodies coloured by lane (white/blue/red)
   instead of by type.
3. **Editor chrome**: optional; a darker blue-grey shell variant. StepMania's
   lesson says keep this a separate toggle from note art.

Copyright caution, consistent with the existing `TechnikaPlayfieldTheme`
approach (colours sampled, chrome re-derived, no shipped assets): lane/note
colours and layout geometry are fine to reproduce; do not lift Konami bitmaps,
fonts, or sound effects. Our theme format should only ever ship original or
user-supplied art.

## 3. "Themes as plugins": three options

### Option A — Data-driven theme folders (recommended)

```
Themes/
  _fallback/                 # ships with the app; the current Studio look, as data
    theme.json               # palette, fonts, metrics, per-mode sections, options schema
    notes/ 4k-white.png ...  # optional art overrides (else procedural/vector)
  IIDX/
    theme.json               # only overrides what changes; rest falls back (cf. §2.2)
    notes/ ...
```

- `theme.json` schema sketch: `meta {name, author, version, targets}`,
  `palette {…StudioPalette keys…}`, `timeline {grid, note fills/edges per
  role}`, `vertical {column tints}`, `playfield {per-mode lane/judge/HUD}`,
  `options [{name, type, default}]` (cf. beatoraja `property`, osu! skin
  options).
- Loader: parse → deep-merge over `_fallback` → build the frozen brush/pen
  sets once (preserving the current no-per-frame-allocation discipline) →
  `IThemeRegistry.ActiveThemeChanged` event; all renderers re-resolve.
- Works on **both** frameworks with one code path (JSON + PNG, no code
  loading). Hot-reload is trivial (watch folder, rebuild). No security review
  surface: a theme can't execute code.
- This is the osu!/beatoraja-JSON/StepMania-metrics intersection, and it is
  enough for an IIDX theme, a light theme, colour-blind palettes, and
  streamer-friendly high-contrast variants.

### Option B — Scripted themes (Lua)

Same folder, plus an optional script entry (`theme.lua`) evaluated with a
sandboxed Lua host (MoonSharp/NLua), exactly the beatoraja Lua-skin / StepMania
BGAnimations role: computed layout, animated elements, conditional art.

- Power: layout logic, theme options with real UI, animated preview chrome.
- Cost: a stable scripting API is a long-term compatibility promise; sandbox
  bugs become CVEs; two runtimes (netfx + net8) to wire.
- Verdict: design the JSON schema so a script layer *could* generate it later
  (beatoraja's JSON↔Lua 1:1 mapping is the pattern), but don't ship scripting
  in v1.

### Option C — Compiled plugin DLLs

`IEditorTheme` contract in `DJMaxEditor.Core` (netstandard2.0, so both hosts
can reference it); scan `Plugins/*/PluginName.dll`; instantiate\ndiscover implementations.

- On Studio (net8) the modern loader is a collectible `AssemblyLoadContext`
  per plugin + `AssemblyDependencyResolver`, which isolates conflicting
  transitive dependencies and supports unload
  [3](https://www.devleader.ca/2026/04/07/plugin-architecture-in-c-the-complete-guide-to-extensible-net-applications)
  [1](https://github.com/DevD4v3/CPlugin.Net);
  MEF is in maintenance mode and not recommended for new work
  [3](https://www.devleader.ca/2026/04/07/plugin-architecture-in-c-the-complete-guide-to-extensible-net-applications).
- On the WinForms editor (netfx 4.6.1) there is no `AssemblyLoadContext` —
  plugins would load via `Assembly.LoadFrom` (no isolation, no unload) or
  AppDomains (heavy, deprecated-leaning). A theme API stable enough to load
  untrusted DLLs against is also a much bigger design job than a JSON schema.
- Verdict: right tool for *functional* plugins (importers, validators,
  generators), overkill and risky for *visual* themes. If we ever want code
  plugins, build the host once for features and let themes stay data.

## 4. Per-format themes and real game assets (BMS / Respect V / Technika)

The editor already detects its input formats positively (`ChartFormat` in
`DJMaxEditor.Core/FormatCapabilities/ChartFormat.cs`): `BmsClassic`
(.bms/.bme/.bml/.pms), `TrailerRespectV`, `PtffDecrypted`/`PtffEncryptedTechnika`
(Technika/Trilogy), `CyclonXml`. That gives a natural default-theme mapping:

| Detected format | Natural theme | Notes |
|---|---|---|
| `BmsClassic` | IIDX-style (§2.4) | BMS has no canonical skin; IIDX-clone layouts (white/blue/red lanes) are the de-facto standard — most circulated BMS play skins imitate arcade IIDX, e.g. WMIX-family ports [2](https://github.com/wcko87/beatoraja-english-guide/wiki/List-of-beatoraja-Skins) |
| `Ptff*Technika*` | TECHNIKA arcade | note art keyed to *type* (tap/chain/hold/repeat), scanline playfield — the existing `TechnikaPlayfieldTheme` already encodes the sampled palette |
| `TrailerRespectV` | Respect V | per-button-mode lane counts already exist as zone renderers (4/5/6/8B) |
| `Ptff*Trilogy*` | Trilogy | 4–8K zone renderers already exist |
| `CyclonXml` | Cyclon | renderer exists |

Auto-selecting the matching theme on file open (with manual override kept in
the existing dropdowns) is cheap UX once §3-Option-A lands: detection already
exists, only the default-theme lookup is new.

### 4.1 Asset inventory ( reviewed 2026-09-10 while the repos were briefly public )

Two repos, two very different shapes:

**Shino-Toku — TECHNIKA arcade note/effect frames** (2,844 files: 2,669 PNG
+ 84 `.str` + README). Layout is `<family>/<part>/frames`:

- 6 families `0`–`5` (matches the family index the existing
  `TechnikaPlayfieldTheme` comment already knew about). Families 1–5 share one
  layout (~510–521 files each); family 0 is smaller (272).
- Parts per family: `cool/`, `good/`, `hold/{last,line,note}/`,
  `long/{last,long}/`, `max/`, `onePoint/`, `onePointLong/`,
  `onePointLongLast/`, `onePointLongLine/` — i.e. tap notes, hold/chain
  segments + caps, and per-judgement hit effects, each as a numbered PNG
  frame sequence (`cool_0000.png` … `cool_0022.png`, …).
- The `.str` files are **binary** (magic `STRM`): an embedded frame-filename
  list followed by per-frame float transforms. Usable as animation data later;
  ignorable for v1 (frame 0 is the rest pose).
- Spot checks: `0/onePoint/bar_0000.png` is a 128×256 magenta/blue starburst
  note; `0/cool/cool_0000.png` is a 256×256 radial judgement burst. Square
  power-of-two effect frames, alpha everywhere.

**Shino-Tokuu — Respect V Unity extraction** (13,236 files: 12,273 PNG,
788 OGG, 10 `.bin`, 2 `.otf`, manifest + README). Layout follows the
extractor (`Texture2D/`, `Sprite/`, `TextAsset/` splits, `@<id>` filename
suffixes):

- `Base Game Skins/Notes` (1,984): `mini_note_{w,r,b}{4,5,6}[_long]` lane
  sprites (white/red/blue × button mode — spot check: 38×15 gold-core bar
  with blue end caps), `tech_note_slide/long` tails, `fever_note_*`,
  `effect_note_*` modifier icons.
- `Base Game Skins/Gears` (1,865): full playfield atlases up to 8192px
  (`…-GearMusedash-…`, `…-GearTekken-…`, …) plus small loops. Whole-gear art,
  not slices — consuming these needs rect/UV metadata we don't have.
- `Base Game Skins/Coolbomb Effects` (3,910): `coolbomb_NNNNN` frame sprites.
- `Mode Skins/` (2,050 files, 30 packs: arcaea, chunithm, ongeki, tekken,
  ez2on, valentine, maid, …): each pack is `mode_bg_*` + `mode_logo_*` PNGs
  and `bgm_*`/`se_*` OGGs from one source `.unity3d` bundle.
- `Song Covers/` (3,264): `song_pic_*` thumbnails — irrelevant to themes,
  useful only if we ever build a song-select browser.
- `Config Tables/` — the jackpot: the game's own skin registries as **CSV**.
  `note_skin` rows carry `prefabName` (`NoteDefault`, `NoteP1`,
  `NoteTechnika1`, …), `noteAnimName`/`coolbombAnimName`, and per-mode
  metrics (`4b/5b/6bHeight`, `Top`/`Bottom` insets, `Calibration`).
  `gear_skin` rows carry `atlasName` plus HUD layout numbers (combo/HP/record
  positions, gear left/right X). `mode_skin_table` + `icon_table` complete
  the set.

### 4.2 What the inventory implies for the design

1. **Explicit manifest, not filename convention.** The two repos share no
   naming scheme (`cool_0000.png` vs `mini_note_w4 @28021.png`), so the
   theme→asset map must be an explicit logical-name → relative-path table in
   `theme.json` (§3/Option A), not a pattern. The CSV tables above can seed an
   *importer* that generates that manifest semi-automatically (read
   `note_skin` metrics + resolve `prefabName` to chosen frames), but the
   runtime format stays hand-authorable.
2. **Static sprites first, animation later.** Everything the *editor timeline*
   needs is a single rest frame per logical sprite (note bodies, lane Judge
   art). `.str` sequences and `coolbomb_*` frames only matter for an animated
   *preview* playfield — Phase 4 territory, not v1.
3. **Don't load gear atlases into the editor.** Multi-thousand-pixel atlases
   without slice metadata are useless to a timeline renderer; preview chrome
   stays procedural/vector (current approach) until/unless someone authors
   nine-slice rects. Budget-wise: note sprites are KBs, gears are MBs —
   the manifest should flag asset *role* (`timeline` vs `preview`) so the
   loader only pulls what's needed.
4. **Audio is out of scope for v1.** The 788 OGGs (menu BGM/SE per mode skin)
   don't map to any editor surface today; note them as a possible future
   "preview hitsounds" pack and move on.
5. **Provenance belongs in the manifest.** Tokuu's `shared-assets-manifest.json`
   records source bundle + byte counts per pack — copy that habit: our
   generated manifests should record source repo + revision + the CSV row id
   each sprite came from, so a re-extract stays traceable.

### 4.3 Where the asset bytes live — licensing

TECHNIKA arcade assets and Respect V Unity extractions are both ripped game
data. Recommendation, in line with how this repo already treats palettes
(sampled colours, re-derived chrome, no shipped artwork — see the
`TechnikaPlayfieldTheme` doc comment):

1. **Never bundle, submodule, or link these repos from DJMax-Studio.** No
   PNG/OGG in git, no submodule pointer, no in-app download/fetch from them.
2. Support **user-provided asset folders**: the theme loader resolves an
   `assetPack` path the user configures locally (their own clone/export). The
   app ships procedural fallbacks, so it is complete without the pack.
3. Keep the asset repos private (they were briefly public for this review on
   2026-09-10 and should go back to private) and don't reference them by URL
   from in-app UI. This doc describes their *layout*, not their location.

## 5. Recommended path

1. **Phase 1 — one theme seam.** Introduce `IThemeRegistry`/`ThemeSnapshot`
   in Core; re-express the Gen-1 theme classes' colours plus the five
   hardcoded palettes as the built-in `_fallback` theme. No visible change,
   but every renderer now resolves colours (and sprite lookups) through the
   registry. Keep `GetName()`/dropdown selection working throughout.
2. **Phase 2 — built-in IIDX theme + format-default mapping.** Hardcode a
   second theme behind the existing theme switcher to prove the seam: IIDX
   lane roles (white/blue/scratch), red judge line, groove-gauge HUD in the
   preview, lane-tinted timeline. Auto-select default theme from the detected
   `ChartFormat` (§4 table); user override sticks.
3. **Phase 3 — user theme folders + asset manifest.** `Themes/<Name>/theme.json`
   (+ optional PNGs), deep-merged over `_fallback`, with an options block from
   day one (§2.1–2.3 all converged on this) and the §4.1 sprite manifest with
   procedural fallback. Ship 2–3 examples: IIDX, light,
   high-contrast/deuteranopia-safe. Separately, one local-only asset pack
   config pointing at the user's TECHNIKA-Asset clone — never committed.
4. **Phase 4 (only if demanded) — scripting.** Optional `theme.lua`
   generating the same schema Lua-side, beatoraja-style.

Non-goals for v1: compiled theme DLLs, animated skins, porting actual LR2/
beatoraja skin files (their element model targets a *player* HUD — gauges,
lasers, BGA — not an *editor* timeline; the concepts transfer, the files
don't).

## 6. Status update: the Studio seam (2026-09-10)

Phase 1 and Phase 2 are in, scoped to the WPF studio surfaces only. This is the seam §5 asked for,
not a theme *system* yet: no folders, no JSON, no scripting, no compiled plugins.

**What landed**

- `DJMaxEditor.Studio/Design/StudioChartTheme.cs` — a theme as data: hex strings plus one
  behavioural flag, no WPF objects, so it can be constructed, compared and listed without a
  Dispatcher. Fallback inheritance is expressed as C# property initialisers rather than a
  `_fallback` folder: every property defaults to the Studio value, so a theme lists only what it
  changes. Three built-ins: `studio` (the shipped ptSequencer-derived canvas, byte-for-byte the
  palette that was hardcoded), `iidx`, and `respectv`.
- The IIDX palette is the second built-in and the proof the seam is real. It needed no new lane
  model, because the layout already carries the roles: `VerticalTrackLayout`'s BMS plan numbers its
  keys 1-7 and stripes them primary/alternate on exactly IIDX's parity (odd key → `RegularPrimary`,
  even → `RegularAlternate`, scratch channel → `IsScratch`). So the theme sets
  `NotesColouredByLane` and the canvas colours white key / blue key / turntable from what the
  column already knows, with a red playhead standing in for the red judgement line. Provenance is
  the same rule `TechnikaPlayfieldTheme` follows: re-derived values, no shipped artwork.
- `StudioTimelineTheme.ForTheme(definition)` — the one place hex strings are parsed. It caches one
  frozen brush set per theme id, which is what keeps §1's "build once, freeze, reuse" discipline
  intact when a theme can change at runtime. `RefreshTheme(theme)` on the canvas and the volume
  lane re-resolves through it and invalidates; both are no-ops for the theme already in use, so the
  shell's single `ApplySettings` pass can push it unconditionally.
- `AppearanceSettings.ChartThemeId` persists the choice in `studio-settings.json` and normalises
  through `StudioChartTheme.Find`, so a settings file naming a theme this build does not ship comes
  back holding the one that will actually be drawn.
- A toolbar "Themes…" button opens `ThemePickerWindow`: one row per palette with its name, a
  sentence on what it is for, a strip of its own colours, and a dot on the one in use. Clicking a
  row applies immediately, so the chart behind the dialog is the preview. It keeps the legacy
  `ThemePickerForm`'s contract — the dialog mirrors the shell's state through a delegate rather
  than owning it, and re-reads that delegate after an apply instead of assuming the click took.
- The `respectv` built-in: the RESPECT V reading of the canvas — deep-space field, ice-white
  primary keys, azure alternating keys, the pink side acts, lane chips all set. The note hues
  are sampled from the owner's Shino-Tokuu extraction (`NoteSteam_000` atlas), not invented.
- Layout answers the palette at the width axis too: `VerticalTrackLayout.BmsScratchWidth` is
  1.5× the key width (beatoraja's BMS convention), so an SC/BMS chart reads like the game's —
  the turntable column announces itself by width rather than by a taller block.
- §4's per-format default is also in: adopting a chart suggests a theme by its container
  (`iidx` for BMS/BMSON, `respectv` for RESPECT V trailers, `studio` for TECHNIKA PTFF) until
  the user has picked one this session, and a suggestion they never confirmed is not written
  back over their settings at shutdown.
- The companion to user art for the preview side is `docs/arcade-assets.md`: TECHNIKA keeps
  its numbered Shino-Toku sets (glyphs plus the CoolBomb burst, whose folder tree the loader
  now also accepts unwrapped), and the RESPECT V playfield (`RespectPlayfieldView`) dresses the
  package-derived lane geometry in the Shino-Tokuu base gear — glass, header plate, bottom deck
  and rails — with the same re-derived fallback when the extraction is not staged.

**What is deliberately not themed**

- The TECHNIKA gameplay playfield. Its colours are sampled from the arcade and are the point of the
  panel; recolouring them would make the preview lie about the game. The picker says so in its
  footer rather than leaving it to be discovered.
- Shell chrome (`StudioPalette`'s surfaces and text). §2.2's lesson was to keep the chrome axis
  separate from note/lane art, and with only one chrome to offer, a setting for it would be a way
  to be wrong later.

**Still open**

- Phase 3: `Themes/<Name>/theme.json` folders deep-merged over the built-ins, with an options block
  and the §4.1 asset manifest. `StudioChartTheme` is data-only and `ForTheme` is already a
  per-id cache, so the loader's job is "parse, merge over the defaults, hand the result to
  `ForTheme`" — plus a `ClearCache()` (already present) for reload, and validation of hex strings
  before anything reaches `ColorConverter`.
- The RESPECT note atlases' slicing table (`Config Tables/note_skin` in Shino-Tokuu). Until
  that is imported, `RespectPlayfieldView` draws notes as vector bars in the sampled hues; the
  gear sprites it *does* stage don't need a slice map because they are whole-frame parts.
- The legacy WinForms surfaces. `TimelineRenderTheme`, `VerticalRenderTheme` and the Gen-1
  renderer themes still carry their own palettes; a data-driven loader is the one part of §3's
  Option A that would reach both hosts through one code path.
