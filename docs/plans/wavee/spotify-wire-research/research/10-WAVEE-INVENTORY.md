# Wavee: what is implemented today (code inventory)

> Workflow agent output, run `wf_5a5408b2-258`.

I have the full picture. Here is the inventory.

---

# Wavee implementation inventory (code as of this working tree)

## 1. Pathfinder operations

All ops are constants in `C:\wavee\fluent-gpu\src\apps\Wavee\SpotifyLive\PathfinderClient.cs` (`PathfinderOps`, lines 95–172). Endpoint is a single literal at `PathfinderClient.cs:14` — `https://api-partner.spotify.com/pathfinder/v2/query`. Body shape built by `PathfinderClient.BuildBody` (line 70): `{"variables":{…},"operationName":…,"extensions":{"persistedQuery":{"version":1,"sha256Hash":…}}}`. Platform is carried by a header (`PathfinderHeadersMiddleware.PlatformHeader`), `Desktop` is the default.

### Catalog / feed ops

| Op | Hash (const line) | Called at | Platform | Variables written |
|---|---|---|---|---|
| `home` | `9052ac65ff42aefe6d39c45c184d9144cf8dbcc233ea1a76f8649264ad3e7896` (`:111`) | `LiveSessionHost.cs:941` (`FetchHomeAsync`, via `UseQueryAsync`) | Desktop | `homeEndUserIntegration:"INTEGRATION_DESKTOP"`, `timeZone:"Etc/UTC"`, `sp_t:""`, `facet:""`, `sectionItemsLimit:10`, `includeEpisodeContentRatingsV2:true` |
| `getAlbum` | `b9bfabef66ed756e5e13f68a942deb60bd4125ec1f1be8cc42769dc0259b4b10` (`:101`) | `LiveSessionHost.cs:1002` (`FetchAlbumAsync`) | Desktop | `uri:<albumUri>`, `locale:<pf.Locale>`, `offset:0`, `limit:50` |
| `getTrack` | `612585ae06ba435ad26369870deaae23b5c8800a256cd8a57e08eddc25a37294` (`:135`) | `LiveSessionHost.cs:1027` (`ResolveNowPlayingTrackAsync`); `SpotifyAlbumEnrichmentService.cs:89` (`GetTrackContextAsync`) | WebPlayer (both) | `uri:<trackUri>` |
| `queryArtistOverview` | `7f86ff63e38c24973a2842b672abe44c910c1973978dc8a4a0cb648edef34527` (`:98`) | `SpotifyArtistStatsService.cs:31`; `SpotifyAlbumEnrichmentService.cs:68` (`GetRelatedArtistsAsync`) | Desktop (both) | `uri:<artistUri>`, `locale:<pf.Locale>`, `preReleaseV2:false` |
| `queryNpvArtist` | `047c9c225967d41a763949a4db3f0493e901c9f8689a6537408aabf9beffc177` (`:131`) | `SpotifyAlbumEnrichmentService.cs:41` (`GetNowPlayingInfoAsync`, `UseQueryAsync`) | WebPlayer | `artistUri`, `trackUri`, `contributorsLimit:10`, `contributorsOffset:0`, `enableRelatedVideos:true`, `enableRelatedAudioTracks:true` |
| `queryAlbumMerch` | `3ef44ed6f17be67299538fe77faffab4075aeaf9e1085f10fc835592266711b5` (`:129`) | `SpotifyAlbumEnrichmentService.cs:97` | Desktop | `uri:<albumUri>` |
| `similarAlbumsBasedOnThisTrack` | `1d1f93a737498adca2c892c73af87fc0b052afe4e1a33c989540c32413dfae17` (`:133`) | `SpotifyAlbumEnrichmentService.cs:105` | Desktop | `uri:<seedTrackUri>`, `limit:<limit, default 24>`, `albumsOnly:true` |
| `queryWhatsNewFeed` | `d889c8c936ab192af8ced595427f5ba2acdf63478fdc0a181c8d477f8322630e` (`:138`) | `SpotifyWhatsNewService.cs:64` (`UseQueryAsync`) | WebPlayer | `offset:0`, `limit:50`, `onlyUnPlayedItems:false`, `includedContentTypes:[]` (empty array), `includeEpisodeContentRatingsV2:true` |
| `feedBaselineLookup` | `a950fb7c4ecdcaf2aad2f3ca9ee9c3aa4b9c43c97e1d07d05148c4d355bea7fc` (`:144`) | `HomeBaselinePreviews.cs:66` | WebPlayer | `uris:[<playlist uris…>]` |
| `fetchExtractedColors` | `36e90fcaea00d47c695fce31874efeb2519b97d4cd0ee1abfb4f8dc9348596ea` (`:106`) | `PlaylistPaletteEnricher.cs:57` (ttl forced `TimeSpan.Zero`) | Desktop | `imageUris:[<single url>]` |

