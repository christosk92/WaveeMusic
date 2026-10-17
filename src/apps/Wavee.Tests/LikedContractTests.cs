// ── Wavee.Tests/LikedContractTests.cs — the Liked Songs page's published keys and Classic's registration (themes plan 6.0, T9c) ─────
//
// The generated cover, its picker and flyout and the chip rail are the structure's pictures over core hosts (`User.LikedCover`'s snapshot, demand
// and persisted pick; `User.LikedPage`'s chip set and the table's filter). What is pure about the contract: the ids (a published id never
// changes), Classic registering every key under a distinct template id, and the equality the props promise (a cover compares by its scalars and
// the tiles' content key, never by the tile ARRAY, so a recompute that changes nothing re-renders nothing).

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class LikedContractTests
{
    static LikedCoverProps Cover(string[] tiles, string key = "k", int count = 3, LikedCoverStyle style = LikedCoverStyle.Lens)
        => new() { Size = 304f, Radius = 8f, Style = style, Tiles = tiles, TileKey = key, TrackCount = count };

    static LikedChipBarProps Chips(string[] titles, int evidenced = 1, string? selected = null)
        => new()
        {
            Titles = titles, Evidenced = evidenced, Selected = selected, ScrollKey = "s",
            SelectAll = static () => { }, Select = static _ => { },
        };

    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Liked.Cover", ViewKeys.LikedCover.Id);
        Assert.Equal("Liked.CoverPicker", ViewKeys.LikedCoverPicker.Id);
        Assert.Equal("Liked.CoverStyleFlyout", ViewKeys.LikedCoverStyleFlyout.Id);
        Assert.Equal("Liked.ChipBar", ViewKeys.LikedChipBar.Id);
    }

    [Fact]
    public void Classic_registers_every_liked_key_under_a_distinct_template_id()
    {
        var classic = ClassicTheme.Create();
        string[] ids = [ViewKeys.LikedCover.Id, ViewKeys.LikedCoverPicker.Id, ViewKeys.LikedCoverStyleFlyout.Id, ViewKeys.LikedChipBar.Id];
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            string template = classic.Registry.TemplateIdOf(id) ?? "";
            Assert.NotEqual("", template);
            Assert.StartsWith("classic.liked.", template, StringComparison.Ordinal);
            Assert.True(templates.Add(template), "template id registered twice: " + template);
        }
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void A_cover_compares_by_its_content_key_not_by_the_tile_array()
    {
        Assert.Equal(Cover(["a", "b"]), Cover(["a", "b"]));          // a fresh-but-equal array: nothing changed
        Assert.NotEqual(Cover(["a", "b"]), Cover(["a", "b"], key: "other"));
        Assert.NotEqual(Cover(["a", "b"]), Cover(["a", "b"], count: 4));
        Assert.NotEqual(Cover(["a", "b"]), Cover(["a", "b"], style: LikedCoverStyle.Wall));
        Assert.Equal(Cover(["a", "b"]).GetHashCode(), Cover(["c", "d"]).GetHashCode());
    }

    [Fact]
    public void The_chip_rail_compares_by_its_titles_evidence_and_selection()
    {
        Assert.Equal(Chips(["Pop", "Rock"]), Chips(["Pop", "Rock"]));
        Assert.NotEqual(Chips(["Pop", "Rock"]), Chips(["Pop", "Jazz"]));
        Assert.NotEqual(Chips(["Pop", "Rock"]), Chips(["Pop", "Rock"], evidenced: 2));
        Assert.NotEqual(Chips(["Pop", "Rock"]), Chips(["Pop", "Rock"], selected: "Pop"));
    }
}
