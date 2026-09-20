// ── Entities/Playlist.Page.cs ──────────────────────────────────────────────────────────────────────────────────────
// owner O's composition: InstallPages (the six routes + O's seams + the playlist verbs), the playlist page over the shared
// detail frame (identity, FrameSpec, TableProfile, the whole-model demand, the hero ⋯ menu, the cover drag, the deposits),
// the local route's arm (Local Files: the header settled locally, a dropped / picked file imported and played), and the
// deposit-target adapter the track menu reads (ch 01 item 42)
//
// Role: UI
// Owner: O
// Wave: 5
// Budget: 2300 lines
// Spec: ch 06 §9; ch 15 §11 (local); ch 03 items 41-50; ch 04 items 56 / 71; ch 01 item 42;
//       docs/plans/wavee/cache-integrity-and-playlist-diff-implementation.md §2 + §3.3 "Freshness" (§6, wave D3)
//
// ── WHAT THE FRAME ALREADY DRAWS (do not duplicate) ──────────────────────────────────────────────────────────────────
//
// Detail.Frame owns the rail and hero scaffolds, the Play pill, the heart, Share, the meta row and its shimmer, the notice
// strip, the chart caption, the tone plane and the table. This page hands it a FrameSpec VALUE, a TableProfile and the
// slots Playlist.UI.cs builds:
//   Cover        → CoverSlot         the editor (hover scrim · JPEG drop · saving) or the art / mosaic
//   Title        → TitleSlot         the inline editor (AnimatedSwap box + the status row)
//   Attribution  → AttributionSlot   the pile / owner row + the invite affordance
//   Description  → DescriptionSlot   the inline description editor (EditableMetadata only)
//   Pulse        → DaylistStrip      the flip countdown (daylist only)
//   LikedFacts   → User.FactsPanel   the facts bento (owner B), when FactsHas
//
// ── DEMAND (the GateAlbumPage pattern) ───────────────────────────────────────────────────────────────────────────────
//
// Once per (playlist slot, scope epoch): PlaylistFields.All + the membership through THE OPEN RULE (§6: ListOpen.Open,
// Surface.Page) — the first open of a session revalidates (an Unknown list comes off the disk and the edge door chains its
// /diff), a fresh list paints, a stale one holds the reveal on its /diff for at most ListFreshness.BlockingBudgetMs. The
// hold reaches the table through its own readiness (HeldRows: the reveal gate reads State/Count), never a page probe. A
// parked page coming back opens again as a Revisit (asks, never holds). As the membership lands: the member rows
// (Row | Audio | Tags | Video), the owner + adders' profiles, and the facts refold (Playlist.Refold — the Added-by / Date /
// Video column facts and the meta line's duration, written only when they moved).

