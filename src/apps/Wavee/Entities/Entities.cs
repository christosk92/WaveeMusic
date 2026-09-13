// ── Entities/Entities.cs — CORE (owner A, wave 1; plan §2, §4.1 — over the 1,240-line budget since the packed
//    identity landed, §2 being the whole of the difference) ────────────────────────────────────────
//
// THE FOUNDATION. Everything else in Entities/ binds to what is in this file: the uri parse, the column slab, the
// per-kind table skeleton, the scope (table set) that a locale/market/account switch replaces whole, the authority
// ladder, the staging buffer a worker fills and the UI drain commits, and the `Entities` statics (Boot, Switch, the
// factories, Ensure, Commit, Publish).
//
// The model in one paragraph (design record §5.3/§5.4, D5-D10, D13): there is no object per entity. A `Track` is a
// `readonly struct` over an `int` slot; the data is columns — `T[]` slabs, one array per field, indexed by slot. A
// provider answer fills columns, ORs bits into `Known` and bumps `Version`; a page compares `Version` across frames
// and asks `Knows(fields)` instead of null-checking. "Which rows still need a fetch" is `wanted & ~known` over one
// column, not a walk over a graph (P3, P11). Text is an engine `StringId`, so the paint path never sees a `string`
// this layer allocated (§5.6, P6).
//
// Rules this file is written under, and that a reader should hold it to:
//   P1/P13  entity = a small integer; columns are `T[]` of unmanaged T, so the GC sees ONE object per column.
//   P2/P7   columns are grouped by WHO reads them together; bookkeeping is its own group so a trim scan touches only it.
//   P4      every API is a batch API. The single-item call is a one-element span.
//   P5      slabs grow ×2 and never shrink; a freed row is a free-list slot, and `Version` bumps so a stale handle
//           held across the free is detectable (a generational index, the engine SlabAllocator's discipline).
//   P6/P14  identity is a packed 24-byte `EntityId` (§2), and the uri→slot lookup takes a `ReadOnlySpan<byte>`/
//           `ReadOnlySpan<char>` probe that allocates NOTHING on a hit: a catalog uri decodes to its 128-bit gid and
//           probes an open-addressed `int[]` over the `Id` column (3.0 ns), and everything else probes the text map
//           through `UriKeys`'s span alternate lookup. A row's interned text is REF-COUNTED — see the block on `Table`.
//           (Measured: docs/plans/wavee/wavee-0.3-entity-identity-memory.md, option 2, approved 2026-09-12: a track
//           row 282 → ~120 B, wire→slot 86-122 → 37-57 ns, a key hit 52 → 3.0 ns, `.Uri`'s re-parse → a field load.)
//   P8/P9   no allocation after warm-up on these paths; no LINQ, no closures, no async, no boxing, no `object` API.
//   P15     the `wanted & ~known` sweep is vectorized (`ScanMissing`), Vector128 with a scalar fallback and a length
//           floor — arm64 NEON is the ceiling and a vector path under ~16 elements measured SLOWER than scalar.
//   C1      SINGLE WRITER. Every mutation here happens on the UI thread, inside one `AppHost.Post` drain. A worker
//           decodes into a `Staging` and posts; the drain commits. Nothing else may touch a column.
//   C3      one drain = one publication: writes bump per-row `Version`s and mark tables dirty; `Publish()` fires each
//           dirty table's single `Changed` signal exactly once (D8), so a 300-row answer costs ONE re-render.
//   C7      `Inflight` carries the scope epoch, so a late answer for a replaced scope is dropped instead of committed.
//
// SLOT 0 IS "NONE", IN EVERY TABLE. `Alloc` never returns it. A `0` in a slot-valued column (`Track.Album`,
// `Track.VideoCounterpart`, an edge target) therefore reads as "unknown/none" with no nullable and no sentinel of its
// own (P3). `Count` starts at 1 for exactly this reason.

using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

// ── 1. kinds and providers ───────────────────────────────────────────────────────────────────────────────────────────

/// <summary>What an entity uri addresses — and therefore which table its row lives in. Ordered so the six kinds the
/// metadata transport can fetch come first (ported from <c>Wavee.Core/Hydration/EntityUri.cs</c>).
/// <para><c>spotify:prerelease:&lt;id&gt;</c> is deliberately NOT a kind of its own: a prerelease resolves to an ALBUM
/// row carrying <c>AlbumFlags.PreRelease</c> (ch 05, ch 31 §7.3), so the countdown surface reads one table and a
/// release does not migrate a row between kinds. <see cref="EntityUri.IsPrerelease"/> answers the uri shape.</para></summary>
public enum EntityKind : byte { Unknown, Track, Episode, Album, Artist, Playlist, Show, User, Collection, Concert }

/// <summary>Who owns a uri — the routing half of the parse, kept separate from <see cref="EntityKind"/>, which is the
/// hydration half. Typed rather than the raw <c>byte</c> plan §4.1 sketched: the same one byte in a column, but a
/// <c>switch</c> over it is exhaustive and a magic 2 cannot be mistyped as a 3.</summary>
public enum EntityProvider : byte
{
    /// <summary>Nobody owns it: an unrecognized uri. Never a guess — an unowned uri must not be addressed at a
    /// transport that would 404 on it. Also <c>default</c>, so <c>default(EntityUri)</c> is honest.</summary>
    None = 0,
    Spotify = 1,
    /// <summary><c>local:*</c> and <c>wavee:local:*</c> — the imported-files library.</summary>
    Local = 2,
    /// <summary><c>wavee:module:&lt;id&gt;:&lt;b64url(playableId)&gt;</c> — Radio / Twitch / YouTube playback modules.</summary>
    Module = 3,
    /// <summary><c>fake:*</c> and the bare legacy ids the demo catalog mints (<c>tr7</c>, <c>al7</c>, …).</summary>
    Fake = 4,
    /// <summary><c>wavee:playlist:*</c> — session-created user playlists.</summary>
    UserPlaylist = 5,
    /// <summary><c>wavee:show:*</c> / <c>wavee:episode:*</c> — the synthetic podcast source.</summary>
    WaveePodcast = 6,
}

/// <summary>Who wrote a column group, and therefore who may overwrite it (D16). A LOWER rung never overwrites a higher
/// one — but it DOES fill a group that is not <c>Known</c> yet, which is the whole point: a search result may complete
/// a row a full fetch has not reached, and may not degrade one it already filled.
/// <para><see cref="Seed"/> is ch 31 §7.2 GAP 2: the demo catalog writes below every wire rung, so a live decode wins
/// cleanly over a seeded row and the diagnostics row inspector can say where a value came from.</para></summary>
public enum Authority : byte
{
    None = 0,
    /// <summary><c>Entities.SeedFake</c> — synthetic, below every wire answer (ch 31).</summary>
    Seed = 1,
    /// <summary>A wire answer that carries only part of a group: a search hit, a playlist item, a cluster row.</summary>
    Thin = 2,
    /// <summary>A wire answer for the entity itself: TrackV4, getAlbum, the pathfinder subject.</summary>
    Full = 3,
    /// <summary>The user's own edit, or a local file's own tags. Nothing overwrites it.</summary>
    Local = 4,
}

/// <summary>How badly a batch is wanted. Declared here rather than nested in <c>Fetch</c> because it is part of the
/// <see cref="Entities.Ensure"/> signature every page calls — <c>Fetch.cs</c> must NOT declare a second one.</summary>
public enum FetchPriority : byte { Prefetch, Visible, Playback }

// ── 2. identity: the packed id, base62, and the uri as a view over it ────────────────────────────────────────────────
//
// THE ROW'S IDENTITY IS 24 PACKED BYTES, NOT A STRING. Measured (docs/plans/wavee/wavee-0.3-entity-identity-memory.md
// §1.2, §3): a `Column<StringId>` + `Dictionary<StringId,int>` + the engine StringTable's copy of the text cost 193 B
// of the 282 B a track row occupied — 68 % of the row — every probe of that map re-hashed 36 resolved characters
// (52 ns), and `.Uri` re-parsed the provider out of the text on every read (45-100 ns). Option 2 of that investigation,
// approved 2026-09-12: the six catalog kinds the wire hands us as a 16-byte gid keep the gid, everything else keeps a
// `StringId`, and both carry their kind, provider and form in the same word. A track row is ~120 B, the wire→slot path
// is 37-57 ns with no allocation, a key hit is 3.0 ns, and `.Kind`/`.Provider` are field loads.
//
// WHY THE GID AND NOT THE TEXT (doc §2): the wire's native identity for track/episode/album/artist/playlist/show IS the
// 16 raw bytes — every 0.2.9 metadata decode base62-ENCODED it into a uri string (569 ns) that the next line hashed
// back into a slot. Keeping the gid deletes both halves: a decoder writes 16 bytes and the commit probes them (3 ns).
//
// WHY 24 BYTES AND NOT 16 (doc §5): the 8 extra bytes per row buy ONE identity type that carries its own kind and
// provider, which a cross-kind list (a search "All" hit, a queue row, a route subject, a pin) needs anyway and which
// kills the `EntityUri.Of` re-parse. Equality is 1.88 ns against 1.85 ns for a bare `int` — measured, not a rounding.

/// <summary>Which half of <see cref="EntityId"/>'s 128-bit payload is live. Decided ONCE, at the parse; nothing
/// downstream branches on it except <see cref="EntityId.Format(Span{char})"/> and the table's choice of index.</summary>
public enum EntityForm : byte
{
    /// <summary><c>default(EntityId)</c> — no identity at all. Slot 0's id, and a freed row's cleared cell.</summary>
    None = 0,
    /// <summary>A Spotify 128-bit gid. The six catalog kinds only.</summary>
    Gid = 1,
    /// <summary>An interned uri string. Everything else — local files, modules, the demo catalog, users, concerts,
    /// collections, folders, synthetic subjects, and any catalog uri whose id is not 22 base62 characters (a test
    /// fixture, a truncated wire value).</summary>
    Text = 2,
}

/// <summary>The spare byte of <see cref="EntityId"/>'s meta word. Eight bits; one is spent.</summary>
[Flags]
public enum EntityIdFlags : byte
{
    None = 0,
    /// <summary><c>spotify:prerelease:&lt;id&gt;</c>. The row is an ALBUM row (see <see cref="EntityKind"/>), but the
    /// gid is NOT the album's own gid and <see cref="EntityId.Format(Span{char})"/> must reproduce <c>prerelease:</c> —
    /// so the flag is part of the identity, and two ids differing only in it are two rows, exactly as the two uris are
    /// two rows today (doc §6).</summary>
    Prerelease = 1 << 0,
}

