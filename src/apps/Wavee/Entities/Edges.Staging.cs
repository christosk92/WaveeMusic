// ── Entities/Edges.Staging.cs — CORE (owner B, wave 1 (gap filled in wave 2), budget 470; plan §4.3, §4.6) ───────────
//
// THE STAGED HALF OF Edges.cs. `Edges.cs` is the live CSR tables; this is what a worker hands the UI drain so those
// tables can be written by their one legal writer (C1/C10): staged EDGES, the RUNS that slice them, and the whole
// `Entities.CommitEdges` body that lands them.
//
// WHY IT IS A FILE AND NOT A DECODER'S PRIVATE BUFFER. A decoder cannot resolve a child slot: `Table.Slot` ALLOCATES a
// row in a live table and that is the UI thread's alone. So an album's tracklist, a track's artists, a playlist's
// membership, the library relations and a home band's cards all cross the thread boundary as (parent identity,
// relation, an ordered run of child identities) — one shape, one buffer, one commit. It belongs beside `Edges.cs`
// because it writes `Edges.cs`'s tables and nothing else; Wave 2 wrote it inside the decoder only because Wave 1 had
// left `CommitEdges` unimplemented.
//
//      decode  ──▶  run.Add().Target = <gid or uri>            [socket thread, no interner, no live table]
//                   run.End(parent) / run.Page(parent, off, total)
//                        │   StagedRun { Parent, Relation, Start, Length, Offset, Total, State }
//                        ▼
//      commit  ──▶  Entities.CommitEdges(staging)              [UI thread, inside one drain]
//                   parent = s.Slot(ParentTable(relation), run.Parent)
//                   targets[i] = s.Slot(TargetTable(relation), edge.Target)
//                   table.Replace / ReplacePage
//
// THE RELATION DECIDES BOTH TABLES — the parent's and the children's — so a run carries no kind of its own and cannot
// name a mismatched pair. That is the whole reason `Relation` is an enum and not a pair of table references.
//
// Rules: no allocation after warm-up (the buffers are pooled with their `Staging`, P8); no LINQ, no closures, no
// boxing (P9); every write to a live table happens inside `CommitEdges`, on the UI thread (C1).

using FluentGpu.Foundation;

namespace Wavee;

// ── 1. the relations a run can name ──────────────────────────────────────────────────────────────────────────────────

/// <summary>Which relation a staged run rewrites. The enum decides BOTH tables — the parent's and the children's — so
/// a run carries no kind of its own and cannot name a mismatched pair.</summary>
public enum Relation : byte
{
    TrackArtists, TrackTags, TrackFormats, TrackRelatedArtists,
    AlbumTracks, AlbumArtists, AlbumVersions, AlbumMoreBy, AlbumFeaturedOn, AlbumSimilar,
    ArtistPopular, ArtistRelated, ArtistReleases, ArtistAppearsOn, ArtistAlbums, ArtistSingles, ArtistCompilations,
    ShowEpisodes,
    PlaylistTracks,
    Liked, SavedAlbums, FollowedArtists, SavedShows, Rootlist,
    HomeSection, SectionCards, SearchResult,
    /// <summary>The ylpin set (G-062): parent = the account's user row, and the targets are CROSS-KIND — a playlist,
    /// album, artist or show row, the Liked collection, or a rootlist folder — so each edge's kind rides its payload's
    /// flag byte (<see cref="PinKind"/>). Appended last: the members above keep their numbers.</summary>
    Pins,
    /// <summary>§9.6 Q4: parent = album; each edge becomes a <see cref="MerchTable"/> row (Spotify.Decode.Album.cs).</summary>
    AlbumMerch,
}

// ── 2. the staged edge and its run ───────────────────────────────────────────────────────────────────────────────────

/// <summary>One staged edge. The identity is a <see cref="StagedId"/> — a packed gid straight off the wire, or the
/// uri's bytes in the arena — and the text is a <see cref="TextRef"/> for the same reason a staged ROW's is: the
/// decoder may not intern (C1).
///
/// <para>The scalar fields are a UNION read only by the payload family the run's <see cref="Relation"/> names: an
/// <c>AlbumTracks</c> run reads <c>B0</c>/<c>U0</c> as disc and number, a <c>PlaylistTracks</c> run reads
/// <c>Text</c>/<c>At</c>/<c>Aux</c> plus the chart triple, a library run reads <c>At</c> (a <c>Pins</c> run also reads
/// <c>Target</c>'s own kind — its targets are cross-kind), a <c>Rootlist</c> run reads <c>U0</c>/<c>B0</c>/<c>B1</c>/
/// <c>Text</c>/<c>At</c> and takes <c>Aux</c>'s TEXT as the folder's bare group id (<see cref="RootlistEdge.FolderId"/>,
/// D10), and a <c>TrackTags</c> run reads only <c>Text</c>. One struct and not seven, because the commit's shape is
/// identical for all of them and seven would be seven buffers.</para></summary>
public struct StagedEdge
{
    /// <summary>The child. The commit resolves it to a slot, ALLOCATING an empty row when it has never been seen —
    /// which is how an album's tracklist becomes bindable before any track is fetched.</summary>
    public StagedId Target;
    /// <summary>Payload text: a descriptor chip, a folder name, the hex <c>item_id</c>.</summary>
    public TextRef Text;
    /// <summary>A second identity on the edge: a playlist item's <c>added_by</c> user.</summary>
    public StagedId Aux;
    public int At;
    public ushort U0, U1;
    public byte B0, B1;
}

