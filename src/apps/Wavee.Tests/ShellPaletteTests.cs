// ── Wavee.Tests/ShellPaletteTests.cs — the command palette's table and keystroke filter (Shell/Shell.Palette.cs) ──────
//
// 0.2.9's `WaveeCommands` had NO test file (ch 19 §8, confirmed by grep in the chapter's audit). These are the three
// the chapter asks for — the table's shape, the scoring/ordering ladder and "`>` suppresses the catalog row" — plus the
// rules a re-author most easily breaks:
//
//   TIES KEEP DECLARATION ORDER. The insert steps back only past STRICTLY worse scores.
//
//   THE LIST IS CAPPED AT EIGHT and written into a caller-owned array — never a per-keystroke List.
//
//   `nav.library` LANDS ON `liked`, not on a `library` route.

using Xunit;

namespace Wavee.Tests;

public class CommandTableShapeTests
{
    [Fact]
    public void The_builtin_table_is_nineteen_rows_nav_first()
    {
        var rows = Shell.CommandTable.Builtins();
        Assert.Equal(Shell.CommandTable.BuiltinCount, rows.Length);
        Assert.Equal("nav.home", rows[0].Id);
        Assert.Equal("nav.search", rows[1].Id);
        Assert.Equal("nav.library", rows[2].Id);
        Assert.Equal("nav.recents", rows[3].Id);
        Assert.Equal("nav.settings", rows[4].Id);
        Assert.Equal("playback.playPause", rows[5].Id);
        Assert.Equal("library.newFolder", rows[18].Id);
    }

    [Fact]
    public void Your_library_lands_on_liked_and_the_glyphs_are_the_shipped_ones()
    {
        var rows = Shell.CommandTable.Builtins();
        Assert.Equal(Shell.RouteKind.Liked, rows[2].Destination);
        Assert.Equal(FluentGpu.Controls.Icons.Home, rows[0].Glyph);
        Assert.Equal(FluentGpu.Controls.Icons.Search, rows[1].Glyph);
        Assert.Equal(FluentGpu.Controls.Icons.MusicNote, rows[2].Glyph);
        Assert.Equal(FluentGpu.Controls.Icons.Headphones, rows[3].Glyph);
        Assert.Equal(FluentGpu.Controls.Icons.Settings, rows[4].Glyph);
        Assert.Equal(FluentGpu.Controls.Icons.Play, rows[5].Glyph);
        Assert.Equal(FluentGpu.Controls.Icons.Next, rows[6].Glyph);
        Assert.Equal(FluentGpu.Controls.Icons.Previous, rows[7].Glyph);
    }

    [Fact]
    public void Every_label_is_pre_lowercased_once()
    {
        foreach (var row in Shell.CommandTable.Builtins())
            Assert.Equal(row.Label.ToLowerInvariant(), row.LabelLower);
    }

    [Fact]
    public void Only_descriptors_the_palette_can_target_earn_a_row()
    {
        Assert.True(Shell.CommandTable.TargetOf(ActionTargetModes.NowPlaying | ActionTargetModes.FixedTrack, out var np));
        Assert.Equal(ActionTargetMode.NowPlaying, np);
        Assert.True(Shell.CommandTable.TargetOf(ActionTargetModes.ActiveRoute | ActionTargetModes.None, out var ar));
        Assert.Equal(ActionTargetMode.ActiveRoute, ar);
        Assert.True(Shell.CommandTable.TargetOf(ActionTargetModes.None, out var none));
        Assert.Equal(ActionTargetMode.None, none);
        Assert.False(Shell.CommandTable.TargetOf(ActionTargetModes.FixedEntity, out _));
    }

    [Fact]
    public void Registry_rows_append_after_the_builtins_with_the_more_glyph_fallback()
    {
        ActionDescriptor[] actions =
        [
            new() { Key = "wavee.toggleLike", LabelLocKey = "menu.saveToLiked", IconKey = "like",
                    AcceptedTargets = ActionTargetModes.NowPlaying, Run = static (_, _, _) => { } },
            new() { Key = "wavee.play", LabelLocKey = "detail.play", IconKey = "play",
                    AcceptedTargets = ActionTargetModes.FixedEntity, Run = static (_, _, _) => { } },
        ];
        var index = Shell.CommandTable.BuildIndex(actions, glyphOf: null);
        Assert.Equal(Shell.CommandTable.BuiltinCount + 1, index.Length);
        var row = index[^1];
        Assert.Equal(Shell.PaletteKind.Registry, row.Kind);
        Assert.Equal("wavee.toggleLike", row.RegistryKey);
        Assert.Equal(ActionTargetMode.NowPlaying, row.RegistryTarget);
        Assert.Equal(FluentGpu.Controls.Icons.More, row.Glyph);
    }
}

