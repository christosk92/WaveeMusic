# AI DJ: the protocol, as the official client speaks it

Status: draft, 2026-11-25. Target: the open release line after 0.4.0 (the evidence behind
[`dj-implementation.md`](dj-implementation.md)).

What this covers: how the official desktop client finds and resolves the DJ, the session and its pages, the narration
clips (text-to-speech), how narrations become queue items, the talk-over and the automix between tracks, "Let DJ pick",
skips, what a DJ player publishes over Connect, and the telemetry.

Sibling documents:
- [`dj-implementation.md`](dj-implementation.md): the plan (design, phases D0 to D6 and D3b).
- [`48-mix-protocol.md`](48-mix-protocol.md), [`48-mix-reference.md`](48-mix-reference.md),
  [`48-mix-full-support.md`](48-mix-full-support.md): playlist Mix, whose fade keys, curve format and voice machinery
  DJ shares.
- [`connect-commands-from-capture.md`](connect-commands-from-capture.md): the general Connect command wire.

Source: captures of the official desktop client 1.3.10, 2026-11-25; the raw material is kept outside the repo (it holds
account data). Two sessions: a **morning session** (about four minutes of DJ: a Connect start sent to Wavee, a local
start, four "Let DJ pick" jumps, skips) and an **afternoon session** (about sixteen minutes: two starts and one natural
segment boundary). Facts tagged [client] come from the official client's own logic or data, not from the wire.

Shapes are written by hand; ids, tokens and account data are replaced. The DJ playlist is Spotify's own
`spotify:playlist:37i9dQZF1EYkqdzj48dyYq`. A track is named by its Connect index `(page, track)`, page 1 being the
first real page. Placeholders: `XX-Segment-<n>` a segment id, `ssp~<36 hex>` / `dj~<36 hex>` decision ids,
`<32 hex>` a request id, `<spoken text>` the words of a narration, `<artist>` an artist name, `<segment label>` a
`station_subtitle` value.

Everything below is observed unless marked **(inference)**, **(unknown)** or [client].

---

## 0. The model in one screen

