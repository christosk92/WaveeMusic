using System.Collections.Generic;
using System.Linq;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// Drag payload for one or more tracks being dragged to a playlist.
/// </summary>
public sealed class TrackDragPayload : IDragPayload
{
    public IReadOnlyList<ITrackItem> Tracks { get; }

    public string DataFormat => "WaveeTrackIds";
    public string SerializedData => string.Join("|", Tracks.Select(t => t.Id));
    public int ItemCount => Tracks.Count;

    public TrackDragPayload(IReadOnlyList<ITrackItem> tracks) => Tracks = tracks;
}
