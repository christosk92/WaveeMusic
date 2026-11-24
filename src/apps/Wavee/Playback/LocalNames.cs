// ── Playback/LocalNames.cs ──────────────────────────────────────────────────────────────────────────────────────────
// how an untagged file's name reads in Wavee: "Nickelback - Savin' Me" shows "Savin' Me" by "Nickelback" (#240 "Your files and Spotify")
//
// Role: PURE (any thread)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-spotify-linking-concept.md (the casual listener: "local songs in playlists that look normal")
//
// DISPLAY ONLY. A file with no title tag is titled by its file name (`LocalTags.TitleOrFileName`), and that name stays the file's
// MATCHING title (`LocalEntry.MatchTitle`): the official client keys a playlist's `spotify:local:` item on it, so a playlist row must keep
// finding the file by its whole name. What changes is what a row SHOWS when the file also names no artist: a name of the common
// "Artist - Title" shape splits at its first spaced dash (" - ", " – ", " — "). A leading track number ("01 - Intro") is a number, not an
// artist: it is dropped and the rest is the title. Anything else shows whole.

namespace Wavee;

/// <summary>The display split of an untagged file's name (see the file header).</summary>
public static class LocalNames
{
    static readonly string[] s_dashes = [" - ", " – ", " — "];

    /// <summary>What a file shows: its tags when it has a title tag or an artist, else its file name split as "Artist - Title".
    /// <paramref name="matchTitle"/> is the file's matching title (the tag, or the file name).</summary>
    public static (string Title, string Artist) Display(in LocalTagSet tags, string matchTitle)
    {
        if (tags.Title.Length > 0 || tags.Artists.Length > 0) return (matchTitle, tags.FirstArtist);
        return Split(matchTitle);
    }

    /// <summary>"Artist - Title" → (Title, Artist); "01 - Title" → (Title, ""); anything else → (name, "").</summary>
    public static (string Title, string Artist) Split(string name)
    {
        foreach (string dash in s_dashes)
        {
            int at = name.IndexOf(dash, StringComparison.Ordinal);
            if (at <= 0) continue;
            string left = name[..at].Trim(), right = name[(at + dash.Length)..].Trim();
            if (left.Length == 0 || right.Length == 0) return (name, "");
            bool number = true;
            foreach (char c in left) if (!char.IsAsciiDigit(c)) { number = false; break; }
            return number ? (right, "") : (right, left);
        }
        return (name, "");
    }
}
