// ── Entities/Profile.Lists.Page.cs — the Following / Followers list pages (people:<facet>:<user uri>) ───────────────────
//
// Role: UI
// Owner: R2 (profile pages, issue #161)
// Spec: docs/plans/wavee/profile-pages-implementation.md Appendix R §4.2/§5 + "Reconciliation, round 2"; the pure half is
//       Profile.Lists.cs (load rule, entries + filter, grid fit, letter rows).
//
// ── THE SHAPE ────────────────────────────────────────────────────────────────────────────────────────────────────────
//
//  PageHost (keyed "people-page:" + FrameRules.PageKeyOf: the user, NOT the facet)
//  └─ column
//     ├─ HEAD (always real, outside the region): the shared PageHead, CrumbTitleViews (200, route-static): BreadcrumbBar
//     │        [name › facet] · the facet title · the Following / Followers views bar (a change NAVIGATES, the route is the
//     │        truth, not the control; Discography's head). The bar is on ONE field signal (`_facetIndex`) synced from the
//     │        route in a layout effect.
//     └─ BODY (page gutter) └─ measured box (_bodyW, floored to 8 DIP) └─ ONE SkelRegionEl (Soft; no outer ScrollView —
//              the bound list OWNS the scroll)
//          Pending     → the derived shimmer (tool bones + two rows of circular seed cards; stateful leaves never mount)
//          Ready       → [tools: chips + find] over [ ZStack{ list · StickyLetter } · JumpStrip ]
//          Empty/Hidden/Unavailable → Controls.Vacancy as CONTENT (no Retry); Failed → the error vacancy with Retry
//
// THE LIST. `ItemsView.CreateBound` over a FLAT projection of letter HEADERS and ROWS of N cards (`ProfileLetterRows`):
// `RepeatLayout.GridFit` has no full-width header items, so the grid is rows we chunk ourselves, each PINNED to the extent the
// projection says (the sticky letter and the jump read the same prefix sums, so seed == measured). `CreateBound` provides no
// `ItemsView.SlotRow`, so every card owns its own click, focus and menu. The list REMOUNTS when its geometry changes
// (columns, grouping: `MountKey`) and re-skins in place when only data lands (`ProfileRowItem.Epoch`).
//
// ONE PAGE PER USER, NOT PER FACET. The page key (Shell.FrameRules.PageKeyOf) and the keep-alive slot ignore the facet, so a
// Following <-> Followers click re-binds THIS instance: the views pill slides (the bar never re-mounts), `_facetIndex` moves,
// `Sync` re-derives for the new relation (it reads `_facetIndex`, the one signal that carries the facet into the effect), the
// chip and the find box reset, and only the body below re-skeletons in place. The list REMOUNTS (the mount key carries the
// facet) and each facet restores its own scroll offset (its ScrollKey carries the facet). The route stays the truth: back and
// forward move the facet through the same re-bind.
//
// DEMAND (the sanctioned planner path): `ProfileAsk.Apply(user, ProfileAsk.Plan(user, surface))` once per (user, facet, scope
// epoch). Retry = `RetryList` (+ `RetryHeader` when the profile row itself failed) — Refresh, never Ensure.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace Wavee;

public static partial class ProfileLists
{
    /// <summary>The route's page: <c>people:&lt;facet&gt;:&lt;user uri&gt;</c>. Keyed by the USER (<c>Shell.FrameRules.PageKeyOf</c>),
    /// so another user is a fresh page and another facet of the same user re-binds the mounted one. <c>PageProps.Key</c> is the
    /// whole route key (the list's scroll identity). Registered by <c>Profile.InstallPages()</c> for <c>RouteKind.ProfileList</c>.</summary>
    // MOUNT POINT (profile pages: Shell lazy group {User, ProfileList})
    public static Element Page(in Shell.Route route)
    {
        string key = Shell.NameOf(route);
        return Embed.Comp(new PageProps(route, key), static () => new PageHost()) with { Key = "people-page:" + Shell.FrameRules.PageKeyOf(route) };
    }

    sealed record PageProps(Shell.Route Route, string Key);

    /// <summary>What the list shows, as a VALUE: the load face, the facet, the counts, the grid plan, the flat row count and
    /// the mount identity. Equality is the compiler's, so a re-derivation that changed nothing writes nothing.
    /// <paramref name="Epoch"/> moves whenever the rows were rebuilt (data, filter or width), which re-fires the realized slots.</summary>
    sealed record ListShape(ProfileListLoad Load, ProfileFacet Facet, int Total, int Visible, ProfileChip Chip,
                            ProfileChipCounts Counts, ProfileGridPlan Plan, int Rows, string MountKey, uint Epoch)
    {
        public static readonly ListShape Pending = new(ProfileListLoad.Pending, ProfileFacet.Following, 0, 0, ProfileChip.All,
            default, default, 0, "", 0u);
    }

