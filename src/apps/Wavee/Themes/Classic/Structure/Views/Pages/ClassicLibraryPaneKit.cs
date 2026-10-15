// ── Themes/Classic/Structure/Views/Pages/ClassicLibraryPaneKit.cs — what the library's panes share (themes plan 6.1, T10c1) ───────────────────
//
// From `Entities\Album.UI.cs` §4 (the library pane statics, library rework §5.5, wave L1) and the two `s_paneSwap` copies of `Album.Pane` and `Show.Pane`: the pane's header and its
// loading twin, its command row, and the selection swap. The album pane and the show pane render the SAME header and swap, rung for rung ("one geometry for both kinds, so a selection
// crossing album -> show moves nothing but the words"). Every one is a VALUE over its arguments — no hooks, no signals, nothing read but what it is handed — so a selection change re-skins
// both panes in place and neither remounts. Accent-NEUTRAL: nothing here reads a palette (ch 15 §0.8). The numbers are `Rules\ClassicLibraryPaneRules.cs`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Classic's library pane pieces.</summary>
internal static class ClassicLibraryPaneKit
{
    /// <summary>The selection swap: the leaving pane fades out as an exit orphan (drawn UNDER the live tree) while the entering one rises 6 DIP into place. Opacity only — a page-sized
    /// subtree is the blur-group perf cliff, and the fade alone reads as the dissolve. Faster out than in, so the new album never waits for the old one.</summary>
    public static readonly LayoutTransition PaneSwap = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(ClassicLibraryPaneRules.SwapInMs, Easing.SmoothOut),
        Enter: new EnterExit(Dy: ClassicLibraryPaneRules.SwapDy, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(ClassicLibraryPaneRules.SwapOutMs, Easing.SmoothOut));

    /// <summary>Cover 128 (r 6, <c>Elevation.Card</c>) · eyebrow · the title LINK in the pane title's one style (<c>R.Type.PageHero</c>, 28/36/600; one line, accent on hover) · the
    /// attribution (<c>ClassicDetailKit.ArtistLine(id.Artists)</c>, or a show's publisher line) · meta 12.5/16. <c>AlignItems End</c>: the text block sits on the cover's baseline, the
    /// prototype's stance.
    /// <para>The block is <see cref="ClassicLibraryPaneRules.PaneHeaderHeight"/> tall, ALWAYS, and the text column is budgeted to fit inside the 128 the cover leaves: 16 eyebrow + 36 title
    /// + 20 attribution + 16 meta + three 3-DIP gaps = 97. A second 36-DIP title line would put the column at 133 and push the whole tracklist down, so the title ellipsizes on its one line.
    /// The <c>MinHeight 0</c> / <c>ClipToBounds</c> on the column and on the block are the BACKSTOP for a locale whose metrics overrun the budget anyway: it clips, it never reflows.</para></summary>
    public static Element PaneHeader(string? cover, string eyebrow, string title, Action open, Element attribution, string meta) => new BoxEl
    {
        // ONE target: cover + eyebrow + title + attribution + meta open the album (the show pane's: the show). A title-only hyperlink left the cover inert in the library pane.
        Direction = 0, Gap = ClassicLibraryPaneRules.HeaderGap, AlignItems = FlexAlign.End, Shrink = 0f,
        Height = ClassicLibraryPaneRules.PaneHeaderHeight, ClipToBounds = true,
        Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.XL, Spacing.M),
        Corners = Radii.CardAll, Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = open,
        Children =
        [
            new BoxEl
            {
                Width = ClassicLibraryPaneRules.PaneCover, Height = ClassicLibraryPaneRules.PaneCover, Shrink = 0f, Corners = Radii.CardAll, ClipToBounds = true, Shadow = Elevation.Card,
                Children = [Controls.Artwork(cover, ClassicLibraryPaneRules.PaneCover, ClassicLibraryPaneRules.PaneCover, Radii.Card, decodePx: ClassicLibraryPaneRules.CoverDecodePx)],
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = ClassicLibraryPaneRules.PaneHeaderGap, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
                Children =
                [
                    R.Type.Eyebrow(eyebrow) with { Color = Tok.TextTertiary },
                    R.Type.PageHero(title) with
                    {
                        Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary,
                        BrushTransitionMs = Design.Motion.Faster, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                    attribution,
                    // The SAME rung for the show pane, which renders this header: "one geometry for both kinds, so a selection crossing album -> show moves nothing but the words".
                    // A typography alias here made the album pane's meta 13/18 secondary against the show pane's 12.5/16 tertiary, and put the header's text arithmetic 2 DIP out.
                    new TextEl(meta)
                    {
                        Size = ClassicLibraryPaneRules.PaneHeaderMetaSize, LineHeight = ClassicLibraryPaneRules.PaneHeaderMetaLine, Color = Tok.TextTertiary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            },
        ],
    }.Interactive(Interaction.Subtle);

    /// <summary><see cref="PaneHeader"/>'s loading twin, for the album pane AND the show pane (both render <see cref="PaneHeader"/>, rung for rung): the cover square and four stand-in
    /// bars — eyebrow, title, attribution, meta — each centred in its real line's slot at the real 3-DIP gap (tuned Classic X2: the three bars at an 8 gap stood 33 DIP below the
    /// words and the meta had none), in the SAME stated <see cref="ClassicLibraryPaneRules.PaneHeaderHeight"/>, padding, gap and bottom alignment, so the crossfade into the real
    /// header is a dissolve and never a reflow. The header's STATED height, not this tree's content height, is the point: the two must occupy the same band. Inert — no click, no role, no
    /// focus (a header that cannot say what it opens offers nothing).</summary>
    public static BoxEl HeaderSkeleton() => new()
    {
        Direction = 0, Gap = ClassicLibraryPaneRules.HeaderGap, AlignItems = FlexAlign.End, Height = ClassicLibraryPaneRules.PaneHeaderHeight, ClipToBounds = true,
        Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.XL, Spacing.M),
        Children =
        [
            new BoxEl { Width = ClassicLibraryPaneRules.PaneCover, Height = ClassicLibraryPaneRules.PaneCover, Shrink = 0f, Corners = Radii.CardAll, Fill = Tok.FillSubtleSecondary },
            new BoxEl
            {
                Direction = 1, Gap = ClassicLibraryPaneRules.PaneHeaderGap, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    SkeletonLine(ClassicLibraryPaneRules.SkeletonEyebrowLine, ClassicLibraryPaneRules.SkeletonEyebrowW, ClassicLibraryPaneRules.SkeletonSmallBarH),
                    SkeletonLine(ClassicLibraryPaneRules.SkeletonTitleLine, ClassicLibraryPaneRules.SkeletonTitleW, ClassicLibraryPaneRules.SkeletonTitleBarH),
                    SkeletonLine(ClassicLibraryPaneRules.SkeletonAttributionLine, ClassicLibraryPaneRules.SkeletonAttributionW, ClassicLibraryPaneRules.SkeletonSmallBarH),
                    SkeletonLine(ClassicLibraryPaneRules.PaneHeaderMetaLine, ClassicLibraryPaneRules.SkeletonMetaW, ClassicLibraryPaneRules.SkeletonSmallBarH),
                ],
            },
        ],
    };

