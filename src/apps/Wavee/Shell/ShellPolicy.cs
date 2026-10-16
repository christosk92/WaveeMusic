// ── Shell/ShellPolicy.cs — the active theme's policies, as the core state machines read them ─────────────────────────
//
// Role: CORE (themes plan 5.5: "core models read the facts signal, never a Context")
//
// The pane, panel, player-bar, chrome and stage state machines are pure functions that take their policy as a trailing
// argument. A caller that does not pass one gets the ACTIVE policy (`ThemeHost.Policies`, the policy half of
// `ThemeHost.Facts` on a signal of its own): the structure's, or the shell's baseline (`ThemePolicies.Default`) before a
// composition is installed. Two ways to ask:
//   · `Now`      a plain read (no subscription): verbs, event handlers, one-shot decisions.
//   · `Tracked`  a read that subscribes the running effect or render, so a policy change (a theme switch republishes the
//                facts, and so would a live option that moves a policy) re-runs it. It does not re-run on a tier change.

using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>The active policies.</summary>
public static class ShellPolicy
{
    /// <summary>The active policies, read without subscribing.</summary>
    public static ThemePolicies Now => ThemeHost.Policies.Peek();

    /// <summary>The active policies, read inside an effect or a render so it subscribes to a change.</summary>
    public static ThemePolicies Tracked => ThemeHost.Policies.Value;

    internal static NavPanePolicy NavOr(NavPanePolicy? given) => given ?? Now.NavPane;

    internal static PanelPolicy PanelOr(PanelPolicy? given) => given ?? Now.Panel;

    internal static PlayerBarPolicy PlayerBarOr(PlayerBarPolicy? given) => given ?? Now.PlayerBar;

    internal static ChromeBudget BudgetOr(ChromeBudget? given) => given ?? Now.Chrome.Budget;

    /// <summary>The title-bar budget of the window tier on screen (the structure's per-tier budget, <see cref="ChromePolicy.BudgetFor(WidthTier)"/>),
    /// read without subscribing; the base budget where no frame resolved the tiers.</summary>
    internal static ChromeBudget ChromeBudgetNow => Now.Chrome.BudgetFor(ThemeHost.Facts.Peek().Tiers);

    internal static AspectThresholds AspectOr(AspectThresholds? given) => given ?? Now.Stage.Aspect;
}
