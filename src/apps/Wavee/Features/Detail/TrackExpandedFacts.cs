using System;
using System.Collections.Generic;
using System.Globalization;
using Wavee.Core;

namespace Wavee;

/// <summary>What an expanded track row STATES, as one ordered list of facts. Engine-free (System + Wavee.Core + the
/// generated <c>Strings</c> key consts) so <c>TrackExpandedFactsTests</c> pins every rule without mounting a page.
///
/// <para>WHY THIS EXISTS. The track table's lanes are a function of WIDTH: the tier ladder and the identity-first
/// relief ladder (<see cref="DetailTrackTableRules"/>) both YIELD lanes as the list narrows, cheapest fact first —
/// Plays, then BPM·key, then Added by, then Date added. That is right for a table and wrong for a reader who wants to
/// know one song's facts. The expanded row is the answer: it states EVERY fact the track carries, at every width, in
/// one fixed order, regardless of which lanes the table above it happens to be drawing.</para>
///
/// <para>Two honesty rules, both deliberate:</para>
/// <list type="bullet">
/// <item><description>A fact that is ABSENT does not render. There are no "—" placeholders for facts a track simply
/// does not have — an em dash reads as "this track has no album", which is a claim, not a gap.</description></item>
/// <item><description>The ONE exception is ENRICHMENT PENDING: BPM·key arrives per row on extension kind 222 and the
/// stream count on kind 185, so a surface that ASKS for those columns (the same <c>ShowTempo × TempoColumn</c> /
/// <c>ShowPlays | PlaysColumnOptIn × PlaysColumn</c> gating the table lanes use) has an honest "asked, not answered
/// yet" state. That renders as <see cref="Dash"/> with <see cref="TrackFactForm.Pending"/>, so the reader can tell
/// "we don't know yet" from "this song has none". A surface that never asks emits no row at all.</description></item>
/// </list>
///
/// <para>This file also OWNS the small formatters the row and its drawer share (<see cref="TrackTime"/>,
/// <see cref="Bpm"/>, <see cref="KeyLabel"/>): <c>DetailFormat</c>, <c>TrackRow</c> and <c>TrackVersionsPanel</c>
/// forward to them, so a row and its expanded facts can never spell the same number two ways.</para></summary>
internal static class TrackExpandedFacts
{
    /// <summary>The "asked, not answered yet" mark. Same glyph as <c>TrackRow.Dash</c> — one em dash, one meaning.</summary>
    internal const string Dash = "—";

