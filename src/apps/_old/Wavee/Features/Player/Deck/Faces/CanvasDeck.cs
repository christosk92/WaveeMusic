using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Features.Player.Deck.Model;

namespace Wavee;

/// <summary>
/// Canvas drift: the cover, blown out and blurred behind itself, with a slow Ken Burns pan-and-zoom over a framed
/// copy in the middle.
///
/// <para><b>The drift is not a tick.</b> Every other deck folds physics 30 times a second; this one hands the whole
/// motion to the animation slab as ONE looping keyframe track per channel and then does nothing at all until a gate
/// flips. That is why <c>DeckModels</c> gives it a <c>ProgressModel</c>: the only per-tick value on this face is the
/// seek line's fraction.</para>
///
/// <para><b>The gates are a value, never a hook branch.</b> Playing, the drift speed, reduced motion and the
/// component's own activation fold into one bool that is part of the layout effect's dep key — so the tracks are
/// seeded once, replaced in place on a flip, and CANCELLED (freezing the drift where it stands, the way pausing a
/// CSS animation does) whenever the deck should be still. A loop that outlived its gate would keep the frame loop
/// awake forever.</para>
///
/// <para><b>A new album is a remount, not a swap.</b> The artwork subtree is keyed on <c>DeckSignals.CoverGen</c>,
/// so the whole canvas cross-fades in over 300 ms and the drift re-seeds against the new node.</para>
/// </summary>
static class CanvasDeck
{
    public static Element Build(in NpvPlayerCatalog.Preset preset, IAppSettings? settings, float side,
                                DeckSignals sig, PlaybackBridge bridge, DeckHost host)
    {
        bool fast = NpvPlayerPrefs.ChoiceSlug(settings, preset, "drift") == "fast";
        bool strong = NpvPlayerPrefs.ChoiceSlug(settings, preset, "bleed") == "strong";

        float seekW = side * 0.78f;
        var frac = sig.Frac;

        return Canvas.Create(side, side,
        [
            new CanvasChild(0f, 0f, Embed.Comp(() => new CanvasArtwork
            {
                Bridge = bridge, Sig = sig, Side = side, Fast = fast, BleedOpacity = strong ? 1f : 0.6f,
            }) with { Key = "cv:" + (fast ? "fast" : "slow") + ":" + (strong ? "strong" : "soft") }),
            // The seek line is built ONCE, outside the artwork component, so its bind thunk survives every
            // album change and every play/pause re-render above it.
            new CanvasChild(side * 0.11f, side * 0.95f - 2f, new BoxEl
            {
                Width = seekW, Height = 2f, Shrink = 0f, Fill = Hex(0xFFFFFF, 0.35f), ClipToBounds = true,
                Children =
                [
                    new BoxEl
                    {
                        Width = seekW, Height = 2f, Shrink = 0f, Fill = Hex(0xFFFFFF),
                        TransformOriginX = 0f, TransformOriginY = 0.5f,
                        Transform = Prop.Of(() => Affine2D.Scale(Clamp01(frac.Value), 1f)),
                    },
                ],
            }),
        ]) with { Fill = Hex(0x05070C) };
    }

    internal static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    internal static ColorF Hex(uint rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);
}

