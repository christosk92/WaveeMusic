# Wavee domain entity model

Generated 2026-09-11 from the actual domain types in `src/apps/Wavee.Core/{Domain,Catalog,Library,Social}`
and the query layer in `src/apps/Wavee/Backend/{Catalog,Queries}` — not a guess, every relationship below
is a field on a real record in the tree.

## The one non-obvious thing to understand first

Wavee doesn't have a single entity graph. It has **two**, and the whole catalog/query system
(`8b87e107`, the most recent commit on this branch) exists to bridge them:

1. **The normalized graph** (`Wavee.Core.Catalog`) — the actual cache. Every entity is an opaque
   `EntityUri`; nothing about it is known except by fetching independently-cached, independently-expiring
   **facets** (`TrackIdentity`, `AlbumDetail`, `ArtistOverview`, …) and **relations** (`AlbumTracks`,
   `ArtistDiscography`, `ShowEpisodes`, …). This is deliberately Apollo/Relay-shaped: an "Album" doesn't
   exist as a row anywhere — it's a `AlbumIdentityValue` facet plus a paged `AlbumTracks` relation, both
   keyed by the same `spotify:album:…` uri, both cached and revalidated separately.
2. **The read model** (`Wavee.Core` — `Track`, `Album`, `Artist`, `Playlist`, `Show`, …) — the rich,
   denormalized records pages actually bind to, with real embedded lists (`Album.Tracks`,
   `Artist.TopAlbums`). `Wavee/Backend/Queries/CatalogQueryDefinitions.cs` + `QueryService.cs` assemble
   these on demand by joining facets to their relations and resolving each relation item's uri back into
   whatever nested ref/entity the record needs.

Everything below describes the read-model graph first (§2 — what a page actually sees), then the
normalized graph underneath it (§4 — what the cache actually stores).

## 1. The universal identity: `EntityUri` / `EntityKind`

Every entity in the app — regardless of provider — is addressed by one parsed value
(`Wavee.Core.Catalog/EntityUri.cs`):

```
EntityUri = (Provider, Kind, Id)   parsed from a string uri, e.g. "spotify:album:6akEvsy...":

Provider:  spotify | local | user (wavee:playlist:*) | wavee-podcast | fake | "" (unowned)
Kind:      Track, Episode, Album, Artist, Playlist, Show, User,
           Collection (spotify:collection:tracks = Liked Songs, and its 3 siblings),
           Prerelease, Concert, Unknown
```

`EntityKind.IsPlayable` (`Track`/`Episode`) and `EntityKind.IsContainer` (`Album`/`Playlist`/`Show`/
`Collection`/`Artist`) are the two predicates every routing/queue decision in the app reduces to. This
single parser replaced ~40 hand-rolled `uri.StartsWith("spotify:track:")` checks across the codebase.

## 2. The read-model entity catalog

