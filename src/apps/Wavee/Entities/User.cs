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
// §8-§11: the library page's CORE (ch 15 §8) — breakpoint, select-in-place commit, the ONE ordering rule, recency, search,
// and (the 2026-09-17 library rework) §9b's letter groups + album-pane readiness and §11's per-artist library reads —
// which the 2026-09-18 correction widened from "saved albums" to saved albums ∪ the albums your liked songs sit on.
// THE FILE IS OVER ITS STATED BUDGET (1,388 lines against 1,040) and was already over it before the rework: the library
// page's CORE — §8 through §11 — is the natural split into a `User.Library.cs` when somebody has a reason to touch it.
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
    /// master selection, depth 1. Album in the artists view → the album pick WITH its owning artist, collapsed depth
    /// <b>1</b> (was 2): the artists page is TWO rungs now, not three — the reader is the second one, and it reads the
    /// album key once as its initial spine target. The filter is always cleared (the search view is gated on a
    /// non-empty query).</summary>
    public static LibrarySelectionCommit For(LibrarySelectKind kind, bool artistsView, bool collapsed, string uri,
                                             string ownerArtistUri = "")
    {
        if (string.IsNullOrEmpty(uri)) return None;
        if (kind == LibrarySelectKind.Artist)
            return new("artist:" + uri, "", ClearFilter: true, collapsed ? 1 : null);
        if (!artistsView)
            return new("album:" + uri, null, ClearFilter: true, collapsed ? 1 : null);
        return new(string.IsNullOrEmpty(ownerArtistUri) ? null : "artist:" + ownerArtistUri,
                   "album:" + uri, ClearFilter: true, collapsed ? 1 : null);
    }

    public static LibrarySelectionCommit ForArtist(bool artistsView, bool collapsed, string uri)
        => For(LibrarySelectKind.Artist, artistsView, collapsed, uri);

    public static LibrarySelectionCommit ForAlbum(bool artistsView, bool collapsed, string uri, string ownerArtistUri)
        => For(LibrarySelectKind.Album, artistsView, collapsed, uri, ownerArtistUri);
}

// ── 9. the ONE library ordering rule (ch 15 §8, LibraryNavOrder verbatim over an allocation-free row view) ─────────

/// <summary>The sort keys the library pickers offer. The int codes are PERSISTED (<c>library.&lt;kind&gt;.sort</c>) and
/// shared with the sidebar's Library V3 (codes 0-3) — never renumber, only APPEND: a user's persisted 3 has to keep
/// meaning "artist" across every release. <see cref="Albums"/> is the rework's new word (artists by how many releases
/// of theirs are in your library, most first); WHICH kind offers which code is <see cref="LibraryWordRail"/>, not this
/// enum.</summary>
public enum LibraryNavSort : byte { Recents = 0, RecentlyAdded = 1, Alphabetical = 2, Creator = 3, ReleaseDate = 4, Albums = 5 }

/// <summary>Which words a kind's rail shows, in RAIL ORDER, as persisted codes — ONE pure table, so the rail, the page
/// and the tests read the same source instead of three lists drifting apart. The rail is the word pivot that replaced the
/// sort pill: there is no "sort by" menu any more, so a code a kind does not offer has to be CLAMPED at the read rather
/// than merely left unlabelled (<see cref="Clamp"/>).</summary>
public static class LibraryWordRail
{
    // `static readonly` arrays rather than collection expressions in the property: a `ReadOnlySpan<T>` over one is a
    // field load, and the rail re-reads its words on every render of the column header.
    static readonly LibraryNavSort[] s_albums = [LibraryNavSort.Recents, LibraryNavSort.Alphabetical, LibraryNavSort.Creator, LibraryNavSort.RecentlyAdded, LibraryNavSort.ReleaseDate];
    static readonly LibraryNavSort[] s_artists = [LibraryNavSort.Recents, LibraryNavSort.Alphabetical, LibraryNavSort.Albums];
    static readonly LibraryNavSort[] s_shows = [LibraryNavSort.Recents, LibraryNavSort.Alphabetical, LibraryNavSort.RecentlyAdded];

    /// <summary>The kind's words. Albums (and any unnamed kind) get the five-word rail; an artists rail has no "artist"
    /// word (it would sort artists by themselves) and no "year" (an artist has no release date) but does have "albums";
    /// a shows rail has neither.</summary>
    public static ReadOnlySpan<LibraryNavSort> WordsFor(EntityKind kind) => kind switch
    {
        EntityKind.Artist => s_artists,
        EntityKind.Show => s_shows,
        _ => s_albums,
    };

    /// <summary>A persisted code this kind's rail does not offer — a value an older build wrote, or 5 read on the albums
    /// page — reads as <see cref="LibraryNavSort.Recents"/>. The persisted value is NOT rewritten: switching kinds must
    /// not destroy the other kind's choice, so the clamp lives at the read and nowhere else.</summary>
    public static LibraryNavSort Clamp(EntityKind kind, int code)
    {
        var words = WordsFor(kind);
        for (int i = 0; i < words.Length; i++) if ((int)words[i] == code) return words[i];
        return LibraryNavSort.Recents;
    }

    /// <summary>The rail word's loc KEY per code. The rail has its OWN keys (<c>library.rail.*</c>, the lowercase words)
    /// rather than the pill's Title-Case <c>library.sort.*</c> labels, because the sidebar's Library V3 shares those for
    /// codes 0-3 and its pills would have gone lowercase with them (plan §5.9's decision).</summary>
    public static string WordKey(LibraryNavSort sort) => sort switch
    {
        LibraryNavSort.RecentlyAdded => Strings.Library.Rail.RecentlyAdded,   // "added"
        LibraryNavSort.Alphabetical => Strings.Library.Rail.Alphabetical,     // "a-z"
        LibraryNavSort.Creator => Strings.Library.Rail.Creator,               // "artist"
        LibraryNavSort.ReleaseDate => Strings.Library.Rail.ReleaseDate,       // "year"
        LibraryNavSort.Albums => Strings.Library.Rail.Albums,                 // "albums"
        _ => Strings.Library.Rail.Recents,                                    // "recents"
    };
}

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
    /// <summary>What the row COUNTS for <see cref="LibraryNavSort.Albums"/>: an artist row's library RELEASE count
    /// (<see cref="User.LibraryReleaseCountOf"/> — saved albums plus the liked-only ones, §11), 0 for every other kind
    /// (which is why only the artists rail offers that word). Named <c>CountOf</c> and not the plan's
    /// <c>Count(int)</c> because one type cannot carry a property and a method of the same name.</summary>
    int CountOf(int row);
}

