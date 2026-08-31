using System;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The General tab. A MANIFEST, not a wall: six eyebrow'd groups, and every group either a one-line row or a NAMED
// builder below (the shape SettingsPage.Playback.cs already uses). Two rules earn their keep here:
//
//  1. A PICKER GOES IN AN EXPANDER BODY, NEVER AN EXPANDER HEADER. The header content slot lands in a SettingsCard's
//     right-hand Auto grid track, which starves the header text track toward zero once the content is wider than the
//     card — and a zero-width text run neither wraps nor clips, so the header paints straight over the content. That
//     was the Sidebar group's overlapping "Sidebar design" bug. The header carries the ANSWER ("Default", "Custom") via
//     SettingsValueTag; the cards live in ItemsHeader.
//  2. COLLAPSED BY DEFAULT. Row density, track page layout and sidebar design stacked ~600 DIP of always-visible
//     wireframes between Theme and Language. Collapsed, each still says what it is set to, and the page fits.
sealed partial class SettingsPage
{
    readonly Signal<int> _density = new(1);
    readonly Signal<int> _language = new(0);

    // Enabled is a parallel mask: nl / ko-KR are shown but DISABLED (greyed, unselectable) — their tables aren't
    // complete enough to ship as a pick yet. Keep them visible so the row advertises what's coming; flip to true
    // per locale as each table lands. System + en-US stay enabled.
    static (string[] Codes, string[] Labels, bool[] Enabled) LanguageOptions()
    {
        return (
            ["system", "en-US", "nl", "ko-KR"],
            [
                Loc.Get(Strings.Settings.Language.System),
                Loc.Get(Strings.Settings.Language.EnglishUs),
                Loc.Get(Strings.Settings.Language.Dutch),
                Loc.Get(Strings.Settings.Language.Korean),
            ],
            [true, true, false, false]);
    }