    /// <summary>One header line's slot with its bar centred in it.</summary>
    static BoxEl SkeletonLine(float line, float width, float bar) => new()
    {
        Height = line, Direction = 1, Justify = FlexJustify.Center, AlignItems = FlexAlign.Start,
        Children = [Controls.PendingBar(width, bar)],
    };

    /// <summary>Play (the SYSTEM accent — the one stated exception) · shuffle · save · more (32/r4 icon actions each) · spacer · "Open album ↗" (a 13-px accent text link with the
    /// OpenInNewWindow glyph — a hyperlink, not a capsule). Never a second accent CTA (ch 15 §0.8). <paramref name="save"/> and <paramref name="more"/> are the caller's hosts (the
    /// SaveButton is keyed per uri, the ⋯ needs the overlay), so this row stays a pure value.
    /// <para>The link is the row's one SHRINKING child (#158): the verbs before it are ~300 DIP that never shrink, and with the link `Shrink 0` too a pane under ~400 DIP clipped it off
    /// the edge. Now the spacer gives way first, then the label ellipsizes, and a trimmed label — only a trimmed one — gets its whole text as a tooltip (<c>Controls.TrimTip</c>). The
    /// tooltip wraps the LABEL, in a column slot of its own, rather than the whole link: that slot is exactly the text's box, so the trim test compares like with like (around the link it
    /// would measure a box 36 DIP wider than the text — padding, gap and glyph), and a ToolTip wrapper is `Shrink 0`, so it can only be sized as a COLUMN child (the engine's rule 11).</para></summary>
    public static Element PaneCommands(Action play, Action shuffle, Element save, Element more, Action open)
    {
        string openLabel = Loc.Get(Strings.Library.OpenAlbum);
        TextEl label = R.Type.DenseMeta(openLabel) with
        {
            Color = Tok.AccentTextPrimary, HoverColor = Tok.AccentTextSecondary,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
        };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicLibraryPaneRules.CommandsGap, Shrink = 0f,
            Padding = new Edges4(Spacing.XL, Spacing.M, Spacing.XL, Spacing.S),
            Children =
            [
                Controls.PlayButton(Tok.AccentDefault, play),
                ClassicDetailKit.CommandCircle(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), shuffle),
                save, more,
                new BoxEl { Grow = 1f },
                new BoxEl
                {
                    Direction = 0, Gap = ClassicLibraryPaneRules.OpenLinkGap, AlignItems = FlexAlign.Center, Corners = Radii.ControlAll, Shrink = 1f, MinWidth = 0f,
                    Padding = new Edges4(Spacing.S, ClassicLibraryPaneRules.OpenLinkPadY, Spacing.S, ClassicLibraryPaneRules.OpenLinkPadY),
                    Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, BrushTransitionMs = Design.Motion.Faster,
                    Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
                    Children =
                    [
                        new BoxEl { Direction = 1, Shrink = 1f, MinWidth = 0f, Children = [Controls.TrimTip(label, openLabel, label)] },
                        Icon(Icons.OpenInNewWindow, ClassicLibraryPaneRules.OpenGlyph, Tok.AccentTextPrimary),
                    ],
                },
            ],
        };
    }
}
