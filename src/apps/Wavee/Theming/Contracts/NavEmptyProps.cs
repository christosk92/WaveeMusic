// ── Theming/Contracts/NavEmptyProps.cs — the props of `Shell.NavEmpty` (themes plan 6.0, T5b) ─────────────────────────────────
//
// The pane body when the plan has no rows: the authored-empty pane (it names the state only, there is no customizer to offer a fix
// from) in the expanded pane, and in the compact rail a bare spacer, because the rail has no room for the hint. The core pane keeps
// WHEN (the published row count is zero) and which form (the presented compact flag); the structure draws it.
//
// Two static instances, so the record is equal across the pane's re-renders and an equal record skips the template.

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.ShellNavEmpty"/>.</summary>
public sealed record NavEmptyProps
{
    /// <summary>The expanded pane's empty body.</summary>
    public static readonly NavEmptyProps Pane = new() { Compact = false };

    /// <summary>The compact rail's empty body (just a spacer under the head).</summary>
    public static readonly NavEmptyProps Rail = new() { Compact = true };

    /// <summary>Is the pane presented as the compact rail?</summary>
    public required bool Compact { get; init; }
}