/// <summary>THE identity of a row: 24 bytes, no string, no table pointer. The payload is either a 128-bit Spotify gid
/// (<see cref="EntityForm.Gid"/>) or an interned <see cref="StringId"/> (<see cref="EntityForm.Text"/>); the meta word
/// holds the kind, the provider, the form and eight flag bits.
///
/// <para><b>Equality and hashing are three word compares and one multiply</b> (1.88 ns measured). The row-binding path
/// compares identity every frame and the table's open-addressed index probes it per wire uri, so nothing here may
/// resolve a string, walk a table or branch on the form.</para>
///
/// <para><b>Threading.</b> <see cref="ForGid(EntityKind,ReadOnlySpan{byte},EntityIdFlags)"/> and
/// <see cref="TryParseGid(ReadOnlySpan{byte},out EntityId)"/> are pure and thread-safe — a decoder on the socket thread
/// may call them (C10). <see cref="Parse(ReadOnlySpan{byte})"/> interns for the text form and is therefore UI-thread
/// only (C1), exactly as <c>EntityUri.Parse</c> has always been.</para>
///
/// <para><b>Text is materialised, never stored, for a gid row</b> (<see cref="Text"/>, 81 ns + one string): a deep
/// link, a <c>PutState</c> body, the sqlite key, a log line, copy-link. Nothing on a per-frame path may call it.</para></summary>
public readonly struct EntityId : IEquatable<EntityId>
{
    // Lo/Hi are the payload, Meta is kind | provider<<8 | form<<16 | flags<<24. Three scalar fields and not a byte
    // array, so a `Column<EntityId>` is one flat 24-byte slab the GC sees as a single object (P1/P13).
    readonly ulong _lo;
    readonly ulong _hi;
    readonly uint _meta;

    EntityId(ulong lo, ulong hi, EntityKind kind, EntityProvider provider, EntityForm form, EntityIdFlags flags)
    {
        _lo = lo;
        _hi = hi;
        _meta = (uint)kind | ((uint)provider << 8) | ((uint)form << 16) | ((uint)flags << 24);
    }

    /// <summary>Big enough for any uri a gid-form id formats to (<c>"spotify:" + "prerelease" + ":" + 22</c> = 41), so
    /// a caller sizes its stack buffer with this and never measures first.</summary>
    public const int MaxGidTextChars = 48;

    // ── the words ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Which table the row lives in. A FIELD read — this is what ends the per-read re-parse (<c>EntityUri.Of</c>
    /// resolved the string and walked the prefix on EVERY <c>.Uri</c>, 45-100 ns, doc §1.3 item 3).</summary>
    public EntityKind Kind => (EntityKind)(byte)_meta;

    /// <summary>Who owns the uri, and therefore which transport may be asked for it.</summary>
    public EntityProvider Provider => (EntityProvider)(byte)(_meta >> 8);

    /// <inheritdoc cref="EntityForm"/>
    public EntityForm Form => (EntityForm)(byte)(_meta >> 16);

    /// <inheritdoc cref="EntityIdFlags"/>
    public EntityIdFlags Flags => (EntityIdFlags)(byte)(_meta >> 24);

    /// <summary>No identity at all — <c>default</c>. Slot 0, a staged row nobody filled, a freed row's cleared cell.</summary>
    public bool IsEmpty => _meta == 0;

    /// <summary>A uri we own and can route: it named a kind AND a provider.</summary>
    public bool IsValid => Kind != EntityKind.Unknown && Provider != EntityProvider.None;

    /// <summary>A playable row: it has a duration, a play context and a queue identity.</summary>
    public bool IsPlayable => Kind is EntityKind.Track or EntityKind.Episode;

    /// <summary>A surface that OPENS to a list of playables (the kinds that page members).</summary>
    public bool IsContainer => Kind is EntityKind.Album or EntityKind.Playlist or EntityKind.Show
                                    or EntityKind.Collection or EntityKind.Artist;

    /// <inheritdoc cref="EntityIdFlags.Prerelease"/>
    public bool IsPrerelease => (Flags & EntityIdFlags.Prerelease) != 0;

    /// <summary>The 128-bit gid, or 0 for a text-form id.</summary>
    public UInt128 Gid => Form == EntityForm.Gid ? new UInt128(_hi, _lo) : UInt128.Zero;

    /// <summary>The interned uri string's id, or <see cref="StringId.Empty"/> for a gid-form id. A gid row has NO uri
    /// string anywhere in the process — that is the 158 B/row this change removes (doc §1.2).</summary>
    public StringId TextId => Form == EntityForm.Text ? new StringId((int)(uint)_lo) : StringId.Empty;

    /// <summary>Write the raw 16 big-endian bytes — what the metadata request, the audio-key request and
    /// <c>PutState</c> all want back. Returns 16, or 0 for a text-form id.</summary>
    public int WriteGid(Span<byte> dst16)
    {
        if (Form != EntityForm.Gid || dst16.Length < Base62.GidBytes) return 0;
        BinaryPrimitives.WriteUInt64BigEndian(dst16, _hi);
        BinaryPrimitives.WriteUInt64BigEndian(dst16[8..], _lo);
        return Base62.GidBytes;
    }

    // ── construction ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The identity a protobuf decode ALREADY holds: kind + the 16 raw bytes. No text, no base62, no intern.
    /// PURE and thread-safe — this is what a Wave-2 decoder calls on the socket thread (C10), and the commit resolves
    /// it to a slot in 3 ns where 0.2.9 paid a 569 ns encode plus a ~100 ns text lookup (doc §2). Spans shorter than 16
    /// bytes are read big-endian and left-padded, as the wire's own <c>bytes gid</c> field allows; an empty span, one
    /// longer than 16, or a kind that has no gid form answers <c>default</c>.</summary>
    public static EntityId ForGid(EntityKind kind, ReadOnlySpan<byte> gid, EntityIdFlags flags = EntityIdFlags.None)
    {
        if (gid.Length == 0 || gid.Length > Base62.GidBytes || !IsGidKind(kind)) return default;
        ulong hi = 0, lo = 0;
        for (int i = 0; i < gid.Length; i++) { hi = (hi << 8) | (lo >> 56); lo = (lo << 8) | gid[i]; }
        return new EntityId(lo, hi, kind, EntityProvider.Spotify, EntityForm.Gid, flags);
    }

    /// <inheritdoc cref="ForGid(EntityKind,ReadOnlySpan{byte},EntityIdFlags)"/>
    public static EntityId ForGid(EntityKind kind, UInt128 gid, EntityIdFlags flags = EntityIdFlags.None)
        => IsGidKind(kind)
            ? new EntityId((ulong)gid, (ulong)(gid >> 64), kind, EntityProvider.Spotify, EntityForm.Gid, flags)
            : default;

    /// <summary>The identity of a uri that is not a catalog gid: the interned string carries it. The caller has already
    /// interned (UI thread, C1); the table AddRefs it — see the REF-COUNTING block on <see cref="Table"/>.</summary>
    public static EntityId ForText(EntityKind kind, EntityProvider provider, StringId text)
        => text.IsEmpty ? default : new EntityId((uint)text.Value, 0, kind, provider, EntityForm.Text, EntityIdFlags.None);

    /// <summary>Classify a uri that is ALREADY interned. The parse runs once per row here (an <c>Alloc</c>, a store
    /// read) and never per read, which is the point of the packed id. A 22-base62 catalog uri lands on the GID form
    /// even though it arrived as a string, so <c>Slot(StringId)</c> and <c>Slot(utf8)</c> can never disagree about
    /// which row a uri names.</summary>
    public static EntityId Of(StringId text)
    {
        if (text.IsEmpty) return default;
        string s = Entities.Strings.Resolve(text);
        if (TryParseGid(s.AsSpan(), out var gid)) return gid;
        var provider = EntityUri.ProviderOf(s.AsSpan(), out var kind);
        return ForText(provider == EntityProvider.None ? EntityKind.Unknown : kind, provider, text);
    }

    // ── the parse ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The GID half of the parse: pure, allocation-free, thread-safe, and the path the wire's hot loop takes
    /// (P14 — a 300-uri batch resolves straight from UTF-8). True only for <c>spotify:</c> + one of the six catalog
    /// kinds + a trailing segment of exactly 22 in-alphabet base62 characters whose value fits in 128 bits. Anything
    /// else answers false and the caller falls to the text form — deliberately: a 22-char string that OVERFLOWS is not
    /// the encoding of any gid, and 0.2.9's decoder truncated such a string silently
    /// (<c>Backend/Spotify/Base62.cs:26-36</c>), which here would alias two entities onto one row (doc §6).
    ///
    /// <para><b>Defect 4, folded here.</b> The id is the TRAILING segment, so <c>spotify:user:&lt;u&gt;:playlist:&lt;g&gt;</c>
    /// and <c>spotify:playlist:&lt;g&gt;</c> produce the SAME id and therefore ONE row — where two spellings interned to
    /// two <c>StringId</c>s and allocated two <c>PlaylistTable</c> rows for one playlist (doc §4.4). It is UNVERIFIED
    /// whether the 0.3 wire path still emits the user-namespaced spelling (0.2.9's Home and recents did); the fold
    /// costs nothing if it does not, and it is the decision written down rather than an accident — a round trip through
    /// <see cref="Format(Span{char})"/> answers the canonical <c>spotify:playlist:&lt;g&gt;</c>, which Connect and deep
    /// links accept (0.2.9 already folds the Liked spellings the same way).</para></summary>
    public static bool TryParseGid(ReadOnlySpan<byte> utf8, out EntityId id)
    {
        id = default;
        if (EntityUri.ProviderOf(utf8, out var kind) != EntityProvider.Spotify || !IsGidKind(kind)) return false;
        if (!Base62.TryDecode(EntityUri.IdOf(utf8), out UInt128 value)) return false;
        id = ForGid(kind, value, EntityUri.IsPrerelease(utf8) ? EntityIdFlags.Prerelease : EntityIdFlags.None);
        return true;
    }

    /// <inheritdoc cref="TryParseGid(ReadOnlySpan{byte},out EntityId)"/>
    public static bool TryParseGid(ReadOnlySpan<char> s, out EntityId id)
    {
        id = default;
        if (EntityUri.ProviderOf(s, out var kind) != EntityProvider.Spotify || !IsGidKind(kind)) return false;
        if (!Base62.TryDecode(EntityUri.IdOf(s), out UInt128 value)) return false;
        id = ForGid(kind, value, EntityUri.IsPrerelease(s) ? EntityIdFlags.Prerelease : EntityIdFlags.None);
        return true;
    }

    /// <summary>The whole parse: the gid form when the uri is one, else the interned text form — INCLUDING for a uri
    /// nobody owns, which keeps its text and answers <see cref="EntityKind.Unknown"/> /
    /// <see cref="EntityProvider.None"/> rather than losing what it was. Interns, so UI thread only (C1).</summary>
    public static EntityId Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return default;
        if (TryParseGid(utf8, out var gid)) return gid;
        var provider = EntityUri.ProviderOf(utf8, out var kind);
        return ForText(provider == EntityProvider.None ? EntityKind.Unknown : kind, provider, Entities.Intern(utf8));
    }

    /// <inheritdoc cref="Parse(ReadOnlySpan{byte})"/>
    public static EntityId Parse(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return default;
        if (TryParseGid(s, out var gid)) return gid;
        var provider = EntityUri.ProviderOf(s, out var kind);
        return ForText(provider == EntityProvider.None ? EntityKind.Unknown : kind, provider, Entities.Strings.Intern(s));
    }

    /// <summary><see cref="Parse(ReadOnlySpan{char})"/>, but false for a uri no provider owns — the shape a deep link
    /// or a persisted setting wants, where "we do not know what this is" must not become a row.</summary>
    public static bool TryParse(ReadOnlySpan<char> s, out EntityId id)
    {
        id = Parse(s);
        return id.IsValid;
    }

    /// <inheritdoc cref="TryParse(ReadOnlySpan{char},out EntityId)"/>
    public static bool TryParse(ReadOnlySpan<byte> utf8, out EntityId id)
    {
        id = Parse(utf8);
        return id.IsValid;
    }

    // ── back to text (COLD: deep links, PutState, the sqlite key, logs, copy-link) ──────────────────────────────────

    /// <summary>How many characters <see cref="Format(Span{char})"/> writes.</summary>
    public int FormattedLength => Form switch
    {
        EntityForm.Gid => SpotifyScheme.Length + Token.Length + 1 + Base62.GidChars,
        EntityForm.Text => Entities.Strings.Resolve(TextId).Length,
        _ => 0,
    };

    /// <summary>Write the canonical uri text: 81 ns for a gid (two <c>UInt128</c> divisions and 22 digit writes), a
    /// copy for the text form, and NO allocation either way — which is what lets the store, the planner and the
    /// <c>PutState</c> builder format into a pooled buffer instead of resolving a string per row (doc §3.1 item 2).</summary>
    public int Format(Span<char> dst)
    {
        switch (Form)
        {
            case EntityForm.Gid:
                var token = Token;
                int n = SpotifyScheme.Length + token.Length + 1 + Base62.GidChars;
                if (dst.Length < n) throw new ArgumentException($"an entity uri needs {n} chars", nameof(dst));
                SpotifyScheme.CopyTo(dst);
                token.CopyTo(dst[SpotifyScheme.Length..]);
                dst[SpotifyScheme.Length + token.Length] = ':';
                Base62.Encode(new UInt128(_hi, _lo), dst[(SpotifyScheme.Length + token.Length + 1)..]);
                return n;
            case EntityForm.Text:
                string s = Entities.Strings.Resolve(TextId);
                if (dst.Length < s.Length) throw new ArgumentException($"an entity uri needs {s.Length} chars", nameof(dst));
                s.CopyTo(dst);
                return s.Length;
            default:
                return 0;
        }
    }

    /// <inheritdoc cref="Format(Span{char})"/>
    public int Format(Span<byte> dst)
    {
        if (Form == EntityForm.Text) return Encoding.UTF8.GetBytes(Entities.Strings.Resolve(TextId), dst);
        if (Form == EntityForm.None) return 0;
        Span<char> buf = stackalloc char[MaxGidTextChars];
        return Encoding.UTF8.GetBytes(buf[..Format(buf)], dst);
    }

    /// <summary>The uri as a <c>string</c> — ONE allocation, at a cold call site (a deep link, a <c>PutState</c> body,
    /// a sqlite key, a log line, copy-link). A gid-form id materialises it; a text-form id resolves the one that
    /// already exists. NEVER per row per frame: compare the id instead.</summary>
    public string Text
    {
        get
        {
            if (Form == EntityForm.Text) return Entities.Strings.Resolve(TextId);
            if (Form == EntityForm.None) return "";
            Span<char> buf = stackalloc char[MaxGidTextChars];
            return new string(buf[..Format(buf)]);
        }
    }

    public override string ToString() => Text;

    const string SpotifyScheme = "spotify:";

    // The kind token as the uri spells it. `prerelease` is a FLAG on an album id rather than a kind of its own, so it
    // is read from the flags and not from `Kind`: a prerelease that formatted as `spotify:album:<its own gid>` would
    // name an album that does not exist (doc §6).
    ReadOnlySpan<char> Token => IsPrerelease ? "prerelease" : Kind switch
    {
        EntityKind.Track => "track",
        EntityKind.Episode => "episode",
        EntityKind.Album => "album",
        EntityKind.Artist => "artist",
        EntityKind.Playlist => "playlist",
        EntityKind.Show => "show",
        EntityKind.User => "user",
        EntityKind.Collection => "collection",
        EntityKind.Concert => "concert",
        _ => "unknown",
    };

    /// <summary>The six kinds the metadata transport addresses BY GID. Every other kind — user, concert, collection —
    /// and every non-Spotify provider takes the text form (doc §3.1 requirement 3).</summary>
    public static bool IsGidKind(EntityKind kind)
        => kind is EntityKind.Track or EntityKind.Episode or EntityKind.Album
                or EntityKind.Artist or EntityKind.Playlist or EntityKind.Show;

    // ── equality (1.88 ns; the table's index probes it per wire uri) ────────────────────────────────────────────────

    public bool Equals(EntityId other) => _lo == other._lo && _hi == other._hi && _meta == other._meta;
    public override bool Equals(object? obj) => obj is EntityId other && Equals(other);
    public static bool operator ==(EntityId a, EntityId b) => a.Equals(b);
    public static bool operator !=(EntityId a, EntityId b) => !a.Equals(b);

    /// <summary>A finalizing mix, not a fold: real gids are uniformly distributed but a text form's payload is a small
    /// sequential <see cref="StringId"/> and the meta word is nearly constant within a table, so an XOR alone would
    /// pile every text row into the low buckets of a power-of-two index. One multiply and three shifts (murmur3's
    /// finalizer) is what keeps the open-addressed probe at 3.0 ns with a one-or-two-slot chain (doc §4.1).</summary>
    public override int GetHashCode()
    {
        ulong h = _lo ^ BitOperations.RotateLeft(_hi, 31) ^ ((ulong)_meta << 40);
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 29;
        return (int)h ^ (int)(h >> 32);
    }
}

