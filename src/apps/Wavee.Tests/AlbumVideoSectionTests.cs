// ── Wavee.Tests/AlbumVideoSectionTests.cs — the album page's music-video section (ch 05 W12) ─────────────────────────
//
// NEW 2026-09-17. The card this covers had four defects at once, reported on Wham!'s "Last Christmas" (3 tracks, 3
// videos):
//
//   1. ONE card for N videos — the gate was a boolean `any` reduction over the members, so one video and three
//      rendered the identical single card.
//   2. The WRONG image — the card drew `Album.ImageId`, so a music video was advertised with the album sleeve. The
//      old spec never said where the thumbnail came from, which is how that survived a parity pass.
//   3. The WRONG subtitle — the card was fed the ALBUM, so it read "3 songs · 16 min · 1984" under a video.
//   4. NOTHING AT ALL on a full-length album — the section was gated on `PageRules.IsShortRelease`, so a 12-track
//      album with three music videos surfaced no videos anywhere.
//
// Two more were found on the rewrite itself and are pinned here too:
//
//   5. THE CARD PLAYED THE SONG, NOT THE VIDEO — the click was `Playback.PlayContext(member.Id)` alone, and the
//      reducer only routes a row to the video host when the surface is WANTED (`KindOfRow` = `videoWanted &&
//      (flags & VideoMask)`). `videoWanted` comes from the placement state, whose `Requested` starts at
//      `SurfacePlacement.None` — so a play badge over a video still started the ordinary song. `PageRules.WatchFor`
//      is the decision that fixes it: request the surface FIRST, then play.
//   6. FOUR VIDEOS WRAPPED INTO A 2×2 BLOCK — the multi arm was a wrapping grid. It is a horizontal SHELF
//      (`PageRules.VideoArm.Shelf`, drawn on the shared `PagedShelf` with its edge fade and its pips).
//
// Everything here is engine-free: `PageRules.SelectVideos` is a pure fold over member handles (the tables are real,
// the UI is not), `ArmFor`/`WatchFor` are pure over an int and two bools, and the fact-strip facts are pure over
// `Track.FactsInput`. The renderer that consumes all of it lives in `Album.Page.cs` (`VideosSection` / `VideoHero` /
// `VideoShelf`); these are its inputs, not its pixels.

using Xunit;
using Facts = Wavee.Track.Facts;
using FactForm = Wavee.Track.FactForm;
using FactKind = Wavee.Track.FactKind;
using Rules = Wavee.Album.PageRules;

namespace Wavee.Tests;

/// <summary>The selection over real member rows: which tracks have videos, and what each entry carries.</summary>
[Collection(EntitiesCollection.Name)]
public class AlbumVideoSelectionTests
{
    /// <summary>One member row. <paramref name="videoUri"/> mints the kind-99 counterpart; <paramref name="still"/> is
    /// the member's own <c>VideoImage</c> (what kind 99 writes beside the counterpart uri).</summary>
    static Track Member(string uri, string title, string cover, bool video = false, bool custom = false,
                        string? videoUri = null, string? still = null, int durationMs = 0, int counterpartMs = 0)
    {
        var tracks = Entities.Current.Tracks;
        int slot = tracks.Slot(uri.AsSpan());
        tracks.SetText(ref tracks.Title, slot, Entities.Strings.Intern(title));
        tracks.SetText(ref tracks.Image, slot, Entities.Strings.Intern(cover));
        tracks.DurationMs[slot] = durationMs;
        if (video) tracks.Flags[slot] |= (uint)TrackFlags.HasVideo;
        if (custom) tracks.Flags[slot] |= (uint)TrackFlags.VideoOverride;
        if (still is not null) tracks.SetText(ref tracks.VideoImage, slot, Entities.Strings.Intern(still));
        if (videoUri is not null)
        {
            int counterpart = tracks.Slot(videoUri.AsSpan());
            tracks.VideoCounterpart[slot] = counterpart;
            if (counterpartMs > 0) tracks.DurationMs[counterpart] = counterpartMs;
        }
        return new Track(slot);
    }

    static Rules.AlbumVideo[] Videos(ReadOnlySpan<Track> members, int cap = Rules.VideoCap)
    {
        var into = new Rules.AlbumVideo[cap];
        int n = Rules.SelectVideos(members, into);
        return into[..n];
    }

    // ── 0 → no section, 1 → the hero arm, 3 → three entries in track order ──────────────────────────────────────────

