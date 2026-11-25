# Mix (#48): the wire, as the official client speaks it

Status: draft, 2026-11-25. Target: the release lines from `release/0.4.0` on (the evidence behind
[`48-mix-full-support.md`](48-mix-full-support.md)).

What this covers: what the official desktop client sends and receives for playlist Mix. Reading a mixed playlist, the
writes (Mix on and off, saving a transition, the other mix signals), the recipe as it travels, the editor's preview over
Connect, what a player publishes while it plays a mixed playlist, a list play sent to a remote, the analysis kinds, and
who fetches them when. The data model (presets, styles, value semantics, the recipe schema, Auto) is in
[`48-mix-reference.md`](48-mix-reference.md).

Sibling documents:
- [`48-mix-full-support.md`](48-mix-full-support.md): the plan (design, phases M1 to M8, open questions).
- [`48-mix-reference.md`](48-mix-reference.md): presets, enums, value formulas, recipe schema, save payload, Auto.
- [`48-mix-implementation.md`](48-mix-implementation.md): the 0.4 design (its revision 6 note says what these files replace).
- [`48-mix-ux-direction.md`](48-mix-ux-direction.md): the UX source of truth.
- [`dj-protocol.md`](dj-protocol.md): AI DJ, which publishes the same fade keys with its cue fields filled in.

Source: captures of the official desktop client 1.3.10, 2026-11-25; the raw material is kept outside the repo (it holds
account data). Two sessions: a **morning session** (the playlist page, Mix off and on, the editor with 15 previews, two
saves, an add and a move; the mixed playlist was not played) and an **afternoon session** (the mixed playlist played
through six seams, and a list play sent to Wavee). Facts tagged [client] come from the official client's own logic or
data, not from the wire. Wavee code references are to `release/0.4.0` at `0a45dbfbc`, paths relative to
`src/apps/Wavee/`.

Shapes are written by hand. Ids, tokens and account data are replaced: `spotify:playlist:<id>` is the mixed playlist,
`spotify:track:<A>` / `<B>` the two tracks of a pair, `<16 hex>` an item id, `<8 hex>` a lens etag, `<32 hex>` an
analysis hash, `<24 B revision>` a playlist revision.

Confidence tags: **[wire]** = bytes seen; **[proven]** = cross-checked against an independent source in the capture
(the client's own UI analytics events, the beat grid, durations); **[inferred]** = consistent with every sample, not
independently proven; **[client]** = the client's own logic or data, not seen on the wire; **[?]** = open.

---

## 0. Headline results

1. **The editor's previews go over Connect.** Every preset click and every point drag sends
   `POST connect-state/v1/player/command/...` `{"endpoint":"signal","signal_id":"automix-preview","parameters":<base64>}`
   to the **active device**. In the morning session that was a Wavee instance, which advertises `automix-preview` in its
   player state and drops the command (§5). 15 previews were sent and none played.
2. **Preset ids are proven** by the client's UI analytics (a `preset_chip` click about 200 ms before each preview):
   1 Fade, 2 Blend, 3 Rise, 4 Melt, 11 Slam, 17 Wave, 21 Surge, 22 Afterglow, absent = No transition. The EQ choice
   "Bass fade out" is preset field 2.4 `{1: 15}`. Wavee's "3 = Rise" is right: 3 is the enum BassSwap, shown as Rise.
   Dissolve (20) comes from the client's table [client]. Full table: [`48-mix-reference.md`](48-mix-reference.md) §1.
3. **`set-transition`'s payload field 1 is the new recipe** (the one the server stores). Field 3 is the pair's Auto
   recipe without item ids (§3.2).
4. **Item attribute 14 is the `set-transition` signal template, not a flag.** The list-level `contents.5[]` carries the
   `mix-state` template. Not-mixable is item attribute 11, keys `not-mixable` and `not-mixable.reason` (§2).
5. **Signals do not bump the playlist revision and cause no push.** Only the mix lens etag in field 22 changes (§3.4).
6. **Kind 10 `Track.duration` (field 7) is `sint32` (zigzag).** With it, every analysis array has an exact length
   formula (§9). Wavee already reads it as zigzag.
7. **A playing mixed playlist publishes the whole plan**: every upcoming track carries its fade keys, PlayerState's
   duration is the outgoing track's mix-out point, and the incoming track becomes current when it starts (§6,
   afternoon session).
8. **A remote `play` carries no lens.** The receiving player decides to mix from the playlist itself (§7).

---

## 1. The morning session, in order