1. DJ is a playlist in the user's library whose attributes carry `lexicon_set_type=your_dj` and
   `lexicon_context_url=hm://lexicon-session-provider/context-resolve/v2/session?contextUri=<playlist uri>` (in the
   rootlist and in the playlist's `diff`). The client plays that playlist with the context URL taken from the attribute.
2. The context resolves over HTTPS: `GET /lexicon-session-provider/context-resolve/v2/session?contextUri=…&reason=interactive`
   returns the session: context metadata, the **welcome** narration, restrictions, and one empty page whose
   `next_page_url` points at the first real page.
3. Each page is one **segment** of 5 tracks. The first track carries the narration before the segment
   (`narration.intro.*`; from page 2 on also `narration.jump.*`, the variant played after "Let DJ pick"); the fifth
   track carries `narration.outro.*`.
4. The player turns narrations into **queue items** of their own: `spotify:media:<22 zeros>` for the welcome,
   `spotify:media:<track id>` for intro, jump and outro; provider `narration/welcome|intro|jump|outro`; metadata
   `is_narration=true` plus a `media.manifest` (base64 protobuf holding the `client-tts/v1/fulfill` request).
5. Audio: `POST /client-tts/v1/fulfill` (protobuf body: SSML, voice, codec, rate) → `303` to
   `tts.spotifycdn.com/v2/generate/sonantic_large/mp3/44100/x-dj-speechcraft-rayserve/0/<base64url SSML>?__token__=…&fauth=<JWT>`
   → `audio/mpeg`.
6. **Talk-over:** the narration item gets `audio.fade_out_start_time/duration` with a flat curve (the voice is never
   faded) and the following track gets `audio.fade_in_*` with the music at 0.25 until its start cue (kind 28). For an
   **ml** cue the overlap is a fixed 3400 ms with the track from position 0; for a **human** cue the narration plays over
   the track's intro and ends 1600 ms before the cue. At a natural segment end the **outro** talks over the last track's
   ducked tail (§1.4).
7. **"Let DJ pick"** (`dj_jump_button`) is local on the playing device: no Connect command and no lexicon call except
   the pre-planned fetch of the next page; the player skips to the next `narration/jump` item. On a remote DJ player it
   is the Connect signal `jump` [client].
8. **Between tracks** the DJ crossfades ("radio" automix): each fade-out ends on the track's end cue, each fade-in on
   the next track's start cue (§7).

---

## 1. The morning session

### 1.1 Before DJ

| Event | Detail |
|---|---|
| Rootlist (`playlist/v2/user/<user>/rootlist`) | lists the DJ playlist with the format attributes `lexicon_set_type=your_dj`, `home.card.*`, `lexicon_context_url`, `automix.mode=radio`, `add_context_load_reason=true`, images `DJ-Beta-CoverArt-{64,300,640,1280}.jpg` |
| Home GraphQL (`pathfinder/v2/query`) | carries the same `home.card.*` keys for the Home DJ card |
| Desktop → Wavee | `update_context` / `play` commands for an artist context (not DJ) |
| Desktop → Wavee | `pause` / `resume` and `signal` `automix-preview` (the Mix editor, [`48-mix-protocol.md`](48-mix-protocol.md) §5) |

### 1.2 The DJ session

Times are seconds from the **local** start (S, the second click on the DJ item).

| Time | Who → what | Detail |
|---|---|---|
| S − 9.35 | UI | `play` on the Your Library DJ item (play_button). Wavee is the active device |
| S − 9.32 | desktop → lexicon | `GET …/session?contextUri=…&reason=interactive`; aborted, no response |
| S − 9.32 | desktop → **Wavee** `player/command` | `endpoint: play`, `context.uri = entity_uri = spotify:playlist:37i9dQZF1EYkqdzj48dyYq`, `context.url = hm://lexicon-session-provider/context-resolve/v2/session?contextUri=…`, metadata = the rootlist attributes + `context_description=DJ`, `initiated_from_list_play_esperanto=true`, `playlist_volatile_context_id`; one empty page; all restriction lists empty; `play_origin.feature_identifier=playlist`, `referrer_identifier=your_library`; `prepare_play_options.player_options_override.modes.context_enhancement=NONE`, `session_id=<22 base62>`, `license=premium`; `play_options.reason=interactive`, `operation=replace`. Ack `{"ack_id":…}` |
| S − 2.92 | UI + desktop | device picker opened; PutState reason 6 (PICKER_OPENED) and `connect-state/v1/cluster/wake-devices` |
| S − 2.77 | cluster | the active device is still Wavee and its player state is still on the **artist** context: Wavee received the DJ `play` and did not switch |
| S − 2.00 | UI + desktop | `pull_playback_to_local_device`; the desktop becomes active, playing the artist context it pulled |
| S − 0.09 | desktop → playlist | `GET playlist/v2/playlist/37i9…/diff?revision=0,…` (attributes only, no items) |
| S | UI | `play` on the DJ item again, now local. No Connect command is sent for a local play |
| S + 0.01 | desktop → lexicon | `GET /lexicon-session-provider/context-resolve/v2/session?contextUri=spotify:playlist:37i9dQZF1EYkqdzj48dyYq&reason=interactive` → 200, zstd JSON (no content-type), 80 ms. Metadata keys: §2.2 |
| S + 0.10 | desktop → client-tts | fulfil the **welcome**: `<speak xml:lang="en-US"><spoken text></speak>` → 303 |
| S + 0.11 | lexicon page 1 | `…/session/00000000000000000000000000000000?contextUri=…&opmPlayed=false&requestId=<32 hex>` → segment `XX-Segment-<n1>` |
| S + 0.17 | TTS CDN | welcome MP3, `audio/mpeg`, 135 835 B, `X-Cache: HIT, HIT`, Age 39 577 s |
| S + 0.26 | PutState | current `spotify:media:0000000000000000000000`, provider `narration/welcome`, duration 3395, playing |
| S + 0.83 | lexicon page 2 | `previousSegmentId=XX-Segment-<n1>&requestId=<32 hex>` → `XX-Segment-<n2>` |
| S + 1.22 | lexicon page 3 | `previousSegmentId=XX-Segment-<n2>&requestId=<32 hex>` → `XX-Segment-<n3>`. Fetched back to back: the start loads the session + 3 pages |
| S + 1.88 | PutState | next_tracks = `[intro narration for (1,0), (1,0), (1,1), (1,2), (1,3), (1,4), jump narration for (2,0), (2,0), …]` (82 entries) |
| S + 2.98 | PutState | the welcome at 2423 ms, buffering |
| S + 3.66 | client-tts | fulfil the page-1 **intro** (the long onboarding monologue; 629-byte body) |
| S + 3.70 | TTS CDN | intro MP3: `X-Cache: MISS, MISS`, no Content-Length, streamed for 5.3 s (synthesis) |
| S + 3.97 | PutState | current: the intro narration (`spotify:media:<id of (1,0)>`, `narration/intro`), duration unknown, buffering. The welcome ran its full 3396 ms, reasons `playbtn` → `trackdone` (telemetry) |
| S + 4.65 | PutState | the intro playing (not buffering) |
| S + 9.96 | client-tts + CDN | prefetch the page-2 **jump** narration, 194 350 B, HIT |
| S + 28.79 | PutState | the intro at 23 630 of 24 555 ms; only now does it carry `audio.fade_out_start_time=17223`, `fade_out_duration=7332` |
| S + 29.74 | PutState | track (1,0), `dj.jump_target=true`. Intro telemetry: 24 555 ms in full, `trackdone`. **The intro played to its end before the track** (§1.3) |
| S + 33.72 | UI | skip_next (the UI reports the item as `spotify:media:000…`: stale) |
| S + 33.88 | PutState | (1,1) |
| S + 34.87 | UI | skip_next on (1,1) at 1025 ms |
| S + 35.07 | PutState | (1,2). next_tracks now `[(1,3), (1,4), outro narration for (1,4), jump narration for (2,0), (2,0), …]`: **the outro is inserted** once the segment's last track is 2 slots ahead |
| S + 35.97 | UI | **`refresh_content` on `desktop-npb-extra / dj_jump_button`** ("Let DJ pick") |
| S + 35.99 | lexicon page 4 | `previousSegmentId=XX-Segment-<n3>&requestId=<32 hex>` (20 ms after the button) |
| S + 36.44 | PutState | current: the jump narration for (2,0), 4858 ms, `fade_out_start_time=1458`. prev_tracks = (1,0) to (1,4) (including the jumped-over tracks); no outro played |
| S + 36.87 | PutState | track (2,0) at position 0–20 ms, 0.43 s after the narration's PutState. Telemetry: the narration 4858 ms in full, overlap 3400 on both items. Talk-over. **(unknown):** the fade fields put the track's start 1458 ms into the narration; the 0.43 s gap between the two PutState messages does not match |
| S + 41.73 | client-tts + CDN | prefetch the page-3 jump narration, 148 374 B |
| S + 41.80 | UI | skip_next on (2,0) at 4929 ms → (2,1) |
| S + 43.92 | UI | `dj_jump_button` |
| S + 43.95 | lexicon page 5 | the same pattern |
| S + 44.66 | PutState | the jump narration for (3,0), 3709 ms, `fade_out_start_time=309` |
| S + 44.80 | PutState | track (3,0) starts about 0.2 s after the narration |
| S + 45.48 | UI | skip_next (the UI names the narration's `spotify:media:` uri, position 0). Telemetry: the narration ended at 923 ms, track (3,0) at 0 ms with `fwdbtn`, (3,1) started. **One skip during a talk-over ends the narration and its target track** |
| S + 45.75 | PutState | (3,1) |
| S + 50.55 | client-tts + CDN | prefetch the page-4 jump narration, 208 978 B |
| S + 83 | PutState | re-sends while the explicit-content filter was toggled (no DJ change) |
| S + 209.28 | PutState | a natural automix advance (3,1) → (3,2), the new track starting at position 10 948 (telemetry overlap 4473; the published `fade_overlap` was 4562, §7). The outro for (3,4) is inserted |
| S + 214.70 | client-tts + CDN | prefetch the page-3 **outro**, 84 635 B. Never played (a jump came first) |
| S + 220.92 | UI | `dj_jump_button` |
| S + 221.00 | lexicon page 6 | the same pattern |
| S + 224.15 | PutState | the jump narration for (4,0), 5224 ms, position 230, `fade_out_start_time=1824` (this PutState was sent about 1.8 s later) |
| S + 225.77 | PutState | track (4,0) at position 3, 1.6 s after the narration started (5224 − 3400 = 1824). Later an odd re-sync to position 3120 |
| S + 234.94 | client-tts + CDN | prefetch the page-5 jump narration, 212 113 B |
| S + 244.61 | lexicon page 7 | the pattern of a jump; the matching UI event was not in the capture **(inference: the DJ button)** |
| S + 245.74 | PutState | the jump narration for (5,0), 5302 ms, `fade_out_start_time=0` |
| S + 246.11 | PutState | track (5,0) at **position 4919** (`fade_in_start_time=4913`, a human cue at 11 815): the narration over the track's intro |
| S + 251.24 | PutState | an artist context, `last_command_sent_by_device_id` = Wavee: Wavee sent a play to the desktop, which ended DJ. The command itself is not visible in the capture |

### 1.3 When each narration plays

| Slot | Queue position | Observed timing |
|---|---|---|
| welcome | the first item after `play` | the first narration of **every start (3 of 3: one morning, two afternoon)**; sequential, then the intro. Durations 3395, 1855 and 1985 ms |
| intro | directly before the segment's first track (`dj.jump_target`) | page 1 (morning): played in full (24 555 ms), then the track. Its overlap window (17223 / 7332) appeared in the state only 1 s before its end; the clip was a streamed cache MISS with an unknown duration when it started **(inference: that is why there was no talk-over)**. Afternoon, after the welcome and at the natural boundary: talk-over by the ml rule (§1.4) |
| jump | after the current segment's last track (and after its outro); played when the DJ button is pressed, which skips the rest of the segment | talk-over: the track starts at the narration start + `fade_out_start_time` |
| outro | after the segment's 5th track, inserted when that track is 2 slots ahead | morning: never played (every boundary was crossed by a jump). Afternoon: talks over the last track's ducked tail (§1.4) |

- At a natural boundary the **intro** plays, not the jump variant queued there (afternoon, 1 of 1). How next_tracks
  changes from the jump item to the intro is **(unknown)**.
- Intro and jump wording differ (an intro announces what comes next; a jump acknowledges the change of direction).

Talk-over parameters (all from PutState):

| Narration | Duration | Narration `fade_out_start_time` / `_duration` | Next track `fade_in_start_time` / `fade_in_duration` / `fade_overlap` | Cue (kind 28) |
|---|---|---|---|---|
| intro, page 1 | 24 555 | 17 223 / 7 332 | 0 / 8 932 / 7 332 | 8 932 human |
| jump, page 2 | 4 858 | 1 458 / 3 400 | 0 / 5 000 / 3 400 | 13 250 ml |
| jump, page 3 | 3 709 | 309 / 3 400 | 0 / 5 000 / 3 400 | 9 030 ml |
| jump, page 4 | 5 224 | 1 824 / 3 400 | 0 / 5 000 / 3 400 | 3 870 ml |
| jump, page 5 | 5 302 | 0 / 5 302 | 4 913 / 6 902 / 5 302 | 11 815 human |
| intro at the afternoon boundary | 4 258 | (track starts 858 ms in) | 0 / 5 000 / 3 400 | ml |

Rules (fit all six rows):
- **ml cue:** overlap = min(3400, narration); the track starts at position 0, `narration − 3400` ms after the narration
  starts; music gain 0.25 for the first 60 % of a 5000 ms fade-in, then up to 1.0.
- **human cue:** the voice must end 1600 ms before the cue: `fade_in_start_time = max(0, cue − narration − 1600)`,
  `fade_overlap = min(narration, cue − 1600)`; the gain ramps 0 → 0.25, holds 0.25, then reaches 1.0 by the cue.
  Checks: 8932 − 1600 = 7332 ✓; 11815 − 5302 − 1600 = 4913 ✓.
- `automix.talk_mode=human_cuepoints_or_full_track` **(inference)**: trust human cue points for talk-over; otherwise
  overlap only a short fixed window, with the track from its start.
- The narration's own `audio.fade_out_curves` is flat (y 1 → 1): the voice is never faded, only the music is ducked.
- **When the target becomes current:** after a **jump**, as soon as it starts (4 of 4, morning; the PutState gaps after
  the jump clip's own PutState were 0.14 to 0.45 s in three cases and 1.6 s in one). After an **intro or welcome**
  (3 of 3, afternoon), the narration stays the current item until it ends. When the target starts under it, one
  PutState moves the target out of `next_tracks` and lifts the `narration` restrictions, with the narration still
  current; at the narration's end the track becomes current at position ≈ 3400 + 50 (3449, 3457, 3427).

### 1.4 The natural segment boundary (afternoon, 1 of 1)

| Step | What the player does |
|---|---|
| 1 | The segment's last track has a fade-out window that runs **to the track's end**: `fade_out_start_time` 149586, `fade_out_duration` 5507, track 155093 ms. The window was already published about 2.5 min earlier (with the track as `next_tracks[0]`), before the outro was fetched |
| 2 | Inside it the music **ducks to 0.25 and stays there; it never fades to 0**. `audio.fade_out_curves` = `[{0–0.726348: (0,1)(.5,1)(.5,.25)(1,.25)}, {0.726348–1: flat 0.25}]`. 0.726348 × 5507 = **4000 ms** for the duck |
| 3 | About when the window starts, the client fulfils the outro |
| 4 | The **outro starts 4000 ms after the window starts**, when the duck has landed (track position 153586; the PutState with the outro as current is stamped 44 ms later). It talks over the track's last 1507 ms at 0.25, then runs on alone (outro 2377 ms) |
| 5 | The next segment's intro is fulfilled about 1.4 s before it plays |
| 6 | The **intro starts when the outro ends** (within about 0.1 s; a 350 ms buffering stall of the outro sits in between) |
| 7 | The next segment's first track starts **`d − 3400` ms after the intro starts** (intro `d` = 4258 → 858 ms; the PutState is 919 ms in) under the ml duck: `fade_in_duration` 5000, `fade_overlap` 3400, the same curve JSON as a jump |
| 8 | The track becomes the current item **when the intro ends**, at position 3457 |

- No lexicon call happens at the boundary: the page was fetched earlier. What triggers that fetch in natural playback is
  **(unknown)**.
- The outro window's start (5507 ms before the end; the track's end cue was 710 ms before the end) has no rule yet
  (one sample).

### 1.5 TTS prefetch

- **(inference, fits 4 of 5 in the morning):** about 5 s after an item starts, the client fulfils every narration in
  next_tracks not yet cached, up to the next `narration/jump` item (after the intro +5.0 s → the page-2 jump; after
  (2,0) +5.06 s; after (3,1) +5.04 s; after (3,2) +5.4 s → the outro; the fifth came +9 s after a position re-sync).
- The welcome and the page-1 intro are fulfilled immediately.
- Intro variants of pages 2 and later were never fulfilled in the morning: only the jump variant is queued at a boundary.
- **(unknown):** in the afternoon the outro was fulfilled only about 4 s before it played and the intro about 1.4 s
  before (§1.4), not 5 s after their tracks started. The rule does not cover narrations at a natural boundary.

---

## 2. Schemas

### 2.1 Endpoints

| Call | Form |
|---|---|
| Resolve the session | `GET https://<spclient>/lexicon-session-provider/context-resolve/v2/session?contextUri=<uri>&reason=interactive`: the `hm://` context URL with `&reason=interactive` appended. The response's own `url` echoes it; PutState keeps the URL without `reason` |
| Next page | `GET https://<spclient>/lexicon-session-provider/context-resolve/v2/session/00000000000000000000000000000000?contextUri=<uri>&…`: the `next_page_url` with `hm://` → spclient, verbatim. The first page `&opmPlayed=false&requestId=<32 hex>`, later ones `&previousSegmentId=<lastComponentId>&requestId=<32 hex>`. **The server writes `requestId` and `previousSegmentId`; the client never builds them** |
| Headers on both | `App-Platform`, `Spotify-App-Version`, `Time-Zone: <IANA zone>`, `X-Client-Id`, `client-token`, `Authorization`, `x-dynamic-device-context` = base64 `{1 {1 "spotify", 2 "<device model>", 3 "computer", 4 {1 <audio output name>, 4 3, 5 0}}}` (page 1 sent it without field 4) |
| TTS | `POST https://<spclient>/client-tts/v1/fulfill`, `Content-Type: application/x-www-form-urlencoded` (wrong: the body is protobuf), `Accept-Encoding: identity`, `Authorization` → `303` with `Location:` |
| Audio | `GET https://tts.spotifycdn.com/v2/generate/sonantic_large/mp3/44100/x-dj-speechcraft-rayserve/0/<base64url(SSML) with = padding>?__token__=exp=<unix>~hmac=<64 hex>&fauth=<JWT>`; the official client also sends its `Authorization` and `client-token` headers here (not needed: the URL is signed) |

- `fauth` JWT: header `{"kid":"<key id>","alg":"RS256"}`, payload
  `{"iss":"scdn-url-signer","exp":nbf+300,"nbf":…,"path":"/v2/generate/…/<b64>"}`; `__token__`'s `exp` is the same.
  **The signed URL expires after 300 s.**
- CDN response: `audio/mpeg`, `Cache-Control: public, max-age=604800`, `x-audio-source: ray-serve-v2-generate`,
  `X-Cache HIT/MISS`; sizes 84 635 to 212 113 B; 135 835 B / 3.395 s ≈ 320 kbit/s MP3 (telemetry labels it "MP3 160
  kbps"). A cache MISS streams without Content-Length while it synthesises (the 24.5 s intro took 5.3 s). The capture
  did not keep the MP3 bodies.

`client-tts/v1/fulfill` body, identical to field 1.1.4 of `media.manifest`:

```
2: string  ssml            ("<speak xml:lang=\"en-US\">…</speak>")
3: varint  5               (inference: tts_provider SONANTIC_FAST → path "sonantic_large")
5: varint  1               (inference: voice VOICE1)
6: varint  6               (inference: codec MP3 → path "mp3")
7: varint  44100           (sample_rate → path "44100")
```

### 2.2 The session response (context level)

Top-level keys: `uri`, `url`, `metadata`, `pages[1]` (`{next_page_url, tracks: []}`), `restrictions`.

| Key | Example | Meaning |
|---|---|---|
| `lexicon_set_type` | `your_dj` | the DJ product marker |
| `agentic_product_type` | `dj` | also on every track and narration item |
| `context_description` | `DJ` | |
| `automix.mode` | `radio` | automix crossfades between tracks (§7) |
| `automix.talk_mode` | `human_cuepoints_or_full_track` | the talk-over policy (§1.3) |
| `shuffle.algorithm` | `none` | |
| `autoplay_candidate` | `false` | no autoplay after the context |
| `end_of_context_action` | `stop` | |
| `add_context_load_reason` | `true` | **(inference)** why the client appends `reason=` |
| `lexicon_current_time` / `lexicon_expiration_time` | `<ISO time>` / `<ISO time>` | session validity; the expiry is the next local midnight |
| `dj.interactivity_enabled` | `true` | the request feature is on |
| `dj.interactivity.localized_cta` | `Your DJ X here. What can I play for you?` | the prompt text |
| `dj.interactivity.localized_jump_button` | `Let DJ pick` | the DJ button's label |
| `narration.background.color` | `#FF0A59B8` | ARGB UI colours |
| `narration.jellyfish.{top,mid,bottom}.color` | `#FF19E68C` / `#FF1ED7BC` / `#FF1E95D7` | the DJ "jellyfish" animation |
| `narration.jump.{background,border,icon}.color` | `#FF0A59B8` / `#FF0A59B8` / `#FF19E68C` | the DJ button |
| `narration.welcome.*` | see §2.3 | the welcome narration (context level only) |

`restrictions`: `disallow_toggling_repeat_context_reasons`, `disallow_toggling_repeat_track_reasons`,
`disallow_toggling_shuffle_reasons` = `["dj-disallow"]`.

**Is it a DJ context? [client]** The client treats a context as DJ when its uri is the DJ list (or one of a small known
set) **or** its metadata says `lexicon_set_type == "your_dj"`. A narration item has `metadata.is_narration == "true"`.
A Home or sidebar start builds `{uri: <DJ uri>, url: <the list's lexicon_context_url attribute>}`.

### 2.3 The page response

Top level: `next_page_url`, `tracks[5]`, `metadata`. No `page_url`.

Page `metadata`: `correlation-id` (`ssp~<36 hex>`, equal to every track's `decision_id`), `lastComponentId` (the segment
id, which becomes `previousSegmentId` in the next URL), `automix.mode=radio`, and the seven colour keys.

Track `metadata` (all tracks):

| Key | Example | Meaning |
|---|---|---|
| `segment` | `XX-Segment-<n>` | the segment id; one per page |
| `source.components` | `YourDJ,XX-Segment-<n>` | |
| `decision_id` | `ssp~<36 hex>` | the page's recommendation decision |
| `custom_reporting_attribution` | `XX-Segment-<n>:0:1:0` | `<segment>:<page ordinal from 0>:<slot>:0`; slot 0 = the intro / jump narration, 1 to 5 = the tracks, 6 = the outro |
| `station_title` | `DJ` | |
| `station_subtitle` | `<segment label>` | the segment's theme label, one per segment |
| `station_uri` | `""` | |
| `album_image_small` / `album_image_large` | `https://i-lo.scdn.co/image/<id>` | |
| `agentic_product_type` | `dj` | |
| `dj.jump_target` | `true` (first track only) | where "Let DJ pick" lands |
| `automix.talk_mode` | `human_cuepoints_or_full_track` (first and last track only) | the tracks next to a narration |

Narration slot keys, prefix `narration.<slot>.`, slot ∈ {`welcome` (context), `intro`, `jump` (first track), `outro`
(last track)}:

| Suffix | Example |
|---|---|
| `ssml` | `<speak xml:lang="en-US"><spoken text> <entity type="artist" uri="<base62 artist id>"><artist></entity> <spoken text></speak>`; `uri` is a bare base62 id and may be absent (`<entity type="artist"><artist></entity>`) |
| `title` | `Welcome` (welcome), `Up next` (intro, jump, outro) |
| `artist` | `DJ X` |
| `album_title` | `DJ` |
| `image` | `https://lexicon-assets.spotifycdn.com/Your-DJ-Cover-Art-300.png` |
| `voice` | `VOICE1` |
| `tts_provider` | `SONANTIC_FAST` |
| `sample_rate` | `44100` |
| `loudness` / `true_peak` | `-16.0` / `-3.0` (also in `media.manifest` field 1.5 as float32) |
| `commentary_type` | `NARRATION`, `LISTENER_STORY_TRACK_DISCOVERY_MORE_FAMILIAR`, `LISTENER_STORY_TOP_4_ARTIST` |
| `commentary_id` | `""` for NARRATION, a `<uuid>` for listener stories |
| `decision_id` | `dj~<36 hex>`, distinct per slot |
| `custom_reporting_attribution` | intro and jump share `…:<p>:0:0`, the outro `…:<p>:6:0` (the welcome has none) |

- Distinct slots: welcome, intro, jump, outro. Distinct commentary types: NARRATION,
  LISTENER_STORY_TRACK_DISCOVERY_MORE_FAMILIAR, LISTENER_STORY_TOP_4_ARTIST.
- Page 1 has an intro only (no jump); pages 2 to 7 carry both an intro and a jump on the first track.
- `opmPlayed=false` on the first page request coincides with the page-1 intro being the onboarding monologue (it
  explains the DJ button) **(inference: an "opening monologue played" flag; where the client keeps it is unknown)**.

### 2.4 The narration queue item (as the player builds it)

- `uri`: `spotify:media:0000000000000000000000` (welcome) or `spotify:media:<base62 id of the associated track>`
  (intro and jump: the jump target; outro: the segment's last track).
- `uid`: the welcome's is `list_intro_item` (afternoon, 2 of 2); the other narration items' are 32 hex, random per item
  (morning). Whether the morning welcome also had `list_intro_item` is **(unknown)**.
- `provider`: `narration/welcome|intro|jump|outro`. No ContextIndex.
- metadata: `title`, `artist_name`, `album_artist_name` (`DJ X`), `album_title` (`DJ`), `image_url`,
  `image_small_url`, `image_large_url`, `image_xlarge_url`, `is_narration=true`, `has_ephemeral_uri=true`,
  `hidden_in_queue=true`, `track_player=audio`, `decision_id`, `custom_reporting_attribution`,
  `agentic_product_type=dj`, `interaction_id`, `page_instance_id`, `actions.skipping_next_past_track=resume`,
  `actions.skipping_prev_past_track=resume`, `automix.mode=radio`, `audio.fade_gapless=1` (welcome, intro),
  `audio.fade_out_{start_time,duration,curves}` and `audio.only_allow_fade_on_advance=true` (talk-over),
  `suppress_progress=true` (afternoon), and `media.manifest`:

```
1 {                                   # media.manifest (base64)
  1 { 1 "POST"  3 "client-tts/v1/fulfill"  4 {the fulfil body, §2.1}  5 1 }
  5 { 1 f32 -16.0 (loudness)  2 f32 -3.0 (true_peak) }
}
```

- Extended metadata for `spotify:media:*`: kind 239 → 200 (93 B); kinds 249, 220, 178, 246, 179 → 404.

---

## 3. Jump and skip

- Three DJ-button presses are logged by the client's UI analytics (`UbiProd1Interaction`, `action_name=refresh_content`,
  `element_path_names=["desktop-npb-extra","dj_jump_button"]`, `element_path_ids=["desktop",""]`,
  `parent_path_names=["desktop-npb"]`, no action parameters). A fourth jump shows the same network pattern.
- What the client sends on a jump: **nothing jump-specific**. The only request is the next lexicon page, 20 to 90 ms
  after the press, from the stored `next_page_url`. No `player/command` (the play is local) and no TTS request (the jump
  MP3 was prefetched, §1.5).
- Effect: the player skips the rest of the segment (and its outro), plays the next `narration/jump`, then the jump
  target with talk-over. The skipped tracks go to prev_tracks. Telemetry marks the narration's start reason `fwdbtn` and
  the interrupted track's end reason `fwdbtn`.
- **On a remote DJ player [client]:** "Let DJ pick" is the Connect `signal` command `jump`
  (`{"endpoint":"signal","signal_id":"jump"}`), in the same player-signal list as `automix-preview` and
  `stop-automix-preview`.
- Page lookahead: the start loads the session + 3 pages; after that one page per jump, so the loaded pages stay at the
  current one + 2 (7 page fetches: 3 at the start, 4 at jumps).
- Skip next (`skip_next_button`, `skip_to_next` with `item_to_be_skipped`, `position_ms`, `total_content_ms`) behaves
  like a normal skip, but during a talk-over one skip ends both the narration and its already started target track.
- The skip events name the wrong item when a narration was involved (`spotify:media:000…` while track (1,0) played).
- Skipped tracks go to `prev_tracks`; narration items never do.

---

## 4. DJ in Connect: what a DJ player publishes

From the desktop's own PutState messages (`PUT connect-state/v1/devices/<device id>`, reason 4 PLAYER_STATE_CHANGED):

| PlayerState field | DJ value |
|---|---|
| 2 context_uri | `spotify:playlist:37i9dQZF1EYkqdzj48dyYq` |
| 3 context_url | `hm://lexicon-session-provider/context-resolve/v2/session?contextUri=spotify:playlist:37i9…` (no `reason`) |
| 4 context_restrictions | 8, 9, 10 = `dj-disallow` (repeat context, repeat track, shuffle) |
| 5 play_origin | `feature_identifier=playlist`, `feature_version=<client build>`, `referrer_identifier=your_library` |
| 6 index | tracks: (page, track) with page 1 = the first real page (page 0 is the empty session page); narration items: none |
| 7 track | the narration item (§2.4) or a context track; provider `context` for tracks |
| 11 duration | the narration MP3's length in ms (3395, 4858, …); unset while the TTS still streams. For tracks, duration = `fade_out_start_time` (§7) |
| 16 options | `modes`: `context_enhancement=NONE`, `jam=off`, `media=""`; shuffle and repeat false |
| 17 restrictions | 8, 9, 10 `dj-disallow`; **while a narration is current and its target has not started, also 3 `narration` (disallow seeking), 9 `narration` (repeat track) and 13 `narration` (transfer)**; plus the ordinary 2 `not_paused`, 6 `no_prev_track` (first item only), 25 `not_supported_by_content_type`, 31 `already_set`. The morning session showed only 9 and 13 for narrations, and `endless_context` on 8 and 10 |
| 19 prev_tracks | context tracks only (narrations never), including the tracks skipped by a jump |
| 20 next_tracks | context tracks interleaved with the upcoming narration items: intro (at the start), outro (once 2 ahead), jump (after the segment); only the nearest boundary gets an item |
| 21 context_metadata | session metadata + command / rootlist metadata (`home.card.*`, `lexicon_context_url`, `initiated_from_list_play_esperanto`, `playlist_volatile_context_id`) + player keys `player.arch=2`, `mixer_enabled=true`, `enhanced_context=false`, `context_owner=spotify`, `image_url=""`, `player_filter_info=""`, `playlist_number_of_tracks=0`, `playlist_number_of_episodes=0` |
| 22 page_metadata | the current page's `metadata` (empty on the session page or a narration) |
| 23 session_id | `<22 base62>`; `playback_id` (8) is new per item |
| 33 signals | `speed-preview`, `automix-preview`, `stop-automix-preview`, `stop-speed-preview`, `list-next-page`, and, once the session is loaded, **`prompt`** and **`jump`** (`switch-to-video` appears too). The morning session listed `interact` instead of `prompt`. `prompt` is most likely the DJ request as a Connect signal; its payload is **(unknown)** |

- Context-track metadata in PutState = the page metadata (§2.3, including the `narration.intro/jump/outro.*` keys) plus
  the player's `title`, `album_title`, `album_uri`, `artist_uri`, `context_uri`, `entity_uri`, `image_*_url`,
  `interaction_id`, `page_instance_id`, `iteration=0`, `view_index`, `hidden_in_queue=true`, `track_player=audio`,
  `actions.skipping_{next,prev}_past_track=resume`, `audio.automix_mode`, `audio.fade_*` and
  `automix.fade_{in,out}_cuepoint.{position,origin,tempo}`.
- Narration items also carry `suppress_progress=true` (afternoon). **[client]** The seek bar hides time and duration
  while `is_narration` is true, and the device picker shows "DJ segment will end soon" during a narration.
- **The receiver of a remote `play`:** the command carries only `context.url` and an empty page; the receiver must
  resolve the URL itself (§2.1) and add `&reason=interactive` **(inference: from `play_options.reason`)**. Wavee acked
  and stayed on its old context. Which of the keys above official controllers *need* to draw the DJ UI is **(unknown)**;
  the safe answer is to echo them all.
- **Narration support [client]:** a remote device without the DJ-narration capability (`supportsDJNarration` in the
  client; its Connect wire name is **(unknown)**) makes the desktop show "In beta, the DJ's voice is unavailable on this
  speaker".
- Commands arriving at the official desktop are not visible in these captures, only their effects.

---

## 5. Telemetry

- **gabo JSON** (the UI layer, `gabo-receiver-service/v3/events`): `UbiProd1Interaction` for the DJ button, skips, play
  and the picker (§3). No DJ-specific event name; DJ shows only through `play_context_uri`. `UbiProd1Impression*`
  events did not include DJ elements.
- **gabo protobuf** (the core, `spclient.wg…/gabo-receiver-service/v3/events/`): `RawCoreStream` and
  `RawCoreStreamSegment` for every narration and DJ track. A narration example: uri `spotify:media:<id>`, provider
  `narration/jump`, start reason `fwdbtn`, end reason `trackdone`, ms played 4858, overlap 3400 (fields 16 and 67),
  format `MP3 160 kbps`, the context uri, `XX-Segment-<n>:1:0:0`, a `dj~<36 hex>` decision id, `dj` (agentic product
  type), feature `context-player`, referrer `your_library`. Tracks carry `ssp~` decision ids and their attribution. The
  welcome starts with `playbtn`.
- `Prefetch` / `Download` events reference `spotify:media:<id>` with a 20-byte file id when narration audio is cached.
- **melody `msg/batch`**: only `jssdk_playback_*` canvas-video stats; nothing DJ-specific.
- Which events the backend needs for DJ personalisation (to count narration plays or set `opmPlayed`) is **(unknown)**;
  `RawCoreStream` with provider, decision id and attribution is the obvious candidate.

---

## 6. Extended metadata for DJ tracks

- In DJ the player uses `client-feature-id: context_player`, with **one request per kind**: 28
  `spotify.automix.proto.Cuepoints`, 6 `spotify.descriptorextension.ExtensionDescriptorData` (genre and mood tags with
  concept URIs), 142 `spotify.playlist.tuner.extension.ListTunerAudioAnalysis` (`{2: 20, 3: bytes}`, an energy curve).
- Kinds 217, 218, 219 and `client-feature-id: automix_esperanto` were **not** used for DJ; they appear only for the
  user's own mixed playlists ([`48-mix-protocol.md`](48-mix-protocol.md) §10).
- `Cuepoints`: 1 start cue `{1 position_ms, 2 tempo f32, 3 origin (absent = human, 1 = ml), 4 confidence f64}`, 2 end
  cue, 3 start candidates, 4 end candidates. Examples: 8932 ms human with confidence 1.0; 13 250 ms ml with 0.88; 11 815
  ms human. The start cue is exactly `automix.fade_in_cuepoint.position` / `origin` in PutState, and the talk-over is
  computed from it (§1.3).
- Timing: cue points are fetched as tracks enter the queue (pages 1 and 2 right after the start); for later jump targets
  just before they become the next jump. Kind 142 is fetched when a track starts.
- The talk-over appears to use only kind 28 (cue position and origin) and the narration's duration. No vocal-activity
  data (218) is requested.

---

## 7. Automix between DJ tracks ("radio")

The DJ does not butt-join its tracks. Every track carries a fade-out window and every next track a fade-in, published
with the playlist-Mix keys ([`48-mix-protocol.md`](48-mix-protocol.md) §6.3) plus `automix.fade_*_cuepoint.origin`
(`ml` or `human`) and `.tempo` (the cue's bpm) filled in, and `audio.automix_mode=radio`, `automix.mode=radio`.

Afternoon session, five pairs, cues from kind 28:

| Rule | Evidence |
|---|---|
| **A's fade-out ends on A's end cue** (kind 28 field 2) within 40 ms (beat-snapped). Length 3.0 to 5.4 s; the length rule is **open** | 164441 against cue 164401 · 151634 / 151630 · 189690 / 189730 · 122789 / 122786 |
| A's curve: `(0,1)(.2,1)(1,0)`, one quadratic segment | every pair |
| f11 duration = `fade_out_start_time`; the position keeps counting past it while A fades | position 119582 with duration 118607 |
| **B with a confident start cue:** its fade-in ends on that cue; it starts at `cue − overlap` when A's fade-out starts (`fade_overlap` = `fade_in_duration` = A's fade-out length) | cue 5570 (confidence 0.90): in 709 + 4861 · cue 8370 (0.88): in 5472 + 2898 |
| **B without one:** from 0, a 350 ms overlap, started 350 ms before A's fade-out ends. A human cue under the overlap gives its own position as the overlap (260 ms) | cues of confidence 0.21 and 0.39: 0 + 350 · a human cue at 260: 0 + 260 |
| B's curve: `(0,0)(.9,0)(.9,1)(1,1)`: silent for 90 % of the window, then up. B is pre-rolled so that its cue lands where A's fade ends | every pair |
| B becomes current when it starts playing | 4 PutState messages |

One pair fits neither B rule (in 9932 + 3638 = 13570; its start cue is 32009 at confidence 0.29): 13570 is probably
another start candidate. The candidate choice is **open**.

The morning session's 15 DJ tracks show the same model:

| Key | Relation |
|---|---|
| `automix.fade_in_cuepoint.position` | = a kind 28 start candidate (usually field 1; not always: 20650 against field 1 = 62050, 7790 against 11430) |
| `automix.fade_in_cuepoint.tempo` | `"%f"` (e.g. `110.000000`), = that cue's bpm |
| `automix.fade_in_cuepoint.origin` | `human` when the cue's field 3 is absent, `ml` when it is 1 (one track: a human field 1 at 744, the chosen ml cue at 730) |
| `automix.fade_out_cuepoint.*` | the same, from the end candidates (field 4; e.g. 178130 and 175099 are field 4 entries, not field 2) |
| `audio.fade_in_start_time` + `audio.fade_in_duration` | = `fade_in_cuepoint.position` exactly, 7 of 7 tracks without a voice-over: 212 + 4575 = 4787, 16196 + 4454 = 20650, 6194 + 2576 = 8770, 10948 + 4562 = 15510, 4972 + 2858 = 7830, 3397 + 4393 = 7790, 4913 + 6902 = 11815 |
| `audio.fade_overlap` | = B's `fade_in_duration` = A's `audio.fade_out_duration` (2576, 2858, 4562), capped by B's in-cue (730) |
| `audio.fade_out_start_time` + `audio.fade_out_duration` | = `fade_out_cuepoint.position` ± 40 ms |
| `audio.fade_in_curves` / `audio.fade_out_curves` | out `[(0,1),(0.2,1),(1,g)]`, in `[(0,g),(0.9,g),(0.9,1),(1,1)]`: g = 0 between tracks, 0.25 under a voice-over |
| `audio.only_allow_fade_on_advance=true`, `audio.automix_mode=radio`, `automix.mode=radio` | DJ only |

So in DJ the cuepoint keys say *which kind 28 cue the player used*, and each fade ends on a cue (A's fade ends at its
out cue, B's fade-in ends at its in cue). Playlist Mix places its overlap differently
([`48-mix-reference.md`](48-mix-reference.md) §7).

For the same transition the morning telemetry reported an overlap of 4473 ms where the published `fade_overlap` was
4562 ms; the 89 ms difference is **(unknown)**.

---

## 8. Unknowns and capture scenarios

| # | Unknown | Status |
|---|---|---|
| C1 | The natural segment boundary: the outro, intro or jump, the page-fetch trigger | **captured** (afternoon, 1 of 1: §1.4). The page-fetch trigger in natural playback and the outro window's start: **(unknown)** |
| C2 | "What can I play for you?" (`dj.interactivity*`) | the desktop has **no** request box [client]; mobile only. Out of scope for a desktop client unless a phone capture comes later. `prompt` (§4) is the likely Connect form |
| C3 | Remote control of a DJ session ("Let DJ pick" from a controller) | **closed** [client]: the Connect `signal` `jump` (§3) |
| C4 | How an official receiver handles a DJ `play` (who resolves, which `reason`) | **(unknown)**: start DJ from a phone with the desktop as the target |
| C5 | Re-entering DJ the same day; restarting the app mid-session | partly: a second start in the same session began with a welcome again, then an intro and a talk-over. Resume, `opmPlayed=true`, other `reason` values and an app restart: **(unknown)** |
| C6 | Session expiry at `lexicon_expiration_time` | **(unknown)** |
| C7 | Language and voice (`voice`, `tts_provider`, `xml:lang`, fulfil fields) | the desktop has **no** DJ language setting [client]; mobile only, out of scope unless a phone capture comes later |
| C8 | Previous, seek, pause and transfer during a narration | partly: transfer is blocked (13 `narration`; "DJ segment will end soon"); seeking is blocked (3 `narration`, `suppress_progress`, the seek bar hides time [client]); pause, previous and next stay allowed (no restriction); previous restarts the narration's track **(inference** from `actions.skipping_prev_past_track=resume`**)**. Previous and a long pause on the wire: **(unknown)** |
| C9 | Starting from the Home DJ card; whether the card transcript is spoken | **(unknown)** |
| C10 | Like, hide and "don't play this artist" inside DJ | dropped: like is the normal collection add [client], nothing DJ-specific |
| C11 | Free-tier behaviour | **(unknown)**; the client has an upsell and country notices [client] |
| — | `prompt`'s payload | **(unknown)** |
| — | The radio automix's fade-out length rule and start-candidate choice (§7) | **(unknown)** |