    static int LanguageIndex(string culture)
    {
        var (codes, _, _) = LanguageOptions();
        for (int i = 0; i < codes.Length; i++)
            if (string.Equals(codes[i], culture, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

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

    // NOTE the index is the INVERSE of the stored value here, unlike every other picker in this file (Theme/Density/
    // PageLayout all have index == stored value). Index 0 = base Mica = WindowMaterialBaseMica TRUE; index 1 = Mica Alt
    // = WindowMaterialBaseMica FALSE. The picker orders base Mica first because it is the default; the setting stores
    // "is it base Mica" rather than "is it the alt", so the two run backwards from each other.
    static string[] WindowMaterialLabels() =>
    [
        Loc.Get(Strings.Settings.Appearance.MaterialMica),
        Loc.Get(Strings.Settings.Appearance.MaterialMicaAlt),
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

    // The scheme association is applied AT THE TOGGLE, not at next launch: a user who turns this on expects the very
    // next spotify: link to open here, and one who turns it off expects the scheme handed straight back.
    Element SpotifyLinksToggle(IAppSettings? settings)
        => ToggleSwitch.Create(new Signal<bool>(settings?.Get(WaveeSettings.HandleSpotifyLinks) ?? false), onChange: _ =>
        {
            if (settings is null) return;
            bool next = !settings.Get(WaveeSettings.HandleSpotifyLinks);
            settings.Set(WaveeSettings.HandleSpotifyLinks, next);
            DeepLink.SyncSpotifySchemeRegistration(next);
            Bump();
        }, style: SettingsCard.CompactToggleStyle());

    Element GeneralTab(Services? svc, Action<float>? requestTheme)
    {
        var settings = svc?.Settings;
        int themeMode = settings?.Get(WaveeSettings.ThemeMode) ?? 0;
        int density = Math.Clamp(_density.Value, 0, DensityLabels().Length - 1);
        int trackListStyle = Math.Clamp(settings?.Get(WaveeSettings.TrackRowStyle) ?? 0, 0, TrackListStyleLabels().Length - 1);
        int pageLayout = Math.Clamp(settings?.Get(WaveeSettings.DetailPageLayout) ?? 0, 0, PageLayoutLabels().Length - 1);
        int lyricsSecondary = Math.Clamp(settings?.Get(WaveeSettings.LyricsSecondaryLine) ?? 0, 0, LyricsSecondaryLabels().Length - 1);
        int windowMaterial = (settings?.Get(WaveeSettings.WindowMaterialBaseMica) ?? true) ? 0 : 1;
        // The LIVE zoom, not the stored key: the chords/wheel change FluentApp.Zoom ahead of the debounced persist,
        // and the row must show what the window is doing right now. Snap → IndexOf is exact (Snap returns a Steps
        // element); Max is belt-and-suspenders for an engine value the ladder somehow doesn't contain.
        int zoomIndex = Math.Max(0, Array.IndexOf(ZoomLadder.Steps, ZoomLadder.Snap(FluentApp.Zoom)));
        var languageOptions = LanguageOptions();

        void SetTheme(int mode)
        {
            WaveeTheme.ApplyThemeMode(mode, settings);
            requestTheme?.Invoke(250f);
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

        // Not an AppearanceToggle: this flips a DWM window attribute, not an AppearancePrefs.Epoch-gated render choice —
        // write the setting, then do the side effect (the SpotifyLinksToggle shape).
        void SetWindowMaterial(int i)
        {
            if (settings is null) return;
            bool baseMica = i == 0;
            settings.Set(WaveeSettings.WindowMaterialBaseMica, baseMica);
            FluentApp.SetWindowMaterialAlt(!baseMica);   // live, no restart
            Bump();
        }

        // The SetWindowMaterial shape (write the setting, then the live side effect — here inverted: apply live, then
        // write): FluentApp.SetZoom re-derives the effective window scale at once, and the picker writes the setting
        // IMMEDIATELY rather than waiting for WaveeApp's debounced zoom-save timer — a deliberate pick from a
        // deliberate control, unlike the chords/wheel where the debounce exists to absorb a spin of repeats.
        void SetZoom(int i)
        {
            if (settings is null || (uint)i >= (uint)ZoomLadder.Steps.Length) return;
            float z = ZoomLadder.Steps[i];
            FluentApp.SetZoom(z);   // live, no restart
            settings.Set(WaveeSettings.ZoomLevel, z);
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

        void SetLanguage(int i)
        {
            if (settings is null || (uint)i >= (uint)languageOptions.Codes.Length) return;
            if (!languageOptions.Enabled[i]) return;   // belt-and-suspenders: the ComboBox already rejects a disabled pick
            settings.Set(WaveeSettings.UiCulture, languageOptions.Codes[i]);
            _language.Value = i;
            Bump();
        }

        return SettingsTabStack(
            SettingsSectionHeader(Loc.Get(Strings.Settings.Appearance.Title), Icons.Brush,
                Loc.Get(Strings.Settings.Appearance.Subtitle)),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.Theme), Loc.Get(Strings.Settings.Appearance.ThemeSub),
                SelectorBar.Create(ThemeLabels(), new Signal<int>(themeMode), onChange: SetTheme), Icons.Brush),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.Palette), Loc.Get(Strings.Settings.Appearance.PaletteSub),
                PaletteRow(settings, requestTheme), Icons.Brush),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.WindowMaterial), Loc.Get(Strings.Settings.Appearance.WindowMaterialSub),
                SelectorBar.Create(WindowMaterialLabels(), new Signal<int>(windowMaterial), onChange: SetWindowMaterial), Icons.Brush),
            // A ComboBox, not a SelectorBar: twelve ladder steps would be twelve segments wide. Same chords as a
            // browser (Ctrl+± / Ctrl+0 / Ctrl+wheel) — the row is the discoverable face of the same ladder.
            SettingsRow(Loc.Get(Strings.Settings.Appearance.Zoom), Loc.Get(Strings.Settings.Appearance.ZoomSub),
                ComboBox.Create(ZoomLabels, new Signal<int>(zoomIndex), width: 140f, isEnabled: settings is not null,
                    onChange: SetZoom), Icons.Brush),
            VisualEffectsGroup(settings),

            // Layout & density: the two picker groups that decide how a track page is put together. Both collapsed —
            // their headers report the current answer, and their wireframes only have to exist while choosing.
            SettingsSectionHeader(Loc.Get(Strings.Settings.Layout.Title), Icons.List,
                Loc.Get(Strings.Settings.Layout.Subtitle)),
            DensityGroup(density, SetDensity, settings),
            TrackListStyleGroup(trackListStyle, SetTrackListStyle),
            PageLayoutGroup(pageLayout, SetPageLayout, settings),

            // The sidebar design. A Component rather than an inline block: the card needs SidebarPreferences and the
            // nav action from CONTEXT, and GeneralTab runs only while the General tab is selected, so a hook added here
            // would be a conditional hook (it would vanish from the page's hook order the moment another tab renders).
            SettingsSectionHeader(Loc.Get(Strings.Settings.Sidebar.Title), Icons.SplitView,
                Loc.Get(Strings.Settings.Sidebar.Subtitle)),
            Embed.Comp(() => new SidebarSettingsCard()),

            // The lyrics reading surface owns its own group: the second line and the cover drift are choices about the
            // same screen, and neither is "appearance" in the shell-wide sense the group above means.
            SettingsSectionHeader(Loc.Get(Strings.Settings.Lyrics.Title), Icons.Font,
                Loc.Get(Strings.Settings.Lyrics.Subtitle)),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.LyricsSecondary), Loc.Get(Strings.Settings.Appearance.LyricsSecondarySub),
                SelectorBar.Create(LyricsSecondaryLabels(), new Signal<int>(lyricsSecondary), onChange: SetLyricsSecondary),
                Icons.Globe),
            // A plain AppearanceToggle: its Bump() raises AppearancePrefs.Epoch, which ImmersiveLyricsSurface reads, so
            // flipping it starts/stops the drift on an OPEN surface — no restart.
            SettingsRow(Loc.Get(Strings.Settings.Appearance.LyricsBackdrop), Loc.Get(Strings.Settings.Appearance.LyricsBackdropSub),
                AppearanceToggle(settings, WaveeSettings.LyricsAnimatedBackdrop), Icons.Brush),

