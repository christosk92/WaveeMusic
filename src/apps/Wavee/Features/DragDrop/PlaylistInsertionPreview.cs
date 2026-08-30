using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The CONTENT of a playlist insertion gap — the cards the user sees land where they are aiming. Position, size and
/// lifecycle belong to the framework (<see cref="InsertionOptions.GapPreview"/> on <see cref="ItemsView"/>); this only
/// draws the ≤<see cref="Cap"/> track cards, the last one carrying the "+N" pill for a larger block.
/// </summary>
static class PlaylistInsertionPreview
{
    /// <summary>Preview cards drawn in the gap (and, for a cross-list copy, the gap's row cap — an exact-N gap for a
    /// 500-track copy would blow the viewport). Deliberately the FRAMEWORK's cap, not a second 3: the view sizes the
    /// gap from <see cref="SortableMath.DefaultPreviewCap"/>, so a local literal would drift the cards off the gap.</summary>
    internal const int Cap = SortableMath.DefaultPreviewCap;

    internal static Element Cards(WaveeResourceDragPayload payload, float rowH, bool showArtwork = true)
    {
        var tracks = payload.Tracks;
        int total = tracks is { Count: > 0 } ? tracks.Count : 1;
        int shown = Math.Min(Cap, total);
        var rows = new Element[shown];
        for (int i = 0; i < shown; i++)
        {
            Track? track = tracks is { Count: > 0 } && i < tracks.Count ? tracks[i] : null;
            int hidden = i == shown - 1 ? total - shown : 0;
            rows[i] = Row(track, payload.Name, rowH, hidden, showArtwork);
        }
        return new BoxEl { Direction = 1, Shrink = 0f, HitTestVisible = false, Children = rows };
    }

    static Element Row(Track? track, string fallback, float height, int hidden, bool showArtwork)
    {
        string title = track?.Title is { Length: > 0 } name ? name : fallback;
        string subtitle = track?.Artists is { Count: > 0 } artists ? artists[0].Name : "";
        Element art = track is { } t
            ? Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff,
                TrackRow.ThumbSize, TrackRow.ThumbSize, Radii.Control)
            : new BoxEl
            {
                Width = TrackRow.ThumbSize, Height = TrackRow.ThumbSize,
                Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
                Children = [Ui.Icon(Icons.MusicNote, 16f, Tok.TextSecondary)],
            };
        var children = new Element[(showArtwork ? 1 : 0) + 1 + (hidden > 0 ? 1 : 0)];
        int child = 0;
        if (showArtwork) children[child++] = art;
        children[child++] = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = Spacing.XXS,
            Children =
            [
                new TextEl(title) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                new TextEl(subtitle) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
        if (hidden > 0)
            children[child] = new BoxEl
            {
                Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS),
                Corners = Radii.PillAll, Fill = Tok.AccentSubtle,
                Children = [new TextEl("+" + hidden) { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary }],
            };
        return new BoxEl
        {
            Direction = 0, Height = height, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Margin = new Edges4(TrackRow.RowInset, 0f, TrackRow.RowInset, 0f),
            Padding = new Edges4(TrackRow.PadX - TrackRow.RowInset, 0f,
                TrackRow.PadX - TrackRow.RowInset, 0f),
            Corners = Radii.ControlAll,
            Fill = Tok.FillSolidSecondary,
            BorderWidth = 1f, BorderColor = Tok.AccentDefault,
            Shadow = Elevation.Card, HitTestVisible = false,
            Children = children,
        };
    }
}