### Search ops

Shared `Vars` writer at `LiveSessionHost.cs:731–742`; all search calls are `Platform.WebPlayer`.

| Op | Hash | Called at | Variables |
|---|---|---|---|
| `searchTracks` | `59ee4a659c32e9ad894a71308207594a65ba67bb6b632b183abe97303a51fa55` (`:114`) | `LiveSessionHost.cs:781` → `:787` | `includePreReleases:false`, `includeAlbumPreReleases:true`, `numberOfTopResults:<limit>`, `searchTerm:<query>`, `offset`, `limit`, `includeAudiobooks:true`, `includeAuthors:true`, `includeEpisodeContentRatingsV2:true` |
| `searchAlbums` | `5e7d2724fbef31a25f714844bf1313ffc748ebd4bd199eaad50628a4f246a7ab` (`:116`) | `LiveSessionHost.cs:782` → `:787` | same `Vars` |
| `searchArtists` | `270905851ba5c7faca81cfe053c2dbd8ceb4f156a0e0ef4b385af75ab69ffd13` (`:118`) | `LiveSessionHost.cs:783` → `:787` | same `Vars` |
| `searchPlaylists` | `af1730623dc1248b75a61a18bad1f47f1fc7eff802fb0676683de88815c958d8` (`:120`) | `LiveSessionHost.cs:784` → `:787` | same `Vars` |
| `searchTopResultsList` | `63a93cc04f6d8dea84a85de315e43f396a76cb681500de9ac5ccf5fc618c84cb` (`:126`) | `LiveSessionHost.cs:772` (`VarsTop`, `:744`) | `query:<query>` (NOT `searchTerm`), `limit`, `offset`, `numberOfTopResults:<limit>`, `includeArtistHasConcertsField:false`, `includeAudiobooks:true`, `includeAuthors:true`, `includePreReleases:true`, `includeAlbumPreReleases:true`, `includeEpisodeContentRatingsV2:true`, `isPrefix:null`, `sectionFilters:["GENERIC","VIDEO_CONTENT"]` |
| `searchSuggestions` | `556f5a15b2fdd3a7113ffd377ad9805e38a3a27b8bb1ca7d6d76bad54aa8ee12` (`:123`) | `LiveSessionHost.cs:923` | `query:<query>`, `limit:30`, `numberOfTopResults:30`, `offset:0`, `includeAuthors:true`, `includeAlbumPreReleases:true`, `includeEpisodeContentRatingsV2:true` |

### Concert ops

All routed through `SpotifyConcertService.QueryAsync` → `Platform.WebPlayer` (`SpotifyConcertService.cs:136`). Variable writers are centralized in `ConcertPathfinderRequests.cs`.

