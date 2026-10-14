// ── Themes/Classic/Structure/Views/Stage/ClassicStageHero.cs — Classic's `Stage.Hero` and `Stage.ArtistName` (themes plan 6.1, T7a2) ──
//
// From `Shell\Stage.Hero.UI.cs`: the ARTIST layout's HeroLayer (the full-bleed photo with a slow Ken Burns on the render thread, the
// arm-aware scrims), HeroPhoto (one posed photo layer) and ArtistNameLayer (eyebrow + the huge name). What the artist's header looks like
// (`HeroFact`, `HeroArtist`, `NextHeroUrl`) is the core lookups' (`Stage.HeroFacts`); the layer reads it from the stage context.
//
// THE PHOTO. One header image, cover-fit and full-bleed, panned and zoomed by four keyframe rows (ScaleX/Y, TranslateX/Y: a 40 s
// ping-pong at 30 Hz) on the render thread. The photo's box is a BoxEl.CompositePose: the engine records it POSE-FREE and composites
// ONE bilinear image quad at the current scale/translate — no tile raster per step. Under reduced motion (or with the option off) the rows
// sit at REST keys and the box never moves. A new artist cross-fades in OVER the old one (keyed by the artist slot, so a different
// rendition of the same artist's header re-uses the layer and only dissolves); until the overview answers the playing cover shows
// blurred, never a blank. The next track's header decodes hidden under the hero in the last ten seconds, so the switch never shows a placeholder.
//
// SCRIMS are two gradient siblings ABOVE the photo (so the photo's slice is a lone image), `Shade(a)` = the arm's veil: dark on the dark
// arm, light on the light arm (the no-per-subtree-theme rule). The bottom scrim's foot alpha is the "Image dimming" setting.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using G = Wavee.Themes.Classic.ClassicStageLayerGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Artist-layout photo: the registered template of <see cref="ViewKeys.StageHero"/> mounts the stateful layer.</summary>
internal static class ClassicStageHero
{
    /// <summary>The registered template of <see cref="ViewKeys.StageHero"/>.</summary>
    public static Element Template(StageHeroProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicStageHeroLayer());
}

/// <summary>The photo stack and its scrims: the shown layer, the previous one fading out under it, the next track's header decoding hidden.</summary>
internal sealed class ClassicStageHeroLayer : Component
{
    readonly Signal<int> _layers = new(0);
    readonly Action _clearPrevious;
    string _shownKey = "", _prevKey = "", _prevUrl = "";
    bool _prevBlur;
    int _prevArtist, _shownArtist, _seq;
    string _shownUrl = "";
    bool _shownBlur;

    public ClassicStageHeroLayer() { _clearPrevious = ClearPrevious; }

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
        ColorF z = ClassicStageLayerKit.Shade(0f);
        GradientSpec down, right;
        if (k.HeroLyrics)
        {
            right = GradientRight(new GradientStop(0f, Shade(0.92f)), new GradientStop(0.38f, Shade(0.78f)), new GradientStop(0.72f, Shade(0.15f)), new GradientStop(1f, z));
            down = GradientDown(new GradientStop(0f, z), new GradientStop(0.70f, z), new GradientStop(1f, Shade(0.80f)));
        }
        else
        {
            right = GradientRight(new GradientStop(0f, Shade(0.55f)), new GradientStop(0.55f, z));
            down = GradientDown(new GradientStop(0f, z), new GradientStop(0.30f, z), new GradientStop(0.55f, Shade(0.35f)), new GradientStop(1f, Shade(Stage.HeroRules.FootAlpha(dim))));
        }
        kids.Add(new BoxEl { Key = "hero:scrim:v", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Gradient = down });
        kids.Add(new BoxEl { Key = "hero:scrim:h", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Gradient = right });
        return Stage.Layer with { ClipToBounds = true, HitTestVisible = false, HitTestPassThrough = false, Children = kids.ToArray() };
    }

    static ColorF Shade(float a) => ClassicStageLayerKit.Shade(a);

    static Element Photo(string key, string url, in Stage.Layout L, int decode, bool blur, bool still, bool fade, int seed, float focusX)
    {
        var props = new ClassicStageHeroPhoto.Props(url, L.W, L.H, decode, blur, still, fade, Stage.HeroRules.Pan(seed), focusX);
        return Embed.Comp(props, static () => new ClassicStageHeroPhoto()) with { Key = key };
    }
}

/// <summary>One photo layer: its ROOT is the posed box (<c>CompositePose</c>) carrying the four Ken Burns rows; its only child is the image.
/// Reduced motion / the option off: REST keys (the engine never snaps a looping track). A layer arriving over another fades in
/// (kept under reduced motion — a fade orients, it does not move).</summary>
internal sealed class ClassicStageHeroPhoto : Component
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

/// <summary>"NOW PLAYING" and the huge artist name, bottom-anchored 18 DIP above the identity row (which the identity view draws): the name block
/// grows UPWARD from there. The name is the artist whose photo shows; while the overview is pending, the first billed artist.</summary>
internal static class ClassicStageArtistName
{
    /// <summary>The registered template of <see cref="ViewKeys.StageArtistName"/>.</summary>
    public static Element Template(StageArtistNameProps props, in ViewContext ctx)
    {
        var sc = props.Stage;
        var L = sc.Layout.Value;
        var k = sc.Look.Value;
        string name = props.Name();
        float size = L.HeroNameSize, line = L.HeroNameLine, pad = L.PadXFor(in k);
        return Stage.Layer with
        {
            HitTestVisible = false, HitTestPassThrough = false,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Gap = Stage.Layout.HeroNameGap * L.UnitK, MaxWidth = L.HeroNameMaxW, HitTestVisible = false,
                    AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(pad, 0f, 0f, L.HeroNameBottomOffset(in k)),
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Stage.NowPlayingEyebrow).ToUpperInvariant())
                        {
                            Size = G.EyebrowSize, LineHeight = G.EyebrowLine, Weight = 600, CharSpacing = G.EyebrowTracking, Color = Ink.InkSecondary, MaxLines = 1, MinWidth = 0f,
                        },
                        new TextEl(name)
                        {
                            Size = size, LineHeight = line, Weight = 700, FontFamily = G.DisplayFace, CharSpacing = G.NameTracking * size, Color = Ink.Ink,
                            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                    ],
                },
            ],
        };
    }
}
