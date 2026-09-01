using System;
using System.Collections.Generic;

namespace Wavee.Core.ReleaseNotes;

/// <summary>Which highlights a given install actually shows. One kind is channel-aware: a <c>"store"</c> highlight
/// ("Wavee is now on the Microsoft Store", with its get-it button) is an announcement FOR the feed channels — a
/// Store-installed build is already there, and showing it a card whose button opens the listing it came from reads
/// as a bug. Every other kind renders everywhere; an unknown kind stays visible (forward-compat: an old build
/// title-cases it into a generic pill rather than dropping the card).
/// <para>Pure by design — the page, the after-update dialog and the tests all ask the same class, so "hidden on the
/// Store" cannot drift between the two surfaces.</para></summary>
public static class HighlightVisibility
{
    /// <summary>The channel-aware highlight kind (compared case-insensitively, like every kind on the wire).</summary>
    public const string StoreKind = "store";

    /// <summary>Is this the Store-announcement kind — the one that gets the special card treatment (accent chrome +
    /// the "Get it from the Microsoft Store" button) instead of the generic kind pill?</summary>
    public static bool IsStore(ReleaseHighlight? highlight)
        => highlight is not null && string.Equals(highlight.Kind, StoreKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>Does this highlight render on this install? Everything renders everywhere, except a
    /// <see cref="StoreKind"/> highlight on a Store-channel install.</summary>
    public static bool IsVisible(ReleaseHighlight? highlight, bool isStoreInstall)
        => highlight is not null && !(isStoreInstall && IsStore(highlight));

    /// <summary>The first <paramref name="max"/> VISIBLE highlights, in document order. The cap counts what actually
    /// renders: a hidden store card frees its slot for the next highlight rather than shipping a two-card strip with
    /// an invisible third.</summary>
    public static List<ReleaseHighlight> SelectVisible(IReadOnlyList<ReleaseHighlight?>? highlights,
                                                       bool isStoreInstall, int max)
    {
        var visible = new List<ReleaseHighlight>(max > 0 ? Math.Min(max, highlights?.Count ?? 0) : 0);
        if (highlights is null || max <= 0) return visible;
        foreach (var h in highlights)
        {
            if (visible.Count >= max) break;
            if (IsVisible(h, isStoreInstall)) visible.Add(h!);
        }
        return visible;
    }
}
