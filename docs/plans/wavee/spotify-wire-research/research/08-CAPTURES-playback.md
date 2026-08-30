# playback_remote.saz — connect-state & remote playback

> Workflow agent output, run `wf_5a5408b2-258`.
- **summary:** playback_remote.saz (676 sessions, 2026-07-27 14:37) captures TWO Spotify clients on one box: Wavee itself (Connect device id `5ba893a8b2b24f378a9b7bd7a24fe7e6`, device name "Wavee", Spotify-App-Version 129300667 / `1.2.93.667.g7b5cc0ce`) and the real desktop client (device id `9b4d5a58acf3c0c00efb1819e74b606c0c3d1439`, name "CHRISLAPT", version 129400583 / `1.2.94.583.g60394bd5`). The desktop opens the device picker, wakes the cluster, transfers playback TO Wavee, marks itself inactive, and then remote-controls Wavee for ~2 minutes: play (playlist / album / artist-list / single-track contexts), update_context, next_track ×8, add_to_queue ×2. Wavee is the active playback device throughout — it publishes 27 PUT_STATE bodies, resolves contexts, fetches storage-resolve/playplay keys, streams audio, and writes play-history resume points. Because the traffic is bidirectional and attributable per client, this capture is a direct A/B of Wavee's PutStateRequest against the real client's, plus a complete inventory of the inbound command surface Wavee must handle. Caveat: Wavee's build here is 129300667 (2026-07-27), predating the 2026-07-28 signals/associated_video work, so some gaps below may already be closed.