            SettingsSectionHeader(Loc.Get(Strings.Settings.Language.Title), Icons.Globe,
                Loc.Get(Strings.Settings.Language.Subtitle)),
            SettingsRow(Loc.Get(Strings.Settings.Language.Label), Loc.Get(Strings.Settings.Language.RestartSub),
                ComboBox.Create(languageOptions.Labels, _language, width: 260f, isEnabled: settings is not null,
                    onChange: SetLanguage, itemEnabled: languageOptions.Enabled), Icons.Globe),

            SettingsSectionHeader(Loc.Get(Strings.Settings.Links.Title), Icons.Link,
                Loc.Get(Strings.Settings.Links.Subtitle)),
            SettingsRow(Loc.Get(Strings.Settings.Links.Spotify), Loc.Get(Strings.Settings.Links.SpotifySub),
                SpotifyLinksToggle(settings), Icons.Link),

            // The setup wizard's manual re-run entry point. This row never touches IOverlayService/SetupDialog
            // itself: it only bumps SetupSession.OpenRequest, and SetupChrome (Features/Setup/SetupChrome.cs,
            // mounted inside WaveeShell's shellWithOverlays ZStack — the component that owns the session's
            // lifetime) is the one place that reacts, building a fresh EntryPoint.Rerun session
            // (alreadyAuthenticated: true — reaching this row means the user is already signed in, so SkipSignIn
            // skips the SignIn page) and opening the dialog.
            SettingsRow(Loc.Get(Strings.Setup.RunAgain), null, isClickEnabled: true, onClick: () => SetupSession.Bump()));
    }

    // ── Appearance → Visual effects ───────────────────────────────────────────────────────────────────────────────────
    /// <summary>The two subtractive appearance switches, grouped and collapsed. Both are "turn a flourish OFF", both are
    /// rarely touched, and neither needs to sit between Theme and the layout pickers competing for the same glance —
    /// but the header has to say whether any of them is off, or the group hides its own state.</summary>
    Element VisualEffectsGroup(IAppSettings? settings)
    {
        int off = (settings?.Get(WaveeSettings.DisableMarquee) ?? false ? 1 : 0)
                + (settings?.Get(WaveeSettings.DisableColorWashes) ?? false ? 1 : 0);

        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Appearance.EffectsTitle),
            Description = Loc.Get(Strings.Settings.Appearance.EffectsSub),
            HeaderIcon = Icons.Brush,
            Content = SettingsValueTag(off == 0
                ? Loc.Get(Strings.Settings.Appearance.EffectsAllOn)
                : Strings.Settings.Appearance.EffectsDisabled(off)),
            Items =
            [
                SettingsItem(Loc.Get(Strings.Settings.Appearance.DisableMarquee),
                    Loc.Get(Strings.Settings.Appearance.DisableMarqueeSub),
                    AppearanceToggle(settings, WaveeSettings.DisableMarquee), icon: Icons.Font),
                SettingsItem(Loc.Get(Strings.Settings.Appearance.DisableColorWashes),
                    Loc.Get(Strings.Settings.Appearance.DisableColorWashesSub),
                    AppearanceToggle(settings, WaveeSettings.DisableColorWashes), icon: Icons.Brush),
            ],
        }) with { Key = "general.effects" };
    }

    // ── Layout & density → Row density ────────────────────────────────────────────────────────────────────────────────
    /// <summary>The density picker, collapsed behind its own answer. <c>ItemsHeader</c> rather than an <c>Items</c> row:
    /// a wireframe strip is not a settings row, and an empty-header <c>SettingsCard</c> would reserve a phantom label
    /// column beside it.</summary>
    Element DensityGroup(int density, Action<int> setDensity, IAppSettings? settings)
        => SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Appearance.RowDensity),
            Description = Loc.Get(Strings.Settings.Appearance.RowDensitySub),
            HeaderIcon = Icons.List,
            Content = SettingsValueTag(DensityLabels()[density]),
            ItemsHeader = SettingsExpanderPanel(DensityCards(density, setDensity)),
            Items =
            [
                SettingsItem(Loc.Get(Strings.Settings.Appearance.HideTrackArtwork),
                    Loc.Get(Strings.Settings.Appearance.HideTrackArtworkSub),
                    TrackArtworkCheckBox(settings), icon: Icons.MusicNote),
            ],
        }) with { Key = "general.density" };

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

    // ── Layout & density → Track list style ──────────────────────────────────────────────────────────────────────────
    Element TrackListStyleGroup(int style, Action<int> setStyle)
        => SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Appearance.TrackListStyle),
            Description = Loc.Get(Strings.Settings.Appearance.TrackListStyleSub),
            HeaderIcon = Icons.List,
            Content = SettingsValueTag(TrackListStyleLabels()[style]),
            ItemsHeader = SettingsExpanderPanel(TrackListStyleCards(style, setStyle)),
        }) with { Key = "general.track-list-style" };

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

    // ── Layout & density → Track page layout ──────────────────────────────────────────────────────────────────────────
    /// <summary>The page-layout picker plus the one other choice about the same surface — how far that page's
    /// art-derived TONE reaches. Grouped because they are the same kind of decision about the same screen; the tone
    /// toggle's <c>Bump()</c> raises <c>AppearancePrefs.Epoch</c>, which every mounted DetailShell already reads, so
    /// flipping it re-solves an open page's ground with no restart.</summary>
    Element PageLayoutGroup(int pageLayout, Action<int> setPageLayout, IAppSettings? settings)
        => SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Appearance.PageLayout),
            Description = Loc.Get(Strings.Settings.Appearance.PageLayoutSub),
            HeaderIcon = Icons.List,
            Content = SettingsValueTag(PageLayoutLabels()[pageLayout]),
            ItemsHeader = SettingsExpanderPanel(PageLayoutCards(pageLayout, setPageLayout)),
            Items =
            [
                SettingsItem(Loc.Get(Strings.Settings.Appearance.PageTone),
                    Loc.Get(Strings.Settings.Appearance.PageToneSub),
                    AppearanceToggle(settings, WaveeSettings.DetailPageToneHeroOnly), icon: Icons.Brush),
            ],
        }) with { Key = "general.pagelayout" };

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

    // ── Appearance → Palette ──────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The palette swatches. A <see cref="WaveePicker.Strip"/> like the wireframe pickers — so it gets the same
    /// one-tab-stop, arrow-roving group contract — but a 30-DIP circle is NOT a preview card, so it keeps its own visual
    /// rather than being forced through <see cref="WaveePicker.Card"/>.</summary>
    Element PaletteRow(IAppSettings? settings, Action<float>? requestTheme)
    {
        string activeId = Tok.Palette.Id;
        int active = Array.IndexOf(AppearanceStageModel.PaletteIds, activeId);
        if (active < 0) active = 0;

        BoxEl Swatch(string label, ColorF fill, bool on) => WaveePicker.Titled(
            new BoxEl
            {
                Width = 30f, Height = 30f, Corners = CornerRadius4.All(15f), Fill = fill,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Cursor = CursorId.Hand,
                BorderWidth = on ? 2f : 1f,
                BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
                Children = on
                    ? [new TextEl(Icons.Accept) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextOnAccentPrimary }]
                    : [],
            },
            label, on, gap: 5f) with { Width = 56f };

        // Ordered to match AppearanceStageModel.PaletteIds — the writer below indexes the same list, so a swatch cannot show one palette
        // and persist another.
        Element Card(int i, bool on) => i switch
        {
            1 => Swatch(Loc.Get(Strings.Settings.Appearance.PaletteSlate), WaveeColors.PresetSwatch(Tok.SlatePalette), on),
            2 => Swatch(Loc.Get(Strings.Settings.Appearance.PaletteNeutral), WaveeColors.PresetSwatch(Tok.NeutralPalette), on),
            3 => Swatch(Loc.Get(Strings.Settings.Appearance.PaletteAccent), WaveeColors.PresetSwatch(Tok.AccentTintedPalette), on),
            _ => Swatch(Loc.Get(Strings.Settings.Appearance.PaletteWarm), WaveeColors.PresetSwatch(Tok.WarmPalette), on),
        };

        return WaveePicker.Strip(AppearanceStageModel.PaletteIds.Length, active, Card, i =>
        {
            if ((uint)i >= (uint)AppearanceStageModel.PaletteIds.Length) return;
            WaveeTheme.ApplyPalette(AppearanceStageModel.PaletteIds[i], settings);
            requestTheme?.Invoke(250f);
            Bump();
        });
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
    /// props — GeneralTab is not a render body of its own, so it cannot hold the hooks these need.</para>
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
                        icon: Icons.Edit),
                ]
                : [];

            return SettingsExpander.Create(new SettingsExpander.Options
            {
                // "Design", not "Sidebar design" — the section eyebrow above already says Sidebar.
                Header = Loc.Get(Strings.Settings.Sidebar.DesignShort),
                Description = Loc.Get(Strings.Settings.Sidebar.DesignSub),
                HeaderIcon = Icons.SplitView,
                Content = SettingsValueTag(Loc.Get(SidebarDesignGating.TitleKey(design))),
                ItemsHeader = SettingsExpanderPanel(picker),
                Items = items,
            }) with { Key = "general.sidebar.design" };
        }
    }
}