/// <summary>A cross-kind row pointer: WHICH TABLE, plus the slot in it (doc §3.1 requirement 4). A slot alone is
/// ambiguous the moment a list mixes kinds — the search "All" facet, the queue, a route subject, the history log, a pin
/// — and 0.3 had nothing recording it: <c>Edges.SearchResult</c> was an <c>EdgeTable&lt;NoEdge&gt;</c> over "entity
/// slots" with the table left implicit (<c>Search.cs:7</c>). The kind rides on the <see cref="EntityId"/> for free, so
/// this is 8 bytes and no lookup.</summary>
public readonly record struct EntityRef(EntityKind Kind, int Slot)
{
    /// <summary>Nothing is pointed at (slot 0 is "none" in every table, and Unknown has no table).</summary>
    public bool IsNone => Slot <= 0 || Kind == EntityKind.Unknown;

    /// <summary>The table the slot indexes, or null for a kind with no table of its own.</summary>
    public Table? Table => Entities.TableFor(Kind);

    /// <summary>The row's identity, or <c>default</c> when the ref is kindless or points past the table.</summary>
    public EntityId Id
    {
        get
        {
            var table = Entities.TableFor(Kind);
            return table is null || (uint)Slot >= (uint)table.Count ? default : table.Id[Slot];
        }
    }
}

/// <summary>Spotify's gid ↔ base62 pair (librespot's alphabet <c>0-9a-zA-Z</c>), moved here from
/// <c>Backend/Spotify/Base62.cs</c> because identity is now the only thing that needs it — and rewritten, because that
/// implementation is one of the costs this change removes: its encoder is a 22-step <c>UInt128</c> divide loop (569 ns
/// measured, doc §3), its decoder accumulates through a <c>BigInteger</c> and allocates a <c>byte[]</c>, and it
/// SILENTLY TRUNCATES a 22-char string whose value exceeds 2^128 (<c>Base62.cs:26-36</c>) — which here would alias two
/// entities onto one row.
///
/// <para><b>The arithmetic</b> (doc §2): 62^21 &lt; 2^128 &lt; 62^22, so 22 is the minimum width that covers every
/// 128-bit gid, and ~87 % of 22-char strings are not the encoding of any gid.
/// <see cref="TryDecode(ReadOnlySpan{byte},out UInt128)"/> REFUSES those instead of wrapping; the caller falls to the
/// text form and the two entities stay two rows.</para></summary>
public static class Base62
{
    const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    // `62` as a uint, not an int: UInt128 declares an IMPLICIT conversion only from the unsigned primitives (the
    // language's constant-expression conversion is a rule about the built-in integral types, not a library one), so
    // `acc * 62` with an int literal does not compile at all.
    const uint Radix = 62;

    /// <summary>A gid is exactly 22 base62 characters.</summary>
    public const int GidChars = 22;
    /// <summary>…and exactly 16 raw bytes.</summary>
    public const int GidBytes = 16;

    // 62^10 — the largest power of 62 that fits in a ulong. Two UInt128 divisions split the value into three ulong
    // chunks (2 + 10 + 10 digits); every digit after that is a ulong div/mod by a constant, which the JIT turns into a
    // multiply. 81 ns, against the 569 ns of a 22-step UInt128 divide loop (doc §3).
    const ulong Pow10 = 839_299_365_868_340_224UL;

    /// <summary>Decode exactly <see cref="GidChars"/> in-alphabet characters, REFUSING any value that does not fit in
    /// 128 bits. False leaves <paramref name="value"/> at zero and means "this is not a gid" — never "here is a wrapped
    /// one". The overflow guard is two comparisons per digit and not a division, because <c>UInt128</c> division is
    /// precisely the operation this decoder exists to avoid (<c>UInt128.MaxValue / Radix</c> folds to a constant).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> utf8, out UInt128 value)
    {
        value = UInt128.Zero;
        if (utf8.Length != GidChars) return false;
        UInt128 acc = UInt128.Zero;
        for (int i = 0; i < GidChars; i++)
        {
            int digit = DigitOf(utf8[i]);
            if (digit < 0) return false;
            if (acc > UInt128.MaxValue / Radix) return false;                    // acc * 62 would wrap
            acc *= Radix;
            if (acc > UInt128.MaxValue - (UInt128)(uint)digit) return false;  // acc + digit would wrap
            acc += (uint)digit;
        }
        value = acc;
        return true;
    }

    /// <inheritdoc cref="TryDecode(ReadOnlySpan{byte},out UInt128)"/>
    public static bool TryDecode(ReadOnlySpan<char> s, out UInt128 value)
    {
        value = UInt128.Zero;
        if (s.Length != GidChars) return false;
        UInt128 acc = UInt128.Zero;
        for (int i = 0; i < GidChars; i++)
        {
            char c = s[i];
            int digit = c < 0x80 ? DigitOf((byte)c) : -1;
            if (digit < 0) return false;
            if (acc > UInt128.MaxValue / Radix) return false;
            acc *= Radix;
            if (acc > UInt128.MaxValue - (UInt128)(uint)digit) return false;
            acc += (uint)digit;
        }
        value = acc;
        return true;
    }

    /// <summary>Encode to exactly <see cref="GidChars"/> characters, zero-padded — the spelling every Spotify uri uses.
    /// Returns 22; allocates nothing.</summary>
    public static int Encode(UInt128 value, Span<char> dst)
    {
        if (dst.Length < GidChars) throw new ArgumentException("a base62 gid needs 22 chars", nameof(dst));
        ulong low = (ulong)(value % Pow10);
        UInt128 rest = value / Pow10;
        ulong mid = (ulong)(rest % Pow10);
        ulong high = (ulong)(rest / Pow10);          // ≤ 483, so two digits cover it (62^2 = 3,844)
        WriteDigits(high, dst[..2]);
        WriteDigits(mid, dst.Slice(2, 10));
        WriteDigits(low, dst.Slice(12, 10));
        return GidChars;
    }

    /// <inheritdoc cref="Encode(UInt128,Span{char})"/>
    public static int Encode(UInt128 value, Span<byte> dst)
    {
        if (dst.Length < GidChars) throw new ArgumentException("a base62 gid needs 22 bytes", nameof(dst));
        Span<char> buf = stackalloc char[GidChars];
        Encode(value, buf);
        for (int i = 0; i < GidChars; i++) dst[i] = (byte)buf[i];
        return GidChars;
    }

    /// <summary>The 16 raw big-endian bytes of a gid — the wire's own spelling.</summary>
    public static void WriteBytes(UInt128 value, Span<byte> dst16)
    {
        if (dst16.Length < GidBytes) throw new ArgumentException("a gid is 16 bytes", nameof(dst16));
        BinaryPrimitives.WriteUInt64BigEndian(dst16, (ulong)(value >> 64));
        BinaryPrimitives.WriteUInt64BigEndian(dst16[8..], (ulong)value);
    }

    /// <summary>Read up to 16 big-endian bytes as a gid value (a shorter span is left-padded, as the wire allows).</summary>
    public static UInt128 ReadBytes(ReadOnlySpan<byte> gid)
    {
        UInt128 value = UInt128.Zero;
        for (int i = 0; i < gid.Length && i < GidBytes; i++) value = (value << 8) | gid[i];
        return value;
    }

    static void WriteDigits(ulong v, Span<char> dst)
    {
        for (int i = dst.Length - 1; i >= 0; i--) { dst[i] = Alphabet[(int)(v % Radix)]; v /= Radix; }
    }

    // Case-sensitive by construction, as the alphabet is and as today's ordinal uri compare is: `0-9`, then `a-z`, then
    // `A-Z` — librespot's order, which is NOT ascii order.
    static int DigitOf(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'z' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'Z' => c - 'A' + 36,
        _ => -1,
    };
}

