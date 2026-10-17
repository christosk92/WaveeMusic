// ── Wavee.ShellTests/ZuneTierLayoutTests.Detail.cs — Zune's album, playlist, Liked, show and episode at every tier size and edge (plan 3.5; P5 ZT5) ──
//
// The five detail pages on the mounted app at every size of the harness (the album at 3840 x 2160 too, a key surface), on the facts and the records,
// never on pixels. For each page and size:
//
//   · the arm is the window's: the vertical arm (the hero, or the show header) at Compact, the two-column arm (the identity column, or Liked Songs' head)
//     from Small; the frame, its columns and its aside stand inside the content column (no horizontal overflow) and the columns never overlap;
//   · the panorama band is the tier's record for every kind (the album's step): the record's height as a floor, folded to 0 on a short window (the
//     identity column then leads with the title); a long title wraps over two lines and the band hugs it, never under its floor;
//   · the identity column stands on the page column's x (A1) and is the record's width;
//   · at Compact every ring is at least 40 x 40; a short window's show header is the name over the ring row (no cover row).
//
// And the album across the edges that move its columns (1008, 1920, 3200 and the 700 height, both ways), by the plan 3.3 sequence: a resized frame
// that still lays the old tier out, then the crossing frame. The frame, the panorama band, the identity column, the aside slot and the songs heading
// are the SAME nodes after it, and a column that changed its width glides (its size channel holds tracks) instead of snapping.

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class ZuneTierLayoutTests
{
    // ── the fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly TierPage AlbumPage = new("album", Shell.For(EntityUri.Parse("spotify:album:al2"), "Album"), ZuneDetailRules.FrameKey);
    static readonly TierPage PlaylistPage = new("playlist", Shell.For(EntityUri.Parse("spotify:playlist:pl1"), "Playlist"), ZuneDetailRules.FrameKey);
    static readonly TierPage LikedPage = new("liked", new Shell.Route(Shell.RouteKind.Liked), ZuneDetailRules.FrameKey);
    static readonly TierPage ShowPage = new("show", Shell.For(EntityUri.Parse("spotify:show:sh0"), "Show"), ZuneDetailRules.FrameKey);
    static readonly TierPage EpisodePage = new("episode", Shell.For(EntityUri.Parse("spotify:episode:sh0e3"), "Episode"), ZuneDetailRules.FrameKey);

    // ── the page at every size ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(FrameSizes))]
    public void An_album_holds_its_detail_tier_record(string size)
        => AssertPage(AlbumPage, size, (shell, tiers) => AssertDetail(shell, size, tiers, DetailKind.Album));

    [Theory, MemberData(nameof(PageSizes))]
    public void A_playlist_holds_its_detail_tier_record(string size)
        => AssertPage(PlaylistPage, size, (shell, tiers) => AssertDetail(shell, size, tiers, DetailKind.Playlist));

    [Theory, MemberData(nameof(PageSizes))]
    public void Liked_songs_holds_its_detail_tier_record(string size)
        => AssertPage(LikedPage, size, (shell, tiers) => AssertDetail(shell, size, tiers, DetailKind.Liked));

    [Theory, MemberData(nameof(PageSizes))]
    public void A_show_holds_its_detail_tier_record(string size)
        => AssertPage(ShowPage, size, (shell, tiers) => AssertDetail(shell, size, tiers, DetailKind.Show));

    [Theory, MemberData(nameof(PageSizes))]
    public void An_episode_holds_its_detail_tier_record(string size)
        => AssertPage(EpisodePage, size, (shell, tiers) => AssertDetail(shell, size, tiers, DetailKind.Episode));

    void AssertDetail(Shell.HeadlessShell shell, string sizeName, TierFacts tiers, DetailKind kind)
    {
        var size = Sizes[sizeName];
        bool vertical = size.Window == WidthTier.Compact;
        bool tracks = kind is DetailKind.Album or DetailKind.Playlist or DetailKind.Liked;
        bool head = kind == DetailKind.Liked;
        // Z-Album-Small is 800 x 600 (short) and keeps its 64/72 panorama; a Small page folds only below the record's floor.
        bool fold = size.Tall == HeightTier.Short && !(size.Window == WidthTier.Small && size.Height >= ZuneDetailRules.SmallPanoramaMinH);

        // What marks the arm on screen: the hero (a track page) or the show header, or the identity column, or Liked Songs' head.
        string armKey = vertical ? (tracks ? ZuneDetailRules.HeroKey : ZuneDetailRules.ShowHeaderKey) : head ? ZuneDetailRules.HeadKey : ZuneDetailRules.RailKey;
        UntilBackground(shell, () => !shell.Find(armKey).IsNull, sizeName + ": the " + kind + " page's arm (" + armKey + ")");
        shell.RunFrames(60);   // entrances and the page's first layout settle before a rect is read

        var scene = shell.Host.Scene;
        var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
        var frame = scene.AbsoluteRect(Node(shell, ZuneDetailRules.FrameKey));
        AssertWithin(sizeName, "the detail frame", frame, content.X, content.X + content.W);

        // The columns stand inside the frame and never overlap.
        var columns = new List<(string Key, RectF Rect)>();
        foreach (string key in new[] { ZuneDetailRules.RailKey, ZuneDetailRules.HeadKey, ZuneDetailRules.AsideKey, ZuneDetailRules.PanoramaKey })
        {
            var node = shell.Find(key);
            if (node.IsNull) continue;
            var r = scene.AbsoluteRect(node);
            if (r.W <= 0.5f) continue;
            AssertWithin(sizeName, key, r, frame.X, frame.X + frame.W);
            if (key != ZuneDetailRules.PanoramaKey && key != ZuneDetailRules.HeadKey) columns.Add((key, r));
        }
        var rail = columns.Find(static c => c.Key == ZuneDetailRules.RailKey);
        var aside = columns.Find(static c => c.Key == ZuneDetailRules.AsideKey);
        if (rail.Key is not null && aside.Key is not null)
            Assert.True(rail.Rect.X + rail.Rect.W <= aside.Rect.X + 0.5f, $"{sizeName}: the identity column ({rail.Rect}) overlaps the aside ({aside.Rect})");

        // The panorama band is the tier's record for every kind (the album's step); a short window folds it; the vertical arm's is the Compact record's.
        var panoramaNode = shell.Find(ZuneDetailRules.PanoramaKey);
        Assert.False(panoramaNode.IsNull, $"{sizeName}: the panorama band is mounted (folded or not)");
        var band = scene.AbsoluteRect(panoramaNode);
        ZuneDetailLayout expected;
        if (vertical)
            expected = fold && !tracks ? ZuneDetailTiers.Hero with { PanoramaH = 0f } : ZuneDetailTiers.Hero;
        else
            expected = ZuneDetailTiers.Of(tiers);
        bool hugs = expected.PanoramaWraps || (kind == DetailKind.Playlist && !shell.Find(ZunePlaylistRules.EditorReadKey).IsNull);
        if (!vertical && fold) Assert.True(band.H <= 0.5f, $"{sizeName}: a short window folds the panorama band ({band})");
        else if (expected.PanoramaH == 0f) Assert.True(band.H <= 0.5f, $"{sizeName}: the folded band ({band})");
        else if (hugs) Assert.True(band.H >= expected.PanoramaH - 1f, $"{sizeName}: a hugging band is never under its floor {expected.PanoramaH} ({band})");
        else Assert.Equal(expected.PanoramaH, band.H, 1f);

        if (!vertical && !head)
        {
            // The identity column is the record's width, on the system page column's x (audit A1).
            var lay = ZuneDetailTiers.Of(tiers);
            float gutter = ZuneTiers.FrameOf(tiers).Gutter;
            float x = frame.X + MathF.Max(gutter, (frame.W - ZuneFrameRules.PageCap) / 2f);
            Assert.True(MathF.Abs(rail.Rect.X - x) <= 1f, $"{sizeName}: the identity column is on the page column's x ({rail.Rect.X} vs {x})");
            Assert.True(rail.Rect.W <= lay.RailW + 1f, $"{sizeName}: the identity column is at most the record's {lay.RailW} ({rail.Rect.W})");
            // The column's rows arrive behind its skeleton boundary: wait for the cover, then read what leads it.
            UntilBackground(shell, () => !shell.Find("rail:cover-art").IsNull, sizeName + ": the identity column's loaded rows");
            if (fold) Assert.False(shell.Find("rail:title").IsNull, $"{sizeName}: a short window's column leads with the title");
            else Assert.True(shell.Find("rail:title").IsNull, $"{sizeName}: the title is the panorama's, not the column's");
        }

        if (vertical && !tracks)
        {
            // The show header: a cover row unless the window is short, where it is the name over the ring row.
            Assert.Equal(fold, shell.Find("hdr:cover").IsNull);
            Assert.False(shell.Find("hdr:actions").IsNull, $"{sizeName}: the ring row stands at every height");
            Assert.Equal(fold, !shell.Find("hdr:title").IsNull);
        }

        // At Compact every ring is at least 40 x 40 (a smaller visual inside a 40 box is fine).
        if (size.Window == WidthTier.Compact)
            foreach (var (node, key) in shell.FindPrefix(ZuneDetailRules.RingPrefix))
            {
                var r = scene.AbsoluteRect(node);
                if (r.W <= 0f || r.H <= 0f) continue;
                Assert.True(r.W >= 40f - 0.5f && r.H >= 40f - 0.5f, $"{sizeName}: {key} is {r.W} x {r.H}, under 40 x 40");
            }
    }

    // ── the album across the edges that move its columns ─────────────────────────────────────────────────────────────────

    public static TheoryData<float, float, float> DetailWindowCrossings() => new()
    {
        { 800f, 1440f, 960f }, { 1440f, 800f, 960f },      // 1008: the aside docks and folds (260 / 0), the identity column 160 <-> 240
        { 1440f, 2048f, 1114f }, { 2048f, 1440f, 1114f },  // 1920: the identity column 240 <-> 320, the aside 260 <-> 400
        { 2560f, 3840f, 1440f }, { 3840f, 2560f, 1440f },  // 3200: the identity column 320 <-> 400, the aside 400 <-> 440
    };

    public static TheoryData<float, float, float> DetailHeightCrossings() => new()
    {
        { 1440f, 960f, 600f }, { 1440f, 600f, 960f },      // 700: the panorama band folds to 0 (the column leads with the title) and unfolds
    };

    [Theory, MemberData(nameof(DetailWindowCrossings))]
    public void A_detail_window_crossing_keeps_the_pages_parts_and_the_columns_glide(float fromW, float toW, float height)
        => CrossDetail(fromW, height, toW, height, ZuneTierAt(toW), ZuneHeightAt(height));

    [Theory, MemberData(nameof(DetailHeightCrossings))]
    public void A_detail_height_crossing_keeps_the_pages_parts(float width, float from, float to)
        => CrossDetail(width, from, width, to, ZuneTierAt(width), ZuneHeightAt(to));

    void CrossDetail(float fromW, float fromH, float toW, float toH, WidthTier window, HeightTier tall) => Mounted(shell =>
    {
        var scene = shell.Host.Scene;
        try
        {
            shell.RunFrames(20);
            Shell.GoTo(AlbumPage.Route);
            UntilBackground(shell, () => !shell.Find(ZuneDetailRules.RailKey).IsNull && !shell.Find(ZuneDetailRules.SongsHeadingKey).IsNull,
                "the album's identity column and its songs heading");
            shell.RunFrames(60);
            var before = Tiers;

            // Identity only: the frame and the panorama band follow the window's own size; the columns are the ones a record resizes.
            var kept = new List<FramePart>();
            foreach (string key in new[] { ZuneDetailRules.FrameKey, ZuneDetailRules.PanoramaKey, ZuneDetailRules.RailKey, ZuneDetailRules.AsideKey, ZuneDetailRules.SongsHeadingKey })
            {
                var n = shell.Find(key);
                Assert.False(n.IsNull, $"{key} is mounted before the crossing");
                kept.Add(new FramePart(key, n, scene.Bounds(n)));
            }

            // The resized frame (the old tier at the new size), then the crossing frame (plan 3.3).
            bool narrowing = toW < fromW || toH < fromH;
            shell.Window.Composited = true;
            if (narrowing)
            {
                shell.Window.InModalLoop = true;
                shell.Window.SizedInModalLoop = true;
                shell.Resize(toW, toH);
                shell.Host.Paint(0, keepAlive: true);
                shell.Window.InModalLoop = false;
                shell.Window.SizedInModalLoop = false;
                shell.Host.Paint(0, keepAlive: true);
            }
            else
            {
                shell.Resize(toW, toH);
                shell.Host.Paint(0, keepAlive: true);
            }
            Assert.Equal(before.Window, Tiers.Window);
            Assert.Equal(before.Height, Tiers.Height);
            kept = kept.ConvertAll(p => p with { Local = scene.Bounds(p.Node) });

            if (narrowing) shell.Host.Paint(0, keepAlive: true); else shell.Host.RunFrame();
            Assert.True(window == Tiers.Window, $"the window tier is {Tiers.Window}, not {window}");
            Assert.True(tall == Tiers.Height, $"the height tier is {Tiers.Height}, not {tall}");

            string what = $"{fromW}x{fromH} -> {toW}x{toH}";
            foreach (var p in kept)
            {
                Assert.True(scene.IsLive(p.Node), $"{what}: {p.Name} was thrown away on the crossing");
                Assert.True(shell.Find(p.Name).Equals(p.Node), $"{what}: {p.Name} was remounted on the crossing");
            }
            // The columns a record resizes glide on their size channel (the frame and the band follow the window itself).
            foreach (var p in kept)
                if (p.Name is ZuneDetailRules.RailKey or ZuneDetailRules.AsideKey)
                    AssertKeptGliding(shell, p, what);
            if (before.Height != Tiers.Height)
                AssertKeptGliding(shell, kept.Find(static p => p.Name == ZuneDetailRules.PanoramaKey), what);

            // Settled: the record of the new tier.
            shell.RunFrames(60);
            var lay = ZuneDetailTiers.Of(Tiers);
            var rail = scene.AbsoluteRect(Node(shell, ZuneDetailRules.RailKey));
            Assert.True(rail.W <= lay.RailW + 1f, $"{what}: the identity column settles at the record's {lay.RailW} ({rail.W})");
            var band = scene.AbsoluteRect(Node(shell, ZuneDetailRules.PanoramaKey));
            Assert.Equal(lay.PanoramaH, band.H, 1f);
        }
        finally
        {
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            shell.RunFrames(10);
        }
    }, fromW, fromH);
}
