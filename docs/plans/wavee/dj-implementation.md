# AI DJ in Wavee: full-support technical plan

Status: draft, 2026-11-25. Target: the open release line after 0.4.0.

What this covers: what it takes for Wavee to play Spotify's AI DJ as the official desktop client does, locally and over
Connect: resolving the DJ session and its pages, the spoken narrations (text-to-speech), talk-over, "Let DJ pick", the
automix between DJ tracks, the restrictions, Connect state as player and as controller, the surfaces in every theme and
the telemetry. Section 1 is the protocol in its final form, as an implementation reference; the evidence is in
[`dj-protocol.md`](dj-protocol.md).

Sibling documents:
- [`dj-protocol.md`](dj-protocol.md): the protocol, as the official client speaks it.
- [`48-mix-full-support.md`](48-mix-full-support.md), [`48-mix-protocol.md`](48-mix-protocol.md),
  [`48-mix-reference.md`](48-mix-reference.md): playlist Mix. DJ's talk-over and automix reuse its voice machinery,
  fade keys and curve format.
- [`connect-commands-from-capture.md`](connect-commands-from-capture.md): the general Connect command wire.

Source: captures of the official desktop client 1.3.10, 2026-11-25 (a morning and an afternoon session); the raw
material is kept outside the repo (it holds account data). Wavee code references are to `release/0.4.0` at
`0a45dbfbc`, paths relative to `src/apps/Wavee/`.

---

## Decisions (owner, 2026-11-25)

| Question | Decision |
|---|---|
| The "DJ must never appear anywhere" rule (`Home/ZonePlanner.cs:31-32`, the filter at `:64-67`) | **Lifted.** DJ shows on Home, in Your Library and in the sidebar like any other list. No setting |
| The queue while DJ plays | **Show the upcoming tracks, grouped by segment** (§2.8), not the official "The DJ doesn't have a queue". A deliberate improvement over the official client |

---

## 0. What "fully supported" means

| # | Capability | Official behaviour | Wavee target |
|---|---|---|---|
| 1 | Start DJ locally | a play from Your Library, Home or the sidebar resolves the lexicon session | the same |
| 2 | Spoken narration | welcome, intro, jump and outro clips from `client-tts`, played in sequence or over a track's intro or tail | the same, sample-accurate |
| 3 | "Let DJ pick" | a local jump to the next segment's jump narration and its target track; on a remote player, the Connect signal `jump` | the same, both ways |
| 4 | DJ restrictions | shuffle and repeat off (`dj-disallow`); no seek, no transfer and no repeat-track while the DJ talks | the same |
| 5 | Infinite session | pages of 5-track segments, lookahead of the current page + 2 | the same |
| 6 | Automix between DJ tracks | each fade ends on a kind 28 cue ("radio" automix) | the same (D3b) |
| 7 | Connect, Wavee as the player | resolve a remote's DJ `play` (today Wavee acks it and stays on its old context) and publish DJ state as the desktop does; advertise DJ-narration support | the same |
| 8 | Connect, Wavee as the controller | start DJ on another device, show its narration state, send `jump` | the same |
| 9 | Home DJ card | jellyfish colours and transcript text | the same |
| 10 | Queue | "The DJ doesn't have a queue" | upcoming tracks by segment (owner decision) |
| 11 | Telemetry | `RawCoreStream` per narration with provider, decision id, attribution and overlap | the same (it feeds DJ personalisation) |
| 12 | DJ request ("What can I play for you?") and DJ language / voice | **not on the desktop**: mobile only | out of scope unless a phone capture comes later |

---

## 1. Protocol summary (implementation reference)

### 1.1 Finding the DJ

- The rootlist and playlist format attributes of `spotify:playlist:37i9dQZF1EYkqdzj48dyYq` carry
  `lexicon_set_type=your_dj` and `lexicon_context_url=hm://lexicon-session-provider/context-resolve/v2/session?contextUri=<uri>`.
  They also carry `home.card.*` (transcript id and text, jellyfish and text colours, a Lottie URL), `automix.mode=radio`
  and `add_context_load_reason=true`.
- Wavee already decodes `lexicon_set_type` for Home (`Spotify/Spotify.Decode.Home.cs:1288-1309`).
- **Rule:** a context is a DJ context when its attributes say `lexicon_set_type=your_dj`. Never key on the hard-coded
  id: per-market and per-account DJ lists may differ. (The official client also accepts its own small set of known DJ
  uris; a narration item is recognised by `is_narration == "true"`.)
- A Home or sidebar start builds `{uri: <DJ uri>, url: <the lexicon_context_url attribute>}`, exactly §2.1.1.

### 1.2 Resolve and pages

| Step | Request | Notes |
|---|---|---|
| Session | `GET spclient /lexicon-session-provider/context-resolve/v2/session?contextUri=<uri>&reason=interactive` | the `hm://` URL with `&reason=interactive` appended. Answer: zstd JSON `{uri, url, metadata, pages:[{next_page_url, tracks:[]}], restrictions}` |
| Page 1 | `next_page_url` verbatim, `hm://` → the spclient path | `…/session/000…0?contextUri=…&opmPlayed=false&requestId=<32 hex>`. **The server writes `requestId` and `previousSegmentId`; the client never builds them** |
| Page n | the `next_page_url` of page n − 1 | `…&previousSegmentId=<lastComponentId>&requestId=…` |
| Headers | `Authorization`, `client-token`, `App-Platform`, `Spotify-App-Version`, `Time-Zone`, `X-Client-Id`, `x-dynamic-device-context` | `x-dynamic-device-context` = base64 `{1 {1 "spotify", 2 <device model>, 3 "computer", 4 {1 <audio output name>, 4 3, 5 0}}}`; page 1 sent it without field 4 |

