using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.Services.Infra;

/// <summary>
/// Scope tag for change events flowing through <see cref="IChangeBus"/>.
/// Coarse-grained on purpose — every subscriber filters down to the scopes
/// it cares about (sidebar refreshes on <see cref="Playlists"/>; liked-songs
/// page reloads on <see cref="Library"/>; etc.).
/// </summary>
public enum ChangeScope
{
    /// <summary>Any library-state change: saves, follow, podcast progress, sync complete.</summary>
    Library = 0,
    /// <summary>Playlist tree changed (added/removed/renamed/reordered/folder ops).</summary>
    Playlists = 1,
    /// <summary>Pinned items list changed.</summary>
    Pins = 2,
    /// <summary>Podcast episode progress changed (resume position written).</summary>
    PodcastProgress = 3,
}

/// <summary>
/// Single sink for "something changed in the library" notifications. Replaces
/// the four fan-out paths the codebase carried before the Phase 1 refactor:
/// <list type="bullet">
///   <item><c>ILibraryDataService.DataChanged</c> event</item>
///   <item><c>ILibraryDataService.PlaylistsChanged</c> event</item>
///   <item><c>LibraryDataChangedMessage</c> via <c>IMessenger</c></item>
///   <item><c>PlaylistsChangedMessage</c> via <c>IMessenger</c></item>
/// </list>
///
/// <para>Coalesces bursts. A storm of <c>Publish(scope)</c> calls inside the
/// window collapses to a single emission per scope. The window matches the
/// previous <c>LibraryDataService.ChangeCoalesceWindow</c> (150 ms).</para>
///
/// <para>Subscribers receive scopes on a background thread — marshal to the
/// UI thread yourself (most consumers route through <c>IUiDispatcher</c> /
/// <see cref="IReloadCoordinator"/>).</para>
/// </summary>
public interface IChangeBus
{
    void Publish(ChangeScope scope);
    IObservable<ChangeScope> Changes { get; }
}

/// <summary>
/// Default <see cref="IChangeBus"/> implementation. Per-scope coalescing
/// via a 150 ms tail-delayed flush.
/// </summary>
public sealed class ChangeBus : IChangeBus, IDisposable
{
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(150);

    private readonly Subject<ChangeScope> _subject = new();
    private readonly object _gate = new();
    private readonly HashSet<ChangeScope> _pending = [];
    private readonly Timer _flushTimer;
    private readonly ILogger<ChangeBus>? _logger;
    private bool _flushScheduled;
    private bool _disposed;

    public ChangeBus(ILogger<ChangeBus>? logger = null)
    {
        _logger = logger;
        _flushTimer = new Timer(_ => FlushPending(), state: null, Timeout.Infinite, Timeout.Infinite);
    }

    public IObservable<ChangeScope> Changes => _subject.AsObservable();

    public void Publish(ChangeScope scope)
    {
        if (_disposed) return;
        bool schedule;
        lock (_gate)
        {
            _pending.Add(scope);
            schedule = !_flushScheduled;
            if (schedule) _flushScheduled = true;
        }
        if (schedule)
        {
            _flushTimer.Change(CoalesceWindow, Timeout.InfiniteTimeSpan);
            _logger?.LogDebug("ChangeBus.Publish({Scope}) — flush scheduled", scope);
        }
        else
        {
            _logger?.LogDebug("ChangeBus.Publish({Scope}) — coalesced into pending flush", scope);
        }
    }

    private void FlushPending()
    {
        ChangeScope[] toEmit;
        lock (_gate)
        {
            toEmit = new ChangeScope[_pending.Count];
            var i = 0;
            foreach (var s in _pending) toEmit[i++] = s;
            _pending.Clear();
            _flushScheduled = false;
        }
        if (_disposed) return;
        foreach (var scope in toEmit)
        {
            try
            {
                _subject.OnNext(scope);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ChangeBus subscriber threw on scope {Scope}", scope);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Dispose();
        _subject.OnCompleted();
        _subject.Dispose();
    }
}