/// <summary>THE uri, as a VIEW over <see cref="EntityId"/> — the text-facing half of identity. Before this type the app
/// carried six copies of <c>IdOf</c>, two <c>KindFor</c>s and ~40 hand-rolled <c>StartsWith("spotify:track:")</c> gates,
/// which is why episodes were silently dropped by seven services. There is ONE parse, and since 2026-09-12 one packed
/// identity behind it.
///
/// <para><b>What belongs where:</b> <see cref="EntityId"/> is what a column stores, an index probes and a frame
/// compares; this view is what a call site holds when it is about to talk in URIS — a deep link, a copy-link, a
/// <c>PutState</c> body, a test fixture. <see cref="Text"/> materialises, so nothing per-frame calls it.</para>
///
/// <para><b>Threading.</b> <see cref="Parse(ReadOnlySpan{char})"/> interns for the text form, so it is UI-thread only
/// (C1). A worker that only needs to route calls <see cref="KindOf(ReadOnlySpan{byte})"/>,
/// <see cref="ProviderOf(ReadOnlySpan{byte},out EntityKind)"/> or
/// <see cref="EntityId.TryParseGid(ReadOnlySpan{byte},out EntityId)"/>, which are pure, allocation-free and
/// thread-safe.</para></summary>
public readonly record struct EntityUri(EntityId Id)
{
    /// <summary>The canonical Liked Songs uri — the one spelling routes, pin ids, now-playing matches and the
    /// collection cover are all written with.</summary>
    public const string LikedCollection = "spotify:collection:tracks";

    /// <summary>A rootlist folder's wire prefix (<c>spotify:folder:&lt;hex&gt;</c>). Not an <see cref="EntityKind"/> —
    /// there is no catalog entity behind the id — only a uri shape <see cref="FolderIdOf"/> recognises for the pins.</summary>
    public const string FolderPrefix = "spotify:folder:";

    /// <summary>Longest uri we transcode on the stack. A <c>wavee:local:file:&lt;b64url(path)&gt;</c> is the long case
    /// and stays well inside it; anything longer takes a pooled buffer rather than a bigger frame.</summary>
    internal const int StackChars = 512;

    /// <inheritdoc cref="EntityId.Kind"/>
    public EntityKind Kind => Id.Kind;
    /// <inheritdoc cref="EntityId.Provider"/>
    public EntityProvider Provider => Id.Provider;
    /// <inheritdoc cref="EntityId.IsValid"/>
    public bool IsValid => Id.IsValid;
    /// <summary>A playable row: it has a duration, a play context and a queue identity.</summary>
    public bool IsPlayable => Id.IsPlayable;
    /// <summary>A surface that OPENS to a list of playables (the kinds that page members).</summary>
    public bool IsContainer => Id.IsContainer;
    /// <inheritdoc cref="EntityId.Text"/>
    public string Text => Id.Text;

    /// <summary>Parse UTF-8 wire bytes. Interns for the text form (UI thread, C1); the walk itself is byte-wise and
    /// never materializes a substring, and a gid-form uri never becomes a string at all.</summary>
    public static EntityUri Parse(ReadOnlySpan<byte> utf8) => new(EntityId.Parse(utf8));

    /// <summary>Parse UTF-16 text (a deep link, a settings value, a test).</summary>
    public static EntityUri Parse(ReadOnlySpan<char> s) => new(EntityId.Parse(s));

    /// <summary>The kind alone — the allocation-free, thread-safe routing primitive.</summary>
    public static EntityKind KindOf(ReadOnlySpan<char> s) => ProviderOf(s, out var k) == EntityProvider.None ? EntityKind.Unknown : k;

    /// <inheritdoc cref="KindOf(ReadOnlySpan{char})"/>
    public static EntityKind KindOf(ReadOnlySpan<byte> utf8) => ProviderOf(utf8, out var k) == EntityProvider.None ? EntityKind.Unknown : k;

    /// <summary>THE id: the trailing segment after the last <c>':'</c> (<c>spotify:user:x:playlist:y</c> → <c>y</c>; a
    /// colon-less token is its own id). Returned as a slice of the input — no substring. This is also what folds the
    /// two playlist spellings onto one gid (<see cref="EntityId.TryParseGid(ReadOnlySpan{byte},out EntityId)"/>).</summary>
    public static ReadOnlySpan<char> IdOf(ReadOnlySpan<char> s)
    {
        int colon = s.LastIndexOf(':');
        return colon < 0 ? s : colon + 1 >= s.Length ? default : s[(colon + 1)..];
    }

    /// <inheritdoc cref="IdOf(ReadOnlySpan{char})"/>
    public static ReadOnlySpan<byte> IdOf(ReadOnlySpan<byte> utf8)
    {
        int colon = utf8.LastIndexOf((byte)':');
        return colon < 0 ? utf8 : colon + 1 >= utf8.Length ? default : utf8[(colon + 1)..];
    }

    /// <summary>Is this uri the Liked Songs collection, in ANY spelling the wire uses? The canonical form, the
    /// user-namespaced <c>spotify:user:&lt;u&gt;:collection</c> that Home and recents carry, and the facet-suffixed
    /// <c>…:collection:tracks</c>. The sibling collections (<c>albums</c>/<c>artists</c>/<c>shows</c>/<c>episodes</c>)
    /// must answer NO — they are separate surfaces — so the tail after <c>:collection</c> has to be absent or
    /// literally <c>:tracks</c>. Four copies of <c>uri == "spotify:collection:tracks"</c> in 0.2.9, all blind to the
    /// user-namespaced form, are why this lives here.</summary>
    public static bool IsLikedCollection(ReadOnlySpan<char> s)
    {
        if (s.SequenceEqual(LikedCollection)) return true;
        if (KindOf(s) != EntityKind.Collection) return false;
        // LAST occurrence: a username starting with "collection" would otherwise match the wrong span.
        int at = s.LastIndexOf(":collection");
        if (at < 0) return false;
        var tail = s[(at + ":collection".Length)..];
        return tail.Length == 0 || tail.SequenceEqual(":tracks");
    }

    /// <summary>Every liked spelling folded to <see cref="LikedCollection"/>, everything else passed through — so the
    /// artwork, the nav dispatcher and the now-playing match all compare one id.</summary>
    public static StringId CanonicalLiked(StringId uri)
        => IsLikedCollection(Entities.Strings.Resolve(uri)) ? Entities.Strings.Intern(LikedCollection) : uri;

    /// <summary>The same fold over a packed id. A collection is always the TEXT form (its id is <c>tracks</c>, not a
    /// gid), so this costs one resolve and only for collection-kind ids. Unlike the playlist fold it is NOT applied
    /// inside the parser: a caller asks for it, exactly as 0.2.9 did, because folding a collection in the parse would
    /// change the uri text a round trip produces for a spelling the wire keeps sending back at us.</summary>
    public static EntityId CanonicalLiked(EntityId id)
        => id.Form == EntityForm.Text && id.Kind == EntityKind.Collection
           && IsLikedCollection(Entities.Strings.Resolve(id.TextId))
            ? EntityId.ForText(EntityKind.Collection, EntityProvider.Spotify, Entities.Strings.Intern(LikedCollection))
            : id;

    /// <summary>An unreleased album (<c>spotify:prerelease:&lt;id&gt;</c>, extension kind 138). The row is an ALBUM row
    /// carrying <c>AlbumFlags.PreRelease</c>; this only answers the uri shape (ch 05, ch 31 §7.3). The packed id keeps
    /// the same answer in a flag bit — <see cref="EntityId.IsPrerelease"/> — so nothing downstream re-reads text.</summary>
    public static bool IsPrerelease(ReadOnlySpan<char> s) => s.StartsWith("spotify:prerelease:");

    /// <inheritdoc cref="IsPrerelease(ReadOnlySpan{char})"/>
    public static bool IsPrerelease(ReadOnlySpan<byte> utf8) => Starts(utf8, "spotify:prerelease:");

    /// <summary>The rootlist group id inside a folder wire uri, or empty when the shape is not
    /// <see cref="FolderPrefix"/> + 1..32 hex characters and nothing else. A slice, never a substring.</summary>
    public static ReadOnlySpan<char> FolderIdOf(ReadOnlySpan<char> s)
    {
        if (!s.StartsWith(FolderPrefix)) return default;
        var id = s[FolderPrefix.Length..];
        if (id.Length == 0 || id.Length > 32) return default;
        foreach (char c in id)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return default;
        return id;
    }

    // ── the span walk ────────────────────────────────────────────────────────────────────────────────────────────────
    // ONE implementation, over bytes. The char overload narrows to ASCII on the stack first: every scheme, provider and
    // kind token in every uri shape the app knows is ASCII, and a non-ASCII char narrows to 0xFF, which matches no
    // token — so the two overloads decide identically BY CONSTRUCTION and there is no second parser to keep in step.
    // (EntitiesTests pins that equivalence over a corpus that includes non-ASCII ids.)

    /// <summary>Provider + kind in one pass. Pure, allocation-free, thread-safe.</summary>
    public static EntityProvider ProviderOf(ReadOnlySpan<char> s, out EntityKind kind)
    {
        if (s.Length is 0 or > StackChars) { kind = EntityKind.Unknown; return EntityProvider.None; }
        Span<byte> ascii = stackalloc byte[StackChars];
        var narrowed = ascii[..s.Length];
        for (int i = 0; i < s.Length; i++) { char c = s[i]; narrowed[i] = c < 0x80 ? (byte)c : (byte)0xFF; }
        return ProviderOf(narrowed, out kind);
    }

    /// <inheritdoc cref="ProviderOf(ReadOnlySpan{char},out EntityKind)"/>
    public static EntityProvider ProviderOf(ReadOnlySpan<byte> s, out EntityKind kind)
    {
        if (Starts(s, "spotify:")) { kind = SpotifyKind(s[8..]); return EntityProvider.Spotify; }
        if (Starts(s, "wavee:"))
        {
            var rest = s[6..];
            if (Starts(rest, "local:")) { kind = LocalKind(rest[6..]); return EntityProvider.Local; }
            if (Starts(rest, "module:")) { kind = EntityKind.Track; return EntityProvider.Module; }
            if (Starts(rest, "playlist:")) { kind = EntityKind.Playlist; return EntityProvider.UserPlaylist; }
            if (Starts(rest, "show:")) { kind = EntityKind.Show; return EntityProvider.WaveePodcast; }
            if (Starts(rest, "episode:")) { kind = EntityKind.Episode; return EntityProvider.WaveePodcast; }
            kind = EntityKind.Unknown;
            return EntityProvider.None;   // wavee:skeleton:*, wavee:media:*, … — routed by their own owners, not here
        }
        if (Starts(s, "local:")) { kind = LocalKind(s[6..]); return EntityProvider.Local; }
        if (Starts(s, "fake:")) { kind = CatalogKind(Head(s[5..])); return EntityProvider.Fake; }
        var legacy = LegacyFakeKind(s);
        if (legacy != EntityKind.Unknown) { kind = legacy; return EntityProvider.Fake; }
        kind = EntityKind.Unknown;
        return EntityProvider.None;
    }

    static bool Starts(ReadOnlySpan<byte> s, ReadOnlySpan<char> ascii)
    {
        if (s.Length < ascii.Length) return false;
        for (int i = 0; i < ascii.Length; i++) if (s[i] != (byte)ascii[i]) return false;
        return true;
    }

    static bool Is(ReadOnlySpan<byte> s, ReadOnlySpan<char> ascii) => s.Length == ascii.Length && Starts(s, ascii);

    /// <summary>The segment up to the next <c>':'</c> (the whole span when there is none).</summary>
    static ReadOnlySpan<byte> Head(ReadOnlySpan<byte> s)
    {
        int colon = s.IndexOf((byte)':');
        return colon < 0 ? s : s[..colon];
    }

    // spotify:<type>[:…]. `user` is the one multiplexed head: spotify:user:<u>:playlist:<id> is a PLAYLIST and
    // spotify:user:<u>:collection[:…] is a COLLECTION — both were Unknown before 0.2.9's parser, which is why a
    // user-namespaced playlist never got its header.
    static EntityKind SpotifyKind(ReadOnlySpan<byte> rest)
    {
        var head = Head(rest);
        if (!Is(head, "user")) return CatalogKind(head);
        var tail = rest[head.Length..];                       // ":<u>:playlist:<id>" | ":<u>" | ""
        if (tail.Length == 0) return EntityKind.User;         // "spotify:user" — degenerate, still a user
        var user = Head(tail[1..]);
        var afterUser = tail[(1 + user.Length)..];
        if (afterUser.Length == 0) return EntityKind.User;    // spotify:user:<id>
        var facet = Head(afterUser[1..]);
        if (Is(facet, "playlist")) return EntityKind.Playlist;
        if (Is(facet, "collection")) return EntityKind.Collection;
        return EntityKind.User;                               // any other tail is still a user surface
    }

    // wavee:local:file:<b64url(path)> is a PLAYABLE (the local media provider decodes it at play time), so it maps to
    // Track like every other local playable — a "file" kind would just be a Track the queue could not carry.
    static EntityKind LocalKind(ReadOnlySpan<byte> rest)
    {
        var head = Head(rest);
        return Is(head, "file") ? EntityKind.Track : CatalogKind(head);
    }

    static EntityKind CatalogKind(ReadOnlySpan<byte> type)
    {
        if (Is(type, "track")) return EntityKind.Track;
        if (Is(type, "episode")) return EntityKind.Episode;
        if (Is(type, "album")) return EntityKind.Album;
        if (Is(type, "artist")) return EntityKind.Artist;
        if (Is(type, "playlist")) return EntityKind.Playlist;
        if (Is(type, "show")) return EntityKind.Show;
        if (Is(type, "user")) return EntityKind.User;
        if (Is(type, "collection")) return EntityKind.Collection;
        if (Is(type, "concert")) return EntityKind.Concert;
        // A prerelease resolves to an ALBUM row (see EntityKind's doc): one table, one countdown surface, and a
        // release does not migrate the row between kinds.
        if (Is(type, "prerelease")) return EntityKind.Album;
        return EntityKind.Unknown;
    }

    // The bare ids the demo catalog mints (`tr7`, `al7`, `pl7`, `ar7`) are ids, not uris — but they reach uri-shaped
    // call sites often enough to be worth claiming for the fake provider rather than silently answering Unknown. A
    // colon anywhere disqualifies the token, so no real uri can fall in here.
    static EntityKind LegacyFakeKind(ReadOnlySpan<byte> s)
    {
        if (s.Length < 3 || s.IndexOf((byte)':') >= 0) return EntityKind.Unknown;
        var kind = (s[0], s[1]) switch
        {
            ((byte)'t', (byte)'r') => EntityKind.Track,
            ((byte)'a', (byte)'l') => EntityKind.Album,
            ((byte)'p', (byte)'l') => EntityKind.Playlist,
            ((byte)'a', (byte)'r') => EntityKind.Artist,
            _ => EntityKind.Unknown,
        };
        if (kind == EntityKind.Unknown) return EntityKind.Unknown;
        for (int i = 2; i < s.Length; i++) if (s[i] is < (byte)'0' or > (byte)'9') return EntityKind.Unknown;
        return kind;
    }
}

// ── 3. the column ────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One column: a slab that grows ×2 and never shrinks (P5). <typeparamref name="T"/> is unmanaged, so the GC
/// sees exactly ONE object per column however many rows it holds, and a row update creates no garbage at all.
///
/// <para><b>It is a mutable struct, and that is deliberate</b> — the array reference sits inline in the owning table,
/// so a column read is one field load and one array index with no pointer chase. The cost is the usual rule: only ever
/// touch a column THROUGH the field that owns it (<c>table.Title[slot] = x</c>), never through a local copy, because
/// <see cref="EnsureCapacity"/> on a copy resizes the copy's array and drops the result on the floor. Helpers that grow
/// a column take it as <c>ref Column&lt;T&gt;</c> for the same reason.</para></summary>
public struct Column<T> where T : unmanaged
{
    T[]? _a;

    public Column(int capacity) => _a = capacity > 0 ? new T[capacity] : null;

    /// <summary>Row access. The caller has already ensured capacity (every <see cref="Table"/> allocation does).</summary>
    public readonly ref T this[int slot] => ref _a![slot];

    /// <summary>The whole slab, for a column scan (P11/P15). Slice it to the table's <c>Count</c> before scanning — the
    /// tail beyond <c>Count</c> is uninitialized capacity, not rows.</summary>
    public readonly Span<T> Span => _a;

    public readonly int Capacity => _a?.Length ?? 0;

    /// <summary>Grow to at least <paramref name="n"/>, doubling (P5). Never shrinks; a freed row is a free-list slot,
    /// not a smaller array.</summary>
    public void EnsureCapacity(int n)
    {
        if (_a is null) { _a = new T[Math.Max(n, 8)]; return; }
        if (n <= _a.Length) return;
        Array.Resize(ref _a, Math.Max(n, _a.Length * 2));
    }

    /// <summary>Zero a run of rows, so a recycled slot never shows the dead row's value.</summary>
    public readonly void Clear(int from, int count)
    {
        if (_a is not null && count > 0) Array.Clear(_a, from, count);
    }
}

// ── 4. change publication ────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A table (entity or edge) that carries ONE <see cref="Changed"/> signal (D8) and a dirty bit.
///
/// <para>Writes never touch the signal: they call <see cref="MarkDirty"/>, which enqueues the table once with
/// <see cref="Entities"/>. At the end of the drain <see cref="Entities.Publish"/> fires each dirty table's signal
/// exactly once with the new publication counter, so a 300-row commit costs ONE re-render, not 300 (C3, P10). A base
/// class rather than an interface so the publish walk is a plain non-virtual call with no interface dispatch and no
/// boxing (P9).</para></summary>
public abstract class Publishable
{
    /// <summary>Bumped once per publication in which this table changed. A bound list reads it to re-run.</summary>
    public readonly Signal<uint> Changed = new(0);

    internal bool Dirty;

    /// <summary>UI thread only (C1). Idempotent within a drain.</summary>
    public void MarkDirty()
    {
        if (Dirty) return;
        Dirty = true;
        Entities.EnqueueDirty(this);
    }

    internal void PublishNow(uint publication)
    {
        Dirty = false;
        Changed.Value = publication;
    }
}

// ── 5. the table skeleton ────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The bookkeeping every kind's table shares (P2 group 3). A kind's own file declares the value columns and
/// overrides <see cref="GrowColumns"/> and <see cref="ReleaseText"/>; everything here — slots, the free list, the
/// identity column and its two indexes, known bits, authority, freshness, inflight and the change signal — is written
/// once.
///
/// <para><b>Slot 0 is the permanent "none" row</b> and is never handed out; see the file header.</para>
///
/// <para><b>IDENTITY IS A PACKED <see cref="EntityId"/>, AND IT HAS TWO INDEXES</b> (doc §5, option 2). A gid-form row
/// — the six catalog kinds off the Spotify wire — lives in <see cref="Id"/> and in an open-addressed <c>int[]</c> of
/// slots keyed by that id: 6.6 B/row and a 3.0 ns key hit, against the 20-35 B/row and 52 ns of the
/// <c>Dictionary&lt;StringId,int&gt;</c> it replaces, whose comparer had to RESOLVE and re-hash 36 characters of text
/// on every probe (doc §4.1). A text-form row — every other provider, and every catalog uri whose id is not 22 base62
/// characters — keeps a <c>Dictionary&lt;StringId,int&gt;</c> with today's <see cref="UriKeys"/>, because that
/// comparer's span alternate-lookup is what lets <see cref="TryGetSlot(ReadOnlySpan{char},out int)"/> MISS without
/// interning the probe (a shared index would have to hash content to answer the same question).</para>
///
/// <para><b>REF-COUNTING, THE RULE EVERY KIND FILE FOLLOWS (defect 1).</b> The engine's <c>StringTable</c> reclaims an
/// id only when its last <c>AddRef</c> is released, and a string that was never AddRef'd is PERMANENT
/// (<c>..\fluent-gpu\src\FluentGpu.Engine\Foundation\StringTable.cs:26</c>). Nothing in <c>Entities/</c> ref-counted
/// anything before 2026-09-12, so <see cref="FreeSlot"/> dropped the map entry while the uri, title, image and
/// artist-line text stayed for the life of the process: a scope's trim could never reclaim text and its memory floor
/// only rose (doc §4.4). The discipline that fixes it is mechanical, and it is TWO CALLS:
/// <list type="number">
/// <item>write every <c>Column&lt;StringId&gt;</c> through <see cref="SetText"/> —
/// <c>t.SetText(ref t.Title, slot, s.Intern(row.Title));</c> — never <c>t.Title[slot] = …</c>, because the write has to
/// AddRef the incoming id and release the one it overwrites;</item>
/// <item>release every one of them in <see cref="ReleaseText"/> —
/// <c>protected override void ReleaseText(int slot) { ClearText(ref Title, slot); ClearText(ref Image, slot); }</c> —
/// which <see cref="FreeSlot"/> and <see cref="ReleaseAllText"/> call for you.</item>
/// </list>
/// <see cref="ReleaseText"/> is abstract for the same reason <see cref="GrowColumns"/> is: a kind cannot forget it.
/// Text held OUTSIDE a column (an edge payload, a side-slab field such as <c>Artist</c>'s pick row) uses the same pair
/// one level down, <see cref="Entities.RetainText"/> / <see cref="Entities.ReleaseText"/>.</para></summary>
public abstract class Table : Publishable
{
    /// <summary>The "no entity" slot. A slot-valued column holding 0 means unknown/none (P3).</summary>
    public const int None = 0;

