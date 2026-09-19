// ── Entities/Album.Pane.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the library master-detail's right column for an album: ONE anatomy at every width (ch 15 §0 amendment 2026-09-17),
// the whole model demanded on mount, four-state readiness (Header · Rows · Failed · Ready), the counted shimmer, and
// the "Also by … in your library" strip.
//
// Role: UI
// Owner: M
// Wave: L2
// Budget: 320 lines (+30 % = 416); actual 498 — the overrun is comment, not machinery: the 2026-09-18 defect pass added
//         four short members (the credits demand, the one-walk row facts, the trailing thunk, the state log) and the
//         notes that say which complaint each one answers.
// Spec: library rework §5.5
//
// THE RULE THAT KILLS "SKELETON FOREVER": the header paints from AlbumFields.Identity, which the navigator already
// demanded for every row it shows — the pane has nothing header-shaped it can wait for. Only the rows load, they load
// as a COUNTED shimmer (TrackCount rows), and a failed edge or a failed row batch is a strip with Retry, never a
// skeleton. It asks AlbumFields.Detail (never Publishing) — ch 05 §9's warning about the Full rung stands: this surface
// does not grow an "About this release" panel. Its one trailing band is the "Also by" strip, and it is a band rather
// than a sibling for geometry, not for content (point 4 below).
//
// ── HOW IT FEELS (the three complaints this file answers) ────────────────────────────────────────────────────────────
//
//  1. SELECTION IS A CROSSFADE, NOT A CUT. The component PERSISTS (the page re-pushes `PaneProps`, whose equality
//     ignores the callback) and its render returns a ZStack host whose single child is keyed by slot — a key change is a
//     real unmount, so the engine orphans the leaving pane and fades it UNDER the entering one, which rises 6 DIP in
//     (`s_paneSwap`, after Concert.Page.cs's `s_presence`). Nothing reads a frame clock; reduced motion is the engine's.
//  2. LOADING → READY IS A CROSS-DISSOLVE, not a swap. The rows area is one `Skel.Region`: pending ⇒ the engine paints
//     and breathes the DERIVED shimmer of the counted rows; Ready ⇒ it mounts the real table and blur-reveals it row by
//     row while the shimmer fades out beneath. The two `Loadable<int>` are CACHED, one per state, because the region's
//     thunks close over the instance we hand it — a fresh one per render would re-arm the boundary instead of flipping it.
//  3. THE HEADER NEVER SKELETONS once the navigator knows the row: only `AlbumPaneState.Header` paints bars, and the real
//     header paints at once, its art arriving through `Controls.Artwork`'s own image reveal. It is also one STATED height
//     (`Album.PaneHeaderHeight`) in both states, so a two-line title cannot push the tracklist down.
//  4. THE PANE SCROLLS ONCE. Header and commands are fixed; everything below — the column header, the rows, then the
//     "Also by" strip — is the embedded table's own trailing arm: ONE scroller, one scrollbar, one edge fade. The strip
//     is therefore handed to the table as `TableArgs.Trailing` instead of being laid out beside it, which is what used to
//     trap a 5-track EP in a half-height inner viewport with the strip pinned underneath.

