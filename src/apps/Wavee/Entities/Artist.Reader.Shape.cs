// ── Entities/Artist.Reader.Shape.cs — the library's artist reader: its SHAPE (order, extents, the two width arms) ──
//
// Role: CORE (ReaderSort, ReaderBlock, ReaderShapeKey, ReaderShape). Engine-free: no FluentGpu type is touched here, so
//       `ArtistReaderShapeTests` and `ArtistReaderHeadLayoutTests` read every rule in it without a host.
// Owner: N (N-a wrote the order and the extents); the two width arms are track L (#158)
// Wave: L1 (CORE); split out of `Artist.Reader.cs` on 2026-10-01, the split that file's budget note had owed since
//       2026-09-18 — the UI half (ReaderProps, Reader, its band, blocks and cells) stays there
// Spec: docs/plans/wavee/library-rework-implementation.md §5.6 (the order and the extents);
//       docs/plans/wavee/library-reader-narrow-heads-implementation.md §1, §3.1-§3.2 (the arms, the heads, the band)
//
// WHAT THE CORE HALF IS. The reader replaces the three-column artists layout: navigator (280) │ one scroller whose items are
// ALBUM BLOCKS (art + the album's tracks). Everything the scroller needs to lay itself out before a single row renders —
// which blocks exist, in which order, how tall each one is, and one string that identifies the sequence — is decided
// here, as pure functions over slots (the `derived-facts-live-on-the-model` rule: the UI renders the shape, it never
// derives it per render, and it never probes data state to guess a count).
//
// WHAT "IN YOUR LIBRARY" MEANS HERE (the 2026-09-18 correction, User.cs §11). A library block is a release you HOLD, and
// that is two groups, not one: the SAVED albums billed to the artist (the block lists the whole record) and then the
// albums holding at least one LIKED track credited to it (the block lists only those tracks — `ReaderBlock.LikedOnly`).
// They sort together, because they answer the same question. An account that hearts songs and saves no albums used to
// get an empty reader; that was the defect.
//
// THE ORDER IS TOTAL, ON PURPOSE (§5.6). Library blocks first, then — scope 1 — the catalogue union; inside each group
// the sort runs, with unknown years sinking as a block; ties break by title, then by uri, then by the source index.
// Two reasons it must be total rather than "good enough": `Span.Sort` is an UNSTABLE introsort, so without a final
// tie-break the same set could come back permuted differently on two computes; and `OrderKey` (§6 rule 7) hashes the
// SEQUENCE — a permutation that carried no new information must still compare EQUAL. Until the 2026-09-18
// stabilization pass `OrderKey` was ALSO the reader's remount key; it no longer is (library-stabilization-plan.md R2:
// the mount key is `ReaderMountPolicy.MountKey`, minted from artist/scope/sort/narrow alone, so a reshape never
// remounts). `OrderKey` still has to be total and deterministic for what it now does: it is what the UI dedupes its
// `library.reader.shape` log on, and R4's `reason=`/`firstDiff=` fields are computed by diffing the SEQUENCE it
// hashes against the one from the last log — an unstable sort's phantom permutation must not read as a "reorder".
//
// WHEN IT RUNS, AND WHAT IT ALLOCATES. A compute happens on an EDGE: the selected artist changes, the scope or sort word
// is tapped, or a table publishes (a facet page landing, a track list arriving). Never per frame. Even so it allocates
// nothing after warm-up: every buffer is the caller's pooled array, the block sorter is one cached instance per thread
// with its three `Comparison<int>` delegates allocated in its constructor (the `LibraryNavSorter` pattern, User.cs:686),
// and both the title read (`Entities.Strings.Resolve` hands back the interned string) and the uri tie-break
// (`LibraryRows.UriOf` formats a gid into a stack buffer) are allocation-free.
//
// DEVIATIONS FROM §5.6, and why (reported to the orchestrator):
//   1. `Build` takes ARRAYS (`int[]`, `ReaderBlock[]`) where the plan wrote `Span<…>`, and the plan's `BlockComparer`
//      struct — which had to `slots.ToArray()` because a `Span<int>` cannot be a field of a non-ref struct — is a cached
//      `BlockSorter` class over the caller's array instead. Same inputs, one fewer copy, and no `IComparer<int>` boxed
//      per compute. The three facet lists stay `ReadOnlySpan<int>`: they come straight off `EdgeTable.Targets(slot)` and
//      are only ever read inline, so nothing has to capture them.
//   2. The uri tie-break is `LibraryRows.UriOf(id, scratch).SequenceCompareTo(…)`, not `Uri.Text.CompareTo(…)`:
//      `EntityUri.Text` MATERIALISES a string for a gid-form id (Entities.cs:387 says so in its own doc), which would
//      have been one allocation per comparison — O(n log n) of them inside a sort.
//   3. Three small additions the UI half would otherwise have to invent per render: `ShimmerRows` (the plan's literal
//      `4`, named because the test and the skeleton both need it), `SortOf(int)` (the persisted-code clamp §4 and §5.7
//      describe: the key is the old `LibraryAlbumSort("artists")`, whose discography values 0..4 clamp to Newest), and
//      `Capacity(…)` (the one place that says how big the pooled buffers must be).
//   4. `Build` takes the library group as THREE parallel arrays (slots · `likedOnly` · `likedRows`) and COMPACTS all
//      three in place when it drops an invalid slot, rather than taking a record per release. A `ReaderBlock[]` sorted
//      directly would need a second block buffer to permute into, and a `(int, bool, int)[]` would be a fresh array per
//      compute; the parallel arrays are the caller's pooled ones and the permutation indexes all three at once.
//   5. `ReaderShapeKey` carries `TotalReleases` and `Songs`, which §5.6 left as live reads on the rail and the band.
//      Both were FROZEN in practice: `Prop.Of(() => EdgeTable.Total(_artist))` reads no signal and the rail element is
//      cached across artists, so "all releases · N" kept the first artist's number forever. On the key they ride the
//      shape memo, and a memo read inside a `Prop` thunk is tracked.
//
// THE TWO WIDTH ARMS (#158, library-reader-narrow-heads-implementation.md). The WIDE library arm routinely hands the
// reader 344-650 DIP (a non-maximized 1080p window with the rail open), and there the old one-row band and one-row album
// head left the 32-px artist name ~79 DIP ("Tro…") and the 20-px title 3-42 DIP ("P…", "Ta…"): every control was
// `Shrink 0` and the text was the only flexible child. A thinner row was tried first (W6's compact band, 2026-09-18) and
// saved ~170 DIP, which was not enough — so both rows STACK instead, each on one reader-width edge with a 24-DIP exit
// hysteresis:
//   · NARROW (< 640): the block cover drops 120 → 88 and the album head stacks — the title on a line of its own over
//     the meta and the three circles. A list mount input (`ReaderMountPolicy.MountKey`), so its extents seed fresh.
//   · MEDIUM (< MediumBelow): the cover spine is gone and the band stacks — avatar + a two-line name over the SAME verbs
//     the wide arm has. NOT a mount input: the band is item 0, a persistent-prefix row, corrected in place in `Settle`.
// The edges are DERIVED, not chosen, and the tests pin each derivation: the inline head keeps `TitleFloorW` at the narrow
// edge, the wide band leaves the name `NameMinW` at the medium one. The wide band shares the lane with the 56-DIP spine
// (one signal drives both), so that edge is `WideBandFixedW + SpineW + NameMinW` = 772 — the plan's "484 + 232 = 716 ≤
// 720" left the spine out, and at 720 the name would have had 180 DIP.
// Every height below is a STATED constant: the tree declares it as its `Height` and the estimator reads the same field,
// so renderer == estimator by identity rather than by measurement (`library.reader.chrome` logs any exception).