| Entity | File | Identity | Owns / embeds | Referenced by |
|---|---|---|---|---|
| **Track** | `Domain/Models.cs` | `Id, Uri` | `Artists: ArtistRef[]`, `Album: AlbumRef` (both **shallow refs**, never the full object) | `Album.Tracks`, `Playlist.Tracks`, `QueueEntry.Track`, `SearchResults.Tracks`, `Artist.TopTracks` |
| **Album** | `Domain/Models.cs` | `Id, Uri` | `Artists: ArtistRef[]`, `Tracks: Track[]?`, `MoreByArtist: Album[]?` (self-ref), `OtherVersions: Album[]?` (self-ref), `ArtistsDetailed: Artist[]?` | `Track.Album` (back-ref), `Artist.TopAlbums/LatestRelease/PopularReleases`, `SearchResults.Albums` |
| **Artist** | `Domain/Models.cs` | `Id, Uri` | `TopAlbums/AppearsOn: Album[]?`, `TopTracks: Track[]?`, `Pinned: PinnedItem?`, `Extras: ArtistExtras?` | `ArtistRef` everywhere tracks/albums point back; `RelatedArtist` (self-ref, "fans also like") |
| **ArtistExtras** | `Domain/Models.cs` | — (owned 1:1 by `Artist`) | `Concerts[]`, `Merch[]`, `Playlists: PlaylistRef[]`, `MusicVideos[]`, `TopCities[]`, `ExternalLinks[]`, `Gallery: Image[]`, `Related: RelatedArtist[]`, `Tour: TourBanner?`, `WatchFeed?`, `PreRelease: ArtistPreRelease?` | the artist "magazine" page's satellite data — one bag per artist |
| **Playlist** | `Domain/Models.cs` | `Id, Uri` | `Tracks: Track[]?`, `Owner: Owner?`, `Collaborators: Owner[]?`, `Tuning: PlaylistTuning?`, `Capabilities: PlaylistCapabilities` | `PlaylistRef` (lightweight pointer), `PlaylistLeaf.Playlist: PlaylistSummary` (sidebar-weight pointer) |
| **PlaylistNode** (`PlaylistLeaf` \| `PlaylistFolder`) | `Library/Sidebar.cs` | — | `PlaylistFolder.Items: PlaylistNode[]` — **recursive**, a folder can contain folders | the sidebar's "Your Library" playlist tree |
| **Show** | `Domain/Models.cs` | `Id, Uri` | `Episodes: Episode[]?` (paged residency, not all-at-once) | `Episode.ShowUri` (back-ref) |
| **Episode** | `Domain/Models.cs` | `Id, Uri` | `ShowUri: string?` (back-ref to `Show`) | `Show.Episodes`; projected into a `Track` via `EpisodeAsTrack.From()` (see §3) whenever a list surface needs a playable |
| **Owner** | `Domain/Models.cs` | `Id, Name` | `Avatar: Image?` | `Playlist.Owner`, `Playlist.Collaborators[]` — the *user* entity, but modeled as a display projection, not a full profile (see `UserProfileIds`, which normalizes the id spelling but stores the resolved owner as an ordinary store entity, not a service) |
| **Concert** / **ConcertDetails** | `Domain/ConcertModels.cs` | `Uri` | `Artists: ConcertArtist[]?` (lineup, each with its own optional `Uri`→`Artist`) | `ArtistExtras.Concerts`, `ConcertFeedSection`, `ArtistConcertSchedule` |
| **QueueEntry** | `Domain/Models.cs` | `ItemId: QueueItemId` (monotonic, session-stable) | `Track: Track` (exactly one, always fully resolved) | the four `QueueBucket`s: `NowPlaying`, `UserQueue`, `NextUp`, `History` |
| **HomeCard** | `Library/HomeFeed.cs` | `Uri` (points at *any* kind) | `Kind: HomeCardKind` discriminates what `Uri` actually is (`Playlist/Album/Artist/Track/Liked/Episode/Audiobook/Podcast`) — a **generic, untyped reference**, not a foreign key | `HomeGroup.Cards[]`, itself grouped into `HomeSection`/`HomeFeed` |
| **SearchResults** | `Library/Library.cs` | — (a response, not an entity) | fans out to **every** entity kind at once: `Tracks/Albums/Artists/Playlists/Shows/Episodes: T[]`, plus generic `SearchTopHit`/`SearchSuggestionItem` for kinds with no dedicated record (`Audiobook`, `Profile`, `Genre`) | — |
| **LibraryItem** | `Library/Library.cs` | `Uri` | `Kind: LibraryItemKind` (`Track/Album/Artist/Playlist`) — same generic-reference pattern as `HomeCard` | the flat "Your Library" list |
| **FriendActivity** | `Social/FriendActivity.cs` | `UserUri` + `TimestampMs` | **fully denormalized**: `TrackUri/TrackName`, `AlbumUri/AlbumName`, `ArtistUri/ArtistName`, `ContextUri/ContextName` all inlined as flat strings — no refs, by design (a display-only feed row, never joined back to the catalog) | — |
| **TrackVersion** | `Library/TrackVersions.cs` | `Uri` | `Kind: Original\|Video\|Audio` — alternate cuts of "the same song" | `TrackExpansion.Versions[]` (the expand-drawer, fetched on demand) |
| **VideoAssociation** | `Domain/Video.cs` | `Uri` (the track/video itself) | `CounterpartUri: string?` — points at the **paired** entity (audio↔video) | a *different* relationship from `TrackVersion`: this is "does this exact uri have a video/audio counterpart", cached with its own freshness window |
| **PinnedItem** / **ArtistPreRelease** / **PreReleaseLink** | `Domain/Models.cs` | `Uri` (pin) vs `PreReleaseUri` vs `AlbumUri` | a pre-release album has **two distinct uris** — the thing a pre-save collection-write addresses (`PreReleaseUri`) and the thing the app navigates to once it ships (`AlbumUri`) — reconciled by `Domain/PreReleaseUris.cs`, never derived from one another | `Artist.Pinned`, `ArtistExtras.PreRelease` |

