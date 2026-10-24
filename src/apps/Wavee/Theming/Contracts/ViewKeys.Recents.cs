// ── Theming/Contracts/ViewKeys.Recents.cs — the Recents page's published view key (themes plan 6.0, T10c2) ──────────────────────────
//
// T10c2 publishes the Recents page (`Pages.Recents`): the masthead, the pivot tabs, the measured grouped list with its pinned day band and its annotated
// rail, the drawer accordion, the group card and the single-play row, the semantic-zoom calendar overview and the skeleton.
//
// What stays core (`Recents.PageView`, a `[CoreHost]`): the demand, the shape (the snapshot, the pivot's cut, the sections, the calendar, the stateful measured
// layout) and its atomic publication, the midnight rollover, the refresh after a play, the accordion's state, the sticky band's arithmetic, the page accent and
// its shell wash, the owner demand, the rail's label math and every verb. A theme draws what the props and the host carry.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The Recents page. Mounted by `Recents.PageView` (the host). Props: <see cref="RecentsPageProps"/>.</summary>
    public static readonly ViewKey<RecentsPageProps> RecentsPage = new("Pages.Recents");
}
