# Mix (#48): reference data (presets, styles, curves, recipe, Auto)

Status: draft, 2026-11-25. Target: the release lines from `release/0.4.0` on (the data behind
[`48-mix-full-support.md`](48-mix-full-support.md)).

What this covers: the official client's Mix data model, as Wavee must reproduce it. The 22 presets and the styles each
one expands to, every style enum, the map from a curve value to dB, Hz or percent, the curve and recipe messages, the
`set-transition` payload, the limits, and what the client's own Auto does. The wire evidence (requests, samples,
published state) is in [`48-mix-protocol.md`](48-mix-protocol.md).

Sibling documents:
- [`48-mix-full-support.md`](48-mix-full-support.md): the plan (design, phases M1 to M8, open questions).
- [`48-mix-protocol.md`](48-mix-protocol.md): the wire, as the official client speaks it.
- [`48-mix-implementation.md`](48-mix-implementation.md): the 0.4 design (its revision 6 note says what these files replace).
- [`48-mix-ux-direction.md`](48-mix-ux-direction.md): the UX source of truth.
- [`dj-implementation.md`](dj-implementation.md), [`dj-protocol.md`](dj-protocol.md): AI DJ, which uses the same fade machinery.

Source: the official desktop client 1.3.10 (style table version 8), 2026-11-25: its own mixing tables and the Auto
answers it computed for 229 adjacent track pairs. The full tables (every curve at 1, 2, 4, 8, 16, 32 and 100 bars, the
effect curves, the looping blocks) and the 229-pair dataset are kept outside the repo with the raw captures (the dataset
holds account data). Wavee code references are to `release/0.4.0` at `0a45dbfbc`, paths relative to `src/apps/Wavee/`.

Tags: **[client]** = the official client's own data or logic, not seen on the wire; **[wire]** = seen in a capture
([`48-mix-protocol.md`](48-mix-protocol.md)); "n of n" = an observed count; **open** = not known.

---

## 1. Presets

### 1.1 What each preset expands to [client]

A preset is a set of style ids, one per ingredient. The client's table also names a jog-wheel and a looping style per
preset; those two columns are not copied here.

| id | Enum | UI name | Volume | EQ | Filter | Effect |
|---|---|---|---|---|---|---|
| 1 | SimpleFade | Fade | CrossShape | BassSwap | None | None |
| 2 | EqSwap | Blend | FullOverlap | EqSwap | None | None |
| 3 | BassSwap | Rise | FullOverlap | BassSwapAtEnd | LowPassFilterInFilterOut | None |
| 4 | BassSwapFilterIn | Melt | SlowInSlowOut | BassSwap | FilterInFilterOut | None |
| 5 | BassSwapFilterOut | (internal) | SlowInFastOutAtEnd | BassSwap | FilterOut | None |
| 6 | BassSwapEcho | (internal) | FullOverlap | BassSwapAtEnd | LowPassFilterInFilterOut | None |
| 7 | BassAndMidSwap | (internal) | SlowInSlowOut | BassAndMidSwap | None | None |
| 8 | FilterFadeOut | (internal) | SlowInFastOutAtEnd | BassSwap | FilterOut | None |
| 9 | FilterFadeIn | (internal) | FastInAtStartSlowOut | BassSwap | FilterIn | None |
| 10 | FilterFadeInOut | (internal) | FullOverlap | BassSwap | FilterInFilterOut | None |
| 11 | SimpleCut | Slam | Cut | None | None | None |
| 12 | ReverbCut | (internal) | Cut | None | None | None |
| 13 | RiserCut | (internal) | Cut | None | None | None |
| 14 | ReverbRiserCut | (internal) | Cut | None | None | None |
| 15 | EchoCut | (internal) | Cut | None | None | None |
| 16 | BassSwapAtEnd | (internal) | SlowInFastOutAtEnd | BassSwapAtEnd | None | None |
| 17 | BassSwapLowPassFilterIn | Wave | FullOverlap | BassSwap | LowPassFilterInLowPassFilterOut | None |
| 18 | BassSwapLowPassFilterOut | (internal) | SlowInFastOutAtEnd | BassSwap | LowPassFilterOut | None |
| 19 | LowPassFilterFadeInOut | (internal) | FullOverlap | BassSwap | LowPassFilterInLowPassFilterOut | None |
| 20 | Dissolve | Dissolve | SlowInFastOutAtEnd | BassSwapAtEnd | HighPassFilterHalfOut | EchoHalfOutEnd |
| 21 | Surge | Surge | SlowInFastOutAtEnd | BassSwapAtEnd | HighPassFilterHalfOut | ReverbOutEnd |
| 22 | Afterglow | Afterglow | CrossShape | BassSwap | None | ReverbOutCentre |

