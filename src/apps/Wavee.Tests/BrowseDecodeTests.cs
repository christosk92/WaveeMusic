// ── Wavee.Tests/BrowseDecodeTests.cs — the chart bit the browse folds decode (Wave 5, P3; ch 12 trap 10) ─────────────
//
// Wave 5 adds ONE thing to B1's browse folds: a band whose uri is one of `ChartSections.All` is stamped
// `SectionFlags.Chart` AT DECODE, in `Band()` — the one path `browseSection` and `browsePage` share — so the drill grid
// threads the decoded bit in and never re-derives it from a uri. B1's own facts for the four folds (directory tiles,
// page bands, a section page at its offset, the home-section drill) live in PathfinderDecodeTests and are not repeated.
//
// The fixtures (`Fixtures/browse/`) are `Fixtures/spotify/browse-section.json`'s shape with the captured Weekly chart
// uri, and a two-band Charts page whose second band differs from Weekly's uri by the CASE of one character — base62
// ids are case-sensitive, so that band is not a chart.

using System.IO;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class BrowseDecodeTests
{
    static byte[] Fixture(string folder, string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, name));

    [Fact]
    public void A_browse_section_answer_for_a_chart_uri_is_stamped_chart_at_decode()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var uri = Spotify.Decode.BrowseSection(Fixture("browse", "browse-section-chart.json"), 0, s);
        Assert.False(uri.IsEmpty);
        TestScope.CommitAndPublish(s);

        var weekly = Entities.BrowseSection(ChartSections.Weekly.AsSpan());
        Assert.True(weekly.Knows(SectionFields.Identity));
        Assert.True(weekly.IsChart);
        Assert.Equal(SectionFlags.Chart, weekly.Flags);
        Assert.Equal(2, weekly.CardSlots.Length);
        Assert.True(HomeSectionView.Of(weekly).IsChart);                // the view the drill page reads carries it
    }

    [Fact]
    public void A_browse_section_answer_for_any_other_uri_is_not_a_chart()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.BrowseSection(Fixture("spotify", "browse-section.json"), 20, s);
        TestScope.CommitAndPublish(s);

        var band = Entities.Section("spotify:section:weekly".AsSpan());     // titled "Weekly Song Charts" — a title is not a uri
        Assert.True(band.Knows(SectionFields.Identity));
        Assert.False(band.IsChart);
        Assert.Equal(SectionFlags.None, band.Flags);
    }

    [Fact]
    public void A_browse_page_stamps_each_band_by_its_own_uri_exactly()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.BrowsePage(Fixture("browse", "browse-page-charts.json"), "spotify:page:0JQ5DAudkNjCgYMM0TZXDw"u8, 0, s);
        TestScope.CommitAndPublish(s);

        var page = Entities.BrowseNode(ChartPages.Charts.AsSpan());
        var bands = Entities.Current.Edges.BrowseSections;
        Assert.Equal(2, bands.Count(page.Slot));

        var featured = new Section(bands.Targets(page.Slot)[0]);
        Assert.Equal(Entities.BrowseSection(ChartSections.Featured.AsSpan()).Slot, featured.Slot);
        Assert.True(featured.IsChart);

        var lookalike = new Section(bands.Targets(page.Slot)[1]);
        Assert.False(lookalike.IsChart);                                 // "…xD1H" is not Weekly's "…xD1h": base62 is case-sensitive
    }
}

// A1 (G-045 follow-up): the image-node source pick, pulled out of the JSON walk as a pure rule so it needs no fixture,
// no Staging and no engine — Spotify image nodes carry several `sources[]` entries (typically 64/300/640 px) and the
// old pick took the WIDEST unconditionally, which downloads and Fant-downscales a 640² master for a 128-DIP shelf
// thumbnail. No [Collection]/TestScope.Fresh(): BrowseImagePick.Choose touches neither Staging nor Entities.
public class BrowseImagePickTests
{
    static (string Url, int Width)[] Sources64_300_640 => [("u64", 64), ("u300", 300), ("u640", 640)];

    [Fact]
    public void Picks_the_300_source_for_a_300_ask_when_64_300_640_are_present()
    {
        string? picked = Spotify.Decode.BrowseImagePick.Choose(Sources64_300_640, 300);
        Assert.Equal("u300", picked);
    }

    [Fact]
    public void Picks_the_640_source_for_a_640_ask()
    {
        string? picked = Spotify.Decode.BrowseImagePick.Choose(Sources64_300_640, 640);
        Assert.Equal("u640", picked);
    }

    [Fact]
    public void Falls_back_to_the_widest_source_when_none_reach_the_target()
    {
        (string Url, int Width)[] sources = [("u64", 64), ("u300", 300)];
        string? picked = Spotify.Decode.BrowseImagePick.Choose(sources, 640);
        Assert.Equal("u300", picked);
    }
}
