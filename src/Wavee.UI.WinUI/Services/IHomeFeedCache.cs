using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Session;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Interface for the home feed cache service.
/// </summary>
public interface IHomeFeedCache : IDisposable
{
    bool HasData { get; }
    bool IsStale { get; }
    HomeFeedSnapshot? GetCached();
    Task<HomeFeedSnapshot> FetchFreshAsync(ISession session, CancellationToken ct = default);
    void Invalidate();
    void StartBackgroundRefresh(ISession session);
    void StopBackgroundRefresh();
    void SuspendRefresh();
    void ResumeRefresh();
    event Action<HomeFeedSnapshot>? DataRefreshed;

    static void ApplyDiff(
        ObservableCollection<HomeSection> current,
        System.Collections.Generic.List<HomeSection> fresh,
        Action<string?>? onGreetingChanged = null,
        string? newGreeting = null)
        => HomeFeedCache.ApplyDiff(current, fresh, onGreetingChanged, newGreeting);
}
