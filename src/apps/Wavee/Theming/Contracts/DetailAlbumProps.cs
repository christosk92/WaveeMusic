// ── Theming/Contracts/DetailAlbumProps.cs — the props of the album sub-contract's views (themes plan 6.0, T9b) ───────────────────────────────
//
// The album page keeps its model, its demands, its page gate and every trailing gate (`Album.PageHost`, `TrailingHost`, `SectionsHost`, `StackHost`,
// `FacePileHost`, `CountdownHost`, `ReleasePanelHost`: `Entities\Album.Page.cs`, `Album.UI.cs`); each host builds ONE props record per render from what
// it decided and mounts the key. The structure's view draws it.
//
// EQUALITY. A record that carries a list a host mutates in place, or whose members are rebuilt per render, must re-run its view whenever its host
// re-rendered (the host is the gate: a stamp that did not move never re-renders it), so those records compare by reference like the frame's. A
// record that is plain data (the countdown's, the unresolved page's) keeps the compiler's value equality: an equal one re-renders nothing.
//
// Every member is read-only (WVTH006).

using System.Runtime.CompilerServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Contracts;

/// <summary>What one render of the billed-artist line paints: the drawn faces (WITHOUT their own click: the whole line is ONE trigger), the names, how
/// many billed artists there are (the Medium "+N"), the pile's own overflow frame, and the trigger's behaviour. <see cref="Menu"/> is true when the
/// click opens a list (the chevron shows, Down / F4 open).</summary>
public sealed record ArtistLineSpec(IReadOnlyList<Controls.Face> Faces, string Names, int Count, int Overflow, float Width, string Tip, bool Menu,
                                    Action? Click, Action<KeyEventArgs>? OnKey, Action<NodeHandle>? OnRealized);

