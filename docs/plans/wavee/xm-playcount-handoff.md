# XM playcount + ExtensionKind handoff

Self-contained paste. Do **not** implement unless asked. Date: 2026-08-15.

Two findings from the same session: (1) the official `ExtensionKind` table in shipping Spotify desktop, (2) a live extended-metadata probe that pins **arbitrary-track playcount** to kind **185**.

Full one-batch decode of kinds 1–258 (all entity types, field walks): `docs/plans/wavee/xm-kind-probe-overview.md`.

| Field | Value |
|---|---|
| Desktop | Spotify **1.2.95.453** (`%APPDATA%\Spotify`, `Spotify.exe` FileVersion) |
| Kind table | `%APPDATA%\Spotify\Apps\xpui.spa` → `4764.js` (compiled protobuf-ts `ExtensionKind` map) |
| Probe URI | `spotify:track:6ZFbXIJkuI1dVNWvzJzown` (Hans Zimmer, *Time*) |
| Probe host | `POST https://gew4-spclient.spotify.com/extended-metadata/v0/extended-metadata` |
| Wavee proto | `src/apps/Wavee/SpotifyLive/Protos/extension_kind.proto` (already updated through 258) |

Do not reuse any bearer that was used for the probe. Mint a fresh session token.

---

## 1. Official ExtensionKind names (how)

`xpui.spa` is a zip. `4764.js` contains the bidirectional enum three times. The assignment form is the one to parse:

```text
t[t.UNKNOWN_EXTENSION=0]="UNKNOWN_EXTENSION",t[t.CANVAZ=1]="CANVAZ",…,t[t.CONTENT_MARKERS=258]="CONTENT_MARKERS"
```

0–215 already matched Wavee. 104 is still unused. Capture-era type_url spellings that were **wrong**:

| n | Wavee had (type_url-derived) | Official (xpui) |
|---|---|---|
| 217 | `MIX_BEATS` | `BEATS` |
| 218 | `MIX_VOCAL_ACTIVITY` | `VOCAL_ACTIVITY` |
| 237 | `MIX_THREE_BAND_WAVEFORMS` | `THREEBAND_WAVEFORMS` |

Those three renames are already in the proto. The one C# call site is `SpotifyTrackExpansionService` → `ExtensionKind.ThreebandWaveforms`.

216–258 (official identifiers, now in the proto): `VIDEO_RELATIONS`, `BEATS`, `VOCAL_ACTIVITY`, `MIXABILITY`, `ENTITY_TYPE_TRAIT`, `CHAT_SHARE_PREVIEW`, `AUDIO_ATTRIBUTES_V2`, `WATCH_FEED_CATEGORIES_TRAIT`, `VENUE_ARTISTS_PREVIEW`, `MIX_STATE`, `WATCH_FEED_SEED_ITEM_TRAIT`, `SONG_DNA_ELIGIBILITY`, `LEARNING_MATERIAL`, `BANNER_WITH_ANIMATIONS`, `VENUE_LOCATION`, `TRANSCRIPT_SEARCH`, `SONGDNA_CREDITS`, `AUDIOBOOK_TO_PHYSICAL_BOOK_MAPPING`, `ARTIST_WRAPPED_2025_VIDEO`, `MIXABILITY_TRAIT`, `SONGDNA_ARTIST_FACTS`, `THREEBAND_WAVEFORMS`, `AUDIOBOOK_PARTNER_SALES`, `CONTENT_CAPABILITY_TRAIT`, `CONCERT_CAMPAIGN`, `CONCERT_CAMPAIGN_USER_STATE`, `CONCERT_CAMPAIGN_STRINGS`, `CONCERT_CAMPAIGN_ROUTINGS`, `TRANSITION_DATA`, `CHAT_RICH_MEDIA`, `CURATION_EXPERIENCE_TRAIT`, `COMMON_TRANSITION_POINT`, `PLAY_LINK_CARD_TRAIT`, `CONTENT_EXPERIENCE_TRAIT`, `TRACK_ALTERNATIVE_VERSIONS`, `LICENSING_IDENTIFIERS_TRAIT`, `SPONSORSHIP`, `AUDIOBOOK_DIRECT_SALES`, `ALBUM_MUSIC_VIDEOS`, `CREATOR_ARTIST_FACTS`, `PLAYBACK_EXPERIENCE_TRAIT`, `SPEEDABILITY`, `CONTENT_MARKERS`.

