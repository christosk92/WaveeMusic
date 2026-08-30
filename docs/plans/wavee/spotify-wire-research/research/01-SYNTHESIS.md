# Workflow synthesis — implementable Wavee gap list

> Auto-extracted from workflow run `wf_5a5408b2-258` (10 agents, 1.23M tokens, 226 tool calls, 36 min).
> **Unverified agent output.** Claims contested by direct verification are corrected in `../CORRECTIONS.md`.


## Hash divergence

---
- **op:** searchAlbums
- **waveeHash:** 5e7d2724fbef31a25f714844bf1313ffc748ebd4bd199eaad50628a4f246a7ab
- **captureHash:** 64ae1fe6df380b038c0a65a2606d3361bc270de6870b2fdc99cf0848b1efa6d3
- **evidence:** CONFIRMED DIVERGENCE. PathfinderClient.cs:116 hardcodes 5e7d2724…; that string appears nowhere on the wire in any of the 14 captures. The shipping 1.2.94.583 client sends 64ae1fe6… (2 samples, both HTTP 200 with full searchV2.albumsV2 data). Wavee's hash is unverified — it may still resolve (old persisted-query documents do coexist, see home) or it may 400. This is the ONE hash where Wavee's value has zero wire support.

---
- **op:** queryArtistOverview
- **waveeHash:** 7f86ff63e38c24973a2842b672abe44c910c1973978dc8a4a0cb648edef34527
- **captureHash:** ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a
- **evidence:** CONFIRMED DIVERGENCE, but not proven broken. Wavee (PathfinderClient.cs:98, preReleaseV2:false) uses a hash never seen on the wire; the client sends ae0e2958… with preReleaseV2:true, 9 samples all 200. The captured document returns artistUnion.onPlatformReputationTrait.verification.isRegistered and discography.latest/popularReleasesAlbums with label+date{year,month,day,precision} — fields Wavee's variant is not observed to carry. Migration is a feature gain, not just a fix.

---
- **op:** queryNpvArtist
- **waveeHash:** 047c9c225967d41a763949a4db3f0493e901c9f8689a6537408aabf9beffc177
- **captureHash:** b2cedf7ed0f29c713567d97ed69b848c8387294edfe58a0e439a3a5669cc27bb
- **evidence:** BOTH LIVE — not a bug. Wavee's 047c9c22… was observed once, HTTP 200. The shipping client overwhelmingly uses b2cedf7e… (16 samples), which is a strict superset adding artistUnion.onPlatformReputationTrait.verification.{isVerified,isRegistered}. Upgrade for the verified-artist badge, not for correctness.

---
- **op:** home
- **waveeHash:** 9052ac65ff42aefe6d39c45c184d9144cf8dbcc233ea1a76f8649264ad3e7896
- **captureHash:** 5366cbf1f73f8c813dd0f1addc6934950f0dd529cec907107c85851e645c2d16
- **evidence:** NOT STALE — earlier 'stale hash' claim is RETRACTED by the wire. Wavee's 9052ac65… appears in the capture once, HTTP 200, with the identical top-level shape {greeting, homeChips, sectionContainer} as the client's preferred 5366cbf1… (11 samples). No action required. What IS wrong is a variable: Wavee sends timeZone:"Etc/UTC" (LiveSessionHost.cs:941) where the client sends the real IANA zone (Europe/Amsterdam) — the greeting/daylist bucketing depends on it.

---
- **op:** concert ops (all 12)
- **waveeHash:** ef53c43b…, 320698465a…, 079939378c…, 5db4c507ea…, a409c1eb39…, 9cae2dbee3…, 29be9d486e…, b13f195349…, 43ededefcb…, 8a059d072a…, 5502351e9f…, 21afefc1c7…
- **captureHash:** identical
- **evidence:** ZERO DRIFT. concerts.saz + concerts_v2.saz exercise every one of the 12 concert operations Wavee declares and every sha256Hash matches byte-for-byte, including concertCount (29be9d486e073a49268e13ed9e2d2180187e669fcb7a19b98011aca7ab61b141) which is the only one NOT asserted in ConcertCaptureContractTests.cs:18-28. Variable casing quirks Wavee already replicates are confirmed: concertConcepts uses lowercase `geohash`, ArtistConcerts uses `geoHash`. Only gap: add the concertCount hash to the contract test.

---
- **op:** searchTracks / searchArtists / searchPlaylists / searchSuggestions / searchTopResultsList / getAlbum / getTrack / queryAlbumMerch / similarAlbumsBasedOnThisTrack / queryWhatsNewFeed / feedBaselineLookup / fetchExtractedColors
- **waveeHash:** as in PathfinderClient.cs
- **captureHash:** identical
- **evidence:** VERIFIED MATCHING on the wire. No action.


## New Pathfinder operations

---
- **op:** browseAll
- **hash:** dbd8b55e09a58afc52eab438bc228ba28fd72ac2f2148c6c26354980e4579001
- **variables:** {"pagePagination":{"offset":0,"limit":10},"sectionPagination":{"offset":0,"limit":99},"browseEndUserIntegration":"INTEGRATION_DESKTOP"}
- **feature:** The Browse / genre landing grid — returns browseStart.sections.items[].{uri, data.__typename}, a list of spotify:page: URIs. Wavee has NO browse surface at all (grep for Browse* only hits the concerts hub tile and Library search-mode naming). 1 sample, 200.
- **effort:** M — new op + new page + card grid; pairs with browsePage.

---
- **op:** browsePage
- **hash:** f5c4e6d668f5716464a231c1cc8b22c1cbf6ad68b09929fd7de813a30581298b
- **variables:** {"pagePagination":{"offset":0,"limit":10},"sectionPagination":{"offset":0,"limit":10},"uri":"spotify:page:0JQ5DAqbMKFSi39LMRT0Cy","browseEndUserIntegration":"INTEGRATION_DESKTOP","includeEpisodeContentRatingsV2":true}
- **feature:** A single genre/browse page: browse.{header.{title.transformedLabel,subtitle,backgroundImage,color.hex}, sections.{totalCount,pagingInfo.nextOffset,items[].{uri,targetLocation,sectionItems.totalCount,data.{__typename,subtitle}}}}. Reuses the existing home-section renderer. 1 sample, 200.
- **effort:** M — same page shell as home sections.

