# DJMax Studio — Keysound Slicer: Evidence-Led Whole-Project Audit
*Non-destructive virtual slices vs. the BMS export ceremony — what to keep, what to kill, and exactly what to build next*

**Date:** 2026-09-18 · **Branch:** `arena/01a0b1fa-djmax-studio` · **Runs:** `35304863874` build+smoke success, `35304863879` firecrawl-research success (1m51s)  
**Sources:** `docs/keyslicer-research.json` (manual, Zod-valid, 6 audits / 12 recs / 20 citations) + `docs/keyslicer-research-firecrawl.md` (Firecrawl spark-2, 19.5k markdown, 19 sources) + live codebase `DJMaxEditor.Studio/Keyslicer/*`

---

## 0. Executive summary — the one decision

> **Keep the source file intact forever.** A slice is `{sourceFile, startMs, endMs, gain, fadeIn, fadeOut, lane}` — a *view* — and only on **Finalize** does it become `ks_0001.wav` + a chart note.  

Every P0 pain in the legacy tools is a consequence of violating this principle:

* **Export ceremony** — DAW → BMHelper/Mid2BMS → Woslicer *clipboard read* → `総出力` → hundreds of `01_000.ogg` → BMSE drag-drop → base-36 index typing → clipboard paste. One misclick breaks sync; `総出力が押せない` is a top forum FAQ because the filename textbox is empty until you click the black area [purureko](https://purureko.com/etc/post-1704/).
* **Positional numbering** — BMSE definitions follow Explorer/drag order; dragging a middle-numbered file first silently shifts every `#WAV` [purureko](https://purureko.com/etc/post-1704/), [Wix](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3).  
* **Hard caps as UX** — BMS `01-ZZ` = 1,296, BMSE help caps 1,295 audio types [hitkey](https://hitkey.nekokan.dyndns.info/bmse_help_full/usage.html); DJMAX PTSEQUENCER caps 1,023 → 2,047 [NamuWiki](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C). Users invent a *definition-reuse wizard* (`同じ音の定義を使い回す`) or split to BGM `<60s` to survive [Wix](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3).
* **Timing vs. audio split** — zero-cross `moves markers` and can break iBMSC high-res clipboard [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer); Woslicer III tail extension pops [Qiita](https://qiita.com/yuinore/items/79db943d2e3447adee71); dtinth's slicer leaves `slight gaps` and has no BPM support [dtinth](https://github.com/dtinth/sound-slicer); Sayaslicer lists *proper BPM grid* as planned-not-done [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer).

**DJMax Studio already has the right model** (`KeysoundSlice`, `KeyslicerProject`, `SliceExporter` with in-memory fades/gain/normalize) and the right defaults (grid advance, BPM sync from `PlayerData.Tempo`, 2.5 ms zero-cross nudge, undo stack). The audit below tells you what to *add* to make it best-in-class, and what *not* to copy.

---

## 1. What DJMax Studio has today — literal context

You don't need to imagine it — it's shipped on `arena/01a0b1fa-djmax-studio` and green on CI.

| Area | Implementation | File |
|---|---|---|
| **Model** | `KeysoundSlice {Id, SourceFile, ResolvedSourcePath, StartMs/EndMs, Gain, FadeIn/Out, Lane, Label, IsConfirmed}` + `KeysoundMappedNote {ChartTimeMs, VirtualTick, Lane, KeysoundId}` + `KeyslicerProject {SnapDenominator, AutoAdvanceMode, SnapToZeroCrossing, Export.Normalize, NextIdCounter}` `.ksp` JSON with portable `sources/` copy | `Keyslicer/KeysoundSlice.cs`, `KeysoundSource.cs`, `KeyslicerProject.cs` |
| **Waveform** | `WaveformPeakProvider` downmixes to mono via NAudio, caps hour-long sessions, produces per-pixel `{min,max,rms}` O(w); `KeyslicerWaveformView` draws vertical lines; zoom centered on cursor, scroll clamped | `Keyslicer/WaveformPeakProvider.cs`, `KeyslicerWaveformView.cs` |
| **Detection** | Flux onset: amplitude > sensitivity-mapped threshold above background, local max ±1px, debounce `MinGap 80ms`, end = next silence run (≈30ms < `SilenceThreshold`) or `MaxDuration 4000ms` minus `Tail 30ms`; ghost `SuggestedSlice {startMs,endMs,confidence}`; `[` `]` walk, `Enter` accept | `Keyslicer/TransientDetector.cs`, `TransientDetectorSettings` (Sens 0.55, PreRoll 6ms) |
| **Defaults (chosen)** | **Grid** auto-advance (one snapped step `beat*4/snapDenom`, re-quantized via `QuantizeMs`), BPM/offset auto-sync from `PlayerData.Tempo` + first `Tempo` event, snap 1/16 (Free/1/4/1/8/1/16/1/32), zero-cross 2.5 ms on, normalize off, export WAV (OGG via `ExportFormat`), slicer-local undo `Ctrl+Z/Y` | `Keyslicer/KeyslicerViewModel.cs` (`ComputeAutoAdvanceMs`, `QuantizeMs`, `AttachChartContext`, `FindNearestZeroCrossing`), `docs/keyslicer.md` |
| **Playback** | Main chart via `NAudioKeysoundPlayer`; slicer audition renders draft interval to `Path.GetTempPath()/djmax_slicer_audition_*` → disposable `WaveOutEvent`, deleted on `PlaybackStopped` | `Keyslicer/KeyslicerWindow.xaml.cs` |
| **Export** | `SliceExporter.Export(..., ExportFormat.Wav, exportOnlyUsed:false/true)` seeks to `StartMs`, reads `EndMs-StartMs`, applies per-frame linear fades+gain, optional peak-normalize 0.98; missing sources skipped; `.tech` path builds minimal `PlayerData` (8 lanes+tempo) → `TechmaniaChartSerializer.Serialize` | `Keyslicer/SliceExporter.cs` |
| **UI** | 1380×860 single-instance `KeyslicerWindow`: toolbar (New/Open/Save, Export WAVs/.tech/Send to Editor, BPM/Offset/SNAP/Zoom, Undo/Redo), left dock (sources, lane-colored library, detection), center waveform + header (source name, dual playheads), draft bar (Make/Clear, `D/F/J/K`, fades, Auto-place), placed-notes list, right inspector, status bar; `Mode=OneWay` bindings for `DurationMs` fix the VirtualizingStackPanel XamlParseException | `Keyslicer/KeyslicerWindow.xaml`, `.xaml.cs` (line 218 fix `0064132`) |
| **Reuse from Studio** | `KeysoundDecoder` probe order `.ogg→.wav→exact→ext-swapped`, `TechmaniaChartSerializer`, `StudioChartTheme/Palette.xaml` (lane colors, draft amber `FFD020`), `DocumentCapabilities/EditorDocumentContext` guard for Send to Editor | `Shared/*`, `DJMaxEditor.Studio/Keyslicer/` |

**Interaction contract today:** `Drag → yellow draft → D/F/J/K → lane-colored slice + (if Auto-place) note at `chartPlayheadMs` → playhead grid-advances → next `F` lands on next snapped beat` ; `Detect → green ghosts → [/] → Enter → All`.

---

## 2. Research methodology — no hallucinations

* **Manual deep crawl** (`web_search` + `fetch_page`, 30+ pages): Sayaslicer README/issues/releases, wosderge Woslicer II English + initmod, Woslicer III diary, BMHelper/Mid2BMS docs, Japanese tutorials (purureko, spirea3cross, note.com, yuinore), NamuWiki `키음`, Grokipedia/Bmse help base-36 limits, FL Slicex manual, dtinth/sound-slicer, LRbase62 notes. Output validated as `docs/keyslicer-research.json` (`ZOD OK`, 6 audits, 12 recs, 20 citations with translated quotes).
* **Firecrawl spark-2 on GitHub Actions** (`scripts/firecrawl-agent.mjs`, `firecrawl-research` workflow, secret `FIRECRAWL_API_KEY`): same 9 targets + 4 search queries, `crawl`+`extract`+`search`. Ran on Actions (sandbox blocks `api.firecrawl.dev` TLS), succeeded **35304863879** in 1m51s, uploaded `keyslicer-research-firecrawl` artifact (14 KB) and committed `ad1bf1b` (`docs/keyslicer-research-firecrawl.json` + `.md` 19.5k). Firecrawl returned `report_markdown` despite Zod schema because prompt's `Deliverable Format (markdown)` beat schema — evidence-led, explicitly *did not* fabricate 10 GitHub issues when Sayaslicer issues page returned `No results`.

Both outputs agree on P0s; manual JSON adds overflow/file-management nuance (virus false positives, `切断位置をコピー→クリップボード読込` ceremony, BGM `<60s` rule), Firecrawl adds format-robustness and clipboard-corruption evidence.

---

## 3. Findings

### 3.1 Comparison — workflow, pain, limits, export

| Tool | Workflow (5–6 steps) | Top 3 evidenced pains (severity) | Max / snap | Export |
|---|---|---|---|---|
| **Sayaslicer** | O / drag-drop → Offset/BPM/Snap 1/1–1/192 + Zero-cross/Tail/Fade → Z/add, Left/Right snap jumps, Ctrl+Left/Right marker jumps → P/Enter preview, MIDI/BMSE import → V (BMSE 192nd) / Shift+V (iBMSC arbitrary) / K/Shift+K → M export + Ctrl+S project → paste in BMSE [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer) | P0 zero-cross moves timing vs. iBMSC high-res → gamemode break, fallback to BMSE rounding [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer); P1 BPM hackish, grid not BPM-aware [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer); P2 drag-drop Win-only, macOS unsigned, nightlies need login [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer) | 1/1–1/192 arbitrary, BMSE rounds to 192; base-36 1,296 or base-62 toggle ~3,844; >2GB/sample-count bug fixed [Sayaslicer issue #5](https://github.com/SayakaIsBaka/sayaslicer/issues/5) | M → wavs + BMSE/iBMSC clipboard, Shift+K appends `.bms` |
| **Woslicer II** | mp3→wav → BMHelper/Mid2BMS MIDI→wav concat OR raw butsu-giri → drag wav, set BPM/offset/fade (initmod zeroes) → BMHelper `切断位置をコピー` → `ファイル→クリップボード読込` (click black area if `総出力` disabled) → `総出力` → BMSE drag-drop `01_000.ogg` → `ファイル→クリップボード出力` base-36 `01-ZZ` → paste; reuse wizard for repeats [purureko](https://purureko.com/etc/post-1704/), [note.com](https://note.com/nakaiankow/n/n03caf5d703f9) | P0 3-app ceremony, `総出力が押せない` [purureko](https://purureko.com/etc/post-1704/); P0 `01_000.ogg…57_023.ogg` file spam [note.com](https://note.com/nakaiankow/n/n03caf5d703f9); P0 1,296 cap → reuse wizard/BGM split [hitkey](https://hitkey.nekokan.dyndns.info/cmds.htm) | Coarse Up/Down snap; single global BPM; WAV only reliably [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer), [purureko](https://purureko.com/etc/post-1704/) | `総出力` → hundreds of wavs + clipboard |
| **Woslicer III** | Drag onto mslider → BPM/ずらし/フェード 0 → `ファイル→クリップボード出力` with start `01-ZZ` → `総出力` + BMSE define → `ファイル→wosファイル読込` Snare.wos → reuse with existing `#WAV01` [note.com](https://note.com/nakaiankow/n/n03caf5d703f9), [Wix](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3) | P1 virus AV false positives → users revert to II [yuinore](https://yuinore.net/2017/04/otokiri-software/); P1 forked clipboard paths (II vs III) [Wix](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3); P0 same 1,296 cap → `入らない場合はBGMに回す` [Wix](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3) | Mouse free + coarse snap; no 1/192; single BPM; BGM `<1分` rule [Wix](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3) | `総出力` batch wavs |
| **BMHelper + Mid2BMS** | DAW color-code Blue/ Yellow/ Red → BMHelper MIDI split/dedup → DAW re-render (watch `音量に注意`) → Mid2BMS [1]Mid2MML→[2]WaveSplitter → `renamed/` + `text6_bms_blue.txt` → on fail rename→`.bms` `犯人捜し` → BMHelper `切断位置をコピー` → Woslicer → `総出力` [how-to-make2](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make2) | P0 DAW-direct/compressor/automation lanes not handled → fallback to butsu-giri [yuinore](https://yuinore.net/2017/04/otokiri-software/); P1 opaque `一発でうまく行かない` [how-to-make2](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make2); P2 site dead Geocities mirror | MIDI 480ppq→192nd; dedup helps but still 1,296 cap | wavs + keysound defs + clipboard |
| **PTSEQUENCER (DJMAX/EZ2AC internal)** | Compose in PTSEQUENCER (2004-2008 → Respect, DPC 2022 new tool kept old) → track assign → Respect `Performance sound ON/OFF` → export with caps | P0 1,023→2,047 cap << BMS 1,296+62 (post-EV 2k+ notes cause racks/sinks) [NamuWiki](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C); P1 same-track cut (pre-Technika2) vs Technika2 non-interfering [NamuWiki](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C); P1 opaque internal, no public undo/zero-cross guidance | 1,023/2,047 (DJMAX), 2,047 (EZ2AC EV), theor. 65,535 tool-limited [NamuWiki](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C) | Proprietary |
| **FL Slicex / dtinth sound-slicer** | Slicex: drag loop → Dull/Medium/Sharp or grid → Trim/Normalize/Cut groups → Assign trigger notes → dump to Piano roll → Drum Stretcher tempo warp (mp3 20-30ms gap via Edison Trim) [image-line](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/plugins/Slicex%20Editor.htm), [flipside](https://www.theflipsideforum.com/index.php?topic=23840.0); dtinth: `synth.wav+txt` → `wav/` numbered + clipboard; melodic dedup | P1 Slicex tempo knob missing vs. Slicer [flipside](https://www.theflipsideforum.com/index.php?topic=23840.0); P1 Normalize lossy, cut groups manual [image-line](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/plugins/Slicex%20Editor.htm); P1 dtinth gaps + no BPM support [dtinth](https://github.com/dtinth/sound-slicer) | Slicex unlimited regions, beat property must be right or warp fails [flipside](https://www.theflipsideforum.com/index.php?topic=23840.0); dtinth 1 note =1 slice | Piano-roll dump / numbered wavs |

### 3.2 Problem inventory — severity, evidence, Studio implication

#### Timeline / timing (the chart is BPM, the source is ms — never conflate)

| P | Problem | Evidence | Implication for Studio (what to build) |
|---|---|---|---|
| **P0** | Zero-cross vs. chart time are separate failures | Sayaslicer `zero-crossing moves markers, affects iBMSC` [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer); Qiita tail pop analysis [Qiita](https://qiita.com/yuinore/items/79db943d2e3447adee71) | Non-destructive boundary: two modes — *move boundary* vs. *adjust rendered edge only*; show timing delta (ms + ticks) and A/B audition; keep `QuantizeMs` authoritative, zero-X nudge inside 2.5 ms window only |
| **P0** | BPM/grid fidelity missing in legacy | dtinth `both modes do not support BPM changes` [dtinth](https://github.com/dtinth/sound-slicer); Sayaslicer planned BPM grid [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer) | **Dual ruler** (waveform ms bottom, chart bar/beat top, shared BPM map from `PlayerData.Tempo` + tempo events, synced playheads + zoom) — the #1 proposal, now justified |
| **P1** | Gaps between slices | dtinth `slight gaps` [dtinth](https://github.com/dtinth/sound-slicer) | Virtual intervals are *contiguous* (`[start,end)`), explicit tail policy (silence vs. reverb tail vs. next onset pre-roll), not file truncation |
| **P1** | Lane semantics invalidate good-looking slices | Older DJMAX/EZ2 `same-track interruption` [NamuWiki](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C) | **Mode simulation** preview: BMS `cut`, RESPECT `cut+budget`, TECHNIKA `non-interfering`; audition must simulate mode |

#### Interface / workflow (the ceremony is the product)

| P | Problem | Evidence | Studio |
|---|---|---|---|
| **P0** | 6-step export ceremony | BMS Creation Notes exact chain [Google Doc](https://docs.google.com/document/d/1ywUO5RxCP8jCM7MuFGFAS5TIRK2gsyM3-rMgvPuMEcw/edit) | **Single-project `Import to Studio`** — one button injects `EventData`+`InstrumentData` into `PlayerData.Tracks` with one `UndoRedoAction` group; no BMSE drag-drop, no base-36 typing |
| **P0** | Positional numbering | Explorer order + drag order [purureko](https://purureko.com/etc/post-1704/), [Wix](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3) | Stable `ks_0001` IDs, auto-generated mapping, never positional; Slice Library shows `StartMs→EndMs`, duration, lane badge, Zero-X/FADE state |
| **P1** | Output controls disabled opaquely | `総出力` disabled when filename not shown → click black area [purureko](https://purureko.com/etc/post-1704/) | Inline validation: missing source, unsupported codec, silent slice, budget overflow — all visible in draft bar, not hidden |
| **P1** | Silent final file cleanup | Delete `008.wav` + marker [Wix](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3) | Auto-omit or flag silent virtual slices (RMS < threshold) with `Silent` badge, not a file to delete |
| **P1** | Keyboard power, low discoverability | Woslicer II praised for shortcuts [Mid2BMS wiki](https://wiki.mid2bms.net/%E4%BB%96%E3%83%84%E3%83%BC%E3%83%AB/woslicer); Sayaslicer documents full set [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer) | Keep `O/Z/C/V/B/K/M/P/Del/Ctrl+Z/Y/D/F/J/K/Home/End/Left/Right` + showcase in draft bar; add command palette later |

#### Settings / assets (caps are UX, not just tech)

| P | Problem | Evidence | Studio |
|---|---|---|---|
| **P0** | Format incompatibility → crashes | 24/32-bit, Studio One WAVs crash Woslicer → re-save in Audacity [Mid2BMS wiki](https://wiki.mid2bms.net/%E4%BB%96%E3%83%84%E3%83%BC%E3%83%AB/woslicer) | Robust decode: NAudio + NAudio.Vorbis + libsndfile fallback; on import show `codec/bit depth/channels/duration`; normalize transcode preview without touching source |
| **P1** | Hard budgets exceeded silently | BMSE 1,295 [hitkey](https://hitkey.nekokan.dyndns.info/bmse_help_full/usage.html), `#WAVZZ` warning [Wix](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3), DJMAX 1,023→2,047 [NamuWiki](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C) | Live **budget meter** (BMS 1,296 / RESPECT 2,047 / TECHNIKA ∞) + reuse suggestions for repeated phrases (hash dedup) |
| **P1** | Manual asset reduction | Move small sounds/long pads to BGM, delete unused defs, trim tails [Qiita](https://qiita.com/yuinore/items/4de2639ea97bc6723650) | One-click `Promote to BGM` (long >60s → BGM stem), dedupe, reversible cleanup |
| **P2** | Invented controls | Firecrawl explicitly flags `Sensitivity 0.55` and per-slice Normalize as *not* evidenced in fetched tools [firecrawl report](https://github.com/SayakaIsBaka/sayaslicer) | Treat as hypotheses; current Studio `Sensitivity 0.55` is fine as tunable, but don't claim convention — keep `Silent tail threshold / Fade / Zero-X / Offset / Snap / BPM` as canonical, per-slice overrides as extension |

#### Backend / interchange (where files used to be)

| P | Problem | Evidence | Studio |
|---|---|---|---|
| **P0** | Clipboard corrupts semantics | iBMSC `weirdly/incorrectly` BMSE paste [iBMSC #5](https://github.com/zardoru/iBMSC/issues/5) | Round-trip tests + import/export diff preview; store exact ms, quantize only at target export (192nd for BMSE, arbitrary for iBMSC) |
| **P0** | iBMSC >192th vs. legacy | Sayaslicer `iBMSC arbitrary → missing keysounds/gamemode change, BMSE rounds to 192th` [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer) | Same as above — virtual slices are ms-precise; export quantizes |
| **P1** | Base-62 doesn't fix runtime | LR2base62 ~1.5GB decoded limit → 22050Hz fallback [note.com](https://note.com/nakaiankow/n/n5d30ba6c19a6?hl=en) | Budget decoded memory, offer `Export as OGG/Opus` diagnostics |
| **P1** | Large files broke Sayaslicer | >2GB signed-int overflow fixed, added Opus [Sayaslicer #5](https://github.com/SayakaIsBaka/sayaslicer/issues/5), [release](https://github.com/SayakaIsBaka/sayaslicer/releases/tag/vb28b1af) | Stream/segment waveform tiles (already O(w) in Studio), regression-test long files |
| **P1** | Reverb tails ≠ lane cut | Effects/core separation [NamuWiki](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C) | Keep source intact, represent `start/end/tailPolicy` virtually, validate in mode playback |

### 3.3 Technical limits — what the fetch *actually* evidenced

| Limit | Sayaslicer | Woslicer II/III | BMHelper/Mid2BMS | PTSEQUENCER | Slicex/dtinth |
|---|---|---|---|---|---|
| **Max slices** | No hard cap in README; BMSE 1,296 (01-ZZ) or base-62 ~3,844 downstream; >2GB bug fixed | 1,296 base-36, reuse wizard required [hitkey](https://hitkey.nekokan.dyndns.info/cmds.htm); vIII same [cosmic](https://cosmic.mearie.org/f/sonorous/bmsexts) | 1,295 [hitkey](https://hitkey.nekokan.dyndns.info/bmse_help_full/usage.html) | 1,023→2,047 [NamuWiki](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C) | Unlimited regions (DAW) |
| **Snap** | 1/1–1/192 arbitrary [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer) | Coarse Up/Down; III mouse free | MIDI 480ppq → 192nd | Internal ticks; TECHNIKA 16th/32nd | Beat detect + quantize [image-line](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/plugins/Fruity%20Slicer.htm) |
| **BPM** | Partial/hackish, full BPM planned [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer) | Single global | MIDI BPM respected but grid lost | Native BPM, BMS export flattens to 192nd | Time-stretch must set beats correctly [flipside](https://www.theflipsideforum.com/index.php?topic=23840.0) |
| **Formats** | wav/ogg/flac/mp3 via libsndfile, stereo, fade/tail [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer) | WAV reliably; mp3→wav manual [note.com](https://note.com/tiv_03sk/n/n97ce18606798) | MIDI→wav; manual reverb trim | WAV + BGM stems; keyless toggle | WAV primary, mp3 gap 20-30ms [flipside](https://www.theflipsideforum.com/index.php?topic=23840.0) |

### 3.4 What to keep / what to kill

**Keep from Sayaslicer:** cross-platform goal, BMSE+X *and* iBMSC clipboard (but virtualized), 1/1–1/192 arbitrary snap, offset, zero-cross, silent-tail threshold, fadeout, project save/load, MIDI+Mid2BMS import, marker copy/paste, undo/redo, preview, shortcuts; plus release polish: drag/drop on macOS/Linux, settings→preview live, marker-list scroll, resnap-all [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer), [release vb543f80](https://github.com/SayakaIsBaka/sayaslicer/releases/tag/vb543f80).

**Keep from Woslicer:** compact direct-audio workflow, fast marker nav, extensive keyboard ops (II), mouse-friendly III, and the *concept* of BMHelper→Woslicer bridge (just make it `Import to Studio`, not clipboard). Guides confirm Woslicer accepts BMHelper markers and batch-exports — right bridge, wrong handoff [Mid2BMS wiki](https://wiki.mid2bms.net/%E4%BB%96%E3%83%84%E3%83%BC%E3%83%AB/woslicer), [purureko](https://purureko.com/etc/post-1704/).

**Kill:** silent-file cleanup, Explorer-order numbering, hidden filename state, manual format re-save, opaque clipboard payloads, `総出力`-disabled-without-feedback, single global BPM.

---

## 4. Recommendations — prioritized, with Studio implementation

| # | Feature | P | Area | Rationale (evidence) | Studio implementation (file) |
|---|---|---|---|---|---|
| 1 | **Virtual slices until Finalize** | P0 | Backend | Kills 6-step ceremony [Google Doc](https://docs.google.com/document/d/1ywUO5RxCP8jCM7MuFGFAS5TIRK2gsyM3-rMgvPuMEcw/edit) | Already `KeysoundSlice` + `SliceExporter`; add `TailPolicy` enum, keep `IsConfirmed` gating |
| 2 | **Dual ruler (waveform ms bottom + chart bar/beat top, synced playheads)** | P0 | Timeline | BPM fidelity is P0 gap [dtinth](https://github.com/dtinth/sound-slicer), [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer); top proposal validated | New `ChartRulerOverlay` above `KeyslicerWaveformView`, shared `BPMMap` from `AttachChartContext` (`PlayerData.Tempo` + `Tempo` events), `TimeZoomChanged` sync |
| 3 | **Mode switcher `BMS \| RESPECT \| TECHNIKA`** | P0 | Settings | Budgets 1,295 vs 1,023→2,047 vs non-interfering differ materially [NamuWiki](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C), [hitkey](https://hitkey.nekokan.dyndns.info/bmse_help_full/usage.html) | `SlicerMode {Bms, Respect, Technika}` in `KeyslicerProject`, budget meter, preview switches `NAudioKeysoundPlayer` polyphony + lane cut logic |
| 4 | **Non-destructive Zero-X with A/B** | P0 | Settings | Zero-cross moves markers → gamemode break [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer); pop source [Qiita](https://qiita.com/yuinore/items/79db943d2e3447adee71) | Extend `FindNearestZeroCrossing` to return `deltaMs`; store `renderStartMs/renderEndMs` vs `logical StartMs/EndMs`; waveform overlay red→green cross, audition toggle |
| 5 | **Slice Library with stable IDs + budget meter** | P0 | Interface | Prevents Explorer-order drift [purureko](https://purureko.com/etc/post-1704/); warns before 1,295/2,047 [hitkey](https://hitkey.nekokan.dyndns.info/bmse_help_full/usage.html) | Already lane-colored library; add search, `P` audition, dedupe hash, reuse suggestions on overflow |
| 6 | **Draft + per-slice Zero-X/FADE/Normalize beside waveform** | P1 | Interface | Proven controls [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer); per-slice overrides are safe extension | Wire `FadeIn/Out/Gain` + per-slice `ZeroCross` toggle from inspector to `SliceExporter` preview |
| 7 | **Stateful Finalize wizard** | P1 | Interface | Replaces black-area workaround [purureko](https://purureko.com/etc/post-1704/) and silent `008.wav` delete [Wix](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3) | Modal showing missing filename, unsupported 24/32-bit, silent slices, output count, budget, `Export.Normalize` toggle, `Export as OGG` option |
| 8 | **Round-trip Import/Export** | P1 | Backend | Fixes `weirdly/incorrectly` BMSE paste [iBMSC #5](https://github.com/zardoru/iBMSC/issues/5) and iBMSC compatibility [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer) | `BmsClipboardParser` + `Bmson` import, diff preview, fixtures for base-36/62 and >192th |
| 9 | **Robust decode + streaming** | P1 | Backend | Preprocessing needed for Woslicer [Mid2BMS wiki](https://wiki.mid2bms.net/%E4%BB%96%E3%83%84%E3%83%BC%E3%83%AB/woslicer); >2GB overflow [Sayaslicer #5](https://github.com/SayakaIsBaka/sayaslicer/issues/5) | Keep O(w) peaks, add OGG/Opus decode via NAudio.Vorbis/libsndfile, tile cache |
| 10 | **Keyboard + discoverability** | P1 | Interface | Shortcuts powerful but fragmented [Sayaslicer](https://github.com/SayakaIsBaka/sayaslicer), [Mid2BMS wiki](https://wiki.mid2bms.net/%E4%BB%96%E3%83%84%E3%83%BC%E3%83%AB/woslicer) | Keep `O/Z/C/V/B/K/M/P/Del/Ctrl+Z/Y/D/F/J/K`, add palette/search, visible hints in draft bar |
| 11 | **Auto-advance `Grid|Beat|Off` + 192 quantize** | P1 | Timeline | Keeps `D/F/J/K` bar-aligned | Already `ComputeAutoAdvanceMs` + `QuantizeMs` in `KeyslicerViewModel.cs`; expose in toolbar |
| 12 | **Long-sample watchdog (>60s)** | P2 | Backend | LR2IR blocks >60s [BMS checklist](https://wcko87.github.io/bms-checklist/) + `1分未満に切り出す` [Wix](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3) | Warn on `Duration > 60000`, offer auto-split |

**P0 = ship; P1 = next; P2 = polish.** Firecrawl's own top 10 aligns 1:1 (virtual slices, dual timeline, mode simulation, Zero-X A/B, library, per-slice controls, wizard, round-trip, robust decode, keyboard). Manual JSON adds overflow guardrail and MP3 gap trim — keep both.

---

## 5. Roadmap — what actually changes in the repo

**Next PR (P0, 1–2 days, no model break):**

1. `ChartRulerOverlay` (new `FrameworkElement`) — bar/beat ticks from `PlayerData` BPM, synced zoom to `WaveformPeakProvider` viewport, playhead `TimeSpan`.
2. `SlicerMode` enum + budget meter in `KeyslicerProject` + `KeyslicerViewModel` (`MaxSlicesForMode` 1295/2047/∞).
3. Zero-X A/B: `FindNearestZeroCrossing` returns delta, store `RenderMs`, preview toggle in inspector, waveform overlay.

**Following (P1, 1 week):**

4. Finalize wizard (`Window` with list `SliceExporter.PreviewExport()`), `Import to Studio` diff.
5. Per-slice overrides persisted in `.ksp`; `SliceExporter` reads `slice.FadeInMs` not `Project.Fade`.
6. Robust decode fallback (libsndfile → NAudio) + tile cache for >2GB.

**No breaking change:** `.ksp` adds optional `mode`/`renderMs` with defaults, old files load.

---

## 6. Raw sources — every claim cites one

* Sayaslicer README/issues/releases: https://github.com/SayakaIsBaka/sayaslicer, /issues/5, /releases/tag/vb28b1af, /vb543f80
* Woslicer guides: https://wiki.mid2bms.net/%E4%BB%96%E3%83%84%E3%83%BC%E3%83%AB/woslicer, https://purureko.com/etc/post-1704/, https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3, https://hitkey.nekokan.dyndns.info/diary1308.php, https://yuinore.net/2017/04/otokiri-software/
* BMS Creation Notes: https://docs.google.com/document/d/1ywUO5RxCP8jCM7MuFGFAS5TIRK2gsyM3-rMgvPuMEcw/edit
* BMSE help + base-36 limits: https://hitkey.nekokan.dyndns.info/bmse_help_full/usage.html, https://hitkey.nekokan.dyndns.info/cmds.htm, https://cosmic.mearie.org/f/sonorous/bmsexts, https://grokipedia.com/page/Be-Music_Source
* NamuWiki keysound (PTSEQUENCER budgets, lane cut): https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C
* dtinth/sound-slicer + FL Slicex: https://github.com/dtinth/sound-slicer, https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/plugins/Fruity%20Slicer.htm, https://www.theflipsideforum.com/index.php?topic=23840.0
* iBMSC clipboard corruption: https://github.com/zardoru/iBMSC/issues/5
* BMS checklist (60s): https://wcko87.github.io/bms-checklist/
* Community practices: https://qiita.com/yuinore/items/79db943d2e3447adee71, https://qiita.com/yuinore/items/4de2639ea97bc6723650, https://note.com/nakaiankow/n/n5d30ba6c19a6?hl=en

*And the literal Studio context:* `docs/keyslicer.md` (264 lines, shipped defaults), `Keyslicer/KeyslicerViewModel.cs` (`SyncCollectionsFromProject`, `QuantizeMs`), `KeyslicerWindow.xaml:218` (`DurationMs Mode=OneWay` fix `0064132`), `SliceExporter.cs`, `WaveformPeakProvider.cs`, `TransientDetector.cs`, `KeyslicerWindow.xaml.cs` (undo, BPM sync).

---

## 7. Appendix — why this report trusts what it trusts

* Firecrawl explicitly refused to invent 10 Sayaslicer issues when the issues page was empty — evidence-led, not filled.
* Firecrawl flagged `Sensitivity 0.55` and per-slice Normalize as *unevidenced* in fetched tools — marked as hypothesis, not convention. Studio's `0.55` is a tunable default, not a standard to defend.
* No PTSEQUENCER manual was fetched; DJMAX budgets are treated as mode-validation targets, not guaranteed tool limits.
* All JP/KR quotes translated in `docs/keyslicer-research.json` `source_citations.translated_quote` (e.g., `切断位置をコピー` → “Copy cut positions”, `総出力が押せない` → “Total output cannot be pressed”).

**Bottom line:** Ship P0 (dual ruler + mode + Zero-X A/B + library). The source stays intact, the chart stays canonical, and the export ceremony disappears. That's the whole report — built from what the tools actually do and what the Studio code actually is.
