// ── Entities/TrackPreview.cs — a Home preview song's hover media: the 30 s audio clip and its cover's colour ──────────────────────
//
// `feedBaselineLookup` (Spotify.Decode.Home.cs's PreviewFold) answers, per baseline card, up to five preview songs, each with its canvas
// (staged into Edges.TrackCanvas, Entities/Canvas.cs, as a SEED: it never replaces a canvas kind 16 already answered), its
// `previews.audioPreviews.items[0].url` (p.scdn.co/mp3-preview/…) and its album cover's `extractedColors.colorDark`. The last two have no
// home on the track row (the row is shared by every page; a hover clip is a Home fact), so they ride here: one payload row per track, owned
// text, landed after the rows like the canvas (Entities.cs's commit order). Read by the Spine Shelf (Home/SpineShelf) through TryPreview.

using FluentGpu.Foundation;

namespace Wavee;

/// <summary>A preview song's hover facts: the 30 s mp3 clip (empty: none) and the cover's dark colour (ARGB, 0: unknown).</summary>
public readonly record struct TrackPreviewEdge(StringId AudioUrl, uint Accent);

public sealed partial class Edges
{
    /// <summary>The Home preview facts of a track (<see cref="TrackPreviewEdge"/>), at most ONE row. Parent = track slot; payload-only.</summary>
    public readonly EdgeTable<TrackPreviewEdge> TrackPreview = new();

    /// <summary>Give back the string one track's preview row owns.</summary>
    internal void ReleasePreviewText(int parent)
    {
        var rows = TrackPreview.Payload(parent);
        for (int i = 0; i < rows.Length; i++) Entities.Strings.Release(rows[i].AudioUrl);
    }

    /// <summary>Every parent's preview text (the scope's retirement).</summary>
    internal void ReleaseAllPreviewText()
    {
        for (int p = 0; p < TrackPreview.ParentCount; p++) ReleasePreviewText(p);
    }
}

/// <summary>One staged preview row (<see cref="TrackPreviewEdge"/>).</summary>
public struct StagedTrackPreview
{
    public StagedId Track;
    public TextRef AudioUrl;
    public uint Accent;
}

public sealed partial class Staging
{
    StagedList<StagedTrackPreview>? _trackPreviews;

    /// <inheritdoc cref="StagedTrackPreview"/>
    public StagedList<StagedTrackPreview> TrackPreviews => _trackPreviews ??= Register(new StagedList<StagedTrackPreview>());

    internal StagedList<StagedTrackPreview>? StagedTrackPreviews => _trackPreviews;
}

public static partial class Entities
{
    // UI thread only (C1): one row's scratch.
    static readonly int[] s_previewTarget = new int[1];
    static readonly TrackPreviewEdge[] s_previewRow = new TrackPreviewEdge[1];

    /// <summary>Land every staged preview row: a whole-list Replace per track, Complete. The new string is AddRef'd before the old row's is
    /// released, so a clip that kept its url keeps its id.</summary>
    static partial void CommitTrackPreviews(Staging s)
    {
        var staged = s.StagedTrackPreviews;
        if (staged is null || staged.Count == 0) return;
        var edges = Current.Edges;
        foreach (ref readonly var p in staged.Span)
        {
            int parent = s.Slot(Current.Tracks, in p.Track);
            if (parent == Table.None) continue;
            s_previewTarget[0] = Table.None;
            s_previewRow[0] = new TrackPreviewEdge(Retained(s.Intern(p.AudioUrl)), p.Accent);
            edges.ReleasePreviewText(parent);
            edges.TrackPreview.Replace(parent, s_previewTarget, s_previewRow, EdgeState.Complete, 1);
        }
    }

    /// <summary>The track's Home preview facts when the lookup answered them. Reads the table; allocates nothing.</summary>
    public static bool TryPreview(int trackSlot, out TrackPreviewEdge preview)
    {
        preview = default;
        var rows = Current.Edges.TrackPreview;
        if (rows.State(trackSlot) != EdgeState.Complete) return false;
        var p = rows.Payload(trackSlot);
        if (p.Length == 0) return false;
        preview = p[0];
        return true;
    }
}
