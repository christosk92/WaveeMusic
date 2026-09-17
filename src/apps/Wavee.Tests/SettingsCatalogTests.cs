using System.Globalization;
using FluentGpu.WindowsApi.Network;
using FluentGpu.WindowsApi.Notifications;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>Pins the Settings regroup's rule (ch 27 §0 N3): every row carries its own glyph; a section header's glyph
/// is never reused by one of ITS rows; no glyph repeats within a section. Plain data — no engine, no render — so an edit
/// to the table fails here, not the next time a human looks at the page. Ported from 0.2.9 `SettingsCatalogTests`
/// (six facts) plus the tray plan §8 group.</summary>
public class SettingsCatalogTests
{
    static IEnumerable<(Settings.Tab Tab, string Section)> AllSections()
        => Settings.Catalog.Sections.Select(s => (s.Tab, Section: s.Title)).Distinct();

    [Fact]
    public void EverySection_HasAtLeastOneRow()
    {
        foreach (var (tab, section) in AllSections())
            Assert.True(Settings.Catalog.Rows.Any(r => r.Tab == tab && r.Section == section), $"{tab}/{section} has a header but no rows.");
    }

    [Fact]
    public void NoRow_ReusesItsOwnSectionsGlyph()
    {
        foreach (var row in Settings.Catalog.Rows)
        {
            string sectionGlyph = Settings.Catalog.SectionGlyph(row.Tab, row.Section);
            Assert.False(row.Glyph == sectionGlyph, $"{row.Tab}/{row.Section}: row '{row.RowId}' repeats the section's glyph '{sectionGlyph}'.");
        }
    }

    [Fact]
    public void NoGlyph_RepeatsWithinASection()
    {
        foreach (var group in Settings.Catalog.Rows.GroupBy(r => (r.Tab, r.Section)))
        {
            var dupes = group.GroupBy(r => r.Glyph).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            Assert.True(dupes.Length == 0, $"{group.Key.Tab}/{group.Key.Section}: glyph(s) {string.Join(", ", dupes)} repeat across rows.");
        }
    }

    [Fact]
    public void RowIds_AreUniquePerTab()
    {
        foreach (var group in Settings.Catalog.Rows.GroupBy(r => r.Tab))
        {
            var dupes = group.GroupBy(r => r.RowId).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            Assert.True(dupes.Length == 0, $"{group.Key}: row id(s) {string.Join(", ", dupes)} are not unique on this tab.");
        }
    }

    [Fact]
    public void RowGlyph_ThrowsForAnUnknownRow()
        => Assert.Throws<InvalidOperationException>(() => Settings.Catalog.RowGlyph(Settings.Tab.General, "no-such-row"));

    [Fact]
    public void SectionGlyph_ThrowsForAnUnknownSection()
        => Assert.Throws<InvalidOperationException>(() => Settings.Catalog.SectionGlyph(Settings.Tab.General, "No Such Section"));

    [Fact]
    public void Scope_IsTheFourTableDrivenTabs()
    {
        var tabs = Settings.Catalog.Sections.Select(s => s.Tab).Distinct().OrderBy(t => t).ToArray();
        Assert.Equal(new[] { Settings.Tab.General, Settings.Tab.Appearance, Settings.Tab.Playback, Settings.Tab.Storage }, tabs);
    }

    [Fact]
    public void TheNotificationAreaGroup_SitsBetweenLinksAndGraphics_WithItsFiveRows()
    {
        var general = Settings.Catalog.Sections.Where(s => s.Tab == Settings.Tab.General).Select(s => s.Title).ToArray();
        int tray = Array.IndexOf(general, "Notification area");
        Assert.Equal(Array.IndexOf(general, "Links") + 1, tray);
        Assert.Equal(Array.IndexOf(general, "Graphics") - 1, tray);
        var rows = Settings.Catalog.Rows.Where(r => r.Section == "Notification area").Select(r => r.RowId).ToArray();
        Assert.Equal(new[] { "trayIcon", "closeToTray", "minimizeToTray", "startHidden", "startOnLogin" }, rows);
    }

    [Fact]
    public void TheTwoDeliberateGears_AreDeveloperModeAndPlayerStyle()
    {
        var gears = Settings.Catalog.Rows.Where(r => r.Glyph == "Settings").Select(r => (r.Tab, r.RowId)).ToArray();
        Assert.Equal(new[] { (Settings.Tab.General, "developerMode"), (Settings.Tab.Appearance, "npvStyle") }, gears);
    }

