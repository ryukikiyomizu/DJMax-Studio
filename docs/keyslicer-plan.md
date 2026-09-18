# DJMax Studio — Keyslicer Implementation Plan
*From audit to shippable: dual ruler + mode switcher + virtual slices, no ceremony*

**Branch:** `arena/01a0b1fa-djmax-studio` · **Report:** `docs/keyslicer-research-report.md` · **Date:** 2026-09-18  
**Principle:** Source stays intact; slices are virtual until Finalize. Chart time (BPM) and source time (ms) never conflate.

---

## 1. Goal & success criteria

**Goal:** Replace the 6-step BMS export ceremony (DAW → BMHelper → Woslicer → hundreds of wavs → BMSE → clipboard) with a single-project, non-destructive slicer inside Studio that *feels* like Woslicer speed but *behaves* like a modern DAW.

**Ship criteria (P0):**

* [ ] Drag → `D/F/J/K` → slice + note in one gesture, with grid-quantized time
* [ ] Dual ruler visible: waveform ms bottom + chart bar/beat top, synced playheads + zoom
* [ ] `BMS | RESPECT | TECHNIKA` switcher changes budget meter + preview polyphony
* [ ] Zero-X is non-destructive with A/B audition and timing delta
* [ ] Slice Library shows stable `ks_0001` IDs, never Explorer order, with budget meter
* [ ] Finalize is one wizard; no `総出力が押せない` or silent `008.wav` surprises
* [ ] All existing tests + new round-trip fixtures pass; build+smoke green on Windows

**Measure:** Time-to-first-slice < 15s from import; time-to-finalize < 30s for 50 slices; zero silent-file cleanups in user testing.

---

## 2. Scope — P0 / P1 / P2

| P | Feature | Area | Why now |
|---|---|---|---|
| **P0** | Dual ruler overlay | Timeline | Fixes P0 BPM/grid gap — top reason for `ズレ譜面` drift |
| **P0** | Mode switcher + budget meter | Settings | Prevents 1,295/1,023/2,047 cap surprises; TECHNIKA non-interfering vs. BMS cut |
| **P0** | Zero-X A/B (non-destructive) | Settings | P0 click vs. timing break; most cited Sayaslicer warning |
| **P0** | Slice Library stable IDs + search + audition | Interface | Fixes P0 positional numbering |
| **P0** | Finalize wizard (inline validation) | Interface | Fixes P0 ceremony + P1 disabled `総出力` |
| **P1** | Per-slice FADE/Zero-X/Normalize overrides | Interface | Proven controls, safe extension |
| **P1** | Round-trip import (BMS/BMSE/iBMSC + Studio) | Backend | Fixes P0 clipboard corruption |
| **P1** | Robust decode + streaming (OGG/Opus, 24/32-bit, >2GB) | Backend | Prevents Woslicer crash + Sayaslicer >2GB bug |
| **P1** | Keyboard palette + discoverability | Interface | Keeps power, adds recovery |
| **P1** | Auto-advance `Grid|Beat|Off` + 192 quantize exposure | Timeline | Keeps `D/F/J/K` bar-aligned |
| **P2** | Long-sample watchdog (>60s) | Backend | LR2IR 60s block |
| **P2** | Spectral view, MIDI import, recording mode | — | Next quarter |

**Out of scope for this plan:** spectral, MIDI renamer, live recording, shared libraries (listed in `docs/keyslicer.md` as natural next steps).

---

## 3. Architecture — no model break

```
KeyslicerProject.cs  → add: SlicerMode {Bms, Respect, Technika}, RenderMs?
KeysoundSlice.cs     → add: RenderStartMs/RenderEndMs?, PerSliceFade?, PerSliceZeroCross?
KeyslicerViewModel.cs → add: ChartRulerModel, BudgetModel, ZeroCrossAB, SlicerMode
KeyslicerWindow.xaml  → add: ChartRulerOverlay (top), Mode Switcher, Budget Meter, Zero-X A/B
KeyslicerWaveformView.cs → add: Zero-X overlay (red→green), Ruler sync
SliceExporter.cs     → read per-slice renderMs if present, else logical
NAudioKeysoundPlayer.cs → mode-aware polyphony (cut vs. non-interfering)
```

