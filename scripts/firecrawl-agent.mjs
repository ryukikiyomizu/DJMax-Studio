import { Firecrawl } from 'firecrawl';
import { z } from 'zod';
import fs from 'fs';

const apiKey = process.env.FIRECRAWL_API_KEY;
if (!apiKey) {
  console.error('FIRECRAWL_API_KEY missing');
  process.exit(1);
}
console.log('Firecrawl agent starting', new Date().toISOString());
// Mask key in logs
console.log('::add-mask::' + apiKey);

const firecrawl = new Firecrawl({ apiKey });

const result = await firecrawl.agent({
  prompt: "# Deep Research Prompt: BMS / DJMax Keysound Slicer UX Audit\n\nYou are a UX researcher auditing keysound slicer tools for DJMax Studio (a BMS/DJMax/TECHNIKA chart editor). Crawl deeply and extract REAL user pain points, not marketing copy.\n\n## Goal\nFind concrete Problems / Limits / Workarounds / Feature Requests for Sayaslicer, Woslicer II, Woslicer III and related BMS keysound tools (BMHelper, Mid2BMS, BMSE, iBMSC, FL Studio Slicer, PTSEQUENCER, Sound Slicer), so we can build a best-in-class non-destructive slicer inside DJMax Studio.\n\nDJMax Studio's new slicer keeps the long source file intact as `{sourceFile, startMs, endMs}` virtual slices that instantly become playable notes, with wav/ogg export only on finalize. Compare this against the traditional \"export hundreds of wavs -> import to BMS editor\" workflow.\n\n## Targets to Crawl (use Firecrawl crawl + extract + search)\n1. https://github.com/SayakaIsBaka/sayaslicer - README, Releases, Issues, Discussions\n2. https://github.com/wosderge - WoslicerII (Way of Slicing)\n3. Woslicer III guides - search: \"WoslicerIII 使い方\", \"Woslicer 切断位置コピー\", \"BMS 切り出し\"\n4. https://github.com/dtinth/sound-slicer\n5. BMSE / iBMSC clipboard workflow docs, base-36/base-62 overflow issues\n6. https://namu.wiki/w/키음, https://namu.wiki/w/BMS, PTSEQUENCER 2047 keysound limit\n7. DJMAX RESPECT / TECHNIKA keysound handling - search: \"DJMAX keysound\", \"TECHNIKA non-overlap\", \"RESPECT keysound toggle\"\n8. Japanese blogs/Qiita/Zenn about \"BMS 自作 発狂 切り出し ワークフロー\"\n9. YouTube comments/tutorials for sayaslicer/woslicer (pain points)\n\nSearch queries to run via Firecrawl search:\n- \"sayaslicer problems issues zero cross 192 snap clipboard\"\n- \"woslicer export ceremony definition reuse wizard btsugiri\"\n- \"BMS keysound workflow problems BMSE iBMSC BGM lane\"\n- \"BMS wav export hundreds files management nightmare\"\n\n## Extract For Each Tool\nFor Sayaslicer, Woslicer II, Woslicer III, BMHelper/Mid2BMS, PTSEQUENCER:\n- Full workflow step-by-step\n- Problems: export ceremony, file management, base36/62 overflow, silent double-placement, reverb-tail cutoff, offset/zero-cross errors\n- Limits: snap denominators, max slices, BPM handling, stereo, format support\n- Settings users actually touch (Sensitivity, Fade, Zero-cross, Snap 1/192, Auto-advance, Normalize)\n- Keyboard shortcuts and what users complain about\n- GitHub Issues labeled bug/enhancement - summarize top 10\n\n## Focus Areas for DJMax Studio Improvements\nEvaluate and give recommendations for:\n1. **Timeline** - waveform + chart preview dual ruler, playhead sync, zoom, bar/beat grid vs ms\n2. **Whole Interface** - layout 1380x860, Slice Library, Draft slice, toolbar (Snap/Undo/Redo), per-slice Zero-X/FADE/Normalize\n3. **Settings** - Snap (1/4 to 1/192), Sensitivity 0.55, Zero-cross on/off, Auto-advance Grid/Beat/Off, per-slice overrides\n4. **Backend** - non-destructive virtual slices, zero-crossing algorithm, 192nd quantization, BPM sync, undo/redo, round-trip Slicer <-> Studio import, wav/ogg export on finalize\n\nProposed feature to validate: Dual timeline (waveform on bottom + Studio chart preview on top where slices auto-create notes), mode switcher BMS / RESPECT / TECHNIKA, and after slicing \"Import to Studio\" to edit in real project. How do existing tools handle (or fail to handle) this?\n\n## Deliverable Format (markdown)\n1. Comparison Table: Tool | Workflow | Top 3 Pain Points | Max Slices/Snap | Export Method\n2. Problem Inventory (grouped: Timeline, Interface, Settings, Backend) with severity (P0/P1/P2) and source URL for each claim\n3. What Sayaslicer did right vs Woslicer (keep these)\n4. Top 10 Recommendations for DJMax Studio Slicer, prioritized, with rationale + source\n5. Raw source list with URLs\n\nRules: Cite URL for every claim. No hallucinations. If a page is Japanese/Korean, translate key quotes. Prefer GitHub Issues / user forums over docs.",
  schema: z.object({
      tool_audits: z.array(z.object({
        tool_name: z.string().describe("Name of the tool (e.g., Sayaslicer, Woslicer III, BMHelper, PTSEQUENCER)"),
        tool_name_citation: z.string().describe("Source URL for tool_name").optional(),
        workflow_steps: z.array(z.object({
          value: z.string(),
          value_citation: z.string().describe("Source URL for this value").optional()
        })).describe("Step-by-step user workflow from file import to final editor integration (e.g. clipboard copy-paste steps)"),
        pain_points: z.array(z.object({
          category: z.string(),
          category_citation: z.string().describe("Source URL for category").optional(),
          description: z.string(),
          description_citation: z.string().describe("Source URL for description").optional(),
          severity: z.string(),
          severity_citation: z.string().describe("Source URL for severity").optional(),
          source_url: z.string(),
          source_url_citation: z.string().describe("Source URL for source_url").optional()
        })),
        technical_limits: z.object({
          max_slices: z.string().describe("Maximum slice/definition capacity (e.g. 1296, 2047, 65535)").optional(),
          max_slices_citation: z.string().describe("Source URL for max_slices").optional(),
          snap_denominators: z.string().describe("Supported grid snaps (e.g. 1/4 to 1/192)").optional(),
          snap_denominators_citation: z.string().describe("Source URL for snap_denominators").optional(),
          bpm_handling: z.string().describe("How the tool handles BPM changes or sync").optional(),
          bpm_handling_citation: z.string().describe("Source URL for bpm_handling").optional(),
          format_support: z.string().describe("Audio formats and channel support (Mono/Stereo, Wav/Ogg)").optional(),
          format_support_citation: z.string().describe("Source URL for format_support").optional()
        }).optional(),
        user_settings: z.array(z.object({
          value: z.string(),
          value_citation: z.string().describe("Source URL for this value").optional()
        })).describe("Actual UI controls users adjust (e.g., Sensitivity, Zero-cross, Fade, Auto-advance)").optional(),
        github_insights: z.array(z.object({
          issue_title: z.string().optional(),
          issue_title_citation: z.string().describe("Source URL for issue_title").optional(),
          summary: z.string().optional(),
          summary_citation: z.string().describe("Source URL for summary").optional(),
          url: z.string().optional(),
          url_citation: z.string().describe("Source URL for url").optional()
        })).describe("Summary of top issues or feature requests from repositories").optional()
      })).describe("Detailed forensic audit of existing BMS/DJMax keysound slicing tools including legacy and modern utilities."),
      comparative_analysis: z.object({
        strengths_sayaslicer: z.array(z.object({
          value: z.string(),
          value_citation: z.string().describe("Source URL for this value").optional()
        })).describe("Features to keep from Sayaslicer (e.g. 192 snap, zero-cross nudge)").optional(),
        strengths_woslicer: z.array(z.object({
          value: z.string(),
          value_citation: z.string().describe("Source URL for this value").optional()
        })).describe("Features to keep from Woslicer (e.g. shake erase, definition reuse wizard)").optional()
      }),
      recommendations_for_djmax_studio: z.array(z.object({
        feature_name: z.string(),
        feature_name_citation: z.string().describe("Source URL for feature_name").optional(),
        priority: z.string(),
        priority_citation: z.string().describe("Source URL for priority").optional(),
        rationale: z.string().describe("Why this solves a legacy P0/P1 pain point"),
        rationale_citation: z.string().describe("Source URL for rationale").optional(),
        focus_area: z.string(),
        focus_area_citation: z.string().describe("Source URL for focus_area").optional(),
        reference_source: z.string().optional(),
        reference_source_citation: z.string().describe("Source URL for reference_source").optional()
      })).describe("Prioritized feature recommendations for the new internal non-destructive slicer"),
      source_citations: z.array(z.object({
        url: z.string(),
        url_citation: z.string().describe("Source URL for url").optional(),
        translated_quote: z.string().describe("Key quote translated to English from JP/KR sources"),
        translated_quote_citation: z.string().describe("Source URL for translated_quote").optional()
      }))
    }),
  model: 'spark-2',
});

console.log("Agent finished, writing files...");
const output = result.data ?? result;
fs.writeFileSync('firecrawl-result.json', JSON.stringify(output, null, 2));
fs.writeFileSync('docs/keyslicer-research-firecrawl.json', JSON.stringify(output, null, 2));
console.log("Wrote firecrawl-result.json", JSON.stringify(output).length, "bytes");
console.log("tools:", output.tool_audits?.length, "recs:", output.recommendations_for_djmax_studio?.length);
