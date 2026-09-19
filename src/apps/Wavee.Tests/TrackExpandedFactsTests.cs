// ── Wavee.Tests/TrackExpandedFactsTests.cs — what an expanded track row STATES, in what order, and how it spells it ──
//
// Wave 4.5's gate for `Track.Facts` / `Track.FactsInput` (Entities/Track.Rules.cs), ported VERBATIM from 0.2.9's
// TrackExpandedFactsTests (591 lines). This is the rule that makes an expanded row COMPLETE at every list width: the
// table above it yields Plays, then BPM·key, then Added by, then Date added as it narrows, so the drawer must not ask
// the column set what to say. Three standing rules are pinned here because all three rot silently: ORDER is the enum's
// declaration order; an ABSENT fact renders nothing (the one exception is enrichment-pending); culture, zone and the two
// mode words are INJECTED.
//
// What moved: the input is a `FactsInput` of resolved values instead of a `Track` record (dates are unix seconds, 0 =
// none; a tempo of 0 is 0.2.9's null; the availability verdict is `AvailabilityKnown` + `Unavailable`, and `NotYetOut`
// is decided by `Track.NotYetOutOf` exactly as the row decides it); the album link is an `EntityUri`; the AddedBy person
// is a `User` handle; the three number formatters (`TrackTime`, `DurationCell`, `Bpm`) moved to `Track.Format`. The last
// fact builds the input from a REAL handle (`FactsInput.Of`), which is where the exact key/camelot tables meet the drawer.
//
// Interns the album uri and boots a scope in the Of facts, so it joins EntitiesCollection.

