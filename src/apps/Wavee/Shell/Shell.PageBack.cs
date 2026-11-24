// ── Shell/Shell.PageBack.cs ────────────────────────────────────────────────────────────────────────────────────────
// a page's own steps inside one route (a detail it opened over its list, #240 "On this PC"): Back and Esc step out of them first, and the
// connected flight flies a cover between the page's list and its detail as it does between two pages
//
// Role: SHELL
// Owner: I
//
// THE STEP. A page that opens a detail in place (state, not a route: "On this PC" opens a folder or an album's folder over the pivot it was
// opened from) registers its step under its route key (`SetPageBack`): a tracked "is a step open" and the verb that takes one out. The title
// bar's Back shows while the CURRENT route's page has one open (`CanGoBack`), `GoBack` (the button, Alt+Left, the mouse's Back) and a bare Esc
// that nothing else took (`OnShellKey`) take that step before they leave the page. A parked page (keep-alive) is not the current route: its
// step waits for it.
//
// THE FLIGHT. `FlyInPage` is the route seam's flight (Shell.Connected.cs) for a step inside a page: the structure's
// `PageMotionPolicy.Connected` spec, the Connected animations setting, the engine's `BeginNav` with the step's key before the step is written.
// Forward flies the tagged picture inside the element the user activated into the like-tagged header the step mounts; Back flies the header
// home into its tile. A structure that flies nothing (Classic, Zune today), or the setting off, still tells the engine (an empty key), so a
// running flight is abandoned.

using FluentGpu.Animation;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Shell
{
    /// <summary>One page's step: whether it has one open (tracked) and the verb that takes it out (true when it did).</summary>
    readonly record struct PageStep(Func<bool> Open, Func<bool> Back);

    static readonly Dictionary<string, PageStep> s_pageSteps = new(StringComparer.Ordinal);

    /// <summary>Bumped whenever a page registers or withdraws its step, so <see cref="CanGoBack"/> re-reads the table.</summary>
    static readonly Signal<int> s_pageStepsEpoch = new(0);

    /// <summary>Register (or, with null verbs, withdraw) the step of the page at <paramref name="routeKey"/> (<see cref="NameOf"/> of its route).
    /// UI thread; from an effect, never a render.</summary>
    public static void SetPageBack(string routeKey, Func<bool>? open, Func<bool>? back)
    {
        if (open is null || back is null) { if (s_pageSteps.Remove(routeKey)) s_pageStepsEpoch.Value++; return; }
        s_pageSteps[routeKey] = new PageStep(open, back);
        s_pageStepsEpoch.Value++;
    }

    /// <summary>Does Back go anywhere: the history, or a step the current page has open? Tracked (the title bar's Back reads it).</summary>
    public static bool CanGoBack()
    {
        if (CanBack.Value) return true;
        _ = s_pageStepsEpoch.Value;
        return s_pageSteps.TryGetValue(NameOf(Current.Value), out var step) && step.Open();
    }

    /// <summary>Take the current page's open step out, if it has one: true when it did (Back then leaves the page no further).</summary>
    static bool PageStepBack()
        => s_pageSteps.TryGetValue(NameOf(Current.Peek()), out var step) && step.Back();

    /// <summary>Tell the connected flight about a step inside a page, before the step is written: fly the picture tagged <paramref name="key"/>
    /// (inside the activated element forward, the largest visible one Back) to the like-tagged end the step shows. True when a flyer is up
    /// (the step's own swap then keeps only its fades). Empty key, the setting off or a structure that flies nothing: abandons a running flight.</summary>
    public static bool FlyInPage(string key, bool back)
    {
        var spec = Theming.ThemeHost.Facts.Peek().Policies.PageMotion.Connected;
        if (s_beginNav is not { } begin) return false;
        if (spec is null || !Platform.Settings.Get(Platform.Keys.ConnectedAnimations)) key = "";
        // The step's end mounts inside a list that measures its width before it realizes its rows (a virtual list): it lands a frame or two
        // after the click, where a page's header mounts on it. The flyer holds at its tile for that long (it shows exactly the tile) before
        // it gives up.
        var request = spec is { } held ? held with { PendingMs = MathF.Max(held.PendingMs, InPagePendingMs) } : default;
        return begin(new ConnectedNavRequest(key, back, request));
    }

    /// <summary>The longest a step's flyer holds at its source for the end to mount (ms): three frames.</summary>
    public const float InPagePendingMs = 50f;
}
