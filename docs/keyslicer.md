# Keysound Slicer Scene

> A new, dedicated scene in DJMax Studio for non-destructive keysound authoring — inspired by the BMS slicers (Woslicer / Woslicer II, Sayaslicer, Sound Slicer) but bridged directly into the chart editor.

## Why a separate scene?

Classic BMS workflow:

1. Cut a long recording into hundreds of `.wav`/`.ogg` files *first* (Woslicer/sayaslicer, or Audacity/Reaper + manual slicing).
2. Export those files.
3. Import them into a chart editor (iBMSC/BMSE/etc).
4. Place notes that reference the exported files.

The handoff between step 1 and step 2 is the tedium: hundreds of files, manual naming, import dialogs, clipboard pastes.

DJMax Studio already owns the chart timeline (BPM map, snap, lanes, undo, playback). What it lacked was a **keysound asset authoring layer** that keeps the original recording intact and only materializes files at export.

## Core idea

> **A slice is a virtual keysound first, and an exported audio file only when needed.**

```jsonc
// a virtual slice — just a view into the source recording
{
  "id": "ks_0042",
  "sourceFile": "sources/session_keyboard.wav",
  "startMs": 12843.5,
  "endMs": 13022.8,
  "gain": 1.0,
  "fadeInMs": 2,
  "fadeOutMs": 5,
  "lane": 2,
  "label": "snare"
}

// a placed note — chart time × lane × slice reference
{
  "chartTimeMs": 32250,
  "virtualTick": 18560,
  "lane": 2,
  "keysoundId": "ks_0042"
}
```

During editing the engine plays `sourceFile[startMs:endMs)` with fades/gain. The source stays untouched. Only on **Export** are the intervals rendered to `ks_0001.wav`, `ks_0002.wav`, … and a Techmania `track.tech` is written that points at them. No temporary files, no manual import.

## Two timelines, strictly separated

| Timeline | Domain | Example |
|---|---|---|
| **Chart/song timeline** | BPM-measured, where notes live | note at `01:25.500`, lane 3 |
| **Source timeline** | Plain milliseconds inside the raw recording | slice `12.843s–13.023s` of `session.wav` |

A 30-minute session can drive a 2-minute chart; chart time `45s` may reference source time `11m32s`. They never need to align.

The UI keeps them visually distinct: the **waveform panel** (top) *is* the source timeline; the **chart timeline** (middle, the existing StudioVerticalCanvas) *is* the song timeline. Slices live in the library between them and bridge the two.

## Where the idea comes from — research notes

### Woslicer / Woslicer II (wosderge)

- Waveform-oriented slicer. Load a long WAV, drag cut points, audition with `P`, batch-export.
- Outputs: sliced WAVs + a BMS clipboard (`V` / BMSE clipboard, `Shift+V` for iBMSC) that pastes pre-placed notes into BMSE/BMHelper.
- Strength: purpose-built for BMS, tiny and fast. Weakness: the clipboard handoff *is* the workflow — no shared project, no lane tagging, no live chart preview.

### Sayaslicer (SayakaIsBaka, successor to Woslicer II)

- Cross-platform (Dear ImGui + libsndfile), drop-in replacement for Woslicer II with fixes: zero-crossing, silence tail remover, fadeout, arbitrary snapping (1/1–1/192), base-62, MIDI-marker import, Mid2BMS renamer, batch export.
- Still a *separate app* from the editor; the bridge is still a clipboard paste.

### Sound Slicer (dtinth)

- Reads `synth.txt` + `synth.wav`, slices, writes a BMSE clipboard. Two MIDI modes: *Rhythmic* (one slice per event) and *Melodic* (deduplicates identical notes so 500 bass notes → 10 slices). No preview, no lane assignment — purely offline.

### Classic combo: Audacity/Reaper + BMSE / iBMSC

- Most flexible (silence/transient detection, spectral view, batch export, manual cleanup) but also most ceremony: silence detection in one app, export in another, import in BMSE, placement by hand.

### What the new scene keeps / drops

All of the above assume “export files *now*”. The slicer scene inverts it: export files *later*. Detection, silence trimming, fades, zero-crossing — all still matter, but they produce **suggestions** (ghost regions on the waveform) that the user accepts/rejects with one key. The table at the bottom of the window *is* the note list; accepting a suggestion can optionally bump the chart playhead so `D/F/J/K` can walk through a song at speed.

## The new scene (MVP)

Entry: **Scissors** button in the main toolbar → non-modal `KeyslicerWindow` (same dark theme, same palettes as the studio). Single-instance: two slicers would be two copies of the same 100 MB waveform.

### Layout