| # | What | Detail |
|---|---|---|
| 1 | Startup read of the mixed playlist | `GET /playlist/v2/playlist/<id>`, `spotify-apply-lenses: auto`, sync-reason `CBMQAQ==`. The list attributes carry the Mix block; pair 1 has a stored recipe and an `automix.transition_uri`; field 22 holds the `auto` lens and the `mix` lens with its etag |
| 2 | A second mixed playlist (no edits) is read | `has-custom-transitions: false` |
| 3 | Your Library decoration | kind 225 MixState for every rootlist playlist; the mixed one answers `{1: 1}` |
| 4 | The user opens the playlist | `GET …/diff?revision=…&handlesContent=`, sync-reason `CAEQAQ==`, lens `auto`. Reply: no change (`f6 {1 rev, 3 rev}`), the mix etag unchanged |
| 5 | Row data | `client-feature-id: playlist`, kinds 85, 98, 99, 183, 222, 237 for 8 visible rows (BPM and key, waveforms) |
| 6 | List data | kinds 114, 170, 179; kind 170 AutoLens = `{1: "mix"}` |
| 7 | Editor data, batch 1 | `client-feature-id: automix_esperanto`: 5 tracks × kinds (6), 28, 142, 217, 218, 219 |
| 8 | Recommendations | `POST /playlistextender/extendp/` `{"playlistURI":…,"trackSkipIDs":[],"numResults":20}` → 20 tracks; no mix data in request or reply |
| 9 | Cluster | the active device is Wavee; its player state lists the signals `interact, automix-preview, speed-preview, stop-speed-preview` |
| 10 | Editor data, batch 2 | `automix_esperanto` for the local file (kind 10 → 400; kinds 6 and 222 also asked) and three tracks; kind 28 → **502** for those three and the local file |
| 11 | **Mix off** | UI toggle on → off; `POST …/signals` (§3.1), lens `auto`, sync-reason `CA8QAQ==`. Reply: no Mix list attributes, items lose attributes 11 and 14, `contents.5` lists only `UPDATE_PROMPT`, field 22 = `auto` only. Revision unchanged |
| 12 | After off | kind 170 with its etag ×3 → 200, empty AutoLens; `automix_esperanto` for the first track (28, 142, 217, 218, 219) |
| 13 | **Mix on** | UI toggle off → on; the signal with payload `{1: 1}`, lenses `mix,auto`, sync-reason `CBAQAQ==`. Reply: the full Mix read again, the stored recipe back byte for byte, the mix etag as before |
| 14 | After on | kind 225 with etag → 304; kind 170 with etag → `{1: "mix"}` |
| 15 | Pair 1's transition chip, then the edit panel | Connect `pause` → Wavee, `pause_origin.feature_identifier: "playlist"`; `automix-preview` #1: pair 1's Auto recipe, preset 1, top-level `{3: 1}` (no 4) |
| 16 | Nine `preset_chip` clicks | Fade (position 1), Rise (2), Blend (3), Wave (4), Melt (5), Slam (6), Surge (8), Afterglow (9), No transition (10); `element_path_reasons: "preset"`. One `automix-preview` per click except "No transition": 2.1 = 1, 3, 2, 17, 4, 11, 21, 22 |
| 17 | **Save #1** (pair 1 → No transition) | `save_button` id `preset`; `set-transition` #1. The reply stores the new recipe and a new `transition_uri` timestamp; the mix etag moves |
| 18 | Close | Connect `resume` (`resume_origin: playlist`) |
| 19 | Pair 2's chip | `pause`; `automix-preview` of pair 2's Auto recipe (out 188053, in 7794, preset 1) |
| 20 | A click on A's waveform (`seek_to_time` 186494.0033 ms) | preview: out 186494, 1.7 changes |
| 21 | A click on B's waveform (`seek_to_time` 46767) | preview: in 46767 |
| 22 | Slam chip (position **7**, reason **`custom`**) | preview with 2.1 = 11 |
| 23 | `eq_selector` → `eq_button` "Bass fade out" (position 7) | preview with preset `{1: 11, 2: 3, 4: {1: 15}}` |
| 24 | **Save #2** | `save_button` id `custom`; stored; the mix etag moves |
| 25 | Close | Connect `resume` |
| 26 | Add a recommended track | `POST …/changes` ADD, sync-reason `CAk=`, lenses `mix,auto`. Revision + 1; the mix etag moves. The recipes survive. The new item has attribute 14 at once. A push carries the ADD op, no mix data |
| 27 | New seam | `automix_esperanto`: kind 28 for the local file, kinds 6, 28, 142, 217, 218, 219 for the new track; 28 → 502 again |
| 28 | Diff after the push | sync-reason `CAI=`, lenses `mix,auto` |
| 29 | Move the new track | `changes` MOVE (op 4). Revision + 1, mix etag moves, push |
| 30 | New seam | `automix_esperanto` kind 28 only; both answers succeed now (the retry after the 502s) |
| 31 | The second mixed playlist is opened | diff; `automix_esperanto` for its 5 tracks |
| 32 | DJ starts | (not the mixed playlist; [`dj-protocol.md`](dj-protocol.md)) |

Headers on every playlist read and write here: no `If-None-Match`, no `ETag`. Also sent: `spotify-accept-geoblock:
dummy`, `spotify-dsa-mode-enabled: false`, `x-accept-list-items: audio-track, audio-episode, video-episode, audiobook`.

### 1.1 `spotify-playlist-sync-reason` (base64 protobuf `{1: reason, 2: 1?}`)

| Value | Decoded | Where |
|---|---|---|
| `CBMQAQ==` | {1:19, 2:1} | startup GET of the user's own playlists |
| `CAEQAQ==` | {1:1, 2:1} | GET or diff when a playlist page is opened |
| `CAYQAQ==` | {1:6, 2:1} | background GET or diff (19×) |
| `CAwQAQ==` / `CAw=` | {1:12(, 2:1)} | prefetch GETs (92×) |
| `CA8QAQ==` | {1:15, 2:1} | **signals**: `mix-state` off and both `set-transition` |
| `CBAQAQ==` | {1:16, 2:1} | **signals**: `mix-state` on |
| `CAk=` | {1:9} | `changes` (add, move) and the diff after a local change |
| `CAI=` | {1:2} | a diff triggered by a push |
| `CAU=`, `CBQ=`, `CCsQAQ==` | 5, 20, 43 | rootlist, a show, one other GET |

`spotify-apply-lenses`: `auto` on every read and on the off signal; `mix,auto` on the on signal and on every request
after it in that page session. **With `auto` alone the server applied the mix lens by itself** when the playlist was
mixed (field 22 of the startup read).

---

## 2. A playlist read under the mix lens [wire]

```
SelectedListContent {
  1  revision            bytes[24] = big-endian u32 counter || 20-byte hash
  2  length
  3  attributes {        1 name, 2 description, 3 picture (20 B), 4 0,
     12[] {1 key, 2 value}  format attributes, ONLY when the mix lens is applied:
        can-view-transition=true, automix.mode=auto, mix=true, mix-type=allow-custom,
        automix.queue=true, automix.autoplay=true, automix.ignore_setting=true,
        has-custom-transitions=true|false
  }
  5  contents {
     1 pos 0, 2 truncated 0,
     3[] item {
        1 uri
        2 attributes {
           1 added_by, 2 timestamp ms,
           11[] {1 key, 2 value}      item format attributes (mix lens only):
                automix.auto_transition_recipe = base64 TransitionRecipe   (the OUTGOING item of an edited pair)
                automix.transition_uri = "spotify:transition:<base62 gid>:<unix ms of the save>"
                custom_reporting_attribution = "MixedPlaylist"             (every mixable item)
                not-mixable = "true", not-mixable.reason = "entity-kind"   (a local file)
           12 item id (8 bytes)
           14 available signal {1 "set-transition", 2 {1 "mix", 2 <item uri>, 3 <item id>}}
           17 {2 add-interaction uuid}
        }
     }
     5[] list signal {1 key, 2 {...}}:  {1 "UPDATE_PROMPT", 2 {1 "ssp-agentic-list"}} always;
                                        {1 "mix-state", 2 {1 "mix"}} ONLY under the mix lens
  }
  15 last modified ms, 16 owner, 17 1, 18 capabilities, 21 created ms, 23 1
  22 applied lenses { 1[] {1 name, 2 etag (8 hex), 3 {1 200}} }   e.g. auto / "auto", mix / "<8 hex>"
}
```

- Item attribute 14 is on every mixable item (and appears at once on a newly added item); it is absent on the local
  file, and on every item when the mix lens is off. The client echoes it verbatim as the signal's field 2.
- A diff reply carries `f6 {1 from-rev, 3 to-rev}` and field 22 only.
- `transition_uri`: pair 1 kept the same gid before and after its save; only the suffix (the server time of the save)
  changed. Pair 2 got a new gid at its first save. The gid is not derived from the item ids, and the client never sends
  it. So the id is stable per pair (or per from-item) [inferred].

