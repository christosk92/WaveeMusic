// ── Themes/Zune/Structure/Views/Artist/ZuneArtistLightbox.cs — Zune's `Artist.Lightbox` (themes plan 6.3; P5 Z6a2; no board: built from Zune's parts, Q18) ──
//
// The full-window gallery viewer over the black ground (the modal the core's `Artist.GalleryOpen` opens: focus-trapped, no light dismiss; Escape closes
// unless zoomed).
//
//   Artist.Lightbox
//   └─ ZStack  (Focusable: Left / Right step the photo; any pointer move wakes the chrome)
//      ├─ FlipView pager (a page per photo; Ctrl+wheel / a pinch enters zoom)  |  the pan/zoom surface (it REPLACES the pager)
//      ├─ the top chrome: ["gallery" / "n / N"] · spacer · the export pill · the close ring, over its scrim, hiding after 2600 ms of stillness
//      └─ the filmstrip (hidden while zoomed): 56 squares at the cover radius, the current one in the accent
//
// Zune's parts: the title is the track-title voice in the label case, the count the dense meta voice, "export image" an outlined pill in the
// command case (the accent ink while it is the busy "exporting…"), the close a 36 ring on a 40 target. The pan/zoom mode REPLACES the pager and the
// filmstrip subtrees so drag/wheel ownership is never ambiguous; the transform is written straight onto the carrier's anim channels (screen px,
// never through a render) and springs home. The unzoom settle is a frame-clock timeout. What stays core (`Artist.GalleryOpen`): the overlay, the
// selected index, the close, the Escape veto's wiring and the export (the picker, the bytes, the notice).

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using Rules = Wavee.Themes.Zune.ZuneArtistRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;
using FluentGpu.Theming;

namespace Wavee.Themes.Zune;

/// <summary>Zune's gallery lightbox.</summary>
internal static class ZuneArtistLightbox
{
    /// <summary>The registered template of <see cref="ViewKeys.ArtistLightbox"/>: the viewer component (one per open; its props never change).</summary>
    public static Element Template(ArtistLightboxProps p, in ViewContext ctx) => Embed.Comp(() => new Viewer(p));

    /// <summary>FlipView paging under auto-hiding floating chrome, a filmstrip, and a real pan/zoom mode that REPLACES the pager and the filmstrip
    /// subtrees. The photo list is mount-stable by construction (the overlay mounts one instance per open).</summary>
    sealed class Viewer : Component
    {
        readonly ArtistLightboxProps _p;
        readonly string[] _photos;
        readonly Signal<bool> _chrome = new(true);
        readonly Signal<bool> _zoomMode = new(false);

        // pan/zoom live state: screen px written straight onto the carrier's anim channels, never through a render
        NodeHandle _zoomNode;
        float _s = 1f, _tx, _ty;
        Point2 _pendingAnchor;
        bool _hasPendingAnchor;
        readonly Keyframe[] _seedKeys = new Keyframe[2];
        Point2 _dragLast, _dragDown;
        bool _dragMoved;
        TimerHandle _idleHide, _unzoom;
        /// <summary>The tiers this viewer is drawn in (its root's facts, read in render), never the process's last window.</summary>
        TierFacts _tiers;

        public Viewer(ArtistLightboxProps p)
        {
            _p = p;
            _photos = p.Photos;
            // The overlay asks this viewer whether Escape was consumed (a zoomed photo unzooms and VETOES the close), and its close stops the timers.
            p.Attach(TryConsumeEscape, Cleanup);
        }

        /// <summary>Zoomed ⇒ unzoom and report the Escape consumed (the overlay vetoes its close).</summary>
        bool TryConsumeEscape()
        {
            if (!_zoomMode.Peek()) return false;
            SpringZoomHome();
            return true;
        }

        void Cleanup()
        {
            _idleHide.Cancel();
            _unzoom.Cancel();
        }

