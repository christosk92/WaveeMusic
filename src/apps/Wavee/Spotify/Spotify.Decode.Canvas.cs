// ── Spotify/Spotify.Decode.Canvas.cs — extension kind 16 CANVAS_V1 → Edges.TrackCanvas ──────────────────────────────────────────────────
//
// Role: CORE · a named partial of Spotify.Decode.cs, in that file's one shape: pure over a span, rows into a `Staging`, the commit's
// business after that (Entities/Canvas.cs).
//
// THE PAYLOAD (Spotify/Protos/canvaz.proto, `spotify.canvaz.cache.EntityCanvazResponse.Canvaz`, in the envelope's Any):
//   1 id · 2 url (the .cnvs.mp4) · 3 file_id · 4 type (0 IMAGE, 1 VIDEO, 2 VIDEO_LOOPING, 3 VIDEO_LOOPING_RANDOM, 4 GIF) · 5 entity_uri
//   6 artist { 1 uri, 2 name, 3 avatar } · 7 explicit · 8 uploaded_by · 9 etag · 11 canvas_uri · 12 storylines_id
//   13 thumbnail, repeated { 1 height, 2 width, 3 url } — NEW since librespot's 1.2.52 proto (the 2026-11 captures: 288x512 and 144x256).
// The envelope's uri is the identity (the payload's own entity_uri is not read), as for every trait.
//
// NONE. Pathfinder answers `canvas: null` for a track without one; on kind 16 that is expected as an entity with no extension_data (not
// observed yet). `CanvasNegatives` stages "none" for every asked track the answer left out, named with no data, or ruled with a terminal
// status; an image, a GIF, an unknown type or a payload with no url is staged as none too. A retryable status (5xx / 429) stages nothing.
//
// THE FIRST REAL ANSWER IS LOGGED, once per process per verdict (`canvas.answer`), so the owner's first live run confirms the shape this
// was written against: track, type, url host, thumbnails, the TTLs — or, for a none, how the server said it.