- Presets 17 (Wave) and 19 list the same three styles. Preset 19 is the one Auto picks most often (§7.2).
- "No transition" is no preset at all: recipe field 2.1 is absent (§4.3).
- Internal presets never appear as chips, but they occur in Auto and may occur in stored recipes. Their curves are
  tabled like the others, so Wavee renders them. A preset id above 22 renders as Fade.

### 1.2 What the editor offers, in order [client]

Style table version 8 (remote config `desktop-ui.playlistMixingAvailableStylesVersion = 8`). The UI names are the
client's own English strings.

| Order | Enum | Wire id (2.1) | UI name |
|---|---|---|---|
| 1 | SimpleFade | 1 | Fade |
| 2 | BassSwap | 3 | Rise |
| 3 | EqSwap | 2 | Blend |
| 4 | BassSwapLowPassFilterIn | 17 | Wave |
| 5 | BassSwapFilterIn | 4 | Melt |
| 6 | SimpleCut | 11 | Slam |
| 7 | Dissolve | 20 | Dissolve |
| 8 | Surge | 21 | Surge |
| 9 | Afterglow | 22 | Afterglow |
| 10 | None | absent | No transition |

- Table versions 1 to 4 offer only the first six presets. Dissolve, Surge and Afterglow arrive with version 6 (the
  "new effects"), looping with version 8.
- Ids 1, 2, 3, 4, 11, 17, 21 and 22 were also proven on the wire against the client's UI analytics; Dissolve's chip was
  never clicked in a capture, its id 20 comes from the client's table ([`48-mix-protocol.md`](48-mix-protocol.md) §4.3).
- The other UI labels: "Auto", "Custom" (a preset whose styles or curves were changed), "No transition".

### 1.3 Ingredients: the styles under Customize [client]

Each style is listed as `Enum (id) "UI label"`. Styles marked internal are never offered but may appear in presets.

| Ingredient | Offered styles | Internal | Curves |
|---|---|---|---|
| **Volume** | CrossShape (6) "Smooth crossfade", FullOverlap (7) "Overlap", SlowInSlowOut (5) "Fade in fade out", FastInAtStartSlowOut (10) "Cut in fade out", SlowInFastOutAtEnd (9) "Fade in cut out", Cut (1) "Center cut", Crossfade (2) "Crossfade", SlowInSemiFastOutAtEnd (11) "Fade in fast out" | FastInSlowOut (4), SlowInFastOut (3), Switcheroo (8) | volume out, volume in |
| **EQ** | BassSwap (4) "Center bass swap", BassSwapAtEnd (10) "End bass swap", BassSwapAtStart (11) "Start bass swap", EqSwap (1) "3-band fade", BassCuts (12) "Quick bass cut", NoBass (13) "Long bass cut", StartFadeOut (14) "Start fade out", BassFadeOut (15) "Bass fade out", None (0) | BassAndMidSwap (7), BassFadeIn (2), BassCrossfade (3), MidSwap (8), HiSwap (9) | low, mid, high gain, out and in |
| **Filter** | LowPassFilterOut (10), LowPassFilterIn (9), LowPassFilterInLowPassFilterOut (11), LowPassFilterInFilterOut (13, low-pass in, high-pass out), FilterOut (6, high-pass), FilterIn (5, high-pass), FilterInFilterOut (7, high-pass in and out), FilterInLowPassFilterOut (12, high-pass in, low-pass out), HighPassFilterHalfOut (14), NoiseOutEnd (15), None (0) | Echo (1), Reverb (2), Riser (3), ReverbRiser (4), EchoAtEnd (8) | cutoff, resonance, noise colour, out and in |
| **Effects** (table version 6 and up) | ReverbOutCentre (1), ReverbCutEnd (2), ReverbOutEnd (3), EchoHalfCutEnd (4), EchoHalfOutEnd (5), Echo3QCutEnd (6), Echo3QOutEnd (7), Echo1CutEnd (10), Echo1OutEnd (11), DelayHalfCutEnd (8), Delay3QCutEnd (9), None (0) | | echo, reverb and delay dry/wet, out and in |
| **Looping** (table version 8) | OneBeat (8), TwoBeat (1), FourBeat (2), EightBeat (3), SixteenBeat (4), HalvingEnd (5), None (0); labels "1 beat loop" … "Loop halving" | | loop blocks (§3.2) |
| **Jog wheel** | preset field 2.15 exists (§4.1); **open**: no jog-wheel style list was obtained | | |