---

## 2. Arbitrary-track playcount (what)

**Kind 185 `ON_PLATFORM_REPUTATION_TRAIT`** is the batchable playcount for any `spotify:track:` URI.

```
type.googleapis.com/spotify.contentagnostic.v2.OnPlatformReputationTrait
```

Live 200 on the probe track:

| | |
|---|---|
| HTTP / per-entity status | 200 / 200 |
| Payload | 6 bytes |
| Shape | **one field**: proto2 varint `f3 = 481338906` |
| `f1` / `f2` | absent |
| `cache_ttl_in_seconds` | 3600 |
| `offline_ttl_in_seconds` | 2592000 (the usual constant) |

481,338,906 is a real stream-count magnitude for *Time*. The wire does not name the unit — treat `f3` as `Track.PlayCount`.

This matches the earlier SAZ census (`docs/plans/wavee/spotify-wire-research/research/03-XM-PAYLOADS.md` kind 185): 100/100 track 200s, never a 304, `f3` only, range 141,184,618–2,138,988,345. The census called the unit inferred. The live probe is the confirmation that it answers on an **arbitrary** track, not only album/artist-popular GraphQL surfaces.

### Not playcount

| Kind | Probe result | Why it is not the answer |
|---|---|---|
| 60 `STREAM_COUNT` | **404** | leftover enum name; desktop does not serve it for tracks |
| 10 `TRACK_V4` | 200 `spotify.metadata.Track` | `popularity = 8` (150 here), `earliest_live_timestamp = 17` (413146860 here). **No playcount field.** This is why `LiveSessionHost` already says V4 cannot fill the Plays column. |
| 7 `PODCAST_COUNTER` / 184 `DETAILED_EVALUATION_TRAIT` | 404 | wrong entity class |
| Pathfinder `getTrack` | named `trackUnion.playcount` | one GraphQL call per track; 403'd on this probe without a client-token. Still valid, not batchable. |

A 1–258 sweep on the same URI produced 64×200 / 152×404 / 27×400 plus a handful of 5xx. **No other 200 payload looked like a playcount.** Kind 168 `SPEECHLESS_SHARE_CARD` had `f3=6185314` — wrong magnitude, different message.

### Polymorphic warning

xpui also reads `onPlatformReputationTrait.rating.average` on **shows / audiobooks / artist unions** (star rating / verification). The message is not track-only.

Live one-batch (same POST as the kind overview):

| URI class | Shape | Read as |
|---|---|---|
| `spotify:track:` | `f3` only (6 B) | playcount |
| `spotify:artist:` | `f1` + `f2=1` + `f4` + `f9{1,1}` (18 B) | monthly listeners / verified / followers — **not plays** |
| `spotify:album:` | **404** | no album playcount |

Do not write artist `f1` into `Track.PlayCount`. Do not decode `rating` on a track payload that never sent it.

---

## 3. How to request it

Same envelope Wavee already uses (`ExtendedMetadataSource.GzipExtensionRequest` / `GetExtensionsAsync`).

```
POST {spclient}/extended-metadata/v0/extended-metadata
Content-Type: application/protobuf
Content-Encoding: gzip
Accept: application/protobuf
client-feature-id: track_metadata_loader   # attribution; the body decides the kind set
Authorization: Bearer <access token>
```

`client-token` is required on a real desktop session (Wavee's `ClientTokenMiddleware`). This probe's HTTP 200 went through with bearer alone against `gew4-spclient`; do not treat that as the production contract.

Body = gzip(`BatchedEntityRequest`):

```
header { country, catalogue="premium", task_id=16 random bytes }
entity_request[] {
  entity_uri = "spotify:track:…"
  query[] { extension_kind = 185 }   # plus any other kinds on the same uri — one POST
}
```

Response `BatchedExtensionResponse` → `EntityExtensionDataArray.extension_kind == 185` → `EntityExtensionData.header.status_code` + `extension_data` (`google.protobuf.Any`). Dispatch on the array's kind (Wavee's existing contract). Inner `Any.value` is the 6-byte trait: field 3 varint.

