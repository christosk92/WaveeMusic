// ── Themes/WinUi/Structure/Views/Overlays/WinUiVacancy.cs — WinUI's `State.Vacancy` (themes plan 6.2, P6 W8a) ─────────────────────────────────
//
// The one vacancy the core mounts for every "nothing to show" surface (empty, error, offline, no match), in the boards' grammar (W-NoResults,
// W-EmptyLibrary, W-Offline): a circular glyph tile over a Subtitle-rung headline (20 / 28, semibold), ONE caption line at Body 14 in the
// secondary ink and at most one STANDARD action. Rail scale (a section inside a card, W-Offline's "Couldn't load this section") is the small
// form: a 48 tile, a 14 / 20 headline and a 12 / 16 caption. The words are the core's (the voice's default sentence where the caller gave none).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's vacancy.</summary>
internal static class WinUiVacancy
{
    /// <summary>The registered template of <see cref="ViewKeys.StateVacancy"/>.</summary>
    public static Element Template(VacancyProps props, in ViewContext ctx)
    {
        var voice = props.Voice;
        (string title, string caption) = voice switch
        {
            Controls.VacancyVoice.Error => (Loc.Get(Strings.Common.ErrorTitle), Loc.Get(Strings.Common.ErrorSubtitle)),
            Controls.VacancyVoice.Offline => (Loc.Get(Strings.Common.OfflineTitle), Loc.Get(Strings.Common.OfflineBanner)),
            _ => (Loc.Get(Strings.Common.EmptyTitle), Loc.Get(Strings.Common.EmptySubtitle)),
        };
        string? label = props.ActionLabel;
        if (label is null && props.OnAction is not null && voice is Controls.VacancyVoice.Error or Controls.VacancyVoice.Offline)
            label = Loc.Get(Strings.Common.Retry);

        var tier = WinUiOverlayTiers.For(ctx.Tier().Window);
        bool rail = props.Scale == Controls.VacancyScale.Compact;
        float tile = rail ? WinUiOverlayRules.VacancyRailTile : WinUiOverlayRules.VacancyTile;
        float glyph = rail ? WinUiOverlayRules.VacancyRailGlyph : WinUiOverlayRules.VacancyGlyph;

        var kids = new List<Element>(5)
        {
            new BoxEl
            {
                Width = tile, Height = tile, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(tile / 2f), Fill = Tok.FillSubtleSecondary,
                Children = [Icon(GlyphFor(voice), glyph, Tok.TextSecondary)],
            },
            (rail
                ? new TextEl(props.Title ?? title)
                    { Size = WinUiOverlayRules.VacancyRailTitleSize, LineHeight = WinUiOverlayRules.VacancyRailTitleLine, Weight = 600, Color = Tok.TextPrimary }
                : Subtitle(props.Title ?? title) with { Weight = 600 })
                with { Wrap = TextWrap.Wrap, Margin = new Edges4(0f, WinUiOverlayRules.VacancyTitleTop, 0f, 0f) },
        };
        string? line = props.Subtitle ?? caption;
        if (line is { Length: > 0 })
            kids.Add((rail
                ? new TextEl(line) { Size = WinUiOverlayRules.VacancyRailCaptionSize, LineHeight = WinUiOverlayRules.VacancyRailCaptionLine, Color = Tok.TextSecondary }
                : new TextEl(line) { Size = WinUiOverlayRules.VacancyCaptionSize, LineHeight = WinUiOverlayRules.VacancyCaptionLine, Color = Tok.TextSecondary })
                with { Wrap = TextWrap.Wrap, MaxWidth = WinUiOverlayRules.VacancyCaptionMaxW });
        if (label is { Length: > 0 } && props.OnAction is not null)
        {
            var action = Button.Standard(label, props.OnAction);   // Standard, never Accent: a recovery route is not the page's primary verb
            float target = tier.VacancyActionHeight;
            kids.Add((target > 0f ? action with { MinHeight = target, Height = target, Justify = FlexJustify.Center } : action)
                with { Margin = new Edges4(0f, WinUiOverlayRules.VacancyActionTop, 0f, 0f) });
        }
        return new BoxEl
        {
            Direction = 1, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = WinUiOverlayRules.VacancyGap,
            Padding = Edges4.All(tier.VacancyPadX > 0f ? tier.VacancyPadX : WinUiOverlayRules.VacancyPad), Children = kids.ToArray(),
        };
    }

    private static string GlyphFor(Controls.VacancyVoice voice) => voice switch
    {
        Controls.VacancyVoice.Offline => WinUiOverlayRules.OfflineGlyph,
        Controls.VacancyVoice.Error => Icons.StatusWarning,
        Controls.VacancyVoice.NoMatch => Icons.Search,
        _ => Icons.Library,
    };
}
