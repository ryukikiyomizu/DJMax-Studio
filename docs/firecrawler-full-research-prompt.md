# Firecrawler Agent — Full Deep Research Prompt (JSON-locked)

Copy everything below `---` into Firecrawler Deep Research Agent. It is engineered to output VALID JSON matching your schema exactly.

---

You are a SENIOR BMS/DJMax UX RESEARCHER + REVERSE ENGINEER. Your job is deep forensic audit of keysound slicer tools to design a best-in-class NON-DESTRUCTIVE slicer inside DJMax Studio (WPF/.NET 8, `DJMaxEditor.Studio/Keyslicer/`).

## CONTEXT: What DJMax Studio Is Building
- Current slicer keeps long source file INTACT as virtual slices `{sourceFile, startMs, endMs}` that INSTANTLY become playable notes/keysounds. Export to wav/ogg ONLY on finalize.
- Traditional BMS workflow you are auditing: `MIDI/BMHelper -> export hundreds of trackXX.wav -> import into Woslicer/Sayaslicer -> drag cuts -> export again -> copy-paste definitions into BMSE/iBMSC`.
- You must compare non-destructive virtual slice vs legacy export-ceremony and find every friction point.
- New Studio features to validate: Dual Timeline (waveform bottom + Studio chart preview top where slices auto-create notes), Mode Switcher `BMS | RESPECT | TECHNIKA`, Round-trip `Slicer <-> Studio Import`, 192-quantize, zero-cross, auto-advance Grid/Beat/Off.