/// <summary>
/// The bleed + the drifting frame. A <see cref="Component"/> for three reasons: it must re-render when the ALBUM
/// changes (to remount the keyed subtree and play its cross-fade), it must own the drift's layout effect, and hooks
/// may only live inside one.
/// </summary>
sealed class CanvasArtwork : Component
{
    /// <summary>300 ms opacity entrance for a new album's canvas. Reduced motion keeps the fade deliberately: this
    /// is a cross-dissolve between two pictures, and snapping it reads as a glitch (the <c>DeckHost</c> rule).</summary>
    static readonly LayoutTransition CoverEnter = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(300f, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true));

    public required PlaybackBridge Bridge;
    public required DeckSignals Sig;
    public required float Side;
    public required bool Fast;
    public required float BleedOpacity;

    NodeHandle _drift;
    Keyframe[]? _tx, _ty, _sx, _sy;
    float _keyedFor = float.NaN;

    public override Element Render()
    {
        int gen = Sig.CoverGen.Value;                 // a NEW ALBUM (never a same-album advance) remounts the canvas
        bool playing = Bridge.IsPlaying.Value;
        bool active = UseIsActive().Value;            // parked tab / minimized window
        bool reduced = Motion.ReducedMotion;          // a VALUE, folded into the gate — not a hook-count branch
        bool run = playing && active && !reduced;

        float s = Side;
        float frame = 0.78f * s;
        float inner = 1.18f * frame;
        float bleed = 1.5f * s;

        var track = Bridge.CurrentTrack.Value;
        string url = track is null ? "" : DeckArt.CoverUrl(track) ?? "";
        ColorF wash = NowPlayingPanel.HeroWashColor(url.Length > 0 ? url : null);
        string? hash = track?.Image?.BlurHash;

        UseLayoutEffect(() =>
        {
            var anim = Context.Anim;
            var scene = Context.Scene;
            var node = _drift;
            if (anim is null || scene is null || node.IsNull || !scene.IsLive(node)) return;
            if (!run)
            {
                // Cancel, not "seed a flat track": a cancelled channel holds its last interpolated value, which is
                // exactly the freeze-in-place a paused Ken Burns wants.
                anim.Cancel(node, AnimChannel.TranslateX);
                anim.Cancel(node, AnimChannel.TranslateY);
                anim.Cancel(node, AnimChannel.ScaleX);
                anim.Cancel(node, AnimChannel.ScaleY);
                return;
            }
            EnsureKeys(frame);
            float durationMs = (Fast ? DriftPath.FastSeconds : DriftPath.SlowSeconds) * 1000f;
            anim.Keyframes(node, AnimChannel.TranslateX, _tx!, durationMs, loop: true);
            anim.Keyframes(node, AnimChannel.TranslateY, _ty!, durationMs, loop: true);
            anim.Keyframes(node, AnimChannel.ScaleX, _sx!, durationMs, loop: true);
            anim.Keyframes(node, AnimChannel.ScaleY, _sy!, durationMs, loop: true);
        }, DepKey.From(HashCode.Combine(gen, run, Fast, frame)));

        Action<NodeHandle> capture = h => _drift = h;

        var canvas = Canvas.Create(s, s,
        [
            // The bleed: ONE oversized, pre-blurred draw. BakedBlur derives a persistent blurred bitmap once, so
            // this stays an ordinary textured quad instead of a per-frame blur layer.
            new CanvasChild(-0.25f * s, -0.25f * s, new BoxEl
            {
                Width = bleed, Height = bleed, Shrink = 0f, HitTestVisible = false,
                Opacity = BleedOpacity,
                Children =
                [
                    new ImageEl
                    {
                        Source = url, Width = bleed, Height = bleed, Fit = ImageFit.Cover, DecodePx = 256f,
                        Placeholder = wash, BlurHash = hash,
                        BakedBlur = new BakedBlurSpec(44f, 0.25f),
                        Saturation = 1.5f,
                    },
                ],
            }),
            // The drift frame is itself a Canvas: the −9% inset rides the CANVAS's own positioning wrapper, which
            // leaves the 118% box below free of any static transform. A node may own exactly one transform, and
            // this one's belongs to the animation slab.
            new CanvasChild(0.11f * s, 0.11f * s, Canvas.Create(frame, frame,
            [
                // The 118% wrapper IS the animated node: the slab writes its transform, the frame clips it, and
                // the extra 18% is the headroom the pan needs so no edge is ever exposed.
                new CanvasChild(-0.09f * frame, -0.09f * frame, new BoxEl
                {
                    Width = inner, Height = inner, Shrink = 0f,
                    OnRealized = capture,
                    Children =
                    [
                        new ImageEl
                        {
                            Source = url, Width = inner, Height = inner, Fit = ImageFit.Cover, DecodePx = 512f,
                            Placeholder = wash, BlurHash = hash,
                        },
                    ],
                }),
            ]) with
            {
                Corners = CornerRadius4.All(6f),
                Shadow = new ShadowSpec(60f, 24f, 0f, CanvasDeck.Hex(0x000000, 0.45f)),
            }),
        ]) with { Animate = CoverEnter, HitTestVisible = false };

        return canvas with { Key = "cv:" + gen };
    }

    /// <summary>Build the four looping tracks from <see cref="DriftPath"/>, MIRRORED (0 → ½ → 1 → ½ → 0) so the loop
    /// has no seam — the CSS <c>alternate</c> direction, expressed as keyframes. Built once per frame size (which is
    /// fixed for a mounted deck), so a gate flip re-seeds without allocating.</summary>
    void EnsureKeys(float frame)
    {
        if (_tx is not null && _keyedFor == frame) return;
        var keys = DriftPath.Keys;
        _tx = Mirror(keys.Length, i => keys[i].tx * frame);
        _ty = Mirror(keys.Length, i => keys[i].ty * frame);
        _sx = Mirror(keys.Length, i => keys[i].scale);
        _sy = Mirror(keys.Length, i => keys[i].scale);
        _keyedFor = frame;
    }

    static Keyframe[] Mirror(int count, Func<int, float> value)
    {
        // count forward frames + (count - 1) mirrored ones, evenly spaced across the loop.
        int total = count * 2 - 1;
        var frames = new Keyframe[total];
        for (int i = 0; i < total; i++)
        {
            int src = i < count ? i : total - 1 - i;
            frames[i] = new Keyframe(i / (float)(total - 1), value(src), Easing.EaseInOut);
        }
        return frames;
    }
}