    static readonly Func<Element> s_followShape = static () => Controls.FollowToggle.SkeletonShape();
    static readonly string[] s_faceKeys =
        ["people:pending", "people:ready", "people:empty", "people:hidden", "people:unavailable", "people:failed"];

    sealed class PageHost : Component
    {
        // ── identity (rebound by Bind on a subject or scope change) ──
        Scope? _scope;
        EntityUri _subject;
        bool _parsed;
        User _user;
        ProfileFacet _facet = ProfileFacet.Following;
        ProfileShelf _shelf = ProfileShelf.Following;
        ProfileSurface _surface = ProfileSurface.Following;
        string _scrollScope = "";

        // ── state ──
        readonly Signal<ListShape> _shape;
        internal ListShape ShapeNow;
        /// <summary>The views bar's selection: ONE signal per page instance (the bar never re-mounts, the pill slides), synced
        /// from the route's facet in a layout effect. <see cref="Sync"/> reads it, which is what re-derives the list for the new
        /// relation when the facet changes under a mounted page.</summary>
        readonly Signal<int> _facetIndex = new(0);
        readonly Signal<ProfileChip> _chip = new(ProfileChip.All);
        readonly Signal<string> _query = new("");
        /// <summary>The body's measured width, floored to 8 DIP (never wider than the box, so a card never overhangs).</summary>
        readonly Signal<float> _bodyW = new(0f);
        readonly Signal<int> _sticky = new(-1);
        readonly Signal<float> _push = new(0f);
        readonly Signal<uint> _present = new(0u);
        IReadSignal<string>? _debounced;

        // ── the pooled model and the flat projection ──
        readonly ProfileListModel _model = new();
        internal readonly ProfileLetterRows Letters = new();
        readonly BoundItemsSource<ProfileRowItem> _rows;
        readonly ScrollHandle _handle = new();
        readonly ItemsViewController _ctl = new();
        uint _epoch;
        long _lettersKey = long.MinValue;

        // ── what the last Sync derived from (so a keystroke does not re-read the tables, a data landing does) ──
        Scope? _dataScope;
        int _dataSlot = -1;
        ProfileShelf _dataShelf;
        uint _uv, _av, _ev;
        ProfileChip _chipSeen = (ProfileChip)255;
        string? _querySeen;
        ProfileGridPlan _planSeen;
        string _mountKeyNow = "";

        // ── the one-entry mount cache (RepeatLayout.Extents is STATEFUL: a fresh one per render throws every measurement away) ──
        string? _mountKey;
        RepeatLayout _layout;
        ListOptions<ProfileRowItem>? _options;
        Element? _stickyEl, _stripEl;

        // ── cached delegates (a node handler never captures a render's closure) ──
        readonly Func<bool> _pendingFn, _failedFn;
        readonly Func<Element> _contentFn, _shimmerFn, _failedPanelFn;
        readonly Action _demand, _sync, _watchLetters, _retry, _syncFacet;
        readonly Action<int> _jump, _onView;
        readonly Action<RectF> _onBounds;
        readonly Func<BoundItemScope<ProfileRowItem>, Element> _rowT;
        readonly Func<int, float> _extentOf;
        readonly Func<int, int> _contentType;
        readonly Action[] _chipClicks;

        public PageHost()
        {
            // The per-artist Follow toggle writes through this seam (Show / Playlist page precedent).
            Controls.Library ??= User.LibrarySeam;

            ShapeNow = ListShape.Pending;
            _shape = new Signal<ListShape>(ShapeNow);
            _rows = BoundItems.Project(_shape, static s => s.Rows, (_, i) => Letters.Item(i), default(ProfileRowItem));

            _demand = Demand;
            _sync = Sync;
            _watchLetters = WatchLetters;
            _retry = Retry;
            _syncFacet = SyncFacet;
            _onView = OnView;
            _jump = Jump;
            _onBounds = r =>
            {
                if (r.W <= 0f) return;
                float q = MathF.Floor(r.W / 8f) * 8f;
                if (q != _bodyW.Peek()) _bodyW.Value = q;
            };
            // Pending until the derivation has a measured grid (Plan.Columns > 0), so the content never mounts at zero columns
            // and is replaced a frame later: the shape is only written by Sync, off the measured width.
            _pendingFn = () => { var s = _shape.Value; return s.Load == ProfileListLoad.Pending || s.Plan.Columns == 0; };
            _failedFn = () => _shape.Value.Load == ProfileListLoad.Failed;
            _contentFn = Content;
            _shimmerFn = Shimmer;
            _failedPanelFn = () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: _retry);
            _rowT = scope => Embed.Comp(() => new RowSlot(this, scope));
            _extentOf = flat => Letters.ExtentOf(flat);
            _contentType = i => Letters.IsHeader(i) ? 1 : 0;
            _chipClicks =
            [
                () => _chip.Value = ProfileChip.All,
                () => _chip.Value = ProfileChip.Artists,
                () => _chip.Value = ProfileChip.People,
            ];
        }

