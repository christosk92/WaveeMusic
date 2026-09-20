using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using Wavee.Backend.Spotify;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Full-window artist-photo lightbox: FlipView paging with auto-hiding floating chrome (counter, export,
/// close, filmstrip) over a pan/zoom surface. Zoom mode REPLACES the FlipView subtree so drag/wheel ownership is never
/// ambiguous. Opened by <see cref="ArtistPage.OpenGallery"/> through the modal overlay — no ContentDialog card. The
/// export action downloads the selected image's original CDN bytes through the native Save As picker.</summary>
sealed class ArtistGalleryLightbox : Component
{
    const float IdleHideMs = 2600f;                 // pointer idle before floating chrome fades
    const float ZoomMax = 4f, ZoomClick = 2.2f, ZoomWheelK = 0.0022f;

    // Top scrim: a black(0.62α)→transparent top-down veil so the white counter/buttons read over any photo. angle 90 =
    // vertical (down); the shader clamps to the first stop before its offset, so no explicit 0-offset transparent stop.
    static readonly GradientSpec TopScrim = GradientDown(
        new GradientStop(0f, ColorF.FromRgba(0, 0, 0, 158)),
        new GradientStop(1f, ColorF.FromRgba(0, 0, 0, 0)));

    readonly IReadOnlyList<Image> _photos;
    readonly int _initialIndex;
    readonly Func<OverlayHandle?> _handle;

    readonly Signal<bool> _saving = new(false);
    readonly Signal<bool> _chrome = new(true);
    readonly Signal<bool> _zoomMode = new(false);

    // pan/zoom live state — screen px, driven directly onto the zoom node's anim channels (not reactive: the component
    // instance is stable across renders, and the values flow to the scene through AnimEngine, not through re-render).
    NodeHandle _zoomNode;
    float _s = 1f, _tx, _ty;
    Point2 _pendingAnchor; bool _hasPendingAnchor;
    readonly Keyframe[] _seedKeys = new Keyframe[2];   // reused per write (FlipView PanMove 0-alloc idiom)
    Point2 _dragLast; Point2 _dragDown; bool _dragMoved;

    // Auto-hiding floating chrome: a frame-clock one-shot Restart()ed on pointer activity (fires → fade the chrome).
    // Generation-guarded, auto-cancels on unmount (replaces the old _idleCts + Task.Delay IdleLoop + UsePost pattern).
    TimerHandle _idleHide;

    // Per-render hand-offs: mount-frozen ctor args can't carry per-render values, so Render() publishes the live index
    // and the UI-thread post dispatcher to the imperative pan/zoom helpers below.
    int _renderCurrent;
    Action<Action> _post = static a => a();

    public ArtistGalleryLightbox(IReadOnlyList<Image> photos, int initialIndex, Func<OverlayHandle?> handle)
    {
        _photos = photos;
        _initialIndex = Math.Clamp(initialIndex, 0, Math.Max(0, photos.Count - 1));
        _handle = handle;
    }

    /// <summary>Overlay Escape veto (change C): zoomed → unzoom and cancel the close; else allow. Returns true when the
    /// Escape was consumed (so <c>ClosingAction</c> should veto the dismissal).</summary>
    public bool TryConsumeEscape()
    {
        if (!_zoomMode.Peek()) return false;
        SpringZoomHome();
        return true;
    }

    /// <summary>Idle-loop teardown, wired from OpenGallery's <c>ClosedAction</c> (UseEffect cleanup does not run inside a
    /// tracked computation, so the overlay drives disposal deterministically on close).</summary>
    public void Cleanup() => _idleHide.Cancel();

    public override Element Render()
    {
        var vp = UseContext(Viewport.Size);
        var (selected, setSelected) = UseState(_initialIndex);
        var post = UsePost();
        int current = Math.Clamp(selected, 0, Math.Max(0, _photos.Count - 1));
        _renderCurrent = current;
        _post = post;
        var photo = _photos[current];
        bool zoomed = _zoomMode.Value;

        // Chrome shows at open, then fades after IdleHideMs of pointer stillness; Poke (pointer activity) re-shows + re-arms.
        _idleHide = UseTimeout(() => { if (_chrome.Peek()) _chrome.Value = false; }, IdleHideMs);

        return new BoxEl
        {
            Grow = 1f, ZStack = true,
            Fill = ColorF.FromRgba(0, 0, 0, 224),          // ~88% scrim over the standard modal smoke
            Focusable = true,
            OnKeyDown = e => OnKeys(e, current, setSelected),
            OnHoverMove = _ => Poke(),
            Children =
            [
                zoomed ? ZoomSurface(photo, vp)
                       : Pager(vp, current, i => { ResetZoomState(); setSelected(i); }),
                TopChrome(vp, current, post),
                zoomed ? new BoxEl() : Filmstrip(vp, current, i => { ResetZoomState(); setSelected(i); }),
            ],
        };
    }

    // ── unzoomed: FlipView pager ─────────────────────────────────────────────────────────────

    Element Pager(Size2 vp, int current, Action<int> onSelect)
    {
        var pages = new Element[_photos.Count];
        for (int i = 0; i < pages.Length; i++)
        {
            pages[i] = new BoxEl
            {
                Width = vp.Width, Height = vp.Height,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                // Ctrl+wheel / touchpad pinch enters zoom; everything else bubbles to FlipView (paging).
                OnPointerWheel = e =>
                {
                    if ((e.Mods & KeyModifiers.Ctrl) == 0) return;
                    e.Handled = true;
                    EnterZoom(e.Local);
                },
                Children = [ HeroImage(_photos[i], vp) ],
            };
        }
        return FlipView.Create(pages, vp.Width, vp.Height, new Signal<int>(current), onChange: onSelect);
    }

    Element HeroImage(Image photo, Size2 vp) => new ImageEl
    {
        Key = "gallery-image:" + photo.Url,
        Source = photo.Url, Width = vp.Width, Height = vp.Height,
        Fit = ImageFit.Contain,
        DecodePx = MathF.Min(MathF.Max(vp.Width, vp.Height), 2048f),
        Placeholder = ColorF.FromRgba(0, 0, 0, 0),
        BlurHash = photo.BlurHash,
        RevealTransition = ImageTransition.Fade(140f),
    };

    // ── zoomed: pan/zoom surface (FlipView unmounted — no input arbitration) ────────────────

    Element ZoomSurface(Image photo, Size2 vp) => new BoxEl
    {
        Width = vp.Width, Height = vp.Height, ClipToBounds = true,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Cursor = CursorId.Hand,
        OnPointerDown = lp => { _dragLast = lp; _dragDown = lp; _dragMoved = false; },
        OnDrag = lp =>
        {
            float dx = lp.X - _dragLast.X, dy = lp.Y - _dragLast.Y;
            // 4px slop (the engine drag-box convention) so a jittery click still reads as click-to-unzoom
            if (MathF.Abs(lp.X - _dragDown.X) + MathF.Abs(lp.Y - _dragDown.Y) > 4f) _dragMoved = true;
            _dragLast = lp;
            _tx += dx; _ty += dy;
            ClampPan(photo, vp);
            WriteTransform();
        },
        OnClick = () => { if (!_dragMoved) SpringZoomHome(); },
        OnPointerWheel = e =>
        {
            e.Handled = true;
            if ((e.Mods & KeyModifiers.Ctrl) != 0)
            {
                float s1 = _s * MathF.Exp(-e.Delta * ZoomWheelK);
                if (s1 <= 1.02f) { SpringZoomHome(); return; }
                ZoomAt(e.Local, s1, photo, vp);
                WriteTransform();
            }
            else
            {
                _tx -= e.DeltaX; _ty -= e.Delta;   // two-finger pan (sign matches the drag convention)
                ClampPan(photo, vp);
                WriteTransform();
            }
        },
        Children =
        [
            new BoxEl   // the transformed carrier — handlers live on the STATIC outer frame above
            {
                Width = vp.Width, Height = vp.Height,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                OnRealized = nh => { _zoomNode = nh; SeedEnterZoom(vp); },
                Children = [ HeroImage(photo, vp) ],
            },
        ],
    };

    void EnterZoom(Point2 anchor)
    {
        _pendingAnchor = anchor; _hasPendingAnchor = true;
        _s = 1f; _tx = 0f; _ty = 0f;
        _zoomMode.Value = true;    // remounts the subtree; SeedEnterZoom fires on the carrier's OnRealized
    }

    void SeedEnterZoom(Size2 vp)
    {
        WriteTransform();          // seed the channels at identity so the spring has a from-value
        var photo = _photos[Math.Clamp(_renderCurrent, 0, _photos.Count - 1)];
        Point2 a = _hasPendingAnchor ? _pendingAnchor : new Point2(vp.Width * 0.5f, vp.Height * 0.5f);
        _hasPendingAnchor = false;
        ZoomAt(a, ZoomClick, photo, vp);
        SpringChannels();          // spring from the seeded identity to the anchored target
    }

    void ZoomAt(Point2 local, float s1, Image photo, Size2 vp)
    {
        s1 = Math.Clamp(s1, 1f, ZoomMax);
        float dx = local.X - vp.Width * 0.5f, dy = local.Y - vp.Height * 0.5f;
        float k = s1 / _s;
        _tx = dx - (dx - _tx) * k;
        _ty = dy - (dy - _ty) * k;
        _s = s1;
        ClampPan(photo, vp);
    }

    void ClampPan(Image photo, Size2 vp)
    {
        (float cw, float ch) = ContainSize(photo, vp);
        float maxX = MathF.Max(0f, (cw * _s - vp.Width) * 0.5f);
        float maxY = MathF.Max(0f, (ch * _s - vp.Height) * 0.5f);
        _tx = Math.Clamp(_tx, -maxX, maxX);
        _ty = Math.Clamp(_ty, -maxY, maxY);
    }

    static (float W, float H) ContainSize(Image photo, Size2 vp)
    {
        float aspect = photo.Width is int w && photo.Height is int h && h > 0 ? (float)w / h : 1f;
        float cw = vp.Width, chh = cw / aspect;
        if (chh > vp.Height) { chh = vp.Height; cw = chh * aspect; }
        return (cw, chh);
    }

    /// <summary>Per-frame direct write — the reused 2-key seed replaces the running track without allocation. The anim
    /// slab composes Translation(Tx,Ty) * Scale(Sx,Sy) about the node origin, so tx/ty stay in screen px.</summary>
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
        var sp = SpringParams.FromResponse(0.32f, 0.9f);
        anim.Spring(_zoomNode, AnimChannel.ScaleX, _s, sp);
        anim.Spring(_zoomNode, AnimChannel.ScaleY, _s, sp);
        anim.Spring(_zoomNode, AnimChannel.TranslateX, _tx, sp);
        anim.Spring(_zoomNode, AnimChannel.TranslateY, _ty, sp);
    }

    void SpringZoomHome()
    {
        _s = 1f; _tx = 0f; _ty = 0f;
        SpringChannels();
        // let the spring visibly settle, then swap back to the FlipView (post to the UI thread)
        var post = _post;
        _ = Task.Delay(260).ContinueWith(_ => post(() => _zoomMode.Value = false));
    }

    void ResetZoomState() { _s = 1f; _tx = 0f; _ty = 0f; if (_zoomMode.Peek()) _zoomMode.Value = false; }

    // ── keys ─────────────────────────────────────────────────────────────────────────────────

    void OnKeys(KeyEventArgs e, int current, Action<int> setSelected)
    {
        if (e.Handled) return;
        // Unzoomed arrows are handled by the focused FlipView; this catches root-focus + zoom mode (unzoom + navigate).
        if (e.KeyCode is Keys.Left or Keys.Right)
        {
            int next = Math.Clamp(current + (e.KeyCode == Keys.Right ? 1 : -1), 0, _photos.Count - 1);
            if (next != current) { ResetZoomState(); setSelected(next); }
            e.Handled = true;
        }
    }

    // ── chrome ───────────────────────────────────────────────────────────────────────────────

    Element TopChrome(Size2 vp, int current, Action<Action> post) => new BoxEl
    {
        Height = 72f,   // explicit height + NaN width fills the ZStack width; AlignSelf(Auto) anchors it to the top
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
        Gradient = TopScrim,
        Opacity = _chrome.Value ? 1f : 0f,
        Transition = MotionTok.ControlNormal,
        Children =
        [
            new BoxEl
            {
                Direction = 1, Gap = 2f,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Artist.Gallery)) { Size = 14f, Weight = 600, Color = Tok.TextPrimary },
                    new TextEl($"{current + 1} / {_photos.Count}") { Size = 12f, Color = Tok.TextSecondary },
                ],
            },
            new BoxEl { Grow = 1f },
            Button.Accent(_saving.Value ? "Exporting…" : "Export image",
                () => ExportImage(_photos[current], current, post), isEnabled: !_saving.Value),
            Button.Standard(Loc.Get(Strings.Auth.Close), () => _handle()?.Close()),
        ],
    };

    Element Filmstrip(Size2 vp, int current, Action<int> setSelected)
    {
        const float Thumb = 56f, Pad = 14f;
        var thumbs = new Element[_photos.Count];
        for (int i = 0; i < thumbs.Length; i++)
        {
            int idx = i;
            bool active = i == current;
            thumbs[i] = new BoxEl
            {
                Width = Thumb, Height = Thumb, Shrink = 0f,
                Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
                BorderWidth = 2f,
                BorderColor = active ? Tok.AccentTextPrimary : ColorF.FromRgba(0, 0, 0, 0),
                Opacity = active ? 1f : 0.55f,
                Transition = MotionTok.ControlFast,
                OnClick = () => setSelected(idx),
                Cursor = CursorId.Hand, HoverScale = WaveeMotion.ScaleStandard.Hover,
                Children = [ Surfaces.Artwork(_photos[idx], idx, Thumb, Thumb, Radii.Control, decodePx: 128) ],
            };
        }
        return new BoxEl
        {
            // explicit height + AlignSelf.End anchors the strip to the window bottom; content centers horizontally
            Height = Thumb + 2 * Pad, AlignSelf = FlexAlign.End,
            Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Padding = Edges4.All(Pad),
            Opacity = _chrome.Value ? 1f : 0f,
            Transition = MotionTok.ControlNormal,
            Children = thumbs,
        };
    }

    // ── idle-hide (frame-clock UseTimeout; Restart on pointer activity) ────────────

    void Poke()
    {
        if (!_chrome.Peek()) _chrome.Value = true;
        _idleHide.Restart();   // re-arm the fade countdown from now
    }

    // ── export: moved verbatim from ArtistGalleryViewer ──────────────────────────────────────

    async void ExportImage(Image photo, int index, Action<Action> post)
    {
        if (_saving.Peek() || photo.Url.Length == 0) return;
        string ext = ExtensionOf(photo.Url);
        string? path = FilePicker.SaveFile(FluentApp.WindowHandle, "Export gallery image",
            $"artist-gallery-{index + 1:D2}{ext}",
            ("Images", "*.jpg;*.jpeg;*.png;*.webp"), ("All files", "*.*"));
        if (path is null) return;

        _saving.Value = true;
        try
        {
            using var response = await HttpPools.Get(HttpPool.Cdn).GetAsync(photo.Url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            post(() =>
            {
                _saving.Value = false;
                Toast.Show("Image exported", new ToastOptions { Severity = InfoBarSeverity.Success });
            });
        }
        catch (Exception ex)
        {
            post(() =>
            {
                _saving.Value = false;
                Toast.Show("Image export failed: " + ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error });
            });
        }
    }

    static string ExtensionOf(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg" or ".png" or ".webp") return ext;
        }
        return ".jpg";
    }
}
