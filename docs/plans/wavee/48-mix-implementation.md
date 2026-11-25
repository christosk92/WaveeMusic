# Mix (#48): Spotify's playlist Mix, read, honoured and edited in Wavee — implementation

> **Design and briefs.** UX source of truth: [`48-mix-ux-direction.md`](48-mix-ux-direction.md) (revision 2,
> Spotify parity). Design board with every screen and the spec boards (architecture, timeline, data model, phases,
> decisions, risks, Spotify parity): https://claude.ai/artifact/Xh4EfwYUxNPnAKRYYpmBBS (private until shared).
> Shareable brief for the reporter: [`briefs/48-mix-brief.pdf`](briefs/48-mix-brief.pdf).

Target: Wavee **0.4 "Drift"**. Base code: `release/0.3.13` (all file:line references below were read there, in
the app and in fluent-gpu). Work branches from the 0.4 release
branch once it exists.

**Revision 5 (2026-11-23), Spotify parity.** The owner found revision 4 "too barebones, it's missing a lot" next to
the Spotify app. `48-mix-ux-direction.md` is now its revision 2: **everything Spotify's Mix offers, quiet by
default**. Where this file and the direction differ, the direction wins. What changes here:

- **Names.** Spotify's preset names throughout: Auto, Fade (id 1), Rise (id 3), Blend, Wave, Melt, Slam, plus
  Wavee's No mix. *Smooth, Lift and Quick are gone* (D17 is void; `MixStyle` becomes `MixPreset` plus `NoMix`).
- **Reversed decisions.** D9: tempo · key **is** shown, as a BPM · Key column with Mix and a small label in the player
  bar while a mixed playlist plays, both hideable from the Mix menu. D18: chips are a view option, "Show
  transitions: On hover · Always" (Always = Spotify's chip in every seam). D1: the card gains **Customize** (Volume 7,
  EQ 4, Effect 8, Beatmatch), a two-lane waveform editor that moves **both** the mix-out and the in-point, an
  overview bar, a looping Preview, Save / Discard.
- **New surfaces.** The **Mix menu** (split button): turn off for everyone (after O1), listen without Mix, Smart
  reorder, show transitions, show tempo and key, mix when shuffling. **Smart reorder** (a dialog; a normal playlist
  move, undoable). A **"Mixed" badge** on the playlist art and tiles. Collaborators edit as owners do; offline, the
  card is read-only.
- **Phases.** 5 grows (Mix menu, column, chips option, badge, player-bar label): L. 7 splits: **7a** presets with
  known ids, waveform editor, Preview, Save/Discard (L); **7b** Customize, Blend/Wave/Melt/Slam, the in-point
  (after capture round 2: O2, O3, O5; M). **8** Smart reorder (S-M, no capture). **9** Beatmatch: engine E4
  per-voice rate plus recipe 1.6-1.8 after O4 (L, Could). The smallest cut adds the list-side parity of phase 5.
- **Rule for unknown writes (unchanged in spirit).** Every control is designed and built; a preset or field whose
  storage is not captured is hidden, never guessed.

**Revision 4 (2026-11-23), the UX direction.** The owner rejected copying Spotify's Mix UX as cluttered. Every UI
part of this plan now follows `docs/plans/wavee/48-mix-ux-direction.md`: one Mix toggle, a list
that stays a list (no chip on every row), seams that show on hover and for edited pairs, a Transitions view, an
inline seam card with four plainly named styles, and a light "Mixing into …" while a join plays. The wire, engine
and planner of revision 3 are kept. What the direction adds to the playback model is small and collected in §3.6a
(shuffle with Auto, a local "this PC only" layer for "No mix", style names over timing, and the moment the player
flips to the next song). The house primitives the new UI uses are listed and verified in §1.7.

**Revision 3 (2026-11-22), after the capture.** The Mix capture report now exists:
the capture report (kept outside the repo: it holds account data) ("the report" below; a small decoder script kept with it decodes a recipe).
Every wire-level part of this plan is now based on it. Its confidence tags are kept: **[wire]** read off the bytes,
**[inferred]**, **[guess]**. What the capture changed against revision 2:

- **Mix is a server-side playlist *lens*, not a playlist edit.** Turning it on is one `/signals` POST; the playlist
  revision never changes. There is no "mixed by" field.
- **Only edited transitions are stored**, as a base64 protobuf *recipe* on the outgoing item. The recipe holds
  timing and a **preset id**, not volume/EQ/filter choices.
- **Auto transitions are computed by the client** from beat grids and cue points. Wavee needs its own auto planner.
- **The sound is described by Bézier curve lists** (volume, low-EQ gain, filter cutoff, resonance), which the client
  derives from the preset and publishes in Connect track metadata. The engine work is now "render these curves".
- **Observed transitions are short (2.5 to 12.8 s), at playback rate 1.0**, and the incoming track often starts
  mid-file on a downbeat. Revision 2's long tempo-stretched overlaps were wrong in style. Beatmatching was never
  switched on in the capture and moves out of 0.4.
- The editor shrinks to what is proven storable: **presets** (Fade and Rise today). (Revision 4 names them Smooth
  and Lift and adds Quick and No mix; see above.)
- A **probable bug in shipped Wavee** was found (§1.6). It is separate from the 0.4 work.

Items the capture leaves open are collected in §5.2 with the captures that would close them.

## Cross-plan notes (0.4)