using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Album
{
    /// <summary>Re-pushed props: the selection changes → the pane re-skins in place (no remount, no lost scroll).
    /// <paramref name="OnAlsoBy"/> is behaviour (ignored by equality): a tile click hands the page a slot to select.</summary>
    public sealed record PaneProps(int Slot, bool ShowAlsoBy, Action<int>? OnAlsoBy = null)
    {
        public bool Equals(PaneProps? other) => other is not null && other.Slot == Slot && other.ShowAlsoBy == ShowAlsoBy;
        public override int GetHashCode() => HashCode.Combine(Slot, ShowAlsoBy);
    }

    public sealed class Pane : Component
    {
        /// <summary>The selection swap: the leaving pane fades out as an exit orphan (drawn UNDER the live tree) while the
        /// entering one rises 6 DIP into place. Opacity only — a page-sized subtree is the blur-group perf cliff, and the
        /// fade alone reads as the dissolve. Faster out than in, so the new album never waits for the old one.</summary>
        static readonly LayoutTransition s_paneSwap = new(
            TransitionChannels.Opacity, TransitionDynamics.Tween(180f, Easing.SmoothOut),
            Enter: new EnterExit(Dy: 6f, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
            ExitDynamics: TransitionDynamics.Tween(120f, Easing.SmoothOut));

        /// <summary>The strip's tile count ceiling: the allocation and the art decodes at a selection edge stay bounded,
        /// and a 13th tile is past the horizontal scroller's reach anyway.</summary>
        const int AlsoByCap = 12;

        /// <summary>The strip's viewport height: 96 art + 6 + the 16 title line + 6 + the 14 year line. A ScrollEl is a
        /// viewport — it has no content height of its own to grow from, so every horizontal rail states one.</summary>
        const float AlsoByTileH = 96f + 6f + 16f + 6f + 14f;

        int _slot, _shimRows, _keySlot = int.MinValue;
        uint _keyEpoch;
        float _rowH = Track.RowMetrics.RowHeight;
        bool _showAlsoBy;
        string _paneKey = "", _skelKey = "", _saveKey = "", _scrollKey = "", _uri = "";
        Action<int>? _onAlsoBy;
        IOverlayService? _overlay;
        int[] _also = new int[16];
        /// <summary>The rows' credited artists, deduped, reused across demands — see <see cref="DemandCredits"/>.</summary>
        int[] _credits = new int[16];
        /// <summary>The last (album, state) this pane put in the log, so the always-on line marks TRANSITIONS and a
        /// steady pane logs nothing (int.MinValue is not a slot, so the first verdict always prints).</summary>
        int _loggedSlot = int.MinValue;
        AlbumPaneState _loggedState;

        readonly Signal<int> _retryEpoch = new(0);
        readonly Action _demand, _demandRows, _play, _shuffle, _open, _retry;
        readonly Func<ContextMenuModel?> _menu;
        readonly Func<Element> _shimmer, _trailing;
        readonly Func<int, Element> _rows;
        readonly Action<int> _alsoBy;
        // One per state, never mutated: the region reads the State signal of the instance it was handed (§5.5 note 2).
        readonly Loadable<int> _pending = Loadable<int>.Pending();
        readonly Loadable<int> _ready = Loadable<int>.Ready(0);

        public Pane()
        {
            _demand = () =>
            {
                var a = new Album(_slot);
                if (!a.IsValid) return;
                Entities.Ensure(a, AlbumFields.Detail);
                var edge = Entities.Current.Edges.AlbumTracks;
                if (edge.State(_slot) == EdgeState.Unknown && !edge.IsFailed(_slot)) Entities.EnsureEdge(FetchEdge.AlbumTracks, _slot);
                // THE SELECTION EDGE asks for the rows too. `_demandRows` below is auto-tracked on table signals and reads
                // `_slot` as a plain field, so a selection change re-runs it ONLY if one of those tables happens to
                // publish afterwards. An album whose tracks edge is ALREADY Complete (a persisted list, a list another
                // surface fetched) publishes nothing — its rows were never asked for, and the pane shimmered until the
                // page was refreshed (`album.pane.state state=1 edge=2 untitled=1`, 2026-09-18). This effect is keyed on
                // the slot, so it is the one place guaranteed to run once per selection.
                DemandRowsNow();
            };
            _demandRows = () =>
            {
                _ = _retryEpoch.Value;                                     // Retry re-arms the row batch
                _ = Entities.ScopeEpoch.Value;
                var scope = Entities.Current;
                _ = scope.Edges.AlbumTracks.Changed.Value;
                _ = scope.Edges.TrackArtists.Changed.Value;                // a row's credits landing re-asks for their names
                _ = scope.Artists.Changed.Value;
                DemandRowsNow();
            };
            _play =() => { var a = new Album(_slot); if (a.IsValid) Playback.PlayContext(a.Id); };
            _shuffle = () => { var a = new Album(_slot); if (!a.IsValid) return; Playback.SetShuffle(true); Playback.PlayContext(a.Id); };
            _open = () => { var a = new Album(_slot); if (a.IsValid) Shell.GoTo(Shell.For(a.Uri, a.Title)); };
            _retry = () =>
            {
                // The edge first (a failed list is what the strip is about), then the epoch: the row demand re-runs on
                // it and the planner clears Table.Failed as each group is re-asked, so the verdict re-derives itself.
                if (Entities.Current.Edges.AlbumTracks.IsFailed(_slot)) Entities.RefreshEdge(FetchEdge.AlbumTracks, _slot);
                _retryEpoch.Value = _retryEpoch.Peek() + 1;
            };
            _menu = () => { var a = new Album(_slot); return a.IsValid ? MoreMenu(a, _overlay) : null; };   // the page's hero menu, lifted (§5.0)
            _shimmer = CountedShimmer;
            _trailing = AlsoByTrailing;
            _rows = RowsTable;
            _alsoBy = slot => _onAlsoBy?.Invoke(slot);
        }

        /// <summary>The row demand for the CURRENT <see cref="_slot"/>: row fields for every listed track, the next page of
        /// a Partial list, and the credited artists' names. Called from the slot-keyed <c>_demand</c> (once per selection)
        /// and from the auto-tracked <c>_demandRows</c> (as lists and credits land). Idempotent — a satisfied
        /// <c>Ensure</c> plans nothing.</summary>
        void DemandRowsNow()
        {
            var scope = Entities.Current;
            var a = new Album(_slot);
            if (!a.IsValid) return;
            var slots = a.TrackSlots;
            if (slots.Length > 0)
                Entities.Ensure(MemoryMarshal.Cast<int, Track>(slots), TrackFields.Row | TrackFields.Audio | TrackFields.Tags | TrackFields.Video);
            // PAGE A PARTIAL LIST — the full page's own rule (Album.Page.cs:362). `_demand` only asks an UNKNOWN edge (an
            // ask that failed must not re-arm itself), so without this a release whose first page answered stayed Partial
            // forever. `_demandRows` reads the edge's Changed, so each landed page asks for the next one from the count it
            // now has, and Complete ends it.
            var edge = scope.Edges.AlbumTracks;
            if (edge.State(_slot) == EdgeState.Partial) Entities.EnsureEdge(FetchEdge.AlbumTracks, _slot, edge.Count(_slot));
            DemandCredits(scope, slots);
        }

        public override Element Render()
        {
            var p = UseProps<PaneProps>();
            _overlay = UseContext(Overlay.Service);
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Albums.Changed.Value; _ = scope.Tracks.Changed.Value; _ = scope.Artists.Changed.Value;
            _ = scope.Edges.AlbumTracks.Changed.Value; _ = scope.Edges.AlbumArtists.Changed.Value; _ = scope.Edges.SavedAlbums.Changed.Value;
            int retry = _retryEpoch.Value;                                 // read HERE: a keyed effect does not track
            _slot = p.Slot; _onAlsoBy = p.OnAlsoBy; _showAlsoBy = p.ShowAlsoBy;
            UseEffect(_demand, DepKey.From(p.Slot, (int)epoch, retry, 0));  // the whole model, once per album per scope
            UseEffect(_demandRows);
            Keys(epoch);

            var a = new Album(_slot);
            if (!a.IsValid) return new BoxEl();                             // the page owns the no-selection vacancy (W5)

            var edge = scope.Edges.AlbumTracks;
            var tracks = MemoryMarshal.Cast<int, Track>(a.TrackSlots);
            var edgeState = edge.State(_slot);
            // The RICH overload (User.cs §9b): a row with no TITLE still gates, a row whose credited artist has no NAME
            // does not. The five-bool shape folded the two into one "unnamed" flag, and since nothing on this surface
            // demanded a credit's name that flag could never clear — the pane shimmered forever. The credit is a NOTICE
            // now (below), which is shown over a rendered list rather than instead of one.
            var facts = RowFactsOf(a);
            var state = AlbumPaneReadiness.Of(
                knowsIdentity: a.Knows(AlbumFields.Title),
                tracks: edgeState, edgeFailed: edge.IsFailed(_slot), rows: facts);
            _rowH = Track.RowMetrics.RowHeightFor(Prefs.Appearance.RowDensity());
            _shimRows = AlbumPaneReadiness.ShimmerRows(a.Knows(AlbumFields.TrackCount), a.TrackCount, tracks.Length);
            LogState(state, edgeState, tracks.Length, in facts);

            BoxEl child = state == AlbumPaneState.Header ? HeaderSkeleton() : Body(a, state);
            // The host persists; only the child is keyed, so the swap is an orphaned fade under a rising entrance
            // instead of a remount of this component (which would lose the table's scroll and re-ask the model).
            return new BoxEl
            {
                ZStack = true, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true,
                Children = [child with { Key = state == AlbumPaneState.Header ? _skelKey : _paneKey, Animate = s_paneSwap }],
            };
        }

        BoxEl Body(Album a, AlbumPaneState state)
        {
            var id = Detail.Identity.For(a);
            Element rows = state == AlbumPaneState.Failed
                // The Error voice supplies its own subtitle and Retry label; only the headline is ours.
                ? Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact,
                    title: Loc.Get(Strings.Library.SongsFailed), onAction: _retry)
                // smoothResize off: the rows area is a fixed flex slot (both branches Grow), so there is no height to
                // ease — and marking a pane-sized region BoundsAnimated would put a Reflow pass on every swap.
                : Skel.Region(state == AlbumPaneState.Ready ? _ready : _pending, _shimmer, _rows,
                    SkelReveal.StaggerRows, smoothResize: false);
            return new BoxEl
            {
                Direction = 1, Grow = 1f, ClipToBounds = true,
                Children =
                [
                    PaneHeader(id.CoverUrl, EyebrowOf(a), id.Title, _open, Detail.ArtistLine(id.Artists), MetaOf(a, id)),
                    PaneCommands(_play, _shuffle,
                        Embed.Comp(() => new Controls.SaveButton { Uri = _uri, Name = id.Title, Box = 36f }) with { Key = _saveKey },
                        Detail.MoreButton(_menu, 36f, 16f, round: true), _open),
                    // The unnamed-credit verdict goes through the page's OWN pipeline rather than the readiness gate:
                    // one strip over a rendered list, and — exactly as on the full page (Detail.UI.cs:572) — suppressed
                    // for `MinifiedAlbum`, which self-heals when the credits land. Zero-height and hit-test free while
                    // there is nothing to say, so the rows below it never move.
                    Detail.NoticeBar(Detail.NoticeRules.ForAlbum(MemoryMarshal.Cast<int, Track>(a.TrackSlots)),
                                     showMinifiedAlbum: false, goLibrary: null),
                    // ONE flex slot, in every state: the shimmer, the Retry strip and the real table all grow into it,
                    // so the header and the commands above never move. "Also by" is NOT a sibling here any more — it is
                    // the table's trailing band, inside the table's scroller (see RowsTable).
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true, Children = [rows] },
                ],
            };
        }

        /// <summary>The real rows: the embedded table over THIS album, under its own per-album <c>ScrollKey</c>. The region
        /// hands it the loadable's value, which this surface has no use for — the album is the pane's own state.
        /// <para>The table carries the "Also by" strip as its TRAILING band. That is the whole of the "5-track EP in its
        /// own little scroller" fix: an embedded table with no trailing band is a <c>Grow 1</c> viewport with a scrollbar
        /// of its own (Track.Table.cs:1048), so a 550-DIP pane showed 3½ rows with the strip pinned under them and a
        /// single left a dead gap. With the band handed over, <c>TrailingBody</c> puts the rows and the strip in ONE
        /// scroller and keeps the column header fixed above it — the pane's own header and command row are outside the
        /// table and never move either way.</para></summary>
        Element RowsTable(int _)
        {
            var a = new Album(_slot);
            return Track.Table(new Track.TableArgs
            {
                Source = Track.TableSource.ForAlbum(a),
                Profile = Track.TableProfile.From(ConfigFor(a)),
                ShowToolbar = false, Embedded = true, ScrollKey = _scrollKey, Trailing = _trailing,
            });
        }

        /// <summary>The trailing band, as the table calls it. It reads the scope signals it depends on ITSELF: the thunk
        /// runs inside the table host's render, not this component's, so the strip would otherwise never hear a save
        /// landing — this pane's own subscriptions do not reach it (the host re-renders only when its props change, and
        /// they do not). Cheap: the reads are the same ones this render already makes, and a saved-album edit is rare.</summary>
        Element AlsoByTrailing()
        {
            var scope = Entities.Current;
            _ = Entities.ScopeEpoch.Value;
            _ = scope.Edges.SavedAlbums.Changed.Value; _ = scope.Edges.AlbumArtists.Changed.Value;
            _ = scope.Albums.Changed.Value; _ = scope.Artists.Changed.Value;
            var a = new Album(_slot);
            return _showAlsoBy && a.IsValid ? AlsoBy(a) : new BoxEl();
        }

        /// <summary>The per-slot strings, rebuilt only when the selection (or the scope that renumbers slots) changes —
        /// a pane render allocates no keys of its own.</summary>
        void Keys(uint epoch)
        {
            if (_keySlot == _slot && _keyEpoch == epoch) return;
            _keySlot = _slot; _keyEpoch = epoch;
            var a = new Album(_slot);
            _uri = a.IsValid ? a.Uri.Text : "";
            string n = FormatCache.Int(_slot);
            _paneKey = "pane:" + n; _skelKey = "paneskel:" + n; _saveKey = "save:" + _uri; _scrollKey = "lib:tracks:" + _uri;
        }

        /// <summary>The embedded album profile: no Plays lane (the pane is 784 at its widest and the lane is the first
        /// thing ch 15 §0 drops), and the trailing band KEPT — "Also by" fills it, and keeping it is what buys the pane
        /// one scroller instead of a nested one (<see cref="RowsTable"/>). The embedded arm drops <c>HasTrailing</c> only
        /// for a caller that hands over no band at all (<c>TableHost.ConfigOf</c>), so stating it here is what asks.</summary>
        static Detail.Config ConfigFor(Album a)
            => Detail.Config.For(DetailKind.Album, a.Knows(AlbumFields.Kind) ? a.Kind : AlbumKind.Album) with { ShowPlays = false, HasTrailing = true };

        /// <summary>"ALBUM · 2013": kind label + year when known; the kind alone otherwise.</summary>
        static string EyebrowOf(Album a)
        {
            string kind = Detail.Text.KindLabel(a.Knows(AlbumFields.Kind) ? a.Kind : AlbumKind.Album);
            return a.Knows(AlbumFields.Year) && a.Year > 0 ? kind + " · " + FormatCache.Int(a.Year) : kind;
        }

        /// <summary>"13 songs · 45 min · saved 2024": Identity's meta (songs · duration, the duration dropped while thin —
        /// ch 03 §7) plus the library's AddedAt year when the album is saved.</summary>
        static string MetaOf(Album a, Detail.Identity id)
        {
            string meta = id.Meta
                ?? Detail.Text.AlbumMeta(a.Knows(AlbumFields.TrackCount) ? a.TrackCount : a.TrackSlots.Length, 0L, durationsKnown: false, a.Year)
                ?? "";
            int added = User.Me.IsValid ? User.Me.AddedAt(LibraryEdgeKind.SavedAlbums, a.Slot) : 0;   // UNIX seconds (User.cs:288)
            if (added <= 0) return meta;
            string saved = Strings.Library.SavedIn(DateTimeOffset.FromUnixTimeSeconds(added).Year);
            return meta.Length == 0 ? saved : meta + " · " + saved;
        }

        /// <summary>The two ROW facts the readiness rule takes, in ONE walk: is any listed row still untitled, and did the
        /// batch that would have named these rows terminally fail (<c>Table.Failed</c>, the per-row twin of
        /// <c>EdgeTable.IsFailed</c>)? Read inline rather than memoized: a memo can only invalidate on a SIGNAL, and the
        /// selection arrives as props — it would hand the new album the previous one's verdict. One pass over the
        /// tracklist per pane render, the same pass <see cref="Detail.Identity.For"/> already pays.
        /// <para>The title is asked of the row a relink DISPLAYS (<c>Track.ForDisplay</c>, the rule
        /// <c>Detail.NoticeRules.Unnamed</c> uses): a relinked row's own gid-only record may never grow a title, and
        /// judging it by that record would be another skeleton that cannot end.</para></summary>
        static AlbumRowFacts RowFactsOf(Album a)
        {
            var table = Entities.Current.Tracks;
            var slots = a.TrackSlots;
            bool untitled = false, failed = false;
            for (int i = 0; i < slots.Length && !(untitled && failed); i++)
            {
                if (!new Track(slots[i]).ForDisplay.Knows(TrackFields.Title)) untitled = true;
                if (table.IsFailed(slots[i], (uint)TrackFields.Row)) failed = true;
            }
            return new AlbumRowFacts(untitled, failed);
        }

        /// <summary>The rows' CREDITED artists at <c>ArtistFields.Identity</c> — the ask the full page makes for the
        /// album's billed artists (Album.Page.cs:365), extended to the per-row credits this surface actually paints.
        /// Nobody else demands them, so before this a track's credit line held ids and no names for the life of the pane.
        /// Deduped in place against the reused buffer: an album's rows share a handful of artists, and a duplicate is a
        /// wasted plan entry. One call, not one per row — the planner batches what it is handed (P4).</summary>
        void DemandCredits(Scope scope, ReadOnlySpan<int> tracks)
        {
            int n = 0;
            for (int i = 0; i < tracks.Length; i++)
            {
                var credits = new Track(tracks[i]).ArtistSlots;
                for (int j = 0; j < credits.Length; j++)
                {
                    int slot = credits[j];
                    bool seen = false;
                    for (int k = 0; k < n && !seen; k++) seen = _credits[k] == slot;
                    if (seen) continue;
                    if (n == _credits.Length) Array.Resize(ref _credits, n * 2);
                    _credits[n++] = slot;
                }
            }
            if (n > 0) Entities.Ensure(scope.Artists, _credits.AsSpan(0, n), (uint)ArtistFields.Identity);
        }

        /// <summary>The pane's verdict, in the log, ON TRANSITION — every input the rule took, so "skeleton forever" is a
        /// question the log answers instead of a bisect. Always on (no switch, ch 15's rule), and gated on the (album,
        /// state) pair so a steady pane and a re-render cost a compare, never a line.</summary>
        void LogState(AlbumPaneState state, EdgeState edgeState, int listed, in AlbumRowFacts facts)
        {
            if (_loggedSlot == _slot && _loggedState == state) return;
            _loggedSlot = _slot; _loggedState = state;
            Log.Event(WaveeLogLevel.Info, "ui", "album.pane.state", "Album pane state", null, -1, null,
                WaveeLogField.Of("album", _slot), WaveeLogField.Of("state", (int)state),
                WaveeLogField.Of("edge", (int)edgeState), WaveeLogField.Of("listed", listed),
                WaveeLogField.Of("untitled", facts.AnyUntitled ? 1 : 0),
                WaveeLogField.Of("rowsFailed", facts.AnyFailed ? 1 : 0));
        }

        // ══ THE COUNTED SHIMMER ══════════════════════════════════════════════════════════════════════════════════════
        //
        // This tree is the region's DERIVATION INPUT, never painted as written: the engine maps its leaves to pulsing bars
        // and cross-dissolves the real table over it. Two consequences shape it.
        //  · The header is CHROME, not data (W5 paints the real "#  Title  ♥  ⏱" while the rows shimmer), so it goes to
        //    the deriver as its own override — `Skel(head)` — which returns it verbatim.
        //  · `Track.ShimmerRow` cannot BE the row: its cells are bare `new BoxEl()` (Track.UI.cs:328), the table's
        //    pre-reveal blank — it paints nothing, and the deriver maps a childless box with no declared extent to an
        //    empty spacer, so the pane would load as a void (the complaint itself). The row below is that row's EXTENT —
        //    the same tracks, `ColGapFor`/`PadXFor(EmbeddedTier)` and pitch — with three cells that declare a bar.

        Element CountedShimmer()
        {
            var head = Track.TableHeaderShim(compact: true);                // pays its own PadXFor: no wrapper padding
            var kids = new Element[_shimRows + 1];
            kids[0] = head.Skel(head);
            for (int i = 0; i < _shimRows; i++) kids[i + 1] = ShimRow(_rowH, i);
            return new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true, Children = kids };
        }

        /// <summary>One placeholder row at the real row's pitch and lanes: the # bar, the title bar (four lengths in
        /// rotation, so a column of rows reads as text and not as a fence) and the clock bar. The ♥ and "…" lanes hold
        /// nothing — they carry no content to promise.</summary>
        static Element ShimRow(float rowH, int i)
        {
            var set = Track.EmbeddedColumns;
            var tracks = Track.EmbeddedTracks;
            var cells = new Element[tracks.Length];
            int k = 0;
            cells[k++] = Cell(Bar(14f), FlexJustify.Center);
            if (set.Heart && k < cells.Length) cells[k++] = new BoxEl();
            if (k < cells.Length) cells[k++] = Cell(Bar(TitleBarWidth(i)), FlexJustify.Start);
            if (k < cells.Length) cells[k++] = Cell(Bar(30f), FlexJustify.End);
            while (k < cells.Length) cells[k++] = new BoxEl();
            float padX = Track.RowMetrics.PadXFor(set.Tier);
            return new GridEl
            {
                Key = "sh:" + FormatCache.Int(i),
                // NOT Grow (Track.ShimmerRow's own Grow is for a ROW-direction slot): in this column it would hand each
                // row a share of the slack and the pitch would stop matching the real rows'. Shrink 0 for the same reason
                // the other way — more rows than fit overflow into the clip, they never squeeze.
                Columns = tracks, ColGap = Track.RowMetrics.ColGapFor(set.Tier), RowHeight = rowH, Shrink = 0f,
                Padding = new Edges4(padX, 0f, padX, 0f), Children = cells,
            };
        }

        static float TitleBarWidth(int i) => (i & 3) switch { 0 => 208f, 1 => 264f, 2 => 176f, _ => 232f };

        static Element Bar(float w) => new BoxEl { Width = w, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary };

        static Element Cell(Element bar, FlexJustify justify) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, MinWidth = 0f, ClipToBounds = true, Children = [bar],
        };

        /// <summary>The whole-header placeholder, painted ONLY while the navigator's own identity demand is still in
        /// flight (<see cref="AlbumPaneState.Header"/>) — short-lived by construction, and it crossfades like any pane.</summary>
        static BoxEl HeaderSkeleton() => new()
        {
            // The real header's STATED height (Album.UI.cs), not this tree's content height: the skeleton and the header
            // it crossfades into must occupy the same band, or the crossing is a reflow wearing a fade.
            Direction = 0, Gap = 18f, AlignItems = FlexAlign.End, Height = PaneHeaderHeight, ClipToBounds = true,
            Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.XL, Spacing.M),
            Children =
            [
                new BoxEl { Width = PaneCover, Height = PaneCover, Shrink = 0f, Corners = Radii.CardAll, Fill = Tok.FillSubtleSecondary },
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children = [Bar(80f), new BoxEl { Width = 220f, Height = 26f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary }, Bar(120f)],
                },
            ],
        };

        // ══ "ALSO BY … IN YOUR LIBRARY" ══════════════════════════════════════════════════════════════════════════════

        /// <summary>The other saved albums billed to this album's FIRST billed artist, as 96-px tiles (art r 4 · title
        /// 12.5 · year 11.5) in the saved edge's own order, each entering on the staggered cascade. Absent when there are
        /// none. A tile selects IN PLACE through the page's callback — the one library-internal jump that stays.</summary>
        Element AlsoBy(Album a)
        {
            var billed = a.ArtistSlots;
            if (billed.Length == 0) return new BoxEl();
            int artist = billed[0];
            int n = User.LibraryAlbumsOf(artist, _also);
            if (n > _also.Length) { _also = new int[n]; n = User.LibraryAlbumsOf(artist, _also); }   // grows at a selection edge only
            var tiles = new List<Element>(Math.Min(n, AlsoByCap));
            for (int i = 0; i < n && tiles.Count < AlsoByCap; i++)
            {
                int slot = _also[i];
                if (slot == a.Slot) continue;
                tiles.Add(Tile(new Album(slot), tiles.Count));
            }
            if (tiles.Count == 0) return new BoxEl();
            return new BoxEl
            {
                Direction = 1, Gap = 10f, Shrink = 0f,
                Padding = new Edges4(Spacing.XL, Spacing.S, Spacing.XL, Spacing.XL),
                Children =
                [
                    Design.Type.Eyebrow(Strings.Library.AlsoBy(new Artist(artist).Name)) with { Color = Tok.TextTertiary },
                    // The house shape for a horizontal rail: a stated height, Grow 0, and the edge fade instead of a
                    // scrollbar rail under 96-px art (Concert.Page.cs:736, Sidebar.UI.LibraryV3.cs:1389).
                    ScrollView(new BoxEl { Direction = 0, Gap = Spacing.M, Children = tiles.ToArray() }, horizontal: true) with
                    {
                        Grow = 0f, Shrink = 0f, Height = AlsoByTileH, SuppressScrollBar = true,
                        AutoEdgeFade = true, AutoEdgeFadeBand = 24f,
                    },
                ],
            };
        }

        Element Tile(Album o, int i)
        {
            int slot = o.Slot;
            return new BoxEl
            {
                // 96 exactly, no inset: the wash is the tile's own rounded rect, so the art stays on the strip's grid and
                // the first tile lines up with the eyebrow above it.
                Width = 96f, Direction = 1, Gap = 6f, Shrink = 0f, Corners = Radii.ControlAll,
                HoverScale = Design.Motion.ScaleStandard.Hover, PressScale = Design.Motion.ScaleStandard.Press,
                Animate = Design.Entrance.Row(i),
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = () => _alsoBy(slot),
                Children =
                [
                    new BoxEl
                    {
                        Width = 96f, Height = 96f, Shrink = 0f, Corners = Radii.ControlAll, ClipToBounds = true,
                        Children = [Controls.Artwork(Controls.ArtUrl(o.ImageId), 96f, 96f, Radii.Control, decodePx: 192)],
                    },
                    new TextEl(o.Title)
                    {
                        Size = 12.5f, LineHeight = 16f, Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary,
                        BrushTransitionMs = Design.Motion.Faster, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                    new TextEl(o.Knows(AlbumFields.Year) && o.Year > 0 ? FormatCache.Int(o.Year) : "")
                        { Size = 11.5f, LineHeight = 14f, Color = Tok.TextTertiary },
                ],
            }.Interactive(Interaction.Subtle);
        }
    }
}
