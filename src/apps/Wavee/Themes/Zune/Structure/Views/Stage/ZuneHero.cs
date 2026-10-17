// ── Themes/Zune/Structure/Views/Stage/ZuneStageHero.cs — Zune's `Stage.Hero` (P5 Z4c) ───────────────────────────────────────────────────
//
// The ARTIST layout's photo (Z-NowPlaying-Artist): the artist's header image full-bleed behind everything, under the stage's flat chrome.
// The stage's production recipe is kept whole (owner rule: entrance choreography is deliberate): a slow Ken Burns on the render thread
// (a posed box: ONE bilinear quad per step, REST keys under reduced motion), a new artist cross-fading in OVER the old one (the layer is
// keyed by the artist, so another rendition of the same header only dissolves), the playing cover blurred until the overview answers, the
// next track's header decoded hidden under the hero in the last ten seconds. What the artist's header looks like (`HeroFact`,
// `HeroArtist`, `NextHeroUrl`) is the core lookups' (`Stage.HeroFacts`).
//
// Zune's change is the SCRIMS above the photo (so the photo's slice stays a lone image): the board's vertical wash (0.35 at the top where
// the bar reads, clear by a quarter, 0.3 at 60 %, the "Image dimming" foot at the bottom, where the plate and the name sit), and, with the
// lyrics over the image, the left veil the lyrics pane reads on. The veils are the stage's (the ink's opposite on either arm).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Theming.Roles.StageInk;
using HeroR = Wavee.Themes.Zune.ZuneStageHeroRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Artist-layout photo: the registered template of <see cref="ViewKeys.StageHero"/> mounts the stateful layer.</summary>
internal static class ZuneStageHero
{
    /// <summary>The registered template of <see cref="ViewKeys.StageHero"/>.</summary>
    public static Element Template(StageHeroProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ZuneStageHeroLayer());
}

/// <summary>The photo stack and its scrims: the shown layer, the previous one fading out under it, the next track's header decoding hidden.</summary>
internal sealed class ZuneStageHeroLayer : Component
{
    readonly Signal<int> _layers = new(0);
    readonly Action _clearPrevious;
    string _shownKey = "", _prevKey = "", _prevUrl = "";
    bool _prevBlur;
    int _prevArtist, _shownArtist, _seq;
    string _shownUrl = "";
    bool _shownBlur;

    public ZuneStageHeroLayer() { _clearPrevious = ClearPrevious; }

    void ClearPrevious()
    {
        if (_prevKey.Length == 0) return;
        _prevKey = ""; _prevUrl = "";
        _layers.Value = _layers.Peek() + 1;
    }

