using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Full-bleed artist photography with a semantic copy veil. The image owns the entire hero; responsive pressure changes
// the veil axis and copy placement, never the photograph's extent.
sealed partial class ArtistPage : Component
{
    readonly Signal<float> _heroWidth = new(ArtistHeroLayout.WideWidth);

    Element Banner(Artist a, string uri, Action play, Action shuffle, Action radio,
                   bool compactCanHit, ContextPivotItem[] pivot, IReadSignal<float> pageScroll,
                   IReadSignal<float> pageViewportHeight, IReadSignal<bool> pageAtEnd)
    {
        float width = MathF.Max(1f, _heroWidth.Value);
        var tier = UseRef(ArtistHeroTier.Wide);
        var metrics = ArtistHeroLayout.For(width, tier.Value);
        tier.Value = metrics.Tier;
        float height = metrics.MinHeight;
        float collapseDistance = ArtistHeroLayout.CollapseDistance(height);
        var background = a.HeaderImage ?? a.Image;

        Element Identity()
        {
            Element verified = a.Verified
                ? new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                    Children =
                    [
                        InfoBadge.Icon(Icons.Accept, color: _accent),
                        Ui.Caption(Loc.Get(Strings.Artist.Verified)) with { Color = Tok.TextSecondary },
                    ],
                }
                : new BoxEl();

            TextEl name = metrics.Tier switch
            {
                ArtistHeroTier.Wide => WaveeType.ArtistDisplay(a.Name),
                ArtistHeroTier.Medium => WaveeType.ArtistTitle(a.Name),
                _ => WaveeType.ArtistCompactTitle(a.Name),
            };
            name = name with
            {
                Color = Tok.TextPrimary,
                Wrap = TextWrap.Wrap,
                MaxLines = metrics.Stacked ? 3 : 2,
                MinWidth = 0f,
            };

            string? sentence = FirstSentence(a.Bio);
            Element bio = sentence is null
                ? new BoxEl()
                : Ui.Body(sentence) with
                {
                    Color = Tok.TextSecondary,
                    Wrap = TextWrap.Wrap,
                    MaxLines = 2,
                    Trim = TextTrim.CharacterEllipsis,
                    MinWidth = 0f,
                };

            return new BoxEl
            {
                Direction = 1,
                Width = MathF.Min(metrics.CopyMaxWidth, MathF.Max(1f, width - 2f * metrics.Gutter)),
                MaxWidth = metrics.CopyMaxWidth,
                MinWidth = 0f,
                Gap = Spacing.S,
                Enter = new EnterExit(Dy: Spacing.M, Opacity: 0f, Active: true),
                Transition = MotionTok.EmphasizedEnter,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = Spacing.S, MinWidth = 0f,
                        Children = [verified, name, bio, HeroMeta(a, metrics.Stacked)],
                    },
                    HeroActions(a, uri, play, shuffle, radio, metrics.Tier),
                ],
            };
        }

        float photoH = ArtistHeroLayout.PhotoHeightFor(metrics);
        ColorF Placeholder() => Surfaces.ArtworkPlaceholder;
        Element art = background?.Url is { Length: > 0 } source
            ? Embed.Comp(() => new HeroArt(source, _heroWidth, background.BlurHash, Placeholder))
                with { Key = "heroart:" + source }
            : new BoxEl { Width = width, Height = photoH, Fill = Placeholder() };
        Element media = new BoxEl
        {
            Width = width, Height = photoH, ZStack = true, ClipToBounds = true,
            TransformOriginX = 0.5f, TransformOriginY = 0f,
            EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, ArtistHeroLayout.PhotoFadeBandFor(photoH)),
            Children = [art],
        }.StretchFromTop().ParallaxY(ArtistHeroLayout.PhotoParallaxFraction, photoH);

        void MeasureHero(RectF bounds)
        {
            if (bounds.W > 0f && MathF.Abs(bounds.W - _heroWidth.Peek()) > 0.5f)
                _heroWidth.Value = bounds.W;
        }

        // Collapse binds are shared by both arms: the whole expanded presentation slides up and fades as the band
        // takes over.
        ScrollBindDsl[] collapseBinds =
        [
            new() { From = ScrollChannel.Offset, To = BindSink.TransY,
                Range = ScrollRange.Px(0f, collapseDistance),
                OutStart = 0f, OutEnd = -collapseDistance, Ease = Easing.Linear },
            new() { From = ScrollChannel.Offset, To = BindSink.Opacity,
                Range = ScrollRange.Px(ArtistHeroLayout.ExpandedFadeStart(collapseDistance), collapseDistance),
                OutStart = 1f, OutEnd = 0f, Ease = Easing.Linear },
        ];

        Element expandedPresentation;
        if (metrics.Stacked)
        {
            // Compact/Narrow: the photograph is a FIELD on top and the identity column sits BELOW it on the page
            // surface — no overlay veil (there is no copy on the picture to protect), no bottom-justified pile at the
            // photo's seam. The photo's own EdgeFade melts it into the page tone the type sits on.
            expandedPresentation = new BoxEl
            {
                Width = width, Height = height, Direction = 1,
                HitTestVisible = !compactCanHit,
                ScrollBinds = collapseBinds,
                Children =
                [
                    media,
                    new BoxEl
                    {
                        Grow = 1f, MinHeight = 0f, Direction = 1,
                        Justify = FlexJustify.Center, AlignItems = FlexAlign.Start,
                        Padding = new Edges4(metrics.Gutter, Spacing.M, metrics.Gutter, Spacing.XL),
                        Children = [Identity()],
                    },
                ],
            };
        }
        else
        {
            Element copy = new BoxEl
            {
                Width = width, Height = height,
                Direction = 1,
                Justify = FlexJustify.Center,
                AlignItems = FlexAlign.Start,
                Padding = new Edges4(metrics.Gutter, Spacing.XXL, metrics.Gutter, Spacing.XXL),
                Children = [Identity()],
            };

            expandedPresentation = new BoxEl
            {
                Width = width, Height = height, ZStack = true,
                HitTestVisible = !compactCanHit,
                ScrollBinds = collapseBinds,
                Children =
                [
                    media,
                    CoverPaletteLeaves.ArtistHeroVeil(
                        PaletteImageUrl(a), metrics.VeilAxis, width, height, key: "artist-veil:" + uri),
                    copy,
                ],
            };
        }
        // Invisible at scroll offset 0 in the real page (its ScrollBind ramps Opacity 0→1 only past the collapse
        // threshold) — but SkeletonDeriver strips ScrollBinds from container nodes (they'd otherwise become dead
        // parallax/pin math on a static tree), so without this the derived shimmer falls back to the default Opacity=1
        // and paints a phantom avatar/name/button row above the hero on every artist page load. Off keeps its slot
        // (an empty spacer, harmless inside this ZStack) without shimmering content nobody sees yet.
        Element compactPresentation = ArtistCompactBar.Build(a, uri, width, collapseDistance,
            _accent, play, compactCanHit, pivot, _anchors, pageScroll, pageViewportHeight, pageAtEnd).Skeletonized(false);

        return new BoxEl
        {
            Direction = 1,
            Height = height,
            ClipToBounds = true,
            ZStack = true,
            OnBoundsChanged = MeasureHero,
            Children = [expandedPresentation, compactPresentation],
        }.Collapse(height, ArtistHeroLayout.CompactIdentityHeight, collapseDistance);
    }

    Element HeroActions(Artist a, string uri, Action play, Action shuffle, Action radio, ArtistHeroTier tier)
    {
        Element playButton = WaveeCta.Play(_accent, play, Loc.Get(Strings.Artist.Play));
        Element follow = Embed.Comp(() => new FollowButton(uri, a.Name)) with
        {
            Key = "artist-follow:" + uri,
            SkeletonProxy = FollowButton.SkeletonShape,
        };

        // Icon-only 36 circles — the labeled stock Button.Create arms were a second, larger grammar beside the
        // Play / Follow capsules and made the row read as oversized (user report: "buttons are bigged").
        Element shuffleButton = ToolTip.Wrap(
            WaveeCta.Icon(Icons.Shuffle, shuffle, ButtonAppearance.Subtle), Loc.Get(Strings.Detail.Shuffle));
        Element radioButton = ToolTip.Wrap(
            WaveeCta.Icon(Icons.RadioTower, radio, ButtonAppearance.Subtle), Loc.Get(Strings.Artist.ArtistRadio));

        if (tier == ArtistHeroTier.Narrow)
        {
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S,
                Children =
                [
                    new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = [playButton, follow] },
                    new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = [shuffleButton, radioButton] },
                ],
            }.Skeletonized(false);
        }

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children = [playButton, shuffleButton, follow, radioButton],
        }.Skeletonized(false);
    }

    static Element HeroMeta(Artist a, bool stacked)
    {
        Element rank = a.WorldRank > 0
            ? Ui.BodyStrong(Strings.Artist.WorldRank(a.WorldRank.ToString())) with { Color = Tok.AccentTextPrimary }
            : new BoxEl();
        Element listeners = a.MonthlyListeners > 0
            ? Ui.Body(Count(a.MonthlyListeners) + " " + Loc.Get(Strings.Artist.MetaMonthly)) with { Color = Tok.TextSecondary }
            : new BoxEl();
        Element followers = a.Followers > 0
            ? Ui.Body(Count(a.Followers) + " " + Loc.Get(Strings.Artist.MetaFollowers)) with { Color = Tok.TextSecondary }
            : new BoxEl();

        var kids = new System.Collections.Generic.List<Element>(3);
        if (a.WorldRank > 0) kids.Add(rank);
        if (a.MonthlyListeners > 0) kids.Add(listeners);
        if (a.Followers > 0) kids.Add(followers);
        return new BoxEl
        {
            Direction = (byte)(stacked ? 1 : 0),
            AlignItems = stacked ? FlexAlign.Start : FlexAlign.Center,
            Gap = stacked ? Spacing.XS : Spacing.L,
            MinWidth = 0f,
            Children = kids.ToArray(),
        };
    }

    static string? FirstSentence(string? bio)
    {
        if (string.IsNullOrWhiteSpace(bio)) return null;
        string plain = StripHtml(bio);
        if (plain.Length == 0) return null;
        int end = plain.IndexOf(". ", StringComparison.Ordinal);
        return end > 20 ? plain[..(end + 1)] : plain;
    }

    static string StripHtml(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        bool tag = false;
        foreach (char c in value)
        {
            if (c == '<') tag = true;
            else if (c == '>') tag = false;
            else if (!tag && c is not ('\r' or '\n')) result.Append(c);
        }
        return result.ToString().Trim();
    }
}

