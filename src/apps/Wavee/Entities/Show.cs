// ── Entities/Show.cs — CORE (owner A, wave 1; plan §2 · the file's full budget is 180) ───────────────────────────────
//
// THE SHOW COLUMNS, FIELD GROUPS AND HANDLE. Ch 09 §7 is the reader; plan §4 declares no show columns, so the three
// text columns below are that chapter's "ShowTable.Publisher, Description, Image" gap row.
//
// The show page is the shared detail frame with EPISODES where the tracks go, so this table is deliberately thin: the
// episode SET is `Edges.ShowEpisodes` (parent = the show slot), its count is that edge's `Total`, and the load-more
// gate is a CURSOR — see `EpisodesAsked` for the one column here that is not a chapter field but a chapter DEFECT FIX.

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

    All = Identity | About,
}

/// <summary>Every show in one scope, as columns.</summary>
public sealed class ShowTable : Table
{
    public Column<StringId> Title, Image, Publisher, Description;

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

    // ── authority, per column GROUP (D16) ──
    public Column<byte> IdentityAuthority, AboutAuthority;

    public override EntityKind Kind => EntityKind.Show;

    protected override void GrowColumns(int capacity)
    {
        Title.EnsureCapacity(capacity);
        Image.EnsureCapacity(capacity);
        Publisher.EnsureCapacity(capacity);
        Description.EnsureCapacity(capacity);
        EpisodesAsked.EnsureCapacity(capacity);
        IdentityAuthority.EnsureCapacity(capacity);
        AboutAuthority.EnsureCapacity(capacity);
    }

    /// <summary>Give back every string a show row owns (defect 1; the REF-COUNTING block on <see cref="Table"/>). One
    /// line per <c>Column&lt;StringId&gt;</c> above, and the pair to the <see cref="Table.SetText"/> the commit writes
    /// through — a show's rail description is the longest single string this table holds, and before this override a
    /// trim reclaimed the row's columns and none of it (doc §4.4).</summary>
    protected override void ReleaseText(int slot)
    {
        ClearText(ref Title, slot);
        ClearText(ref Image, slot);
        ClearText(ref Publisher, slot);
        ClearText(ref Description, slot);
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

/// <summary>How a show survives a restart (Store.cs §2). Persists <see cref="ShowFields.Identity"/> and
/// <see cref="ShowFields.About"/> — the two groups this file's commit actually applies. <see cref="ShowTable.EpisodesAsked"/>
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
    ];

    const uint PersistedFields = (uint)(ShowFields.Identity | ShowFields.About);

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

            w.Emit(row.Id, known, Entities.Now, Entities.Now);
        }
    }

    public override void Load(RowReader r, Staging into)
    {
        ref var row = ref into.Shows.Add();
        row.Id = r.Uri;
        row.Title = r.Text(0);
        row.Image = r.Text(1);
        row.Publisher = r.Text(2);
        row.Description = r.Text(3);
        row.Known = r.Known & PersistedFields;
        row.Authority = (Authority)Math.Max(r.Int(4), r.Int(5));
    }
}
