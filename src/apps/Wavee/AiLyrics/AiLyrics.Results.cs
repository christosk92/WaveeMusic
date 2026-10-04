// ── AiLyrics/AiLyrics.Results.cs ─────────────────────────────────────────────────────────────────────────────────────
// Results: the saved word timing of a finished song (plan §3.3)
//
// Role: CORE (pure: string in, string out; no I/O, no clock reads, no engine)
//
// A finished alignment is saved as `ai\lyrics\results\<trackId>.v<PackVersion>.json` so the next play is instant. The
// file holds TIMING ONLY: the words' texts are written for a human reading the file, but the document shown is always
// rebuilt over the provider's current lines (text, translation, romanization), and the syllable texts are re-cut from
// the provider's text at the word boundaries `Align.Words` finds, exactly as `Align.WordSyncedLine` cuts them, so they
// join back to the line text the karaoke wipe measures against. A different source text (hash) or line count is a
// miss: the song is aligned again.
//
//   { "v": 1, "trackId": "…", "pack": 1, "language": "en", "origin": "spotify", "sourceHash": "a1b2c3d4e5f60708",
//     "savedUnixMs": 0, "lines": [ { "s": 12340, "e": 15000, "w": [[12340,12600,"Nobody "],[12600,13100,"pray "]] } ] }
//
// The host does the file I/O (worker thread at job end, boot Task for the sweep); this file only encodes, decodes,
// overlays and picks sweep victims. Utf8JsonWriter / JsonDocument only: no reflection serialization (NativeAOT).

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Wavee;

public static partial class AiLyrics
{
    public static class Results
    {
        /// <summary>The file format's version (the <c>v</c> field). A file of another version is a miss.</summary>
        public const int FormatVersion = 1;

        /// <summary>The sweep keeps at most this many result files…</summary>
        public const int MaxFiles = 500;

        /// <summary>…and at most this many bytes of them (20 MB).</summary>
        public const long MaxBytes = 20L * 1024 * 1024;

        /// <summary>One timed word: its span and the text it had when saved (informational; the overlay re-cuts the
        /// text from the provider's line).</summary>
        public readonly record struct ResultWord(long StartMs, long EndMs, string Text);

        /// <summary>One line: its start, its end (null when the line had none) and its words (empty when the line was
        /// not word-timed, e.g. a line without words).</summary>
        public sealed record ResultLine(long StartMs, long? EndMs, IReadOnlyList<ResultWord> Words);

        /// <summary>A decoded results file.</summary>
        /// <param name="SourceHash">The host's hash of the provider document the timing was generated over.</param>
        public sealed record ResultDoc(
            string TrackId,
            int Pack,
            string? Language,
            string? Origin,
            ulong SourceHash,
            long SavedUnixMs,
            IReadOnlyList<ResultLine> Lines);

        /// <summary>The file name of a track's results for a pack version: <c>&lt;trackId&gt;.v&lt;pack&gt;.json</c>. A
        /// track id that is not plain ASCII letters/digits (a local or podcast id) is replaced by its SHA-256 hex, so
        /// the name is always filesystem-safe. A case-only alias of a base62 id on a case-insensitive filesystem reads
        /// back as the other track's file, which <see cref="Overlay"/> refuses (track id check) — a miss, never wrong
        /// timing.</summary>
        public static string FileName(string trackId, int packVersion)
            => Stem(trackId) + ".v" + packVersion.ToString(CultureInfo.InvariantCulture) + ".json";