---

## 3. Writes

All writes go to `POST /playlist/v2/playlist/{id}/signals`, Content-Type form-urlencoded, body protobuf. The reply is a
full `SelectedListContent` read through the requested lenses.

### 3.1 `mix-state` [wire]

```
OFF (46 B):  {1 <24 B revision>, 2 {1 "mix-state", 2 {1 "mix"}}}
ON  (50 B):  {1 <24 B revision>, 2 {1 "mix-state", 2 {1 "mix"}, 3 {1: 1}}}
```

- The on payload is field **3** `{1: 1}` beside the template in field 2. In the client's terms the payload is
  `{1 playlistMixed bool}`, so off is an empty payload [client].
- Off: lens `auto`, sync-reason `CA8QAQ==`; reply 1348 B without the Mix attributes. On: lenses `mix,auto`, sync-reason
  `CBAQAQ==`; reply 3298 B with them. The client also asks for the `mix` lens with a force flag when it turns Mix on
  [client].
- **Off keeps the recipes:** on again brings back the same recipe bytes.

### 3.2 `set-transition` [wire]

```
{1 <24 B revision>,
 2 {1 "set-transition",
    2 {1 "mix", 2 <from-item uri>, 3 <from-item id 8 B>}      = item attribute 14.2, verbatim
    3 {1 "<base64 NEW recipe, with item ids>",                 → stored as automix.auto_transition_recipe
       3 "<base64 Auto recipe, no item ids>"}}}               → not stored
```

- Proof: the reply to each save stores exactly payload field 1.
- Field 3 is the pair's Auto recipe: the same out, in, overlap and speeds as the Auto preview, preset 1, `bpmA`/`bpmB`
  = the kind 222 tempos, top-level `{3: 1, 4: 1}`. On pair 1 it is **not** the previously stored recipe: that one had
  preset 3, field 3 had preset 1.
- The payload's fields by the client's names (1 transition, 2 completed, 3 auto, 4 userDefined, 5 userDefinedUri),
  which previous recipe goes where, and **Reset to Auto** (the signal with no payload [client]):
  [`48-mix-reference.md`](48-mix-reference.md) §6. Fields 2, 4 and 5 never appeared on the wire. **Open:** pair 1 had a
  stored user-defined recipe, yet its save sent field 3, not field 4.

### 3.3 Other mix signals [client]

Each is sent through the same endpoint with the signal data `{1 lensIdentifier "mix"}`. None was seen on the wire, so
their sync-reasons and replies are **open**.

| Signal | Payload | Meaning |
|---|---|---|
| `mix-opt-out` / `mix-remove-opt-out` | none | **"Listen without Mix", synced to the account** |
| `mix-auto-toggle-on` / `mix-auto-toggle-off` | none (lens `mix`) | the Auto mix toggle; its place in the official UI is **open** |
| `harmonize` (a playlist lens, requested on and off) | — | **Smart reorder** |

- The list attribute `mix-type` may also be `only-auto` (seen: `allow-custom`).
- **"Duplicate Mix"** (a playlist the user does not own) is the playlist service's `copy`; the editor then reopens on
  the copy.
- How an opt-out reads back on another device (which attribute or lens shows it) is **open**.

### 3.4 Server behaviour [wire]

