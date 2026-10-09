// ── Wavee.Tests/SidebarVisibilityRulesTests.cs — pin dedupe, Your Library's pins, Liked, filters and pin states ─────────
//
// docs/plans/wavee/sidebar-rework-implementation.md §P3.4 and §P3.15 (design A.2, P.2a, Q2, Q9, Q12, D9). Pure rules: each
// branch is driven with plain entries, filters and numbers, no engine host.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class SidebarVisibilityRulesTests
{
    static SidebarLayoutDoc Doc(SidebarLayoutId layout, SidebarLayoutState? state = null, SidebarDensity density = SidebarDensity.Default)
        => SidebarLayoutRules.Resolve(state ?? SidebarLayoutState.Default, layout, density);

    static SidebarLayoutState Then(SidebarLayoutState s, SidebarOp op) => SidebarLayoutRules.Apply(s, op, pinnedLocked: false).State;

    static SidebarLibraryEntry Entity(SidebarEntryKind kind, string id, string name, string creator = "", bool audiobook = false)
        => new(id, kind, id, name, creator, default, null, 0, 0, 0, 0, 0, 0, false, SidebarPlaylistFlavor.None)
        { IsAudiobook = audiobook };

    [Fact]
    public void Dedupe_Matrix()
    {
        var classic = SidebarLayoutState.Default;
        var collapsed = Then(classic, new SetSectionCollapsed(SidebarLayoutId.Classic, "pinned", true));
        var hidden = Then(classic, new SetSectionShown(SidebarLayoutId.Classic, "pinned", false));
        var libraryCollapsed = Then(classic, new SetSectionCollapsed(SidebarLayoutId.Library, "pinned", true));
        var libraryHidden = Then(classic, new SetSectionShown(SidebarLayoutId.Library, "pinned", false));

        // Classic: visible and expanded dedupes; collapsed returns the pins to their sections unless presented compact.
        Assert.True(SidebarVisibilityRules.DedupesPins(Doc(SidebarLayoutId.Classic), compact: false));
        Assert.False(SidebarVisibilityRules.DedupesPins(Doc(SidebarLayoutId.Classic, collapsed), compact: false));
        Assert.True(SidebarVisibilityRules.DedupesPins(Doc(SidebarLayoutId.Classic, collapsed, SidebarDensity.Compact), compact: true));
        Assert.False(SidebarVisibilityRules.DedupesPins(Doc(SidebarLayoutId.Classic, hidden), compact: false));

        // Your Library always dedupes while Pinned is shown, collapsed or not.
        Assert.True(SidebarVisibilityRules.DedupesPins(Doc(SidebarLayoutId.Library), compact: false));
        Assert.True(SidebarVisibilityRules.DedupesPins(Doc(SidebarLayoutId.Library, libraryCollapsed), compact: false));
        Assert.False(SidebarVisibilityRules.DedupesPins(Doc(SidebarLayoutId.Library, libraryHidden), compact: false));
    }

    [Fact]
    public void LibraryPins_RoutePin_NoChip_FollowsTheSearch_AnyChipHides()
    {
        var pin = SidebarLibraryEntry.ForRoute("search", "Search");
        var none = SidebarLibraryKinds.None;
        Assert.True(SidebarVisibilityRules.ShowsPinInLibrary(in pin, SidebarLibraryFilter.None, "", none));
        Assert.True(SidebarVisibilityRules.ShowsPinInLibrary(in pin, SidebarLibraryFilter.None, "sea", none));   // P.2a / Q2
        Assert.False(SidebarVisibilityRules.ShowsPinInLibrary(in pin, SidebarLibraryFilter.None, "xyz", none));
        Assert.False(SidebarVisibilityRules.ShowsPinInLibrary(in pin, SidebarLibraryFilter.Albums, "", none));
        Assert.False(SidebarVisibilityRules.ShowsPinInLibrary(in pin, SidebarLibraryFilter.Playlists, "sea", none));
    }

    [Fact]
    public void LibraryPins_EntityFollowsChip_HiddenKinds_Search()
    {
        var album = Entity(SidebarEntryKind.Album, "album:a", "Blue Album", "Some Artist");
        var none = SidebarLibraryKinds.None;
        Assert.True(SidebarVisibilityRules.ShowsPinInLibrary(in album, SidebarLibraryFilter.None, "", none));
        Assert.True(SidebarVisibilityRules.ShowsPinInLibrary(in album, SidebarLibraryFilter.Albums, "", none));
        Assert.False(SidebarVisibilityRules.ShowsPinInLibrary(in album, SidebarLibraryFilter.Playlists, "", none));
        Assert.False(SidebarVisibilityRules.ShowsPinInLibrary(in album, SidebarLibraryFilter.None, "", SidebarLibraryKinds.Albums));
        Assert.True(SidebarVisibilityRules.ShowsPinInLibrary(in album, SidebarLibraryFilter.None, "blue", none));
        Assert.False(SidebarVisibilityRules.ShowsPinInLibrary(in album, SidebarLibraryFilter.None, "zzz", none));
    }

    [Fact]
    public void Liked_UnderNoneAndPlaylistsOnly_RespectsShowLiked_AndSearch()
    {
        var options = SidebarLibraryOptions.Default;
        Assert.True(SidebarVisibilityRules.ShowsLiked(options, SidebarLibraryFilter.None, "", "Liked Songs"));
        Assert.True(SidebarVisibilityRules.ShowsLiked(options, SidebarLibraryFilter.Playlists, "", "Liked Songs"));
        Assert.False(SidebarVisibilityRules.ShowsLiked(options, SidebarLibraryFilter.Albums, "", "Liked Songs"));
        Assert.False(SidebarVisibilityRules.ShowsLiked(options with { ShowLiked = false }, SidebarLibraryFilter.None, "", "Liked Songs"));
        Assert.False(SidebarVisibilityRules.ShowsLiked(options, SidebarLibraryFilter.None, "", "Liked Songs", drilled: true));
        Assert.True(SidebarVisibilityRules.ShowsLiked(options, SidebarLibraryFilter.None, "lik", "Liked Songs"));
        Assert.False(SidebarVisibilityRules.ShowsLiked(options, SidebarLibraryFilter.None, "xyz", "Liked Songs"));
        Assert.False(SidebarVisibilityRules.ShowsLiked(options, SidebarLibraryFilter.None, "lik", null));
    }

    [Fact]
    public void LockingPins_NamesRouteAndModulePins()
    {
        var pins = new[]
        {
            new SidebarPin("search", SidebarEntryKind.AppRoute, "", "Search", 0),
            new SidebarPin("module:wavee:module:radio:abc", SidebarEntryKind.AppRoute, "", "Radio", 0),
            new SidebarPin("pl:spotify:playlist:a", SidebarEntryKind.Playlist, "", "A", 0),
        };
        var names = new List<string>();
        SidebarVisibilityRules.LockingPins(pins, names);
        Assert.Equal(new[] { "Search", "Radio" }, names.ToArray());

        SidebarVisibilityRules.LockingPins(new[] { pins[2] }, names);
        Assert.Empty(names);
    }

    [Fact]
    public void Filters_Matches_SplitsPodcastsAndAudiobooks()
    {
        var podcast = Entity(SidebarEntryKind.Show, "show:p", "Pod", "Publisher");
        var audiobook = Entity(SidebarEntryKind.Show, "show:b", "Book", "Publisher", audiobook: true);
        Assert.True(SidebarLibraryFilters.Matches(SidebarLibraryFilter.Podcasts, in podcast));
        Assert.False(SidebarLibraryFilters.Matches(SidebarLibraryFilter.Audiobooks, in podcast));
        Assert.True(SidebarLibraryFilters.Matches(SidebarLibraryFilter.Audiobooks, in audiobook));
        Assert.False(SidebarLibraryFilters.Matches(SidebarLibraryFilter.Podcasts, in audiobook));

        var album = Entity(SidebarEntryKind.Album, "album:a", "Album", "Artist");
        Assert.False(SidebarLibraryFilters.Matches(SidebarLibraryFilter.Podcasts, in album));
        Assert.True(SidebarLibraryFilters.Matches(SidebarLibraryFilter.Albums, in album));
    }

    [Fact]
    public void Filters_HasChip_FalseForHiddenKind()
    {
        Assert.False(SidebarLibraryFilters.HasChip(SidebarLibraryFilter.Podcasts, SidebarLibraryKinds.Podcasts));
        Assert.True(SidebarLibraryFilters.HasChip(SidebarLibraryFilter.Albums, SidebarLibraryKinds.Podcasts));
        Assert.True(SidebarLibraryFilters.HasChip(SidebarLibraryFilter.None, SidebarLibraryKinds.Albums | SidebarLibraryKinds.Artists));
    }

    [Fact]
    public void Filters_Effective_FallsBackToNone_WhenItsKindIsHidden()
    {
        Assert.Equal(SidebarLibraryFilter.None, SidebarLibraryFilters.Effective((int)SidebarLibraryFilter.Albums, SidebarLibraryKinds.Albums));
        Assert.Equal(SidebarLibraryFilter.Albums, SidebarLibraryFilters.Effective((int)SidebarLibraryFilter.Albums, SidebarLibraryKinds.None));
        Assert.Equal(SidebarLibraryFilter.None, SidebarLibraryFilters.Effective(99, SidebarLibraryKinds.None));
    }

    [Fact]
    public void PinState_PendingOnlineUnknown_UnavailableOnAuthoritativeMiss_OfflineNeutral()
    {
        Assert.Equal(SidebarPinState.Pending, SidebarPinStateRules.Of(identityKnown: false, authoritativeMiss: false, online: true));
        Assert.Equal(SidebarPinState.Unavailable, SidebarPinStateRules.Of(identityKnown: false, authoritativeMiss: true, online: true));
        Assert.Equal(SidebarPinState.Unavailable, SidebarPinStateRules.Of(identityKnown: false, authoritativeMiss: true, online: false));
        Assert.Equal(SidebarPinState.Offline, SidebarPinStateRules.Of(identityKnown: false, authoritativeMiss: false, online: false));
        Assert.Equal(SidebarPinState.Resolved, SidebarPinStateRules.Of(identityKnown: true, authoritativeMiss: false, online: false));
    }

    [Fact]
    public void PinState_FallbackTitle_KindNoun_NeverBlank()
    {
        // A pin synced from another device before the projection hydrates has no name anywhere: D9 forbids a blank row.
        foreach (var kind in new[] { SidebarEntryKind.Playlist, SidebarEntryKind.Folder, SidebarEntryKind.Album,
                                     SidebarEntryKind.Artist, SidebarEntryKind.Show })
            Assert.False(string.IsNullOrEmpty(SidebarPinStateRules.FallbackTitleKey(kind)));
        Assert.Equal("nav.album", SidebarPinStateRules.FallbackTitleKey(SidebarEntryKind.Album));
        Assert.Null(SidebarPinStateRules.FallbackTitleKey(SidebarEntryKind.AppRoute));   // a route pin has its page title
    }
}
