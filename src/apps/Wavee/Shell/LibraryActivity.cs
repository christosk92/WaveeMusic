// ── Shell/LibraryActivity.cs — what the library activity chip and its flyout say, in every theme (#240 UX S1, S2) ─────────────────────
//
// Role: CORE. The chip sits at the foot of the sidebar's Library section, under "Local files" (WinUI and Classic, one picture: Common's
// `CommonActivityChip`), and in Zune's band (`ZuneBandActivity`); a click opens the one flyout (`CommonActivityFlyout`). Every sentence is built
// here from `LocalLibrary`'s facts, so no theme words a state of its own, and every verb the flyout offers is here.
//
//   CHIP     hidden with no music folder; else, first match wins: a scan running ("Scanning · 1,930 of 6,016", a ring), matching with Spotify
//            running ("Matching · 12 of 48", a ring; "Your files and Spotify"), the announcement
//            after a background change ("Updated · +4", a tick; the view holds it for `UpdatedMs` after `LocalLibrary.Restated` moves with
//            news), what needs the user ("2 need attention", an amber dot), else idle (no words: nothing to report).
//   FLYOUT   the head ("Music on this PC · Up to date"), the four phases (listing, reading tags with its bar, rate and time left, matching
//            playlists, finding covers: the last two have no live data and tick when the scan ends), what changed since the app opened (each a
//            link to Local Files: the page has no filter by change yet), what needs the user (one line, one verb each), the last full scan.
//
// LIVE. A view reads these under `Track()` (the scan's progress at most four times a second, the list's restate). Nothing here touches a file.

using System.Globalization;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>The library activity chip's and flyout's words, states and verbs (see the file header).</summary>
public static class LibraryActivity
{
    /// <summary>The route the chip stands under in a nav pane ("Local files", the last item of the Library section).</summary>
    public const string RouteKey = "local";

    /// <summary>How long the chip says "Updated · +4" after a background change.</summary>
    public const float UpdatedMs = 5000f;

    /// <summary>What the chip shows.</summary>
    public enum ChipKind : byte
    {
        /// <summary>No music folder: no chip (a nav pane keeps its lane; Zune's band closes the word's slot).</summary>
        Hidden,
        /// <summary>Up to date, nothing to report: the lane stays, the chip shows nothing.</summary>
        Idle,
        /// <summary>A scan runs: a ring.</summary>
        Scanning,
        /// <summary>A scan is held (the user's Pause, or battery saver): the pause mark.</summary>
        Paused,
        /// <summary>A background change just landed: a tick.</summary>
        Updated,
        /// <summary>Something needs the user: the amber dot.</summary>
        Attention,
    }

    /// <summary>The chip now: its kind and its one line ("" when idle or hidden).</summary>
    public readonly record struct ChipState(ChipKind Kind, string Text);

    /// <summary>A phase's mark in the flyout's list.</summary>
    public enum PhaseMark : byte { Pending, Active, Done }

    /// <summary>One phase: its name, its mark, the count beside it, and while it runs its bar (-1: none) and its rate line.</summary>
    public readonly record struct Phase(string Label, PhaseMark Mark, string Aside, float Fraction, string Meta);

    /// <summary>One link of "What changed": its words and which count it is (a stable key).</summary>
    public readonly record struct ChangeLink(string Id, string Text);

    /// <summary>What an attention line looks like: a folder that is away (the caution triangle) or files (the blocked mark).</summary>
    public enum AttentionMark : byte { Folder, Files }

    /// <summary>One item that needs the user: a stable id, its line, the line under it ("" for none), its one verb and what the verb does.</summary>
    /// <param name="ActOver">A verb that opens over the page (the missing songs' Review dialog): the flyout calls it with its overlay instead of
    /// <paramref name="Act"/>.</param>
    public readonly record struct AttentionLine(string Id, AttentionMark Mark, string Text, string Sub, string Verb, Action Act,
        Action<FluentGpu.Controls.IOverlayService?>? ActOver = null);

    /// <summary>Subscribe the caller to the library: the scan's progress and the list's restate.</summary>
    public static void Track()
    {
        _ = LocalLibrary.Epoch.Value;
        _ = LocalLibrary.Restated.Value;
        _ = LocalLink.View.Value;   // "Your files and Spotify": matching's progress (board 28 ③)
    }

    /// <summary>The restate counter (an effect that announces "Updated" reads it, and so follows it).</summary>
    public static int Restated() => LocalLibrary.Restated.Value;

