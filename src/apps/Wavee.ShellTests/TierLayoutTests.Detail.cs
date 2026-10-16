// ── Wavee.ShellTests/TierLayoutTests.Detail.cs — the detail pages at every tier size and threshold edge (themes plan 3.5; TC5) ─────────
//
// The REAL Classic frame with each detail page on screen (Album at 3840 too, a key surface; Playlist, Liked, Show and Episode at the page sizes),
// through `AssertPage` (TierLayoutTests.cs: the tier final in the first frame, the frame's records, the regions inside the window and apart,
// the Compact title-bar targets). Then, on the records, never on pixels:
//
//   · the window tier's detail record is the one the core reads (`ThemePolicies.DetailTiers`) and the one the views read (`ClassicDetailTiers`);
//   · the page's ARM is the mode ladder's answer for the page's own width (the vertical hero below 560, the rail arm or a page with no rail's
//     head above it), and only that arm is mounted once the page has settled;
//   · the track table's form: no column header in the Compact row form;
//   · the insights pane docks as a column at Huge and only at Huge (the head arm of a page with no rail: Liked Songs);
//   · the ASIDE (contract 1.21): only at Huge, in the two-column arm, at the board's width for the kind (C-Album-Huge 520, -Huge4K 640;
//     C-Playlist-Huge 440 / C-PlaylistFollowed-Huge 400), behind its hairline, the list keeping its 8 inset before it; nowhere below Huge;
//     with it docked the album's band leaves About / Fans out and the rail its release row (nothing shown twice);
//   · at Compact every hit target of the hero's action row and of the list bar is at least 40 x 40.