/// <summary>One parent's rewritten list. <see cref="Offset"/> &lt; 0 is a whole <c>Replace</c>; &ge; 0 is a
/// <c>ReplacePage</c> at that offset, which is how a paged relation stays <see cref="EdgeState.Partial"/> until its own
/// length reaches the server's total (Edges.cs, D7).</summary>
public struct StagedRun
{
    public StagedId Parent;
    public Relation Relation;
    public int Start, Length, Offset, Total;
    public EdgeState State;
}

/// <summary>The staged edges of one batch, with the runs that slice them. Registered on the <see cref="Staging"/> so it
/// is pooled and cleared with it (P8).</summary>
public sealed class StagedEdgeList : StagedList
{
    StagedEdge[] _edges = new StagedEdge[64];
    StagedRun[] _runs = new StagedRun[8];

    public int RunCount;

    public ref StagedEdge Add()
    {
        if (Count == _edges.Length) Array.Resize(ref _edges, _edges.Length * 2);
        ref var e = ref _edges[Count++];
        e = default;
        return ref e;
    }

    public ReadOnlySpan<StagedEdge> Span => _edges.AsSpan(0, Count);
    public ReadOnlySpan<StagedRun> Runs => _runs.AsSpan(0, RunCount);

    /// <summary>Close a whole-list rewrite over the edges appended since <paramref name="start"/>.</summary>
    public void Run(Relation relation, in StagedId parent, int start, int length,
                    EdgeState state = EdgeState.Complete, int total = 0)
        => Append(relation, in parent, start, length, offset: -1, total == 0 ? length : total, state);

    /// <summary>Close a PAGE at <paramref name="offset"/>. <paramref name="total"/> 0 means "the server did not say",
    /// and the relation stays Partial until somebody does.</summary>
    public void Page(Relation relation, in StagedId parent, int start, int length, int offset, int total)
        => Append(relation, in parent, start, length, offset < 0 ? 0 : offset, total, EdgeState.Partial);

    /// <summary>Squeeze the edges the wire NAMED but did not IDENTIFY out of <c>[start, start+count)</c>, and answer
    /// the run's real length. Done once, here, at stage time — so the commit's payload loops are plain
    /// <c>for (k &lt; n)</c> walks with no second cursor and no per-arm emptiness rule.
    /// <para>An edge with no target but with TEXT survives: that is the "the payload IS the row" shape
    /// (<c>TrackTags</c>, Edges.cs), where the targets are unused by construction.</para></summary>
    public int Compact(int start, int count, bool keepAux = false)
    {
        if (start < 0 || count <= 0 || start + count > Count) return count < 0 ? 0 : count;
        int write = start;
        for (int read = start; read < start + count; read++)
            if (!_edges[read].Target.IsEmpty || !_edges[read].Text.IsEmpty || (keepAux && !_edges[read].Aux.IsEmpty))
                _edges[write++] = _edges[read];
        int kept = write - start;
        if (start + count == Count) Count = write;                 // the run is at the tail: give the slack back
        return kept;
    }

    /// <summary>A run's real length: <see cref="Compact"/>'s squeeze, unless the relation is PAYLOAD-ONLY.
    ///
    /// <para>Compact's rule is "the wire NAMED a child and then failed to IDENTIFY it", and it reads that off the target
    /// and the text. A payload-only relation has neither by construction — a <see cref="FormatEdge"/> rung is two
    /// numbers and nothing else — so running the squeeze over one would throw the whole ladder away and record an empty
    /// run, silently. <see cref="Relation.TrackTags"/> only escapes because its payload happens to BE text.</para>
    /// <para>A <see cref="Relation.Rootlist"/> marker names no child either: a folder's END marker has no target and no
    /// name, and its identity is the group id it carries in <c>Aux</c> — so for that relation an edge with an <c>Aux</c> is
    /// identified too (G-062), or every end marker would be squeezed out of the stream.</para></summary>
    public int Kept(Relation relation, int start, int count)
        => PayloadOnly(relation) ? count : Compact(start, count, keepAux: relation == Relation.Rootlist);

