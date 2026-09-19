// ── Wavee.Tests/ScopeRetirementTests.cs — what a scope switch gives back and who hears about it (gap batch B1) ─────
//
// A login now runs `Entities.Switch` on every welcome, which turned three latent defects into per-sign-in ones:
//   · G-052 — `Scope.ReleaseText` walked the eight entity tables only, so every greeting, band title, browse tile and
//     query of the retired scope, and the recents revision, stayed in the engine's interner for the process's life;
//   · G-179 — a subscriber that captured a table's `Changed` signal kept listening to the dead set (the sidebar binder);
//   · G-051 — `Browse.Commit` had no caller, so a decoded browse page landed nothing.
// Each fact reads the interner or the signal directly: the leak and the deafness are both invisible to a page.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ScopeRetirementTests
{
    /// <summary>Intern-and-look: an id that was reclaimed does not come back — re-interning its text mints a NEW id
    /// (the engine never reuses a reclaimed id), so "released" is observable as "not the same id".</summary>
    static StringId Intern(string text) => Entities.Strings.Intern(text);

    [Fact]
    public void Retiring_a_scope_hands_back_the_synthetic_subjects_text()
    {
        TestScope.Fresh();
        var scope = Entities.Current;
        int home = Entities.HomeFeed().Slot;
        StringId greeting = Intern("ScopeRetirement/greeting");
        scope.Homes.SetText(ref scope.Homes.Greeting, home, greeting);
        int band = Entities.Section("spotify:section:ScopeRetirement".AsSpan()).Slot;
        StringId title = Intern("ScopeRetirement/band");
        scope.Sections.SetText(ref scope.Sections.Title, band, title);
        int node = Entities.BrowseNode("spotify:page:ScopeRetirement".AsSpan()).Slot;
        StringId tile = Intern("ScopeRetirement/tile");
        scope.Browses.SetText(ref scope.Browses.Title, node, tile);
        StringId query = Entities.Search("ScopeRetirement query".AsSpan()).QueryId;

        Entities.Switch(CatalogScope.Fake(locale: "sv-SE", market: "SE"));

        Assert.NotEqual(greeting, Intern("ScopeRetirement/greeting"));
        Assert.NotEqual(title, Intern("ScopeRetirement/band"));
        Assert.NotEqual(tile, Intern("ScopeRetirement/tile"));
        Assert.NotEqual(query, Intern("ScopeRetirement query"));
    }

    [Fact]
    public void Retiring_a_scope_hands_back_the_edge_owned_revisions()
    {
        TestScope.Fresh();
        var edges = Entities.Current.Edges;
        StringId recents = Intern("ScopeRetirement/recents-revision");
        StringId rootlist = Intern("ScopeRetirement/rootlist-revision");
        edges.SetRecentsRevision(3, recents);
        edges.SetRootlistRevision(3, rootlist);
        Assert.Equal(rootlist, edges.RootlistRevision(3));

        Entities.Switch(CatalogScope.Fake(locale: "fi-FI", market: "FI"));

        Assert.NotEqual(recents, Intern("ScopeRetirement/recents-revision"));
        Assert.NotEqual(rootlist, Intern("ScopeRetirement/rootlist-revision"));
    }

    [Fact]
    public void The_scope_epoch_signal_moves_on_every_boot_and_every_switch_and_on_nothing_else()
    {
        TestScope.Fresh();
        uint booted = Entities.ScopeEpoch.Peek();

        Entities.Switch(CatalogScope.Fake(locale: "da-DK", market: "DK"));
        uint switched = Entities.ScopeEpoch.Peek();
        Assert.NotEqual(booted, switched);

        // A commit is not a scope change.
        var s = Staging.Rent();
        s.Browses.RowFor(new StagedId(s.Text("spotify:page:epoch")), Authority.Full, (uint)BrowseFields.Identity).Title = s.Text("x");
        TestScope.CommitAndPublish(s);
        Assert.Equal(switched, Entities.ScopeEpoch.Peek());

        // A re-Boot mints a fresh scope at epoch 0 again — the generation still moves, or nobody would wake.
        TestScope.Fresh();
        Assert.NotEqual(switched, Entities.ScopeEpoch.Peek());
        Assert.Equal(0u, Entities.Current.Epoch);
    }

    [Fact]
    public void A_decoded_browse_node_lands_through_the_commit_chain()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Browses.RowFor(new StagedId(s.Text("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy")), Authority.Full,
                                           (uint)BrowseFields.Identity);
        row.Title = s.Text("Music");
        row.Color = 0xFF1E3264;
        TestScope.CommitAndPublish(s);

        var node = Entities.BrowseNode("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy".AsSpan());
        Assert.True(node.Knows(BrowseFields.Identity));
        Assert.Equal("Music", Entities.Strings.Resolve(node.TitleId));
        Assert.Equal(0xFF1E3264u, node.Color);
    }
}