public class CommandTableFilterTests
{
    static Shell.PaletteEntry E(string label) => new() { Id = label, Label = label, LabelLower = label.ToLowerInvariant() };

    static (Shell.PaletteEntry[] Hits, int Count) Run(Shell.PaletteEntry[] index, string query)
    {
        var dest = new Shell.PaletteEntry[Shell.CommandTable.MaxResults];
        int n = Shell.CommandTable.Filter(index, query, dest, Shell.CommandTable.NewCatalogScratch());
        return (dest, n);
    }

    [Theory]
    [InlineData("playlist", "pl", 0)]
    [InlineData("new playlist", "pl", 1)]
    [InlineData("repeat all", "pl", 2)]
    [InlineData("home", "pl", -1)]
    public void The_scoring_ladder(string label, string query, int expected)
        => Assert.Equal(expected, Shell.CommandTable.ScoreOf(label, query));

    [Fact]
    public void Ranking_is_prefix_then_contains_then_subsequence_then_the_catalog_row_last()
    {
        var (hits, n) = Run([E("Repeat all"), E("New playlist"), E("Play"), E("Home")], "pl");
        Assert.Equal(4, n);
        Assert.Equal("Play", hits[0].Id);
        Assert.Equal("New playlist", hits[1].Id);
        Assert.Equal("Repeat all", hits[2].Id);
        Assert.Equal(Shell.PaletteKind.CatalogSearch, hits[3].Kind);
        Assert.Equal("pl", hits[3].CatalogQuery);
    }

    [Fact]
    public void Ties_keep_declaration_order()
    {
        var (hits, n) = Run([E("Alpha one"), E("Alpha two"), E("Alpha three")], ">alpha");
        Assert.Equal(3, n);
        Assert.Equal("Alpha one", hits[0].Id);
        Assert.Equal("Alpha two", hits[1].Id);
        Assert.Equal("Alpha three", hits[2].Id);
    }

    [Fact]
    public void The_commands_only_prefix_suppresses_the_catalog_row_and_skips_its_own_spaces()
    {
        var index = new[] { E("Play"), E("New playlist") };
        var (hits, n) = Run(index, ">  pl");
        Assert.Equal(2, n);
        Assert.Equal("Play", hits[0].Id);
        Assert.DoesNotContain(hits[..n], static h => h.Kind == Shell.PaletteKind.CatalogSearch);
    }

    [Fact]
    public void A_pasted_query_is_trimmed_and_ranks_as_the_bare_one()
    {
        var index = new[] { E("Repeat all"), E("Play") };
        var (hits, n) = Run(index, "  pl\n");
        Assert.Equal("Play", hits[0].Id);
        Assert.Equal("pl", hits[n - 1].CatalogQuery);
    }

    [Fact]
    public void The_list_is_capped_at_eight_and_a_full_list_gets_no_catalog_row()
    {
        var index = new Shell.PaletteEntry[20];
        for (int i = 0; i < index.Length; i++) index[i] = E("Item " + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
        var (hits, n) = Run(index, "item");
        Assert.Equal(Shell.CommandTable.MaxResults, n);
        Assert.Equal("Item 00", hits[0].Id);
        Assert.Equal("Item 07", hits[7].Id);
        Assert.DoesNotContain(hits, static h => h.Kind == Shell.PaletteKind.CatalogSearch);
    }

    [Fact]
    public void A_better_late_match_displaces_the_worst_of_a_full_list()
    {
        var index = new Shell.PaletteEntry[9];
        for (int i = 0; i < 8; i++) index[i] = E("x item " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        index[8] = E("item first");
        var (hits, n) = Run(index, "item");
        Assert.Equal(Shell.CommandTable.MaxResults, n);
        Assert.Equal("item first", hits[0].Id);                 // prefix beats the eight "contains"
        Assert.Equal("x item 6", hits[7].Id);                   // the last contains fell off the end
    }

    [Fact]
    public void An_empty_query_lists_the_table_in_order_and_zero_hits_is_zero()
    {
        var index = new[] { E("One"), E("Two"), E("Three") };
        var (hits, n) = Run(index, "");
        Assert.Equal(3, n);
        Assert.Equal("One", hits[0].Id);

        var (_, none) = Run(index, ">zzzz");
        Assert.Equal(0, none);
    }
}
