// ── Entities/Episode.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the episode columns, field groups, handle and commit (Wave 1) + `Episode.Rules` (Wave 5)
//
// Role: CORE
// Owner: A (Wave 1: the data model) · M (Wave 5: `Episode.Rules`, stream C of WP-5.M)
// Wave: 1 · 5
// Budget: 200 lines (plan §2; ch 09 §9.5's honest estimate is the same 200 "incl. the new Episode.Rules"). The columns
//   and the commit already took 215, so the rules put the file past +30 %; the partial they would move to is
//   `Entities/Episode.Rules.cs` — named in the WP-5.M report, not created (the WP-5 file rule).
// Spec: plan §2 · ch 09 §7, §8 (last row), §9.3-§9.4 · 0.2.9 `Features/Detail/EpisodeList.cs:40-101`
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

// ── persistence (Store.cs's per-kind seam) ───────────────────────────────────────────────────────────────────────────

/// <summary>How an episode survives a restart. Persists all three groups <c>EpisodeTable</c> carries: identity, the
/// about clamp, AND the resume position — <see cref="EpisodeFields.Progress"/> is the one cold field worth caching,
/// since losing it on every relaunch would silently rewind a listener's place (its own <c>progress_auth</c> column,
/// same as memory: a stale server position must never rewind what THIS device just played to, D16).
/// <para>STORE THREAD (both halves) — see <see cref="ShowShape"/>'s note.</para></summary>
public sealed class EpisodeShape : KindShape
{
    static readonly StoreColumn[] Cols =
    [
        new("title", StoreType.Text, StoreColumnFlags.Title),
        new("image", StoreType.Text),
        new("show_uri", StoreType.Text),
        new("duration_ms", StoreType.Int),
        new("published_at", StoreType.Int),
        new("description", StoreType.Text),
        new("progress_ms", StoreType.Int),
        new("identity_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("about_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("progress_auth", StoreType.Int, StoreColumnFlags.Authority),
    ];

    const uint PersistedFields = (uint)(EpisodeFields.Identity | EpisodeFields.About | EpisodeFields.Progress);

    public override EntityKind Kind => EntityKind.Episode;
    public override string Table => "episode";
    public override ReadOnlySpan<StoreColumn> Columns => Cols;

    public override void Save(Staging s, RowWriter w)
    {
        var rows = s.EpisodesOrNull;
        if (rows is null) return;
        var span = rows.Span;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var row = ref span[i];
            uint known = row.Known & PersistedFields;
            bool identity = (known & (uint)EpisodeFields.Identity) != 0;
            bool about = (known & (uint)EpisodeFields.About) != 0;
            bool progress = (known & (uint)EpisodeFields.Progress) != 0;

            if (identity)
            {
                w.Text(0, row.Title);
                w.Text(1, row.Image);
                w.Id(2, s, row.ShowUri);
                w.Int(3, row.DurationMs);
                w.Int(4, row.PublishedAt);
                w.Int(7, (int)row.Authority);
            }
            else { w.Null(0); w.Null(1); w.Null(2); w.Null(3); w.Null(4); w.Null(7); }

            if (about)
            {
                w.Text(5, row.Description);
                w.Int(8, (int)row.Authority);
            }
            else { w.Null(5); w.Null(8); }

            if (progress)
            {
                w.Int(6, row.ProgressMs);
                w.Int(9, (int)row.Authority);
            }
            else { w.Null(6); w.Null(9); }

            w.Emit(row.Id, known, Entities.Now, Entities.Now);
        }
    }

    public override void Load(RowReader r, Staging into)
    {
        ref var row = ref into.Episodes.Add();
        row.Id = r.Uri;
        row.Title = r.Text(0);
        row.Image = r.Text(1);
        row.ShowUri = r.Text(2);
        row.DurationMs = (int)r.Int(3);
        row.PublishedAt = (int)r.Int(4);
        row.Description = r.Text(5);
        row.ProgressMs = (int)r.Int(6);
        row.Known = r.Known & PersistedFields;
        row.Authority = (Authority)Math.Max(r.Int(7), Math.Max(r.Int(8), r.Int(9)));
    }
}

// ══ WAVE 5 (owner M, stream C): Episode.Rules ════════════════════════════════════════════════════════════════════════
//
// The one genuinely new pure class ch 09 §8 asks for, EXTRACTED from 0.2.9 `EpisodeList.cs` — where these five decisions
// were private statics inside a Component and therefore untestable. The thresholds (`> 0.01`, `< 0.98`, `>= 0.98`) and
// the `max(model, local)` cursor fold are 0.2.9's and load-bearing; the rule (3 DIP), the "In progress" caption and the
// status filter are three presentations of ONE number, so all three read `Pct` and never three predicates (ch 09 §9.1).