    /// <summary>Relations whose EDGE IS THE PAYLOAD (Edges.cs): the children are not rows, the run carries no targets
    /// at all, and their commit arms write <see cref="Table.None"/> into every target slot.</summary>
    public static bool PayloadOnly(Relation relation) => relation is Relation.TrackTags or Relation.TrackFormats;

    void Append(Relation relation, in StagedId parent, int start, int length, int offset, int total, EdgeState state)
    {
        if (parent.IsEmpty || length < 0 || start < 0 || start + length > Count) return;
        if (RunCount == _runs.Length) Array.Resize(ref _runs, _runs.Length * 2);
        ref var run = ref _runs[RunCount++];
        run.Parent = parent;
        run.Relation = relation;
        run.Start = start;
        run.Length = length;
        run.Offset = offset;
        run.Total = total;
        run.State = state;
    }

    public override void Clear() { Count = 0; RunCount = 0; _pendingCount = 0; }

    // ── the pending stack ────────────────────────────────────────────────────────────────────────────────────────
    //
    // WHY IT EXISTS. A run is a CONTIGUOUS slice of the edge array, and the pathfinder's node reader NESTS: decoding
    // one card of a shelf also decodes that card's artists, and those artist edges would land in the middle of the
    // shelf's own run. Interleaved edges make `[start, start+length)` name the wrong children — silently, which is the
    // worst kind. So a nested decoder pushes its members onto this stack and `Close` copies them into the array in one
    // contiguous block when the list is finished.
    //
    // The protobuf decoders do not need it: a wire field number is emitted in order, so their runs are contiguous by
    // construction and `EdgeRun` appends them straight through `Add`.

    StagedEdge[] _pending = new StagedEdge[64];
    int _pendingCount;

    /// <summary>Where this list's members start. Take it before the walk, hand it to <see cref="Close"/>.</summary>
    public int PendingMark => _pendingCount;

    /// <summary>How many members a walk has pushed since <paramref name="mark"/>.</summary>
    public int Pending(int mark) => _pendingCount - mark;

    /// <summary>Push one member of the list being walked.</summary>
    public ref StagedEdge Push()
    {
        if (_pendingCount == _pending.Length) Array.Resize(ref _pending, _pending.Length * 2);
        ref var e = ref _pending[_pendingCount++];
        e = default;
        return ref e;
    }

    /// <summary>Abandon everything pushed since <paramref name="mark"/> — a list whose parent turned out to have no
    /// identity.</summary>
    public void Pop(int mark) { if (mark >= 0 && mark <= _pendingCount) _pendingCount = mark; }

    /// <summary>Land everything pushed since <paramref name="mark"/> as ONE contiguous whole-list rewrite.</summary>
    public void Close(Relation relation, in StagedId parent, int mark, EdgeState state = EdgeState.Complete, int total = 0)
    {
        if (mark > _pendingCount) return;                          // out of order: see Flush
        int n = Flush(mark);
        Run(relation, in parent, Count - n, Kept(relation, Count - n, n), state, total);
        Pop(mark);
    }

    /// <summary>The same, as a PAGE at <paramref name="offset"/>.</summary>
    public void ClosePage(Relation relation, in StagedId parent, int mark, int offset, int total)
    {
        if (mark > _pendingCount) return;                          // out of order: see Flush
        int n = Flush(mark);
        Page(relation, in parent, Count - n, Kept(relation, Count - n, n), offset, total);
        Pop(mark);
    }

    // ONLY THE TOP OF THE STACK CAN BE CLOSED. A caller that closes a LOWER mark first flushes and pops every run
    // above it, and the next `Close` then names a mark that is past the stack — which used to compute a NEGATIVE
    // length and record a run `Append` silently dropped, so the relation simply never landed. It answers 0 now, and
    // the guards in `Close`/`ClosePage` turn that into "record nothing" rather than "record something wrong".
    int Flush(int mark)
    {
        int n = _pendingCount - mark;
        if (n <= 0) return 0;
        for (int i = 0; i < n; i++) Add() = _pending[mark + i];
        return n;
    }
}

// ── 3. EdgeRun: the run bookkeeping, once ────────────────────────────────────────────────────────────────────────────

