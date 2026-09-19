// ── Entities/Show.cs — CORE (owner A, wave 1; plan §2 · the file's full budget is 180) ───────────────────────────────
//
// THE SHOW COLUMNS, FIELD GROUPS AND HANDLE. Ch 09 §7 is the reader; plan §4 declares no show columns, so the three
// text columns below are that chapter's "ShowTable.Publisher, Description, Image" gap row.
//
// The show page is the shared detail frame with EPISODES where the tracks go, so this table is deliberately thin: the
// episode SET is `Edges.ShowEpisodes` (parent = the show slot), its count is that edge's `Total`, and the load-more
// gate is a CURSOR — see `EpisodesAsked` for the one column here that is not a chapter field but a chapter DEFECT FIX.
//
// PODCAST REWORK, wave P1 (owner A; docs/plans/wavee/podcast-show-rework-implementation.md §5.1): two more groups, each
// under its own authority — `Facts` (what `ShowV4` carries beside the identity: flags, consumption order, trailer) and
// `Rating` (the pathfinder-only facts: stars, the listener's own rating, the palette tone, exclusive). Both write the
// ONE `Flags` column, each only its own mask (the `ArtistFlags` precedent), so neither answer can clear the other's bits.

using FluentGpu.Foundation;

namespace Wavee;

/// <summary>Which of a show's columns are filled (ch 09 §7).</summary>
[Flags]
public enum ShowFields : uint
{
    None = 0,

    Title = 1 << 0,
    Image = 1 << 1,
    /// <summary>The publisher line. 0.2.9 builds that line and never renders it — a defect ch 09 §9 says to FIX, not
    /// to port, so the column exists and the meta line states it.</summary>
    Publisher = 1 << 2,
    Identity = Title | Image | Publisher,

    /// <summary>The rail description, which fades in late and must never hold the page (ch 09 §7).</summary>
    About = 1 << 8,

    /// <summary>What <c>ShowV4</c> also carries: the <see cref="ShowFlags.FactsMask"/> bits, the
    /// <see cref="ConsumptionOrder"/> and the trailer uri. One group, one authority — one answer fills all, and an
    /// ABSENT field is a real "false" (proto2 omits a false bool), so the decoder claims the group whole.</summary>
    Facts = 1 << 9,
    /// <summary>The pathfinder-only facts: the average and its count, the listener's own stars, the palette tone and the
    /// <see cref="ShowFlags.RatingMask"/> bits. Its own group because its own ROUTE (<c>queryShowMetadataV2</c>, wave P4):
    /// a dead hash seals this group alone while <c>ShowV4</c> still serves the rest (the <c>ArtistFields.Chart</c>
    /// precedent). Readiness: absent until known — the tone falls back to the cover palette (plan §6.1).</summary>
    Rating = 1 << 10,

    Appearance = 1 << 11,
    Topics = 1 << 12,
    Html = 1 << 13,
    All = Identity | About | Facts | Rating | Appearance | Topics | Html,
}

/// <summary>Show booleans as bits (P3). TWO groups write the one column, each only its own mask, so a <c>ShowV4</c>
/// answer landing after the pathfinder one cannot clear <see cref="Exclusive"/>, nor the reverse.</summary>
[Flags]
public enum ShowFlags : uint
{
    None = 0,
    /// <summary><c>Show.explicit</c> (metadata.proto field 68).</summary>
    Explicit = 1 << 0,
    /// <summary><c>media_type == VIDEO</c> (field 74).</summary>
    Video = 1 << 1,
    /// <summary><c>media_type == MIXED</c> — set only when the wire STATES it; an absent field is neither.</summary>
    Mixed = 1 << 2,
    /// <summary>A platform exclusive (pathfinder <c>showTypes[] ∋ *EXCLUSIVE*</c>, wave P4).</summary>
    Exclusive = 1 << 3,
    /// <summary><c>music_and_talk</c> (field 85).</summary>
    MusicAndTalk = 1 << 4,
    /// <summary>The account may rate this show (pathfinder <c>rating.canRate</c>, wave P4).</summary>
    CanRate = 1 << 5,

    /// <summary>The bits <see cref="ShowFields.Facts"/> owns.</summary>
    FactsMask = Explicit | Video | Mixed | MusicAndTalk,
    /// <summary>The bits <see cref="ShowFields.Rating"/> owns.</summary>
    RatingMask = Exclusive | CanRate,
}

