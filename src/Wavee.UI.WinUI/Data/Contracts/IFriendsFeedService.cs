using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Right-panel Friend Activity feed. Seeded via
/// <c>spclient /presence-view/v2/init-friend-feed/{connectionId}</c> and
/// kept live by dealer websocket pushes.
/// </summary>
public interface IFriendsFeedService : INotifyPropertyChanged
{
    ReadOnlyObservableCollection<FriendFeedRowViewModel> Items { get; }

    FriendsFeedState State { get; }

    bool IsLoading { get; }

    DateTimeOffset? LastUpdated { get; }

    string? ErrorMessage { get; }

    /// <summary>
    /// Last dealer URI that triggered an update. Debug-only, used for R&amp;D to
    /// narrow the push-channel filter.
    /// </summary>
    string? LastPushUri { get; }

    /// <summary>
    /// Called by the view when the Friends tab gains or loses visibility. The
    /// dealer subscription stays alive in both states; only the safety timer
    /// is gated on active.
    /// </summary>
    void SetActive(bool isActive);

    /// <summary>
    /// Manual refresh (e.g. from the Retry button in the error state).
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires on the UI thread after a dealer push has been applied (the row for
    /// this <c>UserUri</c> has just been upserted). The view can use this to run
    /// a brief highlight animation on the corresponding list element. Does NOT
    /// fire for initial seed entries.
    /// </summary>
    event Action<string>? FriendUpserted;
}

public enum FriendsFeedState
{
    Idle,
    Loading,
    Populated,
    Empty,
    Offline,
    Error
}
