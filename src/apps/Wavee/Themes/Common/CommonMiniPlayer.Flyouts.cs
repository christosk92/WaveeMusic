// ── Themes/Common/CommonMiniPlayer.Flyouts.cs — the mini player's options panel and its device picker (#197) ────────────
//
// Options (⋯) is ONE panel that stays open while you choose: the four shapes drawn to scale, the eight faces, two switches (keep on
// top, colour from album art) and the way back to Wavee. A pick acts at once and re-marks its tile; the mini player beside the panel is
// the preview. It closes on a click anywhere else, on Esc, or on ⋯ again. Nothing cascades and nothing closes on a pick (a menu did
// both, and its Face sub-menu covered the very face being chosen).
//
// It is the theme's own menu card (WinUI's acrylic flyout, Zune's flat square, Classic's) holding the theme's own
// controls and type: the ToggleSwitch from its control table, the section heads and tile names in its small voice. Play on (the device
// button) is the player bar's own device picker (`Player.DevicePicker`, toggled by the shell), not a copy.
//
// Both escape the small window into a popup over the screen, anchored to the window (above it, or below it when the screen has no room
// above): never over the mini player. The window itself passes pointer input through the panel's light dismiss, so the face arrows, the
// transport and ⋯ keep working while it is open.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using H = Wavee.Themes.Common.CommonMiniPlayerRules;

namespace Wavee.Themes.Common;

internal static partial class CommonMiniPlayer
{
    static readonly MiniShape[] s_shapes = [MiniShape.Strip, MiniShape.Wide, MiniShape.Square, MiniShape.Tall];

    static string ShapeName(MiniShape s) => s switch
    {
        MiniShape.Strip => Loc.Get(Strings.Mini.Strip),
        MiniShape.Square => Loc.Get(Strings.Mini.Square),
        MiniShape.Tall => Loc.Get(Strings.Mini.Tall),
        _ => Loc.Get(Strings.Mini.Wide),
    };

    sealed record PanelProps(MiniPlayerProps P, MiniPlayerStyle St, Action Close);

