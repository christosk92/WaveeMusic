using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── Settings shell — tab strip + shared layout helpers; tab bodies live in partials ────────────────────────────────
sealed partial class SettingsPage : Component
{
    // 7 tabs (Workstream B, "Settings regroup + removals"): Appearance is new (split out of the old catch-all General
    // tab), and Diagnostics is renamed Logs — its non-log content (the three developer switches, "Simulate an
    // update", crash reports) moved to General › Developer and About › Reports, so Logs is now ONLY the full-height
    // log viewer (LogsPanel, Workstream C).
    const int TabGeneral = 0, TabAppearance = 1, TabPlayback = 2, TabNotifications = 3, TabStorage = 4, TabLogs = 5, TabAbout = 6;
    const float SettingsContentMaxWidth = 1000f;
    const float SettingsCardSpacing = 4f;
    static readonly Edges4 SettingsSectionHeaderMargin = new(0f, Spacing.XXXL, 0f, Spacing.S);
    // Must stay 1:1 with Tab* / TabLabels() — missing Notifications made About (index 5) IndexOutOfRange.
    static readonly string[] s_tabKeys = ["general", "appearance", "playback", "notifications", "storage", "logs", "about"];

    readonly Signal<int> _tab = new(0);
    readonly Signal<int> _uiEpoch = new(0);

    IOverlayService? _overlay;
    Action<Action>? _voPost;   // the UI-thread post, kept for the video-override flyout's deferred deep-link open
    // The app-wide nav action, captured UNCONDITIONALLY in Render (hooks may not sit behind the tab switch) and read
    // back from the per-tab builders — today the Playback tab's runtime-problem banner ("View diagnostics").
    Action<string, string?>? _nav;
    // The developer-mode update simulator's injection target (General › Developer › "Simulate an update"), captured
    // UNCONDITIONALLY here (hooks may not sit behind the tab switch) even though only the General tab's body reads it —
    // the same rule _nav follows.
    NotificationCenterBridge? _nc;

    void Bump() => _uiEpoch.Value = _uiEpoch.Peek() + 1;

    void ConfirmThen(string title, string body, string primaryText, Action onConfirm) =>
        SettingsShared.Confirm(_overlay, title, body, primaryText, onConfirm);

    static string[] TabLabels() =>
    [
        Loc.Get(Strings.Settings.Tabs.General),
        Loc.Get(Strings.Settings.Tabs.Appearance),
        Loc.Get(Strings.Settings.Tabs.Playback),
        Loc.Get(Strings.Settings.Notify.Title),
        Loc.Get(Strings.Settings.Tabs.Storage),
        Loc.Get(Strings.Settings.Tabs.Logs),
        Loc.Get(Strings.Settings.Tabs.About),
    ];

