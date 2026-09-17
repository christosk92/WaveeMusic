// ── Entities/User.Page.Liked.cs ────────────────────────────────────────────────────────────────────────────────────
// the liked page's own composition of the shared detail frame: LikedPageFor + LikedPage (identity, the dynamic cover
// slots, the facts slot, the demand rule), the chip rail (TableProfile.ContentFilterBar) and the lens header's placement
// (TableProfile.LensHeader) — configured only through FrameSpec / TableProfile, never by editing Detail* / Track*
//
// Role: UI
// Owner: O
// Wave: 5
// Budget: 700 lines
// Spec: ch 07 §1.1-§1.2 (the tree), §7 (data & readiness, the demand rule), §6.2 (chips); ch 03 items 23/32/41;
//       ch 04 items 32-34; ch 31 §7.1 rows 8/24/25
//
// ── HOW DATA REACHES THIS PAGE ───────────────────────────────────────────────────────────────────────────────────────
//
// The page subscribes to the scope epoch FIRST, then the liked edge, the track table and the user table (the GateAlbum
// pattern), rebuilds its identity VALUE each render and hands the frame one FrameSpec — whose data equality means an equal
// model costs the frame nothing. Everything that changes faster than the page lives in the child that paints it: the
// chips read a page-owned computed memo (its equality gates on the chip TITLES and the evidence split), the lens header
// and the facts panel share the page-owned LensCell bridge. The two extents the table reserves (chips 48, lens 36) are
// reported through the profile from two equality-gated memos, so they flip exactly when the painted rows do (W19: no
// chip bar and no lens header when there is nothing to say).
//
// The page never asks "am I fake": it demands the liked edge, the row bundle and the curated chip set through Fetch on
// mount, and --fake answers them because the seed already committed them.