    /// <summary>Build the ordered fact list for one track. Order is <see cref="TrackFactKind"/>'s DECLARATION ORDER,
    /// unconditionally: presence decides whether a fact appears, never where. That is what makes the strip scannable —
    /// the reader learns the shape once and every row after that is the same shape with holes.
    ///
    /// <para>The group order, and why: the three facts the relief ladder drops FIRST (Plays · BPM · Key) lead, because
    /// they are exactly what a narrow table stopped saying; then the row's own provenance and measure (Added ·
    /// Duration); then release identity (Album · Released); then membership (Added by); then the technical id (ISRC);
    /// then the descriptor chips; then the flags, which are marks rather than values and read last.</para></summary>
    internal static IReadOnlyList<TrackFact> For(Track? track, TrackFactsOptions options = default)
    {
        var facts = new List<TrackFact>(12);
        if (track is null || track.Uri.Length == 0) return facts;

        var culture = options.Culture ?? CultureInfo.CurrentCulture;
        var zone = options.Zone ?? TimeZoneInfo.Local;
        // The ONE shared "is this row out yet?" predicate (Wavee.Core). Deliberately NOT re-derived here with an
        // injected clock: the greyed row, the play gate and this strip must never disagree about which rows are
        // pending, and a second copy of the test is exactly how that drift starts.
        bool notYetOut = track.IsNotYetOut();

        // ── the lanes the relief ladder yields first ────────────────────────────────────────────────────────────────
        // A pending row reports 0 plays and 0 ms because nothing has happened to it yet, so it states neither: the
        // Released fact below answers the question such a row actually raises.
        if (!notYetOut)
        {
            if (track.PlayCount > 0)
                facts.Add(new TrackFact(TrackFactKind.Plays, TrackFactForm.Value,
                                        track.PlayCount.ToString("N0", culture)));
            else if (options.PlaysPending)
                facts.Add(new TrackFact(TrackFactKind.Plays, TrackFactForm.Pending, Dash));
        }

        if (track.TempoBpm is { } bpm && bpm > 0d)
            facts.Add(new TrackFact(TrackFactKind.Bpm, TrackFactForm.Value, Bpm(bpm)));
        else if (options.TempoPending)
            facts.Add(new TrackFact(TrackFactKind.Bpm, TrackFactForm.Pending, Dash));

        if (PrettyKey(track.CamelotCode, track.MusicalKey, options.MajorWord, options.MinorWord) is { Length: > 0 } key)
            facts.Add(new TrackFact(TrackFactKind.Key, TrackFactForm.Value, key));
        else if (options.TempoPending)
            facts.Add(new TrackFact(TrackFactKind.Key, TrackFactForm.Pending, Dash));

        // ── provenance + measure ────────────────────────────────────────────────────────────────────────────────────
        // The EXACT instant, not the table's "3 days ago" / "Sep 28": the relative label is right for a 88-DIP lane and
        // useless to someone who opened the row to find out precisely when. Full date + short time, in the reader's own
        // culture and zone — both INJECTED, so the test pins the format instead of the build agent's locale.
        if (track.AddedAt is { } addedAt && addedAt > DateTimeOffset.UnixEpoch)
            facts.Add(new TrackFact(TrackFactKind.Added, TrackFactForm.Value, ExactStamp(addedAt, culture, zone)));

        if (track.DurationMs > 0)
            facts.Add(new TrackFact(TrackFactKind.Duration, TrackFactForm.Value, TrackTime(track.DurationMs)));

        // ── release identity ────────────────────────────────────────────────────────────────────────────────────────
        // AlbumRef carries id/uri/name only — there is no release date on it — so Released comes from the TRACK's own
        // earliest-live instant (TrackV4.earliest_live_timestamp), which is the per-track fact this record actually has.
        if (track.Album is { Name.Length: > 0 } album)
            facts.Add(new TrackFact(TrackFactKind.Album, TrackFactForm.Link, album.Name, LinkUri: album.Uri));

        if (track.AvailableAt is { } live && live > DateTimeOffset.UnixEpoch)
            facts.Add(new TrackFact(TrackFactKind.Released, TrackFactForm.Value, ExactDate(live, culture, zone)));

        // ── membership ──────────────────────────────────────────────────────────────────────────────────────────────
        // The resolved display name when the page has the profile; the raw playlist membership id otherwise — a
        // collaborative playlist is the only surface that carries either, so absence here is the common case.
        if (options.AddedByName is { Length: > 0 } who)
            facts.Add(new TrackFact(TrackFactKind.AddedBy, TrackFactForm.Value, who));
        else if (track.AddedBy is { Length: > 0 } raw)
            facts.Add(new TrackFact(TrackFactKind.AddedBy, TrackFactForm.Value, raw));

        if (track.Isrc is { Length: > 0 } isrc)
            facts.Add(new TrackFact(TrackFactKind.Isrc, TrackFactForm.Value, isrc));

        // ── descriptors ─────────────────────────────────────────────────────────────────────────────────────────────
        // Kind-6 TRACK_DESCRIPTOR concepts in the server's own order (descending weight). Null = not fetched and empty
        // = "this track genuinely has none"; both render nothing, because a chip row with no chips is a lie either way.
        if (track.Tags is { Count: > 0 } tags)
            facts.Add(new TrackFact(TrackFactKind.Descriptors, TrackFactForm.Chips, "", Chips: tags));

        // ── flags ───────────────────────────────────────────────────────────────────────────────────────────────────
        // Marks, not values: the LABEL is the whole fact, so Value stays empty and the renderer draws a badge.
        if (track.IsExplicit) facts.Add(new TrackFact(TrackFactKind.Explicit, TrackFactForm.Flag, ""));
        // "Has a music video" is a property of the CATALOGUE ENTRY (the kind-99 association plane), never a field on
        // Track — so it arrives as an option the caller resolved through VideoPresence, exactly like the row's lane.
        if (options.HasVideo) facts.Add(new TrackFact(TrackFactKind.Video, TrackFactForm.Flag, ""));
        if (track.Origin == TrackOrigin.Local) facts.Add(new TrackFact(TrackFactKind.LocalFile, TrackFactForm.Flag, ""));
        // Only a row the server has actually ruled on: null Availability is "nobody told us", which is not a verdict.
        // A not-yet-out row is excluded — it already states WHEN through Released, and "Unavailable" beside a release
        // date reads as a contradiction rather than as two halves of one fact.
        if (!notYetOut && track.Availability is Availability.Unavailable)
            facts.Add(new TrackFact(TrackFactKind.Unavailable, TrackFactForm.Flag, ""));

        return facts;
    }

