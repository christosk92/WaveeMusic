// ── Entities/Profile.Page.cs ───────────────────────────────────────────────────────────────────────────────────────────
// The PROFILE page (spotify:user:…): install + mount point and the page component — demand on mount and on every
// revisit (through the data layer's ProfileAsk plan), the render-cost stamp (marks + Fetch.Settled folded too: a 404 seal /
// failure moves no Version), the four load faces (ProfileLoadRule.Header), the derived shimmer, the person's tone (accent,
// blend wash, shell tint), the context band (Detail.Band + Detail.Pivot + the scroll spy) and one SkelRegion per section.
// The hero, the section bodies and the cards are Profile.UI.cs (U2); the pure decisions are Profile.Rules.cs.
//
// Role: UI   Owner: U1   Template: Entities/Artist.Page.cs (the band / spy / sentinel / clip are copied from it)
// Spec: docs/plans/wavee/profile-pages-implementation.md Appendix U §3 + "Reconciliation, round 2"
//
// THE FOUR FACES (ProfileLoadRule.Header): Loading → the derived shimmer; Ready → the page; Unavailable → the page's
// "isn't available" vacancy as CONTENT (no Retry, the 404 seal); Failed → the error vacancy with Retry = RetryHeader.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Profile
{
    /// <summary>The profile family's install: the profile page AND the "See all" list page. Called once from Shell.cs's
    /// lazy page group (the <c>{User, ProfileList}</c> pair).</summary>
    public static void InstallPages()
    {
        Shell.SetPage(Shell.RouteKind.User, Page);
        Shell.SetPage(Shell.RouteKind.ProfileList, ProfileLists.Page);
    }

    /// <summary>Keyed by the route: user → user is a new slot and a fresh page.</summary>
    // MOUNT POINT (stage B contract)
    public static Element Page(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        return Embed.Comp(new PageProps(route.Subject, routeKey), static () => new PageHost()) with { Key = "user:" + routeKey };
    }

    sealed record PageProps(EntityUri Subject, string RouteKey);

    sealed partial class PageHost : Component, IPropsHost
    {
        static readonly string[] s_secKeys = Keys("sec:"), s_anchorKeys = Keys("anchor:");
        static readonly Func<Element> s_emptyShape = static () => new BoxEl();

        static string[] Keys(string prefix)
        {
            var k = new string[ProfileSections.Count];
            for (int i = 0; i < k.Length; i++) k[i] = prefix + ProfileSections.Key((ProfileSection)i);
            return k;
        }

        // ── identity ──
        PageProps? _latest;
        readonly Signal<PageProps?> _props = new(null);
        bool _accentSeeded;
        Scope? _scope;
        EntityUri _subject;
        User _user;
        bool _own;
        string _name = "";
        /// <summary>This page's identity as the shell material's owner; survives keep-alive park/reactivate.</summary>
        readonly object _tintOwner = new();

        // ── geometry (latched fields — Artist.Page's discipline) ──
        readonly Signal<float> _heroWidth = new(ArtistHeroLayout.WideWidth);
        readonly Signal<int> _layoutEpoch = new(0);
        ArtistHeroTier _tier = ArtistHeroTier.Wide;
        ProfileHeroMetrics _metrics = ProfileHeroLayout.For(ArtistHeroLayout.WideWidth, ArtistHeroTier.Wide);
        float _width = ArtistHeroLayout.WideWidth;
        bool _measured;

        // ── scroll, band, spy ──
        readonly Signal<float> _scrollY = new(0f), _viewportH = new(0f);
        readonly Signal<bool> _atEnd = new(false);
        // The sentinel's sticky ENGAGED edge: the band's input switch (written by the engine on the flip).
        readonly Signal<bool> _compact = new(false) { DebugName = "profile.compact" };
        readonly ScrollHandle _scroll = new();
        long _scrollKey = long.MinValue;
        readonly Signal<int> _active = new(Detail.BandLayout.NoSection);
        readonly Signal<int> _pivotEpoch = new(0);
        NodeHandle _viewport;
        readonly NodeHandle[] _anchors = new NodeHandle[ProfileSections.Count];
        readonly Action<NodeHandle>[] _anchorRealized = new Action<NodeHandle>[ProfileSections.Count];
        readonly Action[] _sectionClicks = new Action[ProfileSections.Count];
        readonly ProfileSection[] _plan = new ProfileSection[ProfileSections.Count];
        int _pivotCount, _pivotHash;

        // ── colour ──
        // Seeded from the last remembered page accent, so the first frame never paints the default over a held colour.
        readonly Signal<ColorF> _accent = new(AccentHold.Last ?? Tok.AccentDefault);
        readonly Signal<Design.PageAccent> _pageAccent = new(new Design.PageAccent(
            AccentHold.Last ?? Tok.AccentTextPrimary, AccentHold.Last ?? Tok.AccentDefault, ""));
        readonly Signal<ThemeKind> _theme = new(ThemeKind.Dark);
        readonly Func<ColorF> _accentFn;

        // ── readiness ──
        ProfileLoad _header = ProfileLoad.Loading;
        bool _bodyReady, _revealed;
        Element? _body;

        // ── sections (latched per compose; the region delegates read these) ──
        readonly ProfileSectionBody[] _secBody = new ProfileSectionBody[ProfileSections.Count];
        readonly Element?[] _secEl = new Element?[ProfileSections.Count];
        readonly Func<bool>[] _secPending, _secFailed;
        readonly Func<Element>[] _secContent, _secShimmer, _secFailedPanel;
        readonly Action[] _secRetry;
        readonly Action?[] _seeAll;

        // ── cached delegates (a node handler never captures a render's closure) ──
        readonly Func<bool> _pendingFn, _failedFn;
        readonly Func<Element> _contentFn, _shimmerFn, _failedPanelFn;
        readonly Action _demand, _demandTop, _publishAccent, _publishTheme, _resolveSpy, _bumpPivot, _retry;
        readonly Action _scrollToTop, _watchScroll;
        readonly Action<NodeHandle> _captureViewport;
        readonly Action<RectF> _measure;
        readonly Func<long> _stampFn;
        readonly HeroActs _acts;

        public PageHost()
        {
            // The Follow primary / the band's Follow word write through this seam (Show / Playlist page precedent).
            Controls.Library ??= User.LibrarySeam;

            int n = ProfileSections.Count;
            _secPending = new Func<bool>[n]; _secFailed = new Func<bool>[n];
            _secContent = new Func<Element>[n]; _secShimmer = new Func<Element>[n]; _secFailedPanel = new Func<Element>[n];
            _secRetry = new Action[n]; _seeAll = new Action?[n];
            for (int i = 0; i < n; i++)
            {
                int k = i;
                var s = (ProfileSection)i;
                _anchorRealized[k] = h => _anchors[k] = h;
                _sectionClicks[k] = () => GoToSection(k);
                _secPending[k] = () => _secBody[k] == ProfileSectionBody.Seed;
                _secFailed[k] = () => _secBody[k] == ProfileSectionBody.Error;
                _secContent[k] = () => _secEl[k] ?? new BoxEl();
                _secShimmer[k] = () => SeedShelf(s, _accent.Peek(), MagazineInnerWidth());
                _secRetry[k] = () => RetrySection(s);
                _secFailedPanel[k] = () => SectionError(s, _accent.Peek(), _secRetry[k]);
                if (s == ProfileSection.Following) _seeAll[k] = () => OpenList(ProfileFacet.Following);
                else if (s == ProfileSection.Followers) _seeAll[k] = () => OpenList(ProfileFacet.Followers);
            }
            _accentFn = () => _accent.Value;
            _pendingFn = () => !_bodyReady && _header is not (ProfileLoad.Failed or ProfileLoad.Unavailable);
            _failedFn = () => !_bodyReady && _header == ProfileLoad.Failed;
            _contentFn = () => _body ?? new BoxEl();
            _shimmerFn = PageShimmer;
            _retry = Retry;
            // Retry = RetryHeader (Refresh, un-asks first); never Ensure — a sealed Asked row is a no-op for Ensure.
            _failedPanelFn = () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: _user.IsValid ? _retry : null);
            _demand = Demand;
            _demandTop = DemandTop;
            _publishAccent = PublishAccent;
            _publishTheme = () => _theme.Value = Tok.Theme;
            _resolveSpy = ResolveSpy;
            _bumpPivot = () => _pivotEpoch.Value = _pivotEpoch.Peek() + 1;
            _scrollToTop = () => _scroll.ScrollTo(0.0, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide);
            _captureViewport = h => _viewport = h;
            _measure = r =>
            {
                // The 0.5-DIP write floor: a smaller one writes every frame of a resize.
                if (r.W <= 0f) return;
                if (!_measured) { _measured = true; _heroWidth.Value = r.W; _layoutEpoch.Value++; return; }
                if (MathF.Abs(r.W - _heroWidth.Peek()) > 0.5f) _heroWidth.Value = r.W;
            };
            // A COARSE key off the page's scroll handle: the 24-DIP write floor, the viewport height in 4-DIP steps and
            // the at-end edge. The watch re-runs on every moved frame and writes only when the key changes.
            _watchScroll = () =>
            {
                float y = (float)_scroll.Offset.Value, vh = (float)_scroll.ViewportSignal.Value, ch = (float)_scroll.ExtentSignal.Value;
                bool atEnd = Detail.BandLayout.IsAtScrollEnd(y, vh, ch);
                long key = HashCode.Combine((int)(y / 24f), (int)MathF.Round(vh / Spacing.XS), atEnd);
                if (key == _scrollKey) return;
                _scrollKey = key;
                _scrollY.Value = y;
                _viewportH.SetIfChanged(vh);
                _atEnd.SetIfChanged(atEnd);
            };
            _stampFn = Stamp;
            _acts = new HeroActs(
                Share: () => Episode.CopyLink(_user.IsValid ? Actions.WebLinkOf(_user.Uri) : ""),
                Menu: () => MenuFor(_user, _name),
                OpenStat: k =>
                {
                    if (k == ProfileStat.Followers) OpenList(ProfileFacet.Followers);
                    else if (k == ProfileStat.Following) OpenList(ProfileFacet.Following);
                });
        }

        public void ApplyProps(object props)
        {
            _latest = (PageProps)props;
            if (!_accentSeeded)
            {
                // The first paint carries a cached grading's accent instead of one frame of the default.
                _accentSeeded = true;
                _theme.Value = Tok.Theme;
                if (_latest.Subject.IsValid) PublishAccent(Entities.User(_latest.Subject), _latest.RouteKey);
            }
            _props.Value = _latest;
        }

        public override Element Render()
        {
            var p = _props.Value ?? _latest!;
            uint scopeEpoch = Entities.ScopeEpoch.Value;              // FIRST: a scope switch re-points every table
            var scope = Entities.Current;

            if (!ReferenceEquals(_scope, scope) || !_subject.Equals(p.Subject))
            {
                _scope = scope;
                _subject = p.Subject;
                _user = p.Subject.IsValid ? Entities.User(p.Subject) : default;   // allocates the row: bind now, the demand fills
                Array.Clear(_anchors);                                            // the body is epoch-keyed: re-register
                Array.Clear(_secEl);
                _revealed = false;
            }

            var shellSlot = UseContext(ShellMaterial.Slot);
            var u = _user;
            bool valid = u.IsValid;
            _own = valid && (u.Slot == scope.MeSlot || u.IsCurrentUser);
            _name = valid ? u.Name : "";
            _ = UseComputed(_stampFn).Value;                       // THE render-cost fold (Stamp)
            UseEffect(_demand, DepKey.From(u.Slot, (int)scopeEpoch));
            // Own top artists ride Home.Feeds; a vanity uri learns it is yours only once Social lands, so this re-keys on it.
            UseEffect(_demandTop, DepKey.From(u.Slot, (int)scopeEpoch, _own ? 1 : 0, 0));
            UseEffect(_publishAccent);
            UseEffect(_publishTheme, DepKey.From((int)Tok.Theme));
            UseEffect(_resolveSpy);
            UseActivation(onActivated: _demand);                   // keep-alive return re-reads (SWR: ProfileAsk → Invalidate)

            bool washes = Prefs.Appearance.SurfaceWash() != WashLevel.Off;

            _ = _layoutEpoch.Value;
            _width = MathF.Max(1f, _heroWidth.Value);
            _metrics = ProfileHeroLayout.For(_width, _tier);
            _tier = _metrics.Tier;

            _header = valid ? ProfileLoadRule.Header(u) : ProfileLoad.Unavailable;
            _bodyReady = ProfileReveal.BodyReady(_header, _measured, _revealed);
            if (_bodyReady) _revealed = true;

            string routeKey = p.RouteKey;
            string? avatar = valid ? u.Image : null;
            var tone = ProfileTone.Of(avatar, valid ? u.Color : 0u, avatar is { Length: > 0 } a && Palette.CanGrade(a));
            // A 0×0 leaf owns the watch + the publish. Unavailable/Failed opt out (definite → neutral chrome).
            Element tint = Palette.ShellTint(tone.PaletteUrl, ready: Detail.CoverLatch.IsUsable(tone.PaletteUrl), disabled: !washes,
                apply: _header is not (ProfileLoad.Unavailable or ProfileLoad.Failed),
                owner: _tintOwner, slot: shellSlot, key: "profile-tint:" + routeKey, payloadAccent: tone.PayloadArgb);

            _body = _header == ProfileLoad.Unavailable ? Unavailable()
                  : _bodyReady ? Compose(u, avatar, tone, washes)
                  : null;
            UseEffect(_bumpPivot, DepKey.From(_pivotHash, (int)scopeEpoch));
            UseSignalEffect(_watchScroll);

            // ONE page region; the shimmer is DERIVED from PageShimmer (never mounted). Soft: the complete tree rises as one.
            Element region = new SkelRegionEl(
                Pending: _pendingFn, Failed: _failedFn, Content: _contentFn, ShimmerSource: _shimmerFn,
                OnFailed: _failedPanelFn, Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default,
                Group: null, SmoothResize: false);

            Element scroll = ScrollView(new BoxEl
            {
                Direction = 1,
                // Keyed CHILD (rule 14): a scope switch remounts the sections, whose anchors re-register after the reset.
                Children = [new BoxEl { Key = "profile-body:" + scopeEpoch.ToString(CultureInfo.InvariantCulture), Direction = 1, Children = [region] }],
            }) with
            {
                Key = "profile-scroll:" + routeKey, Grow = 1f, ScrollKey = UseContext(Shell.PageScrollScope) + routeKey,
                OnRealized = _captureViewport, EdgeCues = ScrollEdgeCues.None, Handle = _scroll,
            };

            // The page PROVIDES its accent (Design.AccentCtx): the heart and every other ambient consumer read it.
            return Ctx.Provide(Design.AccentCtx.Slot, (IReadSignal<Design.PageAccent>?)_pageAccent,
                Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>?)_scrollY, new BoxEl
                {
                    Key = "profile-page:" + routeKey, Grow = 1f, Direction = 1, OnBoundsChanged = _measure,
                    Children = [tint, scroll],
                }));
        }

        // ── the loaded page ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>[wash clipped at the band] under [hero · sentinel · magazine clipped at the band] — Artist.Page's
        /// Compose, verbatim in shape. Plan, anchors and pivot are built in ONE pass so they cannot disagree.</summary>
        Element Compose(User u, string? avatar, ProfileToneSource tone, bool washes)
        {
            var scope = Entities.Current;
            var m = _metrics;
            float width = _width;
            ColorF accent = _accent.Value;
            string uri = u.Uri.Text;
            bool own = _own;

            int topCount = own ? scope.Edges.UserTopArtists.Count(scope.MeSlot) : 0;
            var facts = new ProfileFacts(own, u.ShowFollows,
                own ? ProfileSections.TopState(Home.Feeds.TopContentState.Peek(), topCount) : EdgeState.Complete, topCount,
                u.ProfileReadiness(ProfileShelf.Playlists), u.ProfileCount(ProfileShelf.Playlists),
                u.ProfileReadiness(ProfileShelf.Artists), u.ProfileCount(ProfileShelf.Artists),
                ProfileSections.ListState(u.ProfileReadiness(ProfileShelf.Following), u.ProfileFailure(ProfileShelf.Following)),
                u.ProfileCount(ProfileShelf.Following),
                ProfileSections.ListState(u.ProfileReadiness(ProfileShelf.Followers), u.ProfileFailure(ProfileShelf.Followers)),
                u.ProfileCount(ProfileShelf.Followers));
            int n = ProfileSections.Plan(in facts, _plan);

            var sections = new Element[n];
            var pivotItems = new (string Label, Action OnClick)[n];
            int hash = 17;
            for (int i = 0; i < n; i++)
            {
                var s = _plan[i];
                int k = (int)s;
                Latch(s, u, in facts, accent);
                // Each section is ITS OWN region: a late list shimmers in place, never holding the hero (Group null).
                Element region = new SkelRegionEl(
                    Pending: _secPending[k], Failed: _secFailed[k], Content: _secContent[k], ShimmerSource: _secShimmer[k],
                    OnFailed: _secFailedPanel[k], Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default,
                    Group: null, SmoothResize: false);
                // EVERY section keyed: sections appear mid-stream as data lands.
                sections[i] = new BoxEl
                {
                    Key = s_anchorKeys[k], Direction = 1, MinWidth = 0f, OnRealized = _anchorRealized[k],
                    Children = [new BoxEl { Key = s_secKeys[k], Direction = 1, MinWidth = 0f, Children = [region] }],
                };
                pivotItems[i] = (PivotLabel(s), _sectionClicks[k]);
                hash = unchecked(hash * 31 + k + 1);
            }
            _pivotCount = n;
            _pivotHash = hash;

            float collapse = ProfileHeroLayout.CollapseDistance(in m);
            bool compact = _compact.Value;
            Element band = BandBar(uri, own, width, m.Gutter, collapse, compact, pivotItems);
            Element hero = HeroBanner(HeroText.For(u, own, avatar), uri, width, in m, compact, _acts, band);
            Element sentinel = new BoxEl { Height = 0f, HitTestVisible = false }
                .Sticky(ArtistHeroLayout.CompactIdentityHeight, engaged: _compact);
            Element wash = Palette.ArtistBlendWash(tone.PaletteUrl, ProfileHeroLayout.WashHeight(in m),
                ProfileHeroLayout.WashBoundary(in m), disabled: !washes, key: "profile-wash:" + uri,
                payloadAccent: tone.PayloadArgb);
            // THE MAGAZINE'S CLIP IS THE CONTRACT: the band paints nothing, so no CONTENT may render into its 56 DIP — the
            // magazine is cut at the line, its cut feathered exactly while the clip is engaged. The WASH is deliberately
            // NOT cut (unlike Artist.Page, whose full-bleed photo covers the band's 56 at rest): a profile hero has no
            // photo, so a cut wash left a bare strip of shell colour above the name. Uncut, the colour fills the hero to
            // its top and simply scrolls away under the transparent band.
            Element magazine = new BoxEl
            {
                Key = "profile-under-band", Direction = 1,
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
                    new BoxEl { Key = "profile-wash", Direction = 1, HitTestVisible = false, Children = [wash] },
                    new BoxEl { Direction = 1, Children = [hero, sentinel, magazine] },
                ],
            };
        }

        /// <summary>The centred magazine column: Grow toward the row's free width, capped at 1600, the hero's gutter on
        /// both sides, a 32 section gap, the page's bottom reserve (PageGeometry.BottomReserve) under the last section.</summary>
        static Element Magazine(Element[] sections, float gutter) => new BoxEl
        {
            Direction = 1, Gap = Design.Size.SectionGap,
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f, MaxWidth = Design.Size.PageMaxW,
            Padding = new Edges4(gutter, Spacing.M, gutter, PageGeometry.BottomReserve),
            Children = sections,
        };

        float MagazineInnerWidth() => MathF.Max(1f, MathF.Min(_width, Design.Size.PageMaxW) - 2f * _metrics.Gutter);

        /// <summary>One section's body verdict + element, latched for its region's delegates.</summary>
        void Latch(ProfileSection s, User u, in ProfileFacts f, ColorF accent)
        {
            int k = (int)s;
            var (state, count) = s switch
            {
                ProfileSection.TopArtists => (f.TopArtists, f.TopArtistCount),
                ProfileSection.Playlists => (f.Playlists, f.PlaylistCount),
                ProfileSection.RecentArtists => (f.RecentArtists, f.RecentArtistCount),
                ProfileSection.Following => (f.Following, f.FollowingCount),
                _ => (f.Followers, f.FollowerCount),
            };
            var body = ProfileSections.BodyOf(state, count);
            _secBody[k] = body;
            _secEl[k] = body switch
            {
                ProfileSectionBody.Cards => Shelf(s, CardsOf(s, u), accent, SeeAllFor(s, u, count)),
                ProfileSectionBody.Empty => Artist.SectionBlock(SectionTitle(s), EmptyShelf(s, f.Own), accent),
                _ => null,
            };
        }

        /// <summary>"See all" only for the two people sections, and only when the list holds more than the shelf shows.</summary>
        Action? SeeAllFor(ProfileSection s, User u, int count)
        {
            if (s != ProfileSection.Following && s != ProfileSection.Followers) return null;
            var shelf = s == ProfileSection.Following ? ProfileShelf.Following : ProfileShelf.Followers;
            int total = Math.Max(count, u.ProfileTotal(shelf));
            return ProfileSections.SeeAll(s, total, Math.Min(count, ProfileSections.CapOf(s))) ? _seeAll[(int)s] : null;
        }

        static ProfileCard[] CardsOf(ProfileSection s, User u)
        {
            var scope = Entities.Current;
            return s switch
            {
                ProfileSection.TopArtists => Ranked(scope.Edges.UserTopArtists.Targets(scope.MeSlot), ProfileSections.TopCap),
                ProfileSection.Playlists => Cards(u, ProfileShelf.Playlists, ProfileSections.PlaylistCap, EntityKind.Playlist),
                ProfileSection.RecentArtists => Cards(u, ProfileShelf.Artists, ProfileSections.ArtistCap, EntityKind.Artist),
                ProfileSection.Following => Cards(u, ProfileShelf.Following, ProfileSections.PeopleCap, EntityKind.User),
                _ => Cards(u, ProfileShelf.Followers, ProfileSections.PeopleCap, EntityKind.User),
            };
        }

        static ProfileCard[] Cards(User u, ProfileShelf shelf, int cap, EntityKind fallback)
        {
            var targets = u.ProfileTargets(shelf);
            var payload = u.ProfileCards(shelf);
            int n = Math.Min(targets.Length, cap);
            var items = new ProfileCard[n];
            for (int i = 0; i < n; i++)
            {
                var kind = i < payload.Length && payload[i].Kind != EntityKind.Unknown ? payload[i].Kind : fallback;
                int t = targets[i];
                items[i] = new ProfileCard(kind, t, VersionOf(kind, t), i < payload.Length ? payload[i].Followers : 0, i + 1, KeyOf(kind, t));
            }
            return items;
        }

        static ProfileCard[] Ranked(ReadOnlySpan<int> artists, int cap)
        {
            int n = Math.Min(artists.Length, cap);
            var items = new ProfileCard[n];
            for (int i = 0; i < n; i++)
                items[i] = new ProfileCard(EntityKind.Artist, artists[i], VersionOf(EntityKind.Artist, artists[i]), 0, i + 1,
                    KeyOf(EntityKind.Artist, artists[i]));
            return items;
        }

        static uint VersionOf(EntityKind kind, int slot) => Entities.TableFor(kind) is { } t ? RowFold.Version(t, slot) : 0u;

        static string KeyOf(EntityKind kind, int slot)
            => (kind switch { EntityKind.User => "u", EntityKind.Playlist => "p", _ => "a" }) + slot.ToString(CultureInfo.InvariantCulture);

        void OpenList(ProfileFacet facet)
        {
            if (_user.IsValid) Shell.GoTo(ProfileListRoute.For(_user.Uri, facet));
        }

        // ── the context band (Artist.Page §2.2, the profile arm: title · pivot · Follow as a word) ──────────────────────

        Element BandBar(string uri, bool own, float width, float gutter, float collapse, bool canHit,
                        (string Label, Action OnClick)[] pivotItems)
        {
            float actionH = Detail.BandLayout.Height - 2f * Spacing.M;
            string name = _name;
            // No "Overview" pivot tab: the title itself is the way back to the top of the page.
            Element title = new BoxEl
            {
                Direction = 1, MinWidth = 0f, Shrink = 1f, MaxWidth = Detail.BandLayout.TitleCap,
                Cursor = CursorId.Hand, OnClick = _scrollToTop,
                Children = [Detail.BandTitle(name)],
            };
            // The pivot is the ONLY elastic lane; the title and the actions never drop.
            Element pivot = new BoxEl
            {
                Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = Detail.BandLayout.Height, AlignItems = FlexAlign.Center,
                Children = [Detail.Pivot(pivotItems, _active, _accentFn)],
            };
            Element actions = own
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
            // The content sits at the page's 1600 measure; the band's extent stays full-bleed.
            Element row = Detail.Band(MathF.Min(width, Design.Size.PageMaxW), gutter, [title, pivot, actions]);
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
                        HitTestVisible = false, Children = [Detail.BandHairline()],
                    },
                ],
            }.Reveal(revealStart, collapse - revealStart, Design.Reduced ? 0f : Spacing.XS).Skeletonized(false);
        }

        /// <summary>A pivot click parks the section's top exactly under the band, animated unless reduced motion is on,
        /// through the engine's one bring-into-view seam against the page's OWN viewport.</summary>
        void GoToSection(int section)
        {
            var scene = Context.Scene;
            var node = _anchors[section];
            if (scene is null || node.IsNull || _viewport.IsNull || !scene.IsLive(node) || !scene.IsLive(_viewport)) return;
            scene.BringIntoView(_viewport, node, align: 0f, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide,
                margin: Detail.BandLayout.Height);
        }

        /// <summary>THE SPY: one AbsoluteRect per pivot section, only on a scroll step the 24-DIP projector let through;
        /// the pivot re-renders only when the answer changes.</summary>
        void ResolveSpy()
        {
            _ = _scrollY.Value;
            _ = _viewportH.Value;
            bool atEnd = _atEnd.Value;
            _ = _pivotEpoch.Value;
            var scene = Context.Scene;
            if (scene is null || _viewport.IsNull || !scene.IsLive(_viewport)) return;
            int n = _pivotCount;
            if (n == 0) return;
            RectF vp = scene.AbsoluteRect(_viewport);
            Span<float> tops = stackalloc float[ProfileSections.Count];
            for (int i = 0; i < n; i++)
            {
                var node = _anchors[(int)_plan[i]];
                tops[i] = node.IsNull || !scene.IsLive(node) ? float.NaN : scene.AbsoluteRect(node).Y - vp.Y;
            }
            float vh = _viewportH.Peek();
            if (vh <= 0f) vh = vp.H;
            int at = Detail.BandLayout.ActiveSection(tops[..n], Detail.BandLayout.Height, vh, atEnd);
            if (at != -1) _active.SetIfChanged(at);   // −1 = no answer: hold what we had; NoSection lights nothing
        }

        // ── demand (ProfileAsk is the ONE data path) ─────────────────────────────────────────────────────────────────

        /// <summary>Once per (user, scope), and again on a keep-alive return: the plan decides Ensure (first visit — the row
        /// and both lists in parallel) vs Invalidate (a revisit re-reads, stale keeps rendering). The riding shelves have
        /// no ask: Social brings them.</summary>
        void Demand()
        {
            var u = _user;
            if (!u.IsValid) return;
            ProfileAsk.Apply(u, ProfileAsk.Plan(u, ProfileSurface.Page));
        }

        /// <summary>Own top artists (idempotent, 30-minute freshness): re-keyed on "is this mine" so a vanity uri that only
        /// learns it is yours when Social lands still asks.</summary>
        void DemandTop()
        {
            if (_own) Home.Feeds.EnsureTopContent();
        }

        void Retry()
        {
            var u = _user;
            if (u.IsValid) ProfileAsk.RetryHeader(u);
        }

        void RetrySection(ProfileSection s)
        {
            var u = _user;
            if (!u.IsValid) return;
            switch (s)
            {
                case ProfileSection.TopArtists: Home.Feeds.EnsureTopContent(); break;
                case ProfileSection.Following: ProfileAsk.RetryList(u, ProfileShelf.Following); break;
                case ProfileSection.Followers: ProfileAsk.RetryList(u, ProfileShelf.Followers); break;
                default: ProfileAsk.RetryHeader(u); break;   // the riding shelves retry WITH the header
            }
        }

        // ── the render-cost fold (Artist.Page.Stamp's shape) ───────────────────────────────────────────────────────────

        /// <summary>WAKE on every table/edge the page paints; PUSH only what moved for THIS user. Unlike the artist fold it
        /// also folds the row's MARKS (Known/Asked/Failed/Inflight) and each shelf's READINESS + version, and wakes on
        /// <c>Fetch.Settled</c>: a 404 seal or a terminal failure bumps no Version and publishes no table (mark-only
        /// changes), and without these the page would shimmer forever instead of turning Unavailable/Failed.</summary>
        long Stamp()
        {
            _ = Entities.ScopeEpoch.Value;
            var u = _user;
            if (!u.IsValid) return 0L;
            var scope = Entities.Current;
            var e = scope.Edges;
            var users = scope.Users;
            int slot = u.Slot, me = scope.MeSlot;

            // ── the wakes ──
            _ = users.Changed.Value;
            _ = scope.Playlists.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = e.ProfilePlaylists.Changed.Value;
            _ = e.ProfileArtists.Changed.Value;
            _ = e.ProfileFollowing.Changed.Value;
            _ = e.ProfileFollowers.Changed.Value;
            _ = e.UserTopArtists.Changed.Value;
            _ = global::Wavee.Fetch.Settled.Value;
            var top = Home.Feeds.TopContentState.Value;

            // ── the fold ──
            ulong h = RowFold.Row(RowFold.Seed, users, slot);
            h = RowFold.Add(h, users.Known[slot]);
            h = RowFold.Add(h, users.Asked[slot]);
            h = RowFold.Add(h, users.Failed[slot]);
            h = RowFold.Add(h, users.Inflight[slot]);
            h = Fold(h, u, ProfileShelf.Playlists, ProfileSections.PlaylistCap);
            h = Fold(h, u, ProfileShelf.Artists, ProfileSections.ArtistCap);
            h = Fold(h, u, ProfileShelf.Following, ProfileSections.PeopleCap);
            h = Fold(h, u, ProfileShelf.Followers, ProfileSections.PeopleCap);
            if (slot == me || u.IsCurrentUser)
            {
                h = RowFold.Add(h, (int)top);
                h = RowFold.Add(h, e.UserTopArtists.Version(me));
                var tops = e.UserTopArtists.Targets(me);
                for (int i = 0; i < tops.Length && i < ProfileSections.TopCap; i++) h = RowFold.Row(h, scope.Artists, tops[i]);
            }
            return unchecked((long)h);
        }

        static ulong Fold(ulong h, User u, ProfileShelf shelf, int cap)
        {
            h = RowFold.Add(h, u.ProfileVersion(shelf));
            h = RowFold.Add(h, (int)u.ProfileReadiness(shelf));
            h = RowFold.Add(h, u.ProfileFailure(shelf));
            var targets = u.ProfileTargets(shelf);
            var payload = u.ProfileCards(shelf);
            int n = Math.Min(targets.Length, cap);
            for (int i = 0; i < n; i++)
                if (Entities.TableFor(i < payload.Length ? payload[i].Kind : EntityKind.Unknown) is { } t)
                    h = RowFold.Row(h, t, targets[i]);
            return h;
        }

        // ── colour ─────────────────────────────────────────────────────────────────────────────────────────────────────

        void PublishAccent()
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Users.Changed.Value;
            _ = _theme.Value;
            PublishAccent(_user, _latest?.RouteKey ?? "");
        }

        /// <summary>Graded avatar → profile colour (payload) → held → default (Detail.AccentFor's ladder). The palette url
        /// is WATCHED here, inside the tracked effect, so a grading landing re-derives the accent.</summary>
        void PublishAccent(User u, string routeKey)
        {
            ProfileToneSource tone = default;
            if (u.IsValid)
            {
                string? avatar = u.Image;
                tone = ProfileTone.Of(avatar, u.Color, avatar is { Length: > 0 } a && Palette.CanGrade(a));
            }
            if (tone.PaletteUrl is { Length: > 0 } url) _ = Palette.Watch(url).Value;
            var accent = Detail.AccentFor(tone.PaletteUrl, tone.PayloadArgb);
            _accent.SetIfChanged(accent);
            _pageAccent.SetIfChanged(Detail.PageAccentOf(accent, routeKey));
        }

        // ── the derived shimmer ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A ShimmerSource, never mounted: the SAME hero composition over a placeholder (no band, the action row a
        /// Skeletonized(false) spacer), the divider, and two seed shelves at the magazine's real width — own: Top artists +
        /// Public playlists; other: Public playlists + Recently played artists.</summary>
        Element PageShimmer()
        {
            var m = _metrics;
            float inner = MagazineInnerWidth();
            var first = _own ? ProfileSection.TopArtists : ProfileSection.Playlists;
            var second = _own ? ProfileSection.Playlists : ProfileSection.RecentArtists;
            ColorF accent = _accent.Peek();
            return new BoxEl
            {
                Direction = 1,
                Children =
                [
                    HeroBanner(HeroText.PlaceholderFor(_own), "", _width, in m, false, null, null),
                    new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
                    new BoxEl
                    {
                        Direction = 0, Justify = FlexJustify.Center,
                        Children = [Magazine([SeedShelf(first, accent, inner), SeedShelf(second, accent, inner)], m.Gutter)],
                    },
                ],
            };
        }
    }
}
