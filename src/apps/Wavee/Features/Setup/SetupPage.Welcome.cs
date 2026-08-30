using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 0 · Welcome (<c>data-step="0"</c> in the prototype). The prototype's own layout is deliberately NOT
/// the shared eyebrow+title-over-scroller shape every other page uses — its <c>.col.solo</c> grid drops the
/// pinned header row entirely and centers one "Zune" block (a small kicker, the big headline, a lead paragraph,
/// a 3-cell meta row) in the column. So this page still goes through <see cref="SetupPageHost.Frame"/> — every
/// real page does — but passes <c>pinnedHeader: false</c> and an empty eyebrow/title, which omits the header row
/// outright rather than reserving a blank band above the kicker.</summary>
sealed class SetupWelcomePage : Component
{
    public override Element Render()
    {
        var viewport = UseContextSignal(Viewport.Size);
        float plateW = SetupLayout.PlateWidth(viewport.Value.Width);
        var tierSig = UseSignal(SetupLayout.NominalTierFor(plateW));
        UseEffect(() =>
        {
            var current = tierSig.Peek();
            var next = SetupLayout.TierFor(plateW, current);
            if (next != current) tierSig.Value = next;
        }, plateW);
        bool wide = SetupLayout.ShowsHero(tierSig.Value);

        // TextEl is single-weight, so the bold word rides a per-span Weight override on a SpanTextEl paragraph
        // (Dsl/Element.cs:~628) — the same construction WaveeType.ModuleHeader(title, meta) already uses for a
        // two-weight run. The trailing period is punctuation, not translatable prose, so it is not its own loc key.
        System.Func<TextSpan[], SpanTextEl> headlineBuilder = wide ? SetupType.Display : SetupType.Small;
        Element headline = headlineBuilder(
        [
            new TextSpan(Loc.Get(Strings.Setup.Welcome.HeadlinePrefix)),
            new TextSpan(Loc.Get(Strings.Setup.Welcome.HeadlineBold), Weight: 600),
            new TextSpan("."),
        ]) with { MaxWidth = 460f };

        Element body = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, Justify = FlexJustify.Center,
            Children =
            [
                // Sentence case, not the prototype's uppercase+.28em treatment — WaveeType.Eyebrow's own rule
                // (case is not part of the voice; caps-transforming a localized string mangles some scripts).
                new TextEl(Loc.Get(Strings.Setup.Welcome.Kicker))
                {
                    Size = 11f, Weight = 600, CharSpacing = WaveeType.EyebrowTracking, Color = Tok.AccentTextPrimary,
                    Margin = new Edges4(0f, 0f, 0f, 14f),
                },
                headline,
                SetupRows.Lead(Loc.Get(Strings.Setup.Welcome.Lead)) with
                {
                    MaxWidth = 460f, Margin = new Edges4(0f, 0f, 0f, 18f),
                },
                MetaRow(),
            ],
        };

        Element stage = SetupStage.Column(
            SetupStage.Rail(SetupPage.Welcome, SetupLayout.HeroArtSize),
            SetupStage.Roadmap(SetupGating.RoadmapIndexFor(SetupPage.Welcome), Loc.Get(Strings.Setup.Welcome.RoadmapTotal)),
            SetupStage.Spacer(),
            SetupStage.Caption(Loc.Get(Strings.Setup.Welcome.StageCaptionTitle), Loc.Get(Strings.Setup.Welcome.StageCaptionSub)));

        return SetupPageHost.Frame(SetupPage.Welcome, "", "", body, pinnedHeader: false, stage: stage);
    }

    static Element MetaRow() => new BoxEl
    {
        Direction = 0, Gap = Spacing.XL, Wrap = true, Margin = new Edges4(22f, 0f, 0f, 0f),
        Children =
        [
            MetaCell(Loc.Get(Strings.Setup.Welcome.MetaTimeValue), Loc.Get(Strings.Setup.Welcome.MetaTimeLabel)),
            MetaCell(Loc.Get(Strings.Setup.Welcome.MetaPremiumValue), Loc.Get(Strings.Setup.Welcome.MetaPremiumLabel)),
            MetaCell(Loc.Get(Strings.Setup.Welcome.MetaDownloadValue), Loc.Get(Strings.Setup.Welcome.MetaDownloadLabel)),
        ],
    };

    static Element MetaCell(string value, string label) => new BoxEl
    {
        Direction = 1, Gap = 2f, Shrink = 0f,
        Children =
        [
            new TextEl(value) { Size = 15f, Weight = 600, Color = Tok.TextSecondary },
            new TextEl(label) { Size = 12f, Color = Tok.TextTertiary },
        ],
    };
}
