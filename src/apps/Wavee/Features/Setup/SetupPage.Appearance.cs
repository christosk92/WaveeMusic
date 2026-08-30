using System;
using System.Diagnostics;
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

/// <summary>Page 4 · Appearance (<c>data-step="4"</c>). Seven dense compact rows (<see cref="SetupCompact.Row"/>/
/// <see cref="SetupCompact.ChipRow"/>) against <see cref="SetupLayout.AppearanceRowPlan"/>, beside a live-preview
/// stage miniature (<see cref="AppearanceStageView"/>) built from <see cref="AppearanceStageModel"/>. Every row writes
/// through <see cref="SetupWrites"/> immediately (never a mirrored draft) so the shell behind the setup plate AND the
/// stage miniature both update on the same render — the wizard's whole premise for this page.</summary>
sealed class SetupAppearancePage : Component
{
    // A fresh Signal<T> is seeded from the setting on each render. The page epoch owns the render edge after a write;
    // the setting remains the source of truth and there is no mirror written during render.
    readonly Signal<int> _epoch = new(0);
    void Bump() => _epoch.Value = _epoch.Peek() + 1;

    static SegmentedItem[] ThemeItems() =>
    [
        new(Loc.Get(Strings.Settings.Choice.System), Icons.Devices),
        new(Loc.Get(Strings.Settings.Choice.Light), Icons.Sun),
        new(Loc.Get(Strings.Settings.Choice.Dark), Icons.Moon),
    ];

    static SegmentedItem[] WindowMaterialItems() =>
    [
        new(Loc.Get(Strings.Settings.Appearance.MaterialMica)),
        new(Loc.Get(Strings.Settings.Appearance.MaterialMicaAlt)),
    ];

    static SegmentedItem[] TrackRowStyleItems() =>
    [
        new(Loc.Get(Strings.Settings.Appearance.TrackListModern)),
        new(Loc.Get(Strings.Settings.Appearance.TrackListClassic)),
    ];

    static SegmentedItem[] DensityItems() =>
    [
        new(Loc.Get(Strings.Settings.Choice.Compact)),
        new(Loc.Get(Strings.Settings.Choice.Default)),
        new(Loc.Get(Strings.Settings.Choice.Cozy)),
        new(Loc.Get(Strings.Settings.Choice.Comfortable)),
    ];

    static SegmentedItem[] PageLayoutItems() =>
    [
        new(Loc.Get(Strings.Settings.Choice.Automatic)),
        new(Loc.Get(Strings.Settings.Choice.Hero)),
    ];

    static string PaletteLabel(string id) => id switch
    {
        "slate" => Loc.Get(Strings.Settings.Appearance.PaletteSlate),
        "neutral" => Loc.Get(Strings.Settings.Appearance.PaletteNeutral),
        "accent" => Loc.Get(Strings.Settings.Appearance.PaletteAccent),
        _ => Loc.Get(Strings.Settings.Appearance.PaletteWarm),
    };

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var requestTheme = UseContext(ThemeControl.Request);
        var settings = svc?.Settings;
        _ = _epoch.Value;

        int themeMode = Math.Clamp(settings?.Get(WaveeSettings.ThemeMode) ?? 0, 0, 2);
        int windowMaterial = (settings?.Get(WaveeSettings.WindowMaterialBaseMica) ?? true) ? 0 : 1;
        bool baseMica = windowMaterial == 0;
        string paletteId = AppearanceStageModel.NormalizePalette(settings?.Get(WaveeSettings.PaletteId));
        int trackRowStyle = Math.Clamp(settings?.Get(WaveeSettings.TrackRowStyle) ?? 0, 0, 1);
        int density = Math.Clamp(settings?.Get(WaveeSettings.RowDensity) ?? 1, 0, 3);
        int pageLayout = Math.Clamp(settings?.Get(WaveeSettings.DetailPageLayout) ?? 0, 0, 1);
        bool heroOnly = settings?.Get(WaveeSettings.DetailPageToneHeroOnly) ?? false;
        bool hideTrackArtwork = settings?.Get(WaveeSettings.HideTrackArtwork) ?? false;
        bool noWash = settings?.Get(WaveeSettings.DisableColorWashes) ?? false;
        bool noMarquee = settings?.Get(WaveeSettings.DisableMarquee) ?? false;
        bool lyricsBackdrop = settings?.Get(WaveeSettings.LyricsAnimatedBackdrop) ?? true;

        const float controlLane = 298f;   // SetupLayout.ControlLane(SetupLayout.DecisionWidth(SetupLayout.TargetWidth), sub: false)