        public override Element Render()
        {
            var vp = UseContext(Viewport.Size);
            _tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            int selected = _p.Selected.Value;
            Action<int> setSelected = _p.Select;
            var post = UsePost();
            int current = Math.Clamp(selected, 0, Math.Max(0, _photos.Length - 1));
            bool zoomed = _zoomMode.Value;
            _idleHide = UseTimeout(() => { if (_chrome.Peek()) _chrome.Value = false; }, Rules.IdleHideMs);
            // Swap back to the pager only once the spring has visibly settled, and only if nobody zoomed in again.
            _unzoom = UseTimeout(() => { if (_zoomMode.Peek() && _s <= Rules.ZoomRest) _zoomMode.Value = false; }, Rules.UnzoomSettleMs);

            return new BoxEl
            {
                Key = Rules.LightboxKey, Grow = 1f, ZStack = true, Fill = Rules.Scrim, Focusable = true,
                OnKeyDown = e => OnKeys(e, current, setSelected),
                OnHoverMove = _ => Poke(),
                Children =
                [
                    zoomed ? ZoomSurface(_photos[current], vp) : Pager(vp, current, i => { ResetZoomState(); setSelected(i); }),
                    TopChrome(current, post),
                    zoomed ? new BoxEl() : Filmstrip(current, i => { ResetZoomState(); setSelected(i); }),
                ],
            };
        }

        Element Pager(Size2 vp, int current, Action<int> onSelect)
        {
            var pages = new Element[_photos.Length];
            for (int i = 0; i < pages.Length; i++)
                pages[i] = new BoxEl
                {
                    Width = vp.Width, Height = vp.Height, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    // Ctrl+wheel / a touchpad pinch enters zoom; everything else bubbles to the FlipView.
                    OnPointerWheel = e =>
                    {
                        if ((e.Mods & KeyModifiers.Ctrl) == 0) return;
                        e.Handled = true;
                        EnterZoom(e.Local);
                    },
                    Children = [Photo(_photos[i], vp)],
                };
            return FlipView.Create(pages, vp.Width, vp.Height, new Signal<int>(current), onChange: onSelect);
        }

        /// <summary>The photo's placeholder is fully TRANSPARENT, so a still-decoding frame shows the black ground.</summary>
        static Element Photo(string url, Size2 vp) => new ImageEl
        {
            Key = "gallery-image:" + url,
            Source = url, Width = vp.Width, Height = vp.Height, Fit = ImageFit.Contain,
            DecodePx = MathF.Min(MathF.Max(vp.Width, vp.Height), Design.ImageDecodeScale.Ceiling),
            Placeholder = Rules.Clear,
            RevealTransition = ImageTransition.Fade(Rules.PhotoFadeMs),
        };

        Element ZoomSurface(string url, Size2 vp) => new BoxEl
        {
            Width = vp.Width, Height = vp.Height, ClipToBounds = true,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Cursor = CursorId.Hand,
            OnPointerDown = lp => { _dragLast = lp; _dragDown = lp; _dragMoved = false; },
            OnDrag = lp =>
            {
                float dx = lp.X - _dragLast.X, dy = lp.Y - _dragLast.Y;
                if (MathF.Abs(lp.X - _dragDown.X) + MathF.Abs(lp.Y - _dragDown.Y) > Rules.DragSlop) _dragMoved = true;
                _dragLast = lp;
                _tx += dx;
                _ty += dy;
                ClampPan(vp);
                WriteTransform();
            },
            OnClick = () => { if (!_dragMoved) SpringZoomHome(); },
            OnPointerWheel = e =>
            {
                e.Handled = true;
                if ((e.Mods & KeyModifiers.Ctrl) != 0)
                {
                    float s1 = _s * MathF.Exp(-e.Delta * Rules.ZoomWheelK);
                    if (s1 <= Rules.ZoomFloor) { SpringZoomHome(); return; }
                    ZoomAt(e.Local, s1, vp);
                    WriteTransform();
                }
                else
                {
                    _tx -= e.DeltaX;
                    _ty -= e.Delta;
                    ClampPan(vp);
                    WriteTransform();
                }
            },
            Children =
            [
                new BoxEl
                {
                    Width = vp.Width, Height = vp.Height, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    TransformOriginX = Rules.Half, TransformOriginY = Rules.Half,
                    OnRealized = nh => { _zoomNode = nh; SeedEnterZoom(vp); },
                    Children = [Photo(url, vp)],
                },
            ],
        };