**operations:**

  ---
  - **operationName:** queryNpvArtist
  - **sha256Hash:** b2cedf7ed0f29c713567d97ed69b848c8387294edfe58a0e439a3a5669cc27bb
  - **variablesExample:** {"artistUri": "spotify:artist:0xliTEbFfy5HQHvsTknTkX", "trackUri": "spotify:track:4S1VYqwfkLit9mKVY3MXoo", "contributorsLimit": 10, "contributorsOffset": 0, "enableRelatedVideos": true, "enableRelatedAudioTracks": true}
  - **count:** 16
  - **responseShape:** data.artistUnion.{id,uri,profile.name,profile.biography.{text,type},profile.externalLinks.items[].{name,url},stats.{followers,monthlyListeners,worldRank},stats.topCities.items[].{city,country,region,numberOfListeners},visuals.avatarImage.sources[].{url,width,height},visuals.gallery.items[].sources[],headerImage.data.sources[].{url,maxWidth,maxHeight},goods.concerts.{items[],totalCount},onPlatformReputationTrait.verification.{isVerified,isRegistered}}; data.trackUnion.{canvas.{fileId,type,uri,url},credits[].{__typename,artistName,artistUri,role,isArtistUriLinkable},creditsTrait.contributors.items[].{name,role,uri,url},creditsTrait.sources.items[].name,merch.items[].{nameV2,description,price,uri,url,image.sources[]},merch.totalCount,relatedVideos.items[],associationsV3.unmappedVideoTrackAssociations.items[]}

  ---
  - **operationName:** searchSuggestions
  - **sha256Hash:** 556f5a15b2fdd3a7113ffd377ad9805e38a3a27b8bb1ca7d6d76bad54aa8ee12
  - **variablesExample:** {"query": "s", "limit": 30, "numberOfTopResults": 30, "offset": 0, "includeAuthors": true, "includeAlbumPreReleases": true, "includeEpisodeContentRatingsV2": true}
  - **count:** 4
  - **responseShape:** (response body truncated by the capture at 64KB — not decoded)

  ---
  - **operationName:** queryAlbumMerch
  - **sha256Hash:** 3ef44ed6f17be67299538fe77faffab4075aeaf9e1085f10fc835592266711b5
  - **variablesExample:** {"uri": "spotify:playlist:37i9dQZF1E8RrQBpL2fW7p"}
  - **count:** 4
  - **responseShape:** 

  ---
  - **operationName:** getAlbum
  - **sha256Hash:** b9bfabef66ed756e5e13f68a942deb60bd4125ec1f1be8cc42769dc0259b4b10
  - **variablesExample:** {"uri": "spotify:album:06mXfvDsRZNfnsGZvX2zpb", "locale": "en", "offset": 0, "limit": 50}
  - **count:** 3
  - **responseShape:** 

  ---
  - **operationName:** similarAlbumsBasedOnThisTrack
  - **sha256Hash:** 1d1f93a737498adca2c892c73af87fc0b052afe4e1a33c989540c32413dfae17
  - **variablesExample:** {"uri": "spotify:track:5ac3D4hNLW7NFhYFCThXgh", "limit": 24, "albumsOnly": true}
  - **count:** 3
  - **responseShape:** data.seoRecommendedTrackAlbum.totalCount; data.seoRecommendedTrackAlbum.items[].data.{__typename,uri,name,type,artists.items[],coverArt.sources[],date.{isoString,precision,year},playability.playable,sharingInfo.{shareId,shareUrl}}

  ---
  - **operationName:** fetchExtractedColors
  - **sha256Hash:** 36e90fcaea00d47c695fce31874efeb2519b97d4cd0ee1abfb4f8dc9348596ea
  - **variablesExample:** {"imageUris": ["spotify:image:ab67706c0000da8462b800f6d8cf33cb9e9a37e9"]}
  - **count:** 3
  - **responseShape:** 

  ---
  - **operationName:** home
  - **sha256Hash:** 9052ac65ff42aefe6d39c45c184d9144cf8dbcc233ea1a76f8649264ad3e7896
  - **variablesExample:** {"homeEndUserIntegration": "INTEGRATION_DESKTOP", "timeZone": "Etc/UTC", "sp_t": "", "facet": "", "sectionItemsLimit": 10, "includeEpisodeContentRatingsV2": true}
  - **count:** 2
  - **responseShape:** 

  ---
  - **operationName:** queryNpvArtist
  - **sha256Hash:** 047c9c225967d41a763949a4db3f0493e901c9f8689a6537408aabf9beffc177
  - **variablesExample:** {"artistUri": "spotify:artist:4gzpq5DPGxSnKTe4SA8HAU", "trackUri": "spotify:track:1a3G9SNslcKsPAOuIikaxd", "contributorsLimit": 10, "contributorsOffset": 0, "enableRelatedVideos": true, "enableRelatedAudioTracks": true}
  - **count:** 2
  - **responseShape:** identical to the b2cedf7e variant EXCEPT it returns data.artistUnion.profile.verified (bool) and has NO data.artistUnion.onPlatformReputationTrait.verification.{isVerified,isRegistered}

  ---
  - **operationName:** recentSearches
  - **sha256Hash:** 2520a5aa49f29d20cd3187261b8778fdd55654200801df9768f8a5decf05330b
  - **variablesExample:** {"limit": 50, "includeAuthors": true, "includeEpisodeContentRatingsV2": true}
  - **count:** 2
  - **responseShape:** (response body truncated by the capture at 64KB — not decoded)

  ---
  - **operationName:** feedBaselineLookup
  - **sha256Hash:** a950fb7c4ecdcaf2aad2f3ca9ee9c3aa4b9c43c97e1d07d05148c4d355bea7fc
  - **variablesExample:** {"uris": ["spotify:playlist:37i9dQZF1E8LiNkvLvN1Zj", "spotify:playlist:37i9dQZF1E8Lw0T0QHaEL5", "spotify:playlist:37i9dQZF1E4rFgAnswexiQ", "spotify:playlist:37i9dQZF1EIZ7VI2LDAdVx", "spotify:playlist:37i9dQZF1E4xN1vnodatMF"]}
  - **count:** 1
  - **responseShape:** 

  ---
  - **operationName:** queryWhatsNewFeed
  - **sha256Hash:** d889c8c936ab192af8ced595427f5ba2acdf63478fdc0a181c8d477f8322630e
  - **variablesExample:** {"offset": 0, "limit": 50, "onlyUnPlayedItems": false, "includedContentTypes": [], "includeEpisodeContentRatingsV2": true}
  - **count:** 1
  - **responseShape:** 

  ---
  - **operationName:** queryArtistOverview
  - **sha256Hash:** ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a
  - **variablesExample:** {"uri": "spotify:artist:0K87f3owemzI8NUCoEIXOB", "locale": "", "preReleaseV2": true}
  - **count:** 1
  - **responseShape:** 

  ---
  - **operationName:** queryTrackArtists
  - **sha256Hash:** ee2b038198f5e62c679c3996584d9249bbee55fe69fc212271c56492a022c798
  - **variablesExample:** {"trackUri": "spotify:track:6lV2MSQmRIkycDScNtrBXO"}
  - **count:** 1
  - **responseShape:** data.trackUnion.{__typename,uri,artists.items[].{uri,profile.name}}

  ---
  - **operationName:** searchTopResultsList
  - **sha256Hash:** 63a93cc04f6d8dea84a85de315e43f396a76cb681500de9ac5ccf5fc618c84cb
  - **variablesExample:** {"query": "sasa", "limit": 50, "offset": 0, "numberOfTopResults": 50, "includeArtistHasConcertsField": false, "includeAudiobooks": true, "includeAuthors": true, "includePreReleases": true, "includeAlbumPreReleases": true, "includeEpisodeContentRatingsV2": true, "isPrefix": null, "sectionFilters": ["GENERIC", "VIDEO_CONTENT"]}
  - **count:** 1
  - **responseShape:** (response body truncated by the capture at 64KB — not decoded)

