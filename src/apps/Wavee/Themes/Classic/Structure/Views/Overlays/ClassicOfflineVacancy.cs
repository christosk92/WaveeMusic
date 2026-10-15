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
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's offline vacancy.</summary>
internal static class ClassicOfflineVacancy
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayOfflineVacancy"/>.</summary>
    public static Element Template(OfflineVacancyProps props, in ViewContext ctx)
    {
        string caption = props.Page.Length > 0 ? Strings.Common.OfflineNeeds(props.Page) : Loc.Get(Strings.Common.OfflineNeedsAny);
        var kids = new List<Element>(4)
        {
            Icon(ClassicOverlayRules.OfflineGlyph, ClassicOverlayRules.OfflineVacancyGlyph, Tok.TextTertiary),
            R.Type.PageHero(Loc.Get(Strings.Common.OfflineTitle)) with
            {
                Wrap = TextWrap.Wrap, Margin = new Edges4(0f, ClassicOverlayRules.OfflineVacancyTitleTop, 0f, 0f),
            },
            R.Type.TrackMeta(caption) with { Wrap = TextWrap.Wrap, MaxWidth = ClassicOverlayRules.OfflineVacancyCaptionMaxW },
        };

        var actions = new List<Element>(2);
        if (props.OnRetry is not null) actions.Add(Button.Standard(Loc.Get(Strings.Common.Retry), props.OnRetry));
        if (props.GoToLibrary is not null) actions.Add(HyperlinkButton.Create(Loc.Get(Strings.Common.GoToLibrary), props.GoToLibrary));
        if (actions.Count > 0)
            kids.Add(new BoxEl
            {
                Key = "offline-vacancy:actions",
                Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicOverlayRules.OfflineVacancyActionsGap,
                Margin = new Edges4(0f, ClassicOverlayRules.OfflineVacancyActionsTop, 0f, 0f),
                Children = actions.ToArray(),
            });

        return new BoxEl
        {
            Key = "offline-vacancy",
            Direction = 1, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Gap = ClassicOverlayRules.OfflineVacancyGap,
            Padding = new Edges4(0f, 0f, 0f, ClassicOverlayRules.OfflineVacancyLift),
            Children = kids.ToArray(),
        };
    }
}