    // ── the hero partition ───────────────────────────────────────────────────────────────────────────────────────────
    // The strip draws the same ordered list two ways: four facts as big display-face numbers, the rest as prose. WHICH
    // four is a RULE, not a renderer preference, so it lives here beside For() where Wavee.Tests can pin it — the same
    // reason LabelKey does. A renderer that owned this list would drift the moment a new kind landed.

    /// <summary>Is this fact one the reader came to READ AS A NUMBER? Plays · BPM · Key · Duration and nothing else.
    ///
    /// <para>WHY THESE FOUR. Three of them (Plays · BPM · Key) are exactly what the table's relief ladder yields first
    /// as the list narrows — the strip exists to say them back — and Duration is the one measure every row carries, so
    /// it is the fact that is always there to anchor the group. Everything else is a SENTENCE, not a figure: a date, a
    /// name, an album title and an ISRC all read worse at display size than in a line of prose, and a flag has no value
    /// at all (its label IS the fact).</para>
    ///
    /// <para>Deliberately a switch over kinds rather than a test on <see cref="TrackFactForm"/>: form says how a fact
    /// RENDERS, this says how big it reads, and the two are independent — a Pending Plays is still a hero slot holding
    /// a dash, and a Link is prose even though it is a value.</para></summary>
    internal static bool IsHeroFact(TrackFactKind kind) => kind switch
    {
        TrackFactKind.Plays or TrackFactKind.Bpm or TrackFactKind.Key or TrackFactKind.Duration => true,
        _ => false,
    };

    /// <summary>Split one fact into the big part and the small part a hero slot draws under/beside it.
    ///
    /// <para>Only <see cref="TrackFactKind.Key"/> has two parts, and only when the track carries a Camelot slot: the
    /// wheel code is the FIGURE (it matches the swatch and the filter and is two glyphs wide, which is what a display
    /// face wants) and the spelled key is the gloss — "2B" over "F♯ major". With no wheel slot there is no figure to
    /// promote, so the spelled key IS the value and the unit is null; the strip then draws one line, not a line with an
    /// empty second row.</para>
    ///
    /// <para>Every other kind is one part. That INCLUDES a value with an obvious unit — BPM's label already says "BPM"
    /// and a "min" under a duration is noise — so this method invents nothing: <c>Unit</c> is a real second half or it
    /// is null.</para>
    ///
    /// <para>PENDING needs no branch here, and that is deliberate. <see cref="For"/> already writes <see cref="Dash"/>
    /// into <c>Value</c> for the two enrichment planes, so a pending fact splits like any other and yields
    /// <c>("—", null)</c> — the em dash stays ONE decision made in one place (For), and the strip never has to ask
    /// "is this pending?" to know what glyph to draw.</para>
    ///
    /// <para>The Key case inverts <see cref="PrettyKey"/> across the same <see cref="KeySeparator"/> that joined it,
    /// and <see cref="KeySplit"/> is the shared source of truth both directions answer to — <c>HeroSplit</c> of a
    /// <c>PrettyKey</c> string is pinned equal to <c>KeySplit</c> of the same inputs, so the two forms cannot
    /// drift.</para></summary>
    internal static TrackFactSplit HeroSplit(in TrackFact f)
    {
        if (f.Kind != TrackFactKind.Key) return new TrackFactSplit(f.Value, null);

        int cut = f.Value.IndexOf(KeySeparator, StringComparison.Ordinal);
        return cut < 0
            ? new TrackFactSplit(f.Value, null)
            : new TrackFactSplit(f.Value.Substring(0, cut), f.Value.Substring(cut + KeySeparator.Length));
    }