**endpoints:**

  ---
  - **method:** PUT
  - **url:** https://gew4-spclient.spotify.com/connect-state/v1/devices/{deviceId}
  - **count:** 28
  - **purpose:** PUT_STATE. 27 from Wavee (device 5ba893a8…), 1 from desktop (9b4d5a58…, with query `?wake-devices=false`). Request is gzipped via the non-standard request header `X-Transfer-Encoding: gzip` (NOT Content-Encoding) with `Content-Type: application/protobuf`. Response is a Cluster protobuf, `content-encoding: br` (brotli — NOT gzip).
  - **bodyShape:** PutStateRequest{2=Device,3=member_type(2=CONNECT_STATE),4=is_active,5=put_state_reason,6=message_id,7=last_command_sent_by_device_id,8=last_command_message_id,9=started_playing_at,12=client_side_timestamp}. Response Cluster{1 changed_ts,2 active_device_id,3 PlayerState,4 device map ×3,5 transfer_data(2512B),6 transfer_data_timestamp,8 need_full_player_state=1,9 server_ts,10 needs_state_updates=1,11 started_playing_at,13=0 (UNDECLARED in Wavee's connect.proto)}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/connect-state/v1/player/command/from/{fromDeviceId}/to/{toDeviceId}
  - **count:** 20
  - **purpose:** Inbound remote-control commands from the desktop client to Wavee. Body is JSON (despite `Content-Type: application/x-www-form-urlencoded`) and is gzipped via `X-Transfer-Encoding: gzip`. Endpoints observed: play ×5, next_track ×8, update_context ×3, add_to_queue ×2 (plus 2 more play/next in the 20). Response is JSON `{"ack_id":"…"}`.
  - **bodyShape:** {"command":{…},"connection_type":"wlan","intent_id":"<32hex>"}. play → command{endpoint,context{entity_uri,uri,url,metadata{…},restrictions{22 disallow_*_reasons arrays},pages[{page_url,next_page_url,tracks[{uri,uid,metadata{…}}]}]},play_options{reason:"interactive",operation:"replace",trigger:"immediately",override_restrictions,only_for_local_device,system_initiated},play_origin{feature_identifier,feature_version,view_uri,external_referrer,referrer_identifier,device_identifier,feature_classes[],restriction_identifier},prepare_play_options{always_play_something,skip_to{track_uri,track_uid,track_index},initially_paused,system_initiated,player_options_override{shuffling_context,modes{context_enhancement}},session_id,license:"premium",suppressions{providers[]},prefetch_level:"none",audio_stream:"default",configuration_override{}},logging_params{command_initiated_time,command_received_time,page_instance_ids[],interaction_ids[],device_identifier,command_id}}. next_track → command{endpoint,options{override_restrictions,only_for_local_device,system_initiated},logging_params}. add_to_queue → command{endpoint,track{uri,uid:"",metadata:{}},options,logging_params}. update_context → command{endpoint,context{…same as play},session_id}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/connect-state/v1/connect/transfer/from/{fromDeviceId}/to/{toDeviceId}
  - **count:** 1
  - **purpose:** Desktop transfers active playback to Wavee. 1 sample. Response JSON `{"ack_id":"wz5S0pyivvdsw9Z4zhY_200DsyU"}`.
  - **bodyShape:** {"options": {"restore_paused": "restore", "restore_position": "extrapolate", "restore_track": "only_current", "license": "premium"}, "transfer_intent_id": "4a2c68752bcc898387d57534cbe4af25", "command_id": "e7f2f59421350f8fd27bc18ed2418f1c", "interaction_id": "f5774cef-3357-42c4-b762-da3d5862948b"}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/connect-state/v1/cluster/wake-devices
  - **count:** 1
  - **purpose:** Desktop wakes the cluster immediately before transferring. Content-Length: 0, empty body, 200 OK with empty body. 1 sample.
  - **bodyShape:** (empty)

  ---
  - **method:** PUT
  - **url:** https://gew4-spclient.spotify.com/connect-state/v1/devices/{deviceId}/inactive
  - **count:** 1
  - **purpose:** Desktop marks itself inactive right after handing playback to Wavee. Returns 204 No Content. 1 sample.
  - **bodyShape:** 9-byte gzip-empty body (X-Transfer-Encoding: gzip)

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/extended-metadata/v0/extended-metadata
  - **count:** 135
  - **purpose:** Batched entity extension fetch. 61 from Wavee, 74 from desktop. Not re-analysed here (covered by prior XM passes).
  - **bodyShape:** BatchedEntityRequest / BatchedExtensionResponse per the established XM shape

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/gabo-receiver-service/v3/events
  - **count:** 48
  - **purpose:** UBI/analytics event batches (40 to gew4-spclient, 8 to spclient.wg with a trailing slash). Payload keys seen: context_application_desktop, context_device_desktop, context_client_id, context_installation_id, app_instance_id, page_instance_id, page_presentation_id, element_path_names/ids/uris/pos/reasons, parent_path_*, dwell_time_ms, play_context_uri, from_page_id, from_entity_uri, ubi_event_creation_timestamp, action_parameter_names/values.
  - **bodyShape:** protobuf event envelope

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist-permission/v1/playlist/{id}/permission/base
  - **count:** 38
  - **purpose:** Desktop-only. Playlist permission base lookup, fired per playlist row render.
  - **bodyShape:** 

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/melody/v1/msg/batch
  - **count:** 27
  - **purpose:** Playback-quality telemetry batches (desktop jssdk video path). Keys: jssdk_playback_start, jssdk_playback_stats, ms_start_position, ms_end_position, ms_played_visible, ms_nominal_played, ms_initial_rebuffer, ms_stall_rebuffer_longest, ms_manifest_latency, ms_resolve_latency, ms_play_latency, ms_head_latency, ms_license_{request,generation,session,update}_latency, key_system_impl, n_total_video_frames, n_dropped_video_frames, n_rendition_downgrade, bps_bandwidth_{avg,max}, time_weighted_bitrate, ms_played_per_{audio,video}_format.
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist/v2/playlist/{id}/diff
  - **count:** 22
  - **purpose:** Desktop-only playlist revision diff polling.
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://audio-{cf|ak}.spotifycdn.com/audio/{fileId}
  - **count:** 27
  - **purpose:** Wavee audio chunk fetches (file ids from storage-resolve).
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/storage-resolve/files/audio/interactive/{fileId}
  - **count:** 25
  - **purpose:** Wavee resolves audio file id → CDN URLs. Response protobuf {1=result(0=CDN),2=cdnurl ×3 (audio-fa.scdn.co / audio-cf.spotifycdn.com / audio-ak.spotifycdn.com, each with its own token scheme),4=fileid(20B),5=ttl=86400}.
  - **bodyShape:** StorageResolveResponse{1 result,2 repeated cdnurl,4 fileid,5 ttl}

  ---
  - **method:** GET
  - **url:** https://heads-fa-tls13.spotifycdn.com/head/{fileId}
  - **count:** 24
  - **purpose:** Audio header/first-chunk prefetch (Wavee playback path).
  - **bodyShape:** 

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/herodotus/spotify.resumption.v1.ResumePointRevisionService/CreateResumePointRevision
  - **count:** 16
  - **purpose:** Wavee writes ONE play-history resume point per track start. `Content-Type: application/x-protobuf`.
  - **bodyShape:** Request{2="spotify:list:play-history:v1", 4={2="2$spotify:track:{id}", 3=google.protobuf.Timestamp{1 seconds,2 nanos}}}. Response{1={1=revision uuid, 2={1=list uri, 6=track uri}, 3=Timestamp(update_time), 4=Timestamp(create_time)}}

  ---
  - **method:** GET
  - **url:** https://spclient.wg.spotify.com/metadata/4/track/{gid}
  - **count:** 16
  - **purpose:** Desktop-only (CORS-preflighted with OPTIONS ×16) legacy track metadata by hex gid.
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/color-lyrics/v2/track/{id}
  - **count:** 17
  - **purpose:** Lyrics fetch. Paired 1:1 with 17 GETs to raw.githubusercontent.com/amll-dev/amll-ttml-db/main/spotify-lyrics/{id}.ttml (Wavee's TTML fallback source).
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/manifests/v9/json/sources/{sourceId}/options/supports_drm
  - **count:** 11
  - **purpose:** Desktop video manifest. JSON: contents[{encoding_id,segment_length,start_time_millis,end_time_millis,profiles[{id,file_type,mime_type,max_bitrate,video_bitrate,video_codec,video_height,video_width,video_resolution}]}].
  - **bodyShape:** {"contents":[{"encoding_id","segment_length","start_time_millis","end_time_millis","profiles":[{"id","file_type","max_bitrate","mime_type","video_bitrate","video_codec","video_height","video_resolution","video_width"}]}]}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/popcount/v2/playlist/{id}/count
  - **count:** 11
  - **purpose:** Desktop-only playlist follower count.
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/context-resolve/v1/{encodedContextUri}
  - **count:** 9
  - **purpose:** Wavee resolves a context uri (playlist/album/track/list) into pages+metadata. Returned JSON metadata carries exactly the keys the desktop later echoes in its `play` command context.metadata (context_description, context_owner, correlation-id, request_id, source-loader, resolved-source, tag, took_time, total_candidates, format_list_type, fetch-limit, preset, madeFor.username, loader-role, playlist.revision, should_show_promo_disclosure, session_control.selected_signals, session_control_display.displayName.*).
  - **bodyShape:** {"metadata":{…},"pages":[…]}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/playplay/v1/key/{fileId}
  - **count:** 7
  - **purpose:** Wavee audio decryption key request.
  - **bodyShape:** 

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/collection/v2/delta
  - **count:** 5
  - **purpose:** Wavee library delta sync. Request protobuf {1=username,2="collection",3=last sync ms as string}. Response {1=1,3=new sync ms as string}.
  - **bodyShape:** Request{1 username,2 set,3 last_sync_ms_string} / Response{1 flag,3 sync_ms_string}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/user-profile-view/v3/profile/{user}
  - **count:** 5
  - **purpose:** Wavee profile lookups (own user, 'spotify', 'qmusicnl', two others).
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist-permission/v1/playlist/{id}/permission/members
  - **count:** 4
  - **purpose:** Desktop-only playlist member list.
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/melody/v1/time
  - **count:** 3
  - **purpose:** Wavee server-time sync (3 samples).
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://apresolve.spotify.com/
  - **count:** 3
  - **purpose:** Access-point resolution (3 samples).
  - **bodyShape:** 

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/herodotus/spotify.resumption.v1.CurrentStateService/ListCurrentStates
  - **count:** 2
  - **purpose:** Desktop-only. Reads resumption state with a CEL filter. 2 samples, BOTH returned 200 with a ZERO-length body.
  - **bodyShape:** Request{2=1000 (page size), 4="cs.resume_point_revisions.exists(revision, revision.update_time > timestamp('2026-07-08T21:47:05.521Z'))"}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/herodotus/spotify.resumption.v1.ResumePointRevisionService/ListResumePointRevisions
  - **count:** 2
  - **purpose:** Desktop-only. Pulls the whole play-history list (69,325-byte response). Wavee only ever writes, never reads.
  - **bodyShape:** Request{2="spotify:list:play-history:v1", 3=500}. Response: repeated {1=uuid,2={1=list uri,6=track uri},3=Timestamp update,4=Timestamp create}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/herodotus/spotify.resumption.v1.ResumePointRevisionService/BatchCreateResumePointRevisions
  - **count:** 1
  - **purpose:** Wavee batches two resume points in one call (used when two tracks completed close together). 1 sample.
  - **bodyShape:** Request{2=repeated CreateResumePointRevisionRequest{2=list uri,4={2="2$<track uri>",3=Timestamp}}}. Response{1={1=list uri,2={1=repeated revision}}}

  ---
  - **method:** POST
  - **url:** https://spclient.wg.spotify.com/playlistextender/extendp/
  - **count:** 2
  - **purpose:** Desktop-only 'Add recommended tracks' for a playlist. Plain JSON, CORS-preflighted.
  - **bodyShape:** Request {"playlistURI": "spotify:playlist:4VC1Y6RR3cjZnSUgCfQ9qn", "trackSkipIDs": [], "numResults": 20}. Response {"recommendedTracks":[{id,originalId,name,duration,explicit,popularity,score,contentRating[],artists[{id,name}],album{id,name,imageUrl,largeImageUrl}}]}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/quicksilver/v2/messages
  - **count:** 2
  - **purpose:** Desktop-only in-app-message poll (CORS-preflighted). Both returned `{}`.
  - **bodyShape:** {}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/context-resolve/v1/autoplay
  - **count:** 1
  - **purpose:** Wavee asks the server what to play after a finite context ends. 1 sample. This is how Wavee gets autoplay continuation for an album.
  - **bodyShape:** Request protobuf {1="spotify:album:5oRsGMXp3MX2bUIaxetMr3", 2="spotify:track:5AhipKYvCLFeyxHAv0EYgg"}. Response JSON {"pages":[{"tracks":[{"uri","uid","metadata":{"decision_id":"ssp~…"}}]}]}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/radio-apollo/v3/tracks/spotify:station:track:{id}
  - **count:** 1
  - **purpose:** Wavee fetches a track-radio station. Query: `?salt=750698&autoplay=true&count=50&isVideo=false`. Response carries `next_page_url` as an `hm://radio-router/v3/tracks/…` URI whose query includes a long `prev_tracks=` comma list — pagination requires echoing played track ids. 1 sample.
  - **bodyShape:** {"next_page_url":"hm://radio-router/v3/tracks/spotify:station:track:{id}?salt=…&autoplay=true&count=50&isVideo=false&prev_tracks=<comma-separated base62 ids>", …}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/net-fortune/v2/fortune
  - **count:** 1
  - **purpose:** Bandwidth/bitrate advice. Query params: `bandwidth=195915636&latency=75&stutter=0&bitrate=160000&request_type=interactive&content_…`. Response protobuf {1="fb86472a-777c-44c5-b639-d01cf897c581" (uuid), 2=1400000 (advised bps)}. 1 sample.
  - **bodyShape:** Response{1 uuid,2 advised_bitrate}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/playlist-publish/v1/subscription/playlist/{id}
  - **count:** 1
  - **purpose:** Desktop-only. Subscribes to publish updates for the algorithmic playlist being played. Empty 200 response. 1 sample.
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist/v2/list/popular-release-segments-main-roles/artist_{artistId}/diff
  - **count:** 1
  - **purpose:** Desktop-only. Backing list for the artist 'popular releases' play context (`spotify:list:popular-release-segments-main-roles:artist_{id}`) that appears as a play-command context uri. 1 sample.
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/presence-view/v2/init-friend-feed/{base64}
  - **count:** 1
  - **purpose:** Wavee friend-activity feed init. 1 sample.
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/gander/v2/GetNotifications
  - **count:** 1
  - **purpose:** Wavee notifications. 1 sample.
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/socialgraph/v4/{userId}/is-following
  - **count:** 1
  - **purpose:** Desktop-only follow-state check. 1 sample.
  - **bodyShape:** 

  ---
  - **method:** POST
  - **url:** https://clienttoken.spotify.com/v1/clienttoken
  - **count:** 1
  - **purpose:** Client token issuance. 1 sample.
  - **bodyShape:** 

  ---
  - **method:** POST
  - **url:** https://login5.spotify.com/v3/login
  - **count:** 1
  - **purpose:** Login5 auth. 1 sample.
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/{artistmix|thisisv3|radio/track}/{id}/en
  - **count:** 4
  - **purpose:** Generated cover art for algorithmic contexts (artistmix ×2, thisisv3 ×1, radio/track ×1).
  - **bodyShape:** 

**notable:**
  1. DEVICE.transfer_data (Device field 4) is NEVER published by Wavee. The real desktop emits a 2,512-byte TransferState blob in every PUT_STATE; Wavee emits Device{1,2,3} only in 27/27 PUTs. The cluster echoes it back as Cluster.transfer_data (field 5), so any device that later takes over from Wavee gets no restore payload. This is the single biggest publish gap.
  2. Wavee's transfer_state.proto MIS-MODELS TransferSession.field 1. It declares `string device_id = 1`, but the wire carries a 117-byte PlayOrigin message: {1='playlist', 2='xpui-snapshot_2026-07-01_1782890476915_7b5cc0c', 3='', 4='', 5='playlist', 6='5ba893a8b2b24f378a9b7bd7a24fe7e6', 7='connect', 8=''}. Parsing an incoming transfer_data with the current proto would fail or silently mangle field 1. Also undeclared on the wire: TransferSession field 6 = a message {6=interaction_id uuid, 7=page_instance_id uuid}, TransferSession field 9 = session_id ('7kGgPY0vyZUk5b5RC5w94c'), TransferSession fields 4 and 5 (both empty strings), TransferPlayerOptions field 5 = repeated modes map-entries (jam=off, media='', context_enhancement=NONE), and TransferState field 7 = {1:''} (2 bytes `0a 00`).
  3. PlayerState fields Wavee never publishes across all 27 PUTs, that the real client does publish: field 4 context_restrictions, field 18 suppressions, field 19 prev_tracks (10 entries in the real PUT), field 33 signals (real emits 4: 'interact', 'speed-preview', 'automix-preview', 'stop-speed-preview'), field 35 session_command_id, field 38 unknown_field_38. Note the capture's Wavee build is 129300667 and predates the 2026-07-28 signals work, so 33/35 may already be fixed.
  4. PlayerState.restrictions (field 17): Wavee emits at most {1,2,6} (23/27 PUTs carry only field 2). The real client emits {2 disallow_resuming, 25 disallow_setting_playback_speed='not_supported_by_content_type', 28 disallow_setting_modes{context_enhancement -> RECOMMENDATION/'not_supported_by_content_type'}, 29 disallow_signals{'switch-to-video'->'no_associated_track', 'switch-to-audio'->'no_associated_track'}, 31='already_set'}. Restrictions field 31 is NOT declared in Wavee's player.proto at all.
  5. PlayerState.options (field 16): Wavee emits the message but EMPTY (zero modes entries) in 26/27 PUTs. The real client always emits exactly three ModeEntry values — context_enhancement='NONE', jam='off', media='' — and the same three appear inside TransferPlayerOptions. Remote UIs read these to decide whether to show the enhance/jam affordances.
  6. PlaybackQuality (PlayerState field 32) has TWO undeclared fields on the wire. The real client emits {1=3 bitrate_level, 2=1 strategy, 3=3 target_bitrate_level, 4=1 target_bitrate_available, 5=1 hifi_status, 6=5, 7=1}. Wavee's player.proto declares only 1–5. Wavee also always reports strategy=4 (cached_file) vs the desktop's 1 (best_matching).
  7. PutStateRequest fields 7 (last_command_sent_by_device_id) and 8 (last_command_message_id) are emitted by the real client (7='5ba893a8b2b24f378a9b7bd7a24fe7e6', 8=604111733) and by Wavee in 0/27 PUTs. Without them the server cannot correlate a state update to the command that caused it, which is what suppresses duplicate/echo command delivery.
  8. DeviceInfo gaps: Wavee omits field 22 public_ip (real: an IPv6 literal) and field 24 audio_output_device_info (real: {1=3 BLUETOOTH, 2='Tivoli M1BT|B0C3A', 5=3}) — so remote UIs show no output-device name for Wavee. Wavee also omits Capabilities 33/34/35/36/38 (all =1 on the real client) and uniquely SETS capability 22 (needs_full_player_state), which the real desktop does not set.
  9. Cluster response carries an undeclared field 13 (varint 0) not present in Wavee's connect.proto Cluster message (which declares 1–11 and reserves 7). Observed on the PUT_STATE response alongside need_full_player_state=1 and needs_state_updates=1.
  10. Wavee is using a STALE queryNpvArtist persisted hash. Wavee sends 047c9c225967d41a763949a4db3f0493e901c9f8689a6537408aabf9beffc177 (2 calls); the 1.2.94.583 desktop sends b2cedf7ed0f29c713567d97ed69b848c8387294edfe58a0e439a3a5669cc27bb (16 calls). Both return 200, but the desktop's version replaces `artistUnion.profile.verified` with `artistUnion.onPlatformReputationTrait.verification.{isVerified,isRegistered}` — Wavee will lose the verified badge when the old hash is retired.
  11. Play commands arrive in two shapes and Wavee must handle both. Playlist plays inline the whole context: context.pages[0].tracks[] with 50 entries, each carrying uid + a large recommender metadata map (item-score, original_index, decision_id, matching_concepts, pinning_index, and ~24 boolean decoration flags such as IN_COLLECTION, HAS_CANVAS, HAS_EXPRESSION_VIDEO, PLAYED_IN_THIS_CONTEXT). Album, single-track, and `spotify:list:popular-release-segments-main-roles:artist_{id}` plays send pages=null and metadata={} — Wavee must resolve them itself via /context-resolve/v1/{uri}.
  12. `update_context` is a distinct inbound endpoint Wavee must implement: it carries a full context (uri/url/metadata/restrictions, pages=null) plus a top-level `session_id` (e.g. '632JGyUj8B3HXeg9lJhy4Z') and NO play_options/prepare_play_options. The desktop sent it 3 times, always a few seconds AFTER the corresponding `play`, to push refreshed context metadata into the running session.
  13. The `play` command's prepare_play_options.skip_to is how the controller picks the starting track, and it is populated inconsistently: playlist play → {track_index:0, track_uid:'66363339353465656430356538386665', track_uri:'spotify:track:5kqIPrATaCc2LqxVWzQGbk'}; album play → {track_uri, track_uid:'0f69b1fa3ee15bf7fb21', track_index:0}; artist-list and single-track plays → {} (empty). Note the uid formats differ (32-hex vs 20-hex vs 16-hex across contexts) so uid must be treated as an opaque string.
  14. add_to_queue always sends `track.uid: ""` and `track.metadata: {}` — only the uri is authoritative, so Wavee must mint its own queue uid. 2 samples.
  15. Connect-state request bodies use the non-standard request header `X-Transfer-Encoding: gzip` (not Content-Encoding), and PUT_STATE responses come back `content-encoding: br` (brotli) even though Wavee's Accept-Encoding is `gzip, deflate, br`. A gzip-only response decoder silently fails here.
  16. Wavee writes play-history resume points (CreateResumePointRevision ×16, BatchCreate ×1) but NEVER reads them — ListResumePointRevisions and ListCurrentStates are desktop-only. ListCurrentStates uses a CEL filter string in field 4 and returned a 200 with an EMPTY body both times (2 samples), so its response shape is unproven from this capture.
  17. Autoplay continuation for a finite context is a single POST to /context-resolve/v1/autoplay with just {1=context_uri, 2=current_track_uri} and returns ready-to-play pages[].tracks[] with uid + decision_id. Track-radio uses a separate GET /radio-apollo/v3/tracks/spotify:station:track:{id}?salt=…&autoplay=true&count=50&isVideo=false whose next_page_url is an `hm://` URI requiring the full played-track list echoed back as `prev_tracks=`.
  18. Two pathfinder responses (recentSearches sess 510, searchTopResultsList sess 528) are truncated by the capture at ~64KB and could not be parsed as JSON — their response shapes are unknown from this file, not absent.
  19. net-fortune/v2/fortune is a bitrate-advice endpoint Wavee does not appear to consult in its own right (the 1 sample lacks a Spotify-App-Version header): it takes measured bandwidth/latency/stutter/bitrate as query params and returns an advised bitrate (1,400,000 bps here) plus a uuid. Worth adopting for adaptive quality.
