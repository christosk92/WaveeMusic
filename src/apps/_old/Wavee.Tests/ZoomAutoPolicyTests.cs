using System;
using Xunit;

namespace Wavee.Tests;

public class ZoomAutoPolicyTests
{
    // ── the §3.2 worked table, row for row ──────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(1664f, 1109f, 1f)]      // laptop maximized, 150% OS DPI — unchanged, correct today
    [InlineData(3440f, 1392f, 1.5f)]    // ultrawide maximized, 100% OS DPI — every structural promise kept
    [InlineData(1920f, 1080f, 1f)]      // 1.20 is the coin-flip; snap-down keeps it at 100%
    [InlineData(2560f, 1440f, 1.5f)]    // 1.60 → 150% (the pane demotes to Mid — a separate, out-of-scope Layer C item)
    [InlineData(3840f, 2160f, 2f)]      // 2.40 clamps to the 200% ceiling
    [InlineData(1366f, 768f, 1f)]       // 0.85 never shrinks below 100% in Auto
    [InlineData(3440f, 700f, 1f)]       // the wide sliver — the height guard: width alone would want 200%+
    public void Suggest_Auto_MatchesTheWorkedTable(float baseW, float baseH, float expected)
        => Assert.Equal(expected, ZoomAutoPolicy.Suggest(baseW, baseH, ZoomAutoMode.Auto));

    /// <summary>3840×2160@150% and 2560×1440@100% present the IDENTICAL base box (2560×1440) — the property that makes
    /// this policy a proportion fix, not a DPI hack. Suggest never sees the physical/DPI numbers, only the base dips,
    /// so this holds by construction; the test exists to catch a future refactor that starts taking DPI as an input.</summary>
    [Fact]
    public void Suggest_IsDpiIndependent_ForTheIdenticalBaseBox()
    {
        // 3840x2160 @ 150% OS DPI -> base = 3840/1.5 x 2160/1.5 = 2560x1440, same as the 2560x1440@100% row above.
        float a = ZoomAutoPolicy.Suggest(3840f / 1.5f, 2160f / 1.5f, ZoomAutoMode.Auto);
        float b = ZoomAutoPolicy.Suggest(2560f, 1440f, ZoomAutoMode.Auto);
        Assert.Equal(b, a);
        Assert.Equal(1.5f, a);
    }

    [Theory]
    [InlineData(1366f, 768f, ZoomAutoMode.Auto, 1f)]         // never shrinks below 100% unless asked
    [InlineData(1366f, 768f, ZoomAutoMode.Dense, 0.75f)]     // Dense may go below 100% (Slack's 80% large-monitor case)
    public void Suggest_DenseFloor_OnlyAppliesInDenseMode(float baseW, float baseH, ZoomAutoMode mode, float expected)
        => Assert.Equal(expected, ZoomAutoPolicy.Suggest(baseW, baseH, mode));

    [Theory]
    [InlineData(0f, 900f)]
    [InlineData(1600f, 0f)]
    [InlineData(float.NaN, 900f)]
    [InlineData(1600f, float.NaN)]
    [InlineData(-100f, 900f)]
    public void Suggest_DegenerateInput_FallsBackToNeutralZoom(float baseW, float baseH)
        => Assert.Equal(1f, ZoomAutoPolicy.Suggest(baseW, baseH, ZoomAutoMode.Auto));

    [Fact]
    public void Suggest_NeverExceedsTheCeiling()
    {
        Assert.Equal(2f, ZoomAutoPolicy.Suggest(100_000f, 100_000f, ZoomAutoMode.Auto));
        Assert.Equal(2f, ZoomAutoPolicy.Suggest(100_000f, 100_000f, ZoomAutoMode.Dense));
    }

    // ── the three properties §6 calls out by name ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1000f, 1000f, 1200f, 1000f)]   // wider only
    [InlineData(1000f, 1000f, 1000f, 1200f)]   // taller only
    [InlineData(1000f, 1000f, 1200f, 1200f)]   // both
    public void Suggest_IsMonotonic_ALargerBaseBoxNeverSuggestsASmallerZoom(
        float w0, float h0, float w1, float h1)
    {
        foreach (var mode in new[] { ZoomAutoMode.Auto, ZoomAutoMode.Dense })
        {
            float z0 = ZoomAutoPolicy.Suggest(w0, h0, mode);
            float z1 = ZoomAutoPolicy.Suggest(w1, h1, mode);
            Assert.True(z1 >= z0, $"{mode}: ({w1}x{h1}) suggested {z1} < ({w0}x{h0})'s {z0}");
        }
    }

