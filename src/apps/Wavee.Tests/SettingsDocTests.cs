// ── Wavee.Tests/SettingsDocTests.cs — the Settings page as data (Screens/SettingsDoc*.cs, PR C13) ───────────────────
//
// The views used to own, row by row, the title, the glyph, when a row greys and why, and what writing it does. That is
// `SettingsDoc` now; these tests drive the document over a fake `ISettingsFacts`, so no page, no engine host and no live
// settings store is involved. The facts that pin the contract:
//
//   EVERY ROW HAS A WRITER (or says it has none): a toggle has a toggle verb, a choice a choose verb, ... a fact is explicitly
//   read-only, and a card with its own state machine names its owner. Never a writable row with nothing behind it.
//
//   A GREYED ROW SAYS WHY. The reason is a loc key the view prints in the row's description; a row that greys with no reason
//   of its own (the developer items, the equalizer controls) says so with `SilentWhenDisabled`, and only a row that really
//   can grey may claim it.
//
// Verbs that write are not executed here (they touch process-wide state); the ones whose guards refuse BEFORE touching
// anything are, to pin that an out-of-range pick writes nothing.

using FluentGpu.WindowsApi.Notifications;
using Xunit;

namespace Wavee.Tests;

public class SettingsDocTests
{
    /// <summary>A scriptable world. The defaults are the "everything is fine" one.</summary>
    sealed record Facts : ISettingsFacts
    {
        public bool DeveloperMode { get; init; } = true;
        public bool Packaged { get; init; }
        public Tray.IconMode TrayMode { get; init; } = Tray.IconMode.WhileHidden;
        public bool StartOnLogin { get; init; } = true;
        public bool WindowsNotifications { get; init; } = true;
        public bool QuietHours { get; init; } = true;
        public ToastDeliverySetting ToastDelivery { get; init; } = ToastDeliverySetting.Enabled;
        public bool CanDerive { get; init; } = true;
        public bool EqualizerEnabled { get; init; } = true;
        public bool CrossfadeEnabled { get; init; } = true;
        public bool VideoOverridesPresent { get; init; } = true;
        public int PageLayout { get; init; } = Prefs.DetailHero.Automatic;
        public bool RailUniform { get; init; }
        public bool RailCustomized { get; init; } = true;
        public bool SidebarEditing { get; init; }
        public bool SidebarModified { get; init; } = true;
        public bool AudioCacheAvailable { get; init; } = true;
        public bool MetadataClearAvailable { get; init; } = true;
        public bool CrashServiceConfigured { get; init; } = true;
        public bool CrashReportingOn { get; init; } = true;
        public bool TestCrashBusy { get; init; }
    }

    /// <summary>Everything that can be wrong, is.</summary>
    static readonly Facts AllBad = new()
    {
        DeveloperMode = false, Packaged = true, TrayMode = Tray.IconMode.Never, StartOnLogin = false, WindowsNotifications = false,
        QuietHours = false, CanDerive = false, EqualizerEnabled = false, CrossfadeEnabled = false, VideoOverridesPresent = false,
        PageLayout = Prefs.DetailHero.Hero, RailUniform = true, RailCustomized = false, SidebarEditing = true, SidebarModified = false,
        AudioCacheAvailable = false, MetadataClearAvailable = false, CrashServiceConfigured = false, CrashReportingOn = false,
        TestCrashBusy = true,
    };

    static readonly Facts AllGood = new();

    static IEnumerable<SettingsRow> Rows => SettingsDoc.AllRows;

    // ── the document is the catalog, plus what the catalog does not list ────────────────────────────────────────────

    [Fact]
    public void Every_catalog_row_is_in_the_document_exactly_once_with_the_catalogs_glyph()
    {
        foreach (var c in Settings.Catalog.Rows)
        {
            var matches = Rows.Where(r => r.Tab == c.Tab && r.Id == c.RowId).ToArray();
            var row = Assert.Single(matches);
            Assert.Equal(c.Glyph, row.GlyphRole);
            Assert.Equal(c.Section, row.Section);
        }
    }