| Groundwork | Owner | What this plan (#48) does with it |
|---|---|---|
| **The mix planner and renderer**: lens and recipe wire code, the auto planner, preset → curve tables, scheduling, the Connect metadata of a mixed context | **this plan (#48)** | Everything in §3. |
| **G2: the playback timeline tap** (`Playback.Timeline`, the engine's `AudioTimelineAnchor` / `PcmAudioSession.TryReadTimeline`), the aux audio output, the module observer hook | **#154 (timecode)** | Not defined here. This plan calls `Playback.Timeline.SetTransition(...)` / `ClearTransition()` (§3.9). **Change for #154:** in 0.4 a mixed join plays at rate 1.0, so this plan no longer adds a per-voice rate; `AudioTimelineAnchor.Rate` stays what it is today. The per-voice rate (`SetVoiceRate`) that #154's §3.2 mentions moves to "After 0.4" with beatmatching. Windows are 2.5 to 16 s, scheduled about 2 s ahead, not 20 to 30 s. **Revision 4, for #154's D6:** if the owner takes this plan's D14 (b), a mixed join keeps the *outgoing* track as the app's identity until the overlap ends, so this plan passes `Master.Outgoing` for the window and publishes the incoming identity at its end (§3.6a). A plain crossfade is unchanged. |
| **G1: local library**, and **lifting the local-file hard cut** | **#240** (its §3.13) | See "Local files in 0.4" below. |
| #184 custom palettes | #184 | Independent. Every new surface reads only `Tok.*` and `R.*` roles. |

**Local files in 0.4, precisely.** Today `MediaSwitch.AllowCrossfade(from, to) => from == to && from == Audio`
(`Playback/Playback.Transitions.cs:1672`) hard-cuts every boundary that touches `PlayableKind.LocalFile`
(`Playback/Playback.cs:281`). **#240 owns lifting that gate** (its §3.13), so a boundary with a local track gets the
*base hand-off* (the user's Crossfade length, or gapless). **A Mix transition never applies to a pair with a local
track on either side.** That is now Spotify's own rule, not only Wavee's: in a mixed playlist the server marks a
local item `not-mixable=true`, `not-mixable.reason=entity-kind`, gives it no `set-transition` attribute, and the
official client publishes no fade data for the pair (report §3.1, §3.3 table, last row). The seam between those rows
shows the "No mix" break (§3.10). Until #240's change lands, such a boundary stays today's hard cut.

Suggested 0.4 build order across the four: **#240 and #184 in parallel first** (no shared files with this plan), then
**#154 phase 1 (the timeline tap)**, then **#48**, then the rest of #154. If #48 starts before the tap exists, only
phase 6 waits.

---

## 1. What was asked, and what exists today

**The request (#48, Noobplaycat):** "I like mix feature so much and I want mix button on this app." The owner's
direction (revision 2): *"Plan #48 around reading and honouring Spotify's own mix data, plus an editor of this shape,
not a Wavee-only feature."*

### 1.1 Spotify's Mix, as captured

From the owner's screenshots (as described to me) and the report. This is what Spotify does and what the wire
carries; **Wavee's own UI does not copy it** (§3.10, §3.11 follow the UX direction file):

| What the user sees | What is on the wire | Report |
|---|---|---|
| A **Mix** button on the playlist header | `POST /playlist/v2/playlist/{id}/signals` with signal `mix-state`, args `{1:"mix"}`, payload `{1:1}`, header `spotify-apply-lenses: mix,auto`, sync-reason `CBAQAQ==`. The reply is the whole list with the `mix` lens applied. The revision does not change. | §3.1 |
| "**Mixed by** \<user\>" | Nothing of its own: list format attribute `mix=true` plus the owner's display name. | §3.1 |
| A **BPM column**, key and Camelot colour | Extended-metadata kind 222, which Wavee already decodes. | §3.5 |
| A **transition chip** between rows: "Auto" or edited | Edited: item attributes on the *outgoing* item, `automix.auto_transition_recipe` (base64 `TransitionRecipe`) and `automix.transition_uri`. Auto: nothing stored; the client computes it. List flag `has-custom-transitions`. Each mixable item carries item attribute 14 `{1 "set-transition", 2 {1 "mix", 2 track uri, 3 item_id}}`. | §4.1 |
| **Edit transition (Beta)**: presets Auto, Fade, Rise, Blend, Wave, Melt; Volume, EQ and Filter dropdowns; Beatmatching; Save | Save is the same `/signals` endpoint, signal `set-transition`, args = the outgoing item's attribute 14, payload `{1: new recipe b64, 3: previous auto recipe b64}`, sync-reason `CA8QAQ==`. The recipe stores a **preset id** (Fade = 1, Rise = 3 **[inferred]**); Blend, Wave and Melt were not selected. No dropdown was changed on its own, and Beatmatching was never switched on. | §3.2, §4.2 |
| The transition's sound | Not stored. Each queued track's Connect metadata carries `audio.fade_out_start_time`, `audio.fade_out_duration`, `audio.fade_in_start_time`, `audio.fade_in_duration`, `audio.fade_overlap` and four JSON curve lists per side (volume, low-EQ gain, filter cutoff, filter resonance). Sixteen distinct curves (C1 to C16) were seen. The DSP runs in the client. | §3.3, §4.3, §5 |
| A preview play button | Hidden queue rows with provider `automix/preview`; when another device is active, a Connect `signal` command with `signal_id: "automix-preview"` and the recipe in `parameters`. | §3.3, §3.4 |
| **BPM and key in the player bar** | Kind 222 again. | §6 |
| A mixed playlist playing | Context URL `context://spotify:playlist:<id>?spotify-apply-lenses=mix`; context metadata mirrors the list attributes; `PlayerState.duration` is cut to the mix-out point. | §3.3 |
| A local file in a mixed playlist | `not-mixable=true`, reason `entity-kind`; no fade data. | §3.1, §3.3 |

The nine auto transitions the desktop computed (report §3.3 table and §5) are the reference for Wavee's own planner:
overlaps of 2510 to 11637 ms, incoming start at 0 or at a downbeat a few seconds in (2874, 4144, 7794 ms), three
curve bundles, and every one at `playback_speed 1.000000`.

### 1.2 Data Wavee already has

| Fact | Where | Notes |
|---|---|---|
| Tempo (BPM), key name, Camelot slot and colour | extension kind 222, `Spotify.Decode.AudioAttributes` (`Spotify/Spotify.Decode.cs:1310-1350`); stored as `TrackTable.Tempo` (×10, `ushort`), `.Key`, `.Camelot` (1..24), `.CamelotColor` (`Entities/Track.cs:178-187`), read as `Track.Tempo/.Camelot` (`Track.cs:380-383`); persisted (`Track.cs:768-771`) | `mode` was 2 on every captured sample (`audio_attributes.proto`): only the B (major) ring is proven on the wire. Camelot code is what the planner uses, never the tonic. |
| The BPM · Key column and facts | `ColumnSet.Tempo` + `RowMetrics.ShowTempo` (`Entities/Track.Rules.cs:118, 400`), labels `TrackRules.Bpm/TempoLabel/CamelotLabel` (`Track.Rules.cs:453, 522, 532`), expanded-row facts `FactKind.Bpm/Key` (`Track.Rules.cs:1185`), per-theme cells in `Themes/{Classic,WinUi,Zune}/Structure/Lists/*RowCells.cs` and `*TrackDrawer.cs` | Shipped since 0.2.x. |
| Three-band waveform, 50 Hz | kind 237, `WaveSample` / `WaveformBands` (`Entities/Waveform.cs:19-91`), edge `Edges.TrackWaveform` | ~38 KB per track; fetched on expand and by the stage. |
| A beat grid with downbeats | `Edges.TrackBeats` (`Entities/Edges.cs:859`), `BeatGrid` (`Entities/Waveform.cs:96-151`: `StartMs`, `IsDownbeat`, `Phase`, `TempoPhase`), decoded from the JSON route `/audio-attributes/v1/audio-analysis/{id}` (`Spotify/Spotify.Api.cs:2201-2208`, `Spotify.Decode.Analysis.cs:21-58`) | Today only the full-screen stage asks for it (`Shell/VisualizerClock.cs:178`). The route is served (`Spotify.Api.cs:843`). The official client does not use this route for Mix: it reads kind 217 (§1.5), whose analysis id the recipe pins. |


### 1.3 The hand-off path today

- **Settings:** `playback.crossfade.enabled` / `.ms` (`Platform/Platform.cs:244-245`), dials `playback.crossfade` and
  `playback.crossfade.duration` (`Screens/SettingsSpec.Playback.cs:25-34`), ceiling 12 s
  (`Playback.Audio.MaxCrossfadeMs`, `Playback/Playback.Audio.cs:105`).
- **Queue switch (0.3.11):** `IQueueVerbs.ToggleCrossfade/SetCrossfade` (`Theming/Contracts/QueueProps.cs:123-126`),
  props `QueuePanelProps.Crossfade/CrossfadeLength/OpenCrossfadeLength` (`QueueProps.cs:241-249`, the stage pane's
  twins at `:280-287`), writer `Shell.QueueVerbs.SetCrossfade` through the Settings dial (`Shell/QueueVerbs.cs:139-141`).
  Drawn by `ClassicQueue.cs:194-202` (pills), `WinUiQueuePanel.cs:351-367` (`BetweenSongs` switch rows),
  `ZuneQueuePanel.cs:135-138` (mode words), `ZuneUpNext.cs:142`, `ClassicQueueStagePane.cs`.
- **The pump** (`Playback/Playback.Audio.cs`, a 200 ms timer, `TickMs` `:89`):
  1. `EndgamePlan.Decide` (`Playback.Endgame.cs:54-78`) opens the ask window `fade + 8 s` before the end
     (`EndingSoonMs`, `Playback.Audio.cs:113`) and posts `EndingSoon`; the reducer answers with
     `Audio.Prepare(nextRow, nextId)` (`Playback.Host.cs:873`).
  2. `PrepareCoreAsync` (`Playback.Audio.cs:3285-3345`) opens the next track's bytes and decoder and prerolls a voice
     (`backend.PrepareAsync`, engine `IPreparableBackend`, `QueuePreparation.cs:32-40`). `PrepareContext` has no start
     offset (`QueuePreparation.cs:18`): **a prepared voice always starts at the head of the track.**
  3. `HandOffAt` (`Playback.Audio.cs:132-139`) picks `Crossfade` when `position >= duration - fade`, else `Gapless`.
  4. `CommitCrossfade` (`Playback.Audio.cs:3465-3524`) starts the fade **now**: `start = sess.SampleClock`, adds B with
     `GainEnvelope.Fade(In, start, frames, EqualPower)` and gives A the matching `Out` envelope, and flips the app's
     "active" identity to B at that same moment (`PostHandedOff`). So a crossfade lands on a 200 ms grid, on whatever
     sample the tick happened to run.
  5. Cross-kind boundaries never overlap: `MediaSwitch.AllowCrossfade(from, to) => from == to && from == Audio`
     (`Playback.Transitions.cs:1672`); `PlayableKind { Audio, Video, LocalFile }` (`Playback/Playback.cs:281`).
- **The engine** (`fluent-gpu/src/FluentGpu.Engine/Media/Playback/Audio/`):
  - `CrossfadeMixer` (`CrossfadeMixer.cs:277`): up to 8 voices, each a `MixVoice` with `StartFrame`, a `GainEnvelope`
    and its own DSP chain (`:140-165`). A voice may start in the future; `MixInto` skips blocks before `StartFrame`
    (`:181-183`).
  - `GainEnvelope` (`CrossfadeMixer.cs:52-133`): a **per-sample lookup table** of `fadeFrames` floats, built off the
    render thread; `FadeStartFrame` is in the mixer (device-clock) domain, so a fade can be scheduled at an exact
    future frame. Curves: `CrossCurve.EqualPower` and `Linear` ("for correlated/beatmatched joins", `:9-10`).
  - `AudioTransitionGate` (`AudioTransitionGate.cs`): one atomic commit/cancel shared by both envelopes of a
    scheduled transition (`GainEnvelope.WithTransition`, `CrossfadeMixer.cs:59`).
  - `PcmAudioSession.TryAddCrossfadeVoice(voice, env, startFrame, replayGain, chain, id)` and
    `SetVoiceEnvelope(id, env)` (`PcmAudioPlayer.cs:1178, 1210`); at most three live ring voices (`:1184`).
  - **Tempo:** every voice is already wrapped in a `WsolaAudioSource` (`PcmAudioPlayer.cs:1186`), a pitch-preserving
    time stretcher (40 ms window, 20 ms hop, byte-exact pass-through at 1.0, `WsolaAudioSource.cs:6-11`). But the rate
    is **one number per session**: each block writes `stretched.Rate = PlaybackRate` on every voice
    (`PcmAudioPlayer.cs:2905-2906`). The app only uses it for episodes (`Playback.SpeedApplies`,
    `Playback/Playback.Speed.cs:17`); a track always plays at 1.0, and Connect state denies speed on tracks.
  - `WsolaAudioSource.SourceFrameAt(outputFrame)` (`WsolaAudioSource.cs:270`) maps output frames back to source
    frames through rate changes; `PcmAudioSession.ContentPositionFrames` (`PcmAudioPlayer.cs:838`) uses it. The app
    reads it only for episodes (`ActivePositionMs`, `Playback.Audio.cs:3964-3981`).
  - `EqStage` (`DspStages.cs:115`, 16 RBJ biquad bands, coefficient cross-ramps) and `GainStage` exist per voice
    (`BuildVoiceChain`, `PcmAudioPlayer.cs:1808`).
  - `OnsetDetector` (`OnsetDetector.cs:13-45`) is a spectral-flux onset detector over the visualizer's band frames at
    a ~15 ms control hop (`Step(bandsDb, nowMs, out flux, out strength)`). It runs on what is *playing*; it is not an
    offline beat tracker and cannot analyse a prepared track ahead of the join.
- **The queue:** `Edges.Queue` is one run in reading order (`Entities/Queue.cs:1-17`); "what plays next" is a forward
  scan from the cursor. Reorders go through `Queue.MoveInSection(section, from, to, cursor)`
  (`Entities/Queue.Rules.cs:669-689`) and then `QueueVerbs.Changed()` → `Playback.Input.QueueChanged`
  (`Shell/QueueVerbs.cs:88-92`); a viewed (remote) queue refuses edits (`QueueModel.IsViewer()`).

**What does not exist:** any read of the mix lens, its attributes or recipes; joins at a planned point with planned
curves; per-voice EQ/filter automation; a prepared voice that starts past the head; a Mix toggle, seams, a Transitions view,
a seam card.

### 1.4 The surfaces Mix lands on, as they are today

| Surface | Today | Where |
|---|---|---|
| Playlist list bar | left group: Play next, Shuffle, **Tune** (on Spotify's Mixes); `TuneCommand(label, lit, onClick)` is the precedent for a lit, per-playlist command | `Theming/Contracts/TrackChromeStyle.cs:84-107`; page side `Entities/Playlist.Page.cs:469-486` |
| An in-page panel under the list bar | the **Tune strip** (`ViewKeys.PlaylistTune`, `PlaylistTuneProps`), one view per theme | `Theming/Contracts/DetailPlaylistProps.cs:134-142`, `ViewKeys.DetailAlbumPlaylist.cs:93-95`; `ClassicPlaylistPanels.cs`, `WinUiPlaylistTune.cs`, `ZunePlaylistPanels.cs` |
| Owner row ("by …") | `ViewKeys.PlaylistOwner`, `PlaylistOwnerProps(…, Label, OwnerName, …)`, one composition for rail and hero | `DetailPlaylistProps.cs:84-99` |
| Track rows | a bound grid per row style (`TrackRowGridTemplate`) and a skin; the tempo lane exists (`ColumnSet.Tempo`); a row can expand into a **track drawer** per theme | `Theming/Contracts/TrackListStyle.cs:28-33`; `Themes/*/Structure/Lists/*TrackDrawer.cs` |
| Player bar | the seek rail and the time labels are their own views with a core host (`ViewKeys.SeekRail`, `ViewKeys.TimeLabel`; `Shell.SeekBar` / `Shell.TimeText`); an episode's chapter ticks are a decoration of the rail | `Theming/Contracts/PlayerTransportProps.cs:5-20, 33, 66, 99` |
| Queue | Autoplay and Crossfade as the "between songs" pair, both Settings dials | §1.3 |
| Playlist edits | `Spotify.PlaylistOps` models `Add/Rem/Mov/UpdateItemAttributes/UpdateListAttributes`; an item op that names `format_attributes` is refused today (`Refusal.ItemAttribute`) | `Spotify/Spotify.Playlist.Ops.cs:53-105` |
| Playlist attributes read | `format_attributes` (a key/value bag) is already folded for daylist, chart and tuning | `Spotify/Spotify.Decode.Playlist.cs:116-131` |


### 1.5 What Wavee's wire code lacks (each line read in the code)

| Need | Today | Where |
|---|---|---|
| Ask for the mix lens | `spotify-apply-lenses` is always the literal `auto` | `Spotify/Spotify.Api.cs:591`; header sets at `Spotify/Spotify.cs:1981, 2046` |
| Signals with args and a typed payload | `SignalsBody` writes one signal `{identifier, interaction{uuid}}`: no f2 args, f3 is only the uuid case | `Spotify/Spotify.Api.Playlist.cs:109-114` |
| The right sync-reason | the route hard-codes `CA8QAQ==` (that is `set-transition`'s reason; `mix-state` uses `CBAQAQ==`) | `Spotify/Spotify.cs:2022-2027` |
| A signals reply that *is* the rebuilt list | already how **Tune** works: POST `/signals`, fold the reply as the list | `Spotify/Spotify.Api.Playlist.cs:473-490` |
| Item attributes | `ItemAttributes` reads f1, f2, f11 (chart keys only) and f12; **f14 is skipped**; the recipe keys in f11 are not looked at | `Spotify/Spotify.Decode.cs:1933-1943` |
| List format attributes | folded for daylist, chart, tuning only | `Spotify/Spotify.Decode.Playlist.cs:116-131` |
| Lens etags (`SelectedListContent` f22) | not read; the list's identity is its revision | — |
| Mix analysis kinds | `Spotify.Decode.Ext` has 222 and 237, not 28, 217, 218, 219, 142, 225, 170, 235 | `Spotify/Spotify.Decode.cs:60-82` |
| A mixed context in Connect state | context URL is `"context://" + uri`, no query; `duration` is the track's duration; no fade metadata | `Spotify/Spotify.Decode.PutState.cs:346, 367`; `Spotify/Spotify.Connect.Play.cs:125, 312` |

Analysis kinds, as the capture shows them (report §3.5):

| Kind | Shape | 0.4 use |
|---|---|---|
| **222** audio attributes | tempo (double), key, mode, Camelot code and colour | have it: the optional column, the seam card's compatibility line, Auto's style choice, the recipe's BPMs |
| **237** three-band waveform | 44.1 kHz, 20 ms hop, three byte bands | have it: the seam card's overlap strip |
| **217** beats | `{1 beats per bar, 2[] {1 float seconds, 3 position in bar 1..4, 4 confidence, 5 1.0 = downbeat}, 3 analysis id (32 hex)}` | **decode**: the grid every time snaps to; the id is pinned in the recipe |
| **28** cue points | `{1 start cue, 2 end cue, 3[] start candidates, 4[] end candidates}`, cue = `{1 ms, 2 float bpm, 3 1, 4 double confidence}` | **decode**: seeds the in and out regions |
| 218 vocal activity | 22050 Hz, hop 315 samples (14.29 ms), one byte per frame | not decoded in 0.4 |
| 219 mixability (track), 235 mixability trait (playlist) | `{1 1}` or `{1 1, 2 1.0}`; `{1 1}` | 235 **decode** (gates the button, **[inferred]**); 219 not used |
| 142 list-tuner audio analysis | `{2 20, 3 bytes}`, layout unknown | not decoded in 0.4 |
| **225** mix state, **170** auto lens | `{1 1}` when mixed; `{1 "mix"}` | **decode** both |

Never requested by the official client: 244, 247, 126, 136 (report §3.5). Revision 2 listed them as storage
candidates; they are out.

### 1.6 A probable bug in shipped Wavee (separate from the 0.4 work)

Verified in `release/0.3.13`:

- Wavee **advertises** the signal `automix-preview` in its Connect state:
  `w.Utf8(33, "automix-preview"u8);` (`Spotify/Spotify.Decode.PutState.cs:439`, beside `interact`, `speed-preview`
  and `stop-speed-preview`, `:438-441`).
- Wavee **acks and drops** it. The `signal` endpoint's own comment says "an unknown `signal_id` is acked and dropped,
  because a refusal caches the sender out of the endpoint for good" (`Spotify/Spotify.Decode.Connect.cs:538-541`), and
  the parser maps every `signal_id` other than `switch-to-video` / `switch-to-audio` to `VideoSignal.None`
  (`:607-614`).

Effect, seen in the capture (report §2 step 5, §3.4): with Wavee as the active device, the official client's editor
sent its transition preview to Wavee, got an ack, and nothing played.

**Fix, independent of 0.4:** stop advertising what is not implemented: delete the one `automix-preview` line (and
check `speed-preview` / `stop-speed-preview` the same way: I did not trace whether Wavee handles those). One line,
size S, a candidate for the next 0.3.x. Caveat: the signals list was copied from the official client's state, and I
could not verify that the official client then previews on itself instead; the owner should check that once with the
desktop editor open. 0.4 keeps it un-advertised (decision D13).

### 1.7 House primitives the new UI uses (each looked up with `fg.py api`, none guessed)

| Need (UX direction) | Primitive | Declared at |
|---|---|---|
| The inline seam card opening between two rows | The track table's own expanded-row drawer: `Track.Table.ToggleExpanded(rowKey, track)` drives an `_expanded` signal, `HoldDrawerTail` keeps the closing drawer presented until `AnimChannel.RevealExtent` settles, `CorrectDrawerExtents` fixes the measured extents. The seam card is a second drawer body on that mechanism. | `Entities/Track.Table.cs:2422-2450`; per-theme frame `Theming/Contracts/TrackDrawerStyle.cs:98` |
| The reveal itself | `SizeMode.FlowReveal` through `LayoutTransition.BoundsT(SizeMode)`; `SizeAnchor` picks the anchored edge | `fluent-gpu/src/FluentGpu.Engine/Foundation/LayoutTransition.cs:32, 46, 144` |
| A list whose rows are disclosed as a band (the Transitions view's rows) | `ListOptions.Disclosure` (`DisclosureOptions`: `Version`, `ResolveRange`) with `ItemsViewController.BeginDisclosure(range, direction, collapseCommit, settled)`; the sidebar uses it | `fluent-gpu/src/FluentGpu.Controls/ListOptions.cs:349, 431`, `ItemsView.cs:245`; `Shell/Sidebar.UI.cs:584` |
| The style choice (Smooth / Lift / Quick / No mix) | `Segmented.Create(IReadOnlyList<SegmentedItem> items, Signal<int>? selectedIndex, Action<int>? onChange, SegmentedOptions? options)`: "two to five mutually-exclusive choices"; `SegmentedItem(string Content, IconRef Icon, bool IsEnabled)` | `fluent-gpu/src/FluentGpu.Controls/Segmented.cs:10, 18, 115` |
| The Tracks / Transitions switch | `SelectorBar.Create(IReadOnlyList<string> items, Signal<int>? selectedIndex, Action<int>? onChange, …)` (the WinUI SelectorBar) | `fluent-gpu/src/FluentGpu.Controls/SelectorBar.cs:16` |
| The Mix toggle ("Mix" / "Mix on", a glyph per state) | `ToggleButton.Create(string label, Signal<bool>? on, Action<bool>? onChange, …, string? glyph, string? checkedGlyph, string? checkedLabel)`; the list bar draws it through the structure's own command (`TrackChromeStyle`, the `TuneCommand` precedent) | `fluent-gpu/src/FluentGpu.Controls/ToggleButton.cs:170`; `Theming/Contracts/TrackChromeStyle.cs:105-107` |
| The two waveforms of the overlap strip | `Waveform.Create(WaveformModel model, FloatSignal? progress, Style? style, string? key)`, `WaveformModel(ReadOnlyMemory<float> Peaks, int Bars = 64)`; the track drawer already draws kind 237 with it (`WaveformBands.ToColumns` → `Waveform.Create`) | `fluent-gpu/src/FluentGpu.Controls/Charts/Waveform.cs:10, 50`; `Entities/Waveform.cs:52`, `Entities/Track.Drawer.cs:190-201`, `Themes/WinUi/Structure/Lists/WinUiTrackDrawer.cs:349` |
| Dragging the overlap region | `BoxEl.OnPointerDown` / `BoxEl.OnDrag` (`Action<Point2>`) writing a `FloatSignal` from `UseFloatSignal`, bound to the region's `Width` / `Transform` (no state per pointer move) | `fluent-gpu/src/FluentGpu.Engine/Dsl/Element.cs:256-257`; `Hooks/Component.cs:24` |
| The hover seam | `BoxEl.OnHoverMove`, `OnPointerExit`, and `Element.WhileHover` (a `MotionTarget`) on a thin strip inside the row | `Element.cs:67, 309, 316` |
| The seek bar's overlap segment | A decoration of the seek rail like the chapter ticks: `ViewKeys.SeekRail` / `SeekRailProps` / `ISeekRailHost`, and `SeekChaptersProps` ("Pure decoration: nothing here takes input") as the precedent | `Theming/Contracts/PlayerTransportProps.cs:33, 66-67, 99-112` |
| The "Mixing into …" time label | `ViewKeys.TimeLabel` (`TimeLabelProps`: "the digits' slot (a bound text channel), or the live slot") | `Theming/Contracts/ViewKeys.Player.cs:41`; `PlayerTransportProps.cs:20` |
| The card as a sheet at Compact | `Controls.Sheet(IOverlayService? overlay, string title, Element body, …)` | `Platform/Controls.cs:1574` |
| A disclosure button a theme skins | `Controls.Disclosure(bool expanded, Action toggle, DisclosureSkin skin)` | `Platform/Controls.Disclosure.cs:26, 33` |
| The Transitions list | `ItemsView.CreateBound(int itemCount, Func<RowScope, Element> rowTemplate, RepeatLayout layout, ListOptions? options)`; loading rows with `Skel.Region(...)` | `fluent-gpu/src/FluentGpu.Controls/ItemsView.cs:801`; `fluent-gpu/src/FluentGpu.Engine/Hooks/SkeletonRegion.cs:84` |
| Tooltips on the seam handle and the style choices | `ToolTip.Wrap(Element target, string text, …)` | `fluent-gpu/src/FluentGpu.Controls/ToolTip.cs:229` |
| Keys (Enter opens a seam, Space previews) | the list's own invoke (`ListOptions.OnInvoked`), plus `KeyAccelerator(int Key, KeyModifiers Mods)` on the card's buttons | `fluent-gpu/src/FluentGpu.Engine/Foundation/Events.cs:228` |
| "Put playback back" after Hear it | `Playback.RestorePoint(Track, Context, CursorIndex, PositionMs, DurationMs, Shuffle, Repeat, Rows)` exists for the launch restore. *Not verified* that it can be applied mid-session; phase 7 reads the restore path first. | `Playback/Playback.Transitions.cs:108` |

Nothing in this list is new engine UI work. No `RangeSlider` exists (`fg api RangeSlider`: no declaration); the
overlap region is a `BoxEl` with the two pointer handlers above, not a new control.

---

## 2. Product decisions for the owner

Revised for the UX direction (`48-mix-ux-direction.md`, 2026-11-23). Decisions that are only wire or engine facts
keep revision 3's wording.

| # | Decision | Options | Recommendation |
|---|---|---|---|
| **D1** | Scope of 0.4 | (a) honour and switch only; (b) **honour, switch, and the inline seam card**: four styles plus Auto, the overlap length, Hear it, Reset, Done | **(b)**, with (a) as the stated cut (§4). No dropdown rows, no beatmatching, no curve editing: the direction and the capture agree on that. |
| **D2** | Where Mix lives | one Mix toggle on the playlist header, state on Spotify | Decided. |
| **D3** | Turning Mix **off** | write it to Spotify; **a local "listen without mix" for this playlist** | Local until the off request is captured (O1), then Spotify's for a playlist the user owns. |
| **D4** | Global Crossfade while a mixed playlist plays | Mix owns every join it can make; Crossfade/gapless is the base hand-off for the rest | Unchanged. The queue's Crossfade row says "Mix is on for this playlist". |
| **D5** | **Shuffle with Auto** (the direction asks; revision 3 mixed only pairs consecutive in the playlist's order) | (a) revision 3: with shuffle on, nothing is mixed; (b) **any two rows of the mixed playlist that play one after the other get Wavee's Auto; a saved transition applies only when its own pair is adjacent in play order** | **(b).** Auto is computed on this PC anyway, so the order does not matter to it; a recipe names its from and to items, so it can only apply to that pair. The header then reads "Shuffle: auto transitions". A song queued from elsewhere, autoplay, an episode, a video or a local file still gets the base hand-off. Cost: the planner's context check changes from "next in list order" to "both rows belong to the mixed playlist" (S). |
| **D6** | Beatmatching | after 0.4 | Unchanged; not shown in the card. |
| **D7** | Incoming track starts mid-file | yes | Confirmed required (engine E3). |
| **D8** | EQ and filter automation | in 0.4, as curve rendering | Unchanged (engine E2). |
| **D9** | BPM and key in the player bar | ~~only in a mixed playlist~~ | **Superseded by the direction: not shown.** The bar shows mixing while it happens (D14) and nothing else. BPM and key stay an optional list column, unrelated to Mix; revision 3's `PlayerBarProps.TempoKey` is dropped. |
| **D10** | Where a transition is edited | inline, between its two rows | Unchanged in substance: the seam card (§3.11). |
| **D11** | **"Saved on this PC only"** (the direction leaves it to the owner; revision 3 said no Wavee-only store) | (a) Spotify only: what Spotify cannot hold cannot be chosen; (b) **a small local layer for exactly what Spotify cannot hold, always labelled** | **(b), narrowly.** One record per pair, `MixOverride { NoMix \| Auto \| Recipe }`, used only for: "No mix" (D16); "Reset to auto" on a pair Spotify has a recipe for (the reset request is not captured, O6); an edit on a playlist the user does not own; a pair Save is not yet proven for (O5). Everything else is saved to Spotify. A local record wins on this PC and the card says "Saved on this PC only". Without (b), principle 6 of the direction ("every pair can opt out") cannot ship. |
| **D12** | "Arrange for mixing" | after 0.4 | Unchanged. |
| **D13** | The Connect preview signal | stop advertising it | Unchanged (§1.6). "Hear it" plays on this PC. |
| **D14** | **Which track the player shows during the overlap** (new conflict: the direction wants the seek bar to show the overlap as its end segment and the label "Mixing into \<next\>"; revision 3 flips to the incoming track at the overlap's start, as the wire's shortened duration does) | (a) revision 3: flip at the overlap's start; the outgoing bar ends at the mix-out, so there is no end segment to play through, and the label during the overlap would have to read "Mixing in" on the incoming track; (b) **flip when the outgoing track is over**: the bar runs to the outgoing track's audible end, its last segment is the overlap, the label reads "Mixing into …", then the incoming track appears already `overlap` seconds in | **(b).** It is what the direction draws, and it makes a seek during the overlap well defined (it is a seek in the song on screen, which cancels the join). Its costs are stated in §3.6a: the pump announces at the overlap's end (the gapless join's pattern), Connect's duration becomes the audible end (longer than the capture's by the overlap), and #154 is told `Master.Outgoing` for the window. |
| **D15** | Wavee's Auto | a simple, stated planner | Unchanged, plus the direction's recommendation rule: **Smooth by default, Quick when the tempos are far apart** (the captured 117 → 77 BPM pair got a 2.9 s fade from the official client too). |
| **D16** | **"No mix" for one pair** (new; the top complaint about Spotify's Mix) | (a) **local only**: an override on this PC, the pair plays the base hand-off here, other devices still mix it; (b) a capture task first: look for a per-pair opt-out in the official client (the web research says there is none); (c) approximate it on Spotify as the shortest transition the server accepts, so other devices almost cut | **(a) for 0.4**, labelled "Saved on this PC only". (b) costs one look and is listed (O16). (c) writes a value never seen on the wire and still is not "no mix", so it waits for O15. |
| **D17** | **"Quick" as a Wavee-side style** (new) | (a) **Fade (id 1) with a short overlap: the whole number of beats nearest 2.5 s**; (b) shorter: one or two beats | **(a).** 2510 ms is the shortest overlap in the capture (three Auto pairs), so the recipe stays inside what Spotify's own client produces and Quick can be saved to Spotify. It needs no storage of its own: a Fade recipe whose overlap is at most 2.6 s reads back as Quick. (b) is a truer "cut" but writes unseen values (O15). |
| **D18** | **A quiet list** (new, from the direction) | chips between all rows (Spotify, revision 3); **no chips: a seam on hover, a dot for an edited pair, a break for "No mix", and a Transitions view** | Decided by the direction. One addition for keyboard and touch, where there is no hover: the row's own menu gains "Transition to next song" (opens the same card). |
| **D19** | Which lengths the overlap can take | free; **whole beats of the outgoing track, 2 to 32 beats, at most 16 s** | **Whole beats.** The strip snaps to the kind 217 grid; a pair without a grid offers 2.5 / 5 / 10 s. Writing a changed overlap uses recipe fields 1.3 and 1.5, both read **[inferred]** in the report but matched against the Connect metadata. |

---

## 3. Architecture

### 3.1 The shape

```
 Spotify                               UI thread (entities, reducer, host)                 pump thread (200 ms)        engine render thread
┌───────────────────────────┐  read  ┌──────────────────────────────────────────┐ MixTiming ┌───────────────────────┐ cmds ┌──────────────────────────┐
│ playlist + mix lens       │ ─────► │ Playlist.MixOn / MixLensEtag             │ (a value, │ Playback.Audio        │ ───► │ PcmAudioSession          │
│  list attrs, item attrs   │        │ Edges.PlaylistTransitions (recipes)      │  copied   │  MixRender.Build      │      │  AddVoice(B @ S, offset) │
│  recipe on outgoing item  │        │ Edges.TrackBeats (217) / TrackCues (28)  │  under    │   → 2 gain envelopes  │      │  SetEnvelope(A), (B)     │
│ XM 225 / 170 / 235        │        │ MixPlanner.Plan(pair) → MixTiming        │  s_gate)  │   → 2 automations     │      │  SetVoiceAutomation ×2   │
│ XM 217 / 28 / 222 / 237   │        │   = recipe timing, or Wavee's Auto       │ ────────► │  ScheduleMix/Announce │      │ CrossfadeMixer + stages  │
└───────────────────────────┘        │ MixCurves (preset → Bézier segments)     │           │  Timeline.SetTransition│     └──────────────────────────┘
        ▲  /signals: mix-state,      │ Spotify.Mixes.TurnOn / SaveTransition    │           └───────────────────────┘
        └─ set-transition ◄──────────│ Connect: lens URL, duration = mix-out,   │
                                     │   audio.fade_* metadata                  │
                                     └──────────────────────────────────────────┘
          header: Mix toggle + meta line · list: seams, Transitions view, inline seam card · player bar: "Mixing into …"
```

Rules that shape it:

- **One timing value per pair, computed on the UI thread in milliseconds.** `MixTiming` is the recipe's timing when
  the pair was edited, else the auto planner's. The Transitions view's row, the seam card's strip, the prepare's start offset, the Connect
  metadata, the seek bar's end and the pump all read that one value. The pump only converts it to frames.
- **Curves are data.** A preset is a table of Bézier segments (`MixCurves`). The engine samples them into tables off
  the render thread; the same segments are written as JSON into the Connect metadata.
- **The wire code is three files** (`Spotify.Decode.Mix.cs`, `Spotify.Api.Mix.cs`, `Entities/Mix.Recipe.cs`). A later
  capture (a new preset id, the off request) changes those and a table, not the planner, the engine or the UI.
- **The entity tables are UI-thread, single-writer** (`Entities/Queue.cs` header, C1); the pump never reads them.
- **A scheduled join, not a "now" join.** Today's crossfade starts on whatever sample the 200 ms tick ran
  (`CommitCrossfade`, `Playback.Audio.cs:3465`). A mixed join is committed at an exact future mixer frame, the way
  the gapless join already is (`s_joinPending` / `s_joinFrame`, `Playback.Audio.cs:4072-4078`).

### 3.2 The wire adapter

**Reading a mixed playlist.**

- `HeaderSet` gains `MixLens`: with it, `spotify-apply-lenses` is `mix,auto` (and the diff's
  `spotify-applied-lenses` follows; its exact value for the mix lens was not shown in the report: **open**, §5.2 O11).
  `Spotify.Api.cs:591` becomes a choice between the two literals.
- When to ask: the playlist's header extension batch gains kinds **225**, **170** and **235** (the official client
  fetches 225 with the header bundle; report §2 step 3). `Playlist.MixOn` = kind 225 `{1:1}` or list attribute
  `mix=true`. A playlist with `MixOn` (and without the local "listen without mix" flag) is read with the mix lens.
  Whether a read with only `auto` already returns the mix attributes for a mixed list is **open** (O11); the first
  open of a mixed playlist may therefore cost one extra read, done once when 225 lands.
- `Spotify.Decode.PlaylistFormatAttributes` (`Spotify.Decode.Playlist.cs:131`) also folds `mix`, `mix-type`,
  `has-custom-transitions`, `can-view-transition`, `automix.mode`, `automix.ignore_setting` into a new field group
  `PlaylistFields.Mix`.
- `ItemAttributes` (`Spotify.Decode.cs:1933`) reads, from f11: `automix.auto_transition_recipe`,
  `automix.transition_uri`, `not-mixable`, `not-mixable.reason`; and **f14** (present = this item's outgoing
  transition is editable; its args are `{"mix", the item's track uri, the item's id}`, all of which Wavee already
  holds, so only the presence bit is stored).
- `SelectedListContent` **f22** (applied lenses) is read: the `mix` lens's etag becomes `Playlist.MixLensEtag`.
  **The playlist's cached content is identified by (revision, mix lens etag), not by revision alone**, because a Save
  changes the items and leaves the revision at 83 (report §3.2). *Unverified:* I did not read how Wavee's playlist
  cache and diff replay key their baselines (`docs/plans/wavee/cache-integrity-and-playlist-diff-implementation.md`);
  phase 1 starts there.

```csharp
// Entities/Mix.Recipe.cs (new, CORE): the stored transition
/// <summary>Spotify's TransitionRecipe (report §4.2). Field numbers in comments; "?" = meaning not proven.</summary>
public struct MixRecipe
{
    public ulong FromItemId, ToItemId;      // 1.1, 1.2 (hex strings on the wire); 0 in the "previous" recipe of a Save
    public uint OutStartMs;                 // 1.3  fade_out_start_time, ms into A
    public uint InStartMs;                  // 1.4? fade_in_start_time, ms into B (absent = 0)
    public uint OverlapMs;                  // 1.5
    public float RateA, RateB;              // 1.6?, 1.7?  (1.0 in every sample)
    public bool Beatmatch;                  // 1.8?        (0 in every sample)
    public EntityId TrackA, TrackB;         // 1.10, 1.11 (and 1.12, 1.13 written as the same uris)
    public UInt128 BeatsIdA, BeatsIdB;      // 1.14, 1.15 = kind 217 f3 of A and B
    public float BpmA, BpmB;                // 1.16, 1.17
    public byte Preset;                     // 2.1: 1 = Fade, 3 = Rise
    public byte StyleVersion;               // 2.2? (1 in every sample)

    public static bool TryDecode(ReadOnlySpan<byte> proto, out MixRecipe recipe);
    /// <summary>Encode. Fields this struct does not model (1.18, 1.19, f3, f4, anything new) are copied verbatim
    /// from <paramref name="carry"/> (the recipe being replaced) or written with the captured constants
    /// (1.18 = 1.19 = 1.0, f3 = 1, f4 = 1) when there is none.</summary>
    public static int Encode(in MixRecipe recipe, ReadOnlySpan<byte> carry, Span<byte> into);
}
```

`Edges.PlaylistTransitions` is an `EdgeTable<MixRecipe>` parented on the playlist, one entry per item that has a
recipe, keyed by the from item's id; the raw bytes are kept beside it (a `StringId`) for the verbatim carry. Per row,
two bits ride the existing playlist item edge: `Mixable` (has f14) and `NotMixable`.

**Writing.** `SignalsBody` gets its general form (report §3.2, "AvailableSignal shape"):

```csharp
// Spotify/Spotify.Api.Playlist.cs: replaces today's SignalsBody (the Tune caller passes its uuid payload through it)
public static byte[] SignalsBody(ReadOnlySpan<byte> revision, string identifier, ReadOnlySpan<byte> args, ReadOnlySpan<byte> payload);

// Spotify/Spotify.Api.Mix.cs (new)
public static partial class Spotify { public static class Mixes {
    /// <summary>mix-state, args {1:"mix"}, payload {1:1}, lens mix,auto, sync-reason CBAQAQ==. The reply is the mixed
    /// list and is folded as a list read (the Tune path). Optimistic; reverts on a refusal.</summary>
    public static void TurnOn(Playlist p, Action<bool>? settled);
    /// <summary>set-transition, args {1:"mix", 2: from track uri, 3: from item id}, payload {1: new recipe b64,
    /// 3: previous recipe b64}, sync-reason CA8QAQ==. The reply is folded as a list read; the server mints
    /// automix.transition_uri.</summary>
    public static void SaveTransition(Playlist p, in MixRecipe next, in MixRecipe previousAuto, Action<bool>? settled);
    /// <summary>The local "listen without mix" flag for one playlist (Platform.Settings, keyed by uri): the playlist is
    /// then read and played without the lens. Nothing is sent.</summary>
    public static void SetListenWithoutMix(Playlist p, bool without);
}}
```

`RequestKind.PlaylistSignals` (`Spotify.cs:2022`) takes the sync-reason and the lens from its arguments instead of
hard-coding `CA8QAQ==`. Requests also carry `spotify-list-player-enabled: true` as the capture shows.

**Not written in 0.4** because the request is not captured: turning Mix off on Spotify, and resetting an edited
transition to Auto.

### 3.3 Track data: beats and cue points

Add to `Spotify.Decode.Ext` (`Spotify.Decode.cs:60`): `Cuepoints = 28`, `Beats = 217`, `MixState = 225`,
`AutoLens = 170`, `MixabilityTrait = 235`.

```csharp
// Entities/Waveform.cs: beside BeatGrid
/// <summary>Kind 28: the chosen start and end cue. 0 = none. The candidate lists are not kept.</summary>
public readonly record struct TrackCues(uint StartCueMs, uint EndCueMs, ushort TempoX10);

// Spotify/Spotify.Decode.Mix.cs (new, CORE)
/// <summary>Kind 217 → Edges.TrackBeats in BeatGrid's packing (start ms in bits 0..30, bit 31 = downbeat: position 1,
/// which is where f5 = 1.0 sits), and the analysis id (f3) into TrackTable.BeatsId.</summary>
public static void MixBeats(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s);
public static void Cuepoints(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s);
public static void MixState(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> playlistUri, Staging s);     // 225, 170, 235
```

- **One beat source.** `FetchEdge.TrackBeats` moves from the JSON route
  (`FetchRoute.Spclient(SpclientRoute.AudioAnalysis, 0)`, `Entities/Fetch.Routes.cs:469`) to
  `FetchRoute.Metadata(Beats, 0)`, and `Spotify.Decode.Analysis.cs`, `Spotify.Api.AudioAnalysis*`
  (`Spotify.Api.cs:2201-2230`) and the `analysis` headless verb are **deleted**. The recipe pins kind 217's analysis
  id, so 217 has to be the grid. The visualizer's Pulse and Horizon read the same packing and gain real bar positions.
  **Open (O12):** kind 217 was only seen requested for tracks of a mixed playlist; if the server refuses it
  elsewhere, the visualizer keeps the JSON route for non-mixed tracks and this bullet's deletion is dropped.
- `Edges.TrackCues`: a payload-only `EdgeTable<TrackCues>` (the `TrackWaveform` precedent, `Entities/Edges.cs:847-859`),
  `TraitRelation.TrackCues` appended (`Edges.cs:1040`), `FetchEdge.TrackCues => FetchRoute.Metadata(Cuepoints, 0)`.
- **When they are fetched:** while a mixed playlist is the playing context, for the playing track and the next two
  rows (so the next pair's timing is known a track ahead); and for the two tracks of a pair whose seam card is open,
  and for the rows the Transitions view has realized.
  The official client fetches the whole list's bundle at Mix-on (ten items in the capture); Wavee does not, to stay
  cheap on long playlists. Both kinds ride one extended-metadata POST (`Spotify.Api.Extension`, `Spotify.Api.cs:750`).
- `--fake`: `Entities.Fake` seeds tempo and Camelot already (`Entities/Entities.Fake.cs:339-340`) and `TrackBeats`
  empty (`Entities.Fake.Album.cs:585, 677`). Phase 1 adds one mixed fake playlist: synthetic grids from the seeded
  tempo, cue points, one stored Rise recipe, one local row.

### 3.4 The model: timing and curves

```csharp
// Entities/Mix.cs (new, CORE)
public enum MixPreset : byte { None = 0, Fade = 1, Rise = 3 }          // the wire's 2.1; more rows when captured
public enum MixSource : byte { None, Auto, Saved }
public enum MixWhyNot : byte { None, NotMixed, ListenWithoutMix, NotInMixedList, PairOptOut, NotMixable, LocalFile, CrossKind, NoData, TooShort, Late }

/// <summary>One join, in milliseconds. The single value every layer reads.</summary>
public readonly record struct MixTiming(
    MixSource Source, MixWhyNot WhyNot, byte Preset,   // Preset is the wire id; an id Wavee does not know renders as Fade
    uint OutStartMs,      // A: where the transition starts = A's effective end (audio.fade_out_start_time)
    uint OutDurationMs,   // A's curves run over this (audio.fade_out_duration = audio.fade_overlap)
    uint InStartMs,       // B: source position it enters at (audio.fade_in_start_time)
    uint InDurationMs)    // B's curves run over this (audio.fade_in_duration); may differ from OutDurationMs
{
    public bool Mix => Source != MixSource.None;
}

/// <summary>One segment of a fade curve, as the Connect metadata spells it (report §4.3): Start/End are 0..1 of the
/// window; two points are a line, four a cubic Bézier P0..P3 with x 0..1 inside the segment.</summary>
public readonly record struct CurveSegment(float Start, float End, bool Cubic, float X0, float Y0, float X1, float Y1, float X2, float Y2, float X3, float Y3);

public enum MixLane : byte { Volume, LowEq, FilterCutoff, FilterResonance }

public static class MixCurves
{
    /// <summary>The preset's curve for one lane and side, for a window of <paramref name="units"/> (8 or 16). Writes at
    /// most 4 segments; returns the count (0 = the lane is unused: the metadata key is omitted).</summary>
    public static int Segments(byte preset, MixLane lane, bool incoming, int units, Span<CurveSegment> into);
    /// <summary>y at window position x (0..1): the Bézier is solved for t by bisection on x. Pure.</summary>
    public static float Evaluate(ReadOnlySpan<CurveSegment> curve, float x);
    /// <summary>Sample into a table (the engine's curve-envelope and automation input).</summary>
    public static void Sample(ReadOnlySpan<CurveSegment> curve, Span<float> table);
    /// <summary>The Connect metadata value: [{"start_point":…,"end_point":…,"fade_curve":[{"x":…,"y":…},…]},…].</summary>
    public static int WriteJson(ReadOnlySpan<CurveSegment> curve, Span<byte> utf8);
}
```

**The preset table**, from report §4.2 and §4.3 (curve ids are the report's):

| Preset (wire id) | Volume out / in | Low EQ out / in | Filter cutoff out / in | Resonance | In words |
|---|---|---|---|---|---|
| **Fade (1)** | C1 / C3 | C2 / C4 | none | none | Smooth crossfade, center bass swap |
| **Rise (3)** | C5 / C8 | C14 / C16 | C15 / C9 | C7 | Overlap, end bass swap, low pass in, high pass out |
| *a third bundle, seen on two Auto transitions; name and id unknown* | C5 / C8 (C10 / C12) | C2 / C4 (C11 / C13) | C6 / C9 | — | Overlap, center bass swap, low pass out and in |
| Blend, Wave, Melt | not captured | | | | |

The curves themselves (y scales: volume 1 = unity; low EQ 0.5 = flat, 0 = bass killed; cutoff 0 = full low-pass,
0.5 = bypass, 1 = full high-pass **[inferred]**; resonance 0.5 = default):

| Id | Shape |
|---|---|
| C1 / C3 | volume, smooth: one cubic over the whole window, `(0,1)(.6,1)(.6,0)(1,0)` out, `(0,0)(.4,0)(.4,1)(1,1)` in |
| C5, C10 / C8, C12 | volume, overlap: unity until 0.998 (0.999), then a fast S to 0; in: a fast S to 1 within 0.002 (0.001), then unity |
| C2, C11 / C4, C13 | low EQ, center swap: flat until 0.46875 (0.484), S to killed by 0.5; in: killed until then, S to flat by 0.5 |
| C14 / C16 | low EQ, end swap: flat until 0.96875, S to killed by 1; in: killed until 0.96875, S to flat by 1 |
| C15 | cutoff out, high-pass sweep: 0.5 for the first half, linear 0.5 → 1 over the second |
| C6 | cutoff out, low-pass sweep: 0.5 for the first half, linear 0.5 → 0 over the second |
| C9 | cutoff in, low-pass opening: linear 0 → 0.5 over the first half, then 0.5 |
| C7 | resonance: flat 0.5 |

The paired values (0.998 / 0.999, 0.46875 / 0.484) come from windows of different length. They fit "the bass swap
lasts 1/32 of an 8-unit window and 1/64 of a 16-unit one; the fast volume S lasts 1/512 and 1/1024" (my inference
from two samples each; the 11.6 s window is the 16-unit one). `MixCurves.Segments` takes `units` for that reason:
8 when the window is under 8 s, else 16, until a capture says otherwise (O10).

**What the curve values mean in Hz and dB is not on the wire**: the DSP is local to each client (report §5). Wavee's
mapping, to be tuned by ear against the official client on the same saved transition:

| Lane | Mapping |
|---|---|
| Low EQ `y` | low shelf at 200 Hz, gain `dB = (y − 0.5) × 2 × 26` clamped to [−26, 0] (0.5 → 0 dB, 0 → −26 dB) |
| Cutoff `y < 0.5` | low-pass, `fc = 120 Hz × (20000 / 120)^(y / 0.5)`; `y = 0.5` bypass |
| Cutoff `y > 0.5` | high-pass, `fc = 20 Hz × (6000 / 20)^((y − 0.5) / 0.5)` |
| Resonance `r` | `Q = 0.707 × 4^(r − 0.5)` (0.5 → 0.707) |

### 3.5 The planner

```csharp
// Playback/Playback.Mix.cs (new, CORE)
public static partial class Playback
{
    public static class Mix
    {
        public enum KeyRelation : byte { Unknown, Same, Adjacent, Relative, EnergyUp, Clash }
        public enum TempoRelation : byte { Unknown, Matched, HalfTime, DoubleTime, Apart }
        public const double TempoTolerance = 0.06;

        /// <summary>Camelot bytes are 1..24: number = (c + 1) / 2, ring = c & 1 (1 = A, 0 = B)
        /// (Spotify.Decode.CamelotCode: "1A" → 1, "1B" → 2 … "12B" → 24).</summary>
        public static KeyRelation Key(byte from, byte to)
        {
            if (from is 0 or > 24 || to is 0 or > 24) return KeyRelation.Unknown;
            int nf = (from + 1) / 2, nt = (to + 1) / 2;
            bool sameRing = (from & 1) == (to & 1);
            int step = ((nt - nf) % 12 + 12) % 12;
            if (step == 0) return sameRing ? KeyRelation.Same : KeyRelation.Relative;
            if (sameRing && (step == 1 || step == 11)) return KeyRelation.Adjacent;
            if (sameRing && step == 2) return KeyRelation.EnergyUp;
            return KeyRelation.Clash;
        }

        /// <summary>Tempo ×10 on both sides; half and double time count as matched.</summary>
        public static TempoRelation Tempo(ushort outX10, ushort inX10)
        {
            if (outX10 == 0 || inX10 == 0) return TempoRelation.Unknown;
            double r = (double)outX10 / inX10;
            if (Math.Abs(r - 1) <= TempoTolerance) return TempoRelation.Matched;
            if (Math.Abs(r / 2 - 1) <= TempoTolerance) return TempoRelation.DoubleTime;
            if (Math.Abs(r * 2 - 1) <= TempoTolerance) return TempoRelation.HalfTime;
            return TempoRelation.Apart;
        }
    }

    public static class MixPlanner
    {
        public const uint MinOverlapMs = 2_500, MaxOverlapMs = 16_000, DefaultOverlapMs = 2_510;

        /// <summary>UI thread, pure: the timing of the join from <paramref name="a"/> to <paramref name="b"/>.
        /// A saved recipe wins; otherwise Wavee's Auto. Spans are the tracks' Edges.TrackBeats payloads (may be empty).</summary>
        public static MixTiming Plan(in PairFacts a, ReadOnlySpan<uint> beatsA, in PairFacts b, ReadOnlySpan<uint> beatsB,
            bool hasRecipe, in MixRecipe recipe, MixWhyNot contextVerdict);
    }

    /// <summary>What the planner reads of one track: a value built from the track row and its cue edge.</summary>
    public readonly record struct PairFacts(EntityId Id, long DurationMs, ushort TempoX10, byte Camelot, TrackCues Cues, bool Mixable, bool IsLocal);
}
```

`Key` and `Tempo` feed the seam card's compatibility line (§3.11) and Auto's choice of Quick (§3.6a). The official client computes its
"!" warning itself too; there is no wire flag (report §5).

**A saved recipe → timing** (`Source = Saved`): `OutStartMs` = 1.3, `OutDurationMs` = 1.5, `InStartMs` = 1.4 (0 when
absent), `Preset` = 2.1. `InDurationMs` is not in the recipe; the client derives it. Wavee: the same number of
beats in B's grid as the overlap is in A's (rounded to 4, 8 or 16) when both grids are usable, else the overlap
(the one saved transition in the capture has 2890 on both sides). A recipe whose pinned beat-analysis ids (1.14 /
1.15) differ from the tracks' current kind 217 ids is still played at its stored times; the mismatch is logged.

**Wavee's Auto** (`Source = Auto`), decision D15. Spotify's heuristic is not on the wire; this is Wavee's own, built
from what the nine captured auto plans show (report §3.3 table, §5):

1. **Refusals** (`Source = None`, the base hand-off runs): the context verdict (not mixed, listening without mix, a
   row that does not belong to the mixed playlist: §3.6a item 1); a local "No mix" for the pair (§3.6a item 2);
   either item not mixable or local; either side an episode or a video;
   A shorter than 30 s; neither a cue nor a grid for A (`NoData`).
2. **Out region:** A's end cue (kind 28 f2) when it is inside A's last 60 s, else A's duration.
3. **Overlap:** 8 beats of A at the local beat period near the out region; 4 beats when that exceeds
   `MaxOverlapMs`, 16 when it is under `MinOverlapMs`; `DefaultOverlapMs` when A has no usable grid. (Captured:
   3331 ms = 8 beats at 144.05 BPM; 6233 ms = 8 beats at 77.0 BPM; 2510 ms on three pairs, which reads as the
   client's default when it does not use the grid.)
4. **Out start:** the downbeat nearest `out region − overlap`, moved back a bar at a time until
   `OutStartMs + OutDurationMs ≤ duration` (captured: the edited pair's window ends 1 ms before the file does).
5. **In start:** B's start cue (kind 28 f1) snapped to the nearest downbeat of B; 0 when that lands inside B's first
   bar or B has no grid. (Captured: 0 on six pairs; 2874, 4144, 7794 ms on three, each a kind 217 downbeat and none
   of them a kind 28 candidate.)
6. **In duration:** the same beat count in B's grid; the overlap when B has no grid. (Captured: 3289 ms = 8 beats at
   145.97 BPM against an overlap of 3331 ms.)
7. **Style:** Smooth (Fade, id 1); Quick (Fade with the short overlap, §3.6a item 3) when the tempos are far apart.
   Auto only ever picks a preset whose wire id is known, because Save sends the auto recipe as its "previous" value
   (report §3.2). Whether Auto should pick Lift for some pairs is a question for the DJ session.

The nine captured plans are the **reference set**, not a gate: phase 3 prints Wavee's timing beside the desktop's
for the same playlist (a diagnostics table) and the differences are read by a person. Matching the grid-snapped
cases (items 5 and 6 above) is the target; the 117 → 77 BPM pair's 2890 ms window does not follow any rule the
capture reveals.

### 3.6 The pump (`Playback/Playback.Audio.cs`, `Playback.Endgame.cs`, `Playback.Host.cs`, the reducer)

This section is revision 3's and describes the join with the app's identity flipping at the overlap's first frame
(decision D14 (a)). The recommended D14 (b) moves that one moment; §3.6a item 4 lists exactly what differs.

- **The timing is known a track ahead.** The host owns `Playback.Mix.Current`: on `Started`, on a queue change, on
  the playlist's mix state or transitions changing, and when the beat or cue edges of the playing or next track
  land, it recomputes `MixPlanner.Plan(current, next)` and, when the value changed, posts
  `Input.MixTiming(epoch, timing)`. The reducer stores it. From that one value:
  - `State.DurationMs` of the playing track becomes `OutStartMs` while a mix is planned (D14 (a); with (b) it is the audible end, §3.6a), so the seek bar, the
    remaining time, SMTC and the Connect state all end at the mix-out. A remote `seek_to` past it is clamped.
  - `fx.PrepareNext` carries it: `Audio.Prepare(row, id, kind, in timing)`.
- **Prepare at an offset.** `PrepareCoreAsync` (`Playback.Audio.cs:3285`) passes `timing.InStartMs` into
  `PrepareContext` (engine E3) and the pump keeps the timing it was prepared for as `s_mix`. A later timing for
  the same pair is ignored unless the host re-issues the prepare, which it does only when the timing changes more
  than 10 s before the mix-out.
- **The endgame's boundary is the mix-out.** The pump's own `s_activeDurMs` stays the file's length. While `s_mix.Mix`,
  the three places that do endgame arithmetic (`EndgamePlan.Decide`'s call at `Playback.Audio.cs:4030`, and
  `:3850`, `:3927`) read `s_mix.OutStartMs` as the boundary with `fadeMs = 0`, so the ask window opens 8 s before the
  mix-out (`EndingSoonMs`, `:113`).
- **Schedule above `HandOffAt`:**

```csharp
// in Tick, where `s_prepItem is { IsReady: true }` (Playback.Audio.cs:4054), before the HandOffAt switch
if (s_mix.Mix && !s_mixScheduled && !s_mixRefused)
{
    long startFrame = sess.SampleClock + GaplessJoinClock.MsToFrames(s_mix.OutStartMs - activePos, sess.Format.SampleRate);
    long lead = startFrame - sess.SampleClock;
    if (!s_prepOverlap) Refuse(MixWhyNot.CrossKind);                                             // see "Refuse" below
    else if (lead < MinLeadFrames(sess)) Refuse(MixWhyNot.Late);                                 // 300 ms
    else if (lead <= 2 * sess.Format.SampleRate) ScheduleMix(sess, item, startFrame);            // within 2 s: commit
}
if (s_mixScheduled) { /* the base HandOffAt switch is skipped */ }
```

- **`Refuse`** (rare: the prepared slot may not overlap, or the tick came too late) degrades to the existing
  gapless butt-join at the mix-out with B from the position it was prepared at (`CommitGaplessJoin` against the
  same boundary): the seek bar already ends there, so the track must too. The log line says why.
- **`ScheduleMix`** is `CommitCrossfade` (`Playback.Audio.cs:3465-3524`) split in two, the way the gapless path is:
  1. *Schedule (now, off the render thread):* `MixRender.Build(in s_mix, sampleRate, startFrame, out outEnv, out inEnv,
     out outAuto, out inAuto)` samples the preset's curves (`MixCurves.Sample`) into two gain envelopes and two
     automations and applies §3.4's Hz/dB mapping. Then `var gate = new AudioTransitionGate(startFrame)`;
     `sess.TryAddCrossfadeVoice(B, inEnv.WithTransition(gate), startFrame, rg, chain, id)`;
     `sess.SetVoiceEnvelope(A, outEnv.WithTransition(gate))`; `sess.SetVoiceAutomation(A, outAuto)`;
     `sess.SetVoiceAutomation(id, inAuto)` (engine E1, E2). The app's active identity does not change yet.
  2. *Announce (the tick where `sess.SampleClock >= startFrame`):* the identity swap `CommitCrossfade` does at
     `:3490-3520`, with `s_anchorPlayheadMs = s_mix.InStartMs` instead of 0; `PostHandedOff`;
     `s_crossfadeInFlight = true`; `Playback.Timeline.SetTransition(...)` (§3.9). The existing close-out
     (`:4080-4085`) runs against `OutDurationMs`: A's bytes are closed, then `Timeline.ClearTransition()`.
- **Cancel.** A seek, a skip, a queue edit that changes the next row, a device rebuild, or Mix switched off, before
  the announce: `s_mixGate.TryCancel()` (true = B never sounded and A's envelope reads its start value again,
  `GainEnvelope.GainAt`, `CrossfadeMixer.cs:123-126`), `SetVoiceAutomation(A, null)`, remove the voice, dispose the
  slot, where `AbandonPendingJoin` runs today. `TryCancel() == false` means the render thread already committed: the
  pump announces instead. After the announce a cancel is an ordinary seek or skip on B; B's automation is cleared.
- **"Hear it":** `Playback.Mix.Audition(playlist, fromItem, in MixTiming draft)` plays the outgoing row in the
  playlist's context from 4 s before the overlap to 4 s after it, with `draft` as a one-shot override of the pair's
  timing (cleared when used, on any other play intent, when the card closes), then restores playback (§3.6a item 5).
  It is ordinary playback on this device: Wavee does not create the official client's hidden `automix/preview`
  queue rows in 0.4.
- **Diagnostics, always on:** one `Log.Info("audio", "[mix] …")` line per join (saved or auto, preset id, out start,
  durations, in start, start frame, lead, or the why-not); `Metrics` (`Playback.Audio.cs:5823`) gains `MixSaved`,
  `MixAuto`, `MixRefused`, `MixCancelled` beside `Crossfades`.

Threads: the UI thread owns entities, timing and the seam card's draft; the pump thread builds the envelopes and
automations (allocations per transition, as `CommitCrossfade`'s are today) and schedules; the render thread reads
tables, start frames and coefficient steps. Steady blocks and steady frames allocate nothing. With no mix planned,
every existing path is unchanged.

### 3.6a What the UX direction adds to the playback model

Three small additions and one changed moment. The wire, the engine and the planner's timing are as above.

**1. Shuffle (decision D5 (b)).** The planner's context check becomes "both rows belong to the mixed playlist and
play one after the other" instead of "the next row is the playlist's next item". A saved recipe is used only when
its own from and to item ids are that pair; any other pair gets Auto. The beat and cue fetch already follows the
*play* order (the playing row and the next two rows of the queue).

**2. Local overrides (decision D11 (b)), the home of "No mix".**

```csharp
// Entities/Mix.Overrides.cs (new)
public enum MixOverrideKind : byte { NoMix, Auto, Recipe }

/// <summary>What this PC holds for one pair that Spotify cannot hold (or cannot yet). Keyed by playlist uri and the
/// two item ids; persisted in the app's local store; UI thread.</summary>
public static class MixOverrides
{
    public static bool TryGet(Playlist p, ulong fromItemId, ulong toItemId, out MixOverrideKind kind, out MixRecipe recipe);
    public static void Set(Playlist p, ulong fromItemId, ulong toItemId, MixOverrideKind kind, in MixRecipe recipe);
    public static void Clear(Playlist p, ulong fromItemId, ulong toItemId);
    public static IReadSignal<int> Version { get; }      // the meta line, the seam marks and Mix.Current read it
}
```

`MixPlanner.Plan` consults it first: `NoMix` → `Source = None, WhyNot = PairOptOut` (the base hand-off, the user's
Crossfade or gapless: an album-like pair keeps its own join); `Auto` → Spotify's recipe for the pair is ignored;
`Recipe` → used as a saved recipe. An override whose items left the playlist is dropped when the list is read.
*Unverified:* which local store fits (the entity tables persist through their own schema, `Entities/Track.cs:768`;
the settings registry is the wrong place for unbounded rows); phase 3 picks one existing store, it does not add one.

**3. Styles are names over timing, not new data.**

```csharp
// Playback/Playback.Mix.cs
public enum MixStyle : byte { Smooth, Lift, Quick, NoMix }

public static class MixStyles
{
    public const uint QuickTargetMs = 2_500, QuickMaxMs = 2_600;

    /// <summary>The style a timing reads as: preset 3 → Lift; preset 1 → Quick when the overlap is at most QuickMaxMs,
    /// else Smooth; a pair opted out → NoMix; a preset id Wavee does not know → null.</summary>
    public static MixStyle? Of(in MixTiming t);
    /// <summary>The timing of <paramref name="style"/> for a pair: Auto's out region and in-point, the style's preset id
    /// and length (Smooth and Lift: Auto's beats; Quick: the whole beats of A nearest QuickTargetMs, at least 2).</summary>
    public static MixTiming With(in MixTiming auto, MixStyle style, float beatMsA, float beatMsB);
    /// <summary>The same timing with an overlap of <paramref name="beats"/> beats of A: the mix-out END stays, the out
    /// start moves; B's duration follows in B's beats.</summary>
    public static MixTiming WithBeats(in MixTiming t, int beats, float beatMsA, float beatMsB);
}
```

Auto's style rule (§3.5 step 7) is: **Smooth; Quick when `Mix.Tempo` is `Apart`**. Both are preset id 1, so the
"previous recipe" a Save sends stays expressible.

**4. The moment the player flips (decision D14 (b)).** §3.6 is written for revision 3's rule: the app's identity
flips to the incoming track at the overlap's first frame. The direction's player bar ("the seek bar shows the
overlap as its end segment", "Mixing into …") needs the outgoing track on screen until it is over. With (b):

- **Pump.** `ScheduleMix` is unchanged. The *announce* (the identity swap and `PostHandedOff`) moves from
  `SampleClock >= startFrame` to `SampleClock >= startFrame + outFrames`, the tick that today only closes the
  outgoing bytes; the anchor it sets is `s_anchorPlayheadMs = InStartMs + OutDurationMs`. Between the two frames the
  outgoing voice is still the active identity, so `ActivePositionMs` (`Playback.Audio.cs:3964`) keeps counting it
  with no change. A new `AudioSignal.MixStarted` tells the reducer the overlap began; the UI's "Mixing into …" and
  the queue line read that state.
- **Duration.** The playing track's effective duration is its audible end, `OutStartMs + OutDurationMs`
  (the wire's `automix.fade_out_cuepoint.position`), not `OutStartMs`. In the one saved transition of the capture
  that is 166609 of 166610 ms: the whole file. The overlap segment of the seek bar is the last
  `OutDurationMs` of that.
- **A seek during the overlap** is a seek in the song on screen: the join is cancelled, the incoming voice gets a
  short fade-out envelope and is removed, the outgoing voice's envelope and automation ramp back to unity and
  bypass, and playback continues at the target. The prepared slot is gone, so the pair is prepared again (or gets
  the base hand-off when the end is too close). **Skip next** during the overlap runs the announce at once.
  Pause holds both voices.
- **Connect (§3.7).** `PlayerState.duration` is the audible end, and the incoming track becomes the current track
  when the overlap ends, at position `InStartMs + OutDurationMs`. The fade metadata is unchanged. This differs from
  the official client, which publishes `duration = fade_out_start_time` and names the incoming track from the
  overlap's start: a phone following Wavee shows the outgoing track for `overlap` seconds longer. No consumer of
  that difference is known (O9).
- **Timeline (§3.9).** `SetTransition(incoming, startFrame, endFrame, Master.Outgoing)` at the overlap's first frame;
  `ClearTransition()` and the identity of the incoming track (start offset `InStartMs + OutDurationMs`) at its end.
- **Plain Crossfade is not touched**: it still flips at the fade's start (`CommitCrossfade`).

With option (a) instead, none of the above applies: the bar ends at the mix-out, the label during the overlap reads
"Mixing in" on the incoming track, and there is no end segment to play through.

**5. "Hear it".** `Playback.Mix.Audition` (§3.6) uses the direction's window: it starts 4 s before the overlap,
stops 4 s after it, then puts playback back where it was (track, context, position, paused or playing). The reducer
already has a value for "where the deck was" (`RestorePoint`, `Playback.Transitions.cs:108`, used for the launch
restore); *unverified* whether it can be applied mid-session, so phase 7 reads that path first.

### 3.7 Connect: what a mixed context says, and what Wavee accepts

When Wavee is the playing device and the context is a mixed playlist (report §3.3):

| Field | Wavee publishes | Where |
|---|---|---|
| Context URL | `context://spotify:playlist:<id>?spotify-apply-lenses=mix` | `Spotify/Spotify.Decode.PutState.cs:346`; also the outbound play envelopes (`Spotify/Spotify.Connect.Play.cs:125, 312`), so a phone started from Wavee on a mixed playlist plays it mixed |
| Context metadata (f21) | `mix`, `mix-type`, `automix.mode`, `automix.queue`, `automix.autoplay`, `automix.ignore_setting`, `has-custom-transitions`, `can-view-transition`, as read from the list | beside `player.arch` (`PutState.cs:430`) |
| `PlayerState.duration` (f11) | the playing track's effective end: its audible end with D14 (b) (§3.6a item 4), the mix-out point with (a) | already `snap.DurationMs` (`PutState.cs:367`), which the reducer sets |
| Track metadata of the playing row | `audio.fade_out_start_time`, `audio.fade_out_duration`, the four `audio.fade_out_*curves` lists that the preset uses, `automix.fade_out_cuepoint.position` (= start + duration), `automix.mode` | the track-metadata writer of `PutState` |
| Track metadata of the next row | `audio.fade_in_start_time`, `audio.fade_in_duration`, `audio.fade_overlap`, its `audio.fade_in_*curves` lists | same |

The curve JSON comes from `MixCurves.WriteJson`; the strings are built once when the timing changes and reused by
every state publish (no formatting per publish). Wavee publishes this for the playing pair only; the official client
publishes it for every queued row because it fetches the whole list's analysis up front.

**Not published in 0.4:** the signals `set-transition`, `mix-state`, `stop-automix-preview` in f33. Advertising a
signal is a promise to act on it (§1.6); Wavee adds one only together with its handler.

**Accepted:** a play or transfer whose context URL carries `?spotify-apply-lenses=mix` marks that playlist as mixed
for the session and triggers the lens read. *Unverified:* how Wavee's inbound context-URL parsing treats a query
string today; phase 4 starts by reading it. Incoming `audio.fade_*` metadata on transferred rows is ignored: Wavee
plans from the recipe and its own Auto. Whether a receiver is *meant* to take the sender's curves is open (O9).

**Events.** The official client sends an `AutoTransition` event at each crossing and labels the session
`"crossfade"` (report §3.3). Wavee's play reporting runs through the hand-off it already has (`PostHandedOff`, the
reducer's `PlayReport`); the extra event is in "After 0.4".

### 3.8 Engine work (fluent-gpu)

All in `src/FluentGpu.Engine/Media/Playback/`. Verified in the engine repo with a clean Debug build of
`src/FluentGpu.slnx`; the app's `engine.lock` moves with it. Revision 2's per-voice rate (and its glide and
content-position work) is gone from 0.4 with beatmatching.

| # | Change | Why | Size |
|---|---|---|---|
| E1 | **Curve envelopes.** `GainEnvelope.FromCurve(FadeKind kind, long startFrame, int frames, float[] table)`: gain = the table (1,025 points, sampled from Bézier segments by the app, off the render thread) with linear interpolation; before the start it holds `table[0]`, after the end `table[^1]`; `kind` keeps today's gate semantics (`Audio/CrossfadeMixer.cs:123-126`). The existing `Fade(kind, start, frames, CrossCurve)` is re-implemented over one shared table per `CrossCurve`, which removes today's per-sample lookup table (`:110-120`: a 12 s fade allocates 576,000 floats per envelope). The two envelopes of a join may have different lengths. | Asymmetric, multi-segment volume curves (C1, C3, C5, C8, C10, C12). | S-M |
| E2 | **Per-voice automation.** `VoiceAutomationStage : IDspStage` (`Audio/DspStages.cs`), appended to every voice chain by `BuildVoiceChain` (`Audio/PcmAudioPlayer.cs:1808`) and `Bypassed` until armed (a bypass is a value, `Audio/AudioGraph.cs:50`). Two filters per channel from `Audio/Biquad.cs`: a **low shelf** (gain automated) and one **sweep filter** that is a low-pass, a high-pass or bypassed (frequency and Q automated). `VoiceAutomation.Build(int sampleRate, long startFrame, int frames, float lowShelfHz, ReadOnlySpan<float> lowShelfDb, ReadOnlySpan<SweepPoint> sweep)` precomputes, **off the render thread**, up to 128 coefficient steps per lane with `BiquadCoeffs.Design`, placed where the value changes fastest (a bass swap lasting 1/32 of the window gets most of them); the stage steps through them by block start frame and cross-ramps each step as `EqStage` does (`DspStages.cs:105-110`). `SweepPoint(byte Mode, float FreqHz, float Q)`, mode 0 = bypass, 1 = low-pass, 2 = high-pass. `PcmAudioSession.SetVoiceAutomation(long id, VoiceAutomation? automation)` is a mixer command beside `CmdSetEnvelope` (`PcmAudioPlayer.cs:477`); `null` ramps back to bypass. The engine takes Hz, dB and Q; the curve-to-unit mapping stays in the app (§3.4). | Bass swap (C2, C4, C11, C13, C14, C16), filter sweeps (C6, C9, C15), resonance (C7). | M-L |
| E3 | **A prepared voice that starts past the head.** `PrepareContext` (`QueuePreparation.cs:18`) gains `long StartSourceFrame`; the audio backend seeks the decoder there before the prefill and reports it on `IPreparedItem`. **Unverified:** I did not read the backend's `PrepareAsync` body, so the cost of a decoder seek before prefill (Ogg page seek, FLAC seek table, the encrypted stream's fetch at an offset) is not established; phase 2 starts by reading it. | `fade_in_start_time` (2874, 4144, 7794 ms in the capture). | M |

### 3.9 The timeline tap of the #154 plan (G2a)

#154 owns the signatures (its §3.2). This plan uses them as written. Which `Master` it passes follows decision D14:

| | D14 (b), recommended: the player flips when the outgoing track is over | D14 (a): revision 3 |
|---|---|---|
| At the overlap's first frame | `Playback.Timeline.SetTransition(incoming: s_prepId, startMixFrame: startFrame, endMixFrame: startFrame + MsToFrames(OutDurationMs), Master.Outgoing)` | the same call with `Master.Incoming`, together with the identity of the incoming track (start offset `InStartMs`) |
| At the overlap's end | `Playback.Timeline.ClearTransition()` and the identity of the incoming track, start offset `InStartMs + OutDurationMs` | `ClearTransition()` |
| On a cancel inside the window | `ClearTransition()`; the outgoing identity never changed | `ClearTransition()` |

- **Duration.** The identity's `DurationMs` in a mixed playlist is the track's effective end (§3.6a item 4), which
  can be shorter than the file.
- **Rate.** Nothing to do in 0.4: mixed joins play at rate 1.0, so `AudioTimelineAnchor.Rate` is untouched by this
  plan. When beatmatching is built (after 0.4), the per-voice rate comes back with the rule from revision 2: the
  anchor reads the active voice's effective rate, and a glide moves in at most 16 steps so the tap's epoch does not
  bump every tick.

For #154's D6: its recommendation (the timecode names the incoming track from the fade's start) is what the wire's
shortened duration says, and stays true for a plain crossfade. With this plan's D14 (b) a *mixed* join names the
outgoing track until it is over, and the tap is told so through `Master.Outgoing`. Both are expressible in #154's
API; which the timecode should follow is #154's decision.

### 3.10 UI, part 1: seeing and switching Mix (phase 5)

The UX source of truth is `docs/plans/wavee/48-mix-ux-direction.md`. Revision 5: the direction's revision 2 offers Spotify's full feature set (chips as a view option, BPM · Key,
Customize, the player-bar label) in a quieter layout; where this section differs, the direction wins. What ships:

| Surface | Mix off | Mix on |
|---|---|---|
| Playlist header | a **Mix** toggle beside Play / Shuffle | the toggle reads **Mix on** with a small waveform glyph; the meta line gains "Mixed by Ana · 9 of 10 transitions auto" |
| List toolbar | as today | a **Tracks / Transitions** switch appears (Tracks is the default) |
| Track rows | as today | **exactly as today**; the gap under a row shows a thin seam with a "⋯ Transition" handle on hover or focus; an edited pair keeps a 6 px accent dot on its seam; a "No mix" pair keeps a short break in its seam |
| Player bar | as today | while a transition plays, the seek bar's end segment is slightly brighter and the time label reads "Mixing into \<next title\>"; nothing else changes; no BPM or key |
| Queue / Up next | as today | a small "Mixing" line between the playing and the next row; the Crossfade row says "Mix is on for this playlist" |

Contract additions (`Theming/Contracts/**`, declared `contract: additive` in the change file):

```csharp
// TrackChromeStyle.cs, beside TuneCommand (:105-107)
/// <summary>The header's Mix toggle: "Mix", or "Mix on" with the waveform glyph. A structure without one draws a
/// ToggleButton (label, checkedLabel, glyph, checkedGlyph).</summary>
Element MixCommand(string label, string onLabel, Signal<bool> on, Action<bool> onChange);
/// <summary>The list toolbar's Tracks / Transitions switch, mounted only while Mix is on. Default: SelectorBar.</summary>
Element ViewSwitch(IReadOnlyList<string> labels, Signal<int> selected, Action<int> onChange);

// DetailPlaylistProps.cs: PlaylistOwnerProps gains
string MixLine     // "Mixed by Ana · 9 of 10 transitions auto" (+ " · Shuffle: auto transitions"); "" while Mix is off

// TrackListStyle.cs: the row's bound data gains the seam UNDER this row
public readonly record struct RowSeam(byte Mark, bool Shown);   // Mark: 0 plain · 1 edited (dot) · 2 no mix (break); Shown: Mix is on and a next row exists
// Track.BoundRow exposes Prop<RowSeam> Seam and Action OpenSeam; each row grid template draws the seam strip.

// PlayerTransportProps.cs
// SeekRailProps gains:   Func<float>? OverlapFrom    the rail fraction where the playing track's overlap begins; null = none
// TimeLabelProps gains:  Func<string?>? MixingText   "Mixing into Genesis" while the overlap plays; null = the digits

// QueueProps.cs: QueuePanelProps / QueueStagePaneProps gain
public bool MixingNext { get; init; }              // the "Mixing" line between the playing and the next row
public string CrossfadeNote { get; init; } = "";   // "Mix is on for this playlist"

// ViewKeys.DetailAlbumPlaylist.cs
public static readonly ViewKey<PlaylistTransitionsProps> PlaylistTransitions = new("Playlist.Transitions");

public sealed record PlaylistTransitionsProps(int Count, Func<int, SeamRow> RowAt, IReadSignal<int> Version,
    string? OpenKey, Action<int> Open, Action<int> Hear, Func<int, Element> CardAt);
/// <summary>One pair as the Transitions view lists it. Pending = its timing is not known yet (the row shimmers).</summary>
public readonly record struct SeamRow(string Key, string From, string To, string Style, string Length, bool Auto,
    bool NoMix, bool Pending, float TailFraction, float HeadFraction);
```

Revision 3's `RowTransition` chip, its "Auto / Rise ✎" labels and `PlayerBarProps.TempoKey` are gone.

**Header and meta line** (`Entities/Playlist.Page.cs`, beside `Tune` at `:484-486`). The toggle shows when the
playlist is mixed, or is the user's own and mixable (kind 235, **[inferred]**). Pressing it: not mixed and the
user's own → `Spotify.Mixes.TurnOn`; mixed → the local "listen without mix" (D3). The meta line is computed from the
list read alone, no analysis needed: transitions = adjacent mixable pairs, edited = pairs with a recipe or a local
override, so "9 of 10 transitions auto" costs nothing. With shuffle on it ends "· Shuffle: auto transitions" (D5).
Off: the toggle reads "Mix" and nothing else on the page differs.

**The seam under a row** (Tracks view). A strip 8 DIP tall inside row *A*'s own bounds, along its bottom edge, drawn
by the row grid template as one more layer:

- at rest, Mix on, plain pair: nothing is painted;
- pointer over the strip (`OnHoverMove` / `OnPointerExit`) or the row focused: a 1 DIP line across the row and the
  handle "⋯ Transition" at the title lane's start, faded in with `WhileHover` (opacity only, no layout);
- edited pair: a 6 px dot in the accent, always painted, centred in the strip (so it never crosses the row's edge
  and no clip question arises);
- "No mix" pair, and a pair with a local or not-mixable track: the line always painted, with a 24 DIP break at its
  middle.

Clicking the handle (or the dot or the break) calls `OpenSeam`: the seam card opens under that row (§3.11). Because
the strip sits inside the row and the row's height does not change, the virtualized list's layout is untouched and
nothing moves when the lens read lands. Hover does not exist on touch and a strip is not a 40 DIP target, so at
Compact the strip is not interactive; there, and for the keyboard in every tier, the row's own menu gains
**Transition to next song** (the same `OpenSeam`), and the Transitions view is the full keyboard path.

**The Transitions view** (`ViewKeys.PlaylistTransitions`). The switch replaces the track list's region with a seam
list: one compact row per pair, virtualized (`ItemsView.CreateBound`), in the list's place and width. The header, the
list bar and the scroll position of the page do not move; the region cross-dissolves. A row:

```
 aftermath  ⟶  i think i wanna text u          ▂▂▂▂▃▃        Smooth · 6 s        Auto
                                                    ▃▃▂▂▂▂
```

the two titles, a tiny overlap bar (the outgoing tail and the incoming head as two short bars that overlap by the
transition's share), the style and length, and "Auto" / "Edited" / "This PC" at the end. Timing needs the two tracks'
beat and cue data, which is fetched for realized rows only; until it lands the style and length cells shimmer
(`Skel.Region`), the titles do not. Keys: ↑ / ↓ move between seams (the list's own navigation), **Enter** opens the
seam card inline under the row (`ListOptions.Disclosure`), **Space** plays "Hear it" for the focused seam.

**Player bar.** With decision D14 (b) the player shows the outgoing track until it is over, so:

- from the moment the pair's timing is known, the playing track's seek rail paints its last
  `OutDurationMs / duration` slightly brighter (`SeekRailProps.OverlapFrom`): a decoration of the rail like the
  chapter ticks, taking no input, visible at rest;
- while the playhead is inside that segment, the time label's slot reads "Mixing into \<next title\>"
  (`TimeLabelProps.MixingText`, a bound text in the slot the digits use, so the bar's layout does not change: the
  label's box is as wide as the widest of the two, reserved while a mix is planned);
- when the outgoing track is over, the bar shows the next track, already `OutDurationMs` in.

No BPM, key or badge is added to the bar (D9).

**Queue / Up next.** While the playing context is mixed and the next row will be mixed into, a one-line "Mixing"
separator sits between the now-playing row and the next row (`MixingNext`); the Crossfade control keeps its value and
shows the note. Classic and Zune reserve both lines whenever the context is a mixed playlist and reveal them with
`SizeMode.FlowReveal` on the context change; WinUI's Crossfade row already has a hint line.

Wireframes. Each theme keeps its own header structure; only the new parts are marked.

```
CLASSIC (rail + list)
┌──────────────┐  ┌──────────────────────────────────────────────────────────────────────────────────────────┐
│   cover      │  │ [▶ Play] [⇄ Shuffle] [∿ Mix on]      ( Tracks | Transitions )   [Filter] [Sort ▾] [🔍] […] │
│              │  ├───┬──────────────────────────────────────┬─────────────────────┬───────────┬─────────────┤
│ Late Night   │  │ 1 │ Midnight City      M83               │ Hurry Up…           │ 3 Nov     │ 4:03        │
│ Drive        │  │ 2 │ Genesis            Grimes            │ Visions             │ 3 Nov     │ 4:15        │
│ ◐ by Ana     │  │   │───── ⋯ Transition ───────────────────────────────────────────────────────────────    │ ← hover / focus only
│ Mixed by Ana │  │ 3 │ Oblivion           Grimes            │ Visions             │ 3 Nov     │ 4:11        │
│ · 9 of 10    │  │   │                    ●                                                                 │ ← edited pair: the dot, always
│ transitions  │  │ 4 │ Kill V. Maim       Grimes            │ Art Angels          │ 4 Nov     │ 4:06        │
│ auto         │  │   │─────────────────────      ─────────────────────────────────────────────────────────  │ ← "No mix" pair: the break, always
│ 42 songs     │  │ 5 │ Savin' Me (local)  Nickelback        │                     │ 4 Nov     │ 4:12        │
└──────────────┘  └───┴──────────────────────────────────────┴─────────────────────┴───────────┴─────────────┘
   Mix off: the button reads [∿ Mix], no switch, no meta words, no seams.
```

```
WINUI (hero + command bar + card list)
 Late Night Drive
 Playlist · Ana · Mixed by Ana · 9 of 10 transitions auto · 42 songs
 [▶ Play] [⇄] [♡] [∿ Mix on]                              ┌ Tracks ┬ Transitions ┐        ← SelectorBar, Mix on only
 ┌────────────────────────────────────────────────────────────────────────────────┐
 │ 1  ▣ Midnight City        M83            Hurry Up…                        4:03 │
 │ 2  ▣ Genesis              Grimes         Visions                          4:15 │
 │ ──── ⋯ Transition ──────────────────────────────────────────────────────────── │  ← hover: line and handle in the system accent
 │ 3  ▣ Oblivion             Grimes         Visions                          4:11 │
 │                 ●                                                              │  ← edited: accent dot
 │ 4  ▣ Kill V. Maim         Grimes         Art Angels                       4:06 │
 └────────────────────────────────────────────────────────────────────────────────┘
```

```
ZUNE (large lowercase type; type sizes unchanged)
 late night drive
 by ana · mixed by ana · 9 of 10 transitions auto
 play   shuffle   mix on                                        tracks   TRANSITIONS      ← words; the lit one in the accent
 1  MIDNIGHT CITY                                   m83
 2  GENESIS                                         grimes
    ──── ⋯ transition ─────────────────────────────────────                             ← hover / focus
 3  OBLIVION                                        grimes
               ●                                                                         ← edited
```

The Transitions view:

```
CLASSIC / WINUI
 ( Tracks | Transitions )
 ┌────────────────────────────────────────────────────────────────────────────────────────────┐
 │ Midnight City   ⟶  Genesis                    ▂▂▂▃▃           Smooth · 6 s          Auto   │
 │                                                  ▃▃▂▂▂                                     │
 │ Genesis         ⟶  Oblivion                   ▂▂▂▃            Lift · 3 s            Edited │  ← ● before the style
 │ Oblivion        ⟶  Kill V. Maim               ▂▂▂▃▃           Quick · 2.5 s         Auto   │
 │ Kill V. Maim    ⟶  Savin' Me                  ▂▂▂   ▂▂▂       No mix                local  │  ← the two bars do not overlap
 └────────────────────────────────────────────────────────────────────────────────────────────┘
   WinUI: each row a card-list row; Classic: its list density.

ZUNE
 tracks   TRANSITIONS
 MIDNIGHT CITY  →  GENESIS                              smooth · 6 s     auto
 GENESIS  →  OBLIVION                                 ● lift · 3 s       edited
 OBLIVION  →  KILL V. MAIM                              quick · 2.5 s    auto
```

Player bar while a transition plays (the same slots as today; "┅" is the brighter end segment):

```
CLASSIC   ▣  Midnight City                          ⏮  ▶  ⏭      Mixing into Genesis ━━━━━━━━━━━●┅┅ 4:03   🔊 ▭▭▭  ☰
             M83
WINUI                     ▣ Midnight City — M83        ⏮ ▶ ⏭    Mixing into Genesis ━━━━━━━━━●┅┅ 4:03    🔊  ☰
ZUNE      MIDNIGHT CITY
          m83                                        prev  play  next
          mixing into genesis  ━━━━━━━━━━━━━━━━━━━━━━━━━●┅┅
 Before the overlap the label shows the time as today and the end segment is already a little brighter.
```

Queue:

```
WINUI    ▣ Midnight City   M83              now playing        CLASSIC  ▣ Midnight City  M83      ▶
         ─ Mixing ─                                                     ─ mixing ─
         ▣ Genesis         Grimes                                       ▣ Genesis        Grimes
         …                                                              [▶ Autoplay] [♪ Crossfade] 6 s
         Crossfade                       6 s   [ ●━]                    Mix is on for this playlist
         Mix is on for this playlist
ZUNE     MIDNIGHT CITY
         mixing
         GENESIS
         shuffle  repeat  autoplay  crossfade 6 s
         mix is on for this playlist
```

### 3.11 UI, part 2: the seam card (phase 7)

One pair, edited in place. The card is `ViewKeys.PlaylistSeam` (new), one view per theme. In the Tracks view it is
the *seam drawer* of the from row: the track table's own expanded-row mechanism with a second body kind
(`Track.Table.ToggleExpanded`'s `_expanded` key gains a seam form; the reveal is `SizeMode.FlowReveal`, the closing
tail is held as the track drawer's is). In the Transitions view it is the disclosed band of its row. At Compact it
is a sheet (`Controls.Sheet`). One card open per page; closing it leaves the list as it was.

```csharp
public enum MixStyle : byte { Smooth, Lift, Quick, NoMix }

public sealed record PlaylistSeamProps(
    SeamSide From, SeamSide To,                  // title, artist, small cover
    string Compatibility,                        // "117 → 77 BPM · 11B → 9B — tempos far apart; a quick cut sounds best."
    Signal<int> StyleIndex,                      // the segmented control's selection; −1 = a Spotify preset Wavee does not know
    bool IsAuto, string AutoLine,                // "Auto chose Smooth"; "" once the pair is edited
    Func<string> Sentence,                       // one sentence for the chosen style
    SeamStrip Strip,
    Func<string> Readout,                        // "6.2 s · 8 beats" (bound: it follows the drag)
    string? SavedNote,                           // "Saved on this PC only", or null
    bool Saving, bool Failed,
    Action<MixStyle> PickStyle, Action Hear, Action Reset, Action Done, Action? Previous, Action? Next);

/// <summary>The overlap strip: the outgoing tail and the incoming head as two waveforms (kind 237 through
/// WaveformBands.ToColumns → Waveform.Create) and one region. Region is in beats of the outgoing track; the view binds
/// its Width/Transform to it and writes it from OnPointerDown / OnDrag. Snapping happens in SetBeats.</summary>
public sealed record SeamStrip(WaveformModel Tail, WaveformModel Head, FloatSignal RegionBeats, float BeatWidth,
    int MinBeats, int MaxBeats, Action<int> SetBeats, bool Enabled);
```

**What the card holds, top to bottom:**

1. **Header:** the two tracks with small covers, and one compatibility line in plain words, computed from
   `Mix.Tempo` and `Mix.Key` (§3.5). It recommends, it never warns, and there is no icon:

   | Tempo / key | Line ends with |
   |---|---|
   | matched (or half / double time), keys same, adjacent or relative | "these sit well together." |
   | tempos far apart | "tempos far apart; a quick cut sounds best." |
   | keys clash, tempos matched | "keys clash; a shorter overlap sounds best." |
   | either unknown | only the numbers that are known |

2. **Style:** one segmented control with four choices (`Segmented.Create`; Zune: large text tabs). **Auto is a
   state, not a fifth choice:** an un-edited pair shows "Auto chose Smooth" beside the control with that choice lit;
   picking any choice (the lit one too) makes the pair edited; **Reset to auto** returns to the state.

   | Style | What plays | On the wire | One sentence under the control |
   |---|---|---|---|
   | **Smooth** | long crossfade, bass handed over halfway | Spotify **Fade**, preset id 1 (curves C1 / C3, C2 / C4) | "Volume crossfades over 6 s; the bass hands over at the middle." |
   | **Lift** | the next track rises in under a filter, bass swaps at the end | Spotify **Rise**, preset id 3 (C5 / C8, C14 / C16, C15 / C9, C7) | "The next song comes in under a filter at full volume; the bass swaps at the end." |
   | **Quick** | a short fade on the beat | **Wavee-side name:** preset id 1 with an overlap of the whole beats nearest 2.5 s (D17). Not stored as such: a Fade recipe whose overlap is at most 2.6 s reads back as Quick. | "A short fade on the beat, about 2.5 s." |
   | **No mix** | this pair plays as a normal track change (the user's Crossfade or gapless) | **Nothing on the wire is known for it** (D16): a local override on this PC | "This pair plays as a normal track change." |

   A pair Spotify holds with a preset id Wavee does not know (Blend, Wave, Melt) shows "Edited in Spotify", no
   choice lit, and plays with Fade's curves at its stored timing; picking a style replaces it. Those three are not
   offered until their ids are captured (O2).

3. **Overlap:** one strip. The outgoing tail (left) and the incoming head (right, starting at its in-point) are two
   waveforms on one time axis; the overlap is one highlighted region across both. Dragging the region's leading edge
   changes its length in whole beats of the outgoing track (`OnDrag` writes `RegionBeats`, a bound width; on release
   `SetBeats` snaps and re-plans). The readout follows the drag: "6.2 s · 8 beats". 2 to 32 beats, at most 16 s
   (D19); a pair without a beat grid gets three fixed lengths instead of a drag. Disabled for No mix. There is no
   curve editing and no second handle: the mix-out end and the in-point stay where the planner or the recipe put
   them.

4. **Actions:** **Hear it** plays from 4 s before the overlap to 4 s after it on this PC, then puts playback back
   where it was (§3.6, the audition; disabled with its reason while another device owns playback).
   **Reset to auto.** **Done** saves and closes.

**Where Done saves** (decision D11 (b)):

| The change | The user owns the playlist, the pair is editable (item attribute 14) and its in-point is 0 | Otherwise |
|---|---|---|
| Smooth, Lift, Quick, a changed overlap | Spotify: `set-transition` (§3.2). New recipe = the timing with preset id 1 or 3, out start (1.3), overlap (1.5), item ids, uris, kind 217 ids, BPMs; previous recipe = Auto's. | the local override (`Recipe`); the card says "Saved on this PC only" |
| No mix | the local override (`NoMix`); "Saved on this PC only" | the same |
| Reset to auto | no Spotify recipe: the local override is removed. A Spotify recipe exists: the local override `Auto`, "Saved on this PC only" (the reset request is not captured, O6). | the local override is removed |

"The in-point is 0" is revision 3's O5 rule: until a save on a pair with a non-zero in-point is captured, such a
pair is saved locally. Beatmatching is not shown (D6).

Wireframes:

```
WINUI: an inline expander card under row 2 (Tracks view), or under its seam row (Transitions view)
 2  ▣ Genesis              Grimes         Visions                          4:15
 ┌────────────────────────────────────────────────────────────────────────────────────────┐
 │ ▣ Genesis  ⟶  ▣ Oblivion                                              ‹ 2 of 10 ›   ✕ │
 │ 104 → 156 BPM · 9A → 2B — tempos far apart; a quick cut sounds best.                   │
 │                                                                                        │
 │ ┌ Smooth ┬ Lift ┬ Quick ┬ No mix ┐        Auto chose Smooth                            │  ← Segmented; the Auto line leaves once edited
 │ Volume crossfades over 6 s; the bass hands over at the middle.                         │
 │                                                                                        │
 │  ▁▂▅▇▇▆▇▇▅▆▇▇▆▅▇▆▅▃▂▁▓▓▓▓▓▓▓▓▓▓▓▓│                 outgoing tail                       │
 │                      ▓▓▓▓▓▓▓▓▓▓▓▓▁▂▃▅▆▇▇▆▇▇▅▆▇▇▆▇   incoming head                       │
 │                      ◂┃ drag                         6.2 s · 8 beats                   │  ← the region's edge; snaps to beats
 │                                                                                        │
 │ [▶ Hear it]   Reset to auto                                                   [ Done ] │
 └────────────────────────────────────────────────────────────────────────────────────────┘
 3  ▣ Oblivion             Grimes         Visions                          4:11
```

```
CLASSIC: the card follows the list's density; the style control is a compact segmented control
 2 │ Genesis            Grimes            │ Visions             │ 3 Nov     │ 4:15
 ╭──────────────────────────────────────────────────────────────────────────────────────╮
 │ ▣ Genesis ⟶ ▣ Oblivion   104 → 156 BPM · 9A → 2B — tempos far apart; a quick cut…  ✕ │
 │ (Smooth│Lift│Quick│No mix)  Auto chose Smooth     Volume crossfades over 6 s; the …   │
 │ ▁▂▅▇▇▆▇▇▅▆▇▇▆▅▇▆▅▃▂▁▓▓▓▓▓▓▓▓▓▓▓│                                                     │
 │                     ▓▓▓▓▓▓▓▓▓▓▓▁▂▃▅▆▇▇▆▇▇▅▆▇▇▆▇            6.2 s · 8 beats            │
 │ [▶ Hear it]  Reset to auto                                  ‹ 2 of 10 ›     [ Done ] │
 ╰──────────────────────────────────────────────────────────────────────────────────────╯
 3 │ Oblivion           Grimes            │ Visions             │ 3 Nov     │ 4:11
```

```
ZUNE: a full-width band; styles as large text tabs; type sizes unchanged
 2  GENESIS                                         grimes
 ────────────────────────────────────────────────────────────────────────────────────────
    genesis  →  oblivion                                              ‹ 2 of 10 ›   close
    104 → 156 bpm · 9a → 2b — tempos far apart; a quick cut sounds best.

    SMOOTH     lift     quick     no mix                              auto chose smooth
    volume crossfades over 6 s; the bass hands over at the middle.

    ▁▂▅▇▇▆▇▇▅▆▇▇▆▅▇▆▅▃▂▁▓▓▓▓▓▓▓▓▓▓▓│
                        ▓▓▓▓▓▓▓▓▓▓▓▁▂▃▅▆▇▇▆▇▇▅▆▇▇▆▇                    6.2 s · 8 beats

    hear it     reset to auto                                                       done
 ────────────────────────────────────────────────────────────────────────────────────────
 3  OBLIVION                                        grimes
```

```
COMPACT (all themes): the same content in a sheet (Controls.Sheet). The style control is one full-width row of four
targets at least 40 DIP tall; the strip is full width; Done is a full-width button at the bottom.
```

No layout jumps: the card's height is the same for every style (the sentence is one reserved line, the strip stays
mounted and greys out for No mix), so picking a style changes text and fills only. Opening and closing is the
drawer's reveal. The Auto line and the saved note share one reserved line.

### 3.12 Files

| File | Change |
|---|---|
| `Spotify/Spotify.Decode.PutState.cs:439` | **§1.6 fix, separate change:** stop advertising `automix-preview` |
| `fluent-gpu/…/Audio/CrossfadeMixer.cs`, `DspStages.cs`, `PcmAudioPlayer.cs`, `…/QueuePreparation.cs`, the audio backend's prepare | E1-E3 |
| `Spotify/Spotify.cs` (`HeaderSet.MixLens`, `RequestKind.PlaylistSignals`), `Spotify.Api.cs:591`, `Spotify.Api.Playlist.cs` (`SignalsBody`), `Spotify.Api.Library.cs` (`PlaylistSignals`, the playlist read/diff headers) | lens header, signals v2 |
| `Spotify/Spotify.Decode.cs` (`Ext`, `ItemAttributes`, switch arms at `:1278`), `Spotify.Decode.Playlist.cs`, `Spotify/Protos/playlist4_external.proto` (item attr 14, f22) | read side |
| `Spotify/Spotify.Decode.Mix.cs`, `Spotify/Spotify.Api.Mix.cs` (new) | kinds 28, 217, 225, 170, 235; `Mixes.TurnOn / SaveTransition / SetListenWithoutMix` |
| `Spotify/Spotify.Decode.Analysis.cs`, `Spotify.Api.cs` (`AudioAnalysis*`), the `analysis` headless verb | deleted when kind 217 is confirmed for non-mixed tracks (O12) |
| `Entities/Mix.cs`, `Entities/Mix.Recipe.cs` (new), `Entities/Edges.cs`, `Fetch.Routes.cs`, `Fetch.Edges.cs`, `Waveform.cs`, the playlist and track table files | `MixTiming`, `MixCurves`, `MixRecipe`, `PlaylistTransitions`, `TrackCues`, `PlaylistFields.Mix`, `TrackTable.BeatsId` |
| `Entities/Entities.Fake*.cs` | a mixed fake playlist: auto pairs, one stored Rise, one local row |
| `Playback/Playback.Mix.cs` (new) | `Mix.Key/Tempo`, `MixPlanner`, `MixRender`, `MixStyles`, `Mix.Current`, `Audition` |
| `Entities/Mix.Overrides.cs` (new) | the local per-pair overrides (D11 (b)) |
| `Playback/Playback.cs` (reducer: `Input.MixTiming`, effective duration), `Playback.Audio.cs`, `Playback.Endgame.cs`, `Playback.Host.cs` | §3.6, §3.9 |
| `Spotify/Spotify.Decode.PutState.cs`, `Spotify.Connect.Play.cs`, the inbound context parsing | §3.7 |
| `Entities/Playlist.Page.cs`, `Playlist.UI.cs`, `Track.Table*.cs`, `Track.Rules.cs`, `Platform/Actions.Table.cs` | the Mix toggle, the meta line, the view switch and the Transitions view's host, the row seam, the seam drawer and its session, the row menu's "Transition to next song" |
| `Theming/Contracts/{TrackChromeStyle,DetailPlaylistProps,PlayerTransportProps,TrackListStyle,QueueProps,ViewKeys.DetailAlbumPlaylist}.cs` | §3.10, §3.11 |
| `Themes/Classic/**`, `Themes/WinUi/**`, `Themes/Zune/**` | the Mix toggle and view switch, the meta line, the seam strip in each row grid, the `PlaylistTransitions` view, the `PlaylistSeam` card, the seek rail's overlap segment and the time label's text, the queue's "Mixing" line and note |
| `Shell/Shell.PlayerBar.UI.cs` (`Shell.SeekBar`, `Shell.TimeText`), `Entities/Queue.UI.cs` | `OverlapFrom`, `MixingText`, `MixingNext`, `CrossfadeNote` |
| `Screens/Diagnostics*.cs` | a "Mix" section: lens etag, list attributes, recipes, the timing table of the open playlist |
| `assets/loc/*.json`, `changes/unreleased/<n>-mix.md` (`issues: [48]`, `contract: additive`), `docs/guide/mix.md` | strings, change file, guide |

---

## 4. Phases

**Before 0.4, on its own:** the §1.6 fix (stop advertising `automix-preview`), size S, a 0.3.x change with its own
issue.

All of the following are inside 0.4.

| Phase | What ships | Size | Engine | Verification |
|---|---|---|---|---|
| **1. Wire, read side** | Mix lens header; list and item attributes; attribute 14; f22 lens etag in the cache identity; kinds 225, 170, 235, 217, 28; `MixRecipe` decoder; the edges; `TrackBeats` re-sourced; fake seeds; the diagnostics "Mix" section. User-visible alone: nothing new in the pages; the stage's Pulse follows real bars. | M | no | Debug build; on the owner's two mixed playlists the diagnostics section shows `mix=true`, the lens etag, and the stored Rise recipe decoded to the same values the capture's recipe decoder prints; `--fake` stage render. |
| **2. Engine** | E1 curve envelopes, E2 automation stage, E3 prepared offset. No app behaviour change. | M-L | **yes** | Engine and app Debug builds; by ear: an existing 12 s crossfade, a seek fade and a gapless album sound as before; no per-block allocation in the engine counters. |
| **3. Playback** | `MixCurves` with the Fade and Rise tables, `MixPlanner` (saved and Auto), `MixStyles`, the local overrides store, shuffle pairs, the reducer's timing and effective duration, prepare at an offset, the scheduled join with the flip at the overlap's end (D14), cancel and seek inside the overlap, "Hear it", logs, metrics, the reference table against the nine captured plans. A mixed playlist now *plays* mixed. | **L** | no | Debug build; the listening matrix (§5.4); `[mix]` log lines; the diagnostics timing table beside the report's §3.3 table. |
| **4. Connect** | Lens context URL (published and accepted), context metadata, `audio.fade_*` metadata for the playing pair, the effective duration. | M | no | With the official desktop and a phone on the account: the phone's progress bar ends where Wavee's does; a playlist started from Wavee on the phone plays mixed; the desktop's PUT-state view (a Fiddler session by the owner) shows the same keys the capture shows. |
| **5. Seeing and switching** | The header's Mix toggle (turn on; listen-without-mix) and meta line; the Tracks / Transitions switch and the Transitions view (rows read-only, Space plays "Hear it"); the hover seam, the edited dot and the "No mix" break in the track rows; the seek bar's overlap segment and "Mixing into …"; the queue's "Mixing" line and Crossfade note; three themes. No per-row chips, no BPM column requirement, nothing else in the player bar. | **L** | no | Per theme × playlist board size × light/dark on the fake mixed playlist: `render` of both views and of Mix off; `lint min=error`; `trace` on switching Mix, on switching views, on the lens read landing (no row moves), on a join playing (the bar's label swaps in its reserved slot, nothing else moves); `crossing` at the page's tier edges both ways; the seam's hover and focus states through `hover` / `find`; screenshots beside the boards. Live: Mix switched on in Wavee shows as mixed in the official client. |
| **6. Timeline** | `SetTransition` / `ClearTransition` (§3.9), when #154 phase 1 is in. | S | no (uses #154's) | #154's tap rows during a mixed join: the transition flag, the incoming id, the master as D14 decides, the position after the join starting at the in-point plus the overlap. |
| **7. The seam card** | The inline card in both views and the Compact sheet: the compatibility line, the four styles and the Auto state, the overlap strip with its beat-snapped drag, Hear it, Reset to auto, Done; the row menu's "Transition to next song"; signals v2 write for `set-transition`; the `MixRecipe` encoder; the save rules of §3.11. | **L** | no | The inspector set on the open card in both views (`trace` on open/close: the reveal moves nothing at once; on each style pick: text and fills only; `drag` on the region: the readout follows, the card does not relayout); `lint` at Compact for the 40 DIP targets. Live: save Lift in Wavee, the official client shows Rise on that pair and plays it; save Quick, the official client plays a 2.5 s Fade; save in the official client, Wavee repaints; No mix on one pair plays the plain track change here and still mixes on the phone. Before this phase: capture round 2, item 5 (§5.2). |

**Sizing and the cut.** Against revision 3: phase 5 stays **L** (the chips, the BPM column and the player-bar badge
left; the Transitions view, the seam strip and the seek rail's overlap came in); phase 7 grows from M-L to **L**
(four styles, the overlap drag, the save rules with the local layer); phase 3 absorbs §3.6a and stays L, now the
heaviest phase. In order: 1 M, 2 M-L, 3 L, 4 M, 5 L, 6 S, 7 L.

- **The 0.4 target:** phases 1 to 7.
- **The smallest complete cut:** phases 1 to 6 **plus one piece of phase 7: "Don't mix into the next song" in the
  row's menu** (size S; it writes the local `NoMix` override that phase 3 already honours and phase 5 already
  draws as a break). That cut is: mixed playlists play as mixed with Spotify's saved transitions and Wavee's Auto;
  the header says so; the Transitions view lists every pair; a join shows in the player bar and the queue; Mix can
  be switched on; any pair can opt out; Connect tells the truth. The seam card (styles, overlap, saving to Spotify)
  then moves to "After 0.4" as a unit. This cut needs no further capture. Without the menu item the cut would ship
  the top complaint about Spotify's Mix (no per-pair opt-out), which the direction rules out.
- Phase 5 before phase 3 is never merged (a toggle that changes nothing audible); phase 3 without phase 4 is
  acceptable only inside the release branch, not in a release (remotes would show the wrong duration).

### After 0.4 (nothing of it half-built in 0.4)

Blocked on a capture (§5.2 lists which):

- **Blend, Wave, Melt** as styles, and rendering them by name (their curves are partly known already).
- **Volume / EQ / Filter as controls.**
- **Beatmatching:** the per-voice rate in the engine (revision 2's E1 and its content-position work), the toggle,
  the recipe's rate fields.
- **Turning Mix off on Spotify**, and **Reset to Auto**.
- **The Connect preview:** inbound `automix-preview` / `stop-automix-preview`, the hidden `automix/preview` queue
  rows, and sending "Hear it" to another device.

Not blocked, simply later:

- Moving the mix-out end and the in-point (0.4 edits only the overlap's length); dragging curves.
- A truly short "Quick" (one or two beats) and "No mix" on Spotify's side, once O15 and O16 are answered.
- Mixing into queued and autoplay tracks (`automix.queue`, `automix.autoplay`), and Mix outside playlists.
- Fade metadata for the whole queue, and the `AutoTransition` event.
- Vocal-clash avoidance (kind 218), mixability (219), the energy envelope (142).
- "Arrange for mixing" and any tempo/key ordering.
- **Local-file analysis** on #240's index. The engine's `OnsetDetector` (`Audio/OnsetDetector.cs:13-45`) finds onsets
  from the visualizer's live band flux; it is not a beat tracker and cannot analyse a file ahead of a join.

---

## 5. Risks, unknowns and verification

### 5.1 Risks

| Risk | Detail | Mitigation |
|---|---|---|
| **One capture, 92 seconds** (biggest) | Everything wire-level rests on a single session: two presets, one save, no Mix-off, no dealer socket, and the saved transition's real crossing is not in it (report §1, §2 step 20). Several recipe fields are guesses. | The adapter is three files and a table. Wavee writes only byte shapes that were seen (one `mix-state` on, one `set-transition` with a preset change), copies unknown recipe fields verbatim, and offers nothing whose storage is unknown. Capture round 2 (§5.2) is short and listed. |
| Writing to Spotify | A malformed recipe could corrupt a transition for every listener of the playlist; a lens write on the wrong list is visible to followers. | Optimistic UI with revert on refusal; never write on a playlist the user does not own; the first live writes are the owner's, on a scratch playlist, checked in the official client; the capture's recipe decoder decodes what Wavee sent. |
| The same revision, different content | The lens changes items without changing the revision. A cache or diff baseline keyed by revision alone would serve stale chips or lose a save. | Cache identity = (revision, mix lens etag) (§3.2); phase 1 starts by reading the cache and diff-replay code, which I did not. |
| Fidelity | Wavee's DSP differs from the official client's: shelf frequency, filter ranges, Q, and the reading of 4-point curves as cubic Béziers is **[inferred]**. A saved transition will sound similar, not identical. | "Spotify-compatible" in the change file; A/B by ear on the same saved pair; the mapping is one table (§3.4). |
| Auto differs between clients | Auto is never stored and Spotify's heuristic is unknown, so an un-edited pair may cross at a different point in Wavee than in the official client, and the mix-out a phone shows comes from whichever device plays. | Stated (D15). The reference table makes the differences visible. An edited pair is identical in timing everywhere. |
| Engine scope | E2 and E3 are new render-path and prepare-path code in the most regression-prone area (the G-112 / G-113 notes in `Playback.Audio.cs`). | The stage is bypassed unless armed; `StartSourceFrame` defaults to 0; with no mix planned every existing path is unchanged. Phase 2 ships alone and is listened to before phase 3. |
| Effective duration | The seek bar's end moves when the timing becomes known mid-track (data landing late) or the next row changes. | Analysis for the playing and next two rows is fetched at track start, so it is normally known within a second; the change is one bound value, proven with `trace` (no layout move). |
| Play accounting | A track mixed out of ends before its file does; how Spotify counts that play, and whether the missing `AutoTransition` event matters, is not visible. | The mix-out is in the track's last minute for Auto; the official client itself reports the shortened duration. |
| Spotify ToS / account risk | Private endpoints, as the whole app uses; now also private writes in a feature labelled Beta, gated by an entitlement (`Spotify.Feature.MixingTools {edit, view, listen}`, report §3.6) Wavee does not read. | Same headers and cadence as the capture; a refused write reverts and says so; Premium-only is assumed until shown otherwise. |
| A local layer beside Spotify's data | "No mix", a reset, and edits on playlists the user does not own live only on this PC (D11 (b)). Another device, or the official client, still plays Spotify's version of that pair. | Always labelled "Saved on this PC only"; the layer holds only what Spotify cannot; an override is dropped when its items leave the playlist. |
| Writing lengths Spotify's client did not produce | Quick and a dragged overlap write recipe fields 1.3 and 1.5 with values the capture never showed being *edited* (they were read off one saved recipe and the Connect metadata). | Quick stays at the shortest overlap seen (2.5 s); lengths are whole beats within 2.5 to 16 s; the first live saves are checked in the official client (O15). |
| The player flips later than the wire does | With D14 (b) Wavee publishes the outgoing track `overlap` seconds longer than the official client would. | Stated in §3.6a; one rule in one place (the announce frame); (a) remains available. |
| The seam's hover strip | 8 DIP at the bottom of every row become the seam's hover area while Mix is on. | Inside the row, no height change, not interactive at Compact; the row menu and the Transitions view are the keyboard and touch paths. |
| Licensing | No third-party code: mixer, biquads and decoders are in-house; Camelot is a notation. | — |

### 5.2 The checklist, after the capture

Revision 2's C1 to C18, each with its status and the report section.

| # | Assumption (rev 2) | Status | What the report says |
|---|---|---|---|
| C1 | Kind 225 `f1 = 1` means Mix is on | **Confirmed** | §3.5: `{1 1}` when mixed, empty when not; §2 step 3. The state itself is the server's `mix` lens (§3.1). |
| C2 | How the state is written, and who may | **Answered: on. Open: off, and who** | §3.1: `POST /signals`, `mix-state`, args `{1:"mix"}`, payload `{1:1}` **[inferred]**, lens `mix,auto`, sync-reason `CBAQAQ==`. Off is not captured (§7.1). The entitlement is in Nebula (§3.6). |
| C3 | Where "Mixed by" comes from | **Answered differently** | §3.1: there is no such field; it is `mix=true` plus the owner's display name. |
| C4 | What carries a saved transition | **Answered differently** | §4.1: item attribute 11 on the outgoing item, `automix.auto_transition_recipe` and `automix.transition_uri`; edit target = item attribute 14. Kinds 136, 126, 244, 247 were never requested (§3.5). |
| C5 | The saved transition's fields | **Partly** | §4.2: item ids, mix-out ms, overlap ms, track uris, beat-analysis ids, BPMs, preset id. No volume/EQ/filter fields. 1.4, 1.6-1.8, 1.18, 1.19, f3, f4 unproven. |
| C6 | How Auto is represented | **Answered** | §4.1, §5: absent; computed client-side; no suggestion endpoint. |
| C7 | The save request; reset to Auto | **Confirmed: save. Open: reset** | §3.2: `set-transition`, args = attribute 14, payload `{1 new, 3 previous}`, sync-reason `CA8QAQ==`, reply = the list, revision unchanged. |
| C8 | What each preset sets | **Partly; my table was wrong** | §4.2: Fade = id 1 = smooth crossfade + center bass swap; Rise = id 3 = overlap + end bass swap + low pass in / high pass out. Blend, Wave, Melt not seen. |
| C9 | Numeric shapes | **Answered for the description** | §4.3: curves C1 to C16. The DSP (Hz, dB) is local and not on the wire (§5). |
| C10 | Beatmatching | **Open** | §7.4: never switched on; every transition at rate 1.0. |
| C11 | Incoming track started at a cue | **Confirmed in substance** | §3.3, §5: `fade_in_start_time` 2874 / 4144 / 7794 ms, each a kind 217 downbeat, none a kind 28 candidate. |
| C12 | Kinds 28 and 217: fields, availability | **Mostly confirmed** | §3.5: 217 f5 = 1.0 marks the downbeat, f3 is the analysis id; 28 as assumed and also fetched for album playback. 217 outside a mixed list: not seen (O12). |
| C13 | The gate; Premium-only | **Partly** | §3.5: 170 = `"mix"` tells the client to apply the lens; 235 probably gates the button **[inferred]**; §3.6: Nebula `mixing-tools = "EDIT"`. Premium-only not shown. |
| C14 | Shuffle, repeat, the queue on a mixed list | **Open** | Not exercised. List attributes `automix.queue=true`, `automix.autoplay=true` exist (§3.1). |
| C15 | What a non-editor listener can toggle | **Open** | Only the owner's own playlists are in the capture. |
| C16 | How a mixed join is reported | **Partly** | §3.3: duration cut to the mix-out, an `AutoTransition` event, session type `"crossfade"`. The real crossing is not in the capture (§2 step 20). |
| C17 | Player-bar BPM/key only in a mixed playlist | **Open** | §6 says the official client shows it; in which contexts is not stated. |
| C18 | What the editor's waveform draws | **Open** | §7.7: kind 142 or 237. Wavee draws 237 either way. |

**Still open, and the capture that would close each** ("capture round 2": items 1 to 5 are a few clicks each in the
official client with Fiddler running; none blocks the smallest cut):

| # | Open item | Blocks | Capture |
|---|---|---|---|
| O1 | How Mix is turned **off**, and whether that drops the recipes | D3 (a) | 1. Switch Mix off on a scratch playlist that has one saved transition; switch it on again. |
| O2 | Preset ids and curves of **Blend, Wave, Melt** | offering them as styles | 2. Select and save each preset on three pairs; let one play. |
| O3 | How a **Volume / EQ / Filter** choice that departs from the preset is stored | the option rows | 3. Change one dropdown, save. |
| O4 | The **beatmatching** flag and rates (recipe 1.6 to 1.8, `*_cuepoint.tempo`) | beatmatching | 4. Switch Beatmatching on, save, let it play. |
| O5 | Recipe field **1.4** (in-point), and 1.18, 1.19, f3, f4 | **phase 7** writes 1.4 when the in-point is not 0 | 5. Save a preset on a pair whose Auto in-point is not 0. Until then Save is offered only on pairs whose in-point is 0. |
| O6 | **Reset to Auto** | that button | 6. Reset an edited transition. |
| O7 | Kind **142**'s layout | nothing in 0.4 | — |
| O8 | **Dealer pushes** after a save or a state change (does another device hear it?) | cross-device freshness: until known, Wavee re-reads a mixed playlist's lens when its page is activated | 7. A capture with the dealer WebSocket proxied. |
| O9 | How a Connect **receiver** gets the curves (context resolve with the lens, or the sender's metadata) | nothing: Wavee plans itself | 8. Transfer a mixed context to another device while capturing that device. |
| O10 | The rule behind the duration-dependent curve values (0.998 / 0.999, 0.46875 / 0.484) | curve fidelity on long windows | more saved transitions of different lengths (comes with 2). |
| O11 | Whether a read with only the `auto` lens returns the mix attributes of a mixed list; the `spotify-applied-lenses` value for the mix lens on a diff | one extra read on first open | 9. Open a mixed playlist cold. |
| O12 | Whether kind 217 is served for tracks outside a mixed playlist | deleting the JSON beat route | a one-line probe by the owner on the diagnostics page (never a `--spotify-*` CLI probe). |
| O13 | `stop-automix-preview`, preview parameters f6, f14, f15, and the "New effects" opt-in (`mixing-fx-looping-jogwheel`) | the Connect preview | with 2, on a second device. |
| O14 | Spotify's Auto heuristic | D15 stands without it | more auto plans (any capture of a mixed playlist playing adds some). |
| O15 | Whether the official client accepts and plays a recipe whose overlap was edited, and one shorter than 2510 ms | a shorter Quick (D17 (b)); confidence in the overlap drag | not a capture: save from Wavee on a scratch playlist and look at that pair in the official client. |
| O16 | Whether Spotify has any per-pair opt-out | "No mix" on Spotify's side (D16 (b), (c)) | 10. Look for one in the official editor (the web research says there is none); if it exists, capture it. |

### 5.3 What else I could not verify

- Wavee's playlist cache and diff-replay keying (§3.2), the inbound context-URL parsing (§3.7), the track-metadata
  writer of `PutState` beyond the lines cited.
- The audio backend's `PrepareAsync` body (E3's cost).
- Each theme's seek rail and time label views (I read the contract, `PlayerTransportProps.cs`, not the three
  pictures); the WinUI stage pane's Crossfade twin; whether the track table's drawer can host a second body kind
  without change (I read `ToggleExpanded` and its helpers, not the whole host); whether `RestorePoint` can be
  applied mid-session; which local store fits the overrides.
- Whether Wavee handles `speed-preview` / `stop-speed-preview`, which sit beside `automix-preview` (§1.6).
- The screenshots themselves, and the raw capture: I read the report, not the raw capture.

### 5.4 Verification (no tests: build, inspector, ears, logs)

- **Every phase:** `dotnet build src/apps/Wavee/Wavee.csproj` Debug, clean (warnings are errors); the engine phase
  also `dotnet build src/FluentGpu.slnx` in fluent-gpu.
- **Wire (phases 1, 7):** the diagnostics "Mix" section against the capture's recipe decoder on the same base64; after a save
  from Wavee, the official client shows the preset on that pair.
- **UI (phases 5, 7):** `tools/inspect.py` per theme (classic, winui, zune) × the playlist boards' sizes ×
  light/dark on the fake mixed playlist. Phase 5: `render` with Mix off, Mix on in the Tracks view (rows must look
  as they do today but for the dot and the break), and the Transitions view; `trace=90` on switching Mix, on the
  view switch, on the lens read landing, and across a join playing (only the label's text and the rail's fill
  change); the seam's hover and focus states; the queue with and without a mixed context. Phase 7: the open card in
  both views and as the Compact sheet; `trace` on open and close, on every style pick and on the overlap drag;
  `lint min=error` clean and `min=warn` read everywhere; `crossing` at each tier edge the page spans, both ways, with
  a card open; the vacancies (`state kind=loading|error|offline`). Screenshots looked at beside the boards.
- **Audio (phases 2, 3, 7), by the owner on a real device**, with the `[mix]` log open. The listening matrix:
  1. the saved Rise transition on the owner's test playlist (163.7 s, 2.9 s), A/B with the official client;
  2. a saved Fade; the same pair re-saved as Rise from Wavee;
  3. Auto pairs with the in-point at 0 and at a downbeat (the report's pairs at 7794, 4144 and 2874 ms);
  4. the pair before the local file, a pair set to No mix, a queued song between two mixed rows, an episode, a
     music video → the base hand-off and the log's why-not; shuffle on → Auto on every shuffled pair, the saved
     transition only when its pair comes up in order;
  5. seek, skip, pause, Mix switched to listen-without and an output-device switch (a) after scheduling and before
     the start frame, (b) mid-transition (a seek there returns to the outgoing song, skip jumps to the incoming one):
     no stuck voice, no stuck filter or bass cut, position correct afterwards; "Mixing into …" shows for exactly the
     overlap;
  6. Hear it from the card (playback returns to where it was), then Done, then the same pair in normal play; each
     of Smooth, Lift and Quick, and an overlap dragged to 4 and to 16 beats;
  7. a phone on the account shows the right track, the mix-out as the end, and the incoming track starting at its
     in-point;
  8. a non-mixed context → the 0.3.13 behaviour exactly (compare `Metrics`).
- **Steady-state allocation:** the diagnostics page's audio counters and the frame bench
  (`docs/guide/frame-bench.md`) show no new per-block or per-frame allocation with a transition running and with the
  seam card open.

### 5.5 Questions for the R&D session with DJs

1. Wavee's curve-to-sound mapping (§3.4): is a 200 Hz shelf at −26 dB the right "bass kill"? Are the filter ranges
   (low-pass 120 Hz to 20 kHz, high-pass 20 Hz to 6 kHz) and the default Q right for "low pass in, high pass out"?
2. A/B of one saved transition in Wavee and in the official client: what differs first to a trained ear?
3. Wavee's Auto picks Fade for every pair. Should it pick Rise for some (matched tempo, compatible key)? Which?
4. Auto's window: 8 beats ending at the cue (planned). When is 4 or 16 right? What should Auto do for tempos far
   apart, where the official client used a 2.9 s window that is not on the beat?
5. The in-point: the start cue snapped to a downbeat, or always the head, or the first downbeat?
6. The seam card recommends instead of warning. Which recommendations are right: "a quick cut" for tempos far
   apart, "a shorter overlap" for a key clash? Should +2 on the wheel count as compatible?
7. Beatmatching, when it is built: stretch the incoming track and glide back, stretch the outgoing one, or meet in
   the middle? What limit (±6 %)?
8. The four styles: are Smooth, Lift and Quick the right three to offer first, and do the one-sentence descriptions
   say what a listener hears? Is "about 2.5 s on the beat" short enough to be called Quick?
9. When should Auto recommend Quick, and should it ever choose Lift?
10. The overlap strip moves only the length, in whole beats. What is the next thing to make movable: the mix-out
    end, or the in-point?
11. For the timecode users (#154): during a mixed join, should the timecode follow the outgoing track until it is
    over (what the player shows), or the incoming track from the first frame of the join (as Spotify's own state
    does)?