/// <summary>The props of <see cref="ViewKeys.AlbumArtistLine"/>: the line in the width mode the host decided (its hysteresis is a decision). An album with
/// no resolvable artist has no attribution row at all (<paramref name="Line"/> null: the view draws an empty box, never a blank row of air).</summary>
public sealed record AlbumArtistLineProps(Detail.RailArtistMode Mode, ArtistLineSpec? Line)
{
    /// <inheritdoc />
    public bool Equals(AlbumArtistLineProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>One row of a person flyout: the display name, the portrait url and the verb a click runs (null: the row is inert).</summary>
public sealed record PersonFlyoutRow(string Name, string? ImageUrl, Action? Go);

/// <summary>The props of <see cref="ViewKeys.AlbumArtistFlyout"/> and <see cref="ViewKeys.PlaylistMembersFlyout"/>: the rows, in order.</summary>
public sealed record PersonFlyoutProps(IReadOnlyList<PersonFlyoutRow> Rows)
{
    /// <inheritdoc />
    public bool Equals(PersonFlyoutProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.AlbumCountdown"/>: what the host's clock decided this second.</summary>
/// <param name="Released">The instant has passed ("Out now": the ring goes inactive, the tiles leave).</param>
/// <param name="Left">The time left (zero once released).</param>
/// <param name="Bare">Only the tiles / "Out now" (an artist page's tone panels already carry a plate and an eyebrow).</param>
/// <param name="Accent">The page accent thunk (the eyebrow binds it into its Color channel: a late grading re-tints without a render).</param>
/// <param name="RingAccent">The accent the ring paints this second (a scalar: peeked untracked by the host).</param>
public sealed record AlbumCountdownProps(bool Released, TimeSpan Left, bool Bare, Func<ColorF> Accent, ColorF RingAccent);

/// <summary>One edition on the Other versions picker: its label ("Name · Year · KIND") and the verb that opens it.</summary>
public sealed record AlbumVersionChoice(string Label, Action Open);

/// <summary>The props of <see cref="ViewKeys.AlbumReleasePanel"/>: the facts the host read off the album, whole (the panel appears once, complete).</summary>
/// <param name="OuterPadding">The panel carries its own outer padding (the hero arm's body under the rows; the rail's row supplies its own).</param>
/// <param name="Released">The released-date text, or null.</param>
/// <param name="ReleasedCaption">The label of the released row ("Released" / "Releases").</param>
/// <param name="Label">The record label, or null.</param>
/// <param name="Notes">The ℗ / © lines.</param>
/// <param name="Versions">The other editions.</param>
public sealed record AlbumReleasePanelProps(bool OuterPadding, string? Released, string ReleasedCaption, string? Label,
                                            IReadOnlyList<string> Notes, IReadOnlyList<AlbumVersionChoice> Versions)
{
    /// <inheritdoc />
    public bool Equals(AlbumReleasePanelProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.AlbumTrailing"/>: the reserved region (the skeleton's shape and its key are decided ONCE at mount) and the
/// two thunks the region reads (pending, and the resolved band).</summary>
/// <param name="SkeletonKey">The region's key (the shape's hash: a later kind or billing answer must not re-key it).</param>
/// <param name="Shape">What the shimmer reserves (<see cref="Album.PageRules.SkeletonShape"/>).</param>
/// <param name="Pending">The region's pending gate (the host's one-swap latch lives behind it).</param>
/// <param name="Content">The resolved band (`Album.SectionsHost`).</param>
public sealed record AlbumTrailingProps(string SkeletonKey, Album.PageRules.TrailingShape Shape, Func<bool> Pending, Func<Element> Content);

/// <summary>One music video of the section: what its card shows and the verbs it runs. A music video is a TRACK (kind 99 keys it on its song): its
/// identity for the now-playing relation is the song's uri, its menu and its drag are the track's, and a click or the badge WATCHES it.</summary>
/// <param name="Key">The card's key (`video:&lt;member slot&gt;`).</param>
/// <param name="Uri">The song's uri ("" for a stale handle).</param>
/// <param name="Title">The video's title (the counterpart's once it landed, else the song's).</param>
/// <param name="Thumb">The video's own still.</param>
/// <param name="Meta">The hero arm's subtitle ("Music video · 3:12").</param>
/// <param name="Duration">The shelf card's caption, or null.</param>
/// <param name="Watch">Request the video surface, then play (the order is the contract).</param>
/// <param name="Drag">The track payload factory a drag carries.</param>
/// <param name="Menu">The track menu, built at open.</param>
public sealed record AlbumVideoCard(string Key, string Uri, string Title, string? Thumb, string Meta, string? Duration,
                                    Action Watch, Func<object?> Drag, Func<ContextMenuModel?> Menu);

/// <summary>One media-surface row or card of the trailing band: related albums, the playlists an album appears on, and the fans' circular artist cards.
/// The surface's subtitle is a plain string the view paints.</summary>
public sealed record TrailCardData(string Key, string Uri, string Title, string? Subtitle, string? CoverUrl, Action OnClick, Action? OnPlay,
                                   bool Circular, Func<object?>? Drag, Func<ContextMenuModel?>? Menu);

/// <summary>What a trailing list row is.</summary>
public enum TrailRowKind : byte
{
    /// <summary>The row's slot is gone: a blank row of the plated row's height.</summary>
    Missing,

    /// <summary>A related album or a playlist on the shared media surface (<see cref="TrailRowData.Card"/>).</summary>
    Card,

    /// <summary>A merch listing (<see cref="TrailRowData.Merch"/>): not a media row, its call to action is the price.</summary>
    Merch,
}

/// <summary>A merch listing: nothing to play, its call to action is the PRICE, trailing in accent ink ("Buy" when the wire gave none). With no shop url
/// (<see cref="Open"/> null) it is a listing, not a dead button.</summary>
public sealed record MerchRowData(string Key, string Name, string Price, string? ImageUrl, Action? Open);

/// <summary>One row of a trailing list section.</summary>
public sealed record TrailRowData(TrailRowKind Kind, TrailCardData? Card = null, MerchRowData? Merch = null);

/// <summary>The props of <see cref="ViewKeys.AlbumAside"/> (contract 1.21): the Huge aside's sections, each null or empty when absent. Compares by reference
/// (the host is the gate, as <see cref="AlbumSectionsProps"/>).</summary>
public sealed record AlbumAsideProps
{
    /// <summary>About this release (the `Album.ReleasePanel` host, without outer padding); null before the album row exists.</summary>
    public Element? Release { get; init; }

    /// <summary>The lead artist the card is for (null: no card).</summary>
    public Artist? About { get; init; }

    /// <summary>Fans also like's circular artist cards (absent when empty).</summary>
    public required IReadOnlyList<TrailCardData> Fans { get; init; }

    /// <summary>The "More by" list section (a keyed `Album.StackHost`), or null.</summary>
    public Element? MoreBy { get; init; }

    /// <inheritdoc />
    public bool Equals(AlbumAsideProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.AlbumSections"/>: the sections the host decided are present, each with what it paints. Their order is fixed
/// (videos, About the artist, Fans also like, then the lists).</summary>
public sealed record AlbumSectionsProps
{
    /// <summary>At least one section is present (false: the region eases to zero).</summary>
    public required bool Any { get; init; }

    /// <summary>One entry per video-bearing row, in track order (the section is absent when empty): ONE is the hero card, two or more the shelf.</summary>
    public required IReadOnlyList<AlbumVideoCard> Videos { get; init; }

    /// <summary>A track carries a custom video (the hero's eyebrow says so).</summary>
    public bool CustomVideo { get; init; }

    /// <summary>The overlay the hero card's context menu attaches to (a shelf card's surface finds its own); null: no menu.</summary>
    public IOverlayService? Overlay { get; init; }

    /// <summary>The lead artist the About card is for (null: no card). The card itself is the shared `Controls.ArtistAboutCard`.</summary>
    public Artist? About { get; init; }

    /// <summary>The circular artist cards of Fans also like (the section is absent when empty).</summary>
    public required IReadOnlyList<TrailCardData> Fans { get; init; }

    /// <summary>The list sections, in order: each a keyed `Album.StackHost` (it owns its expand state and its stamp).</summary>
    public required IReadOnlyList<Element> Lists { get; init; }

    /// <inheritdoc />
    public bool Equals(AlbumSectionsProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.AlbumTrailStack"/>: a capped vertical stack under a section header.</summary>
/// <param name="Title">The section's title.</param>
/// <param name="Count">How many rows the relation has.</param>
/// <param name="Shown">How many are drawn (the cap, until the one-way "Show all" lengthened it in place).</param>
/// <param name="Expand">The one-way "Show all N".</param>
/// <param name="Rows">The drawn rows.</param>
public sealed record AlbumTrailStackProps(string Title, int Count, int Shown, Action Expand, IReadOnlyList<TrailRowData> Rows)
{
    /// <inheritdoc />
    public bool Equals(AlbumTrailStackProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.AlbumUnresolved"/>: none.</summary>
public sealed record AlbumUnresolvedProps
{
    /// <summary>The one instance (the props freeze at mount).</summary>
    public static readonly AlbumUnresolvedProps Instance = new();
}

/// <summary>The props of <see cref="ViewKeys.DetailNotice"/>: the notice, whether a minified album states its verdict, and the one way OUT (never a retry).</summary>
/// <param name="Notice">The verdict about the page.</param>
/// <param name="ShowMinifiedAlbum">False on the full album page, which self-heals a thin tracklist; the minified verdict carries no action.</param>
/// <param name="GoLibrary">Where the one action goes, or null.</param>
public sealed record DetailNoticeProps(DetailNotice Notice, bool ShowMinifiedAlbum, Action? GoLibrary);

/// <summary>The props of <see cref="ViewKeys.DetailVerticalTrailing"/>: the pieces the page's slots built, null for an absent one.</summary>
/// <param name="PreRelease">The prerelease card (it moves BELOW the rows because the hero system has no rail).</param>
/// <param name="Release">About this release, with its own outer padding.</param>
/// <param name="Trailing">The album trailing band.</param>
public sealed record DetailVerticalTrailingProps(Element? PreRelease, Element? Release, Element? Trailing)
{
    /// <inheritdoc />
    public bool Equals(DetailVerticalTrailingProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}
