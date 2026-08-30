using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// The five seed layouts (§C2 + the §C1.8.8 amendments). A template is the ONLY thing that decides what a fresh
// Curated sidebar looks like, so its composition is pinned row-for-row here rather than eyeballed in a screenshot.
public sealed class SidebarTemplateTests
{
    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarSectionKind[] Kinds(SidebarCustomLayout l)
    {
        var k = new SidebarSectionKind[l.Sections.Count];
        for (int i = 0; i < k.Length; i++) k[i] = l.Sections[i].Kind;
        return k;
    }

    static string[] Keys(SidebarSectionSpec s)
    {
        var k = new string[s.ItemList.Count];
        for (int i = 0; i < k.Length; i++) k[i] = s.ItemList[i].Key;
        return k;
    }

    static string?[] IconNames(SidebarSectionSpec s)
    {
        var k = new string?[s.ItemList.Count];
        for (int i = 0; i < k.Length; i++) k[i] = s.ItemList[i].IconOverride;
        return k;
    }

    static SidebarSectionSpec First(SidebarCustomLayout l, SidebarSectionKind kind)
    {
        for (int i = 0; i < l.Sections.Count; i++) if (l.Sections[i].Kind == kind) return l.Sections[i];
        throw new InvalidOperationException("no " + kind + " section in the template");
    }

    static int CountOf(SidebarCustomLayout l, SidebarSectionKind kind)
    {
        int n = 0;
        for (int i = 0; i < l.Sections.Count; i++) if (l.Sections[i].Kind == kind) n++;
        return n;
    }

    // ── C2.1 Wavee Curated ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Curated_HasExactSectionComposition()
    {
        var l = SidebarTemplates.Build(SidebarTemplates.Curated);
        Assert.Equal(SidebarTemplates.Curated, l.TemplateId);

        Assert.Equal(new[]
        {
            SidebarSectionKind.Pinned,
            SidebarSectionKind.Divider,
            SidebarSectionKind.JumpBackIn,
            SidebarSectionKind.Divider,
            SidebarSectionKind.CollectionShortcuts,
            SidebarSectionKind.Divider,
            SidebarSectionKind.PlaylistTree,
        }, Kinds(l));

        Assert.Equal("sidebar.pinned", l.Sections[0].TitleLocKey);
        Assert.True(l.Sections[0].Opts.ShowInRail);
        Assert.False(l.Sections[0].Opts.CountBadges);
        Assert.False(l.Sections[0].Opts.CollapsedByDefault);

        // §C1.8.8: Curated's Jump Back In ships as RECENTLY PLAYED, top 4, rail off.
        var jump = l.Sections[2];
        Assert.Equal(SidebarRecentsSource.Played, jump.Opts.Recents);
        Assert.Equal(4, jump.Opts.MaxItems);
        Assert.Equal(SidebarPresentation.Grid, jump.Opts.Presentation);
        Assert.Equal(2, jump.Opts.GridColumns);
        Assert.True(jump.Opts.Artwork);
        Assert.False(jump.Opts.ShowInRail);
        Assert.False(jump.Opts.Subtitles);
        Assert.Equal("sidebar.section.recentlyPlayed", jump.TitleLocKey);

        // Liked Songs FIRST — the ordering divergence from today's sidebar.
        var shortcuts = l.Sections[4];
        Assert.Equal("sidebar.yourLibrary", shortcuts.TitleLocKey);
        // Comfortable + Subtitles:false = the 44-DIP glyph row — pixel parity with Classic's locked document (R3).
        Assert.Equal(SidebarDensity.Comfortable, shortcuts.Opts.Density);
        Assert.True(shortcuts.Opts.CountBadges);
        Assert.True(shortcuts.Opts.ShowInRail);
        Assert.Equal(new[] { "liked", "albums", "artists", "podcasts", "local" }, Keys(shortcuts));
        Assert.Equal(new string?[] { "Heart", "Album", "Contact", "RadioTower", "Folder" }, IconNames(shortcuts));
        foreach (var it in shortcuts.ItemList) Assert.Equal(SidebarItemTarget.Route, it.Target);

        var tree = l.Sections[^1];
        Assert.Equal("sidebar.playlists", tree.TitleLocKey);
        Assert.True(tree.Opts.Subtitles);
        Assert.True(tree.Opts.ShowInRail);
    }

