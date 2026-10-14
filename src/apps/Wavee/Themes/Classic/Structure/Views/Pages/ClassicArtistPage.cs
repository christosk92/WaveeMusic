// ── Themes/Classic/Structure/Views/Pages/ClassicArtistPage.cs — Classic's `Pages.Artist` (themes plan 6.1, T10b1) ──────────────────
//
// From `Entities\Artist.Page.cs` (`PageHost.Render`, `Compose`, `Magazine`, `BandBar`, `PageShimmer`, `MagazineShimmer`): the render half of the artist page.
//
//   Pages.Artist
//   └─ Ctx.Provide(Design.AccentCtx = the page accent) ▸ Ctx.Provide(LazyScroll = the scroll offset)
//      └─ Column  Key=artist-page:<route>  OnBoundsChanged = the host's measure
//         ├─ the shell-tint claim (a zero-size leaf the host built)
//         └─ ScrollView  Key=artist-scroll:<route>  (EdgeCues None: the band itself is the occlusion cue)
//            └─ keyed child artist-body:<scope epoch>
//               └─ SkelRegionEl (Reveal.Soft, Group null: the chart's inner region must not join this one)
//                  ├─ Content  ZStack [ the blend wash clipped at the band,
//                  │                    Column [ hero (pinned, collapsing into the band) · sentinel · magazine clipped at the band ] ]
//                  └─ Shimmer  the SAME hero over a placeholder, the divider, the magazine's skeleton
//
// THE CLIP IS THE CONTRACT: the band paints nothing, so nothing may render into its 56 DIP — the magazine and the wash are both cut at the line, and the
// magazine's cut is feathered exactly while its clip is engaged (`EdgeFadeSpec.WhileStuck`; nothing is softened at rest).
//
// What the view does NOT own: the demands, the readiness, the section plan and the pivot's membership, the hero metrics' hysteresis, the accent, the scroll
// spy and its anchors (the host hands each pivot destination's `OnRealized`), the tint claim, and the Play / Shuffle / Radio decisions. One
// `ArtistHeroMetrics` per render feeds the hero, the band's gutter, the magazine's gutter and the wash (ch 08 §9 inconsistency #1).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's artist page.</summary>
internal static class ClassicArtistPage
{
    static readonly string[] s_secKeys = SectionKeys("sec:");
    static readonly string[] s_anchorKeys = SectionKeys("anchor:");
    static readonly Func<Element> s_emptyShape = static () => new BoxEl();

    static string[] SectionKeys(string prefix)
    {
        var keys = new string[ArtistSections.Count];
        for (int i = 0; i < keys.Length; i++) keys[i] = prefix + ArtistSections.Key((ArtistSection)i);
        return keys;
    }

    /// <summary>The registered template of <see cref="ViewKeys.ArtistPage"/>.</summary>
    public static Element Template(ArtistPageProps p, in ViewContext ctx)
    {
        // The latched layout decisions outlive a render (they were the host's fields); the styles resolve once per mount, as the host cached them.
        var latchCell = ctx.Hooks.UseRef<ClassicArtistLatch?>(null);
        var latch = latchCell.Value ??= new ClassicArtistLatch();
        var registry = ctx.Hooks.UseContext(ViewRegistry.Context);
        var surface = latch.Surface ??= ListStyleLookup.Of(registry, StyleKeys.Surface);
        var chart = latch.Chart ??= ListStyleLookup.Of(registry, StyleKeys.ArtistChart);

        // ONE visual swap: overview + measured width + chart settled. Snap, not FadeOnly: waiting for the bitmap then fading the whole tree from 0 hid the
        // photo again (stillwrong.mp4).
        Element? body = p.BodyReady ? Compose(p, latch, surface) : null;

        // ONE region, and the shimmer is DERIVED — `Shimmer` is a ShimmerSource (a representative tree at the loaded page's geometry), never a tree to mount.
        // Rendering it directly painted its own placeholder copy as if it were data: "Artist name", "A first sentence of the biography stands in this line.",
        // "10,000,000 Monthly listeners", "Top track title" (evenworsenow.mp4).
        //
        // Reveal.Soft - the page-level reveal the rest of the app uses (Concert.Page's Region): the whole tree blur-rises in as ONE (Opacity + TranslateY + Blur
        // -> rest) over the shimmer dissolving out beneath it. Not None, which means "the content animates its own entrance" - and this content has none, so the
        // swap landed as a hard cut with nothing moving at all (shitttt.mp4).
        //
        // The reveal is safe to animate because `BodyReady` has already waited for the measured width and a settled chart, so the tree that rises is complete.
        // Group null, not routeKey: the chart's inner region must NOT join this one, or top tracks plays a second wave after the page has already revealed.
        Element region = new SkelRegionEl(
            Pending: p.Pending, Failed: p.Failed, Content: () => body ?? new BoxEl(), ShimmerSource: () => Shimmer(p, chart),
            OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: p.Retry),
            Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default,
            Group: null, SmoothResize: false);