        // ── identity ─────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Rebind the user handle when the subject or the scope moved (an account switch re-points every table).
        /// Called by Render AND by Sync, so an effect that runs before the render after a scope switch never reads a
        /// slot of the old scope.</summary>
        void Bind(bool parsed, EntityUri uri, ProfileFacet facet)
        {
            var scope = Entities.Current;
            if (!ReferenceEquals(_scope, scope) || _parsed != parsed || !_subject.Equals(uri))
            {
                _scope = scope;
                _parsed = parsed;
                _subject = uri;
                _user = parsed ? Entities.User(uri) : default;    // allocates the row: bind now, the demand fills it
            }
            _facet = facet;
            _shelf = ProfileListFacets.ShelfOf(facet);
            _surface = ProfileListFacets.SurfaceOf(facet);
        }

        // ── render ───────────────────────────────────────────────────────────────────────────────────────────────────────

        public override Element Render()
        {
            var p = UseProps<PageProps>();
            _scrollScope = UseContext(Shell.PageScrollScope);
            uint epoch = Entities.ScopeEpoch.Value;                    // FIRST: a scope switch re-points every table
            var scope = Entities.Current;
            _ = scope.Users.Changed.Value;                             // the head's name and counts
            bool parsed = ProfileListRoute.TryParse(p.Route, out var facet, out var uri);
            Bind(parsed, uri, facet);
            _debounced = UseDebouncedValue<string>(_query, 150f);
            var u = _user;
            UseEffect(_demand, DepKey.From(u.Slot, (int)facet, (int)epoch, 0));
            UseLayoutEffect(_syncFacet, DepKey.From(ProfileListFacets.IndexOf(facet)));
            UseSignalEffect(_sync);
            UseSignalEffect(_watchLetters);
            if (!parsed) return Controls.Vacancy(Controls.VacancyVoice.Error);

            float g = Shell.Ui.PageGutter.Value;
            Element head = Head(u, facet, g);
            Element region = new SkelRegionEl(
                Pending: _pendingFn, Failed: _failedFn, Content: _contentFn, ShimmerSource: _shimmerFn,
                OnFailed: _failedPanelFn, Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default,
                Group: null, SmoothResize: false);
            Element body = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                // The list owns its scroller and the shell clips above the player bar, so the trailing inset is Spacing.L of air:
                // the documented exception to PageGeometry.BottomReserve.
                Padding = new Edges4(g, 0f, g, Spacing.L),
                Children =
                [
                    // The measured box is INSIDE the gutters: its width is the list's, not the page's.
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                        OnBoundsChanged = _onBounds, Children = [region],
                    },
                ],
            };
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                Children = [head, body],
            };
        }

        // ── the head (always real) ───────────────────────────────────────────────────────────────────────────────────────

        Element Head(User u, ProfileFacet facet, float g)
        {
            bool valid = u.IsValid;
            string name = valid ? u.Name : "";
            if (name.Length == 0) name = Loc.Get(Strings.Person.List.FallbackName);
            string title = Loc.Get(facet == ProfileFacet.Followers ? Strings.Person.List.Title.Followers : Strings.Person.List.Title.Following);
            bool social = valid && u.Knows(UserFields.Social);
            var culture = CultureInfo.CurrentCulture;
            string[] labels =
            [
                ProfileListFilter.WordCount(Loc.Get(Strings.Person.Pivot.Following), valid ? u.Following : 0, social, culture),
                ProfileListFilter.WordCount(Loc.Get(Strings.Person.Pivot.Followers), valid ? u.Followers : 0, social, culture),
            ];
            var profile = valid ? ProfileRoute.For(u) : Shell.Route.None;
            // CrumbTitleViews (200) in every data state: a late name or count changes only text inside boxes that were
            // reserved from the first frame (the crumb's AboveLine, the views row's labels).
            return PageHead.Create(new PageHeadSpec(title)
            {
                Above = BreadcrumbBar.Create([name, title], i => { if (i == 0 && !profile.IsNone) Shell.GoTo(profile); }),
                Views = labels, ViewsSelected = _facetIndex, OnView = _onView,
                Gutter = g, Key = "profile-lists:head",
            });
        }

        /// <summary>A word chosen in the views bar: the ROUTE, not the control, is the truth. A change navigates and the layout
        /// effect re-seeds <see cref="_facetIndex"/> from the new route.</summary>
        void OnView(int index)
        {
            var target = ProfileListFacets.At(index);
            if (target == _facet) return;
            var u = _user;
            var route = u.IsValid ? ProfileListRoute.For(u, target) : Shell.Route.None;
            if (!route.IsNone) Shell.GoTo(route);
        }

        /// <summary>The route's facet moved (a click, back or forward): the bar follows (the pill slides) and the per-facet tools
        /// start clean, as a fresh page's did. Runs on mount too, where it writes the defaults it already holds.</summary>
        void SyncFacet()
        {
            _facetIndex.SetIfChanged(ProfileListFacets.IndexOf(_facet));
            _chip.SetIfChanged(ProfileChip.All);
            _query.SetIfChanged("");
        }

        // ── demand ───────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>THE page's demand, once per (user, facet, scope epoch): the planner path. A list page asks the profile row
        /// while it is unknown and its list while that is Unknown — the profile page already refreshed both on this visit.</summary>
        void Demand()
        {
            var u = _user;
            if (!u.IsValid) return;
            ProfileAsk.Apply(u, ProfileAsk.Plan(u, _surface));
        }

        /// <summary>Retry — Refresh, never Ensure (a sealed Asked row is a no-op for Ensure): the list, and the profile row
        /// too when IT is what failed.</summary>
        void Retry()
        {
            var u = _user;
            if (!u.IsValid) return;
            if (u.IsFailed(UserFields.Social)) ProfileAsk.RetryHeader(u);
            ProfileAsk.RetryList(u, _shelf);
        }

        // ── the derivation (a signal effect: the only writer of _shape, _present and the sticky pair) ────────────────────────

        /// <summary>Three stages, each skipped when its inputs did not move: (1) ENTRIES — re-read the target rows and re-sort
        /// when a table or the relation published; (2) FILTER — the chip and the (debounced) query; (3) ROWS — the letter
        /// projection when the filter or the grid plan moved. Then the face (the load rule) is decided every run: marks move
        /// without publishing a table, so <c>Fetch.Settled</c> wakes this too.</summary>
        void Sync()
        {
            _ = Entities.ScopeEpoch.Value;                             // FIRST: a scope switch re-points every table
            _ = _facetIndex.Value;                                     // a facet change under the mounted page re-derives
            var scope = Entities.Current;
            Bind(_parsed, _subject, _facet);
            var u = _user;
            bool valid = u.IsValid;

            uint uv = scope.Users.Changed.Value, av = scope.Artists.Changed.Value;
            uint ev = User.ProfileRelation(_shelf).Changed.Value;
            _ = global::Wavee.Fetch.Settled.Value;
            var chipRaw = _chip.Value;
            string query = _debounced?.Value ?? "";
            float bodyW = _bodyW.Value;

            var chip = ProfileListFacets.HasChips(_facet) ? chipRaw : ProfileChip.All;
            bool data = !ReferenceEquals(_dataScope, scope) || _dataSlot != u.Slot || _dataShelf != _shelf
                        || uv != _uv || av != _av || ev != _ev;
            if (data)
            {
                _dataScope = scope; _dataSlot = u.Slot; _dataShelf = _shelf; _uv = uv; _av = av; _ev = ev;
                BuildEntries(u);
            }

            bool filter = data || chip != _chipSeen || !string.Equals(query, _querySeen, StringComparison.Ordinal);
            if (filter)
            {
                _chipSeen = chip; _querySeen = query;
                _model.Filter(chip, query.AsSpan());
            }

            var plan = ProfileGridFit.For(bodyW, Spacing.Card);
            bool rows = filter || plan != _planSeen;
            if (rows)
            {
                _planSeen = plan;
                _epoch++;
                Letters.Build(_model.VisibleLetters, _model.VisibleArtists, plan.Columns, ProfileGridFit.CardRow(plan.CellWidth), _epoch);
                _mountKeyNow = string.Concat("people:", ((int)_facet).ToString(CultureInfo.InvariantCulture), ":",
                    plan.Columns.ToString(CultureInfo.InvariantCulture), ":", Letters.Key().ToString("x16", CultureInfo.InvariantCulture));
            }

            var facts = new ProfileListFacts(
                valid ? ProfileLoadRule.Header(u) : ProfileLoad.Unavailable,
                valid && u.Knows(UserFields.Social), valid && u.ShowFollows, valid && u.IsCurrentUser,
                valid ? u.ProfileReadiness(_shelf) : EdgeState.Unknown, valid ? u.ProfileFailure(_shelf) : 0, _model.Count);
            var shape = new ListShape(ProfileListLoadRule.Of(in facts), _facet, _model.Count, _model.VisibleCount, chip,
                _model.Counts, plan, Letters.FlatCount, _mountKeyNow, _epoch);
            ShapeNow = shape;
            _shape.Value = shape;

            _present.SetIfChanged(Letters.Present);
            int st = _sticky.Peek();
            if (st >= 0 && !Letters.Has(st)) { _sticky.SetIfChanged(-1); _push.SetIfChanged(0f); }
            if (rows)
            {
                // A re-projected list moves every band: the pinned letter is re-resolved against the live offset.
                _lettersKey = long.MinValue;
                UpdateLetters((float)_handle.Offset.Peek());
            }
        }

        /// <summary>Read the relation's rows into the pooled model: the target's NAME comes off its own row (Artists / Users —
        /// the list answer stages it Thin), the follower count off the edge payload.</summary>
        void BuildEntries(User u)
        {
            _model.Begin();
            if (u.IsValid)
            {
                var targets = u.ProfileTargets(_shelf);
                var cards = u.ProfileCards(_shelf);
                int n = Math.Min(targets.Length, cards.Length);
                for (int i = 0; i < n; i++)
                {
                    int slot = targets[i];
                    var card = cards[i];
                    if (card.Kind == EntityKind.Artist)
                    {
                        var a = new Artist(slot);
                        if (a.IsValid) _model.Add(new ProfileEntry(EntityKind.Artist, slot, a.Name, card.Followers));
                    }
                    else if (card.Kind == EntityKind.User)
                    {
                        var person = new User(slot);
                        if (!person.IsValid) continue;
                        string name = person.Name;
                        if (name.Length == 0) name = ProfileEntryName.OrId(name, person.Uri.Text);
                        _model.Add(new ProfileEntry(EntityKind.User, slot, name, card.Followers));
                    }
                }
            }
            _model.Seal();
        }

        // ── the letters: the sticky overlay, the jump strip (User.Page.Library's WatchLetters, over our projection) ──────────

        /// <summary>The sticky letter's watch over the list's scroll handle: it re-runs on every published offset (and extent)
        /// and writes the two signals only when the coarse key moves.</summary>
        void WatchLetters()
        {
            float offsetY = (float)_handle.Offset.Value;
            _ = _handle.ExtentSignal.Value;            // a measured-extent correction re-projects too
            long key = ProjectLetters(offsetY);
            if (key == _lettersKey) return;
            _lettersKey = key;
            UpdateLetters(offsetY);
        }

        /// <summary>The coarse key: the pinned letter plus its push quantized to 2 DIP — two signals per key CHANGE, never
        /// per frame.</summary>
        long ProjectLetters(float offsetY)
        {
            int letter = Letters.StickyLetterAt(offsetY);
            int quantized = letter < 0 ? 0 : (int)MathF.Round(PushOf(offsetY, letter) / Spacing.XXS);
            return ((long)(letter + 1) << 16) | (uint)(ushort)(quantized + 32768);
        }

        void UpdateLetters(float offsetY)
        {
            int letter = Letters.StickyLetterAt(offsetY);
            _sticky.SetIfChanged(letter);
            _push.SetIfChanged(letter < 0 ? 0f : MathF.Round(PushOf(offsetY, letter) / Spacing.XXS) * Spacing.XXS);
        }

        /// <summary>The next header's top minus the viewport top minus the overlay's height, clamped ≤ 0: the pinned letter is
        /// pushed up by the one arriving under it.</summary>
        float PushOf(float offsetY, int letter)
        {
            for (int l = letter + 1; l < LibraryLetters.Count; l++)
            {
                int next = Letters.HeaderFlat(l);
                if (next >= 0) return MathF.Min(0f, Letters.OffsetOf(next) - offsetY - ProfileLetterRows.HeaderExtent);
            }
            return 0f;
        }

        /// <summary>A strip tap — UNANIMATED (a jump is a jump). A letter with no cards resolves to -1 and the tap NO-OPS.</summary>
        void Jump(int letter)
        {
            int flat = Letters.HeaderFlat(letter);
            if (flat >= 0) _ctl.StartBringItemIntoView(flat, alignmentRatio: 0f);
        }

        // ── the region's faces ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Content (everything but Pending / Failed). Every face is a KEYED CHILD of one stable root (rule 14: a key on
        /// a single-child slot's root is inert), so a face change remounts instead of morphing one tree into another.</summary>
        Element Content()
        {
            var s = _shape.Value;
            Element face = s.Load switch
            {
                ProfileListLoad.Ready => ReadyFace(s),
                ProfileListLoad.Empty => Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page,
                    Loc.Get(s.Facet == ProfileFacet.Followers ? Strings.Person.List.Empty.Followers : Strings.Person.List.Empty.Following), ""),
                ProfileListLoad.Hidden => Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page,
                    Loc.Get(Strings.Person.List.Hidden.Title), Strings.Person.List.Hidden.Subtitle(DisplayName())),
                ProfileListLoad.Unavailable => Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page,
                    Loc.Get(Strings.Person.List.Unavailable.Title), Loc.Get(Strings.Person.List.Unavailable.Subtitle)),
                _ => new BoxEl(),
            };
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
                Children = [face with { Key = s_faceKeys[(int)s.Load] }],
            };
        }

        string DisplayName()
        {
            var u = _user;
            string name = u.IsValid ? u.Name : "";
            return name.Length > 0 ? name : Loc.Get(Strings.Person.List.FallbackName);
        }

        Element ReadyFace(ListShape s)
        {
            Element body = s.Visible == 0
                ? Controls.Vacancy(Controls.VacancyVoice.NoMatch) with { Key = "people:nomatch" }
                : ListBody(s);
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
                Children = [Tools(s) with { Key = "people:tools" }, body],
            };
        }

        /// <summary>The tools: Following has the All / Artists / People chips and the find box, Followers the find box alone.
        /// Wide, chips sit left and find right; below <see cref="ProfileGridFit.ToolsStackBelow"/> they stack. They stay mounted
        /// when a query narrows the list to nothing, so the query can be cleared.</summary>
        Element Tools(ListShape s)
        {
            string placeholder = Loc.Get(s.Facet == ProfileFacet.Followers ? Strings.Person.List.Find.Followers : Strings.Person.List.Find.Following);
            Element find = Controls.FindBox(_query, placeholder) with { Key = "people:find" };
            bool stack = s.Plan.StackTools;
            var kids = new List<Element>(2);
            if (ProfileListFacets.HasChips(s.Facet))
            {
                var c = s.Counts;
                var culture = CultureInfo.CurrentCulture;
                kids.Add(new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, Shrink = 0f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Chip(ProfileChip.All, Strings.Person.List.Chip.All, c.All, s.Chip, culture),
                        Chip(ProfileChip.Artists, Strings.Person.List.Chip.Artists, c.Artists, s.Chip, culture),
                        Chip(ProfileChip.People, Strings.Person.List.Chip.People, c.People, s.Chip, culture),
                    ],
                });
            }
            kids.Add(find);
            return new BoxEl
            {
                Direction = stack ? (byte)1 : (byte)0, Gap = stack ? Spacing.S : Spacing.M, MinWidth = 0f, Shrink = 0f,
                AlignItems = stack ? FlexAlign.Start : FlexAlign.Center,
                Justify = kids.Count > 1 ? FlexJustify.SpaceBetween : FlexJustify.End,
                Padding = new Edges4(0f, 0f, 0f, Spacing.XS),
                Children = kids.ToArray(),
            };
        }

        Element Chip(ProfileChip chip, string wordKey, int count, ProfileChip selected, CultureInfo culture)
            => Controls.Chip(ProfileListFilter.WordCount(Loc.Get(wordKey), count, true, culture), selected == chip, count > 0,
                             _chipClicks[(int)chip]);

        /// <summary>The list: the bound view (a keyed child, remounted when its geometry changes) with the sticky letter over
        /// it, and the A–Z strip beside it when the body is wide enough. Rows scroll UNDER the pinned letter: clipped at exactly
        /// its height with a feather.</summary>
        Element ListBody(ListShape s)
        {
            EnsureMount(s.MountKey);
            Element list = new BoxEl
            {
                Key = s.MountKey, Grow = 1f, Direction = 1, MinHeight = 0f,
                Children = [ItemsView.CreateBound(_rows, _rowT, _layout, _options!)],
            };
            Element zstack = new BoxEl
            {
                Key = "people:body", Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
                Children = [list, _stickyEl ??= User.StickyLetter(_sticky, _push)],
            };
            return new BoxEl
            {
                Key = "people:list", Direction = 0, Gap = ProfileGridFit.StripGap, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                AlignItems = FlexAlign.Stretch,
                Children = s.Plan.Strip ? [zstack, _stripEl ??= User.JumpStrip(_present, _sticky, _jump)] : [zstack],
            };
        }

        /// <summary>The (layout, options) pair for one mount key, built once and reused while the key does not change. The
        /// options are FROZEN at mount, so everything per-mount (the scroll restore key) is decided here.</summary>
        void EnsureMount(string key)
        {
            if (_options is not null && _mountKey == key) return;
            _mountKey = key;
            _layout = RepeatLayout.Extents(_extentOf, ProfileGridFit.CardRow(ProfileGridFit.MinCell));
            _options = new ListOptions<ProfileRowItem>
            {
                SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None, IsItemInvokedEnabled = false,
                Controller = _ctl, Grow = 1f,
                // A header and a row never share a recycle pool, and a header is never an arrow-key / typeahead stop.
                ContentType = _contentType,
                IsItemEnabledTyped = static (_, it) => !it.IsHeader,
                Scroll = new ScrollOptions
                {
                    Handle = _handle, ScrollKey = _scrollScope + key,
                    ItemClipTopInset = ProfileLetterRows.HeaderExtent, ItemClipTopFadeBand = Detail.VerticalLayout.StickyFadeBand,
                },
            };
        }

        // ── the shimmer SOURCE: tool bones + two rows of circular seed cards ─────────────────────────────────────────────────

        /// <summary>Derived once per pending→loaded edge; stateful leaves (the Follow toggle, the find box, the list) never
        /// mount in it. The tools are childless declared-size boxes (the deriver turns them into bars); each cell is the
        /// surface's own SEED face (already bones — handed to the deriver as-is, because derived, its aspect-only cover would
        /// collapse) over a Follow bone on every other cell of Following.</summary>
        Element Shimmer()
        {
            bool chips = ProfileListFacets.HasChips(_facet);
            var plan = ProfileGridFit.For(_bodyW.Peek(), Spacing.Card);
            int cols = plan.Columns > 0 ? plan.Columns : 4;

            var toolKids = new List<Element>(2);
            if (chips)
                toolKids.Add(new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, Shrink = 0f,
                    Children =
                    [
                        new BoxEl { Width = 96f, Height = Controls.ChipHeight, Shrink = 0f, Corners = Radii.PillAll },
                        new BoxEl { Width = 96f, Height = Controls.ChipHeight, Shrink = 0f, Corners = Radii.PillAll },
                        new BoxEl { Width = 96f, Height = Controls.ChipHeight, Shrink = 0f, Corners = Radii.PillAll },
                    ],
                });
            toolKids.Add(new BoxEl { Width = Controls.FindBoxWidth, Height = 32f, Shrink = 0f, Corners = Radii.ControlAll });
            Element tools = new BoxEl
            {
                Direction = plan.StackTools ? (byte)1 : (byte)0, Gap = plan.StackTools ? Spacing.S : Spacing.M, MinWidth = 0f,
                AlignItems = plan.StackTools ? FlexAlign.Start : FlexAlign.Center,
                Justify = toolKids.Count > 1 ? FlexJustify.SpaceBetween : FlexJustify.End,
                Padding = new Edges4(0f, 0f, 0f, Spacing.XS),
                Children = toolKids.ToArray(),
            };

            var rowsEl = new Element[2];
            for (int r = 0; r < rowsEl.Length; r++)
            {
                var cells = new Element[cols];
                for (int c = 0; c < cols; c++)
                {
                    bool toggle = chips && (r + c) % 2 == 0;
                    Element cell = new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children = toggle
                            ? [SurfaceParts.Seed(Shape.Grid, float.NaN, circular: true), FollowBone()]
                            : [SurfaceParts.Seed(Shape.Grid, float.NaN, circular: true)],
                    };
                    cells[c] = cell.Skel(cell);
                }
                rowsEl[r] = new BoxEl { Direction = 0, Gap = Spacing.Card, AlignItems = FlexAlign.Start, Children = cells };
            }
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
                Children = [tools, new BoxEl { Direction = 1, Gap = Spacing.Card, MinWidth = 0f, Children = rowsEl }],
            };
        }

        /// <summary>A Follow toggle's bone, in its action line (the verbatim seed is not derived, so the bone is explicit).</summary>
        static Element FollowBone() => new BoxEl
        {
            Height = ProfileGridFit.ActionLine, Shrink = 0f, Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Start,
            Padding = new Edges4(0f, Spacing.S, 0f, 0f), IsEnabled = false,
            Children = [new BoxEl { Width = 96f, Height = 32f, Shrink = 0f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary }],
        };

        // ── the slot: one flat item → a letter header, or a pinned-height row of cards ───────────────────────────────────────

        internal Element BuildSlot(in ProfileRowItem it, int index, IOverlayService? overlay)
        {
            // An out-of-range slot (a shrink in flight) resolves to the default item: draw nothing, not a "#".
            if (it.Epoch == 0u) return new BoxEl();
            if (it.IsHeader)
                return new BoxEl
                {
                    Height = ProfileLetterRows.HeaderExtent, Direction = 0, AlignItems = FlexAlign.End, HitTestVisible = false,
                    Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
                    Children = [User.LetterPlate(User.LetterText(it.Letter))],
                };
            var s = ShapeNow;
            // A stale item (the filter shrank under a slot that has not rebound yet) never reads past the visible set.
            int n = Math.Min(it.Count, _model.VisibleCount - it.Start);
            if (n <= 0) return new BoxEl();
            var cells = new Element[n];
            for (int k = 0; k < cells.Length; k++)
            {
                var e = _model.VisibleAt(it.Start + k);
                cells[k] = Cell(s, in e, overlay);
            }
            // PINNED to the projection's extent: the seed the layout used and the measured height are the same number.
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.Card, AlignItems = FlexAlign.Start, Height = Letters.ExtentOf(index), Shrink = 0f,
                Children = cells,
            };
        }

        /// <summary>One cell: a fixed-width column holding the surface card, and — for an artist in Following — its Follow toggle
        /// on a 40-DIP centred line. The toggle's uri freezes at mount, so it is keyed on it.</summary>
        Element Cell(ListShape s, in ProfileEntry e, IOverlayService? overlay)
        {
            float w = s.Plan.CellWidth;
            string name = e.Name;
            if (e.IsArtist)
            {
                var a = new Artist(e.Slot);
                string uri = a.Uri.Text;
                var hc = new HomeCard(new EntityRef(EntityKind.Artist, e.Slot));
                var menu = HomeCardNav.MenuOf(in hc);
                bool hasMenu = menu is not null && !Controls.IsNullOverlay(overlay);
                string sub = e.Followers > 0 ? Profile.FollowerCount(Strings.Person.List.Card.ArtistFollowersKey, e.Followers) : Loc.Get(Strings.Person.List.Card.Artist);
                var data = new Controls.CardData(uri, name, Sub(sub), Controls.ArtUrl(a.ImageId),
                    () => HomeCardNav.Open(in hc, null), () => HomeCardNav.Play(in hc),
                    Circular: true, Drag: HomeCardNav.DragOf(in hc), ShowMenu: hasMenu) { Menu = hasMenu ? menu : null };
                Element card = Controls.Surface(data, Shape.Grid) with { Key = "card:" + uri };
                Element[] kids = s.Facet == ProfileFacet.Following
                    ? [card, FollowLine(uri, name)]
                    : [card];
                return new BoxEl { Key = "cell:" + uri, Width = w, Shrink = 0f, Direction = 1, Children = kids };
            }

            var person = new User(e.Slot);
            string puri = person.Uri.Text;
            string? art = person.Image;
            var route = ProfileRoute.For(person);
            string psub = e.Followers > 0 ? Profile.FollowerCount(Strings.Person.List.Card.FollowersKey, e.Followers) : Loc.Get(Strings.Person.List.Card.Profile);
            // No avatar: the initials in the card's own inner width (its 8-DIP plate padding either side), not a blank hole.
            Element? cover = art is null
                ? PersonPicture.Create("", MathF.Max(1f, w - 2f * SurfaceGeometry.ShelfPlatePad), displayName: name)
                : null;
            var pdata = new Controls.CardData(puri, name, Sub(psub), art,
                () => { if (!route.IsNone) Shell.GoTo(route); }, null, Circular: true, ShowMenu: false) { CoverOverride = cover };
            Element pcard = Controls.Surface(pdata, Shape.Grid) with { Key = "card:" + puri };
            return new BoxEl { Key = "cell:" + puri, Width = w, Shrink = 0f, Direction = 1, Children = [pcard] };
        }

        static Element FollowLine(string uri, string name) => new BoxEl
        {
            Height = ProfileGridFit.ActionLine, Shrink = 0f, Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Start,
            Padding = new Edges4(0f, Spacing.S, 0f, 0f),
            Children =
            [
                Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name })
                    with { Key = "follow:" + uri, SkeletonProxy = s_followShape },
            ],
        };

        static Element Sub(string text) => Design.Type.TrackMeta(text) with
            { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
    }

    /// <summary>A BOUND slot: the template runs once per recycled slot and rebinds through the scope's item signal. It
    /// subscribes to exactly what it paints — the bound item (an epoch bump re-skins it when data lands or the width moves)
    /// and its index (the pinned extent).</summary>
    sealed class RowSlot(PageHost page, BoundItemScope<ProfileRowItem> scope) : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var it = scope.Item.Value;
            int index = scope.Index.Value;
            return page.BuildSlot(in it, index, overlay);
        }
    }
}
