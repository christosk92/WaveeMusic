using System;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Playlists;
using Wavee.Backend.Queries;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class CatalogProjectionCostTests
{
    [Fact]
    public void ChunkedVectorCopiesOnlyChangedChunksAndRetainsUnchangedVector()
    {
        var values = Enumerable.Range(0, 10000).Select(_ => new object()).ToArray();
        var first = ChunkedRows<object>.Project(null, values.Length, i => values[i]);
        Assert.Same(first, ChunkedRows<object>.Project(first, values.Length, i => values[i]));
        var replacement = new object();
        long start = GC.GetAllocatedBytesForCurrentThread();
        var changed = ChunkedRows<object>.Project(first, values.Length, i => i == 8000 ? replacement : values[i]);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(bytes < 12000, $"One row replacement allocated {bytes} bytes.");
        Assert.Same(values[7999], changed[7999]); Assert.Same(replacement, changed[8000]);
        Assert.Same(values[8000], first[8000]); Assert.Equal(10000, changed.Count);
    }

    [Fact]
    public async Task OneIdentityChangeRetainsOtherPlaylistOccurrencesAndMembershipOrder()
    {
        await using var host = new CatalogQueryTestHost();
        const string playlist = "spotify:playlist:cost", changed = "spotify:track:128";
        var rows = Enumerable.Range(0, 600).Select(i => new PlaylistMember("item:" + i, "spotify:track:" + i, "owner", i)).ToArray();
        var seeds = rows.Select((r, i) => new CatalogSeed(new(host.Scope, r.ItemUri, FacetKind.TrackIdentity),
            new TrackIdentityPatch(Title: FieldChange<string?>.Set("Song " + i)))).ToArray();
        await host.SeedAsync(seeds);
        await host.Data.Replicas.AdoptPlaylistAsync(new(playlist, PlaylistReadKind.Snapshot, null, new byte[24], [..rows], [], null));
        using var query = host.Data.Queries.Acquire(new PlaylistDetailQuery(host.Scope, playlist));
        await QueryPublication.Initial(query);
        var before = query.Current;
        await host.AcceptAsync(new(host.Scope, changed, FacetKind.TrackIdentity), new TrackIdentityValue("Corrected"));
        await QueryPublication.Until(() => query.Current.Value.Tracks![128].Title == "Corrected");
        var after = query.Current;
        Assert.Equal(before.OrderRevision, after.OrderRevision);
        Assert.Equal("Corrected", after.Value.Tracks![128].Title);
        Assert.Equal("Song 128", before.Value.Tracks![128].Title);
        for (int i = 0; i < rows.Length; i++) if (i != 128) Assert.Same(before.Value.Tracks[i], after.Value.Tracks[i]);
        Assert.Equal("item:128", after.Value.Tracks[128].ContextUid);
    }

    [Fact]
    public async Task FailedNativeSaveDoesNotPublishOrChangeTheSavedSet()
    {
        var source = new LocalMutationSource(["spotify:track:old"], _ => throw new InvalidOperationException("Disk full"));
        int notifications = 0;
        using var subscription = source.SavedChanged.Subscribe(Observers.From<System.Collections.Generic.IReadOnlySet<string>>(_ => notifications++));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.SetSavedAsync("spotify:track:new", true));
        Assert.True(source.IsSaved("spotify:track:old")); Assert.False(source.IsSaved("spotify:track:new"));
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task ConcurrentNativeSavesRetainEveryCommittedEdit()
    {
        System.Collections.Generic.IReadOnlySet<string>? persisted = null;
        var source = new LocalMutationSource(persist: rows => persisted = rows);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => source.SetSavedAsync("spotify:track:" + i, true)));
        Assert.Equal(100, source.Saved.Count); Assert.Equal(100, persisted!.Count);
    }
}
