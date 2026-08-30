using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The setup wizard's LEFT (344-DIP) column — the "stage". <see cref="Rail"/> is the promoted
/// <c>HeroView.For</c> box (the animated per-page art card); the rest are the building blocks a page composes into a
/// richer stage once its own work package lands (Appearance's live-preview miniature, the sign-in pairing pane, the
/// local-playback progress panel, …). Every page still gets SOME stage for free — <see cref="SetupPageHost.Frame"/>
/// falls back to a bare <see cref="Rail"/> when a page passes no <c>stage:</c> of its own.</summary>
static class SetupStage
{
    /// <summary>The bottom padding of <see cref="Column"/>'s own inset — asymmetric (24/24/24/16) because the caption
    /// at the floor already carries its own visual weight and doesn't want the full 24-DIP breathing room the other
    /// three edges get.</summary>
    const float ColumnBottomPad = 16f;

    /// <summary>The animated per-page hero art, in its card chrome (radial gradient, <see cref="Radii.Card"/>,
    /// <see cref="Tok.StrokeCardDefault"/>) — the exact box <c>HeroView.For</c> used to build directly. Called with no
    /// <paramref name="height"/> it's the WHOLE stage column (stretches to fill the frame's left slot, width pinned to
    /// <see cref="SetupLayout.StageWidth"/> — <see cref="SetupPageHost"/>'s own fallback when a page supplies no
    /// custom stage). Called with a <paramref name="height"/> it's meant to sit as the FIRST child inside a
    /// <see cref="Column"/> instead — a fixed-height card that stretches to the column's own (padding-inset) width
    /// rather than reasserting the full 344.</summary>
    public static BoxEl Rail(SetupPage page, float? height = null) => height is { } h
        // Nested inside a Column: the Column IS the card, so this is just the art's centred, fixed-height slot.
        ? new BoxEl
        {
            Height = h, AlignSelf = FlexAlign.Stretch, Shrink = 0f, MinHeight = 0f,
            Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Children = [HeroView.Art(page)],
        }
        : Chrome(new BoxEl
        {
            Width = SetupLayout.StageWidth, AlignSelf = FlexAlign.Stretch, Shrink = 0f, MinHeight = 0f,
            Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Children = [HeroView.Art(page)],
        });

    /// <summary>The ONE stage surface: the radial accent glow over <see cref="Tok.FillCardSecondary"/>, a 1-px
    /// <see cref="Tok.StrokeCardDefault"/> edge and <see cref="Radii.Card"/> corners — what <c>HeroView.For</c> used to
    /// paint for the 192-DIP rail, now the whole 344×459 column so art, miniature, facts and caption all sit on the same
    /// plate (the approved board's stage is one card, not a card inside a column).</summary>
    static BoxEl Chrome(BoxEl box) => box with
    {
        Corners = CornerRadius4.All(Radii.Card),
        Gradient = new GradientSpec(GradientShape.Radial, 0f,
        [
            new GradientStop(0f, ColorF.Lerp(Tok.FillCardSecondary, Tok.AccentDefault, 0.24f)),
            new GradientStop(0.72f, Tok.FillCardSecondary),
            new GradientStop(1f, Tok.FillCardSecondary),
        ])
        {
            RadialCenter = new Point2(0.32f, 0.18f),
            RadialRadius = new Point2(0.74f, 0.28f),
        },
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
    };

    /// <summary>The stage column shell: fixed 344 DIP wide, inset-padded, clipped (a miniature that overflows its
    /// card must be cut, never paint over the caption below it).</summary>
    public static BoxEl Column(params Element[] children) => Chrome(new BoxEl
    {
        Width = SetupLayout.StageWidth, Direction = 1, Gap = SetupLayout.StageGap,
        AlignItems = FlexAlign.Stretch, Shrink = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
        Padding = new Edges4(SetupLayout.StageInset, SetupLayout.StageInset, SetupLayout.StageInset, ColumnBottomPad),
        Children = children,
    });

    /// <summary>A quiet card inside the stage — the shell every <see cref="DetailBox"/>/facts/status block in the
    /// stage column shares.</summary>
    public static BoxEl Card(params Element[] children) => new BoxEl
    {
        Direction = 1, Gap = 2f, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(12f, 10f, 12f, 10f),
        Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Corners = Radii.CardAll,
        Children = children,
    };

