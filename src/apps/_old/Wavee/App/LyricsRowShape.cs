using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The PURE, engine-free rule for whether a lyrics document swap may KEEP the mounted lyric rows. Like
/// <see cref="LyricsSyncGate"/> it takes plain values and returns a decision — no <c>Signal&lt;T&gt;</c>, no FluentGpu
/// type — so it is source-included into the engine-free unit-test project.
///
/// <para>WHY THIS EXISTS. A <c>LyricLineView</c> freezes its <see cref="LyricLine"/> at mount (component props freeze at
/// mount), and the karaoke wipe, the held-note glow and the secondary line are all built from that frozen line. The
/// aggregator routinely returns a LINE-synced document first (Spotify's own transcription answers inside the first-hit
/// grace window) and publishes a WORD-synced upgrade a second or so later — and because both are scored against the same
/// reference, the upgrade's per-line TEXT is usually identical (2026-09-10 log: <c>text=1.00 lcs=69/69</c>). A "same
/// text ⇒ keep the rows" rule therefore kept rows whose frozen line had NO syllables, so the whole track played with no
/// karaoke wipe at all, while the next play of the same track (disk cache now holding the word-synced document) worked.
/// That is the "syllable lyrics work sometimes and sometimes not" report.</para>
///
/// <para>So the rows may only survive when every field a row FREEZES is identical: the text, the word-timing shape (flag,
/// syllable count and every syllable's timing/text) and both secondary layers. Anything else is a genuine document change
/// and the rows are rebuilt (the caller bumps its row key epoch), which is the path that was always correct.</para>
/// </summary>
public static class LyricsRowShape
{
    /// <summary>True when every row of <paramref name="next"/> would render byte-identically to the row already mounted
    /// for <paramref name="current"/> — i.e. the mounted rows may be kept.</summary>
    public static bool SameRows(LyricsDocument current, LyricsDocument next)
    {
        if (ReferenceEquals(current, next)) return true;
        if (!StringComparer.Ordinal.Equals(current.TrackId, next.TrackId)) return false;
        var a = current.Lines; var b = next.Lines;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!SameRow(a[i], b[i])) return false;
        return true;
    }

    /// <summary>The per-row half of the rule: everything a mounted row reads from its frozen line.</summary>
    public static bool SameRow(LyricLine a, LyricLine b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (!StringComparer.Ordinal.Equals(a.Text, b.Text)) return false;
        if (a.IsWordByWord != b.IsWordByWord) return false;
        if (!StringComparer.Ordinal.Equals(a.Translation ?? "", b.Translation ?? "")) return false;
        if (!StringComparer.Ordinal.Equals(a.Romanization ?? "", b.Romanization ?? "")) return false;
        return SameSyllables(a.Syllables, b.Syllables);
    }

    static bool SameSyllables(IReadOnlyList<LyricSyllable> a, IReadOnlyList<LyricSyllable> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.StartMs != y.StartMs || x.EndMs != y.EndMs || !StringComparer.Ordinal.Equals(x.Text, y.Text)) return false;
        }
        return true;
    }
}
