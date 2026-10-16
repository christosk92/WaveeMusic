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
//   · the insights pane docks as a column at Huge and only at Huge (a two-column or head arm of a track page);
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
    public void The_playlist_page_holds_its_tier_layout(string size) => AssertPage(Playlist, size, (shell, tiers) => AssertDetail(shell, tiers, size, rail: true, tracks: true));

    [Theory, MemberData(nameof(PageSizes))]
    public void The_liked_page_holds_its_tier_layout(string size) => AssertPage(Liked, size, (shell, tiers) => AssertDetail(shell, tiers, size, rail: false, tracks: true));

    [Theory, MemberData(nameof(PageSizes))]
    public void The_show_page_holds_its_tier_layout(string size) => AssertPage(Show, size, (shell, tiers) => AssertDetail(shell, tiers, size, rail: true, tracks: false));

    [Theory, MemberData(nameof(PageSizes))]
    public void The_episode_page_holds_its_tier_layout(string size) => AssertPage(Episode, size, (shell, tiers) => AssertDetail(shell, tiers, size, rail: true, tracks: false));

    // ── the detail page's own assertions ──────────────────────────────────────────────────────────────────────────────

    static void AssertDetail(Shell.HeadlessShell shell, TierFacts tiers, string sizeName, bool rail, bool tracks)
    {
        var size = Sizes[sizeName];
        var scene = shell.Host.Scene;

        // The record in use is the structure's for the window tier, the same one the core reads.
        var record = ClassicDetailTiers.Of(tiers);
        Assert.Equal(size.Window, record.Tier);
        Assert.Same(record.Core, ShellPolicy.Now.DetailTiers.For(tiers));
        Assert.Equal(size.Window == WidthTier.Compact, record.Core.Table.CompactRows);
        Assert.Equal(size.Window >= WidthTier.Huge, record.InsightsDocked);

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
            // The insights pane docks as a column at Huge, and only there (a track page's two-column or head arm composes the dock slot).
            bool docked = size.Window >= WidthTier.Huge && expected != "vertical";
            Assert.True(shell.FindPrefix("insights:dock-column").Count == (docked ? 1 : 0), $"{size.Name}: the docked insights column");
        }

        if (size.Window == WidthTier.Compact) AssertCompactPageTargets(shell, size);
    }

    /// <summary>At Compact every hit target of the hero's action row and of the list bar is at least 40 x 40 (the brief's Compact rule).</summary>
    static void AssertCompactPageTargets(Shell.HeadlessShell shell, TierSize size)
    {
        var scene = shell.Host.Scene;
        int targets = 0;
        foreach (string prefix in new[] { "vhero-actions", "bar:left", "bar:right" })
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
}
