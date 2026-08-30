# Wavee ↔ Spotify wire diff (exact) — pass 2

**Supersedes nothing.** Companion to `GAP-ANALYSIS.md` (which is the *feature* inventory). This file is the
**byte-level request/response diff** plus the **needs-more-capture** ledger.

**Captures folded in this pass** (all under `%USERPROFILE%\Documents\Fiddler2\Captures`):

| Capture | Sessions | Pathfinder ops | What it exercises |
|---|---|---|---|
| `spotify.saz` (pass 1, raw still in `%TEMP%\spotify-saz\raw`) | 1147 (1018 Spotify) | 15 | broad browse: home, artist, album, podcast NPV, notifications, profile |
| `artist_more.saz` | 72 | 3 | artist page → **Show all / discography paging** |
| `content-filters.saz` | 646 | 4 | Liked Songs + content-filter chips |
| `pre-release.saz` | 21 | **0** | pre-release album **save** flow (pure spclient) |

Decoder note: pass-1's REST dumps were **wrong** — it gunzipped without de-chunking, so
`transcript-read-along`, `clip-transcript`, `gander`, `ratings`, `recently-played` all showed as
`gzip-fail` binary. Re-decoded correctly here (`scratchpad/rest2/`). Those bodies below are real.

---

## 1. Transport-header diff (applies to EVERY Pathfinder call)

| Header | Spotify 1.2.94.583 | Wavee (`PathfinderHeadersMiddleware`) | Verdict |
|---|---|---|---|
| `app-platform` | `Win32_ARM64` | `Win32_x86_64` | benign (both desktop); see Q4 |
| `spotify-app-version` | `1.2.94.583` **or** `896000000` (per-op!) | **not sent at all** | see §1.1 |
| `user-agent` | `… Spotify/1.2.94.583 …` | `… Spotify/1.2.88.483 …` | stale, cosmetic |
| `origin` / `referer` | `https://xpui.app.spotify.com/` | not sent | not required (server is CORS-permissive) |
| `accept-language` | `en` | `_language` | ✅ |
| `client-token` | present | present | ✅ |

### 1.1 The per-op `spotify-app-version` split (new finding)

Two *different* values ride the same socket, same `app-platform`:

```
home                  spotify-app-version: 1.2.94.583
queryNpvArtist        spotify-app-version: 1.2.94.583
queryNpvEpisode       spotify-app-version: 1.2.94.583
showItemsPlayedState  spotify-app-version: 1.2.94.583
browsePage/Section    spotify-app-version: 1.2.94.583
getCommentsForEntity  spotify-app-version: 1.2.94.583
userTopContent        spotify-app-version: 1.2.94.583
queryAlbumMerch       spotify-app-version: 1.2.94.583
lookupChildEntities   spotify-app-version: 1.2.94.583
queryArtistOverview   spotify-app-version: 896000000     ← web-player-style
getAlbum              spotify-app-version: 896000000     ← web-player-style
```

`896000000` is the **web-player** version token. So the desktop client itself serves the artist and album
pages from a *web-player bundle* while home/NPV/browse come from the desktop bundle. That is almost
certainly **why the artist/album persisted hashes drift on a different cadence than home's**, and it is a
strong hint that Wavee's current split (`Desktop` for overview, `WebPlayer` for NPV) is backwards.

**DECIDED (Q1): mirror the capture per-op.** That means `PathfinderHeadersMiddleware` must stop deriving
identity from a 2-valued enum and start carrying an explicit `spotify-app-version` per platform, and four
call sites flip:

| Op | Wavee today | Target |
|---|---|---|
| `queryArtistOverview` | `Platform.Desktop` | web-player identity (`896000000`) |
| `getAlbum` | `Platform.Desktop` | web-player identity (`896000000`) |
| `queryNpvArtist` | `Platform.WebPlayer` | desktop identity (`1.2.94.583`) + hash `b2cedf7e…` |
| `getTrack` | `Platform.WebPlayer` | **unknown — not in any capture** (see §6) |

⚠️ Wavee sends **no** `spotify-app-version` on Pathfinder at all today, and it works. So "mirror the
capture" adds a header that is currently absent — which is a behaviour change on *every* Pathfinder call,
not just the four. Recommend landing it as: (1) hashes + `preReleaseV2` + `timeZone` first, verify green,
(2) platform/identity flip as a separate change so a regression is attributable.

---

## 2. Pathfinder request diffs — exact

Format: capture body verbatim → Wavee body as built by `PathfinderClient.BuildBody` → the edit.

### 2.1 `home` — hash stale + `timeZone` hardcoded ❗P0

```jsonc
// CAPTURE (spotify.saz sid 1088, content-filters.saz — identical in both)
{"variables":{"homeEndUserIntegration":"INTEGRATION_DESKTOP","timeZone":"Europe/Amsterdam",
  "sp_t":"","facet":"","sectionItemsLimit":10,"includeEpisodeContentRatingsV2":true},
 "operationName":"home",
 "extensions":{"persistedQuery":{"version":1,
   "sha256Hash":"5366cbf1f73f8c813dd0f1addc6934950f0dd529cec907107c85851e645c2d16"}}}
```

```jsonc
// WAVEE (LiveSessionHost.FetchHomeAsync:941)
{"variables":{"homeEndUserIntegration":"INTEGRATION_DESKTOP","timeZone":"Etc/UTC",
  "sp_t":"","facet":"","sectionItemsLimit":10,"includeEpisodeContentRatingsV2":true},
 "operationName":"home",
 "extensions":{"persistedQuery":{"version":1,
   "sha256Hash":"9052ac65ff42aefe6d39c45c184d9144cf8dbcc233ea1a76f8649264ad3e7896"}}}
```

Variable **shape** is byte-identical. Two value diffs:

```diff
  // src/apps/Wavee/SpotifyLive/PathfinderClient.cs — PathfinderOps
- public const string HomeHash = "9052ac65ff42aefe6d39c45c184d9144cf8dbcc233ea1a76f8649264ad3e7896";
+ public const string HomeHash = "5366cbf1f73f8c813dd0f1addc6934950f0dd529cec907107c85851e645c2d16";
```

```diff
  // src/apps/Wavee/SpotifyLive/LiveSessionHost.cs — FetchHomeAsync
- w.WriteString("timeZone", "Etc/UTC");
+ w.WriteString("timeZone", TimeZoneInfo.Local.Id);   // IANA id; see Q2
```

`timeZone` is what drives *"Soundtrack your Tuesday afternoon"* and the greeting. `Etc/UTC` is not wrong,
it is just **someone else's afternoon**. **DECIDED (Q2): send the real local zone.**

Windows `TimeZoneInfo.Local.Id` yields a *Windows* id (`W. Europe Standard Time`), and Spotify wants IANA
(`Europe/Amsterdam`). The conversion must be explicit, with a fallback:

```csharp
static string LocalIanaZone()
    => TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "Etc/UTC";
```

On NativeAOT this reads ICU data — worth confirming it doesn't trip `InvariantGlobalization` (Wavee
already ships localized strings, so ICU is presumably in, but verify rather than assume).

### 2.2 `queryArtistOverview` — hash stale + `preReleaseV2:false` ❗P0

```jsonc
// CAPTURE (artist_more.saz sid 06 — vaultboy; spotify.saz — IU. Identical shape.)
{"variables":{"uri":"spotify:artist:0K87f3owemzI8NUCoEIXOB","locale":"","preReleaseV2":true},
 "operationName":"queryArtistOverview",
 "extensions":{"persistedQuery":{"version":1,
   "sha256Hash":"ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a"}}}
```

