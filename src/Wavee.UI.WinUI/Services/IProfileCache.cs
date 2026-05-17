using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Interface for the profile cache service.
/// </summary>
public interface IProfileCache : IDisposable
{
    bool HasData { get; }
    bool IsStale { get; }
    ProfileSnapshot? GetCached();
    Task<ProfileSnapshot> FetchFreshAsync(CancellationToken ct = default);
    void Invalidate();
    void Clear();
    void StartBackgroundRefresh();
    void StopBackgroundRefresh();
    event Action<ProfileSnapshot>? DataRefreshed;
}
