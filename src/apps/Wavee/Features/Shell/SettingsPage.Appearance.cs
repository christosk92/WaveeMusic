using System;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The Appearance tab: Theme · Lists · Sidebar · Lyrics — split out of the old catch-all General tab (Workstream B,
// "Settings regroup + removals"). Two rules earn their keep here (carried over from the old file):
//
//  1. A PICKER GOES IN AN EXPANDER BODY, NEVER AN EXPANDER HEADER. The header content slot lands in a SettingsCard's
//     right-hand Auto grid track, which starves the header text track toward zero once the content is wider than the
//     card — and a zero-width text run neither wraps nor clips, so the header paints straight over the content. That
//     was the Sidebar group's overlapping "Sidebar design" bug. The header carries the ANSWER ("Default", "Custom") via
//     SettingsValueTag; the cards live in ItemsHeader.
//  2. COLLAPSED BY DEFAULT. Row density and sidebar design stacked always-visible wireframes between rows; collapsed,
//     each still says what it is set to, and the page fits.
//
// GONE from here: the palette picker (Settings + profile menu — Wavee always renders the neutral palette now), Mica
// Alt (always base Mica), and "Limit page color to the hero" (the tone always covers the full page now). "Track page
// layout" (Automatic/Hero) is BACK — restored on user request after the regroup shipped without it — but travels
// alone now: its former "Limit page color to the hero" sibling stays removed for good. "Disable marquee text" /
// "Disable color washes" are inverted to two flat ON switches, "Marquee text" / "Color washes" — no wrapping "Visual
// effects" expander, no "N disabled" tag: two rows, two answers.
sealed partial class SettingsPage
{
    readonly Signal<int> _density = new(1);

    /// <summary>The live app-zoom factor. Written in <c>SettingsPage.Render</c> from <c>Viewport.Zoom</c> — a Context,
    /// so the page re-renders when the chords, the Ctrl+wheel hook or the palette change it. It is read there rather
    /// than here because <see cref="AppearanceTab"/> only runs for one tab, and a hook called conditionally breaks
    /// hook order.</summary>
    float _zoomLive = 1f;

    /// <summary>The live DIP viewport (<c>Viewport.Size</c>), captured the same way as <see cref="_zoomLive"/> and for
    /// the same reason: the Zoom row's Auto item needs <c>baseDip = _viewportLive * _zoomLive</c> (ZoomAutoPolicy's
    /// base-extent recovery — see its own type doc) to show the CURRENT resolved percentage, not whatever the window
    /// size was when this tab last rebuilt.</summary>
    Size2 _viewportLive;

    static string[] DensityLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.Compact),
        Loc.Get(Strings.Settings.Choice.Default),
        Loc.Get(Strings.Settings.Choice.Cozy),
        Loc.Get(Strings.Settings.Choice.Comfortable),
    ];

    static string[] TrackListStyleLabels() =>
    [
        Loc.Get(Strings.Settings.Appearance.TrackListModern),
        Loc.Get(Strings.Settings.Appearance.TrackListClassic),
    ];

    static string[] PageLayoutLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.Automatic),
        Loc.Get(Strings.Settings.Choice.Hero),
    ];

    // The zoom picker's labels, hoisted ONCE — unlike the Loc-backed label builders around it these can never change
    // with culture (digits + '%'), so rebuilding the array per render would be pure churn. The picker index IS the
    // ZoomLadder.Steps index (the ThemeMode/RowDensity "index == stored shape" convention, with the ladder as the wire).
    static readonly string[] ZoomLabels = BuildZoomLabels();
    static string[] BuildZoomLabels()
    {
        var labels = new string[ZoomLadder.Steps.Length];
        for (int i = 0; i < labels.Length; i++) labels[i] = ZoomLadder.Percent(ZoomLadder.Steps[i]) + "%";
        return labels;
    }

    // The Auto head item's label carries the LIVE resolved percentage (the JetBrains "Zoom IDE" affordance —
    // large-display-scaling.md §3.2), so unlike ZoomLabels above this is rebuilt per render off autoSuggested — one
    // small array, on a settings page, is not the churn ZoomLabels' comment is guarding against.
    static string[] BuildZoomComboLabels(int autoPercent)
    {
        var labels = new string[ZoomLabels.Length + 1];
        labels[0] = Strings.Settings.Appearance.ZoomAuto(autoPercent);
        Array.Copy(ZoomLabels, 0, labels, 1, ZoomLabels.Length);
        return labels;
    }

    // The lyrics SECOND line. Ordered to match WaveeSettings.LyricsSecondaryLine (0 none · 1 translation · 2
    // romanization) so the SelectorBar index IS the stored value — the ThemeMode/RowDensity convention.
    static string[] LyricsSecondaryLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.Off),
        Loc.Get(Strings.Settings.Choice.Translation),
        Loc.Get(Strings.Settings.Choice.Romanization),
    ];

    /// <summary>An appearance on/off row. Takes <paramref name="settings"/> explicitly rather than closing over it, so
    /// the group builders below can reuse it without a per-render delegate.</summary>
    Element AppearanceToggle(IAppSettings? settings, SettingKey<bool> key)
        => ToggleSwitch.Create(new Signal<bool>(settings?.Get(key) ?? false), onChange: _ =>
        {
            if (settings is null) return;
            settings.Set(key, !settings.Get(key));
            AppearancePrefs.Bump();
            Bump();
        }, style: SettingsCard.CompactToggleStyle());

    Element AppearanceTab(Services? svc, Action<float>? requestTheme)
    {
        var settings = svc?.Settings;
        int themeMode = settings?.Get(WaveeSettings.ThemeMode) ?? 0;
        int density = Math.Clamp(_density.Value, 0, DensityLabels().Length - 1);
        int trackListStyle = Math.Clamp(settings?.Get(WaveeSettings.TrackRowStyle) ?? 0, 0, TrackListStyleLabels().Length - 1);
        int pageLayout = Math.Clamp(settings?.Get(WaveeSettings.DetailPageLayout) ?? 0, 0, PageLayoutLabels().Length - 1);
        bool railUniform = settings?.Get(WaveeSettings.DetailRailUniform) ?? false;
        // The reset button's enable gate: offering "Clear all remembered sizes" when nothing has ever moved from its
        // authored default would be a destructive-looking no-op. Deliberately excludes the uniform pair itself — this
        // row only ever shows while uniform mode is off (see PageLayoutItems).
        bool hasCustomRail = settings is not null && DetailRailPolicy.HasCustomizedRailPrefs(
            settings.Get(WaveeSettings.DetailAlbumRailWidth), settings.Get(WaveeSettings.DetailAlbumRailCollapsed),
            settings.Get(WaveeSettings.DetailPlaylistRailWidth), settings.Get(WaveeSettings.DetailPlaylistRailCollapsed),
            settings.Get(WaveeSettings.DetailLikedRailWidth), settings.Get(WaveeSettings.DetailLikedRailCollapsed),
            settings.Get(WaveeSettings.DetailShowRailWidth), settings.Get(WaveeSettings.DetailShowRailCollapsed));
        int lyricsSecondary = Math.Clamp(settings?.Get(WaveeSettings.LyricsSecondaryLine) ?? 0, 0, LyricsSecondaryLabels().Length - 1);
        // The zoom picker's mode: Auto/Dense show as the ONE head item ("Auto"); only Manual shows a ladder rung
        // selected. Clamp tolerates a value this build doesn't define (the int-enum convention).
        var zoomMode = (ZoomAutoMode)Math.Clamp(settings?.Get(WaveeSettings.ZoomMode) ?? (int)ZoomAutoMode.Auto, 0, 2);
        bool zoomManual = zoomMode == ZoomAutoMode.Manual;
        // baseDip: the window's DIP extent AT ZOOM 1, recovered from the live (already-zoomed) viewport without a new
        // engine seam — see ZoomAutoPolicy's own type doc for the full contract (baseDip = viewportDip * zoom;
        // Viewport.Zoom read here is the engine's sanctioned display-only use — a POLICY INPUT, never a coordinate
        // conversion). _viewportLive/_zoomLive are both captured unconditionally in Render (see their own docs).
        float baseW = _viewportLive.Width * _zoomLive, baseH = _viewportLive.Height * _zoomLive;
        float autoSuggested = ZoomAutoPolicy.Suggest(baseW, baseH, ZoomAutoMode.Auto);
        // The LIVE zoom, not the stored key: the chords/wheel change the factor ahead of the debounced persist, and the
        // row must show what the window is doing right now. Read off _zoomLive (Viewport.Zoom, subscribed in Render)
        // rather than FluentApp.Zoom — that is a plain static, so reading it here subscribed to nothing and the row
        // showed whatever it read at mount. Snap → IndexOf is exact (Snap returns a Steps element); Max is
        // belt-and-suspenders for an engine value the ladder somehow doesn't contain. Index 0 is the Auto head item —
        // see BuildZoomComboLabels — so every ladder-step index shifts up by one.
        int zoomIndex = zoomManual
            ? 1 + Math.Max(0, Array.IndexOf(ZoomLadder.Steps, ZoomLadder.Snap(_zoomLive)))
            : 0;
        string[] zoomComboLabels = BuildZoomComboLabels(ZoomLadder.Percent(autoSuggested));

        void SetTheme(int mode)
        {
            WaveeTheme.ApplyThemeMode(mode, settings);
            requestTheme?.Invoke(250f);
            Bump();
        }

        // The SetWindowMaterial shape this used to sit beside (write the setting, then the live side effect — here
        // inverted: apply live, then write): FluentApp.SetZoom re-derives the effective window scale at once, and the
        // picker writes the setting IMMEDIATELY rather than waiting for WaveeApp's debounced zoom-save timer — a
        // deliberate pick from a deliberate control, unlike the chords/wheel where the debounce exists to absorb a
        // spin of repeats. Index 0 (Auto) sets mode = Auto and applies THIS render's already-computed autoSuggested
        // immediately, the same "deliberate pick" way; any other index is a manual ladder rung and sets mode = Manual
        // — the SidebarPreferences.WidthUserSet idiom: a deliberate act overrides the policy.
        void SetZoom(int i)
        {
            if (settings is null) return;
            if (i <= 0)
            {
                settings.Set(WaveeSettings.ZoomMode, (int)ZoomAutoMode.Auto);
                FluentApp.SetZoom(autoSuggested);
                settings.Set(WaveeSettings.ZoomLevel, autoSuggested);
                Bump();
                return;
            }
            int stepIndex = i - 1;
            if ((uint)stepIndex >= (uint)ZoomLadder.Steps.Length) return;
            float z = ZoomLadder.Steps[stepIndex];
            settings.Set(WaveeSettings.ZoomMode, (int)ZoomAutoMode.Manual);
            FluentApp.SetZoom(z);   // live, no restart
            settings.Set(WaveeSettings.ZoomLevel, z);
            Bump();
        }

        void SetDensity(int i)
        {
            settings?.Set(WaveeSettings.RowDensity, i);
            _density.Value = i;
            Bump();
        }

        void SetTrackListStyle(int i)
        {
            if (settings is null || (uint)i >= (uint)TrackListStyleLabels().Length) return;
            settings.Set(WaveeSettings.TrackRowStyle, i);
            AppearancePrefs.Bump();
            Bump();
        }

        void SetPageLayout(int i)
        {
            settings?.Set(WaveeSettings.DetailPageLayout, i);
            DetailHeroPrefs.Bump();   // live-update any mounted (incl. KeepAlive-parked) detail page's rail↔hero choice
            Bump();
        }

        // The SAME epoch SetPageLayout bumps above — no second bump mechanism. A mounted DetailShell re-reads this
        // flag (and re-syncs its four per-scope rail signals) off that one bump; see DetailShell.Render.
        void SetRailUniform(bool on)
        {
            settings?.Set(WaveeSettings.DetailRailUniform, on);
            DetailHeroPrefs.Bump();
            Bump();
        }

        // "Clear all remembered sizes": resets the FOUR per-scope pairs to their authored defaults — never the
        // uniform pair, which this row has no business touching (it is the per-surface case, offered only while
        // uniform mode is off). Bumps the same epoch so an open page's live rail signals snap to the reset widths
        // immediately (DetailShell.ResyncRail) instead of on the next launch.
        void ResetPerScopeRailPrefs()
        {
            if (settings is null) return;
            settings.Set(WaveeSettings.DetailAlbumRailWidth, WaveeSettings.DetailAlbumRailWidth.Default);
            settings.Set(WaveeSettings.DetailAlbumRailCollapsed, false);
            settings.Set(WaveeSettings.DetailPlaylistRailWidth, WaveeSettings.DetailPlaylistRailWidth.Default);
            settings.Set(WaveeSettings.DetailPlaylistRailCollapsed, false);
            settings.Set(WaveeSettings.DetailLikedRailWidth, WaveeSettings.DetailLikedRailWidth.Default);
            settings.Set(WaveeSettings.DetailLikedRailCollapsed, false);
            settings.Set(WaveeSettings.DetailShowRailWidth, WaveeSettings.DetailShowRailWidth.Default);
            settings.Set(WaveeSettings.DetailShowRailCollapsed, false);
            DetailHeroPrefs.Bump();
            Bump();
        }

        // Its own writer rather than an AppearanceToggle: the lyrics surfaces re-read this one under LyricsPrefs.Epoch
        // (which the rail/immersive header toggles also bump), not under AppearancePrefs — one setting, one epoch, so a
        // change from either place reaches both mounted surfaces on the same frame.
        void SetLyricsSecondary(int i)
        {
            LyricsPrefs.Set(settings, i);
            Bump();
        }

        return SettingsTabStack(
            SettingsSectionHeader(Loc.Get(Strings.Settings.Appearance.Title),
                SettingsGlyphs.Section(SettingsTab.Appearance, "Theme"),
                Loc.Get(Strings.Settings.Appearance.Subtitle)),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.Theme), Loc.Get(Strings.Settings.Appearance.ThemeSub),
                SelectorBar.Create(ThemeLabels(), new Signal<int>(themeMode), onChange: SetTheme),
                SettingsGlyphs.Row(SettingsTab.Appearance, "theme")),
            // A ComboBox, not a SelectorBar: thirteen items (Auto + twelve ladder steps) would be thirteen segments
            // wide. Same chords as a browser (Ctrl+± / Ctrl+0 / Ctrl+wheel) — the row is the discoverable face of the
            // same ladder, now headed by the display-derived Auto item (large-display-scaling.md §3.2).
            SettingsRow(Loc.Get(Strings.Settings.Appearance.Zoom), Loc.Get(Strings.Settings.Appearance.ZoomSub),
                ComboBox.Create(zoomComboLabels, new Signal<int>(zoomIndex), width: 160f, isEnabled: settings is not null,
                    onChange: SetZoom), SettingsGlyphs.Row(SettingsTab.Appearance, "zoom")),
            // Two FLAT on-switches — no "Visual effects" expander, no "N disabled" tag. Both read ENABLED-key/default-
            // true settings now (MarqueeEnabled/ColorWashesEnabled), so AppearanceToggle's Get/!Get shape is naturally
            // an ON switch instead of the double negative "Disable marquee text" used to read as.
            SettingsRow(Loc.Get(Strings.Settings.Appearance.Marquee), Loc.Get(Strings.Settings.Appearance.MarqueeSub),
                AppearanceToggle(settings, WaveeSettings.MarqueeEnabled), SettingsGlyphs.Row(SettingsTab.Appearance, "marquee")),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.ColorWashes), Loc.Get(Strings.Settings.Appearance.ColorWashesSub),
                AppearanceToggle(settings, WaveeSettings.ColorWashesEnabled), SettingsGlyphs.Row(SettingsTab.Appearance, "colorWashes")),

            // Lists: the two picker groups that decide how track rows look. Both collapsed — their headers report the
            // current answer, and their wireframes only have to exist while choosing.
            SettingsSectionHeader(Loc.Get(Strings.Settings.Layout.Title),
                SettingsGlyphs.Section(SettingsTab.Appearance, "Lists"),
                Loc.Get(Strings.Settings.Layout.Subtitle)),
            DensityGroup(density, SetDensity, settings),
            TrackListStyleGroup(trackListStyle, SetTrackListStyle),
            PageLayoutGroup(pageLayout, SetPageLayout, railUniform, SetRailUniform, hasCustomRail,
                () => ConfirmThen(Loc.Get(Strings.Settings.Appearance.RailResetConfirmTitle),
                    Loc.Get(Strings.Settings.Appearance.RailResetConfirmBody),
                    Loc.Get(Strings.Settings.Appearance.RailResetAction),
                    ResetPerScopeRailPrefs)),

            // The sidebar design. A Component rather than an inline block: the card needs SidebarPreferences and the
            // nav action from CONTEXT, and AppearanceTab runs only while the Appearance tab is selected, so a hook
            // added here would be a conditional hook (it would vanish from the page's hook order the moment another
            // tab renders).
            SettingsSectionHeader(Loc.Get(Strings.Settings.Sidebar.Title),
                SettingsGlyphs.Section(SettingsTab.Appearance, "Sidebar"),
                Loc.Get(Strings.Settings.Sidebar.Subtitle)),
            Embed.Comp(() => new SidebarSettingsCard()),

            // The lyrics reading surface owns its own group: the second line and the cover drift are choices about the
            // same screen, and neither is "appearance" in the shell-wide sense the group above means.
            SettingsSectionHeader(Loc.Get(Strings.Settings.Lyrics.Title),
                SettingsGlyphs.Section(SettingsTab.Appearance, "Lyrics"),
                Loc.Get(Strings.Settings.Lyrics.Subtitle)),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.LyricsSecondary), Loc.Get(Strings.Settings.Appearance.LyricsSecondarySub),
                SelectorBar.Create(LyricsSecondaryLabels(), new Signal<int>(lyricsSecondary), onChange: SetLyricsSecondary),
                SettingsGlyphs.Row(SettingsTab.Appearance, "lyricsSecondary")),
            // A plain AppearanceToggle: its Bump() raises AppearancePrefs.Epoch, which ImmersiveLyricsSurface reads, so
            // flipping it starts/stops the drift on an OPEN surface — no restart.
            SettingsRow(Loc.Get(Strings.Settings.Appearance.LyricsBackdrop), Loc.Get(Strings.Settings.Appearance.LyricsBackdropSub),
                AppearanceToggle(settings, WaveeSettings.LyricsAnimatedBackdrop), SettingsGlyphs.Row(SettingsTab.Appearance, "lyricsBackdrop")));
    }

    // ── Lists → Row density ───────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The density picker, collapsed behind its own answer. <c>ItemsHeader</c> rather than an <c>Items</c> row:
    /// a wireframe strip is not a settings row, and an empty-header <c>SettingsCard</c> would reserve a phantom label
    /// column beside it.</summary>
    Element DensityGroup(int density, Action<int> setDensity, IAppSettings? settings)
        => SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Appearance.RowDensity),
            Description = Loc.Get(Strings.Settings.Appearance.RowDensitySub),
            HeaderIcon = SettingsGlyphs.Row(SettingsTab.Appearance, "rowDensity"),
            Content = SettingsValueTag(DensityLabels()[density]),
            ItemsHeader = SettingsExpanderPanel(DensityCards(density, setDensity)),
            Items =
            [
                SettingsItem(Loc.Get(Strings.Settings.Appearance.HideTrackArtwork),
                    Loc.Get(Strings.Settings.Appearance.HideTrackArtworkSub),
                    TrackArtworkCheckBox(settings), icon: SettingsGlyphs.Row(SettingsTab.Appearance, "hideTrackArtwork")),
            ],
        }) with { Key = "appearance.density" };

    Element TrackArtworkCheckBox(IAppSettings? settings)
        => CheckBox.Create("", new Signal<bool>(settings?.Get(WaveeSettings.HideTrackArtwork) ?? false), onChange: next =>
        {
            if (settings is null) return;
            settings.Set(WaveeSettings.HideTrackArtwork, next);
            AppearancePrefs.Bump();
            Bump();
        }, style: CheckBox.DefaultStyle with { MinWidth = Spacing.XXXL, MinHeight = Spacing.XXXL });

    // The preview card IS the radio (WaveePicker owns the shell, the ink pair and the group keyboard contract). The real
    // density ordering is compressed into each fixed-size wireframe, so the choice communicates row height before it is
    // applied.
    static Element DensityCards(int selected, Action<int> set)
    {
        var labels = DensityLabels();

        Element Card(int value, bool on)
            => WaveePicker.Titled(
                WaveePicker.Card(on, WaveePicker.Tile, WaveePicker.DensityRows(value, on)),
                labels[value], on);

        return WaveePicker.Strip(labels.Length, selected, Card, set);
    }

    // ── Lists → Track list style ──────────────────────────────────────────────────────────────────────────────────────
    Element TrackListStyleGroup(int style, Action<int> setStyle)
        => SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Appearance.TrackListStyle),
            Description = Loc.Get(Strings.Settings.Appearance.TrackListStyleSub),
            HeaderIcon = SettingsGlyphs.Row(SettingsTab.Appearance, "trackListStyle"),
            Content = SettingsValueTag(TrackListStyleLabels()[style]),
            ItemsHeader = SettingsExpanderPanel(TrackListStyleCards(style, setStyle)),
        }) with { Key = "appearance.track-list-style" };

    // These are UI-native miniature rows, not screenshots: the examples inherit the live theme and remain crisp at
    // every scale. Modern shows the art-led stacked-row grammar; Classic shows three aligned text lanes + a hairline.
    static Element TrackListStyleCards(int selected, Action<int> set)
    {
        var labels = TrackListStyleLabels();

        Element Card(int value, bool on)
        {
            var ink = WaveePicker.Ink.For(on);
            Element Row() => value == 0 ? WaveePicker.ModernRow(ink) : WaveePicker.ClassicRow(ink);
            return WaveePicker.Titled(
                WaveePicker.Card(on, WaveePicker.Tile, Row(), Row(), Row()) with { Justify = FlexJustify.Center },
                labels[value], on);
        }

        return WaveePicker.Strip(labels.Length, selected, Card, set);
    }

    // ── Lists → Track page layout ─────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The page-layout picker. Collapsed like its siblings above — the header reports the current answer, and
    /// the wireframes only have to exist while choosing. Its former sibling row here, "Limit page color to the hero",
    /// stays removed (the tone always covers the full page now). Two sub-rows now ride the Items slot, both gated to
    /// the Automatic choice (the Hero layout has no rail at all — see PageLayoutItems).</summary>
    Element PageLayoutGroup(int pageLayout, Action<int> setPageLayout, bool railUniform, Action<bool> setRailUniform,
        bool hasCustomRail, Action confirmReset)
        => SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Appearance.PageLayout),
            Description = Loc.Get(Strings.Settings.Appearance.PageLayoutSub),
            HeaderIcon = SettingsGlyphs.Row(SettingsTab.Appearance, "pageLayout"),
            Content = SettingsValueTag(PageLayoutLabels()[pageLayout]),
            ItemsHeader = SettingsExpanderPanel(PageLayoutCards(pageLayout, setPageLayout)),
            Items = PageLayoutItems(pageLayout, railUniform, setRailUniform, hasCustomRail, confirmReset),
        }) with { Key = "appearance.pagelayout" };

    // Two mutually exclusive sub-rows, both meaningless outside the Automatic layout (Hero never composes a rail):
    //  · "Keep left-rail same size" — always shown for Automatic.
    //  · "Clear all remembered sizes" — only while that toggle is OFF: once every surface already shares one width,
    //    there is nothing per-surface left to clear.
    static Element[] PageLayoutItems(int pageLayout, bool railUniform, Action<bool> setRailUniform,
        bool hasCustomRail, Action confirmReset)
    {
        if (pageLayout != DetailVerticalLayout.PageAuto) return [];
        Element uniformRow = SettingsItem(Loc.Get(Strings.Settings.Appearance.RailUniform),
            Loc.Get(Strings.Settings.Appearance.RailUniformSub),
            ToggleSwitch.Create(new Signal<bool>(railUniform), onChange: setRailUniform, style: SettingsCard.CompactToggleStyle()),
            icon: SettingsGlyphs.Row(SettingsTab.Appearance, "railUniform"));
        if (railUniform) return [uniformRow];
        Element resetRow = SettingsItem(Loc.Get(Strings.Settings.Appearance.RailReset),
            Loc.Get(Strings.Settings.Appearance.RailResetSub),
            Button.Standard(Loc.Get(Strings.Settings.Appearance.RailResetAction), confirmReset, isEnabled: hasCustomRail),
            icon: SettingsGlyphs.Row(SettingsTab.Appearance, "railReset"));
        return [uniformRow, resetRow];
    }

    // Each card is a mini skeleton-bar wireframe of the page SYSTEM it selects — Automatic: a narrow metadata rail
    // (art + title/meta bars + a pill) BESIDE a column of full-width track rows (the rail-when-wide layout); Hero:
    // adaptive artwork + identity ABOVE the track rows at every width.
    static Element PageLayoutCards(int selected, Action<int> set)
    {
        var labels = PageLayoutLabels();

        Element Card(int value, bool on)
        {
            var ink = WaveePicker.Ink.For(on);

            Element Bar(float w, float h) => new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(h / 2f), Fill = ink.Faint };
            Element RowBar() => new BoxEl { Height = 4f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(2f), Fill = ink.Faint };
            Element Art(float edge) => new BoxEl { Width = edge, Height = edge, Corners = CornerRadius4.All(4f), Fill = ink.Block, Shrink = 0f };
            Element Pill() => new BoxEl { Width = 24f, Height = 8f, Corners = CornerRadius4.All(Radii.Control), Fill = ink.Block };
            Element SmallPill() => new BoxEl { Width = 20f, Height = 8f, Corners = CornerRadius4.All(4f), Fill = ink.Block };
            Element Pills() => new BoxEl { Direction = 0, Gap = 4f, Children = [Pill(), Pill()] };

            Element sketch = value == DetailVerticalLayout.PageAuto
                // Automatic: a narrow LEFT rail column (art over title/meta bars + a pill) beside a RIGHT column of
                // full-width track rows — "side rail beside tracks" on a wide window.
                ? new BoxEl
                {
                    Direction = 0, Gap = 8f, Grow = 1f, AlignItems = FlexAlign.Stretch,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Gap = 4f, Shrink = 0f, Justify = FlexJustify.Center,
                            Children = [Art(20f), Bar(30f, 6f), Bar(22f, 4f), SmallPill()],
                        },
                        new BoxEl
                        {
                            Direction = 1, Gap = 5f, Grow = 1f, Justify = FlexJustify.Center,
                            Children = [RowBar(), RowBar(), RowBar(), RowBar()],
                        },
                    ],
                }
                // Hero: an immersive artwork field and compact identity above the track rows.
                : new BoxEl
                {
                    Direction = 1, Gap = 5f, Grow = 1f, Justify = FlexJustify.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Gap = 4f, AlignItems = FlexAlign.Stretch,
                            Children =
                            [
                                new BoxEl
                                {
                                    Height = 24f, AlignSelf = FlexAlign.Stretch,
                                    Corners = CornerRadius4.All(4f), Fill = ink.Block,
                                },
                                new BoxEl
                                {
                                    Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center,
                                    Children = [Bar(48f, 6f), Bar(28f, 4f), Pills()],
                                },
                            ],
                        },
                        RowBar(), RowBar(), RowBar(),
                    ],
                };

            return WaveePicker.Titled(WaveePicker.Card(on, WaveePicker.Tile, sketch), labels[value], on);
        }

        return WaveePicker.Strip(labels.Length, selected, Card, set);
    }

    // ── the Sidebar group (§C6.3) ─────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The sidebar design group: the shared three-card design picker in the expander BODY, the active design's
    /// name in its header, and — only while Wavee Curated is the active design — the "Customize sidebar" link row.
    ///
    /// <para>ONE shape for all three designs. It used to return a bare <c>SettingsRow</c> for Classic/Library and a
    /// <c>SettingsExpander</c> for Curated: two different element types at the same child slot with no Key, so a design
    /// switch remounted the whole card and the section's silhouette changed under the user. Worse, the Curated arm put
    /// the 624-DIP picker in the expander's HEADER, which starved the header text track to zero and painted "Sidebar
    /// design" straight across the cards.</para>
    ///
    /// <para>NESTED inside <see cref="SettingsPage"/> so it can use the page's own <c>SettingsRow</c>/<c>SettingsItem</c>
    /// helpers (a sibling class could not), and a <see cref="Component"/> so it can take
    /// <see cref="SidebarPreferences"/>, <see cref="Services"/> and the nav action from CONTEXT rather than from frozen
    /// props — AppearanceTab is not a render body of its own, so it cannot hold the hooks these need.</para>
    ///
    /// <para>No page-epoch <c>Bump()</c> is involved: the card subscribes to <c>prefs.Design</c> directly, so a switch
    /// made from the sidebar's own layout menu while this page is open re-renders the cards, re-labels the header AND
    /// appears/disappears the link row live. Ctor-arg-free, so the frozen-props contract is trivially satisfied.</para></summary>
    sealed class SidebarSettingsCard : Component
    {
        public override Element Render()
        {
            var prefs = UseContext(SidebarPreferences.Slot);
            var svc = UseContext(Services.Slot);
            var go = UseContext(HistoryStore.NavCtx);
            var settings = svc?.Settings;

            // The LIVE design (a subscription when the service is present; the persisted value when the page is mounted
            // in isolation without one). Both paths coerce through the same table, so a hand-edited value cannot make the
            // picker show nothing selected.
            var design = prefs is not null
                ? prefs.Design.Value
                : SidebarDesignGating.ActiveDesign(settings);

            // Compact cards: this row shares a page column with the header/description block, and the compact
            // ladder keeps all three visible on a narrow window before the row has to wrap.
            Element picker = SidebarDesignPicker.Row(prefs, settings, compact: true);

            // The customizer edits the Curated document, so offering it for Classic/Library would navigate to an editor
            // for something the user is not looking at. The quick layout menu's "Customize sidebar…" row is the path
            // that switches first — this one never switches silently.
            Element[] items = SidebarDesignGating.CanCustomize(design)
                ?
                [
                    SettingsItem(Loc.Get(Strings.Settings.Sidebar.Customize),
                        Loc.Get(Strings.Settings.Sidebar.CustomizeSub), control: null,
                        isClickEnabled: true, onClick: () => go(SidebarLayoutMenu.CustomizeRoute, null),
                        icon: SettingsGlyphs.Row(SettingsTab.Appearance, "sidebarCustomize")),
                ]
                : [];

            return SettingsExpander.Create(new SettingsExpander.Options
            {
                // "Design", not "Sidebar design" — the section eyebrow above already says Sidebar.
                Header = Loc.Get(Strings.Settings.Sidebar.DesignShort),
                Description = Loc.Get(Strings.Settings.Sidebar.DesignSub),
                HeaderIcon = SettingsGlyphs.Row(SettingsTab.Appearance, "sidebarDesign"),
                Content = SettingsValueTag(Loc.Get(SidebarDesignGating.TitleKey(design))),
                ItemsHeader = SettingsExpanderPanel(picker),
                Items = items,
            }) with { Key = "appearance.sidebar.design" };
        }
    }
}