    [Fact]
    public void Every_catalog_section_is_in_the_document_and_no_row_id_repeats_within_a_tab()
    {
        foreach (var s in Settings.Catalog.Sections)
            Assert.Equal(s.Title, SettingsDoc.Section(s.Tab, s.Title).Section);

        foreach (var group in Rows.GroupBy(r => (r.Tab, r.Id)))
            Assert.True(group.Count() == 1, $"{group.Key.Tab}/{group.Key.Id} appears {group.Count()} times.");
    }

    [Fact]
    public void All_seven_tabs_are_there_in_the_tab_strips_order()
        => Assert.Equal(Enum.GetValues<Settings.Tab>(), SettingsDoc.Tabs.Select(t => t.Tab).ToArray());

    [Fact]
    public void A_row_outside_the_catalog_has_no_glyph_role_and_an_unknown_row_throws()
    {
        Assert.Null(SettingsDoc.Row(Settings.Tab.Notifications, "windows").GlyphRole);
        Assert.Throws<InvalidOperationException>(() => SettingsDoc.Row(Settings.Tab.General, "no-such-row"));
        Assert.Throws<InvalidOperationException>(() => SettingsDoc.Section(Settings.Tab.General, "No Such Section"));
    }

    [Fact]
    public void Every_row_has_a_title_and_every_section_a_heading()
    {
        foreach (var row in Rows) Assert.False(string.IsNullOrEmpty(row.TitleKey), $"{row.Tab}/{row.Id} has no title.");
        foreach (var tab in SettingsDoc.Tabs)
            foreach (var s in tab.Sections)
            {
                Assert.False(string.IsNullOrEmpty(s.TitleKey), $"{tab.Tab}/{s.Section} has no heading.");
                Assert.NotEmpty(s.Rows);
            }
    }

    [Fact]
    public void The_lyrics_blur_is_one_slider_row_and_the_Auto_row_is_gone()
    {
        Assert.Equal(SettingsRowKind.Slider, SettingsDoc.Row(Settings.Tab.Appearance, "lyricsBlur").Kind);
        Assert.DoesNotContain(Rows, r => r.Id == "lyricsBlurAuto");
        Assert.DoesNotContain(Rows, r => r.Writer.Verb is { } v && v.Contains("ResetLyricsBlur"));
    }

    [Fact]
    public void The_lyrics_blur_row_shows_a_legacy_stored_Auto_as_100()
    {
        var store = new MemoryAppSettings();
        store.Set(Platform.Keys.LyricsBlurStrength, -1);
        Assert.Equal(100f, SettingsDoc.Row(Settings.Tab.Appearance, "lyricsBlur").Value(store));
    }

    // ── every row has a writer ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_writable_row_has_exactly_the_verb_its_kind_needs_and_a_fact_has_none()
    {
        foreach (var row in Rows)
        {
            var w = row.Writer;
            string where = $"{row.Tab}/{row.Id} ({row.Kind})";
            Assert.NotNull(w);
            switch (row.Kind)
            {
                case SettingsRowKind.Toggle: Assert.True(w.Toggle is not null && Arms(w) == 1, where); break;
                case SettingsRowKind.Choice or SettingsRowKind.Segmented or SettingsRowKind.Picker:
                    Assert.True(w.Choose is not null && Arms(w) == 1, where); break;
                case SettingsRowKind.Slider: Assert.True(w.Slide is not null && Arms(w) == 1, where); break;
                case SettingsRowKind.Action: Assert.True(w.Run is not null && Arms(w) == 1, where); break;
                case SettingsRowKind.Fact: Assert.True(w.IsReadOnly, where); break;
                case SettingsRowKind.Composite: Assert.True(w.IsOwned && Arms(w) == 0, where); break;
            }
            Assert.False(string.IsNullOrEmpty(w.Verb), where);
        }
    }

