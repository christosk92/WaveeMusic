// ── Entities/Profile.Page.cs ───────────────────────────────────────────────────────────────────────────────────────────
// The PROFILE page (spotify:user:…): install + mount point and the CORE HOST — demand on mount and on every revisit
// (through the data layer's ProfileAsk plan), the render-cost stamp (marks + Fetch.Settled folded too: a 404 seal /
// failure moves no Version), the load faces (ProfileLoadRule.Header), the section plan and each section's body verdict and
// cards, the person's tone (accent, shell tint), the scroll spy and the verbs. The hero, the band, the sections and the
// cards are Classic's (T10c2: `Themes\Classic\Structure\Views\Pages\ClassicProfile*.cs`); the pure decisions are
// Profile.Rules.cs; what a card opens or plays is `ProfileVerbs` (Profile.Verbs.cs).
//
// Role: CORE (host)   Owner: U1   Template: Entities/Artist.Page.cs (the band / spy / sentinel / clip are copied from it)
// Spec: docs/plans/wavee/profile-pages-implementation.md Appendix U §3 + "Reconciliation, round 2"; themes plan 6.1 (T10c2)
//
// THE FOUR FACES (ProfileLoadRule.Header): Loading → the derived shimmer; Ready → the page; Unavailable → the page's
// "isn't available" vacancy as CONTENT (no Retry, the 404 seal); Failed → the error vacancy with Retry = RetryHeader.
// The host builds a `ProfilePageProps` on every render and mounts `ViewKeys.ProfilePage`.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

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

    /// <summary>A subject the page can bind a row to: a <c>spotify:user:</c> uri, or YOUR OWN account's row. The own row is staged under the account's bare
    /// id (`--fake`'s "wavee-listener", the convention `Entities.ResolveMe` resolves the account by), which no provider owns, so its uri is not
    /// <see cref="EntityUri.IsValid"/> although its row exists and is known: gating on validity alone left your own profile "unavailable" (never Ready).</summary>
    static bool Addressable(EntityUri subject)
        => subject.IsValid
           || (subject.Id.Form != EntityForm.None && Entities.Current.MeSlot > global::Wavee.Table.None
               && Entities.Current.Users.TryGetSlot(subject.Id, out int slot) && slot == Entities.Current.MeSlot);

    [CoreHost]
    sealed partial class PageHost : Component, IPropsHost
    {
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

        // ── geometry (latched fields — `Artist.PageHost`'s discipline) ──
        // The page's measured width (0 until the first measure: the hero ladder's seed answers until then) and the hero's sub-tier on the structure's
        // ladder, the artist hero's (themes plan 3.6, TC6): a crossing found after mount commits on the first frame that is not resized.
        readonly Signal<float> _heroWidth = new(0f);
        readonly Signal<int> _layoutEpoch = new(0);
        IReadSignal<int>? _heroTier;
        IReadSignal<Size2>? _viewportSize;
        /// <summary>The facts this page is drawn in (`TierContext.DrawnIn`, set in render): its root's, never the process's last window. The seeds
        /// read them before the first render through the process's.</summary>
        IReadSignal<Theming.ThemeFacts>? _facts;
        readonly Func<float> _heroSeed;
        ProfileHeroMetrics _metrics;
        float _width;
        bool _measured;
        /// <summary>How far under the viewport's top the band reaches (`ArtistTierPolicy.BandClearance`: Classic's sticky band 56, a band outside the
        /// viewport 0 or its air): the pivot click's margin and the spy's band edge. Latched each render.</summary>
        float _bandClear = Detail.BandLayout.Height;

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
        readonly Signal<ColorF> _accent = new(HeldAccent());
        readonly Signal<Design.PageAccent> _pageAccent = new(InitialPageAccent());
        int _accentEpoch = -1;   // the Tok.Epoch the accent was last published at (themes plan X44)
        readonly Func<ColorF> _accentFn, _accentPeekFn;

        // ── readiness ──
        ProfileLoad _header = ProfileLoad.Loading;
        bool _bodyReady, _revealed;

        // ── sections ──
        readonly Action[] _secRetry;
        readonly Action?[] _seeAll;

        // ── cached delegates (a node handler never captures a render's closure) ──
        readonly Func<bool> _pendingFn, _failedFn;
        readonly Func<Signal<bool>> _compactEdgeFn;
        readonly Action _demand, _demandTop, _publishAccent, _republishAccent, _resolveSpy, _bumpPivot, _retry;
        readonly Action _scrollToTop, _watchScroll;
        readonly Action<NodeHandle> _captureViewport;
        readonly Action<RectF> _measure;
        readonly Func<long> _stampFn;
        readonly ProfileHeroActs _acts;

        // ── row 2 (P5 Z7): the sections as the band's views, "open account" and "share" as its actions ──
        // A band-style structure (Zune) draws them; Classic reads none of it. The effect runs on the pivot's epoch (the plan changed), the lit section
        // (the spy answered) and the count of sections the last render planned (0 for an unavailable or failed page: the row is withdrawn).
        PageChromeSink _sink = PageChromeSink.None;
        int _chromeCount;
        bool _chromePublished;
        readonly Action _syncChrome, _withdrawChrome;

        public PageHost()
        {
            // The Follow primary / the band's Follow word write through this seam (Show / Playlist page precedent).
            Controls.Library ??= User.LibrarySeam;

            int n = ProfileSections.Count;
            _secRetry = new Action[n]; _seeAll = new Action?[n];
            for (int i = 0; i < n; i++)
            {
                int k = i;
                var s = (ProfileSection)i;
                _anchorRealized[k] = h => _anchors[k] = h;
                _sectionClicks[k] = () => GoToSection(k);
                _secRetry[k] = () => RetrySection(s);
                if (s == ProfileSection.Following) _seeAll[k] = () => OpenList(ProfileFacet.Following);
                else if (s == ProfileSection.Followers) _seeAll[k] = () => OpenList(ProfileFacet.Followers);
            }
            _accentFn = () => _accent.Value;
            _accentPeekFn = () => _accent.Peek();
            _compactEdgeFn = () => _compact;
            _pendingFn = () => !_bodyReady && _header is not (ProfileLoad.Failed or ProfileLoad.Unavailable);
            _failedFn = () => !_bodyReady && _header == ProfileLoad.Failed;
            _retry = Retry;
            _demand = Demand;
            _demandTop = DemandTop;
            _publishAccent = PublishAccent;
            _republishAccent = RepublishAccent;
            _resolveSpy = ResolveSpy;
            _bumpPivot = () => _pivotEpoch.Value = _pivotEpoch.Peek() + 1;
            _scrollToTop = () => _scroll.ScrollTo(0.0, GlideOrJump());
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
            _syncChrome = SyncChrome;
            _withdrawChrome = () =>
            {
                if (!_chromePublished) return;
                _chromePublished = false;
                _sink.Publish(null);
            };
            _heroSeed = () => Detail.Breakpoints.EstimatePageWidth((_facts ?? Theming.ThemeHost.Facts).Peek().Tiers, _viewportSize?.Peek().Width ?? 0f);
            _acts = new ProfileHeroActs(
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
                _accentEpoch = ThemeEpoch();
                if (Addressable(_latest.Subject)) PublishAccent(Entities.User(_latest.Subject), _latest.RouteKey);
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
                _user = Addressable(p.Subject) ? Entities.User(p.Subject) : default;   // allocates the row: bind now, the demand fills
                Array.Clear(_anchors);                                            // the body is epoch-keyed: re-register
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
            UseEffect(_republishAccent, DepKey.From(ThemeEpoch()));    // a theme flip or a definition swap re-derives the accent (X44)
            UseEffect(_resolveSpy);
            _sink = UseContext(PageChromeSink.Context);
            UseSignalEffect(_syncChrome);
            UseEffect(() => _withdrawChrome, DepKey.Empty);   // the row leaves with the page
            UseActivation(onActivated: _demand);                   // keep-alive return re-reads (SWR: ProfileAsk → Invalidate)

            // THE HERO'S SUB-TIER AND METRICS (TC6): the artist hero's ladder over the page's own width, seeded before the first measure from the content
            // region the frame resolved the window tiers on; the metrics are the structure's record for that sub-tier in the window tier on screen.
            _viewportSize = UseContextSignal(Viewport.Size);
            _facts = Theming.TierContext.DrawnIn(UseContext(Theming.ThemeFactsContext.Signal));
            _heroTier = UseTier(Theming.ArtistLadders.Of(ShellPolicy.Now.Tiers, Theming.ArtistLadders.Hero), TierSource.Measured(_heroWidth, _heroSeed));
            var tierFacts = _facts.Value.Tiers;
            _ = _layoutEpoch.Value;
            float measuredW = _heroWidth.Value;
            _width = MathF.Max(1f, measuredW > 0.5f ? measuredW : _heroSeed());
            _metrics = ShellPolicy.Tracked.ArtistTiers.ProfileHero(Theming.ArtistLadders.HeroOf(_heroTier.Value), in tierFacts, _width);
            _bandClear = ShellPolicy.Tracked.ArtistTiers.BandClearance(in tierFacts);

            _header = valid ? ProfileLoadRule.Header(u) : ProfileLoad.Unavailable;
            _bodyReady = ProfileReveal.BodyReady(_header, _measured, _revealed);
            if (_bodyReady) _revealed = true;

            string routeKey = p.RouteKey;
            string? avatar = valid ? u.Image : null;
            var tone = ProfileTone.Of(avatar, valid ? u.Color : 0u, avatar is { Length: > 0 } a && Palette.CanGrade(a));
            // A 0×0 leaf owns the watch + the publish. Unavailable/Failed opt out (definite → neutral chrome).
            Element tint = Palette.ShellTint(tone.PaletteUrl, ready: Detail.CoverLatch.IsUsable(tone.PaletteUrl),
                apply: _header is not (ProfileLoad.Unavailable or ProfileLoad.Failed),
                owner: _tintOwner, slot: shellSlot, key: "profile-tint:" + routeKey, payloadAccent: tone.PayloadArgb);

            bool unavailable = _header == ProfileLoad.Unavailable;
            bool composed = !unavailable && _bodyReady;
            ProfileSectionSlot[] sections = composed ? PlanSections(u) : [];
            _chromeCount = composed ? _pivotCount : 0;
            UseEffect(_bumpPivot, DepKey.From(_pivotHash, (int)scopeEpoch, _chromeCount, 0));
            UseSignalEffect(_watchScroll);

            // The accent and the band's engaged edge are read here, as the page always read them (only for a body that is composed): this render wakes on
            // the grading and on the sentinel's flip. The derived skeleton peeks at the accent instead (`AccentPeek`).
            ColorF accent = composed ? _accent.Value : _accent.Peek();
            bool compact = composed && _compact.Value;

            return global::FluentGpu.Theming.Slots.View(ViewKeys.ProfilePage, new ProfilePageProps
            {
                RouteKey = routeKey,
                ScopeKey = scopeEpoch.ToString(CultureInfo.InvariantCulture),
                ScrollKey = UseContext(Shell.PageScrollScope) + routeKey,
                Uri = valid ? u.Uri.Text : "",
                Name = _name,
                Own = _own,
                Pending = _pendingFn,
                Failed = _failedFn,
                Retry = valid ? _retry : null,
                Unavailable = unavailable,
                BodyReady = composed,
                Width = _width,
                Metrics = _metrics,
                Measure = _measure,
                Accent = accent,
                AccentFn = _accentFn,
                AccentPeek = _accentPeekFn,
                PageAccent = _pageAccent,
                PaletteUrl = tone.PaletteUrl,
                PayloadAccent = tone.PayloadArgb,
                Tint = tint,
                Hero = composed ? ProfileHeroText.For(u, _own, avatar) : ProfileHeroText.PlaceholderFor(_own),
                HeroActs = _acts,
                Compact = compact,
                CompactEdge = _compactEdgeFn,
                Active = _active,
                Scroll = _scroll,
                CaptureViewport = _captureViewport,
                ScrollY = _scrollY,
                ScrollToTop = _scrollToTop,
                Sections = sections,
            });
        }

        // ── row 2 ────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The pivot's short word for a section (the band's view label).</summary>
        static string PivotKeyOf(ProfileSection s) => s switch
        {
            ProfileSection.TopArtists => Strings.Person.Pivot.TopArtists,
            ProfileSection.Playlists => Strings.Person.Pivot.Playlists,
            ProfileSection.RecentArtists => Strings.Person.Pivot.Artists,
            ProfileSection.Following => Strings.Person.Pivot.Following,
            _ => Strings.Person.Pivot.Followers,
        };

        /// <summary>Publishes row 2 through the page's sink: one view per planned section (the one the spy lit is current; a view scrolls to its section),
        /// then the actions. Runs as a signal effect, so the lit view follows the scroll; withdraws when the page holds no sections.</summary>
        void SyncChrome()
        {
            int active = _active.Value;
            _ = _pivotEpoch.Value;
            int n = _chromeCount;
            var sink = _sink;
            if (n <= 0 || !_user.IsValid)
            {
                if (_chromePublished) { _chromePublished = false; sink.Publish(null); }
                return;
            }
            int lit = active >= 0 && active < n ? active : 0;
            var views = new PageView[n];
            for (int i = 0; i < n; i++)
            {
                var s = _plan[i];
                views[i] = new PageView(ProfileSections.Key(s), Loc.Get(PivotKeyOf(s)), i == lit, _sectionClicks[(int)s]);
            }
            var actions = new List<PageAction>(2);
            if (_own) actions.Add(new PageAction("open-account", Loc.Get(Strings.Person.OpenAccount), ProfileVerbs.OpenAccount));
            actions.Add(new PageAction("share", Loc.Get(Strings.Menu.Share), _acts.Share));
            _chromePublished = true;
            sink.Publish(new PageChromeRow(views, actions));
        }

        // ── the section plan ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The page's sections in their one fixed order, each with its body verdict, its cards and its verbs. Plan, anchors and pivot are built in ONE
        /// pass so they cannot disagree.</summary>
        ProfileSectionSlot[] PlanSections(User u)
        {
            var scope = Entities.Current;
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

            var slots = new ProfileSectionSlot[n];
            int hash = 17;
            for (int i = 0; i < n; i++)
            {
                var s = _plan[i];
                int k = (int)s;
                var (state, count) = s switch
                {
                    ProfileSection.TopArtists => (facts.TopArtists, facts.TopArtistCount),
                    ProfileSection.Playlists => (facts.Playlists, facts.PlaylistCount),
                    ProfileSection.RecentArtists => (facts.RecentArtists, facts.RecentArtistCount),
                    ProfileSection.Following => (facts.Following, facts.FollowingCount),
                    _ => (facts.Followers, facts.FollowerCount),
                };
                var body = ProfileSections.BodyOf(state, count);
                bool cards = body == ProfileSectionBody.Cards;
                slots[i] = new ProfileSectionSlot(s, body, cards ? CardsOf(s, u) : [], cards ? SeeAllFor(s, u, count) : null,
                    _secRetry[k], _sectionClicks[k], _anchorRealized[k]);
                hash = unchecked(hash * 31 + k + 1);
            }
            _pivotCount = n;
            _pivotHash = hash;
            return slots;
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

        /// <summary>A pivot click parks the section's top exactly under the band, animated unless reduced motion is on,
        /// through the engine's one bring-into-view seam against the page's OWN viewport.</summary>
        void GoToSection(int section)
        {
            var scene = Context.Scene;
            var node = _anchors[section];
            if (scene is null || node.IsNull || _viewport.IsNull || !scene.IsLive(node) || !scene.IsLive(_viewport)) return;
            scene.BringIntoView(_viewport, node, align: 0f, GlideOrJump(), margin: _bandClear);
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
            int at = Detail.BandLayout.ActiveSection(tops[..n], _bandClear, vh, atEnd);
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
            PublishAccent(_user, _latest?.RouteKey ?? "");
        }

        /// <summary>Themes plan X44: the accent follows <c>Tok.Epoch</c> (the light/dark kind AND the definition) instead of a
        /// <c>Signal&lt;ThemeKind&gt;</c> the page republished on every flip. The first publish (<see cref="ApplyProps"/>) already ran
        /// at the current epoch, so the effect's mount run does nothing.</summary>
        void RepublishAccent()
        {
            if (_accentEpoch == ThemeEpoch()) return;
            _accentEpoch = ThemeEpoch();
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
            _pageAccent.SetIfChanged(PageAccentOf(accent, routeKey));
        }
    }

    // ── what a host type may not read itself (WVTH008: no token / design reads inside a `[CoreHost]`) ───────────────────────

    /// <summary>The last remembered page accent, else the theme's default (so the first frame never paints the default over a held colour).</summary>
    static ColorF HeldAccent() => Detail.HeldAccent ?? Tok.AccentDefault;

    static Design.PageAccent InitialPageAccent()
        => new(Detail.HeldAccent ?? Tok.AccentTextPrimary, Detail.HeldAccent ?? Tok.AccentDefault, "");

    static Design.PageAccent PageAccentOf(ColorF accent, string routeKey) => new(accent, accent, routeKey);

    /// <summary>The theme epoch (the light/dark kind AND the definition) the accent was last published at.</summary>
    static int ThemeEpoch() => Tok.Epoch;

    /// <summary>The scroll move a pivot click and the band title use: a glide, or an immediate jump under reduced motion.</summary>
    static ScrollMove GlideOrJump() => Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide;

    /// <summary>The ⋯ menu: Share ▸ (Copy link / Copy Spotify URI / Open in Spotify Web) — the show page's ⋯ grammar
    /// (<c>Episode.ShareMenu</c>, whose Copy link reads the uri; the registered CopyLink verb reads a TRACK set). A
    /// profile has no Pin row: no sidebar pin resolves a user route yet, and the verb would only ever be absent. Null when
    /// nothing applies, so the ⋯ opens nothing rather than an empty flyout.</summary>
    internal static ContextMenuModel? MenuFor(User u, string name)
    {
        if (!u.IsValid || !u.Uri.IsValid) return null;
        var ctx = new ActionContext(new ActionTarget(TargetKind.None, [], u.Uri, name, PlaylistHost.None), Actions.Services);
        if (Episode.ShareMenu(u.Uri, in ctx) is not { } share) return null;
        return new ContextMenuModel([share], Actions.Menu.Header(u.Image, name, Loc.Get(Strings.Search.TypeUser), circular: true));
    }

    /// <summary>A follower-count message (<paramref name="key"/> = a <c>…FollowersKey</c> const): <c>count</c> picks the
    /// plural arm, <c>n</c> is the grouped numeral — the formatter's own <c>#</c> prints invariant digits with no grouping
    /// ("85324 followers").</summary>
    internal static string FollowerCount(string key, int count)
        => Loc.Format(key, ("count", count), ("n", ProfileStats.Numeral(count, CultureInfo.CurrentCulture)));
}
