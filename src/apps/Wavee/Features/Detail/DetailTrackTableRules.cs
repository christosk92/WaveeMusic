namespace Wavee;

/// <summary>What the list is sorted by. <see cref="Index"/> = the original context order. Values are persisted, so new
/// columns append and existing values never move.</summary>
public enum SortColumn { Index, Title, Album, Duration, Artist, DateAdded, Plays }

/// <summary>The track-list sort state persisted per context.</summary>
public readonly record struct DetailTrackSort(SortColumn Column, bool Descending)
{
    public static readonly DetailTrackSort Default = new(SortColumn.Index, false);
}

/// <summary>The identity lanes selected by the app-wide row style. Kept engine-free so the responsive grammar is
/// unit-tested without mounting the detail page.</summary>
internal readonly record struct TrackIdentityColumns(bool Thumb, bool Artist, bool ArtistInTitle);

/// <summary>The trailing lanes selected by a row style. Kept beside the identity decision so the header, rows and
/// tests all agree that Classic folds media facts into Title and exposes commands through one overflow lane.</summary>
internal readonly record struct TrackTrailingColumns(bool Video, bool Actions, bool Expand);

/// <summary>Pure column and sort rules shared by the detail-table renderer and its tests.</summary>
internal static class DetailTrackTableRules
{
    // The Classic Artist lane survives one tier longer than Album and folds below 440 DIP (tier 4).
    internal const int ClassicArtistFoldTier = 4;
    internal const int ClassicInlineVideoDropTier = 4;
    internal const float ClassicHeaderHeight = 32f;

    internal static TrackIdentityColumns IdentityColumns(
        bool classic, bool showArtThumb, bool artworkHidden, bool showTrackArtist, int tier)
    {
        bool artist = classic && showTrackArtist && tier < ClassicArtistFoldTier;
        return new TrackIdentityColumns(
            Thumb: !classic && showArtThumb && !artworkHidden && tier < 5,
            Artist: artist,
            ArtistInTitle: showTrackArtist && !artist);
    }

    /// <summary>Classic keeps density independent, but maps it onto the tighter table ladder shown by the legacy
    /// desktop client. Modern retains the established 40/48/56/64 ladder owned by <c>TrackRow</c>.</summary>
    internal static float RowHeightFor(int density, bool classic) => classic
        ? density switch { 0 => 36f, 2 => 44f, 3 => 48f, _ => 40f }
        : density switch { 0 => 40f, 2 => 56f, 3 => 64f, _ => 48f };

    internal static float HeaderHeightFor(bool classic) => classic ? ClassicHeaderHeight : 36f;

    /// <summary>Classic folds VIDEO into the Title line (an inline film glyph) and keeps ONE trailing command lane, so
    /// it has no dedicated media lane. It DOES get the disclosure chevron: the expanded row states every fact the
    /// track carries — precisely the facts the relief ladder takes away from a narrow table — and that is a row
    /// affordance, not a skin flourish. One implementation, two skins; the lane follows the same width gate as the
    /// "…" lane in both, because a drawer needs room to breathe.</summary>
    internal static TrackTrailingColumns TrailingColumns(
        bool classic, bool hasVideo, bool showVersions, int tier)
    {
        bool hasTrailingRoom = tier < 6;
        bool expand = showVersions && hasTrailingRoom;
        if (classic) return new TrackTrailingColumns(false, hasTrailingRoom, expand);
        bool video = hasVideo && hasTrailingRoom;
        return new TrackTrailingColumns(video, hasTrailingRoom && !video, expand);
    }

    internal static bool ShowClassicInlineVideo(bool classic, bool hasVideo, int tier) =>
        classic && hasVideo && tier < ClassicInlineVideoDropTier;

    /// <summary>Does the row's context menu need to carry a "Track details" verb? Exactly when the row wants a drawer
    /// but the table has no room for the chevron lane — the ultra-compact tier, in EITHER skin. It is a FALLBACK for a
    /// missing affordance, never a duplicate of a visible one: offering the verb beside a chevron that already opens
    /// the same drawer is two controls for one action.
    ///
    /// <para>Replaces the old Classic-only rule. Classic now owns the chevron like Modern does, so "which skin is
    /// this" stopped being the question; "is the affordance on screen" is.</para></summary>
    internal static bool ShowVersionsMenuItem(bool showVersions, bool expandLane, bool singleTrack) =>
        showVersions && singleTrack && !expandLane;

    /// <summary>A Title header owns Artist only while Artist is folded into its metadata subline.</summary>
    internal static bool HeaderActive(SortColumn header, SortColumn active, bool artistColumn) =>
        header == active || (!artistColumn && header == SortColumn.Title && active == SortColumn.Artist);