    /// <summary>How many delegates a writer has set (a writable row has exactly one).</summary>
    internal static int Arms(SettingsWriter w)
        => (w.Toggle is null ? 0 : 1) + (w.Choose is null ? 0 : 1) + (w.Slide is null ? 0 : 1) + (w.Run is null ? 0 : 1);

    [Fact]
    public void Only_the_facts_are_read_only_and_the_owned_cards_name_their_owner()
    {
        Assert.Equal(
            new[] { "Privacy/whatLeaves", "Privacy/whatLeavesAi", "Privacy/crashContents", "Right now/receipts", "Licenses/licenses" },
            Rows.Where(r => r.Writer.IsReadOnly).Select(r => r.Section + "/" + r.Id).ToArray());

        foreach (var row in Rows.Where(r => r.Writer.IsOwned))
            Assert.Contains('.', row.Writer.Verb);   // "AiLyrics.…", "SettingsVerbs.…": a named hand-off, not a blank
    }

    [Fact]
    public void A_destructive_row_carries_its_confirmation_and_only_actions_do()
    {
        foreach (var row in Rows.Where(r => r.Confirm is not null))
        {
            Assert.Equal(SettingsRowKind.Action, row.Kind);
            Assert.False(string.IsNullOrEmpty(row.Confirm!.TitleKey));
            Assert.False(string.IsNullOrEmpty(row.Confirm.BodyKey));
            Assert.False(string.IsNullOrEmpty(row.Confirm.PrimaryKey));
        }
        foreach (var id in new[] { "railReset", "videoOverrides", "factoryReset", "logsClear", "audioBodiesClear", "licenseKeysClear", "clearMetadata", "crashService", "reportsDeleteAll" })
            Assert.NotNull(Rows.Single(r => r.Id == id).Confirm);
    }

    // ── disabled reasons ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_greyed_row_gives_a_reason_or_owns_up_to_being_silent()
    {
        foreach (var facts in new[] { AllBad, AllGood })
            foreach (var row in Rows)
            {
                var e = row.Enabled(facts);
                if (e.Enabled) { Assert.Null(e.ReasonKey); continue; }
                Assert.True(e.ReasonKey is { Length: > 0 } || row.SilentWhenDisabled,
                    $"{row.Tab}/{row.Id} is greyed with no reason and is not declared silent.");
            }
    }

    [Fact]
    public void A_row_declared_silent_really_can_grey()
    {
        foreach (var row in Rows.Where(r => r.SilentWhenDisabled))
            Assert.False(row.Enabled(AllBad).Enabled && row.Enabled(AllGood).Enabled, $"{row.Tab}/{row.Id} is declared silent but never greys.");
    }

    [Fact]
    public void Nothing_is_greyed_in_the_all_good_world_except_what_it_cannot_help()
    {
        foreach (var row in Rows)
            Assert.True(row.Enabled(AllGood).Enabled, $"{row.Tab}/{row.Id} is greyed even though nothing is wrong.");
    }

    [Fact]
    public void The_tray_rows_that_need_an_icon_grey_with_that_reason()
    {
        var never = new Facts { TrayMode = Tray.IconMode.Never };
        foreach (var id in new[] { "closeToTray", "minimizeToTray" })
        {
            var row = SettingsDoc.Row(Settings.Tab.General, id);
            Assert.False(row.Enabled(never).Enabled);
            Assert.Equal(Strings.Settings.General.Tray.NeedsIcon, row.DescriptionKey(never));
            Assert.True(row.Enabled(AllGood).Enabled);
            Assert.NotEqual(Strings.Settings.General.Tray.NeedsIcon, row.DescriptionKey(AllGood));
        }
    }