/// <summary><see cref="ILibraryNavRows"/> over the record fixtures.</summary>
public readonly struct LibraryFactsRows(LibraryNavFacts[] rows, long[]? played, int[]? counts = null) : ILibraryNavRows
{
    public int Count => rows.Length;
    public long PlayedAt(int row) => played is null ? 0 : played[row];
    public int Year(int row) => rows[row].Year;
    public string Title(int row) => rows[row].Title;
    public string Subtitle(int row) => rows[row].Subtitle;
    public ReadOnlySpan<char> Uri(int row, Span<char> scratch) => rows[row].Uri;
    public string Cover(int row) => rows[row].CoverUrl ?? "";
    /// <summary>The fixture's counts, parallel to the rows; null counts 0 everywhere, which is every fixture that is not
    /// exercising the <see cref="LibraryNavSort.Albums"/> arm.</summary>
    public int CountOf(int row) => counts is null ? 0 : counts[row];
}

/// <summary>A reusable sorter with cached comparison delegates: ordering allocates NOTHING after construction (ch 15 §9).</summary>
public sealed class LibraryNavSorter<TRows> where TRows : ILibraryNavRows
{
    static readonly StringComparer Name = StringComparer.OrdinalIgnoreCase;
    TRows _rows = default!;
    int _sign = 1;
    readonly Comparison<int> _recents, _added, _alphabetical, _creator, _release, _albums;

    public LibraryNavSorter()
    {
        _recents = Recents; _added = Added; _alphabetical = Alphabetical; _creator = Creator; _release = Release;
        _albums = Albums;
    }

    /// <summary>Write the permutation into <paramref name="into"/> (≥ Count). Recents: played newest-first, then never-played
    /// in source order (the block split survives desc). RecentlyAdded: source order. Alphabetical / Creator: title /
    /// subtitle, then title, then uri. ReleaseDate: year desc, unknown years sink. Albums: saved-album count desc, then
    /// title. Desc reverses the tie-breaks too.</summary>
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
            LibraryNavSort.Albums => _albums,
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
    /// <summary>The LETTER first, then the title. The letter is <see cref="LibraryLetters.Of"/> — the same function the
    /// a–z grouping bands by — because a plain ordinal title order does NOT agree with it: "The Beatles" files under B,
    /// a leading quote is skipped, and an accented/CJK/digit initial is '#' while ordinal sorts it after Z. Ordering by
    /// title alone handed <see cref="LibraryLetters.Build"/> rows whose letter changed back and forth, which opened a
    /// band per flip (the same letter many times over) and, past 27 extra headers, overran its buffers — the
    /// 2026-09-18 IndexOutOfRange in <c>ComputeShape</c> once the artists list grew to hundreds of rows.</summary>
    int Alphabetical(int a, int b)
    {
        int c = LibraryLetters.Of(_rows.Title(a)).CompareTo(LibraryLetters.Of(_rows.Title(b)));
        return _sign * (c != 0 ? c : ByTitle(a, b));
    }

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

