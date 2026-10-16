// ── Themes/Classic/Structure/Views/Pages/ClassicProfilePage.cs — Classic's `Pages.User` (themes plan 6.1, T10c2) ───────────────────
//
// From `Entities\Profile.Page.cs` (`PageHost.Render`, `Compose`, `Magazine`, `BandBar`, `PageShimmer`, `Latch`): the render half of the profile page, a sibling of
// `ClassicArtistPage` (the profile page was copied from it).
//
//   Pages.User
//   └─ Ctx.Provide(Design.AccentCtx = the page accent) ▸ Ctx.Provide(LazyScroll = the scroll offset)
//      └─ Column  Key=profile-page:<route>  OnBoundsChanged = the host's measure
//         ├─ the shell-tint claim (a zero-size leaf the host built)
//         └─ ScrollView  Key=profile-scroll:<route>  (EdgeCues None: the band itself is the occlusion cue)
//            └─ keyed child profile-body:<scope epoch>
//               └─ SkelRegionEl (Reveal.Soft, Group null)
//                  ├─ Content  the "isn't available" vacancy, or
//                  │           ZStack [ the blend wash (NOT cut at the band: a profile hero has no photo),
//                  │                    Column [ hero (pinned, collapsing into the band) · sentinel · magazine clipped at the band ] ]
//                  │           the magazine's sections are each their OWN region (a late list shimmers in place, never holding the hero)
//                  └─ Shimmer  the SAME hero over a placeholder, the divider, two seed shelves at the magazine's real width
//
// What the view does NOT own: the demands, the load faces and the reveal latch, the section plan and each section's body verdict and cards, the hero metrics'
// hysteresis, the accent, the scroll spy and its anchors (the host hands each section's `OnRealized`), the tint claim and every verb.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>The page view's latched state: plain fields that outlive a render (the view's hook cell), exactly the host fields they replace (`_secBody`, `_secEl`, `_body`
/// and the region delegates over them). The delegates are built ONCE: a fresh delegate would re-arm a skeleton region.</summary>
internal sealed class ClassicProfileLatch
{
    /// <summary>The props of the latest render (the derived skeleton and the failed panels read the live width, accent and own flag through it).</summary>
    public ProfilePageProps P = null!;

    /// <summary>The window tier's page record and the hero's form for the latest render's metrics (TC6, `Rules\Tiers.Profile.cs`).</summary>
    public ClassicProfileLayout Layout = ClassicProfileTiers.Standard;
    public ClassicProfileHeroForm Form = ClassicProfileTiers.StandardForm(ArtistHeroTier.Wide);

    /// <summary>The page body of this render (null while it is not composed).</summary>
    public Element? Body;

    /// <summary>Each section's body verdict and element, latched per compose; the regions' delegates read these.</summary>
    public readonly ProfileSectionBody[] Verdict = new ProfileSectionBody[ProfileSections.Count];
    public readonly Element?[] Els = new Element?[ProfileSections.Count];
    public readonly Action[] Retry = new Action[ProfileSections.Count];

    public readonly Func<Element> Content, Shimmer, FailedPanel;
    public readonly Func<bool>[] SecPending = new Func<bool>[ProfileSections.Count], SecFailed = new Func<bool>[ProfileSections.Count];
    public readonly Func<Element>[] SecContent = new Func<Element>[ProfileSections.Count], SecShimmer = new Func<Element>[ProfileSections.Count],
        SecFailedPanel = new Func<Element>[ProfileSections.Count];

    public ClassicProfileLatch()
    {
        Content = () => Body ?? new BoxEl();
        Shimmer = () => ClassicProfilePage.Shimmer(this);
        FailedPanel = () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: P.Retry);
        for (int i = 0; i < ProfileSections.Count; i++)
        {
            int k = i;
            var s = (ProfileSection)i;
            SecPending[k] = () => Verdict[k] == ProfileSectionBody.Seed;
            SecFailed[k] = () => Verdict[k] == ProfileSectionBody.Error;
            SecContent[k] = () => Els[k] ?? new BoxEl();
            SecShimmer[k] = () => ClassicProfileSections.SeedShelf(s, P.AccentPeek(), ClassicProfilePage.MagazineInnerWidth(P, Layout.PageMaxWidth), Layout.Shelf);
            SecFailedPanel[k] = () => ClassicProfileSections.SectionError(s, P.AccentPeek(), Retry[k]);
        }
    }
}