```
┌─ Toolbar (project: New/Open/Save, Export WAVs / Export .tech / Send to Editor, BPM/Offset/SNAP/Zoom, Undo/Redo) ─┐
├──────────┬──────────────────────────────────────┬──────────┤
│ Sources  │  Waveform header (source name,      │ Inspector│
│ (long    │   playhead, chart playhead, play/   │ (slice   │
│  takes)  │   stop)                             │  props)  │
│─────────│──────────────────────────────────────│          │
│ Slice    │  WaveformView (peaks, lane strip,   │          │
│ Library  │   draft yellow, suggestions ghost,  │          │
│ (lane-   │   playhead)  ← Ctrl/Alt+wheel zoom  │          │
│  colored)│   wheel scroll, drag to draft       │          │
│─────────│──────────────────────────────────────│          │
│ Auto-   │  Draft bar (Make slice / Clear, lane │          │
│ Detect  │   D/F/J/K, fades, auto-place)        │          │
│ (sens., │──────────────────────────────────────│          │
│  Detect/ │  Chart timeline preview (placed      │          │
│  Accept) │   notes list — time × lane × slice) │          │
├──────────┴──────────────────────────────────────┴──────────┤
│ Status bar (status, zoom)                                   │
└─────────────────────────────────────────────────────────────┘
```

* Left dock: source list (import long takes), slice library (confirmed regions, lane colors), detection strip.
* Center top: large waveform + header (source info, audition, chart playhead). The waveform *is* the source timeline; the lane strip above it *is* the library.
* Center middle: draft bar (fades, lane shortcuts, auto-place toggle) + placed-notes list (chart timeline).
* Right: inspector for the selected slice (id, label, bounds, gain, lane, source, export preview) + workflow hints.

### Interaction contract

* **Draft** = yellow, transient. Drag on the waveform → draft. Not a slice, not a note.
* **Confirmed slice** = lane-colored chip in the strip + row in the library. Created by **Make slice** or by pressing a lane key while a draft exists.
* **Note** = chart-timeline entry. Created by `Send to Editor` / `Place note` or automatically when `Auto-place` is on and the draft is confirmed via a lane key — the slice appears *and* a note is dropped at `chartPlayheadMs` on that lane, then `chartPlayheadMs` advances one beat so the next `F` lands on the next beat.

So the defining gesture is:

> **Drag a sound → press `D/F/J/K` → slice + note appear.**

Batch path: `Detect` → `MaxSignal` ghosts → `[` / `]` to walk → `Enter` to accept → `All` to accept the rest.

### Chosen defaults — the "work it out" decisions

The spec left several trade-offs open ("pick what's best"). The shipped defaults are:

* **Auto-advance = Grid (not bare beat, not off).** After an `Auto-place` note, the chart playhead steps by one *snapped* grid unit (`beat × 4 / snapDenom`; at 1/16 that's a 16th) and is re-quantized so `D-F-J-K` walks the chart in time without drifting. `Beat` and `Off` are still in the combo for free-form charting or manual stepping, but `Grid` is the stock choice because the chart's grid — not wall time — is what players hear. The slicer also quantizes every placed note's `ChartTimeMs` via `QuantizeMs(offset + round((ms-offset)/step)×step)` before deriving `VirtualTick`, so a hand-placed note never lands micro-off because the source had human timing.

* **BPM/offset sync = follows the open chart.** `AttachChartContext` reads `PlayerData.Tempo` (and the first `Tempo` event if present) and auto-fills `BPM`/`Offset` so a note at tick X in the slicer lands at the same millisecond in the editor without the user retyping numbers. The slicer stays `IsDirty=false` after an auto-sync — saving remains explicit. Users who slice without a chart still type BPM/Offset by hand.

* **Snap = 1/16, Free/1/4/1/8/1/16/1/32 in the toolbar + same denominator persisted in the `.ksp`.** Woslicer-family tools expose 1/1–1/192; DJMax charts live mostly on 1/4–1/32, so 1/16 as default, five choices in the UI now, and the engine already supports 1/64 and 1/192 (add a combo item and `SnapDenomFromIndex` maps it — no model change).

* **Zero-crossing = on by default, ~2.5 ms nudge.** Each new slice's `StartMs`/`EndMs` is nudged to the nearest zero-crossing found in `Waveform.ZoomSamples` inside a 2.5 ms window before the slice is committed. The nudge is mechanical, not ML, so it never second-guesses the user's ear — and it stays off when `SnapToZeroCrossing=false` (uncheck `Zero-X` in the draft bar). This mirrors Sayaslicer/Woslicer zero-crossing without requiring the user to hunt it per slice.

* **Normalize = toggle, off by default, respected everywhere.** `Export.WAVs` and `Export .tech` now read `Project.Export.Normalize`; when checked, `SliceExporter` peak-normalizes each rendered slice to 0.98 before writing, so a soft piano take and a slammed snare don't fight for headroom — but untreated dynamics remain available for users who want them.

* **Undo = slicer-local, per action.** `Ctrl+Z` / `Ctrl+Y` (and the toolbar arrows) walk a slicer undo stack for `AddSlice`/`RemoveSlice`; the stack survives until close or the next slicer-initiated action. This matches the editor's Undo idiom without entangling the chart's `UndoManager` — slices are non-destructive virtual assets, so undo is just re-insert/remove in `Slices`.

* **Export format = WAV always; OGG still available via `SliceExporter.ExportFormat`.** The UI button says "Export Wavs" — the research expectation (hundreds of `.wav`s for BMS/TECHMANIA) — but the exporter already writes either container, and the filename stem stays `ks_0001` so a later "export as OGG" switch would be a one-line combo change. No need to ask the user at MVP time.

None of these add a dialog. They all live as properties on the project (`SnapDenominator`, `AutoAdvanceMode=grid|beat|off`, `SnapToZeroCrossing`, `Export.Normalize`) so an opened `.ksp` restores the exact feel it was saved with.

### Slice model

```csharp
class KeysoundSlice {
  string Id;                // ks_0001
  string SourceFile;        // stored (relative when portable)
  string ResolvedSourcePath;// absolute (runtime)
  double StartMs, EndMs;
  double Gain;              // 0.1–2.0
  double FadeInMs, FadeOutMs;
  int    Lane;              // -1 = library-only, 0–7 = chart lane
  string Label;             // "kick", "C4", …
  bool   IsConfirmed;
}
class KeysoundMappedNote {
  double ChartTimeMs;       // song time
  int    VirtualTick;       // derived for the model (6 × native tick)
  int    Lane;
  string KeysoundId;        // → KeysoundSlice.Id
}
```

The project file (`*.ksp`, JSON) stores sources + slices + notes + BPM/offset + view state + `NextIdCounter`. `Save (portable)` copies the original source files into `sources/` next to the project so the `.ksp` is self-contained. Hash/size are stored for moved-file detection.

### Waveform + peaks

Decoded mono peaks are built off the UI thread (peak provider reads via NAudio/NAudio.Vorbis, downmixes to mono, caps hour-long sessions by downsampling, and produces per-pixel `{min,max,rms}` for the current viewport width). The wave view then draws `w` vertical lines O(w), not O(samples). Cursors, grid, and a 1 kHz-appropriate time ruler go with it. Zoom is centered on the cursor; scroll is clamped to `[0, duration-visible]`.

### Detection

A flux-based onset detector runs on the peak array (no native FFT needed):

* amplitude exceeds a sensitivity-mapped threshold above a short background window,
* is a local max over ±1 px,
* is debounced by `MinGapMs` (default 80 ms),
* end is the next silence run (≈30 ms below `SilenceThreshold`) or `MaxDurationMs`, minus `TailMs`, clipped to the next onset's pre-roll.

Produced as `SuggestedSlice { startMs, endMs, confidence }` — green ghosts on the waveform. `[` / `]` walks, `Enter` / `Accept` makes a confirmed slice.

Settings (with shipped defaults): `Sensitivity 0.55`, `MinGap 80 ms`, `Silence 0.02`, `PreRoll 6 ms`, `Tail 30 ms`, `MaxDuration 4000 ms`. All are in `TransientDetectorSettings`.

### Playback / audition

The main chart's `NAudioKeysoundPlayer` keeps playing the chart; the slicer has a lightweight preview path that renders a draft/selected interval to a temp WAV in `Path.GetTempPath()/djmax_slicer_audition_*` and plays it on a disposable `WaveOutEvent`. The temp file and output are deleted on `PlaybackStopped`. Failures are silent — audition is convenience, not part of the export path.

### Export

* **Export WAVs…** — folder picker → `SliceExporter.Export(..., ExportFormat.Wav, exportOnlyUsed: false)` → `ks_0001.wav`, `ks_0002.wav`, … ordered by `StartMs`. Each render seeks to `StartMs`, reads `EndMs-StartMs`, applies per-slice linear fades per-frame and `Gain`, optionally normalizes to 0.98. Missing sources are skipped but logged.
* **Export .tech…** — writes `keysounds/` beside the chosen `track.tech`, calls the slicer for the audio, then builds a minimal `PlayerData` (8 lane tracks + tempo track) and round-trips it through `TechmaniaChartSerializer.Serialize`. Volume/pan defaults are model-default (127/64) → TECHMANIA defaults (100/0) is handled by the serializer. Only slices referenced by at least one `KeysoundMappedNote` are emitted when `exportOnlyUsed` is true (the default for `.tech`).
* **Send to Editor** — when a chart is open in the main window, notes are injected into its `PlayerData.Tracks` as new `EventData` (`Attribute=0`, `VirtualDuration=36`) on the slice's lane, with a new `InstrumentData` `ks_xxxx.wav` each. The user then re-saves the chart and copies the exported `ks_*.wav` beside it — the chart will play because `KeysoundDecoder.ResolvePath` probes `name+.ogg/.wav`, `name`, and extension-swapped fallbacks.

### Keyboard map (slicer-focused)

| Key | Action |
|---|---|
| Drag on waveform | make/extend yellow draft |
| `D` `F` `J` `K` | create slice on lane 1–4 from draft + place note at chart playhead (if Auto-place on) — playhead then grid-advances |
| `Enter` | if suggestions exist: Accept current suggestion; else Make slice from draft |
| `Space` / `P` | audition draft / selected slice / playhead |
| `Esc` | clear draft or stop preview |
| `Del` | delete selected slice |
| `[` `]` | prev / next suggestion |
| `Ctrl+Z` / `Ctrl+Y` | Undo / Redo slice add/remove (toolbar arrows do the same) |
| `Ctrl`/`Alt` + wheel | zoom centered on cursor |
| wheel / `Shift`+wheel | scroll (4× with Shift) |
| Double-click | seek playhead or audition slice |
| `SNAP` (toolbar combo) | `Free` / `1/4` / `1/8` / `1/16` / `1/32` — grid for new notes + `Quantize` target; persisted as `SnapDenominator` in the `.ksp` |
| `Advance` (draft bar) | `Grid` (one snapped step) / `Beat` / `Off` — how far the chart playhead steps after each auto-placed note |
| `Zero-X` (draft bar) | nudges new slice edges to the nearest zero-crossing (~2.5 ms) to shave clicks; toggle in draft bar |
| `Normalize` (project box) | when checked, `Export Wavs` / `Export .tech` peak-normalize every slice to 0.98 on render |

## Reuse from DJMax Studio

The slicer leans on the shell it lives in:

* `KeysoundDecoder` probe order (`.ogg` → `.wav` → exact → extension-swapped) for audition/export sanity.
* `NAudioKeysoundPlayer` / `NAudioDeviceOutput` as the reference audio graph (audition uses a throwaway output so the chart's graph is untouched).
* `TechmaniaChartSerializer` / `TechmaniaChartWriter` as the only honest `.tech` writer — the slicer builds a `PlayerData` and lets the shared serializer speak.
* `StudioChartTheme` / `Palette.xaml` for colors (slice chips reuse lane colors; draft is amber `FFD020`).
* `DocumentCapabilities` / `EditorDocumentContext` for the `Send to Editor` guard.

Everything *new* is under `DJMaxEditor.Studio/Keyslicer/`:

* `KeysoundSlice.cs`, `KeysoundSource.cs`, `KeyslicerProject.cs` — model + portable JSON persistence.
* `WaveformPeakProvider.cs` — per-pixel peaks, bounded decode, `CancellationToken`-aware.
* `TransientDetector.cs` — flux + silence + debounce, configurable.
* `SliceExporter.cs` — interval render with fades/gain/normalize.
* `KeyslicerWaveformView.cs` — horizontal peak view (FrameworkElement, no GDI+).
* `KeyslicerViewModel.cs` — project + waveform + suggestions + draft + notes; the single source of truth the view and the inspector bind to.
* `KeyslicerWindow.xaml` / `.xaml.cs` — the scene itself.

## Out of scope for this patch, natural next steps

* Spectral view + zero-crossing nudges.
* MIDI import (Mid2BMS-style renamer + marker table) — same virtual-slice output.
* Multi-source lane-color tagging / batch labeling / duplicate detection (identical slices).
* Live recording mode (play along, quantized capture) — the spec's Workflow C.
* BMS `BMSE`/`iBMSC` clipboard export for shops that still round-trip through BMS.
* Shared/reusable slice libraries (import a `.ksp` as a library into another project).

## File map

```
DJMaxEditor.Studio/Keyslicer/
  KeysoundSlice.cs          // virtual keysound interval
  KeysoundSource.cs         // source recording ref + hash
  KeyslicerProject.cs       // *.ksp project model (portable save)
  WaveformPeakProvider.cs   // peak cache
  TransientDetector.cs      // onset suggestions
  SliceExporter.cs          // interval → wav/ogg
  KeyslicerWaveformView.cs  // peak canvas
  KeyslicerViewModel.cs     // state
  KeyslicerWindow.xaml[.cs] // scene

docs/keyslicer.md           // this file
```
