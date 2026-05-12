# Wavee.Local

Local-files library for WaveeMusic. Owns scan, classify, index, enrich,
query, and edit for `wavee:local:*` URIs. Framework-neutral, AOT-compatible,
no dependency on `Wavee.dll` or any UI.

## Scope

- Watched-folder management (add / remove / enable / rescan)
- Filesystem scanning + metadata extraction (ATL.Net)
- Content classification — Music / MusicVideo / TvEpisode / Movie / Other
- TV-series and season auto-grouping from filenames
- Subtitle discovery (sibling files + RARBG-style `Subs/` folders)
- Embedded audio / subtitle / video track indexing (via host-supplied
  `IEmbeddedTrackProber` impl)
- Online metadata enrichment — TMDB (movies + TV), MusicBrainz + Cover
  Art Archive (music)
- Local lyrics (sibling `.lrc` files + LrcLib fetch)
- User-defined collections + custom show/album groupings
- Per-item kind override + metadata override (JSON sidecar; never
  overwrites ATL tags)
- Watched state + resume position for video; recently-played for audio
- Liked local tracks (delegates to host `ILocalLikeService` /
  `entities.is_locally_liked`)

## Public surface

- `ILocalLibraryService` — folder management, read API, watcher hookup
- `ILocalEnrichmentService` — background TMDB / MusicBrainz lookups
- `ILocalLikeService` — like state for local tracks
- `ILocalMediaPlayer` — video playback dispatch (impl in host process,
  e.g. `Wavee.UI.WinUI/Services/LocalMediaPlayer.cs` uses
  `Windows.Media.Playback`)
- `IVideoThumbnailExtractor` — frame extraction for poster fallback
- `IEmbeddedTrackProber` — enumerates embedded streams in `.mkv` /
  `.mp4` (impl in host process via Media Foundation)
- `LocalUri.Build*` / `LocalUri.TryParse` — `wavee:local:{kind}:{hash}`
  URI helpers
- `LocalArtworkCache` — deduped artwork cache under
  `%LOCALAPPDATA%/Wavee/local-artwork/`; emits `wavee-artwork://{hash}` URIs
- `LocalFilenameParser` — extracts hints from filenames (episode markers,
  year, artist-title patterns, release-group strip)
- `LocalContentClassifier` — pure-function classifier producing
  `LocalContentKind` + parsed hints
- Models: `LocalTrackRow`, `LocalAlbumDetail`, `LocalArtistDetail`,
  `LocalShow`, `LocalMovie`, `LocalEpisode`, `LocalCollection`,
  `LocalSubtitle`, `LocalEmbeddedTrack`, `MetadataPatch`,
  `EnrichmentProgress`, ...

## Schema

Wavee.Local does NOT own a SQLite database. It writes to the shared
metadata DB hosted by `Wavee.Core.Storage.MetadataDatabase` in `Wavee.dll`
(the same DB used to cache Spotify entities). The local-files schema is
declared as SQL string constants in `Schema/LocalSchemaV17.cs` and
imported by `MetadataDatabase`'s v17 migration. See
`docs/superpowers/specs/2026-05-12-local-files-redesign-design.md` for the
"Schema migration" section.

## AOT posture

Strict — `IsAotCompatible=true`, `WarningsAsErrors` includes the full
IL2xxx / IL3xxx set. No reflection codegen. ATL.Net,
Microsoft.Data.Sqlite, and System.Reactive are pure-managed.

## Why a separate project

- Spotify protocols (`Wavee.dll`) don't need local-files code
- Console / future surfaces can consume it without WinUI
- Local-files changes don't trigger Spotify protocol rebuilds
- Pure separation of concerns: protocol stack ↔ local-media library
