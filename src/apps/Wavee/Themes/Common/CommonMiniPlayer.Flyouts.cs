// ── Themes/Common/CommonMiniPlayer.Flyouts.cs — the mini player's two flyouts (#197) ───────────────────────────────────
//
// Options (⋯): one small panel, nothing else: the four shapes drawn to scale ("Or drag any edge to any shape."), the eight faces, two
// switches (keep on top, colour from album art) and the shortcut. Play on (the device button): where it plays and how loud is one
// decision, so the bar picker's own rows (this computer, the other devices, a check on the one playing) sit over the volume row.
// Both escape the small window (ConstrainToRootBounds false) and take the theme's plate: WinUI's acrylic card, Zune's flat square,
// Classic's album wash and soft corners.

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

    static PopupOptions FlyoutOptions(MiniPlayerStyle st, ColorF accent) => new(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss)
    {
        ConstrainToRootBounds = false,
        Plate = PopupPlate.Fluent() with
        {
            Fill = st.Solid(accent), Acrylic = st.MaterialLight == FluentGpu.Pal.WindowMaterial.Acrylic ? PopupPlate.Fluent().Acrylic : null,
            Radius = st.Radius, Padding = default,
        },
    };

    static void OpenOptions(MiniPlayerProps p, MiniPlayerStyle st, ColorF accent, IOverlayService overlay, Anchors a)
    {
        if (a.OptionsMenu.Value is { IsOpen: true } open) { open.Close(); return; }
        var props = new FlyoutProps(p, st, accent);
        a.OptionsMenu.Value = overlay.Open(() => a.Options.Value, () => Embed.Comp(props, static () => new OptionsBody()),
            FlyoutPlacement.BottomEdgeAlignedRight, FlyoutOptions(st, accent));
        a.OptionsMenu.Value.ClosedAction = () => a.OptionsMenu.Value = null;
    }

    static void OpenDevices(MiniPlayerProps p, MiniPlayerStyle st, ColorF accent, IOverlayService overlay, Anchors a)
    {
        if (a.DeviceMenu.Value is { IsOpen: true } open) { open.Close(); return; }
        p.DevicePickerOpened();
        var picker = p.DevicePicker(() => a.DeviceMenu.Value?.Close());
        var props = new DeviceProps(p, st, accent, picker);
        a.DeviceMenu.Value = overlay.Open(() => a.Device.Value, () => Embed.Comp(props, static () => new DevicesBody()),
            FlyoutPlacement.TopEdgeAlignedRight, FlyoutOptions(st, accent));
        a.DeviceMenu.Value.ClosedAction = () => a.DeviceMenu.Value = null;
    }

    sealed record FlyoutProps(MiniPlayerProps P, MiniPlayerStyle St, ColorF Accent);

    sealed record DeviceProps(MiniPlayerProps P, MiniPlayerStyle St, ColorF Accent, DevicePickerProps Picker);

    // ── the pieces both flyouts share ──

    static Element Head(MiniPlayerStyle st, string text) => new BoxEl
    {
        Padding = new Edges4(H.HeadPadX, H.HeadPadTop, H.HeadPadX, H.HeadPadFoot), Children = [st.Heading(text)],
    };

    static Element Group(MiniPlayerStyle st, string text) => new BoxEl
    {
        Padding = new Edges4(H.HeadPadX, H.GroupPadTop, H.HeadPadX, H.GroupPadFoot), Children = [st.Small(text) with { Color = Tok.TextSecondary }],
    };

    static Element Note(MiniPlayerStyle st, string text) => new BoxEl
    {
        Padding = new Edges4(H.HeadPadX, H.GroupPadFoot, H.HeadPadX, H.NotePadFoot),
        Children = [st.Detail(text) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 0 }],
    };

    static Element Rule() => new BoxEl { Height = H.RuleW, Fill = Tok.StrokeCardDefault, HitTestVisible = false };

    static float TileCorner(MiniPlayerStyle st) => MathF.Min(st.ButtonCorner, st.Radius);

    /// <summary>A grid tile: the picture (or none) over its label; chosen, it reads in the accent inside an accent ring.</summary>
    static Element Tile(MiniPlayerStyle st, ColorF accent, Element? picture, string label, bool on, Action act, float minH) => new BoxEl
    {
        Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = minH, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Gap = H.TileTextGap, Padding = new Edges4(0f, H.TilePadY, 0f, H.TilePadY), Corners = CornerRadius4.All(TileCorner(st)),
        Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        BorderWidth = on ? H.TileRing : 0f, BorderColor = on ? accent : ColorF.Transparent,
        Cursor = CursorId.Hand, OnClick = act, Role = AutomationRole.RadioButton, Focusable = true,
        Children = picture is null
            ? [st.Small(label) with { Color = on ? accent : Tok.TextSecondary }]
            : [picture, st.Small(label) with { Color = on ? accent : Tok.TextSecondary }],
    };

    static Element TileRow(Element[] tiles) => new BoxEl { Direction = 0, Gap = H.TileGap, Padding = Edges4.All(H.ChromePad), Children = tiles };

    /// <summary>A shape drawn to scale in its tile (the prototype's default size over 12 and 16).</summary>
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

    /// <summary>A labelled switch row (44 DIP), ruled above.</summary>
    static Element SwitchRow(MiniPlayerStyle st, ColorF accent, string label, Signal<bool> on, Action<bool> set) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            Rule(),
            new BoxEl
            {
                MinHeight = H.RowH, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Gap = H.RowGap,
                Padding = new Edges4(H.RowPadX, 0f, H.RowPadX, 0f),
                Children =
                [
                    new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [st.Label(label) with { Color = Tok.TextPrimary }] },
                    ToggleSwitch.Create(on, set, style: ToggleSwitch.DefaultStyle with
                    {
                        MinWidth = 0f, ContentGap = 0f, OnFill = accent, OnHover = accent, OnPressed = accent,
                    }),
                ],
            },
        ],
    };

    /// <summary>The ⋯ flyout. It reads the shape and face live, so a pick re-marks its tile while it stays open.</summary>
    sealed class OptionsBody : Component
    {
        Signal<bool>? _onTop, _art;

        public override Element Render()
        {
            var f = UseProps<FlyoutProps>();
            var p = f.P;
            var st = f.St;
            var accent = f.Accent;
            var shape = p.Shape.Value;
            int face = Math.Max(0, p.Face.Value);
            _onTop ??= new Signal<bool>(p.OnTop.Peek());
            _art ??= new Signal<bool>(p.ArtColour(st.ArtColourSlot, st.ArtColourDefault));

            var shapes = new Element[s_shapes.Length];
            for (int i = 0; i < shapes.Length; i++)
            {
                var s = s_shapes[i];
                bool on = s == shape;
                shapes[i] = Tile(st, accent, ShapePicture(s, on ? accent : Tok.TextSecondary), ShapeName(s), on, () => p.SetShape(s), H.TileMinH);
            }
            var faces = new List<Element>(p.FaceCount);
            for (int i = 0; i < p.FaceCount; i++)
            {
                int fi = i;
                faces.Add(Tile(st, accent, null, p.FaceName(fi), fi == face, () => p.SetFace(fi), H.FaceTileMinH));
            }
            const int perRow = 4;
            var rows = new List<Element>(12)
            {
                Head(st, Loc.Get(Strings.Mini.Title)),
                Group(st, Loc.Get(Strings.Mini.Shape)),
                TileRow(shapes),
                Note(st, Loc.Get(Strings.Mini.DragHint)),
                Group(st, Loc.Get(Strings.Mini.Face)),
            };
            for (int i = 0; i < faces.Count; i += perRow)
                rows.Add(TileRow(faces.GetRange(i, Math.Min(perRow, faces.Count - i)).ToArray()));
            rows.Add(Note(st, Loc.Get(Strings.Mini.FaceNote)));
            var onTop = _onTop;
            var art = _art;
            rows.Add(SwitchRow(st, accent, Loc.Get(Strings.Mini.OnTop), onTop, v => { if (v != p.OnTop.Peek()) p.ToggleOnTop(); }));
            rows.Add(SwitchRow(st, accent, Loc.Get(Strings.Mini.ArtColour), art, v => p.SetArtColour(st.ArtColourSlot, v)));
            rows.Add(Rule());
            rows.Add(new BoxEl { Padding = new Edges4(H.HeadPadX, H.FootPadTop, H.HeadPadX, H.FootPadFoot),
                Children = [st.Detail(Loc.Get(Strings.Mini.Shortcut)) with { Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 0 }] });
            return new BoxEl { Key = H.OptionsBodyKey, Width = H.FlyoutW, Direction = 1, Padding = Edges4.All(H.FlyoutPad), Children = rows.ToArray() };
        }
    }

    /// <summary>The "Play on" flyout: the picker's rows (a header per section, a check on the one playing) over the volume row. It re-reads the
    /// roster while open, so a transfer landing moves the check in place.</summary>
    sealed class DevicesBody : Component
    {
        public override Element Render()
        {
            var d = UseProps<DeviceProps>();
            var st = d.St;
            var accent = d.Accent;
            var view = d.Picker.Read();
            var bar = d.P.Bar;
            var rows = new List<Element>(view.Entries.Count + 4) { Head(st, Loc.Get(Strings.Mini.PlayOn)) };
            foreach (var e in view.Entries)
            {
                switch (e.Kind)
                {
                    case DevicePickerEntryKind.Separator:
                        break;
                    case DevicePickerEntryKind.Info:
                        rows.Add(Group(st, e.Label));
                        break;
                    default:
                        rows.Add(DeviceRow(st, accent, e));
                        break;
                }
            }
            bool muted = bar.Muted.Value;
            var vol = bar.Volume;
            rows.Add(Rule());
            rows.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = H.TallPanelGap, Padding = new Edges4(H.ChromePad, H.TallPanelGap, H.ChromePad, H.GroupPadFoot),
                Children =
                [
                    MenuButton(st, muted ? Icons.Mute : Icons.Volume, bar.ToggleMute, Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute)),
                    VolumeSlider(vol, bar.SetVolume, accent, H.FlyoutW - 2f * H.FlyoutPad - 2f * H.ChromePad - st.ButtonBox - H.VolumePctW - 2f * H.TallPanelGap),
                    new BoxEl { Width = H.VolumePctW, Direction = 0, Justify = FlexJustify.End, HitTestVisible = false,
                        Children = [st.Detail("") with { Text = Prop.Of(() => Percent(bar.Muted.Value ? 0f : vol.Value)), Color = Tok.TextSecondary }] },
                ],
            });
            return new BoxEl { Key = H.DevicesBodyKey, Width = H.FlyoutW, Direction = 1, Padding = Edges4.All(H.FlyoutPad), Children = rows.ToArray() };
        }

        static Element DeviceRow(MiniPlayerStyle st, ColorF accent, DevicePickerEntry e) => new BoxEl
        {
            MinHeight = H.RowH, Direction = 0, AlignItems = FlexAlign.Center, Gap = H.RowGap, Padding = new Edges4(H.RowPadX, 0f, H.RowPadX, 0f),
            Corners = CornerRadius4.All(TileCorner(st)), Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Cursor = e.Enabled && e.Invoke is not null ? CursorId.Hand : null, OnClick = e.Enabled ? e.Invoke : null,
            Role = AutomationRole.MenuItem, Focusable = e.Enabled, IsEnabled = e.Enabled,
            Children =
            [
                new BoxEl { Width = H.CheckW, Justify = FlexJustify.Center, Direction = 0, HitTestVisible = false,
                    Children = [Glyph(e.Glyph.Length > 0 ? e.Glyph : Icons.Devices, st.ChromeGlyph, e.IsChecked ? accent : Tok.TextPrimary)] },
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 1, HitTestVisible = false,
                    Children = e.Accelerator is { Length: > 0 } note
                        ? [st.Label(e.Label) with { Color = e.Enabled ? Tok.TextPrimary : Tok.TextDisabled }, st.Detail(note) with { Color = Tok.TextSecondary }]
                        : [st.Label(e.Label) with { Color = e.Enabled ? Tok.TextPrimary : Tok.TextDisabled }],
                },
                new BoxEl { Width = H.CheckW, Justify = FlexJustify.Center, Direction = 0, HitTestVisible = false,
                    Children = e.IsChecked ? [Glyph(Icons.Accept, st.ChromeGlyph, accent)] : [] },
            ],
        };

        static Element MenuButton(MiniPlayerStyle st, string glyph, Action act, string tip) => ToolTip.Wrap(new BoxEl
        {
            Width = st.ButtonBox, Height = st.ButtonBox, Shrink = 0f, Corners = CornerRadius4.All(st.ButtonCorner),
            Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Cursor = CursorId.Hand, OnClick = act, Role = AutomationRole.Button, Focusable = true,
            Children = [Glyph(glyph, st.ButtonGlyph, Tok.TextPrimary)],
        }, tip);
    }
}
