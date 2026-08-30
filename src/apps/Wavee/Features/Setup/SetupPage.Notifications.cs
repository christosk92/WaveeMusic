using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 7 · Notifications &amp; reach (<c>data-step="7"</c>). Stage + decision, per the wizard rework: the
/// master Windows-notifications gate, the three headline topic dials (each with its own sub-label), a "More topics
/// &amp; quiet hours" <see cref="SetupCompact.ClickRow"/> that opens <see cref="SetupMoreTopicsPanel"/> in a flyout,
/// then the links toggle and app-language combo. The master-off <c>InfoBar</c> the shipped Settings tab shows below
/// the toggle does NOT live in this column any more — with the sub-label gone from the Windows row (a fourth
/// 52-DIP row would land the column at 452, five DIP over budget by the same text-metric margin the shipped
/// <c>SettingsExpander</c> version never had to account for), that summary becomes the STAGE pill instead: it was
/// already the more prominent home for it, and freeing the row buys back exactly the height a fourth sub-label
/// would have spent.
///
/// <para><b>Follows the SHIPPED dial, not the prototype.</b> A topic whose <see cref="NotificationPolicy.CeilingFor"/>
/// is not <see cref="NotifyLevel.Windows"/> (only <see cref="NotifyTopic.LibraryActivity"/>) renders a genuinely
/// two-segment dial rather than a disabled third segment — "an unreachable switch teaches the user the wrong thing
/// about the product," per <c>SettingsPage.Notifications.cs</c>'s own <c>TopicRow</c>.</para>
///
/// <para>Quiet hours renders as the flyout's own single 3-option preset combo (Off / 23:00–08:00 / 00:00–07:00)
/// rather than the Settings tab's toggle + two independent hour combos — a genuine simplification for a first-run
/// screen, not a data-model fiction: all three presets are exact, real <c>(Enabled, FromHour, ToHour)</c> triples
/// (<see cref="SetupNotificationSummary.QuietPresets"/>).</para></summary>
sealed class SetupNotificationsPage : Component
{
    readonly Signal<int> _epoch = new(0);
    void Bump() => _epoch.Value = _epoch.Peek() + 1;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var overlay = UseContext(Overlay.Service);
        var settings = svc?.Settings;
        _ = _epoch.Value;
        _ = NotificationPrefs.Epoch.Value;   // re-read after any dial write made elsewhere (the bell, a live escalation)

        // ── the tier ladder (SetupPage.SignIn.cs pattern): only Wide gets the stage column ────────────────────────
        var viewport = UseContextSignal(Viewport.Size);
        float plateW = SetupLayout.PlateWidth(viewport.Value.Width);
        var tierSig = UseSignal(SetupLayout.NominalTierFor(plateW));
        UseEffect(() =>
        {
            var current = tierSig.Peek();
            var next = SetupLayout.TierFor(plateW, current);
            if (next != current) tierSig.Value = next;
        }, plateW);
        bool wide = SetupLayout.ShowsHero(tierSig.Value);

        bool windowsOn = settings?.Get(WaveeSettings.NotifyWindows) ?? false;
        bool linksOn = settings?.Get(WaveeSettings.HandleSpotifyLinks) ?? false;
        int language = LanguageIndex(settings?.Get(WaveeSettings.UiCulture) ?? "system");

        int fromHour = settings?.Get(WaveeSettings.NotifyQuietFromHour) ?? 22;
        int toHour = settings?.Get(WaveeSettings.NotifyQuietToHour) ?? 8;
        bool quietOn = settings?.Get(WaveeSettings.NotifyQuietEnabled) ?? false;
        var moreSummary = SetupNotificationSummary.Summarize(new QuietHours(quietOn, fromHour, toHour));

        // ComboBox.Create freezes SelectedIndex at mount (ComboBox.cs:151-158) — the ONE control on this page that
        // needs the UseSignal-once + post-render UseEffect-sync pattern (SetupLayout.cs §0's signal ownership rule).
        // ToggleSwitch/Segmented re-read their prop live every render (verified against SetupCompact.Segmented's own
        // doc comment), so a fresh throwaway Signal per render is correct for those, same as the shipped page.
        var languageSig = UseSignal(language);
        UseEffect(() => languageSig.SetIfChanged(language), language);

