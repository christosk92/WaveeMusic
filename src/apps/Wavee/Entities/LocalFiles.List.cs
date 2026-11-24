// ── Entities/LocalFiles.List.cs — what the Local Files list says and does beyond a playlist's (#240 UX S4, S5, S6) ─────────────────────
//
// Role: CORE. The Local Files page is the playlist page's local arm (`Playlist.Page.cs`, route `local`); this file holds what only that list has,
// worded from `LocalLibrary`'s facts, and the hosts that hand each theme's view its props. No theme words a state of its own.
//
//   EMPTY     no music folder and nothing listed: the list's body is `LocalFiles.Empty` ("Your music on this PC", Add my Music folder, Choose a
//             folder…, the drop zone's words). The verbs are Settings' own (`SettingsVerbs.AddMusicFolder`, `AddDefaultMusicFolder`): one home.
//   SCAN      while the first scan of a folder runs (`LocalLibrary.FirstScanRunning`) the page head carries `LocalFiles.Scan` in its status slot
//             under the facts: "Reading your music · 1,930 of 4,812 · 212 albums so far", the time left, a determinate bar. When it ends the slot
//             leaves (the head's own row motion) and one toast sums it up: "6,016 songs from 2 folders".
//   PILL      a restate with news while the list is scrolled away from its top: "4 new songs · 1 moved" over the list (`LocalFiles.Pill`); a click
//             glides to the top. Nothing in the list moves for it (it floats). Reaching the top by hand retires it too.
//   TAGS      a row added this session wears "New", a row moved this session "Moved" (its tooltip: "Moved from …"), for the session.
//   UNPLAYABLE an indexed file that cannot be played says why in the time lane: "Missing" (tooltip "Not found at <path> since 12 Nov"), "Drive
//             offline", or the lane's "Unavailable" for a format Wavee cannot play yet; the unavailable bar counts them by cause (one home per
//             message: the bar is the missing songs' banner too, with Review beside Hide them).
//   REVIEW    the missing songs' dialog (`LocalFiles.Review` in the house ContentDialog): per file its title, artist, last-known path, "Not found since
//             12 Nov · removed on 11 Jan" and Locate… (a folder picker: the pick is added as a music folder, or rescanned if it is one; Wavee cannot
//             pair one file by hand yet, and the index has no verb that drops one entry, so there is no Remove); the keep-days footer.
//
// LIVE. The hosts read `LocalLibrary.Epoch` (scan progress, at most four times a second) and `LocalLibrary.Restated` (the list changed). A row's
// tag and reason are read in its bound cells, on a presentation change or a restate, never per frame.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>The Local Files list's words, facts, hosts and verbs (see the file header).</summary>
public static class LocalFilesList
{
    // ══ 1. A ROW ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Why an indexed local file cannot be played (None: it can, or the row is no indexed file).</summary>
    public enum Cause : byte { None, Missing, Offline, Format }

    /// <summary>A session mark on a row (S5).</summary>
    public enum Mark : byte { None, New, Moved }

    /// <summary>The index entry behind an indexed local row. UI thread; one probe (it reads the row's uri text, which a local row already holds).</summary>
    static bool TryEntry(Track t, out LocalEntry entry)
    {
        entry = null!;
        if (!t.IsValid || !t.IsLocal) return false;
        if (!LocalLibrary.TryLocalIdOf(t.Uri.Text, out long id)) return false;
        if (!LocalLibrary.Index.TryGet(id, out LocalEntry? e)) return false;
        entry = e;
        return true;
    }

    /// <summary>Why <paramref name="t"/> cannot be played, for an indexed local row the list marks unavailable: its drive away first (nothing on it can be
    /// read, missing or not), then missing, then a format Wavee cannot play yet. None for any other row.</summary>
    public static Cause CauseOf(Track t)
    {
        if (!t.IsLocal || !t.Unplayable() || !TryEntry(t, out LocalEntry e)) return Cause.None;
        return CauseOf(e);
    }

    static Cause CauseOf(LocalEntry e)
        => LocalLibrary.RootAway(e.RootId) ? Cause.Offline
         : e.State == LocalState.Missing ? Cause.Missing
         : !e.Playable ? Cause.Format
         : Cause.None;

    /// <summary>The time lane's short reason for an unplayable indexed row ("Missing", "Drive offline"), or null (the lane's own words then: a
    /// format Wavee cannot play is "Unavailable", as every unplayable row).</summary>
    public static string? ShortReason(Track t) => CauseOf(t) switch
    {
        Cause.Missing => Loc.Get(Strings.LocalFiles.List.MissingShort),
        Cause.Offline => Loc.Get(Strings.LocalFiles.List.OfflineShort),
        _ => null,
    };

