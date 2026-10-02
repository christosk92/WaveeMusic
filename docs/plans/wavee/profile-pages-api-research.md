# Profile pages — API research (capture of the official client, 2026-10-01)

Status: RESEARCH (input for the profile-pages design; no code yet). Source: a Fiddler capture of the official desktop
client 1.2.96.518 (`Win32_ARM64`, market NL) opening the own profile, a second account's profile (then following it),
and a stranger's profile found through user search (then its Following and Followers lists, then one of its playlists).
894 sessions over 57.5 s, decoded and analysed in six parallel slices. User ids, names and credentials are deliberately
absent from this document: users are **U-own** (the signed-in account), **U1** (a second account, no avatar) and **U2**
(a stranger with 118 followers / 1063 following / 94 public playlists).

---

## 0. The answer in one screen

A profile page is **one REST call plus two lists**, with Pathfinder only for follow state, follow actions, the own
account's top artists and the header colour. Extended metadata and the playlist service are NOT needed to draw it.

```
open spotify:user:<id>
 ├─ GET  spclient.wg.spotify.com/user-profile-view/v3/profile/<id>?playlist_limit=10&artist_limit=10&episode_limit=10&market=from_token
 │        → header (name, avatar, colour, counts, is_following, is_current_user) + ≤10 public playlists + ≤10 recently played artists
 ├─ GET  …/profile/<id>/followers?market=from_token   → the WHOLE list, unpaged
 ├─ GET  …/profile/<id>/following?market=from_token   → the WHOLE list (artists first, then users), unpaged
 ├─ own profile only:   Pathfinder userTopContent      → "Top artists this month" (10, AFFINITY, SHORT_TERM)
 ├─ other users only:   Pathfinder isFollowingUsers    → follow state (the profile's is_following already carries it)
 └─ after the header:   Pathfinder fetchExtractedColors(avatar 300 px)  → header background (skipped when no avatar)
Follow / Unfollow:      Pathfinder followUsers / unfollowUsers {usernames:[<bare id>]} → then re-GET the profile
```

The official client fires the three REST GETs **in parallel** on mount (header data ~0.4–0.6 s after the click, lists
~0.8 s). Everything else it does on a profile (per-card `playlist-permission/base` + playlist GET/diff + track metadata,
a deferred extended-metadata batch ~1.7 s later) is **prefetch for clicking into a playlist**, not page content.

---

## 1. The page as the official client builds it

