// ── Entities/Track.Rules.cs ────────────────────────────────────────────────────────────────────────────────────────
// every pure rule of the track surface: the lane table, the tier + relief ladders, the density and art ladders, the
// sort cycle, the command-bar fit, the filter model, the expanded-row facts, the shared number formats (incl. the exact
// key / camelot tables), the membership diff, the reorder rules, the right-click target resolver, not-yet-out
//
// Role: CORE
// Owner: M
// Wave: 4.5
// Budget: 1,200 lines — a NAMED partial of Track.cs, because Track.cs's 1,200 cannot hold the columns AND the rules
// Spec: ch 01 §8-§9, ch 04 §8 (plan §5 Wave 4.5, A2)
//
// ── WHAT THIS FILE IS ────────────────────────────────────────────────────────────────────────────────────────────────
//
// A VERBATIM port. Every constant, ladder, hysteresis band, yield order and reset threshold below is 0.2.9's, from
// `Features/Detail/{DetailTrackTableRules, DetailTrackCommandBarLayout, TrackFilterModel, TrackExpandedFacts,
// PlaylistReorderRules, DetailConfig (DetailFormat)}.cs`, `Components/{TrackRow.cs:101-166,578, MembershipDiff.cs}`,
// `DetailTracks.cs:626-681` and `Actions/TrackTargetResolver.cs`. Chapter 04 §9 names the one fidelity risk that has no
// wireframe: "the table's pure rules are re-derived instead of ported". They are 1,100 lines of tested decisions, and
// each number carries the measured reason it has (Plays 52 because "1.85B" is ~42 DIP at 14 px; Date 88 because the
// HEADER is wider than the value). Re-deriving any of them by eye reproduces the defect it closed.
//
// What CHANGED is the input shape, never the decision: 0.2.9 records become handles, interned strings, unix SECONDS in
// an `int` (0 = none) and the two closed rings (`Key`, `Camelot`) as one-based bytes. Everything here is pure — no
// engine UI type (the `SelectionModel` the resolver mutates is a plain model), no clock (a rule that needs "now" takes
// it), and no allocation on a path a row runs per recycle (`MembershipDiff.RowKeyMatches` compares in place,
// `FilterRow.Of` resolves interned strings and never concatenates, the key tables are lookups).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;

namespace Wavee;

