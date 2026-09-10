# RESPECT V playfield research: 4B / 5B / 6B / 8B

Why this file exists: the gameplay preview drew the RESPECT gear wrong in every mode
that has more than plain lanes - shoulder inputs as narrow cyan lanes overlapping the
mains, side tracks as thin red/purple rails, 8B as eight equal lanes. The owner asked
for deep research before touching it. This is the research: one table per mode, the
track mapping the trailer charts use, and the rendering rules the preview now follows.
Every claim is pinned to a source; the assumptions the sources could not answer are
listed as assumptions at the end, not smuggled into the tables.

## The gear, all modes

The gear is one vertical lane core. Lanes are read left to right; notes fall onto a
judgement deck at the bottom. Note colour carries no gameplay meaning - it exists so
the eye can tell lanes apart at speed [1](https://www.reddit.com/r/djmax/comments/cgh0u4/so_what_do_the_note_colors_mean/).

The lane striping, confirmed independently by the PS4 launch community and the Steam
guide:

| Gear lane (left to right) | 1 | 2 | 3 | 4 | 5 | 6 |
|---|---|---|---|---|---|---|
| Colour | white | **blue** | white | white | **blue** | white |
| PS4 input (6B) | Left | Up | Right | Square | Triangle | Circle |

"UP and TRIANGLE buttons control middle lanes and the notes on these lanes are always
blue to help the eye distinct the notes from each other"
[2](https://gamefaqs.gamespot.com/boards/190197-djmax-respect/76454304).
The outer four lanes are silver-white; [3](https://psnprofiles.com/guide/7372-djmax-respect-trophy-guide)
adds that on 8B the L1/R1 shoulder notes are always pink.

Modes are subsets and supersets of this one 6-lane gear - 4B does not renumber the
lanes, it leaves two of them empty.

## 4B

| | |
|---|---|
| Lanes | 4: gear lanes 1, 2, 5, 6. Both middle lanes unused |
| Colours | white, **blue**, **blue**, white |
| PC keys | A, S, `;`, `'` |
| PS4 inputs | Left, Up, Triangle, Circle |

"If you play 4 button mode then both middle lanes and Left/Square buttons for middle
lanes aren't used" [2](https://gamefaqs.gamespot.com/boards/190197-djmax-respect/76454304);
the Steam guide's "4 different lanes" with side tracks on Shift
[4](https://steamcommunity.com/sharedfiles/filedetails/?id=2362889860).

## 5B

| | |
|---|---|
| Lanes | 5: gear lanes 1, 2, (3+4 shared), 5, 6 |
| Colours | white, **blue**, white, **blue**, white |
| PC keys | A, S, D, L, `;`, `'` (six keys, five lanes) |
| PS4 inputs | Left, Up, Right-or-Square, Triangle, Circle |

"The middle lane is shared by 2 keys simultaneously"
[4](https://steamcommunity.com/sharedfiles/filedetails/?id=2362889860).
The shared middle lane takes the colour of the gear lanes it covers (white), keeping
the Up/Triangle blue pair at lanes 2 and 4. 5B adds Square-or-Right to 4B; 6B
separates them [5](https://operationrainfall.com/2018/04/25/review-djmax-respect/).

## 6B

| | |
|---|---|
| Lanes | 6: the whole gear |
| Colours | white, **blue**, white, white, **blue**, white |
| PC keys | A, S, D, J, K, L (default V layout) |
| PS4 inputs | Left, Up, Right, Square, Triangle, Circle |

The base layout everything else is described against
[2](https://gamefaqs.gamespot.com/boards/190197-djmax-respect/76454304)
[5](https://operationrainfall.com/2018/04/25/review-djmax-respect/).

## 8B

| | |
|---|---|
| Lanes | 6 mains (the 6B gear) + 2 shoulder bars |
| Mains | exactly the 6B striping above |
| Shoulders | L1 (left half) and R1 (right half), always pink |

The shoulder inputs are **not** two more narrow lanes. They are wide bars, each
spanning the three lanes of its half ("L1 ... span the 3 left/right lanes")
[3](https://psnprofiles.com/guide/7372-djmax-respect-trophy-guide), drawn
**underneath** the ordinary notes [3](https://psnprofiles.com/guide/7372-djmax-respect-trophy-guide).
PC defaults put them on C and `,` ("two thin pink FX lanes")
[4](https://steamcommunity.com/sharedfiles/filedetails/?id=2362889860); PS4 plays them
with the index fingers on the triggers ("with index fingers resting on the triggers"
[6](https://gamefaqs.gamespot.com/boards/190197-djmax-respect/77624792)), which is why
players also call them the thumb notes on a keyboard rebind
("pink FX bars on thumbs" [7](https://www.reddit.com/r/djmax/comments/nrt4z7/who_else_had_to_relearn_8b_on_respect_v/)).

8B is therefore a 6-lane gear with two overlay bars, never an 8-lane gear. The preview
used to derive eight equal lanes from the eight note-carrying tracks; that was the
most visible half of "so wrong".

## Side tracks (all modes)

Every button mode has two side-track inputs alongside the mains - Shift keys on PC in
4B/5B/6B/8B alike [4](https://steamcommunity.com/sharedfiles/filedetails/?id=2362889860),
analog sticks on PS4. They descend from the PSP rainbow/analog note, which PS4 Respect
split in two and recoloured "a pulsing greenish blue"
[8](https://www.reddit.com/r/djmax/comments/1brrgue/why_do_sidetrack_buttons_have_to_have_dedicated/);
Respect V kept the charts, dropped the stick-spin warning animation and added a SIDE
TRACK warning instead [8](https://www.reddit.com/r/djmax/comments/1brrgue/why_do_sidetrack_buttons_have_to_have_dedicated/).

Rendering rules for side notes:

- **Teal/green long bars**, one per half: "teal long notes that take up either half of
  the playfield" [9](https://tvtropes.org/pmwiki/pmwiki.php/YMMV/DJMAX) (PS4 analog
  notes, whose charts V plays unchanged). Same wide-bar construction as the pink
  shoulder bars, in teal.
- They are holds ("adds to your combo like a hold note"
  [10](https://www.reddit.com/r/djmax/comments/7mehv1/the_analogue_sticks/); "just hold
  them left or right"
  [6](https://gamefaqs.gamespot.com/boards/190197-djmax-respect/77624792)).
- Chart design rule, load-bearing for the renderer: a side note never coincides with
  lane notes on the same half ("Considering when there's a side track note you aren't
  going to have any other notes on the same side as the side track"
  [11](https://www.reddit.com/r/djmax/comments/eduz5r/tried_to_like_the_game_but_i_cant_find_a/);
  "side note never simultaneous with same-side lane notes"
  [7](https://www.reddit.com/r/djmax/comments/nrt4z7/who_else_had_to_relearn_8b_on_respect_v/)).
  The wide bars can therefore sit underneath the mains without ever hiding one.

The preview used to draw sides as thin red (left) / purple (right) rails at the lane
positions they overlap. No source supports any of that; it was invented.

## Trailer track mapping

RESPECT V trailer charts carry gameplay on fixed DPC track ids, which the editor's
own `TrackPresetLibrary` fallback and `InferRespectTrackChannels` already agreed on
before this research:

| Track id | Meaning |
|---|---|
| 2 | SIDE L |
| 3..8 | the six gear lanes, left to right |
| 9 | SIDE R |
| 10 / 11 | L1 / R1 (shoulder bars) |
| 12 / 13 | L2 / R2 (XB missions only) |

4B charts use tracks 3..6, 5B 3..7, 6B 3..8, 8B 3..8 + 10..11. Track id order within
3..8 is gear order - no remapping.

## Mission-only variants

- **4BFX / 5BFX**: 4 or 5 mains plus the pink shoulder bars (Clazziquai DLC missions).
  The projector used to read any chart with notes on 10/11 as 8B, promoting a 4BFX
  chart onto the 6-lane pitch with two ghost lanes. Mains and shoulders are now
  inferred separately: the mode comes from the mains alone.
- **XB**: 8B plus L2/R2 notes on 12/13 (a handful of mission charts). Rendered as pink
  shoulder bars - see assumptions.

## What the preview draws now

- Mains: flat white/blue bars per the striping table, on the mode's own pitch
  (120/96/80 native units for 4/5/6 mains).
- Shoulder (FX) bars: hot pink, each spanning from its half's outer lane edge to the
  centre, drawn **under** the mains.
- Side-track bars: teal, same wide-bar construction, drawn under mains and shoulders.
- Layering bottom to top: lane glass, side bars, shoulder bars, main notes, deck and
  judgement line. Holds get a tail cap at the far end.
- The SIDE TRACK approach warning the game flashes is HUD, not chart, and is not drawn.

## Assumptions (sources silent, revisit if footage disagrees)

1. **L2/R2 rendering.** No source describes what XB's L2/R2 notes look like. Drawn as
   the same pink shoulder bars on the same halves - they are shoulder inputs on the
   same halves, and XB is ~5 charts. If they stack with L1/R1 they are
   indistinguishable; that overlap is accepted, not solved.
2. **Side-bar width on V specifically.** The half-playfield width is attested for the
   PS4 analog notes [9](https://tvtropes.org/pmwiki/pmwiki.php/YMMV/DJMAX); V plays the
   same charts with the spin animation removed [8](https://www.reddit.com/r/djmax/comments/1brrgue/why_do_sidetrack_buttons_have_to_have_dedicated/),
   so the bars are assumed unchanged. If V narrowed them to edge tabs, only the side
   width constant moves.
3. **FX-vs-side stacking order.** Both span the same half and both sit under the
   mains; sides go under shoulders. Charts never stack them (see the design rule
   above), so the order is nearly unobservable.
4. **5B middle-lane colour.** White, from the gear lanes it covers. No source shows a
   5B gear close up contradicting the W-B-W-B-W reading the Up/Triangle rule implies.
5. **Shoulder-bar vertical extent.** Heads are note-height bars; holds extend like any
   long note. The exact cap art is the Shino-Tokuu extraction's when present,
   re-derived flat bars otherwise.