        var rows = new List<Element>(7)
        {
            SetupCompact.Row(Loc.Get(Strings.Settings.Notify.Windows),
                ToggleSwitch.Create(new Signal<bool>(windowsOn), onChange: _ =>
                {
                    if (settings is null) return;
                    SetupWrites.SetNotifyWindows(!windowsOn, settings);
                    Bump();
                }, style: SetupCompact.RowToggleStyle)),
            SetupCompact.Row(Loc.Get(Strings.Settings.Notify.NewAlbums),
                Dial(settings, NotifyTopic.NewAlbums, HeadlineDialWidth, Bump),
                Loc.Get(Strings.Setup.Notifications.NewAlbumsSub)),
            SetupCompact.Row(Loc.Get(Strings.Settings.Notify.ReleaseDrops),
                Dial(settings, NotifyTopic.ReleaseDrops, HeadlineDialWidth, Bump),
                Loc.Get(Strings.Settings.Notify.ClosedBadge)),
            SetupCompact.Row(Loc.Get(Strings.Settings.Notify.Concerts),
                Dial(settings, NotifyTopic.Concerts, HeadlineDialWidth, Bump),
                Loc.Get(Strings.Setup.Notifications.ConcertsSub)),
            MoreTopicsRow(overlay, moreSummary),
            SetupCompact.Row(Loc.Get(Strings.Settings.Links.Spotify),
                ToggleSwitch.Create(new Signal<bool>(linksOn), onChange: _ =>
                {
                    if (settings is null) return;
                    SetupWrites.SetHandleSpotifyLinks(!linksOn, settings);
                    Bump();
                }, style: SetupCompact.RowToggleStyle)),
            SetupCompact.Row(Loc.Get(Strings.Settings.Language.Label), LanguageCombo(settings, languageSig)),
        };

        int reachCount = SetupNotificationSummary.WindowsReachCount(AllLevels(settings));
        Element stagePill = windowsOn
            ? SetupStage.Pill(Loc.Get(Strings.Setup.Notifications.MasterOnTitle),
                Strings.Setup.Notifications.MasterOnSub(reachCount), accent: true)
            : SetupStage.Pill(Loc.Get(Strings.Setup.Notifications.MasterOffTitle),
                Loc.Get(Strings.Settings.Notify.WindowsOffHint), accent: false);

        // Below Wide there is no stage column at all — the pill's summary would otherwise become unreachable, so it
        // joins the body instead of the stage (SetupPageHost.Frame's own "pages compute the tier themselves" rule).
        if (!wide) rows.Add(stagePill);

        Element body = SetupCompact.Column(rows.ToArray());

        Element? stage = wide
            ? SetupStage.Column(
                BellRing(),
                ToastPreview(),
                stagePill,
                SetupStage.Spacer(),
                SetupStage.Caption(Loc.Get(Strings.Setup.Notifications.StageCaptionTitle), Loc.Get(Strings.Setup.Notifications.StageCaptionSub)))
            : null;

