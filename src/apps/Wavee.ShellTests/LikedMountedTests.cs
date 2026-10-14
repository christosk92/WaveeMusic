// ── Wavee.ShellTests/LikedMountedTests.cs — the Liked Songs page's views, mounted (themes plan 6.1, T9c) ───────────────────────────
//
// The generated cover (nine treatments, palette leaves, ambient loops), its "Cover style" picker and the chip rail are the structure's since T9c;
// `User.LikedCover`'s hosts keep the snapshot, the demand and the persisted pick, and `User.LikedPage` keeps the chip set and the table's filter
// (`Wavee.Tests` pins the pure ones: `LikedCoverRulesTests`, `LikedPageRulesTests`, `LikedContractTests`). What only a mounted tree can say:
//
//   registered   Classic registers every Liked key
//   cover        a treatment mounts its keyed canvas (one style per remount: a swap never keeps a loop ticking on another style's node), the
//                site ladder answers the stock PNG below an empty library, and an unfed Wall is the flat mosaic below the treatment floor
//   picker       the cover is mounted under its pill (the pill is always in the tree; hover only reveals it)
//   chips        "All" first, one chip per title in order, the un-evidenced tail disabled; no titles is the zero-hit placeholder

using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class LikedMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    static readonly string[] Tiles = ["a", "b", "c", "d", "e", "f", "g", "h", "i"];

    static LikedCoverProps Cover(LikedCoverStyle style, float size, string[]? tiles = null, int count = 0)
        => new() { Size = size, Radius = 8f, Style = style, Tiles = tiles ?? Tiles, TileKey = string.Join('\u001f', tiles ?? Tiles), TrackCount = count };

    // ── registered ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_template_of_every_liked_key()
        => host.Run(() =>
        {
            (string Id, string Template)[] expected =
            [
                (ViewKeys.LikedCover.Id, "classic.liked.cover"),
                (ViewKeys.LikedCoverPicker.Id, "classic.liked.cover-picker"),
                (ViewKeys.LikedCoverStyleFlyout.Id, "classic.liked.cover-style-flyout"),
                (ViewKeys.LikedChipBar.Id, "classic.liked.chip-bar"),
            ];
            foreach (var (id, template) in expected)
                Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
        });

    // ── the cover ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_treatment_mounts_its_canvas_keyed_by_its_style()
        => ViewHarness.With(host, ViewKeys.LikedCover, Cover(LikedCoverStyle.Tone, 304f, count: 12), harness =>
        {
            harness.RunFrames(4);
            Assert.True(harness.Contains("liked-style:Tone"), "the Tone canvas, keyed by its style");
            Assert.True(harness.Contains("liked-tone"), "Tone's graded face is its own leaf");
            Assert.False(harness.Contains("liked-style:Lens"), "one style at a time");
        }, UnderClassic());

    [Fact]
    public void Lens_composes_the_ground_veil_under_the_window()
        => ViewHarness.With(host, ViewKeys.LikedCover, Cover(LikedCoverStyle.Lens, 304f), harness =>
        {
            harness.RunFrames(4);
            Assert.True(harness.Contains("liked-style:Lens"));
            Assert.True(harness.Contains("liked-lens-veil"), "the veil is a palette leaf");
        }, UnderClassic());

    [Fact]
    public void An_unfed_wall_below_the_floor_is_the_flat_mosaic_and_composes_no_treatment()
        => ViewHarness.With(host, ViewKeys.LikedCover, Cover(LikedCoverStyle.Wall, 48f), harness =>
        {
            harness.RunFrames(4);
            Assert.False(harness.Contains("liked-style:Wall"), "a 48-DIP wall is the mosaic, not 36 unreadable specks");
        }, UnderClassic());

    [Fact]
    public void An_empty_library_is_the_stock_cover()
        => ViewHarness.With(host, ViewKeys.LikedCover, Cover(LikedCoverStyle.Stock, 304f, tiles: []), harness =>
        {
            harness.RunFrames(2);
            Assert.False(harness.Contains("liked-style:Stock"), "the stock PNG is not a composed treatment");
            Assert.False(harness.Contains("liked-tone"));
        }, UnderClassic());

    // ── the picker ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_picker_mounts_the_cover_under_its_pill()
        => ViewHarness.With(host, ViewKeys.LikedCoverPicker, new LikedCoverPickerProps
        {
            Size = 200f, Radius = 8f,
            Art = Probe(),
            Flyout = static () => new FluentGpu.Dsl.BoxEl(),
        }, harness =>
        {
            harness.RunFrames(4);
            Assert.True(harness.Contains("art:probe"), "the cover the host handed the picker");
        }, UnderClassic());

    static FluentGpu.Dsl.Element Probe() => new FluentGpu.Dsl.BoxEl { Key = "art:probe", Width = 200f, Height = 200f };

    // ── the chip rail ────────────────────────────────────────────────────────────────────────────────────────────────────

    static LikedChipBarProps Chips(string[] titles, int evidenced, string? selected = null)
        => new()
        {
            Titles = titles, Evidenced = evidenced, Selected = selected, ScrollKey = "chips-test",
            SelectAll = static () => { }, Select = static _ => { },
        };

    [Fact]
    public void The_chip_rail_stands_all_first_then_one_chip_per_title_in_order()
        => ViewHarness.With(host, ViewKeys.LikedChipBar, Chips(["Pop", "Rock", "Jazz"], evidenced: 2, selected: "Rock"), harness =>
        {
            harness.RunFrames(4);
            Assert.True(harness.Contains("chips"));
            var all = harness.AbsoluteBounds(harness.Node("chip:all"));
            var pop = harness.AbsoluteBounds(harness.Node("chip:Pop"));
            var rock = harness.AbsoluteBounds(harness.Node("chip:Rock"));
            var jazz = harness.AbsoluteBounds(harness.Node("chip:Jazz"));
            Assert.True(pop.X >= all.X + all.W - 0.5f, "All is first");
            Assert.True(rock.X >= pop.X + pop.W - 0.5f, "titles keep their order");
            Assert.True(jazz.X >= rock.X + rock.W - 0.5f, "the un-evidenced tail stays in line");
        }, UnderClassic());

    [Fact]
    public void No_titles_is_the_zero_hit_placeholder()
        => ViewHarness.With(host, ViewKeys.LikedChipBar, Chips([], evidenced: 0), harness =>
        {
            harness.RunFrames(2);
            Assert.True(harness.Contains("chips:off"));
            Assert.False(harness.Contains("chips"), "no rail");
        }, UnderClassic());
}