/// <summary>ONE relation being appended, as a cursor. It replaces the `int start = s.Edges.Count, count = 0;` … `if
/// (start &lt; 0) start = …` … `s.Edges.Truncate(start)` … `if (count > 0) s.Edges.Run(…)` quartet that appeared at
/// thirteen sites in the protobuf decoders, each of which could get one of the four wrong on its own.
///
/// <para><b>The start is LAZY</b>, which is what the hand-written `start = -1` sentinel was doing: a relation whose
/// first child arrives after another relation's children (an artist's albums after its top tracks) must record its own
/// beginning, not the row's. A run that never added anything ends as nothing at all — and "nothing at all" is the right
/// answer, because an empty run and an absent one render differently on every detail surface (an album that named no
/// disc leaves its tracklist Unknown, ch 03 §7).</para>
///
/// <para>A <c>ref struct</c> over the pooled list: no allocation, and it cannot outlive the decode that made it.</para></summary>
public ref struct EdgeRun
{
    readonly StagedEdgeList _list;
    readonly Relation _relation;
    int _start;
    int _count;

    public EdgeRun(Staging s, Relation relation)
    {
        _list = s.Edges;
        _relation = relation;
        _start = -1;
        _count = 0;
    }

    /// <summary>How many children this run has taken.</summary>
    public readonly int Count => _count;

    /// <summary>Append one child, by reference, so the caller fills the payload fields in place (P8).</summary>
    public ref StagedEdge Add()
    {
        if (_start < 0) _start = _list.Count;
        _count++;
        return ref _list.Add();
    }

    /// <summary>Append one child and set its identity — the shape every relation opens with. The edge comes back by
    /// reference so the payload fields (a disc number, an added-at, a chart position) are filled in place.</summary>
    public ref StagedEdge Add(in StagedId target)
    {
        ref var edge = ref Add();
        edge.Target = target;
        return ref edge;
    }

    /// <summary>Undo the last <see cref="Add"/>: the wire named a child and then failed to identify it.</summary>
    public void DropLast()
    {
        if (_count == 0) return;
        _list.Drop();
        _count--;
    }

    /// <summary>Close the run as a WHOLE-LIST rewrite. A run with no children records nothing.</summary>
    public void End(in StagedId parent, EdgeState state = EdgeState.Complete, int total = 0)
    {
        if (_count == 0 || parent.IsEmpty) return;
        _list.Run(_relation, in parent, _start, Kept(), state, total);
        Reset();
    }

    // The run's real length, via `StagedEdgeList.Kept`: Compact's squeeze, except for a payload-only relation, whose
    // edges name no child at all and would be squeezed away entirely.
    int Kept() => _list.Kept(_relation, _start < 0 ? _list.Count : _start, _count);

    /// <summary>Close the run as a PAGE at <paramref name="offset"/> — the relation stays Partial until its own length
    /// reaches <paramref name="total"/> (Edges.cs, D7). A run with no children still records the page when the parent
    /// is known, because "the server answered with an empty page" is a real answer.</summary>
    public void Page(in StagedId parent, int offset, int total)
    {
        if (parent.IsEmpty) return;
        _list.Page(_relation, in parent, _start < 0 ? _list.Count : _start, Kept(), offset, total);
        Reset();
    }

    /// <summary>Close an EMPTY whole-list rewrite deliberately: "this track has no descriptors" is an answer and the
    /// planner must stop asking for it (finding 27). The only caller that wants a zero-length <c>Replace</c>.</summary>
    public void EndEvenIfEmpty(in StagedId parent, int total = 0)
    {
        if (parent.IsEmpty) return;
        _list.Run(_relation, in parent, _start < 0 ? _list.Count : _start, Kept(), EdgeState.Complete, total);
        Reset();
    }

    /// <summary>Throw the run's children away — the parent turned out to have no identity, so its children belong to
    /// nobody and must not be sliced into the NEXT run.</summary>
    public void Discard()
    {
        if (_start >= 0) _list.Rewind(_start);
        Reset();
    }

    // A closed run is EMPTY again, start included: a decoder that opened one relation twice would otherwise slice the
    // second run from the first one's beginning.
    void Reset() { _start = -1; _count = 0; }
}

// ── 4. the staging surface ───────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The edge half of <see cref="Staging"/>, declared here for the same reason each kind's file declares its own
/// staged-row list: the buffer belongs to whoever writes the table it lands in.</summary>
public sealed partial class Staging
{
    StagedEdgeList? _edges;

    /// <summary>The staged edges and the runs that slice them. Lazy: a decode that touches no relation allocates no
    /// edge list at all.</summary>
    public StagedEdgeList Edges => _edges ??= Register(new StagedEdgeList());

    internal StagedEdgeList? EdgesOrNull => _edges;

    /// <summary>Begin a relation (see <see cref="EdgeRun"/>).</summary>
    public EdgeRun Run(Relation relation) => new(this, relation);
}

// ── 5. the commit ────────────────────────────────────────────────────────────────────────────────────────────────────

public static partial class Entities
{
    // The commit's scratch. UI thread only (C1), so one static set is safe and a steady stream of answers allocates
    // nothing after the largest run it has seen (P8).
    static int[] s_edgeTargets = new int[64];
    static NoEdge[] s_edgeNone = new NoEdge[64];
    static AlbumTrackEdge[] s_edgeAlbumTrack = new AlbumTrackEdge[64];
    static DiscographyEdge[] s_edgeDiscography = new DiscographyEdge[64];
    static PlaylistTrackEdge[] s_edgePlaylistTrack = new PlaylistTrackEdge[64];
    static LibraryEdge[] s_edgeLibrary = new LibraryEdge[64];
    static RootlistEdge[] s_edgeRootlist = new RootlistEdge[64];
    static KindEdge[] s_edgeKind = new KindEdge[64];
    static StringId[] s_edgeText = new StringId[64];
    static FormatEdge[] s_edgeFormat = new FormatEdge[64];