        Element scroll = ScrollView(new BoxEl
        {
            Direction = 1,
            // Keyed CHILD (a key on a single content root is inert): a scope switch remounts the sections, whose
            // anchors re-register after the host's render-time reset.
            Children = [new BoxEl { Key = "artist-body:" + p.ScopeKey, Direction = 1, Children = [region] }],
        }) with
        {
            Key = "artist-scroll:" + p.RouteKey, Grow = 1f, ScrollKey = p.ScrollKey,
            // The spy resolves against, and a pivot click scrolls, THIS viewport — never a nested scroller.
            OnRealized = p.CaptureViewport,
            // The band itself is the occlusion cue; the default colour cue resolved the wrong surface.
            EdgeCues = ScrollEdgeCues.None,
            Handle = p.Scroll,
        };

        // The page PROVIDES its accent (Design.AccentCtx): the heart and every other ambient consumer read it.
        return Ctx.Provide(Design.AccentCtx.Slot, p.PageAccent,
            Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>?)p.ScrollY, new BoxEl
            {
                Key = "artist-page:" + p.RouteKey, Grow = 1f, Direction = 1, OnBoundsChanged = p.Measure,
                Children = [p.Tint, scroll],
            }));
    }

    // ── the loaded page ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The magazine (ArtistPage.cs:188-352): [wash clipped at the band] under [hero · sentinel · magazine
    /// clipped at the band]. The host built the section plan, the anchors and the pivot in ONE pass, so the pivot and the
    /// sections can never disagree about what the page renders. <see cref="ArtistPageProps.HeroUrl"/> is the page's LATCHED
    /// hero-art identity (ch 08 BUG E) — never re-read from the row here, so the same latch that keeps the derived shimmer's
    /// caller from unmounting the photo also decides what it paints.</summary>
    static Element Compose(ArtistPageProps p, ClassicArtistLatch latch, SurfaceStyle? surface)
    {
        Artist a = p.Artist;
        string uri = a.Uri.Text;
        ColorF accent = p.Accent;
        var m = p.Metrics;
        float width = p.Width;

        // EVERY section keyed: sections appear mid-stream as data lands, and keyless siblings would pair against
        // their neighbours' old subtrees (ArtistPage.cs:213-221).
        var plan = p.Sections;
        var sections = new Element[plan.Count];
        for (int i = 0; i < sections.Length; i++)
        {
            var slot = plan[i];
            Element sectionBody = ClassicArtistSections.Body(slot.Section, p, latch, surface) with { Key = s_secKeys[(int)slot.Section] };
            if (slot.Anchor is null) { sections[i] = sectionBody; continue; }
            // A layout-neutral anchor: no size, no padding, no flex of its own.
            sections[i] = new BoxEl
            {
                Key = s_anchorKeys[(int)slot.Section], Direction = 1, MinWidth = 0f,
                OnRealized = slot.Anchor,
                Children = [sectionBody],
            };
        }

        float collapse = ArtistHeroLayout.CollapseDistance(m.MinHeight);
        bool compact = p.Compact;
        Element band = BandBar(p, collapse);
        Element hero = ClassicArtistHero.Build(Artist.HeroText.For(a), uri, p.HeroUrl, p.PaletteUrl, width, in m, accent,
            compact, p.Play, p.Shuffle, p.Radio, band, headerAccent: a.HeaderAccent);

        // One edge-only hand-off: the sentinel's sticky(56) ENGAGED edge is the band's input switch (`p.CompactEdge`, written by the engine). The
        // magazine's feather is NOT switched by it (RCA 2026-09-25 F(ii): a re-render off the engaged edge landed two
        // presents after the render-posed clip — `[scroll.engaged.present] ticksAfterCross=2`); it is the clip's own
        // composite-time parameter below (EdgeFadeSpec.WhileStuck).
        Element sentinel = new BoxEl { Height = 0f, HitTestVisible = false }
            .Sticky(ArtistHeroLayout.CompactIdentityHeight, engaged: p.CompactEdge());

        // The blend wash, from the RESOLVED metrics (ch 08 §9 inconsistency #1); a cover-keyed leaf, so a grading
        // re-renders only the wash. Colour washes off ⇒ it renders nothing (the veil and the Play colour stay, §4).
        Element wash = Palette.ArtistBlendWash(p.PaletteUrl, ArtistHeroLayout.BlendBackdropHeightFor(in m),
            ArtistHeroLayout.BlendBoundaryFor(in m), disabled: !p.Washes, key: "artist-wash:" + uri,
            payloadAccent: a.HeaderAccent);

        // THE CLIP IS THE CONTRACT: the band paints nothing, so nothing may render into its 56 DIP — the magazine and
        // the wash are both cut at the line, and the magazine's cut is feathered exactly while its clip is engaged, on
        // the same render turn that poses the cut (EdgeFadeSpec.WhileStuck; nothing is softened at rest). No gate
        // of its own (ch 08 BUG F, restored to 0.2.10's model): the page-level `region` above already keeps the
        // whole body — this magazine included — unmounted until the overview lands, so a second, nested
        // SkelRegionEl here pending on the SAME predicate would never once see Pending() true and would only add a
        // dead group member.
        Element magazine = new BoxEl
        {
            Key = "artist-under-band", Direction = 1,
            EdgeFade = new EdgeFadeSpec(EdgeMask.Top, Detail.BandLayout.ClipFadeBand) { WhileStuck = true },
            Children =
            [
                new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, HitTestVisible = false },
                new BoxEl { Direction = 0, Justify = FlexJustify.Center, Children = [Magazine(sections, m.Gutter)] },
            ],
        }.StickyClip(Detail.BandLayout.ClipInset);

        return new BoxEl
        {
            ZStack = true,
            Children =
            [
                new BoxEl { Key = "artist-wash-clip", Direction = 1, HitTestVisible = false, Children = [wash] }
                    .StickyClip(Detail.BandLayout.ClipInset),
                new BoxEl { Direction = 1, Children = [hero, sentinel, magazine] },
            ],
        };
    }

    /// <summary>The centred magazine column: Grow toward the row's free width, capped at 1600, the hero's gutter on
    /// both sides, a 32 section gap, the player dock's reserve under the last section (ArtistPage.cs:271-285).</summary>
    static Element Magazine(Element[] sections, float gutter) => new BoxEl
    {
        Direction = 1, Gap = R.Space.SectionGap,
        Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f, MaxWidth = R.Space.PageMaxW,
        Padding = new Edges4(gutter, Spacing.M, gutter, R.Space.DockReserve + ClassicArtistRules.MagazineDockExtra),
        Children = sections,
    };

    // ── the context band's artist arm (ArtistCompactBar.cs) ──────────────────────────────────────────────────────────

    /// <summary>title · pivot · Play + Follow as WORDS, one hairline, no fill, no avatar, no capsules (ch 08 §0 #4).
    /// Everything geometric is the shared band's; the gutter is the hero metrics' and the content is this page's.
    /// Reveals over the last 44 DIP of the collapse with a 4-DIP settle; takes input only past the sentinel's edge.</summary>
    static Element BandBar(ArtistPageProps p, float collapse)
    {
        Artist a = p.Artist;
        float width = p.Width;
        float gutter = p.Metrics.Gutter;
        string uri = a.Uri.Text;
        string name = a.Name;
        float actionH = Detail.BandLayout.Height - 2f * Spacing.M;
        var items = new (string Label, Action OnClick)[p.Pivot.Count];
        for (int i = 0; i < items.Length; i++) items[i] = (p.Pivot[i].Label, p.Pivot[i].OnClick);

        // No "Overview" pivot tab: the title itself is the way back to the top of the page.
        Element title = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Shrink = 1f, MaxWidth = Detail.BandLayout.TitleCap,
            Cursor = CursorId.Hand, OnClick = p.ScrollToTop,
            Children = [Detail.BandTitle(name)],
        };
        // The pivot is the ONLY elastic lane; the title and the actions never drop (W28).
        Element pivot = new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = Detail.BandLayout.Height, AlignItems = FlexAlign.Center,
            Children = [Detail.Pivot(items, p.Active, p.AccentFn)],
        };
        Element actions = new BoxEl
        {
            Direction = 0, Gap = Detail.BandLayout.ActionGap, Shrink = 0f, AlignItems = FlexAlign.Center,
            Children =
            [
                Controls.TextAction(Loc.Get(Strings.Artist.Play), p.Play, primary: true,
                    height: actionH, padX: Detail.BandLayout.ActionPadX),
                Embed.Comp(() => new Controls.FollowTextAction { Uri = uri, Name = name, Height = actionH, PadX = Detail.BandLayout.ActionPadX })
                    with { Key = "artist-band-follow:" + uri, SkeletonProxy = s_emptyShape },
            ],
        };
        // The content sits at the page's 1600 measure; the band's extent stays full-bleed.
        Element row = Detail.Band(MathF.Min(width, R.Space.PageMaxW), gutter, [title, pivot, actions]);
        return new BoxEl
        {
            Width = width, Height = Detail.BandLayout.Height, ZStack = true,
            HitTestVisible = p.Compact, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Width = width, Height = Detail.BandLayout.Height, Justify = FlexJustify.Center,
                    Children = [row],
                },
                // The ONE hairline, overlaid INSIDE the 56 so the collapse arithmetic stays exact.
                new BoxEl
                {
                    Width = width, Height = Detail.BandLayout.Height, Direction = 1, Justify = FlexJustify.End,
                    HitTestVisible = false, Children = [Detail.BandHairline()],
                },
            ],
        }.Reveal(ArtistHeroLayout.CompactRevealStart(collapse), collapse - ArtistHeroLayout.CompactRevealStart(collapse),
            Design.Reduced ? 0f : Spacing.XS).Skeletonized(false);
    }

    // ── the skeleton ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The derived shimmer's SOURCE (W5): the same hero composition over a representative shape at the
    /// tier's real heights (no action row, no band — both are never skeleton content), the divider, a title-width
    /// header bar over the chart's two 5-row columns at the live pitch, a facet header bar and a row of square grid
    /// placeholders. Every geometry is the loaded page's, so nothing shifts when the content lands (parity 74).</summary>
    static Element Shimmer(ArtistPageProps p, ArtistChartStyle? chart)
    {
        var m = p.Metrics;
        Element hero = ClassicArtistHero.Build(Artist.HeroText.Placeholder, "", null, null, p.Width, in m, p.AccentPeek(), false,
            null, null, null, null);
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                hero,
                new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
                new BoxEl
                {
                    Direction = 0, Justify = FlexJustify.Center,
                    Children = [MagazineShimmer(p, chart)],
                },
            ],
        };
    }

    /// <summary>The magazine's own derived-shimmer shape (chart skeleton + one facet skeleton) — <see cref="Shimmer"/>'s
    /// bottom half, factored out so the doc above stays readable. There is no second consumer any more (ch 08 BUG F,
    /// restored to 0.2.10's model): the whole page — hero included — is one derived shimmer while the overview is
    /// unknown, never a hero-then-magazine two-step. The facet's cells are the grid's own card — the shared surface's
    /// SEED face at <c>Shape.Grid</c> (the discography grid's gap), so a cell is the height of the real card it stands in
    /// for (<c>cell width + 50</c>) and nothing moves when the grid lands. The face IS already the bone description, so
    /// each cell hands it to the deriver as-is (<c>Skel</c>): derived, its aspect-only cover declares no extent and
    /// would collapse to nothing.</summary>
    static Element MagazineShimmer(ArtistPageProps p, ArtistChartStyle? chart)
    {
        var cells = new Element[ClassicArtistRules.ShimmerCells];
        for (int i = 0; i < cells.Length; i++)
        {
            Element cell = new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children = [SurfaceParts.Seed(Shape.Grid, float.NaN)],
            };
            cells[i] = cell.Skel(cell);
        }
        Element facet = new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Children = [Controls.AccentHeader(Loc.Get(Strings.Artist.Albums), p.AccentPeek())],
                },
                new BoxEl { Direction = 0, Gap = Artist.DiscoGap, Children = cells },
            ],
        };
        return Magazine([Artist.ChartSkeleton(chart, 0, p.RowsClassic, p.ShowTrackArt), facet], p.Metrics.Gutter);
    }
}
