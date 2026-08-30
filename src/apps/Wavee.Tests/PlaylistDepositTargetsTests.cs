using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The ADD-TO-PLAYLIST target gates: which playlists a deposit may land in, in what order, and what a brand-new one is
/// called. All three used to be decided in three different places (the picker, the menu builder, the tab drop rules) or
/// not at all (the ordering was rootlist order truncated to ten; the "#N" numbering did not exist and the screenshot
/// people recognise it from was fake data).
/// </summary>
public class PlaylistDepositTargetsTests
{
    static PlaylistSummary P(string id, string name, bool canEdit = true, bool owner = true)
        => new($"spotify:playlist:{id}", name, "me", 0, null, null, CanEdit: canEdit, IsOwner: owner);

    static string[] Uris(IEnumerable<PlaylistSummary> ps) => ps.Select(p => p.Uri).ToArray();

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
        var followed = P("a", "Editorial", canEdit: false, owner: false);
        Assert.False(PlaylistDepositTargets.IsEligible(in followed));

        // Collaborator (CanEdit && !IsOwner) IS eligible — that is the whole point of a collaborative playlist.
        var collab = P("b", "Shared", canEdit: true, owner: false);
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
        Assert.Equal(Uris(PlaylistDepositTargets.Order(all, recents)),
                     Uris(PlaylistDepositTargets.Order(all, recents)));
    }

    [Fact]
    public void OrderHandlesNoPlaylistsAndNoRecents()
    {
        Assert.Empty(PlaylistDepositTargets.Order(null));
        Assert.Empty(PlaylistDepositTargets.Order(Array.Empty<PlaylistSummary>(), new[] { "spotify:playlist:a" }));
    }

    [Fact]
    public void MoreThanMaxInlineEligiblePlaylistsMeansTheSubmenuMustDeferToThePicker()
    {
        // The truncation is the CALLER's, but the cap lives here so the submenu and the picker agree on when
        // "More playlists…" has to appear at all.
        var all = Enumerable.Range(0, PlaylistDepositTargets.MaxInline + 3)
                            .Select(i => P($"p{i}", $"P{i}")).ToArray();
        var ordered = PlaylistDepositTargets.Order(all);
        Assert.True(ordered.Count > PlaylistDepositTargets.MaxInline);
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

        // A hand-edited / older / partly-invalid stored value can never wedge the codec.
        Assert.Equal(new[] { "spotify:playlist:a" },
            PlaylistDepositTargets.Parse("\n\nspotify:playlist:a\n\n"));
        Assert.Equal("spotify:playlist:a",
            PlaylistDepositTargets.Serialize(new[] { "junk", "spotify:playlist:a", "" }));
        Assert.Empty(PlaylistDepositTargets.Parse(null));
        Assert.Equal("", PlaylistDepositTargets.Serialize(null));
    }

    // ── "{base} #N" naming ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NextDefaultNameCountsFromOneOnAnEmptyLibrary()
    {
        Assert.Equal("My Playlist #1", PlaylistDepositTargets.NextDefaultName(null, "My Playlist"));
        Assert.Equal("My Playlist #1",
            PlaylistDepositTargets.NextDefaultName(Array.Empty<PlaylistSummary>(), "My Playlist"));
    }

    [Fact]
    public void NextDefaultNameFillsTheFirstGapRatherThanClimbing()
    {
        // Deliberately first-unused, not max+1: deleting "#2" should let the next one reuse it instead of the numbering
        // marching upward forever.
        var all = new[] { P("a", "My Playlist #1"), P("c", "My Playlist #3") };
        Assert.Equal("My Playlist #2", PlaylistDepositTargets.NextDefaultName(all, "My Playlist"));
    }

    [Fact]
    public void NextDefaultNameSkipsAUserRenamedCollision()
    {
        var all = new[] { P("a", "My Playlist #1"), P("b", "my playlist #2"), P("c", "Road trip") };
        // Case-insensitive: two names that read identically in the sidebar are a collision.
        Assert.Equal("My Playlist #3", PlaylistDepositTargets.NextDefaultName(all, "My Playlist"));
    }

    [Fact]
    public void NextDefaultNameWorksForAnyLocalizedBaseAndNeverReturnsBlank()
    {
        // No parsing of existing names and no regex, so a localized base needs no special case.
        Assert.Equal("Mijn afspeellijst #1", PlaylistDepositTargets.NextDefaultName(null, "Mijn afspeellijst"));
        Assert.Equal("Playlist #1", PlaylistDepositTargets.NextDefaultName(null, "   "));
    }

    [Fact]
    public void NextDefaultNameAlwaysTerminates()
    {
        // N playlists can take at most N candidates, so N+1 is always free — pinning the bound so the search can never
        // become unbounded if the eligibility rules change.
        var all = Enumerable.Range(1, 50).Select(i => P($"p{i}", $"My Playlist #{i}")).ToArray();
        Assert.Equal("My Playlist #51", PlaylistDepositTargets.NextDefaultName(all, "My Playlist"));
    }
}
