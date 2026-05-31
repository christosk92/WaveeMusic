using FluentAssertions;
using Moq;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.Services.Playlists;

namespace Wavee.UI.Tests.Services;

public sealed class RefreshPlaylistSessionTests
{
    private static PlaylistTrackDto Track(string uri, string title = "t") => new()
    {
        Id = uri.Replace("spotify:track:", ""),
        Uri = uri,
        Title = title,
        ArtistName = "artist",
        ArtistId = "ar",
        AlbumName = "album",
        AlbumId = "al",
        Duration = TimeSpan.FromMinutes(3),
        OriginalIndex = 1,
    };

    private static (RefreshPlaylistSession s, Mock<IPlaylistMutationService> mut) Start(params string[] uris)
    {
        var mut = new Mock<IPlaylistMutationService>();
        var tracks = uris.Select((u, i) => Track(u, $"t{i}")).ToList();
        return (RefreshPlaylistSession.Start("pl1", tracks, "rev0", mut.Object), mut);
    }

    [Fact]
    public void Dedupes_deck_by_uri_preserving_first_order()
    {
        var (s, _) = Start("spotify:track:a", "spotify:track:b", "spotify:track:a");
        s.Deck.Select(c => c.Uri).Should().Equal("spotify:track:a", "spotify:track:b");
        s.Phase.Should().Be(RefreshPhase.Auditioning);
    }

    [Fact]
    public void Empty_playlist_starts_in_Empty_phase()
    {
        var (s, _) = Start();
        s.Phase.Should().Be(RefreshPhase.Empty);
        s.CurrentCard.Should().BeNull();
    }

    [Fact]
    public void Decide_advances_records_decisions_and_reaches_review()
    {
        var (s, _) = Start("spotify:track:a", "spotify:track:b");
        s.CurrentCard!.Uri.Should().Be("spotify:track:a");
        s.Decide(SwipeDirection.Right);                 // keep a
        s.CurrentCard!.Uri.Should().Be("spotify:track:b");
        s.Decide(SwipeDirection.Left);                  // remove b
        s.Phase.Should().Be(RefreshPhase.Review);
        s.RemovedCount.Should().Be(1);
        s.KeptCount.Should().Be(1);
        s.RemovedCards.Single().Uri.Should().Be("spotify:track:b");
    }

    [Fact]
    public void Skip_advances_without_removing_and_track_is_kept()
    {
        var (s, _) = Start("spotify:track:a", "spotify:track:b");
        s.Skip();                                       // a deferred
        s.Decide(SwipeDirection.Left);                  // remove b
        s.Phase.Should().Be(RefreshPhase.Review);
        s.RemovedCount.Should().Be(1);
        s.KeptCount.Should().Be(1);                     // skipped 'a' counts as kept
        s.RemovedCards.Single().Uri.Should().Be("spotify:track:b");
    }

    [Fact]
    public void UndoLast_reverts_a_decision()
    {
        var (s, _) = Start("spotify:track:a", "spotify:track:b");
        s.Decide(SwipeDirection.Left);                  // remove a
        s.UndoLast().Should().BeTrue();
        s.CurrentCard!.Uri.Should().Be("spotify:track:a");
        s.RemovedCount.Should().Be(0);
    }

    [Fact]
    public void UndoLast_from_review_returns_to_auditioning()
    {
        var (s, _) = Start("spotify:track:a");
        s.Decide(SwipeDirection.Left);
        s.Phase.Should().Be(RefreshPhase.Review);
        s.UndoLast();
        s.Phase.Should().Be(RefreshPhase.Auditioning);
        s.CurrentCard!.Uri.Should().Be("spotify:track:a");
    }

    [Fact]
    public void UnRemove_in_review_flips_removed_to_kept()
    {
        var (s, _) = Start("spotify:track:a", "spotify:track:b");
        s.Decide(SwipeDirection.Left);                  // remove a
        s.Decide(SwipeDirection.Left);                  // remove b
        s.RemovedCount.Should().Be(2);
        s.UnRemove("spotify:track:a");
        s.RemovedCount.Should().Be(1);
        s.KeptCount.Should().Be(1);
        s.RemovedCards.Single().Uri.Should().Be("spotify:track:b");
    }

