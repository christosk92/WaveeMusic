// ── Wavee.Tests/PlaylistDepositTargetsTests.cs — WP-5.O stream A ─────────────────────────────────────────────────────
// 0.2.9's PlaylistDepositTargetsTests, ported verbatim: only the input changes, `PlaylistSummary` → `DepositCandidate`
// (the three facts the rule reads). Pure: no scope, no settings store.

using System;
using System.Collections.Generic;
using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>The ADD-TO-PLAYLIST target gates: which playlists a deposit may land in, in what order, and what a brand-new
/// one is called — once decided in three places (the picker, the menu builder, the tab drop rules) or not at all.</summary>
public class PlaylistDepositTargetsTests
{
    static DepositCandidate P(string id, string name, bool canEdit = true)
        => new($"spotify:playlist:{id}", name, canEdit);

    static string[] Uris(IEnumerable<DepositCandidate> ps) => ps.Select(p => p.Uri).ToArray();

    // ── eligibility: ONE predicate, three former call sites ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("spotify:playlist:abc", true)]
    [InlineData("wavee:playlist:local", false)]      // the offline source's own playlists — nowhere for a deposit to land
    [InlineData("spotify:collection:tracks", false)] // Liked Songs is not a playlist you add INTO
    [InlineData("spotify:album:xyz", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyRealSpotifyPlaylistUrisAreDepositable(string? uri, bool expected)
        => Assert.Equal(expected, PlaylistDepositTargets.IsDepositable(uri));

    [Fact]
    public void AFollowedPlaylistIsNotEligible()
    {
        var followed = P("a", "Editorial", canEdit: false);
        Assert.False(PlaylistDepositTargets.IsEligible(in followed));

        // Collaborator (can edit, not the owner) IS eligible — that is the whole point of a collaborative playlist.
        var collab = P("b", "Shared", canEdit: true);
        Assert.True(PlaylistDepositTargets.IsEligible(in collab));
    }

    [Fact]
    public void ExcludeUriDropsTheSourceList()
    {
        var p = P("a", "Road trip");
        Assert.True(PlaylistDepositTargets.IsEligible(in p));
        Assert.False(PlaylistDepositTargets.IsEligible(in p, excludeUri: p.Uri));
    }

    // ── ordering: MRU first, then rootlist order ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void OrderPutsRecentTargetsFirstThenRootlistOrder()
    {
        var all = new[] { P("a", "A"), P("b", "B"), P("c", "C"), P("d", "D") };
        var recents = new[] { all[2].Uri, all[0].Uri };   // C filed into most recently, then A

        var ordered = PlaylistDepositTargets.Order(all, recents);

        Assert.Equal(new[] { all[2].Uri, all[0].Uri, all[1].Uri, all[3].Uri }, Uris(ordered));
    }

    [Fact]
    public void OrderNeverDuplicatesAndNeverDropsAnEligiblePlaylist()
    {
        var all = new[] { P("a", "A"), P("b", "B"), P("c", "C") };
        var ordered = PlaylistDepositTargets.Order(all, new[] { all[1].Uri, all[1].Uri });

        Assert.Equal(3, ordered.Count);
        Assert.Equal(3, Uris(ordered).Distinct().Count());
    }

    [Fact]
    public void AStaleRecentIsSkippedNotSurfaced()
    {
        // The MRU is a preference, not an assertion the playlist still exists: unfollowed, permissions revoked, or
        // simply not loaded yet. Surfacing it would offer a destination the deposit cannot reach.
        var all = new[] { P("a", "A"), P("gone", "Revoked", canEdit: false) };
        var ordered = PlaylistDepositTargets.Order(all, new[] { "spotify:playlist:vanished", all[1].Uri, all[0].Uri });

        Assert.Equal(new[] { all[0].Uri }, Uris(ordered));
    }

    [Fact]
    public void OrderFiltersByNameCaseInsensitively()
    {
        var all = new[] { P("a", "90s Love Songs"), P("b", "Road trip"), P("c", "loveless") };
        Assert.Equal(new[] { all[0].Uri, all[2].Uri }, Uris(PlaylistDepositTargets.Order(all, null, null, "LOVE")));
        Assert.Empty(PlaylistDepositTargets.Order(all, null, null, "zzz"));
    }

    [Fact]
    public void OrderIsStable()
    {
        var all = new[] { P("a", "A"), P("b", "B"), P("c", "C") };
        var recents = new[] { all[1].Uri };
        Assert.Equal(Uris(PlaylistDepositTargets.Order(all, recents)), Uris(PlaylistDepositTargets.Order(all, recents)));
    }

    [Fact]
    public void OrderHandlesNoPlaylistsAndNoRecents()
    {
        Assert.Empty(PlaylistDepositTargets.Order(null));
        Assert.Empty(PlaylistDepositTargets.Order(Array.Empty<DepositCandidate>(), new[] { "spotify:playlist:a" }));
    }

    [Fact]
    public void MoreThanMaxInlineEligiblePlaylistsMeansTheSubmenuMustDeferToThePicker()
    {
        var all = Enumerable.Range(0, PlaylistDepositTargets.MaxInline + 3).Select(i => P($"p{i}", $"P{i}")).ToArray();
        var ordered = PlaylistDepositTargets.Order(all);
        Assert.True(ordered.Count > PlaylistDepositTargets.MaxInline);
        // …and the platform submenu's cap is the same number, so the two cannot disagree about when "More" appears.
        Assert.Equal(PlaylistDepositTargets.MaxInline, Actions.MenuRules.MaxInlinePlaylists);
    }

    // ── the MRU itself ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RememberPromotesToFrontDedupesAndCaps()
    {
        var mru = PlaylistDepositTargets.Remember(null, "spotify:playlist:a");
        mru = PlaylistDepositTargets.Remember(mru, "spotify:playlist:b");
        mru = PlaylistDepositTargets.Remember(mru, "spotify:playlist:a");   // re-file into A → A moves back to front

        Assert.Equal(new[] { "spotify:playlist:a", "spotify:playlist:b" }, mru);

        for (int i = 0; i < PlaylistDepositTargets.MaxRecent + 5; i++)
            mru = PlaylistDepositTargets.Remember(mru, $"spotify:playlist:x{i}");
        Assert.Equal(PlaylistDepositTargets.MaxRecent, mru.Count);
    }

    [Fact]
    public void RememberIgnoresANonDepositableUri()
    {
        var mru = PlaylistDepositTargets.Remember(new[] { "spotify:playlist:a" }, "wavee:playlist:local");
        Assert.Equal(new[] { "spotify:playlist:a" }, mru);
    }

    [Fact]
    public void TheMruCodecRoundTripsAndDropsJunk()
    {
        var mru = new[] { "spotify:playlist:a", "spotify:playlist:b" };
        Assert.Equal(mru, PlaylistDepositTargets.Parse(PlaylistDepositTargets.Serialize(mru)));

        Assert.Equal(new[] { "spotify:playlist:a" }, PlaylistDepositTargets.Parse("\n\nspotify:playlist:a\n\n"));
        Assert.Equal("spotify:playlist:a", PlaylistDepositTargets.Serialize(new[] { "junk", "spotify:playlist:a", "" }));
        Assert.Empty(PlaylistDepositTargets.Parse(null));
        Assert.Equal("", PlaylistDepositTargets.Serialize(null));
    }

    // ── "{base} #N" naming ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NextDefaultNameCountsFromOneOnAnEmptyLibrary()
    {
        Assert.Equal("My Playlist #1", PlaylistDepositTargets.NextDefaultName(null, "My Playlist"));
        Assert.Equal("My Playlist #1", PlaylistDepositTargets.NextDefaultName(Array.Empty<DepositCandidate>(), "My Playlist"));
    }

    [Fact]
    public void NextDefaultNameFillsTheFirstGapRatherThanClimbing()
    {
        var all = new[] { P("a", "My Playlist #1"), P("c", "My Playlist #3") };
        Assert.Equal("My Playlist #2", PlaylistDepositTargets.NextDefaultName(all, "My Playlist"));
    }

    [Fact]
    public void NextDefaultNameSkipsAUserRenamedCollision()
    {
        var all = new[] { P("a", "My Playlist #1"), P("b", "my playlist #2"), P("c", "Road trip") };
        Assert.Equal("My Playlist #3", PlaylistDepositTargets.NextDefaultName(all, "My Playlist"));
    }

    [Fact]
    public void NextDefaultNameWorksForAnyLocalizedBaseAndNeverReturnsBlank()
    {
        Assert.Equal("Mijn afspeellijst #1", PlaylistDepositTargets.NextDefaultName(null, "Mijn afspeellijst"));
        Assert.Equal("Playlist #1", PlaylistDepositTargets.NextDefaultName(null, "   "));
    }

    [Fact]
    public void NextDefaultNameAlwaysTerminates()
    {
        var all = Enumerable.Range(1, 50).Select(i => P($"p{i}", $"My Playlist #{i}")).ToArray();
        Assert.Equal("My Playlist #51", PlaylistDepositTargets.NextDefaultName(all, "My Playlist"));
    }
}
