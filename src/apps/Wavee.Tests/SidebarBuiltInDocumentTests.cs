using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// R3.0.2 — CLASSIC AS A LOCKED DOCUMENT. Classic used to be a hand-built pane body, so its information architecture and
// its 44-DIP row geometry lived in code the tests could not reach. It is now a document over the ONE pane renderer, which
// means the IA and the DENSITY INTENT behind the metrics are ordinary data — and pinned here, because "Classic looks
// exactly like Classic" is a pixel contract that must not be re-litigated by a future template edit.
//
// Why density and not heights: the shared ladder lives in SidebarRowMetrics (engine-bound, so not source-includable here).
// These tests pin the INPUT to that ladder — Cozy+Subtitles for the artwork bands (⇒ 44 with 32-DIP art) and
// Comfortable+no-subtitle for the glyph bands (⇒ 44). A change to either silently changes Classic's row height.
public sealed class SidebarBuiltInDocumentTests
{
    /// <summary>The document a NORMAL user gets: every section open, developer mode OFF (so no Tools section).</summary>
    static SidebarCustomLayout Classic() => SidebarBuiltInDocuments.Classic(true, true, true);

    /// <summary>The document a DEVELOPER gets — the same thing plus the Tools section and its leading divider.</summary>
    static SidebarCustomLayout ClassicDev() => SidebarBuiltInDocuments.Classic(true, true, true, devTools: true);

    static SidebarSectionSpec Section(SidebarCustomLayout l, string id)
    {
        var s = l.Find(id);
        Assert.NotNull(s);
        return s!;
    }

    static SidebarSectionKind[] Kinds(SidebarCustomLayout doc)
    {
        var kinds = new SidebarSectionKind[doc.Sections.Count];
        for (int i = 0; i < kinds.Length; i++) kinds[i] = doc.Sections[i].Kind;
        return kinds;
    }

    [Fact]
    public void Classic_ReproducesTodaysInformationArchitecture()
    {
        var doc = ClassicDev();

        // Pinned · rule · Your Library · rule · Playlists · rule · DevTools — the retired WaveeSidebar.ExpandedBody order.
        Assert.Equal(new[]
        {
            SidebarSectionKind.Pinned,
            SidebarSectionKind.Divider,
            SidebarSectionKind.CollectionShortcuts,
            SidebarSectionKind.Divider,
            SidebarSectionKind.PlaylistTree,
            SidebarSectionKind.Divider,
            SidebarSectionKind.StaticLinks,
        }, Kinds(doc));

        // Pinned is FIRST, so the planner emits no leading divider before it (Classic's `rule: false`).
        Assert.Equal(SidebarSectionKind.Pinned, doc.Sections[0].Kind);
    }

    // ── developer mode gates the API console (and takes its divider with it) ──────────────────────────────────────────

    /// <summary>The API console is DEVELOPER surface. Off — the product default — Classic ends at Playlists, and the
    /// divider that used to separate Playlists from Tools goes too: a trailing rule under the last real section, with
    /// nothing after it, is exactly how a reader spots that something was removed rather than never offered.</summary>
    [Fact]
    public void Classic_WithoutDeveloperMode_EndsAtPlaylistsAndCarriesNoToolsDivider()
    {
        var doc = Classic();

        Assert.Equal(new[]
        {
            SidebarSectionKind.Pinned,
            SidebarSectionKind.Divider,
            SidebarSectionKind.CollectionShortcuts,
            SidebarSectionKind.Divider,
            SidebarSectionKind.PlaylistTree,
        }, Kinds(doc));

        Assert.Null(doc.Find(SidebarBuiltInDocuments.ToolsId));
        Assert.Equal(SidebarSectionKind.PlaylistTree, doc.Sections[^1].Kind);
    }

    /// <summary>Turning developer mode on is PURELY ADDITIVE: every section the plain document had is still there, in
    /// the same order, with the same ids — Tools is appended, nothing is rearranged to make room for it.</summary>
    [Fact]
    public void Classic_DeveloperMode_OnlyAppendsTheToolsBand()
    {
        var plain = Classic();
        var dev = ClassicDev();

        Assert.Equal(plain.Sections.Count + 2, dev.Sections.Count);   // the divider AND the section
        for (int i = 0; i < plain.Sections.Count; i++)
            Assert.Equal(plain.Sections[i].Id, dev.Sections[i].Id);
        Assert.Equal(SidebarBuiltInDocuments.ToolsId, dev.Sections[^1].Id);
    }

    /// <summary>The RULE the three sidebar offer sites share (Classic's document, the Curated palette's destination
    /// group, the static-links picker). Pinned here so "hidden in two of the three" cannot happen quietly.</summary>
    [Theory]
    [InlineData("api-console", false, false)]
    [InlineData("api-console", true, true)]
    [InlineData("settings", false, true)]
    [InlineData("home", false, true)]
    [InlineData(null, false, true)]
    public void ShowsRoute_HidesOnlyTheDeveloperRoutes(string? route, bool devMode, bool shown)
        => Assert.Equal(shown, DeveloperMode.ShowsRoute(route, devMode));

