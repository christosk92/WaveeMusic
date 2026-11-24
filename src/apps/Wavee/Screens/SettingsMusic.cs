// ── Screens/SettingsMusic.cs — what Settings › Storage › Music on this PC says, in every theme (#240 S3) ────────────────────────
//
// Role: CORE. The three themes draw the `storage.music.folders` composite their own way (WinUI and Classic one card, Zune its words), but
// they say the same things: each folder's line (its songs, what cannot be played, or its state: drive offline, sign in needed, scanning,
// waking up, slow), its state mark, and the one status line under the folders (the running scan with its progress, else when the last
// scan ran). Every sentence is built here from `LocalLibrary`'s facts, so no theme words a state of its own.
//
// NETWORK FOLDERS (S13, plan §3.16). A folder on a share says so under its path ("1,204 songs · network") and its line is the share's
// state: online ("Checked 12 min ago · checks every 45 min and on reconnect"), waking up ("Waking up \\nas… (disks spinning up)"), slow
// ("Slow connection · scanning gently", with the scan's bar while it walks), sign in needed ("\\nas needs you to sign in" and Sign in)
// or offline ("Offline · 1,204 songs kept" and Retry). A state change swaps the words, the mark and the one verb in place: a network row
// keeps one extent in every state.
//
// LIVE. A view reads these under `Track()` (the library's two signals: the scan's progress at most four times a second, the list's
// restate). The registry composes the folder card's buttons from facts (`ISettingsFacts.MusicFolderCount`, `DefaultMusicFolderOffered`);
// a change the page did not make itself (a fixture, a folder added from elsewhere) re-composes it through `Stale` (the view bumps the page).

using System.Globalization;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>The words and states of the music folders (see the file header).</summary>
public static class SettingsMusic
{
    /// <summary>"Keep missing songs for": the three choices, in days.</summary>
    public static readonly int[] MissingDays = [30, 60, 90];

    /// <summary>The choice index of a stored day count (60 for anything that is not one of the three).</summary>
    public static int MissingDaysIndex(int days) => Array.IndexOf(MissingDays, days) is int at and >= 0 ? at : 1;

    /// <summary>What a folder row's state mark draws.</summary>
    public enum Mark : byte
    {
        /// <summary>Nothing yet (not scanned).</summary>
        None,
        /// <summary>Up to date.</summary>
        Ok,
        /// <summary>Working (scanning, waking a network drive): a ring.</summary>
        Busy,
        /// <summary>Needs the user (offline): the caution mark.</summary>
        Caution,
        /// <summary>A share that wants a sign-in: the lock, in the caution ink.</summary>
        Lock,
        /// <summary>A share that answers slowly: the gauge, in the secondary ink.</summary>
        Slow,
    }

    /// <summary>The ink of a line's lead words.</summary>
    public enum Tone : byte
    {
        /// <summary>The caution ink ("Drive offline", "\\nas needs you to sign in").</summary>
        Caution,
        /// <summary>The primary ink ("Waking up \\nas…": working, not wrong).</summary>
        Primary,
    }

    /// <summary>The one verb a network folder's state offers beside its line.</summary>
    public enum Verb : byte
    {
        None,
        /// <summary>"Sign in": the Windows credential dialog for the share (<see cref="LocalLibrary.SignIn"/>).</summary>
        SignIn,
        /// <summary>"Retry": look at the share again now (<see cref="LocalLibrary.Rescan"/>).</summary>
        Retry,
    }

    /// <summary>One folder's line: the path as stored and as the user knows it (<see cref="LocalLibrary.DisplayOf"/>), the lead words in
    /// their <see cref="LeadTone"/> ("Drive offline"), the rest in the secondary ink, the mark, and for a network folder the words under its
    /// path ("1,204 songs · network"), its verb and its scan's fraction (-1: no bar). <see cref="Dim"/>: the path steps back (offline).</summary>
    public readonly record struct FolderLine(string Path, string Display, string? Lead, string Rest, Mark Mark, bool Network = false, string? Sub = null,
        Tone LeadTone = Tone.Caution, Verb Action = Verb.None, float Fraction = -1f, bool Dim = false);

    /// <summary>The status line under the folders: the words, the time left beside them (a running scan), and the scan's fraction (-1: no
    /// determinate scan to show).</summary>
    public readonly record struct StatusLine(string Text, string Aside, float Fraction, bool Scanning);

