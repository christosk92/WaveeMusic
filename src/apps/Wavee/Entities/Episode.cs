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
//
// PODCAST REWORK, wave P1 (owner B; docs/plans/wavee/podcast-show-rework-implementation.md §5.1): `Number`, `Variant`
// (the episode kind) and `Flags` ride the Identity group — `EpisodeV4` carries them with the identity and the row paints
// them — and `PlayedAt` rides Progress; `Rules` gains THE one completion rule and the herodotus fold.

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
    Detail = 1 << 10,
    Completion = 1 << 11,
    Transcript = 1 << 12,
    Media = 1 << 13,

    /// <summary>What an episode row paints. Identical to <see cref="Identity"/> ON PURPOSE: the list's shimmer gate and
    /// the cells' gate are the same set, and <see cref="Progress"/> is excluded because its absence is a rendered
    /// state, not a missing one (ch 09 §7).</summary>
    Row = Identity,
    All = Identity | About | Progress | Detail | Completion | Transcript | Media,
}

/// <summary>Episode booleans as bits (P3). The first three come with the identity (<c>EpisodeV4</c>); the last three
/// are the pathfinder episode answer's (wave P5), and when that answer gets its own group its commit takes those three
/// as ITS mask — the <see cref="ShowFlags"/> split. Until then the Identity arm writes the whole column.</summary>
[Flags]
public enum EpisodeFlags : uint
{
    None = 0,
    /// <summary><c>Episode.explicit</c> (metadata.proto field 70).</summary>
    Explicit = 1 << 0,
    /// <summary>A video episode: the repeated <c>video</c> file list (field 72) is non-empty.</summary>
    Video = 1 << 1,
    /// <summary><c>is_podcast_short</c> (field 97).</summary>
    Short = 1 << 2,
    /// <summary>Subscribers-only (pathfinder <c>restrictions.paywallContent</c>).</summary>
    Paywalled = 1 << 3,
    /// <summary>Only a preview plays here (pathfinder <c>previewPlayback</c> while not <c>playability.playable</c>).</summary>
    PreviewOnly = 1 << 4,
    /// <summary>A transcript exists (pathfinder <c>transcripts.items[]</c> non-empty) — the reader's transcript tab.</summary>
    HasTranscript = 1 << 5,
    Unplayable = 1 << 6,
    /// <summary><c>metadata.Episode.is_audiobook_chapter</c> (field 96).</summary>
    AudiobookChapter = 1 << 7,
}

/// <summary>Which kind of episode (<c>Episode.type</c>, field 87). The numbers ARE the wire's (FULL 0 · TRAILER 1 ·
/// BONUS 2), so an absent field is a full episode.</summary>
public enum EpisodeKind : byte { Full = 0, Trailer = 1, Bonus = 2 }

/// <summary>Every episode in one scope, as columns (ch 09 DATA GAPS; podcast plan §5.1).</summary>
public sealed class EpisodeTable : Table
{
    public Column<StringId> Title, Image, Description, HtmlDescription, TranscriptUrl, TranscriptLanguage;
    public Column<int> DurationMs;
    /// <summary>Unix seconds, the same epoch as <c>FetchedAt</c> (ch 09 DATA GAPS).</summary>
    public Column<int> PublishedAt;
    /// <summary>The resume position, ms. USER state — see <see cref="ProgressAuthority"/>. A completed episode holds
    /// its duration (or <see cref="int.MaxValue"/> before the duration is resident — <see cref="Episode.Rules.ProgressOf"/>).</summary>
    public Column<int> ProgressMs;
    /// <summary>The owning show's slot, 0 = the writer did not know (ch 09 DATA GAPS).</summary>
    public Column<int> Show;
    /// <summary>The number within its show (<c>number</c>, field 89); 0 = unnumbered. Identity group.</summary>
    public Column<ushort> Number, Season;
    public Column<bool> TranscriptReadAlong, ExplicitCompleted;
    public Column<long> RevisionUpdateSeconds, RevisionCreateSeconds, CompletionAtMs;
    public Column<int> RevisionUpdateNanos, RevisionCreateNanos;
    /// <summary><see cref="EpisodeKind"/> as a byte. Not <c>Kind</c>: <see cref="Table.Kind"/> is the table's
    /// <see cref="EntityKind"/> (the <c>AlbumTable.ReleaseKind</c> precedent). Identity group.</summary>
    public Column<byte> Variant;
    /// <summary><see cref="EpisodeFlags"/>. Identity group (see the enum for the P5 split).</summary>
    public Column<uint> Flags;
    /// <summary>Unix seconds of the newest resume-point revision; 0 = never played. PROGRESS state — it rides that group
    /// and its authority, so a stale server stamp cannot rewind this device's own. Feeds "new since you were here".</summary>
    public Column<int> PlayedAt;

