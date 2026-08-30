# Extended-metadata: payload decode (kinds Wavee does not consume)

> Workflow agent output, run `wf_5a5408b2-258`.

**findings:**
  1. DECODING CORRECTION (affects any earlier pass using scratchpad xmresp_lib.decode): 968 of 2442 extended-metadata responses in the .saz set silently decoded to ZERO bytes because xmresp_lib.decode() uses `zstd.ZstdDecompressor().decompressobj().decompress(body)`, which fails on Spotify's multi-frame zstd responses and the except-swallow returns the raw compressed bytes. Using `zstd.ZstdDecompressor().stream_reader(io.BytesIO(body)).read()` fixes it (0 of 461 bad on all.saz). After the fix, response-side EntityExtensionData counts reconcile EXACTLY with request-side ExtensionQuery counts (e.g. TRACK_V4 26,267 -> 231,636). All counts in this report are post-fix. Content-Encoding distribution over the 2442 XM responses: zstd 2070, gzip 206, none 166. Every XM response was HTTP 200 at the transport layer; per-entity status lives in the inner header.
  2. Request header (BatchedEntityRequest f1) was IDENTICAL in all 2718 XM requests: {f1='NL', f2='premium'}. f3 (task_id) never observed populated on the wire in these captures.
  3. kind 239 spotify.contentagnostic.v2.ContentCapabilityTrait is the direct content-gating signal Wavee is missing (665x 200, 611x 304). It is four parallel capability slots, each a sub-message whose SET FIELD NUMBER is the verdict and whose f3 carries a restriction-reason map {f1=key, f2=value}. Observed complete shapes (exact, from 665 samples): track = `1{2}, 2{3{1{1='offline',2='1'}}}, 3{1}, 4{4}` or with slot4 = `4{3{1{1='music-video-disabled',2='1'}}}` (223 samples carried a slot-4 restriction). episode = `1{3}, 2{1}, 3{1}, 4{4}` or slot4 = `4{3{1{1='other-video-disabled',2='1'}}}`. show (1 sample) = `1{3}, 2{2}, 3{1}, 4{4}`. Reading: slot 2 = download/offline capability (track always restricted with reason 'offline'='1' on this premium/NL account -- 399/399 track samples), slot 4 = video capability with reason 'music-video-disabled' for tracks and 'other-video-disabled' for episodes. This is the server telling the client to suppress the video affordance. Slot 1 and slot 3 semantics are NOT determinable from these captures (only ever two distinct verdicts each, never a reason string).
  4. kind 21 EPISODE_TRANSCRIPTS carries REAL, directly consumable transcript pointers (188x 200 after the zstd fix, 318x 304) -- an earlier small sample made it look like a bare URI marker. f1 = entity uri. f2 = REPEATED transcript entry: f2.1 = 'spotify:transcript:<id>', f2.2 = BCP-47-ish locale ('en-us','de-de','fr-fr','pt-br'), f2.4 = 'https://episode-transcripts.spotifycdn.com/1.0/spotify:transcript:<id>', f2.6 = 'https://spclient.wg.spotify.com/transcript-read-along/v2/episode/<episodeId>[/<lang>]'. f2.3 and f2.5 never observed populated. Only 12 of 188 samples carried any f2 entry -- most 200s are f1-only, i.e. 'no transcript'. The read-along URL (f2.6) is the synced-lyrics-equivalent surface for podcasts.
  5. kind 6 TRACK_DESCRIPTOR (spotify.descriptorextension.ExtensionDescriptorData) is the user-visible mood/genre tag set. Repeated f1 = descriptor: f1.1 = lowercase slug ('quiet','soft','cozy','mellow','k-pop','chill','nostalgia','dance pop','emotional'), f1.2 = fixed32 float confidence 0.0-1.0 (observed 0.0166-0.9749, sorted descending within a track), f1.3 = PACKED repeated varint category ids, f1.4 = 'spotify:concept:<id>', f1.5 = Title-Cased display label ('Quiet','K-Pop'). Category id correlation is clean across 43 descriptors in 6 tracks: id 1 accompanies genre words (pop, k-pop, electropop, dance pop, pop rock, synthpop, easy listening, alternative pop, bubblegum pop, soundtrack), id 2 accompanies mood words (quiet, soft, slow, cozy, mellow, calm, romantic, chill, moody, emotional, love, nostalgia, relaxing, soothing, gentle). Ids 3,6,7,9,10,11,16,17 co-occur but their meaning is NOT determinable (e.g. 'k-pop'=[1,7,9,10,11], 'chill'=[2,3,16], 'nostalgia'=[2,6], 'soundtrack'=[1,17]). Only 219 queries observed -- always requested alone, 95% with an etag.
  6. kind 178 IDENTITY_TRAIT (130,846x 200) is a compact, cheap display-tuple that would let Wavee render a track/list row WITHOUT a full TrackV4 fetch: f1 = entity-class display string ('Song','Playlist','Episode'), f2 = entity display name, f3 = subtitle/blurb (rare -- 1 of 3000 samples, and empty-string on 13 of 13 'spotify:list' samples), f4 = parent {1=album/collection name, 2=album uri}, f5 = REPEATED creator {1=name, 2=uri} where the uri can be spotify:artist OR spotify:user (spotify:user:spotify for editorial lists). Requested for track/episode/list/show/playlist/album/user/podcast-chapter/local. Note 24x HTTP 451 (legal/regional block) on this kind -- the only kind besides EPISODE_V4 (52x 451) to ever return 451.
  7. kind 179 VISUAL_IDENTITY_TRAIT (130,689x 200) is a per-entity image set PLUS a fully-baked color scheme -- it obsoletes a separate fetchExtractedColors round-trip for anything that carries it. f1.1 = REPEATED image {1{1=url}, 2=size enum}. Size enum correlation is exact over 349 decoded samples: 1 -> ab67616d00004851 (64px album art) / ab6761610000f178 (artist) / ab67706c00006c11 (playlist) / mosaic.scdn.co/64/; 2 -> ab67616d00001e02 (300px) / ab67616100005174 / ab67706c0000da84 / mosaic/300/; 3 -> ab67616d0000b273 (640px) / ab6761610000e5eb / mosaic/640/; 4 -> playlist ab67706c000097ac / mosaic/1280/ only. f1.2 = color scheme block with three variants (f1.2.1, f1.2.2, f1.2.3), each holding five RGBA quads {1=r,2=g,3=b,4=a}, plus f1.2.4 = one standalone RGBA. Within every variant, slot 3 is always pure white (255,255,255,255) and slot 5 of variant 3 is Spotify green (30,215,96,255) in every sample examined; slots 1 and 2 are the entity-derived dark/darker pair. The three variants are almost certainly (but NOT provably from the wire) contrast/theme tiers.
  8. kind 249 spotify.contentagnostic.v2.ContentExperienceTrait is enormous by volume (131,911x 200, third most-requested kind overall, co-requested on EVERY track-row batch) yet carries a SINGLE varint f1. Correlation is perfectly clean: track -> 1, episode -> 2, podcast-chapter -> 2 (219 samples across 3 sources). It is an entity-experience enum, functionally redundant with the URI scheme. Wavee gains nothing by implementing it, but a Wavee client that wants to look wire-identical to the real client must send it in the batch.
  9. kind 220 EntityTypeTrait is likewise a single varint f1: track -> 1 (56 samples), podcast-chapter -> 14 (20 samples). No other values observed.
  10. kind 98 AUDIO_ASSOCIATIONS is requested at colossal volume (11,869 queries, always for spotify:track, always alongside 85 ORIGINAL_VIDEO + 99 VIDEO_ASSOCIATIONS + 182 + 222) but returned 404 on 11,868 of 11,869. The ONE 200 (all.saz session 1849, spotify:track:7xaLZCwnjb1RVVsZDxfeR9) decodes as f1{ f1='spotify:track:4P6SImVjdXLhfeGfWzU51q' (a DIFFERENT track -- the association target), f2{ repeated f1 = image {1=raw 20-byte image-id bytes, 2=size enum 0/1/2, 3=width, 4=height} with (0,600,600),(1,128,128),(2,1280,1280) } }. Note the size enum here is 0-based and the image id is RAW BYTES (ab67616d00001e02..., ab67616d00004851..., ab67616d0000b273...) NOT a URL -- a different encoding from kind 179. 1 sample only; the shape of any repeated-association case is unknown. Never sent with an etag.
  11. kind 16 CANVAS_V1 (spotify.canvaz.cache.EntityCanvazResponse.Canvaz) -- 191x 200, 426x 304, 475x 404 (canvas coverage is roughly 29% of queried tracks). Full field map: f1 = 32-hex canvas id; f2 = 'https://canvaz.scdn.co/upload/{artist|licensor}/<artistId>/video/<canvasId>.cnvs.mp4' (also observed .../image/... for a still canvas); f3 = a second 32-hex digest (differs from f1; 190/191 present) -- likely a content/file hash; f4 = varint, always 3 (probably a type enum, only one value seen); f5 = the track uri; f6 = uploader {1=artist uri, 2=artist name, 3=artist image url}; f7 = varint 1, present on only 2 of 191 samples (meaning unknown); f8 = 'artist' or 'licensor' (matches the URL path segment); f11 = 'spotify:canvas:<id>'; f13 = REPEATED rendition {1=width, 2=height, 3=url}, always exactly two entries at 512x288 and 256x144. Fields 9, 10, 12 never observed.
  12. kind 222 audio_attributes.v2.AudioAttributes decoded at scale (9,842x 200, 2,056x 304, 11,898 queries -- always track, co-requested with 85/98/99/182, and NOT only under the mix lens as previously believed): f1 = fixed64 double BPM (observed 80.0 - 145.38, many exact integers like 125.0/120.0/140.0 plus fitted values like 128.001, 97.3368); f2 = key {f2.1 = pitch-class string ('C','D','A','E','G','F','G#'...), 12 distinct; f2.2 = varint mode, values 1 and 2 only (minor/major); f2.3 = camelot {1 = wheel code string, 24 distinct: '1B'..'12B','8A'...; 2 = hex color string, 24 distinct, one per code: '#ee82d9','#56d9f8','#a0b6ff','#04ebeb','#deca73','#05eccb','#cb90ff','#f1ace7'}}. This is a ready-made, per-track BPM+key+camelot-color chip.
  13. kind 28 CUEPOINTS (spotify.automix.proto.Cuepoints, 6 samples, track) is a genuine automix/DJ-transition descriptor: f1 = start cue, f2 = end cue, f3 = REPEATED (105 across 6 tracks) start-side candidates, f4 = REPEATED (114) end-side candidates. Every cue has the same inner shape {1 = varint position in milliseconds (f1 710-10570 near the head, f2 175731-237152 near the tail), 2 = fixed32 float BPM (identical within a track: 133.601, 93.0, 77.9, 99.564, 87.0, 109.959, 117.089), 3 = varint always 1 (present on all but one entry per list), 4 = fixed64 double confidence 0.0-1.0 where the f1/f2 pick is not always the max}.
  14. PODCAST/AUDIOBOOK GATING KINDS THAT NEVER RETURNED A PAYLOAD IN ANY CAPTURE -- 404 or 400 on 100% of queries: 30 EPISODE_ACCESS (1,644 queries, all 404), 58 CONTENT_WARNING (1,196, all 404), 31 SHOW_ACCESS (516, all 404), 22 PODCAST_SUBSCRIPTIONS (126, all 404), 88 AUDIOBOOK_RELATIONS (3, 404), 52 AUDIOBOOK_SPECIFICS (3, 404), 64 AUDIOBOOK_PRICE (3, HTTP 400), 27 AUTOMIX_MODE (2, 404). Their payload shape is unobtainable from this corpus. Separately, 164 GATED_ENTITY_RELATIONS returned 852 HTTP 200s whose Any.value is ZERO BYTES (all 852) -- a 200-with-empty-message means 'no gating relations', so the populated shape is also unknown. Same for 83 AUDIOBOOK_GENRE (1x 200, empty), 108 PODCAST_SPONSORED_CONTENT (1x 200, empty), 20 PODCAST_AD_SEGMENTS (1x 200, empty).
  15. kind 4 PODCAST_SEGMENTS: 855x 200, ZERO 304s, ttl 7776000 (90 days -- the longest cache_ttl of any kind observed). Every one of the 855 payloads contains ONLY f1 = the episode uri. No segment array was ever populated in this corpus, so the segment shape is unknown. Never sent with an etag despite the 90-day TTL.
  16. kind 54 HTML_DESCRIPTION (163x 200, 343x 304): f2 = raw HTML episode/show description string ('<p>Speak&#39;s Lowest Price Promo Is Here!</p><p>&#x1f517;Purchase link: https://bit.ly/4w...'). f1 never observed populated. 4 of 163 contained non-UTF8-printable bytes so the dumper classified them as bytes; they are still HTML ('<p>South Korea has the b...', '<p>Russia&#39;s war econ...'). This is directly user-visible copy Wavee has no source for today.
  17. kind 138 PRERELEASE (spotify.prerelease.extension.Prerelease, 2x 200 of 5 queries -- 1 sample per distinct entity, so treat as 1 sample): f1 = 'spotify:prerelease:0iqKCCqFwlqzSnJgV22Nmh'; f2{1 = varint 1788472800 = unix release timestamp}; f3 = release-candidate block {1 = 'spotify:album:0qi1ztU4S08zA1FsP1DUaY', 2 = 'ALBUM', 3 = 'ARE YOU EVER COMING BACK?', 4 = {1 = artist uri, 2 = 'vaultboy'}, 5 = REPEATED cover {1 = i.scdn.co url, 2 = SIZE NAME STRING 'SMALL'|'DEFAULT'|'LARGE', 3 = width, 4 = height} with (SMALL,128,128),(DEFAULT,600,600),(LARGE,1280,1280)}. Note the size names here are STRINGS, unlike the integer enums in kinds 179 and 98 -- three different image-size encodings across three kinds.
  18. kind 29 PODCAST_POLL (1 sample, spotify.polls.PodcastPoll) is fully user-visible and fully decoded: f1{ 1 = varint poll id 2394214; 2 = '2026-07-26T23:00:00' start (naive ISO string, no timezone); 3 = '2026-08-03T22:59:59' end; 5 = episode uri; 6 = episode uri (repeated context, same value); 7 = question text "Venezuela's official oil ledger this year contains one entry. How would you rate its bookkeeping?"; 8 = varint 1; 9 = REPEATED option { 1 = option text ('Refreshingly concise','Better than mine, honestly','Under audit','$13 billion? Never heard of her'), 2 = varint 5/1/6 (vote count or ordinal -- ambiguous at n=1), 3 = varint 2394214 back-pointer to poll id, 4 = varint sequential option id 7157911..7157914 }; 10 = varint 2; 12 = 'spotify:poll:1eR3o3HerVMrg8JtHqjY86' }.
  19. kind 37 PODCAST_RATING (4 samples, shows): f1 = aggregate { 1 = fixed64 double average 4.5578/4.8600/4.8601/4.9937, 2 = varint rating count 3887/1829/1830/2382, 3 = varint 1 }; f2 = THE CURRENT USER'S OWN RATING, present on 1 of 4 shows: { 2 = show uri, 3 = varint 5 (the user's star value), 4 = { 1 = varint 1785242701 unix seconds, 2 = varint 434485250 nanos } }; f3 = varint 1. This is a write-capable surface (user rating) Wavee has no equivalent for.
  20. kind 183 PUBLISHING_METADATA_TRAIT (14x 200): f1{3{1=year 2026, 2=month 7|9, 3=day 27|4}} = a structured release date; f2{1 = unix seconds}; f3{1 = unix seconds, 2 = nanos 938000000} = two distinct timestamps (f2 present on 11/14, f3 on 14/14); f4 = copyright line, only 2 samples, values '© 2026 broke' and '℗ 2026 broke' -- i.e. f4 is REPEATED with the C-copyright and P-phonogram lines as separate entries. Requested for show / album / podcast-chapter, 87% with an etag.
  21. kind 246 CurationExperienceTrait (11x 200, show + podcast-chapter): f1 = the entity's own uri; f2 { 2{1 = 'spotify:playlist:37i9dQZF1FgnTBfUlzkeKt'} for podcast-chapters, or 3{1 = 'show'} for the show sample }; f3 { 4 { 1 = REPEATED playlist uri, 2 entries per sample: 'spotify:playlist:37i9dQZF1F5p3rmiWPIYgZ' and 'spotify:playlist:37i9dQZF1CIl0ks0ynmzpD' } }. Reads as 'which curated surfaces this chapter belongs to / is surfaced in', but the 1-vs-2 slot distinction is not determinable at n=11.
  22. kind 114 WATCH_FEED_ENTITY_EXPLORER (4x 200) is a swipeable-preview entry point: f3{ 1 = 'spotify:watch-feed:playlist:<id>?itemId=<base64 of a spotify:track: uri>' (base64 decodes to a truncated 'spotify:track:...' string), 2{1 = canvaz.scdn.co video url} present on 2 of 4, 3{1 = preview image or canvas url, 2 = the literal string 'video'}, 4 = fixed CTA subtitle 'Swipe through previews of tracks in this playlist.' (identical in all 4), 5 = 'Explore <playlist name>' }. f1 and f2 at the top level never observed.
  23. kind 149 ROOTLISTABILITY_TRAIT: single varint f1, 1 for 191 of 192 playlists and 0 for the one 'spotify:list:' entity -- 'can this be added to your rootlist/library'. kind 78 PLAYABILITY (1 sample, show): f1=1, f2=1. kind 86 SMART_SHUFFLE: 43x 200 but 41 of them are ZERO-LENGTH payloads; only 2 (both playlists) carried f1=1. kind 225 MixState: 238x 200 of which only 1 carried f1=1, the rest empty. kind 170 AUTO_LENS: 11x 200, 6 carried f1='mix', 5 empty. These four are all effectively boolean-with-empty-means-false.
  24. kind 185 ON_PLATFORM_REPUTATION_TRAIT (100x 200, all tracks) carries exactly ONE field: f3 = varint, 95 distinct values across 100 samples, range observed 141184618 - 2138988345. Almost certainly a play/stream count, but nothing in the wire proves the unit and f1/f2 are absent, so treat the magnitude as unverified.
  25. kind 80 SHARE_TRAIT (1,570x 200, near-exclusively episodes): payload is 37-40 bytes and contains ONLY f1{f1 = the entity's own uri}. No share URL, no share text, no template was ever present. Despite the name it is not directly useful as-is.
  26. Three mix-lens payloads carry raw signal blobs, not structured data, and are only worth implementing for a DJ/waveform feature: kind 217 mixbeats.Beats (6 samples) = f1 varint 4 (beats per bar), f2 REPEATED 2757 beats across 6 tracks {1 = fixed32 float timestamp seconds, 3 = varint beat position 1-4, 4 = fixed32 float 1.0 confidence, 5 = fixed32 float 1.0 on 691 of them}, f3 = 32-hex analysis id. kind 218 mixvocalactivity.VocalActivity = f1 fixed32 22050.0 (sample rate), f2 varint 1 (channels), f4 varint 315 (frame/hop), f5 = an opaque packed byte array (values cluster around 0x0d/0x0e). kind 237 mixthreebandwaveforms.ThreeBandWaveforms = f1 varint 44100 (sample rate), f2 varint 20 (bands/resolution), f3/f4/f5 = three ~10KB opaque packed arrays (the three bands). kind 219 mixability.Mixability = f1 varint 1, f2 fixed64 1.0. kind 142 LIST_TUNER_AUDIO_ANALYSIS = f2 varint 20, f3 = a single ~10KB opaque blob. kind 136 TRANSITION_MAPS (spotify:audio entities) = f1{1{1=32-hex id}, 2{2=32-hex id}, 3=varint 1, 4{1,2 = two envelope blocks each {1 = opaque packed array, 2 = varint 22050 sample rate}}, 5{1=40-hex sha1}, 6{1=40-hex sha1}}.
  27. kind 113 COMPANION_CONTENT (1 sample) contained ONLY f1 = the episode uri -- no companion content. Do not implement from this evidence.

**kinds:**

  ---
  - **kind:** 179
  - **name:** VISUAL_IDENTITY_TRAIT
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.VisualIdentityTrait
  - **entityTypes:** spotify:track (130313), spotify:episode (1683), spotify:local (86), spotify:show (75), spotify:artist (31), spotify:playlist (28), spotify:album (22), spotify:podcast-chapter (10), spotify:list (1)
  - **entityCount:** 132249
  - **status:** mixed
  - **cacheTtl:** cache_ttl 600 (208), 3600 (76), 60 (75), 10800 (6), plus long per-entity values ~72000-86000; offline_ttl 2592000 always
  - **payloadShape:** f1.1 = REPEATED image {1{1=str url}, 2=varint size enum}. Size enum -> url prefix, exact over 349 samples: 1=ab67616d00004851(64px album)/ab6761610000f178(artist)/ab67706c00006c11(playlist)/mosaic.scdn.co/64/; 2=ab67616d00001e02(300)/ab67616100005174/ab67706c0000da84/mosaic/300/ and ab67c0de0000deef(pickasso topic art); 3=ab67616d0000b273(640)/ab6761610000e5eb/ab6765630000ba8a/mosaic/640/; 4=ab67706c000097ac|0000bebb(playlist only)/mosaic/1280/. f1.2 = color scheme: f1.2.1, f1.2.2, f1.2.3 are three variants each holding five RGBA quads {1=r,2=g,3=b,4=a}; slot 3 is always 255,255,255,255; variant3 slot5 is always 30,215,96,255 (Spotify green); slots 1-2 are entity-derived. f1.2.4 = one standalone RGBA (e.g. 208,216,200,255). No top-level field other than f1 ever observed.

  ---
  - **kind:** 249
  - **name:** UNKNOWN (ContentExperienceTrait)
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.ContentExperienceTrait
  - **entityTypes:** spotify:track (131049), spotify:episode (955), spotify:local (86), spotify:playlist (54), spotify:album (17), spotify:podcast-chapter (20), spotify:artist (11), spotify:collection (3)
  - **entityCount:** 132195
  - **status:** mixed
  - **cacheTtl:** cache_ttl 86400 (131986), 3600 (209); offline_ttl 2592000
  - **payloadShape:** f1 = varint ONLY. track -> 1; episode -> 2; podcast-chapter -> 2. No other field ever present in 3000 decoded samples.

  ---
  - **kind:** 178
  - **name:** IDENTITY_TRAIT
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.IdentityTrait
  - **entityTypes:** spotify:track (130049), spotify:episode (906), spotify:local (86), spotify:list (25), spotify:podcast-chapter (20), spotify:user (17), spotify:show (75), spotify:playlist (16), spotify:artist (4), spotify:album (3)
  - **entityCount:** 131201
  - **status:** mixed (also 24x HTTP 451 and 4x 404)
  - **cacheTtl:** cache_ttl 60 (938), 604800 (85), 3600 (83), 300 (40), plus long per-entity values ~72000-86000; offline_ttl 2592000
  - **payloadShape:** f1 = str entity-class label: 'Song' | 'Playlist' | 'Episode'. f2 = str display name ('Not the Only One', 'SLANDER Popular'). f3 = str subtitle/blurb, RARE (1 of 3000; empty-string on all 13 spotify:list samples). f4 = msg parent {1 = str album/collection name, 2 = str 'spotify:album:<id>'} (absent for list entities). f5 = REPEATED msg creator {1 = str display name, 2 = str uri} -- uri is spotify:artist:<id> for tracks, spotify:user:spotify for editorial lists; up to 2+ per entity.

  ---
  - **kind:** 222
  - **name:** UNKNOWN (audio_attributes v2)
  - **typeUrl:** type.googleapis.com/spotify.playlistmixing.extensions.audio_attributes.v2.AudioAttributes
  - **entityTypes:** spotify:track only
  - **entityCount:** 11898
  - **status:** mixed
  - **cacheTtl:** cache_ttl 21600 uniformly; offline_ttl 2592000
  - **payloadShape:** f1 = fixed64 double BPM (80.0-145.38; both exact integers 125.0/120.0/140.0 and fitted 128.001, 97.3368, 99.9159). f2 = key msg {1 = str pitch class, 12 distinct ('C','D','A','E','G','F','G#'...); 2 = varint mode, only 1 and 2 observed; 3 = camelot msg {1 = str wheel code, 24 distinct ('1B'..'12B','8A'..); 2 = str hex color, 24 distinct, one-to-one with the code ('#ee82d9','#56d9f8','#a0b6ff','#04ebeb','#cb90ff','#deca73','#05eccb','#f1ace7')}}.

  ---
  - **kind:** 98
  - **name:** AUDIO_ASSOCIATIONS
  - **typeUrl:** type.googleapis.com/spotify.bumblebee.audio_associations.v1.AudioAssociations
  - **entityTypes:** spotify:track only
  - **entityCount:** 11869
  - **status:** 200-payload on 1 of 11869; the other 11868 are HTTP 404 (never a 304)
  - **cacheTtl:** cache_ttl 86400 (11868), 75725 (1); offline_ttl 2592000
  - **payloadShape:** 1 SAMPLE ONLY (all.saz sid 1849, entity spotify:track:7xaLZCwnjb1RVVsZDxfeR9). f1 = association msg { f1 = str 'spotify:track:4P6SImVjdXLhfeGfWzU51q' (a DIFFERENT track -- the associated audio), f2 = msg { REPEATED f1 = image {1 = RAW 20 BYTES image id (ab67616d00001e02e48a..., ab67616d00004851e48a..., ab67616d0000b273e48a...) NOT a url, 2 = varint size enum 0|1|2, 3 = varint width, 4 = varint height} with (0,600,600),(1,128,128),(2,1280,1280) } }. Note the 0-based enum and raw-bytes id differ from kind 179. Never sent with an etag.

  ---
  - **kind:** 164
  - **name:** GATED_ENTITY_RELATIONS
  - **typeUrl:** type.googleapis.com/spotify.gatedentityrelations.v1.GatedEntityRelations
  - **entityTypes:** spotify:episode (1646), spotify:track (100), spotify:show (9)
  - **entityCount:** 1755
  - **status:** mixed (852x 200, 796x 304, 100x 404)
  - **cacheTtl:** cache_ttl 3600 (1639), 21600 (100), 1800 (16); offline_ttl 2592000
  - **payloadShape:** ALL 852 of the 200 payloads have Any.value of length 0 -- zero fields. The populated shape is NOT observable from this corpus. 45% of queries carry an etag.

  ---
  - **kind:** 80
  - **name:** SHARE_TRAIT
  - **typeUrl:** type.googleapis.com/spotify.traits.v1.ShareTrait
  - **entityTypes:** spotify:episode (1646), spotify:show (3), spotify:playlist (1)
  - **entityCount:** 1650
  - **status:** mixed
  - **cacheTtl:** cache_ttl 600 (1638), 300 (8), 60 (3), 86400 (1); offline_ttl 2592000
  - **payloadShape:** f1 = msg {f1 = str the entity's OWN uri}. Payload is 37-40 bytes. No share url, share text, or template ever present in 1570 decoded samples.

  ---
  - **kind:** 30
  - **name:** EPISODE_ACCESS
  - **typeUrl:** 
  - **entityTypes:** spotify:episode (1641), spotify:show (3)
  - **entityCount:** 1644
  - **status:** no payload ever: HTTP 404 on 1644 of 1644, never a 200 or 304
  - **cacheTtl:** cache_ttl 3600; offline_ttl 2592000
  - **payloadShape:** 

  ---
  - **kind:** 239
  - **name:** UNKNOWN (ContentCapabilityTrait)
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.ContentCapabilityTrait
  - **entityTypes:** spotify:track (957), spotify:episode (316), spotify:show (3)
  - **entityCount:** 1276
  - **status:** mixed
  - **cacheTtl:** cache_ttl 3600 (579), 10800 (105), plus per-entity ~70700-81800; offline_ttl 2592000
  - **payloadShape:** Four capability slots f1-f4; in each slot the SET FIELD NUMBER is the verdict and f3 (when set) is a restriction map {1=reason key, 2=value}. Exact shapes across 665 samples -- track: 1{2=<empty>}, 2{3{1{1='offline',2='1'}}}, 3{1=<empty>}, 4{4=<empty>} OR 4{3{1{1='music-video-disabled',2='1'}}}. episode: 1{3=<empty>}, 2{1=<empty>}, 3{1=<empty>}, 4{4=<empty>} OR 4{3{1{1='other-video-disabled',2='1'}}}. show (1 sample): 1{3}, 2{2}, 3{1}, 4{4}. Slot 2 = offline/download capability (restricted with reason 'offline'='1' on 399/399 track samples, premium NL account). Slot 4 = video capability. Slots 1 and 3 carry only a verdict, never a reason -- meaning not determinable. 48% of queries carry an etag.

  ---
  - **kind:** 58
  - **name:** CONTENT_WARNING
  - **typeUrl:** 
  - **entityTypes:** spotify:episode only
  - **entityCount:** 1196
  - **status:** no payload ever: HTTP 404 on 1196 of 1196
  - **cacheTtl:** cache_ttl 302400 (3.5 days); offline_ttl 2592000
  - **payloadShape:** 

  ---
  - **kind:** 16
  - **name:** CANVAS_V1
  - **typeUrl:** type.googleapis.com/spotify.canvaz.cache.EntityCanvazResponse.Canvaz
  - **entityTypes:** spotify:track (1091), spotify:episode (1)
  - **entityCount:** 1092
  - **status:** mixed (191x 200, 426x 304, 475x 404 -- ~29% of queried tracks actually have a canvas)
  - **cacheTtl:** cache_ttl 3600 uniformly; offline_ttl 2592000
  - **payloadShape:** f1 = str 32-hex canvas id. f2 = str 'https://canvaz.scdn.co/upload/{artist|licensor}/<artistId>/video/<canvasId>.cnvs.mp4' (also .../image/... variant seen). f3 = str a SECOND 32-hex digest, differs from f1, present 190/191. f4 = varint, always 3. f5 = str the track uri. f6 = msg uploader {1 = artist uri, 2 = artist display name, 3 = artist image url}. f7 = varint 1, present on only 2 of 191. f8 = str 'artist' | 'licensor' (matches the f2 path segment). f11 = str 'spotify:canvas:<id>'. f13 = REPEATED msg rendition {1 = varint width, 2 = varint height, 3 = str url}, always exactly two: 512x288 and 256x144. Fields 9, 10, 12 never observed.

  ---
  - **kind:** 4
  - **name:** PODCAST_SEGMENTS
  - **typeUrl:** type.googleapis.com/spotify.podcast_segments.PodcastSegments
  - **entityTypes:** spotify:episode only
  - **entityCount:** 855
  - **status:** 200-payload (855x 200, zero 304s)
  - **cacheTtl:** cache_ttl 7776000 (90 days -- longest observed of any kind); offline_ttl 2592000
  - **payloadShape:** f1 = str the episode uri. That is ALL -- in every one of 855 decoded 200 payloads. No segment array was ever populated, so the segment shape is unknown. Never sent with an etag.

  ---
  - **kind:** 225
  - **name:** UNKNOWN (mixstate.MixState)
  - **typeUrl:** type.googleapis.com/spotify.playlistmixing.extensions.mixstate.MixState
  - **entityTypes:** spotify:playlist only
  - **entityCount:** 724
  - **status:** mixed (238x 200, 486x 304)
  - **cacheTtl:** cache_ttl 600 uniformly; offline_ttl 2592000
  - **payloadShape:** Effectively a boolean: 1 of 8 sampled 200 payloads carried f1 = varint 1; the other 7 were zero-length (empty message = false/off). 67% of queries carry an etag.

  ---
  - **kind:** 31
  - **name:** SHOW_ACCESS
  - **typeUrl:** 
  - **entityTypes:** spotify:episode (510), spotify:show (6)
  - **entityCount:** 516
  - **status:** no payload ever: HTTP 404 on 516 of 516
  - **cacheTtl:** cache_ttl 3600; offline_ttl 2592000
  - **payloadShape:** 

  ---
  - **kind:** 54
  - **name:** HTML_DESCRIPTION
  - **typeUrl:** type.googleapis.com/spotify.podcast.extensions.PodcastHtmlDescription
  - **entityTypes:** spotify:episode (510), spotify:show (3)
  - **entityCount:** 513
  - **status:** mixed (163x 200, 343x 304)
  - **cacheTtl:** cache_ttl 3600 uniformly; offline_ttl 2592000
  - **payloadShape:** f2 = str raw HTML description, e.g. '<p>Speak&#39;s Lowest Price Promo Is Here!</p><p>&#x1f517;Purchase link: https://bit.ly/4w...'. Also seen as plain text without tags ('Hosted by Tyler Measom and Liz Iacuzzi, Was I In A Cult? is a documentary-style podcast...'). 4 of 163 contain non-printable bytes so require lenient UTF-8 decode. f1 never observed populated. 66% of queries carry an etag.

  ---
  - **kind:** 21
  - **name:** EPISODE_TRANSCRIPTS
  - **typeUrl:** type.googleapis.com/spotify.corex.transcripts.metadata.EpisodeTranscript
  - **entityTypes:** spotify:episode (510), spotify:show (3)
  - **entityCount:** 513
  - **status:** mixed (188x 200, 318x 304)
  - **cacheTtl:** cache_ttl 3600 (318), 300 (195); offline_ttl 2592000
  - **payloadShape:** f1 = str the entity uri (present on all 188). f2 = REPEATED msg transcript, present on only 12 of 188 samples: {1 = str 'spotify:transcript:<id>'; 2 = str locale, 4 distinct ('en-us','de-de','fr-fr','pt-br'); 4 = str 'https://episode-transcripts.spotifycdn.com/1.0/spotify:transcript:<id>'; 6 = str 'https://spclient.wg.spotify.com/transcript-read-along/v2/episode/<episodeId>' with an optional '/de','/fr','/pt' language suffix}. f2.3 and f2.5 never observed. 61% of queries carry an etag.

  ---
  - **kind:** 149
  - **name:** ROOTLISTABILITY_TRAIT
  - **typeUrl:** type.googleapis.com/spotify.traits.v1.RootlistabilityTrait
  - **entityTypes:** spotify:playlist (228), spotify:list (1)
  - **entityCount:** 229
  - **status:** mixed (221x 200, 8x 304)
  - **cacheTtl:** cache_ttl 86400 uniformly; offline_ttl 2592000
  - **payloadShape:** f1 = varint. 1 for 191 of 192 decoded (all spotify:playlist); 0 for the single spotify:list entity. Reads as 'can be added to the rootlist/library'.

  ---
  - **kind:** 6
  - **name:** TRACK_DESCRIPTOR
  - **typeUrl:** type.googleapis.com/spotify.descriptorextension.ExtensionDescriptorData
  - **entityTypes:** spotify:track only
  - **entityCount:** 219
  - **status:** 200-payload (214x 200, no 304 delivered despite 95% of queries carrying an etag)
  - **cacheTtl:** cache_ttl 86400 uniformly; offline_ttl 2592000
  - **payloadShape:** REPEATED f1 = descriptor { 1 = str lowercase slug ('quiet','soft','cozy','mellow','k-pop','chill','nostalgia','dance pop','emotional','soundtrack'); 2 = fixed32 float confidence 0.0166-0.9749, sorted descending within a track; 3 = PACKED repeated varint category ids; 4 = str 'spotify:concept:<id>'; 5 = str Title-Cased display label ('Quiet','K-Pop','Cozy') }. Category id 1 accompanies every genre slug and id 2 accompanies every mood slug across all 43 descriptors in 6 tracks; ids 3,6,7,9,10,11,16,17 also occur ('k-pop'=[1,7,9,10,11], 'chill'=[2,3,16], 'nostalgia'=[2,6], 'soundtrack'=[1,17]) but their meaning is NOT determinable.

  ---
  - **kind:** 22
  - **name:** PODCAST_SUBSCRIPTIONS
  - **typeUrl:** 
  - **entityTypes:** spotify:track (75), spotify:episode (41), spotify:podcast-chapter (10)
  - **entityCount:** 126
  - **status:** no payload ever: HTTP 404 on 126 of 126
  - **cacheTtl:** cache_ttl 3600; offline_ttl 2592000
  - **payloadShape:** 

  ---
  - **kind:** 220
  - **name:** UNKNOWN (EntityTypeTrait)
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.EntityTypeTrait
  - **entityTypes:** spotify:track (105), spotify:podcast-chapter (20)
  - **entityCount:** 125
  - **status:** mixed (76x 200, 49x 304)
  - **cacheTtl:** cache_ttl 86400 uniformly; offline_ttl 2592000
  - **payloadShape:** f1 = varint ONLY. track -> 1 (56 samples); podcast-chapter -> 14 (20 samples). No other values.

  ---
  - **kind:** 185
  - **name:** ON_PLATFORM_REPUTATION_TRAIT
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.OnPlatformReputationTrait
  - **entityTypes:** spotify:track only
  - **entityCount:** 100
  - **status:** 200-payload (100x 200, zero 304)
  - **cacheTtl:** cache_ttl 3600 uniformly; offline_ttl 2592000
  - **payloadShape:** f3 = varint ONLY (f1 and f2 never present). 95 distinct values in 100 samples, range 141184618 - 2138988345. Magnitude is consistent with a stream/play count but the wire gives no unit or label -- unverified.

  ---
  - **kind:** 183
  - **name:** PUBLISHING_METADATA_TRAIT
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.PublishingMetadataTrait
  - **entityTypes:** spotify:show (74), spotify:podcast-chapter (10), spotify:album (1)
  - **entityCount:** 85
  - **status:** mixed (14x 200, 71x 304)
  - **cacheTtl:** cache_ttl 60 (74), 600 (10), 3600 (1); offline_ttl 2592000
  - **payloadShape:** f1 { 3 { 1 = varint year 2026, 2 = varint month 7|9, 3 = varint day 27|4 } } structured release date, on 11 of 14. f2 { 1 = varint unix seconds 1785168000|1788472800 } on 11 of 14. f3 { 1 = varint unix seconds (1785168590, 1784472240, 1784599200, 1784793600, 1788472800), 2 = varint nanos 938000000 } on 14 of 14. f4 = REPEATED str copyright line, only 2 samples: '© 2026 broke' and '℗ 2026 broke'. 87% of queries carry an etag.

  ---
  - **kind:** 142
  - **name:** LIST_TUNER_AUDIO_ANALYSIS
  - **typeUrl:** type.googleapis.com/spotify.playlist.tuner.extension.ListTunerAudioAnalysis
  - **entityTypes:** spotify:track only
  - **entityCount:** 56
  - **status:** 200-payload (56x 200)
  - **cacheTtl:** cache_ttl 3600; offline_ttl 2592000
  - **payloadShape:** f2 = varint 20 (constant across samples). f3 = a single ~9.4-12.4 KB OPAQUE packed binary blob that does not parse as nested protobuf. Contents not determinable without the real .proto.

  ---
  - **kind:** 237
  - **name:** UNKNOWN (mixthreebandwaveforms)
  - **typeUrl:** type.googleapis.com/spotify.playlistmixing.extensions.mixthreebandwaveforms.ThreeBandWaveforms
  - **entityTypes:** spotify:track only
  - **entityCount:** 52
  - **status:** 200-payload (52x 200)
  - **cacheTtl:** cache_ttl 3600; offline_ttl 2592000
  - **payloadShape:** f1 = varint 44100 (sample rate). f2 = varint 20 (bands/resolution). f3, f4, f5 = three ~10.1-10.7 KB OPAQUE packed byte arrays -- the three frequency-band waveform envelopes. Not nested protobuf.

  ---
  - **kind:** 86
  - **name:** SMART_SHUFFLE
  - **typeUrl:** type.googleapis.com/spotify.smartshuffle.SmartShuffle
  - **entityTypes:** spotify:list (13), spotify:album (11), spotify:playlist (10), spotify:station (6), spotify:track (3), spotify:internal (2)
  - **entityCount:** 45
  - **status:** mixed (43x 200, 2x 304)
  - **cacheTtl:** cache_ttl 86400 uniformly; offline_ttl 2592000
  - **payloadShape:** Boolean-with-empty-means-false: 41 of 43 200 payloads are ZERO-LENGTH; only 2 (both spotify:playlist) carry f1 = varint 1.

  ---
  - **kind:** 170
  - **name:** AUTO_LENS
  - **typeUrl:** type.googleapis.com/spotify.autolensextension.v1.AutoLens
  - **entityTypes:** spotify:playlist (33), spotify:list (1)
  - **entityCount:** 34
  - **status:** mixed (11x 200, 23x 304)
  - **cacheTtl:** cache_ttl 60 uniformly; offline_ttl 2592000
  - **payloadShape:** f1 = str lens id. Only value ever seen: 'mix' (6 of 11 200s); the other 5 payloads are zero-length (no lens). 85% of queries carry an etag.

  ---
  - **kind:** 246
  - **name:** UNKNOWN (CurationExperienceTrait)
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.CurationExperienceTrait
  - **entityTypes:** spotify:podcast-chapter (10), spotify:show (3)
  - **entityCount:** 13
  - **status:** mixed (11x 200, 2x 304)
  - **cacheTtl:** cache_ttl 86400; offline_ttl 2592000
  - **payloadShape:** f1 = str the entity's own uri. f2 = msg, either {2{1 = str 'spotify:playlist:37i9dQZF1FgnTBfUlzkeKt'}} (all 10 podcast-chapters) or {3{1 = str 'show'}} (the 1 show). f3 = msg {4 {1 = REPEATED str playlist uri; 2 entries per sample: 'spotify:playlist:37i9dQZF1F5p3rmiWPIYgZ' and 'spotify:playlist:37i9dQZF1CIl0ks0ynmzpD'}} on 10 of 11. The f2.2-vs-f2.3 distinction is not determinable at n=11.

  ---
  - **kind:** 114
  - **name:** WATCH_FEED_ENTITY_EXPLORER
  - **typeUrl:** type.googleapis.com/spotify.watchfeedextensions.api.v1.EntityExplorerEntrypointResponse
  - **entityTypes:** spotify:playlist (12), spotify:list (1)
  - **entityCount:** 13
  - **status:** mixed (4x 200, 7x 304, 2x 404)
  - **cacheTtl:** cache_ttl 10800; offline_ttl 2592000
  - **payloadShape:** f3 = msg entrypoint { 1 = str 'spotify:watch-feed:playlist:<playlistId>?itemId=<base64>' where the base64 decodes to a truncated 'spotify:track:<id>' string; 2 = msg {1 = str canvaz.scdn.co video url} present on 2 of 4; 3 = msg {1 = str preview image or canvas url, 2 = str literal 'video'}; 4 = str CTA subtitle, identical in all 4: 'Swipe through previews of tracks in this playlist.'; 5 = str 'Explore <playlist name>' }. Top-level f1 and f2 never observed.

  ---
  - **kind:** 217
  - **name:** UNKNOWN (mixbeats.Beats)
  - **typeUrl:** type.googleapis.com/spotify.playlistmixing.extensions.mixbeats.Beats
  - **entityTypes:** spotify:track only
  - **entityCount:** 6
  - **status:** 200-payload (6x 200)
  - **cacheTtl:** cache_ttl 3600; offline_ttl 2592000
  - **payloadShape:** 6 samples. f1 = varint 4 (beats per bar). f2 = REPEATED beat (2757 entries across the 6 tracks) {1 = fixed32 float timestamp in seconds (107.32, 127.47, 137.32, 145.38...); 3 = varint beat position within bar, values 1-4; 4 = fixed32 float 1.0 (confidence); 5 = fixed32 float 1.0, present on 691 of 2757}. f3 = str 32-hex analysis id, one per track.

  ---
  - **kind:** 218
  - **name:** UNKNOWN (mixvocalactivity)
  - **typeUrl:** type.googleapis.com/spotify.playlistmixing.extensions.mixvocalactivity.VocalActivity
  - **entityTypes:** spotify:track only
  - **entityCount:** 6
  - **status:** 200-payload (6x 200)
  - **cacheTtl:** cache_ttl 3600; offline_ttl 2592000
  - **payloadShape:** 6 samples. f1 = fixed32 float 22050.0 (sample rate). f2 = varint 1 (channels). f4 = varint 315 (frame/hop count). f5 = OPAQUE packed byte array, ~14-15 KB, byte values clustering on 0x0d/0x0e -- a per-frame vocal-activity level track.

  ---
  - **kind:** 28
  - **name:** CUEPOINTS
  - **typeUrl:** type.googleapis.com/spotify.automix.proto.Cuepoints
  - **entityTypes:** spotify:track only
  - **entityCount:** 6
  - **status:** 200-payload (6x 200)
  - **cacheTtl:** cache_ttl 1800; offline_ttl 2592000
  - **payloadShape:** 6 samples. f1 = chosen start cue, f2 = chosen end cue, f3 = REPEATED start-side candidates (105 across 6 tracks), f4 = REPEATED end-side candidates (114). Every cue shares one shape: {1 = varint position in MILLISECONDS (f1 range 710-10570 near the head; f2 range 175731-237152 near the tail); 2 = fixed32 float BPM, identical within a track (133.601, 93.0, 77.9, 99.564, 87.0, 109.959, 117.089); 3 = varint 1, on all but one entry per list; 4 = fixed64 double confidence 0.0-1.0, and the chosen f1/f2 is NOT always the highest-confidence candidate}.

  ---
  - **kind:** 219
  - **name:** UNKNOWN (mixability.Mixability)
  - **typeUrl:** type.googleapis.com/spotify.playlistmixing.extensions.mixability.Mixability
  - **entityTypes:** spotify:track only
  - **entityCount:** 6
  - **status:** 200-payload (6x 200)
  - **cacheTtl:** cache_ttl 21600; offline_ttl 2592000
  - **payloadShape:** 6 samples, all identical: f1 = varint 1, f2 = fixed64 double 1.0. No variation observed so the value range is unknown.

  ---
  - **kind:** 136
  - **name:** TRANSITION_MAPS
  - **typeUrl:** type.googleapis.com/spotify.playback_platform.transition.v1.TransitionMaps
  - **entityTypes:** spotify:audio (the file/audio uri, not spotify:track)
  - **entityCount:** 5
  - **status:** 200-payload (5x 200)
  - **cacheTtl:** cache_ttl 86400; offline_ttl 2592000
  - **payloadShape:** 5 samples. f1 = transition map {1{1 = str 32-hex id}; 2{2 = str 32-hex id} (note the inner field number differs: f1.1.1 vs f1.2.2); 3 = varint 1; 4 = msg {1 and 2 = two envelope blocks, each {1 = OPAQUE packed array, 2 = varint 22050 sample rate}}; 5{1 = str 40-hex sha1}; 6{1 = str 40-hex sha1}}.

  ---
  - **kind:** 138
  - **name:** PRERELEASE
  - **typeUrl:** type.googleapis.com/spotify.prerelease.extension.Prerelease
  - **entityTypes:** spotify:show (3), spotify:album (1), spotify:prerelease (1)
  - **entityCount:** 5
  - **status:** mixed (2x 200, 3x 404); the two 200s are the same prerelease so treat as 1 distinct sample
  - **cacheTtl:** cache_ttl 3600 (3), 300 (2); offline_ttl 2592000
  - **payloadShape:** f1 = str 'spotify:prerelease:0iqKCCqFwlqzSnJgV22Nmh'. f2 = msg {1 = varint 1788472800 unix seconds = release moment}. f3 = msg release candidate {1 = str 'spotify:album:0qi1ztU4S08zA1FsP1DUaY'; 2 = str 'ALBUM'; 3 = str 'ARE YOU EVER COMING BACK?'; 4 = msg {1 = str artist uri, 2 = str 'vaultboy'}; 5 = REPEATED msg cover {1 = str i.scdn.co url, 2 = str SIZE NAME 'SMALL'|'DEFAULT'|'LARGE', 3 = varint width, 4 = varint height} with (SMALL,128,128),(DEFAULT,600,600),(LARGE,1280,1280)}. Size is a STRING here, unlike the integer enums in kinds 179 and 98.

  ---
  - **kind:** 37
  - **name:** PODCAST_RATING
  - **typeUrl:** type.googleapis.com/spotify.ratings.PodcastRating
  - **entityTypes:** spotify:show only
  - **entityCount:** 4
  - **status:** 200-payload (4x 200)
  - **cacheTtl:** cache_ttl 3600; offline_ttl 2592000
  - **payloadShape:** 4 samples. f1 = aggregate {1 = fixed64 double average rating (4.557757, 4.860033, 4.860109, 4.993703); 2 = varint rating count (3887, 1829, 1830, 2382); 3 = varint 1}. f2 = THE CURRENT USER'S OWN RATING, present on 1 of 4 shows: {2 = str show uri; 3 = varint 5 (the user's star value); 4 = msg timestamp {1 = varint 1785242701 seconds, 2 = varint 434485250 nanos}}. f3 = varint 1. 75% of queries carry an etag.

  ---
  - **kind:** 3
  - **name:** PODCAST_TOPICS
  - **typeUrl:** type.googleapis.com/spotify.podcast.extensions.PodcastTopics
  - **entityTypes:** spotify:show only
  - **entityCount:** 3
  - **status:** mixed (1x 200, 2x 304)
  - **cacheTtl:** cache_ttl 43200; offline_ttl 2592000
  - **payloadShape:** 1 SAMPLE. REPEATED f1 = topic {1 = str 'spotify:genre:0JQ5DAqbMKFNr6gDrHHVKL', 2 = str 'Comedy'}. Only one topic present in the single sample.

  ---
  - **kind:** 78
  - **name:** PLAYABILITY
  - **typeUrl:** type.googleapis.com/spotify.bumblebee.playability.v1.Playability
  - **entityTypes:** spotify:show only
  - **entityCount:** 3
  - **status:** mixed (1x 200, 2x 304)
  - **cacheTtl:** cache_ttl 600 (2), 60 (1); offline_ttl 2592000
  - **payloadShape:** 1 SAMPLE: f1 = varint 1, f2 = varint 1. No variation observed, so the not-playable encoding is unknown.

  ---
  - **kind:** 83
  - **name:** AUDIOBOOK_GENRE
  - **typeUrl:** type.googleapis.com/spotify.audiobookgenres.AudiobookGenres
  - **entityTypes:** spotify:show only
  - **entityCount:** 3
  - **status:** mixed (1x 200, 2x 304) but the single 200 payload is ZERO BYTES
  - **cacheTtl:** cache_ttl 129600 (36h); offline_ttl 2592000
  - **payloadShape:** 

  ---
  - **kind:** 88
  - **name:** AUDIOBOOK_RELATIONS
  - **typeUrl:** 
  - **entityTypes:** spotify:show only
  - **entityCount:** 3
  - **status:** no payload ever: HTTP 404 on 3 of 3
  - **cacheTtl:** cache_ttl 3600; offline_ttl 2592000
  - **payloadShape:** 

  ---
  - **kind:** 52
  - **name:** AUDIOBOOK_SPECIFICS
  - **typeUrl:** 
  - **entityTypes:** spotify:show only
  - **entityCount:** 3
  - **status:** no payload ever: HTTP 404 on 3 of 3
  - **cacheTtl:** cache_ttl 600; offline_ttl 2592000
  - **payloadShape:** 

  ---
  - **kind:** 64
  - **name:** AUDIOBOOK_PRICE
  - **typeUrl:** 
  - **entityTypes:** spotify:show only
  - **entityCount:** 3
  - **status:** no payload ever: HTTP 400 (not 404) on 3 of 3
  - **cacheTtl:** cache_ttl 86400; offline_ttl 2592000
  - **payloadShape:** 

  ---
  - **kind:** 27
  - **name:** AUTOMIX_MODE
  - **typeUrl:** 
  - **entityTypes:** spotify:playlist only
  - **entityCount:** 2
  - **status:** no payload ever: HTTP 404 on 2 of 2
  - **cacheTtl:** cache_ttl 79937 and 101373 (per-entity); offline_ttl 2592000
  - **payloadShape:** 

  ---
  - **kind:** 29
  - **name:** PODCAST_POLL
  - **typeUrl:** type.googleapis.com/spotify.polls.PodcastPoll
  - **entityTypes:** spotify:episode only
  - **entityCount:** 1
  - **status:** 200-payload (1 sample)
  - **cacheTtl:** cache_ttl 3600; offline_ttl 2592000
  - **payloadShape:** 1 SAMPLE, fully decoded. f1 = poll {1 = varint id 2394214; 2 = str start '2026-07-26T23:00:00' (naive ISO, no tz); 3 = str end '2026-08-03T22:59:59'; 5 = str episode uri; 6 = str same episode uri; 7 = str question "Venezuela's official oil ledger this year contains one entry. How would you rate its bookkeeping?"; 8 = varint 1; 9 = REPEATED option {1 = str option text ('Refreshingly concise','Better than mine, honestly','Under audit','$13 billion? Never heard of her'); 2 = varint 5|1|6 (vote count or ordinal -- ambiguous at n=1); 3 = varint 2394214 back-pointer to the poll id; 4 = varint sequential option id 7157911-7157914}; 10 = varint 2; 12 = str 'spotify:poll:1eR3o3HerVMrg8JtHqjY86'}.

  ---
  - **kind:** 113
  - **name:** COMPANION_CONTENT
  - **typeUrl:** type.googleapis.com/spotify.figs.companion_content.v0.CompanionContent
  - **entityTypes:** spotify:episode only
  - **entityCount:** 1
  - **status:** 200-payload (1 sample)
  - **cacheTtl:** cache_ttl 60; offline_ttl 2592000
  - **payloadShape:** 1 SAMPLE: f1 = str 'spotify:episode:5AUBkkFGq9GlIq4gF9T1oH' (the entity's own uri). No companion content present.

  ---
  - **kind:** 108
  - **name:** PODCAST_SPONSORED_CONTENT
  - **typeUrl:** type.googleapis.com/spotify.sponsoredcontentlistener.v1.SponsoredContentListenerPayload
  - **entityTypes:** spotify:episode only
  - **entityCount:** 1
  - **status:** 200-payload but ZERO BYTES (1 sample)
  - **cacheTtl:** cache_ttl 86400; offline_ttl 2592000
  - **payloadShape:** 

  ---
  - **kind:** 20
  - **name:** PODCAST_AD_SEGMENTS
  - **typeUrl:** type.googleapis.com/spotify.ads.formats.PodcastAds
  - **entityTypes:** spotify:episode only
  - **entityCount:** 1
  - **status:** 200-payload but ZERO BYTES (1 sample)
  - **cacheTtl:** cache_ttl 1800; offline_ttl 2592000
  - **payloadShape:** 

**requestPatterns:**
  1. 2718 POST /extended-metadata/v0/extended-metadata requests across the 12 named .saz files plus the pre-extracted spotify.saz loose dir. All 2718 carried the IDENTICAL header f1 = {f1='NL' (country), f2='premium' (catalogue)}; f3 (task_id) never populated.
  2. MAX BATCH SIZE IS 300 ENTITIES for every multi-kind batch (795 requests hit exactly 300), and 500 for the single-kind TRACK_V4-only batch (12 requests at exactly 500). No request exceeded 500. Batch-size distribution: 300 (795x), 1 (644x), 5 (105x), 2 (97x), 3 (74x), 6 (52x), 4 (40x), 9 (31x), 12 (28x), 20 (26x). Batches just under 300 (291-299) are common for TRACK_V4-only, i.e. the client pages a list into <=300-entity chunks.
  3. THE TRACK-ROW SURFACE (playlist/album/liked/search track lists) is one canonical 6-kind batch, 398 occurrences, 396 of them at exactly 300 entities: {10 TRACK_V4, 178 IDENTITY_TRAIT, 179 VISUAL_IDENTITY_TRAIT, 182 CONSUMPTION_EXPERIENCE_TRAIT, 212 PLAYBACK_TRAIT, 249 ContentExperienceTrait}. Wavee consumes only 3 of those 6 (10, 182, 212) and is missing 178/179/249 on the highest-volume path in the whole client.
  4. A 5-kind variant WITHOUT TrackV4 (the entity is already cached): {178, 179, 182, 212, 249} -- 37 occurrences, 24 at exactly 300 entities.
  5. THE VIDEO SURFACE is a distinct 5-kind batch, 46 occurrences, 37 at exactly 300 entities: {85 ORIGINAL_VIDEO, 98 AUDIO_ASSOCIATIONS, 99 VIDEO_ASSOCIATIONS, 182 CONSUMPTION_EXPERIENCE_TRAIT, 222 AudioAttributes}. Wavee consumes 85, 99, 182 -- it is missing 98 (which 404s 11868/11869 times anyway) and 222 (which 200s 9842 times and carries BPM/key/camelot).
  6. THE CANVAS SURFACE is a 4-kind batch, 27 occurrences: {16 CANVAS_V1, 98 AUDIO_ASSOCIATIONS, 99 VIDEO_ASSOCIATIONS, 239 ContentCapabilityTrait}. Note 239 is co-requested with the canvas/video kinds -- consistent with 239 slot 4 being the video-capability gate ('music-video-disabled' / 'other-video-disabled').
  7. THE PLAYLIST-HEADER SURFACE adds rootlistability and mix state: {149 ROOTLISTABILITY_TRAIT, 178, 179, 182, 212, 225 MixState, 249} -- 17 occurrences, 6 at exactly 300 entities.
  8. Single-kind batches dominate the long tail: TRACK_V4 alone 1428x, ALBUM_V4 alone 98x, 249 alone 83x, SMART_SHUFFLE alone 45x, 178 alone 31x, 182 alone 30x, 179 alone 30x, 220 alone 21x, 99 alone 20x, 164 alone 19x, EPISODE_V4 alone 19x, 225 alone 18x, 170 AUTO_LENS alone 17x, 22 alone 16x, 185 alone 16x, 239 alone 15x, ARTIST_V4 alone 15x. The client does NOT always co-batch related kinds -- many traits are fetched in their own request.
  9. ETAG (conditional-request) DISCIPLINE IS HIGHLY UNEVEN and correlates with payload volatility, not volume. Near-always conditional: 6 TRACK_DESCRIPTOR 95%, 183 PUBLISHING_METADATA 87%, 170 AUTO_LENS 85%, 37 PODCAST_RATING 75%, 11 SHOW_V4 72%, 225 MixState 67%, 54 HTML_DESCRIPTION 66%, 3/78/83 66%, 21 EPISODE_TRANSCRIPTS 61%, 114 WATCH_FEED 61%, 12 EPISODE_V4 52%, 239 ContentCapabilityTrait 48%, 164 GATED_ENTITY_RELATIONS 45%, 16 CANVAS_V1 39%, 220 39%. Almost never conditional despite huge volume: 10 TRACK_V4 5%, 182 2%, 179 0.8%, 178 0.17%, 249 0.05%, 212 0.02%. NEVER conditional at all (0 etags ever sent): 98 AUDIO_ASSOCIATIONS, 30 EPISODE_ACCESS, 31 SHOW_ACCESS, 58 CONTENT_WARNING, 4 PODCAST_SEGMENTS (despite its 90-day cache_ttl), 22 PODCAST_SUBSCRIPTIONS, 142, 237, 205 LIST_METADATA_V2, 15 USER_PROFILE, 28, 217, 218, 219, 136, 138, 151, 52, 64, 88, 27.
  10. offline_ttl is a CONSTANT 2592000 (30 days) on every single kind and every single entity observed -- it is not a per-kind tuning knob. cache_ttl is where the variation lives, and for the metadata kinds (10, 9, 178, 179, 182, 212, 85, 99) it is a per-entity randomized value in the ~72000-86400 band (jittered ~1 day) rather than a fixed constant; the trait/gating kinds use fixed values (3600, 600, 60, 86400, 21600, 10800, 1800, 300).
  11. The client asks for kinds it will almost never get: 98 AUDIO_ASSOCIATIONS costs 11,869 query slots for ONE 200; 30/58/31/22 cost 3,482 query slots for zero payloads; 164 costs 1,755 slots for 852 empty-message 200s. Wavee should NOT replicate these purely for parity -- they are pure batch overhead on this account/market (NL/premium).
