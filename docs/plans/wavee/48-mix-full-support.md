# Mix (#48): full support, technical plan

Status: draft, 2026-11-25. Target: from `release/0.4.0` on; M1 ships first. Builds on #48 as it is on
`release/0.4.0` (the 0.4 plan's phases 1 to 6 done, 7a partial).

What this covers: what it takes for Wavee to support Spotify's playlist Mix as fully as the official desktop client
1.3.10 does (style table version 8): every preset and style, Spotify's own Auto, beatmatching, the full editor saved to
Spotify, previews over Connect, and Connect state that matches the official player. It lists the facts that change the
0.4 code, the design, the phases M1 to M8 with their gates, and what is still open.

Sibling documents:
- [`48-mix-reference.md`](48-mix-reference.md): presets, styles, enums, value formulas, the recipe schema, the save
  payload, what the client's Auto does.
- [`48-mix-protocol.md`](48-mix-protocol.md): the wire, as the official client speaks it.
- [`48-mix-implementation.md`](48-mix-implementation.md): the 0.4 design. Its revision 6 note lists what this file
  replaces there.
- [`48-mix-ux-direction.md`](48-mix-ux-direction.md): the UX source of truth (unchanged).
- [`dj-implementation.md`](dj-implementation.md), [`dj-protocol.md`](dj-protocol.md): AI DJ, which reuses the Mix voice
  machinery for its talk-over and its automix.

Source: captures of the official desktop client 1.3.10, 2026-11-25 (and the 2026-11-22 capture that
`48-mix-implementation.md` cites), plus the client's own mixing tables and the Auto answers it computed for 229 track
pairs; the raw material is kept outside the repo (it holds account data). Wavee code references are to
`release/0.4.0` at `0a45dbfbc`, paths relative to `src/apps/Wavee/`.

---

## Decisions (owner, 2026-11-25)

| Question | Decision | Where it lands |
|---|---|---|
| Effects and looping (beta and opt-in in the official client) | **Always available in Wavee's editor.** No opt-in and no Wavee switch; the account preference `mixing-fx-looping-jogwheel` is not read | §2.5; the pickers ship when E7 and E8 do (M4, M6) |
| "Listen without Mix" | **Synced to the account:** `mix-opt-out` / `mix-remove-opt-out`. The per-PC setting goes; existing per-PC choices are sent once as opt-outs | M2 adds the two signals and the migration; `Platform.Keys.MixListenWithout` is removed after it |

---

## 0. What "fully supported" means

Parity target: the official desktop client 1.3.10, style table version 8.

| Area | Official | Wavee on `release/0.4.0` | Target |
|---|---|---|---|
| Play a mixed playlist | stored recipes and Auto transitions, all presets, beatmatched pairs | Auto (Fade only) and stored Fade / Rise | every preset and style, Auto as the official computes it, beatmatching |
| Mix on and off | `mix-state` signal both ways | on only; off is a local "listen without" | both on Spotify |
| Listen without Mix | `mix-opt-out` / `mix-remove-opt-out`, synced | a per-PC setting | synced, after a one-time migration |
| Edit a transition | presets, Customize (volume, EQ, filter, effects, looping), curve editing, points on the waveforms, length, beatmatch, undo / redo, preview, save | a local card: 4 styles, ± beats, saved on this PC | the full editor, saved to Spotify |
| Reset to Auto, No transition | `set-transition` with no payload; the "none" recipe | local overrides | both on Spotify |
| Preview | `automix-preview` over Connect to the active device | **advertised but dropped (a live bug)** | render previews as a receiver; send them as an editor |
| Connect | the whole plan published on every upcoming track | the playing pair only | publish, receive and control as the official does |
| Not the owner | "Duplicate Mix" | — | the same |
| Smart reorder | the `harmonize` lens | — | the same |

---

## 1. What we know now

The facts, with their evidence, are in [`48-mix-reference.md`](48-mix-reference.md) and
[`48-mix-protocol.md`](48-mix-protocol.md). This section lists what they change in the 0.4 code.

### 1.1 Facts that change the 0.4 code

| Fact | Detail | 0.4 code to change |
|---|---|---|
| **Mix off** is `mix-state` without a payload, lens `auto`, sync-reason `CA8QAQ==`. The recipes are kept; on again restores identical bytes | protocol §3.1 | `Entities/Mix.Menu.cs:92` (the disabled "Turn off for everyone"); a new `TurnOff` in `Spotify/Spotify.Api.Mix.cs` |
| **`set-transition`**: signal field 2 = item attribute 14.2 verbatim; field 3 = the payload `{1 new recipe, 3 previous when Auto, 4 previous when user-defined}`; sync-reason `CA8QAQ==`; the server stores field 1 | protocol §3.2, reference §6 | a new `SaveTransition` in `Spotify/Spotify.Api.Mix.cs` |
| **Reset to Auto** is `set-transition` with no payload [client] | reference §6 | the local Auto override (`Entities/Mix.Card.cs:203-210`) becomes a write |
| **"No transition"** is the "none" recipe (overlap 1, no preset id) | reference §5.3 | the local "No mix" override becomes a write |
| **"Listen without Mix"** is `mix-opt-out` / `mix-remove-opt-out` [client] | protocol §3.3 | `MixListenWithout` (`Entities/Mix.cs:236-272`), `Platform.Keys.MixListenWithout` |
| **Item attribute 14 is the signal template**, present on every mixable item under the mix lens: echo it, never rebuild it. Not-mixable is attribute 11 `not-mixable` + `not-mixable.reason` (e.g. `entity-kind`) | protocol §2 | `Spotify/Spotify.Decode.cs:2043-2054`, `Entities/Mix.cs:72-74`: keep the bytes, not a presence bit |
| The list's `contents.5[]` carries `{1 "mix-state", 2 {1 "mix"}}` only under the mix lens: the `mix-state` template and the "can toggle" gate | protocol §2 | decode and keep |
| **Signals neither bump the revision nor cause a push.** Only the mix lens etag in field 22 moves; other devices see an edit only on a lens re-read | protocol §3.4 | `Spotify/Spotify.Api.Library.cs:262-268`: cache by (revision, mix lens etag); keep the re-read on page activation |
| `changes` (add, move) keep each recipe on its from-item, even when the seam changes | protocol §3.4 | none: the planner already pairs by adjacency and `ToItemId` |
| **The recipe schema is known** field by field (`durationBars`, `isBeatmatched`, preset `type`, every style and curve override, `isOverlapOverride`, `isPresetIdOverride`, `beatmatchPreference`) | reference §5 | `Entities/Mix.Recipe.cs:63, 79, 104-151`: the round trip loses 1.8, 1.9, 2.2, 2.4 and forces top-level 3 |
| **The value semantics are exact** (EQ −48 to +6 dB with a kill at 0; low-pass 120 to 16000 Hz; high-pass 20 to 9000 Hz; volume 20·log10) | reference §4 | `Entities/Mix.Curves.cs:180-193` (a −26 dB shelf, a 20 kHz low-pass top, a 6 kHz high-pass top) |
| **Every preset's and style's curves are tabled** by bars; effects also by A's tempo | reference §1, §3 | the two-preset `MixCurves` |
| **The non-beatmatched Auto rule is exact** (156 of 156 pairs) and needs kind 28's candidate lists | reference §7.1 | `Spotify/Spotify.Decode.Mix.cs:78-110` keeps only fields 1 and 2 |
| **Previews go to the active device** as a Connect `signal` `automix-preview`, wrapped in `pause` … `resume`. Wavee advertises the signal and drops it | protocol §5 | `Spotify/Spotify.Decode.PutState.cs:470`, `Spotify/Spotify.Decode.Connect.cs:559-633` |
| **A playing mixed playlist publishes the whole plan**: f11 = `fade_out_start_time`, the incoming track current from its start, the fade keys on every upcoming track, the speed glide | protocol §6 | the PutState publisher (D14 of the 0.4 plan) |
| **A remote `play` carries no lens**; a list play carries its index in `playlist_play_request` | protocol §7 | `Spotify/Spotify.Connect.Play.cs:125, 312` stay without a lens; the inbound play decode |
| Kind 10 `Track.duration` is sint32 (zigzag) | protocol §9 | none: `Spotify/Spotify.Decode.cs:640, 880, 1066` already read `ZigZag()` |

### 1.2 Presets, by Spotify's names

Fade (1), Rise (3), Blend (2), Wave (17), Melt (4), Slam (11), Dissolve (20), Surge (21), Afterglow (22), and No
transition (no preset id). Thirteen more internal presets occur in Auto and possibly in stored recipes. What each one
expands to, and the styles under Customize with their UI labels: [`48-mix-reference.md`](48-mix-reference.md) §1.

### 1.3 The official client's UI rules [client]

- **Undo / redo** in the edit panel, and previous / next transition.
- **Beatmatching** toggle ("Beatmatching"; "Beatmatch unavailable" when the pair cannot be matched). Whether the toggle
  writes `beatmatchPreference` (top-level 5), `isBeatmatched` (1.9) or both: **open**.
- **Shuffle:** "Mixed playlists sound better in order … Some transitions may not play while in Shuffle", with "Turn off
  shuffle" / "Shuffle anyway".
- **Sort order:** "Turn off mix to change your sort order".
- **Not the owner:** "You can't edit the transitions in this playlist … Duplicate Mix".
- **Free tier:** the upsell "Play and create mixed playlists with Premium".
- **Labels:** "Mixed by", "· Mixed", "{0} BPM" on the waveform, Auto / Custom / No transition.

### 1.4 Analysis data

Kinds 28 (cue points with candidate lists), 217 (beats), 218 (vocal activity, 70 frames/s), 219 (mixability), 142
(loudness envelope, 50/s), 237 (three-band waveform, 50/s), 222 (tempo and key), with exact length formulas:
[`48-mix-protocol.md`](48-mix-protocol.md) §9. The official editor fetches 28, 142, 217, 218 and 219 (+6, 222) with
`client-feature-id: automix_esperanto` when a mixed playlist opens (batches of at most 5) and for each new seam; playback
(`context_player`) fetches only 28 and 142; a 502 on kind 28 means "not analysed yet", retried on the next seam change.

---

## 2. Design

### 2.1 Wire (`Spotify/Spotify.Api.Mix.cs`, `Spotify/Spotify.Decode.Mix.cs`, `Entities/Mix.Recipe.cs`)

1. **A lossless recipe codec.**
   - Decode into the model and **keep the raw bytes**.
   - Encode by patching the raw message: replace the known fields, keep every unknown field verbatim, at any depth.
     Never rebuild from the struct alone.
   - The model uses the client's field names ([`48-mix-reference.md`](48-mix-reference.md) §5.1): `StartAMs`,
     `StartBMs`, `DurationMs`, `SpeedA` / `SpeedB`, `DurationBars` (1.8), `IsBeatmatched` (1.9), the BPMs, beats hashes
     and item speeds, `PresetId` (2.1), `PresetType` (2.2), the style overrides (2.3, 2.4, 2.5, 2.12, 2.15, 2.16 as
     `{1 id}`), the curve overrides (2.6 to 2.11, 2.13, 2.14), the loop blocks (2.17), `IsOverlapOverride` (3),
     `IsPresetIdOverride` (4), `BeatmatchPreference` (5).
   - The "none" recipe as observed: overlap 1, no 1.3 or 1.4, no 1.16 / 1.17, no 2.1, no top-level 3
     ([`48-mix-reference.md`](48-mix-reference.md) §5.3).
   - This fixes the 1.8, 1.9, 2.2, 2.4 and top-level 3 defects (`Entities/Mix.Recipe.cs:63, 79, 104-151`).
2. **Value semantics:** replace `Entities/Mix.Curves.cs:180-193` with the client's evaluator
   ([`48-mix-reference.md`](48-mix-reference.md) §4).
3. **Item attribute 14 and the list's `contents.5`:** store the template bytes per item and per list
   (`MixTransition.Signal`, `PlaylistMixSignals`). `CanEdit` = the template is present (it replaces the presence bit).
   Not-mixable comes from attribute 11, with its reason.
4. **Writes:**
   - `TurnOn` (exists; it also reads its template from `contents.5`).
   - **`TurnOff`:** `mix-state` with no payload, lens `auto`, `CA8QAQ==`; fold the reply; keep the local recipes. It
     replaces the disabled "Turn off for everyone".
   - **`SaveTransition(from item, recipe)`:**
     - signal field 2 = attribute 14 verbatim;
     - payload field 1 = the new recipe (item ids, beats hashes, BPMs), encoded from the patched raw;
     - the previous transition: field 3 = the pair's **Auto** recipe (preset 1, kind 222 BPMs, no item ids), computed
       by the planner, when the pair was Auto (as captured, 2 of 2 saves); field 4 = the stored user-defined recipe
       when there is one, as the client's logic does. **Open:** the one capture of a save over a stored recipe sent
       field 3 instead (reference §6);
     - fold the reply (the new lens etag, the recipe, `transition_uri`).
     - "No transition" saves the "none" recipe.
   - **Reset to Auto:** `set-transition` with no payload.
   - **`OptOut` / `RemoveOptOut`:** `mix-opt-out` / `mix-remove-opt-out`, no data. They replace the per-PC "listen
     without Mix". A one-time migration sends every playlist in the per-PC list (`Platform.Keys.MixListenWithout`) as
     an opt-out, then removes the key. **Open:** their sync-reason, and how an opt-out reads back on another device
     (protocol §3.3).
5. **Cache identity** = (revision, mix lens etag) (`Spotify/Spotify.Api.Library.cs:262-268`). Persist `MixData` with
   the list (`Entities/Mix.cs:105-107`) so a cold start does not owe a lens read. Keep the lens re-read on page
   activation: signals cause no push.
6. **Analysis:**
   - Decode 218, 219 and 142 (protocol §9) into `Edges.TrackVocal`, `TrackMixable`, `TrackLoudness`.
   - Kind 28 keeps the smallest start candidate and the largest end candidate per track (the non-beatmatched Auto rule
     needs both).
   - Fetch them with the editor's batches (at most 5; retry kind 28 after a 502).

### 2.2 Engine (fluent-gpu; every automation curve is a 1025-point table, as E1 to E3)

| Need | Today | Change |
|---|---|---|
| Volume styles (all) | a gain envelope per voice | none (tables) |
| **3-band EQ** (low / mid / high per voice: Blend's EqSwap, 3-band fade, mid and high swaps) | one low shelf (200 Hz) | **E5:** add a mid peak lane and a high shelf lane to `VoiceAutomationStage`. Gains per reference §4 (−48 to +6 dB, kill at 0). **Open:** the crossover frequencies (not in the client's tables; measure them against the official client's output) |
| Filter low-pass / high-pass sweep + resonance | one 12 dB/oct sweep lane | keep; cutoff per reference §4. **Open:** resonance has no value semantics in the client's table. A **noise generator lane** (`noiseColor` curve, NoiseOutEnd) = **E6**: a pink / white noise voice with a colour filter, colour per reference §4 (kind 5) |
| **Effects**: echo and delay (½, ¾, 1 beat), reverb, each with dry / wet | none | **E7:** a tempo-synced delay line (feedback) and a small reverb (FDN) per voice, with a dry / wet lane (0 to 100 %); tempo from recipe 1.16 |
| **Looping**: 1 to 16 beats, halving | none | **E8:** a beat-loop source wrapper (repeat a decoder region on the beat grid, sample-accurate, crossfaded seams). The block message also has SPINBACK and VINYL_STOP types; whether a style uses them is open |
| **Beatmatching** (`isBeatmatched`, `speedB`) | one rate for the whole session | **E4:** a per-voice rate. Hold `speedB` through B's fade-in, then glide to 1.0 at 0.005 per second, as the official player publishes (protocol §6.2). Speeds of ±10 % occur (0.9017, 1.0999): those need the pitch-keeping stretch (WSOLA); deviations under 0.5 % (1.00008) must be inaudible, so resample, not WSOLA |
| Lane count | 2 | budget: volume + 3 EQ + filter + noise + 3 effects = 9 lanes of at most 128 steps each; armed only when used |

Each engine item ships as its own fluent-gpu PR (the engine keeps PRs into its `main`) and an `engine.lock` bump.

### 2.3 Playback (`Playback/Playback.Mix.cs`, `Playback/Playback.Audio.Mix.cs`, `Entities/Mix.Curves.cs`)

1. **Style table.** `MixStyles` = preset → (volume, EQ, filter, effect, jog wheel, looping) style ids, and style id →
   curve generator (bars, bpm), seeded from the client's tables ([`48-mix-reference.md`](48-mix-reference.md) §1, §3).
   A preset id Wavee does not know renders as Fade. It replaces the two-preset `MixCurves`.
2. **Planner.**
   - **A saved recipe:** honour `startAMs`, `startBMs`, `durationMs`, `durationBars`, `isBeatmatched` with `speedB`,
     and the overrides. "None" means no transition (a plain track change at A's end, or gapless), never a 1 ms Fade.
   - **B's window:** beatmatched = `durationMs × speedB` (B's media time); not beatmatched = B's first start candidate
     + 2500 ms, independent of the overlap. A's and B's curves then run over different lengths from the same start
     instant; the renderer takes both windows, not one.
   - **Auto, not beatmatched (exact):** `startAMs` = A's last end candidate − 2500, the overlap runs to A's end, B from
     0, preset 1, `durationBars` 0, speeds 1 ([`48-mix-reference.md`](48-mix-reference.md) §7.1).
   - **Auto, beatmatched:** fitted against the 229-pair dataset (which pairs match, 2 or 4 bars, the preset score, the
     speed). Until the fit lands, the one observed placement: 2 bars (8 beats of A) centred on A's end cue and on a B
     start candidate that sits on a downbeat, preset 1, at most 30 % of the shorter track.
   - Keep Wavee's refusal reasons and logs.
3. **Cancel inside the overlap:** ramp A back over 30 ms and fade B out over 30 ms (today a one-frame cut,
   `Playback/Playback.Audio.Mix.cs:262-263`). Fix the possible stale `s_joinMix` after a load.
4. **Hear it (local preview):** loop the pair from (out − 3000 ms) through the transition into B, until Stop or the next
   change; restore the user's playback afterwards (a state machine: Idle → Previewing → Restoring).

### 2.4 Connect (`Spotify/Spotify.Decode.PutState.cs`, `Spotify/Spotify.Decode.Connect.cs`, `Spotify/Spotify.Connect.Play.cs`)

1. **Receive `automix-preview`** (fixes the live bug). Decode the `AutomixPreview` payload (protocol §5). On the first
   preview, remember the playback state (the editor has already paused it). Render the transition with §2.3.4's preview
   engine from `startPositionMs` (3000) of pre-roll before the out-point. Replace it on the next preview. Stop on
   `stop-automix-preview` (resume afterwards when its payload is `"true"`), and on `resume`, `pause` or `play` (the
   captured editor wraps its previews in `pause` … `resume`). Play it as the official receiver does: context
   `spotify:mixing:preview`, items flagged `is_transition_preview=true`, uids `preview_1` / `preview_2`. **Until this
   works, stop advertising `automix-preview`** (M1); then advertise it and `stop-automix-preview` (M5).
2. **Send previews as an editor** when another device is active: `pause` → previews → `resume`, as captured; stop with
   `stop-automix-preview`, or a skip-next when the device does not advertise it.
3. **Play commands carry no lens**, as the official client's do: `Spotify/Spotify.Connect.Play.cs:125, 312` keep
   `context://<uri>`. On an inbound `play`, Wavee decides to mix from the playlist itself: read the list with the mix
   lens when its mix state says mixed. Decode the start index of a list play from
   `context.metadata.playlist_play_request` (protocol §7).
4. **Publish every upcoming transition**, not only the playing pair (protocol §6):
   - the fade keys of protocol §6.3 on the current track and on every `next_tracks` entry, only the lanes a style uses;
   - `mixer_enabled=true` and the context metadata of protocol §6.1;
   - **f11 = `fade_out_start_time`, and the incoming track current from the moment it starts**, as the official player
     does. This differs from the 0.4 plan's D14 (b), which runs the outgoing track to its audible end. A phone
     controlling Wavee reads f11, so on the wire Wavee follows the official player; what Wavee's own bar shows is an
     owner decision in M7;
   - f9 and `playback_speed` = `speedB` while B is matched, each glide step published.
5. **As a controller:** read the remote's published fades for the bar's rail and "Mixing into …", instead of Wavee's
   own plan.

### 2.5 UI (follows [`48-mix-ux-direction.md`](48-mix-ux-direction.md), with Spotify's names and vocabulary)

| Item | Detail |
|---|---|
| Names | Spotify's: Auto, Fade, Rise, Blend, Wave, Melt, Slam, Dissolve, Surge, Afterglow, No transition, Custom (they replace Smooth, Lift and Quick) |
| Editor | per pair: two waveform lanes (kind 237 bands, kind 217 beat ticks, kind 218 vocal shading); out and in points draggable and snapped to beats (keeping the bar phase, as the official editor does); length in bars; preset chips in table order; **Customize** with five ingredient pickers (Volume, EQ, Filter, Effects, Looping), **all always available**; curve editing ([`48-mix-reference.md`](48-mix-reference.md) §3.3); the Beatmatching toggle (disabled with "Beatmatch unavailable" when the pair cannot be matched); Undo / Redo; ‹ › transitions; Preview; Save / Discard |
| Lists | Tracks / Transitions switch; the BPM · Key column with Mix; the player-bar "117 BPM · 6B"; "Mixed by \<owner\>"; badges on sidebar and library tiles |
| Mix menu | "Turn off Mix for everyone" becomes a real write; "Listen without Mix" is synced to the account, so it loses "(this PC)" |
| Rules | the shuffle notice (official wording); the sort-order lock; not the owner: "Duplicate Mix"; the free-tier upsell; offline: read-only |
| Owner rules | inline, no dialogs (`inline-over-dialogs`: the shuffle and duplicate prompts become inline notices; the owner's rule beats parity); no layout jumps; all themes |

### 2.6 Proof (`--fake`)

- Extend `Entities/Entities.Fake.Mix.cs`:
  - one pair per preset;
  - a beatmatched and a non-beatmatched pair;
  - a "none" pair;
  - a recipe carrying unknown fields (the round trip of §2.1.1, checked in the inspector: decode → encode →
    byte-equal);
  - kind 28 fixtures with candidate lists; kinds 218 and 142 fixtures.
- The inspector's `mix` verb gains `recipe-roundtrip`, `styles` and a preview state.
- `--mix-bench` renders each style to a WAV.
- No tests: the proof is the inspector, the bench renders and screenshots.

---

## 3. Phases

Each phase ends with a clean Debug build (`dotnet build src/apps/Wavee/Wavee.csproj`; the engine phases also build
fluent-gpu), its inspector proof and screenshots looked at.

| Phase | Status | Scope | Needs capture | Gate |
|---|---|---|---|---|
| **M1 Fixes (ship first)** | **in progress (2026-11-25)** | the lossless recipe codec on the client's field names; the exact value semantics; attribute 14 and `contents.5` templates kept as bytes; the not-mixable reason; stop advertising `automix-preview` until M5; a "none" recipe plays as no transition (not a 1 ms Fade); the 30 ms cancel ramps | none | `mix recipe-roundtrip` byte-equal on every fixture recipe (unknown fields and "none" included); `mix styles` shows the reference values (e.g. EQ 0.25 → −8.49 dB); the published player state no longer lists `automix-preview`; the "none" pair plays a plain track change in `--mix-bench` |
| **M2 Writes** | not started | Mix off for everyone; `set-transition` save (new recipe, previous in field 3 or 4); "No transition"; Reset to Auto; `mix-opt-out` / `mix-remove-opt-out` with the one-time migration, then `Platform.Keys.MixListenWithout` removed; cache identity (revision, mix lens etag); `MixData` persisted with the list | none (Reset to Auto and the opt-out signals come from the client's logic; their first live use is the check) | the owner's live check on a test playlist: each write from Wavee shows in the official client (off and on keep the recipes; a saved preset shows on its pair; Reset shows Auto; an opt-out follows to the other device); the migration runs once (one log line) and the key is gone |
| **M3 Presets and Auto** | not started | the style table and curves for all 22 presets and every style; the planner honours `startBMs`, `durationBars`, `isBeatmatched` / `speedB`, the overrides and "none"; B's window per §2.3.2; the exact non-beatmatched Auto; kind 28 candidates kept; the beatmatched Auto fit | none (the curves come from the client's tables; the published metadata is captured) | every seam of protocol §6.5, rendered from the tables at its bar count, equals the published curves; the non-beatmatched rule reproduces the dataset's 156 pairs; the beatmatched fit's score against the 73 pairs is logged |
| **M4 Engine** | not started | E4 per-voice rate with the glide, E5 3-band EQ, E6 noise, E7 echo / delay / reverb, E8 looping (fluent-gpu PRs) | none; the effect lanes are matched by ear against the official client | engine and app Debug builds; `--mix-bench` renders each style; by ear, A/B against the official client on the same saved transition; no per-block allocation in the engine counters |
| **M5 Preview** | not started | local Hear it; Connect `automix-preview` receive and send; advertise `automix-preview` and `stop-automix-preview` once it works | none (M-C8 optional: a receiver's PutState during a preview, to confirm the client's receiver spec on the wire) | with the official editor open and Wavee the active device, each chip click plays on Wavee and closing the editor restores playback; the inspector's `mix` preview state |
| **M6 Editor** | not started | the full editor of §2.5, all themes | none | inspector `render`, `trace` and `lint min=error` per theme on the editor (open, preset pick, point drag, Customize); a save from Wavee reads back identically in the official client |
| **M7 Connect** | not started | publish every upcoming transition (§2.4.4); f11 and the current track as the official player; the speed glide; the list play's start index; inbound plays read the mix state; the controller reads the remote's fades; the owner's decision on what Wavee's own bar shows during an overlap | M-C7, the rest: a phone as the controller, the official desktop as the receiver | a phone on the account shows the right track, the mix-out as the end and the incoming track at its in-point; the official desktop's list Play on Wavee starts at the right index and plays mixed |
| **M8 Rest** | not started | Smart reorder (the `harmonize` lens), Duplicate Mix (the playlist service's `copy`), the free-tier upsell, badges | M-C9 (low priority): a non-owned list's read shape; what happens to a recipe when its from-item is removed | per-theme renders and traces; Smart reorder and Duplicate Mix show the same result in the official client |

---

## 4. Captures: status

The scenarios planned before the afternoon session, with what answered them.

| # | Scenario | Settles | Status |
|---|---|---|---|
| M-C1 | Save every preset and every Customize style on a matched and an unmatched pair, and play through each seam | style ids, Dissolve's id, the curves | **closed**: the client's own tables give every id and curve; six played seams check them (protocol §6.5) |
| M-C2 | Change the transition length three times on a matched pair | 1.5 against bars, 1.16 / 1.17, speed, the 30 % cap | **answered** by the dataset (2 or 4 bars; `durationMs` = bars × 4 × 60000 / bpmA); an editor drag was never captured |
| M-C3 | Three matched pairs at different tempos; toggle Beatmatching | the speed formula, 1.8, 1.9 | **partly**: 1.8 and 1.9 are known; `speedB = bpmA / bpmB` in 38 of 73 beatmatched pairs, the rest open |
| M-C4 | Reset an edited pair to Auto | the reset signal | **closed** by the client's logic (`set-transition`, no payload) |
| M-C5 | Customize a curve, undo / redo, save | how curve overrides are stored | **not needed**: the schema is known (reference §5.1) |
| M-C6 | Play the mixed playlist through edited, Auto, none and unmatched seams | what a playlist-Mix PutState publishes; which kinds load before a seam | **closed** for the PutState (protocol §6); which kinds load before a seam: open |
| M-C7 | Play the mixed playlist on a phone from the desktop, and the reverse | the command and context shape, who reads the lens | **half**: a desktop list play to Wavee is captured (protocol §7); a phone as the controller and the desktop as the receiver are not |
| M-C8 | Edit while a phone is the active device | the receiver's side of a preview | **answered** by the client's logic (protocol §5); a receiver's PutState during a preview was not seen |
| M-C9 | A followed (non-owned) mixed playlist; "Duplicate Mix"; remove the from-item of an edited seam | template absence, the duplicate call, recipe cleanup | **open**: the non-owned read shape (low priority) and the recipe cleanup; "Duplicate Mix" is answered (`copy`) |

---

## 5. Risks

- **The effect lanes' sound.** The curves of every lane are known, but not the DSP behind echo, reverb, delay, noise and
  looping, nor the Connect keys those lanes publish (none of the six played seams used one). They must be modelled by
  ear against the official client. Plan the time for it.
- **Beatmatched Auto is not fitted yet.** Until M3's fit, an untouched beatmatched pair may cross at a different point
  and speed in Wavee than in the official client. An edited pair is identical in timing everywhere, and the
  non-beatmatched rule is exact.
- **Writes from the client's logic, not the wire.** Reset to Auto and the opt-out signals were never seen on the wire,
  and one captured save contradicts the client's rule for the previous recipe. The first live writes are the owner's,
  on a test playlist, checked in the official client (M2's gate).

---

## 6. Open questions

| Question | Blocks | Detail |
|---|---|---|
| The beatmatched Auto choice: which pairs, 2 or 4 bars, the preset score, the speed for 35 of 73 pairs | M3's fit | reference §7.2 |
| Which previous recipe a save over a stored recipe sends (field 3 or 4) | M2 | reference §6 |
| The opt-out signals' sync-reason, and how an opt-out reads back on another device | M2 | protocol §3.3 |
| The Auto mix toggle (`mix-auto-toggle-on` / `-off`, `mix-type=only-auto`): what it is in the official UI | no phase yet | protocol §3.3 |
| The published keys of noise, effect and looping lanes | M7 | protocol §6.3 |
| The EQ crossover frequencies; how resonance maps to Q | M4 | reference §4.2 |
| The jog-wheel styles (preset field 2.15) | M6 | reference §1.3 |
| The curves at bar counts between the tabled ones | M3, M6 | reference §3.1 |
| What the "beatmatch opt-out" limit (5000 ms) bounds | M3 | reference §7.3 |
| What the Beatmatching toggle writes (top-level 5, 1.9 or both) | M6 | §1.3 |
| `supports_playlist_mixing` showed as true for the Wavee device in the morning cluster although the code does not write it | M1 (check after the fix) | protocol §5 |
| Which analysis kinds the player loads before a seam of a mixed playlist | M3 (prefetch) | protocol §6.3 |
| A phone as the controller; the official desktop as a receiver | M7 | protocol §7 |
| A non-owned list's read shape; what happens to a recipe when its from-item is removed | M8 | protocol §13 |
