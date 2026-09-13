// ── Entities/User.cs — CORE (owner B, wave 1; plan §2, §4.14, ch 07 §7, ch 15 §7) ────────────────────────────────────
//
// THE LIBRARY IS EDGES (G6). There is no `IsLiked` column, no saved-albums list object, no `LibraryStore` with five
// parallel collections and five `Ensure*` methods. There is ONE user row — the signed-in account — and six relations
// hanging off its slot:
//
//        Users[me] ──┬── Liked            EdgeTable<LibraryEdge>   → track slots, newest first
//                    ├── SavedAlbums      EdgeTable<LibraryEdge>   → album slots
//                    ├── FollowedArtists  EdgeTable<LibraryEdge>   → artist slots
//                    ├── SavedShows       EdgeTable<LibraryEdge>   → show slots
//                    ├── Pins             EdgeTable<LibraryEdge>   → any slot the sidebar can pin
//                    └── Rootlist         EdgeTable<RootlistEdge>  → playlist slots + folder markers
//
// "Is this track liked" is `Edges.Liked.Contains(me, slot)` — an O(1) membership question with no bool column anywhere
// (P3), answerable for every painted row without a service, a signal per uri, or the 0.2.9 `LibraryBridge.IsSaved`
// dictionary. "When was it added" is the EDGE's payload, not the track's (D10): the same recording added to the
// library twice under two accounts disagrees about the date, and only the edge can hold that.
//
// THE WRITE PATH IS OPTIMISTIC BY CONSTRUCTION (C6). `Like` splices the edge in with `EdgePending.Add` and returns —
// the heart is filled in the same frame the user clicked, because the model already says so. The shell PUTs and posts
// `SettleLike`, which clears the bit or removes the edge. There is no outbox, no replay, no second store: the pending
// bit IS the outbox, and it dies with the scope. The shell half is reached through <see cref="User.Dispatch"/>, an
// unimplemented partial method — so this file, and every test over it, is engine-free and network-free (D17).
//
// The user row itself carries only what the surfaces read: a display name, an avatar, and (ch 07 §7 G4) the curated
// content-filter chip set, which is a per-account list with no entity behind it and therefore lives on the row rather
// than in a table of its own.
//
// IDENTITY IS THE PACKED `EntityId`, AND THE ROW OWNS ITS TEXT. Two consequences for this file, both from the identity
// investigation (docs/plans/wavee/wavee-0.3-entity-identity-memory.md, option 2, approved 2026-09-12):
//
//   • the write seam carries an `EntityId`, not a `StringId` uri. `Like`/`Save`/`Follow` pass `track.Id`, and the
//     shell formats it once, at the request builder, instead of the model resolving a string per click (doc §3.1
//     item 2). A user row's own uri is `spotify:user:<name>`, which is never 22 base62 characters, so a user is always
//     the TEXT form — the one population that keeps a string in the interner and therefore the one that MUST be
//     ref-counted (doc §6 "not solved": non-Spotify and non-gid rows get no memory win, only the correct lifetime).
//
//   • every `StringId` this file owns is ref-counted (defect 1, doc §4.4): the engine reclaims an id only when its
//     last reference is released and a never-AddRef'd string is PERMANENT (the engine's `StringTable.cs:26`), so
//     before 2026-09-12 a trimmed user row leaked its name and avatar, and a rewritten chip set leaked the whole
//     previous set, for the life of the process. `Name`/`Image` go through `Table.SetText` and come back in
//     <see cref="UserTable.ReleaseText"/>; the chip SLABS are not row-indexed, so they use the one-level-down pair
//     `Entities.RetainText` / `Entities.ReleaseText` in <see cref="UserTable.SetContentFilters"/>.
//
// Rules: single writer, UI thread (C1); no LINQ, no closures, no async, no boxing (P8/P9); timestamps are app-epoch
// seconds (P7); text is a REF-COUNTED StringId (P6).

using FluentGpu.Foundation;

namespace Wavee;

// ── 1. field groups and the small enums ──────────────────────────────────────────────────────────────────────────────