    [Fact]
    public void NoMemberHasAVideo_SelectsNothing_AndTheSectionDoesNotMount()
    {
        TestScope.Fresh();
        Track[] members =
        [
            Member("spotify:track:n0", "Song A", "cover"),
            Member("spotify:track:n1", "Song B", "cover"),
        ];
        Assert.Empty(Videos(members));
        Assert.False(Rules.HasTrailingSections(hasVideo: false, about: false, 0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void OneVideo_IsOneEntry_TheHeroArm()
    {
        TestScope.Fresh();
        Track[] members =
        [
            Member("spotify:track:h0", "Song A", "cover"),
            Member("spotify:track:h1", "Last Christmas", "cover", video: true,
                   videoUri: "spotify:track:hv1", still: "video-still-1", durationMs: 262_000),
        ];
        var videos = Videos(members);
        var one = Assert.Single(videos);
        Assert.Equal(members[1].Slot, one.MemberSlot);
        Assert.Equal(Entities.Strings.Intern("video-still-1"), one.Thumb);
    }

    [Fact]
    public void ThreeVideos_AreThreeEntries_InTrackOrder_EachWithItsOwnStillAndCounterpart()
    {
        TestScope.Fresh();
        Track[] members =
        [
            Member("spotify:track:w0", "Last Christmas", "album-cover", video: true,
                   videoUri: "spotify:track:wv0", still: "still-0"),
            Member("spotify:track:w1", "Everything She Wants", "album-cover"),          // no video: skipped
            Member("spotify:track:w2", "Pudding Mix", "album-cover", video: true,
                   videoUri: "spotify:track:wv2", still: "still-2"),
            Member("spotify:track:w3", "Everything She Wants (Remix)", "album-cover", video: true,
                   videoUri: "spotify:track:wv3", still: "still-3"),
        ];
        var videos = Videos(members);

        Assert.Equal(3, videos.Length);
        Assert.Equal(new[] { members[0].Slot, members[2].Slot, members[3].Slot },
                     new[] { videos[0].MemberSlot, videos[1].MemberSlot, videos[2].MemberSlot });
        // Each entry carries its OWN still and its OWN counterpart — no entry may alias another's.
        Assert.Equal(new[] { Entities.Strings.Intern("still-0"), Entities.Strings.Intern("still-2"), Entities.Strings.Intern("still-3") },
                     new[] { videos[0].Thumb, videos[1].Thumb, videos[2].Thumb });
        Assert.Equal(new[] { members[0].VideoCounterpart.Slot, members[2].VideoCounterpart.Slot, members[3].VideoCounterpart.Slot },
                     new[] { videos[0].CounterpartSlot, videos[1].CounterpartSlot, videos[2].CounterpartSlot });
        Assert.Equal(3, videos.Select(v => v.CounterpartSlot).Distinct().Count());
    }

    // ── the thumbnail ladder: the video's own still, NEVER the album cover (defect 2) ───────────────────────────────

    [Fact]
    public void Thumbnail_IsTheMembersOwnVideoStill_WhenKind99WroteOne()
    {
        TestScope.Fresh();
        Track[] members = [Member("spotify:track:t0", "Song", "song-art", video: true,
                                  videoUri: "spotify:track:tv0", still: "video-still")];
        var one = Assert.Single(Videos(members));
        Assert.Equal(Entities.Strings.Intern("video-still"), one.Thumb);
        Assert.NotEqual(members[0].ImageId, one.Thumb);      // and NOT the row's cover, which is the album's
    }

    [Fact]
    public void Thumbnail_FallsBackToTheCounterpartsOwnArt_ThenToTheSongs()
    {
        TestScope.Fresh();
        var tracks = Entities.Current.Tracks;

        // No `VideoImage`, but the counterpart row has its own art.
        Track viaCounterpart = Member("spotify:track:f0", "Song", "song-art", video: true, videoUri: "spotify:track:fv0");
        int counterpart = viaCounterpart.VideoCounterpart.Slot;
        tracks.SetText(ref tracks.Image, counterpart, Entities.Strings.Intern("counterpart-art"));
        Assert.Equal(Entities.Strings.Intern("counterpart-art"), Assert.Single(Videos([viaCounterpart])).Thumb);

        // Nothing anywhere — a user-attached mp4 has no still at all — so the song's own art is the honest last rung.
        Track viaSong = Member("spotify:track:f1", "Song", "song-art", custom: true);
        Assert.Equal(viaSong.ImageId, Assert.Single(Videos([viaSong])).Thumb);
    }

    // ── the duration: the VIDEO's, not the song's ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Duration_IsTheCounterparts_WhenItHasOne_ElseTheSongs()
    {
        TestScope.Fresh();
        Track withCounterpartLength = Member("spotify:track:d0", "Song", "cover", video: true,
                                             videoUri: "spotify:track:dv0", durationMs: 262_000, counterpartMs: 400_000);
        Assert.Equal(400_000, Assert.Single(Videos([withCounterpartLength])).DurationMs);

        Track songOnly = Member("spotify:track:d1", "Song", "cover", video: true,
                                videoUri: "spotify:track:dv1", durationMs: 262_000);
        Assert.Equal(262_000, Assert.Single(Videos([songOnly])).DurationMs);

        Track nothingKnown = Member("spotify:track:d2", "Song", "cover", custom: true);
        Assert.Equal(0, Assert.Single(Videos([nothingKnown])).DurationMs);   // 0 = say nothing
    }

    // ── the two video planes and the cap ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AUserAttachedOverrideIsAVideo_AndCarriesNoCounterpart()
    {
        TestScope.Fresh();
        Track custom = Member("spotify:track:c0", "Song", "cover", custom: true);
        var one = Assert.Single(Videos([custom]));
        Assert.Equal(custom.Slot, one.MemberSlot);
        Assert.Equal(Table.None, one.CounterpartSlot);
    }

    /// <summary>What the card PLAYS is the member, never the counterpart. Kind 99 keys a video on its SONG, so the
    /// counterpart row is a metadata handle (the video's own title and length) and not a playable of its own — the
    /// versions drawer's play verb targets the parent for exactly this reason. A card that tried to play
    /// <c>CounterpartSlot</c> would ask for a row the context resolve has never seen.</summary>
    [Fact]
    public void ThePlayTargetIsTheOwningMember_NeverTheCounterpartRow()
    {
        TestScope.Fresh();
        Track member = Member("spotify:track:p0", "Last Christmas", "cover", video: true,
                              videoUri: "spotify:track:pv0", still: "still");
        var one = Assert.Single(Videos([member]));
        Assert.Equal(member.Slot, one.MemberSlot);
        Assert.NotEqual(one.MemberSlot, one.CounterpartSlot);
        Assert.Equal(member.VideoCounterpart.Slot, one.CounterpartSlot);
    }

    [Fact]
    public void SelectVideos_StopsAtTheSpan()
    {
        TestScope.Fresh();
        Track[] members =
        [
            Member("spotify:track:s0", "A", "cover", video: true, still: "s0"),
            Member("spotify:track:s1", "B", "cover", video: true, still: "s1"),
            Member("spotify:track:s2", "C", "cover", video: true, still: "s2"),
        ];
        Assert.Equal(2, Videos(members, cap: 2).Length);
        Assert.Equal(3, Videos(members).Length);
    }

    // ── defect 4: the section is not a short-release privilege ──────────────────────────────────────────────────────

    [Fact]
    public void AFullLengthAlbumWithVideos_MountsTheSection_AndWaitsForItsVerdict()
    {
        TestScope.Fresh();
        var members = new Track[12];
        for (int i = 0; i < members.Length; i++)
            members[i] = Member("spotify:track:full" + i, "Track " + i, "cover", video: i < 3, still: "still" + i);

        // Twelve tracks is NOT a short release …
        Assert.False(Rules.IsShortRelease(AlbumKind.Album, members.Length));
        // … and the section mounts anyway, with one entry per video-bearing row. (It used to show nothing at all.)
        Assert.Equal(3, Videos(members).Length);
        Assert.True(Rules.HasTrailingSections(hasVideo: true, about: false, 0, 0, 0, 0, 0, 0));

        // The trailing reserve waits for the verdict at ANY length now, so the section cannot land a frame after the
        // band reveals and shove About-the-artist down: it is the FIRST section in the band.
        Assert.False(Rules.VideoDecided(members));
        for (int i = 0; i < members.Length; i++)
            Entities.Current.Tracks.Known[members[i].Slot] |= (uint)TrackFields.Video;
        Assert.True(Rules.VideoDecided(members));
    }
}

/// <summary>Which ARM the section draws (defect 6). One video is the hero card 0.2.9 always drew; two or more is a
/// HORIZONTAL SHELF. There is deliberately no third arm: a wrapping grid was built, shown, and rejected on sight —
/// four videos in a 2×2 block reads as a page of its own rather than a strip under the tracklist.</summary>
public class AlbumVideoArmTests
{
    [Fact]
    public void NoVideos_IsNoArm_SoTheSectionNeverMountsEmpty()
        => Assert.Equal(Rules.VideoArm.None, Rules.ArmFor(0));

    /// <summary>A negative count cannot happen (<c>SelectVideos</c> returns a count in <c>[0, span]</c>) but the arm
    /// must still name "nothing", never fall through to a shelf of no cards.</summary>
    [Fact]
    public void ANegativeCountIsStillNoArm()
        => Assert.Equal(Rules.VideoArm.None, Rules.ArmFor(-1));

    [Fact]
    public void ExactlyOneVideo_IsTheHeroCard()
        => Assert.Equal(Rules.VideoArm.Hero, Rules.ArmFor(1));

    /// <summary>TWO is already a shelf. The boundary is the whole decision: the hero card is for the case where there
    /// is nothing to page through.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]          // Wham!'s "Last Christmas" + the remixes — the 2×2 block the user rejected
    [InlineData(Rules.VideoCap)]
    public void TwoOrMoreVideos_IsTheShelf_NeverAGrid(int count)
        => Assert.Equal(Rules.VideoArm.Shelf, Rules.ArmFor(count));

    /// <summary>Every count the selection can produce maps to exactly one arm, and the hero is the only singleton.</summary>
    [Fact]
    public void TheArmIsAFunctionOfTheCountAlone_AcrossTheWholeCapRange()
    {
        for (int n = 0; n <= Rules.VideoCap; n++)
        {
            var arm = Rules.ArmFor(n);
            Assert.Equal(n == 0 ? Rules.VideoArm.None : n == 1 ? Rules.VideoArm.Hero : Rules.VideoArm.Shelf, arm);
        }
    }
}

/// <summary>What an explicit click on a video card DOES (defect 5). The reducer decides a row's media kind from
/// <c>videoWanted &amp;&amp; (flags &amp; VideoMask)</c>, and <c>videoWanted</c> is the placement state's — which starts
/// at <c>SurfacePlacement.None</c>. So "play the song" and "watch the video" are two different instructions and only
/// one of them asks for the surface. All three arms below request it or say why they cannot; none of them silently
/// plays audio from under a play badge, which was the whole defect.</summary>
public class AlbumWatchTargetTests
{
    /// <summary>The ordinary case: nothing of this row is on the deck, a host exists. Request the surface, THEN play —
    /// the order is the contract, because the reducer folds its inbox FIFO in one batch and the placement input has to
    /// land before the load for <c>KindOfRow</c> to read <c>videoWanted = true</c>.</summary>
    [Fact]
    public void AColdClickRequestsTheSurfaceAndThenPlays()
        => Assert.Equal(Rules.WatchAction.RequestThenPlay, Rules.WatchFor(canHostVideo: true, isDeckRow: false));

    /// <summary>The clicked row is ALREADY playing. Switching it to video must not restart it: the placement input
    /// alone re-decides the kind and reloads it on the video host at the carried position.</summary>
    [Fact]
    public void ClickingTheRowAlreadyOnTheDeckSwitchesInPlace_AndDoesNotRestartIt()
        => Assert.Equal(Rules.WatchAction.SwitchInPlace, Rules.WatchFor(canHostVideo: true, isDeckRow: true));

    /// <summary>No placement can host a video (no rail room, no second window, no fullscreen hook). The card SAYS so
    /// and then plays the song — it is the one arm that ends in audio, and it is never silent about it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithNoHostAtAll_ItIsAudio_AndThatOutranksEvenTheDeckRow(bool isDeckRow)
        => Assert.Equal(Rules.WatchAction.AudioOnly, Rules.WatchFor(canHostVideo: false, isDeckRow: isDeckRow));

    /// <summary>The bug in one assertion: the OLD click was "play the song", which is this table's audio arm — and it
    /// was taken on a state (a host is available, the row is cold) whose correct answer is to ask for the surface
    /// first. The two must never be the same verdict again.</summary>
    [Fact]
    public void TheCaseTheUserHit_AHostIsAvailableAndTheRowIsCold_IsNotTheAudioArm()
    {
        var hit = Rules.WatchFor(canHostVideo: true, isDeckRow: false);
        Assert.NotEqual(Rules.WatchAction.AudioOnly, hit);
        Assert.Equal(Rules.WatchAction.RequestThenPlay, hit);
    }

    /// <summary>The decision takes NO count, so the hero's one card and a shelf's sixteen cannot disagree about what a
    /// click means — the arms differ in layout only. Pinned because the hero arm shipped working and the shelf arm was
    /// added after it, which is exactly the shape of a divergence nobody would notice.
    /// <para>The whole map, in four states: a host is the only thing that decides whether the surface is asked for, and
    /// the deck only decides whether the row restarts.</para></summary>
    [Fact]
    public void TheDecisionTakesNoVideoCount_SoBothArmsClickIdentically()
    {
        // Availability alone separates "watch" from "audio" …
        Assert.Equal(Rules.WatchAction.AudioOnly, Rules.WatchFor(false, false));
        Assert.Equal(Rules.WatchAction.AudioOnly, Rules.WatchFor(false, true));
        // … and, once a host exists, the deck alone separates "switch" from "start".
        Assert.Equal(Rules.WatchAction.RequestThenPlay, Rules.WatchFor(true, false));
        Assert.Equal(Rules.WatchAction.SwitchInPlace, Rules.WatchFor(true, true));
        // Three outcomes over four states: no fifth verdict can appear without this failing.
        var all = new[] { Rules.WatchFor(false, false), Rules.WatchFor(false, true),
                          Rules.WatchFor(true, false), Rules.WatchFor(true, true) };
        Assert.Equal(3, all.Distinct().Count());
    }
}

/// <summary>The expanded row's flag run: "Music video" and the availability verdict are TWO facts about two different
/// things, and the drawer middot-joins them onto one line — so they must never read as one ("Music video ·
/// Unavailable" was taken to mean the VIDEO was unavailable). The verdict is about the AUDIO, which is why its label
/// (<c>detail.trackFacts.unavailable</c>) names its own subject: "Audio unavailable".</summary>
public class AlbumVideoFactSeparationTests
{
    static Track.FactsInput Row(bool ruled = false, bool unavailable = false) => new(
        HasIdentity: true, DurationMs: 180_000, AvailabilityKnown: ruled, Unavailable: ruled && unavailable);

    static bool Has(IReadOnlyList<Track.Fact> facts, FactKind kind) => Find(facts, kind) is not null;

    static Track.Fact? Find(IReadOnlyList<Track.Fact> facts, FactKind kind)
    {
        for (int i = 0; i < facts.Count; i++) if (facts[i].Kind == kind) return facts[i];
        return null;
    }

    [Theory]
    [InlineData(false, false, false, false)]   // available, no video: neither fact
    [InlineData(true, false, true, false)]     // a video, available audio: the mark alone
    [InlineData(false, true, false, true)]     // no video, blocked audio: the verdict alone
    [InlineData(true, true, true, true)]       // both: TWO facts, never one
    public void TheVideoMarkAndTheAvailabilityVerdictAreIndependent(bool hasVideo, bool blocked, bool expectVideo, bool expectUnavailable)
    {
        var facts = Facts.For(Row(ruled: blocked, unavailable: blocked), new Track.FactsOptions(HasVideo: hasVideo));
        Assert.Equal(expectVideo, Has(facts, FactKind.Video));
        Assert.Equal(expectUnavailable, Has(facts, FactKind.Unavailable));
    }

    [Fact]
    public void BothAreFlags_ButTwoDistinctKinds_WithDistinctLabels()
    {
        var facts = Facts.For(Row(ruled: true, unavailable: true), new Track.FactsOptions(HasVideo: true));
        var video = Find(facts, FactKind.Video);
        var verdict = Find(facts, FactKind.Unavailable);
        Assert.True(video.HasValue);
        Assert.True(verdict.HasValue);
        Assert.Equal(FactForm.Flag, video!.Value.Form);
        Assert.Equal(FactForm.Flag, verdict!.Value.Form);
        Assert.NotEqual(video.Value.Kind, verdict.Value.Kind);
        // The label IS the whole fact for a flag, so two flags on one line can only be told apart by their two keys.
        Assert.NotEqual(Facts.LabelKey(FactKind.Video), Facts.LabelKey(FactKind.Unavailable));
    }

    /// <summary>The verdict never claims anything about the video: an unruled row states nothing, and a not-yet-out row
    /// states its date instead (a date beside "unavailable" is a contradiction) — in both cases the video mark is
    /// untouched.</summary>
    [Fact]
    public void AnUnruledOrPendingRowStatesNoVerdict_AndStillStatesItsVideo()
    {
        var unruled = Facts.For(Row(), new Track.FactsOptions(HasVideo: true));
        Assert.True(Has(unruled, FactKind.Video));
        Assert.False(Has(unruled, FactKind.Unavailable));

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var pending = Facts.For(
            Row(ruled: true, unavailable: true) with { NotYetOut = true, AvailableAt = (int)(now + 86_400) },
            new Track.FactsOptions(HasVideo: true));
        Assert.True(Has(pending, FactKind.Video));
        Assert.False(Has(pending, FactKind.Unavailable));
    }
}