---
- **op:** getDynamicColorsByUris
- **hash:** f0f112945d6d745bd8ff790317bbf8d310036da75df33130490e9d6dc96c59d9
- **variables:** {"imageUris":["spotify:image:ab67616d0000aa54e86f30ec6f14a30f1cf9bb9d"]}
- **feature:** Server-graded palette, strictly better than the fetchExtractedColors Wavee uses in PlaylistPaletteEnricher.cs:57. Returns dynamicColors[].{bestFit, dark|light.{encoreBaseSetTextColor, highContrast|higherContrast.{backgroundBase,backgroundTintedBase,textBase,textSubdued,textBrightAccent}}} as {red,green,blue,alpha}. Deletes all client-side contrast math from the cover-tint work. NOTE: takes spotify:image: URIs, not https URLs. 7 samples, 200.
- **effort:** S — drop-in alongside the existing enricher + cache.

---
- **op:** queryArtistDiscographyOverview
- **hash:** 5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599
- **variables:** {"uri":"spotify:artist:1McMsnEElThX1knmY4oliG"}
- **feature:** artistUnion.discography.{all,albums,singles,compilations}.totalCount — the tab counts on the artist Discography page. 1 sample, 200. WARNING: this hash also hosts queryArtistDiscographyAll; a hash→operation map keyed on hash alone will be wrong.
- **effort:** S

---
- **op:** queryArtistDiscographyAll
- **hash:** 5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599
- **variables:** {"uri":"spotify:artist:1McMsnEElThX1knmY4oliG","offset":20,"limit":20,"order":"DATE_DESC"}
- **feature:** Paged full discography (offset/limit + order:"DATE_DESC"). Wavee currently only has queryArtistOverview's capped popularReleasesAlbums — this is the fix for the known 10-cap clobber on the artist page. 2 samples, 200.
- **effort:** S–M — paging plumbing on an existing page.

---
- **op:** searchPodcasts
- **hash:** 0195d9f61b43606d490bca64c3456e3593528cea6cc05c7e822c7c42beed0f4e
- **variables:** {"includePreReleases":false,"includeAlbumPreReleases":true,"numberOfTopResults":20,"searchTerm":"wasa","offset":0,"limit":30,"includeAudiobooks":true,"includeAuthors":true,"includeEpisodeContentRatingsV2":true}
- **feature:** Wires SearchFacet.Podcasts, which today throws NotSupportedException at LiveSessionHost.cs:785. Same Vars writer already exists. Returns searchV2.podcasts.items[].data.{uri,name,mediaType}. 2 samples, 200.
- **effort:** S — one constant + one switch arm.

---
- **op:** searchAudiobooks
- **hash:** e05ac765d02c084f8783d3c1572b23d57761c43f47eb8b87ce2f9ccced3fa068
- **variables:** {"includePreReleases":true,"includeAlbumPreReleases":true,"numberOfTopResults":20,"searchTerm":"wasa","offset":0,"limit":30,"includeAudiobooks":true,"includeAuthors":true,"includeEpisodeContentRatingsV2":true}
- **feature:** Wires SearchFacet.Audiobooks (also throws today). NOTE: this is the ONLY search* op that sends includePreReleases:TRUE — the shared Vars writer at LiveSessionHost.cs:731 hardcodes false and needs a per-op override. 1 sample, 200 but empty data.
- **effort:** S

---
- **op:** searchFullEpisodes
- **hash:** d54e35fafe7520cb53883b86d012911cbad75c14ac079a917951c24cdb07c60f
- **variables:** {"searchTerm":"wasa","offset":0,"limit":30,"includeEpisodeContentRatingsV2":true}
- **feature:** Episode results tab. CRITICAL SHAPE NOTE: completely different, much smaller variable object — no include* block at all, so it cannot reuse the shared Vars writer. 5 samples, 200, data empty for both terms sampled.
- **effort:** S

---
- **op:** searchUsers
- **hash:** d3f7547835dc86a4fdf3997e0f79314e7580eaf4aaf2f4cb1e71e189c5dfcb1f
- **variables:** same shared Vars block as searchTracks
- **feature:** searchV2.users.items[].data.{uri,id,username,displayName} — the Profiles search tab. 1 sample, 200.
- **effort:** S

---
- **op:** searchAuthors
- **hash:** 4a9d403a7cbc7e19da5520d619a865472b35382b043bfa458154e73a5c6f46bd
- **variables:** same shared Vars block as searchTracks
- **feature:** searchV2.authors.items[].data.{uri,name,biography,saved,visualIdentity} — audiobook authors. Only useful if audiobooks ship. 2 samples, 200.
- **effort:** S

---
- **op:** recentSearches
- **hash:** 2520a5aa49f29d20cd3187261b8778fdd55654200801df9768f8a5decf05330b
- **variables:** {"limit":50,"includeAuthors":true,"includeEpisodeContentRatingsV2":true}
- **feature:** Server-side recent-search history shown when the search box is focused and empty. 6 samples, all 200 but with an EMPTY data payload (this account had no entries) — so the item shape is UNVERIFIED.
- **effort:** S — but ship with saveRecentSearches or it stays empty.

---
- **op:** saveRecentSearches
- **hash:** 2520a5aa49f29d20cd3187261b8778fdd55654200801df9768f8a5decf05330b
- **variables:** {"uris":["spotify:show:43lKIk7Tt69S4tdCpHWCnH"]}
- **feature:** Mutation that records a picked search result; returns saveToRecentSearches.revisionId. SHARES ITS HASH with recentSearches (query+mutation in one persisted document). 2 samples, 200.
- **effort:** S