    /// <summary>The time lane's tooltip for an unplayable indexed row: "Not found at D:\Music\…\Song.flac since 12 Nov", "D:\Music is offline", "Wavee
    /// can’t play this file’s format yet"; null for any other row.</summary>
    public static string? LongReason(Track t)
    {
        if (!t.IsLocal || !t.Unplayable() || !TryEntry(t, out LocalEntry e)) return null;
        switch (CauseOf(e))
        {
            case Cause.Missing:
                return Strings.LocalFiles.List.NotFound(LocalLibrary.PathOf(e.LocalId) ?? e.RelativePath, SinceOf(e));
            case Cause.Offline:
                string path = LocalLibrary.PathOf(e.LocalId) ?? e.RelativePath;
                return Strings.LocalFiles.Activity.Offline(FolderOf(path));
            case Cause.Format:
                return Loc.Get(Strings.LocalFiles.List.FormatReason);
            default:
                return null;
        }
    }

    /// <summary>The music folder that holds <paramref name="path"/>, else the path's own folder.</summary>
    static string FolderOf(string path) => LocalLibrary.ContainerOf(path) ?? (Path.GetDirectoryName(path) ?? path);

    /// <summary>"12 Nov": when the file was last seen (local time, the year added across a year).</summary>
    static string SinceOf(LocalEntry e) => DayOf(e.LastSeenTicks);

