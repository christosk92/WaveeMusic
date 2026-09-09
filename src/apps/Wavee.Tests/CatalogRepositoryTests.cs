using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Persistence;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class CatalogRepositoryTests
{
    [Fact]
    public async Task EndingAnOwnedSession_FencesLateResponsesAndPreservesOfflineFacts()
    {
        await using var fixture = new CatalogFixture();
        var key = fixture.Key();
        await fixture.AcceptCountAsync(key, 8);
        var request = await fixture.Repository.CaptureRequestAsync(key, ResourcePriority.Visible);
        long epoch = fixture.Repository.Epoch;
        Assert.False(await fixture.Repository.SetOfflineAsync(epoch - 1));
        Assert.Equal(epoch, fixture.Repository.Epoch);
        Assert.True(await fixture.Repository.SetOfflineAsync(epoch));
        Assert.False(fixture.Repository.IsOnline);
        Assert.Equal(8, Assert.IsType<PlayCountValue>(fixture.Repository.Peek(key).Value).Count);
        Assert.Equal(ResourceActivity.Offline, fixture.Repository.Peek(key).Activity);
        var late = Assert.Single(await fixture.Repository.AcceptAsync([new(request,
            ResourceFetchResult.Present(new ReplaceFacetPatch(new PlayCountValue(100))))]));
        Assert.Equal(ResourceEnsureStatus.Superseded, late.Status);
        Assert.Equal(8, Assert.IsType<PlayCountValue>(fixture.Persistence.Records[key].Value).Count);
        Assert.False(await fixture.Repository.SetOfflineAsync(epoch));
    }

    [Fact]
    public async Task Acceptance_PersistsBeforeSinglePrecisePublicationIncludingSeeds()
    {
        await using var fixture = new CatalogFixture();
        var parent = fixture.Key("track", FacetKind.TrackIdentity);
        var artist = fixture.Key("artist", FacetKind.ArtistIdentity);
        var request = await fixture.Repository.CaptureRequestAsync(parent, ResourcePriority.Visible);
        var changes = new List<CatalogChangeSet>();
        using var subscription = fixture.Repository.Changes.Subscribe(new CatalogObserver(change =>
        {
            Assert.True(fixture.Persistence.Records.ContainsKey(parent));
            Assert.True(fixture.Persistence.Records.ContainsKey(artist));
            changes.Add(change);
        }));
        await fixture.Repository.AcceptAsync([new(request, ResourceFetchResult.Present(new TrackIdentityPatch(
            Title: FieldChange<string?>.Set("Song"))), [new(artist, new ReplaceFacetPatch(new ArtistIdentityValue("Artist")))])]);
        var change = Assert.Single(changes);
        Assert.Equal(new[] { parent, artist }.ToHashSet(), change.Keys.ToHashSet());
        var seeded = fixture.Repository.Peek(artist);
        Assert.Equal(CatalogProvenance.InlineSeed, seeded.Provenance);
        Assert.False(seeded.IsFresh(fixture.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task SeedWithoutATitle_LeavesAnUnknownEntryUnknown_ButAppliesOnceItIsAlreadyPresent()
    {
        await using var fixture = new CatalogFixture();
        var parent = fixture.Key("track", FacetKind.TrackIdentity);
        var nameless = fixture.Key("gidonly", FacetKind.TrackIdentity);

        // A gid-only reference (a LeanAlbum disc track, an ArtistRef) seeds a patch with no readiness field — the
        // Unknown entry must stay Unknown so the UI reads "never asked" (IsFresh already requires Provider
        // provenance, so the normal fetch still proceeds unaffected).
        var first = await fixture.Repository.CaptureRequestAsync(parent, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(first, ResourceFetchResult.Present(new TrackIdentityPatch(
            Title: FieldChange<string?>.Set("Song"))),
            [new(nameless, new TrackIdentityPatch(DurationMs: FieldChange<long?>.Set(210_000)))])]);
        Assert.Equal(Knowledge.Unknown, fixture.Repository.Peek(nameless).Knowledge);

        // A titled seed DOES promote it …
        var second = await fixture.Repository.CaptureRequestAsync(parent, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(second, ResourceFetchResult.Present(new TrackIdentityPatch(
            Title: FieldChange<string?>.Set("Song 2"))),
            [new(nameless, new TrackIdentityPatch(Title: FieldChange<string?>.Set("Nameless now named")))])]);
        var named = fixture.Repository.Peek(nameless);
        Assert.Equal(Knowledge.Present, named.Knowledge);
        Assert.Equal("Nameless now named", Assert.IsType<TrackIdentityValue>(named.Value).Title);

        // … and once Present, a seed that carries no readiness field still applies its OTHER fields.
        var third = await fixture.Repository.CaptureRequestAsync(parent, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(third, ResourceFetchResult.Present(new TrackIdentityPatch(
            Title: FieldChange<string?>.Set("Song 3"))),
            [new(nameless, new TrackIdentityPatch(DurationMs: FieldChange<long?>.Set(180_000)))])]);
        var updated = fixture.Repository.Peek(nameless);
        Assert.Equal("Nameless now named", Assert.IsType<TrackIdentityValue>(updated.Value).Title);
        Assert.Equal(180_000, Assert.IsType<TrackIdentityValue>(updated.Value).DurationMs);
    }

    [Fact]
    public async Task SeedManyAsync_IsNotReadinessGated_UnlikeAFetchResponsesChildSeed()
    {
        await using var fixture = new CatalogFixture();
        var key = fixture.Key("track", FacetKind.TrackIdentity);
        // SeedManyAsync (PlaybackQueueProjection.SeedTracksAsync's path — a track the app is directly playing or
        // queueing) is the app's own directly observed data, never a decoder's unconfirmed child reference —
        // CatalogDomainSeeds.Track's Text() helper deliberately leaves Title unspecified when it merely equals
        // the track's own Uri (no real name resolved yet), and that must not block every OTHER field (duration,
        // …) from ever becoming readable: Knowledge staying Unknown makes CatalogReadView.Fact return null for
        // the WHOLE value, not just the name. Regression: this used to return 0 instead of 200_000.
        await fixture.Repository.SeedManyAsync(
            [new(key, new TrackIdentityPatch(DurationMs: FieldChange<long?>.Set(200_000)))], fixture.Repository.Epoch);
        var seeded = fixture.Repository.Peek(key);
        Assert.Equal(Knowledge.Present, seeded.Knowledge);
        Assert.Equal(200_000, Assert.IsType<TrackIdentityValue>(seeded.Value).DurationMs);
    }

    [Fact]
    public async Task FailedPersistence_PublishesNothingAndPreservesLastGoodValue()
    {
        await using var fixture = new CatalogFixture();
        var key = fixture.Key();
        await fixture.AcceptCountAsync(key, 8);
        int changes = 0;
        using var subscription = fixture.Repository.Changes.Subscribe(new CatalogObserver(_ => changes++));
        fixture.Persistence.FailCommit = true;
        var request = await fixture.Repository.CaptureRequestAsync(key, ResourcePriority.Visible);
        await Assert.ThrowsAsync<IOException>(() => fixture.Repository.AcceptAsync([new(request,
            ResourceFetchResult.Present(new ReplaceFacetPatch(new PlayCountValue(0))))]));
        Assert.Equal(8, Assert.IsType<PlayCountValue>(fixture.Repository.Peek(key).Value).Count);
        Assert.Equal(8, Assert.IsType<PlayCountValue>(fixture.Persistence.Records[key].Value).Count);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task Invalidation_RejectsOldResponseAndItsChildSeeds()
    {
        await using var fixture = new CatalogFixture();
        var key = fixture.Key();
        var seedKey = fixture.Key("artist", FacetKind.ArtistIdentity);
        var old = await fixture.Repository.CaptureRequestAsync(key, ResourcePriority.Visible);
        await fixture.Repository.InvalidateAsync([key]);
        var result = Assert.Single(await fixture.Repository.AcceptAsync([new(old,
            ResourceFetchResult.Present(new ReplaceFacetPatch(new PlayCountValue(2))),
            [new(seedKey, new ReplaceFacetPatch(new ArtistIdentityValue("stale")))])]));
        Assert.Equal(ResourceEnsureStatus.Superseded, result.Status);
        Assert.Equal(Knowledge.Unknown, fixture.Repository.Peek(seedKey).Knowledge);
        Assert.Empty(fixture.Persistence.Records);
    }

    [Fact]
    public async Task SessionAccountEpoch_RejectsOldResponsesButAllowsExplicitOfflineLocalProvider()
    {
        await using var fixture = new CatalogFixture();
        var key = fixture.Key();
        var old = await fixture.Repository.CaptureRequestAsync(key, ResourcePriority.Visible);
        await fixture.Repository.SetSessionAsync(fixture.Scope, "account-a", online: false);
        Assert.Equal(ResourceEnsureStatus.Superseded, Assert.Single(await fixture.Repository.AcceptAsync(
            [new(old, ResourceFetchResult.Absent())])).Status);
        var local = key with { Scope = fixture.Scope with { Provider = "local" } };
        Assert.True(fixture.Repository.CanRequest(local, requiresNetwork: false));
        var request = await fixture.Repository.CaptureRequestAsync(local, ResourcePriority.Visible, requiresNetwork: false);
        Assert.Equal(ResourceEnsureStatus.Ready, Assert.Single(await fixture.Repository.AcceptAsync([new(request,
            ResourceFetchResult.Present(new ReplaceFacetPatch(new PlayCountValue(0))))])).Status);
        var otherAccount = local with { Scope = local.Scope with { ProviderAccount = "account-b" } };
        Assert.False(fixture.Repository.CanRequest(otherAccount, requiresNetwork: false));
    }

    [Fact]
    public async Task AnAcceptDuringAnInFlightColdRead_IsNeverReplacedByTheStaleColdValue()
    {
        await using var fixture = new CatalogFixture();
        var key = fixture.Key();
        fixture.Persistence.Records[key] = new(key, Knowledge.Present, new PlayCountValue(1),
            CatalogProvenance.ImportedUnknownContext, default, default, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        fixture.Persistence.BeforeRead = async _ =>
        { if (Interlocked.Increment(ref reads) == 1) { entered.TrySetResult(); await release.Task; } };
        var read = fixture.Repository.ReadAsync(key);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The cold read is parked in persistence and holds nothing: capture and accept both run to completion.
        var capture = await fixture.Repository.CaptureRequestAsync(key, ResourcePriority.Visible)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Repository.AcceptAsync([new(capture,
            ResourceFetchResult.Present(new ReplaceFacetPatch(new PlayCountValue(2))))]).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(read.IsCompleted);

        release.SetResult();
        Assert.Equal(2, Assert.IsType<PlayCountValue>((await read).Value).Count);
        Assert.Equal(2, Assert.IsType<PlayCountValue>(fixture.Repository.Peek(key).Value).Count);
    }

    [Fact]
    public async Task ASlowColdRead_DoesNotBlockAnUnrelatedAcceptance()
    {
        await using var fixture = new CatalogFixture();
        var slow = fixture.Key("slow");
        var other = fixture.Key("other");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Persistence.BeforeRead = async keys =>
        { if (keys.Contains(slow)) { entered.TrySetResult(); await release.Task; } };

        var read = fixture.Repository.ReadAsync(slow);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.AcceptCountAsync(other, 5).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(read.IsCompleted);
        Assert.Equal(5, Assert.IsType<PlayCountValue>(fixture.Repository.Peek(other).Value).Count);
        release.SetResult();
        await read;
    }

    [Fact]
    public async Task OneBatchedColdRead_FillsOnlyUnknownEntries_MarksEveryKeyLoaded_AndPublishesOnce()
    {
        await using var fixture = new CatalogFixture();
        var cold = fixture.Key("cold");
        var accepted = fixture.Key("accepted");
        var missing = fixture.Key("missing");
        fixture.Persistence.Records[cold] = new(cold, Knowledge.Present, new PlayCountValue(1),
            CatalogProvenance.ImportedUnknownContext, default, default, 3);
        fixture.Persistence.Records[accepted] = new(accepted, Knowledge.Present, new PlayCountValue(1),
            CatalogProvenance.ImportedUnknownContext, default, default, 3);
        await fixture.AcceptCountAsync(accepted, 9);
        int reads;
        lock (fixture.Persistence.Reads) reads = fixture.Persistence.Reads.Count;
        var changes = new List<CatalogChangeSet>();
        using var subscription = fixture.Repository.Changes.Subscribe(new CatalogObserver(changes.Add));

        var snapshots = await fixture.Repository.ReadManyAsync([cold, accepted, missing]);

        Assert.Equal(1, Assert.IsType<PlayCountValue>(snapshots[0].Value).Count);
        Assert.Equal(9, Assert.IsType<PlayCountValue>(snapshots[1].Value).Count);   // the accepted value survives
        Assert.Equal(Knowledge.Unknown, snapshots[2].Knowledge);
        // One round trip for the whole page, carrying only the keys that were not loaded yet.
        List<IReadOnlyList<ResourceKey>> issued;
        lock (fixture.Persistence.Reads) issued = fixture.Persistence.Reads.Skip(reads).ToList();
        Assert.Equal(new[] { cold, missing }, Assert.Single(issued).ToArray());
        // One publication for the batch, naming only the entry that actually changed.
        var change = Assert.Single(changes, set => set.Kind == CatalogChangeKind.ColdRead);
        Assert.Equal(new[] { cold }, change.Keys.ToArray());

        // A miss is loaded too: reading again asks persistence nothing.
        await fixture.Repository.ReadManyAsync([cold, accepted, missing]);
        lock (fixture.Persistence.Reads) Assert.Equal(reads + 1, fixture.Persistence.Reads.Count);
    }

    [Fact]
    public async Task Failure404403AndEmptySuccess_StayDistinct()
    {
        await using var fixture = new CatalogFixture();
        var key = fixture.Key();
        await fixture.AcceptCountAsync(key, 0);
        var request = await fixture.Repository.CaptureRequestAsync(key, ResourcePriority.Visible);
        var failed = Assert.Single(await fixture.Repository.AcceptAsync([new(request,
            ResourceFetchResult.Failed(new(ResourceErrorKind.Forbidden, "denied", 403)))]));
        Assert.Equal(ResourceEnsureStatus.Failed, failed.Status);
        Assert.Equal(Knowledge.Present, failed.Snapshot.Knowledge);
        Assert.Equal(0, Assert.IsType<PlayCountValue>(failed.Snapshot.Value).Count);
        request = await fixture.Repository.CaptureRequestAsync(key, ResourcePriority.Visible);
        var absent = Assert.Single(await fixture.Repository.AcceptAsync([new(request, ResourceFetchResult.Absent())]));
        Assert.Equal(ResourceEnsureStatus.Absent, absent.Status);
        Assert.Null(absent.Snapshot.Value);
        Assert.True(absent.Snapshot.IsFresh(fixture.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task AcceptedCommit_OwnsDurabilityDespiteCallerCancellationAndHonorsByteAdmission()
    {
        await using var queue = new DataCommitQueue(capacity: 2, byteCapacity: 8);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        int published = 0;
        var first = queue.CommitAsync(() => new DataCommit<int>(async _ =>
        { started.TrySetResult(); await release.Task; }, () => published++, 1), caller.Token, encodedBytes: 8);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = queue.ExecuteAsync(_ => ValueTask.FromResult(2), encodedBytes: 1);
        Assert.Equal(8, queue.PendingBytes);
        Assert.False(second.IsCompleted);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        Assert.Equal(2, await second);
        await queue.FlushAsync();
        Assert.Equal(1, published);
        Assert.Equal(0, queue.PendingBytes);
    }

    [Fact]
    public async Task FifteenHundredResidentIdentities_ReadWithAHandfulOfStatements()
    {
        using var db = new CatalogTestDb();
        var scope = new CatalogScope("spotify", "account-a", "en", "NL", "premium", 1, false);
        var keys = new ResourceKey[1500];
        var records = new CatalogRecord[1500];
        for (int i = 0; i < 1500; i++)
        {
            var key = new ResourceKey(scope, "spotify:track:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FacetKind.TrackIdentity);
            keys[i] = key;
            records[i] = new(key, Knowledge.Present,
                new TrackIdentityValue("T" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                CatalogProvenance.Provider, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), 1);
        }
        using (var writer = new SqliteColdStore(db.Path))
            await writer.CommitAsync(new CatalogCommit(1, records), TestContext.Current.CancellationToken);

        using var storage = new SqliteColdStore(db.Path);
        await using var commits = new DataCommitQueue();
        var repository = new CatalogRepository(commits, storage, TimeProvider.System, scope, scope.ProviderAccount);
        int before = storage.ReadStatements;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var snapshots = await repository.ReadManyAsync(keys);
        started.Stop();
        Assert.Equal(1500, snapshots.Count);
        Assert.All(snapshots, snapshot => Assert.Equal(Knowledge.Present, snapshot.Knowledge));
        int statements = storage.ReadStatements - before;
        Assert.InRange(statements, 1, 6);
        Assert.True(started.ElapsedMilliseconds < 500,
            "1500-key cold read took " + started.ElapsedMilliseconds + " ms (statements=" + statements
            + " coldMs=" + repository.LastColdReadMs + ")");
    }
}

/// <summary>The LOCK-FREE published view a frame reads (CatalogRepository.TryPeekPublished / PublishedScope). The
/// render path must never take the repository's gate: it is the same gate a whole-membership join holds for its
/// entire duration, so probing it from a rendered row put the UI thread behind a 1,494-row join.</summary>
public sealed class CatalogRenderViewTests
{
    static VideoAssociation Association(string uri, bool hasVideo)
        => new(uri, hasVideo, null, [], null, DateTimeOffset.UnixEpoch, 0);

    [Fact]
    public async Task PublishedVideoAssociationsAnswerWithoutTheGate_AndUnlistedFacetsDoNot()
    {
        await using var fixture = new CatalogFixture();
        var video = fixture.Key("track:v", FacetKind.VideoAssociation);
        var plays = fixture.Key("track:v", FacetKind.PlayCount);
        Assert.False(fixture.Repository.TryPeekPublished(video, out _));   // nothing published yet

        var request = await fixture.Repository.CaptureRequestAsync(video, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(request, ResourceFetchResult.Present(
            new ReplaceFacetPatch(new VideoAssociationValue(Association(video.Subject, true)))))]);
        Assert.True(fixture.Repository.TryPeekPublished(video, out var published));
        Assert.True(Assert.IsType<VideoAssociationValue>(published.Value).Association.HasVideo);

        // Only the facets a frame actually probes are carried; everything else keeps travelling on the query's own
        // publication and must not be answered from here.
        await fixture.AcceptCountAsync(plays, 5);
        Assert.False(fixture.Repository.TryPeekPublished(plays, out _));
    }

    [Fact]
    public async Task TheRenderViewFollowsTheSession_ScopeAndSnapshotsAlike()
    {
        await using var fixture = new CatalogFixture();
        var video = fixture.Key("track:v", FacetKind.VideoAssociation);
        var request = await fixture.Repository.CaptureRequestAsync(video, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(request, ResourceFetchResult.Present(
            new ReplaceFacetPatch(new VideoAssociationValue(Association(video.Subject, true)))))]);
        Assert.Same(fixture.Repository.Scope, fixture.Repository.PublishedScope);

        var next = fixture.Scope with { ProviderAccount = "account-b" };
        await fixture.Repository.SetSessionAsync(next, "account-b", online: true);
        // A session change republishes every key, so the lock-free view is at worst one publication behind — never
        // stale across a scope swap.
        Assert.Equal(next, fixture.Repository.PublishedScope);
        Assert.True(fixture.Repository.TryPeekPublished(video, out var carried));
        // …and it carries the session's own re-stamping: this entry's key names the account the session just left,
        // so the view answers with the Offline snapshot rather than a stale online one.
        Assert.Equal(ResourceActivity.Offline, carried.Activity);
    }
}

internal sealed class CatalogFixture : IAsyncDisposable
{
    public readonly CatalogScope Scope = new("spotify", "account-a", "en", "NL", "premium", 1, false);
    public readonly CatalogClock Clock = new();
    public readonly DataCommitQueue Commits = new();
    public readonly CatalogMemoryPersistence Persistence = new();
    public CatalogRepository Repository { get; }
    public CatalogFixture() => Repository = new(Commits, Persistence, Clock, Scope, Scope.ProviderAccount);
    public ResourceKey Key(string subject = "track", FacetKind facet = FacetKind.PlayCount)
        => new(Scope, "spotify:" + subject, facet);
    public async Task AcceptCountAsync(ResourceKey key, long count)
    {
        var request = await Repository.CaptureRequestAsync(key, ResourcePriority.Visible);
        await Repository.AcceptAsync([new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(new PlayCountValue(count))))]);
    }
    public ValueTask DisposeAsync() => Commits.DisposeAsync();
}

internal sealed class CatalogMemoryPersistence : ICatalogPersistence
{
    public readonly Dictionary<ResourceKey, CatalogRecord> Records = new();
    /// <summary>Every batch this fake was asked for, in order - the shape of the cold reads under test.</summary>
    public readonly List<IReadOnlyList<ResourceKey>> Reads = new();
    public bool FailCommit;
    public Func<IReadOnlyList<ResourceKey>, Task>? BeforeRead;
    public ValueTask<CatalogTransportRecord?> ReadTransportAsync(CatalogScope scope, string subject, int extensionKind,
        CancellationToken ct) => ValueTask.FromResult<CatalogTransportRecord?>(null);
    public async ValueTask<IReadOnlyList<CatalogRecord?>> ReadManyAsync(IReadOnlyList<ResourceKey> keys, CancellationToken ct)
    {
        lock (Reads) Reads.Add(keys.ToArray());
        if (BeforeRead is not null) await BeforeRead(keys);
        var records = new CatalogRecord?[keys.Count];
        lock (Records) for (int i = 0; i < keys.Count; i++) records[i] = Records.GetValueOrDefault(keys[i]);
        return records;
    }
    public ValueTask CommitAsync(CatalogCommit commit, CancellationToken ct)
    {
        if (FailCommit) throw new IOException("test persistence failure");
        lock (Records) foreach (var record in commit.Records) Records[record.Key] = record;
        return ValueTask.CompletedTask;
    }
}

internal sealed class CatalogObserver(Action<CatalogChangeSet> action) : IObserver<CatalogChangeSet>
{
    public void OnNext(CatalogChangeSet value) => action(value);
    public void OnError(Exception error) => throw error;
    public void OnCompleted() { }
}

internal sealed class CatalogClock : TimeProvider
{
    readonly object _gate = new();
    readonly List<Timer> _timers = new();
    DateTimeOffset _now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new Timer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }
    public void Advance(TimeSpan amount)
    {
        List<Timer> due;
        lock (_gate)
        {
            _now += amount;
            due = _timers.Where(timer => timer.Due <= _now).ToList();
            foreach (var timer in due) timer.Due = DateTimeOffset.MaxValue;
        }
        foreach (var timer in due) timer.Fire();
    }
    sealed class Timer(CatalogClock owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due = DateTimeOffset.MaxValue;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._now + dueTime;
            return true;
        }
        public void Fire() => callback(state);
        public void Dispose() { lock (owner._gate) { Due = DateTimeOffset.MaxValue; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