    /// <summary>Feed the suggested zoom's own resulting viewport back through the SAME base-extent recovery the app
    /// uses (baseDip = viewportDip * zoom) and confirm it reproduces the original base box, and therefore the same
    /// suggestion — the no-op guard WaveeShell's debounced effect relies on to not thrash on its own SetZoom.</summary>
    [Theory]
    [InlineData(1664f, 1109f)]
    [InlineData(3440f, 1392f)]
    [InlineData(2560f, 1440f)]
    [InlineData(1366f, 768f)]
    public void Suggest_IsIdempotent_UnderItsOwnBaseExtentRoundTrip(float baseW, float baseH)
    {
        float z1 = ZoomAutoPolicy.Suggest(baseW, baseH, ZoomAutoMode.Auto);
        float viewportW = baseW / z1, viewportH = baseH / z1;
        float roundTrippedW = viewportW * z1, roundTrippedH = viewportH * z1;
        Assert.Equal(baseW, roundTrippedW, 3);
        Assert.Equal(baseH, roundTrippedH, 3);
        float z2 = ZoomAutoPolicy.Suggest(roundTrippedW, roundTrippedH, ZoomAutoMode.Auto);
        Assert.Equal(z1, z2);
    }

    // ── plateau / ladder membership ─────────────────────────────────────────────────────────────────────────────────

    static readonly float[] PlateauSet = [0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f];

    [Theory]
    [InlineData(1664f, 1109f)]
    [InlineData(3440f, 1392f)]
    [InlineData(1920f, 1080f)]
    [InlineData(2560f, 1440f)]
    [InlineData(3840f, 2160f)]
    [InlineData(1366f, 768f)]
    [InlineData(3440f, 700f)]
    public void Suggest_EveryFixture_ReturnsAPlateauMember(float baseW, float baseH)
    {
        foreach (var mode in new[] { ZoomAutoMode.Auto, ZoomAutoMode.Dense })
        {
            float z = ZoomAutoPolicy.Suggest(baseW, baseH, mode);
            Assert.Contains(z, PlateauSet);
        }
    }

    /// <summary>The design box is NOT a fresh number — it must equal the app's own PageMaxW / tall-hero constants.
    /// ZoomAutoPolicy.cs stays BCL-only (so it source-includes cleanly) by keeping these as literals; this test is
    /// what keeps the literal from drifting away from the real one.</summary>
    [Fact]
    public void DesignBox_MatchesWaveeSizePageMaxWAndDetailShellsTallHeroGate()
    {
        Assert.Equal(WaveeSize.PageMaxW, ZoomAutoPolicy.DesignW);
        Assert.Equal(900f, ZoomAutoPolicy.DesignH);   // DetailShell.cs: winH >= 900f ? TitleLarge : Title
    }

    /// <summary>Every plateau this policy can return must also be a real ZoomLadder rung — the AUTO suggestion must
    /// always land on a value Ctrl+± can also reach, or a subsequent manual nudge would step off-ladder.</summary>
    [Fact]
    public void EveryPlateau_IsAMemberOfTheEngineZoomLadder()
    {
        foreach (float p in PlateauSet)
            Assert.Contains(p, FluentGpu.Foundation.ZoomLadder.Steps);
    }

    // ── the one-shot settings migration ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MigrateMode_FreshInstall_LeavesModeAtItsOwnAutoDefault()
    {
        var settings = new MemoryAppSettings();
        ZoomAutoPolicy.MigrateMode(settings);

        Assert.False(settings.WasWritten(WaveeSettings.ZoomMode));   // nothing to override — the default IS Auto
        Assert.Equal((int)ZoomAutoMode.Auto, settings.Get(WaveeSettings.ZoomMode));
        Assert.Equal(ZoomAutoPolicy.MigrationTargetVersion, settings.Get(WaveeSettings.ZoomModeBootstrapVersion));
    }

    [Fact]
    public void MigrateMode_UpgradeWithACustomZoom_PinsManual_SoAutoNeverOverridesIt()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.ZoomLevel, 1.25f);   // the user already picked 125% on an older build

        ZoomAutoPolicy.MigrateMode(settings);

        Assert.Equal((int)ZoomAutoMode.Manual, settings.Get(WaveeSettings.ZoomMode));
    }

    [Fact]
    public void MigrateMode_UpgradeStillAtTheNeutralZoom_StaysAuto()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.ZoomLevel, 1f);   // never touched the picker (or reset it back to 100%)

        ZoomAutoPolicy.MigrateMode(settings);

        Assert.Equal((int)ZoomAutoMode.Auto, settings.Get(WaveeSettings.ZoomMode));
    }

    [Fact]
    public void MigrateMode_RunsExactlyOnce()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.ZoomLevel, 1.25f);
        ZoomAutoPolicy.MigrateMode(settings);

        settings.Set(WaveeSettings.ZoomMode, (int)ZoomAutoMode.Auto);   // simulate the user picking Auto afterward
        ZoomAutoPolicy.MigrateMode(settings);                            // a second launch must not re-pin Manual

        Assert.Equal((int)ZoomAutoMode.Auto, settings.Get(WaveeSettings.ZoomMode));
    }
}