    [Fact]
    public void Start_hidden_says_which_of_its_two_needs_is_missing()
    {
        var row = SettingsDoc.Row(Settings.Tab.General, "startHidden");
        Assert.Equal(Strings.Settings.General.Tray.NeedsIcon, row.DescriptionKey(new Facts { TrayMode = Tray.IconMode.Never }));
        Assert.Equal(Strings.Settings.General.Tray.NeedsIcon, row.DescriptionKey(new Facts { TrayMode = Tray.IconMode.Never, StartOnLogin = false }));
        Assert.Equal(Strings.Settings.General.Tray.NeedsStartOnLogin, row.DescriptionKey(new Facts { StartOnLogin = false }));
        Assert.Equal(Strings.Settings.General.Tray.StartHiddenSub, row.DescriptionKey(AllGood));
    }

    [Fact]
    public void The_tray_icon_row_words_itself_by_its_mode()
    {
        var row = SettingsDoc.Row(Settings.Tab.General, "trayIcon");
        Assert.Equal(Strings.Settings.General.Tray.IconNeverSub, row.DescriptionKey(new Facts { TrayMode = Tray.IconMode.Never }));
        Assert.Equal(Strings.Settings.General.Tray.IconModeSub, row.DescriptionKey(new Facts { TrayMode = Tray.IconMode.Always }));
    }

    [Fact]
    public void A_packaged_build_leaves_the_spotify_scheme_to_windows()
    {
        var row = SettingsDoc.Row(Settings.Tab.General, "spotifyLinks");
        Assert.False(row.Enabled(new Facts { Packaged = true }).Enabled);
        Assert.Equal(Strings.Settings.General.SpotifyLinksPackagedSub, row.DescriptionKey(new Facts { Packaged = true }));
        Assert.Equal(Strings.Settings.Links.SpotifySub, row.DescriptionKey(AllGood));
    }

    [Fact]
    public void Automatic_crash_reporting_needs_a_service_and_says_so()
    {
        int auto = (int)Crash.Reporting.Auto;
        var none = new Facts { CrashServiceConfigured = false };
        Assert.False(SettingsDoc.CrashModeEnablement(auto, none).Enabled);
        Assert.Equal(Strings.Settings.Privacy.Facts.ModeAutoNeedsService, SettingsDoc.CrashModeEnablement(auto, none).ReasonKey);
        Assert.Equal(Strings.Settings.Privacy.Facts.ModeAutoNeedsService, SettingsDoc.CrashModeCaptionKey(auto, none));
        Assert.True(SettingsDoc.CrashModeEnablement(auto, AllGood).Enabled);
        Assert.Equal(Strings.Settings.Privacy.Facts.ModeAutoSub, SettingsDoc.CrashModeCaptionKey(auto, AllGood));
        foreach (var other in new[] { (int)Crash.Reporting.Off, (int)Crash.Reporting.Ask })
            Assert.True(SettingsDoc.CrashModeEnablement(other, none).Enabled);
    }

    [Fact]
    public void The_sidebar_controls_give_the_finish_editing_reason_while_it_is_being_edited()
    {
        var editing = new Facts { SidebarEditing = true };
        foreach (var id in new[] { "sidebarCustomize", "sidebarReset", "sidebarResetEverything" })
        {
            var row = SettingsDoc.Row(Settings.Tab.Appearance, id);
            Assert.False(row.Enabled(editing).Enabled, id);
            Assert.Equal(SettingsDoc.FinishEditingKey, row.DescriptionKey(editing));
        }
        // Reset is also inert (silently) when there is nothing to reset.
        var reset = SettingsDoc.Row(Settings.Tab.Appearance, "sidebarReset");
        Assert.False(reset.Enabled(new Facts { SidebarModified = false }).Enabled);
        Assert.Null(reset.Enabled(new Facts { SidebarModified = false }).ReasonKey);
    }

