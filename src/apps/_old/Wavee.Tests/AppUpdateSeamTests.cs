using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Xunit;

// The app-update seam: the Null impl is permanently inert, and a scripted fake walking every state maps to an
// AppUpdateNotification that the aggregation pins (unread) — while None contributes nothing.
public class AppUpdateSeamTests
{
    [Fact]
    public async Task Null_IsInert()
    {
        var svc = new NullAppUpdateService();
        Assert.Equal(AppUpdateSnapshot.Idle, svc.Current);
        Assert.Equal(AppUpdateState.None, svc.Current.State);
        Assert.Equal("", svc.FeedUrl);
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        await svc.ApplyAsync(CancellationToken.None);
        svc.Snooze();
        svc.Acknowledge();   // no throw
        Assert.Equal(AppUpdateState.None, svc.Current.State);
    }

    [Fact]
    public void None_ContributesNoNotification()
    {
        var (items, unread) = NotificationMerge.Build(UpdateNotification(new FakeAppUpdateService()),
            Array.Empty<SocialNotification>(), 0, Array.Empty<NewReleaseNotification>(), 0, Array.Empty<ActivityEntry>());
        Assert.Empty(items);
        Assert.Equal(0, unread);
    }

    [Theory]
    [InlineData(AppUpdateState.Checking)]
    [InlineData(AppUpdateState.Available)]
    [InlineData(AppUpdateState.Snoozed)]
    [InlineData(AppUpdateState.Downloading)]
    [InlineData(AppUpdateState.Installing)]
    [InlineData(AppUpdateState.Completed)]
    [InlineData(AppUpdateState.Failed)]
    public void EachState_MapsToPinnedUnreadNotification(AppUpdateState state)
    {
        var fake = new FakeAppUpdateService();
        int changes = 0;
        using var sub = fake.Changed.Subscribe(Obs<int>(_ => Interlocked.Increment(ref changes)));
        fake.Set(AppUpdateSnapshot.Idle with
        {
            State = state,
            TargetQuad = "9.9.9.9",
            TargetSemVer = "9.9.9",
            Failure = state == AppUpdateState.Failed ? new AppUpdateFailure(AppUpdateFailureKind.Network, 0, "network") : null,
        });
        Assert.True(changes >= 1);

        var (items, unread) = NotificationMerge.Build(UpdateNotification(fake),
            Array.Empty<SocialNotification>(), 0, Array.Empty<NewReleaseNotification>(), 0, Array.Empty<ActivityEntry>());
        var n = Assert.IsType<AppUpdateNotification>(Assert.Single(items));
        Assert.Equal(state, n.Snapshot.State);
        Assert.Equal("9.9.9.9", n.Snapshot.TargetQuad);
        Assert.True(n.IsUnread);
        Assert.Equal(1, unread);
    }

    // The snapshot is published WHOLE: a reader that captured it never sees a later value bleed into it.
    [Fact]
    public void Snapshot_IsImmutable_AcrossPublishes()
    {
        var fake = new FakeAppUpdateService();
        fake.Set(AppUpdateSnapshot.Idle with { State = AppUpdateState.Downloading, ProgressPercent = 37, TargetQuad = "1.0.0.5" });
        var captured = fake.Current;
        fake.Set(captured with { ProgressPercent = 91 });

        Assert.Equal(37, captured.ProgressPercent);
        Assert.Equal(91, fake.Current.ProgressPercent);
        Assert.Equal("1.0.0.5", fake.Current.TargetQuad);
    }

    static AppUpdateNotification? UpdateNotification(IAppUpdateService svc)
        => svc.Current.State == AppUpdateState.None
            ? null
            : new AppUpdateNotification(long.MaxValue, true, svc.Current);

    static IObserver<T> Obs<T>(Action<T> onNext) => new Ob<T>(onNext);
    sealed class Ob<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    // A scripted updater walking states — the shape a real updater would satisfy.
    sealed class FakeAppUpdateService : IAppUpdateService
    {
        readonly SimpleEvent<int> _changed = new();
        int _rev;
        public AppUpdateSnapshot Current { get; private set; } = AppUpdateSnapshot.Idle;
        public IObservable<int> Changed => _changed;
        public string FeedUrl => "https://example.invalid/Wavee.arm64.appinstaller";

        public void Set(AppUpdateSnapshot snapshot)
        {
            Current = snapshot;
            _changed.OnNext(Interlocked.Increment(ref _rev));
        }

        public Task CheckAsync(UpdateCheckOrigin origin, CancellationToken ct)
        {
            Set(AppUpdateSnapshot.Idle with { State = AppUpdateState.Available, TargetQuad = "9.9.9.9", TargetSemVer = "9.9.9" });
            return Task.CompletedTask;
        }

        public Task ApplyAsync(CancellationToken ct)
        {
            Set(Current with { State = AppUpdateState.Installing, ProgressPercent = 100 });
            return Task.CompletedTask;
        }

        public void Snooze() => Set(Current with { State = AppUpdateState.Snoozed });
        public void Acknowledge() => Set(AppUpdateSnapshot.Idle);
    }
}
