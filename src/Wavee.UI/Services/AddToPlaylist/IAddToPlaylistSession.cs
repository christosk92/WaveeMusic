using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.Services.AddToPlaylist;

/// <summary>
/// App-wide "add tracks to a chosen playlist" modal session. Begun from a
/// playlist page (empty-state CTA, an empty-state genre chip, or the
/// non-empty "Find more songs" chip); the user then keeps navigating the
/// rest of the app while <see cref="TrackItem"/> instances expose a + button
/// that toggles entries in <see cref="Pending"/>. Submitted via
/// <see cref="SubmitAsync"/>; cancelled via <see cref="Cancel"/>.
///
/// Exactly one session is live in the app at a time. <see cref="Begin"/>
/// while already active <em>replaces</em> the target playlist and clears the
/// pending set (a deliberate choice — confirm-replace prompting can be
/// layered on later if it proves needed).
///
/// Implements <see cref="INotifyPropertyChanged"/> so XAML can bind directly
/// to <see cref="IsActive"/>, <see cref="TargetPlaylistName"/>, etc.
/// <see cref="Pending"/> fires <c>INotifyCollectionChanged</c> for the
/// stacked-thumbnail strip.
/// </summary>
public interface IAddToPlaylistSession : INotifyPropertyChanged
{
    bool IsActive { get; }

    string? TargetPlaylistId { get; }
    string? TargetPlaylistName { get; }
    string? TargetPlaylistImageUrl { get; }

    /// <summary>Pending tracks, in insertion order.</summary>
    ReadOnlyObservableCollection<PendingTrackEntry> Pending { get; }

    /// <summary>Count of <see cref="Pending"/> — bindable convenience so XAML
    /// doesn't have to call .Count on the collection (which doesn't repaint
    /// on collection change in some binding scenarios).</summary>
    int PendingCount { get; }

    /// <summary>True iff <paramref name="trackUri"/> is already in <see cref="Pending"/>. O(1).</summary>
    bool Contains(string trackUri);

    /// <summary>Open a new session targeted at the given playlist. Clears
    /// any previously pending tracks. <paramref name="playlistId"/> is the
    /// canonical playlist identifier passed straight to the submitter
    /// (so callers should pass whichever form their data layer expects —
    /// today, the bare playlist id or a <c>spotify:playlist:*</c> URI).</summary>
    void Begin(string playlistId, string playlistName, string? playlistImageUrl);

    /// <summary>Add the track if not already pending, remove it otherwise.
    /// Identity is the <see cref="PendingTrackEntry.Uri"/>.</summary>
    void Toggle(PendingTrackEntry entry);

    /// <summary>End the session without writing anything. Clears pending,
    /// flips <see cref="IsActive"/> to false.</summary>
    void Cancel();

    /// <summary>Submit the pending tracks to the target playlist and end the
    /// session on success. No-op (returns 0) if no pending tracks or no
    /// active target. Returns the number of tracks submitted.</summary>
    Task<int> SubmitAsync(CancellationToken ct = default);
}
