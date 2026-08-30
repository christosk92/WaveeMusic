using System;
using FluentGpu.Signals;

namespace Wavee;

// DEVELOPER MODE — the ONE switch that reveals every tooling surface in the app.
//
// Wavee shipped its developer affordances as permanent product surface: the API console sat in Classic's sidebar and in
// Library V3's overflow menu, the lyrics inspector sat in the lyrics rail header for everyone, every notification topic
// carried a "Send event" row, and the home image tracer was gated on a WAVEE_HOME_IMAGE_DIAG environment variable that
// nobody can flip from inside the running app. That is four different visibility mechanisms for one audience.
//
// It is now ONE persisted setting (Settings ▸ Diagnostics ▸ Developer mode) mirrored into ONE signal. Everything that is
// developer surface reads `DeveloperMode.Enabled` and composes itself away when it is off — there is no env var, no
// second gate and no per-surface preference. Off by default: a normal listener never meets any of it.
//
// LIVE, NOT NEXT-LAUNCH. `Enabled` is a signal, so every reader that reads `.Value` inside a render re-renders on the
// flip. The one indirect reader worth naming is Classic's LOCKED sidebar document: `ClassicDocumentCache.Get` reads
// `Enabled.Value` unconditionally (it runs inside `SidebarPane`'s own render, so that read IS the pane's subscription)
// and folds the answer into its cache key — so writing this signal both re-renders the pane AND forces the document
// rebuild that adds/removes the API-console row. `Set` therefore needs no separate "bump the sidebar" call; the signal
// write is the trigger.
static class DeveloperMode
{
    /// <summary>The live switch. Read <c>.Value</c> inside a render to compose a developer surface conditionally; read
    /// <c>.Peek()</c> from a pure/one-shot decision that must not subscribe.</summary>
    public static readonly Signal<bool> Enabled = new(false);

    /// <summary>The FPS HUD switch (Settings › Diagnostics). A signal for the same reason as <see cref="Enabled"/>: WaveeApp
    /// reads it inside Render, so flipping the row alone re-renders the root.</summary>
    public static readonly Signal<bool> FpsOverlay = new(false);

    /// <summary>Seed the signal from the persisted setting. Called ONCE at startup (composition root), before the first
    /// mount, so no surface ever paints its developer form for a frame and then removes it.</summary>
    public static void Load(IAppSettings? settings)
    {
        Enabled.Value = settings?.Get(WaveeSettings.DeveloperMode) ?? false;
        FpsOverlay.Value = settings?.Get(WaveeSettings.FpsOverlay) ?? false;
    }

    /// <summary>Flip the FPS HUD: persist, then publish.</summary>
    public static void SetFpsOverlay(IAppSettings? settings, bool on)
    {
        settings?.Set(WaveeSettings.FpsOverlay, on);
        FpsOverlay.Value = on;
    }

    /// <summary>Flip the switch: persist it, then publish it. The order matters only for a reader that re-reads the
    /// setting rather than the signal — but every reader is supposed to read the signal, and the write below is what
    /// makes them all re-render (including the sidebar's built-in documents; see the file header).</summary>
    public static void Set(IAppSettings? settings, bool on)
    {
        settings?.Set(WaveeSettings.DeveloperMode, on);
        Enabled.Value = on;
    }

    /// <summary>The DEVELOPER-ONLY routes: pages that exist to inspect the app, not to listen to music. Kept as one
    /// list rather than a condition spelled at each call site, because the sidebar offers routes from three places
    /// (Classic's document, the Curated customizer's destination palette, the static-links picker) and a route hidden
    /// in two of them is worse than a route hidden in none.</summary>
    public const string ApiConsoleRoute = "api-console";

    /// <summary>Is <paramref name="route"/> a developer-only route? Pure — the visibility RULE, testable without a
    /// window.</summary>
    public static bool IsDeveloperRoute(string? route)
        => string.Equals(route, ApiConsoleRoute, StringComparison.Ordinal);

    /// <summary>May <paramref name="route"/> be OFFERED (a sidebar row, a palette entry, a picker row) while developer
    /// mode is <paramref name="enabled"/>? Every non-developer route is always offered; a developer route is offered
    /// only in developer mode.
    ///
    /// <para>This is deliberately about OFFERING, never about reachability: a route a user already placed in their own
    /// Curated document keeps rendering (the sidebar's "never auto-remove a user's row" rule), and a deep link to the
    /// console still resolves. Turning developer mode off hides the doors, it does not brick the page.</para></summary>
    public static bool ShowsRoute(string? route, bool enabled) => enabled || !IsDeveloperRoute(route);
}