    [Fact]
    public void The_test_crash_report_explains_a_build_that_cannot_send_one()
    {
        var row = SettingsDoc.Row(Settings.Tab.PrivacyDiagnostics, "sendTestCrashReport");
        var unconfigured = new Facts { CrashServiceConfigured = false };
        Assert.Equal(Strings.Settings.Privacy.Developer.SendTestCrashReportUnavailable, row.DescriptionKey(unconfigured));
        Assert.Equal(Strings.Settings.Privacy.Developer.SendTestCrashReportSub, row.DescriptionKey(AllGood));
        Assert.False(row.Enabled(new Facts { DeveloperMode = false }).Enabled);
        Assert.False(row.Enabled(new Facts { TestCrashBusy = true }).Enabled);   // one live dump request at a time
    }

    [Fact]
    public void The_equalizer_controls_follow_its_switch()
    {
        foreach (var id in new[] { "eqPreset", "eqCurve", "eqReset" })
        {
            var row = SettingsDoc.Row(Settings.Tab.Playback, id);
            Assert.False(row.Enabled(new Facts { EqualizerEnabled = false }).Enabled, id);
            Assert.True(row.Enabled(AllGood).Enabled, id);
        }
        Assert.Equal(Strings.Settings.Sound.CurveOff, SettingsDoc.Row(Settings.Tab.Playback, "eqCurve").Caption(new Facts { EqualizerEnabled = false }));
        Assert.Equal(Strings.Settings.Sound.CurveOn, SettingsDoc.Row(Settings.Tab.Playback, "eqCurve").Caption(AllGood));
    }

    // ── visibility ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_developer_only_catalog_row_is_absent_not_greyed_outside_developer_mode()
    {
        foreach (var c in Settings.Catalog.Rows.Where(r => r.DeveloperOnly))
        {
            var row = SettingsDoc.Row(c.Tab, c.RowId);
            Assert.False(row.Visible(new Facts { DeveloperMode = false }), c.RowId);
            Assert.True(row.Visible(new Facts { DeveloperMode = true }), c.RowId);
        }
        // And every other catalog row is composed in both worlds, exactly as the catalog says.
        foreach (var c in Settings.Catalog.Rows)
        {
            var row = SettingsDoc.Row(c.Tab, c.RowId);
            foreach (bool dev in new[] { false, true })
                if (c.Tab != Settings.Tab.Appearance || c.RowId is not ("railUniform" or "railReset"))
                    Assert.True(Settings.Catalog.RowVisible(c.Tab, c.RowId, dev) == row.Visible(new Facts { DeveloperMode = dev }), c.RowId);
        }
    }

    [Fact]
    public void The_rail_sub_rows_exist_only_for_the_automatic_layout_and_the_reset_only_without_uniform()
    {
        var uniform = SettingsDoc.Row(Settings.Tab.Appearance, "railUniform");
        var reset = SettingsDoc.Row(Settings.Tab.Appearance, "railReset");

        Assert.True(uniform.Visible(new Facts { PageLayout = Prefs.DetailHero.Automatic }));
        Assert.False(uniform.Visible(new Facts { PageLayout = Prefs.DetailHero.Hero }));

        Assert.True(reset.Visible(new Facts { PageLayout = Prefs.DetailHero.Automatic, RailUniform = false }));
        Assert.False(reset.Visible(new Facts { PageLayout = Prefs.DetailHero.Automatic, RailUniform = true }));   // composed AWAY, not disabled
        Assert.False(reset.Visible(new Facts { PageLayout = Prefs.DetailHero.Hero }));

        // A destructive-looking no-op is never offered: with nothing customized the reset greys.
        Assert.False(reset.Enabled(new Facts { RailCustomized = false }).Enabled);
        Assert.True(reset.Enabled(new Facts { RailCustomized = true }).Enabled);
    }

