// ── Themes/Classic/Structure/Views/Liked/ClassicLikedCover.cs — Classic's Liked Songs cover, picker and flyout (themes plan 6.1, T9c) ──
//
// From `Entities\User.Cover.cs` (`LikedCoverArt`, `LikedCoverPicker`, `LikedCoverStyleFlyout`): the pictures. The core hosts keep the snapshot
// (the persisted style after the library's floor, the newest-first distinct tiles, their content key, the count), its equality gate, the
// demand and the persisted style; they mount these keys with the values they read.
//
//   Liked.Cover          the SITE LADDER (`LikedCoverRules.Site`: a 48-DIP Wall is 36 unreadable specks with a loop nobody sees, so below the
//                        floor the answer is the flat 2x2 mosaic; Tone keeps its gradient + heart), the treatments and the AMBIENT LOOPS. The
//                        view owns the loops' hooks, declared unconditionally and in one order on every path, so a hook behind "which
//                        style?" can never shift order the first time a user picks one.
//   Liked.CoverPicker    the cover PLUS its "Cover style" affordance: hovering or focusing the cover reveals an on-media pill whose flyout offers
//                        the nine LIVE miniatures as one radio group. The pill is ALWAYS mounted; its reveal is a BOUND opacity (a compositor
//                        cross-fade on one node), so hovering the cover costs no reconcile.
//   Liked.CoverStyleFlyout  the eyebrow, a 3-wide column-major radio grid of LIVE miniatures (the real treatments in `mini` form: static,
//                        capped cell counts, texture-cache hits at the cover's own decode buckets) and the note.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's Liked Songs cover views.</summary>
internal static class ClassicLikedCover
{
    // ══ 1. THE COVER ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The ambient loops' nodes as ONE slot-owned object: the sinks are allocated once per slot, never per render.</summary>
    sealed class LoopState
    {
        NodeHandle _a, _b, _c;
        public readonly Action<NodeHandle> SinkA, SinkB, SinkC;
        public readonly Action Seed;
        public RenderContext? Hooks;
        public LikedCoverStyle Style;
        public bool HasLoops;

        public LoopState()
        {
            SinkA = h => _a = h;
            SinkB = h => _b = h;
            SinkC = h => _c = h;
            // After realize (the handles are only valid then) and with the scene-liveness check. Loops quiesce by themselves under a parked
            // page; there is nothing to tear down on nav away.
            Seed = () =>
            {
                if (!HasLoops || Hooks?.Anim is not { } anim || Hooks.Scene is not { } scene) return;
                var a = !_a.IsNull && scene.IsLive(_a) ? _a : default;
                var b = !_b.IsNull && scene.IsLive(_b) ? _b : default;
                var c = !_c.IsNull && scene.IsLive(_c) ? _c : default;
                ClassicLikedTreatments.SeedLoops(Style, anim, a, b, c);
            };
        }
    }

    /// <summary>The registered template of <see cref="ViewKeys.LikedCover"/>.</summary>
    public static Element CoverTemplate(LikedCoverProps p, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        var loops = hooks.UseRef(new LoopState()).Value;

        // The AND with the size must survive: a 76-DIP miniature must never allocate three timeline rows.
        bool hasLoops = ClassicLikedTreatments.HasLoops(p.Style) && p.Size >= ClassicLikedTreatments.BadgeMinSize;
        loops.Hooks = hooks;
        loops.Style = p.Style;
        loops.HasLoops = hasLoops;
        // Deps carry the style, the art and the size: a swap or a tile change re-seeds, a repaint does not.
        hooks.UseLayoutEffect(loops.Seed, DepKey.From(HashCode.Combine((int)p.Style, p.TileKey, (int)p.Size, hasLoops)));

        // The site ladder (LikedCoverRules.Site).
        switch (LikedCoverRules.Site(p.Style, p.Size, p.Tiles.Length, ClassicLikedTreatments.BadgeMinSize))
        {
            case LikedCoverSite.Stock:
                return ClassicLikedLeaves.StockCover(p.Size, p.Radius, p.MorphKey);
            case LikedCoverSite.MiniTone:
                return ClassicLikedTreatments.Build(LikedCoverStyle.Tone, p.Tiles, p.TileKey, p.TrackCount, p.Size, p.Radius, mini: true, p.MorphKey);
            case LikedCoverSite.MiniMosaic:
                return Controls.Mosaic(p.Tiles, p.Size, p.Size, p.Radius) with { MorphId = p.MorphKey };
            default:
                return ClassicLikedTreatments.Build(p.Style, p.Tiles, p.TileKey, p.TrackCount, p.Size, p.Radius, mini: false, p.MorphKey,
                    hasLoops ? new ClassicLikedTreatments.Loops(loops.SinkA, loops.SinkB, loops.SinkC) : default);
        }
    }

    // ══ 2. THE PICKER ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.LikedCoverPicker"/>: the picker is its own component (its state is the
    /// hover, the focus and the open flyout), mounted once per slot; the geometry freezes at mount, like the host's key.</summary>
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
        readonly Prop<float> _pillOpacity;