/// <summary>Which column groups of a user row are filled (P3).</summary>
[Flags]
public enum UserFields : uint
{
    /// <summary>Display name and avatar — what an owner line, an added-by cell and a friend row need.</summary>
    Identity = 1 << 0,
    /// <summary>Follower / following counts. Only the account's own row ever carries them today.</summary>
    Social = 1 << 1,
    /// <summary>The curated Liked-Songs chip set (<c>content-filter/v1/liked-songs</c>, ch 07 §7 G4). Its own group
    /// because its own service answers it, and because an EMPTY answer is a real publish: it is what hands the chip
    /// bar to the descriptor-derived fallback instead of leaving a stale curated bar up for the session.</summary>
    ContentFilters = 1 << 2,

    All = Identity | Social | ContentFilters,
}

/// <summary>What a <see cref="RootlistEdge"/>'s <c>Kind</c> byte means. The rootlist is a FLAT ordered stream with
/// folder markers, exactly as the wire sends it — not a tree — because that is the only shape in which a reorder is a
/// single index move. The sidebar's tree is built from this stream by the pipeline, not stored as one.</summary>
public enum RootlistKind : byte { Item = 0, FolderStart = 1, FolderEnd = 2 }

/// <summary>Which library relation a write targets. One enum instead of five near-identical call paths — the shell's
/// dispatcher switches on it once and the four surfaces that mutate the library share one seam (C6).</summary>
public enum LibraryEdgeKind : byte { Liked, SavedAlbums, FollowedArtists, SavedShows, Pins }

// ── 2. the table ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The user rows: the signed-in account, every playlist owner, every added-by, every friend. Most of them
/// carry nothing but a name, which is exactly why they are rows and not objects — 4,000 owners is two columns, not
/// 4,000 records (P1).</summary>
public sealed class UserTable : Table
{
    public Column<StringId> Name, Image;
    public Column<int> Followers, Following;

    /// <summary>Where this row's curated chips start in <see cref="FilterTitles"/> / <see cref="FilterTokens"/>, and
    /// how many there are (ch 07 §7 G4). A range into two shared slabs rather than a list per row: the chip set is
    /// written whole, once per account per session, and read every time the chip bar paints.</summary>
    public Column<int> FilterStart, FilterCount;

    /// <summary>The chip LABEL (<c>display_name</c>, "K-Pop") — what the bar renders.</summary>
    public Column<StringId> FilterTitles;
    /// <summary>The chip TOKEN (<c>text</c>, "k-pop") — what the descriptor join matches on. Two columns because the
    /// wire genuinely has two strings and 0.2.9's single one is why a chip could match nothing (ch 07 §7 G3).</summary>
    public Column<StringId> FilterTokens;
    /// <summary>Bump allocator over the two chip slabs. Never reclaimed: the set is rewritten a handful of times in a
    /// session and the whole scope is dropped in one piece on a switch (P5, D9).</summary>
    public int FilterTail;

    public Column<byte> IdentityAuthority, ExtrasAuthority;

    public override EntityKind Kind => EntityKind.User;

    /// <summary>Write a row's whole chip set and point the row at it. Appends to the slabs; the previous range's
    /// CELLS are abandoned, which is the same trade the edge arena makes (P5) — but its STRINGS are given back first,
    /// or a chip set rewritten once a session leaks a whole set every time (defect 1).
    ///
    /// <para>The slabs are addressed by a per-row RANGE and not by slot, so they cannot go through
    /// <see cref="Table.SetText"/> (which indexes a column BY SLOT); they use the level below it,
    /// <see cref="Entities.RetainText"/> — same AddRef-then-Release order, same no-op when the id is unchanged, and
    /// the cells being appended to are freshly zeroed capacity, so the release half is a no-op there.</para></summary>
    public void SetContentFilters(int slot, ReadOnlySpan<StringId> titles, ReadOnlySpan<StringId> tokens)
    {
        ReleaseContentFilters(slot);                      // FIRST: the range we are about to abandon owns its strings
        int n = titles.Length < tokens.Length ? titles.Length : tokens.Length;
        FilterTitles.EnsureCapacity(FilterTail + n);
        FilterTokens.EnsureCapacity(FilterTail + n);
        for (int i = 0; i < n; i++)
        {
            Entities.RetainText(ref FilterTitles[FilterTail + i], titles[i]);
            Entities.RetainText(ref FilterTokens[FilterTail + i], tokens[i]);
        }
        FilterStart[slot] = FilterTail;
        FilterCount[slot] = n;
        FilterTail += n;
    }

