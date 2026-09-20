using System;
using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class ContentFilterTagsTests
{
    static Track T(string id, params string[] tags) => new(
        id, "spotify:track:" + id, id,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""),
        180_000, false, null,
        Tags: tags.Length == 0 ? null : tags);

    static IReadOnlyList<Track> Many(int n, params string[] tags)
    {
        var list = new List<Track>(n);
        for (int i = 0; i < n; i++) list.Add(T("t" + i, tags));
        return list;
    }

    [Fact]
    public void NoTags_YieldsNoChips()
    {
        Assert.Empty(ContentFilterTags.Derive(Many(10)));
        Assert.Empty(ContentFilterTags.Derive(Array.Empty<Track>()));
    }

    [Fact]
    public void RareTag_DoesNotEarnAChip()
    {
        // Two carriers is below the floor: a chip nobody can usefully tap is worse than no chip.
        var tracks = new List<Track> { T("a", "K-Pop"), T("b", "K-Pop") };
        Assert.Empty(ContentFilterTags.Derive(tracks));
    }

    [Fact]
    public void CommonTag_EarnsAChip_AtTheFloor()
    {
        Assert.Equal(["K-Pop"], ContentFilterTags.Derive(Many(3, "K-Pop")));
    }

    [Fact]
    public void ChipsAreOrderedByCarrierCountDescending()
    {
        var tracks = new List<Track>();
        for (int i = 0; i < 3; i++) tracks.Add(T("rare" + i, "Chill"));
        for (int i = 0; i < 9; i++) tracks.Add(T("common" + i, "Pop"));
        for (int i = 0; i < 5; i++) tracks.Add(T("mid" + i, "Dance"));

        Assert.Equal(["Pop", "Dance", "Chill"], ContentFilterTags.Derive(tracks));
    }

    [Fact]
    public void CasingVariantsCollapseToOneChip()
    {
        // display_name is absent on some descriptors, so the lowercase wire token arrives instead — same concept.
        var tracks = new List<Track> { T("a", "K-Pop"), T("b", "k-pop"), T("c", "K-POP") };
        Assert.Single(ContentFilterTags.Derive(tracks));
    }

    [Fact]
    public void ChipCountIsCapped()
    {
        var tracks = new List<Track>();
        for (int tag = 0; tag < 30; tag++)
            for (int i = 0; i < 3 + tag; i++)   // distinct counts so the ordering is total, not tie-broken
                tracks.Add(T($"t{tag}_{i}", "tag" + tag));

        var chips = ContentFilterTags.Derive(tracks);
        Assert.Equal(10, chips.Count);
        Assert.Equal("tag29", chips[0]);   // the most-carried tag leads
    }

    [Fact]
    public void OrderIsStableAcrossEqualCounts()
    {
        var tracks = new List<Track>();
        foreach (var tag in new[] { "Zeta", "Alpha", "Mid" })
            for (int i = 0; i < 4; i++) tracks.Add(T(tag + i, tag));

        // Equal counts fall back to name order, so an enrichment pass cannot visibly shuffle the bar.
        Assert.Equal(["Alpha", "Mid", "Zeta"], ContentFilterTags.Derive(tracks));
    }

    // ── DeriveCounted: the same answer with the numbers kept ────────────────────────────────────────────────────────

    /// <summary>Derive is now a projection of DeriveCounted, so the two can never disagree about which chips exist, in
    /// what order, or where the cap falls. Pinned as a PARITY property over several shapes rather than as one example,
    /// because the risk is a divergence introduced later, not today's output.</summary>
    [Fact]
    public void DeriveIsExactlyDeriveCountedWithoutTheNumbers()
    {
        var shapes = new List<IReadOnlyList<Track>>
        {
            Array.Empty<Track>(),
            Many(10),                                                  // no tags at all
            new List<Track> { T("a", "K-Pop"), T("b", "K-Pop") },       // below the floor
            Many(3, "K-Pop"),                                          // exactly at the floor
            Many(4, "Pop", "Chill"),                                   // multi-tag rows
        };

        var mixed = new List<Track>();
        for (int i = 0; i < 3; i++) mixed.Add(T("rare" + i, "Chill"));
        for (int i = 0; i < 9; i++) mixed.Add(T("common" + i, "Pop"));
        for (int i = 0; i < 5; i++) mixed.Add(T("mid" + i, "Dance"));
        shapes.Add(mixed);

        var capped = new List<Track>();
        for (int tag = 0; tag < 30; tag++)
            for (int i = 0; i < 3 + tag; i++) capped.Add(T($"t{tag}_{i}", "tag" + tag));
        shapes.Add(capped);

        foreach (var tracks in shapes)
        {
            var chips = ContentFilterTags.Derive(tracks);
            var counted = ContentFilterTags.DeriveCounted(tracks);

            Assert.Equal(chips.Count, counted.Count);
            for (int i = 0; i < chips.Count; i++) Assert.Equal(chips[i], counted[i].Title);
        }
    }

    /// <summary>The counts are the real carrier counts — the numbers the old path computed and threw away.</summary>
    [Fact]
    public void DeriveCountedReportsTheCarrierCounts()
    {
        var tracks = new List<Track>();
        for (int i = 0; i < 3; i++) tracks.Add(T("rare" + i, "Chill"));
        for (int i = 0; i < 9; i++) tracks.Add(T("common" + i, "Pop"));
        for (int i = 0; i < 5; i++) tracks.Add(T("mid" + i, "Dance"));

        var counted = ContentFilterTags.DeriveCounted(tracks);
        Assert.Equal([("Pop", 9), ("Dance", 5), ("Chill", 3)], Pairs(counted));
    }

    /// <summary>Casing variants collapse into one chip, and their carriers are SUMMED rather than split — a legend
    /// that reported 1+1+1 for one concept would be worse than no legend.</summary>
    [Fact]
    public void CountsSumAcrossCasingVariants()
    {
        var tracks = new List<Track> { T("a", "K-Pop"), T("b", "k-pop"), T("c", "K-POP") };
        var counted = ContentFilterTags.DeriveCounted(tracks);

        Assert.Single(counted);
        Assert.Equal(3, counted[0].Count);
    }

    /// <summary>The floor is public because the Liked facts blend bar gates on the same number; if it moved, both
    /// surfaces would have to move together.</summary>
    [Fact]
    public void TheEvidenceFloorIsThree() => Assert.Equal(3, ContentFilterTags.MinTrackCount);

    static (string Title, int Count)[] Pairs(IReadOnlyList<TagCount> counted)
    {
        var pairs = new (string, int)[counted.Count];
        for (int i = 0; i < counted.Count; i++) pairs[i] = (counted[i].Title, counted[i].Count);
        return pairs;
    }
}
