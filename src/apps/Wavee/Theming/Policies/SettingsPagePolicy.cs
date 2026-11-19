// ── Theming/Policies/SettingsPagePolicy.cs — what a structure decides for the registry's Settings page (contract 1.116) ──────────────
//
// Settings rework phase 4 (handoff/settings-2026-11-18/DESIGN.md 4.9, 7.4). The page's state and its decisions are core (the category, the
// arrival, the search); the numbers a core decision carries to the view come from the structure's own Rules through this policy, so the core
// never names a theme (WVTH004) and a theme's number lives in its Rules (WVTH003):
//   · `HighlightMs`  how long a link's arrival highlights the row (it holds, then fades back): `SettingsArrival.HighlightMs`. Every theme of the
//                    design uses 1.6 s; a structure that wants another declares it from its Rules record.

namespace Wavee.Theming;

/// <summary>What the structure decides for the registry's Settings page (see the file header).</summary>
/// <param name="HighlightMs">How long a link's arrival highlights its row, in milliseconds (1600 where the structure declares nothing).</param>
public sealed record SettingsPagePolicy(int HighlightMs)
{
    /// <summary>The design's arrival (4.9): a 1.6 s highlight.</summary>
    public static SettingsPagePolicy Default { get; } = new(HighlightMs: 1600);

    /// <summary>Everything wrong with the policy, empty when the page can run on it.</summary>
    public IReadOnlyList<string> Validate()
        => HighlightMs is < 0 or > 10_000 ? ["SettingsPage.HighlightMs must be 0..10000 (was " + HighlightMs + ")"] : [];
}
