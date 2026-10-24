// ── Theming/Policies/TierRegions.cs — the clip-region API of the tier layout records (themes plan 3.3 rule 2) ─────────────
//
// A RESIZED FRAME KEEPS THE OLD TIER. The window just changed size, the old tier's layout is laid out at the new size and
// presented for one refresh, and the new tier crosses on the next frame (animated). A region the old tier cannot fit at the
// new size is therefore CLIPPED for that one frame, never squeezed: it keeps a declared minimum size, and an ancestor with
// `ClipToBounds` hides what overflows. A tier layout record (a structure's per-tier record of columns, pane widths, the
// player bar's slot set, ...) declares its regions through `ITierLayoutRecord`, builds each region's host with
// `TierRegion.Host`, and puts them in a `TierClip.Ancestor`. The tests read the same declaration back: a shrinking resized
// frame may clip exactly the declared regions and no others.

using System.Collections.Immutable;
using FluentGpu.Dsl;
using Wavee.Theming.Contracts;

namespace Wavee.Theming;

/// <summary>One region of a tier layout record that may be clipped, never squeezed, on a resized frame.</summary>
/// <param name="Id">The region's id, unique in its record (<c>nav.pane</c>).</param>
/// <param name="MinWidth">The least width the region keeps (DIP).</param>
/// <param name="MinHeight">The least height the region keeps (DIP); 0 for a region that only has a width floor.</param>
public readonly record struct TierRegion(string Id, float MinWidth, float MinHeight = 0f)
{
    /// <summary>The prefix of the node key a region's host carries (<c>tier.region:nav.pane</c>), so a test finds it.</summary>
    public const string KeyPrefix = "tier.region:";

    /// <summary>The key of this region's host node.</summary>
    public string NodeKey => KeyPrefix + Id;

    /// <summary>The region's host: it shrinks with the flex pass but never below its declared minimum, so what the old tier
    /// cannot fit overflows into the <see cref="TierClip.Ancestor"/> instead of being squeezed into unreadable widths.</summary>
    public BoxEl Host(Element child) => new()
    {
        Key = NodeKey,
        Direction = 1,
        Shrink = 1f,
        MinWidth = MinWidth,
        MinHeight = MinHeight,
        Children = [child],
    };

    /// <summary>Would the region be clipped in a box <paramref name="availableWidth"/> by <paramref name="availableHeight"/>?
    /// True when either size is below the region's declared minimum.</summary>
    public bool ClipsIn(float availableWidth, float availableHeight)
        => availableWidth < MinWidth || availableHeight < MinHeight;
}

/// <summary>A tier layout record: what a structure lays out in one <see cref="WidthTier"/>. The record's sizes are its own
/// (columns, pane widths, max content width); this is the part core reads for the resize contract.</summary>
public interface ITierLayoutRecord
{
    /// <summary>The tier the record lays out.</summary>
    WidthTier Tier { get; }

    /// <summary>The regions the record may clip on a resized frame, each with its minimum size. Every other region of the
    /// record fits at every size its tier can be laid out at.</summary>
    ImmutableArray<TierRegion> Regions { get; }
}

/// <summary>Builders and checks for the clip contract.</summary>
public static class TierClip
{
    /// <summary>The clipping ancestor of a record's regions: it takes the room its parent gives (it never asks for more) and
    /// clips whatever its regions overflow.</summary>
    /// <param name="row">True to lay the regions out in a row, false in a column.</param>
    /// <param name="regions">The regions' hosts (<see cref="TierRegion.Host"/>) and any region without a clip floor.</param>
    public static BoxEl Ancestor(bool row, params Element[] regions) => new()
    {
        Direction = row ? (byte)0 : (byte)1,
        Grow = 1f,
        Shrink = 1f,
        MinWidth = 0f,
        MinHeight = 0f,
        ClipToBounds = true,
        Children = regions,
    };

    /// <summary>The ids of the regions of <paramref name="record"/> that a box of <paramref name="width"/> by
    /// <paramref name="height"/> cannot fit (they are clipped there). Empty when everything fits.</summary>
    public static IReadOnlyList<string> ClippedAt(ITierLayoutRecord record, float width, float height)
    {
        ArgumentNullException.ThrowIfNull(record);
        var clipped = new List<string>();
        if (record.Regions.IsDefaultOrEmpty) return clipped;
        // The regions share the box in declaration order: the narrowest box that fits them all is the sum of their minimum
        // widths, the shortest is the tallest minimum. A box smaller than that clips the regions at the end of the order, which
        // is where the flex pass overflows.
        float remaining = width;
        foreach (TierRegion region in record.Regions)
        {
            if (region.ClipsIn(remaining, height)) clipped.Add(region.Id);
            remaining = MathF.Max(0f, remaining - region.MinWidth);
        }
        return clipped;
    }

    /// <summary>Everything wrong with <paramref name="record"/>'s declaration: an empty or repeated id, a negative or
    /// non-finite minimum.</summary>
    public static IReadOnlyList<string> Validate(ITierLayoutRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var problems = new List<string>();
        if (record.Regions.IsDefaultOrEmpty) return problems;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TierRegion region in record.Regions)
        {
            if (string.IsNullOrWhiteSpace(region.Id)) problems.Add("a region has an empty id");
            else if (!seen.Add(region.Id)) problems.Add($"region '{region.Id}' is declared more than once");
            if (!float.IsFinite(region.MinWidth) || region.MinWidth < 0f || !float.IsFinite(region.MinHeight) || region.MinHeight < 0f)
                problems.Add($"region '{region.Id}' has a negative or non-finite minimum size");
        }
        return problems;
    }
}