`.ksp` adds optional `mode` (default `Bms` for compat) + `renderStartMs/renderEndMs` per slice. Old files load with defaults — no migration.

---

## 4. Detailed tasks

### Phase 0 — Foundations (0.5 day)

| # | Task | Files | Acceptance |
|---|---|---|---|
| 0.1 | Add `SlicerMode` enum + `BudgetModel` (max 1295/2047/∞) + `MaxSlicesForMode` helper | `KeyslicerProject.cs`, `KeyslicerViewModel.cs` | Unit test: `Respect` → 2047, `Bms` →1295, `Technika` ∞ |
| 0.2 | Extend `KeyslicerProject` with `SlicerMode` + per-slice `RenderMs` (nullable) with JSON compat | `KeyslicerProject.cs`, `KeysoundSlice.cs` | Old `.ksp` opens, new saves round-trip |

### Phase 1 — P0 Dual Ruler (1 day, parallelizable)

| # | Task | Files | Acceptance |
|---|---|---|---|
| 1.1 | Create `ChartRulerOverlay : FrameworkElement` — draws bar/beat ticks from `PlayerData` BPM map (`Tempo` + `Tempo` events) | `Keyslicer/ChartRulerOverlay.cs` (new) | Ruler ticks align to chart `VirtualTick` → `Ms` via `QuantizeMs`; zoom sync via `TimeZoomChanged` |
| 1.2 | Wire ruler to `KeyslicerViewModel.AttachChartContext` — share `BPMMap`, sync `TimeZoom` + playheads | `KeyslicerViewModel.cs`, `KeyslicerWindow.xaml` (add ruler above waveform), `.xaml.cs` (sync scroll) | Scrub waveform → chart playhead moves; scrub chart → waveform updates; no drift at 120/180 BPM switch |
| 1.3 | Grid-quantize draft preview (ghost shows snapped position) | `KeyslicerWaveformView.cs`, `KeyslicerViewModel.cs` (`QuantizeMs`) | Drag at 1/16 → ghost snaps to 16th, not free ms |

**Depends:** 0.1

### Phase 2 — P0 Mode Switcher + Budget Meter (0.5 day)

| # | Task | Files | Acceptance |
|---|---|---|---|
| 2.1 | Toolbar switcher `BMS | RESPECT | TECHNIKA` (radio) + budget meter `78/1295` + overflow warning | `KeyslicerWindow.xaml` (toolbar), `KeyslicerViewModel.cs` (`SlicerMode`, `BudgetModel`) | Switch to RESPECT → meter shows 78/2047; exceed → red + reuse suggestions |
| 2.2 | Preview polyphony switch (BMS cut vs. TECHNIKA non-interfering) | `KeyslicerWindow.xaml.cs` (audition path), `NAudioKeysoundPlayer` (branch) | In BMS, rapid same-lane triggers cut tail; in TECHNIKA, they layer |
| 2.3 | Persist mode in `.ksp` | `KeyslicerProject.cs` | Reopen project → mode restored |

**Depends:** 0.1

### Phase 3 — P0 Zero-X A/B (0.5 day)

| # | Task | Files | Acceptance |
|---|---|---|---|
| 3.1 | `FindNearestZeroCrossing` returns `(renderMs, deltaMs)`; store `RenderStartMs/RenderEndMs` vs. logical `StartMs/EndMs` | `KeyslicerViewModel.cs`, `WaveformPeakProvider.cs` | `Zero-X` toggle shows delta `+1.2ms` in inspector; logical time unchanged for chart |
| 3.2 | Waveform overlay: red cross (logical) → green cross (render) + `Zero-X` toggle per draft + per slice | `KeyslicerWaveformView.cs`, `KeyslicerWindow.xaml` | Visual A/B; audition button toggles `logical` vs `render` |
| 3.3 | Inspector A/B audition | `KeyslicerWindow.xaml.cs` (temp WAV render) | Click `A/B` → hear click/no-click |