    /// <summary>Give one row's chip strings back and forget the range. Idempotent: a second call sees a zero count.</summary>
    void ReleaseContentFilters(int slot)
    {
        int start = FilterStart[slot], count = FilterCount[slot];
        for (int i = 0; i < count; i++)
        {
            Entities.ReleaseText(ref FilterTitles[start + i]);
            Entities.ReleaseText(ref FilterTokens[start + i]);
        }
        FilterCount[slot] = 0;
    }

    /// <summary>Give back every string a user row owns (defect 1): the two identity columns and the chip range. One
    /// line per <c>Column&lt;StringId&gt;</c> — miss one and its text is permanent for the life of the process.
    /// <para>Called by <see cref="Table.FreeSlot"/> (the store's trim, R2) and by <see cref="Table.ReleaseAllText"/>
    /// (a retired scope, D9).</para></summary>
    protected override void ReleaseText(int slot)
    {
        ClearText(ref Name, slot);
        ClearText(ref Image, slot);
        ReleaseContentFilters(slot);
    }

    protected override void GrowColumns(int capacity)
    {
        Name.EnsureCapacity(capacity);
        Image.EnsureCapacity(capacity);
        Followers.EnsureCapacity(capacity);
        Following.EnsureCapacity(capacity);
        FilterStart.EnsureCapacity(capacity);
        FilterCount.EnsureCapacity(capacity);
        IdentityAuthority.EnsureCapacity(capacity);
        ExtrasAuthority.EnsureCapacity(capacity);
    }
}