- The UI labels of the filter styles were not obtained (**open**); the names above are the enum names.
- The official client marks effects and looping as beta and offers them behind an account preference
  (`mixing-fx-looping-jogwheel`). **Wavee offers them always** (owner decision, 2026-11-25) and does not read that
  preference.

---

## 2. Enums [client]

- **preset**: Unknown=-1, None=0, SimpleFade=1, EqSwap=2, BassSwap=3, BassSwapFilterIn=4, BassSwapFilterOut=5,
  BassSwapEcho=6, BassAndMidSwap=7, FilterFadeOut=8, FilterFadeIn=9, FilterFadeInOut=10, SimpleCut=11, ReverbCut=12,
  RiserCut=13, ReverbRiserCut=14, EchoCut=15, BassSwapAtEnd=16, BassSwapLowPassFilterIn=17, BassSwapLowPassFilterOut=18,
  LowPassFilterFadeInOut=19, Dissolve=20, Surge=21, Afterglow=22
- **volume**: Unknown=-1, None=0, Cut=1, Crossfade=2, SlowInFastOut=3, FastInSlowOut=4, SlowInSlowOut=5, CrossShape=6,
  FullOverlap=7, Switcheroo=8, SlowInFastOutAtEnd=9, FastInAtStartSlowOut=10, SlowInSemiFastOutAtEnd=11
- **eq**: Unknown=-1, None=0, EqSwap=1, BassFadeIn=2, BassCrossfade=3, BassSwap=4, BassAndMidSwap=7, MidSwap=8, HiSwap=9,
  BassSwapAtEnd=10, BassSwapAtStart=11, BassCuts=12, NoBass=13, StartFadeOut=14, BassFadeOut=15
- **filterFx**: Unknown=-1, None=0, Echo=1, Reverb=2, Riser=3, ReverbRiser=4, FilterIn=5, FilterOut=6,
  FilterInFilterOut=7, EchoAtEnd=8, LowPassFilterIn=9, LowPassFilterOut=10, LowPassFilterInLowPassFilterOut=11,
  FilterInLowPassFilterOut=12, LowPassFilterInFilterOut=13, HighPassFilterHalfOut=14, NoiseOutEnd=15
- **fx**: Unknown=-1, None=0, ReverbOutCentre=1, ReverbCutEnd=2, ReverbOutEnd=3, EchoHalfCutEnd=4, EchoHalfOutEnd=5,
  Echo3QCutEnd=6, Echo3QOutEnd=7, DelayHalfCutEnd=8, Delay3QCutEnd=9, Echo1CutEnd=10, Echo1OutEnd=11
- **looping**: Unknown=-1, None=0, TwoBeat=1, FourBeat=2, EightBeat=3, SixteenBeat=4, HalvingEnd=5, OneBeat=8
- **preset type** (recipe 2.2): 1 FADE, 2 CUT, 3 BEATMATCHED_FADE, 4 BEATMATCHED_CUT
- **beatmatchPreference** (recipe top-level 5): 0 AUTO, 1 OFF, 2 ON
- **loop block type**: 1 LOOP, 2 SPINBACK, 3 VINYL_STOP

---

## 3. Curves

### 3.1 Where curves come from [client]

