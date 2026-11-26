// ── Playback/LocalLink.Versions.cs ──────────────────────────────────────────────────────────────────────────────────
// "Your files and Spotify" phase 2: a file's own name in Wavee (rename), several files as versions of one Spotify song, and which version
// plays when the song is played (play by default, "Prefer my files")
//
// Role: SHELL (UI thread: the verbs and what a row or the drawer asks; immutable copies for the restate's thread and the audio pump)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-link-phase2.md; 240-spotify-linking-concept.md §4 US6-US8, US10, §6, §7
//
// RENAME. A Wavee-side name for one file (title, artist, album; an empty field keeps the file's own). Show-only, as phase 1's accepted matches:
// nothing is written to the file. The user's name is the strongest: rename > an accepted match's Spotify info > the tags > the file name's
// split. It changes what a row, the drawer, the collection and the Spotify search for that file show; it never changes the file's MATCHING
// names (`LocalEntry.MatchTitle` and the tags), which a playlist's `spotify:local:` item, Connect and reports use.
//
// VERSIONS. Every file whose match was accepted onto a Spotify song is a version of it (`FilesFor`), listed by its (renamed) name in the song's
// "Versions and formats"; linking another file adds a version, it never replaces one.
//
// PLAY BY DEFAULT (§7: on this PC only, in every list, playlists you follow included). Per Spotify song: Spotify's own, or one of its files.
// With no choice made, "Prefer my files" (Settings › Playback, off by default) picks the song's first linked file. The pick is read by the
// playback layer when the song is played from anywhere (`PlayAsFile`); the queue keeps the Spotify song, and what plays is reported as a
// local play (#240 P3). Reversible per song; Undo on the toast.
//
// THE FILE. Two more record kinds in `Cache\locallinks.tsv` (an older release skips them):
//   R  local-id  title  artist  album         a rename ("" keeps the file's own)
//   D  spotify-uri  local-id                  the song's default version (0: Spotify's, chosen on purpose even with "Prefer my files" on)

using System.Globalization;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>A file's Wavee-side name: each field "" keeps the file's own (see the file header).</summary>
public sealed record LocalRename(string Title, string Artist, string Album)
{
    public bool IsEmpty => Title.Length == 0 && Artist.Length == 0 && Album.Length == 0;
}

public static partial class LocalLink
{
    static readonly SettingKey<bool> s_preferFiles = new("local.link.preferFiles", false);

    static readonly Dictionary<long, LocalRename> s_renames = new();
    /// <summary>Spotify song uri → its chosen default: a LocalId, or 0 for Spotify's own.</summary>
    static readonly Dictionary<string, long> s_defaults = new(StringComparer.Ordinal);

    static volatile Dictionary<long, LocalRename> s_renamesShown = new();
    static volatile Dictionary<string, long> s_playAs = new(StringComparer.Ordinal);
    static readonly Signal<bool> s_preferFilesOn = new(false);

    /// <summary>"Prefer my files when I have them" (Settings › Playback; off by default).</summary>
    public static SettingKey<bool> PreferFilesKey => s_preferFiles;

    /// <summary>"Prefer my files" now (tracked).</summary>
    public static IReadSignal<bool> PreferFiles => s_preferFilesOn;

    /// <summary>LocalId → its rename. Immutable; ANY thread (the restate's pass, the collection's fold).</summary>
    public static IReadOnlyDictionary<long, LocalRename> Renames => s_renamesShown;

    /// <summary>A file's rename, or null. Any thread.</summary>
    public static LocalRename? RenameOf(long localId) => s_renamesShown.TryGetValue(localId, out LocalRename? r) ? r : null;

    /// <summary>Spotify song uri → the file that plays when the song is played (its default, or "Prefer my files"' pick). Immutable; ANY thread
    /// (the playback layer asks it when it opens a track).</summary>
    public static IReadOnlyDictionary<string, long> PlayAs => s_playAs;

    /// <summary>The file that plays for <paramref name="spotifyUri"/>, or 0 (Spotify plays). Any thread.</summary>
    public static long PlayAsFile(string spotifyUri) => s_playAs.TryGetValue(spotifyUri, out long id) ? id : 0;

    /// <summary>The version a song plays by default: a LocalId, or 0 for Spotify's own (tracked: reads <see cref="Version"/>).</summary>
    public static long DefaultOf(string spotifyUri)
    {
        _ = s_version.Value;
        return PlayAsFile(spotifyUri);
    }

