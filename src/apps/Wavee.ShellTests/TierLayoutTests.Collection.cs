// ── Wavee.ShellTests/TierLayoutTests.Collection.cs — the collection pages at every tier size and threshold edge (themes plan 3.5; TC7) ─────────────
//
// The Albums library (a list and a grid, at 3840 too: a key surface) and the Artists library (the reader's kind), Recents, and Local files, each mounted through the
// real Classic frame at the plan 3.5 sizes and every threshold edge, through `AssertPage` (TierLayoutTests.cs: the tier final in the first frame, the frame's records,
// the regions inside the window and apart, the Compact title-bar targets). On top, on the records and the node geometry, never on pixels:
//
//   · the library's arm is the layout ladder's answer for the page's own width: the one-column drill-in (the kind chips over the root rung, no breadcrumb at depth 0,
//     the Compact head's controls and chips all >= 40) below the split edge, the navigator beside the pane or reader above it, only that arm mounted;
//   · the record in use is the structure's (`ClassicLibraryTiers.Of`), and the split's pane keeps the record's floor while the navigator never exceeds its
//     remembered width;
//   · THE PERSISTED COLUMN WIDTH IS NEVER WRITTEN BY A TIER: a width of 420 stored before the mount (a tier that hides the column, or squeezes it, must leave it) reads
//     420 after;
//   · WHILE THE USER NEVER DRAGGED THE GRIP (contract 1.21) the navigator rests at the tier's width (C-Library-Huge 560, C-LibraryList-Huge 400, Small's 240 for every kind,
//     contract 1.22; the key's 340 / 280 at Standard) and the store stays unset; the sidebar likewise rests at 320 from Huge on (C-Shell-Huge) while never resized, its Small /
//     Standard width unchanged;
//   · the Huge record tints the navigator and docks the album pane's "Also by" aside: its slot is in the pane's row at every tier, 0 wide below Huge, never past the card;
//     the Small record's grid floor is the policy's (96) and nowhere else;
//   · Recents' record is the content tier's, its kind pivots carry the record's tier and are 40 tall at Compact; at Huge the column is capped at the 1100 list (plus the
//     gutters) and the Overview aside stands beside it inside the card, 0 wide below Huge;
//   · Local files (an ordinary playlist page: no board in any theme, owner Q18) holds the playlist family's tier layout.

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class TierLayoutTests
{
    // ── the fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────

    public static readonly TierPage LibraryAlbums = new("library-albums", new Shell.Route(Shell.RouteKind.LibraryAlbums), "lib:");
    public static readonly TierPage LibraryArtists = new("library-artists", new Shell.Route(Shell.RouteKind.LibraryArtists), "lib:");
    public static readonly TierPage RecentsPage = new("recents", new Shell.Route(Shell.RouteKind.Recents), "recents-pivots:");
    public static readonly TierPage LocalFiles = new("local-files", new Shell.Route(Shell.RouteKind.Local), "detail:arm:");

    /// <summary>The remembered navigator width every library fact stores before it mounts: inside the grip's range, so the clamp changes nothing.</summary>
    const float StoredLeftW = 420f;

    /// <summary>Store the view code and the navigator width of <paramref name="kind"/>, run <paramref name="body"/>, put the kind's state back.</summary>
    void WithLibraryState(string kind, int view, Action body)
    {
        LibraryViewState saved = default;
        host.Run(() =>
        {
            saved = LibraryViewPrefs.Read(kind);
            LibraryViewPrefs.Save(kind, saved with { View = view }, artists: false);
            LibraryViewPrefs.SetLeftW(kind, StoredLeftW);
        });
        try { body(); }
        finally
        {
            host.Run(() =>
            {
                LibraryViewPrefs.Save(kind, saved, artists: false);
                LibraryViewPrefs.SetLeftW(kind, saved.LeftW);
            });
        }
    }

    // ── the library ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(FrameSizes))]
    public void The_albums_library_as_a_list_holds_its_tier_layout(string size)
        => WithLibraryState("albums", view: 1, () => AssertPage(LibraryAlbums, size, (shell, tiers) => AssertLibrary(shell, tiers, size, "albums", view: 1)));

    [Theory, MemberData(nameof(PageSizes))]
    public void The_albums_library_as_a_grid_holds_its_tier_layout(string size)
        => WithLibraryState("albums", view: 3, () => AssertPage(LibraryAlbums, size, (shell, tiers) => AssertLibrary(shell, tiers, size, "albums", view: 3)));

    [Theory, MemberData(nameof(PageSizes))]
    public void The_artists_library_holds_its_tier_layout(string size)
        => WithLibraryState("artists", view: 1, () => AssertPage(LibraryArtists, size, (shell, tiers) => AssertLibrary(shell, tiers, size, "artists", view: 1)));

    static void AssertLibrary(Shell.HeadlessShell shell, TierFacts tiers, string sizeName, string kind, int view)
    {
        var size = Sizes[sizeName];
        var scene = shell.Host.Scene;
        var card = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.ContentCardKey));

        // ONE arm, the layout ladder's answer for the page's own width (a fresh mount: the edge itself).
        bool single = !shell.Find("lib:collapsed").IsNull;
        Assert.True(shell.Find("lib:wide").IsNull == single, $"{size.Name}: exactly one arm is mounted (collapsed {single})");
        Assert.True(single == (card.W < ClassicLibraryTiers.SplitFrom), $"{size.Name}: a {card.W}-wide page is {(single ? "one column" : "split")}");

        // The record in use is the structure's for that answer.
        var layout = ClassicLibraryTiers.Of(tiers, single);
        Assert.Equal(single, layout.Single);
        Assert.Equal(single, layout.KindChips);
        Assert.Equal(ClassicTiers.PaneInset, layout.PaneInset);
        Assert.True(shell.Find("lib:kinds").IsNull == !single, $"{size.Name}: the kind chips stand over the one-column head only");
        var expected = single ? WidthTier.Compact : tiers.Content switch { WidthTier.Compact or WidthTier.Small => WidthTier.Small, WidthTier.Standard => WidthTier.Standard, _ => WidthTier.Huge };
        Assert.Equal(expected, layout.Tier);
        // C-Library-Huge: the tinted navigator and the album pane's aside are Huge's only; the grid's floor is Small's 96 (contract 1.22), the policy's for the same tier.
        Assert.Equal(layout.Tier == WidthTier.Huge, layout.NavTinted);
        Assert.Equal(layout.Tier == WidthTier.Huge, layout.AlsoByDocked);
        Assert.Equal(layout.Tier == WidthTier.Small ? ClassicLibraryTiers.SmallCardCellMin : LibraryNavMount.CardCellMinFull, layout.CardCellMin);
        Assert.Equal(tiers.Content == WidthTier.Small, float.IsFinite(ClassicLibraryTiers.Policy.CardCellMinFor(tiers.Content)));

        // The navigator's mount follows the persisted view (list 1, grid 3): its key carries the code.
        var mounts = shell.FindPrefix("nav:" + view.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":");
        Assert.True(mounts.Count >= 1 || shell.FindPrefix("navskel").Count >= 1, $"{size.Name}: the navigator is neither mounted nor shimmering");

        // The head lies inside the card.
        var toolbar = Node(shell, "lib:toolbar");
        var head = scene.AbsoluteRect(toolbar);
        Assert.True(head.X >= card.X - 0.5f && head.X + head.W <= card.X + card.W + 0.5f, $"{size.Name}: the head runs outside the card ({head} in {card})");

        if (single)
        {
            // The root rung's head is the chips, not a breadcrumb; every chip and the picker row's controls are 40 targets (the filter's box takes the record's 40 too).
            Assert.True(shell.Find("lib:crumbs").IsNull, $"{size.Name}: a breadcrumb over the root rung");
            foreach (var n in Subtree(scene, Node(shell, "lib:kinds")))
            {
                if ((scene.Flags(n) & NodeFlags.Focusable) == 0 || scene.InCollapsedSubtree(n)) continue;
                var r = scene.AbsoluteRect(n);
                if (r.W > 0f && r.H > 0f) Assert.True(r.H >= 40f - 0.5f, $"{size.Name}: a kind chip is {r.W} x {r.H}, under 40 tall");
            }
            var rows = new List<NodeHandle>();
            for (var c = scene.FirstChild(toolbar); !c.IsNull; c = scene.NextSibling(c)) rows.Add(c);
            Assert.True(rows.Count >= 3, $"{size.Name}: the Compact head has its chips, title, controls and filter rows (found {rows.Count})");
            var picker = rows[2];
            foreach (var n in Subtree(scene, picker))
            {
                if ((scene.Flags(n) & NodeFlags.Focusable) == 0 || scene.InCollapsedSubtree(n)) continue;
                var r = scene.AbsoluteRect(n);
                if (r.W > 0f && r.H > 0f) Assert.True(r.H >= 40f - 0.5f && r.W >= 40f - 0.5f, $"{size.Name}: a sort or view control is {r.W} x {r.H}, under 40 x 40");
            }
            Assert.True(scene.AbsoluteRect(rows[^1]).H >= layout.ControlH - 0.5f, $"{size.Name}: the filter row is under {layout.ControlH} tall");
        }
        else
        {
            // Split: the navigator never exceeds its remembered width and keeps the grip's floor; the pane or reader keeps the record's floor; the two never overlap.
            var nav = scene.AbsoluteRect(Node(shell, "lib:nav"));
            Assert.True(nav.W <= StoredLeftW + 0.5f, $"{size.Name}: the navigator is {nav.W}, past its remembered {StoredLeftW}");
            Assert.True(nav.W >= ClassicLibraryRules.LeftMin - 0.5f, $"{size.Name}: the navigator is {nav.W}, under the grip's floor");
            NodeHandle right = default;
            foreach (var key in new[] { "lib:detail", "lib:reader", "lib:empty" })
            {
                right = shell.Find(key);
                if (!right.IsNull) break;
            }
            Assert.False(right.IsNull, $"{size.Name}: no detail column");
            var pane = scene.AbsoluteRect(right);
            Assert.True(pane.W >= layout.RightMin - 0.5f, $"{size.Name}: the pane is {pane.W}, under its {layout.RightMin} floor");
            Assert.True(nav.X + nav.W <= pane.X + 0.5f, $"{size.Name}: the navigator overlaps the pane");
            Assert.True(pane.X + pane.W <= card.X + card.W + 0.5f, $"{size.Name}: the pane runs outside the card");

            // C-Library-Small: the sort is ONE button at the stock 32 beside the view cells (the artists' list keeps its word rail, the board's). Every other split tier keeps the rail.
            var sort = shell.Find("lib:sort");
            Assert.Equal(layout.SortButton && kind != "artists", !sort.IsNull);
            Assert.Equal(layout.Tier == WidthTier.Small, layout.SortButton);
            if (!sort.IsNull)
            {
                var button = scene.AbsoluteRect(sort);
                Assert.Equal(layout.ControlH, button.H, 0.5f);
                Assert.True(button.X >= nav.X - 0.5f && button.X + button.W <= nav.X + nav.W + 0.5f, $"{size.Name}: the sort button runs outside the navigator ({button} in {nav})");
            }

            // The album pane's "Also by" aside slot: 0 wide below Huge, at most the record's column at Huge (0 where the album has no other saved albums by its artist),
            // inside the pane, after the rows.
            var aside = shell.Find("pane:also-aside");
            if (!aside.IsNull)
            {
                var a = scene.AbsoluteRect(aside);
                float max = ClassicLibraryPaneRules.AlsoByAsideOuter(layout.AlsoByAside, layout.AlsoByAsideGap);
                if (!layout.AlsoByDocked) Assert.True(a.W <= 0.5f, $"{size.Name}: the also-by aside is {a.W} wide below Huge");
                else Assert.True(a.W <= max + 0.5f, $"{size.Name}: the also-by aside is {a.W}, past the record's {max}");
                Assert.True(a.X + a.W <= pane.X + pane.W + 0.5f, $"{size.Name}: the also-by aside runs past the pane ({a} in {pane})");
            }
        }

        // A tier never writes the persisted widths: what the fact stored is what is stored.
        Assert.Equal(StoredLeftW, LibraryViewPrefs.Read(kind).LeftW, 0.5f);
    }

    // ── Recents ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(PageSizes))]
    public void Recents_holds_its_tier_layout(string size) => AssertPage(RecentsPage, size, (shell, tiers) =>
    {
        var layout = ClassicRecentsTiers.Of(tiers);
        Assert.Equal((WidthTier)Math.Min((int)tiers.Content, (int)WidthTier.Huge), layout.Tier);
        var scene = shell.Host.Scene;
        var card = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.ContentCardKey));

        // C-Recents-Huge: the column capped at the 1100 list (its gutters outside it) and the Overview aside beside it; below Huge the aside is 0 wide and the column fills.
        Assert.Equal(layout.Tier == WidthTier.Huge, layout.OverviewDocked);
        var column = scene.AbsoluteRect(Node(shell, "recents:column"));
        var aside = scene.AbsoluteRect(Node(shell, "recents:overview-aside"));
        float gutter = ClassicTiers.PageOf(tiers).Gutter;
        if (layout.OverviewDocked)
        {
            Assert.True(column.W <= layout.ListMax + 2f * gutter + 0.5f, $"{size}: the Recents column is {column.W}, past the 1100 list and its gutters");
            Assert.True(MathF.Abs(aside.W - (layout.OverviewGap + layout.OverviewAside)) <= 0.5f, $"{size}: the Overview aside is {aside.W}, not {layout.OverviewGap + layout.OverviewAside}");
            Assert.True(column.X + column.W <= aside.X + 0.5f, $"{size}: the Recents column overlaps the Overview ({column} / {aside})");
            Assert.True(aside.X + aside.W <= card.X + card.W + 0.5f, $"{size}: the Overview runs outside the card ({aside} in {card})");
            Assert.False(shell.Find("recents:overview-toggle").IsNull, $"{size}: the Overview button is not the aside's toggle");
        }
        else
        {
            Assert.True(aside.W <= 0.5f, $"{size}: the Overview aside is {aside.W} wide below Huge");
            Assert.True(shell.Find("recents:overview-toggle").IsNull, $"{size}: an Overview toggle below Huge");
        }

        // The pivot strip is keyed on the shape alone (a crossing keeps it), lies inside the card, and at Compact its tabs are 40 targets.
        var (strip, key) = Single(shell, "recents-pivots:");
        Assert.Matches("^recents-pivots:[0-9]+$", key);
        var stripRect = scene.AbsoluteRect(strip);
        Assert.True(stripRect.X >= card.X - 0.5f && stripRect.X + stripRect.W <= card.X + card.W + 0.5f, $"{size}: the pivot strip runs outside the card ({stripRect} in {card})");
        if (layout.PivotH > 0f)
            foreach (var n in Subtree(scene, strip))
            {
                if ((scene.Flags(n) & NodeFlags.Focusable) == 0 || scene.InCollapsedSubtree(n)) continue;
                var r = scene.AbsoluteRect(n);
                if (r.W > 0f && r.H > 0f) Assert.True(r.H >= 40f - 0.5f, $"a Compact pivot tab is {r.W} x {r.H}, under 40 tall");
            }
    });

    // ── Local files ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(PageSizes))]
    public void Local_files_holds_its_tier_layout(string size) => AssertPage(LocalFiles, size, (shell, tiers) =>
    {
        // No board in any theme: built from Classic's parts, i.e. the playlist family's frame at every tier (one arm, the window tier's detail record).
        var record = ClassicDetailTiers.Of(tiers);
        Assert.Equal(Sizes[size].Window, record.Tier);
        var arms = shell.FindPrefix("detail:arm:");
        Assert.True(arms.Count == 1, $"{size}: {arms.Count} arms mounted ({string.Join(", ", arms.Select(a => a.Key))})");
    });

    // ── the resting widths while never dragged (contract 1.21) ─────────────────────────────────────────────────────────────────────

    /// <summary>Clear the kind's stored navigator width (never dragged), run <paramref name="body"/>, put the kind's state back.</summary>
    void WithUnsetLibraryWidth(string kind, int view, Action body)
    {
        LibraryViewState saved = default;
        bool customized = false;
        string name = "";
        host.Run(() =>
        {
            saved = LibraryViewPrefs.Read(kind);
            customized = LibraryViewPrefs.LeftWCustomized(kind);
            name = ThemeState.SettingName(ThemeState.Scope.Peek(), LibraryViewPrefs.LeftWKey(kind).Id);
            LibraryViewPrefs.Save(kind, saved with { View = view }, artists: false);
            Platform.Settings.Remove(name);
        });
        try { body(); }
        finally
        {
            host.Run(() =>
            {
                LibraryViewPrefs.Save(kind, saved, artists: false);
                if (customized) LibraryViewPrefs.SetLeftW(kind, saved.LeftW);
                else Platform.Settings.Remove(name);
            });
        }
    }

    [Theory, MemberData(nameof(FrameSizes))]
    public void The_albums_navigator_and_the_sidebar_rest_at_the_tiers_width_while_never_dragged(string size)
        => WithUnsetLibraryWidth("albums", view: 3, () => AssertPage(LibraryAlbums, size, (shell, tiers) =>
        {
            AssertNavigatorRest(shell, tiers, size, "albums");
            AssertSidebarRest(size);
        }));

    [Theory, MemberData(nameof(PageSizes))]
    public void The_artists_navigator_rests_at_the_tiers_width_while_never_dragged(string size)
        => WithUnsetLibraryWidth("artists", view: 1, () => AssertPage(LibraryArtists, size, (shell, tiers) => AssertNavigatorRest(shell, tiers, size, "artists")));

    static void AssertNavigatorRest(Shell.HeadlessShell shell, TierFacts tiers, string sizeName, string kind)
    {
        var size = Sizes[sizeName];
        var scene = shell.Host.Scene;
        float rest = LibraryViewPrefs.RestingLeftW(kind, LibraryViewPrefs.LeftWKey(kind).Default, customized: false, tiers.Content, ClassicLibraryTiers.Policy);
        Assert.Equal(tiers.Content >= WidthTier.Huge ? (kind == "artists" ? 400f : 560f)
                     : tiers.Content <= WidthTier.Small ? ClassicLibraryTiers.SmallNavWidth                  // C-Library-Small / C-LibraryList-Small (contract 1.22)
                     : (kind == "artists" ? 280f : 340f), rest);
        if (!shell.Find("lib:wide").IsNull)
        {
            // The navigator rests at the tier's width wherever the pane's floor leaves room for it, and never past it.
            var card = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.ContentCardKey));
            var nav = scene.AbsoluteRect(Node(shell, "lib:nav"));
            var layout = ClassicLibraryTiers.Of(tiers, single: false);
            Assert.True(nav.W <= rest + 0.5f, $"{size.Name}: the navigator is {nav.W}, past the tier's {rest}");
            if (card.W >= rest + 16f + layout.RightMin + 1f)
                Assert.True(MathF.Abs(nav.W - rest) <= 0.5f, $"{size.Name}: the navigator is {nav.W}, not the tier's {rest}");
        }
        // The tier never wrote the store: the width is still the user's to set.
        Assert.False(LibraryViewPrefs.LeftWCustomized(kind), $"{size.Name}: a tier stored the navigator's width");
    }

    /// <summary>The expanded sidebar rests at the tier's width (C-Shell-Huge 320 from Huge on) unless the user resized it.</summary>
    static void AssertSidebarRest(string sizeName)
    {
        var size = Sizes[sizeName];
        if (Sidebar.Mode.Peek() != SidebarPaneMode.Expanded) return;
        bool user = Sidebar.WidthUserSet();
        float expected = ClassicPolicies.NavPane.RestingWidthFor(size.Window, ThemeState.Get(Sidebar.PaneWidthKey), user);
        Assert.True(MathF.Abs(Sidebar.Width.Peek() - expected) <= 0.5f, $"{size.Name}: the sidebar rests at {Sidebar.Width.Peek()}, not {expected}");
        if (size.Window >= WidthTier.Huge && !user)
            Assert.True(MathF.Abs(Sidebar.Width.Peek() - 320f) <= 0.5f, $"{size.Name}: an unresized Huge sidebar is {Sidebar.Width.Peek()}, not the board's 320");
    }
}