- **The client computes every curve** from the style id and the transition's length in bars; effect curves also depend
  on A's tempo. No network call carries a curve ([`48-mix-protocol.md`](48-mix-protocol.md) §10).
- The tables kept outside the repo hold: every volume, EQ and filter style at 1, 2, 4, 8, 16, 32 and 100 bars; the
  effect dry/wet curves of every effect style at 60, 90, 120, 150 and 180 bpm × 2, 4, 8 and 16 bars; the loop blocks
  of every looping style.
- **Open:** the curves at bar counts between the tabled ones, if the editor offers such lengths.
- What a playing transition publishes over Connect, and the six seams that check these tables against the player:
  [`48-mix-protocol.md`](48-mix-protocol.md) §6.

### 3.2 Messages [client]

```
Curves { 1[] Curve; 3 minimum double; 4 maximum double }
Curve  { 1[] Point; 2 start double; 3 end double }          // one segment over start..end of the window
Point  { 1 x double; 2 y double }
Block  { 1 type (1 LOOP, 2 SPINBACK, 3 VINYL_STOP); 2 beats float; 3 start float; 4 end float }
```

Which looping styles use the SPINBACK and VINYL_STOP block types is **open**.

Also **open**: whether a block loops the beats *starting at* its `start` fraction or the beats *before* it. Wavee (M4)
starts each block on A's beat nearest its window fraction and skips a block whose loop is longer than its span; under
that reading SixteenBeat does nothing in a 4-bar window. Settle it by ear against the official client.

### 3.3 The curve-editing model [client]

This is what Customize edits and what the curve overrides (§4.1, fields 2.6 to 2.14) store.

- A curve is a list of points `{position 0..1, value 0..1, smoothAfter, bendAfter}`.
- Each segment between two points is a cubic Bézier. Its control points sit at 1/3 and 2/3 of the value span, plus
  `bend × 2/3`; the bend is clamped so the controls stay in [0, 1].
- `smoothAfter = false` with bend 0 is a straight line.
- Points closer than 0.015 merge.

---

## 4. Value semantics: from a curve value to sound [client]

### 4.1 The client's semantic entries, verbatim

Volume:
`[{"kind": 1, "outputUnit": 1, "base10LogRatio": {"rawReference": 1, "outputMultiplier": 20}, "specialCases": [{"rawValue": 0, "result": {"role": 3}}, {"rawValue": 1, "result": {"numericValue": 0}}]}]`

EQ:
`[{"kind": 2, "outputUnit": 1, "piecewiseInterpolation": {"segments": [{"rawStart": 0, "rawEnd": 0.5, "outputStart": -48, "outputEnd": -1.5, "signedGeometric": {}, "noEasing": {}, "outputMode": 0}, {"rawStart": 0.5, "rawEnd": 1, "outputStart": 0.638943076133728, "outputEnd": 6, "signedGeometric": {}, "noEasing": {}, "outputMode": 0}]}, "specialCases": [{"rawValue": 0, "result": {"role": 4}}, {"rawValue": 0.5, "result": {"numericValue": 0}}]}]`

Filter (cutoff, then noise colour):
`[{"kind": 3, "outputUnit": 2, "piecewiseInterpolation": {"segments": [{"rawStart": 0, "rawEnd": 0.5, "outputStart": 120, "outputEnd": 16000, "signedGeometric": {}, "noEasing": {}, "outputMode": 1}, {"rawStart": 0.5, "rawEnd": 1, "outputStart": 20, "outputEnd": 9000, "signedGeometric": {}, "noEasing": {}, "outputMode": 2}]}, "specialCases": [{"rawValue": 0.5, "result": {"role": 1}}]}, {"kind": 5, "outputUnit": 2, "piecewiseInterpolation": {"segments": [{"rawStart": 0, "rawEnd": 0.5, "outputStart": 100.48416305247163, "outputEnd": 29436.773229308717, "signedGeometric": {}, "powerIn": {"exponent": 1.7799999713897705}, "outputMode": 0}, {"rawStart": 0.5, "rawEnd": 1, "outputStart": 37.33756354910836, "outputEnd": 15214.43784823654, "signedGeometric": {}, "powerOut": {"exponent": 1.7799999713897705}, "outputMode": 0}]}, "specialCases": [{"rawRange": {"minimum": 0.4950000001117587, "maximum": 0.5049999998882413}, "result": {"role": 2}}]}]`

