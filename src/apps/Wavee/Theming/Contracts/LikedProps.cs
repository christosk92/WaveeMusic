// ── Theming/Contracts/LikedProps.cs — the props of the Liked Songs page's views (themes plan 6.0, T9c) ────────────────────────────
//
// The core hosts read the data (the liked edge, the track rows, the persisted style, the table's filter) and hand the views plain
// values: the effective style, the newest-first distinct tile urls and their content key, the count, the selected chip, the verbs.
// A cover record compares by the scalars and `TileKey`, never by the tile ARRAY (an array compares by reference, so default record
// equality would report "changed" on every recompute and defeat the gate).

using FluentGpu.Dsl;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.LikedCover"/>: one cover at one size.</summary>
public sealed record LikedCoverProps
{
    /// <summary>The edge of the square (geometry freezes at mount: the host keys on it).</summary>
    public required float Size { get; init; }

    /// <summary>The corner radius of the square.</summary>
    public required float Radius { get; init; }

    /// <summary>The shared-element id the cover claims (a Home card to rail fly keeps a participant on both ends); null in a miniature.</summary>
    public string? MorphKey { get; init; }

    /// <summary>The EFFECTIVE style (the persisted choice after the library's floor, <c>LikedCoverRules.Effective</c>).</summary>
    public required LikedCoverStyle Style { get; init; }

    /// <summary>The newest-first distinct liked cover urls (at most <c>LikedCoverRules.MaxTiles</c>).</summary>
    public required string[] Tiles { get; init; }

    /// <summary>The tiles' content key (unit-separator joined): what the equality compares.</summary>
    public required string TileKey { get; init; }

    /// <summary>The collection size Tone states; 0 until the liked edge is Complete (a partial count on the cover is a lie).</summary>
    public required int TrackCount { get; init; }

    /// <inheritdoc />
    public bool Equals(LikedCoverProps? other)
        => other is not null && Size.Equals(other.Size) && Radius.Equals(other.Radius) && Style == other.Style
           && TrackCount == other.TrackCount && string.Equals(MorphKey, other.MorphKey, StringComparison.Ordinal)
           && string.Equals(TileKey, other.TileKey, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Size, Radius, Style, TrackCount, TileKey);
}

/// <summary>The props of <see cref="ViewKeys.LikedCoverPicker"/>: the cover with its "Cover style" pill. The component props freeze at
/// mount, like the geometry they come from (the host's key carries the size, radius and morph id).</summary>
public sealed record LikedCoverPickerProps
{
    /// <summary>The edge of the square.</summary>
    public required float Size { get; init; }

    /// <summary>The corner radius of the square.</summary>
    public required float Radius { get; init; }

    /// <summary>The shared-element id the cover claims.</summary>
    public string? MorphKey { get; init; }

    /// <summary>The cover under the pill: the core cover host's element (it owns the snapshot and the demand).</summary>
    public required Element Art { get; init; }

    /// <summary>The flyout the pill opens: the core flyout host's element, built at open (it owns the snapshot and applies the pick).</summary>
    public required Func<Element> Flyout { get; init; }

    /// <inheritdoc />
    public bool Equals(LikedCoverPickerProps? other)
        => other is not null && Size.Equals(other.Size) && Radius.Equals(other.Radius)
           && string.Equals(MorphKey, other.MorphKey, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Size, Radius, MorphKey);
}

/// <summary>The props of <see cref="ViewKeys.LikedCoverStyleFlyout"/>: the picker's body over the snapshot the core host computed.</summary>
public sealed record LikedCoverStyleFlyoutProps
{
    /// <summary>The newest-first distinct liked cover urls the miniatures compose from (read even while the persisted style is Stock).</summary>
    public required string[] Tiles { get; init; }

    /// <summary>The tiles' content key.</summary>
    public required string TileKey { get; init; }

    /// <summary>The collection size Tone states.</summary>
    public required int TrackCount { get; init; }

    /// <summary>The index in <c>LikedCoverRules.PickerOrder</c> of the PERSISTED style (a hand-edited value reads as Stock's slot).</summary>
    public required int Selected { get; init; }

    /// <summary>Persist the style at an index of <c>LikedCoverRules.PickerOrder</c>. Idempotent and cheap: the strip fires it on every
    /// keyboard rove (selection follows focus), and roving IS the preview.</summary>
    public required Action<int> Apply { get; init; }

    /// <inheritdoc />
    public bool Equals(LikedCoverStyleFlyoutProps? other)
        => other is not null && Selected == other.Selected && TrackCount == other.TrackCount
           && string.Equals(TileKey, other.TileKey, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Selected, TrackCount, TileKey);
}

/// <summary>The props of <see cref="ViewKeys.LikedChipBar"/>: the rail's chips, the selection and the verbs.</summary>
public sealed record LikedChipBarProps
{
    /// <summary>The chip titles in their final order (empty: no rail, a zero-hit placeholder).</summary>
    public required string[] Titles { get; init; }

    /// <summary>How many of the leading chips have evidence: the rest draw disabled, as one contiguous tail.</summary>
    public required int Evidenced { get; init; }

    /// <summary>The selected chip's title (the table's tag filter), or null for "All".</summary>
    public string? Selected { get; init; }

    /// <summary>The rail's scroll key (its scroll offset survives a rebuild).</summary>
    public required string ScrollKey { get; init; }

    /// <summary>The chips ride the page bar between its lead and its tools (a page with no rail, tuned Classic D8) instead of a row of their
    /// own. Appended (contract 1.9).</summary>
    public bool Inline { get; init; }

    /// <summary>"All" was clicked: clear the tag filter.</summary>
    public required Action SelectAll { get; init; }

    /// <summary>A chip was clicked: set the tag filter to the title, or clear it (null) when the clicked chip is the selected one
    /// (re-tapping the active chip clears it; the view decides, from what it drew).</summary>
    public required Action<string?> Select { get; init; }

    /// <inheritdoc />
    public bool Equals(LikedChipBarProps? other)
        => other is not null && Evidenced == other.Evidenced && Inline == other.Inline
           && string.Equals(Selected, other.Selected, StringComparison.Ordinal)
           && string.Equals(ScrollKey, other.ScrollKey, StringComparison.Ordinal) && Titles.AsSpan().SequenceEqual(other.Titles);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Titles.Length, Evidenced, Selected, ScrollKey);
}
