// ── Wavee.Tests/ContentFilterTagsTests.cs — the Liked Songs chip derivation (Entities/User.Liked.cs) ─────────────────
//
// A VERBATIM port of 0.2.9's ContentFilterTagsTests over the rule's record shape (one descriptor list per row, null =
// "not fetched"), plus the 0.3 additions: OrderByEvidence's evidence PREFIX (ch 07 §0.13 — the bar must never
// interleave live and dead chips), the older Reconcile, and one parity fact over liked HANDLES (`!Knows(Tags)` is "not
// fetched", never "no descriptors").

using System.Text;
using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ContentFilterTagsTests
{
    static IReadOnlyList<string>? T(params string[] tags) => tags.Length == 0 ? null : tags;

    static List<IReadOnlyList<string>?> Many(int n, params string[] tags)
    {
        var list = new List<IReadOnlyList<string>?>(n);
        for (int i = 0; i < n; i++) list.Add(T(tags));
        return list;
    }

    [Fact]
    public void NoTags_YieldsNoChips()
    {
        Assert.Empty(ContentFilterTags.Derive(Many(10)));
        Assert.Empty(ContentFilterTags.Derive(new List<IReadOnlyList<string>?>()));
    }

    [Fact]
    public void RareTag_DoesNotEarnAChip()
    {
        // Two carriers is below the floor: a chip nobody can usefully tap is worse than no chip.
        var tracks = new List<IReadOnlyList<string>?> { T("K-Pop"), T("K-Pop") };
        Assert.Empty(ContentFilterTags.Derive(tracks));
    }

    [Fact]
    public void CommonTag_EarnsAChip_AtTheFloor()
    {
        Assert.Equal(new[] { "K-Pop" }, ContentFilterTags.Derive(Many(3, "K-Pop")));
    }

    [Fact]
    public void ChipsAreOrderedByCarrierCountDescending()
    {
        var tracks = new List<IReadOnlyList<string>?>();
        for (int i = 0; i < 3; i++) tracks.Add(T("Chill"));
        for (int i = 0; i < 9; i++) tracks.Add(T("Pop"));
        for (int i = 0; i < 5; i++) tracks.Add(T("Dance"));

        Assert.Equal(new[] { "Pop", "Dance", "Chill" }, ContentFilterTags.Derive(tracks));
    }

    [Fact]
    public void CasingVariantsCollapseToOneChip()
    {
        // display_name is absent on some descriptors, so the lowercase wire token arrives instead — same concept.
        var tracks = new List<IReadOnlyList<string>?> { T("K-Pop"), T("k-pop"), T("K-POP") };
        Assert.Single(ContentFilterTags.Derive(tracks));
    }

    [Fact]
    public void ChipCountIsCapped()
    {
        var tracks = new List<IReadOnlyList<string>?>();
        for (int tag = 0; tag < 30; tag++)
            for (int i = 0; i < 3 + tag; i++)   // distinct counts so the ordering is total, not tie-broken
                tracks.Add(T("tag" + tag));

        var chips = ContentFilterTags.Derive(tracks);
        Assert.Equal(10, chips.Count);
        Assert.Equal("tag29", chips[0]);   // the most-carried tag leads
    }

    [Fact]
    public void OrderIsStableAcrossEqualCounts()
    {
        var tracks = new List<IReadOnlyList<string>?>();
        foreach (var tag in new[] { "Zeta", "Alpha", "Mid" })
            for (int i = 0; i < 4; i++) tracks.Add(T(tag));

        // Equal counts fall back to name order, so an enrichment pass cannot visibly shuffle the bar.
        Assert.Equal(new[] { "Alpha", "Mid", "Zeta" }, ContentFilterTags.Derive(tracks));
    }

    // ── DeriveCounted: the same answer with the numbers kept ────────────────────────────────────────────────────────

    /// <summary>Derive is a projection of DeriveCounted, so the two can never disagree about which chips exist, in what
    /// order, or where the cap falls. Pinned as a PARITY property over several shapes.</summary>
    [Fact]
    public void DeriveIsExactlyDeriveCountedWithoutTheNumbers()
    {
        var shapes = new List<List<IReadOnlyList<string>?>>
        {
            new(),
            Many(10),
            new() { T("K-Pop"), T("K-Pop") },
            Many(3, "K-Pop"),
            Many(4, "Pop", "Chill"),
        };

        var mixed = new List<IReadOnlyList<string>?>();
        for (int i = 0; i < 3; i++) mixed.Add(T("Chill"));
        for (int i = 0; i < 9; i++) mixed.Add(T("Pop"));
        for (int i = 0; i < 5; i++) mixed.Add(T("Dance"));
        shapes.Add(mixed);

        var capped = new List<IReadOnlyList<string>?>();
        for (int tag = 0; tag < 30; tag++)
            for (int i = 0; i < 3 + tag; i++) capped.Add(T("tag" + tag));
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
        var tracks = new List<IReadOnlyList<string>?>();
        for (int i = 0; i < 3; i++) tracks.Add(T("Chill"));
        for (int i = 0; i < 9; i++) tracks.Add(T("Pop"));
        for (int i = 0; i < 5; i++) tracks.Add(T("Dance"));

        var counted = ContentFilterTags.DeriveCounted(tracks);
        Assert.Equal(new[] { ("Pop", 9), ("Dance", 5), ("Chill", 3) }, Pairs(counted));
    }

    /// <summary>Casing variants collapse into one chip, and their carriers are SUMMED rather than split.</summary>
    [Fact]
    public void CountsSumAcrossCasingVariants()
    {
        var tracks = new List<IReadOnlyList<string>?> { T("K-Pop"), T("k-pop"), T("K-POP") };
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

    // ── 0.3: the curated set, ordered by evidence ───────────────────────────────────────────────────────────────────

    static readonly ContentFilterChip[] Curated =
    [
        new("Jazz", "jazz"), new("K-Pop", "k-pop"), new("Metal", "metal"), new("Chill", "chill"),
    ];

    /// <summary>Evidence is a PREFIX: the evidenced chips move to the front in server order, the dead ones follow as one
    /// tail in server order, and nothing is dropped — the bar shows the tail disabled.</summary>
    [Fact]
    public void OrderByEvidence_KeepsTheWholeSet_EvidencedFirst_AsOneContiguousPrefix()
    {
        var rows = new List<IReadOnlyList<string>?> { T("chill"), T("k-pop"), null };
        var set = ContentFilterTags.OrderByEvidence(Curated, rows);

        Assert.Equal(new[] { "K-Pop", "Chill", "Jazz", "Metal" }, set.Titles);
        Assert.Equal(2, set.EvidencedCount);
        Assert.True(set.IsEvidenced(0));
        Assert.True(set.IsEvidenced(1));
        Assert.False(set.IsEvidenced(2));
        Assert.False(set.IsEvidenced(3));
        Assert.Equal(4, set.Count);
    }

    /// <summary>A chip joins on its TOKEN or its TITLE, case-insensitively (the descriptor may arrive as either).</summary>
    [Fact]
    public void OrderByEvidence_MatchesTheTokenOrTheTitle_CaseInsensitively()
    {
        var rows = new List<IReadOnlyList<string>?> { T("METAL"), T("Jazz") };
        var set = ContentFilterTags.OrderByEvidence(Curated, rows);
        Assert.Equal(new[] { "Jazz", "Metal", "K-Pop", "Chill" }, set.Titles);
        Assert.Equal(2, set.EvidencedCount);
    }

    [Fact]
    public void OrderByEvidence_OfNoCuratedChips_IsTheEmptySet()
    {
        var set = ContentFilterTags.OrderByEvidence(Array.Empty<ContentFilterChip>(), Many(5, "Pop"));
        Assert.Equal(0, set.Count);
        Assert.Equal(0, set.EvidencedCount);
    }

    /// <summary>The OLDER rule, still exported: un-evidenced chips are DROPPED (superseded by OrderByEvidence).</summary>
    [Fact]
    public void Reconcile_DropsTheUnevidencedChips()
    {
        var rows = new List<IReadOnlyList<string>?> { T("chill"), T("k-pop") };
        Assert.Equal(new[] { "K-Pop", "Chill" }, ContentFilterTags.Reconcile(Curated, rows));
        Assert.Empty(ContentFilterTags.Reconcile(Curated, new List<IReadOnlyList<string>?>()));
    }

    // ── 0.3: the same rule over liked HANDLES ───────────────────────────────────────────────────────────────────────

    /// <summary>The span overload reads <c>Edges.TrackTags</c> only where <c>Knows(TrackFields.Tags)</c> — a row whose
    /// descriptors were never fetched is skipped exactly as 0.2.9's null list was, never counted as "no descriptors"
    /// and never allowed to contribute a stale payload.</summary>
    [Fact]
    public void OverHandles_TheRuleMatchesTheRecordShape_AndUnfetchedRowsAreSkipped()
    {
        TestScope.Fresh();
        int serial = 0;
        Track Row(bool fetched, params string[] tags)
        {
            var t = Entities.Current.Tracks;
            int slot = t.Slot(("spotify:track:chips-" + (++serial)).AsSpan());
            var ids = new StringId[tags.Length];
            for (int i = 0; i < ids.Length; i++) ids[i] = Entities.Intern(Encoding.UTF8.GetBytes(tags[i]));
            Entities.Current.Edges.TrackTags.Replace(slot, new int[tags.Length], ids, EdgeState.Complete, tags.Length);
            if (fetched) t.Known[slot] |= (uint)TrackFields.Tags;
            return new Track(slot);
        }

        Track[] handles =
        [
            Row(true, "Pop"), Row(true, "Pop", "Chill"), Row(true, "pop"), Row(true, "Chill"), Row(true, "Chill"),
            Row(false, "Metal"), Row(false, "Metal"), Row(false, "Metal"),   // a stale payload on unfetched rows
        ];
        var records = new List<IReadOnlyList<string>?>
        {
            T("Pop"), T("Pop", "Chill"), T("pop"), T("Chill"), T("Chill"), null, null, null,
        };

        Assert.Equal(ContentFilterTags.Derive(records), ContentFilterTags.Derive(handles));
        Assert.Equal(Pairs(ContentFilterTags.DeriveCounted(records)), Pairs(ContentFilterTags.DeriveCounted(handles)));
        Assert.DoesNotContain("Metal", ContentFilterTags.Derive(handles));

        var viaHandles = ContentFilterTags.OrderByEvidence(Curated, handles);
        var viaRecords = ContentFilterTags.OrderByEvidence(Curated, records);
        Assert.Equal(viaRecords.Titles, viaHandles.Titles);
        Assert.Equal(viaRecords.EvidencedCount, viaHandles.EvidencedCount);
        Assert.Equal(1, viaHandles.EvidencedCount);                  // Chill only — Metal's carriers were never fetched
    }
}
