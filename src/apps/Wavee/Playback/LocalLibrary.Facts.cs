// ── Playback/LocalLibrary.Facts.cs ──────────────────────────────────────────────────────────────────────────────────
// what the surfaces show about the library: each folder's counts and state, the running scan's progress (phase, rate, time
// left), the items that need the user, and when the last scan ran
//
// Role: SHELL (UI thread readers; the counts are computed off the UI thread with each restate pass)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-files-ux.md S1 (the chip), S2 (the flyout), S3 (the folder rows),
//       S4 (the first-scan header), S6 (missing), S7 (offline)
//
// Every fact is a plain read of numbers already kept (the restate pass's per-folder counts, the scanner's live counters, the
// last scan's stats); nothing here touches a file. Surfaces read them under `LocalLibrary.Epoch` (scan progress, at most four
// times a second) and `LocalLibrary.Restated` (the list changed).

namespace Wavee;

public static partial class LocalLibrary
{
    /// <summary>One music folder as the Settings row shows it (S3, S7, S13).</summary>
    public readonly record struct FolderFacts(string Path, int Songs, int Unplayable, int Missing, int CloudOnly, LocalRootState State,
                                              bool Network, long LastScanUtcTicks, double LastScanMs, bool Scanning);

    /// <summary>The running scan (S1 chip, S2 phases, S4 header). <see cref="Active"/> false: idle.</summary>
    public readonly record struct ScanProgress(bool Active, string Folder, ScanPhase Phase, int FoldersListed, int FilesSeen, int FilesDone,
                                               int ToRead, int Read, double FilesPerSecond, double SecondsLeft, int Albums)
    {
        /// <summary>0-1 once the total is known (the listing is done or files are being read), else -1 (indeterminate).</summary>
        public double Fraction => FilesSeen <= 0 ? -1 : Math.Clamp((double)FilesDone / FilesSeen, 0, 1);
    }

    /// <summary>The phases the flyout lists (S2): listing, reading tags, matching playlists, finding covers.</summary>
    public enum ScanPhase : byte { Idle, Listing, Reading, Matching, Covers }

    /// <summary>One item that needs the user (S2 "Needs attention", S1's amber count).</summary>
    public readonly record struct AttentionItem(AttentionKind Kind, string Folder, int Count);

    public enum AttentionKind : byte { FolderOffline, SignInNeeded, Missing, Unplayable, NoAccess }

    readonly record struct Counts(int Songs, int Unplayable, int Missing, int Cloud);

    static Dictionary<int, Counts> s_counts = new();
    static int s_albums;

    /// <summary>Distinct albums in the list (the first-scan header's "212 albums so far").</summary>
    public static int Albums => Volatile.Read(ref s_albums);

