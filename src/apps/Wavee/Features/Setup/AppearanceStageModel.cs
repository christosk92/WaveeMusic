using System;
using Wavee.Features.Detail;

namespace Wavee;

/// <summary>Appearance page 4's engine-free live-preview model: given the raw settings, produces every derived fact
/// <c>AppearanceStageView</c> draws (row geometry, ink tint, the rail/hero layout arm) — and nothing else. Free of
/// every FluentGpu/Loc/Signal type BY CONSTRUCTION, exactly like <c>DetailTrackTableRules</c>/<c>DetailVerticalLayout</c>
/// (the two production files it depends on, both already engine-free and already source-included by Wavee.Tests for
/// the same reason), so <c>AppearanceStageModelTests</c> can drive the real <see cref="Resolve"/> decision headlessly.</summary>
static class AppearanceStageModel
{
    /// <summary>The miniature draws every row/thumb at half the real UI's scale.</summary>
    public const float Scale = 0.5f;

    /// <summary>The ONE palette-id list, in card order — the picker's swatches, the writer's index and
    /// <see cref="NormalizePalette"/>'s valid set all read this single array, so none of the three can drift from the
    /// other two the way <c>SettingsPage.General</c>'s own copy (<c>s_paletteIds</c>) used to.</summary>
    public static readonly string[] PaletteIds = ["warm", "slate", "neutral", "accent"];

    /// <summary>Every raw setting the stage needs. The page reads these off <c>IAppSettings</c>/<c>FluentApp</c> and
    /// hands them in — nothing here reaches out to a live setting or the OS itself, which is what keeps this
    /// testable without a settings seam or a window.</summary>
    public readonly record struct Inputs(
        int ThemeMode, bool SystemIsLight, string? PaletteId, bool BaseMica, int TrackRowStyle, int Density,
        int DetailPageLayout, bool HideTrackArtwork, bool DisableColorWashes, bool DisableMarquee,
        bool LyricsAnimatedBackdrop, bool DetailPageToneHeroOnly);

    /// <summary>Every derived fact the miniature (and its caption) draws from. <see cref="Dark"/>/<see cref="PaletteId"/>
    /// exist purely so a test can assert the theme resolution without mounting the engine's own <c>Tok</c> — the view
    /// itself needs neither: the live shell behind the plate already re-themes through <c>Tok</c>/<c>WaveeTheme</c>
    /// the moment a row writes, so the miniature's ink/fills just read the current tokens like anything else.</summary>
    public readonly record struct Result(
        bool Dark, string PaletteId, bool MicaAlt, bool Classic, float RowHeight, float ArtEdge, bool TwoLineRows,
        bool HeroLayout, int RowCount, float TintAlpha, bool TintHeroOnly, bool Marquee, bool LyricsMotion);

    /// <summary>An id outside <see cref="PaletteIds"/> — a corrupt or pre-upgrade persisted value, or simply
    /// <c>null</c> — resolves to "neutral", the same fallback <c>WaveeTheme.ResolvePalette</c> uses for the same
    /// unreachable-preset failure mode.</summary>
    public static string NormalizePalette(string? id) =>
        id is not null && Array.IndexOf(PaletteIds, id) >= 0 ? id : "neutral";

    /// <summary>Mirrors <c>WaveeTheme.ApplyThemeMode</c>'s own kind resolution (0 System · 1 Light · 2 Dark) without
    /// touching <c>FluentApp</c>/<c>Tok</c> directly — <paramref name="systemIsLight"/> is the one live fact the page
    /// must read itself (<c>FluentApp.SystemUsesLightTheme()</c>) and pass in.</summary>
    public static bool IsDark(int themeMode, bool systemIsLight) => themeMode switch
    {
        1 => false,
        2 => true,
        _ => !systemIsLight,
    };

    /// <summary>The whole decision. <paramref name="thumbDip"/> is the real track-row art edge at 1x
    /// (<c>TrackRow.ThumbSize</c>, passed in by the view so this file never references the engine-bound control), and
    /// <paramref name="listHeight"/> is however much vertical room the miniature's track-list area has at 1x for the
    /// current layout arm (the view's own rail-vs-hero constants) — both are already view geometry, not settings, which
    /// is why they arrive as plain floats rather than living in <see cref="Inputs"/>.</summary>
    public static Result Resolve(in Inputs inputs, float thumbDip, float listHeight)
    {
        int density = Math.Clamp(inputs.Density, 0, 3);
        bool classic = Math.Clamp(inputs.TrackRowStyle, 0, 1) == 1;
        int layout = Math.Clamp(inputs.DetailPageLayout, 0, 1);
        string paletteId = NormalizePalette(inputs.PaletteId);

        float rowHeight = DetailTrackTableRules.RowHeightFor(density, classic) * Scale;
        bool showsThumb = DetailTrackTableRules.IdentityColumns(classic, true, inputs.HideTrackArtwork, true, 0).Thumb;
        float artEdge = showsThumb ? thumbDip * Scale : 0f;
        int rowCount = rowHeight > 0f ? Math.Max(1, (int)(listHeight / rowHeight)) : 1;
        float tintAlpha = inputs.DisableColorWashes ? 0f : inputs.BaseMica ? 0.10f : 0.18f;

        return new Result(
            Dark: IsDark(inputs.ThemeMode, inputs.SystemIsLight),
            PaletteId: paletteId,
            MicaAlt: !inputs.BaseMica,
            Classic: classic,
            RowHeight: rowHeight,
            ArtEdge: artEdge,
            TwoLineRows: !classic,
            HeroLayout: layout == DetailVerticalLayout.PageHero,
            RowCount: rowCount,
            TintAlpha: tintAlpha,
            TintHeroOnly: inputs.DetailPageToneHeroOnly,
            Marquee: !inputs.DisableMarquee,
            LyricsMotion: inputs.LyricsAnimatedBackdrop);
    }
}