using System.Globalization;
using Xunit;
using Facts = Wavee.Track.Facts;
using FactKind = Wavee.Track.FactKind;
using FactForm = Wavee.Track.FactForm;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class TrackExpandedFactsTests
{
    static readonly DateTimeOffset Added = new(2024, 9, 28, 15, 41, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Live = new(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A fixed +05:00 zone, built by hand: the tz database is not the thing under test.</summary>
    static readonly TimeZoneInfo PlusFive =
        TimeZoneInfo.CreateCustomTimeZone("t+5", TimeSpan.FromHours(5), "t+5", "t+5");

    /// <summary>A culture whose date patterns are SET rather than inherited, so the pinned literal is the code's.</summary>
    static CultureInfo Patterned(string longDate, string shortTime)
    {
        var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        c.DateTimeFormat.LongDatePattern = longDate;
        c.DateTimeFormat.ShortTimePattern = shortTime;
        return c;
    }

    static int Seconds(DateTimeOffset at) => (int)at.ToUnixTimeSeconds();

    /// <summary>0.2.9's <c>T(...)</c> over the 0.3 input. <paramref name="unavailable"/>: null = nobody ruled (0.2.9's null
    /// Availability), true/false = a verdict. <paramref name="bpm"/> 0 = no kind-222 answer.</summary>
    static Track.FactsInput T(
        long durationMs = 180_000, bool isExplicit = false, DateTimeOffset? addedAt = null, string? addedBy = null,
        long playCount = 0, bool local = false, bool? unavailable = null, DateTimeOffset? availableAt = null,
        string? isrc = null, double bpm = 0d, string? musicalKey = null, string? camelot = null,
        IReadOnlyList<string>? tags = null, string albumName = "", string albumUri = "")
    {
        int available = availableAt is { } live ? Seconds(live) : 0;
        bool ruled = unavailable is not null;
        bool blocked = unavailable is true;
        return new Track.FactsInput(
            HasIdentity: true,
            PlayCount: playCount,
            TempoBpm: bpm,
            CamelotCode: camelot,
            MusicalKey: musicalKey,
            AddedAt: addedAt is { } at ? Seconds(at) : 0,
            DurationMs: (int)durationMs,
            AlbumName: albumName,
            AlbumUri: albumUri.Length == 0 ? default : EntityUri.Parse(albumUri),
            AvailableAt: available,
            AddedByRaw: addedBy,
            Isrc: isrc,
            Tags: tags,
            Explicit: isExplicit,
            Local: local,
            AvailabilityKnown: ruled,
            Unavailable: blocked,
            NotYetOut: Track.NotYetOutOf(ruled, blocked, available, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    static FactKind[] Kinds(IReadOnlyList<Track.Fact> facts)
    {
        var k = new FactKind[facts.Count];
        for (int i = 0; i < facts.Count; i++) k[i] = facts[i].Kind;
        return k;
    }

    static Track.Fact Pick(IReadOnlyList<Track.Fact> facts, FactKind kind)
    {
        for (int i = 0; i < facts.Count; i++) if (facts[i].Kind == kind) return facts[i];
        Assert.Fail($"no {kind} fact was emitted");
        return default;
    }

    static bool Has(IReadOnlyList<Track.Fact> facts, FactKind kind)
    {
        for (int i = 0; i < facts.Count; i++) if (facts[i].Kind == kind) return true;
        return false;
    }

    static Track.FactsOptions Utc => new(Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc);

    // ── ordering ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every fact at once, in the one order the strip ever draws. Deliberately implausible (a local file with a
    /// stream count and an ISRC) — this pins ORDER, not plausibility.</summary>
    [Fact]
    public void For_EmitsEveryPresentFactInDeclarationOrder()
    {
        var facts = Facts.For(
            T(playCount: 1_847_392, bpm: 128d, camelot: "8B", musicalKey: "C", addedAt: Added,
              durationMs: 214_000, albumName: "Rumours", albumUri: "spotify:album:a1",
              availableAt: Live, unavailable: true, addedBy: "raw-id",
              isrc: "USRC17607839", tags: new[] { "Rock", "Mellow" }, isExplicit: true,
              local: true),
            Utc with { HasVideo = true });

        Assert.Equal(new[]
        {
            FactKind.Plays, FactKind.Bpm, FactKind.Key, FactKind.Added, FactKind.Duration,
            FactKind.Album, FactKind.Released, FactKind.AddedBy, FactKind.Isrc,
            FactKind.Descriptors, FactKind.Explicit, FactKind.Video, FactKind.LocalFile,
            FactKind.Unavailable,
        }, Kinds(facts));
    }

    /// <summary>A bare row says one thing and invents nothing: no "—" album, no "0" plays, no key.</summary>
    [Fact]
    public void For_OmitsEveryAbsentFactRatherThanDashingIt()
    {
        var facts = Facts.For(T(), Utc);

        Assert.Equal(new[] { FactKind.Duration }, Kinds(facts));
    }

    /// <summary>Order does not depend on WHICH facts are present: the emitted kinds are always ascending.</summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void For_IsAscendingInKindWhateverIsMissing(bool withAdded, bool withAlbum, bool withIsrc)
    {
        var facts = Facts.For(
            T(addedAt: withAdded ? Added : null,
              albumName: withAlbum ? "Rumours" : "",
              isrc: withIsrc ? "USRC17607839" : null),
            Utc);

        for (int i = 1; i < facts.Count; i++)
            Assert.True(facts[i - 1].Kind < facts[i].Kind, $"{facts[i - 1].Kind} must precede {facts[i].Kind}");
    }

    /// <summary>An unreleased row states WHEN, and nothing it cannot know: "0 plays" would read as a real, dismal track,
    /// and "Unavailable" beside a release date reads as a contradiction.</summary>
    [Fact]
    public void For_PendingRelease_StatesTheDateAndNotTheEmptyNumbers()
    {
        var facts = Facts.For(
            T(durationMs: 0, playCount: 0, unavailable: true, availableAt: DateTimeOffset.UtcNow.AddYears(5)),
            Utc with { PlaysPending = true });

        Assert.Equal(new[] { FactKind.Released }, Kinds(facts));
    }

    // ── enrichment gating ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ONE honest dash: asked and not answered says so; never asked says nothing; answered says the value.</summary>
    [Theory]
    [InlineData(true, true, FactForm.Value)]     // asked, answered
    [InlineData(false, true, FactForm.Value)]    // never asked, but the row carries it anyway -> still stated
    [InlineData(true, false, FactForm.Pending)]  // asked, kind 222 has not landed
    public void For_TempoFollowsTheSameEnrichmentGatingAsTheLane(bool asked, bool answered, FactForm form)
    {
        var facts = Facts.For(
            T(bpm: answered ? 128d : 0d, camelot: answered ? "8B" : null),
            Utc with { TempoPending = asked && !answered });

        Assert.Equal(form, Pick(facts, FactKind.Bpm).Form);
        Assert.Equal(form, Pick(facts, FactKind.Key).Form);
        if (form == FactForm.Pending)
        {
            Assert.Equal(Facts.Dash, Pick(facts, FactKind.Bpm).Value);
            Assert.Equal(Facts.Dash, Pick(facts, FactKind.Key).Value);
        }
    }

    /// <summary>A surface that does not offer the column emits NO tempo row — the dash means "we asked".</summary>
    [Fact]
    public void For_UnaskedTempo_EmitsNothing()
    {
        var facts = Facts.For(T(), Utc with { TempoPending = false });

        Assert.False(Has(facts, FactKind.Bpm));
        Assert.False(Has(facts, FactKind.Key));
    }

    [Theory]
    [InlineData(0L, true, FactForm.Pending)]
    [InlineData(0L, false, null)]
    [InlineData(1_847_392L, true, FactForm.Value)]
    [InlineData(1_847_392L, false, FactForm.Value)]
    public void For_PlaysFollowsTheSameEnrichmentGatingAsTheLane(long count, bool asked, FactForm? form)
    {
        var facts = Facts.For(T(playCount: count), Utc with { PlaysPending = asked });

        if (form is null) Assert.False(Has(facts, FactKind.Plays));
        else Assert.Equal(form, Pick(facts, FactKind.Plays).Form);
    }

    /// <summary>The strip states the EXACT count, not the lane's "1.8M", grouped in the injected culture.</summary>
    [Fact]
    public void For_PlaysStatesTheExactCountInTheInjectedCulture()
    {
        var facts = Facts.For(T(playCount: 1_847_392), Utc);

        Assert.Equal("1,847,392", Pick(facts, FactKind.Plays).Value);
    }

    // ── the exact stamp ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The lane says "3 days ago"; the strip says the instant — full date + time of day, in the INJECTED culture
    /// and zone.</summary>
    [Fact]
    public void ExactStamp_IsAFullLocalisedDateAndTime()
    {
        Assert.Equal("Saturday, 28 September 2024 15:41",
            Facts.ExactStamp(Added.ToUnixTimeSeconds(), CultureInfo.InvariantCulture, TimeZoneInfo.Utc));

        // The CULTURE is actually consulted — patterns, not just digits.
        Assert.Equal("28. September 2024 15:41 Uhr",
            Facts.ExactStamp(Added.ToUnixTimeSeconds(), Patterned("d. MMMM yyyy", "HH:mm 'Uhr'"), TimeZoneInfo.Utc));

        // …and so is the ZONE: +05:00 pushes 15:41 UTC to 20:41 local.
        Assert.Equal("Saturday, 28 September 2024 20:41",
            Facts.ExactStamp(Added.ToUnixTimeSeconds(), CultureInfo.InvariantCulture, PlusFive));
    }

    /// <summary>A release instant is a DAY; minute precision beside "Added" would be false precision.</summary>
    [Fact]
    public void ExactDate_DropsTheTimeOfDay()
    {
        Assert.Equal("Friday, 01 March 2024",
            Facts.ExactDate(Live.ToUnixTimeSeconds(), CultureInfo.InvariantCulture, TimeZoneInfo.Utc));
    }

    /// <summary>Epoch is UNKNOWN, not "added in 1970": zero is what a missing timestamp decodes to.</summary>
    [Fact]
    public void For_EpochStampsAreUnknownRatherThan1970()
    {
        var facts = Facts.For(T(addedAt: DateTimeOffset.UnixEpoch), Utc);

        Assert.False(Has(facts, FactKind.Added));
    }

    // ── key notation ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Camelot encodes MODE in its suffix (B major, A minor); a slot we do not recognise yields no mode.</summary>
    [Theory]
    [InlineData("8B", Track.KeyMode.Major)]
    [InlineData("11a", Track.KeyMode.Minor)]
    [InlineData("4A", Track.KeyMode.Minor)]
    [InlineData("8", Track.KeyMode.Unknown)]
    [InlineData("", Track.KeyMode.Unknown)]
    [InlineData(null, Track.KeyMode.Unknown)]
    public void ModeOf_ReadsTheCamelotRing(string? camelot, Track.KeyMode expected)
        => Assert.Equal(expected, Facts.ModeOf(camelot));

    /// <summary>The expanded row spells the key out and degrades through every partial state.</summary>
    [Theory]
    [InlineData("8B", "C", "8B · C major")]
    [InlineData("11A", "A", "11A · A minor")]
    [InlineData("8B", null, "8B")]
    [InlineData(null, "C", "C")]
    [InlineData("", "", null)]
    [InlineData(null, null, null)]
    public void PrettyKey_PairsTheWheelSlotWithTheSpelledKey(string? camelot, string? tonic, string? expected)
        => Assert.Equal(expected, Facts.PrettyKey(camelot, tonic, "major", "minor"));

    /// <summary>Without the injected mode words the tonic simply keeps no mode.</summary>
    [Fact]
    public void PrettyKey_WithoutModeWords_KeepsTheTonicAndDropsTheMode()
        => Assert.Equal("8B · C", Facts.PrettyKey("8B", "C"));

    /// <summary>The narrow-lane notation stays one token: Camelot when present, else the tonic, never both.</summary>
    [Theory]
    [InlineData("8B", "C", "8B")]
    [InlineData(null, "C", "C")]
    [InlineData(null, null, null)]
    public void KeyLabel_IsOneTokenForTheLane(string? camelot, string? tonic, string? expected)
        => Assert.Equal(expected, Facts.KeyLabel(camelot, tonic));

    // ── the hero partition ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The injected trio every hero test needs: culture, zone AND the two mode words.</summary>
    static readonly Track.FactsOptions Injected = new(
        Culture: CultureInfo.InvariantCulture, Zone: TimeZoneInfo.Utc, MajorWord: "major", MinorWord: "minor");

    /// <summary>Exactly four facts read as FIGURES. The enum roster is asserted alongside so adding a kind FAILS here and
    /// its classification is made on purpose rather than inherited.</summary>
    [Fact]
    public void IsHeroFact_IsExactlyPlaysBpmKeyAndDuration()
    {
        Assert.Equal(new[]
        {
            FactKind.Plays, FactKind.Bpm, FactKind.Key, FactKind.Added, FactKind.Duration,
            FactKind.Album, FactKind.Released, FactKind.AddedBy, FactKind.Isrc,
            FactKind.Descriptors, FactKind.Explicit, FactKind.Video, FactKind.LocalFile,
            FactKind.Unavailable,
        }, Enum.GetValues<FactKind>());

        var hero = new HashSet<FactKind> { FactKind.Plays, FactKind.Bpm, FactKind.Key, FactKind.Duration };
        foreach (FactKind kind in Enum.GetValues<FactKind>())
            Assert.Equal(hero.Contains(kind), Facts.IsHeroFact(kind));
    }

    /// <summary>The key is the ONE fact with two halves; every partial state degrades rather than inventing one.</summary>
    [Theory]
    [InlineData("2B", "F♯", "2B", "F♯ major")]
    [InlineData("11A", "A", "11A", "A minor")]
    [InlineData("8B", null, "8B", null)]
    [InlineData(null, "C", "C", null)]
    public void HeroSplit_PromotesTheWheelSlotAndGlossesItWithTheSpelling(
        string? camelot, string? tonic, string expectedValue, string? expectedUnit)
    {
        var split = Facts.HeroSplit(Pick(Facts.For(T(camelot: camelot, musicalKey: tonic), Injected), FactKind.Key));

        Assert.Equal(expectedValue, split.Value);
        Assert.Equal(expectedUnit, split.Unit);
    }

    /// <summary>Every other fact is ONE part; no unit is invented to fill the second line.</summary>
    [Theory]
    [InlineData(FactKind.Plays, "1,847,392")]
    [InlineData(FactKind.Bpm, "128")]
    [InlineData(FactKind.Duration, "3:34")]
    [InlineData(FactKind.Album, "Rumours")]
    [InlineData(FactKind.Isrc, "USRC17607839")]
    public void HeroSplit_LeavesEveryOtherFactWhole(FactKind kind, string expected)
    {
        var facts = Facts.For(
            T(playCount: 1_847_392, bpm: 128d, durationMs: 214_000,
              albumName: "Rumours", albumUri: "spotify:album:a1", isrc: "USRC17607839"),
            Injected);

        var split = Facts.HeroSplit(Pick(facts, kind));
        Assert.Equal(expected, split.Value);
        Assert.Null(split.Unit);
    }

    /// <summary>A pending hero slot needs no special case: <c>For</c> already wrote the em dash into the Value.</summary>
    [Fact]
    public void HeroSplit_PendingFactsCarryTheEmDashThatForAlreadyWrote()
    {
        var facts = Facts.For(T(playCount: 0), Injected with { PlaysPending = true, TempoPending = true });

        foreach (var kind in new[] { FactKind.Plays, FactKind.Bpm, FactKind.Key })
        {
            var f = Pick(facts, kind);
            Assert.Equal(FactForm.Pending, f.Form);

            var split = Facts.HeroSplit(f);
            Assert.Equal(Facts.Dash, split.Value);
            Assert.Null(split.Unit);
        }
    }

    /// <summary>The prose form and the hero form are the SAME two strings arranged two ways, pinned as a round trip
    /// through <c>KeySplit</c>; the last assert re-pins the combined literal separator and all.</summary>
    [Theory]
    [InlineData("8B", "C")]
    [InlineData("11A", "A")]
    [InlineData("2B", "F♯")]
    [InlineData("8B", null)]
    [InlineData(null, "C")]
    [InlineData("8", "C")]   // an unrecognised ring carries no mode, so the gloss is the bare tonic
    public void HeroSplit_IsTheExactInverseOfPrettyKeysJoin(string? camelot, string? tonic)
    {
        string combined = Facts.PrettyKey(camelot, tonic, "major", "minor")!;
        var split = Facts.HeroSplit(new Track.Fact(FactKind.Key, FactForm.Value, combined));
        var halves = Facts.KeySplit(camelot, tonic, "major", "minor")!.Value;

        Assert.Equal(halves.Value, split.Value);
        Assert.Equal(halves.Unit, split.Unit);
        Assert.Equal(combined, split.Unit is null ? split.Value : split.Value + " · " + split.Unit);
    }

    /// <summary>Three surfaces, three notations, one pair of halves.</summary>
    [Fact]
    public void KeyNotation_LaneProseAndHeroAllSpeakOfTheSameKey()
    {
        Assert.Equal("2B", Facts.KeyLabel("2B", "F♯"));
        Assert.Equal("2B · F♯ major", Facts.PrettyKey("2B", "F♯", "major", "minor"));

        var split = Facts.HeroSplit(Pick(Facts.For(T(camelot: "2B", musicalKey: "F♯"), Injected), FactKind.Key));
        Assert.Equal("2B", split.Value);
        Assert.Equal("F♯ major", split.Unit);
    }

    // ── the shared formatters (now Track.Format; the strip forwards to them) ─────────────────────────────────────────

    [Theory]
    [InlineData(0L, "0:00")]
    [InlineData(59_000L, "0:59")]
    [InlineData(214_000L, "3:34")]
    [InlineData(3_599_000L, "59:59")]
    [InlineData(7_199_000L, "1:59:59")]
    public void TrackTime_IsMinutesUntilItIsHours(long ms, string expected)
        => Assert.Equal(expected, Track.Format.TrackTime(ms));

    /// <summary>The clock spells 0 ms as "0:00"; the duration CELL must not — 0 is "not known yet".</summary>
    [Theory]
    [InlineData(0L, "—")]
    [InlineData(-1L, "—")]
    [InlineData(214_000L, "3:34")]
    public void DurationCell_DashesUnknownLength(long ms, string expected)
        => Assert.Equal(expected, Track.Format.DurationCell(ms));

    /// <summary>One decimal at most, and invariant.</summary>
    [Theory]
    [InlineData(101.0099d, "101")]
    [InlineData(101.5d, "101.5")]
    [InlineData(171.06d, "171.1")]
    [InlineData(128d, "128")]
    public void Bpm_RoundsToOneMeaningfulDecimal(double bpm, string expected)
        => Assert.Equal(expected, Track.Format.Bpm(bpm));

    // ── the remaining shapes ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The album is the strip's one LINK, and it carries the uri the renderer routes.</summary>
    [Fact]
    public void For_AlbumIsALinkCarryingItsUri()
    {
        var f = Pick(Facts.For(T(albumName: "Rumours", albumUri: "spotify:album:a1"), Utc), FactKind.Album);

        Assert.Equal(FactForm.Link, f.Form);
        Assert.Equal("Rumours", f.Value);
        Assert.Equal("spotify:album:a1", f.LinkUri.Text);
    }

    /// <summary>Added by prefers the resolved display name and falls back to the raw membership id.</summary>
    [Theory]
    [InlineData("Jane", "raw-id", "Jane")]
    [InlineData(null, "raw-id", "raw-id")]
    [InlineData(null, null, null)]
    public void For_AddedByPrefersTheResolvedProfileName(string? resolved, string? raw, string? expected)
    {
        var facts = Facts.For(T(addedBy: raw), Utc with { AddedByName = resolved });

        if (expected is null) Assert.False(Has(facts, FactKind.AddedBy));
        else Assert.Equal(expected, Pick(facts, FactKind.AddedBy).Value);
    }

    /// <summary>With the full collaborator profile resolved, AddedBy carries the PERSON, so the strip draws the column's
    /// avatar chip rather than a bare name.</summary>
    [Fact]
    public void AddedBy_WithResolvedProfile_CarriesThePerson()
    {
        var jane = new User(7);
        var f = Pick(Facts.For(T(addedBy: "u1"), Utc with { AddedByName = "Jane", AddedByProfile = jane }),
                     FactKind.AddedBy);

        Assert.Equal("Jane", f.Value);
        Assert.Equal(jane, f.Person);
    }

    /// <summary>Unresolved membership falls back to the plain string and carries NO person — never an avatar for someone
    /// it never resolved.</summary>
    [Fact]
    public void AddedBy_Unresolved_FallsBackToTheRawId()
    {
        var f = Pick(Facts.For(T(addedBy: "raw-id"), Utc), FactKind.AddedBy);

        Assert.Equal("raw-id", f.Value);
        Assert.Equal(default(User), f.Person);
    }

    /// <summary>Descriptors ride as ONE chips fact in the server's own order.</summary>
    [Fact]
    public void For_DescriptorsAreOneChipsFactInServerOrder()
    {
        var f = Pick(Facts.For(T(tags: new[] { "K-Pop", "Energetic" }), Utc), FactKind.Descriptors);

        Assert.Equal(FactForm.Chips, f.Form);
        Assert.Equal(new[] { "K-Pop", "Energetic" }, f.Chips!);
    }

    /// <summary>An empty tag list is a real "this track has none" and renders nothing.</summary>
    [Fact]
    public void For_EmptyDescriptorsEmitNoChipsFact()
        => Assert.False(Has(Facts.For(T(tags: Array.Empty<string>()), Utc), FactKind.Descriptors));

    /// <summary>Flags are marks: the LABEL is the whole fact, so the value stays empty.</summary>
    [Theory]
    [InlineData(FactKind.Explicit)]
    [InlineData(FactKind.Video)]
    [InlineData(FactKind.LocalFile)]
    [InlineData(FactKind.Unavailable)]
    public void For_FlagsCarryNoValue(FactKind kind)
    {
        var facts = Facts.For(T(isExplicit: true, local: true, unavailable: true, availableAt: Live),
                              Utc with { HasVideo = true });

        var f = Pick(facts, kind);
        Assert.Equal(FactForm.Flag, f.Form);
        Assert.Equal("", f.Value);
    }

    /// <summary>"Has a music video" is the catalogue entry's (the video planes), so it arrives as an option.</summary>
    [Fact]
    public void For_VideoFlagComesFromTheHostNotTheRecord()
    {
        Assert.False(Has(Facts.For(T(), Utc), FactKind.Video));
        Assert.True(Has(Facts.For(T(), Utc with { HasVideo = true }), FactKind.Video));
    }

    /// <summary>A row with no verdict at all is NOT "unavailable": nobody told us.</summary>
    [Fact]
    public void For_NoAvailabilityVerdictIsNotAnUnavailableFlag()
        => Assert.False(Has(Facts.For(T(unavailable: null), Utc), FactKind.Unavailable));

    /// <summary>A slot with no track states nothing rather than throwing — the shimmer/overscan case.</summary>
    [Fact]
    public void For_AnEmptySlotStatesNothing()
    {
        Assert.Empty(Facts.For(default));

        TestScope.Fresh();
        Assert.Empty(Facts.For(Track.FactsInput.Of(default, 0, 0)));
    }

    /// <summary>Every kind has its OWN label key: a duplicate would silently relabel one fact as another.</summary>
    [Fact]
    public void LabelKey_IsPresentAndDistinctForEveryKind()
    {
        var seen = new HashSet<string>();
        foreach (FactKind kind in Enum.GetValues<FactKind>())
        {
            string key = Facts.LabelKey(kind);
            Assert.False(string.IsNullOrWhiteSpace(key), $"{kind} has no label key");
            Assert.True(seen.Add(key), $"{kind} reuses the label key {key}");
        }
    }

    // ── FactsInput.Of over a real handle (the 0.3 half) ─────────────────────────────────────────────────────────────

    /// <summary>The drawer's input from committed columns: the key and camelot BYTES become the exact labels (so the prose
    /// line reads "11B · F# major"), the membership stamp is the caller's, and a row whose release instant is still ahead
    /// states Released and nothing it cannot know yet.</summary>
    [Fact]
    public void FactsInput_Of_turns_the_two_rings_into_the_same_key_line_the_wire_spelled()
    {
        TestScope.Fresh();
        long now = 1_800_000_000;
        var s = Staging.Rent();
        ref var row = ref s.Tracks.Add();
        row.Id = s.Text("spotify:track:factsof");
        row.Title = s.Text("Everything In Its Right Place");
        row.DurationMs = 251_000;
        row.Flags = (uint)TrackFlags.Explicit;
        row.PlayCount = 1_847_392;
        row.Tempo = 1284;
        row.Key = Spotify.Decode.KeyCode("F#"u8);
        row.Camelot = Spotify.Decode.CamelotCode("11B"u8);
        row.Known = (uint)(TrackFields.Identity | TrackFields.PlayCount | TrackFields.Audio | TrackFields.Availability);
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse("spotify:track:factsof"));
        var input = Track.FactsInput.Of(track, addedAt: Seconds(Added), now);

        Assert.True(input.HasIdentity);
        Assert.Equal("11B", input.CamelotCode);
        Assert.Equal("F#", input.MusicalKey);
        Assert.Equal(128.4, input.TempoBpm);
        Assert.True(input.AvailabilityKnown);
        Assert.False(input.Unavailable);
        Assert.False(input.NotYetOut);

        var facts = Facts.For(input, Injected);
        Assert.Equal("11B · F# major", Pick(facts, FactKind.Key).Value);
        Assert.Equal("128.4", Pick(facts, FactKind.Bpm).Value);
        Assert.Equal("1,847,392", Pick(facts, FactKind.Plays).Value);
        Assert.Equal("Saturday, 28 September 2024 15:41", Pick(facts, FactKind.Added).Value);
        Assert.True(Has(facts, FactKind.Explicit));
        Assert.False(Has(facts, FactKind.Unavailable));
    }

    /// <summary>Before kind 222 lands the rings read as ABSENT, never as "C" / "1A": the column's zero is unknown.</summary>
    [Fact]
    public void FactsInput_Of_an_unenriched_row_names_no_key_and_no_tempo()
    {
        TestScope.Fresh();
        var track = Entities.Track(EntityUri.Parse("spotify:track:factscold"));

        var input = Track.FactsInput.Of(track, 0, 0);

        Assert.True(input.HasIdentity);
        Assert.Null(input.CamelotCode);
        Assert.Null(input.MusicalKey);
        Assert.Equal(0d, input.TempoBpm);
        Assert.Null(input.Tags);                                      // not fetched ≠ none, and neither renders
        Assert.False(Has(Facts.For(input, Injected), FactKind.Key));
    }
}
