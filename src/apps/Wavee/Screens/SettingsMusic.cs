// ── Screens/SettingsMusic.cs — what Settings › Storage › Music on this PC says, in every theme (#240 S3) ────────────────────────
//
// Role: CORE. The three themes draw the `storage.music.folders` composite their own way (WinUI and Classic one card, Zune its words), but
// they say the same things: each folder's line (its songs, what cannot be played, or its state: drive offline, sign in needed, scanning,
// waking up, slow), its state mark, and the one status line under the folders (the running scan with its progress, else when the last
// scan ran). Every sentence is built here from `LocalLibrary`'s facts, so no theme words a state of its own.
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
        /// <summary>Needs the user (offline, sign-in needed, slow): the caution mark.</summary>
        Caution,
    }

    /// <summary>One folder's line: the path, the lead words in the caution ink ("Drive offline"), the rest in the secondary ink, the mark.</summary>
    public readonly record struct FolderLine(string Path, string? Lead, string Rest, Mark Mark);

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
        switch (f.State)
        {
            case LocalRootState.Offline:
                return new(folder, Loc.Get(Strings.Settings.Music.DriveOffline), f.Songs > 0 ? Kept(f.Songs) : "", Mark.Caution);
            case LocalRootState.SignInNeeded:
                return new(folder, Loc.Get(Strings.Settings.Music.SignIn), f.Songs > 0 ? Kept(f.Songs) : "", Mark.Caution);
            case LocalRootState.Waking:
                return new(folder, null, Loc.Get(Strings.Settings.Music.Waking), Mark.Busy);
            case LocalRootState.Checking:   // just added: its existence or reachability is being asked off the UI thread (F4)
                return new(folder, null, Loc.Get(Strings.Settings.Music.Checking), Mark.Busy);
            case LocalRootState.Slow:
                return new(folder, null, Loc.Get(Strings.Settings.Music.Slow), Mark.Caution);
        }
        if (f.Scanning || f.State == LocalRootState.Scanning) return new(folder, null, Loc.Get(Strings.Settings.Music.FolderScanning), Mark.Busy);
        if (f.State == LocalRootState.Unknown && f.Songs == 0) return new(folder, null, Loc.Get(Strings.Settings.Music.NotScanned), Mark.None);
        string rest = Songs(f.Songs);
        int unplayable = Math.Max(0, f.Unplayable - f.Missing);
        if (unplayable > 0) rest += Separator + Strings.Settings.Music.Unplayable(Number(unplayable));
        if (f.Missing > 0) rest += Separator + Strings.Settings.Music.Missing(Number(f.Missing));
        return new(folder, null, rest, f.State == LocalRootState.Online ? Mark.Ok : Mark.None);
    }

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