| Op | Hash | Called at | Variables (writer) |
|---|---|---|---|
| `ArtistConcerts` | `ef53c43b865496b9890b7167eab1dc614a8949ef9451b3c41184ea888de8bd2b` | `SpotifyConcertService.cs:22` | `artistUri`, `geoHash` (null when empty), `includeNearby` |
| `ArtistConcertsPageLocation` | `320698465a352f0d0247ec8ed02471244106d4199820f99de4d0a785561c2b03` | `:77` | none (`null` writer), ttl 0 |
| `userLocation` | `079939378ca79b67c6d047be9152ea940d21f10bbfa2f5d4cf4d8320d87774c2` | `:70` | none, ttl 0 |
| `inferredUserLocation` | `5db4c507ea735d2a1f37bd1166eca2c1a0e3387bb875ebca5d6031b6eccceeba` | `:84` | none, ttl 0 |
| `concertConcepts` | `a409c1eb39b6345e7993d424d2408b65a6699bafc2b8a03217033e517cd76b72` | `:32` | `geohash` (lowercase h), `conceptUri` (nullable) |
| `concertFeed` | `9cae2dbee3f47904c60bab45256260b3ddb9844d5ef25038c17112619d14ce9a` | `:43` | `geoHash`, `geonameId`, `dateRange:{from,to}` as `yyyy-MM-dd` or null, `conceptUris:[…]`/null, `radiusInKm` number/null, `paginationKey` |
| `concertCount` | `29be9d486e073a49268e13ed9e2d2180187e669fcb7a19b98011aca7ab61b141` | `:52`, ttl 0 | `geonameId`, `radiusInKm`, `dateRange`, `conceptUris` — in that order, no `paginationKey`/`geoHash` |
| `concertLocationDetails` | `b13f195349f188fee25480ae889d782852d68663bf07743c654244454750d681` | `:116` | `geonameId`, `isAnonymous` |
| `searchConcertLocations` | `43ededefcba8b3f519fd0c2d6c025dfeec9f742cf47d04a3c3711d95b27deda3` | `:93`, ttl 0 | `query` |
| `concertLocationsByLatLon` | `8a059d072a17a1199feb21fe846271f1680eda87010c832852ced0c55c6c7c96` | `:105`, ttl 0 | `lat`, `lon` |
| `saveLocation` | `5502351e9f201ae29014ca55d3b24b755ba261a1a9eb35fb498cb4c7df419353` | `:126`, ttl 0 | `geonameId` |
| `concert` | `21afefc1c7f9e38cbf7c60d03f5c8b6e602b7a91e04f2c2e0aa7d1743052768e` | `:62` | `uri`, `authenticated` |

Notes:
- `ConcertCountHash` is the ONLY concert hash not asserted in `Wavee.Tests/ConcertCaptureContractTests.cs:18-28`.
- Caching TTLs: `PathfinderResource.TtlFor` (`PathfinderResource.cs:126-138`) — home 15m, getAlbum/getTrack 10m, similarAlbums/queryNpvArtist/queryArtistOverview 30m, queryAlbumMerch 1h, queryWhatsNewFeed 5m, anything starting `search` → 0 (no cache), everything else 10m.
- `FetchSearchAsync`/`FetchSuggestRichAsync` use the raw `PathfinderClient` (no cache); everything else goes through `PathfinderResource`.

## 2. ExtensionKinds requested

Transport: `Wavee\Backend\Metadata\ExtendedMetadataSource.cs` — `POST {spclientBase}/extended-metadata/v0/extended-metadata`, gzipped `BatchedEntityRequest` with `header{Country=ctx.Market, Catalogue=ctx.Catalogue, TaskId=16 random bytes}`, one `EntityRequest` per URI with kinds grouped under it (`:213-232`), optional `ExtensionQuery.Etag` when an etag is supplied (`:202-203`).

Kinds actually requested (number from `SpotifyLive/Protos/extension_kind.proto`):

