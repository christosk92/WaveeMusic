// ── Themes/Classic/Structure/Lists/ClassicEagerRow.cs — Classic's eager track row (themes plan 6.1, T8c) ───────────────────────
//
// From `Entities/Track.UI.cs` ("4. THE EAGER ROW", `EagerTrackRowHost.Render`): the picture of the short preview lists' row. The
// style Classic registers under `StyleKeys.EagerTrackRow` holds the three templates the core host calls while it renders: the grid
// (`ClassicRowCells.Grid`, the lanes and every cell of them), the title run, and the skin (the hover container: fills for rest,
// hover and pressed, the zebra border, the corners, the hover "…" overlay).
//
// WHAT THE CORE KEEPS. `Track.EagerRow` and its host component decide which rows exist and what they hold: the stamp memo that
// re-renders a row only when ITS track moved, the like edge, the hover signal, the drag payload, the click and the context menu.
// The hover container is the interactive ancestor of every reveal in the grid (the transport, the equalizer's pause, the heart's
// reveal), so its hover and press fills and its `OnHoverMove` / `OnPointerExit` edges are applied exactly as before.
//
// Numbers are `Rules\ClassicTrackRowLayout.cs` (WVTH003 allows no literal look here).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

using RowMetrics = global::Wavee.Track.RowMetrics;

/// <summary>Classic's style for the eager track row.</summary>
internal static class ClassicEagerRow
{
    /// <summary>The style Classic registers under <c>StyleKeys.EagerTrackRow</c>. Stateless: every host and a test share it.</summary>
    public static readonly EagerRowStyle Style = new(ClassicRowCells.Grid, Title, Skin);

    /// <summary>The title carries the full-text tooltip while it is cut (the plain run only; the marquee scrolls instead).</summary>
    static Element Title(Track t, bool isNow)
    {
        Element title = ClassicRowCells.TitleCell(t, isNow);
        if (title is TextEl titleRun) title = Controls.TrimmedTitle(titleRun, t.ForDisplay.Title);
        return title;
    }

    static BoxEl Skin(EagerRowSkin skin)
    {
        bool oddZebra = skin.OddZebra;
        return new BoxEl
        {
            ZStack = skin.OverlayMore,
            Draggable = skin.Draggable,
            MinHeight = skin.RowHeight, ClipToBounds = true,
            Margin = new Edges4(RowMetrics.RowInset, 0f, RowMetrics.RowInset, 0f),
            Corners = Radii.ControlAll,
            Fill = oddZebra ? R.Layer.RowZebra : ColorF.Transparent,
            HoverFill = oddZebra ? R.Layer.RowHoverZebra : R.Layer.RowHover,
            PressedFill = oddZebra ? R.Layer.RowPressedZebra : R.Layer.RowPressed,
            // NO PressScale: a near-full-width row visibly shrinks and blurs its title mid-scale (ch 01 §0.11). The
            // pressed fill is the row's only press acknowledgement.
            BorderWidth = 1f,
            BorderColor = oddZebra ? Tok.StrokeCardDefault : ColorF.Transparent,
            HoverBorderColor = Tok.StrokeCardDefault,
            Role = AutomationRole.Button, OnClick = skin.OnClick,
            FocusVisualMargin = Design.FocusInsetRow,
            // Real enter/exit edges: the PointerBit every HoverOpacity descendant inherits AND the equalizer pause.
            OnHoverMove = skin.OnHoverMove,
            OnPointerExit = skin.OnPointerExit,
            Children = skin.OverlayMore ? [skin.Grid, MoreOverlay(skin.Classic)] : [skin.Grid],
        };
    }

    static Element MoreOverlay(bool classic) => new BoxEl
    {
        HitTestPassThrough = true, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
        Padding = new Edges4(0f, 0f, Spacing.S, 0f),
        Children =
        [
            new BoxEl
            {
                Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, BlocksDragArm = true,
                Corners = Radii.ControlAll, Fill = Tok.FillControlSolid,
                Opacity = 0f, HoverOpacity = 1f,
                Children = [Track.MoreCell(true, classic)],
            },
        ],
    };
}
