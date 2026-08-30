# VIDEO.saz — video / music-video / DRM

> Workflow agent output, run `wf_5a5408b2-258`.
- **summary:** VIDEO.saz (1415 sessions, Spotify web/desktop client 4.75.0-17c433860, market NL/premium, 2026-07-28) captures a large library/playlist sync (a 10,000-track playlist plus several others) followed by a Now-Playing-View browsing session in which the user played tracks and let the NPV canvas videos loop. The dominant traffic is 1002 extended-metadata batches (TRACK_V4 x187823, plus the trait family) and 39 Pathfinder queries. The critical find is queryNpvArtist: its response carries a fully-populated trackUnion.relatedVideos (RelatedVideoPage, non-empty in 12/12 samples, totalCount up to 44) AND trackUnion.associationsV3.unmappedVideoTrackAssociations non-empty in 2/12 samples — the item schemas that were previously unknown. Video delivery is the canvas path only: trackUnion.canvas.fileId feeds GET /manifests/v9/json/sources/{fileId}/options/supports_drm, which returns an unencrypted 16-profile ladder (encryption_infos: []). There is NO DRM license endpoint, NO widevine/playready traffic, and NO queryArtistOverview anywhere in this capture.

**operations:**

  ---
  - **operationName:** searchSuggestions
  - **sha256Hash:** 556f5a15b2fdd3a7113ffd377ad9805e38a3a27b8bb1ca7d6d76bad54aa8ee12
  - **variablesExample:** {"query":"wasa","limit":30,"numberOfTopResults":30,"offset":0,"includeAuthors":true,"includeAlbumPreReleases":true,"includeEpisodeContentRatingsV2":true}
  - **count:** 17
  - **responseShape:** data.searchV2 (per-keystroke suggestions); hash matches Wavee's SearchSuggestionsHash exactly

  ---
  - **operationName:** queryNpvArtist
  - **sha256Hash:** b2cedf7ed0f29c713567d97ed69b848c8387294edfe58a0e439a3a5669cc27bb
  - **variablesExample:** {"artistUri":"spotify:artist:2EXpthNgSeTDeX8nGwxppp","trackUri":"spotify:track:4B0cJGASxVICLW2AsBZhiE","contributorsLimit":10,"contributorsOffset":0,"enableRelatedVideos":true,"enableRelatedAudioTracks":true}
  - **count:** 12
  - **responseShape:** data.artistUnion.{__typename,goods.concerts.items[].data(ConcertV2: artists.items[].data{id,profile.name,uri}, festival, location{city,name}, startDateIsoString, title, uri=spotify:concert:{id}), headerImage.data.sources[]{maxHeight,maxWidth,url}, id, onPlatformReputationTrait.verification{isRegistered,isVerified}, profile{biography{text,type},externalLinks.items[]{name,url},name}, stats{followers,monthlyListeners,topCities.items[]{city,country,numberOfListeners,region},worldRank}, uri, visuals{avatarImage.sources[]{height,url,width}, gallery.items[].sources[]}} AND data.trackUnion.{__typename, associationsV3.unmappedVideoTrackAssociations.items[], canvas{fileId,type,uri,url}, credits, creditsTrait.contributors.items[]{name,role,roleGroup.name,uri,url}, merch{items[]{description,image.sources[].url,nameV2,price,uri,url},totalCount}, relatedVideos{__typename:RelatedVideoPage,totalCount,items[]}}. NOTE: artistUnion has NO relatedVideos/relatedMusicVideos key despite enableRelatedVideos:true — the videos live on trackUnion.

  ---
  - **operationName:** searchTopResultsList
  - **sha256Hash:** 63a93cc04f6d8dea84a85de315e43f396a76cb681500de9ac5ccf5fc618c84cb
  - **variablesExample:** {"query":"10000 tracks","limit":50,"offset":0,"numberOfTopResults":50,"includeArtistHasConcertsField":false,"includeAudiobooks":true,"includeAuthors":true,"includePreReleases":true,"includeAlbumPreReleases":true,"includeEpisodeContentRatingsV2":true,"isPrefix":null,"sectionFilters":["GENERIC","VIDEO_CONTENT"]}
  - **count:** 2
  - **responseShape:** data.searchV2.{__typename,albumsV2,artists,audiobooks,authors,chipOrder,episodes,genres,playlists,podcasts,query,topResultsV2,tracksV2,users}. Both responses fully decompressed (118955 / 122753 bytes) contain ZERO occurrences of VIDEO_CONTENT, spotify:video:, MusicVideo or videoTracks — the VIDEO_CONTENT sectionFilter is request-side only and produced no video section for these queries.

  ---
  - **operationName:** searchPlaylists
  - **sha256Hash:** af1730623dc1248b75a61a18bad1f47f1fc7eff802fb0676683de88815c958d8
  - **variablesExample:** {"includePreReleases":false,"includeAlbumPreReleases":true,"numberOfTopResults":20,"searchTerm":"10000 tracks","offset":0,"limit":30,"includeAudiobooks":true,"includeAuthors":true,"includeEpisodeContentRatingsV2":true}
  - **count:** 2
  - **responseShape:** data.searchV2.playlists; hash matches Wavee's SearchPlaylistsHash exactly

  ---
  - **operationName:** fetchExtractedColors
  - **sha256Hash:** 36e90fcaea00d47c695fce31874efeb2519b97d4cd0ee1abfb4f8dc9348596ea
  - **variablesExample:** {"imageUris":["https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio/track/3DarAbFujv6eYNliUTyqtz/en"]}
  - **count:** 2
  - **responseShape:** data.extractedColors[]. NOTE: one sample passes a spotify:mosaic:{id}:{id}:{id}:{id} uri, the other passes a full pickasso HTTPS URL — both accepted.

  ---
  - **operationName:** recentSearches
  - **sha256Hash:** 2520a5aa49f29d20cd3187261b8778fdd55654200801df9768f8a5decf05330b
  - **variablesExample:** {"limit":50,"includeAuthors":true,"includeEpisodeContentRatingsV2":true}
  - **count:** 1
  - **responseShape:** 1 sample. SHARES its sha256Hash with saveRecentSearches (the multi-op-per-hash case).

  ---
  - **operationName:** saveRecentSearches
  - **sha256Hash:** 2520a5aa49f29d20cd3187261b8778fdd55654200801df9768f8a5decf05330b
  - **variablesExample:** {"uris":["spotify:playlist:04ZwFco4KsjgPlVMtzwfgS"]}
  - **count:** 1
  - **responseShape:** 1 sample. Same hash as recentSearches.

  ---
  - **operationName:** getAlbum
  - **sha256Hash:** b9bfabef66ed756e5e13f68a942deb60bd4125ec1f1be8cc42769dc0259b4b10
  - **variablesExample:** {"uri":"spotify:album:2x6LWti2bjYS6AllSomoV7","locale":"","offset":0,"limit":50}
  - **count:** 1
  - **responseShape:** data.albumUnion... including watchFeedEntrypoint{entrypointUri:"spotify:watch-feed:album:{id}?itemId={base64(spotify:track:{id})}", thumbnailImage.data{__typename:ImageV2,imageId(full https URL),imageIdType:"IMAGE_URL",sources[]{imageFormat:"JPEG",maxHeight,maxWidth,url}}}. 1 sample. Hash matches Wavee's GetAlbumHash exactly.

  ---
  - **operationName:** queryAlbumMerch
  - **sha256Hash:** 3ef44ed6f17be67299538fe77faffab4075aeaf9e1085f10fc835592266711b5
  - **variablesExample:** {"uri":"spotify:album:2x6LWti2bjYS6AllSomoV7"}
  - **count:** 1
  - **responseShape:** 1 sample; hash matches Wavee's QueryAlbumMerchHash exactly