```jsonc
// WAVEE (SpotifyArtistStatsService:31 and SpotifyAlbumEnrichmentService:68 — two call sites, same body)
{"variables":{"uri":"spotify:artist:…","locale":"<pf.Locale>","preReleaseV2":false},
 "operationName":"queryArtistOverview",
 "extensions":{"persistedQuery":{"version":1,
   "sha256Hash":"7f86ff63e38c24973a2842b672abe44c910c1973978dc8a4a0cb648edef34527"}}}
```

```diff
  // PathfinderClient.cs
- public const string QueryArtistOverviewHash = "7f86ff63e38c24973a2842b672abe44c910c1973978dc8a4a0cb648edef34527";
+ public const string QueryArtistOverviewHash = "ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a";
```

```diff
  // SpotifyArtistStatsService.cs:31  AND  SpotifyAlbumEnrichmentService.cs:68  (both call sites)
- w => { w.WriteString("uri", artistUri); w.WriteString("locale", pf.Locale); w.WriteBoolean("preReleaseV2", false); },
+ w => { w.WriteString("uri", artistUri); w.WriteString("locale", pf.Locale); w.WriteBoolean("preReleaseV2", true); },
```

⚠️ `locale`: the capture sends **`""`** (empty), Wavee sends `pf.Locale`. Unverified whether a non-empty
locale changes the response under the new hash. **Q3.**

### 2.3 `queryNpvArtist` — variables byte-identical, hash + platform differ

```jsonc
// CAPTURE (Desktop bundle)
{"variables":{"artistUri":"spotify:artist:4k5fFEYgkWYrYvtOK3zVBl",
  "trackUri":"spotify:track:3jsYQw78lrxJA2ysnmOIf9",
  "contributorsLimit":10,"contributorsOffset":0,
  "enableRelatedVideos":true,"enableRelatedAudioTracks":true},
 "operationName":"queryNpvArtist",
 "extensions":{"persistedQuery":{"version":1,
   "sha256Hash":"b2cedf7ed0f29c713567d97ed69b848c8387294edfe58a0e439a3a5669cc27bb"}}}
```

Wavee (`SpotifyAlbumEnrichmentService:41`) sends **exactly these six variables**, but with
hash `047c9c22…` on `Platform.WebPlayer`. Both are presumably live. **Do not bump blind** — the WebPlayer
hash is currently working. Bump only if it starts 400ing, or if Q1 resolves toward "match the desktop
bundle everywhere". Recorded for completeness:

```diff
- public const string QueryNpvArtistHash = "047c9c225967d41a763949a4db3f0493e901c9f8689a6537408aabf9beffc177"; // WebPlayer
+ // Desktop-bundle equivalent seen in spotify.saz: b2cedf7ed0f29c713567d97ed69b848c8387294edfe58a0e439a3a5669cc27bb
```

### 2.4 Ops where capture == Wavee (no change) ✅

| Op | Capture variables | Wavee | Match |
|---|---|---|---|
| `getAlbum` | `{uri, locale:"", offset:0, limit:50}` | same (hash `b9bfabef…`) | ✅ hash + shape |
| `queryAlbumMerch` | `{uri}` | same (hash `3ef44ed6…`) | ✅ |
| `fetchExtractedColors` | `{imageUris:[…]}` | same (hash `36e90fca…`) | ✅ |
| `feedBaselineLookup` | `{uris:[…]}` | same (hash `a950fb7c…`) | ✅ |

All four hashes match the capture **byte for byte**. Nothing to do.

### 2.5 Ops Wavee does not implement — exact request bodies to copy

```jsonc
// showItemsPlayedState — podcast episode progress column
{"variables":{"uri":"spotify:show:7uhrWlDvxzy9hLoW0EYf0b","limit":100},
 "operationName":"showItemsPlayedState",
 "extensions":{"persistedQuery":{"version":1,"sha256Hash":"4a070b9bfab2e8537a5271e6839bc3ef51501dcac8170b5fb69a98f967c5fb60"}}}

// queryNpvEpisode — episode now-playing panel
{"variables":{"uri":"spotify:episode:5AUBkkFGq9GlIq4gF9T1oH","includeEpisodeContentRatingsV2":true},
 "operationName":"queryNpvEpisode",
 "extensions":{"persistedQuery":{"version":1,"sha256Hash":"b1cb5ba81403b81628bd4b73e4b15cabde1898a2ed63667b30bfd053f82a281f"}}}

// browsePage — "Browse all" category page
{"variables":{"pagePagination":{"offset":0,"limit":10},"sectionPagination":{"offset":0,"limit":10},
  "uri":"spotify:page:0JQ5IMCbQBLkCuGhI0Epb1","browseEndUserIntegration":"INTEGRATION_DESKTOP",
  "includeEpisodeContentRatingsV2":true},
 "operationName":"browsePage",
 "extensions":{"persistedQuery":{"version":1,"sha256Hash":"f5c4e6d668f5716464a231c1cc8b22c1cbf6ad68b09929fd7de813a30581298b"}}}

// browseSection — one section, paged
{"variables":{"pagination":{"offset":0,"limit":20},"uri":"spotify:section:0JQ5IMCbQBLkCuGhI0Epb1",
  "browseEndUserIntegration":"INTEGRATION_DESKTOP","includeEpisodeContentRatingsV2":true},
 "operationName":"browseSection",
 "extensions":{"persistedQuery":{"version":1,"sha256Hash":"b13c1cccbfcb6947753c2613411b3566485c21fd5f36d80a80bb64be61ba2d51"}}}

// lookupChildEntities — batch square cover art for N tracks
{"variables":{"uris":["spotify:track:75InM94w13mJcj0wCpyaTn","spotify:track:32r7xn09FoylHoSOatv4dy",
  "spotify:track:013AWvizllIUEC2FOBzOnh"]},
 "operationName":"lookupChildEntities",
 "extensions":{"persistedQuery":{"version":1,"sha256Hash":"91ce02e32b19123de231dc8de91fe4b9ab84eca087d4c015549308d77fbb6d10"}}}

// userTopContent — profile "Top artists this month"
{"variables":{"includeTopArtists":true,
  "topArtistsInput":{"offset":0,"limit":10,"sortBy":"AFFINITY","timeRange":"SHORT_TERM"},
  "includeTopTracks":false,
  "topTracksInput":{"offset":0,"limit":4,"sortBy":"AFFINITY","timeRange":"SHORT_TERM"}},
 "operationName":"userTopContent",
 "extensions":{"persistedQuery":{"version":1,"sha256Hash":"49ee15704de4a7fdeac65a02db20604aa11e46f02e809c55d9a89f6db9754356"}}}

// getCommentsForEntity — episode comments (token=null on first page)
{"variables":{"uri":"spotify:episode:5AUBkkFGq9GlIq4gF9T1oH","token":null},
 "operationName":"getCommentsForEntity",
 "extensions":{"persistedQuery":{"version":1,"sha256Hash":"bba34fe5f2da3aaa25ab5c90eef1fe2036d325bf32e791ae462b637665185d83"}}}
```

`episodeSponsoredContent` (`a5c1fe72…`) is an **ad** op — its variables carry the whole gabo playback
context blob. Recommend permanent skip.

---

## 3. Pathfinder **response** diffs (what lands vs what Wavee reads)

### 3.1 `queryArtistOverview` under the NEW hash — 6 unmapped top-level fields

Verified against the artist_more capture (`vaultboy`) and the spotify.saz capture (`IU`). Top-level keys
returned by hash `ae0e2958…`:

```
__typename discography goods headerImage id onPlatformReputationTrait preReleaseV2 profile
relatedContent relatedMusicVideos saved sharingInfo stats unmappedMusicVideosV2 uri
visualIdentity visuals watchFeedEntrypoint
```