    /// <summary>A folder's facts. UI thread.</summary>
    public static FolderFacts FactsOf(string folder)
    {
        int rootId = 0;
        foreach (LocalRoot r in s_index.Roots()) if (string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase)) rootId = r.RootId;
        Counts c = rootId != 0 && s_counts.TryGetValue(rootId, out Counts got) ? got : default;
        long when = 0;
        double ms = 0;
        if (s_scan.Last.TryGetValue(folder, out LocalScanStats? last)) { when = last.FinishedTicks; ms = last.TotalMs; }
        (LocalScanStats? running, string? root) = s_scan.Current;
        bool scanning = running is not null && string.Equals(root, folder, StringComparison.OrdinalIgnoreCase)
                        || StateOf(folder) == LocalRootState.Scanning;
        return new FolderFacts(folder, c.Songs, c.Unplayable, c.Missing, c.Cloud, StateOf(folder), NtDirectory.IsNetworkPath(folder), when, ms, scanning);
    }

    /// <summary>The running scan's progress (any thread; the numbers are live counters).</summary>
    public static ScanProgress Progress
    {
        get
        {
            if (s_fixture) return s_fixtureProgress;
            (LocalScanStats? s, string? root) = s_scan.Current;
            if (s is null || root is null) return default;
            int done = Volatile.Read(ref s.FilesUnchanged) + Volatile.Read(ref s.FilesRead) + Volatile.Read(ref s.CloudOnly);
            int read = Volatile.Read(ref s.FilesRead), toRead = Volatile.Read(ref s.ToRead);
            double seconds = Math.Max(0.001, (DateTime.UtcNow.Ticks - s.StartedTicks) / (double)TimeSpan.TicksPerSecond);
            double rate = read / seconds;
            int seen = Volatile.Read(ref s.FilesSeen);
            double left = rate > 0 && toRead > read ? (toRead - read) / rate : 0;
            ScanPhase phase = toRead > read || read > 0 ? ScanPhase.Reading : ScanPhase.Listing;
            return new ScanProgress(true, root, phase, Volatile.Read(ref s.FoldersListed), seen, Math.Min(done, seen), toRead, read, rate, left, Albums);
        }
    }

    static ScanProgress s_fixtureProgress;

    /// <summary>What needs the user now (S2): offline folders, folders that need a sign-in, missing songs, songs that cannot
    /// be played. UI thread.</summary>
    public static List<AttentionItem> Attention()
    {
        var items = new List<AttentionItem>();
        int missing = 0, unplayable = 0;
        foreach (Counts c in s_counts.Values) { missing += c.Missing; unplayable += c.Unplayable - c.Missing; }
        foreach (string f in Folders)
        {
            LocalRootState st = StateOf(f);
            FolderFacts facts = FactsOf(f);
            if (st == LocalRootState.Offline) items.Add(new AttentionItem(AttentionKind.FolderOffline, f, facts.Songs));
            else if (st == LocalRootState.SignInNeeded) items.Add(new AttentionItem(AttentionKind.SignInNeeded, f, facts.Songs));
            else if (st == LocalRootState.NoAccess) items.Add(new AttentionItem(AttentionKind.NoAccess, f, facts.Songs));
            foreach (string sub in UnreadableOf(f)) items.Add(new AttentionItem(AttentionKind.NoAccess, sub, 0));
        }
        if (missing > 0) items.Add(new AttentionItem(AttentionKind.Missing, "", missing));
        if (unplayable > 0) items.Add(new AttentionItem(AttentionKind.Unplayable, "", unplayable));
        return items;
    }

    /// <summary>The music folder that already holds <paramref name="path"/> (S11's "That folder is already inside …"), or null when
    /// none does (or the path is no path). UI thread; touches no file.</summary>
    public static string? ContainerOf(string path)
    {
        string folder;
        try { folder = Normalize(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        foreach (string f in Folders) if (Contains(f, folder)) return f;
        return null;
    }

    /// <summary>When the last scan of any folder finished (UTC ticks, 0 = never) and how long it took.</summary>
    public static (long UtcTicks, double Ms) LastScan
    {
        get
        {
            long when = 0;
            double ms = 0;
            foreach (LocalScanStats s in s_scan.Last.Values) if (s.FinishedTicks > when) { when = s.FinishedTicks; ms = s.TotalMs; }
            return (when, ms);
        }
    }

    /// <summary>The songs the library lists, every folder together (the last restate pass's counts). UI thread.</summary>
    public static int SongCount
    {
        get
        {
            int songs = 0;
            foreach (Counts c in s_counts.Values) songs += c.Songs;
            return songs;
        }
    }

    /// <summary>A scan runs over a folder that has never finished one (S4's first-scan header): the list is filling, not being checked.
    /// UI thread.</summary>
    public static bool FirstScanRunning
    {
        get
        {
            ScanProgress p = Progress;
            return p.Active && p.Folder.Length > 0 && FactsOf(p.Folder).LastScanUtcTicks <= 0;
        }
    }

    /// <summary>Is the music folder an entry of <paramref name="rootId"/> lives in away (offline, or a share that needs a sign-in)? UI thread.</summary>
    public static bool RootAway(int rootId)
    {
        foreach (LocalRoot r in s_index.Roots())
            if (r.RootId == rootId) return StateOf(r.Path) is LocalRootState.Offline or LocalRootState.SignInNeeded;
        return false;
    }

    /// <summary>The missing audio files (S6's Review), the longest gone first (the first to be removed). Reads a snapshot of the index: call it
    /// when the review opens, never per frame. UI thread.</summary>
    public static List<LocalEntry> MissingEntries()
    {
        var missing = new List<LocalEntry>();
        foreach (LocalEntry e in s_index.Snapshot())
            if (e.State == LocalState.Missing) missing.Add(e);
        missing.Sort(static (a, b) => a.LastSeenTicks.CompareTo(b.LastSeenTicks));
        return missing;
    }

    /// <summary>The indexed audio files Wavee cannot play that are still on disk (the attention line's "N files can't be played"), by path. UI thread.</summary>
    public static List<LocalEntry> UnplayableEntries()
    {
        var list = new List<LocalEntry>();
        foreach (LocalEntry e in s_index.Snapshot())
            if (!e.Playable && e.State != LocalState.Missing) list.Add(e);
        list.Sort(static (a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Background (the restate pass): the per-folder counts and the album count from the snapshot it already took.</summary>
    static (Dictionary<int, Counts> Counts, int Albums) CountPass(LocalEntry[] entries)
    {
        var counts = new Dictionary<int, Counts>();
        var albums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LocalEntry e in entries)
        {
            counts.TryGetValue(e.RootId, out Counts c);
            counts[e.RootId] = new Counts(c.Songs + 1, c.Unplayable + (e.Playable && e.State != LocalState.Missing ? 0 : 1),
                c.Missing + (e.State == LocalState.Missing ? 1 : 0), c.Cloud + (e.State == LocalState.CloudOnly ? 1 : 0));
            if (e.Tags.Album.Length > 0) albums.Add((e.Tags.AlbumArtist.Length > 0 ? e.Tags.AlbumArtist : e.Tags.FirstArtist) + "\u001f" + e.Tags.Album);
        }
        return (counts, albums.Count);
    }
}