---
- **op:** trackPreview
- **hash:** fc26ffc7a1a4f93bd4c2d705649f7dba1de34005b3dc2915549847a9959405d8
- **variables:** {"uris":["spotify:track:6PYyVPaD3pzSbrdQKEDHm6","spotify:track:4WFfPxJv1KRekG6mxn837K","spotify:track:1yANFRps7jfQMe1dfP3sI9"]}
- **feature:** Batch (≈20 URIs) track-preview lookup, fired alongside playlistextender recommendations. Only lookup[].data.uri was observed populated — the preview-URL field shape is UNVERIFIED from these samples. 5 samples, 200.
- **effort:** S, but low confidence in the response shape.

---
- **op:** lookupChildEntities
- **hash:** 91ce02e32b19123de231dc8de91fe4b9ab84eca087d4c015549308d77fbb6d10
- **variables:** {"uris":["spotify:track:75InM94w13mJcj0wCpyaTn","spotify:track:3aQz0z86zrKjd1mcZlonxE"]}
- **feature:** Batch URI → lookupEntities[].{uri, visualIdentityTrait.__typename}. Only the __typename was ever populated in the 5 samples, so this is a thin existence/type probe — extended-metadata kind 179 gives strictly more. LOW VALUE.
- **effort:** S — but probably skip.

---
- **op:** playlistSection
- **hash:** 2615df403a9043c1d7d3094fbeb4c9653b07b11a33d8081fbd31f0f7959ff4a1
- **variables:** {"sectionUri":"spotify:section:0JQ5DAob0LgAOAm50K90Od","playlistUri":"spotify:playlist:37i9dQZF1E8RrQBpL2fW7p"}
- **feature:** Resolves a spotify:section: URI in playlist context — the 'recommended' shelf at the bottom of an editorial playlist page. Only homeSections.sections[].__typename observed; item shape UNVERIFIED. 1 sample, 200.
- **effort:** M — low confidence, needs a fresh capture to pin the shape.

---
- **op:** getCommentsForEntity
- **hash:** bba34fe5f2da3aaa25ab5c90eef1fe2036d325bf32e791ae462b637665185d83
- **variables:** {"uri":"spotify:episode:2xHFjw5aIzfi1aAcnusmEp","token":null}
- **feature:** Podcast episode comments: comments[].{entityUri,eligibilityStatus,totalCount,nextPageToken,items[].{uri,commentString,author.__typename,createDate.isoString,isPinned,isSensitive,isPendingReview,numberOfRepliesWithThreads,replies[],reactionsMetadata.{numberOfReactions,usersReactionUnicode,highlightedReactions[]}}}. Read-only; no write mutation was captured. 1 sample.
- **effort:** M — whole new UI surface, podcast-only.


## New REST endpoints

---
- **endpoint:** GET {spclient}/recently-played/v3/recently-played?limit=50&filter=default,collection-new-episodes
- **feature:** The entire 'Recently played' rail in ONE protobuf call. Wavee has zero references to this path and reconstructs recents from the home response / local state instead.
- **shape:** content-type vnd.spotify/collection-favorites; repeated f1={f1 context uri, f2 played_at ms, f3 last-played track uri}. Context uri includes spotify:station:* and spotify:user:{id}:collection. 2 samples, 200.

---
- **endpoint:** GET {spclient}/device-capabilities/v1/capabilities?device_type=computer&client_id=…&device_model=…&client_version=…
- **feature:** AUTHORITATIVE entitlement source. Wavee currently infers quality tiers. Determines HiFi eligibility, DJ support and the legal set of media types.
- **shape:** JSON {license:"tft", effective_license:"premium", supported_media_types:["audio/track","audio/episode","audio/dj","audio/media","audio/agnostic","audio/ad","audio/interruption"], supported_audio_quality:"HIFI", audio_quality:"HIFI_24", supports_hifi:{fully_supported,user_eligible,device_supported}, supports_dj:true, supports_observing:true, supports_external_episodes:true, supports_v2_playlist_uris:false, supports_playback_speed:false, ad_beacon_reporting:false, is_dynamic_device:false, is_voice_enabled:false, debug_client_type:"client-zelda"}. 2 samples.

---
- **endpoint:** GET {spclient}/popcount/v2/playlist/{id}/count
- **feature:** Playlist follower count — the '128,345,311 saves' line on a playlist header. 54 calls in capture; Wavee does not implement it.
- **shape:** protobuf {f1:0, f2:1, f7: varint count, f8:1}

---
- **endpoint:** POST {spclient}/playback-settings/spotify.playbacksettings.PlaybackSettingsService/GetAllStoredContentValues (+ GetSettingsDeviceSelection, WriteContentValue)
- **feature:** Per-context shuffle/repeat state for EVERY context the user ever touched, in one gRPC call — so reopening a playlist restores its own shuffle mode. Wavee has no equivalent.
- **shape:** REQUEST protobuf {f1:1000}. RESPONSE: long list of context URIs (album/track/playlist/artist and spotify:list:popular-release-segments-main-roles:artist_*) each with its stored value. 2 samples.

---
- **endpoint:** GET spclient.wg/clip-transcript/v1/transcripts/{episodeUri}?offsets.start=0.000s&offsets.end=60.000s
- **feature:** Word-level podcast preview captions WITH speaker diarization — drives the animated caption on podcast preview cards. 32 calls; Wavee has no reference.
- **shape:** JSON {words:[{word, offsets:{start:"0.160s", end:"0.440s"}, speakerId:"1"}]}

---
- **endpoint:** GET spclient.wg/transcript-read-along/v2/episode/{episodeId}[/{lang}] and GET https://episode-transcripts.spotifycdn.com/1.0/spotify:transcript:{id}
- **feature:** Full synced podcast transcript (the podcast analogue of color-lyrics, which Wavee already renders). URLs are handed to you by extension kind 21, so no discovery needed.
- **shape:** URLs confirmed on the wire inside kind 21 payloads (f2.4 = cdn url, f2.6 = read-along url); the RESPONSE BODY of these two URLs was NOT captured — shape UNVERIFIED.