        Element[] rows =
        [
            SetupCompact.Row(
                Loc.Get(Strings.Settings.Appearance.Theme),
                SetupCompact.Segmented(ThemeItems(), themeMode, i =>
                {
                    if (settings is null) return;
                    SetupWrites.SetThemeMode(i, settings, requestTheme);
                    Bump();
                }, controlLane)),
            SetupCompact.Row(
                Loc.Get(Strings.Settings.Appearance.WindowMaterial),
                SetupCompact.Segmented(WindowMaterialItems(), windowMaterial, i =>
                {
                    if (settings is null) return;
                    SetupWrites.SetWindowMaterial(i, settings);
                    Bump();
                }, controlLane)),
            SetupCompact.Row(
                Loc.Get(Strings.Setup.Appearance.Palette),
                PaletteControl(paletteId, settings, requestTheme)),
            SetupCompact.Row(
                Loc.Get(Strings.Settings.Appearance.TrackListStyle),
                SetupCompact.Segmented(TrackRowStyleItems(), trackRowStyle, i =>
                {
                    if (settings is null) return;
                    SetupWrites.SetTrackRowStyle(i, settings);
                    Bump();
                }, controlLane)),
            SetupCompact.Row(
                Loc.Get(Strings.Settings.Appearance.RowDensity),
                SetupCompact.Segmented(DensityItems(), density, i =>
                {
                    if (settings is null) return;
                    SetupWrites.SetRowDensity(i, settings);
                    Bump();
                }, 256f, equalWidth: false)),
            SetupCompact.Row(
                Loc.Get(Strings.Setup.Appearance.Pages),
                SetupCompact.Controls(
                    SetupCompact.Segmented(PageLayoutItems(), pageLayout, i =>
                    {
                        if (settings is null) return;
                        SetupWrites.SetDetailPageLayout(i, settings);
                        Bump();
                    }, 160f),
                    ToolTip.Wrap(FlagToggle(settings, WaveeSettings.DetailPageToneHeroOnly, heroOnly),
                        Loc.Get(Strings.Settings.Appearance.PageTone))),
                sub: Loc.Get(Strings.Setup.Appearance.PagesSub)),
            SetupCompact.ChipRow(
                Loc.Get(Strings.Setup.Appearance.Extras),
                SetupCompact.Chip(Loc.Get(Strings.Setup.Appearance.ChipArtwork), !hideTrackArtwork, () =>
                {
                    if (settings is null) return;
                    SetupWrites.SetAppearanceFlag(WaveeSettings.HideTrackArtwork, !hideTrackArtwork, settings);
                    Bump();
                }),
                SetupCompact.Chip(Loc.Get(Strings.Setup.Appearance.ChipWashes), !noWash, () =>
                {
                    if (settings is null) return;
                    SetupWrites.SetAppearanceFlag(WaveeSettings.DisableColorWashes, !noWash, settings);
                    Bump();
                }),
                SetupCompact.Chip(Loc.Get(Strings.Setup.Appearance.ChipMarquee), !noMarquee, () =>
                {
                    if (settings is null) return;
                    SetupWrites.SetAppearanceFlag(WaveeSettings.DisableMarquee, !noMarquee, settings);
                    Bump();
                }),
                SetupCompact.Chip(Loc.Get(Strings.Setup.Appearance.ChipLyricsMotion), lyricsBackdrop, () =>
                {
                    if (settings is null) return;
                    SetupWrites.SetAppearanceFlag(WaveeSettings.LyricsAnimatedBackdrop, !lyricsBackdrop, settings);
                    Bump();
                })),
        ];
        Debug.Assert(rows.Length == SetupLayout.AppearanceRowPlan.Length);

        Element body = SetupCompact.Column(rows);

        Element? stage = null;
        if (settings is not null)
        {
            var model = AppearanceStageModel.Resolve(
                new AppearanceStageModel.Inputs(themeMode, FluentApp.SystemUsesLightTheme(), paletteId, baseMica,
                    trackRowStyle, density, pageLayout, hideTrackArtwork, noWash, noMarquee, lyricsBackdrop, heroOnly),
                TrackRow.ThumbSize,
                pageLayout == DetailVerticalLayout.PageHero ? AppearanceStageView.HeroListHeight : AppearanceStageView.RailListHeight);

            stage = SetupStage.Column(
                AppearanceStageView.Build(in model, FakeData.Cover(6, 96)),
                SetupStage.Spacer(),
                SetupStage.Caption(
                    Loc.Get(Strings.Setup.Appearance.StageCaptionTitle),
                    Loc.Get(Strings.Setup.Appearance.StageCaptionSub)));
        }

        return SetupPageHost.Frame(SetupPage.Appearance, Loc.Get(Strings.Setup.Eyebrow.Appearance),
            Loc.Get(Strings.Setup.Appearance.Title), body, lead: Loc.Get(Strings.Setup.Appearance.Lead),
            stage: stage, scrollBody: false);
    }

    Element PaletteControl(string activeId, IAppSettings? settings, Action<float>? requestTheme)
    {
        int active = Array.IndexOf(AppearanceStageModel.PaletteIds, activeId);
        if (active < 0) active = 0;

        Element Swatch(int i, bool on)
        {
            string id = AppearanceStageModel.PaletteIds[i];
            return new BoxEl
            {
                Width = SetupLayout.SwatchSize, Height = SetupLayout.SwatchSize, Shrink = 0f,
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Corners = Radii.Circle(SetupLayout.SwatchSize),
                Fill = WaveeColors.PresetSwatch(WaveeTheme.ResolvePalette(id)),
                BorderWidth = on ? 2f : 1f, BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
                Cursor = CursorId.Hand,
                Children = on ? [Icon(Icons.Accept, 10f, Tok.TextOnAccentPrimary)] : [],
            };
        }

        Element strip = WaveePicker.Strip(AppearanceStageModel.PaletteIds.Length, active, Swatch, i =>
        {
            if (settings is null || (uint)i >= (uint)AppearanceStageModel.PaletteIds.Length) return;
            SetupWrites.SetPalette(AppearanceStageModel.PaletteIds[i], settings, requestTheme);
            Bump();
        });

        Element label = WaveePicker.Label(PaletteLabel(activeId), true, 12f) with { Width = 80f, Shrink = 0f };
        return SetupCompact.Controls(strip, label);
    }

    Element FlagToggle(IAppSettings? settings, SettingKey<bool> key, bool value)
        => ToggleSwitch.Create(new Signal<bool>(value), onChange: _ =>
        {
            if (settings is null) return;
            SetupWrites.SetAppearanceFlag(key, !value, settings);
            Bump();
        }, style: SetupCompact.RowToggleStyle);
}