        public CoverPicker()
        {
            _anchorSink = h => _anchor = h;
            _hover = _ => { if (!_hovered.Peek()) _hovered.Value = true; };
            _exit = () => { if (_hovered.Peek()) _hovered.Value = false; };
            _focus = f => { if (_focused.Peek() != f) _focused.Value = f; };
            // It stays lit while the flyout is up, so the affordance does not vanish as the pointer travels into it.
            _pillOpacity = Prop.Of(() => _hovered.Value || _focused.Value || _open.Value ? 1f : 0f);
            _toggle = Toggle;
            // Down / F4 open an anchored surface: the app's one keyboard contract (Space/Enter arrive as OnClick).
            _key = e => { if (e.KeyCode is Keys.Down or Keys.F4) { Toggle(); e.Handled = true; } };
        }

        void Toggle()
        {
            var overlay = _overlay;
            var flyout = _flyout;
            if (Controls.IsNullOverlay(overlay) || flyout is null) return;
            if (_handle is { IsOpen: true } already) { already.Close(); return; }
            // The chrome (acrylic + stroke + elevation + reveal) is the HOST's; FocusTrap keeps the radio roving inside;
            // ConstrainToRootBounds flips the flyout up near the window's foot.
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
                ZStack = true, Width = p.Size, Height = p.Size, Shrink = 0f,
                // Hover is read on the WHOLE cover: the pill must be visible before the pointer can reach it.
                OnHoverMove = _hover, OnPointerExit = _exit,
                Children = [p.Art, Pill()],
            };
        }

        /// <summary>The scrim "Change cover" affordance over the art: a stock <see cref="Button"/> Standard on
        /// <see cref="Controls.OnMediaPalette"/> (Workstream B), 32/r4, not the old 30-capsule hand-rolled pill.</summary>
        Element Pill() => Button.Create(Loc.Get(Strings.Detail.LikedCover.Pill), _toggle, glyph: Icons.Brush,
            style: Button.DefaultStyle(ButtonAppearance.Standard, palette: Controls.OnMediaPalette) with
            {
                MinHeight = Controls.ButtonHeight,
            }) with
        {
            AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End, Margin = ClassicLikedRules.PillMargin, Shrink = 0f,
            OnKeyDown = _key, OnRealized = _anchorSink,
            // FOCUS is part of the reveal: a keyboard user must never land on an invisible control (item 28).
            OnFocusChanged = _focus,
            Opacity = _pillOpacity, Transition = MotionTok.ControlNormal,
        };
    }

    // ══ 3. THE FLYOUT ═════════════════════════════════════════════════════════════════════════════════════════════════════

    const float MiniEdge = 76f;      // PickerCoverMini's 92 minus its 8-per-side resting inset
    const float MiniRadius = 5f;
    const int Columns = 3;

    /// <summary>The registered template of <see cref="ViewKeys.LikedCoverStyleFlyout"/>.</summary>
    public static Element FlyoutTemplate(LikedCoverStyleFlyoutProps p, in ViewContext ctx)
    {
        var order = LikedCoverRules.PickerOrder;
        var cardPad = ClassicLikedRules.FlyoutPad;
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Shrink = 0f, Padding = cardPad,
            // BORDER-box: exactly three cards, two gutters and the inset: no early wrap, no fourth-column slack (332).
            Width = Columns * Controls.PickerCoverMini.Width + (Columns - 1) * Spacing.M + cardPad.Left + cardPad.Right,
            Children =
            [
                R.Type.Eyebrow(Loc.Get(Strings.Detail.LikedCover.Title)) with { Color = Tok.TextPrimary },
                Controls.PickerStrip(order.Length, p.Selected, (i, on) => Mini(p, i, on), p.Apply, maxColumns: Columns),
                new TextEl(Loc.Get(Strings.Detail.LikedCover.Note))
                {
                    Size = ClassicLikedRules.NoteSize, LineHeight = ClassicLikedRules.NoteLine, Color = Tok.TextTertiary,
                    Wrap = TextWrap.WrapWholeWords, MaxLines = 3,
                },
            ],
        };
    }

    /// <summary>Below its floor a style shows what picking it would paint TODAY (the stock cover), dimmed to .45, and stays SELECTABLE
    /// (the delivered E1 deviation: RadioButtons has no per-item enabled state, and an unfed choice lights up by itself when the library
    /// reaches the floor).</summary>
    static Element Mini(LikedCoverStyleFlyoutProps p, int i, bool on)
    {
        var style = LikedCoverRules.PickerOrder[i];
        bool fed = p.Tiles.Length >= LikedCoverRules.MinTiles(style);
        var effective = LikedCoverRules.Effective(style, p.Tiles.Length);
        Element thumb = new BoxEl
        {
            Width = MiniEdge, Height = MiniEdge, Shrink = 0f, HitTestVisible = false, Opacity = fed ? 1f : 0.45f,
            // morphKey null: nine miniatures claiming the hero's shared-element id would hijack a Home to detail fly.
            Children = [ClassicLikedTreatments.Build(effective, p.Tiles, p.TileKey, p.TrackCount, MiniEdge, MiniRadius, mini: true, morphKey: null)],
        };
        return Controls.PickerTitled(Controls.PickerCard(on, Controls.PickerCoverMini, thumb), Loc.Get(LikedCoverRules.NameKey(style)), on);
    }
}