sealed class HeroArt : Component
{
    const float RestScale = 1.05f;
    const float FrameScale = 1.08f;
    const float FrameLiftFraction = 0.04f;
    static readonly Keyframe[] ZoomIn = [new(0f, 1f), new(1f, RestScale, Easing.FluentDecelerate)];
    static readonly Keyframe[] Rest = [new(0f, RestScale), new(1f, RestScale)];

    readonly string _url;
    readonly IReadSignal<float> _width;
    readonly string? _blurHash;
    readonly Func<ColorF> _placeholder;

    public HeroArt(string url, IReadSignal<float> width, string? blurHash, Func<ColorF> placeholder)
    { _url = url; _width = width; _blurHash = blurHash; _placeholder = placeholder; }

    public override Element Render()
    {
        float width = MathF.Max(1f, _width.Value);
        var tier = UseRef(ArtistHeroTier.Wide);
        var metrics = ArtistHeroLayout.For(width, tier.Value);
        tier.Value = metrics.Tier;
        // The photo BAND, not the hero: on stacked tiers the photograph is the top slice and the identity column
        // owns the rest — sized through the same helper the banner's media box uses, so the two cannot disagree.
        float height = ArtistHeroLayout.PhotoHeightFor(metrics);

        var decode = UseRef((0, 0));
        if (decode.Value.Item1 <= 0 && width > 1f)
        {
            int decodeW = Math.Clamp((int)MathF.Round(width), 320, 1920);
            int decodeH = Math.Max(1, (int)MathF.Round(decodeW * (height / width)));
            decode.Value = (decodeW, decodeH);
        }
        int dw = decode.Value.Item1 > 0 ? decode.Value.Item1 : (int)width;
        int dh = decode.Value.Item2 > 0 ? decode.Value.Item2 : Math.Max(1, (int)height);
        float aspect = (float)dw / dh;

        var settled = UseRef(false);
        var zoom = UseRef(false);
        bool warm = false;
        if (!settled.Value)
        {
            var state = UseImage(_url, dw, dh).State;
            warm = state == ImageState.Ready;
            if (state == ImageState.Ready) { settled.Value = true; zoom.Value = true; }
            else if (state == ImageState.Failed) settled.Value = true;
        }
        Keyframe[] keys = zoom.Value ? ZoomIn : Rest;
        float duration = zoom.Value ? MotionTok.EmphasizedEnter.DurationMs : MotionTok.ControlFaster.DurationMs;
        UseKeyframes(AnimChannel.ScaleX, keys, duration, false, DepKey.From(zoom.Value));
        UseKeyframes(AnimChannel.ScaleY, keys, duration, false, DepKey.From(zoom.Value));
        var reveal = warm || settled.Value ? ImageTransition.None : ImageTransition.Fade(MotionTok.StandardEnter.DurationMs);

        return new BoxEl
        {
            Width = width, Height = height, ZStack = true, ScaleX = RestScale, ScaleY = RestScale,
            Children =
            [
                new BoxEl
                {
                    ZStack = true,
                    ScaleX = FrameScale,
                    ScaleY = FrameScale,
                    OffsetY = -height * FrameLiftFraction,
                    Children =
                    [
                        Ui.Image(_url, ImageFit.Cover, aspect: aspect, decodePx: dw, corners: 0f,
                                 placeholder: _placeholder(), blurHash: _blurHash, transition: reveal)
                            with { FocusX = 0.62f, FocusY = 0.34f },
                    ],
                },
            ],
        };
    }
}