    // ── authority, per column GROUP (D16) ──
    /// <summary><see cref="ProgressAuthority"/> is its own column because progress is written at
    /// <see cref="Authority.Local"/> by the player and at a lower rung by the catalogue's playback-state trait: a
    /// stale server position must never rewind the position this device just played to (ch 09 DATA GAPS).</summary>
    public Column<byte> IdentityAuthority, AboutAuthority, ProgressAuthority, DetailAuthority, CompletionAuthority, TranscriptAuthority, MediaAuthority;

    public override EntityKind Kind => EntityKind.Episode;

    protected override void GrowColumns(int capacity)
    {
        HtmlDescription.EnsureCapacity(capacity);
        TranscriptUrl.EnsureCapacity(capacity);
        TranscriptLanguage.EnsureCapacity(capacity);
        Season.EnsureCapacity(capacity);
        TranscriptReadAlong.EnsureCapacity(capacity);
        ExplicitCompleted.EnsureCapacity(capacity);
        RevisionUpdateSeconds.EnsureCapacity(capacity);
        RevisionCreateSeconds.EnsureCapacity(capacity);
        CompletionAtMs.EnsureCapacity(capacity);
        RevisionUpdateNanos.EnsureCapacity(capacity);
        RevisionCreateNanos.EnsureCapacity(capacity);
        DetailAuthority.EnsureCapacity(capacity);
        TranscriptAuthority.EnsureCapacity(capacity);
        MediaAuthority.EnsureCapacity(capacity);
        CompletionAuthority.EnsureCapacity(capacity);
        Title.EnsureCapacity(capacity);
        Image.EnsureCapacity(capacity);
        Description.EnsureCapacity(capacity);
        DurationMs.EnsureCapacity(capacity);
        PublishedAt.EnsureCapacity(capacity);
        ProgressMs.EnsureCapacity(capacity);
        Show.EnsureCapacity(capacity);
        Number.EnsureCapacity(capacity);
        Variant.EnsureCapacity(capacity);
        Flags.EnsureCapacity(capacity);
        PlayedAt.EnsureCapacity(capacity);
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
        ClearText(ref HtmlDescription, slot);
        ClearText(ref TranscriptUrl, slot);
        ClearText(ref TranscriptLanguage, slot);
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
    public bool ExplicitCompleted => T.ExplicitCompleted[Slot];
    public bool Completed => ExplicitCompleted || ProgressMs == int.MaxValue || Rules.Completed(ProgressMs, DurationMs);
    public int Season => T.Season[Slot];
    public string HtmlDescription => Entities.Strings.Resolve(T.HtmlDescription[Slot]);
    public string TranscriptUrl => Entities.Strings.Resolve(T.TranscriptUrl[Slot]);
    public string TranscriptLanguage => Entities.Strings.Resolve(T.TranscriptLanguage[Slot]);
    public bool TranscriptReadAlong => T.TranscriptReadAlong[Slot];
    public Show Show => new(T.Show[Slot]);
    public int ShowSlot => T.Show[Slot];
    /// <inheritdoc cref="EpisodeTable.Number"/>
    public int Number => T.Number[Slot];
    public EpisodeKind Kind => (EpisodeKind)T.Variant[Slot];
    public EpisodeFlags Flags => (EpisodeFlags)T.Flags[Slot];
    public bool IsAudiobookChapter => IsValid && (Flags & EpisodeFlags.AudiobookChapter) != 0;
    /// <inheritdoc cref="EpisodeTable.PlayedAt"/>
    public int PlayedAt => T.PlayedAt[Slot];

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
    public TextRef Title, Image, Description, HtmlDescription, TranscriptUrl, TranscriptLanguage;
    /// <summary>The show this episode belongs to — staged thin so the row can name it before the fetch.</summary>
    public StagedId ShowUri;
    public int DurationMs, PublishedAt, ProgressMs;
    /// <summary>Identity: the number, the <see cref="EpisodeKind"/> as a byte, the <see cref="EpisodeFlags"/>.</summary>
    public ushort Number, Season;
    public bool TranscriptReadAlong, ExplicitCompleted;
    public long RevisionUpdateSeconds, RevisionCreateSeconds, CompletionAtMs;
    public int RevisionUpdateNanos, RevisionCreateNanos;
    public byte Kind;
    public uint Flags;
    /// <summary>Progress: unix seconds of the newest resume-point revision, written with <see cref="ProgressMs"/>.</summary>
    public int PlayedAt;
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
                if ((known & (uint)EpisodeFields.Title) != 0) t.SetText(ref t.Title, slot, s.Intern(row.Title));
                if ((known & (uint)EpisodeFields.Image) != 0)
                {
                    // The bit is answered; the pixels may not go backwards (Detail.CoverLatch.AcceptsImage).
                    var incomingImage = s.Intern(row.Image);
                    if (Detail.CoverLatch.AcceptsImage(t.Image[slot], incomingImage))
                        t.SetText(ref t.Image, slot, incomingImage);
                }
                if ((known & (uint)EpisodeFields.Duration) != 0 && !t.Knows(slot, (uint)EpisodeFields.Media)) t.DurationMs[slot] = row.DurationMs;
                if ((known & (uint)EpisodeFields.Published) != 0) t.PublishedAt[slot] = row.PublishedAt;
                if (!row.ShowUri.IsEmpty) t.Show[slot] = s.Slot(Current.Shows, in row.ShowUri);
                t.Number[slot] = row.Number;
                t.Season[slot] = row.Season;
                t.Variant[slot] = row.Kind;
                uint identityFlags = (uint)EpisodeFlags.Short;
                // A thin title/image mention is not an answer to chapter classification. Only a
                // complete identity answer (or an explicit positive bit) may replace this fact.
                if ((known & (uint)EpisodeFields.Identity) == (uint)EpisodeFields.Identity
                    || (row.Flags & (uint)EpisodeFlags.AudiobookChapter) != 0)
                    identityFlags |= (uint)EpisodeFlags.AudiobookChapter;
                if (!t.Knows(slot, (uint)EpisodeFields.Media)) identityFlags |= (uint)(EpisodeFlags.Explicit | EpisodeFlags.Video);
                t.Flags[slot] = (t.Flags[slot] & ~identityFlags) | (row.Flags & identityFlags);
                t.Applied(slot, known & (uint)EpisodeFields.Identity, auth, ref t.IdentityAuthority);
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
                t.PlayedAt[slot] = row.PlayedAt;
                t.RevisionUpdateSeconds[slot] = row.RevisionUpdateSeconds;
                t.RevisionUpdateNanos[slot] = row.RevisionUpdateNanos;
                t.RevisionCreateSeconds[slot] = row.RevisionCreateSeconds;
                t.RevisionCreateNanos[slot] = row.RevisionCreateNanos;
                t.Applied(slot, (uint)EpisodeFields.Progress, auth, ref t.ProgressAuthority);
            }
            if ((known & (uint)EpisodeFields.Media) != 0
                && t.Accepts(slot, (uint)EpisodeFields.Media, auth, in t.MediaAuthority))
            {
                const uint mediaFlags = (uint)(EpisodeFlags.Explicit | EpisodeFlags.Video);
                t.DurationMs[slot] = row.DurationMs;
                t.Flags[slot] = (t.Flags[slot] & ~mediaFlags) | (row.Flags & mediaFlags);
                t.Applied(slot, (uint)EpisodeFields.Media, auth, ref t.MediaAuthority);
            }
            if ((known & (uint)EpisodeFields.Detail) != 0
                && t.Accepts(slot, (uint)EpisodeFields.Detail, auth, in t.DetailAuthority))
            {
                const uint detailFlags = (uint)(EpisodeFlags.Paywalled | EpisodeFlags.PreviewOnly | EpisodeFlags.Unplayable);
                t.Flags[slot] = (t.Flags[slot] & ~detailFlags) | (row.Flags & detailFlags);
                t.SetText(ref t.HtmlDescription, slot, s.Intern(row.HtmlDescription));
                t.Applied(slot, (uint)EpisodeFields.Detail, auth, ref t.DetailAuthority);
            }
            if ((known & (uint)EpisodeFields.Transcript) != 0
                && t.Accepts(slot, (uint)EpisodeFields.Transcript, auth, in t.TranscriptAuthority))
            {
                const uint transcriptFlag = (uint)EpisodeFlags.HasTranscript;
                t.Flags[slot] = (t.Flags[slot] & ~transcriptFlag) | (row.Flags & transcriptFlag);
                t.SetText(ref t.TranscriptUrl, slot, s.Intern(row.TranscriptUrl));
                t.SetText(ref t.TranscriptLanguage, slot, s.Intern(row.TranscriptLanguage));
                t.TranscriptReadAlong[slot] = row.TranscriptReadAlong;
                t.Applied(slot, (uint)EpisodeFields.Transcript, auth, ref t.TranscriptAuthority);
            }
            if ((known & (uint)EpisodeFields.Completion) != 0 && row.CompletionAtMs >= t.CompletionAtMs[slot])
            {
                t.ExplicitCompleted[slot] = row.ExplicitCompleted;
                t.CompletionAtMs[slot] = row.CompletionAtMs;
                t.Applied(slot, (uint)EpisodeFields.Completion, auth, ref t.CompletionAuthority);
            }
        }
    }
}

