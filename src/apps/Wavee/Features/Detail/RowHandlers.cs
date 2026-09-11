using System;
using System.Collections.Generic;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee;

/// <summary>Per-list callbacks a bound track row template invokes, resolved once from the owning
/// <c>TrackList</c> (Operation ultra-fast GPU engine, P5 slice 2 —
/// <c>docs/plans/wavee/operation-ultra-fast-app-progress.md</c>; mirrors <c>DetailHandlers</c>'s shape, see
/// <c>Features/Detail/DetailTracks.cs</c>). Every delegate takes the live <see cref="RowPresentation"/> (or the
/// plain int the caller already keys play-by) rather than a captured track — <c>BoundItemScope&lt;T&gt;.Invoke</c>
/// (<c>FluentGpu.Controls/BoundItemScope.cs</c>) resolves <c>Item.Peek()</c> at INVOCATION time, so these never
/// need to know whether the slot they were bound to still points at the same row.
/// <para>Split into its OWN file (Wavee.Core + BCL only — no FluentGpu, no <c>TrackList</c>/<c>TrackRowTemplate</c>)
/// next to <see cref="RowPresentation"/>, exactly for the reason slice 1 split <c>RowPresentation.cs</c> out of
/// <c>TrackList</c>: so <c>Wavee.Tests</c> can source-include it directly and drive the real type.</para></summary>
internal readonly record struct RowHandlers(
    /// <summary>Single-click play/toggle-pause for the row at this display index (mirrors <c>TrackList.PlayRow</c>).</summary>
    Action<int> Play,
    /// <summary>Toggle the saved/liked state of this row's track.</summary>
    Action<RowPresentation> ToggleLike,
    /// <summary>Navigate — artist/album links, metadata spans (slice 3).</summary>
    Action<string, string?> Go,
    /// <summary>Open/close this row's expand drawer (slice 3's chevron cell).</summary>
    Action<RowPresentation> ToggleExpanded,
    /// <summary>Open the row context menu, anchored at the invoking cell (video/more lane, slice 3).</summary>
    Action<RowPresentation> RequestContext,
    /// <summary>Retry a failed/offline metadata fetch (the title cell's retry line).</summary>
    Action RetryMetadata);

/// <summary>Pure per-row glyph selection the number cell's chart lane reads (Operation ultra-fast P5 slice 2):
/// split out so the SAME decision <see cref="TrackRowTemplate"/>'s bound cell renders from is directly testable
/// without a FluentGpu element tree. Mirrors <c>TrackRow.ChartGlyph</c>'s three-way rank-movement mapping
/// (<c>Components/TrackRow.cs</c>) — Equal/Unknown states render nothing, never a guessed arrow.</summary>
internal static class TrackRowGlyphs
{
    internal static string ChartText(ChartEntry? chart) => chart?.Status switch
    {
        ChartEntryStatus.Up => "▲",
        ChartEntryStatus.Down => "▼",
        ChartEntryStatus.New => "NEW",
        _ => "",
    };
}

/// <summary>What ONE span of the bound track row's metadata/artist link runs refers to (Operation ultra-fast P5
/// slice 3): <see cref="Separator"/> spans are never hyperlinks (no <c>IsLink</c>) and never reach a click handler,
/// so their <see cref="MetaSpanSlot.Uri"/>/<see cref="MetaSpanSlot.Name"/> are unused placeholders.</summary>
internal enum MetaSpanKind
{
    Separator, Artist, Album,
    /// <summary>Plain, unlinked text that is not a separator — "feat. ", a plays label.</summary>
    Label,
    /// <summary>An icon-font glyph inline in the run (the artist chart's video mark). <see cref="MetaSpanSlot.Text"/>
    /// is the glyph string; the span builder sets the icon font family on it.</summary>
    Glyph,
}

/// <summary>One slot in a span-index-ordered layout — the plain-text separator or the ref an artist/album link
/// resolves to. <see cref="RowSpanLayout"/> is the ONE place this order is decided; both the span-BUILDING code
/// (<c>TrackRowTemplate.Meta</c>/<c>ArtistCell</c>) and the click-index-to-route RESOLVER read the same list, so
/// they can never disagree about what clicking span N means.</summary>
internal readonly record struct MetaSpanSlot(MetaSpanKind Kind, string Text, string Uri, string Name);

