using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Contracts;
using Wavee.UI.Models;

namespace Wavee.UI.Services.Actions;

public interface ILibrarySavedActionExecutor
{
    Task ApplySavedStateAsync(SavedItemType type, string itemUri, bool saved, CancellationToken ct = default);
}

public interface IPinActionExecutor
{
    Task ApplyPinnedStateAsync(string uri, bool pinned, CancellationToken ct = default);
}

public interface IPlaylistMutationActionExecutor
{
    Task<PlaylistSummaryDto> CreatePlaylistCoreAsync(string name, IReadOnlyList<string>? trackIds = null, CancellationToken ct = default);
    Task DeletePlaylistCoreAsync(string playlistId, CancellationToken ct = default);
    Task SetPlaylistFollowedCoreAsync(string playlistId, bool followed, CancellationToken ct = default);
    Task AddTracksToPlaylistCoreAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken ct = default);
    Task RemoveTracksFromPlaylistCoreAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken ct = default);
}

public sealed class LibraryUserActionFactory : IUserActionFactory
{
    private readonly ILibrarySavedActionExecutor _savedExecutor;
    private readonly IPinActionExecutor _pinExecutor;
    private readonly IPlaylistMutationActionExecutor _playlistExecutor;

    public LibraryUserActionFactory(
        ILibrarySavedActionExecutor savedExecutor,
        IPinActionExecutor pinExecutor,
        IPlaylistMutationActionExecutor playlistExecutor)
    {
        _savedExecutor = savedExecutor;
        _pinExecutor = pinExecutor;
        _playlistExecutor = playlistExecutor;
    }

    public IUndoableUserAction Create(UserActionDescriptor descriptor)
    {
        return descriptor.Kind switch
        {
            SetLibrarySavedAction.Kind => SetLibrarySavedAction.FromDescriptor(_savedExecutor, descriptor),
            SetPinnedAction.Kind => SetPinnedAction.FromDescriptor(_pinExecutor, descriptor),
            SetPlaylistFollowedAction.Kind => SetPlaylistFollowedAction.FromDescriptor(_playlistExecutor, descriptor),
            PlaylistTracksAction.Kind => PlaylistTracksAction.FromDescriptor(_playlistExecutor, descriptor),
            CreatePlaylistAction.Kind => CreatePlaylistAction.FromDescriptor(_playlistExecutor, descriptor),
            DeletePlaylistAction.Kind => DeletePlaylistAction.FromDescriptor(_playlistExecutor, descriptor),
            _ => throw new InvalidOperationException($"Unknown undoable action kind '{descriptor.Kind}'.")
        };
    }
}

public sealed class SetLibrarySavedAction : IUndoableUserAction
{
    public const string Kind = "library.saved.set";
    private readonly ILibrarySavedActionExecutor _executor;
    private readonly Payload _payload;

    public SetLibrarySavedAction(
        ILibrarySavedActionExecutor executor,
        SavedItemType itemType,
        string itemUri,
        bool previousSaved,
        bool newSaved)
    {
        _executor = executor;
        _payload = new Payload(itemType, itemUri, previousSaved, newSaved);
    }

    private SetLibrarySavedAction(ILibrarySavedActionExecutor executor, Payload payload)
    {
        _executor = executor;
        _payload = payload;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Category => "library";
    public string Title => FormatSavedTitle(_payload.ItemType, _payload.NewSaved);
    public string? Message => null;
    public string? IconGlyph => null;
    public string UndoLabel => "Undo";
    public UserActionDescriptor Descriptor => new(Kind, JsonSerializer.Serialize(_payload));

    public Task ExecuteAsync(CancellationToken ct = default) =>
        _executor.ApplySavedStateAsync(_payload.ItemType, _payload.ItemUri, _payload.NewSaved, ct);

    public Task UndoAsync(CancellationToken ct = default) =>
        _executor.ApplySavedStateAsync(_payload.ItemType, _payload.ItemUri, _payload.PreviousSaved, ct);

    public static SetLibrarySavedAction FromDescriptor(ILibrarySavedActionExecutor executor, UserActionDescriptor descriptor)
    {
        var payload = JsonSerializer.Deserialize<Payload>(descriptor.PayloadJson)
                      ?? throw new InvalidOperationException("Saved action payload is missing.");
        return new SetLibrarySavedAction(executor, payload);
    }

    private static string FormatSavedTitle(SavedItemType type, bool saved) => (type, saved) switch
    {
        (SavedItemType.Track, true) => "Saved song",
        (SavedItemType.Track, false) => "Removed song from library",
        (SavedItemType.Album, true) => "Saved album",
        (SavedItemType.Album, false) => "Removed album from library",
        (SavedItemType.Artist, true) => "Followed artist",
        (SavedItemType.Artist, false) => "Unfollowed artist",
        (SavedItemType.Show, true) => "Followed show",
        (SavedItemType.Show, false) => "Unfollowed show",
        _ => saved ? "Saved item" : "Removed item from library"
    };

    private sealed record Payload(SavedItemType ItemType, string ItemUri, bool PreviousSaved, bool NewSaved);
}

public sealed class SetPinnedAction : IUndoableUserAction
{
    public const string Kind = "library.pin.set";
    private readonly IPinActionExecutor _executor;
    private readonly Payload _payload;

