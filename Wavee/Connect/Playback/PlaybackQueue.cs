using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace Wavee.Connect.Playback;

/// <summary>
/// Thread-safe playback queue with shuffle, user queue, and infinite context support.
/// Uses Rx observables for signaling (consistent with codebase patterns).
/// </summary>
/// <remarks>
/// Thread safety: Uses lock pattern consistent with SafeSubject.
/// Signaling: Uses IObservable for NeedsMoreTracks instead of events.
/// </remarks>
public sealed class PlaybackQueue : IDisposable
{
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    // Track storage
    private readonly List<QueueTrack> _contextTracks = new();
    private readonly List<QueueTrack> _userQueue = new();  // Play these first

    // Shuffle state
    private List<int>? _shuffledIndices;
    private bool _isShuffled;

    // Position state
    private int _currentIndex = -1;
    private int _userQueuePlayedCount;  // How many user queue items have been played
    private int _queueUidCounter;  // Counter for q# UIDs (q0, q1, q2...)

    // Context metadata
    private string? _contextUri;
    private bool _isInfinite;
    private int? _totalTracks;

    // Observables for signaling
    private readonly Subject<Unit> _needsMoreTracks = new();
    private readonly Subject<QueueStateSnapshot> _stateChanged = new();
    private bool _needsMoreTracksRequested;

    private bool _disposed;

    /// <summary>
    /// Creates a new PlaybackQueue.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public PlaybackQueue(ILogger? logger = null)
    {
        _logger = logger;
    }

    #region Properties (Thread-Safe)

    /// <summary>
    /// Gets the context URI (playlist, album, station, etc.).
    /// </summary>
    public string? ContextUri
    {
        get { lock (_lock) { return _contextUri; } }
    }

    /// <summary>
    /// Gets whether this is an infinite context (radio, autoplay).
    /// </summary>
    public bool IsInfinite
    {
        get { lock (_lock) { return _isInfinite; } }
    }

    /// <summary>
    /// Gets the total track count (null for infinite contexts).
    /// </summary>
    public int? TotalTracks
    {
        get { lock (_lock) { return _totalTracks; } }
    }

    /// <summary>
    /// Gets the current track (or null if queue is empty or at end).
    /// </summary>
    public QueueTrack? Current
    {
        get { lock (_lock) { return GetCurrentInternal(); } }
    }

    /// <summary>
    /// Gets whether there's a next track available.
    /// </summary>
    public bool HasNext
    {
        get { lock (_lock) { return HasNextInternal(); } }
    }

    /// <summary>
    /// Gets whether there's a previous track available.
    /// </summary>
    public bool HasPrevious
    {
        get { lock (_lock) { return _currentIndex > 0; } }
    }

    /// <summary>
    /// Gets the number of loaded context tracks.
    /// </summary>
    public int LoadedCount
    {
        get { lock (_lock) { return _contextTracks.Count; } }
    }

    /// <summary>
    /// Gets the current index in the context (not counting user queue).
    /// </summary>
    public int CurrentIndex
    {
        get { lock (_lock) { return _currentIndex; } }
    }

    /// <summary>
    /// Gets whether shuffle is enabled.
    /// </summary>
    public bool IsShuffled
    {
        get { lock (_lock) { return _isShuffled; } }
    }

    /// <summary>
    /// Gets the number of tracks in the user queue.
    /// </summary>
    public int UserQueueCount
    {
        get { lock (_lock) { return _userQueue.Count; } }
    }

    #endregion

    #region Observables

    /// <summary>
    /// Observable that signals when more tracks are needed (for infinite/large contexts).
    /// Subscribe to load more tracks when approaching end of loaded tracks.
    /// </summary>
    public IObservable<Unit> NeedsMoreTracks => _needsMoreTracks.AsObservable();

    /// <summary>
    /// Observable for queue state changes (for UI updates).
    /// </summary>
    public IObservable<QueueStateSnapshot> StateChanged => _stateChanged.AsObservable();

    #endregion

    #region Context Setup