using System.Globalization;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        static int s_canvasLoggedPositive, s_canvasLoggedNone;

        /// <summary>Kind 16 → one <see cref="StagedCanvas"/>. <paramref name="cacheTtl"/> and <paramref name="offlineTtl"/> are the entity
        /// header's (0 = not stated: the commit takes the captured defaults).</summary>
        public static void Canvas(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, int cacheTtl, int offlineTtl, Staging s)
        {
            var track = Identity(s, entityUri);
            if (track.IsEmpty || !entityUri.StartsWith("spotify:track:"u8)) return;
            ReadOnlySpan<byte> url = default, canvasUri = default, uploadedBy = default, etag = default, artistUri = default,
                               artistName = default, poster = default;
            int type = -1;
            long posterArea = -1;
            int posterW = 0, posterH = 0, thumbs = 0;
            var r = new ProtoReader(proto);
            while (r.Next())
            {
                switch (r.Field)
                {
                    case 2 when r.Wire == 2: url = r.Bytes(); break;
                    case 4 when r.Wire == 0: type = (int)Math.Clamp((long)r.Varint(), -1, 255); break;
                    case 6 when r.Wire == 2: r.Message().Fields(1, 2, out artistUri, out artistName); break;
                    case 8 when r.Wire == 2: uploadedBy = r.Bytes(); break;
                    case 9 when r.Wire == 2: etag = r.Bytes(); break;
                    case 11 when r.Wire == 2: canvasUri = r.Bytes(); break;
                    case 13 when r.Wire == 2:
                        {
                            var t = r.Message();
                            long h = 0, w = 0;
                            ReadOnlySpan<byte> u = default;
                            while (t.Next())
                            {
                                if (t.Field == 1 && t.Wire == 0) h = (long)t.Varint();
                                else if (t.Field == 2 && t.Wire == 0) w = (long)t.Varint();
                                else if (t.Field == 3 && t.Wire == 2) u = t.Bytes();
                                else t.Skip();
                            }
                            if (u.IsEmpty) break;
                            thumbs++;
                            if (w * h > posterArea) { posterArea = w * h; poster = u; posterW = (int)Math.Min(w, ushort.MaxValue); posterH = (int)Math.Min(h, ushort.MaxValue); }
                            break;
                        }
                    default: r.Skip(); break;
                }
            }
            // A proto3 enum at its zero (IMAGE) is not written on the wire: an absent type with a url reads as IMAGE, which is right.
            var kind = (CanvasKind)Math.Max(0, type);
            bool plays = !url.IsEmpty && type is 1 or 2 or 3;
            ref var c = ref s.Canvases.Add();
            c.Track = track;
            c.CacheTtl = cacheTtl;
            c.OfflineTtl = offlineTtl;
            if (!plays)
            {
                c.None = true;
                LogCanvas(entityUri, "none", "type=" + type.ToString(CultureInfo.InvariantCulture) + " url=" + (url.IsEmpty ? "absent" : "present"));
                return;
            }
            c.Url = s.AddText(url);
            c.Poster = s.AddText(poster);
            c.CanvasUri = s.AddText(canvasUri);
            c.Artist = s.AddText(artistName);
            c.ArtistUri = s.AddText(artistUri);
            c.UploadedBy = s.AddText(uploadedBy);
            c.Etag = s.AddText(etag);
            c.Kind = kind;
            c.PosterW = (ushort)posterW;
            c.PosterH = (ushort)posterH;
            if (Volatile.Read(ref s_canvasLoggedPositive) == 0 && Interlocked.Exchange(ref s_canvasLoggedPositive, 1) == 0)
                LogCanvas(entityUri, kind.ToString(),
                    "url-host=" + HostOf(url) + " thumbs=" + thumbs.ToString(CultureInfo.InvariantCulture)
                    + " poster=" + posterW.ToString(CultureInfo.InvariantCulture) + "x" + posterH.ToString(CultureInfo.InvariantCulture)
                    + " uploaded-by=" + System.Text.Encoding.UTF8.GetString(uploadedBy)
                    + " ttl=" + cacheTtl.ToString(CultureInfo.InvariantCulture) + " offline-ttl=" + offlineTtl.ToString(CultureInfo.InvariantCulture)
                    + " bytes=" + proto.Length.ToString(CultureInfo.InvariantCulture), positive: true);
        }

        /// <summary>The kind-16 "none" verdicts of an EDGE batch: every asked track the answer left out, named with a 2xx header and no
        /// data, or ruled with a terminal status, is staged as "no canvas" — an empty Complete run, not the door's NoRoute vacancy, so its
        /// own TTL (the default) brings it back. A retryable status is this request being unlucky and stages nothing. Allocates (an api
        /// worker thread, per answer).</summary>
        static void CanvasNegatives(ReadOnlySpan<byte> response, Staging s, FetchBatch asked)
        {
            if (asked.Subject != FetchSubject.Edge || asked.Edge != FetchEdge.TrackCanvas || asked.Count == 0) return;
            var uris = new string[asked.Count];
            for (int i = 0; i < uris.Length; i++) uris[i] = asked.Uri(i);
            ReadOnlySpan<int> kinds = [FetchRoutes.CanvasV1];
            var summary = XmAnswerSummary.Read(response, uris, kinds);
            foreach (var row in summary.Rows)
            {
                bool terminal = row.Verdict == XmAnswerSummary.Verdict.Failed && ExtendedMetadataRules.TerminalEntry(row.Status);
                if (!terminal && row.Verdict is not (XmAnswerSummary.Verdict.Omitted or XmAnswerSummary.Verdict.Empty)) continue;
                int at = Array.IndexOf(uris, row.Uri);
                if (at < 0) continue;
                var id = new StagedId(asked.Ids[at]);
                if (id.IsEmpty) continue;
                ref var c = ref s.Canvases.Add();
                c.Track = id;
                c.None = true;
                LogCanvas(System.Text.Encoding.UTF8.GetBytes(row.Uri), "none",
                    "verdict=" + row.Verdict + " status=" + row.Status.ToString(CultureInfo.InvariantCulture));
            }
        }

        static void LogCanvas(ReadOnlySpan<byte> track, string type, string rest, bool positive = false)
        {
            if (!positive && (Volatile.Read(ref s_canvasLoggedNone) != 0 || Interlocked.Exchange(ref s_canvasLoggedNone, 1) != 0)) return;
            Log.Info("canvas", "canvas.answer track=" + System.Text.Encoding.UTF8.GetString(track) + " type=" + type + " " + rest);
        }

        static string HostOf(ReadOnlySpan<byte> url)
        {
            string u = System.Text.Encoding.UTF8.GetString(url);
            return Uri.TryCreate(u, UriKind.Absolute, out var parsed) ? parsed.Host : "unparsed";
        }
    }
}
