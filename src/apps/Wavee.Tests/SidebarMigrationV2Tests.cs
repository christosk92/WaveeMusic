// ── Wavee.Tests/SidebarMigrationV2Tests.cs — the one-time migration from the three designs and sidebar-layout.json v2 ────
//
// SidebarMigrationV2.Migrate is pure: every fixture is a DTO built in code (the v2 shapes of SidebarStoreV2), and every
// fact asserts on the result (the layout, the device state, the account data, the toast and the dropped keys). The IO half
// (SidebarMigrationHost) is covered by SidebarWiringTests' boot facts.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SidebarMigrationV2Tests
{
    static SidebarMigrationInput In(int design = 0, SidebarLayoutDocDto? v2 = null, bool pinnedOpen = true,
                                    bool libraryOpen = true, bool playlistsOpen = true, float width = 300f,
                                    bool collapsed = false, int filter = 0, int sort = 0, bool desc = false,
                                    int view = 1, bool pinsMigrated = false, string account = "spotify:me")
        => new(Design: design, V2: v2, ClassicPinnedOpen: pinnedOpen, ClassicLibraryOpen: libraryOpen,
               ClassicPlaylistsOpen: playlistsOpen, DesignWidth: width, DesignCollapsed: collapsed,
               V3Filter: filter, V3Sort: sort, V3Desc: desc, V3View: view, PinsMigrated: pinsMigrated, AccountKey: account);

    static SidebarLayoutDocDto Curated(params SidebarSectionDto[] sections)
        => new() { Version = 2, Curated = new SidebarCuratedDto { Sections = sections } };

    static SidebarSectionDto Sec(string kind, bool? hidden = null, SidebarItemDto[]? items = null,
                                 SidebarDisplayDto? display = null, SidebarExtensionDto? ext = null)
        => new() { Kind = kind, Hidden = hidden, Items = items, Display = display, Extension = ext };

    static SectionState Classic(SidebarMigrationResult r, string id) => r.State.Classic.Find(id)!;
    static SectionState Library(SidebarMigrationResult r, string id) => r.State.Library.Find(id)!;

    // ── the layout and the sections a design maps to ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Design0_Classic_FlagsBecomeCollapsed()
    {
        var r = SidebarMigrationV2.Migrate(In(design: 0, pinnedOpen: false, libraryOpen: true, playlistsOpen: false));

        Assert.Equal(SidebarLayoutId.Classic, r.Layout);
        Assert.True(Classic(r, "pinned").Collapsed);
        Assert.False(Classic(r, "collections").Collapsed);
        Assert.True(Classic(r, "playlists").Collapsed);
    }

    [Fact]
    public void Design1_Library_SortDescView_CompactListBecomesCompactDensity()
    {
        var r = SidebarMigrationV2.Migrate(In(design: 1, sort: 1, desc: true, view: 0));

        Assert.Equal(SidebarLayoutId.Library, r.Layout);
        var library = Library(r, "library");
        Assert.Equal(SidebarLibrarySort.RecentlyAdded, library.Sort);
        Assert.True(library.Descending == true);
        Assert.Equal(SidebarLibraryView.List, library.View);
        Assert.Equal(SidebarDensity.Compact, r.Density);
    }

    [Fact]
    public void Design1_FilterMapped()
    {
        Assert.Equal(SidebarLibraryFilter.Playlists, SidebarMigrationV2.Migrate(In(design: 1, filter: 1)).Filter);
        Assert.Equal(SidebarLibraryFilter.Podcasts, SidebarMigrationV2.Migrate(In(design: 1, filter: 2)).Filter);
        Assert.Equal(SidebarLibraryFilter.Albums, SidebarMigrationV2.Migrate(In(design: 1, filter: 3)).Filter);
        Assert.Equal(SidebarLibraryFilter.Artists, SidebarMigrationV2.Migrate(In(design: 1, filter: 4)).Filter);
        Assert.Equal(SidebarLibraryFilter.None, SidebarMigrationV2.Migrate(In(design: 1, filter: 9)).Filter);
    }

    [Fact]
    public void Design2_ListNoTree_IsLibrary()
        => Assert.Equal(SidebarLayoutId.Library,
            SidebarMigrationV2.Migrate(In(design: 2, v2: Curated(Sec("entityList")))).Layout);

    [Fact]
    public void Design2_WithTree_IsClassic()
        => Assert.Equal(SidebarLayoutId.Classic,
            SidebarMigrationV2.Migrate(In(design: 2, v2: Curated(Sec("entityList"), Sec("playlistTree")))).Layout);

    [Fact]
    public void Design2_JumpBackIn_BecomesRecent_LimitNearest()
    {
        var twelve = SidebarMigrationV2.Migrate(In(design: 2,
            v2: Curated(Sec("jumpBackIn", display: new SidebarDisplayDto { MaxItems = 12 }))));
        Assert.False(Classic(twelve, "recent").Hidden);
        Assert.Equal(10, Classic(twelve, "recent").Limit);

        var none = SidebarMigrationV2.Migrate(In(design: 2,
            v2: Curated(Sec("jumpBackIn", display: new SidebarDisplayDto { MaxItems = 0 }))));
        Assert.Equal(5, Classic(none, "recent").Limit);
    }

    [Fact]
    public void Design2_NewReleases_Shown()
    {
        var r = SidebarMigrationV2.Migrate(In(design: 2, v2: Curated(Sec("newReleases"))));
        Assert.False(Classic(r, "newReleases").Hidden);
    }

    [Fact]
    public void Design2_HiddenPinned_Hidden()
    {
        var r = SidebarMigrationV2.Migrate(In(design: 2, v2: Curated(Sec("pinned", hidden: true))));
        Assert.True(Classic(r, "pinned").Hidden);
    }

    [Fact]
    public void Design2_HiddenShortcutItems_BecomeHiddenItems()
    {
        var r = SidebarMigrationV2.Migrate(In(design: 2, v2: Curated(Sec("collectionShortcuts", items:
        [
            new SidebarItemDto { Target = "route", Key = "liked", Hidden = true },
            new SidebarItemDto { Target = "route", Key = "albums" },
        ]))));

        Assert.Contains("liked", Classic(r, "collections").HiddenList);
        Assert.DoesNotContain("albums", Classic(r, "collections").HiddenList);
    }

    [Fact]
    public void Design2_DroppedKinds_Named_InOrder_NoDuplicates()
    {
        // A spotlight and a queue do not carry over; dividers are structural now, so nothing is named for them; a repeated
        // kind is named once.
        var r = SidebarMigrationV2.Migrate(In(design: 2, v2: Curated(
            Sec("entityEmbed"),
            Sec("divider"),
            Sec("extension", ext: new SidebarExtensionDto { ExtensionId = "wavee", ContributionId = "queue" }),
            Sec("divider"),
            Sec("entityEmbed"))));

        Assert.Equal(new[] { "sidebar.migration.kind.spotlight", "sidebar.migration.kind.queue" }, r.Dropped);
    }

    [Fact]
    public void LayoutDerivedFromDesign_WithoutAFile()
    {
        // No sidebar-layout.json at all: the layout still comes from the design key, with no pins, no toast and no drops.
        var r = SidebarMigrationV2.Migrate(In(design: 1, v2: null));

        Assert.Equal(SidebarLayoutId.Library, r.Layout);
        Assert.Empty(r.Account.Pins);
        Assert.False(r.ShowToast);
        Assert.Empty(r.Dropped);
    }

    // ── the TopBar shortcuts and the v2 pins ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TopBar_HomeOnly_NoToast()
    {
        var v2 = new SidebarLayoutDocDto { Version = 2, TopBar = [new SidebarItemDto { Target = "route", Key = "home" }] };

        var r = SidebarMigrationV2.Migrate(In(design: 0, v2: v2));

        Assert.False(r.ShowToast);
        Assert.Empty(r.Account.Pins);
    }

    [Fact]
    public void TopBar_Shortcuts_BecomeLocalPins_Prepended_HomeAndLikedDropped_Toast()
    {
        var v2 = new SidebarLayoutDocDto
        {
            Version = 2,
            TopBar =
            [
                new SidebarItemDto { Target = "route", Key = "home" },
                new SidebarItemDto { Target = "route", Key = "search", Label = "Search" },
                new SidebarItemDto { Target = "route", Key = "liked" },
            ],
            Pins = [new SidebarPinDto { Id = "pl:spotify:playlist:a", EntityKind = "playlist", Uri = "spotify:playlist:a", Name = "A", AddedAtMs = 5 }],
        };

        var r = SidebarMigrationV2.Migrate(In(design: 0, v2: v2));

        Assert.Equal(new[] { "search", "pl:spotify:playlist:a" }, r.Account.Pins.Select(p => p.Id).ToArray());
        Assert.Equal("Search", r.Account.Pins[0].Name);
        Assert.True(r.ShowToast);
    }

    [Fact]
    public void V2Pins_LikedAndHomeDropped()
    {
        // Q1a: Liked and Home have fixed homes and are never pins, whatever a v2 file says.
        var v2 = new SidebarLayoutDocDto
        {
            Version = 2,
            Pins =
            [
                new SidebarPinDto { Id = "liked", EntityKind = "appRoute" },
                new SidebarPinDto { Id = "home", EntityKind = "appRoute" },
                new SidebarPinDto { Id = "search", EntityKind = "appRoute", Name = "Search" },
            ],
        };

        var r = SidebarMigrationV2.Migrate(In(design: 0, v2: v2));

        Assert.Equal(new[] { "search" }, r.Account.Pins.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void V2Pins_LegacyKindDecoded()
    {
        // A pin written before the kind was a name carries only the integer kind (Playlist = 1).
        var v2 = new SidebarLayoutDocDto
        {
            Version = 2,
            Pins = [new SidebarPinDto { Id = "pl:spotify:playlist:a", Kind = 1, Uri = "spotify:playlist:a", Name = "A" }],
        };

        var r = SidebarMigrationV2.Migrate(In(design: 0, v2: v2));

        Assert.Equal(SidebarEntryKind.Playlist, Assert.Single(r.Account.Pins).Kind);
    }

    // ── what the account carries, and the width, collapse and latch ──────────────────────────────────────────────────

    [Fact]
    public void FoldersAndFirstSeen_Carried()
    {
        var v2 = new SidebarLayoutDocDto
        {
            Version = 2,
            V3 = new SidebarV3Dto
            {
                ExpandedFolders = ["folder:x", "folder:x", "folder:y"],
                FirstSeen = [new SidebarFirstSeenDto("pl:a", 100)],
            },
        };

        var r = SidebarMigrationV2.Migrate(In(design: 1, v2: v2));

        Assert.Equal(new[] { "folder:x", "folder:y" }, r.Account.ExpandedFolders);   // duplicates folded
        var seen = Assert.Single(r.Account.FirstSeen);
        Assert.Equal("pl:a", seen.Id);
        Assert.Equal(100, seen.Ms);
    }

    [Fact]
    public void Width_Clamped()
    {
        Assert.Equal(SidebarPaneBounds.NavPaneMaxW, SidebarMigrationV2.Migrate(In(width: 9_999f)).Width);
        Assert.Equal(SidebarPaneBounds.NavPaneMinW, SidebarMigrationV2.Migrate(In(width: 1f)).Width);
    }

    [Fact]
    public void Collapsed_Carried()
        => Assert.True(SidebarMigrationV2.Migrate(In(collapsed: true)).UserCollapsed);

    [Fact]
    public void SignedOut_AccountKeyEmpty_IsPending()
    {
        // A migration that runs before anyone signs in parks the old pins in the pending file; the first account adopts them.
        var r = SidebarMigrationV2.Migrate(In(account: ""));

        Assert.Equal("", r.Account.Key);
        Assert.Equal(SidebarAccountStore.PendingFileName, SidebarAccountStore.FileNameOf(r.Account.Key));
    }

    [Fact]
    public void PinsMigrated_Carried()
        => Assert.True(SidebarMigrationV2.Migrate(In(pinsMigrated: true)).Account.MigratedToServer);
}