public readonly partial struct Track
{
    // ══ 1. THE LANE TABLE AND THE COLUMN SET ═════════════════════════════════════════════════════════════════════════

    /// <summary>THE lane width table — read by the width tracks the grid is built from, by the cells that fill them and
    /// by the relief ladder that decides which yield; a metric the ladder computes from one number while the grid is
    /// built from another silently mis-predicts the squeeze. Every fixed width is its widest REALISTIC content plus optical
    /// margin at the Classic 14/20 caption or its 11 px uppercase header, whichever is larger.</summary>
    public static class Lane
    {
        /// <summary>The # ↔ play/pause lane — ONE number with <see cref="Heart"/>: at 36 beside the heart's 28 it read as
        /// a gutter with a number lost in it. Holds three digits, the 24-DIP transport and the header's "#" between its
        /// two <see cref="NumCaretSlot"/>s.</summary>
        public const float Num = 28f;
        /// <summary>The header's symmetric caret slot inside <see cref="Num"/> (the caret draws in the trailing one), so
        /// turning the sort indicator on never nudges the "#" off the numbers under it.</summary>
        public const float NumCaretSlot = 9f;
        /// <summary>Exactly the 28-DIP like hit target.</summary>
        public const float Heart = 28f;
        /// <summary>The art lane — one number with <see cref="RowMetrics.ThumbSize"/>.</summary>
        public const float Thumb = 32f;
        /// <summary>Added by: a 24-DIP avatar + 8 gap + ~100 of name.</summary>
        public const float By = 132f;
        /// <summary>Sized to the "Date added" HEADER; "Sep 28, 2024" ≈ 86 at 14 px is why this is 88, not 76.</summary>
        public const float Date = 88f;
        /// <summary>"1.85B" is ~42 DIP at 14 px; the lane was 84 — the table's single biggest slack.</summary>
        public const float Plays = 52f;
        /// <summary>A 6-DIP swatch + "171.1" + "·" + one key token, with the 4-DIP gaps.</summary>
        public const float Tempo = 80f;
        /// <summary>"1:59:59" (an episode in a playlist), not just "59:59".</summary>
        public const float Duration = 52f;
        /// <summary>The trailing film / hover-"…" lane.</summary>
        public const float Video = 28f;
        /// <summary>The trailing "…" lane when Video is off (a 28-DIP button + air).</summary>
        public const float Actions = 40f;
        /// <summary>The expand chevron's hit target.</summary>
        public const float Expand = 26f;

        // The identity STAR lanes: weights and a readable FLOOR each, ratio-consistent (120 : 90 : 90 == 1 : .75 : .75),
        // so a star pool equal to the floors' sum hands every identity lane exactly its floor.
        public const float TitleStar = 1f;
        public const float ArtistStar = 0.75f;
        public const float AlbumStar = 0.75f;
        /// <summary>~14 characters of BodyStrong 14/20 — below this the Title states nothing.</summary>
        public const float TitleFloor = 120f;
        /// <summary>Classic's Artist lane — ~11 characters of Caption 14/20, one billed name.</summary>
        public const float ArtistFloor = 90f;
        public const float AlbumFloor = 90f;
    }

    /// <summary>Which optional lanes a track row shows; #, Title and Duration are always present. Cell (and track) order:
    /// # · ♥ · thumb · Title · Artist · Album · AddedBy · DateAdded · Plays · Tempo · Duration · Video · Actions · Expand.
    /// SHARED by the header and every row builder, and it is the width-track CACHE KEY (ch 01 §9): a value record with no
    /// mutable field, or the cache forks. <paramref name="Actions"/> is the "…" lane when Video is off (Video carries the
    /// "…" on hover when on — the lane is reserved once); <paramref name="Tier"/> is the width tier the set was built for,
    /// so header, rows and tracks derive the SAME padding/gap; <paramref name="Tempo"/> means "this surface wants BPM·Key"
    /// (the tier gate is <see cref="RowMetrics.ShowTempo"/>); <paramref name="Artist"/> is Classic's dedicated lane;
    /// <paramref name="Classic"/> rides with the shape so persistent rows re-skin live.</summary>
    public readonly record struct ColumnSet(bool Album, bool By, bool Date, bool Video, bool Plays, bool Heart, bool Thumb,
                                            bool Actions = true, int Tier = 0, bool Tempo = false, bool Expand = false,
                                            bool Artist = false, bool Classic = false);

    /// <summary>What the list is sorted by; <see cref="Index"/> = the context's own order. PERSISTED — new columns append
    /// and existing values never move.</summary>
    public enum SortColumn { Index, Title, Album, Duration, Artist, DateAdded, Plays }

    /// <summary>The sort state persisted per context (<c>Detail.SortKeys</c>).</summary>
    public readonly record struct SortSpec(SortColumn Column, bool Descending)
    {
        public static readonly SortSpec Default = new(SortColumn.Index, false);
    }

    /// <summary>The identity lanes a row style selects.</summary>
    public readonly record struct IdentityColumns(bool Thumb, bool Artist, bool ArtistInTitle);

    /// <summary>The trailing lanes a row style selects: Classic folds media facts into Title and exposes one command lane.</summary>
    public readonly record struct TrailingColumns(bool Video, bool Actions, bool Expand);

    /// <summary>The lanes a table draws, as pure geometry — the relief ladder's whole input beside the gap and inset.</summary>
    public readonly record struct Lanes(
        bool Heart, bool Thumb, bool Artist, bool Album, bool By, bool Date, bool Plays, bool Tempo,
        bool Video, bool Actions, bool Expand);

    // ══ 2. THE TABLE RULES: skins, ladders, the sort cycle, the relief ladder ════════════════════════════════════════

    /// <summary>Column and sort rules shared by the table renderer, the row and the tests.</summary>
    public static class TableRules
    {
        /// <summary>Classic's Artist lane survives one tier longer than Album and folds below 440 DIP (tier 4).</summary>
        public const int ClassicArtistFoldTier = 4;
        public const int ClassicInlineVideoDropTier = 4;
        public const float ClassicHeaderHeight = 32f;
        /// <summary>The scale the Settings row-density MINIATURE draws the real row geometry at — here, not in the
        /// picker, so "the preview mirrors the real row" is a tested number.</summary>
        public const float PreviewScale = 0.25f;
        /// <summary>Relief steps; step 0 is "none", each later step drops exactly one more lane (<see cref="Relieve"/>).</summary>
        public const int MaxRelief = 8;
        /// <summary>How much width a yielded lane must re-earn — the tier ladder's band and asymmetry: yield at once,
        /// re-admit with margin.</summary>
        public const float ReliefHysteresisDip = Detail.Breakpoints.TierHysteresisDip;

        public static IdentityColumns IdentityColumns(
            bool classic, bool showArtThumb, bool artworkHidden, bool showTrackArtist, int tier)
        {
            bool artist = classic && showTrackArtist && tier < ClassicArtistFoldTier;
            return new IdentityColumns(
                Thumb: !classic && showArtThumb && !artworkHidden && tier < 5,
                Artist: artist,
                ArtistInTitle: showTrackArtist && !artist);
        }

        /// <summary>Classic maps density onto the tighter legacy ladder (36/40/44/48); Modern keeps 40/48/56/64.</summary>
        public static float RowHeightFor(int density, bool classic) => classic
            ? density switch { 0 => 36f, 2 => 44f, 3 => 48f, _ => 40f }
            : density switch { 0 => 40f, 2 => 56f, 3 => 64f, _ => 48f };

        public static float HeaderHeightFor(bool classic) => classic ? ClassicHeaderHeight : 36f;

        /// <summary>The rows the shimmer holds for before the list is allowed to show: the first page. A Partial edge
        /// whose first page (two rows) swapped the shimmer for a near-empty list that then filled row by row is the
        /// defect this bounds.</summary>
        public const int FirstPageRows = 12;

        /// <summary>Whether the table region shimmers instead of showing rows. Unknown ⇒ shimmer (nobody answered; never
        /// "empty"). Partial ⇒ shimmer until the first page is in — <see cref="FirstPageRows"/> rows, or every row of a
        /// list shorter than that (<paramref name="total"/> is clamped to ≥ <paramref name="count"/>, so an undeclared
        /// total cannot strand a short list). Complete / Failed ⇒ never: a complete list renders (even empty) and a failure
        /// becomes the Retry vacancy. Bounded, not a hang: the page re-asks the next page while Partial, so the count only
        /// grows toward the threshold or the edge turns Complete.</summary>
        public static bool RowsPending(EdgeState state, int count, int total) =>
            state == EdgeState.Unknown
            || (state == EdgeState.Partial && count < Math.Min(Math.Max(total, count), FirstPageRows));

        /// <summary>The whole reveal gate: the edge half above, AND the hot rows have settled. A tracklist edge answers
        /// with row ids first and the rows' own fields land in a later fetch, so a Complete edge whose first rows know
        /// nothing yet still shimmers — the real grid must never mount over rows with no data (the 2026-09-17 recording:
        /// zebra plates, blank titles and `—` durations for a beat). A Failed edge never waits on rows it does not have.
        /// <paramref name="hotSettled"/> is <see cref="RowUnsettled"/> folded over the first <see cref="HotRows"/>
        /// rows.</summary>
        public static bool RowsPending(EdgeState state, int count, int total, bool hotSettled) =>
            RowsPending(state, count, total) || (!hotSettled && state != EdgeState.Failed);

        /// <summary>The rows the reveal waits for: the first page, or every row of a shorter list. Source order — the
        /// row fetch goes out in that order and lands as one answer, so these are the rows the first frame paints.</summary>
        public static int HotRows(int count) => Math.Clamp(count, 0, FirstPageRows);

        /// <summary>Is a hot row still LOADING? Settled once it knows its face (<c>TrackFields.Face</c>), or once an
        /// answer has been given and nothing is out for it — the disk saying "not here", a batch that did not name it, a
        /// terminal failure after the disk probe. A VIRGIN row (nobody has asked or answered, <paramref name="answered"/>
        /// false, nothing in flight) is still loading: the edge landed in this flush and the page's demand effect has not
        /// run yet, so the gate must not open on the frame between the two.</summary>
        public static bool RowUnsettled(bool knowsFace, bool inFlight, bool answered) =>
            !knowsFace && (inFlight || !answered);

        /// <summary>Does a mounted row paint as REAL? A valid row that knows its face; anything less keeps the
        /// placeholder of the same extent (the rows past the hot band that a later batch fills, a row scrolled to
        /// before its page arrived). The one rule the row component reads.</summary>
        public static bool RowHasData(bool isValid, bool knowsFace) => isValid && knowsFace;

        /// <summary>Art edge per density on the thumbnail ladder, keeping row − art ≥ 16 (Compact 40 → 32 · Default 48 → 32
        /// · Cozy 56 → 40 · Comfortable 64 → 48); Classic stays on 32 except Comfortable → 40.</summary>
        public static float ArtSizeFor(int density, bool classic) => classic
            ? (density == 3 ? Design.Size.Thumb40 : Design.Size.Thumb32)
            : density switch { 2 => Design.Size.Thumb40, 3 => Design.Size.Thumb48, _ => Design.Size.Thumb32 };

        /// <summary>Classic folds VIDEO into the Title line and keeps ONE trailing command lane; both skins keep the
        /// disclosure chevron (the drawer restates what relief took away — a row affordance, not a skin flourish), under
        /// the same width gate as the "…" lane.</summary>
        public static TrailingColumns TrailingColumns(bool classic, bool hasVideo, bool showVersions, int tier)
        {
            bool hasTrailingRoom = tier < 6;
            bool expand = showVersions && hasTrailingRoom;
            if (classic) return new TrailingColumns(false, hasTrailingRoom, expand);
            bool video = hasVideo && hasTrailingRoom;
            return new TrailingColumns(video, hasTrailingRoom && !video, expand);
        }

        public static bool ShowClassicInlineVideo(bool classic, bool hasVideo, int tier) =>
            classic && hasVideo && tier < ClassicInlineVideoDropTier;

        /// <summary>"Track details" in the menu is a FALLBACK for a missing chevron lane, in either skin — never a second
        /// control beside a visible one.</summary>
        public static bool ShowVersionsMenuItem(bool showVersions, bool expandLane, bool singleTrack) =>
            showVersions && singleTrack && !expandLane;

        /// <summary>A Title header owns Artist only while Artist is folded into its subline.</summary>
        public static bool HeaderActive(SortColumn header, SortColumn active, bool artistColumn) =>
            header == active || (!artistColumn && header == SortColumn.Title && active == SortColumn.Artist);

        /// <summary>The header-click cycle. # flips while on Index, else resets; a dedicated Artist lane gets its own
        /// asc → desc → default; without it Title runs Title↑ → Title↓ → Artist↑ → Artist↓ → default.</summary>
        public static SortSpec NextSort(SortSpec cur, SortColumn clicked, bool artistColumn)
        {
            if (clicked == SortColumn.Index)
                return cur.Column == SortColumn.Index ? new SortSpec(SortColumn.Index, !cur.Descending) : SortSpec.Default;

            if (clicked == SortColumn.Title && !artistColumn)
            {
                if (cur.Column == SortColumn.Title)
                    return cur.Descending ? new SortSpec(SortColumn.Artist, false) : new SortSpec(SortColumn.Title, true);
                if (cur.Column == SortColumn.Artist)
                    return cur.Descending ? SortSpec.Default : new SortSpec(SortColumn.Artist, true);
                return new SortSpec(SortColumn.Title, false);
            }

            if (cur.Column == clicked) return cur.Descending ? SortSpec.Default : new SortSpec(clicked, true);
            return new SortSpec(clicked, false);
        }

        // ── the identity-first RELIEF LADDER ──────────────────────────────────────────────────────────────────────────
        // The tier keys on the pane's TOTAL width and cannot see what the identity lanes have LEFT, so every DIP of
        // pressure landed on Title/Artist (a 650-DIP Classic Liked table kept Plays + BPM·Key and paid with a 65-DIP
        // Title). Relief measures what the current lane set needs — fixed lanes, gaps, inset and the identity floors — and
        // yields trailing lanes, cheapest fact first, until the floors clear. It composes with the tier (only ever REMOVES
        // a lane the tier admitted), never touches #, Title, duration or the trailing lane, and shares the tier's
        // hysteresis grammar so the two ladders cannot disagree about the safe direction.

        /// <summary>The lanes surviving <paramref name="step"/> steps. YIELD ORDER: Plays (1) → BPM·key (2) → Added by (3)
        /// → Date added (4) → Album (5) → Artist (6) → art thumb (7) → ♥ (8).</summary>
        public static Lanes Relieve(in Lanes l, int step) => step <= 0 ? l : new Lanes(
            Heart: l.Heart && step < 8,
            Thumb: l.Thumb && step < 7,
            Artist: l.Artist && step < 6,
            Album: l.Album && step < 5,
            By: l.By && step < 3,
            Date: l.Date && step < 4,
            Plays: l.Plays && step < 1,
            Tempo: l.Tempo && step < 2,
            Video: l.Video, Actions: l.Actions, Expand: l.Expand);

        /// <summary>The narrowest width at which <paramref name="l"/> still clears the identity floors: fixed lanes at
        /// their <see cref="Lane"/> widths, identity lanes at their FLOORS, one gap between each pair and the grid's own
        /// padding on both sides. Title and duration are unconditional, so the seed carries both.</summary>
        public static float MinWidthFor(in Lanes l, float colGap, float padX)
        {
            float w = Lane.Num + Lane.TitleFloor + Lane.Duration;
            int cols = 3;
            if (l.Heart) { w += Lane.Heart; cols++; }
            if (l.Thumb) { w += Lane.Thumb; cols++; }
            if (l.Artist) { w += Lane.ArtistFloor; cols++; }
            if (l.Album) { w += Lane.AlbumFloor; cols++; }
            if (l.By) { w += Lane.By; cols++; }
            if (l.Date) { w += Lane.Date; cols++; }
            if (l.Plays) { w += Lane.Plays; cols++; }
            if (l.Tempo) { w += Lane.Tempo; cols++; }
            if (l.Video) { w += Lane.Video; cols++; }
            if (l.Actions) { w += Lane.Actions; cols++; }
            if (l.Expand) { w += Lane.Expand; cols++; }
            return w + (cols - 1) * colGap + padX * 2f;
        }

        /// <summary>The FEWEST steps that fit <paramref name="available"/>: 0 when the floors already clear (a wide table is
        /// untouched by construction), <see cref="MaxRelief"/> when nothing can. A width of 0 is "not measured yet".</summary>
        public static int NominalReliefFor(in Lanes l, float available, float colGap, float padX)
        {
            if (available <= 0f) return 0;
            for (int step = 0; step < MaxRelief; step++)
                if (MinWidthFor(Relieve(in l, step), colGap, padX) <= available) return step;
            return MaxRelief;
        }

        /// <summary>The nominal step with hysteresis: yield the moment a floor is breached, re-admit only once the width
        /// clears the threshold by <see cref="ReliefHysteresisDip"/>. <paramref name="initialized"/> false ⇒ nothing was
        /// measured yet, so take the nominal outright (the tier ladder's first-measure rule).</summary>
        public static int ReliefFor(in Lanes l, float available, float colGap, float padX, int prev, bool initialized = true)
        {
            if (available <= 0f) return prev;
            int nominal = NominalReliefFor(in l, available, colGap, padX);
            if (!initialized || nominal >= prev) return nominal;
            int eased = NominalReliefFor(in l, available - ReliefHysteresisDip, colGap, padX);
            return eased < prev ? eased : prev;
        }

        /// <summary>The set's lanes as the ladder sees them; Tempo goes through <see cref="RowMetrics.ShowTempo"/> so the
        /// metric measures the lane the grid will actually build (DetailTracks.cs:626).</summary>
        public static Lanes LanesOf(in ColumnSet s) => new(
            Heart: s.Heart, Thumb: s.Thumb, Artist: s.Artist, Album: s.Album, By: s.By, Date: s.Date,
            Plays: s.Plays, Tempo: RowMetrics.ShowTempo(in s), Video: s.Video, Actions: s.Actions, Expand: s.Expand);

        /// <summary>The set with <paramref name="step"/> lanes yielded, applied in ONE place so header, rows, shimmer and
        /// tracks agree. Tempo is cleared only where the lane was actually up: the flag also means "this surface wants
        /// BPM·Key", and clearing it where the tier had already hidden the lane would fork the ColumnSet cache for no
        /// visible difference (DetailTracks.cs:650).</summary>
        public static ColumnSet ApplyRelief(in ColumnSet s, int step)
        {
            if (step <= 0) return s;
            var r = Relieve(LanesOf(in s), step);
            return s with
            {
                Heart = r.Heart, Thumb = r.Thumb, Artist = r.Artist, Album = r.Album, By = r.By, Date = r.Date,
                Plays = r.Plays, Tempo = s.Tempo && (r.Tempo || !RowMetrics.ShowTempo(in s)),
            };
        }
    }

    // ══ 3. ROW METRICS — the alignment invariant ═════════════════════════════════════════════════════════════════════

    /// <summary>The grid constants the header AND the rows read, keyed by the set's tier, so a heading sits over its values
    /// at every width by construction. A row's left inset is <c>PadXFor(tier) − RowInset</c> inside a <c>RowInset</c> skin
    /// margin, which sums to the header's padding; Classic and plain rows carry no margin and pay the full padding.</summary>
    public static class RowMetrics
    {
        /// <summary>Density Default.</summary>
        public const float RowHeight = 48f;
        public const float HeaderHeight = 36f;
        public const float ColGap = Spacing.M;
        public const float PadX = Spacing.L;
        /// <summary>The rounded row-highlight inset (rows pad PadX − RowInset so columns stay header-aligned).</summary>
        public const float RowInset = Spacing.S;
        /// <summary>36 sat between two ladder rungs and broke DOWNWARD: the Compact row is 40 tall.</summary>
        public const float ThumbSize = Design.Size.Thumb32;
        public const float HeartCol = Lane.Heart;
        /// <summary>Where the drawer's connector rail sits inside its own left padding (TrackVersionsPanel.RailX): the
        /// caller subtracts it so the RAIL, not the gutter's edge, lands on the art centre.</summary>
        public const float RailOffset = 7f;

        /// <summary>0 Compact · 1 Default · 2 Cozy · 3 Comfortable (the Modern ladder).</summary>
        public static float RowHeightFor(int density) => density switch { 0 => 40f, 2 => 56f, 3 => 64f, _ => RowHeight };

        /// <summary>Forwards to the one decision so every Modern row agrees on 32/40/48.</summary>
        public static float ArtSizeFor(int density) => TableRules.ArtSizeFor(density, classic: false);

        /// <summary>Full at wide tiers, tighter as the pane narrows; tier 0 is the unchanged constants.</summary>
        public static float PadXFor(int tier) => tier <= 3 ? PadX : tier <= 5 ? Spacing.M : Spacing.S;
        public static float ColGapFor(int tier) => tier <= 4 ? ColGap : Spacing.S;

        /// <summary>BPM·Key shows only when asked AND tier ≤ 3 — read by the row AND the tracks, so they never disagree.</summary>
        public static bool ShowTempo(in ColumnSet set) => set.Tempo && set.Tier <= 3;

        /// <summary>The x of the row's ARTWORK CENTRE from the skin's left edge — the leading lanes (# · ♥) and their gaps
        /// plus half the art. DERIVED from the lane table, not a constant: the leading cluster is # alone, # + ♥, or
        /// # + ♥ + art depending on the tier, and the old hard-coded 52 landed mid-♥ at every wide tier. The drawer's rail
        /// descends from here ("these belong to that record"). Modern subtracts <see cref="RowInset"/> (its skin margin).</summary>
        public static float ArtCentreIndent(in ColumnSet set, float art)
        {
            float gap = ColGapFor(set.Tier);
            float x = PadXFor(set.Tier) - (set.Classic ? 0f : RowInset);   // the grid's own left pad
            x += Lane.Num;
            if (set.Heart) x += gap + HeartCol;
            return set.Thumb ? x + gap + art / 2f : x + gap;
        }

        /// <summary>The drawer's indent: the art centre less the rail's own offset, never negative (DetailTracks.cs:3347).</summary>
        public static float DrawerIndent(in ColumnSet set, float art) => MathF.Max(0f, ArtCentreIndent(in set, art) - RailOffset);
    }

    // ══ 4. THE SHARED FORMATS — one spelling per fact for the lane, the drawer and the facts strip ══════════════════

    public static class Format
    {
        /// <summary>Unknown is an em dash, never a zero: "0" and "0:00" read as facts.</summary>
        public const string Dash = "—";

        // The two closed rings as EXACT tables (ch 04 DATA GAPS). They agree with `Spotify.Decode.KeyCode` (1 = C … 12 = B,
        // sharps spelled '#', flats folded onto them) and `Spotify.Decode.CamelotCode` ("1A" → 1, "1B" → 2 … "12B" → 24);
        // both are ONE-BASED so the column's zero stays "unknown". TrackFormatTests round-trips every value.
        static readonly string[] s_keys = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        static readonly string[] s_camelot =
        [
            "1A", "1B", "2A", "2B", "3A", "3B", "4A", "4B", "5A", "5B", "6A", "6B",
            "7A", "7B", "8A", "8B", "9A", "9B", "10A", "10B", "11A", "11B", "12A", "12B",
        ];

        /// <summary>The tonic for a <c>TrackTable.Key</c> byte; null for 0 (unknown) or anything off the ring.</summary>
        public static string? KeyLabel(byte key) => key is >= 1 and <= 12 ? s_keys[key - 1] : null;

        /// <summary>The wheel slot for a <c>TrackTable.Camelot</c> byte; null for 0 (unknown) or anything off the wheel.</summary>
        public static string? CamelotLabel(byte camelot) => camelot is >= 1 and <= 24 ? s_camelot[camelot - 1] : null;

        // ── the row's three hot formats go through caches (fluentgpu rule 13: never format a number/duration/date per
        // recycle). ONE cache per call site, static — a per-row cache would defeat the point. The engine's
        // `FormatCache<TKey>` clears itself at `Capacity` (4,096) entries, so the key domain (every play count and every
        // date-added stamp a user scrolls past) stays bounded. Plays and Date-added read the CURRENT culture and the
        // localization table, so both caches are dropped when either flips — a test's invariant-culture scope must not be
        // handed a "1,85B" formatted under nl-NL a moment earlier, and a language change must not keep "Today" in English.
        static FormatCache<long> s_plays = FormatCache.Create<long>();
        static FormatCache<(int Added, int NowDay)> s_dateAdded = FormatCache.Create<(int Added, int NowDay)>();
        static FormatCache<(int At, int NowYear)> s_releaseDate = FormatCache.Create<(int At, int NowYear)>();
        static CultureInfo? s_cachesCulture;
        static int s_cachesEpoch;
        static readonly Func<long, string> s_playsFormat = FormatPlays;
        static readonly Func<(int Added, int NowDay), string> s_dateAddedFormat = FormatDateAdded;
        static readonly Func<(int At, int NowYear), string> s_releaseDateFormat = FormatReleaseDate;
        // `now` arrives as live unix seconds and changes once a second while thirteen rows share it inside one frame;
        // one memo of its local calendar day spares twelve of the thirteen zone conversions.
        static long s_nowMemoSeconds = long.MinValue;
        static int s_nowMemoDay;

        /// <summary>Live entries in the plays cache — informational (tests pin the bound), never read on the hot path.</summary>
        public static int PlaysCacheCount => s_plays.Count;
        /// <summary>Live entries in the date-added cache — informational (tests pin the bound).</summary>
        public static int DateAddedCacheCount => s_dateAdded.Count;
        /// <summary>Live entries in the release-date cache — informational (tests pin the bound).</summary>
        public static int ReleaseDateCacheCount => s_releaseDate.Count;

        /// <summary>Drops the culture-bound caches when the culture or the localization epoch moved since the last format.
        /// Reads <c>CultureEpoch.Value</c> — a SUBSCRIBING read, exactly what <c>Loc.Get</c> did on the uncached path — so
        /// a row printing "Today" still re-renders on a language flip although the hit no longer reaches <c>Loc.Get</c>.</summary>
        static void FreshenCultureCaches()
        {
            var culture = CultureInfo.CurrentCulture;
            int epoch = Localization.CultureEpoch.Value;
            if (ReferenceEquals(culture, s_cachesCulture) && epoch == s_cachesEpoch) return;
            s_cachesCulture = culture;
            s_cachesEpoch = epoch;
            s_plays = FormatCache.Create<long>();
            s_dateAdded = FormatCache.Create<(int Added, int NowDay)>();
            s_releaseDate = FormatCache.Create<(int At, int NowYear)>();
            s_nowMemoSeconds = long.MinValue;
        }

        /// <summary>THE compact stream count: "1.85B" / "11.8M" / "654.8K" / N0. The artist chart uses it always (the exact
        /// count goes in a tooltip). Current culture, as 0.2.9. Cached per count: the same count returns the same string
        /// instance until the culture moves.</summary>
        public static string PlaysLabel(long n)
        {
            FreshenCultureCaches();
            return s_plays.Get(n, s_playsFormat);
        }

        static string FormatPlays(long n) =>
            n >= 1_000_000_000 ? $"{n / 1_000_000_000f:0.##}B"
            : n >= 1_000_000 ? $"{n / 1_000_000f:0.#}M"
            : n >= 1_000 ? $"{n / 1_000f:0.#}K"
            : n.ToString("N0", CultureInfo.CurrentCulture);

        /// <summary>The CLOCK: "m:ss", or "h:mm:ss" past an hour. 0 ms spells "0:00" — a cell must use
        /// <see cref="DurationCell"/>. The engine's whole-second cache (<c>FormatCache.HhMmSs</c>): the same length
        /// returns the same string instance; a negative length clamps to "0:00" (every caller guards <c>ms &gt; 0</c>).</summary>
        public static string TrackTime(long ms) => FormatCache.DurationHhMmSs(ms);

        /// <summary>The duration CELL: 0 ms is "not known yet", never a zero-second track.</summary>
        public static string DurationCell(long ms) => ms > 0 ? TrackTime(ms) : Dash;

        /// <summary>"101" for a whole BPM, "101.5" when the fraction means something; invariant, because a comma decimal
        /// beside the key label reads as a list.</summary>
        public static string Bpm(double bpm)
        {
            double rounded = Math.Round(bpm, 1, MidpointRounding.AwayFromZero);
            return Math.Abs(rounded - Math.Round(rounded)) < 0.05
                ? ((int)Math.Round(rounded)).ToString(CultureInfo.InvariantCulture)
                : rounded.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>The Tempo lane's figure off the ×10 column. EMPTY (not "0", not a dash) until kind 222 lands, because a
        /// placeholder would flicker to a value a moment later (ch 01 §7).</summary>
        public static string TempoLabel(ushort tempoX10) => tempoX10 == 0 ? "" : Bpm(tempoX10 / 10d);

        /// <summary>"2 hr 59 min" / "47 min" (never "0 min").</summary>
        public static string TotalTime(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            int h = (int)t.TotalHours, m = t.Minutes;
            return h >= 1 ? Strings.Detail.DurationHrMin(h, m) : Strings.Detail.DurationMin(Math.Max(1, m));
        }

        /// <summary>A not-yet-out row's release date in the duration lane: "4 Sep", or "4 Sep 2027" across a year (a bare
        /// day a year out reads as imminent). Local zone, current culture.</summary>
        public static string ShortDate(int unixSeconds, long nowUnixSeconds)
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
            return local.Year == LocalYear(nowUnixSeconds)
                ? local.ToString("d MMM", CultureInfo.CurrentCulture)
                : local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        }

        /// <summary><see cref="ShortDate"/> cached on (stamp, now's local year) — the label is a pure function of those two.
        /// The bound duration lane's not-yet-out arm reads it on every rebind, so a hit must not format.</summary>
        public static string ReleaseDateLabel(int unixSeconds, long nowUnixSeconds)
        {
            FreshenCultureCaches();
            return s_releaseDate.Get((unixSeconds, LocalYear(nowUnixSeconds)), s_releaseDateFormat);
        }

        static string FormatReleaseDate((int At, int NowYear) key)
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(key.At).ToLocalTime();
            return local.Year == key.NowYear
                ? local.ToString("d MMM", CultureInfo.CurrentCulture)
                : local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        }

        /// <summary>The Date-added lane: "" for no stamp; relative for the last week (Today / Yesterday / N days ago), else
        /// absolute — same calendar year drops the year so the 88-DIP lane stays readable. Days count LOCAL calendar dates.
        /// Cached on (stamp, now's local day): the label is a pure function of those two, so the same stamp on the same
        /// day returns the same string instance however many times the clock ticks inside it.</summary>
        public static string DateAddedLabel(int unixSeconds, long nowUnixSeconds)
        {
            if (unixSeconds == 0) return "";
            FreshenCultureCaches();
            if (nowUnixSeconds != s_nowMemoSeconds)
            {
                s_nowMemoDay = LocalDay(nowUnixSeconds);
                s_nowMemoSeconds = nowUnixSeconds;
            }
            return s_dateAdded.Get((unixSeconds, s_nowMemoDay), s_dateAddedFormat);
        }

        static string FormatDateAdded((int Added, int NowDay) key)
        {
            var d = DateTimeOffset.FromUnixTimeSeconds(key.Added).ToLocalTime();
            var now = DateTime.SpecifyKind(new DateTime(key.NowDay * TimeSpan.TicksPerDay), DateTimeKind.Local);
            int days = (int)(now - d.Date).TotalDays;
            if (days <= 0) return Loc.Get(Strings.Detail.Today);
            if (days == 1) return Loc.Get(Strings.Detail.Yesterday);
            if (days < 7) return Strings.Detail.DaysAgo(days);
            return d.Year == now.Year
                ? d.ToString("MMM d", CultureInfo.CurrentCulture)
                : d.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        }

        /// <summary>The LOCAL calendar day of a unix instant as a day number (ticks / day) — the date-added cache key.</summary>
        static int LocalDay(long unixSeconds)
            => (int)(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().Date.Ticks / TimeSpan.TicksPerDay);

        /// <summary>The chart header's "last updated": always absolute (WHEN the chart rolled over, not how long ago).</summary>
        public static string ChartUpdatedDateLabel(long unixMs, long nowUnixSeconds)
        {
            if (unixMs <= 0) return "";
            var d = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
            return d.Year == LocalYear(nowUnixSeconds)
                ? d.ToString("MMM d", CultureInfo.CurrentCulture)
                : d.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        }

        static int LocalYear(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().Year;
    }

    /// <summary>The row's credit-line discriminator: ONE 64-bit stamp over exactly what the row prints per billed artist —
    /// the slot, the interned name id (an interned id never changes its text, so a new id IS a new name) and whether the
    /// uri is known (the span's link-ness). The table row's presentation gate compares this instead of summing every
    /// credited artist's <c>Version</c>, which flipped on ANY field landing for ANY artist (a monthly-listener count, a
    /// header image) and re-rendered thirteen rows per publish. Order-sensitive (FNV-1a over the words with a shift-xor
    /// after each), so a re-billed order or a moved name reads as a change.</summary>
    public static class CreditStamp
    {
        /// <summary>The empty credit line — FNV-1a's 64-bit offset basis, so an unbilled row never equals any billed one.</summary>
        public const ulong Seed = 0xcbf29ce484222325UL;
        const ulong Prime = 0x100000001b3UL;

        /// <summary>Folds one billed artist into <paramref name="stamp"/>.</summary>
        public static ulong Add(ulong stamp, int slot, int nameId, bool link)
        {
            stamp = Mix(stamp, (uint)slot);
            stamp = Mix(stamp, (uint)nameId);
            return Mix(stamp, link ? 1u : 0u);
        }

        static ulong Mix(ulong h, uint word)
        {
            h = (h ^ word) * Prime;
            return h ^ (h >> 29);
        }
    }

    // ══ 5. NOT YET OUT — the one "is this row out yet?" predicate ═══════════════════════════════════════════════════

    /// <summary>Announced but not out: somebody RULED it unavailable and its release instant has not passed. The greyed
    /// row, the play gate, <c>PlayableOnly</c> and the facts strip all read this one rule. An UNRULED row is never
    /// not-yet-out (0.2.9's null ≠ Unavailable), and a passed <paramref name="availableAt"/> heals a stale verdict without
    /// a refetch (the release drop). <paramref name="availableAt"/> 0 = no instant.</summary>
    public static bool NotYetOutOf(bool availabilityKnown, bool unavailable, int availableAt, long nowUnixSeconds)
        => availabilityKnown && unavailable && !(availableAt != 0 && availableAt <= nowUnixSeconds);

    /// <inheritdoc cref="NotYetOutOf"/>
    public bool NotYetOut(long nowUnixSeconds)
        => NotYetOutOf(Knows(TrackFields.Availability), (FlagBits & (uint)TrackFlags.Unavailable) != 0, AvailableAt,
                       nowUnixSeconds);

    /// <summary>Ruled unavailable with NO release instant: the catalog said this row does not exist for this account (a
    /// terminal per-entity envelope verdict — withdrawn, region-locked, gone), and no date will ever heal it. The greyed
    /// row, the play gate and <c>PlayableOnly</c> treat it exactly like <see cref="NotYetOutOf"/>; the duration cell
    /// states "Unavailable" where a pending row states WHEN. An UNRULED row is never unplayable (a stray flag is not a
    /// verdict), and a ruled row WITH an instant is not-yet-out or out — never this.</summary>
    public static bool Unplayable(bool availabilityKnown, bool unavailable, int availableAt)
        => availabilityKnown && unavailable && availableAt == 0;

    /// <inheritdoc cref="Unplayable(bool,bool,int)"/>
    public bool Unplayable()
        => Unplayable(Knows(TrackFields.Availability), (FlagBits & (uint)TrackFlags.Unavailable) != 0, AvailableAt);

    // ══ 5b. FOR DISPLAY — the relink redirection ═════════════════════════════════════════════════════════════════════

    /// <summary>The row whose FACTS a surface paints for this one. Spotify RELINKS a market-restricted id to a canonical
    /// track: the asked row may carry only <see cref="Canonical"/> (a persisted column, a partial answer) and no title,
    /// while the canonical row has everything. A row that knows its own title paints itself; one that does not and
    /// points at a valid canonical row paints THAT; one with neither paints itself (thin). Identity — the uri, the
    /// heart, the play target, the availability verdict — stays this row's: only what is READ is redirected. PURE.</summary>
    public Track ForDisplay => Knows(TrackFields.Title) || !Canonical.IsValid ? this : Canonical;

    // ══ 6. THE COMMAND BAR FIT ═══════════════════════════════════════════════════════════════════════════════════════

    [Flags]
    public enum InlineCommand : byte { None = 0, Shuffle = 1, Sort = 2, Density = 4, Select = 8 }

    /// <summary>Measured LABELED widths of the commands whose presence the resolver controls.</summary>
    public readonly record struct CommandWidths(float Play, float Tune, float Shuffle, float Sort, float Density, float Select);

    public readonly record struct CommandBarFit(InlineCommand Inline, bool SearchExpanded, float SearchWidth)
    {
        public bool Has(InlineCommand command) => (Inline & command) != 0;
        public int Richness =>
            (SearchExpanded ? 8 : 0)
            + (Has(InlineCommand.Shuffle) ? 4 : 0)
            + (Has(InlineCommand.Sort) ? 2 : 0)
            + (Has(InlineCommand.Density) ? 1 : 0)
            + (Has(InlineCommand.Select) ? 1 : 0);
    }

    /// <summary>The command bar PROMOTES, it does not shrink: every inline command is icon + label, and one that does not
    /// fit is evicted into "…". Never returns a layout wider than its input.</summary>
    public static class CommandBarLayout
    {
        public const float MoreWidth = 32f;
        /// <summary>At rest the search affordance is TWO adjacent buttons (query + filter); the field opens only when invoked.</summary>
        public const float SearchIconWidth = 66f;
        public const float SearchMinExplicit = 160f;
        public const float SearchPreferred = 240f;
        public const float SearchMax = 280f;
        public const float Gap = 2f;
        public const float SearchGap = 8f;
        public const float GroupSeparatorWidth = 17f;
        public const float PromotionHysteresis = 16f;

        /// <summary>Hysteresis applies in EVERY mode — it used to be skipped while search was open, exactly when evicted
        /// commands re-measure mid-animation and the promoted set oscillated. Narrowing stays immediate.</summary>
        public static CommandBarFit Resolve(float available, in CommandWidths widths, bool vertical, bool hasTune,
                                            bool hasSelect, bool explicitSearch, CommandBarFit? previous = null)
        {
            available = MathF.Max(0f, available);
            var candidate = ResolveCore(available, widths, vertical, hasTune, hasSelect, explicitSearch);
            if (previous is not { } old || candidate.Richness <= old.Richness)
                return candidate;
            return ResolveCore(MathF.Max(0f, available - PromotionHysteresis), widths, vertical, hasTune, hasSelect,
                               explicitSearch);
        }

        static CommandBarFit ResolveCore(float available, in CommandWidths widths, bool vertical, bool hasTune,
                                         bool hasSelect, bool explicitSearch)
        {
            float mandatory = MoreWidth;
            int mandatoryCount = 1; // More
            if (!vertical) { mandatory += widths.Play; mandatoryCount++; }
            if (hasTune) { mandatory += widths.Tune; mandatoryCount++; }
            mandatory += MathF.Max(0, mandatoryCount - 1) * Gap;

            bool expanded = explicitSearch;
            float reservedSearch = expanded ? SearchMinExplicit : SearchIconWidth;

            InlineCommand inline = InlineCommand.None;
            float used = mandatory + SearchGap + reservedSearch;

            void Add(InlineCommand command, float width, bool viewCommand)
            {
                float extra = Gap + width;
                bool firstView = viewCommand
                    && (inline & (InlineCommand.Sort | InlineCommand.Density | InlineCommand.Select)) == 0;
                if (firstView && (!vertical || hasTune || mandatoryCount > 1)) extra += GroupSeparatorWidth;
                if (used + extra > available) return;
                used += extra;
                inline |= command;
            }

            if (!vertical) Add(InlineCommand.Shuffle, widths.Shuffle, viewCommand: false);
            Add(InlineCommand.Sort, widths.Sort, viewCommand: true);
            Add(InlineCommand.Density, widths.Density, viewCommand: true);
            if (hasSelect) Add(InlineCommand.Select, widths.Select, viewCommand: true);

            float searchWidth = reservedSearch;
            if (expanded)
            {
                float spare = MathF.Max(0f, available - used);
                searchWidth = Math.Clamp(reservedSearch + spare, SearchMinExplicit, SearchMax);
            }
            return new CommandBarFit(inline, expanded, searchWidth);
        }
    }

    // ══ 7. THE FILTER MODEL ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Which textual field the list query searches.</summary>
    public enum SearchScope : byte { Everything = 0, Title = 1, Artist = 2, Album = 3 }
    /// <summary>A three-way rule for a boolean trait: include everything, hide matches, or show only matches.</summary>
    public enum TraitMode : byte { All = 0, Hide = 1, Only = 2 }
    [Flags]
    public enum FilterFlags : byte { None = 0, LikedOnly = 1, PlayableOnly = 2 }
    public enum DurationRange : byte { Any = 0, UnderThreeMinutes = 1, ThreeToFiveMinutes = 2, OverFiveMinutes = 3 }
    public enum AddedRange : byte { Any = 0, LastSevenDays = 1, LastThirtyDays = 2, LastSixMonths = 3, LastYear = 4 }
    public enum OriginFilter : byte { Any = 0, Streamed = 1, Local = 2 }
    /// <summary>Tempo in a listener's vocabulary: 90 separates ballad from mid, 120 is four-on-the-floor, 140 is where
    /// drum-and-bass begins. A row with no tempo matches only <see cref="Any"/>, so an un-enriched list is never emptied.</summary>
    public enum TempoBand : byte { Any = 0, Under90 = 1, From90To119 = 2, From120To139 = 3, From140AndUp = 4 }

    /// <summary>The complete transient track-list filter; <c>default</c> filters nothing and searches everything.
    /// <paramref name="Camelot"/>: the wheel slot byte, 0 = any key (the stable DJ notation, not the spelled tonic).
    /// <paramref name="Tag"/>: the Liked content-filter chip — one at a time, a lens not a stack.
    /// <paramref name="AddedAfterMs"/>/<paramref name="AddedBeforeMs"/>: an arbitrary saved-date window in unix ms,
    /// half-open (After, Before], 0/0 off — what a sparkline bar means; MUTUALLY EXCLUSIVE with <paramref name="Added"/>,
    /// so use <see cref="WithAddedWindow"/>/<see cref="WithAddedRange"/>, never a bare <c>with</c>.
    /// <paramref name="ArtistSlot"/>: an EXACT credited artist (0 = none) — a lens means "these songs credit THIS artist",
    /// not a substring. <paramref name="ArtistName"/>: display only, carried WITH the slot so the lens header can name an
    /// artist the filter excluded; filters nothing and is not counted. <paramref name="ReleaseYearMin"/>/<paramref
    /// name="ReleaseYearMax"/>: inclusive, 0/0 off.</summary>
    public readonly record struct FilterState(
        SearchScope SearchScope = default,
        TraitMode ExplicitMode = TraitMode.All,
        TraitMode VideoMode = TraitMode.All,
        FilterFlags Flags = FilterFlags.None,
        DurationRange Duration = DurationRange.Any,
        AddedRange Added = AddedRange.Any,
        OriginFilter Origin = OriginFilter.Any,
        TempoBand Tempo = TempoBand.Any,
        byte Camelot = 0,
        string? Tag = null,
        long AddedAfterMs = 0L,
        long AddedBeforeMs = 0L,
        int ArtistSlot = 0,
        string? ArtistName = null,
        int ReleaseYearMin = 0,
        int ReleaseYearMax = 0)
    {
        public static readonly FilterState Default = new();

        public bool LikedOnly => (Flags & FilterFlags.LikedOnly) != 0;
        public bool PlayableOnly => (Flags & FilterFlags.PlayableOnly) != 0;
        public bool IsDefault => Equals(Default);

        /// <summary>The number on the Filter affordance: each toggle and each non-default facet counts once; a window is ONE
        /// facet however many endpoints it names.</summary>
        public int ActiveCount
        {
            get
            {
                int n = SearchScope == SearchScope.Everything ? 0 : 1;
                if (ExplicitMode != TraitMode.All) n++;
                if (VideoMode != TraitMode.All) n++;
                if (LikedOnly) n++;
                if (PlayableOnly) n++;
                if (Duration != DurationRange.Any) n++;
                if (Added != AddedRange.Any) n++;
                if (Origin != OriginFilter.Any) n++;
                if (Tempo != TempoBand.Any) n++;
                if (Camelot != 0) n++;
                if (!string.IsNullOrEmpty(Tag)) n++;
                if (AddedAfterMs != 0L || AddedBeforeMs != 0L) n++;
                if (ArtistSlot != 0) n++;
                if (ReleaseYearMin != 0 || ReleaseYearMax != 0) n++;
                return n;
            }
        }

        /// <summary>Set the coarse preset, clearing any window: ANDing both would return fewer rows than either promised.</summary>
        public FilterState WithAddedRange(AddedRange range)
            => this with { Added = range, AddedAfterMs = 0L, AddedBeforeMs = 0L };

        /// <summary>Set (0/0 clears) the explicit window, clearing the preset.</summary>
        public FilterState WithAddedWindow(long afterMs, long beforeMs)
            => this with { AddedAfterMs = afterMs, AddedBeforeMs = beforeMs, Added = AddedRange.Any };

        /// <summary>Set (0 clears) the exact-artist lens; the name travels with the slot and is dropped with it.</summary>
        public FilterState WithArtist(int artistSlot, string? displayName = null)
            => artistSlot > 0
                ? this with { ArtistSlot = artistSlot, ArtistName = displayName }
                : this with { ArtistSlot = 0, ArtistName = null };

        /// <summary>Set (0/0 clears) the inclusive release-year window.</summary>
        public FilterState WithReleaseYear(int min, int max)
            => this with { ReleaseYearMin = min, ReleaseYearMax = max };
    }

    /// <summary>The resolved values the filter predicate reads off one row — a ref struct, so the credit and tag spans
    /// are the edge table's own CSR runs (never held across a drain). <see cref="AddedAt"/> is unix seconds (0 = no
    /// stamp); <see cref="TempoBpm"/> 0 = no kind-222 answer; <see cref="Camelot"/> 0 = no wheel slot; <see cref="Year"/>
    /// 0 = unknown.</summary>
    public readonly ref struct FilterRow
    {
        public FilterRow() { }

        public string Title { get; init; } = "";
        public string ArtistLine { get; init; } = "";
        public string AlbumTitle { get; init; } = "";
        public bool Explicit { get; init; }
        public bool HasVideo { get; init; }
        public bool Saved { get; init; }
        public bool NotYetOut { get; init; }
        /// <summary><see cref="Track.Unplayable()"/>: ruled unavailable with no release instant.</summary>
        public bool Unplayable { get; init; }
        public bool Local { get; init; }
        public int AddedAt { get; init; }
        public int Year { get; init; }
        public double TempoBpm { get; init; }
        public byte Camelot { get; init; }
        public int DurationMs { get; init; }
        public ReadOnlySpan<int> ArtistSlots { get; init; }
        public ReadOnlySpan<StringId> Tags { get; init; }

        /// <summary>One track's row. Strings are the INTERNED ones (<c>Entities.Strings.Resolve</c>) — never concatenated —
        /// and the credit line is the commit-time <c>ArtistLine</c>. Audio values read as unknown until
        /// <c>Knows(Audio)</c>, so a cold row is never swept into a tempo band.</summary>
        public static FilterRow Of(Track t, int addedAt, bool saved, long nowUnixSeconds)
        {
            if (!t.IsValid) return new FilterRow();
            var album = t.Album;
            bool audio = t.Knows(TrackFields.Audio);
            return new FilterRow
            {
                Title = Entities.Strings.Resolve(t.TitleId),
                ArtistLine = Entities.Strings.Resolve(t.ArtistLineId),
                AlbumTitle = album.IsValid ? Entities.Strings.Resolve(album.TitleId) : "",
                Explicit = t.IsExplicit,
                HasVideo = t.HasVideo,
                Saved = saved,
                NotYetOut = t.NotYetOut(nowUnixSeconds),
                Unplayable = t.Unplayable(),
                Local = t.IsLocal,
                AddedAt = addedAt,
                Year = t.Year,
                TempoBpm = audio ? t.Tempo / 10d : 0d,
                Camelot = audio ? t.Camelot : (byte)0,
                DurationMs = t.DurationMs,
                ArtistSlots = t.ArtistSlots,
                Tags = t.Tags,
            };
        }
    }

    /// <summary>The filter predicate shared by the table and the tests.</summary>
    public static class FilterModel
    {
        const long SecondsPerDay = 86_400L;

        public static bool Matches(in FilterRow row, string query, in FilterState filter, long nowUnixSeconds)
        {
            if (!MatchesTrait(row.Explicit, filter.ExplicitMode)) return false;
            if (!MatchesTrait(row.HasVideo, filter.VideoMode)) return false;
            if (filter.LikedOnly && !row.Saved) return false;
            // Only a CONFIRMED unavailable is hidden: an unruled row is never not-yet-out nor unplayable, so a surface
            // that carries no verdict at all is never emptied — and a passed release instant keeps a stale verdict's row.
            // A ruled row with NO instant (`Track.Unplayable`) has nothing to wait for and is hidden outright.
            if (filter.PlayableOnly && (row.NotYetOut || row.Unplayable)) return false;

            if (filter.Origin == OriginFilter.Streamed && row.Local) return false;
            if (filter.Origin == OriginFilter.Local && !row.Local) return false;

            if (filter.Tag is { Length: > 0 } tag && !HasTag(row.Tags, tag)) return false;
            if (filter.ArtistSlot != 0 && !HasArtist(row.ArtistSlots, filter.ArtistSlot)) return false;
            if (!MatchesAddedWindow(row.AddedAt, filter.AddedAfterMs, filter.AddedBeforeMs)) return false;
            if (!MatchesReleaseYear(row.Year, filter.ReleaseYearMin, filter.ReleaseYearMax)) return false;
            if (!MatchesTempo(row.TempoBpm, filter.Tempo)) return false;
            if (filter.Camelot != 0 && row.Camelot != filter.Camelot) return false;

            if (!MatchesDuration(row.DurationMs, filter.Duration)) return false;
            if (!MatchesAdded(row.AddedAt, filter.Added, nowUnixSeconds)) return false;
            return query.Length == 0 || MatchesQuery(in row, query, filter.SearchScope);
        }

        public static bool MatchesQuery(in FilterRow row, string query, SearchScope scope) => scope switch
        {
            SearchScope.Title => row.Title.Contains(query, StringComparison.OrdinalIgnoreCase),
            SearchScope.Artist => row.ArtistLine.Contains(query, StringComparison.OrdinalIgnoreCase),
            SearchScope.Album => row.AlbumTitle.Contains(query, StringComparison.OrdinalIgnoreCase),
            _ => row.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                 || row.ArtistLine.Contains(query, StringComparison.OrdinalIgnoreCase)
                 || row.AlbumTitle.Contains(query, StringComparison.OrdinalIgnoreCase),
        };

        /// <summary>THE tempo boundary table — the predicate, the Liked rail's tempo facts and the flyout's labels all read
        /// it, so a pill and the rows it lenses cannot disagree by a boundary. Half-open; non-positive or NaN has no band.</summary>
        public static TempoBand BandOf(double bpm)
            => double.IsNaN(bpm) || bpm <= 0d ? TempoBand.Any
             : bpm < 90d ? TempoBand.Under90
             : bpm < 120d ? TempoBand.From90To119
             : bpm < 140d ? TempoBand.From120To139
             : TempoBand.From140AndUp;

        static bool MatchesTrait(bool hasTrait, TraitMode mode) => mode switch
        {
            TraitMode.Hide => !hasTrait,
            TraitMode.Only => hasTrait,
            _ => true,
        };

        /// <summary>Case-insensitive on the DISPLAY name — the one string the chip bar shows and the store holds.</summary>
        static bool HasTag(ReadOnlySpan<StringId> tags, string tag)
        {
            for (int i = 0; i < tags.Length; i++)
                if (string.Equals(Entities.Strings.Resolve(tags[i]), tag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>EVERY credit counts, not just the lead — a feature credit is a real reason a track is in the library.
        /// The slot IS the identity, so two artists sharing a display name can never collapse into one lens.</summary>
        static bool HasArtist(ReadOnlySpan<int> artists, int slot)
        {
            for (int i = 0; i < artists.Length; i++) if (artists[i] == slot) return true;
            return false;
        }

        /// <summary>Half-open <c>(after, before]</c>, 0 on an endpoint = unbounded on that side. Half-open because the
        /// buckets are rolling windows laid end to end; a like on the seam belongs to exactly one bar. An UNSTAMPED row
        /// never matches a window that is on — the bar counted stamped likes only.</summary>
        static bool MatchesAddedWindow(int addedAt, long afterMs, long beforeMs)
        {
            if (afterMs == 0L && beforeMs == 0L) return true;
            if (addedAt <= 0) return false;
            long ms = addedAt * 1000L;
            if (afterMs != 0L && ms <= afterMs) return false;
            if (beforeMs != 0L && ms > beforeMs) return false;
            return true;
        }

        /// <summary>Inclusive; an undated row never matches a window that is on.</summary>
        static bool MatchesReleaseYear(int year, int min, int max)
        {
            if (min == 0 && max == 0) return true;
            if (year <= 0) return false;
            if (min != 0 && year < min) return false;
            if (max != 0 && year > max) return false;
            return true;
        }

        static bool MatchesTempo(double bpm, TempoBand band)
        {
            if (band == TempoBand.Any) return true;
            if (double.IsNaN(bpm) || bpm <= 0d) return false;   // unknown tempo cannot satisfy an explicit band
            return BandOf(bpm) == band;
        }

        static bool MatchesDuration(long durationMs, DurationRange range) => range switch
        {
            DurationRange.UnderThreeMinutes => durationMs < 180_000L,
            DurationRange.ThreeToFiveMinutes => durationMs is >= 180_000L and <= 300_000L,
            DurationRange.OverFiveMinutes => durationMs > 300_000L,
            _ => true,
        };

        static bool MatchesAdded(int addedAt, AddedRange range, long nowUnixSeconds)
        {
            if (range == AddedRange.Any) return true;
            if (addedAt <= 0) return false;
            int days = range switch
            {
                AddedRange.LastSevenDays => 7,
                AddedRange.LastThirtyDays => 30,
                AddedRange.LastSixMonths => 180,
                _ => 365,
            };
            return addedAt >= nowUnixSeconds - days * SecondsPerDay;
        }
    }

    // ══ 8. THE EXPANDED-ROW FACTS ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What one expanded-row fact IS. Declaration order is PRESENTATION order (never persisted).</summary>
    public enum FactKind : byte
    {
        Plays, Bpm, Key, Added, Duration, Album, Released, AddedBy, Isrc, Descriptors,
        Explicit, Video, LocalFile, Unavailable,
    }

    /// <summary>How a fact renders. <see cref="Pending"/> is the ONE em dash — "asked, not answered yet" — for the two
    /// enrichment planes (kind 222 tempo/key, kind 185 plays).</summary>
    public enum FactForm : byte { Value, Link, Chips, Flag, Pending }

    /// <summary>Camelot's two rings; Unknown when there is no wheel slot (the bare tonic says nothing about mode).</summary>
    public enum KeyMode : byte { Unknown, Major, Minor }

    /// <summary>One fact: <paramref name="Value"/> is the display string (empty for a flag, whose label IS the fact);
    /// <paramref name="LinkUri"/> only on a Link; <paramref name="Chips"/> only on Chips; <paramref name="Person"/> only on
    /// AddedBy, and only when the page resolved the full profile (the strip then draws the column's avatar chip).</summary>
    public readonly record struct Fact(FactKind Kind, FactForm Form, string Value, EntityUri LinkUri = default,
                                       IReadOnlyList<string>? Chips = null, User Person = default);

    /// <summary>A hero fact's two halves: the FIGURE and the small gloss beneath it — null far more often than not, and
    /// never invented ("min" under a duration is noise).</summary>
    public readonly record struct FactSplit(string Value, string? Unit);

    /// <summary>What the fact list needs that the track does not carry. <paramref name="TempoPending"/> /
    /// <paramref name="PlaysPending"/> mean "this surface ASKED for the lane" (the same gating the lanes use), so the
    /// honest dash appears exactly where the table would reserve a lane. Culture and zone are nullable only so the app
    /// can omit them; tests always inject both. The mode words are injected so this stays free of the loc runtime.</summary>
    public readonly record struct FactsOptions(
        bool TempoPending = false,
        bool PlaysPending = false,
        bool HasVideo = false,
        string? AddedByName = null,
        User AddedByProfile = default,
        CultureInfo? Culture = null,
        TimeZoneInfo? Zone = null,
        string? MajorWord = null,
        string? MinorWord = null);

    /// <summary>The track's own facts as RESOLVED values (the drawer builds it once, on expand). Dates are unix seconds, 0 =
    /// none; <paramref name="TempoBpm"/> 0 = unknown; <paramref name="Tags"/> null = not fetched, empty = none (both render
    /// nothing); <paramref name="AddedByRaw"/> is the membership's raw adder id when no profile resolved.</summary>
    public readonly record struct FactsInput(
        bool HasIdentity = false,
        long PlayCount = 0,
        double TempoBpm = 0d,
        string? CamelotCode = null,
        string? MusicalKey = null,
        int AddedAt = 0,
        int DurationMs = 0,
        string? AlbumName = null,
        EntityUri AlbumUri = default,
        int AvailableAt = 0,
        string? AddedByRaw = null,
        string? Isrc = null,
        IReadOnlyList<string>? Tags = null,
        bool Explicit = false,
        bool Local = false,
        bool AvailabilityKnown = false,
        bool Unavailable = false,
        bool NotYetOut = false)
    {
        /// <summary>One handle's facts. The key and camelot labels go through the exact <see cref="Format"/> tables and
        /// read as absent until <c>Knows(Audio)</c>; <c>AddedByRaw</c> is the caller's (<c>with</c>) — membership is an edge.</summary>
        public static FactsInput Of(Track t, int addedAt, long nowUnixSeconds)
        {
            if (!t.IsValid) return default;
            bool audio = t.Knows(TrackFields.Audio);
            bool ruled = t.Knows(TrackFields.Availability);
            var album = t.Album;
            string[]? tags = null;
            if (t.Knows(TrackFields.Tags))
            {
                var ids = t.Tags;
                tags = new string[ids.Length];
                for (int i = 0; i < ids.Length; i++) tags[i] = Entities.Strings.Resolve(ids[i]);
            }
            return new FactsInput(
                HasIdentity: true,
                PlayCount: t.PlayCount,
                TempoBpm: audio ? t.Tempo / 10d : 0d,
                CamelotCode: audio ? Format.CamelotLabel(t.Camelot) : null,
                MusicalKey: audio ? Format.KeyLabel(t.Key) : null,
                AddedAt: addedAt,
                DurationMs: t.DurationMs,
                AlbumName: album.IsValid ? Entities.Strings.Resolve(album.TitleId) : null,
                AlbumUri: album.IsValid ? album.Uri : default,
                AvailableAt: t.AvailableAt,
                Isrc: t.IsrcId.IsEmpty ? null : Entities.Strings.Resolve(t.IsrcId),
                Tags: tags,
                Explicit: t.IsExplicit,
                Local: t.IsLocal,
                AvailabilityKnown: ruled,
                // The RULED verdict bit — `Unplayable` (no instant) and a pending release (instant ahead) both read true
                // here; `NotYetOut` is what separates them for the strip (a date beside "Unavailable" is a contradiction).
                Unavailable: ruled && !t.IsPlayable,
                NotYetOut: t.NotYetOut(nowUnixSeconds));
        }
    }

    /// <summary>What an expanded row STATES, as one ordered list. The table's lanes are a function of WIDTH (tier and
    /// relief yield Plays, BPM·Key, Added by, Date added); the drawer states EVERY fact at every width, in one fixed order.
    /// Two honesty rules: an ABSENT fact does not render (an em dash beside a label is a claim), and the one exception is
    /// ENRICHMENT PENDING on a surface that asked.</summary>
    public static class Facts
    {
        public const string Dash = Format.Dash;

        /// <summary>The token joining the wheel slot to the spelled key; <see cref="HeroSplit"/> cuts on exactly this.</summary>
        public const string KeySeparator = " · ";

        /// <summary>The ordered list. Order is <see cref="FactKind"/>'s declaration order, unconditionally — presence
        /// decides whether a fact appears, never where. The facts relief drops FIRST lead (what a narrow table stopped
        /// saying), then provenance and measure, release identity, membership, the ISRC, the chips, and the flags last.</summary>
        public static IReadOnlyList<Fact> For(in FactsInput input, FactsOptions options = default)
        {
            var facts = new List<Fact>(12);
            if (!input.HasIdentity) return facts;

            var culture = options.Culture ?? CultureInfo.CurrentCulture;
            var zone = options.Zone ?? TimeZoneInfo.Local;
            // The ONE shared predicate (Track.NotYetOutOf): the greyed row, the play gate and this strip never disagree.
            bool notYetOut = input.NotYetOut;

            // A pending release reports 0 plays because nothing has happened to it yet; Released answers its question.
            if (!notYetOut)
            {
                if (input.PlayCount > 0)
                    facts.Add(new Fact(FactKind.Plays, FactForm.Value, input.PlayCount.ToString("N0", culture)));
                else if (options.PlaysPending)
                    facts.Add(new Fact(FactKind.Plays, FactForm.Pending, Dash));
            }

            if (input.TempoBpm > 0d)
                facts.Add(new Fact(FactKind.Bpm, FactForm.Value, Format.Bpm(input.TempoBpm)));
            else if (options.TempoPending)
                facts.Add(new Fact(FactKind.Bpm, FactForm.Pending, Dash));

            if (PrettyKey(input.CamelotCode, input.MusicalKey, options.MajorWord, options.MinorWord) is { Length: > 0 } key)
                facts.Add(new Fact(FactKind.Key, FactForm.Value, key));
            else if (options.TempoPending)
                facts.Add(new Fact(FactKind.Key, FactForm.Pending, Dash));

            // The EXACT instant, not the lane's "3 days ago" — the reader opened the row to find out precisely when.
            if (input.AddedAt > 0)
                facts.Add(new Fact(FactKind.Added, FactForm.Value, ExactStamp(input.AddedAt, culture, zone)));

            if (input.DurationMs > 0)
                facts.Add(new Fact(FactKind.Duration, FactForm.Value, Format.TrackTime(input.DurationMs)));

            if (input.AlbumName is { Length: > 0 } album)
                facts.Add(new Fact(FactKind.Album, FactForm.Link, album, LinkUri: input.AlbumUri));

            // Released is the TRACK's own earliest-live instant — the album ref carries no date.
            if (input.AvailableAt > 0)
                facts.Add(new Fact(FactKind.Released, FactForm.Value, ExactDate(input.AvailableAt, culture, zone)));

            if (options.AddedByName is { Length: > 0 } who)
                facts.Add(new Fact(FactKind.AddedBy, FactForm.Value, who, Person: options.AddedByProfile));
            else if (input.AddedByRaw is { Length: > 0 } raw)
                facts.Add(new Fact(FactKind.AddedBy, FactForm.Value, raw));

            if (input.Isrc is { Length: > 0 } isrc)
                facts.Add(new Fact(FactKind.Isrc, FactForm.Value, isrc));

            if (input.Tags is { Count: > 0 } tags)
                facts.Add(new Fact(FactKind.Descriptors, FactForm.Chips, "", Chips: tags));

            // Marks, not values: the label is the whole fact.
            if (input.Explicit) facts.Add(new Fact(FactKind.Explicit, FactForm.Flag, ""));
            if (options.HasVideo) facts.Add(new Fact(FactKind.Video, FactForm.Flag, ""));
            if (input.Local) facts.Add(new Fact(FactKind.LocalFile, FactForm.Flag, ""));
            // Only a RULED row; a not-yet-out row already states WHEN, and "Unavailable" beside a date reads as a contradiction.
            if (!notYetOut && input.AvailabilityKnown && input.Unavailable)
                facts.Add(new Fact(FactKind.Unavailable, FactForm.Flag, ""));

            return facts;
        }

        /// <summary>Read AS A NUMBER: Plays · BPM · Key (what relief yields first) and Duration (the one measure every row
        /// carries). A switch over KINDS, not forms — a Pending Plays is still a hero slot holding a dash.</summary>
        public static bool IsHeroFact(FactKind kind) => kind switch
        {
            FactKind.Plays or FactKind.Bpm or FactKind.Key or FactKind.Duration => true,
            _ => false,
        };

        /// <summary>Only a Camelot-coded Key has two parts ("2B" over "F# major"); every other fact is one. Inverts
        /// <see cref="PrettyKey"/> across <see cref="KeySeparator"/>; a pending fact splits as ("—", null) with no branch.</summary>
        public static FactSplit HeroSplit(in Fact f)
        {
            if (f.Kind != FactKind.Key) return new FactSplit(f.Value, null);
            int cut = f.Value.IndexOf(KeySeparator, StringComparison.Ordinal);
            return cut < 0
                ? new FactSplit(f.Value, null)
                : new FactSplit(f.Value.Substring(0, cut), f.Value.Substring(cut + KeySeparator.Length));
        }

        /// <summary>The loc KEY for a fact's label, beside the ordering, so a new kind cannot ship label-less.</summary>
        public static string LabelKey(FactKind kind) => kind switch
        {
            FactKind.Plays => Strings.Detail.TrackFacts.Plays,
            FactKind.Bpm => Strings.Detail.TrackFacts.Bpm,
            FactKind.Key => Strings.Detail.TrackFacts.Key,
            FactKind.Added => Strings.Detail.TrackFacts.Added,
            FactKind.Duration => Strings.Detail.TrackFacts.Duration,
            FactKind.Album => Strings.Detail.TrackFacts.Album,
            FactKind.Released => Strings.Detail.TrackFacts.Released,
            FactKind.AddedBy => Strings.Detail.TrackFacts.AddedBy,
            FactKind.Isrc => Strings.Detail.TrackFacts.Isrc,
            FactKind.Descriptors => Strings.Detail.TrackFacts.Descriptors,
            FactKind.Explicit => Strings.Detail.TrackFacts.Explicit,
            FactKind.Video => Strings.Detail.TrackFacts.Video,
            FactKind.LocalFile => Strings.Detail.TrackFacts.LocalFile,
            _ => Strings.Detail.TrackFacts.Unavailable,
        };

        /// <summary>ONE key token for a narrow lane: the Camelot slot (matches swatch and filter), else the tonic. Never both.</summary>
        public static string? KeyLabel(string? camelotCode, string? musicalKey) =>
            camelotCode is { Length: > 0 } c ? c
            : musicalKey is { Length: > 0 } k ? k
            : null;

        /// <summary>Mode off the wheel's own suffix: B is the major ring, A the minor — the only mode signal there is.</summary>
        public static KeyMode ModeOf(string? camelotCode)
        {
            if (camelotCode is not { Length: > 1 } c) return KeyMode.Unknown;
            char suffix = char.ToUpperInvariant(c[c.Length - 1]);
            return suffix == 'B' ? KeyMode.Major : suffix == 'A' ? KeyMode.Minor : KeyMode.Unknown;
        }

        /// <summary>The prose key line — the JOIN of <see cref="KeySplit"/>: "8B · C major", "8B · C" (no mode words),
        /// "8B", "C", or null.</summary>
        public static string? PrettyKey(string? camelotCode, string? musicalKey, string? major = null, string? minor = null)
            => KeySplit(camelotCode, musicalKey, major, minor) is { } s
                ? s.Unit is null ? s.Value : s.Value + KeySeparator + s.Unit
                : null;

        /// <summary>The key's two halves, decided ONCE. Every partial state degrades instead of inventing the missing half:
        /// no wheel slot ⇒ the spelled tonic is the whole value (and has no honest mode).</summary>
        public static FactSplit? KeySplit(string? camelotCode, string? musicalKey, string? major = null, string? minor = null)
        {
            string? camelot = camelotCode is { Length: > 0 } c ? c : null;
            string? tonic = musicalKey is { Length: > 0 } k ? k : null;
            if (camelot is null && tonic is null) return null;

            string? mode = ModeOf(camelot) switch
            {
                KeyMode.Major => major is { Length: > 0 } ? major : null,
                KeyMode.Minor => minor is { Length: > 0 } ? minor : null,
                _ => null,
            };
            string? spelled = tonic is null ? null : mode is null ? tonic : tonic + " " + mode;
            return camelot is null ? new FactSplit(spelled!, null) : new FactSplit(camelot, spelled);
        }

        /// <summary>Full date + time of day in the reader's culture and zone ("f").</summary>
        public static string ExactStamp(long unixSeconds, CultureInfo culture, TimeZoneInfo zone) =>
            TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds), zone).DateTime.ToString("f", culture);

        /// <summary>A release instant is a DAY ("D"); minute precision beside Added would be false precision.</summary>
        public static string ExactDate(long unixSeconds, CultureInfo culture, TimeZoneInfo zone) =>
            TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds), zone).DateTime.ToString("D", culture);
    }

    // ══ 9. THE MEMBERSHIP DIFF ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One row-level change: an add has no <see cref="OldIndex"/>, a remove no <see cref="NewIndex"/>, a move both.</summary>
    public readonly record struct RowChange(string Key, int? OldIndex, int? NewIndex);

    /// <summary>The keyed diff between two snapshots. <see cref="Moves"/> lists EVERY survivor whose index changed,
    /// including rows merely displaced (the FLIP pass needs them); classification counts only STRUCTURAL changes, so one
    /// insert above 999 survivors is a small edit, not a reset. <see cref="IsReset"/> = a curated re-cut.</summary>
    public sealed record MembershipDelta(
        IReadOnlyList<RowChange> Adds,
        IReadOnlyList<RowChange> Removes,
        IReadOnlyList<RowChange> Moves,
        double RetainedFraction,
        bool IsReset)
    {
        public bool IsEmpty => Adds.Count == 0 && Removes.Count == 0 && Moves.Count == 0;
    }

    /// <summary>The O(n) keyed row diff and THE per-row identity every drawer, like-edge and expansion keys on. Identity =
    /// the membership <c>ItemId</c> (stable across reorders) else <c>uri#occurrence</c>, so a list holding a song twice
    /// still diffs row-accurately — whichever network path produced the new list.</summary>
    public static class MembershipDiff
    {
        const double ResetRetainedBelow = 0.5;
        const int ResetStructuralAbove = 40;

        /// <summary>The per-row keys for one snapshot. <paramref name="itemIds"/> is parallel to <paramref name="tracks"/>
        /// and may be empty (an album has no membership ids).</summary>
        public static string[] Keys(ReadOnlySpan<Track> tracks, ReadOnlySpan<StringId> itemIds)
        {
            var occ = new Dictionary<EntityId, int>();
            var keys = new string[tracks.Length];
            for (int i = 0; i < tracks.Length; i++)
                keys[i] = KeyOf(tracks[i], i < itemIds.Length ? itemIds[i] : default, occ);
            return keys;
        }

        public static MembershipDelta Diff(ReadOnlySpan<string> oldKeys, ReadOnlySpan<string> newKeys)
        {
            var oldIndex = new Dictionary<string, int>(oldKeys.Length, StringComparer.Ordinal);
            for (int i = 0; i < oldKeys.Length; i++) oldIndex[oldKeys[i]] = i;

            var adds = new List<RowChange>();
            var moves = new List<RowChange>();
            var consumed = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < newKeys.Length; i++)
            {
                var key = newKeys[i];
                if (oldIndex.TryGetValue(key, out int was) && consumed.Add(key))
                {
                    if (was != i) moves.Add(new RowChange(key, was, i));
                }
                else adds.Add(new RowChange(key, null, i));
            }

            var removes = new List<RowChange>();
            foreach (var kv in oldIndex)
                if (!consumed.Contains(kv.Key)) removes.Add(new RowChange(kv.Key, kv.Value, null));
            removes.Sort(static (a, b) => a.OldIndex!.Value.CompareTo(b.OldIndex!.Value));   // dictionary order isn't positional

            int retained = consumed.Count;
            double retainedFraction = oldKeys.Length == 0 ? 1.0 : (double)retained / oldKeys.Length;
            int structural = adds.Count + removes.Count;
            // A first fill (old empty) is a load, not an edit — never a reset.
            bool isReset = oldKeys.Length > 0 && (retainedFraction < ResetRetainedBelow || structural > ResetStructuralAbove);

            return new MembershipDelta(adds, removes, moves, retainedFraction, isReset);
        }

        /// <summary>ONE row's identity for per-row UI STATE: the item id where the membership has one, else the uri
        /// qualified by DISPLAY POSITION — the fallback, never the primary, so a real playlist keeps its drawer attached
        /// across a re-sort and only a uid-less list (an album, where duplicates do not occur) closes it.</summary>
        public static string RowKey(Track t, StringId itemId, int displayIndex)
        {
            if (!itemId.IsEmpty && Entities.Strings.Resolve(itemId) is { Length: > 0 } uid) return uid;
            var id = t.Id;
            return id.IsEmpty ? "" : id.Text + "#@" + displayIndex.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Does <paramref name="key"/> (a <see cref="RowKey"/>) name THIS row? Compared IN PLACE — the callers are
        /// bound re-skin closures that run per row per scroll step — so a gid uri formats into a stack buffer.</summary>
        public static bool RowKeyMatches(string? key, Track t, StringId itemId, int displayIndex)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (!itemId.IsEmpty)
            {
                string uid = Entities.Strings.Resolve(itemId);
                if (uid.Length > 0) return string.Equals(key, uid, StringComparison.Ordinal);
            }
            var id = t.Id;
            if (id.IsEmpty) return false;
            Span<char> buf = stackalloc char[EntityId.MaxGidTextChars];
            ReadOnlySpan<char> uri = id.Form == EntityForm.Text
                ? Entities.Strings.Resolve(id.TextId).AsSpan()
                : buf[..id.Format(buf)];
            if (uri.Length == 0) return false;
            var span = key.AsSpan();
            if (!span.StartsWith(uri, StringComparison.Ordinal)) return false;
            var tail = span[uri.Length..];
            return tail.Length > 2 && tail[0] == '#' && tail[1] == '@'
                && int.TryParse(tail[2..], NumberStyles.None, CultureInfo.InvariantCulture, out int i)
                && i == displayIndex;
        }

        static string KeyOf(Track t, StringId itemId, Dictionary<EntityId, int> occ)
        {
            if (!itemId.IsEmpty && Entities.Strings.Resolve(itemId) is { Length: > 0 } uid) return uid;
            var id = t.Id;
            int n = occ.TryGetValue(id, out var c) ? c + 1 : 0;
            occ[id] = n;
            return id.Text + "#" + n.ToString(CultureInfo.InvariantCulture);
        }
    }

    // ══ 10. THE REORDER RULES ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What an insertion's chip CLAIMS the drop will do; <see cref="AddContainer"/> is "some tracks, count unknown".</summary>
    public enum DropVerb : byte { None = 0, MoveRows = 1, AddTracks = 2, AddContainer = 3 }

    /// <summary>When a same-playlist move is legal and how it is addressed. The every-row-keyed half is
    /// <c>Drag.RowsAreKeyed</c> (Platform/Drag.cs).</summary>
    public static class ReorderRules
    {
        /// <summary>A same-list move addresses MEMBERSHIP rows through the DISPLAYED order, so it is unambiguous only while
        /// the display IS the membership order: natural sort, no query, no filter.</summary>
        public static bool AllowsSameListMove(bool naturalOrder, string query, in FilterState filters)
            => naturalOrder && query.Length == 0 && filters.IsDefault;

        /// <summary>A same-list drop MOVES rows; a foreign snapshot ADDS a known count; a cold container adds an unknown
        /// one, said without a fabricated number.</summary>
        public static DropVerb VerbFor(bool sameList, int sourceRowCount, int trackCount)
            => sameList
                ? (sourceRowCount > 0 ? DropVerb.MoveRows : DropVerb.None)
                : trackCount > 0 ? DropVerb.AddTracks : DropVerb.AddContainer;

        /// <summary>May Alt+↑/↓ run? The drag's ambiguity gate plus the write gate — an outcome-equivalent COMMAND, not a
        /// simulated keyboard drag.</summary>
        public static bool AllowsBlockMove(bool canEditItems, bool naturalOrder, string query, in FilterState filters)
            => canEditItems && AllowsSameListMove(naturalOrder, query, in filters);

        /// <summary>The PRE-move insertion index for shifting the selection by ±1, or −1. "Pre-move" is the backend's own
        /// convention (insert before the row CURRENTLY at this index), which is why DOWN targets <c>max + 2</c>;
        /// pre-correcting for the lifted rows moves the block twice. A GAPPED (or duplicated) selection is refused.</summary>
        public static int BlockMoveTarget(ReadOnlySpan<int> originalIndices, int itemCount, int delta)
        {
            if (originalIndices.Length == 0 || itemCount <= 0 || (delta != -1 && delta != 1)) return -1;
            int min = originalIndices[0], max = originalIndices[0];
            for (int i = 1; i < originalIndices.Length; i++)
            {
                if (originalIndices[i] < min) min = originalIndices[i];
                if (originalIndices[i] > max) max = originalIndices[i];
            }
            if (min < 0 || max >= itemCount) return -1;
            if (max - min + 1 != originalIndices.Length) return -1;   // gapped (or duplicated) selection
            if (delta < 0) return min > 0 ? min - 1 : -1;
            return max + 1 < itemCount ? max + 2 : -1;
        }

        /// <summary>The MEMBERSHIP index a DISPLAY slot names. Slot 0 is the head of the display and the end is the end of
        /// membership, so the two edges never read through the view map.</summary>
        public static int OriginalInsertionIndex(ReadOnlySpan<int> view, int trackCount, int displaySlot)
        {
            if (displaySlot <= 0) return view.Length > 0 ? view[0] : 0;
            if (displaySlot >= view.Length) return trackCount;
            return view[displaySlot];
        }

        /// <summary>Will the move's ANCHOR row carry an item id? The wire reorder is ONE keyed MOV with no positional
        /// fallback, so this is answered BEFORE the gesture commits. The two ends (add_first / add_last) need no anchor;
        /// otherwise the anchor is the predecessor at <paramref name="at"/> − 1, walking back over rows that are
        /// themselves moving (a gapped selection lands as one run) — nothing left above is add_first.
        /// <paramref name="itemIds"/> is membership in ORIGINAL order; <paramref name="moved"/> carries original indices.</summary>
        public static bool AnchorRowIsKeyedAt(ReadOnlySpan<StringId> itemIds, ReadOnlySpan<RowRef> moved, int at)
        {
            if (at <= 0 || at >= itemIds.Length) return true;   // add_first / add_last — no anchor row to name
            int j = at - 1;
            while (j >= 0 && IsMoved(moved, j)) j--;
            if (j < 0) return true;                             // everything above is moving → add_first
            return !itemIds[j].IsEmpty;

            static bool IsMoved(ReadOnlySpan<RowRef> rows, int original)
            {
                for (int i = 0; i < rows.Length; i++) if (rows[i].Index == original) return true;
                return false;
            }
        }

        /// <summary>The same question in DISPLAY coordinates (what a drop hands over).</summary>
        public static bool AnchorRowIsKeyed(ReadOnlySpan<int> view, ReadOnlySpan<StringId> itemIds,
                                            ReadOnlySpan<RowRef> moved, int displaySlot)
            => AnchorRowIsKeyedAt(itemIds, moved, OriginalInsertionIndex(view, itemIds.Length, displaySlot));

        /// <summary>Invert the display→original map for ONE row (the payload carries ORIGINAL indices, the engine's
        /// virtual-removal math counts DISPLAY positions). O(1) in natural order, a scan otherwise; −1 = not displayed.</summary>
        public static int DisplayRowOf(int originalIndex, ReadOnlySpan<int> view)
        {
            if ((uint)originalIndex < (uint)view.Length && view[originalIndex] == originalIndex) return originalIndex;
            for (int d = 0; d < view.Length; d++)
                if (view[d] == originalIndex) return d;
            return -1;
        }
    }

    // ══ 11. THE RIGHT-CLICK TARGET (Explorer rules) ══════════════════════════════════════════════════════════════════

    /// <summary>The ONE place right-click ↔ multi-selection semantics live, shared by every virtualized track list: a click
    /// INSIDE a ≥2 selection targets ALL selected tracks (display order) and keeps the selection; a click outside it
    /// re-anchors the selection to the clicked row FIRST and targets that row. The host thunk runs AFTER the selection
    /// settles, so a playlist host resolves rows for exactly the target set.</summary>
    public static class TargetResolver
    {
        /// <summary><paramref name="itemIndex"/> is in the SelectionModel's ITEM space; <paramref name="trackAt"/> maps an
        /// item to its track and returns <c>default</c> (an invalid handle) for a non-track row (hero, chrome, a
        /// recommendation), which gets no menu and is never counted.</summary>
        public static ActionTarget? Resolve(SelectionModel selection, Func<int, Track> trackAt, int itemIndex,
                                            Func<PlaylistHost>? host)
        {
            var clicked = trackAt(itemIndex);
            if (!clicked.IsValid) return null;

            bool partOfMulti = selection.IsSelected(itemIndex) && SelectedTrackCount(selection, trackAt) >= 2;
            if (!partOfMulti)
            {
                if (!(selection.IsSelected(itemIndex) && selection.SelectedCount == 1))
                {
                    selection.DeselectAll();
                    selection.Select(itemIndex);
                }
                return ActionTarget.ForTracks([clicked], host is null ? default : host());
            }

            var tracks = new List<Track>(selection.SelectedCount);
            for (int r = 0; r < selection.RangeCount; r++)
            {
                var (start, end) = selection.GetRange(r);
                for (int i = start; i <= end; i++)
                    if (trackAt(i) is { IsValid: true } t) tracks.Add(t);
            }
            return ActionTarget.ForTracks(tracks, host is null ? default : host());
        }

        static int SelectedTrackCount(SelectionModel selection, Func<int, Track> trackAt)
        {
            int n = 0;
            for (int r = 0; r < selection.RangeCount; r++)
            {
                var (start, end) = selection.GetRange(r);
                for (int i = start; i <= end && n < 2; i++)
                    if (trackAt(i).IsValid) n++;
                if (n >= 2) break;
            }
            return n;
        }
    }
}