    /// <summary>Land every staged run: resolve the parent, resolve the children, build the payload the relation's own
    /// family needs, and <c>Replace</c> or <c>ReplacePage</c>. One arm per PAYLOAD FAMILY and no more — everything else
    /// about them is identical and lives in <see cref="Resolve"/> and <see cref="Land"/>.</summary>
    static partial void CommitEdges(Staging s)
    {
        var list = s.EdgesOrNull;
        if (list is null || list.RunCount == 0) return;

        var edges = list.Span;
        var runs = list.Runs;
        for (int i = 0; i < runs.Length; i++)
        {
            ref readonly var run = ref runs[i];
            if (run.Start < 0 || run.Length < 0 || run.Start + run.Length > edges.Length) continue;
            int parent = s.Slot(ParentTable(run.Relation), in run.Parent);
            if (parent == Table.None) continue;

            var page = edges.Slice(run.Start, run.Length);
            Grow(run.Length);

            switch (run.Relation)
            {
                case Relation.AlbumTracks:
                    {
                        // Defensive kind filter (G-231 follow-up): a mis-ordered `tracksV2`/`artists` decode can leave
                        // artist identities pushed above the tracks mark on the same pending run (GetAlbum), which
                        // would otherwise land here as empty Track rows. SKIP IN PLACE rather than compact: `j` is the
                        // ordinal, and for `tracksV2` the ordinal IS the track number when the edge's own disc/number
                        // are 0 — dropping entry `j` would shift every entry after it one slot toward `offset`,
                        // silently overwriting the wrong tracks and renumbering the rest of the album (unlike
                        // TrackTags/TrackFormats below, which are payload-only and have no ordinal to protect). A
                        // non-Track entry lands as `Table.None` / `default`, which `ReplacePage`'s own zero-fill
                        // already reads as "none / un-arrived" (Edges.cs).
                        for (int j = 0; j < page.Length; j++)
                        {
                            ref readonly var e = ref page[j];
                            if (e.Target.Kind(s) != EntityKind.Track)
                            {
                                s_edgeTargets[j] = Table.None;
                                s_edgeAlbumTrack[j] = default;
                                continue;
                            }
                            s_edgeTargets[j] = s.Slot(Current.Tracks, in e.Target);
                            s_edgeAlbumTrack[j] = new AlbumTrackEdge(e.B0, e.U0);
                        }
                        Land(Current.Edges.AlbumTracks, parent, page.Length, s_edgeAlbumTrack, in run);
                        break;
                    }
                case Relation.ShowEpisodes:
                case Relation.PlaylistTracks:
                    {
                        // Every membership fact is the EDGE's (D10). `AddedBy` is a user SLOT, so the adder's row is
                        // allocated here exactly like any other child — a byline is bindable before the profile lands.
                        int n = Resolve(s, page, run.Relation == Relation.ShowEpisodes ? Current.Episodes : Current.Tracks);
                        for (int j = 0; j < n; j++)
                        {
                            ref readonly var e = ref page[j];
                            s_edgePlaylistTrack[j] = new PlaylistTrackEdge(
                                s.Intern(e.Text), e.At, s.Slot(Current.Users, in e.Aux), e.B1, e.U0, e.U1, e.B0);
                        }
                        Land(run.Relation == Relation.ShowEpisodes ? Current.Edges.ShowEpisodes : Current.Edges.PlaylistTracks, parent, n, s_edgePlaylistTrack, in run);
                        if (run.Relation == Relation.PlaylistTracks) new global::Wavee.Playlist(parent).Refold();
                        break;
                    }
                case Relation.TrackTags:
                    {
                        // The payload IS the tag id and the targets are unused (Edges.cs). The run still lands when it
                        // is EMPTY: "this track has no descriptors" is a real answer (finding 27).
                        int n = 0;
                        for (int j = 0; j < page.Length; j++)
                        {
                            if (page[j].Text.IsEmpty) continue;
                            s_edgeTargets[n] = Table.None;
                            s_edgeText[n++] = s.Intern(page[j].Text);
                        }
                        Land(Current.Edges.TrackTags, parent, n, s_edgeText, in run);
                        break;
                    }
                case Relation.TrackFormats:
                    {
                        // The format ladder (extension kind 5, FLAC plan §5.2). Payload-only like TrackTags, but the
                        // payload is two NUMBERS, so there is nothing to skip and the wire's order is kept verbatim —
                        // it is the order the account was offered the rungs in, and the drawer sorts its own copy.
                        // An EMPTY run lands as a Complete relation on purpose: an account with no lossless in this
                        // market gets a ladder with no FLAC rung, and that is the answer to the question (finding 27).
                        int n = 0;
                        for (int j = 0; j < page.Length; j++)
                        {
                            s_edgeTargets[n] = Table.None;
                            s_edgeFormat[n++] = new FormatEdge(page[j].B0, page[j].U0);
                        }
                        Land(Current.Edges.TrackFormats, parent, n, s_edgeFormat, in run);
                        break;
                    }
                case Relation.Liked:
                case Relation.SavedAlbums:
                case Relation.FollowedArtists:
                case Relation.SavedShows:
                    {
                        int n = Resolve(s, page, TableFor(TargetKind(run.Relation)));
                        for (int j = 0; j < n; j++) s_edgeLibrary[j] = new LibraryEdge(page[j].At, 0);
                        Land(LibraryRelation(run.Relation), parent, n, s_edgeLibrary, in run);
                        break;
                    }
                case Relation.Rootlist:
                    {
                        // The generic arm of the marker stream (the protobuf rootlist and libraryV3 land through
                        // `CommitRootlist`; this serves a decoder that stages the stream as plain runs). Both strings are
                        // OWNED by the edge (Edges.cs header, G-062): AddRef the incoming FIRST, then give back the rows
                        // this run overwrites, then land — so a folder that kept its name keeps its id, and a replaced
                        // folder name is no longer permanent.
                        int n = Resolve(s, page, Current.Playlists);
                        for (int j = 0; j < n; j++)
                        {
                            ref readonly var e = ref page[j];
                            s_edgeRootlist[j] = new RootlistEdge(e.U0, e.B0, e.B1, Retained(s.Intern(e.Text)), e.At,
                                                                 Retained(s.Intern(e.Aux.Text)));
                        }
                        ReleaseRootlistRows(parent, run.Offset, n, run.Total);
                        Land(Current.Edges.Rootlist, parent, n, s_edgeRootlist, in run);
                        break;
                    }
                case Relation.Pins:
                    {
                        // CROSS-KIND (G-062): each target resolves in its OWN table, and the kind rides the payload's
                        // flag byte. The two non-row pins land too — Liked targets None, a folder targets its interned
                        // group id's value (User.cs, PinKind) — and a shape the sidebar cannot represent (a track, an
                        // episode, a prerelease, an unknown scheme) is dropped here, so it can never become a local pin.
                        int n = 0;
                        for (int j = 0; j < page.Length; j++)
                        {
                            var kind = PinTargetOf(s, in page[j].Target, out int target);
                            if (kind == PinKind.Unknown) continue;
                            s_edgeTargets[n] = target;
                            s_edgeLibrary[n++] = new LibraryEdge(page[j].At, (byte)kind);
                        }
                        Land(Current.Edges.Pins, parent, n, s_edgeLibrary, in run);
                        break;
                    }
                case Relation.ArtistReleases:
                case Relation.ArtistAppearsOn:
                    {
                        int n = Resolve(s, page, Current.Albums);
                        for (int j = 0; j < n; j++) s_edgeDiscography[j] = new DiscographyEdge(page[j].B0);
                        Land(run.Relation == Relation.ArtistAppearsOn
                             ? Current.Edges.ArtistAppearsOn : Current.Edges.ArtistReleases,
                             parent, n, s_edgeDiscography, in run);
                        break;
                    }
                case Relation.SearchResult:
                case Relation.SectionCards:
                    {
                        // Mixed-kind targets, and the KIND is the payload: two hits can be slot 5 in two different
                        // tables, and a result that lost which table it indexed is a route to the wrong page
                        // (Search.cs's own note on why this is an `EdgeTable<KindEdge>`).
                        int n = 0;
                        for (int j = 0; j < page.Length; j++)
                        {
                            var kind = page[j].Target.Kind(s);
                            var table = kind == EntityKind.Collection ? Current.Playlists : TableFor(kind);
                            if (table is null) continue;
                            s_edgeTargets[n] = s.Slot(table, in page[j].Target);
                            s_edgeKind[n++] = new KindEdge(kind);
                        }
                        Land(run.Relation == Relation.SectionCards ? Current.Edges.SectionCards : Current.Edges.SearchResult,
                             parent, n, s_edgeKind, in run);
                        break;
                    }
                case Relation.AlbumMerch:
                    {
                        // Merch is not an entity: one MerchTable row per edge. Union read: Text = name, Target's text =
                        // image, Aux's text = shop url, (At, U0) = the price's TextRef.
                        var merch = Current.Edges.Merch;
                        int first = merch.AllocRun(page.Length);
                        for (int j = 0; j < page.Length; j++)
                        {
                            ref readonly var e = ref page[j];
                            ref var row = ref merch.Row[first + j];
                            row.Name = s.Intern(e.Text);
                            row.Price = s.Intern(new TextRef(e.At, e.U0));
                            row.ImageId = s.Intern(e.Target.Text);
                            row.ShopUrl = s.Intern(e.Aux.Text);
                            s_edgeTargets[j] = first + j;
                        }
                        Land(Current.Edges.AlbumMerch, parent, page.Length, s_edgeNone, in run);
                        break;
                    }
                default:
                    {
                        // Every remaining relation is a plain ordered list with no payload at all: of one kind, of the
                        // SECTION rows a home holds, or — for a band that mixes kinds — of whatever each uri names.
                        int n = Resolve(s, page, TargetTable(run.Relation));
                        Land(NoEdgeRelation(run.Relation), parent, n, s_edgeNone, in run);
                        break;
                    }
            }
        }
    }

