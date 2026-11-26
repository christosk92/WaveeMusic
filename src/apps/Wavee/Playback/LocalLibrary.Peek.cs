// ── Playback/LocalLibrary.Peek.cs ───────────────────────────────────────────────────────────────────────────────────
// "Find music on this PC" (#240, 0dm's "check Music and Downloads first"): a count, never a read, of the audio and video files in the
// user's Music and Downloads folders, so the empty Local Files page and Settings can offer them with numbers (D3 holds: nothing is added
// until the user presses "Add selected")
//
// Role: SHELL (StartPeek: UI thread; the walk: a below-normal background thread; the answer lands on the UI thread)
// Spec: C:\WAVEE\fgpu\handoff\240-0dm-suggestions.md §3 F2
//
// THE PEEK runs once per session, when a surface that shows the card first mounts (the page opened: nothing runs at start). Each folder
// (FOLDERID_Music, FOLDERID_Downloads: the Music folder only, not the Windows Music library) is enumerated with the scanner's own listing
// (NtDirectory: never through a reparse point, a hidden or a system folder) and its files are judged by extension only: no file is opened
// and no tag is read. A folder stops at 5 s or 50,000 directory entries (its count then reads "1,204+ songs"). A cloud-only placeholder is
// counted apart ("238 in OneDrive"), from its attributes alone. The rows land as each folder finishes.

using System.Runtime.InteropServices;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>One folder the peek looked at: its counts (songs and videos by extension; <see cref="Cloud"/> of the songs are cloud-only), whether
/// it is done, and whether it stopped at the cap.</summary>
public sealed record LocalPeekRow(string Key, string Path, int Songs, int Cloud, int Videos, bool Done, bool Capped);

public static partial class LocalLibrary
{
    /// <summary>A folder's walk stops here (directory entries) or after <see cref="PeekBudgetMs"/>.</summary>
    public const int PeekEntryCap = 50_000;
    const int PeekBudgetMs = 5_000;

    static readonly Signal<LocalPeekRow[]> s_peek = new([]);
    static int s_peekStarted;

    /// <summary>The peek's rows (Music, then Downloads; a folder that does not exist has none). Empty until <see cref="StartPeek"/>. UI thread.</summary>
    public static IReadSignal<LocalPeekRow[]> Peek => s_peek;