        void EnterZoom(Point2 anchor)
        {
            _pendingAnchor = anchor;
            _hasPendingAnchor = true;
            _s = 1f; _tx = 0f; _ty = 0f;
            _zoomMode.Value = true;
        }

        void SeedEnterZoom(Size2 vp)
        {
            WriteTransform();
            Point2 a = _hasPendingAnchor ? _pendingAnchor : new Point2(vp.Width * Rules.Half, vp.Height * Rules.Half);
            _hasPendingAnchor = false;
            ZoomAt(a, Rules.ZoomClick, vp);
            SpringChannels();
        }

        void ZoomAt(Point2 local, float s1, Size2 vp)
        {
            s1 = Math.Clamp(s1, 1f, Rules.ZoomMax);
            float dx = local.X - vp.Width * Rules.Half, dy = local.Y - vp.Height * Rules.Half;
            float k = s1 / _s;
            _tx = dx - (dx - _tx) * k;
            _ty = dy - (dy - _ty) * k;
            _s = s1;
            ClampPan(vp);
        }

        /// <summary>The pan bound. The gallery edge carries no pixel dimensions, so the contain box is taken as the viewport's shorter side squared:
        /// a bound that never lets the photo leave the window.</summary>
        void ClampPan(Size2 vp)
        {
            float edge = MathF.Min(vp.Width, vp.Height);
            float maxX = MathF.Max(0f, (edge * _s - vp.Width) * Rules.Half);
            float maxY = MathF.Max(0f, (edge * _s - vp.Height) * Rules.Half);
            _tx = Math.Clamp(_tx, -maxX, maxX);
            _ty = Math.Clamp(_ty, -maxY, maxY);
        }

        void WriteTransform()
        {
            var anim = Context.Anim;
            if (anim is null || _zoomNode.IsNull) return;
            Seed(anim, AnimChannel.ScaleX, _s);
            Seed(anim, AnimChannel.ScaleY, _s);
            Seed(anim, AnimChannel.TranslateX, _tx);
            Seed(anim, AnimChannel.TranslateY, _ty);
        }

        void Seed(AnimEngine anim, AnimChannel ch, float v)
        {
            _seedKeys[0] = new Keyframe(0f, v, Easing.Linear);
            _seedKeys[1] = new Keyframe(1f, v, Easing.Linear);
            anim.Keyframes(_zoomNode, ch, _seedKeys, 1f);
        }

        void SpringChannels()
        {
            var anim = Context.Anim;
            if (anim is null || _zoomNode.IsNull) return;
            var sp = SpringParams.FromResponse(Rules.SpringResponse, Rules.SpringDamping);
            anim.Spring(_zoomNode, AnimChannel.ScaleX, _s, sp);
            anim.Spring(_zoomNode, AnimChannel.ScaleY, _s, sp);
            anim.Spring(_zoomNode, AnimChannel.TranslateX, _tx, sp);
            anim.Spring(_zoomNode, AnimChannel.TranslateY, _ty, sp);
        }

        void SpringZoomHome()
        {
            _s = 1f; _tx = 0f; _ty = 0f;
            SpringChannels();
            _unzoom.Restart();
        }

        void ResetZoomState()
        {
            _s = 1f; _tx = 0f; _ty = 0f;
            if (_zoomMode.Peek()) _zoomMode.Value = false;
        }

        void OnKeys(KeyEventArgs e, int current, Action<int> setSelected)
        {
            if (e.Handled) return;
            if (e.KeyCode is Keys.Left or Keys.Right)
            {
                int next = Math.Clamp(current + (e.KeyCode == Keys.Right ? 1 : -1), 0, _photos.Length - 1);
                if (next != current) { ResetZoomState(); setSelected(next); }
                e.Handled = true;
            }
        }

