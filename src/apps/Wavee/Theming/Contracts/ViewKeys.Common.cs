// ── Theming/Contracts/ViewKeys.Common.cs — the Common blocks' published view keys (themes plan 6.0 / 6.5, T11c) ────────────────────
//
// T11c writes the utility pages every theme shares from today's code (`Themes\Common\`, registered by every structure): the History page here;
// the diagnostics and logs pages (ViewKeys.Diagnostics.cs), the Home customizer (`Pages.HomeCustomize`, ViewKeys.Home.cs), the frame-rate
// overlay (`Shell.Fps`, ViewKeys.FrameBits.cs) and the not-found page (the content state's NotFound arm) are the other Common blocks.
//
// What stays core (`Shell.HistoryPage` + `Shell.History`, `Shell\HistoryVerbs.cs`): the navigation log, the filter, the search, the sort, the most-visited
// fold, the date buckets and the timestamps, the row facts (title, glyph, reachability) and the two destructive verbs. A theme draws what the model
// carries.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The history page (the `history` route). Mounted by `Shell.HistoryPage`. Props: <see cref="HistoryPageProps"/>.</summary>
    public static readonly ViewKey<HistoryPageProps> HistoryPage = new("Pages.History");
}
