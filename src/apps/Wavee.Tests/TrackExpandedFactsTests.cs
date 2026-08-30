using System;
using System.Collections.Generic;
using System.Globalization;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The expanded track row's fact list (<c>Features/Detail/TrackExpandedFacts.cs</c>, source-included because
/// it is engine-free). This is the rule that makes an expanded row COMPLETE at every list width: the table above it
/// yields Plays, then BPM·key, then Added by, then Date added as it narrows, so the drawer must not ask the column set
/// what to say.
///
/// <para>Three standing rules are pinned here, because all three are the kind that rot silently:</para>
/// <list type="bullet">
/// <item><description>ORDER is the enum's declaration order, unconditionally. Presence decides whether a fact appears,
/// never where — a strip whose facts shuffle as data lands is unreadable.</description></item>
/// <item><description>An ABSENT fact renders nothing. The single exception is enrichment-pending (kind 222 tempo/key,
/// kind 185 plays), which is a real state and gets a real dash.</description></item>
/// <item><description>Culture, time zone and the two key-mode words are INJECTED. Nothing here reads
/// <c>CultureInfo.CurrentCulture</c>, <c>TimeZoneInfo.Local</c> or the localization runtime, which is the only reason
/// the exact-stamp format is pinnable on a build agent in any locale.</description></item>
/// </list></summary>
public class TrackExpandedFactsTests
{
    static readonly DateTimeOffset Added = new(2024, 9, 28, 15, 41, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Live = new(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A fixed +05:00 zone. Built by hand rather than looked up by id: the tz database is not the thing under
    /// test, and a machine without one would fail this for the wrong reason.</summary>
    static readonly TimeZoneInfo PlusFive =
        TimeZoneInfo.CreateCustomTimeZone("t+5", TimeSpan.FromHours(5), "t+5", "t+5");

    /// <summary>A culture whose date patterns are SET rather than inherited from ICU. The literal we pin is then a
    /// property of the code, not of whichever ICU version the agent happens to ship.</summary>
    static CultureInfo Patterned(string longDate, string shortTime)
    {
        var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        c.DateTimeFormat.LongDatePattern = longDate;
        c.DateTimeFormat.ShortTimePattern = shortTime;
        return c;
    }

    static Track T(
        long durationMs = 180_000, bool isExplicit = false, DateTimeOffset? addedAt = null, string? addedBy = null,
        long playCount = 0, TrackOrigin origin = TrackOrigin.Streamed, Availability? availability = null,
        DateTimeOffset? availableAt = null, string? isrc = null, double? bpm = null, string? musicalKey = null,
        string? camelot = null, IReadOnlyList<string>? tags = null, string albumName = "", string albumUri = "")
        => new("t1", "spotify:track:t1", "Song",
            Array.Empty<ArtistRef>(), new AlbumRef("a1", albumUri, albumName),
            durationMs, isExplicit, null,
            AddedAt: addedAt, AddedBy: addedBy, PlayCount: playCount, Origin: origin,
            Availability: availability, AvailableAt: availableAt, Isrc: isrc,
            TempoBpm: bpm, MusicalKey: musicalKey, CamelotCode: camelot, Tags: tags);

    static TrackFactKind[] Kinds(IReadOnlyList<TrackFact> facts)
    {
        var k = new TrackFactKind[facts.Count];
        for (int i = 0; i < facts.Count; i++) k[i] = facts[i].Kind;
        return k;
    }

    static TrackFact Pick(IReadOnlyList<TrackFact> facts, TrackFactKind kind)
    {
        for (int i = 0; i < facts.Count; i++) if (facts[i].Kind == kind) return facts[i];
        Assert.Fail($"no {kind} fact was emitted");
        return default;
    }

    static bool Has(IReadOnlyList<TrackFact> facts, TrackFactKind kind)
    {
        for (int i = 0; i < facts.Count; i++) if (facts[i].Kind == kind) return true;
        return false;
    }

    // ── ordering ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every fact at once, in the one order the strip ever draws. The track is deliberately implausible (a
    /// local file with a stream count and an ISRC) — this pins ORDER, not plausibility, and a rule that only holds for
    /// realistic rows is a rule that breaks on the first odd one.</summary>
    [Fact]
    public void For_EmitsEveryPresentFactInDeclarationOrder()
    {
        var facts = TrackExpandedFacts.For(
            T(playCount: 1_847_392, bpm: 128d, camelot: "8B", musicalKey: "C", addedAt: Added,
              durationMs: 214_000, albumName: "Rumours", albumUri: "spotify:album:a1",
              availableAt: Live, availability: Availability.Unavailable, addedBy: "raw-id",
              isrc: "USRC17607839", tags: new[] { "Rock", "Mellow" }, isExplicit: true,
              origin: TrackOrigin.Local),
            new TrackFactsOptions(HasVideo: true, Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc));

        Assert.Equal(new[]
        {
            TrackFactKind.Plays, TrackFactKind.Bpm, TrackFactKind.Key, TrackFactKind.Added, TrackFactKind.Duration,
            TrackFactKind.Album, TrackFactKind.Released, TrackFactKind.AddedBy, TrackFactKind.Isrc,
            TrackFactKind.Descriptors, TrackFactKind.Explicit, TrackFactKind.Video, TrackFactKind.LocalFile,
            TrackFactKind.Unavailable,
        }, Kinds(facts));
    }

    /// <summary>A bare row says one thing and invents nothing. No "—" for an album it has no name for, no "0" plays,
    /// no key: an em dash beside a label is a CLAIM ("this track has none"), and the strip is not allowed to make one.</summary>
    [Fact]
    public void For_OmitsEveryAbsentFactRatherThanDashingIt()
    {
        var facts = TrackExpandedFacts.For(T(), new TrackFactsOptions(
            Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc));

        Assert.Equal(new[] { TrackFactKind.Duration }, Kinds(facts));
    }

    /// <summary>Order does not depend on WHICH facts are present: the emitted kinds are always ascending. That is what
    /// lets a reader learn the shape once and read every later row as the same shape with holes.</summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void For_IsAscendingInKindWhateverIsMissing(bool withAdded, bool withAlbum, bool withIsrc)
    {
        var facts = TrackExpandedFacts.For(
            T(addedAt: withAdded ? Added : null,
              albumName: withAlbum ? "Rumours" : "",
              isrc: withIsrc ? "USRC17607839" : null),
            new TrackFactsOptions(Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc));

        for (int i = 1; i < facts.Count; i++)
            Assert.True(facts[i - 1].Kind < facts[i].Kind, $"{facts[i - 1].Kind} must precede {facts[i].Kind}");
    }

    /// <summary>An unreleased row states WHEN, and nothing it cannot know. It reports 0 plays and 0 ms because nothing
    /// has happened to it yet; "0 plays" would read as a real, dismal track. "Unavailable" is withheld too — beside a
    /// release date it reads as a contradiction rather than as two halves of one fact.</summary>
    [Fact]
    public void For_PendingRelease_StatesTheDateAndNotTheEmptyNumbers()
    {
        var facts = TrackExpandedFacts.For(
            T(durationMs: 0, playCount: 0, availability: Availability.Unavailable,
              availableAt: DateTimeOffset.UtcNow.AddYears(5)),
            new TrackFactsOptions(PlaysPending: true, Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc));

        Assert.Equal(new[] { TrackFactKind.Released }, Kinds(facts));
    }

    // ── enrichment gating ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ONE honest dash. A surface that ASKS for the BPM·key lane and has not been answered yet says
    /// "asked, not answered"; a surface that never asks says nothing at all; a row that HAS the value says the value.
    /// Same three-way rule for both enrichment planes.</summary>
    [Theory]
    [InlineData(true, true, TrackFactForm.Value)]     // asked, answered
    [InlineData(false, true, TrackFactForm.Value)]    // never asked, but the row carries it anyway -> still stated
    [InlineData(true, false, TrackFactForm.Pending)]  // asked, kind 222 has not landed
    public void For_TempoFollowsTheSameEnrichmentGatingAsTheLane(bool asked, bool answered, TrackFactForm form)
    {
        var facts = TrackExpandedFacts.For(
            T(bpm: answered ? 128d : null, camelot: answered ? "8B" : null),
            new TrackFactsOptions(TempoPending: asked && !answered,
                                  Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc));

        Assert.Equal(form, Pick(facts, TrackFactKind.Bpm).Form);
        Assert.Equal(form, Pick(facts, TrackFactKind.Key).Form);
        if (form == TrackFactForm.Pending)
        {
            Assert.Equal(TrackExpandedFacts.Dash, Pick(facts, TrackFactKind.Bpm).Value);
            Assert.Equal(TrackExpandedFacts.Dash, Pick(facts, TrackFactKind.Key).Value);
        }
    }

    /// <summary>A surface that does not offer the column at all emits NO tempo row — not a dash. The dash means "we
    /// asked"; on search or artist Popular nobody did.</summary>
    [Fact]
    public void For_UnaskedTempo_EmitsNothing()
    {
        var facts = TrackExpandedFacts.For(T(), new TrackFactsOptions(
            TempoPending: false, Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc));

        Assert.False(Has(facts, TrackFactKind.Bpm));
        Assert.False(Has(facts, TrackFactKind.Key));
    }

    [Theory]
    [InlineData(0L, true, TrackFactForm.Pending)]
    [InlineData(0L, false, null)]
    [InlineData(1_847_392L, true, TrackFactForm.Value)]
    [InlineData(1_847_392L, false, TrackFactForm.Value)]
    public void For_PlaysFollowsTheSameEnrichmentGatingAsTheLane(long count, bool asked, TrackFactForm? form)
    {
        var facts = TrackExpandedFacts.For(T(playCount: count), new TrackFactsOptions(
            PlaysPending: asked, Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc));

        if (form is null) Assert.False(Has(facts, TrackFactKind.Plays));
        else Assert.Equal(form, Pick(facts, TrackFactKind.Plays).Form);
    }

    /// <summary>The strip states the EXACT count, not the lane's "1.8M": the lane is 52 DIP wide and this is the place
    /// a reader came to for the real number. Grouped in the injected culture.</summary>
    [Fact]
    public void For_PlaysStatesTheExactCountInTheInjectedCulture()
    {
        var facts = TrackExpandedFacts.For(T(playCount: 1_847_392),
            new TrackFactsOptions(Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc));

        Assert.Equal("1,847,392", Pick(facts, TrackFactKind.Plays).Value);
    }

    // ── the exact stamp ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The whole point of the Added fact: the table's lane says "3 days ago" (right for 88 DIP, useless to
    /// someone who opened the row to find out precisely when), and the strip says the instant. Full date + time of day,
    /// in the INJECTED culture and zone.</summary>
    [Fact]
    public void ExactStamp_IsAFullLocalisedDateAndTime()
    {
        Assert.Equal("Saturday, 28 September 2024 15:41",
            TrackExpandedFacts.ExactStamp(Added, CultureInfo.InvariantCulture, TimeZoneInfo.Utc));

        // The CULTURE is actually consulted — patterns, not just digits.
        Assert.Equal("28. September 2024 15:41 Uhr",
            TrackExpandedFacts.ExactStamp(Added, Patterned("d. MMMM yyyy", "HH:mm 'Uhr'"), TimeZoneInfo.Utc));

        // …and so is the ZONE: +05:00 pushes 15:41 UTC to 20:41 local.
        Assert.Equal("Saturday, 28 September 2024 20:41",
            TrackExpandedFacts.ExactStamp(Added, CultureInfo.InvariantCulture, PlusFive));
    }

    /// <summary>A release instant is a DAY. Minute precision beside "Added" would be false precision — nobody knows
    /// which minute a record came out, and the wire's timestamp does not mean that.</summary>
    [Fact]
    public void ExactDate_DropsTheTimeOfDay()
    {
        Assert.Equal("Friday, 01 March 2024",
            TrackExpandedFacts.ExactDate(Live, CultureInfo.InvariantCulture, TimeZoneInfo.Utc));
    }

    /// <summary>Epoch-or-earlier is UNKNOWN, not "added in 1970": zero is what a missing timestamp deserialises to
    /// across half the wire formats involved. Same sentinel rule the Liked rail facts use.</summary>
    [Fact]
    public void For_EpochStampsAreUnknownRatherThan1970()
    {
        var facts = TrackExpandedFacts.For(T(addedAt: DateTimeOffset.UnixEpoch),
            new TrackFactsOptions(Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc));

        Assert.False(Has(facts, TrackFactKind.Added));
    }

    // ── key notation ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Camelot encodes MODE in its own suffix — the B ring is major, the A ring minor — and that is the only
    /// mode signal this record carries (MusicalKey is the bare tonic). A slot we do not recognise yields no mode at
    /// all rather than a guessed one.</summary>
    [Theory]
    [InlineData("8B", KeyMode.Major)]
    [InlineData("11a", KeyMode.Minor)]
    [InlineData("4A", KeyMode.Minor)]
    [InlineData("8", KeyMode.Unknown)]
    [InlineData("", KeyMode.Unknown)]
    [InlineData(null, KeyMode.Unknown)]
    public void ModeOf_ReadsTheCamelotRing(string? camelot, KeyMode expected)
        => Assert.Equal(expected, TrackExpandedFacts.ModeOf(camelot));

    /// <summary>The expanded row has the width the lane never had, so it spells the key out — and degrades through
    /// every partial state instead of inventing the missing half.</summary>
    [Theory]
    [InlineData("8B", "C", "8B · C major")]
    [InlineData("11A", "A", "11A · A minor")]
    [InlineData("8B", null, "8B")]
    [InlineData(null, "C", "C")]
    [InlineData("", "", null)]
    [InlineData(null, null, null)]
    public void PrettyKey_PairsTheWheelSlotWithTheSpelledKey(string? camelot, string? tonic, string? expected)
        => Assert.Equal(expected, TrackExpandedFacts.PrettyKey(camelot, tonic, "major", "minor"));

    /// <summary>Without the injected mode words the key still renders — the tonic simply keeps no mode. The rules file
    /// holds no localized copy of its own, which is what keeps it free of the localization runtime.</summary>
    [Fact]
    public void PrettyKey_WithoutModeWords_KeepsTheTonicAndDropsTheMode()
        => Assert.Equal("8B · C", TrackExpandedFacts.PrettyKey("8B", "C"));

    /// <summary>The narrow-lane notation stays one token: Camelot when present, else the tonic, never both.</summary>
    [Theory]
    [InlineData("8B", "C", "8B")]
    [InlineData(null, "C", "C")]
    [InlineData(null, null, null)]
    public void KeyLabel_IsOneTokenForTheLane(string? camelot, string? tonic, string? expected)
        => Assert.Equal(expected, TrackExpandedFacts.KeyLabel(camelot, tonic));

    // ── the hero partition ───────────────────────────────────────────────────────────────────────────────────────────
    // The strip draws the same ordered list two ways — four facts at display size, the rest as prose — and WHICH four
    // is a rule of the fact list, not a renderer preference. Pinned here for the same reason the order is: a renderer
    // that owned the list would drift the first time a kind landed, silently and only on screen.

    /// <summary>The injected trio every hero test needs at once: culture, zone AND the two mode words, because a key
    /// with no mode word spells "C" rather than "C major" and that is the half the hero slot glosses with.</summary>
    static readonly TrackFactsOptions Injected = new(
        Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc, MajorWord: "major", MinorWord: "minor");

    /// <summary>Exactly four facts read as FIGURES: the three the relief ladder yields first (Plays · BPM · Key) plus
    /// Duration, the one measure every row carries. Everything else is a sentence — a date, a name, an album title, an
    /// ISRC — and a flag has no value to enlarge at all.
    ///
    /// <para>The enum roster is asserted alongside deliberately. A new kind defaults to prose, which is the right
    /// default and therefore the silent one; pinning the roster makes adding a kind FAIL here, so the classification
    /// is made on purpose rather than inherited.</para></summary>
    [Fact]
    public void IsHeroFact_IsExactlyPlaysBpmKeyAndDuration()
    {
        Assert.Equal(new[]
        {
            TrackFactKind.Plays, TrackFactKind.Bpm, TrackFactKind.Key, TrackFactKind.Added, TrackFactKind.Duration,
            TrackFactKind.Album, TrackFactKind.Released, TrackFactKind.AddedBy, TrackFactKind.Isrc,
            TrackFactKind.Descriptors, TrackFactKind.Explicit, TrackFactKind.Video, TrackFactKind.LocalFile,
            TrackFactKind.Unavailable,
        }, Enum.GetValues<TrackFactKind>());

        var hero = new HashSet<TrackFactKind>
        {
            TrackFactKind.Plays, TrackFactKind.Bpm, TrackFactKind.Key, TrackFactKind.Duration,
        };
        foreach (TrackFactKind kind in Enum.GetValues<TrackFactKind>())
            Assert.Equal(hero.Contains(kind), TrackExpandedFacts.IsHeroFact(kind));
    }

    /// <summary>The key is the ONE fact with two halves: the wheel slot is the figure (two glyphs, and it matches the
    /// swatch and the filter) and the spelling is the gloss beneath it. Every partial state degrades rather than
    /// inventing the missing half — no wheel slot means no figure to promote, so the spelling itself becomes the value
    /// and the slot draws one line instead of a line over a blank.</summary>
    [Theory]
    [InlineData("2B", "F♯", "2B", "F♯ major")]
    [InlineData("11A", "A", "11A", "A minor")]
    [InlineData("8B", null, "8B", null)]
    [InlineData(null, "C", "C", null)]
    public void HeroSplit_PromotesTheWheelSlotAndGlossesItWithTheSpelling(
        string? camelot, string? tonic, string expectedValue, string? expectedUnit)
    {
        var split = TrackExpandedFacts.HeroSplit(
            Pick(TrackExpandedFacts.For(T(camelot: camelot, musicalKey: tonic), Injected), TrackFactKind.Key));

        Assert.Equal(expectedValue, split.Value);
        Assert.Equal(expectedUnit, split.Unit);
    }

    /// <summary>Every other fact is ONE part, hero or not. No unit is invented to fill the second line: "min" under a
    /// duration and "BPM" under a tempo whose own label already says BPM are noise, and the value is the fact.</summary>
    [Theory]
    [InlineData(TrackFactKind.Plays, "1,847,392")]
    [InlineData(TrackFactKind.Bpm, "128")]
    [InlineData(TrackFactKind.Duration, "3:34")]
    [InlineData(TrackFactKind.Album, "Rumours")]
    [InlineData(TrackFactKind.Isrc, "USRC17607839")]
    public void HeroSplit_LeavesEveryOtherFactWhole(TrackFactKind kind, string expected)
    {
        var facts = TrackExpandedFacts.For(
            T(playCount: 1_847_392, bpm: 128d, durationMs: 214_000,
              albumName: "Rumours", albumUri: "spotify:album:a1", isrc: "USRC17607839"),
            Injected);

        var split = TrackExpandedFacts.HeroSplit(Pick(facts, kind));
        Assert.Equal(expected, split.Value);
        Assert.Null(split.Unit);
    }

    /// <summary>A pending hero slot needs no special case, and that is the point: <c>For</c> already wrote the em dash
    /// into the fact's Value, so the split is the ordinary one and the "asked, not answered yet" glyph stays ONE
    /// decision made in one place. The strip never has to ask "is this pending?" to know what to draw.</summary>
    [Fact]
    public void HeroSplit_PendingFactsCarryTheEmDashThatForAlreadyWrote()
    {
        var facts = TrackExpandedFacts.For(T(playCount: 0),
            Injected with { PlaysPending = true, TempoPending = true });

        foreach (var kind in new[] { TrackFactKind.Plays, TrackFactKind.Bpm, TrackFactKind.Key })
        {
            var f = Pick(facts, kind);
            Assert.Equal(TrackFactForm.Pending, f.Form);

            var split = TrackExpandedFacts.HeroSplit(f);
            Assert.Equal(TrackExpandedFacts.Dash, split.Value);
            Assert.Null(split.Unit);
        }
    }

    /// <summary>The prose form and the hero form are the SAME two strings arranged two ways. Pinned as a round trip
    /// through <c>KeySplit</c> — the one place the halves are decided — so the join and the cut can never drift into
    /// two formatters that agree only by luck. The last assert re-pins the combined literal: "8B · C major", separator
    /// and all, is exactly what <c>PrettyKey</c> said before the split existed.</summary>
    [Theory]
    [InlineData("8B", "C")]
    [InlineData("11A", "A")]
    [InlineData("2B", "F♯")]
    [InlineData("8B", null)]
    [InlineData(null, "C")]
    [InlineData("8", "C")]   // an unrecognised ring carries no mode, so the gloss is the bare tonic
    public void HeroSplit_IsTheExactInverseOfPrettyKeysJoin(string? camelot, string? tonic)
    {
        string combined = TrackExpandedFacts.PrettyKey(camelot, tonic, "major", "minor")!;
        var split = TrackExpandedFacts.HeroSplit(new TrackFact(TrackFactKind.Key, TrackFactForm.Value, combined));
        var halves = TrackExpandedFacts.KeySplit(camelot, tonic, "major", "minor")!.Value;

        Assert.Equal(halves.Value, split.Value);
        Assert.Equal(halves.Unit, split.Unit);
        Assert.Equal(combined, split.Unit is null ? split.Value : split.Value + " · " + split.Unit);
    }

    /// <summary>Three surfaces, three notations, one pair of halves: the 88-DIP lane keeps the single token, the prose
    /// line keeps the joined pair, the hero slot keeps them apart. <c>KeyLabel</c>'s behaviour is unchanged by the
    /// split — <c>TrackRow</c> and <c>TrackVersionsPanel</c> both call it and neither wants the spelling.</summary>
    [Fact]
    public void KeyNotation_LaneProseAndHeroAllSpeakOfTheSameKey()
    {
        Assert.Equal("2B", TrackExpandedFacts.KeyLabel("2B", "F♯"));
        Assert.Equal("2B · F♯ major", TrackExpandedFacts.PrettyKey("2B", "F♯", "major", "minor"));

        var split = TrackExpandedFacts.HeroSplit(
            Pick(TrackExpandedFacts.For(T(camelot: "2B", musicalKey: "F♯"), Injected), TrackFactKind.Key));
        Assert.Equal("2B", split.Value);
        Assert.Equal("F♯ major", split.Unit);
    }

    // ── the shared formatters ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, "0:00")]
    [InlineData(59_000L, "0:59")]
    [InlineData(214_000L, "3:34")]
    [InlineData(3_599_000L, "59:59")]
    [InlineData(7_199_000L, "1:59:59")]
    public void TrackTime_IsMinutesUntilItIsHours(long ms, string expected)
        => Assert.Equal(expected, TrackExpandedFacts.TrackTime(ms));

    /// <summary>The clock formatter will happily spell 0 ms as "0:00". The duration CELL must not: 0 is "not known
    /// yet", the same 0-is-unknown rule Plays already uses. A thin album disc row that still has no length must dash,
    /// not claim a zero-second track.</summary>
    [Theory]
    [InlineData(0L, "—")]
    [InlineData(-1L, "—")]
    [InlineData(214_000L, "3:34")]
    public void DurationCell_DashesUnknownLength(long ms, string expected)
        => Assert.Equal(expected, TrackExpandedFacts.DurationCell(ms));

    /// <summary>One decimal at most, and invariant: a comma decimal separator next to the key label reads as a list,
    /// and 101.0099… is noise a listener cannot act on.</summary>
    [Theory]
    [InlineData(101.0099d, "101")]
    [InlineData(101.5d, "101.5")]
    [InlineData(171.06d, "171.1")]
    [InlineData(128d, "128")]
    public void Bpm_RoundsToOneMeaningfulDecimal(double bpm, string expected)
        => Assert.Equal(expected, TrackExpandedFacts.Bpm(bpm));

    // ── the remaining shapes ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The album is the strip's one LINK, and it carries the uri so the renderer can route it through the same
    /// RichText table the row's own album lane uses (an episode's "album" is its show).</summary>
    [Fact]
    public void For_AlbumIsALinkCarryingItsUri()
    {
        var f = Pick(TrackExpandedFacts.For(T(albumName: "Rumours", albumUri: "spotify:album:a1"),
            new TrackFactsOptions(Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc)),
            TrackFactKind.Album);

        Assert.Equal(TrackFactForm.Link, f.Form);
        Assert.Equal("Rumours", f.Value);
        Assert.Equal("spotify:album:a1", f.LinkUri);
    }

    /// <summary>Added by prefers the resolved display name and falls back to the raw playlist membership id — the same
    /// two-step the row's own Added-by cell takes, so a row and its drawer never name the collaborator differently.</summary>
    [Theory]
    [InlineData("Jane", "raw-id", "Jane")]
    [InlineData(null, "raw-id", "raw-id")]
    [InlineData(null, null, null)]
    public void For_AddedByPrefersTheResolvedProfileName(string? resolved, string? raw, string? expected)
    {
        var facts = TrackExpandedFacts.For(T(addedBy: raw), new TrackFactsOptions(
            AddedByName: resolved, Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc));

        if (expected is null) Assert.False(Has(facts, TrackFactKind.AddedBy));
        else Assert.Equal(expected, Pick(facts, TrackFactKind.AddedBy).Value);
    }

    /// <summary>Descriptors ride as ONE chips fact carrying the server's own order (descending weight), not as N facts:
    /// they are one row of pills, and splitting them would interleave them with the flags.</summary>
    [Fact]
    public void For_DescriptorsAreOneChipsFactInServerOrder()
    {
        var f = Pick(TrackExpandedFacts.For(T(tags: new[] { "K-Pop", "Energetic" }),
            new TrackFactsOptions(Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc)),
            TrackFactKind.Descriptors);

        Assert.Equal(TrackFactForm.Chips, f.Form);
        Assert.Equal(new[] { "K-Pop", "Energetic" }, f.Chips!);
    }

    /// <summary>An empty tag list is a real "this track has none" and renders nothing — a chip row with no chips states
    /// nothing either way, and reserving one would claim the enrichment is still coming.</summary>
    [Fact]
    public void For_EmptyDescriptorsEmitNoChipsFact()
        => Assert.False(Has(TrackExpandedFacts.For(T(tags: Array.Empty<string>()),
            new TrackFactsOptions(Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc)),
            TrackFactKind.Descriptors));

    /// <summary>Flags are marks: the LABEL is the whole fact, so the value stays empty and the renderer draws a badge
    /// rather than a label/value pair with nothing on the value line.</summary>
    [Theory]
    [InlineData(TrackFactKind.Explicit)]
    [InlineData(TrackFactKind.Video)]
    [InlineData(TrackFactKind.LocalFile)]
    [InlineData(TrackFactKind.Unavailable)]
    public void For_FlagsCarryNoValue(TrackFactKind kind)
    {
        var facts = TrackExpandedFacts.For(
            T(isExplicit: true, origin: TrackOrigin.Local, availability: Availability.Unavailable, availableAt: Live),
            new TrackFactsOptions(HasVideo: true, Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc));

        var f = Pick(facts, kind);
        Assert.Equal(TrackFactForm.Flag, f.Form);
        Assert.Equal("", f.Value);
    }

    /// <summary>"Has a music video" is a property of the CATALOGUE ENTRY (the kind-99 association plane), never a field
    /// on Track — so it can only ever arrive as an option the host resolved.</summary>
    [Fact]
    public void For_VideoFlagComesFromTheHostNotTheRecord()
    {
        var opts = new TrackFactsOptions(Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc);
        Assert.False(Has(TrackExpandedFacts.For(T(), opts), TrackFactKind.Video));
        Assert.True(Has(TrackExpandedFacts.For(T(), opts with { HasVideo = true }), TrackFactKind.Video));
    }

    /// <summary>A row with no verdict at all is NOT "unavailable": null Availability means nobody told us, and only
    /// getAlbum/getTrack ever carry a verdict.</summary>
    [Fact]
    public void For_NoAvailabilityVerdictIsNotAnUnavailableFlag()
        => Assert.False(Has(TrackExpandedFacts.For(T(availability: null),
            new TrackFactsOptions(Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc)),
            TrackFactKind.Unavailable));

    /// <summary>A slot with no track states nothing rather than throwing — the shimmer/overscan case.</summary>
    [Fact]
    public void For_AnEmptySlotStatesNothing()
    {
        Assert.Empty(TrackExpandedFacts.For(null));
        Assert.Empty(TrackExpandedFacts.For(
            new Track("", "", "", Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0L, false, null)));
    }

    /// <summary>Every kind has its OWN label key. A duplicate would silently relabel one fact as another, and a missing
    /// one would ship a label-less tile.</summary>
    [Fact]
    public void LabelKey_IsPresentAndDistinctForEveryKind()
    {
        var seen = new HashSet<string>();
        foreach (TrackFactKind kind in Enum.GetValues<TrackFactKind>())
        {
            string key = TrackExpandedFacts.LabelKey(kind);
            Assert.False(string.IsNullOrWhiteSpace(key), $"{kind} has no label key");
            Assert.True(seen.Add(key), $"{kind} reuses the label key {key}");
        }
    }
}