// ── 3. the handle ────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A user: one <c>int</c> over the columns, and the parent of every library relation (G6).</summary>
public readonly partial struct User(int slot) : IEquatable<User>
{
    static UserTable T => Entities.Current.Users;
    static Edges E => Entities.Current.Edges;

    public int Slot { get; } = slot;

    /// <summary>The signed-in account's own row. <see cref="Table.None"/> — and therefore an empty library, never a
    /// wrong one — until <c>Entities.Boot</c> resolves the account (plan §4.1's <c>ResolveMe</c>).</summary>
    public static User Me => new(Entities.Current.MeSlot);

    public bool IsValid => Slot > Table.None && Slot < T.Count;
    public uint Version => T.Version[Slot];
    public bool Knows(UserFields fields) => T.Knows(Slot, (uint)fields);

    /// <summary>THE row's identity, packed (<c>Entities.cs</c> §2). A user uri is never a 22-character gid, so this
    /// is always the TEXT form — the payload is the interned <c>spotify:user:&lt;name&gt;</c> the row owns.</summary>
    public EntityId Id => T.Id[Slot];

    /// <summary>The identity as the text-facing VIEW — a profile deep link, a copy-link, an added-by tooltip. Free:
    /// an <see cref="EntityUri"/> IS an <see cref="EntityId"/>.</summary>
    public EntityUri Uri => new(T.Id[Slot]);

    public StringId NameId => T.Name[Slot];
    public StringId ImageId => T.Image[Slot];
    public int Followers => T.Followers[Slot];
    public int Following => T.Following[Slot];

    /// <summary>The curated chip labels for this account, in server order (ch 07 §7 G4). Empty is a real answer.</summary>
    public ReadOnlySpan<StringId> ContentFilterTitles
        => T.FilterTitles.Span.Slice(T.FilterStart[Slot], T.FilterCount[Slot]);
    /// <summary>The matching tokens, parallel to <see cref="ContentFilterTitles"/>.</summary>
    public ReadOnlySpan<StringId> ContentFilterTokens
        => T.FilterTokens.Span.Slice(T.FilterStart[Slot], T.FilterCount[Slot]);

    // ── the library, as edges ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The relation behind a <see cref="LibraryEdgeKind"/>. One switch instead of five call paths.
    /// <para>Named <c>Relation</c> and not <c>Table</c> on purpose: a member called <c>Table</c> would hide the
    /// <see cref="Wavee.Table"/> TYPE in every expression position inside this struct.</para></summary>
    public static EdgeTable<LibraryEdge> Relation(LibraryEdgeKind kind) => kind switch
    {
        LibraryEdgeKind.SavedAlbums => Entities.Current.Edges.SavedAlbums,
        LibraryEdgeKind.FollowedArtists => Entities.Current.Edges.FollowedArtists,
        LibraryEdgeKind.SavedShows => Entities.Current.Edges.SavedShows,
        LibraryEdgeKind.Pins => Entities.Current.Edges.Pins,
        _ => Entities.Current.Edges.Liked,
    };

    /// <summary>Liked tracks, newest first (the order the cover mosaic and the Liked page both assume).</summary>
    public ReadOnlySpan<int> LikedTrackSlots => E.Liked.Targets(Slot);
    /// <summary>Per-row <c>AddedAt</c> for the liked list — the week spark, the "since" line, the rediscover pick and
    /// the save-decade card all read it (ch 07 §7). It is an EDGE payload, not a Known bit.</summary>
    public ReadOnlySpan<LibraryEdge> LikedEdges => E.Liked.Payload(Slot);
    public ReadOnlySpan<int> SavedAlbumSlots => E.SavedAlbums.Targets(Slot);
    public ReadOnlySpan<int> FollowedArtistSlots => E.FollowedArtists.Targets(Slot);
    public ReadOnlySpan<int> SavedShowSlots => E.SavedShows.Targets(Slot);
    /// <summary>The pinned rows' slots. CROSS-KIND and therefore AMBIGUOUS today: a pin can be a playlist, an album,
    /// an artist, a show or the Liked collection, and a bare <c>int</c> cannot say which table it indexes — album
    /// slot 5 and playlist slot 5 are the same <c>int</c> (doc §3.1 requirement 4, the same defect
    /// <see cref="KindEdge"/> fixes for search and <c>Queue.cs</c> fixes by packing the kind into its target).
    /// <c>LibraryEdge</c> has no kind field, so the fix is one byte on that payload (or a <see cref="KindEdge"/>
    /// relation beside it) and belongs to <c>Edges.cs</c>'s owner — REPORTED, not worked around here, because pins
    /// are read by the sidebar and the shape has to be decided once.</summary>
    public ReadOnlySpan<int> PinSlots => E.Pins.Targets(Slot);

    /// <summary>The rootlist as the wire sends it: a flat ordered stream of playlist slots and folder markers.</summary>
    public ReadOnlySpan<int> RootlistSlots => E.Rootlist.Targets(Slot);
    /// <inheritdoc cref="RootlistSlots"/>
    public ReadOnlySpan<RootlistEdge> Rootlist => E.Rootlist.Payload(Slot);
    public EdgeState RootlistState => E.Rootlist.State(Slot);

    /// <summary>Is a relation whole? The library navigator's own readiness gate: <see cref="EdgeState.Unknown"/> means
    /// "…", never an empty state (ch 15 §7).</summary>
    public EdgeState State(LibraryEdgeKind kind) => Relation(kind).State(Slot);
    /// <summary>How many rows the relation holds right now.</summary>
    public int Count(LibraryEdgeKind kind) => Relation(kind).Count(Slot);
    /// <summary>Bumps on every structural change — the number a bound library list compares.</summary>
    public uint EdgeVersion(LibraryEdgeKind kind) => Relation(kind).Version(Slot);

    /// <summary>Membership, by slot. The reverse index answers it in O(1) once the list is long enough to have built
    /// one; below that it is a vectorized scan of a handful of ints (see <c>Edges.cs</c>).</summary>
    public bool Has(LibraryEdgeKind kind, int targetSlot) => Relation(kind).Contains(Slot, targetSlot);
    /// <summary>The optimistic state of one membership (C6) — a pending add spins, a pending remove greys.</summary>
    public EdgePending PendingOf(LibraryEdgeKind kind, int targetSlot) => Relation(kind).PendingOf(Slot, targetSlot);
    /// <summary>When the row was added, or 0 when it is not a member.</summary>
    public int AddedAt(LibraryEdgeKind kind, int targetSlot)
    {
        var table = Relation(kind);
        int at = table.IndexOf(Slot, targetSlot);
        return at < 0 ? 0 : table.Payload(Slot)[at].AddedAt;
    }

    public bool Equals(User other) => other.Slot == Slot;
    public override bool Equals(object? o) => o is User u && u.Slot == Slot;
    public override int GetHashCode() => Slot;
    public static bool operator ==(User a, User b) => a.Slot == b.Slot;
    public static bool operator !=(User a, User b) => a.Slot != b.Slot;
}

// ── 4. the typed membership questions ────────────────────────────────────────────────────────────────────────────────
// Plan §4.14's spelling. One line each over the slot-based core, so a page never converts a handle to a slot by hand
// and the four surfaces that ask "is this saved" all ask the same way.