    /// <summary>Subscribe the caller to the library: the scan's progress and the list's restate.</summary>
    public static void Track()
    {
        _ = LocalLibrary.Epoch.Value;
        _ = LocalLibrary.Restated.Value;
    }

    /// <summary>The music folders now.</summary>
    public static IReadOnlyList<string> Folders => LocalLibrary.Folders;

    /// <summary>One folder's line (S3, S7, S13).</summary>
    public static FolderLine Line(string folder)
    {
        var f = LocalLibrary.FactsOf(folder);
        if (f.Network) return NetworkLine(folder, f);
        switch (f.State)
        {
            case LocalRootState.Offline:
                return new(folder, folder, Loc.Get(Strings.Settings.Music.DriveOffline), f.Songs > 0 ? Kept(f.Songs) : "", Mark.Caution, Dim: true);
            case LocalRootState.SignInNeeded:
                return new(folder, folder, Loc.Get(Strings.Settings.Music.SignIn), f.Songs > 0 ? Kept(f.Songs) : "", Mark.Lock);
            case LocalRootState.Waking:
                return new(folder, folder, null, Loc.Get(Strings.Settings.Music.Waking), Mark.Busy);
            case LocalRootState.Checking:   // just added: its existence or reachability is being asked off the UI thread (F4)
                return new(folder, folder, null, Loc.Get(Strings.Settings.Music.Checking), Mark.Busy);
            case LocalRootState.Slow:
                return new(folder, folder, null, Loc.Get(Strings.Settings.Music.Slow), Mark.Slow);
        }
        if (f.Scanning || f.State == LocalRootState.Scanning) return new(folder, folder, null, Loc.Get(Strings.Settings.Music.FolderScanning), Mark.Busy);
        if (f.State == LocalRootState.Unknown && f.Songs == 0) return new(folder, folder, null, Loc.Get(Strings.Settings.Music.NotScanned), Mark.None);
        string rest = Songs(f.Songs);
        int unplayable = Math.Max(0, f.Unplayable - f.Missing);
        if (unplayable > 0) rest += Separator + Strings.Settings.Music.Unplayable(Number(unplayable));
        if (f.Missing > 0) rest += Separator + Strings.Settings.Music.Missing(Number(f.Missing));
        return new(folder, folder, null, rest, f.State == LocalRootState.Online ? Mark.Ok : Mark.None);
    }

    /// <summary>A network folder's line (S13): its songs under the path, the share's state as the line, the state's one verb.</summary>
    static FolderLine NetworkLine(string folder, LocalLibrary.FolderFacts f)
    {
        string display = LocalLibrary.DisplayOf(folder);
        string sub = Songs(f.Songs) + Separator + Loc.Get(Strings.Settings.Music.Network);
        string server = ServerOf(folder);
        var p = LocalLibrary.Progress;
        float fraction = p.Active && string.Equals(p.Folder, folder, StringComparison.OrdinalIgnoreCase) && p.Fraction >= 0 ? (float)p.Fraction : -1f;
        FolderLine line = new(folder, display, null, "", Mark.None, Network: true, Sub: sub, Fraction: fraction);
        switch (f.State)
        {
            case LocalRootState.Offline:
                return line with { Lead = Loc.Get(Strings.Settings.Music.Offline), Rest = f.Songs > 0 ? Kept(f.Songs) : "", Mark = Mark.Caution, Action = Verb.Retry, Dim = true };
            case LocalRootState.SignInNeeded:
                return line with { Lead = Strings.Settings.Music.SignInTo(server), Mark = Mark.Lock, Action = Verb.SignIn };
            case LocalRootState.Waking:
                return line with { Lead = Strings.Settings.Music.WakingServer(server), LeadTone = Tone.Primary, Rest = Loc.Get(Strings.Settings.Music.SpinningUp), Mark = Mark.Busy };
            case LocalRootState.Slow:
                return line with { Rest = Loc.Get(Strings.Settings.Music.Slow), Mark = Mark.Slow };
            case LocalRootState.Checking:   // just added: its reachability is being asked off the UI thread (F4)
                return line with { Rest = Loc.Get(Strings.Settings.Music.Checking), Mark = Mark.Busy };
        }
        if (f.Scanning || f.State == LocalRootState.Scanning) return line with { Rest = Loc.Get(Strings.Settings.Music.FolderScanning), Mark = Mark.Busy };
        if (f.LastScanUtcTicks <= 0) return line with { Rest = Loc.Get(Strings.Settings.Music.NotScanned) };
        var (unit, count) = Notify.RelativeAge(Math.Max(0, (DateTime.UtcNow.Ticks - f.LastScanUtcTicks) / TimeSpan.TicksPerMillisecond));
        string when = unit switch
        {
            RelativeUnit.Now => Loc.Get(Strings.Settings.Music.CheckedNow),
            RelativeUnit.Minutes => Strings.Settings.Music.CheckedMinutes(count),
            RelativeUnit.Hours => Strings.Settings.Music.CheckedHours(count),
            _ => Strings.Settings.Music.CheckedDays(count),
        };
        return line with { Rest = Strings.Settings.Music.Cadence(when), Mark = f.State == LocalRootState.Online ? Mark.Ok : Mark.None };
    }

