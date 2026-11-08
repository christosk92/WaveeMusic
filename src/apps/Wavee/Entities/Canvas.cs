// ── Entities/Canvas.cs — a track's Canvas: the record, its relation and its commit (extension kind 16 CANVAS_V1) ──────────────────────────
//
// Role: CORE (the record; the shell's `Canvas` host decides when to ask and where it shows: Shell/Canvas.cs)
//
// THE SOURCE. The official client asks extension kind 16 `CANVAS_V1` on the extended-metadata POST, batched per entity, and gets back a
// `spotify.canvaz.cache.EntityCanvazResponse.Canvaz` in the envelope's `Any` (Spotify/Protos/canvaz.proto; handoff canvas-2026-11-07).
// Wavee asks it through the edge door like credits (kind 186): `FetchEdge.TrackCanvas` → `FetchRoute.Metadata(16)`, so the playing track
// and the next ones the host asks for ride ONE POST. Decoded by `Spotify.Decode.Canvas` (Spotify.Decode.Canvas.cs).
//
// THE RECORD. One row per track at most, "the row is the payload" (as TrackTags): `Edges.TrackCanvas`, parent = the track's slot, its one
// target unused. An EMPTY Complete run is "this track has no canvas" — the answer that stops the host asking (finding 27's rule). The
// entity the server leaves out (no extension_data) stays Unknown and is recorded NoRoute by the door, which every reader reads as none too.
// Seven OWNED strings per row — AddRef'd at commit, released when the track's canvas is replaced and when the scope retires
// (`Edges.ReleaseText`). The answer's own lifetimes ride the row (`CacheTtl`, `OfflineTtl`, stamped `FetchedAt`): the host re-asks a row past
// its cache TTL (the metadata cache then does the ETag round trip) and the disk index keeps one for offline use up to its offline TTL.

using FluentGpu.Foundation;

namespace Wavee;

/// <summary>The wire's <c>spotify.canvaz.cache.Type</c>. The numbers ARE the protocol.</summary>
public enum CanvasKind : byte
{
    Image = 0,
    Video = 1,
    VideoLooping = 2,
    VideoLoopingRandom = 3,
    Gif = 4,
}

/// <summary>One track's Canvas (extension kind 16). Seven OWNED strings; times in Unix seconds.</summary>
/// <param name="Url">The <c>.cnvs.mp4</c> on canvaz.scdn.co (or a local path under <c>--fake</c>).</param>
/// <param name="Poster">The largest <c>.thmb.&lt;w&gt;x&lt;h&gt;.jpg</c> still (field 13), the frame shown until the first video frame.</param>
/// <param name="CanvasUri"><c>spotify:canvas:…</c>.</param>
/// <param name="Artist">The uploading artist's name (field 6).</param>
/// <param name="ArtistUri">Its <c>spotify:artist:…</c>.</param>
/// <param name="UploadedBy"><c>licensor</c> or <c>artist</c>.</param>
/// <param name="Etag">The canvas's own etag (field 9).</param>
public readonly record struct CanvasEdge(StringId Url, StringId Poster, StringId CanvasUri, StringId Artist, StringId ArtistUri,
                                         StringId UploadedBy, StringId Etag, CanvasKind Kind, ushort PosterW, ushort PosterH,
                                         long FetchedAt, int CacheTtl, int OfflineTtl)
{
    /// <summary>The server's cache TTL when the header names none (the captures: 3600 s).</summary>
    public const int DefaultCacheTtl = 3600;
    /// <summary>The offline TTL when the header names none (the captures: 30 days).</summary>
    public const int DefaultOfflineTtl = 30 * 24 * 3600;

    /// <summary>Is this a canvas the app plays: a video kind with a url. An image or GIF canvas, an unknown kind and an empty url are all
    /// "no canvas" (the page shows what it shows without one).</summary>
    public bool Plays => !Url.IsEmpty && Kind is CanvasKind.Video or CanvasKind.VideoLooping or CanvasKind.VideoLoopingRandom;

    /// <summary>Past its cache TTL at <paramref name="nowSec"/>: the host asks again (the rows keep rendering).</summary>
    public bool Stale(long nowSec) => nowSec >= FetchedAt + Math.Max(60, CacheTtl);

    /// <summary>Still usable offline at <paramref name="nowSec"/>.</summary>
    public bool OfflineFresh(long nowSec) => nowSec < FetchedAt + Math.Max(CacheTtl, OfflineTtl);
}