    public SetPinnedAction(IPinActionExecutor executor, string uri, bool previousPinned, bool newPinned)
    {
        _executor = executor;
        _payload = new Payload(uri, previousPinned, newPinned);
    }

    private SetPinnedAction(IPinActionExecutor executor, Payload payload)
    {
        _executor = executor;
        _payload = payload;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Category => "library";
    public string Title => _payload.NewPinned ? "Pinned item" : "Unpinned item";
    public string? Message => null;
    public string? IconGlyph => null;
    public string UndoLabel => "Undo";
    public UserActionDescriptor Descriptor => new(Kind, JsonSerializer.Serialize(_payload));

    public Task ExecuteAsync(CancellationToken ct = default) =>
        _executor.ApplyPinnedStateAsync(_payload.Uri, _payload.NewPinned, ct);

    public Task UndoAsync(CancellationToken ct = default) =>
        _executor.ApplyPinnedStateAsync(_payload.Uri, _payload.PreviousPinned, ct);

    public static SetPinnedAction FromDescriptor(IPinActionExecutor executor, UserActionDescriptor descriptor)
    {
        var payload = JsonSerializer.Deserialize<Payload>(descriptor.PayloadJson)
                      ?? throw new InvalidOperationException("Pin action payload is missing.");
        return new SetPinnedAction(executor, payload);
    }

    private sealed record Payload(string Uri, bool PreviousPinned, bool NewPinned);
}

public sealed class SetPlaylistFollowedAction : IUndoableUserAction
{
    public const string Kind = "playlist.follow.set";
    private readonly IPlaylistMutationActionExecutor _executor;
    private readonly Payload _payload;

    public SetPlaylistFollowedAction(
        IPlaylistMutationActionExecutor executor,
        string playlistUri,
        bool previousFollowed,
        bool newFollowed)
    {
        _executor = executor;
        _payload = new Payload(playlistUri, previousFollowed, newFollowed);
    }

    private SetPlaylistFollowedAction(IPlaylistMutationActionExecutor executor, Payload payload)
    {
        _executor = executor;
        _payload = payload;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Category => "library";
    public string Title => _payload.NewFollowed ? "Followed playlist" : "Unfollowed playlist";
    public string? Message => null;
    public string? IconGlyph => null;
    public string UndoLabel => "Undo";
    public UserActionDescriptor Descriptor => new(Kind, JsonSerializer.Serialize(_payload));

    public Task ExecuteAsync(CancellationToken ct = default) =>
        _executor.SetPlaylistFollowedCoreAsync(_payload.PlaylistUri, _payload.NewFollowed, ct);

    public Task UndoAsync(CancellationToken ct = default) =>
        _executor.SetPlaylistFollowedCoreAsync(_payload.PlaylistUri, _payload.PreviousFollowed, ct);

    public static SetPlaylistFollowedAction FromDescriptor(
        IPlaylistMutationActionExecutor executor,
        UserActionDescriptor descriptor)
    {
        var payload = JsonSerializer.Deserialize<Payload>(descriptor.PayloadJson)
                      ?? throw new InvalidOperationException("Playlist follow action payload is missing.");
        return new SetPlaylistFollowedAction(executor, payload);
    }

    private sealed record Payload(string PlaylistUri, bool PreviousFollowed, bool NewFollowed);
}

public sealed class PlaylistTracksAction : IUndoableUserAction
{
    public const string Kind = "playlist.tracks.set";
    private readonly IPlaylistMutationActionExecutor _executor;
    private readonly Payload _payload;

    public PlaylistTracksAction(
        IPlaylistMutationActionExecutor executor,
        string playlistUri,
        IReadOnlyList<string> trackUris,
        bool addTracks)
    {
        _executor = executor;
        _payload = new Payload(
            playlistUri,
            trackUris.Where(static uri => !string.IsNullOrWhiteSpace(uri)).ToArray(),
            addTracks);
    }