    [Fact]
    public void The_quiet_hours_range_is_composed_only_while_windows_delivery_and_quiet_hours_are_on()
    {
        foreach (var id in new[] { "quietFrom", "quietTo" })
        {
            var row = SettingsDoc.Row(Settings.Tab.Notifications, id);
            Assert.True(row.Visible(new Facts { WindowsNotifications = true, QuietHours = true }), id);
            Assert.False(row.Visible(new Facts { WindowsNotifications = false, QuietHours = true }), id);
            Assert.False(row.Visible(new Facts { WindowsNotifications = true, QuietHours = false }), id);
        }
        // Sound and quiet hours stay visible but go inert while Windows delivery is off.
        foreach (var id in new[] { "sound", "quiet" })
        {
            var row = SettingsDoc.Row(Settings.Tab.Notifications, id);
            Assert.True(row.Visible(AllBad), id);
            Assert.False(row.Enabled(new Facts { WindowsNotifications = false }).Enabled, id);
            Assert.True(row.SilentWhenDisabled, id);
        }
    }

    [Fact]
    public void Every_notification_topic_has_a_ladder_and_a_developer_only_test_event()
    {
        foreach (var topic in Notify.Prefs.AllTopics)
        {
            var ladder = SettingsDoc.Row(Settings.Tab.Notifications, SettingsDoc.TopicRowId(topic));
            var test = SettingsDoc.Row(Settings.Tab.Notifications, SettingsDoc.TopicTestRowId(topic));
            Assert.Equal(SettingsRowKind.Segmented, ladder.Kind);
            Assert.Equal(SettingsRowKind.Action, test.Kind);
            Assert.Equal(SettingsDoc.TopicTitleKey(topic), ladder.TitleKey);
            Assert.True(ladder.Visible(AllBad));
            Assert.False(test.Visible(new Facts { DeveloperMode = false }));
            Assert.True(test.Visible(new Facts { DeveloperMode = true }));
        }
        // The ladders are in Notify.Prefs.AllTopics order, read — never re-listed.
        var order = SettingsDoc.Section(Settings.Tab.Notifications, "Topics").Rows.Where(r => r.Kind == SettingsRowKind.Segmented).Select(r => r.Id).ToArray();
        Assert.Equal(Notify.Prefs.AllTopics.Select(SettingsDoc.TopicRowId).ToArray(), order);
    }

    // ── values ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_toggles_value_reads_its_key_from_the_store_it_is_given()
    {
        var store = DefaultsOnlySettings.Instance;
        Assert.Equal(Platform.Keys.MarqueeEnabled.Default, SettingsDoc.Row(Settings.Tab.Appearance, "marquee").Value(store));
        Assert.Equal(Platform.Keys.HideTrackArtwork.Default, SettingsDoc.Row(Settings.Tab.Appearance, "hideTrackArtwork").Value(store));
        Assert.Equal(Platform.Keys.RememberVolume.Default, SettingsDoc.Row(Settings.Tab.Playback, "rememberVolume").Value(store));
        Assert.Equal(Platform.Keys.AutoplayEnabled.Default, SettingsDoc.Row(Settings.Tab.Playback, "autoplay").Value(store));
        Assert.Equal(Platform.Keys.AudioBodyCacheEnabled.Default, SettingsDoc.Row(Settings.Tab.Storage, "cacheAudio").Value(store));
        Assert.Equal(Platform.Keys.CrashIncludeDump.Default, SettingsDoc.Row(Settings.Tab.PrivacyDiagnostics, "crashDump").Value(store));
        Assert.Equal(Platform.Keys.UpdateInstallOnQuit.Default, SettingsDoc.Row(Settings.Tab.About, "installOnQuit").Value(store));
    }

