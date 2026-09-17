// ── Wavee.Tests/ArtistRevealTests.cs — bug F/D (2026-09-15 handoff §4, §5), pure facts only ────────────────────────────
//
// Bug F, restored to 0.2.10's model after a same-day regression: the WHOLE artist page — hero photo, hero text
// (name/verified/bio/stats), magazine, chart — is ONE reveal unit gated on the overview alone; there is no separate
// hero gate. Bug D: every discography facet's first page asks at the same priority — no facet-identity demotion. Both
// facts are read straight off the production pure decisions (ArtistReadiness.MagazinePending,
// DiscographyRules.FirstPagePriority) with no source-text reads.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DiscographyRulesTests
{
    /// <summary>Bug D: Albums, Singles and Compilations are simultaneously on screen (the artist page's tabs are a
    /// scroll-spy, not a view switch) — no facet is demoted below another's priority for its first page.</summary>
    [Theory]
    [InlineData(DiscoFacet.Albums)]
    [InlineData(DiscoFacet.Singles)]
    [InlineData(DiscoFacet.Compilations)]
    public void FirstPage_IsAlwaysVisible_RegardlessOfFacetIdentity(DiscoFacet facet)
        => Assert.Equal(FetchPriority.Visible, DiscographyRules.FirstPagePriority(facet));
}

[Collection(EntitiesCollection.Name)]
public class ArtistPageRevealTests
{
    const string Uri = "spotify:artist:page-reveal";

    static Artist ArtistOf() => Entities.Artist(EntityUri.Parse(Uri.AsSpan()));

    /// <summary>Bug F (restored to 0.2.10's model): the page's ONE reveal gate is exactly the overview readiness —
    /// <see cref="ArtistReadiness.MagazinePending"/> and <see cref="ArtistReadiness.Overview"/> must always disagree
    /// (one is the other's negation), across the SAME real state transition <c>ArtistReadinessTests</c> pins for
    /// Overview itself: nothing known ⇒ pending, one overview group short ⇒ still pending, the whole unit landed ⇒
    /// not pending. There is no separate, earlier-flipping hero predicate any more (Artist.Page.cs's `_bodyReady`
    /// is this rule's negation directly: `_bodyReady = _ready = ArtistReadiness.Overview(a)`) — the hero's photo,
    /// its verified badge, its bio lead and its stats all wait for the SAME instant as the magazine and the chart.</summary>
    [Fact]
    public void MagazinePending_IsExactlyNotOverview_TheWholePagesOnlyGate()
    {
        TestScope.Fresh();
        var a = ArtistOf();
        Assert.True(ArtistReadiness.MagazinePending(a));                     // nothing known yet ⇒ pending
        Assert.Equal(!ArtistReadiness.Overview(a), ArtistReadiness.MagazinePending(a));

        var partial = Staging.Rent();
        ref var row = ref partial.Artists.RowFor(new StagedId(partial.Text(Uri)), Authority.Full,
            (uint)(ArtistFields.Overview & ~ArtistFields.Tour));
        row.Name = partial.Text("Page Reveal");
        TestScope.CommitAndPublish(partial);
        Assert.True(ArtistReadiness.MagazinePending(a));                     // one group short ⇒ still pending
        Assert.Equal(!ArtistReadiness.Overview(a), ArtistReadiness.MagazinePending(a));

        var rest = Staging.Rent();
        rest.Artists.RowFor(new StagedId(rest.Text(Uri)), Authority.Full, (uint)ArtistFields.Tour);
        TestScope.CommitAndPublish(rest);
        Assert.False(ArtistReadiness.MagazinePending(a));                    // the whole unit landed ⇒ not pending
        Assert.Equal(!ArtistReadiness.Overview(a), ArtistReadiness.MagazinePending(a));
    }

    /// <summary>An invalid artist (no row at all) is pending, same as <see cref="ArtistReadiness.Overview"/> reading
    /// false for it — the page never shows content for a subject that doesn't exist.</summary>
    [Fact]
    public void MagazinePending_TrueForAnInvalidArtist()
        => Assert.True(ArtistReadiness.MagazinePending(default));
}
