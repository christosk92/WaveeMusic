// ── Themes/Classic/Structure/Views/Pages/ClassicArtistHero.cs — Classic's artist hero (themes plan 6.1, T10b1) ─────────────────
//
// From `Entities\Artist.UI.cs` §4.1 (`HeroBanner`, `HeroIdentity`, `HeroMeta`, `HeroActions`, `HeroArt`): the full-bleed hero and its collapse into the 56-DIP
// band (ArtistPage.Hero.cs:22-198). Horizontal tiers are a ZStack (photo · veil · copy centred); stacked tiers are a column (photo band · identity justified
// End) — two compositions, never one with a flag (ch 08 §9 #1). A null `play` is the skeleton arm: no veil leaf, no band, the actions as their shapes.
//
// The hero is built by the page view for the live page AND for the derived shimmer (the skeleton deriver cannot run a view slot), and its pinned,
// collapsing root has to stay a direct child of the page column, which is why it is a builder and not a key of its own. The tiers, the heights, the gutters
// and the collapse arithmetic are `ArtistHeroLayout` (core: the profile page reads the same tiers); what the hero SAYS is `Artist.HeroText` (core facts).

using System.Globalization;
using FluentGpu;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's artist hero.</summary>
internal static class ClassicArtistHero
{
    /// <summary>The full-bleed hero and its collapse into the 56-DIP band (ArtistPage.Hero.cs:22-198).</summary>
    public static Element Build(in Artist.HeroText text, string uri, string? photoUrl, string? paletteUrl, float width,
                                in ArtistHeroMetrics m, ColorF accent, bool compactCanHit,
                                Action? play, Action? shuffle, Action? radio, Element? band,
                                uint headerAccent = 0)
    {
        float w = MathF.Max(1f, width);
        float height = m.MinHeight;
        float collapse = ArtistHeroLayout.CollapseDistance(height);
        float photoH = ArtistHeroLayout.PhotoHeightFor(m);

        // W4b: no url ⇒ HeroArt is NOT mounted; the flat theme neutral stands in (never a cover tint — ch 08 §4 row 6b).
        // KEYED ON THE ARTIST, NOT THE URL (ch 08 BUG E #2): the caller already latches which art identity is showing
        // (Detail.CoverLatch.PreferVisible), so the url itself still changes CDN size/rendition across a render — a
        // key on `src` would remount HeroArt (and its zoom/settle state) on every one of those, undoing the latch. The
        // url instead reaches the mounted instance live as a re-pushed prop (HeroArtProps + UseProps), exactly like the
        // wash and veil below.
        // ch 08 BUG F: WATCHED, not the raw `R.Layer.ArtPlaceholder` ColorF — the same frozen-placeholder defect as
        // PersonPicture's fill (the pick avatar, ClassicArtistPick). No url here means no tint EITHER way (the "never a
        // cover tint" rule above still holds — Design.WatchedPlaceholder(null) resolves to the same flat neutral), but
        // it is now a live-bound Prop like every other art slot's placeholder instead of a value baked in at this
        // render, so a future caller of this arm with a real (still-ungrading) url repaints instead of staying grey.
        Element art = photoUrl is { Length: > 0 } src
            ? Embed.Comp(new HeroArtProps(src, w, photoH), static () => new HeroArt()) with { Key = "heroart:" + uri }
            : new BoxEl { Width = w, Height = photoH, Fill = Design.WatchedPlaceholder(photoUrl) };
        Element media = new BoxEl
        {
            Width = w, Height = photoH, ZStack = true, ClipToBounds = true,
            TransformOriginX = 0.5f, TransformOriginY = 0f,
            EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, ArtistHeroLayout.PhotoFadeBandFor(photoH)),
            Children = [art],
        }.StretchFromTop().ParallaxY(ArtistHeroLayout.PhotoParallaxFraction, photoH);

        // Shared by both arms: the expanded presentation slides up and fades as the band takes over.
        ScrollEffectSpec[] collapseEffects =
        [
            new(ScrollEffect.Parallax(0.0, collapse, 0f, -collapse)),
            new(ScrollEffect.Fade(ArtistHeroLayout.ExpandedFadeStart(collapse), collapse, 1f, 0f)),
        ];