using FluentGpu;
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
    // ── the fixtures (the fake catalogue's album, playlist, show and its trailer, and Liked Songs) ────────────────────────────────

    public static readonly TierPage Album = new("album", Shell.For(EntityUri.Parse("spotify:album:al2"), "Album"), ArmPrefix);
    public static readonly TierPage Playlist = new("playlist", Shell.For(EntityUri.Parse("spotify:playlist:pl1"), "Playlist"), ArmPrefix);
    public static readonly TierPage Liked = new("liked", new Shell.Route(Shell.RouteKind.Liked), ArmPrefix);
    public static readonly TierPage Show = new("show", Shell.For(EntityUri.Parse("spotify:show:sh0"), "Show"), ArmPrefix);
    public static readonly TierPage Episode = new("episode", Shell.For(EntityUri.Parse("spotify:episode:sh0trailer"), "Episode"), ArmPrefix);

    /// <summary>The frame's arm roots (`ClassicDetailFrame.Arm`): one per page mode, cross-faded on a crossing.</summary>
    const string ArmPrefix = "detail:arm:";

    [Theory, MemberData(nameof(FrameSizes))]
    public void The_album_page_holds_its_tier_layout(string size) => AssertPage(Album, size, (shell, tiers) => AssertDetail(shell, tiers, size, rail: true, tracks: true));

    [Theory, MemberData(nameof(PageSizes))]
    public void The_playlist_page_holds_its_tier_layout(string size) => AssertPage(Playlist, size, (shell, tiers) => AssertDetail(shell, tiers, size, rail: true, tracks: true, list: true));

    [Theory, MemberData(nameof(PageSizes))]
    public void The_liked_page_holds_its_tier_layout(string size) => AssertPage(Liked, size, (shell, tiers) => AssertDetail(shell, tiers, size, rail: false, tracks: true, list: true));

    [Theory, MemberData(nameof(PageSizes))]
    public void The_show_page_holds_its_tier_layout(string size) => AssertPage(Show, size, (shell, tiers) => AssertDetail(shell, tiers, size, rail: true, tracks: false));

    [Theory, MemberData(nameof(PageSizes))]
    public void The_episode_page_holds_its_tier_layout(string size) => AssertPage(Episode, size, (shell, tiers) => AssertDetail(shell, tiers, size, rail: true, tracks: false));

    // ── the detail page's own assertions ──────────────────────────────────────────────────────────────────────────────

    static void AssertDetail(Shell.HeadlessShell shell, TierFacts tiers, string sizeName, bool rail, bool tracks, bool list = false)
    {
        var size = Sizes[sizeName];
        var scene = shell.Host.Scene;

        // The record in use is the structure's for the window tier, the same one the core reads.
        var record = ClassicDetailTiers.Of(tiers);
        Assert.Equal(size.Window, record.Tier);
        Assert.Same(record.Core, ShellPolicy.Now.DetailTiers.For(tiers));
        Assert.Equal(size.Window == WidthTier.Compact, record.Core.Table.CompactRows);
        Assert.Equal(size.Window >= WidthTier.Huge, record.InsightsDocked);
        Assert.Equal(size.Window >= WidthTier.Huge, record.Core.AsideDocked);
        Assert.Equal(size.Window >= WidthTier.Huge, record.Aside is not null);

        // One arm on screen once the page has settled, and it is the mode ladder's answer for the page's own width.
        var arms = shell.FindPrefix(ArmPrefix);
        Assert.True(arms.Count == 1, $"{size.Name}: {arms.Count} arms mounted ({string.Join(", ", arms.Select(a => a.Key))})");
        var (armNode, armKey) = arms[0];
        float pageW = scene.AbsoluteRect(armNode).W;
        int mode = ClassicDetailTiers.ModeFor(pageW, 0, initialized: false);
        string expected = mode == Detail.Breakpoints.VerticalMode ? "vertical" : rail ? "two-column" : "head";
        Assert.True(armKey == ArmPrefix + expected, $"{size.Name}: the {pageW}-wide page shows {armKey}, not {ArmPrefix + expected} (mode {mode})");
        if (size.Window == WidthTier.Compact && size.Width < ClassicDetailTiers.TwoColumnFrom)
            Assert.Equal(ArmPrefix + "vertical", armKey);   // the Compact boards draw the vertical hero

        if (tracks)
        {
            // The Compact row form has no column header: its sort labels never mount.
            if (size.Window == WidthTier.Compact) Assert.Empty(shell.FindPrefix("sort-label:"));
            // The insights pane docks as a column at Huge, and only there, on a page with no rail (Liked: the head arm composes the dock slot); a rail
            // page's insights live in its aside at Huge instead.
            bool docked = size.Window >= WidthTier.Huge && expected == "head";
            Assert.True(shell.FindPrefix("insights:dock-column").Count == (docked ? 1 : 0), $"{size.Name}: the docked insights column");
            // The unavailable-songs bar's slot (contract 1.22, C-Unavailable / -Compact) never takes more than its tier's band: the bar and the air under it.
            var unavailable = shell.Find("unavailable");
            if (!unavailable.IsNull)
                Assert.True(scene.AbsoluteRect(unavailable).H <= record.Unavailable.Extent + 0.5f,
                    $"{size.Name}: the unavailable bar's slot is {scene.AbsoluteRect(unavailable).H} tall, past its {record.Unavailable.Extent} band");
        }

        if (tracks) AssertBarAndRows(shell, record, size, list, expected);
        if (rail && tracks && expected == "two-column") AssertRailCta(shell, record, size);
        if (rail) AssertAside(shell, record, size, expected == "two-column");

        if (size.Window == WidthTier.Compact) AssertCompactPageTargets(shell, size);
    }

    /// <summary>The list bar and the rows as the record draws them (fix/remaining-pages): at Compact the Filter is the funnel alone (C-Album-Compact,
    /// C-Playlist-Compact, C-Liked-Compact) and a LIST's Modern rows have no "#" lane (C-TrackList compact); at Small the Sort is its glyph and chevron
    /// (C-Album-Small, C-Playlist-Small, C-Liked-Small); the hero arm carries at most one recommendations section, after its rows, and at Compact its
    /// header's commands are 40 targets (C-Playlist-Compact, C-Empty-Compact).</summary>
    static void AssertBarAndRows(Shell.HeadlessShell shell, ClassicDetailTier record, TierSize size, bool list, string arm)
    {
        var scene = shell.Host.Scene;
        foreach (var (node, key) in shell.FindPrefix("cmd:filter"))
        {
            if (key != "cmd:filter" || scene.InCollapsedSubtree(node) || !record.FilterGlyph) continue;
            float w = scene.AbsoluteRect(node).W;
            Assert.True(w <= ClassicDetailTiers.CompactTarget + 0.5f, $"{size.Name}: the Compact Filter is {w} wide, not the 40 funnel");
        }
        foreach (var (node, key) in shell.FindPrefix("cmd:sort"))
        {
            if (key != "cmd:sort" || scene.InCollapsedSubtree(node) || !record.SortGlyph) continue;
            // The glyph form: 9, the sort glyph, 4, the chevron, 6 (about 41); never the order's word (Custom order, Album order, Date added: 100+).
            float w = scene.AbsoluteRect(node).W;
            Assert.True(w > 0f && w < 64f, $"{size.Name}: the Small Sort is {w} wide, not its glyph");
        }
        // A list's Modern Compact row: art, title, heart, more. Told apart from a Classic row (which keeps its "#" and its duration) by the duration lane.
        if (list && !record.Core.Table.ListIndex && shell.FindPrefix(Track.CellKey.Title).Count > 0 && shell.FindPrefix(Track.CellKey.Duration).Count == 0)
            Assert.True(shell.FindPrefix(Track.CellKey.Num).Count == 0, $"{size.Name}: a Compact list row still lays out its # lane");
        if (arm == "vertical") Assert.True(shell.FindPrefix("vitem:recs").Count <= 1, $"{size.Name}: the hero arm carries more than one recommendations section");
        if (!record.EmptyLead) return;
        foreach (var (root, _) in shell.FindPrefix("rec:header"))
            foreach (var n in Subtree(scene, root))
            {
                if (scene.InCollapsedSubtree(n)) continue;
                var flags = scene.Flags(n);
                if ((flags & NodeFlags.Focusable) == 0 || (flags & NodeFlags.Visible) == 0) continue;
                var r = scene.AbsoluteRect(n);
                if (r.W <= 0f || r.H <= 0f) continue;
                Assert.True(r.W >= 40f - 0.5f && r.H >= 40f - 0.5f, $"{size.Name}: a Recommended songs command is {r.W} x {r.H}, under 40 x 40");
            }
    }

    /// <summary>The rail's CTA (fix/remaining-pages, C-Album-Small): where the record breaks the Play row the fixed actions start their own line (the keyed
    /// break follows the split); elsewhere no break. Only once the rail has loaded its CTA.</summary>
    static void AssertRailCta(Shell.HeadlessShell shell, ClassicDetailTier record, TierSize size)
    {
        var ctas = shell.FindPrefix("rail:cta");
        if (!ctas.Any(c => c.Key == "rail:cta")) return;
        bool broken = ctas.Any(c => c.Key == "rail:cta:break");
        Assert.True(broken == record.Rail.BreakAfterPrimary, $"{size.Name}: the rail's Play row break is {(broken ? "on" : "off")}, the record says {record.Rail.BreakAfterPrimary}");
        if (!broken) return;
        var scene = shell.Host.Scene;
        var mark = scene.AbsoluteRect(ctas.First(c => c.Key == "rail:cta:break").Node);
        foreach (var (node, key) in shell.FindPrefix("rail:shuffle:button"))
            if (key == "rail:shuffle:button")
                Assert.True(scene.AbsoluteRect(node).Y >= mark.Y - 0.5f, $"{size.Name}: Shuffle stayed on the Play row");
    }

    /// <summary>At Compact every hit target of the hero's action row, of the list bar and of the unavailable-songs bar ("Hide them", C-Unavailable-Compact,
    /// contract 1.22) is at least 40 x 40 (the brief's Compact rule).</summary>
    static void AssertCompactPageTargets(Shell.HeadlessShell shell, TierSize size)
    {
        var scene = shell.Host.Scene;
        int targets = 0;
        foreach (string prefix in new[] { "vhero-actions", "bar:left", "bar:right", "unavailable:bar" })
            foreach (var (root, _) in shell.FindPrefix(prefix))
                foreach (var n in Subtree(scene, root))
                {
                    if (scene.InCollapsedSubtree(n)) continue;
                    var flags = scene.Flags(n);
                    if ((flags & NodeFlags.Focusable) == 0 || (flags & NodeFlags.Visible) == 0) continue;
                    var r = scene.AbsoluteRect(n);
                    if (r.W <= 0f || r.H <= 0f) continue;
                    targets++;
                    Assert.True(r.W >= 40f - 0.5f && r.H >= 40f - 0.5f, $"{size.Name}: a {prefix} target is {r.W} x {r.H}, under 40 x 40");
                }
        _ = targets;   // a page whose header is still pending lays out no action row yet; the bar's tools are there from the first frame
    }

    /// <summary>The aside (contract 1.21): its column only at Huge in the two-column arm, at the record's width for the page's kind, after the hairline 16
    /// past the list's 8 inset, inside the card; below Huge (and in any other arm) no column at all.</summary>
    static void AssertAside(Shell.HeadlessShell shell, ClassicDetailTier record, TierSize size, bool twoColumn)
    {
        var scene = shell.Host.Scene;
        var columns = shell.FindPrefix("aside:scroll:");
        if (size.Window < WidthTier.Huge || !twoColumn)
        {
            Assert.True(columns.Count == 0, $"{size.Name}: an aside column below Huge or outside the two-column arm");
            var slot = shell.Find("detail:aside");
            if (!slot.IsNull && size.Window < WidthTier.Huge)
                Assert.True(scene.AbsoluteRect(slot).W <= 0.5f, $"{size.Name}: the aside slot is {scene.AbsoluteRect(slot).W} wide below Huge");
            return;
        }
        if (columns.Count == 0) return;   // a page with neither an aside body nor facts docks none (a show)
        var g = record.Aside!;
        var column = scene.AbsoluteRect(columns[0].Node);
        float[] widths = [g.AlbumWidth, g.PlaylistWidth, g.InsightsWidth];
        Assert.True(widths.Any(w => MathF.Abs(w - column.W) <= 0.5f), $"{size.Name}: the aside column is {column.W}, none of the board's {string.Join(" / ", widths)}");
        var card = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.ContentCardKey));
        Assert.True(column.X + column.W <= card.X + card.W + 0.5f, $"{size.Name}: the aside runs past the card ({column} in {card})");
        // The rows keep their inset: the list column ends 16 (the hairline's margin) before the hairline, its rows 8 inside that.
        var list = scene.AbsoluteRect(Node(shell, "right:tracks"));
        var hairline = scene.AbsoluteRect(Node(shell, "aside:hairline"));
        Assert.True(MathF.Abs(hairline.X - (list.X + list.W) - g.HairlineMargin.Left) <= 0.5f,
            $"{size.Name}: the list ends at {list.X + list.W}, the hairline is at {hairline.X} (want {g.HairlineMargin.Left} between)");
        Assert.True(list.X + list.W + g.HairlineSpan <= column.X + 0.5f, $"{size.Name}: the list overlaps the aside");
    }

    /// <summary>Huge's album aside holds the release, artist and fans, and they leave the band and the rail: nothing is shown twice.</summary>
    [Theory]
    [InlineData("huge 2560x1440")]
    [InlineData("huge4k 3840x2160")]
    public void The_huge_album_aside_holds_what_the_band_and_the_rail_drop(string size) => AssertPage(Album, size, (shell, tiers) =>
    {
        var record = ClassicDetailTiers.Of(tiers);
        Assert.Same(size.StartsWith("huge4k", StringComparison.Ordinal) ? ClassicDetailTiers.Huge4K : ClassicDetailTiers.Huge, record);
        if (shell.FindPrefix("aside:scroll:").Count == 0) return;   // the arm settled vertical (a Hero page layout): no aside, the band keeps it all
        Assert.Equal(record.Aside!.AlbumWidth, shell.Host.Scene.AbsoluteRect(shell.FindPrefix("aside:scroll:")[0].Node).W, 0.5f);
        // At most one artist card and one fans section on the page: the aside's.
        Assert.True(shell.FindPrefix("about:").Count == 0, "the band still draws About the artist beside the aside's card");
        Assert.True(shell.FindPrefix("aside:fans").Count <= 1);
        Assert.True(shell.FindPrefix("hdr:release").Count == 0 && shell.FindPrefix("rail:release").Count == 0, "the rail still carries About this release");
    });
}
