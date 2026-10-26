// ── Themes/Classic/Structure/Views/Pages/ClassicArtistLightbox.cs — Classic's `Artist.Lightbox` (themes plan 6.1, T10b2) ──────────────
//
// From `Entities\Artist.UI.cs` §4.2 (`GalleryLightbox`, ArtistGalleryLightbox.cs, W21): the full-window gallery viewer over the black ground.
//
//   Artist.Lightbox  (in the core's modal overlay: focus-trapped, no light dismiss; Escape closes unless zoomed)
//   └─ ZStack  (Focusable: Left / Right step the photo; any pointer move wakes the chrome)
//      ├─ FlipView pager (a page per photo; Ctrl+wheel / a pinch enters zoom)  |  the pan/zoom surface (it REPLACES the pager)
//      ├─ the top chrome: [Gallery / "n / N"] · spacer · Export · Close, over its scrim, hiding after 2600 ms of stillness
//      └─ the filmstrip (hidden while zoomed)
//
// The pan/zoom mode REPLACES the pager and the filmstrip subtrees so drag/wheel ownership is never ambiguous; the transform is written straight onto the
// carrier's anim channels (screen px, never through a render) and springs home. The unzoom settle is a frame-clock timeout. What stays core
// (`Artist.GalleryOpen`): the overlay, the selected index, the close, the Escape veto's wiring and the export (the picker, the bytes, the notice).
//
// Numbers are `Rules\ClassicArtistLightboxRules.cs` (WVTH003 allows no literal look here).

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Rules = Wavee.Themes.Classic.ClassicArtistLightboxRules;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's gallery lightbox.</summary>
internal static class ClassicArtistLightbox
{
    /// <summary>The registered template of <see cref="ViewKeys.ArtistLightbox"/>: the viewer component (one per open; its props never change).</summary>
    public static Element Template(ArtistLightboxProps p, in ViewContext ctx) => Embed.Comp(() => new Viewer(p));

    /// <summary>FlipView paging under auto-hiding floating chrome (2600 ms of pointer stillness), a filmstrip, and a real
    /// pan/zoom mode that REPLACES the pager and the filmstrip subtrees so drag/wheel ownership is never ambiguous. The
    /// photo list is mount-stable by construction (the overlay mounts one instance per open). The unzoom settle is a
    /// frame-clock <c>UseTimeout</c> (ch 08 §5), and every string goes through its key (ch 08 §6).</summary>
    sealed class Viewer : Component
    {
        readonly ArtistLightboxProps _p;
        readonly string[] _photos;
        readonly Signal<bool> _chrome = new(true);
        readonly Signal<bool> _zoomMode = new(false);

        // pan/zoom live state — screen px written straight onto the carrier's anim channels, never through a render
        NodeHandle _zoomNode;
        float _s = 1f, _tx, _ty;
        Point2 _pendingAnchor;
        bool _hasPendingAnchor;
        readonly Keyframe[] _seedKeys = new Keyframe[2];
        Point2 _dragLast, _dragDown;
        bool _dragMoved;
        TimerHandle _idleHide, _unzoom;
        /// <summary>The tiers this viewer is drawn in (its root's facts, read in render), never the process's last window.</summary>
        global::Wavee.Theming.TierFacts _tiers;

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
            _tiers = global::Wavee.Theming.TierContext.DrawnIn(UseContext(global::Wavee.Theming.ThemeFactsContext.Signal)).Value.Tiers;
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
                // The viewport's size, not Grow alone: the modal host does not stretch its content, so the black ground painted nothing and the
                // chrome sat on the page's ground (white in light mode) instead.
                Grow = 1f, Width = vp.Width, Height = vp.Height, ZStack = true, Fill = Rules.Scrim, Focusable = true,
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
                    Width = vp.Width, Height = vp.Height, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Fill = Rules.PageGround,
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
                if (MathF.Abs(lp.X - _dragDown.X) + MathF.Abs(lp.Y - _dragDown.Y) > Rules.DragSlop) _dragMoved = true;   // 4-px slop
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
                    TransformOriginX = 0.5f, TransformOriginY = 0.5f,
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
            Point2 a = _hasPendingAnchor ? _pendingAnchor : new Point2(vp.Width * 0.5f, vp.Height * 0.5f);
            _hasPendingAnchor = false;
            ZoomAt(a, Rules.ZoomClick, vp);
            SpringChannels();
        }