    /// <summary>The last restate brought news worth announcing (added, moved, missing, back or changed songs).</summary>
    public static bool HasNews => LocalLibrary.LastChanges.Total > 0;

    /// <summary>The chip now. <paramref name="announcing"/>: the view is inside its "Updated" window.</summary>
    public static ChipState Chip(bool announcing)
    {
        if (LocalLibrary.Folders.Count == 0) return new(ChipKind.Hidden, "");
        var p = LocalLibrary.Progress;
        if (PausedNow)
            return new(ChipKind.Paused, LocalLibrary.PausedBy == LocalLibrary.PauseReason.BatterySaver
                ? Loc.Get(Strings.LocalFiles.Activity.ChipPausedSaver)
                : Loc.Get(Strings.LocalFiles.Activity.ChipPaused));
        if (p.Active || LocalLibrary.Scanning)
            return new(ChipKind.Scanning, p.FilesSeen > 0
                ? Strings.LocalFiles.Activity.ChipScanning(Number(p.FilesDone), Number(p.FilesSeen))
                : Loc.Get(Strings.LocalFiles.Activity.ChipListing));
        // "Your files and Spotify" matching in the background (board 28 ③: "Matching · 12 of 48", a ring), after the scan it follows.
        if (LocalLink.Matching && LocalLink.Progress is var (done, total) && total > 0)
            return new(ChipKind.Scanning, LocalLinkWords.Chip(done, total));
        if (announcing && LocalLibrary.LastChanges is { Total: > 0 } news)
            return new(ChipKind.Updated, news.Added > 0
                ? Strings.LocalFiles.Activity.ChipUpdated(Number(news.Added))
                : Strings.LocalFiles.Activity.ChipUpdatedChanges(news.Total));
        int attention = LocalLibrary.Attention().Count;
        if (attention > 0) return new(ChipKind.Attention, Strings.LocalFiles.Activity.ChipAttention(attention));
        return new(ChipKind.Idle, "");
    }

    /// <summary>The chip's accessible name and tooltip.</summary>
    public static string ChipLabel => Loc.Get(Strings.LocalFiles.Activity.ChipLabel);

    // ── the flyout ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Music on this PC".</summary>
    public static string Title => Loc.Get(Strings.LocalFiles.Activity.Title);

    /// <summary>The head's state word: "Paused on battery saver" / "Paused", "Scanning", "Offline" (every folder away), else "Up to date".</summary>
    public static string StateWord()
    {
        if (PausedNow)
            return LocalLibrary.PausedBy == LocalLibrary.PauseReason.BatterySaver
                ? Loc.Get(Strings.LocalFiles.Activity.StatePausedSaver)
                : Loc.Get(Strings.LocalFiles.Activity.StatePaused);
        if (LocalLibrary.Progress.Active || LocalLibrary.Scanning) return Loc.Get(Strings.LocalFiles.Activity.StateScanning);
        return LocalLibrary.StateWord == "offline"
            ? Loc.Get(Strings.LocalFiles.Activity.StateOffline)
            : Loc.Get(Strings.LocalFiles.Activity.StateUpToDate);
    }

    /// <summary>The list's accessible name ("Scan phases").</summary>
    public static string PhasesLabel => Loc.Get(Strings.LocalFiles.Activity.Phases);

    /// <summary>The four phases. While a scan runs: listing (its files so far), reading tags (done of to-read, its bar, the rate and the
    /// time left), matching and covers pending (no live data for them yet). At rest every phase is ticked, listing with the library's files.</summary>
    public static Phase[] Phases()
    {
        var p = LocalLibrary.Progress;
        bool running = p.Active || LocalLibrary.Scanning;
        string listing = Loc.Get(Strings.LocalFiles.Activity.PhaseListing), reading = Loc.Get(Strings.LocalFiles.Activity.PhaseReading);
        string matching = Loc.Get(Strings.LocalFiles.Activity.PhaseMatching), covers = Loc.Get(Strings.LocalFiles.Activity.PhaseCovers);
        if (!running)
        {
            int files = 0;
            foreach (string f in LocalLibrary.Folders) files += LocalLibrary.FactsOf(f).Songs;
            return
            [
                new(listing, PhaseMark.Done, Files(files), -1f, ""),
                new(reading, PhaseMark.Done, "", -1f, ""),
                new(matching, PhaseMark.Done, "", -1f, ""),
                new(covers, PhaseMark.Done, "", -1f, ""),
            ];
        }
        bool listingNow = p.Phase is LocalLibrary.ScanPhase.Listing or LocalLibrary.ScanPhase.Idle;
        string rate = p.FilesPerSecond > 0 ? Strings.LocalFiles.Activity.Rate(Number((int)Math.Round(p.FilesPerSecond))) : "";
        string left = p.SecondsLeft <= 0 ? ""
            : p.SecondsLeft < 90 ? Strings.Settings.Music.LeftSeconds(((int)Math.Ceiling(p.SecondsLeft)).ToString(CultureInfo.CurrentCulture))
            : Strings.Settings.Music.LeftMinutes((int)Math.Round(p.SecondsLeft / 60));
        string meta = rate.Length > 0 && left.Length > 0 ? rate + Separator + left : rate + left;
        int total = Math.Max(p.ToRead, p.Read);
        float fraction = total > 0 ? Math.Clamp((float)p.Read / total, 0f, 1f) : -1f;
        return
        [
            new(listing, listingNow ? PhaseMark.Active : PhaseMark.Done, Files(p.FilesSeen), -1f, ""),
            listingNow
                ? new(reading, PhaseMark.Pending, "", -1f, "")
                : new(reading, PhaseMark.Active, Strings.LocalFiles.Activity.ReadOf(Number(p.Read), Number(total)), fraction, meta),
            new(matching, PhaseMark.Pending, "", -1f, ""),
            new(covers, PhaseMark.Pending, "", -1f, ""),
        ];
    }