| Section (gabo shelf id, order) | Own profile | Other user | Source |
|---|---|---|---|
| Header: avatar (or initial on the brand colour), name, "N followers · N following · N public playlists", tint | ✓ | ✓ | REST profile + `fetchExtractedColors` |
| Action row: **Edit** (own) / **Follow ↔ Following** (other) | Edit | Follow | REST `is_current_user` / `is_following`; Pathfinder follow ops |
| `top-artists` (0) "Top artists this month — Only visible to you" | ✓ | – | Pathfinder `userTopContent` |
| Top tracks this month | flag-gated, off in this capture | – | `userTopContent.topTracks` |
| `playlists` (1) Public playlists (≤10, "Show all" when more) | ✓ | ✓ | REST profile field 8 / total field 9 |
| `recently-played-artists` (2) | when not hidden | when not hidden | REST profile field 7 (absent = owner's privacy switch) |
| `followers` (3) | ✓ | ✓ (hidden when `show_follows` is off) | REST `/followers` |
| `following` (4) | ✓ | ✓ (same) | REST `/following` |

Sub-pages "Show all" for followers / following re-use the same unpaged endpoints (telemetry pages `profile/followers`,
`profile/following`; the profile page itself reports `page_id=profile`, user search `search/profiles`). Back navigation
re-mounts and refetches the three REST calls (no client cache on them). A Follow click → `followUsers` (~290 ms) →
the profile is re-fetched ~0.4 s later (followers_count +1, `is_following` set); the lists are not refetched.

---

## 2. `user-profile-view/v3` — the page's data

Host `spclient.wg.spotify.com` (not `gew4-spclient`). Send `Accept: application/x-protobuf` → body
`application/vnd.spotify.user-profile-view` (protobuf; gzip). No ETag, `cache-control: private, max-age=0`. The OPTIONS
preflights seen are web-layer CORS artefacts. Every response in the capture parses with the schema below with zero
unknown fields and round-trips byte-exactly.

```proto
syntax = "proto3";
// GET /user-profile-view/v3/profile/{id}?playlist_limit=10&artist_limit=10&episode_limit=10&market=from_token
message UserProfile {
  string  uri                          = 1;   // spotify:user:<id>
  string  name                         = 2;
  string  image_url                    = 3;   // empty when the user has no image (then: colour + initial)
  int32   followers_count              = 4;
  int32   following_count              = 5;   // artists + users together
  bool    is_following                 = 6;   // the VIEWER follows this user
  repeated RecentlyPlayedArtist recently_played_artists = 7;   // absent = hidden by the owner
  repeated PublicPlaylist       public_playlists        = 8;   // ≤ playlist_limit
  int32   total_public_playlists_count = 9;
  bool    is_current_user              = 10;  // set only on the viewer's own profile
  bool    has_spotify_name             = 14;  // guess
  bool    has_spotify_image            = 15;  // absent when no image
  int32   color                        = 16;  // 0xRRGGBB — one of six brand avatar colours
  bool    allow_follows                = 23;  // guess (23/24 may be swapped)
  bool    show_follows                 = 24;  // guess
  string  public_id                    = 35;  // opaque 10-char id, stable across profile and list entries
}
message RecentlyPlayedArtist { string uri = 1; string name = 2; string image_url = 3; int32 followers_count = 4;
                               bool owner_follows = 5; }   // the PROFILE OWNER follows the artist (viewer-independent)
message PublicPlaylist { string uri = 1; string name = 2; string image_url = 3; int32 followers_count = 4;  // absent = 0
                         string owner_name = 5; string owner_uri = 6; bool is_following = 7; }   // viewer-relative

// GET …/profile/{id}/followers?market=from_token   (users only)
// GET …/profile/{id}/following?market=from_token   (all artists first, then all users; each group by name, case-insensitive)
message ProfileList { repeated ProfileEntry entries = 1; }   // NO paging, total or cursor — the whole list (1063 entries / 131 KB seen)
message ProfileEntry {
  string uri = 1; string name = 2; string image_url = 3;      // image may be a short-lived signed Facebook CDN URL
  int32  followers_count = 4;                                 // absent = 0
  bool   is_following_user = 6;                               // users: viewer follows (single sample — unproven)
  bool   is_following = 7;                                    // artists: viewer follows
  bool   flag8 = 8;                                           // artists, 2 of 897; meaning unknown
  int32  color = 11;                                          // users only (avatar colour)
  string public_id = 13;                                      // users only (== UserProfile.public_id)
}
```

Semantics worth designing around:
- **Counts are exact**: the lists' entry counts equal the profile's counts. `following_count` mixes artists and users
  (U2: 1063 = 897 artists + 166 users) — the page can split them by URI kind.
- **No "follows you" / mutual marker** anywhere (93 of U2's 118 followers are also followed back; nothing says so).
- **Public playlist images** take five forms: `https` (`i.scdn.co` / `image-cdn-{ak,fa}.spotifycdn.com`),
  `pickasso.spotifycdn.com` radio art, `spotify:image:<id>`, and `spotify:mosaic:<4 album ids>` (no custom cover — the
  client composes a 2×2 of 300 px album tiles; the CDN never serves a mosaic for these).
- **Viewer-relative flags**: `is_following` on the profile and on playlist cards are the viewer's; `owner_follows` on a
  recently-played artist is the profile owner's.
- Never observed (need another capture): `is_verified`, `report_abuse_disabled`, public episodes (`episode_limit`),
  a profile with hidden follows / no public playlists, the list size cap.

The repo's existing REST fallback (`Spotify.Api.ProfileRoute`, JSON Accept, no limits) only proves name + image; the
older `user_profile_social.proto` draft in the wire-research notes has the list entries wrong (11 = colour, 13 = public_id).

---

## 3. Pathfinder (`POST api-partner.spotify.com/pathfinder/v2/query`)

Every hash Wavee already holds matches the capture. Profile-relevant operations:

| Operation | sha256 (persisted query) | Variables | Response (paths that matter) |
|---|---|---|---|
| `userTopContent` (own profile only) | `49ee15704de4a7fdeac65a02db20604aa11e46f02e809c55d9a89f6db9754356` | `includeTopArtists:true, topArtistsInput{offset:0,limit:10,sortBy:"AFFINITY",timeRange:"SHORT_TERM"}, includeTopTracks:false, topTracksInput{limit:4,…}`; "See all" uses limit 100 | `data.me.profile.topArtists{totalCount, items[].data{uri, profile.name, visuals.avatarImage.sources[640,320,160]}}` |
| `isFollowingUsers` | `c00e0cb6c7766e7230fc256cf4fe07aec63b53d1160a323940fce7b664e95596` | `{uris:["spotify:user:<id>"]}` (not `ids`) | `data.users[]: User{uri, following} \| NotFound{uri}` |
| `followUsers` / `unfollowUsers` (same hash) | `c00e0cb6…` | `{usernames:["<bare id>"]}` — bare ids, not URIs | `data.followUsers.responses[]{__typename:"FollowUserResult", result, username}` (Unfollow…Result) |
| `searchUsers` | `8f358dd82e62f61dd4ceaa9f8cd0889e644c9b707f1b724fbfb356a757cb7e5a` | `searchTerm, offset:0, limit:30, numberOfTopResults:20, …` | `data.searchV2.users{totalCount, pagingInfo{nextOffset}, items[].data{id, username, uri, displayName, avatar{sources[64,300], extractedColors.colorDark}}}` — avatar may be null; a special-character id is percent-encoded in the URI |
| `fetchExtractedColors` | `36e90fcaea00d47c695fce31874efeb2519b97d4cd0ee1abfb4f8dc9348596ea` | `{imageUris:[1–10 URLs]}` (positional answer) | `data.extractedColors[]{colorDark, colorLight, colorRaw}{hex, isFallback}`; the profile header uses **`colorRaw` of the avatar's 300 px URL**; a mosaic returns the fallback `#535353` |

The persisted-query hash is shared by several operation names (the operation name selects). Recent searches
(`recentSearches` / `saveRecentSearches` / `removeRecentSearches`) moved to hash `3ec071f8…` (the older docs' `2520a5aa…`
has rotated); a visited profile is saved there with `{uris:["spotify:user:<id>"]}`.

**Wavee lacks:** `isFollowingUsers`, `followUsers`, `unfollowUsers`, `fetchExtractedColors` (Wavee's
`getDynamicColorsByUris` takes only `spotify:image:` URIs — it cannot grade an https avatar or a mosaic),
`lookupChildEntities`, the recent-searches trio. Differences without a hash mismatch: Wavee's `userTopContent` asks top
tracks (limit 10) where the official profile does not; `searchUsers` uses `numberOfTopResults 30` vs 20.

---

## 4. Extended metadata — what it is (and is not) for profiles

No `spotify:user:` entity is requested during any profile visit. Only two kinds ever name a user:

- **Kind 15 `USER_PROFILE`** (`spotify.identity.v3.UserProfile`, cache_ttl 6 h) — the avatar/owner chip everywhere else
  (own avatar, playlist owner, recents owner names, library rows). Every scalar is wrapped `{1: value}`:
  `1 username` (vanity or id) · `2 display_name` · `3 images[{1:w,2:h,3:url}]` (64 and 300) · `9 has_spotify_name` ·
  `10 has_spotify_image` · **`11 color` (0xRRGGBB — not a follower count, as older notes said)** · `24 public_id`.
  Wavee decodes only 2 and 3; `color`/`username`/`public_id`/`has_spotify_image` are free to add (an instant accent and
  the initial-on-colour avatar).
- **Kind 178 `IDENTITY_TRAIT`** — only 304/404 for users in this capture (shape unknown).

No profile-specific kind exists in practice: `USER_PROFILE_V2` (215), `PRIVACY_TRAIT`, `BADGES`, `CREATOR_*`… are never
requested. Followers/following counts and lists are REST-only.

Optional card upgrades (the REST profile already carries name, owner and image): **178** (`contentagnostic.v2.IdentityTrait`:
class label, title, description, parent, creators — ~120–600 B) and **179** `VISUAL_IDENTITY_TRAIT` (images 64/300/640/1280 +
a 3-variant ColorSet of 5 RGBA each + flat; ~0.5–1.4 KB) — 179 can replace `fetchExtractedColors` for cards; **149**
`ROOTLISTABILITY` (can be added to the library). Never put `ARTIST_V4` (5.6–26.5 KB) on a card.

Protocol facts confirmed (Wavee already matches most): ≤300 entities per POST, real `Content-Encoding: gzip` on larger
request bodies, per-entity etag → 304 (kind 15's 304 carries no etag echo), 404 = "kind does not exist for this entity"
(cacheable for its `cache_ttl`), `offline_ttl` 30 d everywhere, response arrays sorted by kind then URI. The official
client keeps etags across sessions (startup is all-304 from a disk cache); Wavee's etag cache is in-memory only.

---

## 5. Playlist side, images, telemetry

- **Not needed to draw the page.** Per card the official client calls `playlist-permission/v1/playlist/<id>/permission/base`
  (`Permission{1: revision, 2: level}` — every other user's public playlist is VIEWER; it carries no signal for a
  profile) and a full `playlist/v2/playlist/<id>` (no `decorate`) or a `/diff` for a cached own playlist, then track
  metadata for the head tracks. Card length / last-modified / collaborative exist only in that playlist GET.
  `permission/members` on someone else's playlist is 200 + empty. `popcount/v2/playlist/<id>/count` matches the card's
  `followers_count`. `recently-played/v3` is the viewer's own history, not a profile source.
- **Image size codes** (prefix → pixels): user avatar `ab6775700000ee85` 300 / `ab67757000003b82` 64; artist
  `ab6761610000e5eb` 640 / `ab67616100005174` 320 / `ab6761610000f178` 160; album `ab67616d0000b273` 640 /
  `ab67616d00001e02` 300 (also the mosaic tile) / `ab67616d00004851` 64; custom playlist `ab67706c0000da84` ~300 (JPEG) /
  `ab67706c0000d72c` ≤300 (WebP); editorial playlist `ab67706f00000002` 300. JPEG and WebP only; covers carry a 183-day
  max-age. The official client loads covers only for visible cards.
- **Telemetry (gabo)**: `UbiExpr2PageView page_id=profile entity_uri=spotify:app:profile`; the user URI rides
  `KmPageView.view_uri` and impressions' `parent_path_uris`; shelf ids as in §1; a Follow click logs
  `UbiProd1Interaction action=follow`.

---

## 6. Wavee today, and the gaps

Wavee has no profile page, no `RouteKind.User`, no user deep link and no follow-user action — but the data skeleton is
further along than it looks:

| Layer | Today | Gap / cheapest seam |
|---|---|---|
| Route | 33 `RouteKind`s, none for users; `Shell.For` → NotFound; `TryParseSpotifyUri` refuses `spotify:user:` on purpose; search "Profiles" hits, the omnibar, playlist owner rows, the friends rail and the account flyout are inert | add `RouteKind.User` (+ test count), the deep-link arm, and the click sources (`Detail.Identity.Owner` already carries the owner URI) |
| Entity | a user table with Name, Image, Followers, Following columns and a `UserFields.Social` group with a working commit path — never filled | stage the REST profile into it; a profile demand for `Social` must route to REST (kind 15 cannot fill it) |
| Transport | kind 15 decoded for name + image; the REST profile exists only as a JSON fallback with no limits | a protobuf decoder for §2 (followers/following/public playlists/recent artists need new edges + `FetchEdge` members + a spclient route); widen kind 15 to `color`; a mosaic/`spotify:image:` cover resolver |
| Own profile | name/avatar/tier; `Edges.UserTopArtists/Tracks` exist for me | the same page through `User.Me`; top artists from `userTopContent` |
| Follow | `FollowToggle` works for artists/playlists; user arms refused | `followUsers`/`unfollowUsers`/`isFollowingUsers` on the existing Pathfinder mutation pattern + user arms in the saved/toggle seams; re-ensure the profile after a write |
| Colour | `getDynamicColorsByUris` (`spotify:image:` only) | `fetchExtractedColors` for the avatar, or kind-15/REST `color` as the instant accent |
| UI | — | the Artist page is the template (PageHost, hero layout, `PagedShelf` + `Controls.Surface` shelves, `FollowToggle`); not `Detail.Frame` (a track-table shell). A user card adapter for `Controls.Surface` (circular avatar, initial on `color` when none) |

Suggested order (each shippable): route + deep link + click-through to an identity-only page → counts (REST) → public
playlists + recently played artists → followers / following lists → follow / unfollow → own-profile top artists.
Watch the existing trap: the `ContentFilters` route ignores the URI and fetches the account's chips — a profile page
must never demand `UserFields.All`.

---

## 7. Open questions (a second capture would settle them)

1. Follow → open lists → unfollow → open lists: list-entry field 6 and whether lists refresh.
2. A verified user, an artist-linked profile, a profile with hidden follows / no public playlists (→ `is_verified`,
   `show_follows`, `allow_follows`, the privacy-absent fields).
3. A user with public episodes (the `episode_limit` field number).
4. The size cap of the unpaged following list; whether `Accept: application/json` is honoured on `/followers` and
   `/following`.
5. Where "Show all" public playlists come from (nothing in the capture fetched more than 10).
