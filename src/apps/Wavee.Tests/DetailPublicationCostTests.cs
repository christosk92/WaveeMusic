using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

/// <summary>What ONE query publication is allowed to cost the detail page.
/// <para>The measured regression these lock down: a cold 1,494-track playlist open republishes ≈20 times, and every
/// publication used to (a) re-run the whole-membership projection, which probed five to seven facts per track and
/// allocated a fresh <see cref="CatalogScope"/> for every one of those ~9,000 probes, and (b) re-render every realized
/// row. The rules are pure, so they are pinned here without a window, a service or a frame.</para></summary>
public sealed class DetailPublicationCostTests
{
    const string Provider = "spotify";
    static readonly CatalogScope Scope = new(Provider, "acct", "en", "NL", "premium", 1, false);

    static Track Song(int i, bool explicitTrack = false) => new("t" + i, "spotify:track:t" + i, "Title " + i,
        [], new("album", "spotify:album:album", "Album"), 123_000, explicitTrack, null);

    static Track[] Fixture(int count = 1500)
    {
        var rows = new Track[count];
        for (int i = 0; i < count; i++) rows[i] = Song(i, explicitTrack: i % 3 == 0);
        return rows;
    }

    /// <summary>A fact map with a video-association answer for the named subjects.</summary>
    static Dictionary<ResourceKey, QueryFact> Facts(params (string Uri, bool HasVideo)[] rows)
    {
        var map = new Dictionary<ResourceKey, QueryFact>();
        foreach (var (uri, hasVideo) in rows)
            map[new ResourceKey(Scope, uri, FacetKind.VideoAssociation)] = new(Knowledge.Present,
                new VideoAssociationValue(new VideoAssociation(uri, hasVideo, null, [], null, DateTimeOffset.UnixEpoch, 0)));
        return map;
    }

    sealed class CountingProvider
    {
        public int Calls;
        public string ForSubject(string uri)
        {
            Calls++;
            return EntityUri.Parse(uri).Provider;
        }
    }

    [Fact]
    public void ARestingListReadsNoKnowledgeAtAll_OverFifteenHundredTracks()
    {
        var rows = Fixture();
        var counter = new CountingProvider();
        var facts = new PublicationTrackFacts(Facts(), Scope, counter.ForSubject, static _ => false);
        var projection = DetailTrackProjection.Build(rows, default, "", TrackFilterState.Default, null,
            facts, DateTimeOffset.UnixEpoch);
        Assert.Equal(rows.Length, projection.Indices.Length);
        // Every Known probe exists to RELAX a filter whose fact has not landed. No filter, no query — nothing to relax,
        // so the publication is not read at all: zero key builds, zero scopes, zero dictionary probes.
        Assert.Equal(0, counter.Calls);
    }

    [Theory]
    // (filters, the facets those filters can be changed by) — the pass may read at most one lookup per track per facet.
    [InlineData(1)]
    [InlineData(2)]
    public void AnActiveFilterReadsAtMostOneLookupPerTrackPerNeededFacet(int facets)
    {
        var rows = Fixture();
        var counter = new CountingProvider();
        var facts = new PublicationTrackFacts(Facts(), Scope, counter.ForSubject, static _ => false);
        // One needed facet: the identity relaxation behind the explicit-content filter. Two: the video trait, which
        // reads the association's knowledge AND its value.
        var filters = facets == 1
            ? new TrackFilterState(ExplicitMode: TrackTraitMode.Hide)
            : new TrackFilterState(VideoMode: TrackTraitMode.Only);
        DetailTrackProjection.Build(rows, default, "", filters, null, facts, DateTimeOffset.UnixEpoch);
        Assert.InRange(counter.Calls, rows.Length, rows.Length * facets);
    }

    [Fact]
    public void OneScopeInstanceIsBuiltPerProvider_NotOnePerLookup()
    {
        var facts = new PublicationTrackFacts(Facts(), Scope, static uri => EntityUri.Parse(uri).Provider,
            static _ => false);
        var first = facts.ScopeFor("spotify:track:a");
        var again = facts.ScopeFor("spotify:track:b");
        // The SAME instance, so a repeated lookup neither allocates a scope nor pays CatalogScope's nine-field record
        // comparison on the dictionary probe — the record's reference shortcut answers it.
        Assert.Same(Scope, first);
        Assert.Same(first, again);
        var other = facts.ScopeFor("wavee:local:song.flac");
        Assert.NotSame(first, other);
        Assert.Equal("local", other!.Provider);
        Assert.Same(other, facts.ScopeFor("wavee:local:other.flac"));
        // Everything else about the scope travels unchanged: only the provider segment is re-pointed.
        Assert.Equal(Scope with { Provider = "local" }, other);
    }