/// <summary>How a show means to be heard (<c>Show.consumption_order</c>, field 75). The numbers ARE the wire's —
/// metadata.proto numbers the enum from 1 (SEQUENTIAL) — so the decoder stores what came and 0 is "the show did not
/// say". <c>Show.Rules</c> (listen-next, the ledger, neighbours) reads it: a serial walks forward from the newest
/// finished episode, everything else takes the newest unplayed.</summary>
public enum ConsumptionOrder : byte { Unknown = 0, Sequential = 1, Episodic = 2, Recent = 3 }

/// <summary>Every show in one scope, as columns.</summary>
public sealed class ShowTable : Table
{
    public Column<StringId> Title, Image, Publisher, Description, ListRevision, Topics, HtmlDescription;

    /// <summary>THE PAGING CURSOR: how far into the episode membership the source has already ASKED — advanced whether
    /// or not the page came back with rows, so a withdrawn or region-locked member cannot pin the "Load more" pill on
    /// screen forever (ch 09 DATA GAPS, "the paging CURSOR"; 0.2.9's <c>Show.PagedThrough</c>).
    ///
    /// <para><b>This column is a stand-in and is meant to be deleted.</b> Ch 09 asks for
    /// <c>EdgeTable&lt;T&gt;.Asked</c> — the cursor is a property of the MEMBERSHIP LIST, not of the show, and every
    /// paged edge has the same problem. <c>Edges.cs</c> (owner B) has no such column today, so the show page gets a
    /// correct gate now from here; when the edge grows one, this column and its field bit go and the reader moves.
    /// The gate itself is <c>max(edgeAsked, localAsked) &lt; total</c> either way (ch 09 §8's <c>Episode.Rules</c>).</para></summary>
    public Column<int> EpisodesAsked;

    // ── facts (ShowFields.Facts) ──
    /// <summary><see cref="ShowFlags"/>; Facts writes <see cref="ShowFlags.FactsMask"/>, Rating <see cref="ShowFlags.RatingMask"/>.</summary>
    public Column<uint> Flags;
    /// <summary><see cref="ConsumptionOrder"/> as a byte.</summary>
    public Column<byte> Order;
    /// <summary>The trailer episode's uri (<c>trailer_uri</c>, field 83) — TEXT, not a slot: naming a trailer must not
    /// allocate an episode row per show visited; the door that plays it resolves it on the click.</summary>
    public Column<StringId> Trailer;

    // ── rating (ShowFields.Rating) ──
    /// <summary>The average × 100 (4.8 → 480); 0 = the provider shows none (<c>showAverage</c> false).</summary>
    public Column<ushort> RatingX100;
    public Column<int> RatingCount;
    /// <summary>The listener's own stars, 1-5; 0 = not rated.</summary>
    public Column<byte> MyRating;
    /// <summary>The provider's tone, 0xAARRGGBB (<c>backgroundTintedBase</c>, never <c>textBrightAccent</c> — plan D-7);
    /// 0 = none, and the page falls back to the cover palette.</summary>
    public Column<uint> Tone;

    // ── authority, per column GROUP (D16) ──
    public Column<byte> IdentityAuthority, AboutAuthority, FactsAuthority, RatingAuthority, AppearanceAuthority, TopicsAuthority, HtmlAuthority;

    public override EntityKind Kind => EntityKind.Show;

    protected override void GrowColumns(int capacity)
    {
        ListRevision.EnsureCapacity(capacity);
        Topics.EnsureCapacity(capacity);
        HtmlDescription.EnsureCapacity(capacity);
        AppearanceAuthority.EnsureCapacity(capacity);
        TopicsAuthority.EnsureCapacity(capacity);
        HtmlAuthority.EnsureCapacity(capacity);
        Title.EnsureCapacity(capacity);
        Image.EnsureCapacity(capacity);
        Publisher.EnsureCapacity(capacity);
        Description.EnsureCapacity(capacity);
        EpisodesAsked.EnsureCapacity(capacity);
        Flags.EnsureCapacity(capacity);
        Order.EnsureCapacity(capacity);
        Trailer.EnsureCapacity(capacity);
        RatingX100.EnsureCapacity(capacity);
        RatingCount.EnsureCapacity(capacity);
        MyRating.EnsureCapacity(capacity);
        Tone.EnsureCapacity(capacity);
        IdentityAuthority.EnsureCapacity(capacity);
        AboutAuthority.EnsureCapacity(capacity);
        FactsAuthority.EnsureCapacity(capacity);
        RatingAuthority.EnsureCapacity(capacity);
    }