    /// <summary>Header-click sort cycle. A dedicated Artist lane gets its own ordinary three-state cycle; without that
    /// lane the Title header retains the existing Title/Artist five-state cycle.</summary>
    internal static DetailTrackSort NextSort(DetailTrackSort cur, SortColumn clicked, bool artistColumn)
    {
        if (clicked == SortColumn.Index)
            return cur.Column == SortColumn.Index ? new DetailTrackSort(SortColumn.Index, !cur.Descending) : DetailTrackSort.Default;

        if (clicked == SortColumn.Title && !artistColumn)
        {
            if (cur.Column == SortColumn.Title)
                return cur.Descending ? new DetailTrackSort(SortColumn.Artist, false) : new DetailTrackSort(SortColumn.Title, true);
            if (cur.Column == SortColumn.Artist)
                return cur.Descending ? DetailTrackSort.Default : new DetailTrackSort(SortColumn.Artist, true);
            return new DetailTrackSort(SortColumn.Title, false);
        }

        if (cur.Column == clicked) return cur.Descending ? DetailTrackSort.Default : new DetailTrackSort(clicked, true);
        return new DetailTrackSort(clicked, false);
    }

    // ── identity-first width relief (the RELIEF LADDER) ──────────────────────────────────────────────────────────────
    // WHY THIS EXISTS. Lane presence used to be decided by the width TIER alone (DetailLayoutBreakpoints), which keys on
    // the pane's TOTAL width and can therefore never see what the identity lanes have LEFT after the fixed lanes have
    // taken theirs. The fixed lanes are absolute-priority tracks and Title/Artist are STAR tracks floored at 0, so all
    // of the pressure landed on exactly the two lanes that carry the row's identity: at ~650 DIP a Classic Liked table
    // still kept Date added + Plays + BPM·Key + duration + ♥ + the "…" lane (all admitted at tier 2) and paid for them
    // with a 65-DIP Title and a 49-DIP Artist — one glyph and an ellipsis each, beside a Plays lane twice as wide as
    // any number it can ever hold.
    //
    // The ladder inverts that priority. It measures what the CURRENT lane set actually needs — every fixed lane, every
    // inter-column gap, the grid's own inset, and the identity floors (TrackLane.TitleFloor / ArtistFloor / AlbumFloor)
    // — and yields trailing lanes, cheapest fact first, until the identity lanes clear their floor. It never widens a
    // lane and never touches #, Title, duration or the trailing film/"…" lane: a row must always keep its transport,
    // its title, its length and its verbs.
    //
    // It composes with the tier rather than replacing it: the tier still decides padding, gap and the Classic artist
    // fold, and relief only ever REMOVES a lane the tier already admitted. Both ladders are monotone in width and both
    // release a lane only after the width clears its threshold by the hysteresis band, so a slow drag never flickers.

    /// <summary>The lanes a track table is drawing, as pure geometry — the relief ladder's whole input alongside the
    /// grid's gap and inset. Deliberately NOT the renderer's <c>ColumnSet</c>: this file is engine-free and source-
    /// included by Wavee.Tests, and the ladder needs nothing from a ColumnSet but which lanes are up.</summary>
    internal readonly record struct TrackTableLanes(
        bool Heart, bool Thumb, bool Artist, bool Album, bool By, bool Date, bool Plays, bool Tempo,
        bool Video, bool Actions, bool Expand);

    /// <summary>The number of relief steps. Step 0 is "no relief"; every step from 1 to here drops exactly one more
    /// lane, in the order documented on <see cref="Relieve"/>.</summary>
    internal const int MaxRelief = 8;

    /// <summary>How much extra width a lane must re-earn before it comes back. Same band, and the same safe asymmetry,
    /// as the tier ladder: yield IMMEDIATELY (the cost of guessing wrong while narrowing is an unreadable table), take
    /// one back only with margin.</summary>
    internal const float ReliefHysteresisDip = Features.Detail.DetailLayoutBreakpoints.TierHysteresisDip;