    /// <summary>Slots handed out, INCLUDING freed ones and slot 0 — the high-water mark of the columns.</summary>
    public int Count = 1;

    /// <summary>Recycled slots (P5). A freed row's slot is reused; its <see cref="Version"/> keeps climbing so a page
    /// holding the old (slot, version) pair can tell.</summary>
    public readonly Stack<int> Free = new();

    /// <summary>Bumps on every write to the row (D8). A bound row compares it across frames — cost 1 (P5).</summary>
    public Column<uint> Version;
    /// <summary>Which field groups are filled, as the kind's <c>&lt;Kind&gt;Fields</c> bits (P3). Replaces every
    /// nullable, every 0-means-unknown convention and the whole "hydration level" idea: <c>wanted &amp; ~known</c>.</summary>
    public Column<uint> Known;
    /// <summary>Row-level authority: who wrote the row's IDENTITY (D16). Per-GROUP authority columns live in the kind's
    /// own table (<c>IdentityAuthority</c>, <c>ExtrasAuthority</c>, …) and are gated with <c>Accepts</c>.</summary>
    public Column<byte> Authority;
    /// <summary>Seconds since the app epoch when a provider last answered for this row (freshness / TTL, P7).</summary>
    public Column<int> FetchedAt;
    /// <summary>Seconds since the app epoch when a page last read this row (LRU for the store's trim scan, P7).</summary>
    public Column<int> Touched;
    /// <summary>The scope epoch that owns this row's in-flight request, 0 = none (C7). Ten pages asking for the same
    /// rows in one drain produce one request; a scope switch bumps the epoch and orphans the late answer.</summary>
    public Column<uint> Inflight;

    /// <summary>THE row's identity, packed (see the class summary and <see cref="EntityId"/>). Read it freely — kind,
    /// provider and the gid are field loads. WRITE it only through <see cref="Alloc(EntityId)"/> / <see cref="Bind"/> /
    /// <see cref="FreeSlot"/>, which keep the indexes and the text refcount in step.</summary>
    public Column<EntityId> Id;

    // The gid index: open addressing with linear probing, power-of-two capacity, load ≤ 0.75 (measured 6.6 B/row at
    // 0.61). A bucket holds the SLOT and nothing else — slot 0 is "none" in every table, so 0 is a free bucket for
    // free, and the key is read back out of the `Id` column rather than copied here (that is the whole memory win).
    // Deletion is a backward shift, not a tombstone, so a like/unlike storm cannot degrade the probe.
    int[] _index = [];
    int _mask;
    int _indexed;

    // The text index: today's map, for the population that still has uri text.
    readonly Dictionary<StringId, int> _byText = new(UriKeys.Instance);
    readonly Dictionary<StringId, int>.AlternateLookup<ReadOnlySpan<char>> _byTextChars;

    protected Table()
    {
        _byTextChars = _byText.GetAlternateLookup<ReadOnlySpan<char>>();
        EnsureCapacity(16);
    }

    /// <summary>Which kind's rows this table holds — the store keys its tables on it and the diagnostics row inspector
    /// names them with it.</summary>
    public abstract EntityKind Kind { get; }

    /// <summary>Live rows (slot 0 and the free list excluded).</summary>
    public int LiveCount => Count - 1 - Free.Count;

    /// <summary>Buckets in the gid index — the diagnostics page's load read (<c>IndexedRows / IndexCapacity</c>).</summary>
    public int IndexCapacity => _index.Length;
    /// <summary>Rows the gid index holds.</summary>
    public int IndexedRows => _indexed;
    /// <summary>Rows that still carry uri TEXT, and therefore a string in the interner (doc §6 "not solved").</summary>
    public int TextRows => _byText.Count;

    // ── slots ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Allocate a row for <paramref name="id"/> — a recycled slot if one is free, else a new one. Never
    /// returns <see cref="None"/>. The row starts with <c>Known = 0</c>: it exists, and nothing about it is known yet,
    /// which is exactly the state a page renders as a skeleton.</summary>
    public int Alloc(EntityId id)
    {
        int slot;
        if (Free.Count > 0) { slot = Free.Pop(); }
        else { slot = Count++; EnsureCapacity(Count); }
        BindId(slot, id);
        Version[slot]++;                 // ++ not = 1: a recycled slot's version must keep climbing (P5, generational)
        Known[slot] = 0;
        Authority[slot] = (byte)Wavee.Authority.None;
        Inflight[slot] = 0;
        FetchedAt[slot] = 0;
        Touched[slot] = Entities.Now;
        MarkDirty();
        return slot;
    }

    /// <summary>Allocate a row for an already-interned uri. Parses it ONCE, here, so a 22-base62 catalog uri lands on
    /// the gid form and the same entity can never occupy two rows depending on which spelling reached the table first
    /// (<see cref="EntityId.Of(StringId)"/>).</summary>
    public int Alloc(StringId uri) => Alloc(EntityId.Of(uri));

    /// <summary>Allocate <paramref name="n"/> CONTIGUOUS slots in one go and return the first (ch 31 §7 GAP 3). The
    /// seed and the decoder's bulk commit use it so ~1,700 rows cost one capacity probe instead of 1,700, and so a
    /// fixture's slot is addressable as <c>first + i</c>. Ignores the free list by construction — contiguity is the
    /// point. Identities are the caller's to fill, with <see cref="Bind"/> (never <c>Id[slot] = …</c>: that would
    /// index nothing and refcount nothing).</summary>
    public int AllocRun(int n)
    {
        if (n <= 0) return None;
        int first = Count;
        Count += n;
        EnsureCapacity(Count);
        for (int i = 0; i < n; i++)
        {
            int slot = first + i;
            Version[slot]++;
            Known[slot] = 0;
            Authority[slot] = (byte)Wavee.Authority.None;
            Inflight[slot] = 0;
            FetchedAt[slot] = 0;
            Touched[slot] = Entities.Now;
        }
        MarkDirty();
        return first;
    }

    /// <summary>Give a slot its identity (the <see cref="AllocRun"/> half of <see cref="Alloc(EntityId)"/>): indexes it, AddRefs
    /// a text-form uri, and drops whatever identity the slot carried before. THE only sanctioned write to
    /// <see cref="Id"/>.</summary>
    public void Bind(int slot, EntityId id)
    {
        if (slot <= None || slot >= Count) return;
        BindId(slot, id);
        Version[slot]++;
        MarkDirty();
    }

    /// <summary>Retire a row: its uri stops resolving, its interned text is released (defect 1), its version bumps (so
    /// a stale handle is detectable) and its slot goes on the free list. The store's trim tick is the only caller in
    /// normal operation (R2).</summary>
    public void FreeSlot(int slot)
    {
        if (slot <= None || slot >= Count) return;
        UnbindId(slot);
        ReleaseText(slot);               // the kind's own StringId columns — the other half of the leak (doc §4.4)
        Version[slot]++;
        Known[slot] = 0;
        Authority[slot] = (byte)Wavee.Authority.None;
        Inflight[slot] = 0;
        Free.Push(slot);
        MarkDirty();
    }

    /// <summary>The slot for an identity, allocating an empty row if unseen (D10). This is what every factory does.</summary>
    public int Slot(EntityId id) => TryGetSlot(id, out int s) ? s : Alloc(id);

    /// <summary>The slot for an already-interned uri (<see cref="EntityId.Of(StringId)"/> classifies it first).</summary>
    public int Slot(StringId uri) => Slot(EntityId.Of(uri));

    /// <summary>The slot for a uri given as text, allocating if unseen — and allocating NOTHING when it is already
    /// known, in either form (P6).</summary>
    public int Slot(ReadOnlySpan<char> uri)
        => EntityId.TryParseGid(uri, out var gid) ? Slot(gid) : SlotOfText(uri);

    // The text half, split out so the UTF-8 entry point does not re-run the gid walk it has already run once.
    int SlotOfText(ReadOnlySpan<char> uri)
    {
        if (_byTextChars.TryGetValue(uri, out int found)) return found;      // the hit path: no intern, no allocation
        var provider = EntityUri.ProviderOf(uri, out var kind);
        return Alloc(EntityId.ForText(provider == EntityProvider.None ? EntityKind.Unknown : kind, provider,
                                      Entities.Strings.Intern(uri)));
    }

    /// <summary>The slot for a uri straight off the wire (P14) — a 300-uri batch resolves each one from UTF-8 with no
    /// string, no transcode and no allocation when it is a catalog gid (37-57 ns), and falls to the stack transcode
    /// only for the text-form population.</summary>
    public int Slot(ReadOnlySpan<byte> utf8)
    {
        if (EntityId.TryParseGid(utf8, out var gid)) return Slot(gid);
        if (utf8.Length <= EntityUri.StackChars)
        {
            Span<char> buf = stackalloc char[EntityUri.StackChars];
            return SlotOfText(buf[..Encoding.UTF8.GetChars(utf8, buf)]);
        }
        char[] rented = ArrayPool<char>.Shared.Rent(utf8.Length);
        try { return SlotOfText(rented.AsSpan(0, Encoding.UTF8.GetChars(utf8, rented))); }
        finally { ArrayPool<char>.Shared.Return(rented); }
    }

    /// <summary>The slot for the identity a protobuf answer already carries: the 16 raw gid bytes, no uri anywhere
    /// (doc §2). The 3 ns path Wave 2's decoders commit through.</summary>
    public int Slot(EntityKind kind, ReadOnlySpan<byte> gid16) => Slot(EntityId.ForGid(kind, gid16));

    /// <summary>Look an identity up WITHOUT allocating a row — "do we already know about this?".</summary>
    public bool TryGetSlot(EntityId id, out int slot)
    {
        switch (id.Form)
        {
            case EntityForm.Gid: return IndexTryGet(id, out slot);
            case EntityForm.Text: return _byText.TryGetValue(id.TextId, out slot);
            default: slot = None; return false;
        }
    }

    /// <inheritdoc cref="TryGetSlot(EntityId,out int)"/>
    public bool TryGetSlot(StringId uri, out int slot) => TryGetSlot(EntityId.Of(uri), out slot);

    /// <summary>Look a uri up without allocating a row AND without interning it: a miss must not leave a permanent
    /// string behind (defect 1 — an id nobody AddRefs is never reclaimed).</summary>
    public bool TryGetSlot(ReadOnlySpan<char> uri, out int slot)
        => EntityId.TryParseGid(uri, out var gid) ? IndexTryGet(gid, out slot) : _byTextChars.TryGetValue(uri, out slot);

    /// <inheritdoc cref="TryGetSlot(ReadOnlySpan{char},out int)"/>
    public bool TryGetSlot(ReadOnlySpan<byte> utf8, out int slot)
    {
        if (EntityId.TryParseGid(utf8, out var gid)) return IndexTryGet(gid, out slot);
        if (utf8.Length <= EntityUri.StackChars)
        {
            Span<char> buf = stackalloc char[EntityUri.StackChars];
            return _byTextChars.TryGetValue(buf[..Encoding.UTF8.GetChars(utf8, buf)], out slot);
        }
        char[] rented = ArrayPool<char>.Shared.Rent(utf8.Length);
        try { return _byTextChars.TryGetValue(rented.AsSpan(0, Encoding.UTF8.GetChars(utf8, rented)), out slot); }
        finally { ArrayPool<char>.Shared.Return(rented); }
    }

    /// <inheritdoc cref="TryGetSlot(EntityId,out int)"/>
    public bool TryGetSlot(EntityKind kind, ReadOnlySpan<byte> gid16, out int slot)
        => TryGetSlot(EntityId.ForGid(kind, gid16), out slot);

    // ── the identity indexes ────────────────────────────────────────────────────────────────────────────────────────

    void BindId(int slot, in EntityId id)
    {
        if (Id[slot].Form != EntityForm.None) UnbindId(slot);       // a recycled slot, or a re-Bind over an old id
        Id[slot] = id;
        switch (id.Form)
        {
            case EntityForm.Gid:
                IndexAdd(slot, id);
                break;
            case EntityForm.Text:
                Entities.Strings.AddRef(id.TextId);                  // the row now OWNS this string (defect 1)
                _byText[id.TextId] = slot;
                break;
        }
    }

    void UnbindId(int slot)
    {
        ref EntityId cell = ref Id[slot];
        switch (cell.Form)
        {
            case EntityForm.Gid:
                IndexRemove(cell);
                break;
            case EntityForm.Text:
                _byText.Remove(cell.TextId);                         // remove FIRST: the comparer hashes the text
                Entities.Strings.Release(cell.TextId);
                break;
        }
        cell = default;
    }

    void IndexAdd(int slot, in EntityId id)
    {
        if ((_indexed + 1) * 4 > _index.Length * 3) GrowIndex(_index.Length == 0 ? 32 : _index.Length * 2);
        int i = id.GetHashCode() & _mask;
        for (int s = _index[i]; s != None; s = _index[i])
        {
            if (Id[s] == id) { _index[i] = slot; return; }           // same identity, new slot (a re-Bind)
            i = (i + 1) & _mask;
        }
        _index[i] = slot;
        _indexed++;
    }

    bool IndexTryGet(in EntityId id, out int slot)
    {
        if (_indexed > 0)
        {
            int i = id.GetHashCode() & _mask;
            for (int s = _index[i]; s != None; s = _index[i])
            {
                if (Id[s] == id) { slot = s; return true; }
                i = (i + 1) & _mask;
            }
        }
        slot = None;
        return false;
    }