**Depends:** none (extends existing `SnapToZeroCrossing` 2.5ms nudge)

### Phase 4 — P0 Slice Library + Finalize Wizard (1 day)

| # | Task | Files | Acceptance |
|---|---|---|---|
| 4.1 | Library upgrades: search, `P` audition, `Del` delete, `Zero-X/FADE` badges, `Lane D/F/J/K` hint, duration, stable `ks_0001` sort by `StartMs` | `KeyslicerWindow.xaml` (library ListView), `KeyslicerViewModel.cs` | No Explorer order; duplicate hash flagged |
| 4.2 | Dedupe hash (perceptual or PCM hash) + reuse suggestions on overflow | `KeyslicerViewModel.cs` (new `SliceDedupe`) | At 1296+1, UI suggests reuse of identical phrase |
| 4.3 | Finalize wizard modal: validates missing source, unsupported 24/32-bit, silent slices (RMS<threshold), output count, budget; shows `Export.Normalize` + `Export as OGG` | `Keyslicer/FinalizeWizardWindow.xaml` (new) + `SliceExporter.cs` (`PreviewExport()`) | `総出力` disabled state never happens — wizard shows inline error, not black-area workaround |

**Depends:** 0.1, 2.1

### Phase 5 — P1 Per-slice overrides + Auto-advance polish (0.5 day)

| # | Task | Files | Acceptance |
|---|---|---|---|
| 5.1 | Move `FadeIn/Out/Gain` from project to per-slice (keep project defaults as fallback) | `KeysoundSlice.cs`, `KeyslicerViewModel.cs`, `SliceExporter.cs` | Per-slice ` FADE 2/5` badges; export respects slice values |
| 5.2 | Expose `SNAP Free/1/4/1/8/1/16/1/32 (+1/64/1/192 later)` + `Advance Grid|Beat|Off` in toolbar/draft bar, persist | `KeyslicerWindow.xaml`, `KeyslicerViewModel.cs` (`SnapDenominator`, `AutoAdvanceMode`) | `Grid` steps one snapped unit via `ComputeAutoAdvanceMs` |

**Depends:** none

### Phase 6 — P1 Round-trip + Robust decode (1 day)

| # | Task | Files | Acceptance |
|---|---|---|---|
| 6.1 | BMS/BMSE/iBMSC clipboard parser + import diff preview | `Keyslicer/BmsClipboardParser.cs` (new) | Paste `V` clipboard → preview diff, no `weirdly` corruption |
| 6.2 | Robust decode: libsndfile → NAudio fallback, report `codec/bitDepth/channels`, tile cache for >2GB | `WaveformPeakProvider.cs`, `KeyslicerViewModel.cs` | 24/32-bit + OGG/Opus load; >2GB streams, not `signed-int` load-all |
| 6.3 | Fixtures: base-36/62 + >192th round-trip | `test-fixtures/keyslicer/*` | CI checks |

### Phase 7 — P1 Keyboard palette (0.5 day)

| # | Task | Files | Acceptance |
|---|---|---|---|
| 7.1 | Keep `O/Z/C/V/B/K/M/P/Del/Ctrl+Z/Y/D/F/J/K/Home/End/Left/Right` + palette/search + hints | `KeyslicerWindow.xaml.cs` (input bindings), `KeyslicerWindow.xaml` (hints) | No retraining; discoverability + |

---

## 5. Timeline & sequencing

```
Week 1 (P0 ship):
 Mon  Phase 0 (0.5d) + Phase 1.1-1.2 (1d) ── parallel ── Phase 2 (0.5d) + Phase 3 (0.5d)
 Tue  Phase 1.3 + Phase 4 (Library+Wizard) (1d)
 Wed  Integration + manual QA (30-min session, 2-min chart, 50 slices, all 3 modes)
 Thu  Build+smoke on Windows, fix XAML thickness/ModeOneWay if needed, ship on arena/*
Week 2 (P1 polish):
 Mon-Tue  Phase 5 + 6 (per-slice + decode)
 Wed  Phase 7 + fixtures
 Thu  User testing + finalize
```

