# BMS / DJMax Keysound Slicer UX Audit

## Scope and evidence quality
This is an evidence-led audit of the fetched documentation, guides, repository pages, and issue pages. The strongest sources are Sayaslicer’s README/releases/issues, Woslicer guides, a BMHelper/BMS creation guide, and practical Japanese tutorials. Some requested tools (especially PTSEQUENCER and Woslicer III’s original distribution) did not expose a reliable primary manual in the fetched results, so their limits are not inferred. GitHub’s Sayaslicer open-issues page returned **No results** when fetched; therefore there are not ten public open issues to summarize, and no issue list is fabricated.

## 1. Comparison table

| Tool | Workflow | Top 3 pain points / limits evidenced | Max slices / snap | Export method |
|---|---|---|---|---|
| **Sayaslicer** | Open audio; add or import markers; preview each keysound; copy BMSE/iBMSC clipboard data; export keysounds or append BMS data; save project. README shortcuts include `Z`, `P`, `M`, `V`, `Shift+V`, `K`, `Shift+K`, undo/redo. ([README](https://github.com/SayakaIsBaka/sayaslicer)) | 1) iBMSC arbitrary-resolution clipboard can produce missing keysounds or gamemode changes after save; BMSE fallback rounds to 192th. 2) Zero-crossing reduces clicks but moves timing markers. 3) Better BPM-change support and proper grid display remain planned. ([README](https://github.com/SayakaIsBaka/sayaslicer)) | Arbitrary 1/1–1/192; no slice-count limit stated in fetched README. Very large-file loading had a >2GB/sample-count bug, later fixed. ([README](https://github.com/SayakaIsBaka/sayaslicer), [issue #5](https://github.com/SayakaIsBaka/sayaslicer/issues/5)) | Direct keysound export plus BMSE/iBMSC clipboard and BMS append. |
| **Woslicer II** | Usually receive marker/cutting positions from BMHelper; paste clipboard positions; export slices; copy keysound data and paste chart data into BMSE/iBMSC. ([BMS Creation Notes](https://docs.google.com/document/d/1ywUO5RxCP8jCM7MuFGFAS5TIRK2gsyM3-rMgvPuMEcw/edit), [Woslicer guide](https://wiki.mid2bms.net/%E4%BB%96%E3%83%ツール/woslicer)) | Multi-tool/manual handoff; file ordering and first free keysound slot matter; some 24/32-bit or Studio One WAVs can crash it and users re-save through Audacity/SoundEngine. ([Woslicer guide](https://wiki.mid2bms.net/%E4%BB%96%E3%ツール/woslicer), [creation notes](https://docs.google.com/document/d/1ywUO5RxCP8jCM7MuFGFAS5TIRK2gsyM3-rMgvPuMEcw/edit)) | No reliable maximum or snap denominator found in fetched primary-quality sources. | Hundreds of physical WAVs plus clipboard/BMS text. |
| **Woslicer III** | Drag rendered audio in; copy cutting positions from BMHelper; File → clipboard import; set BPM; click “総出力” (export all). ([guide](https://purureko.com/etc/post-1704/)) | Export can be disabled when filename is not registered; guide workaround is clicking the black area. It can emit a silent final file that users delete. Its tail-extension/reverse behavior can create a pop. ([guide](https://purureko.com/etc/post-1704/), [Qiita analysis](https://qiita.com/yuinore/items/79db943d2e3447adee71), [tutorial](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3)) | No reliable maximum found; BPM must be set manually in the cited workflow. | “総出力” batch WAV export, then manual BMSE registration. |
| **BMHelper + Mid2BMS** | Split MIDI by pitch/length/velocity; render the reorganized MIDI in the original instrument; send cutting positions to Woslicer; export WAVs; paste keysound definitions and clipboard chart data into editor. ([BMS Creation Notes](https://docs.google.com/document/d/1ywUO5RxCP8jCM7MuFGFAS5TIRK2gsyM3-rMgvPuMEcw/edit)) | Requires DAW re-render; automation may not work well and forces rough audio chopping; operator chooses first free slot and maintains numbering. ([tutorial](https://purureko.com/etc/post-1704/), [creation notes](https://docs.google.com/document/d/1ywUO5RxCP8jCM7MuFGFAS5TIRK2gsyM3-rMgvPuMEcw/edit)) | Documented offset is 01 through ZZ; no general max stated there. BMS chart registration is max 1,295 audio types. ([creation notes](https://docs.google.com/document/d/1ywUO5RxCP8jCM7MuFGFAS5TIRK2gsyM3-rMgvPuMEcw/edit), [BMSE help](https://hitkey.nekokan.dyndns.info/bmse_help_full/usage.html)) | Physical WAV folder, keysound text definitions, then clipboard paste. |
| **dtinth/sound-slicer** | Prepare original WAV + TXT in project folder; tool writes numbered WAVs in a `wav` subfolder and a clipboard file. Melodic mode additionally renders a reorganized MIDI before slicing. ([README](https://github.com/dtinth/sound-slicer)) | Rhythmic mode creates one slice per MIDI note (1,000 notes → 1,000 slices), may leave slight gaps, and both modes do not support BPM changes. ([README](https://github.com/dtinth/sound-slicer)) | No snap/max stated. | Numbered WAVs and generated BMS clipboard file. |
| **Fruity Slicer** | Beat-detect or use embedded WAV region data; slice; optionally zero-cross/de-click; dump pattern to Piano roll or drag slices to WAV-compatible destinations. ([FL Studio manual](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/plugins/Fruity%20Slicer.htm)) | MP3 cannot contain slice markers; click cleanup is a separate operation; exported individual slices require dragging from slice properties. ([FL Studio manual](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/plugins/Fruity%20Slicer.htm)) | Beat detection/quantize controls; no BMS max stated. | Piano-roll dump or individual WAV drag-out. |
| **PTSEQUENCER / DJMAX** | Evidence retrieved describes the production/playback ecosystem rather than a public editor manual. DJMAX’s cited older tool budget is 1,023 keysounds, later reported as 2,047; RESPECT V has automatic keysound playback. ([NamuWiki keysound](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C)) | Asset budget is finite; missing/lost multitrack resources can force keyless collaboration tracks; older lane-interruption behavior can cut off tails, while Technika 2 is described as non-interfering. ([NamuWiki keysound](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C)) | 1,023 historically cited; 2,047 in DPC 2025 report. Treat as target-mode validation, not a guaranteed PTSEQUENCER manual limit. | Proprietary production workflow not documented sufficiently in fetched sources. |

## 2. Problem inventory

### Timeline / timing

| Severity | Problem | Evidence and implication |
|---|---|---|
| **P0** | Timing and audio boundaries are separate failure surfaces: zero-crossing moves markers, and Woslicer III’s tail extension can pop. | Sayaslicer explicitly warns that zero-crossing moves markers and affects iBMSC data ([README](https://github.com/SayakaIsBaka/sayaslicer)); reverse/invert/zero-sample tail processing is analyzed as a pop source ([Qiita](https://qiita.com/yuinore/items/79db943d2e3447adee71)). Provide non-destructive boundary preview, audible A/B, and a “preserve chart time” zero-X mode. |
| **P0** | BPM/grid fidelity is weak in legacy workflows. | dtinth says both modes do not support BPM changes ([README](https://github.com/dtinth/sound-slicer)); Sayaslicer lists BMSE BPM clipboard, MIDI BPM import, and proper grid display as planned ([README](https://github.com/SayakaIsBaka/sayaslicer)). A dual ms + bar/beat ruler is justified. |
| **P1** | Slight gaps can appear between slices. | dtinth documents possible gaps in rhythmic conversion ([README](https://github.com/dtinth/sound-slicer)). Use contiguous virtual intervals and explicit tail policy rather than file-by-file truncation. |
| **P1** | Lane playback semantics can invalidate a “good-looking” slicer assignment. | Older DJMAX/EZ2 behavior can interrupt a playing sample when another sample arrives on the same track; effects/reverb may be intercepted ([NamuWiki](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C)). Preview must simulate selected BMS/RESPECT/TECHNIKA mode. |

### Interface / workflow

| Severity | Problem | Evidence and implication |
|---|---|---|
| **P0** | Export ceremony is a long, error-prone chain: DAW render → Woslicer → physical files → keysound definitions → clipboard → editor. | Exact sequence is documented in BMS Creation Notes ([Google Doc](https://docs.google.com/document/d/1ywUO5RxCP8jCM7MuFGFAS5TIRK2gsyM3-rMgvPuMEcw/edit)). A single-project “Import to Studio” should replace it. |
| **P0** | File order silently determines numbering. | Tutorial warns definitions follow Explorer order and dragged order ([purureko](https://purureko.com/etc/post-1704/)); another tutorial says dragging a middle-numbered file as first shifts definitions and requires reload/retry ([Wix tutorial](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3)). Show stable IDs and auto-generated mapping, never positional numbering. |
| **P1** | Output controls can be unavailable for non-obvious state reasons. | Woslicer III guide says “総出力” may be disabled because filename is not shown, with a black-area click workaround ([purureko](https://purureko.com/etc/post-1704/)). Surface validation errors inline. |
| **P1** | Empty/silent outputs require cleanup. | A practical tutorial tells users to delete a silent final 008.wav and its marker ([Wix tutorial](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3)). Automatically omit or label silent virtual slices. |
| **P1** | Legacy keyboard workflows are powerful but discoverability is low. | Woslicer II is praised for extensive shortcuts, while III is preferred by a guide author for mouse operation ([Mid2BMS wiki](https://wiki.mid2bms.net/%E4%BB%96%E3%83%84%E3%83%BC%E3%83%AB/woslicer)); Sayaslicer documents a large shortcut set ([README](https://github.com/SayakaIsBaka/sayaslicer)). Preserve shortcuts, add searchable command palette and visible shortcut hints. |

### Settings / assets

| Severity | Problem | Evidence and implication |
|---|---|---|
| **P0** | Format incompatibility causes crashes and preprocessing work. | Woslicer guide reports termination on some WAVs and recommends re-saving in Audacity/SoundEngine; specifically flags Studio One and 24/32-bit WAVs ([Mid2BMS wiki](https://wiki.mid2bms.net/%E4%BB%96%E3%83ツール/woslicer)). Normalize/decode on import and report format details. |
| **P1** | Hard budget and capacity limits are easy to exceed. | BMSE help says max 1,295 audio types per chart ([BMSE help](https://hitkey.nekokan.dyndns.info/bmse_help_full/usage.html)); practical guide warns not to pass `#WAVZZ` ([Wix tutorial](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3)); DJMAX cited budget is 1,023, later 2,047 ([NamuWiki](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C)). Show unique-slice count, format-mode budget, and reuse suggestions. |
| **P1** | Users manually reduce key assets to fit size/count. | Japanese practice recommends moving small sounds/long pads to BGM, deleting unused definitions/files, trimming tails, and keeping backups ([Qiita](https://qiita.com/yuinore/items/4de2639ea97bc6723650)). Provide “promote to BGM”, dedupe, tail preview, and reversible cleanup. |
| **P2** | Requested controls such as “Sensitivity 0.55” and per-slice Normalize are not evidenced in the fetched tools. | Do not copy these as established conventions. Treat them as product hypotheses requiring usability testing; Sayaslicer evidence supports threshold, fade, zero-cross, offset, snap, and BPM-related controls ([README](https://github.com/SayakaIsBaka/sayaslicer)). |

### Backend / interchange

| Severity | Problem | Evidence and implication |
|---|---|---|
| **P0** | Clipboard interchange can corrupt semantics. | iBMSC issue reports BMSE clipboard pasting “weirdly/incorrectly” and shows BMHelper-generated data differing from expected layout ([iBMSC issue #5](https://github.com/zardoru/iBMSC/issues/5)). Add round-trip tests and import/export diff preview. |
| **P0** | iBMSC’s >192th precision conflicts with legacy compatibility. | Sayaslicer warns iBMSC data may cause missing keysounds/gamemode changes; BMSE clipboard rounds to nearest 192th ([README](https://github.com/SayakaIsBaka/sayaslicer)). Store exact ms internally and quantize only at target export. |
| **P1** | Base-62 does not remove runtime capacity issues. | A practical LR2base62 note describes an approximately 1.5GB post-decoding limit and suggests awkward 22050Hz reduction in worst cases ([note.com](https://note.com/nakaiankow/n/n5d30ba6c19a6?hl=en)). Budget decoded memory and offer compression/export diagnostics. |
| **P1** | Large files historically made Sayaslicer unusable. | Issue #5 attributes >2GB/sample-count failure to signed-int overflow; a later release says the backend overhaul fixed it and added formats such as Opus, while warning of regressions ([issue #5](https://github.com/SayakaIsBaka/sayaslicer/issues/5), [release](https://github.com/SayakaIsBaka/sayaslicer/releases/tag/vb28b1af)). Stream/segment waveform generation and regression-test long files. |
| **P1** | Reverb tails and lane interruption are not equivalent across target modes. | NamuWiki describes effects/core separation and interception problems in older behavior ([NamuWiki](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C)). Keep source intact; represent start/end/tail policy virtually and validate in mode-specific playback. |

## 3. What Sayaslicer did right vs Woslicer — keep these

**Keep from Sayaslicer:** cross-platform distribution goal; BMSE and iBMSC clipboard paths; arbitrary 1/1–1/192 snapping; offset; zero-crossing; configurable silent-tail threshold; fadeout; project save/load; MIDI and Mid2BMS imports; marker copy/paste; undo/redo; keysound preview; and explicit keyboard shortcuts. These are all stated in the README ([Sayaslicer README](https://github.com/SayakaIsBaka/sayaslicer)). Its release notes also show practical UX improvements: drag/drop on macOS/Linux, settings applied to preview, waveform response to settings, marker-list scroll, and resnap-all ([release](https://github.com/SayakaIsBaka/sayaslicer/releases/tag/vb543f80)).

**Keep from Woslicer:** the compact direct-audio workflow, fast marker navigation, extensive keyboard operation in II, and the III mouse-friendly path. The guides confirm that Woslicer can accept markers from BMHelper and batch-export, which is the right conceptual bridge even though the physical-file handoff is costly ([Mid2BMS wiki](https://wiki.mid2bms.net/%E4%BB%96%E3%83ツール/woslicer), [purureko](https://purureko.com/etc/post-1704/)).

**Do not keep:** silent-file cleanup, positional Explorer ordering, hidden filename state, manual format conversion, and opaque clipboard payloads.

## 4. Top 10 recommendations for DJMax Studio Slicer

1. **P0 — Make slices virtual until finalize.** Store `{sourceFile,startMs,endMs}` plus per-slice processing metadata; preview instantly and export WAV/OGG only on finalize. This directly removes the documented DAW→render→hundreds-of-files→definitions→clipboard ceremony ([Google Doc](https://docs.google.com/document/d/1ywUO5RxCP8jCM7MuFGFAS5TIRK2gsyM3-rMgvPuMEcw/edit)).
2. **P0 — Build the proposed dual timeline.** Put waveform/ms ruler below and chart/bar/beat ruler above, with a synchronized playhead and zoom. BPM-change support and proper grid display are explicitly missing/planned in existing tools ([Sayaslicer README](https://github.com/SayakaIsBaka/sayaslicer), [sound-slicer README](https://github.com/dtinth/sound-slicer)).
3. **P0 — Add target-mode simulation: BMS / RESPECT / TECHNIKA.** Simulate lane interruption, non-interference, automatic-keysound preview, and per-mode asset budgets; these behaviors differ materially ([NamuWiki](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C)).
4. **P0 — Make zero-X non-destructive and audibly comparable.** Offer “move boundary” versus “adjust only rendered edge,” show timing delta, and A/B click detection. Existing zero-crossing moves markers and Woslicer’s tail workaround can pop ([Sayaslicer README](https://github.com/SayakaIsBaka/sayaslicer), [Qiita](https://qiita.com/yuinore/items/79db943d2e3447adee71)).
5. **P0 — Add a Slice Library with stable IDs, dedupe, reuse, and budget meter.** Prevent Explorer-order drift and warn before BMS 1,295 / DJMAX 1,023–2,047 budgets ([BMSE help](https://hitkey.nekokan.dyndns.info/bmse_help_full/usage.html), [NamuWiki](https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C)).
6. **P1 — Put Draft slice and per-slice Zero-X/FADE/Normalize controls beside the waveform.** Threshold/fade/zero-X are proven controls; per-slice overrides are a safe product extension, not a claim that legacy tools already provide them ([Sayaslicer README](https://github.com/SayakaIsBaka/sayaslicer), [FL Studio manual](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/plugins/Fruity%20Slicer.htm)).
7. **P1 — Add a visible stateful export/finalize wizard.** Show missing filename, unsupported codec/bit depth, silent slices, output count, and target budget inline instead of requiring Woslicer’s black-area workaround or deleting 008.wav manually ([purureko](https://purureko.com/etc/post-1704/), [Wix tutorial](https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3)).
8. **P1 — Guarantee round-trip import/export.** Provide BMS/BMSE/iBMSC import, Studio chart import, a diff preview, and automated fixtures for base-36/base-62 and >192th timing. iBMSC reports incorrect BMSE clipboard pastes, and Sayaslicer documents compatibility failures ([iBMSC issue #5](https://github.com/zardoru/iBMSC/issues/5), [Sayaslicer README](https://github.com/SayakaIsBaka/sayaslicer)).
9. **P1 — Use robust audio decoding and streaming.** Accept common WAV bit depths plus OGG/Opus where supported, generate waveform tiles, and avoid whole-file signed-int assumptions. Woslicer preprocessing and Sayaslicer’s >2GB issue show why ([Woslicer wiki](https://wiki.mid2bms.net/%E4%BB%96%E3%83ツール/woslicer), [Sayaslicer issue #5](https://github.com/SayakaIsBaka/sayaslicer/issues/5), [release](https://github.com/SayakaIsBaka/sayaslicer/releases/tag/vb28b1af)).
10. **P1 — Preserve keyboard speed but add discoverability and recovery.** Keep Woslicer/Sayaslicer shortcuts, add command search, remapping, undo history, marker-list navigation, and autosave project snapshots. The existing tools demonstrate that keyboard editing is valuable but fragmented across opaque handoffs ([Sayaslicer README](https://github.com/SayakaIsBaka/sayaslicer), [Mid2BMS wiki](https://wiki.mid2bms.net/%E4%BB%96%E3%83ツール/woslicer)).

## 5. Raw source list

- https://github.com/SayakaIsBaka/sayaslicer
- https://github.com/SayakaIsBaka/sayaslicer/issues
- https://github.com/SayakaIsBaka/sayaslicer/issues/5
- https://github.com/SayakaIsBaka/sayaslicer/releases/tag/vb28b1af
- https://github.com/SayakaIsBaka/sayaslicer/releases/tag/vb543f80
- https://github.com/dtinth/sound-slicer
- https://wiki.mid2bms.net/%E4%BB%96%E3%83%84%E3%83%BC%E3%83%AB/woslicer
- https://purureko.com/etc/post-1704/
- https://qiita.com/yuinore/items/79db943d2e3447adee71
- https://spirea3cross.wixsite.com/bmstukuru2020/how-to-make3
- https://docs.google.com/document/d/1ywUO5RxCP8jCM7MuFGFAS5TIRK2gsyM3-rMgvPuMEcw/edit
- https://github.com/zardoru/iBMSC/issues/5
- https://hitkey.nekokan.dyndns.info/bmse_help_full/usage.html
- https://note.com/nakaiankow/n/n5d30ba6c19a6?hl=en
- https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/plugins/Fruity%20Slicer.htm
- https://en.namu.wiki/w/%ED%82%A4%EC%9D%8C
- https://qiita.com/yuinore/items/4de2639ea97bc6723650
- https://de0.hatenablog.com/entry/20080412/1208067908
- https://bms-community.github.io/resources/