// ── Entities/Episode.cs — CORE (owner A, wave 1; plan §2 · the file's full budget is 200) ────────────────────────────
//
// THE EPISODE COLUMNS, FIELD GROUPS AND HANDLE. `Episode.Rules` — `Pct`, `InProgress`, `Played`, `Unplayed`, the
// four-way status predicate, the Newest/Oldest order, the resume PICK and the load-more gate — is the one genuinely
// new pure class ch 09 §8 asks for, and it is owner M's in Wave 5. It computes from the two columns below at READ
// time and caches nothing: derived facts live on the model, and a second column for "in progress" would be a fact
// that can disagree with the numbers it came from.
//
// Ch 09 §7 is the reader. Its table says, of six of these columns, "NOT IN THE PLAN" — plan §4 declares no episode
// columns at all — so every one below cites that chapter's gap row.

using FluentGpu.Foundation;

namespace Wavee;

/// <summary>Which of an episode's columns are filled. <see cref="Progress"/> is the odd one out and deliberately so:
/// it is per-USER state, not catalogue state, so it carries its own authority column and a catalogue write must never
/// clobber a local one (ch 09 DATA GAPS, first row).</summary>
[Flags]
public enum EpisodeFields : uint
{
    None = 0,

    Title = 1 << 0,
    Image = 1 << 1,
    Duration = 1 << 2,
    Published = 1 << 3,
    /// <summary>The owning show's slot — the episode row's subtitle LINK and the "Go to podcast" menu row
    /// (ch 09 DATA GAPS). It is also where <c>EpisodeAsTrack</c> puts the show, in the album slot.</summary>
    Show = 1 << 4,
    Identity = Title | Image | Duration | Published | Show,

    /// <summary>The two-line description clamp (ch 09 §7: the row FADES IN late and must not hold the page).</summary>
    About = 1 << 8,
    /// <summary>The resume position. An episode whose progress is unknown renders as UNPLAYED, never as a half-drawn
    /// bar (ch 09 §7) — which is why this bit is not part of <see cref="Row"/>.</summary>
    Progress = 1 << 9,

    /// <summary>What an episode row paints. Identical to <see cref="Identity"/> ON PURPOSE: the list's shimmer gate and
    /// the cells' gate are the same set, and <see cref="Progress"/> is excluded because its absence is a rendered
    /// state, not a missing one (ch 09 §7).</summary>
    Row = Identity,
    All = Identity | About | Progress,
}

/// <summary>Every episode in one scope, as columns (ch 09 DATA GAPS). No <c>Flags</c> column: nothing on this surface
/// reads a boolean about an episode that is not derived from the two duration/progress numbers (P3 cuts both ways — a
/// bit nobody reads is as wrong as a bool column).</summary>
public sealed class EpisodeTable : Table
{
    public Column<StringId> Title, Image, Description;
    public Column<int> DurationMs;
    /// <summary>Unix seconds, the same epoch as <c>FetchedAt</c> (ch 09 DATA GAPS).</summary>
    public Column<int> PublishedAt;
    /// <summary>The resume position, ms. USER state — see <see cref="ProgressAuthority"/>.</summary>
    public Column<int> ProgressMs;
    /// <summary>The owning show's slot, 0 = the writer did not know (ch 09 DATA GAPS).</summary>
    public Column<int> Show;

    // ── authority, per column GROUP (D16) ──
    /// <summary><see cref="ProgressAuthority"/> is its own column because progress is written at
    /// <see cref="Authority.Local"/> by the player and at a lower rung by the catalogue's playback-state trait: a
    /// stale server position must never rewind the position this device just played to (ch 09 DATA GAPS).</summary>
    public Column<byte> IdentityAuthority, AboutAuthority, ProgressAuthority;

    public override EntityKind Kind => EntityKind.Episode;

    protected override void GrowColumns(int capacity)
    {
        Title.EnsureCapacity(capacity);
        Image.EnsureCapacity(capacity);
        Description.EnsureCapacity(capacity);
        DurationMs.EnsureCapacity(capacity);
        PublishedAt.EnsureCapacity(capacity);
        ProgressMs.EnsureCapacity(capacity);
        Show.EnsureCapacity(capacity);
        IdentityAuthority.EnsureCapacity(capacity);
        AboutAuthority.EnsureCapacity(capacity);
        ProgressAuthority.EnsureCapacity(capacity);
    }

    /// <summary>Give back every string an episode row owns (defect 1; the REF-COUNTING block on <see cref="Table"/>).
    /// One line per <c>Column&lt;StringId&gt;</c> above, and the pair to the <see cref="Table.SetText"/> the commit
    /// writes through: an id nobody releases is PERMANENT (<c>StringTable.cs:26</c>), so before this override a trim
    /// freed the row and left its title, cover and two-line description behind for good (doc §4.4).</summary>
    protected override void ReleaseText(int slot)
    {
        ClearText(ref Title, slot);
        ClearText(ref Image, slot);
        ClearText(ref Description, slot);
    }
}