    public override Element Render()
    {
        var props = UseProps<StageHeroProps>();
        var ctx = props.Stage;
        _ = _layers.Value;                                   // the fade-out timeout's "drop the previous layer"
        var L = ctx.Layout.Value;
        var k = ctx.Look.Value;
        var track = ctx.RowValue();
        int artistSlot = ctx.HeroArtist.Value;
        bool header = ctx.HeroFact.Value == Stage.HeroState.Header && artistSlot > 0;
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
        int decode = Stage.HeroRules.DecodePx(L.W, L.H, 1.5f);
        float focusX = k.HeroLyrics ? 1f : 0.5f;
        var kids = new List<Element>(4);
        bool hasPrev = _prevKey.Length > 0 && _prevUrl.Length > 0;
        if (hasPrev) kids.Add(Photo(_prevKey, _prevUrl, L, decode, _prevBlur, still, fade: false, _prevArtist, focusX));
        if (_shownUrl.Length > 0) kids.Add(Photo(_shownKey, _shownUrl, L, decode, _shownBlur, still, fade: hasPrev, _shownArtist, focusX));
        // the next track's header, mounted invisible just before the switch (outside the posed box)
        string next = ctx.NextHeroUrl.Value;
        if (next.Length > 0 && header && !string.Equals(next, _shownUrl, StringComparison.Ordinal) && props.PrefetchDue())
            kids.Add(new BoxEl { Key = "pre:" + next, Width = L.W, Height = L.H, ZStack = true, Opacity = 0f, HitTestVisible = false,
                Children = [Ui.Image(next, ImageFit.Cover, float.NaN, decode, 0f, Ink.ArtStandIn(next)) with { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch }] });
        UseTimeout(_clearPrevious, Stage.HeroRules.FadeMs + 150f, DepKey.From(_seq));

        // the scrims: ABOVE the photo so its slice stays a lone image
        int dim = ctx.HeroDim.Value;
        ColorF z = Shade(0f);
        GradientSpec down;
        GradientSpec? left = null;
        if (k.HeroLyrics)
        {
            left = GradientRight(new GradientStop(0f, Shade(HeroR.LyricsLeftA)), new GradientStop(HeroR.LyricsLeftMidStop, Shade(HeroR.LyricsLeftMidA)),
                new GradientStop(HeroR.LyricsLeftFarStop, Shade(HeroR.LyricsLeftFarA)), new GradientStop(1f, z));
            down = GradientDown(new GradientStop(0f, z), new GradientStop(HeroR.LyricsFootClearStop, z), new GradientStop(1f, Shade(HeroR.LyricsFootA)));
        }
        else
        {
            down = GradientDown(new GradientStop(0f, Shade(HeroR.TopA)), new GradientStop(HeroR.TopClearStop, z),
                new GradientStop(HeroR.MidStop, Shade(HeroR.MidA)), new GradientStop(1f, Shade(Stage.HeroRules.FootAlpha(dim))));
        }
        kids.Add(new BoxEl { Key = "hero:scrim:v", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Gradient = down });
        if (left is { } l)
            kids.Add(new BoxEl { Key = "hero:scrim:h", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Gradient = l });
        return Stage.Layer with { ClipToBounds = true, HitTestVisible = false, HitTestPassThrough = false, Children = kids.ToArray() };
    }

    static ColorF Shade(float a) => ZuneStageKit.Shade(a);

    static Element Photo(string key, string url, in Stage.Layout L, int decode, bool blur, bool still, bool fade, int seed, float focusX)
    {
        var props = new ZuneStageHeroPhoto.Props(url, L.W, L.H, decode, blur, still, fade, Stage.HeroRules.Pan(seed), focusX);
        return Embed.Comp(props, static () => new ZuneStageHeroPhoto()) with { Key = key };
    }
}

/// <summary>One photo layer: its ROOT is the posed box (<c>CompositePose</c>) carrying the four Ken Burns rows; its only child is the image.
/// Reduced motion / the option off: REST keys (the engine never snaps a looping track). A layer arriving over another fades in
/// (kept under reduced motion: a fade orients, it does not move).</summary>
internal sealed class ZuneStageHeroPhoto : Component
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
        var hz = Cadence.At(Stage.HeroRules.MotionHz);
        UseKeyframes(AnimChannel.ScaleX, scale, Stage.HeroRules.SpanMs, !still, key, hz);
        UseKeyframes(AnimChannel.ScaleY, scale, Stage.HeroRules.SpanMs, !still, key, hz);
        UseKeyframes(AnimChannel.TranslateX, tx, Stage.HeroRules.SpanMs, !still, key, hz);
        UseKeyframes(AnimChannel.TranslateY, ty, Stage.HeroRules.SpanMs, !still, key, hz);
        Context.UseAmbientPause(AmbientMotion.Pose, key, loops: !still);   // paused playback: the pan stands still (AmbientMotion)
        return new BoxEl
        {
            Width = p.W, Height = p.H, ZStack = true, HitTestVisible = false, CompositePose = true,
            Enter = p.Fade ? new EnterExit(Opacity: 0f, Active: true) : null,
            Transition = p.Fade ? MotionTokenDef.Eased(Stage.HeroRules.FadeMs, Easing.Linear, ReducedMotionPolicy.KeepFade) : null,
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