/// <summary>A canvas as plain text — what the disk index (Shell/Canvas.Files.cs) and the fake seed hand to <see cref="Entities.LandCanvas"/>.</summary>
public sealed record CanvasText(string Url, string Poster, string CanvasUri, string Artist, string ArtistUri, string UploadedBy, string Etag,
                                CanvasKind Kind, int PosterW, int PosterH, long FetchedAt, int CacheTtl, int OfflineTtl)
{
    /// <inheritdoc cref="CanvasEdge.Stale"/>
    public bool Stale(long nowSec) => nowSec >= FetchedAt + Math.Max(60, CacheTtl);

    /// <inheritdoc cref="CanvasEdge.OfflineFresh"/>
    public bool OfflineFresh(long nowSec) => nowSec < FetchedAt + Math.Max(CacheTtl, OfflineTtl);

    public static CanvasText Of(in CanvasEdge e)
    {
        var s = Entities.Strings;
        return new(s.Resolve(e.Url), s.Resolve(e.Poster), s.Resolve(e.CanvasUri), s.Resolve(e.Artist), s.Resolve(e.ArtistUri),
                   s.Resolve(e.UploadedBy), s.Resolve(e.Etag), e.Kind, e.PosterW, e.PosterH, e.FetchedAt, e.CacheTtl, e.OfflineTtl);
    }
}

public sealed partial class Edges
{
    /// <summary>Extension kind 16 (<c>CANVAS_V1</c>): the track's Canvas, at most ONE row (<see cref="CanvasEdge"/>). Parent = track slot;
    /// payload-only (the target is unused). EMPTY Complete = the track has no canvas. Owned text: see <see cref="CanvasEdge"/>.</summary>
    public readonly EdgeTable<CanvasEdge> TrackCanvas = new();

    /// <summary>When a NONE answer (an empty run) stops being true (Unix seconds), per parent: the host re-asks after it like a stale row.
    /// Only the empty answers are kept here; a row carries its own stamp.</summary>
    internal readonly Dictionary<int, long> CanvasNoneUntil = new();

    /// <summary>Give back the strings one track's canvas owns.</summary>
    internal void ReleaseCanvasText(int parent)
    {
        var rows = TrackCanvas.Payload(parent);
        for (int i = 0; i < rows.Length; i++)
        {
            ref readonly var r = ref rows[i];
            Entities.Strings.Release(r.Url);
            Entities.Strings.Release(r.Poster);
            Entities.Strings.Release(r.CanvasUri);
            Entities.Strings.Release(r.Artist);
            Entities.Strings.Release(r.ArtistUri);
            Entities.Strings.Release(r.UploadedBy);
            Entities.Strings.Release(r.Etag);
        }
    }

    /// <summary>Every parent's canvas text (the scope's retirement, <see cref="ReleaseText"/>).</summary>
    internal void ReleaseAllCanvasText()
    {
        for (int p = 0; p < TrackCanvas.ParentCount; p++) ReleaseCanvasText(p);
        CanvasNoneUntil.Clear();
    }
}

/// <summary>One staged canvas answer. <see cref="None"/>: the server said this track has none (or named a kind the app does not read).</summary>
public struct StagedCanvas
{
    public StagedId Track;
    public TextRef Url, Poster, CanvasUri, Artist, ArtistUri, UploadedBy, Etag;
    public CanvasKind Kind;
    public ushort PosterW, PosterH;
    public int CacheTtl, OfflineTtl;
    public bool None;
}

public sealed partial class Staging
{
    StagedList<StagedCanvas>? _canvases;

    /// <inheritdoc cref="StagedCanvas"/>
    public StagedList<StagedCanvas> Canvases => _canvases ??= Register(new StagedList<StagedCanvas>());

    internal StagedList<StagedCanvas>? StagedCanvases => _canvases;
}

public static partial class Entities
{
    // UI thread only (C1): one row's scratch.
    static readonly int[] s_canvasTarget = new int[1];
    static readonly CanvasEdge[] s_canvasRow = new CanvasEdge[1];