    /// <summary>The ⋯ panel: opens over the screen beside the window and stays until dismissed; ⋯ again closes it.</summary>
    static void OpenOptions(MiniPlayerProps p, MiniPlayerStyle st, IOverlayService overlay, Anchors a)
    {
        if (a.OptionsMenu.Value is { IsOpen: true } open) { open.Close(); return; }
        var handle = a.OptionsMenu;
        var props = new PanelProps(p, st, () => handle.Value?.Close());
        handle.Value = overlay.Open(() => a.Window.Value, () => Embed.Comp(props, static () => new OptionsPanel()),
            FlyoutPlacement.TopEdgeAlignedRight,
            // The menu chrome (the default) on purpose: it is the one flyout chrome the overlay host gives its own window, and this
            // panel is taller than every shape of the mini window. Any other chrome stays inside the window and is clipped away.
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss)
            {
                ConstrainToRootBounds = false, PassThrough = () => a.Window.Value, PreserveFocusOnOpen = true,
            });
        handle.Value.ClosedAction = () => handle.Value = null;
    }

    /// <summary>Play on: the player bar's device picker, escaping this window (the shell owns the overlay).</summary>
    static void OpenDevices(MiniPlayerProps p, IOverlayService overlay, Anchors a) => p.ToggleDevicePicker(overlay, a.Window, a.DeviceMenu);

    // ── the panel's pieces ──

    static Element PanelHead(MiniPlayerStyle st, string text) => new BoxEl
    {
        Padding = new Edges4(H.PanelInset, 0f, H.PanelInset, H.PanelHeadGap), HitTestVisible = false,
        Children = [st.Small(text) with { Color = Tok.TextSecondary }],
    };

    static Element PanelRule() => new BoxEl { Height = H.RuleW, Fill = Tok.StrokeCardDefault, HitTestVisible = false };

    static float TileCorner(MiniPlayerStyle st) => MathF.Min(st.ButtonCorner, st.Radius);

    /// <summary>A choice tile: its picture (or none) over its name. Chosen, it reads in the accent inside an accent ring (the stage
    /// options' chosen face).</summary>
    static Element Tile(MiniPlayerStyle st, string key, Element? picture, string label, bool on, Action act, float minH)
    {
        var name = st.Small(label) with { Color = on ? Tok.AccentTextPrimary : Tok.TextPrimary, MaxLines = 1 };
        return new BoxEl
        {
            Key = key, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = minH, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Gap = H.TileTextGap, Padding = new Edges4(0f, H.TilePadY, 0f, H.TilePadY), Corners = CornerRadius4.All(TileCorner(st)),
            Fill = on ? Tok.FillSubtleSecondary : ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            BorderWidth = H.TileRing, BorderColor = on ? Tok.AccentDefault : ColorF.Transparent,
            Cursor = CursorId.Hand, OnClick = act, Role = AutomationRole.RadioButton, Focusable = true,
            Children = picture is null ? [name] : [picture, name],
        };
    }

    static Element TileRow(Element[] tiles) => new BoxEl { Direction = 0, Gap = H.TileGap, Children = tiles };

    /// <summary>A shape drawn to scale in its tile (each at the size it opens at, over the same divisor).</summary>
    static Element ShapePicture(MiniShape s, ColorF ink)
    {
        var (w, ratio) = s switch
        {
            MiniShape.Strip => (H.StripTileW, H.StripRatio),
            MiniShape.Square => (H.SquareTileW, 1f),
            MiniShape.Tall => (H.TallTileW, H.TallRatio),
            _ => (H.WideTileW, H.WideRatio),
        };
        float pw = MathF.Round(w / H.ShapeDivW), ph = MathF.Max(H.ShapeMinH, MathF.Round(w / ratio / H.ShapeDivH));
        return new BoxEl
        {
            Width = H.ShapePicW, Height = H.ShapePicH, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
            Children = [new BoxEl { Width = pw, Height = ph, BorderWidth = H.ShapeStroke, BorderColor = ink, Corners = CornerRadius4.All(H.ShapeCorner), HitTestVisible = false }],
        };
    }

    /// <summary>A labelled switch row: the theme's own ToggleSwitch.</summary>
    static Element SwitchRow(MiniPlayerStyle st, string label, Signal<bool> on, Action<bool> set) => new BoxEl
    {
        MinHeight = H.PanelRowH, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Gap = H.PanelRowGap,
        Padding = new Edges4(H.PanelInset, 0f, H.PanelInset, 0f),
        Children =
        [
            new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, HitTestVisible = false, Children = [st.Label(label) with { Color = Tok.TextPrimary }] },
            ToggleSwitch.Create(on, set, style: ToggleSwitch.DefaultStyle with { MinWidth = 0f, ContentGap = 0f }),
        ],
    };

    /// <summary>The ⋯ panel's body. It reads the shape, the face and the switches live: a pick here, a flip on the art or a drag of the
    /// window re-marks it in place.</summary>
    sealed class OptionsPanel : Component
    {
        public override Element Render()
        {
            var f = UseProps<PanelProps>();
            var p = f.P;
            var st = f.St;
            var shape = p.Shape.Value;
            int face = Math.Max(0, p.Face.Value);
            int slot = st.ArtColourSlot;
            bool fallback = st.ArtColourDefault;
            var onTop = UseSignal(p.OnTop.Peek());
            var art = UseSignal(p.ArtColour(slot, fallback));
            UseSignalEffect(() => onTop.SetIfChanged(p.OnTop.Value));
            UseSignalEffect(() => art.SetIfChanged(p.ArtColour(slot, fallback)));

            var shapes = new Element[s_shapes.Length];
            for (int i = 0; i < shapes.Length; i++)
            {
                var s = s_shapes[i];
                bool on = s == shape;
                shapes[i] = Tile(st, H.PanelKey + ".shape:" + s, ShapePicture(s, on ? Tok.AccentTextPrimary : Tok.TextSecondary), ShapeName(s), on, () => p.SetShape(s), H.ShapeTileMinH);
            }
            var rows = new List<Element>(12)
            {
                PanelHead(st, Loc.Get(Strings.Mini.Shape)),
                new BoxEl { Padding = new Edges4(H.PanelInset, 0f, H.PanelInset, H.PanelSectionGap), Direction = 1, Children = [TileRow(shapes)] },
                PanelHead(st, Loc.Get(Strings.Mini.Face)),
            };
            var grid = new List<Element>(2);
            for (int i = 0; i < p.FaceCount; i += H.FaceCols)
            {
                var row = new Element[Math.Min(H.FaceCols, p.FaceCount - i)];
                for (int c = 0; c < row.Length; c++)
                {
                    int fi = i + c;
                    row[c] = Tile(st, H.PanelKey + ".face:" + fi, null, p.FaceName(fi), fi == face, () => p.SetFace(fi), H.FaceTileMinH);
                }
                grid.Add(TileRow(row));
            }
            rows.Add(new BoxEl { Padding = new Edges4(H.PanelInset, 0f, H.PanelInset, H.PanelSectionGap), Direction = 1, Gap = H.TileGap, Children = grid.ToArray() });
            rows.Add(PanelRule());
            rows.Add(SwitchRow(st, Loc.Get(Strings.Mini.OnTop), onTop, v => { if (v != p.OnTop.Peek()) p.ToggleOnTop(); }));
            rows.Add(SwitchRow(st, Loc.Get(Strings.Mini.ArtColour), art, v => p.SetArtColour(slot, v)));
            rows.Add(PanelRule());
            var close = f.Close;
            rows.Add(new BoxEl
            {
                Key = H.BackKey + ".row", MinHeight = H.PanelRowH, Direction = 0, AlignItems = FlexAlign.Center, Gap = H.PanelRowGap,
                Margin = new Edges4(H.PanelBackInset, H.PanelBackInset, H.PanelBackInset, 0f),
                Padding = new Edges4(H.PanelInset - H.PanelBackInset, 0f, H.PanelInset - H.PanelBackInset, 0f), Corners = CornerRadius4.All(TileCorner(st)),
                Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                Cursor = CursorId.Hand, OnClick = () => { close(); p.BackToWavee(); }, Role = AutomationRole.Button, Focusable = true,
                Children =
                [
                    Glyph(Icons.BackToWindow, st.ChromeGlyph, Tok.TextPrimary),
                    new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, HitTestVisible = false, Children = [st.Label(Loc.Get(Strings.Mini.Back)) with { Color = Tok.TextPrimary }] },
                    st.Detail(H.BackAccelerator) with { Color = Tok.TextTertiary },
                ],
            });
            return new BoxEl
            {
                Key = H.PanelKey, Width = H.PanelW, Direction = 1, Padding = new Edges4(0f, H.PanelPadTop, 0f, H.PanelPadFoot),
                Children = rows.ToArray(),
            };
        }
    }
}