- Signals do not change the revision (unchanged through all four signals) and cause no dealer or AP push. Only the mix
  lens etag in field 22 moves: etag 1 → (off: lens gone) → etag 1 again (on: unchanged, because the edits were kept) →
  etag 2 (save #1) → etag 3 (save #2) → etag 4 (ADD, revision + 1) → etag 5 (MOVE, revision + 1).
- Other devices therefore see a mix edit only when they read the lens again.
- `changes` (ADD, MOVE) keep the recipes. A recipe stays on its from-item even when the seam it described changes (the
  added track was moved between two items; no recipe was touched). Pushes carry only the ops, never mix attributes.
- Removing the from-item of an edited seam was not captured: what happens to its recipe is **open**.

---

## 4. The TransitionRecipe on the wire

The schema with the client's field names: [`48-mix-reference.md`](48-mix-reference.md) §5.

### 4.1 The 20 samples [wire]

Stored and saved: R1 (pair 1, stored before the session), R2 (save #1, new), R3 (save #1, Auto baseline), R4 (save #2,
new), R5 (save #2, Auto baseline). Previews P1 to P15 (§5).

- **Pair 1**: not beatmatched. A 166609 ms at 117.002 bpm → B 196481 ms at 77.0036 bpm.
- **Pair 2**: beatmatched. A (the same track as pair 1's B, 77.0036 bpm) → B 135584 ms at 76.9975 bpm.

| # | Pair | 1.3 `startAMs` | 1.4 `startBMs` | 1.5 `durationMs` | 1.7 `speedB` | 1.8 `durationBars` | 1.9 `isBeatmatched` | 1.16 / 1.17 `bpmA`/`bpmB` | 2.1 preset | 2.2 type | 2.4 EQ | top 3 | top 4 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| R1 stored | 1 | 163719 | – | 2890 | 1.0 | 0 | – | 117.002 / 77.0036 | 3 | 1 | – | 1 | 1 |
| P1 editor open | 1 | 163719 | – | 2890 | 1.0 | 0 | – | 117.002 / 77.0036 | 1 | 1 | – | 1 | – |
| P2–P9 chips | 1 | 163719 | – | 2890 | 1.0 | 0 | – | 117.002 / 77.0036 | 1, 3, 2, 17, 4, 11, 21, 22 | 1 | – | 1 | 1 |
| R3 baseline | 1 | 163719 | – | 2890 | 1.0 | 0 | – | 117.002 / 77.0036 | 1 | 1 | – | 1 | 1 |
| **R2 new ("none")** | 1 | – | – | **1** | 1.0 | 0 | – | – | **–** | 1 | – | **–** | 1 |
| P10 editor open | 2 | 188053 | 7794 | 6233 | 1.0001602 | 2 | 1 | 77.009468 ×2 | 1 | 3 | – | 1 | – |
| P11 A point moved | 2 | 186494 | 7794 | 6233 | 1.0000801 | 2 | 1 | 77.009468 ×2 | 1 | 3 | – | 1 | – |
| P12 B point moved | 2 | 186494 | 46767 | 6233 | 1.0000801 | 2 | 1 | 77.009468 ×2 | 1 | 3 | – | 1 | – |
| P13 Slam | 2 | 186494 | 46767 | 6233 | 1.0000801 | 2 | 1 | 77.009468 ×2 | 11 | 3 | – | 1 | 1 |
| P14 Bass fade out | 2 | 186494 | 46767 | 6233 | 1.0000801 | 2 | 1 | 77.009468 ×2 | 11 | 3 | {1:15} | 1 | 1 |
| **R4 new** | 2 | 186494 | 46767 | 6233 | 1.0000801 | 2 | 1 | 77.009468 ×2 | 11 | 3 | {1:15} | 1 | 1 |
| R5 baseline | 2 | 188053 | 7794 | 6233 | 1.0001602 | 2 | 1 | 77.0036 / 76.9975 | 1 | 3 | – | 1 | 1 |

Constant in all 20: 1.6 `speedA` = 1.0f; 1.18 = 1.19 = 1.0 (double); 1.10 = 1.12 = A's uri, 1.11 = 1.13 = B's uri;
1.14 / 1.15 = kind 217 field 3 (`<32 hex>`) of A and B [proven]; 1.1 / 1.2 = the from and to item ids as 16 lowercase
hex, present in stored recipes and every preview, absent in the baselines. R4 = P14 and nothing else (timing and preset
byte-identical) [wire].

### 4.2 Wire notes

- Ascending field order; floats are fixed32, 18 and 19 fixed64; 1.8 is written even when 0 (R1, R2) [wire].
- Top-level 3 (`isOverlapOverride`) is absent only in the "none" recipe R2; top-level 4 (`isPresetIdOverride`) is
  absent only in previews of an untouched Auto transition (P1, P10 to P12). Top-level 5 never appeared.
- The client's analytics name the save: `save_button` id `preset` for an untouched preset, `custom` once points or the
  EQ were changed; chips clicked after a point drag carry `element_path_reasons: "custom"`. This is the "Custom" state.

### 4.3 Preset ids [proven: a UI click, then a preview 200 ms later on the same pair, only 2.1 changed]

| 2.1 | Client UI id | Position on pair 1 |
|---|---|---|
| 1 | `simpleFade` (Fade) | 1 |
| 3 | `bassSwap` (Rise) | 2 |
| 2 | `eqSwap` (Blend) | 3 |
| 17 | `bassSwapLowPassFilterIn` (Wave) | 4 |
| 4 | `bassSwapFilterIn` (Melt) | 5 |
| 11 | `simpleCut` (Slam) | 6 (pair 2: 7) |
| 20 [client] | Dissolve, not clicked | 7 |
| 21 | `surge` | 8 |
| 22 | `afterglow` | 9 |
| absent | `none` (No transition) | 10; no preview; saved as R2 |
| 2.4 `{1:15}` | `bassFadeOut` (`eq_selector` / `eq_button`, position 7) | – |

- The Auto transition uses preset 1 on both pairs (P1, P10, R3, R5).
- No UI string list travels over the network: the camelCase ids above are the only names on the wire.
- Pair 2 lists one more chip before Slam than pair 1 (position 7 against 6), so the chip list depends on the pair
  (probably styles offered only for beatmatched pairs). Which chip: **open**.

### 4.4 Times, overlap, tempo and speed against the analysis data [proven against the kind 217 beat grids]

- **Pair 2's Auto (P10, R5):** out 188053 = A's beat #241 (188.05299 s, a downbeat); out + overlap = 194286 = A's beat
  #249 (a downbeat), so the overlap is exactly 8 beats of A (2 bars, `durationBars` 2). In 7794 = B's beat #10
  (7.79446 s, a downbeat). The overlap is **centred on kind 28 cues**: A's end cue (field 2) = 191171 ≈ out +
  overlap / 2 (191169.5); B's start candidate field 3[1] = 10910 = in + overlap / 2 (10910.5). B's top candidate (field
  1) = 12470 (beat #16, bar position 3, not a downbeat) was skipped. Config
  `core-automix.auto_transition_downbeat_confidence_threshold = 40` fits a "the cue must sit on a downbeat" rule
  [inferred].
- **Pair 1's Auto (R1, R3, P1):** 117 against 77 bpm, `durationBars` 0, speeds 1.0, in 0, and out + overlap = 163719 +
  2890 = 166609 = A's duration exactly: a plain end-of-track crossfade with B from 0. The 2890 ms is the exact
  non-beatmatched rule: A's last end candidate lies 390 ms before its end, and 390 + 2500 = 2890
  ([`48-mix-reference.md`](48-mix-reference.md) §7.1).
- **Edits:** the clicks on the waveforms (`seek_to_time` 186494.00329589844 on track A, 46767 on track B) become 1.3 and
  1.4 directly. 186494 = A's beat #239 (186.49400 s, bar position 3); 46767 = B's beat #60 + 10.3 ms (bar position 3).
  Both points sit on the same bar phase; the overlap stays 6233. Wavee's beat snapping can use kind 217 `f2[].1` seconds
  × 1000.
- **1.16 / 1.17:** on the beatmatched pair both are 77.009468 = f32(8 × 60000 / 6233), the common tempo that makes 8
  beats span the overlap, and they stay unchanged when out or in move. On the unmatched pair and in both baselines they
  are the kind 222 tempos (117.002 / 77.0036; 77.0036 / 76.9975). "BPMs frozen at save time" holds only for an unmatched
  pair.
- **Speed (`speedB`):** not 1.16 / 1.17 (those are equal, yet `speedB` ≠ 1), and not the kind 222 ratio (77.0036 /
  76.9975 = 1.0000792 against 1.0000801 observed; the Auto value is 1.0001602). The two values are exactly
  1 + 1344·2⁻²³ and 1 + 672·2⁻²³ (2 : 1). Moving A's out-point (P10 → P11) halved it; moving B's in-point (P11 → P12)
  did not change it. Local tempo ratios from the beat grid (windows of 1, 2, 4 and 8 beats around either point, against
  B's kind 222 or 217 tempo) reproduce neither value. The Auto dataset gives `speedB = bpmA / bpmB` in 38 of 73
  beatmatched pairs; this pair is one of the other 35. Rule: **open**
  ([`48-mix-reference.md`](48-mix-reference.md) §7.2).

---

## 5. Connect: the editor's preview

Command, JSON, gzip, sent from the editing desktop to the active device:

```json
{"command":{"endpoint":"signal","signal_id":"automix-preview","parameters":"<base64 protobuf>",
  "options":{"override_restrictions":false,"only_for_local_device":false,"system_initiated":false},
  "logging_params":{...,"command_id":"<32 hex>"}},"connection_type":"wlan","intent_id":"<32 hex>"}
```

Reply `{"ack_id": …}` (the first one `{"errors": []}`).

Parameters, by the client's names [client], with what the 15 previews carried [wire]:

```
AutomixPreview {
  1 trackUri1 string            2 trackUri2 string                  // A, B
  3 automixMode string          "auto" in every preview (= the list attribute automix.mode)
  4 transitionUri string        not seen
  5 startPositionMs int64       3000 in every preview: the pre-roll before the out-point
  6 relativeStartPosition bool  1 in every preview
  7 cuepoints                   not seen
  8 stopPositionMs              not seen
  9 transitionRecipe            the recipe, with item ids (§4)
  10 playableTrackUri1  11 playableTrackUri2   // A, B
  12 contextUri                 spotify:playlist:<id>
  13 armId                      not seen
  14 itemSpeedA double  15 itemSpeedB double   // 1.0 in every preview
}
```

- **Sequence [wire]:** the editor opens → `pause` with `pause_origin.feature_identifier: "playlist"` → a preview of the
  current transition → one preview per chip, point or EQ change → the editor closes → `resume` with
  `resume_origin: playlist`.
- **Stopping [client]:** `stop-automix-preview` with the payload `"true"` or `"false"` (resume afterwards or not); when
  the device cannot take that signal, the client falls back to a skip-next. Not seen on the wire: the morning device
  (Wavee) did not advertise it, and the editor closed with `resume`.
- **The receiver [client]:** the receiving player plays the context **`spotify:mixing:preview`**, with items flagged
  `is_transition_preview=true` and uids `preview_1` / `preview_2` (or the prefixes `automix_preview_a_` /
  `automix_preview_b_`). A receiver's PutState during a preview was never seen on the wire.
- **The preview is rendered by the active device.** A Wavee that is the active device must render it (A from the
  out-point minus the pre-roll, through the transition, into B) or stop advertising it. On `release/0.4.0` Wavee still
  advertises it (`Spotify/Spotify.Decode.PutState.cs:470`) and acks and drops every unknown `signal_id`
  (`Spotify/Spotify.Decode.Connect.cs:559-633`).
- Devices advertise the capability `supports_playlist_mixing` (config `core-connect-feature.supports_playlist_mixing`).
  In the morning cluster the Wavee device showed `supports_playlist_mixing: true`, yet the code at `0a45dbfbc` writes it
  nowhere (only the signal). Where that value came from is **open**.
- The desktop's own PutState lists the preview signals in PlayerState field 33 too (§6.1).

---

## 6. What a playlist-Mix player publishes [wire, afternoon session]

The official desktop's own PutState messages while it played six seams of a mixed playlist:

| Seam | Transition |
|---|---|
| 0 | "none" |
| 1 | Slam (11) with EQ override 15, beatmatched, 2 bars |
| 2, 4 | Fade (1), not beatmatched |
| 3 | preset 19, beatmatched, 2 bars |
| 5 | Wave (17), beatmatched, 4 bars |

### 6.1 PlayerState

| Field | Value |
|---|---|
| f3 context_url | `context://<playlist uri>?spotify-apply-lenses=mix` |
| f11 duration | **`audio.fade_out_start_time`** (the recipe's `startAMs`) when the track has an outgoing transition; the full duration when it has none. The track metadata key `duration` always holds the full file duration |
| current track | **B becomes current when B starts playing**, at A's `fade_out_start_time` (the PutState follows about 50 ms later), with position = `startBMs`. A is never shown during the overlap |
| f9 playback_speed | B's `speedB` while it is tempo-matched (seen 1.00008, 0.901696, 1.09994); also the metadata key `playback_speed` as `%.6f` |
| f21 context metadata | `mix=true`, `mix-type=allow-custom`, `has-custom-transitions=true`, `can-view-transition=true`, `automix.mode=auto`, `automix.queue=true`, `automix.autoplay=true`, `automix.ignore_setting=true`, `mixer_enabled=true`, `ignore_enhance_lens=true`, `enhanced_context=false`, `initiated_from_list_play_esperanto=true`, `player.arch=3` (plus the usual playlist keys) |
| f33 signals | `set-transition`, `mix-state`, `speed-preview`, `automix-preview`, `stop-automix-preview`, `stop-speed-preview`, `UPDATE_PROMPT`, `list-next-page` |
| f17 restrictions | nothing Mix-specific |

`player.arch` was 2 in the morning DJ session's PutState messages ([`dj-protocol.md`](dj-protocol.md) §4) and 3 here; what it
means is **open**.

### 6.2 The speed glides back

About 0.5 s after B's fade-in ends, the speed moves towards 1.0 in steps of **0.005, one step per second**, and every
step is published (0.901696 → 0.906696 → … → 1; 1.09994 → 1.09494 → … → 1). A difference under 0.005 goes in one step
(1.00008 → 1). A plays at 1.0 in every sample (`speedA` = 1): only B is stretched.

Wavee (M4) publishes B's `playback_speed` when B becomes its deck row, at A's end (D14 b), not when the overlap starts
as the official client does; the glide steps after that are published as above. The beatmatched `fade_in_duration` is
rounded to the ms, so B's mixer window can differ from A's by a few frames.

### 6.3 Track metadata

On the current track and on **every** upcoming `next_tracks` entry (9 of 9 decorated):

| Key | Value |
|---|---|
| `automix.mode` | `auto` |
| `audio.fade_out_start_time` | `startAMs` |
| `audio.fade_out_duration` | `durationMs` (A's time, speed 1) |
| `automix.fade_out_cuepoint.position` | on the current track: `fade_out_start_time + fade_out_duration`; on upcoming tracks: 0 |
| `audio.fade_in_start_time` | `startBMs` |
| `audio.fade_overlap` | `durationMs` of the incoming seam |
| `audio.fade_in_duration` | **beatmatched: `durationMs × speedB`**, B's media time (6233 × 1.00008 = 6234; 4364 × 0.901696 = 3935; 11637 × 1.09994 = 12800). **Not beatmatched: B's earliest start candidate (kind 28 field 3, the smallest `ms`) + 2500**, independent of the overlap: 5 of 5 (candidates 545, 10, 30, 10, 630 → 3045, 2510, 2530, 2510, 3130, for overlaps 2510, 4510, 5000, 2510, 2510) |
| `automix.fade_{in,out}_cuepoint.origin` / `.tempo` / fade-in `.position` | empty / 0 / 0 (unused by playlist Mix; DJ fills them) |
| `audio.fade_{in,out}_curves` | the volume lane |
| `audio.fade_{in,out}_eq_{low,mid,high}_gain_curves` | the EQ lanes, only those the style moves (an override to style 15 publishes all three on B, flat 0.5) |
| `audio.fade_{in,out}_filter_cutoff_curves`, `audio.fade_{in,out}_filter_resonance_curves` | the filter lanes, only for presets with a filter |
| `duration`, `playback_speed`, `position`, `media.type=audio`, `track_player=audio`, `provider=context`, `context_uri`, `requested_uri` | as always |

- In a non-beatmatched Fade the two curves run over **different lengths from the same start instant**: A's over
  `fade_out_duration`, B's over `fade_in_duration`.
- **A "none" seam** (seam 0): A carries **no** `audio.fade_*` or `automix.*` key and f11 is the full duration; B carries
  no fade-in keys. Its natural crossing was not captured (playback was restarted before A ended).
- A `play` at an index (not an advance) drops B's fade-in keys for one PutState and restores them 100 ms later. The keys
  describe the seam, played or not.
- No noise, effect or looping lane was seen: none of the six seams used one. Their published keys are **open**.
- Which analysis kinds the player fetched before each seam was not reported from this session (**open**; the morning
  DJ playback fetched only 28 and 142, §10).

### 6.4 Curve format

`[{"start_point": s, "end_point": e, "fade_curve": [{"x": …, "y": …}, …]}, …]`: segments over the fraction `s..e` of
the fade's duration. Inside a segment `fade_curve` holds control points in the segment's unit square: **2 points = a
line, 4 points = a cubic Bézier** (3 points, a quadratic, appear in DJ). Values are the raw style units of
[`48-mix-reference.md`](48-mix-reference.md) §4 (volume = gain; EQ 0.5 = 0 dB and 0 = cut; cutoff 0.5 = open, below
it a low-pass closing towards 120 Hz).

### 6.5 The published curves (segments `s–e: points`)

| Seam | Lane | Published curve |
|---|---|---|
| 1: Slam (11) + EQ override 15, beatmatched, 2 bars | A volume | 0–0.5: flat 1 · 0.5–0.53125: linear 1→0 · then 0 |
| | A EQ low | 0–0.5: (0,.5)(.75,.5)(.75,0)(1,0) · then 0 |
| | B volume | 0–0.46875: 0 · 0.46875–0.5: linear 0→1 · then 1 |
| | B EQ low / mid / high | flat 0.5 |
| 2 and 4: Fade (1), not beatmatched | A volume | 0–1: (0,1)(.6,1)(.6,0)(1,0) |
| | A EQ low | 0–0.46875: 0.5 · 0.46875–0.5: (0,.5)(.5,.5)(.5,0)(1,0) · then 0 |
| | B volume | 0–1: (0,0)(.4,0)(.4,1)(1,1) |
| | B EQ low | 0–0.46875: 0 · 0.46875–0.5: (0,0)(.5,0)(.5,.5)(1,.5) · then 0.5 |
| 3: preset 19, beatmatched, 2 bars | A volume | 0–0.998046875: 1 · then a step to 0 |
| | A EQ low | as seam 2 |
| | A cutoff | 0–0.5: 0.5 · 0.5–1: (0,.5)(0,.5)(1,0)(1,0) |
| | A, B resonance | flat 0.5 |
| | B volume | 0–0.001953125: a step 0→1 · then 1 |
| | B EQ low | as seam 2 |
| | B cutoff | 0–0.5: (0,0)(0,0)(1,.5)(1,.5) · then 0.5 |
| 5: Wave (17), beatmatched, 4 bars | all | as seam 3, with the volume step at 0.9990234375 / 0.0009765625 and the bass swap at 0.484375–0.5 |

- The bass swap always sits 1/16 bar before the centre: 0.46875 at 2 bars (1/32 of the window), 0.484375 at 4 bars
  (1/64). The volume step lasts 1/512 of a 2-bar window and 1/1024 of a 4-bar one.
- The two unmatched Fade seams (`durationBars` 0) put the bass swap where a 2-bar window has it (2 of 2).
- These are the same curves as the client's tables ([`48-mix-reference.md`](48-mix-reference.md) §3): rendering each
  seam from the tables at the same bar count must give exactly this table.

---

## 7. Wavee as the remote player: a list play from the desktop [wire, afternoon session]

- A list play with a start index (the path the playlist's own Play button uses) went out as a `play` command whose
  **`skip_to` is `{"track_uri": ""}`**. The index travels in `context.metadata.playlist_play_request`, base64 protobuf
  `{1 uri, 2 query, 4 {1 {3 <index>}, 3 1, 5 0}, 5 timestamps, 6 play origin, 7 1, 9 5}`, next to `playlist_query`.
  Wavee started at index 0.
- A click on a row in the desktop UI sends a normal `skip_to {track_uri, track_uid, track_index}` (morning session).
  So Wavee does not ignore `skip_to`; it does not decode the index of a list play.
- **The command carries no lens:** `context.url` is `context://spotify:playlist:<id>`, without
  `?spotify-apply-lenses=mix`. The receiving player decides to mix from the playlist itself (it reads the list's mix
  state) [inferred from the command].
- Not seen: a phone as the controller, and the official desktop as the receiver (commands arriving at the desktop are
  not visible in these captures, only their effects).

---

## 8. Cue points: kind 28

```
Cuepoints { 1 Cue start (fade-in) cue   2 Cue end (fade-out) cue
            3[] Cue start candidates   4[] Cue end candidates }
Cue { 1 uint32 ms   2 float bpm   3 uint32 1 = machine (ml) cue; ABSENT = human-curated cue   4 double confidence }
```

[wire, 40 responses]

- Human cues: no field 3, confidence exactly 1.0, an integer bpm (110.0, 156.0, 102.0, 166.0). Ml cues carry the
  analysed bpm (109.966, 155.998, 101.776, 82.697) [proven by the DJ player's `origin` keys]. A human cue is field 1 or
  2 when there is one.
- Candidates are sorted by confidence, with the human cue inserted. Fields 1 and 2 are not simply the top candidates:
  field 1 was always early (at most 23 % of the duration; one track had field 1 = 1050 while its first candidate was
  141310) and field 2 late (at least 87.5 %), across all 40 answers.
- Cue positions sit on kind 217 beats (±10 ms), mostly downbeats (§4.4).
- **A 502 is a normal answer** for a track not yet analysed; the client retries on the next seam change (§1, steps 10,
  27, 30).
- How Mix uses the cues: the non-beatmatched Auto rule takes A's **last end candidate** and B's **first start
  candidate** ([`48-mix-reference.md`](48-mix-reference.md) §7.1); one beatmatched Auto pair centred its overlap on A's
  end cue and a B start candidate on a downbeat (§4.4).
- The cuepoint keys of a player's track metadata: playlist Mix leaves `origin`, `tempo` and the fade-in `position` empty
  or 0 and sets the fade-out `position` on the current track only (§6.3). DJ ("radio" automix) fills them with the cue
  it used and ends each fade on a cue, where playlist Mix centres or anchors its overlap differently:
  [`dj-protocol.md`](dj-protocol.md) §7.

---

## 9. Analysis kinds used for mixing

Request: `POST /extended-metadata/v0/extended-metadata`, `Content-Encoding: gzip`, `Accept: application/protobuf`,
`client-feature-id: <id>`:

```
{1 {1 "<country>", 2 "<catalogue>", 3 <16 B request id>},
 2[] {1 <entity uri>, 2[] {1 <kind>, 2 "<etag>"?}}}        (the etag form is used for kinds 170 and 225)
```

Reply: `{2[] {1 {1 200, 2 3600 ttl s, 3 2592000 offline ttl s}, 2 kind, 3[] {1 {1 status, 2 etag, 4 ttl, 5 offline ttl},
2 uri, 3 Any {1 type_url, 2 bytes}}}}`. Durations below use kind 10 `Track.duration` (field 7, **sint32 zigzag**: raw
392962 → 196481 ms).

| Kind | type_url | Layout | Decode |
|---|---|---|---|
| 28 | `spotify.automix.proto.Cuepoints` | §8 | |
| 217 | `spotify.playlistmixing.extensions.mixbeats.Beats` | `{1 beatsPerBar=4, 2[] {1 float t_s, 3 barPos 1..4, 4 float conf (1.0 in all 3,607 beats of 15 tracks), 5 float 1.0 iff barPos == 1}, 3 "32-hex analysis id"}` | `t_ms = f1 × 1000`; a beat at 0.0 s omits field 1. The grid spans the whole track (one track: 253 beats to 196.624 s of 196.481 s). The median-interval bpm matches kind 222 within 0.003 bpm. Field 3 is the recipe's beats hash |
| 218 | `…mixvocalactivity.VocalActivity` | `{1 float 22050.0, 2 uint 1, 4 uint 315, 5 bytes}` | `fps = f1 / f4 = 70`; frame i covers `[i·f4/f1, (i+1)·f4/f1)` s = 14.2857 ms; `len = ceil(duration_ms × f1 / f4 / 1000)`, exact on 13 of 15 tracks (two were longer: +109 frames = +1.557 s and +292 = +4.171 s, analysed audio longer than the catalogue duration). One byte per frame (packed varint, all values < 128), 13..100 in every track: a vocal probability in percent with a floor of 13 [inferred: instrumental tracks have only 20–22 % of frames ≥ 90, vocal tracks 41–73 %]. Field 2 = 1 [?] |
| 219 | `…mixability.Mixability` | `{1 uint 1, 2 double 1.0 or absent}` | 1 = mixable (even for the local file). Field 2 is 1.0 or absent (0.0) with no visible pattern [?] |
| 142 | `spotify.playlist.tuner.extension.ListTunerAudioAnalysis` | `{2 uint 20, 3 bytes}` | field 3 = packed varints (0..248); field 2 = frame length in ms; `len = ceil(duration_ms / f2)`, exact on 46 of 47 tracks (+1 on the others). Correlation with the sum of kind 237's three bands 0.91–0.97: a broadband loudness envelope, 50 values/s |
| 237 | `…mixthreebandwaveforms.ThreeBandWaveforms` | `{1 uint 44100, 2 uint 20, 3 bytes, 4 bytes, 5 bytes}` | field 1 = source sample rate; field 2 = frame ms; fields 3/4/5 = packed varints 0..255, one per frame per band, `len = ceil(duration_ms / f2)` (193 of 195 exact, 2 × +1). Mean magnitudes f3 > f4 > f5: low / mid / high [inferred]. Fetched by `client-feature-id: playlist` for the visible rows of any playlist (editorial lists too), not only mixed ones |
| 222 | `…audio_attributes.v2.AudioAttributes` | `{1 double bpm, 2 {1 key "G", 2 mode 2, 3 {1 camelot "9B", 2 colour "#cb90ff"}}}` | all 10 samples are mode 2 with Camelot "nB" (major) [? 1 = minor]. The bpm can be an f32 widened to double (99.84124755859375). The source of recipe 1.16 / 1.17 for unmatched pairs |
| 6 | `spotify.descriptorextension.ExtensionDescriptorData` | `1[] {1 tag, 2 float score, 3 bytes category codes, 4 concept uri, 5 display name}` | mood and genre descriptors; asked by `automix_esperanto` only when not cached [? a style suggestion] |
| 170 | `spotify.autolensextension.v1.AutoLens` (playlist) | `{1 "mix"}` or empty | asked with an etag → 200 or 304 |
| 225 | `…mixstate.MixState` (playlist) | `{1 1}` when mixed, empty otherwise | `your_library` or no feature id, rootlist-wide; with an etag → 304 |
| 235 | `spotify.traits.v1.MixabilityTrait` (playlist) | `{1 1}` | once, for an editorial playlist |

Kinds 170, 225 and 235 are playlist-level; 28, 142, 217, 218, 219, 222, 237 and 6 are track-level.

---

## 10. Who fetches what, and when [wire]

| `client-feature-id` | Kinds | When |
|---|---|---|
| `automix_esperanto` | 28, 142, 217, 218, 219 (+6 and 222 when not cached; 10 for a local file → 400) | the **Mix UI**, never during playback: on opening a mixed playlist (5 + 4 rows; the first row again after the Mix toggle; a second mixed playlist's 5 rows), and for each **new seam** after an ADD (all kinds) and a MOVE (kind 28 only). Batches of at most 5 entities |
| `playlist` | 222, 237 (+85, 98, 99, 182/183) for rows; 170 (+114, 179) for the list | rendering any playlist page |
| `your_library` / none | 225 | rootlist decoration |
| `context_player` | **28 and 142 only**, 1 to 3 upcoming tracks per request, one request per kind | **playback** (the morning DJ session): about 10 s after play and before each upcoming track. Kinds 217, 218 and 219 were never fetched for playback, and all `automix_esperanto` requests were for the two mixed playlists |

The editor itself (previews, saves) fetched nothing more: everything it needed was fetched when the page opened. No
network call carries a curve.

---

## 11. Remote config and account flags [wire]

- `core-automix`: `max_auto_transition_length_fraction_permil = 300`, `auto_transition_downbeat_confidence_threshold =
  40`, `auto_transition_fallback_cuepoint_selection_strategy = "First"`, `auto_transition_use_curve_overrides = true`,
  **`use_backend_auto_transition = true`** (yet no backend call for an Auto transition was seen: the Auto recipe first
  appears inside the preview command).
- `playback-settings.use_playback_settings_crossfade = true`.
- `desktop-ui`: `playlistMixingAvailableStylesVersion = 8`, `playlistMixingMaxDurationPercent = 30`,
  `enableMixingCurveEditing`, `enableMixingEditHistory`, `enableMixingCurveValueDisplay`,
  `enablePlaylistMixingCurveShapePresets` = true.
- `core-connect-feature.supports_playlist_mixing`.
- Account product info: `mixing-tools = EDIT`, `mixing-fx-looping-jogwheel = 1` (the effects opt-in; Wavee does not
  read it).

---

## 12. Against Wavee on `release/0.4.0` (`0a45dbfbc`)

| Wavee says | The captures and the client | Verdict |
|---|---|---|
| 1.1 / 1.2 hex item ids; 0 in the "previous" recipe of a save | absent in the save's field 3, which is the Auto baseline, not the previous recipe | layout confirmed, meaning corrected |
| 1.3 out ms [inferred] | `startAMs`, the start of the overlap in A | confirmed |
| 1.4 in ms [guess] | `startBMs`: 7794, 46767; a click on B's waveform | confirmed |
| 1.5 overlap | `durationMs`: 2890, 6233 (8 beats), 1 for none | confirmed; "none" = 1 |
| 1.6 / 1.7 rates [guess] | `speedA` / `speedB`; 1.7 = 1.0000801 / 1.0001602 when beatmatched | extended; the speed rule is partly open |
| 1.8 beatmatch bool (Encode writes 1) | `durationBars` (0 or 2 seen), always written | **contradicted** |
| (1.9 dropped) | `isBeatmatched`, 1 when beatmatched | **missing** |
| 1.16 / 1.17 BPMs at save | beatmatched: the tempo that fills the bars; otherwise kind 222 | refined |
| 1.18, 1.19, f3, f4 carried, default 1 | `itemSpeedA/B`; `isOverlapOverride` absent for none; `isPresetIdOverride` absent in untouched-Auto previews | Encode must not force f3 = 1 for none |
| 2.1 preset: 1 = Fade, 3 = Rise | confirmed (3 is BassSwap, shown as Rise); all 22 known | confirmed, extended |
| 2.2 "StyleVersion", default 1 | preset `type`: 1 FADE or 3 BEATMATCHED_FADE by pair | **contradicted** as a version |
| (2.4 dropped) | `eqStyleOverride {1 id}`; 15 = Bass fade out | **missing** |
| (top-level 5 dropped) | `beatmatchPreference` [client] | missing |
| attribute 14 = "editable transition" flag | the `set-transition` signal template `{1 name, 2 {1 lens, 2 uri, 3 item id}}` | **contradicted**: echo it verbatim |
| not-mixable via attribute 14 | attribute 11 `not-mixable` + `not-mixable.reason` | corrected |
| field 22 `{1 name, 2 etag, 3 {…}}`, read as entry or holder | `22 {1[] {1 name, 2 etag, 3 {1 200}}}` (holder form) | confirmed |
| recipes only through the mix lens | yes; the `auto` lens alone applies mix when the list is mixed | confirmed |
| kind 217 beats | as §9 | confirmed |
| kind 28 `{1 ms, 2 bpm, 3 1, 4 double}`, candidates dropped | field 3 = the ml marker (absent = human); Auto needs the candidates | refined; keep the candidates |
| kinds 225 / 170 / 235 | as documented; 235 is a playlist trait, per-track mixability is 219 | confirmed, extended |
| (no 218 / 219 / 142 / 237 decode for Mix) | formulas in §9 | missing |
| `set-transition` `{1 new, 3 previous auto}` | `{1 new, 3 Auto}`; a user-defined previous goes in 4 [client] | confirmed for an Auto pair |
| `automix-preview` advertised (`Spotify/Spotify.Decode.PutState.cs:470`) | received from the official editor, dropped | **gap** |
| kind 10 duration | sint32; Wavee reads `ZigZag()` (`Spotify/Spotify.Decode.cs:640, 880, 1066`) | already correct |
| an inbound `play` ignores `skip_to` (suspected) | a list play carries its index in `playlist_play_request` | not a bug; the index is not decoded |
| the curve value mapping (`Entities/Mix.Curves.cs:180-193`) | the client's exact value semantics | **wrong** |

---

## 13. Open items and what would settle them

| # | Question | Status |
|---|---|---|
| 1 | Every style's id and curves; which chips exist per pair (2.2 = 1 against 3) | ids and curves **closed** (the client's tables); the extra chip on a beatmatched pair: **open** |
| 2 | How a length change moves 1.5, 1.16 / 1.17, 1.7; the 30 % cap | Auto: **closed** by the dataset (2 or 4 bars; `durationMs` = bars × 4 × 60000 / bpmA); an editor drag on the wire: not captured |
| 3 | The speed formula | partly: `bpmA / bpmB` in 38 of 73 beatmatched Auto pairs; the rest **open** (§4.4) |
| 4 | Reset to Auto | **closed** [client]: `set-transition` with no payload. Whether `automix.transition_uri` survives a reset: **open** |
| 5 | Curve editing and edit history on the wire | schema **closed** [client] (preset fields 2.6 to 2.17); never seen on the wire |
| 6 | What a playing mixed playlist publishes | **closed** (§6). Which kinds the player fetches before each seam: **open** |
| 7 | A mixed playlist played on a Connect remote | desktop → Wavee list play **closed** (§7). A phone as controller, the official desktop as receiver: **open** |
| 8 | `automix-preview` on an official receiver | the receiver's context and items **closed** [client] (§5); its PutState during a preview: not seen |
| 9 | A non-owned mixed playlist (followed, editorial "mixed", collaborative): attribute 14 and the `mix-state` template present? `can-view-transition`? is `set-transition` refused? | **open** (low priority); "Duplicate Mix" is closed [client] |
| 10 | Removing a track at an edited seam; moving the from-item; does `has-custom-transitions` flip | move: the recipe stays on its from-item (§3.4); remove: **open** |
| 11 | Beatmatching values | the fields are **closed** [client] (1.8, 1.9, top-level 5); top-level 5 never seen on the wire |
| 12 | Mixability edge cases: an episode or video in a mixed list (`not-mixable.reason` values), a track whose kind 28 answers 502 at play time (fallback `First`?), kind 219 field 2 | **open** |
| 13 | A minor-key track for kind 222's mode; a track with silence to confirm kind 237's band order | **open** |
| 14 | The sync-reasons and read-back of `mix-opt-out`, `mix-remove-opt-out`, `mix-auto-toggle-*`, `harmonize` (§3.3) | **open** |
| 15 | The published keys of noise, effect and looping lanes | **open** (none of the six played seams used one) |
| 16 | `supports_playlist_mixing` on the Wavee device in the morning cluster (§5) | **open** |