    /// <summary>A UTC tick count as the short date the list uses ("12 Nov", "12 Nov 2025" in another year).</summary>
    static string DayOf(long utcTicks)
    {
        long unix = Math.Max(0, (utcTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond);
        return Track.Format.ShortDate((int)Math.Min(int.MaxValue, unix), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>The session mark of a row: "New" for a file added since the app opened, "Moved" for one moved since. A tracked read for a local row
    /// (it follows <see cref="LocalLibrary.Restated"/>: a move changes no row field, so the row's presentation alone would never re-ask); a streamed
    /// row reads nothing.</summary>
    public static Mark MarkOf(Track t)
    {
        if (!t.IsValid || !t.IsLocal) return Mark.None;
        _ = LocalLibrary.Restated.Value;
        if (!LocalLibrary.TryLocalIdOf(t.Uri.Text, out long id)) return Mark.None;
        if (LocalLibrary.IsNew(id)) return Mark.New;
        return LocalLibrary.MovedFrom(id) is not null ? Mark.Moved : Mark.None;
    }

    /// <summary>The mark's word ("New", "Moved"), "" for none.</summary>
    public static string MarkLabel(Mark mark) => mark switch
    {
        Mark.New => Loc.Get(Strings.LocalFiles.List.TagNew),
        Mark.Moved => Loc.Get(Strings.LocalFiles.List.TagMoved),
        _ => "",
    };

    /// <summary>The "Moved" tag's tooltip ("Moved from D:\Music\Unsorted\Coastline.flac"), "" for a row that did not move. A tracked read, as <see cref="MarkOf"/>.</summary>
    public static string MovedFromTip(Track t)
    {
        if (!t.IsValid || !t.IsLocal) return "";
        _ = LocalLibrary.Restated.Value;
        return LocalLibrary.TryLocalIdOf(t.Uri.Text, out long id) && LocalLibrary.MovedFrom(id) is { } from ? Strings.LocalFiles.List.MovedFrom(from) : "";
    }

    // ══ 2. THE UNAVAILABLE BAR'S LOCAL REASONS ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>The bar's sentence when every row it counts is an indexed local file (the Local Files list): "3 songs are missing · They were moved,
    /// renamed or deleted outside Wavee." when every one is missing (S6's banner: the bar is its one home), else "2 songs can’t be played here: one is
    /// missing, one is in a format Wavee can’t play yet." Null when the tally holds other reasons too (the shared sentence words them all).</summary>
    internal static (string Summary, string Explained)? BarWords(in Track.UnavailableTally tally)
    {
        int local = tally.Missing + tally.Format + tally.Offline;
        if (local == 0 || local != tally.Total) return null;
        if (tally.Missing == local)
            return (Strings.LocalFiles.Missing.Summary(local), Strings.LocalFiles.Missing.Explained(local));
        return (Strings.LocalFiles.List.BarSummary(local), Strings.LocalFiles.List.BarExplained(local, Reasons(in tally)));
    }

    /// <summary>The local reasons of a tally, each counted ("one is missing, 2 are in a format …"), or one shared reason ("they’re in a format …").</summary>
    internal static string Reasons(in Track.UnavailableTally tally)
    {
        int kinds = (tally.Missing > 0 ? 1 : 0) + (tally.Format > 0 ? 1 : 0) + (tally.Offline > 0 ? 1 : 0);
        bool alone = kinds == 1 && tally.NotAvailable + tally.NotYetOut + tally.LocalFiles == 0;
        if (alone && tally.Format > 0) return Strings.LocalFiles.List.AllFormat(tally.Format);
        if (alone && tally.Offline > 0) return Strings.LocalFiles.List.AllOffline(tally.Offline);
        var parts = new List<string>(3);
        if (tally.Missing > 0) parts.Add(Strings.LocalFiles.List.ReasonMissing(tally.Missing));
        if (tally.Format > 0) parts.Add(Strings.LocalFiles.List.ReasonFormat(tally.Format));
        if (tally.Offline > 0) parts.Add(Strings.LocalFiles.List.ReasonOffline(tally.Offline));
        return string.Join(", ", parts);
    }

    /// <summary>"Review": the bar's second verb while it counts missing files.</summary>
    internal static string ReviewLabel => Loc.Get(Strings.LocalFiles.List.Review);

    // ══ 3. THE EMPTY LIST (S4) ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Does the list show the onboarding body: no music folder (an empty list with folders is a scan that has not listed anything yet).</summary>
    public static bool Onboarding => LocalLibrary.Folders.Count == 0;

    /// <summary>The list's body while it is empty: the onboarding (no music folder), nothing while a first scan has not listed a song yet (the head says
    /// what is happening), else null (the table's own "Nothing here yet"). A tracked read of the scan's progress.</summary>
    public static Element? EmptyBody()
    {
        _ = LocalLibrary.Epoch.Value;   // a folder added, a scan starting: the body follows (read only while the list is empty)
        if (Onboarding) return Embed.Comp(static () => new EmptyHost()) with { Key = "local-empty" };
        if (LocalLibrary.FirstScanRunning) return new BoxEl { Key = "local-empty:scanning" };
        return null;
    }

    /// <summary>The empty state's host: it finds the user's Music folder in an effect (a disk probe never runs in a render) and re-asks when the folders change.</summary>
    sealed class EmptyHost : Component
    {
        readonly Signal<string?> _music = new(null);

        public override Element Render()
        {
            UseEffect(() =>
            {
                _ = LocalLibrary.Epoch.Value;   // a folder added or removed elsewhere
                _music.Value = LocalLibrary.DefaultMusicFolder;
            });
            string? music = _music.Value;
            return ViewSlots.View(ViewKeys.LocalFilesEmpty, new LocalFilesEmptyProps
            {
                Title = Loc.Get(Strings.LocalFiles.List.EmptyTitle),
                Body = Loc.Get(Strings.LocalFiles.List.EmptyBody),
                AddMusicLabel = music is null ? "" : Loc.Get(Strings.LocalFiles.List.AddMusicFolder),
                AddMusic = music is null ? null : s_addMusic,
                MusicFolderCaption = music is null ? "" : Strings.LocalFiles.List.MusicFolderIs(music),
                MusicFolderPath = music ?? "",
                ChooseLabel = Loc.Get(Strings.LocalFiles.List.ChooseFolder),
                Choose = s_choose,
                DropTitle = Loc.Get(Strings.LocalFiles.List.DropTitle),
                DropSub = Loc.Get(Strings.LocalFiles.List.DropSub),
            });
        }
    }

    static readonly Action s_addMusic = static () => SettingsVerbs.AddDefaultMusicFolder();
    static readonly Action s_choose = static () => SettingsVerbs.AddMusicFolder();

    // ══ 4. THE FIRST SCAN (S4) ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Is a first scan running (the page head carries the scan line)? Subscribes the caller to the scan's progress.</summary>
    public static bool FirstScanNow()
    {
        _ = LocalLibrary.Epoch.Value;
        return LocalLibrary.FirstScanRunning;
    }

    /// <summary>The page head's status slot while the first scan runs: the host that re-words the line as the scan moves.</summary>
    public static Element ScanSlot(float width, bool oneLine) => Embed.Comp(new ScanHostProps(width, oneLine), static () => new ScanHost()) with { Key = "local-scan" };

    sealed record ScanHostProps(float Width, bool OneLine);

    /// <summary>The scan line now: "Reading your music · 1,930 of 4,812 · 212 albums so far", "about 40 s left", the bar's fraction.</summary>
    static LocalFilesScanProps ScanProps(float width)
    {
        var p = LocalLibrary.Progress;
        string label = Loc.Get(Strings.LocalFiles.List.ScanLabel);
        if (p.FilesSeen <= 0) return new LocalFilesScanProps { Text = Loc.Get(Strings.LocalFiles.List.ScanListing), Fraction = -1f, Label = label, Width = width };
        string text = Strings.LocalFiles.List.ScanReading(Number(p.FilesDone), Number(p.FilesSeen));
        if (p.Albums > 0) text += Separator + Strings.LocalFiles.List.ScanAlbums(p.Albums, Number(p.Albums));
        string left = p.SecondsLeft <= 0 ? ""
            : p.SecondsLeft < 90 ? Strings.Settings.Music.LeftSeconds(((int)Math.Ceiling(p.SecondsLeft)).ToString(CultureInfo.CurrentCulture))
            : Strings.Settings.Music.LeftMinutes((int)Math.Round(p.SecondsLeft / 60));
        return new LocalFilesScanProps { Text = text, Aside = left, Fraction = (float)Math.Max(0.0, p.Fraction), Label = label, Width = width };
    }

    /// <summary>The scan line's host. When the scan ends the line keeps its last words while the head lets it go (its row leaves on the head's own
    /// motion): it never flips to "Looking for your music…" on its way out.</summary>
    sealed class ScanHost : Component
    {
        LocalFilesScanProps? _last;

        public override Element Render()
        {
            var p = UseProps<ScanHostProps>();
            var now = UseComputed(static () =>
            {
                _ = LocalLibrary.Epoch.Value;
                return LocalLibrary.Progress.Active ? ScanProps(float.NaN) : null;
            }).Value;
            _last = now ?? _last ?? ScanProps(float.NaN);
            return ViewSlots.View(ViewKeys.LocalFilesScan, _last with { Width = p.Width, OneLine = p.OneLine });
        }
    }

    /// <summary>The toast at the end of a first scan: "6,016 songs from 2 folders".</summary>
    public static void SayScanned()
    {
        int songs = LocalLibrary.SongCount, folders = LocalLibrary.Folders.Count;
        if (songs <= 0 || folders <= 0) return;
        Notify.Say(Strings.LocalFiles.List.Summary(Strings.LocalFiles.List.SummarySongs(songs, Number(songs)), Strings.LocalFiles.List.SummaryFolders(folders)),
            dedupeKey: "local.first-scan");
    }

    // ══ 5. THE PILL (S5) ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>How far from its top the list must stand for a change to raise the pill instead of entering in view.</summary>
    const double PillAfter = 240.0;

    /// <summary>The longest stretch the pill's glide covers before it cuts in from that far above the top (a long list never scrolls for seconds).</summary>
    const double PillTravel = 2400.0;

    /// <summary>The pill's words for the last restate's news: "4 new songs · 1 moved"; "" when it brought none the pill announces.</summary>
    static string PillText(LocalLibrary.SessionChanges news)
    {
        string added = news.Added > 0 ? Strings.LocalFiles.List.PillNew(news.Added, Number(news.Added)) : "";
        string moved = news.Moved > 0 ? Strings.LocalFiles.List.PillMoved(news.Moved, Number(news.Moved)) : "";
        return added.Length > 0 && moved.Length > 0 ? added + Separator + moved : added + moved;
    }

    /// <summary>The pill's host over a list: the table hands it the list's scroll handle and the band its list pins at the top.</summary>
    public static Element PillOverlay(ScrollHandle scroll, float top)
        => Embed.Comp(new PillHostProps(scroll, top), static () => new PillHost()) with { Key = "local-pill-host" };

    sealed record PillHostProps(ScrollHandle Scroll, float Top);

    sealed class PillHost : Component
    {
        readonly Signal<string> _armed = new("");
        int _seen = -1;
        ScrollHandle? _scroll;
        Action? _go;

        public override Element Render()
        {
            var p = UseProps<PillHostProps>();
            _scroll = p.Scroll;
            // A restate with news while the list stands away from its top arms the pill; one seen at mount, or at the top, does not (the rows enter there).
            UseEffect(() =>
            {
                int version = LocalLibrary.Restated.Value;
                if (_seen == version) return;
                bool first = _seen < 0;
                _seen = version;
                if (first) return;
                string text = PillText(LocalLibrary.LastChanges);
                if (text.Length == 0) return;
                if ((_scroll?.Offset.Peek() ?? 0.0) <= PillAfter) return;
                _armed.Value = text;
            });
            // At the top by hand: the news is in view, the pill retires.
            bool away = UseComputed(() => (_scroll?.Offset.Value ?? 0.0) > PillAfter / 2).Value;
            UseEffect(() => { if (!away) _armed.Value = ""; }, DepKey.From(away ? 1 : 0));
            string armed = _armed.Value;
            return ViewSlots.View(ViewKeys.LocalFilesPill, new LocalFilesPillProps
            {
                Shown = armed.Length > 0,
                Text = armed,
                Label = Loc.Get(Strings.LocalFiles.List.PillLabel),
                Go = _go ??= Go,
                Top = p.Top,
            });
        }

        void Go()
        {
            _armed.Value = "";
            _scroll?.ScrollTo(0.0, ScrollMove.Glide, PillTravel);
        }
    }

    // ══ 6. THE MISSING SONGS' REVIEW (S6) ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>Opens the review over the page: the house dialog with the theme's body (<see cref="ViewKeys.LocalFilesReview"/>) and Close.</summary>
    public static void OpenReview(IOverlayService? overlay)
    {
        if (overlay is null || Controls.IsNullOverlay(overlay)) return;
        int count = Math.Max(1, LocalLibrary.Index.MissingCount);
        ContentDialog.Show(overlay, d =>
        {
            d.Title = Strings.LocalFiles.Missing.Summary(count);
            d.PrimaryText = "";
            d.CloseText = Loc.Get(Strings.Common.Close);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.Content = Embed.Comp(static () => new ReviewHost());
            OverlayFit.Dialog(d, Controls.DialogWidthMax);
        });
    }

    /// <summary>The review's body: re-read when the list restates, so a file found again leaves it.</summary>
    sealed class ReviewHost : Component
    {
        public override Element Render()
        {
            _ = LocalLibrary.Restated.Value;
            _ = LocalLibrary.Epoch.Value;
            var missing = LocalLibrary.MissingEntries();
            int keepDays = Math.Clamp(Platform.Settings.Get(Platform.Keys.MusicMissingDays), 7, 365);
            string locate = Loc.Get(Strings.LocalFiles.Missing.Locate);
            var rows = new LocalFilesReviewRow[missing.Count];
            for (int i = 0; i < rows.Length; i++)
            {
                LocalEntry e = missing[i];
                string path = LocalLibrary.PathOf(e.LocalId) ?? e.RelativePath;
                string line = Strings.LocalFiles.Missing.Since(SinceOf(e)) + Separator
                    + Strings.LocalFiles.Missing.RemovedOn(DayOf(e.LastSeenTicks + TimeSpan.TicksPerDay * keepDays));
                LocalTagSet tags = e.Tags;
                string artist = Playlist.LocalArtistLine(in tags);
                rows[i] = new LocalFilesReviewRow(e.LocalId.ToString(CultureInfo.InvariantCulture), e.MatchTitle, artist, path, line, locate, () => Locate(path));
            }
            return ViewSlots.View(ViewKeys.LocalFilesReview, new LocalFilesReviewProps
            {
                Body = Loc.Get(Strings.LocalFiles.Missing.Body),
                Rows = rows,
                Note = Loc.Get(Strings.LocalFiles.Missing.LocateNote),
                Footer = Strings.LocalFiles.Missing.Footer(keepDays),
                Empty = Loc.Get(Strings.LocalFiles.Missing.NoneLeft),
            });
        }
    }

    /// <summary>"Locate…": the house folder picker, starting from where the file was. The pick is added as a music folder, or rescanned when a music
    /// folder already holds it (the scan pairs a moved file it finds again by its file id). One file cannot be paired by hand yet.</summary>
    static void Locate(string lastPath)
    {
        Pickers.Pick(PickerRequest.Folder(Loc.Get(Strings.LocalFiles.Missing.LocateTitle)),
            done: picked =>
            {
                if (string.IsNullOrWhiteSpace(picked)) return;
                string folder;
                try { folder = LocalLibrary.Normalize(picked); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
                string? home = LocalLibrary.ContainerOf(folder);
                foreach (string f in LocalLibrary.Folders) if (string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) home = f;
                if (home is not null) LocalLibrary.Rescan(home);
                else SettingsVerbs.AddMusicFolderAt(folder);
            },
            failed: ex => Log.Warn("localfile", "locate picker failed (" + Path.GetFileName(lastPath) + ")", ex));
    }

    static string Number(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    const string Separator = " · ";
}
