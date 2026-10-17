// ── Wavee.Tests/QueueRowRulesTests.cs — the queue row's pure decisions the core owns ────────────────────────────────────────
//
// Which row has a ✕, which row drags on its own, and that no queue surface grows a "…" button (`QueueRowRules`, Entities/Queue.UI.cs):
// the queue model and the rail's "Next up" rows read them. The row's height under each skin, the autoplay dim, which adapter feeds a row and
// where a click on the playing card goes are the structure's (`ClassicQueueRulesTests`).
//
// No window, no loop, no table is touched.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class QueueRowRulesTests
{
    [Theory]
    [InlineData(false, 7UL, true)]    // our queue, an entry with an item id: removable
    [InlineData(false, 0UL, false)]   // no item id to name it by
    [InlineData(true, 7UL, false)]    // a remote device's mirror is read-only
    [InlineData(true, 0UL, false)]
    public void A_row_has_an_x_when_the_queue_is_ours_and_the_entry_is_named(bool viewer, ulong itemId, bool removable)
    {
        Assert.Equal(removable, QueueRowRules.Removable(viewer, itemId));
    }

    [Fact]
    public void A_row_drags_on_its_own_only_when_no_reorder_lane_does()
    {
        Assert.False(QueueRowRules.RowDrags(viewer: false));   // the lane's wrapper is the drag source
        Assert.True(QueueRowRules.RowDrags(viewer: true));     // a viewed queue has no lane
    }

    // ── the "…" button ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_queue_surface_grows_a_menu_button()
    {
        // The rows (both skins), the now-playing card and the NPV's Next up are right-click only: the surface's "…"
        // overlays the row's end, which here is the ✕ or the heart.
        Assert.False(QueueRowRules.ShowsMenuButton);
    }
}