using System.Globalization;

namespace Wavee;

// ══ CORE: the reader's shape ═════════════════════════════════════════════════════════════════════════════════════════

public readonly partial struct Artist
{
    /// <summary>The reader's own sort — a reader-local enum, persisted through <c>LibraryAlbumSort("artists")</c>; old
    /// discography values (0..4) clamp to <see cref="Newest"/> (<see cref="ReaderShape.SortOf"/>). Never renumber.</summary>
    public enum ReaderSort : byte { Newest = 0, Oldest = 1, Alphabetical = 2 }

    /// <summary>What one block is: the album, whether it is IN YOUR LIBRARY (<paramref name="Saved"/> — library blocks
    /// precede catalogue blocks in every sort; it covers BOTH library groups, the saved albums and the liked-only ones),
    /// how many rows it lays out (<c>TrackCount</c> when known, else the listed edge length, else
    /// <see cref="ReaderShape.ShimmerRows"/> — the counted skeleton; for a liked-only block it is the LIKED-track count
    /// exactly), whether its track list failed (the head plus a Retry note, never a silent empty block), and whether it
    /// is a LIKED-ONLY release.
    /// <para><paramref name="LikedOnly"/> is group 2 of <see cref="User.LibraryReleasesOf"/>: an album you did not save
    /// but hold liked tracks of. Its block lists ONLY those tracks (<see cref="User.LikedTracksOfAlbum"/>), so its
    /// <c>AlbumTracks</c> edge is irrelevant to it — it neither demands it nor waits on it, and it can never carry the
    /// failure bit of a list it does not read.</para></summary>
    public readonly record struct ReaderBlock(int AlbumSlot, bool Saved, int Rows, bool Failed, bool LikedOnly);

    /// <summary>The reader's shape as a VALUE (the remount key + the counts); the blocks themselves live in the reader's
    /// pooled buffer, because a shape is recomputed far more often than it changes.
    /// <para><see cref="Empty"/> is the pre-mount sentinel. Its <c>OrderKey</c> is the literal <c>"0"</c>, which no real
    /// shape can produce (a real key is always <c>count ":" hex16</c>) — so the first real compute, even of a genuinely
    /// empty artist, is seen as a change exactly once.</para>
    /// <para><paramref name="TotalReleases"/> and <paramref name="Songs"/> are on the KEY rather than read live by the
    /// rail and the band (deviation 5): both are answers to "how much is there", both move when an edge lands, and both
    /// were frozen when they were a plain read of a field inside a cached element's thunk — the rail's "all releases ·
    /// N" stuck at the first artist's number for the life of the reader. A memo read inside a <c>Prop</c> thunk IS
    /// tracked, so carrying them here is what makes them live.</para></summary>
    public sealed record ReaderShapeKey(int Count, int Library, string OrderKey, int Scope, int Sort,
                                        int TotalReleases, int Songs)
    {
        public static readonly ReaderShapeKey Empty = new(0, 0, "0", 0, 0, 0, 0);
    }

    /// <summary>PURE: the block order and extents, and the reader's two width arms with the geometry each one states.
    /// Library blocks first (Scope 0 shows only them), then — Scope 1 — the union of the three facet edges minus the
    /// saved ones; inside each group the sort applies: Newest = year desc, unknown years sink; Oldest = year asc,
    /// unknown years sink; Alphabetical = title (OrdinalIgnoreCase), then uri. The source index is the tie-break of last
    /// resort, so the order is total and the key deterministic.</summary>
    public static class ReaderShape
    {
        // ── the two reader-width edges (#158; moved from the Reader's SpineHideBelow / NarrowBelow / BreakHysteresis) ─

        /// <summary>The narrow edge: under it the block cover drops 120 → 88 and the album head STACKS. The library's
        /// own 640 (`LibraryLayoutBreakpoints.Collapsed`; WinUI's 641 compact threshold), and already sufficient for the
        /// inline head — at 640 its title keeps <see cref="TitleFloorW"/> with room to spare (pinned by a test).</summary>
        public const float NarrowBelow = 640f;
        /// <summary>Both edges' exit band: an arm entered under its edge holds until the width clears the edge by this
        /// much, so dragging the grip across an edge cannot oscillate the layout (the page's own rule).</summary>
        public const float BreakHysteresis = 24f;

        /// <summary>Under 640 the block cover drops 120 → 88 and the album head STACKS; 24-DIP hysteresis on exit.
        /// An unmeasured width keeps the previous answer (the library page's own rule).</summary>
        public static bool Narrow(float w, bool was) => w <= 0f ? was : was ? w < NarrowBelow + BreakHysteresis : w < NarrowBelow;
        /// <summary>Under <see cref="MediumBelow"/> the cover spine is gone AND the band folds to its stacked arm; the
        /// same hysteresis and the same unmeasured rule.</summary>
        public static bool Medium(float w, bool was) => w <= 0f ? was : was ? w < MediumBelow + BreakHysteresis : w < MediumBelow;

        // ── block geometry (literals, not Spacing.*: this class is engine-free; the tree reads these same fields) ─────

        public const float BlockPadTop = 16f, BlockPadBottom = 8f, CoverEdge = 120f, CoverEdgeNarrow = 88f, RowH = 36f, RowsPadBottom = 8f, Divider = 1f;
        public const float BlockPadLeft = 16f, BlockCoverGap = 16f, BlockPadRight = 20f;
        /// <summary>Under the art: the gap and the one 12/16 "2022 · Album" caption line.</summary>
        public const float CoverCaptionGap = 8f, CaptionLine = 16f;
        /// <summary>The cover spine's width. It stands beside the list, in the same lane, exactly while the reader is NOT
        /// <see cref="Medium"/> — so every width the band or a block gets is the reader's less this, in that arm.</summary>
        public const float SpineW = 56f;
        public static float CoverOf(bool narrow) => narrow ? CoverEdgeNarrow : CoverEdge;
        /// <summary>The width the band and every block lay out in: the reader, less the spine when it stands.</summary>
        public static float ListW(float readerW, bool medium) => medium ? readerW : readerW - SpineW;
        /// <summary>What a block leaves its head + rows: the list width less the block's padding, its cover and the gap.
        /// <paramref name="narrow"/> implies <paramref name="medium"/> — the narrow exit (664) is under the medium edge.</summary>
        public static float BodyWidth(float readerW, bool narrow, bool medium)
            => ListW(readerW, medium) - BlockPadLeft - CoverOf(narrow) - BlockCoverGap - BlockPadRight;

        // ── the album head: one row (inline) or the title over the meta + commands (stacked, under the narrow edge) ────

        /// <summary>The inline head's stated height (W3's, unchanged): the 30-DIP title link and the 32 circles, centred.</summary>
        public const float HeadH = 44f;
        public const float HeadCircle = 32f, HeadGlyph = 15f, HeadGap = 10f;
        /// <summary>The title is 20/26/600 and its link pads 2 DIP above and below it.</summary>
        public const float HeadTitleLine = 26f, HeadLinkInsetY = 4f;
        /// <summary>The title link's horizontal inset: 4 DIP of padding each side, the LEFT one cancelled by a −4 margin
        /// (the hover plate overhangs, the text stays flush with the rows) — so the text is its slot less 4.</summary>
        public const float HeadLinkInsetX = 4f;
        public const float HeadTitleRowH = HeadTitleLine + HeadLinkInsetY;                                  // 30
        public const float HeadStackGap = 6f, HeadStackPadBottom = 4f;
        /// <summary>The stacked head: the title row, the gap, the commands row and a bottom pad — exactly two track rows.</summary>
        public const float HeadStackedH = HeadTitleRowH + HeadStackGap + HeadCircle + HeadStackPadBottom;   // 72 = 2 × RowH
        public static float HeadHeight(bool narrow) => narrow ? HeadStackedH : HeadH;
        /// <summary>▶ ♡ ⋯ — three circles and the two gaps between them.</summary>
        public const float HeadCommandsW = 3f * HeadCircle + 2f * HeadGap;                                  // 116
        /// <summary>The inline title SLOT never goes under this (its flex <c>MinWidth</c>); the meta shrinks first.</summary>
        public const float TitleFloorW = 120f;
        /// <summary>The title TEXT's width: the whole body (stacked), or the body less the meta, the commands and the two
        /// gaps around the meta (inline — the title slot is that row's `Grow 1 / Basis 0` filler); the link inset either way.</summary>
        public static float TitleAvailW(float readerW, bool narrow, bool medium, float metaW)
            => (narrow ? BodyWidth(readerW, true, true)
                       : BodyWidth(readerW, false, medium) - HeadGap - metaW - HeadGap - HeadCommandsW) - HeadLinkInsetX;
        /// <summary>The stacked head's meta: the body less the commands and the one gap before them (the row is
        /// `Justify End`, so there is no spacer and no spacer's gap to pay for).</summary>
        public static float StackedMetaAvailW(float readerW) => BodyWidth(readerW, true, true) - HeadGap - HeadCommandsW;

        // ── the band: one row (wide) or avatar + name over the same verbs (stacked, under the medium edge) ────────────

        public const float AvatarEdge = 72f, AvatarEdgeStacked = 56f, BandCircle = 36f, IconActionW = 32f;
        /// <summary>The Follow slot's reserved width — the LONGER face ("Following" ≈ 106 DIP in en-US), so nothing beside
        /// it moves on a follow flip. A MinWidth in the tree, not a Width: a longer locale label still grows it.</summary>
        public const float FollowSlotW = 108f;
        /// <summary>Play all's floor: the primary CTA's own min-width, by reference rather than by copy.</summary>
        public const float PlayButtonMinW = ButtonRules.PrimaryWidthNominal;                                // 120
        public const float BandPadX = 20f, BandPadTop = 20f, BandPadBottom = 8f, BandGap = 16f;             // wide
        public const float BandStackPadX = 16f, BandStackPadTop = 16f, BandStackGap = 12f, BandStackRowGap = 8f;
        /// <summary>The stacked name may take two lines — auto-fitting 32 → 28 first, ellipsis after that.</summary>
        public const int NameLines = 2;
        /// <summary><c>ArtistCompactTitle</c>'s 32/40 line, the 2-DIP gap and the 13/18 "in your library" subline.</summary>
        public const float NameLine = 40f, NameSubGap = 2f, SubLine = 18f;
        public const float NameColumnH = NameLines * NameLine + NameSubGap + SubLine;                       // 100
        public const float BandH = BandPadTop + AvatarEdge + BandPadBottom;                                // 100 (unchanged)
        public const float BandStackedH = BandStackPadTop + NameColumnH + BandStackRowGap + BandCircle + BandPadBottom;   // 168
        public static float BandHeight(bool stacked) => stacked ? BandStackedH : BandH;
        /// <summary>Everything in the wide band's row that never shrinks: the padding, five gaps, the avatar, Play all at
        /// its floor, shuffle, the Follow slot and ↗.</summary>
        public const float WideBandFixedW = 2f * BandPadX + 5f * BandGap + AvatarEdge + PlayButtonMinW + IconActionW + FollowSlotW + IconActionW;   // 484
        /// <summary>The stacked arm's second row: the play FAB, shuffle, the Follow slot, ↗ and the three gaps.</summary>
        public const float StackedControlsW = BandCircle + IconActionW + FollowSlotW + IconActionW + 3f * BandStackGap;                           // 244
        /// <summary>The floor the WIDE band must leave the name: ≈ 13 display glyphs ("Florence + The…").</summary>
        public const float NameMinW = 232f;
        /// <summary>The medium edge, DERIVED: the narrowest reader whose wide band — which shares the lane with the spine —
        /// still leaves the name <see cref="NameMinW"/>. Also where the spine goes (one signal drives both).</summary>
        public const float MediumBelow = WideBandFixedW + SpineW + NameMinW;                               // 772
        /// <summary>The narrowest reader there is: <c>Shell.Host</c>'s window MinWidth.</summary>
        public const float MinReaderW = 300f;
        /// <summary>The name text's width: the list less the stacked row's padding, avatar and gap — or the wide row's fixed
        /// width. (The link's ±8 padding and margin cancel; the text is the name column.)</summary>
        public static float NameAvailW(float readerW, bool stacked)
            => ListW(readerW, stacked) - (stacked ? 2f * BandStackPadX + AvatarEdgeStacked + BandStackGap : WideBandFixedW);

        /// <summary>How many rows a block shimmers when nothing has answered its count yet — four, the plan's literal.
        /// It is a COUNTED skeleton either way (§6 rule 5): a bare card is never an answer.</summary>
        public const int ShimmerRows = 4;

        /// <summary>The persisted int (<c>library.artists.album.sort</c>) as a reader sort. The key is the discography's
        /// old one and its values ran 0..4, so anything outside this enum's three codes reads as Newest (§4, §5.7).</summary>
        public static ReaderSort SortOf(int persisted)
            => persisted is (int)ReaderSort.Oldest or (int)ReaderSort.Alphabetical ? (ReaderSort)persisted : ReaderSort.Newest;

        /// <summary>How long the caller's pooled <c>scratch</c> / <c>perm</c> / <c>into</c> buffers must be: every
        /// LIBRARY release (saved + liked-only) plus every listed one, before the union drops the duplicates. Said once,
        /// here, so the reader cannot disagree with <see cref="Build"/> about it (a short buffer silently truncates the
        /// shape — see Build).</summary>
        public static int Capacity(int libraryCount, int albums, int singles, int compilations)
            => Math.Max(0, libraryCount) + Math.Max(0, albums) + Math.Max(0, singles) + Math.Max(0, compilations);

        /// <summary>The analytic block extent: pad + max(cover + its caption, head + rows) + pad + divider, the head in its
        /// arm (<see cref="HeadHeight"/> — 44 inline, 72 stacked). The list's layout asks for this BEFORE the block
        /// renders (`RepeatLayout.Extents`), which is why it may not read anything the block's own measure would decide.</summary>
        public static float ExtentOf(in ReaderBlock b, bool narrow)
        {
            float cover = CoverOf(narrow) + CoverCaptionGap + CaptionLine;   // cover + gap + the "2022 · Album" caption
            float body = HeadHeight(narrow) + b.Rows * RowH + RowsPadBottom;
            return BlockPadTop + MathF.Max(cover, body) + BlockPadBottom + Divider;
        }

        /// <summary>Fills <paramref name="into"/> with the ordered blocks; returns the count (the library count in
        /// <paramref name="library"/>, so the UI can title the two groups without re-deriving "in your library").
        ///
        /// <para><paramref name="librarySlots"/> is <see cref="User.LibraryReleasesOf"/>'s answer — the artist's SAVED
        /// albums and then its LIKED-ONLY ones — with <paramref name="likedOnly"/> the parallel group bit and
        /// <paramref name="likedRows"/> the parallel <see cref="User.LikedTracksOfAlbum"/> count (read only where the bit
        /// is set; it is that block's row count exactly). The two groups sort TOGETHER: both are "in your library", and
        /// splitting them would put a liked-only 2024 release under a saved 1994 one under "newest".
        /// <paramref name="albums"/>/<paramref name="singles"/>/<paramref name="compilations"/> are the three facet
        /// edges' targets. <paramref name="scratch"/> holds the slot union, <paramref name="perm"/> the permutation and
        /// <paramref name="into"/> the blocks; all of them are the CALLER's pooled arrays, sized by
        /// <see cref="Capacity"/>. A buffer shorter than that truncates the shape rather than throwing: this runs on the
        /// UI thread inside a compute, where a partial list is a visible bug and an exception is a dead app.</para>
        ///
        /// <para><b><paramref name="likedOnly"/> and <paramref name="likedRows"/> are WRITTEN as well as read</b>
        /// (deviation 4): the valid library slots are copied into <paramref name="scratch"/>, and the two bit arrays are
        /// compacted in step with that copy — forward, so nothing is clobbered before it is read — which is what lets the
        /// permutation index one set of parallel rows. They are the caller's POOLED buffers, refilled from scratch on
        /// every compute and read by nobody after Build returns, so carrying the two bits costs no allocation at all.
        /// The caller's <paramref name="librarySlots"/> is never written.</para></summary>
        public static int Build(int artistSlot, int scope, ReaderSort sort,
                                int[] librarySlots, bool[] likedOnly, int[] likedRows, int libraryCount,
                                ReadOnlySpan<int> albums, ReadOnlySpan<int> singles, ReadOnlySpan<int> compilations,
                                int[] scratch, int[] perm, ReaderBlock[] into, out int library)
        {
            library = 0;
            if (artistSlot <= Table.None) return 0;                      // no artist, no shape (a cleared selection)
            int cap = Math.Min(into.Length, Math.Min(scratch.Length, perm.Length));
            if (cap <= 0) return 0;

            // 1. the library blocks — saved AND liked-only, sorted as one group. They lead in EVERY sort and every scope:
            //    "in your library" is the surface's subject, and a release you hold must not sink below a catalogue one
            //    because it happens to be older or later in the alphabet. Invalid slots are dropped here (compacting the
            //    parallel bits with them) so nothing downstream has to branch on them.
            int n = 0;
            int lib = Math.Min(libraryCount, Math.Min(librarySlots.Length, Math.Min(likedOnly.Length, likedRows.Length)));
            for (int i = 0; i < lib && n < cap; i++)
            {
                int s = librarySlots[i];
                if (s <= Table.None) continue;
                scratch[n] = s;
                likedOnly[n] = likedOnly[i];                             // n <= i always: a forward compaction, in place
                likedRows[n] = likedRows[i];
                n++;
            }
            Order(scratch, 0, n, perm, sort);
            for (int i = 0; i < n; i++)
            {
                int k = perm[i];
                into[i] = BlockOf(scratch[k], true, likedOnly[k], likedRows[k]);
            }
            library = n;
            if (scope == 0) return n;

            // 2. the catalogue blocks: the union of the three facets, minus everything already emitted above (a library
            //    release IS a library block — saved or liked-only; listing it twice would be two hearts on one record)
            //    and minus its own duplicates (a release can sit in two facets — a single re-issued on a compilation).
            int m = 0;
            m = Union(albums, scratch, n, m, cap - n);
            m = Union(singles, scratch, n, m, cap - n);
            m = Union(compilations, scratch, n, m, cap - n);
            Order(scratch, n, m, perm, sort);
            for (int i = 0; i < m; i++) into[n + i] = BlockOf(scratch[n + perm[i]], false, false, 0);
            return n + m;
        }

        /// <summary>One block off its album: the counted-skeleton row count and the failure bit. `TrackCount` is the
        /// album's own advertised count, the edge length is what actually landed, and four is the last resort.
        /// <para>A LIKED-ONLY block skips all three: its rows are the liked ones the caller counted, and the album's
        /// tracks edge — which nothing demands for it — can neither shorten that list nor fail it.</para></summary>
        static ReaderBlock BlockOf(int slot, bool saved, bool likedOnly, int likedRows)
        {
            if (likedOnly) return new ReaderBlock(slot, Saved: true, Rows: likedRows, Failed: false, LikedOnly: true);
            var a = new Album(slot);
            var edge = Entities.Current.Edges.AlbumTracks;
            int listed = edge.Count(slot);
            int rows = a.Knows(AlbumFields.TrackCount) && a.TrackCount > 0 ? a.TrackCount : listed > 0 ? listed : ShimmerRows;
            return new ReaderBlock(slot, saved, rows, edge.IsFailed(slot), LikedOnly: false);
        }

        /// <summary>Appends <paramref name="facet"/>'s new slots to <c>scratch[start + m …]</c>. The dedup scan covers
        /// <c>scratch[0 .. start + m]</c> — the library blocks AND what the earlier facets already contributed — so one
        /// linear scan answers both "already saved" and "already unioned". Facets are tens of entries, not thousands.</summary>
        static int Union(ReadOnlySpan<int> facet, int[] scratch, int start, int m, int room)
        {
            for (int i = 0; i < facet.Length && m < room; i++)
            {
                int s = facet[i];
                if (s <= Table.None || scratch.AsSpan(0, start + m).IndexOf(s) >= 0) continue;
                scratch[start + m++] = s;
            }
            return m;
        }

        /// <summary>Writes the permutation of <c>slots[offset .. offset + count]</c> into <c>perm[0 .. count]</c>.</summary>
        static void Order(int[] slots, int offset, int count, int[] perm, ReaderSort sort)
        {
            for (int i = 0; i < count; i++) perm[i] = i;
            if (count < 2) return;
            (t_sorter ??= new BlockSorter()).Order(slots, offset, perm.AsSpan(0, count), sort);
        }

        /// <summary>One sorter per thread, allocated on that thread's first compute and reused forever. It is a mutable
        /// cache, so it cannot be a plain static: the UI thread owns the real one, and xunit runs test classes on
        /// whatever thread it likes.</summary>
        [ThreadStatic] static BlockSorter? t_sorter;

        /// <summary>The comparator set, `LibraryNavSorter`'s shape (User.cs:686): the three delegates are allocated once
        /// in the constructor and the rows arrive as fields, so ordering allocates nothing at all.</summary>
        sealed class BlockSorter
        {
            static readonly StringComparer Name = StringComparer.OrdinalIgnoreCase;

            int[] _slots = [];
            int _offset;
            readonly Comparison<int> _newest, _oldest, _alphabetical;

            public BlockSorter()
            {
                _newest = Newest;
                _oldest = Oldest;
                _alphabetical = Alphabetical;
            }

            public void Order(int[] slots, int offset, Span<int> perm, ReaderSort sort)
            {
                _slots = slots;
                _offset = offset;
                perm.Sort(sort switch
                {
                    ReaderSort.Alphabetical => _alphabetical,
                    ReaderSort.Oldest => _oldest,
                    _ => _newest,
                });
                _slots = [];                                             // never hold the caller's pooled buffer alive
            }

            int Newest(int x, int y) => ByYear(x, y, newest: true);
            int Oldest(int x, int y) => ByYear(x, y, newest: false);
            int Alphabetical(int x, int y) => ByTitle(x, y);

            /// <summary>Year, with the unknown years SINKING as a block whichever direction the dated ones run: a
            /// release whose year nobody has answered yet must not jump to the top of "oldest first" and then move when
            /// the answer lands.</summary>
            int ByYear(int x, int y, bool newest)
            {
                Album a = At(x), b = At(y);
                bool ya = a.Knows(AlbumFields.Year) && a.Year > 0, yb = b.Knows(AlbumFields.Year) && b.Year > 0;
                if (ya != yb) return ya ? -1 : 1;
                int c = newest ? b.Year.CompareTo(a.Year) : a.Year.CompareTo(b.Year);
                return c != 0 ? c : ByTitle(x, y);
            }

            /// <summary>Title, then uri, then the source index — the tail every arm shares, and what makes the order
            /// total (see the file header: an unstable sort plus a sequence-hashing remount key).</summary>
            int ByTitle(int x, int y)
            {
                Album a = At(x), b = At(y);
                int c = Name.Compare(a.Title, b.Title);
                if (c == 0)
                {
                    Span<char> sa = stackalloc char[EntityId.MaxGidTextChars], sb = stackalloc char[EntityId.MaxGidTextChars];
                    c = LibraryRows.UriOf(a.Id, sa).SequenceCompareTo(LibraryRows.UriOf(b.Id, sb));
                }
                return c != 0 ? c : x.CompareTo(y);
            }

            Album At(int index) => new(_slots[_offset + index]);
        }

        /// <summary>FNV-1a over the block slots + the two GROUP bits (saved, liked-only), so the list keys on the
        /// SEQUENCE and nothing else — not on selection, not on how many rows a block has landed (a block that grows
        /// re-renders itself; it never remounts the list). The liked-only bit is in because it decides what the block
        /// PAINTS (the whole tracklist or just the liked rows) and therefore what its extent is: an album that flips from
        /// liked-only to saved is a different block, not a grown one. The count prefix makes two different lengths
        /// distinct without trusting the hash.</summary>
        public static string OrderKey(ReadOnlySpan<ReaderBlock> blocks)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < blocks.Length; i++)
            {
                h ^= (uint)blocks[i].AlbumSlot; h *= 1099511628211UL;
                h ^= blocks[i].Saved ? 1u : 0u; h *= 1099511628211UL;
                h ^= blocks[i].LikedOnly ? 1u : 0u; h *= 1099511628211UL;
            }
            return blocks.Length.ToString(CultureInfo.InvariantCulture) + ":" + h.ToString("x16", CultureInfo.InvariantCulture);
        }
    }
}