---
- **endpoint:** POST spclient.wg/assisted-curation/v1/recommendations/curation/uri
- **feature:** 'Add shows' suggestions for a playlist — the non-music sibling of /playlistextender/extendp/ which Wavee already calls (PlaylistExtenderClient.cs:34). Wavee has zero references to assisted-curation.
- **shape:** REQUEST {"curation_uri":"spotify:playlist:{id}","suggested_audiobooks":{},"skip_item_uris":[],"limit":5}. RESPONSE {"uris":["spotify:show:…"]} — bare URIs, needs an extended-metadata follow-up. 1 sample.

---
- **endpoint:** GET https://subtitles.spotifycdn.com/subtitles/v1.1/sources/{fileId}/en-us.webvtt?__token__=exp=…~acl=…~hmac=…
- **feature:** Video-podcast / music-video subtitles. Directly relevant to Wavee's video-surface work.
- **shape:** WEBVTT text, HH:MM:SS.mmm --> cue ranges. 1 sample.

---
- **endpoint:** GET {spclient}/storage-resolve/v2/files/audio/interactive/{n}/{fileId}?product=0
- **feature:** v2 form of the storage-resolve Wavee already calls at LiveTrackResolver.cs:233. Worth adopting only if the v1 form starts failing — no observed behavioural difference.
- **shape:** protobuf {f1 result enum, f2 repeated cdnurl, f4 fileid, f5 ttl=86400}

---
- **endpoint:** GET {spclient}/playlist/v2/list/recents/main/diff, /playlist/v2/list/whats-new/podcasts, /playlist/v2/list/podcast-chapters/{uri}
- **feature:** Pseudo-playlist list endpoints reusing the SelectedListContent protobuf Wavee's PlaylistFetcher already parses — recents, the podcast What's New list, and podcast chapters. Cheap reuse of existing decode.
- **shape:** same SelectedListContent protobuf as /playlist/v2/playlist/{id}

---
- **endpoint:** POST {spclient}/playlist-publish/v1/subscription/playlist/{id}
- **feature:** Subscribes to an editorial playlist's update stream (fired on open, so the page live-updates when Spotify re-cuts it). Empty request, empty 200. 3 samples.
- **shape:** empty body both directions

---
- **endpoint:** GET {spclient}/socialgraph/v4/{username}/is-following?limit=1000
- **feature:** Bulk 'am I following this artist/user' check for follow buttons. 2 samples but EMPTY body in both — response shape UNVERIFIED.
- **shape:** unknown (empty in all samples)

---
- **endpoint:** POST spclient.wg/listening-activity/v1/audience and POST /profile-privacy/v2/read-settings
- **feature:** Friend-activity audience + privacy settings; complements the /presence-view feed Wavee already consumes.
- **shape:** audience REQUEST {"unused":true} RESPONSE {"users":[]} (1 sample, empty). read-settings: protobuf request = username.

---
- **endpoint:** GET {spclient}/net-fortune/v2/fortune
- **feature:** Startup bandwidth-hint token (f2 = 1400000 bps). Would let Wavee pick an initial video/audio ladder rung instead of guessing. Marginal.
- **shape:** protobuf {f1: uuid string, f2: 1400000}. 3 samples.

---
- **endpoint:** PUT {spclient}/clientsettings/api/v1/
- **feature:** Pushes a client setting server-side; observed carrying the preferred locale so other Spotify surfaces agree with the app.
- **shape:** protobuf {f1:"preferred-locale", f2:{f4:{f1:"en"}}}. 2 samples.

---
- **endpoint:** GET spclient.wg/library-import/v1/eligible
- **feature:** Whether to offer the 'import your library from another service' flow. Trivial.
- **shape:** JSON {"eligible":false}. 2 samples.

---
- **endpoint:** POST {spclient}/offline/v1/devices/{deviceId}/cache/{cacheId}/disable and .../resources:delta
- **feature:** Server-side offline-cache lifecycle. Only relevant when/if Wavee ships downloads — and note kind 239 reports offline as RESTRICTED on this premium/NL account.
- **shape:** empty request, 204. 2 samples.

---
- **endpoint:** POST {spclient}/connect-state/v1/cluster/wake-devices
- **feature:** Wakes idle Connect devices before showing the device picker, so the list is populated instead of stale. 1 sample, empty response.
- **shape:** empty body

---
- **endpoint:** GET https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/{radio|artistmix|daily|topic|thisisv3|dw|release-radar-v4}/{seedId}/{locale} and https://seed-mix-image.spotifycdn.com/v6/img/desc/{Mood Name}/{locale}/default
- **feature:** Generated mix/radio and mood-descriptor cover art. NOT an API — the URL is fully templated from the mix type + seed id, so Wavee can construct it with zero requests. 50 samples.
- **shape:** jpeg

---
- **endpoint:** EXCLUDED as ad/telemetry/experiment surface (do not implement): POST /gabo-receiver-service/v3/events (Wavee already has GaboBatcher), POST /melody/v1/msg/batch, POST /ads/v3/ads?slots=hpto, GET https://aet.spotify.com/v2/t, POST /podcast-ap4p/leavebehinds/ads, GET /desktop-update/v2/update, POST /capping-api/…/PutConsumption, GET /quicksilver/v2/{triggers,messages} (in-app promo modals; returned {} in every sample), GET raw.githubusercontent.com/amll-dev/amll-ttml-db (a third-party Spicetify lyrics mod in the captured session, not Spotify).
- **feature:** n/a — listed so they are not mistaken for gaps.
- **shape:** n/a


## Extended-metadata opportunities

---
- **kind:** 16
- **name:** CANVAS_V1 (spotify.canvaz.cache.EntityCanvazResponse.Canvaz)
- **feature:** Canvas looping video behind the now-playing art — the single most visible missing piece. Wavee requests kinds 10/9/8/11/12/5/15/85/99/151/182/205/212 and NOT 16. Coverage ~29% of queried tracks (191x200, 426x304, 475x404 — a 404 is normal, not an error).
- **payloadShape:** f1=32-hex canvas id; f2='https://canvaz.scdn.co/upload/{artist|licensor}/{artistId}/video/{canvasId}.cnvs.mp4' (also .../image/... for a still); f3=second 32-hex digest; f4=varint always 3; f5=track uri; f6={1 artist uri,2 artist name,3 artist image url}; f8='artist'|'licensor'; f11='spotify:canvas:{id}'; f13=REPEATED {1 width,2 height,3 poster url}, always exactly 512x288 and 256x144. Fields 9/10/12 never observed.