    // Backward-shift deletion (Knuth 6.4 algorithm R): the entries after the hole are pulled back into it when their
    // own home bucket is not inside the run being closed. No tombstones — so a table that likes and unlikes all day
    // never degrades into a linear scan the way a tombstoned open-addressed table does.
    void IndexRemove(in EntityId id)
    {
        if (_indexed == 0) return;
        int i = id.GetHashCode() & _mask;
        while (true)
        {
            int s = _index[i];
            if (s == None) return;                                   // not indexed
            if (Id[s] == id) break;
            i = (i + 1) & _mask;
        }
        int j = i;
        while (true)
        {
            _index[i] = None;
            while (true)
            {
                j = (j + 1) & _mask;
                int s = _index[j];
                if (s == None) { _indexed--; return; }
                int home = Id[s].GetHashCode() & _mask;
                bool insideRun = i <= j ? (home > i && home <= j) : (home > i || home <= j);
                if (!insideRun) break;                               // this one belongs before the hole: move it in
            }
            _index[i] = _index[j];
            i = j;
        }
    }

    void GrowIndex(int capacity)
    {
        var old = _index;
        _index = new int[capacity];
        _mask = capacity - 1;
        _indexed = 0;
        for (int b = 0; b < old.Length; b++)
        {
            int slot = old[b];
            if (slot != None) IndexAdd(slot, Id[slot]);
        }
    }

    // ── known / authority (D16) ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Does this row already carry every bit of <paramref name="groups"/>? The one question a page asks before
    /// it paints, and the one the planner asks before it fetches (P3).</summary>
    public bool Knows(int slot, uint groups) => (Known[slot] & groups) == groups;

    /// <summary>THE authority rule (D16), pure and testable: a write of <paramref name="group"/> at
    /// <paramref name="incoming"/> lands when it is at least as authoritative as whoever wrote that group — or when the
    /// group is not fully known yet, because filling a hole is never a downgrade.
    /// <para>Read it as: <b>a Thin write never overwrites a Full one, but it does fill a group nobody has filled.</b></para></summary>
    public static bool Accepts(Authority incoming, Authority existing, uint known, uint group)
        => (known & group) != group || incoming >= existing;

    /// <summary>The same rule against a row's per-GROUP authority column. Call it before writing the group's columns;
    /// call <see cref="Applied"/> after.</summary>
    public bool Accepts(int slot, uint group, Authority incoming, in Column<byte> groupAuthority)
        => Accepts(incoming, (Wavee.Authority)groupAuthority.Span[slot], Known[slot], group);

    /// <summary>Record that a group WAS written: mark its bits known, raise the group's authority, stamp freshness,
    /// clear the in-flight marker, bump the row's version and mark the table dirty. The two-call shape
    /// (<c>Accepts</c> … write … <c>Applied</c>) keeps the column writes in
    /// the kind's own file with no delegate and no boxing (P9).</summary>
    public void Applied(int slot, uint group, Authority incoming, ref Column<byte> groupAuthority)
    {
        Known[slot] |= group;
        // Fully qualified: inside Table the FIELD `Authority` hides the type name in every expression position.
        if (incoming > (Wavee.Authority)groupAuthority[slot]) groupAuthority[slot] = (byte)incoming;
        if (incoming > (Wavee.Authority)Authority[slot]) Authority[slot] = (byte)incoming;
        Version[slot]++;
        FetchedAt[slot] = Entities.Now;
        Inflight[slot] = 0;
        MarkDirty();
    }

    /// <summary>A write that is not a provider answer (a user edit, an optimistic flip): mark known, bump, dirty.</summary>
    public void Bump(int slot, uint group = 0)
    {
        Known[slot] |= group;
        Version[slot]++;
        MarkDirty();
    }

    /// <summary>LRU touch for the store's trim scan (R2). One column write, no branch.</summary>
    public void Touch(int slot) => Touched[slot] = Entities.Now;

    // ── text ownership (defect 1) ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE way to write a <c>Column&lt;StringId&gt;</c>: <c>t.SetText(ref t.Title, slot, s.Intern(row.Title));</c>
    /// AddRefs the incoming id and releases the one it overwrites, so a row that is re-answered a hundred times owns
    /// exactly one title's worth of interner at the end of it. Writing the column directly leaks the overwritten
    /// string for the life of the process (class summary).</summary>
    public void SetText(ref Column<StringId> column, int slot, StringId value)
        => Entities.RetainText(ref column[slot], value);

    /// <summary>Release one text column's id and blank the cell — what a <see cref="ReleaseText"/> override is made
    /// of, and what keeps a recycled slot from carrying a released id.</summary>
    public void ClearText(ref Column<StringId> column, int slot)
        => Entities.ReleaseText(ref column[slot]);

    /// <summary>Give back every string THIS row owns. One <see cref="ClearText"/> per <c>Column&lt;StringId&gt;</c> the
    /// kind declares; abstract so a kind cannot forget one, exactly as <see cref="GrowColumns"/> is.</summary>
    protected abstract void ReleaseText(int slot);

    /// <summary>Give back every string the WHOLE table owns — the identity text and each row's columns — and empty
    /// both indexes. Called when a scope is retired (<see cref="Entities.Switch"/>): the old table set is garbage in
    /// one piece, but the interner is process-wide and would otherwise keep every string the dead scope interned
    /// (doc §4.4). The engine's reader quarantine (<c>StringTable.Tick</c>, 16 frames) is what makes this safe to do
    /// while the last frame that used those ids is still in flight.</summary>
    public void ReleaseAllText()
    {
        for (int slot = 1; slot < Count; slot++)
        {
            ref EntityId cell = ref Id[slot];
            if (cell.Form == EntityForm.Text) Entities.Strings.Release(cell.TextId);
            cell = default;
            ReleaseText(slot);
        }
        _byText.Clear();
        Array.Clear(_index);
        _indexed = 0;
    }

    // ── capacity ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Grow every column to at least <paramref name="capacity"/>. Not virtual: it grows the bookkeeping
    /// columns and then calls the kind's <see cref="GrowColumns"/>, so a kind can never forget one of these. It also
    /// presizes the gid index, which is what turns a warm read of a known row count into one allocation instead of a
    /// doubling chain (doc §4.2).</summary>
    public void EnsureCapacity(int capacity)
    {
        if (capacity <= Version.Capacity) return;
        Version.EnsureCapacity(capacity);
        Known.EnsureCapacity(capacity);
        Authority.EnsureCapacity(capacity);
        FetchedAt.EnsureCapacity(capacity);
        Touched.EnsureCapacity(capacity);
        Inflight.EnsureCapacity(capacity);
        Id.EnsureCapacity(capacity);
        // Load ≤ 0.75 → capacity * 4/3, rounded up to a power of two (the mask is the whole probe's arithmetic).
        int buckets = 32;
        while (buckets * 3 < capacity * 4) buckets <<= 1;
        if (buckets > _index.Length) GrowIndex(buckets);
        GrowColumns(capacity);
    }

    /// <summary>Grow the kind's OWN columns. One line per column; the base has already grown the bookkeeping.</summary>
    protected abstract void GrowColumns(int capacity);
}

/// <summary>The comparer behind a table's TEXT-form index. Keys are 4-byte <see cref="StringId"/>s, and the alternate
/// lookup lets a caller probe with a <c>ReadOnlySpan&lt;char&gt;</c> straight off the wire, allocating only when the
/// uri is genuinely new (P6, P14 — the same mechanism the engine's own StringTable uses for its span probe).
///
/// <para>It hashes RESOLVED TEXT, which is what the span probe's contract requires and what cost 52 ns a probe when
/// every row went through here (doc §4.1). Since 2026-09-12 only the text-form population does — the catalog rows go
/// through <see cref="Table"/>'s <c>int[]</c> index at 3.0 ns — so the expensive comparer now serves the small
/// population that genuinely needs a content probe.</para></summary>
public sealed class UriKeys : IEqualityComparer<StringId>, IAlternateEqualityComparer<ReadOnlySpan<char>, StringId>
{
    public static readonly UriKeys Instance = new();
    UriKeys() { }

    public bool Equals(StringId a, StringId b) => a.Value == b.Value;
    public int GetHashCode(StringId id) => string.GetHashCode(Entities.Strings.Resolve(id));

    public bool Equals(ReadOnlySpan<char> alternate, StringId other) => alternate.SequenceEqual(Entities.Strings.Resolve(other));
    public int GetHashCode(ReadOnlySpan<char> alternate) => string.GetHashCode(alternate);
    /// <summary>Only called when the lookup ADDS: interning here is what makes the probe path allocation-free.</summary>
    public StringId Create(ReadOnlySpan<char> alternate) => Entities.Strings.Intern(alternate);
}

// ── 6. scope: the table set ──────────────────────────────────────────────────────────────────────────────────────────

/// <summary>What a table set is keyed by (D9/§5.5, kept as 0.2.9 spelled it). Locale, market, tier and the explicit
/// filter all change what the provider ANSWERS for the same uri, so they cannot share rows; the cheapest correct model
/// is one table set per scope and a swap on change — the hot path then never carries a scope at all.</summary>
public readonly record struct CatalogScope(string Provider, string Account, string Locale, string Market, byte Tier, bool AllowExplicit)
{
    /// <summary>The offline demo scope (<c>--fake</c>): no account, nothing persisted under a real one.</summary>
    public static CatalogScope Fake(string locale = "en-US", string market = "US") => new("fake", "", locale, market, 0, true);
}

/// <summary>The table set for ONE scope (D9). Switching locale, market or account replaces this whole object: the old
/// set is garbage in ONE piece (P5 — one free, not N), and every late answer for it is dropped by its epoch (C7).
///
/// <para>Partial by design: a later wave's file (Home, Search, Recents, Concerts…) adds its own synthetic-subject
/// bookkeeping here without editing this file. The eight entity tables below are the fixed core.</para></summary>
public sealed partial class Scope
{
    public readonly CatalogScope Key;

    public readonly TrackTable Tracks = new();
    public readonly AlbumTable Albums = new();
    public readonly ArtistTable Artists = new();
    public readonly PlaylistTable Playlists = new();
    public readonly ShowTable Shows = new();
    public readonly EpisodeTable Episodes = new();
    public readonly UserTable Users = new();
    public readonly ConcertTable Concerts = new();
    public readonly Edges Edges = new();

    /// <summary>Every entity table, for the store's warm/trim walk. Edge tables are reached through <see cref="Edges"/>.</summary>
    public readonly Table[] Tables;

    /// <summary>Bumps on every switch. A request stamps it into <see cref="Table.Inflight"/>; a commit for an older
    /// epoch is dropped instead of writing into a table nobody is looking at (C7).</summary>
    public uint Epoch;

    /// <summary>The signed-in account's own row — the parent slot of every library edge (G6: the library IS edges).
    /// <see cref="Table.None"/> until <see cref="Entities.Boot"/> resolves the account uri.</summary>
    public int MeSlot;

    public Scope(CatalogScope key)
    {
        Key = key;
        Tables = [Tracks, Albums, Artists, Playlists, Shows, Episodes, Users, Concerts];
    }

    /// <summary>Fire every dirty table's <c>Changed</c> once (ch 31 calls it at the end of the seed). Same publication
    /// as <see cref="Entities.Publish"/> — there is only one.</summary>
    public void PublishAll() => Entities.Publish();

    /// <summary>Give every string this table set owns back to the interner. Called when the scope is RETIRED
    /// (<see cref="Entities.Switch"/> / a re-<see cref="Entities.Boot"/>): the tables themselves are garbage in one
    /// piece (P5), but the engine's <c>StringTable</c> is process-wide, and an id nobody releases is permanent
    /// (defect 1, doc §4.4) — so a market switch would otherwise raise the app's floor by a whole catalog's text
    /// every time. Edge-owned text (a payload <c>StringId</c>, a merch row) is its owner's to release the same way.</summary>
    public void ReleaseText()
    {
        for (int i = 0; i < Tables.Length; i++) Tables[i].ReleaseAllText();
    }
}

// ── 7. staging: what a worker hands the UI thread ────────────────────────────────────────────────────────────────────

/// <summary>A slice of a <see cref="Staging"/>'s text arena: where a decoded string sits, as UTF-8, until the drain
/// interns it.
///
/// <para><b>Why not a <see cref="StringId"/> straight out of the decoder?</b> Because interning is a WRITE to the
/// engine's <see cref="StringTable"/>, and that table has exactly one writer — the UI thread (§5.6, C1). A decode runs
/// on the socket's own thread (C10), so it cannot intern. It copies the bytes into the staging arena instead and the
/// commit interns them, which is also precisely P14: UTF-8 straight to a <see cref="StringId"/> with no
/// <c>Encoding.UTF8.GetString</c> temporary anywhere in between.</para></summary>
public readonly record struct TextRef(int Offset, int Length)
{
    public bool IsEmpty => Length == 0;
}