    [Fact]
    public void TabSlugs_AreOneToOneWithTheTabs()
    {
        Assert.Equal(Enum.GetValues<Settings.Tab>().Length, Settings.TabSlugs.Length);
        foreach (var tab in Enum.GetValues<Settings.Tab>())
            Assert.Equal(tab, Settings.TabFromSlug(Settings.SlugOf(tab)));
        Assert.Equal(Settings.Tab.General, Settings.TabFromSlug("no-such-tab"));
        Assert.Equal(Settings.Tab.Storage, Settings.TabFromSlug("STORAGE"));
        Assert.Equal(Settings.Tab.General, Settings.TabFromSlug(null));
    }
}

/// <summary>The pure decisions the Settings tabs call: the equalizer vector (0.2.9 `EqualizerSettingsTests`, ported),
/// the metered status line (0.2.9 `MeteredStatusLineTests`, ported), the quality ladders (D7), storage formatting and
/// the blocked-banner decision (ch 27 §8: untested in 0.2.9 — added), the language mask and the receipts formatting.</summary>
public class SettingsRulesTests
{
    // ── the equalizer ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadGains_DefaultAndNullSettings_AreFlat()
    {
        Assert.All(Settings.Eq.ReadGains(new MemoryAppSettings()), g => Assert.Equal(0f, g));
        var gains = Settings.Eq.ReadGains(null);
        Assert.Equal(10, gains.Length);
        Assert.All(gains, g => Assert.Equal(0f, g));
    }