    // ── C2.2 Classic-inspired ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClassicInspired_MatchesTodaysIA()
    {
        var l = SidebarTemplates.Build(SidebarTemplates.ClassicInspired);
        Assert.Equal(SidebarTemplates.ClassicInspired, l.TemplateId);

        Assert.Equal(new[]
        {
            SidebarSectionKind.Pinned,
            SidebarSectionKind.CollectionShortcuts,
            SidebarSectionKind.Divider,
            SidebarSectionKind.PlaylistTree,
            SidebarSectionKind.Divider,
            SidebarSectionKind.StaticLinks,
        }, Kinds(l));

        // Classic's rail carries no pin tiles.
        Assert.False(First(l, SidebarSectionKind.Pinned).Opts.ShowInRail);

        Assert.Equal(new[] { "albums", "artists", "liked", "podcasts", "local" },
            Keys(First(l, SidebarSectionKind.CollectionShortcuts)));

        var links = First(l, SidebarSectionKind.StaticLinks);
        Assert.Single(links.ItemList);
        Assert.Equal("api-console", links.ItemList[0].Key);
        Assert.Equal("Code", links.ItemList[0].IconOverride);
        Assert.Equal(SidebarItemTarget.Route, links.ItemList[0].Target);

        Assert.True(First(l, SidebarSectionKind.PlaylistTree).Opts.Subtitles);
    }

    // ── C2.3 Library-inspired ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void V3Inspired_HasOneEntityListWithAllKindsRecents()
    {
        var l = SidebarTemplates.Build(SidebarTemplates.V3Inspired);
        Assert.Equal(SidebarTemplates.V3Inspired, l.TemplateId);
        Assert.Equal(1, CountOf(l, SidebarSectionKind.EntityList));

        var list = First(l, SidebarSectionKind.EntityList);
        Assert.Equal("sidebar.yourLibrary", list.TitleLocKey);
        var q = list.Query!;
        Assert.Equal(SidebarEntityKinds.All, q.Kinds);
        Assert.Equal(SidebarSortMode.Recents, q.Sort);
        Assert.True(q.Descending);
        Assert.Equal(SidebarPlaylistQualifier.Any, q.Qualifier);

        // §C1.8.8: the V3-inspired template's unified section ships InlineControls.
        Assert.True(list.Opts.InlineControls);
        Assert.True(list.Opts.Subtitles);
        Assert.True(list.Opts.ShowInRail);

        Assert.Equal(new[] { "home", "search" }, Keys(First(l, SidebarSectionKind.StaticLinks)));
        Assert.True(First(l, SidebarSectionKind.Pinned).Opts.ShowInRail);
    }

    // ── C2.4 / C2.5 ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Minimal_HasThreeShortcutsAndArtlessPlaylists()
    {
        var l = SidebarTemplates.Build(SidebarTemplates.Minimal);
        Assert.Equal(SidebarTemplates.Minimal, l.TemplateId);
        Assert.Equal(new[] { SidebarSectionKind.CollectionShortcuts, SidebarSectionKind.Divider,
            SidebarSectionKind.PlaylistTree }, Kinds(l));

        var sc = First(l, SidebarSectionKind.CollectionShortcuts);
        Assert.Equal(new[] { "liked", "albums", "artists" }, Keys(sc));
        Assert.False(sc.Opts.CountBadges);
        Assert.Equal(SidebarDensity.Compact, sc.Opts.Density);

        var tree = First(l, SidebarSectionKind.PlaylistTree);
        Assert.False(tree.Opts.Artwork);
        Assert.False(tree.Opts.Subtitles);
        Assert.Equal(SidebarDensity.Compact, tree.Opts.Density);
    }

    [Fact]
    public void Blank_IsEmpty()
    {
        var l = SidebarTemplates.Build(SidebarTemplates.Blank);
        Assert.Equal(SidebarTemplates.Blank, l.TemplateId);
        Assert.Empty(l.Sections);
        Assert.Equal(0, l.SectionCount);
    }

