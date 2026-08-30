using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Wavee;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Actions;

// P3 of the universal video overrides — the UX SURFACE half. Everything the context menu, the Settings roster and the
// row indicator DECIDE is engine-free (VideoOverrideUx), so it is pinned here against production code rather than
// against a mock of it:
//   • which rows the Video ▸ submenu builds, per attachment state, and its single-selection gate,
//   • attach validation (extension + existence) and what a drop set contributes,
//   • the status chip, incl. the Missing-vs-Drive-offline split that decides whether "Locate…" is even offered,
//   • the Settings roster: ordering, title fallback, per-row capabilities, and sentinel-driven rebuild,
//   • the undo contract (a replace's undo restores the PREVIOUS file; a first attach's undo detaches),
//   • the indicator predicate that makes an override-only video light the row + pass the "Videos only" filter.
//
// The tests are one class on purpose: VideoPresence is a process-wide attachment point (row rendering cannot afford a
// context read per row), and xunit runs the tests INSIDE a class sequentially — so nothing here races it.
public class VideoOverrideUxTests
{
    static VideoOverrideService Svc(params (string Uri, string Path)[] attached)
    {
        var svc = new VideoOverrideService(new InMemoryStore());
        svc.FileExists = _ => true;
        foreach (var (uri, path) in attached) svc.Attach(uri, path);
        return svc;
    }

    static bool NoDirs(string _) => false;
    static bool AllDirs(string _) => true;

    // ── the Video ▸ submenu shape ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Menu_NoAttachment_OffersAttachOnly()
    {
        var svc = Svc();
        Assert.Equal(VideoMenuItems.Attach, VideoOverrideUx.MenuFor(true, "spotify:track:a", svc));
    }

    [Fact]
    public void Menu_Attached_OffersReplaceRemoveAndReveal_NeverAttach()
    {
        var svc = Svc(("spotify:track:a", @"C:\v\a.mp4"));

        var items = VideoOverrideUx.MenuFor(true, "spotify:track:a", svc);

        Assert.Equal(VideoMenuItems.Replace | VideoMenuItems.Remove | VideoMenuItems.ShowInExplorer, items);
        Assert.Equal(VideoMenuItems.None, items & VideoMenuItems.Attach);   // the duplicate path IS the replace path
        Assert.Equal(VideoMenuItems.None, items & VideoMenuItems.Locate);   // nothing to repair
    }

    [Fact]
    public void Menu_BrokenLink_SwapsRevealForLocate()
    {
        var svc = Svc(("spotify:track:a", @"C:\v\gone.mp4"));
        svc.FileExists = _ => false;

        var items = VideoOverrideUx.MenuFor(true, "spotify:track:a", svc);

        Assert.Equal(VideoMenuItems.Replace | VideoMenuItems.Remove | VideoMenuItems.Locate, items);
        Assert.Equal(VideoMenuItems.None, items & VideoMenuItems.ShowInExplorer);   // there is nothing to reveal
    }

    [Fact]
    public void Menu_Quarantined_StillOffersRevealAndRepair_ButNotLocate()
    {
        var svc = Svc(("spotify:track:a", @"C:\v\bad.mp4"));
        svc.TryGetActive("spotify:track:a", out var o);
        svc.Quarantine("spotify:track:a", o.SourceKey);

        var items = VideoOverrideUx.MenuFor(true, "spotify:track:a", svc);

        // The file is THERE — it just won't decode. Reveal it (so the user can inspect it), replace it, or detach it.
        Assert.Equal(VideoMenuItems.Replace | VideoMenuItems.Remove | VideoMenuItems.ShowInExplorer, items);
    }

    [Fact]
    public void Menu_MultiSelection_OrNoService_OrNoUri_IsAbsentEntirely()
    {
        var svc = Svc(("spotify:track:a", @"C:\v\a.mp4"));
        Assert.Equal(VideoMenuItems.None, VideoOverrideUx.MenuFor(false, "spotify:track:a", svc));  // multi-select
        Assert.Equal(VideoMenuItems.None, VideoOverrideUx.MenuFor(true, "spotify:track:a", null));  // kill switch
        Assert.Equal(VideoMenuItems.None, VideoOverrideUx.MenuFor(true, "", svc));
        Assert.Equal(VideoMenuItems.None, VideoOverrideUx.MenuFor(true, null, svc));
    }