public readonly partial struct Episode
{
    public static class Rules
    {
        /// <summary>At or below this an episode is unplayed; above it the 3-DIP rule exists.</summary>
        public const float InProgressFloor = 0.01f;
        /// <summary>At or above this an episode is played.</summary>
        public const float PlayedCeiling = 0.98f;

        /// <summary>The four-way status filter, in the SelectorBar's order (0 All · 1 Unplayed · 2 In progress · 3 Played).</summary>
        public enum Status : byte { All = 0, Unplayed = 1, InProgress = 2, Played = 3 }

        /// <summary><c>clamp(progress / duration, 0, 1)</c>; 0 when the duration is not known (EpisodeList.cs:40).</summary>
        public static float Pct(int progressMs, int durationMs)
            => durationMs > 0 ? Math.Clamp(progressMs / (float)durationMs, 0f, 1f) : 0f;

        /// <summary>A row's fraction. Progress UNKNOWN reads 0 — an unplayed card, never a half-drawn bar (ch 09 §7).</summary>
        public static float PctOf(Episode e) => e.Knows(EpisodeFields.Progress) ? Pct(e.ProgressMs, e.DurationMs) : 0f;

        public static bool InProgress(float pct) => pct > InProgressFloor && pct < PlayedCeiling;

        public static bool Played(float pct) => pct >= PlayedCeiling;

        /// <summary>Does the card draw its progress rule (EpisodeList.cs:230)?</summary>
        public static bool HasRule(float pct) => pct > InProgressFloor;

        /// <summary>The status filter's predicate (EpisodeList.cs:56).</summary>
        public static bool Matches(Status status, float pct) => status switch
        {
            Status.Unplayed => pct <= InProgressFloor,
            Status.InProgress => InProgress(pct),
            Status.Played => Played(pct),
            _ => true,
        };

        /// <summary>The "Listen next" pick: the most-progressed IN-PROGRESS episode over ALL episodes, whatever the
        /// filter shows (EpisodeList.cs:78-81); ties keep the earlier (newer) one; -1 when none is in progress.</summary>
        public static int ResumePick(ReadOnlySpan<float> pcts)
        {
            int resume = -1;
            float best = 0f;
            for (int i = 0; i < pcts.Length; i++)
                if (InProgress(pcts[i]) && pcts[i] > best) { best = pcts[i]; resume = i; }
            return resume;
        }

        /// <summary>The filtered, ordered view as ORIGINAL indices (so Play addresses the show context, not the view):
        /// newest-first as the wire orders them, reversed for Oldest (EpisodeList.cs:52-59). Returns the count written.</summary>
        public static int View(ReadOnlySpan<float> pcts, Status status, bool oldest, Span<int> into)
        {
            int n = 0;
            for (int i = 0; i < pcts.Length && n < into.Length; i++)
                if (Matches(status, pcts[i])) into[n++] = i;
            if (oldest) into[..n].Reverse();
            return n;
        }

        /// <summary>THE load-more gate — the paging CURSOR, never the resident count (EpisodeList.cs:65-72; ch 09 §9.1):
        /// how far anybody has ASKED, the model's cursor or the page's own, against the total.</summary>
        public static bool CanLoadMore(int edgeAsked, int localAsked, int total) => Math.Max(edgeAsked, localAsked) < total;

        /// <summary>The pill as the page shows it: only a PARTIAL membership pages (a Complete list never does, whatever
        /// the cursor column says), and then the cursor gate above decides.</summary>
        public static bool CanLoadMore(EdgeState state, int edgeAsked, int localAsked, int total)
            => state == EdgeState.Partial && CanLoadMore(edgeAsked, localAsked, total);

        /// <summary>Is a load-more ask still out? It was asked (<paramref name="askedFrom"/> ≥ 0), the relation's version has
        /// not moved since the ask, and the ask has not failed. A version move or a failure ends it.</summary>
        public static bool Paging(int askedFrom, uint versionAtAsk, uint versionNow, bool failed)
            => askedFrom >= 0 && versionNow == versionAtAsk && !failed;

        /// <summary>The page's own half of the cursor after an ask settles: a page that ANSWERED (with rows or without)
        /// moves it past the offset asked, so a withdrawn member cannot pin the pill; a failure leaves it so the next tap
        /// retries (0.2.9's `_pagedTo`, EpisodeList.cs:107-123).</summary>
        public static int LocalCursorAfter(int localAsked, int askedFrom, bool paging, bool failed)
            => askedFrom >= 0 && !paging && !failed ? Math.Max(localAsked, askedFrom + 1) : localAsked;

        /// <summary>Where the next page starts: the furthest of the model's cursor, the page's cursor and the resident
        /// count.</summary>
        public static int NextOffset(int edgeAsked, int localAsked, int resident)
            => Math.Max(Math.Max(edgeAsked, localAsked), resident);

        /// <summary>Whole minutes, 0.2.9's integer arithmetic (<c>DurationMs / 60000</c>); the view formats
        /// <c>podcast.minutes</c> (ch 09 §9.4's loc fix).</summary>
        public static int Minutes(int durationMs) => durationMs / 60_000;

        /// <summary>The zero-episode arm (ch 09 §9.4 fix): the show's own empty copy only when no filter is applied and
        /// the unfiltered set is genuinely empty; any other empty view is "No episodes match this filter".</summary>
        public static bool IsEmptyShow(int total, int resident, Status status)
            => status == Status.All && resident == 0 && total <= 0;
    }
}