public readonly partial struct User
{
    public bool Likes(Track track) => E.Liked.Contains(Slot, track.Slot);
    public bool Saves(Album album) => E.SavedAlbums.Contains(Slot, album.Slot);
    public bool Follows(Artist artist) => E.FollowedArtists.Contains(Slot, artist.Slot);
    public bool Saves(Show show) => E.SavedShows.Contains(Slot, show.Slot);
    public bool Pinned(int targetSlot) => E.Pins.Contains(Slot, targetSlot);
}

// ── 5. the optimistic write path (C6) ────────────────────────────────────────────────────────────────────────────────

public readonly partial struct User
{
    /// <summary>Add a membership NOW and tell the shell to make it true. The edge carries
    /// <see cref="EdgePending.Add"/> until <see cref="Settle"/>; a rejection removes it and the heart un-fills.
    /// <para><paramref name="at"/> is 0 for the lists the user reads newest-first (liked tracks, saved albums) and -1
    /// to append. Re-adding something already present updates it in place — a double-click on the heart must not add a
    /// second edge (see <c>EdgeTable.Insert</c>).</para></summary>
    public void Add(LibraryEdgeKind kind, int targetSlot, EntityId targetId, int at = 0)
    {
        if (Slot <= Wavee.Table.None || targetSlot <= Wavee.Table.None) return;
        Relation(kind).Insert(Slot, targetSlot, new LibraryEdge(Entities.Now, 0), at, EdgePending.Add);
        Dispatch(kind, /* add: */ true, Slot, targetSlot, targetId);
    }

    /// <summary>Remove a membership optimistically: the row STAYS, marked <see cref="EdgePending.Remove"/>, so the
    /// list does not jump before the server agrees and an undo has something to undo. <see cref="Settle"/> drops it
    /// for real or brings it back.</summary>
    public void Remove(LibraryEdgeKind kind, int targetSlot, EntityId targetId)
    {
        if (!Relation(kind).MarkRemove(Slot, targetSlot)) return;
        Dispatch(kind, /* add: */ false, Slot, targetSlot, targetId);
    }

    /// <summary>The server answered (C6). Four cases, one call — see <c>EdgeTable.Settle</c>.</summary>
    public bool Settle(LibraryEdgeKind kind, int targetSlot, bool ok) => Relation(kind).Settle(Slot, targetSlot, ok);

    /// <summary>Replace a whole relation from a provider answer (the collection sync's shape). Clears every pending
    /// bit by construction, which is correct: the server's list IS the answer to every write still in flight.</summary>
    public void Replace(LibraryEdgeKind kind, ReadOnlySpan<int> targets, ReadOnlySpan<LibraryEdge> payload,
        EdgeState state = EdgeState.Complete, int total = 0)
        => Relation(kind).Replace(Slot, targets, payload, state, total < targets.Length ? targets.Length : total);

    /// <summary>Replace the rootlist. Its own method because its payload is not a <see cref="LibraryEdge"/>: the
    /// sidebar needs the position, the folder depth and the marker kind, and those are the edge, not the playlist.</summary>
    public void ReplaceRootlist(ReadOnlySpan<int> targets, ReadOnlySpan<RootlistEdge> payload)
        => E.Rootlist.ReplaceRun(Slot, targets, payload);

    /// <summary>THE SHELL SEAM. Implemented by the Spotify half in its own file as another part of this struct; an
    /// unimplemented partial method is erased by the compiler, so CORE — and every test over it — needs no network,
    /// no journal and no session (D17, C1). The shell posts <see cref="Settle"/> back on the UI thread.</summary>
    /// <param name="targetId">The row's packed identity, which is all the PUT needs: the shell formats it into the
    /// request buffer (<c>EntityId.Format</c>, 81 ns, no allocation) instead of the model resolving an interned uri
    /// string per click. It also carries the KIND, so the dispatcher does not have to infer one from the relation.</param>
    static partial void Dispatch(LibraryEdgeKind kind, bool add, int userSlot, int targetSlot, EntityId targetId);
}

// ── 6. typed sugar for the five relations ────────────────────────────────────────────────────────────────────────────

