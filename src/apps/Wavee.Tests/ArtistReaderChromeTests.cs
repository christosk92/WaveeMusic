// ── Wavee.Tests/ArtistReaderChromeTests.cs — the artist reader's chrome seam (T10c1) ──────────────────────────────────
//
// The reader's picture is Classic's `Library.ArtistReader` view; the numbers the core and the picture must agree on live on `ReaderShape`. These facts pin the
// two chrome rows' flat indices and the pinned rail's stated height (the extent table's seed, the pinned clip, the spy's probe and the rail root's declared Height are
// that one number), and that the props record is a per-render value that never compares equal to another instance.

using Wavee.Theming.Contracts;
using Xunit;
using RS = Wavee.Artist.ReaderShape;

namespace Wavee.Tests;

public class ArtistReaderChromeTests
{
    [Fact]
    public void The_band_and_the_rail_are_the_two_chrome_items_of_the_one_scroller()
        => Assert.Equal(2, RS.Prefix);

    [Fact]
    public void The_sub_rail_states_its_pads_the_rail_row_and_the_divider()
    {
        Assert.Equal(RS.SubRailPadTop + Controls.Words.Height + RS.SubRailPadBottom + RS.Divider, RS.SubRailH);
        Assert.Equal(49f, RS.SubRailH);
    }

    [Fact]
    public void The_props_record_compares_only_to_itself()
    {
        var props = new LibraryArtistReaderProps { Host = null! };
        Assert.Equal(props, props);
        Assert.NotEqual(props, new LibraryArtistReaderProps { Host = null! });
    }
}