- **Lookahead:** at the start, fetch the session and 3 pages back to back. After that, one page per jump (20 to 90 ms
  after the button). At a natural segment boundary no fetch happens (the page is already there); what triggers the
  fetch in natural playback is not identified, so Wavee refills when fewer than 2 unplayed pages remain.
- **Session expiry:** `lexicon_expiration_time` is the next local midnight. Re-resolve on the first page fetch after
  it, or when DJ restarts.

### 1.3 Session and page metadata

Context keys:

| Key | Value and use |
|---|---|
| `automix.mode` | `radio` |
| `automix.talk_mode` | `human_cuepoints_or_full_track` |
| `shuffle.algorithm` | `none` |
| `autoplay_candidate` | `false` |
| `end_of_context_action` | `stop` |
| `dj.interactivity_enabled`, `dj.interactivity.localized_cta`, `dj.interactivity.localized_jump_button` | the "Let DJ pick" label comes from here |
| `narration.welcome.*` | the welcome clip |
| `narration.{background,jellyfish.*,jump.*}.color` | the DJ colours |
| `agentic_product_type=dj` | the product marker |

Restrictions: `disallow_toggling_{repeat_context,repeat_track,shuffle}_reasons = ["dj-disallow"]`.

Page: `metadata {correlation-id ssp~…, lastComponentId XX-Segment-<n>, colours}` plus 5 tracks.

Per track:
- `segment`, `decision_id` (`ssp~`), `custom_reporting_attribution` (`<segment>:<page ordinal>:<slot>:0`; slot 0 = the
  intro or jump narration, 1 to 5 = tracks, 6 = the outro);