    /// <summary>The lanes that survive <paramref name="step"/> relief steps. The YIELD ORDER, cheapest fact first:
    /// Plays (1) → BPM·key (2) → Added by (3) → Date added (4) → Album (5) → Artist (6) → art thumb (7) → ♥ (8).
    /// <para>Plays is a stream count — the weakest fact in the row and the widest slack in the table. BPM·key is
    /// enrichment, not identity (the same reason it is already the first tier casualty). Added by is 132 DIP, the
    /// single most expensive optional lane, for a fact the row's own menu also states. Date added is provenance, so it
    /// outranks all three. Album is the weaker identity fact and survives in the Title metadata subline; Classic's
    /// Artist lane folds INTO the Title line rather than disappearing. The left cluster (art, ♥) goes last: 32 and 28
    /// DIP, and one of the two is an affordance rather than a fact.</para>
    /// <para><c>#</c>, Title, duration and the trailing film/"…"/chevron lanes NEVER yield.</para></summary>
    internal static TrackTableLanes Relieve(in TrackTableLanes l, int step) => step <= 0 ? l : new TrackTableLanes(
        Heart: l.Heart && step < 8,
        Thumb: l.Thumb && step < 7,
        Artist: l.Artist && step < 6,
        Album: l.Album && step < 5,
        By: l.By && step < 3,
        Date: l.Date && step < 4,
        Plays: l.Plays && step < 1,
        Tempo: l.Tempo && step < 2,
        Video: l.Video, Actions: l.Actions, Expand: l.Expand);

    /// <summary>The narrowest table width at which <paramref name="l"/> still clears the identity floors: every fixed
    /// lane at its <see cref="TrackLane"/> width, the identity lanes at their FLOORS (not at zero, which is what the
    /// grid's overflow guard would otherwise hand them), one <paramref name="colGap"/> between each pair, and the
    /// grid's own <paramref name="padX"/> on both sides. This is the one function the ladder inverts.
    ///
    /// Title and duration are unconditional, so the seed already carries both plus the Title floor.</summary>
    internal static float MinWidthFor(in TrackTableLanes l, float colGap, float padX)
    {
        float w = TrackLane.Num + TrackLane.TitleFloor + TrackLane.Duration;
        int cols = 3;
        if (l.Heart) { w += TrackLane.Heart; cols++; }
        if (l.Thumb) { w += TrackLane.Thumb; cols++; }
        if (l.Artist) { w += TrackLane.ArtistFloor; cols++; }
        if (l.Album) { w += TrackLane.AlbumFloor; cols++; }
        if (l.By) { w += TrackLane.By; cols++; }
        if (l.Date) { w += TrackLane.Date; cols++; }
        if (l.Plays) { w += TrackLane.Plays; cols++; }
        if (l.Tempo) { w += TrackLane.Tempo; cols++; }
        if (l.Video) { w += TrackLane.Video; cols++; }
        if (l.Actions) { w += TrackLane.Actions; cols++; }
        if (l.Expand) { w += TrackLane.Expand; cols++; }
        return w + (cols - 1) * colGap + padX * 2f;
    }

    /// <summary>The FEWEST relief steps that let <paramref name="l"/> fit <paramref name="available"/>. 0 when the table
    /// already clears the identity floors — so a wide layout is untouched by construction, not by a special case — and
    /// <see cref="MaxRelief"/> when even the last step cannot, at which point the star tracks collapse exactly as they
    /// do today (a table narrower than # + a floor + duration has no honest answer left).</summary>
    internal static int NominalReliefFor(in TrackTableLanes l, float available, float colGap, float padX)
    {
        if (available <= 0f) return 0;
        for (int step = 0; step < MaxRelief; step++)
            if (MinWidthFor(Relieve(in l, step), colGap, padX) <= available) return step;
        return MaxRelief;
    }

    /// <summary>The nominal step with resize hysteresis: yield a lane the moment the floor is breached, re-admit one
    /// only once the width clears its threshold by <see cref="ReliefHysteresisDip"/>.
    ///
    /// <paramref name="initialized"/> false ⇒ nothing has been measured yet, so <paramref name="prev"/> is a
    /// construction default rather than a step the user has seen: take the nominal outright. Mirrors
    /// <see cref="Features.Detail.DetailLayoutBreakpoints.TierFor"/>, deliberately — one hysteresis grammar for both
    /// ladders, so they can never disagree about which direction is the safe guess.</summary>
    internal static int ReliefFor(in TrackTableLanes l, float available, float colGap, float padX,
                                 int prev, bool initialized = true)
    {
        if (available <= 0f) return prev;
        int nominal = NominalReliefFor(in l, available, colGap, padX);
        if (!initialized || nominal >= prev) return nominal;
        int eased = NominalReliefFor(in l, available - ReliefHysteresisDip, colGap, padX);
        return eased < prev ? eased : prev;
    }
}