## 3. The read-model relationship diagram

```
                    ┌─────────────┐        Related (self, "fans also like")
        ┌──────────▶│   Artist    │◀───────────────────┐
        │           └──────┬──────┘                     │
        │     TopAlbums/AppearsOn/LatestRelease/         │
        │     PopularReleases (Artist → Album*)          │
        │                  │                              │
   Artists[]               ▼                        Extras.Related
   (many-to-many)    ┌─────────────┐   MoreByArtist  ┌──────────┐
        │            │    Album    │◀───(self, N)────┤  Album   │
        │            └──────┬──────┘   OtherVersions │(editions)│
        │                   │           (self, N)    └──────────┘
        │              Tracks[] (1→N)
        │                   ▼
        │            ┌─────────────┐   AddedAt/AddedBy = per-playlist
        └───Artists[]─┤    Track    │◀──membership metadata, NOT ownership
                      └──────┬──────┘
                             │ wrapped 1:1 for playback
                             ▼
                      ┌─────────────┐
                      │ QueueEntry  │── Bucket: NowPlaying|UserQueue|NextUp|History
                      └─────────────┘

  Playlist ──Tracks[]──▶ Track            Playlist ──Owner/Collaborators[]──▶ Owner (user)
     │                                        PlaylistNode tree (sidebar):
     └─PlaylistRef (lightweight pointer,   PlaylistFolder.Items[] ──▶ PlaylistNode (RECURSIVE:
       used by ArtistExtras/Home/Search)                              leaf playlist OR nested folder)

  Show ──Episodes[] (paged)──▶ Episode ──ShowUri (back-ref)──▶ Show
                                  │
                                  └──EpisodeAsTrack.From()──▶ Track  (podcast row plays anywhere
                                                                       a Track-shaped list expects one;
                                                                       Show fills the "album" slot)

  Concert ──Artists[]──▶ ConcertArtist ──Uri?──▶ Artist (optional; some lineup billing has no resolved artist yet)

  Generic / untyped references (Uri + Kind discriminator, not a real FK):
    HomeCard.Uri      + HomeCardKind      → Playlist | Album | Artist | Track | Liked | Episode | Audiobook | Podcast
    LibraryItem.Uri   + LibraryItemKind   → Track | Album | Artist | Playlist
    SearchSuggestionItem.Uri + Kind       → Track | Artist | Album | Playlist | Genre | Episode | Podcast | Audiobook | User

  Denormalized display-only join (never dereferenced back into the catalog):
    FriendActivity  { UserUri/Name, TrackUri/Name, AlbumUri/Name, ArtistUri/Name, ContextUri/Name }  — all flat strings
```

Two relationship shapes recur constantly and are worth naming:

- **Ref, not object.** `Track.Artists` is `ArtistRef[]` (id/uri/name only) and `Track.Album` is a bare
  `AlbumRef` — never the full `Artist`/`Album`. The full object is only ever embedded going the *other*
  direction (`Album.Tracks: Track[]`, `Artist.TopTracks: Track[]`). This keeps a 10,000-row tracklist from
  ever forcing 10,000 full artist objects into memory.
- **Generic reference, not a typed FK.** `HomeCard`, `LibraryItem`, and `SearchSuggestionItem` all use the
  same `(Uri, KindEnum)` pair to point at *any* entity kind, rather than five nullable typed fields. The
  discriminator enum is what a renderer switches on to decide the card shape and the nav route.

## 4. The normalized catalog graph underneath it

`Wavee.Core.Catalog` (`Resources.cs`, `Values.cs`) is what actually gets cached, persisted, and
revalidated. Nothing here is a rich object — everything is a **facet** or a **relation**, keyed by the
same `EntityUri` from §1:

```csharp
ResourceKey = (CatalogScope, Subject: EntityUri, FacetKind, ResourceArguments)
```

