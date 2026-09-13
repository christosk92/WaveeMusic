using System.Collections.Generic;

namespace Wavee.Core;

/// <summary>Which shelf treatment a browse section wants. Mirrors the server's <c>data.__typename</c> — the ONLY
/// signal distinguishing "a row of cards" from "a grid of further categories", so it is carried rather than inferred
/// from the item types (a generic section can legitimately contain a single item).</summary>
public enum BrowseSectionKind
{
    /// <summary>A horizontal shelf of entity cards (playlists, albums, episodes, shows).</summary>
    Shelf,
    /// <summary>A grid of further browse categories — Browse is a TREE, and this is the branch node.</summary>
    CategoryGrid,
    /// <summary>The trailing "related categories" block. Same content as a grid, different placement/emphasis.</summary>
    Related,
}

/// <summary>One browse category tile from <c>browseAll</c>. <paramref name="IsClientFeature"/> marks a
/// <c>BrowseClientFeature</c> such as Live Events, which is NOT a browse page: it carries a <c>featureUri</c>
/// (<c>spotify:concerts</c>) and routes into the client's own surface instead.</summary>
public sealed record BrowseCategory(
    string Uri,
    string Title,
    uint? Color,
    Image? Artwork = null,
    bool IsClientFeature = false);

/// <summary>One card inside a browse shelf. Deliberately a flat projection rather than a union of domain types: a
/// browse shelf mixes playlists, albums, episodes and shows freely, and every one renders as the same card.</summary>
public sealed record BrowseCard(
    string Uri,
    string Title,
    string? Subtitle,
    Image? Image,
    uint? Accent = null);

/// <summary>One section of a browse page. <paramref name="Total"/> is the server's item count, which is frequently far
/// larger than <paramref name="Cards"/> (a section returned 10 of 1000) — that gap is what a "Show all" affordance
/// pages through via <c>browseSection</c>, an axis INDEPENDENT of the page's own section paging.
/// <para><paramref name="NextOffset"/> is the server's own <c>sectionItems.pagingInfo.nextOffset</c> cursor for THIS
/// axis, in one of three states: a non-negative value is the offset to request next; <see cref="PagingComplete"/>
/// (a sentinel, not a real offset) marks that <c>pagingInfo</c> was present and <c>nextOffset</c> was EXPLICITLY
/// <c>null</c> — the section is done, full stop, even if <see cref="Total"/> still claims more (the same
/// total-vs-cursor disagreement <c>HomeSectionPaging.cs</c> documents for Home's own sections: measured, a complete
/// section can answer a total that overshoots what it actually holds); plain <c>null</c> means no <c>pagingInfo</c>
/// came back at all, so callers fall back to the synthesized <c>HomeSectionPaging.BrowseNextOffset</c> cursor instead
/// of trusting an absent field as "done". Use <c>HomeSectionPaging.BrowseSectionNextOffset</c> rather than reading
/// this field directly — it resolves all three states.</para></summary>
public sealed record BrowseSection(
    string Uri,
    string? Title,
    BrowseSectionKind Kind,
    IReadOnlyList<BrowseCard> Cards,
    IReadOnlyList<BrowseCategory> Categories,
    int Total,
    int? NextOffset = null)
{
    /// <summary>Sentinel for <see cref="NextOffset"/>: the server explicitly terminated this section's paging (see the
    /// record's own doc comment). Never a legal offset value — offsets are always non-negative.</summary>
    public const int PagingComplete = -1;
}

/// <summary>A rendered browse page: the header (title + the server's own accent, which is often absent) and its
/// sections. <paramref name="NextSectionOffset"/> is the page-level paging cursor — null when every section has been
/// returned. Deliberately un-wired by the UI: captures show browse pages return all sections at offset 0, so there
/// is nothing to page here in practice. The per-SECTION cursor (<see cref="BrowsePageModel.WithSectionCardsAppended"/>
/// + <c>IBrowseService.GetSectionAsync</c>) is the axis the UI actually pages.</summary>
public sealed record BrowsePageModel(
    string Uri,
    string? Title,
    uint? Accent,
    IReadOnlyList<BrowseSection> Sections,
    int TotalSections,
    int? NextSectionOffset)
{
    /// <summary>Append a further page of a SECTION's items (the browseSection cursor), returning a new model with just
    /// that section replaced. Cards already present are skipped by uri: the server can overlap pages, and a duplicate
    /// card would both look wrong and break the keyed reconciler's identity for that shelf.</summary>
    public BrowsePageModel WithSectionCardsAppended(string sectionUri, IReadOnlyList<BrowseCard> more)
    {
        if (more.Count == 0) return this;
        var sections = new BrowseSection[Sections.Count];
        bool hit = false;
        for (int i = 0; i < Sections.Count; i++)
        {
            var s = Sections[i];
            if (!hit && string.Equals(s.Uri, sectionUri, StringComparison.Ordinal))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in s.Cards) seen.Add(c.Uri);
                var merged = new List<BrowseCard>(s.Cards.Count + more.Count);
                merged.AddRange(s.Cards);
                foreach (var c in more) if (seen.Add(c.Uri)) merged.Add(c);
                sections[i] = s with { Cards = merged };
                hit = true;
            }
            else sections[i] = s;
        }
        return hit ? this with { Sections = sections } : this;
    }

    /// <summary>A page that resolved but carries nothing. Spotify genuinely returns HTTP 200 with a body containing
    /// only <c>__typename</c> — no header, no sections — so this is a NORMAL state, not an error.</summary>
    public bool IsEmpty => Sections.Count == 0 && string.IsNullOrEmpty(Title);
}