    /// <summary>The loc KEY for a fact's label. Kept here, beside the ordering, so the renderer owns no copy of the
    /// vocabulary and a new kind cannot ship label-less.</summary>
    internal static string LabelKey(TrackFactKind kind) => kind switch
    {
        TrackFactKind.Plays => Strings.Detail.TrackFacts.Plays,
        TrackFactKind.Bpm => Strings.Detail.TrackFacts.Bpm,
        TrackFactKind.Key => Strings.Detail.TrackFacts.Key,
        TrackFactKind.Added => Strings.Detail.TrackFacts.Added,
        TrackFactKind.Duration => Strings.Detail.TrackFacts.Duration,
        TrackFactKind.Album => Strings.Detail.TrackFacts.Album,
        TrackFactKind.Released => Strings.Detail.TrackFacts.Released,
        TrackFactKind.AddedBy => Strings.Detail.TrackFacts.AddedBy,
        TrackFactKind.Isrc => Strings.Detail.TrackFacts.Isrc,
        TrackFactKind.Descriptors => Strings.Detail.TrackFacts.Descriptors,
        TrackFactKind.Explicit => Strings.Detail.TrackFacts.Explicit,
        TrackFactKind.Video => Strings.Detail.TrackFacts.Video,
        TrackFactKind.LocalFile => Strings.Detail.TrackFacts.LocalFile,
        _ => Strings.Detail.TrackFacts.Unavailable,
    };

    // ── the shared formatters ────────────────────────────────────────────────────────────────────────────────────────
    // These live HERE, not in DetailFormat/TrackRow/TrackVersionsPanel, because this file is the one that Wavee.Tests
    // source-includes. Those three forward to them, so the row lane, the drawer's version rows and the facts strip are
    // one implementation and can never spell the same number two ways.

    /// <summary>Per-track duration "m:ss" (or "h:mm:ss" once it crosses an hour — a podcast episode in a playlist).
    /// This is a CLOCK formatter: 0 ms spells "0:00". The duration <em>cell</em> must not use it for an unknown
    /// length — that is <see cref="DurationCell"/>.</summary>
    internal static string TrackTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>The duration CELL, not the clock. 0 ms is "not known yet", never a zero-second track — the same
    /// 0-is-unknown rule Plays already uses. A thin album disc row that still has no length must dash, not claim
    /// <c>0:00</c>.</summary>
    internal static string DurationCell(long ms) => ms > 0 ? TrackTime(ms) : Dash;