    [Fact]
    public void Classic_LibraryShortcutsKeepTodaysOrderAndIcons()
    {
        var lib = Section(Classic(), SidebarBuiltInDocuments.LibraryId);
        var keys = new string[lib.ItemList.Count];
        var icons = new string?[lib.ItemList.Count];
        for (int i = 0; i < keys.Length; i++) { keys[i] = lib.ItemList[i].Key; icons[i] = lib.ItemList[i].IconOverride; }

        Assert.Equal(new[] { "albums", "artists", "liked", "podcasts", "local" }, keys);
        Assert.Equal(new string?[] { "Album", "Contact", "Heart", "RadioTower", "Folder" }, icons);
        Assert.True(lib.Opts.CountBadges);   // the counts survive — as quiet numbers, never the accent pill
    }

    [Fact]
    public void Classic_DensityIntentYields44DipRowsEverywhere()
    {
        var doc = ClassicDev();   // the Tools band only exists in developer mode, and its density is pinned below

        // Glyph bands: Comfortable + no subtitle ⇒ HeightFor == 44 (Cozy would be 40 and would shrink Classic's rows).
        foreach (string id in new[] { SidebarBuiltInDocuments.LibraryId, SidebarBuiltInDocuments.ToolsId })
        {
            var s = Section(doc, id);
            Assert.Equal(SidebarDensity.Comfortable, s.Opts.Density);
            Assert.False(s.Opts.Subtitles);
            Assert.False(s.Opts.Artwork);
        }

        // Artwork bands: Cozy + subtitles ⇒ HeightFor == 44 with 32-DIP covers (Classic's pinned + playlist rows).
        foreach (string id in new[] { SidebarBuiltInDocuments.PinnedId, SidebarBuiltInDocuments.PlaylistsId })
        {
            var s = Section(doc, id);
            Assert.Equal(SidebarDensity.Cozy, s.Opts.Density);
            Assert.True(s.Opts.Subtitles);
            Assert.True(s.Opts.Artwork);
        }
    }

    [Fact]
    public void Classic_DevToolsSectionIsHeaderlessAndBadgeless()
    {
        var tools = Section(ClassicDev(), SidebarBuiltInDocuments.ToolsId);
        // No Title and no TitleLocKey ⇒ the planner emits NO SectionHeader row, which is how the landed DevToolsRow
        // rendered: a flat row outside every section.
        Assert.Null(tools.Title);
        Assert.Null(tools.TitleLocKey);
        Assert.False(tools.Opts.CountBadges);
        // …and it is EXPANDED-ONLY: the 56-DIP rail would show a bare Icons.Code glyph ("{}") with no label, plus the
        // divider that precedes it. The planner drops both once the section opts out.
        Assert.False(tools.Opts.ShowInRail);
        Assert.Single(tools.ItemList);
        Assert.Equal(SidebarBuiltInDocuments.DevToolsRoute, tools.ItemList[0].Key);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void Classic_CollapseFlagsDriveTheThreeCollapsibleSections(bool pinned, bool library, bool playlists)
    {
        var doc = SidebarBuiltInDocuments.Classic(pinned, library, playlists);
        Assert.Equal(!pinned, Section(doc, SidebarBuiltInDocuments.PinnedId).Collapsed);
        Assert.Equal(!library, Section(doc, SidebarBuiltInDocuments.LibraryId).Collapsed);
        Assert.Equal(!playlists, Section(doc, SidebarBuiltInDocuments.PlaylistsId).Collapsed);
    }

    [Fact]
    public void Classic_SectionIdsAreStableAcrossRebuildsAndMapToTheirPreferenceFlag()
    {
        // NOT SidebarIds.NewSection(): the pane keys its reorder bands, collapse routing and section identity off these,
        // so a fresh id per rebuild would reset all three on every toggle.
        var a = ClassicDev();
        var b = SidebarBuiltInDocuments.Classic(false, false, false, devTools: true);
        for (int i = 0; i < a.Sections.Count; i++) Assert.Equal(a.Sections[i].Id, b.Sections[i].Id);

        Assert.Equal(ClassicSection.Pinned, SidebarBuiltInDocuments.ClassicSectionOf(SidebarBuiltInDocuments.PinnedId));
        Assert.Equal(ClassicSection.Library, SidebarBuiltInDocuments.ClassicSectionOf(SidebarBuiltInDocuments.LibraryId));
        Assert.Equal(ClassicSection.Playlists, SidebarBuiltInDocuments.ClassicSectionOf(SidebarBuiltInDocuments.PlaylistsId));
        // A non-collapsible section (a divider, the header-less tools links) must be a NO-OP, never a mis-write.
        Assert.Null(SidebarBuiltInDocuments.ClassicSectionOf(SidebarBuiltInDocuments.ToolsId));
        Assert.Null(SidebarBuiltInDocuments.ClassicSectionOf("nope"));
    }

    [Fact]
    public void Classic_TemplateIdIsNotOneOfTheCuratedTemplates()
    {
        // Classic is never offered in the customizer's template palette, and a Curated document must never claim its id.
        Assert.False(SidebarTemplates.IsKnown(SidebarBuiltInDocuments.ClassicId));
    }
}