/// <summary>An episode: one <c>int</c>. Partial because <c>Episode.UI.cs</c> (Wave 5) adds the row and its variants,
/// and this file's Wave-5 half adds <c>Episode.Rules</c>.</summary>
public readonly partial struct Episode(int slot) : IEquatable<Episode>
{
    static EpisodeTable T => Entities.Current.Episodes;

    public int Slot { get; } = slot;
    public bool IsValid => Slot > Table.None && Slot < T.Count;
    public uint Version => T.Version[Slot];
    public bool Knows(EpisodeFields fields) => (T.Known[Slot] & (uint)fields) == (uint)fields;

    /// <inheritdoc cref="Track.Id"/>
    public EntityId Id => T.Id[Slot];
    /// <inheritdoc cref="Track.Uri"/>
    public EntityUri Uri => new(T.Id[Slot]);

    public StringId TitleId => T.Title[Slot];
    public string Title => Entities.Strings.Resolve(T.Title[Slot]);
    public StringId ImageId => T.Image[Slot];
    public StringId DescriptionId => T.Description[Slot];
    public int DurationMs => T.DurationMs[Slot];
    public int PublishedAt => T.PublishedAt[Slot];
    public int ProgressMs => T.ProgressMs[Slot];
    public Show Show => new(T.Show[Slot]);
    public int ShowSlot => T.Show[Slot];

    public bool Equals(Episode other) => other.Slot == Slot;
    public override bool Equals(object? obj) => obj is Episode other && other.Slot == Slot;
    public override int GetHashCode() => Slot;
    public static bool operator ==(Episode a, Episode b) => a.Slot == b.Slot;
    public static bool operator !=(Episode a, Episode b) => a.Slot != b.Slot;
}

// ── staging and the commit ───────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One decoded episode.</summary>
public struct StagedEpisode : IStagedRow
{
    /// <summary>THE row's identity as the wire gave it (<see cref="StagedId"/>): the packed <see cref="EntityId"/> when
    /// it arrived as 16 gid bytes, the uri's UTF-8 in the arena when it arrived as text. One field, one resolve —
    /// <c>s.Slot(table, in row.Id)</c> — and no format-to-arena-then-parse-back round trip.</summary>
    public StagedId Id;
    public TextRef Title, Image, Description;
    /// <summary>The show this episode belongs to — staged thin so the row can name it before the fetch.</summary>
    public StagedId ShowUri;
    public int DurationMs, PublishedAt, ProgressMs;
    /// <summary><see cref="EpisodeFields"/>: which groups this row speaks for.</summary>
    public uint Known;
    public Authority Authority;

    /// <inheritdoc cref="IStagedRow.Init"/>
    public void Init(in StagedId id, Authority authority, uint known) { Id = id; Authority = authority; Known = known; }
    /// <inheritdoc cref="IStagedRow.Identity"/>
    public readonly StagedId Identity => Id;
}

public sealed partial class Staging
{
    StagedList<StagedEpisode>? _episodes;
    public StagedList<StagedEpisode> Episodes => _episodes ??= Register(new StagedList<StagedEpisode>());
    internal StagedList<StagedEpisode>? EpisodesOrNull => _episodes;
}

public static partial class Entities
{
    /// <inheritdoc cref="Ensure(ReadOnlySpan{Track},TrackFields,FetchPriority)"/>
    public static void Ensure(ReadOnlySpan<Episode> rows, EpisodeFields wanted, FetchPriority priority = FetchPriority.Visible)
        => Ensure(Current.Episodes, Slots(rows), (uint)wanted, priority);

    /// <inheritdoc cref="Ensure(ReadOnlySpan{Track},TrackFields,FetchPriority)"/>
    public static void Ensure(Episode row, EpisodeFields wanted, FetchPriority priority = FetchPriority.Visible)
    {
        Span<int> one = stackalloc int[1];
        one[0] = row.Slot;
        Ensure(Current.Episodes, one, (uint)wanted, priority);
    }

    static partial void CommitEpisodes(Staging s)
    {
        var staged = s.EpisodesOrNull;
        if (staged is null || staged.Count == 0) return;

        var t = Current.Episodes;
        var rows = staged.Span;
        t.EnsureCapacity(t.Count + rows.Length);

        for (int i = 0; i < rows.Length; i++)
        {
            ref var row = ref rows[i];
            int slot = s.Slot(t, in row.Id);
            if (slot == Table.None) continue;   // a row with no identity is not a row
            var auth = row.Authority;
            uint known = row.Known;

            if ((known & (uint)EpisodeFields.Identity) != 0
                && t.Accepts(slot, (uint)EpisodeFields.Identity, auth, in t.IdentityAuthority))
            {
                // `SetText`, never `Title[slot] = …`: it AddRefs what comes in and releases what it overwrites, so a
                // re-answered episode owns one title and one cover rather than one per answer (defect 1).
                t.SetText(ref t.Title, slot, s.Intern(row.Title));
                t.SetText(ref t.Image, slot, s.Intern(row.Image));
                t.DurationMs[slot] = row.DurationMs;
                t.PublishedAt[slot] = row.PublishedAt;
                if (!row.ShowUri.IsEmpty) t.Show[slot] = s.Slot(Current.Shows, in row.ShowUri);
                t.Applied(slot, (uint)EpisodeFields.Identity, auth, ref t.IdentityAuthority);
            }
            if ((known & (uint)EpisodeFields.About) != 0
                && t.Accepts(slot, (uint)EpisodeFields.About, auth, in t.AboutAuthority))
            {
                t.SetText(ref t.Description, slot, s.Intern(row.Description));
                t.Applied(slot, (uint)EpisodeFields.About, auth, ref t.AboutAuthority);
            }
            if ((known & (uint)EpisodeFields.Progress) != 0
                && t.Accepts(slot, (uint)EpisodeFields.Progress, auth, in t.ProgressAuthority))
            {
                t.ProgressMs[slot] = row.ProgressMs;
                t.Applied(slot, (uint)EpisodeFields.Progress, auth, ref t.ProgressAuthority);
            }
        }
    }
}