/// <summary>A STAGED IDENTITY: what a decoder knows about which row it is talking about, in the one form it is allowed
/// to hold it. Either the packed <see cref="EntityId"/> — the six catalog kinds arrive as 16 raw gid bytes and
/// <see cref="EntityId.ForGid(EntityKind,ReadOnlySpan{byte},EntityIdFlags)"/> is pure and thread-safe — or the uri's
/// UTF-8 in the staging arena, for everything the wire spells as text (a local file, a module playable, a user, a
/// section, a pathfinder node's <c>uri</c>).
///
/// <para><b>Why both, and why one field.</b> A decoder may not intern (C1), so it cannot build the TEXT form of an
/// <see cref="EntityId"/> — <see cref="EntityId.ForText"/> needs a <see cref="StringId"/>. But it CAN build the gid
/// form, and until 2026-09-12 it did not: every catalog identity was formatted into the arena as a uri string and
/// parsed straight back into the same packed id at commit (one 81 ns encode + one 37-57 ns parse per staged row, for
/// nothing). This union deletes that round trip without splitting every staged row into two parallel fields — one
/// <c>Id</c> per row, resolved by one call, <see cref="Staging.Slot(Table,in StagedId)"/>.</para></summary>
public readonly struct StagedId
{
    /// <summary>The packed identity, when the wire gave a gid. <c>default</c> otherwise.</summary>
    public readonly EntityId Packed;
    /// <summary>The uri's bytes in the staging arena, when the wire gave text. <c>default</c> otherwise.</summary>
    public readonly TextRef Text;

    public StagedId(EntityId packed) { Packed = packed; Text = default; }
    public StagedId(TextRef text) { Packed = default; Text = text; }

    /// <summary>Nothing was identified — the row is not a row and the commit drops it.</summary>
    public bool IsEmpty => Packed.IsEmpty && Text.IsEmpty;

    /// <summary>Which table this identity's row lives in, without resolving a slot. The packed form answers from a
    /// field; the text form walks the uri (allocation-free, thread-safe).</summary>
    public EntityKind Kind(Staging s) => Packed.IsEmpty ? EntityUri.KindOf(s.Utf8(Text)) : Packed.Kind;

    /// <summary>A gid identity, straight off the wire: no text, no base62, no intern (P14).</summary>
    public static implicit operator StagedId(EntityId packed) => new(packed);
    /// <summary>A uri identity: bytes already copied into the arena by <see cref="Staging.AddText"/>.</summary>
    public static implicit operator StagedId(TextRef text) => new(text);
}

/// <summary>A growable list of staged rows of one kind. Reused across batches (the buffer is pooled with its
/// <see cref="Staging"/>), so a steady stream of answers allocates nothing after warm-up (P8).</summary>
public abstract class StagedList
{
    public int Count;
    /// <summary>Drop the rows but keep the capacity — the whole point of pooling the buffer.</summary>
    public abstract void Clear();

    /// <summary>Undo the last <c>Add</c>. A row is appended BY REFERENCE and filled in place, so "this turned out not
    /// to be a row" can only be said afterwards; <see cref="Count"/> is the cursor the commit reads to, so dropping one
    /// is a decrement.</summary>
    public void Drop() { if (Count > 0) Count--; }

    /// <summary>Roll back to a mark — a decoder that gave up halfway must not leave half a batch behind.</summary>
    public void Rewind(int to) { if (to >= 0 && to < Count) Count = to; }
}

/// <summary>The three fields EVERY staged row has — its identity, who is speaking and which groups it speaks for — as
/// the one thing a generic helper can ask of it. Implemented by a two-line method rather than by properties because the
/// rows carry FIELDS (a field does not implement a property, and a property would have to shadow it); a constrained
/// generic call on a struct type parameter is devirtualized, so there is no boxing and no interface dispatch (P9).</summary>
public interface IStagedRow
{
    /// <summary>Fill the three: <c>Id = id; Authority = authority; Known = known;</c>.</summary>
    void Init(in StagedId id, Authority authority, uint known);
    /// <summary>The row's identity — what <see cref="StagedRows.Settle{T}"/> reads to decide whether it stands.</summary>
    StagedId Identity { get; }
}

/// <summary>The staged-row prologue and epilogue, once. Every decoder opened a row with three lines
/// (<c>Add</c>, <c>Authority =</c>, <c>Known =</c>) and closed it with two more (<c>if (row.Uri.IsEmpty) Drop()</c>),
/// at ~25 sites, and each of those sites could forget one of the five.</summary>
public static class StagedRows
{
    /// <summary>Open a staged row for <paramref name="id"/>: append it, stamp the authority and the groups it speaks
    /// for, and hand it back BY REFERENCE so the decoder fills the rest in place (P8).</summary>
    public static ref T RowFor<T>(this StagedList<T> list, in StagedId id, Authority authority, uint known)
        where T : unmanaged, IStagedRow
    {
        ref T row = ref list.Add();
        row.Init(in id, authority, known);
        return ref row;
    }

    /// <summary>Close the row just opened: keep it, or drop it when the wire never identified it. Answers whether it
    /// stands, so a decoder's epilogue is <c>if (!s.Tracks.Settle()) { run.Discard(); return; }</c>.</summary>
    public static bool Settle<T>(this StagedList<T> list) where T : unmanaged, IStagedRow
    {
        if (list.Count > 0 && !list[list.Count - 1].Identity.IsEmpty) return true;
        list.Drop();
        return false;
    }
}

/// <inheritdoc cref="StagedList"/>
public sealed class StagedList<T> : StagedList where T : unmanaged
{
    T[] _a = new T[16];

    /// <summary>Append an empty row and return it BY REFERENCE, so the decoder fills fields in place rather than
    /// building a value and copying it (P8).</summary>
    public ref T Add()
    {
        if (Count == _a.Length) Array.Resize(ref _a, _a.Length * 2);
        ref T row = ref _a[Count++];
        row = default;
        return ref row;
    }

    public ref T this[int i] => ref _a[i];
    /// <summary>The staged rows, for the commit's copy loop.</summary>
    public Span<T> Span => _a.AsSpan(0, Count);
    public override void Clear() => Count = 0;
}

/// <summary>What a worker thread hands the UI drain (C10): decoded rows and their text, in columns, with nothing
/// interned and nothing written to a live table yet. The decode is the expensive part and it happens off the UI thread;
/// the commit is a copy.
///
/// <para>Partial by design: each kind's file declares its own staged-row struct and its lazy list here
/// (<c>StagedList&lt;StagedTrack&gt; Tracks</c>), so adding a kind does not touch this file. Register the list through
/// <see cref="Register{T}"/> and <see cref="Reset"/> keeps working.</para></summary>
public sealed partial class Staging
{
    static readonly Stack<Staging> s_pool = new();

    readonly List<StagedList> _lists = new();
    byte[] _text = new byte[4096];
    int _textLength;

    // The credit-line joiner's scratch (ch 01 GAP 2): a row's artist names are joined ONCE, here, and interned once at
    // commit, because `TrackTable.ArtistLine` exists precisely so a 200-row page does not join names per frame. It is
    // addressed by a MARK rather than cleared, because the pathfinder's node reader NESTS — a track's `albumOfTrack` is
    // decoded in the middle of the track's own artist walk, and a joiner that reset at the top would lose the names
    // collected so far.
    byte[] _credit = new byte[192];
    int _creditLength;

    /// <summary>The scope epoch this batch was requested for. The commit drops it if the scope has moved on (C7).</summary>
    public uint Epoch;

    /// <summary>How authoritative this whole batch is (D16); a decoder may lower it per row.</summary>
    public Authority Authority = Wavee.Authority.Full;

    /// <summary>Take a staging buffer. Pooled: a steady stream of wire answers reuses the same arenas (P8).</summary>
    public static Staging Rent()
    {
        lock (s_pool) { if (s_pool.Count > 0) return s_pool.Pop(); }
        return new Staging();
    }

    /// <summary>Give it back AFTER the commit has copied out of it. Never hold a <see cref="TextRef"/> past this.</summary>
    public static void Return(Staging s)
    {
        s.Reset();
        lock (s_pool) { if (s_pool.Count < 8) s_pool.Push(s); }
    }

    /// <summary>Register a kind's list so <see cref="Reset"/> clears it. Kind files call this from their lazy property.</summary>
    public T Register<T>(T list) where T : StagedList { _lists.Add(list); return list; }

    public void Reset()
    {
        for (int i = 0; i < _lists.Count; i++) _lists[i].Clear();
        _textLength = 0;
        _creditLength = 0;
        Epoch = 0;
        Authority = Wavee.Authority.Full;
    }

    /// <summary>Copy decoded UTF-8 into the arena and return where it landed. The wire buffer is recycled the moment the
    /// decode returns, so the bytes have to be copied — one memcpy of a title, not an allocation.</summary>
    public TextRef AddText(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return default;
        if (_textLength + utf8.Length > _text.Length)
            Array.Resize(ref _text, Math.Max(_textLength + utf8.Length, _text.Length * 2));
        utf8.CopyTo(_text.AsSpan(_textLength));
        var r = new TextRef(_textLength, utf8.Length);
        _textLength += utf8.Length;
        return r;
    }

    /// <summary>The bytes behind a <see cref="TextRef"/>.</summary>
    public ReadOnlySpan<byte> Utf8(TextRef r) => _text.AsSpan(r.Offset, r.Length);

    /// <summary>Where the arena is now. Paired with <see cref="RewindText"/> by a decoder that has to ABANDON a walk
    /// and start it again (the recents page, whose item count is not stated until the page has been read): without it
    /// a retry leaves the first attempt's whole text behind in the arena.</summary>
    internal int TextMark => _textLength;

    /// <inheritdoc cref="TextMark"/>
    public void RewindText(int mark) { if (mark >= 0 && mark <= _textLength) _textLength = mark; }

    /// <summary>Intern a staged string. UI thread only — this is the commit's job, not the decoder's.</summary>
    public StringId Intern(TextRef r) => r.IsEmpty ? StringId.Empty : Entities.Intern(Utf8(r));

    /// <summary>THE staged identity → slot resolution, and the only one: every kind's commit and every staged edge
    /// goes through it. A packed id probes the table's open-addressed index (3.0 ns); a text uri takes
    /// <c>Table.Slot(ReadOnlySpan&lt;byte&gt;)</c>, which parses a catalog uri back to the gid form and falls to the
    /// text index otherwise. Allocates the row when it has never been seen — which is how an album's tracklist becomes
    /// bindable before any track is fetched.
    /// <para><paramref name="table"/> <c>null</c> means "take the identity's own kind" — the mixed-kind case a search
    /// facet or a home band is. <see cref="Table.None"/> comes back for an empty identity and for a kind with no table
    /// (an unrecognised uri, a section inside a list of entities). UI thread only (C1).</para></summary>
    public int Slot(Table? table, in StagedId id)
    {
        if (id.IsEmpty) return Table.None;
        table ??= Entities.TableFor(id.Kind(this));
        if (table is null) return Table.None;
        return id.Packed.IsEmpty ? table.Slot(Utf8(id.Text)) : table.Slot(id.Packed);
    }

    /// <inheritdoc cref="Slot(Table,in StagedId)"/>
    public int Slot(in StagedId id) => Slot(null, in id);

    // ── the credit-line joiner (ch 01 GAP 2) ────────────────────────────────────────────────────────────────────────

    /// <summary>Where this row's credit line starts. Take it before decoding a row, pass it to every
    /// <see cref="AppendCredit"/> and to <see cref="TakeCredit"/>, and a nested row's names cannot touch it.</summary>
    public int CreditMark => _creditLength;

    /// <summary>Start a credit line at the bottom of the joiner (the protobuf path, which never nests).</summary>
    public void ClearCredit() => _creditLength = 0;

    /// <summary>Append one artist to the credit line, with the separator between (never before the first).</summary>
    public void AppendCredit(ReadOnlySpan<byte> name, int mark = 0)
    {
        if (name.IsEmpty) return;
        int need = _creditLength + name.Length + 2;
        if (need > _credit.Length) Array.Resize(ref _credit, Math.Max(need, _credit.Length * 2));
        if (_creditLength > mark)
        {
            _credit[_creditLength++] = (byte)',';
            _credit[_creditLength++] = (byte)' ';
        }
        name.CopyTo(_credit.AsSpan(_creditLength));
        _creditLength += name.Length;
    }

    /// <summary>Copy the finished credit line into the text arena and pop the joiner back to <paramref name="mark"/>.
    /// Empty in, empty out — a row with no artists gets no line rather than an empty interned string.</summary>
    public TextRef TakeCredit(int mark = 0)
    {
        var r = _creditLength <= mark ? default : AddText(_credit.AsSpan(mark, _creditLength - mark));
        _creditLength = mark;
        return r;
    }
}

// ── 8. the statics ───────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The graph's front door: the interner, the current scope, the factories, the batch <c>Ensure</c>, and the
/// commit/publish pair the UI drain runs. Partial: each kind's file adds its own typed sugar and its commit hook.</summary>
public static partial class Entities
{
    static StringTable? s_strings;
    static readonly List<Publishable> s_dirty = new(64);

    /// <summary>THE interner — the engine's <see cref="StringTable"/> (§5.6): one table for the app and the paint path,
    /// so a title reaches DirectWrite as an id the shaper already has cached and no string this layer made is copied
    /// into a draw list.
    /// <para>Wave 0's <c>App.cs</c> calls <see cref="UseStringTable"/> with the host's table before <see cref="Boot"/>.
    /// Until then (unit tests, a probe) this property mints a private one, so nothing here needs a running engine
    /// (D17).</para></summary>
    public static StringTable Strings => s_strings ??= new StringTable();

    /// <summary>Adopt the host's interner. Once, before <see cref="Boot"/>.</summary>
    public static void UseStringTable(StringTable strings) => s_strings = strings;

    /// <summary>Seconds since the app epoch, published ONCE per drain by the host (P7, P10). Core never reads a clock:
    /// a freshness stamp on 300 committed rows must be 300 column writes, not 300 syscalls.</summary>
    public static int Now;

    /// <summary>The active table set (D9). Replaced whole by <see cref="Switch"/>.</summary>
    public static Scope Current { get; private set; } = null!;

    /// <summary>Bumped once per drain that changed anything (C3). Every table that changed in that drain publishes this
    /// same number, so a page can answer "did anything at all move since I painted?" with one comparison.</summary>
    public static uint Publication { get; private set; }

    // ── boot / switch ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Open the graph for a scope. The store and the planner are optional (the partial hooks below are erased
    /// when their files are absent), so <c>--fake</c> and the unit tests boot a pure in-memory graph.</summary>
    public static void Boot(CatalogScope key)
    {
        Scope? previous = Current;                         // null on the first Boot; `Current` is `null!` until then
        s_dirty.Clear();
        previous?.ReleaseText();                           // a re-Boot retires the old set's interned text (defect 1)
        Current = new Scope(key);
        StoreBoot();
        FetchBoot();
        ResolveMe(Current);
        StoreWarm(Current);
    }