    /// <summary>The server of a network folder as the user names it: its share up to the second backslash (`\\nas\share` gives `\\nas`).</summary>
    static string ServerOf(string folder)
    {
        string share = LocalLibrary.ShareOf(folder) ?? folder;
        int cut = share.Length > 2 ? share.IndexOf('\\', 2) : -1;
        return cut > 0 ? share[..cut] : share;
    }

    /// <summary>A network folder's verb, run.</summary>
    public static void Run(Verb verb, string folder)
    {
        switch (verb)
        {
            case Verb.SignIn: SettingsVerbs.SignInMusicFolder(folder); break;
            case Verb.Retry: SettingsVerbs.RescanMusicFolder(folder); break;
        }
    }

    /// <summary>The verb's word (a loc key), or null.</summary>
    public static string? VerbKey(Verb verb) => verb switch
    {
        Verb.SignIn => Strings.Settings.Music.SignInAction,
        Verb.Retry => Strings.Settings.Music.Retry,
        _ => null,
    };

    /// <summary>The status line under the folders.</summary>
    public static StatusLine Status()
    {
        var p = LocalLibrary.Progress;
        if (p.Active)
        {
            double fraction = p.Fraction;
            if (fraction < 0) return new(Strings.Settings.Music.Listing(p.Folder), "", -1f, true);
            string left = p.SecondsLeft <= 0 ? ""
                : p.SecondsLeft < 90 ? Strings.Settings.Music.LeftSeconds(((int)Math.Ceiling(p.SecondsLeft)).ToString(CultureInfo.CurrentCulture))
                : Strings.Settings.Music.LeftMinutes((int)Math.Round(p.SecondsLeft / 60));
            return new(Strings.Settings.Music.Scanning(p.Folder, Number(p.FilesDone), Number(p.FilesSeen)), left, (float)fraction, true);
        }
        var (ticks, ms) = LocalLibrary.LastScan;
        if (ticks <= 0) return new(Loc.Get(Strings.Settings.Music.NotScanned), "", -1f, false);
        long ageMs = Math.Max(0, (DateTime.UtcNow.Ticks - ticks) / TimeSpan.TicksPerMillisecond);
        var (unit, count) = Notify.RelativeAge(ageMs);
        string when = unit switch
        {
            RelativeUnit.Now => Loc.Get(Strings.Settings.Music.ScannedNow),
            RelativeUnit.Minutes => Strings.Settings.Music.ScannedMinutes(count),
            RelativeUnit.Hours => Strings.Settings.Music.ScannedHours(count),
            _ => Strings.Settings.Music.ScannedDays(count),
        };
        string took = (ms / 1000.0).ToString(ms < 10_000 ? "0.0" : "0", CultureInfo.CurrentCulture);
        return new(Strings.Settings.Music.Took(when, took), "", -1f, false);
    }

    /// <summary>The registry's folder card was composed from facts that no longer hold (the folders changed without a page write: a fixture,
    /// a folder added elsewhere): the view bumps the page so the buttons follow. Touches the disk once (the Music folder's existence): call it
    /// from an effect, never a render.</summary>
    public static bool Stale(bool composedWithFolders, bool composedOffered)
        => composedWithFolders != (LocalLibrary.Folders.Count > 0) || composedOffered != (LocalLibrary.DefaultMusicFolder is not null);

    /// <summary>"4,812 songs".</summary>
    static string Songs(int n) => Strings.Settings.Music.Songs(n, Number(n));

    /// <summary>"1,204 songs kept".</summary>
    static string Kept(int n) => Strings.Settings.Music.Kept(n, Number(n));

    static string Number(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    const string Separator = " · ";
}
