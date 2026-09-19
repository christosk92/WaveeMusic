// ── Screens/Settings.UI.Appearance.cs ──────────────────────────────────────────────────────────────────────────────
// the Appearance tab: Theme (theme, zoom, marquee, colour washes) · Lists (the three collapsed picker groups: row density
// + hide artwork, track list style, track page layout + the two rail rows) · Sidebar (the design picker's three compact
// cards + "Customize sidebar") · Lyrics (second line, animated backdrop, blur) · Now playing (hero, player style)
//
// Role: UI
// Owner: R
// Wave: 6
// Budget: 700 lines
// Spec: DERIVED (ch 27 §9.3 / ch 30 §9.4); ch 27 §0 N3-N6, §1.2 props-freeze map, W2-W4, W29, §3, §4.2, §5, §6, §10
// items 12-20; ch 30 §1.1 (which epoch each write bumps) + N12 (the row-density write reaches mounted pages); ch 25 W21
// (the design cards — no 0.3 `SidebarDesignPicker` exists, so the compact cards are built here on owner L's picker)
//
// EVERY ROW WRITES THROUGH ITS OWN EPOCH (ch 27 W2 "three facts"): marquee / washes / animated backdrop / hide artwork /
// row density / track list style → `Prefs.Appearance`; page layout, rail uniform, rail reset → `Prefs.DetailHero`;
// lyrics second line and blur → `Prefs.Lyrics`; the two Now-playing rows → `Prefs.NpvPlayer` with NO page `Bump()` (the
// tab reads that epoch). The tab body reads the store directly (never a foreign epoch it does not need), so a write
// re-renders this page once, through `Bump()`.
//
// FROZEN CONTROL INPUTS. A ComboBox freezes its items, its signal AND its onChange at mount: the zoom and player-style
// combos are therefore tiny child components that keep one stable signal in step through an effect, and every handler
// reads the store at invocation, never a render local. SelectorBar / Slider / CheckBox re-push their props.

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Settings
{
    // ══ 1. STABLE CONTROL STATE (0.2.9's `_lyricsBlurSlider` instance field + the two combo selections) ═════════════

    /// <summary>The lyrics-blur slider's value (the resolved 0..100). A slider binds ONE signal for its life.</summary>
    static readonly FloatSignal s_lyricsBlurSlider = new(Lyrics.BlurPolicy.StrongGpuDefault);
    /// <summary>The zoom combo's selection, kept in step with the LIVE zoom by <see cref="ZoomPicker"/>.</summary>
    static readonly Signal<int> s_zoomIndex = new(0);
    /// <summary>The player-style combo's selection, kept in step with every other style writer by <see cref="NpvStylePicker"/>.</summary>
    static readonly Signal<int> s_npvStyle = new(Rail.PlayerCatalog.DefaultPresetId);

    static readonly Slider.SliderOptions s_lyricsBlurOptions = new()
    {
        Min = 0f, Max = 100f, Step = 1f, TickFrequency = 25f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%",
    };

    /// <summary>The ladder rungs as labels, hoisted once — digits and '%' never change with culture.</summary>
    static readonly string[] s_zoomRungLabels = BuildZoomRungLabels();

    static partial void SeedAppearance()
        => s_lyricsBlurSlider.Value = Lyrics.BlurPolicy.Resolve(Platform.Settings.Get(Platform.Keys.LyricsBlurStrength), GpuProfile.IsWeak);

    // ══ 2. THE TAB ════════════════════════════════════════════════════════════════════════════════════════════════════

    private static partial Element AppearanceTab()
    {
        int themeMode = Math.Clamp(Platform.Settings.Get(Platform.Keys.ThemeMode), 0, 2);
        int lyricsSecondary = Prefs.Lyrics.ClampMode(Platform.Settings.Get(Platform.Keys.LyricsSecondaryLine));
        bool lyricsBlurAuto = Platform.Settings.Get(Platform.Keys.LyricsBlurStrength) < 0;
        // The Now-playing rows' ONE foreign epoch (ch 27 §7): the header row, the flyout, the art menu and the palette all
        // write it, and these two rows must follow them live.
        int npvPresentation = Rail.PlayerPrefs.Presentation();
        var npvPreset = Rail.PlayerPrefs.CurrentPreset();
        int pageMotion = Prefs.Appearance.PageMotionStyle(Design.PageMotionStyleCount);

        return TabStack(
            SectionHeader(Loc.Get(Strings.Settings.Appearance.Title), SectionGlyph(Tab.Appearance, "Theme"),
                Loc.Get(Strings.Settings.Appearance.Subtitle)),
            Row(Loc.Get(Strings.Settings.Appearance.Theme), Loc.Get(Strings.Settings.Appearance.ThemeSub),
                SelectorBar.Create(ThemeLabels(), new Signal<int>(themeMode), onChange: static m => SetTheme(m)),
                RowGlyph(Tab.Appearance, "theme")),
            // A ComboBox, not a SelectorBar: thirteen items (Auto + twelve rungs) would be thirteen segments wide.
            Row(Loc.Get(Strings.Settings.Appearance.Zoom), Loc.Get(Strings.Settings.Appearance.ZoomSub),
                Embed.Comp(static () => new ZoomPicker()), RowGlyph(Tab.Appearance, "zoom")),
            Row(Loc.Get(Strings.Settings.Appearance.Marquee), Loc.Get(Strings.Settings.Appearance.MarqueeSub),
                AppearanceToggle(Platform.Keys.MarqueeEnabled), RowGlyph(Tab.Appearance, "marquee")),
            Row(Loc.Get(Strings.Settings.Appearance.ColorWashes), Loc.Get(Strings.Settings.Appearance.ColorWashesSub),
                AppearanceToggle(Platform.Keys.ColorWashesEnabled), RowGlyph(Tab.Appearance, "colorWashes")),
            // A SelectorBar, not a ComboBox: it re-pushes its selected index every render (ch 27 §0's frozen-inputs
            // rule), so the row needs no dedicated stable-signal component the way Zoom/NPV-style do.
            Row(Loc.Get(Strings.Settings.Appearance.PageMotion), Loc.Get(Strings.Settings.Appearance.PageMotionSub),
                SelectorBar.Create(PageMotionLabels(), new Signal<int>(pageMotion), onChange: static i => SetPageMotionStyle(i)),
                RowGlyph(Tab.Appearance, "pageMotion")),

            SectionHeader(Loc.Get(Strings.Settings.Layout.Title), SectionGlyph(Tab.Appearance, "Lists"),
                Loc.Get(Strings.Settings.Layout.Subtitle)),
            DensityGroup(),
            TrackListStyleGroup(),
            PageLayoutGroup(),

            SectionHeader(Loc.Get(Strings.Settings.Sidebar.Title), SectionGlyph(Tab.Appearance, "Sidebar"),
                Loc.Get(Strings.Settings.Sidebar.Subtitle)),
            Embed.Comp(static () => new SidebarDesignCard()),

            SectionHeader(Loc.Get(Strings.Settings.Lyrics.Title), SectionGlyph(Tab.Appearance, "Lyrics"),
                Loc.Get(Strings.Settings.Lyrics.Subtitle)),
            Row(Loc.Get(Strings.Settings.Appearance.LyricsSecondary), Loc.Get(Strings.Settings.Appearance.LyricsSecondarySub),
                SelectorBar.Create(LyricsSecondaryLabels(), new Signal<int>(lyricsSecondary), onChange: static i =>
                {
                    Prefs.Lyrics.SetSecondaryLine(i);   // persists the clamped mode + bumps the lyrics epoch once
                    Bump();
                }),
                RowGlyph(Tab.Appearance, "lyricsSecondary")),
            // Prefs.Lyrics.AnimatedBackdrop reads BOTH epochs, so the appearance bump reaches an open immersive stage.
            Row(Loc.Get(Strings.Settings.Appearance.LyricsBackdrop), Loc.Get(Strings.Settings.Appearance.LyricsBackdropSub),
                AppearanceToggle(Platform.Keys.LyricsAnimatedBackdrop), RowGlyph(Tab.Appearance, "lyricsBackdrop")),
            Row(Loc.Get(Strings.Settings.Appearance.LyricsBlur), Loc.Get(Strings.Settings.Appearance.LyricsBlurSub),
                LyricsBlurControl(lyricsBlurAuto), RowGlyph(Tab.Appearance, "lyricsBlur")),

            SectionHeader(Loc.Get(Strings.Settings.NowPlaying.Title), SectionGlyph(Tab.Appearance, "Now playing"),
                Loc.Get(Strings.Settings.NowPlaying.Subtitle)),
            // The second label FOLLOWS the current style; SelectorBar re-pushes its items, so it relabels in place.
            Row(Loc.Get(Strings.Settings.Appearance.NpvPresentation), Loc.Get(Strings.Settings.Appearance.NpvPresentationSub),
                SelectorBar.Create([Loc.Get(Strings.Player.PresentationCover), Loc.Get(npvPreset.ShortLabelKey)],
                    new Signal<int>(npvPresentation),
                    onChange: static i => Rail.PlayerPrefs.SetPresentation(i, Rail.NpvDiagnostics.SourceSettings)),
                RowGlyph(Tab.Appearance, "npvPresentation")),
            Row(Loc.Get(Strings.Settings.Appearance.NpvStyle), Loc.Get(Strings.Settings.Appearance.NpvStyleSub),
                Embed.Comp(static () => new NpvStylePicker()), RowGlyph(Tab.Appearance, "npvStyle")));
    }

    // ══ 3. THEME + ZOOM ═══════════════════════════════════════════════════════════════════════════════════════════════

    static string[] ThemeLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.System),
        Loc.Get(Strings.Settings.Choice.Light),
        Loc.Get(Strings.Settings.Choice.Dark),
    ];

    /// <summary>0 System · 1 Light · 2 Dark: swap the installed Wavee palette IN PLACE (System re-reads the OS theme and
    /// accent now), persist, then ask the host for the 250 ms re-theme cross-fade (ch 27 §5).</summary>
    static void SetTheme(int mode)
    {
        mode = Math.Clamp(mode, 0, 2);
        var kind = mode switch
        {
            1 => ThemeKind.Light,
            2 => ThemeKind.Dark,
            _ => FluentApp.SystemUsesLightTheme() ? ThemeKind.Light : ThemeKind.Dark,
        };
        if (Shell.SeedPalette is { } seed) seed(kind); else Tok.Use(kind);
        if (mode == 0)
        {
            if (FluentApp.SystemAccentRamp() is { } ramp) Tok.SetAccent(in ramp);
            else if (FluentApp.SystemAccent() is { } accent) Tok.SetAccent(accent);
        }
        Platform.Settings.Set(Platform.Keys.ThemeMode, mode);
        s_requestTheme?.Invoke(Design.Motion.Standard);
        Bump();
    }

    /// <summary>Ordered to match the stored value (<see cref="Design.PageMotionStyle"/>'s declaration order) — the
    /// index IS the store, same contract as <see cref="LyricsSecondaryLabels"/>.</summary>
    static string[] PageMotionLabels() =>
    [
        Loc.Get(Strings.Settings.Appearance.PageMotionFluent),
        Loc.Get(Strings.Settings.Appearance.PageMotionSpatial),
        Loc.Get(Strings.Settings.Appearance.PageMotionWinUi),
        Loc.Get(Strings.Settings.Appearance.PageMotionClassic),
        Loc.Get(Strings.Settings.Appearance.PageMotionNone),
    ];

    /// <summary>Persist the page-motion style through the ONE Appearance writer (bumps the epoch once), then bump the
    /// page. Every mounted content host re-reads it on its NEXT swap — no restart, no re-navigation required to try a
    /// style, same live-apply contract as row density (ch 30 N12).</summary>
    static void SetPageMotionStyle(int i)
    {
        if ((uint)i >= (uint)Design.PageMotionStyleCount || Platform.Settings.Get(Platform.Keys.PageMotionStyle) == i) return;
        Prefs.Appearance.Set(Platform.Keys.PageMotionStyle, i);
        Bump();
    }

    static string[] BuildZoomRungLabels()
    {
        var labels = new string[ZoomLadder.Steps.Length];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = ZoomLadder.Percent(ZoomLadder.Steps[i]).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
        return labels;
    }

    /// <summary>The Zoom row's combo. Index 0 is "Auto (N%)" with the LIVE resolved percent and is the selection for BOTH
    /// Auto and Dense; only Manual selects a rung (ch 27 W2). Its own component because it needs the live zoom, the
    /// viewport and one effect: a chord or the Ctrl+wheel moves the selection with no settings write.</summary>
    sealed class ZoomPicker : Component
    {
        float _auto = 1f;
        int _labelsPercent = -1;
        string[] _labels = [];

        public override Element Render()
        {
            float zoom = UseContext(Viewport.Zoom);
            var viewport = UseContext(Viewport.Size);
            // baseDip = the live (already zoomed) viewport × zoom — ZoomAutoPolicy takes BASE dips, never live ones.
            _auto = ZoomAutoPolicy.Suggest(viewport.Width * zoom, viewport.Height * zoom, ZoomAutoMode.Auto);
            var mode = (ZoomAutoMode)Math.Clamp(Platform.Settings.Get(Platform.Keys.ZoomMode), 0, 2);
            int index = mode == ZoomAutoMode.Manual
                ? 1 + Math.Max(0, Array.IndexOf(ZoomLadder.Steps, ZoomLadder.Snap(zoom)))
                : 0;
            UseEffect(() => s_zoomIndex.Value = index, DepKey.From(index));

            int percent = ZoomLadder.Percent(_auto);
            if (percent != _labelsPercent)
            {
                _labelsPercent = percent;
                _labels = new string[s_zoomRungLabels.Length + 1];
                _labels[0] = Strings.Settings.Appearance.ZoomAuto(percent);
                Array.Copy(s_zoomRungLabels, 0, _labels, 1, s_zoomRungLabels.Length);
            }
            // The combo's ITEMS freeze at mount: the head label changes only on a plateau change, so re-key on that.
            return ComboBox.Create(_labels, s_zoomIndex, width: 160f, onChange: Pick) with { Key = "appearance.zoom:" + percent };
        }

        /// <summary>A deliberate pick writes at once (the chords' debounce exists for key repeats). The head item writes
        /// mode = Auto — never Dense — and applies THIS render's suggestion; a rung writes Manual.</summary>
        void Pick(int i)
        {
            if (i <= 0)
            {
                Platform.Settings.Set(Platform.Keys.ZoomMode, (int)ZoomAutoMode.Auto);
                ApplyZoom(_auto);
                return;
            }
            int step = i - 1;
            if ((uint)step >= (uint)ZoomLadder.Steps.Length) return;
            Platform.Settings.Set(Platform.Keys.ZoomMode, (int)ZoomAutoMode.Manual);
            ApplyZoom(ZoomLadder.Steps[step]);
        }

        static void ApplyZoom(float zoom)
        {
            FluentApp.SetZoom(zoom);   // live, instant (ch 27 §5)
            Platform.Settings.Set(Platform.Keys.ZoomLevel, zoom);
            Bump();
        }
    }

    /// <summary>An appearance switch: persist, bump the appearance epoch once, bump the page.</summary>
    static Element AppearanceToggle(SettingKey<bool> key) => Toggle(key, afterWrite: static _ => Prefs.Appearance.Bump());

    // ══ 4. LISTS ══════════════════════════════════════════════════════════════════════════════════════════════════════

    static string DensityLabel(int i) => Loc.Get(i switch
    {
        0 => Strings.Settings.Choice.Compact,
        2 => Strings.Settings.Choice.Cozy,
        3 => Strings.Settings.Choice.Comfortable,
        _ => Strings.Settings.Choice.Default,
    });

    static string TrackListStyleLabel(int i)
        => Loc.Get(i == 1 ? Strings.Settings.Appearance.TrackListClassic : Strings.Settings.Appearance.TrackListModern);

    static string PageLayoutLabel(int i)
        => Loc.Get(i == Prefs.DetailHero.Hero ? Strings.Settings.Choice.Hero : Strings.Settings.Choice.Automatic);

    /// <summary>Row density, collapsed behind its answer (N4/N5): the cards in <c>ItemsHeader</c> — a wireframe strip is
    /// not a settings row — and "Always hide track artwork" as the one item.</summary>
    static Element DensityGroup()
    {
        int density = Prefs.Clamp(Platform.Settings.Get(Platform.Keys.RowDensity), 4);
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Appearance.RowDensity),
            Description = Loc.Get(Strings.Settings.Appearance.RowDensitySub),
            HeaderIcon = RowGlyph(Tab.Appearance, "rowDensity"),
            Content = ValueTag(DensityLabel(density)),
            ItemsHeader = ExpanderPanel(Controls.PickerStrip(4, density, static (i, on) => DensityCard(i, on), static i => SetDensity(i))),
            Items =
            [
                Item(Loc.Get(Strings.Settings.Appearance.HideTrackArtwork), Loc.Get(Strings.Settings.Appearance.HideTrackArtworkSub),
                    CheckBox.Create("", new Signal<bool>(Platform.Settings.Get(Platform.Keys.HideTrackArtwork)), onChange: static on =>
                    {
                        Prefs.Appearance.Set(Platform.Keys.HideTrackArtwork, on);
                        Bump();
                    }, style: CheckBox.DefaultStyle with { MinWidth = Spacing.XXXL, MinHeight = Spacing.XXXL }),
                    icon: RowGlyph(Tab.Appearance, "hideTrackArtwork")),
            ],
        }) with { Key = "appearance.density" };
    }

    /// <summary>The miniature's row height and art edge are the REAL Modern ladder × the tested preview scale (N6).</summary>
    static Element DensityCard(int value, bool on)
        => Controls.PickerTitled(
            Controls.PickerCard(on, Controls.PickerTile, Controls.PickerDensityRows(
                Track.TableRules.RowHeightFor(value, classic: false) * Track.TableRules.PreviewScale,
                Track.TableRules.ArtSizeFor(value, classic: false) * Track.TableRules.PreviewScale, on)),
            DensityLabel(value), on);

    /// <summary>ch 30 N12, NOT ported: the Settings density write bumps the appearance epoch, so a mounted or parked track
    /// list re-reads it. Called on every keyboard rove too, so an unchanged value writes nothing.</summary>
    static void SetDensity(int i)
    {
        if ((uint)i >= 4u || Platform.Settings.Get(Platform.Keys.RowDensity) == i) return;
        Prefs.Appearance.Set(Platform.Keys.RowDensity, i);
        Bump();
    }

    static Element TrackListStyleGroup()
    {
        int style = Prefs.Clamp(Platform.Settings.Get(Platform.Keys.TrackRowStyle), 2);
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Appearance.TrackListStyle),
            Description = Loc.Get(Strings.Settings.Appearance.TrackListStyleSub),
            HeaderIcon = RowGlyph(Tab.Appearance, "trackListStyle"),
            Content = ValueTag(TrackListStyleLabel(style)),
            ItemsHeader = ExpanderPanel(Controls.PickerStrip(2, style, static (i, on) => TrackListStyleCard(i, on), static i =>
            {
                if ((uint)i >= 2u || Platform.Settings.Get(Platform.Keys.TrackRowStyle) == i) return;
                Prefs.Appearance.Set(Platform.Keys.TrackRowStyle, i);
                Bump();
            })),
        }) with { Key = "appearance.track-list-style" };
    }

    /// <summary>UI-native miniature rows, never screenshots: Modern = the art-led stacked row, Classic = three lanes + a
    /// hairline; both inherit the live theme and the card's selected ink.</summary>
    static Element TrackListStyleCard(int value, bool on)
    {
        var ink = Controls.PickerInk.For(on);
        Element RowOf() => value == 0 ? Controls.PickerModernRow(ink) : Controls.PickerClassicRow(ink);
        return Controls.PickerTitled(
            Controls.PickerCard(on, Controls.PickerTile, RowOf(), RowOf(), RowOf()) with { Justify = FlexJustify.Center },
            TrackListStyleLabel(value), on);
    }

    /// <summary>Track page layout. Both sub-rows exist only for Automatic (Hero composes no rail); "Clear all remembered
    /// sizes" is composed AWAY — not disabled — while "Keep left-rail same size" is on (ch 27 W4, parity 17).</summary>
    static Element PageLayoutGroup()
    {
        int layout = Prefs.Clamp(Platform.Settings.Get(Platform.Keys.DetailPageLayout), 2);
        Element[] items = [];
        if (layout == Prefs.DetailHero.Automatic)
        {
            bool uniform = Platform.Settings.Get(Platform.Keys.DetailRailUniform);
            Element uniformRow = Item(Loc.Get(Strings.Settings.Appearance.RailUniform), Loc.Get(Strings.Settings.Appearance.RailUniformSub),
                ToggleSwitch.Create(new Signal<bool>(uniform), onChange: static on =>
                {
                    Prefs.DetailHero.Set(Platform.Keys.DetailRailUniform, on);
                    Bump();
                }, style: SettingsCard.CompactToggleStyle()),
                icon: RowGlyph(Tab.Appearance, "railUniform"));
            items = uniform
                ? [uniformRow]
                :
                [
                    uniformRow,
                    Item(Loc.Get(Strings.Settings.Appearance.RailReset), Loc.Get(Strings.Settings.Appearance.RailResetSub),
                        Button.Standard(Loc.Get(Strings.Settings.Appearance.RailResetAction), static () => ConfirmThen(
                                Loc.Get(Strings.Settings.Appearance.RailResetConfirmTitle),
                                Loc.Get(Strings.Settings.Appearance.RailResetConfirmBody),
                                Loc.Get(Strings.Settings.Appearance.RailResetAction), ResetRailSizes),
                            isEnabled: HasCustomizedRail()),
                        icon: RowGlyph(Tab.Appearance, "railReset")),
                ];
        }

        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Appearance.PageLayout),
            Description = Loc.Get(Strings.Settings.Appearance.PageLayoutSub),
            HeaderIcon = RowGlyph(Tab.Appearance, "pageLayout"),
            Content = ValueTag(PageLayoutLabel(layout)),
            ItemsHeader = ExpanderPanel(Controls.PickerStrip(2, layout, static (i, on) => PageLayoutCard(i, on), static i =>
            {
                if ((uint)i >= 2u || Platform.Settings.Get(Platform.Keys.DetailPageLayout) == i) return;
                Prefs.DetailHero.Set(Platform.Keys.DetailPageLayout, i);
                Bump();
            })),
            Items = items,
        }) with { Key = "appearance.pagelayout" };
    }

    /// <summary>The reset's enable gate — a destructive-looking no-op is never offered. The uniform pair is excluded.</summary>
    static bool HasCustomizedRail()
    {
        var s = Platform.Settings;
        return Detail.RailPolicy.HasCustomizedRailPrefs(
            s.Get(Platform.Keys.DetailAlbumRailWidth), s.Get(Platform.Keys.DetailAlbumRailCollapsed),
            s.Get(Platform.Keys.DetailPlaylistRailWidth), s.Get(Platform.Keys.DetailPlaylistRailCollapsed),
            s.Get(Platform.Keys.DetailLikedRailWidth), s.Get(Platform.Keys.DetailLikedRailCollapsed),
            s.Get(Platform.Keys.DetailShowRailWidth), s.Get(Platform.Keys.DetailShowRailCollapsed));
    }

    /// <summary>Reset the FOUR per-scope pairs to their authored defaults (never the uniform pair), then ONE bump, so an
    /// open detail page's rail snaps to the reset widths now rather than next launch.</summary>
    static void ResetRailSizes()
    {
        var s = Platform.Settings;
        s.Set(Platform.Keys.DetailAlbumRailWidth, Platform.Keys.DetailAlbumRailWidth.Default);
        s.Set(Platform.Keys.DetailAlbumRailCollapsed, false);
        s.Set(Platform.Keys.DetailPlaylistRailWidth, Platform.Keys.DetailPlaylistRailWidth.Default);
        s.Set(Platform.Keys.DetailPlaylistRailCollapsed, false);
        s.Set(Platform.Keys.DetailLikedRailWidth, Platform.Keys.DetailLikedRailWidth.Default);
        s.Set(Platform.Keys.DetailLikedRailCollapsed, false);
        s.Set(Platform.Keys.DetailShowRailWidth, Platform.Keys.DetailShowRailWidth.Default);
        s.Set(Platform.Keys.DetailShowRailCollapsed, false);
        Prefs.DetailHero.Bump();
        Bump();
    }

    /// <summary>A wireframe of the page SYSTEM each choice selects — Automatic: a narrow metadata rail beside full-width
    /// rows; Hero: an artwork field and compact identity above the rows (ch 27 W4).</summary>
    static Element PageLayoutCard(int value, bool on)
    {
        var ink = Controls.PickerInk.For(on);

        Element Bar(float w, float h) => new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(h / 2f), Fill = ink.Faint };
        Element RowBar() => new BoxEl { Height = 4f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(2f), Fill = ink.Faint };
        Element Block(float w, float h) => new BoxEl { Width = w, Height = h, Shrink = 0f, Corners = CornerRadius4.All(4f), Fill = ink.Block };

        Element sketch = value == Prefs.DetailHero.Automatic
            ? new BoxEl
            {
                Direction = 0, Gap = 8f, Grow = 1f, AlignItems = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = 4f, Shrink = 0f, Justify = FlexJustify.Center,
                        Children = [Block(20f, 20f), Bar(30f, 6f), Bar(22f, 4f), Block(20f, 8f)],
                    },
                    new BoxEl { Direction = 1, Gap = 5f, Grow = 1f, Justify = FlexJustify.Center, Children = [RowBar(), RowBar(), RowBar(), RowBar()] },
                ],
            }
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
                            new BoxEl { Height = 24f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(4f), Fill = ink.Block },
                            new BoxEl
                            {
                                Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center,
                                Children =
                                [
                                    Bar(48f, 6f), Bar(28f, 4f),
                                    new BoxEl { Direction = 0, Gap = 4f, Children = [Block(24f, 8f), Block(24f, 8f)] },
                                ],
                            },
                        ],
                    },
                    RowBar(), RowBar(), RowBar(),
                ],
            };

        return Controls.PickerTitled(Controls.PickerCard(on, Controls.PickerTile, sketch), PageLayoutLabel(value), on);
    }

    // ══ 5. SIDEBAR (G-182; ch 25 W21 compact cards, ch 27 W2/W29 ②) ═════════════════════════════════════════════════

    /// <summary>ONE shape for all three designs, one Key — a design switch never remounts the card or changes the
    /// section's silhouette. Its own component so it subscribes to <see cref="Sidebar.Design"/> directly: a switch made
    /// from the sidebar's own layout menu while this page is open re-labels, re-selects and adds/removes the
    /// "Customize sidebar" item live.</summary>
    sealed class SidebarDesignCard : Component
    {
        public override Element Render()
        {
            var design = Sidebar.Design.Value;
            // The customizer edits the Curated document; offering it for another design would edit something the user is
            // not looking at, and this row never switches design silently.
            Element[] items = SidebarDesignGating.CanCustomize(design)
                ?
                [
                    Item(Loc.Get(Strings.Settings.Sidebar.Customize), Loc.Get(Strings.Settings.Sidebar.CustomizeSub), null,
                        isClickEnabled: true, onClick: static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.SidebarCustomize)),
                        icon: RowGlyph(Tab.Appearance, "sidebarCustomize")),
                ]
                : [];

            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(Strings.Settings.Sidebar.DesignShort),   // "Design": the eyebrow already says Sidebar
                Description = Loc.Get(Strings.Settings.Sidebar.DesignSub),
                HeaderIcon = RowGlyph(Tab.Appearance, "sidebarDesign"),
                Content = ValueTag(Loc.Get(SidebarDesignGating.TitleKey(design))),
                ItemsHeader = ExpanderPanel(SidebarDesignCards(design)),
                Items = items,
            }) with { Key = "appearance.sidebar.design" };
        }
    }

    /// <summary>The three compact (200-wide) design cards as ONE radio group, applied IMMEDIATELY through
    /// <see cref="Sidebar.SwitchDesign"/> (the snapshot/restore contract; a no-op when unchanged, so a keyboard rove is safe).
    /// Public so the setup wizard can reuse the card mechanic rather than grow a second copy.</summary>
    public static Element SidebarDesignCards(SidebarDesign active)
        => Controls.PickerStrip(SidebarDesignInfo.All.Length, SidebarDesignGating.IndexOf(active),
            static (i, on) => SidebarDesignCardFace(SidebarDesignGating.FromIndex(i), on),
            static i => Sidebar.SwitchDesign(SidebarDesignGating.FromIndex(i)));

    static Element SidebarDesignCardFace(SidebarDesign design, bool on)
    {
        var ink = Controls.PickerInk.For(on);
        Element preview = new BoxEl
        {
            Height = 96f, AlignSelf = FlexAlign.Stretch, Shrink = 0f, Direction = 1, Gap = 3f, ClipToBounds = true,
            Padding = new Edges4(8f, 7f, 8f, 0f),   // no bottom pad: the miniature continues past the fold, like a pane
            Corners = CornerRadius4.All(6f),
            Fill = on ? Tok.AccentSubtle : Tok.FillLayerDefault,
            BorderWidth = 1f, BorderColor = on ? Tok.AccentDefault : Tok.StrokeCardDefault,
            Children = SidebarPreview(design, ink),
        };
        var title = Controls.PickerLabel(Loc.Get(SidebarDesignGating.TitleKey(design)), on);
        Element titleRow = new BoxEl
        {
            Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
            // The "Active" tag makes the selection survive a colour-blind read.
            Children = on ? [title with { Shrink = 1f }, ActiveTag()] : [title],
        };
        return Controls.PickerCard(on, Controls.PickerPaneCompact, preview, titleRow,
            new TextEl(Loc.Get(SidebarDesignGating.SubtitleKey(design)))
            {
                Size = 10.5f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis,
                AlignSelf = FlexAlign.Stretch,
            }) with { Key = SidebarDesignInfo.Slug(design) };
    }

    static Element ActiveTag() => new BoxEl
    {
        Height = Spacing.L, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.PillAll, Fill = Tok.AccentDefault,
        Children = [new TextEl(Loc.Get(Strings.Sidebar.Design.Active)) { Size = 9f, Weight = 600, Color = Tok.TextOnAccentPrimary, MaxLines = 1 }],
    };

    /// <summary>Static semantic geometry at quarter scale (the compact counts) — never a mounted sidebar, never text.</summary>
    static Element[] SidebarPreview(SidebarDesign design, Controls.PickerInk ink)
    {
        var kids = new List<Element>(9);
        switch (design)
        {
            case SidebarDesign.LibraryV3:   // the filter chip strip, the sort pill, then the unified list
                kids.Add(new BoxEl
                {
                    Direction = 0, Gap = 4f, Shrink = 0f,
                    Children = [MiniBar(26f, 9f, ink.Block), MiniBar(20f, 9f, ink.Faint), MiniBar(24f, 9f, ink.Faint), MiniBar(18f, 9f, ink.Faint)],
                });
                kids.Add(new BoxEl { Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [MiniBar(34f, 8f, ink.Faint)] });
                for (int i = 0; i < 3; i++) kids.Add(MiniArtRow(ink));
                break;
            case SidebarDesign.Curated:     // two pins, a rule, the 2-up grid, the route links, a library row
                kids.Add(new BoxEl { Direction = 0, Gap = 5f, Shrink = 0f, Children = [MiniPinTile(ink), MiniPinTile(ink)] });
                kids.Add(MiniHairline(ink));
                kids.Add(new BoxEl { Direction = 0, Gap = 5f, Shrink = 0f, Children = [MiniGridCell(ink), MiniGridCell(ink)] });
                for (int i = 0; i < 2; i++) kids.Add(MiniIconRow(i, ink));
                kids.Add(MiniArtRow(ink));
                break;
            default:                        // Classic: the library shortcuts, a rule, the flat playlist list
                for (int i = 0; i < 4; i++) kids.Add(MiniIconRow(i, ink));
                kids.Add(MiniHairline(ink));
                for (int i = 0; i < 2; i++) kids.Add(MiniArtRow(ink));
                break;
        }
        return kids.ToArray();
    }

    static Element MiniBar(float w, float h, ColorF fill)
        => new BoxEl { Width = w, Height = h, Shrink = 0f, Corners = CornerRadius4.All(h / 2f), Fill = fill };

    static Element MiniHairline(Controls.PickerInk ink) => new BoxEl
    {
        Height = 1f, AlignSelf = FlexAlign.Stretch, Shrink = 0f, Fill = ink.Faint,
        Margin = new Edges4(0f, Spacing.XXS, 0f, Spacing.XXS),
    };

    static Element MiniIconRow(int i, Controls.PickerInk ink) => new BoxEl
    {
        Direction = 0, Height = Spacing.S, Shrink = 0f, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl { Width = Spacing.S, Height = Spacing.S, Shrink = 0f, Corners = Radii.ControlAll, Fill = ink.Block },
            MiniBar(i switch { 0 => 46f, 1 => 38f, 2 => 42f, _ => 34f }, Spacing.XXS, ink.Faint),
        ],
    };

    /// <summary>One text-free list row: a 10-DIP cover tile plus two metadata bars (compact 52 / 34).</summary>
    static Element MiniArtRow(Controls.PickerInk ink) => new BoxEl
    {
        Direction = 0, Height = 10f, Shrink = 0f, Gap = 5f, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl { Width = 10f, Height = 10f, Shrink = 0f, Corners = CornerRadius4.All(2.5f), Fill = ink.Block },
            new BoxEl
            {
                Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children = [MiniBar(52f, Spacing.XS, ink.Faint), MiniBar(34f, Spacing.XXS, ink.Faint)],
            },
        ],
    };

    static Element MiniPinTile(Controls.PickerInk ink) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, Height = 16f, MinWidth = 0f, Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f), ClipToBounds = true, Corners = Radii.ControlAll, Fill = ink.Faint,
        Children = [new BoxEl { Width = Spacing.XL, Height = Spacing.XXS, Corners = Radii.PillAll, Fill = Tok.AccentDefault with { A = 0.58f } }],
    };

    static Element MiniGridCell(Controls.PickerInk ink) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, Height = 18f, MinWidth = 0f, Gap = Spacing.XXS, Direction = 0, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f), Corners = Radii.ControlAll, Fill = ink.Faint,
        Children =
        [
            new BoxEl { Width = 14f, Height = 14f, Shrink = 0f, Corners = Radii.ControlAll, Fill = ink.Block },
            MiniBar(22f, Spacing.XXS, ink.Block),
        ],
    };

    // ══ 6. LYRICS + NOW PLAYING ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Ordered to match the stored value (0 none · 1 translation · 2 romanization) — the index IS the store.</summary>
    static string[] LyricsSecondaryLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.Off),
        Loc.Get(Strings.Settings.Choice.Translation),
        Loc.Get(Strings.Settings.Choice.Romanization),
    ];

    /// <summary>The 0..100 slider plus, ONLY while the setting is pinned to an explicit value, an "Auto" link 12 DIP away
    /// (ch 27 W2). The slider always sits in slot 0 of the same box, so the link appearing on the first drag never
    /// remounts the slider under the pointer.</summary>
    static Element LyricsBlurControl(bool isAuto)
    {
        Element slider = Slider.Create(s_lyricsBlurSlider, static v => SetLyricsBlur(v), s_lyricsBlurOptions, length: 180f);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f,
            Children = isAuto
                ? [slider]
                : [slider, HyperlinkButton.Create(Loc.Get(Strings.Settings.Appearance.LyricsBlurAuto), static () => ResetLyricsBlur())],
        };
    }

    /// <summary>Writes the EXPLICIT 0..100 value (dragging always overrides auto), resolved to the int the store holds.</summary>
    static void SetLyricsBlur(float v)
    {
        int strength = Lyrics.BlurPolicy.Resolve(Math.Clamp((int)MathF.Round(v), 0, 100), GpuProfile.IsWeak);
        if (Platform.Settings.Get(Platform.Keys.LyricsBlurStrength) == strength) return;
        Prefs.Lyrics.SetBlurStrength(strength);
        s_lyricsBlurSlider.Value = strength;
        Bump();
    }

    /// <summary>"Auto" writes −1 back — never whatever the policy currently resolves — so the row stops overriding.</summary>
    static void ResetLyricsBlur()
    {
        Prefs.Lyrics.SetBlurStrength(Lyrics.BlurPolicy.Auto);
        s_lyricsBlurSlider.Value = Lyrics.BlurPolicy.Resolve(Lyrics.BlurPolicy.Auto, GpuProfile.IsWeak);
        Bump();
    }

    /// <summary>The player-style combo: all twelve presets in catalog order, index == id. The selection follows every
    /// other style writer through one effect on the now-playing epoch; the write goes through the rail's adapter (it also
    /// flips the hero to Player and logs the source) and never bumps the page.</summary>
    sealed class NpvStylePicker : Component
    {
        public override Element Render()
        {
            UseSignalEffect(static () => s_npvStyle.Value = Rail.PlayerPrefs.Style());
            var presets = Rail.PlayerCatalog.Presets;
            var labels = new string[presets.Length];
            for (int i = 0; i < presets.Length; i++) labels[i] = Loc.Get(presets[i].LabelKey);
            return ComboBox.Create(labels, s_npvStyle, width: 180f,
                onChange: static i => Rail.PlayerPrefs.SetStyle(i, Rail.NpvDiagnostics.SourceSettings));
        }
    }
}
