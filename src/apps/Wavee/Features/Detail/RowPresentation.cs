using System;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee;

/// <summary>Everything ONE realized track row renders from, as a value (Operation ultra-fast, P5 — the app-side
/// virtualized track row on the new bound-item API; <c>docs/plans/wavee/operation-ultra-fast-app-progress.md</c>).
/// Built by <c>TrackList.Presentation(TrackRowsSnapshot, int)</c> in <c>Features/Detail/DetailTracks.cs</c> — the
/// engine-bound page that owns every render-time consumer — and equality-gated by construction (a plain C# record
/// struct), so a bound source that republishes an equal value (an activity-only transition upstream, for example)
/// fires nothing downstream once a later slice reads it through a gated <c>Memo&lt;RowPresentation&gt;</c>.
/// <para>Split into its OWN file (Wavee.Core + BCL only — no FluentGpu, no <c>TrackList</c>) so <c>Wavee.Tests</c>
/// can source-include it directly, exactly like <c>DetailTrackProjection.cs</c> beside it
/// (<c>Wavee.Tests.csproj</c>'s <c>&lt;Compile Include="..\Wavee\Features\Detail\DetailTrackProjection.cs" /&gt;</c>
/// pattern) — <c>RowPresentationTests</c> drives the real type, not a copy of it. Its one non-BCL field type,
/// <see cref="TrackRow.State"/>, is itself split into the engine-free <c>Components/TrackRow.State.cs</c> for exactly
/// the same reason: the REST of <c>TrackRow.cs</c> is FluentGpu-bound and cannot compile into the test project.</para>
/// <para><paramref name="PlaysState"/> is the same projection the row grid's Plays cell already reads
/// (<c>TrackList.PlaysStateFor</c>), carried on the value itself so a later slice's bound template can read it off
/// one source instead of re-deriving it per cell. <paramref name="IsSkeleton"/> is title readiness only
/// (<c>TitleState == TrackTitleState.Loading</c>) — the reveal ramp is gone; a bound template picks shimmer vs.
/// real content from this one field. <paramref name="IsExpanded"/> mirrors
/// <c>_expandedRow</c> — whether THIS row's drawer is open — computed once per projection instead of once per cell
/// that needs to know.</para></summary>
readonly record struct RowPresentation(
    Track Track, int DisplayIndex, TrackRow.State State,
    bool MarqueeDisabled, bool ShowTrackArtist, bool ShowListMetadata,
    Action<string, string?> Go, Owner? AddedBy, TrackTitleState TitleState, bool HasVideo,
    TrackFactState PlaysState, bool IsSkeleton, bool IsExpanded,
    // Operation ultra-fast P5 slice 3: the Date-added cell's FormatCache<int> key — whole UTC days since the Unix
    // epoch (Track.AddedAt.Date, stripped to a stable small integer so BoundItemScope<T>.Text<T,TKey> can cache the
    // formatted "3 days ago"/"12 Aug" label instead of rebuilding it every recycle). -1 = no AddedAt (never rendered
    // as a real day). Engine-free (plain int arithmetic) — unlike the Plays tooltip text, which stays OUT of this
    // record because its formatter needs FluentGpu.Localization's Loc.Get (see TrackRowTemplate's own note).
    int AddedDayKey = -1)
{
    static readonly Track EmptyTrackValue =
        new("", "", "", Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0L, false, null);

    /// <summary>Out-of-range / not-yet-projected value: no track, non-interactive, skeleton on. What
    /// <c>BoundItems.Project</c>'s fallback hands a recycled slot whose index falls outside the current view (e.g. a
    /// shrink mid-recycle) and what every equality-gated bound channel should treat as "nothing to show".</summary>
    public static readonly RowPresentation Empty = new(
        EmptyTrackValue, 0, default, false, false, false, static (_, _) => { }, null,
        TrackTitleState.Loading, false, TrackFactState.Present, true, false, -1);

    /// <summary>A skeleton placeholder at a KNOWN display position — same shape as a real row (so a bound template
    /// never has to fork on "do I even have a position yet"), <see cref="IsSkeleton"/> on.</summary>
    public static RowPresentation Skeleton(int displayIndex) => Empty with { DisplayIndex = displayIndex };
}