/// <summary>Wavee Classic's profile page.</summary>
internal static class ClassicProfilePage
{
    static readonly string[] s_secKeys = SectionKeys("sec:");
    static readonly string[] s_anchorKeys = SectionKeys("anchor:");
    static readonly Func<Element> s_emptyShape = static () => new BoxEl();

    static string[] SectionKeys(string prefix)
    {
        var keys = new string[ProfileSections.Count];
        for (int i = 0; i < keys.Length; i++) keys[i] = prefix + ProfileSections.Key((ProfileSection)i);
        return keys;
    }

    /// <summary>The registered template of <see cref="ViewKeys.ProfilePage"/>.</summary>
    public static Element Template(ProfilePageProps p, in ViewContext ctx)
    {
        // The tier records (TC6): the page's (the shelves' fit) and the hero's form for the metrics the core latched (they already carry the one page gutter,
        // tuned Classic F3, as the artist page's).
        var facts = ctx.Tier();
        var latchCell = ctx.Hooks.UseRef<ClassicProfileLatch?>(null);
        var latch = latchCell.Value ??= new ClassicProfileLatch();
        latch.P = p;
        latch.Layout = ClassicProfileTiers.Of(in facts);
        latch.Form = ClassicProfileTiers.HeroForm(p.Metrics.Tier, in facts, p.Width);

        latch.Body = p.Unavailable ? ClassicProfileSections.Unavailable()
                   : p.BodyReady ? Compose(p, latch)
                   : null;

        // ONE page region; the shimmer is DERIVED from Shimmer (never mounted). Soft: the complete tree rises as one.
        Element region = new SkelRegionEl(
            Pending: p.Pending, Failed: p.Failed, Content: latch.Content, ShimmerSource: latch.Shimmer,
            OnFailed: latch.FailedPanel, Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default,
            Group: null, SmoothResize: false);

        Element scroll = ScrollView(new BoxEl
        {
            Direction = 1,
            // Keyed CHILD (rule 14): a scope switch remounts the sections, whose anchors re-register after the reset.
            Children = [new BoxEl { Key = "profile-body:" + p.ScopeKey, Direction = 1, Children = [region] }],
        }) with
        {
            Key = "profile-scroll:" + p.RouteKey, Grow = 1f, ScrollKey = p.ScrollKey,
            OnRealized = p.CaptureViewport, EdgeCues = ScrollEdgeCues.None, Handle = p.Scroll,
        };

        // The page PROVIDES its accent (Design.AccentCtx): the heart and every other ambient consumer read it.
        return Ctx.Provide(Design.AccentCtx.Slot, p.PageAccent,
            Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>?)p.ScrollY, new BoxEl
            {
                Key = "profile-page:" + p.RouteKey, Grow = 1f, Direction = 1, OnBoundsChanged = p.Measure,
                Children = [p.Tint, scroll],
            }));
    }

    // ── the loaded page ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>[wash] under [hero · sentinel · magazine clipped at the band] — `ClassicArtistPage`'s Compose, verbatim in shape. Plan, anchors and pivot were built
    /// by the host in ONE pass so they cannot disagree.</summary>
    static Element Compose(ProfilePageProps p, ClassicProfileLatch latch)
    {
        var m = p.Metrics;
        float width = p.Width;
        ColorF accent = p.Accent;
        string uri = p.Uri;

        var plan = p.Sections;
        int n = plan.Count;
        var sections = new Element[n];
        var pivotItems = new (string Label, Action OnClick)[n];
        for (int i = 0; i < n; i++)
        {
            var slot = plan[i];
            var s = slot.Section;
            int k = (int)s;
            latch.Verdict[k] = slot.Body;
            latch.Retry[k] = slot.Retry;
            latch.Els[k] = slot.Body switch
            {
                ProfileSectionBody.Cards => ClassicProfileSections.Shelf(s, slot.Cards, accent, slot.SeeAll, latch.Layout.Shelf),
                ProfileSectionBody.Empty => ClassicArtistSections.SectionBlock(ClassicProfileSections.SectionTitle(s),
                    ClassicProfileSections.EmptyShelf(p.Own), accent),
                _ => null,
            };
            // Each section is ITS OWN region: a late list shimmers in place, never holding the hero (Group null).
            Element region = new SkelRegionEl(
                Pending: latch.SecPending[k], Failed: latch.SecFailed[k], Content: latch.SecContent[k], ShimmerSource: latch.SecShimmer[k],
                OnFailed: latch.SecFailedPanel[k], Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default,
                Group: null, SmoothResize: false);
            // EVERY section keyed: sections appear mid-stream as data lands.
            sections[i] = new BoxEl
            {
                Key = s_anchorKeys[k], Direction = 1, MinWidth = 0f, OnRealized = slot.Anchor,
                Children = [new BoxEl { Key = s_secKeys[k], Direction = 1, MinWidth = 0f, Children = [region] }],
            };
            pivotItems[i] = (ClassicProfileSections.PivotLabel(s), slot.Click);
        }

        float collapse = ProfileHeroLayout.CollapseDistance(in m);
        bool compact = p.Compact;
        Element band = BandBar(p, collapse, compact, pivotItems, latch.Layout.PageMaxWidth);
        Element hero = ClassicProfileHero.Build(p.Hero, uri, width, in m, latch.Form, latch.Layout.PageMaxWidth, compact, p.HeroActs, band);
        Element sentinel = new BoxEl { Height = 0f, HitTestVisible = false }
            .Sticky(ArtistHeroLayout.CompactIdentityHeight, engaged: p.CompactEdge());
        Element wash = Palette.ArtistBlendWash(p.PaletteUrl, ProfileHeroLayout.WashHeight(in m),
            ProfileHeroLayout.WashBoundary(in m), key: "profile-wash:" + uri,
            payloadAccent: p.PayloadAccent);
        // THE MAGAZINE'S CLIP IS THE CONTRACT: the band paints nothing, so no CONTENT may render into its 56 DIP — the
        // magazine is cut at the line, its cut feathered exactly while the clip is engaged. The WASH is deliberately
        // NOT cut (unlike the artist page, whose full-bleed photo covers the band's 56 at rest): a profile hero has no
        // photo, so a cut wash left a bare strip of shell colour above the name. Uncut, the colour fills the hero to
        // its top and simply scrolls away under the transparent band.
        Element magazine = new BoxEl
        {
            Key = "profile-under-band", Direction = 1, Animate = ClassicArtistTiers.FollowMotion,
            EdgeFade = new EdgeFadeSpec(EdgeMask.Top, Detail.BandLayout.ClipFadeBand) { WhileStuck = true },
            Children =
            [
                new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, HitTestVisible = false },
                new BoxEl { Direction = 0, Justify = FlexJustify.Center, Children = [Magazine(sections, m.Gutter, latch.Layout.PageMaxWidth)] },
            ],
        }.StickyClip(Detail.BandLayout.ClipInset);

        return new BoxEl
        {
            ZStack = true,
            Children =
            [
                new BoxEl { Key = "profile-wash", Direction = 1, HitTestVisible = false, Children = [wash] },
                new BoxEl { Direction = 1, Children = [hero, sentinel, magazine] },
            ],
        };
    }

    /// <summary>The centred magazine column: Grow toward the row's free width, capped at the tier's measure (1600; Huge's 2400), the hero's gutter on both sides,
    /// a 32 section gap, the player dock's reserve under the last section.</summary>
    static Element Magazine(Element[] sections, float gutter, float pageMax) => new BoxEl
    {
        Direction = 1, Gap = R.Space.SectionGap,
        Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f, MaxWidth = pageMax,
        Padding = new Edges4(gutter, Spacing.M, gutter, R.Space.DockReserve + ClassicProfileRules.MagazineDockExtra),
        Children = sections,
    };

    /// <summary>The magazine's measure: the page's width capped at the tier's column, minus the hero's gutter on both sides.</summary>
    internal static float MagazineInnerWidth(ProfilePageProps p, float pageMax) => MathF.Max(1f, MathF.Min(p.Width, pageMax) - 2f * p.Metrics.Gutter);

    // ── the context band (`ClassicArtistPage.BandBar`, the profile arm: title · pivot · Follow as a word) ───────────────────

    static Element BandBar(ProfilePageProps p, float collapse, bool canHit, (string Label, Action OnClick)[] pivotItems, float pageMax)
    {
        float width = p.Width;
        float gutter = p.Metrics.Gutter;
        string uri = p.Uri;
        string name = p.Name;
        float actionH = Detail.BandLayout.Height - 2f * Spacing.M;
        // No "Overview" pivot tab: the title itself is the way back to the top of the page.
        Element title = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Shrink = 1f, MaxWidth = Detail.BandLayout.TitleCap,
            Cursor = CursorId.Hand, OnClick = p.ScrollToTop,
            Children = [ClassicDetailBand.BandTitle(name)],
        };
        // The pivot is the ONLY elastic lane; the title and the actions never drop.
        Element pivot = new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = Detail.BandLayout.Height, AlignItems = FlexAlign.Center,
            Children = [ClassicDetailBand.Pivot(pivotItems, p.Active, p.AccentFn)],
        };
        Element actions = p.Own
            ? new BoxEl { Width = 0f, Height = 0f }
            : new BoxEl
            {
                Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
                Children =
                [
                    Embed.Comp(() => new Controls.FollowTextAction
                        { Uri = uri, Name = name, Height = actionH, PadX = Detail.BandLayout.ActionPadX })
                        with { Key = "profile-band-follow:" + uri, SkeletonProxy = s_emptyShape },
                ],
            };
        // The content sits at the page's measure (the magazine's); the band's extent stays full-bleed.
        Element row = ClassicDetailBand.Band(MathF.Min(width, pageMax), gutter, [title, pivot, actions]);
        float revealStart = ArtistHeroLayout.CompactRevealStart(collapse);
        return new BoxEl
        {
            Width = width, Height = Detail.BandLayout.Height, ZStack = true,
            HitTestVisible = canHit, HitTestPassThrough = true,
            Children =
            [
                new BoxEl { Direction = 0, Width = width, Height = Detail.BandLayout.Height, Justify = FlexJustify.Center, Children = [row] },
                // The ONE hairline, overlaid INSIDE the 56 so the collapse arithmetic stays exact.
                new BoxEl
                {
                    Width = width, Height = Detail.BandLayout.Height, Direction = 1, Justify = FlexJustify.End,
                    HitTestVisible = false, Children = [ClassicDetailBand.BandHairline()],
                },
            ],
        }.Reveal(revealStart, collapse - revealStart, Design.Reduced ? 0f : Spacing.XS).Skeletonized(false);
    }

    // ── the skeleton ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A ShimmerSource, never mounted: the SAME hero composition over a placeholder (no band, the action row a Skeletonized(false) spacer), the divider, and two
    /// seed shelves at the magazine's real width — own: Top artists + Public playlists; other: Public playlists + Recently played artists.</summary>
    internal static Element Shimmer(ClassicProfileLatch latch)
    {
        var p = latch.P;
        var m = p.Metrics;
        float inner = MagazineInnerWidth(p, latch.Layout.PageMaxWidth);
        var first = p.Own ? ProfileSection.TopArtists : ProfileSection.Playlists;
        var second = p.Own ? ProfileSection.Playlists : ProfileSection.RecentArtists;
        ColorF accent = p.AccentPeek();
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                ClassicProfileHero.Build(ProfileHeroText.PlaceholderFor(p.Own), "", p.Width, in m, latch.Form, latch.Layout.PageMaxWidth, false, null, null),
                new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
                new BoxEl
                {
                    Direction = 0, Justify = FlexJustify.Center,
                    Children =
                    [
                        Magazine([ClassicProfileSections.SeedShelf(first, accent, inner, latch.Layout.Shelf),
                                  ClassicProfileSections.SeedShelf(second, accent, inner, latch.Layout.Shelf)], m.Gutter, latch.Layout.PageMaxWidth),
                    ],
                },
            ],
        };
    }
}

/// <summary>Registers the profile page's view.</summary>
internal static class ClassicProfile
{
    /// <summary>Registers the profile page.</summary>
    internal static void Register(ViewRegistryBuilder views)
        => views.Add(ViewKeys.ProfilePage, ClassicProfilePage.Template, "classic.profile.page");
}
