using System;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
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
// Alt (always base Mica), and the "Track page layout" group (Automatic/Hero + "Limit page color to the hero" — detail
// pages are always Automatic). "Disable marquee text" / "Disable color washes" are inverted to two flat ON switches,
// "Marquee text" / "Color washes" — no wrapping "Visual effects" expander, no "N disabled" tag: two rows, two answers.
sealed partial class SettingsPage
{
    readonly Signal<int> _density = new(1);

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
        int lyricsSecondary = Math.Clamp(settings?.Get(WaveeSettings.LyricsSecondaryLine) ?? 0, 0, LyricsSecondaryLabels().Length - 1);
        // The LIVE zoom, not the stored key: the chords/wheel change FluentApp.Zoom ahead of the debounced persist,
        // and the row must show what the window is doing right now. Snap → IndexOf is exact (Snap returns a Steps
        // element); Max is belt-and-suspenders for an engine value the ladder somehow doesn't contain.
        int zoomIndex = Math.Max(0, Array.IndexOf(ZoomLadder.Steps, ZoomLadder.Snap(FluentApp.Zoom)));

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
        // spin of repeats.
        void SetZoom(int i)
        {
            if (settings is null || (uint)i >= (uint)ZoomLadder.Steps.Length) return;
            float z = ZoomLadder.Steps[i];
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
            // A ComboBox, not a SelectorBar: twelve ladder steps would be twelve segments wide. Same chords as a
            // browser (Ctrl+± / Ctrl+0 / Ctrl+wheel) — the row is the discoverable face of the same ladder.
            SettingsRow(Loc.Get(Strings.Settings.Appearance.Zoom), Loc.Get(Strings.Settings.Appearance.ZoomSub),
                ComboBox.Create(ZoomLabels, new Signal<int>(zoomIndex), width: 140f, isEnabled: settings is not null,
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