        /// <summary>ONE row: ["gallery" / "n / N"] · spacer · the export pill · the close ring.</summary>
        Element TopChrome(int current, Action<Action> post)
        {
            bool saving = _p.Saving.Value;
            float target = ZuneArtistTiers.Of(in _tiers).Target;
            return new BoxEl
            {
                Height = Rules.ChromeH, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
                Gradient = Rules.TopScrim,
                Opacity = _chrome.Value ? 1f : 0f, Transition = MotionTok.ControlNormal,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = Rules.ChromeTitleGap,
                        Children =
                        [
                            R.Type.TrackTitle(Loc.Get(Strings.Artist.Gallery)) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1 },
                            R.Type.DenseMeta((current + 1).ToString(CultureInfo.CurrentCulture) + " / " + _photos.Length.ToString(CultureInfo.CurrentCulture))
                                with { Color = Tok.TextSecondary, MaxLines = 1 },
                        ],
                    },
                    new BoxEl { Grow = 1f },
                    ExportPill(Loc.Get(saving ? Strings.Artist.GalleryExporting : Strings.Artist.GalleryExport), saving, () => _p.Export(current, post), target),
                    Controls.Named(new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shrink = 0f, MinWidth = target, MinHeight = target,
                        Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                        OnClick = _p.Close, Children = [K.RingDisc(Icons.ChromeClose, primary: false)],
                    }, Loc.Get(Strings.Common.Close)),
                ],
            };
        }

        /// <summary>The export pill: a 2-dip outline in the primary ink and its word in the command case; dimmed and inert while it exports.</summary>
        static Element ExportPill(string label, bool busy, Action export, float target) => new BoxEl
        {
            Key = "zune-lightbox-export", Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shrink = 0f, MinHeight = target,
            Padding = new Edges4(Rules.PillPadX, 0f, Rules.PillPadX, 0f), Corners = CornerRadius4.All(Radii.Full),
            BorderWidth = Rules.PillBorder, BorderColor = Tok.TextPrimary, Opacity = busy ? Rules.ThumbIdleOpacity : 1f,
            HoverFill = busy ? ColorF.Transparent : R.Layer.ChromeHover, PressedFill = busy ? ColorF.Transparent : R.Layer.ChromePressed,
            Cursor = busy ? null : CursorId.Hand, Role = AutomationRole.Button, Focusable = !busy, AllowFocusOnInteraction = false,
            OnClick = busy ? null : export,
            Children =
            [
                R.Type.TrackTitle(label) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap },
            ],
        };

        Element Filmstrip(int current, Action<int> setSelected)
        {
            var thumbs = new Element[_photos.Length];
            for (int i = 0; i < thumbs.Length; i++)
            {
                int idx = i;
                bool active = i == current;
                thumbs[i] = new BoxEl
                {
                    Width = Rules.Thumb, Height = Rules.Thumb, Shrink = 0f,
                    Corners = CornerRadius4.All(R.Shape.Cover), ClipToBounds = true,
                    BorderWidth = Rules.ThumbBorder, BorderColor = active ? Prop.Of(static () => R.Ink.AccentAction) : Rules.Clear,
                    Opacity = active ? 1f : Rules.ThumbIdleOpacity, Transition = MotionTok.ControlFast,
                    OnClick = () => setSelected(idx), Cursor = CursorId.Hand, Role = AutomationRole.Button,
                    Children = [Controls.Artwork(_photos[idx], Rules.Thumb, Rules.Thumb, R.Shape.Cover, decodePx: Rules.ThumbDecode)],
                };
            }
            // The strip scrolls sideways where the thumbs outrun the window instead of running off it; centred when they fit.
            Element row = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Padding = Edges4.All(Rules.StripPad), Children = thumbs,
            };
            return new BoxEl
            {
                Height = Rules.Thumb + Rules.StripPad + Rules.StripPad, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Opacity = _chrome.Value ? 1f : 0f, Transition = MotionTok.ControlNormal,
                Children =
                [
                    ScrollView(new BoxEl { Direction = 0, Children = [row] }, horizontal: true) with
                    {
                        ContentSized = true, Shrink = 1f, MinWidth = 0f, Height = Rules.Thumb + Rules.StripPad + Rules.StripPad,
                        SuppressScrollBar = true, AutoEdgeFade = true,
                    },
                ],
            };
        }

        void Poke()
        {
            if (!_chrome.Peek()) _chrome.Value = true;
            _idleHide.Restart();
        }
    }
}