`SpotifyExportMapper.MapArtist` (line 1077) reads: `uri/id`, `profile.name`, `visuals.avatarImage`,
`headerImage.data.sources`, `onPlatformReputationTrait.verification`, `profile.biography`,
`discography.{albums,compilations,singles,topTracks,latest,popularReleasesAlbums}`,
`relatedContent.{appearsOn,relatedArtists}`, `profile.pinnedItem`, `goods.{concerts,merch}`,
`profile.playlistsV2`, `stats.{monthlyListeners,followers,worldRank,topCities}`,
`profile.externalLinks`, `visuals.gallery`, `visualIdentity.wideFullBleedImage`.

**Unmapped and live in the capture:**

| Field | Capture value (vaultboy / IU) | What it is | Wavee today |
|---|---|---|---|
| `relatedMusicVideos` | `{items:[], totalCount:0, pagingInfo:{nextOffset:null}}` | artist music-video shelf | `ArtistExtras.MusicVideos: null` hardcoded (line 1116) |
| `unmappedMusicVideosV2` | `{items:[], totalCount:0}` | videos with no track mapping | — |
| `watchFeedEntrypoint` | **populated on BOTH artists** — `entrypointUri: spotify:watch-feed:artist:{id}?itemId={base64 track uri}`, `thumbnailImage`, `video:{fileId, videoType, startTime, endTime}` | the artist-page video canvas entry | — |
| `preReleaseV2` | `{data:{Album, coverArt, name, preReleaseEndDateTime, type, uri}}` on vaultboy; **`null`** on IU | upcoming release card | — (and Wavee sends `preReleaseV2:false` so it never even asks) |
| `relatedContent.discoveredOnV2` | `totalCount: 100` (20 items) | "Discovered on" playlists | — |
| `relatedContent.featuringV2` | `totalCount: 2` / `8` | "Featuring {artist}" playlists | — |
| `saved` | `true` / `false` | is the artist followed | Wavee derives follow state from collection |
| `sharingInfo` | `{shareId, shareUrl}` | share sheet | — |

❗ **`relatedMusicVideos.totalCount == 0` on both captured artists.** Given the open SAZ
video-detection campaign, this is the single field worth a targeted recapture — see §6.

### 3.2 `home` — `greeting` and `homeChips` unmapped

`SpotifyHomeComposer.Compose` switches on `HomeGenericSectionData` / `HomeFeedBaselineSectionData` /
`HomeRecentlyPlayedSectionData` and skips `HomeShortsSectionData` (line 80). The capture's
`data.home` has **four** keys, not one:

```jsonc
"greeting": {"transformedLabel":"Good afternoon","translatedBaseText":"Good afternoon"},
"homeChips": [
  {"id":"music-chip",    "label":{"transformedLabel":"Music"},
   "subChips":[{"id":"music-following-chip","label":{"transformedLabel":"Following"}}],
   "highlightColor":null,"highlightScheme":null},
  {"id":"podcasts-chip", "label":{"transformedLabel":"Podcasts"},
   "subChips":[{"id":"podcasts-following-chip","label":{"transformedLabel":"Following"}}]},
  {"id":"audiobooks-chip","label":{"transformedLabel":"Audiobooks"},"subChips":[]}
],
"sectionContainer": {...},   // ← the only one Wavee reads
"__typename": "Home"
```

`homeChips[].id` is exactly what goes back into the `facet` request variable (Wavee sends `facet:""`).
So the whole Music/Podcasts/Audiobooks chip row on Spotify's home is **one already-wired variable away**.
`highlightColor` / `highlightScheme` are `null` in this capture (Q7).

Section census of the captured home (31 sections):
`HomeShortsSectionData ×1` (skipped), `HomeGenericSectionData ×9`, `HomeRecentlyPlayedSectionData ×1`,
`HomeFeedBaselineSectionData ×20`. One baseline section is **"Videos you might like"** carrying an
`Episode` content type — Wavee's baseline path assumes `Playlist`. Worth checking it doesn't drop.

### 3.3 Response shapes for the unimplemented ops (field-exact, from the capture)

```
queryNpvEpisode → data.episodeUnionV2
  .id .uri .name .type .htmlDescription
  .coverArt.extractedColors.colorDark.hex
  .transcripts.items[].{uri, language, cdnUrl, readAlongUrlV2, isStatic}   ← feeds §4.1
  .podcastV2.data.{__typename, name, uri, htmlDescription, publisher.name, accessInfo: NULL}
  .aiDubbedEpisodes.items[]  = []        ← EMPTY in capture
  .gatedEntityRelations[]    = []        ← EMPTY in capture
  .originalEpisode           = null      ← NULL in capture

showItemsPlayedState → data.lookup[].data.episodesV2.items[].entity.data
  .playedState.{playPositionMilliseconds:int, state:str}

lookupChildEntities → data.lookupEntities[]
  .uri .visualIdentityTrait.squareCoverImage.image.data.sources[].{url,maxWidth,maxHeight,imageFormat}
  .visualIdentityTrait.squareCoverImage.originalInstances[].{size, flatFile.cdnUrl}

userTopContent → data.me.profile.topArtists
  .totalCount .items[].data.{uri, profile.name, visuals.avatarImage.sources[].{url,width,height}}

getCommentsForEntity → data.comments[]
  .entityUri .totalCount .nextPageToken .eligibilityStatus
  .items[].{uri, commentString, isPinned, isSensitive, isPendingReview,
            hasUserReachedReplyLimit, numberOfRepliesWithThreads,
            createDate.{isoString,precision}, author,
            reactionsMetadata.{numberOfReactions, usersReactionUnicode},
            replies:[], topRepliesAuthors:[], coverImagesReacted:[], coverImagesReplied:[]}  ← 4 EMPTY

browsePage → data.browse
  .uri .header.title.transformedLabel
  .header.{backgroundImage:NULL, color:NULL, subtitle:NULL}                ← 3 NULL
  .sections.{totalCount, pagingInfo.nextOffset:NULL}                       ← NULL (single page)
  .sections.items[].{uri, targetLocation, data.__typename,
                     data.title:NULL, data.subtitle:NULL,                  ← NULL on browsePage
                     sectionItems.totalCount,
                     sectionItems.items[].content.data.{uri,name,mediaType,publisher.name,
                                                       coverArt.sources[]}}

browseSection → data.browseSection
  .data.title.transformedLabel   ← non-null here (unlike browsePage)
  .data.subtitle : NULL
  .sectionItems.pagingInfo.nextOffset : int   ← real paging cursor
  (item shape identical to browsePage)
```

---

## 4. REST / spclient diffs — real decoded bodies

### 4.1 `transcript-read-along` ✅ decoded (394 KB)

```
GET https://spclient.wg.spotify.com/transcript-read-along/v2/episode/{base62}
      ?format=json&maxSentenceLength=500&excludeCC=true
```

```jsonc
{ "version":"1.0", "transcriptUri":"spotify:transcript:7HUiQXT1PKF5GpNy7WN1px",
  "publishedAt":"2026-07-28T12:45:13.922488958Z", "language":"en-us",
  "section":[
    {"startMs":160,"title":{}},
    {"startMs":160,"text":{"sentence":{
        "startMs":160,
        "text":"Since January, the United States government has sold roughly $13 billion …",
        "highlight":[{"startMs":160,"numChars":5},{"startMs":680,"numChars":1}, …]}}},
    …
  ]}
```

Per-word karaoke via `highlight[].{startMs,numChars}` — i.e. **the same shape class as color-lyrics**,
so Wavee's lyrics renderer is the natural host. Note `title:{}` (empty object, not null) on section heads.

### 4.2 `clip-transcript` ✅ decoded

```
GET https://spclient.wg.spotify.com/clip-transcript/v1/transcripts/{uriEncodedEpisodeUri}
      ?offsets.start=28.000s&offsets.end=89.000s
```

```jsonc
{"words":[{"word":"This,","offsets":{"start":"28.600s","end":"29s"},"speakerId":"1"}, …]}
```

