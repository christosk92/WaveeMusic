// ── Playback/Playback.Video.SeekPanels.cs ──────────────────────────────────────────────────────────────────────────
// the seek-preview sheets of a video: what Spotify's v9 manifest says under `seekpanels`, and the lookup a seek tip needs
// (a time → the sheet and the cell that hold its frame). NAMED PARTIAL of Playback/Playback.Video.cs.
//
// Role: CORE (pure: no I/O, no signal, no engine type)
// Spec: docs/plans/wavee/video-seek-preview-implementation.md
//
// THE WIRE (measured 2026-11-16 on three PlayReady music videos and four clear video podcast episodes):
//
//   "seekpanels": {
//     "base_urls": ["https://seekpanels.spotifycdn.com/"],
//     "templates": ["seekpanels/v1/origins/…/sources/…/…/{{variant_id}}/{{panel_id}}.jpg?__token__=…&fauth=…"],
//     "variants":  [{ "id": "0", "mime_type": "image/jpeg", "frame_width": 180, "frame_height": 100, "rows": 6, "columns": 6,
//                     "frames": { "frame_deltas_in_seconds": { "values": [0, 1, 1, …] } } },
//                   { "id": "1", …, "frame_width": 452, "frame_height": 254, "rows": 3, "columns": 3, … }]
//   }
//
// FRAME i shows the video at the running sum of the deltas (a music video steps 1 s, a podcast 8 or 10 s). It lives in panel
// i / (rows × columns), numbered from 0, in reading order; the LAST panel is cropped to the rows it uses; panel N answers 404.
// The url needs nothing but its own signed query (no bearer, no Origin). `spritemaps` in the same manifest is NOT this: it is one
// poster frame.

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Playback
{
    public static partial class Video
    {
        /// <summary>The seek-preview sheets of one video source. Immutable; safe to read from any thread.</summary>
        public sealed class SeekPanels
        {
            const string VariantToken = "{{variant_id}}", PanelToken = "{{panel_id}}";

            /// <summary>One size of sheet. <paramref name="TimesSec"/> is the time each frame shows, ascending.</summary>
            public sealed record Variant(string Id, int FrameW, int FrameH, int Rows, int Columns, int[] TimesSec)
            {
                public int PerPanel => Rows * Columns;
                public int Frames => TimesSec.Length;
                public int Panels => (Frames + PerPanel - 1) / PerPanel;

                /// <summary>The rows panel <paramref name="panel"/> really holds: the last one is cropped to the rows it uses.</summary>
                public int RowsIn(int panel)
                {
                    int left = Frames - panel * PerPanel;
                    return left >= PerPanel ? Rows : Math.Max(1, (left + Columns - 1) / Columns);
                }

                /// <summary>The frame shown for <paramref name="ms"/>: the LAST one at or before it (never one from the future).</summary>
                public int FrameAt(long ms)
                {
                    long sec = Math.Max(0L, ms) / 1000L;
                    int lo = 0, hi = TimesSec.Length - 1;
                    while (lo < hi)
                    {
                        int mid = (lo + hi + 1) >>> 1;
                        if (TimesSec[mid] <= sec) lo = mid; else hi = mid - 1;
                    }
                    return lo;
                }
            }

            readonly string _head, _between, _tail;   // the url around the two tokens, split once
            readonly bool _variantFirst;

            /// <summary>What a mounted tip is keyed on: two sources never share it.</summary>
            public string Key { get; }

            /// <summary>The sheets, coarsest first (the most frames per panel: the fewest fetches).</summary>
            public Variant[] Variants { get; }

            /// <summary>The variant a sweep across the rail draws from: the most frames per panel, so a whole video is a handful of sheets.</summary>
            public Variant Coarse => Variants[0];

            /// <summary>The variant a resting pointer sharpens to: the largest frame.</summary>
            public Variant Fine { get; }

            /// <summary>Frame width ÷ height (the tip's picture keeps it).</summary>
            public float Aspect => Fine.FrameH > 0 ? Fine.FrameW / (float)Fine.FrameH : 16f / 9f;

            public SeekPanels(string key, string urlTemplate, Variant[] variants)
            {
                Key = key;
                Array.Sort(variants, static (a, b) => b.PerPanel != a.PerPanel ? b.PerPanel - a.PerPanel : a.FrameW - b.FrameW);
                Variants = variants;
                Variant fine = variants[0];
                foreach (Variant v in variants) if (v.FrameW > fine.FrameW) fine = v;
                Fine = fine;

                int vAt = urlTemplate.IndexOf(VariantToken, StringComparison.Ordinal);
                int pAt = urlTemplate.IndexOf(PanelToken, StringComparison.Ordinal);
                _variantFirst = vAt >= 0 && (pAt < 0 || vAt < pAt);
                int first = _variantFirst ? vAt : pAt, firstLen = _variantFirst ? VariantToken.Length : PanelToken.Length;
                int second = _variantFirst ? pAt : vAt, secondLen = _variantFirst ? PanelToken.Length : VariantToken.Length;
                if (first < 0) { _head = urlTemplate; _between = _tail = ""; return; }
                _head = urlTemplate[..first];
                if (second < 0) { _between = ""; _tail = urlTemplate[(first + firstLen)..]; return; }
                _between = urlTemplate[(first + firstLen)..second];
                _tail = urlTemplate[(second + secondLen)..];
            }

            /// <summary>Where sheet <paramref name="panel"/> of <paramref name="variant"/> is fetched from (or read from, for a file template).</summary>
            public string Url(Variant variant, int panel)
            {
                string p = panel.ToString(CultureInfo.InvariantCulture);
                return _variantFirst ? string.Concat(_head, variant.Id, _between, p, _tail) : string.Concat(_head, p, _between, variant.Id, _tail);
            }

            /// <summary>Read the manifest's <c>seekpanels</c> block. Null when it is absent or carries nothing usable: a video
            /// without sheets simply has no picture in its seek tip.</summary>
            public static SeekPanels? Parse(JsonElement root, string key)
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("seekpanels", out JsonElement block)
                    || block.ValueKind != JsonValueKind.Object) return null;
                string baseUrl = First(block, "base_urls"), template = First(block, "templates");
                if (template.Length == 0 || !block.TryGetProperty("variants", out JsonElement variants)
                    || variants.ValueKind != JsonValueKind.Array) return null;

                var read = new List<Variant>(2);
                foreach (JsonElement v in variants.EnumerateArray())
                {
                    if (v.ValueKind != JsonValueKind.Object) continue;
                    string id = v.TryGetProperty("id", out JsonElement idEl)
                        ? idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : idEl.GetRawText()
                        : "";
                    int fw = Int(v, "frame_width"), fh = Int(v, "frame_height"), rows = Int(v, "rows"), cols = Int(v, "columns");
                    if (id.Length == 0 || fw <= 0 || fh <= 0 || rows <= 0 || cols <= 0) continue;
                    if (!v.TryGetProperty("frames", out JsonElement frames) || frames.ValueKind != JsonValueKind.Object
                        || !frames.TryGetProperty("frame_deltas_in_seconds", out JsonElement deltas)
                        || deltas.ValueKind != JsonValueKind.Object
                        || !deltas.TryGetProperty("values", out JsonElement values) || values.ValueKind != JsonValueKind.Array) continue;
                    var times = new int[values.GetArrayLength()];
                    int at = 0, n = 0;
                    foreach (JsonElement d in values.EnumerateArray())
                    {
                        at += d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out int step) ? Math.Max(0, step) : 0;
                        times[n++] = at;
                    }
                    if (n > 0) read.Add(new Variant(id, fw, fh, rows, cols, times));
                }
                if (read.Count == 0) return null;

                string url = template.StartsWith("http", StringComparison.OrdinalIgnoreCase) || baseUrl.Length == 0 ? template
                    : baseUrl.EndsWith('/') || template.StartsWith('/') ? baseUrl + template : baseUrl + "/" + template;
                return new SeekPanels(key, url, [.. read]);
            }

            /// <summary>Evenly spaced sheets (one frame every <paramref name="stepSec"/> seconds) in two sizes: what a fixture or a
            /// locally generated set describes itself with.</summary>
            public static SeekPanels Uniform(string key, string urlTemplate, int durationSec, int stepSec,
                                             (string Id, int FrameW, int FrameH, int Rows, int Columns)[] sizes)
            {
                int frames = Math.Max(1, durationSec / Math.Max(1, stepSec) + 1);
                var variants = new Variant[sizes.Length];
                for (int i = 0; i < sizes.Length; i++)
                {
                    var times = new int[frames];
                    for (int f = 0; f < frames; f++) times[f] = f * stepSec;
                    variants[i] = new Variant(sizes[i].Id, sizes[i].FrameW, sizes[i].FrameH, sizes[i].Rows, sizes[i].Columns, times);
                }
                return new SeekPanels(key, urlTemplate, variants);
            }

            static string First(JsonElement e, string name)
                => e.TryGetProperty(name, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
                    ? arr[0].GetString() ?? "" : "";

            static int Int(JsonElement e, string name)
                => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
        }
    }
}
