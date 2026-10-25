// ── Themes/Classic/Structure/Views/Pages/ClassicArtistHero.cs — Classic's artist hero (themes plan 6.1, T10b1) ─────────────────
//
// From `Entities\Artist.UI.cs` §4.1 (`HeroBanner`, `HeroIdentity`, `HeroMeta`, `HeroActions`, `HeroArt`): the full-bleed hero and its collapse into the 56-DIP
// band (ArtistPage.Hero.cs:22-198). Horizontal tiers are a ZStack (photo · veil · copy centred); stacked tiers are a column (photo band · identity justified
// End) — two compositions, never one with a flag (ch 08 §9 #1). A null `play` is the skeleton arm: no veil leaf, no band, the actions as their shapes.
//
// The hero is built by the page view for the live page AND for the derived shimmer (the skeleton deriver cannot run a view slot), and its pinned,
// collapsing root has to stay a direct child of the page column, which is why it is a builder and not a key of its own. The sub-tiers, the heights, the arms
// and the copy measures are Classic's tier records (`Rules\Tiers.Artist.cs`, TC6: the core latches the metrics through them); the collapse arithmetic is
// `ArtistHeroLayout` (core); what the hero SAYS is `Artist.HeroText` (core facts).

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
    /// <summary>The full-bleed hero and its collapse into the 56-DIP band (ArtistPage.Hero.cs:22-198), in its tier's <paramref name="form"/>
    /// (`Rules\Tiers.Artist.cs`): the photo arms (copy centred or at the bottom over a horizontal veil), the field arm (the photograph over the identity
    /// column) or the overlay arm (the copy on the photograph over a vertical veil, the actions a row under it). The arm is keyed, so a tier crossing that
    /// swaps it cross-fades the two, and the pinned root's height tweens (TC6). <paramref name="inset"/> is the page column's inset
    /// (<see cref="ClassicArtistLayout.ColumnInset"/>, the one alignment source: the copy lines up with the magazine's capped column; NaN reads the gutter);
    /// <paramref name="menu"/> is the More button's menu where the form shows one.</summary>
    public static Element Build(in Artist.HeroText text, string uri, string? photoUrl, string? paletteUrl, float width,
                                in ArtistHeroMetrics m, ClassicArtistHeroForm form, ColorF accent, bool compactCanHit,
                                Action? play, Action? shuffle, Action? radio, Element? band,
                                uint headerAccent = 0, float inset = float.NaN, Func<ContextMenuModel?>? menu = null)
    {
        float w = MathF.Max(1f, width);
        float edge = float.IsNaN(inset) ? m.Gutter : inset;
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
        // The photograph's band eases its height through real layout when a crossing changes it (the Short cap at 680, the 1920 edge): it crops the
        // photograph (laid out at the height it was first decoded for) down or up with the hero's own reveal, so no void opens under a photo that
        // already stood at its new height while the hero and the magazine under it still eased.
        Element media = new BoxEl
        {
            Width = w, Height = photoH, ZStack = true, ClipToBounds = true, Animate = ClassicTierMotion.HeightReflow,
            TransformOriginX = 0.5f, TransformOriginY = 0f,
            EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, ArtistHeroLayout.PhotoFadeBandFor(photoH)),
            Children = [art],
        }.StretchFromTop().ParallaxY(ArtistHeroLayout.PhotoParallaxFraction, photoH);

        // Shared by every arm: the expanded presentation slides up and fades as the band takes over.
        ScrollEffectSpec[] collapseEffects =
        [
            new(ScrollEffect.Parallax(0.0, collapse, 0f, -collapse)),
            new(ScrollEffect.Fade(ArtistHeroLayout.ExpandedFadeStart(collapse), collapse, 1f, 0f)),
        ];

        var acts = new HeroActs(play, shuffle, radio, menu);
        Element expanded = form.Arm switch
        {
            ClassicArtistHeroArm.Field => FieldArm(in text, uri, w, height, edge, in m, form, media, accent, compactCanHit, collapseEffects, acts),
            ClassicArtistHeroArm.Overlay => OverlayArm(in text, uri, paletteUrl, w, height, photoH, edge, in m, form, media, accent, compactCanHit, collapseEffects,
                acts, headerAccent),
            _ => PhotoArm(in text, uri, paletteUrl, w, height, edge, in m, form, media, accent, compactCanHit, collapseEffects, acts, headerAccent),
        };
        // One arm on screen under its COMPOSITION's key: the centred and the bottom arms are one tree (the photo arm, its copy centred or at the foot), so a
        // crossing between them (1008, 1920, the Short cap at 680) keeps every part and moves the copy; a crossing to the field or the overlay, genuinely
        // different trees, cross-fades the two in the pinned z-stack under the declared tier swap.
        Element arm = new BoxEl
        {
            Key = s_armKeys[(int)form.Arm], Direction = 1, Width = w, Height = height,
            Enter = ClassicArtistTiers.ArmFade, Exit = ClassicArtistTiers.ArmFade, Transition = ClassicArtistTiers.ArmFadeMotion,
            Children = [expanded],
        };

        // Pinned at the viewport top and COLLAPSING into the compact band: over the collapse distance the presented height
        // shrinks from the banner height to the band, and the Leading collapse cuts the expanded presentation (paint and
        // input) at that edge by itself — the presentation carries its own slide-away (collapseEffects). No ClipToBounds:
        // a box clip here would also cut the photo's overscroll stretch above the root's top (a dark band over the photo).
        // A tier crossing eases the hero's height on the detail hero's tween (280 ms SmoothOut) THROUGH REAL LAYOUT, so the collapse edge, which
        // cuts the presentation at the laid-out height, the photograph's band and the magazine under it all move with it. A revealed height (the
        // presented extent easing over a layout already at its new height) left the collapse cut at the new height at once: a 152 DIP void opened
        // between the photograph and the magazine still gliding up to it (the Short cap at 680).
        return new BoxEl
        {
            Direction = 1, Height = height, ZStack = true, Animate = ClassicTierMotion.HeightReflow,
            Children = band is null ? [arm] : [arm, band.Value],
        }.Sticky(0f).Collapse(collapse, ArtistHeroLayout.CompactIdentityHeight, CollapseAnchor.Leading);
    }

    static readonly string[] s_armKeys = ["artist-hero:photo", "artist-hero:photo", "artist-hero:field", "artist-hero:overlay"];

    /// <summary>The hero's verbs: Play, Shuffle, Radio and the More button's menu (all null on the skeleton arm).</summary>
    readonly record struct HeroActs(Action? Play, Action? Shuffle, Action? Radio, Func<ContextMenuModel?>? Menu);

    /// <summary>The photo arms: the photograph fills the hero, a horizontal veil holds to the copy's edge, the copy (with its actions) centred or at the
    /// bottom-left, inset by the page column's <paramref name="edge"/>.</summary>
    static Element PhotoArm(in Artist.HeroText text, string uri, string? paletteUrl, float w, float height, float edge, in ArtistHeroMetrics m,
                            ClassicArtistHeroForm form, Element media, ColorF accent, bool compactCanHit, ScrollEffectSpec[] collapseEffects,
                            HeroActs acts, uint headerAccent)
    {
        bool bottom = form.Arm == ClassicArtistHeroArm.Bottom;
        Element copy = new BoxEl
        {
            Width = w, Height = height, Direction = 1,
            Justify = bottom ? FlexJustify.End : FlexJustify.Center, AlignItems = FlexAlign.Start,
            Padding = new Edges4(edge, Spacing.XXL, edge, bottom ? form.CopyBottom : Spacing.XXL),
            Children = [Identity(in text, uri, w, edge, in m, form, accent, acts, actions: true)],
        };
        // A cover-keyed leaf: a late grading swaps the gradient without rebuilding the banner (ch 08 §4 row 3).
        // headerAccent (row.HeaderAccent) is the raw payload rung the page passes through from the artist row.
        // The veil stretches WITH the photo on a top overpan (the same top-anchored StretchFromTop): a fixed veil
        // over a growing photo left an unveiled band above the hero's top edge.
        Element veil = acts.Play is null
            ? new BoxEl()
            : new BoxEl
            {
                // Eases its height with the photograph's band on a crossing, the veil stretched over it (its stretch arm), so no strip of the
                // photograph shows unveiled while the hero's height eases.
                Width = w, Height = height, ZStack = true, HitTestVisible = false, Animate = ClassicTierMotion.HeightReflow,
                TransformOriginX = 0.5f, TransformOriginY = 0f,
                Children =
                [
                    Palette.ArtistHeroVeil(paletteUrl, vertical: false, w, float.NaN, key: "artist-veil:" + uri,
                                           payloadAccent: headerAccent, hold: ClassicArtistRules.HeroVeilHold(w, in m, form, edge)),
                ],
            }.StretchFromTop();
        return new BoxEl
        {
            Width = w, Height = height, ZStack = true,
            HitTestVisible = !compactCanHit, ScrollEffects = collapseEffects,
            Children = [media, veil, copy],
        };
    }

    /// <summary>The field arm: the photograph is a field at the top and the identity column (with its actions) sits below it. No veil: no type sits on the
    /// photograph (ch 08 §0 #2).</summary>
    static Element FieldArm(in Artist.HeroText text, string uri, float w, float height, float edge, in ArtistHeroMetrics m, ClassicArtistHeroForm form,
                            Element media, ColorF accent, bool compactCanHit, ScrollEffectSpec[] collapseEffects, HeroActs acts)
        => new BoxEl
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
                    Padding = new Edges4(edge, Spacing.M, edge, Spacing.XL),
                    Children = [Identity(in text, uri, w, edge, in m, form, accent, acts, actions: true)],
                },
            ],
        };

    /// <summary>The overlay arm (C-Artist-Compact): the photograph is a band of its own with the copy at its bottom over a vertical veil, and the actions are
    /// a row of their own under it, at the Compact targets.</summary>
    static Element OverlayArm(in Artist.HeroText text, string uri, string? paletteUrl, float w, float height, float photoH, float edge,
                              in ArtistHeroMetrics m, ClassicArtistHeroForm form, Element media, ColorF accent, bool compactCanHit,
                              ScrollEffectSpec[] collapseEffects, HeroActs acts, uint headerAccent)
    {
        Element veil = acts.Play is null
            ? new BoxEl()
            : new BoxEl
            {
                Width = w, Height = photoH, ZStack = true, HitTestVisible = false,
                // Held from the copy's top down: on a bright photograph the Verified line sat on the veil's thin ramp (2.3 : 1).
                Children = [Palette.ArtistHeroVeil(paletteUrl, vertical: true, w, photoH, key: "artist-veil:" + uri, payloadAccent: headerAccent,
                                                   hold: ClassicArtistRules.OverlayVeilHold)],
            };
        Element copy = new BoxEl
        {
            Width = w, Height = photoH, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Start,
            Padding = new Edges4(edge, Spacing.M, edge, form.CopyBottom),
            Children = [Identity(in text, uri, w, edge, in m, form, accent, acts, actions: false)],
        };
        return new BoxEl
        {
            Width = w, Height = height, Direction = 1, Gap = ClassicArtistTiers.OverlayActionsGap,
            HitTestVisible = !compactCanHit, ScrollEffects = collapseEffects,
            Children =
            [
                new BoxEl { Width = w, Height = photoH, ZStack = true, Children = [media, veil, copy] },
                new BoxEl
                {
                    Direction = 0, MinWidth = 0f, Padding = new Edges4(edge, 0f, edge, 0f),
                    Children = [Actions(text.Name, uri, accent, acts, form)],
                },
            ],
        };
    }

    /// <summary>Verified · name · first sentence · meta, then (on every arm but the overlay) the action row (Hero.cs:34-95).</summary>
    static Element Identity(in Artist.HeroText text, string uri, float w, float edge, in ArtistHeroMetrics m, ClassicArtistHeroForm form, ColorF accent,
                            HeroActs acts, bool actions)
    {
        // The skeleton arm (no play) states every text run as its LINES: the deriver turns a text leaf into a bar 0.72 of its size tall, so the
        // copy came out ~70 DIP short of the loaded one at Wide (an 84/96 name became a 60 bar, each 14/20 line a 10 one) and, centred on the
        // photo, every bar and the action row stood off the words (tuned Classic X2).
        bool shimmer = acts.Play is null;
        Element verified = text.Verified && form.Details
            ? new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Children =
                [
                    InfoBadge.Icon(Icons.Accept, color: accent),
                    // Primary on the overlay arm (no action row: the copy sits on the photograph's veil, where secondary read 3.7 : 1).
                    Lines(Caption(Loc.Get(Strings.Artist.Verified)) with { Color = actions ? Tok.TextSecondary : Tok.TextPrimary }, 1, shimmer),
                ],
            }
            : new BoxEl();

        TextEl name = form.Name switch
        {
            ClassicArtistName.Display => R.Type.ArtistDisplay(text.Name),
            ClassicArtistName.Title => R.Type.ArtistTitle(text.Name),
            _ => R.Type.ArtistCompactTitle(text.Name),
        };
        name = name with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2, MinWidth = 0f };
        Element nameLine = Lines(name, 1, shimmer);

        // The lead is a COMMIT-computed column (ch 08 GAP 3): the hero never strips HTML on a render.
        Element bio = text.Lead.Length == 0 || form.BioLines <= 0
            ? new BoxEl()
            : Lines(Body(text.Lead) with
            {
                Color = Tok.TextSecondary, Wrap = form.BioLines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = form.BioLines,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, MaxWidth = form.BioMax,
            }, Math.Min(form.BioLines, ClassicArtistRules.HeroLeadSkeletonLines), shimmer);

        // Meta stays ONE horizontal row on the photo arms and wraps on the overlay band; only the Narrow field stacks it.
        Element meta = form.Details
            ? Meta(in text, stacked: form.Tier == ArtistHeroTier.Narrow && form.Arm == ClassicArtistHeroArm.Field,
                   wrap: form.Arm == ClassicArtistHeroArm.Overlay, shimmer)
            : new BoxEl();

        return new BoxEl
        {
            // Moves (centred or at the foot) and changes its measure with the form inside the one photo arm: it reflows into its new box.
            Key = "artist-hero-copy", Direction = 1, Animate = shimmer ? null : ClassicTierMotion.Reflow,
            // D18: on the centred photo arm the copy ends near 56 % of the hero and the veil holds to that edge (ClassicArtistRules).
            Width = ClassicArtistRules.HeroCopyWidth(w, in m, form, edge),
            MaxWidth = m.CopyMaxWidth, MinWidth = 0f, Gap = Spacing.S,
            // The page-level SkelRegion owns the single reveal. A second hero-copy entrance here made the
            // already-revealed page flash and move for another frame when the overview landed.
            Children = actions
                ?
                [
                    new BoxEl { Direction = 1, Gap = form.CopyGap, MinWidth = 0f, Children = [verified, nameLine, bio, meta] },
                    Actions(text.Name, uri, accent, acts, form),
                ]
                : [new BoxEl { Direction = 1, Gap = form.CopyGap, MinWidth = 0f, Children = [verified, nameLine, bio, meta] }],
        };
    }

    /// <summary>"#N in the world" (accent) · monthly listeners · followers — a zero stat drops its run (Hero.cs:236-259). Wrapped (the overlay band), its
    /// lines sit 4 apart and its runs 16 (C-Artist-Compact's <c>gap: 4px 16px</c>): the row's one gap is the line gap and each run carries the rest of the
    /// run gap as its trailing margin (a line's last margin only moves its break by that much, never a run).</summary>
    static Element Meta(in Artist.HeroText text, bool stacked, bool wrap, bool shimmer)
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
        bool wrapped = wrap && !stacked;
        if (wrapped)
            for (int i = 0; i < kids.Length; i++)
                kids[i] = new BoxEl
                {
                    Direction = 0, MinWidth = 0f,
                    Margin = new Edges4(0f, 0f, ClassicArtistTiers.OverlayMetaColumnGap - ClassicArtistTiers.OverlayMetaRowGap, 0f),
                    Children = [kids[i]],
                };
        return new BoxEl
        {
            Direction = (byte)(stacked ? 1 : 0),
            AlignItems = stacked ? FlexAlign.Start : FlexAlign.Center,
            Gap = stacked ? Spacing.XS : wrapped ? ClassicArtistTiers.OverlayMetaRowGap : Spacing.L,
            Wrap = wrapped,
            MinWidth = 0f,
            Children = kids,
        };
    }

    /// <summary>Play (the cover's accent) · Shuffle · Follow · Radio; [Play, Follow] over [Shuffle, Radio] where one row does not fit; Play · Follow on the
    /// Short strip (Hero.cs:200-234). At the Compact targets every control is 40 tall and Shuffle is its glyph (C-Artist-Compact). Never skeleton content
    /// (ch 08 §9 #8). Keyed, so the tier tests find the row.
    /// <para>Workstream B grammar: [<see cref="Controls.PlayButton(ColorF,Action,string?,string?)"/>][Shuffle, a stock
    /// Standard <c>Button.Create</c>][<see cref="Controls.FollowToggle"/>][radio, <see cref="Controls.IconAction"/>], then (where the form shows it,
    /// C-Artist-Huge) the More button: the artist's menu, a same-size blank on the skeleton arm.</para></summary>
    static Element Actions(string name, string uri, ColorF accent, HeroActs acts, ClassicArtistHeroForm form)
    {
        Action? play = acts.Play, shuffle = acts.Shuffle, radio = acts.Radio;
        bool large = form.Target >= ClassicArtistTiers.CompactTarget;
        var size = large ? ControlSize.Large : ControlSize.Medium;
        Element playButton = Controls.PlayButton(accent, play ?? s_noop, size, Loc.Get(Strings.Artist.Play));
        // Keyed on the uri: FollowToggle carries its uri as a mount-frozen field.
        Element follow = uri.Length == 0
            ? Controls.FollowToggle.SkeletonShape()
            : Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name, Size = size })
                with { Key = "artist-follow:" + uri, SkeletonProxy = s_followShape };

        if (form.Actions == ClassicHeroActions.Primary)
            return new BoxEl
            {
                Key = "artist-hero-actions", Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Children = [playButton, follow],
            }.Skeletonized(false);

        Element shuffleButton = large
            ? Controls.Named(Controls.IconAction(Icons.Shuffle, shuffle, size: form.Target), Loc.Get(Strings.Detail.Shuffle))
            : Button.Create(Loc.Get(Strings.Detail.Shuffle), shuffle ?? s_noop, ButtonAppearance.Standard, glyph: Icons.Shuffle);
        Element radioButton = Controls.Named(
            Controls.IconAction(Icons.RadioTower, radio, size: form.Target), Loc.Get(Strings.Artist.ArtistRadio));

        if (form.Actions == ClassicHeroActions.TwoRows)
            return new BoxEl
            {
                Key = "artist-hero-actions", Direction = 1, Gap = ClassicArtistTiers.ActionRowGap,
                Children =
                [
                    new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = [playButton, follow] },
                    new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = [shuffleButton, radioButton] },
                ],
            }.Skeletonized(false);

        Element[] row = form.More
            ? [playButton, shuffleButton, follow, radioButton, MoreButton(acts.Menu, form.Target, uri)]
            : [playButton, shuffleButton, follow, radioButton];
        return new BoxEl
        {
            Key = "artist-hero-actions", Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children = row,
        }.Skeletonized(false);
    }

    /// <summary>The hero's More button (C-Artist-Huge): the artist's menu at the form's edge, keyed on the uri so the tier tests find it; a same-size blank
    /// where there is no menu (the skeleton arm).</summary>
    static Element MoreButton(Func<ContextMenuModel?>? menu, float edge, string uri)
        => menu is null
            ? new BoxEl { Width = edge, Height = edge, Shrink = 0f, Corners = Radii.ControlAll }
            : ClassicDetailKit.MoreButton(menu, edge) with { Key = "artist-hero-more:" + uri };

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
            // The crop waits for the photograph's subject (faces place it; it always settles), so the entrance plays once, at the final crop;
            // until then the slot draws no bitmap (the decode above runs meanwhile).
            PhotoSubject? subject = Palette.Subject(p.Url);
            bool settled = subject is not null;
            bool faces = subject is { Faces: true };
            bool ready = settled && image.State is ImageState.Ready or ImageState.Failed;
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
                // The band's height, eased with the band on a crossing (the same reflow), so the photograph fills the band frame by frame
                // whichever way the hero's height goes.
                Width = width, Height = height, ZStack = true, ScaleX = 1f, ScaleY = 1f, Animate = ClassicTierMotion.HeightReflow,
                Children =
                [
                    new BoxEl
                    {
                        ZStack = true, ScaleX = FrameScale, ScaleY = FrameScale, OffsetY = -height * FrameLiftFraction,
                        Children =
                        [
                            // The FLAT theme neutral, never a cover tint: a 440-DIP tinted slab is a different design. Until the subject settles
                            // the slot shows that neutral alone (no source), as it does while the bitmap decodes.
                            Ui.Image(settled ? p.Url : "", ImageFit.Cover, aspect: aspect, decodePx: dw, corners: 0f,
                                     placeholder: R.Layer.ArtPlaceholder, blurHash: null, transition: ImageTransition.None)
                                with
                                {
                                    FocusX = faces ? subject!.Value.X : ClassicArtistRules.PhotoFocusX,
                                    FocusY = faces ? subject!.Value.Y : ClassicArtistRules.PhotoFocusY,
                                    FocusFrameX = faces ? ClassicArtistRules.PhotoFacesAtX : 0.5f,
                                    FocusFrameY = faces ? ClassicArtistRules.PhotoFacesAtY : 0.5f,
                                },
                        ],
                    },
                ],
            };
        }

        static readonly Keyframe[] s_restScale = [new(0f, RestScale), new(1f, RestScale)];
    }
}