    /// <summary>Give back the folder strings of the rootlist rows a run is about to overwrite: the whole list for a
    /// rewrite, only <c>[offset, offset + n)</c> for a page (the rows outside the page stay, and so does their text) —
    /// UNLESS this page is terminal (<paramref name="total"/> stated and reached), in which case <c>ReplacePage</c>
    /// (Edges.cs) shrinks the list past the page's own extent when the previous answer was longer, and the truncated
    /// tail's owned <c>FolderName</c>/<c>FolderId</c> would otherwise leak their interner refcount — so release out to
    /// the OLD list's end too, under the same terminal guard `ReplacePage` uses.</summary>
    static void ReleaseRootlistRows(int parent, int offset, int n, int total)
    {
        if (offset < 0) { Current.Edges.ReleaseRootlistText(parent); return; }
        var rows = Current.Edges.Rootlist.Payload(parent);
        int pageEnd = offset + n;
        bool terminal = total > 0 && pageEnd >= total;
        int end = terminal ? rows.Length : Math.Min(rows.Length, pageEnd);
        for (int i = offset; i < end; i++)
        {
            Strings.Release(rows[i].FolderName);
            Strings.Release(rows[i].FolderId);
        }
    }

    /// <summary>One staged pin's target and kind (<see cref="PinKind"/>). A catalogue row resolves in its own table
    /// (allocating an empty row, like every edge child); the Liked collection targets <see cref="Table.None"/>; a
    /// <c>spotify:folder:&lt;hex&gt;</c> targets the value of its interned group id (permanent — see <see cref="PinKind"/>).
    /// <see cref="PinKind.Unknown"/> for anything the sidebar cannot pin, which the commit drops.</summary>
    static PinKind PinTargetOf(Staging s, in StagedId id, out int target)
    {
        target = Table.None;
        if (id.IsEmpty) return PinKind.Unknown;
        var entityKind = id.Kind(s);
        var pinKind = global::Wavee.User.PinKindFor(entityKind);
        if (pinKind != PinKind.Unknown)
        {
            // A prerelease parses as an ALBUM row, but it is not a pin the sidebar can show (SidebarPinSyncTests'
            // preservation invariant): a foreign client's prerelease pin stays on the server, untouched.
            bool prerelease = id.Packed.IsEmpty ? EntityUri.IsPrerelease(s.Utf8(id.Text)) : id.Packed.IsPrerelease;
            if (prerelease) return PinKind.Unknown;
            target = s.Slot(TableFor(entityKind), in id);
            return target == Table.None ? PinKind.Unknown : pinKind;
        }
        if (!id.Packed.IsEmpty) return PinKind.Unknown;          // a packed id of a kind no pin represents
        var utf8 = s.Utf8(id.Text);
        if (entityKind == EntityKind.Collection && global::Wavee.User.IsLikedPinUri(utf8)) return PinKind.Liked;
        var folder = global::Wavee.User.FolderIdOf(utf8);
        if (folder.IsEmpty) return PinKind.Unknown;
        target = Intern(folder).Value;
        return PinKind.Folder;
    }

