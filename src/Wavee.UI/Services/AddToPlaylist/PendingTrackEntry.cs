using System;

namespace Wavee.UI.Services.AddToPlaylist;

/// <summary>
/// One pending track in an <see cref="IAddToPlaylistSession"/>. Uri is the
/// canonical identity (used for de-dupe and as the wire payload at submit
/// time). The other fields exist only to drive the floating bar's stacked
/// thumbnails and tooltip text — they are not persisted anywhere.
/// </summary>
public sealed class PendingTrackEntry
{
    public PendingTrackEntry()
    {
    }

    public PendingTrackEntry(
        string Uri,
        string Title,
        string? ArtistName,
        string? ImageUrl,
        TimeSpan Duration)
    {
        this.Uri = Uri;
        this.Title = Title;
        this.ArtistName = ArtistName;
        this.ImageUrl = ImageUrl;
        this.Duration = Duration;
    }

    public string Uri { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? ArtistName { get; set; }

    public string? ImageUrl { get; set; }

    public TimeSpan Duration { get; set; }
}