        void ZoomAt(Point2 local, float s1, Size2 vp)
        {
            s1 = Math.Clamp(s1, 1f, Rules.ZoomMax);
            float dx = local.X - vp.Width * 0.5f, dy = local.Y - vp.Height * 0.5f;
            float k = s1 / _s;
            _tx = dx - (dx - _tx) * k;
            _ty = dy - (dy - _ty) * k;
            _s = s1;
            ClampPan(vp);
        }

        /// <summary>The pan bound. The gallery edge carries no pixel dimensions, so the contain box is taken as the
        /// viewport's shorter side squared — a bound that never lets the photo leave the window.</summary>
        void ClampPan(Size2 vp)
        {
            float edge = MathF.Min(vp.Width, vp.Height);
            float maxX = MathF.Max(0f, (edge * _s - vp.Width) * 0.5f);
            float maxY = MathF.Max(0f, (edge * _s - vp.Height) * 0.5f);
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

        /// <summary>ONE 72-DIP row: [Gallery / "n / N"] · spacer · Export · Close (ch 08 audit #3).</summary>
        Element TopChrome(int current, Action<Action> post)
        {
            bool saving = _p.Saving.Value;
            // The window tier's targets (TC6, built from Classic's parts: no board draws the lightbox): Export and Close are 40 tall at Compact.
            var size = ClassicArtistTiers.Of(_tiers).Target >= ClassicArtistTiers.CompactTarget ? ControlSize.Large : ControlSize.Medium;
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
                        Direction = 1, Gap = Rules.TitleGap,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Artist.Gallery)) { Size = Rules.TitleSize, Weight = Rules.TitleWeight, Color = R.OnMedia.Ink },
                            new TextEl((current + 1).ToString(CultureInfo.CurrentCulture) + " / "
                                       + _photos.Length.ToString(CultureInfo.CurrentCulture)) { Size = Rules.CountSize, Color = R.OnMedia.InkSecondary },
                        ],
                    },
                    new BoxEl { Grow = 1f },
                    Button.Create(Loc.Get(saving ? Strings.Artist.GalleryExporting : Strings.Artist.GalleryExport),
                        () => _p.Export(current, post), ButtonAppearance.Accent, size: size, isEnabled: !saving),
                    Button.Create(Loc.Get(Strings.Common.Close), _p.Close, ButtonAppearance.Standard, size: size),
                ],
            };
        }

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
                    Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
                    BorderWidth = Rules.ThumbBorder, BorderColor = active ? Tok.AccentTextPrimary : Rules.Clear,
                    Opacity = active ? 1f : Rules.ThumbIdleOpacity, Transition = MotionTok.ControlFast,
                    OnClick = () => setSelected(idx), Cursor = CursorId.Hand, Role = AutomationRole.Button,
                    HoverScale = R.Motion.ScaleStandard.Hover,
                    Children = [Controls.Artwork(_photos[idx], Rules.Thumb, Rules.Thumb, Radii.Control, decodePx: Rules.ThumbDecode)],
                };
            }
            // The strip scrolls sideways where the thumbs outrun the window (a narrow window, a long gallery) instead of running off it; centred when they fit.
            Element row = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Padding = Edges4.All(Rules.StripPad), Children = thumbs,
            };
            return new BoxEl
            {
                // Along the viewer's foot (the pre-theme lightbox's place): stretched, the ZStack laid it over the top chrome's title.
                Height = Rules.Thumb + 2f * Rules.StripPad, AlignSelf = FlexAlign.End, MinWidth = 0f,
                Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Opacity = _chrome.Value ? 1f : 0f, Transition = MotionTok.ControlNormal,
                Children =
                [
                    ScrollView(new BoxEl { Direction = 0, Children = [row] }, horizontal: true) with
                    {
                        ContentSized = true, Shrink = 1f, MinWidth = 0f, Height = Rules.Thumb + 2f * Rules.StripPad, SuppressScrollBar = true, AutoEdgeFade = true,
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