| Kind | # | Requested at |
|---|---|---|
| `TrackV4` | 10 | `ExtendedMetadataSource.cs:254` / `MetadataService.cs:114` (EntityKind.Track mapping); `AudioPlaybackStack.cs:87`; `AudioFormatProbe.cs:158`; `SpotifyVideoService.cs:133`; `SpotifyVideoService.Resolve.cs:68`; `SpotifyVideoTraitProbe.cs:76` |
| `AlbumV4` | 9 | `ExtendedMetadataSource.cs:255`, `MetadataService.cs:115` |
| `ArtistV4` | 8 | `ExtendedMetadataSource.cs:256`, `MetadataService.cs:116` |
| `ShowV4` | 11 | `ExtendedMetadataSource.cs:257`, `MetadataService.cs:117` |
| `EpisodeV4` | 12 | `ExtendedMetadataSource.cs:258`, `MetadataService.cs:118`; `AudioPlaybackStack.cs:89` |
| `AudioFiles` | 5 | `AudioPlaybackStack.cs:88` (`fetchAudioFilesV5`) |
| `UserProfile` | 15 | `SpotifyUserProfileService.cs:76,82` |
| `OriginalVideo` | 85 | `SpotifyVideoTraitProbe.cs:73` (diagnostic probe only) |
| `VideoAssociations` | 99 | `SpotifyVideoService.cs:70,91,110,150,161,165,190,192,204,207`; `SpotifyVideoService.Resolve.cs:84`; `SpotifyVideoTraitProbe.cs:72,134` |
| `RecommendedPlaylists` | 151 | `SpotifyAlbumEnrichmentService.cs:133-134` |
| `ConsumptionExperienceTrait` | 182 | `SpotifyVideoService.cs:73,94,113,190,195,205,210`; `SpotifyVideoTraitProbe.cs:74` |
| `ListMetadataV2` | 205 | `SpotifyAlbumEnrichmentService.cs:150,160` |
| `PlaybackTrait` | 212 | `SpotifyVideoService.cs:134,148`; `SpotifyVideoTraitProbe.cs:75` |

`UnknownExtension` (0) is used only as a "skip" sentinel (`ExtendedMetadataSource.cs:86`, `ExtensionEtagCache.cs:233`, `MetadataService.cs:71`, `ApiDebugBodyBuilder.cs:43`). The diagnostics console (`Features/Diagnostics/ApiDebugProto.cs:248`) will `Enum.TryParse` ANY kind name typed by the user, so the debug surface is not limited to the list above; `ApiDebugProtoDecomposer.cs:144-148` only knows how to decode ArtistV4/TrackV4/AlbumV4/ShowV4/EpisodeV4.

## 3. spclient / REST endpoints called

Host resolution: `https://apresolve.spotify.com/?type=spclient` (`SpotifyLiveSpclient.cs:67`), `?type=dealer` (`LiveSessionHost.cs:120`, `SpotifyLibrarySync.cs:47`), `?type=accesspoint` (`SpotifyLiveLogin.cs:73`). `Channel.SpclientWg` hardcodes `https://spclient.wg.spotify.com` (`LiveDealerTransport.cs:66`); `Channel.Spclient` uses the resolved base.

**Metadata / catalog**
- `POST /extended-metadata/v0/extended-metadata` — `ExtendedMetadataSource.cs:32,249`
- `GET /artistplaycontext/v1/page/spotify/artist-top-tracks-extensions/{artistUri}` — `SpotifyArtistPopularTracksService.cs:129` (JSON)

**Collections (library)**
- `POST /collection/v2/delta` — `CollectionFetcher.cs:106`
- `POST /collection/v2/paging` (`limit:300`) — `CollectionFetcher.cs:114`
- `POST /collection/v2/write` — `Mutation.cs:77`

**Playlists**
- `GET /playlist/v2/{path}?decorate=revision,attributes,length,owner,capabilities,picture` — `PlaylistFetcher.cs:23,187`
- `GET /playlist/v2/{path}/diff?revision={enc}&handlesContent=&hint_revision={enc}` — `PlaylistFetcher.cs:82` (comma must be `%2C`)
- `POST /playlist/v2/{path}/changes` — `Mutation.cs:137`
- `POST /playlist/v2/playlist` (create) — `PlaylistMutationSource.cs:45`
- `POST /playlist/v2/playlist/{id}/register-image` (SpclientWg) — `PlaylistMutationSource.cs:182`
- `POST /playlist/v2/playlist/{id}/signals` — `PlaylistSignalsClient.cs:43`
- `GET /playlist/v2/user/{account}/rootlist?decorate=revision` — `RootlistOps.cs:59`
- `POST /playlist/v2/user/{account}/rootlist/changes` — `RootlistOps.cs:130`
- `GET|POST /playlist-permission/v1/playlist/{id}/permission/base` — `PlaylistPermissionClient.cs:63` (GET), `:103` (POST)
- `POST /playlist-permission/v1/playlist/{id}/permission-grant` body `{"permission":{"permissionLevel":"CONTRIBUTOR"},"ttlMs":604800000}` — `PlaylistMutationSource.cs:261`
- `POST /playlistextender/extendp/` (SpclientWg) — `PlaylistExtenderClient.cs:34`
- `POST https://image-upload.spotify.com/v4/playlist` (non-spclient, `image/jpeg` → `uploadToken`) — `PlaylistMutationSource.cs:162`