    [Fact]
    public async Task ApplyAsync_calls_remove_once_with_removed_uris_in_deck_order()
    {
        var (s, mut) = Start("spotify:track:a", "spotify:track:b", "spotify:track:c");
        s.Decide(SwipeDirection.Left);                  // remove a
        s.Decide(SwipeDirection.Right);                 // keep b
        s.Decide(SwipeDirection.Left);                  // remove c

        var result = await s.ApplyAsync();

        result.Success.Should().BeTrue();
        result.RemovedCount.Should().Be(2);
        s.Phase.Should().Be(RefreshPhase.Done);
        mut.Verify(m => m.RemoveTracksFromPlaylistAsync(
            "pl1",
            It.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "spotify:track:a", "spotify:track:c" })),
            It.IsAny<CancellationToken>()), Times.Once);
        mut.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ApplyAsync_with_no_removals_skips_the_network_call()
    {
        var (s, mut) = Start("spotify:track:a");
        s.Decide(SwipeDirection.Right);                 // keep
        var result = await s.ApplyAsync();
        result.Success.Should().BeTrue();
        result.RemovedCount.Should().Be(0);
        s.Phase.Should().Be(RefreshPhase.Done);
        mut.Verify(m => m.RemoveTracksFromPlaylistAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Nothing_mutates_before_apply()
    {
        var (s, mut) = Start("spotify:track:a", "spotify:track:b");
        s.Decide(SwipeDirection.Left);
        s.Decide(SwipeDirection.Left);
        mut.VerifyNoOtherCalls();
    }

    [Fact]
    public void Restart_clears_decisions_and_returns_to_first_card()
    {
        var (s, _) = Start("spotify:track:a", "spotify:track:b");
        s.Decide(SwipeDirection.Left);
        s.Decide(SwipeDirection.Left);
        s.Restart();
        s.Phase.Should().Be(RefreshPhase.Auditioning);
        s.CurrentIndex.Should().Be(0);
        s.RemovedCount.Should().Be(0);
        s.CurrentCard!.Uri.Should().Be("spotify:track:a");
    }

    [Fact]
    public void StateChanged_fires_on_decide()
    {
        var (s, _) = Start("spotify:track:a", "spotify:track:b");
        var fired = 0;
        s.StateChanged += (_, _) => fired++;
        s.Decide(SwipeDirection.Right);
        fired.Should().Be(1);
    }

    // ── Resume / reconcile ──

    private static RefreshSessionState Saved(string[] snapshotUris, params (string uri, SwipeDecision d)[] decisions)
        => new("pl1", "rev0", snapshotUris, decisions.ToDictionary(x => x.uri, x => x.d));

    [Fact]
    public void Resume_preserves_decisions_by_uri_across_reorder()
    {
        var mut = new Mock<IPlaylistMutationService>();
        var saved = Saved(new[] { "spotify:track:a", "spotify:track:b", "spotify:track:c" },
            ("spotify:track:a", SwipeDecision.Remove), ("spotify:track:b", SwipeDecision.Keep));
        // reordered upstream: c, a, b — same set
        var current = new[] { "spotify:track:c", "spotify:track:a", "spotify:track:b" }.Select(u => Track(u)).ToList();

        var s = RefreshPlaylistSession.Resume(current, "rev1", saved, mut.Object);

        s.RemovedCards.Single().Uri.Should().Be("spotify:track:a");   // remove survived
        s.RemovedCount.Should().Be(1);
        s.LastDiff.HasChanges.Should().BeFalse();                     // same set
        s.CurrentCard!.Uri.Should().Be("spotify:track:c");            // only undecided card
    }

    [Fact]
    public void Resume_drops_decisions_for_tracks_removed_upstream()
    {
        var mut = new Mock<IPlaylistMutationService>();
        var saved = Saved(new[] { "spotify:track:a", "spotify:track:b" },
            ("spotify:track:a", SwipeDecision.Remove), ("spotify:track:b", SwipeDecision.Keep));
        var current = new[] { "spotify:track:b" }.Select(u => Track(u)).ToList();   // a removed upstream

        var s = RefreshPlaylistSession.Resume(current, "rev1", saved, mut.Object);

        s.Deck.Select(c => c.Uri).Should().Equal("spotify:track:b");
        s.RemovedCount.Should().Be(0);                                // a's removal dropped
        s.LastDiff.Removed.Should().Be(1);
        s.LastDiff.Added.Should().Be(0);
    }

    [Fact]
    public void Resume_auditions_newly_added_tracks_and_skips_decided_ones()
    {
        var mut = new Mock<IPlaylistMutationService>();
        var saved = Saved(new[] { "spotify:track:a", "spotify:track:b" },
            ("spotify:track:a", SwipeDecision.Remove), ("spotify:track:b", SwipeDecision.Keep));
        var current = new[] { "spotify:track:a", "spotify:track:b", "spotify:track:c" }.Select(u => Track(u)).ToList();

        var s = RefreshPlaylistSession.Resume(current, "rev1", saved, mut.Object);

        s.LastDiff.Added.Should().Be(1);                              // c added
        s.LastDiff.Removed.Should().Be(0);
        s.CurrentCard!.Uri.Should().Be("spotify:track:c");            // jumps past decided a & b
        s.RemainingCount.Should().Be(1);
    }

    [Fact]
    public void Snapshot_round_trips_uris_and_decisions()
    {
        var (s, _) = Start("spotify:track:a", "spotify:track:b");
        s.Decide(SwipeDirection.Left);                                // remove a
        var snap = s.Snapshot();
        snap.PlaylistId.Should().Be("pl1");
        snap.BaseRevision.Should().Be("rev0");
        snap.SnapshotUris.Should().Equal("spotify:track:a", "spotify:track:b");
        snap.Decisions.Should().ContainKey("spotify:track:a").WhoseValue.Should().Be(SwipeDecision.Remove);
        snap.Decisions.Should().NotContainKey("spotify:track:b");    // undecided
    }
}