Durations are **strings with an `s` suffix**, and are irregular (`"29s"`, `"29.520s"`) — a naive
`double.Parse` on the raw string will throw. Needs a trim-`s` parse helper.

### 4.3 `gander` — the two missing halves ✅ decoded

```
GET  /gander/v2/GetNotifications?locale=&limit=20        ← Wavee HAS this
GET  /gander/v2/GetUserHasUnreadNotification?postFix=a    ← MISSING
POST /gander/v2/ResetLatestCursor                         ← MISSING
```

```jsonc
// GetUserHasUnreadNotification
{"userHasUnreadNotification": false}

// ResetLatestCursor  — request
{"cursor":"1784192949362"}                 // ms epoch of the newest notification seen
// ResetLatestCursor  — response
{"success": true}

// GetNotifications item shape (Wavee already parses this)
{"id":"2d871069-…","createdTimestamp":"2026-07-16T09:09:09.362Z",
 "title":"New Tinie Tempah show just announced near you. Save the date!",
 "action":{"uri":"spotify:concert:4mkgW2DbO6pMb0A8i9NC0s","type":"NAVIGATE"},
 "entityImage":{"imageUrl":"https://i.scdn.co/image/…"},
 "isNew":false,
 "storageId":"9223370252661826445#2d871069-…",
 "messagingMetadata":{"opportunityId":"2244d000-…","messageId":"186533"}}
```

Wavee keeps a **local** last-seen stamp; the server-side cursor means the badge desyncs across devices.
`cursor` is the ms-epoch string, not the `storageId`.

### 4.4 `ratings` ✅ decoded

```
POST /ratings/v1/rating/show/{showUri}?market=from_token     body: {"rating":5}
 200 {"show_uri":"spotify:show:7uhr…","rating":5,"rated_at":"2026-07-28T12:45:01.434485250Z"}
```

Note the response uses **snake_case** while every other JSON surface here is camelCase.

### 4.5 `social-connect` (Jam) — captured, and it's a 404

```
GET /social-connect/v2/sessions/current?alt=protobuf        → 404, 0 bytes  (no active Jam)
GET /social-connect/v2/devices/{deviceId}/jam_status        → 200 JSON:
{"device_broadcast_status":{
   "timestamp":"1785242588476","broadcast_status":"BROADCAST_UNAVAILABLE",
   "device_id":"5ba893a8b2b24f378a9b7bd7a24fe7e6",
   "link_token":{"token":"1dYVMenGrtvZc2V9amJMkw"},
   "device_type":"COMPUTER","device_name":"Wavee"}}
```

⚠️ `device_name: "Wavee"` — this is the **Spotify client reading Wavee's own broadcast row**. So Wavee
already publishes a jam-capable device entry; only the read side is missing. `sessions/current` 404 is the
**no-session** state, so the populated Jam shape is **NOT captured** (§6).

### 4.6 Protobuf endpoints — now decoded field-by-field (Q9 answered: protos drafted, §10)

| Endpoint | Decoded | Status |
|---|---|---|
| `GET /popcount/v2/playlist/{id}/count` | `#1=0 #2=1 #7=128345311 #8=1`; a private playlist returns only `#1=0 #2=1 #7=0` | ⚠️ see caveat below |
| `GET /playlist/v2/list/podcast-chapters/{episodeUri}` | **standard playlist list format** — `#3 {#1 "Chapters"}` is the attributes message | ✅ reuse `playlist4_external` |
| `GET /user-profile-view/v3/profile/{id}/following` | `{uri, name, imageUrl, followerCount, isFollowing}` per artist | ✅ drafted |
| `GET /user-profile-view/v3/profile/{id}/followers` | `{uri, name, ?, ?, followerCount, colorToken}` per user | ✅ drafted |
| `GET /recently-played/v3/recently-played?limit=50&filter=default,collection-new-episodes` | `{contextUri, timestampMs, lastTrackUri}` triples | ✅ drafted |
| `POST /herodotus/…/ListResumePointRevisions` | req = `uri="spotify:list:play-history:v1"`, `limit=500` | ✅ Wavee has this |

