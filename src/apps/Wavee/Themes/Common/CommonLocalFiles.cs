// ── Themes/Common/CommonLocalFiles.cs — the Local Files list's own views (#240 UX S4, S5, S6; boards240 Onboarding, FirstScan, Changes, Missing) ──
//
// One picture for WinUI and Classic (Classic uses the WinUI layout); Zune draws its own empty state in its voice (`ZuneLocalFiles`) and shares the
// rest, which reads the style's roles and case (lowercase words in Zune). The words, states and verbs are the core's (`LocalFilesList`); this only
// draws them. Numbers: `Rules\CommonLocalFilesRules.cs`.
//
//   EMPTY    (`local-empty:body`) a 600 column centred in the list: "Your music on this PC" (the fold title), the line (secondary, 440 measure),
//            [Add my Music folder] (accent, while the Music folder is on offer) [Choose a folder…], the caption naming the Music folder, then the
//            drop zone (152, dashed, r8): its glyph, "Or drop files here", "Folders or single songs". The drop itself is the page's (any drop on
//            the window plays or adds); the zone names it. At Compact the column fills the list and the buttons take the 40 target.
//   SCAN     (`local-scan:line`) under the head's facts: the line with the time left after it (under it where the head is narrow: a rail), the bar 6
//            under them (determinate; indeterminate while the first listing has no total). It takes the head's measure up to 560.
//   PILL     (`local-pill`) a 32 accent pill centred over the rows, 16 under the band the list pins: the up arrow and "4 new songs · 1 moved". It
//            MOUNTS to show and unmounts to leave (a short drop with a fade, one curve both ways) in a hit-test pass-through lane, so nothing in
//            the list moves and the rows under the lane keep every click. At Compact it is 40 tall.
//   TAG      (`local-tag`) a row's session mark after its title: "New" in the accent's subtle plate, "Moved" with its arrow in the neutral plate
//            and the old path as its tooltip. Mounted only on a marked row (a tooltip host on every row costs memory).
//   REVIEW   (`local-review`) the dialog's body: the line, the plate of missing files (a row each: the note tile, title · artist, the last-known
//            path, "Not found since 12 Nov · removed on 11 Jan", Locate…), what Locate does, the keep-days footer.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using Rules = Wavee.Themes.Common.CommonLocalFilesRules;

namespace Wavee.Themes.Common;

/// <summary>The Local Files list's views (see the file header).</summary>
internal static class CommonLocalFiles
{
    // ══ EMPTY ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.LocalFilesEmpty"/> (WinUI, Classic).</summary>
    public static Element Empty(LocalFilesEmptyProps p, in ViewContext ctx)
    {
        var tier = TierContext.WindowOf(ctx.Tier());
        float target = CommonTiers.Target(tier);
        bool compact = CommonTiers.Stacks(tier);
        var buttons = new List<Element>(2);
        if (p.AddMusic is { } add && p.AddMusicLabel.Length > 0)
            buttons.Add(CommonTouch.Button(p.AddMusicLabel, add, target, accent: true, glyph: Icons.FolderOpen) with { Key = Rules.AddMusicKey, Shrink = 0f });
        buttons.Add(CommonTouch.Button(p.ChooseLabel, p.Choose, target) with { Key = Rules.ChooseKey, Shrink = 0f });

        var kids = new List<Element>(6)
        {
            Centered(R.Type.FoldTitle(p.Title) with { Key = Rules.EmptyTitleKey, Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Heading) }),
            Centered(R.Type.DenseTitle(p.Body) with { Color = Tok.TextSecondary, MaxWidth = Rules.EmptyMeasure }) with { Margin = new Edges4(0f, Rules.EmptyLineGap, 0f, 0f) },
            new BoxEl
            {
                Direction = 0, Wrap = true, Gap = Rules.EmptyButtonGap, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Margin = new Edges4(0f, Rules.EmptyButtonsGap, 0f, 0f), Children = buttons.ToArray(),
            },
        };
        if (p.MusicFolderCaption.Length > 0)
            kids.Add(Centered(R.Type.DenseMeta(p.MusicFolderCaption) with { Key = Rules.CaptionKey, Color = Tok.TextSecondary }) with
            {
                Margin = new Edges4(0f, Rules.EmptyCaptionGap, 0f, 0f),
            });
        kids.Add(DropZone(p) with { Margin = new Edges4(0f, Rules.EmptyDropGap, 0f, 0f) });

