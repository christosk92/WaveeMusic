// ── Screens/Diagnostics.Drive.Cursor.cs — the live driver's drawn pointer ────────────────────────────────────────────────────
//
// The driver (Diagnostics.Drive.cs) moves no OS cursor, so a recording would show the UI answering a pointer nobody sees. This
// layer draws one: the Windows arrow (white, a dark outline), its tip on the driver's point, placed by a bound transform (a
// compositor-only write per frame: no render, no layout) and dipped 8 % while a button is down. It is mounted only while a drive
// runs (Drive.Active), never hit-tested, and sits in the frame's top overlay lane beside the FPS HUD (FpsProps.DriveCursor), so
// it draws above every page, the stage and the in-window overlays.
//
// Role: UI (developer-only: a drive is a developer-mode diag verb)

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Render;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>The frame's drive-cursor lane: a full-bleed pass-through layer holding the arrow while a drive runs.</summary>
    public static Element DriveCursorLayer() => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, HitTestPassThrough = true, ZStack = true,
        Children = [Flow.Show(static () => Drive.Active.Value, Embed.Comp(static () => new DriveCursor()))],
    };

    /// <summary>The arrow: 12.4 x 19.6 DIP, the system cursor's outline at 100 %.</summary>
    sealed class DriveCursor : Component
    {
        static readonly PathData s_arrow = PathDataParser.Parse("M1 1 L1 17.6 L5.1 13.7 L7.9 20.2 L10.6 19 L7.9 12.7 L13.4 12.7 Z", PathContentEpoch.Mint(), FillRule.NonZero);

        public override Element Render() => new BoxEl
        {
            Width = 15f, Height = 22f, Shrink = 0f, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
            Transform = Drive.CursorPose, Opacity = Drive.CursorOpacity,
            Children =
            [
                new PathEl
                {
                    Geometry = s_arrow, Width = 15f, Height = 22f,
                    Fill = new ColorF(1f, 1f, 1f, 1f), StrokeColor = new ColorF(0f, 0f, 0f, 0.92f),
                    Stroke = new StrokeStyle(1.15f, LineCap.Round, LineJoin.Miter),
                },
            ],
        };
    }
}