    /// <summary>Give back every string a show row owns (defect 1; the REF-COUNTING block on <see cref="Table"/>). One
    /// line per <c>Column&lt;StringId&gt;</c> above, and the pair to the <see cref="Table.SetText"/> the commit writes
    /// through — a show's rail description is the longest single string this table holds, and before this override a
    /// trim reclaimed the row's columns and none of it (doc §4.4).</summary>
    protected override void ReleaseText(int slot)
    {
        ClearText(ref ListRevision, slot);
        ClearText(ref Topics, slot);
        ClearText(ref HtmlDescription, slot);
        ClearText(ref Title, slot);
        ClearText(ref Image, slot);
        ClearText(ref Publisher, slot);
        ClearText(ref Description, slot);
        ClearText(ref Trailer, slot);
    }
}

/// <summary>A show: one <c>int</c>. Partial because <c>Show.UI.cs</c> and <c>Show.Page.cs</c> (Wave 5) add the show arm
/// of the shared frame.</summary>
public readonly partial struct Show(int slot) : IEquatable<Show>
{
    static ShowTable T => Entities.Current.Shows;

    public int Slot { get; } = slot;
    public bool IsValid => Slot > Table.None && Slot < T.Count;
    public uint Version => T.Version[Slot];
    public bool Knows(ShowFields fields) => (T.Known[Slot] & (uint)fields) == (uint)fields;

    /// <inheritdoc cref="Track.Id"/>
    public EntityId Id => T.Id[Slot];
    /// <inheritdoc cref="Track.Uri"/>
    public EntityUri Uri => new(T.Id[Slot]);

    public StringId TitleId => T.Title[Slot];
    public string Title => Entities.Strings.Resolve(T.Title[Slot]);
    public StringId ImageId => T.Image[Slot];
    public StringId PublisherId => T.Publisher[Slot];
    public StringId DescriptionId => T.Description[Slot];
    public StringId TopicsId => T.Topics[Slot];
    public StringId HtmlDescriptionId => T.HtmlDescription[Slot];

    /// <summary>Both groups' bits (<see cref="ShowFields.Facts"/>, <see cref="ShowFields.Rating"/>).</summary>
    public ShowFlags Flags => (ShowFlags)T.Flags[Slot];
    public ConsumptionOrder Order => (ConsumptionOrder)T.Order[Slot];
    /// <inheritdoc cref="ShowTable.Trailer"/>
    public StringId TrailerId => T.Trailer[Slot];
    /// <inheritdoc cref="ShowTable.RatingX100"/>
    public int RatingX100 => T.RatingX100[Slot];
    public int RatingCount => T.RatingCount[Slot];
    /// <inheritdoc cref="ShowTable.MyRating"/>
    public int MyRating => T.MyRating[Slot];
    /// <inheritdoc cref="ShowTable.Tone"/>
    public uint Tone => T.Tone[Slot];

    /// <summary>The resident episodes, in the provider's order.</summary>
    public ReadOnlySpan<int> EpisodeSlots => Entities.Current.Edges.ShowEpisodes.Targets(Slot);
    /// <summary>How many episodes the membership baseline has, resident or not — the "N episodes" line and the
    /// shimmer count while the list is partial.</summary>
    public int TotalEpisodes => Entities.Current.Edges.ShowEpisodes.Total(Slot);
    /// <inheritdoc cref="ShowTable.EpisodesAsked"/>
    public int EpisodesAsked => T.EpisodesAsked[Slot];

    public bool Equals(Show other) => other.Slot == Slot;
    public override bool Equals(object? obj) => obj is Show other && other.Slot == Slot;
    public override int GetHashCode() => Slot;
    public static bool operator ==(Show a, Show b) => a.Slot == b.Slot;
    public static bool operator !=(Show a, Show b) => a.Slot != b.Slot;
}

