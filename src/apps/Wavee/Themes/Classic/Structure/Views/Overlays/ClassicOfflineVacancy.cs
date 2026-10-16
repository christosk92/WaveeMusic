// ── Themes/Classic/Structure/Views/Overlays/ClassicOfflineVacancy.cs — Classic's `Overlay.OfflineVacancy` (tuned Classic X1) ───────────
//
// The offline voice of a page that has NOTHING saved (board C-Offline): the page's error vacancy speaks it while the session is offline
// (the core decides: `Shell.FrameRules.VoiceFor`). A page with its content saved keeps its rows under the offline strip instead.
//
//   centred column  gap 4, lifted 120 above the centre
//   ├─ the cloud-off glyph, 40, tertiary
//   ├─ "You're offline"                                            the page-hero rung (28/36/600), 12 below the glyph
//   ├─ "<Page> needs a connection. Your downloads and …"           the metadata rung, at most 360 wide, wrapping
//   └─ [ Retry ]  Go to Your Library                               the standard button and a link, 12 apart, 16 below

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's offline vacancy.</summary>
internal static class ClassicOfflineVacancy
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayOfflineVacancy"/>.</summary>
    public static Element Template(OfflineVacancyProps props, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        var tier = ClassicOverlayTiers.For(facts.Window);
        bool glyph = ClassicOverlayTiers.VacancyShowsGlyph(facts.Height);   // a Short window gives the decoration up: the words and the actions fit its 210 DIP
        string caption = props.Page.Length > 0 ? Strings.Common.OfflineNeeds(props.Page) : Loc.Get(Strings.Common.OfflineNeedsAny);
        var kids = new List<Element>(4);
        if (glyph) kids.Add(Icon(ClassicOverlayRules.OfflineGlyph, ClassicOverlayRules.OfflineVacancyGlyph, Tok.TextTertiary));
        kids.AddRange(new Element[]
        {
            // Compact (board C-Offline-Compact): the 20/28 rung, not the page hero.
            (tier.VacancyQuietTitle ? Subtitle(Loc.Get(Strings.Common.OfflineTitle)) : R.Type.PageHero(Loc.Get(Strings.Common.OfflineTitle))) with
            {
                Wrap = TextWrap.Wrap, Margin = new Edges4(0f, glyph ? ClassicOverlayRules.OfflineVacancyTitleTop : 0f, 0f, 0f),
            },
            R.Type.TrackMeta(caption) with { Wrap = TextWrap.Wrap, MaxWidth = ClassicOverlayRules.OfflineVacancyCaptionMaxW },
        });

        // The actions: one row at the standard bands; a column of 40-tall targets at Compact (Retry at least 120 wide, the link a full-height tap area).
        float target = tier.VacancyActionHeight;
        var actions = new List<Element>(2);
        if (props.OnRetry is not null)
        {
            var retry = Button.Standard(Loc.Get(Strings.Common.Retry), props.OnRetry);
            actions.Add(target > 0f ? retry with { MinWidth = ClassicOverlayRules.OfflineVacancyRetryMinW, Height = target, MinHeight = target, Justify = FlexJustify.Center } : retry);
        }
        if (props.GoToLibrary is not null)
        {
            var link = HyperlinkButton.Create(Loc.Get(Strings.Common.GoToLibrary), props.GoToLibrary);
            actions.Add(target > 0f ? link with { MinHeight = target, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center } : link);
        }
        if (actions.Count > 0)
            kids.Add(new BoxEl
            {
                Key = "offline-vacancy:actions",
                Direction = tier.VacancyActionsStack ? (byte)1 : (byte)0, AlignItems = FlexAlign.Center,
                Gap = tier.VacancyActionsStack ? ClassicOverlayRules.OfflineVacancyStackGap : ClassicOverlayRules.OfflineVacancyActionsGap,
                Margin = new Edges4(0f, ClassicOverlayRules.OfflineVacancyActionsTop, 0f, 0f),
                Children = actions.ToArray(),
            });

        return new BoxEl
        {
            Key = "offline-vacancy",
            Direction = 1, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Gap = ClassicOverlayRules.OfflineVacancyGap,
            Padding = new Edges4(tier.VacancyPadX, 0f, tier.VacancyPadX, tier.VacancyLiftAt(facts.Height)),
            Children = kids.ToArray(),
        };
    }
}