**endpoints:**

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/extended-metadata/v0/extended-metadata
  - **count:** 1002
  - **purpose:** The batch metadata/trait fetch. Kinds REQUESTED (entity-scheme -> count): TRACK_V4(10) x187823, CONSUMPTION_EXPERIENCE_TRAIT(182) x127801, 249=ContentExperienceTrait x119496, PLAYBACK_TRAIT(212) x119426, VISUAL_IDENTITY_TRAIT(179) x119078, IDENTITY_TRAIT(178) x119030, 222=AudioAttributes x10027, ORIGINAL_VIDEO(85) x9763 (all spotify:track), AUDIO_ASSOCIATIONS(98) x9750 (all spotify:track), VIDEO_ASSOCIATIONS(99) x9750 (all spotify:track), CANVAS_V1(16) x155 (spotify:track), ROOTLISTABILITY_TRAIT(149) x31, 225=MixState x31, 239=ContentCapabilityTrait x121, AUDIO_FILES(5) x117, ALBUM_V4(9) x110, TRANSITION_MAPS(136) x5 (spotify:audio), EPISODE_V4(12) x5, USER_PROFILE(15) x3, WATCH_FEED_ENTITY_EXPLORER(114) x2 (spotify:playlist), AUTO_LENS(170) x2 (spotify:playlist), SMART_SHUFFLE(86) x2, AUTOMIX_MODE(27) x1, PUBLISHING_METADATA_TRAIT(183) x12.
  - **bodyShape:** Request header f1={f1 country="NL", f2 catalogue="premium", f3 task_id=16 random bytes}. RESPONSE Any.type_url per kind (all after full zstd decompression): 85 -> spotify.bumblebee.originalvideo.v1.OriginalVideo (9730x status 200 + 33x 304, but the Any.value is a ZERO-LENGTH message in ALL 9730 -> no original video for any track here); 98 AUDIO_ASSOCIATIONS -> 9750/9750 status 404, never any payload; 99 -> spotify.bumblebee.video_associations.v1.VideoAssociations (2125x 200 WITH payload, 7548x 404, 77x 304); 114 -> spotify.watchfeedextensions.api.v1.EntityExplorerEntrypointResponse; 16 -> spotify.canvaz.cache.EntityCanvazResponse.Canvaz; 136 -> spotify.playback_platform.transition.v1.TransitionMaps; 149 -> spotify.traits.v1.RootlistabilityTrait; 170 -> spotify.autolensextension.v1.AutoLens; 212 -> spotify.contentagnostic.v2.PlaybackTrait; 225 -> spotify.playlistmixing.extensions.mixstate.MixState; 249 -> spotify.contentagnostic.v2.ContentExperienceTrait. VideoAssociations(99) wire shape, 2125 samples, EXACTLY 1 association each: f1 Association{ f1 associated_uri (a spotify:track: uri = the VIDEO track), f2 VideoFileGroup{ f1 repeated VideoFile{ f1 20-byte image/file id, f2 variant, f3 width, f4 height } } }. variant histogram: 0 x2125, 2 x2125, 4 x2107, 1 x18. dims: (2560,1440) x4214 with id prefix ab6742d3000053b7, (1280,720) x2107 with ab6742d3000052b7, and 18 square-album cases (600,600)/ab67616d00001e02, (128,128)/ab67616d00004851, (1280,1280)/ab67616d0000b273. Cross-checking the SAME image ids in the GraphQL coverArt.sources proves the declared f3/f4 are exactly 2x the real CDN pixel size (ab6742d3000053b7 = 1280x720 real, declared 2560x1440). WATCH_FEED_ENTITY_EXPLORER(114) payload (2 samples, both on spotify:playlist): f3{ f1 entrypoint uri "spotify:watch-feed:playlist:{id}?itemId={base64 of spotify:track:{id}}", f2{f1 canvas .cnvs.mp4 URL} (present in 1 of 2), f3{f1 thumbnail https URL, f2 "video"}, f4 "Swipe through previews of tracks in this playlist.", f5 "Explore {playlist name}" }. CANVAS_V1(16) payload: f1 canvas file id (32 hex), f2 canvaz.scdn.co .cnvs.mp4 URL, f3 32-hex, f4=3, f5 track uri, f6{f1 artist uri, f2 artist name, f3 artist image}, f8 "artist"|"licensor", f11 spotify:canvas:{id}, f13 repeated {f1 width, f2 height, f3 thumbnail jpg URL} at 256x144 and 512x288.

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/gabo-receiver-service/v3/events
  - **count:** 43
  - **purpose:** Event/telemetry batch (also 3x POST https://spclient.wg.spotify.com/gabo-receiver-service/v3/events/ and 1x .../gabo-receiver-service/public/v3/events)
  - **bodyShape:** binary protobuf event envelope

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist-permission/v1/playlist/{id}/permission/base
  - **count:** 40
  - **purpose:** Playlist base permission
  - **bodyShape:** application/x-protobuf, e.g. 0a0262661002 (f1="bf", f2=2)

  ---
  - **method:** GET
  - **url:** https://image-cdn-fa.spotifycdn.com/image/{hex}
  - **count:** 35
  - **purpose:** Album/playlist artwork (also image-cdn-ak.spotifycdn.com x16)
  - **bodyShape:** image bytes

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist/v2/playlist/{id}
  - **count:** 31
  - **purpose:** Full playlist listing (zstd; up to 720 KB decompressed — the 10k-track playlist)
  - **bodyShape:** protobuf SelectedListContent

  ---
  - **method:** PUT
  - **url:** https://gew4-spclient.spotify.com/connect-state/v1/devices/{hex}
  - **count:** 24
  - **purpose:** Connect device state publish
  - **bodyShape:** gzip protobuf. NOTE: none of the 24 bodies contains the substring "video" — no video field is published to Connect in this capture.

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/melody/v1/msg/batch
  - **count:** 17
  - **purpose:** JS-SDK playback telemetry — THE richest description of the video pipeline in this capture
  - **bodyShape:** {"messages":[{"type":..,"message":{..}}]}. Types seen: jssdk_playback_start x9, jssdk_playback_stats x9, jssdk_playback_error x8, jssdk_warning x8. jssdk_playback_stats fields include play_track:"spotify:canvas:{id}", file_id, playback_id, internal_play_id, memory_cached, persistent_cached, audio_format:"", video_format:"vp9" (9/9), manifest_id (== canvas fileId), protected:false (9/9), key_system:"" (9/9), key_system_impl, urls_json (contains the resolved https://video-fa.scdn.co/segments/v1/origins/{origin}/sources/{source}/encodings/{encoding}/profiles/0/{{segment_timestamp}}.webm?token=..&fauth=..&token_ak=..&token_cf=..), start_time, end_time, ms_play_latency, ms_init_latency, ms_head_latency, ms_first_bytes_latency, ms_manifest_latency, ms_resolve_latency, ms_license_session_latency:null, ms_license_generation_latency:null, ms_license_request_latency:null, ms_license_update_latency:null, ms_played, ms_file_duration, ms_start_position, ms_end_position, ms_initial_rebuffer, ms_seek_rebuffer, ms_stall_rebuffer, ms_played_per_surface, ms_played_visible, n_stalls, n_rendition_upgrade/downgrade, bps_bandwidth_max/min/avg, n_seekback, n_seekforward, video_start_bitrate, start_bitrate, time_weighted_bitrate, reason_start:"unknown", reason_end:"endplay", initially_paused, had_error. jssdk_playback_error carries sdk_id "overture:4.75.0", content_id, content_class "DirectorMa..". jssdk_warning type PLAYER_CONTAINER_ELEMENT_NOT_FOUND with json_data.track{uri:spotify:canvas:{id}, fileId, mediaFormat:"MANIFEST_ID"}.

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/playplay/v1/key/{hex}
  - **count:** 15
  - **purpose:** AUDIO decryption key (not video). Confirms the only key exchange present is playplay for audio.
  - **bodyShape:** req protobuf 0805121002517d9e67502214cddfe35218fa8c772001280130cceda1d306; resp 24 bytes 0a1099dd37b5a4a4bcf58ead209d9e6378d61204d65cb38c (f1 = 16-byte key, f2 = 4 bytes)

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/net-fortune/v2/fortune?bandwidth=&latency=&stutter=&bitrate=&request_type=
  - **count:** 13
  - **purpose:** Bandwidth/CDN advisory
  - **bodyShape:** protobuf: f1 = uuid string, f2 = varint (e.g. 0xc0b955)

  ---
  - **method:** GET
  - **url:** https://heads-fa-tls13.spotifycdn.com/head/{hex}
  - **count:** 12
  - **purpose:** Audio file head/prefetch (OggS magic 4f676753 in the body)
  - **bodyShape:** application/octet-stream, Ogg

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/storage-resolve/v2/files/audio/interactive/1/{hex}
  - **count:** 12
  - **purpose:** Resolve audio file id -> CDN URLs (also .../interactive_prefetch/1/{hex} x2)
  - **bodyShape:** protobuf: f1=0 (CDN), f2 repeated https://audio-fa.scdn.co/audio/{fileid}?... and https://audio-cf.spotifycdn.com/...

  ---
  - **method:** GET
  - **url:** https://spclient.wg.spotify.com/metadata/4/track/{hex}?market=from_token
  - **count:** 11
  - **purpose:** REST track metadata (JSON), 12 matching OPTIONS preflights precede these
  - **bodyShape:** {"gid","name","album":{"gid","name","artist":[{"gid","name"}],"type","label","date":{year,month,day},"cover_group":{"image":[{"file_id",...}]}},...}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/manifests/v9/json/sources/{hex32}/options/supports_drm
  - **count:** 7
  - **purpose:** THE VIDEO MANIFEST ENDPOINT. The {hex32} is exactly trackUnion.canvas.fileId from queryNpvArtist (traced end-to-end for 5 distinct ids). Preceded by a matching OPTIONS preflight x7. All 7 responses are canvas loops (7.0-8.0 s, portrait 540x960 spritemap) and are UNENCRYPTED.
  - **bodyShape:** JSON. Top-level: {contents:[{encoding_id, segment_length:4, start_time_millis:0, end_time_millis:7099..8000, profiles:[16], offline_profiles:[], background_profiles:[], encryption_infos:[] <- ALWAYS EMPTY, no DRM}], spritemaps:[{id:0,height:960,width:540,number:1}], start_time_millis, end_time_millis, initialization_template, segment_template, subtitle_template, spritemap_template, base_urls:["https://video-fa.scdn.co/segments/","https://video-cf.spotifycdn.com/segments/"], spritemap_base_urls:["https://spritemaps.spotifycdn.com/spritemaps/"], subtitle_base_urls:["https://subtitles.spotifycdn.com/subtitles/"], subtitle_language_codes:[]}. segment_template = "v1/origins/{origin32}/sources/{source32}/encodings/{encoding32}/profiles/{{profile_id}}/{{segment_timestamp}}.{{file_type}}?token=..&fauth=<RS256 JWT iss=scdn-url-signer,exp,nbf,paths>&token_ak=..&token_cf=.."; initialization_template is the same with /inits/{{file_type}}; subtitle_template = "v1/origins/{origin}/sources/{source}/{{language_code}}.webvtt?__token__=..&fauth=.."; spritemap_template = ".../profiles/{{spritemap_id}}.jpg". The 16-profile ladder is fixed: mp4/avc1 ids 9,8,7,6,5,4 (180/240/320/480/720/1080), ts/mp2t ids 15,14,13,12,11,10 (same ladder), webm/vp9 ids 3,2,1,0 (320/480/720/1080). Every capture used webm profile 0 (vp9 1080). Signed-URL lifetime observed = 604800 s (exp - nbf).

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/popcount/v2/playlist/{id}/count
  - **count:** 5
  - **purpose:** Playlist follower/like count
  - **bodyShape:** protobuf 089809100138cc04

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist/v2/playlist/{id}/diff
  - **count:** 5
  - **purpose:** Playlist incremental diff (also /playlist/v2/album/{id}/diff x1)
  - **bodyShape:** protobuf

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist-permission/v1/playlist/{id}/permission/members
  - **count:** 4
  - **purpose:** Collaborator list
  - **bodyShape:** protobuf

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/herodotus/spotify.resumption.v1.ResumePointRevisionService/CreateResumePointRevision
  - **count:** 3
  - **purpose:** Resume-point write (gRPC over HTTP). Also ListResumePointRevisions x1, BatchCreateResumePointRevisions x1, and spotify.resumption.v1.CurrentStateService/ListCurrentStates x1.
  - **bodyShape:** application/grpc protobuf: f2 context uri "spotify:list:play-history:v1", nested f2 = track uri, f3 timestamps

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/quicksilver/v2/messages?ctv_type=web-modal&trigger=&action=
  - **count:** 2
  - **purpose:** In-app message/promo poll (preceded by 2 OPTIONS)
  - **bodyShape:** {} (empty JSON object)

  ---
  - **method:** GET
  - **url:** https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/thisisv3/{id}/en
  - **count:** 2
  - **purpose:** Generated 'This Is' / radio cover art (also .../img/radio/track/{id}/en x1)
  - **bodyShape:** image/JPEG

  ---
  - **method:** POST
  - **url:** https://spclient.wg.spotify.com/playlistextender/extendp/
  - **count:** 2
  - **purpose:** Playlist 'recommended tracks' extender (1 OPTIONS preflight)
  - **bodyShape:** REQ {"playlistURI":"spotify:playlist:{id}","trackSkipIDs":[],"numResults":20}; RESP {"recommendedTracks":[{"id","originalId","name","artists":[{"id","name"}],"album":{"id","name","largeImageUrl","imageUrl"},"duration","explicit","popularity","score","contentRating":[]}]}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/context-resolve/v1/autoplay
  - **count:** 2
  - **purpose:** Autoplay continuation for a finished context
  - **bodyShape:** REQ protobuf: f1 = context uri (spotify:album:{id}) then repeated f2 = track uris. RESP JSON {"pages":[{"tracks":[{"uri","uid","metadata":{"decision_id":"ssp~{hex}"}}]}]}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/playlist-publish/v1/subscription/playlist/{id}
  - **count:** 1
  - **purpose:** Subscribe to playlist change notifications (also .../subscription/album/{id} x1)
  - **bodyShape:** empty 200

  ---
  - **method:** GET
  - **url:** https://spclient.wg.spotify.com/inspiredby-mix/v2/seed_to_playlist/spotify:track:{id}?response-format=json
  - **count:** 1
  - **purpose:** Track -> radio/mix playlist lookup (1 OPTIONS preflight)
  - **bodyShape:** {"total":1,"mediaItems":[{"uri":"spotify:playlist:37i9dQZF1E8Lh89wUVjZqo"}]}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/collection/v2/delta
  - **count:** 1
  - **purpose:** Library collection delta sync
  - **bodyShape:** REQ protobuf f1=username, f2="ignoreinrecs", f3=last sync epoch-ms string; RESP 08011a0d{epoch-ms string}

  ---
  - **method:** CONNECT
  - **url:** {host}:443
  - **count:** 34
  - **purpose:** Fiddler TLS tunnels (gew4-spclient x10, api-partner x7, image-cdn-fa x6, image-cdn-ak x6, spclient.wg x2, pickasso x2, heads-fa x1). Notably ABSENT from the entire capture: video-fa.scdn.co, video-cf.spotifycdn.com, canvaz.scdn.co, spritemaps.spotifycdn.com, subtitles.spotifycdn.com, audio-fa.scdn.co — no media segment traffic was proxied.
  - **bodyShape:** 

**notable:**
  1. UNBLOCKER — the video item schema you needed is in queryNpvArtist, not queryArtistOverview. trackUnion.relatedVideos is NON-EMPTY in 12/12 samples (item counts 1,2,3,3,3,10,10,10,10,10,10,10 with totalCount 1,2,3,3,3,11,29,30,37,38,39,44 — so the page is capped at 10 with a real totalCount for paging). Exact item shape (verbatim from raw/1329_s.txt): {"__typename":"RelatedVideo","trackOfVideo":{"__typename":"TrackResponseWrapper","_uri":"spotify:track:3jW1eX32ubccuvE9Qdl6Wb","data":{"__typename":"Track","albumOfTrack":{"coverArt":{"extractedColors":{"colorDark":{"hex":"#383020"}},"sources":[{"height":720,"url":"https://i.scdn.co/image/ab6742d3000053b7ef7de7b106dd91e1615e28c0","width":1280},{"height":360,"url":"https://i.scdn.co/image/ab6742d3000052b7ef7de7b106dd91e1615e28c0","width":640},{"height":720,"url":"https://i.scdn.co/image/ab6742d3000053b7ef7de7b106dd91e1615e28c0","width":1280}]},"uri":"spotify:album:1LOJhABpDQ4gsk0x8owlJl"},"artists":{"items":[{"profile":{"name":"Paris Paloma"},"uri":"spotify:artist:2EXpthNgSeTDeX8nGwxppp"}]},"associationsV3":{"audioAssociations":{"items":[{"trackAudio":{"_uri":"spotify:track:0e00DiF2T9znEdmWakYSC3"}}]}},"contentRating":{"label":"NONE"},"name":"labour","uri":"spotify:track:3jW1eX32ubccuvE9Qdl6Wb"}},"uri":"spotify:video:7fGukuCyY6epYrc4IL7LRS"}. Two load-bearing facts: (a) RelatedVideo.uri is a spotify:video:{base62} URI — the first sighting of that scheme on the wire; (b) trackOfVideo.data.associationsV3.audioAssociations.items[].trackAudio._uri gives the AUDIO track the video maps to, i.e. the reverse of extension kind 99.
  2. UNBLOCKER 2 — unmappedVideoTrackAssociations is NON-EMPTY in 2 of 12 samples (raw/1329_c.txt spotify:track:4B0cJGASxVICLW2AsBZhiE -> 2 items; raw/1386_c.txt spotify:track:7xDd7gl6AGgpiOz5trz4dM -> 1 item). Exact item shape: {"associatedTrack":{"_uri":"spotify:track:7uqwHueeTHLRC6O1pITJgw","data":{"__typename":"Track","albumOfTrack":{"coverArt":{"sources":[{"height":720,"url":"https://i.scdn.co/image/ab6742d3000053b757d6242c35c0a13e0ffb798e","width":1280},{"height":360,"url":"...ab6742d3000052b7...","width":640},{"height":720,"url":"...ab6742d3000053b7...","width":1280}]},"uri":"spotify:album:5h55O8PNgiawI81xuVxxnJ"},"artists":{"__typename":"ArtistPage","items":[{"profile":{"name":"Paris Paloma"},"uri":"spotify:artist:2EXpthNgSeTDeX8nGwxppp"}]},"contentRating":{"label":"NONE"},"name":"labour - (RAK Session)","uri":"spotify:track:7uqwHueeTHLRC6O1pITJgw"}}}. Differences vs relatedVideos items: no spotify:video: uri, no extractedColors, no nested audioAssociations, and artists carries an explicit __typename:"ArtistPage". These are alternate-version video tracks (live/session cuts) that have no canonical audio mapping.
  3. STALE HASH — Wavee's C:\wavee\fluent-gpu\src\apps\Wavee\SpotifyLive\PathfinderClient.cs:131 has QueryNpvArtistHash = "047c9c225967d41a763949a4db3f0493e901c9f8689a6537408aabf9beffc177". The wire hash in all 12 captures is "b2cedf7ed0f29c713567d97ed69b848c8387294edfe58a0e439a3a5669cc27bb". SpotifyAlbumEnrichmentService.cs already sends enableRelatedVideos:true but the capture shows the client must ALSO send enableRelatedAudioTracks:true, contributorsLimit:10, contributorsOffset:0 and BOTH artistUri and trackUri.
  4. NO DRM ANYWHERE. Zero sessions contain the substrings widevine / playready / Widevine / PlayReady, and there is no license-acquisition endpoint of any kind. Every one of the 7 v9 manifests has encryption_infos: [] and every one of the 9 melody playback_stats rows reports protected:false, key_system:"", key_system_impl:"" with all four ms_license_*_latency fields null. This capture contains ONLY unencrypted canvas video — it is not evidence about the DRM music-video path.
  5. The canvas/video resolution chain is proven end-to-end in this capture: queryNpvArtist -> data.trackUnion.canvas{fileId, type:"VIDEO_LOOPING_RANDOM", uri:"spotify:canvas:{base62}", url:"https://canvaz.scdn.co/upload/{artist|licensor}/{id}/video/{hex32}.cnvs.mp4"} -> OPTIONS+GET /manifests/v9/json/sources/{canvas.fileId}/options/supports_drm -> segment_template on video-fa.scdn.co. NOTE the trap: canvas.fileId (e.g. 4b7f070d0b8437a4b83dd39d02856119) is NOT the hex in the .cnvs.mp4 URL (66e00c64aab84d79b0c3432f3fbd708e) — they are different ids and only fileId works for the manifest. canvas is null for 4 of the 12 sampled tracks. The only canvas.type value observed is VIDEO_LOOPING_RANDOM (8 samples).
  6. ORIGINAL_VIDEO (kind 85) is a dead end here: 9730 status-200 responses all carry Any.type_url = type.googleapis.com/spotify.bumblebee.originalvideo.v1.OriginalVideo with a ZERO-BYTE Any.value, plus 33x 304. AUDIO_ASSOCIATIONS (kind 98) returned 404 on all 9750 requests. Only VIDEO_ASSOCIATIONS (99) actually yields data (2125 of 9750 -> ~22% of library tracks have a video counterpart).
  7. VideoAssociations confirms and extends Wavee's existing C:\wavee\fluent-gpu\src\apps\Wavee\SpotifyLive\Protos\video_associations.proto: the proto's comment says variants "0, 2, 4 seen" — this capture adds VARIANT 1 (18 samples), which appears only on the 3 square album-art entries (ab67616d* ids at 600x600 / 128x128 / 1280x1280). Also: the width/height in VideoFile are exactly 2x the real CDN pixel size (id ab6742d3000053b7... is declared 2560x1440 by XM but the identical id is served at 1280x720 per the GraphQL coverArt.sources) — treat f3/f4 as @2x, not device pixels. Every one of the 2125 payloads has exactly ONE association (the proto's `optional Association association = 1` singular field is correct).
  8. WATCH FEED: two distinct surfaces observed. (1) XM kind 114 WATCH_FEED_ENTITY_EXPLORER on spotify:playlist entities returns EntityExplorerEntrypointResponse with entrypoint uri "spotify:watch-feed:playlist:{id}?itemId={base64url of spotify:track:{id}}", a thumbnail {url, "video"}, subtitle "Swipe through previews of tracks in this playlist." and title "Explore {name}" (2 samples). (2) getAlbum returns data.albumUnion.watchFeedEntrypoint with entrypointUri "spotify:watch-feed:album:{id}?itemId={base64}" and thumbnailImage.data ImageV2 (1 sample). NO Pathfinder operation that FETCHES a watch feed was captured — only the entrypoints. The itemId is plain base64 of the track uri (c3BvdGlmeTp0cmFjazo... decodes to "spotify:track:...").
  9. TOOLING BUG that will silently corrupt any future pass: the shared C:\Users\CHRIST~1\AppData\Local\Temp\claude\C--wavee-fluent-gpu-app\d440d2ee-c502-478c-8d41-6d64927a2974\scratchpad\xmresp_lib.py decode() uses zstd.ZstdDecompressor().decompressobj().decompress(body) with no loop, which SILENTLY TRUNCATES every zstd body at exactly 65536 bytes. 914 of the 1415 responses in VIDEO.saz are affected (the largest is 720202 bytes truncated to 65536). Using it undercounted VIDEO_ASSOCIATIONS payloads 33 -> real 2125 and TRACK_V4 payloads 1969 -> real 186924. Fix: zstd.ZstdDecompressor().stream_reader(body).read().
  10. Fiddler did not proxy any media CDN in this session (no canvaz.scdn.co, video-fa.scdn.co, video-cf.spotifycdn.com, spritemaps/subtitles.spotifycdn.com, audio-fa.scdn.co). So there is no captured evidence of segment fetch behaviour, init-segment format, or spritemap/webvtt retrieval — only the templates that name them.
  11. queryArtistOverview does NOT appear in VIDEO.saz at all (0 sessions), so this capture neither confirms nor refutes the stale hash ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a. If the goal is relatedMusicVideos/unmappedMusicVideosV2 specifically on the artist page, that op still needs its own capture — but the item schemas are now known from the NPV equivalents above.
  12. searchTopResultsList now sends sectionFilters:["GENERIC","VIDEO_CONTENT"] (2 samples, both). Wavee's SearchTopResultsHash 63a93cc0... still matches the wire, but if Wavee omits the sectionFilters variable it may be requesting a different section set than the real client.
  13. recentSearches and saveRecentSearches share ONE sha256Hash (2520a5aa49f29d20cd3187261b8778fdd55654200801df9768f8a5decf05330b) with completely different variables ({limit,includeAuthors,includeEpisodeContentRatingsV2} vs {uris:[...]}) — a live instance of the one-hash-multiple-operations case. Neither hash appears in Wavee's PathfinderClient.cs.
  14. Client version fingerprint for anyone reproducing this: overture SDK 4.75.0, package_version 4.75.0-17c433860, access-control-allow-origin https://xpui.app.spotify.com, market NL, catalogue premium, capture date 2026-07-28.