/// <summary>Pure, engine-free span-index layouts for the bound track row's two link runs — the artist-only lane
/// (<c>ColumnSet.Artist</c>, Classic's dedicated column) and the combined artist+album metadata subline (the Modern
/// title cell's second line). Mirrors <c>TrackRow.ArtistLinks</c>/<c>TrackRow.MetadataLine</c>'s span order exactly
/// (<c>Components/TrackRow.cs</c>) — same separators, same "album only when named", same episode-always-shows-show
/// rule — so the bound cell renders IDENTICALLY to the eager one it replaces.</summary>
internal static class RowSpanLayout
{
    /// <summary>Artist-only link run: name, ", ", name, ", ", name — index <c>i</c> is an <see cref="MetaSpanKind.Artist"/>
    /// slot at every even position, a plain <see cref="MetaSpanKind.Separator"/> at every odd one.</summary>
    internal static IReadOnlyList<MetaSpanSlot> Artists(IReadOnlyList<ArtistRef> artists)
    {
        if (artists.Count == 0) return Array.Empty<MetaSpanSlot>();
        var slots = new List<MetaSpanSlot>(artists.Count * 2 - 1);
        for (int i = 0; i < artists.Count; i++)
        {
            if (i > 0) slots.Add(new MetaSpanSlot(MetaSpanKind.Separator, ", ", "", ""));
            var a = artists[i];
            slots.Add(new MetaSpanSlot(MetaSpanKind.Artist, a.Name, a.Uri, a.Name));
        }
        return slots;
    }

    /// <summary>The metadata subline's combined run: the artist links (when <see cref="RowPresentation.ShowTrackArtist"/>)
    /// followed by " · " + the album/show link (when <paramref name="showAlbumInMeta"/> — a STATIC per-shape value,
    /// <c>!set.Classic &amp;&amp; !set.Album</c> — is true for this row, OR the track is a podcast episode, per
    /// <c>TrackRow.MetadataLine</c>'s own episode-always rule) AND the album has a name.</summary>
    internal static IReadOnlyList<MetaSpanSlot> Metadata(RowPresentation p, bool showAlbumInMeta)
    {
        bool episode = EntityUri.KindOf(p.Track.Uri) == EntityKind.Episode;
        var slots = new List<MetaSpanSlot>(p.Track.Artists.Count * 2 + 2);
        if (p.ShowTrackArtist) slots.AddRange(Artists(p.Track.Artists));
        if ((showAlbumInMeta || episode) && p.Track.Album.Name.Length > 0)
        {
            if (slots.Count > 0) slots.Add(new MetaSpanSlot(MetaSpanKind.Separator, " · ", "", ""));
            var album = p.Track.Album;
            slots.Add(new MetaSpanSlot(MetaSpanKind.Album, album.Name, album.Uri, album.Name));
        }
        return slots;
    }

    /// <summary>The artist chart row's subtitle run, in PRIORITY order (the row is one line with a tail ellipsis, so
    /// what comes first survives a narrow cell): the featured credits — "feat. A, B", every name a link, the page
    /// artist itself left out, and only when the page artist IS credited (a chart row that does not credit the page
    /// artist at all has no meaningful "feat.") — then " · " + the video glyph, then " · " + the plays label.
    /// The album is deliberately not part of this row (an artist's Popular list is not an album context; for a
    /// single it would repeat the title). <paramref name="playsLabel"/> null = no plays slot (a pending count).
    /// A run with nothing to say is empty — never a leading separator.</summary>
    internal static IReadOnlyList<MetaSpanSlot> ChartSubtitle(Track t, string pageArtistUri, bool hasVideo,
                                                              string? playsLabel, string featLabel, string videoGlyph)
    {
        var slots = new List<MetaSpanSlot>(t.Artists.Count * 2 + 4);
        bool pageInCredits = false;
        for (int i = 0; i < t.Artists.Count && !pageInCredits; i++)
            pageInCredits = pageArtistUri.Length > 0
                && string.Equals(t.Artists[i].Uri, pageArtistUri, StringComparison.OrdinalIgnoreCase);
        if (pageInCredits)
        {
            bool first = true;
            for (int i = 0; i < t.Artists.Count; i++)
            {
                var a = t.Artists[i];
                if (string.Equals(a.Uri, pageArtistUri, StringComparison.OrdinalIgnoreCase)) continue;
                slots.Add(first
                    ? new MetaSpanSlot(MetaSpanKind.Label, featLabel + " ", "", "")
                    : new MetaSpanSlot(MetaSpanKind.Separator, ", ", "", ""));
                slots.Add(new MetaSpanSlot(MetaSpanKind.Artist, a.Name, a.Uri, a.Name));
                first = false;
            }
        }
        if (hasVideo)
        {
            Dot(slots);
            slots.Add(new MetaSpanSlot(MetaSpanKind.Glyph, videoGlyph, "", ""));
        }
        if (playsLabel is not null)
        {
            Dot(slots);
            slots.Add(new MetaSpanSlot(MetaSpanKind.Label, playsLabel, "", ""));
        }
        return slots;

        static void Dot(List<MetaSpanSlot> s)
        {
            if (s.Count > 0) s.Add(new MetaSpanSlot(MetaSpanKind.Separator, " · ", "", ""));
        }
    }
}