    /// <summary>The user chose this song's default on purpose (else "Prefer my files" or Spotify decides).</summary>
    public static bool HasChosenDefault(string spotifyUri) => s_defaults.ContainsKey(spotifyUri);

    // ── the verbs (UI thread) ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rename a file in Wavee (null or all-empty: back to the file's own names). The row, the drawer, the collection and the toast's Undo
    /// follow at once; nothing is written to the file.</summary>
    public static void Rename(long localId, LocalRename? rename)
    {
        LocalRename? before = s_renames.TryGetValue(localId, out LocalRename? r) ? r : null;
        if (rename is null || rename.IsEmpty) { if (!s_renames.Remove(localId)) return; }
        else if (before == rename) return;
        else s_renames[localId] = rename;
        Publish();
        Save();
    }

    /// <summary>Choose which version plays when <paramref name="spotifyUri"/> is played: one of its files (<paramref name="localId"/>), or
    /// Spotify's own (0). <paramref name="clear"/>: forget the choice ("Prefer my files" decides again).</summary>
    public static void SetDefault(string spotifyUri, long localId, bool clear = false)
    {
        if (clear) { if (!s_defaults.Remove(spotifyUri)) return; }
        else
        {
            if (localId != 0 && Array.IndexOf(FilesOf(spotifyUri), localId) < 0) return;   // only a version of this song
            if (s_defaults.TryGetValue(spotifyUri, out long was) && was == localId) return;
            s_defaults[spotifyUri] = localId;
        }
        Publish();
        Save();
    }

    /// <summary>Settings › Playback › "Prefer my files when I have them".</summary>
    public static void SetPreferFiles(bool on)
    {
        Platform.Settings.Set(s_preferFiles, on);
        s_preferFilesOn.Value = on;
        Publish();
    }

    static long[] FilesOf(string spotifyUri) => s_byTrack.TryGetValue(spotifyUri, out long[]? ids) ? ids : [];

    // ── the copies other threads read ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rebuild the rename and play-as copies (from <see cref="RebuildShown"/>); true when either moved.</summary>
    static bool RebuildVersions()
    {
        bool moved = false;
        if (!SameMap(s_renames, s_renamesShown))
        {
            s_renamesShown = new Dictionary<long, LocalRename>(s_renames);
            moved = true;
        }
        bool prefer = s_preferFilesOn.Peek();
        var playAs = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (uri, files) in s_byTrack)
        {
            if (files.Length == 0) continue;
            if (s_defaults.TryGetValue(uri, out long chosen))
            {
                if (chosen != 0 && Array.IndexOf(files, chosen) >= 0) playAs[uri] = chosen;
            }
            else if (prefer) playAs[uri] = files[0];
        }
        if (!SameMap(playAs, s_playAs)) { s_playAs = playAs; moved = true; }
        return moved;

        static bool SameMap<TKey, TValue>(Dictionary<TKey, TValue> a, Dictionary<TKey, TValue> b) where TKey : notnull
        {
            if (a.Count != b.Count) return false;
            foreach (var (k, v) in a) if (!b.TryGetValue(k, out TValue? w) || !EqualityComparer<TValue>.Default.Equals(v, w)) return false;
            return true;
        }
    }

    // ── the file's two record kinds ──────────────────────────────────────────────────────────────────────────────────

    static bool ReadVersions(string[] f)
    {
        switch (f[0])
        {
            case "R" when f.Length >= 5 && long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out long id):
                var r = new LocalRename(f[2], f[3], f[4]);
                if (!r.IsEmpty) s_renames[id] = r;
                return true;
            case "D" when f.Length >= 3 && long.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out long file):
                s_defaults[f[1]] = file;
                return true;
            case "T" when f.Length >= 3 && long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out long video)
                         && long.TryParse(f[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long choice) && choice != 0:
                s_thumbs[video] = choice;   // "Use as thumbnail" (LocalLink.Attach.cs)
                return true;
        }
        return false;
    }

    static void WriteVersions(System.Text.StringBuilder sb, Func<string, string> clean)
    {
        foreach (var (id, r) in s_renames)
            sb.Append('R').Append('\t').Append(id.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(clean(r.Title)).Append('\t')
              .Append(clean(r.Artist)).Append('\t').Append(clean(r.Album)).Append('\n');
        foreach (var (uri, file) in s_defaults)
            sb.Append('D').Append('\t').Append(clean(uri)).Append('\t').Append(file.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var (video, choice) in s_thumbs)
            sb.Append('T').Append('\t').Append(video.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(choice.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }
}