    [Fact]
    public void ReadGains_ParsesClampsPadsAndNeverThrows()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.EqualizerGains, "1,-2,3.5,0,0,0,0,0,0,6");
        Assert.Equal(new[] { 1f, -2f, 3.5f, 0f, 0f, 0f, 0f, 0f, 0f, 6f }, Settings.Eq.ReadGains(settings));

        var clamped = Settings.Eq.ParseGains("20,-20,0,0,0,0,0,0,0,0");
        Assert.Equal(12f, clamped[0]);
        Assert.Equal(-12f, clamped[1]);

        var padded = Settings.Eq.ParseGains("4,5");
        Assert.Equal(4f, padded[0]);
        Assert.Equal(5f, padded[1]);
        for (int i = 2; i < padded.Length; i++) Assert.Equal(0f, padded[i]);

        var garbage = Settings.Eq.ParseGains("not-a-number,3,,,,,,,,");
        Assert.Equal(0f, garbage[0]);
        Assert.Equal(3f, garbage[1]);
    }

    [Fact]
    public void SerializeGains_RoundTripsAndClampsAndPads()
    {
        var written = new float[] { 12f, -12f, 0f, 1.5f, -1.5f, 0f, 0f, 0f, 0f, 0f };
        Assert.Equal(written, Settings.Eq.ParseGains(Settings.Eq.SerializeGains(written)));

        var gains = Settings.Eq.ParseGains(Settings.Eq.SerializeGains([99f, -99f]));
        Assert.Equal(12f, gains[0]);
        Assert.Equal(-12f, gains[1]);
        for (int i = 2; i < gains.Length; i++) Assert.Equal(0f, gains[i]);
        Assert.Equal("0,0,0,0,0,0,0,0,0,0", Settings.Eq.SerializeGains([]));
    }

    [Fact]
    public void Presets_AreSixTenBandVectors_AndTheIdLookupIsForgiving()
    {
        Assert.Equal(6, Settings.Eq.PresetIds.Length);
        Assert.Equal(Settings.Eq.PresetIds.Length, Settings.Eq.PresetGains.Length);
        Assert.All(Settings.Eq.PresetGains, g => Assert.Equal(Settings.Eq.BandCount, g.Length));
        Assert.All(Settings.Eq.PresetGains[0], g => Assert.Equal(0f, g));                // Flat is a straight line
        Assert.All(Settings.Eq.PresetGains[5], g => Assert.Equal(12f, MathF.Abs(g)));    // Proof swings the full range
        Assert.Equal(1, Settings.Eq.PresetIndex("BASS"));
        Assert.Equal(0, Settings.Eq.PresetIndex(null));
        Assert.Equal(0, Settings.Eq.PresetIndex("custom"));
    }

    [Theory]
    [InlineData(0.24f, 0f)]
    [InlineData(0.26f, 0.5f)]
    [InlineData(-3.74f, -3.5f)]
    [InlineData(30f, 12f)]
    [InlineData(-30f, -12f)]
    public void SnapGain_SnapsToHalfADecibel_AndClamps(float input, float expected)
        => Assert.Equal(expected, Settings.Eq.SnapGain(input));

    // ── the metered status line ──────────────────────────────────────────────────────────────────────────────────────

    static NetworkCost Cost(NetworkCostKind kind, bool overLimit = false, bool approaching = false, bool roaming = false)
        => new(kind, overLimit, approaching, roaming);

    static string Keys(Settings.MeteredStatusLine line) => line.Render(static k => k);

    [Fact]
    public void UnknownCost_SaysItCouldNotRead_NotNotMetered()
    {
        var line = Settings.MeteredStatusLine.For(NetworkCost.Unknown, capInEffect: true);
        Assert.Equal(Settings.MeteredStatusKind.Unknown, line.Kind);
        Assert.Equal(Strings.Settings.Playback.MeteredStatus.Unknown, Keys(line));
    }

    [Fact]
    public void Unrestricted_IsNotMetered_RegardlessOfCap()
    {
        Assert.Equal(Settings.MeteredStatusKind.NotMetered, Settings.MeteredStatusLine.For(Cost(NetworkCostKind.Unrestricted), true).Kind);
        Assert.Equal(Settings.MeteredStatusKind.NotMetered, Settings.MeteredStatusLine.For(Cost(NetworkCostKind.Unrestricted), false).Kind);
    }

    [Theory]
    [InlineData(NetworkCostKind.Fixed, true, Settings.MeteredStatusKind.Metered)]
    [InlineData(NetworkCostKind.Variable, true, Settings.MeteredStatusKind.Metered)]
    [InlineData(NetworkCostKind.Fixed, false, Settings.MeteredStatusKind.MeteredWithinCap)]
    [InlineData(NetworkCostKind.Variable, false, Settings.MeteredStatusKind.MeteredWithinCap)]
    public void MeteredKinds_OnlyClaimTheCapWhenItBites(NetworkCostKind kind, bool capInEffect, Settings.MeteredStatusKind expected)
        => Assert.Equal(expected, Settings.MeteredStatusLine.For(Cost(kind), capInEffect).Kind);

    [Fact]
    public void OverLimit_ThenRoaming_AreSuffixedInThatOrder_AndApproachingIsNotSurfaced()
    {
        var sep = Settings.MeteredStatusLine.Separator;
        Assert.Equal(
            Strings.Settings.Playback.MeteredStatus.Metered + sep + Strings.Settings.Playback.MeteredStatus.OverLimit + sep +
            Strings.Settings.Playback.MeteredStatus.Roaming,
            Keys(Settings.MeteredStatusLine.For(Cost(NetworkCostKind.Variable, overLimit: true, roaming: true), true)));
        Assert.Equal(Strings.Settings.Playback.MeteredStatus.Metered + sep + Strings.Settings.Playback.MeteredStatus.Roaming,
            Keys(Settings.MeteredStatusLine.For(Cost(NetworkCostKind.Fixed, roaming: true), true)));
        Assert.Equal(Strings.Settings.Playback.MeteredStatus.Metered,
            Keys(Settings.MeteredStatusLine.For(Cost(NetworkCostKind.Fixed, approaching: true), true)));
    }

    // ── the quality ladders (D7) ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheLosslessRung_IsOfferedOnlyWhenAKeyDeriverIsInstalled()
    {
        Assert.Equal(3, Settings.Quality.AudioRungCount(canDerive: false));
        Assert.Equal(4, Settings.Quality.AudioRungCount(canDerive: true));
        Assert.False(Settings.Quality.IsWritableAudio(Settings.Quality.LosslessRung, canDerive: false));
        Assert.True(Settings.Quality.IsWritableAudio(Settings.Quality.LosslessRung, canDerive: true));
        Assert.False(Settings.Quality.IsWritableAudio(-1, canDerive: true));
        // flac plan §5.4: the metered cap is the same ladder, under the same gate
        Assert.Equal(Settings.Quality.AudioRungCount(false), Settings.Quality.MeteredRungCount(false));
        Assert.Equal(Settings.Quality.AudioRungCount(true), Settings.Quality.MeteredRungCount(true));
    }

    [Theory]
    [InlineData(3, false, 2)]   // a stored Lossless on a build that cannot derive shows Very High — what actually plays
    [InlineData(3, true, 3)]
    [InlineData(-4, true, 0)]
    [InlineData(9, true, 3)]
    [InlineData(1, false, 1)]
    public void AudioIndex_ClampsToTheOfferedLadder(int stored, bool canDerive, int expected)
        => Assert.Equal(expected, Settings.Quality.AudioIndex(stored, canDerive));

    [Fact]
    public void VideoLadders_MapHeights_WithTheirDefaults()
    {
        Assert.Equal(5, Settings.Quality.VideoIndex(720));
        Assert.Equal(0, Settings.Quality.VideoIndex(999));
        Assert.Equal(2, Settings.Quality.MeteredVideoIndex(720));
        Assert.Equal(1, Settings.Quality.MeteredVideoIndex(999));   // unmatched → 480p
    }

    [Theory]
    [InlineData(5.0, 5000)]
    [InlineData(0.25, 250)]
    [InlineData(-1.0, 0)]
    [InlineData(40.0, 12000)]
    [InlineData(double.NaN, 0)]
    public void Crossfade_CommitsRoundedClampedMilliseconds(double seconds, int expectedMs)
        => Assert.Equal(expectedMs, Settings.Quality.CrossfadeMs(seconds));

    // ── storage formatting (ch 27 §8: untested in 0.2.9) ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "2 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(52L * 1048576 + 1048576 / 2, "52.5 MB")]
    [InlineData(1073741824L, "1.0 GB")]
    [InlineData(2040109465L, "1.9 GB")]
    public void Bytes_UsesBinaryDivisorsAndTheInvariantCulture(long bytes, string expected)
    {
        // The app and its tests run with InvariantGlobalization, where no other culture can even be constructed, so a
        // comma-decimal culture cannot leak in; the format is pinned against the invariant culture it always runs under.
        Assert.Equal(expected, Settings.StorageFormat.Bytes(bytes));
    }

    [Fact]
    public void BudgetLadders_PickTheNearestPreset_AndCustomWhenInexact()
    {
        Assert.Equal(Settings.StorageFormat.BodyBudgetBytes.Length + 1, Settings.StorageFormat.BodyBudgetLabels.Length);
        Assert.Equal(5, Settings.StorageFormat.BodyBudgetIndex(32L << 30));
        Assert.Equal(5, Settings.StorageFormat.BodyBudgetIndex(30L << 30));
        Assert.Equal(0, Settings.StorageFormat.BodyBudgetIndex(1));
        Assert.Equal(10, Settings.StorageFormat.BodyBudgetIndex(long.MaxValue));
        Assert.Equal(3, Settings.StorageFormat.BodyBudgetExactIndex(8L << 30));
        Assert.Equal(Settings.StorageFormat.CustomBudgetIndex, Settings.StorageFormat.BodyBudgetExactIndex(9L << 30));
        Assert.Equal(2, Settings.StorageFormat.MetaBudgetIndex(128L << 20));
        Assert.Equal(1, Settings.StorageFormat.MetaBudgetIndex(77));
        Assert.Equal(1L << 26, Settings.StorageFormat.GiBToBytes(0.0));   // floored at 1/16 GiB
    }

    [Fact]
    public void Percent_RoundsAndNeverDividesByZero()
    {
        Assert.Equal(0, Settings.StorageFormat.Percent(5, 0));
        Assert.Equal(0, Settings.StorageFormat.Percent(0, 100));
        Assert.Equal(94, Settings.StorageFormat.Percent(94, 100));
        Assert.Equal(1, Settings.StorageFormat.Percent(1, 200));   // 0.5 rounds away from zero
    }

    // ── the notifications tab ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ToastDeliverySetting.Enabled, Settings.BlockedBanner.None, false)]
    [InlineData(ToastDeliverySetting.Unknown, Settings.BlockedBanner.None, false)]
    [InlineData(ToastDeliverySetting.DisabledForApplication, Settings.BlockedBanner.App, true)]
    [InlineData(ToastDeliverySetting.DisabledForUser, Settings.BlockedBanner.User, true)]
    [InlineData(ToastDeliverySetting.DisabledByGroupPolicy, Settings.BlockedBanner.Policy, false)]
    public void TheBlockedBanner_NeverOffersADeadEnd(ToastDeliverySetting setting, Settings.BlockedBanner expected, bool offersButton)
    {
        var banner = Settings.NotifyRules.Blocked(setting);
        Assert.Equal(expected, banner);
        Assert.Equal(offersButton, Settings.NotifyRules.OffersOpenSettings(banner));
    }

    [Fact]
    public void TestEventOutcomes_CarryTheirClockAndSeverity()
    {
        Assert.Equal("--:--", Settings.NotifyRules.Clock(null, CultureInfo.InvariantCulture));
        var at = new DateTimeOffset(2026, 9, 13, 8, 5, 0, TimeSpan.Zero);
        Assert.Equal(at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), Settings.NotifyRules.Clock(at, CultureInfo.InvariantCulture));

        Assert.True(Settings.NotifyRules.OutcomeHasClock(Settings.TestEventOutcome.Scheduled));
        Assert.True(Settings.NotifyRules.OutcomeHasClock(Settings.TestEventOutcome.BannerQuietDeferred));
        Assert.False(Settings.NotifyRules.OutcomeHasClock(Settings.TestEventOutcome.Banner));
        Assert.Equal(0, Settings.NotifyRules.OutcomeSeverity(Settings.TestEventOutcome.Dropped));
        Assert.Equal(2, Settings.NotifyRules.OutcomeSeverity(Settings.TestEventOutcome.Unavailable));
        Assert.Equal(1, Settings.NotifyRules.OutcomeSeverity(Settings.TestEventOutcome.Scheduled));
        Assert.Equal(Strings.Settings.Notify.OutcomeUnavailable, Settings.NotifyRules.OutcomeKey(Settings.TestEventOutcome.Unavailable));
        Assert.Equal("settings.notify.outcomeScheduled", Settings.NotifyRules.OutcomeKey(Settings.TestEventOutcome.Scheduled));
    }

    [Fact]
    public void HourLabels_AreTheInvariantHourIndex()
    {
        var hours = Settings.NotifyRules.HourLabels();
        Assert.Equal(24, hours.Length);
        Assert.Equal("00:00", hours[0]);
        Assert.Equal("22:00", hours[22]);
    }

    // ── language + receipts ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Language_ShowsFourButOnlySystemAndEnglishArePickable()
    {
        Assert.Equal(4, Settings.Language.Codes.Length);
        Assert.True(Settings.Language.CanPick(0));
        Assert.True(Settings.Language.CanPick(1));
        Assert.False(Settings.Language.CanPick(2));
        Assert.False(Settings.Language.CanPick(3));
        Assert.False(Settings.Language.CanPick(9));
        Assert.Equal(3, Settings.Language.IndexOf("KO-kr"));
        Assert.Equal(0, Settings.Language.IndexOf("fr-FR"));
    }

    [Theory]
    [InlineData(0, 0, 0, 5, "5s")]
    [InlineData(0, 0, 3, 7, "3m 7s")]
    [InlineData(0, 2, 14, 0, "2h 14m")]
    [InlineData(1, 3, 0, 0, "1d 3h")]
    public void Uptime_UsesTheTwoLargestUnits(int days, int hours, int minutes, int seconds, string expected)
        => Assert.Equal(expected, Settings.Receipts.Uptime(new TimeSpan(days, hours, minutes, seconds)));

    [Fact]
    public void Receipts_FormatTheGpuLine_AndClassifyTheAdapter()
    {
        Assert.Equal("412.7 MB", Settings.Receipts.Mb((long)(412.7 * 1048576)));
        Assert.Equal("Apple M2  (Strong)", Settings.Receipts.GpuLine("Apple M2", Settings.GpuTier.Strong, software: false));
        Assert.Equal("—  (Unknown · software)", Settings.Receipts.GpuLine("", Settings.GpuTier.Unknown, software: true));
        Assert.True(Settings.Receipts.IsSharedIgpu(Settings.GpuTier.Weak, 10, 0));
        Assert.False(Settings.Receipts.IsSharedIgpu(Settings.GpuTier.Strong, 0, 10));
        Assert.True(Settings.Receipts.IsSharedIgpu(Settings.GpuTier.Unknown, 5, 10));
        Assert.False(Settings.Receipts.IsSharedIgpu(Settings.GpuTier.Unknown, 0, 0));
        Assert.Equal(0, Settings.Receipts.AppExclusive(100, sharedIgpu: true, localUsage: 500, nonLocalUsage: 0));
        Assert.Equal(60, Settings.Receipts.AppExclusive(100, sharedIgpu: false, localUsage: 500, nonLocalUsage: 40));
        Assert.Equal("—", Settings.Receipts.Fps(0));
        Assert.Equal("120.0", Settings.Receipts.Fps(120));
    }
}