    /// <summary>Most releases in your library first (<see cref="ILibraryNavRows.CountOf"/>), then title. No block split
    /// for zero (unlike an unknown year): an artist with nothing of theirs saved or liked is still an artist — a row the
    /// followed set put there — and title order below the counted rows reads as one list. Desc flips it whole.</summary>
    int Albums(int a, int b)
    {
        int c = _rows.CountOf(b).CompareTo(_rows.CountOf(a));
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
public readonly struct LibraryRows(EntityKind kind, int[] slots, int count, long[]? played = null, int[]? counts = null) : ILibraryNavRows
{
    readonly EntityKind _kind = kind;
    readonly int[] _slots = slots;
    readonly long[]? _played = played;
    readonly int[]? _counts = counts;
    readonly int _count = count;

    public int Count => _count;
    public int SlotAt(int row) => _slots[row];
    public long PlayedAt(int row) => _played is null ? 0 : _played[row];
    public int Year(int row) => YearOf(_kind, _slots[row]);
    public string Title(int row) => TitleOf(_kind, _slots[row]);
    public string Subtitle(int row) => SubtitleOf(_kind, _slots[row]);
    public ReadOnlySpan<char> Uri(int row, Span<char> scratch) => UriOf(IdOf(_kind, _slots[row]), scratch);
    public string Cover(int row) => Entities.Strings.Resolve(ImageOf(_kind, _slots[row]));

    /// <summary>The library RELEASE count behind <see cref="LibraryNavSort.Albums"/>. PRECOMPUTED when the caller passed
    /// a counts buffer (<see cref="FillCounts"/>), which the page does: a comparator that walked the saved-albums and
    /// liked relations per comparison would be O(n log n · (saved · billed + liked)) on one click. The live read is the
    /// one-off path.</summary>
    public int CountOf(int row)
        => _counts is not null ? _counts[row]
         : _kind == EntityKind.Artist ? User.LibraryAlbumCountOf(_slots[row]) : 0;

    /// <summary>Fill <paramref name="into"/> with each slot's library release count — the Albums sort's precomputed input
    /// and the twin of <see cref="FillPlayed"/>. Artists only; every other kind counts 0 and never offers the word.
    /// <para>ONE walk of the library for the WHOLE list (<see cref="User.FillReleaseCounts"/>), not one read per row.
    /// The count reads the liked relation now (§11), and a per-row read over thousands of liked tracks times the
    /// hundreds of rows the widened navigator carries is a stall on every reorder — the same reason the buffer exists at
    /// all. It answers exactly what <see cref="User.LibraryReleaseCountOf"/> answers per slot.</para></summary>
    public static void FillCounts(EntityKind kind, ReadOnlySpan<int> slots, Span<int> into)
    {
        if (kind != EntityKind.Artist) { into[..slots.Length].Clear(); return; }
        User.FillReleaseCounts(slots, into);
    }

    /// <summary>Does <paramref name="table"/> hold <paramref name="slot"/> — a row it has handed out, slot 0 (the blank
    /// "none" row) included? THE guard every column read below goes through, and the reason none of them can take the
    /// app loop down on a slot that is not a row of that table. The navigator's slots are binds over ONE shared item
    /// source (<c>BoundItems.Project</c> over the shape memo), and a mounted list keeps re-resolving its items from it
    /// for the one flush in which the a–z projection flips underneath it — a Recents→a–z tap, a grid→list toggle under
    /// a–z, or the letter grouping moving as rows land — BEFORE the keyed remount replaces it. In that flush a ROW slot
    /// built without letters is handed the letter HEADER item, <c>Slot = -(letter + 1)</c>, and its art bind read
    /// <c>Image[-1]</c>: the 2026-09-19 IndexOutOfRange that killed the artists navigator on every a–z tap. A slot the
    /// table does not hold answers the blank row, exactly as <see cref="Table.IsFailed(int, uint)"/> answers false for a
    /// stale one — for one frame the row reads as empty, and then the remount is there.</summary>
    static bool Holds(Table table, int slot) => (uint)slot < (uint)table.Count;

    public static EntityId IdOf(EntityKind kind, int slot)
    {
        var scope = Entities.Current;
        return kind switch
        {
            EntityKind.Album when Holds(scope.Albums, slot) => scope.Albums.Id[slot],
            EntityKind.Artist when Holds(scope.Artists, slot) => scope.Artists.Id[slot],
            EntityKind.Show when Holds(scope.Shows, slot) => scope.Shows.Id[slot],
            EntityKind.Track when Holds(scope.Tracks, slot) => scope.Tracks.Id[slot],
            _ => default,
        };
    }

    public static string TitleOf(EntityKind kind, int slot)
    {
        var scope = Entities.Current;
        return Entities.Strings.Resolve(kind switch
        {
            EntityKind.Album when Holds(scope.Albums, slot) => scope.Albums.Title[slot],
            EntityKind.Artist when Holds(scope.Artists, slot) => scope.Artists.Name[slot],
            EntityKind.Show when Holds(scope.Shows, slot) => scope.Shows.Title[slot],
            EntityKind.Track when Holds(scope.Tracks, slot) => scope.Tracks.Title[slot],
            _ => StringId.Empty,
        });
    }

    /// <summary>Album → its first billed artist's name; show → the publisher; anything else → "".</summary>
    public static string SubtitleOf(EntityKind kind, int slot)
    {
        var scope = Entities.Current;
        if (kind == EntityKind.Show) return Holds(scope.Shows, slot) ? Entities.Strings.Resolve(scope.Shows.Publisher[slot]) : "";
        if (kind != EntityKind.Album) return "";
        var artists = scope.Edges.AlbumArtists.Targets(slot);
        return artists.Length > 0 && artists[0] > Table.None && Holds(scope.Artists, artists[0])
            ? Entities.Strings.Resolve(scope.Artists.Name[artists[0]]) : "";
    }

    public static StringId ImageOf(EntityKind kind, int slot)
    {
        var scope = Entities.Current;
        return kind switch
        {
            EntityKind.Album when Holds(scope.Albums, slot) => scope.Albums.Image[slot],
            EntityKind.Artist when Holds(scope.Artists, slot) => scope.Artists.Image[slot],
            EntityKind.Show when Holds(scope.Shows, slot) => scope.Shows.Image[slot],
            EntityKind.Track when Holds(scope.Tracks, slot) => scope.Tracks.Image[slot],
            _ => StringId.Empty,
        };
    }

    public static int YearOf(EntityKind kind, int slot)
    {
        var albums = Entities.Current.Albums;
        return kind == EntityKind.Album && Holds(albums, slot) && albums.Knows(slot, (uint)AlbumFields.Year) ? albums.Year[slot] : 0;
    }

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

// ── 9b. the letter groups and the album pane's readiness (the rework's derived facts; plan §5.1) ──────────────────

/// <summary>Letter groups over an ALPHABETICALLY sorted navigator: a FLAT index space that interleaves up to 27 header
/// items ("#", A-Z) with the rows. A header is a flat item whose <see cref="RowOf"/> is -1; every other flat item is a
/// row. Everything here is a pure function of the sorted titles — the page keys its list on <see cref="Key"/>, the
/// sticky overlay reads <see cref="StickyLetterAt"/>, the jump strip reads <see cref="Present"/> and
/// <see cref="HeaderFlat"/> — so no surface has to probe the rows to know where a band starts (derived facts live on the
/// model). Reused across computes: the arrays grow and never shrink, so a rebuild per filter keystroke allocates nothing
/// once warm. The page owns ONE instance. UI thread (C1).</summary>
public sealed class LibraryLetters
{
    /// <summary>'#' (0) plus A-Z (1..26). 27 fits a <c>uint</c> bitmask, which is what <see cref="Present"/> is.</summary>
    public const int Count = 27;
    /// <summary>The letter header's main-axis extent — the ONE number the offsets, the extents and the sticky overlay
    /// share, so a header can never be laid out at one height and scrolled at another.</summary>
    public const float HeaderExtent = 28f;

    int[] _flatToRow = new int[64];         // flat index -> row index, or -1 for a header
    byte[] _flatLetter = new byte[64];      // flat index -> the header's letter / the row's group
    float[] _offset = new float[65];        // flat index -> main-axis offset (prefix sum); [FlatCount] = the total
    int[] _seq = new int[64];               // 0..FlatCount-1 — the flat-index space JumpIndex.Project walks
    /// <summary>letter -> its header's flat index (A2 plan §3.4: the same <see cref="JumpIndex"/> kernel the show
    /// reader's date rail projects into) — rebuilt by <see cref="Build"/>, resolved by <see cref="HeaderFlat"/>.</summary>
    readonly JumpGroup[] _groups = new JumpGroup[Count];
    int _groupCount;
    int _flatCount, _rows;
    uint _present;                          // bit i = letter i has at least one row
    // Cached ONCE (the page owns ONE instance): a closure allocated per Build() call would undo the "allocation-free
    // once warm" the rest of this class holds itself to.
    readonly Func<int, bool> _isHeaderAt;
    readonly Func<int, int> _letterAt;

    public LibraryLetters()
    {
        _isHeaderAt = flat => _flatToRow[flat] < 0;
        _letterAt = flat => _flatLetter[flat];
    }

    public int FlatCount => _flatCount;
    /// <summary>How many ROWS the last build covered (<see cref="FlatCount"/> minus its headers).</summary>
    public int Rows => _rows;
    public bool IsHeader(int flat) => (uint)flat < (uint)_flatCount && _flatToRow[flat] < 0;
    public int RowOf(int flat) => (uint)flat < (uint)_flatCount ? _flatToRow[flat] : -1;
    public int LetterOf(int flat) => (uint)flat < (uint)_flatCount ? _flatLetter[flat] : -1;
    public bool Has(int letter) => (uint)letter < Count && (_present & (1u << letter)) != 0;
    public int HeaderFlat(int letter) => JumpIndex.Resolve(_groups.AsSpan(0, _groupCount), letter);
    public float OffsetOf(int flat) => _offset[Math.Clamp(flat, 0, _flatCount)];
    public float TotalExtent => _offset[_flatCount];
    /// <summary>"Which letters exist" as ONE value: bit i = letter i has rows. The jump strip renders its 27 cells from
    /// it without a second pass over the rows, and a test pins a whole grouping in one comparison.</summary>
    public uint Present => _present;

    /// <summary>The letter of a title: leading punctuation, brackets and quotes are skipped, then a leading "the " (the
    /// article files under the real word — "the Beatles" is B; a bare "The" is a title and files under T). A first
    /// character outside A-Z — a digit, CJK, an accented letter, an empty title — is '#' (index 0), because
    /// <c>OrdinalIgnoreCase</c>, which the alphabetical comparator uses, folds A-Z and nothing else.</summary>
    public static int Of(ReadOnlySpan<char> title)
    {
        int i = 0;
        while (i < title.Length && !char.IsLetterOrDigit(title[i])) i++;
        var rest = title[i..];
        if (rest.Length > 4 && rest[3] == ' ' && rest[..3].Equals("the", StringComparison.OrdinalIgnoreCase)) rest = rest[4..];
        if (rest.Length == 0) return 0;
        char c = char.ToUpperInvariant(rest[0]);
        return c is >= 'A' and <= 'Z' ? c - 'A' + 1 : 0;
    }

    /// <summary>Rebuild for <paramref name="rows"/>, which must already be in ALPHABETICAL order: the grouping is one
    /// pass that opens a band when the letter changes, so an unsorted list would open a letter twice and the later header
    /// would win <see cref="HeaderFlat"/>. O(n), allocation-free once warm. Generic over the row view (the plan wrote
    /// <c>in LibraryRows</c>) so the rule is testable without a live scope, exactly as <c>LibraryNavOrder.OrderKey</c>
    /// is.</summary>
    public void Build<TRows>(in TRows rows, float rowExtent) where TRows : ILibraryNavRows
    {
        _rows = rows.Count;
        // WORST CASE, not the sorted case: a band opens on every letter CHANGE, so rows that are not grouped by letter
        // can open up to one header per row. The sorter orders by this same letter (LibraryNavSorter.Alphabetical), so
        // the sorted case is `rows + 27` — but sizing for it let a disagreement between the two overrun the buffers and
        // take the app loop down. A grouping helper must never be able to crash on its input.
        Grow(_rows * 2 + 1);
        _present = 0; _flatCount = 0;
        int last = -1; float off = 0f;
        for (int r = 0; r < _rows; r++)
        {
            int letter = Of(rows.Title(r));
            if (letter != last)
            {
                _present |= 1u << letter;
                _flatToRow[_flatCount] = -1; _flatLetter[_flatCount] = (byte)letter; _offset[_flatCount] = off;
                _flatCount++; off += HeaderExtent; last = letter;
            }
            _flatToRow[_flatCount] = r; _flatLetter[_flatCount] = (byte)letter; _offset[_flatCount] = off;
            _flatCount++; off += rowExtent;
        }
        _offset[_flatCount] = off;
        for (int i = 0; i < _flatCount; i++) _seq[i] = i;
        _groupCount = JumpIndex.Project(_seq.AsSpan(0, _flatCount), _isHeaderAt, _letterAt, _groups);
    }

    /// <summary>A flat item's extent: the header band, or a row. The analytic seed for the list's extents.</summary>
    public float ExtentOf(int flat, float rowExtent) => IsHeader(flat) ? HeaderExtent : rowExtent;

    /// <summary>The letter whose band contains <paramref name="offset"/> — the sticky overlay's one input. Binary search
    /// over the prefix sums; -1 above the first header, and for an empty build.</summary>
    public int StickyLetterAt(float offset)
    {
        int lo = 0, hi = _flatCount - 1, hit = -1;
        while (lo <= hi) { int mid = (lo + hi) >> 1; if (_offset[mid] <= offset) { hit = mid; lo = mid + 1; } else hi = mid - 1; }
        return hit < 0 ? -1 : _flatLetter[hit];
    }

    /// <summary>A stable identity of the GROUPING (letter -> header flat index), folded FNV-1a: the navigator's remount
    /// key part. Two builds of the same grouping agree; a different grouping does not. Selection is not in it.</summary>
    public ulong Key()
    {
        ulong h = 14695981039346656037UL;
        for (int l = 0; l < Count; l++) { h ^= (uint)(HeaderFlat(l) + 1); h *= 1099511628211UL; }
        return h;
    }

    // Grow discards the old contents on purpose: its only caller is Build, which rewrites every cell it will read.
    void Grow(int n)
    {
        if (_flatToRow.Length >= n) return;
        int size = Math.Max(n, _flatToRow.Length * 2);
        _flatToRow = new int[size]; _flatLetter = new byte[size]; _offset = new float[size + 1]; _seq = new int[size];
    }
}

/// <summary>The album pane's readiness: THREE not-yet states and Ready, where ch 15 W24 had two. The third is the whole
/// point — a pane that cannot tell "still coming" from "the ask failed" paints a skeleton forever, which is 0.2.10's
/// defect A.</summary>
public enum AlbumPaneState : byte { Header, Rows, Failed, Ready }

/// <summary>The two ROW-level facts the readiness rule takes, as one value so the richer
/// <see cref="AlbumPaneReadiness.Of(bool,EdgeState,bool,AlbumRowFacts)"/> is a real overload of the five-argument one
/// (five bools in the same positions could not be). They travel together because they are asked of the same rows in the
/// same walk.</summary>
/// <param name="AnyUntitled">A listed row whose own TITLE has not landed — the only row fact that still gates: a
/// tracklist with a blank line in it is loading, not ready.</param>
/// <param name="AnyFailed">A listed row whose <c>TrackFields.Row</c> batch is <c>Table.Failed</c>.</param>
public readonly record struct AlbumRowFacts(bool AnyUntitled, bool AnyFailed);

/// <summary>The pane's readiness rule: pure over the album's known bits and its tracks edge, so the pane never paints a
/// skeleton it cannot leave and the whole state table is a test rather than a per-render probe.</summary>
public static class AlbumPaneReadiness
{
    /// <summary>The rule, over the album's identity bit, its tracks edge, and the two row facts:
    /// <list type="bullet">
    /// <item><b>Header</b> — the row's identity has not answered (the navigator's own demand; short-lived).</item>
    /// <item><b>Rows</b> — identity known and the tracks edge Unknown/Partial, or Complete with a row still UNTITLED and
    /// nothing failed: the counted shimmer.</item>
    /// <item><b>Failed</b> — the tracks edge failed, OR the edge is Complete, a row is untitled and that row batch failed
    /// (<c>Table.IsFailed</c>): the Retry strip, never a notice about a "minified" album.</item>
    /// <item><b>Ready</b> — identity plus a Complete edge whose every row knows its own title.</item>
    /// </list>
    /// An edge failure outranks a missing identity's shimmer on purpose: a failed list is answerable (Retry), and the
    /// identity the navigator is still fetching is not this pane's to wait on forever.
    /// <para>A row whose CREDITED ARTIST has no name is NOT a gate (the 2026-09-18 correction). Nobody demands
    /// <c>ArtistFields.Name</c> for a track's credits, so that bit can simply never land — and the pane, which had it
    /// folded into one "unnamed" flag with the missing titles, then shimmered forever: 0.2.10's defect A wearing a
    /// second hat. The unnamed credit is a notice (<c>Detail.NoticeRules</c>, <c>DetailNotice.MinifiedAlbum</c>), and a
    /// notice is shown OVER a rendered list, not instead of one.</para></summary>
    public static AlbumPaneState Of(bool knowsIdentity, EdgeState tracks, bool edgeFailed, AlbumRowFacts rows)
    {
        if (!knowsIdentity) return AlbumPaneState.Header;
        if (edgeFailed) return AlbumPaneState.Failed;
        if (tracks != EdgeState.Complete) return AlbumPaneState.Rows;
        if (!rows.AnyUntitled) return AlbumPaneState.Ready;
        return rows.AnyFailed ? AlbumPaneState.Failed : AlbumPaneState.Rows;
    }

    /// <summary>The five-argument shape the pane and the reader call today, kept so their call sites (named argument
    /// <c>anyUnnamed:</c> included) keep compiling — with the corrected semantics: <paramref name="anyUnnamed"/> is
    /// ACCEPTED AND IGNORED, because it mixes "this row has no title" with "a credited artist of this row has no name"
    /// and only the first of those may hold a pane in its skeleton (see the overload's remarks). A caller that can tell
    /// the two apart passes <see cref="AlbumRowFacts"/> instead and gets the untitled gate back.</summary>
    public static AlbumPaneState Of(bool knowsIdentity, EdgeState tracks, bool edgeFailed, bool anyUnnamed, bool rowsFailed)
        => Of(knowsIdentity, tracks, edgeFailed, new AlbumRowFacts(AnyUntitled: false, AnyFailed: rowsFailed));

    /// <summary>The COUNTED shimmer: the album's own track count when it knows it, the listed edge length when it does
    /// not, 6 when neither has answered — never 0, because a zero-row shimmer is a blank pane, and a blank pane reads as
    /// an empty album rather than as loading.</summary>
    public static int ShimmerRows(bool knowsCount, int trackCount, int listed)
        => knowsCount && trackCount > 0 ? trackCount : listed > 0 ? listed : 6;
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
                if (saved[i] > Table.None && hits.Seen.Add(saved[i])) SearchAlbum(hits, saved[i], e.AlbumTracks.Targets(saved[i]), parentMatched: false, q);
            StableSort(hits.AlbumRun(0, hits.AlbumCount), s_albumRank);
            return hits;
        }

        // THE LIBRARY, not the catalogue. This used to walk the FOLLOWED artists and their three discography facets —
        // two wrong sets at once: the navigator lists followed ∪ saved-album ∪ liked-song artists (§11), so an artist
        // you only have likes from could be on screen and unfindable; and the facets are paged catalogue edges that are
        // resident only for artists whose reader has been opened (kind 8 no longer seeds them), so even a followed
        // artist usually searched an empty set. "Search your library" means: every library artist, over what the
        // library HOLDS of them — the saved albums' tracklists and the liked songs (§11's LibraryReleasesOf).
        int artistTotal = LibraryArtistsOf(t_searchArtists ??= new int[512]);
        if (artistTotal > t_searchArtists.Length)
        {
            t_searchArtists = new int[Math.Max(artistTotal, t_searchArtists.Length * 2)];
            artistTotal = LibraryArtistsOf(t_searchArtists);
        }
        int[] artists = t_searchArtists;
        Span<int> releases = stackalloc int[128];
        Span<bool> likedOnly = stackalloc bool[128];
        Span<int> likedRows = stackalloc int[LibraryHits.TracksPerAlbumCap];
        for (int i = 0; i < artistTotal && i < artists.Length; i++)
        {
            int artist = artists[i];
            if (artist <= Table.None) continue;
            int at = LibraryRows.TitleOf(EntityKind.Artist, artist).AsSpan().IndexOf(q, StringComparison.OrdinalIgnoreCase);
            int albumStart = hits.AlbumCount, trackMark = hits.TrackCount;
            int relCount = Math.Min(LibraryReleasesOf(artist, releases, likedOnly), releases.Length);
            for (int r = 0; r < relCount; r++)
            {
                int album = releases[r];
                if (album <= Table.None) continue;
                // A saved album searches its whole tracklist when that list is resident; a liked-only release — and a
                // saved one whose list has not been fetched yet — searches the liked songs the library holds on it.
                var listed = likedOnly[r] ? default : e.AlbumTracks.Targets(album);
                if (listed.Length > 0) { SearchAlbum(hits, album, listed, at >= 0, q); continue; }
                int n = Math.Min(LikedTracksOfAlbum(artist, album, likedRows), likedRows.Length);
                SearchAlbum(hits, album, likedRows[..n], at >= 0, q);
            }
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

    [ThreadStatic] static int[]? t_searchArtists;

    static void SearchAlbum(LibraryHits hits, int album, ReadOnlySpan<int> tracks, bool parentMatched, ReadOnlySpan<char> q)
    {
        int at = LibraryRows.TitleOf(EntityKind.Album, album).AsSpan().IndexOf(q, StringComparison.OrdinalIgnoreCase);
        bool albumMatched = at >= 0 || parentMatched;
        int trackStart = hits.TrackCount;
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

// ── 11. "in your library" for an artist (the reader's releases, the navigator's rows and its artist set) ────────────
//
// WHAT "IN YOUR LIBRARY" MEANS (the 2026-09-18 correction). Until now it was `LibraryAlbumsOf` alone — saved albums
// billed to the artist — and liked songs counted for NOTHING: an account that hearts tracks and saves no albums had an
// empty Artists tab, and an artist whose only presence is one liked feature showed "Artist" instead of a release. The
// rule is now TWO groups, in this order:
//
//   1. SAVED albums billed to the artist   → the block lists the album's WHOLE tracklist (it is the album you saved)
//   2. albums holding ≥ 1 LIKED track credited to the artist, minus group 1
//                                          → the block lists ONLY those liked tracks (you did not save the album)
//
// and the Artists navigator itself is followed ∪ (1) ∪ (2)'s artists (`LibraryArtistsOf`).
//
// WHO DEMANDS THE RELATIONS. `Edges.SavedAlbums` / `Edges.Liked` are the account's own relations, demanded once per
// scope by the library page (`User.Page.Library.cs`) and the sidebar. The two IDENTITY relations these reads filter
// through are NOT persisted and are NOT demanded here — this file only ever READS, so that a navigator row may call it
// while it renders:
//   · `Edges.AlbumArtists` lands with `AlbumFields.Identity`, demanded by the library page's rows and by the reader;
//   · `Edges.TrackArtists` lands with `TrackFields.Identity`, demanded by the page's Artists arm (it has to, or group 2
//     is invisible) and by the reader's blocks.
// A relation that has not answered simply contributes nothing — every number here is a fact about what is KNOWN, never
// a probe that asks for more.

public readonly partial struct User
{
    /// <summary>The dedup scratch for <see cref="LibraryReleasesOf"/> and <see cref="LibraryArtistsOf"/>: ONE reusable
    /// set per thread, cleared at acquire. A linear rescan of the output span cannot do the job — both helpers answer a
    /// TOTAL through an EMPTY span (the count-only call), and the liked relation can carry thousands of tracks, which
    /// would make the rescan O(n · artists). These run on edges (a library publish, a selection change), not per frame,
    /// so one warm set that never shrinks is the whole cost. <c>[ThreadStatic]</c> rather than a plain static because
    /// the test host runs unrelated classes in parallel; the UI only ever has one thread here (C1). Nothing below nests
    /// two scratch uses — that is the invariant that lets them share one set.</summary>
    [ThreadStatic] static HashSet<int>? t_seen;

    static HashSet<int> Scratch()
    {
        var seen = t_seen ??= new HashSet<int>(64);
        seen.Clear();                                     // a slot from a dead scope is a wrong answer, not a stale one
        return seen;
    }

    /// <summary>The saved albums billed to <paramref name="artistSlot"/>: <c>Edges.SavedAlbums</c> (parent = me) filtered
    /// through <c>Edges.AlbumArtists</c>. The saved-only PRIMITIVE — group 1 of the rule in this section's header; what a
    /// surface wants is almost always <see cref="LibraryReleasesOf"/>, which adds the liked-only albums. Asks for
    /// NOTHING (see the header on who demands the two relations), O(saved · billed), allocation-free. Returns the TOTAL,
    /// which may exceed <paramref name="into"/>: the caller either sizes a buffer and reads again (the reader) or only
    /// wanted the number (an empty span).</summary>
    public static int LibraryAlbumsOf(int artistSlot, Span<int> into)
    {
        Scope? scope = Entities.Current;
        if (scope is null || scope.MeSlot <= Table.None || artistSlot <= Table.None) return 0;
        var saved = scope.Edges.SavedAlbums.Targets(scope.MeSlot);
        int n = 0;
        for (int i = 0; i < saved.Length; i++)
        {
            var billed = scope.Edges.AlbumArtists.Targets(saved[i]);
            for (int j = 0; j < billed.Length; j++)
                if (billed[j] == artistSlot) { if (n < into.Length) into[n] = saved[i]; n++; break; }
        }
        return n;
    }

    /// <summary>THE "in your library" release list for an artist: the saved albums billed to it FIRST, in
    /// <see cref="LibraryAlbumsOf"/> order, then — deduplicated against them and against each other, in LIKED-EDGE order
    /// (newest liked first) — the albums holding at least one liked track credited to the artist.
    /// <paramref name="likedOnly"/>[i] is <c>false</c> for the first group (the block lists the album's whole tracklist)
    /// and <c>true</c> for the second (the block lists only <see cref="LikedTracksOfAlbum"/>).
    /// <para>A saved album that ALSO holds liked tracks of the artist stays in group 1 and appears once: you saved it, so
    /// you get all of it. A saved album that is NOT billed to the artist but carries one of its liked tracks (a
    /// compilation, a soundtrack) is group 2 — the artist's presence there is the track, not the record.</para>
    /// Returns the TOTAL, which may exceed the spans (same contract as <see cref="LibraryAlbumsOf"/>); both spans may be
    /// empty, which is the counting call. Allocation-free bar the shared <see cref="Scratch"/> set.</summary>
    public static int LibraryReleasesOf(int artistSlot, Span<int> albums, Span<bool> likedOnly)
    {
        Scope? scope = Entities.Current;
        if (scope is null || scope.MeSlot <= Table.None || artistSlot <= Table.None) return 0;

        int n = LibraryAlbumsOf(artistSlot, albums);                       // group 1 — may exceed `albums`
        for (int i = 0; i < n && i < likedOnly.Length; i++) likedOnly[i] = false;

        var e = scope.Edges;
        var liked = e.Liked.Targets(scope.MeSlot);
        if (liked.Length == 0) return n;

        var seen = Scratch();                                              // group 2's own dedup (two liked tracks, one album)
        for (int i = 0; i < liked.Length; i++)
        {
            int track = liked[i];
            if (track <= Table.None) continue;
            int album = scope.Tracks.Album[track];
            if (album <= Table.None) continue;                             // a liked row with no album is a song, not a release
            if (!CreditedTo(e, track, artistSlot)) continue;
            if (IsSavedAndBilled(scope, album, artistSlot)) continue;      // already group 1, wherever it sits in that order
            if (!seen.Add(album)) continue;
            if (n < albums.Length) albums[n] = album;
            if (n < likedOnly.Length) likedOnly[n] = true;
            n++;
        }
        return n;
    }

    /// <summary>The liked tracks on <paramref name="albumSlot"/> credited to <paramref name="artistSlot"/>, in
    /// LIKED-EDGE order (newest first) — the rows a group-2 block of <see cref="LibraryReleasesOf"/> paints, which is
    /// why it is that order and not the album's own. Returns the TOTAL; <paramref name="tracks"/> may be short or empty.
    /// O(liked), allocation-free.</summary>
    public static int LikedTracksOfAlbum(int artistSlot, int albumSlot, Span<int> tracks)
    {
        Scope? scope = Entities.Current;
        if (scope is null || scope.MeSlot <= Table.None || artistSlot <= Table.None || albumSlot <= Table.None) return 0;
        var e = scope.Edges;
        var liked = e.Liked.Targets(scope.MeSlot);
        int n = 0;
        for (int i = 0; i < liked.Length; i++)
        {
            int track = liked[i];
            if (track <= Table.None || scope.Tracks.Album[track] != albumSlot) continue;
            if (!CreditedTo(e, track, artistSlot)) continue;
            if (n < tracks.Length) tracks[n] = track;
            n++;
        }
        return n;
    }

    /// <summary>How many releases this artist has in your library — <see cref="LibraryReleasesOf"/> counted through two
    /// empty spans, so counting allocates no buffer.</summary>
    public static int LibraryReleaseCountOf(int artistSlot) => LibraryReleasesOf(artistSlot, default, default);

    /// <summary>The navigator row's "3 albums" and the <see cref="LibraryNavSort.Albums"/> sort key. It is the RELEASE
    /// count (saved + liked-only), not the saved-album count any more, because that is the list the row's "in your
    /// library" now stands for — a row that read one number and opened onto a longer list was the defect. The sort word
    /// still reads as "albums" and still ranks the artists you have most of first, which is what it always meant.</summary>
    public static int LibraryAlbumCountOf(int artistSlot) => LibraryReleaseCountOf(artistSlot);

    /// <summary>The songs line, "· 34 songs": the track counts of the SAVED albums (group 1 — you have the whole record)
    /// plus the individual liked tracks credited to the artist that sit ANYWHERE else (group 2's rows, and the liked
    /// rows whose album has not answered — a song you liked is a song you have, even before its album is a row). A liked
    /// track ON a group-1 album is already inside that album's count and is NOT added twice.
    /// <para>An album whose track count has not answered contributes 0, and a caller whose sum is 0 drops the songs
    /// clause rather than claiming an empty discography: the number is a fact about what is KNOWN. Bounded at 128 saved
    /// albums per artist, because the row must not allocate and a longer sum is not a number anyone reads as exact.</para></summary>
    public static int LibrarySongCountOf(int artistSlot)
    {
        Scope? scope = Entities.Current;
        if (scope is null || scope.MeSlot <= Table.None || artistSlot <= Table.None) return 0;

        Span<int> slots = stackalloc int[128];
        int n = Math.Min(LibraryAlbumsOf(artistSlot, slots), slots.Length);
        int songs = 0;
        for (int i = 0; i < n; i++) { var a = new Album(slots[i]); if (a.Knows(AlbumFields.TrackCount)) songs += a.TrackCount; }

        var e = scope.Edges;
        var liked = e.Liked.Targets(scope.MeSlot);
        for (int i = 0; i < liked.Length; i++)
        {
            int track = liked[i];
            if (track <= Table.None || !CreditedTo(e, track, artistSlot)) continue;
            int album = scope.Tracks.Album[track];
            if (album > Table.None && IsSavedAndBilled(scope, album, artistSlot)) continue;   // counted by its album
            songs++;
        }
        return songs;
    }

    /// <summary>THE artists navigator's set: the followed artists FIRST, in followed-edge order, then everybody else in
    /// FIRST-SEEN order — the artists billed on your saved albums (in saved-edge order, then billing order), then the
    /// artists credited on your liked tracks (in liked-edge order, then credit order). Deduplicated; an invalid slot is
    /// never a row. Returns the TOTAL, which may exceed <paramref name="into"/> (an empty span counts).
    /// <para>Followed-first is not cosmetic: following is a deliberate act and those rows must not sink under a hundred
    /// artists you merely have a track of, whatever the rail's word then does to the order.</para>
    /// O(followed + saved · billed + liked · credited) through the shared <see cref="Scratch"/> set.</summary>
    public static int LibraryArtistsOf(Span<int> into)
    {
        Scope? scope = Entities.Current;
        if (scope is null || scope.MeSlot <= Table.None) return 0;
        var e = scope.Edges;
        int me = scope.MeSlot, n = 0;
        var seen = Scratch();

        var followed = e.FollowedArtists.Targets(me);
        for (int i = 0; i < followed.Length; i++) Take(seen, followed[i], into, ref n);

        var saved = e.SavedAlbums.Targets(me);
        for (int i = 0; i < saved.Length; i++)
        {
            if (saved[i] <= Table.None) continue;
            var billed = e.AlbumArtists.Targets(saved[i]);
            for (int j = 0; j < billed.Length; j++) Take(seen, billed[j], into, ref n);
        }

        var liked = e.Liked.Targets(me);
        for (int i = 0; i < liked.Length; i++)
        {
            if (liked[i] <= Table.None) continue;
            var credited = e.TrackArtists.Targets(liked[i]);
            for (int j = 0; j < credited.Length; j++) Take(seen, credited[j], into, ref n);
        }
        return n;

        // The total counts every distinct artist; the span only takes what fits (the caller resizes and reads again).
        static void Take(HashSet<int> set, int artist, Span<int> dst, ref int written)
        {
            if (artist <= Table.None || !set.Add(artist)) return;
            if (written < dst.Length) dst[written] = artist;
            written++;
        }
    }

    /// <summary>The whole navigator's release counts in ONE walk of the library: <c>O(artists + saved · billed +
    /// liked · credited)</c> where a read per row would be <c>O(artists · (saved · billed + liked))</c> — with a
    /// thousand artist rows and ten thousand liked tracks that difference is the reorder's whole frame budget. Each
    /// <paramref name="into"/>[i] ends as <see cref="LibraryReleaseCountOf"/> of <paramref name="artists"/>[i] exactly;
    /// an invalid or repeated slot answers the same number it would alone. <paramref name="into"/> must be at least as
    /// long as <paramref name="artists"/>.
    /// <para>Two more <c>[ThreadStatic]</c> scratches (<see cref="t_counts"/>, <see cref="t_pairs"/>), cleared per call
    /// and warm thereafter — the same bargain as <see cref="Scratch"/>, and neither is nested inside it.</para></summary>
    public static void FillReleaseCounts(ReadOnlySpan<int> artists, Span<int> into)
    {
        into[..artists.Length].Clear();
        Scope? scope = Entities.Current;
        if (scope is null || scope.MeSlot <= Table.None || artists.Length == 0) return;

        var counts = t_counts ??= new Dictionary<int, int>(64);
        counts.Clear();
        for (int i = 0; i < artists.Length; i++) if (artists[i] > Table.None) counts[artists[i]] = 0;
        if (counts.Count == 0) return;

        var e = scope.Edges;
        int me = scope.MeSlot;

        // Group 1: every saved album adds one to each artist it is BILLED to (once, however often it is billed).
        var saved = e.SavedAlbums.Targets(me);
        for (int i = 0; i < saved.Length; i++)
        {
            if (saved[i] <= Table.None) continue;
            var billed = e.AlbumArtists.Targets(saved[i]);
            for (int j = 0; j < billed.Length; j++)
            {
                if (Repeats(billed, j)) continue;
                if (counts.TryGetValue(billed[j], out int c)) counts[billed[j]] = c + 1;
            }
        }

        // Group 2: every (album, credited artist) pair a liked track introduces, minus the pairs group 1 already holds.
        var liked = e.Liked.Targets(me);
        if (liked.Length == 0) { Write(artists, into, counts); return; }
        var pairs = t_pairs ??= new HashSet<long>(128);
        pairs.Clear();
        for (int i = 0; i < liked.Length; i++)
        {
            int track = liked[i];
            if (track <= Table.None) continue;
            int album = scope.Tracks.Album[track];
            if (album <= Table.None) continue;
            var credited = e.TrackArtists.Targets(track);
            for (int j = 0; j < credited.Length; j++)
            {
                int artist = credited[j];
                if (Repeats(credited, j)) continue;
                if (!counts.TryGetValue(artist, out int c)) continue;       // not a row this list asked about
                if (IsSavedAndBilled(scope, album, artist)) continue;
                if (pairs.Add(((long)album << 32) | (uint)artist)) counts[artist] = c + 1;
            }
        }
        Write(artists, into, counts);

        static void Write(ReadOnlySpan<int> rows, Span<int> dst, Dictionary<int, int> map)
        {
            for (int i = 0; i < rows.Length; i++) dst[i] = map.TryGetValue(rows[i], out int v) ? v : 0;
        }
    }

    /// <summary>The whole navigator's SONG counts in ONE walk — the batch twin of <see cref="LibrarySongCountOf"/>, and
    /// the same bargain as <see cref="FillReleaseCounts"/>: <c>O(artists + saved · billed + liked · credited)</c> where a
    /// read per row is <c>O(artists · (saved · billed + liked))</c>. The navigator's second line is this number for every
    /// row, so it is a per-LIST fact, never a per-row read. Each <paramref name="into"/>[i] is
    /// <see cref="LibrarySongCountOf"/> of <paramref name="artists"/>[i]; <paramref name="into"/> must be at least as
    /// long as <paramref name="artists"/>.
    /// <para>The ONE documented divergence from the per-slot read: that one bounds its saved-album sum at 128 albums per
    /// artist because it stack-allocates their slots, and this one has no buffer to bound — so an artist with more than
    /// 128 saved albums counts them ALL here. Both answer the same number for every row anyone has ever seen, and the
    /// batch is the more honest of the two.</para></summary>
    public static void FillSongCounts(ReadOnlySpan<int> artists, Span<int> into)
    {
        into[..artists.Length].Clear();
        Scope? scope = Entities.Current;
        if (scope is null || scope.MeSlot <= Table.None || artists.Length == 0) return;

        var songs = t_songs ??= new Dictionary<int, int>(64);
        songs.Clear();
        for (int i = 0; i < artists.Length; i++) if (artists[i] > Table.None) songs[artists[i]] = 0;
        if (songs.Count == 0) return;

        var e = scope.Edges;
        int me = scope.MeSlot;

        // Group 1: a SAVED album gives every artist it is billed to its whole track count. An album whose track count
        // has not answered adds nothing — the number is a fact about what is KNOWN, never a guess.
        var saved = e.SavedAlbums.Targets(me);
        for (int i = 0; i < saved.Length; i++)
        {
            if (saved[i] <= Table.None) continue;
            var album = new Album(saved[i]);
            if (!album.Knows(AlbumFields.TrackCount) || album.TrackCount <= 0) continue;
            int tracks = album.TrackCount;
            var billed = e.AlbumArtists.Targets(saved[i]);
            for (int j = 0; j < billed.Length; j++)
            {
                if (Repeats(billed, j)) continue;
                if (songs.TryGetValue(billed[j], out int c)) songs[billed[j]] = c + tracks;
            }
        }

        // Group 2: a liked track counts ONE for each artist credited on it — unless its album is that artist's group 1,
        // where the album's own track count already counted it.
        var liked = e.Liked.Targets(me);
        for (int i = 0; i < liked.Length; i++)
        {
            int track = liked[i];
            if (track <= Table.None) continue;
            int album = scope.Tracks.Album[track];                      // a liked row with no album still counts as a song
            var credited = e.TrackArtists.Targets(track);
            for (int j = 0; j < credited.Length; j++)
            {
                int artist = credited[j];
                if (Repeats(credited, j)) continue;
                if (!songs.TryGetValue(artist, out int c)) continue;     // not a row this list asked about
                if (album > Table.None && IsSavedAndBilled(scope, album, artist)) continue;
                songs[artist] = c + 1;
            }
        }

        for (int i = 0; i < artists.Length; i++) into[i] = songs.TryGetValue(artists[i], out int v) ? v : 0;
    }

    [ThreadStatic] static Dictionary<int, int>? t_counts;
    [ThreadStatic] static Dictionary<int, int>? t_songs;
    [ThreadStatic] static HashSet<long>? t_pairs;

    /// <summary>Has this entry already appeared earlier in the same billing / credit list? Those lists are a handful of
    /// slots, so the scan beats a set — and a repeated credit must not count its album twice (the per-slot reads stop at
    /// the FIRST match, so this is what keeps the two paths saying the same number).</summary>
    static bool Repeats(ReadOnlySpan<int> list, int at)
    {
        if (list[at] <= Table.None) return true;                    // an invalid slot is never a row, never a count
        for (int i = 0; i < at; i++) if (list[i] == list[at]) return true;
        return false;
    }

    /// <summary>Is <paramref name="artistSlot"/> one of the track's credited artists (<c>Edges.TrackArtists</c>)? An
    /// un-hydrated track has no credits and so credits nobody — the honest answer, not a guess.</summary>
    static bool CreditedTo(Edges e, int trackSlot, int artistSlot)
    {
        var credited = e.TrackArtists.Targets(trackSlot);
        for (int i = 0; i < credited.Length; i++) if (credited[i] == artistSlot) return true;
        return false;
    }

    /// <summary>Is this album group 1 for the artist — saved by me AND billed to it? The membership half is the reverse
    /// index's O(1) probe, so this is the billing scan and nothing else.</summary>
    static bool IsSavedAndBilled(Scope scope, int albumSlot, int artistSlot)
    {
        if (!scope.Edges.SavedAlbums.Contains(scope.MeSlot, albumSlot)) return false;
        var billed = scope.Edges.AlbumArtists.Targets(albumSlot);
        for (int i = 0; i < billed.Length; i++) if (billed[i] == artistSlot) return true;
        return false;
    }
}
