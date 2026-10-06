// ── Shell/Stage.Hero.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// The ARTIST layout: HeroFacts (what the playing artist's header looks like), HeroLayer (the full-bleed photo with a slow Ken Burns
// on the render thread, the arm-aware scrims), HeroPhoto (one posed photo layer), ArtistNameLayer (eyebrow + the huge name)
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 330 lines
// Spec: Hero.dc.html / HeroLyrics.dc.html; Stage.Layouts.cs (HeroRules, the geometry)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE PHOTO. One header image, cover-fit and full-bleed, panned and zoomed by four keyframe rows (ScaleX/Y, TranslateX/Y: a 40 s
// ping-pong at 30 Hz) on the render thread. The photo's box is a BoxEl.CompositePose: the engine records it POSE-FREE and composites
// ONE bilinear image quad at the current scale/translate — no tile raster per step. Under reduced motion (or with the option off) the rows
// sit at REST keys and the box never moves. A new artist cross-fades in OVER the old one (keyed by the artist slot, so a different
// rendition of the same artist's header re-uses the layer and only dissolves); until the overview answers the playing cover shows
// blurred, never a blank. The next track's header decodes hidden under the hero in the last ten seconds, so the switch never shows a placeholder.
//
// SCRIMS are two gradient siblings ABOVE the photo (so the photo's slice is a lone image), `Shade(a)` = the arm's veil: dark on the dark
// arm, light on the light arm (the no-per-subtree-theme rule). The bottom scrim's foot alpha is the "Image dimming" setting.

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Design.StageInk;

namespace Wavee;

public static partial class Stage
{
    // ══ the facts ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Zero-size. Asks the first billed artists for their overview, walks them in billing order (<see cref="HeroRules.Pick"/>) and writes
    /// <c>ctx.HeroFact</c> / <c>ctx.HeroArtist</c>; also ensures the NEXT queued track's first artist and publishes its header url. Mounted only
    /// while the Artist layout is stored in Visualizer mode (Card users never pay for artist lookups); its unmount resets the facts, so a stale
    /// None never flashes Large art on the next mount.</summary>
    sealed class HeroFacts : Component
    {
        readonly bool[] _known = new bool[HeroRules.MaxBilled], _has = new bool[HeroRules.MaxBilled];

        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            // reads Artists.Changed / TrackArtists.Changed / Queue.Changed and the track key: re-runs on exactly those, before any paint
            UseSignalEffect(() =>
            {
                _ = Entities.ScopeEpoch.Value;
                _ = Entities.Current.Artists.Changed.Value;
                _ = Entities.Current.Edges.TrackArtists.Changed.Value;
                _ = Entities.Current.Edges.Queue.Changed.Value;
                var track = ctx.RowValue();
                ReadOnlySpan<int> slots = track.IsValid ? track.ArtistSlots : default;
                int n = Math.Min(HeroRules.MaxBilled, slots.Length);
                for (int i = 0; i < n; i++)
                {
                    var a = new Artist(slots[i]);
                    _known[i] = false; _has[i] = false;
                    if (!a.IsValid) continue;
                    Entities.Ensure(a, ArtistFields.Overview, i == 0 ? FetchPriority.Visible : FetchPriority.Prefetch);
                    _known[i] = a.Knows(ArtistFields.Header);
                    _has[i] = !a.HeaderId.IsEmpty;
                }
                var (state, index) = HeroRules.Pick(_known.AsSpan(0, n), _has.AsSpan(0, n));
                ctx.HeroFact.SetIfChanged(state);
                ctx.HeroArtist.SetIfChanged(state == HeroState.Header && index >= 0 ? slots[index] : 0);

                // the next track's header, decoded hidden under the hero shortly before the switch
                string next = "";
                if (Queue.UpNext(out int start, out int length) && length > 0)
                {
                    var r = Queue.RefAt(start);
                    if (!r.IsNone && r.Kind == EntityKind.Track)
                    {
                        var nextSlots = new Track(r.Slot).ArtistSlots;
                        if (nextSlots.Length > 0)
                        {
                            var na = new Artist(nextSlots[0]);
                            if (na.IsValid)
                            {
                                Entities.Ensure(na, ArtistFields.Overview, FetchPriority.Prefetch);
                                if (na.Knows(ArtistFields.Header) && !na.HeaderId.IsEmpty) next = Controls.ArtUrl(na.HeaderId) ?? "";
                            }
                        }
                    }
                }
                ctx.NextHeroUrl.SetIfChanged(next);
            });
            UseEffect(() => () => { ctx.HeroFact.Value = HeroState.Pending; ctx.HeroArtist.Value = 0; ctx.NextHeroUrl.Value = ""; }, DepKey.Empty);
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

    // ══ the photo stack + scrims ═════════════════════════════════════════════════════════════════════════════════════