// ── staging and the commit ───────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One decoded show.</summary>
public struct StagedShow : IStagedRow
{
    /// <summary>THE row's identity as the wire gave it (<see cref="StagedId"/>): the packed <see cref="EntityId"/> when
    /// it arrived as 16 gid bytes, the uri's UTF-8 in the arena when it arrived as text. One field, one resolve —
    /// <c>s.Slot(table, in row.Id)</c> — and no format-to-arena-then-parse-back round trip.</summary>
    public StagedId Id;
    public TextRef Title, Image, Publisher, Description;
    /// <summary>The cursor AFTER this answer, or 0 when the answer says nothing about paging.</summary>
    public int EpisodesAsked;
    /// <summary><see cref="ShowFlags"/> — the commit takes <see cref="ShowFlags.FactsMask"/> under Facts and
    /// <see cref="ShowFlags.RatingMask"/> under Rating, so a writer sets only the bits of the groups it claims.</summary>
    public uint Flags;
    /// <summary><see cref="ConsumptionOrder"/> as a byte (Facts).</summary>
    public byte Order;
    /// <summary>The trailer uri (Facts).</summary>
    public TextRef Trailer, ListRevision, Topics, HtmlDescription;
    /// <summary>The Rating group: average × 100, its count, the listener's stars, the 0xAARRGGBB tone.</summary>
    public ushort RatingX100;
    public int RatingCount;
    public byte MyRating;
    public uint Tone;
    /// <summary><see cref="ShowFields"/>: which groups this row speaks for.</summary>
    public uint Known;
    public Authority Authority;

    /// <inheritdoc cref="IStagedRow.Init"/>
    public void Init(in StagedId id, Authority authority, uint known) { Id = id; Authority = authority; Known = known; }
    /// <inheritdoc cref="IStagedRow.Identity"/>
    public readonly StagedId Identity => Id;
}

public sealed partial class Staging
{
    StagedList<StagedShow>? _shows;
    public StagedList<StagedShow> Shows => _shows ??= Register(new StagedList<StagedShow>());
    internal StagedList<StagedShow>? ShowsOrNull => _shows;
}

public static partial class Entities
{
    /// <inheritdoc cref="Ensure(ReadOnlySpan{Track},TrackFields,FetchPriority)"/>
    public static void Ensure(ReadOnlySpan<Show> rows, ShowFields wanted, FetchPriority priority = FetchPriority.Visible)
        => Ensure(Current.Shows, Slots(rows), (uint)wanted, priority);

    /// <inheritdoc cref="Ensure(ReadOnlySpan{Track},TrackFields,FetchPriority)"/>
    public static void Ensure(Show row, ShowFields wanted, FetchPriority priority = FetchPriority.Visible)
    {
        Span<int> one = stackalloc int[1];
        one[0] = row.Slot;
        Ensure(Current.Shows, one, (uint)wanted, priority);
    }

