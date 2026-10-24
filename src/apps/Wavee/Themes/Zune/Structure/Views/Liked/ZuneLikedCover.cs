// ── Themes/Zune/Structure/Views/Liked/ZuneLikedCover.cs — Zune's Liked Songs cover, picker and flyout (themes plan 6.3; P5 Z6b1) ─────────────────
//
// Boards Z-Liked (+ -Small, -Compact, -Huge): the page's head is the title and the meta line (no cover on the wide arm, the frame's head arm); the cover is
// drawn where the page hands one over: the vertical hero's cover (with its style picker), the Home and library tiles, the drag chip. Zune draws it FLAT:
//
//   · stock    the bundled Liked Songs PNG (the degrade target: correct offline, never a render-time request, no reveal);
//   · mosaic   a flat mosaic of the newest likes (2 x 2) at the Cover shape;
//   · tone     the accent plate with the heart in the on-accent ink.
//
// The engine's other six treatments (lens, wall, rainbow, marquee, feature, stack) are ambient loops over sixteen tiles with timeline rows: they are Classic's
// picture, not Zune's. A persisted look Zune does not draw reads as the mosaic (`ZunePlaylistRules.LookOf`), so the choice a user made under Classic is kept
// and still shows something honest here. The picker offers Zune's three looks and writes the engine's persisted value (`LikedCoverRules.PickerOrder` index).
//
//   Liked.Cover            the three looks above by the host's EFFECTIVE style (the floors have applied: below the mosaic's four tiles the style is stock).
//   Liked.CoverPicker      the cover PLUS its "cover style" affordance: a strip along the cover's foot, revealed on hover or focus (a BOUND opacity, a compositor
//                          cross-fade on one node: hovering costs no reconcile) and kept up while the flyout is open. It is always mounted.
//   Liked.CoverStyleFlyout the eyebrow, three rows (a 32 miniature of the look and its name, the current one marked with the accent bar) and the note.
//
// Numbers: `Rules\ZunePlaylistRules.cs`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Z = Wavee.Themes.Zune.ZunePlaylistRules;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Liked Songs cover views.</summary>
internal static class ZuneLikedCover
{
    static readonly string s_stockCoverPath = Path.Combine(AppContext.BaseDirectory, "assets", "covers", "liked-songs-300.png");

    // ══ 1. THE COVER ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.LikedCover"/>.</summary>
    public static Element CoverTemplate(LikedCoverProps p, in ViewContext ctx)
        => Look(Z.LookOf(p.Style), p.Tiles, p.Size, p.MorphKey) with { Key = Z.LikedCoverKey };

    /// <summary>One look at one size: stock, the mosaic (stock again below its floor) or the tone plate.</summary>
    static Element Look(LikedCoverStyle look, string[] tiles, float size, string? morphKey)
    {
        float radius = R.Shape.For(ShapeRole.Cover);
        switch (look)
        {
            case LikedCoverStyle.Tone:
                return new BoxEl
                {
                    Direction = 0, Width = size, Height = size, Shrink = 0f, Corners = R.Shape.All(ShapeRole.Cover), Fill = Tok.AccentDefault,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, MorphId = morphKey,
                    Children = [Icon(Icons.HeartFill, size * Z.HeartFraction, Tok.TextOnAccentPrimary)],
                };
            case LikedCoverStyle.Mosaic when tiles.Length >= Z.MosaicFloor:
                return Controls.Mosaic(tiles, size, size, radius) with { MorphId = morphKey };
            default:
                return Image(s_stockCoverPath, size, size, radius, R.Layer.ArtPlaceholder, transition: ImageTransition.None) with { MorphId = morphKey };
        }
    }

    // ══ 2. THE PICKER ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.LikedCoverPicker"/>: the picker is its own component (its state is the hover, the focus and the
    /// open flyout), mounted once per slot; the geometry freezes at mount, like the host's key.</summary>
    public static Element PickerTemplate(LikedCoverPickerProps p, in ViewContext ctx)
        => Embed.Comp(p, static () => new CoverPicker());

    sealed class CoverPicker : Component
    {
        readonly Signal<bool> _hovered = new(false), _focused = new(false), _open = new(false);
        NodeHandle _anchor;
        OverlayHandle? _handle;
        IOverlayService? _overlay;
        Func<Element>? _flyout;
        readonly Action<NodeHandle> _anchorSink;
        readonly Action _toggle, _exit;
        readonly Action<Point2> _hover;
        readonly Action<KeyEventArgs> _key;
        readonly Action<bool> _focus;
        readonly Prop<float> _stripOpacity;

        public CoverPicker()
        {
            _anchorSink = h => _anchor = h;
            _hover = _ => { if (!_hovered.Peek()) _hovered.Value = true; };
            _exit = () => { if (_hovered.Peek()) _hovered.Value = false; };
            _focus = f => { if (_focused.Peek() != f) _focused.Value = f; };
            // It stays lit while the flyout is up, so the affordance does not vanish as the pointer travels into it.
            _stripOpacity = Prop.Of(() => _hovered.Value || _focused.Value || _open.Value ? 1f : 0f);
            _toggle = Toggle;
            // Down / F4 open an anchored surface: the app's one keyboard contract (Space / Enter arrive as OnClick).
            _key = e => { if (e.KeyCode is Keys.Down or Keys.F4) { Toggle(); e.Handled = true; } };
        }