**Facets** — one closed fact-bundle per entity, independently fetched/cached (`CatalogValue` subtypes):
`TrackIdentityValue`, `EpisodeIdentityValue`, `AlbumIdentityValue`, `ArtistIdentityValue`,
`PlaylistHeaderValue`, `ShowIdentityValue`, `UserIdentityValue`, plus cross-cutting facets that apply to
*any* subject uri: `PlayCountValue`, `DescriptorsValue`, `AudioAttributesValue`, `PublishingValue`,
`AvailabilityValue`, `VideoAssociationValue`, `VisualIdentityValue`, `AlbumDetailValue`,
`ArtistOverviewValue`, `EpisodeDetailValue`.

**Relations** — the actual edges of the graph. A `RelationPageValue` is a paged list of
`CatalogRelationItem(OccurrenceKey, EntityUri, RelationContext?)` — i.e. "subject X, facet
`AlbumTracks`, page N" resolves to an ordered list of *other* `EntityUri`s, each optionally carrying
positional context (`DiscNumber`/`TrackNumber` for an album track, `ChartEntry` for a chart-playlist row).
The relation kinds map 1:1 onto the read-model's embedded lists:

| Relation (`FacetKind`) | Read-model equivalent |
|---|---|
| `AlbumTracks` | `Album.Tracks` |
| `AlbumVersions` | `Album.OtherVersions` |
| `ArtistDiscography` (filtered by Albums/Singles/Compilations) | `Artist.TopAlbums` / the discography grid |
| `ArtistPopular` | `Artist.TopTracks` |
| `ArtistAppearsOn` | `Artist.AppearsOn` |
| `ArtistRelated` | `ArtistExtras.Related` |
| `ShowEpisodes` | `Show.Episodes` |
| `PlaylistRevision` | membership list behind `Playlist.Tracks` |
| `Home` / `HomeSection` / `Search` / `SearchSuggestions` | `HomeFeed`/`SearchResults` — a **synthetic subject** (`wavee:catalog:home`, `wavee:catalog:search:<query>`) whose "relation" is a page of `CatalogDocumentItem` refs into real entities, exactly like an album's track list is a page of refs into real tracks |

`Wavee/Backend/Queries/CatalogQueryDefinitions.cs` + `QueryService.cs` is where the two graphs meet: a
query definition (`AlbumQuery`, `ArtistQuery`, `ShowQuery`, …) asks the coordinator for a subject's
identity facet plus its relation pages, resolves each relation item's uri back into its own identity
facet, and assembles the result into the rich `Album`/`Artist`/`Show` record a page binds to. This is why
`Wavee.Tests` can unit-test relationship *shape* (ordering, dedup, pagination) against the pure
`Catalog.Values`/`Resources` types without ever touching a real network call.

## 5. Why it's built this way (from the code's own comments)

- **Facets are independent on purpose.** An album's hero + track list must not block on
  below-the-fold data — `IAlbumEnrichmentService` (`Library/AlbumEnrichment.cs`) is a deliberately
  separate read path from `IMusicLibrary.GetAlbumAsync` for exactly this reason (related artists, merch,
  similar albums, recommended playlists all fetch and fail independently).
- **0 / null / empty all mean different things**, consistently, across every entity: `Track.Year == 0`
  means unknown (never "year zero"); `Availability == null` means "nobody has told us yet" (distinct from
  `Unavailable`); `PreReleaseLink.ReleaseAt == null` counts as *upcoming* everywhere **except**
  `PinnedItem`, where a null date means "an ordinary released album with no countdown" — the one
  deliberate polarity flip in the model, called out explicitly in the doc comments on both records.
- **Two audio pairings that look similar but aren't**: `TrackVersion` (alternate cuts of the same
  logical song — live/remix/sped-up, surfaced in the expand drawer) vs `VideoAssociation` (does *this
  exact* uri have an audio/video counterpart, a separate cache with its own positive/negative TTLs).
  Conflating them was an actual historical bug this model is structured to prevent.
- **Podcasts reuse the Track pipeline instead of getting a parallel one.** `EpisodeAsTrack.From()` is the
  single projection every list surface (queue, playlist, recents) uses to render an episode — rather than
  every consumer needing its own `Track`-or-`Episode` branch.
