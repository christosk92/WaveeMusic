# Spotify SAZ → Wavee API gap analysis

Capture: `spotify.saz` (2026-07-28). Filtered to **Spotify process only** (1018 sessions; Wavee 128 excluded).

Merged from: SAZ inventory + [Map Wavee API coverage](34533911-0bf8-49cb-9da5-651530e84489) + [Deep-dive GraphQL ops](05104d4e-643c-4eca-a592-6beea7757af0) + [Deep-dive rare REST APIs](49a10839-badf-4b1d-b693-3dfdb475de2e).

Artifacts: `tmp/saz-analysis/` · Canvas: `spotify-saz-api-gap.canvas.tsx`

## P0 — update Desktop Pathfinder hashes

| Op | Wavee hash | Capture hash |
|---|---|---|
| `home` | `9052ac65…ad3e7896` | `5366cbf1f73f8c813dd0f1addc6934950f0dd529cec907107c85851e645c2d16` |
| `queryArtistOverview` | `7f86ff63…edef34527` | `ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a` |

Both called with `Platform.Desktop`. Variable shapes already match; also consider `preReleaseV2: true` on artist overview (Wavee sends `false`).

`queryNpvArtist`: capture Desktop `b2cedf7e…` ≠ Wavee WebPlayer `047c9c22…`. Wavee intentionally uses WebPlayer — only change if that path 400s. After any hash bump, harvest `relatedVideos` / Watch Feed / `homeChips`.

## P1 — missing GraphQL

| Op | Hash | Why |
|---|---|---|
| `showItemsPlayedState` | `4a070b9bfab2…` | Podcast episode progress |
| `queryNpvEpisode` | `b1cb5ba81403…` | Episode NPV (description, AI dubs, transcripts) |
| `browsePage` / `browseSection` | `f5c4e6d6…` / `b13c1ccc…` | Browse all (UI placeholder today) |
| `getCommentsForEntity` | `bba34fe5…` | Episode comments |
| `userTopContent` | `49ee1570…` | Profile affinity tops |
| `lookupChildEntities` | `91ce02e3…` | Batch visualIdentityTrait covers |

Skip: `episodeSponsoredContent` (ads).

Also from Wavee inventory: live search only wires Tracks/Albums/Artists/Playlists/Top — **Podcasts/Audiobooks facets throw `NotSupportedException`**.

## P1 — missing REST (podcasts + library polish)

- `GET /transcript-read-along/v2/episode/{id}?format=json&maxSentenceLength=500&excludeCC=true`
- `GET /clip-transcript/v1/transcripts/{uri}?offsets.start=&offsets.end=`
- `GET /playlist/v2/list/podcast-chapters/{episodeUri}` (protobuf)
- `GET /content-filter/v1/liked-songs?subjective=true` — Liked Songs filters
- `GET /popcount/v2/playlist/{id}/count` — follower badge

## P2 — finish partial surfaces

| Gap | Notes |
|---|---|
| Gander mark-read | Have `GetNotifications`; missing `GetUserHasUnreadNotification` + `POST ResetLatestCursor` (local last-seen only today) |
| Herodotus `ListCurrentStates` | Have create/batch/list revisions; missing bulk “what’s new since T” probe |
| Social-connect Jam | `sessions/current`, `jam_status` — connect-state only stubs `jam=off` |
| Profile followers/following | Have profile card; missing `…/followers` + `…/following` |
| `ratings/v1/rating/show/{uri}` | Podcast show stars |
| `recently-played/v3` | Optional; home GraphQL embed covers UI recents |

## Already covered

`getAlbum`, `queryAlbumMerch`, `fetchExtractedColors`, `feedBaselineLookup`, collection v2, playlistextender, popular-release-segments, gander GetNotifications, herodotus write/list revisions, user-profile-view (self), connect-state, presence, color-lyrics, home composer (skips Shorts), ~28 Pathfinder ops including concerts/search.

## Ignore

quicksilver, library-import, ads/capping/hpto, CDN images, gabo telemetry volume, net-fortune, clientsettings preferred-locale, user-customization (until home personalization is a goal), offline/v1 (until downloads ship).