        Element identity = Identity(in text, uri, w, in m, accent, play, shuffle, radio);
        Element expanded;
        if (m.Stacked)
        {
            // No veil: no type sits on the photograph on these tiers (ch 08 §0 #2).
            expanded = new BoxEl
            {
                Width = w, Height = height, Direction = 1,
                HitTestVisible = !compactCanHit, ScrollEffects = collapseEffects,
                Children =
                [
                    media,
                    new BoxEl
                    {
                        Grow = 1f, MinHeight = 0f, Direction = 1,
                        Justify = FlexJustify.End, AlignItems = FlexAlign.Start,
                        Padding = new Edges4(m.Gutter, Spacing.M, m.Gutter, Spacing.XL),
                        Children = [identity],
                    },
                ],
            };
        }
        else
        {
            Element copy = new BoxEl
            {
                Width = w, Height = height, Direction = 1,
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Start,
                Padding = new Edges4(m.Gutter, Spacing.XXL, m.Gutter, Spacing.XXL),
                Children = [identity],
            };
            // A cover-keyed leaf: a late grading swaps the gradient without rebuilding the banner (ch 08 §4 row 3).
            // headerAccent (row.HeaderAccent) is the raw payload rung the page passes through from the artist row.
            // The veil stretches WITH the photo on a top overpan (the same top-anchored StretchFromTop): a fixed veil
            // over a growing photo left an unveiled band above the hero's top edge.
            Element veil = play is null
                ? new BoxEl()
                : new BoxEl
                {
                    Width = w, Height = height, ZStack = true, HitTestVisible = false,
                    TransformOriginX = 0.5f, TransformOriginY = 0f,
                    Children =
                    [
                        Palette.ArtistHeroVeil(paletteUrl, vertical: false, w, height, key: "artist-veil:" + uri,
                                               payloadAccent: headerAccent, hold: ClassicArtistRules.HeroVeilHold(w, in m)),
                    ],
                }.StretchFromTop();
            expanded = new BoxEl
            {
                Width = w, Height = height, ZStack = true,
                HitTestVisible = !compactCanHit, ScrollEffects = collapseEffects,
                Children = [media, veil, copy],
            };
        }