        return SetupPageHost.Frame(SetupPage.Notifications, Loc.Get(Strings.Setup.Eyebrow.Notifications),
            Loc.Get(Strings.Settings.Notify.Title), body, lead: Loc.Get(Strings.Setup.Notifications.Lead),
            stage: stage, scrollBody: !wide);
    }

    // -- Stage art: a quiet bell inside two accent rings, then a mock of what a Windows banner from Wavee looks like
    // (app row, artwork + text bars, Play / Save) - the approved board's stage. A diagram, not a real toast. -----------
    static Element BellRing() => new BoxEl
    {
        Width = 112f, Height = 112f, Shrink = 0f, AlignSelf = FlexAlign.Center,
        Corners = CornerRadius4.All(56f), BorderWidth = 1f, BorderColor = Tok.AccentDefault with { A = 0.18f },
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children =
        [
            new BoxEl
            {
                Width = 78f, Height = 78f, Corners = CornerRadius4.All(39f), BorderWidth = 1f, BorderColor = Tok.AccentDefault with { A = 0.32f },
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.Bell, 40f, Tok.AccentDefault)],
            },
        ],
    };

    static Element ToastPreview()
    {
        static BoxEl Bar(float w, float h, ColorF c) => new() { Width = w, Height = h, Corners = CornerRadius4.All(h / 2f), Fill = c };
        var ink = Tok.TextPrimary with { A = 0.8f };
        var ink2 = Tok.TextPrimary with { A = 0.35f };
        return new BoxEl
        {
            Direction = 1, Gap = 8f, AlignSelf = FlexAlign.Stretch, Shrink = 0f,
            Padding = new Edges4(14f, 12f, 14f, 12f), Corners = Radii.CardAll,
            Fill = Tok.FillSolidBase, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl { Width = 16f, Height = 16f, Corners = CornerRadius4.All(4f), Fill = Tok.AccentDefault },
                        new TextEl("Wavee") { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary },
                        new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f },
                        new TextEl(Loc.Get(Strings.Setup.Notifications.ToastNow)) { Size = 11f, LineHeight = 15f, Color = Tok.TextTertiary },
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = 44f, Height = 44f, Corners = CornerRadius4.All(4f), Shrink = 0f,
                            Gradient = new GradientSpec(GradientShape.Linear, 135f,
                            [
                                new GradientStop(0f, ColorF.FromRgba(122, 79, 176)),
                                new GradientStop(1f, ColorF.FromRgba(208, 106, 90)),
                            ]),
                        },
                        new BoxEl { Direction = 1, Gap = 5f, Justify = FlexJustify.Center, Children = [Bar(120f, 5f, ink), Bar(160f, 3f, ink2), Bar(90f, 3f, ink2)] },
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Gap = 6f, AlignSelf = FlexAlign.Stretch,
                    Children =
                    [
                        new BoxEl
                        {
                            Grow = 1f, Basis = 0f, Height = 24f, Corners = Radii.ControlAll, Fill = Tok.AccentDefault,
                            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Children = [new TextEl(Loc.Get(Strings.Setup.Notifications.ToastPlay)) { Size = 11f, Weight = 600, Color = Tok.TextOnAccentPrimary }],
                        },
                        new BoxEl
                        {
                            Grow = 1f, Basis = 0f, Height = 24f, Corners = Radii.ControlAll, Fill = Tok.FillControlDefault,
                            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Children = [new TextEl(Loc.Get(Strings.Setup.Notifications.ToastSave)) { Size = 11f, Color = Tok.TextSecondary }],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>The headline dials' control width: a 480-DIP decision column, minus a sub-labelled row's own
    /// padding/label lane/control gap (<see cref="SetupLayout.ControlLane"/> with <c>sub: true</c>) — 248 DIP.</summary>
    static readonly float HeadlineDialWidth = 216f;   // 3 × 72 — "In Wavee" needs ~60; leaves the 52-px rows a 232-px label lane ("Pre-saved albums, on release day" ≈ 230)

    /// <summary>The flyout panel's own dial width: its 420-DIP width minus its own 12-DIP padding on each side, then
    /// a plain (no sub-label) row's lane arithmetic — 214 DIP.</summary>
    internal static readonly float PanelDialWidth = SetupLayout.ControlLane(420f - 2f * 12f, sub: false);

    Element MoreTopicsRow(IOverlayService overlay, SetupNotificationSummary.MoreSummary summary)
    {
        string trailing = summary.QuietOn
            ? Strings.Setup.Notifications.MoreSummaryQuietOn(summary.Count, summary.From, summary.To)
            : Strings.Setup.Notifications.MoreSummaryQuietOff(summary.Count);
        var row = SetupCompact.ClickRow(Loc.Get(Strings.Setup.Notifications.MoreTopics), trailing, () => { });
        return Flyout.Attach(row, overlay, () => Embed.Comp(() => new SetupMoreTopicsPanel()),
            FlyoutPlacement.Top, new PopupOptions(FocusTrap: true, Chrome: PopupChrome.Popup));
    }

    static NotifyLevel[] AllLevels(IAppSettings? settings)
    {
        var topics = NotificationPrefs.AllTopics;
        var levels = new NotifyLevel[topics.Length];
        for (int i = 0; i < topics.Length; i++) levels[i] = NotificationPrefs.Level(settings, topics[i]);
        return levels;
    }

    /// <summary>One topic's dial — a <see cref="SetupCompact.Segmented"/> sized to <paramref name="width"/>, 3
    /// segments when the topic's ceiling is <see cref="NotifyLevel.Windows"/> (every topic except
    /// <see cref="NotifyTopic.LibraryActivity"/>) or 2 when it caps at <see cref="NotifyLevel.InApp"/>. Shared with
    /// <see cref="SetupMoreTopicsPanel"/> so the headline rows and the flyout's rows write through the identical
    /// path.</summary>
    internal static Element Dial(IAppSettings? settings, NotifyTopic topic, float width, System.Action bump)
    {
        var level = NotificationPrefs.Level(settings, topic);
        bool canWindows = NotificationPolicy.CeilingFor(topic) == NotifyLevel.Windows;
        SegmentedItem[] items = canWindows
            ?
            [
                new SegmentedItem(Loc.Get(Strings.Settings.Notify.LevelOff)),
                new SegmentedItem(Loc.Get(Strings.Settings.Notify.LevelInApp)),
                new SegmentedItem(Loc.Get(Strings.Settings.Notify.LevelWindows)),
            ]
            :
            [
                new SegmentedItem(Loc.Get(Strings.Settings.Notify.LevelOff)),
                new SegmentedItem(Loc.Get(Strings.Settings.Notify.LevelInApp)),
            ];

        return SetupCompact.Segmented(items, (int)level, i =>
        {
            if (settings is null) return;
            SetupWrites.SetTopicLevel(topic, (NotifyLevel)i, settings);
            bump();
        }, width);
    }

    internal static string Label(NotifyTopic topic) => Loc.Get(topic switch
    {
        NotifyTopic.NewAlbums => Strings.Settings.Notify.NewAlbums,
        NotifyTopic.NewEpisodes => Strings.Settings.Notify.NewEpisodes,
        NotifyTopic.ReleaseDrops => Strings.Settings.Notify.ReleaseDrops,
        NotifyTopic.Concerts => Strings.Settings.Notify.Concerts,
        NotifyTopic.Followers => Strings.Settings.Notify.Followers,
        NotifyTopic.DaylistRefresh => Strings.Settings.Notify.Daylist,
        NotifyTopic.AppUpdates => Strings.Settings.Notify.AppUpdates,
        _ => Strings.Settings.Notify.LibraryActivity,
    });

    Element LanguageCombo(IAppSettings? settings, Signal<int> sig)
    {
        string[] codes = ["system", "en-US", "nl", "ko-KR"];
        string[] labels =
        [
            Loc.Get(Strings.Settings.Language.System),
            Loc.Get(Strings.Settings.Language.EnglishUs),
            Loc.Get(Strings.Settings.Language.Dutch),
            Loc.Get(Strings.Settings.Language.Korean),
        ];
        return ComboBox.Create(labels, sig, width: 180f, isEnabled: settings is not null,
            onChange: i =>
            {
                if (settings is null || (uint)i >= (uint)codes.Length) return;
                SetupWrites.SetUiCulture(codes[i], settings);
                Bump();
            });
    }

    static int LanguageIndex(string culture)
    {
        string[] codes = ["system", "en-US", "nl", "ko-KR"];
        for (int i = 0; i < codes.Length; i++)
            if (string.Equals(codes[i], culture, System.StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }
}

/// <summary>The "More topics &amp; quiet hours" flyout body — the five non-headline dials plus the quiet-hours
/// preset combo. Opened via <see cref="Flyout.Attach"/> (light-dismiss + Escape closes only this popup;
/// <c>OverlayHost</c> pushes a NESTED focus scope for it and pops just that scope on close, leaving the wizard's own
/// modal scope/veto untouched underneath — see the class-level remarks on <see cref="SetupNotificationsPage"/> for
/// the read-only verification trail). No constructor args by design (the flyout factory is
/// <c>() =&gt; Embed.Comp(() =&gt; new SetupMoreTopicsPanel())</c>): every input is read fresh from context/statics
/// on each render, so re-opening the flyout (or a write made from elsewhere while it's open) always shows the
/// current settings.</summary>
sealed class SetupMoreTopicsPanel : Component
{
    readonly Signal<int> _epoch = new(0);
    void Bump() => _epoch.Value = _epoch.Peek() + 1;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var settings = svc?.Settings;
        _ = _epoch.Value;
        _ = NotificationPrefs.Epoch.Value;

        int fromHour = settings?.Get(WaveeSettings.NotifyQuietFromHour) ?? 22;
        int toHour = settings?.Get(WaveeSettings.NotifyQuietToHour) ?? 8;
        bool quietOn = settings?.Get(WaveeSettings.NotifyQuietEnabled) ?? false;
        int preset = SetupNotificationSummary.QuietPresetIndex(quietOn, fromHour, toHour);

        var presetSig = UseSignal(preset);   // ComboBox freezes SelectedIndex at mount — sync it post-render
        UseEffect(() => presetSig.SetIfChanged(preset), preset);

        Element quietRow = SetupCompact.Row(Loc.Get(Strings.Settings.Notify.Quiet),
            ComboBox.Create(
            [
                Loc.Get(Strings.Settings.Choice.Off),
                "23:00 – 08:00",
                "00:00 – 07:00",
            ], presetSig, width: 150f, isEnabled: settings is not null, onChange: i =>
            {
                if (settings is null || (uint)i >= (uint)SetupNotificationSummary.QuietPresets.Length) return;
                var p = SetupNotificationSummary.QuietPresets[i];
                SetupWrites.SetQuietHours(p.Enabled, p.From, p.To, settings);
                Bump();
            }));

        Element DialRow(NotifyTopic topic) => SetupCompact.Row(SetupNotificationsPage.Label(topic),
            SetupNotificationsPage.Dial(settings, topic, SetupNotificationsPage.PanelDialWidth, Bump));

        return new BoxEl
        {
            Width = 420f, Direction = 1, Gap = SetupLayout.RowGap, Padding = Edges4.All(12f),
            Children =
            [
                quietRow,
                DialRow(NotifyTopic.NewEpisodes),
                DialRow(NotifyTopic.Followers),
                DialRow(NotifyTopic.DaylistRefresh),
                DialRow(NotifyTopic.AppUpdates),
                DialRow(NotifyTopic.LibraryActivity),
            ],
        };
    }
}