Effect dry/wet:
`[{"kind": 4, "outputUnit": 3, "piecewiseInterpolation": {"segments": [{"rawStart": 0, "rawEnd": 1, "outputStart": 0, "outputEnd": 100, "linear": {}, "noEasing": {}, "outputMode": 0}]}, "specialCases": [{"rawValue": 0, "result": {"role": 2}}]}]`

### 4.2 How the client evaluates them

- **Kinds:** 1 LinearGain, 2 EqGain, 3 FilterCutoff, 4 DryWet, 5 NoiseColor. Resonance has no semantic entry (kind 5 is
  noise colour, not resonance). **Open:** how resonance values map to Q.
- **Units:** 1 dB, 2 Hz, 3 %. **Roles:** 1 Bypass, 2 Off, 3 Mute, 4 Kill (Mute and Kill show as −∞ dB).
- Clamp the raw value to the curve's `[minimum, maximum]` (§3.2). A matching special case wins: `rawValue` means
  equal, `rawRange` means strictly inside.
- `base10LogRatio`: `out = outputMultiplier × log10(raw / rawReference)`.
- `piecewiseInterpolation`: take the segment with `rawStart ≤ raw < rawEnd` (else the last one);
  `t = (raw − rawStart) / (rawEnd − rawStart)`; ease `t` (`powerIn`: `t^exponent`; `powerOut`: `1 − (1 − t)^exponent`;
  otherwise `t`); then `signedGeometric`: `out = outputStart × (outputEnd / outputStart)^t`, or `linear`:
  `out = outputStart + t × (outputEnd − outputStart)`.
- `outputMode`: 1 = low-pass, 2 = high-pass. The kind 5 (noise colour) segments have `outputMode` 0, so the client's
  entry does not say which filter colours the noise. Wavee reads it like the cutoff lane: low-pass below 0.5, high-pass
  above; 0.495–0.505 is off. The client defines no noise level either; Wavee uses −20 dBFS RMS of the white noise
  before the colour filter (M4). Both are to be checked by ear.

### 4.3 The result, lane by lane

| Lane | Raw value | Sound |
|---|---|---|
| Volume | raw | `20 × log10(raw)` dB; 0 = mute; 1 = 0 dB |
| EQ gain | 0 | kill (−∞ dB) |
| | 0 < raw < 0.5 | `−48 × (1.5 / 48)^(raw / 0.5)` dB (0.25 → −8.49 dB) |
| | 0.5 | 0 dB |
| | 0.5 < raw ≤ 1 | `0.638943 × (6 / 0.638943)^((raw − 0.5) / 0.5)` dB (up to +6 dB) |
| Filter cutoff | raw < 0.5 | low-pass at `120 × (16000 / 120)^(raw / 0.5)` Hz |
| | 0.5 | bypass |
| | raw > 0.5 | high-pass at `20 × (9000 / 20)^((raw − 0.5) / 0.5)` Hz |
| Noise colour | 0.495 < raw < 0.505 | off |
| | otherwise | Hz by the kind 5 entry above (`powerIn` below 0.5, `powerOut` above) |
| Effect dry/wet | raw | `100 × raw` %; 0 = off |

Wavee's mapping on `release/0.4.0` (`Entities/Mix.Curves.cs:180-193`) differs: a 200 Hz shelf floored at −26 dB, a
20 kHz low-pass top and a 6 kHz high-pass top. The full-support plan replaces it in M1.

---

## 5. TransitionRecipe

The value of the item attribute `automix.auto_transition_recipe` (base64), field 9 of an `automix-preview`, and field 1
of a `set-transition` payload.

### 5.1 Schema [client]

