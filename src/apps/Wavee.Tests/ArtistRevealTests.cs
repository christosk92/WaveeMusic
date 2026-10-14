// ── Wavee.Tests/ArtistRevealTests.cs — bug F/D (2026-09-15 handoff §4, §5), pure facts only ────────────────────────────
//
// Bug F, restored to 0.2.10's model after a same-day regression: the WHOLE artist page — hero photo, hero text
// (name/verified/bio/stats), magazine, chart — is ONE reveal unit gated on the overview alone; there is no separate
// hero gate. Bug D: every discography facet's first page asks at the same priority — no facet-identity demotion. Both
// facts are read straight off the production pure decisions (ArtistReadiness.Overview,
// DiscographyRules.FirstPagePriority) with no source-text reads.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DiscographyRulesTests
{
    /// <summary>Bug D: Albums, Singles and Compilations are simultaneously on screen (the artist page's tabs are a
    /// scroll-spy, not a view switch) — no facet is demoted below another's priority for its first page.</summary>
    [Fact]
    public void FirstPage_IsAlwaysVisible_RegardlessOfFacetIdentity()
        => Assert.Equal(FetchPriority.Visible, DiscographyRules.FirstPagePriority);
}

[Collection(EntitiesCollection.Name)]
public class ArtistPageRevealTests
{
    const string Uri = "spotify:artist:page-reveal";

    static Artist ArtistOf() => Entities.Artist(EntityUri.Parse(Uri.AsSpan()));

    /// <summary>Bug F (restored to 0.2.10's model): the page's DATA gate is exactly the overview readiness
    /// (<see cref="ArtistReadiness.Overview"/>) across the real state transition: nothing known ⇒ pending, one overview
    /// group short ⇒ still pending, the whole unit landed ⇒ not pending. The visual swap (<see cref="ArtistReadiness.BodyReady"/>) still waits on that unit, plus a
    /// measured width, the first hero decode, and the chart ready-or-failed, so the page does not flash Wide
    /// geometry, a flat hero, or a second top-tracks wave.</summary>
    [Fact]
    public void TheOverviewIsTheWholePagesOnlyDataGate()
    {
        TestScope.Fresh();
        var a = ArtistOf();
        Assert.False(ArtistReadiness.Overview(a));                     // nothing known yet ⇒ pending

        var partial = Staging.Rent();
        ref var row = ref partial.Artists.RowFor(new StagedId(partial.Text(Uri)), Authority.Full,
            (uint)(ArtistFields.Overview & ~ArtistFields.Tour));
        row.Name = partial.Text("Page Reveal");
        TestScope.CommitAndPublish(partial);
        Assert.False(ArtistReadiness.Overview(a));                     // one group short ⇒ still pending

        var rest = Staging.Rent();
        rest.Artists.RowFor(new StagedId(rest.Text(Uri)), Authority.Full, (uint)ArtistFields.Tour);
        TestScope.CommitAndPublish(rest);
        Assert.True(ArtistReadiness.Overview(a));                      // the whole unit landed ⇒ not pending
    }

    /// <summary>An invalid artist (no row at all) is pending: <see cref="ArtistReadiness.Overview"/> reads false for it —
    /// the page never shows content for a subject that doesn't exist.</summary>
    [Fact]
    public void AnInvalidArtistIsPending()
        => Assert.False(ArtistReadiness.Overview(default));
}
