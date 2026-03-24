using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Session;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Interface for the profile cache service.
/// </summary>
public interface IProfileCache : IDisposable
{
    bool HasData { get; }
    bool IsStale { get; }
    ProfileSnapshot? GetCached();
    Task<ProfileSnapshot> FetchFreshAsync(ISession session, CancellationToken ct = default);
    void Invalidate();
    void StartBackgroundRefresh(ISession session);
    void StopBackgroundRefresh();
    event Action<ProfileSnapshot>? DataRefreshed;
}
