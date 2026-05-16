using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Services.DragDrop.Payloads;

namespace Wavee.UI.Services.DragDrop;

/// <summary>
/// Default <see cref="IDragDropService"/>. Built up with fluent
/// <see cref="Register"/> calls at startup; thread-safe for read after
/// the registration phase.
/// </summary>
public sealed class DragDropService : IDragDropService
{
    private readonly record struct Entry(
        Func<DropContext, bool> CanDrop,
        Func<DropContext, CancellationToken, Task<DropResult>> Handle);

    private readonly Dictionary<(DragPayloadKind, DropTargetKind), Entry> _routes = new();

    /// <summary>
    /// Register a handler for <c>(payloadKind, targetKind)</c>. Pass
    /// <c>null</c> for <paramref name="canDrop"/> to always accept.
    /// Returns <c>this</c> for fluent chaining.
    /// </summary>
    public DragDropService Register(
        DragPayloadKind payloadKind,
        DropTargetKind targetKind,
        Func<DropContext, bool>? canDrop,
        Func<DropContext, CancellationToken, Task<DropResult>> handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _routes[(payloadKind, targetKind)] = new Entry(canDrop ?? (static _ => true), handle);
        return this;
    }

    public bool CanDrop(IDragPayload payload, DropTargetKind targetKind, string? targetId)
    {
        if (!_routes.TryGetValue((payload.Kind, targetKind), out var entry))
            return false;
        var ctx = new DropContext(payload, targetKind, targetId, DropPosition.Inside, null, DropModifiers.None);
        return entry.CanDrop(ctx);
    }

    public Task<DropResult> DropAsync(DropContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!_routes.TryGetValue((ctx.Payload.Kind, ctx.TargetKind), out var entry))
            return Task.FromResult(DropResult.NoHandler);
        if (!entry.CanDrop(ctx))
            return Task.FromResult(DropResult.NoHandler);
        return entry.Handle(ctx, ct);
    }

    public bool TryDeserialize(string format, string raw, out IDragPayload? payload)
    {
        payload = format switch
        {
            DragFormats.Tracks      => TrackDragPayload.Deserialize(raw),
            DragFormats.Album       => AlbumDragPayload.Deserialize(raw),
            DragFormats.Playlist    => PlaylistDragPayload.Deserialize(raw),
            DragFormats.Artist      => ArtistDragPayload.Deserialize(raw),
            DragFormats.SidebarItem => SidebarReorderPayload.Deserialize(raw),
            DragFormats.LikedSongs  => LikedSongsDragPayload.Deserialize(raw),
            DragFormats.Show        => ShowDragPayload.Deserialize(raw),
            _ => null
        };
        return payload is not null;
    }
}
