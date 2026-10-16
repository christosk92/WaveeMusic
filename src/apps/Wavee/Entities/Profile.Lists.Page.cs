// ── Entities/Profile.Lists.Page.cs — the Following / Followers list pages (people:<facet>:<user uri>) ───────────────────
//
// Role: UI
// Owner: R2 (profile pages, issue #161)
// Spec: docs/plans/wavee/profile-pages-implementation.md Appendix R §4.2/§5 + "Reconciliation, round 2"; the pure half is
//       Profile.Lists.cs (load rule, entries + filter, grid fit, letter rows).
//
// ── THE SHAPE ────────────────────────────────────────────────────────────────────────────────────────────────────────
//
// T10c2: the structure draws the page (`Themes\Classic\Structure\Views\Pages\ClassicProfileList*.cs`, key `Pages.ProfileList`). This host (a `[CoreHost]`) keeps what decides.
//
//  PageHost (keyed "people-page:" + route key)  →  Slots.View(ViewKeys.ProfileListPage, ProfileListProps)
//  view:  column
//     ├─ HEAD (always real, outside the region): BreadcrumbBar [name › facet] · PageHero(facet) · SelectorBar (a change NAVIGATES —
//     │        the route is the truth, not the control; Discography's head)
//     └─ BODY (the page gutters) └─ measured box (_bodyW, floored to 8 DIP) └─ ONE SkelRegionEl (Soft; no outer ScrollView —
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
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class ProfileLists
{
    /// <summary>The route's page: <c>people:&lt;facet&gt;:&lt;user uri&gt;</c>. Keyed by the whole route key, so another user or
    /// another facet is a fresh page. Registered by <c>Profile.InstallPages()</c> for <c>RouteKind.ProfileList</c>.</summary>
    // MOUNT POINT (profile pages: Shell lazy group {User, ProfileList})
    public static Element Page(in Shell.Route route)
    {
        string key = Shell.NameOf(route);
        return Embed.Comp(new PageProps(route, key), static () => new PageHost()) with { Key = "people-page:" + key };
    }

    sealed record PageProps(Shell.Route Route, string Key);

    [CoreHost]
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
        readonly Signal<ProfileListShape> _shape;
        readonly Signal<ProfileChip> _chip = new(ProfileChip.All);
        readonly Signal<string> _query = new("");
        /// <summary>The body's measured width, floored to 8 DIP (never wider than the box, so a card never overhangs).</summary>
        readonly Signal<float> _bodyW = new(0f);
        /// <summary>The list's ARM on the structure's list ladder (<see cref="Theming.ArtistLadders.ProfileList"/>, TC6) over <see cref="_bodyW"/>: 1 shows the A–Z
        /// strip and lays the tools side by side, 0 stacks them. Seeded from the content region before the first measure.</summary>
        IReadSignal<int>? _listArm;
        readonly Func<float> _bodySeed;
        /// <summary>The structure's narrowest card for the tier on screen, as the last <see cref="Sync"/> read it (the mount's seed extent reads it too).</summary>
        float _minCell = Theming.ArtistTierPolicy.Default.ProfileListMinCell(default);
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

        // ── cached delegates (a node handler never captures a render's closure) ──
        readonly Func<bool> _pendingFn, _failedFn;
        readonly Action _demand, _sync, _watchLetters, _retry;
        readonly Action<int> _jump, _pickFacet;
        readonly Action<RectF> _onBounds;
        readonly Func<int, float> _extentOf;
        readonly Func<int, int> _contentType;
        readonly Action<ProfileChip> _selectChip;
        readonly Func<Signal<string>> _queryCell;
        readonly Func<string, ProfileListMount> _mount;
        readonly Func<ProfileFacet> _facetNow;
        readonly Func<float> _bodyWidthNow;
        readonly Func<string> _displayName;
        readonly Func<int> _visibleCount;
        readonly Func<int, ProfileEntry> _visibleAt;

        public PageHost()
        {
            // The per-artist Follow toggle writes through this seam (Show / Playlist page precedent).
            Controls.Library ??= User.LibrarySeam;

            _shape = new Signal<ProfileListShape>(ProfileListShape.Pending);
            _rows = BoundItems.Project(_shape, static s => s.Rows, (_, i) => Letters.Item(i), default(ProfileRowItem));

            _demand = Demand;
            _sync = Sync;
            _watchLetters = WatchLetters;
            _retry = Retry;
            _jump = Jump;
            _pickFacet = PickFacet;
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
            _extentOf = flat => Letters.ExtentOf(flat);
            _contentType = i => Letters.IsHeader(i) ? 1 : 0;
            _selectChip = chip => _chip.Value = chip;
            _queryCell = () => _query;
            _mount = key => { EnsureMount(key); return new ProfileListMount(_layout, _options!); };
            _facetNow = () => _facet;
            _bodyWidthNow = () => _bodyW.Peek();
            _bodySeed = () => Theming.ThemeHost.Facts.Peek().Tiers.ContentWidth is var w && w > 0f ? w : float.MaxValue;
            _displayName = DisplayName;
            _visibleCount = () => _model.VisibleCount;
            _visibleAt = k => _model.VisibleAt(k);
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
            _listArm = UseTier(Theming.ArtistLadders.Of(ShellPolicy.Now.Tiers, Theming.ArtistLadders.ProfileList), TierSource.Measured(_bodyW, _bodySeed));
            UseSignalEffect(_sync);
            UseSignalEffect(_watchLetters);

            // The structure draws the page (T10c2): this host keeps every decision and hands the view the values and verbs it draws.
            return global::FluentGpu.Theming.Slots.View(ViewKeys.ProfileListPage, new ProfileListProps
            {
                Parsed = parsed, Head = Head(u, facet),
                Pending = _pendingFn, Failed = _failedFn, Retry = _retry,
                Shape = _shape, Facet = _facetNow, BodyWidth = _bodyWidthNow, OnBodyBounds = _onBounds, DisplayName = _displayName,
                Query = _queryCell, SelectChip = _selectChip, Mount = _mount, Rows = _rows,
                StickyLetter = _sticky, StickyPush = _push, Present = _present, Jump = _jump,
                ExtentOf = _extentOf, VisibleCount = _visibleCount, VisibleAt = _visibleAt,
            });
        }

        // ── the head's words and navigation ──────────────────────────────────────────────────────────────────────────────

        ProfileListHead Head(User u, ProfileFacet facet)
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
            return new ProfileListHead(name, title, facet, labels,
                profile.IsNone ? null : () => Shell.GoTo(profile), _pickFacet);
        }

        /// <summary>A facet word was picked: the ROUTE, not the control, is the truth, so this navigates (and a pick of the current facet does nothing).</summary>
        void PickFacet(int index)
        {
            var target = ProfileListFacets.At(index);
            if (target == _facet) return;
            var u = _user;
            var route = u.IsValid ? ProfileListRoute.For(u, target) : Shell.Route.None;
            if (!route.IsNone) Shell.GoTo(route);
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

            // The structure's arm and narrowest card (tracked: a list-arm or a window crossing re-runs this derivation).
            bool wideArm = (_listArm?.Value ?? 1) > 0;
            _minCell = ShellPolicy.Tracked.ArtistTiers.ProfileListMinCell(Theming.ThemeHost.Facts.Value.Tiers);
            var plan = ProfileGridFit.For(bodyW, Spacing.Card, wideArm, _minCell);
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
            var shape = new ProfileListShape(ProfileListLoadRule.Of(in facts), _facet, _model.Count, _model.VisibleCount, chip,
                _model.Counts, plan, Letters.FlatCount, _mountKeyNow, _epoch);
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

        string DisplayName()
        {
            var u = _user;
            string name = u.IsValid ? u.Name : "";
            return name.Length > 0 ? name : Loc.Get(Strings.Person.List.FallbackName);
        }

        /// <summary>The (layout, options) pair for one mount key, built once and reused while the key does not change. The
        /// options are FROZEN at mount, so everything per-mount (the scroll restore key) is decided here.</summary>
        void EnsureMount(string key)
        {
            if (_options is not null && _mountKey == key) return;
            _mountKey = key;
            _layout = RepeatLayout.Extents(_extentOf, ProfileGridFit.CardRow(_minCell));
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
    }
}
