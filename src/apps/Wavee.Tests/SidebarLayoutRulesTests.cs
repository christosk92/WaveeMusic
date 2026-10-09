// ── Wavee.Tests/SidebarLayoutRulesTests.cs — every layout op, the merge with the catalogue, IsModified and Resolve ─────
//
// docs/plans/wavee/sidebar-rework-implementation.md §P3.2-§P3.3 and §P3.15 (design A.2, C.1, corner cases). A refused op
// changes nothing; a collapse is a live toggle and never a modification; the merge reads a file and never refuses it.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class SidebarLayoutRulesTests
{
    static SidebarLayoutState S() => SidebarLayoutState.Default;

    static SidebarOpResult Apply(SidebarOp op, bool pinnedLocked = false) => SidebarLayoutRules.Apply(S(), op, pinnedLocked);

    static SidebarLayoutState Then(SidebarLayoutState s, SidebarOp op) => SidebarLayoutRules.Apply(s, op, pinnedLocked: false).State;

    static SectionState Sec(SidebarLayoutState s, SidebarLayoutId layout, string id) => s.Of(layout).Find(id)!;

    static string[] IdsOf(LayoutOverlay overlay)
    {
        var ids = new string[overlay.Sections.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = overlay.Sections[i].Id;
        return ids;
    }

    static SectionState St(string id, bool hidden = false, int? limit = null) => new(id, Hidden: hidden, Limit: limit);

    [Fact]
    public void Hide_Collections_SetsHidden_AndIsModified()
    {
        var r = Apply(new SetSectionShown(SidebarLayoutId.Classic, "collections", false));
        Assert.True(r.Changed);
        Assert.True(Sec(r.State, SidebarLayoutId.Classic, "collections").Hidden);
        Assert.True(SidebarLayoutRules.IsModified(r.State.Classic));
    }

    [Fact]
    public void Hide_Home_Playlists_Library_Refused_Locked()
    {
        var home = Apply(new SetSectionShown(SidebarLayoutId.Classic, "home", false));
        var playlists = Apply(new SetSectionShown(SidebarLayoutId.Classic, "playlists", false));
        var library = Apply(new SetSectionShown(SidebarLayoutId.Library, "library", false));
        foreach (var r in new[] { home, playlists, library })
        {
            Assert.False(r.Changed);
            Assert.Equal(SidebarOpReject.Locked, r.Reject);
        }
    }

    [Fact]
    public void Hide_Pinned_RefusedWhileLocked()
    {
        var r = Apply(new SetSectionShown(SidebarLayoutId.Classic, "pinned", false), pinnedLocked: true);
        Assert.False(r.Changed);
        Assert.Equal(SidebarOpReject.Locked, r.Reject);
    }

    [Fact]
    public void Hide_Pinned_AllowedWhenNotLocked()
    {
        var r = Apply(new SetSectionShown(SidebarLayoutId.Classic, "pinned", false), pinnedLocked: false);
        Assert.True(r.Changed);
        Assert.True(Sec(r.State, SidebarLayoutId.Classic, "pinned").Hidden);
    }

    [Fact]
    public void Show_Recent_DropsOnlyTheHiddenBit()
    {
        var limited = Then(S(), new SetSectionLimit(SidebarLayoutId.Classic, "recent", 20));
        var r = SidebarLayoutRules.Apply(limited, new SetSectionShown(SidebarLayoutId.Classic, "recent", true), false);
        Assert.True(r.Changed);
        var recent = Sec(r.State, SidebarLayoutId.Classic, "recent");
        Assert.False(recent.Hidden);
        Assert.Equal(20, recent.Limit);
        Assert.Null(recent.HiddenItems);
    }

    [Fact]
    public void Move_Classic_ReordersMovableSlots_LockedKeepTheirPlace()
    {
        var r = Apply(new MoveSection(SidebarLayoutId.Classic, "newReleases", 0));
        Assert.True(r.Changed);
        Assert.Equal(
            new[] { "home", "newReleases", "pinned", "collections", "playlists", "recent", "settings" },
            IdsOf(r.State.Classic));
    }

    [Fact]
    public void Move_OutOfRange_Refused()
    {
        Assert.Equal(SidebarOpReject.OutOfRange, Apply(new MoveSection(SidebarLayoutId.Classic, "pinned", 5)).Reject);
        Assert.Equal(SidebarOpReject.OutOfRange, Apply(new MoveSection(SidebarLayoutId.Classic, "pinned", -1)).Reject);
    }

    [Fact]
    public void Move_InLibrary_Refused_NotMovable()
    {
        var r = Apply(new MoveSection(SidebarLayoutId.Library, "pinned", 0));
        Assert.False(r.Changed);
        Assert.Equal(SidebarOpReject.NotMovable, r.Reject);
    }

    [Fact]
    public void Collapse_NotRecordedAsModified()
    {
        var r = Apply(new SetSectionCollapsed(SidebarLayoutId.Classic, "pinned", true));
        Assert.True(r.Changed);
        Assert.True(Sec(r.State, SidebarLayoutId.Classic, "pinned").Collapsed);
        Assert.False(SidebarLayoutRules.IsModified(r.State.Classic));
    }

    [Fact]
    public void Collapse_Home_Refused()
    {
        var r = Apply(new SetSectionCollapsed(SidebarLayoutId.Classic, "home", true));
        Assert.False(r.Changed);
        Assert.Equal(SidebarOpReject.Locked, r.Reject);
    }

    [Fact]
    public void Limit_Accepts5_10_20_Only()
    {
        foreach (var limit in new[] { 5, 10, 20 })
            Assert.NotEqual(SidebarOpReject.BadLimit, Apply(new SetSectionLimit(SidebarLayoutId.Classic, "newReleases", limit)).Reject);
        foreach (var limit in new[] { 0, 7, 25 })
        {
            var r = Apply(new SetSectionLimit(SidebarLayoutId.Classic, "recent", limit));
            Assert.False(r.Changed);
            Assert.Equal(SidebarOpReject.BadLimit, r.Reject);
        }
        Assert.Equal(SidebarOpReject.BadLimit, Apply(new SetSectionLimit(SidebarLayoutId.Classic, "pinned", 5)).Reject);
    }

    [Fact]
    public void HideItem_AddsToHiddenItems()
    {
        var r = Apply(new SetItemShown(SidebarLayoutId.Classic, "collections", "albums", false));
        Assert.True(r.Changed);
        var collections = Sec(r.State, SidebarLayoutId.Classic, "collections");
        Assert.Contains("albums", collections.HiddenList);
        Assert.False(collections.Hidden);
    }

    [Fact]
    public void HideLastItem_HidesSection_KeepsPreviousHiddenItems()
    {
        var s = S();
        foreach (var item in new[] { "liked", "albums", "artists", "podcasts" })
            s = Then(s, new SetItemShown(SidebarLayoutId.Classic, "collections", item, false));
        var r = SidebarLayoutRules.Apply(s, new SetItemShown(SidebarLayoutId.Classic, "collections", "audiobooks", false), false);
        Assert.True(r.Changed);
        var collections = Sec(r.State, SidebarLayoutId.Classic, "collections");
        Assert.True(collections.Hidden);
        Assert.Equal(4, collections.HiddenList.Count);
    }

    [Fact]
    public void ShowSection_AfterAutoHide_RestoresTheLastItem()
    {
        var s = S();
        foreach (var item in new[] { "liked", "albums", "artists", "podcasts", "audiobooks" })
            s = Then(s, new SetItemShown(SidebarLayoutId.Classic, "collections", item, false));
        Assert.True(Sec(s, SidebarLayoutId.Classic, "collections").Hidden);

        s = Then(s, new SetSectionShown(SidebarLayoutId.Classic, "collections", true));
        var collections = SidebarLayoutRules.Resolve(s, SidebarLayoutId.Classic, SidebarDensity.Default)
            .Find(SidebarSectionKind.Collections)!;
        Assert.False(collections.Hidden);
        Assert.Equal(new[] { "audiobooks" }, collections.Items.ToArray());
    }

    [Fact]
    public void HideItem_Unknown_Refused()
    {
        var collections = Apply(new SetItemShown(SidebarLayoutId.Classic, "collections", "spotlight", false));
        Assert.False(collections.Changed);
        Assert.Equal(SidebarOpReject.UnknownItem, collections.Reject);

        var library = Apply(new SetItemShown(SidebarLayoutId.Library, "library", "liked", false));
        Assert.False(library.Changed);
        Assert.Equal(SidebarOpReject.UnknownItem, library.Reject);
    }

    [Fact]
    public void MoveItem_StoresOrder_NullWhenBackToDefault()
    {
        var moved = Apply(new MoveItem(SidebarLayoutId.Classic, "collections", "albums", 0));
        Assert.True(moved.Changed);
        Assert.NotNull(Sec(moved.State, SidebarLayoutId.Classic, "collections").ItemOrder);
        Assert.Equal(new[] { "albums", "liked", "artists", "podcasts", "audiobooks" },
            SidebarLayoutRules.EffectiveItemOrder(SidebarLayoutId.Classic, SidebarSectionKind.Collections,
                Sec(moved.State, SidebarLayoutId.Classic, "collections")).ToArray());

        var back = SidebarLayoutRules.Apply(moved.State, new MoveItem(SidebarLayoutId.Classic, "collections", "albums", 1), false);
        Assert.True(back.Changed);
        Assert.Null(Sec(back.State, SidebarLayoutId.Classic, "collections").ItemOrder);
    }

    [Fact]
    public void LibrarySort_View_Liked_Persist_AndCountAsModified()
    {
        var sort = Apply(new SetLibrarySort(SidebarLibrarySort.Alphabetical, false));
        var view = Apply(new SetLibraryView(SidebarLibraryView.Grid));
        var liked = Apply(new SetShowLiked(false));
        foreach (var r in new[] { sort, view, liked })
        {
            Assert.True(r.Changed);
            Assert.True(SidebarLayoutRules.IsModified(r.State.Library));
        }

        var options = SidebarLayoutRules.Resolve(sort.State, SidebarLayoutId.Library, SidebarDensity.Default).Library;
        Assert.Equal(SidebarLibrarySort.Alphabetical, options.Sort);
        Assert.False(options.Descending);
        Assert.Equal(SidebarLibraryView.Grid, SidebarLayoutRules.Resolve(view.State, SidebarLayoutId.Library,
            SidebarDensity.Default).Library.View);
        Assert.False(SidebarLayoutRules.Resolve(liked.State, SidebarLayoutId.Library, SidebarDensity.Default).Library.ShowLiked);
    }

    [Fact]
    public void Reset_KeepsCollapse_DropsEverythingElse()
    {
        var s = Then(S(), new SetSectionCollapsed(SidebarLayoutId.Classic, "pinned", true));
        s = Then(s, new SetSectionShown(SidebarLayoutId.Classic, "collections", false));
        s = Then(s, new SetSectionLimit(SidebarLayoutId.Classic, "recent", 20));

        var r = SidebarLayoutRules.Apply(s, new ResetLayout(SidebarLayoutId.Classic), false);
        Assert.True(r.Changed);
        Assert.True(Sec(r.State, SidebarLayoutId.Classic, "pinned").Collapsed);
        Assert.False(Sec(r.State, SidebarLayoutId.Classic, "collections").Hidden);
        Assert.Equal(5, Sec(r.State, SidebarLayoutId.Classic, "recent").Limit);
        Assert.True(Sec(r.State, SidebarLayoutId.Classic, "recent").Hidden);
    }

    [Fact]
    public void Replace_MergesAndSanitizes()
    {
        var replaced = new LayoutOverlay(SidebarLayoutId.Classic, new[]
        {
            St("home"), St("pinned"), St("collections"), St("spotlight"), St("playlists"),
            St("recent", hidden: true, limit: 7), St("newReleases", hidden: true, limit: 10), St("settings"),
        });
        var dropped = new List<string>();
        var merged = SidebarLayoutRules.MergeWithCatalogue(replaced, dropped);
        Assert.Equal(new[] { "spotlight" }, dropped.ToArray());
        Assert.Equal(5, merged.Find("recent")!.Limit);
        Assert.Equal(10, merged.Find("newReleases")!.Limit);
        Assert.True(merged.Find("recent")!.Hidden);
    }

    [Fact]
    public void Merge_DropsUnknown_ReportsIt()
    {
        var overlay = new LayoutOverlay(SidebarLayoutId.Library, new[]
        {
            St("home"), St("pinned"), St("bogus"), St("library"), St("settings"),
        });
        var dropped = new List<string>();
        var merged = SidebarLayoutRules.MergeWithCatalogue(overlay, dropped);
        Assert.Equal(new[] { "bogus" }, dropped.ToArray());
        Assert.Equal(new[] { "home", "pinned", "library", "settings" }, IdsOf(merged));
    }

    [Fact]
    public void Merge_InsertsMissingAfterPrevious()
    {
        var overlay = new LayoutOverlay(SidebarLayoutId.Classic, new[]
        {
            St("home"), St("pinned"), St("playlists"), St("recent"), St("newReleases"), St("settings"),
        });
        var merged = SidebarLayoutRules.MergeWithCatalogue(overlay, null);
        Assert.Equal(
            new[] { "home", "pinned", "collections", "playlists", "recent", "newReleases", "settings" },
            IdsOf(merged));
        Assert.False(merged.Find("collections")!.Hidden);
    }

    [Fact]
    public void Merge_HomeFirst_LibraryLast_SettingsLast()
    {
        var classic = SidebarLayoutRules.MergeWithCatalogue(new LayoutOverlay(SidebarLayoutId.Classic, new[]
        {
            St("settings"), St("pinned"), St("collections"), St("playlists"), St("recent"), St("newReleases"), St("home"),
        }), null);
        Assert.Equal(
            new[] { "home", "pinned", "collections", "playlists", "recent", "newReleases", "settings" },
            IdsOf(classic));

        var library = SidebarLayoutRules.MergeWithCatalogue(new LayoutOverlay(SidebarLayoutId.Library, new[]
        {
            St("settings"), St("library"), St("pinned"), St("home"),
        }), null);
        Assert.Equal(new[] { "home", "pinned", "library", "settings" }, IdsOf(library));
    }

    [Fact]
    public void Merge_DropsDuplicateIds()
    {
        var merged = SidebarLayoutRules.MergeWithCatalogue(new LayoutOverlay(SidebarLayoutId.Classic, new[]
        {
            St("home"), St("pinned"), St("pinned"), St("collections"), St("playlists"), St("recent"), St("newReleases"), St("settings"),
        }), null);
        Assert.Single(merged.Sections, s => s.Id == "pinned");
        Assert.Equal(SidebarCatalogue.KindsOf(SidebarLayoutId.Classic).Count, merged.Sections.Count);
    }

    [Fact]
    public void Merge_FiltersHiddenItemsToCatalogue()
    {
        var collections = new SectionState("collections", HiddenItems: new[] { "albums", "bogus", "albums" });
        var overlay = SidebarLayoutRules.MergeWithCatalogue(
            SidebarLayoutState.Default.Classic with
            {
                Sections = SidebarLayoutState.Default.Classic.Sections
                    .Select(s => s.Id == "collections" ? collections : s).ToArray(),
            }, null);
        Assert.Equal(new[] { "albums" }, overlay.Find("collections")!.HiddenList.ToArray());
    }

    [Fact]
    public void Merge_HiddenItemsDedupedAndCatalogueBound()
    {
        // The catalogue holds five collection items, so a merged hidden list never grows past five entries: repeats and
        // unknown ids are dropped before the 64-entry cap could ever apply.
        var repeated = new string[200];
        for (int i = 0; i < repeated.Length; i++) repeated[i] = i % 2 == 0 ? "albums" : "bogus";
        var overlay = SidebarLayoutState.Default.Classic with
        {
            Sections = SidebarLayoutState.Default.Classic.Sections
                .Select(s => s.Id == "collections" ? s with { HiddenItems = repeated } : s).ToArray(),
        };
        var merged = SidebarLayoutRules.MergeWithCatalogue(overlay, null);
        Assert.Equal(new[] { "albums" }, merged.Find("collections")!.HiddenList.ToArray());
    }

    [Fact]
    public void IsModified_DefaultFalse_OrderTrue_HiddenItemTrue_ItemOrderTrue()
    {
        Assert.False(SidebarLayoutRules.IsModified(SidebarLayoutState.Default.Classic));
        Assert.False(SidebarLayoutRules.IsModified(SidebarLayoutState.Default.Library));

        Assert.True(SidebarLayoutRules.IsModified(Apply(new MoveSection(SidebarLayoutId.Classic, "recent", 0)).State.Classic));
        Assert.True(SidebarLayoutRules.IsModified(
            Apply(new SetItemShown(SidebarLayoutId.Classic, "collections", "albums", false)).State.Classic));
        Assert.True(SidebarLayoutRules.IsModified(
            Apply(new MoveItem(SidebarLayoutId.Classic, "collections", "audiobooks", 0)).State.Classic));
    }

    [Fact]
    public void Resolve_ClassicShapes_AreTextUnlessShowCovers()
    {
        // Classic ignores density: its entity rows are text-only (28) by default, one-line with a cover when covers are on.
        var def = SidebarLayoutRules.Resolve(S(), SidebarLayoutId.Classic, SidebarDensity.Default);
        Assert.Equal(SidebarRowShape.Text, def.Find(SidebarSectionKind.Pinned)!.Shape);
        Assert.Equal(SidebarRowShape.Text, def.Find(SidebarSectionKind.Playlists)!.Shape);
        Assert.Equal(SidebarRowShape.Glyph, def.Find(SidebarSectionKind.Collections)!.Shape);

        var compact = SidebarLayoutRules.Resolve(S(), SidebarLayoutId.Classic, SidebarDensity.Compact, classicCovers: true);
        Assert.Equal(SidebarRowShape.EntityOneLine, compact.Find(SidebarSectionKind.Pinned)!.Shape);
        Assert.Equal(SidebarRowShape.Glyph, compact.Find(SidebarSectionKind.Collections)!.Shape);

        var covers = SidebarLayoutRules.Resolve(S(), SidebarLayoutId.Classic, SidebarDensity.Default, classicCovers: true);
        Assert.Equal(SidebarRowShape.EntityOneLine, covers.Find(SidebarSectionKind.Pinned)!.Shape);
    }

    [Fact]
    public void Resolve_LibraryShapes_FollowDensity()
    {
        var def = SidebarLayoutRules.Resolve(S(), SidebarLayoutId.Library, SidebarDensity.Default, classicCovers: false);
        Assert.Equal(SidebarRowShape.EntityTwoLine, def.Find(SidebarSectionKind.Pinned)!.Shape);

        var compact = SidebarLayoutRules.Resolve(S(), SidebarLayoutId.Library, SidebarDensity.Compact);
        Assert.Equal(SidebarRowShape.EntityOneLine, compact.Find(SidebarSectionKind.Pinned)!.Shape);
        Assert.Equal(SidebarRowShape.Glyph, compact.Find(SidebarSectionKind.Home)!.Shape);
    }

    [Fact]
    public void ForRail_RestoresTheDensityShape()
    {
        var text = SidebarLayoutRules.Resolve(S(), SidebarLayoutId.Classic, SidebarDensity.Default).Find(SidebarSectionKind.Pinned)!;
        Assert.Equal(SidebarRowShape.Text, text.Shape);
        Assert.Equal(SidebarRowShape.EntityTwoLine, SidebarSection.ForRail(text, SidebarDensity.Default).Shape);
        Assert.Equal(SidebarRowShape.EntityOneLine, SidebarSection.ForRail(text, SidebarDensity.Compact).Shape);

        var glyph = SidebarLayoutRules.Resolve(S(), SidebarLayoutId.Classic, SidebarDensity.Default).Find(SidebarSectionKind.Collections)!;
        Assert.Same(glyph, SidebarSection.ForRail(glyph, SidebarDensity.Default));
    }

    [Fact]
    public void Two_Classic_docs_differing_only_in_covers_are_not_the_same_document()
    {
        var text = SidebarLayoutRules.Resolve(S(), SidebarLayoutId.Classic, SidebarDensity.Default);
        var covers = SidebarLayoutRules.Resolve(S(), SidebarLayoutId.Classic, SidebarDensity.Default, classicCovers: true);
        Assert.False(covers.SameExceptCollapsed(text));
        Assert.False(text.SameExceptCollapsed(covers));
    }

    [Fact]
    public void Resolve_LibraryHiddenKinds_Flags()
    {
        var s = Then(S(), new SetItemShown(SidebarLayoutId.Library, "library", "podcasts", false));
        s = Then(s, new SetItemShown(SidebarLayoutId.Library, "library", "audiobooks", false));
        var options = SidebarLayoutRules.Resolve(s, SidebarLayoutId.Library, SidebarDensity.Default).Library;
        Assert.Equal(SidebarLibraryKinds.Podcasts | SidebarLibraryKinds.Audiobooks, options.HiddenKinds);
    }

    [Fact]
    public void Resolve_VisibleItems_InEffectiveOrder()
    {
        var s = Then(S(), new MoveItem(SidebarLayoutId.Classic, "collections", "audiobooks", 0));
        s = Then(s, new SetItemShown(SidebarLayoutId.Classic, "collections", "albums", false));
        var collections = SidebarLayoutRules.Resolve(s, SidebarLayoutId.Classic, SidebarDensity.Default)
            .Find(SidebarSectionKind.Collections)!;
        Assert.Equal(new[] { "audiobooks", "liked", "artists", "podcasts" }, collections.Items.ToArray());
    }

    static SidebarLayoutDoc Doc(SidebarLayoutState s, SidebarDensity density = SidebarDensity.Default)
        => SidebarLayoutRules.Resolve(s, SidebarLayoutId.Classic, density);

    [Fact]
    public void A_collapse_is_the_same_document_for_the_rows_that_stay()
    {
        string playlists = SidebarCatalogue.IdOf(SidebarSectionKind.Playlists);
        var before = Doc(S());
        var after = Doc(Then(S(), new SetSectionCollapsed(SidebarLayoutId.Classic, playlists, true)));
        Assert.NotSame(before, after);
        Assert.True(after.SameExceptCollapsed(before));
        Assert.True(before.SameExceptCollapsed(after));
    }

    [Fact]
    public void Hiding_a_section_limiting_it_or_a_new_density_is_a_different_document()
    {
        string collections = SidebarCatalogue.IdOf(SidebarSectionKind.Collections);
        var before = Doc(S());
        Assert.False(Doc(Then(S(), new SetSectionShown(SidebarLayoutId.Classic, collections, false))).SameExceptCollapsed(before));
        Assert.False(Doc(S(), SidebarDensity.Compact).SameExceptCollapsed(before));
        string recent = SidebarCatalogue.IdOf(SidebarSectionKind.Recent);
        var shown = Then(S(), new SetSectionShown(SidebarLayoutId.Classic, recent, true));
        Assert.False(Doc(Then(shown, new SetSectionLimit(SidebarLayoutId.Classic, recent, SidebarCatalogue.DefaultLimit(SidebarSectionKind.Recent) == 20 ? 10 : 20))).SameExceptCollapsed(Doc(shown)));
    }
}