    [Fact]
    public void Menu_IsUriKeyed_SoAnEpisodeGetsTheSameSubmenuAsATrack()
    {
        var svc = Svc(("spotify:episode:e1", @"C:\v\e.mp4"));
        Assert.Equal(VideoMenuItems.Attach, VideoOverrideUx.MenuFor(true, "spotify:track:a", svc));
        Assert.True((VideoOverrideUx.MenuFor(true, "spotify:episode:e1", svc) & VideoMenuItems.Replace) != 0);
    }

    // ── attach validation ────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\v\a.mp4", true)]
    [InlineData(@"C:\v\a.MP4", true)]
    [InlineData(@"C:\v\a.Mp4", true)]
    [InlineData(@"C:\v\a.mkv", false)]
    [InlineData(@"C:\v\amp4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMp4_IsCaseInsensitiveAndExtensionExact(string? path, bool expected)
        => Assert.Equal(expected, VideoOverrideUx.IsMp4(path));

    [Fact]
    public void Validate_RejectsNonMp4_BeforeItEverTouchesTheDisk()
    {
        bool probed = false;
        var r = VideoOverrideUx.Validate(@"C:\v\a.mkv", _ => { probed = true; return true; });
        Assert.Equal(VideoAttachRejection.NotMp4, r);
        Assert.False(probed);
    }

    [Fact]
    public void Validate_RejectsMissingFile_AndTreatsAThrowingProbeAsMissing()
    {
        Assert.Equal(VideoAttachRejection.NotFound, VideoOverrideUx.Validate(@"C:\v\a.mp4", _ => false));
        Assert.Equal(VideoAttachRejection.NotFound, VideoOverrideUx.Validate(@"\\nas\v\a.mp4", _ => throw new IOException()));
        Assert.Equal(VideoAttachRejection.None, VideoOverrideUx.Validate(@"C:\v\a.mp4", _ => true));
    }

    [Fact]
    public void FirstMp4_TakesTheFirstVideoInAMixedDrop_AndNullWhenThereIsNone()
    {
        Assert.Equal(@"C:\v\b.mp4", VideoOverrideUx.FirstMp4(new[] { @"C:\v\a.txt", @"C:\v\b.mp4", @"C:\v\c.mp4" }));
        Assert.Null(VideoOverrideUx.FirstMp4(new[] { @"C:\v\a.txt", @"C:\v\b.mkv" }));
        Assert.Null(VideoOverrideUx.FirstMp4(Array.Empty<string>()));
        Assert.Null(VideoOverrideUx.FirstMp4(null));
    }

    // ── status chips ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Status_PresentFileIsOk_AndQuarantinedIsUnplayable()
    {
        var svc = Svc(("spotify:track:a", @"C:\v\a.mp4"));
        Assert.Equal(VideoOverrideStatus.Ok, VideoOverrideUx.StatusOf(svc.Decide("spotify:track:a"), AllDirs));

        svc.TryGetActive("spotify:track:a", out var o);
        svc.Quarantine("spotify:track:a", o.SourceKey);
        Assert.Equal(VideoOverrideStatus.Unplayable, VideoOverrideUx.StatusOf(svc.Decide("spotify:track:a"), AllDirs));
    }

    [Fact]
    public void Status_BrokenSplitsOnWhetherTheVolumeIsStillMounted()
    {
        var svc = Svc(("spotify:track:a", @"C:\v\gone.mp4"));
        svc.FileExists = _ => false;
        var d = svc.Decide("spotify:track:a");

        // The drive is there, the file moved → repairable, so the roster offers "Locate…".
        Assert.Equal(VideoOverrideStatus.Missing, VideoOverrideUx.StatusOf(d, AllDirs));
        // The whole volume is gone → it heals by itself when the drive returns; never prompt to remove the link.
        Assert.Equal(VideoOverrideStatus.DriveOffline, VideoOverrideUx.StatusOf(d, NoDirs));
    }

    [Fact]
    public void Status_DriveOfflineRowsAreNeitherLocatableNorRevealable()
    {
        var offline = new VideoOverrideRow(default, VideoOverrideStatus.DriveOffline, "t", null, "a.mp4");
        var missing = new VideoOverrideRow(default, VideoOverrideStatus.Missing, "t", null, "a.mp4");
        var ok = new VideoOverrideRow(default, VideoOverrideStatus.Ok, "t", null, "a.mp4");
        var bad = new VideoOverrideRow(default, VideoOverrideStatus.Unplayable, "t", null, "a.mp4");

        Assert.False(offline.CanLocate);
        Assert.False(offline.CanReveal);
        Assert.True(missing.CanLocate);
        Assert.False(missing.CanReveal);
        Assert.False(ok.CanLocate);
        Assert.True(ok.CanReveal);
        Assert.True(bad.CanReveal);   // the file is there; the user may want to look at it
    }

    // ── repair pick start folder ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NearestExistingAncestor_WalksUpToTheClosestSurvivingFolder()
    {
        string full = Path.GetFullPath(@"C:\media\videos\2026\clip.mp4");
        string keep = Path.GetDirectoryName(Path.GetDirectoryName(full)!)!;    // ...\media\videos
        Assert.Equal(keep, VideoOverrideUx.NearestExistingAncestor(full, d => d.Length <= keep.Length));
    }

    [Fact]
    public void NearestExistingAncestor_IsNullWhenNothingOnTheChainExists()
    {
        Assert.Null(VideoOverrideUx.NearestExistingAncestor(@"Z:\gone\clip.mp4", NoDirs));
        Assert.Null(VideoOverrideUx.NearestExistingAncestor(null, AllDirs));
        Assert.Null(VideoOverrideUx.NearestExistingAncestor("", AllDirs));
    }

    // ── the Settings roster ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Roster_ListsEveryAttachment_NewestFirst_WithFileNameAndStatus()
    {
        var store = new InMemoryStore();
        var svc = new VideoOverrideService(store) { FileExists = _ => true };
        svc.Attach("spotify:track:a", @"C:\v\a.mp4");
        svc.Attach("spotify:track:b", @"C:\v\b.mp4");
        // AddedAtUnix has 1s resolution, so pin the ordering deterministically through the store rather than by sleeping.
        store.UpsertVideoOverride(store.GetVideoOverride("spotify:track:b")!.Value with { AddedAtUnix = 5_000 });
        store.UpsertVideoOverride(store.GetVideoOverride("spotify:track:a")!.Value with { AddedAtUnix = 1_000 });
        svc.Reload();

        var rows = VideoOverrideUx.BuildRoster(svc, AllDirs);

        Assert.Equal(2, rows.Count);
        Assert.Equal("spotify:track:b", rows[0].Uri);          // newest attachment first
        Assert.Equal("spotify:track:a", rows[1].Uri);
        Assert.Equal("b.mp4", rows[0].FileName);
        Assert.All(rows, r => Assert.Equal(VideoOverrideStatus.Ok, r.Status));
    }

    [Fact]
    public void Roster_ResolvesTitleAndArtists_AndFallsBackToTheUriForAnUnknownPlayable()
    {
        var svc = Svc(("spotify:track:known", @"C:\v\a.mp4"), ("spotify:track:stranger", @"C:\v\b.mp4"));
        var known = new Track("known", "spotify:track:known", "Real Title",
            new[] { new ArtistRef("ar", "spotify:artist:ar", "An Artist"), new ArtistRef("ar2", "spotify:artist:ar2", "Another") },
            new AlbumRef("al", "spotify:album:al", "Al"), 1000, false, null);

        var rows = VideoOverrideUx.BuildRoster(svc, AllDirs, uri => uri == known.Uri ? known : null);

        var hit = rows.Single(r => r.Uri == "spotify:track:known");
        Assert.Equal("Real Title", hit.Title);
        Assert.Equal("An Artist, Another", hit.Subtitle);
        // A device-wide roster outlives any one account's catalog: showing the raw uri is honest, showing nothing is not.
        var miss = rows.Single(r => r.Uri == "spotify:track:stranger");
        Assert.Equal("spotify:track:stranger", miss.Title);
        Assert.Null(miss.Subtitle);
    }

    [Fact]
    public void Roster_IsEmptyWithoutACurationService_AndSurvivesAThrowingResolver()
    {
        Assert.Empty(VideoOverrideUx.BuildRoster(null, AllDirs));
        var svc = Svc(("spotify:track:a", @"C:\v\a.mp4"));
        var rows = VideoOverrideUx.BuildRoster(svc, AllDirs, _ => throw new InvalidOperationException("store is busy"));
        Assert.Single(rows);
        Assert.Equal("spotify:track:a", rows[0].Title);
    }

    [Fact]
    public void Roster_RebuildsFromTheStoreSentinel_WhenAnAttachHappensElsewhere()
    {
        var store = new InMemoryStore();
        var svc = new VideoOverrideService(store) { FileExists = _ => true };
        int rosterBumps = 0;
        using var sub = store.Changes.Subscribe(Observers.From<StoreChange>(c =>
        {
            if (string.Equals(c.Uri, VideoOverride.ChangeKey, StringComparison.Ordinal)) rosterBumps++;
        }));

        Assert.Empty(VideoOverrideUx.BuildRoster(svc, AllDirs));

        svc.Attach("spotify:track:a", @"C:\v\a.mp4");      // e.g. the track context menu, on another page
        Assert.Equal(1, rosterBumps);                       // the roster sentinel is what Settings subscribes to
        Assert.Single(VideoOverrideUx.BuildRoster(svc, AllDirs));

        svc.Remove("spotify:track:a");
        Assert.Equal(2, rosterBumps);
        Assert.Empty(VideoOverrideUx.BuildRoster(svc, AllDirs));
    }

    // ── the Manage flyout: recency section, search, and the root's section state ─────────────────────────────────────
    // The roster moved out of the Settings card into an anchored "Manage" flyout (root = search + recently-added +
    // Browse all…, leaf = the full roster). Everything the flyout DECIDES is pure, so it is pinned here rather than
    // through a rendered tree.

    static VideoOverrideRow Row(string uri, long addedAt, string title, string? artists = null, string file = "clip.mp4")
        => new(new VideoOverride(uri, @"C:\v\" + file, "", 0, 0, 0, addedAt),
               VideoOverrideStatus.Ok, title, artists, file);

    [Fact]
    public void RecentlyAdded_TakesTheNewestN_NewestFirst_EvenFromAnUnsortedInput()
    {
        var rows = new[]
        {
            Row("spotify:track:c", 3_000, "C"),
            Row("spotify:track:a", 1_000, "A"),
            Row("spotify:track:e", 5_000, "E"),
            Row("spotify:track:b", 2_000, "B"),
            Row("spotify:track:d", 4_000, "D"),
        };

        var recent = VideoOverrideUx.RecentlyAdded(rows, 3);

        Assert.Equal(new[] { "E", "D", "C" }, recent.Select(r => r.Title).ToArray());
        // The input list is never reordered under the caller (the settings page holds it across renders).
        Assert.Equal("C", rows[0].Title);
    }

    [Fact]
    public void RecentlyAdded_TiesBreakOnUri_SoASameSecondBatchNeverShuffles()
    {
        // AddedAtUnix has 1s resolution — attaching three files from one drop lands them all on the same stamp.
        var rows = new[] { Row("spotify:track:c", 7, "C"), Row("spotify:track:a", 7, "A"), Row("spotify:track:b", 7, "B") };

        var first = VideoOverrideUx.RecentlyAdded(rows, 3).Select(r => r.Uri).ToArray();
        var again = VideoOverrideUx.RecentlyAdded(rows.Reverse().ToArray(), 3).Select(r => r.Uri).ToArray();

        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b", "spotify:track:c" }, first);
        Assert.Equal(first, again);
    }

    [Fact]
    public void RecentlyAdded_ReturnsEverythingWhenTheRosterIsShorterThanN_AndNothingForDegenerateInput()
    {
        var rows = new[] { Row("spotify:track:a", 1, "A"), Row("spotify:track:b", 2, "B") };
        Assert.Equal(2, VideoOverrideUx.RecentlyAdded(rows, 5).Count);
        Assert.Empty(VideoOverrideUx.RecentlyAdded(rows, 0));
        Assert.Empty(VideoOverrideUx.RecentlyAdded(Array.Empty<VideoOverrideRow>()));
        Assert.Empty(VideoOverrideUx.RecentlyAdded(null));
        Assert.Equal(VideoOverrideUx.RecentCount, VideoOverrideUx.RecentlyAdded(
            Enumerable.Range(0, 20).Select(i => Row("spotify:track:" + i, i, "T" + i)).ToArray()).Count);
    }

    [Fact]
    public void Search_MatchesTitleArtistAndFileName_CaseInsensitively()
    {
        var rows = new[]
        {
            Row("spotify:track:a", 3, "Midnight City", "M83", "midnight-live.mp4"),
            Row("spotify:track:b", 2, "Outro", "M83", "outro.mp4"),
            Row("spotify:track:c", 1, "Teardrop", "Massive Attack", "tear.MP4"),
        };

        Assert.Equal(new[] { "spotify:track:a" }, VideoOverrideUx.Search(rows, "midnight").Select(r => r.Uri));
        Assert.Equal(new[] { "spotify:track:a" }, VideoOverrideUx.Search(rows, "MIDNIGHT").Select(r => r.Uri));
        // artist line
        Assert.Equal(2, VideoOverrideUx.Search(rows, "m83").Count);
        // file name (and the extension's case is not the user's problem)
        Assert.Equal(new[] { "spotify:track:c" }, VideoOverrideUx.Search(rows, "tear.mp4").Select(r => r.Uri));
        Assert.Empty(VideoOverrideUx.Search(rows, "nothing here"));
    }

    [Fact]
    public void Search_PreservesRosterOrder_AndAnEmptyOrWhitespaceQueryRestoresEverything()
    {
        var rows = new[]
        {
            Row("spotify:track:a", 3, "Alpha", "Band"),
            Row("spotify:track:b", 2, "Beta", "Band"),
            Row("spotify:track:c", 1, "Gamma", "Band"),
        };

        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, VideoOverrideUx.Search(rows, "band").Select(r => r.Title));
        Assert.Same(rows, VideoOverrideUx.Search(rows, ""));
        Assert.Same(rows, VideoOverrideUx.Search(rows, "   "));
        Assert.Same(rows, VideoOverrideUx.Search(rows, null));
        Assert.Empty(VideoOverrideUx.Search(Array.Empty<VideoOverrideRow>(), "a"));
        Assert.Empty(VideoOverrideUx.Search(null, "a"));
    }

    [Fact]
    public void Search_IgnoresSurroundingWhitespace_ButNeverMatchesOnThePath()
    {
        var rows = new[] { Row("spotify:track:a", 1, "Alpha", "Band", "clip.mp4") };
        Assert.Single(VideoOverrideUx.Search(rows, "  alpha  "));
        // The whole roster can live under one folder — matching the path would make every row a hit.
        Assert.Empty(VideoOverrideUx.Search(rows, @"C:\v"));
    }

    [Fact]
    public void IsSearching_TreatsWhitespaceAsNoQuery()
    {
        Assert.False(VideoOverrideUx.IsSearching(null));
        Assert.False(VideoOverrideUx.IsSearching(""));
        Assert.False(VideoOverrideUx.IsSearching("   "));
        Assert.True(VideoOverrideUx.IsSearching("a"));
    }

    [Fact]
    public void RootSection_WalksEmpty_Recent_Results_NoMatches()
    {
        // Nothing attached → teach the context-menu attach path, whatever is in the (hidden) search box.
        Assert.Equal(VideoManagerSection.Empty, VideoOverrideUx.RootSection(0, null, 0));
        Assert.Equal(VideoManagerSection.Empty, VideoOverrideUx.RootSection(0, "anything", 0));
        // Resting root.
        Assert.Equal(VideoManagerSection.Recent, VideoOverrideUx.RootSection(9, null, 0));
        Assert.Equal(VideoManagerSection.Recent, VideoOverrideUx.RootSection(9, "  ", 0));
        // Typing swaps the recent section for the results IN PLACE — search never requires a drill.
        Assert.Equal(VideoManagerSection.Results, VideoOverrideUx.RootSection(9, "mid", 2));
        Assert.Equal(VideoManagerSection.NoMatches, VideoOverrideUx.RootSection(9, "zzz", 0));
        // Clearing the box restores the resting root.
        Assert.Equal(VideoManagerSection.Recent, VideoOverrideUx.RootSection(9, "", 0));
    }

    [Fact]
    public void ShowsBrowseAll_WheneverSomethingIsAttachedAndNoQueryIsLive()
    {
        // Even a single attachment keeps the drill: the FULL action set lives in the leaf, not on the compact rows.
        Assert.True(VideoOverrideUx.ShowsBrowseAll(1, null));
        Assert.True(VideoOverrideUx.ShowsBrowseAll(50, "  "));
        Assert.False(VideoOverrideUx.ShowsBrowseAll(0, null));      // nothing to browse
        Assert.False(VideoOverrideUx.ShowsBrowseAll(50, "mid"));    // the search already spans everything
    }

    [Fact]
    public void ManagerHelpers_ComposeOnARealRoster_FromTheServiceThroughRecentAndSearch()
    {
        var store = new InMemoryStore();
        var svc = new VideoOverrideService(store) { FileExists = _ => true };
        foreach (var (uri, path) in new[]
                 {
                     ("spotify:track:a", @"C:\v\alpha.mp4"),
                     ("spotify:track:b", @"C:\v\beta.mp4"),
                     ("spotify:track:c", @"C:\v\gamma.mp4"),
                 })
            svc.Attach(uri, path);
        store.UpsertVideoOverride(store.GetVideoOverride("spotify:track:a")!.Value with { AddedAtUnix = 1 });
        store.UpsertVideoOverride(store.GetVideoOverride("spotify:track:b")!.Value with { AddedAtUnix = 2 });
        store.UpsertVideoOverride(store.GetVideoOverride("spotify:track:c")!.Value with { AddedAtUnix = 3 });
        svc.Reload();

        var roster = VideoOverrideUx.BuildRoster(svc, AllDirs);

        Assert.Equal(new[] { "spotify:track:c", "spotify:track:b" },
            VideoOverrideUx.RecentlyAdded(roster, 2).Select(r => r.Uri));
        // The uri IS the title fallback here, so a uri fragment is a legitimate query.
        Assert.Equal(VideoManagerSection.Results,
            VideoOverrideUx.RootSection(roster.Count, "beta", VideoOverrideUx.Search(roster, "beta").Count));
        Assert.Equal("spotify:track:b", VideoOverrideUx.Search(roster, "beta.mp4").Single().Uri);
    }

    // ── the undo contract (what the toasts' Undo restores) ───────────────────────────────────────────────────────────

    [Fact]
    public void Undo_OfAReplace_RestoresThePreviousFile_NotTheAbsenceOfOne()
    {
        var svc = Svc(("spotify:track:a", @"C:\v\first.mp4"));
        svc.TryGetActive("spotify:track:a", out var previous);   // the snapshot the action takes BEFORE mutating

        svc.Attach("spotify:track:a", @"C:\v\second.mp4");
        Assert.EndsWith("second.mp4", svc.Decide("spotify:track:a").Override.Path, StringComparison.OrdinalIgnoreCase);

        svc.Attach("spotify:track:a", previous.Path);            // the Undo
        Assert.EndsWith("first.mp4", svc.Decide("spotify:track:a").Override.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Single(svc.All());                                 // the uri is the primary key — never two rows
    }

    [Fact]
    public void Undo_OfAFirstAttach_DetachesAgain_AndUndoOfARemoveReAttaches()
    {
        var svc = Svc();
        svc.Attach("spotify:track:a", @"C:\v\a.mp4");
        Assert.True(svc.Has("spotify:track:a"));

        svc.Remove("spotify:track:a");                            // the Undo of a first attach (no previous path)
        Assert.False(svc.Has("spotify:track:a"));
        Assert.Empty(svc.All());

        svc.Attach("spotify:track:a", @"C:\v\a.mp4");             // the Undo of a remove
        Assert.True(svc.Has("spotify:track:a"));
    }

    [Fact]
    public void Undo_AfterAQuarantine_ReArmsTheFile_SoARepairIsNotSwallowed()
    {
        var svc = Svc(("spotify:track:a", @"C:\v\a.mp4"));
        svc.TryGetActive("spotify:track:a", out var o);
        svc.Quarantine("spotify:track:a", o.SourceKey);
        Assert.Equal(VideoOverrideTier.Quarantined, svc.Decide("spotify:track:a").Tier);

        svc.Attach("spotify:track:a", @"C:\v\a.mp4");             // re-picking the same path IS the repair gesture
        Assert.Equal(VideoOverrideTier.UseOverride, svc.Decide("spotify:track:a").Tier);
    }

    // ── the row indicator / "Videos only" predicate ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Indicator_AnswersFromTheAssociationPlaneAndTheOverrides_AndFromNowhereElse()
    {
        var plain = new Track("a", "spotify:track:a", "A", Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);
        var official = plain with { Uri = "spotify:track:o" };
        var store = new Wavee.Backend.InMemoryStore();
        // Spotify's own verdict lives in the association plane, keyed by uri — there is no row field to set.
        store.UpsertVideoAssociation(new VideoAssociation("spotify:track:o", true, null,
            VideoAssociation.NoFiles, null, DateTimeOffset.UtcNow, 0));
        var svc = Svc(("spotify:track:a", @"C:\v\a.mp4"));
        try
        {
            VideoPresence.Attach(svc, store);

            Assert.True(VideoPresence.HasVideo(plain));       // override-only → the row indicator + "Videos only" filter
            Assert.True(VideoPresence.HasVideo(official));    // the source's own video, straight from the plane
            Assert.True(VideoPresence.HasOverride("spotify:track:a"));
            Assert.False(VideoPresence.HasOverride("spotify:track:o"));

            svc.Remove("spotify:track:a");
            Assert.False(VideoPresence.HasVideo(plain));      // and it goes dark again on a detach
            Assert.True(VideoPresence.HasVideo(official));    // …while the association is untouched by that
        }
        finally { VideoPresence.Attach(null); }
    }

    [Fact]
    public void Indicator_IsInertWithNoCurationAttached()
    {
        VideoPresence.Attach(null);
        var t = new Track("a", "spotify:track:a", "A", Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);
        Assert.False(VideoPresence.HasVideo(t));
        Assert.False(VideoPresence.HasOverride("spotify:track:a"));
        Assert.False(VideoPresence.HasOverride(null));
        Assert.Null(VideoPresence.Service);
    }

    // ── loc coverage ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LocKeys_AreGenerated_AndDistinct()
    {
        string[] keys =
        [
            Strings.VideoOverride.MenuTitle, Strings.VideoOverride.Attach, Strings.VideoOverride.Replace,
            Strings.VideoOverride.Locate, Strings.VideoOverride.Remove, Strings.VideoOverride.ShowInExplorer,
            Strings.VideoOverride.PickTitle, Strings.VideoOverride.LocateTitle, Strings.VideoOverride.Filter,
            Strings.VideoOverride.Attached, Strings.VideoOverride.Replaced, Strings.VideoOverride.Removed,
            Strings.VideoOverride.Undo, Strings.VideoOverride.Manage, Strings.VideoOverride.Restored,
            Strings.VideoOverride.RejectedNotMp4, Strings.VideoOverride.RejectedNotFound,
            Strings.VideoOverride.MissingToast, Strings.VideoOverride.UnplayableToast,
            Strings.VideoOverride.DropHint, Strings.VideoOverride.CustomLabel,
            Strings.VideoOverride.SettingsTitle, Strings.VideoOverride.SettingsHeader, Strings.VideoOverride.SettingsSub,
            Strings.VideoOverride.SettingsEmpty, Strings.VideoOverride.SettingsEmptySub,
            Strings.VideoOverride.RecentlyAdded, Strings.VideoOverride.SearchPlaceholder,
            Strings.VideoOverride.BrowseAll, Strings.VideoOverride.NoMatches,
            Strings.VideoOverride.ClearAll, Strings.VideoOverride.ClearAllBody, Strings.VideoOverride.ClearedAll,
            Strings.VideoOverride.StatusOk, Strings.VideoOverride.StatusMissing,
            Strings.VideoOverride.StatusDriveOffline, Strings.VideoOverride.StatusUnplayable,
        ];
        Assert.All(keys, k => Assert.StartsWith("videoOverride.", k, StringComparison.Ordinal));
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.NotEmpty(Strings.VideoOverride.SettingsCount(3));   // the parameterized (plural) keys
        Assert.NotEmpty(Strings.VideoOverride.MatchCount(3));
    }

    [Fact]
    public void LocKeys_AreTranslatedInEveryShippedCulture()
    {
        string? locDir = FindLocDir();
        if (locDir is null) return;   // running outside the repo layout — nothing to assert against

        var baseKeys = ReadBlock(Path.Combine(locDir, "en-US.json"));
        Assert.NotEmpty(baseKeys);
        foreach (string culture in new[] { "nl.json", "ko-KR.json" })
        {
            var keys = ReadBlock(Path.Combine(locDir, culture));
            Assert.True(baseKeys.SetEquals(keys), culture + " is missing/extra videoOverride keys");
        }
    }

    static HashSet<string> ReadBlock(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("videoOverride", out var block))
            foreach (var p in block.EnumerateObject()) set.Add(p.Name);
        return set;
    }

    static string? FindLocDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Wavee", "assets", "loc", "en-US.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
            candidate = Path.Combine(dir.FullName, "src", "apps", "Wavee", "assets", "loc", "en-US.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
        }
        return null;
    }
}