    sealed class HeroLayer : Component
    {
        readonly Signal<int> _layers = new(0);
        readonly Action _clearPrevious;
        string _shownKey = "", _prevKey = "", _prevUrl = "";
        bool _prevBlur;
        int _prevArtist, _shownArtist, _seq;
        string _shownUrl = "";
        bool _shownBlur;

        public HeroLayer() { _clearPrevious = ClearPrevious; }

        void ClearPrevious()
        {
            if (_prevKey.Length == 0) return;
            _prevKey = ""; _prevUrl = "";
            _layers.Value = _layers.Peek() + 1;
        }

        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            _ = _layers.Value;                                   // the fade-out timeout's "drop the previous layer"
            var L = ctx.Layout.Value;
            var k = ctx.Look.Value;
            var track = ctx.RowValue();
            int artistSlot = ctx.HeroArtist.Value;
            bool header = ctx.HeroFact.Value == HeroState.Header && artistSlot > 0;
            string coverUrl = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
            string url = header ? Controls.ArtUrl(new Artist(artistSlot).HeaderId) ?? "" : coverUrl;
            bool blur = !header;
            // keyed by the ARTIST (or "the cover"): a new rendition of the same header re-uses the layer and only dissolves (the engine's swap)
            string key = header ? "hero:a" + artistSlot : "hero:cover";
            if (!string.Equals(key, _shownKey, StringComparison.Ordinal) && url.Length > 0)
            {
                _prevKey = _shownKey; _prevUrl = _shownUrl; _prevBlur = _shownBlur; _prevArtist = _shownArtist;
                _shownKey = key; _shownUrl = url; _shownBlur = blur; _shownArtist = artistSlot; _seq++;
            }
            else if (string.Equals(key, _shownKey, StringComparison.Ordinal)) { _shownUrl = url; }
            bool still = Design.Reduced || !ctx.HeroMotion.Value;
            int decode = HeroRules.DecodePx(L.W, L.H, 1.5f);
            float focusX = k.HeroLyrics ? 1f : 0.5f;
            var kids = new List<Element>(4);
            bool hasPrev = _prevKey.Length > 0 && _prevUrl.Length > 0;
            if (hasPrev) kids.Add(Photo(_prevKey, _prevUrl, L, decode, _prevBlur, still, fade: false, _prevArtist, focusX));
            if (_shownUrl.Length > 0) kids.Add(Photo(_shownKey, _shownUrl, L, decode, _shownBlur, still, fade: hasPrev, _shownArtist, focusX));
            // the next track's header, mounted invisible just before the switch (outside the posed box)
            string next = ctx.NextHeroUrl.Value;
            if (next.Length > 0 && header && !string.Equals(next, _shownUrl, StringComparison.Ordinal)
                && HeroRules.PrefetchDue(Playback.PositionMs.Value, Playback.DurationMs.Value))
                kids.Add(new BoxEl { Key = "pre:" + next, Width = L.W, Height = L.H, ZStack = true, Opacity = 0f, HitTestVisible = false,
                    Children = [Ui.Image(next, ImageFit.Cover, float.NaN, decode, 0f, Ink.ArtStandIn(next)) with { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch }] });
            UseTimeout(_clearPrevious, HeroRules.FadeMs + 150f, DepKey.From(_seq));

            // the scrims: ABOVE the photo so its slice stays a lone image
            int dim = ctx.HeroDim.Value;
            ColorF z = Shade(0f);
            GradientSpec down, right;
            if (k.HeroLyrics)
            {
                right = GradientRight(new GradientStop(0f, Shade(0.92f)), new GradientStop(0.38f, Shade(0.78f)), new GradientStop(0.72f, Shade(0.15f)), new GradientStop(1f, z));
                down = GradientDown(new GradientStop(0f, z), new GradientStop(0.70f, z), new GradientStop(1f, Shade(0.80f)));
            }
            else
            {
                right = GradientRight(new GradientStop(0f, Shade(0.55f)), new GradientStop(0.55f, z));
                down = GradientDown(new GradientStop(0f, z), new GradientStop(0.30f, z), new GradientStop(0.55f, Shade(0.35f)), new GradientStop(1f, Shade(HeroRules.FootAlpha(dim))));
            }
            kids.Add(new BoxEl { Key = "hero:scrim:v", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Gradient = down });
            kids.Add(new BoxEl { Key = "hero:scrim:h", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Gradient = right });
            return Layer with { ClipToBounds = true, HitTestVisible = false, HitTestPassThrough = false, Children = kids.ToArray() };
        }

        static Element Photo(string key, string url, in Layout L, int decode, bool blur, bool still, bool fade, int seed, float focusX)
        {
            var props = new HeroPhoto.Props(url, L.W, L.H, decode, blur, still, fade, HeroRules.Pan(seed), focusX);
            return Embed.Comp(props, static () => new HeroPhoto()) with { Key = key };
        }
    }