    /// <summary>"What changed since you opened Wavee".</summary>
    public static string ChangedHead => Loc.Get(Strings.LocalFiles.Activity.ChangedHead);

    /// <summary>The session's changes, each count that is not zero ("+46 added", "3 moved", "2 missing", "1 back", "1 changed"); none: empty.</summary>
    public static ChangeLink[] Changes()
    {
        var c = LocalLibrary.Changes;
        if (c.Total == 0) return [];
        var links = new List<ChangeLink>(5);
        if (c.Added > 0) links.Add(new("added", Strings.LocalFiles.Activity.Added(Number(c.Added))));
        if (c.Moved > 0) links.Add(new("moved", Strings.LocalFiles.Activity.Moved(Number(c.Moved))));
        if (c.Missing > 0) links.Add(new("missing", Strings.LocalFiles.Activity.Missing(Number(c.Missing))));
        if (c.Back > 0) links.Add(new("back", Strings.LocalFiles.Activity.Back(Number(c.Back))));
        if (c.Changed > 0) links.Add(new("changed", Strings.LocalFiles.Activity.Changed(Number(c.Changed))));
        return links.ToArray();
    }

    /// <summary>"Needs attention".</summary>
    public static string AttentionHead => Loc.Get(Strings.LocalFiles.Activity.AttentionHead);

    /// <summary>What needs the user, one line and one verb each: a folder away (Retry rescans it), missing songs (Review) and files that cannot be
    /// played (See which), both opening Local Files.</summary>
    public static AttentionLine[] Attention()
    {
        var items = LocalLibrary.Attention();
        var lines = new AttentionLine[items.Count];
        for (int i = 0; i < lines.Length; i++)
        {
            var a = items[i];
            string folder = a.Folder;
            lines[i] = a.Kind switch
            {
                LocalLibrary.AttentionKind.FolderOffline => new("offline:" + folder, AttentionMark.Folder, Strings.LocalFiles.Activity.Offline(folder),
                    a.Count > 0 ? Strings.Settings.Music.Kept(a.Count, Number(a.Count)) : "", Loc.Get(Strings.LocalFiles.Activity.Retry), () => LocalLibrary.Rescan(folder)),
                LocalLibrary.AttentionKind.SignInNeeded => new("signin:" + folder, AttentionMark.Folder, Strings.LocalFiles.Activity.SignIn(folder),
                    a.Count > 0 ? Strings.Settings.Music.Kept(a.Count, Number(a.Count)) : "", Loc.Get(Strings.LocalFiles.Activity.Retry), () => LocalLibrary.Rescan(folder)),
                LocalLibrary.AttentionKind.NoAccess => new("noaccess:" + folder, AttentionMark.Folder, Strings.LocalFiles.Activity.NoAccess(folder), "",
                    Loc.Get(Strings.LocalFiles.Activity.FixPermissions), () => LocalLibrary.FixPermissions(folder)),
                LocalLibrary.AttentionKind.Missing => new("missing", AttentionMark.Files, Strings.LocalFiles.Activity.MissingSongs(a.Count), "",
                    Loc.Get(Strings.LocalFiles.Activity.Review), OpenLocalFiles, LocalFilesList.OpenReview),
                _ => new("unplayable", AttentionMark.Files, Strings.LocalFiles.Activity.Unplayable(a.Count), "",
                    Loc.Get(Strings.LocalFiles.Activity.SeeWhich), OpenLocalFiles),
            };
        }
        return lines;
    }