using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public readonly partial struct User
{
    /// <summary>RouteKind.Liked. ONE <c>Embed.Comp(props, factory)</c> keyed by <c>Shell.NameOf(route)</c> (the
    /// <c>Detail.GateAlbum</c> pattern).</summary>
    public static Element LikedPageFor(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        return Embed.Comp(new LikedPageProps(routeKey), static () => new LikedPage()) with { Key = "liked-page:" + routeKey };
    }

    sealed record LikedPageProps(string RouteKey);

    /// <summary>The web link the rail's Share FAB copies (the collection has no ShareUrl column behind it).</summary>
    const string LikedShareUrl = "https://open.spotify.com/collection/tracks";

    /// <summary>ch 07 §7's demand bundle for every liked row: the row the table paints (Identity — credits included — +
    /// plays + availability), the release year (years card), kind 222 (tempo card, BPM · Key lane), kind 6 (blend, chips)
    /// and the film lane.</summary>
    const TrackFields LikedRowBundle = TrackFields.Row | TrackFields.Year | TrackFields.Audio | TrackFields.Tags | TrackFields.Video;

    sealed class LikedPage : Component
    {
        readonly LensCell _lens = new();
        Scope? _scope;
        User _me;
        Track.TableSource? _source;
        EntityUri _subject;
        string _routeKey = "";
        string _chipScrollKey = "";

        IReadSignal<LikedChipModel>? _chips;
        readonly Track.TableProfile?[] _profiles = new Track.TableProfile?[4];
        readonly Detail.FrameActions _actions;
        readonly Detail.FrameSlots _slotsWithFacts, _slotsWithoutFacts;

        readonly Action _demandModel;
        readonly Action _demandRows;
        readonly Func<bool> _lensActive;
        readonly Func<LikedChipModel> _computeChips;
        readonly Func<Element?> _chipBar;
        readonly Func<Element?> _lensHeader;

        public LikedPage()
        {
            _demandModel = DemandModel;
            _demandRows = DemandRows;
            _lensActive = () => LensActive(_lens);
            _computeChips = ComputeChips;
            _chipBar = () => _chips is { } chips
                ? Embed.Comp(new LikedChipBarProps(chips, _chipScrollKey), static () => new LikedChipBar()) with { Key = "liked-chips" }
                : null;
            _lensHeader = () => LensHeader(_lens, DetailKind.Liked);

            // The cover slots: the dynamic treatment WITH its style picker on the rail and the vertical hero, the flat
            // mosaic / stock with NO picker on the 96-DIP strip (ch 03 item 23, ch 07 §1.1 mount table).
            Func<float, Element> cover = static size => LikedCover(size, Radii.Card, picker: true);
            Func<float, Element> compactCover = static size => LikedCover(size, Radii.Card, picker: false);
            Func<float, Element> facts = width => _source is { } src ? FactsPanel(src, DetailKind.Liked, _lens, width, false) : new BoxEl();
            _slotsWithFacts = new Detail.FrameSlots { Cover = cover, CompactCover = compactCover, LikedFacts = facts };
            _slotsWithoutFacts = new Detail.FrameSlots { Cover = cover, CompactCover = compactCover };
            _actions = new Detail.FrameActions { Shuffle = Shuffle, CoverDrag = CoverDragPayload };
        }

        public override Element Render()
        {
            var p = UseProps<LikedPageProps>();
            uint scopeEpoch = Entities.ScopeEpoch.Value;          // FIRST: a scope switch re-points every table below
            var scope = Entities.Current;
            _ = scope.Edges.Liked.Changed.Value;
            _ = scope.Tracks.Changed.Value;                        // durations (meta), years (FactsHas), the tone tiles
            _ = scope.Edges.TrackArtists.Changed.Value;            // credits (FactsHas)
            _ = scope.Users.Changed.Value;

            if (!ReferenceEquals(_scope, scope) || _me.Slot != scope.MeSlot)
            {
                _scope = scope;
                _me = Me;
                _source = Track.TableSource.ForLiked(_me);
                _subject = EntityUri.Parse(EntityUri.LikedCollection);   // interns once per scope (UI thread, C1)
            }
            if (!string.Equals(_routeKey, p.RouteKey, StringComparison.Ordinal))
            {
                _routeKey = p.RouteKey;
                _chipScrollKey = "contentfilter:" + p.RouteKey;
            }

            UseEffect(_demandModel, DepKey.From(_me.Slot, (int)scopeEpoch));   // once per account per scope
            UseEffect(_demandRows);                                              // re-runs as the membership lands

            var chips = UseComputed(_computeChips);
            _chips = chips;
            var lensOn = UseComputed(_lensActive);

            if (!_me.IsValid || _source is null) return Controls.Vacancy(Controls.VacancyVoice.Empty);

            bool chipsPresent = chips.Value.Titles.Length > 0;
            bool lensPresent = lensOn.Value;
            var source = _source;

            return Detail.Frame(new Detail.FrameSpec
            {
                Identity = IdentityNow(),
                Config = Detail.Config.Liked,
                Source = source,
                Profile = ProfileFor(chipsPresent, lensPresent),
                Actions = _actions,
                // The facts row is ADDED only when a card would mount (W23 e: "not an empty panel").
                Slots = FactsHas(source, DetailKind.Liked) ? _slotsWithFacts : _slotsWithoutFacts,
                RouteKey = p.RouteKey,
            });
        }

        /// <summary>The identity VALUE. The cover url is NULL on purpose — a provider cover would turn the dynamic
        /// treatment off (ch 07 §1.1 "the router is m.Cover is null"); the page tone follows the LEAD tile when a treatment
        /// composes (ch 07 §0.12). The meta line drops its duration while any row is thin or the list is incomplete.</summary>
        Detail.Identity IdentityNow()
        {
            var liked = Entities.Current.Edges.Liked;
            int slot = _me.Slot;
            var state = liked.State(slot);
            var rows = liked.Targets(slot);
            long totalMs = 0;
            bool durationsKnown = state == EdgeState.Complete;
            for (int i = 0; i < rows.Length; i++)
            {
                var t = new Track(rows[i]);
                totalMs += t.DurationMs;
                if (!t.Knows(TrackFields.Duration)) durationsKnown = false;
            }
            bool metaLoading = state == EdgeState.Unknown && rows.Length == 0;
            return new Detail.Identity
            {
                Subject = _subject,
                Kind = DetailKind.Liked,
                Title = Loc.Get(Strings.Detail.LikedSongs),
                CoverUrl = null,
                PaletteUrl = LikedToneAnchorUrl(),
                Eyebrow = Detail.Text.Eyebrow(DetailKind.Liked, BadgeStyle.None, AlbumKind.Album, 0,
                                              collaborative: false, isPublic: true, visibilityKnown: true),
                Meta = metaLoading ? null : Detail.Text.LikedMeta(liked.Total(slot), totalMs, durationsKnown),
                MetaLoading = metaLoading,
                ShareUrl = LikedShareUrl,
            };
        }

        /// <summary>Four profiles at most — (chip rail present) × (lens on) — built once each, so the table's props gate
        /// sees the SAME instance while nothing changed.</summary>
        Track.TableProfile ProfileFor(bool chips, bool lens)
        {
            int index = (chips ? 1 : 0) | (lens ? 2 : 0);
            return _profiles[index] ??= Track.TableProfile.From(Detail.Config.Liked) with
            {
                ContentFilterBar = _chipBar,
                ContentFilterExtent = chips ? Controls.ChipRailExtent : 0f,
                LensHeader = _lensHeader,
                LensExtent = lens ? LensExtent : 0f,
            };
        }

        // ── demand (effects, never render) ──

        void DemandModel()
        {
            var me = _me;
            if (!me.IsValid) return;
            // The whole liked list, paged by Fetch, never by the UI — and only while nobody has answered it.
            if (Entities.Current.Edges.Liked.State(me.Slot) == EdgeState.Unknown)
                Entities.EnsureEdge(FetchEdge.Liked, me.Slot);
            // The curated chip set (G4/G10): an empty answer is a publish that hands the bar to the derived fallback.
            Entities.Ensure(me, UserFields.ContentFilters);
        }

        void DemandRows()
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Edges.Liked.Changed.Value;
            var me = _me;
            if (!me.IsValid) return;
            var slots = me.LikedTrackSlots;
            // The WHOLE model once — the planner pages it at 300 uris per POST (ch 07 §7); never a visible window.
            if (slots.Length > 0) Entities.Ensure(MemoryMarshal.Cast<int, Track>(slots), LikedRowBundle);
        }

        // ── the chip set (a computed memo: equality-gated on titles + the evidence split) ──

        LikedChipModel ComputeChips()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Users.Changed.Value;
            _ = scope.Edges.Liked.Changed.Value;
            _ = scope.Edges.TrackTags.Changed.Value;
            _ = scope.Tracks.Changed.Value;          // the Tags known bit rides the track row
            var me = Me;
            if (!me.IsValid) return LikedChipModel.None;

            var titles = me.ContentFilterTitles;
            var tokens = me.ContentFilterTokens;
            int n = Math.Min(titles.Length, tokens.Length);
            var curated = new ContentFilterChip[n];
            for (int i = 0; i < n; i++)
                curated[i] = new ContentFilterChip(Entities.Strings.Resolve(titles[i]), Entities.Strings.Resolve(tokens[i]));

            var set = LikedFactsRules.ChipSet(me.Knows(UserFields.ContentFilters), curated,
                                              MemoryMarshal.Cast<int, Track>(me.LikedTrackSlots));
            if (set.Count == 0) return LikedChipModel.None;
            var names = new string[set.Count];
            for (int i = 0; i < names.Length; i++) names[i] = set.Titles[i];
            return new LikedChipModel(names, set.EvidencedCount);
        }

        // ── the page verbs ──

        void Shuffle()
        {
            if (!_subject.IsValid) return;
            Playback.SetShuffle(true);
            Actions.Services.Play?.Invoke(_subject);
        }

        /// <summary>The whole collection as a drag (0.2.9 <c>WaveeDetailDrag.Hero</c>): built at promotion, cold.</summary>
        object? CoverDragPayload()
        {
            var me = _me;
            if (!me.IsValid) return null;
            var tracks = MemoryMarshal.Cast<int, Track>(me.LikedTrackSlots).ToArray();
            return new DragPayload(DragKind.Route, "liked", EntityUri.LikedCollection, Loc.Get(Strings.Detail.LikedSongs),
                                   Tracks: tracks);
        }
    }

    // ══ THE CHIP RAIL (ch 07 §0.13, W18; ch 04 items 32-33) ═══════════════════════════════════════════════════════════

    /// <summary>The chip SET as the rail paints it: the titles in their final order and the evidence PREFIX length.
    /// Value equality over both, so a recompute that changes nothing notifies nothing.</summary>
    sealed class LikedChipModel(string[] titles, int evidenced) : IEquatable<LikedChipModel>
    {
        public static readonly LikedChipModel None = new([], 0);

        public readonly string[] Titles = titles;
        public readonly int Evidenced = evidenced;

        public bool Equals(LikedChipModel? other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null || other.Evidenced != Evidenced || other.Titles.Length != Titles.Length) return false;
            for (int i = 0; i < Titles.Length; i++)
                if (!string.Equals(Titles[i], other.Titles[i], StringComparison.Ordinal)) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is LikedChipModel m && Equals(m);
        public override int GetHashCode() => HashCode.Combine(Titles.Length, Evidenced);
    }

    sealed record LikedChipBarProps(IReadSignal<LikedChipModel> Chips, string ScrollKey);

    /// <summary>ONE line that scrolls (the rail's own edge fade), "All" first, EXCLUSIVE selection (All + at most one),
    /// re-tapping the active chip clears it, and an un-evidenced curated chip rendered DISABLED as one contiguous tail —
    /// the evidence is a prefix, so the order IS the state. Reads and writes the table's filter through
    /// <c>Track.TableLiveSlot</c> (it is built under the table's provider). 0 DIP when there is no set (W19).</summary>
    sealed class LikedChipBar : Component
    {
        Track.TableLive? _live;
        readonly Action _selectAll;

        public LikedChipBar() => _selectAll = () => Select(null);

        public override Element Render()
        {
            var p = UseProps<LikedChipBarProps>();
            var live = UseContext(Track.TableLiveSlot);
            _live = live;
            var model = p.Chips.Value;
            if (live is null || model.Titles.Length == 0) return new BoxEl { Key = "chips:off", HitTestVisible = false };

            string? selected = live.View.Value.Filters.Tag;   // subscribe: the selected chip IS filter state
            var kids = new Element[model.Titles.Length + 1];
            // AllChip, not AllTracks: a chip beside "Pop" and "K-Pop", where the short word reads as one of the set.
            kids[0] = Controls.Chip(Loc.Get(Strings.Detail.Filter.AllChip), selected is null, true, _selectAll) with { Key = "chip:all" };
            for (int i = 0; i < model.Titles.Length; i++)
            {
                string tag = model.Titles[i];
                bool on = string.Equals(tag, selected, StringComparison.OrdinalIgnoreCase);
                bool available = i < model.Evidenced;
                kids[i + 1] = Controls.Chip(tag, on, available, available ? () => Select(on ? null : tag) : null)
                    with { Key = "chip:" + tag };
            }
            return Controls.ChipRail(kids, p.ScrollKey) with { Key = "chips" };
        }

        void Select(string? tag)
        {
            if (_live is not { } live) return;
            // The LIVE filter at click time — a lens pill's × or the flyout may have moved it since this render.
            live.SetFilters(live.View.Peek().Filters with { Tag = tag });
        }
    }
}
