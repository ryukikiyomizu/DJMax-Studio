# Arcade assets (local, not redistributed)

The studio's gameplay previews can dress themselves in the owner's own extracted arcade art.
Nothing in this document describes committed content: every folder mentioned below is
gitignored, a fresh clone renders procedurally instead, and setting the folders up is one
script. This is the same arrangement `LocalAssets\Technika2` has always had, extended to cover
the RESPECT V gear.

## Where things live

| Folder | Feeds | Source |
| --- | --- | --- |
| `DJMaxEditor.Studio/LocalAssets/Technika2/` | TECHNIKA playfield note glyphs + CoolBomb hit burst | `ryukikiyomizu/Shino-Toku` (numbered sets `0`–`5`) |
| `DJMaxEditor.Studio/LocalAssets/RespectV/Gear/` | RESPECT V playfield glass, header plate, bottom deck, edge rails | `ryukikiyomizu/Shino-Tokuu` (`Base Game Skins/Gears/Texture2D`) |

Overrides when the art lives elsewhere:

- `DJMAX_EDITOR_TECHNIKA_ASSETS` — TECHNIKA root (also used to point at a *different* numbered
  set than the one copied: the loader resolves roots, set folders and whole trees).
- `DJMAX_EDITOR_RESPECT_ASSETS` — RESPECT V gear folder.

## One-time setup

```powershell
git clone https://github.com/ryukikiyomizu/Shino-Toku.git
git clone https://github.com/ryukikiyomizu/Shino-Tokuu.git   # large: gears + covers + skins

powershell -File scripts\fetch-arcade-assets.ps1 `
    -TechnikaRoot <clone>\Shino-Toku `
    -RespectRoot  <clone>\Shino-Tokuu
```

The script (`scripts/fetch-arcade-assets.ps1`, comments document every choice):

1. Copies one TECHNIKA numbered set (`-Set`, default `0`) into
   `LocalAssets\Technika2\<set>\`. The glyph loader finds flat note art there; the judgment
   burst loader finds `cool\cool_0000.png…` in the same set, so the two stay of one piece. Sets
   `0`–`5` are alternate skins of the same parts, so one is enough.
2. Copies five pinned RESPECT V gear textures into `LocalAssets\RespectV\Gear\` under plain
   names (`gear_bg.png`, `gear_back.png`, `gear_bottom.png`, `gear_frame_left.png`,
   `gear_frame_right.png`). The pins are Unity asset ids — the base-skin folder holds several
   same-named variants, and these are the five whose pixel sizes match the native 502-wide
   playfield that `Shared/Preview/RespectGameplayLayout` derives its geometry from. If a dump
   refresh renumbers the ids the script falls back to the first same-named texture and warns.

The RESPECT note *atlas* (`Notes/Texture2D`, e.g. `NoteSteam_000`) is deliberately not staged:
its slicing coordinates live in the game's config tables (`Config Tables/note_skin`), and
until those are imported the notes are drawn as vector bars — silver-white primary lanes
and azure alternating lanes in the hues sampled from that atlas, hot-pink shoulder bars
and teal side-track bars per `docs/respectv-playfield-research.md` (the extraction carries
no art for either bar, and the old aqua-shoulder / red-purple-side reading of the atlas
was wrong about both).

## Fallbacks

- No TECHNIKA folder → the repository's own arcade strips (the shared `Timeline/Notes/`
  resources the editor timeline draws, sliced the same way a local extraction would be) draw
  the heads and the hold/drag/repeat trails; the run connecting lines, the actively-held trail
  variants, the approach ring and the hit burst keep their vector fallbacks. The panel header
  reads the packaged label.
- No RESPECT V gear → `RespectPlayfieldView` draws the same scenes as flat plates in the same
  hues (header reads `RE-DERIVED GEAR` instead of `SHINO-TOKUU GEAR`).

Either way nothing commits: `.gitignore` carries `/DJMaxEditor.Studio/LocalAssets/` and the
script's destination lives under it.
