// ── Themes/WinUi/Structure/Lists/WinUiEagerRow.cs — WinUI's eager track row (themes plan 6.2; P6 W3) ──────────────────────────────────
//
// The short preview lists' row (Home's top tracks, the library reader's blocks, Recents, the album drawer's rows) as WinUI draws it (W-RowStates' row with no
// selection): the grid (`WinUiRowCells.Grid`), the title run and the skin. The skin is the hover container every reveal in the grid inherits from: a full-width r4
// plate, bare at rest (no zebra, no border), the subtle fill 1 on hover, fill 2 pressed; the "..." rides the row's end as a hover overlay where the lane set
// reserves none.
//
// WHAT THE CORE KEEPS. `Track.EagerRow` and its host: which rows exist, the stamp memo, the like edge, the hover signal, the drag payload,
// the click and the context menu. Every wired member of `EagerRowSkin` is applied.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's style for the eager track row.</summary>
internal static class WinUiEagerRow
{
    /// <summary>The style WinUI registers under <c>StyleKeys.EagerTrackRow</c>. Stateless.</summary>
    public static readonly EagerRowStyle Style = new(WinUiRowCells.Grid, Title, Skin);

    /// <summary>The title with the full-text tooltip while it is cut.</summary>
    static Element Title(Track t, bool isNow) => Controls.TrimmedTitle(WinUiRowCells.TitleCell(t, isNow), t.ForDisplay.Title);

    static BoxEl Skin(EagerRowSkin skin) => new()
    {
        ZStack = skin.OverlayMore,
        Draggable = skin.Draggable,
        MinHeight = skin.RowHeight, ClipToBounds = true, Corners = CornerRadius4.All(WinUiListRules.RowRadius),
        Fill = ColorF.Transparent,
        HoverFill = R.Layer.RowHover,
        PressedFill = R.Layer.RowPressed,
        Role = AutomationRole.Button, OnClick = skin.OnClick,
        FocusVisualMargin = Design.FocusInsetRow,
        OnHoverMove = skin.OnHoverMove,
        OnPointerExit = skin.OnPointerExit,
        Children = skin.OverlayMore ? [skin.Grid, MoreOverlay()] : [skin.Grid],
    };

    static Element MoreOverlay() => new BoxEl
    {
        HitTestPassThrough = true, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
        Padding = new Edges4(0f, 0f, Spacing.S, 0f),
        Children =
        [
            new BoxEl
            {
                Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, BlocksDragArm = true,
                Fill = R.Layer.FloatingChrome,
                Opacity = 0f, HoverOpacity = 1f,
                Children = [Track.MoreCell(true, classic: true)],
            },
        ],
    };
}