    /// <summary>Resolve a run's children to slots. <paramref name="fixedTable"/> <c>null</c> means "take each
    /// identity's own kind" — the mixed-kind case a home band is.</summary>
    static int Resolve(Staging s, ReadOnlySpan<StagedEdge> page, Table? fixedTable)
    {
        for (int i = 0; i < page.Length; i++) s_edgeTargets[i] = s.Slot(fixedTable, in page[i].Target);
        return page.Length;
    }

    /// <summary>A whole rewrite or a page, by the run's own <c>Offset</c>. The two are not interchangeable: a
    /// <c>Replace</c> settles the relation's state, a <c>ReplacePage</c> leaves it Partial until the length reaches the
    /// total (Edges.cs, D7).</summary>
    static void Land<TEdge>(EdgeTable<TEdge> table, int parent, int n, TEdge[] payload, in StagedRun run)
        where TEdge : unmanaged
    {
        var targets = s_edgeTargets.AsSpan(0, n);
        var values = payload.AsSpan(0, n);
        if (run.Offset >= 0) table.ReplacePage(parent, run.Offset, targets, values, run.Total);
        else table.Replace(parent, targets, values, run.State, run.Total == 0 ? n : run.Total);
    }

    static Table? ParentTable(Relation relation) => relation switch
    {
        Relation.TrackArtists or Relation.TrackTags or Relation.TrackFormats
            or Relation.TrackRelatedArtists => Current.Tracks,
        Relation.AlbumTracks or Relation.AlbumArtists or Relation.AlbumVersions or Relation.AlbumMoreBy
            or Relation.AlbumFeaturedOn or Relation.AlbumSimilar or Relation.AlbumMerch => Current.Albums,
        Relation.ArtistPopular or Relation.ArtistRelated or Relation.ArtistReleases or Relation.ArtistAppearsOn
            or Relation.ArtistAlbums or Relation.ArtistSingles or Relation.ArtistCompilations => Current.Artists,
        Relation.ShowEpisodes => Current.Shows,
        Relation.PlaylistTracks => Current.Playlists,
        Relation.Liked or Relation.SavedAlbums or Relation.FollowedArtists or Relation.SavedShows
            or Relation.Rootlist or Relation.Pins => Current.Users,
        Relation.HomeSection => Current.Homes,
        Relation.SectionCards => Current.Sections,
        Relation.SearchResult => Current.Searches,
        _ => null,
    };