## MISSION
Produce a JSON object that validates 100% against this JSON Schema (draft-07). Output ONLY raw JSON, no markdown, no comments, no trailing commas:

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "properties": {
    "tool_audits": {
      "type": "array",
      "description": "Detailed audit of existing BMS/DJMax keysound slicing tools",
      "items": {
        "type": "object",
        "properties": {
          "tool_name": { "type": "string" },
          "workflow_steps": { "type": "array", "items": { "type": "string" } },
          "pain_points": {
            "type": "array",
            "items": {
              "type": "object",
              "properties": {
                "category": { "type": "string", "enum": ["export_ceremony","file_management","overflow","audio_artifacts","ui_ux","technical_limit"] },
                "description": { "type": "string" },
                "severity": { "type": "string", "enum": ["P0","P1","P2"] },
                "source_url": { "type": "string" }
              },
              "required": ["description","severity","source_url"]
            }
          },
          "technical_limits": {
            "type": "object",
            "properties": {
              "max_slices": { "type": "string" },
              "snap_denominators": { "type": "string" },
              "bpm_handling": { "type": "string" },
              "format_support": { "type": "string" }
            }
          },
          "user_settings": { "type": "array", "items": { "type": "string" } },
          "github_insights": {
            "type": "array",
            "items": {
              "type": "object",
              "properties": {
                "issue_title": { "type": "string" },
                "summary": { "type": "string" },
                "url": { "type": "string" }
              }
            }
          }
        },
        "required": ["tool_name","pain_points","workflow_steps"]
      }
    },
    "comparative_analysis": {
      "type": "object",
      "properties": {
        "strengths_sayaslicer": { "type": "array", "items": { "type": "string" } },
        "strengths_woslicer": { "type": "array", "items": { "type": "string" } }
      }
    },
    "recommendations_for_djmax_studio": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "feature_name": { "type": "string" },
          "priority": { "type": "string", "enum": ["High","Medium","Low"] },
          "rationale": { "type": "string" },
          "focus_area": { "type": "string", "enum": ["Timeline","Interface","Settings","Backend"] },
          "reference_source": { "type": "string" }
        },
        "required": ["feature_name","priority","rationale"]
      }
    },
    "source_citations": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "url": { "type": "string" },
          "translated_quote": { "type": "string" }
        }
      }
    }
  },
  "required": ["tool_audits","recommendations_for_djmax_studio","source_citations"]
}
```

## TOOLS TO AUDIT (minimum 5, target 6)
You MUST audit ALL of these with Firecrawl crawl + extract + search. If one has no GitHub, still audit via blogs/wiki:
1. **Sayaslicer** - https://github.com/SayakaIsBaka/sayaslicer
2. **Woslicer II** (wosderge) - search "Woslicer II Way of Slicing"
3. **Woslicer III** - search "WoslicerIII 使い方 切断位置コピー"
4. **BMHelper / Mid2BMS** (MIDI -> BMS wav export precursor)
5. **PTSEQUENCER / DTX BMS limits** (2047 keysound cap, 65535 theory, base36/62 overflow)
6. **BMSE / iBMSC + FL Studio Slicer / dtinth/sound-slicer** as reference modern slicer

## FIRECRAWL EXECUTION PLAN (do this step-by-step)
1. **Crawl** each GitHub repo: README, Releases, Issues (open+closed, sort by reactions/comments), Discussions, Wiki. Use `firecrawl crawl --depth deep --limit 50`.
2. **Search** with Firecrawl Search API for JP/KR sources (translate quotes):
   - `sayaslicer github issues zero-cross 192 snap clipboard BMSE`
   - `woslicer 使い方 切断位置コピー total output BMSE 貼り付け`
   - `BMS 切り出し ワークフロー 発狂 自作 Pain`
   - `BMS keysound base36 overflow 1296 定義 再利用`
   - `PTSEQUENCER 2047 キー音 上限 DJMAX RESPECT キー音`
   - `DTX BMS definition reuse wizard bms制作`
   - `DJMAX TECHNIKA non-overlap keysound patent`
3. **Extract** structured data from top 20 pages per tool with `firecrawl extract` using prompt: "Extract workflow steps, pain points, limits, settings, keyboard shortcuts, export method, snap denominators, BPM handling".
4. **Map pain_points.category strictly:**
   - `export_ceremony` = multi-step export/import/copy-paste (e.g., woslicer `切断位置コピー → BMSE貼り付け`, BMHelper multi-wav export)
   - `file_management` = hundreds of wav files, naming, folder chaos
   - `overflow` = WAV definition 36^2=1296 / base62 / 2047 cap
   - `audio_artifacts` = clicks without zero-cross, reverb tail cut, silent tail, normalization
   - `ui_ux` = no preview, no undo, no dual ruler, drag precision, 1380x860 layout
   - `technical_limit` = snap only 1/4-1/16, no 1/192, BPM change not supported, mono only
   Severity: P0=blocks workflow, P1=major friction, P2=annoyance.
5. **Fill technical_limits with VERBATIM strings from docs.** Example: `max_slices: "1296 (36^2 WAV definitions, WoslicerII) / 2047 (PTSEQUENCER)"`, `snap_denominators: "1/1 to 1/192 (Sayaslicer arbitrary, BMSE rounded to 192nd)"`
6. **github_insights**: Top 5-7 highest-impact issues per repo with title, 1-sentence summary, direct URL. If repo has <5 issues, include feature requests from README TODO.
7. **comparative_analysis**: 4-6 bullets each. What Sayaslicer got right (zero-cross nudge, arbitrary 192 snap, nightlies via nightly.link, BMSE vs iBMSC clipboard modes) vs Woslicer got right (shake erase, P audition, btsugiri V/Shift+V/B, definition reuse wizard).
8. **recommendations_for_djmax_studio**: MINIMUM 10, target 12. Each must map to focus_area and cite reference_source URL. Prioritize: High=P0 fix, Medium=P1 polish, Low=P2 nice-to-have. Cover ALL focus_areas at least twice. Must include: Dual-ruler chart preview overlay, 192 snap sync to BPM, per-slice Zero-X+FADE+Normalize preview, Auto-advance Grid/Beat/Off, Mode Tabs BMS/RESPECT/TECHNIKA semantics, Round-trip Import to Studio with UndoRedoAction, Virtual slice non-destructive backend, Silent-tail remover toggle, Keyboard map D/F/J/K + Ctrl+Z/Y.
9. **source_citations**: MINIMUM 15 distinct URLs, each with English translated_quote (15-30 words) if source is JP/KR. Must include at least one from each tool. Deduplicate.

## OUTPUT VALIDATION RULES
- Every `pain_points[].source_url` and `reference_source` and `github_insights[].url` must be a REAL crawlable URL (github.com, namu.wiki, bemaniwiki.com, qiita.com, zenn.dev, youtube.com, etc). No fake URLs.
- `workflow_steps` minimum 5 steps per tool, in user order (e.g., ["Drag New_Project.mp3 (152s, 44100Hz) onto waveform", "Set BPM 170, set Snap 1/16, enable Zero-cross", ... "Ctrl+C 切断位置コピー -> BMSE Pastes WAV definitions"]).
- `user_settings` must list actual controls seen in screenshots/docs: Sensitivity, Zero-cross, Snap, Auto-advance, Normalize, Fade, Offset.
- Escape all JSON strings properly. If no data for optional object, use `""` not null.
- Respond with JSON ONLY. No markdown fence. No explanation before/after.

## QUALITY BAR
- Research depth: crawl ≥30 pages, search ≥8 queries.
- Translate JP/KR quotes accurately; keep original nuance (e.g., 「切断位置コピー」="Copy cut positions").
- Severity justification: P0 must cite blocker language ("cannot", "fails", "overflow"), P1 "cumbersome/manual", P2 "minor".
- DJMax Studio recommendations must explicitly state why virtual-slice backend solves legacy pain (no 1296 cap, no file spam).

Begin crawling now. Return the final JSON object when complete.