    [Fact]
    public void TheHasVideoAnswerComesFromThePublication_AndFoldsInThePlanesThatDoNotTravelOnOne()
    {
        var facts = new PublicationTrackFacts(
            Facts(("spotify:track:t0", true), ("spotify:track:t1", false)), Scope,
            static uri => EntityUri.Parse(uri).Provider,
            // The user's own attachment / a module's verdict: not on the publication, folded in here.
            static uri => uri == "spotify:track:t2");
        Assert.True(facts.HasVideo("spotify:track:t0"));
        Assert.False(facts.HasVideo("spotify:track:t1"));
        Assert.True(facts.HasVideo("spotify:track:t2"));
        Assert.False(facts.HasVideo("spotify:track:t3"));   // no answer at all is not a video
        Assert.True(facts.Known("spotify:track:t1", FacetKind.VideoAssociation));
        Assert.False(facts.Known("spotify:track:t3", FacetKind.VideoAssociation));
    }

    [Fact]
    public void AVideoFilterSelectsFromThePublicationsAssociations()
    {
        Track[] rows = [Song(0), Song(1), Song(2)];
        var facts = new PublicationTrackFacts(
            Facts(("spotify:track:t0", true), ("spotify:track:t1", false), ("spotify:track:t2", false)),
            Scope, static uri => EntityUri.Parse(uri).Provider, static _ => false);
        var only = DetailTrackProjection.Build(rows, default, "", new(VideoMode: TrackTraitMode.Only), null,
            facts, DateTimeOffset.UnixEpoch);
        Assert.Equal([0], only.Indices);
        var hidden = DetailTrackProjection.Build(rows, default, "", new(VideoMode: TrackTraitMode.Hide), null,
            facts, DateTimeOffset.UnixEpoch);
        Assert.Equal([1, 2], hidden.Indices);
    }

    [Fact]
    public void AnUnansweredVideoAssociationRelaxesTheVideoFilterRatherThanEmptyingTheList()
    {
        Track[] rows = [Song(0), Song(1)];
        var facts = new PublicationTrackFacts(Facts(), Scope, static uri => EntityUri.Parse(uri).Provider,
            static _ => false);
        var only = DetailTrackProjection.Build(rows, default, "", new(VideoMode: TrackTraitMode.Only), null,
            facts, DateTimeOffset.UnixEpoch);
        Assert.Equal([0, 1], only.Indices);
    }

    /// <summary>The per-row values a realized row is rebuilt from must be EQUAL across a publication that changed
    /// nothing — that is what makes each row's <c>RowPresentation</c> compare equal and skip its re-render even though
    /// the rows-snapshot record itself is a new instance. (The presentation record is private to the engine-bound
    /// table; what it is computed FROM is pinned here.)</summary>
    [Fact]
    public void TheSameKnowledgeInADifferentDictionaryInstanceProjectsIdenticalRowValues()
    {
        var rows = Fixture(64);
        (string, bool)[] answers = [("spotify:track:t0", true), ("spotify:track:t1", false)];
        var before = new PublicationTrackFacts(Facts(answers), Scope, static uri => EntityUri.Parse(uri).Provider,
            static _ => false);
        // A fresh dictionary instance carrying identical content — exactly what the join hands out on every
        // publication, and the reason rows must not key off the dictionary's reference.
        var after = new PublicationTrackFacts(Facts(answers), Scope, static uri => EntityUri.Parse(uri).Provider,
            static _ => false);
        foreach (var row in rows)
        {
            Assert.Equal(before.HasVideo(row.Uri), after.HasVideo(row.Uri));
            Assert.Equal(before.Known(row.Uri, FacetKind.TrackIdentity), after.Known(row.Uri, FacetKind.TrackIdentity));
        }
        // …and the index map is byte-identical, so nothing downstream sees a change either.
        var first = DetailTrackProjection.Build(rows, default, "", TrackFilterState.Default, null, before, DateTimeOffset.UnixEpoch);
        var second = DetailTrackProjection.Build(rows, default, "", TrackFilterState.Default, null, after, DateTimeOffset.UnixEpoch);
        Assert.Equal(first.Indices, second.Indices);
        Assert.Equal(first.TopTrackId, second.TopTrackId);
    }

    [Fact]
    public void NoPublicationMeansEverythingIsKnown_SoAFilterIsNeverSilentlyRelaxed()
    {
        // TrackFacts.Unknown is what a surface with no query binding projects against: relaxing the user's filter
        // because "the fact has not landed" would be a lie there — nothing is ever going to land.
        Track[] rows = [Song(0, explicitTrack: true), Song(1)];
        var projection = DetailTrackProjection.Build(rows, default, "", new(ExplicitMode: TrackTraitMode.Hide), null,
            TrackFacts.Unknown, DateTimeOffset.UnixEpoch);
        Assert.Equal([1], projection.Indices);
        Assert.False(TrackFacts.Unknown.HasVideo("spotify:track:t0"));
        Assert.Null(TrackFacts.Unknown.ScopeFor("spotify:track:t0"));
    }
}