    /// <summary>The table a relation's children live in, or null when the relation mixes kinds and each identity
    /// answers for itself (a home band, a search facet). A home's children are SECTION rows, not entities — the one
    /// relation whose targets are a synthetic table.</summary>
    static Table? TargetTable(Relation relation)
        => relation == Relation.HomeSection ? Current.Sections : TableFor(TargetKind(relation));

    static EntityKind TargetKind(Relation relation) => relation switch
    {
        Relation.TrackArtists or Relation.AlbumArtists or Relation.ArtistRelated
            or Relation.TrackRelatedArtists or Relation.FollowedArtists => EntityKind.Artist,
        Relation.ArtistPopular or Relation.Liked => EntityKind.Track,
        Relation.AlbumVersions or Relation.AlbumMoreBy or Relation.AlbumFeaturedOn or Relation.AlbumSimilar
            or Relation.ArtistAlbums or Relation.ArtistSingles or Relation.ArtistCompilations
            or Relation.ArtistReleases or Relation.ArtistAppearsOn or Relation.SavedAlbums => EntityKind.Album,
        Relation.ShowEpisodes => EntityKind.Episode,
        Relation.SavedShows => EntityKind.Show,
        _ => EntityKind.Unknown,                       // a home section mixes kinds; take each identity's own
    };

    static EdgeTable<LibraryEdge> LibraryRelation(Relation relation) => relation switch
    {
        Relation.SavedAlbums => Current.Edges.SavedAlbums,
        Relation.FollowedArtists => Current.Edges.FollowedArtists,
        Relation.SavedShows => Current.Edges.SavedShows,
        _ => Current.Edges.Liked,
    };

    static EdgeTable<NoEdge> NoEdgeRelation(Relation relation) => relation switch
    {
        Relation.AlbumArtists => Current.Edges.AlbumArtists,
        Relation.AlbumVersions => Current.Edges.AlbumVersions,
        Relation.AlbumMoreBy => Current.Edges.AlbumMoreBy,
        Relation.AlbumFeaturedOn => Current.Edges.AlbumFeaturedOn,
        Relation.AlbumSimilar => Current.Edges.AlbumSimilar,
        Relation.ArtistPopular => Current.Edges.ArtistPopular,
        Relation.ArtistRelated => Current.Edges.ArtistRelated,
        Relation.ArtistAlbums => Current.Edges.ArtistAlbums,
        Relation.ArtistSingles => Current.Edges.ArtistSingles,
        Relation.ArtistCompilations => Current.Edges.ArtistCompilations,
        Relation.TrackRelatedArtists => Current.Edges.TrackRelatedArtists,
        Relation.HomeSection => Current.Edges.HomeSection,
        _ => Current.Edges.TrackArtists,
    };

    static void Grow(int n)
    {
        if (n <= s_edgeTargets.Length) return;
        int size = s_edgeTargets.Length;
        while (size < n) size *= 2;
        s_edgeTargets = new int[size];
        s_edgeNone = new NoEdge[size];
        s_edgeAlbumTrack = new AlbumTrackEdge[size];
        s_edgeDiscography = new DiscographyEdge[size];
        s_edgePlaylistTrack = new PlaylistTrackEdge[size];
        s_edgeLibrary = new LibraryEdge[size];
        s_edgeRootlist = new RootlistEdge[size];
        s_edgeKind = new KindEdge[size];
        s_edgeText = new StringId[size];
        s_edgeFormat = new FormatEdge[size];
    }
}