```
TransitionRecipe {
  1 overlap Overlap
  2 preset Preset
  3 isOverlapOverride bool
  4 isPresetIdOverride bool
  5 beatmatchPreference enum (0 AUTO, 1 OFF, 2 ON)
}
Overlap {
  1 trackARowId string           2 trackBRowId string            // the playlist item ids, 16 lowercase hex
  3 startAMs int32               4 startBMs int32                // where the overlap starts in A; where B starts
  5 durationMs int32                                             // the overlap
  6 speedA float                 7 speedB float                  // beatmatch speed
  8 durationBars int32           9 isBeatmatched bool
  10 trackAUri string            11 trackBUri string
  12 trackAPlayableUri string    13 trackBPlayableUri string
  14 trackAPlayableBeatsHash string   15 trackBPlayableBeatsHash string   // = kind 217 field 3 of A and B
  16 bpmA float                  17 bpmB float
  18 itemSpeedA double           19 itemSpeedB double
}
Preset {
  1 id int32 (preset enum)       2 type enum (1 FADE, 2 CUT, 3 BEATMATCHED_FADE, 4 BEATMATCHED_CUT)
  3 volumeStyleOverride {1 id}   4 eqStyleOverride {1 id}        5 filterFxStyleOverride {1 id}
  6 volumeOutCurveOverride Curves                                7 volumeInCurveOverride Curves
  8 eqOutCurveOverrides {1 low, 2 mid, 3 high: Curves}           9 eqInCurveOverrides
  10 filterOutCurveOverrides {1 cutoff, 2 resonance, 3 noiseColor: Curves}   11 filterInCurveOverrides
  12 fxStyleOverride {1 id}
  13 fxOutCurveOverrides {1 echoDryWet, 2 reverbDryWet, 3 delayDryWet: Curves}   14 fxInCurveOverrides
  15 jogwheelStyleOverride {1 id}   16 loopingStyleOverride {1 id}
  17 loopingOutBlocksOverride {1[] Block}
}
```

### 5.2 What the 20 captured recipes show [wire]

The samples and the evidence are in [`48-mix-protocol.md`](48-mix-protocol.md) §4.

| Field | Observed |
|---|---|
| order and types | ascending field order; floats are fixed32, 18 and 19 fixed64 |
| 1.1, 1.2 | present in stored recipes and every preview; absent in the "baseline" recipe of a save |
| 1.3, 1.4 | omitted when 0 |
| 1.4 | B's in-point; a click on B's waveform in the editor becomes 1.4 directly |
| 1.6 | 1.0 in all 20 |
| 1.7 | 1.0 when not beatmatched; 1.0000801 and 1.0001602 on the one beatmatched pair (how the speed is chosen: §7.2) |
| 1.8 | **always written**, also when 0: 0 on the unmatched pair, 2 on the beatmatched pair |
| 1.9 | 1 on the beatmatched pair, absent otherwise |
| 1.10 / 1.12, 1.11 / 1.13 | the same uri twice in every sample |
| 1.16, 1.17 | beatmatched: both equal to the tempo that makes `durationBars × 4` beats fill `durationMs`; not beatmatched, and in every baseline: the kind 222 tempo of each track |
| 1.18, 1.19 | 1.0 in all 20 |
| 2.2 | 1 (FADE) on the unmatched pair, 3 (BEATMATCHED_FADE) on the beatmatched pair, in every recipe of that pair, also with Slam and with "No transition" |
| 2.4 | `{1 15}` (Bass fade out) after that EQ was picked |
| top 3 | present (1) in every recipe except the "none" recipe |
| top 4 | present (1) in stored, baseline and chip-preview recipes; absent only in previews of an untouched Auto transition |
| top 5 | never seen |

### 5.3 The "none" recipe ("No transition") [wire, one sample]

Overlap 1 ms; no 1.3, 1.4, 1.16 or 1.17; 1.7 = 1.0; 1.8 = 0; no 2.1; 2.2 = 1; no top 3; top 4 = 1. It means no
transition: Wavee plays it as a plain track change, never as a 1 ms Fade.

---

## 6. Saving and resetting [client]

`set-transition` carries this payload as its field 3 (the signal around it: [`48-mix-protocol.md`](48-mix-protocol.md)
§3.2):

```
{ 1 transition       base64 TransitionRecipe   // the new recipe; the server stores exactly this
  2 completed        bool
  3 auto             base64 TransitionRecipe   // the previous transition, when it was Auto
  4 userDefined      base64 TransitionRecipe   // the previous transition, when it was user-defined
  5 userDefinedUri   string }
```