using System.Runtime.InteropServices;
using System.Text;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Playlist
{
    // ══ 1. INSTALL ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Owner O's ONE install. Called once by the composition root after <c>Track.InstallActions()</c>: the six
    /// routes (WP-5.O §2.1), the seams still unassigned (§2.6; <c>Playback.Audio.LocalPath</c> is WP-6.T's, amendment 3),
    /// and the playlist verbs nobody registered before (first registration wins).</summary>
    public static void InstallPages()
    {
        Shell.SetPage(Shell.RouteKind.Playlist, PageFor);
        Shell.SetPage(Shell.RouteKind.Local, PageFor);
        Shell.SetPage(Shell.RouteKind.Liked, User.LikedPageFor);
        Shell.SetPage(Shell.RouteKind.LibraryAlbums, User.LibraryPageFor);
        Shell.SetPage(Shell.RouteKind.LibraryArtists, User.LibraryPageFor);
        Shell.SetPage(Shell.RouteKind.LibraryPodcasts, User.LibraryPageFor);
        Shell.SetPage(Shell.RouteKind.LibraryAudiobooks, User.LibraryPageFor);

        Fetch.ListSettled = ListOpen.Settled;   // wave D3: an unchanged /diff releases a page's reveal hold at once (Playlist.Open.cs)
        Shell.OnNewPlaylist ??= static () => Sidebar.LibraryWrites?.CreatePlaylist?.Invoke(null, true);
        Shell.OnNewFolder ??= static () => Sidebar.LibraryWrites?.NewFolderWith?.Invoke(null, Array.Empty<RootlistItemRef>());
        Controls.Library ??= User.LibrarySeam;
        Drag.LikedChipArt ??= User.LikedChipArt();
        Shell.OnFilesDropped ??= PlayFiles;
        Shell.PickAndPlayFile ??= PickAndPlay;
        Track.MenuSeams.RemoveRows ??= static (host, _) =>
        {
            if (Entities.Current.Playlists.TryGetSlot(host.Playlist.Id, out int slot))
                Spotify.PlaylistEdits.RemoveRows(new Playlist(slot), host.Rows);
        };
        Track.MenuSeams.MoveRows ??= Spotify.PlaylistEdits.MoveToPlaylist;
        RegisterActions();
    }

    /// <summary><see cref="Shell.RouteKind.Playlist"/> (subject = the playlist uri) and <see cref="Shell.RouteKind.Local"/>.</summary>
    // MOUNT POINT (stage B contract)
    public static Element PageFor(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        bool local = route.Kind == Shell.RouteKind.Local;
        return Embed.Comp(new PageProps(local ? default : route.Subject, local, routeKey), static () => new PlaylistPage())
            with { Key = "playlist-page:" + routeKey };
    }

    sealed record PageProps(EntityUri Subject, bool Local, string RouteKey);

    /// <summary>0.2.9 <c>PlaylistCreateIntent</c>: a create that NAVIGATED arms its uri (the shared patch to
    /// <c>Spotify.Library.StartCreate</c>); the title editor takes it once and opens in edit mode (item 37).</summary>
    public static class PlaylistCreateIntent
    {
        static readonly HashSet<string> s_armed = new(StringComparer.Ordinal);
        public static void Arm(string uri) { if (uri.Length > 0) s_armed.Add(uri); }
        /// <summary>True exactly once per <see cref="Arm"/>.</summary>
        public static bool Take(string uri) => uri.Length > 0 && s_armed.Remove(uri);
    }

    /// <summary>The overlay the last mounted playlist page saw — where a context-menu verb with no overlay seam of its
    /// own (rename, invite, the picker) opens (<see cref="ActionServices"/> carries none; see the report's gaps).</summary>
    static IOverlayService? s_overlay;

    static IOverlayService? ActionOverlay => !Controls.IsNullOverlay(s_overlay) ? s_overlay
                                           : !Controls.IsNullOverlay(Track.DrawerOverlay) ? Track.DrawerOverlay : null;

    /// <summary>A Spotify account scope with a session that can send (0.2.9 <c>SpotifyEditsLive</c>). SUBSCRIBES to the
    /// session phase — read it in a render.</summary>
    static bool EditsLiveNow()
    {
        var scope = Entities.Current;
        bool account = scope is not null && scope.Key.Provider == "spotify" && scope.MeSlot > Table.None && scope.Key.Account.Length > 0;
        return SpotifyEditsLiveOf(account, Spotify.Status.Value == Spotify.SessionPhase.Online);
    }

    // ══ 2. THE PAGE ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The page render's gate value (W3-A3, the value gates): exactly what <c>PlaylistPage.Render</c> and its
    /// <c>IdentityOf</c> read off the tables — the playlist row (title, cover, saves, count, caps, flags, description,
    /// chart, daylist, share, notice), the membership edge's state and version plus every member's version (the meta
    /// line's duration and its "durations known" arm), the owner row (name, avatar), the recommendations edge's state
    /// (the Recommended section's mount), the tuning edge's version (the Tune affordance), the session's edit-path
    /// verdict, the facts bento's presence, and whether the open's revalidation is HOLDING the list (<see cref="ListOpen"/>:
    /// the meta line's count and duration are the list's, so they wait with it). The seven table counters are read INSIDE
    /// the memo that computes this, so a publication that leaves it equal — a cover grading, an unrelated row, a straggler
    /// on another page's edge — never re-renders the page, rebuilds its <c>Detail.Identity</c> or pushes a new
    /// <c>FrameSpec</c>.</summary>
    /// <param name="Membership">The membership edge's READINESS (<c>EdgeTableBase.Readiness</c>, G-050), never its raw
    /// <c>State</c>: an Unknown list whose last ask failed — or was answered with nothing — must reach the identity as
    /// <see cref="EdgeState.Failed"/>, because the meta line's loading arm is the membership's (see
    /// <see cref="PageRules.MetaArmFor"/>).</param>
    public readonly record struct PageStamp(uint Epoch, int Slot, uint Row, EdgeState Membership, uint MembersEdge, ulong Members,
                                            int OwnerSlot, uint Owner, EdgeState Recommendations, uint Tuning, bool EditsLive, bool Facts,
                                            bool Holding);

    /// <summary>What the hero's meta line ("N songs · 2 hr 59 min") shows. THREE answers, never a fourth.</summary>
    public enum MetaArm : byte
    {
        /// <summary>A fact is genuinely on its way: the open's own budgeted hold, or a membership nobody has answered
        /// YET. <c>Detail.MetaRow</c> paints the shimmer bar shaped "00 songs · 0 hr 00 min".</summary>
        Loading,
        /// <summary>A count can be stated.</summary>
        Text,
        /// <summary>Nothing can be stated and nothing more is coming. The row leaves the hero entirely
        /// (<c>Detail.Identity.Meta</c> null + <c>MetaLoading</c> false ⇒ the hero's <c>Meta</c> flag is false and the
        /// block is never added) rather than shimmer at a fact that will not arrive.</summary>
        Absent,
    }

    /// <summary>The playlist page's engine-free decisions (the pure half <c>Wavee.Tests</c> pins): which optional
    /// sections mount, the meta line's arm, and the two value-cache masks the render keys its profile and slots on.</summary>
    public static class PageRules
    {
        /// <summary>THE META LINE'S ARM (the fix for an eternal shimmer). <c>Detail.MetaRow</c> paints a
        /// <c>SkelRegionEl</c> whose <c>Pending</c> is hard-wired true and whose <c>Failed</c> is hard-wired false, so
        /// an identity that says "loading" and never stops says it FOREVER — the page is the only thing that can end
        /// that state, and it must be able to end it in every case.
        /// <list type="bullet">
        /// <item><paramref name="holding"/> (the open's revalidation hold, <see cref="ListOpen"/>) ⇒
        /// <see cref="MetaArm.Loading"/>: the count and duration ARE the list's, so they wait with it — and that hold
        /// carries its own budget (<see cref="ListFreshness.BlockingBudgetMs"/>), so it always ends.</item>
        /// <item>a count the page can state ⇒ <see cref="MetaArm.Text"/>: the header's own length
        /// (<paramref name="countKnown"/>), resident rows, or a membership that ANSWERED — Complete with no rows is a
        /// real, renderable "0 songs" (ch 03 §7).</item>
        /// <item>a membership that FAILED, with no count to fall back on ⇒ <see cref="MetaArm.Absent"/>.</item>
        /// <item>otherwise ⇒ <see cref="MetaArm.Loading"/>: the ask is out (the page's own
        /// <c>ListOpen.Open</c>) and its answer — landing, failing or answering with nothing — moves
        /// <paramref name="readiness"/> off Unknown.</item>
        /// </list>
        /// <para><b>Why the count cannot simply be demanded.</b> <see cref="PlaylistFields.TrackCount"/> is named by no
        /// route in <see cref="FetchRoutes"/> (its own doc says so: "no route names this bit in its Primary/Groups") —
        /// only the one decoder that actually sees the wire's length fills it, and that decoder IS the membership read.
        /// "Ask for the count" is not a thing this page can do, so the meta line's loading state is the MEMBERSHIP's,
        /// and it must therefore read the membership's FAILURE too. <paramref name="readiness"/> is
        /// <c>EdgeTableBase.Readiness</c>, which is the only reading that ever answers
        /// <see cref="EdgeState.Failed"/>.</para></summary>
        public static MetaArm MetaArmFor(bool holding, EdgeState readiness, bool countKnown, int residentRows)
        {
            if (holding) return MetaArm.Loading;
            if (countKnown || residentRows > 0 || readiness is EdgeState.Complete or EdgeState.Partial) return MetaArm.Text;
            return readiness == EdgeState.Failed ? MetaArm.Absent : MetaArm.Loading;
        }

        /// <summary>The Recommended Songs section mounts for an editable Spotify playlist on a remote route, once a live
        /// edit path exists OR the recommendations edge has already answered (a signed-out reopen keeps a landed
        /// batch; item 58: absent under <c>--fake</c> until the edge lands).</summary>
        public static bool ShowsRecommendations(bool local, bool editable, bool spotify, bool editsLive, EdgeState recommendations)
            => !local && editable && spotify && (editsLive || recommendations != EdgeState.Unknown);

        /// <summary>The <c>TableProfile</c> cache key: every input that changes the profile VALUE, and nothing else.</summary>
        public static int ProfileMask(bool editable, bool recs, bool tune, bool local, float lensExtent)
            => (editable ? 1 : 0) | (recs ? 2 : 0) | (tune ? 4 : 0) | (local ? 8 : 0) | ((int)lensExtent << 4);

        /// <summary>The <c>FrameSlots</c> cache key: which optional slots are present.</summary>
        public static int SlotsMask(bool editableMeta, bool daylist, bool facts)
            => (editableMeta ? 1 : 0) | (daylist ? 2 : 0) | (facts ? 4 : 0);
    }

    sealed class PlaylistPage : Component
    {
        Scope? _scope;
        EntityUri _subject;
        bool _local;
        Playlist _playlist;
        string? _visibleCover;
        IOverlayService? _overlay;

        // value caches: an equal re-render costs the frame and the table nothing
        HeldRows? _source;
        int _sourceSlot = -1;
        Track.TableProfile? _profile;
        int _profileMask = -1;
        Detail.FrameSlots? _slots;
        int _slotsMask = -1;
        // The identity VALUE, rebuilt only when the stamp it was computed under moved: IdentityOf walks the whole
        // membership and mints half a dozen strings, and the frame's own _spec.SetIfChanged already short-circuits an
        // equal spec — so an unmoved identity must cost nothing above the compare.
        Detail.Identity? _identity;
        PageStamp _identityFor;
        /// <summary>The owner slot whose thin profile this page already re-asked (<see cref="DemandOwnerProfile"/>).</summary>
        int _ownerAsked = Table.None;
        readonly User.LensCell _lens = new();

        readonly Detail.FrameActions _actions, _actionsReadOnly;
        readonly Func<ColorF> _accent;
        readonly Func<PageStamp> _stamp;
        readonly Action _demand, _demandRows, _observe, _activated, _tune, _holdExpired;
        /// <summary>The open hold's deadline wake (<see cref="HoldExpired"/>), re-armed from its own fire while the hold
        /// still has budget — a wake that lands a frame early must not be the last one.</summary>
        TimerHandle _holdWake;
        readonly Func<bool> _editable;
        readonly Func<DragPayload, int?, bool> _deposit;
        readonly Func<ReadOnlySpan<RowRef>, int, bool> _moveRows;
        readonly Func<float> _lensExtent;
        readonly Action<IReadOnlyList<int>> _removeRows;
        readonly Func<Element?> _recs, _lensHeader;
        readonly Func<float, Element> _cover, _compactCover, _attribution, _description, _facts;
        readonly Func<float, float, Element> _title;
        readonly Func<Element> _pulse;

        public PlaylistPage()
        {
            _accent = () => AccentOf(_playlist);
            _stamp = Stamp;
            _demand = Demand;
            _demandRows = DemandRows;
            _observe = () => ListOpen.Observe(_playlist.Slot);
            _holdExpired = HoldExpired;
            _activated = Activated;
            _tune = () => { if (!Controls.IsNullOverlay(_overlay)) OpenTune(_overlay, _playlist); };
            _editable = () => _playlist.Editable;
            _deposit = Deposit;
            _moveRows = (rows, at) => Spotify.PlaylistEdits.MoveRows(_playlist, rows, at);
            _lensExtent = () => User.LensExtentFor(_lens);
            _removeRows = rows => Spotify.PlaylistEdits.RemoveRows(_playlist, rows);
            _recs = () => RecommendationsSection(_playlist);
            _lensHeader = () => User.LensHeader(_lens, DetailKind.Playlist);
            _cover = size => CoverSlot(_playlist, size);
            _compactCover = size => CoverArt(_playlist, size);
            _attribution = w => AttributionSlot(_playlist, w);
            _description = w => DescriptionSlot(_playlist, w);
            _facts = w => User.FactsPanel(_source!, DetailKind.Playlist, _lens, w, false);
            _title = (size, lineHeight) => TitleSlot(_playlist, size, lineHeight);
            _pulse = () => DaylistStrip(_playlist, compact: true, _accent);
            _actions = new Detail.FrameActions
            {
                Shuffle = Shuffle, More = MoreMenu, CoverDrag = CoverPayload, DepositOnPage = _deposit,
                GoLibrary = static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.LibraryAlbums)),
            };
            _actionsReadOnly = _actions with { DepositOnPage = null };
        }

        public override Element Render()
        {
            var p = UseProps<PageProps>();
            uint scopeEpoch = Entities.ScopeEpoch.Value;          // FIRST: a scope switch re-points every table below
            var scope = Entities.Current;
            var overlay = UseContext(Overlay.Service);
            _overlay = overlay;
            if (!Controls.IsNullOverlay(overlay)) s_overlay = overlay;

            if (!ReferenceEquals(_scope, scope) || !_subject.Equals(p.Subject) || _local != p.Local)
            {
                _scope = scope;
                _subject = p.Subject;
                _local = p.Local;
                // The factory allocates an empty row for an unseen uri: the page binds it at once and Ensure fills it.
                _playlist = p.Local ? LocalFiles : p.Subject.IsValid && p.Subject.Kind == EntityKind.Playlist ? Entities.Playlist(p.Subject) : default;
                _source = null;
                _identity = null;
                _visibleCover = null;
                _ownerAsked = Table.None;
            }
            var pl = _playlist;
            // The source BEFORE the gate: the stamp's Facts arm scans it (User.FactsHas), so it must exist by then. It is the
            // playlist's rows with the open's hold folded into their readiness (HeldRows) — the table's reveal gate reads it.
            if (pl.IsValid && (_source is null || _sourceSlot != pl.Slot))
            {
                _source = new HeldRows(pl);
                _sourceSlot = pl.Slot;
            }
            UseEffect(_demand, DepKey.From(pl.Slot, (int)scopeEpoch));
            UseEffect(_demandRows);
            // The open's revalidation: subscribed to the tables that publish its answer, settled when the model says how it ended.
            UseEffect(_observe);
            // 0 while no lens is on, 36 while one is (ch 04 item 34): a memo, so a filter edit that does not flip the answer
            // never re-renders the page. Hooks before the early return.
            var lensMemo = UseComputed(_lensExtent);
            // THE gate (W3-A3): the seven table counters and the session phase are subscribed inside this memo, and the
            // memo's equality cut-off resolves an equal stamp CLEAN without running the body below — in real mode the
            // tables publish nearly every frame while rows and covers stream in, and only a stamp that moved rebuilds
            // the identity and the frame spec.
            var stamp = UseComputed(_stamp).Value;
            // Coming back to a parked page: the daylist rollover ladder re-decides and the list opens again as a Revisit
            // (a preallocated delegate — no per-render alloc).
            UseActivation(onActivated: _activated);

            if (!pl.IsValid) return Controls.Vacancy(Controls.VacancyVoice.Error);

            var cfg = _local ? Detail.Config.Playlist with { Heart = HeartMode.None, Recommendations = false } : Detail.Config.Playlist;
            bool editable = pl.Editable;
            bool recs = PageRules.ShowsRecommendations(_local, editable, pl.Uri.Provider == EntityProvider.Spotify, stamp.EditsLive, stamp.Recommendations);
            bool tune = !_local && HasTuneChoice(pl) && pl.TuningCurrent;
            bool facts = stamp.Facts;
            float lensExtent = lensMemo.Value;

            int profileMask = PageRules.ProfileMask(editable, recs, tune, _local, lensExtent);
            if (_profile is null || _profileMask != profileMask)
            {
                _profileMask = profileMask;
                _profile = Track.TableProfile.From(cfg) with
                {
                    Editable = _editable,
                    Deposit = editable ? _deposit : null,
                    MoveRows = editable ? _moveRows : null,
                    RemoveRows = editable ? _removeRows : null,
                    Recommendations = recs ? _recs : null,
                    Tune = tune ? _tune : null,
                    LensHeader = _lensHeader,
                    LensExtent = lensExtent,
                    Drawer = Track.DrawerSeam,
                };
            }

            bool editableMeta = pl.EditableMetadata && !_local;
            bool daylist = pl.DaylistExpiresAt > 0;
            int slotsMask = PageRules.SlotsMask(editableMeta, daylist, facts);
            if (_slots is null || _slotsMask != slotsMask)
            {
                _slotsMask = slotsMask;
                _slots = new Detail.FrameSlots
                {
                    Cover = _cover,
                    CompactCover = _compactCover,
                    Title = editableMeta ? _title : null,
                    Attribution = _attribution,
                    Description = editableMeta ? _description : null,
                    Pulse = daylist ? _pulse : null,
                    LikedFacts = facts ? _facts : null,
                };
            }

            // editableMeta is a function of the row's caps and notice — both inside stamp.Row — so the stamp alone keys
            // the identity cache.
            var identity = _identity;
            if (identity is null || stamp != _identityFor)
            {
                _identityFor = stamp;
                identity = IdentityOf(pl, editableMeta, stamp.Holding);
                _identity = identity;
            }

            return Detail.Frame(new Detail.FrameSpec
            {
                Identity = identity,
                Config = cfg,
                Source = _source,
                Profile = _profile,
                Actions = editable ? _actions : _actionsReadOnly,
                Slots = _slots,
                RouteKey = p.RouteKey,
            });
        }

        /// <summary>The gate's compute (the <c>UseComputed</c> body): reads the seven counters this page depends on —
        /// tracked, so a drain of any of them wakes the memo — plus the session phase and, through
        /// <c>_source.Held</c>, the source's own one-shot "the open has run" signal (<see cref="HeldRows.Opened"/>:
        /// <c>Held</c> answers by a DIFFERENT rule either side of it, so the flip has to reach this memo), and folds
        /// what the render and <see cref="IdentityOf"/> read into a <see cref="PageStamp"/>. <c>_playlist</c>, <c>_local</c> and
        /// <c>_source</c> are Render's fields, written before the memo is read; the page is keyed on its route, so they
        /// change only together with a scope epoch the memo tracks. Runs one member loop (the fold) and the facts
        /// bento's early-exit scan per publication; allocates nothing once the open has run (the pre-open preview reads
        /// the list's stamps by uri — the first frame only, see <see cref="HeldRows"/>).</summary>
        PageStamp Stamp()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = scope.Playlists.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            _ = scope.Users.Changed.Value;
            _ = e.PlaylistTracks.Changed.Value;
            _ = e.PlaylistTuning.Changed.Value;
            _ = e.PlaylistRecs.Changed.Value;
            _ = e.TrackArtists.Changed.Value;          // FactsHas: a keyed credit alone earns the bento
            _ = ListOpen.Changed.Value;                // the open's hold: taken, answered, out of budget
            // The open ITSELF — HeldRows.Held switches rule at it — is the source's own signal, read through Held below.
            bool editsLive = EditsLiveNow();           // subscribes to the session phase (item 58)
            var pl = _playlist;
            if (!pl.IsValid)
                return new PageStamp(epoch, pl.Slot, 0, EdgeState.Unknown, 0, RowFold.Seed, Table.None, 0, EdgeState.Unknown, 0, editsLive, false, false);
            int slot = pl.Slot;
            var owner = pl.Owner;
            bool holding = _source is not null && _source.Held;
            bool facts = _source is not null && User.FactsHas(_source, DetailKind.Playlist);
            // Readiness, not State: a failed / unanswered membership must MOVE the stamp, or the identity is never
            // rebuilt and the meta line keeps the shimmer it can no longer earn (PageRules.MetaArmFor).
            return new PageStamp(epoch, slot, pl.Version, e.PlaylistTracks.Readiness(slot), e.PlaylistTracks.Version(slot),
                                 RowFold.Rows(scope.Tracks, pl.TrackSlots), owner.Slot, RowFold.Version(scope.Users, owner.Slot),
                                 e.PlaylistRecs.State(slot), e.PlaylistTuning.Version(slot), editsLive, facts, holding);
        }

        /// <summary>The identity snapshot every arm renders (ch 06 §7's table, read off the columns and the membership).
        /// Called only when the <see cref="PageStamp"/> moved (the cache in Render). <paramref name="holding"/>: the open's
        /// revalidation holds the list, and the meta line's count and duration ARE the list's — they shimmer with it
        /// rather than paint yesterday's numbers and then change.</summary>
        Detail.Identity IdentityOf(Playlist pl, bool editableMeta, bool holding)
        {
            var slots = pl.TrackSlots;
            var state = pl.MembershipState;
            // THE reveal discipline the rest of the app already reads (EdgeTableBase.Readiness, G-050): an Unknown list
            // whose last ask FAILED — or was answered with nothing (MarkUnanswered's NoRoute) — reads Failed here. Raw
            // State never answers Failed, which is exactly why this line shimmered forever on a list nobody could read.
            var readiness = Entities.Current.Edges.PlaylistTracks.Readiness(pl.Slot);
            long totalMs = 0;
            bool durationsKnown = state == EdgeState.Complete;
            for (int i = 0; i < slots.Length; i++)
            {
                var t = new Track(slots[i]);
                totalMs += t.DurationMs;
                if (!t.Knows(TrackFields.Duration)) durationsKnown = false;
            }
            // A count this page can state WITHOUT the membership: the bit the one length-bearing decoder sets (a real
            // `length: 0` included, bug A1), or a nonzero count some answer wrote beside Identity.
            bool countKnown = !_local && (pl.Knows(PlaylistFields.TrackCount) || (pl.Knows(PlaylistFields.Identity) && pl.TrackCount > 0));
            var arm = PageRules.MetaArmFor(holding, readiness, countKnown, slots.Length);
            bool metaLoading = arm == MetaArm.Loading;
            int count = countKnown && pl.TrackCount > 0 ? pl.TrackCount : slots.Length;
            string? meta = arm != MetaArm.Text ? null
                : Detail.Text.PlaylistMeta(count, totalMs, durationsKnown && slots.Length > 0, pl.Knows(PlaylistFields.Saves) ? pl.Saves : 0, pl.EpisodeCount);

            string? incoming = Controls.ArtUrl(pl.ImageId);
            _visibleCover = Detail.CoverLatch.PreferVisible(incoming, _visibleCover);

            var owner = pl.Owner;
            bool spotify = pl.Uri.Provider == EntityProvider.Spotify;
            string title = TitleOf(pl);
            return new Detail.Identity
            {
                Subject = pl.Uri,
                Kind = DetailKind.Playlist,
                // The header has not answered: the rail and the hero shimmer as a whole until it does (Local Files is
                // settled on this device and never waits).
                HeaderPending = !_local && !pl.Knows(PlaylistFields.Identity),
                Title = _local && title.Length == 0 ? Loc.Get("localFile.playlistTitle") : title,
                CoverUrl = _visibleCover,
                CardAccent = pl.Accent,
                Eyebrow = _local ? Loc.Get(Strings.Nav.LocalFiles)
                    : Detail.Text.Eyebrow(DetailKind.Playlist, BadgeStyle.OwnerRow, AlbumKind.Album, 0, pl.IsCollaborative, pl.IsPublic,
                                          pl.Knows(PlaylistFields.Visibility)),
                Meta = meta,
                MetaLoading = metaLoading,
                OwnerName = _local && !owner.IsValid ? Loc.Get("localFile.onThisDevice") : NameOf(owner),
                OwnerImageUrl = Controls.ArtUrl(owner.ImageId),
                Owner = owner.IsValid ? owner.Uri : default,
                Collaborative = pl.IsCollaborative,
                DescriptionHtml = _local && pl.DescriptionId.IsEmpty ? Loc.Get("localFile.playlistDescription") : Entities.Strings.Resolve(pl.DescriptionId),
                ChartNewEntries = pl.ChartNewEntries,
                ChartUpdatedAtMs = pl.ChartUpdatedAt * 1000L,
                DaylistExpiresAtMs = pl.DaylistExpiresAt * 1000L,
                DaylistCreatedAtMs = pl.DaylistCreatedAt * 1000L,
                ShareUrl = !pl.ShareUrlId.IsEmpty ? Entities.Strings.Resolve(pl.ShareUrlId)
                         : spotify ? "https://open.spotify.com/playlist/" + new string(EntityUri.IdOf(pl.Uri.Text.AsSpan())) : null,
                Notice = pl.Notice,
                EditableMetadata = editableMeta,
            };
        }

        void Demand()
        {
            var pl = _playlist;
            if (!pl.IsValid) return;
            if (_local) { SettleLocalFiles(); return; }
            // Membership owns the playlist-v2 header and revision. Asking its groups here races
            // an unconditional full read against the list-open disk/diff path.
            Entities.Ensure(pl, PlaylistFields.Visibility | PlaylistFields.Saves | PlaylistFields.Accent);
            // The list goes through THE open rule (§6): an Unknown one is asked once — the disk leg, then the /diff the edge
            // door chains itself — a fresh one paints, a stale one is revalidated and holds the reveal on the answer.
            ListOpen.Open(pl, ListOpenPolicy.Surface.Page);
            _source?.Opened();
        }

        /// <summary>The page came back from being parked (KeepAlive, an un-minimize): the daylist rollover ladder
        /// re-decides, and the list opens again as a <see cref="ListOpenPolicy.Surface.Revisit"/> — past the window it is
        /// revalidated, and never held (its rows are on screen). A scope that moved while it was parked is the next
        /// render's to re-bind, not this callback's.</summary>
        void Activated()
        {
            Home.Feeds.RearmDaylist();
            var pl = _playlist;
            if (_local || !pl.IsValid || !ReferenceEquals(_scope, Entities.Current)) return;
            ListOpen.Open(pl, ListOpenPolicy.Surface.Revisit);
        }

        void DemandRows()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Edges.PlaylistTracks.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            // The OWNER arrives with the playlist ROW, not with the membership: a page opened before its header landed
            // has no owner slot to ask about on its first run, and without this subscription the effect never ran again
            // on a list whose membership never publishes. That is why the owner avatar was "not always resolved".
            _ = scope.Playlists.Changed.Value;
            var pl = _playlist;
            if (!pl.IsValid) return;
            var slots = pl.TrackSlots;
            if (slots.Length > 0 && !_local)
                Entities.Ensure(MemoryMarshal.Cast<int, Track>(slots), TrackFields.Row | TrackFields.Audio | TrackFields.Tags | TrackFields.Video);
            Span<int> people = stackalloc int[64];
            int n = pl.CollaboratorSlots(people);
            if (n > 0 && !_local) Entities.Ensure(scope.Users, people[..n], (uint)UserFields.Identity);
            if (!_local) DemandOwnerProfile(scope, pl.Owner.Slot);
            if (pl.MembershipState == EdgeState.Partial) Entities.EnsureEdge(FetchEdge.PlaylistTracks, pl.Slot, pl.TrackSlots.Length);
            pl.Refold();
        }

        /// <summary>THE OWNER'S PORTRAIT. A CARD's answer stages the owner's NAME alone at <see cref="Authority.Thin"/>
        /// and stamps <see cref="UserFields.Identity"/> KNOWN on that row (<c>Spotify.Decode.Home</c>'s owner arm, the
        /// pathfinder's user node) — and <see cref="Entities.Ensure"/> asks only for <c>wanted &amp; ~known</c>, so the
        /// row above never asks for the profile that actually carries the avatar. The owner line then renders the
        /// monogram forever on any route reached through a card, and resolves only when the page is the first thing to
        /// name the user. So: ONE invalidate per (page, owner slot), and only while the group is known at less than
        /// <see cref="Authority.Full"/> — an account that genuinely has no picture answers Full with no image and is
        /// never asked again (the monogram is then the correct, deliberate fallback, not a missing fetch).</summary>
        void DemandOwnerProfile(Scope scope, int owner)
        {
            if (owner <= Table.None || owner == _ownerAsked || owner >= scope.Users.Count) return;
            if ((scope.Users.Known[owner] & (uint)UserFields.Identity) == 0) return;            // Ensure above owns this case
            if ((Authority)scope.Users.IdentityAuthority[owner] >= Authority.Full) return;
            _ownerAsked = owner;
            Entities.Invalidate(scope.Users, new ReadOnlySpan<int>(in owner), (uint)UserFields.Identity);
        }

        /// <summary>The Local Files header and membership are settled LOCALLY — there is no remote to ask (G-110).</summary>
        void SettleLocalFiles()
        {
            EnsureLocalFilesHeader();
            var local = LocalFiles;
            var e = Entities.Current.Edges.PlaylistTracks;
            if (e.State(local.Slot) != EdgeState.Unknown) return;
            e.Replace(local.Slot, ReadOnlySpan<int>.Empty, ReadOnlySpan<PlaylistTrackEdge>.Empty, EdgeState.Complete, 0);
            Entities.Publish();
        }

        void Shuffle()
        {
            var pl = _playlist;
            if (!pl.IsValid) return;
            var slots = pl.TrackSlots;
            if (slots.Length == 0) return;
            var rows = new EntityRef[slots.Length];
            for (int i = 0; i < rows.Length; i++) rows[i] = new EntityRef(EntityKind.Track, slots[i]);
            Playback.SetShuffle(true);
            Playback.PlayRows(rows, 0, pl.Id);
        }

        /// <summary>The hero/rail ⋯ (ch 03 item 49): "Copy to playlist ▸" (a playlist page's label for owned and followed
        /// alike — Heart is Follow for both) · Play next · Add to queue · then the owner pair, each self-gated.</summary>
        ContextMenuModel? MoreMenu()
        {
            var pl = _playlist;
            if (!pl.IsValid) return null;
            var rows = new List<MenuFlyoutItem>(6);
            var tracks = TracksOf(pl);
            if (tracks.Length > 0) rows.Add(DepositMenu(Loc.Get(Strings.Detail.CopyToPlaylist), tracks, pl.Uri, _overlay));
            var ctx = new ActionContext(ActionTarget.ForPlaylist(pl.Uri, TitleOf(pl), new PlaylistHost(pl.Uri, pl.Caps, Array.Empty<int>())), Actions.Services);
            if (Actions.Menu.Row(ActionId.PlayContextNext, in ctx) is { } next) rows.Add(next);
            if (Actions.Menu.Row(ActionId.AddContextToQueue, in ctx) is { } queue) rows.Add(queue);
            AppendOwnerItems(rows, pl, _overlay);
            return rows.Count == 0 ? null : new ContextMenuModel(rows);
        }

        object? CoverPayload()
        {
            var pl = _playlist;
            if (!pl.IsValid) return null;
            string uri = pl.Uri.Text;
            Track[]? tracks = pl.MembershipState == EdgeState.Complete ? TracksOf(pl) : null;
            Func<CancellationToken, Task<Track[]>>? resolver = null;
            if (tracks is null && Sidebar.LibraryWrites?.ResolveTracks is { } resolve) resolver = ct => resolve(uri, ct);
            return new DragPayload(DragKind.Playlist, uri, uri, TitleOf(pl), new EntityRef(EntityKind.Playlist, pl.Slot),
                                   Tracks: tracks, TrackResolver: resolver, ArtUrl: Controls.ArtUrl(pl.ImageId));
        }

        /// <summary>A foreign drop into the list (<paramref name="at"/> a PRE-move slot) or onto the page body (null =
        /// append). A same-list payload is the table's own move and never a copy.</summary>
        bool Deposit(DragPayload payload, int? at)
        {
            var pl = _playlist;
            if (!pl.IsValid || !pl.Editable || !payload.CanCopyTracks) return false;
            string uri = pl.Uri.Text;
            if (string.Equals(payload.SourcePlaylistUri, uri, StringComparison.Ordinal) || string.Equals(payload.Uri, uri, StringComparison.Ordinal))
                return false;
            if (_local) return false;
            int slot = pl.Slot;
            Task<Track[]> resolving;
            try { resolving = payload.ResolveTracksAsync(); }
            catch (Exception ex) { Log.Warn("playlist", "a deposit's tracks could not be resolved", ex); return false; }
            resolving.ContinueWith(t =>
            {
                Track[] tracks = t.IsCompletedSuccessfully ? t.Result : [];
                Spotify.Post(() => Spotify.PlaylistEdits.AddTracks(new Playlist(slot), tracks, at));
            }, TaskScheduler.Default);
            return true;
        }
    }

    /// <summary>THE PAGE'S TABLE SOURCE: the playlist's rows with the open's revalidation hold folded into the readiness
    /// the table's reveal gate already reads (<c>TableRules.RowsPending</c> over State / Count / Total). While
    /// <see cref="ListOpen"/> holds this list — a stale baseline whose <c>/diff</c> is out, for at most
    /// <see cref="ListFreshness.BlockingBudgetMs"/> — it reads as a list nobody has answered yet: Unknown, no rows. The
    /// shimmer stays up, and what the reveal ramp then shows is the revalidated list, never yesterday's copy painted and
    /// swapped. Everything else is the plain playlist source's.
    /// <para>BEFORE THE OPEN. The page's first frame renders before its demand effect runs the open (keyed effects drain
    /// after present), and one frame of yesterday's rows is exactly the paint-then-swap the hold exists to prevent — so
    /// until <see cref="Opened"/>, the source reads the hold the open WILL take (<see cref="ListOpen.WouldHold"/>, the
    /// same pure rule over the same facts). Subscribe reads <see cref="ListOpen.Changed"/>, so the table's memos re-read
    /// on every take, answer and budget.</para>
    /// <para><b>THE INVARIANT (a fix, not a decoration).</b> <see cref="Held"/> switches RULE at the open —
    /// <see cref="ListOpen.WouldHold"/> before, <see cref="ListOpen.Holding"/> after — and the two do not agree in
    /// general: <c>WouldHold</c> re-decides the plan and looks at neither <c>Observed</c>, nor a spent budget, nor a
    /// record gone stale, all of which <c>Holding</c> consults. So the flip is a REAL change of the answer, and every
    /// memo derived from it — the page's <see cref="PageStamp"/> folds <c>Held</c> into <c>Holding</c> and, through
    /// <c>Count</c>, into <c>Facts</c> — must be able to see it. It is therefore a SIGNAL, written from the demand
    /// EFFECT and read (tracked) everywhere <c>Held</c> is. As a plain field it flipped with no write for a memo to
    /// track, and <see cref="ListOpen.Open"/>'s own bump only masked it: the bump is skipped whenever the open JOINS a
    /// record whose hold is already exactly this one (a re-mount inside <see cref="ListFreshness.BlockingBudgetMs"/>,
    /// another surface that got there first) and whenever it plans no ask at all. A page whose entity tables then go
    /// quiet kept its PRE-open stamp for good: zero rows for <c>User.FactsHas</c>, so the facts bento — and the
    /// Insights toggle behind it — never appeared.</para></summary>
    sealed class HeldRows(Playlist playlist) : Track.TableSource
    {
        readonly Playlist _playlist = playlist;
        readonly Track.TableSource _rows = Track.TableSource.ForPlaylist(playlist);
        /// <summary>One-shot, and TRACKED: see the invariant on the class. Written only by <see cref="Opened"/>.</summary>
        readonly Signal<bool> _opened = new(false);

        /// <summary>The page's demand effect ran <see cref="ListOpen.Open"/>: the model's record is the truth from here.
        /// A signal write, so it must stay on the EFFECT path (<c>PlaylistPage.Demand</c>) — never a render body.
        /// Idempotent: <c>Signal.Value</c> is set-if-changed, so a re-run of the demand costs nothing.</summary>
        internal void Opened() => _opened.Value = true;

        /// <summary>Is the list held right now? The rule either side of the open — a TRACKED read of <see cref="Opened"/>
        /// (so a memo over this source re-runs when the open lands), and an untracked read of the model's record
        /// (<see cref="Subscribe"/>, or <see cref="ListOpen.Changed"/>, is that half).</summary>
        internal bool Held => _opened.Value ? ListOpen.Holding(_playlist.Slot) : ListOpen.WouldHold(_playlist, ListOpenPolicy.Surface.Page);

        public override EntityUri Context => _rows.Context;
        public override int Count => Held ? 0 : _rows.Count;
        public override int Total => Held ? 0 : _rows.Total;
        public override EdgeState State => Held ? EdgeState.Unknown : _rows.State;
        public override uint Version => _rows.Version;
        // Index reads are gated by Count (0 while held), so they go straight through.
        public override Track At(int index) => _rows.At(index);
        public override int AddedAt(int index) => _rows.AddedAt(index);
        public override User AddedBy(int index) => _rows.AddedBy(index);
        public override StringId ItemId(int index) => _rows.ItemId(index);
        public override byte ChartStatus(int index) => _rows.ChartStatus(index);
        public override Track TopTrack => _rows.TopTrack;
        public override bool HasDateAdded => _rows.HasDateAdded;
        public override bool HasAddedBy => _rows.HasAddedBy;
        public override bool HasVideo => _rows.HasVideo;
        public override void Subscribe()
        {
            _rows.Subscribe();
            _ = ListOpen.Changed.Value;
            _ = _opened.Value;          // the other edge of Held: a consumer that subscribes here and reads Count later
        }
        internal override void Retry() => _rows.Retry();
        internal override Playlist HostPlaylist => _rows.HostPlaylist;
        public override bool Equals(object? obj) => obj is HeldRows o && o._playlist.Slot == _playlist.Slot;
        public override int GetHashCode() => HashCode.Combine(4, _playlist.Slot);
    }

    /// <summary>Any row carries a NAMED Choice — the allocation-free half of <c>PlaylistTuneMenuModel.IsEligible</c> the
    /// render asks (the source is the Spotify signals write, always wired; a signed-out click reports it).</summary>
    static bool HasTuneChoice(Playlist p)
    {
        var options = p.TuningOptions;
        for (int i = 0; i < options.Length; i++)
            if (options[i].Kind == (byte)TuningOptionKind.Choice && !options[i].DisplayName.IsEmpty) return true;
        return false;
    }

    static Track[] TracksOf(Playlist p)
    {
        var slots = p.TrackSlots;
        var tracks = new Track[slots.Length];
        for (int i = 0; i < tracks.Length; i++) tracks[i] = new Track(slots[i]);
        return tracks;
    }

    // ══ 3. THE OWNER ITEMS AND THE DEPOSIT SUBMENU (W18, W19, W20) ═══════════════════════════════════════════════════

    /// <summary>W18's owner pair, each self-gated: "Invite collaborators" + its separator only with
    /// <c>CanAdministratePermissions</c>; "Delete playlist" for an owner. Both need a live Spotify edit path and no
    /// notice (item 58: absent under <c>--fake</c>).</summary>
    static void AppendOwnerItems(List<MenuFlyoutItem> rows, Playlist p, IOverlayService? overlay)
    {
        if (!p.IsOwner || !p.Live || !EditsLiveNow()) return;
        if (p.CanAdministratePermissions && !Controls.IsNullOverlay(overlay))
        {
            Actions.Menu.OpenGroup(rows);
            var svc = overlay;
            rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.Edit.InviteCollaborators), Icons.Friends, true,
                () => OpenAccessPanel(svc, p, null, FlyoutPlacement.BottomEdgeAlignedRight)));
        }
        Actions.Menu.OpenGroup(rows);
        rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.Edit.DeletePlaylist), Icons.Delete, true, () => ConfirmDelete(p, overlay)));
    }

    /// <summary>"Delete this playlist? …" then the rootlist remove and home (item 31).</summary>
    static void ConfirmDelete(Playlist p, IOverlayService? overlay)
    {
        string label = Loc.Get(Strings.Detail.Edit.DeletePlaylist);
        if (!Controls.IsNullOverlay(overlay))
            Controls.Confirm(overlay, label, Loc.Get(Strings.Detail.Edit.DeletePlaylistConfirm), label, () => Spotify.PlaylistEdits.Delete(p));
        else if (Actions.Services.Confirm is { } confirm)
            confirm(new ConfirmRequest(Strings.Detail.Edit.DeletePlaylist, Strings.Detail.Edit.DeletePlaylistConfirm,
                                       Strings.Detail.Edit.DeletePlaylist, () => Spotify.PlaylistEdits.Delete(p)));
    }

    /// <summary>The ONE deposit submenu shape (<c>Actions.Menu.Deposit</c>) over <see cref="DepositTargetsFor"/>'s order:
    /// New playlist · ≤ 10 playlists · — · More playlists… (the picker over the SAME order, W19).</summary>
    static MenuFlyoutItem DepositMenu(string title, Track[] tracks, EntityUri exclude, IOverlayService? overlay)
    {
        var arts = new List<string?>();
        var ordered = DepositTargetsFor(exclude, arts);
        var writes = Sidebar.LibraryWrites;
        bool canAdd = tracks.Length > 0 && writes?.DepositTracks is not null && writes.CreatePlaylistWith is not null;
        var payload = new DragPayload(DragKind.Track, "", "", "", Tracks: tracks);
        Action<Actions.Menu.DepositTarget> deposit = target => Sidebar.LibraryWrites?.DepositTracks?.Invoke(target.Uri.Text, target.Name, payload);
        Action create = () => Sidebar.LibraryWrites?.CreatePlaylistWith?.Invoke(payload);
        Action? more = !canAdd || Controls.IsNullOverlay(overlay) ? null : () => OpenPicker(overlay, title, ordered, arts, deposit, create);
        return Actions.Menu.Deposit(title, Icons.Add, canAdd, ordered, deposit, create, more);
    }

    /// <summary>W19 in the platform's picker dialog: "New playlist" pinned first, then the rows with their art (a cover-less
    /// playlist's row takes its FIRST member cover — the dialog's rows carry one url, see the report's gaps).</summary>
    static void OpenPicker(IOverlayService overlay, string title, List<Actions.Menu.DepositTarget> ordered, List<string?> arts,
                           Action<Actions.Menu.DepositTarget> deposit, Action create)
    {
        const string NewKey = "new";
        var items = new Actions.PickerItem[ordered.Count + 1];
        items[0] = new Actions.PickerItem(NewKey, Loc.Get(Strings.Detail.NewPlaylist), Glyph: Icons.Add, Pinned: true, Plated: true);
        for (int i = 0; i < ordered.Count; i++)
        {
            var target = ordered[i];
            bool collaborator = Entities.Current.Playlists.TryGetSlot(target.Uri.Id, out int s) && new Playlist(s) is { IsOwner: false } pl && pl.Editable;
            items[i + 1] = new Actions.PickerItem(i.ToString(System.Globalization.CultureInfo.InvariantCulture), target.Name,
                Subtitle: collaborator ? Loc.Get(Strings.Detail.Collaborator) : null, ArtUrl: i < arts.Count ? arts[i] : null);
        }
        Actions.OpenPicker(overlay, new Actions.PickerSpec(title, items, pick =>
        {
            if (pick.Key == NewKey) { create(); return; }
            if (int.TryParse(pick.Key, out int index) && (uint)index < (uint)ordered.Count) deposit(ordered[index]);
        })
        {
            Placeholder = Loc.Get(Strings.Detail.FindPlaylist),
            EmptyText = Loc.Get(Strings.Detail.NoPlaylists),
            PanelPadding = 8f,
        });
    }

    /// <summary>ch 01 item 42 / ch 06 §0.14: the playlists a deposit can land in, MOST-RECENTLY-FILED FIRST then rootlist
    /// order (<see cref="PlaylistDepositTargets.Order"/> over the account's rootlist and the persisted MRU), the source
    /// excluded; <paramref name="arts"/> receives each row's art in step. The track menu's shared patch calls this.</summary>
    public static List<Actions.Menu.DepositTarget> DepositTargetsFor(EntityUri exclude, List<string?> arts)
    {
        var list = new List<Actions.Menu.DepositTarget>();
        var me = User.Me;
        if (me.Slot <= Table.None) return list;
        var slots = me.RootlistSlots;
        var edges = me.Rootlist;
        int n = Math.Min(slots.Length, edges.Length);
        var candidates = new List<DepositCandidate>(n);
        for (int i = 0; i < n; i++)
        {
            if (edges[i].Kind != (byte)RootlistKind.Item || slots[i] <= Table.None) continue;
            var p = new Playlist(slots[i]);
            if (!p.IsValid || p.Uri.Provider != EntityProvider.Spotify) continue;
            candidates.Add(new DepositCandidate(p.Uri.Text, Entities.Strings.Resolve(p.TitleId), p.Editable, p.Slot));
        }
        var recents = PlaylistDepositTargets.Parse(Platform.Settings.Get(Platform.Keys.PlaylistDepositRecents));
        var ordered = PlaylistDepositTargets.Order(candidates, recents, exclude.IsValid ? exclude.Text : null);
        var tiles = new string[1];
        foreach (var c in ordered)
        {
            var p = new Playlist(c.Slot);
            list.Add(new Actions.Menu.DepositTarget(p.Uri, c.Name));
            string? art = Controls.ArtUrl(p.ImageId);
            if (art is null && MosaicTiles(p, tiles) > 0) art = tiles[0];
            arts.Add(art);
        }
        return list;
    }

    /// <summary>Record a successful deposit in the MRU — written only when the serialized list actually changed, so
    /// re-filing into the front playlist costs no settings write (ch 06 §6.3).</summary>
    public static void RememberDeposit(string uri)
    {
        string current = Platform.Settings.Get(Platform.Keys.PlaylistDepositRecents) ?? "";
        string next = PlaylistDepositTargets.Serialize(PlaylistDepositTargets.Remember(PlaylistDepositTargets.Parse(current), uri));
        if (!string.Equals(current, next, StringComparison.Ordinal)) Platform.Settings.Set(Platform.Keys.PlaylistDepositRecents, next);
    }

    // ══ 4. THE PLAYLIST VERBS (ActionServices rows the sidebar, cards and queue menus ask for) ═══════════════════════

    static void RegisterActions()
    {
        AppActions.Register(new AppAction
        {
            Id = ActionId.RenamePlaylist, IconKey = ActionIcons.Rename,
            Label = static _ => Loc.Get(Strings.Menu.Rename),
            IsEnabled = static c => ActionOverlay is not null && TargetPlaylist(c) is { } p && p.EditableMetadata && Spotify.PlaylistEdits.CanWrite(out _, out _),
            Execute = static c =>
            {
                if (TargetPlaylist(c) is not { } p || ActionOverlay is not { } overlay) return;
                string current = TitleOf(p);
                Controls.Prompt(overlay, Loc.Get(Strings.Menu.Rename), Loc.Get(Strings.Detail.Edit.Save), current, typed =>
                {
                    string next = (typed ?? "").Trim();
                    if (next.Length > 0 && !string.Equals(next, current, StringComparison.Ordinal))
                        Spotify.PlaylistEdits.Rename(p, next, current, null);
                });
            },
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.TogglePlaylistPublic, IconKey = ActionIcons.Globe,
            Label = static c => Loc.Get(TargetPlaylist(c) is { IsPublic: true } ? Strings.Menu.MakePrivate : Strings.Menu.MakePublic),
            IsEnabled = static c => TargetPlaylist(c) is { } p && p.EditableMetadata && Spotify.PlaylistEdits.CanWrite(out _, out _),
            Execute = static c => { if (TargetPlaylist(c) is { } p) Spotify.PlaylistEdits.SetVisibility(p, !p.IsPublic); },
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.InviteCollaborators, IconKey = ActionIcons.People,
            Label = static _ => Loc.Get(Strings.Detail.Edit.InviteCollaborators),
            IsEnabled = static c => ActionOverlay is not null && TargetPlaylist(c) is { } p && p.IsOwner && p.Live && p.CanAdministratePermissions
                                    && Spotify.PlaylistEdits.CanWrite(out _, out _),
            Execute = static c =>
            {
                if (TargetPlaylist(c) is { } p && ActionOverlay is { } overlay) OpenAccessPanel(overlay, p, null, FlyoutPlacement.BottomEdgeAlignedRight);
            },
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.DeletePlaylist, IconKey = ActionIcons.Delete, Destructive = true,
            Label = static _ => Loc.Get(Strings.Detail.Edit.DeletePlaylist),
            IsEnabled = static c => TargetPlaylist(c) is { } p && p.IsOwner && Spotify.PlaylistEdits.CanWrite(out _, out _)
                                    && (ActionOverlay is not null || c.S.Confirm is not null),
            Execute = static c => { if (TargetPlaylist(c) is { } p) ConfirmDelete(p, ActionOverlay); },
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.AddToPlaylist, IconKey = ActionIcons.Add,
            Label = static _ => Loc.Get(Strings.Detail.AddToPlaylist),
            IsEnabled = static c => c.Target.Count > 0 && ActionOverlay is not null && Sidebar.LibraryWrites?.DepositTracks is not null,
            Execute = static c => PickAndDeposit([.. c.Target.Tracks], default),
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.AddContextToPlaylist, IconKey = ActionIcons.Add,
            Label = static _ => Loc.Get(Strings.Detail.AddToPlaylist),
            IsEnabled = static c => c.Target.Uri.IsValid && ActionOverlay is not null && Sidebar.LibraryWrites?.ResolveTracks is not null,
            Execute = static c =>
            {
                if (Sidebar.LibraryWrites?.ResolveTracks is not { } resolve) return;
                var uri = c.Target.Uri;
                resolve(uri.Text, CancellationToken.None).ContinueWith(t =>
                {
                    Track[] tracks = t.IsCompletedSuccessfully ? t.Result : [];
                    Spotify.Post(() => PickAndDeposit(tracks, uri));
                }, TaskScheduler.Default);
            },
        });
    }

    /// <summary>A playlist-kind target's row (a sidebar/card container target, or a track set's host).</summary>
    static Playlist? TargetPlaylist(in ActionContext c)
    {
        var uri = c.Target.Kind == TargetKind.Playlist ? c.Target.Uri : c.Target.Host.Playlist;
        if (!uri.IsValid || uri.Kind != EntityKind.Playlist) return null;
        return Entities.Current.Playlists.TryGetSlot(uri.Id, out int slot) ? new Playlist(slot) : null;
    }

    static void PickAndDeposit(Track[] tracks, EntityUri exclude)
    {
        if (tracks.Length == 0)
        {
            Notify.Say(Loc.Get("library.nothingToAdd"), InfoBarSeverity.Informational, dedupeKey: "library.nothing-to-add");
            return;
        }
        if (ActionOverlay is not { } overlay) return;
        var arts = new List<string?>();
        var ordered = DepositTargetsFor(exclude, arts);
        var payload = new DragPayload(DragKind.Track, "", "", "", Tracks: tracks);
        OpenPicker(overlay, Loc.Get(Strings.Detail.AddToPlaylist), ordered, arts,
            target => Sidebar.LibraryWrites?.DepositTracks?.Invoke(target.Uri.Text, target.Name, payload),
            () => Sidebar.LibraryWrites?.CreatePlaylistWith?.Invoke(payload));
    }

    // ══ 5. LOCAL FILES (G-110; ch 15 §11; 0.2.9 LocalFileActions) ════════════════════════════════════════════════════

    /// <summary>Give the Local Files row its header when nobody has (0.2.9 <c>LocalSource</c>): "Local Files", owned, items
    /// and metadata editable. SEED authority, so the fake seed or a real local source always wins.</summary>
    public static void EnsureLocalFilesHeader()
    {
        var local = LocalFiles;
        if (local.Knows(PlaylistFields.Identity)) return;
        var scope = Entities.Current;
        var staging = Staging.Rent();
        staging.Epoch = scope.Epoch;
        ref var row = ref staging.Playlists.RowFor(new StagedId(staging.AddText(Encoding.UTF8.GetBytes(LocalFilesUri))), Authority.Seed,
            (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities));
        row.Title = staging.AddText(Encoding.UTF8.GetBytes(Loc.Get("localFile.playlistTitle")));
        row.Description = staging.AddText(Encoding.UTF8.GetBytes(Loc.Get("localFile.playlistDescription")));
        row.Caps = (byte)(PlaylistCaps.CanView | PlaylistCaps.CanEditItems | PlaylistCaps.CanEditMetadata | PlaylistCaps.IsOwner);
        Entities.Commit(staging);
        Staging.Return(staging);
        Entities.Publish();
    }

    /// <summary>Import one file into the Local Files membership: a COMPLETE row at construction (0.2.9 <c>ForLocalFile</c>:
    /// the file name as its title, availability stated, no album, no artists), appended to the local list.</summary>
    public static Track ImportLocalFile(string path)
    {
        var scope = Entities.Current;
        string uri = LocalFileUri(path);
        var staging = Staging.Rent();
        staging.Epoch = scope.Epoch;
        ref var row = ref staging.Tracks.RowFor(new StagedId(staging.AddText(Encoding.UTF8.GetBytes(uri))), Authority.Local,
            (uint)(TrackFields.Identity | TrackFields.Availability));
        row.Title = staging.AddText(Encoding.UTF8.GetBytes(LocalTitleOf(path)));
        row.Flags = (uint)TrackFlags.Local;
        Entities.Commit(staging);
        Staging.Return(staging);
        if (!scope.Tracks.TryGetSlot(uri.AsSpan(), out int slot)) return default;

        EnsureLocalFilesHeader();
        var local = LocalFiles;
        var e = scope.Edges.PlaylistTracks;
        if (e.State(local.Slot) == EdgeState.Unknown)
            e.Replace(local.Slot, ReadOnlySpan<int>.Empty, ReadOnlySpan<PlaylistTrackEdge>.Empty, EdgeState.Complete, 0);
        e.Insert(local.Slot, slot, new PlaylistTrackEdge(default, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0, 0, 0, 0, 0));
        local.Refold();
        Entities.Publish();
        return new Track(slot);
    }

    /// <summary><c>Shell.OnFilesDropped</c> (0.2.9 <c>LocalFileActions.PlayDropped</c>): audio wins over video; nothing
    /// playable is an Error toast; no local playback wired is Informational; an .mp4 attaches to its OWN row first.</summary>
    static void PlayFiles(IReadOnlyList<string> paths)
    {
        var action = ClassifyDrop(paths, out string path);
        if (action == LocalDropAction.None) { Notify.Say(Loc.Get(Strings.LocalFile.Rejected), InfoBarSeverity.Error); return; }
        if (Playback.Audio.LocalPath is null) { Notify.Say(Loc.Get(Strings.LocalFile.NotReady), InfoBarSeverity.Informational); return; }
        var track = ImportLocalFile(path);
        if (!track.IsValid) { Notify.Say(Loc.Get(Strings.LocalFile.Rejected), InfoBarSeverity.Error); return; }
        if (action == LocalDropAction.PlayVideo && Video.Overrides.Attach(track.Uri.Text, path, path, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) is null)
        {
            Notify.Say(Loc.Get(Strings.LocalFile.Rejected), InfoBarSeverity.Error);
            return;
        }
        ReadOnlySpan<EntityRef> rows = [new EntityRef(EntityKind.Track, track.Slot)];
        Playback.PlayRows(rows, 0, LocalFiles.Id);
    }

    /// <summary><c>Shell.PickAndPlayFile</c> (0.2.9 <c>LocalFileActions.PickAndPlay</c>): one playable-files row in the
    /// system picker, then the drop path.</summary>
    static void PickAndPlay()
    {
        string? picked;
        try
        {
            picked = FilePicker.OpenFile(FluentApp.WindowHandle, Loc.Get(Strings.LocalFile.PickTitle),
                Video.OverrideUx.PlayableFilter(Loc.Get(Strings.LocalFile.Filter)));
        }
        catch (Exception ex) { Log.Warn("localfile", "file picker failed", ex); return; }
        if (picked is { Length: > 0 }) PlayFiles([picked]);
    }
}