⚠️ **popcount caveat:** field 7 reads **128,345,311** for `37i9dQZF1EYkqdzj48dyYq`. That is far larger than
any real playlist follower count (Today's Top Hits is ~35 M), so field 7 is probably **not** a follower
count — or the field is not a plain varint count. A second private playlist returns `0`, so it *scales*
with something. **Do not ship a "followers" badge off this until it's cross-checked against a playlist
whose follower count you can read in the Spotify UI.**

The recents payload is the only surface carrying **`spotify:station:album:{id}`** and
`spotify:user:{id}:collection` as contexts — the inline home recents don't have those.

### 4.7 `content-filter/v1/liked-songs` — **304 in every capture** ⛔

```
OPTIONS …/content-filter/v1/liked-songs?subjective=true&market=from_token → 200 (preflight)
GET     …/content-filter/v1/liked-songs?subjective=true&market=from_token → 304, 0 bytes
   If-None-Match: 2026-07-28T00:54:51.829985865Z#-1547928215#1137299681#3241
```

Both `spotify.saz` and `content-filters.saz` only ever caught the **conditional** request. The 200 body is
**not in these SAZs**. It *is* in the separate `%TEMP%\wavee-saz-content-filters\report\final-analysis.md`
run, which `docs/plans/wavee/liked-songs-content-filters-plan.md` §2.1 already locks as:

```jsonc
{"contentFilters":[{"title":"Mellow","query":"tags contains mellow"}]}
```

Note the **ETag format**: `{isoTimestamp}#{int}#{int}#{int}` — four `#`-joined fields, not an opaque hash.
Any cache layer must round-trip it verbatim.

---

## 5. Extended-metadata (protobuf) kind diff — new in this pass

Decoded the `BatchedEntityRequest` protobufs directly (`scratchpad/xmreq.py`). Kinds Spotify asks for:

**`artist_more.saz`** (artist page → Show all):

| Count | Entity | Kind | Wavee asks? |
|---|---|---|---|
| 563 | `spotify:track` | `TRACK_V4` (10) | ✅ |
| 6 | `spotify:track` | `IDENTITY_TRAIT` (178) | ❌ |
| 5 | `spotify:track` | `GATED_ENTITY_RELATIONS` (164) | ❌ |
| 5 | `spotify:track` | `ON_PLATFORM_REPUTATION_TRAIT` (185) | ❌ |
| 5 | `spotify:track` | `PODCAST_SUBSCRIPTIONS` (22) | ❌ |
| 5 | `spotify:track` | `VISUAL_IDENTITY_TRAIT` (179) | ❌ |
| 2 | `spotify:track` | `CONSUMPTION_EXPERIENCE_TRAIT` (182) | ✅ |
| 1 | `spotify:track` | `PLAYBACK_TRAIT` (212) | ✅ |
| 1 | `spotify:track` | **`?249`** | ❌ **unknown — not in our `extension_kind.proto` (max 215)** |
| 1 | `spotify:artist` | `ARTIST_V4` (8) | ✅ |
| 1 | `spotify:album` | `PRERELEASE` (138) | ❌ |
| 1 | `spotify:list` | `IDENTITY_TRAIT` (178) | ❌ |

**`pre-release.saz`** (save a pre-release album — **zero GraphQL**, all spclient):

| Count | Entity | Kind | Wavee asks? |
|---|---|---|---|
| 216 | `spotify:playlist` | **`?225`** | ❌ **unknown — not in our proto** |
| 17 | `spotify:user` | `IDENTITY_TRAIT` (178) | ❌ |
| 14 | `spotify:artist` | `VISUAL_IDENTITY_TRAIT` (179) | ❌ |
| 11 | `spotify:track` | `IDENTITY_TRAIT` / `TRACK_V4` | partial |
| 6 | `spotify:show` | `IDENTITY_TRAIT` / `PUBLISHING_METADATA_TRAIT` (183) / `VISUAL_IDENTITY_TRAIT` | ❌ |
| 3 | `spotify:show` | `SHOW_V4` (11) | ✅ |
| 4 | `spotify:album` | `VISUAL_IDENTITY_TRAIT` | ❌ |
| 1 | `spotify:album` | `ALBUM_V4` (9) / `IDENTITY_TRAIT` / `PUBLISHING_METADATA_TRAIT` | partial |
| 1 | **`spotify:prerelease:{id}`** | `PRERELEASE` (138) | ❌ |

### 5.1 Unknown kinds — RESOLVED by `type_url` (Q8 answered: inferred from payload)

The `Any.type_url` on a **200** response names the message outright. Scanning every extended-metadata
response in all four captures resolves four of the five unknowns without needing a newer proto:

```diff
  // src/apps/Wavee/SpotifyLive/Protos/extension_kind.proto  — after USER_PROFILE_V2 = 215;
+ ENTITY_TYPE_TRAIT           = 220;   // spotify.contentagnostic.v2.EntityTypeTrait
+ AUDIO_ATTRIBUTES_V2         = 222;   // spotify.playlistmixing.extensions.audio_attributes.v2.AudioAttributes
+ CONTENT_CAPABILITY_TRAIT    = 239;   // spotify.contentagnostic.v2.ContentCapabilityTrait
+ CURATION_EXPERIENCE_TRAIT   = 246;   // spotify.contentagnostic.v2.CurationExperienceTrait
+ CONTENT_EXPERIENCE_TRAIT    = 249;   // spotify.contentagnostic.v2.ContentExperienceTrait
```

Names are **derived from the type_url, not from an official enum** — the enum constant Spotify uses could
differ in spelling. The *number → message* binding is solid; the identifier is my naming.

**Kind 225 remains unresolved.** It is **304 in every single occurrence** across all four captures
(27 entities in `spotify.saz`, 216 in `pre-release.saz`, always against `spotify:playlist:`), so no
`type_url` was ever transmitted. What we do know from its 304 headers:

```
status_code = 304
etag        = "310e0edd47db6f72"     ← 16 hex chars, identical across all 27 playlists in one batch
cache_ttl   = 600 s
offline_ttl = 2592000 s (30 d)
```

A **single etag shared by 27 different playlists** means the payload is not per-playlist content — it's
more like a config/capability blob keyed by user or client. To name it, capture with a cold cache (§11).

### 5.2 Full kind census — every extension seen, with its message type

From `spotify.saz` responses (count = entities returned for that kind):

| n | Kind | Name | `type_url` message | Wavee |
|---|---|---|---|---|
| 3509 | 10 | `TRACK_V4` | `spotify.metadata.Track` | ✅ |
| 957 | 12 | `EPISODE_V4` | `spotify.metadata.Episode` | ✅ |
| 518 | 182 | `CONSUMPTION_EXPERIENCE_TRAIT` | `contentagnostic.v2.ConsumptionExperienceTrait` | ✅ |
| 336 | 85 | `ORIGINAL_VIDEO` | `bumblebee.originalvideo.v1.OriginalVideo` | ✅ |
| 317 | 99 | `VIDEO_ASSOCIATIONS` | `bumblebee.video_associations.v1.VideoAssociations` | ✅ |
| 273 | 178 | `IDENTITY_TRAIT` | `contentagnostic.v2.IdentityTrait` | ❌ |
| 201 | 98 | `AUDIO_ASSOCIATIONS` | *(304-only)* | ❌ |
| 187 | 239 | **`CONTENT_CAPABILITY_TRAIT`** | `contentagnostic.v2.ContentCapabilityTrait` | ❌ |
| 117 | 179 | `VISUAL_IDENTITY_TRAIT` | `contentagnostic.v2.VisualIdentityTrait` | ❌ |
| 96 | 16 | `CANVAS_V1` | `canvaz.cache.EntityCanvazResponse.Canvaz` | ❌ |
| 90 | 164 | `GATED_ENTITY_RELATIONS` | `gatedentityrelations.v1.GatedEntityRelations` | ❌ |
| 84 | 9 | `ALBUM_V4` | `spotify.metadata.Album` | ✅ |
| 56 | 249 | **`CONTENT_EXPERIENCE_TRAIT`** | `contentagnostic.v2.ContentExperienceTrait` | ❌ |
| 52/50 | 31/30 | `SHOW_ACCESS` / `EPISODE_ACCESS` | *(304-only)* | ❌ |
| 50 | 222 | **`AUDIO_ATTRIBUTES_V2`** | `playlistmixing.extensions.audio_attributes.v2.AudioAttributes` | ❌ |
| 50 | 21 | `EPISODE_TRANSCRIPTS` | `corex.transcripts.metadata.EpisodeTranscript` | ❌ |
| 50 | 54 | `HTML_DESCRIPTION` | `podcast.extensions.PodcastHtmlDescription` | ❌ |
| 50 | 80 | `SHARE_TRAIT` | `traits.v1.ShareTrait` | ❌ |
| 48 | 58 | `CONTENT_WARNING` | *(304-only)* | ❌ |
| 40 | 185 | `ON_PLATFORM_REPUTATION_TRAIT` | `contentagnostic.v2.OnPlatformReputationTrait` | ❌ |
| 35 | 220 | **`ENTITY_TYPE_TRAIT`** | `contentagnostic.v2.EntityTypeTrait` | ❌ |
| 29 | 183 | `PUBLISHING_METADATA_TRAIT` | `contentagnostic.v2.PublishingMetadataTrait` | ❌ |
| 27 | 225 | **UNKNOWN** | *(304-only, everywhere)* | ❌ |
| 22 | 212 | `PLAYBACK_TRAIT` | `contentagnostic.v2.PlaybackTrait` | ✅ |
| 21 | 149 | `ROOTLISTABILITY_TRAIT` | `traits.v1.RootlistabilityTrait` | ❌ |
| 12 | 246 | **`CURATION_EXPERIENCE_TRAIT`** | `contentagnostic.v2.CurationExperienceTrait` | ❌ |
| 7 | 4 | `PODCAST_SEGMENTS` | `podcast_segments.PodcastSegments` | ❌ |
| 4 | 86 | `SMART_SHUFFLE` | `smartshuffle.SmartShuffle` | ❌ |
| 3 | 8 | `ARTIST_V4` | `spotify.metadata.Artist` | ✅ |
| 3 | 114 | `WATCH_FEED_ENTITY_EXPLORER` | *(304-only)* | ❌ |
| 3 | 37 | `PODCAST_RATING` | `ratings.PodcastRating` | ❌ |
| 2 | 138 | `PRERELEASE` | `prerelease.extension.Prerelease` | ❌ |
| 1 | 6 | `TRACK_DESCRIPTOR` | `descriptorextension.ExtensionDescriptorData` | ❌ (plan pending) |
| 1 | 20 | `PODCAST_AD_SEGMENTS` | `ads.formats.PodcastAds` | skip |
| 1 | 29 | `PODCAST_POLL` | `polls.PodcastPoll` | ❌ |
| 1 | 108 | `PODCAST_SPONSORED_CONTENT` | `sponsoredcontentlistener.v1.…Payload` | skip |
| 1 | 113 | `COMPANION_CONTENT` | `figs.companion_content.v0.CompanionContent` | ❌ |

### 5.3 Decoded payload shapes (field-exact, from the wire)

```proto
// kind 138 PRERELEASE — spotify.prerelease.extension.Prerelease  (422 B, artist_more sid 67)
//   entity_uri was BOTH spotify:album:0qi1ztU… and spotify:prerelease:0iqKCC… in different batches
message Prerelease {
  optional string prerelease_uri  = 1;  // "spotify:prerelease:0iqKCCqFwlqzSnJgV22Nmh"
  optional Timestamp release_at   = 2;  // { int64 seconds = 1 }  → 1788472800
  optional Release  release       = 3;
}
message Release {
  optional string album_uri = 1;        // "spotify:album:0qi1ztU4S08zA1FsP1DUaY"
  optional string type      = 2;        // "ALBUM"
  optional string name      = 3;        // "ARE YOU EVER COMING BACK?"
  optional ArtistRef artist = 4;        // { uri = 1, name = 2 }
  repeated Image  images    = 5;        // { url = 1, size = 2 ("DEFAULT"|"SMALL"|"LARGE"), w = 3, h = 4 }
}
```

```proto
// kind 178 IDENTITY_TRAIT — contentagnostic.v2.IdentityTrait  (140 B)
message IdentityTrait {
  optional string kind      = 1;        // "Song"
  optional string name      = 2;        // "Letting Go"
  optional Ref    album     = 4;        // { name = 1, uri = 2 }
  repeated Ref    artists   = 5;        // { name = 1, uri = 2 }
}
```

```proto
// kind 183 PUBLISHING_METADATA_TRAIT  (58 B)
message PublishingMetadataTrait {
  optional Date  date          = 1;     // nested { year=1(2026) month=2(9) day=3(4) } under field 3
  optional Timestamp published = 2;     // 1788472800
  optional Timestamp available = 3;     // 1788472800
  repeated string copyright    = 4;     // "© 2026 broke", "℗ 2026 broke"
}
```

```proto
// kind 6 TRACK_DESCRIPTOR — descriptorextension.ExtensionDescriptorData  (the Liked-Songs chip tags)
message ExtensionDescriptorData { repeated Descriptor descriptors = 1; }
message Descriptor {
  optional string text        = 1;      // "k-pop"        ← the token the chip query matches
  optional float  weight      = 2;      // 0x0c93793f ≈ 0.9744
  optional bytes  types       = 3;      // 01 07 09 0a 0b   (packed enum list)
  optional string concept_uri = 4;      // "spotify:concept:0JOcC1ypWJCg2qQqoEpYL9"
  optional string display     = 5;      // "K-Pop"
}
```

This is a **direct confirmation of `liked-songs-content-filters-plan.md` §2.2** — and it adds two fields
the plan doesn't mention: `concept_uri` (a navigable `spotify:concept:` entity) and `display` (the
properly-cased label, so chips don't have to title-case `text` themselves).

```proto
// kind 249 CONTENT_EXPERIENCE_TRAIT (2 B) and kind 149 ROOTLISTABILITY_TRAIT (2 B)
// both are a single varint enum, value 1 in every sample. Semantics unknown without more polarity.
```

### 5.4 Two structural findings

1. **`spotify:prerelease:{id}` is a first-class URI scheme.** The pre-release save flow resolves it via
   XM kind 138 and then writes with `POST /collection/v2/write`. Wavee has no `prerelease:` handling —
   consistent with the [[playable-uri-not-always-spotify-track]] rule (URIs are opaque), but the
   pre-release *card* can't be built without kind 138. Note the same payload is served under **both**
   an `spotify:album:` and an `spotify:prerelease:` entity_uri, so either key works.
2. **`podcast-chapters` is not a new format.** `GET /playlist/v2/list/podcast-chapters/{episodeUri}`
   returns a **playlist list** (`#3 { #1 "Chapters" }` = the standard attributes message) — i.e. it can be
   parsed with Wavee's existing `playlist4_external.proto` / list-metadata path, not a new parser. It does
   require two extra request headers: `x-accept-list-items: audio-track, audio-episode, video-episode,
   audiobook` and `spotify-playlist-sync-reason: CAwQAQ==`.

Wavee's own kind usage today (`grep ExtensionKind.`): `TrackV4, AlbumV4, ArtistV4, ShowV4, EpisodeV4,
VideoAssociations, ConsumptionExperienceTrait, PlaybackTrait, UserProfile, RecommendedPlaylists,
OriginalVideo, ListMetadataV2, AudioFiles`.

Also: spclient `Spotify-App-Version` is `129400583` in the capture; Wavee's
`SpotifyRuntimeIdentity.DefaultAppVersion = "129300667"`. Cosmetic, but it's the value the pre-release and
chapters endpoints echo.

---

## 6. ⛔ NULL / EMPTY / needs-more-capture ledger

Ranked by how much it blocks going e2e.

| # | What | Why it's blocked | How to capture it |
|---|---|---|---|
| 1 | **`relatedMusicVideos` / `unmappedMusicVideosV2` item shape** | `totalCount: 0` on **both** captured artists (vaultboy, IU) — the arrays are empty, so the item schema is unknown | Open an artist page for someone with music videos on Spotify desktop (e.g. Taylor Swift, The Weeknd, BLACKPINK) and capture `queryArtistOverview` |
| 2 | **`content-filter/v1/liked-songs` 200 body** | 304 in both SAZs | Clear the client cache / restart Spotify, then open Liked Songs so the request goes out **without** `If-None-Match` |
| 3 | **Jam / `social-connect/v2/sessions/current` populated body** | 404 (no active session) in capture | Start a Jam from the phone, then capture the desktop |
| 4 | **`aiDubbedEpisodes`, `gatedEntityRelations`, `originalEpisode`** (queryNpvEpisode) | `[]`, `[]`, `null` | Open an AI-dubbed episode (Spotify marks them "AI dubbed") |
| 5 | **`getCommentsForEntity` replies / reactions** | `replies:[]`, `topRepliesAuthors:[]`, `coverImagesReacted:[]`, `coverImagesReplied:[]` all empty; only top-level comments captured | Open an episode with a threaded/reacted comment |
| 6 | **`queryAlbumMerch` items** | `{items:[], totalCount:0}` — merch shape unknown | Open an album from an artist with merch (US/UK majors) |
| 7 | **`episodeSponsoredContent`** | `{containsSponsoredContent:false}` | probably never needed — recommend skip |
| 8 | ~~XM kinds 225 and 249~~ | **RESOLVED for 249/220/222/239/246** via `Any.type_url` (§5.1). **225 still unknown** — 304 in all 243 occurrences, one etag shared across 27 playlists | cold-cache capture (§11 step 1) |
| 9 | ~~missing protos~~ | **RESOLVED** — drafted in §10; `podcast-chapters` needs none (reuses the playlist list format). **`popcount` field 7 semantics unverified** (§4.6) | calibrate popcount against a UI-readable follower count |
| 10 | **`browsePage` header fields** | `backgroundImage`, `color`, `subtitle` all `null`; `sections.pagingInfo.nextOffset` null | capture a *genre* page (e.g. `spotify:page` for Pop/Rock), which does carry a colored header |
| 11 | **`homeChips[].highlightColor` / `highlightScheme`** | both `null` | capture a home with a promoted/highlighted chip (rare; may be market-gated) |
| 12 | **`preReleaseV2` on an artist without an upcoming release** | `null` on IU, populated on vaultboy | already have both polarities ✅ |
| 13 | **`podcastV2.data.accessInfo`** | `null` | capture a **paid/subscriber** podcast episode |

Additionally, the following are **not exercised at all** in any of the four SAZs, so nothing can be said
about drift: `searchTracks/Albums/Artists/Playlists/Suggestions/TopResults`, `queryWhatsNewFeed`,
`getTrack`, `similarAlbumsBasedOnThisTrack`, and every concert op. Their hashes in `PathfinderOps` are
**unverified against 1.2.94.583** — a search-and-concerts capture would close that.

---

## 7. Suggested order of work

1. **P0, 2 lines:** bump `HomeHash` + `QueryArtistOverviewHash`, flip `preReleaseV2` to `true`
   (2 call sites), fix `timeZone`. Purely mechanical; the variable shapes already match.
2. **P0 verification:** re-run and confirm no `pathfinder … -> HTTP 400 (stale persisted-query hash)` in
   `wavee.log`. `PathfinderClient` already logs exactly this string on 400.
3. **P1 harvest (free, same call):** map `watchFeedEntrypoint`, `relatedContent.discoveredOnV2`,
   `featuringV2`, `sharingInfo`, `saved`, `preReleaseV2` off the artist overview.
4. **P1 home:** `greeting` + `homeChips` → `facet` round-trip.
5. **P1 podcasts:** `queryNpvEpisode` + `showItemsPlayedState` + `transcript-read-along`.
6. **P2:** gander unread + ResetLatestCursor (small, fixes a real desync), `browsePage`/`browseSection`,
   `userTopContent`, `getCommentsForEntity`.
7. **Blocked on recapture:** everything in §6.

---

## 8. Questions — answered / still open

**ANSWERED this pass:**

- **Q1 → mirror the capture per-op.** Folded into §1.1. Caveat recorded there: Wavee currently sends *no*
  `spotify-app-version` on Pathfinder and works, so this is additive; land it separately from the hash bump.
- **Q2 → send the real local zone.** Folded into §2.1 with the Windows→IANA conversion.
- **Q8 → infer from payload shape.** Done, §5.1 — four of five unknown kinds resolved by `Any.type_url`.
  **Kind 225 is still unknown and cannot be resolved from these captures** (304 in all 243 occurrences).
- **Q9 → hand-write the protos.** Done, §10.
- **Q5/Q10 → yes to all four next-pass items.** Capture recipe in §11; podcast NPV depth in §12.

**STILL OPEN:**

**Q3 — `locale` on `queryArtistOverview` / `getAlbum`.** Capture sends `""`, Wavee sends `pf.Locale`.
Keep Wavee's (localized bios/labels) or match the capture byte-for-byte? "Mirror the capture" as decided
in Q1 was about *platform identity* — I don't want to silently extend it to dropping localization.
**My recommendation: keep `pf.Locale`.** Confirm?

**Q4 — `app-platform`.** Capture is `Win32_ARM64`; Wavee hardcodes `Win32_x86_64` while shipping ARM64
builds. Derive from `RuntimeInformation.ProcessArchitecture`, or leave it pinned?

**Q6 — pre-release.** `pre-release.saz` proves the save flow is entirely spclient (XM 138 +
`collection/v2/write`). Is "save an upcoming album" a feature you want, or was that capture only to prove
`preReleaseV2` lights up on the artist page?

**Q7 — `homeChips`.** Music / Podcasts / Audiobooks chip row on home: want it? It's cheap (the `facet`
variable is already in the request) but it changes the home layout.

**Q11 (new) — kind 225.** It's requested 216× against playlists in a single session and is always a 304
with **one etag shared across 27 different playlists** — that smells like a per-user config blob, not
per-playlist data. Worth chasing, or park it?

**Q12 (new) — popcount.** Field 7 reads 128 M for an editorial playlist, which can't be a follower count.
Do you have a playlist whose follower number you can read off the Spotify UI to calibrate against?

**Q13 (new) — `getTrack` platform.** Wavee calls it on `Platform.WebPlayer`, but `getTrack` appears in
**none** of the four captures, so "mirror the capture" has nothing to mirror. Leave as-is?

**Q14 (new) — proto placement.** §10 drafts five messages. Do they go in
`src/apps/Wavee/SpotifyLive/Protos/` as new `.proto` files (matching the existing convention), or would
you rather hand-roll readers and skip the codegen for these five small shapes?

---

## 10. Drafted `.proto` definitions (Q9)

Hand-written from the captured bytes. Field numbers and wire types are **observed**; names are mine.
Unobserved fields are omitted rather than guessed — these are minimal readers, not full schemas.

```proto
// recently_played.proto — GET /recently-played/v3/recently-played?limit=50&filter=…
syntax = "proto2";
package wavee.recentlyplayed;

message RecentlyPlayed {
  repeated RecentContext contexts = 1;
}
message RecentContext {
  optional string context_uri   = 1;  // spotify:playlist: | spotify:album: | spotify:station:album: |
                                      // spotify:user:{id}:collection
  optional int64  played_at_ms  = 2;  // 1785242559196
  optional string last_item_uri = 3;  // spotify:track:…
}
```

```proto
// user_profile_social.proto — GET /user-profile-view/v3/profile/{id}/{following|followers}
syntax = "proto2";
package wavee.userprofileview;

message ProfileList {
  repeated ProfileEntry entries = 1;
}
message ProfileEntry {
  optional string uri            = 1;  // spotify:artist:… (following) | spotify:user:… (followers)
  optional string name           = 2;  // "Imagine Dragons" | "christosk92"
  optional string image_url      = 3;  // artists only; absent on user entries in the capture
  optional uint32 field4         = 4;  // artists: follower count (60330995 for Imagine Dragons)
                                       // users:   1  ← NOT the same semantic; see note
  optional uint32 field6         = 6;  // users only, = 1
  optional uint32 follower_count = 11; // users only, = 1697420
  optional uint32 is_following   = 7;  // artists only, = 1
  optional string color_token    = 13; // users only, "BjlSj9OW9c"
}
```

⚠️ **`followers` and `following` do NOT share a schema.** On artist entries the follower count is field 4;
on user entries field 4 is `1` and the count is field **11**. Only one user entry was captured, so the
user shape rests on a single sample — treat field 6 / 11 / 13 as provisional.

```proto
// popcount.proto — GET /popcount/v2/playlist/{id}/count
syntax = "proto2";
package wavee.popcount;

message PlaylistCount {
  optional uint32 field1 = 1;  // 0 in both samples
  optional uint32 field2 = 2;  // 1 in both samples
  optional uint64 count  = 7;  // 128345311 (editorial) / 0 (private) — SEMANTIC UNVERIFIED, see §4.6
  optional uint32 field8 = 8;  // 1 when count > 0, absent when count == 0
}
```

```proto
// prerelease.proto — XM kind 138, spotify.prerelease.extension.Prerelease
syntax = "proto2";
package wavee.prerelease;

message Prerelease {
  optional string    prerelease_uri = 1;
  optional Timestamp release_at     = 2;
  optional Release   release        = 3;
}
message Timestamp { optional int64 seconds = 1; }
message Release {
  optional string    album_uri = 1;
  optional string    type      = 2;   // "ALBUM"
  optional string    name      = 3;
  optional ArtistRef artist    = 4;
  repeated Image     images    = 5;
}
message ArtistRef { optional string uri = 1; optional string name = 2; }
message Image {
  optional string url  = 1;
  optional string size = 2;   // "DEFAULT" | "SMALL" | "LARGE"
  optional uint32 width  = 3;
  optional uint32 height = 4;
}
```

```proto
// identity_trait.proto — XM kind 178, contentagnostic.v2.IdentityTrait
syntax = "proto2";
package wavee.contentagnostic;

message IdentityTrait {
  optional string kind    = 1;   // "Song"
  optional string name    = 2;
  optional Ref    album   = 4;
  repeated Ref    artists = 5;
}
message Ref { optional string name = 1; optional string uri = 2; }
```

Podcast chapters needs **no new proto** — see §5.4.2.

---

## 11. Capture recipe for the next Fiddler session

One session closes ledger items 1, 2, 6 and every unverified hash. Order matters — the cache-cold steps
must come first, before the client warms up.

**Before starting Fiddler**

1. Fully quit Spotify (tray icon → Quit, not just close the window).
2. Delete the HTTP cache so conditional requests can't 304:
   `%LOCALAPPDATA%\Spotify\Data` and `%LOCALAPPDATA%\Spotify\Storage`.
   *(This is what makes items 2 and 5.1/kind-225 catchable at all — everything below is wasted if the
   client answers from cache.)*
3. Start Fiddler with HTTPS decrypt on, then launch Spotify. Let the login settle ~10 s.

**The clicks, in order**

| # | Action | Catches |
|---|---|---|
| 1 | Open **Liked Songs** immediately (first navigation after a cold start) | `content-filter/v1/liked-songs` **200** + XM kind 6 `TRACK_DESCRIPTOR` batch + possibly kind 225 with a payload |
| 2 | Click each filter chip once | whether chips re-query or filter locally |
| 3 | Open an artist **with music videos** — Taylor Swift, The Weeknd, BLACKPINK, Doja Cat | `queryArtistOverview` with **non-empty `relatedMusicVideos` / `unmappedMusicVideosV2`** ⟵ item 1 |
| 4 | On that artist, click **Show all → Discography** | discography paging path |
| 5 | Open an album by a **US/UK major** artist (merch-carrying) | `queryAlbumMerch` with non-empty `items` ⟵ item 6 |
| 6 | Type a query in **search**, press Enter, click through **all** result tabs incl. Podcasts + Audiobooks | `searchTracks/Albums/Artists/Playlists/Suggestions/TopResults` + the two podcast/audiobook ops Wavee doesn't have |
| 7 | Open **Concerts** (artist page → Concerts, and the standalone Live Events tab) | all 12 concert ops |
| 8 | Open **Browse all**, then one genre page | `browsePage` with a **coloured header** ⟵ item 10 |
| 9 | Open a **podcast episode** → play → open transcript → open chapters → open comments | `queryNpvEpisode`, `showItemsPlayedState`, transcript, chapters, comments-with-replies ⟵ items 4, 5 |
| 10 | Open **What's New** | `queryWhatsNewFeed` |
| 11 | *(optional)* Start a Jam from your phone, then reopen Spotify desktop | `social-connect/v2/sessions/current` **200** ⟵ item 3 |

Save as **one** `.saz`. I'll re-run `sazdump.py` / `xmscan.py` over it and produce pass 3.

If step 1's 200 doesn't land, the cache wasn't cleared — check for `If-None-Match` on the
`content-filter` request; if the header is present, the delete didn't take.

---

## 12. Podcast NPV stack — the full missing surface

Everything below is captured and decoded; nothing here is blocked.

**Wire order the real client uses when you open an episode:**

```
1. queryNpvEpisode        {uri, includeEpisodeContentRatingsV2:true}   → hash b1cb5ba8…
2. XM kind 12 EPISODE_V4  + kind 54 HTML_DESCRIPTION + kind 21 EPISODE_TRANSCRIPTS
3. GET /playlist/v2/list/podcast-chapters/{episodeUri}                 (playlist list format)
4. GET /transcript-read-along/v2/episode/{id}?format=json&maxSentenceLength=500&excludeCC=true
5. getCommentsForEntity   {uri, token:null}                            → hash bba34fe5…
6. showItemsPlayedState   {uri:<show>, limit:100}                      → hash 4a070b9b…  (on the show page)
7. POST /ratings/v1/rating/show/{showUri}?market=from_token  {"rating":N}
```

Step 1's `transcripts.items[].readAlongUrlV2` is what step 4's URL is built from — so 4 is not an
independent guess, it's a follow-link. `cdnUrl` is the static (non-timed) variant.

Step 6 is the *only* source of `playedState.{playPositionMilliseconds, state}` for a show's episode list —
herodotus resume points cover playback position for the *current* item, not the whole list.

The three pieces Wavee would need that don't exist yet: an episode NPV mapper, a chapters reader (reuses
the playlist parser), and a read-along renderer. The read-along shape
(`section[].text.sentence.{startMs, text, highlight[]}`) is close enough to color-lyrics that the existing
lyrics view is the natural host rather than a new control.

---

## 13. Superseded question list (pass 1, kept for traceability)

**Q1 — the Desktop/WebPlayer split.** The capture shows the real client serving `queryArtistOverview` and
`getAlbum` with `spotify-app-version: 896000000` (web-player) while everything else uses `1.2.94.583`.
Wavee currently uses `Platform.Desktop` for overview/album and `Platform.WebPlayer` for NPV/getTrack —
roughly inverted. Do you want me to (a) leave the platform routing alone and only bump hashes, (b) mirror
the capture exactly per-op, or (c) capture a Wavee run side-by-side first to see which combination 400s?

**Q2 — `timeZone`.** Should Wavee send the real local IANA zone
(`TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id)`), or is a fixed zone deliberate for
deterministic home output in tests? If real: is there an existing clock/locale seam I should route through
instead of touching `TimeZoneInfo` directly in `LiveSessionHost`?

**Q3 — `locale`.** Capture sends `locale: ""` on both `queryArtistOverview` and `getAlbum`; Wavee sends
`pf.Locale`. Keep Wavee's (localized bios/labels) or match the capture exactly? I'd keep Wavee's unless the
new hash rejects it — but that's a guess, not evidence.

**Q4 — `app-platform`.** Capture is `Win32_ARM64` (their box is ARM). Wavee sends `Win32_x86_64`. Should
this become architecture-derived (`RuntimeInformation.ProcessArchitecture`), given Wavee ships ARM64?

**Q5 — scope.** Is this pass supposed to also cover **search** (`searchTracks` etc.), **concerts**, and
`queryWhatsNewFeed`? None of them appear in any of the four SAZs, so their hashes are unverified against
1.2.94.583. If you care, I need a capture where you type in the search box and open the Concerts tab.

**Q6 — pre-release.** `pre-release.saz` shows the flow is *entirely* spclient: XM kind 138 `PRERELEASE` on
a `spotify:prerelease:{id}` URI + `POST /collection/v2/write`. Is "save an upcoming album" actually a
feature you want, or was that capture just to prove `preReleaseV2` on the artist page?

**Q7 — `homeChips`.** Do you want the Music/Podcasts/Audiobooks chip row on Wavee's home? It's cheap (the
`facet` variable is already in the request), but it changes the home page layout.

**Q8 — unknown XM kinds 225 / 249.** Do you have a newer `extension_kind.proto` anywhere (a dump, a
decompile, another repo)? 225 is hit 216× against playlists in one session, so naming it matters. If not,
I can try to infer from the response payload shape — but that's slower and less certain.

**Q9 — protobuf-only endpoints.** `popcount`, `podcast-chapters`, `user-profile-view followers/following`,
and `recently-played/v3` all return protobuf we have no `.proto` for. Want me to hand-write minimal
message definitions from the captured bytes (doable — the shapes are simple and I've already read them
field-by-field above), or is that out of scope for an analysis pass?

**Q10 — recapture batch.** If you're willing to do one more Fiddler session, the highest-value single
capture is: **an artist page for someone with music videos** + **Liked Songs with a cold cache** +
**search + concerts**. That closes ledger items 1, 2, and the entire "unverified hashes" gap in one go.
Want me to write out the exact click-by-click steps?

---

## 14. Artifacts

- `tmp/saz-analysis/graphql-decoded/*.resp.json` — pass-1 GraphQL responses (correct)
- `tmp/saz-analysis/graphql-samples/*.req.txt` — pass-1 raw requests **incl. headers** (auth not redacted here)
- scratchpad `artist_more/`, `content_filters/`, `prerelease/` — this pass's per-SAZ dumps
- scratchpad `rest2/` — **correctly de-chunked** REST bodies (supersedes `tmp/saz-analysis/endpoint-decoded/`)
- scratchpad `sazdump.py`, `xmreq.py`, `redecode.py` — the tooling; `xmreq.py` maps XM kind ints → names
  straight from `src/apps/Wavee/SpotifyLive/Protos/extension_kind.proto`

⚠️ `graphql-samples/*.req.txt` contain **live bearer + client-token** headers. They're under `tmp/` — confirm
that's gitignored before any commit.
