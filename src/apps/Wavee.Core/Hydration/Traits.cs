using System;

namespace Wavee.Core;

// ── Traits (docs/plans/wavee/hydration-facade-design.md §1.4) ────────────────────────────────────────────────────────
// A TRAIT is a per-entity extended-metadata FACET that decorates a row already in the store — never a row's identity.
// Modelling them as flags is what lets one POST carry every wanted kind for a uri set instead of the 2–3 the four
// separate trait services used to fire (and each with its own 300-cap, etag arm and negative memo).

/// <summary>The extension facets a surface can want. The comment on each member is the extended-metadata kind it maps
/// to — the projector for that kind is the ONE place that decodes it.</summary>
[Flags]
public enum TraitSet : ushort
{
    None = 0,
    /// <summary>99 VIDEO_ASSOCIATIONS (+182 CONSUMPTION_EXPERIENCE companion; TRACK_V4/212 canonical recovery
    /// follow-up) → the VideoAssociation plane.</summary>
    Video = 1 << 0,
    /// <summary>222 audio attributes → <c>Track.TempoBpm</c>/<c>MusicalKey</c>/<c>Camelot*</c>.</summary>
    AudioAttributes = 1 << 1,
    /// <summary>6 TRACK_DESCRIPTOR → <c>Track.Tags</c> (an empty list writes <c>[]</c> — a real "has none").</summary>
    Descriptors = 1 << 2,
    /// <summary>179 → the CoverColorPlane (image-keyed, so no row write can clobber it).</summary>
    VisualIdentity = 1 << 3,
    /// <summary>185 → <c>Track.PlayCount</c> (the track f3 field).</summary>
    PlayCount = 1 << 4,
    /// <summary>183 → <c>Album.Copyright</c>/<c>ReleaseDate</c>/<c>ReleaseDatePrecision</c>. Album-only.</summary>
    Publishing = 1 << 5,
    /// <summary>178 + 220 — asked for wire fidelity with the desktop client; nothing is projected from them.</summary>
    IdentityTraits = 1 << 6,

    /// <summary>What every track ROW wants: the four facets a list surface paints.</summary>
    RowBundle = Video | AudioAttributes | Descriptors | VisualIdentity,
}

/// <summary>WHICH screen is asking. Two jobs: <c>TraitPolicy</c> maps it to a <see cref="TraitSet"/>, and
/// <c>TraitSurfaces.ClientFeatureId</c> maps it to the <c>client-feature-id</c> attribution header the desktop client
/// stamps per surface. Never a behaviour switch beyond those two tables.</summary>
public enum TraitSurface : byte
{
    None,
    AlbumOpen, PlaylistOpen, LikedSongs, ShowOpen, ArtistPopular,
    Queue, Search, Recents, NowPlaying, TrackExpansion,
    Credits, PreRelease, UserProfiles, Prefetch, Context,
}