        return new BoxEl
        {
            Key = Rules.EmptyKey, Direction = 1, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Padding = new Edges4(0f, Rules.EmptyTop, 0f, Rules.EmptyBottom),
            Children =
            [
                new BoxEl
                {
                    Direction = 1, AlignItems = FlexAlign.Stretch, MinWidth = 0f, Width = compact ? float.NaN : Rules.EmptyWidth, MaxWidth = Rules.EmptyWidth,
                    AlignSelf = compact ? FlexAlign.Stretch : FlexAlign.Center,
                    Children = kids.ToArray(),
                },
            ],
        };
    }

    /// <summary>A line centred in the column, wrapping inside it.</summary>
    static BoxEl Centered(TextEl text) => new()
    {
        Direction = 1, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Children = [text with { Wrap = TextWrap.WrapWholeWords, TextAlign = TextAlign.Center, MinWidth = 0f }],
    };

    /// <summary>The drop zone: what a drop does, said in its own dashed plate (the drop is the page's own).</summary>
    static BoxEl DropZone(LocalFilesEmptyProps p) => new()
    {
        Key = Rules.DropKey, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Rules.DropLineGap, MinWidth = 0f,
        Height = Rules.DropHeight, Shrink = 0f, Padding = new Edges4(Spacing.L, 0f, Spacing.L, 0f),
        Corners = CornerRadius4.All(Rules.DropRadius), BorderWidth = Rules.DropBorder, BorderDashOn = Rules.DashOn, BorderDashOff = Rules.DashOff,
        BorderColor = Prop.Of(static () => Tok.StrokeControlStrongDefault), Fill = Prop.Of(static () => Tok.FillSubtleSecondary),
        Children =
        [
            Icon(Icons.Download, Rules.DropGlyph, Tok.TextSecondary),
            R.Type.DenseTitle(p.DropTitle) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), TextAlign = TextAlign.Center },
            R.Type.DenseMeta(p.DropSub) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), TextAlign = TextAlign.Center },
        ],
    };

    // ══ SCAN ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.LocalFilesScan"/> (every theme).</summary>
    public static Element Scan(LocalFilesScanProps p, in ViewContext ctx)
    {
        float width = float.IsNaN(p.Width) || p.Width <= 0f ? Rules.ScanMax : MathF.Min(Rules.ScanMax, p.Width);
        float barW = MathF.Max(Rules.ScanMinBar, width);
        var line = new List<Element>(2)
        {
            // The line wraps (a narrow rail's head takes it on two lines) and the time left follows it, on its own line when it no longer fits beside it.
            R.Type.DenseTitle(p.Text) with
            {
                Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), Shrink = 1f, MinWidth = 0f,
                MaxLines = p.OneLine ? 1 : 2, Wrap = p.OneLine ? TextWrap.NoWrap : TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (p.Aside.Length > 0)
            line.Add(R.Type.DenseMeta(p.Aside) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), Shrink = 0f, MaxLines = 1 });
        Element bar = p.Fraction >= 0f ? ProgressBar.Determinate(p.Fraction, barW) : ProgressBar.Indeterminate(barW);
        return new BoxEl
        {
            Key = Rules.ScanKey, Direction = 1, MinWidth = 0f, MaxWidth = width, Padding = new Edges4(0f, Rules.ScanTop, 0f, 0f),
            Children =
            [
                new BoxEl { Direction = 0, Wrap = !p.OneLine, AlignItems = FlexAlign.End, Gap = Rules.ScanAsideGap, MinWidth = 0f, Children = line.ToArray() },
                new BoxEl { Key = Rules.ScanBarKey, Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, Rules.ScanBarGap, 0f, 0f), Children = [bar] },
            ],
        };
    }

    // ══ PILL ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.LocalFilesPill"/> (every theme).</summary>
    public static Element Pill(LocalFilesPillProps p, in ViewContext ctx)
    {
        float target = CommonTiers.Target(TierContext.WindowOf(ctx.Tier()));
        if (!p.Shown) return Lane([]);
        EnterExit motion = ctx.Reduced ? Rules.PillFade : Rules.PillMotion;
        var pill = new BoxEl
        {
            Key = Rules.PillKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.PillGap, Shrink = 0f,
            Height = MathF.Max(Rules.PillHeight, target), Padding = new Edges4(Rules.PillPadLead, 0f, Rules.PillPadEnd, 0f),
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Center, Margin = new Edges4(0f, p.Top + Rules.PillTop, 0f, 0f),
            Corners = Radii.FullAll, Fill = Prop.Of(static () => Tok.AccentDefault), HoverFill = Prop.Of(static () => Tok.AccentSecondary),
            PressedFill = Prop.Of(static () => Tok.AccentTertiary), Shadow = Elevation.Flyout,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = p.Go,
            Enter = motion, Exit = motion, Transition = Rules.PillTiming,
            Children =
            [
                Icon(Icons.ChevronUp, Rules.PillGlyph, Tok.TextOnAccentPrimary) with { Shrink = 0f },
                R.Type.DenseTitle(p.Text) with
                {
                    Color = Tok.TextOnAccentPrimary, Weight = Rules.PillWeight, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Shrink = 0f,
                },
            ],
        };
        return Lane([pill]);
    }

    /// <summary>A full-bleed, hit-test pass-through lane over the rows: the rows under its empty area keep every click.</summary>
    static BoxEl Lane(Element[] children) => new()
    {
        ZStack = true, Grow = 1f, MinHeight = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
        HitTestPassThrough = true, HitTestVisible = children.Length > 0,
        Children = children,
    };

    // ══ TAG ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A row's session mark after its title ("New", "Moved" with the old path as its tooltip), for the bound row templates. Mounted only on a
    /// marked row; a recycle onto another row re-reads the mark.</summary>
    public static Element RowTag(global::Wavee.Track.BoundRow r)
        => Flow.Show(() => LocalFilesList.MarkOf(r.P.Track) != LocalFilesList.Mark.None, Tag(r));

    static Element Tag(global::Wavee.Track.BoundRow r)
    {
        Func<bool> moved = () => LocalFilesList.MarkOf(r.P.Track) == LocalFilesList.Mark.Moved;
        var plate = new BoxEl
        {
            Key = Rules.TagKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.TagGap, Shrink = 0f,
            Padding = new Edges4(Rules.TagPadX, 0f, Rules.TagPadX, 0f), Corners = CornerRadius4.All(Rules.TagRadius),
            Fill = Prop.Of(() => moved() ? Tok.FillSubtleSecondary : Tok.AccentSubtle),
            Children =
            [
                Icon(Icons.Forward, Rules.TagGlyph, Tok.TextSecondary) with { Shrink = 0f, Visible = Prop.Of(moved) },
                new TextEl(Prop.Of(() => LocalFilesList.MarkLabel(LocalFilesList.MarkOf(r.P.Track))))
                {
                    Size = Rules.TagSize, LineHeight = Rules.TagLine, Weight = Rules.TagWeight, Case = R.Case.For(LabelKind.Label),
                    // The accent's text ink reads on the accent's subtle plate in a dark style; a light style's accent can sit under 4.5 : 1 there, so the
                    // word takes the primary ink and the plate alone says "accent".
                    Color = Prop.Of(() => moved() ? Tok.TextSecondary : R.IsLight ? Tok.TextPrimary : Tok.AccentTextPrimary), MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
            ],
        };
        return ToolTip.Wrap(plate, Prop.Of<string?>(() => LocalFilesList.MovedFromTip(r.P.Track) is { Length: > 0 } tip ? tip : null));
    }

    // ══ REVIEW ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.LocalFilesReview"/> (every theme): the dialog's body.</summary>
    public static Element Review(LocalFilesReviewProps p, in ViewContext ctx)
    {
        float target = CommonTiers.Target(TierContext.WindowOf(ctx.Tier()));
        var rows = new List<Element>(p.Rows.Count);
        for (int i = 0; i < p.Rows.Count; i++) rows.Add(ReviewRow(p.Rows[i], i > 0, target));
        Element list = p.Rows.Count == 0
            ? R.Type.DenseTitle(p.Empty) with { Key = Rules.ReviewKey + ":none", Color = Tok.TextSecondary, Margin = new Edges4(0f, Rules.ReviewListTop, 0f, 0f) }
            : new BoxEl
            {
                Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, Rules.ReviewListTop, 0f, 0f),
                Corners = CornerRadius4.All(Rules.ReviewRadius), BorderWidth = 1f, ClipToBounds = true,
                BorderColor = Prop.Of(static () => Tok.StrokeCardDefault), Fill = Prop.Of(static () => Tok.FillCardDefault),
                Children =
                [
                    new ScrollEl
                    {
                        MaxHeight = Rules.ReviewListMax, ContentSized = true, AutoEdgeFade = true, ScrollKey = "local-review",
                        Content = new BoxEl { Direction = 1, MinWidth = 0f, Children = rows.ToArray() },
                    },
                ],
            };
        return new BoxEl
        {
            Key = Rules.ReviewKey, Direction = 1, MinWidth = 0f,
            Children =
            [
                Wrapping(R.Type.DenseTitle(p.Body) with { Color = Tok.TextPrimary }),
                list,
                Wrapping(R.Type.DenseMeta(p.Note) with { Color = Tok.TextSecondary, Margin = new Edges4(0f, Rules.ReviewNoteTop, 0f, 0f), Visible = p.Note.Length > 0 }),
                Wrapping(R.Type.DenseMeta(p.Footer) with { Color = Tok.TextSecondary, Margin = new Edges4(0f, Rules.ReviewFooterTop, 0f, 0f), Visible = p.Footer.Length > 0 }),
            ],
        };
    }

    static TextEl Wrapping(TextEl text) => text with { Wrap = TextWrap.WrapWholeWords, MinWidth = 0f };

    /// <summary>One missing file: the note tile, title · artist, the last-known path, the "Not found since" line, Locate… and Remove; a hairline above it from the
    /// second row on.</summary>
    static Element ReviewRow(LocalFilesReviewRow row, bool rule, float target)
    {
        var dense = R.Type.DenseTitle("");
        var title = new SpanTextEl(row.Artist.Length > 0
            ? [new TextSpan(row.Title, Color: Tok.TextPrimary), new TextSpan(" · " + row.Artist, Color: Tok.TextSecondary)]
            : [new TextSpan(row.Title, Color: Tok.TextPrimary)])
        {
            Size = dense.Size, LineHeight = dense.LineHeight, Color = Tok.TextPrimary,
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        var line = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.ReviewGap, MinWidth = 0f,
            MinHeight = Rules.ReviewRow, Padding = new Edges4(Rules.ReviewPadX, Spacing.S, Rules.ReviewPadX, Spacing.S),
            Children =
            [
                new BoxEl
                {
                    Width = Rules.ReviewTile, Height = Rules.ReviewTile, Shrink = 0f, Corners = Radii.ControlAll,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Fill = Prop.Of(static () => Tok.FillSubtleSecondary),
                    Children = [Icon(Icons.MusicNote, Rules.ReviewTileGlyph, Tok.TextTertiary)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        title,
                        R.Type.DenseMeta(row.Path) with { Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        // The dates wrap rather than trim (a narrow dialog would hide when the file goes).
                        R.Type.DenseMeta(row.Line) with { Color = Tok.TextSecondary, MaxLines = 2, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                },
                // Locate… and Remove side by side (board 12), the row's one pair of verbs.
                new BoxEl
                {
                    Direction = 0, Gap = Rules.ReviewVerbGap, Shrink = 0f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        CommonTouch.Button(row.LocateLabel, row.Locate, target) with { Key = Rules.LocateKey + row.Id, Shrink = 0f },
                        CommonTouch.Button(row.RemoveLabel, row.Remove, target) with { Key = Rules.RemoveKey + row.Id, Shrink = 0f },
                    ],
                },
            ],
        };
        return new BoxEl
        {
            Key = Rules.ReviewRowKey + row.Id, Direction = 1, MinWidth = 0f,
            Children = rule ? [new BoxEl { Height = 1f, Shrink = 0f, Fill = Prop.Of(static () => Tok.StrokeCardDefault) }, line] : [line],
        };
    }
}
