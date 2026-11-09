// ── Themes/Common/CommonMiniRail.cs — the mini player's seek rail and edge line, in a theme's look (#197) ──────────────
//
// The rail the three themes draw over the player bar's own scrub host (`MiniPlayerProps.SeekRail`: the gesture, the pixel dwell, the one
// commit per drag): a track, the played fill (a compositor scale, never a re-render), the theme's thumb, and the keyboard ladder's clock.
// A theme passes its look (`MiniRailLook`): WinUI's 4-DIP track with the ringed accent dot, Zune's 2-DIP line with a small square-cut
// thumb in the ink, Classic's 3-DIP track under a fill that ripples while the music moves (`Wave`) and lies flat while it rests.
// The EDGE form is the window's foot line (strip, square): a hairline at rest that thickens into a seek bar while the controls show.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using H = Wavee.Themes.Common.CommonMiniPlayerRules;

namespace Wavee.Themes.Common;

/// <summary>How a theme draws its mini rail.</summary>
internal sealed record MiniRailLook
{
    /// <summary>The track's and the fill's thickness, and their corner.</summary>
    public required float Track { get; init; }
    public required float Fill { get; init; }
    public required float Corner { get; init; }
    /// <summary>The track's alpha over the theme's ink (on imagery: white at the on-media track alpha).</summary>
    public required float TrackAlpha { get; init; }
    /// <summary>The thumb's diameter (0 = none), its corner, and the inner accent dot (0 = a solid thumb).</summary>
    public required float Thumb { get; init; }
    public required float ThumbCorner { get; init; }
    public required float ThumbDot { get; init; }
    /// <summary>The thumb's fill: the accent, or the theme's ink (WinUI's control fill, Zune's black or white).</summary>
    public required Func<ColorF, bool, ColorF> ThumbFill { get; init; }
    /// <summary>The thumb shows only while the controls show (Classic).</summary>
    public bool ThumbOnHot { get; init; }
    /// <summary>The thumb's ring (WinUI: the control stroke), 0 for none.</summary>
    public float ThumbRing { get; init; }
    /// <summary>The rippling fill while playing (Classic): the picture for a width, and its height.</summary>
    public Func<float, ColorF, Element>? Wave { get; init; }
    public float WaveH { get; init; }
}

internal static class CommonMiniRail
{
    public static Element Draw(MiniSeekRequest r, MiniRailLook look)
        => Embed.Comp(new RailProps(r, look), static () => new Rail());

    sealed record RailProps(MiniSeekRequest R, MiniRailLook Look);

    sealed class Rail : Component
    {
        readonly FloatSignal _width = new(0f);
        Action<RectF>? _bounds;
        ISeekRailHost? _host;

        public override Element Render()
        {
            var p = UseProps<RailProps>();
            var r = p.R;
            var look = p.Look;
            var rail = r.Rail;
            var host = rail.Host;
            _host = host;
            _bounds ??= b => { _host?.BoundsChanged(b); _width.Value = b.W; };
            bool enabled = rail.Enabled;
            bool drawn = enabled || rail.Pending;
            ColorF track = r.OnDark ? H.OnMediaInk with { A = H.OnMediaTrackAlpha } : Tok.TextPrimary with { A = look.TrackAlpha };
            ColorF accent = drawn ? r.Accent : r.Accent with { A = r.Accent.A * H.DisabledAlpha };

            if (r.Edge)
            {
                float t = r.Hot ? H.EdgeHot : H.EdgeRest;
                return new BoxEl
                {
                    Key = H.EdgeKey + ":rail", Grow = 1f, Height = H.EdgeHit, Direction = 1, Justify = FlexJustify.End, Role = AutomationRole.Slider,
                    IsEnabled = enabled && !rail.Line, Cursor = enabled ? CursorId.Hand : null, OnBoundsChanged = _bounds,
                    OnPointerDown = enabled ? host.PointerDown : null, OnDrag = enabled ? host.DragMove : null,
                    OnClick = enabled ? host.Commit : null, OnDragCanceled = enabled ? host.Cancel : null,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = t, ZStack = true, ClipToBounds = true, Fill = track, HitTestVisible = false,
                            Animate = LayoutTransition.BoundsT(SizeMode.ScaleCorrect),
                            Children = [new BoxEl { Grow = 1f, AlignSelf = FlexAlign.Stretch, Fill = accent, TransformOriginX = 0f, Transform = host.FillTransform, HitTestVisible = false }],
                        },
                        host.KeyLadderTick(),
                    ],
                };
            }

            if (rail.Line)
                return new BoxEl
                {
                    Grow = 1f, Height = H.SeekRowH, Direction = 0, AlignItems = FlexAlign.Center, HitTestVisible = false,
                    Children = [new BoxEl { Grow = 1f, Height = look.Track, Corners = CornerRadius4.All(look.Corner), Fill = accent, HitTestVisible = false }],
                };

            Element fill;
            if (look.Wave is { } wave && r.Playing && drawn)
            {
                float w = _width.Value;
                var fraction = host.FillTransform;
                var width = _width;
                // KEYED apart from the flat fill: the two would otherwise share one unkeyed slot, and the flat fill's bound x-scale (the
                // played fraction) stayed on the reused node when this identity-transform box took it over, squashing the wave.
                fill = new BoxEl
                {
                    Key = H.SeekKey + ":wave", AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start, Height = look.WaveH, ClipToBounds = true, HitTestVisible = false,
                    Width = Prop.Of(() => MathF.Max(0f, width.Value * fraction.Current().M11)), IsolateLayout = true,
                    Children = [wave(w, accent)],
                };
            }
            else
                fill = new BoxEl
                {
                    Key = H.SeekKey + ":fill", Grow = 1f, AlignSelf = FlexAlign.Center, Height = look.Fill, Corners = CornerRadius4.All(look.Corner), Fill = accent,
                    TransformOriginX = 0f, Transform = host.FillTransform, HitTestVisible = false,
                };
            var kids = new List<Element>(4)
            {
                new BoxEl { Grow = 1f, AlignSelf = FlexAlign.Center, Height = look.Track, Corners = CornerRadius4.All(look.Corner), Fill = track, HitTestVisible = false },
                fill,
            };
            if (look.Thumb > 0f && drawn)
            {
                float d = look.Thumb;
                bool show = !look.ThumbOnHot || r.Hot;
                kids.Add(new BoxEl
                {
                    Width = d, Height = d, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(look.ThumbCorner), Fill = look.ThumbFill(r.Accent, r.OnDark),
                    BorderWidth = look.ThumbRing, BorderColor = look.ThumbRing > 0f ? Tok.StrokeControlDefault : ColorF.Transparent,
                    Opacity = show ? 1f : 0f, HitTestVisible = false, Transform = host.ThumbTransform(d), OnRealized = host.ThumbRealized,
                    Children = look.ThumbDot > 0f
                        ? [new BoxEl { Width = look.ThumbDot, Height = look.ThumbDot, Corners = CornerRadius4.All(look.ThumbDot * 0.5f), Fill = accent, HitTestVisible = false }]
                        : [],
                });
            }
            kids.Add(host.KeyLadderTick());
            return new BoxEl
            {
                Key = H.SeekKey + ":rail", Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = H.SeekRowH, ZStack = true, Role = AutomationRole.Slider,
                IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null, OnBoundsChanged = _bounds,
                OnPointerDown = enabled ? host.PointerDown : null, OnDrag = enabled ? host.DragMove : null,
                OnClick = enabled ? host.Commit : null, OnDragCanceled = enabled ? host.Cancel : null,
                Children = kids.ToArray(),
            };
        }
    }
}
