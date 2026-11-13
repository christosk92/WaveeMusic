// ── Theming/TierContext.cs — `ctx.Tier()`: how a template reads the window tiers (themes plan 3.2, T3b) ────────────────
//
// A view never compares a width with a number (WVTH013, T3c). It reads the tier it is in and its structure's layout record
// for that tier:
//
//     var tier = ctx.Tier();
//     var layout = tier.Content >= WidthTier.Huge ? HugeLayout : StandardLayout;   // a record the structure's Rules\Tiers*.cs owns
//
// The read subscribes the template to the facts signal `ThemeRoot` provides, which changes only when a TIER crosses (and a
// rung, and the modal size-move edge), never once per resized frame: the engine commits a crossing on the first frame that
// is not resized (plan 3.3), so the template re-renders once, on the frame the crossing animates.

using System.Runtime.CompilerServices;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using FluentGpu.Theming;

namespace Wavee.Theming;

/// <summary>The template-side reader of <see cref="TierFacts"/>.</summary>
public static class TierContext
{
    /// <summary>The window tiers on screen now. <see cref="TierFacts.Unknown"/> where no root provides the facts (a bare unit
    /// test of a view); a harness that mounts a view alone provides facts resolved from its window size.</summary>
    public static TierFacts Tier(this in ViewContext ctx)
    {
        var facts = ctx.Hooks.UseContext(ThemeFactsContext.Signal);
        return facts is null ? TierFacts.Unknown : facts.Value.Tiers;
    }

    /// <summary>The WINDOW's width tier alone (Standard where no root resolved the tiers: a view mounted alone), for a view whose structure follows the
    /// window and never the content region (the play bar, the side panel). It reads the facts through a memo, so the view re-renders when the window
    /// tier crosses, never when only the content tier, a rung or the published widths move: a docking panel's slide crosses the content tier while the
    /// window stays put, and a template subscribed to the whole facts re-rendered for nothing on the slide's commit frame.</summary>
    public static WidthTier WindowTier(this in ViewContext ctx, [CallerFilePath] string? file = null, [CallerLineNumber] int line = 0)
    {
        var facts = ctx.Hooks.UseContext(ThemeFactsContext.Signal);
        return ctx.Hooks.UseComputed(() => WindowOf(facts is null ? TierFacts.Unknown : facts.Value.Tiers), file, line).Value;
    }

    /// <summary>The window's width tier of <paramref name="tiers"/>: Standard where no policy resolved them (the records' rule for a view mounted alone).</summary>
    public static WidthTier WindowOf(in TierFacts tiers) => tiers.Policy is null ? WidthTier.Standard : tiers.Window;

    /// <summary>The window tiers as a LIVE reader for a bound channel (<c>Prop.Of(() => record(tiers()).X)</c>): calling it inside a bind
    /// subscribes that bind, never the template, so a template that renders once (the frame) follows a crossing without re-rendering.
    /// <see cref="TierFacts.Unknown"/> where no root provides the facts.</summary>
    public static Func<TierFacts> TierReader(this in ViewContext ctx)
    {
        var facts = ctx.Hooks.UseContext(ThemeFactsContext.Signal);
        return facts is null ? static () => TierFacts.Unknown : () => facts.Value.Tiers;
    }

    /// <summary>The facts a CORE COMPONENT is drawn in (a host has no view context): the signal its root provides (`ThemeRoot`, or a harness mounting a
    /// view under its own composition and window), else the process's, for a host mounted outside any root. A component passes
    /// <c>UseContext(ThemeFactsContext.Signal)</c>; reading <c>.Value</c> subscribes it to a crossing, <c>.Peek()</c> does not. Never the process's
    /// facts where a root provides others: a composition mounted at another size than the app window draws in its own tiers.</summary>
    public static IReadSignal<ThemeFacts> DrawnIn(IReadSignal<ThemeFacts>? provided) => provided ?? ThemeHost.Facts;
}
