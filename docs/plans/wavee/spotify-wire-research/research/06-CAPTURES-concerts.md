# concerts.saz + concerts_v2.saz — concert ops

> Workflow agent output, run `wf_5a5408b2-258`.
- **summary:** Two captures (concerts.saz, 156 sessions, 2026-07-13; concerts_v2.saz, 113 sessions, 2026-07-14) of the Spotify desktop client (xpui 1.2.93.667, app-platform Win32_ARM64) driving the Live Events / concerts surface: opening an artist page for Henry Moodie (spotify:artist:7hr9W3IjXcm3UlLY7guLk5), opening the concerts hub, changing the saved location (New York geonameId 5128581 -> Amsterdam 2759794 via lat/lon reverse lookup + saveLocation), sweeping the radius filter (25/50/100 km), applying date-range and genre-concept filters, and finally opening a concert detail page (spotify:concert:1eG6vqzYQWIJxysjL4q4Ui). All 12 concert Pathfinder operations Wavee declares appear on the wire, and every one of Wavee's 12 sha256Hash constants is confirmed byte-for-byte — zero contradictions. Non-Pathfinder traffic is incidental (gabo telemetry, playlist permission/diff polling, image CDN, quicksilver).

**operations:**

  ---
  - **operationName:** ArtistConcerts
  - **sha256Hash:** ef53c43b865496b9890b7167eab1dc614a8949ef9451b3c41184ea888de8bd2b
  - **variablesExample:** {"artistUri": "spotify:artist:7hr9W3IjXcm3UlLY7guLk5", "geoHash": "dr5regy3zpwg", "includeNearby": true}
  - **count:** 6
  - **responseShape:** data.artistUnion.{uri, profile.name, headerImage.data.sources[].url, visuals.avatarImage.extractedColors.colorDark.hex}; data.concerts.concerts.items[].data.{uri,title,startDateIsoString,location.city,artists.items[].data.profile.name}; data.nearby.locationName; data.nearby.concerts.items[].data.{same}. NOTE: 4 of 6 samples returned `"nearby":{}` and `"concerts":{}` (empty objects, NOT null and NOT items:[]) — the artist had no announced dates at that moment; a later sample (raw/135) returned populated items. geoHash null is a legal value (3 samples). `concerts` is unfiltered/global, `nearby` is geo-filtered and can be `{"concerts":{"items":[]},"locationName":"Amsterdam"}`.

  ---
  - **operationName:** concertFeed
  - **sha256Hash:** 9cae2dbee3f47904c60bab45256260b3ddb9844d5ef25038c17112619d14ce9a
  - **variablesExample:** {"geoHash": null, "geonameId": null, "dateRange": null, "conceptUris": null, "radiusInKm": null, "paginationKey": null}
  - **count:** 10
  - **responseShape:** data.liveEventsFeed.sections[].{__typename,key,uri,description,isBeta,ubiIdentifier,paginationKey}; sections[].concerts[].__typename + .data{...}; nested sections[].sections[].{key,concerts[]}. Observed section __typename values: LiveEventSection, ConcertCarousel, ConcertGroup, AllEvents. Item wrapper __typename: ConcertV2ResponseWrapper (429 occurrences), PlaylistResponseWrapper (9). Observed section keys: concerts-near-you, recommended-events, all-events, popular-events, date keys ('2026-07-24','2026-08-05'), genre keys ('edm events near you','christian events near you','latin events near you'). ubiIdentifier seen: concerts-near-you, genre-events. paginationKey observed as base64 offset, e.g. "MjU=" (=25). PlaylistResponseWrapper carries data.{name,description,uri,images.items[].sources[].url,extractedColors.colorDark.hex} — e.g. spotify:playlist:37i9dQZF1Fco9hnGkFHwSD 'Concerts Near You'. ConcertV2ResponseWrapper.data = {uri,title,startDateIsoString,location.{city,name},artists.items[].data.{profile.name,uri,visuals.avatarImage.sources[].url}}. CAVEAT: 4 of 10 responses were cut off by Fiddler at exactly 65536 bytes, so shape is a union over the 6 that parsed plus regex over the truncated ones.

  ---
  - **operationName:** userLocation
  - **sha256Hash:** 079939378ca79b67c6d047be9152ea940d21f10bbfa2f5d4cf4d8320d87774c2
  - **variablesExample:** {}
  - **count:** 7
  - **responseShape:** data.me.profile.location.{geoHash,geonameId,name} e.g. {"geoHash":"dr5regy3zpwg","geonameId":"5128581","name":"New York"}. Variables object is literally empty {} in all 7 samples.

  ---
  - **operationName:** concertConcepts
  - **sha256Hash:** a409c1eb39b6345e7993d424d2408b65a6699bafc2b8a03217033e517cd76b72
  - **variablesExample:** {"geohash": "dr5regy3zpwg", "conceptUri": null}
  - **count:** 7
  - **responseShape:** data.concertConcepts.items[].{data.{name,uri},weight(float)}. 21 concepts returned, weight is a float relevance score (4.0/3.0/2.0/1.0 for edm/electronic/latin/pop, 0.0 for the rest). NOTE the variable key is lowercase `geohash` here (contrast `geoHash` in ArtistConcerts/concertFeed) — verified across all 7 samples. Empty string "" is a legal geohash value (1 sample).

  ---
  - **operationName:** concertLocationDetails
  - **sha256Hash:** b13f195349f188fee25480ae889d782852d68663bf07743c654244454750d681
  - **variablesExample:** {"geonameId": "5128581", "isAnonymous": false}
  - **count:** 4
  - **responseShape:** data.concertLocations.items[].{geoHash,geonameId,name} + data.me.profile.location.{geoHash,geonameId,name}. WIRE ODDITY: when geonameId is null the items[0] came back as {"geoHash":"u15pmurhmcgq","geonameId":"","name":""} — empty-string geonameId/name, not null and not an empty array. Clients must tolerate that.

  ---
  - **operationName:** ArtistConcertsPageLocation
  - **sha256Hash:** 320698465a352f0d0247ec8ed02471244106d4199820f99de4d0a785561c2b03
  - **variablesExample:** {}
  - **count:** 3
  - **responseShape:** data.me.profile.location.{geoHash,name} — note this projection omits geonameId (unlike userLocation, which includes it).

  ---
  - **operationName:** inferredUserLocation
  - **sha256Hash:** 5db4c507ea735d2a1f37bd1166eca2c1a0e3387bb875ebca5d6031b6eccceeba
  - **variablesExample:** {}
  - **count:** 3
  - **responseShape:** data.me.profile.location.isInferred (bool) — that single field only. Observed true.

  ---
  - **operationName:** concertCount
  - **sha256Hash:** 29be9d486e073a49268e13ed9e2d2180187e669fcb7a19b98011aca7ab61b141
  - **variablesExample:** {"geonameId": "5128581", "radiusInKm": 100, "dateRange": null, "conceptUris": null}
  - **count:** 3
  - **responseShape:** data.concerts.concerts.totalCount (int) — doubly-nested `concerts.concerts`. Observed 9191 for geonameId 5128581 / radius 100. Note: no geoHash and no paginationKey in the variables (subset of concertFeed's variables).

  ---
  - **operationName:** concert
  - **sha256Hash:** 21afefc1c7f9e38cbf7c60d03f5c8b6e602b7a91e04f2c2e0aa7d1743052768e
  - **variablesExample:** {"uri": "spotify:concert:1eG6vqzYQWIJxysjL4q4Ui", "authenticated": true}
  - **count:** 1
  - **responseShape:** 1 sample, 19923 bytes. data.concert.{__typename:'ConcertV2', uri, title, startDateIsoString('2026-08-31T19:30-04:00'), doorsOpenTimeIsoString(null), ageRestriction(''), festival(bool), saved(bool), status('UNKNOWN')}; .location.{name,city,region,country,coordinates.{latitude,longitude},metroAreaLocation.{fullName,geonameId}}; .venue.data.uri ('spotify:venue:3Y0OWuOCXAVpzihKC7BfC0'); .offers.{totalCount, items[].{providerName('Bandsintown'),providerImageUrl,url,urlType('TICKET_PAGE'),saleType('on-sale'),availability('UNKNOWN'),accessCode(64-hex),firstParty,hasPromoCodes,currency(null),minPrice(null),maxPrice(null),dates.{startDateIsoString,endDateIsoString}}}; .artists.{totalCount, items[].{_uri, data.{__typename:'Artist',uri,profile.name,headerImage.data,visualIdentity.wideFullBleedImage,goods.concerts.totalCount,relatedContent.featuringV2.totalCount,discography.popularReleasesAlbums.items[].{uri,name,coverArt.sources[].{url,width,height},sharingInfo.{shareId,shareUrl},artists.items[].{uri,profile.name}}}}}; .concepts.items[].{data.{name,uri},weight}; .relatedConcerts.{totalCount, items[].{_uri,data.{uri,title,startDateIsoString,festival,firstParty,artists.totalCount,location.{name,city,region(can be null),country,coordinates,metroAreaLocation.{fullName,geonameId}}}}}; .data.me.profile.location.name

  ---
  - **operationName:** concertLocationsByLatLon
  - **sha256Hash:** 8a059d072a17a1199feb21fe846271f1680eda87010c832852ced0c55c6c7c96
  - **variablesExample:** {"lat": 52.36196536090799, "lon": 4.873349445609038}
  - **count:** 1
  - **responseShape:** data.concertLocations.items[].{geonameId,name} — e.g. {"geonameId":"2759794","name":"Amsterdam"}. Note this projection has NO geoHash (contrast concertLocationDetails). Variable keys are short `lat`/`lon`, full float precision. 1 sample.

  ---
  - **operationName:** saveLocation
  - **sha256Hash:** 5502351e9f201ae29014ca55d3b24b755ba261a1a9eb35fb498cb4c7df419353
  - **variablesExample:** {"geonameId": "2759794"}
  - **count:** 1
  - **responseShape:** data.storeUserLocation.success (bool) — mutation field is named storeUserLocation, not saveLocation. 1 sample, returned true.

  ---
  - **operationName:** searchConcertLocations
  - **sha256Hash:** 43ededefcba8b3f519fd0c2d6c025dfeec9f742cf47d04a3c3711d95b27deda3
  - **variablesExample:** {"query": ""}
  - **count:** 1
  - **responseShape:** data.concertLocations.items[] — 1 sample only, and it was the EMPTY query (""), which returned items:[]. Hash is confirmed but the populated item shape for this op was never observed; presumably {geonameId,name} like concertLocationsByLatLon, but that is inference, not wire fact.

  ---
  - **operationName:** isFollowingUsers
  - **sha256Hash:** c00e0cb6c7766e7230fc256cf4fe07aec63b53d1160a323940fce7b664e95596
  - **variablesExample:** {"ids": ["spotify:user:..."]}
  - **count:** 11
  - **responseShape:** incidental to the concert flows (playlist/profile chrome), not decoded in detail

  ---
  - **operationName:** searchSuggestions
  - **sha256Hash:** 556f5a15b2fdd3a7113ffd377ad9805e38a3a27b8bb1ca7d6d76bad54aa8ee12
  - **variablesExample:** {"query":"..."}
  - **count:** 5
  - **responseShape:** incidental — matches the already-verified searchSuggestions hash

  ---
  - **operationName:** recentSearches
  - **sha256Hash:** 2520a5aa49f29d20cd3187261b8778fdd55654200801df9768f8a5decf05330b
  - **variablesExample:** {}
  - **count:** 2
  - **responseShape:** incidental. NOTE this exact hash also hosts operationName saveRecentSearches (1 sample) — a live example of one persisted-query hash serving multiple named operations.

  ---
  - **operationName:** saveRecentSearches
  - **sha256Hash:** 2520a5aa49f29d20cd3187261b8778fdd55654200801df9768f8a5decf05330b
  - **variablesExample:** {}
  - **count:** 1
  - **responseShape:** same hash as recentSearches

  ---
  - **operationName:** queryArtistOverview
  - **sha256Hash:** ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a
  - **variablesExample:** {"uri":"spotify:artist:7hr9W3IjXcm3UlLY7guLk5"}
  - **count:** 1
  - **responseShape:** incidental — this is the hash previously flagged as STALE; it was current as of these 2026-07-13/14 captures, so the staleness postdates them.

  ---
  - **operationName:** searchTopResultsList
  - **sha256Hash:** 63a93cc04f6d8dea84a85de315e43f396a76cb681500de9ac5ccf5fc618c84cb
  - **variablesExample:** {"searchTerm":"..."}
  - **count:** 1
  - **responseShape:** incidental

**endpoints:**

  ---
  - **method:** POST
  - **url:** https://api-partner.spotify.com/pathfinder/v2/query
  - **count:** 68
  - **purpose:** All GraphQL persisted queries incl. all 12 concert ops
  - **bodyShape:** {"variables":{...},"operationName":"NAME","extensions":{"persistedQuery":{"version":1,"sha256Hash":"..."}}}. Request headers of note: app-platform: Win32_ARM64, spotify-app-version: 1.2.93.667, accept-language: en, Origin/Referer https://xpui.app.spotify.com. No concert op used GET/APQ — all POST.

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/gabo-receiver-service/v3/events
  - **count:** 41
  - **purpose:** Client telemetry batch (UBI events). The concert feed sections carry ubiIdentifier values (concerts-near-you, genre-events) that feed this.
  - **bodyShape:** protobuf event batch, not decoded

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist-permission/v1/playlist/{id}/permission/base
  - **count:** 34
  - **purpose:** Per-playlist base permission probe, fired for playlists in the left rail — unrelated to concerts
  - **bodyShape:** 

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/extended-metadata/v0/extended-metadata
  - **count:** 24
  - **purpose:** Extended metadata batches during these sessions. Kinds requested: TRACK_V4, IDENTITY_TRAIT, PLAYBACK_TRAIT, VISUAL_IDENTITY_TRAIT, CONSUMPTION_EXPERIENCE_TRAIT, SMART_SHUFFLE, and kind 249 (ContentExperienceTrait). NO concert-specific extension kind was ever requested — concerts are 100% Pathfinder, zero XM.
  - **bodyShape:** BatchedEntityRequest -> BatchedExtensionResponse. type_urls seen: spotify.contentagnostic.v2.IdentityTrait (932), spotify.metadata.Track (231), spotify.contentagnostic.v2.ConsumptionExperienceTrait (215), spotify.contentagnostic.v2.VisualIdentityTrait (102), spotify.traits.v1.RootlistabilityTrait (8), spotify.contentagnostic.v2.PlaybackTrait (6), spotify.contentagnostic.v2.OnPlatformReputationTrait (5), spotify.contentagnostic.v2.ContentExperienceTrait (3), spotify.smartshuffle.SmartShuffle (1)

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist/v2/playlist/{id}/diff?…
  - **count:** 17
  - **purpose:** Playlist revision diff polling — unrelated to concerts
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://image-cdn-fa.spotifycdn.com/image/{hex}
  - **count:** 13
  - **purpose:** Artist header / avatar images referenced by concert responses
  - **bodyShape:** binary image

  ---
  - **method:** GET
  - **url:** https://image-cdn-ak.spotifycdn.com/image/{hex}
  - **count:** 6
  - **purpose:** Image CDN mirror
  - **bodyShape:** binary image

  ---
  - **method:** GET
  - **url:** https://audio-cf.spotifycdn.com/audio/{hex}?…
  - **count:** 5
  - **purpose:** Audio chunk fetch (playback continued during capture)
  - **bodyShape:** binary

  ---
  - **method:** GET
  - **url:** https://audio-ak.spotifycdn.com/audio/{hex}?…
  - **count:** 5
  - **purpose:** Audio chunk fetch mirror
  - **bodyShape:** binary

  ---
  - **method:** GET
  - **url:** https://concerts.spotifycdn.com/images/concerts-near-you/playlist_image.jpg
  - **count:** 3
  - **purpose:** Cover art for the 'Concerts Near You' editorial playlist (spotify:playlist:37i9dQZF1Fco9hnGkFHwSD) surfaced in the feed's ConcertCarousel
  - **bodyShape:** JPEG

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/gabo-receiver-service/public/v3/events
  - **count:** 2
  - **purpose:** Unauthenticated telemetry variant
  - **bodyShape:** 

  ---
  - **method:** POST
  - **url:** https://spclient.wg.spotify.com/gabo-receiver-service/v3/events/
  - **count:** 2
  - **purpose:** Telemetry via the wg. host
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/quicksilver/v2/triggers?trig_type=URI&trig_type=CLIENT_EVENT&ctv_type=web-modal&
  - **count:** 1
  - **purpose:** In-app-message trigger registration — tells the client which URIs open a modal
  - **bodyShape:** [{"type":"URI","pattern":"spotify:collection","format":"web-modal","cache":false},{..."spotify:home"...},{..."spotify:search"...},{..."spotify:open"...},{"type":"CLIENT_EVENT","pattern":"app:update:eol","format":"web-modal","cache":false}]

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/quicksilver/v2/messages?ctv_type=web-modal&trigger=spotify%3Asearch&action=DISMISS&action=URL&action=EXTERNAL_URL&locale=…
  - **count:** 1
  - **purpose:** Fetch the in-app message for a fired trigger (preceded by an OPTIONS preflight)
  - **bodyShape:** {}  (no message)

  ---
  - **method:** GET
  - **url:** https://tickets.spotifycdn.com/partner-assets/bandsintown.png
  - **count:** 1
  - **purpose:** Ticket-provider logo — this exact URL is what concert.offers.items[].providerImageUrl points at
  - **bodyShape:** PNG

  ---
  - **method:** GET
  - **url:** https://concerts.spotifycdn.com/ConcertHubCard.png
  - **count:** 1
  - **purpose:** Concert hub entry-point card art
  - **bodyShape:** PNG

  ---
  - **method:** GET
  - **url:** https://concerts.spotifycdn.com/images/live-events_category-image.jpg
  - **count:** 1
  - **purpose:** Live Events category tile art (search browse category)
  - **bodyShape:** JPEG

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist/v2/list/popular-release-segments-main-roles/artist_{id}/diff?…
  - **count:** 1
  - **purpose:** Artist popular-releases pseudo-playlist diff (artist page), not concerts
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist/v2/playlist/{id}
  - **count:** 1
  - **purpose:** Full playlist fetch
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/thisisv3/{id}/en
  - **count:** 1
  - **purpose:** Generated 'This Is' cover art
  - **bodyShape:** image

  ---
  - **method:** POST
  - **url:** https://spclient.wg.spotify.com/user-customization-service/v1/customize
  - **count:** 1
  - **purpose:** Client customization/feature-flag fetch
  - **bodyShape:** 

  ---
  - **method:** GET
  - **url:** https://aet.spotify.com/v2/t?…
  - **count:** 1
  - **purpose:** Ad/experiment tracking pixel
  - **bodyShape:** 

**notable:**
  1. VERDICT: all 12 of Wavee's concert sha256Hash constants in src/apps/Wavee/SpotifyLive/PathfinderClient.cs lines 148-171 are CONFIRMED by the captures, exact 64-hex match, zero contradictions. Confirmed: ArtistConcerts ef53c43b865496b9890b7167eab1dc614a8949ef9451b3c41184ea888de8bd2b; ArtistConcertsPageLocation 320698465a352f0d0247ec8ed02471244106d4199820f99de4d0a785561c2b03; userLocation 079939378ca79b67c6d047be9152ea940d21f10bbfa2f5d4cf4d8320d87774c2; inferredUserLocation 5db4c507ea735d2a1f37bd1166eca2c1a0e3387bb875ebca5d6031b6eccceeba; concertConcepts a409c1eb39b6345e7993d424d2408b65a6699bafc2b8a03217033e517cd76b72; concertFeed 9cae2dbee3f47904c60bab45256260b3ddb9844d5ef25038c17112619d14ce9a; concertCount 29be9d486e073a49268e13ed9e2d2180187e669fcb7a19b98011aca7ab61b141; concertLocationDetails b13f195349f188fee25480ae889d782852d68663bf07743c654244454750d681; searchConcertLocations 43ededefcba8b3f519fd0c2d6c025dfeec9f742cf47d04a3c3711d95b27deda3; concertLocationsByLatLon 8a059d072a17a1199feb21fe846271f1680eda87010c832852ced0c55c6c7c96; saveLocation 5502351e9f201ae29014ca55d3b24b755ba261a1a9eb35fb498cb4c7df419353; concert 21afefc1c7f9e38cbf7c60d03f5c8b6e602b7a91e04f2c2e0aa7d1743052768e. Every one returned HTTP 200 — no PersistedQueryNotFound anywhere in either capture.
  2. CASING TRAP (highest-risk production bug in this area): concertConcepts uses the variable key `geohash` (all lowercase h), while ArtistConcerts and concertFeed use `geoHash` (capital H). Verified across 7 concertConcepts samples and 16 ArtistConcerts/concertFeed samples. A single shared serializer that writes `geoHash` everywhere would silently break concept weighting.
  3. Concerts are a pure-Pathfinder surface: across 269 sessions not one extended-metadata extension kind was requested for a spotify:concert:, spotify:venue:, or spotify:concept: URI. Wavee should not look for a concert XM kind.
  4. Two location projections differ and must not be conflated: userLocation returns {geoHash, geonameId, name}; ArtistConcertsPageLocation returns only {geoHash, name} (no geonameId). concertLocationsByLatLon returns {geonameId, name} (no geoHash); concertLocationDetails returns {geoHash, geonameId, name}. Picking the wrong query for a geonameId-dependent call (concertCount/concertFeed take geonameId, ArtistConcerts takes geoHash) yields a silent null.
  5. EMPTY-OBJECT SENTINEL: ArtistConcerts returned `"nearby":{}` and `"concerts":{}` in 4 of 6 samples — empty JSON objects with no `concerts`/`items` member at all, not null and not an empty array. A deserializer that does data.concerts.concerts.items will NRE/throw on the common no-dates-announced case. Same class of hazard: concertLocationDetails returned an item with geonameId:"" and name:"" when geonameId was null.
  6. saveLocation's response field is `data.storeUserLocation.success` — the mutation name on the wire does not match the operation name. Confirm Wavee's response mapper reads storeUserLocation, not saveLocation (1 sample, returned true).
  7. concertFeed pagination is a base64-encoded numeric offset carried per section: paginationKey "MjU=" == "25". It appears on the section object in the response and is echoed back in the request variables. Feed section keys observed: concerts-near-you, recommended-events, all-events, popular-events, plus date-bucket keys ("2026-07-24", "2026-08-05") and genre keys ("edm events near you"). Section __typenames: LiveEventSection, ConcertCarousel, ConcertGroup, AllEvents. Item wrappers: ConcertV2ResponseWrapper and PlaylistResponseWrapper — the feed mixes a real playlist (spotify:playlist:37i9dQZF1Fco9hnGkFHwSD 'Concerts Near You') in among concerts, so the renderer must handle a non-concert card.
  8. concert detail exposes spotify:venue:{base62} (venue.data.uri) and spotify:concept:{base62} URIs. No query in either capture ever resolved a spotify:venue: URI — venue is a dangling reference with only its uri available. Worth noting before building a venue page.
  9. concertCount and concertFeed share the geonameId/radiusInKm/dateRange/conceptUris filter tuple, but concertCount omits geoHash and paginationKey. Radius sweep 25/50/100 km against geonameId 5128581 produced totalCount up to 9191, i.e. the count query is the cheap way to drive the filter-chip labels without pulling the 65KB+ feed.
  10. Fiddler truncated 4 of the 10 concertFeed responses at exactly 65536 bytes — the feed payload is large (>64KB uncompressed). Any deep-shape claim about the tail of concertFeed sections rests on 6 fully-parsed samples, not 10.
  11. searchConcertLocations is confirmed by hash but only 1 sample exists and it was the empty query ("") returning items:[]. Its populated item shape was never observed on the wire — do not assume it matches concertLocationsByLatLon without a fresh capture.
  12. concert detail's `authenticated: true` variable and the `saved` (bool) response field imply a save/unsave concert mutation exists; no such mutation was captured. Also uncaptured: any concert-notification/reminder op.
  13. Incidental but relevant to other tickets: queryArtistOverview ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a — the hash currently flagged stale — was live and returning 200 in these 2026-07-13/14 captures, so it rotated after that date. And recentSearches / saveRecentSearches both ride sha256Hash 2520a5aa49f29d20cd3187261b8778fdd55654200801df9768f8a5decf05330b, a concrete confirmation that one hash hosts multiple operation names.
