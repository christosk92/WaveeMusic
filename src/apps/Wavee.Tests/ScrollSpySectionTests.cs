using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ScrollSpySectionTests
{
    [Fact]
    public void Empty_span_has_no_answer()
        => Assert.Equal(-1, Detail.ScrollSpy.ActiveSectionOf([], 0f, 800f));

    [Fact]
    public void Unmeasured_first_section_has_no_answer()
        => Assert.Equal(-1, Detail.ScrollSpy.ActiveSectionOf([float.NaN, 400f], 0f, 800f));

    [Fact]
    public void At_the_very_top_the_first_section_is_active()
        => Assert.Equal(0, Detail.ScrollSpy.ActiveSectionOf([0f, 600f, 1400f, 2200f], 0f, 800f));

    [Fact]
    public void A_section_becomes_active_once_its_top_crosses_the_quarter_viewport_line()
    {
        // spy line = offset + 0.25 * viewportHeight = offset + 200 at an 800-tall viewport.
        float[] anchors = [0f, 600f, 1400f, 2200f];
        Assert.Equal(0, Detail.ScrollSpy.ActiveSectionOf(anchors, 350f, 800f));   // line = 550, only section 0 crossed
        Assert.Equal(1, Detail.ScrollSpy.ActiveSectionOf(anchors, 450f, 800f));   // line = 650, section 1's top (600) crossed
        Assert.Equal(2, Detail.ScrollSpy.ActiveSectionOf(anchors, 1250f, 800f));  // line = 1450, section 2 crossed, 3 not yet
        Assert.Equal(3, Detail.ScrollSpy.ActiveSectionOf(anchors, 2100f, 800f));  // line = 2300, every section crossed
    }

    [Fact]
    public void A_NaN_anchor_past_the_first_stops_the_scan_there()
        // section 2 not realized yet (still laying out) — the answer never reaches past section 1 even though
        // section 3 (a stale/late-realized node) reports a top that would otherwise qualify.
        => Assert.Equal(1, Detail.ScrollSpy.ActiveSectionOf([0f, 600f, float.NaN, 2200f], 2100f, 800f));

    [Fact]
    public void A_shrunken_or_zero_viewport_never_reads_active_sections_past_the_top()
        => Assert.Equal(0, Detail.ScrollSpy.ActiveSectionOf([0f, 600f], 0f, 0f));

    [Fact]
    public void Never_scrolled_negative_offset_still_answers_the_first_section()
        => Assert.Equal(0, Detail.ScrollSpy.ActiveSectionOf([0f, 600f], -50f, 800f));
}
