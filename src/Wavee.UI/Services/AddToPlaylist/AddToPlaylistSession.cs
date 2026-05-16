using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.Services.AddToPlaylist;

/// <inheritdoc/>
public sealed class AddToPlaylistSession : IAddToPlaylistSession
{
    private readonly IAddToPlaylistSubmitter _submitter;
    private readonly ILogger? _logger;

    private readonly ObservableCollection<PendingTrackEntry> _pending = new();
    private readonly ReadOnlyObservableCollection<PendingTrackEntry> _pendingReadOnly;
    private readonly HashSet<string> _pendingIndex = new(StringComparer.Ordinal);

    private bool _isActive;
    private string? _targetPlaylistId;
    private string? _targetPlaylistName;
    private string? _targetPlaylistImageUrl;

    public AddToPlaylistSession(
        IAddToPlaylistSubmitter submitter,
        ILogger<AddToPlaylistSession>? logger = null)
    {
        _submitter = submitter ?? throw new ArgumentNullException(nameof(submitter));
        _logger = logger;
        _pendingReadOnly = new ReadOnlyObservableCollection<PendingTrackEntry>(_pending);
        _pending.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PendingCount));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsActive
    {
        get => _isActive;
        private set => SetField(ref _isActive, value);
    }

    public string? TargetPlaylistId
    {
        get => _targetPlaylistId;
        private set => SetField(ref _targetPlaylistId, value);
    }

    public string? TargetPlaylistName
    {
        get => _targetPlaylistName;
        private set => SetField(ref _targetPlaylistName, value);
    }

    public string? TargetPlaylistImageUrl
    {
        get => _targetPlaylistImageUrl;
        private set => SetField(ref _targetPlaylistImageUrl, value);
    }

    public ReadOnlyObservableCollection<PendingTrackEntry> Pending => _pendingReadOnly;

    public int PendingCount => _pending.Count;

    public bool Contains(string trackUri)
        => !string.IsNullOrEmpty(trackUri) && _pendingIndex.Contains(trackUri);

    public void Begin(string playlistId, string playlistName, string? playlistImageUrl)
    {
        if (string.IsNullOrEmpty(playlistId))
            throw new ArgumentException("playlistId is required", nameof(playlistId));

        ClearPending();
        TargetPlaylistId = playlistId;
        TargetPlaylistName = playlistName ?? string.Empty;
        TargetPlaylistImageUrl = playlistImageUrl;
        IsActive = true;

        _logger?.LogInformation(
            "AddToPlaylist session begun for '{PlaylistId}' ({PlaylistName})",
            playlistId, playlistName);
    }

    public void Toggle(PendingTrackEntry entry)
    {
        if (entry is null) return;
        if (string.IsNullOrEmpty(entry.Uri)) return;
        if (!IsActive) return;

        if (_pendingIndex.Remove(entry.Uri))
        {
            for (var i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Uri == entry.Uri)
                {
                    _pending.RemoveAt(i);
                    break;
                }
            }
        }
        else
        {
            _pendingIndex.Add(entry.Uri);
            _pending.Add(entry);
        }
    }

    public void Cancel()
    {
        if (!IsActive) return;
        _logger?.LogInformation(
            "AddToPlaylist session cancelled (target='{PlaylistId}', pending={Count})",
            TargetPlaylistId, _pending.Count);
        EndSession();
    }

    public async Task<int> SubmitAsync(CancellationToken ct = default)
    {
        if (!IsActive) return 0;
        if (string.IsNullOrEmpty(TargetPlaylistId)) return 0;
        if (_pending.Count == 0) return 0;

        var playlistId = TargetPlaylistId!;
        var uris = _pending.Select(p => p.Uri).ToList();

        _logger?.LogInformation(
            "AddToPlaylist submitting {Count} tracks to '{PlaylistId}'",
            uris.Count, playlistId);

        try
        {
            // No ConfigureAwait(false) here — EndSession() below clears the
            // ObservableCollection that the floating bar / flyout ListView
            // are bound to. If the continuation resumes on a thread-pool
            // thread (which the submitter's task often does), the
            // CollectionChanged event would fire across threads into the
            // bound WinUI controls and throw a RPC_E_WRONG_THREAD, which
            // the bar's OnAddClick would swallow — leaving the bar visible
            // with tracks not actually added. Resuming on the caller's
            // sync context (UI thread for the bar's click handler) keeps
            // the mutation single-threaded.
            await _submitter.SubmitAsync(playlistId, uris, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "AddToPlaylist submit failed for '{PlaylistId}' ({Count} tracks) — session left open for retry",
                playlistId, uris.Count);
            throw;
        }

        EndSession();
        return uris.Count;
    }

    private void EndSession()
    {
        ClearPending();
        TargetPlaylistId = null;
        TargetPlaylistName = null;
        TargetPlaylistImageUrl = null;
        IsActive = false;
    }

    private void ClearPending()
    {
        if (_pending.Count == 0 && _pendingIndex.Count == 0) return;
        _pendingIndex.Clear();
        _pending.Clear();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