    /// <summary>Count Music and Downloads, once per session (a card mounted: the page opened). UI thread; the walk is off it.</summary>
    public static void StartPeek()
    {
        if (s_fixture || Interlocked.Exchange(ref s_peekStarted, 1) != 0) return;
        var folders = new List<(string Key, string Path)>(2);
        if (KnownFolder(FolderMusic) is { } music) folders.Add(("music", music));
        if (KnownFolder(FolderDownloads) is { } downloads) folders.Add(("downloads", downloads));
        var rows = new LocalPeekRow[folders.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = new LocalPeekRow(folders[i].Key, folders[i].Path, 0, 0, 0, Done: false, Capped: false);
        s_peek.Value = rows;
        if (rows.Length == 0) return;
        new Thread(() =>
        {
            NtDirectory.ExposePlaceholders();
            for (int i = 0; i < rows.Length; i++)
            {
                LocalPeekRow row = CountFolder(rows[i]);
                int at = i;
                Playback.ToUi(() =>
                {
                    var now = (LocalPeekRow[])s_peek.Peek().Clone();
                    if (at < now.Length) now[at] = row;
                    s_peek.Value = now;
                });
            }
        })
        { IsBackground = true, Name = "Wavee.LocalLibrary.Peek", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>The inspector's card states (no disk): <c>counting</c>, <c>landed</c> (Music 1,204 songs with 238 cloud-only, Downloads 37 songs and
    /// 12 videos), <c>empty</c> (Downloads holds no songs), <c>none</c> (no rows).</summary>
    public static void InspectPeek(string value)
    {
        const string music = @"C:\Users\Listener\Music", downloads = @"C:\Users\Listener\Downloads";
        s_peek.Value = value switch
        {
            "counting" => [new("music", music, 0, 0, 0, false, false), new("downloads", downloads, 0, 0, 0, false, false)],
            "landed" => [new("music", music, 1204, 238, 0, true, false), new("downloads", downloads, 37, 0, 12, true, false)],
            "empty" => [new("music", music, 1204, 238, 0, true, false), new("downloads", downloads, 0, 0, 3, true, false)],
            "capped" => [new("music", music, 48_210, 0, 4, true, true), new("downloads", downloads, 37, 0, 12, true, false)],
            _ => [],
        };
    }

    /// <summary>Walk one folder by extension (BLOCKS; the peek's thread).</summary>
    static LocalPeekRow CountFolder(LocalPeekRow row)
    {
        int songs = 0, cloud = 0, videos = 0, entries = 0;
        bool capped = false;
        long deadline = Environment.TickCount64 + PeekBudgetMs;
        var listing = new List<NtEntry>();
        try
        {
            // A Music folder redirected to a share that is away answers no listing (an open there blocks for the redirector's timeout).
            if (ReachOf(row.Path) is not (LocalRootState.Online or LocalRootState.Slow or LocalRootState.Waking)) return row with { Done = true };
            using var root = NtDirectory.OpenDirectory(null, row.Path, out _);
            if (root is null) return row with { Done = true };
            uint serial = NtDirectory.VolumeOf(root, row.Path).Serial;
            var stack = new Stack<(Microsoft.Win32.SafeHandles.SafeFileHandle Handle, bool Own)>();
            stack.Push((root, false));
            while (stack.Count > 0)
            {
                var (dir, own) = stack.Pop();
                try
                {
                    listing.Clear();
                    if (!NtDirectory.List(dir, serial, listing, out _)) continue;
                    foreach (NtEntry e in listing)
                    {
                        if (++entries > PeekEntryCap || Environment.TickCount64 > deadline) { capped = true; break; }
                        if (e.IsDirectory)
                        {
                            if (NtDirectory.WalksInto(in e) && NtDirectory.OpenDirectory(dir, e.Name, out _) is { } child) stack.Push((child, true));
                            continue;
                        }
                        if (Playlist.IsLocalAudioFile(e.Name)) { songs++; if (e.IsCloudOnly) cloud++; }
                        else if (Playlist.IsLocalVideoFile(e.Name)) videos++;
                    }
                }
                finally { if (own) dir.Dispose(); }
                if (capped) break;
            }
            while (stack.Count > 0) { var (dir, own) = stack.Pop(); if (own) dir.Dispose(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("local", "local.peek walk failed", ex); }
        // Counts only: the folder's NAME is the user's own, its path holds the account name.
        Log.Info("local", "local.peek " + row.Key + " songs=" + songs + " cloud=" + cloud + " videos=" + videos + " entries=" + entries + " capped=" + (capped ? 1 : 0)
                              + " ms=" + (Environment.TickCount64 - (deadline - PeekBudgetMs)));
        return row with { Songs = songs, Cloud = cloud, Videos = videos, Done = true, Capped = capped };
    }

    // ── the known folders ────────────────────────────────────────────────────────────────────────────────────────────

    static readonly Guid FolderMusic = new("4BD8D571-6D19-48D3-BE97-422220080E43");
    static readonly Guid FolderDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>A known folder's registered path (SHGetKnownFolderPath), else null. Whether it is there is the walk's to find.</summary>
    static string? KnownFolder(Guid id)
    {
        // KF_FLAG_DONT_VERIFY: the path as registered, no disk or network touched here (the UI thread asks).
        if (SHGetKnownFolderPath(in id, 0x4000, IntPtr.Zero, out IntPtr raw) != 0) { if (raw != IntPtr.Zero) Marshal.FreeCoTaskMem(raw); return null; }
        try { return Marshal.PtrToStringUni(raw) is { Length: > 0 } path ? Normalize(path) : null; }
        finally { Marshal.FreeCoTaskMem(raw); }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
