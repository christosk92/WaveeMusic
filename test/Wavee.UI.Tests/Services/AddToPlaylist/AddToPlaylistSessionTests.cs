using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wavee.UI.Services.AddToPlaylist;

namespace Wavee.UI.Tests.Services.AddToPlaylist;

public sealed class AddToPlaylistSessionTests
{
    private static AddToPlaylistSession CreateSession(out Mock<IAddToPlaylistSubmitter> submitter)
    {
        submitter = new Mock<IAddToPlaylistSubmitter>();
        return new AddToPlaylistSession(submitter.Object);
    }

    private static PendingTrackEntry Track(string uri, string title = "Song")
        => new(uri, title, "Artist", "spotify:image:abc", TimeSpan.FromSeconds(180));

    [Fact]
    public void Begin_SetsTarget_AndMarksActive()
    {
        var session = CreateSession(out _);

        session.Begin("spotify:playlist:abc", "My Playlist", "spotify:image:cover");

        session.IsActive.Should().BeTrue();
        session.TargetPlaylistId.Should().Be("spotify:playlist:abc");
        session.TargetPlaylistName.Should().Be("My Playlist");
        session.TargetPlaylistImageUrl.Should().Be("spotify:image:cover");
        session.Pending.Should().BeEmpty();
    }

    [Fact]
    public void Begin_TwiceReplacesTarget_AndClearsPending()
    {
        var session = CreateSession(out _);
        session.Begin("spotify:playlist:a", "A", null);
        session.Toggle(Track("spotify:track:1"));
        session.PendingCount.Should().Be(1);

        session.Begin("spotify:playlist:b", "B", null);

        session.TargetPlaylistId.Should().Be("spotify:playlist:b");
        session.Pending.Should().BeEmpty();
        session.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Toggle_AddsIfMissing_RemovesIfPresent()
    {
        var session = CreateSession(out _);
        session.Begin("p", "P", null);
        var t = Track("spotify:track:1");

        session.Toggle(t);
        session.Contains("spotify:track:1").Should().BeTrue();
        session.PendingCount.Should().Be(1);

        session.Toggle(t);
        session.Contains("spotify:track:1").Should().BeFalse();
        session.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Toggle_Ignored_WhenSessionInactive()
    {
        var session = CreateSession(out _);

        session.Toggle(Track("spotify:track:1"));

        session.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Toggle_NullOrEmptyUri_IsNoop()
    {
        var session = CreateSession(out _);
        session.Begin("p", "P", null);

        session.Toggle(null!);
        session.Toggle(Track(""));

        session.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Toggle_SameUri_DeDupes()
    {
        var session = CreateSession(out _);
        session.Begin("p", "P", null);
        // Two entries with same URI but different titles — Toggle treats them as the same track.
        session.Toggle(Track("spotify:track:1", "Song A"));
        session.Toggle(Track("spotify:track:1", "Song A Remastered"));

        session.PendingCount.Should().Be(0); // second toggle removed it
    }

    [Fact]
    public void Cancel_ClearsState()
    {
        var session = CreateSession(out _);
        session.Begin("p", "P", null);
        session.Toggle(Track("spotify:track:1"));

        session.Cancel();

        session.IsActive.Should().BeFalse();
        session.TargetPlaylistId.Should().BeNull();
        session.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task SubmitAsync_NoPending_ReturnsZero_AndDoesNotCallSubmitter()
    {
        var session = CreateSession(out var submitter);
        session.Begin("p", "P", null);

        var n = await session.SubmitAsync();

        n.Should().Be(0);
        submitter.Verify(
            s => s.SubmitAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Empty submit doesn't end the session — user can keep shopping.
        session.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SubmitAsync_PostsUrisInInsertionOrder_AndEndsSession()
    {
        var session = CreateSession(out var submitter);
        session.Begin("spotify:playlist:x", "X", null);
        session.Toggle(Track("spotify:track:1"));
        session.Toggle(Track("spotify:track:2"));
        session.Toggle(Track("spotify:track:3"));

        IReadOnlyList<string>? capturedUris = null;
        string? capturedPlaylist = null;
        submitter
            .Setup(s => s.SubmitAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, CancellationToken>((pid, uris, _) =>
            {
                capturedPlaylist = pid;
                capturedUris = uris;
            })
            .Returns(Task.CompletedTask);

        var n = await session.SubmitAsync();

        n.Should().Be(3);
        capturedPlaylist.Should().Be("spotify:playlist:x");
        capturedUris.Should().Equal("spotify:track:1", "spotify:track:2", "spotify:track:3");
        session.IsActive.Should().BeFalse();
        session.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task SubmitAsync_OnFailure_LeavesSessionOpenForRetry()
    {
        var session = CreateSession(out var submitter);
        session.Begin("p", "P", null);
        session.Toggle(Track("spotify:track:1"));

        submitter
            .Setup(s => s.SubmitAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = async () => await session.SubmitAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        session.IsActive.Should().BeTrue();
        session.PendingCount.Should().Be(1);
    }

    [Fact]
    public void Contains_ReturnsFalseForNullOrEmpty()
    {
        var session = CreateSession(out _);
        session.Begin("p", "P", null);
        session.Toggle(Track("spotify:track:1"));

        session.Contains("").Should().BeFalse();
        session.Contains(null!).Should().BeFalse();
        session.Contains("spotify:track:2").Should().BeFalse();
        session.Contains("spotify:track:1").Should().BeTrue();
    }

    [Fact]
    public void Begin_FiresPropertyChanged_ForIsActiveAndTargets()
    {
        var session = CreateSession(out _);
        var changed = new List<string?>();
        session.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        session.Begin("p", "P", "img");

        changed.Should().Contain(nameof(IAddToPlaylistSession.IsActive));
        changed.Should().Contain(nameof(IAddToPlaylistSession.TargetPlaylistId));
        changed.Should().Contain(nameof(IAddToPlaylistSession.TargetPlaylistName));
    }

    [Fact]
    public void Toggle_FiresPendingCountChange()
    {
        var session = CreateSession(out _);
        session.Begin("p", "P", null);
        var changed = new List<string?>();
        session.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        session.Toggle(Track("spotify:track:1"));

        changed.Should().Contain(nameof(IAddToPlaylistSession.PendingCount));
    }
}
