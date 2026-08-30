# Extended-metadata: complete kind census

> Workflow agent output, run `wf_5a5408b2-258`.

**findings:**
  1. SCOPE: 2718 POST https://gew4-spclient.spotify.com/extended-metadata/v0/extended-metadata request/response pairs were decoded across all 12 named .saz captures plus the 1147-session pre-extracted spotify.saz raw dir. Every one of the 2718 had a matching HTTP 200 response envelope. 59 distinct extension kinds were observed. All numbers below are totals over that whole corpus.
  2. The request header (BatchedEntityRequest.f1) was ALWAYS exactly {f1='NL', f2='premium', f3=<16 raw bytes>}. task_id is a fresh random 16-byte value on every single request (2718/2718 were 16 bytes) - it is not a hash of the batch and is never echoed anywhere useful.
  3. offline_ttl_in_seconds (EntityExtensionData.header.f5) was 2592000 (30 days) in EVERY response of EVERY kind, without exception. It is a constant, not per-kind tuning. Wavee can hardcode it.
  4. cache_ttl behaves in two distinct regimes. (a) The big catalog kinds - 10 TRACK_V4, 9 ALBUM_V4, 8 ARTIST_V4, 85 ORIGINAL_VIDEO, 99 VIDEO_ASSOCIATIONS, 178, 179, 182 - come back with a per-entity JITTERED ttl (TRACK_V4: 11662 distinct values spread over 60..86399; ORIGINAL_VIDEO: 8045 distinct over 72000..86398). The server deliberately spreads expiry. (b) Trait/podcast kinds return a fixed constant (e.g. 220/246/5/136/149 = 86400, 239 mostly 86400/3600, 170 = 60, 225 = 600, 4 = 7776000 = 90 days, 58 = 302400).
  5. 205 LIST_METADATA_V2 is the ONLY kind that returns NO cache_ttl field at all (absent in all 18 responses); it carries only the 2592000 offline_ttl.
  6. A 200 status does NOT imply a payload. Five kinds returned 200 with an ABSENT google.protobuf.Any.value (type_url present, value field omitted = defaults-only message): 85 ORIGINAL_VIDEO (10069/10069 200s empty - this is the single highest-volume 200 in the corpus and it never once carried bytes), 164 GATED_ENTITY_RELATIONS (8/8 empty), 20 PODCAST_AD_SEGMENTS (1/1), 83 AUDIOBOOK_GENRE (1/1), 108 PODCAST_SPONSORED_CONTENT (1/1), and 86 SMART_SHUFFLE (41 of 43 empty, 2 carried f1=1). For these kinds the SIGNAL is 'a 200 came back at all', not the bytes. Wavee must not treat an empty Any as a decode failure.
  7. Eight kinds NEVER produced a payload in any capture - every response was 404 (or 400): 22 PODCAST_SUBSCRIPTIONS (126x404), 27 AUTOMIX_MODE (2x404), 30 EPISODE_ACCESS (544x404), 31 SHOW_ACCESS (54x404), 52 AUDIOBOOK_SPECIFICS (3x404), 58 CONTENT_WARNING (96x404), 64 AUDIOBOOK_PRICE (3x400), 88 AUDIOBOOK_RELATIONS (3x404). Their shapes are unknown from this corpus.
  8. 98 AUDIO_ASSOCIATIONS is effectively dead weight: 11869 entities requested, 11868 returned 404, exactly 1 returned a payload. 99 VIDEO_ASSOCIATIONS is nearly as bad: 12085 requested -> 1406x404, 930x304, only 135 real payloads. Both share an identical shape (f1{f1 = source spotify:track uri, f2{repeated f1{f1 = 20-byte image/video id, f2 = size-enum, f3 = width, f4 = height}}}); the discriminator is the id prefix - ab67616d... = album art (audio assoc), ab6742d3... = video still (video assoc), and video assoc carries 16:9 dims (2560x1440, 1280x720) vs square for audio.
  9. The response envelope uses HTTP-style per-entity status codes beyond 200/304/404: 451 was observed 52x on 12 EPISODE_V4 and 24x on 178 IDENTITY_TRAIT (legal/geo block), and 400 on 64 AUDIOBOOK_PRICE (3x) and 151 RECOMMENDED_PLAYLISTS (1x, when asked against a spotify:playlist instead of spotify:album). Wavee's parser must handle 451 and 400 per-entity, not just 200/304/404.
  10. ETag is per (entity, kind) - ExtensionQuery.f2 - never per request. Usage is wildly uneven: 182 sent an etag on 3467 of 143196 queries, 222 on 2073/11898, 225 on 487/724, 170 on 29/34, 6 TRACK_DESCRIPTOR on 210/219, but 98 AUDIO_ASSOCIATIONS, 30, 31, 58, 4, 15, 22, 27, 28, 29, 88 sent an etag ZERO times. The client only bothers with etags for kinds it actually caches.
  11. NEW KIND not in the previously-established list: 237 = type.googleapis.com/spotify.playlistmixing.extensions.mixthreebandwaveforms.ThreeBandWaveforms. 52 track entities requested, 6 responses in the pairing, 2 real 200 payloads (max 30989 bytes). Shape: f1 varint 44100 (sample rate), f2 varint 20, f3/f4/f5 = three ~10KB opaque byte blobs (low/mid/high band waveform envelopes). It is part of the same DJ/Mix lens family as 217/218/219/222/225.
  12. 138 PRERELEASE now HAS an observed 200 payload (it was 404-only in the earlier pass). type_url = type.googleapis.com/spotify.prerelease.extension.Prerelease, requested against spotify:prerelease:, spotify:album: and spotify:show:. Shape: f1 = 'spotify:prerelease:<id>', f2{f1 = unix release timestamp 1788472800}, f3{f1 album uri, f2 'ALBUM', f3 title, f4{f1 artist uri, f2 artist name}, repeated f5{f1 image url, f2 'DEFAULT'|'SMALL'|'LARGE', f3 width, f4 height}}.
  13. 212 PLAYBACK_TRAIT is far richer than a 'trait' - it is a full playback bundle. Max payload 1086B. Shape: repeated f1/f2 = {f1 varint kind, f2{f2{repeated f1{f1 20-byte file_id, f2 format enum, f3 byte size}, f2 16-byte gid, f3/f4 10-byte blobs}, f3 = spotify:track uri, f6 = spotify:track uri}} PLUS f3 = an EMBEDDED transition map {f1{f1 16B gid}, f2{f2 16B gid}, f3 varint, f4{f1/f2 = two ~180B loudness/gain curve blobs}, f5{f1 20B file_id}, f6{f1 20B file_id}} - i.e. the same shape 136 TRANSITION_MAPS returns standalone. 212 is where the client gets file ids + crossfade curves for a row without a separate metadata call.
  14. Requested URI schemes beyond the obvious: spotify:local: (86 entities each on 178/179/182/212/249 - the client hydrates LOCAL FILES through extended-metadata, and they all come back with no data), spotify:podcast-chapter: (20 entities on 178/182/183/212/220/246/249), spotify:collection: (3 entities on 249), spotify:list: (13 on 86 SMART_SHUFFLE, 25 on 178, 1 on 149/170/179/114), spotify:station: (6 on 86), spotify:internal: (2 on 86), spotify:audio: (205 on 5 AUDIO_FILES, 5 on 136 TRANSITION_MAPS - these two are the ONLY kinds ever asked about spotify:audio:), spotify:user: (11 on 15 USER_PROFILE, 17 on 178), spotify:prerelease: (1 on 138).
  15. 170 AUTO_LENS payload is a bare string. The only value ever observed on the wire is 'mix' (f1 str 'mix'), with cache_ttl 60. This is the gate for the 217/218/219/222/225/237 mix-lens family, confirmed again here.

