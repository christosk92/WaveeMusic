// ── Entities/PlaylistCover.cs ────────────────────────────────────────────────────────────────────────────────────────
// UI: "Change cover" / "Remove cover" for a playlist the user may edit (#155), from the cover's own hover affordance,
// a file dropped on it, and the playlist page's More menu.
//
//     click / menu ─▶ ModalFilePicker (own STA thread; the UI thread never waits) ─▶ Spotify.Post ─┐
//     drop ──────────────────────────────────────────────────────────────────────────────────────────┤
//                                                                                                  ▼
//     Apply: worker — CoverImage.Prepare (WIC: square, upright, ≤ 256 KB JPEG) + the preview file ─▶ UI thread:
//            OPTIMISTIC — the row's image becomes the local preview at once (every surface that paints the row follows)
//            then the write: live = Spotify.PlaylistEdits.SetCover (upload · register · UPDATE_LIST picture);
//                            --fake = a local success, or a failure under --fake-cover-fail (no network either way)
//            ok  → the row's image becomes the server's url (a NEW url: no cached pixel of the old cover can answer it)
//            bad → the image the row had before, and the mapped toast
//
// One cover write at a time (BusySlot drives the editor's saving chip); one picker at a time (ModalFilePicker's gate).

using System.Text;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public static class PlaylistCover
{
    /// <summary>The playlist slot whose cover write is out; <see cref="Table.None"/> when none is.</summary>
    public static readonly Signal<int> BusySlot = new(Table.None);

    /// <summary>How long the --fake backend "uploads" for — long enough to see the saving chip.</summary>
    const int FakeLatencyMs = 900;

    /// <summary>May the user change this playlist's cover right now? An editable Spotify playlist with a live session —
    /// or any editable one under --fake, whose backend is local.</summary>
    public static bool CanEdit(Playlist p)
        => p.IsValid && p.EditableMetadata && p.Uri.Provider == EntityProvider.Spotify
           && (Platform.Args.Fake || Spotify.PlaylistEdits.CanWrite(out _, out _));

    /// <summary>Does it have a cover of its own to remove?</summary>
    public static bool HasOwnCover(Playlist p) => p.IsValid && PlaylistCoverRules.IsOwnCover(ImageOf(p));

    // ── 1. the picker ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Change cover": the image picker, modal to the window, on its own thread; the answer comes back to the
    /// UI thread and goes through <see cref="Apply"/>.</summary>
    public static void Pick(Playlist p)
    {
        if (!Gate(p)) return;
        nint owner = FluentApp.WindowHandle;
        Task<string?>? picking;
        try
        {
            picking = ModalFilePicker.OpenFileAsync(owner, Loc.Get(Strings.Detail.Edit.PickCover),
                (Loc.Get(Strings.Detail.Edit.CoverFilter), PlaylistCoverRules.PickerSpec));
        }
        catch (Exception ex)
        {
            Log.Warn("playlist", "cover picker could not start", ex);
            Notify.Say(Loc.Get(Strings.Detail.Edit.CoverPickerFailed), InfoBarSeverity.Error, dedupeKey: "playlist.cover.picker");
            return;
        }
        if (picking is null)
        {
            Notify.Say(Loc.Get(Strings.Detail.Edit.CoverPickerOpen), InfoBarSeverity.Informational, dedupeKey: "playlist.cover.picker");
            return;
        }
        int slot = p.Slot;
        var scope = Entities.Current;
        long started = Environment.TickCount64;
        int generation = ModalFilePicker.Generation;
        _ = Task.Delay(PlaylistCoverRules.PickerWatchdogMs).ContinueWith(_ => Spotify.Post(() => WatchPicker(owner, started, generation)),
            TaskScheduler.Default);
        picking.ContinueWith(t => Spotify.Post(() =>
        {
            if (t.IsFaulted)
            {
                Log.Warn("playlist", "cover picker failed", t.Exception);
                Notify.Say(Loc.Get(Strings.Detail.Edit.CoverPickerFailed), InfoBarSeverity.Error, dedupeKey: "playlist.cover.picker");
                return;
            }
            if (t.Result is { Length: > 0 } path && ReferenceEquals(scope, Entities.Current)) Apply(new Playlist(slot), path);
        }), TaskScheduler.Default);
    }

    /// <summary>The watchdog (<see cref="PlaylistCoverRules.Watch"/>): a dialog that never became visible must not
    /// leave the window disabled.</summary>
    static void WatchPicker(nint owner, long started, int generation)
    {
        // A later picker (this one closed, another opened) is not this watch's business.
        bool open = ModalFilePicker.IsOpen && ModalFilePicker.Generation == generation;
        var verdict = PlaylistCoverRules.Watch(open, ModalFilePicker.DialogVisible(),
            ModalFilePicker.IsEnabled(owner), Environment.TickCount64 - started);
        if (verdict == PlaylistCoverRules.PickerVerdict.Wait)
        {
            _ = Task.Delay(1000).ContinueWith(_ => Spotify.Post(() => WatchPicker(owner, started, generation)), TaskScheduler.Default);
            return;
        }
        if (verdict != PlaylistCoverRules.PickerVerdict.ReleaseOwner) return;
        Log.Warn("playlist", "cover picker still invisible after " + PlaylistCoverRules.PickerWatchdogMs + " ms; window re-enabled");
        ModalFilePicker.Release(owner);
        Notify.Say(Loc.Get(Strings.Detail.Edit.CoverPickerSlow), InfoBarSeverity.Warning, dedupeKey: "playlist.cover.picker");
    }

    // ── 2. a file → the cover ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A picked or dropped file: checked and encoded off the UI thread, then shown at once and written.</summary>
    public static void Apply(Playlist p, string path)
    {
        if (!Gate(p)) return;
        if (!PlaylistCoverRules.IsSupportedPath(path)) { Say(CoverProblem.Unsupported); return; }
        int slot = p.Slot;
        var scope = Entities.Current;
        BusySlot.Value = slot;
        _ = Task.Run(() =>
        {
            CoverEncoding? cover = null;
            var problem = CoverProblem.Unreadable;
            string? preview = null;
            try
            {
                (cover, problem) = CoverImage.Prepare(path);
                if (cover is { } c) preview = WritePreview(c.Jpeg);
            }
            catch (Exception ex) { Log.Warn("playlist", "cover image could not be prepared", ex); cover = null; }
            Spotify.Post(() => Prepared(scope, slot, cover, problem, preview));
        });
    }

    static void Prepared(Scope scope, int slot, CoverEncoding? cover, CoverProblem problem, string? preview)
    {
        if (!ReferenceEquals(scope, Entities.Current)) { Settle(); return; }
        if (cover is not { } c || preview is null)
        {
            Settle();
            Say(problem == CoverProblem.None ? CoverProblem.Unreadable : problem);
            return;
        }
        var p = new Playlist(slot);
        string? previous = ImageOf(p);
        WriteImage(slot, preview);                                    // optimistic: the new cover, now
        Log.Info("playlist", "cover prepared edge=" + c.Edge + " quality=" + c.Quality + " bytes=" + c.Jpeg.Length);
        Action<string> ok = url =>
        {
            if (ReferenceEquals(scope, Entities.Current)) WriteImage(slot, url);
            Settle();
        };
        Action<PlaylistMutationFailure> failed = kind =>
        {
            if (ReferenceEquals(scope, Entities.Current)) WriteImage(slot, previous);   // the rollback
            Settle();
            PlaylistEditErrors.Raise(kind, PlaylistEditVerb.Cover);
        };
        if (Platform.Args.Fake) Fake(ok: () => ok(preview), failed);
        else Spotify.PlaylistEdits.SetCover(p, c.Jpeg, ok, failed);
    }

    // ── 3. remove ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Remove cover": the mosaic of the playlist's first albums comes back (optimistic, rolled back on a refusal).</summary>
    public static void Remove(Playlist p)
    {
        if (!Gate(p) || !HasOwnCover(p)) return;
        int slot = p.Slot;
        var scope = Entities.Current;
        string? previous = ImageOf(p);
        BusySlot.Value = slot;
        WriteImage(slot, null);
        Action ok = Settle;
        Action<PlaylistMutationFailure> failed = kind =>
        {
            if (ReferenceEquals(scope, Entities.Current)) WriteImage(slot, previous);
            Settle();
            PlaylistEditErrors.Raise(kind, PlaylistEditVerb.Cover);
        };
        if (Platform.Args.Fake) Fake(ok, failed);
        else Spotify.PlaylistEdits.ClearCover(p, ok, failed);
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────────────────────────

    static bool Gate(Playlist p)
    {
        if (BusySlot.Peek() != Table.None) return false;   // one cover write at a time; its chip is already showing
        if (CanEdit(p)) return true;
        if (p.IsValid) PlaylistEditErrors.Raise(PlaylistMutationFailure.NotSupported, PlaylistEditVerb.Cover);
        return false;
    }

    static void Settle() => BusySlot.Value = Table.None;

    /// <summary>The --fake backend: no network, a short "upload", then success — or failure under --fake-cover-fail.</summary>
    static void Fake(Action ok, Action<PlaylistMutationFailure> failed)
        => _ = Task.Delay(FakeLatencyMs).ContinueWith(_ => Spotify.Post(() =>
        {
            if (Platform.Args.FakeCoverFail) failed(PlaylistMutationFailure.Unknown);
            else ok();
        }), TaskScheduler.Default);

    static void Say(CoverProblem problem)
    {
        string key = problem switch
        {
            CoverProblem.Unsupported => Strings.Detail.Edit.CoverUnsupported,
            CoverProblem.TooLarge => Strings.Detail.Edit.CoverTooLarge,
            CoverProblem.TooSmall => Strings.Detail.Edit.CoverTooSmall,
            _ => Strings.Detail.Edit.CoverUnreadable,
        };
        Notify.Say(Loc.Get(key), InfoBarSeverity.Warning, dedupeKey: "playlist.cover.file");
    }

    static string? ImageOf(Playlist p)
    {
        if (p.ImageId.IsEmpty) return null;
        string id = Entities.Strings.Resolve(p.ImageId);
        return id.Length > 0 ? id : null;
    }

    /// <summary>The row's image column, in place (null = no cover of its own): every surface that paints the row — the
    /// page hero, the sidebar, cards, pickers — follows on the publish.</summary>
    static void WriteImage(int slot, string? image)
    {
        var t = Entities.Current.Playlists;
        t.SetText(ref t.Image, slot, image is { Length: > 0 } ? Entities.Intern(Encoding.UTF8.GetBytes(image)) : StringId.Empty);
        t.Bump(slot);
        Entities.Publish();
    }

    /// <summary>The encoded cover as a local file the image pipeline can paint before the server answers: a fresh name
    /// per write (a reused path would be answered from the decoded-image cache). Older previews are swept here.</summary>
    static string WritePreview(byte[] jpeg)
    {
        string dir = Path.Combine(Platform.LocalFolder, "cache", "cover-uploads");
        Directory.CreateDirectory(dir);
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var old in new DirectoryInfo(dir).EnumerateFiles("*.jpg"))
                if (old.LastWriteTimeUtc < cutoff) old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(path, jpeg);
        return path;
    }
}