/// <summary>The track table's LANE WIDTH TABLE — one place, read by the width tracks the grid is built from
/// (<c>DetailTracks.TracksFor</c>), by the cells that fill them (<see cref="TrackRow"/>) and by the relief ladder in
/// <see cref="DetailTrackTableRules"/> that decides which of them yield. They must agree by construction: a metric the
/// ladder computes from one number while the grid is built from another silently mis-predicts the squeeze.
///
/// Every fixed width is its widest REALISTIC content plus optical margin, measured at the CLASSIC row's 14/20 caption
/// (the wider of the two type ramps) or at its 11px uppercase header, whichever is larger — not at a worst case nobody
/// renders. The header is frequently the binding constraint, not the value: "Date added" is wider than "Sep 28, 2024".</summary>
internal static class TrackLane
{
    /// <summary>The # ↔ play/pause lane. ONE number with <see cref="Heart"/>: the two sit side by side at the head of
    /// every row, and at 36 against the heart's 28 the transport lane read as a gutter with a number lost in it —
    /// visibly wider than the affordance beside it for content (three digits, or a 24-DIP play glyph) that never
    /// needed the room. 28 holds the widest realistic row number at the row's Caption 12/16, the 24-DIP transport
    /// button with a DIP of air on each side, and the header's own "#" between its two <see cref="NumCaretSlot"/>
    /// caret slots.</summary>
    internal const float Num = 28f;
    /// <summary>The header's symmetric caret reservation inside <see cref="Num"/>: the same slot on BOTH sides, with
    /// the descending caret drawn only in the trailing one, so turning the sort indicator on never nudges # away from
    /// the row numbers underneath it. Sized to the 9-DIP caret glyph; the label keeps what is left in the middle.</summary>
    internal const float NumCaretSlot = 9f;
    /// <summary>The ♥ lane — exactly the 28-DIP like hit target (see <see cref="TrackRow.HeartCol"/>).</summary>
    internal const float Heart = 28f;
    /// <summary>The art lane. Mirrors <see cref="TrackRow.ThumbSize"/> (WaveeSize.Thumb32); the two are one number.</summary>
    internal const float Thumb = 32f;
    /// <summary>Added by: a 24-DIP PersonPicture + an 8-DIP gap + ~100 DIP of display name.</summary>
    internal const float By = 132f;
    /// <summary>Date added. Sized to the "Date added" HEADER (~72 at Classic's tracked 11px uppercase), which is wider
    /// than the widest value the cell formats ("Sep 28, 2024" ≈ 86 at 14px is the one exception, and it is the reason
    /// this is 88 rather than 76).</summary>
    internal const float Date = 88f;
    /// <summary>Plays. "1.85B" / "999.9M" is ~42 DIP at Classic 14px and the "PLAYS" header ~37 — the lane was 84,
    /// i.e. twice what it can ever hold, and every one of those spare DIPs came straight out of the Title star. This is
    /// the single biggest slack in the table and the visible "huge gap between Date added and Plays".</summary>
    internal const float Plays = 52f;
    /// <summary>BPM · key: a 6-DIP Camelot swatch + "171.1" + "·" + one key token, with the 4-DIP gaps between them.</summary>
    internal const float Tempo = 80f;
    /// <summary>Duration — "1:59:59" (a podcast episode in a playlist), not just "59:59".</summary>
    internal const float Duration = 52f;
    /// <summary>The trailing film / hover-"…" lane.</summary>
    internal const float Video = 28f;
    /// <summary>The trailing "…" overflow lane when Video is off (a 28-DIP button + breathing room).</summary>
    internal const float Actions = 40f;
    /// <summary>The expand chevron — matches the <c>ExpandChevron</c> hit target.</summary>
    internal const float Expand = 26f;

    // ── the identity (STAR) lanes ────────────────────────────────────────────────────────────────────────────────────
    // Weights, and — the part that did not exist before — a readable FLOOR for each. The floors are deliberately
    // ratio-consistent with the weights (120 : 90 : 90 == 1 : 0.75 : 0.75), so a star pool exactly equal to their sum
    // hands every identity lane exactly its floor and no lane is starved to buy another one room.
    internal const float TitleStar = 1f;
    internal const float ArtistStar = 0.75f;
    internal const float AlbumStar = 0.75f;
    /// <summary>The narrowest Title that still reads as a song title rather than as an ellipsis: ~14 characters of the
    /// row's BodyStrong 14/20. Below this the lane states nothing at all.</summary>
    internal const float TitleFloor = 120f;
    /// <summary>The Classic Artist lane's floor — ~11 characters of Caption 14/20, enough for one billed name.</summary>
    internal const float ArtistFloor = 90f;
    /// <summary>The Album lane's floor. Equal to Artist's: it is the same kind of fact at the same weight.</summary>
    internal const float AlbumFloor = 90f;
}