    /// <summary>
    /// Sets the context for the queue.
    /// </summary>
    /// <param name="contextUri">Context URI (playlist, album, station).</param>
    /// <param name="isInfinite">Whether this is an infinite context (radio, autoplay).</param>
    /// <param name="totalTracks">Total track count (null for infinite).</param>
    public void SetContext(string contextUri, bool isInfinite, int? totalTracks = null)
    {
        lock (_lock)
        {
            _contextUri = contextUri;
            _isInfinite = isInfinite;
            _totalTracks = totalTracks;
            _needsMoreTracksRequested = false;

            _logger?.LogDebug("Context set: uri={Uri}, infinite={IsInfinite}, total={Total}",
                contextUri, isInfinite, totalTracks);
        }
    }

    /// <summary>
    /// Sets the tracks for the queue, replacing any existing tracks.
    /// </summary>
    /// <param name="tracks">Tracks to set.</param>
    /// <param name="startIndex">Index to start playing from.</param>
    public void SetTracks(IEnumerable<QueueTrack> tracks, int startIndex = 0)
    {
        lock (_lock)
        {
            _contextTracks.Clear();
            _contextTracks.AddRange(tracks);
            _currentIndex = Math.Min(startIndex, Math.Max(0, _contextTracks.Count - 1));
            _needsMoreTracksRequested = false;

            // Regenerate shuffle if enabled
            if (_isShuffled)
            {
                GenerateShuffledOrderInternal();
            }
            else
            {
                _shuffledIndices = null;
            }

            _logger?.LogDebug("Tracks set: count={Count}, startIndex={StartIndex}",
                _contextTracks.Count, _currentIndex);
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// Appends tracks to the queue (for lazy loading / infinite contexts).
    /// </summary>
    /// <param name="tracks">Tracks to append.</param>
    public void AppendTracks(IEnumerable<QueueTrack> tracks)
    {
        var newTracks = tracks.ToList();
        if (newTracks.Count == 0)
            return;

        lock (_lock)
        {
            var oldCount = _contextTracks.Count;
            _contextTracks.AddRange(newTracks);
            _needsMoreTracksRequested = false;

            // If shuffled, add new tracks at random positions
            if (_isShuffled && _shuffledIndices != null)
            {
                var random = new Random();
                for (int i = oldCount; i < _contextTracks.Count; i++)
                {
                    // Insert at random position after current
                    var insertPos = _currentIndex + 1 + random.Next(_shuffledIndices.Count - _currentIndex);
                    insertPos = Math.Min(insertPos, _shuffledIndices.Count);
                    _shuffledIndices.Insert(insertPos, i);
                }
            }

            _logger?.LogDebug("Tracks appended: added={Added}, total={Total}",
                newTracks.Count, _contextTracks.Count);
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// Clears the queue.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _contextTracks.Clear();
            _userQueue.Clear();
            _shuffledIndices = null;
            _currentIndex = -1;
            _userQueuePlayedCount = 0;
            _contextUri = null;
            _isInfinite = false;
            _totalTracks = null;
            _needsMoreTracksRequested = false;

            _logger?.LogDebug("Queue cleared");
        }

        NotifyStateChanged();
    }

    #endregion

    #region Navigation

    /// <summary>
    /// Advances to the next track and returns it.
    /// Returns user queue items first, then context tracks.
    /// </summary>
    /// <returns>The next track, or null if at end of queue.</returns>
    public QueueTrack? MoveNext()
    {
        QueueTrack? result;
        bool needsMore = false;

        lock (_lock)
        {
            // First check user queue
            if (_userQueue.Count > 0)
            {
                result = _userQueue[0];
                _userQueue.RemoveAt(0);
                _userQueuePlayedCount++;

                _logger?.LogDebug("Playing from user queue: {Uri}", result.Uri);
            }
            else
            {
                // Move to next context track
                if (!HasNextInternal())
                {
                    _logger?.LogDebug("No next track available");
                    return null;
                }

                _currentIndex++;
                result = GetCurrentInternal();

                _logger?.LogDebug("Moved to next: index={Index}, uri={Uri}",
                    _currentIndex, result?.Uri);

                // Check if we need more tracks
                needsMore = CheckNeedsMoreTracksInternal();
            }
        }

        if (needsMore)
        {
            RequestMoreTracks();
        }

        NotifyStateChanged();
        return result;
    }

    /// <summary>
    /// Goes back to the previous track and returns it.
    /// </summary>
    /// <returns>The previous track, or null if at beginning.</returns>
    public QueueTrack? MovePrevious()
    {
        QueueTrack? result;

        lock (_lock)
        {
            if (_currentIndex <= 0)
            {
                _logger?.LogDebug("No previous track available");
                return null;
            }

            _currentIndex--;
            result = GetCurrentInternal();

            _logger?.LogDebug("Moved to previous: index={Index}, uri={Uri}",
                _currentIndex, result?.Uri);
        }

        NotifyStateChanged();
        return result;
    }

    /// <summary>
    /// Skips to a specific index in the context.
    /// </summary>
    /// <param name="index">Target index.</param>
    /// <returns>The track at that index, or null if invalid.</returns>
    public QueueTrack? SkipTo(int index)
    {
        QueueTrack? result;
        bool needsMore = false;

        lock (_lock)
        {
            if (index < 0 || index >= _contextTracks.Count)
            {
                _logger?.LogWarning("Skip to invalid index: {Index}, count={Count}",
                    index, _contextTracks.Count);
                return null;
            }

            _currentIndex = index;
            result = GetCurrentInternal();

            _logger?.LogDebug("Skipped to index: {Index}, uri={Uri}",
                index, result?.Uri);

            needsMore = CheckNeedsMoreTracksInternal();
        }

        if (needsMore)
        {
            RequestMoreTracks();
        }

        NotifyStateChanged();
        return result;
    }

    #endregion

    #region User Queue

    /// <summary>
    /// Adds a track to the user queue (plays next before continuing context).
    /// Assigns q# UID format (q0, q1, q2...) as per librespot.
    /// </summary>
    /// <param name="track">Track to add.</param>
    public void AddToQueue(QueueTrack track)
    {
        lock (_lock)
        {
            // Generate q# UID (librespot format)
            var queueUid = $"q{_queueUidCounter++}";
            var userQueuedTrack = track with { Uid = queueUid, IsUserQueued = true };
            _userQueue.Add(userQueuedTrack);

            _logger?.LogDebug("Added to user queue: {Uri} (uid={Uid}), queue size={Size}",
                track.Uri, queueUid, _userQueue.Count);
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// Removes a track from the user queue.
    /// </summary>
    /// <param name="index">Index in user queue to remove.</param>
    /// <returns>True if removed, false if invalid index.</returns>
    public bool RemoveFromQueue(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _userQueue.Count)
                return false;

            _userQueue.RemoveAt(index);

            _logger?.LogDebug("Removed from user queue: index={Index}, remaining={Count}",
                index, _userQueue.Count);
        }

        NotifyStateChanged();
        return true;
    }

    #endregion

    #region Shuffle

    /// <summary>
    /// Enables or disables shuffle mode.
    /// </summary>
    /// <param name="enabled">Whether to enable shuffle.</param>
    public void SetShuffle(bool enabled)
    {
        lock (_lock)
        {
            if (_isShuffled == enabled)
                return;

            _isShuffled = enabled;

            if (enabled)
            {
                GenerateShuffledOrderInternal();
            }
            else
            {
                // Find current track's actual index when disabling shuffle
                if (_shuffledIndices != null && _currentIndex >= 0 && _currentIndex < _shuffledIndices.Count)
                {
                    _currentIndex = _shuffledIndices[_currentIndex];
                }
                _shuffledIndices = null;
            }

            _logger?.LogDebug("Shuffle set: enabled={Enabled}", enabled);
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// Fisher-Yates shuffle to generate random order.
    /// Keeps current track at position 0.
    /// </summary>
    private void GenerateShuffledOrderInternal()
    {
        if (_contextTracks.Count == 0)
        {
            _shuffledIndices = null;
            return;
        }

        _shuffledIndices = Enumerable.Range(0, _contextTracks.Count).ToList();

        var random = new Random();

        // If we have a current track, put it first
        if (_currentIndex >= 0 && _currentIndex < _contextTracks.Count)
        {
            // Swap current track to position 0
            (_shuffledIndices[0], _shuffledIndices[_currentIndex]) =
                (_shuffledIndices[_currentIndex], _shuffledIndices[0]);

            // Shuffle everything after position 0
            for (int i = _shuffledIndices.Count - 1; i > 1; i--)
            {
                int j = random.Next(1, i + 1);  // Start from 1 to keep current at 0
                (_shuffledIndices[i], _shuffledIndices[j]) = (_shuffledIndices[j], _shuffledIndices[i]);
            }

            _currentIndex = 0;  // Current is now at position 0
        }
        else
        {
            // No current track, just shuffle everything
            for (int i = _shuffledIndices.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (_shuffledIndices[i], _shuffledIndices[j]) = (_shuffledIndices[j], _shuffledIndices[i]);
            }
        }

        _logger?.LogDebug("Generated shuffle order for {Count} tracks", _shuffledIndices.Count);
    }

    #endregion

    #region Internal Helpers

    private QueueTrack? GetCurrentInternal()
    {
        if (_currentIndex < 0)
            return null;

        var actualIndex = GetActualIndex(_currentIndex);
        if (actualIndex < 0 || actualIndex >= _contextTracks.Count)
            return null;

        return _contextTracks[actualIndex];
    }

    private bool HasNextInternal()
    {
        // User queue always has "next" if not empty
        if (_userQueue.Count > 0)
            return true;

        // Check context tracks
        var nextIndex = _currentIndex + 1;

        if (_isShuffled && _shuffledIndices != null)
            return nextIndex < _shuffledIndices.Count;

        return nextIndex < _contextTracks.Count;
    }

    private int GetActualIndex(int logicalIndex)
    {
        if (_isShuffled && _shuffledIndices != null)
        {
            if (logicalIndex < 0 || logicalIndex >= _shuffledIndices.Count)
                return -1;
            return _shuffledIndices[logicalIndex];
        }

        return logicalIndex;
    }

    private bool CheckNeedsMoreTracksInternal()
    {
        // Already requested, don't spam
        if (_needsMoreTracksRequested)
            return false;

        // Check if we're approaching the end of loaded tracks
        var threshold = 5;
        var tracksRemaining = _contextTracks.Count - _currentIndex - 1;

        if (tracksRemaining <= threshold)
        {
            // Only signal if infinite or we haven't loaded all tracks yet
            if (_isInfinite || (_totalTracks.HasValue && _contextTracks.Count < _totalTracks.Value))
            {
                _needsMoreTracksRequested = true;
                return true;
            }
        }

        return false;
    }

    private void RequestMoreTracks()
    {
        // Fire on thread pool to avoid blocking under lock
        Task.Run(() =>
        {
            try
            {
                _needsMoreTracks.OnNext(Unit.Default);
                _logger?.LogDebug("NeedsMoreTracks signal sent");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error sending NeedsMoreTracks signal");
            }
        });
    }

    private void NotifyStateChanged()
    {
        QueueStateSnapshot snapshot;

        lock (_lock)
        {
            var upcoming = GetUpcomingTracksInternal(10);
            snapshot = new QueueStateSnapshot(
                Current: GetCurrentInternal(),
                CurrentIndex: _currentIndex,
                LoadedCount: _contextTracks.Count,
                IsShuffled: _isShuffled,
                IsInfinite: _isInfinite,
                UpcomingTracks: upcoming,
                UserQueueTracks: _userQueue.ToList().AsReadOnly()
            );
        }

        // Fire on thread pool to avoid blocking
        Task.Run(() =>
        {
            try
            {
                _stateChanged.OnNext(snapshot);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error sending state change notification");
            }
        });
    }

    private IReadOnlyList<QueueTrack> GetUpcomingTracksInternal(int count)
    {
        var result = new List<QueueTrack>();

        // First add user queue items
        var userQueueToAdd = Math.Min(count, _userQueue.Count);
        for (int i = 0; i < userQueueToAdd; i++)
        {
            result.Add(_userQueue[i]);
        }

        // Then add context tracks
        var contextToAdd = count - result.Count;
        for (int i = 1; i <= contextToAdd; i++)
        {
            var logicalIndex = _currentIndex + i;
            var actualIndex = GetActualIndex(logicalIndex);

            if (actualIndex < 0 || actualIndex >= _contextTracks.Count)
                break;

            result.Add(_contextTracks[actualIndex]);
        }

        return result.AsReadOnly();
    }

    #endregion

    #region State Export (for PlayerState)

    /// <summary>
    /// Maximum previous tracks to include in state (matches librespot).
    /// </summary>
    private const int MaxPrevTracks = 16;

    /// <summary>
    /// Maximum next tracks to include in state (matches librespot).
    /// </summary>
    private const int MaxNextTracks = 48;

    /// <summary>
    /// Gets the previous tracks in context (up to 16).
    /// </summary>
    /// <returns>List of previous tracks, most recent last.</returns>
    public IReadOnlyList<QueueTrack> GetPrevTracks()
    {
        lock (_lock)
        {
            return GetPrevTracksInternal();
        }
    }

    /// <summary>
    /// Gets the next tracks (user queue first, then up to 48 context tracks).
    /// </summary>
    /// <returns>List of next tracks.</returns>
    public IReadOnlyList<QueueTrack> GetNextTracks()
    {
        lock (_lock)
        {
            return GetNextTracksInternal();
        }
    }

    private IReadOnlyList<QueueTrack> GetPrevTracksInternal()
    {
        var result = new List<QueueTrack>();

        // Get up to 16 tracks before current index
        var startLogical = Math.Max(0, _currentIndex - MaxPrevTracks);
        for (int i = startLogical; i < _currentIndex; i++)
        {
            var actualIndex = GetActualIndex(i);
            if (actualIndex >= 0 && actualIndex < _contextTracks.Count)
            {
                result.Add(_contextTracks[actualIndex]);
            }
        }

        return result.AsReadOnly();
    }

    private IReadOnlyList<QueueTrack> GetNextTracksInternal()
    {
        var result = new List<QueueTrack>();

        // First add all user queue items
        foreach (var track in _userQueue)
        {
            result.Add(track);
        }

        // Then add up to MaxNextTracks from context
        var contextToAdd = MaxNextTracks - result.Count;
        for (int i = 1; i <= contextToAdd; i++)
        {
            var logicalIndex = _currentIndex + i;
            var actualIndex = GetActualIndex(logicalIndex);

            if (actualIndex < 0 || actualIndex >= _contextTracks.Count)
                break;

            result.Add(_contextTracks[actualIndex]);
        }

        return result.AsReadOnly();
    }

    /// <summary>
    /// Gets the queue revision - a hash of next track URIs for change detection.
    /// Used by Spotify web player to know when queue UI needs refresh.
    /// </summary>
    /// <returns>Hash string representing current queue state.</returns>
    public string GetQueueRevision()
    {
        lock (_lock)
        {
            var hash = new HashCode();
            foreach (var track in GetNextTracksInternal())
            {
                hash.Add(track.Uri);
            }
            return hash.ToHashCode().ToString();
        }
    }

    #endregion

    #region Snapshot

    /// <summary>
    /// Gets a snapshot of the current queue state.
    /// </summary>
    /// <returns>Immutable snapshot of queue state.</returns>
    public QueueStateSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new QueueStateSnapshot(
                Current: GetCurrentInternal(),
                CurrentIndex: _currentIndex,
                LoadedCount: _contextTracks.Count,
                IsShuffled: _isShuffled,
                IsInfinite: _isInfinite,
                UpcomingTracks: GetUpcomingTracksInternal(10),
                UserQueueTracks: _userQueue.ToList().AsReadOnly()
            );
        }
    }

    #endregion

    #region Disposal

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _needsMoreTracks.OnCompleted();
        _needsMoreTracks.Dispose();

        _stateChanged.OnCompleted();
        _stateChanged.Dispose();

        _logger?.LogDebug("PlaybackQueue disposed");
    }

    #endregion
}

/// <summary>
/// Immutable snapshot of queue state for UI/observers.
/// </summary>
public record QueueStateSnapshot(
    QueueTrack? Current,
    int CurrentIndex,
    int LoadedCount,
    bool IsShuffled,
    bool IsInfinite,
    IReadOnlyList<QueueTrack> UpcomingTracks,
    IReadOnlyList<QueueTrack> UserQueueTracks
);