- `station_title=DJ`, `station_subtitle` (the segment's label), `album_image_small/large`;
- the first track: `dj.jump_target=true`, `narration.intro.*`, and from page 2 on `narration.jump.*`;
- the last track: `narration.outro.*`.

Narration slot keys (`narration.<slot>.`):

| Key | Value |
|---|---|
| `ssml` | the spoken text |
| `title` | Welcome / Up next |
| `artist` | DJ X |
| `album_title` | DJ |
| `image` | the cover |
| `voice` | VOICE1 |
| `tts_provider` | SONANTIC_FAST |
| `sample_rate` | 44100 |
| `loudness` | −16.0 |
| `true_peak` | −3.0 |
| `commentary_type` | NARRATION or LISTENER_STORY_* |
| `commentary_id` | empty, or a uuid for listener stories |
| `decision_id` | `dj~…` |
| `custom_reporting_attribution` | |

The SSML holds `<entity type="artist" uri="<base62>">Name</entity>`; `uri` may be missing.

### 1.4 TTS

1. **Fulfil:** `POST spclient /client-tts/v1/fulfill`. Body: protobuf `{2 ssml, 3 5, 5 1, 6 6, 7 44100}` (= the
   `media.manifest`'s field 1.1.4; field meanings inferred: 3 = provider SONANTIC_FAST, 5 = voice VOICE1, 6 = codec
   MP3). Headers: `Content-Type: application/x-www-form-urlencoded` (sic), `Accept-Encoding: identity`, `Authorization`. The
   answer is a **303** whose `Location` is
   `https://tts.spotifycdn.com/v2/generate/sonantic_large/mp3/44100/x-dj-speechcraft-rayserve/0/<b64url SSML>?__token__=exp=…~hmac=…&fauth=<JWT>`.
2. **Download** the MP3 at once: the signed URL expires after **300 s**. The CDN serves `audio/mpeg` at about 320
   kbit/s, `Cache-Control public, max-age=604800`. A cache MISS streams without Content-Length while it synthesises; the
   24.5 s intro took 5.3 s.
3. **Prefetch:**
   - fulfil the welcome and the page-1 intro immediately;
   - after that, about 5 s after each item starts, fulfil every narration not yet fetched up to and including the next
     `narration/jump` item (fits 4 of 5 morning observations);
   - a jump clip must therefore be on disk before the button is pressed.
   - **Open:** at the afternoon's natural boundary the outro was fulfilled only about 4 s before it played and the
     intro about 1.4 s before (the intro is not in `next_tracks` until then: the jump variant is). The rule above does
     not cover them, and when Wavee should fetch them is not decided.

### 1.5 Narration as playable items (the Connect shape)

The player inserts narration **items** into the queue:

| Slot | uri | provider |
|---|---|---|
| welcome | `spotify:media:0000000000000000000000` | `narration/welcome` |
| intro, jump | `spotify:media:<id of the jump-target track>` | `narration/intro`, `narration/jump` |
| outro | `spotify:media:<id of the segment's last track>` | `narration/outro` |

- Every item carries `is_narration=true`, `hidden_in_queue=true`, `has_ephemeral_uri=true`, `track_player=audio`,
  `suppress_progress=true`, `media.manifest` (base64 `{1 {1 {1 "POST", 3 "client-tts/v1/fulfill", 4 <fulfil body>, 5 1},
  5 {1 f32 loudness, 2 f32 true_peak}}}`), `actions.skipping_{next,prev}_past_track=resume`, the narration display
  keys and `decision_id` / attribution. The welcome's uid is `list_intro_item`.
- **Every DJ start begins with the welcome** (3 of 3 starts), then the first segment's intro.
- Queue rules:
  - the **intro** goes directly before the segment's first track;
  - the **outro** goes after the segment's 5th track, inserted once that track is 2 slots ahead;
  - the **jump** goes after the current segment (after its outro).
  - Only the nearest boundary gets an item; an intro and a jump never both appear at one boundary.
- **At a natural boundary the intro plays**, not the queued jump variant (1 of 1). How the official player swaps the
  queued jump item for the intro is not observed; Wavee's planner decides it (§2.2).

### 1.6 Talk-over

Given the narration's duration `d` and the target track's start cue (kind 28 `Cuepoints` field 1:
`{1 ms, 2 bpm, 3 origin (absent = human, 1 = ml), 4 confidence}`). These fit all six observed narrations into a track
and the one observed outro.

| Case | Track starts at position | Track starts (from the narration's start) | Fade window, overlap | Music gain curve |
|---|---|---|---|---|
| **human** cue | `max(0, cue − d − 1600)` | `max(0, d − (cue − 1600))` ms (0 when the voice fits before the cue) | `[start position, cue]`; `fade_overlap = min(d, cue − 1600)` | 0 → 0.25 linear over the first 1000 ms; 0.25 plateau; up to 1.0 at `cue − 1000` |
| **ml** cue, or no human cue | 0 | `max(0, d − 3400)` ms | 5000 ms; `fade_overlap = min(d, 3400)` | 0.25 flat for 0 to 3000 ms; up to 1.0 at 4000 ms |
| **outro** (one sample) | — | the outro starts 4000 ms into the last track's fade-out window, which runs to the track's end | the window was 5507 ms; where its start comes from is open | the **outgoing** track ducks 1 → 0.25 over 4000 ms, then stays at 0.25 to its end (it never fades to 0) |
| duration not known in time (the page-1 intro, a streamed cache MISS) | 0 | after the narration ends | none | the 24.5 s intro played in full, then the track |

- In both cue cases the music returns to full volume 600 ms after the voice ends.
- The voice itself is never faded: the narration's `audio.fade_out_curves` is flat at 1.
- After an outro, the next segment's intro starts when the outro ends, and its target follows the ml or human rule.
- Exact curve JSON, to publish to Connect verbatim:
  - **ml**, `audio.fade_in_curves`:
    `[{"start_point":0,"end_point":0.6,"fade_curve":[{"x":0,"y":0.25},{"x":1,"y":0.25}]},{"start_point":0.6,"end_point":1,"fade_curve":[{"x":0,"y":0.25},{"x":0.5,"y":0.25},{"x":0.5,"y":1},{"x":1,"y":1}]}]`
  - **human**, for a 6902 ms window:
    `[{"start_point":0,"end_point":0.14489,"fade_curve":[{"x":0,"y":0},{"x":1,"y":0.25}]},{"start_point":0.14489,"end_point":0.71023,"fade_curve":[{"x":0,"y":0.25},{"x":1,"y":0.25}]},{"start_point":0.71023,"end_point":1,"fade_curve":[{"x":0,"y":0.25},{"x":0.5,"y":0.25},{"x":0.5,"y":1},{"x":1,"y":1}]}]`
    (break points = 1000 / 6902 and (6902 − 2000) / 6902).
  - **outro**, the last track's `audio.fade_out_curves`:
    `[{0–0.726348: (0,1)(.5,1)(.5,.25)(1,.25)}, {0.726348–1: flat 0.25}]` (0.726348 × 5507 = 4000 ms).
- Narration items also carry `audio.only_allow_fade_on_advance=true`: the duck applies only when the track is reached
  by advancing, not by a skip into it.
- **When the target becomes the current item:**
  - after a **jump** clip (4 of 4): as soon as the target starts;
  - after an **intro or welcome** (3 of 3): the narration stays current until it ends. When the target starts under it,
    the target leaves `next_tracks` and the `narration` restrictions are lifted, with the narration still current; at
    the narration's end the track becomes current at position ≈ 3400 + 50.

### 1.7 Automix between DJ tracks ("radio", D3b)

DJ tracks are not butt-joined. Each track has a fade-out window, each next track a fade-in, published with the
playlist-Mix keys plus `automix.fade_*_cuepoint.origin` (`ml` / `human`) and `.tempo` (afternoon, five pairs; the
morning's tracks agree, [`dj-protocol.md`](dj-protocol.md) §7):

| Rule | Detail |
|---|---|
| A's fade-out | ends on A's end cue (kind 28 field 2) within 40 ms; 3.0 to 5.4 s long (the length rule is open) |
| A's curve | `(0,1)(.2,1)(1,0)`, one quadratic segment |
| f11 | = `fade_out_start_time`; the position keeps counting past it while A fades |
| B with a confident start cue | its fade-in ends on that cue; it starts at `cue − overlap` when A's fade-out starts; `fade_overlap` = `fade_in_duration` = A's fade-out length |
| B without one | from 0, a 350 ms overlap started 350 ms before A's fade-out ends; a human cue under the overlap gives its own position as the overlap (260 ms seen) |
| B's curve | `(0,0)(.9,0)(.9,1)(1,1)`: silent for 90 % of the window, then up |
| current item | B becomes current when it starts |
| open | one pair fits neither B rule (its in-point is probably another start candidate): the candidate choice |

### 1.8 Controls

- **Let DJ pick** (`dj_jump_button`) is local. The client skips the rest of the segment and its outro, plays the next
  `narration/jump` with talk-over into its target, and fetches the next page 20 to 90 ms later. No Connect command and
  no lexicon call besides that page.
- **On a remote DJ player**, "Let DJ pick" is the Connect `signal` command `jump`. A DJ player advertises `jump` in
  PlayerState field 33 while DJ plays.
- **Skip during a talk-over** skips the narration and its target track together.
- Skipped tracks go to `prev_tracks`; narration items never do.
- **During a narration** (until its target starts): seeking is refused (the seek bar hides time and duration), repeat
  track and transfer are refused (the device picker says "DJ segment will end soon"); pause, previous and next stay
  allowed. Previous restarts the track the narration belongs to (an inference from
  `actions.skipping_prev_past_track=resume`).

### 1.9 Connect state (what Wavee publishes as the DJ player)

| PlayerState field | Value |
|---|---|
| f2 context_uri | the DJ playlist uri |
| f3 context_url | the lexicon URL **without** `&reason` |
| f4 context_restrictions | 8, 9, 10 = `dj-disallow` |
| f6 index | (page, track) for tracks, page 1 = the first real page; none for narrations |
| f7 track | the narration item while it is current (§1.6); `duration` = the MP3's length, unset while streaming |
| f11 duration | the narration's length; for tracks, `fade_out_start_time` (§1.7) |
| f17 restrictions | the `dj-disallow` set; **plus 3, 9 and 13 = `narration`** while a narration is current and its target has not started (no seek, no repeat-track, no transfer); plus the ordinary 2 `not_paused`, 6 `no_prev_track`, 25 `not_supported_by_content_type`, 31 `already_set` |
| f19 prev_tracks | tracks only |
| f20 next_tracks | tracks interleaved with the upcoming narration item |
| f21 context_metadata | session metadata + rootlist / command metadata + `player.arch`, `mixer_enabled=true`, `enhanced_context=false`, `context_owner=spotify`, … |
| f22 page_metadata | the current page's metadata (empty on a narration) |
| f33 signals | `speed-preview`, `automix-preview`, `stop-automix-preview`, `stop-speed-preview`, `list-next-page`, and once the session is loaded **`prompt`** and **`jump`** (`switch-to-video` too). Wavee advertises `jump`, not `prompt` (its payload is unknown), and the preview signals only once Mix's M5 renders them |

- Track metadata adds `automix.fade_{in,out}_cuepoint.{position,origin,tempo}` and the `audio.fade_*` keys of §1.6 and
  §1.7; narration items add `suppress_progress=true`.
- `player.arch` was 2 in the DJ session and 3 in a playlist-Mix session; its meaning is open.
- A remote device without DJ-narration support makes the official desktop show "In beta, the DJ's voice is unavailable
  on this speaker" (the client's capability `supportsDJNarration`; its Connect wire name is open).

---

## 2. Design in Wavee

### 2.1 Context resolution (`Playback/Playback.Host.Context.cs`, `Spotify/Spotify.Api.cs`)

1. **`PlayContext` gains a context URL and metadata.** A local DJ start (the Home card, a Library row, the sidebar, the
   playlist page's play button) reads `lexicon_context_url` and the `home.card.*` attributes from the playlist's format
   attributes (already in the list read; keep them: today only the Mix attributes are lifted,
   `Spotify/Spotify.Decode.Mix.cs:136-180`) and sets `load.ContextUrl` and `load.ContextMetadata`
   (`Playback/Playback.Host.Context.cs:114-125`).
2. **`ResolveContext` branches on the URL** (`Playback/Playback.Host.Context.cs:465-491`): when `ContextUrl` starts with
   `hm://lexicon-session-provider/`, GET `RemotePlan.PageRoute(url) + "&reason=interactive"`
   (`Playback/Playback.Transitions.cs:386`) through `Spotify.Api.Send(RequestKind.Custom…)` with the DJ header set
   (§1.2; a new `HeaderSet.DynamicDeviceContext` built from the device model and the current output device's name).
   - The answer goes through the existing `Decode.ContextResolve` (`Spotify/Spotify.Decode.Remote.cs:398`).
   - **Inbound Connect needs no change beyond this:** `RunLoad` (`Playback/Playback.Host.Remote.cs:185`) already
     carries `ContextUrl`; it was never read. This fixes the observed "acked but stayed on the artist" bug.
3. **Decode extensions** (`Spotify/Spotify.Decode.Remote.cs`):
   - read the root `restrictions` into `ResolvedRestrictions` (three reason lists);
   - keep the page `metadata`;
   - mark the page `Infinite` when `metadata.lexicon_set_type == your_dj` (`:437`);
   - `JsonTracks` (`:445-470`): copy `narration.*`, `dj.jump_target`, `segment`, `decision_id`,
     `custom_reporting_attribution`, `station_subtitle`, `automix.talk_mode` and `album_image_*` into a **DjBook**
     staged row keyed by the queue `ItemId` (the pattern of `s_uids.NoteQueuedBy`,
     `Playback/Playback.Transitions.cs:647`, and of autoplay's decision book, `Playback/Playback.Host.Autoplay.cs:268`).
4. **Paging:** `RequestPage` / `FetchPage` (`:927-990`) unchanged (the URL is complete). A new DJ lookahead policy in
   `Playback/Playback.Refill.cs`:
   - at the start: the session + 3 pages;
   - on a jump: one page;
   - otherwise: refill when fewer than 2 unplayed pages remain.
   - Pages append as `QueueProvider.Context` rows, as today.
5. **Expiry:** `DjSession.ExpiresAt` from `lexicon_expiration_time`. A page fetch after it re-resolves the session
   (`reason=interactive`); play continues from the newly resolved first page.

### 2.2 The narration item (a new queue kind)

Narration is a **first-class queue item**, not a pure overlay:
- Connect needs it as the current item with its own duration and restrictions (§1.9).
- The welcome and the long intro play on their own, so the pump must treat them as an item anyway.
- Talk-over is then exactly the Mix machinery's "B starts under A" (§2.4).

| Piece | Change |
|---|---|
| Entity | `EntityKind.Media` rows for `spotify:media:*`; display from the narration keys (title "Up next" / "Welcome", artist "DJ X", image) |
| Queue | `QueueProvider.Narration`; built by a **DjPlanner** from the DjBook after each page lands and after each advance; rows `hidden_in_queue` (the queue UI skips them, §2.8) |
| Insertion | the welcome first on every start; the intro before the segment's first track; the outro after track 5 once it is 2 ahead; the jump after the current segment (one boundary item at a time, §1.5). At a natural boundary the planner plays the intro; on a jump, the jump variant |
| Source route | a new `SourceRoute.Tts`: resolve with `DjTts.Url(item)` (fulfil, §2.3), then the existing `Spotify.Audio.ExternalChoice(url, durationMs)` MP3 path (`Spotify/Spotify.Audio.cs:1218`; NLayer `Mp3AudioDecoder`, `Playback/Playback.Audio.cs:2973`) |
| Loudness | normalisation gain = (the user's normalisation target − the manifest's loudness, −16 LUFS), capped by the true peak (−3 dBTP); applied in place of the Spotify file gain (`NormalizationFactor`, `Playback/Playback.Audio.cs:1602-1640`) |
| Duration | unknown while a CDN MISS streams. PutState f11 stays unset (as the official client does) until the decoder knows it; the talk-over waits for it and falls back to "in full, then the track" (§1.6, last row) |
| Progress | `suppress_progress`: no seek, the time and duration hidden while a narration is current |
| Previous / next | `skipping_prev_past_track=resume`: previous during a narration restarts the track it belongs to (an inference; the wire is not captured) |

### 2.3 The TTS client (`Spotify/Spotify.Api.Dj.cs`, new)

- `Fulfil(ssml, manifestFields) → Uri`:
  - `PostEncoded` with `ProtoWriter` (`Spotify/Spotify.Decode.PutState.cs:44`); the body is taken verbatim from the
    item's `media.manifest` field 1.1.4 when present, else built from the narration keys;
  - **a non-redirecting send**: the API `HttpClient` follows redirects (`Spotify/Spotify.Api.cs:337-343`). Add a second
    handler with `AllowAutoRedirect = false` for this route, or a per-request flag, and read `Location`;
  - never forward `client-token` or `Authorization` to the CDN. The official client does, but the URL is signed.
- **Download** on the CDN client (the one `ExternalChoice` uses). Keep the bytes in a session cache
  `ssml-hash → mp3 bytes + duration`, capped at 64 MB, memory first and spilled to the app cache folder (respecting
  `max-age=604800`). The 300 s signed URL is used once.
- **Prefetch scheduler** (`DjTts.Prefetch`): on every track change (+5 s), fulfil and download every unfetched
  narration up to the next jump item. Fetch the welcome and the first intro immediately on resolve. When to fetch the
  intro of a natural boundary is open (§1.4). A failed fulfil retries once, then drops the
  narration (the music continues; log `[dj] narration dropped why=`).
- **Streaming MISS:** play while downloading (the podcast route's range reader already streams), so the intro starts at
  once, as in the official client.

### 2.4 Talk-over in the audio engine (reusing Mix, `Playback/Playback.Audio.Mix.cs`)

The Mix join already does what talk-over needs: B is added at an exact mixer frame with a curve envelope, A gets its own
curve, and one transition gate covers both (`ScheduleMix` `:159-243`, `MixRender.Build` `:292-340`, the engine's
`GainEnvelope.FromCurve`).

- **`MixTiming` from a DJ pair:** when the playing row is a narration and the next row is its target track, `Mix.Follow`
  (`Playback/Playback.Mix.cs:78-118`) asks a **TalkOverPlanner** instead of the playlist planner. It returns a
  `MixTiming` with:
  - out start = `max(0, d − 3400)` (ml) or `max(0, d − (cue − 1600))` (human);
  - in start = the track position (§1.6);
  - A's curve = flat 1;
  - B's curve = the duck of §1.6 (a 1025-point table from the exact break points);
  - no EQ or filter automation.
- **The outro case** (a third case beside human and ml): the outgoing row is the segment's last track and the next row
  its outro. Duck the track 1 → 0.25 over 4000 ms from the start of its fade-out window, start the clip at 4000 ms, keep
  0.25 to the track's end; the voice is not faded. Then the intro follows the outro, and its target follows the ml or
  human rule.
- **Announce:** `MixTiming.AnnounceAt` = `TargetStart` after a **jump** clip (the track is published as current when it
  starts), `NarrationEnd` after an **intro or welcome** (the narration stays current until it ends; the target then
  appears at its position ≈ 3400 + 50).
- **The narration voice keeps playing after a `TargetStart` announce** until it ends: A's envelope stays at 1 to its
  natural end, and the gate releases A's voice at EOF instead of at the window's end. A small change in the app's
  `AbandonPendingJoin` and the announce path: treat "A outlives the announce" as allowed for `Kind == TalkOver`.
- **Skip during the overlap** removes both voices and advances past the target: when the playing row is a narration and
  the join has started, `skip_next` advances two rows.
- **Fallback with no cue data** (kind 28 missing): the ml rule (the track from 0, `d − 3400`).
- **Seek inside a narration:** refused (f17 field 3 = `narration`; `suppress_progress`).

### 2.5 Automix between DJ tracks (D3b)

Ordinary automix on top of D3's talk-over, with the same Mix voice machinery and the two curves of §1.7:
- a **RadioPlanner** beside the TalkOverPlanner: A's fade-out ends on A's end cue (kind 28 field 2); B with a confident
  start cue is pre-rolled so its fade-in ends on that cue, else from 0 with a 350 ms overlap (a human cue under it
  shortens the overlap to its position);
- curves: A `(0,1)(.2,1)(1,0)` quadratic, B `(0,0)(.9,0)(.9,1)(1,1)`;
- B becomes current when it starts; f11 = `fade_out_start_time`;
- what counts as a "confident" start cue is open (cues of confidence 0.88 and 0.90 were used; 0.21 and 0.39 were
  not), and so is the candidate choice for the one pair that fits neither rule.
- Until D3b ships, DJ tracks play gapless.

### 2.6 DJ state, restrictions and controls (`Playback/Playback.cs`, the reducer)

- `State.Dj` (null when not DJ): `Session`, the current segment, `JumpTargetAhead`, `Narrating` (`ItemId`, slot,
  caption text), the colours, the CTA and jump labels.
- **Restrictions:** add `ContextRestrictions` to the reducer and honour them locally:
  - the shuffle and repeat buttons are disabled with a tooltip whose reason text comes from the restriction
    (`dj-disallow` → "Not available with DJ");
  - `StartContext` must not apply the user's shuffle when `disallow_toggling_shuffle` is set
    (`Playback/Playback.Host.Context.cs:603`);
  - while narrating (until the target starts): seek, repeat-track and transfer are refused (official f17 3, 9, 13 =
    `narration`).
- **Jump** (`Playback.DjJump()`):
  - drop the rest of the current segment (it goes to history as skipped);
  - drop the pending outro;
  - make the next `narration/jump` item current;
  - schedule the talk-over into its target;
  - trigger one page fetch.
- **Remote jump:** advertise `jump` in f33 while DJ plays and run `DjJump()` on an inbound
  `{"endpoint":"signal","signal_id":"jump"}`. As a controller, send that command.
- `end_of_context_action=stop`, `autoplay_candidate=false`: autoplay never takes over a DJ context (`Infinite` already
  blocks it).

### 2.7 Connect

**As the player** (`Spotify/Spotify.Decode.PutState.cs`):
- f3 context_url = the lexicon URL without `reason` (today always `context://…`, `:352-363`);
- f4 context_restrictions from `ContextRestrictions` (today written empty, `:364`);
- f17 adds 3, 9 and 13 = `narration` while narrating (until the target starts);
- narration items in f20 with their metadata (`QueueRow` `:591` gains the DJ keys);
- f22 page_metadata;
- f11 = the narration's duration;
- no index for narrations;
- track metadata carries the `automix.fade_*_cuepoint.*` and `audio.fade_*` keys of §1.6 and §1.7 (the Mix publisher
  already writes `audio.fade_*` for the playing pair, `:549-601`), and `suppress_progress=true` on narrations;
- f33 adds `jump` while DJ plays, not `prompt`;
- echo the full context metadata (`CaptureOrigin`, `Playback/Playback.Host.Wire.cs:53-71`). **Check that
  `Queue.ContextMetadataValueSound` (`Entities/Queue.Rules.cs:338-345`) does not drop SSML values.**
- **Device capability:** advertise DJ-narration support in Wavee's Connect device capabilities once D2 ships, and not
  before (its wire name is open; find it before D4).

**As the receiver of a remote DJ `play`:** handled by §2.1.2. Append `&reason=<play_options.reason>`, default
`interactive`.

**As the controller:**
- `Spotify/Spotify.Connect.Play.cs:124-126, 311-313` build the DJ envelope as the desktop does:
  - `context.uri = entity_uri` = the DJ uri, `context.url` = the lexicon URL;
  - metadata = the playlist's format attributes + `context_description=DJ`;
  - one empty page;
  - `play_origin.feature_identifier=playlist`,
    `prepare_play_options.player_options_override.modes.context_enhancement=NONE`, `license=premium`,
    `play_options.reason=interactive`, `operation=replace`.
- Read a remote's DJ state from its cluster: a narration item current → the caption and the DJ chrome on Wavee's bar;
  its `restrictions` drive the disabled buttons. "Let DJ pick" on a remote sends the `jump` signal.

### 2.8 UI (all themes)

| Surface | Content | Files |
|---|---|---|
| Home | the DJ card: a colour gradient from the `home.card.*` colours, the transcript text; play starts DJ (not `PlayContext` with a bare uri). Lift `IsDj` from `SectionRoles.IsUsable` (owner decision) | `Home/ZonePlanner.cs:31-32, 64-67`; `Home/Board/HomeCatalog.cs`, `Home/Board/HomeSources.cs`, `Home/Board/Hosts/Tile.Hosts.cs` |
| Library, sidebar, playlist page | the DJ list shows as "DJ" with its cover; its page lists no tracks (the server returns none) and shows a DJ hero: "Your DJ", the CTA text, play | `Entities/Playlist.Page.cs` (a DJ branch) |
| Player bar | while DJ plays: the **"Let DJ pick"** button (label from `localized_jump_button`, tooltip "Get some different DJ picks", colours `narration.jump.*`); shuffle and repeat disabled; the title area shows "DJ · \<segment label\>" (`station_subtitle`); during a narration the seek bar hides time and duration and refuses seeks | WinUI `WinUiPlayerBar.cs` / `.Parts.cs` / `WinUiTransport.cs`; Classic `ClassicPlayerBar.cs` / `ClassicTransport.cs`; Zune `ZunePlayerBar.cs` / `.Parts.cs` / `ZuneTransport.cs` |
| Narration caption | SSML → plain text with the `<entity>` names as links (artist uri). Shown timed (the whole text, fading in with the voice) in the stage caption and the now-playing hero. The stage caption already does timed text | `Shell/CaptionClock.cs`, `Shell/Stage.Caption.UI.cs`, `Theming/Contracts/StageCaptionProps.cs`; per theme `*StageCaption.cs`, `*NowPlayingHero.cs` |
| Queue | narration rows hidden; tracks grouped by segment with the segment label as the group header; the jump target marked (owner decision; the official client shows "The DJ doesn't have a queue") | `Shell/QueueModel.cs`, `*QueuePanel.cs` / `ClassicQueue*.cs` / `ZuneQueuePanel.cs`, `Themes/Zune/Structure/Views/Stage/ZuneUpNext.cs` |
| Mini player | the jump button and a caption line | `*MiniPlayer.cs`, `Themes/Common/CommonMiniPlayer*.cs` |

Motion rules apply (no jumps):
- the caption fades in and out on its own;
- the jump button appears with DJ and leaves when DJ ends;
- the queue regroups per item (`nav-hold`, `no-layout-jumps`).

### 2.9 Telemetry (`Spotify.Telemetry*`)

- Per narration: `RawCoreStream` + `RawCoreStreamSegment` with the uri `spotify:media:…`, provider `narration/<slot>`,
  start and end reasons (`playbtn` / `fwdbtn` / `trackdone`), ms played, overlap ms (fields 16 and 67), format, the
  context uri, the attribution, the `dj~` decision id, the `dj` product, feature `context-player`, and the referrer.
- Tracks: add `decision_id` and `custom_reporting_attribution` from the DjBook to the existing play report
  (`RunPlayReport`, `Playback/Playback.Host.Context.cs:1377`).
- `ms_narration_overlapping` (`Spotify/Protos/event_sender_events.proto:54`) on the overlapped track.

### 2.10 Headless proof (`--fake`)

- `Entities/Entities.Fake.Dj.cs`:
  - a DJ playlist with the attributes;
  - a canned session plus 3 pages (the real shapes, anonymised);
  - narration clips = generated speech-band noise MP3s of fixed lengths (3.4 s, 24.5 s, 4.9 s);
  - kind 28 cues (human and ml);
  - the jump is drivable.
- The inspector verb `dj`: the session, pages, the DjBook, the queue with narration rows, the talk-over plan per pair
  (curve break points, frames), the restriction state.
- Render and trace each theme's player bar, now playing and queue with DJ active, during a narration and after a jump.
  `lint min=error` clean.

---

## 3. Phases

Each phase ends with a clean Debug build, an inspector render plus trace, and screenshots checked.

| Phase | Status | Scope | Gate |
|---|---|---|---|
| **D0** | **in progress (2026-11-25)** | the owner's decisions are taken (DJ rule lifted, queue by segment); the fake fixtures; the inspector `dj` verb | the fixtures load in `--fake` |
| **D1 Resolve and queue** | **in progress (2026-11-25)** | §2.1 (context URL, lexicon resolve, decode extensions, DjBook, lookahead, expiry, infinite); restrictions in the reducer; a local start from Library, the sidebar and the playlist page; an inbound remote `play` resolves (fixes the ack-and-ignore bug) | in `--fake`: DJ plays tracks from pages, shuffle and repeat disabled; the inspector shows the pages and the DjBook |
| **D2 Narration** | not started | §2.2 and §2.3: narration rows (welcome first on every start), `SourceRoute.Tts`, the fulfil without redirect, CDN download and cache, the prefetch scheduler, loudness, sequential playback (welcome → intro → track) | the welcome and the intro play in sequence; the caption shows; the logs show the prefetch timing |
| **D3 Talk-over and jump** | not started | §2.4 and §2.6: the TalkOverPlanner (human, ml, outro, fallback), `AnnounceAt` per narration kind, A outlives a `TargetStart` announce, skip-both, seek refused during a narration, Let DJ pick | the inspector's timing table matches §1.6 for the fixture cues to the millisecond; a jump plays jump → target with the duck; a natural boundary plays outro over the ducked tail → intro → the track under the intro |
| **D3b Radio automix** | not started | §2.5: the RadioPlanner and its two curves between DJ tracks | the inspector's timing per fixture pair matches §1.7 (A's fade ends on the end cue; B's fade-in ends on its start cue, or the 350 ms fallback) |
| **D4 Connect** | not started | §2.7: the PutState DJ shape (3 / 9 / 13 `narration`, `suppress_progress`, f33 `jump`), the inbound `jump` signal, the controller envelope and `jump` command, the remote DJ state on the bar, the DJ-narration capability (once D2 has shipped) | the official desktop as controller starts DJ on Wavee and shows the narration and what is next; "Let DJ pick" from the desktop jumps on Wavee; Wavee starts DJ on the desktop (a live check with an approved real-window run on the secondary monitor) |
| **D5 Surfaces and telemetry** | not started | §2.8 Home card, the DJ page, every theme's surfaces, queue grouping; §2.9 telemetry | per-theme renders; the telemetry payload dump matches §2.9 |
| **D6 After captures** | not started | the scenarios still open in §4 (C4, C5, C6, C8, C9, C11, `prompt`), each captured first, then built | each scenario captured before it is built |

---

## 4. Captures: status

| # | Scenario | Status |
|---|---|---|
| C1 | DJ untouched across natural segment boundaries: outro playback, intro or jump at a natural boundary, the page-fetch trigger | **captured** (one boundary): the outro talks over the ducked tail, then the intro (§1.6). Still open: the page-fetch trigger in natural playback, where the outro window's start comes from (one sample: 5507 ms before the end, while the end cue was 710 ms before it) |
| C2 | "What can I play for you?" | the desktop has **no** request box; mobile only. Out of scope unless a phone capture comes later |
| C3 | Let DJ pick and skip from a controller | **closed**: the Connect `signal` `jump` (in D4) |
| C4 | A phone starts DJ **on the desktop** (an official receiver resolving the lexicon URL) | open |
| C5 | Stop DJ, play something else, start DJ again; restart the app mid-session (resume, `opmPlayed=true`, other `reason` values) | partly: a second start in the same session began with the welcome again, then an intro and a talk-over. The rest is open |
| C6 | A session across `lexicon_expiration_time` | open |
| C7 | DJ language and voice | the desktop has **no** DJ language setting; mobile only. Out of scope unless a phone capture comes later |
| C8 | Previous, seek, pause 30 s and transfer during a narration | partly (§1.8): transfer and seek are refused, pause, previous and next allowed. Still open on the wire: what previous does, and a long pause |
| C9 | Start from the Home DJ card (is the card transcript spoken?) | open |
| C10 | Like, hide and "don't play this artist" inside DJ | **dropped**: like is the normal collection add; nothing DJ-specific |
| C11 | Free tier | open |

Commands arriving at the official desktop are not visible in the captures so far, only their effects; C4 needs them.

---

## 5. Risks

- **Personalisation:** if Spotify counts DJ engagement through `RawCoreStream`, Wavee without §2.9 would make DJ worse
  over time. §2.9 is in D5 and is not optional.
- **Signed URL expiry (300 s) and the synthesis lag** (5 s for a long intro): the prefetch must stay ahead. Jump clips
  are short (3 to 5 s) and prefetched.
- **Free tier:** unknown (C11). Gate DJ on the account's product type `premium` until it is captured.
- **The hard-coded DJ uri:** never rely on it (§1.1).

---

## 6. Open questions

| Question | Blocks | Detail |
|---|---|---|
| What triggers a page fetch in natural playback | nothing (Wavee refills at fewer than 2 unplayed pages) | §1.2 |
| Where the outro's fade-out window starts | D3 | one sample (§4, C1) |
| The radio automix: the fade-out length rule, the start-candidate choice, the "confident cue" threshold | D3b | §1.7, §2.5 |
| `prompt`'s payload | nothing on the desktop (C2 is mobile only) | §1.9 |
| The Connect wire name of the DJ-narration capability | D4 | §1.9 |
| `player.arch` (2 in DJ, 3 in playlist Mix) | D4 | §1.9 |
| How the official player swaps the queued jump item for the intro at a natural boundary | nothing (Wavee's planner decides) | §1.5 |
| When to fetch a natural boundary's outro and intro (the official client fetched them late in the one sample) | D2 | §1.4 |
| Which keys an official controller needs to draw DJ UI from Wavee's state | D4 (echo everything until known) | [`dj-protocol.md`](dj-protocol.md) §4 |
| C4, C5 (rest), C6, C8 (rest), C9, C11 | D6 | §4 |
