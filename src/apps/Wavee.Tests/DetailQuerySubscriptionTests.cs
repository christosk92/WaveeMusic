using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class DetailQuerySubscriptionTests
{
    const string PlaylistUri = "spotify:playlist:owners";
    const string TrackUri = "spotify:track:one";
    static async Task Seed(CatalogQueryTestHost host)
    {
        await host.Data.Replicas.AdoptPlaylistAsync(new(PlaylistUri, PlaylistReadKind.Snapshot, null, new byte[24],
            [new("first", TrackUri, "spotify:user:DAVE", 1000), new("second", TrackUri, "erin", 2000)], [], null));
        await host.AcceptAsync(new(host.Scope, PlaylistUri, FacetKind.PlaylistHeader),
            new PlaylistHeaderValue(Name: "Mix", OwnerUri: "spotify:user:BOB")
            { CollaboratorUris = ["spotify:user:Carol", "bob", "Bob The Builder", "a:b"] });
        await host.AcceptAsync(new(host.Scope, TrackUri, FacetKind.TrackIdentity), new TrackIdentityValue("Song"));
    }

    [Fact]
    public async Task DemandIncludesHeaderContributorsAndEveryAddedByOwner()
    {
        var provider = new QueryTestProvider(request => new(request,
            request.Key.Facet == FacetKind.UserIdentity
                ? ResourceFetchResult.Present(new ReplaceFacetPatch(new UserIdentityValue("Name:" + request.Key.Subject)))
                : ResourceFetchResult.Absent()));
        await using var host = new CatalogQueryTestHost(provider);
        await Seed(host);
        var result = await host.Data.Queries.ReadOnceAsync(new PlaylistDetailQuery(host.Scope, PlaylistUri),
            QueryDemand.Initial, TestContext.Current.CancellationToken);
        var owners = provider.Requests.Where(key => key.Facet == FacetKind.UserIdentity).Select(key => key.Subject).ToHashSet();
        Assert.Equal(new[] { "spotify:user:bob", "spotify:user:carol", "spotify:user:dave", "spotify:user:erin" }.ToHashSet(), owners);
        Assert.Equal("Name:spotify:user:bob", result.Value.OwnerName);
        Assert.Contains(result.Value.Collaborators!, owner => owner.Id == "dave" && owner.Name == "Name:spotify:user:dave");
        Assert.DoesNotContain(result.Value.Collaborators!, owner => owner.Id.Contains(' ') || owner.Id.Contains(':'));
        Assert.Equal(new[] { "first", "second" }, result.Value.Tracks!.Select(row => row.ContextUid));
    }

    [Fact]
    public async Task OnlyReferencedOwnerChangesReproject_AndOwnerIdsAreNormalized()
    {
        await using var host = new CatalogQueryTestHost();
        await Seed(host);
        using var detail = host.Data.Queries.Acquire(new PlaylistDetailQuery(host.Scope, PlaylistUri));
        await QueryPublication.Until(() => detail.Current.Value.Tracks?.Count == 2 && !detail.Current.Status.IsRefreshing);
        long before = detail.Current.Revision;
        await host.AcceptAsync(new(host.Scope, "spotify:user:stranger", FacetKind.UserIdentity), new UserIdentityValue("Stranger"));
        Assert.Equal(before, detail.Current.Revision);
        await host.AcceptAsync(new(host.Scope, "spotify:user:bob", FacetKind.UserIdentity), new UserIdentityValue("Robert"));
        await QueryPublication.Until(() => detail.Current.Value.OwnerName == "Robert");
        Assert.Equal("Robert", detail.Current.Value.OwnerName);
        Assert.Equal("bob", detail.Current.Value.Owner!.Id);
        Assert.Single(detail.Current.Value.Collaborators!.Where(owner => owner.Id == "bob"));
        await host.AcceptAsync(new(host.Scope, "spotify:user:dave", FacetKind.UserIdentity), new UserIdentityValue("David"));
        await QueryPublication.Until(() => detail.Current.Value.Collaborators?.Any(owner => owner.Id == "dave" && owner.Name == "David") == true);
        Assert.Contains(detail.Current.Value.Collaborators!, owner => owner.Id == "dave" && owner.Name == "David");
    }

    [Fact]
    public async Task MetadataBurstsCoalesceIntoOneUiPublication_WithoutStartingAnotherLoad()
    {
        var provider = new QueryTestProvider(request => new(request, ResourceFetchResult.Absent()));
        await using var host = new CatalogQueryTestHost(provider);
        await Seed(host);
        var posts = new ConcurrentQueue<Action>();
        var titles = new List<string>();
        using var handle = host.Data.Queries.Acquire(new PlaylistDetailQuery(host.Scope, PlaylistUri));
        using var binding = new QuerySignalBinding<Playlist>(handle,
            posts.Enqueue, snapshot => titles.Add(snapshot.Value.Tracks![0].Title));
        binding.SetDemand(QueryDemand.None); binding.SetActive(true);
        await QueryPublication.Until(() => handle.Current.Value.Tracks?.Count == 2 && !handle.Current.Status.IsRefreshing);
        await QueryPublication.Until(() => { while (posts.TryDequeue(out var post)) post(); return titles.Count > 0; });
        while (posts.TryDequeue(out var initial)) initial();
        titles.Clear();
        for (int i = 0; i < 20; i++)
            await host.AcceptAsync(new(host.Scope, TrackUri, FacetKind.TrackIdentity), new TrackIdentityValue("Song " + i));
        await QueryPublication.Until(() => handle.Current.Value.Tracks?[0].Title == "Song 19");
        await QueryPublication.Until(() => !posts.IsEmpty);
        Assert.Single(posts);
        Assert.Empty(titles);
        Assert.Empty(provider.Requests);
        Assert.True(posts.TryDequeue(out var publish)); publish!();
        Assert.Equal(new[] { "Song 19" }, titles);
        await host.AcceptAsync(new(host.Scope, TrackUri, FacetKind.TrackIdentity), new TrackIdentityValue("Later"));
        await QueryPublication.Until(() => handle.Current.Value.Tracks?[0].Title == "Later" && !posts.IsEmpty);
        Assert.True(posts.TryDequeue(out publish)); publish!();
        Assert.Equal(new[] { "Song 19", "Later" }, titles);
    }
}