    static partial void CommitShows(Staging s)
    {
        var staged = s.ShowsOrNull;
        if (staged is null || staged.Count == 0) return;

        var t = Current.Shows;
        var rows = staged.Span;
        t.EnsureCapacity(t.Count + rows.Length);

        for (int i = 0; i < rows.Length; i++)
        {
            ref var row = ref rows[i];
            int slot = s.Slot(t, in row.Id);
            if (slot == Table.None) continue;   // a row with no identity is not a row
            var auth = row.Authority;
            uint known = row.Known;
            if (!row.ListRevision.IsEmpty) t.SetText(ref t.ListRevision, slot, s.Intern(row.ListRevision));

            if ((known & (uint)ShowFields.Identity) != 0
                && t.Accepts(slot, (uint)ShowFields.Identity, auth, in t.IdentityAuthority))
            {
                // `SetText`, never `Title[slot] = …` — AddRef in, release what it overwrites (defect 1).
                t.SetText(ref t.Title, slot, s.Intern(row.Title));
                t.SetText(ref t.Image, slot, s.Intern(row.Image));
                t.SetText(ref t.Publisher, slot, s.Intern(row.Publisher));
                t.Applied(slot, (uint)ShowFields.Identity, auth, ref t.IdentityAuthority);
            }
            if ((known & (uint)ShowFields.About) != 0
                && t.Accepts(slot, (uint)ShowFields.About, auth, in t.AboutAuthority))
            {
                t.SetText(ref t.Description, slot, s.Intern(row.Description));
                t.Applied(slot, (uint)ShowFields.About, auth, ref t.AboutAuthority);
            }
            if ((known & (uint)ShowFields.Facts) != 0
                && t.Accepts(slot, (uint)ShowFields.Facts, auth, in t.FactsAuthority))
            {
                // Only this group's bits move; the Rating group's stay as its own answer left them.
                t.Flags[slot] = (t.Flags[slot] & ~(uint)ShowFlags.FactsMask) | (row.Flags & (uint)ShowFlags.FactsMask);
                t.Order[slot] = row.Order;
                t.SetText(ref t.Trailer, slot, s.Intern(row.Trailer));
                t.Applied(slot, (uint)ShowFields.Facts, auth, ref t.FactsAuthority);
            }
            if ((known & (uint)ShowFields.Rating) != 0
                && t.Accepts(slot, (uint)ShowFields.Rating, auth, in t.RatingAuthority))
            {
                t.Flags[slot] = (t.Flags[slot] & ~(uint)ShowFlags.RatingMask) | (row.Flags & (uint)ShowFlags.RatingMask);
                t.RatingX100[slot] = row.RatingX100;
                t.RatingCount[slot] = row.RatingCount;
                t.MyRating[slot] = row.MyRating;
                t.Applied(slot, (uint)ShowFields.Rating, auth, ref t.RatingAuthority);
            }

            if ((known & (uint)ShowFields.Appearance) != 0 && t.Accepts(slot, (uint)ShowFields.Appearance, auth, in t.AppearanceAuthority))
            {
                t.Tone[slot] = row.Tone;
                t.Applied(slot, (uint)ShowFields.Appearance, auth, ref t.AppearanceAuthority);
            }
            if ((known & (uint)ShowFields.Topics) != 0 && t.Accepts(slot, (uint)ShowFields.Topics, auth, in t.TopicsAuthority))
            {
                t.SetText(ref t.Topics, slot, s.Intern(row.Topics));
                t.Applied(slot, (uint)ShowFields.Topics, auth, ref t.TopicsAuthority);
            }
            if ((known & (uint)ShowFields.Html) != 0 && t.Accepts(slot, (uint)ShowFields.Html, auth, in t.HtmlAuthority))
            {
                t.SetText(ref t.HtmlDescription, slot, s.Intern(row.HtmlDescription));
                t.Applied(slot, (uint)ShowFields.Html, auth, ref t.HtmlAuthority);
            }
            // The cursor only ever moves FORWARD, and outside the authority ladder: it is not a fact about the show,
            // it is how far this session has asked, and an out-of-order answer must not rewind it.
            if (row.EpisodesAsked > t.EpisodesAsked[slot])
            {
                t.EpisodesAsked[slot] = row.EpisodesAsked;
                t.Bump(slot);
            }
        }
    }
}

// ── persistence (Store.cs's per-kind seam) ───────────────────────────────────────────────────────────────────────────

