// ── Themes/Zune/Structure/Views/Overlays/ZuneOfflineVacancy.cs — Zune's `Overlay.OfflineVacancy` (themes plan 6.3, P5 Z8a) ────────────────────────
//
// The offline voice of a page that has NOTHING saved: the page's error vacancy speaks it while the session is offline (the core decides). A page with its content saved keeps its
// rows under the offline strip instead. No board draws it (owner Q18); it is the strip's grammar at page scale:
//
//   centred column  gap 4, lifted 120 above the centre (none in a Short window)
//   ├─ the cloud-off glyph, 40, tertiary                           (not in a Short window: the decoration gives way)
//   ├─ "you're offline"                                            the page title (40/48/300), the group voice (22/28) at Compact
//   ├─ "<page> needs a connection. ..."                            the metadata voice, at most 360 wide, wrapping
//   └─ [ -> retry ]  go to your library                            the accent disc with its word and a command word, 20 apart; a column of 40-DIP targets at Compact

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>Zune's offline vacancy.</summary>
internal static class ZuneOfflineVacancy
{
    /// <summary>The key the vacancy is mounted under.</summary>
    public const string VacancyKey = "offline-vacancy";

    /// <summary>The registered template of <see cref="ViewKeys.OverlayOfflineVacancy"/>.</summary>
    public static Element Template(OfflineVacancyProps props, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        var layout = ZuneOverlayTiers.Of(facts);
        var kit = ZuneKit.Of(facts);
        bool glyph = ZuneOverlayTiers.VacancyShowsGlyph(facts.Height);
        string caption = props.Page.Length > 0 ? Strings.Common.OfflineNeeds(props.Page) : Loc.Get(Strings.Common.OfflineNeedsAny);
        string title = Loc.Get(Strings.Common.OfflineTitle);
        var kids = new List<Element>(4);
        if (glyph) kids.Add(Icon(ZuneOverlayRules.OfflineGlyph, ZuneOverlayRules.OfflineVacancyGlyph, Tok.TextTertiary));
        kids.Add((layout.VacancyQuietTitle ? ZuneText.Group(title) : ZuneText.Title(title)) with
        {
            Wrap = TextWrap.Wrap, Margin = new Edges4(0f, glyph ? ZuneOverlayRules.OfflineVacancyTitleTop : 0f, 0f, 0f)
        });
        kids.Add(R.Type.TrackMeta(caption) with { Wrap = TextWrap.Wrap, MaxWidth = ZuneOverlayRules.OfflineVacancyCaptionMaxW, });

        // The actions: one row at the desktop tiers; a column of 40-tall targets at Compact.
        var actions = new List<Element>(2);
        if (props.OnRetry is not null) actions.Add(kit.Go(Loc.Get(Strings.Common.Retry), props.OnRetry, glyph: Icons.Refresh));
        if (props.GoToLibrary is not null) actions.Add(kit.Cmd(Loc.Get(Strings.Common.GoToLibrary), props.GoToLibrary));
        if (actions.Count > 0)
            kids.Add(new BoxEl
            {
                Key = VacancyKey + ":actions",
                Direction = layout.VacancyActionsStack ? (byte)1 : (byte)0, AlignItems = FlexAlign.Center, Wrap = !layout.VacancyActionsStack,
                Justify = FlexJustify.Center,
                Gap = layout.VacancyActionsStack ? ZuneOverlayRules.OfflineVacancyStackGap : ZuneOverlayRules.OfflineVacancyActionsGap,
                Margin = new Edges4(0f, ZuneOverlayRules.OfflineVacancyActionsTop, 0f, 0f),
                Children = actions.ToArray(),
            });

        return new BoxEl
        {
            Key = VacancyKey,
            Direction = 1, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Gap = ZuneOverlayRules.OfflineVacancyGap,
            Padding = new Edges4(layout.VacancyPadX, 0f, layout.VacancyPadX, layout.VacancyLiftAt(facts.Height)),
            Children = kids.ToArray(),
        };
    }
}