    private PlaylistTracksAction(IPlaylistMutationActionExecutor executor, Payload payload)
    {
        _executor = executor;
        _payload = payload;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Category => "library";
    public string Title => FormatTitle(_payload.TrackUris.Length, _payload.AddTracks);
    public string? Message => null;
    public string? IconGlyph => null;
    public string UndoLabel => "Undo";
    public UserActionDescriptor Descriptor => new(Kind, JsonSerializer.Serialize(_payload));

    public Task ExecuteAsync(CancellationToken ct = default) =>
        _payload.AddTracks
            ? _executor.AddTracksToPlaylistCoreAsync(_payload.PlaylistUri, _payload.TrackUris, ct)
            : _executor.RemoveTracksFromPlaylistCoreAsync(_payload.PlaylistUri, _payload.TrackUris, ct);

    public Task UndoAsync(CancellationToken ct = default) =>
        _payload.AddTracks
            ? _executor.RemoveTracksFromPlaylistCoreAsync(_payload.PlaylistUri, _payload.TrackUris, ct)
            : _executor.AddTracksToPlaylistCoreAsync(_payload.PlaylistUri, _payload.TrackUris, ct);

    public static PlaylistTracksAction FromDescriptor(
        IPlaylistMutationActionExecutor executor,
        UserActionDescriptor descriptor)
    {
        var payload = JsonSerializer.Deserialize<Payload>(descriptor.PayloadJson)
                      ?? throw new InvalidOperationException("Playlist tracks action payload is missing.");
        return new PlaylistTracksAction(executor, payload);
    }

    private static string FormatTitle(int count, bool added)
    {
        var noun = count == 1 ? "song" : "songs";
        return added ? $"Added {count} {noun} to playlist" : $"Removed {count} {noun} from playlist";
    }

    private sealed record Payload(string PlaylistUri, string[] TrackUris, bool AddTracks);
}

public sealed class CreatePlaylistAction : IUndoableUserAction
{
    public const string Kind = "playlist.create";
    private readonly IPlaylistMutationActionExecutor _executor;
    private Payload _payload;

    public CreatePlaylistAction(
        IPlaylistMutationActionExecutor executor,
        string name,
        IReadOnlyList<string>? trackUris)
    {
        _executor = executor;
        _payload = new Payload(name, trackUris?.ToArray(), null);
    }

    private CreatePlaylistAction(IPlaylistMutationActionExecutor executor, Payload payload)
    {
        _executor = executor;
        _payload = payload;
    }

    public PlaylistSummaryDto? Result { get; private set; }
    public Guid Id { get; } = Guid.NewGuid();
    public string Category => "library";
    public string Title => "Created playlist";
    public string? Message => _payload.Name;
    public string? IconGlyph => null;
    public string UndoLabel => "Undo";
    public UserActionDescriptor Descriptor => new(Kind, JsonSerializer.Serialize(_payload));

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        Result = await _executor.CreatePlaylistCoreAsync(_payload.Name, _payload.TrackUris, ct).ConfigureAwait(false);
        _payload = _payload with { CreatedPlaylistUri = Result.Id };
    }

    public Task UndoAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_payload.CreatedPlaylistUri))
            return Task.CompletedTask;

        return _executor.DeletePlaylistCoreAsync(_payload.CreatedPlaylistUri, ct);
    }

    public static CreatePlaylistAction FromDescriptor(
        IPlaylistMutationActionExecutor executor,
        UserActionDescriptor descriptor)
    {
        var payload = JsonSerializer.Deserialize<Payload>(descriptor.PayloadJson)
                      ?? throw new InvalidOperationException("Create playlist action payload is missing.");
        return new CreatePlaylistAction(executor, payload);
    }

    private sealed record Payload(string Name, string[]? TrackUris, string? CreatedPlaylistUri);
}

public sealed class DeletePlaylistAction : IUndoableUserAction
{
    public const string Kind = "playlist.delete";
    private readonly IPlaylistMutationActionExecutor _executor;
    private readonly Payload _payload;

    public DeletePlaylistAction(IPlaylistMutationActionExecutor executor, string playlistUri)
    {
        _executor = executor;
        _payload = new Payload(playlistUri);
    }

    private DeletePlaylistAction(IPlaylistMutationActionExecutor executor, Payload payload)
    {
        _executor = executor;
        _payload = payload;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Category => "library";
    public string Title => "Deleted playlist";
    public string? Message => null;
    public string? IconGlyph => null;
    public string UndoLabel => "Undo";
    public UserActionDescriptor Descriptor => new(Kind, JsonSerializer.Serialize(_payload));

    public Task ExecuteAsync(CancellationToken ct = default) =>
        _executor.DeletePlaylistCoreAsync(_payload.PlaylistUri, ct);

    public Task UndoAsync(CancellationToken ct = default) =>
        _executor.SetPlaylistFollowedCoreAsync(_payload.PlaylistUri, followed: true, ct);

    public static DeletePlaylistAction FromDescriptor(
        IPlaylistMutationActionExecutor executor,
        UserActionDescriptor descriptor)
    {
        var payload = JsonSerializer.Deserialize<Payload>(descriptor.PayloadJson)
                      ?? throw new InvalidOperationException("Delete playlist action payload is missing.");
        return new DeletePlaylistAction(executor, payload);
    }

    private sealed record Payload(string PlaylistUri);
}