/// <summary>How a show survives a restart (Store.cs §2). Persists <see cref="ShowFields.Identity"/>,
/// <see cref="ShowFields.About"/>, <see cref="ShowFields.Facts"/> and <see cref="ShowFields.Rating"/> — the four groups
/// this file's commit applies. The flags are TWO columns, one per group's mask: the upsert coalesces per column, so a
/// shared one would let a Facts-only answer overwrite the Rating bits on disk. Appending the columns moved the DDL
/// fingerprint, which is the schema bump (Store.cs: a new fingerprint names a new file). <see cref="ShowTable.EpisodesAsked"/>
/// is NOT persisted: it is a paging cursor over <c>Edges.ShowEpisodes</c>, which this shape does not persist either
/// (only the five <c>LibraryEdge</c> relations are), so a stale cursor with no membership behind it would just pin a
/// wrong "load more" gate after a cold start — the cursor re-derives itself from the network the moment the page asks.
///
/// <para>STORE THREAD (both halves): never touches a live column or the interner (file header). <see cref="Save"/>
/// reads the <see cref="Staging"/> that was just committed — the same batch the provider answered with — and
/// <see cref="Load"/> only ever produces TEXT-form <see cref="StagedId"/>s (<see cref="RowReader.Uri"/>), which the
/// later UI-thread commit resolves through the ordinary <c>Staging.Slot</c> path (Store.cs's read-side note).</para></summary>
public sealed class ShowShape : KindShape
{
    static readonly StoreColumn[] Cols =
    [
        new("title", StoreType.Text, StoreColumnFlags.Title),
        new("image", StoreType.Text),
        new("publisher", StoreType.Text),
        new("description", StoreType.Text),
        new("identity_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("about_auth", StoreType.Int, StoreColumnFlags.Authority),
        // ── podcast rework P1 (appended: the indices above are stable) ──
        new("flags", StoreType.Int),                    // 6  FactsMask bits
        new("consumption_order", StoreType.Int),        // 7  (`order` is an sql keyword; the DDL does not quote)
        new("trailer", StoreType.Text),                 // 8
        new("facts_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("rating_x100", StoreType.Int),              // 10
        new("rating_count", StoreType.Int),
        new("my_rating", StoreType.Int),
        new("tone", StoreType.Int),
        new("rating_flags", StoreType.Int),             // 14 RatingMask bits
        new("rating_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("appearance_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("topics", StoreType.Text),
        new("topics_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("html_description", StoreType.Text),
        new("html_auth", StoreType.Int, StoreColumnFlags.Authority),
    ];

    const uint PersistedFields = (uint)ShowFields.All;

    public override EntityKind Kind => EntityKind.Show;
    public override string Table => "show";
    public override ReadOnlySpan<StoreColumn> Columns => Cols;

    public override void Save(Staging s, RowWriter w)
    {
        var rows = s.ShowsOrNull;
        if (rows is null) return;
        var span = rows.Span;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var row = ref span[i];
            uint known = row.Known & PersistedFields;
            bool identity = (known & (uint)ShowFields.Identity) != 0;
            bool about = (known & (uint)ShowFields.About) != 0;
            bool facts = (known & (uint)ShowFields.Facts) != 0;
            bool rating = (known & (uint)ShowFields.Rating) != 0;

            if (identity)
            {
                w.Text(0, row.Title);
                w.Text(1, row.Image);
                w.Text(2, row.Publisher);
                w.Int(4, (int)row.Authority);
            }
            else { w.Null(0); w.Null(1); w.Null(2); w.Null(4); }

            if (about)
            {
                w.Text(3, row.Description);
                w.Int(5, (int)row.Authority);
            }
            else { w.Null(3); w.Null(5); }

            if (facts)
            {
                w.Int(6, row.Flags & (uint)ShowFlags.FactsMask);
                w.Int(7, row.Order);
                w.Text(8, row.Trailer);
                w.Int(9, (int)row.Authority);
            }
            else { w.Null(6); w.Null(7); w.Null(8); w.Null(9); }

            if (rating)
            {
                w.Int(10, row.RatingX100);
                w.Int(11, row.RatingCount);
                w.Int(12, row.MyRating);
                w.Int(14, row.Flags & (uint)ShowFlags.RatingMask);
                w.Int(15, (int)row.Authority);
            }
            else { w.Null(10); w.Null(11); w.Null(12); w.Null(14); w.Null(15); }
            if ((known & (uint)ShowFields.Appearance) != 0) { w.Int(13, row.Tone); w.Int(16, (int)row.Authority); }
            else { w.Null(13); w.Null(16); }
            if ((known & (uint)ShowFields.Topics) != 0) { w.Text(17, row.Topics); w.Int(18, (int)row.Authority); }
            else { w.Null(17); w.Null(18); }
            if ((known & (uint)ShowFields.Html) != 0) { w.Text(19, row.HtmlDescription); w.Int(20, (int)row.Authority); }
            else { w.Null(19); w.Null(20); }

            w.Emit(row.Id, known, Entities.Now, Entities.Now);
        }
    }

    public override void Load(RowReader r, Staging into)
    {
        var row = new StagedShow { Id = r.Uri };
        row.Title = r.Text(0);
        row.Image = r.Text(1);
        row.Publisher = r.Text(2);
        row.Description = r.Text(3);
        row.Flags = ((uint)r.Int(6) & (uint)ShowFlags.FactsMask) | ((uint)r.Int(14) & (uint)ShowFlags.RatingMask);
        row.Order = (byte)r.Int(7);
        row.Trailer = r.Text(8);
        row.RatingX100 = (ushort)r.Int(10);
        row.RatingCount = (int)r.Int(11);
        row.MyRating = (byte)r.Int(12);
        row.Tone = (uint)r.Int(13);
        row.Known = r.Known & PersistedFields;
        row.Topics = r.Text(17);
        row.HtmlDescription = r.Text(19);
        ReadOnlySpan<uint> groups = [(uint)ShowFields.Identity, (uint)ShowFields.About, (uint)ShowFields.Facts,
            (uint)ShowFields.Rating, (uint)ShowFields.Appearance, (uint)ShowFields.Topics, (uint)ShowFields.Html];
        ReadOnlySpan<int> authorities = [4, 5, 9, 15, 16, 18, 20];
        for (int i = 0; i < groups.Length; i++)
        {
            uint known = row.Known & groups[i];
            if (known == 0) continue;
            ref var staged = ref into.Shows.Add();
            staged = row;
            staged.Known = known;
            staged.Authority = (Authority)r.Int(authorities[i]);
        }
    }
}