    /// <summary>Tempo readout — "101" for a whole BPM, "101.5" when the fraction is meaningful. Spotify reports tempo
    /// as a double (101.0099…) and full precision in a narrow lane is noise; one decimal is the most a listener can act
    /// on. Invariant culture: this is a technical figure, not a localised quantity, and a comma decimal separator next
    /// to the key label reads as a list.</summary>
    internal static string Bpm(double bpm)
    {
        double rounded = Math.Round(bpm, 1, MidpointRounding.AwayFromZero);
        return Math.Abs(rounded - Math.Round(rounded)) < 0.05
            ? ((int)Math.Round(rounded)).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>ONE key notation for a narrow lane: the Camelot slot when present (it matches the colour swatch and the
    /// filter), else the server's own key name. Never both — dual tokens bloated the Tempo lane.</summary>
    internal static string? KeyLabel(string? camelotCode, string? musicalKey) =>
        camelotCode is { Length: > 0 } c ? c
        : musicalKey is { Length: > 0 } k ? k
        : null;

    /// <summary>Major or minor, read off the Camelot slot's own suffix: the wheel encodes mode in the letter — <c>B</c>
    /// is the major ring, <c>A</c> the minor one. That is the ONLY mode signal this record carries
    /// (<see cref="Track.MusicalKey"/> is the bare tonic, "A"/"F"/"B"), so a track with no Camelot code has no honest
    /// mode and gets none.</summary>
    internal static KeyMode ModeOf(string? camelotCode)
    {
        if (camelotCode is not { Length: > 1 } c) return KeyMode.Unknown;
        char suffix = char.ToUpperInvariant(c[c.Length - 1]);
        return suffix == 'B' ? KeyMode.Major : suffix == 'A' ? KeyMode.Minor : KeyMode.Unknown;
    }

    /// <summary>The expanded row's key line: the Camelot slot AND the spelled key, because the strip has the room the
    /// lane never had — "8B · C major". Falls back through every partial state rather than inventing one:
    /// <list type="bullet">
    /// <item><description>Camelot + tonic + a mode word ⇒ "8B · C major"</description></item>
    /// <item><description>Camelot + tonic, no mode words supplied ⇒ "8B · C"</description></item>
    /// <item><description>Camelot only ⇒ "8B"</description></item>
    /// <item><description>tonic only (kind 222 gave a name but no wheel slot) ⇒ "C"</description></item>
    /// <item><description>neither ⇒ null, and the caller emits nothing</description></item>
    /// </list>
    /// The mode words are INJECTED (<paramref name="major"/>/<paramref name="minor"/>) so this file stays free of the
    /// localization runtime — the same contract <c>PlayableLinks</c> uses.
    ///
    /// <para>This is the JOIN of <see cref="KeySplit"/> and nothing more. The two halves are decided once, there, so
    /// the prose form ("8B · C major") and the hero form (<see cref="HeroSplit"/>'s "8B" over "C major") are the same
    /// two strings arranged two ways rather than two formatters that agree by luck.</para></summary>
    internal static string? PrettyKey(string? camelotCode, string? musicalKey, string? major = null, string? minor = null)
        => KeySplit(camelotCode, musicalKey, major, minor) is { } s
            ? s.Unit is null ? s.Value : s.Value + KeySeparator + s.Unit
            : null;

    /// <summary>The token that joins the wheel slot to the spelled key. A const, not a literal, because
    /// <see cref="HeroSplit"/> cuts on exactly this — one glyph, one meaning, one place to change it.</summary>
    internal const string KeySeparator = " · ";

    /// <summary>The key's two halves, decided ONCE: the wheel slot (the figure) and the spelled key (the gloss).
    /// <c>null</c> when the track carries neither, which is what makes <see cref="For"/>'s "emit nothing" case fall
    /// out rather than be tested for.
    ///
    /// <para>Every partial state degrades instead of inventing the missing half — Camelot only ⇒ ("8B", null); tonic
    /// only ⇒ ("C", null), because with no wheel slot there is no figure to promote and no honest mode word either
    /// (<see cref="ModeOf"/>); Camelot + tonic ⇒ ("8B", "C major"), or ("8B", "C") when no mode words were
    /// injected.</para></summary>
    internal static TrackFactSplit? KeySplit(
        string? camelotCode, string? musicalKey, string? major = null, string? minor = null)
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
        // No wheel slot ⇒ the spelled key is the whole fact and rides in Value; the hero slot then draws one line.
        return camelot is null ? new TrackFactSplit(spelled!, null) : new TrackFactSplit(camelot, spelled);
    }

    /// <summary>The exact "when": full date plus the time of day, in the reader's culture and zone. <c>"f"</c> is the
    /// framework's long-date/short-time pattern, which is what every OS date picker means by "the full date".</summary>
    internal static string ExactStamp(DateTimeOffset when, CultureInfo culture, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(when, zone).DateTime.ToString("f", culture);

    /// <summary>A full date with no time — a release instant is a DAY, and a minute-precise release reads as false
    /// precision beside "Added".</summary>
    internal static string ExactDate(DateTimeOffset when, CultureInfo culture, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(when, zone).DateTime.ToString("D", culture);
}

/// <summary>What one expanded-row fact IS. Declaration order is PRESENTATION order — see
/// <see cref="TrackExpandedFacts.For"/>. Values are never persisted, so this may be reordered when the reading order
/// should change; it is a rendering contract, not a wire one.</summary>
public enum TrackFactKind : byte
{
    Plays, Bpm, Key, Added, Duration, Album, Released, AddedBy, Isrc, Descriptors,
    Explicit, Video, LocalFile, Unavailable,
}

/// <summary>How a fact renders. <see cref="Pending"/> is the ONE state that draws an em dash — "asked, not answered
/// yet" — and it exists only for the two enrichment planes (kind 222 tempo/key, kind 185 play counts).</summary>
public enum TrackFactForm : byte { Value, Link, Chips, Flag, Pending }

/// <summary>Camelot's two rings. <see cref="Unknown"/> when the track carries no wheel slot at all — the bare
/// <c>MusicalKey</c> tonic says nothing about mode.</summary>
public enum KeyMode : byte { Unknown, Major, Minor }

/// <summary>One fact. <paramref name="Value"/> is the formatted display string (empty for a flag, whose label IS the
/// fact); <paramref name="LinkUri"/> is set only on <see cref="TrackFactForm.Link"/>; <paramref name="Chips"/> only on
/// <see cref="TrackFactForm.Chips"/>.</summary>
internal readonly record struct TrackFact(
    TrackFactKind Kind, TrackFactForm Form, string Value,
    string? LinkUri = null, IReadOnlyList<string>? Chips = null);

/// <summary>One hero fact's two halves — see <see cref="TrackExpandedFacts.HeroSplit"/>. <paramref name="Value"/> is
/// the FIGURE the display face draws and is never null or empty for a hero fact (a pending one carries
/// <see cref="TrackExpandedFacts.Dash"/>); <paramref name="Unit"/> is the small gloss beneath it and is null far more
/// often than not — today only a Camelot-coded key has a real second half, and a unit is never INVENTED to fill the
/// slot ("min" under a duration, "BPM" under a tempo whose label already says BPM).</summary>
internal readonly record struct TrackFactSplit(string Value, string? Unit);

/// <summary>Everything the fact list needs that a <see cref="Track"/> does not carry.
/// <para><paramref name="TempoPending"/> / <paramref name="PlaysPending"/>: "this surface ASKED for the column".
/// Caller-derived from the same gating the lanes use — <c>Config.ShowTempo &amp;&amp; TempoColumn</c> and
/// <c>Config.ShowPlays || (Config.PlaysColumnOptIn &amp;&amp; PlaysColumn)</c> — so the strip's honest "not enriched
/// yet" dash appears exactly where the table would have reserved a lane for the same fact.</para>
/// <para><paramref name="Culture"/> / <paramref name="Zone"/> are nullable purely so the app call site can omit them;
/// tests ALWAYS inject both, which is what makes the exact-date format pinnable at all.</para></summary>
internal readonly record struct TrackFactsOptions(
    bool TempoPending = false,
    bool PlaysPending = false,
    bool HasVideo = false,
    string? AddedByName = null,
    CultureInfo? Culture = null,
    TimeZoneInfo? Zone = null,
    string? MajorWord = null,
    string? MinorWord = null);
