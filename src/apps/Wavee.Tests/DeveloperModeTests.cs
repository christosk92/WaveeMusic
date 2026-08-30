using Xunit;

namespace Wavee.Tests;

// DEVELOPER MODE — the ONE switch behind every tooling surface in the app (App/DeveloperMode.cs).
//
// Wavee used to reveal its developer affordances four different ways: the API console was permanent product surface in
// two sidebar designs, the lyrics inspector was permanent chrome in the lyrics rail, every notification topic carried a
// "Send event" row, and the home image tracer was gated on an environment variable that cannot be flipped from inside a
// running app. One persisted setting mirrored into one signal replaced all four.
//
// WHAT IS PINNED HERE is the pure half: the persisted key's shape (a wrong default ships the whole developer surface to
// every listener) and the OFFER RULE — a route hidden in two of the three sidebar offer sites but not the third is
// exactly the drift a screenshot review misses. `SidebarBuiltInDocumentTests` drives the same rule through the real
// Classic document builder.
//
// WHAT IS DELIBERATELY NOT HERE: anything that writes `DeveloperMode.Enabled`. That signal is process-wide, xUnit runs
// test classes in parallel, and `ClassicDocumentCache` reads it — so a test that flipped it would make every other
// sidebar test in the run order-dependent. The signal's own behaviour (a write re-renders the pane, which rebuilds the
// document) is a live-run concern, not a unit-test one.
public sealed class DeveloperModeTests
{
    // ── the offer rule ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Off, the API console is not OFFERED anywhere; on, it is offered everywhere. Nothing else is affected —
    /// the rule is a deny-list of developer routes, not an allow-list of product ones, so a route added to the app
    /// tomorrow is visible by default rather than accidentally hidden.</summary>
    [Theory]
    [InlineData("api-console", false, false)]
    [InlineData("api-console", true, true)]
    [InlineData("settings", false, true)]
    [InlineData("settings", true, true)]
    [InlineData("home", false, true)]
    [InlineData("concerts", false, true)]
    [InlineData("", false, true)]
    [InlineData(null, false, true)]
    public void ShowsRoute_HidesDeveloperRoutesOnlyWhileTheSwitchIsOff(string? route, bool enabled, bool shown)
        => Assert.Equal(shown, DeveloperMode.ShowsRoute(route, enabled));

    /// <summary>The route constant IS the route the sidebar document, the customizer palette and the static-links
    /// picker all name. Spelled once so the three offer sites cannot drift onto three different strings.</summary>
    [Fact]
    public void ApiConsoleRoute_IsTheOneDeveloperRoute()
    {
        Assert.Equal("api-console", DeveloperMode.ApiConsoleRoute);
        Assert.True(DeveloperMode.IsDeveloperRoute(DeveloperMode.ApiConsoleRoute));
        Assert.True(DeveloperMode.IsDeveloperRoute("api-console"));
        Assert.False(DeveloperMode.IsDeveloperRoute("API-CONSOLE"));   // ordinal: routes are lowercase identifiers
        Assert.False(DeveloperMode.IsDeveloperRoute("settings"));
        Assert.False(DeveloperMode.IsDeveloperRoute(null));
    }

    /// <summary>Hiding is about the OFFER, never about reachability: a Curated document that already contains the route
    /// keeps rendering it (the sidebar's "never auto-remove a user's row" rule) and a deep link still resolves. Turning
    /// the switch on therefore only ever ADDS offers — it can never take one away.</summary>
    [Theory]
    [InlineData("api-console")]
    [InlineData("settings")]
    [InlineData("home")]
    public void ShowsRoute_IsMonotone_TurningTheSwitchOnNeverHidesAnything(string route)
    {
        if (DeveloperMode.ShowsRoute(route, false)) Assert.True(DeveloperMode.ShowsRoute(route, true));
    }

    // ── the persisted key ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A fresh profile is a LISTENER's profile. This is the one assertion that keeps the entire developer
    /// surface out of a normal install: every reader is `settings.Get(WaveeSettings.DeveloperMode)`, and an absent key
    /// returns the key's own default.</summary>
    [Fact]
    public void DeveloperMode_DefaultsOffOnAFreshProfile()
    {
        var settings = new MemoryAppSettings();

        Assert.False(settings.Get(WaveeSettings.DeveloperMode));
        Assert.False(settings.WasWritten(WaveeSettings.DeveloperMode));
        Assert.False(WaveeSettings.DeveloperMode.Default);
    }

    /// <summary>The switch round-trips through the settings seam in BOTH directions — turning developer mode off has to
    /// be as durable as turning it on, or the next launch quietly re-reveals everything.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeveloperMode_RoundTripsThroughTheSettingsSeam(bool on)
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.DeveloperMode, on);

        Assert.True(settings.WasWritten(WaveeSettings.DeveloperMode));
        Assert.Equal(on, settings.Get(WaveeSettings.DeveloperMode));
    }
}