**Critical path:** Dual ruler (1.1) → Library (4.1) → Wizard (4.3) → Round-trip (6.1). Mode switcher and Zero-X are parallel.

**Estimates:** P0 = 3 dev-days; P1 = 2.5 dev-days; P2 = backlog.

---

## 6. Testing strategy

* **Existing:** `build` (Debug+Release) + `smoke` (BMS/bmson/techmania round-trip) must stay green — no Windows Desktop pack change.
* **New unit:** `BudgetModel` (caps), `QuantizeMs` (offset+192nd), `ZeroCross AB` (delta), `SliceDedupe` (hash), `BmsClipboardParser` (base-36/62).
* **New integration:** `ChartRulerOverlay` vs `BPMMap` (120→180→120), `FinalizeWizard` with missing source + silent slice + 1296+1 overflow, `Import to Studio` undo group.
* **Manual:** 30-min WAV (44.1kHz), 152s demo `New_Project (2).mp3` from screenshot, plus OGG/Opus + 24-bit WAV; test all `Advance` modes.

---

## 7. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Ruler drift (ms vs. ticks) | Single source of truth: `QuantizeMs` + `BPMMap` from `PlayerData`; ruler and waveform share `TimeZoom` |
| Zero-X breaks iBMSC | Store exact ms, render vs. logical split; export quantizes only at Finalize |
| Git push from Actions fails | Already proven `ad1bf1b` push works; use `contents: write` + `ad1bf1b`-style `git push origin HEAD:arena/...` |
| Woslicer users expect `総出力` | Keep `Export WAVs` + `Send to Editor` familiar; wizard explains inline |
| Large file >2GB regression | Streamed peaks + tile cache, not full decode; regression fixture |

---

## 8. What I need from you (free to answer, but plan proceeds with best defaults if not)

I’ll proceed with these defaults unless you say otherwise — these are the “whatever is best” choices:

* Mode default **BMS** (1,295) for new projects; RESPECT 2,047 via switcher; TECHNIKA ∞ — correct?
* Snap defaults **Free/1/4/1/8/1/16/1/32** exposed, with 1/64+1/192 as future combo items — enough, or expose 1/192 now?
* Advance default **Grid** (one snapped step) — keep `Beat`+`Off` as options?
* Lanes for `D/F/J/K` — keep 4-lane (Studio default) for `D/F/J/K`, or need 5B/6B/8B mapping (`D/F/J/K` + extra)?
* Finalize default **WAV**, with `Export as OGG/Opus` toggle in wizard — or default to OGG?

Answer any, or say “keep defaults” and I’ll ship P0.

---

## 9. Files & commits — what lands where

```
docs/keyslicer-research-report.md      # audit (this plan’s input, 30K, becdf39)
docs/keyslicer-plan.md                 # this file
docs/keyslicer-research.json           # structured Zod (62K, 6 audits, ZOD OK)
docs/keyslicer-research-firecrawl.md   # Firecrawl markdown (19.5k, ad1bf1b)
scripts/firecrawl-agent.mjs            # spark-2 agent (633aa55)
.github/workflows/firecrawl-research.yml # push+dispatch, secrets fallback (c3e1d32)
.github/workflows/build.yml            # untouched stable (622 lines)

Next commits (P0):
 DJMaxEditor.Studio/Keyslicer/ChartRulerOverlay.cs (new)
 DJMaxEditor.Studio/Keyslicer/KeyslicerViewModel.cs (mode/budget/zeroAB)
 DJMaxEditor.Studio/Keyslicer/KeyslicerWindow.xaml(+.cs) (ruler/mode/meter/wizard)
 DJMaxEditor.Studio/Keyslicer/FinalizeWizardWindow.xaml (new)
```

All on `arena/01a0b1fa-djmax-studio`, build+smoke gated, no history rewrite.

---

## 10. Immediate next step

If “keep defaults” → I start **Phase 1 Dual Ruler** (ChartRulerOverlay) + **Phase 2 Mode Switcher** in parallel — first code in ~1h, green build. If you have answers, I’ll incorporate before coding.