        void Toggle()
        {
            var overlay = _overlay;
            var flyout = _flyout;
            if (Controls.IsNullOverlay(overlay) || flyout is null) return;
            if (_handle is { IsOpen: true } already) { already.Close(); return; }
            // The chrome (plate, stroke, reveal) is the HOST's; FocusTrap keeps the rows' roving inside; ConstrainToRootBounds flips the flyout up near the foot.
            _handle = overlay.Open(
                () => _anchor,
                flyout,
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Flyout)
                { ConstrainToRootBounds = true });
            _open.Value = true;
            _handle.ClosedAction = () => { _handle = null; _open.Value = false; };
        }

        public override Element Render()
        {
            var p = UseProps<LikedCoverPickerProps>();
            _overlay = UseContext(Overlay.Service);
            _flyout = p.Flyout;
            return new BoxEl
            {
                Key = Z.LikedPickerKey, ZStack = true, Width = p.Size, Height = p.Size, Shrink = 0f,
                // Hover is read on the WHOLE cover: the strip must be visible before the pointer can reach it.
                OnHoverMove = _hover, OnPointerExit = _exit,
                Children = [p.Art, Strip(p.Size)],
            };
        }

        /// <summary>The "cover style" strip along the cover's foot: the on-media scrim and the label voice, lowercased as a command.</summary>
        Element Strip(float width) => new BoxEl
        {
            Direction = 0, Width = width, Height = Z.PickerStripH, Shrink = 0f, AlignSelf = FlexAlign.End, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Fill = R.OnMedia.CoverScrim,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = _toggle, OnKeyDown = _key, OnRealized = _anchorSink,
            // FOCUS is part of the reveal: a keyboard user must never land on an invisible control.
            OnFocusChanged = _focus,
            Opacity = _stripOpacity, Transition = MotionTok.ControlNormal,
            Children =
            [
                R.Type.DenseTitle(Loc.Get(Strings.Detail.LikedCover.Pill)) with
                {
                    Size = Z.PickerWord, Color = R.OnMedia.Ink, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap, MinWidth = 0f,
                },
            ],
        };
    }

    // ══ 3. THE FLYOUT ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.LikedCoverStyleFlyout"/>: Zune's three looks as rows. A look the library cannot feed yet shows
    /// what picking it would paint TODAY (the stock cover), dimmed, and stays SELECTABLE.</summary>
    public static Element FlyoutTemplate(LikedCoverStyleFlyoutProps p, in ViewContext ctx)
    {
        var order = LikedCoverRules.PickerOrder;
        var current = Z.LookOf(order[Math.Clamp(p.Selected, 0, order.Length - 1)]);
        var looks = Z.ZuneLooks;
        var kids = new List<Element>(looks.Length + 2)
        {
            new BoxEl
            {
                Key = "liked-flyout:title", Direction = 1, MinWidth = 0f, Padding = new Edges4(Z.FlyoutPad, Z.FlyoutPad, Z.FlyoutPad, 0f),
                Children =
                [
                    R.Type.DenseTitle(Loc.Get(Strings.Detail.LikedCover.Title)) with
                    {
                        Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            },
        };
        foreach (var look in looks) kids.Add(LookRow(p, look, look == current));
        kids.Add(new BoxEl
        {
            Key = "liked-flyout:note", Direction = 1, MinWidth = 0f, Padding = new Edges4(Z.FlyoutPad, 0f, Z.FlyoutPad, Z.FlyoutPad),
            Children =
            [
                R.Type.DenseMeta(Loc.Get(Strings.Detail.LikedCover.Note)) with
                {
                    Size = Z.FlyoutNote, LineHeight = Z.FlyoutNoteLine, Color = Tok.TextTertiary, Wrap = TextWrap.WrapWholeWords, MaxLines = 3,
                    Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        });
        return new BoxEl { Direction = 1, Width = Z.FlyoutW, Padding = Edges4.All(Z.FlyoutPad), Gap = Spacing.XS, Children = kids.ToArray() };
    }

    static Element LookRow(LikedCoverStyleFlyoutProps p, LikedCoverStyle look, bool on)
    {
        bool fed = p.Tiles.Length >= LikedCoverRules.MinTiles(look);
        // morphKey null: three miniatures claiming the hero's shared-element id would hijack a Home to detail fly.
        Element thumb = new BoxEl
        {
            Width = Z.MiniEdge, Height = Z.MiniEdge, Shrink = 0f, HitTestVisible = false, Opacity = fed ? 1f : Z.UnfedOpacity,
            Children = [Look(look, p.Tiles, Z.MiniEdge, morphKey: null)],
        };
        int index = Z.PickerIndexOf(look);
        var apply = p.Apply;
        return new BoxEl
        {
            Key = "liked-look:" + look, Direction = 0, Gap = Z.MiniGap, Height = Z.FlyoutRowH, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Padding = new Edges4(Z.FlyoutPad, 0f, Z.FlyoutPad, 0f),
            Fill = on ? R.Layer.SelectedRest : ColorF.Transparent, HoverFill = on ? R.Layer.SelectedHover : R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            Role = AutomationRole.RadioButton, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = () => apply(index),
            Children =
            [
                thumb,
                R.Type.TrackTitle(Loc.Get(LikedCoverRules.NameKey(look))) with
                {
                    Color = on ? Tok.AccentTextPrimary : Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), Grow = 1f, Basis = 0f, MinWidth = 0f,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }
}
