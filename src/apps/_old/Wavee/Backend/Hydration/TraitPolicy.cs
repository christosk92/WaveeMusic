using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── surface → trait bundle, and surface → attribution, as TWO pure tables (design §2.4) ──────────────────────────────
// Before this, "which extension kinds does this screen want?" was distributed across four services' own entry points
// (adornments, play counts, video detect, publishing), each with its own caller list — which is why the album page
// asked for kind 185 twice and the show page asked for nothing at all. Making it a table means one POST can carry a
// surface's whole bundle, and adding a surface is one line rather than four call sites.

/// <summary>THE surface → <see cref="TraitSet"/> table. A pure function of the surface: nothing here reads a user
/// setting, because a trait ask is a DATA decision and a setting is a DISPLAY one.</summary>
public sealed class TraitPolicy
{
    public TraitSet For(TraitSurface surface) => surface switch
    {
        // An album page paints the ©/℗ line (183, album-only) and the Plays star — the star IS the album surface's
        // identity.
        TraitSurface.AlbumOpen => TraitSet.RowBundle | TraitSet.PlayCount | TraitSet.Publishing,

        // A list of arbitrary playables: the row bundle AND the counts, always. This used to be gated on the Plays
        // column setting, and that gate STARVED the lane: the trait pass is a post-step of the OPEN rung, and a list
        // already at Open is short-circuited as "reached" (SpotifyProviderHydrator) — ContinueAsync never runs again,
        // so a bundle chosen while the column was off is the only bundle that list ever gets for the session. Kind 185
        // has no retry surface of its own, so the lane painted "—" no matter how many times the user reopened the page.
        // The setting now controls COLUMN VISIBILITY only; the data is always there when it is switched on.
        TraitSurface.PlaylistOpen or TraitSurface.LikedSongs => TraitSet.RowBundle | TraitSet.PlayCount,

        // Episodes have no play count (185 is a TRACK trait) — the row bundle's ask-once kinds are the whole story.
        TraitSurface.ShowOpen => TraitSet.RowBundle,

        // The artist chart renders counts as its ORDERING, so they are not optional here.
        TraitSurface.ArtistPopular => TraitSet.RowBundle | TraitSet.PlayCount,

        TraitSurface.Queue or TraitSurface.Search => TraitSet.RowBundle,

        // The recents viewport is the one surface the capture attributes 178/220 to; 179 is what tints its cards
        // before an image byte arrives.
        TraitSurface.Recents => TraitSet.IdentityTraits | TraitSet.VisualIdentity,

        // Now playing wants exactly one thing the row bundle would over-fetch for: does this playable have a video?
        TraitSurface.NowPlaying => TraitSet.Video,

        // Everything else asks for no traits. TrackExpansion/Credits/PreRelease/UserProfiles are DISPLAY-ONLY extension
        // reads (P2's IExtensionReader owns them — they decorate a drawer, not a row); Prefetch/Context/None are
        // identity-only waves whose whole point is to cost one catalogue POST and nothing more.
        _ => TraitSet.None,
    };
}

/// <summary>THE surface → <c>client-feature-id</c> table: the attribution header the desktop client stamps per surface.
/// Kept next to <see cref="TraitPolicy"/> because it answers the sibling half of "which screen is asking".</summary>
public static class TraitSurfaces
{
    /// <summary>Null means the header is omitted — which is the pre-existing behaviour for unattributed traffic, and
    /// stays that way rather than inventing an attribution the capture never showed.</summary>
    public static string? ClientFeatureId(this TraitSurface surface) => surface switch
    {
        // The scroll-driven recents viewport hydrator: the ONE caller the census attributes 178/179/220 to.
        TraitSurface.Recents => "mdata_esperanto",
        // Display-only reads the desktop client issues without this attribution, and the unattributed default.
        TraitSurface.PreRelease or TraitSurface.UserProfiles or TraitSurface.None => null,
        _ => "track_metadata_loader",
    };
}