    // ── cross-template invariants ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void All_ListsEveryTemplateOnceInPaletteOrder()
    {
        Assert.Equal(new[] { SidebarTemplates.Curated, SidebarTemplates.ClassicInspired,
            SidebarTemplates.V3Inspired, SidebarTemplates.Minimal, SidebarTemplates.Blank }, SidebarTemplates.All);
        Assert.Equal(SidebarTemplates.All.Length, new HashSet<string>(SidebarTemplates.All, StringComparer.Ordinal).Count);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void AllTemplates_HaveUniqueIds(string templateId)
    {
        var ids = SidebarLayoutCompare.AllIds(SidebarTemplates.Build(templateId));
        Assert.Equal(ids.Count, new HashSet<string>(ids, StringComparer.Ordinal).Count);
        foreach (var id in ids)
            Assert.True(id.StartsWith(SidebarIds.SectionPrefix, StringComparison.Ordinal) ||
                        id.StartsWith(SidebarIds.ItemPrefix, StringComparison.Ordinal), id);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Build_TwiceYieldsDifferentIds_ButEqualStructure(string templateId)
    {
        var a = SidebarTemplates.Build(templateId);
        var b = SidebarTemplates.Build(templateId);

        Assert.True(SidebarLayoutCompare.EqualIgnoringIds(a, b),
            SidebarLayoutCompare.FirstDifference(a, b, ignoreIds: true));

        var idsA = SidebarLayoutCompare.AllIds(a);
        if (idsA.Count > 0)
        {
            Assert.NotEqual(idsA, SidebarLayoutCompare.AllIds(b));
            Assert.False(SidebarLayoutCompare.Equal(a, b));
        }
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void AllTemplates_AreStructurallyEqualToThemselves(string templateId)
    {
        var a = SidebarTemplates.Build(templateId);
        Assert.True(SidebarLayoutCompare.Equal(a, a));
        Assert.Null(SidebarLayoutCompare.FirstDifference(a, a));
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void EveryTemplateSection_CarriesALocalizedTitleOrIsChrome(string templateId)
    {
        foreach (var s in SidebarTemplates.Build(templateId).Sections)
        {
            // Templates never author a LITERAL title — a template-seeded name must follow the UI culture.
            Assert.Null(s.Title);
            if (s.Kind is SidebarSectionKind.Divider or SidebarSectionKind.StaticLinks) continue;
            Assert.False(string.IsNullOrEmpty(s.TitleLocKey));
            Assert.StartsWith("sidebar.", s.TitleLocKey, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UnknownTemplateId_BuildsCurated()
    {
        foreach (var bad in new string?[] { null, "", "nope", "CURATED" })
        {
            var l = SidebarTemplates.Build(bad);
            Assert.Equal(SidebarTemplates.Curated, l.TemplateId);
            Assert.True(SidebarLayoutCompare.EqualIgnoringIds(l, SidebarTemplates.Build(SidebarTemplates.Curated)));
            Assert.False(SidebarTemplates.IsKnown(bad));
        }
        foreach (var good in SidebarTemplates.All) Assert.True(SidebarTemplates.IsKnown(good));
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void NameAndDescriptionKeys_AreNamespacedAndDistinct(string templateId)
    {
        var name = SidebarTemplates.NameLocKey(templateId);
        var sub = SidebarTemplates.DescriptionLocKey(templateId);
        Assert.Equal("sidebar.template." + templateId, name);
        Assert.Equal(name + "Sub", sub);
        Assert.NotEqual(name, sub);
    }

    [Fact]
    public void UnknownTemplateId_FallsBackToTheCuratedLocKeys()
    {
        Assert.Equal("sidebar.template.curated", SidebarTemplates.NameLocKey("nope"));
        Assert.Equal("sidebar.template.curatedSub", SidebarTemplates.DescriptionLocKey(null));
    }

    // ── kind defaults the templates and the palette both lean on ──────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(KnownKinds))]
    public void EveryKind_HasADefaultTitleLocKey_ExceptDivider(SidebarSectionKind kind)
    {
        var key = SidebarSectionKinds.DefaultTitleLocKey(kind);
        if (kind == SidebarSectionKind.Divider) { Assert.Null(key); return; }
        Assert.False(string.IsNullOrEmpty(key));
        Assert.StartsWith("sidebar.", key, StringComparison.Ordinal);

        var palette = SidebarSectionKinds.PaletteNameLocKey(kind);
        Assert.False(string.IsNullOrEmpty(palette));
        Assert.Equal(palette + "Sub", SidebarSectionKinds.PaletteDescriptionLocKey(kind));
    }

    [Fact]
    public void JumpBackInDefaultTitle_FollowsItsRecentsSource()
    {
        Assert.Equal("sidebar.section.jumpBackIn",
            SidebarSectionKinds.DefaultTitleLocKey(SidebarSectionKind.JumpBackIn, SidebarRecentsSource.Visited));
        Assert.Equal("sidebar.section.recentlyPlayed",
            SidebarSectionKinds.DefaultTitleLocKey(SidebarSectionKind.JumpBackIn, SidebarRecentsSource.Played));
    }

    [Fact]
    public void FeedKinds_ShipTheirSpecdTopN()
    {
        Assert.Equal(4, SidebarSectionKinds.DefaultDisplay(SidebarSectionKind.NewReleases).MaxItems);
        Assert.Equal(3, SidebarSectionKinds.DefaultDisplay(SidebarSectionKind.Concerts).MaxItems);
        Assert.Equal(SidebarDisplayOptions.Shortcuts,
            SidebarSectionKinds.DefaultDisplay(SidebarSectionKind.CollectionShortcuts));
        // DEFECT 8 — StaticLinks has its OWN preset (`Links`), which is `Shortcuts with { CountBadges = false }`. The
        // two kinds shared one preset, so every Links section was seeded a count flag `AllowsDisplayField` forbids: the
        // panel never offered it and `SetDisplayOption` rejected it, yet the row renderer honoured it.
        Assert.Equal(SidebarDisplayOptions.Links,
            SidebarSectionKinds.DefaultDisplay(SidebarSectionKind.StaticLinks));
        Assert.NotEqual(SidebarDisplayOptions.Shortcuts,
            SidebarSectionKinds.DefaultDisplay(SidebarSectionKind.StaticLinks));
    }

    [Fact]
    public void IconWhitelist_IsOrderedStableAndClosed()
    {
        Assert.Equal(30, SidebarIconNames.Allowed.Length);
        Assert.Equal("MusicNote", SidebarIconNames.Allowed[0]);
        Assert.Equal("Download", SidebarIconNames.Allowed[^1]);
        Assert.Equal(SidebarIconNames.Allowed.Length,
            new HashSet<string>(SidebarIconNames.Allowed, StringComparer.Ordinal).Count);

        Assert.True(SidebarIconNames.IsAllowed("Heart"));
        Assert.False(SidebarIconNames.IsAllowed("heart"));      // ordinal, on purpose
        Assert.False(SidebarIconNames.IsAllowed("Nope"));
        Assert.False(SidebarIconNames.IsAllowed(null));
        Assert.False(SidebarIconNames.IsAllowed(""));

        // Every glyph a template authors must be whitelisted, or the reducer would reject the user's own default layout.
        foreach (var t in SidebarTemplates.All)
            foreach (var s in SidebarTemplates.Build(t).Sections)
                foreach (var it in s.ItemList)
                    if (it.IconOverride is not null) Assert.True(SidebarIconNames.IsAllowed(it.IconOverride),
                        it.IconOverride);
    }

    public static TheoryData<string> TemplateIds()
    {
        var d = new TheoryData<string>();
        foreach (var t in SidebarTemplates.All) d.Add(t);
        return d;
    }

    public static TheoryData<SidebarSectionKind> KnownKinds()
    {
        var d = new TheoryData<SidebarSectionKind>();
        for (byte b = 0; b <= SidebarSectionKinds.MaxKnown; b++) d.Add((SidebarSectionKind)b);
        return d;
    }
}