        static string Stem(string trackId)
        {
            bool plain = trackId.Length is > 0 and <= 128;
            for (int i = 0; plain && i < trackId.Length; i++) plain = char.IsAsciiLetterOrDigit(trackId[i]);
            return plain ? trackId : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(trackId)));
        }

        // ── encode ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The results file for a finished document (the job's final <c>BuildDoc</c>). Every line is written,
        /// so the line count is the provider's; a line carries words only when it is word-by-word.</summary>
        public static string Encode(Lyrics.Doc final, ulong sourceHash, long savedUnixMs)
        {
            var buffer = new ArrayBufferWriter<byte>(4096);
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteNumber("v", FormatVersion);
                w.WriteString("trackId", final.TrackId);
                w.WriteNumber("pack", PackVersion);
                if (final.Language is not null) w.WriteString("language", final.Language);
                if (final.Origin is not null) w.WriteString("origin", final.Origin);
                w.WriteString("sourceHash", sourceHash.ToString("x16", CultureInfo.InvariantCulture));
                w.WriteNumber("savedUnixMs", savedUnixMs);
                w.WriteStartArray("lines");
                foreach (var line in final.Lines)
                {
                    w.WriteStartObject();
                    w.WriteNumber("s", line.StartMs);
                    if (line.EndMs is long e) w.WriteNumber("e", e);
                    if (line.IsWordByWord && line.Syllables.Count > 0)
                    {
                        w.WriteStartArray("w");
                        foreach (var syl in line.Syllables)
                        {
                            w.WriteStartArray();
                            w.WriteNumberValue(syl.StartMs);
                            w.WriteNumberValue(syl.EndMs);
                            w.WriteStringValue(syl.Text);
                            w.WriteEndArray();
                        }
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        // ── decode ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Parses a results file. False for anything malformed, of another format version, or with a word
        /// that ends before it starts — the caller treats that as a miss (and may delete the file).</summary>
        public static bool TryDecode(string json, [NotNullWhen(true)] out ResultDoc? r)
        {
            r = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                if (!Int(root, "v", out long ver) || ver != FormatVersion) return false;
                if (!Str(root, "trackId", out string? trackId) || trackId is not { Length: > 0 }) return false;
                if (!Int(root, "pack", out long pack) || pack is < int.MinValue or > int.MaxValue) return false;
                if (!OptStr(root, "language", out string? language) || !OptStr(root, "origin", out string? origin)) return false;
                if (!Str(root, "sourceHash", out string? hashText) || hashText is null
                    || !ulong.TryParse(hashText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hash)) return false;
                if (!Int(root, "savedUnixMs", out long saved)) return false;
                if (!root.TryGetProperty("lines", out var linesEl) || linesEl.ValueKind != JsonValueKind.Array) return false;

                var lines = new List<ResultLine>(linesEl.GetArrayLength());
                foreach (var le in linesEl.EnumerateArray())
                {
                    if (le.ValueKind != JsonValueKind.Object || !Int(le, "s", out long ls)) return false;
                    long? lend = null;
                    if (le.TryGetProperty("e", out var eEl) && eEl.ValueKind != JsonValueKind.Null)
                    {
                        if (eEl.ValueKind != JsonValueKind.Number || !eEl.TryGetInt64(out long ev)) return false;
                        lend = ev;
                    }
                    IReadOnlyList<ResultWord> words = [];
                    if (le.TryGetProperty("w", out var wEl) && wEl.ValueKind != JsonValueKind.Null)
                    {
                        if (wEl.ValueKind != JsonValueKind.Array) return false;
                        var list = new List<ResultWord>(wEl.GetArrayLength());
                        foreach (var we in wEl.EnumerateArray())
                        {
                            if (we.ValueKind != JsonValueKind.Array || we.GetArrayLength() != 3) return false;
                            var ws = we[0]; var wend = we[1]; var wt = we[2];
                            if (ws.ValueKind != JsonValueKind.Number || !ws.TryGetInt64(out long s)) return false;
                            if (wend.ValueKind != JsonValueKind.Number || !wend.TryGetInt64(out long e)) return false;
                            if (wt.ValueKind != JsonValueKind.String || e < s) return false;
                            list.Add(new ResultWord(s, e, wt.GetString() ?? ""));
                        }
                        words = list;
                    }
                    lines.Add(new ResultLine(ls, lend, words));
                }
                r = new ResultDoc(trackId, (int)pack, language, origin, hash, saved, lines);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        static bool Int(JsonElement obj, string name, out long value)
        {
            value = 0;
            return obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out value);
        }

        static bool Str(JsonElement obj, string name, out string? value)
        {
            value = null;
            if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return false;
            value = el.GetString();
            return true;
        }

        /// <summary>An optional string: absent or null is fine (null); any other kind is malformed.</summary>
        static bool OptStr(JsonElement obj, string name, out string? value)
        {
            value = null;
            if (!obj.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return true;
            if (el.ValueKind != JsonValueKind.String) return false;
            value = el.GetString();
            return true;
        }

        // ── overlay ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The generated document for <paramref name="provider"/> from saved timing, or null for a miss: a
        /// different source hash, pack version or track id, a different line count, or nothing word-timed. Each line
        /// keeps the PROVIDER's text, translation and romanization; a line with saved words gets their timing, its
        /// syllable texts re-cut from the provider's text at the <see cref="Align.Words"/> boundaries (so they join back
        /// to the line text); a line without saved words — or whose word count no longer matches — stays the provider's
        /// line.</summary>
        public static Lyrics.Doc? Overlay(ResultDoc r, Lyrics.Doc provider, ulong providerHash)
        {
            if (r.SourceHash != providerHash || r.Pack != PackVersion) return null;
            if (!string.Equals(r.TrackId, provider.TrackId, StringComparison.Ordinal)) return null;
            int n = provider.Lines.Count;
            if (r.Lines.Count != n || n == 0) return null;

            var lines = new List<Lyrics.Line>(n);
            int timed = 0;
            for (int li = 0; li < n; li++)
            {
                var src = provider.Lines[li];
                var cached = r.Lines[li];
                if (cached.Words.Count == 0) { lines.Add(src); continue; }
                string text = src.Text;
                var words = Align.Words(text);
                if (words.Count != cached.Words.Count) { lines.Add(src); continue; }

                var syl = new List<Lyrics.Syllable>(words.Count);
                for (int i = 0; i < words.Count; i++)
                {
                    int from = i == 0 ? 0 : words[i].Index;
                    int to = i + 1 < words.Count ? words[i + 1].Index : text.Length;
                    var cw = cached.Words[i];
                    syl.Add(new Lyrics.Syllable(cw.StartMs, Math.Max(cw.EndMs, cw.StartMs), text.Substring(from, to - from)));
                }
                lines.Add(src with
                {
                    StartMs = syl[0].StartMs,
                    Syllables = syl,
                    EndMs = src.EndMs ?? cached.EndMs ?? syl[^1].EndMs,
                    IsWordByWord = true,
                });
                timed++;
            }
            if (timed == 0) return null;

            return provider with
            {
                Lines = lines,
                IsSynced = true,
                Sync = Lyrics.SyncKind.Syllable,
                Provider = ProviderId,
                Origin = r.Origin ?? provider.Provider,
                Language = r.Language ?? provider.Language,
                Generated = true,
                OffsetMsApplied = 0,
            };
        }

        // ── sweep ───────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The files to delete so that at most <paramref name="maxFiles"/> files and
        /// <paramref name="maxBytes"/> bytes remain: oldest last-write first (ties by path, ordinal), stopping as soon
        /// as both limits hold. Empty when they already hold.</summary>
        public static IReadOnlyList<string> SweepVictims(
            IReadOnlyList<(string Path, long Bytes, long LastWriteUnixMs)> files,
            int maxFiles = MaxFiles,
            long maxBytes = MaxBytes)
        {
            int count = files.Count;
            long total = 0;
            for (int i = 0; i < count; i++) total += Math.Max(0, files[i].Bytes);
            if (count <= maxFiles && total <= maxBytes) return [];

            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int c = files[a].LastWriteUnixMs.CompareTo(files[b].LastWriteUnixMs);
                return c != 0 ? c : string.CompareOrdinal(files[a].Path, files[b].Path);
            });

            var victims = new List<string>();
            foreach (int i in order)
            {
                if (count <= maxFiles && total <= maxBytes) break;
                victims.Add(files[i].Path);
                count--;
                total -= Math.Max(0, files[i].Bytes);
            }
            return victims;
        }
    }
}