- **Reset to Auto** is the same signal with **no payload**. Not yet seen on the wire.
- **Which previous recipe a save sends:** the client logic sends the stored user-defined recipe in field 4 when there is
  one, else the Auto recipe in field 3. **Open:** in the one capture of a save over a stored recipe, the client sent
  field 3 with the Auto baseline and no field 4 (the editor had also opened that pair on its Auto transition). The other
  captured save (a pair with no stored recipe) sent field 3, as expected.
- Fields 2 and 5 never appeared in the two captured saves either.
- "Duplicate Mix" (a playlist the user does not own) is the playlist service's `copy`; the editor then reopens on the
  copy.

---

## 7. Auto: what the client's own Auto does

The dataset: for 229 adjacent pairs, the transition the client chose and its ranked candidates (each with a
`computedScore` and `beatmatchOverrideAllowed`). Per pair the client returns
`{autoTransition, beatmatchedCandidate, beatmatchOverrideAllowed}`; the last two are null or false exactly when the
pair is not beatmatched. The inputs were recorded for the same tracks: kind 28 cues with their candidate lists, kind
217 beats, kind 222 tempo and key, and the lengths of kinds 218 and 142 (coverage of the 226 tracks: cues 212, beats
211, attributes 208). The dataset's track durations are rounded to whole seconds; Wavee uses kind 10 for exact values.

### 7.1 Not beatmatched: the exact rule (156 of 156 pairs)