    /// <summary>Land every staged canvas: a whole-list Replace per track, Complete, EMPTY for none. The new strings are AddRef'd BEFORE
    /// the old row's are released, so a canvas that kept its url keeps its id.</summary>
    static partial void CommitCanvas(Staging s)
    {
        var staged = s.StagedCanvases;
        if (staged is null || staged.Count == 0) return;
        var edges = Current.Edges;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (ref readonly var c in staged.Span)
        {
            int parent = s.Slot(Current.Tracks, in c.Track);
            if (parent == Table.None) continue;
            int cacheTtl = c.CacheTtl > 0 ? c.CacheTtl : CanvasEdge.DefaultCacheTtl;
            if (c.None || c.Url.IsEmpty)
            {
                edges.ReleaseCanvasText(parent);
                edges.TrackCanvas.Replace(parent, default, default, EdgeState.Complete, 0);
                edges.CanvasNoneUntil[parent] = now + cacheTtl;
                continue;
            }
            s_canvasTarget[0] = Table.None;
            s_canvasRow[0] = new CanvasEdge(Retained(s.Intern(c.Url)), Retained(s.Intern(c.Poster)), Retained(s.Intern(c.CanvasUri)),
                Retained(s.Intern(c.Artist)), Retained(s.Intern(c.ArtistUri)), Retained(s.Intern(c.UploadedBy)), Retained(s.Intern(c.Etag)),
                c.Kind, c.PosterW, c.PosterH, now, cacheTtl, c.OfflineTtl > 0 ? c.OfflineTtl : CanvasEdge.DefaultOfflineTtl);
            edges.ReleaseCanvasText(parent);
            edges.TrackCanvas.Replace(parent, s_canvasTarget, s_canvasRow, EdgeState.Complete, 1);
            edges.CanvasNoneUntil.Remove(parent);
        }
    }

    /// <summary>Land a canvas the app already holds as text — the disk index's offline copy, the fake catalog — on the track at
    /// <paramref name="slot"/>, marked asked so the door does not send for it. <c>null</c> lands "no canvas". UI thread; the caller publishes.</summary>
    public static void LandCanvas(int slot, CanvasText? c)
    {
        var edges = Current.Edges;
        if (slot <= Table.None || slot >= Current.Tracks.Count) return;
        edges.TrackCanvas.MarkAsked(slot, 0);
        if (c is null || c.Url.Length == 0)
        {
            edges.ReleaseCanvasText(slot);
            edges.TrackCanvas.Replace(slot, default, default, EdgeState.Complete, 0);
            edges.CanvasNoneUntil[slot] = long.MaxValue;
            return;
        }
        s_canvasTarget[0] = Table.None;
        s_canvasRow[0] = new CanvasEdge(Own(c.Url), Own(c.Poster), Own(c.CanvasUri), Own(c.Artist), Own(c.ArtistUri), Own(c.UploadedBy),
            Own(c.Etag), c.Kind, (ushort)Math.Clamp(c.PosterW, 0, ushort.MaxValue), (ushort)Math.Clamp(c.PosterH, 0, ushort.MaxValue),
            c.FetchedAt, c.CacheTtl, c.OfflineTtl);
        edges.ReleaseCanvasText(slot);
        edges.TrackCanvas.Replace(slot, s_canvasTarget, s_canvasRow, EdgeState.Complete, 1);
        edges.CanvasNoneUntil.Remove(slot);

        static StringId Own(string text)
            => text.Length == 0 ? StringId.Empty : Retained(Intern(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The track's canvas when one is known and plays (<see cref="CanvasEdge.Plays"/>). Reads the table; allocates nothing.</summary>
    public static bool TryCanvas(int trackSlot, out CanvasEdge canvas)
    {
        canvas = default;
        var rows = Current.Edges.TrackCanvas;
        if (rows.State(trackSlot) != EdgeState.Complete) return false;
        var p = rows.Payload(trackSlot);
        if (p.Length == 0 || !p[0].Plays) return false;
        canvas = p[0];
        return true;
    }

    /// <summary>Is the parent's canvas answer due again (a row past its cache TTL, a "none" past its own, a transport failure)? Unknown
    /// and never asked is not "due": the first ask is the caller's <c>EnsureEdge</c>.</summary>
    public static bool CanvasDue(int trackSlot, long nowSec)
    {
        var edges = Current.Edges;
        var rows = edges.TrackCanvas;
        switch (rows.State(trackSlot))
        {
            case EdgeState.Complete:
                var p = rows.Payload(trackSlot);
                if (p.Length > 0) return p[0].Stale(nowSec);
                return edges.CanvasNoneUntil.TryGetValue(trackSlot, out long until) && nowSec >= until;
            default:
                return false;
        }
    }
}