**Connect / playback control**
- `PUT /connect-state/v1/devices/{deviceId}` (gzipped PutState, `X-Spotify-Connection-Id`) — `LiveDealerTransport.cs:100`
- `POST /connect-state/v1/player/command/from/{ourDeviceId}/to/{targetDeviceId}` (gzipped, form-urlencoded content-type) — `PlaybackController.cs:62`
- `PUT /connect-state/v1/connect/volume/from/{ourDeviceId}/to/{targetDeviceId}` — `PlaybackController.cs:76`
- `POST /connect-state/v1/connect/transfer/from/{fromDeviceId}/to/{targetDeviceId}` — `PlaybackController.cs:83`
- `GET /melody/v1/time` → `{"timestamp":ms}` — `LiveConnect.cs:265`

**Context resolution / autoplay**
- `GET /context-resolve/v1/{escaped uri}[?…from spec.Url]` — `LiveContextResolver.cs:395` (`ResolvePath`), used at `:75`
- `POST /context-resolve/v1/autopodcast` (protobuf in, JSON out) — `LiveContextResolver.cs:145`
- `POST /context-resolve/v1/autoplay` (protobuf in, JSON out) — `LiveContextResolver.cs:172`
- `GET /radio-apollo/v3/tracks/spotify:station:track:{seedId}?salt=…&autoplay=true&count=50&isVideo=false&prev_tracks=…&pageNum=2&minimal=true` — `LiveContextResolver.cs:200`
- `GET /inspiredby-mix/v2/seed_to_playlist/{seedUri}?response-format=json` — `LiveContextResolver.cs:229`
- `GET /playlist/v2/list/popular-release-segments-main-roles/artist_{id}` (protobuf `SelectedListContent`) — `LiveContextResolver.cs:318`

