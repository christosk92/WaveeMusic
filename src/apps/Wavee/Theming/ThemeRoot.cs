// ── Theming/ThemeRoot.cs — the providers around the root frame, and the remount a theme switch needs ──────────────────
//
// `RootHost` (the window) and `HeadlessRoot` (the shell test host) both mount `Shell.RootFrame()`, which wraps the frame in
// `ThemeRoot.Wrap`: the three contexts every themed view resolves through, and nothing else.
//
//   · ViewRegistry.Context     the active composition's views and style tables (`Slots.View` resolves keys through it)
//   · OptionStore.Context      the active composition's option cells (`ctx.Options` / `ctx.OptionRequests`)
//   · ThemeFactsContext.Signal the facts SIGNAL (policies, capabilities, metrics, tiers), so a live change re-renders the
//                              readers without re-rendering the root
//
// The three providers are layout-transparent boundaries and carry no key: the node tree below them is exactly the frame.
//
// REMOUNT. Wrap reads `ThemeHost.RootEpoch`, so the component that calls it re-renders on a theme switch. At epoch 0 (every
// run until the first switch) the frame is returned as it is. From epoch 1 on it is wrapped in a component whose type
// alternates between two classes on every switch; the engine replaces a component whose type differs, which disposes the
// old frame's whole scope (hook cells, effects, timers, pages) and mounts the new one fresh. A plain element Key on the
// root would be ignored by the reconciler's single-child slot.

using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Theming;

namespace Wavee.Theming;

/// <summary>Wraps the root content in the theme providers.</summary>
public static class ThemeRoot
{
    /// <summary>The content inside the theme providers. Call it from a component's <c>Render</c> (it subscribes that component
    /// to the theme switch). <paramref name="content"/> builds the frame and runs once per render.</summary>
    public static Element Wrap(Func<Element> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        long epoch = ThemeHost.RootEpoch.Value;
        Element body = epoch == 0L
            ? content()
            : (epoch & 1L) == 1L
                ? Embed.Comp(() => new RootBodyA(content))
                : Embed.Comp(() => new RootBodyB(content));
        return Ctx.Provide(ViewRegistry.Context, ThemeHost.Registry,
            Ctx.Provide(OptionStore.Context, ThemeHost.Options,
                Ctx.Provide(ThemeFactsContext.Signal, ThemeHost.Facts, body)));
    }

    private abstract class RootBody(Func<Element> content) : Component
    {
        public sealed override Element Render() => content();
    }

    private sealed class RootBodyA(Func<Element> content) : RootBody(content);

    private sealed class RootBodyB(Func<Element> content) : RootBody(content);
}
