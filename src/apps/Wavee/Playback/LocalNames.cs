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

/// <summary>What a file's row shows: its title, its credit line, its album, and a cover that is not the file's own (an accepted match's).</summary>
public readonly record struct LocalFace(string Title, string Artists, string Album, string? Image);

/// <summary>The display split of an untagged file's name, and the one rule of what a file shows (see the file header).</summary>
public static class LocalNames
{
    /// <summary>THE rule of what a file in the music folders shows, used by its row, its drawer, the collection and the Spotify search for it
    /// ("Your files and Spotify" phase 2): the user's rename (each field it sets) &gt; an accepted match's Spotify info (when Spotify's info
    /// wins, <paramref name="shown"/>) &gt; the tags &gt; the file name's "Artist - Title" split. "Unknown artist" is the row's own fallback after
    /// it (<c>Track.LocalCredit</c>). Display only: the file's matching names (<see cref="LocalEntry.MatchTitle"/>, the tags) never change.</summary>
    public static LocalFace Face(LocalEntry e, LinkTrack? shown, LocalRename? rename)
    {
        LocalTagSet t = e.Tags;
        string title = e.MatchTitle, artists = Playlist.LocalArtistLine(in t), album = t.Album;
        string? image = null;
        if (shown is not null) (title, artists, album, image) = (shown.Title, shown.Artists, shown.Album, shown.Cover);
        else if (artists.Length == 0) (title, artists) = Display(in t, e.MatchTitle);
        if (rename is not null)
        {
            if (rename.Title.Length > 0) title = rename.Title;
            if (rename.Artist.Length > 0) artists = rename.Artist;
            if (rename.Album.Length > 0) album = rename.Album;
        }
        return new LocalFace(title, artists, album, image);
    }

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