    /// <summary>One photo layer: its ROOT is the posed box (<c>CompositePose</c>) carrying the four Ken Burns rows; its only child is the image.
    /// Reduced motion / the option off: REST keys (the engine never snaps a looping track). A layer arriving over another fades in
    /// (kept under reduced motion — a fade orients, it does not move).</summary>
    sealed class HeroPhoto : Component
    {
        public sealed record Props(string Url, float W, float H, int Decode, bool Blur, bool Still, bool Fade, Visualizer.Covers.Spot.KenBurns Pan, float FocusX);
        static readonly Keyframe[] s_restOne = [new Keyframe(0f, 1f), new Keyframe(1f, 1f)], s_restZero = [new Keyframe(0f, 0f), new Keyframe(1f, 0f)];

        public override Element Render()
        {
            var p = UseProps<Props>();
            bool still = p.Still;
            var pan = p.Pan;
            Keyframe[] scale = still ? s_restOne : [new Keyframe(0f, pan.ScaleFrom), new Keyframe(0.5f, pan.ScaleTo, Easing.EaseInOut), new Keyframe(1f, pan.ScaleFrom, Easing.EaseInOut)];
            Keyframe[] tx = still ? s_restZero : [new Keyframe(0f, 0f), new Keyframe(0.5f, pan.Dx * p.W, Easing.EaseInOut), new Keyframe(1f, 0f, Easing.EaseInOut)];
            Keyframe[] ty = still ? s_restZero : [new Keyframe(0f, 0f), new Keyframe(0.5f, pan.Dy * p.H, Easing.EaseInOut), new Keyframe(1f, 0f, Easing.EaseInOut)];
            var key = DepKey.From(still ? 1 : 0, (int)p.W, (int)p.H, HashCode.Combine(pan.ScaleTo, pan.Dx, pan.Dy));
            var hz = Cadence.At(HeroRules.MotionHz);
            UseKeyframes(AnimChannel.ScaleX, scale, HeroRules.SpanMs, !still, key, hz);
            UseKeyframes(AnimChannel.ScaleY, scale, HeroRules.SpanMs, !still, key, hz);
            UseKeyframes(AnimChannel.TranslateX, tx, HeroRules.SpanMs, !still, key, hz);
            UseKeyframes(AnimChannel.TranslateY, ty, HeroRules.SpanMs, !still, key, hz);
            return new BoxEl
            {
                Width = p.W, Height = p.H, ZStack = true, HitTestVisible = false, CompositePose = true,
                Enter = p.Fade ? new EnterExit(Opacity: 0f, Active: true) : null,
                Transition = p.Fade ? MotionTokenDef.Eased(HeroRules.FadeMs, Easing.Linear, ReducedMotionPolicy.KeepFade) : null,
                Children =
                [
                    Ui.Image(p.Url, ImageFit.Cover, float.NaN, p.Decode, 0f, Ink.ArtStandIn(p.Url)) with
                    {
                        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, FocusX = p.FocusX,
                        BakedBlur = p.Blur ? new BakedBlurSpec(40f, 0.5f) : null,
                    },
                ],
            };
        }
    }

    // ══ the name ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"NOW PLAYING" and the huge artist name, bottom-anchored 18 DIP above the identity row (which the Hero component draws): the
    /// name block grows UPWARD from there. The name is the artist whose photo shows; while the overview is pending, the first billed artist.</summary>
    sealed class ArtistNameLayer : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var k = ctx.Look.Value;
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Artists.Changed.Value;
            _ = Entities.Current.Edges.TrackArtists.Changed.Value;
            var track = ctx.RowValue();
            int slot = ctx.HeroArtist.Value;
            var artist = slot > 0 ? new Artist(slot) : Rail.NowPlayingArtist(track);
            string name = artist.IsValid ? artist.Name : "";
            float size = L.HeroNameSize, line = L.HeroNameLine, pad = L.PadXFor(in k);
            return Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = Layout.HeroNameGap * L.UnitK, MaxWidth = L.HeroNameMaxW, HitTestVisible = false,
                        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(pad, 0f, 0f, L.HeroNameBottomOffset(in k)),
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Stage.NowPlayingEyebrow).ToUpperInvariant())
                            {
                                Size = 13f, LineHeight = 16f, Weight = 600, CharSpacing = 2.34f, Color = Ink.InkSecondary, MaxLines = 1, MinWidth = 0f,
                            },
                            new TextEl(name)
                            {
                                Size = size, LineHeight = line, Weight = 700, FontFamily = DisplayFace, CharSpacing = -0.02f * size, Color = Ink.Ink,
                                Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                            },
                        ],
                    },
                ],
            };
        }
    }
}
