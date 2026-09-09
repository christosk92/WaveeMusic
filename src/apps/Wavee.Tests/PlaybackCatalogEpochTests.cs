using System;
using System.Threading.Tasks;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>Lane C regression: NowPlayingProjection used to capture the catalog epoch in its constructor, but the
/// projection is built (golive.stack) BEFORE LiveSessionHost installs the online session (Data.SetSessionAsync ->
/// CatalogRepository.SetSessionCore -> epoch++). Every later ObserveTracksAsync/ObserveOwnerTrackAsync then compared
/// against a stale epoch and threw "Inline observations belong to a previous catalog session.", which
/// LocalPlaySpecAsync swallowed — a clicked track silently did nothing. The fix reads the CURRENT
/// PlaybackQueueProjection.Epoch at call time instead.</summary>
public sealed class PlaybackCatalogEpochTests : PlaybackCatalogTestBase
{
    const string SongUri = "spotify:track:song";

    static Track TrackFor(string uri, string title) => new(
        Id: uri, Uri: uri, Title: title, Artists: Array.Empty<ArtistRef>(), Album: new AlbumRef("", "", ""),
        DurationMs: 205_000, IsExplicit: false, Image: null);

    [Fact]
    public async Task ObserveTracksAsync_SucceedsAfterTheSessionEpochAdvancesPastConstruction()
    {
        using var p = Catalog.Projection("us");

        // The session installs AFTER the projection is built — exactly the LiveConnect/LiveSessionHost ordering
        // that made a constructor-captured epoch stale by the first click.
        await Catalog.Repository.SetSessionAsync(Catalog.Repository.Scope with { ProviderAccount = "next" }, "next", online: true);

        await p.ObserveTracksAsync([TrackFor(SongUri, "Seeded Title")]);

        Assert.Equal("Seeded Title", Catalog.Queue.ReadTrack(SongUri).Title);
    }

    [Fact]
    public async Task ObserveTracksAsync_StillThrowsWhenTheQueueOwnerChanges()
    {
        using var p = Catalog.Projection("us");

        Catalog.Queue.ActivateOwner();   // a newer owner supersedes the one this projection captured at construction

        await Assert.ThrowsAsync<OperationCanceledException>(() => p.ObserveTracksAsync([TrackFor(SongUri, "x")]));
    }
}