Census: kind 185 **never 304s**. Etag is optional. Always refetch; honor the 3600s ttl.

---

## 4. What Wavee does today

Playcount is GraphQL-only:

| Source | Surface | Mapper |
|---|---|---|
| `queryArtistOverview` | artist Popular chart (top ~10) | `SpotifyExportMapper` → `ArtistTopTrack(uri, plays)` |
| `getAlbum` | album `tracksV2[].track.playcount` | `SpotifyExportMapper` → `Track.PlayCount` |
| `getTrack` | single-track hydrate | same, `trackUnion.playcount` |

`ArtistOverview.cs` still says *“queryArtistOverview is the only source of play counts”*. That is now stale: 185 is a second source, and it works for playlist rows / liked songs / any URI list.

Store merge already preserves a positive count (`Store.cs`: `incoming.PlayCount > 0 ? incoming : current`). That is load-bearing — a later TrackV4 write must not zero a 185 count. The artist-chart split (`ArtistTopTrack`) exists because TrackV4 / cluster / library rewrites were wiping plays; 185 does not remove that split, it just lets *other* surfaces get a count without `getAlbum`.

~~**Status 2026-08-15 — implemented for the artist chart.**~~ `Wavee/Backend/Metadata/TrackPlayCounts.cs` (`ITrackPlayCountSource` / `TrackPlayCountSource` over the shared source + etag cache) was step three of `SpotifyArtistPopularTracksService`. **Both classes are deleted.**

**Status 2026-08-16 — implemented everywhere, as a trait projector.** Kind 185 is now
`src/apps/Wavee/Backend/Hydration/Projectors/PlayCountProjector.cs` (`TraitSet.PlayCount`), one of seven projectors
on the shared trait POST — no client, no cap, no memo of its own. The f3 varint walk moved verbatim
(`PlayCountProjector.PlayCountField = 3`); the fill rules moved verbatim from `TrackPlayCountHydrator` (decorate a
resident row, never mint one, never invent a 0 — an absent count is *unknown*). `TraitApplicability` keeps the ask to
playables, so the polymorphic `rating` arm is never reached and never decoded.

Which surfaces request it, from the single `TraitPolicy` table:

| Surface | 185 asked? |
|---|---|
| `AlbumOpen` | always — the Plays star IS the album surface's identity |
| `ArtistPopular` | always — the counts are the chart's ordering |
| `PlaylistOpen`, `LikedSongs` | only when the user's Plays column is actually rendered |
| `PlaysToggle` | the toggle path: the column just came on for rows that already have their bundle |
| `ShowOpen` and everything else | no — 185 is a track trait |

Item 5's "highest-value first consumer" (album / playlist / liked rows that showed `0` / `—`) is therefore done, and
the artist ladder's `Full` rung still folds the counts onto the extended chart. Items 1–5 of §5 all hold as written;
the one deviation is that there is no per-surface call site to add — adding a surface is one line in `TraitPolicy`.
See `.claude/skills/wavee/hydration.md` and `docs/plans/wavee/hydration-facade-design.md` §2.4.

---

## 5. If implementing (later)

1. Ask for `Xm.ExtensionKind.OnPlatformReputationTrait` (185) on the track URIs that need a Plays column and do not already have `PlayCount > 0` from overview/getAlbum.
2. Parse `Any.value` as proto2: skip to field 3, read varint → `long`. No `.proto` is required for one field; add `on_platform_reputation_trait.proto` only if you want the polymorphic rating/verification arms later.
3. Write through the existing `PlayCount > 0` merge. Do not invent counts for 404.
4. Batch it with whatever XM the surface already sends (`GzipExtensionRequest` groups kinds under one `EntityRequest`). Do not add a per-row `getTrack`.
5. Do not request kind 60. Do not treat TrackV4 `popularity` or `earliest_live_timestamp` as plays.

Highest-value first consumer: album/playlist/liked-songs rows that currently show `0` / `—` because they never went through `getAlbum` / overview.