**Audio / video / DRM**
- `GET /storage-resolve/files/audio/interactive/{fileIdHex}` — `LiveTrackResolver.cs:233`, `AudioFormatProbe.cs:85`
- `GET /manifests/v9/json/sources/{manifestId}/options/supports_drm` (Origin/Referer `https://xpui.app.spotify.com`) — `SpotifyVideoResolver.cs:57` (`ManifestRoute`), `AudioFormatProbe.cs:184`
- `POST /playready-license` (or the manifest's `license_server_endpoint`), SOAPAction `"http://schemas.microsoft.com/DRM/2007/03/protocols/AcquireLicense"` — `SpotifyLicenseRelay.cs:18-20`
- `GET https://heads-fa-tls13.spotifycdn.com/head/{fileIdHex}` — `HeadFileClient.cs:44`
- `https://p.scdn.co/mp3-preview/…` — `AudioFormatProbe.cs:135`

**Resume points / telemetry**
- `POST /herodotus/spotify.resumption.v1.ResumePointRevisionService/CreateResumePointRevision` — `HerodotusClient.cs:16`
- `POST /herodotus/…/BatchCreateResumePointRevisions` — `HerodotusClient.cs:17`
- `POST /herodotus/…/ListResumePointRevisions` — `HerodotusClient.cs:53`
- `POST /gabo-receiver-service/v3/events/` (SpclientWg, gzip protobuf) — `GaboBatcher.cs:115`

**Social / profile / notifications**
- `GET /user-profile-view/v3/profile/{username}?market=from_token` — `LiveSessionHost.cs:899`, `SpotifyUserProfileService.cs:115`
- `GET /presence-view/v2/init-friend-feed/{connectionId}` — `SpotifyFriendActivityService.cs:123`
- `GET /presence-view/v1/user/{userId}` — `SpotifyFriendActivityService.cs:161`
- `GET /gander/v2/GetNotifications?locale={lang}&limit=20` — `SpotifyNotificationsService.cs:78`
- `GET /color-lyrics/v2/track/{trackId}?format=json&vocalRemoval=false&market={market}` — `SpotifyNativeLyricsSource.cs:35`

**Auth (non-spclient)**
- `POST https://clienttoken.spotify.com/v1/clienttoken` — `ClientTokenClient.cs:19`
- `POST https://login5.spotify.com/v3/login` — `Login5Client.cs:17`
- `https://accounts.spotify.com/oauth2/device/authorize` + `/api/token` — `Auth.cs:97-98`
- `https://accounts.spotify.com/authorize` + `/api/token` (PKCE loopback) — `Auth.Loopback.cs:24-25`

**Dealer (WebSocket, hm:// idents)** — `hm://pusher/v1/connections/` (`Connect.cs:19`), `hm://connect-state/v1/` + `.../connect/volume` (`ConnectCommand.cs:176,178`), `hm://connect-state/v1/cluster` (`ClusterMapper.cs:113`), `hm://playlist/…`, `hm://collection/…` (`DealerRouter.cs:27-28,63,70`), `hm://presence2/user/` (`SpotifyFriendActivityService.cs:21`).

## 4. Explicitly stubbed / unsupported surfaces

- **Search facets `Audiobooks` and `Podcasts` are not wired** — `SearchFacet` declares 7 values (`Wavee.Core/Library/Library.cs:25`: `All, Tracks, Albums, Playlists, Audiobooks, Podcasts, Artists`) but the Pathfinder switch only maps 4; `LiveSessionHost.cs:785` throws `NotSupportedException($"Search facet '{facet}' is not wired to a Pathfinder operation yet.")`.
- **Local (non-Spotify) playlist mutations** — every branch in `Wavee/Backend/Playlists/PlaylistMutationSource.cs` throws `NotSupportedException`: row removal `:85`, reordering `:99`, metadata edit `:152`, cover set `:160` and `:193`, permissions `:200`, visibility `:212`, delete `:223`, invites `:251`.
- **`StubTransport`** (`Wavee/Backend/Transport.cs:48`) — the inert mutation transport used while logged out; writes queue in the durable outbox and replay on next login. Swapped in/out at `App/Services.cs:335,350,446`.
- **`NullAppUpdateService`** (`Wavee.Core/Notifications/AppUpdate.cs:30`) — permanently `AppUpdateState.None`, all actions inert; `App/Services.cs:133` calls it "permanent until a real updater ships".
- **`UnsupportedPlaybackPlayer`** (`Wavee.Core/Playback/UnsupportedPlayback.cs:13`).
- **`StubCrypto.Decrypt` / `StubAudioEngine`** (`Wavee/Backend/Playback.cs:256,268,275`) — passthrough decrypt + silent engine behind `IAudioEngine`. (The real path is the separate `SpotifyLive/Audio` stack; this seam is still the declared stub.)
- **`FluentMediaAudioHost.cs:134`** — `NotSupportedException("SpotifyEngineAudioDecoder requires a SpotifyMediaByteSource.")`.
- **`RangedHttpSource.cs:589`** — files > 2 GB unsupported.
- Fakes used when no live session: `Wavee.Core/Fakes/FakeData.cs`, `FakeSource.cs`, `FakeSpotifySession.cs`; `Wavee.Core/Sources/FakePodcastSource.cs`.

No "browse" page surface exists in the code — the only `Browse*` identifiers are the concerts hub navigation tile (`Features/Concerts/ConcertDetailPage.cs:317-331`) and library search-vs-browse mode naming (`Features/Library/LibraryPage.cs:42`).