    static string[] ThemeLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.System),
        Loc.Get(Strings.Settings.Choice.Light),
        Loc.Get(Strings.Settings.Choice.Dark),
    ];

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        var svc = UseContext(Services.Slot);
        var requestTheme = UseContext(ThemeControl.Request);
        var post = UsePost();
        var seeded = UseRef(false);
        _overlay = UseContext(Overlay.Service);
        _nav = UseContext(HistoryStore.NavCtx);
        _nc = UseContext(NotificationCenterBridge.Slot);
        _voPost = post;

        UseEffect(() =>
        {
            if (seeded.Value || svc is null) return;
            seeded.Value = true;
            _density.Value = svc.Settings.Get(WaveeSettings.RowDensity);
            _quality.Value = Math.Clamp(svc.Settings.Get(WaveeSettings.PlaybackQuality), 0, 2);
            _videoQuality.Value = VideoQualityIndex(svc.Settings.Get(WaveeSettings.VideoQuality));
            _meteredVideoQuality.Value = MeteredVideoQualityIndex(svc.Settings.Get(WaveeSettings.VideoMeteredMaxHeight));
            _eqPreset.Value = EqPresetIndex(svc.Settings.Get(WaveeSettings.EqualizerPreset));
            int crossMs = Math.Clamp(svc.Settings.Get(WaveeSettings.CrossfadeMs), 0, 12_000);
            _crossSecs.Value = crossMs / 1000.0;
            _crossSlider.Value = (float)(crossMs / 1000.0);
            _language.Value = LanguageIndex(svc.Settings.Get(WaveeSettings.UiCulture));
        }, DepKey.Empty);

        _ = _uiEpoch.Value;
        _ = PlayerBarPrefs.Epoch.Value;

        // "Manage" on a video-override toast navigates here and bumps this counter; land on the tab that owns the
        // roster AND open its Manage flyout (the roster no longer lives inline, so landing on the tab alone would
        // strand the user one click short). Same monotonic-request shape as OpenPlaybackRuntimeSetup (Settings has no
        // route-arg tab deep-link).
        int overridesReq = svc?.Playback.OpenVideoOverrides.Value ?? 0;
        var lastOverridesReq = UseRef(-1);
        UseEffect(() =>
        {
            if (lastOverridesReq.Value < 0) { lastOverridesReq.Value = overridesReq; return; }
            if (overridesReq == lastOverridesReq.Value) return;
            lastOverridesReq.Value = overridesReq;
            _tab.Value = TabPlayback;
            RequestVideoOverrideManager(post);
        }, overridesReq);

        int tab = _tab.Value;

        UseEffect(() =>
        {
            if (tab == TabStorage && _storageLoad.Peek() == StorageLoadPhase.NotStarted)
            {
                RefreshStorage(post);
                RefreshMetadataStats(svc, post);   // the library.db cache-tier census (§G) — its own writer-lane read
            }
            if (tab == TabPlayback) RefreshVideoOverrides(svc, post);
            // Leaving the tab destroys the Manage button: close its flyout and drop the now-dead anchor, so a later
            // deep-link can never open against a stale node.
            else CloseVideoManager();
        }, tab);

        // Live roster refresh: the curation also changes from the track context menu (and from an undo toast raised
        // anywhere), so the section watches the store's roster sentinel rather than only its own mutations.
        UseEffect(() => WatchVideoOverrides(svc, post), DepKey.Empty);

        Element body = tab switch
        {
            TabAppearance => AppearanceTab(svc, requestTheme),
            TabPlayback => PlaybackTab(svc),
            TabNotifications => NotificationsTab(svc),
            TabStorage => StorageTab(svc, post),
            TabLogs => new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinHeight = 0f,
                Children = [Embed.Comp(() => new LogsPanel(svc?.Settings))],
            },
            TabAbout => AboutTab(svc, hooks),
            _ => GeneralTab(svc, _nc),
        };

        Element content = tab == TabLogs
            ? new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1,
                Padding = new Edges4(Spacing.PageWide, Spacing.L, Spacing.PageWide, Spacing.L),
                Children = [body],
            }
            : ScrollView(new BoxEl
            {
                Direction = 1,
                Padding = new Edges4(Spacing.PageWide, Spacing.L, Spacing.PageWide, Spacing.PageWide),
                Children = [SettingsContentColumn(body)],
            }) with { Grow = 1f, ScrollKey = "settings:" + s_tabKeys[tab], Key = "settings:scroll:" + s_tabKeys[tab] };

        return new BoxEl
        {
            Grow = 1f, Direction = 1,
            Children =
            [
                Header(),
                new BoxEl
                {
                    Direction = 1, Padding = new Edges4(Spacing.PageWide, 0f, Spacing.PageWide, 0f),
                    Children =
                    [
                        SelectorBar.Create(TabLabels(), _tab),
                        Divider(),
                    ],
                },
                content,
            ],
        };
    }

    static Element SettingsContentColumn(Element body) => new BoxEl
    {
        Direction = 1,
        MaxWidth = SettingsContentMaxWidth,
        AlignSelf = FlexAlign.Stretch,
        Children = [body],
    };

    static Element SettingsTabStack(params Element[] children) => new BoxEl
    {
        Direction = 1,
        Gap = SettingsCardSpacing,
        AlignSelf = FlexAlign.Stretch,
        Children = children,
    };

    /// <summary>A group eyebrow: icon + bold title, and optionally a one-line caption saying what the group holds.
    /// Title and caption share a COLUMN beside the icon, so the caption aligns under the title without anyone
    /// hand-computing a glyph-width indent.</summary>
    static Element SettingsSectionHeader(string title, string? icon = null, string? subtitle = null)
    {
        Element text = subtitle is { Length: > 0 } sub
            ? new BoxEl
            {
                Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    BodyStrong(title),
                    Caption(sub) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2 },
                ],
            }
            : BodyStrong(title);

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S,
            // A one-line header centres on its icon; a two-line block hangs from the top, so the glyph sits beside the
            // TITLE rather than floating between the two lines.
            AlignItems = subtitle is { Length: > 0 } ? FlexAlign.Start : FlexAlign.Center,
            Margin = SettingsSectionHeaderMargin,
            AlignSelf = FlexAlign.Stretch,
            Children = icon is null
                ? [text]
                : [Icon(icon, 16f, Tok.TextSecondary) with { Margin = new Edges4(0f, 2f, 0f, 0f) }, text],
        };
    }

    static Element Header() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.PageWide, Spacing.L, Spacing.PageWide, Spacing.M),
        Children =
        [
            Icon(Icons.Settings, 24f, Tok.TextPrimary),
            WaveeType.PageHero(Loc.Get(Strings.Settings.Title)) with { Grow = 1f },
        ],
    };

    static Element SettingsRow(string label, string? sub, Element? control = null, string? icon = null,
                               SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                               bool isClickEnabled = false, Action? onClick = null, bool isEnabled = true)
        => SettingsCard.Create(new SettingsCard.Options
        {
            Header = label,
            Description = sub,
            HeaderIcon = icon,
            Content = control,
            Alignment = align,
            IsClickEnabled = isClickEnabled,
            IsActionIconVisible = isClickEnabled,
            OnClick = onClick,
            IsEnabled = isEnabled,
        });

    static Element SettingsItem(string label, string? sub, Element? control = null,
                                SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                                bool isEnabled = true, bool isClickEnabled = false, Action? onClick = null,
                                string? icon = null)
        => SettingsExpander.Item(label, sub, control, align, isEnabled, isClickEnabled, onClick, icon);

    /// <summary>What a COLLAPSED group is currently set to, for a <see cref="SettingsExpander"/>'s header content slot.
    /// A group whose body is a picker has to answer its own question from the outside, or the user has to open every
    /// one of them to find out what the page says.</summary>
    static Element SettingsValueTag(string value) => new TextEl(value)
    {
        Size = 14f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>Wide content (a wireframe card strip) inside a <see cref="SettingsExpander"/> body, for its
    /// <c>ItemsHeader</c> slot. The items panel carries no padding of its own, and a card strip is not a settings ROW —
    /// an empty-header <c>SettingsCard</c> would reserve a phantom label column beside it. Deliberately NO
    /// fill/border/corners: the expander body already paints the group chrome, and a second card around the first
    /// doubles the stroke.</summary>
    static Element SettingsExpanderPanel(Element content) => new BoxEl
    {
        Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
        Children = [content],
    };
}
