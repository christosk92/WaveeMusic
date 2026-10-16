// ── Home/Facet.Props.cs — the props of the facet row (`Home.FacetRow`, T10a1) ──────────────────────────────────────────
//
// Role: CORE (data only)
//
// The facet row — the stock `SelectorBar` in the page-title style, the Following `ToggleButton`, the busy `ProgressBar` and the
// failure `InfoBar`, pinned as a transparent band — is Classic's view now (`Themes\Classic\Structure\Views\Home\ClassicHomeFacetRow.cs`,
// from the old `Facet.UI.cs`), with its numbers in `Rules\ClassicHomeRules.cs` (`SelectorH`, `BarH`, `StuckInset`, `PageScope`).
// What stays here is what the row is handed: the facet machine's own signals and verbs, owned by `HomeHost`.
//
// Selection: the SelectorBar OWNS `Index` (the host's `Signal<int>`, written on click/roving before OnChange fires); `HomeScreen`
// re-syncs it from `FacetPivot.Resolve(words, Switch.Target)` in an effect so a failed/no-op switch snaps the selection back. A word
// click selects `FacetPivot.Target(word, FollowingMemory(word.Id))` — the sub-chip when Following was last on for that word — so the
// toggle's memory survives leaving and re-entering the word (Facets.cs).

using FluentGpu.Signals;

namespace Wavee.HomeUi;

/// <summary>The props of <c>Home.FacetRow</c>. The two signals are the SAME long-lived objects across <c>HomeScreen</c>'s re-renders
/// (its per-tab fields); the row reads them while it renders and re-renders on a genuine change only.</summary>
/// <param name="Words">The pivot's words, "All" first (<c>FacetPivot.Words</c>).</param>
/// <param name="Switch">The facet-switch machine — <c>Target</c> is the selected facet, <c>Bar</c> drives the busy
/// bar, <c>Phase == Failed</c> the InfoBar.</param>
/// <param name="Index">The SelectorBar's selected index cell — owned by the host, written by the bar on click and re-synced from
/// <c>Switch.Target</c> by the screen's own effect. A <c>Func</c> because a props record carries no writable <c>Signal</c> (WVTH006);
/// the row asks for the cell when it builds the bar, and it is the same cell every time.</param>
/// <param name="Select">Selects a facet id (a word's id or its Following sub-chip id) through <c>HomeScreen</c>'s
/// one select path (verdict + state machine + fetch + history push). Nothing here touches <c>Entities</c>/<c>Shell</c>.</param>
/// <param name="Dismiss">Closes the failure InfoBar (<c>FacetSwitch.Dismiss</c>).</param>
/// <param name="FollowingMemory">"Was Following last on for this word id?" — read-only; decides which target a plain
/// word click carries.</param>
public sealed record FacetRowProps(
    IReadSignal<FacetWord[]> Words,
    IReadSignal<FacetSwitchState> Switch,
    Func<Signal<int>> Index,
    Action<string> Select,
    Action Dismiss,
    Func<string, bool> FollowingMemory)
{
    /// <summary>Opens or closes the friend-activity panel (contract 1.22): a structure that puts a Friend activity button on the facet row (Classic
    /// Compact, <c>C-Home-Compact</c>) calls it. Null where the host offers none.</summary>
    public Action? ToggleFriends { get; init; }
}