// ── persistence (Store.cs's per-kind seam) ───────────────────────────────────────────────────────────────────────────

/// <summary>How an episode survives a restart. Persists all three groups <c>EpisodeTable</c> carries: identity, the
/// about clamp, AND the resume position — <see cref="EpisodeFields.Progress"/> is the one cold field worth caching,
/// since losing it on every relaunch would silently rewind a listener's place (its own <c>progress_auth</c> column,
/// same as memory: a stale server position must never rewind what THIS device just played to, D16). The podcast
/// rework's four columns are APPENDED — number, kind and flags with the identity, <c>played_at</c> with the progress —
/// which moves the DDL fingerprint: that is the schema bump (Store.cs, a new fingerprint names a new file).
/// <para><b>AUTHORITY IS PER GROUP, BOTH WAYS.</b> <see cref="Save"/> writes each group's authority into that group's own
/// column (<c>identity_auth</c>, <c>about_auth</c>, <c>progress_auth</c>; the upsert keeps the max of each), and
/// <see cref="Load"/> restores each group at ITS column's rung — one staged row per distinct authority
/// (<see cref="RunsOf"/>), each speaking only for its groups. Restoring the whole row at the highest rung, as the load
/// did first, froze an episode: the player's Local progress lifted the reloaded identity and about clamp to Local too,
/// and every later wire refresh of either bounced off <c>Table.Accepts</c> for good.</para>
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
        new("number", StoreType.Int),                   // 10 ┐
        new("kind", StoreType.Int),                     // 11 ├ identity
        new("flags", StoreType.Int),                    // 12 ┘
        new("played_at", StoreType.Int),                // 13   progress
        new("season", StoreType.Int),
        new("html_description", StoreType.Text),
        new("transcript_url", StoreType.Text),
        new("transcript_language", StoreType.Text),
        new("transcript_read_along", StoreType.Int),
        new("detail_flags", StoreType.Int),
        new("detail_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("explicit_completed", StoreType.Int),
        new("completion_at_ms", StoreType.Int),
        new("completion_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("revision_update_seconds", StoreType.Int),
        new("revision_update_nanos", StoreType.Int),
        new("revision_create_seconds", StoreType.Int),
        new("revision_create_nanos", StoreType.Int),
        new("transcript_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("transcript_flags", StoreType.Int),
        new("media_duration_ms", StoreType.Int),
        new("media_flags", StoreType.Int),
        new("media_auth", StoreType.Int, StoreColumnFlags.Authority),
    ];

    const uint PersistedFields = (uint)EpisodeFields.All;

    public override EntityKind Kind => EntityKind.Episode;
    public override string Table => "episode";
    public override ReadOnlySpan<StoreColumn> Columns => Cols;

    /// <summary>Write every staged episode: each group's columns when the row carries that group, NULL otherwise (the
    /// upsert's "said nothing"), and each group's authority into ITS column — as a number either way. An authority
    /// column is never bound NULL: the upsert merges it as <c>max(col, coalesce(excluded.col, 0))</c>, and sqlite's
    /// multi-argument <c>max()</c> is NULL when ANY argument is, so a row inserted without a group would have kept a NULL
    /// rung for that group forever — every later write of it merging to NULL, and <see cref="Load"/> restoring it at
    /// <see cref="Authority.None"/>. 0 is the same "nothing" to the merge and a real number to <c>max()</c>.</summary>
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
            int authority = (int)row.Authority;

            if (identity)
            {
                w.Text(0, row.Title);
                w.Text(1, row.Image);
                w.Id(2, s, row.ShowUri);
                w.Int(3, row.DurationMs);
                w.Int(4, row.PublishedAt);
                w.Int(10, row.Number);
                w.Int(11, row.Kind);
                // Partial identity mentions carry no negative classification answer. NULL preserves
                // the complete metadata flags through the store's column-coalescing upsert.
                if ((known & (uint)EpisodeFields.Identity) == (uint)EpisodeFields.Identity) w.Int(12, row.Flags);
                else w.Null(12);
            }
            else { w.Null(0); w.Null(1); w.Null(2); w.Null(3); w.Null(4); w.Null(10); w.Null(11); w.Null(12); }
            w.Int(7, identity ? authority : 0);

            if (about) w.Text(5, row.Description);
            else w.Null(5);
            w.Int(8, about ? authority : 0);

            if (progress)
            {
                w.Int(6, row.ProgressMs);
                w.Int(13, row.PlayedAt);
            }
            else { w.Null(6); w.Null(13); }
            w.Int(9, progress ? authority : 0);

            if (identity) w.Int(14, row.Season); else w.Null(14);
            bool detail = (known & (uint)EpisodeFields.Detail) != 0;
            if (detail)
            {
                w.Text(15, row.HtmlDescription); w.Int(19, row.Flags & (uint)(EpisodeFlags.Paywalled | EpisodeFlags.PreviewOnly | EpisodeFlags.Unplayable));
            }
            else { w.Null(15); w.Null(19); }
            bool transcript = (known & (uint)EpisodeFields.Transcript) != 0;
            if (transcript)
            {
                w.Text(16, row.TranscriptUrl); w.Text(17, row.TranscriptLanguage);
                w.Int(18, row.TranscriptReadAlong ? 1 : 0); w.Int(29, row.Flags & (uint)EpisodeFlags.HasTranscript);
            }
            else { w.Null(16); w.Null(17); w.Null(18); w.Null(29); }
            w.Int(28, transcript ? authority : 0);
            w.Int(20, detail ? authority : 0);
            bool completion = (known & (uint)EpisodeFields.Completion) != 0;
            if (completion) { w.Int(21, row.ExplicitCompleted ? 1 : 0); w.Int(22, row.CompletionAtMs); }
            else { w.Null(21); w.Null(22); }
            w.Int(23, completion ? authority : 0);
            if (progress)
            {
                w.Int(24, row.RevisionUpdateSeconds); w.Int(25, row.RevisionUpdateNanos);
                w.Int(26, row.RevisionCreateSeconds); w.Int(27, row.RevisionCreateNanos);
            }
            else { for (int c = 24; c <= 27; c++) w.Null(c); }
            bool media = (known & (uint)EpisodeFields.Media) != 0;
            if (media) { w.Int(30, row.DurationMs); w.Int(31, row.Flags & (uint)(EpisodeFlags.Explicit | EpisodeFlags.Video)); }
            else { w.Null(30); w.Null(31); }
            w.Int(32, media ? authority : 0);
            w.Emit(row.Id, known, Entities.Now, Entities.Now);
        }
    }

    /// <summary>Restore one persisted row as one staged row PER AUTHORITY (<see cref="RunsOf"/>): the key is copied into
    /// the arena once and shared, and each run's row carries only its own groups' columns — so the commit applies every
    /// group at the rung that wrote it (the class note says why). A row persisted with nothing known restores nothing.</summary>
    public override void Load(RowReader r, Staging into)
    {
        Span<GroupRun> runs = stackalloc GroupRun[7];
        int n = RunsOf(r.Known & PersistedFields, (Authority)r.Int(7), (Authority)r.Int(8), (Authority)r.Int(9), runs, (Authority)r.Int(20), (Authority)r.Int(23), (Authority)r.Int(28), (Authority)r.Int(32));
        if (n == 0) return;
        StagedId id = r.Uri;
        for (int k = 0; k < n; k++)
        {
            uint groups = runs[k].Groups;
            ref StagedEpisode row = ref into.Episodes.RowFor(in id, runs[k].Authority, groups);
            if ((groups & (uint)EpisodeFields.Identity) != 0)
            {
                row.Title = r.Text(0);
                row.Image = r.Text(1);
                row.ShowUri = r.Text(2);
                row.DurationMs = (int)r.Int(3);
                row.PublishedAt = (int)r.Int(4);
                row.Number = (ushort)r.Int(10);
                row.Season = (ushort)r.Int(14);
                row.Kind = (byte)r.Int(11);
                row.Flags = (uint)r.Int(12);
            }
            if ((groups & (uint)EpisodeFields.About) != 0) row.Description = r.Text(5);
            if ((groups & (uint)EpisodeFields.Progress) != 0)
            {
                row.ProgressMs = (int)r.Int(6);
                row.PlayedAt = (int)r.Int(13);
                row.RevisionUpdateSeconds = r.Int(24); row.RevisionUpdateNanos = (int)r.Int(25);
                row.RevisionCreateSeconds = r.Int(26); row.RevisionCreateNanos = (int)r.Int(27);
            }
            if ((groups & (uint)EpisodeFields.Detail) != 0)
            {
                row.HtmlDescription = r.Text(15); row.Flags |= (uint)r.Int(19);
            }
            if ((groups & (uint)EpisodeFields.Transcript) != 0)
            {
                row.TranscriptUrl = r.Text(16); row.TranscriptLanguage = r.Text(17);
                row.TranscriptReadAlong = r.Int(18) != 0; row.Flags |= (uint)r.Int(29);
            }
            if ((groups & (uint)EpisodeFields.Media) != 0)
            {
                row.DurationMs = (int)r.Int(30);
                row.Flags = (row.Flags & ~(uint)(EpisodeFlags.Explicit | EpisodeFlags.Video)) | (uint)r.Int(31);
            }
            if ((groups & (uint)EpisodeFields.Completion) != 0)
            {
                row.ExplicitCompleted = r.Int(21) != 0; row.CompletionAtMs = r.Int(22);
            }
        }
    }

    /// <summary>One run of a restored row: the groups it speaks for, at the one authority they were all written at.</summary>
    public readonly record struct GroupRun(uint Groups, Authority Authority);

    /// <summary>PURE: a persisted row's groups (<paramref name="known"/>) split into runs of EQUAL authority, in group order
    /// — Identity, About, Progress — with groups that share a rung sharing a run (the usual row: one Full answer wrote
    /// identity and about, the player wrote progress at Local ⇒ two runs). A group not in <paramref name="known"/> is not
    /// restored, whatever its column says. Writes at most three runs into <paramref name="into"/>; returns the count.</summary>
    public static int RunsOf(uint known, Authority identity, Authority about, Authority progress, Span<GroupRun> into, Authority detail = Authority.None, Authority completion = Authority.None, Authority transcript = Authority.None, Authority media = Authority.None)
    {
        int n = 0;
        Add(known & (uint)EpisodeFields.Identity, identity, into, ref n);
        Add(known & (uint)EpisodeFields.About, about, into, ref n);
        Add(known & (uint)EpisodeFields.Progress, progress, into, ref n);
        Add(known & (uint)EpisodeFields.Detail, detail, into, ref n);
        Add(known & (uint)EpisodeFields.Completion, completion, into, ref n);
        Add(known & (uint)EpisodeFields.Transcript, transcript, into, ref n);
        Add(known & (uint)EpisodeFields.Media, media, into, ref n);
        return n;

        static void Add(uint groups, Authority authority, Span<GroupRun> into, ref int n)
        {
            if (groups == 0) return;
            for (int i = 0; i < n; i++)
            {
                if (into[i].Authority != authority) continue;
                into[i] = new GroupRun(into[i].Groups | groups, authority);
                return;
            }
            into[n++] = new GroupRun(groups, authority);
        }
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
        public const float PlayedCeiling = 1f;

        /// <summary>The four-way status filter, in the SelectorBar's order (0 All · 1 Unplayed · 2 In progress · 3 Played).</summary>
        public enum Status : byte { All = 0, Unplayed = 1, InProgress = 2, Played = 3 }

        /// <summary><c>clamp(progress / duration, 0, 1)</c>; 0 when the duration is not known (EpisodeList.cs:40).</summary>
        public static float Pct(int progressMs, int durationMs)
            => durationMs > 0 ? Math.Clamp(progressMs / (float)durationMs, 0f, 1f) : 0f;

        /// <summary>A row's fraction. Progress UNKNOWN reads 0 — an unplayed card, never a half-drawn bar (ch 09 §7).</summary>
        public static float PctOf(Episode e) => e.Completed ? 1f : e.Knows(EpisodeFields.Progress) ? Pct(e.ProgressMs, e.DurationMs) : 0f;

        public static bool InProgress(float pct) => pct > InProgressFloor && pct < PlayedCeiling;

        public static bool Played(float pct) => pct >= PlayedCeiling;

        /// <summary>The tail that counts as finished: 30 s (podcast plan D-5).</summary>
        public const int CompletedTailMs = 30_000;

        /// <summary>THE completion rule (D-5; the WinUI app had three — 90 s, 30 s, 0.995): at or past
        /// <see cref="PlayedCeiling"/>, or no more than <see cref="CompletedTailMs"/> left. Never with the duration
        /// unknown, and never at a position of 0 — without that guard every unplayed episode of 30 s or less would read
        /// finished. Long arithmetic, so no position can wrap the tail test. Note it is WIDER than
        /// <see cref="Played(float)"/> under 25 minutes, where 30 s is less than the last 2 %.</summary>
        public static bool Completed(int progressMs, int durationMs)
            => durationMs > 0 && progressMs >= durationMs;

        /// <summary>Which arm of herodotus's <c>CurrentStateValue.state</c> oneof a revision carries. The numbers ARE the
        /// wire's field numbers (the official client 1.2.96.518, captured 2026-09-19: findings-podcast-wire.md §3.2,
        /// §4.1). Markers 3 and 4 are PROVISIONAL readings of one sample each; a capture of mark played / mark unplayed /
        /// a natural end settles them.</summary>
        public enum ResumeArm : byte
        {
            /// <summary>No arm. 0 of 31 captured episode states lacked one: it says nothing, so it claims nothing —
            /// never "completed" (the retired reading of an omitted resume point).</summary>
            None = 0,
            /// <summary>A position (<c>google.protobuf.Duration</c>, the caller has scaled it to ms); <c>{}</c> = 0 =
            /// NOT_STARTED.</summary>
            Position = 2,
            /// <summary>Empty. The official desktop wrote it the moment a fresh play of an episode began: started, no
            /// position yet — in progress at 0, never completed.</summary>
            Marker3 = 3,
            /// <summary>Empty. Its one sample is stamped exactly its episode's duration after a plausible start: finished.</summary>
            Marker4 = 4,
            /// <summary>An album/playlist context resume — never an episode's own position.</summary>
            Context = 12,
        }

        /// <summary>THE herodotus fold for one episode revision: <see cref="ResumeArm.Position"/> ⇒ the position, clamped
        /// to [0, int.MaxValue]; <see cref="ResumeArm.Marker3"/> ⇒ 0 (started, not completed; any position is ignored);
        /// <see cref="ResumeArm.Marker4"/> ⇒ completed — the full duration, or <see cref="int.MaxValue"/> while the
        /// duration is not resident, which <see cref="Pct"/> clamps to 1 the moment it lands (plan §5.8; a duration-less
        /// 1 would read UNPLAYED then). <see cref="ResumeArm.None"/> and <see cref="ResumeArm.Context"/> return false:
        /// the row's progress stays UNKNOWN (§6.1), never a guessed "unplayed" or "completed".</summary>
        public static bool ProgressOf(ResumeArm arm, long positionMs, int durationMs, out int progressMs)
        {
            switch (arm)
            {
                case ResumeArm.Position: progressMs = (int)Math.Clamp(positionMs, 0L, int.MaxValue); return true;
                case ResumeArm.Marker3: progressMs = 0; return true;
                case ResumeArm.Marker4: progressMs = durationMs > 0 ? durationMs : int.MaxValue; return true;
                default: progressMs = 0; return false;
            }
        }

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
