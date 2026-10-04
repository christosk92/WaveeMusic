// ── Shell/Lyrics.Fixtures.cs ──────────────────────────────────────────────────────────────────────────────────────
// A deterministic WORD-SYNCED lyrics document for the `--fake` backend's tracks, so the karaoke surfaces (the rail's
// lyrics view, the now-playing peek, the stage caption) run offline exactly as they do on a word-synced Spotify track:
// a wipe that sweeps every line, handoffs, and one long interlude for the breathing dots.
//
// Role: fixture (installed ONLY by App.cs under --fake through Lyrics.Store.Fixture; real mode never sees it)
//
// Shape: lines start at 4 s and run to 128 s — under the shortest seeded track (138 s), so no line outlives its track.
// Each line lasts 3.0–4.3 s (varied, never uniform), carries 5–8 words timed in reading order with short gaps, and the
// gap after line 12 is 6 s (an interlude past Lyrics.InterludeGapMs). Built from the track index alone: no Random.
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

namespace Wavee;

public static partial class Lyrics
{
    internal static class Fixtures
    {
        static readonly string[] Words =
        [
            "city", "lights", "falling", "over", "the", "river", "we", "keep", "running", "into", "morning", "slow",
            "hold", "on", "to", "every", "word", "you", "said", "under", "a", "paper", "moon", "tonight", "and",
            "the", "radio", "plays", "our", "song", "again", "so", "turn", "it", "up", "until", "the", "windows", "glow",
        ];

        const long FirstMs = 4_000, LastEndMs = 128_000, InterludeAfter = 12, InterludeMs = 6_000;

        /// <summary>The fixture document for a seeded track id (<c>tr0</c>..<c>tr165</c>), or null for anything else.</summary>
        public static Doc? For(string trackId)
        {
            if (trackId.Length < 3 || !trackId.StartsWith("tr", StringComparison.Ordinal)
                || !int.TryParse(trackId.AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index))
                return null;
            var lines = new List<Line>(40);
            long at = FirstMs;
            for (int k = 0; at < LastEndMs - 3_000; k++)
            {
                long duration = 3_000 + (k * 370 + index * 53) % 1_300;
                int count = 5 + (k + index) % 4;
                var syllables = new List<Syllable>(count);
                var text = new System.Text.StringBuilder();
                long slot = duration / count;
                for (int w = 0; w < count; w++)
                {
                    string word = Words[(k * 7 + w * 3 + index) % Words.Length] + (w + 1 < count ? " " : "");
                    long start = at + w * slot, end = start + slot * 85 / 100;
                    syllables.Add(new Syllable(start, end, word));
                    text.Append(word);
                }
                long lineEnd = at + duration;
                lines.Add(new Line(at, text.ToString(), syllables, lineEnd, IsWordByWord: true));
                at = lineEnd + (k == InterludeAfter ? InterludeMs : 250);
            }
            return new Doc(trackId, IsSynced: true, lines, SyncKind.Syllable, Provider: "fixture");
        }
    }
}