---
- **kind:** 239
- **name:** ContentCapabilityTrait (spotify.contentagnostic.v2.ContentCapabilityTrait)
- **feature:** THE server-side video/offline kill switch — directly load-bearing for Wavee's video-detection campaign. Always co-requested with canvas/video kinds by the real client. 665x200 / 611x304.
- **payloadShape:** Four parallel capability slots; the SET FIELD NUMBER inside each slot is the verdict and f3 carries a reason map {f1=key,f2=value}. track = `1{2}, 2{3{1{1='offline',2='1'}}}, 3{1}, 4{4}` or slot4=`4{3{1{1='music-video-disabled',2='1'}}}` (223 of 665 samples). episode = `1{3}, 2{1}, 3{1}, 4{4}` or slot4 reason 'other-video-disabled'. CONFIRMED: slot2=offline/download, slot4=video. INFERRED-ONLY: slot1 and slot3 meanings (never carried a reason string).

---
- **kind:** 178
- **name:** IDENTITY_TRAIT (spotify.contentagnostic.v2.IdentityTrait)
- **feature:** Cheap display tuple that renders a full track/list row WITHOUT a TrackV4 fetch — the real client requests it on EVERY 300-entity row batch. 130,846x200. Note 24x per-entity 451 (legal block) — Wavee's parser must handle 451/400 per entity, not just 200/304/404.
- **payloadShape:** f1=str class label 'Song'|'Playlist'|'Episode'; f2=str display name; f3=str subtitle (rare, 1/3000); f4={1 album/collection name, 2 album uri}; f5=REPEATED {1 creator name, 2 uri} where uri may be spotify:artist OR spotify:user (spotify:user:spotify for editorial).

---
- **kind:** 179
- **name:** VISUAL_IDENTITY_TRAIT (spotify.contentagnostic.v2.VisualIdentityTrait)
- **feature:** Per-entity image set PLUS a baked colour scheme — obsoletes a separate fetchExtractedColors round trip for anything carrying it. 130,689x200.
- **payloadShape:** f1.1=REPEATED image {1{1=url}, 2=size enum}; enum→url prefix exact over 349 samples: 1=…00004851 (64px), 2=…00001e02 (300px), 3=…0000b273 (640px), 4=playlist/mosaic 1280 only. f1.2 = three colour variants f1.2.1/.2/.3, each five RGBA quads {1=r,2=g,3=b,4=a}; slot3 always 255,255,255,255 and variant3 slot5 always 30,215,96,255 (Spotify green); slots 1–2 entity-derived. f1.2.4 = one standalone RGBA. That the three variants are contrast tiers is INFERRED, not proven.

---
- **kind:** 185
- **name:** ON_PLATFORM_REPUTATION_TRAIT (spotify.contentagnostic.v2.OnPlatformReputationTrait)
- **feature:** Per-track play count for the artist-Popular chart and album track rows — Wavee currently gets playcount only via GraphQL getAlbum/queryArtistOverview. 100x200, never a 304 (always refetched).
- **payloadShape:** Exactly ONE field: f3 = varint, 95 distinct values over 100 samples, range 141,184,618 – 2,138,988,345. 'Play count' is INFERRED from magnitude; the wire does not name the unit.

---
- **kind:** 222
- **name:** audio_attributes.v2.AudioAttributes
- **feature:** Ready-made BPM + musical key + Camelot chip per track. IMPORTANT CORRECTION to the earlier pass: at scale it is requested with 85/98/99/182 on ordinary track batches, NOT only under the mix lens. 9,842x200 / 2,056x304 over 11,898 queries.
- **payloadShape:** f1=fixed64 double BPM (80.0–145.38, both exact integers and fitted values like 128.001); f2={1=str pitch class (12 distinct), 2=varint mode (only 1 and 2), 3={1=str camelot code (24 distinct '1B'..'12B','8A'..), 2=str hex colour, one-to-one with the code, e.g. '#ee82d9','#04ebeb'}}.

---
- **kind:** 6
- **name:** TRACK_DESCRIPTOR (spotify.descriptorextension.ExtensionDescriptorData)
- **feature:** User-visible mood/genre tag chips per track ('Quiet','K-Pop','Nostalgia'). Always requested alone, 95% with an etag.
- **payloadShape:** REPEATED f1 = {1=lowercase slug, 2=fixed32 float confidence 0.0166–0.9749 sorted descending, 3=PACKED repeated varint category ids, 4='spotify:concept:{id}', 5=Title-Cased display label}. Category ids: 1 co-occurs with genre words and 2 with mood words across 43 descriptors (clean correlation, but INFERRED); ids 3,6,7,9,10,11,16,17 meaning undetermined.

---
- **kind:** 21
- **name:** EPISODE_TRANSCRIPTS (spotify.corex.transcripts.metadata.EpisodeTranscript)
- **feature:** Podcast transcript discovery — hands you both the CDN and the read-along URL, i.e. the podcast equivalent of the synced lyrics Wavee already renders. 188x200 / 318x304, but only 12 of 188 carried any entry (most 200s mean 'no transcript').
- **payloadShape:** f1=entity uri; f2=REPEATED {1='spotify:transcript:{id}', 2=locale 'en-us'|'de-de'|'fr-fr'|'pt-br', 4='https://episode-transcripts.spotifycdn.com/1.0/spotify:transcript:{id}', 6='https://spclient.wg.spotify.com/transcript-read-along/v2/episode/{episodeId}[/{lang}]'}. f2.3 and f2.5 never populated.