**kinds:**

  ---
  - **kind:** 3
  - **name:** PODCAST_TOPICS
  - **typeUrl:** type.googleapis.com/spotify.podcast.extensions.PodcastTopics
  - **entityTypes:** spotify:show
  - **entityCount:** 3
  - **status:** mixed (1x200, 2x304)
  - **cacheTtl:** cache_ttl=43200; offline_ttl=2592000
  - **payloadShape:** repeated f1{f1 str topic uri 'spotify:genre:0JQ5DAqbMKFNr6gDrHHVKL', f2 str display name 'Comedy'} (1 sample)

  ---
  - **kind:** 4
  - **name:** PODCAST_SEGMENTS
  - **typeUrl:** type.googleapis.com/spotify.podcast_segments.PodcastSegments
  - **entityTypes:** spotify:episode
  - **entityCount:** 855
  - **status:** 200-payload (555x200, 0x304)
  - **cacheTtl:** cache_ttl=7776000 (90d); offline_ttl=2592000
  - **payloadShape:** f1 str episode uri only; max payload seen 40B across all 555 200s - no actual segment list ever present in this corpus

  ---
  - **kind:** 5
  - **name:** AUDIO_FILES
  - **typeUrl:** type.googleapis.com/spotify.extendedmetadata.audiofiles.AudioFilesExtensionResponse
  - **entityTypes:** spotify:audio
  - **entityCount:** 205
  - **status:** mixed (144x200, 61x304)
  - **cacheTtl:** cache_ttl=86400; offline_ttl=2592000
  - **payloadShape:** repeated f1{f1{f1 20-byte file_id, f2 varint format enum (0,1,2,8,16)}, f4 varint byte size}, f2{f1 fixed32 float, f2 fixed32 float} (loudness pair), f3{f1 fixed32, f2 fixed32}, f4{sparse fixed64s}

  ---
  - **kind:** 6
  - **name:** TRACK_DESCRIPTOR
  - **typeUrl:** type.googleapis.com/spotify.descriptorextension.ExtensionDescriptorData
  - **entityTypes:** spotify:track
  - **entityCount:** 219
  - **status:** 200-payload (6x200)
  - **cacheTtl:** cache_ttl=86400; offline_ttl=2592000
  - **payloadShape:** repeated f1{f1 str slug 'soundtrack', f2 fixed32 float score 0.94, f3 bytes flags, f4 str 'spotify:concept:<id>', f5 str display 'Soundtrack'}; 12 descriptors in the sample

  ---
  - **kind:** 8
  - **name:** ARTIST_V4
  - **typeUrl:** type.googleapis.com/spotify.metadata.Artist
  - **entityTypes:** spotify:artist
  - **entityCount:** 41
  - **status:** mixed (38x200, 3x304)
  - **cacheTtl:** cache_ttl jittered 3251..86015 (40 distinct); offline_ttl=2592000
  - **payloadShape:** standard spotify.metadata.Artist (not re-decoded here)

  ---
  - **kind:** 9
  - **name:** ALBUM_V4
  - **typeUrl:** type.googleapis.com/spotify.metadata.Album
  - **entityTypes:** spotify:album
  - **entityCount:** 2274
  - **status:** mixed (1778x200, 415x304)
  - **cacheTtl:** cache_ttl jittered 3600..86393 (1994 distinct); offline_ttl=2592000
  - **payloadShape:** standard spotify.metadata.Album (not re-decoded here)

  ---
  - **kind:** 10
  - **name:** TRACK_V4
  - **typeUrl:** type.googleapis.com/spotify.metadata.Track
  - **entityTypes:** spotify:track
  - **entityCount:** 231636
  - **status:** mixed (18589x200, 7677x304, 1x404)
  - **cacheTtl:** cache_ttl jittered 60..86399 (11662 distinct); offline_ttl=2592000
  - **payloadShape:** standard spotify.metadata.Track (not re-decoded here). By far the highest-volume kind: 231636 entity queries.

  ---
  - **kind:** 11
  - **name:** SHOW_V4
  - **typeUrl:** type.googleapis.com/spotify.metadata.Show
  - **entityTypes:** spotify:show
  - **entityCount:** 18
  - **status:** mixed (5x200, 13x304)
  - **cacheTtl:** cache_ttl=600; offline_ttl=2592000
  - **payloadShape:** standard spotify.metadata.Show

  ---
  - **kind:** 12
  - **name:** EPISODE_V4
  - **typeUrl:** type.googleapis.com/spotify.metadata.Episode
  - **entityTypes:** spotify:episode
  - **entityCount:** 3591
  - **status:** mixed (112x200, 1854x304, 52x451)
  - **cacheTtl:** cache_ttl=60|600|3600; offline_ttl=2592000
  - **payloadShape:** standard spotify.metadata.Episode. Notable: 52 entities returned per-entity status 451.

  ---
  - **kind:** 15
  - **name:** USER_PROFILE
  - **typeUrl:** type.googleapis.com/spotify.identity.v3.UserProfile
  - **entityTypes:** spotify:user
  - **entityCount:** 11
  - **status:** 200-payload (11x200)
  - **cacheTtl:** cache_ttl=21600; offline_ttl=2592000
  - **payloadShape:** f1{f1 str username 'spotify'}, f2{f1 str display name 'Spotify'}, repeated f3{f1 width, f2 height, f3 str i.scdn.co image url} (64x64 and 300x300 observed), f4/f6/f9/f10{f1 varint bool}, f11{f1 varint follower count 16085920}, f24{f1 str short id 'y84kXNyOqb'}

  ---
  - **kind:** 16
  - **name:** CANVAS_V1
  - **typeUrl:** type.googleapis.com/spotify.canvaz.cache.EntityCanvazResponse.Canvaz
  - **entityTypes:** spotify:track (1091), spotify:episode (1)
  - **entityCount:** 1092
  - **status:** mixed (191x200, 426x304, 475x404)
  - **cacheTtl:** cache_ttl=3600; offline_ttl=2592000
  - **payloadShape:** f1 str canvas id hex32, f2 str https://canvaz.scdn.co/upload/licensor/<licensorId>/video/<id>.cnvs.mp4, f3 str file hash hex32, f4 varint type (3), f5 str spotify:track uri, f6{f1 artist uri, f2 artist name, f3 artist image url}, f8 str 'licensor', f11 str 'spotify:canvas:<id>', repeated f13{f1 width, f2 height, f3 poster image url} (512x288 and 256x144)

  ---
  - **kind:** 20
  - **name:** PODCAST_AD_SEGMENTS
  - **typeUrl:** type.googleapis.com/spotify.ads.formats.PodcastAds
  - **entityTypes:** spotify:episode
  - **entityCount:** 1
  - **status:** 200 with EMPTY Any.value (1 sample)
  - **cacheTtl:** cache_ttl=1800; offline_ttl=2592000
  - **payloadShape:** Any.value field absent - defaults-only message

  ---
  - **kind:** 21
  - **name:** EPISODE_TRANSCRIPTS
  - **typeUrl:** type.googleapis.com/spotify.corex.transcripts.metadata.EpisodeTranscript
  - **entityTypes:** spotify:episode (510), spotify:show (3)
  - **entityCount:** 513
  - **status:** mixed (30x200, 21x304)
  - **cacheTtl:** cache_ttl=300|3600; offline_ttl=2592000
  - **payloadShape:** f1 str uri only; max payload 40B across all 30 200s - no transcript body ever inlined

  ---
  - **kind:** 22
  - **name:** PODCAST_SUBSCRIPTIONS
  - **typeUrl:** 
  - **entityTypes:** spotify:track (75), spotify:episode (41), spotify:podcast-chapter (10)
  - **entityCount:** 126
  - **status:** 404-only (126x404, never a payload)
  - **cacheTtl:** cache_ttl=3600; offline_ttl=2592000
  - **payloadShape:** 

  ---
  - **kind:** 27
  - **name:** AUTOMIX_MODE
  - **typeUrl:** 
  - **entityTypes:** spotify:playlist
  - **entityCount:** 2
  - **status:** 404-only (2x404)
  - **cacheTtl:** cache_ttl=79937|101373; offline_ttl=2592000
  - **payloadShape:** 

  ---
  - **kind:** 28
  - **name:** CUEPOINTS
  - **typeUrl:** type.googleapis.com/spotify.automix.proto.Cuepoints
  - **entityTypes:** spotify:track
  - **entityCount:** 6
  - **status:** 200-payload (6x200)
  - **cacheTtl:** cache_ttl=1800; offline_ttl=2592000
  - **payloadShape:** f1 = start cue {f1 varint ms offset, f2 fixed32 float, f3 varint 1, f4 fixed64 double weight}, f2 = end cue (same shape), repeated f3 = candidate intro cues, repeated f4 = candidate outro cues; ~40 cue entries in the 896B sample

  ---
  - **kind:** 29
  - **name:** PODCAST_POLL
  - **typeUrl:** type.googleapis.com/spotify.polls.PodcastPoll
  - **entityTypes:** spotify:episode
  - **entityCount:** 1
  - **status:** 200-payload (1 sample)
  - **cacheTtl:** cache_ttl=3600; offline_ttl=2592000
  - **payloadShape:** f1{f1 varint poll id 2394214, f2 str start '2026-07-26T23:00:00', f3 str end '2026-08-03T22:59:59', f5/f6 str episode uri, f7 str question text, f8 varint 1, repeated f9{f1 str option text, f2 varint vote count, f3 varint poll id, f4 varint option id}}

  ---
  - **kind:** 30
  - **name:** EPISODE_ACCESS
  - **typeUrl:** 
  - **entityTypes:** spotify:episode (1641), spotify:show (3)
  - **entityCount:** 1644
  - **status:** 404-only (544x404)
  - **cacheTtl:** cache_ttl=3600; offline_ttl=2592000
  - **payloadShape:** 

  ---
  - **kind:** 31
  - **name:** SHOW_ACCESS
  - **typeUrl:** 
  - **entityTypes:** spotify:episode (510), spotify:show (6)
  - **entityCount:** 516
  - **status:** 404-only (54x404)
  - **cacheTtl:** cache_ttl=3600; offline_ttl=2592000
  - **payloadShape:** 

  ---
  - **kind:** 37
  - **name:** PODCAST_RATING
  - **typeUrl:** type.googleapis.com/spotify.ratings.PodcastRating
  - **entityTypes:** spotify:show
  - **entityCount:** 4
  - **status:** 200-payload (4x200)
  - **cacheTtl:** cache_ttl=3600; offline_ttl=2592000
  - **payloadShape:** f1{f1 fixed64 double average rating, f2 varint rating count 3887, f3 varint 1}, f3 varint 1

  ---
  - **kind:** 52
  - **name:** AUDIOBOOK_SPECIFICS
  - **typeUrl:** 
  - **entityTypes:** spotify:show
  - **entityCount:** 3
  - **status:** 404-only (3x404)
  - **cacheTtl:** cache_ttl=600; offline_ttl=2592000
  - **payloadShape:** 

  ---
  - **kind:** 54
  - **name:** HTML_DESCRIPTION
  - **typeUrl:** type.googleapis.com/spotify.podcast.extensions.PodcastHtmlDescription
  - **entityTypes:** spotify:episode (510), spotify:show (3)
  - **entityCount:** 513
  - **status:** mixed (5x200, 46x304)
  - **cacheTtl:** cache_ttl=3600; offline_ttl=2592000
  - **payloadShape:** f2 str = HTML-escaped description body (entity-escaped &#34; &#39; &#64; plus <br/>). No f1 observed.

  ---
  - **kind:** 58
  - **name:** CONTENT_WARNING
  - **typeUrl:** 
  - **entityTypes:** spotify:episode
  - **entityCount:** 1196
  - **status:** 404-only (96x404)
  - **cacheTtl:** cache_ttl=302400; offline_ttl=2592000
  - **payloadShape:** 

  ---
  - **kind:** 64
  - **name:** AUDIOBOOK_PRICE
  - **typeUrl:** 
  - **entityTypes:** spotify:show
  - **entityCount:** 3
  - **status:** 400-only (3x400)
  - **cacheTtl:** cache_ttl=86400; offline_ttl=2592000
  - **payloadShape:** 

  ---
  - **kind:** 78
  - **name:** PLAYABILITY
  - **typeUrl:** type.googleapis.com/spotify.bumblebee.playability.v1.Playability
  - **entityTypes:** spotify:show
  - **entityCount:** 3
  - **status:** mixed (1x200, 2x304)
  - **cacheTtl:** cache_ttl=60|600; offline_ttl=2592000
  - **payloadShape:** f1 varint 1, f2 varint 1 (max payload 4B)

  ---
  - **kind:** 80
  - **name:** SHARE_TRAIT
  - **typeUrl:** type.googleapis.com/spotify.traits.v1.ShareTrait
  - **entityTypes:** spotify:episode (1646), spotify:show (3), spotify:playlist (1)
  - **entityCount:** 1650
  - **status:** mixed (192x200, 9x304, 1x404)
  - **cacheTtl:** cache_ttl=60|600|86400; offline_ttl=2592000
  - **payloadShape:** f1{f1 str canonical share uri e.g. 'spotify:episode:036m7ydvLW8RcITrGl8kGg'}; max 42B

  ---
  - **kind:** 83
  - **name:** AUDIOBOOK_GENRE
  - **typeUrl:** type.googleapis.com/spotify.audiobookgenres.AudiobookGenres
  - **entityTypes:** spotify:show
  - **entityCount:** 3
  - **status:** mixed (1x200 with EMPTY value, 2x304)
  - **cacheTtl:** cache_ttl=129600; offline_ttl=2592000
  - **payloadShape:** Any.value absent on the single 200

  ---
  - **kind:** 85
  - **name:** ORIGINAL_VIDEO
  - **typeUrl:** type.googleapis.com/spotify.bumblebee.originalvideo.v1.OriginalVideo
  - **entityTypes:** spotify:track
  - **entityCount:** 11742
  - **status:** mixed (10069x200 ALL EMPTY, 1673x304)
  - **cacheTtl:** cache_ttl jittered 72000..86398 (8045 distinct); offline_ttl=2592000
  - **payloadShape:** Any.value ABSENT on all 10069 200s. The 200 itself is the signal; no bytes ever observed.

  ---
  - **kind:** 86
  - **name:** SMART_SHUFFLE
  - **typeUrl:** type.googleapis.com/spotify.smartshuffle.SmartShuffle
  - **entityTypes:** spotify:list (13), spotify:album (11), spotify:playlist (10), spotify:station (6), spotify:track (3), spotify:internal (2)
  - **entityCount:** 45
  - **status:** mixed (43x200 of which 41 empty, 2x304)
  - **cacheTtl:** cache_ttl=86400; offline_ttl=2592000
  - **payloadShape:** f1 varint 1 (2B) when non-empty; otherwise Any.value absent

  ---
  - **kind:** 88
  - **name:** AUDIOBOOK_RELATIONS
  - **typeUrl:** 
  - **entityTypes:** spotify:show
  - **entityCount:** 3
  - **status:** 404-only (3x404)
  - **cacheTtl:** cache_ttl=3600; offline_ttl=2592000
  - **payloadShape:** 

  ---
  - **kind:** 98
  - **name:** AUDIO_ASSOCIATIONS
  - **typeUrl:** type.googleapis.com/spotify.bumblebee.audio_associations.v1.AudioAssociations
  - **entityTypes:** spotify:track
  - **entityCount:** 11869
  - **status:** mixed but effectively 404-only (11868x404, 1x200)
  - **cacheTtl:** cache_ttl=75725|86400; offline_ttl=2592000
  - **payloadShape:** f1{f1 str source spotify:track uri, f2{repeated f1{f1 20-byte image id (ab67616d... = album art), f2 varint size enum 0|1|2, f3 varint width, f4 varint height}}}; observed 600x600, 128x128, 1280x1280

  ---
  - **kind:** 99
  - **name:** VIDEO_ASSOCIATIONS
  - **typeUrl:** type.googleapis.com/spotify.bumblebee.video_associations.v1.VideoAssociations
  - **entityTypes:** spotify:track
  - **entityCount:** 12085
  - **status:** mixed (135x200, 930x304, 1406x404)
  - **cacheTtl:** cache_ttl jittered 211..86400 (1029 distinct); offline_ttl=2592000
  - **payloadShape:** Identical shape to kind 98: f1{f1 str spotify:track uri, f2{repeated f1{f1 20-byte id (ab6742d3... = video still), f2 varint size enum 0|2|4, f3 width, f4 height}}}; observed 2560x1440 and 1280x720 (16:9)

  ---
  - **kind:** 108
  - **name:** PODCAST_SPONSORED_CONTENT
  - **typeUrl:** type.googleapis.com/spotify.sponsoredcontentlistener.v1.SponsoredContentListenerPayload
  - **entityTypes:** spotify:episode
  - **entityCount:** 1
  - **status:** 200 with EMPTY Any.value (1 sample)
  - **cacheTtl:** cache_ttl=86400; offline_ttl=2592000
  - **payloadShape:** Any.value absent

  ---
  - **kind:** 113
  - **name:** COMPANION_CONTENT
  - **typeUrl:** type.googleapis.com/spotify.figs.companion_content.v0.CompanionContent
  - **entityTypes:** spotify:episode
  - **entityCount:** 1
  - **status:** 200-payload (1 sample)
  - **cacheTtl:** cache_ttl=60; offline_ttl=2592000
  - **payloadShape:** f1 str 'spotify:episode:5AUBkkFGq9GlIq4gF9T1oH' only (40B)

  ---
  - **kind:** 114
  - **name:** WATCH_FEED_ENTITY_EXPLORER
  - **typeUrl:** type.googleapis.com/spotify.watchfeedextensions.api.v1.EntityExplorerEntrypointResponse
  - **entityTypes:** spotify:playlist (12), spotify:list (1)
  - **entityCount:** 13
  - **status:** mixed (4x200, 7x304, 2x404)
  - **cacheTtl:** cache_ttl=10800; offline_ttl=2592000
  - **payloadShape:** f3{f1 str 'spotify:watch-feed:playlist:37i9dQZF1EVHGWrwldPRtj?itemId=<base64 of spotify:track:...>', f3{f1 str thumbnail i.scdn.co url, f2 str 'video'}, f4 str subtitle 'Swipe through previews of tracks in this playlist.', f5 str title 'Explore Chill Mix'}

  ---
  - **kind:** 136
  - **name:** TRANSITION_MAPS
  - **typeUrl:** type.googleapis.com/spotify.playback_platform.transition.v1.TransitionMaps
  - **entityTypes:** spotify:audio
  - **entityCount:** 5
  - **status:** 200-payload (5x200)
  - **cacheTtl:** cache_ttl=86400; offline_ttl=2592000
  - **payloadShape:** repeated f1{f1{f1 str from-file hex32}, f2{f2 str to-file hex32}, f3 varint 1, f4{f1{f1 sparse varint map of sample-offset -> sample-offset, f2 varint 22050}, f2{same shape}}, f5{f1 str 40-hex file_id}}. Same inner shape appears embedded as f3 of kind 212.

  ---
  - **kind:** 138
  - **name:** PRERELEASE
  - **typeUrl:** type.googleapis.com/spotify.prerelease.extension.Prerelease
  - **entityTypes:** spotify:show (3), spotify:album (1), spotify:prerelease (1)
  - **entityCount:** 5
  - **status:** mixed (2x200, 3x404)
  - **cacheTtl:** cache_ttl=300|3600; offline_ttl=2592000
  - **payloadShape:** f1 str 'spotify:prerelease:0iqKCCqFwlqzSnJgV22Nmh', f2{f1 varint unix release ts 1788472800}, f3{f1 str album uri, f2 str 'ALBUM', f3 str title, f4{f1 artist uri, f2 artist name}, repeated f5{f1 str image url, f2 str 'DEFAULT'|'SMALL'|'LARGE', f3 width, f4 height}}

  ---
  - **kind:** 142
  - **name:** LIST_TUNER_AUDIO_ANALYSIS
  - **typeUrl:** type.googleapis.com/spotify.playlist.tuner.extension.ListTunerAudioAnalysis
  - **entityTypes:** spotify:track
  - **entityCount:** 56
  - **status:** 200-payload (6x200)
  - **cacheTtl:** cache_ttl=3600; offline_ttl=2592000
  - **payloadShape:** f2 varint 20 (hz/bucket rate), f3 = ~12.4KB opaque byte blob (envelope samples). 12418B total in the sample.

  ---
  - **kind:** 149
  - **name:** ROOTLISTABILITY_TRAIT
  - **typeUrl:** type.googleapis.com/spotify.traits.v1.RootlistabilityTrait
  - **entityTypes:** spotify:playlist (228), spotify:list (1)
  - **entityCount:** 229
  - **status:** mixed (192x200, 8x304)
  - **cacheTtl:** cache_ttl=86400; offline_ttl=2592000
  - **payloadShape:** f1 varint 1 (max payload 2B) - a single enum

  ---
  - **kind:** 151
  - **name:** RECOMMENDED_PLAYLISTS
  - **typeUrl:** type.googleapis.com/spotify.artistsectionprovider.v1.RecommendedPlaylists
  - **entityTypes:** spotify:album (3), spotify:playlist (1)
  - **entityCount:** 4
  - **status:** mixed (3x200, 1x400 - the 400 was the spotify:playlist query)
  - **cacheTtl:** cache_ttl=300|600; offline_ttl=2592000
  - **payloadShape:** repeated f1{f1 str 'spotify:playlist:37i9dQZF1DX...'}; 6 playlists in the 258B sample

  ---
  - **kind:** 164
  - **name:** GATED_ENTITY_RELATIONS
  - **typeUrl:** type.googleapis.com/spotify.gatedentityrelations.v1.GatedEntityRelations
  - **entityTypes:** spotify:episode (1646), spotify:track (100), spotify:show (9)
  - **entityCount:** 1755
  - **status:** mixed (8x200 ALL EMPTY, 199x304, 100x404)
  - **cacheTtl:** cache_ttl=1800|3600|21600; offline_ttl=2592000
  - **payloadShape:** Any.value absent on all 8 200s

  ---
  - **kind:** 170
  - **name:** AUTO_LENS
  - **typeUrl:** type.googleapis.com/spotify.autolensextension.v1.AutoLens
  - **entityTypes:** spotify:playlist (33), spotify:list (1)
  - **entityCount:** 34
  - **status:** mixed (11x200 of which 6 non-empty, 23x304)
  - **cacheTtl:** cache_ttl=60; offline_ttl=2592000
  - **payloadShape:** f1 str lens name - only value ever observed on the wire is 'mix' (5B)

  ---
  - **kind:** 178
  - **name:** IDENTITY_TRAIT
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.IdentityTrait
  - **entityTypes:** spotify:track (130049), spotify:episode (906), spotify:local (86), spotify:show (75), spotify:list (25), spotify:podcast-chapter (20), spotify:user (17), spotify:playlist (16), spotify:artist (4), spotify:album (3)
  - **entityCount:** 131201
  - **status:** mixed (1915x200, 223x304, 24x451, 4x404)
  - **cacheTtl:** cache_ttl jittered 60..86370 (1730 distinct); offline_ttl=2592000
  - **payloadShape:** f1 str type label 'Song', f2 str title 'Know Me Too Well', f4{f1 str parent/album name, f2 str spotify:album uri}, repeated f5{f1 str artist name, f2 str spotify:artist uri}

  ---
  - **kind:** 179
  - **name:** VISUAL_IDENTITY_TRAIT
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.VisualIdentityTrait
  - **entityTypes:** spotify:track (130313), spotify:episode (1683), spotify:local (86), spotify:show (75), spotify:artist (31), spotify:playlist (28), spotify:album (22), spotify:podcast-chapter (10), spotify:list (1)
  - **entityCount:** 132249
  - **status:** mixed (349x200, 313x304, 2x404)
  - **cacheTtl:** cache_ttl jittered 60..86382 (298 distinct); offline_ttl=2592000
  - **payloadShape:** repeated f1{f1{f1 str i.scdn.co image url}, f2 varint size enum 1|2|3 (ab67616d00004851=small, ...00001e02=medium, ...0000b273=large)}, f2{repeated f1{f1{f1..f4 varint r,g,b,a}, f2{f1..f4 varint r,g,b,a}}} = extracted colour pairs (e.g. 180,149,68,255 / 155,124,41,255). Largest payload 1430B.

  ---
  - **kind:** 182
  - **name:** CONSUMPTION_EXPERIENCE_TRAIT
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.ConsumptionExperienceTrait
  - **entityTypes:** spotify:track (141382), spotify:episode (1683), spotify:local (86), spotify:playlist (16), spotify:album (5), spotify:artist (4), spotify:podcast-chapter (20)
  - **entityCount:** 143196
  - **status:** mixed (259x200, 1389x304, 3x404)
  - **cacheTtl:** cache_ttl jittered 211..86396 (1269 distinct); offline_ttl=2592000
  - **payloadShape:** tiny: 8-13B. f1{f1 varint duration-ish value (175, 970 observed)}, f2{empty}, f4{small opaque bytes 0x04 / 0x0204}. Size distribution 9B x164, 10B x50, 8B x21, 12B x14, 13B x8, 11B x2.

  ---
  - **kind:** 183
  - **name:** PUBLISHING_METADATA_TRAIT
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.PublishingMetadataTrait
  - **entityTypes:** spotify:show (74), spotify:podcast-chapter (10), spotify:album (1)
  - **entityCount:** 85
  - **status:** mixed (14x200, 71x304)
  - **cacheTtl:** cache_ttl=60|600|3600; offline_ttl=2592000
  - **payloadShape:** f3{f1 varint unix publish timestamp 1784472240} (8B)

  ---
  - **kind:** 185
  - **name:** ON_PLATFORM_REPUTATION_TRAIT
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.OnPlatformReputationTrait
  - **entityTypes:** spotify:track
  - **entityCount:** 100
  - **status:** 200-payload (100x200, 0x304)
  - **cacheTtl:** cache_ttl=3600; offline_ttl=2592000
  - **payloadShape:** f3 varint play count 2138988345 (6B). This is the track play-count source.

  ---
  - **kind:** 205
  - **name:** LIST_METADATA_V2
  - **typeUrl:** type.googleapis.com/spotify.list.v1.model.Attributes
  - **entityTypes:** spotify:playlist
  - **entityCount:** 18
  - **status:** 200-payload (18x200)
  - **cacheTtl:** NO cache_ttl field at all; offline_ttl=2592000 (unique - only kind with no cache_ttl)
  - **payloadShape:** f3 str playlist name 'Satang Mix (사탕 믹스)', f4 str description, f5{f1 varint timestamp 1784626084}, f7 str owner 'spotify', f9{f1/f2/f4/f5/f6 varint flags, f8{numbered sub-permission map f1..f18 each {f1 varint 0}}}; 1035B sample

  ---
  - **kind:** 212
  - **name:** PLAYBACK_TRAIT
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.PlaybackTrait
  - **entityTypes:** spotify:track (130901), spotify:episode (906), spotify:local (86), spotify:playlist (16), spotify:album (5), spotify:artist (4), spotify:podcast-chapter (20)
  - **entityCount:** 131938
  - **status:** mixed (47x200, 26x304, 2x404)
  - **cacheTtl:** cache_ttl 600..85227 (17 distinct); offline_ttl=2592000
  - **payloadShape:** Max 1086B. repeated f1/f2 = {f1 varint kind, f2{f2{repeated f1{f1 20-byte file_id, f2 varint format enum, f3 varint byte size}, f2 16-byte gid, f3/f4 10-byte blobs}, f3 str spotify:track uri, f6 str spotify:track uri}}. PLUS f3 = embedded transition map {f1{f1 16B gid}, f2{f2 16B gid}, f3 varint 1, f4{f1 ~182B blob, f2 ~180B blob = gain curves}, f5{f1 20B file_id}, f6{f1 20B file_id}} - identical shape to kind 136.

  ---
  - **kind:** 217
  - **name:** UNKNOWN (beyond enum) - mixbeats.Beats
  - **typeUrl:** type.googleapis.com/spotify.playlistmixing.extensions.mixbeats.Beats
  - **entityTypes:** spotify:track
  - **entityCount:** 6
  - **status:** 200-payload (6x200)
  - **cacheTtl:** cache_ttl=3600; offline_ttl=2592000
  - **payloadShape:** f1 varint 4 (beats per bar), repeated f2{f1 fixed32 float time seconds, f3 varint beat index within bar 1..4, f4 fixed32 float confidence 1.0, optional f5 fixed32 float} - 8482B / ~2900 beat entries in the sample

  ---
  - **kind:** 218
  - **name:** UNKNOWN (beyond enum) - mixvocalactivity.VocalActivity
  - **typeUrl:** type.googleapis.com/spotify.playlistmixing.extensions.mixvocalactivity.VocalActivity
  - **entityTypes:** spotify:track
  - **entityCount:** 6
  - **status:** 200-payload (6x200)
  - **cacheTtl:** cache_ttl=3600; offline_ttl=2592000
  - **payloadShape:** f1 fixed32 float 44100.0 (sample rate), f2 varint 1, f4 varint 315 (duration s), f5 = 17370-byte opaque per-frame vocal-activity level array (byte values ~0x0d/0x0e)

  ---
  - **kind:** 219
  - **name:** UNKNOWN (beyond enum) - mixability.Mixability
  - **typeUrl:** type.googleapis.com/spotify.playlistmixing.extensions.mixability.Mixability
  - **entityTypes:** spotify:track
  - **entityCount:** 6
  - **status:** 200-payload (6x200)
  - **cacheTtl:** cache_ttl=21600; offline_ttl=2592000
  - **payloadShape:** f1 varint 1, f2 fixed64 double 1.0 (11B)

  ---
  - **kind:** 220
  - **name:** UNKNOWN (beyond enum) - EntityTypeTrait
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.EntityTypeTrait
  - **entityTypes:** spotify:track (105), spotify:podcast-chapter (20)
  - **entityCount:** 125
  - **status:** mixed (76x200, 49x304)
  - **cacheTtl:** cache_ttl=86400; offline_ttl=2592000
  - **payloadShape:** f1 varint enum, always 1 in all 76 payloads (max 2B)

  ---
  - **kind:** 222
  - **name:** UNKNOWN (beyond enum) - audio_attributes.v2.AudioAttributes
  - **typeUrl:** type.googleapis.com/spotify.playlistmixing.extensions.audio_attributes.v2.AudioAttributes
  - **entityTypes:** spotify:track
  - **entityCount:** 11898
  - **status:** mixed (207x200, 345x304)
  - **cacheTtl:** cache_ttl=21600; offline_ttl=2592000
  - **payloadShape:** f1 fixed64 double tempo BPM (110.93 in sample), f2{f1 str key name 'F', f2 varint mode 2, f3{f1 str camelot code '7B', f2 str camelot colour '#ff80b4'}} (31B)

  ---
  - **kind:** 225
  - **name:** UNKNOWN (beyond enum) - mixstate.MixState
  - **typeUrl:** type.googleapis.com/spotify.playlistmixing.extensions.mixstate.MixState
  - **entityTypes:** spotify:playlist
  - **entityCount:** 724
  - **status:** mixed (8x200 of which only 1 non-empty, 486x304)
  - **cacheTtl:** cache_ttl=600; offline_ttl=2592000
  - **payloadShape:** f1 varint 1 (2B) on the single non-empty 200; the other 7 200s had an absent Any.value

  ---
  - **kind:** 237
  - **name:** UNKNOWN (beyond enum) - mixthreebandwaveforms.ThreeBandWaveforms
  - **typeUrl:** type.googleapis.com/spotify.playlistmixing.extensions.mixthreebandwaveforms.ThreeBandWaveforms
  - **entityTypes:** spotify:track
  - **entityCount:** 52
  - **status:** 200-payload (2 real payloads observed)
  - **cacheTtl:** cache_ttl=3600; offline_ttl=2592000
  - **payloadShape:** f1 varint 44100 (sample rate), f2 varint 20 (hz), f3 = 10688B blob, f4 = 10146B blob, f5 = 10140B blob (low/mid/high band envelopes). 30989B total.

  ---
  - **kind:** 239
  - **name:** UNKNOWN (beyond enum) - ContentCapabilityTrait
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.ContentCapabilityTrait
  - **entityTypes:** spotify:track (957), spotify:episode (316), spotify:show (3)
  - **entityCount:** 1276
  - **status:** mixed (665x200, 611x304)
  - **cacheTtl:** cache_ttl 3600..86400 (26 distinct); offline_ttl=2592000
  - **payloadShape:** f1/f2/f3/f4 each = a capability slot {f3{f1{f1 str capability key, f2 str value}}}. Observed keys: 'offline'='1' (under f2), 'music-video-disabled'='1' and 'other-video-disabled'='1' (under f4). Payload sizes 16B (138x), 30B (304x), 43B (128x), 57B (95x). THIS IS THE VIDEO/OFFLINE KILL-SWITCH SOURCE.

  ---
  - **kind:** 246
  - **name:** UNKNOWN (beyond enum) - CurationExperienceTrait
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.CurationExperienceTrait
  - **entityTypes:** spotify:podcast-chapter (10), spotify:show (3)
  - **entityCount:** 13
  - **status:** mixed (11x200, 2x304)
  - **cacheTtl:** cache_ttl=86400; offline_ttl=2592000
  - **payloadShape:** f1 str curation-target uri 'spotify:show:43lKIk7Tt69S4tdCpHWCnH', f2{f3{f1 str type label 'show'}} (47B)

  ---
  - **kind:** 249
  - **name:** UNKNOWN (beyond enum) - ContentExperienceTrait
  - **typeUrl:** type.googleapis.com/spotify.contentagnostic.v2.ContentExperienceTrait
  - **entityTypes:** spotify:track (131049), spotify:episode (955), spotify:local (86), spotify:playlist (54), spotify:album (17), spotify:artist (11), spotify:collection (3), spotify:podcast-chapter (20)
  - **entityCount:** 132195
  - **status:** mixed (219x200, 74x304, 85x404)
  - **cacheTtl:** cache_ttl=3600|86400; offline_ttl=2592000
  - **payloadShape:** f1 varint enum only, max payload 2B; values 1 and 2 observed (219 payloads, all 2B). Broadest URI-scheme coverage of any kind including spotify:collection.

**requestPatterns:**
  1. All 2718 requests went to a single endpoint: POST https://gew4-spclient.spotify.com/extended-metadata/v0/extended-metadata. No other host or path variant was seen.
  2. MAX BATCH SIZE OBSERVED = 1425 entities in one BatchedEntityRequest (a TRACK_V4-only batch). Only 17 batches exceeded 300 entities: 1425 x1, 500 x13, 496 x1, 457 x2. So 300 is a CLIENT chunking convention, not a server hard cap.
  3. Entity-count distribution across the 2718 batches (top): 300 entities x795, 1 entity x644, 5 x105, 2 x97, 3 x74, 6 x52, 4 x40, 9 x31, 12 x28, 20 x26, 10 x26, 8 x23, 50 x21, 17 x21, 7 x20, 27 x20. The bimodal shape is: 300-entity bulk hydration sweeps, plus a long tail of single-entity on-demand fetches.
  4. CANONICAL LIST-ROW HYDRATION BUNDLE (the single most important pattern for Wavee): kinds [10 TRACK_V4, 178 IDENTITY_TRAIT, 179 VISUAL_IDENTITY_TRAIT, 182 CONSUMPTION_EXPERIENCE_TRAIT, 212 PLAYBACK_TRAIT, 249 ContentExperienceTrait] requested per spotify:track entity, 300 entities per batch, 398 batches (55636 entities). The same 5 traits WITHOUT TRACK_V4 (client already has metadata cached) is the second bundle: [178,179,182,212,249], 37 batches, 73003 entities.
  5. TRACK_V4-ALONE is the highest-count batch shape: 1428 batches, up to 1425 entities each, 175988 entities total. ALBUM_V4-alone: 98 batches, up to 500 entities. These are the pure catalog-hydration sweeps.
  6. VIDEO / MUSIC-VIDEO SURFACE BUNDLE: [85 ORIGINAL_VIDEO, 98 AUDIO_ASSOCIATIONS, 99 VIDEO_ASSOCIATIONS, 182, 222 AudioAttributes] per spotify:track, 300/batch, 46 batches (11199 entities). A reduced variant [85, 99, 182] appears at up to 82 entities, and [85, 182, 222] at 297 entities.
  7. CANVAS SURFACE BUNDLE: [16 CANVAS_V1, 98, 99, 239 ContentCapabilityTrait] per spotify:track (27 batches, up to 51 entities, 576 entities) and the lighter [16, 239] (12 batches, up to 85 entities, 370 entities). Note 239 is always co-requested with canvas/video - it carries the 'music-video-disabled'/'other-video-disabled' kill switches.
  8. PODCAST EPISODE PAGE BUNDLE (the widest bundle observed, 11 kinds in one query): [4 PODCAST_SEGMENTS, 12 EPISODE_V4, 21 EPISODE_TRANSCRIPTS, 30 EPISODE_ACCESS, 31 SHOW_ACCESS, 54 HTML_DESCRIPTION, 58 CONTENT_WARNING, 80 SHARE_TRAIT, 164 GATED_ENTITY_RELATIONS, 179, 182] per spotify:episode, 300/batch, 3 batches (297 entities). Reduced variants drop 21/31/54 or 4.
  9. PLAYLIST PAGE BUNDLE: [149 ROOTLISTABILITY_TRAIT, 178, 179, 182, 212, 225 MixState, 249] at up to 300 entities (17 batches), and the playlist-only pair [149, 225] (214 entities). 225 alone: 508 entities. 170 AUTO_LENS is fetched as its own single-kind batch (17 batches) BEFORE the mix-lens family is requested.
  10. ETag/If-None-Match is carried per (entity,kind) inside ExtensionQuery.f2 - never as an HTTP header and never per batch. Etag adoption per kind (queries with etag / total queries): 182 = 3467/143196, 222 = 2073/11898, 179 = 1093/132249, 99 = 1022/12085, 12 = 1891/3591, 85 = 1927/11742, 164 = 796/1755, 9 = 623/2274, 10 = 13675/231636, 225 = 487/724, 6 = 210/219, 54 = 343/513, 21 = 318/513, 170 = 29/34, 183 = 74/85, 178 = 225/131201, 212 = 27/131938, 249 = 74/132195. Kinds 4, 15, 22, 27, 28, 29, 30, 31, 58, 64, 88, 98, 108, 113, 136, 138, 142, 205 sent an etag ZERO times.
  11. The 304-vs-200 ratio is strongly kind-dependent and shows what the desktop client actually caches on disk: 182 = 1389 x304 vs 259 x200 (84% cache hits), 12 EPISODE_V4 = 1854 x304 vs 112 x200 (94%), 225 = 486 vs 8 (98%), 54 = 46 vs 5 (90%), 222 = 345 vs 207. By contrast 185 ON_PLATFORM_REPUTATION_TRAIT, 4 PODCAST_SEGMENTS, 15, 28, 29, 37, 136, 142, 205, 217, 218, 219, 237 never once returned 304 - they are always re-fetched.
  12. Request header f3 (task_id) is a fresh 16 raw bytes on every request (2718/2718 exactly 16 bytes, never repeated). f1 country and f2 catalogue were constant 'NL' / 'premium' across the whole corpus.