public readonly partial struct User
{
    public void Like(Track track) => Add(LibraryEdgeKind.Liked, track.Slot, track.Id);
    public void Unlike(Track track) => Remove(LibraryEdgeKind.Liked, track.Slot, track.Id);
    public void Save(Album album) => Add(LibraryEdgeKind.SavedAlbums, album.Slot, album.Id);
    public void Unsave(Album album) => Remove(LibraryEdgeKind.SavedAlbums, album.Slot, album.Id);
    public void Follow(Artist artist) => Add(LibraryEdgeKind.FollowedArtists, artist.Slot, artist.Id);
    public void Unfollow(Artist artist) => Remove(LibraryEdgeKind.FollowedArtists, artist.Slot, artist.Id);
    public void Save(Show show) => Add(LibraryEdgeKind.SavedShows, show.Slot, show.Id);
    public void Unsave(Show show) => Remove(LibraryEdgeKind.SavedShows, show.Slot, show.Id);
}

// ── 7. staging + commit (C10, C1) ────────────────────────────────────────────────────────────────────────────────────

/// <summary>One decoded user row. Text is a <see cref="TextRef"/> into the staging arena, never a
/// <see cref="StringId"/>: the decoder cannot intern (§5.6, C1).</summary>
public struct StagedUser : IStagedRow
{
    /// <summary>THE row's identity as the wire gave it (<see cref="StagedId"/>): the packed <see cref="EntityId"/> when
    /// it arrived as 16 gid bytes, the uri's UTF-8 in the arena when it arrived as text. One field, one resolve —
    /// <c>s.Slot(table, in row.Id)</c> — and no format-to-arena-then-parse-back round trip.</summary>
    public StagedId Id;
    public TextRef Name, Image;
    public int Followers, Following;
    public uint Known;
    public Authority Authority;

    /// <inheritdoc cref="IStagedRow.Init"/>
    public void Init(in StagedId id, Authority authority, uint known) { Id = id; Authority = authority; Known = known; }
    /// <inheritdoc cref="IStagedRow.Identity"/>
    public readonly StagedId Identity => Id;
}

public sealed partial class Staging
{
    StagedList<StagedUser>? _users;
    /// <summary>Lazy: a decode that touches no user allocates no user list.</summary>
    public StagedList<StagedUser> Users => _users ??= Register(new StagedList<StagedUser>());
    internal StagedList<StagedUser>? StagedUsers => _users;
}

public static partial class Entities
{
    /// <summary>Typed batch sugar over <see cref="Ensure(Table,ReadOnlySpan{int},uint,FetchPriority)"/> (P4).</summary>
    public static void Ensure(ReadOnlySpan<User> rows, UserFields wanted, FetchPriority priority = FetchPriority.Visible)
        => Ensure(Current.Users, Slots(rows), (uint)wanted, priority);

    /// <inheritdoc cref="Ensure(ReadOnlySpan{User},UserFields,FetchPriority)"/>
    public static void Ensure(User row, UserFields wanted, FetchPriority priority = FetchPriority.Visible)
    {
        int slot = row.Slot;
        Ensure(Current.Users, new ReadOnlySpan<int>(in slot), (uint)wanted, priority);
    }

    static partial void CommitUsers(Staging s)
    {
        var staged = s.StagedUsers;
        if (staged is null || staged.Count == 0) return;

        var t = Current.Users;
        var rows = staged.Span;
        for (int i = 0; i < rows.Length; i++)
        {
            ref var row = ref rows[i];
            int slot = s.Slot(t, in row.Id);
            if (slot == Table.None) continue;   // a row with no identity is not a row
            var authority = row.Authority == Wavee.Authority.None ? s.Authority : row.Authority;

            if ((row.Known & (uint)UserFields.Identity) != 0
                && t.Accepts(slot, (uint)UserFields.Identity, authority, in t.IdentityAuthority))
            {
                // A friend row and an added-by cell both answer Identity, and the friend row's name is the better one;
                // the authority ladder, not an "is it empty" test, is what decides that (D16).
                // SetText, never `t.Name[slot] = …`: the write AddRefs the incoming id and releases the one it
                // overwrites, so a row re-answered by a friend feed and an added-by cell owns one name, not two
                // (defect 1, file header).
                t.SetText(ref t.Name, slot, s.Intern(row.Name));
                t.SetText(ref t.Image, slot, s.Intern(row.Image));
                t.Applied(slot, (uint)UserFields.Identity, authority, ref t.IdentityAuthority);
            }

            if ((row.Known & (uint)UserFields.Social) != 0
                && t.Accepts(slot, (uint)UserFields.Social, authority, in t.ExtrasAuthority))
            {
                t.Followers[slot] = row.Followers;
                t.Following[slot] = row.Following;
                t.Applied(slot, (uint)UserFields.Social, authority, ref t.ExtrasAuthority);
            }
        }
    }
}
