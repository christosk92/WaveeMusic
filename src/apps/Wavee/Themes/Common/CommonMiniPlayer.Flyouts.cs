// ── Themes/Common/CommonMiniPlayer.Flyouts.cs — the mini player's options panel and its device picker (#197) ────────────
//
// Options (⋯) is ONE compact panel that stays open while you choose (owner, 2026-11-09: "a native panel that doesnt auto close on every
// option"): the eight faces as a 4 x 2 grid of still pictures, the four shapes as one row of pictograms drawn to scale, and two switches
// (keep on top, colour from album art). A pick acts at once and re-marks its tile; the mini player beside the panel is the preview. It
// closes on a click anywhere else, on Esc, or on ⋯ again. Nothing cascades and nothing closes on a pick. "Back to Wavee" is not here:
// the window's own back button is that verb's one home.
//
// It is the theme's own menu card (WinUI's acrylic flyout, Zune's flat square, Classic's) holding the theme's own controls and type: the
// ToggleSwitch from its control table, the section heads and tile names in its small voice (lowercase in Zune), the pictures in its art
// corner capped small. The chosen tile's ring, its name and the switches' on-state are the MINI PLAYER's accent: the playing cover's when
// "Colour from album art" is on for the theme (floored for text by AlbumAccent), else the theme's; flipping that switch re-tints the panel
// at once. The face pictures never animate: Cover, Lyrics, Canvas and Visualizer are drawn here over the playing cover, the four decks are
// the real deck faces at tile size under a surface gate that never opens (the core's MiniFaceUse.Tile), so they run no clock.
//
// Play on (the device button) is the player bar's own device picker (`Player.DevicePicker`, toggled by the shell), not a copy.
//
// Both escape the small window into a popup over the screen, anchored to the window (above it, or below it when the screen has no room
// above): never over the mini player. The window itself passes pointer input through the panel's light dismiss, so the transport and ⋯
// keep working while it is open.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using H = Wavee.Themes.Common.CommonMiniPanelRules;
using M = Wavee.Themes.Common.CommonMiniPlayerRules;

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

    sealed record PanelProps(MiniPlayerProps P, MiniPlayerStyle St);

    /// <summary>The ⋯ panel: opens over the screen beside the window and stays until dismissed; ⋯ again closes it.</summary>
    static void OpenOptions(MiniPlayerProps p, MiniPlayerStyle st, IOverlayService overlay, Anchors a)
    {
        if (a.OptionsMenu.Value is { IsOpen: true } open) { open.Close(); return; }
        var handle = a.OptionsMenu;
        var props = new PanelProps(p, st);
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

    /// <summary>The panel's look for one render: the theme's style, the mini player's accent (a ring and a switch fill) and its text ink,
    /// the picture's corner, the ring's and the hover plate's.</summary>
    readonly record struct PanelLook(MiniPlayerStyle St, ColorF Accent, ColorF AccentText, float Corner, float RingCorner, float PlateCorner);

    static Element PanelHead(MiniPlayerStyle st, string text) => new BoxEl
    {
        Padding = new Edges4(H.PanelInset, 0f, H.PanelInset, H.HeadGap), HitTestVisible = false,
        Children = [st.Small(text) with { Color = Tok.TextSecondary }],
    };

    static Element PanelRule() => new BoxEl { Height = H.RuleW, Fill = Tok.StrokeCardDefault, HitTestVisible = false };

    /// <summary>A choice tile: its picture inside the ring (the accent when chosen, else clear) over its name (the accent's text ink when
    /// chosen), on a subtle plate under the pointer. A radio button for assistive tech.</summary>
    static Element Tile(in PanelLook k, string key, Element picture, float pictureH, string label, bool on, Action act)
    {
        float inset = H.Ring + H.RingGap;
        return new BoxEl
        {
            Key = key, Width = H.TileW, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Center, Gap = H.TileTextGap,
            Padding = new Edges4(0f, H.TilePadTop, 0f, H.TilePadFoot), Corners = CornerRadius4.All(k.PlateCorner),
            Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Cursor = CursorId.Hand, OnClick = act, Role = AutomationRole.RadioButton, Focusable = true,
            Children =
            [
                new BoxEl
                {
                    Width = H.TileW, Height = pictureH + 2f * inset, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                    BorderWidth = H.Ring, BorderColor = on ? k.Accent : ColorF.Transparent, Corners = CornerRadius4.All(k.RingCorner),
                    Children = [picture],
                },
                // The name's box is the tile PITCH (the tile and one gap), centred under the picture: a long name may run a little past its
                // picture's edges into the half gap on each side, never into its neighbour's, and none of the eight is ever trimmed.
                new BoxEl
                {
                    AlignSelf = FlexAlign.Stretch, Direction = 0, Justify = FlexJustify.Center, HitTestVisible = false,
                    Margin = new Edges4(-H.TileGap * 0.5f, 0f, -H.TileGap * 0.5f, 0f),
                    Children = [k.St.Small(label) with
                    {
                        Color = on ? k.AccentText : Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                    }],
                },
            ],
        };
    }

    static Element TileRow(Element[] tiles) => new BoxEl { Direction = 0, Gap = H.TileGap, Children = tiles };

    /// <summary>A layer filling the picture (a ZStack child).</summary>
    static BoxEl Layer(ColorF fill) => new() { Grow = 1f, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Fill = fill, HitTestVisible = false };

    /// <summary>The picture for face <paramref name="fi"/>: still, at the tile's side. The cover and the decks come from the core
    /// (<see cref="MiniFaceUse.Tile"/>); Lyrics, Canvas and Visualizer are marks drawn over the cover or on the analyser's ground.</summary>
    static Element FacePicture(MiniPlayerProps p, in PanelLook k, int fi, string? url)
    {
        float side = H.PictureSide;
        var corners = CornerRadius4.All(k.Corner);
        var kind = p.FaceKind(fi);
        if (kind == MiniFaceKind.Visualizer)
        {
            var ink = ColorF.Lerp(k.Accent, M.OnMediaInk, M.OnDarkLift);
            var bars = new Element[H.VizBars.Length];
            float room = side - 2f * H.VizFoot;
            for (int i = 0; i < bars.Length; i++)
                bars[i] = new BoxEl
                {
                    Width = H.VizBarW, Height = MathF.Round(room * H.VizBars[i]), Fill = ink, HitTestVisible = false,
                    Corners = CornerRadius4.All(k.Corner > 0f ? H.VizBarW * 0.5f : 0f),
                };
            return new BoxEl
            {
                Width = side, Height = side, Corners = corners, Fill = M.VizGround, Direction = 0, AlignItems = FlexAlign.End,
                Justify = FlexJustify.Center, Gap = H.VizBarGap, Padding = new Edges4(0f, 0f, 0f, H.VizFoot), HitTestVisible = false,
                Children = bars,
            };
        }
        var face = p.FaceView(new MiniFaceRequest(fi, side, side, url, k.Corner, MiniFaceUse.Tile));
        if (kind == MiniFaceKind.Lyrics)
        {
            float barCorner = k.Corner > 0f ? H.LyricBarH * 0.5f : 0f;
            BoxEl Bar(float share, bool bright) => new()
            {
                Width = MathF.Round(side * share), Height = H.LyricBarH, Corners = CornerRadius4.All(barCorner), HitTestVisible = false,
                Fill = bright ? M.OnMediaInk : M.OnMediaInk with { A = H.LyricDimAlpha },
            };
            return new BoxEl
            {
                Width = side, Height = side, ZStack = true, ClipToBounds = true, Corners = corners, HitTestVisible = false,
                Children =
                [
                    face,
                    Layer(H.LyricVeil),
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start, Margin = new Edges4(H.LyricInset, 0f, 0f, 0f),
                        Direction = 1, Gap = H.LyricBarGap, HitTestVisible = false,
                        Children = [Bar(H.LyricBar0, false), Bar(H.LyricBar1, true), Bar(H.LyricBar2, false)],
                    },
                ],
            };
        }
        if (kind == MiniFaceKind.Canvas)
            return new BoxEl
            {
                Width = side, Height = side, ZStack = true, ClipToBounds = true, Corners = corners, HitTestVisible = false,
                Children =
                [
                    face,
                    new BoxEl
                    {
                        Width = H.CanvasMark, Height = H.CanvasMark, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End,
                        Margin = new Edges4(0f, 0f, H.CanvasInset, H.CanvasInset), Corners = CornerRadius4.All(MathF.Min(k.Corner, H.CanvasMark * 0.5f)),
                        Fill = H.CanvasPlate, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                        Children = [new TextEl(Icons.Video) { Size = H.CanvasGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = M.OnMediaInk }],
                    },
                ],
            };
        return kind == MiniFaceKind.Object ? new BoxEl { Key = H.DeckPictureKey + fi, HitTestVisible = false, Children = [face] } : face;
    }

    /// <summary>A shape's outline pictogram, each shape at the size it opens at over one divisor, so the four read to scale.</summary>
    static Element ShapePicture(MiniShape s, ColorF ink)
    {
        var (w, ratio) = s switch
        {
            MiniShape.Strip => (H.StripW, H.StripRatio),
            MiniShape.Square => (H.SquareW, 1f),
            MiniShape.Tall => (H.TallW, H.TallRatio),
            _ => (H.WideW, H.WideRatio),
        };
        float pw = MathF.Round(w / H.ShapeDiv), ph = MathF.Max(H.ShapeMinH, MathF.Round(w / ratio / H.ShapeDiv));
        return new BoxEl
        {
            Width = H.PictureSide, Height = H.ShapePicH, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
            Children = [new BoxEl { Width = pw, Height = ph, BorderWidth = H.ShapeStroke, BorderColor = ink, Corners = CornerRadius4.All(H.ShapeCorner), HitTestVisible = false }],
        };
    }

    /// <summary>A labelled switch row: the theme's own ToggleSwitch, its on-state in the album's accent when the player wears one. The whole
    /// row acts (a click on the label flips the switch, under the theme's subtle hover plate); a click on the switch is the switch's own (the
    /// engine gives a click to its nearest owner only), so one click never flips twice.</summary>
    static Element SwitchRow(in PanelLook k, string key, string label, Signal<bool> on, Action<bool> set, ColorF? album)
    {
        var style = ToggleSwitch.DefaultStyle with { MinWidth = 0f, ContentGap = 0f };
        // The album accent is floored for text against the theme, so the theme's on-accent knob (dark in dark mode, white in light) holds.
        if (album is { } a) style = style with { OnFill = a, OnHover = a with { A = a.A * H.SwitchHoverAlpha }, OnPressed = a with { A = a.A * H.SwitchPressedAlpha } };
        return new BoxEl
        {
            Key = key, MinHeight = H.RowH, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Gap = H.RowGap,
            Margin = new Edges4(H.RowPlateInset, 0f, H.RowPlateInset, 0f), Padding = new Edges4(H.PanelInset - H.RowPlateInset, 0f, H.PanelInset - H.RowPlateInset, 0f),
            Corners = CornerRadius4.All(k.PlateCorner), Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Cursor = CursorId.Hand, OnClick = () => { bool v = !on.Peek(); on.Value = v; set(v); },
            Children =
            [
                new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, HitTestVisible = false, Children = [k.St.Label(label) with { Color = Tok.TextPrimary }] },
                ToggleSwitch.Create(on, set, style: style),
            ],
        };
    }

    /// <summary>The ⋯ panel's body. It reads the shape, the face, the switches, the playing cover and its accent live: a pick here, a flip
    /// of a switch, a track change or a drag of the window re-marks it in place.</summary>
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

            // THE ACCENT: the mini player's own (the playing cover's with "Colour from album art", else the theme's).
            string? url = p.ArtUrl();
            ColorF? album = p.ArtColour(slot, fallback) ? p.AlbumAccent(url) : null;
            float corner = MathF.Min(st.ArtCorner, H.PictureCornerMax);
            var k = new PanelLook(st, album ?? Tok.AccentDefault, album ?? Tok.AccentTextPrimary, corner,
                corner > 0f ? corner + H.Ring + H.RingGap : 0f, corner > 0f ? corner + H.PlateCornerExtra : 0f);

            var rows = new List<Element>(8) { PanelHead(st, Loc.Get(Strings.Mini.Face)) };
            var grid = new List<Element>(2);
            for (int i = 0; i < p.FaceCount; i += H.FaceCols)
            {
                var row = new Element[Math.Min(H.FaceCols, p.FaceCount - i)];
                for (int c = 0; c < row.Length; c++)
                {
                    int fi = i + c;
                    row[c] = Tile(k, H.FaceTileKey + fi, FacePicture(p, k, fi, url), H.PictureSide, p.FaceName(fi), fi == face, () => p.SetFace(fi));
                }
                grid.Add(TileRow(row));
            }
            rows.Add(new BoxEl { Padding = new Edges4(H.PanelInset, 0f, H.PanelInset, H.SectionGap), Direction = 1, Gap = H.TileGap, Children = grid.ToArray() });

            var shapes = new Element[s_shapes.Length];
            for (int i = 0; i < shapes.Length; i++)
            {
                var s = s_shapes[i];
                bool on = s == shape;
                shapes[i] = Tile(k, H.ShapeTileKey + s, ShapePicture(s, on ? k.AccentText : Tok.TextSecondary), H.ShapePicH, ShapeName(s), on, () => p.SetShape(s));
            }
            rows.Add(PanelHead(st, Loc.Get(Strings.Mini.Shape)));
            rows.Add(new BoxEl { Padding = new Edges4(H.PanelInset, 0f, H.PanelInset, H.SectionGap), Direction = 1, Children = [TileRow(shapes)] });

            rows.Add(PanelRule());
            rows.Add(SwitchRow(k, H.OnTopKey, Loc.Get(Strings.Mini.OnTop), onTop, v => { if (v != p.OnTop.Peek()) p.ToggleOnTop(); }, album));
            rows.Add(SwitchRow(k, H.ArtColourKey, Loc.Get(Strings.Mini.ArtColour), art, v => p.SetArtColour(slot, v), album));
            return new BoxEl
            {
                Key = H.PanelKey, Width = H.PanelW, Direction = 1, Padding = new Edges4(0f, H.PanelPadTop, 0f, H.PanelPadFoot),
                Children = rows.ToArray(),
            };
        }
    }
}
