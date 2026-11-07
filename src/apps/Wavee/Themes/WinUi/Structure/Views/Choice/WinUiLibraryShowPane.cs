// ── Themes/WinUi/Structure/Views/Choice/WinUiLibraryShowPane.cs — WinUI's `Library.ShowPane` (W-LibraryList's detail column; winui show pages) ──────────
//
// The library's podcast (or audiobook) pane, the detail column beside the shows: W-LibraryList draws the picked item's head as a row (the 128 cover, then
// "Podcast", the title in Subtitle 28/36, the publisher in the accent text ink and the meta in Caption) over WinUI's action row (the accent primary,
// "✓ Following" as the neutral toggle, "...") and the SAME reader the show page mounts. Until this lane it was Classic's pane under WinUI's colours: Classic's
// header and the blue heart beside the primary.
//
// THE PANE RE-SKINS IN PLACE (Classic's rule, kept): a selection change re-skins the header and the commands; the arms' keys are the arm's, only the reader
// is keyed per show (by the host). The loading arm is the body's own derived shimmer.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

internal static class WinUiLibraryShowPane
{
    const string BodyKey = "showpane:body", SkelKey = "showpane:skel";

    /// <summary>The registered template of <see cref="ViewKeys.LibraryShowPane"/>.</summary>
    public static Element Template(LibraryShowPaneProps p, in ViewContext ctx)
    {
        var tier = WinUiDetailTiers.Of(ctx.Tier());
        bool compact = ctx.Tier().Content == WidthTier.Compact;
        BoxEl child = p.HeaderReady ? Body(p, tier, compact) : Skeleton(tier);
        return new BoxEl
        {
            ZStack = true, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true,
            Children = [child with { Key = p.HeaderReady ? BodyKey : SkelKey, Enter = WinUiDetailRules.FormSwap, Exit = WinUiDetailRules.FormSwap }],
        };
    }

    static BoxEl Body(LibraryShowPaneProps p, WinUiDetailTier tier, bool compact)
    {
        float target = compact ? WinUiDetailRules.CompactTarget : WinUiDetailRules.Target;
        var words = new List<Element>(4)
        {
            Caption(p.Eyebrow) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            new TextEl(p.Title)
            {
                FontFamily = WinUiShowRules.DisplayFace, Size = WinUiShowRules.CompactTitle, LineHeight = WinUiShowRules.CompactTitleLine, Weight = WinUiShowRules.VoiceWeight,
                Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        };
        if (p.Publisher.Length > 0)
            words.Add(Ui.Body(p.Publisher) with { Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        if (p.Meta.Length > 0)
            words.Add(Caption(p.Meta) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        var cover = new BoxEl
        {
            Width = WinUiShowRules.CompactCover, Height = WinUiShowRules.CompactCover, Shrink = 0f, Corners = CornerRadius4.All(WinUiDetailRules.CoverRadius),
            ClipToBounds = true, Shadow = Elevation.Card, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = p.Open,
            Children = [Controls.Artwork(p.CoverUrl, WinUiShowRules.CompactCover, WinUiShowRules.CompactCover, WinUiDetailRules.CoverRadius, decodePx: WinUiDetailRules.CoverDecode(WinUiShowRules.CompactCover))],
        };
        Element head = new BoxEl
        {
            Direction = 0, Gap = WinUiShowRules.CompactCoverGap, AlignItems = FlexAlign.End, MinWidth = 0f,
            Padding = new Edges4(ShowReaderRules.Pad, WinUiShowRules.PaneTop, ShowReaderRules.Pad, 0f),
            Children = [cover, new BoxEl { Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = words.ToArray() }],
        };
        Element commands = new BoxEl
        {
            Direction = 0, Gap = WinUiDetailRules.ActionGap, AlignItems = FlexAlign.Center,
            Padding = new Edges4(ShowReaderRules.Pad, WinUiShowRules.CompactActionsTop, ShowReaderRules.Pad, Spacing.S),
            Children =
            [
                p.Primary!(static () => Tok.AccentDefault, compact ? ControlSize.Large : ControlSize.Medium),
                new BoxEl
                {
                    Visible = p.HeartShown,
                    Children = [ToggleFollow(p.Uri, p.Title)],
                },
                WinUiDetailKit.MoreButton(p.More, target),
            ],
        };
        return new BoxEl
        {
            Direction = 1, Grow = 1f, ClipToBounds = true,
            Children = [head, commands, new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true, Children = [p.Reader!.Value] }],
        };
    }

    /// <summary>"✓ Following": the show's library state as WinUI's neutral toggle with its check (the stand-in drew Classic's blue heart).</summary>
    static Element ToggleFollow(string uri, string title) => WinUiShow.FollowToggle(uri, title) with { Key = "showpane:follow" };

    /// <summary>The pane's loading arm: the head's shapes (the cover, the eyebrow, the title and the meta as bars) and the command row's.</summary>
    static BoxEl Skeleton(WinUiDetailTier tier) => new()
    {
        Direction = 1, Grow = 1f, Gap = WinUiShowRules.CompactActionsTop,
        Padding = new Edges4(ShowReaderRules.Pad, WinUiShowRules.PaneTop, ShowReaderRules.Pad, 0f),
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = WinUiShowRules.CompactCoverGap, AlignItems = FlexAlign.End,
                Children =
                [
                    new BoxEl { Width = WinUiShowRules.CompactCover, Height = WinUiShowRules.CompactCover, Shrink = 0f, Corners = CornerRadius4.All(WinUiDetailRules.CoverRadius), Fill = Tok.FillSubtleSecondary },
                    new BoxEl
                    {
                        Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f,
                        Children =
                        [
                            new BoxEl { Width = WinUiShowRules.PaneSeedEyebrow, Height = WinUiShowRules.PaneSeedLine, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
                            new BoxEl { Width = WinUiShowRules.PaneSeedTitle, Height = WinUiShowRules.CompactTitleLine, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
                            new BoxEl { Width = WinUiShowRules.PaneSeedMeta, Height = WinUiShowRules.PaneSeedLine, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
                        ],
                    },
                ],
            },
            new BoxEl { Width = WinUiShowRules.PaneSeedPrimary, Height = WinUiDetailRules.Target, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
        ],
    };
}
