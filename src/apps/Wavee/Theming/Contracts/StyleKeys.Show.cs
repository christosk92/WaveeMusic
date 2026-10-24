// ── Theming/Contracts/StyleKeys.Show.cs — the podcast show page's style key (themes plan 6.0, T9c) ────────────────────────────────
//
// The show page is the shared detail frame (T9a) with a rail of podcast slots on the left and the episode reader on the right. Its core
// hosts keep every hook, memo, handle and decision (the reader model, the snapshot, the toolbar stage, the selection, the extents, the
// reveal gates, the follow state); the PICTURES they compose are one STYLE (`ShowReaderStyle`), read through `ListStyleLookup` like the
// list surfaces (T8c): a rail slot, the sticky toolbar, the visit head, a month header and the foot are PURE builders that the reader's
// bound list, the skeleton deriver and the rail's slot callbacks can all call outside a view slot.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class StyleKeys
{
    /// <summary>The show page's pictures: the rail's slots (primary, ledger, rating, badges, topics, publisher, satellites), the library pane's
    /// column, and the reader's toolbar, visit head, "episodes" header, month header, sticky month, year strip, foot and cold seed
    /// (<see cref="ShowReaderStyle"/>).</summary>
    public static readonly StyleKey<ShowReaderStyle> ShowReader = new("Show.Reader");
}
