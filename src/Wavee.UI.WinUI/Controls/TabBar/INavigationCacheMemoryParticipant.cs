using System;
using System.Collections.Generic;

namespace Wavee.UI.WinUI.Controls.TabBar;

/// <summary>
/// Optional contract for cached pages that can release heavyweight UI state while
/// they are hidden, without disposing the page instance or its lightweight route state.
/// </summary>
public interface INavigationCacheMemoryParticipant
{
    /// <summary>
    /// Single-shot synchronous trim. Pages that don't override
    /// <see cref="GetTrimMicroSteps"/> have this method called as one chunk
    /// by the centralised deferred-trim path in <c>TabBarItem</c>.
    /// </summary>
    void TrimForNavigationCache();

    void RestoreFromNavigationCache();

    /// <summary>
    /// Returns the trim work as a sequence of small actions. The host
    /// (<c>TabBarItem</c>'s deferred-trim path) enqueues each action on a
    /// separate <c>DispatcherQueuePriority.Low</c> pump, allowing rendering
    /// and input frames to interleave between steps.
    ///
    /// <para>
    /// Default implementation returns a single-step sequence that calls
    /// <see cref="TrimForNavigationCache"/> — preserves existing behaviour
    /// for pages that haven't been split yet. Heavy participants (AlbumPage,
    /// PlaylistPage, ArtistPage) override this to split <c>ViewModel.Hibernate</c>
    /// and <c>Bindings.StopTracking</c> into separate dispatcher pumps so the
    /// 100+ ms trim no longer lands as a single hitch.
    /// </para>
    /// </summary>
    IEnumerable<Action> GetTrimMicroSteps()
    {
        yield return TrimForNavigationCache;
    }
}