| Side | Rule |
|---|---|
| A (outgoing) | `startAMs` = **A's last end candidate (kind 28 field 4, the largest `ms`) − 2500**. The overlap runs to **that candidate + 2500, or to A's end if that comes first**: `durationMs` = min(5000, A's duration − `startAMs`). Running it to A's end without the 5000 ms cap matches only 79 of the 156 pairs on kind 10 durations |
| B (incoming) | starts at 0 (`startBMs` 0). Its published fade-in runs to **B's first start candidate (kind 28 field 3, the smallest `ms`) + 2500** (5 of 5 published seams, [`48-mix-protocol.md`](48-mix-protocol.md) §6.3) |
| Preset | 1 (Fade), type FADE, `durationBars` 0, speeds 1 |

- The common overlaps follow from the candidates: 5000 ms in 77 pairs (a last end candidate 2500 ms before the end),
  2510 ms in 43 (a candidate 10 ms before the end), the rest between 2.5 and 5 s. The 2890 ms of the morning capture's
  unmatched pair is the same rule (a last end candidate 390 ms before the end).
- So A's and B's curves run over different lengths from the same start instant.
- Wavee needs the kind 28 **candidate lists** for this. `Spotify/Spotify.Decode.Mix.cs:78-110` keeps only fields 1 and
  2 today; M3 keeps the smallest start candidate and the largest end candidate per track.

### 7.2 Beatmatched: the exact rule (`mix autocheck`: which pairs 228 of 228, window 72 of 72, speedB bit-exact 72 of 72, preset 72 of 72)

| Step | Rule |
|---|---|
| Downbeats | From the kind 217 grid (ms = round(float(seconds) × 1000f)): each downbeat whose next downbeat is exactly 4 beats on, plus the last one. A downbeat's confidence (beat × downbeat confidence) must be > 0.40 (`auto_transition_downbeat_confidence_threshold`) |
| Windows | 2, 4, 8 or 16 bars (`max_auto_transition_length_bars` 16) from one downbeat to another; shorter than 35 s; bar-duration variance < 0.0005 s². Both tracks get the same list |
| Pairs | A window of A and one of B with the same bar count, lengths within ±10 % (`max_auto_transition_speed_difference_pct` 10) |
| r0 (place) | Let d = the track's duration in s and k = min(0.15, 90 s / d). A: clamp((start/d − 0.5 − 15/d) / (0.5 − k − 15/d)), so 1 when A's window starts within its last k. B: with m = max(0.5 − 15/d, k + 0.01), clamp((end/d − m) / (k − m)), so 1 when B's window ends within its first k. r0 = A × B. A window longer than 30 % of its track (`max_auto_transition_length_fraction_permil` 300) or 35 s scores 0. Pairs with r0 < 0.40 (`auto_transition_base_score_threshold`) are dropped |
| r1 (cue) | ½ (A's fade-out cue score + B's fade-in cue score). Take the best cue (kind 28 field 2 / 1); failing that, the first candidate in answer order (field 4 / 3) that lands. A cue lands within 0.5 s of the window's middle (score 1.0), else its end (0.8), else its start (0.8). A candidate scores 0.95 × that |
| r2 (vocals) | 1 − (number of the window's 32 equal bins where both tracks' mean kind 218 activity (byte/255) is ≥ 0.2) / 32 |
| r3 (key) | Camelot: same, ±1 on the same ring, the relative key, or ±1 across rings: 1. Otherwise 0.9 / 0.8 / 0.6 for ≤ 4 / ≤ 8 / more bars. The client's ±1 is (n + 1) mod 12, so 11→12 does not count |
| r4 (genre) | Kind 6 concepts. Pop, rock, hip hop, indie / alternative / modern rock (7 concepts) on either track: 1 / 0.8 / 0.6 for ≤ 4 / ≤ 8 / more bars. Techno, house, EDM (4 concepts) on both tracks: 1 / 0.8 / 0.6 for ≥ 16 / ≥ 8 / fewer bars. Otherwise 1 |
| Total | r0 × (r1 + r2 + r3 + r4) (all weights 1) |
| Choice | Stable sort by bars (desc), then total (desc). Keep the first 5 per bar count. Rank by Pareto layer over (r0…r4), then total, then bars. No pair left: not beatmatched, so §7.1 applies (`auto_transition_fallback_transition_length_milliseconds` 5000) |
| Recipe | startAMs / startBMs = the windows' first downbeats; durationMs = A's window; durationBars = the bar count. speedB = (B end s − B start s) / (A end s − A start s), with s = float(ms) × 0.001f in float32. B covers its own window (`audio.fade_in_duration`). bpmA / bpmB = kind 222 |
| Preset | SHA-1 over uriA ‖ uriB ‖ int32 bars ‖ int32 startA ms ‖ int32 startB ms (little-endian ints); the digest's first 8 bytes as a little-endian u64, modulo the table. 2 bars: {1, 10, 19}; 4+ bars: {1, 2, 3, 4, 5, 17, 18, 8, 9, 10, 19} |

Client defaults (`core-automix`): cue start / end 800, middle 1000, margin 500 ms, non-best 950, confidence-based cue
scoring off. Vocal threshold 200, exponent 1000. Key tiers 3. Diminish knee 150 permil / 90000 ms. Speed glide 5 permil
per 1000 ms after 500 ms. Octave bpm correction off, 2/4 bars allowed, avoid-cutting-vocals off. Of these, only the
length fraction (300) is set by the server.

Not modelled: the 2/4-bar downbeat paths (no 2/4 grid in the dataset) and the `beatmatchedCandidate` answer.

Wavee live (`BeatmatchFacts.Live`) has no kind 218 or kind 6 and keeps two kind 28 candidates. The decision stays exact;
the window matches 19 of 72 (without vocals 32/72, without full cue lists 25/72). Kind 6 genres cost nothing here:
defaulting to the short-window group, which holds 70 of 73 pairs, gives the same choices. The kind 217 decoder also
drops a beat at 0.0 s (field 1 absent on the wire) and rounds in double where the client rounds the float product.

### 7.3 Bounds and limits

| Limit | Value |
|---|---|
| Longest transition | 35000 ms [client] |
| Beatmatch opt-out | 5000 ms [client]; what it bounds is **open** |
| Auto length | at most 30 % of the track (`core-automix.max_auto_transition_length_fraction_permil = 300`) |
| Editor length | `desktop-ui.playlistMixingMaxDurationPercent = 30` |
| Cue fallback | `auto_transition_fallback_cuepoint_selection_strategy = "First"` |
| Backend Auto | `use_backend_auto_transition = true`, yet no backend call for an Auto transition was ever seen: the Auto recipe first appears inside the editor's preview command |

The remote config in full: [`48-mix-protocol.md`](48-mix-protocol.md) §11.
