// ── Entities/User.cs — CORE (owner B, wave 1; owner O, wave 5; plan §2, §4.14, ch 07 §7, ch 15 §7-§8) ─────────────────
//
// Role: CORE
// Owner: O (wave 5; the library-edge core is B's wave-1 / B2's gap-batch work and is kept whole)
// Wave: 5
// Budget: 800 lines (+30 % = 1040)
// Spec: ch 15 §8 (LibraryNavOrder, LibrarySelectionCommit, LibraryLayoutBreakpoints, the library search matcher),
//       ch 07 §7 G4/G10 (the curated chip set's staging + commit), WP-4.5 gap 1 (edge AddedAt is UNIX seconds)
//
// THE LIBRARY IS EDGES (G6). No `IsLiked` column, no list objects, no five-collection `LibraryStore`: ONE user row (the
// signed-in account) and six relations off its slot —
//
//        Users[me] ──┬── Liked / SavedAlbums / FollowedArtists / SavedShows   EdgeTable<LibraryEdge> (Liked newest first)
//                    ├── Pins             EdgeTable<LibraryEdge>   → any slot the sidebar can pin (kind in Flags)
//                    └── Rootlist         EdgeTable<RootlistEdge>  → playlist slots + folder markers
//
// "Is it liked" is `Edges.Liked.Contains(me, slot)` (P3); "when was it added" is the EDGE's payload, not the track's (D10).
// THE WRITE PATH IS OPTIMISTIC (C6): `Add` splices the edge in with `EdgePending.Add` and the heart fills that frame; the
// shell (the `Dispatch` partial, `Spotify/Spotify.Library.cs`) PUTs and posts `Settle` — the pending bit IS the outbox,
// and every fake-scope test stays network-free (D17). The row itself carries a name, an avatar and (ch 07 §7 G4) the
// curated content-filter chip set.
//
// IDENTITY IS THE PACKED `EntityId` AND THE ROW OWNS ITS TEXT (docs/plans/wavee/wavee-0.3-entity-identity-memory.md,
// option 2): the write seam carries an `EntityId` (the shell formats it once); a user uri is never a gid, so a user row is
// always the TEXT form; every `StringId` here is ref-counted (defect 1) — `Name`/`Image` through `Table.SetText` /
// `UserTable.ReleaseText`, the chip SLABS through `Entities.RetainText` / `ReleaseText` in `UserTable.SetContentFilters`.
//
// TIME UNITS (WP-4.5 gap 1): every EDGE instant (`LibraryEdge.AddedAt`, `RootlistEdge.AddedAt`) is UNIX SECONDS — what
// every decoder stages and the wire writes back; `Entities.Now` is APP seconds (P7), so the optimistic write converts
// ONCE with `Store.ToUnix` (before the fix an optimistic like dated itself to January 1970).
//
// §8-§10: the library page's CORE (ch 15 §8) — breakpoint, select-in-place commit, the ONE ordering rule, recency, search.
// Rules: single writer, UI thread (C1); no LINQ, no hot-path closures, no async, no boxing (P8/P9); ref-counted text (P6).

using System.Buffers;
using System.Globalization;
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

/// <summary>THE PIN KIND BIT (G-062): what a <see cref="Edges.Pins"/> edge's target means, carried in that edge's
/// <see cref="LibraryEdge.Flags"/> byte. A pin is CROSS-KIND — a playlist, an album, an artist, a show, the Liked Songs
/// collection or a rootlist FOLDER — and a bare target <c>int</c> cannot say which table it indexes (album slot 5 and
/// playlist slot 5 are the same number). The byte is the smallest fix that keeps <see cref="LibraryEdge"/> the payload
/// of all five library relations; the other four always write 0 there.
/// <para>The two non-row kinds: <see cref="Liked"/> targets <see cref="Table.None"/> (there is one Liked collection),
/// and <see cref="Folder"/> targets the <see cref="StringId"/> VALUE of the folder's interned group id — a folder has no
/// row anywhere, only the id the rootlist's markers carry (D10). That id is interned WITHOUT an AddRef, i.e. permanent:
/// a pinned folder id is 16 characters and an account pins a handful, and the scope's edge-text walk
/// (<c>Edges.ReleaseText</c>) does not visit the pins, so a ref-counted id here would be a leaked reference instead.</para>
/// <para><see cref="Unknown"/> (0) is a pin written before this bit existed (the <c>--fake</c> seed's playlists); a
/// reader that needs the kind skips it.</para></summary>
public enum PinKind : byte { Unknown = 0, Playlist = 1, Album = 2, Artist = 3, Show = 4, Liked = 5, Folder = 6 }

// ── 2. the table ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The user rows: the signed-in account, every playlist owner, every added-by, every friend. Most of them
/// carry nothing but a name, which is exactly why they are rows and not objects — 4,000 owners is two columns, not
/// 4,000 records (P1).</summary>
public sealed class UserTable : Table
{
    public Column<StringId> Name, Image;
    public Column<int> Followers, Following;

    /// <summary>This row's curated chips as a RANGE into two shared slabs (ch 07 §7 G4): written whole, read per paint.</summary>
    public Column<int> FilterStart, FilterCount;

    /// <summary>The chip LABEL (<c>display_name</c>, "K-Pop") — what the bar renders.</summary>
    public Column<StringId> FilterTitles;
    /// <summary>The chip TOKEN (<c>text</c>, "k-pop") — what the descriptor join matches on (ch 07 §7 G3).</summary>
    public Column<StringId> FilterTokens;
    /// <summary>Bump allocator over the two chip slabs; never reclaimed — the scope drops in one piece (P5, D9).</summary>
    public int FilterTail;

    /// <summary>Per-group authority (D16); the chip set has its own rung (its own service).</summary>
    public Column<byte> IdentityAuthority, ExtrasAuthority, FiltersAuthority;

    public override EntityKind Kind => EntityKind.User;

    /// <summary>Write a row's whole chip set: appends to the slabs, abandons the previous range's CELLS (P5) but gives its
    /// STRINGS back first (defect 1). Range-addressed, so it uses <see cref="Entities.RetainText"/> rather than
    /// <see cref="Table.SetText"/> (which indexes by slot).</summary>
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

    /// <summary>Give back every string a user row owns (defect 1) — one line per <c>Column&lt;StringId&gt;</c>. Called by
    /// <see cref="Table.FreeSlot"/> (R2) and <see cref="Table.ReleaseAllText"/> (D9).</summary>
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
        FiltersAuthority.EnsureCapacity(capacity);
    }
}