---
- **kind:** 54
- **name:** HTML_DESCRIPTION (spotify.podcast.extensions.PodcastHtmlDescription)
- **feature:** The rich HTML episode/show description with links — directly user-visible copy Wavee has no source for today. 163x200 / 343x304.
- **payloadShape:** f2 = raw HTML string, entity-escaped (&#39;, &#34;, <p>, <br/>). f1 never observed populated. 4 of 163 contained non-UTF8-printable bytes; still HTML.

---
- **kind:** 28
- **name:** CUEPOINTS (spotify.automix.proto.Cuepoints)
- **feature:** Real automix/crossfade in/out points — pairs with kind 136/the transition map embedded in kind 212 that Wavee already fetches for video. Feeds the crossfade prepared-next work. 6 samples.
- **payloadShape:** f1=start cue, f2=end cue, f3=REPEATED start-side candidates (105 across 6 tracks), f4=REPEATED end-side (114). Each cue {1=varint position ms (710–10570 head, 175731–237152 tail), 2=fixed32 float BPM identical within a track, 3=varint 1, 4=fixed64 double confidence 0–1 — the chosen f1/f2 is NOT always the max}.

---
- **kind:** 183
- **name:** PUBLISHING_METADATA_TRAIT (spotify.contentagnostic.v2.PublishingMetadataTrait)
- **feature:** Structured release date + the © / ℗ copyright lines for album/show/chapter headers. 14x200, 87% with etag.
- **payloadShape:** f1{3{1=year,2=month,3=day}}; f2{1=unix seconds}; f3{1=unix seconds,2=nanos}; f4=REPEATED copyright line, observed '© 2026 broke' and '℗ 2026 broke' as separate entries (only 2 samples).

---
- **kind:** 37
- **name:** PODCAST_RATING (spotify.ratings.PodcastRating)
- **feature:** Show rating widget INCLUDING the current user's own star value — i.e. a write-capable surface Wavee has no equivalent for (the write endpoint itself was not captured). 4 samples.
- **payloadShape:** f1={1=fixed64 double average (4.5578–4.9937), 2=varint rating count, 3=varint 1}; f2 (present on 1 of 4) = the user's own rating {2=show uri, 3=varint 5 stars, 4={1=unix seconds,2=nanos}}; f3=varint 1.

---
- **kind:** 3
- **name:** PODCAST_TOPICS (spotify.podcast.extensions.PodcastTopics)
- **feature:** Genre chips on a show page. 1x200 / 2x304.
- **payloadShape:** REPEATED f1 {1='spotify:genre:{id}', 2='Comedy'} (1 sample)

---
- **kind:** 29
- **name:** PODCAST_POLL (spotify.polls.PodcastPoll)
- **feature:** In-episode poll card, fully user-visible and fully decoded. 1 sample only.
- **payloadShape:** f1{1=varint poll id; 2='2026-07-26T23:00:00' start (naive ISO, no tz); 3=end; 5,6=episode uri; 7=question text; 8=varint 1; 9=REPEATED option {1=option text, 2=varint 5/1/6 (vote count OR ordinal — AMBIGUOUS at n=1), 3=back-pointer to poll id, 4=sequential option id}; 10=varint 2; 12='spotify:poll:{id}'}

---
- **kind:** 149
- **name:** ROOTLISTABILITY_TRAIT (spotify.traits.v1.RootlistabilityTrait)
- **feature:** Whether a playlist may be added to the library — gates the Save affordance. Trivial to consume. 192x200.
- **payloadShape:** single varint f1: 1 for 191 of 192 playlists, 0 for the one spotify:list: entity.

---
- **kind:** 138
- **name:** PRERELEASE (spotify.prerelease.extension.Prerelease)
- **feature:** Pre-release / countdown album cards (What's New and search already surface isAlbumPreRelease flags Wavee ignores). Treat as 1 sample.
- **payloadShape:** f1='spotify:prerelease:{id}'; f2{1=unix release ts}; f3{1=album uri, 2='ALBUM', 3=title, 4={1 artist uri,2 artist name}, 5=REPEATED cover {1=url, 2=SIZE NAME STRING 'SMALL'|'DEFAULT'|'LARGE', 3=width, 4=height}}. NOTE the size is a STRING here vs an integer enum in kinds 179 and 98 — three different image-size encodings across three kinds.

---
- **kind:** 170
- **name:** AUTO_LENS (spotify.autolensextension.v1.AutoLens) + the mix family 217/218/219/225/237/142
- **feature:** DJ/Mix lens. Only worth it if Wavee ships a DJ/waveform feature. 170 is fetched as its OWN single-kind batch BEFORE the family; the family is gated on it. Wavee already POSTs /playlist/v2/playlist/{id}/signals (PlaylistSignalsClient.cs:43) which is what flips this on.
- **payloadShape:** 170: f1 = bare string, ONLY value ever seen is 'mix', cache_ttl 60. 217 mixbeats.Beats: f1=varint 4 beats/bar, f2=REPEATED beat {1=float seconds, 3=varint 1–4 position, 4=float confidence}. 218 VocalActivity: f1=float 22050/44100 rate, f4=varint 315, f5=opaque packed byte array. 237 ThreeBandWaveforms: f1=44100, f2=20, f3/f4/f5 = three ~10KB opaque band envelopes. 219 Mixability: f1=1, f2=double 1.0. 142 ListTunerAudioAnalysis: f2=20, f3=~12.4KB opaque blob.

---
- **kind:** 114
- **name:** WATCH_FEED_ENTITY_EXPLORER (spotify.watchfeedextensions.api.v1.EntityExplorerEntrypointResponse)
- **feature:** The 'swipe through previews of this playlist' entry point card. 4x200.
- **payloadShape:** f3{1='spotify:watch-feed:playlist:{id}?itemId={base64 of a spotify:track: uri}', 2{1=canvaz.scdn.co video url} on 2 of 4, 3{1=preview image/canvas url, 2=literal 'video'}, 4=fixed CTA 'Swipe through previews of tracks in this playlist.', 5='Explore {playlist name}'}

---
- **kind:** 246
- **name:** CurationExperienceTrait (spotify.contentagnostic.v2.CurationExperienceTrait)
- **feature:** Which curated playlists a chapter/show is surfaced in. 11x200 — the 1-vs-2 slot distinction is NOT determinable at n=11.
- **payloadShape:** f1=own uri; f2{2{1='spotify:playlist:…'} for podcast-chapters, or 3{1='show'}}; f3{4{1=REPEATED playlist uri, 2 entries per sample}}

---
- **kind:** 0
- **name:** NEGATIVE FINDINGS — do NOT spend effort here
- **feature:** Kinds that look promising and are not. 80 SHARE_TRAIT: 1,570x200 and the payload contains ONLY the entity's own uri — no share URL/text/template, useless as-is. 249 ContentExperienceTrait and 220 EntityTypeTrait: a single varint fully determined by the URI scheme (track→1, episode→2) — send them to look wire-identical, consume nothing. 98 AUDIO_ASSOCIATIONS: 11,868 of 11,869 queries 404. 85 ORIGINAL_VIDEO: 10,069 200s and EVERY ONE had an ABSENT Any.value — the 200 itself is the signal. 4 PODCAST_SEGMENTS (855x200) and 113 COMPANION_CONTENT (1x200) carried only the entity uri, never a segment/content list. Shape UNOBTAINABLE from this corpus (100% 404/400): 30 EPISODE_ACCESS, 58 CONTENT_WARNING, 31 SHOW_ACCESS, 22 PODCAST_SUBSCRIPTIONS, 88, 52, 64, 27; plus 164 GATED_ENTITY_RELATIONS whose 852 200s were all zero-byte.
- **payloadShape:** n/a

---
- **kind:** -1
- **name:** TRANSPORT RULES CONFIRMED ACROSS 2,718 REQUESTS
- **feature:** Batching/caching conventions Wavee's ExtendedMetadataSource should match. Wavee sends a random 16-byte TaskId (ExtendedMetadataSource.cs header) — harmless, the real client does too in one corpus and omits it in another; it is never echoed.
- **payloadShape:** offline_ttl is the CONSTANT 2592000 (30d) on every kind, every response — hardcodable. cache_ttl is per-entity JITTERED for catalog kinds (10/9/8/85/99/178/179/182: e.g. TRACK_V4 11,662 distinct values over 60–86399) and a fixed constant for trait kinds. Kind 205 is the only kind with NO cache_ttl field at all. Client chunks at 300 entities (795 batches) with a long single-entity tail (644 batches); 300 is a CLIENT convention, not a server cap — 1,425 was accepted in one batch. Etag lives per (entity,kind) inside ExtensionQuery.f2, never as an HTTP header. Canonical list-row bundle = [10,178,179,182,212,249] at 300 entities/batch (398 batches); the same minus TrackV4 when metadata is cached. Video bundle = [85,98,99,182,222]. Canvas bundle = [16,98,99,239]. Playlist bundle = [149,178,179,182,212,225,249]. DECODING TRAP: 2,070 of 2,442 XM responses are zstd and Spotify uses MULTI-FRAME zstd — decompressobj().decompress() silently returns garbage; use stream_reader().


## Ranked implementable list
1. 1. Fix searchAlbums hash 5e7d2724…→64ae1fe6df380b038c0a65a2606d3361bc270de6870b2fdc99cf0848b1efa6d3 (PathfinderClient.cs:116). The only Wavee hash with zero wire support; a stale persisted query 400s and takes the whole Albums search tab with it. Effort: XS (one constant).
2. 2. Consume XM kind 239 ContentCapabilityTrait on every track/episode batch — it is the server telling you 'music-video-disabled'/'other-video-disabled'/'offline', which is exactly the signal the video-detection campaign is currently inferring. Effort: S (kind already reachable via ExtendedMetadataSource).
3. 3. Consume XM kind 16 CANVAS_V1 — looping canvas video behind now-playing, the highest-visibility missing feature; URL, poster renditions (512x288/256x144) and artist attribution all come in one payload. ~29% coverage, 404 is normal. Effort: S for the fetch, M with the video surface (which already exists).
4. 4. Adopt getDynamicColorsByUris f0f112945d6d745bd8ff790317bbf8d310036da75df33130490e9d6dc96c59d9 for cover tinting — a pre-graded dark/light × highContrast/higherContrast palette deletes all client-side contrast math from the palette-tint work. Takes spotify:image: URIs, not https. Effort: S.
5. 5. Add GET /recently-played/v3/recently-played?limit=50&filter=default,collection-new-episodes — the entire Recently-played rail in one protobuf call instead of client-side reconstruction. Effort: S.
6. 6. Wire searchPodcasts 0195d9f61b43606d490bca64c3456e3593528cea6cc05c7e822c7c42beed0f4e and searchAudiobooks e05ac765d02c084f8783d3c1572b23d57761c43f47eb8b87ce2f9ccced3fa068 — LiveSessionHost.cs:785 currently THROWS for the two facets SearchFacet already declares. Watch the includePreReleases:true override on audiobooks. Effort: S.
7. 7. Migrate queryArtistOverview to ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a with preReleaseV2:true — the shipping document, and it adds onPlatformReputationTrait + richer discography.latest/popularReleasesAlbums. Effort: S plus response-mapper work.
8. 8. Add queryArtistDiscographyOverview + queryArtistDiscographyAll (both 5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599, order:"DATE_DESC", offset/limit) — real paged discography, the fix for the known 10-cap clobber. Effort: S–M.
9. 9. Adopt the canonical [10,178,179,182,212,249] 300-entity row bundle. Kinds 178/179 are compact display+image+colour tuples that let a list row render without a TrackV4 round trip, and 179 kills a fetchExtractedColors call per entity. Effort: M (touches the batching layer), but it is the single biggest request-volume win.
10. 10. Consume kind 185 for per-track play counts on the artist-Popular chart and album rows (unit is inferred, magnitude is right). Effort: S.
11. 11. Call GET /device-capabilities/v1/capabilities at startup and stop guessing quality tiers — authoritative supports_hifi / audio_quality:"HIFI_24" / supports_dj / supported_media_types. Effort: S.
12. 12. Add browseAll dbd8b55e09a58afc52eab438bc228ba28fd72ac2f2148c6c26354980e4579001 + browsePage f5c4e6d668f5716464a231c1cc8b22c1cbf6ad68b09929fd7de813a30581298b — the whole Browse/genre tab, which Wavee does not have in any form. Renders through the existing home-section machinery. Effort: M.
13. 13. Add GET /popcount/v2/playlist/{id}/count for the playlist follower count on the header. Effort: XS.
14. 14. Add POST /playback-settings/…/GetAllStoredContentValues so per-context shuffle/repeat is restored when reopening a playlist. Effort: M (gRPC framing already exists for herodotus).
15. 15. Consume kind 222 for a BPM / key / Camelot-colour chip — 9,842 real payloads, and it is NOT mix-lens-gated as previously believed. Effort: S.
16. 16. Consume kind 6 TRACK_DESCRIPTOR for mood/genre chips (slug + confidence + Title-Cased label + spotify:concept: uri). Effort: S.
17. 17. Fix the home timeZone variable: Wavee sends "Etc/UTC" (LiveSessionHost.cs:941), the client sends the real IANA zone — greeting and daylist bucketing depend on it. Wavee's home HASH is fine, do not change it. Effort: XS.
18. 18. Podcast text surfaces, if podcasts are a priority: kind 54 HTML_DESCRIPTION (rich description), kind 21 (transcript URLs) → /transcript-read-along/v2/episode/{id}, kind 3 (topic chips), kind 37 (show rating + the user's own stars), kind 29 (poll card). Effort: M as a group, S each.
19. 19. Add recentSearches + saveRecentSearches (both 2520a5aa49f29d20cd3187261b8778fdd55654200801df9768f8a5decf05330b — one document hosts query and mutation). Ship as a pair; the read alone returned an empty payload in all 6 samples so the item shape is unverified. Effort: S.
20. 20. Split search cadence like the real client: searchSuggestions per keystroke (22 calls for typing 'wasa', limit 30) and the heavy searchTopResultsList (limit 50, sectionFilters GENERIC+VIDEO_CONTENT) only on commit — rather than debouncing one query. Effort: S, pure client-side.
21. 21. Add searchUsers d3f7547835dc86a4fdf3997e0f79314e7580eaf4aaf2f4cb1e71e189c5dfcb1f, searchFullEpisodes d54e35fafe7520cb53883b86d012911cbad75c14ac079a917951c24cdb07c60f (note: completely different, minimal variable shape — cannot reuse the shared Vars writer) and searchAuthors 4a9d403a7cbc7e19da5520d619a865472b35382b043bfa458154e73a5c6f46bd to complete the result tabs. Effort: S each.
22. 22. Upgrade queryNpvArtist to b2cedf7ed0f29c713567d97ed69b848c8387294edfe58a0e439a3a5669cc27bb (strict superset, adds the verified-artist badge). Wavee's current hash still returns 200, so this is optional. Effort: XS.
23. 23. Assert ConcertCountHash 29be9d486e073a49268e13ed9e2d2180187e669fcb7a19b98011aca7ab61b141 in ConcertCaptureContractTests.cs — it is the only one of the 12 concert hashes not covered, and all 12 are otherwise confirmed byte-for-byte with zero drift. Effort: XS.
24. 24. Consume kind 28 CUEPOINTS (+ the transition map already embedded in kind 212) for real automix in/out points, feeding the crossfade prepared-next work. Effort: M.
25. 25. Add POST /assisted-curation/v1/recommendations/curation/uri (suggested shows for a playlist) beside the /playlistextender/extendp/ Wavee already calls — note it returns bare URIs and needs an XM follow-up, unlike extendp which returns fully hydrated tracks. Effort: S.
26. 26. Consume kind 149 ROOTLISTABILITY (gate the playlist Save button) and kind 183 (structured release date + © / ℗ lines on album/show headers). Effort: XS each.
27. 27. Templated art with zero API cost: build pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/{mixType}/{seedId}/{locale} and seed-mix-image.spotifycdn.com/v6/img/desc/{Mood}/{locale}/default directly. Effort: XS.
28. 28. Reuse PlaylistFetcher's SelectedListContent decoder against /playlist/v2/list/recents/main/diff, /list/whats-new/podcasts and /list/podcast-chapters/{uri} — three surfaces for free. Effort: S.
29. 29. Add POST /playlist-publish/v1/subscription/playlist/{id} (editorial playlists live-update) and POST /connect-state/v1/cluster/wake-devices (device picker shows woken devices). Effort: XS each.
30. 30. Video-adjacent extras: subtitles.spotifycdn.com WebVTT for video podcasts/music videos, and /clip-transcript/v1/transcripts/{episodeUri} for word-level diarized preview captions. Effort: S each, only if those surfaces ship.
31. 31. Long tail, implement only on demand: /net-fortune/v2/fortune (bandwidth hint), PUT /clientsettings/api/v1/ (preferred locale), /library-import/v1/eligible, /socialgraph/v4/{user}/is-following (response shape UNVERIFIED — empty in both samples), /listening-activity/v1/audience, /offline/v1/… cache lifecycle, storage-resolve v2, kinds 114/246/138/170+mix family. Effort: XS–S each.
32. 32. DO NOT IMPLEMENT (ad/telemetry/experiment/metering): melody msg batch, ads/v3 hpto, aet.spotify.com beacons, podcast-ap4p leavebehinds, desktop-update, capping-api PutConsumption, quicksilver triggers+messages (in-app promo modals, returned {} in every sample), and the raw.githubusercontent amll-ttml-db traffic (a third-party Spicetify lyrics mod in the captured session, not Spotify API surface). Also skip XM kinds 80, 220, 249, 98 and 85-payloads per the negative findings.
33. 33. Two hard traps for whoever implements this: per-entity XM statuses include 451 and 400, not just 200/304/404, and a 200 with an ABSENT Any.value is a valid 'nothing here' (85 ORIGINAL_VIDEO did this 10,069/10,069 times) — neither is a decode failure; and Spotify's XM responses are MULTI-FRAME zstd, which silently yields garbage through decompressobj().decompress().

