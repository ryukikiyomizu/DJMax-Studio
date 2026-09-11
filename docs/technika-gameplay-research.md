# TECHNIKA gameplay research: the field, the clock, notes, and effectors

Why this file exists: the playfield preview was built against geometry measured
from a D3D9 capture (documented in code, see `ScanFieldLeft` in
`Shared/Preview/GameplayPreviewProjector.cs` and `TechnikaPlayfieldMetrics.cs`),
but the *rules* behind that geometry - how the clock sweeps, what every note
colour means, and what the arcade's pre-song effectors do - were scattered
across comments. The owner asked for deep research before extending the
preview (scroll-direction inversion, faders, blink/blind). This is the research;
the rendering rules below are what the preview now follows, and every claim is
pinned to a source. Tuned numbers the sources could not give are listed as
assumptions at the end.

## 1. The field and the clock

TECHNIKA is not a scrolling rhythm game. The cabinet has two monitors - a
32" spectator screen on top and a 22" infrared touchscreen below
[4](https://www.giantbomb.com/djmax-technika/3030-27806/) - and the touch
field is split horizontally into two halves. A vertical time line sweeps the
**upper half left to right**, then the **lower half right to left**, and so on
alternately [4](https://www.giantbomb.com/djmax-technika/3030-27806/)
[7](https://en.wikipedia.org/wiki/DJMax_Technika). The Vita port describes it
as "a metronome sort of bar scrolls across the top half of the screen, then
back across the bottom half"
[8](https://insidepulse.com/2013/01/14/review-djmax-technika-tune-sony-playstation-vita/).

Default motion is a clockwise loop; the manuals and the Korean community call
the four sweep modes CW / ACW / LL / RR (section 5).

A note's horizontal position **is** its position in time inside the current
sweep; its vertical position is its lane. The touch point is where the line
crosses the centre of the note. Normal notes judge on both X and Y; connected
notes judge along their path [T3 2.3](https://en.namu.wiki/w/DJMAX%20TECHNIKA%203/%ED%94%8C%EB%A0%88%EC%9D%B4%20%EB%B0%A9%EB%B2%95%20&%20%EC%8B%9C%EC%8A%A4%ED%85%9C).

### Scan length (scroll speed)

There is no speed multiplier. Each song/pattern has one authored sweep length:

* **4-beat scroll** - one sweep covers four beats. The common case and the
  default the chart format places [T3 2.6](https://en.namu.wiki/w/DJMAX%20TECHNIKA%203/%ED%94%8C%EB%A0%88%EC%9D%B4%20%EB%B0%A9%EB%B2%95%20&%20%EC%8B%9C%EC%8A%A4%ED%85%9C).
* **8-beat scroll** - one sweep covers eight beats. Leisurely, common in
  beginner charts; with dense notes it is *harder* because twice as much chart
  is on one screen.
* **2-beat scroll** - only `D2` and `Feel Ma Beat` in TECHNIKA 3; at D2's 178
  BPM it moves like a 4-beat chart at 356 BPM ("you practically have to
  memorise the notes").
* **Variable scrolls** - `Black Swan` switches between 4/8, 5/8 and 6/8 beat
  sweeps mid-chart; the D2 MX ending has a 1-beat sweep; the long hold ending
  `Rage Of Demon` has a 12-beat sweep. "In this case think of it as shifting
  gears in a keyboard rhythm game."

The editor projector fixes four beats per scan
(`DefaultBeatsPerScan = 4`, 240 pulses per beat → 960 pulses per scan). That is
correct for the whole TECHNIKA 2 corpus the track census covered, but a future
variable-scan pass should derive sweep boundaries from the end-of-scan flags
(tracks 4–7) rather than assume them (section 7).

### The handover

Before a sweep ends, the next half's notes and line are already prepared: note
state flips to Active at 87.5% of the current scan and, during that window,
both sweeps are on screen - the outgoing line finishing its run and the
incoming one parked at its start edge. This number is measured from the
client's note lifecycle and is encoded as `HandoverPhase = 0.875`.

## 2. Note types

| Attr. | Projector `Kind` | Colour / art | Gesture |
|---|---|---|---|
| 0 | `Basic` | **pink / red** disc | Tap once when the line is on the note's centre [4](https://www.giantbomb.com/djmax-technika/3030-27806/) |
| 0, long | `Drag` | **green** head with a straight **yellow** path (horizontal or angled) | Put the finger down on the head and follow the path keeping it on the line; staying slightly ahead judges more reliably. T3 calls this the drag note, T1/2 "Dragging Long Note" [4](https://www.giantbomb.com/djmax-technika/3030-27806/) [T3 2.3](https://en.namu.wiki/w/DJMAX%20TECHNIKA%203/%ED%94%8C%EB%A0%88%EC%9D%B4%20%EB%B0%A9%EB%B2%95%20&%20%EC%8B%9C%EC%8A%A4%ED%85%9C) |
| 5 + 6 | `ChainHead` / `ChainNode` (plus implicit nodes) | Joints looking like normal notes on a path | Scratch along the whole path with the line; paths cross lanes (vertical/ diagonal screen motion). Each joint is individually tappable - "single hitting" a chain sometimes judges better than scratching; joints not on a shared axis (same X or same Y) cannot be scratched and must be tapped [T3 2.3](https://en.namu.wiki/w/DJMAX%20TECHNIKA%203/%ED%94%8C%EB%A0%88%EC%9D%B4%20%EB%B0%A9%EB%B2%95%20&%20%EC%8B%9C%EC%8A%A4%ED%85%9C) |
| 12 | `Hold` | dark note with a straight **blue** line to an end cap | Press at the head and hold until the line reaches the blue end. The release burst fires *slightly before* the art's literal end; holding longer is free (no penalty). Releasing late is what costs the judgement [4](https://www.giantbomb.com/djmax-technika/3030-27806/) |
| 10 + 11 | `RepeatHead` + `Repeat` | **purple** head with small purple tick lines in the same lane | Tap the head, then tap again for each purple tick the line crosses. The repeat series belongs to one lane [1](https://djmax.fandom.com/wiki/DJMAX_Technika_Q) [4](https://www.giantbomb.com/djmax-technika/3030-27806/) |
| 10/11, long | `RepeatHeadHold` / `RepeatHold` | purple, ending in a hold | Tap the ticks, then hold the final segment - "a cross between holding and repeat" [8](https://insidepulse.com/2013/01/14/review-djmax-technika-tune-sony-playstation-vita/) |

Other attribute facts the projector relies on:

* Attribute **100** is keysound-only: it carries audio and never draws
  gameplay. Tracks **20–31** are the keysound accompaniment bank (~1200
  events/chart vs ~336 on the lanes) and are not lanes; tracks **0–3** are the
  four touch lanes and tracks **4–7** hold per-lane end-of-scan marker events.
  The full per-track census over 444 TECHNIKA 2 charts is documented in
  `GameplayPreviewProjector.LastLaneTrack`.
* A stored duration is the **keysound** length on every note, not a hold
  length; only the kind table above (and the legacy editor's `Duration > 6`
  gate for attributes 0/10/11) makes something a long note. See
  `GameplayPreviewNoteKinds.HasHoldTrail`.
* Up to three normal notes can share an instant (two are routine); chains can
  absorb normal taps that fall inside their span across any lane, which is why
  the chain fixup pass turns in-between `Basic` notes into implicit chain
  nodes.

Chart patterns per mix: Lite (tutorial), Popular (normal), Technical
(harder/faster scroll), Special (Platinum missions)
[6](https://en.wikipedia.org/wiki/DJMax_Technika)
[3](http://cyphergate.net/index.php?title=DJMAX_Technika). Line counts are
2/3/4 (Q and Tune drop 4-line); a two-player DUO chart gives each player three
lanes - source tracks 8–10 are player 2, which the one-player projection
reports as a diagnostic instead of drawing (see the census comment).

## 3. Judgement, gauge, fever

* Verdicts: **MAX** (rainbow MAX for the closest timing), **COOL**, **GOOD**,
  **BREAK**. Good still counts toward combo; a break resets combo and drains
  the groove gauge. Empty gauge ends the song; hits refill it
  [7](https://en.wikipedia.org/wiki/DJMax_Technika)
  [T1 scoring](https://en.namu.wiki/w/DJMAX%20TECHNIKA).
* In TECHNIKA 1 the base note scores rise with the combo band (e.g. MAX 334 →
  388, COOL 182 → 218, GOOD 81 → 99 across bands up to 401 combo).
* Fever fills by consecutive hits and, while active, makes every touch judge
  at MAX for its duration (TECHNIKA Q labels these FEVER MAX; fever in the
  arcade titles also boosts combo gain). The arcade TECHNIKA titles removed
  the older speed-lock effect of fever.
* TECHNIKA Q replaced the verdict grades with percentage accuracy and adds a
  random "Lucky!" judgement; it is a mobile redesign and not the arcade
  reference [1](https://djmax.fandom.com/wiki/DJMAX_Technika_Q).

The preview models none of the scoring/health systems - it is an editing aid,
not an autoplay scorer - and the header HUD therefore draws as a clean plate
rather than fake gauge/score cells unless measurement guides are enabled.

## 4. The effectors

TECHNIKA 1 exposed effectors through Platinum Crew mission courses (mission
strings already combine "fade in blind reverse scroll direction" and
"blink reverse scroll", so the full effector set ships with the first game)
[T1 missions](https://en.namu.wiki/w/DJMAX%20TECHNIKA).
TECHNIKA 2 and 3 let players pick them on the pre-song screen, grouped into
three families
[T2 2.3](https://en.namu.wiki/w/DJMAX%20TECHNIKA%202/%ED%94%8C%EB%A0%88%EC%9D%B4%20%EB%B0%A9%EB%B2%95&%EC%8B%9C%EC%8A%A4%ED%85%9C)
[T3 2.5](https://en.namu.wiki/w/DJMAX%20TECHNIKA%203/%ED%94%8C%EB%A0%88%EC%9D%B4%20%EB%B0%A9%EB%B2%95%20&%20%EC%8B%9C%EC%8A%A4%ED%85%9C).
TECHNIKA Tune's song modifiers carry the same three: change scroll direction,
fade notes in or out, and hide the time line
[8](https://insidepulse.com/2013/01/14/review-djmax-technika-tune-sony-playstation-vita/).

### 4.1 Note series (faders)

| Code | Behaviour |
|---|---|
| FI (Fade In) | Notes are invisible far from the line and appear as it approaches |
| FI2 | Same, over a shorter distance |
| FO (Fade Out) | Notes are visible far away and disappear as the line reaches them - judged harder on a touchscreen, because hitting a missing note causes stray misses |
| FO2 | Same, over a shorter distance |

The fade depends on **position, not scroll speed**, which is why FI is harder
on fast songs and FO on slow ones. The preview therefore computes opacity from
each note's `ApproachScanDistance` (scans between the sweep and the note head),
not from wall-clock time.

### 4.2 Timeline series

| Code | Behaviour |
|---|---|
| BK (Blink) | The sweep flashes; it is present about 50% of the time |
| BK2 (Blink 2) | Present about 25% of the time |
| BL (Blind) | No sweep at all - memory play |

### 4.3 Scroll direction series

| Code | Top half | Bottom half | `TechnikaSweepRightward(top/bottom)` |
|---|---|---|---|
| **CW** (default) | left → right | right → left | `true` / `false` |
| **ACW / CCW** (counter-clockwise) | right → left | left → right | `false` / `true` |
| **LL** (all left) | right → left | right → left | `false` / `false` |
| **RR** (all right) | left → right | left → right | `true` / `true` |

Which scan fills which half is fixed by the clock (odd scans top, even scans
bottom); only the direction inside the half changes. The same shared field
rectangle is swept in every mode - it is run backwards, never reflected (the
field itself sits 7 px left of centre; see `ScanFieldLeft`). ACW is the
"inverse scrolling" reading. Uniquely among effectors, the manuals note scroll
direction can *help* a player's score: right-handed players anchor the purple
repeat lines with the left hand and melody with the right like a piano, and a
reversed bottom half stops the hands from tangling there.

## 5. What the Studio preview now models

Geometry and chromes: `TechnikaPlayfieldMetrics` (1280×768 arcade frame,
960 px shared field, sidebars, divider, header), `TechnikaPlayfieldTheme`,
`TechnikaNoteSprites` (extracted arcade art, packaged fallback glyphs).

| Rule | Where it lives |
|---|---|
| Scan/half/lane/kind resolution, chains, repeats, end-of-scan markers, fader distance | `GameplayPreviewProjector` (Shared) |
| Note placement under CW/ACW/LL/RR | `PlaceTechnikaNote` + `TechnikaSweepRightward`; chosen at projection time via `Project(model, profile, scrollDirection)` |
| Sweep rendering, quad trailing edge, both-lines handover, blink/blind | `TechnikaPlayfieldView.DrawScanline(s)` |
| Hold/drag/repeat trails per scan incl. cross-divider holds, cap orientation | `DrawTrail` / `DrawTrailSegment` (direction-aware) |
| Chain paths and the head arrow aimed at the next member | `GroupRuns` / `DrawChainLink` |
| Approach glow entering from the edge the sweep comes from | `DrawApproach` (direction-aware) |
| Note opacity: next-scan Prepare (0.6) × fader ramp | `DrawNote` / `DrawGroupLink` + `FaderOpacityFor` |
| Hit bursts after the sweep | `TechnikaHitFlash` / `TechnikaHitEffectProfile` |
| 3-2-1 musical count-in | `TechnikaCountIn` |
| Effectors UI | playfield panel strip: SCROLL (reprojects), NOTES (fader), LINE (blink/blind), GUIDES (measurement aids) |

Fader and blink are render-only state on the view; scroll direction moves
notes inside their scans and therefore rebuilds the projection (cheap - a
single pass over the chart).

## 6. Not modelled yet (deliberate or deferred)

* **Variable beat-per-scan charts** (2/5/6/8/12-beat sweeps). The projector
  assumes 4. Doing this honestly means taking scan boundaries from the
  tracks 4–7 marker events rather than from the pulse grid.
* **DUO player 2** (tracks 8–10): reported as a projection diagnostic; how a
  second field should be presented is a design decision.
* **Judgement, groove gauge, combo, score and fever** - no interactive
  playback; the HUD stays a plate.
* **Tune rear-touch notes** (rear hold / rear repeat): Vita-only mechanics
  with no equivalent arcade chart data.
* Fader windows and blink duties are tuned, not measured (below): the
  arcade's exact fade distances per FI vs FI2 are not in any source found.
* Hit bursts are drawn regardless of fader (judgement feedback rather than a
  note); under FO a burst can read at an otherwise empty line.

## 7. Assumptions (sources could not confirm the number)

1. **Fader distance**: FI/FO complete across ~0.9 of a scan, the "2" variants
   across ~0.45. Sources confirm only that 2 is stronger and that the fade is
   position-based with no exact pixel/beat distance.
2. **Blink timing**: a half-scan blink period with 50% (BK) / 25% (BK2) duty,
   taken from the manual's "about 50%" / "25%" descriptions; both lines flash
   in phase during a handover.
3. **Prepare opacity 0.6** and **handover 87.5%** come from the reference
   client's own draw lifecycle (already documented in code) rather than a
   source; they are retained.
4. Scroll direction reuses the same authored notes - the arcade changes sweep
   direction, not chart data, consistent with missions applying it to
   unmodified charts.

## Sources

* [1] DJMAX Technika Q - gameplay and note gestures: https://djmax.fandom.com/wiki/DJMAX_Technika_Q
* [2] Codex Gamicus, DJ Max Technika (mirrors of the pre-shutdown arcade wikis): https://gamicus.gamepedia.com/DJ_Max_Technika and https://gamicus.fandom.com/wiki/DJ_Max_Technika
* [3] Cypher Gate Wiki, DJMAX Technika (mix patterns): http://cyphergate.net/index.php?title=DJMAX_Technika
* [4] Giant Bomb, DJMax Technika (cabinet, field, note colours/gestures, gauge): https://www.giantbomb.com/djmax-technika/3030-27806/
* [5] IGN Wiki, DJ Max -- Technika guide (Tune note names, rear touch): https://www.ign.com/wikis/dj-max-technika
* [6] Wikipedia, DJMax Technika (loop, breaks, HP, patterns): https://en.wikipedia.org/wiki/DJMax_Technika
* [7] (merged into [6] numbering)
* [8] Inside Pulse review, DJMax Technika Tune (modifier trio, note descriptions): https://insidepulse.com/2013/01/14/review-djmax-technika-tune-sony-playstation-vita/
* [9] Pocket Gamer review, Technika Tune (split sweep description): https://pocketgamer.com/articles/047358/djmax-technika-tune
* [10] NamuWiki, DJMAX TECHNIKA (verdicts, score tables, mission effector combos): https://en.namu.wiki/w/DJMAX%20TECHNIKA
* [11] NamuWiki, DJMAX TECHNIKA 2 / How to Play & System (effector families): https://en.namu.wiki/w/DJMAX%20TECHNIKA%202/%ED%94%8C%EB%A0%88%EC%9D%B4%20%EB%B0%A9%EB%B2%95&%EC%8B%9C%EC%8A%A4%ED%85%9C
* [12] NamuWiki, DJMAX TECHNIKA 3 / How to Play & System (note gestures, note skins, effectors, scroll lengths): https://en.namu.wiki/w/DJMAX%20TECHNIKA%203/%ED%94%8C%EB%A0%88%EC%9D%B4%20%EB%B0%A9%EB%B2%95%20&%20%EC%8B%9C%EC%8A%A4%ED%85%9C