    [Fact]
    public void A_choices_value_is_the_index_its_combo_shows()
    {
        var store = DefaultsOnlySettings.Instance;
        Assert.Equal(Settings.Language.IndexOf(Platform.Keys.UiCulture.Default), SettingsDoc.Row(Settings.Tab.General, "language").Value(store));
        Assert.Equal(Settings.StorageFormat.MetaBudgetIndex(Platform.Keys.MetadataCacheBudgetBytes.Default),
            SettingsDoc.Row(Settings.Tab.Storage, "metadataBudget").Value(store));
        Assert.Equal(Settings.Quality.VideoIndex(Platform.Keys.VideoQuality.Default), SettingsDoc.Row(Settings.Tab.Playback, "videoQuality").Value(store));
        Assert.Equal(Settings.Eq.PresetIndex(Platform.Keys.EqualizerPreset.Default), SettingsDoc.Row(Settings.Tab.Playback, "eqPreset").Value(store));
    }

    [Fact]
    public void Topic_ladders_are_written_through_the_notification_verbs_and_titled_by_the_documents_keys()
    {
        foreach (var topic in Notify.Prefs.AllTopics)
        {
            Assert.False(string.IsNullOrEmpty(SettingsDoc.TopicTitleKey(topic)));
            Assert.False(string.IsNullOrEmpty(SettingsDoc.TopicCaptionKey(topic)));
        }
        Assert.Equal(Notify.Prefs.AllTopics.Length, Notify.Prefs.AllTopics.Select(SettingsDoc.TopicTitleKey).Distinct().Count());
    }

    // ── a pick that does not exist writes nothing (guards that refuse before touching any process state) ──────────────

    [Fact]
    public void An_out_of_range_pick_is_refused_without_touching_anything()
    {
        Assert.False(SettingsVerbs.SetLanguage(99));
        Assert.False(SettingsVerbs.SetLanguage(2));                  // nl: shown but not yet pickable
        Assert.False(SettingsVerbs.SetTrayIconMode(5));
        Assert.False(SettingsVerbs.SetCrashMode(7));
        Assert.False(SettingsVerbs.SetDensity(9));
        Assert.False(SettingsVerbs.SetTrackListStyle(5));
        Assert.False(SettingsVerbs.SetPageLayout(5));
        Assert.False(SettingsVerbs.SetDaylistClock(99));
        Assert.False(SettingsVerbs.PickZoom(999));
        Assert.False(SettingsVerbs.SetStageVisualizer(999));
        Assert.False(SettingsVerbs.SetEqBand(-1, 3f));
        Assert.False(SettingsVerbs.SetEqBand(Settings.Eq.BandCount, 3f));
        Assert.False(SettingsVerbs.SetVideoQuality(99));
        Assert.False(SettingsVerbs.SetMeteredVideo(99));
        Assert.False(SettingsVerbs.SetBudgetPreset(99));
        Assert.False(SettingsVerbs.SetBudgetGiB(double.NaN));
        Assert.False(SettingsVerbs.SetMetadataBudget(99));
    }

    [Fact]
    public void The_budget_percent_a_box_commits_is_clamped_and_a_non_number_is_zero()
    {
        Assert.Equal(90, SettingsVerbs.ClampBudgetPercent(250));
        Assert.Equal(0, SettingsVerbs.ClampBudgetPercent(-5));
        Assert.Equal(0, SettingsVerbs.ClampBudgetPercent(double.NaN));
        Assert.Equal(12, SettingsVerbs.ClampBudgetPercent(12.2));
    }

    [Fact]
    public void The_description_a_view_prints_is_the_reason_while_greyed_else_the_caption()
    {
        var row = SettingsDoc.Row(Settings.Tab.General, "closeToTray");
        Assert.Equal(Strings.Settings.General.Tray.CloseToTraySub, row.DescriptionKey(AllGood));
        Assert.Equal(Strings.Settings.General.Tray.NeedsIcon, row.DescriptionKey(new Facts { TrayMode = Tray.IconMode.Never }));
        // A row with no caption of its own prints nothing (the metered cap's sentence is live).
        Assert.Null(SettingsDoc.Row(Settings.Tab.Playback, "meteredQuality").DescriptionKey(AllGood));
    }
}
