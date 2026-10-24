// ── Themes/Zune/Structure/Views/Overlays/ZuneVacancy.cs — Zune's `State.Vacancy` (themes plan 6.3; contract 1.37) ──────────────────────────────────────
//
// Boards Z-State-Empty, Z-State-Error and their Compact boards: the shared "nothing to show" page, in the page's own words, LEFT-aligned at the page's gutter (not a centred
// card): a 40/48 light heading 40 under the top (28 at Compact), the sentence under it (14/20 secondary, at most 440 wide; 15/22 at Compact), then the action as a ring with
// its word (36 ring at the desktop tiers; at Compact a 56-tall row with a 40 ring, the accent one). The rail scale (a vacancy inside a card or a rail) keeps the group voice
// (22/28) and no top step. The voice's glyph: a retry arrow for an error or offline vacancy, the forward arrow for an empty one.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;
using R_ = Wavee.Themes.Zune.ZuneOverlayRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's shared vacancy.</summary>
internal static class ZuneVacancy
{
    /// <summary>The node key of the vacancy's column.</summary>
    public const string Key = "zune-vacancy";

    /// <summary>The registered template of <see cref="ViewKeys.StateVacancy"/>.</summary>
    public static Element Template(VacancyProps props, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        var layout = ZuneOverlayTiers.Of(facts);
        bool compact = layout.VacancyActionsStack;
        bool rail = props.Scale == Controls.VacancyScale.Compact;
        (string title, string caption) = props.Voice switch
        {
            Controls.VacancyVoice.Error => (Loc.Get(Strings.Common.ErrorTitle), Loc.Get(Strings.Common.ErrorSubtitle)),
            Controls.VacancyVoice.Offline => (Loc.Get(Strings.Common.OfflineTitle), Loc.Get(Strings.Common.OfflineBanner)),
            _ => (Loc.Get(Strings.Common.EmptyTitle), Loc.Get(Strings.Common.EmptySubtitle)),
        };
        title = props.Title ?? title;
        caption = props.Subtitle ?? caption;
        string? action = props.ActionLabel;
        if (action is null && props.OnAction is not null && props.Voice is Controls.VacancyVoice.Error or Controls.VacancyVoice.Offline) action = Loc.Get(Strings.Common.Retry);

        var kids = new List<Element>(3)
        {
            (rail ? ZuneText.Group(title) : ZuneText.Title(title)) with { Wrap = TextWrap.Wrap },
        };
        if (caption.Length > 0)
            kids.Add((compact ? R.Type.TrackTitle(caption) : R.Type.DenseTitle(caption)) with
            {
                Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = R_.VacancyCaptionMaxW, MinWidth = 0f,
                Margin = new Edges4(0f, compact ? R_.VacancyCaptionGapCompact : R_.VacancyCaptionGap, 0f, 0f),
            });
        if (action is { Length: > 0 } label && props.OnAction is { } go)
        {
            string glyph = props.Voice is Controls.VacancyVoice.Error or Controls.VacancyVoice.Offline ? Icons.Refresh : Icons.Forward;
            kids.Add(new BoxEl
            {
                Key = Key + ":actions", Direction = (byte)(compact ? 1 : 0), AlignItems = compact ? FlexAlign.Stretch : FlexAlign.Center,
                Gap = compact ? R_.VacancyRowGap : R_.VacancyActionsGap,
                Margin = new Edges4(0f, compact ? R_.VacancyActionsTopCompact : R_.VacancyActionsTop, 0f, 0f),
                Children =
                [
                    K.Ring(Key + ":action", glyph, label, showLabel: true, primary: compact, go, compact ? R_.VacancyRingCompact : R_.VacancyRing, target: compact ? R_.VacancyRowH : 0f),
                ],
            });
        }

        return new BoxEl
        {
            Key = Key, Direction = 1, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Start, Justify = FlexJustify.Start,
            Padding = new Edges4(0f, rail ? 0f : compact ? R_.VacancyTopCompact : R_.VacancyTop, 0f, 0f),
            Children = kids.ToArray(),
        };
    }
}
