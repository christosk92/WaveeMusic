// ── Wavee.ShellTests/ClassicEmptyLeadMountedTests.cs — the empty playlist's lead at the plan's sizes (C-Empty, C-Empty-Compact) ──────
//
// An empty playlist's Recommended songs are led by "Nothing here yet", its line and a field that finds songs to add. Compact draws it from C-Empty-Compact (the 20/28
// message over a 40 field) and Small, Standard and Huge from C-Empty (the 28/36 message over a 32 field). Mounted alone under Classic at each size, held on the RECORD
// (`ClassicDetailTiers.Of(...).Lead`), never on pixels:
//
//   the record in use     the window tier the harness published picks the lead's record, in the first frame and after more frames
//   the lead is drawn     at every tier, ahead of the header, inside the window (no horizontal overflow), the field the record's height and the header under it
//   a reserved height     the lead is never shorter than the record's `Height`, so the rows under it start on the same DIP whatever the string or the wrap
//   one crossing          a window resized across the Compact | Small edge keeps the lead (one node), never shrinks below the smaller record's height and settles on the new one
//
// The tests never name a Classic edge: they read the tier the harness published.

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class ClassicEmptyLeadMountedTests(ShellHost host)
{
    static readonly Action s_noop = static () => { };

    static ViewHarnessOptions At(float w, float h) => new() { Composition = ClassicTheme.Create(), WidthDip = w, HeightDip = h };

    /// <summary>An empty playlist's section: no rows, no suggestions yet (the lead is the section's head while the list is empty).</summary>
    static PlaylistRecsProps Empty() => new(Loading: false, Empty: false, s_noop, []) { Lead = new PlaylistRecsLead(static _ => { }) };

    /// <summary>The plan's window sizes (3.5) and the threshold edges that apply to the window ladder (527/528, 1007/1008, 1919/1920).</summary>
    public static TheoryData<float, float> Sizes() => new()
    {
        { 300f, 640f }, { 400f, 860f }, { 527f, 700f }, { 528f, 700f }, { 800f, 600f }, { 1007f, 700f }, { 1008f, 700f },
        { 1440f, 960f }, { 1919f, 1000f }, { 1920f, 1056f }, { 2560f, 1440f }, { 3840f, 2160f },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void The_lead_is_drawn_at_every_tier_in_its_records_reserved_height(float width, float height)
        => ViewHarness.With(host, ViewKeys.PlaylistRecs, Empty(), harness =>
        {
            var first = harness.Facts.Peek().Tiers;
            harness.RunFrames(40);
            var tiers = harness.Facts.Peek().Tiers;
            Assert.Equal(first.Window, tiers.Window);   // the tier is the first frame's, and it does not change in the frames after

            var record = ClassicDetailTiers.Of(tiers);
            var lead = record.Lead;
            bool compact = tiers.Window == WidthTier.Compact;
            Assert.Same(compact ? ClassicEmptyLead.Compact : ClassicEmptyLead.Roomy, lead);

            Assert.True(harness.Contains("rec:lead"), $"{width}x{height}: the empty playlist's lead is not drawn");
            Assert.True(harness.Contains("rec:lead:message") && harness.Contains("rec:lead:find") && harness.Contains("rec:header"));
            var box = harness.AbsoluteBounds(harness.Node("rec:lead"));
            var header = harness.AbsoluteBounds(harness.Node("rec:header"));
            var field = harness.AbsoluteBounds(harness.Node("rec:lead:find"));

            // Reserved height: never under the record's, so the header under it starts on the same DIP whatever the string or the wrap.
            Assert.True(box.H >= lead.Height - 0.5f, $"{width}x{height}: the lead is {box.H} tall, under its record's reserved {lead.Height}");
            Assert.True(field.H >= lead.FieldH - 0.5f, $"{width}x{height}: the find field is {field.H} tall, under the record's {lead.FieldH}");
            Assert.True(header.Y >= box.Y + box.H - 0.5f, $"{width}x{height}: the header ({header}) overlaps the lead ({box})");

            // No horizontal overflow: the lead lies inside the window.
            Assert.True(box.X >= -0.5f && box.X + box.W <= width + 0.5f, $"{width}x{height}: the lead runs outside the window ({box})");
            Assert.True(field.X >= -0.5f && field.X + field.W <= width + 0.5f, $"{width}x{height}: the find field runs outside the window ({field})");
        }, At(width, height));

    [Fact]
    public void A_playlist_with_rows_has_no_lead_at_any_tier()
    {
        foreach (var (w, h) in new[] { (400f, 860f), (800f, 600f), (1440f, 960f), (2560f, 1440f) })
            ViewHarness.With(host, ViewKeys.PlaylistRecs, new PlaylistRecsProps(false, false, s_noop, []), harness =>
            {
                harness.RunFrames(10);
                Assert.False(harness.Contains("rec:lead"), $"{w}x{h}: a lead without an empty list");
                Assert.True(harness.Contains("rec:header"));
            }, At(w, h));
    }

    [Fact]
    public void A_window_resized_across_the_compact_edge_keeps_the_lead_and_settles_on_the_new_records_height()
        => ViewHarness.With(host, ViewKeys.PlaylistRecs, Empty(), harness =>
        {
            harness.RunFrames(40);
            var narrow = ClassicDetailTiers.Of(harness.Facts.Peek().Tiers);
            Assert.Equal(WidthTier.Compact, narrow.Tier);
            var node = harness.Node("rec:lead");
            Assert.True(harness.LocalBounds(node).H >= ClassicEmptyLead.Compact.Height - 0.5f);

            harness.Resize(800f, 600f);
            harness.RunFrames(1);
            Assert.Equal(node, harness.Node("rec:lead"));   // one node across the crossing: the lead is eased, never rebuilt
            // Mid-crossing the box is between the two reserved heights (it eases from the Compact one to the roomy one), never under the smaller.
            Assert.True(harness.LocalBounds(node).H >= ClassicEmptyLead.Compact.Height - 0.5f, "the lead dipped under the Compact record's height mid-crossing");

            harness.RunFrames(60);
            var wide = ClassicDetailTiers.Of(harness.Facts.Peek().Tiers);
            Assert.NotEqual(narrow.Tier, wide.Tier);
            Assert.Same(ClassicEmptyLead.Roomy, wide.Lead);
            Assert.True(harness.LocalBounds(harness.Node("rec:lead")).H >= ClassicEmptyLead.Roomy.Height - 0.5f, "the lead did not settle on the roomy record's height");
        }, At(400f, 860f));
}