    /// <summary>A <see cref="Card"/>'s title line — 13/18/600, primary ink, one line.</summary>
    public static TextEl CardTitle(string text) => new TextEl(text)
    {
        Size = 13f, LineHeight = 18f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>A <see cref="Card"/>'s plain body line — 12/16, secondary ink, one line.</summary>
    public static TextEl CardLine(string text) => new TextEl(text)
    {
        Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The stage's bottom-anchored caption: a bold one-liner over a quieter one, both centred and clamped to
    /// one line, fixed 34 DIP tall so every page's stage ends on the identical floor regardless of what sits above
    /// it. Always the LAST child of a <see cref="Column"/>, usually preceded by a <see cref="Spacer"/> so it actually
    /// sits at the floor rather than immediately under whatever came before.</summary>
    public static BoxEl Caption(string title, string sub) => new BoxEl
    {
        Direction = 1, Gap = 2f, MinHeight = SetupLayout.StageCaptionHeight, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, AlignSelf = FlexAlign.Stretch,
        Children =
        [
            new TextEl(title) { Size = 13f, LineHeight = 18f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            // Two lines, centred: the real strings run longer than the board's placeholders, and a caption that
            // ellipsizes mid-sentence ("…and nothi…") reads as a bug, not a caption.
            new TextEl(sub) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis, MinWidth = 0f },
        ],
    };

    /// <summary>A status pill inside the stage — the master-off/master-on notifications summary, a runtime status
    /// blurb. <paramref name="accent"/> tints the plate with a faint accent wash instead of the plain card fill (an
    /// "everything's on" affirmative rather than a neutral fact).</summary>
    public static BoxEl Pill(string title, string sub, bool accent) => new BoxEl
    {
        Direction = 1, Gap = 4f, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(12f, 8f, 12f, 8f),
        Corners = Radii.CardAll,
        Fill = accent ? Tok.AccentDefault with { A = 0.12f } : Tok.FillCardDefault,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new TextEl(title) { Size = 13f, LineHeight = 18f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new TextEl(sub) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.WordEllipsis },
        ],
    };

    /// <summary>The small labelled-fact box (<c>PlaybackRuntimeSetupCard</c>'s verification detail block, promoted):
    /// a tinted panel of <paramref name="rows"/> (build each with e.g.
    /// <c>PlaybackRuntimeSetupCard.RuntimeDetailRow(label, value)</c>), fixed to the stage's inner width.</summary>
    public static BoxEl DetailBox(params Element[] rows) => new BoxEl
    {
        Width = SetupLayout.StageInnerWidth, Direction = 1, Gap = Spacing.XS,
        Padding = Edges4.All(Spacing.S),
        Fill = Tok.FillLayerAlt, Corners = CornerRadius4.All(Radii.Control),
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children = rows,
    };

    /// <summary>The Welcome page's "seven steps" rail: one row per <see cref="SetupGating.RoadmapPages"/> entry — a
    /// 16-px numbered pill (accent-filled once this row IS <paramref name="currentIndex"/>) plus its short label.
    /// <paramref name="trailingLabel"/>, when given, sits right-aligned on the FIRST row only (Welcome's "~2 min
    /// total" — the one place the roadmap needs an extra annotation; every later page's own stage draws its own
    /// caption instead of reusing this slot).</summary>
    public static Element Roadmap(int currentIndex, string? trailingLabel = null)
    {
        var pages = SetupGating.RoadmapPages;
        var rows = new Element[pages.Length];
        for (int i = 0; i < pages.Length; i++)
        {
            bool current = i == currentIndex;
            Element pill = new BoxEl
            {
                Width = 16f, Height = 16f, Shrink = 0f,
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Corners = Radii.Circle(16f),
                Fill = current ? Tok.AccentDefault : Tok.FillControlDefault,
                Children = [new TextEl((i + 1).ToString()) { Size = 10f, Weight = 600, Color = current ? Tok.TextOnAccentPrimary : Tok.TextSecondary }],
            };
            Element label = new TextEl(Loc.Get(SetupGating.RoadmapLabelKey(pages[i])))
            {
                Size = 12f, LineHeight = 16f, Color = current ? Tok.TextPrimary : Tok.TextSecondary,
                Weight = (ushort)(current ? 600 : 400), MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                Grow = 1f, Basis = 0f, MinWidth = 0f,
            };
            var kids = new List<Element> { pill, label };
            if (i == 0 && trailingLabel is { Length: > 0 })
                kids.Add(new TextEl(trailingLabel) { Size = 11f, Color = Tok.TextTertiary, Shrink = 0f });
            rows[i] = new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children = kids.ToArray(),
            };
        }
        return new BoxEl { Direction = 1, Gap = Spacing.XXS, Shrink = 0f, Children = rows };
    }

    /// <summary>A flexible gap inside the stage column — same shape as <see cref="SetupCompact.Spacer"/>, kept as a
    /// separate member here so a stage composition never has to reach into the decision-column helper class for one
    /// primitive.</summary>
    public static BoxEl Spacer() => new BoxEl { Grow = 1f, Shrink = 1f, MinHeight = 0f };
}