// ── 3. the handle ────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A user: one <c>int</c> over the columns, and the parent of every library relation (G6).</summary>
public readonly partial struct User(int slot) : IEquatable<User>
{
    static UserTable T => Entities.Current.Users;
    static Edges E => Entities.Current.Edges;

    public int Slot { get; } = slot;

    /// <summary>The signed-in account's own row — <see cref="Table.None"/> (an empty library, never a wrong one) until
    /// <c>Entities.Boot</c> resolves the account.</summary>
    public static User Me => new(Entities.Current.MeSlot);

    public bool IsValid => Slot > Table.None && Slot < T.Count;
    public uint Version => T.Version[Slot];
    public bool Knows(UserFields fields) => T.Knows(Slot, (uint)fields);

    /// <summary>THE row's identity, packed — always the TEXT form for a user (a username is never a gid).</summary>
    public EntityId Id => T.Id[Slot];

    /// <summary>The identity as the text-facing VIEW (free: an <see cref="EntityUri"/> IS an <see cref="EntityId"/>).</summary>
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

    /// <summary>The relation behind a <see cref="LibraryEdgeKind"/> (not named <c>Table</c>: that would hide the TYPE).</summary>
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
    /// <summary>Per-row <c>AddedAt</c> (UNIX seconds) for the liked list — an EDGE payload, not a Known bit (ch 07 §7).</summary>
    public ReadOnlySpan<LibraryEdge> LikedEdges => E.Liked.Payload(Slot);
    public ReadOnlySpan<int> SavedAlbumSlots => E.SavedAlbums.Targets(Slot);
    public ReadOnlySpan<int> FollowedArtistSlots => E.FollowedArtists.Targets(Slot);
    public ReadOnlySpan<int> SavedShowSlots => E.SavedShows.Targets(Slot);
    /// <summary>The pinned rows' targets. CROSS-KIND: read each WITH its <see cref="PinKindAt"/> (a folder is no slot).</summary>
    public ReadOnlySpan<int> PinSlots => E.Pins.Targets(Slot);
    /// <summary>The pins' payloads, parallel to <see cref="PinSlots"/>: the added-at instant and the kind byte.</summary>
    public ReadOnlySpan<LibraryEdge> PinEdges => E.Pins.Payload(Slot);
    /// <summary>What the pin at <paramref name="index"/> is.</summary>
    public PinKind PinKindAt(int index)
    {
        var rows = E.Pins.Payload(Slot);
        return (uint)index < (uint)rows.Length ? (PinKind)rows[index].Flags : PinKind.Unknown;
    }

    /// <summary>The pin kind a row of <paramref name="kind"/> pins as; Unknown for a kind the sidebar never pins. PURE.</summary>
    public static PinKind PinKindFor(EntityKind kind) => kind switch
    {
        EntityKind.Playlist => PinKind.Playlist,
        EntityKind.Album => PinKind.Album,
        EntityKind.Artist => PinKind.Artist,
        EntityKind.Show => PinKind.Show,
        _ => PinKind.Unknown,
    };

    /// <summary>The inverse for the four row kinds; <see cref="EntityKind.Unknown"/> for Liked, Folder and Unknown.</summary>
    public static EntityKind EntityKindOf(PinKind kind) => kind switch
    {
        PinKind.Playlist => EntityKind.Playlist,
        PinKind.Album => EntityKind.Album,
        PinKind.Artist => EntityKind.Artist,
        PinKind.Show => EntityKind.Show,
        _ => EntityKind.Unknown,
    };

    /// <summary>Is this wire uri (UTF-8) the Liked Songs collection, in any ylpin spelling? PURE, allocation-free.</summary>
    public static bool IsLikedPinUri(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty || utf8.Length > 256) return false;
        Span<char> chars = stackalloc char[utf8.Length];
        for (int i = 0; i < utf8.Length; i++) chars[i] = utf8[i] < 0x80 ? (char)utf8[i] : (char)0xFFFD;
        return chars.SequenceEqual("spotify:collection") || EntityUri.IsLikedCollection(chars);
    }

    /// <summary>The 1..32-hex group id inside a <c>spotify:folder:</c> wire uri (UTF-8), or empty — a slice, never a copy.</summary>
    public static ReadOnlySpan<byte> FolderIdOf(ReadOnlySpan<byte> utf8)
    {
        ReadOnlySpan<byte> prefix = "spotify:folder:"u8;
        if (!utf8.StartsWith(prefix)) return default;
        var id = utf8[prefix.Length..];
        if (id.Length is 0 or > 32) return default;
        foreach (byte c in id)
            if (c is not (>= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F'))
                return default;
        return id;
    }

    /// <summary>The rootlist as the wire sends it: a flat ordered stream of playlist slots and folder markers.</summary>
    public ReadOnlySpan<int> RootlistSlots => E.Rootlist.Targets(Slot);
    /// <inheritdoc cref="RootlistSlots"/>
    public ReadOnlySpan<RootlistEdge> Rootlist => E.Rootlist.Payload(Slot);
    public EdgeState RootlistState => E.Rootlist.State(Slot);

    /// <summary>Is a relation whole? <see cref="EdgeState.Unknown"/> is "…", never an empty state (ch 15 §7).</summary>
    public EdgeState State(LibraryEdgeKind kind) => Relation(kind).State(Slot);
    public int Count(LibraryEdgeKind kind) => Relation(kind).Count(Slot);
    /// <summary>Bumps on every structural change — the number a bound library list compares.</summary>
    public uint EdgeVersion(LibraryEdgeKind kind) => Relation(kind).Version(Slot);

    /// <summary>Membership, by slot (O(1) through the reverse index once built).</summary>
    public bool Has(LibraryEdgeKind kind, int targetSlot) => Relation(kind).Contains(Slot, targetSlot);
    /// <summary>The optimistic state of one membership (C6) — a pending add spins, a pending remove greys.</summary>
    public EdgePending PendingOf(LibraryEdgeKind kind, int targetSlot) => Relation(kind).PendingOf(Slot, targetSlot);
    /// <summary>When the row was added, in UNIX SECONDS, or 0 when it is not a member (or the edge is undated).</summary>
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

// ── 4. the typed membership questions (plan §4.14's spelling over the slot-based core) ───────────────────────────────

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
    /// <summary>Add a membership NOW (<see cref="EdgePending.Add"/> until <see cref="Settle"/>) and tell the shell.
    /// <paramref name="at"/> 0 prepends (newest-first lists), -1 appends; a re-add updates in place (no second edge). The
    /// edge is stamped in UNIX SECONDS — the unit its synced neighbours carry (WP-4.5 gap 1).</summary>
    public void Add(LibraryEdgeKind kind, int targetSlot, EntityId targetId, int at = 0)
    {
        if (Slot <= Wavee.Table.None || targetSlot <= Wavee.Table.None) return;
        // A pin carries its kind (G-062); the other four relations are single-kind and keep the byte at 0.
        byte flags = kind == LibraryEdgeKind.Pins ? (byte)PinKindFor(targetId.Kind) : (byte)0;
        Relation(kind).Insert(Slot, targetSlot, new LibraryEdge(AddedNow(), flags), at, EdgePending.Add);
        Dispatch(kind, /* add: */ true, Slot, targetSlot, targetId);
    }

    /// <summary>"Now" as an edge instant: <c>Store.ToUnix(Entities.Now)</c>, clamped into the payload's <c>int</c>.</summary>
    public static int AddedNow()
    {
        long unix = Store.ToUnix(Entities.Now);
        return unix <= 0 ? 0 : unix >= int.MaxValue ? int.MaxValue : (int)unix;
    }

    /// <summary>Remove optimistically: the row STAYS marked <see cref="EdgePending.Remove"/> until <see cref="Settle"/>.</summary>
    public void Remove(LibraryEdgeKind kind, int targetSlot, EntityId targetId)
    {
        if (!Relation(kind).MarkRemove(Slot, targetSlot)) return;
        Dispatch(kind, /* add: */ false, Slot, targetSlot, targetId);
    }

    /// <summary>The server answered (C6). Four cases, one call — see <c>EdgeTable.Settle</c>.</summary>
    public bool Settle(LibraryEdgeKind kind, int targetSlot, bool ok) => Relation(kind).Settle(Slot, targetSlot, ok);

    /// <summary>Replace a whole relation from a provider answer; clears every pending bit (the server's list IS the answer).</summary>
    public void Replace(LibraryEdgeKind kind, ReadOnlySpan<int> targets, ReadOnlySpan<LibraryEdge> payload,
        EdgeState state = EdgeState.Complete, int total = 0)
        => Relation(kind).Replace(Slot, targets, payload, state, total < targets.Length ? targets.Length : total);

    /// <summary>Replace the rootlist (its payload is a <see cref="RootlistEdge"/>). THE LIST OWNS ITS FOLDER STRINGS
    /// (G-062): AddRef every incoming folder name/id FIRST, then release the replaced list's (<see cref="Edges.ReleaseRootlistText"/>)
    /// — the order <c>Entities.CommitRootlist</c> keeps.</summary>
    public void ReplaceRootlist(ReadOnlySpan<int> targets, ReadOnlySpan<RootlistEdge> payload)
    {
        for (int i = 0; i < payload.Length; i++)
        {
            Entities.Strings.AddRef(payload[i].FolderName);
            Entities.Strings.AddRef(payload[i].FolderId);
        }
        E.ReleaseRootlistText(Slot);
        E.Rootlist.ReplaceRun(Slot, targets, payload);
    }

    /// <summary>THE shell seam (<c>Spotify/Spotify.Library.cs</c>): writes only for the signed-in account of a Spotify
    /// scope, so fake-scope tests see the optimistic edge and no network (D17); posts <see cref="Settle"/> back on the UI
    /// thread. <paramref name="targetId"/> is the packed identity (with its KIND) the request formats once.</summary>
    static partial void Dispatch(LibraryEdgeKind kind, bool add, int userSlot, int targetSlot, EntityId targetId);

    /// <summary>The seed's DIRECT writer (UI thread) for the curated chip set (ch 07 §7 G4): the whole set, in server
    /// order, and the <see cref="UserFields.ContentFilters"/> bit — an empty pair is a real, known-empty answer.</summary>
    public void SetContentFilters(ReadOnlySpan<string> titles, ReadOnlySpan<string> tokens)
    {
        if (!IsValid) return;
        int n = titles.Length < tokens.Length ? titles.Length : tokens.Length;
        StringId[] ids = ArrayPool<StringId>.Shared.Rent(Math.Max(2, 2 * n));
        try
        {
            for (int i = 0; i < n; i++) { ids[i] = Entities.Strings.Intern(titles[i]); ids[n + i] = Entities.Strings.Intern(tokens[i]); }
            T.SetContentFilters(Slot, ids.AsSpan(0, n), ids.AsSpan(n, n));
            T.Applied(Slot, (uint)UserFields.ContentFilters, Wavee.Authority.Seed, ref T.FiltersAuthority);
        }
        finally { ArrayPool<StringId>.Shared.Return(ids); }
    }
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

/// <summary>One decoded user row; text is a <see cref="TextRef"/> into the arena (the decoder cannot intern, C1).</summary>
public struct StagedUser : IStagedRow
{
    /// <summary>THE row's identity as the wire gave it (packed gid or uri text) — one field, one resolve.</summary>
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

/// <summary>One curated chip in SERVER order (ch 07 §7 G4): an owner's answer is its contiguous run; an EMPTY answer is ONE
/// row with both texts empty — a known-empty publish that hands the bar to the derived fallback (G10).</summary>
public struct StagedContentFilter
{
    public StagedId Owner;
    public TextRef Title, Token;
}

public sealed partial class Staging
{
    StagedList<StagedUser>? _users;
    /// <summary>Lazy: a decode that touches no user allocates no user list.</summary>
    public StagedList<StagedUser> Users => _users ??= Register(new StagedList<StagedUser>());
    internal StagedList<StagedUser>? StagedUsers => _users;

    StagedList<StagedContentFilter>? _contentFilters;
    /// <summary>Lazy, like <see cref="Users"/>: the curated chip rows (<see cref="StagedContentFilter"/>).</summary>
    public StagedList<StagedContentFilter> ContentFilters => _contentFilters ??= Register(new StagedList<StagedContentFilter>());
    internal StagedList<StagedContentFilter>? StagedContentFilters => _contentFilters;
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
        if (staged is not null && staged.Count > 0)
        {
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
                    // The authority ladder, not an "is it empty" test, picks the better name (D16); SetText AddRefs the new
                    // id and releases the overwritten one (defect 1).
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
        CommitContentFilters(s);
    }

    /// <summary>The curated chip runs (G4): per owner the WHOLE set in staged order, then the Known bit; a run of one empty
    /// row is a known-EMPTY set (G10) — written as zero chips, never skipped.</summary>
    static void CommitContentFilters(Staging s)
    {
        var staged = s.StagedContentFilters;
        if (staged is null || staged.Count == 0) return;
        var t = Current.Users;
        var rows = staged.Span;
        StringId[] ids = ArrayPool<StringId>.Shared.Rent(2 * rows.Length);
        try
        {
            int i = 0;
            while (i < rows.Length)
            {
                int slot = s.Slot(t, in rows[i].Owner);
                int end = i + 1;
                while (end < rows.Length && s.Slot(t, in rows[end].Owner) == slot) end++;
                int n = 0, span = end - i;
                for (int k = i; k < end; k++)
                {
                    if (rows[k].Title.IsEmpty && rows[k].Token.IsEmpty) continue;       // the known-empty marker
                    ids[n] = s.Intern(rows[k].Title);
                    ids[span + n] = s.Intern(rows[k].Token.IsEmpty ? rows[k].Title : rows[k].Token);
                    n++;
                }
                if (slot != Table.None
                    && t.Accepts(slot, (uint)UserFields.ContentFilters, s.Authority, in t.FiltersAuthority))
                {
                    t.SetContentFilters(slot, ids.AsSpan(0, n), ids.AsSpan(span, n));
                    t.Applied(slot, (uint)UserFields.ContentFilters, s.Authority, ref t.FiltersAuthority);
                }
                i = end;
            }
        }
        finally { ArrayPool<StringId>.Shared.Return(ids); }
    }
}

// ── persistence (Store.cs's per-kind seam) ──────────────────────────────────────────────────────────────────────────

/// <summary>How a user row survives a restart. Persists <see cref="UserFields.Identity"/> and
/// <see cref="UserFields.Social"/> only — the curated <see cref="UserFields.ContentFilters"/> chip set is NOT
/// persisted: it lives in two shared, session-local slabs addressed by a bump-allocated RANGE
/// (<see cref="UserTable.FilterStart"/>/<see cref="UserTable.FilterCount"/>), and its own service answers it
/// quickly, so the gap costs one extra round trip rather than a wrong range into a slab this session never filled
/// the same way.
/// <para>STORE THREAD (both halves) — see <see cref="ShowShape"/>'s note; the shape and the rule are the same for
/// every kind.</para></summary>
public sealed class UserShape : KindShape
{
    static readonly StoreColumn[] Cols =
    [
        new("name", StoreType.Text, StoreColumnFlags.Title),
        new("image", StoreType.Text),
        new("followers", StoreType.Int),
        new("following", StoreType.Int),
        new("identity_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("extras_auth", StoreType.Int, StoreColumnFlags.Authority),
    ];

    const uint PersistedFields = (uint)(UserFields.Identity | UserFields.Social);

    public override EntityKind Kind => EntityKind.User;
    public override string Table => "user";
    public override ReadOnlySpan<StoreColumn> Columns => Cols;

    public override void Save(Staging s, RowWriter w)
    {
        var rows = s.StagedUsers;
        if (rows is null) return;
        var span = rows.Span;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var row = ref span[i];
            uint known = row.Known & PersistedFields;
            bool identity = (known & (uint)UserFields.Identity) != 0;
            bool social = (known & (uint)UserFields.Social) != 0;

            if (identity)
            {
                w.Text(0, row.Name);
                w.Text(1, row.Image);
                w.Int(4, (int)row.Authority);
            }
            else { w.Null(0); w.Null(1); w.Null(4); }

            if (social)
            {
                w.Int(2, row.Followers);
                w.Int(3, row.Following);
                w.Int(5, (int)row.Authority);
            }
            else { w.Null(2); w.Null(3); w.Null(5); }

            w.Emit(row.Id, known, Entities.Now, Entities.Now);
        }
    }

    public override void Load(RowReader r, Staging into)
    {
        ref var row = ref into.Users.Add();
        row.Id = r.Uri;
        row.Name = r.Text(0);
        row.Image = r.Text(1);
        row.Followers = (int)r.Int(2);
        row.Following = (int)r.Int(3);
        row.Known = r.Known & PersistedFields;
        row.Authority = (Authority)Math.Max(r.Int(4), r.Int(5));
    }
}

// ── 8. the library page's small pure rules (ch 15 §8, verbatim) ──────────────────────────────────────────────────────

/// <summary>The library master–detail's collapse breakpoint: below <see cref="CollapseBelow"/> the columns become a
/// breadcrumb drill-in, with hysteresis so a window on the boundary does not flip-flop.</summary>
public static class LibraryLayoutBreakpoints
{
    public const float CollapseBelow = 640f;   // ~NavigationView.CompactModeThresholdWidth
    public const float Hysteresis = 24f;

    /// <summary>Collapse when narrow; un-collapse only once comfortably wide. An unmeasured width (≤ 0) keeps the answer.</summary>
    public static bool Collapsed(float w, bool wasCollapsed)
    {
        if (w <= 0f) return wasCollapsed;
        return wasCollapsed ? w < CollapseBelow + Hysteresis : w < CollapseBelow;
    }
}

/// <summary>Which kind of library-search hit is being committed into the browse selection.</summary>
public enum LibrarySelectKind { Artist, Album }

/// <summary>The select-in-place rule behind library search: given the clicked hit and the page's shape, which pieces of
/// page state change. A null <see cref="SelectedKey"/>/<see cref="AlbumKey"/>/<see cref="Depth"/> means "leave that
/// signal alone" — the two keys are the PERSISTED pair.</summary>
public readonly record struct LibrarySelectionCommit(string? SelectedKey, string? AlbumKey, bool ClearFilter, int? Depth)
{
    /// <summary>Write nothing — the hit carried no usable uri.</summary>
    public static readonly LibrarySelectionCommit None = default;

    public bool IsNone => SelectedKey is null && AlbumKey is null && !ClearFilter && Depth is null;

    /// <summary>Artist → select it and RESET the discography key, collapsed depth 1. Album in the albums view → the
    /// master selection, depth 1. Album in the artists view → the discography pick WITH its owning artist, depth 2.
    /// The filter is always cleared (the search view is gated on a non-empty query).</summary>
    public static LibrarySelectionCommit For(LibrarySelectKind kind, bool artistsView, bool collapsed, string uri,
                                             string ownerArtistUri = "")
    {
        if (string.IsNullOrEmpty(uri)) return None;
        if (kind == LibrarySelectKind.Artist)
            return new("artist:" + uri, "", ClearFilter: true, collapsed ? 1 : null);
        if (!artistsView)
            return new("album:" + uri, null, ClearFilter: true, collapsed ? 1 : null);
        return new(string.IsNullOrEmpty(ownerArtistUri) ? null : "artist:" + ownerArtistUri,
                   "album:" + uri, ClearFilter: true, collapsed ? 2 : null);
    }

    public static LibrarySelectionCommit ForArtist(bool artistsView, bool collapsed, string uri)
        => For(LibrarySelectKind.Artist, artistsView, collapsed, uri);

    public static LibrarySelectionCommit ForAlbum(bool artistsView, bool collapsed, string uri, string ownerArtistUri)
        => For(LibrarySelectKind.Album, artistsView, collapsed, uri, ownerArtistUri);
}

// ── 9. the ONE library ordering rule (ch 15 §8, LibraryNavOrder verbatim over an allocation-free row view) ─────────

/// <summary>The sort keys the library pickers offer (rows 0..4). The int codes are PERSISTED
/// (<c>library.&lt;kind&gt;.sort</c>) and shared with the sidebar's Library V3 — never renumber.</summary>
public enum LibraryNavSort : byte { Recents = 0, RecentlyAdded = 1, Alphabetical = 2, Creator = 3, ReleaseDate = 4 }

/// <summary>What an order needs from a row, as a RECORD — the test fixture shape and 0.2.9's own.</summary>
public readonly record struct LibraryNavFacts(string Uri, string Title, string Subtitle, int Year, string? CoverUrl);

/// <summary>What an order needs from a row, as a VIEW — over slots (<see cref="LibraryRows"/>) or records
/// (<see cref="LibraryFactsRows"/>). Rows arrive in SOURCE order; that index is the last tie-break (every order is total).
/// <c>PlayedAt</c> is unix ms (0 = never); <c>Uri</c> slices an existing string or <c>scratch</c>; <c>Cover</c> is "" for none.</summary>
public interface ILibraryNavRows
{
    int Count { get; }
    long PlayedAt(int row);
    int Year(int row);
    string Title(int row);
    string Subtitle(int row);
    ReadOnlySpan<char> Uri(int row, Span<char> scratch);
    string Cover(int row);
}

/// <summary><see cref="ILibraryNavRows"/> over the record fixtures.</summary>
public readonly struct LibraryFactsRows(LibraryNavFacts[] rows, long[]? played) : ILibraryNavRows
{
    public int Count => rows.Length;
    public long PlayedAt(int row) => played is null ? 0 : played[row];
    public int Year(int row) => rows[row].Year;
    public string Title(int row) => rows[row].Title;
    public string Subtitle(int row) => rows[row].Subtitle;
    public ReadOnlySpan<char> Uri(int row, Span<char> scratch) => rows[row].Uri;
    public string Cover(int row) => rows[row].CoverUrl ?? "";
}

/// <summary>A reusable sorter with cached comparison delegates: ordering allocates NOTHING after construction (ch 15 §9).</summary>
public sealed class LibraryNavSorter<TRows> where TRows : ILibraryNavRows
{
    static readonly StringComparer Name = StringComparer.OrdinalIgnoreCase;
    TRows _rows = default!;
    int _sign = 1;
    readonly Comparison<int> _recents, _added, _alphabetical, _creator, _release;

    public LibraryNavSorter()
    {
        _recents = Recents; _added = Added; _alphabetical = Alphabetical; _creator = Creator; _release = Release;
    }

    /// <summary>Write the permutation into <paramref name="into"/> (≥ Count). Recents: played newest-first, then never-played
    /// in source order (the block split survives desc). RecentlyAdded: source order. Alphabetical / Creator: title /
    /// subtitle, then title, then uri. ReleaseDate: year desc, unknown years sink. Desc reverses the tie-breaks too.</summary>
    public void Order(in TRows rows, LibraryNavSort sort, bool desc, Span<int> into)
    {
        int n = rows.Count;
        for (int i = 0; i < n; i++) into[i] = i;
        if (n < 2) return;
        _rows = rows;
        _sign = desc ? -1 : 1;
        into[..n].Sort(sort switch
        {
            LibraryNavSort.Recents => _recents,
            LibraryNavSort.Alphabetical => _alphabetical,
            LibraryNavSort.Creator => _creator,
            LibraryNavSort.ReleaseDate => _release,
            _ => _added,
        });
        _rows = default!;
    }

    int Recents(int a, int b)
    {
        long pa = _rows.PlayedAt(a), pb = _rows.PlayedAt(b);
        bool ha = pa > 0, hb = pb > 0;
        if (ha != hb) return ha ? -1 : 1;                       // block split — direction-proof
        int c = ha ? pb.CompareTo(pa) : a.CompareTo(b);         // newest play first · never played: source order
        return _sign * (c != 0 ? c : ByTitle(a, b));
    }

    int Added(int a, int b) => _sign * a.CompareTo(b);
    int Alphabetical(int a, int b) => _sign * ByTitle(a, b);

    int Creator(int a, int b)
    {
        int c = Name.Compare(_rows.Subtitle(a), _rows.Subtitle(b));
        return _sign * (c != 0 ? c : ByTitle(a, b));
    }

    int Release(int a, int b)
    {
        int ya = _rows.Year(a), yb = _rows.Year(b);
        if (ya > 0 != yb > 0) return ya > 0 ? -1 : 1;           // unknown years sink as a block
        int c = yb.CompareTo(ya);
        return _sign * (c != 0 ? c : ByTitle(a, b));
    }

    int ByTitle(int a, int b)
    {
        int c = Name.Compare(_rows.Title(a), _rows.Title(b));
        if (c == 0)
        {
            Span<char> sa = stackalloc char[EntityId.MaxGidTextChars], sb = stackalloc char[EntityId.MaxGidTextChars];
            c = _rows.Uri(a, sa).SequenceCompareTo(_rows.Uri(b, sb));
        }
        return c != 0 ? c : a.CompareTo(b);
    }
}

/// <summary>The ordering rule's keys and its record-array entry points (the 0.2.9 surface the tests pin).</summary>
public static class LibraryNavOrder
{
    const ulong FnvBasis = 14695981039346656037UL, FnvPrime = 1099511628211UL;

    /// <summary>The permutation over records (tests; <see cref="LibraryNavSorter{TRows}"/> is the page's path).</summary>
    public static int[] Order(LibraryNavFacts[] rows, LibraryNavSort sort, bool desc, IReadOnlyDictionary<string, long> lastPlayed)
    {
        var played = new long[rows.Length];
        for (int i = 0; i < rows.Length; i++) played[i] = lastPlayed.TryGetValue(rows[i].Uri, out long ms) ? ms : 0;
        var idx = new int[rows.Length];
        new LibraryNavSorter<LibraryFactsRows>().Order(new LibraryFactsRows(rows, played), sort, desc, idx);
        return idx;
    }

    /// <summary>Identity of the SEQUENCE: <c>count ":" FNV-1a-hex16</c> over the uris (separator 0x1F).</summary>
    public static string OrderKey(LibraryNavFacts[] rows) => OrderKey(new LibraryFactsRows(rows, null));

    /// <summary>Identity of what the rows DISPLAY (uri, title, subtitle, cover) — never selection.</summary>
    public static string FactsKey(LibraryNavFacts[] rows) => FactsKey(new LibraryFactsRows(rows, null));

    /// <inheritdoc cref="OrderKey(LibraryNavFacts[])"/>
    public static string OrderKey<TRows>(in TRows rows) where TRows : ILibraryNavRows
    {
        ulong h = FnvBasis;
        Span<char> scratch = stackalloc char[EntityId.MaxGidTextChars];
        for (int i = 0; i < rows.Count; i++) h = Fnv(h, rows.Uri(i, scratch));
        return rows.Count.ToString(CultureInfo.InvariantCulture) + ":" + h.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc cref="FactsKey(LibraryNavFacts[])"/>
    public static string FactsKey<TRows>(in TRows rows) where TRows : ILibraryNavRows
    {
        ulong h = FnvBasis;
        Span<char> scratch = stackalloc char[EntityId.MaxGidTextChars];
        for (int i = 0; i < rows.Count; i++)
            h = Fnv(Fnv(Fnv(Fnv(h, rows.Uri(i, scratch)), rows.Title(i)), rows.Subtitle(i)), rows.Cover(i));
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }

    static ulong Fnv(ulong h, ReadOnlySpan<char> s)
    {
        for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= FnvPrime; }
        h ^= 0x1F; h *= FnvPrime;   // field separator so ("ab","c") ≠ ("a","bc")
        return h;
    }
}

/// <summary><see cref="ILibraryNavRows"/> over slots of ONE kind in a caller-owned buffer, plus the column reads, the
/// title filter and the play-recency read the page and the search share. UI thread (C1).</summary>
public readonly struct LibraryRows(EntityKind kind, int[] slots, int count, long[]? played = null) : ILibraryNavRows
{
    readonly EntityKind _kind = kind;
    readonly int[] _slots = slots;
    readonly long[]? _played = played;
    readonly int _count = count;

    public int Count => _count;
    public int SlotAt(int row) => _slots[row];
    public long PlayedAt(int row) => _played is null ? 0 : _played[row];
    public int Year(int row) => YearOf(_kind, _slots[row]);
    public string Title(int row) => TitleOf(_kind, _slots[row]);
    public string Subtitle(int row) => SubtitleOf(_kind, _slots[row]);
    public ReadOnlySpan<char> Uri(int row, Span<char> scratch) => UriOf(IdOf(_kind, _slots[row]), scratch);
    public string Cover(int row) => Entities.Strings.Resolve(ImageOf(_kind, _slots[row]));

    public static EntityId IdOf(EntityKind kind, int slot) => kind switch
    {
        EntityKind.Album => Entities.Current.Albums.Id[slot],
        EntityKind.Artist => Entities.Current.Artists.Id[slot],
        EntityKind.Show => Entities.Current.Shows.Id[slot],
        EntityKind.Track => Entities.Current.Tracks.Id[slot],
        _ => default,
    };

    public static string TitleOf(EntityKind kind, int slot) => Entities.Strings.Resolve(kind switch
    {
        EntityKind.Album => Entities.Current.Albums.Title[slot],
        EntityKind.Artist => Entities.Current.Artists.Name[slot],
        EntityKind.Show => Entities.Current.Shows.Title[slot],
        EntityKind.Track => Entities.Current.Tracks.Title[slot],
        _ => StringId.Empty,
    });

    /// <summary>Album → its first billed artist's name; show → the publisher; anything else → "".</summary>
    public static string SubtitleOf(EntityKind kind, int slot)
    {
        if (kind == EntityKind.Show) return Entities.Strings.Resolve(Entities.Current.Shows.Publisher[slot]);
        if (kind != EntityKind.Album) return "";
        var artists = Entities.Current.Edges.AlbumArtists.Targets(slot);
        return artists.Length > 0 && artists[0] > Table.None ? Entities.Strings.Resolve(Entities.Current.Artists.Name[artists[0]]) : "";
    }

    public static StringId ImageOf(EntityKind kind, int slot) => kind switch
    {
        EntityKind.Album => Entities.Current.Albums.Image[slot],
        EntityKind.Artist => Entities.Current.Artists.Image[slot],
        EntityKind.Show => Entities.Current.Shows.Image[slot],
        EntityKind.Track => Entities.Current.Tracks.Image[slot],
        _ => StringId.Empty,
    };

    public static int YearOf(EntityKind kind, int slot)
        => kind == EntityKind.Album && Entities.Current.Albums.Knows(slot, (uint)AlbumFields.Year) ? Entities.Current.Albums.Year[slot] : 0;

    /// <summary>The uri text of an id: the interned string for the text form, the formatted scratch for a gid.</summary>
    public static ReadOnlySpan<char> UriOf(EntityId id, Span<char> scratch)
        => id.Form switch
        {
            EntityForm.Text => Entities.Strings.Resolve(id.TextId),
            EntityForm.Gid => scratch[..id.Format(scratch)],
            _ => default,
        };

    /// <summary>The play-recency read (<c>Shell.PlayLog.Recency</c>: uri → last played, unix ms; 0 = never). A span
    /// probe through the dictionary's alternate lookup, so a gid row never becomes a string.</summary>
    public static long PlayedOf(IReadOnlyDictionary<string, long>? map, EntityId id)
    {
        if (map is null || map.Count == 0 || id.IsEmpty) return 0;
        Span<char> scratch = stackalloc char[EntityId.MaxGidTextChars];
        var uri = UriOf(id, scratch);
        if (uri.IsEmpty) return 0;
        if (map is Dictionary<string, long> d && d.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup))
            return lookup.TryGetValue(uri, out long ms) ? ms : 0;
        return map.TryGetValue(uri.ToString(), out long v) ? v : 0;
    }

    /// <summary>Fill <paramref name="into"/> with each slot's last-played stamp (the sort's precomputed input).</summary>
    public static void FillPlayed(EntityKind kind, ReadOnlySpan<int> slots, IReadOnlyDictionary<string, long>? map, Span<long> into)
    {
        for (int i = 0; i < slots.Length; i++) into[i] = PlayedOf(map, IdOf(kind, slots[i]));
    }

    /// <summary>The browse title filter (instant, case-insensitive contains): the matching slots, in source order,
    /// written into <paramref name="into"/>; returns how many. An empty query passes every row.</summary>
    public static int Filter(EntityKind kind, ReadOnlySpan<int> source, ReadOnlySpan<char> query, Span<int> into)
    {
        var q = query.Trim();
        int n = 0;
        for (int i = 0; i < source.Length && n < into.Length; i++)
        {
            if (source[i] <= Table.None) continue;
            if (q.Length == 0 || TitleOf(kind, source[i]).AsSpan().Contains(q, StringComparison.OrdinalIgnoreCase))
                into[n++] = source[i];
        }
        return n;
    }
}

// ── 10. the cache-only library search (ch 15 §7 DATA GAP 5; 0.2.9 LibrarySearchIndex over the resident tables) ──────

/// <summary>Artists → followed artists ▸ releases ▸ tracks. Albums → saved albums ▸ tracks.</summary>
public enum LibrarySearchScope : byte { Artists, Albums }

/// <summary>Which field matched on a hit whose OWN name did not.</summary>
public enum LibraryMatchKind : byte { None, Album, Track }

/// <summary>Why a non-exact hit appeared; only an attributable, non-name reason explains (an empty one renders nothing).</summary>
public readonly record struct MatchReason(LibraryMatchKind Kind, string? Term = null)
{
    public static readonly MatchReason None = default;
    public bool ShouldExplain => Kind != LibraryMatchKind.None && !string.IsNullOrEmpty(Term);
}

/// <summary>A track under an album hit: <see cref="MatchLen"/> 0 ⇒ shown through its album/artist, no highlight;
/// <see cref="AlbumIndex"/> is the play index.</summary>
public readonly record struct LibraryTrackHit(int Slot, int AlbumIndex, int MatchStart, int MatchLen);

/// <summary>One album hit: ALL its tracks when its own or its artist's name matched, else only the matching ones.</summary>
public readonly record struct LibraryAlbumHit(int Slot, int MatchStart, int MatchLen, int TrackStart, int TrackCount, MatchReason Match);

/// <summary>One artist hit: present when its name matched OR anything under it did.</summary>
public readonly record struct LibraryArtistHit(int Slot, int MatchStart, int MatchLen, int AlbumStart, int AlbumCount, MatchReason Match);

/// <summary>The grouped result, as three flat pooled runs addressed by ranges (no records per level, no per-query
/// allocation once warm). Reused by its owner across queries.</summary>
public sealed class LibraryHits
{
    public const int ArtistCap = 200;
    public const int TracksPerAlbumCap = 500;

    LibraryArtistHit[] _artists = new LibraryArtistHit[16];
    LibraryAlbumHit[] _albums = new LibraryAlbumHit[32];
    LibraryTrackHit[] _tracks = new LibraryTrackHit[64];
    int _artistCount, _albumCount, _trackCount;
    internal readonly HashSet<int> Seen = new();

    public LibrarySearchScope Scope { get; private set; }
    public string Query { get; private set; } = "";
    /// <summary>Bumps on every run — the value a memo compares.</summary>
    public uint Generation { get; private set; }

    public ReadOnlySpan<LibraryArtistHit> Artists => new(_artists, 0, _artistCount);
    /// <summary>The top-level album hits (albums scope); empty in the artists scope.</summary>
    public ReadOnlySpan<LibraryAlbumHit> Albums => Scope == LibrarySearchScope.Albums ? new(_albums, 0, _albumCount) : default;
    public ReadOnlySpan<LibraryAlbumHit> AlbumsOf(in LibraryArtistHit artist) => new(_albums, artist.AlbumStart, artist.AlbumCount);
    public ReadOnlySpan<LibraryTrackHit> TracksOf(in LibraryAlbumHit album) => new(_tracks, album.TrackStart, album.TrackCount);
    public bool IsEmpty => _artistCount == 0 && (Scope != LibrarySearchScope.Albums || _albumCount == 0);

    public static int IndexOf(ReadOnlySpan<LibraryArtistHit> hits, int slot)
    {
        for (int i = 0; i < hits.Length; i++) if (hits[i].Slot == slot) return i;
        return -1;
    }

    public static int IndexOf(ReadOnlySpan<LibraryAlbumHit> hits, int slot)
    {
        for (int i = 0; i < hits.Length; i++) if (hits[i].Slot == slot) return i;
        return -1;
    }

    internal void Reset(LibrarySearchScope scope, string query)
    {
        Scope = scope; Query = query; Generation++;
        _artistCount = _albumCount = _trackCount = 0;
    }

    internal int AlbumCount { get => _albumCount; set => _albumCount = value; }
    internal int TrackCount { get => _trackCount; set => _trackCount = value; }
    internal int ArtistCount { get => _artistCount; set => _artistCount = value; }
    internal Span<LibraryAlbumHit> AlbumRun(int start, int count) => new(_albums, start, count);
    internal Span<LibraryArtistHit> ArtistRun => new(_artists, 0, _artistCount);

    internal void Add(in LibraryArtistHit hit) { Grow(ref _artists, _artistCount); _artists[_artistCount++] = hit; }
    internal void Add(in LibraryAlbumHit hit) { Grow(ref _albums, _albumCount); _albums[_albumCount++] = hit; }
    internal void Add(in LibraryTrackHit hit) { Grow(ref _tracks, _trackCount); _tracks[_trackCount++] = hit; }
    internal LibraryTrackHit TrackAt(int i) => _tracks[i];

    static void Grow<T>(ref T[] a, int count) { if (count == a.Length) Array.Resize(ref a, a.Length * 2); }
}

public readonly partial struct User
{
    static readonly Comparison<LibraryArtistHit> s_artistRank = static (a, b) =>
    {
        int pa = a.MatchLen > 0 ? 0 : 1, pb = b.MatchLen > 0 ? 0 : 1;
        if (pa != pb) return pa.CompareTo(pb);
        return string.Compare(LibraryRows.TitleOf(EntityKind.Artist, a.Slot), LibraryRows.TitleOf(EntityKind.Artist, b.Slot),
                              StringComparison.OrdinalIgnoreCase);
    };

    static readonly Comparison<LibraryAlbumHit> s_albumRank = static (a, b) =>
    {
        int pa = a.MatchLen > 0 ? 0 : 1, pb = b.MatchLen > 0 ? 0 : 1;
        if (pa != pb) return pa.CompareTo(pb);
        return LibraryRows.YearOf(EntityKind.Album, b.Slot).CompareTo(LibraryRows.YearOf(EntityKind.Album, a.Slot));
    };

    /// <summary>THE library search (ch 15 §7 GAP 5): cache-only over the RESIDENT tables — no network, no demand. 0.2.9's
    /// cascade: an artist match shows all its releases, an album/artist match all its tracks, else only what matched (with
    /// <c>MatchLen 0</c> + a <see cref="MatchReason"/>); name matches rank first, stable. Fills <paramref name="into"/>. C1.</summary>
    public static LibraryHits SearchLibrary(LibrarySearchScope scope, ReadOnlySpan<char> query, LibraryHits? into = null)
    {
        var hits = into ?? new LibraryHits();
        var q = query.Trim();
        hits.Reset(scope, q.ToString());
        var scope0 = Entities.Current;
        if (q.IsEmpty || scope0 is null || scope0.MeSlot <= Table.None) return hits;
        var e = scope0.Edges;
        int me = scope0.MeSlot;

        if (scope == LibrarySearchScope.Albums)
        {
            hits.Seen.Clear();
            var saved = e.SavedAlbums.Targets(me);
            for (int i = 0; i < saved.Length; i++)
                if (saved[i] > Table.None && hits.Seen.Add(saved[i])) SearchAlbum(hits, e, saved[i], parentMatched: false, q);
            StableSort(hits.AlbumRun(0, hits.AlbumCount), s_albumRank);
            return hits;
        }

        var followed = e.FollowedArtists.Targets(me);
        for (int i = 0; i < followed.Length; i++)
        {
            int artist = followed[i];
            if (artist <= Table.None) continue;
            int at = LibraryRows.TitleOf(EntityKind.Artist, artist).AsSpan().IndexOf(q, StringComparison.OrdinalIgnoreCase);
            int albumStart = hits.AlbumCount, trackMark = hits.TrackCount;
            hits.Seen.Clear();
            SearchReleases(hits, e, e.ArtistAlbums.Targets(artist), at >= 0, q);
            SearchReleases(hits, e, e.ArtistSingles.Targets(artist), at >= 0, q);
            SearchReleases(hits, e, e.ArtistCompilations.Targets(artist), at >= 0, q);
            int albumCount = hits.AlbumCount - albumStart;
            if (at < 0 && albumCount == 0) { hits.AlbumCount = albumStart; hits.TrackCount = trackMark; continue; }
            var run = hits.AlbumRun(albumStart, albumCount);
            StableSort(run, s_albumRank);
            hits.Add(new LibraryArtistHit(artist, at, at >= 0 ? q.Length : 0, albumStart, albumCount,
                                          at >= 0 ? MatchReason.None : ArtistReason(hits, run)));
        }
        StableSort(hits.ArtistRun, s_artistRank);
        if (hits.ArtistCount > LibraryHits.ArtistCap) hits.ArtistCount = LibraryHits.ArtistCap;
        return hits;
    }

    static void SearchReleases(LibraryHits hits, Edges e, ReadOnlySpan<int> releases, bool parentMatched, ReadOnlySpan<char> q)
    {
        for (int i = 0; i < releases.Length; i++)
            if (releases[i] > Table.None && hits.Seen.Add(releases[i])) SearchAlbum(hits, e, releases[i], parentMatched, q);
    }

    static void SearchAlbum(LibraryHits hits, Edges e, int album, bool parentMatched, ReadOnlySpan<char> q)
    {
        int at = LibraryRows.TitleOf(EntityKind.Album, album).AsSpan().IndexOf(q, StringComparison.OrdinalIgnoreCase);
        bool albumMatched = at >= 0 || parentMatched;
        int trackStart = hits.TrackCount;
        var tracks = e.AlbumTracks.Targets(album);
        for (int i = 0; i < tracks.Length && hits.TrackCount - trackStart < LibraryHits.TracksPerAlbumCap; i++)
        {
            if (tracks[i] <= Table.None) continue;
            string title = LibraryRows.TitleOf(EntityKind.Track, tracks[i]);
            if (title.Length == 0) continue;
            int tm = title.AsSpan().IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (albumMatched || tm >= 0) hits.Add(new LibraryTrackHit(tracks[i], i, tm, tm >= 0 ? q.Length : 0));
        }
        int count = hits.TrackCount - trackStart;
        if (!albumMatched && count == 0) { hits.TrackCount = trackStart; return; }
        var reason = MatchReason.None;
        if (at < 0 && !parentMatched)
            for (int k = trackStart; k < hits.TrackCount; k++)
                if (hits.TrackAt(k).MatchLen > 0)
                {
                    reason = new MatchReason(LibraryMatchKind.Track, LibraryRows.TitleOf(EntityKind.Track, hits.TrackAt(k).Slot));
                    break;
                }
        hits.Add(new LibraryAlbumHit(album, at, at >= 0 ? q.Length : 0, trackStart, count, reason));
    }

    /// <summary>A name-unmatched artist's "why": a name-matched album first, else the first title-matched track.</summary>
    static MatchReason ArtistReason(LibraryHits hits, ReadOnlySpan<LibraryAlbumHit> albums)
    {
        for (int i = 0; i < albums.Length; i++)
            if (albums[i].MatchLen > 0) return new MatchReason(LibraryMatchKind.Album, LibraryRows.TitleOf(EntityKind.Album, albums[i].Slot));
        for (int i = 0; i < albums.Length; i++)
            foreach (var t in hits.TracksOf(albums[i]))
                if (t.MatchLen > 0) return new MatchReason(LibraryMatchKind.Track, LibraryRows.TitleOf(EntityKind.Track, t.Slot));
        return MatchReason.None;
    }

    /// <summary>Binary-insertion sort: STABLE (equal ranks keep walk order, so a republish never shuffles ties) and
    /// allocation-free with a cached comparison.</summary>
    static void StableSort<T>(Span<T> span, Comparison<T> compare)
    {
        for (int i = 1; i < span.Length; i++)
        {
            T item = span[i];
            int lo = 0, hi = i;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (compare(span[mid], item) <= 0) lo = mid + 1; else hi = mid;
            }
            if (lo == i) continue;
            span[lo..i].CopyTo(span[(lo + 1)..(i + 1)]);
            span[lo] = item;
        }
    }
}