    /// <summary>"2 min ago", "just now", "3 hours ago" for a UTC tick time (a scan's end).</summary>
    public static string Ago(long utcTicks)
    {
        long ageMs = Math.Max(0, (DateTime.UtcNow.Ticks - utcTicks) / TimeSpan.TicksPerMillisecond);
        var (unit, count) = Notify.RelativeAge(ageMs);
        return unit switch
        {
            RelativeUnit.Now => Loc.Get(Strings.LocalFiles.Activity.AgoNow),
            RelativeUnit.Minutes => Strings.LocalFiles.Activity.AgoMinutes(count),
            RelativeUnit.Hours => Strings.LocalFiles.Activity.AgoHours(count),
            _ => Strings.LocalFiles.Activity.AgoDays(count),
        };
    }

    static readonly FluentGpu.Signals.Signal<int> s_minute = new(0);
    static Timer? s_minuteTimer;

    /// <summary>Moves every 30 s while something reads it, so an "ago" line bound to it (`Prop.Of`) stays true without a page re-render. The
    /// timer starts at the first read and writes on the UI thread.</summary>
    public static FluentGpu.Signals.IReadSignal<int> Minute
    {
        get
        {
            s_minuteTimer ??= new Timer(static _ => Playback.ToUi(static () => s_minute.Value++), null, 30_000, 30_000);
            return s_minute;
        }
    }

    /// <summary>The footer's line: "Last full scan 2 min ago · 1.8 s", "First scan under way" while the first one runs, or "Not scanned yet".</summary>
    public static string LastScanLine()
    {
        var (ticks, ms) = LocalLibrary.LastScan;
        if (ticks <= 0)
            return LocalLibrary.Progress.Active || LocalLibrary.Scanning
                ? Loc.Get(Strings.LocalFiles.Activity.FirstScan)
                : Loc.Get(Strings.LocalFiles.Activity.NotScanned);
        string when = Ago(ticks);
        string took = (ms / 1000.0).ToString(ms < 10_000 ? "0.0" : "0", CultureInfo.CurrentCulture);
        return Strings.LocalFiles.Activity.LastScan(when, took);
    }

    /// <summary>A scan runs or waits to run: the flyout's head offers Pause (or Resume).</summary>
    public static bool CanPause => LocalLibrary.Progress.Active || LocalLibrary.Scanning;

    /// <summary>A scan that runs or waits is held (by the user or by battery saver).</summary>
    public static bool PausedNow => LocalLibrary.PausedBy != LocalLibrary.PauseReason.None && CanPause;

    /// <summary>"Pause", or "Resume" while held.</summary>
    public static string PauseLabel => Loc.Get(PausedNow ? Strings.LocalFiles.Activity.Resume : Strings.LocalFiles.Activity.Pause);

    /// <summary>The head's Pause / Resume: Resume releases the user's pause and, on battery saver, runs anyway until battery saver next turns on.</summary>
    public static readonly Action TogglePause = static () =>
    {
        if (PausedNow) LocalLibrary.ResumeNow();
        else LocalLibrary.SetPaused(true);
    };

    /// <summary>"Rescan now" can run (no scan is running).</summary>
    public static bool CanRescan => !(LocalLibrary.Progress.Active || LocalLibrary.Scanning);

    public static string RescanLabel => Loc.Get(Strings.LocalFiles.Activity.Rescan);
    public static string SettingsLabel => Loc.Get(Strings.LocalFiles.Activity.Settings);

    // ── verbs ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Rescan now": a warm walk of every folder.</summary>
    public static readonly Action RescanAll = static () => LocalLibrary.Rescan();

    /// <summary>Opens Local Files (a change link, "Review", "See which": the page has no filter by change or state yet).</summary>
    public static readonly Action OpenLocalFiles = static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.Local));

    /// <summary>Opens Settings › Storage › Music on this PC at the music folders.</summary>
    public static readonly Action OpenSettings = static () => Settings.Open("storage.music.folders");

    /// <summary>"1,930 of 6,016 read".</summary>
    public static string ReadOf(int done, int total) => Strings.LocalFiles.Activity.ReadOf(Number(done), Number(total));

    static string Files(int n) => Strings.LocalFiles.Activity.Files(n, Number(n));

    static string Number(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    const string Separator = " · ";
}