        // Pinned at the viewport top and COLLAPSING into the compact band: over the collapse distance the presented height
        // shrinks from the banner height to the band, and the Leading collapse cuts the expanded presentation (paint and
        // input) at that edge by itself — the presentation carries its own slide-away (collapseEffects). No ClipToBounds:
        // a box clip here would also cut the photo's overscroll stretch above the root's top (a dark band over the photo).
        return new BoxEl
        {
            Direction = 1, Height = height, ZStack = true,
            Children = band is null ? [expanded] : [expanded, band],
        }.Sticky(0f).Collapse(collapse, ArtistHeroLayout.CompactIdentityHeight, CollapseAnchor.Leading);
    }

    /// <summary>Verified · name · first sentence · meta, then the action row (Hero.cs:34-95).</summary>
    static Element Identity(in Artist.HeroText text, string uri, float w, in ArtistHeroMetrics m, ColorF accent,
                            Action? play, Action? shuffle, Action? radio)
    {
        // The skeleton arm (no play) states every text run as its LINES: the deriver turns a text leaf into a bar 0.72 of its size tall, so the
        // copy came out ~70 DIP short of the loaded one at Wide (an 84/96 name became a 60 bar, each 14/20 line a 10 one) and, centred on the
        // photo, every bar and the action row stood off the words (tuned Classic X2).
        bool shimmer = play is null;
        Element verified = text.Verified
            ? new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Children =
                [
                    InfoBadge.Icon(Icons.Accept, color: accent),
                    Lines(Caption(Loc.Get(Strings.Artist.Verified)) with { Color = Tok.TextSecondary }, 1, shimmer),
                ],
            }
            : new BoxEl();

        TextEl name = m.Tier switch
        {
            ArtistHeroTier.Wide => R.Type.ArtistDisplay(text.Name),
            ArtistHeroTier.Medium => R.Type.ArtistTitle(text.Name),
            _ => R.Type.ArtistCompactTitle(text.Name),
        };
        name = name with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2, MinWidth = 0f };
        Element nameLine = Lines(name, 1, shimmer);

        // The lead is a COMMIT-computed column (ch 08 GAP 3): the hero never strips HTML on a render.
        Element bio = text.Lead.Length == 0
            ? new BoxEl()
            : Lines(Body(text.Lead) with
            {
                Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f,
            }, ClassicArtistRules.HeroLeadSkeletonLines, shimmer);

        return new BoxEl
        {
            Key = "artist-hero-copy", Direction = 1,
            // D18: on the photo tiers the copy ends near 56 % of the hero and the veil holds to that edge (ClassicArtistRules).
            Width = ClassicArtistRules.HeroCopyWidth(w, in m),
            MaxWidth = m.CopyMaxWidth, MinWidth = 0f, Gap = Spacing.S,
            // The page-level SkelRegion owns the single reveal. A second hero-copy entrance here made the
            // already-revealed page flash and move for another frame when the overview landed.
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, MinWidth = 0f,
                    // Meta stays ONE horizontal row at Compact; only Narrow stacks it.
                    Children = [verified, nameLine, bio, Meta(in text, m.Tier == ArtistHeroTier.Narrow, shimmer)],
                },
                Actions(text.Name, uri, accent, play, shuffle, radio, m.Tier),
            ],
        };
    }

    /// <summary>"#N in the world" (accent) · monthly listeners · followers — a zero stat drops its run (Hero.cs:236-259).</summary>
    static Element Meta(in Artist.HeroText text, bool stacked, bool shimmer)
    {
        int count = (text.Rank > 0 ? 1 : 0) + (text.Monthly > 0 ? 1 : 0) + (text.Followers > 0 ? 1 : 0);
        var kids = new Element[count];
        int k = 0;
        if (text.Rank > 0)
            kids[k++] = Lines(BodyStrong(Strings.Artist.WorldRank(text.Rank.ToString(CultureInfo.CurrentCulture)))
                with { Color = Tok.AccentTextPrimary }, 1, shimmer);
        if (text.Monthly > 0)
            kids[k++] = Lines(Body(CountLabel(text.Monthly) + " " + Loc.Get(Strings.Artist.MetaMonthly)) with { Color = Tok.TextSecondary }, 1, shimmer);
        if (text.Followers > 0)
            kids[k++] = Lines(Body(CountLabel(text.Followers) + " " + Loc.Get(Strings.Artist.MetaFollowers)) with { Color = Tok.TextSecondary }, 1, shimmer);
        return new BoxEl
        {
            Direction = (byte)(stacked ? 1 : 0),
            AlignItems = stacked ? FlexAlign.Start : FlexAlign.Center,
            Gap = stacked ? Spacing.XS : Spacing.L,
            MinWidth = 0f,
            Children = kids,
        };
    }

    /// <summary>Play (the cover's accent) · Shuffle · Follow · Radio; at Narrow [Play, Follow] over [Shuffle, Radio]
    /// (Hero.cs:200-234). Never skeleton content (ch 08 §9 #8).
    /// <para>Workstream B grammar: [<see cref="Controls.PlayButton(ColorF,Action,string?,string?)"/>][Shuffle, a stock
    /// Standard <c>Button.Create</c>][<see cref="Controls.FollowToggle"/>][radio, <see cref="Controls.IconAction"/>].</para></summary>
    static Element Actions(string name, string uri, ColorF accent, Action? play, Action? shuffle, Action? radio,
                           ArtistHeroTier tier)
    {
        Element playButton = Controls.PlayButton(accent, play ?? s_noop, Loc.Get(Strings.Artist.Play));
        // Keyed on the uri: FollowToggle carries its uri as a mount-frozen field.
        Element follow = uri.Length == 0
            ? Controls.FollowToggle.SkeletonShape()
            : Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name })
                with { Key = "artist-follow:" + uri, SkeletonProxy = s_followShape };
        Element shuffleButton = Button.Create(Loc.Get(Strings.Detail.Shuffle), shuffle ?? s_noop,
            ButtonAppearance.Standard, glyph: Icons.Shuffle);
        Element radioButton = Controls.Named(
            Controls.IconAction(Icons.RadioTower, radio), Loc.Get(Strings.Artist.ArtistRadio));

        if (tier == ArtistHeroTier.Narrow)
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S,
                Children =
                [
                    new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = [playButton, follow] },
                    new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = [shuffleButton, radioButton] },
                ],
            }.Skeletonized(false);

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children = [playButton, shuffleButton, follow, radioButton],
        }.Skeletonized(false);
    }

    /// <summary>A text run as the loaded hero lays it out, for the skeleton arm: <paramref name="lines"/> line boxes of the run's own line height,
    /// one bar each. The live arm (and a run with no stated line height) is the run itself.</summary>
    static Element Lines(TextEl run, int lines, bool shimmer)
    {
        if (!shimmer || !(run.LineHeight > 0f)) return run;
        var boxes = new Element[Math.Max(1, lines)];
        for (int i = 0; i < boxes.Length; i++)
            boxes[i] = new BoxEl
            {
                Height = run.LineHeight, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = [run with { MaxLines = 1, Wrap = TextWrap.NoWrap }],
            };
        return boxes.Length == 1 ? boxes[0] : new BoxEl { Direction = 1, MinWidth = 0f, Children = boxes };
    }

    static readonly Func<Element> s_followShape = static () => Controls.FollowToggle.SkeletonShape();
    static readonly Action s_noop = static () => { };

    /// <summary>A count in the current culture's grouping ("20,577,457"). Also the biography band's profile facts and the city bars'.</summary>
    internal static string CountLabel(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    sealed record HeroArtProps(string Url, float Width, float PhotoHeight);

    /// <summary>The photograph: one gentle image-only entrance settle. The page keeps its shimmer until the overview, the measured width and
    /// the chart settle, and the photo owns its own entrance when its bitmap lands. The scale is owned by this keyed component and runs once per
    /// artist; it never restarts when a CDN rendition or window measurement changes. The decode size is latched at the first measured width so a
    /// resize cannot create a visible resize flash.</summary>
    sealed class HeroArt : Component
    {
        const float StartScale = 1f, RestScale = 1.03f, FrameScale = 1.02f, FrameLiftFraction = 0.02f;
        static readonly Keyframe[] s_enterScale =
            [new(0f, StartScale, Easing.Linear), new(1f, RestScale, Easing.FluentDecelerate)];
        static readonly Keyframe[] s_enterOpacity = [new(0f, 0f, Easing.Linear), new(1f, 1f, Easing.FluentDecelerate)];
        static readonly Keyframe[] s_restOpacity = [new(0f, 1f), new(1f, 1f)];

        public override Element Render()
        {
            var p = UseProps<HeroArtProps>();
            float width = MathF.Max(1f, p.Width);
            float height = MathF.Max(1f, p.PhotoHeight);

            var decode = UseRef((0, 0));
            if (decode.Value.Item1 <= 0 && width > 1f)
            {
                int decodeW = Math.Clamp((int)MathF.Round(width), 320, 1920);
                int decodeH = Math.Max(1, (int)MathF.Round(decodeW * (height / width)));
                decode.Value = (decodeW, decodeH);
            }
            int baseW = decode.Value.Item1 > 0 ? decode.Value.Item1 : (int)width;
            int baseH = decode.Value.Item2 > 0 ? decode.Value.Item2 : Math.Max(1, (int)height);
            // The zoom scale still reaches the decode budget (recomputed per render, outside the resize latch).
            float scale = UseContext(Viewport.Scale);
            int dw = Design.ImageDecodeScale.For(baseW, scale);
            int dh = Math.Max(1, Design.ImageDecodeScale.For(baseH, scale));
            float aspect = (float)dw / dh;

            // THE HERO OWNS ITS OWN ENTRANCE, and the page no longer waits for it (`ArtistReadiness.BodyReady`): the
            // copy, the chart and the rails reveal as soon as THEY are ready, and the photograph scales up from its
            // 1.00 start to its 1.03 rest and fades 0 -> 1 when its bitmap actually lands, however long that takes.
            //
            // This is what the page-wide wait was standing in for, badly. Blocking the whole reveal on the decode
            // meant a slow photo held an otherwise fully-known page at a shimmer; and when it did land, the page
            // reveal and the image fade were two animations over the same pixels (stillwrong.mp4). One owner, one
            // entrance, keyed on the ready EDGE so it plays once per photo and never re-runs on a plain re-render.
            var image = UseImage(p.Url, dw, dh, ImagePriority.Visible, blurHash: null, transition: ImageTransition.None);
            bool ready = image.State is ImageState.Ready or ImageState.Failed;
            var zoom = UseRef(false);
            if (!zoom.Value && ready) zoom.Value = true;
            UseKeyframes(AnimChannel.ScaleX, zoom.Value ? s_enterScale : s_restScale,
                zoom.Value ? MotionTok.EmphasizedEnter.DurationMs : MotionTok.ControlFaster.DurationMs,
                loop: false, DepKey.From(zoom.Value));
            UseKeyframes(AnimChannel.ScaleY, zoom.Value ? s_enterScale : s_restScale,
                zoom.Value ? MotionTok.EmphasizedEnter.DurationMs : MotionTok.ControlFaster.DurationMs,
                loop: false, DepKey.From(zoom.Value));
            // Same ready edge as the scale settle. Reduced motion is a duration of 0, never a skipped call.
            float enterOpacityMs = Design.Reduced ? 0f : Design.Motion.Standard;
            UseKeyframes(AnimChannel.Opacity, zoom.Value ? s_enterOpacity : s_restOpacity,
                zoom.Value ? enterOpacityMs : MotionTok.ControlFaster.DurationMs,
                loop: false, DepKey.From(zoom.Value));

            return new BoxEl
            {
                Width = width, Height = height, ZStack = true, ScaleX = 1f, ScaleY = 1f,
                Children =
                [
                    new BoxEl
                    {
                        ZStack = true, ScaleX = FrameScale, ScaleY = FrameScale, OffsetY = -height * FrameLiftFraction,
                        Children =
                        [
                            // The FLAT theme neutral, never a cover tint: a 440-DIP tinted slab is a different design.
                            Ui.Image(p.Url, ImageFit.Cover, aspect: aspect, decodePx: dw, corners: 0f,
                                     placeholder: R.Layer.ArtPlaceholder, blurHash: null, transition: ImageTransition.None)
                                with { FocusX = 0.62f, FocusY = 0.34f },
                        ],
                    },
                ],
            };
        }

        static readonly Keyframe[] s_restScale = [new(0f, RestScale), new(1f, RestScale)];
    }
}