    /// <summary>Locale, market, tier, explicit filter or account changed (D9): build a new set, bump the epoch so every
    /// answer still in flight for the old one is dropped on arrival (C7), and let the old set go in one piece.</summary>
    public static void Switch(CatalogScope key)
    {
        uint epoch = Current.Epoch + 1;                    // Boot runs first, always (App.cs §3.5)
        s_dirty.Clear();
        // The old set is dropped whole — but its rows OWN interned strings, and the interner outlives the scope. Hand
        // them back before the tables go, or a locale/market/account switch raises the floor for the life of the
        // process (defect 1, doc §4.4). Safe mid-frame: the engine quarantines a released slot for 16 ticks.
        Current.ReleaseText();
        Current = new Scope(key) { Epoch = epoch };
        ResolveMe(Current);
        StoreWarm(Current);
    }

    static void ResolveMe(Scope scope)
        => scope.MeSlot = scope.Key.Account.Length == 0 ? Table.None : scope.Users.Slot(scope.Key.Account.AsSpan());

    // The SHELL seams. Implemented by Entities/Store.cs and Entities/Fetch.cs as parts of THIS partial class; an
    // unimplemented partial method is erased by the compiler, so this file — and every test over it — stands alone.
    static partial void StoreBoot();
    static partial void StoreWarm(Scope scope);
    static partial void FetchBoot();
    static partial void PlanFetch(Table table, ReadOnlySpan<int> slots, uint wanted, FetchPriority priority);

    // ── text ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Intern UTF-8 straight off the wire (P14). A direct forward to the engine's own
    /// <c>StringTable.Intern(ReadOnlySpan&lt;byte&gt;)</c> (plan §3.4), which probes the interner by span itself —
    /// text already in the table costs no allocation at all; only genuinely new text pays for one.</summary>
    public static StringId Intern(ReadOnlySpan<byte> utf8) => Strings.Intern(utf8);

    /// <summary>THE write to any <see cref="StringId"/> the graph OWNS: AddRef the incoming id, release the one it
    /// replaces, then store it. Two array increments; no allocation; a no-op when nothing changed.
    ///
    /// <para><b>Why it exists (defect 1, doc §4.4).</b> The engine's interner reclaims an id only when its last
    /// ownership reference is released, and a string that was never AddRef'd is PERMANENT
    /// (<c>StringTable.cs:26</c>). Before 2026-09-12 nothing in <c>Entities/</c> called <c>AddRef</c> at all, so a
    /// trim freed a row's 89 B of columns and leaked the 158 B of uri text plus its title, image and artist line —
    /// a scope's memory floor could only ever rise. The rule is: <b>if a column, a payload or a side-slab field holds
    /// a StringId, it was written through here and it is released through
    /// <see cref="ReleaseText(ref StringId)"/>.</b></para>
    ///
    /// <para>AddRef runs BEFORE Release deliberately: when the two ids happen to be the same string reached by two
    /// paths, releasing first could take the count to zero and reclaim an id the very next line stores.</para></summary>
    public static void RetainText(ref StringId cell, StringId value)
    {
        if (cell.Value == value.Value) return;
        Strings.AddRef(value);
        Strings.Release(cell);
        cell = value;
    }

    /// <summary>Give one owned <see cref="StringId"/> back and blank the cell — the other half of
    /// <see cref="RetainText"/>, and what a row's teardown is made of. Blanking matters: a recycled slot must never
    /// carry an id whose last reference it just dropped.</summary>
    public static void ReleaseText(ref StringId cell)
    {
        Strings.Release(cell);
        cell = StringId.Empty;
    }

    // ── the batch API (P4) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"These rows, these field groups, this urgency." The ONLY way to ask for data (P4): a page demands its
    /// whole model in one call and the planner decides sqlite vs network, dedupes against <c>Inflight</c> and batches by
    /// provider. There is no single-uri fetch to reach for; the one-row call is a one-element span.
    /// <para>Each kind's file adds the typed sugar over this — <c>Entities.Ensure(ReadOnlySpan&lt;Track&gt;,
    /// TrackFields)</c> — by reinterpreting the handle span as slots (a handle is one <c>int</c>; see
    /// <see cref="Slots{T}"/>).</para></summary>
    public static void Ensure(Table table, ReadOnlySpan<int> slots, uint wanted, FetchPriority priority = FetchPriority.Visible)
    {
        for (int i = 0; i < slots.Length; i++) table.Touched[slots[i]] = Now;
        PlanFetch(table, slots, wanted, priority);
    }

    /// <summary>Reinterpret a span of handles as a span of slots. A handle is a <c>readonly struct</c> over exactly one
    /// <c>int</c>, so this is a cast, not a copy — it is how a kind's typed <c>Ensure</c> forwards without allocating a
    /// temporary array (P8).</summary>
    public static ReadOnlySpan<int> Slots<T>(ReadOnlySpan<T> handles) where T : unmanaged
    {
        System.Diagnostics.Debug.Assert(sizeof(int) == System.Runtime.CompilerServices.Unsafe.SizeOf<T>(),
            "A handle must be exactly one int wide for the slot reinterpretation to be valid (D13).");
        return MemoryMarshal.Cast<T, int>(handles);
    }

    // ── the column sweep (P15) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Below this many rows a vector path measured SLOWER than the scalar loop on arm64 NEON (scalar ÷ simd =
    /// 0.92 at 1 element, ~1.0 at 3-5, and only 1.4+ from 16 up), so the gate is a length floor as well as
    /// <c>IsHardwareAccelerated</c>.</summary>
    public const int SimdFloor = 16;

    /// <summary>THE <c>wanted &amp; ~known</c> sweep (P15, §5.4): the slots in <c>[from, from+count)</c> that are missing
    /// any bit of <paramref name="wanted"/>, written into <paramref name="dst"/>; returns how many were written. Stops
    /// when <paramref name="dst"/> is full — the caller resumes at <c>dst[n-1] + 1</c>.
    ///
    /// <para>Vectorized because it is the one scan that runs over the WHOLE table on every page mount: a 10k-row library
    /// asked for <c>TrackFields.Row</c> is 10k lane loads, and the common answer is "this quad knows everything" — four
    /// rows skipped per compare. <c>Vector128</c> and not <c>Vector256</c> because arm64 is a shipping target and NEON's
    /// 128 bits are its ceiling; the scalar tail is always present and always correct, and <c>EntitiesTests</c> pins the
    /// two against each other on random data.</para></summary>
    public static int ScanMissing(ReadOnlySpan<uint> known, uint wanted, int from, int count, Span<int> dst)
    {
        if (wanted == 0 || count <= 0 || dst.IsEmpty) return 0;
        int n = 0, end = from + count, i = from;

        if (Vector128.IsHardwareAccelerated && count >= SimdFloor)
        {
            var want = Vector128.Create(wanted);
            ref uint origin = ref MemoryMarshal.GetReference(known);
            int lanes = Vector128<uint>.Count;
            for (int last = end - lanes; i <= last; i += lanes)
            {
                // want & ~known: any non-zero lane is a row that is missing something.
                var missing = Vector128.AndNot(want, Vector128.LoadUnsafe(ref origin, (nuint)i));
                if (missing == Vector128<uint>.Zero) continue;              // the common case: the whole quad is known
                for (int lane = 0; lane < lanes; lane++)
                {
                    if (missing[lane] == 0) continue;
                    if (n == dst.Length) return n;
                    dst[n++] = i + lane;
                }
            }
        }

        for (; i < end; i++)
        {
            if ((wanted & ~known[i]) == 0) continue;
            if (n == dst.Length) return n;
            dst[n++] = i;
        }
        return n;
    }

    /// <summary>The same question over an explicit slot list (a page's rows are rarely contiguous). Scalar by
    /// construction: this is a gather, and a 128-bit gather is not a thing arm64 does.</summary>
    public static int SelectMissing(ReadOnlySpan<int> slots, ReadOnlySpan<uint> known, uint wanted, Span<int> dst)
    {
        int n = 0;
        for (int i = 0; i < slots.Length && n < dst.Length; i++)
            if ((wanted & ~known[slots[i]]) != 0) dst[n++] = slots[i];
        return n;
    }

    // ── commit / publish (C1, C3) ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Copy a staged batch into the live columns. UI thread, inside the drain (C1/C10). Per kind it is the same
    /// shape every time — resolve the uri to a slot, ask <c>Table.Accepts</c>
    /// whether this authority may write the group, write the columns, call <see cref="Table.Applied"/> — and each kind's
    /// own file implements it through the hooks below, because only that file knows its columns.
    /// <para>A batch stamped for a scope that has since been replaced is dropped whole (C7).</para></summary>
    public static void Commit(Staging staging)
    {
        if (staging.Epoch != 0 && staging.Epoch != Current.Epoch) return;
        CommitTracks(staging);
        CommitEpisodes(staging);
        CommitAlbums(staging);
        CommitArtists(staging);
        CommitPlaylists(staging);
        CommitShows(staging);
        CommitUsers(staging);
        CommitConcerts(staging);
        // The synthetic subjects, before the edges that hang off them: a section run whose rows nobody filled would
        // allocate a slot and never paint (ch 10 §7).
        CommitHome(staging);
        CommitEdges(staging);
        // Recents is its own relation with its own payload (ch 16 §7.2) and rides after the edges for the same reason
        // the edges ride after the rows.
        CommitRecents(staging);
        // Not an entity at all: the dark gradings extension kind 179 carries, keyed by the IMAGE (Palette.cs).
        CommitGradings(staging);
    }

    // Implemented by each kind's file as part of this partial class. Unimplemented ones are erased.
    static partial void CommitTracks(Staging s);
    static partial void CommitEpisodes(Staging s);
    static partial void CommitAlbums(Staging s);
    static partial void CommitArtists(Staging s);
    static partial void CommitPlaylists(Staging s);
    static partial void CommitShows(Staging s);
    static partial void CommitUsers(Staging s);
    static partial void CommitConcerts(Staging s);
    static partial void CommitHome(Staging s);
    static partial void CommitEdges(Staging s);
    static partial void CommitRecents(Staging s);
    static partial void CommitGradings(Staging s);

    /// <summary>Enqueue a table that changed in this drain. Called by <see cref="Publishable.MarkDirty"/> only.</summary>
    internal static void EnqueueDirty(Publishable p) => s_dirty.Add(p);

    /// <summary>End of the drain: bump the publication counter once and fire the <c>Changed</c> signal of every table
    /// that changed, exactly once each (D8, C3). Returns the new publication, or the old one when nothing moved — a
    /// drain that changed nothing must not wake the frame loop.</summary>
    public static uint Publish()
    {
        if (s_dirty.Count == 0) return Publication;
        uint publication = ++Publication;
        for (int i = 0; i < s_dirty.Count; i++) s_dirty[i].PublishNow(publication);
        s_dirty.Clear();
        return publication;
    }

    /// <summary>How many tables are waiting to publish — a drain-length guardrail for the diagnostics page.</summary>
    public static int PendingPublications => s_dirty.Count;

    // ── factories (D10) ─────────────────────────────────────────────────────────────────────────────────────────────
    // The ONLY way to a handle. Allocating an empty row for an unseen uri is the point: the page gets a handle it can
    // bind immediately, `Knows` says nothing is filled, the skeleton renders, and `Ensure` fills it. No factory ever
    // touches sqlite or the network.

    public static Track Track(EntityId id) => new(Current.Tracks.Slot(id));
    public static Episode Episode(EntityId id) => new(Current.Episodes.Slot(id));
    public static Album Album(EntityId id) => new(Current.Albums.Slot(id));
    public static Artist Artist(EntityId id) => new(Current.Artists.Slot(id));
    public static Playlist Playlist(EntityId id) => new(Current.Playlists.Slot(id));
    public static Show Show(EntityId id) => new(Current.Shows.Slot(id));
    public static User User(EntityId id) => new(Current.Users.Slot(id));
    public static Concert Concert(EntityId id) => new(Current.Concerts.Slot(id));

    // The uri-shaped overloads, for a call site that just parsed one (a deep link, a test, a route). Identical cost —
    // `EntityUri` IS an `EntityId` (§2) — and kept so a caller does not have to reach through `.Id` to say the obvious.
    public static Track Track(EntityUri uri) => Track(uri.Id);
    public static Episode Episode(EntityUri uri) => Episode(uri.Id);
    public static Album Album(EntityUri uri) => Album(uri.Id);
    public static Artist Artist(EntityUri uri) => Artist(uri.Id);
    public static Playlist Playlist(EntityUri uri) => Playlist(uri.Id);
    public static Show Show(EntityUri uri) => Show(uri.Id);
    public static User User(EntityUri uri) => User(uri.Id);
    public static Concert Concert(EntityUri uri) => Concert(uri.Id);

    /// <summary>The table a uri's rows live in, or null for a kind with no table of its own
    /// (<see cref="EntityKind.Collection"/> is the user's liked edge, not a row).</summary>
    public static Table? TableFor(EntityKind kind) => kind switch
    {
        EntityKind.Track => Current.Tracks,
        EntityKind.Episode => Current.Episodes,
        EntityKind.Album => Current.Albums,
        EntityKind.Artist => Current.Artists,
        EntityKind.Playlist => Current.Playlists,
        EntityKind.Show => Current.Shows,
        EntityKind.User => Current.Users,
        EntityKind.Concert => Current.Concerts,
        _ => null,
    };

    /// <summary>The table an IDENTITY's row lives in — the cross-kind dispatch a mixed list needs (doc §3.1
    /// requirement 4). Null for a kind with no table of its own.</summary>
    public static Table? TableFor(EntityId id) => TableFor(id.Kind);

    /// <summary>Resolve a cross-kind identity to (kind, slot), allocating the row if it is unseen — what a search
    /// "All" hit, a queue row, a route subject or a pin turns into. <see cref="EntityRef.IsNone"/> when the kind has
    /// no table (an unrecognised uri, or <see cref="EntityKind.Collection"/>, which is an edge and not a row).</summary>
    public static EntityRef Ref(EntityId id)
    {
        var table = TableFor(id.Kind);
        return table is null ? default : new EntityRef(id.Kind, table.Slot(id));
    }
}
